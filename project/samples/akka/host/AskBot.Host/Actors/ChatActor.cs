using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using AskBot.Host.Chat;

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

    // Per-device state, keyed by the sender's address (one entry per board).
    private sealed class Device
    {
        public int Conversation = 1;
        public int RunningRequest;
        public CancellationTokenSource? Cancel;
    }

    private readonly Dictionary<string, Device> _devices = new(StringComparer.Ordinal);

    // Local (never serialized) completion messages.
    private sealed record Answered(string Key, int RequestId, string Text, IActorRef Target);
    private sealed record Failed(string Key, int RequestId, string Error, IActorRef Target);

    public ChatActor(HostConfig config)
    {
        _config = config;
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
                        writer.WriteBoolean("tts", false);  // voice answers not implemented yet
                        writer.WriteNumber("chat", device.Conversation);
                        writer.WriteNumber("v", 1);
                    }));
                    break;

                case "text":
                    var prompt = root.TryGetProperty("text", out var textElement) &&
                                 textElement.ValueKind == JsonValueKind.String
                        ? textElement.GetString() ?? ""
                        : "";
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
