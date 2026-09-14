using System.Text;
using System.Text.Json;
using AkkaHost.Agent.Os;

namespace AkkaHost.Agent.Tools;

/// <summary>
/// The music library as the tools see it: one scan of the configured root, cached, with the artist
/// and title pulled out of the file name.
///
/// The library on this machine is named "Artist-NN-Title.mp3", so that pattern is parsed; anything
/// that does not match still works, because the whole file name stays searchable either way.
/// </summary>
public sealed class MusicLibrary(string root)
{
    public sealed record Track(string Path, string Artist, string Title)
    {
        public string Display => Artist.Length > 0 ? $"{Artist} - {Title}" : Title;
    }

    private static readonly string[] Extensions = [".mp3", ".m4a", ".wav", ".flac", ".wma", ".ogg"];
    private readonly object _lock = new();
    private List<Track>? _tracks;

    public string Root => root;
    public bool Exists => Directory.Exists(root);

    public IReadOnlyList<Track> Tracks
    {
        get { lock (_lock) return _tracks ??= Scan(); }
    }

    public void Invalidate()
    {
        lock (_lock) _tracks = null;
    }

    private List<Track> Scan()
    {
        var tracks = new List<Track>();
        if (!Exists) return tracks;

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!Extensions.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
            tracks.Add(Parse(file));
        }
        tracks.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase));
        return tracks;
    }

    private static Track Parse(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        // Artist-NN-Title: split on the first two dashes only, because titles contain dashes too.
        var first = stem.IndexOf('-');
        if (first > 0)
        {
            var second = stem.IndexOf('-', first + 1);
            if (second > first + 1 &&
                int.TryParse(stem.AsSpan(first + 1, second - first - 1).Trim(), out _))
            {
                return new Track(path, stem[..first].Trim(), stem[(second + 1)..].Trim());
            }
        }
        return new Track(path, "", stem);
    }

    /// <summary>Loose match over artist and title: every whitespace-separated word must appear
    /// somewhere. That is enough for spoken queries, which drop and reorder parts of a title.</summary>
    public List<Track> Search(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return Tracks.ToList();

        return Tracks.Where(t =>
        {
            var haystack = t.Artist + " " + t.Title;
            return words.All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase));
        }).ToList();
    }
}

/// <summary>Base for the music tools: they share the library, the player and the availability rule.</summary>
public abstract class MusicTool(MusicLibrary library, IMusicPlayer player) : AgentTool
{
    protected MusicLibrary Library { get; } = library;
    protected IMusicPlayer Player { get; } = player;

    /// <summary>No library folder or no playback backend means the model is never told these tools
    /// exist, so it cannot offer to play music on a host that cannot.</summary>
    public override bool Available => Library.Exists && Player.Available;

    protected static string Describe(IReadOnlyList<MusicLibrary.Track> tracks, int limit)
    {
        var text = new StringBuilder();
        for (var i = 0; i < tracks.Count && i < limit; i++) text.AppendLine($"{i + 1}. {tracks[i].Display}");
        if (tracks.Count > limit) text.AppendLine($"... and {tracks.Count - limit} more");
        return text.ToString().TrimEnd();
    }
}

public sealed class FindMusicTool(MusicLibrary library, IMusicPlayer player) : MusicTool(library, player)
{
    public override string Name => "find_music";
    public override string Description =>
        "Search the user's own music library by artist or title, or list it when the query is empty. " +
        "Use this before play_music whenever it is not obvious which track is meant.";
    public override string ParametersJson => """
        {"type":"object",
         "properties":{
           "query":{"type":"string","description":"Artist or part of a title; empty lists the library"},
           "limit":{"type":"integer","description":"Maximum tracks to return, default 12"}},
         "required":[]}
        """;

    public override Task<string> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var query = Str(args, "query");
        var limit = Math.Clamp(Int(args, "limit", 12), 1, 60);
        var hits = Library.Search(query);

        if (hits.Count == 0)
            return Task.FromResult($"no track matches '{query}' ({Library.Tracks.Count} tracks in the library)");

        var header = query.Length == 0
            ? $"{Library.Tracks.Count} tracks in the library:"
            : $"{hits.Count} match '{query}':";
        return Task.FromResult(header + "\n" + Describe(hits, limit));
    }
}

public sealed class PlayMusicTool(MusicLibrary library, IMusicPlayer player) : MusicTool(library, player)
{
    public override string Name => "play_music";
    public override string Description =>
        "Play a track from the user's music library on this computer's speakers. Give a query and the " +
        "best match starts playing; with no query a random track plays.";
    public override string ParametersJson => """
        {"type":"object",
         "properties":{
           "query":{"type":"string","description":"Artist or part of a title; empty picks at random"}},
         "required":[]}
        """;

    public override Task<string> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var query = Str(args, "query");
        var hits = Library.Search(query);
        if (hits.Count == 0)
            return Task.FromResult(Error($"nothing in the library matches '{query}'"));

        // An empty query is an explicit "anything", so pick freely; a real query means the first
        // (best-sorted) hit, with the rest reported so the model can offer them.
        var track = query.Length == 0 ? hits[Random.Shared.Next(hits.Count)] : hits[0];

        if (!Player.Play(track.Path, out var error))
            return Task.FromResult(Error($"could not play {track.Display}: {error}"));

        var also = hits.Count > 1 && query.Length > 0 ? $" ({hits.Count - 1} other matches)" : "";
        return Task.FromResult($"now playing: {track.Display}{also}");
    }
}

public sealed class StopMusicTool(MusicLibrary library, IMusicPlayer player) : MusicTool(library, player)
{
    public override string Name => "stop_music";
    public override string Description => "Stop the music playing on this computer.";
    public override string ParametersJson => """{"type":"object","properties":{}}""";

    public override Task<string> InvokeAsync(JsonElement args, CancellationToken ct)
        => Task.FromResult(Player.Stop() ? "stopped" : "nothing was playing");
}

public sealed class NowPlayingTool(MusicLibrary library, IMusicPlayer player) : MusicTool(library, player)
{
    public override string Name => "now_playing";
    public override string Description => "Report which track is playing on this computer and how far in.";
    public override string ParametersJson => """{"type":"object","properties":{}}""";

    public override Task<string> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var path = Player.NowPlaying;
        if (path == null) return Task.FromResult("nothing is playing");

        var name = Path.GetFileNameWithoutExtension(path);
        var (position, length) = Player.Progress;
        if (length <= 0) return Task.FromResult($"playing: {name}");
        return Task.FromResult($"playing: {name} ({Clock(position)} / {Clock(length)})");
    }

    private static string Clock(int ms) => TimeSpan.FromMilliseconds(ms).ToString("m\\:ss");
}
