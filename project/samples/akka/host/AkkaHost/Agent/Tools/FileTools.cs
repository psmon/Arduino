using System.Text;
using System.Text.Json;

namespace AkkaHost.Agent.Tools;

/// <summary>
/// Shared rules for the tools that touch the disk.
///
/// Everything is read-only, and every path is checked against the configured roots before it is
/// opened - an agent answering a watch has no business writing anything, and a model that has
/// been told to "look at my files" should not be able to wander into a profile directory because
/// a prompt talked it into it.
/// </summary>
internal static class FileGuard
{
    public static IReadOnlyList<string> Roots(AgentOptions options)
    {
        if (options.Roots.Count > 0)
            return options.Roots.Where(Directory.Exists).ToList();

        // No roots configured: the fixed drives, which is what "explore my drives" means here.
        return DriveScan.Drives
            .Where(d => d.Ready && d.Type == DriveType.Fixed)
            .Select(d => d.Root)
            .ToList();
    }

    public static bool Allowed(AgentOptions options, string path, out string full)
    {
        full = "";
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return false;
        }
        foreach (var root in Roots(options))
        {
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Human sizes; the model reads these, and "1.4 GB" costs fewer tokens than the digits.</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
    };
}

/// <summary>
/// The drives, probed once and remembered.
///
/// This machine has five letters (F, H, X, Y, Z) that point at media or shares which are not
/// there, and <see cref="DriveInfo.IsReady"/> on one of those blocks for seconds - the first
/// version of list_drives took 147 s to answer because it walked them twice. So each drive is
/// probed on its own task with a deadline, whatever misses it is reported as "not responding",
/// and the result is cached for the life of the host.
/// </summary>
internal static class DriveScan
{
    public sealed record Entry(string Root, DriveType Type, bool Ready, string Label, long Free, long Total);

    private static readonly Lazy<List<Entry>> Cache = new(Scan, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(700);

    public static IReadOnlyList<Entry> Drives => Cache.Value;

    private static List<Entry> Scan()
    {
        var drives = DriveInfo.GetDrives();
        var probes = drives.Select(d => Task.Run(() => Probe(d))).ToArray();
        // All drives in parallel, so one dead share costs the deadline rather than its own wait.
        Task.WaitAll(probes, Deadline + TimeSpan.FromMilliseconds(300));

        var entries = new List<Entry>();
        for (var i = 0; i < drives.Length; i++)
        {
            entries.Add(probes[i].IsCompletedSuccessfully
                ? probes[i].Result
                : new Entry(drives[i].Name, DriveType.Unknown, false, "not responding", 0, 0));
        }
        return entries;
    }

    private static Entry Probe(DriveInfo drive)
    {
        try
        {
            if (!drive.IsReady) return new Entry(drive.Name, drive.DriveType, false, "empty", 0, 0);
            return new Entry(drive.RootDirectory.FullName, drive.DriveType, true, drive.VolumeLabel,
                drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception)
        {
            return new Entry(drive.Name, DriveType.Unknown, false, "unreadable", 0, 0);
        }
    }
}

public sealed class ListDrivesTool(AgentOptions options) : AgentTool
{
    public override string Name => "list_drives";
    public override string Description =>
        "List the drives on this computer with their labels and free space. Start here when the user " +
        "asks what is on the machine and no path is known yet.";
    public override string ParametersJson => """{"type":"object","properties":{}}""";

    public override Task<string> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var text = new StringBuilder();
        foreach (var drive in DriveScan.Drives)
        {
            if (!drive.Ready) continue;
            var label = string.IsNullOrWhiteSpace(drive.Label) ? "" : $" \"{drive.Label}\"";
            text.AppendLine($"{drive.Root}{label} {drive.Type}, " +
                            $"{FileGuard.Size(drive.Free)} free of {FileGuard.Size(drive.Total)}");
        }
        var roots = string.Join(", ", FileGuard.Roots(options));
        text.AppendLine($"readable roots: {roots}");
        return Task.FromResult(text.ToString().TrimEnd());
    }
}

public sealed class ListDirectoryTool(AgentOptions options) : AgentTool
{
    public override string Name => "list_dir";
    public override string Description =>
        "List the folders and files directly inside one directory on this computer. Read-only.";
    public override string ParametersJson => """
        {"type":"object",
         "properties":{
           "path":{"type":"string","description":"Absolute directory path, e.g. E:\\music"},
           "limit":{"type":"integer","description":"Maximum entries to return, default 40"}},
         "required":["path"]}
        """;

    public override Task<string> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var path = Str(args, "path");
        var limit = Math.Clamp(Int(args, "limit", 40), 1, 200);

        if (!FileGuard.Allowed(options, path, out var full))
            return Task.FromResult(Error($"'{path}' is outside the readable roots"));
        if (!Directory.Exists(full))
            return Task.FromResult(Error($"no such directory: {full}"));

        var text = new StringBuilder($"{full}\n");
        var count = 0;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(full))
            {
                if (count++ >= limit) break;
                text.AppendLine($"  [dir]  {Path.GetFileName(dir)}");
            }
            foreach (var file in Directory.EnumerateFiles(full))
            {
                if (count++ >= limit) break;
                var info = new FileInfo(file);
                text.AppendLine($"  {FileGuard.Size(info.Length),9}  {info.Name}");
            }
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(Error("access denied"));
        }
        if (count >= limit) text.AppendLine($"  ... more than {limit} entries, narrow the path");
        return Task.FromResult(Ok(text.ToString().TrimEnd()));
    }
}

public sealed class FindFilesTool(AgentOptions options) : AgentTool
{
    public override string Name => "find_files";
    public override string Description =>
        "Search for files by name under a directory, recursively. Use when the user knows roughly " +
        "what a file is called but not where it is.";
    public override string ParametersJson => """
        {"type":"object",
         "properties":{
           "path":{"type":"string","description":"Directory to search under"},
           "query":{"type":"string","description":"Part of the file name, case-insensitive"},
           "limit":{"type":"integer","description":"Maximum hits, default 20"}},
         "required":["path","query"]}
        """;

    public override Task<string> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var path = Str(args, "path");
        var query = Str(args, "query");
        var limit = Math.Clamp(Int(args, "limit", 20), 1, 100);

        if (query.Length == 0) return Task.FromResult(Error("query is empty"));
        if (!FileGuard.Allowed(options, path, out var full))
            return Task.FromResult(Error($"'{path}' is outside the readable roots"));
        if (!Directory.Exists(full)) return Task.FromResult(Error($"no such directory: {full}"));

        var hits = new List<string>();
        // A manual walk so one unreadable folder does not abort the search, which is what
        // EnumerateFiles with AllDirectories does.
        var stack = new Stack<string>();
        stack.Push(full);
        while (stack.Count > 0 && hits.Count < limit && !ct.IsCancellationRequested)
        {
            var dir = stack.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (Path.GetFileName(file).Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        hits.Add(file);
                        if (hits.Count >= limit) break;
                    }
                }
                foreach (var sub in Directory.EnumerateDirectories(dir)) stack.Push(sub);
            }
            catch (Exception)
            {
                // unreadable folder: skip it and keep going
            }
        }

        if (hits.Count == 0) return Task.FromResult($"no file under {full} matches '{query}'");
        return Task.FromResult(string.Join("\n", hits));
    }
}
