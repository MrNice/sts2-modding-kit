using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Sts2Bridge.Protocol;

namespace Sts2Mcp;

/// <summary>The MCP tools. Each returns JSON text (or an image) for the agent.</summary>
[McpServerToolType]
public sealed class Tools(GamePaths paths, InstanceRegistry registry, Launcher launcher)
{
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private const string PlayerHelp = "Which game copy: omit for the host (or the only copy), 'client' for the first joiner, or a client id like '1002'.";

    // ---- Session ----

    [McpServerTool(Name = "doctor", ReadOnly = false, Destructive = false)]
    [Description("Checks the setup: game install and version, .NET, the bridge build, test profiles, running copies. Run this first if anything fails.")]
    public async Task<string> Doctor(CancellationToken ct) => Json(await Diagnostics.RunAsync(paths, registry, buildBridge: true, ct));

    [McpServerTool(Name = "launch", Destructive = true)]
    [Description("Starts local test copies of Slay the Spire 2 on offline profiles (no Steam): 1 = one game at the main menu; 2-4 = a co-op host plus joiners, all in the co-op lobby. " +
                 "Copies the bridge and the given mods into the game's mods folder for the session (a folder there with the same name is moved aside and restored by stop) and turns mods on in the test profiles. Takes 30-90 s. Call stop when done.")]
    public async Task<string> Launch(
        [Description("Number of game copies, 1-4. Default 2 (host + one client).")] int players = 2,
        [Description("Folders holding built mods to test, each with <Id>.dll and <Id>.json (e.g. the folder your build writes to).")] string[]? mods = null,
        [Description("How this session will be used: 'check' (default; instant speed, the agent does everything), 'stage' (instant speed; plans hand the moment that matters to the person with a handoff step) or 'watch' (normal speed, so a person can follow along).")] string mode = "check",
        [Description("Start even if the game is already running (not recommended).")] bool allow_running = false,
        [Description("Keep a co-op run saved in the host's test profile instead of abandoning it (each session leaves one; by default it's abandoned so a new lobby can open).")] bool keep_saved_run = false,
        CancellationToken ct = default)
    {
        string speed = ModeSpeed(mode);
        var steps = new List<string>();
        var result = await launcher.LaunchAsync(players, mods ?? [], allow_running, s => { steps.Add(s); return Task.CompletedTask; }, ct, keep_saved_run);
        foreach (var c in registry.All())
            try { await c.CallAsync("speed", new JsonObject { ["set"] = speed }, Quick, ct); } catch (ToolError) { }
        result["mode"] = $"{mode}: speed {speed} on every copy";
        result["steps"] = new JsonArray(steps.Select(s => (JsonNode)s).ToArray());
        return Json(result);
    }

    private static string ModeSpeed(string mode) => mode.ToLowerInvariant() switch
    {
        "check" or "stage" => "instant",
        "watch" => "normal",
        _ => throw new ToolError("mode must be check, stage or watch"),
    };

    // ---- The everyday tools: look, plan, speed, start_run, goto ----

    [McpServerTool(Name = "look", ReadOnly = true)]
    [Description("A compact view of one copy: screen, speed, floor, my player (hp, gold, energy), the others, my hand with indexes, enemies with intents, and the buttons on the current screen with their text. Use this to decide the next plan.")]
    public async Task<string> Look([Description(PlayerHelp)] string? player = null, CancellationToken ct = default) =>
        Json(await registry.Resolve(player).CallAsync("look", null, Quick, ct));

    [McpServerTool(Name = "speed", Destructive = false)]
    [Description("Sets the game's animation speed: 'instant' (fastest; best for checking logic), 'fast', or 'normal' (to see animations and effects as players will). Every copy by default; each copy has its own setting.")]
    public async Task<string> Speed(
        [Description("instant, fast or normal")] string speed,
        [Description(PlayerHelp + " Omit to set every copy.")] string? player = null,
        CancellationToken ct = default)
    {
        var targets = player is null ? registry.All() : [registry.Resolve(player)];
        if (targets.Count == 0) throw new ToolError("no game copy is running");
        var done = new JsonArray();
        foreach (var c in targets)
            done.Add(new JsonObject { ["clientId"] = c.Info.ClientId, ["speed"] = (await c.CallAsync("speed", new JsonObject { ["set"] = speed }, Quick, ct))?["speed"]?.DeepClone() });
        return Json(done);
    }

