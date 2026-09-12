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

    // Only one child of this provider may run at a time. A chat CLI keeps per-session server state and
    // per-session files - netclaw holds an exclusive lock on ~/.netclaw/logs/<session>.log - so two
    // overlapping prompts on one session make the second fail instantly. Preemption therefore means
    // "abandon the answer", not "run both": the next request waits here until the old child is gone.
    private readonly SemaphoreSlim _slot = new(1, 1);
    private const int DetachGraceSec = 12;

    public async Task<ChatResult> AskAsync(string prompt, string? session, CancellationToken ct = default)
    {
        await _slot.WaitAsync(ct);
        bool release = true;
        try { return await RunOnceAsync(prompt, session, () => release = false, ct); }
        finally { if (release) _slot.Release(); }
    }

    /// <param name="onDetach">Called when the child is abandoned: its reaper takes over the slot.</param>
    private async Task<ChatResult> RunOnceAsync(string prompt, string? session, Action onDetach, CancellationToken ct)
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

        var p = new Process { StartInfo = psi };
        bool detached = false;
        try { p.Start(); }
        catch (Exception ex)
        {
            p.Dispose();
            throw new InvalidOperationException($"cannot start '{o.Command}': {ex.Message}");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, o.TimeoutSec)));

        // Deliberately untokenised: if we abandon this child we still have to drain its pipes, or it
        // blocks on a full buffer and never exits.
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        string stdout, stderr;
        int exitCode;                     // read before the finally disposes the Process
        try
        {
            if (psi.RedirectStandardInput)
            {
                await p.StandardInput.WriteAsync(prompt);
                p.StandardInput.Close();
            }
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Preempted by a newer question. Let this one finish on its own and throw away the
                // answer: killing it mid-flight wedges the CLI's server-side session (netclaw leaves the
                // daemon hub connection half-open and every later prompt on that session id fails).
                detached = true;
                onDetach();                             // the reaper below owns the slot from here on
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(DetachGraceSec));
                        try { await p.WaitForExitAsync(grace.Token); }
                        catch (OperationCanceledException)
                        {
                            log.LogWarning("[{Name}] abandoned child still running after {Sec}s - killing it; "
                                         + "its session may need rotating", name, DetachGraceSec);
                            try { p.Kill(entireProcessTree: true); } catch { }
                            try { await p.WaitForExitAsync(); } catch { }
                        }
                        await stdoutTask; await stderrTask;
                    }
                    catch { }
                    finally { p.Dispose(); _slot.Release(); }
                });
                throw;
            }
            catch (OperationCanceledException)
            {
                // A genuinely stuck child has to be killed even though it may cost us the session.
                try { p.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException($"{name} did not answer within {o.TimeoutSec}s");
            }
            stdout = await stdoutTask;
            stderr = await stderrTask;
            exitCode = p.ExitCode;
        }
        finally { if (!detached) p.Dispose(); }
        sw.Stop();

        if (exitCode != 0)
            log.LogWarning("[{Name}] exit {Code}: {Err}", name, exitCode, Trim(stderr, 300));

        string answer;
        if (o.Output.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            answer = ExtractJsonField(stdout, o.ResponseField)
                     ?? throw new InvalidOperationException($"{name}: field '{o.ResponseField}' not found in JSON output: {Trim(stdout, 200)}");
        }
        else
        {
            answer = stdout.Trim();
            if (answer.Length == 0 && exitCode != 0)
                throw new InvalidOperationException($"{name} failed (exit {exitCode}): {Trim(stderr, 200)}");
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
