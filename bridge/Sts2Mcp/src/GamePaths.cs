using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Sts2Mcp;

/// <summary>Where the game, its player data and this tool's own files are.</summary>
public sealed class GamePaths
{
    public const string GameFolderName = "Slay the Spire 2";
    public const string ExeName = "SlayTheSpire2.exe";
    public const string DataSubdir = "data_sts2_windows_x86_64";

    /// <summary>The game install (folder with SlayTheSpire2.exe), or null when not found.</summary>
    public string? GameDir { get; init; }

    /// <summary>The game's user data folder (%APPDATA%/SlayTheSpire2 on Windows): profiles, logs, bridge files.</summary>
    public string DataDir { get; init; } = "";

    /// <summary>This tool's cache: built bridge mods, launch records, backups.</summary>
    public string CacheDir { get; init; } = "";

    public string? Sts2Dll => GameDir is null ? null : Path.Combine(GameDir, DataSubdir, "sts2.dll");
    public string? Pck => GameDir is null ? null : Path.Combine(GameDir, "SlayTheSpire2.pck");
    public string? Exe => GameDir is null ? null : Path.Combine(GameDir, ExeName);
    public string? ModsDir => GameDir is null ? null : Path.Combine(GameDir, "mods");
    public string LogsDir => Path.Combine(DataDir, "logs");

    public string? GameVersion()
    {
        if (GameDir is null) return null;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(Path.Combine(GameDir, "release_info.json")));
            return $"{node?["version"]} ({node?["branch"]})";
        }
        catch { return null; }
    }

    /// <summary>Settings file of an offline test profile (the game's --clientId).</summary>
    public string ProfileSettings(int clientId) => Path.Combine(DataDir, "default", clientId.ToString(), "settings.save");

    /// <summary>
    /// Finds everything. Overrides: STS2_GAME_DIR, STS2_DATA_DIR, STS2_MCP_CACHE. Otherwise the game is looked up in
    /// Steam's library folders.
    /// </summary>
    public static GamePaths Discover(string? gameDirOverride = null)
    {
        string? game = gameDirOverride ?? Environment.GetEnvironmentVariable("STS2_GAME_DIR");
        if (string.IsNullOrWhiteSpace(game) || !Directory.Exists(game)) game = FindInSteam();

        string data = Environment.GetEnvironmentVariable("STS2_DATA_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SlayTheSpire2");

        string cache = Environment.GetEnvironmentVariable("STS2_MCP_CACHE") is { Length: > 0 } c
            ? c
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sts2-mcp");

        return new GamePaths { GameDir = game, DataDir = data, CacheDir = cache };
    }

    private static string? FindInSteam()
    {
        foreach (string steam in SteamRoots())
        {
            foreach (string library in LibraryFolders(steam).Prepend(steam))
            {
                string candidate = Path.Combine(library, "steamapps", "common", GameFolderName);
                if (File.Exists(Path.Combine(candidate, ExeName))) return candidate;
            }
        }
        return null;
    }

    private static IEnumerable<string> SteamRoots()
    {
        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key?.GetValue("SteamPath") is string p) roots.Add(p.Replace('/', Path.DirectorySeparatorChar));
            }
            catch { /* no registry access */ }
            roots.Add(@"C:\Program Files (x86)\Steam");
            roots.Add(@"C:\Program Files\Steam");
        }
        else
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            roots.Add(Path.Combine(home, ".steam", "steam"));
            roots.Add(Path.Combine(home, ".local", "share", "Steam"));
            roots.Add(Path.Combine(home, "Library", "Application Support", "Steam"));
        }
        return roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Library folders listed in steamapps/libraryfolders.vdf.</summary>
    public static IEnumerable<string> LibraryFolders(string steamRoot)
    {
        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) return [];
        try { return ParseLibraryFolders(File.ReadAllText(vdf)); }
        catch { return []; }
    }

    /// <summary>Pulls every <c>"path" "..."</c> value out of a libraryfolders.vdf file.</summary>
    public static List<string> ParseLibraryFolders(string vdfText) =>
        Regex.Matches(vdfText, "\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value.Replace(@"\\", @"\"))
            .ToList();
}
