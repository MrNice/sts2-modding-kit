using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Sts2Bridge.Protocol;

namespace Sts2Mcp;

/// <summary>One TCP connection to one game copy's bridge. Requests are sent one at a time.</summary>
public sealed class BridgeClient : IAsyncDisposable
{
    private readonly InstanceInfo _info;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TcpClient? _tcp;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private long _nextId = 1;

    public BridgeClient(InstanceInfo info) => _info = info;

    public InstanceInfo Info => _info;

    public async Task<JsonNode?> CallAsync(string cmd, JsonObject? args, TimeSpan timeout, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    await ConnectAsync(ct);
                    return await SendAsync(cmd, args, timeout, ct);
                }
                catch (Exception e) when (attempt == 0 && e is IOException or SocketException or ObjectDisposedException)
                {
                    Reset();   // the game may have dropped the connection; try once more on a new one
                }
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            Reset();
            throw new ToolError($"lost the connection to game copy {Name} ({e.Message}). Is it still running?");
        }
        finally
        {
            _gate.Release();
        }
    }

    private string Name => _info.ClientId?.ToString() ?? $"pid {_info.Pid}";

    private async Task ConnectAsync(CancellationToken ct)
    {
        if (_tcp is { Connected: true }) return;
        Reset();
        if (_info.Port <= 0) throw new ToolError($"game copy {Name} has no TCP port (only its file queue is available)");
        var tcp = new TcpClient { NoDelay = true };
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync("127.0.0.1", _info.Port, connect.Token);
        }
        var stream = tcp.GetStream();
        _tcp = tcp;
        _reader = new StreamReader(stream, new UTF8Encoding(false), false, 65536);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        try
        {
            await SendAsync("hello", new JsonObject { ["token"] = _info.Token, ["protocol"] = BridgeInfo.ProtocolVersion }, TimeSpan.FromSeconds(10), ct);
        }
        catch
        {
            Reset();   // never keep a connection that didn't authenticate
            throw;
        }
    }

    private async Task<JsonNode?> SendAsync(string cmd, JsonObject? args, TimeSpan timeout, CancellationToken ct)
    {
        long id = _nextId++;
        long deadline = DateTimeOffset.UtcNow.Add(timeout).ToUnixTimeMilliseconds();
        await _writer!.WriteLineAsync(Wire.Encode(new BridgeRequest { Id = id, Cmd = cmd, Args = args, DeadlineMs = deadline }).AsMemory(), ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        while (true)
        {
            string? line;
            try { line = await _reader!.ReadLineAsync(cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Reset();   // the reply may still arrive later; a fresh connection keeps ids in step
                throw new ToolError($"game copy {Name} didn't answer '{cmd}' within {timeout.TotalSeconds:0}s (paused, loading, or stuck?)");
            }
            if (line is null) throw new IOException("connection closed");
            var response = Wire.Decode<BridgeResponse>(line);
            if (response is null || response.Id != id) continue;
            if (!response.Ok) throw new ToolError($"[{Name}] {response.Error}", response.Retry == true);
            return response.Result;
        }
    }

    private void Reset()
    {
        try { _tcp?.Dispose(); } catch { }
        _tcp = null;
        _reader = null;
        _writer = null;
    }

    public ValueTask DisposeAsync()
    {
        Reset();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Finds running game copies from their discovery files and keeps one client per copy.</summary>
public sealed class InstanceRegistry
{
    private readonly GamePaths _paths;
    private readonly Dictionary<int, BridgeClient> _clients = new();
    private readonly object _lock = new();

    public InstanceRegistry(GamePaths paths) => _paths = paths;

    /// <summary>Running copies, host first, then by client id. Stale files (process gone) are removed.</summary>
    public List<InstanceInfo> List()
    {
        string dir = BridgePaths.Instances(_paths.DataDir);
        var found = new List<InstanceInfo>();
        if (!Directory.Exists(dir)) return found;
        foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
        {
            InstanceInfo? info;
            try { info = Wire.Decode<InstanceInfo>(File.ReadAllText(file)); }
            catch { continue; }
            if (info is null) continue;
            if (!IsAlive(info.Pid, info.StartedAt))
            {
                try { File.Delete(file); } catch { }
                continue;
            }
            found.Add(info);
        }
        return found
            .OrderBy(i => i.FastMp == "host" ? 0 : 1)
            .ThenBy(i => i.ClientId ?? int.MaxValue)
            .ToList();
    }

    /// <summary>
    /// True when <paramref name="pid"/> is still the game copy that wrote the instance file: a running Slay the Spire 2
    /// process that started no later than the file says. Guards against Windows reusing the pid of a closed copy.
    /// </summary>
    public static bool IsAlive(int pid, string? startedAt = null)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            if (!p.ProcessName.StartsWith("SlayTheSpire2", StringComparison.OrdinalIgnoreCase) && !IsTestHost(p)) return false;
            if (startedAt is not null && DateTimeOffset.TryParse(startedAt, out var written)
                && p.StartTime.ToUniversalTime() > written.UtcDateTime.AddSeconds(5))
                return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>The test suite stands in for a game copy with its own process.</summary>
    public static Func<Process, bool> IsTestHost = _ => false;

    /// <summary>
    /// Picks one copy. <paramref name="player"/>: null/"" (the only copy, or the host), "host", "client" (the first
    /// joiner), or a client id such as "1002".
    /// </summary>
    public BridgeClient Resolve(string? player)
    {
        var all = List();
        if (all.Count == 0)
            throw new ToolError("no game copy with the bridge is running. Use launch first (or start the game with --force-steam=off).");

        InstanceInfo pick;
        string p = (player ?? "").Trim().ToLowerInvariant();
        if (p is "" or "host" or "me")
            pick = all[0];
        else if (p is "client" or "join" or "guest")
            pick = all.FirstOrDefault(i => i.FastMp != "host" && !ReferenceEquals(i, all[0])) ?? throw new ToolError("no client copy is running");
        else if (int.TryParse(p, out int id))
            pick = all.FirstOrDefault(i => i.ClientId == id) ?? throw new ToolError($"no running copy with client id {id}. Running: {Describe(all)}");
        else
            throw new ToolError($"unknown player '{player}'. Use host, client, or a client id ({Describe(all)})");

        return ClientFor(pick);
    }

    public List<BridgeClient> All() => List().Select(ClientFor).ToList();

    private BridgeClient ClientFor(InstanceInfo info)
    {
        lock (_lock)
        {
            if (_clients.TryGetValue(info.Pid, out var existing) && existing.Info.Token == info.Token) return existing;
            var client = new BridgeClient(info);
            _clients[info.Pid] = client;
            return client;
        }
    }

    public static string Describe(IEnumerable<InstanceInfo> list) =>
        string.Join(", ", list.Select(i => $"{i.ClientId?.ToString() ?? "?"}{(i.FastMp == "host" ? " (host)" : "")}"));
}
