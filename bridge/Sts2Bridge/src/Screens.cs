using System.Reflection;
using System.Text.Json.Nodes;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Bridge;

/// <summary>What's on screen, which controls can be pressed, and pressing them the way a click would.</summary>
internal static class Screens
{
    private const int MaxButtons = 200;

    public static Node? GameRoot => NGame.Instance;

    public static Node? CurrentScreenNode() => Safe.Get(() => ActiveScreenContext.Instance?.GetCurrentScreen()) as Node;

    public static string Signature()
    {
        string screen = CurrentScreenNode()?.GetType().Name ?? "";
        string overlay = Safe.Get(() => NOverlayStack.Instance?.Peek())?.GetType().Name ?? "";
        string scene = Safe.Get(() => NGame.Instance?.RootSceneContainer?.CurrentScene)?.GetType().Name ?? "";
        bool map = Safe.Get(() => NMapScreen.Instance is { IsOpen: true });
        return $"{scene}|{screen}|{overlay}|{map}";
    }

    public static JsonObject Describe()
    {
        var rm = RunManager.Instance;
        var cm = CombatManager.Instance;
        var state = Safe.Get(() => rm?.DebugOnlyGetState());
        return new JsonObject
        {
            ["scene"] = Safe.Get(() => NGame.Instance?.RootSceneContainer?.CurrentScene)?.GetType().Name,
            ["screen"] = CurrentScreenNode()?.GetType().Name,
            ["overlay"] = Safe.Get(() => NOverlayStack.Instance?.Peek())?.GetType().Name,
            ["mapOpen"] = Safe.Get(() => NMapScreen.Instance is { IsOpen: true }),
            ["inRun"] = rm is not null && rm.IsInProgress,
            ["inCombat"] = cm is not null && cm.IsInProgress && !cm.IsOverOrEnding,
            ["room"] = state?.CurrentRoom?.GetType().Name,
            ["floor"] = state?.TotalFloor,
            ["netType"] = Safe.Get(() => rm?.NetService?.Type.ToString()),
        };
    }

    // ---- Buttons ----

    /// <summary>
    /// Visible, on-screen controls a player could press: the game's clickable controls and plain Godot buttons.
    /// Ids are node paths from the game root, stable while that screen is open.
    /// </summary>
    public static JsonObject Buttons(string? match, bool includeDisabled)
    {
        var root = GameRoot ?? throw BridgeError.NotYet("game not ready");
        var viewport = root.GetViewport().GetVisibleRect();
        var screen = CurrentScreenNode();
        var found = new List<(Control c, Rect2 r)>();
        Walk(root, n =>
        {
            if (n is CanvasItem hidden && !hidden.Visible) return false;   // nothing under a hidden node shows
            if (n is not Control c || n is not (NClickableControl or BaseButton)) return true;
            if (!c.IsVisibleInTree()) return false;
            var r = c.GetGlobalRect();
            if (r.Size.X <= 0 || r.Size.Y <= 0 || !viewport.Intersects(r)) return true;
            if (!includeDisabled && !IsEnabled(c)) return true;
            found.Add((c, r));
            return true;
        });

        var list = new JsonArray();
        int total = 0;
        foreach (var (c, r) in found.OrderBy(f => f.r.Position.Y).ThenBy(f => f.r.Position.X))
        {
            string id = root.GetPathTo(c).ToString();
            string text = LabelText(c);
            string type = c.GetType().Name;
            if (match is { Length: > 0 } &&
                !(id.Contains(match, StringComparison.OrdinalIgnoreCase) || text.Contains(match, StringComparison.OrdinalIgnoreCase)
                  || type.Contains(match, StringComparison.OrdinalIgnoreCase)))
                continue;
            total++;
            if (list.Count >= MaxButtons) continue;
            var center = r.GetCenter();
            list.Add(new JsonObject
            {
                ["id"] = id,
                ["type"] = type,
                ["text"] = text.Length > 0 ? text : null,
                ["enabled"] = IsEnabled(c),
                ["onScreen"] = screen is not null && (screen == c || screen.IsAncestorOf(c)) ? true : null,
                ["x"] = (int)center.X,
                ["y"] = (int)center.Y,
            });
        }
        return new JsonObject { ["count"] = total, ["shown"] = list.Count, ["buttons"] = list };
    }

    public static JsonObject Press(string id, bool force)
    {
        var node = Find(id);
        switch (node)
        {
            case NClickableControl clickable:
                if (!force && !clickable.IsEnabled) throw BridgeError.NotYet($"{id} is disabled (pass force to press anyway)");
                if (!clickable.IsVisibleInTree()) throw BridgeError.NotYet($"{id} is not visible");
                Click(clickable);
                break;
            case BaseButton button:
                if (!force && button.Disabled) throw BridgeError.NotYet($"{id} is disabled (pass force to press anyway)");
                button.EmitSignal(BaseButton.SignalName.Pressed);
                break;
            default:
                throw new BridgeError($"{id} is a {node.GetType().Name}, not a button");
        }
        GameEvents.Push("pressed", new JsonObject { ["id"] = id, ["type"] = node.GetType().Name });
        return new JsonObject { ["pressed"] = id, ["type"] = node.GetType().Name };
    }

