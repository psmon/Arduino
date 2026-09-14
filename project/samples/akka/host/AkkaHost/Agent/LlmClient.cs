using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AkkaHost.Agent;

/// <summary>
/// A record so the command line can override one field of what appsettings.json said
/// (<c>options with { Model = ... }</c>) without a second copy of every default.
/// </summary>
public sealed record AgentOptions
{
    public bool Enabled { get; init; } = true;
    /// <summary>"lmstudio" or "openai" - both speak the same wire format; this only changes defaults.</summary>
    public string Provider { get; init; } = "lmstudio";
    public string BaseUrl { get; init; } = "https://a1.webnori.com";
    public string Model { get; init; } = "google/gemma-4-e4b";
    /// <summary>Empty for a keyless endpoint; sent as a bearer token when set.</summary>
    public string ApiKey { get; init; } = "";
    public double Temperature { get; init; } = 0.3;
    public int MaxTokens { get; init; } = 512;
    /// <summary>How many tool round trips before the loop gives up and answers with what it has.</summary>
    public int MaxSteps { get; init; } = 6;
    public int TimeoutSec { get; init; } = 120;
    /// <summary>Where the music tools look. Windows-only today.</summary>
    public string MusicRoot { get; init; } = @"E:\music\favorite-music";
    /// <summary>Directories the file tools may read. Empty = every fixed drive.</summary>
    public List<string> Roots { get; init; } = [];
}

/// <summary>One message in the conversation the model sees.</summary>
public sealed record LlmMessage(string Role, string? Content, string? ToolCallId = null,
    IReadOnlyList<LlmToolCall>? ToolCalls = null, string? Name = null);

public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson);

public sealed record LlmReply(string? Content, IReadOnlyList<LlmToolCall> ToolCalls, string FinishReason);

/// <summary>
/// An OpenAI-compatible chat client, which is all LM Studio and OpenAI both need.
///
/// The default endpoint is a LM Studio instance serving google/gemma-4-e4b, and it does support
/// native tool calling - checked before writing any of this, because it decides the whole design:
/// AgentZeroLite had to constrain a local llama.cpp with a GBNF grammar and parse one JSON object
/// per turn, which is a lot of machinery to avoid. Here `tools` goes up and `tool_calls` comes
/// back, so the loop is the standard one.
/// </summary>
public sealed class LlmClient : IDisposable
{
    private readonly AgentOptions _options;
    private readonly HttpClient _http;

    public LlmClient(AgentOptions options)
    {
        _options = options;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(options.TimeoutSec) };
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
    }

    public string Endpoint => $"{_options.BaseUrl.TrimEnd('/')}/v1/chat/completions";
    public string Model => _options.Model;

    public async Task<LlmReply> CompleteAsync(IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<AgentTool> tools, CancellationToken ct)
    {
        var body = BuildRequest(messages, tools);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(Endpoint, content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{(int)response.StatusCode} from {Endpoint}: {Head(text, 300)}");

        return ParseReply(text);
    }

    private string BuildRequest(IReadOnlyList<LlmMessage> messages, IReadOnlyList<AgentTool> tools)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(2048);
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions
               {
                   Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
               }))
        {
            w.WriteStartObject();
            w.WriteString("model", _options.Model);
            w.WriteNumber("temperature", _options.Temperature);
            w.WriteNumber("max_tokens", _options.MaxTokens);

            w.WriteStartArray("messages");
            foreach (var m in messages)
            {
                w.WriteStartObject();
                w.WriteString("role", m.Role);
                // A tool result carries the id of the call it answers; an assistant turn that asked
                // for tools carries the calls themselves and no content.
                if (m.ToolCallId != null) w.WriteString("tool_call_id", m.ToolCallId);
                if (m.Name != null) w.WriteString("name", m.Name);
                w.WriteString("content", m.Content ?? "");
                if (m.ToolCalls is { Count: > 0 })
                {
                    w.WriteStartArray("tool_calls");
                    foreach (var call in m.ToolCalls)
                    {
                        w.WriteStartObject();
                        w.WriteString("id", call.Id);
                        w.WriteString("type", "function");
                        w.WriteStartObject("function");
                        w.WriteString("name", call.Name);
                        w.WriteString("arguments", call.ArgumentsJson);
                        w.WriteEndObject();
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();

            if (tools.Count > 0)
            {
                w.WriteStartArray("tools");
                foreach (var tool in tools)
                {
                    w.WriteStartObject();
                    w.WriteString("type", "function");
                    w.WriteStartObject("function");
                    w.WriteString("name", tool.Name);
                    w.WriteString("description", tool.Description);
                    w.WritePropertyName("parameters");
                    using (var schema = JsonDocument.Parse(tool.ParametersJson))
                        schema.RootElement.WriteTo(w);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static LlmReply ParseReply(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            throw new InvalidOperationException($"no choices in reply: {Head(json, 200)}");

        var choice = choices[0];
        var finish = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() ?? "" : "";
        var message = choice.GetProperty("message");
        var content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;

        var calls = new List<LlmToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                var id = call.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                if (!call.TryGetProperty("function", out var fn)) continue;
                var name = fn.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var args = fn.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}";
                if (name.Length > 0) calls.Add(new LlmToolCall(id, name, args));
            }
        }
        return new LlmReply(content, calls, finish);
    }

    private static string Head(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    public void Dispose() => _http.Dispose();
}
