using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AkkaHost.Agent.Os;
using AkkaHost.Agent.Tools;

namespace AkkaHost.Agent;

/// <summary>
/// AskBot's own agent: an LLM plus a handful of tools that act on this PC.
///
/// The Chat app keeps talking to netclaw and the other agent CLIs, which are whole agents in
/// their own right. AskBot instead drives the model directly, so the toolchain is ours to extend -
/// the first two families look around the local drives and play the music that is on them.
///
/// One instance serves every device; conversations are kept apart by session key.
/// </summary>
public sealed class AgentRunner : IDisposable
{
    private readonly AgentOptions _options;
    private readonly Action<string, string> _log;
    private readonly LlmClient _client;
    private readonly List<AgentTool> _tools;
    private readonly IMusicPlayer _player;
    private readonly MusicLibrary _library;

    // Per-session history, so "play the next one" and "and the other artist?" mean something.
    private readonly Dictionary<string, List<LlmMessage>> _sessions = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    // One request at a time per runner: a small local model serves one prompt at a time anyway,
    // and this keeps two devices from interleaving tool calls on the same speaker.
    private readonly SemaphoreSlim _slot = new(1, 1);

    /// <summary>How many turns of history to keep. Two exchanges plus their tool traffic is
    /// enough for follow-ups and still cheap for a 4B model.</summary>
    private const int MaxHistory = 12;

    public AgentRunner(AgentOptions options, Action<string, string> log)
    {
        _options = options;
        _log = log;
        _client = new LlmClient(options);
        _player = MusicPlayerFactory.Create();
        _library = new MusicLibrary(options.MusicRoot);

        _tools =
        [
            new ListDrivesTool(options),
            new ListDirectoryTool(options),
            new FindFilesTool(options),
            new FindMusicTool(_library, _player),
            new PlayMusicTool(_library, _player),
            new StopMusicTool(_library, _player),
            new NowPlayingTool(_library, _player),
        ];

        var offered = string.Join(", ", Tools.Select(t => t.Name));
        var withheld = _tools.Where(t => !t.Available).Select(t => t.Name).ToList();
        _log("info", $"agent: {options.Model} at {options.BaseUrl}, tools: {offered}");
        if (withheld.Count > 0)
            _log("info", $"agent: unavailable here, not offered: {string.Join(", ", withheld)} ({_player.Status})");
        if (_library.Exists)
            _log("info", $"agent: music library {_library.Root}, {_library.Tracks.Count} tracks");
        else
            _log("warn", $"agent: no music library at {_library.Root}");
    }

    public string Name => "agent";
    public string Model => _options.Model;
    public string Endpoint => _client.Endpoint;
    /// <summary>What the music tools are playing right now, if anything.</summary>
    public string? NowPlaying => _player.NowPlaying;

    /// <summary>Only the tools that can actually work here. An unavailable tool is never described
    /// to the model, so it cannot promise the watch something this host cannot do.</summary>
    public IReadOnlyList<AgentTool> Tools => _tools.Where(t => t.Available).ToList();

