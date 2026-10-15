using System.Diagnostics;
using System.Security.Cryptography;
using Sts2Bridge.Protocol;

namespace Sts2Mcp;

/// <summary>
/// Builds the Sts2Bridge mod from the source shipped with this tool, against the installed game's sts2.dll, and
/// caches the result per game build. A game update or branch switch gets a fresh build automatically.
/// </summary>
public static class BridgeBuilder
{
    public sealed record Result(string ModFolder, bool Built, string Log);

    /// <summary>Folder with Sts2Bridge.dll + Sts2Bridge.json for the current game, building it if needed.</summary>
    public static async Task<Result> EnsureBuiltAsync(GamePaths paths, bool force, CancellationToken ct)
    {
        string dll = paths.Sts2Dll ?? throw new ToolError("Slay the Spire 2 not found. Set STS2_GAME_DIR to the game folder.");
        if (!File.Exists(dll)) throw new ToolError($"{dll} not found. Is the game installed there?");

        string shipped = Path.Combine(AppContext.BaseDirectory, "bridge-src");
        if (!Directory.Exists(shipped)) throw new ToolError($"bridge source missing from the tool install ({shipped})");
        // Rebuild when the game changes or when the bridge's own source does (not only on a version bump).
        string key = $"{BridgeInfo.BridgeVersion}-{Hash(dll)}-{SourceHash(shipped)}";
        string root = Path.Combine(paths.CacheDir, "bridge", key);
        string output = Path.Combine(root, "out");
        string builtDll = Path.Combine(output, "Sts2Bridge.dll");
        if (!force && File.Exists(builtDll) && File.Exists(Path.Combine(output, "Sts2Bridge.json")))
            return new Result(output, false, "cached");

        string src = Path.Combine(root, "src");
        if (Directory.Exists(src)) Directory.Delete(src, recursive: true);
        CopyDirectory(shipped, src);
        Directory.CreateDirectory(output);

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in new[]
                 {
                     "build", Path.Combine(src, "Sts2Bridge", "Sts2Bridge.csproj"), "-c", "Release", "-nologo",
                     $"-p:Sts2Dir={paths.GameDir}", "-o", output,
                 })
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new ToolError("could not start `dotnet` (is the .NET SDK installed and on PATH?)");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        string log = (await stdout) + (await stderr);

        if (process.ExitCode != 0 || !File.Exists(builtDll))
        {
            var errors = log.Split('\n').Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).Distinct().Take(15);
            throw new ToolError($"building the bridge mod failed (exit {process.ExitCode}). This usually means the game changed in a way the bridge doesn't handle yet.\n{string.Join("\n", errors)}");
        }
        return new Result(output, true, "built");
    }

    private static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream))[..16].ToLowerInvariant();
    }

    /// <summary>A short hash of every shipped bridge source file (names and contents).</summary>
    public static string SourceHash(string dir)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                     .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj"))
                     .OrderBy(f => Path.GetRelativePath(dir, f).Replace('\\', '/'), StringComparer.Ordinal))
        {
            sha.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(dir, file).Replace('\\', '/')));
            // Line endings differ between checkouts; they don't change the build.
            sha.AppendData(System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(file).Replace("\r\n", "\n")));
        }
        return Convert.ToHexString(sha.GetHashAndReset())[..12].ToLowerInvariant();
    }

    internal static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(from, file);
            if (rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj")) continue;
            string dest = Path.Combine(to, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }
}

/// <summary>
/// A failure to show the agent as-is (McpException messages reach the caller). <paramref name="retry"/>: the game said
/// "not yet", so the same call may work a moment later.
/// </summary>
public sealed class ToolError(string message, bool retry = false) : ModelContextProtocol.McpException(message)
{
    public bool Retry { get; } = retry;
}
