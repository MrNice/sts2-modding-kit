using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sts2Bridge.Protocol;

namespace Sts2Mcp;

/// <summary>
/// Starts and stops local test copies of the game (offline profiles 1001, 1002, ...), with the bridge and the mods
/// under test copied into the game's mods folder for the session. A mod folder already there with the same name is
/// moved aside (next to the mods folder, same drive) and put back on stop. Every change is written to the session
/// record before it's made, so a crash or a restarted server can still undo it.
/// </summary>
public sealed class Launcher
{
    public const int FirstClientId = 1001;

    private readonly GamePaths _paths;
    private readonly InstanceRegistry _registry;

    public Launcher(GamePaths paths, InstanceRegistry registry)
    {
        _paths = paths;
        _registry = registry;
    }

    private string RecordFile => Path.Combine(_paths.CacheDir, "session.json");

    /// <summary>Outside mods/ (the game reads mods/ recursively) but on the same drive, so moves are cheap and safe.</summary>
    private string BackupRoot => Path.Combine(_paths.GameDir!, "mods.sts2mcp-backup");

    /// <summary>What a launch changed, so stop can undo it.</summary>
    public sealed record Session
    {
        public List<int> Pids { get; init; } = new();
        /// <summary>Mod folders this session created in the game's mods folder.</summary>
        public List<string> InstalledMods { get; init; } = new();
        /// <summary>Original mod folder → where it was moved for the session.</summary>
        public Dictionary<string, string> Backups { get; init; } = new();
        public string StartedAt { get; init; } = "";
    }

    public bool HasSession => File.Exists(RecordFile);

