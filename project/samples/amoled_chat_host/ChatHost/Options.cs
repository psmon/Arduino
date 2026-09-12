namespace ChatHost;

/// <summary>Bound from appsettings.json "Host".</summary>
public sealed class HttpHostOptions
{
    /// <summary>Local HTTP port. 8765 keeps the old ble_bridge.py contract (POST /status, POST /event).</summary>
    public int Port { get; set; } = 8765;
}

/// <summary>Bound from "Ble".</summary>
public sealed class BleOptions
{
    /// <summary>Advertised name of the device to auto-connect when nothing is paired yet.</summary>
    public string DeviceName { get; set; } = "claude-hud";
    public bool AutoConnect { get; set; } = true;
    public int ScanSeconds { get; set; } = 5;
    public int ReconnectSeconds { get; set; } = 5;
    /// <summary>Upper bound for one text line written to NUS RX (firmware buffers 600 B per write).</summary>
    public int MaxLineBytes { get; set; } = 480;
}

/// <summary>Bound from "Stt".</summary>
public sealed class SttOptions
{
    /// <summary>tiny | small | medium (ggml-{model}.bin)</summary>
    public string Model { get; set; } = "small";
    /// <summary>Empty = %USERPROFILE%\.ollama\models\agentzero\whisper (shared with AgentZeroLite).</summary>
    public string? ModelDir { get; set; }
    /// <summary>"auto" or an ISO code such as "ko" / "en".</summary>
    public string Language { get; set; } = "auto";
    public bool AutoDownload { get; set; } = true;
    /// <summary>Load the model at startup instead of on first request.</summary>
    public bool Preload { get; set; } = true;
    /// <summary>Discard captures longer than this (seconds of 16 kHz audio).</summary>
    public int MaxSeconds { get; set; } = 60;
}

/// <summary>Bound from "Chat".</summary>
public sealed class ChatOptions
{
    /// <summary>Name of the provider used when the request does not name one. Can be overridden at runtime (persisted).</summary>
    public string Default { get; set; } = "netclaw";
    /// <summary>Prepended to every prompt sent to the CLI (the device screen is tiny). Empty = none.</summary>
    public string ReplyStyle { get; set; } =
        "You are answering on a small round watch-like display. Reply in the user's language. Plain text only: no markdown, no bullet lists, no code blocks, and never include URLs, links or citations. At most 3 short sentences.";
    /// <summary>Replies longer than this are truncated before going to the device.</summary>
    public int MaxReplyChars { get; set; } = 1200;
    public Dictionary<string, CliProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One registered "chat CLI". Anything that can answer a prompt from the command line fits:
/// netclaw (default), claude, codex, a shell script... Placeholders: {prompt}, {session}.
/// </summary>
public sealed class CliProviderOptions
{
    public string? Description { get; set; }
    /// <summary>Executable (resolved through PATH) — e.g. "netclaw". For npm .cmd shims use "cmd" + Args ["/c","claude",...].</summary>
    public string Command { get; set; } = "";
    /// <summary>Fixed arguments, may contain {prompt} / {session}.</summary>
    public List<string> Args { get; set; } = new();
    /// <summary>Inserted (in place of the "{session-args}" marker in Args, or appended before the prompt) only when a session id is known.</summary>
    public List<string> SessionArgs { get; set; } = new();
    /// <summary>"arg" = prompt appended as the last argument (unless {prompt} appears in Args); "stdin" = written to standard input.</summary>
    public string PromptVia { get; set; } = "arg";
    /// <summary>"text" = whole stdout is the answer; "json" = parse stdout as JSON and read ResponseField.</summary>
    public string Output { get; set; } = "text";
    /// <summary>Dotted path inside the JSON output, e.g. "response" or "result.text".</summary>
    public string ResponseField { get; set; } = "response";
    public int TimeoutSec { get; set; } = 120;
    public string? WorkingDir { get; set; }
    public Dictionary<string, string> Env { get; set; } = new();
}
