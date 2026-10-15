using System.Reflection;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Nodes.Debug;
using Sts2Bridge.Protocol;

namespace Sts2Bridge;

/// <summary>
/// Runs commands through the game's own developer console (the one behind the ` key), without its window.
/// With mods loaded the game registers the full command set. In co-op, commands that change the run go through
/// the game's synchronized action queue, so every player's game applies them.
/// </summary>
internal static class DevConsoleBridge
{
    private static readonly FieldInfo? ConsoleField =
        typeof(NDevConsole).GetField("_devConsole", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    public static async Task<JsonObject> Run(string command, int timeoutMs)
    {
        command = command.Trim();
        if (command.Length == 0) throw new BridgeError("empty command");
        string verb = BridgeInfo.ConsoleVerb(command);
        if (BridgeInfo.BlockedConsoleCommands.Contains(verb))
            throw new BridgeError($"'{verb}' is blocked by the bridge (it touches Steam Cloud, online services or real achievements, or crashes the game)");

        var console = Get();
        CmdResult result = console.ProcessCommand(command);
        bool success = Field<bool>(result, "success");
        string message = Field<string>(result, "msg") ?? "";
        var task = Field<Task>(result, "task");

        bool finished = true;
        if (task is not null)
        {
            var done = await Task.WhenAny(task, Task.Delay(timeoutMs));
            finished = done == task;
            if (finished && task.IsFaulted)
            {
                success = false;
                message = $"{message} (failed: {task.Exception?.GetBaseException().Message})".Trim();
            }
        }

        GameEvents.Push("console", new JsonObject { ["command"] = command, ["success"] = success });
        return new JsonObject
        {
            ["command"] = command,
            ["success"] = success,
            ["message"] = StripMarkup(message),
            ["finished"] = finished ? null : false,
        };
    }

    private static DevConsole Get()
    {
        NDevConsole? node;
        try { node = NDevConsole.Instance; }
        catch (InvalidOperationException) { throw BridgeError.NotYet("the game's console isn't created yet; wait for the main menu"); }
        if (ConsoleField?.GetValue(node) is not DevConsole console)
            throw BridgeError.NotYet("the game's console isn't ready yet");
        return console;
    }

    private static T? Field<T>(CmdResult result, string name)
    {
        var f = typeof(CmdResult).GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return f?.GetValue(result) is T v ? v : default;
    }

    /// <summary>The console's output uses BBCode colours; tools want plain text.</summary>
    private static string StripMarkup(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\[/?[a-z]+(=[^\]]*)?\]", "");
}
