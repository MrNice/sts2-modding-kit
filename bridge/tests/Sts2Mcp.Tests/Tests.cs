using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Sts2Bridge.Protocol;
using Sts2Mcp;
using Xunit;

namespace Sts2Mcp.Tests;

public class ProtocolTests
{
    [Theory]
    [InlineData(1001, 7701)]
    [InlineData(1002, 7702)]
    [InlineData(1099, 7799)]
    [InlineData(1, 7700)]
    [InlineData(null, 7700)]
    public void PreferredPortFollowsTheProfile(int? clientId, int port) => Assert.Equal(port, BridgeInfo.PreferredPort(clientId));

    [Fact]
    public void CandidatePortsStepByHundred() =>
        Assert.Equal(new[] { 7702, 7802, 7902, 8002, 8102 }, BridgeInfo.CandidatePorts(1002).ToArray());

    [Fact]
    public void ReadsArgumentsInBothForms()
    {
        Assert.Equal(1002, BridgeInfo.ClientId(["--force-steam=off", "--clientId=1002"]));
        Assert.Equal(1003, BridgeInfo.ClientId(["--clientId", "1003"]));
        Assert.Null(BridgeInfo.ClientId(["--fastmp=host"]));
        Assert.Equal("host", BridgeInfo.ArgValue(["--fastmp=host"], "--fastmp"));
    }

    [Fact]
    public void ActivatesOnlyOffline()
    {
        Assert.True(BridgeInfo.ShouldActivate(["--force-steam=off"]));
        Assert.True(BridgeInfo.ShouldActivate(["--sts2-bridge"]));
        Assert.False(BridgeInfo.ShouldActivate([]));
        Assert.False(BridgeInfo.ShouldActivate(["--fastmp=host"]));
    }

    [Theory]
    [InlineData("  Cloud delete", "cloud")]
    [InlineData("room shop", "room")]
    [InlineData("", "")]
    public void ConsoleVerbIsFirstWordLowerCase(string command, string verb) => Assert.Equal(verb, BridgeInfo.ConsoleVerb(command));

    [Fact]
    public void BlocksDangerousConsoleCommands()
    {
        foreach (string verb in new[] { "cloud", "sentry", "leaderboard", "achievement" })
            Assert.Contains(verb, BridgeInfo.BlockedConsoleCommands);
        Assert.DoesNotContain("room", BridgeInfo.BlockedConsoleCommands);
    }

    [Fact]
    public void WireIsCamelCaseAndRoundTrips()
    {
        string json = Wire.Encode(new BridgeRequest { Id = 7, Cmd = "press", Args = new JsonObject { ["id"] = "a/b" } });
        Assert.Contains("\"cmd\":\"press\"", json);
        var back = Wire.Decode<BridgeRequest>(json)!;
        Assert.Equal(7, back.Id);
        Assert.Equal("a/b", Args.Str(back.Args, "id"));

        string failure = Wire.Encode(BridgeResponse.Failure(3, "nope"));
        Assert.DoesNotContain("result", failure);   // nulls omitted
        Assert.Equal("nope", Wire.Decode<BridgeResponse>(failure)!.Error);
    }

    [Fact]
    public void ArgsAreLenient()
    {
        var a = JsonNode.Parse("""{"n":"5","b":"true","d":2.0,"list":["x","",3]}""")!.AsObject();
        Assert.Equal(5, Args.Int(a, "n"));
        Assert.Equal(2, Args.Int(a, "d"));
        Assert.True(Args.Bool(a, "b"));
        Assert.Equal(new[] { "x", "3" }, Args.StrList(a, "list"));
        Assert.Null(Args.Int(a, "missing"));
    }
}

