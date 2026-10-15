using System.Text.Json.Nodes;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using Sts2Bridge.Protocol;

namespace Sts2Bridge;

/// <summary>
/// The pieces plans are built from: a compact look at the game, game speed, an in-game banner for hand-offs,
/// pressing a button by its text, and playing a card by its name.
/// </summary>
internal static class Play
{
    // ---- Speed ----

    /// <summary>Gets or sets this copy's game speed (the game's own fast-mode setting; local, not synchronized).</summary>
    public static JsonObject Speed(string? set)
    {
        var prefs = SaveManager.Instance?.PrefsSave ?? throw BridgeError.NotYet("settings not loaded yet");
        if (set is { Length: > 0 })
        {
            prefs.FastMode = set.ToLowerInvariant() switch
            {
                "normal" => FastModeType.Normal,
                "fast" => FastModeType.Fast,
                "instant" => FastModeType.Instant,
                _ => throw new BridgeError($"speed must be one of {string.Join(", ", BridgeInfo.Speeds)}"),
            };
            GameEvents.Push("speed", new JsonObject { ["speed"] = prefs.FastMode.ToString().ToLowerInvariant() });
        }
        return new JsonObject { ["speed"] = prefs.FastMode.ToString().ToLowerInvariant() };
    }

    // ---- Look ----

    /// <summary>
    /// Everything an agent usually needs to decide the next step, small: where we are, my player, the others,
    /// my hand and the enemies in a fight, and the buttons on the current screen.
    /// </summary>
    public static JsonObject Look()
    {
        var o = new JsonObject();
        var d = Screens.Describe();
        o["screen"] = d["screen"]?.DeepClone();
        if (d["overlay"] is { } ov && ov.ToString() != d["screen"]?.ToString()) o["overlay"] = ov.DeepClone();
        if (d["mapOpen"]?.GetValue<bool>() == true) o["mapOpen"] = true;
        o["speed"] = Safe.Get(() => SaveManager.Instance?.PrefsSave?.FastMode.ToString().ToLowerInvariant());

        var rm = RunManager.Instance;
        var state = Safe.Get(() => rm?.DebugOnlyGetState());
        if (rm is not null && rm.IsInProgress && state is not null)
        {
            o["floor"] = state.TotalFloor;
            o["room"] = state.CurrentRoom?.GetType().Name;
            var me = Safe.Get(() => LocalContext.GetMe(state.Players));
            var others = new JsonArray();
            int i = 0;
            foreach (var p in state.Players)
            {
                var po = new JsonObject
                {
                    ["i"] = i++,
                    ["character"] = Safe.Text(() => p.Character.Title.GetFormattedText()),
                    ["hp"] = Safe.Get(() => p.Creature.CurrentHp),
                    ["maxHp"] = Safe.Get(() => p.Creature.MaxHp),
                    ["gold"] = p.Gold,
                    ["deck"] = Safe.Get(() => p.Deck.Cards.Count),
                };
                if (ReferenceEquals(p, me))
                {
                    if (p.PlayerCombatState is { } pcs && CombatManager.Instance is { IsInProgress: true })
                    {
                        po["energy"] = pcs.Energy;
                        po["block"] = Safe.Get(() => p.Creature.Block);
                    }
                    o["me"] = po;
                }
                else others.Add(po);
            }
            if (others.Count > 0) o["others"] = others;

            var cm = CombatManager.Instance;
            if (cm is not null && cm.IsInProgress && me is not null)
            {
                o["side"] = Safe.Text(() => (me.Creature.CombatState as CombatState)?.CurrentSide.ToString());
                o["hand"] = new JsonArray(Hand(me).Select((c, idx) => (JsonNode)CardBrief(c, idx)).ToArray());
                var cs = me.Creature.CombatState;
                var targets = Safe.Get(() => cs!.PlayerCreatures) ?? new List<Creature>();
                if (cs is not null)
                    o["enemies"] = new JsonArray(GameState.Enemies(cs).Select((e, idx) => (JsonNode)EnemyBrief(e, idx, targets)).ToArray());
            }
        }

        o["buttons"] = ScreenButtons();
        return o;
    }

    private static IReadOnlyList<CardModel> Hand(MegaCrit.Sts2.Core.Entities.Players.Player me) =>
        Safe.Get(() => me.PlayerCombatState!.Hand.Cards) ?? (IReadOnlyList<CardModel>)Array.Empty<CardModel>();