    /// <param name="replyLanguage">The language the answer must be in - the watch's output language
    /// setting. Null falls back to the script the question was written in.</param>
    public async Task<string> AskAsync(string prompt, string? session, CancellationToken ct,
        string? replyLanguage = null)
    {
        await _slot.WaitAsync(ct);
        try
        {
            return await RunAsync(prompt, session ?? "default", replyLanguage, ct);
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>Forgets a session's history - the device's "new conversation" button.</summary>
    public void Reset(string session)
    {
        lock (_lock) _sessions.Remove(session);
    }

    private async Task<string> RunAsync(string prompt, string session, string? replyLanguage,
        CancellationToken ct)
    {
        var messages = new List<LlmMessage> { new("system", SystemPrompt(Language(replyLanguage, prompt))) };
        lock (_lock)
        {
            if (_sessions.TryGetValue(session, out var history)) messages.AddRange(history);
        }
        messages.Add(new LlmMessage("user", prompt));

        var tools = Tools;
        var watch = Stopwatch.StartNew();
        var used = new List<string>();

        for (var step = 0; step < _options.MaxSteps; step++)
        {
            var reply = await _client.CompleteAsync(messages, tools, ct);

            if (reply.ToolCalls.Count == 0)
            {
                var answer = (reply.Content ?? "").Trim();
                if (answer.Length == 0)
                {
                    // A model that stops with nothing to say after working is still an answer to
                    // report: say what was done rather than showing the watch an empty bubble.
                    answer = used.Count > 0 ? $"Done ({string.Join(", ", used)})." : "I have no answer for that.";
                }
                Remember(session, prompt, answer);
                _log("debug", $"agent answered in {watch.ElapsedMilliseconds}ms, {step} tool step(s)");
                return answer;
            }

            // Keep the assistant turn that asked, then answer every call it made: the protocol
            // requires one tool message per id, in order, or the next completion rejects the run.
            messages.Add(new LlmMessage("assistant", reply.Content, ToolCalls: reply.ToolCalls));
            foreach (var call in reply.ToolCalls)
            {
                ct.ThrowIfCancellationRequested();
                var result = await InvokeAsync(call, ct);
                used.Add(call.Name);
                messages.Add(new LlmMessage("tool", result, ToolCallId: call.Id, Name: call.Name));
            }
        }

        // Out of steps: ask once more with no tools so the model has to answer in words.
        var final = await _client.CompleteAsync(messages, [], ct);
        var text = (final.Content ?? "").Trim();
        if (text.Length == 0) text = $"I ran out of steps after {string.Join(", ", used)}.";
        Remember(session, prompt, text);
        return text;
    }

    private async Task<string> InvokeAsync(LlmToolCall call, CancellationToken ct)
    {
        var tool = _tools.FirstOrDefault(t =>
            string.Equals(t.Name, call.Name, StringComparison.OrdinalIgnoreCase));
        if (tool == null) return $"error: no tool named {call.Name}";
        if (!tool.Available) return $"error: {call.Name} is not available on this host";

        JsonElement args;
        JsonDocument? document = null;
        try
        {
            // Models occasionally send "{}" as an empty string, or arguments already unescaped.
            var json = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson;
            document = JsonDocument.Parse(json);
            args = document.RootElement;
        }
        catch (JsonException ex)
        {
            document?.Dispose();
            return $"error: could not read arguments ({ex.Message})";
        }

        try
        {
            var watch = Stopwatch.StartNew();
            var result = await tool.InvokeAsync(args, ct);
            _log("debug", $"agent tool {call.Name}{Head(call.ArgumentsJson, 80)} -> " +
                          $"{result.Length} chars in {watch.ElapsedMilliseconds}ms");
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failing tool is information the model can act on, not a failed request.
            _log("warn", $"agent tool {call.Name} threw: {ex.Message}");
            return $"error: {ex.Message}";
        }
        finally
        {
            document.Dispose();
        }
    }

    private void Remember(string session, string prompt, string answer)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(session, out var history))
                _sessions[session] = history = [];
            history.Add(new LlmMessage("user", prompt));
            history.Add(new LlmMessage("assistant", answer));
            // Only the plain exchanges are kept; the tool traffic was scaffolding for one answer
            // and replaying it would cost more context than it is worth on a 4B model.
            if (history.Count > MaxHistory) history.RemoveRange(0, history.Count - MaxHistory);
        }
    }

    /// <summary>
    /// Which language the answer must be in. The watch's Settings screen already decides this for
    /// speech, so the same choice decides the text; with nothing set, the script of the question
    /// does - a 4B model asked in Korean answers in English unless it is told the language by name.
    /// </summary>
    private static string Language(string? requested, string prompt)
    {
        var code = (requested ?? "").Trim().ToLowerInvariant();
        if (code.Length == 0 || code == "auto")
            code = prompt.Any(c => c >= 0xAC00 && c <= 0xD7A3) ? "ko" : "en";

        return code switch
        {
            "ko" or "ko-kr" => "Korean",
            "en" or "en-us" or "en-gb" => "English",
            "ja" => "Japanese",
            "zh" => "Chinese",
            _ => code,
        };
    }

    private string SystemPrompt(string language)
    {
        var text = new StringBuilder();
        text.AppendLine("You are AskBot, an assistant answering on a small round smartwatch screen, " +
                        "often read aloud by a speech synthesiser.");
        text.AppendLine("Keep answers to one or two short sentences with no markdown, no lists and no " +
                        "code blocks.");
        // A 4B model follows the language of the tool output rather than the question: it answered a
        // Korean question in English and read "C:" out as "C colon". Naming the language works; asking
        // it to match the user's language did not.
        text.AppendLine($"Write the answer in {language}. Tool results are in English, but your answer " +
                        $"must be in {language}.");
        text.AppendLine("Write drive letters and paths as they are (C:), and quote track and file names " +
                        "exactly as the tools return them - never translate or respell them.");
        text.AppendLine("You act on the user's own Windows PC through tools. Use them instead of guessing, " +
                        "and never claim to have done something a tool did not report.");
        if (_library.Exists)
        {
            text.AppendLine($"The user's music is at {_library.Root}. When they ask for their music, " +
                            "search it with find_music and start it with play_music.");
        }
        text.Append("After a tool runs, say plainly what happened - which track is playing, what was found.");
        return text.ToString();
    }

    private static string Head(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    public void Dispose()
    {
        _player.Stop();
        _client.Dispose();
        _slot.Dispose();
    }
}