    /// <param name="keepSavedRun">
    /// Every test session ends mid-run, and the game saves that co-op run in the host's test profile; the next host then
    /// stops at Host from Save / Abandon Save instead of opening a lobby. By default the launcher abandons that saved
    /// test run (test profile only) and hosts a new lobby; with keepSavedRun it stops and says so instead.
    /// </param>
    public async Task<JsonObject> LaunchAsync(int players, IReadOnlyList<string> modFolders, bool allowRunning, Func<string, Task> progress, CancellationToken ct, bool keepSavedRun = false)
    {
        if (players is < 1 or > 4) throw new ToolError("players must be 1 to 4");
        string exe = _paths.Exe ?? throw new ToolError("Slay the Spire 2 not found. Set STS2_GAME_DIR to the game folder.");
        if (!File.Exists(exe)) throw new ToolError($"{exe} not found");
        if (HasSession) throw new ToolError("a test session is already running (or wasn't cleaned up). Call stop first.");
        if (!allowRunning && Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GamePaths.ExeName)).Length > 0)
            throw new ToolError("Slay the Spire 2 is already running. Close it first (a running copy locks mod files), or pass allow_running.");

        var mods = CheckMods(modFolders);
        await progress("Building the bridge for this game version (cached after the first time)...");
        var bridge = await BridgeBuilder.EnsureBuiltAsync(_paths, force: false, ct);
        RemoveLeftoverBridge();

        var session = new Session { StartedAt = DateTimeOffset.Now.ToString("o") };
        var notes = new JsonArray();
        Save(session);
        try
        {
            Install(bridge.ModFolder, BridgeInfo.ModId, session);
            foreach (var (folder, id) in mods) Install(folder, id, session);

            var ids = new[] { BridgeInfo.ModId }.Concat(mods.Select(m => m.Id)).ToList();
            var clientIds = Enumerable.Range(FirstClientId, players).ToList();
            foreach (int clientId in clientIds) ProfileSettings.EnableMods(_paths, clientId, ids);

            await progress($"Starting {(players == 1 ? "the game" : "the host")} (profile {FirstClientId})...");
            StartTracked(exe, FirstClientId, players == 1 ? null : "host", session);
            await WaitForBridgeAsync(session.Pids[0], TimeSpan.FromSeconds(90), ct);

            if (players == 1)
            {
                await WaitForScreenAsync("MainMenu", TimeSpan.FromSeconds(45), ct);   // informational only
            }
            else
            {
                await progress("Host is up; waiting for its lobby...");
                string? lobbyNote = await OpenLobbyAsync(keepSavedRun, progress, ct);
                if (lobbyNote is not null) notes.Add(lobbyNote);
                foreach (int clientId in clientIds.Skip(1))
                {
                    await progress($"Starting client {clientId}...");
                    StartTracked(exe, clientId, "join", session);
                    await WaitForBridgeAsync(session.Pids[^1], TimeSpan.FromSeconds(90), ct);
                }
            }

            return new JsonObject
            {
                ["started"] = players,
                ["game"] = _paths.GameVersion(),
                ["bridge"] = bridge.Built ? "built for this game version" : "cached build",
                ["mods"] = new JsonArray(ids.Select(i => (JsonNode)i).ToArray()),
                ["instances"] = new JsonArray(_registry.List().Select(i => (JsonNode)new JsonObject
                {
                    ["clientId"] = i.ClientId,
                    ["role"] = i.FastMp ?? "solo",
                    ["pid"] = i.Pid,
                }).ToArray()),
                ["notes"] = notes.Count > 0 ? notes : null,
                ["next"] = players > 1
                    ? "All copies should be in the co-op lobby (check with screen or screenshot). Pick characters with buttons/press, then start the run from the host."
                    : "The game should be at the main menu (check with screen).",
            };
        }
        catch
        {
            // Undo from the in-memory record (also on disk), and don't let a cancelled call skip the cleanup.
            await StopAsync(CancellationToken.None, session);
            throw;
        }
    }

    public async Task<JsonObject> StopAsync(CancellationToken ct, Session? inMemory = null)
    {
        Session? session = inMemory ?? Load();
        var closed = new JsonArray();
        // Also close bridge copies started some other way (e.g. by hand with test-coop.bat); every pid is checked
        // to still be a game copy before it's touched.
        var pids = (session?.Pids ?? new List<int>()).Where(p => InstanceRegistry.IsAlive(p))
            .Concat(_registry.List().Select(i => i.Pid))
            .Distinct()
            .ToList();

        foreach (int pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited) continue;
                p.CloseMainWindow();
                if (!p.WaitForExit(5000)) p.Kill(entireProcessTree: true);
                closed.Add(pid);
            }
            catch { /* already gone */ }
            try { File.Delete(BridgePaths.InstanceFile(_paths.DataDir, pid)); } catch { }
        }
        // Mod files can stay locked for a moment after the process exits.
        if (pids.Count > 0) await Task.Delay(1500, CancellationToken.None);

        var removed = new JsonArray();
        var restored = new JsonArray();
        var problems = new JsonArray();
        if (session is not null)
        {
            foreach (string folder in session.InstalledMods)
            {
                try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); removed.Add(Path.GetFileName(folder)); }
                catch (Exception e) { problems.Add($"could not remove {folder}: {e.Message}"); }
            }
            foreach (var (original, backup) in session.Backups)
            {
                if (!Directory.Exists(backup)) continue;   // never moved (the launch stopped before that step)
                if (Directory.Exists(original))
                {
                    problems.Add($"{original} exists again, so your original copy was left at {backup}; move it back by hand");
                    continue;
                }
                try
                {
                    Directory.Move(backup, original);
                    restored.Add(Path.GetFileName(original));
                }
                catch (Exception e) { problems.Add($"could not restore {original} from {backup}: {e.Message}"); }
            }
            if (problems.Count == 0)
            {
                try { File.Delete(RecordFile); } catch { }
                try { if (Directory.Exists(BackupRoot) && !Directory.EnumerateFileSystemEntries(BackupRoot).Any()) Directory.Delete(BackupRoot); } catch { }
            }
        }

        return new JsonObject
        {
            ["closed"] = closed,
            ["removedMods"] = removed,
            ["restoredMods"] = restored,
            ["problems"] = problems.Count > 0 ? problems : null,
        };
    }

    // ---- checks ----

    /// <summary>Validates the mods to test before anything is changed.</summary>
    private List<(string Folder, string Id)> CheckMods(IReadOnlyList<string> modFolders)
    {
        var mods = modFolders.Select(ReadModFolder).ToList();
        string modsDir = Path.GetFullPath(_paths.ModsDir!);
        foreach (var group in mods.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
            if (group.Count() > 1) throw new ToolError($"mod id {group.Key} is given twice");
        foreach (var (folder, id) in mods)
        {
            if (id.Equals(BridgeInfo.ModId, StringComparison.OrdinalIgnoreCase)) throw new ToolError($"{folder} is the bridge itself; launch adds it automatically");
            string full = Path.GetFullPath(folder);
            if (IsInside(full, modsDir))
                throw new ToolError($"{folder} is inside the game's mods folder. Point at your build output (the folder your build copies from), not the installed copy.");
            var elsewhere = FindManifests(modsDir, id).Where(d => !PathEquals(d, Path.Combine(modsDir, id))).ToList();
            if (elsewhere.Count > 0)
                throw new ToolError($"another copy of {id} is in the mods folder at {string.Join(", ", elsewhere)}; the game would see two mods with the same id. Move it out first.");
        }
        return mods;
    }

    /// <summary>A bridge left in mods/ by a session that never got cleaned up (the record is gone): it's ours, remove it.</summary>
    private void RemoveLeftoverBridge()
    {
        string folder = Path.Combine(_paths.ModsDir!, BridgeInfo.ModId);
        string manifest = Path.Combine(folder, BridgeInfo.ModId + ".json");
        if (!File.Exists(manifest)) return;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(manifest));
            if (node?["id"]?.ToString() == BridgeInfo.ModId) Directory.Delete(folder, recursive: true);
        }
        catch { /* leave it; Install will back it up instead */ }
    }

    /// <summary>Folders under <paramref name="modsDir"/> holding a manifest whose id is <paramref name="id"/>.</summary>
    public static List<string> FindManifests(string modsDir, string id)
    {
        var found = new List<string>();
        if (!Directory.Exists(modsDir)) return found;
        foreach (string json in Directory.EnumerateFiles(modsDir, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                if (new FileInfo(json).Length > 64 * 1024) continue;
                if (JsonNode.Parse(File.ReadAllText(json))?["id"]?.ToString() is { } found_id
                    && found_id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    found.Add(Path.GetDirectoryName(json)!);
            }
            catch { /* not a manifest */ }
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---- helpers ----

    private void StartTracked(string exe, int clientId, string? fastMp, Session session)
    {
        var p = StartGame(exe, clientId, fastMp);
        session.Pids.Add(p.Id);
        Save(session);
    }

    /// <summary>
    /// Starts a game copy as its own program (shell execute), so it doesn't inherit this process's console: under
    /// MCP, stdout is the protocol channel, and the game prints its log there.
    /// </summary>
    private Process StartGame(string exe, int clientId, string? fastMp)
    {
        string args = $"{BridgeInfo.OfflineArg} --clientId={clientId}" + (fastMp is null ? "" : $" --fastmp={fastMp}");
        var psi = new ProcessStartInfo(exe, args) { WorkingDirectory = _paths.GameDir!, UseShellExecute = true };
        return Process.Start(psi) ?? throw new ToolError($"could not start {exe}");
    }

    /// <summary>Closes one copy and starts it again with the same profile and role (e.g. a client that failed to join).</summary>
    public async Task<JsonObject> RelaunchAsync(string? player, CancellationToken ct)
    {
        var session = Load() ?? throw new ToolError("no test session is running (relaunch only restarts copies started by launch)");
        var old = _registry.Resolve(player).Info;
        int clientId = old.ClientId ?? throw new ToolError("that copy has no client id");
        try
        {
            using var p = Process.GetProcessById(old.Pid);
            p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);
        }
        catch { /* already gone */ }
        try { File.Delete(BridgePaths.InstanceFile(_paths.DataDir, old.Pid)); } catch { }
        await Task.Delay(1500, ct);

        var fresh = StartGame(_paths.Exe!, clientId, old.FastMp);
        session.Pids.Remove(old.Pid);
        session.Pids.Add(fresh.Id);
        Save(session);
        await WaitForBridgeAsync(fresh.Id, TimeSpan.FromSeconds(90), ct);
        return new JsonObject { ["relaunched"] = clientId, ["role"] = old.FastMp ?? "solo", ["pid"] = fresh.Id };
    }

    private async Task WaitForBridgeAsync(int pid, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (!InstanceRegistry.IsAlive(pid)) throw new ToolError($"the game (pid {pid}) exited during startup. Check tail_log.");
            if (_registry.List().Any(i => i.Pid == pid)) return;
            await Task.Delay(500, ct);
        }
        throw new ToolError($"the game (pid {pid}) started but the bridge didn't report in within {timeout.TotalSeconds:0}s. " +
                            "If the game asked to enable mods, choose not to restart, call stop, and launch again.");
    }

    /// <summary>Waits until the host's screen description mentions <paramref name="text"/>; false on timeout.</summary>
    private async Task<bool> WaitForScreenAsync(string text, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        var host = _registry.Resolve("host");
        while (DateTime.UtcNow < until)
        {
            try
            {
                var screen = await host.CallAsync("screen", null, TimeSpan.FromSeconds(5), ct);
                if ((screen?.ToJsonString() ?? "").Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (ToolError) { /* still loading */ }
            await Task.Delay(1000, ct);
        }
        return false;
    }

    /// <summary>
    /// Gets the host into the co-op lobby. --fastmp=host normally goes straight there; when the profile has a saved
    /// co-op run, the game shows the multiplayer menu instead (Host from Save / Abandon Save / Join), and when that
    /// happens the saved test run is abandoned (unless kept) and Host pressed. Returns a note when it had to step in.
    /// </summary>
    private async Task<string?> OpenLobbyAsync(bool keepSavedRun, Func<string, Task> progress, CancellationToken ct)
    {
        var host = _registry.Resolve("host");
        var until = DateTime.UtcNow.AddSeconds(60);
        string? note = null;
        while (DateTime.UtcNow < until)
        {
            string screen = "";
            try { screen = (await host.CallAsync("screen", null, TimeSpan.FromSeconds(5), ct))?["screen"]?.ToString() ?? ""; }
            catch (ToolError) { /* still loading */ }

            if (screen.Contains("CharacterSelect", StringComparison.OrdinalIgnoreCase)) return note;

            if (screen.Contains("MultiplayerSubmenu", StringComparison.OrdinalIgnoreCase))
            {
                int saved = await CountAsync(host, "AbandonButton", ct);
                if (saved > 0)
                {
                    if (keepSavedRun)
                        throw new ToolError($"the host's test profile ({FirstClientId}) has a saved co-op run, and you asked to keep it. " +
                                            "Continue it by hand (Host from Save) or launch again without keep_saved_run to abandon it.");
                    await progress("The host's test profile has a saved co-op run from an earlier session; abandoning it...");
                    await PressAsync(host, "AbandonButton", ct);
                    await PressAsync(host, "YesButton", ct);
                    note = $"abandoned the co-op run saved in test profile {FirstClientId} by an earlier session";
                    await Task.Delay(1000, ct);
                    continue;
                }
                if (await CountAsync(host, "HostButton", ct) > 0)
                {
                    await PressAsync(host, "HostButton", ct);
                    await Task.Delay(1500, ct);
                    continue;
                }
            }
            await Task.Delay(1000, ct);
        }
        throw new ToolError(await HostStuckMessageAsync(ct));
    }

    private static async Task<int> CountAsync(BridgeClient c, string match, CancellationToken ct)
    {
        try { return (await c.CallAsync("buttons", new JsonObject { ["match"] = match }, TimeSpan.FromSeconds(5), ct))?["count"]?.GetValue<int>() ?? 0; }
        catch (ToolError) { return 0; }
    }

    /// <summary>Presses a button by name, waiting up to 10 s for it to be there and enabled.</summary>
    private static async Task PressAsync(BridgeClient c, string name, CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                await c.CallAsync("press", new JsonObject { ["text"] = name }, TimeSpan.FromSeconds(5), ct);
                return;
            }
            catch (ToolError e) when (e.Retry && DateTime.UtcNow < until) { await Task.Delay(200, ct); }
        }
    }

    /// <summary>Why the host didn't open a lobby, as far as can be told. The copies are closed afterwards.</summary>
    private async Task<string> HostStuckMessageAsync(CancellationToken ct)
    {
        string where = "unknown";
        try
        {
            var host = _registry.Resolve("host");
            where = (await host.CallAsync("screen", null, TimeSpan.FromSeconds(5), ct))?["screen"]?.ToString() ?? "unknown";
            var buttons = (await host.CallAsync("buttons", new JsonObject { ["match"] = "AbandonButton" }, TimeSpan.FromSeconds(5), ct))?["count"]?.GetValue<int>() ?? 0;
            if (buttons > 0)
                return $"the host's profile ({FirstClientId}) has a saved co-op run, so the game shows Host from Save / Abandon Save instead of opening a lobby. " +
                       "Launch with players=1, press Abandon Save and Yes (buttons/press), stop, then launch again. Or keep the save and continue it by hand.";
        }
        catch (ToolError) { }
        return $"the host didn't reach the co-op lobby within 45 s (its screen: {where}). Check screenshot and tail_log, then stop.";
    }

    private void Install(string sourceFolder, string id, Session session)
    {
        string modsDir = _paths.ModsDir!;
        Directory.CreateDirectory(modsDir);
        string target = Path.Combine(modsDir, id);
        if (Directory.Exists(target))
        {
            Directory.CreateDirectory(BackupRoot);
            string backup = Path.Combine(BackupRoot, $"{id}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
            session.Backups[target] = backup;
            Save(session);                       // record first: if the move half-happens, stop still knows where
            Directory.Move(target, backup);
        }
        session.InstalledMods.Add(target);
        Save(session);
        CopyMod(sourceFolder, target);
    }

    private static void CopyMod(string from, string to)
    {
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)) continue;
            string dest = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    /// <summary>A built mod folder: must hold &lt;Id&gt;.json (the manifest) and the dll it names.</summary>
    public static (string Folder, string Id) ReadModFolder(string folder)
    {
        if (!Directory.Exists(folder)) throw new ToolError($"mod folder not found: {folder}");
        foreach (string json in Directory.EnumerateFiles(folder, "*.json"))
        {
            if (json.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || json.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)) continue;
            JsonNode? node;
            try { node = JsonNode.Parse(File.ReadAllText(json)); }
            catch (JsonException) { continue; }
            if (node?["id"]?.ToString() is not { Length: > 0 } id || node["has_dll"] is null) continue;
            bool hasDll = node["has_dll"]?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true;
            if (hasDll && !File.Exists(Path.Combine(folder, id + ".dll")))
                throw new ToolError($"{folder} has the manifest for {id} but no {id}.dll. Build the mod first.");
            return (folder, id);
        }
        throw new ToolError($"{folder} has no mod manifest (<Id>.json with id and has_dll). Point at the folder with the built dll and manifest.");
    }

    private static bool IsInside(string path, string dir)
    {
        string d = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return (path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .StartsWith(d, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);

    private void Save(Session s)
    {
        Directory.CreateDirectory(_paths.CacheDir);
        string tmp = RecordFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, RecordFile, overwrite: true);
    }

    private Session? Load()
    {
        try { return File.Exists(RecordFile) ? JsonSerializer.Deserialize<Session>(File.ReadAllText(RecordFile)) : null; }
        catch { return null; }
    }
}