    /// <summary>
    /// Ends the local player's turn the way the button does after its checks (the button's own click handler can stop
    /// at a first-time tutorial popup or a long-press setting instead). Pressing it again in co-op takes the end back,
    /// so this refuses when the turn is already ended unless <paramref name="undo"/> is set.
    /// </summary>
    private static readonly MethodInfo? PressHandler =
        typeof(NClickableControl).GetMethod("OnPressHandler", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, Type.EmptyTypes);
    private static readonly MethodInfo? ReleaseHandler =
        typeof(NClickableControl).GetMethod("OnReleaseHandler", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, Type.EmptyTypes);

    /// <summary>
    /// A whole mouse click: the press half (some controls act on press, e.g. character select) and then the release
    /// half (most act on release), through the same handlers the mouse events reach.
    /// </summary>
    private static void Click(NClickableControl c)
    {
        if (PressHandler is null || ReleaseHandler is null)
        {
            if (c.FocusMode != Control.FocusModeEnum.None) c.GrabFocus();
            c.ForceClick();   // release half only; better than nothing if the game renames its handlers
            return;
        }
        // A real mouse press focuses the control first (some act on focus: character select picks on focus).
        if (c.FocusMode != Control.FocusModeEnum.None) c.GrabFocus();
        PressHandler.Invoke(c, null);
        ReleaseHandler.Invoke(c, null);
    }

    public static JsonObject EndTurn(bool undo)
    {
        var root = GameRoot ?? throw BridgeError.NotYet("game not ready");
        var cm = CombatManager.Instance;
        if (cm is null || !cm.IsInProgress) throw BridgeError.NotYet("not in a fight");
        var state = RunManager.Instance?.DebugOnlyGetState() ?? throw BridgeError.NotYet("no run");
        var me = MegaCrit.Sts2.Core.Context.LocalContext.GetMe(state.Players) ?? throw new BridgeError("local player not found");

        NEndTurnButton? button = null;
        Walk(root, n =>
        {
            if (n is NEndTurnButton b && b.IsVisibleInTree()) { button = b; return false; }
            return button is null;
        });
        if (button is null) throw BridgeError.NotYet("no end turn button on screen");

        bool ready = cm.IsPlayerReadyToEndTurn(me);
        if (ready && !undo) throw new BridgeError("this player's turn is already ended (waiting for the others); pass undo to take it back");
        if (!ready && undo) throw new BridgeError("this player's turn isn't ended, nothing to undo");
        if (CanTurnBeEnded?.GetValue(button) is false)
            throw BridgeError.NotYet("the game won't end the turn right now (cards still resolving, or not this side's turn)");

        // Queues the game's end-turn action (synchronized in co-op); it takes effect a moment later.
        button.CallReleaseLogic();
        GameEvents.Push("pressed", new JsonObject { ["id"] = root.GetPathTo(button).ToString(), ["type"] = nameof(NEndTurnButton) });
        return new JsonObject
        {
            ["requested"] = undo ? "undo end turn" : "end turn",
            ["hint"] = "wait_for turn_ended (all players must end their turn in co-op)",
        };
    }

    private static readonly System.Reflection.PropertyInfo? CanTurnBeEnded =
        typeof(NEndTurnButton).GetProperty("CanTurnBeEnded", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    // ---- Tree and nodes ----

    public static JsonObject Tree(string? path, int depth, int maxChildren)
    {
        var node = string.IsNullOrEmpty(path) ? GameRoot ?? throw BridgeError.NotYet("game not ready") : Find(path);
        return TreeNode(node, Math.Clamp(depth, 0, 8), Math.Clamp(maxChildren, 1, 200));
    }

    private static JsonObject TreeNode(Node n, int depth, int maxChildren)
    {
        var o = new JsonObject { ["name"] = n.Name.ToString(), ["type"] = n.GetType().Name };
        if (n is CanvasItem ci && !ci.Visible) o["visible"] = false;
        string text = OwnText(n);
        if (text.Length > 0) o["text"] = text;
        int count = n.GetChildCount();
        if (count == 0) return o;
        if (depth <= 0) { o["children"] = count; return o; }
        var kids = new JsonArray();
        foreach (var child in n.GetChildren().Take(maxChildren)) kids.Add(TreeNode(child, depth - 1, maxChildren));
        o["children"] = kids;
        if (count > maxChildren) o["moreChildren"] = count - maxChildren;
        return o;
    }

    /// <summary>A node's Godot basics plus its C# properties (and any named fields, public or not).</summary>
    public static JsonObject NodeInfo(string path, IReadOnlyList<string> members)
    {
        var n = Find(path);
        var o = new JsonObject
        {
            ["path"] = (GameRoot?.GetPathTo(n) ?? n.GetPath()).ToString(),
            ["type"] = n.GetType().FullName,
            ["children"] = n.GetChildCount(),
        };
        if (n is Control c)
        {
            o["visible"] = c.IsVisibleInTree();
            var r = c.GetGlobalRect();
            o["rect"] = new JsonObject { ["x"] = r.Position.X, ["y"] = r.Position.Y, ["w"] = r.Size.X, ["h"] = r.Size.Y };
        }
        string text = OwnText(n);
        if (text.Length > 0) o["text"] = text;

        var props = new JsonObject();
        foreach (var p in n.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.DeclaringType?.Namespace?.StartsWith("MegaCrit") != true || p.GetIndexParameters().Length > 0) continue;
            props[p.Name] = Short(Safe.Get(() => p.GetValue(n)));
        }
        o["properties"] = props;

        if (members.Count > 0)
        {
            var extra = new JsonObject();
            foreach (string m in members)
            {
                const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                object? value = null;
                bool found = false;
                for (var t = n.GetType(); t is not null && !found; t = t.BaseType)
                {
                    if (t.GetField(m, all | BindingFlags.DeclaredOnly) is { } f) { value = Safe.Get(() => f.GetValue(n)); found = true; }
                    else if (t.GetProperty(m, all | BindingFlags.DeclaredOnly) is { } p && p.GetIndexParameters().Length == 0) { value = Safe.Get(() => p.GetValue(n)); found = true; }
                }
                extra[m] = found ? Short(value) : "(no such field or property)";
            }
            o["members"] = extra;
        }
        return o;
    }

