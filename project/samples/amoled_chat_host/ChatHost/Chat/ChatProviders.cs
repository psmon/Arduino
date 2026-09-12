using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ChatHost.Ble;
using Microsoft.Extensions.Options;

namespace ChatHost.Chat;

public sealed record ChatResult(string Provider, string Response, long ElapsedMs, string? Raw = null);

public interface IChatProvider
{
    string Name { get; }
    string? Description { get; }
    Task<ChatResult> AskAsync(string prompt, string? session, CancellationToken ct = default);
}

/// <summary>
/// Runs any "chat CLI" as a child process: netclaw chat -p, claude -p, codex exec, a script...
/// The prompt goes in as an argument or via stdin; the answer is stdout (text) or a field of a JSON stdout.
/// </summary>
public sealed class CliChatProvider(string name, CliProviderOptions o, ILogger log) : IChatProvider
{
    public string Name => name;
    public string? Description => o.Description;

    public async Task<ChatResult> AskAsync(string prompt, string? session, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo
        {
            FileName = o.Command,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = o.PromptVia.Equals("stdin", StringComparison.OrdinalIgnoreCase),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrEmpty(o.WorkingDir) ? Environment.CurrentDirectory : o.WorkingDir,
        };
        if (psi.RedirectStandardInput) psi.StandardInputEncoding = Encoding.UTF8;
        foreach (var (k, v) in o.Env) psi.Environment[k] = v;
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        bool promptInArgs = false;
        foreach (var a in BuildArgs(session, prompt, ref promptInArgs)) psi.ArgumentList.Add(a);
        if (!psi.RedirectStandardInput && !promptInArgs) psi.ArgumentList.Add(prompt);

        log.LogInformation("[{Name}] run: {Cmd} {Args}", name, o.Command,
            string.Join(' ', psi.ArgumentList.Select(a => a.Length > 60 ? a[..57] + "..." : a)));

        using var p = new Process { StartInfo = psi };
        try { p.Start(); }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"cannot start '{o.Command}': {ex.Message}");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, o.TimeoutSec)));

        var stdoutTask = p.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = p.StandardError.ReadToEndAsync(timeout.Token);
        if (psi.RedirectStandardInput)
        {
            await p.StandardInput.WriteAsync(prompt);
            p.StandardInput.Close();
        }
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"{name} did not answer within {o.TimeoutSec}s");
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        sw.Stop();

        if (p.ExitCode != 0)
            log.LogWarning("[{Name}] exit {Code}: {Err}", name, p.ExitCode, Trim(stderr, 300));

        string answer;
        if (o.Output.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            answer = ExtractJsonField(stdout, o.ResponseField)
                     ?? throw new InvalidOperationException($"{name}: field '{o.ResponseField}' not found in JSON output: {Trim(stdout, 200)}");
        }
        else
        {
            answer = stdout.Trim();
            if (answer.Length == 0 && p.ExitCode != 0)
                throw new InvalidOperationException($"{name} failed (exit {p.ExitCode}): {Trim(stderr, 200)}");
        }
        return new ChatResult(name, answer, sw.ElapsedMilliseconds, Trim(stdout, 2000));
    }

    private IEnumerable<string> BuildArgs(string? session, string prompt, ref bool promptInArgs)
    {
        var list = new List<string>();
        bool haveSession = !string.IsNullOrEmpty(session) && o.SessionArgs.Count > 0;
        bool markerSeen = false;
        foreach (var a in o.Args)
        {
            if (a == "{session-args}")
            {
                markerSeen = true;
                if (haveSession) list.AddRange(o.SessionArgs.Select(s => s.Replace("{session}", session)));
                continue;
            }
            if (a.Contains("{prompt}")) { promptInArgs = true; list.Add(a.Replace("{prompt}", prompt)); continue; }
            list.Add(a.Replace("{session}", session ?? ""));
        }
        if (haveSession && !markerSeen)
            list.AddRange(o.SessionArgs.Select(s => s.Replace("{session}", session)));
        return list;
    }

    /// <summary>Read a dotted path from the last JSON object found in stdout (CLIs sometimes print banners first).</summary>
    private static string? ExtractJsonField(string stdout, string path)
    {
        var text = stdout.Trim();
        int start = text.IndexOf('{');
        if (start < 0) return null;
        // try from the first '{'; if that fails, try each subsequent line that starts with '{'
        foreach (var candidate in Candidates(text))
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                JsonElement el = doc.RootElement;
                bool ok = true;
                foreach (var seg in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(seg, out var next)) el = next;
                    else { ok = false; break; }
                }
                if (!ok) continue;
                return el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
            }
            catch (JsonException) { }
        }
        return null;

        static IEnumerable<string> Candidates(string t)
        {
            yield return t[t.IndexOf('{')..];
            foreach (var line in t.Split('\n'))
            {
                var l = line.Trim();
                if (l.StartsWith('{')) yield return l;
            }
        }
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

/// <summary>Registry of chat providers from configuration, with a runtime-switchable default.</summary>
public sealed class ChatProviderRegistry
{
    private readonly Dictionary<string, IChatProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ChatOptions _opt;
    private readonly PairingStore _store;
    private readonly ILogger<ChatProviderRegistry> _log;

    public ChatProviderRegistry(IOptions<ChatOptions> opt, PairingStore store, ILoggerFactory lf, ILogger<ChatProviderRegistry> log)
    {
        _opt = opt.Value;
        _store = store;
        _log = log;
        foreach (var (name, p) in _opt.Providers)
        {
            if (string.IsNullOrWhiteSpace(p.Command)) { log.LogWarning("provider '{Name}' has no Command — skipped", name); continue; }
            _providers[name] = new CliChatProvider(name, p, lf.CreateLogger("chat." + name));
        }
        if (_providers.Count == 0) log.LogError("No chat providers configured (appsettings.json: Chat:Providers)");
        log.LogInformation("chat providers: {List} (default={Default})", string.Join(", ", _providers.Keys), DefaultName);
    }

    public IReadOnlyCollection<IChatProvider> All => _providers.Values;

    public string DefaultName
    {
        get
        {
            var saved = _store.Provider;
            if (!string.IsNullOrEmpty(saved) && _providers.ContainsKey(saved)) return saved;
            if (_providers.ContainsKey(_opt.Default)) return _opt.Default;
            return _providers.Keys.FirstOrDefault() ?? "";
        }
    }

    public bool SetDefault(string name)
    {
        if (!_providers.ContainsKey(name)) return false;
        _store.SetProvider(name);
        _log.LogInformation("default chat provider -> {Name}", name);
        return true;
    }

    public IChatProvider? Get(string? name) =>
        string.IsNullOrEmpty(name) ? Get(DefaultName) : _providers.GetValueOrDefault(name);

    public string ReplyStyle => _opt.ReplyStyle;
    public int MaxReplyChars => _opt.MaxReplyChars;
}
