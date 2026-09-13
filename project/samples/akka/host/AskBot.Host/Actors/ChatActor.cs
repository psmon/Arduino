using System.Diagnostics;
using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using AskBot.Host.Chat;
using AskBot.Host.Voice;

namespace AskBot.Host.Actors;

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

    // Per-device state, keyed by the sender's address (one entry per board).
    private sealed class Device
    {
        public int Conversation = 1;
        public int RunningRequest;
        public bool WantsVoice;
        public CancellationTokenSource? Cancel;
    }

    private readonly Dictionary<string, Device> _devices = new(StringComparer.Ordinal);

    // Local (never serialized) completion messages.
    private sealed record Answered(string Key, int RequestId, string Text, IActorRef Target);
    private sealed record Failed(string Key, int RequestId, string Error, IActorRef Target);
    private sealed record SpeechReady(string Key, int RequestId, Speech Speech, long ElapsedMs, IActorRef Target);
    private sealed record SpeechFailed(string Key, int RequestId, string Error, IActorRef Target);

    /// <summary>
    /// Optional text the host says to a device as soon as it connects: a push
    /// notification, and the way to exercise the screen and speaker without
    /// touching the watch.
    /// </summary>
    private readonly string? _announce;

    public ChatActor(HostConfig config, VoiceSynth? voice = null, string? announce = null)
    {
        _config = config;
        _voice = voice;
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

            // Audio frames will arrive here once voice is wired up; acknowledge the
            // shape now so a stray frame is a log line rather than an unhandled
            // message that Akka reports as a dead letter.
            case byte[] frame:
                _log.Debug("audio frame of {0} bytes from {1} (voice path not implemented yet)",
                    frame.Length, Sender.Path);
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
                        if (_voice?.Available == true) writer.WriteString("voice", _voice.VoiceId);
                        writer.WriteNumber("chat", device.Conversation);
                        writer.WriteNumber("v", 1);
                    }));
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
                    StartRequest(key, device, id, prompt, sender);
                    break;

                case "cancel":
                    // Newest-question-wins is the rule the BLE host settled on: a
                    // cancel abandons the answer rather than refusing the next one.
                    device.Cancel?.Cancel();
                    device.RunningRequest = 0;
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
        _log.Info("answered #{0} in {1} chunk(s), {2} chars", answered.RequestId, chunks.Count, text.Length);

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

        _ = Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var speech = voice.Synthesize(text, requestId, cancel?.Token ?? CancellationToken.None);
                if (cancel?.IsCancellationRequested != true)
                    self.Tell(new SpeechReady(key, requestId, speech, sw.ElapsedMilliseconds, target));
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

        _log.Info("spoke #{0}: {1} ms of audio in {2} frames, synthesized in {3} ms",
            ready.RequestId, speech.DurationMs, speech.Frames.Count, ready.ElapsedMs);
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
