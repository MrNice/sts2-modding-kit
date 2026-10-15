using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Sts2Bridge.Protocol;
using Sts2Mcp;
using Xunit;

namespace Sts2Mcp.Tests;

/// <summary>
/// A pretend game copy behind the real wire protocol: a lobby button that only becomes pressable after a moment,
/// a hand of cards, gold, events and speed, enough to exercise plans end to end.
/// </summary>
internal sealed class FakeGame : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly DateTime _readyAt;
    private readonly List<JsonObject> _events = new();
    public readonly TempDir Data = new();
    public int Gold = 99;
    public string Speed = "normal";
    public string? Banner;
    public readonly List<string> BannerHistory = new();
    public readonly List<string> Commands = new();
    public List<string> Hand = ["Strike", "Bash", "Defend"];
    public int EnemyHp = 40;

    public FakeGame(TimeSpan buttonDelay)
    {
        _readyAt = DateTime.UtcNow + buttonDelay;
        _listener.Start();
        InstanceRegistry.IsTestHost = p => p.Id == Environment.ProcessId;
        string dir = BridgePaths.Instances(Data.Path);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{Environment.ProcessId}.json"), Wire.Encode(new InstanceInfo
        {
            Pid = Environment.ProcessId, Port = ((IPEndPoint)_listener.LocalEndpoint).Port, Token = "t", ClientId = 1001,
            FastMp = "host", StartedAt = DateTimeOffset.Now.ToString("o"),
        }));
        _ = Task.Run(Accept);
    }

    public InstanceRegistry Registry => new(new GamePaths { DataDir = Data.Path });

    public void Event(string type, JsonObject? data = null)
    {
        lock (_events) _events.Add(new JsonObject { ["seq"] = _events.Count + 1, ["type"] = type, ["data"] = data ?? new JsonObject() });
    }

    private async Task Accept()
    {
        while (true)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(); } catch { return; }
            _ = Task.Run(() => Serve(c));
        }
    }

    private async Task Serve(TcpClient c)
    {
        using (c)
        {
            var s = c.GetStream();
            var reader = new StreamReader(s);
            var writer = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            while (await reader.ReadLineAsync() is { } line)
            {
                var req = Wire.Decode<BridgeRequest>(line)!;
                BridgeResponse res;
                try { res = BridgeResponse.Success(req.Id, Handle(req.Cmd!, req.Args)); }
                catch (NotYet e) { res = BridgeResponse.Failure(req.Id, e.Message, retry: true); }
                catch (Exception e) { res = BridgeResponse.Failure(req.Id, e.Message); }
                await writer.WriteLineAsync(Wire.Encode(res));
            }
        }
    }

    private sealed class NotYet(string m) : Exception(m);

    private JsonNode? Handle(string cmd, JsonObject? a)
    {
        lock (this) Commands.Add(cmd);
        switch (cmd)
        {
            case "hello": return new JsonObject();
            case "events":
            {
                long since = Args.Int(a, "since") is { } i ? i : (long?)a?["since"]?.GetValue<long>() ?? 0;
                lock (_events)
                {
                    var list = new JsonArray(_events.Where(e => (int)e["seq"]! >= since).Select(e => (JsonNode)e.DeepClone()).ToArray());
                    return new JsonObject { ["next"] = _events.Count + 1, ["events"] = list };
                }
            }
            case "look":
                return new JsonObject
                {
                    ["screen"] = "NCombatRoom",
                    ["speed"] = Speed,
                    ["me"] = new JsonObject { ["gold"] = Gold, ["hp"] = 80, ["energy"] = 3 },
                    ["hand"] = new JsonArray(Hand.Select((h, i) => (JsonNode)new JsonObject { ["i"] = i, ["name"] = h }).ToArray()),
                    ["enemies"] = new JsonArray(new JsonObject { ["i"] = 0, ["name"] = "Cultist", ["hp"] = EnemyHp }),
                };
            case "screen": return new JsonObject { ["screen"] = "NCombatRoom" };
            case "press":
                if (DateTime.UtcNow < _readyAt) throw new NotYet("no button matching yet");
                Event("pressed", new JsonObject { ["id"] = Args.Str(a, "text") });
                return new JsonObject { ["pressed"] = "Root/Lobby/" + Args.Str(a, "text") };
            case "play_card":
            {
                string name = Args.Str(a, "card") ?? Hand[Args.Int(a, "handIndex")!.Value];
                if (!Hand.Remove(name)) throw new NotYet($"no '{name}' in hand");
                if (name == "Bash") EnemyHp -= 8;
                return new JsonObject { ["played"] = name, ["target"] = Args.Int(a, "target") is null ? null : "Cultist" };
            }
            case "end_turn":
                Event("turn_ended");
                Event("turn_started", new JsonObject { ["side"] = "Player" });
                return new JsonObject { ["requested"] = "end turn" };
            case "console":
            {
                string command = Args.Str(a, "command")!;
                if (command.StartsWith("gold ")) Gold += int.Parse(command[5..]);
                return new JsonObject { ["success"] = !command.StartsWith("nope"), ["message"] = "Enqueued" };
            }
            case "speed":
                if (Args.Str(a, "set") is { } sp) Speed = sp;
                return new JsonObject { ["speed"] = Speed };
            case "banner":
                Banner = Args.Str(a, "text");
                if (Banner is not null) BannerHistory.Add(Banner);
                return new JsonObject { ["banner"] = Banner };
            case "screenshot":
                return new JsonObject { ["data"] = Convert.ToBase64String([1, 2, 3]), ["mime"] = "image/png", ["width"] = 1, ["height"] = 1 };
            default: throw new Exception($"unknown command '{cmd}'");
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        Data.Dispose();
    }
}

