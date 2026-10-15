using System.Text.Json.Nodes;
using Sts2Bridge.Protocol;

namespace Sts2Bridge;

/// <summary>Every command the bridge understands. Runs on the main thread.</summary>
internal static class Commands
{
    public static readonly string[] Names =
    [
        "ping", "state", "screen", "buttons", "press", "end_turn", "play_card",
        "console", "events", "tree", "node", "screenshot", "info", "look", "speed", "banner",
    ];

    public static async Task<JsonNode?> Handle(string cmd, JsonObject? a)
    {
        switch (cmd)
        {
            case "ping":
                return new JsonObject { ["pong"] = true, ["frame"] = (long)Godot.Engine.GetProcessFrames() };
            case "info":
                return JsonNode.Parse(Wire.Encode(BridgeServer.Info with { Token = "" }));
            case "state":
                return GameState.Snapshot(full: Args.Str(a, "detail") is "full");
            case "screen":
                return Screens.Describe();
            case "buttons":
                return Screens.Buttons(Args.Str(a, "match"), Args.Bool(a, "includeDisabled"));
            case "press":
            {
                // id (exact, from buttons) or text (what the button says, or its name)
                string id = Args.Str(a, "id") is { Length: > 0 } given ? given
                    : Args.Str(a, "text") is { Length: > 0 } text ? Play.ResolveButton(text)
                    : throw new BridgeError("press needs id (from buttons) or text");
                return Screens.Press(id, Args.Bool(a, "force"));
            }
            case "end_turn":
                return Screens.EndTurn(Args.Bool(a, "undo"));
            case "play_card":
            {
                int hand = Args.Int(a, "handIndex")
                           ?? (Args.Str(a, "card") is { Length: > 0 } card ? Play.ResolveCard(card) : throw new BridgeError("play_card needs handIndex or card"));
                int? target = Args.Int(a, "target")
                              ?? (Args.Str(a, "targetName") is { Length: > 0 } tn ? Play.ResolveEnemy(tn) : null);
                return GameState.PlayCard(hand, target, Args.Int(a, "ally"));
            }
            case "console":
                return await DevConsoleBridge.Run(
                    Args.Str(a, "command") ?? throw new BridgeError("console needs command"),
                    Math.Clamp(Args.Int(a, "timeoutMs") ?? 10_000, 100, 60_000));
            case "events":
                return GameEvents.Since(Args.Int(a, "since") ?? 0, Math.Clamp(Args.Int(a, "max") ?? 100, 1, 500));
            case "tree":
                return Screens.Tree(Args.Str(a, "path"), Args.Int(a, "depth") ?? 2, Args.Int(a, "maxChildren") ?? 40);
            case "node":
                return Screens.NodeInfo(Args.Str(a, "path") ?? throw new BridgeError("node needs path"), Args.StrList(a, "members"));
            case "look":
                return Play.Look();
            case "speed":
                return Play.Speed(Args.Str(a, "set"));
            case "banner":
                return Play.Banner(Args.Str(a, "text"));
            case "screenshot":
                return Screens.Screenshot(Args.Int(a, "maxWidth") ?? 1280, Args.Str(a, "format") ?? "png");
            default:
                throw new BridgeError($"unknown command '{cmd}'. Commands: {string.Join(", ", Names)}");
        }
    }
}
