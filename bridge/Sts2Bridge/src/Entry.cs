using Godot;
using MegaCrit.Sts2.Core.Modding;
using Sts2Bridge.Protocol;

namespace Sts2Bridge;

[ModInitializer(nameof(Init))]
public static class Entry
{
    public static void Init()
    {
        var args = OS.GetCmdlineArgs().Concat(OS.GetCmdlineUserArgs()).ToList();
        int? clientId = BridgeInfo.ClientId(args);
        Log.Prefix = clientId?.ToString() ?? "-";
        Log.Path = System.IO.Path.Combine(OS.GetUserDataDir(), "sts2bridge_log.txt");

        if (!BridgeInfo.ShouldActivate(args))
        {
            Log.Info($"Inactive: start the game with {BridgeInfo.OfflineArg} (test profiles) or {BridgeInfo.ExplicitArg} to use the bridge.");
            return;
        }

        try
        {
            BridgeServer.Start(clientId, BridgeInfo.ArgValue(args, "--fastmp"));
        }
        catch (Exception ex)
        {
            Log.Error("Bridge failed to start", ex);
        }
    }
}

internal static class Log
{
    public static string Prefix = "-";
    /// <summary>Set on the main thread at startup; log lines are also written from network threads.</summary>
    public static string? Path;
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}\n{ex}");

    private static void Write(string level, string message)
    {
        string line = $"[Sts2Bridge {Prefix}] {level} {message}";
        try { GD.Print(line); } catch { /* engine shutting down */ }
        try
        {
            if (Path is null) return;
            lock (Gate) File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {line}\n");
        }
        catch { /* logging must never break the game */ }
    }
}
