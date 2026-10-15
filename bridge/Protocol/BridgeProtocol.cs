// Shared by the in-game bridge mod (Sts2Bridge) and the MCP server (sts2-mcp).
// Compiled into both; no dependencies beyond the .NET base library, no game references.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Sts2Bridge.Protocol;

/// <summary>Constants and conventions both sides agree on.</summary>
public static class BridgeInfo
{
    /// <summary>Bumped whenever a request or response shape changes incompatibly.</summary>
    public const int ProtocolVersion = 1;

    public const string BridgeVersion = "0.1.0";

    public const string ModId = "Sts2Bridge";

    /// <summary>Port for the first local test profile (1001 → 7701, 1002 → 7702, ...).</summary>
    public const int BasePort = 7700;

    /// <summary>The bridge only switches on in a game started with one of these arguments.</summary>
    public const string OfflineArg = "--force-steam=off";
    public const string ExplicitArg = "--sts2-bridge";

    /// <summary>
    /// Console commands the bridge refuses: they touch Steam Cloud, online services, real achievements,
    /// or crash the game on purpose.
    /// </summary>
    public static readonly IReadOnlySet<string> BlockedConsoleCommands =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cloud", "sentry", "leaderboard", "achievement", "open", "getlogs" };

    /// <summary>The preferred port for a test profile. 1001..1099 map to 7701..7799; anything else uses 7700.</summary>
    public static int PreferredPort(int? clientId) =>
        clientId is >= 1000 and <= 1099 ? BasePort + clientId.Value - 1000 : BasePort;

    /// <summary>Ports to try in order when the preferred one is taken.</summary>
    public static IEnumerable<int> CandidatePorts(int? clientId)
    {
        int first = PreferredPort(clientId);
        for (int i = 0; i < 5; i++) yield return first + i * 100;
    }

    /// <summary>Reads <c>--name=value</c> (or <c>--name value</c>) from command-line arguments.</summary>
    public static string? ArgValue(IEnumerable<string> args, string name)
    {
        var list = args as IList<string> ?? args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            string a = list[i];
            if (a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return a[(name.Length + 1)..];
            if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase) && i + 1 < list.Count && !list[i + 1].StartsWith("--"))
                return list[i + 1];
        }
        return null;
    }

    public static int? ClientId(IEnumerable<string> args) =>
        int.TryParse(ArgValue(args, "--clientId"), out int id) ? id : null;

    public static bool ShouldActivate(IEnumerable<string> args) =>
        args.Any(a => string.Equals(a, OfflineArg, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(a, ExplicitArg, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first word of a console command, lower-cased.</summary>
    /// <summary>The game's speed settings (PrefsSave.FastMode), as the bridge names them.</summary>
    public static readonly string[] Speeds = ["normal", "fast", "instant"];

    public static string ConsoleVerb(string command) =>
        command.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "";
}

/// <summary>Where the bridge keeps its files, under the game's user data folder (%APPDATA%/SlayTheSpire2 on Windows).</summary>
public static class BridgePaths
{
    public static string Root(string gameUserDataDir) => Path.Combine(gameUserDataDir, "bridge");
    public static string Instances(string gameUserDataDir) => Path.Combine(Root(gameUserDataDir), "instances");
    public static string InstanceFile(string gameUserDataDir, int pid) => Path.Combine(Instances(gameUserDataDir), $"{pid}.json");

    /// <summary>File queue for one game copy: requests go in <c>in/</c>, responses come back in <c>out/</c>.</summary>
    public static string Queue(string gameUserDataDir, int? clientId) =>
        Path.Combine(Root(gameUserDataDir), "queue", clientId?.ToString() ?? "default");
}

/// <summary>Written by each running bridge to <see cref="BridgePaths.Instances"/>; read by the server to find it.</summary>
public sealed record InstanceInfo
{
    public int Pid { get; init; }
    public int Port { get; init; }
    public string Token { get; init; } = "";
    public int? ClientId { get; init; }
    public string GameVersion { get; init; } = "";
    public string BridgeVersion { get; init; } = "";
    public int Protocol { get; init; }
    public string QueueDir { get; init; } = "";
    public string StartedAt { get; init; } = "";
    /// <summary>"host", "join", or null when the copy was started without --fastmp.</summary>
    public string? FastMp { get; init; }
}

/// <summary>One request. Over TCP: one JSON object per line. In the file queue: one JSON object per file.</summary>
public sealed record BridgeRequest
{
    public long Id { get; init; }
    public string? Cmd { get; init; }
    public JsonObject? Args { get; init; }

    /// <summary>
    /// Optional: Unix time in milliseconds after which the caller has given up. The bridge then drops the request
    /// instead of acting late (a press the caller was told had failed must not happen afterwards).
    /// </summary>
    public long? DeadlineMs { get; init; }

    public bool Expired(DateTimeOffset now) => DeadlineMs is { } d && now.ToUnixTimeMilliseconds() > d;
}

public sealed record BridgeResponse
{
    public long Id { get; init; }
    public bool Ok { get; init; }
    public JsonNode? Result { get; init; }
    public string? Error { get; init; }

    /// <summary>
    /// True when the failure means "not yet" (the button isn't there yet, the card isn't playable yet, it's not this
    /// side's turn): the same request may succeed a moment later. Plans retry these until their timeout.
    /// </summary>
    public bool? Retry { get; init; }

    public static BridgeResponse Success(long id, JsonNode? result) => new() { Id = id, Ok = true, Result = result };
    public static BridgeResponse Failure(long id, string error, bool retry = false) =>
        new() { Id = id, Ok = false, Error = error, Retry = retry ? true : null };
}

/// <summary>JSON settings used on both sides: camelCase, compact, nulls omitted.</summary>
public static class Wire
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Decode<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>Upper bound on one line, so a broken client can't make the game buffer without limit.</summary>
    public const int MaxLineLength = 1 << 20;
}

/// <summary>Helpers for reading request arguments.</summary>
public static class Args
{
    public static string? Str(JsonObject? args, string name) =>
        args?[name] is JsonValue v && v.TryGetValue(out string? s) ? s : args?[name]?.ToString();

    public static int? Int(JsonObject? args, string name)
    {
        if (args?[name] is not JsonValue v) return null;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out long l)) return (int)l;
        if (v.TryGetValue(out double d)) return (int)d;
        if (v.TryGetValue(out string? s) && int.TryParse(s, out int p)) return p;
        return null;
    }

    public static bool Bool(JsonObject? args, string name, bool fallback = false)
    {
        if (args?[name] is not JsonValue v) return fallback;
        if (v.TryGetValue(out bool b)) return b;
        if (v.TryGetValue(out string? s) && bool.TryParse(s, out bool p)) return p;
        return fallback;
    }

    public static List<string> StrList(JsonObject? args, string name) =>
        args?[name] is JsonArray a ? a.Select(n => n?.ToString() ?? "").Where(s => s.Length > 0).ToList() : new List<string>();
}
