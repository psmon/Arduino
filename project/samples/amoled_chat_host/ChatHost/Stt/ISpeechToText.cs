namespace ChatHost.Stt;

/// <summary>
/// Same shape as AgentZeroLite's Agent.Common.Voice.ISpeechToText so providers can be ported 1:1.
/// Input is always raw 16-bit little-endian, 16 kHz, mono PCM (no WAV header).
/// </summary>
public interface ISpeechToText
{
    string ProviderName { get; }
    bool IsReady { get; }
    string Status { get; }
    Task<bool> EnsureReadyAsync(IProgress<string>? progress = null, CancellationToken ct = default);
    Task<string> TranscribeAsync(byte[] pcm16kMono, string language = "auto", CancellationToken ct = default);
}
