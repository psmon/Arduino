namespace AkkaHost.Voice;

public sealed class VoiceOptions
{
    /// <summary>Empty = the directory AgentZeroLite installs SuperTonic into.</summary>
    public string ModelDir { get; init; } = "";
    /// <summary>M1..M5 / F1..F5.</summary>
    public string Voice { get; init; } = "F1";
    /// <summary>Denoising steps, 5..12. 8 is the reference default.</summary>
    public int Steps { get; init; } = 8;
    public string Language { get; init; } = "ko";
    public float Speed { get; init; } = 1.05f;
    public bool Enabled { get; init; } = true;
}

/// <summary>One synthesized answer, already framed for the device.</summary>
public sealed record Speech(List<byte[]> Frames, int DurationMs, int Rate, byte[] Pcm16);

/// <summary>
/// Lazy facade over <see cref="SuperTonicSynthesizer"/>: loads the four ONNX graphs on
/// first use (a few seconds, 383 MB on disk), caches voice styles, and hands back
/// device-ready ADPCM frames.
///
/// It never downloads anything. If the model is not installed, <see cref="Available"/>
/// is false and the chat path simply answers in text - the same degradation the BLE
/// host applies when no voice is installed.
/// </summary>
public sealed class VoiceSynth : IDisposable
{
    private readonly VoiceOptions _options;
    private readonly string _modelDir;
    private readonly Action<string, string> _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, SuperTonicStyle> _styles = new(StringComparer.OrdinalIgnoreCase);
    private SuperTonicSynthesizer? _synth;

    public VoiceSynth(VoiceOptions options, Action<string, string> log)
    {
        _options = options;
        _log = log;
        _modelDir = string.IsNullOrWhiteSpace(options.ModelDir)
            ? SuperTonicModel.DefaultDirectory
            : options.ModelDir;
    }

    public string ModelDirectory => _modelDir;

    /// <summary>The configured default; a request may ask for another.</summary>
    public string VoiceId => _options.Voice;

    /// <summary>The configured default output language.</summary>
    public string LanguageId => _options.Language;

    /// <summary>
    /// The voices actually on disk, read from voice_styles/. SuperTonic's styles are speaker
    /// embeddings extracted from reference audio with no language binding (the model is
    /// "opensource-multilingual" and the language is a tag around the text), so every voice
    /// can speak every supported language. The device lists these in its Settings screen.
    /// </summary>
    public IReadOnlyList<string> AvailableVoices
    {
        get
        {
            var dir = Path.Combine(_modelDir, "voice_styles");
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            return Directory.EnumerateFiles(dir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>Languages the model accepts; "na" lets it auto-detect from the script.</summary>
    public static IReadOnlyList<string> AvailableLanguages => SuperTonicLanguages.Available;

    public bool Available => _options.Enabled && SuperTonicModel.IsPresent(_modelDir);

    /// <summary>Why voice is off, for the log line at startup.</summary>
    public string Status => !_options.Enabled
        ? "disabled in appsettings.json"
        : Available
            ? $"SuperTonic {_options.Voice}, {_options.Steps} steps, {_options.Language} ({_modelDir})"
            : $"model not installed at {_modelDir}";

    /// <summary>
    /// Synthesize and frame. Blocking and CPU-heavy (flow matching over N steps), so
    /// callers run it off the actor thread.
    /// </summary>
    public Speech Synthesize(string text, int requestId, string? voice = null, string? language = null,
        CancellationToken ct = default)
    {
        if (!Available) throw new InvalidOperationException($"voice unavailable: {Status}");

        var synth = EnsureLoaded();
        // Per-request overrides come from the device's Settings screen; the configured values
        // are the fallback.
        var voiceId = string.IsNullOrWhiteSpace(voice) ? _options.Voice : voice!;
        var lang = string.IsNullOrWhiteSpace(language) ? _options.Language : language!;
        var style = Style(voiceId);
        ct.ThrowIfCancellationRequested();

        var samples = synth.Synthesize(text, lang, style,
            Math.Clamp(_options.Steps, 5, 12), Math.Clamp(_options.Speed, 0.7f, 2.0f));

        var pcm = DeviceAudio.ToPcm16k(samples, synth.SampleRate);
        var blocks = ImaAdpcm.EncodeBlocks(pcm, DeviceAudio.SamplesPerBlock);

        var frames = new List<byte[]>(blocks.Count);
        for (var i = 0; i < blocks.Count; i++) frames.Add(DeviceAudio.SpeechFrame(requestId, i, blocks[i]));

        return new Speech(frames, DeviceAudio.DurationMs(pcm), DeviceAudio.TargetRate, pcm);
    }

    private SuperTonicSynthesizer EnsureLoaded()
    {
        if (_synth is not null) return _synth;
        lock (_lock)
        {
            if (_synth is not null) return _synth;
            _log("info", $"loading SuperTonic from {_modelDir}");
            var started = DateTime.UtcNow;
            _synth = SuperTonicSynthesizer.Load(_modelDir);
            _log("info", $"SuperTonic ready in {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms, " +
                         $"{_synth.SampleRate} Hz");
            return _synth;
        }
    }

    private SuperTonicStyle Style(string voice)
    {
        lock (_lock)
        {
            if (_styles.TryGetValue(voice, out var cached)) return cached;

            var path = SuperTonicModel.VoiceStylePath(_modelDir, voice);
            if (!File.Exists(path))
                throw new InvalidOperationException($"voice style '{voice}' not found at {path}");

            var style = SuperTonicStyle.Load(path);
            _styles[voice] = style;
            return style;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _synth?.Dispose();
            _synth = null;
            _styles.Clear();
        }
    }
}