    private static JsonObject CardBrief(CardModel c, int index)
    {
        var o = new JsonObject
        {
            ["i"] = index,
            ["name"] = Safe.Text(() => c.Title) + (c.CurrentUpgradeLevel > 0 ? "+" : ""),
            ["cost"] = Safe.Get(() => c.EnergyCost.CostsX) ? "X" : Safe.Get(() => c.EnergyCost.GetResolved()).ToString(),
        };
        if (!Safe.Get(() => c.CanPlay())) o["playable"] = false;
        string target = Safe.Text(() => c.TargetType.ToString());
        if (target.Contains("Enemy")) o["target"] = "enemy";
        else if (target.Contains("Ally")) o["target"] = "ally";
        return o;
    }

    private static JsonObject EnemyBrief(Creature e, int index, IReadOnlyList<Creature> targets)
    {
        var o = new JsonObject
        {
            ["i"] = index,
            ["name"] = Safe.Text(() => e.Name),
            ["hp"] = e.CurrentHp,
        };
        if (e.Block > 0) o["block"] = e.Block;
        if (!e.IsAlive) { o["dead"] = true; return o; }
        var parts = new List<string>();
        foreach (var intent in Safe.Get(() => e.Monster?.NextMove?.Intents) ?? [])
        {
            if (intent is AttackIntent a)
            {
                int dmg = Safe.Get(() => a.GetSingleDamage(targets, e));
                int hits = Safe.Get(() => a.Repeats);
                parts.Add(hits > 1 ? $"attack {dmg}x{hits}" : $"attack {dmg}");
            }
            else parts.Add(intent.IntentType.ToString().ToLowerInvariant());
        }
        if (parts.Count > 0) o["intent"] = string.Join(" + ", parts);
        return o;
    }

    /// <summary>Enabled buttons on the current screen (or, if none are, any visible ones), text and id only.</summary>
    private static JsonArray ScreenButtons()
    {
        // Hand cards are listed under "hand" (play them with play); their hitboxes would only be noise here.
        var all = new JsonArray(Screens.Buttons(null, includeDisabled: false)["buttons"]!.AsArray()
            .Where(b => !(b?["id"]?.ToString() ?? "").Contains("/Hand/CardHolderContainer/"))
            .Select(b => b!.DeepClone()).ToArray());
        var onScreen = all.Where(b => b?["onScreen"] is not null).ToList();
        var pick = (onScreen.Count > 0 ? onScreen : all.ToList()).Take(25);
        return new JsonArray(pick.Select(b => (JsonNode)new JsonObject
        {
            ["text"] = b!["text"]?.DeepClone() ?? Short(b["id"]!.ToString()),
            ["id"] = b["id"]!.DeepClone(),
        }).ToArray());
    }

    private static string Short(string id) => id[(id.LastIndexOf('/') + 1)..];

    // ---- Press by text ----

    /// <summary>
    /// Finds a button by what a person would call it: its text (exact, then contained), then its node name or type.
    /// Buttons on the current screen win over ones behind a popup.
    /// </summary>
    public static string ResolveButton(string query)
    {
        if (query.Contains('/')) return query;   // already an id
        var all = Screens.Buttons(null, includeDisabled: true)["buttons"]!.AsArray().Select(b => b!.AsObject()).ToList();
        if (all.Count == 0) throw BridgeError.NotYet("no buttons on screen yet");

        static int Score(JsonObject b, string q)
        {
            string text = b["text"]?.ToString() ?? "";
            string id = b["id"]!.ToString();
            string name = id[(id.LastIndexOf('/') + 1)..];
            string type = b["type"]?.ToString() ?? "";
            int s =
                text.Equals(q, StringComparison.OrdinalIgnoreCase) ? 100 :
                name.Equals(q, StringComparison.OrdinalIgnoreCase) ? 90 :
                name.Equals(q + "_button", StringComparison.OrdinalIgnoreCase) || name.Equals(q + "Button", StringComparison.OrdinalIgnoreCase) ? 85 :
                text.Contains(q, StringComparison.OrdinalIgnoreCase) ? 60 :
                name.Contains(q, StringComparison.OrdinalIgnoreCase) ? 50 :
                type.Contains(q, StringComparison.OrdinalIgnoreCase) ? 40 : 0;
            if (s == 0) return 0;
            if (b["onScreen"] is not null) s += 20;
            if (b["enabled"]?.GetValue<bool>() == true) s += 10;
            return s;
        }

        var best = all.Select(b => (b, s: Score(b, query))).Where(x => x.s > 0).OrderByDescending(x => x.s).FirstOrDefault();
        if (best.b is null)
            throw BridgeError.NotYet($"no button matching '{query}' on screen. Showing: {string.Join(", ", all.Take(12).Select(b => b["text"]?.ToString() ?? Short(b["id"]!.ToString())))}");
        return best.b["id"]!.ToString();
    }