    // ---- Screenshot ----

    public static JsonObject Screenshot(int maxWidth, string format)
    {
        var root = GameRoot ?? throw BridgeError.NotYet("game not ready");
        var image = root.GetViewport().GetTexture().GetImage();
        int w = image.GetWidth(), h = image.GetHeight();
        if (maxWidth > 0 && w > maxWidth)
        {
            image.Resize(maxWidth, Math.Max(1, h * maxWidth / w), Image.Interpolation.Bilinear);
            w = image.GetWidth();
            h = image.GetHeight();
        }
        bool jpg = format.Equals("jpg", StringComparison.OrdinalIgnoreCase) || format.Equals("jpeg", StringComparison.OrdinalIgnoreCase);
        byte[] bytes = jpg ? image.SaveJpgToBuffer(0.85f) : image.SavePngToBuffer();
        return new JsonObject
        {
            ["mime"] = jpg ? "image/jpeg" : "image/png",
            ["width"] = w,
            ["height"] = h,
            ["data"] = Convert.ToBase64String(bytes),
        };
    }

    // ---- Helpers ----

    public static Node Find(string id)
    {
        var root = GameRoot ?? throw BridgeError.NotYet("game not ready");
        var node = id.StartsWith("/") ? root.GetTree().Root.GetNodeOrNull(id) : root.GetNodeOrNull(id);
        return node ?? throw BridgeError.NotYet($"no node at {id} (the screen may have changed; list buttons again)");
    }

    /// <summary>Depth-first walk; return false from <paramref name="visit"/> to skip a node's children.</summary>
    public static void Walk(Node root, Func<Node, bool> visit)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!visit(n)) continue;
            int count = n.GetChildCount();
            for (int i = count - 1; i >= 0; i--) stack.Push(n.GetChild(i));
        }
    }

    private static bool IsEnabled(Control c) => c switch
    {
        NClickableControl k => k.IsEnabled,
        BaseButton b => !b.Disabled,
        _ => true,
    };

    private static string OwnText(Node n) => n switch
    {
        RichTextLabel r => Clip(Safe.Text(() => r.GetParsedText())),
        Label l => Clip(l.Text),
        BaseButton b when !string.IsNullOrEmpty(b is Button bt ? bt.Text : "") => Clip(((Button)b).Text),
        _ => "",
    };

    /// <summary>The first text found inside a control (its own or a label's within a few levels).</summary>
    private static string LabelText(Node n)
    {
        string own = OwnText(n);
        if (own.Length > 0) return own;
        string found = "";
        int budget = 60;
        Walk(n, x =>
        {
            if (found.Length > 0 || budget-- <= 0) return false;
            if (x != n && x is CanvasItem ci && !ci.Visible) return false;
            string t = OwnText(x);
            if (t.Length > 0) { found = t; return false; }
            return true;
        });
        return found;
    }

    private static string Clip(string? s)
    {
        s = (s ?? "").Replace('\n', ' ').Trim();
        return s.Length > 80 ? s[..80] + "…" : s;
    }

    private static string? Short(object? v)
    {
        if (v is null) return null;
        string s = v switch
        {
            Node node => $"{node.GetType().Name} {node.Name}",
            System.Collections.ICollection col => $"{v.GetType().Name} (count {col.Count})",
            _ => v.ToString() ?? "",
        };
        return s.Length > 200 ? s[..200] + "…" : s;
    }
}