    [McpServerTool(Name = "plan", Destructive = true)]
    [Description("""
        Runs a list of steps back to back in the game, without a round trip to you between them. Each action waits until it
        can happen (button there and enabled, card playable, your turn) up to its timeout, so write steps the way a person
        would act. Stops at the first failed step and reports where and why, with a look and a screenshot. Ends with a look.

        Steps (one action each; any step also takes "player" (host/client/1002), "timeout" seconds, "optional": true):
          {"press": "Proceed"}                     button by its text or name (or an id from buttons)
          {"play": "Bash", "target": 0}            card by name or hand index; target = enemy index or name; "ally": player index
          {"end_turn": true}                       then usually {"wait": "my_turn"}
          {"console": "room Shop"}                 dev console (synchronized in co-op: run once)
          {"wait": "my_turn"}                      event: my_turn, turn_started, turn_ended, combat_won, fight_over, room,
                                                   screen, run_started; or "idle"; or seconds (1.5); or {"screen": "Merchant"}
          {"expect": {"me.gold": ">=100", "enemies.0.hp": "<40", "screen": "~Rewards"}}
                                                   paths into look; values: number/text, or >= <= > < != ~(contains) !~
          {"look": true}  {"screenshot": "caption"}  {"speed": "normal"}  {"banner": "text"}
          {"handoff": "Play Bash on the Cultist", "until": "turn_ended"}
                                                   gives the next action to the person: normal speed, the message on screen in
                                                   that window, waits (until: event type, {"screen": ...}, checks like expect,
                                                   or by default any change), lets it play out ("settle" s, "frames" screenshots),
                                                   then restores speed. Tell the person what's coming before you run it.
        """)]
    public async Task<CallToolResult> Plan(
        [Description("The steps, as a JSON array of objects.")] JsonElement steps,
        [Description(PlayerHelp + " Default for steps without their own player.")] string? player = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default)
    {
        JsonArray list = steps.ValueKind switch
        {
            JsonValueKind.Array => JsonNode.Parse(steps.GetRawText())!.AsArray(),
            JsonValueKind.String => JsonNode.Parse(steps.GetString()!) as JsonArray ?? throw new ToolError("steps must be a JSON array"),
            _ => throw new ToolError("steps must be a JSON array of step objects"),
        };
        if (list.Count == 0) throw new ToolError("the plan has no steps");
        if (list.Count > 200) throw new ToolError("at most 200 steps per plan");
        var outcome = await new PlanRunner(registry).RunAsync(list, player, progress, ct);
        return Result(outcome);
    }

    [McpServerTool(Name = "start_run", Destructive = true)]
    [Description("From the co-op lobby (right after launch): each copy picks its character, everyone readies up (clients first, then the host) and the run starts. Characters in copy order, host first: IRONCLAD, SILENT, DEFECT, REGENT, NECROBINDER. Solo: from the main menu, use plan with presses instead.")]
    public async Task<CallToolResult> StartRun(
        [Description("Characters for host, client, ... (default IRONCLAD, SILENT, DEFECT, REGENT).")] string[]? characters = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default)
    {
        var copies = registry.List();
        if (copies.Count == 0) throw new ToolError("no game copy is running; launch first");
        var picks = characters is { Length: > 0 } ? characters : ["IRONCLAD", "SILENT", "DEFECT", "REGENT"];
        var steps = new JsonArray();
        for (int k = 0; k < copies.Count; k++)
            steps.Add(new JsonObject { ["press"] = $"{picks[Math.Min(k, picks.Length - 1)].ToUpperInvariant()}_button", ["player"] = Who(copies[k]), ["timeout"] = 30 });
        foreach (var c in copies.Skip(1).Append(copies[0]))
            steps.Add(new JsonObject { ["press"] = "ConfirmButton", ["player"] = Who(c) });
        steps.Add(new JsonObject { ["wait"] = "run_started", ["player"] = Who(copies[0]) });
        steps.Add(new JsonObject { ["wait"] = "idle", ["player"] = Who(copies[0]) });
        return Result(await new PlanRunner(registry).RunAsync(steps, Who(copies[0]), progress, ct));
    }

