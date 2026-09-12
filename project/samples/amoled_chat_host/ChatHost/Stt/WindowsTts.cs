using System.Globalization;
using System.Speech.Synthesis;

namespace ChatHost.Stt;

/// <summary>
/// Text to speech through Windows SAPI. Produces raw 16 kHz / 16-bit / mono PCM directly - the same
/// shape the device plays and the same shape <see cref="ImaAdpcm"/> encodes - so nothing has to parse
/// or resample a WAV container on the way to the speaker.
///
/// SAPI is synchronous and not thread-safe, so every call is serialised.
/// </summary>
public sealed class WindowsTts : IDisposable
{
    private readonly ILogger<WindowsTts> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly SpeechSynthesizer _synth = new();
    private readonly List<(string name, string culture)> _voices = new();

    public WindowsTts(ILogger<WindowsTts> log)
    {
        _log = log;
        try
        {
            foreach (var v in _synth.GetInstalledVoices().Where(v => v.Enabled))
                _voices.Add((v.VoiceInfo.Name, v.VoiceInfo.Culture.Name));
            _log.LogInformation("TTS voices: {List}", string.Join(", ", _voices.Select(v => $"{v.name} [{v.culture}]")));
        }
        catch (Exception ex) { _log.LogWarning("TTS enumeration failed: {Msg}", ex.Message); }
    }

    public bool Available => _voices.Count > 0;
    public IReadOnlyList<string> Voices => _voices.Select(v => $"{v.name} [{v.culture}]").ToList();

    /// <summary>Two-letter language guess from the text itself; SAPI picks a voice per language, not per prompt.</summary>
    public static string GuessLanguage(string text)
    {
        foreach (var ch in text)
        {
            if (ch >= 0xAC00 && ch <= 0xD7A3) return "ko";     // Hangul syllables
            if (ch >= 0x3040 && ch <= 0x30FF) return "ja";     // kana
            if (ch >= 0x4E00 && ch <= 0x9FFF) return "zh";     // CJK ideographs
            if (ch >= 0x0400 && ch <= 0x04FF) return "ru";
        }
        return "en";
    }

    /// <summary>Synthesise to 16 kHz mono PCM16. Returns an empty array when SAPI has no usable voice.</summary>
    public async Task<byte[]> SpeakAsync(string text, string? language = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text) || !Available) return [];
        var lang = string.IsNullOrWhiteSpace(language) || language == "auto" ? GuessLanguage(text) : language;

        await _lock.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                var pick = _voices.FirstOrDefault(v => v.culture.StartsWith(lang, StringComparison.OrdinalIgnoreCase));
                if (pick.name is not null)
                {
                    try { _synth.SelectVoice(pick.name); }
                    catch (Exception ex) { _log.LogWarning("SelectVoice({Name}) failed: {Msg}", pick.name, ex.Message); }
                }
                else
                {
                    _log.LogWarning("no SAPI voice for '{Lang}' - using the default one, pronunciation will be off", lang);
                }

                using var ms = new MemoryStream();
                var fmt = new System.Speech.AudioFormat.SpeechAudioFormatInfo(
                    AudioConvert.TargetRate, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen,
                    System.Speech.AudioFormat.AudioChannel.Mono);
                _synth.SetOutputToAudioStream(ms, fmt);      // raw PCM, no RIFF header
                try { _synth.Speak(text); }
                finally { _synth.SetOutputToNull(); }
                return ms.ToArray();
            }, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning("TTS failed: {Msg}", ex.Message);
            return [];
        }
        finally { _lock.Release(); }
    }

    public void Dispose() => _synth.Dispose();
}