/// <summary>Turns mods on in an offline test profile's settings, so the game doesn't stop to ask.</summary>
public static class ProfileSettings
{
    public static void EnableMods(GamePaths paths, int clientId, IReadOnlyList<string> modIds)
    {
        string file = paths.ProfileSettings(clientId);
        if (!File.Exists(file))
        {
            // A new profile: start from profile 1001's settings when there are some; otherwise the game creates
            // defaults and may ask about mods once.
            string template = paths.ProfileSettings(Launcher.FirstClientId);
            if (clientId == Launcher.FirstClientId || !File.Exists(template)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.Copy(template, file);
        }

        string text = File.ReadAllText(file);
        string patched = Patch(text, modIds);
        if (patched == text) return;
        string backup = file + ".sts2mcp.bak";
        if (!File.Exists(backup)) File.Copy(file, backup);
        File.WriteAllText(file, patched);
    }

    /// <summary>Sets mod_settings.mods_enabled and adds or enables a mods_directory entry for each id.</summary>
    public static string Patch(string settingsJson, IReadOnlyList<string> modIds)
    {
        var root = JsonNode.Parse(settingsJson) as JsonObject ?? throw new ToolError("settings.save isn't a JSON object");
        var ms = root["mod_settings"] as JsonObject;
        if (ms is null) root["mod_settings"] = ms = new JsonObject();
        ms["mods_enabled"] = true;
        var list = ms["mod_list"] as JsonArray;
        if (list is null) ms["mod_list"] = list = new JsonArray();
        foreach (string id in modIds)
        {
            var entry = list.OfType<JsonObject>().FirstOrDefault(e => e["id"]?.ToString() == id && e["source"]?.ToString() == "mods_directory");
            if (entry is null) list.Add(new JsonObject { ["id"] = id, ["is_enabled"] = true, ["source"] = "mods_directory" });
            else entry["is_enabled"] = true;
        }
        string result = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return JsonNode.DeepEquals(JsonNode.Parse(settingsJson), root) ? settingsJson : result;
    }
}
