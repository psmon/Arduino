using System.Diagnostics;
using System.Text;
using Whisper.net;

namespace AkkaHost.Voice;

public sealed class SttOptions
{
    /// <summary>tiny | base | small | medium — the ggml-{model}.bin that must already be on disk.</summary>
    public string Model { get; init; } = "small";
    /// <summary>Empty = the directory AgentZeroLite installs Whisper models into.</summary>
    public string ModelDir { get; init; } = "";
    /// <summary>"auto", or an ISO code such as "ko" / "en".</summary>
    public string Language { get; init; } = "auto";
    /// <summary>Load the model at startup instead of on the first utterance.</summary>
    public bool Preload { get; init; } = true;
    /// <summary>Ignore captures longer than this (seconds of 16 kHz audio).</summary>
    public int MaxSeconds { get; init; } = 60;
    /// <summary>whisper.cpp worker threads; 0 = one per core, minus one for everything else.</summary>
    public int Threads { get; init; } = 0;
    /// <summary>
    /// Captures whose peak is below this (dBFS) are treated as silence and never reach
    /// whisper. Left to itself on a quiet capture it invents text - "[구독 / 좋아요]" and
    /// similar YouTube boilerplate from its training data - and the watch would then ask the
    /// LLM about it.
    /// </summary>
    public double SilencePeakDb { get; init; } = -45;
    /// <summary>
    /// And the same for RMS, which is the one that actually separates the two cases. Measured
    /// on this board at the default 30 dB gain: an empty room is rms -47 to -61 dBFS, while
    /// someone speaking is -32 to -31. -45 sits in that gap. (-50 was too generous: a -47 dBFS
    /// capture slipped through and whisper answered it with "[끝]".)
    /// </summary>
    public double SilenceRmsDb { get; init; } = -45;
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Offline speech-to-text through whisper.cpp (Whisper.net, CPU).
///
/// Like the TTS side, this uses the model AgentZeroLite already installed and never
/// downloads anything: a 466 MB fetch is not something a chat request should trigger.
/// If the file is missing, <see cref="Available"/> is false and the host tells the device
/// it has no STT rather than failing the utterance halfway through.
/// </summary>
public sealed class Stt : IDisposable
{
    private static readonly Dictionary<string, (string file, long minBytes)> Models = new()
    {
        ["tiny"] = ("ggml-tiny.bin", 70_000_000),
        ["base"] = ("ggml-base.bin", 130_000_000),
        ["small"] = ("ggml-small.bin", 400_000_000),
        ["medium"] = ("ggml-medium.bin", 1_400_000_000),
    };

    private readonly SttOptions _options;
    private readonly Action<string, string> _log;
    private readonly string _model;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;

    public Stt(SttOptions options, Action<string, string> log)
    {
        _options = options;
        _log = log;
        _model = Models.ContainsKey(options.Model) ? options.Model : "small";
    }

    public string ModelDirectory => string.IsNullOrWhiteSpace(_options.ModelDir)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ollama", "models", "agentzero", "whisper")
        : Environment.ExpandEnvironmentVariables(_options.ModelDir);

    public string ModelPath => Path.Combine(ModelDirectory, Models[_model].file);

    public bool Available
    {
        get
        {
            if (!_options.Enabled || !File.Exists(ModelPath)) return false;
            return new FileInfo(ModelPath).Length >= Models[_model].minBytes;
        }
    }

    public string Status => !_options.Enabled
        ? "disabled in appsettings.json"
        : Available
            ? $"whisper-{_model} ({ModelPath})"
            : $"model not installed at {ModelPath}";

    public string ProviderName => $"whisper-{_model}";

    /// <summary>Optional warm-up so the first utterance does not pay the model load.</summary>
    public void Preload()
    {
        if (!_options.Preload || !Available) return;
        _ = Task.Run(() =>
        {
            try
            {
                EnsureLoaded();
            }
            catch (Exception ex)
            {
                _log("warn", $"preload failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 16 kHz mono PCM16 in, text out. Whisper.net only exposes the segment stream as an
    /// IAsyncEnumerable, so this is async; callers still run it off the actor thread because
    /// the work itself is CPU-bound.
    /// </summary>
    public async Task<string> TranscribeAsync(byte[] pcm16kMono, string? language = null,
        CancellationToken ct = default)
    {
        if (!Available) throw new InvalidOperationException($"stt unavailable: {Status}");
        if (pcm16kMono.Length < 3200) return "";   // under 0.1 s: nothing was said

        var maxBytes = _options.MaxSeconds * 16000 * 2;
        if (pcm16kMono.Length > maxBytes)
        {
            _log("warn", $"capture of {pcm16kMono.Length / 32000.0:F1} s truncated to {_options.MaxSeconds} s");
            pcm16kMono = pcm16kMono[..maxBytes];
        }

        var (peakDb, rmsDb) = Levels(pcm16kMono);
        if (peakDb < _options.SilencePeakDb || rmsDb < _options.SilenceRmsDb)
        {
            _log("info", $"capture is silence (peak {peakDb:F1} dBFS, rms {rmsDb:F1} dBFS), not transcribing");
            return "";
        }

        var factory = EnsureLoaded();
        var samples = new float[pcm16kMono.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (short)(pcm16kMono[i * 2] | (pcm16kMono[i * 2 + 1] << 8)) / 32768f;

        var lang = string.IsNullOrWhiteSpace(language) ? _options.Language : language;
        await _gate.WaitAsync(ct);
        try
        {
            var sw = Stopwatch.StartNew();
            // Threads matter more than anything else here: the default left a 4 s capture
            // taking 25 s on this machine, which is not a usable watch interaction.
            var threads = _options.Threads > 0
                ? _options.Threads
                : Math.Max(1, Environment.ProcessorCount - 1);
            await using var processor = factory.CreateBuilder()
                .WithLanguage(lang)
                .WithThreads(threads)
                .Build();
            var text = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(samples, ct))
                if (!string.IsNullOrWhiteSpace(segment.Text)) text.Append(segment.Text);

            var result = text.ToString().Trim();
            _log("info", $"transcribed {samples.Length / 16000.0:F1} s in {sw.ElapsedMilliseconds} ms " +
                         $"({threads} threads, lang {lang}): " +
                         $"{(result.Length == 0 ? "(silence)" : result)}");
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Peak and RMS in dBFS: enough to tell "nobody spoke" from "something was said".</summary>
    public static (double peakDb, double rmsDb) Levels(byte[] pcm16)
    {
        if (pcm16.Length < 2) return (-120, -120);

        var peak = 0;
        double sum = 0;
        var count = pcm16.Length / 2;
        for (var i = 0; i < count; i++)
        {
            var sample = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
            var abs = Math.Abs((int)sample);
            if (abs > peak) peak = abs;
            sum += (double)sample * sample;
        }
        var rms = Math.Sqrt(sum / count);
        return (Db(peak), Db(rms));

        static double Db(double value) => value < 1 ? -120 : 20 * Math.Log10(value / 32768.0);
    }

    private WhisperFactory EnsureLoaded()
    {
        if (_factory is not null) return _factory;
        lock (Models)
        {
            if (_factory is not null) return _factory;
            var sw = Stopwatch.StartNew();
            _log("info", $"loading {Path.GetFileName(ModelPath)}");
            _factory = WhisperFactory.FromPath(ModelPath);
            _log("info", $"whisper ready in {sw.ElapsedMilliseconds} ms");
            return _factory;
        }
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _factory = null;
    }
}
