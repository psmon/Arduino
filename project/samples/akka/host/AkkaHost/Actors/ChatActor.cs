using System.Diagnostics;
using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using AkkaHost.Chat;
using AkkaHost.Voice;

namespace AkkaHost.Actors;

/// <summary>
/// The device-facing actor: same conversation flow as amoled_chat_host's BLE
/// protocol, but every line is an Akka message instead of a NUS write.
///
/// The device registers a local actor at
/// <c>akka.tcp://askbot-device@ip:port/user/chat</c> and sends from it, so
/// <see cref="ActorBase.Sender"/> here is a ref the host can push to - stages,
/// reply chunks, session changes - exactly as it would to any other actor. That
/// is the whole point of the exercise: the device is a peer, not a polling client.
/// </summary>
public sealed class ChatActor : UntypedActor
{
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private readonly HostConfig _config;
    private readonly Dictionary<string, CliProvider> _providers;

    private readonly VoiceSynth? _voice;
    private readonly Stt? _stt;

    // Per-device state, keyed by the sender's address (one entry per board).
    private sealed class Device
    {
        public int Conversation = 1;
        public int RunningRequest;
        public bool WantsVoice;

        // Output preferences, set per request from the device's Settings screen. Input and
        // output are deliberately separate: the watch may be spoken to in Korean and answer
        // in English, and whisper recognises better when it is told which language to expect.
        public string? OutLanguage;
        public string? Voice;
        public CancellationTokenSource? Cancel;

        // An utterance being captured: microphone frames are decoded straight into this
        // buffer as they arrive, so a 30 s capture costs one 960 KB stream and no
        // per-frame allocations.
        public int CaptureId = -1;
        public MemoryStream? Capture;
        public string? CaptureLanguage;
        public int CaptureFrames;
        public int CaptureGaps;
        public int LastSeq = -1;
    }

    private readonly Dictionary<string, Device> _devices = new(StringComparer.Ordinal);

    // Local (never serialized) completion messages.
    private sealed record Answered(string Key, int RequestId, string Text, IActorRef Target);
    private sealed record Failed(string Key, int RequestId, string Error, IActorRef Target);
    private sealed record SpeechReady(string Key, int RequestId, Speech Speech, long ElapsedMs, IActorRef Target,
        string Voice, string Language);
    private sealed record SpeechFailed(string Key, int RequestId, string Error, IActorRef Target);
    private sealed record SpeechText(int RequestId, string Text);
    private sealed record Transcribed(string Key, int RequestId, string Text, IActorRef Target);
    private sealed record TranscribeFailed(string Key, int RequestId, string Error, IActorRef Target);

    /// <summary>
    /// Optional text the host says to a device as soon as it connects: a push
    /// notification, and the way to exercise the screen and speaker without
    /// touching the watch.
    /// </summary>
    private readonly string? _announce;

    /// <summary>
    /// When set, a device that connects is asked to record for this long. The Chat app takes
    /// that as a "C" line over BLE; AskBot is a real actor, so it just gets a message.
    /// </summary>
    private readonly int _talkMs;

