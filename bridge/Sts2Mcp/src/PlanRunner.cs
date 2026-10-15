using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using Sts2Bridge.Protocol;

namespace Sts2Mcp;

/// <summary>
/// Runs a plan: a list of steps executed back to back without going through the agent in between. Each action
/// waits until it can happen (button present and enabled, card playable, the player's turn) up to its timeout, so
/// a plan reads like what a person would do. The first failure stops the plan and reports where and why, with a
/// look at the game, so the agent only has to think again when something unexpected happened.
/// </summary>
public sealed class PlanRunner(InstanceRegistry registry)
{
    public sealed record Image(byte[] Data, string Mime, string Caption);
    public sealed record Outcome(JsonObject Report, List<Image> Images);

    public static readonly string[] Verbs =
        ["press", "play", "end_turn", "console", "wait", "expect", "look", "screenshot", "speed", "handoff", "banner"];

    private static readonly TimeSpan Call = TimeSpan.FromSeconds(15);
    private const double DefaultActionTimeout = 10;
    private const double DefaultExpectTimeout = 5;
    private const double DefaultWaitTimeout = 30;
    private const double DefaultHandoffTimeout = 120;

    private readonly Dictionary<string, long> _cursor = new();   // per copy: events after this belong to the latest action
    private readonly List<Image> _images = new();
    private string? _defaultPlayer;

