using System.Diagnostics;
using System.Text.Json.Nodes;
using Sts2Bridge.Protocol;

namespace Sts2Mcp;

/// <summary>The doctor report: what's found, what's missing, and what to do about it.</summary>
public static class Diagnostics
{
    public static async Task<JsonObject> RunAsync(GamePaths paths, InstanceRegistry registry, bool buildBridge, CancellationToken ct)
    {
        var problems = new JsonArray();
        var o = new JsonObject
        {
            ["sts2Mcp"] = typeof(Diagnostics).Assembly.GetName().Version?.ToString(3),
            ["bridgeProtocol"] = BridgeInfo.ProtocolVersion,
        };

        if (paths.GameDir is null)
        {
            problems.Add("Game not found. Set the STS2_GAME_DIR environment variable to the folder with SlayTheSpire2.exe.");
        }
        else
        {
            o["gameDir"] = paths.GameDir;
            o["gameVersion"] = paths.GameVersion() ?? "unknown (no release_info.json)";
            if (!File.Exists(paths.Sts2Dll)) problems.Add($"{paths.Sts2Dll} missing; the install looks incomplete.");
        }

        o["dataDir"] = paths.DataDir;
        if (!Directory.Exists(paths.DataDir)) problems.Add($"{paths.DataDir} doesn't exist yet; run the game once.");
        o["cacheDir"] = paths.CacheDir;

        var profiles = new JsonObject();
        foreach (int id in new[] { 1001, 1002, 1003, 1004 })
        {
            string file = paths.ProfileSettings(id);
            if (!File.Exists(file)) { profiles[id.ToString()] = "not created yet"; continue; }
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(file));
                bool enabled = node?["mod_settings"]?["mods_enabled"]?.GetValue<bool>() == true;
                profiles[id.ToString()] = enabled ? "mods enabled" : "mods not enabled yet (launch turns them on)";
            }
            catch { profiles[id.ToString()] = "unreadable settings.save"; }
        }
        o["testProfiles"] = profiles;

        o["dotnet"] = await DotnetVersionAsync(ct) ?? "not found";
        if (o["dotnet"]!.ToString() == "not found") problems.Add("`dotnet` isn't on PATH; it's needed to build the bridge mod.");

        if (buildBridge && paths.GameDir is not null && File.Exists(paths.Sts2Dll))
        {
            try
            {
                var r = await BridgeBuilder.EnsureBuiltAsync(paths, force: false, ct);
                o["bridgeBuild"] = $"{r.Log}: {r.ModFolder}";
            }
            catch (ToolError e)
            {
                o["bridgeBuild"] = "failed";
                problems.Add(e.Message);
            }
        }

        var running = registry.List();
        if (File.Exists(Path.Combine(paths.CacheDir, "session.json")) && running.Count == 0)
            problems.Add("A previous test session wasn't cleaned up (game closed by hand or a crash). Run stop to remove its mods and restore anything it moved.");
        else if (paths.ModsDir is { } mods && Directory.Exists(Path.Combine(mods, BridgeInfo.ModId)) && !File.Exists(Path.Combine(paths.CacheDir, "session.json")))
            problems.Add($"{Path.Combine(mods, BridgeInfo.ModId)} is installed outside a session; normal play then counts as modded (separate saves). Delete that folder, or launch + stop once to clean it.");
        o["running"] = running.Count == 0 ? "none" : InstanceRegistry.Describe(running);
        int games = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GamePaths.ExeName)).Length;
        if (games > running.Count) o["note"] = $"{games - running.Count} game copy(ies) running without the bridge (normal play, or started without --force-steam=off).";

        o["problems"] = problems.Count == 0 ? "none" : problems;
        return o;
    }

    private static async Task<string?> DotnetVersionAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p is null) return null;
            string v = (await p.StandardOutput.ReadToEndAsync(ct)).Trim();
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0 ? v : null;
        }
        catch { return null; }
    }
}
