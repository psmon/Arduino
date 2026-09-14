using System.Text.Json;

namespace AkkaHost.Agent;

/// <summary>
/// One thing the agent can do. The model sees <see cref="Name"/>, <see cref="Description"/> and
/// <see cref="ParametersJson"/> (a JSON Schema object); everything else is ours.
///
/// Tools are added case by case as the watch needs them - the first two families are looking
/// around the local drives and playing the music on them.
/// </summary>
public abstract class AgentTool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract string ParametersJson { get; }

    /// <summary>Runs the tool. The returned string goes back to the model verbatim, so keep it
    /// compact and factual - it is context the model pays for.</summary>
    public abstract Task<string> InvokeAsync(JsonElement args, CancellationToken ct);

    /// <summary>False when this tool cannot work here (wrong OS, missing folder). Unavailable
    /// tools are never offered to the model, so it cannot promise what the host cannot do.</summary>
    public virtual bool Available => true;

    protected static string Str(JsonElement args, string name, string fallback = "")
        => args.ValueKind == JsonValueKind.Object &&
           args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback
            : fallback;

    protected static int Int(JsonElement args, string name, int fallback)
        => args.ValueKind == JsonValueKind.Object &&
           args.TryGetProperty(name, out var v) && v.TryGetInt32(out var i)
            ? i
            : fallback;

    protected static string Ok(string text) => text;

    protected static string Error(string text) => "error: " + text;
}