public class PlanTests
{
    private static JsonArray Steps(string json) => JsonNode.Parse(json)!.AsArray();

    [Fact]
    public async Task ActionsWaitUntilTheyCanHappenThenTheChecksPass()
    {
        using var game = new FakeGame(TimeSpan.FromMilliseconds(600));
        var outcome = await new PlanRunner(game.Registry).RunAsync(Steps("""
            [ {"press": "Proceed"},
              {"console": "gold 200"},
              {"play": "Bash", "target": 0},
              {"end_turn": true},
              {"wait": "my_turn"},
              {"expect": {"me.gold": ">=299", "enemies.0.hp": 32, "hand.count": 2, "hand.Bash": null, "screen": "~Combat"}} ]
            """), null, null, CancellationToken.None);

        var r = outcome.Report;
        Assert.True(r["ok"]!.GetValue<bool>(), r.ToJsonString());
        Assert.Equal(6, r["steps"]!.AsArray().Count);
        Assert.Contains("played Bash on Cultist", r["steps"]![2]!["result"]!.ToString());
        Assert.NotNull(r["look"]);
    }

    [Fact]
    public async Task AFailedCheckStopsThePlanAndSaysWhere()
    {
        using var game = new FakeGame(TimeSpan.Zero);
        var outcome = await new PlanRunner(game.Registry).RunAsync(Steps("""
            [ {"expect": {"me.gold": ">=500"}, "timeout": 0.3},
              {"console": "gold 1"} ]
            """), null, null, CancellationToken.None);
        var r = outcome.Report;
        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Equal(1, r["stoppedAt"]!.GetValue<int>());
        Assert.Equal(1, r["notRun"]!.GetValue<int>());
        Assert.Contains("me.gold is 99, expected >=500", r["error"]!.ToString());
        Assert.Single(outcome.Images);                       // a screenshot of where it stopped
        Assert.DoesNotContain("console", game.Commands);     // the next step never ran
    }

    [Fact]
    public async Task OptionalStepsDontStopThePlan()
    {
        using var game = new FakeGame(TimeSpan.FromSeconds(30));
        var outcome = await new PlanRunner(game.Registry).RunAsync(Steps("""
            [ {"press": "Never", "timeout": 0.3, "optional": true},
              {"console": "gold 1"} ]
            """), null, null, CancellationToken.None);
        Assert.True(outcome.Report["ok"]!.GetValue<bool>());
        Assert.Contains("waited 0.3s", outcome.Report["steps"]![0]!["error"]!.ToString());
        Assert.Equal(100, game.Gold);
    }

    [Fact]
    public async Task AFailedConsoleCommandFailsTheStep()
    {
        using var game = new FakeGame(TimeSpan.Zero);
        var outcome = await new PlanRunner(game.Registry).RunAsync(Steps("""[ {"console": "nope"} ]"""), null, null, CancellationToken.None);
        Assert.Contains("console 'nope' failed", outcome.Report["error"]!.ToString());
    }

    [Fact]
    public async Task HandoffShowsTheMessageAtNormalSpeedWaitsAndRestores()
    {
        using var game = new FakeGame(TimeSpan.Zero);
        game.Speed = "instant";
        var run = new PlanRunner(game.Registry).RunAsync(Steps("""
            [ {"handoff": "Play Bash on the Cultist", "until": "turn_ended", "settle": 0.1, "frames": 1, "timeout": 10} ]
            """), null, null, CancellationToken.None);

        await Task.Delay(700);
        Assert.Equal("Play Bash on the Cultist", game.Banner);   // visible in the game window
        Assert.Equal("normal", game.Speed);                      // so the person sees the real animation
        game.Event("turn_ended");                                // the person did it

        var outcome = await run;
        Assert.True(outcome.Report["ok"]!.GetValue<bool>(), outcome.Report.ToJsonString());
        Assert.Null(game.Banner);
        Assert.Equal("instant", game.Speed);
        Assert.Single(outcome.Images);
    }

