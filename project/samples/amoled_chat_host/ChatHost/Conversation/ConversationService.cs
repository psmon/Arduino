using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatHost.Ble;
using ChatHost.Chat;
using ChatHost.Stt;
using Microsoft.Extensions.Options;

namespace ChatHost.Conversation;

/// <summary>
/// The pipeline: device request (text or voice) → [STT] → chat CLI → answer back to the device.
/// Also used by the HTTP test endpoints, and forwards HUD status/event lines (S/E) unchanged.
/// One request at a time; a second request while busy is answered with st="busy".
/// </summary>
public sealed class ConversationService
{
    private sealed class Capture
    {
        public int Id;
        public string Fmt = "pcm16";
        public int Rate = 16_000;
        public int Channels = 1;
        public string? Lang;
        public bool Tts;
        public readonly MemoryStream Data = new();
        public int Frames, Gaps, LastSeq = -1;
        public readonly Stopwatch Timer = Stopwatch.StartNew();
    }

    private readonly BleLink _link;
    private readonly ISpeechToText _stt;
    private readonly WindowsTts _tts;
    private readonly ChatProviderRegistry _chat;
    private readonly SttOptions _sttOpt;
    private readonly BleOptions _bleOpt;
    private readonly ILogger<ConversationService> _log;
    private readonly object _capLock = new();
    private Capture? _cap;

    // One request at a time, but a new one preempts the old instead of being refused: on a handheld
    // device the newest question is always the one the person cares about, and "host busy" left them
    // guessing whether the thing was broken or just serial.
    private readonly object _runLock = new();
    private CancellationTokenSource? _runCts;
    private int _runId;
    private Task _runTask = Task.CompletedTask;

    // Write Korean (and every other non-ASCII script) as real UTF-8 instead of \uXXXX. The default
    // encoder doubles the size of a Korean payload, which pushes one reply line past a single BLE
    // write and doubles the airtime. Quotes, backslashes and control characters are still escaped,
    // and the consumer is cJSON on the device - never a browser - so relaxed escaping is safe here.
    private static readonly JsonSerializerOptions LineJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public int Handled, Errors, Preempted;
    public string LastTranscript { get; private set; } = "";
    public string LastReply { get; private set; } = "";
    public string LastDeviceInfo { get; private set; } = "";
    public bool IsBusy { get { lock (_runLock) return !_runTask.IsCompleted; } }
    public int BusyId { get { lock (_runLock) return _runTask.IsCompleted ? 0 : _runId; } }
    public bool TtsAvailable => _tts.Available;
    public string CaptureState
    {
        get { lock (_capLock) return _cap is null ? "idle" : $"id={_cap.Id} {_cap.Fmt}@{_cap.Rate} {_cap.Data.Length} B in {_cap.Frames} frames (gaps {_cap.Gaps})"; }
    }

    public ConversationService(BleLink link, ISpeechToText stt, WindowsTts tts, ChatProviderRegistry chat,
        IOptions<SttOptions> sttOpt, IOptions<BleOptions> bleOpt, ILogger<ConversationService> log)
    {
        _link = link; _stt = stt; _tts = tts; _chat = chat; _sttOpt = sttOpt.Value; _bleOpt = bleOpt.Value; _log = log;
        _link.Connected += () => _ = SendHelloAsync();
        _link.LineReceived += OnLine;
        _link.FrameReceived += OnFrame;
    }

    // The CLI session id doubles as the conversation's identity. If a provider wedges one (a killed
    // client can leave netclaw's daemon session half-open), the next attempt moves to a fresh id rather
    // than failing forever; the cost is losing that conversation's history, which beats losing the device.
    private int _sessionEpoch;
    public string DeviceSession =>
        "amoled-" + (_link.IsConnected ? _link.AddressHex : "device") + (_sessionEpoch == 0 ? "" : $"-{_sessionEpoch}");

    public void RotateSession()
    {
        _sessionEpoch++;
        _log.LogWarning("chat session rotated -> {Session} (previous one stopped answering)", DeviceSession);
    }

    // ------------------------------------------------------------ inbound