    // ---- Play by name ----

    /// <summary>Index in the hand of the first card named <paramref name="name"/> (title or id), preferring a playable one.</summary>
    public static int ResolveCard(string name)
    {
        var rm = RunManager.Instance;
        var state = Safe.Get(() => rm?.DebugOnlyGetState()) ?? throw BridgeError.NotYet("no run");
        var me = LocalContext.GetMe(state.Players) ?? throw BridgeError.NotYet("local player not found");
        var hand = Hand(me);
        string q = name.Trim().TrimEnd('+');
        var matches = hand.Select((c, i) => (c, i))
            .Where(x => Safe.Text(() => x.c.Title).Equals(q, StringComparison.OrdinalIgnoreCase)
                        || Safe.Text(() => x.c.Id.Entry).Equals(q.Replace(' ', '_'), StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
            throw BridgeError.NotYet($"no '{name}' in hand. Hand: {string.Join(", ", hand.Select(c => Safe.Text(() => c.Title)))}");
        return matches.FirstOrDefault(x => Safe.Get(() => x.c.CanPlay())).c is not null
            ? matches.First(x => Safe.Get(() => x.c.CanPlay())).i
            : matches[0].i;
    }

    /// <summary>Index of the first living enemy whose name contains <paramref name="name"/>.</summary>
    public static int ResolveEnemy(string name)
    {
        var state = Safe.Get(() => RunManager.Instance?.DebugOnlyGetState()) ?? throw BridgeError.NotYet("no run");
        var me = LocalContext.GetMe(state.Players) ?? throw BridgeError.NotYet("local player not found");
        var cs = me.Creature.CombatState ?? throw BridgeError.NotYet("not in a fight");
        var enemies = GameState.Enemies(cs);
        for (int i = 0; i < enemies.Count; i++)
            if (enemies[i].IsAlive && Safe.Text(() => enemies[i].Name).Contains(name, StringComparison.OrdinalIgnoreCase)) return i;
        throw BridgeError.NotYet($"no living enemy named like '{name}'. Enemies: {string.Join(", ", enemies.Select(e => Safe.Text(() => e.Name)))}");
    }

    // ---- Banner ----

    private static CanvasLayer? _banner;

    /// <summary>Shows a message across the top of this game window (a hand-off to the person playing), or clears it.</summary>
    public static JsonObject Banner(string? text)
    {
        if (_banner is not null && GodotObject.IsInstanceValid(_banner)) _banner.QueueFree();
        _banner = null;
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject { ["banner"] = null };

        var root = NGame.Instance ?? throw BridgeError.NotYet("game not ready");
        var layer = new CanvasLayer { Layer = 120, Name = "Sts2BridgeBanner" };
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        var style = new StyleBoxFlat { BgColor = new Color(0.05f, 0.08f, 0.1f, 0.88f), BorderColor = new Color(0.937f, 0.784f, 0.318f) };
        style.SetBorderWidthAll(3);
        style.SetCornerRadiusAll(10);
        style.SetContentMarginAll(18);
        panel.AddThemeStyleboxOverride("panel", style);
        var label = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(700, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", 30);
        label.AddThemeColorOverride("font_color", new Color(0.937f, 0.784f, 0.318f));
        if (ResourceLoader.Exists("res://themes/kreon_bold_glyph_space_two.tres"))
            label.AddThemeFontOverride("font", GD.Load<Font>("res://themes/kreon_bold_glyph_space_two.tres"));
        panel.AddChild(label);
        layer.AddChild(panel);
        root.AddChild(layer);
        var size = root.GetViewport().GetVisibleRect().Size;
        panel.Position = new Vector2((size.X - 760) / 2, 24);
        panel.CustomMinimumSize = new Vector2(760, 0);
        _banner = layer;
        return new JsonObject { ["banner"] = text };
    }
}