    public ChatActor(HostConfig config, VoiceSynth? voice = null, string? announce = null, Stt? stt = null,
        int talkMs = 0)
    {
        _talkMs = talkMs;
        _config = config;
        _voice = voice;
        _stt = stt;
        _announce = string.IsNullOrWhiteSpace(announce) ? null : announce.Trim();
        _providers = new Dictionary<string, CliProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, provider) in config.Providers)
        {
            _providers[name] = new CliProvider(name, provider,
                (level, message) => _log.Debug("[{0}] {1}", level, message));
        }
    }

    private CliProvider Provider =>
        _providers.TryGetValue(_config.DefaultProvider, out var provider) ? provider : _providers.Values.First();

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case string json:
                HandleJson(json);
                break;

            case Answered answered:
                Complete(answered);
                break;

            case Failed failed:
                Tell(failed.Target, Stage("err", failed.RequestId, text: failed.Error));
                break;

            case SpeechReady ready:
                StreamSpeech(ready);
                break;

            case SpeechFailed failed:
                // Speech is an extra, not the answer: the text already arrived, so this
                // ends the utterance rather than failing the request.
                _log.Warning("speech #{0} failed: {1}", failed.RequestId, failed.Error);
                Tell(failed.Target, Json.Write(writer =>
                {
                    writer.WriteString("t", "answer");
                    writer.WriteString("st", "speak_end");
                    writer.WriteNumber("id", failed.RequestId);
                    writer.WriteString("text", failed.Error);
                }));
                break;

            case byte[] frame:
                MicFrame(frame);
                break;

            case SpeechText speaking:
                _log.Info("speaking #{0}, {1} chars: {2}", speaking.RequestId, speaking.Text.Length,
                    Head(speaking.Text, 200));
                break;

            case Transcribed done:
                Heard(done);
                break;

            case TranscribeFailed failed:
                _log.Warning("stt #{0} failed: {1}", failed.RequestId, failed.Error);
                Tell(failed.Target, Stage("err", failed.RequestId, text: failed.Error));
                break;

            default:
                Unhandled(message);
                break;
        }
    }

    private void HandleJson(string json)
    {
        var sender = Sender;
        var key = sender.Path.Address.ToString();
        var device = GetDevice(key);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            _log.Warning("unparseable message from {0}: {1}", sender.Path, Head(json, 120));
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            var type = root.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var id = root.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var idValue) ? idValue : 0;

            switch (type)
            {
                case "hello":
                    var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                        ? n.GetString() : "?";
                    _log.Info("device {0} said hello as {1}", sender.Path.Address, name);
                    Tell(sender, Json.Write(writer =>
                    {
                        writer.WriteString("t", "hostinfo");
                        writer.WriteString("host", Environment.MachineName);
                        writer.WriteString("provider", Provider.Name);
                        // The device only offers the voice toggle when the host can
                        // actually speak, same rule as the BLE app.
                        writer.WriteBoolean("tts", _voice?.Available == true);
                        if (_voice?.Available == true)
                        {
                            writer.WriteString("voice", _voice.VoiceId);
                            writer.WriteString("outLang", _voice.LanguageId);
                            // The Settings screen lists what the host actually has rather than a
                            // hardcoded set: SuperTonic's voices are whatever is in voice_styles/.
                            writer.WriteStartArray("voices");
                            foreach (var id in _voice.AvailableVoices) writer.WriteStringValue(id);
                            writer.WriteEndArray();
                        }
                        // The Chat app shows the microphone as unavailable when the host
                        // cannot transcribe, rather than recording into a void.
                        writer.WriteBoolean("sttReady", _stt?.Available == true);
                        if (_stt?.Available == true) writer.WriteString("stt", _stt.ProviderName);
                        writer.WriteNumber("chat", device.Conversation);
                        writer.WriteNumber("v", 1);
                    }));
                    if (_talkMs > 0 && name == "askbot")
                    {
                        Tell(sender, Json.Write(writer =>
                        {
                            writer.WriteString("t", "cmd");
                            writer.WriteString("cmd", "talk");
                            writer.WriteNumber("ms", _talkMs);
                        }));
                    }
                    if (_announce != null)
                    {
                        // Unsolicited answer: the device renders and speaks it the same
                        // way it would an answer to its own question.
                        device.WantsVoice = _voice?.Available == true;
                        Complete(new Answered(key, 0, _announce, sender));
                    }
                    break;

                case "text":
                    var prompt = root.TryGetProperty("text", out var textElement) &&
                                 textElement.ValueKind == JsonValueKind.String
                        ? textElement.GetString() ?? ""
                        : "";
                    device.WantsVoice = root.TryGetProperty("tts", out var tts) &&
                                        tts.ValueKind == JsonValueKind.True;
                    ReadOutputPrefs(device, root);
                    StartRequest(key, device, id, prompt, sender);
                    break;

                case "voice":
                    BeginCapture(device, id, root, sender);
                    break;

                case "end":
                    EndCapture(key, device, id, sender);
                    break;

                case "cancel":
                    // Newest-question-wins is the rule the BLE host settled on: a
                    // cancel abandons the answer rather than refusing the next one.
                    device.Cancel?.Cancel();
                    device.RunningRequest = 0;
                    DropCapture(device);
                    Tell(sender, Stage("idle", id));
                    break;

                case "newsession":
                    device.Cancel?.Cancel();
                    device.Conversation++;
                    _log.Info("device {0} starts conversation {1}", sender.Path.Address, device.Conversation);
                    Tell(sender, Json.Write(writer =>
                    {
                        writer.WriteString("t", "answer");
                        writer.WriteString("st", "session");
                        writer.WriteNumber("id", id);
                        writer.WriteNumber("n", device.Conversation);
                    }));
                    break;

                case "ping":
                    Tell(sender, Stage("pong", id));
                    break;

                default:
                    _log.Warning("unknown message type '{0}' from {1}", type, sender.Path);
                    break;
            }
        }
    }

    private void StartRequest(string key, Device device, int id, string prompt, IActorRef target)
    {
        if (prompt.Trim().Length == 0)
        {
            Tell(target, Stage("err", id, text: "empty prompt"));
            return;
        }

        device.Cancel?.Cancel();
        var cancel = new CancellationTokenSource();
        device.Cancel = cancel;
        device.RunningRequest = id;

        Tell(target, Stage("think", id));

        var provider = Provider;
        var session = $"askbot-{Sanitize(key)}-{device.Conversation}";
        var styled = _config.ReplyStyle.Length > 0 ? _config.ReplyStyle + "\n\n" + prompt : prompt;
        var self = Self;

        _ = Task.Run(async () =>
        {
            try
            {
                var answer = await provider.AskAsync(styled, session, cancel.Token);
                if (!cancel.IsCancellationRequested) self.Tell(new Answered(key, id, answer, target));
            }
            catch (OperationCanceledException)
            {
                // superseded by a newer question; the device already moved on
            }
            catch (Exception ex)
            {
                if (!cancel.IsCancellationRequested) self.Tell(new Failed(key, id, ex.Message, target));
            }
        }, cancel.Token);
    }

    private void Complete(Answered answered)
    {
        var device = GetDevice(answered.Key);
        if (device.RunningRequest != answered.RequestId) return;  // a newer question won
        device.RunningRequest = 0;

        var text = answered.Text.Trim();
        if (text.Length > _config.MaxReplyChars) text = text[.._config.MaxReplyChars];

        var chunks = Json.ChunkUtf8(text, _config.ChunkBytes);
        if (chunks.Count == 0) chunks.Add("");

        for (var i = 0; i < chunks.Count; i++)
        {
            var index = i;
            var last = i == chunks.Count - 1;
            Tell(answered.Target, Json.Write(writer =>
            {
                writer.WriteString("t", "answer");
                writer.WriteString("st", "reply");
                writer.WriteNumber("id", answered.RequestId);
                writer.WriteNumber("seq", index);
                writer.WriteNumber("n", chunks.Count);
                writer.WriteString("text", chunks[index]);
                if (last) writer.WriteBoolean("done", true);
            }));
        }
        _log.Info("answered #{0} in {1} chunk(s), {2} chars: {3}", answered.RequestId, chunks.Count,
            text.Length, Head(text, 200));

        if (device.WantsVoice && _voice?.Available == true && text.Length > 0)
            StartSpeech(answered.Key, device, answered.RequestId, text, answered.Target);
    }

    /// <summary>
    /// Synthesis is CPU-heavy (flow matching over N steps), so it runs off the actor
    /// thread and comes back as a message. The device has the text already; speech is
    /// an addition, never a precondition for the answer.
    /// </summary>
    private void StartSpeech(string key, Device device, int requestId, string text, IActorRef target)
    {
        var voice = _voice!;
        var cancel = device.Cancel;
        var self = Self;
        var voiceId = device.Voice;
        var language = device.OutLanguage;

        _ = Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                // Logged separately from the displayed answer so a mismatch between what the
                // watch shows and what it says is visible rather than guessed at.
                self.Tell(new SpeechText(requestId, text));
                var speech = voice.Synthesize(text, requestId, voiceId, language,
                    cancel?.Token ?? CancellationToken.None);
                if (cancel?.IsCancellationRequested != true)
                    self.Tell(new SpeechReady(key, requestId, speech, sw.ElapsedMilliseconds, target,
                        voiceId ?? voice.VoiceId, language ?? voice.LanguageId));
            }
            catch (OperationCanceledException)
            {
                // the device cancelled or asked something newer
            }
            catch (Exception ex)
            {
                if (cancel?.IsCancellationRequested != true)
                    self.Tell(new SpeechFailed(key, requestId, ex.Message, target));
            }
        });
    }

    /// <summary>
    /// Announce the utterance, push the ADPCM frames as byte[] messages, then close it.
    /// The device buffers the frames and plays them when speak_end arrives - the same
    /// contract the BLE app uses, so its playback code ports across unchanged.
    /// </summary>
    private void StreamSpeech(SpeechReady ready)
    {
        var speech = ready.Speech;
        Tell(ready.Target, Json.Write(writer =>
        {
            writer.WriteString("t", "answer");
            writer.WriteString("st", "speak");
            writer.WriteNumber("id", ready.RequestId);
            writer.WriteString("fmt", "adpcm");
            writer.WriteNumber("rate", speech.Rate);
            writer.WriteNumber("ch", 1);
            writer.WriteNumber("frames", speech.Frames.Count);
            writer.WriteNumber("ms", speech.DurationMs);
        }));

        foreach (var frame in speech.Frames) ready.Target.Tell(frame, Self);

        Tell(ready.Target, Json.Write(writer =>
        {
            writer.WriteString("t", "answer");
            writer.WriteString("st", "speak_end");
            writer.WriteNumber("id", ready.RequestId);
        }));

        _log.Info("spoke #{0} as {1}/{2}: {3} ms of audio in {4} frames, synthesized in {5} ms",
            ready.RequestId, ready.Voice, ready.Language, speech.DurationMs, speech.Frames.Count,
            ready.ElapsedMs);
    }

    // ---------------------------------------------------------------- voice input

    /// <summary>
    /// "voice" opens a capture. Frames that follow carry the same id; "end" closes it. The
    /// device keeps recording while the user holds the button, so this can run for tens of
    /// seconds and must not buffer per frame.
    /// </summary>
    private void BeginCapture(Device device, int id, JsonElement root, IActorRef target)
    {
        if (_stt?.Available != true)
        {
            Tell(target, Stage("err", id, text: "no speech recognition on this host"));
            return;
        }

        device.Cancel?.Cancel();          // newest utterance wins, same rule as text
        device.CaptureId = id;
        device.Capture = new MemoryStream(16000 * 2 * 8);   // 8 s before it grows
        device.CaptureFrames = 0;
        device.CaptureGaps = 0;
        device.LastSeq = -1;
        device.CaptureLanguage = root.TryGetProperty("lang", out var lang) &&
                                 lang.ValueKind == JsonValueKind.String
            ? lang.GetString()
            : null;
        device.WantsVoice = root.TryGetProperty("tts", out var tts) && tts.ValueKind == JsonValueKind.True;
        ReadOutputPrefs(device, root);

        _log.Info("capture #{0} started (in {1}, out {2}/{3})", id, device.CaptureLanguage ?? "auto",
            device.OutLanguage ?? _voice?.LanguageId ?? "-", device.Voice ?? _voice?.VoiceId ?? "-");
        Tell(target, Stage("rec", id));
    }

    /// <summary>
    /// "outLang" and "voice" travel with every request rather than in a separate settings
    /// message: the device owns these settings, and carrying them per request means a changed
    /// setting takes effect on the next question with no state to keep in sync.
    /// </summary>
    private static void ReadOutputPrefs(Device device, JsonElement root)
    {
        if (root.TryGetProperty("outLang", out var outLang) && outLang.ValueKind == JsonValueKind.String)
            device.OutLanguage = outLang.GetString();
        if (root.TryGetProperty("voice", out var voice) && voice.ValueKind == JsonValueKind.String)
            device.Voice = voice.GetString();
    }

    private void MicFrame(byte[] frame)
    {
        var key = Sender.Path.Address.ToString();
        var device = GetDevice(key);

        if (!DeviceAudio.TryParseFrame(frame, DeviceAudio.MicMagic, out var id, out var seq, out var block))
        {
            _log.Debug("{0} byte frame from {1} is not microphone audio", frame.Length, Sender.Path);
            return;
        }
        if (device.Capture is null || device.CaptureId != id) return;   // a capture that was abandoned

        // One lost frame is one lost ADPCM block, not a desync - the block header carries
        // its own predictor - so a gap is worth counting and no more.
        if (device.LastSeq >= 0 && seq != ((device.LastSeq + 1) & 0xFFFF)) device.CaptureGaps++;
        device.LastSeq = seq;

        ImaAdpcm.DecodeBlock(block.Span, device.Capture);
        device.CaptureFrames++;
    }

    private void EndCapture(string key, Device device, int id, IActorRef target)
    {
        var capture = device.Capture;
        if (capture is null || device.CaptureId != id)
        {
            Tell(target, Stage("idle", id));
            return;
        }

        var pcm = capture.ToArray();
        var language = device.CaptureLanguage;
        DropCapture(device);

        var seconds = pcm.Length / 32000.0;
        var (peakDb, rmsDb) = Stt.Levels(pcm);
        _log.Info("capture #{0} ended: {1:F1} s, {2} frames, {3} gaps, peak {4:F1} dBFS, rms {5:F1} dBFS",
            id, seconds, device.CaptureFrames, device.CaptureGaps, peakDb, rmsDb);

        if (pcm.Length < 3200)
        {
            Tell(target, Stage("err", id, text: "nothing recorded"));
            return;
        }

        Tell(target, Stage("stt", id));   // no text yet: "transcribing"

        var cancel = new CancellationTokenSource();
        device.Cancel = cancel;
        device.RunningRequest = id;
        var stt = _stt!;
        var self = Self;

        _ = Task.Run(async () =>
        {
            try
            {
                var text = await stt.TranscribeAsync(pcm, language, cancel.Token);
                if (!cancel.IsCancellationRequested) self.Tell(new Transcribed(key, id, text, target));
            }
            catch (OperationCanceledException)
            {
                // superseded
            }
            catch (Exception ex)
            {
                if (!cancel.IsCancellationRequested) self.Tell(new TranscribeFailed(key, id, ex.Message, target));
            }
        }, cancel.Token);
    }

    /// <summary>
    /// The transcript goes back to the device first - seeing what the host heard is half the
    /// value when it mis-hears - and then straight into the same request path a typed
    /// question takes.
    /// </summary>
    private void Heard(Transcribed done)
    {
        var device = GetDevice(done.Key);
        if (device.RunningRequest != done.RequestId) return;   // a newer utterance won

        Tell(done.Target, Json.Write(writer =>
        {
            writer.WriteString("t", "answer");
            writer.WriteString("st", "stt");
            writer.WriteNumber("id", done.RequestId);
            writer.WriteString("text", done.Text);
        }));

        if (done.Text.Length == 0)
        {
            device.RunningRequest = 0;
            Tell(done.Target, Stage("err", done.RequestId, text: "no speech detected"));
            return;
        }

        StartRequest(done.Key, device, done.RequestId, done.Text, done.Target);
    }

    private static void DropCapture(Device device)
    {
        device.Capture?.Dispose();
        device.Capture = null;
        device.CaptureId = -1;
    }

    private static string Stage(string stage, int id, string? text = null) => Json.Write(writer =>
    {
        writer.WriteString("t", "answer");
        writer.WriteString("st", stage);
        writer.WriteNumber("id", id);
        if (text != null) writer.WriteString("text", text);
    });

    private void Tell(IActorRef target, string json) => target.Tell(json, Self);

    private Device GetDevice(string key)
    {
        if (!_devices.TryGetValue(key, out var device))
        {
            device = new Device();
            _devices[key] = device;
        }
        return device;
    }

    private static string Sanitize(string address)
    {
        var chars = address.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        return new string(chars).Trim('-');
    }

    private static string Head(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