    private void OnLine(string line)
    {
        if (line.Length < 2 || line[0] != 'R' || line[1] != ' ')
        {
            _log.LogInformation("device: {Line}", line.Length > 200 ? line[..200] + "…" : line);
            return;
        }
        JsonNode? js;
        try { js = JsonNode.Parse(line[2..]); }
        catch (JsonException ex) { _log.LogWarning("bad R json: {Msg}", ex.Message); return; }
        if (js is null) return;

        var t = js["t"]?.GetValue<string>() ?? "";
        int id = js["id"]?.GetValue<int>() ?? 0;
        switch (t)
        {
            case "hello":
                // Do NOT answer with another H: the device replies to H with a hello, so answering
                // here would ping-pong forever. The H sent on connect is the only greeting we send.
                LastDeviceInfo = js.ToJsonString();
                _log.LogInformation("device hello: {Info}", LastDeviceInfo);
                break;

            case "ping":
                _ = SendAsync(id, "pong");
                break;

            case "text":
            {
                var text = js["text"]?.GetValue<string>() ?? "";
                var lang = js["lang"]?.GetValue<string>();
                bool wantTts = js["tts"]?.GetValue<bool>() ?? false;
                LastTranscript = text;          // so /api/status shows the prompt for typed requests too
                _log.LogInformation("device text request #{Id}{Tts}: {Text}", id, wantTts ? " (+speech)" : "", text);
                StartRequest(id, null, text, lang, wantTts);
                break;
            }

            case "voice":
            {
                var cap = new Capture
                {
                    Id = id,
                    Fmt = js["fmt"]?.GetValue<string>() ?? "pcm16",
                    Rate = js["rate"]?.GetValue<int>() ?? 16_000,
                    Channels = js["ch"]?.GetValue<int>() ?? 1,
                    Lang = js["lang"]?.GetValue<string>(),
                    Tts = js["tts"]?.GetValue<bool>() ?? false,
                };
                lock (_capLock) _cap = cap;
                _log.LogInformation("voice capture #{Id} begin: {Fmt} {Rate} Hz ch={Ch}{Tts}", id, cap.Fmt, cap.Rate,
                    cap.Channels, cap.Tts ? " (+speech)" : "");
                _ = SendAsync(id, "rec");
                break;
            }

            case "end":
            {
                Capture? cap;
                lock (_capLock) { cap = _cap; _cap = null; }
                if (cap is null || cap.Id != id) { _log.LogWarning("end #{Id} without matching capture", id); return; }
                _log.LogInformation("voice capture #{Id} end: {Bytes} B, {Frames} frames, {Gaps} gaps, {Ms} ms",
                    id, cap.Data.Length, cap.Frames, cap.Gaps, cap.Timer.ElapsedMilliseconds);
                byte[] pcm;
                try { pcm = DecodeCapture(cap); }
                catch (Exception ex)
                {
                    _log.LogWarning("decode failed: {Msg}", ex.Message);
                    _ = SendAsync(id, "err", "audio decode failed: " + ex.Message);
                    return;
                }
                StartRequest(id, pcm, null, cap.Lang, cap.Tts);
                break;
            }

            case "cancel":
                lock (_capLock) { if (_cap?.Id == id) _cap = null; }
                CancelCurrent("device asked to stop");
                _ = SendAsync(id, "idle");
                break;

            default:
                _log.LogWarning("unknown device request t='{T}'", t);
                break;
        }
    }

    private void OnFrame(byte[] pkt)
    {
        var (id, seq, payload) = NusFrames.ParseAudio(pkt);
        lock (_capLock)
        {
            var cap = _cap;
            if (cap is null || cap.Id != id) return;
            if (cap.LastSeq >= 0 && seq != ((cap.LastSeq + 1) & 0xFFFF)) cap.Gaps++;
            cap.LastSeq = seq;
            cap.Frames++;
            if (cap.Fmt == "adpcm") ImaAdpcm.DecodeBlock(payload.Span, cap.Data);
            else cap.Data.Write(payload.Span);
            if (cap.Data.Length > (long)_sttOpt.MaxSeconds * cap.Rate * 2 * cap.Channels)
            {
                _log.LogWarning("capture #{Id} exceeds Stt:MaxSeconds={S}s — dropping", id, _sttOpt.MaxSeconds);
                _cap = null;
                _ = SendAsync(id, "err", $"too long (> {_sttOpt.MaxSeconds}s)");
            }
        }
    }

    private static byte[] DecodeCapture(Capture cap)
    {
        var raw = cap.Data.ToArray();   // pcm16 (already decoded if adpcm) at cap.Rate / cap.Channels
        return AudioConvert.Pcm16To16kMono(raw, cap.Rate, cap.Channels);
    }

    // ----------------------------------------------------------- pipeline