public class PathAndSettingsTests
{
    [Fact]
    public void ParsesSteamLibraryFolders()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0" { "path"  "C:\\Program Files (x86)\\Steam"  "label" "" }
                "1" { "path"  "D:\\SteamLibrary"  "apps" { "2868840" "123" } }
            }
            """;
        Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }, GamePaths.ParseLibraryFolders(vdf));
    }

    [Fact]
    public void PatchEnablesModsAndKeepsEverythingElse()
    {
        const string before = """{"fps_limit":60,"mod_settings":{"mod_list":[{"id":"RingDraft","is_enabled":false,"source":"mods_directory"}],"mods_enabled":false}}""";
        string after = ProfileSettings.Patch(before, ["Sts2Bridge", "RingDraft"]);
        var node = JsonNode.Parse(after)!;
        Assert.Equal(60, node["fps_limit"]!.GetValue<int>());
        Assert.True(node["mod_settings"]!["mods_enabled"]!.GetValue<bool>());
        var list = node["mod_settings"]!["mod_list"]!.AsArray();
        Assert.Equal(2, list.Count);
        Assert.All(list, e => Assert.True(e!["is_enabled"]!.GetValue<bool>()));
    }

    [Fact]
    public void PatchIsANoOpWhenAlreadyEnabled()
    {
        const string ready = """{"mod_settings":{"mod_list":[{"id":"Sts2Bridge","is_enabled":true,"source":"mods_directory"}],"mods_enabled":true}}""";
        Assert.Same(ready, ProfileSettings.Patch(ready, ["Sts2Bridge"]));
    }

    [Fact]
    public void PatchCreatesModSettingsWhenMissing()
    {
        var node = JsonNode.Parse(ProfileSettings.Patch("{}", ["X"]))!;
        Assert.Equal("X", node["mod_settings"]!["mod_list"]![0]!["id"]!.ToString());
    }

    [Fact]
    public void ReadsABuiltModFolder()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "MyMod.json"), """{"id":"MyMod","has_dll":true}""");
        File.WriteAllText(Path.Combine(dir.Path, "MyMod.deps.json"), "{}");
        Assert.Throws<ToolError>(() => Launcher.ReadModFolder(dir.Path));   // no dll yet
        File.WriteAllText(Path.Combine(dir.Path, "MyMod.dll"), "");
        Assert.Equal("MyMod", Launcher.ReadModFolder(dir.Path).Id);
    }

    [Fact]
    public void RejectsAFolderWithoutAManifest()
    {
        using var dir = new TempDir();
        Assert.Throws<ToolError>(() => Launcher.ReadModFolder(dir.Path));
        Assert.Throws<ToolError>(() => Launcher.ReadModFolder(Path.Combine(dir.Path, "missing")));
    }

    [Fact]
    public async Task StopRemovesSessionModsAndRestoresBackups()
    {
        using var game = new TempDir();
        using var cache = new TempDir();
        using var data = new TempDir();
        var paths = new GamePaths { GameDir = game.Path, DataDir = data.Path, CacheDir = cache.Path };
        string mods = Path.Combine(game.Path, "mods");
        string installed = Path.Combine(mods, "Sts2Bridge");
        string original = Path.Combine(mods, "RingDraft");
        string backup = Path.Combine(cache.Path, "backup", "RingDraft-1");
        Directory.CreateDirectory(installed);
        Directory.CreateDirectory(original);           // the session's copy
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "mine.txt"), "user's own install");

        var session = new Launcher.Session
        {
            InstalledMods = [installed, original],
            Backups = new Dictionary<string, string> { [original] = backup },
        };
        File.WriteAllText(Path.Combine(cache.Path, "session.json"), System.Text.Json.JsonSerializer.Serialize(session));

        var result = await new Launcher(paths, new InstanceRegistry(paths)).StopAsync(CancellationToken.None);
        Assert.False(Directory.Exists(installed));
        Assert.True(File.Exists(Path.Combine(original, "mine.txt")));
        Assert.False(File.Exists(Path.Combine(cache.Path, "session.json")));
        Assert.Null(result["problems"]);
    }

    [Fact]
    public async Task StopLeavesABackupWhenTheOriginalCameBack()
    {
        using var game = new TempDir();
        using var cache = new TempDir();
        using var data = new TempDir();
        var paths = new GamePaths { GameDir = game.Path, DataDir = data.Path, CacheDir = cache.Path };
        string original = Path.Combine(game.Path, "mods", "RingDraft");
        string backup = Path.Combine(game.Path, "mods.sts2mcp-backup", "RingDraft-1");
        Directory.CreateDirectory(original);
        Directory.CreateDirectory(backup);
        var session = new Launcher.Session { Backups = new Dictionary<string, string> { [original] = backup } };
        File.WriteAllText(Path.Combine(cache.Path, "session.json"), System.Text.Json.JsonSerializer.Serialize(session));

        var result = await new Launcher(paths, new InstanceRegistry(paths)).StopAsync(CancellationToken.None);
        Assert.Contains("move it back by hand", result["problems"]!.ToJsonString());
        Assert.True(Directory.Exists(backup));                                    // nothing lost
        Assert.True(File.Exists(Path.Combine(cache.Path, "session.json")));       // kept so stop can be retried
    }

    [Fact]
    public void FindsManifestsAnywhereUnderMods()
    {
        using var mods = new TempDir();
        Directory.CreateDirectory(Path.Combine(mods.Path, "old", "RingDraft"));
        File.WriteAllText(Path.Combine(mods.Path, "old", "RingDraft", "RingDraft.json"), """{"id":"RingDraft","has_dll":true}""");
        File.WriteAllText(Path.Combine(mods.Path, "notes.json"), "not json at all");
        Assert.Single(Launcher.FindManifests(mods.Path, "ringdraft"));
        Assert.Empty(Launcher.FindManifests(mods.Path, "Other"));
    }

    /// <summary>
    /// A launch that fails after touching the mods folder must put everything back. Needs a game folder with
    /// sts2.dll and GodotSharp.dll (STS2_TEST_GAME_DIR) to build the bridge; skipped otherwise.
    /// </summary>
    [Fact]
    public async Task FailedLaunchPutsTheModsFolderBack()
    {
        string? realGame = Environment.GetEnvironmentVariable("STS2_TEST_GAME_DIR");
        if (realGame is null || !File.Exists(Path.Combine(realGame, GamePaths.DataSubdir, "sts2.dll"))) return;

        using var game = new TempDir();
        using var cache = new TempDir();
        using var data = new TempDir();
        using var build = new TempDir();
        Directory.CreateDirectory(Path.Combine(game.Path, GamePaths.DataSubdir));
        foreach (string dll in new[] { "sts2.dll", "GodotSharp.dll" })
            File.Copy(Path.Combine(realGame, GamePaths.DataSubdir, dll), Path.Combine(game.Path, GamePaths.DataSubdir, dll));
        File.WriteAllText(Path.Combine(game.Path, GamePaths.ExeName), "not a program");   // starting it fails
        string mine = Path.Combine(game.Path, "mods", "MyMod");
        Directory.CreateDirectory(mine);
        File.WriteAllText(Path.Combine(mine, "MyMod.json"), """{"id":"MyMod","has_dll":true}""");
        File.WriteAllText(Path.Combine(mine, "MyMod.dll"), "the user's own install");
        File.WriteAllText(Path.Combine(build.Path, "MyMod.json"), """{"id":"MyMod","has_dll":true}""");
        File.WriteAllText(Path.Combine(build.Path, "MyMod.dll"), "new build");

        var paths = new GamePaths { GameDir = game.Path, DataDir = data.Path, CacheDir = cache.Path };
        var launcher = new Launcher(paths, new InstanceRegistry(paths));
        await Assert.ThrowsAnyAsync<Exception>(() => launcher.LaunchAsync(1, [build.Path], allowRunning: true, _ => Task.CompletedTask, CancellationToken.None));

        Assert.Equal("the user's own install", File.ReadAllText(Path.Combine(mine, "MyMod.dll")));
        Assert.False(Directory.Exists(Path.Combine(game.Path, "mods", "Sts2Bridge")));
        Assert.False(launcher.HasSession);
    }

    [Fact]
    public async Task RefusesDuplicateAndInstalledModFolders()
    {
        using var game = new TempDir();
        using var cache = new TempDir();
        using var build = new TempDir();
        File.WriteAllText(Path.Combine(game.Path, GamePaths.ExeName), "");
        File.WriteAllText(Path.Combine(build.Path, "MyMod.json"), """{"id":"MyMod","has_dll":false}""");
        string installed = Path.Combine(game.Path, "mods", "MyMod");
        Directory.CreateDirectory(installed);
        File.WriteAllText(Path.Combine(installed, "MyMod.json"), """{"id":"MyMod","has_dll":false}""");

        var paths = new GamePaths { GameDir = game.Path, DataDir = cache.Path, CacheDir = cache.Path };
        var launcher = new Launcher(paths, new InstanceRegistry(paths));
        Task Launch(params string[] mods) => launcher.LaunchAsync(1, mods, allowRunning: true, _ => Task.CompletedTask, CancellationToken.None);

        Assert.Contains("given twice", (await Assert.ThrowsAsync<ToolError>(() => Launch(build.Path, build.Path))).Message);
        Assert.Contains("inside the game's mods folder", (await Assert.ThrowsAsync<ToolError>(() => Launch(installed))).Message);
        Assert.False(launcher.HasSession);
        Assert.True(File.Exists(Path.Combine(installed, "MyMod.json")));
    }

    [Fact]
    public void TailReadsTheEndAndFilters()
    {
        using var data = new TempDir();
        var paths = new GamePaths { DataDir = data.Path };
        File.WriteAllLines(Path.Combine(data.Path, "mymod_log.txt"), Enumerable.Range(1, 50).Select(i => $"line {i}{(i % 10 == 0 ? " ERROR" : "")}"));
        string tail = GameLogs.Tail(paths, "mymod_log.txt", 3, null, null);
        Assert.EndsWith("line 48\nline 49\nline 50 ERROR", tail);
        string errors = GameLogs.Tail(paths, "mymod_log.txt", 100, "error", null);
        Assert.Equal(5, errors.Split('\n').Count(l => l.Contains("ERROR")));
        Assert.Throws<ToolError>(() => GameLogs.Tail(paths, "../secrets.txt", 3, null, null));
    }
}

public class BridgeClientTests
{
    /// <summary>A stand-in for the in-game bridge: checks the token, answers ping and echo, fails anything else.</summary>
    private sealed class FakeBridge : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public const string Token = "secret";
        public int Connections;

        public FakeBridge()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient c;
                    try { c = await _listener.AcceptTcpClientAsync(); } catch { return; }
                    Interlocked.Increment(ref Connections);
                    _ = Task.Run(() => Serve(c));
                }
            });
        }

        private static async Task Serve(TcpClient c)
        {
            using (c)
            {
                var s = c.GetStream();
                var reader = new StreamReader(s);
                var writer = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                bool authed = false;
                while (await reader.ReadLineAsync() is { } line)
                {
                    var req = Wire.Decode<BridgeRequest>(line)!;
                    BridgeResponse res = req.Cmd switch
                    {
                        "hello" when Args.Str(req.Args, "token") == Token => Ok(req, new JsonObject(), () => authed = true),
                        "hello" => BridgeResponse.Failure(req.Id, "wrong token"),
                        _ when !authed => BridgeResponse.Failure(req.Id, "hello first"),
                        "ping" => BridgeResponse.Success(req.Id, new JsonObject { ["pong"] = true }),
                        "echo" => BridgeResponse.Success(req.Id, req.Args?.DeepClone()),
                        "drop" => null!,
                        _ => BridgeResponse.Failure(req.Id, $"unknown command '{req.Cmd}'"),
                    };
                    if (res is null) return;   // hang up
                    await writer.WriteLineAsync(Wire.Encode(res));
                }
            }
        }

        private static BridgeResponse Ok(BridgeRequest r, JsonNode n, Action then) { then(); return BridgeResponse.Success(r.Id, n); }

        public void Dispose() => _listener.Stop();
    }

    private static InstanceInfo Info(int port, string token = FakeBridge.Token) =>
        new() { Pid = Environment.ProcessId, Port = port, Token = token, ClientId = 1001 };

    [Fact]
    public async Task SaysHelloThenCalls()
    {
        using var fake = new FakeBridge();
        await using var client = new BridgeClient(Info(fake.Port));
        var r = await client.CallAsync("echo", new JsonObject { ["x"] = 1 }, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(1, r!["x"]!.GetValue<int>());
        var p = await client.CallAsync("ping", null, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(p!["pong"]!.GetValue<bool>());
        Assert.Equal(1, fake.Connections);   // one connection reused
    }

    [Fact]
    public async Task BridgeErrorsBecomeToolErrors()
    {
        using var fake = new FakeBridge();
        await using var client = new BridgeClient(Info(fake.Port));
        var e = await Assert.ThrowsAsync<ToolError>(() => client.CallAsync("nope", null, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Contains("unknown command", e.Message);
    }

    [Fact]
    public async Task WrongTokenIsReported()
    {
        using var fake = new FakeBridge();
        await using var client = new BridgeClient(Info(fake.Port, "wrong"));
        var e = await Assert.ThrowsAsync<ToolError>(() => client.CallAsync("ping", null, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Contains("wrong token", e.Message);
    }

    [Fact]
    public async Task ReconnectsAfterTheGameHangsUp()
    {
        using var fake = new FakeBridge();
        await using var client = new BridgeClient(Info(fake.Port));
        await Assert.ThrowsAsync<ToolError>(() => client.CallAsync("drop", null, TimeSpan.FromSeconds(5), CancellationToken.None));
        var p = await client.CallAsync("ping", null, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(p!["pong"]!.GetValue<bool>());
    }

    [Fact]
    public async Task NoListenerIsAClearError()
    {
        int port;
        using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); }
        await using var client = new BridgeClient(Info(port));
        var e = await Assert.ThrowsAsync<ToolError>(() => client.CallAsync("ping", null, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Contains("lost the connection", e.Message);
    }
}

public class RegistryTests
{
    [Fact]
    public void FindsLiveCopiesHostFirstAndDropsStaleFiles()
    {
        using var data = new TempDir();
        var paths = new GamePaths { DataDir = data.Path };
        string dir = BridgePaths.Instances(data.Path);
        Directory.CreateDirectory(dir);

        // One live copy (this test process stands in for the game) and one dead pid.
        InstanceRegistry.IsTestHost = p => p.Id == Environment.ProcessId;
        int me = Environment.ProcessId;
        File.WriteAllText(Path.Combine(dir, $"{me}.json"), Wire.Encode(new InstanceInfo { Pid = me, ClientId = 1002, FastMp = "join", StartedAt = DateTimeOffset.Now.ToString("o") }));
        File.WriteAllText(Path.Combine(dir, "999999.json"), Wire.Encode(new InstanceInfo { Pid = 999999, ClientId = 1003 }));

        var registry = new InstanceRegistry(paths);
        var list = registry.List();
        Assert.Single(list);
        Assert.False(File.Exists(Path.Combine(dir, "999999.json")));
        Assert.Equal(1002, registry.Resolve("1002").Info.ClientId);
        Assert.Equal(1002, registry.Resolve(null).Info.ClientId);
        Assert.Throws<ToolError>(() => registry.Resolve("1009"));
        Assert.Throws<ToolError>(() => registry.Resolve("banana"));
    }

    [Fact]
    public void NothingRunningIsAClearError()
    {
        using var data = new TempDir();
        var e = Assert.Throws<ToolError>(() => new InstanceRegistry(new GamePaths { DataDir = data.Path }).Resolve(null));
        Assert.Contains("launch", e.Message);
    }
}

public class InspectToolTests
{
    [Fact]
    public void RunsTheInspectorInProcess()
    {
        var tools = new Tools(new GamePaths { DataDir = Path.GetTempPath() }, new InstanceRegistry(new GamePaths()), null!);
        string usage = tools.Inspect([]);
        Assert.StartsWith("exit 1", usage);
        Assert.Contains("sts2-inspect", usage);

        // Inspecting the test assembly itself proves the command runs end to end.
        string own = typeof(InspectToolTests).Assembly.Location;
        string types = tools.Inspect(["types", own, "=InspectToolTests"]);
        Assert.StartsWith("exit 0", types);
        Assert.Contains("RunsTheInspectorInProcess", types);
    }
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sts2mcp-test-" + Guid.NewGuid().ToString("N")[..8]);
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
}