    [McpServerTool(Name = "goto", Destructive = true)]
    [Description("Jumps the run to a place with the dev console and waits until it's ready (in a fight: until your turn): shop, rest, treasure, event, monster, elite, boss, or 'fight <ENCOUNTER_ID>', 'event <EVENT_ID>', 'act <n>'. Synchronized in co-op.")]
    public async Task<CallToolResult> Goto(
        [Description("shop, rest, treasure, event, monster, elite, boss, or a console command such as 'fight <ID>'.")] string place,
        [Description(PlayerHelp)] string? player = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default)
    {
        string p = place.Trim();
        string command = p.ToLowerInvariant() switch
        {
            "shop" or "merchant" => "room Shop",
            "rest" or "restsite" or "campfire" => "room RestSite",
            "treasure" or "chest" => "room Treasure",
            "event" => "room Event",
            "monster" or "fight" or "combat" => "room Monster",
            "elite" => "room Elite",
            "boss" => "room Boss",
            _ => p,
        };
        bool fight = command.StartsWith("fight", StringComparison.OrdinalIgnoreCase) || command is "room Monster" or "room Elite" or "room Boss";
        var steps = new JsonArray
        {
            new JsonObject { ["console"] = command },
            new JsonObject { ["wait"] = "room" },
            fight ? new JsonObject { ["wait"] = "my_turn" } : new JsonObject { ["wait"] = "idle" },
        };
        return Result(await new PlanRunner(registry).RunAsync(steps, player, progress, ct));
    }

    private static string Who(InstanceInfo i) => i.ClientId?.ToString() ?? "host";

    private static CallToolResult Result(PlanRunner.Outcome outcome)
    {
        var content = new List<ContentBlock> { new TextContentBlock { Text = outcome.Report.ToJsonString(Pretty) } };
        foreach (var img in outcome.Images)
        {
            content.Add(new TextContentBlock { Text = img.Caption });
            content.Add(ImageContentBlock.FromBytes(img.Data, img.Mime));
        }
        return new CallToolResult { Content = content, IsError = outcome.Report["ok"]?.GetValue<bool>() == false ? true : null };
    }

    [McpServerTool(Name = "stop", Destructive = true)]
    [Description("Closes the test copies and removes the session's mods from the game's mods folder, restoring anything that was there before.")]
    public async Task<string> Stop(CancellationToken ct) => Json(await launcher.StopAsync(ct));

    [McpServerTool(Name = "relaunch", Destructive = true)]
    [Description("Closes one test copy and starts it again with the same profile and role, e.g. a client that failed to join the lobby (it rejoins the host).")]
    public async Task<string> Relaunch([Description(PlayerHelp)] string? player = null, CancellationToken ct = default) =>
        Json(await launcher.RelaunchAsync(player, ct));

    [McpServerTool(Name = "instances", ReadOnly = true)]
    [Description("Lists running game copies that have the bridge (client id, host/join, pid, game version).")]
    public string Instances() => Json(new JsonArray(registry.List().Select(i => (JsonNode)new JsonObject
    {
        ["clientId"] = i.ClientId,
        ["role"] = i.FastMp ?? "solo",
        ["pid"] = i.Pid,
        ["port"] = i.Port,
        ["game"] = i.GameVersion,
        ["bridge"] = i.BridgeVersion,
    }).ToArray()));

    // ---- Reading ----