    [Fact]
    public async Task HandoffByDefaultWaitsForAnyChange()
    {
        using var game = new FakeGame(TimeSpan.Zero);
        var run = new PlanRunner(game.Registry).RunAsync(Steps("""[ {"handoff": "Buy something", "settle": 0, "timeout": 10} ]"""), null, null, CancellationToken.None);
        await Task.Delay(600);
        game.Gold -= 50;
        var outcome = await run;
        Assert.Contains("something changed", outcome.Report["steps"]![0]!["result"]!.ToString());
    }

    [Fact]
    public async Task HandoffTimesOutCleanly()
    {
        using var game = new FakeGame(TimeSpan.Zero);
        var outcome = await new PlanRunner(game.Registry).RunAsync(Steps("""[ {"handoff": "Do it", "until": "combat_won", "timeout": 0.5} ]"""), null, null, CancellationToken.None);
        Assert.Contains("hand-off timed out", outcome.Report["error"]!.ToString());
        Assert.Null(game.Banner);
    }

    [Fact]
    public async Task StepsMustHaveExactlyOneAction()
    {
        using var game = new FakeGame(TimeSpan.Zero);
        await Assert.ThrowsAsync<ToolError>(() => new PlanRunner(game.Registry).RunAsync(Steps("""[ {"jump": 1} ]"""), null, null, CancellationToken.None));
        await Assert.ThrowsAsync<ToolError>(() => new PlanRunner(game.Registry).RunAsync(Steps("""[ {"press": "A", "play": "B"} ]"""), null, null, CancellationToken.None));
    }

    [Theory]
    [InlineData("""{"a": 5}""", "a", "5", true)]
    [InlineData("""{"a": 5}""", "a", ">=5", true)]
    [InlineData("""{"a": 5}""", "a", "<5", false)]
    [InlineData("""{"a": 5}""", "a", "!=4", true)]
    [InlineData("""{"a": "NMerchantRoom"}""", "a", "~merchant", true)]
    [InlineData("""{"a": "NMerchantRoom"}""", "a", "!~merchant", false)]
    [InlineData("""{"a": "Shop"}""", "a", "shop", true)]
    [InlineData("""{"a": [1,2,3]}""", "a.count", "3", true)]
    [InlineData("""{"a": [{"name": "Strike+"}]}""", "a.Strike.name", "Strike+", true)]
    [InlineData("""{"a": [{"hp": 7}]}""", "a.0.hp", "<10", true)]
    [InlineData("""{"a": [{"hp": 7}]}""", "a.3.hp", "7", false)]
    public void ChecksReadPathsAndOperators(string json, string path, string want, bool pass)
    {
        var actual = PlanRunner.Path(JsonNode.Parse(json), path);
        JsonNode wantNode = int.TryParse(want, out int n) ? JsonValue.Create(n) : JsonValue.Create(want);
        Assert.Equal(pass, PlanRunner.Check(actual, wantNode));
    }

    [Fact]
    public void ChecksHandleBooleansAndMissing()
    {
        Assert.True(PlanRunner.Check(JsonValue.Create(true), JsonValue.Create(true)));
        Assert.False(PlanRunner.Check(JsonValue.Create(false), JsonValue.Create(true)));
        Assert.True(PlanRunner.Check(null, null));
        Assert.False(PlanRunner.Check(JsonValue.Create(1), null));
    }
}

public class BridgeBuildKeyTests
{
    [Fact]
    public void SourceHashChangesWithTheSourceButNotLineEndings()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "A.cs"), "class A {}\n");
        string first = BridgeBuilder.SourceHash(dir.Path);
        File.WriteAllText(Path.Combine(dir.Path, "A.cs"), "class A {}\r\n");
        Assert.Equal(first, BridgeBuilder.SourceHash(dir.Path));
        File.WriteAllText(Path.Combine(dir.Path, "A.cs"), "class A { int x; }\n");
        Assert.NotEqual(first, BridgeBuilder.SourceHash(dir.Path));
    }

    [Fact]
    public void SourceHashWorksWhenTheSourceSitsUnderABinFolder()
    {
        // The tool run from a build output keeps its bridge source in .../bin/Release/net9.0/bridge-src.
        using var root = new TempDir();
        string src = Path.Combine(root.Path, "bin", "Release", "bridge-src");
        Directory.CreateDirectory(Path.Combine(src, "obj"));
        File.WriteAllText(Path.Combine(src, "A.cs"), "class A {}");
        File.WriteAllText(Path.Combine(src, "obj", "junk.cs"), "1");
        string first = BridgeBuilder.SourceHash(src);
        File.WriteAllText(Path.Combine(src, "obj", "junk.cs"), "2");        // build leftovers don't count
        Assert.Equal(first, BridgeBuilder.SourceHash(src));
        File.WriteAllText(Path.Combine(src, "A.cs"), "class A { }");        // source does
        Assert.NotEqual(first, BridgeBuilder.SourceHash(src));
    }
}