    public async Task<Outcome> RunAsync(JsonArray steps, string? player, IProgress<ProgressNotificationValue>? progress, CancellationToken ct)
    {
        _defaultPlayer = player;
        var results = new JsonArray();
        var total = Stopwatch.StartNew();
        var report = new JsonObject();

        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i] is not JsonObject step) throw new ToolError($"step {i + 1} isn't an object, e.g. {{\"press\": \"Proceed\"}}");
            string verb = Verb(step, i);
            bool optional = step["optional"]?.GetValue<bool>() == true;
            var sw = Stopwatch.StartNew();
            var r = new JsonObject { ["step"] = i + 1, ["do"] = verb };
            try
            {
                string summary = await RunStepAsync(verb, step, progress, i, steps.Count, ct);
                r["ok"] = true;
                r["result"] = summary;
            }
            catch (ToolError e) when (!ct.IsCancellationRequested)
            {
                r["ok"] = false;
                r["error"] = e.Message;
                if (!optional)
                {
                    r["ms"] = sw.ElapsedMilliseconds;
                    results.Add(r);
                    report["ok"] = false;
                    report["stoppedAt"] = i + 1;
                    report["error"] = e.Message;
                    report["notRun"] = steps.Count - i - 1;
                    report["steps"] = results;
                    report["ms"] = total.ElapsedMilliseconds;
                    string who = step["player"]?.ToString() ?? player ?? "host";
                    try { report["look"] = await LookAsync(who, ct); } catch (ToolError) { }
                    try { await ScreenshotAsync(who, $"where step {i + 1} stopped ({who})", 1024, ct); } catch (ToolError) { }
                    return new Outcome(report, _images);
                }
            }
            r["ms"] = sw.ElapsedMilliseconds;
            results.Add(r);
        }

        report["ok"] = true;
        report["steps"] = results;
        report["ms"] = total.ElapsedMilliseconds;
        try { report["look"] = await LookAsync(player, ct); } catch (ToolError) { }
        return new Outcome(report, _images);
    }

    private static string Verb(JsonObject step, int i)
    {
        var found = Verbs.Where(step.ContainsKey).ToList();
        if (found.Count == 1) return found[0];
        if (found.Count == 0)
            throw new ToolError($"step {i + 1} has no action. Use one of: {string.Join(", ", Verbs)} (e.g. {{\"press\": \"Proceed\"}}, {{\"play\": \"Bash\", \"target\": 0}})");
        throw new ToolError($"step {i + 1} has several actions ({string.Join(", ", found)}); one per step");
    }

    private async Task<string> RunStepAsync(string verb, JsonObject s, IProgress<ProgressNotificationValue>? progress, int i, int n, CancellationToken ct)
    {
        string? who = s["player"]?.ToString() ?? _defaultPlayer;
        double timeout = Num(s["timeout"]) ?? (verb switch
        {
            "expect" => DefaultExpectTimeout,
            "wait" => DefaultWaitTimeout,
            "handoff" => DefaultHandoffTimeout,
            _ => DefaultActionTimeout,
        });
        progress?.Report(new ProgressNotificationValue { Progress = i, Total = n, Message = $"step {i + 1}/{n}: {verb}" });

        switch (verb)
        {
            case "press":
            {
                string what = s["press"]!.ToString();
                await MarkAsync(who, ct);
                var args = what.Contains('/') ? new JsonObject { ["id"] = what } : new JsonObject { ["text"] = what };
                if (s["force"]?.GetValue<bool>() == true) args["force"] = true;
                var res = await RetryAsync(who, "press", args, timeout, ct);
                string id = res?["pressed"]?.ToString() ?? what;
                return $"pressed {what}{(what.Contains('/') ? "" : $" ({id[(id.LastIndexOf('/') + 1)..]})")}";
            }
            case "play":
            {
                var args = new JsonObject();
                var card = s["play"]!;
                if (card is JsonValue v && v.TryGetValue(out int index)) args["handIndex"] = index;
                else args["card"] = card.ToString();
                if (s["target"] is JsonValue t)
                {
                    if (t.TryGetValue(out int ti)) args["target"] = ti;
                    else args["targetName"] = t.ToString();
                }
                if (Num(s["ally"]) is { } ally) args["ally"] = (int)ally;
                await MarkAsync(who, ct);
                var res = await RetryAsync(who, "play_card", args, timeout, ct);
                return $"played {res?["played"]}{(res?["target"] is { } tn ? $" on {tn}" : "")}";
            }
            case "end_turn":
            {
                await MarkAsync(who, ct);
                await RetryAsync(who, "end_turn", new JsonObject { ["undo"] = s["undo"]?.GetValue<bool>() == true }, timeout, ct);
                return "ended turn";
            }
            case "console":
            {
                string command = s["console"]!.ToString();
                await MarkAsync(who, ct);
                var res = await RetryAsync(who, "console", new JsonObject { ["command"] = command, ["timeoutMs"] = 15000 }, timeout, ct);
                if (res?["success"]?.GetValue<bool>() != true) throw new ToolError($"console '{command}' failed: {res?["message"]}");
                return $"console {command}: {res["message"]}";
            }
            case "wait":
                return await WaitAsync(who, s["wait"]!, s, timeout, ct);
            case "expect":
                return await ExpectAsync(who, s["expect"] as JsonObject ?? throw new ToolError("expect takes an object of checks, e.g. {\"me.gold\": \">=100\"}"), timeout, ct);
            case "look":
                return (await LookAsync(who, ct)).ToJsonString();
            case "screenshot":
            {
                int width = (int)(Num(s["width"]) ?? 1024);
                string caption = s["screenshot"] is JsonValue c && c.TryGetValue(out string? text) ? text : $"step {i + 1} ({who ?? "host"})";
                await ScreenshotAsync(who, caption, width, ct);
                return $"screenshot: {caption}";
            }
            case "speed":
            {
                string speed = s["speed"]!.ToString();
                var targets = s["player"] is null ? registry.All() : [registry.Resolve(who)];
                foreach (var c in targets) await c.CallAsync("speed", new JsonObject { ["set"] = speed }, Call, ct);
                return $"speed {speed} on {(s["player"] is null ? "every copy" : who)}";
            }
            case "banner":
            {
                string? text = s["banner"] is JsonValue b && b.TryGetValue(out string? bt) ? bt : null;
                await registry.Resolve(who).CallAsync("banner", new JsonObject { ["text"] = text }, Call, ct);
                return text is null ? "banner cleared" : $"banner: {text}";
            }
            case "handoff":
                return await HandoffAsync(who, s, timeout, progress, i, n, ct);
        }
        throw new ToolError($"unknown step '{verb}'");
    }

    // ---- actions ----

    /// <summary>Calls a bridge command, retrying while the game says "not yet", until the timeout.</summary>
    private async Task<JsonNode?> RetryAsync(string? who, string cmd, JsonObject args, double timeoutSeconds, CancellationToken ct)
    {
        var client = registry.Resolve(who);
        var until = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            try { return await client.CallAsync(cmd, args.DeepClone().AsObject(), Call, ct); }
            catch (ToolError e) when (e.Retry)
            {
                if (DateTime.UtcNow >= until) throw new ToolError($"{e.Message} (waited {timeoutSeconds:0.#}s)");
                await Task.Delay(100, ct);
            }
        }
    }

    /// <summary>Remembers where the event log stood before an action, so a following wait sees what the action caused.</summary>
    private async Task MarkAsync(string? who, CancellationToken ct)
    {
        var client = registry.Resolve(who);
        var r = await client.CallAsync("events", new JsonObject { ["since"] = long.MaxValue }, Call, ct);
        _cursor[Key(client)] = (long?)r?["next"] ?? 0;
    }

    private static string Key(BridgeClient c) => c.Info.Pid.ToString(CultureInfo.InvariantCulture);

    // ---- waiting ----

    /// <summary>
    /// wait: a number of seconds; an event type ("turn_started", "combat_won", "room", "screen", "run_started", ...);
    /// "my_turn" (a player turn started); "idle" (nothing on screen changed for half a second);
    /// or {"event": type, "contains": text}, {"screen": text}.
    /// </summary>
    private async Task<string> WaitAsync(string? who, JsonNode spec, JsonObject s, double timeout, CancellationToken ct)
    {
        if (spec is JsonValue v && v.TryGetValue(out double seconds))
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 120)), ct);
            return $"waited {seconds:0.##}s";
        }

        string? type = null, contains = s["contains"]?.ToString(), screen = null;
        if (spec is JsonObject o)
        {
            type = o["event"]?.ToString();
            contains = o["contains"]?.ToString() ?? contains;
            screen = o["screen"]?.ToString();
        }
        else type = spec.ToString();

        if (type == "idle") return await WaitIdleAsync(who, timeout, ct);
        if (type == "my_turn") { type = "turn_started"; contains ??= "Player"; }
        if (type == "fight_over") type = "combat_ended";

        var client = registry.Resolve(who);
        var until = DateTime.UtcNow.AddSeconds(timeout);
        if (screen is not null)
        {
            while (DateTime.UtcNow < until)
            {
                var look = await client.CallAsync("screen", null, Call, ct);
                string now = $"{look?["screen"]} {look?["overlay"]} {look?["room"]}";
                if (now.Contains(screen, StringComparison.OrdinalIgnoreCase)) return $"screen {look?["screen"]}";
                await Task.Delay(150, ct);
            }
            throw new ToolError($"timed out after {timeout:0.#}s waiting for screen '{screen}'");
        }

        long from = _cursor.TryGetValue(Key(client), out long c) ? c : 0;
        while (DateTime.UtcNow < until)
        {
            var batch = await client.CallAsync("events", new JsonObject { ["since"] = from, ["max"] = 200 }, Call, ct) as JsonObject;
            foreach (var e in batch?["events"]?.AsArray() ?? [])
            {
                if (e is null) continue;
                long seq = (long)e["seq"]!;
                from = seq + 1;
                bool typeOk = string.Equals(e["type"]?.ToString(), type, StringComparison.OrdinalIgnoreCase);
                bool textOk = contains is null || (e["data"]?.ToJsonString() ?? "").Contains(contains, StringComparison.OrdinalIgnoreCase);
                if (typeOk && textOk)
                {
                    _cursor[Key(client)] = seq + 1;
                    return $"{type} {Brief(e["data"])}".Trim();
                }
            }
            _cursor[Key(client)] = from;
            await Task.Delay(100, ct);
        }
        throw new ToolError($"timed out after {timeout:0.#}s waiting for {type}{(contains is null ? "" : $" containing '{contains}'")}");
    }

    private async Task<string> WaitIdleAsync(string? who, double timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(timeout);
        string last = await SignatureAsync(who, ct);
        var stableSince = DateTime.UtcNow;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(100, ct);
            string now = await SignatureAsync(who, ct);
            if (now != last) { last = now; stableSince = DateTime.UtcNow; }
            else if (DateTime.UtcNow - stableSince >= TimeSpan.FromMilliseconds(500)) return "idle";
        }
        throw new ToolError($"the game kept changing for {timeout:0.#}s (never idle)");
    }

    // ---- checks ----

    /// <summary>
    /// Each key is a path into look (me.gold, me.hp, enemies.0.hp, hand.count, screen, others.0.gold); each value is
    /// what it should be: a number or text (equal), or text starting with &gt;= &lt;= &gt; &lt; != ~ (contains) !~.
    /// Retried until the timeout, since effects take a moment to land.
    /// </summary>
    private async Task<string> ExpectAsync(string? who, JsonObject checks, double timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(timeout);
        List<string> failures;
        while (true)
        {
            var look = await LookAsync(who, ct);
            failures = new List<string>();
            var passed = new List<string>();
            foreach (var (path, want) in checks)
            {
                var actual = Path(look, path);
                if (Check(actual, want)) passed.Add($"{path}={Show(actual)}");
                else failures.Add($"{path} is {Show(actual)}, expected {Show(want)}");
            }
            if (failures.Count == 0) return string.Join(", ", passed);
            if (DateTime.UtcNow >= until) break;
            await Task.Delay(150, ct);
        }
        throw new ToolError($"expect failed: {string.Join("; ", failures)}");
    }

    public static JsonNode? Path(JsonNode? node, string path)
    {
        foreach (string part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node is null) return null;
            if (node is JsonArray arr)
            {
                if (part is "count" or "length") return arr.Count;
                if (int.TryParse(part, out int idx)) { node = idx >= 0 && idx < arr.Count ? arr[idx] : null; continue; }
                // hand.Strike → the first entry whose name is Strike
                node = arr.FirstOrDefault(x => string.Equals(x?["name"]?.ToString()?.TrimEnd('+'), part, StringComparison.OrdinalIgnoreCase));
                continue;
            }
            node = node is JsonObject obj ? obj[part] : null;
        }
        return node;
    }

    public static bool Check(JsonNode? actual, JsonNode? want)
    {
        if (want is null) return actual is null;
        if (want is JsonValue wv && wv.TryGetValue(out bool wb)) return actual is JsonValue av && av.TryGetValue(out bool ab) && ab == wb;
        if (want is JsonValue wn && TryNum(wn, out double wantNum) && !(wn.TryGetValue(out string? _)))
            return TryNum(actual, out double a) && Math.Abs(a - wantNum) < 1e-9;

        string spec = want.ToString().Trim();
        string text = actual?.ToString() ?? "";
        foreach (string op in new[] { ">=", "<=", "!=", "!~", ">", "<", "=", "~" })
        {
            if (!spec.StartsWith(op)) continue;
            string rhs = spec[op.Length..].Trim();
            bool numeric = double.TryParse(rhs, NumberStyles.Float, CultureInfo.InvariantCulture, out double r) && TryNum(actual, out double l);
            TryNum(actual, out double lhs);
            return op switch
            {
                ">=" => numeric && lhs >= r,
                "<=" => numeric && lhs <= r,
                ">" => numeric && lhs > r,
                "<" => numeric && lhs < r,
                "!=" => numeric ? Math.Abs(lhs - r) > 1e-9 : !text.Equals(rhs, StringComparison.OrdinalIgnoreCase),
                "=" => numeric ? Math.Abs(lhs - r) < 1e-9 : text.Equals(rhs, StringComparison.OrdinalIgnoreCase),
                "~" => text.Contains(rhs, StringComparison.OrdinalIgnoreCase),
                "!~" => !text.Contains(rhs, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }
        if (double.TryParse(spec, NumberStyles.Float, CultureInfo.InvariantCulture, out double sn) && TryNum(actual, out double an)) return Math.Abs(an - sn) < 1e-9;
        return text.Equals(spec, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNum(JsonNode? n, out double value)
    {
        value = 0;
        if (n is not JsonValue v) return false;
        if (v.TryGetValue(out double d)) { value = d; return true; }
        if (v.TryGetValue(out int i)) { value = i; return true; }
        if (v.TryGetValue(out long l)) { value = l; return true; }
        return v.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string Show(JsonNode? n) => n is null ? "missing" : n is JsonValue ? n.ToString() : n.ToJsonString().Length > 80 ? n.ToJsonString()[..80] + "…" : n.ToJsonString();

    // ---- hand-off ----

    /// <summary>
    /// Hands the next action to the person at the keyboard: switches every copy to normal speed (or "speed"), shows the
    /// message as a banner in that copy's window, waits until something happens ("until": an event type such as
    /// turn_ended or combat_won, {"screen": text}, a check object like expect's, or by default any change on screen),
    /// lets the result play out ("settle" seconds, default 2, optionally capturing "frames" screenshots), then clears the
    /// banner and restores the speed.
    /// </summary>
    private async Task<string> HandoffAsync(string? who, JsonObject s, double timeout, IProgress<ProgressNotificationValue>? progress, int i, int n, CancellationToken ct)
    {
        string message = s["handoff"]!.ToString();
        string speed = s["speed"]?.ToString() ?? "normal";
        bool restore = s["restore"]?.GetValue<bool>() != false;
        double settle = Num(s["settle"]) ?? 2;
        int frames = (int)Math.Clamp(Num(s["frames"]) ?? 0, 0, 8);
        var until = s["until"];

        var copies = registry.All();
        var before = new Dictionary<BridgeClient, string>();
        foreach (var c in copies)
        {
            before[c] = (await c.CallAsync("speed", null, Call, ct))?["speed"]?.ToString() ?? "normal";
            await c.CallAsync("speed", new JsonObject { ["set"] = speed }, Call, ct);
        }
        var target = registry.Resolve(who);
        await MarkAsync(who, ct);
        string baseline = await SignatureAsync(who, ct);
        await target.CallAsync("banner", new JsonObject { ["text"] = message }, Call, ct);
        progress?.Report(new ProgressNotificationValue { Progress = i, Total = n, Message = $"waiting for you: {message}" });

        string what;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeout);
            what = await WaitForPersonAsync(who, until, baseline, deadline, ct)
                   ?? throw new ToolError($"hand-off timed out after {timeout:0}s: nobody did \"{message}\". The game is left at {speed} speed; run the plan's remaining steps once it's done.");
            await target.CallAsync("banner", new JsonObject { ["text"] = null }, Call, ct);

            if (frames > 0)
            {
                for (int f = 0; f < frames; f++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(settle / frames), ct);
                    await ScreenshotAsync(who, $"after hand-off, frame {f + 1}/{frames}", 768, ct);
                }
            }
            else await Task.Delay(TimeSpan.FromSeconds(settle), ct);
        }
        catch
        {
            try { await target.CallAsync("banner", new JsonObject { ["text"] = null }, Call, CancellationToken.None); } catch { }
            throw;
        }

        if (restore)
            foreach (var (c, sp) in before)
                try { await c.CallAsync("speed", new JsonObject { ["set"] = sp }, Call, ct); } catch (ToolError) { }
        return $"you did it ({what}){(restore ? "; speed restored" : "")}";
    }

    private async Task<string?> WaitForPersonAsync(string? who, JsonNode? until, string baseline, DateTime deadline, CancellationToken ct)
    {
        var client = registry.Resolve(who);
        long from = _cursor.TryGetValue(Key(client), out long c) ? c : 0;
        while (DateTime.UtcNow < deadline)
        {
            if (until is JsonObject checks && !checks.ContainsKey("screen") && !checks.ContainsKey("event"))
            {
                var look = await LookAsync(who, ct);
                if (checks.All(kv => Check(Path(look, kv.Key), kv.Value))) return "checks passed";
            }
            else if (until is JsonObject so && so["screen"] is { } scr)
            {
                var d = await client.CallAsync("screen", null, Call, ct);
                if ($"{d?["screen"]} {d?["overlay"]} {d?["room"]}".Contains(scr.ToString(), StringComparison.OrdinalIgnoreCase)) return $"screen {d?["screen"]}";
            }
            else if (until is not null)
            {
                string type = until is JsonObject eo ? eo["event"]!.ToString() : until.ToString();
                var batch = await client.CallAsync("events", new JsonObject { ["since"] = from, ["max"] = 200 }, Call, ct) as JsonObject;
                foreach (var e in batch?["events"]?.AsArray() ?? [])
                {
                    from = (long)e!["seq"]! + 1;
                    if (string.Equals(e["type"]?.ToString(), type, StringComparison.OrdinalIgnoreCase)) return type;
                }
            }
            else if (await SignatureAsync(who, ct) != baseline)
            {
                return "something changed";
            }
            await Task.Delay(200, ct);
        }
        return null;
    }

    // ---- looking ----

    private async Task<JsonObject> LookAsync(string? who, CancellationToken ct) =>
        await registry.Resolve(who).CallAsync("look", null, Call, ct) as JsonObject ?? new JsonObject();

    /// <summary>What a person would notice changing: screen, hand, energy, hp, gold, enemies.</summary>
    private async Task<string> SignatureAsync(string? who, CancellationToken ct)
    {
        var l = await LookAsync(who, ct);
        return $"{l["screen"]}|{l["overlay"]}|{l["room"]}|{l["me"]?["hp"]}|{l["me"]?["gold"]}|{l["me"]?["energy"]}|{l["hand"]?.ToJsonString()}|{l["enemies"]?.ToJsonString()}|{l["mapOpen"]}";
    }

    private async Task ScreenshotAsync(string? who, string caption, int width, CancellationToken ct)
    {
        var shot = await registry.Resolve(who).CallAsync("screenshot", new JsonObject { ["maxWidth"] = width, ["format"] = "png" }, TimeSpan.FromSeconds(20), ct);
        if (shot?["data"]?.ToString() is { Length: > 0 } b64)
            _images.Add(new Image(Convert.FromBase64String(b64), shot["mime"]?.ToString() ?? "image/png", caption));
    }

    private static double? Num(JsonNode? n) => TryNum(n, out double d) ? d : null;

    private static string Brief(JsonNode? data)
    {
        if (data is not JsonObject o) return "";
        var parts = o.Where(kv => kv.Value is JsonValue).Select(kv => $"{kv.Key}={kv.Value}").Take(4);
        return string.Join(" ", parts);
    }
}