    [McpServerTool(Name = "state", ReadOnly = true)]
    [Description("The run as one game copy sees it: screen, floor, players (hp, gold, relics, potions, deck size), and in a fight the hand (with indexes for play_card), energy, piles and enemies with intents. " +
                 "detail='full' adds card lists. player='all' returns every copy, to check they agree.")]
    public async Task<string> State(
        [Description(PlayerHelp + " Or 'all'.")] string? player = null,
        [Description("'summary' (default) or 'full'.")] string detail = "summary",
        CancellationToken ct = default)
    {
        var args = new JsonObject { ["detail"] = detail };
        if (string.Equals(player, "all", StringComparison.OrdinalIgnoreCase))
        {
            var all = new JsonArray();
            foreach (var c in registry.All())
                all.Add(new JsonObject { ["clientId"] = c.Info.ClientId, ["state"] = await c.CallAsync("state", args.DeepClone().AsObject(), Quick, ct) });
            return Json(all);
        }
        return Json(await registry.Resolve(player).CallAsync("state", args, Quick, ct));
    }

    [McpServerTool(Name = "screen", ReadOnly = true)]
    [Description("What's on screen in one copy: scene, screen and overlay class names, whether the map is open, room and floor.")]
    public async Task<string> Screen([Description(PlayerHelp)] string? player = null, CancellationToken ct = default) =>
        Json(await registry.Resolve(player).CallAsync("screen", null, Quick, ct));

    [McpServerTool(Name = "buttons", ReadOnly = true)]
    [Description("Visible controls that can be pressed right now, with ids for press, their text and screen position. onScreen=true marks controls on the current screen; when a popup is open, prefer those (controls behind it are listed too). Use match to filter by text, type or id.")]
    public async Task<string> Buttons(
        [Description(PlayerHelp)] string? player = null,
        [Description("Only controls whose text, type or id contains this.")] string? match = null,
        [Description("Include disabled controls.")] bool include_disabled = false,
        CancellationToken ct = default) =>
        Json(await registry.Resolve(player).CallAsync("buttons", new JsonObject { ["match"] = match, ["includeDisabled"] = include_disabled }, Quick, ct));

