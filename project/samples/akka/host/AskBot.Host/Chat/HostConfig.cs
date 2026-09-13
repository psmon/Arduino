using System.Text.Json;
using AskBot.Host.Voice;

namespace AskBot.Host.Chat;

public sealed class ProviderConfig
{
    public string? Description { get; init; }
    public string Command { get; init; } = "";
    public List<string> Args { get; init; } = [];
    public List<string> SessionArgs { get; init; } = [];
    /// <summary>"arg" = prompt as the last argument; "stdin" = written to standard input.</summary>
    public string PromptVia { get; init; } = "arg";
    /// <summary>"text" = stdout is the answer; "json" = read <see cref="ResponseField"/> from it.</summary>
    public string Output { get; init; } = "text";
    public string ResponseField { get; init; } = "response";
    public int TimeoutSec { get; init; } = 120;
    public string? WorkingDir { get; init; }
}

/// <summary>
/// appsettings.json, read with <see cref="JsonDocument"/> on purpose: the
/// Microsoft.Extensions.Configuration + Options stack binds by reflection, which
/// is exactly what Native AOT trims away.
/// </summary>
public sealed class HostConfig
{
    public string DefaultProvider { get; private set; } = "echo";
    public string ReplyStyle { get; private set; } = "";
    public int MaxReplyChars { get; private set; } = 1200;
    /// <summary>Bytes per reply chunk. One Akka frame holds far more, but the device
    /// appends chunks into a fixed buffer and redraws per chunk.</summary>
    public int ChunkBytes { get; private set; } = 400;
    public Dictionary<string, ProviderConfig> Providers { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Bound from the "Voice" section; defaults point at the installed SuperTonic bundle.</summary>
    public VoiceOptions Voice { get; private set; } = new();

    public bool TrySetDefaultProvider(string name)
    {
        if (!Providers.ContainsKey(name)) return false;
        DefaultProvider = name;
        return true;
    }

    public static HostConfig Load(string path)
    {
        var config = new HostConfig();
        if (!File.Exists(path))
        {
            config.Providers["echo"] = EchoFallback();
            return config;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        if (!document.RootElement.TryGetProperty("Chat", out var chat))
        {
            config.Providers["echo"] = EchoFallback();
            return config;
        }

        if (chat.TryGetProperty("Default", out var def) && def.ValueKind == JsonValueKind.String)
            config.DefaultProvider = def.GetString()!;
        if (chat.TryGetProperty("ReplyStyle", out var style) && style.ValueKind == JsonValueKind.String)
            config.ReplyStyle = style.GetString()!;
        if (chat.TryGetProperty("MaxReplyChars", out var max) && max.TryGetInt32(out var maxValue))
            config.MaxReplyChars = maxValue;
        if (chat.TryGetProperty("ChunkBytes", out var chunk) && chunk.TryGetInt32(out var chunkValue))
            config.ChunkBytes = chunkValue;

        if (document.RootElement.TryGetProperty("Voice", out var voice) && voice.ValueKind == JsonValueKind.Object)
            config.Voice = ReadVoice(voice);

        if (chat.TryGetProperty("Providers", out var providers) && providers.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in providers.EnumerateObject())
                config.Providers[entry.Name] = ReadProvider(entry.Value);
        }
        if (config.Providers.Count == 0) config.Providers["echo"] = EchoFallback();
        return config;
    }

    private static VoiceOptions ReadVoice(JsonElement element) => new()
    {
        Enabled = !element.TryGetProperty("Enabled", out var enabled) || enabled.ValueKind != JsonValueKind.False,
        ModelDir = Str(element, "ModelDir") ?? "",
        Voice = Str(element, "Voice") ?? "F1",
        Language = Str(element, "Language") ?? "ko",
        Steps = element.TryGetProperty("Steps", out var steps) && steps.TryGetInt32(out var stepValue) ? stepValue : 8,
        Speed = element.TryGetProperty("Speed", out var speed) && speed.TryGetSingle(out var speedValue) ? speedValue : 1.05f,
    };

    private static ProviderConfig ReadProvider(JsonElement element) => new()
    {
        Description = Str(element, "Description"),
        Command = Str(element, "Command") ?? "",
        Args = StrList(element, "Args"),
        SessionArgs = StrList(element, "SessionArgs"),
        PromptVia = Str(element, "PromptVia") ?? "arg",
        Output = Str(element, "Output") ?? "text",
        ResponseField = Str(element, "ResponseField") ?? "response",
        TimeoutSec = element.TryGetProperty("TimeoutSec", out var t) && t.TryGetInt32(out var timeout) ? timeout : 120,
        WorkingDir = Str(element, "WorkingDir"),
    };

    private static string? Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static List<string> StrList(JsonElement element, string name)
    {
        var list = new List<string>();
        if (element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString()!);
        }
        return list;
    }

    /// <summary>Loopback provider so the host is testable with no LLM installed.</summary>
    private static ProviderConfig EchoFallback() => new()
    {
        Description = "offline loopback (no LLM)",
        Command = "powershell",
        Args =
        [
            "-NoProfile", "-NonInteractive", "-Command",
            "[Console]::InputEncoding=[Text.Encoding]::UTF8; [Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
            "'echo: ' + [Console]::In.ReadToEnd()"
        ],
        PromptVia = "stdin",
        Output = "text",
        TimeoutSec = 20,
    };
}
