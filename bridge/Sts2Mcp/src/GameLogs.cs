using System.Text.RegularExpressions;

namespace Sts2Mcp;

/// <summary>Reads the ends of the game's logs and mods' own log files.</summary>
public static class GameLogs
{
    /// <summary>
    /// <paramref name="source"/>: "game" (the Godot log of the copy started at <paramref name="startedAt"/>, else the newest),
    /// "bridge" (sts2bridge_log.txt), or a file name in the game's data folder such as ringdraft_log.txt.
    /// </summary>
    public static string Tail(GamePaths paths, string source, int lines, string? match, DateTimeOffset? startedAt)
    {
        string file = Resolve(paths, source, startedAt);
        if (!File.Exists(file)) throw new ToolError($"{file} not found");

        var all = ReadShared(file).TrimEnd('\n', '\r').Split('\n');
        IEnumerable<string> selected = all;
        if (!string.IsNullOrEmpty(match))
        {
            Regex re;
            try { re = new Regex(match, RegexOptions.IgnoreCase); }
            catch (ArgumentException e) { throw new ToolError($"bad match regex: {e.Message}"); }
            selected = all.Where(l => re.IsMatch(l));
        }
        var tail = selected.TakeLast(Math.Clamp(lines, 1, 2000)).Select(l => l.TrimEnd('\r'));
        return $"== {file}\n{string.Join("\n", tail)}";
    }

    private static string Resolve(GamePaths paths, string source, DateTimeOffset? startedAt)
    {
        switch (source.ToLowerInvariant())
        {
            case "" or "game":
                if (!Directory.Exists(paths.LogsDir)) throw new ToolError($"no game logs at {paths.LogsDir}");
                var logs = new DirectoryInfo(paths.LogsDir).GetFiles("godot*.log").OrderBy(f => f.LastWriteTimeUtc).ToList();
                if (logs.Count == 0) throw new ToolError($"no godot*.log in {paths.LogsDir}");
                if (startedAt is { } t)
                {
                    // The log a copy writes is created when it starts: pick the first one created after its start.
                    var match = logs.Where(f => f.CreationTimeUtc >= t.UtcDateTime.AddSeconds(-30)).OrderBy(f => f.CreationTimeUtc).FirstOrDefault();
                    if (match is not null) return match.FullName;
                }
                return logs[^1].FullName;
            case "bridge":
                return Path.Combine(paths.DataDir, "sts2bridge_log.txt");
            default:
                if (source.Contains("..") || Path.IsPathRooted(source)) throw new ToolError("source must be game, bridge, or a file name in the game's data folder");
                return Path.Combine(paths.DataDir, source);
        }
    }

    /// <summary>Reads a file the game may still be writing.</summary>
    private static string ReadShared(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        const long maxBytes = 4 * 1024 * 1024;
        if (stream.Length > maxBytes) stream.Seek(-maxBytes, SeekOrigin.End);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