    /// <summary>Cancel whatever is running. Safe to call when nothing is.</summary>
    public bool CancelCurrent(string why)
    {
        CancellationTokenSource? cts;
        int id;
        lock (_runLock)
        {
            if (_runTask.IsCompleted) return false;
            cts = _runCts; id = _runId;
        }
        _log.LogInformation("cancelling request #{Id}: {Why}", id, why);
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }

    private void StartRequest(int id, byte[]? pcm, string? text, string? lang, bool tts)
    {
        Task previous;
        CancellationTokenSource cts = new();
        lock (_runLock)
        {
            previous = _runTask;
            if (!previous.IsCompleted)
            {
                Preempted++;
                _log.LogInformation("request #{Old} preempted by #{New}", _runId, id);
                try { _runCts?.Cancel(); } catch (ObjectDisposedException) { }
            }
            _runCts = cts;
            _runId = id;
            _runTask = Task.Run(async () =>
            {
                // Let the old one unwind first so its late 'reply' cannot land after the new one's.
                try { await previous.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
                try { await RunAsync(id, pcm, text, lang, tts, cts.Token); }
                finally { cts.Dispose(); }
            });
        }
    }

    private async Task RunAsync(int id, byte[]? pcm, string? text, string? lang, bool tts, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string prompt = text ?? "";
            if (pcm is not null)
            {
                var (peak, rms) = AudioConvert.Levels(pcm);
                _log.LogInformation("STT #{Id}: {Sec:F1}s audio peak={Peak:F0} dBFS rms={Rms:F0} dBFS", id, pcm.Length / 32000.0, peak, rms);
                await SendAsync(id, "stt");                             // "transcribing…" hint for the UI
                prompt = await _stt.TranscribeAsync(pcm, lang ?? _sttOpt.Language, ct);
                LastTranscript = prompt;
                _log.LogInformation("STT #{Id} ({Ms} ms): {Text}", id, sw.ElapsedMilliseconds, prompt);
                if (string.IsNullOrWhiteSpace(prompt))
                {
                    await SendAsync(id, "err", "(no speech recognized)");
                    return;
                }
                await SendAsync(id, "stt", prompt);
            }

            await SendAsync(id, "think");
            var reply = await AskAsync(prompt, null, DeviceSession, ct);
            ct.ThrowIfCancellationRequested();
            Handled++;
            await SendReplyAsync(id, reply.Response);
            _log.LogInformation("reply #{Id} via {Prov} ({Ms} ms total): {Text}", id, reply.Provider, sw.ElapsedMilliseconds,
                reply.Response.Length > 160 ? reply.Response[..160] + "…" : reply.Response);

            if (tts) await SpeakToDeviceAsync(id, reply.Response, lang, ct);
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("request #{Id} cancelled after {Ms} ms", id, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Errors++;
            _log.LogError("request #{Id} failed: {Msg}", id, ex.Message);
            // An unusable answer usually means the provider's session is wedged, not that the question
            // was bad, so move to a fresh session and let the person simply ask again.
            if (ex is InvalidOperationException or TimeoutException) RotateSession();
            await SendAsync(id, "err", ex.Message);
        }
    }

    /// <summary>
    /// Synthesise the answer and stream it to the device speaker as 0xA6 frames.
    /// ADPCM keeps it to 8 kB/s, so a 5 s answer is ~40 kB and lands in a second or two; the device
    /// buffers the whole utterance in PSRAM and plays it when "speak_end" arrives.
    /// </summary>
    private async Task SpeakToDeviceAsync(int id, string text, string? lang, CancellationToken ct)
    {
        if (!_tts.Available)
        {
            _log.LogWarning("speech requested but no SAPI voice is installed");
            await SendAsync(id, "speak_end", "no voice installed");
            return;
        }
        var sw = Stopwatch.StartNew();
        var pcm = await _tts.SpeakAsync(text, lang, ct);
        if (pcm.Length == 0) { await SendAsync(id, "speak_end", "synthesis produced no audio"); return; }
        ct.ThrowIfCancellationRequested();

        // One ADPCM block per BLE write: 4 frame header + 4 block header + samples/2 bytes.
        int payloadMax = Math.Max(24, _link.MaxPdu - 3);
        int samplesPerBlock = Math.Min(960, (payloadMax - 8) * 2) & ~1;
        var blocks = ImaAdpcm.EncodeBlocks(pcm, samplesPerBlock);
        double seconds = pcm.Length / (double)(AudioConvert.TargetRate * 2);

        await SendAsync2(id, "speak", new JsonObject
        {
            ["fmt"] = "adpcm", ["rate"] = AudioConvert.TargetRate, ["ch"] = 1,
            ["frames"] = blocks.Count, ["ms"] = (int)(seconds * 1000),
        });

        int sent = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!await _link.SendRawAsync(NusFrames.SpeechFrame(id, i, blocks[i]), ct)) break;
            sent++;
            if ((i & 7) == 7) await Task.Delay(8, ct);   // let the controller drain
        }
        await SendAsync(id, "speak_end");
        _log.LogInformation("speech #{Id}: {Sec:F1}s audio, {Sent}/{Total} frames, {Ms} ms",
            id, seconds, sent, blocks.Count, sw.ElapsedMilliseconds);
    }

    /// <summary>Speak arbitrary text on the device (web UI test button).</summary>
    public Task SpeakAsync(string text, string? lang, CancellationToken ct = default)
        => SpeakToDeviceAsync(0, text, lang, ct);

    /// <summary>Run the chat CLI (HTTP test endpoints call this directly).</summary>
    public async Task<ChatResult> AskAsync(string prompt, string? provider, string? session, CancellationToken ct = default)
    {
        var p = _chat.Get(provider) ?? throw new InvalidOperationException($"chat provider '{provider ?? _chat.DefaultName}' not registered");
        var full = string.IsNullOrWhiteSpace(_chat.ReplyStyle) ? prompt : $"{_chat.ReplyStyle}\n\n{prompt}";
        var r = await p.AskAsync(full, session, ct);
        var answer = r.Response.Trim();
        if (answer.Length > _chat.MaxReplyChars) answer = answer[.._chat.MaxReplyChars] + "…";
        LastReply = answer;
        return r with { Response = answer };
    }

    public Task<string> TranscribeAsync(byte[] pcm16k, string? lang, CancellationToken ct = default)
        => _stt.TranscribeAsync(pcm16k, lang ?? _sttOpt.Language, ct);

    // ------------------------------------------------------------ outbound

    private Task SendHelloAsync()
    {
        var js = new JsonObject
        {
            ["host"] = Environment.MachineName,
            ["provider"] = _chat.DefaultName,
            ["stt"] = _stt.ProviderName,
            ["sttReady"] = _stt.IsReady,
            ["tts"] = _tts.Available,          // device hides the speech setting when the host has no voice
            ["v"] = 2,
        };
        return _link.SendLineAsync("H " + js.ToJsonString(LineJson));
    }

    /// <summary>A {"id":..,"st":..[,"text":..]}</summary>
    public Task<bool> SendAsync(int id, string stage, string? text = null)
    {
        var js = new JsonObject { ["id"] = id, ["st"] = stage };
        if (text != null) js["text"] = text;
        return _link.SendLineAsync("A " + js.ToJsonString(LineJson));
    }

    /// <summary>A line with extra fields merged in (used by the speech header).</summary>
    private Task<bool> SendAsync2(int id, string stage, JsonObject extra)
    {
        var js = new JsonObject { ["id"] = id, ["st"] = stage };
        foreach (var kv in extra.ToList()) { extra.Remove(kv.Key); js[kv.Key] = kv.Value; }
        return _link.SendLineAsync("A " + js.ToJsonString(LineJson));
    }

    /// <summary>Send the answer as numbered chunks that each fit one line (≤ Ble:MaxLineBytes).</summary>
    public async Task<bool> SendReplyAsync(int id, string text)
    {
        // JSON envelope overhead ≈ 60 B; JSON escaping of quotes/newlines can grow the payload, keep margin.
        var parts = NusFrames.SplitUtf8(text, Math.Max(60, _bleOpt.MaxLineBytes - 120));
        for (int i = 0; i < parts.Count; i++)
        {
            var js = new JsonObject { ["id"] = id, ["st"] = "reply", ["seq"] = i, ["n"] = parts.Count, ["text"] = parts[i] };
            if (i == parts.Count - 1) js["done"] = true;
            if (!await _link.SendLineAsync("A " + js.ToJsonString(LineJson))) return false;
        }
        return true;
    }

    /// <summary>HUD passthrough (compatible with the old ble_bridge.py): "S {json}" / "E {json}".</summary>
    public Task<bool> SendHudAsync(char tag, string json) => _link.SendLineAsync($"{tag} {json.Trim()}");
}
