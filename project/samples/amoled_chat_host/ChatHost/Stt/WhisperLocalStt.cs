using Microsoft.Extensions.Options;
using Whisper.net;
using Whisper.net.Ggml;

namespace ChatHost.Stt;

/// <summary>
/// Offline STT with whisper.cpp through Whisper.net (CPU runtime).
/// Ported from AgentZeroLite WhisperLocalStt, minus the Vulkan/GPU loader.
/// Model files are shared with AgentZeroLite: %USERPROFILE%\.ollama\models\agentzero\whisper\ggml-{size}.bin,
/// downloaded on first use when missing (Stt:AutoDownload).
/// </summary>
public sealed class WhisperLocalStt : ISpeechToText
{
    public string ProviderName => $"whisper-{_model}";

    private static readonly Dictionary<string, (GgmlType type, string file, string sizeLabel, long minBytes)> Models = new()
    {
        ["tiny"]   = (GgmlType.Tiny,   "ggml-tiny.bin",   "~75 MB",   70_000_000),
        ["base"]   = (GgmlType.Base,   "ggml-base.bin",   "~142 MB", 130_000_000),
        ["small"]  = (GgmlType.Small,  "ggml-small.bin",  "~466 MB", 400_000_000),
        ["medium"] = (GgmlType.Medium, "ggml-medium.bin", "~1.5 GB", 1_400_000_000),
    };

    private readonly SttOptions _opt;
    private readonly ILogger<WhisperLocalStt> _log;
    private readonly string _model;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private WhisperFactory? _factory;
    private Task<bool>? _readyTask;

    public bool IsReady => _factory != null;
    public string Status { get; private set; } = "not loaded";

    public WhisperLocalStt(IOptions<SttOptions> opt, ILogger<WhisperLocalStt> log)
    {
        _opt = opt.Value;
        _log = log;
        _model = Models.ContainsKey(_opt.Model) ? _opt.Model : "small";
    }

    public string ModelDir => string.IsNullOrWhiteSpace(_opt.ModelDir)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models", "agentzero", "whisper")
        : Environment.ExpandEnvironmentVariables(_opt.ModelDir);

    public string ModelPath => Path.Combine(ModelDir, Models[_model].file);

    public bool IsModelDownloaded()
    {
        if (!File.Exists(ModelPath)) return false;
        return new FileInfo(ModelPath).Length >= Models[_model].minBytes;
    }

    public Task<bool> EnsureReadyAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // one shared load/download task so concurrent callers do not double-download
        lock (_lock)
        {
            if (_factory != null) return Task.FromResult(true);
            _readyTask ??= LoadAsync(progress, ct);
            return _readyTask;
        }
    }

    private async Task<bool> LoadAsync(IProgress<string>? progress, CancellationToken ct)
    {
        try
        {
            if (!IsModelDownloaded())
            {
                if (!_opt.AutoDownload)
                {
                    Status = $"model missing: {ModelPath} (Stt:AutoDownload=false)";
                    _log.LogError("{Status}", Status);
                    return false;
                }
                if (File.Exists(ModelPath)) File.Delete(ModelPath);   // truncated download
                Directory.CreateDirectory(ModelDir);
                var (type, _, size, _) = Models[_model];
                Status = $"downloading ggml-{_model}.bin ({size})…";
                _log.LogInformation("Whisper model missing — {Status} -> {Dir}", Status, ModelDir);
                progress?.Report(Status);
                var tmp = ModelPath + ".part";
                await using (var src = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(type, cancellationToken: ct))
                await using (var dst = File.Create(tmp))
                {
                    var buf = new byte[1 << 20];
                    long total = 0; int n; var last = DateTime.UtcNow;
                    while ((n = await src.ReadAsync(buf, ct)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n), ct);
                        total += n;
                        if ((DateTime.UtcNow - last).TotalSeconds >= 5)
                        {
                            last = DateTime.UtcNow;
                            Status = $"downloading ggml-{_model}.bin: {total / (1024 * 1024)} MB";
                            _log.LogInformation("{Status}", Status);
                            progress?.Report(Status);
                        }
                    }
                }
                File.Move(tmp, ModelPath, overwrite: true);
                _log.LogInformation("Whisper model saved: {Path} ({MB} MB)", ModelPath, new FileInfo(ModelPath).Length / (1024 * 1024));
            }

            Status = $"loading {Path.GetFileName(ModelPath)}…";
            progress?.Report(Status);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var factory = await Task.Run(() => WhisperFactory.FromPath(ModelPath), ct);
            _factory = factory;
            Status = $"ready ({Path.GetFileName(ModelPath)}, cpu, {sw.ElapsedMilliseconds} ms load)";
            _log.LogInformation("Whisper {Status}", Status);
            progress?.Report(Status);
            return true;
        }
        catch (Exception ex)
        {
            Status = "load failed: " + ex.Message;
            _log.LogError(ex, "Whisper load failed");
            lock (_lock) { _readyTask = null; }      // allow a retry on the next call
            return false;
        }
    }

    public async Task<string> TranscribeAsync(byte[] pcm16kMono, string language = "auto", CancellationToken ct = default)
    {
        if (pcm16kMono.Length < 3200) return "";     // < 0.1 s
        if (!await EnsureReadyAsync(null, ct) || _factory is null)
            throw new InvalidOperationException("Whisper not ready: " + Status);

        var samples = new float[pcm16kMono.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (short)(pcm16kMono[i * 2] | (pcm16kMono[i * 2 + 1] << 8)) / 32768f;

        var lang = string.IsNullOrWhiteSpace(language) ? _opt.Language : language;
        await _lock.WaitAsync(ct);
        try
        {
            var builder = _factory.CreateBuilder().WithLanguage(lang);
            await using var processor = builder.Build();
            var sb = new System.Text.StringBuilder();
            await foreach (var seg in processor.ProcessAsync(samples, ct))
                if (!string.IsNullOrWhiteSpace(seg.Text)) sb.Append(seg.Text);
            return sb.ToString().Trim();
        }
        finally { _lock.Release(); }
    }
}

/// <summary>Optional warm-up so the first voice request does not pay the model load.</summary>
public sealed class SttPreloadWorker(ISpeechToText stt, IOptions<SttOptions> opt, ILogger<SttPreloadWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!opt.Value.Preload) return;
        try { await stt.EnsureReadyAsync(new Progress<string>(s => log.LogInformation("STT: {S}", s)), ct); }
        catch (Exception ex) { log.LogWarning("STT preload failed: {Msg}", ex.Message); }
    }
}
