using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Bridge;

/// <summary>
/// A numbered log of things that happened in the game (screen changes, rooms, turns, fights, runs, console commands),
/// so a tool can wait for something instead of polling the whole state. Locked, in case a game event fires off
/// the main thread.
/// </summary>
internal static class GameEvents
{
    private const int Capacity = 500;
    private static readonly LinkedList<JsonObject> Buffer = new();
    private static long _next = 1;
    private static readonly object Gate = new();

    private static CombatManager? _subscribed;
    private static string _lastScreen = "";
    private static bool _lastInRun;
    private static int _lastFloor = -1;
    private static string _lastRoom = "";

    public static long Next { get { lock (Gate) return _next; } }

    public static void Start() => Push("bridge_started", new JsonObject());

    public static void Push(string type, JsonObject data)
    {
        lock (Gate)
        {
            var e = new JsonObject
            {
                ["seq"] = _next++,
                ["time"] = DateTimeOffset.Now.ToString("HH:mm:ss.fff"),
                ["type"] = type,
                ["data"] = data,
            };
            Buffer.AddLast(e);
            while (Buffer.Count > Capacity) Buffer.RemoveFirst();
        }
    }

    /// <summary>Events with seq &gt;= since, oldest first, at most <paramref name="max"/>.</summary>
    public static JsonObject Since(long since, int max)
    {
        lock (Gate) return SinceLocked(since, max);
    }

    private static JsonObject SinceLocked(long since, int max)
    {
        var list = new JsonArray();
        long next = Math.Min(Math.Max(since, 0), _next);
        bool truncated = false;
        foreach (var e in Buffer)
        {
            long seq = (long)e["seq"]!;
            if (seq < since) continue;
            if (list.Count >= max) { truncated = true; break; }
            list.Add(e.DeepClone());
            next = seq + 1;
        }
        if (!truncated) next = _next;
        long oldest = Buffer.First is { } f ? (long)f.Value["seq"]! : _next;
        return new JsonObject { ["next"] = next, ["oldest"] = oldest, ["more"] = truncated ? true : null, ["events"] = list };
    }

    /// <summary>Called every frame: turns state changes into events.</summary>
    public static void Poll()
    {
        Subscribe();

        string screen = Screens.Signature();
        if (screen != _lastScreen)
        {
            _lastScreen = screen;
            Push("screen", Screens.Describe());
        }

        var rm = RunManager.Instance;
        bool inRun = rm is not null && rm.IsInProgress;
        if (inRun != _lastInRun)
        {
            _lastInRun = inRun;
            Push(inRun ? "run_started" : "run_ended", new JsonObject());
            _lastFloor = -1;
            _lastRoom = "";
        }

        if (inRun)
        {
            var state = Safe.Get(() => rm!.DebugOnlyGetState());
            int floor = state?.TotalFloor ?? -1;
            string room = state?.CurrentRoom?.GetType().Name ?? "";
            if (floor != _lastFloor || room != _lastRoom)
            {
                _lastFloor = floor;
                _lastRoom = room;
                Push("room", new JsonObject { ["floor"] = floor, ["room"] = room });
            }
        }
    }

    private static void Subscribe()
    {
        var cm = CombatManager.Instance;
        if (cm is null || ReferenceEquals(cm, _subscribed)) return;
        _subscribed = cm;
        // These run inside the game's own code: never let one throw back into it.
        cm.CombatSetUp += s => Guard(() => Push("combat_started", new JsonObject { ["round"] = s.RoundNumber, ["enemies"] = s.Enemies.Count }));
        cm.TurnStarted += s => Guard(() => Push("turn_started", new JsonObject { ["round"] = s.RoundNumber, ["side"] = s.CurrentSide.ToString() }));
        cm.TurnEnded += s => Guard(() => Push("turn_ended", new JsonObject { ["round"] = s.RoundNumber, ["nextSide"] = s.CurrentSide.ToString() }));
        cm.CombatWon += r => Guard(() => Push("combat_won", new JsonObject { ["room"] = r?.GetType().Name }));
        cm.CombatEnded += r => Guard(() => Push("combat_ended", new JsonObject { ["room"] = r?.GetType().Name }));
    }

    private static void Guard(Action a)
    {
        try { a(); }
        catch (Exception e) { Log.Error("Event handler failed", e); }
    }
}

internal static class Safe
{
    public static T? Get<T>(Func<T> f)
    {
        try { return f(); }
        catch { return default; }
    }

    public static string Text(Func<string?> f)
    {
        try { return f() ?? ""; }
        catch { return ""; }
    }
}