    [McpServerTool(Name = "screenshot", ReadOnly = true)]
    [Description("A picture of one game copy's window.")]
    public async Task<CallToolResult> Screenshot(
        [Description(PlayerHelp)] string? player = null,
        [Description("Scale down to at most this width in pixels (default 1280).")] int max_width = 1280,
        CancellationToken ct = default)
    {
        var shot = await registry.Resolve(player).CallAsync("screenshot", new JsonObject { ["maxWidth"] = max_width, ["format"] = "png" }, TimeSpan.FromSeconds(20), ct) as JsonObject
                   ?? throw new ToolError("no screenshot returned");
        byte[] bytes = Convert.FromBase64String(shot["data"]?.ToString() ?? "");
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = $"{shot["width"]}x{shot["height"]}" },
                ImageContentBlock.FromBytes(bytes, shot["mime"]?.ToString() ?? "image/png"),
            ],
        };
    }

    [McpServerTool(Name = "events", ReadOnly = true)]
    [Description("Recent game events from one copy, oldest first: screen changes, rooms, run start/end, combat start, turns, wins, presses, cards played, console commands. Pass since=<next from the last call> to get only new ones.")]
    public async Task<string> Events(
        [Description(PlayerHelp)] string? player = null,
        [Description("Only events with seq >= this.")] long since = 0,
        CancellationToken ct = default) =>
        Json(await registry.Resolve(player).CallAsync("events", new JsonObject { ["since"] = since, ["max"] = 200 }, Quick, ct));

    [McpServerTool(Name = "wait_for", ReadOnly = true)]
    [Description("Waits until an event happens in one copy (e.g. turn_started, combat_won, room, screen, run_started), optionally with text in its data (e.g. a screen class name). Only counts events after the call starts unless since is given.")]
    public async Task<string> WaitFor(
        [Description("Event type to wait for, e.g. screen, room, turn_started, combat_won, run_started. Omit to match any type.")] string? type = null,
        [Description("Text that must appear in the event's data, e.g. NMerchantRoom or RestSiteRoom.")] string? contains = null,
        [Description(PlayerHelp)] string? player = null,
        [Description("Seconds to wait (default 30, max 300).")] int timeout_seconds = 30,
        [Description("Start from this event seq instead of now.")] long? since = null,
        CancellationToken ct = default)
    {
        var client = registry.Resolve(player);
        long from = since ?? (long)((await client.CallAsync("events", new JsonObject { ["since"] = long.MaxValue }, Quick, ct))?["next"] ?? 0);
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Clamp(timeout_seconds, 1, 300));
        while (DateTime.UtcNow < until)
        {
            var batch = await client.CallAsync("events", new JsonObject { ["since"] = from, ["max"] = 200 }, Quick, ct) as JsonObject;
            foreach (var e in batch?["events"]?.AsArray() ?? [])
            {
                if (e is null) continue;
                bool typeOk = type is null || string.Equals(e["type"]?.ToString(), type, StringComparison.OrdinalIgnoreCase);
                bool textOk = contains is null || (e["data"]?.ToJsonString() ?? "").Contains(contains, StringComparison.OrdinalIgnoreCase);
                if (typeOk && textOk) return Json(new JsonObject { ["matched"] = e.DeepClone(), ["next"] = batch!["next"]?.DeepClone() });
            }
            from = (long)(batch?["next"] ?? from);
            await Task.Delay(300, ct);
        }
        throw new ToolError($"timed out after {timeout_seconds}s waiting for {type ?? "any event"}{(contains is null ? "" : $" containing '{contains}'")}. Check screen or screenshot.");
    }

    [McpServerTool(Name = "tree", ReadOnly = true)]
    [Description("The Godot node tree under a path (default: the game root), with class names and label text. For finding node paths and debugging UI.")]
    public async Task<string> Tree(
        [Description("Node path from the game root (as buttons/tree show it), or empty for the root.")] string? path = null,
        [Description("Levels to show (default 2, max 8).")] int depth = 2,
        [Description(PlayerHelp)] string? player = null,
        CancellationToken ct = default) =>
        Json(await registry.Resolve(player).CallAsync("tree", new JsonObject { ["path"] = path, ["depth"] = depth, ["maxChildren"] = 40 }, Quick, ct));

    [McpServerTool(Name = "node", ReadOnly = true)]
    [Description("One node's details: type, rect, text, its public game properties, and any named fields or properties (private ones too), e.g. members=['_posOffset'].")]
    public async Task<string> Node(
        [Description("Node path from the game root.")] string path,
        [Description("Extra field or property names to read.")] string[]? members = null,
        [Description(PlayerHelp)] string? player = null,
        CancellationToken ct = default) =>
        Json(await registry.Resolve(player).CallAsync("node", new JsonObject
        {
            ["path"] = path,
            ["members"] = new JsonArray((members ?? []).Select(m => (JsonNode)m).ToArray()),
        }, Quick, ct));

    [McpServerTool(Name = "tail_log", ReadOnly = true)]
    [Description("The end of a log: 'game' (the Godot log of that copy, where exceptions land), 'bridge', or a mod's own log file name in the game's data folder (e.g. ringdraft_log.txt).")]
    public string TailLog(
        [Description("game, bridge, or a file name.")] string source = "game",
        [Description("Lines to return (default 80).")] int lines = 80,
        [Description("Only lines matching this regex.")] string? match = null,
        [Description(PlayerHelp + " Used to pick that copy's game log.")] string? player = null)
    {
        DateTimeOffset? started = null;
        if (source is "" or "game" && registry.List().Count > 0)
        {
            var info = registry.Resolve(player).Info;
            if (DateTimeOffset.TryParse(info.StartedAt, out var t)) started = t;
        }
        return GameLogs.Tail(paths, source, lines, match, started);
    }

    // ---- Acting ----

    [McpServerTool(Name = "press", Destructive = false)]
    [Description("Presses a control by id (from buttons), the same way a click does. In co-op, act in the copy of the player who should act.")]
    public async Task<string> Press(
        [Description("Control id from buttons.")] string id,
        [Description(PlayerHelp)] string? player = null,
        [Description("Press even if the control is disabled.")] bool force = false,
        CancellationToken ct = default) =>
        Json(await registry.Resolve(player).CallAsync("press", new JsonObject { ["id"] = id, ["force"] = force }, Quick, ct));

    [McpServerTool(Name = "play_card", Destructive = false)]
    [Description("Plays a card from that copy's hand, as if dragged: hand_index from state's hand; for targeted cards, target = enemy index from state's enemies, or ally = player index from state's players (cards that target an ally).")]
    public async Task<string> PlayCard(
        [Description("Index in the hand (0 = leftmost).")] int hand_index,
        [Description("Enemy index from state, for cards that target an enemy.")] int? target = null,
        [Description("Player index from state, for cards that target an ally.")] int? ally = null,
        [Description(PlayerHelp)] string? player = null,
        CancellationToken ct = default) =>
        Json(await registry.Resolve(player).CallAsync("play_card", new JsonObject { ["handIndex"] = hand_index, ["target"] = target, ["ally"] = ally }, Quick, ct));

    [McpServerTool(Name = "end_turn", Destructive = false)]
    [Description("Ends that copy's player's turn (in co-op the round moves on once every player has). Takes effect a moment later: follow with wait_for turn_ended. undo takes back an ended turn while others are still playing.")]
    public async Task<string> EndTurn(
        [Description(PlayerHelp)] string? player = null,
        [Description("Take back this player's ended turn.")] bool undo = false,
        CancellationToken ct = default) =>
        Json(await registry.Resolve(player).CallAsync("end_turn", new JsonObject { ["undo"] = undo }, Quick, ct));

    [McpServerTool(Name = "console", Destructive = true)]
    [Description("Runs a command in the game's developer console in one copy, acting as that copy's player. Run-changing commands (gold, card, relic, potion, room, fight, event, act, heal, damage, kill, win...) " +
                 "are synchronized in co-op, so run each once, not once per copy. Useful: 'help', 'help <cmd>', 'room shop', 'room restsite', 'fight <ID>', 'gold 300', 'card BODY_SLAM', 'relic <ID>', 'godmode', 'win'. " +
                 "IDs are SCREAMING_SNAKE_CASE. cloud, sentry, leaderboard, achievement, open and getlogs are blocked.")]
    public async Task<string> Console(
        [Description("The console command, e.g. 'room shop'.")] string command,
        [Description(PlayerHelp)] string? player = null,
        CancellationToken ct = default) =>
        Json(await registry.Resolve(player).CallAsync("console", new JsonObject { ["command"] = command, ["timeoutMs"] = 15000 }, TimeSpan.FromSeconds(25), ct));

    // ---- Static inspection (no game running needed) ----

    [McpServerTool(Name = "inspect", ReadOnly = true)]
    [Description("Runs sts2-inspect on the installed game (no game copy needed). args is the command line, where {sts2} is the game's sts2.dll and {pck} its SlayTheSpire2.pck. " +
                 "Examples: ['types','{sts2}','=CombatManager'], ['il','{sts2}','=CombatManager','LoseCombat'], ['has','{sts2}','CombatManager.EndCombatInternal'], ['pck','list','{pck}','scenes/ui/'], ['refs','C:/path/MyMod.dll']. Run with [] for usage.")]
    public string Inspect([Description("Arguments, e.g. ['types','{sts2}','=Player'].")] string[] args)
    {
        var expanded = args.Select(a => a.Replace("{sts2}", paths.Sts2Dll ?? "{sts2}").Replace("{pck}", paths.Pck ?? "{pck}")).ToArray();
        var output = new StringWriter();
        int code = Sts2Inspect.Program.Run(expanded, output, output);
        string text = output.ToString();
        const int max = 40_000;
        if (text.Length > max) text = text[..max] + $"\n... ({text.Length - max} more characters; narrow the filter)";
        return $"exit {code}\n{text}";
    }

    private static string Json(JsonNode? node) => node?.ToJsonString(Pretty) ?? "null";
}
