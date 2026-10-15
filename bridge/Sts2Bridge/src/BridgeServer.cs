using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Godot;
using Sts2Bridge.Protocol;

namespace Sts2Bridge;

/// <summary>
/// Takes requests from two places and runs them on the game's main thread:
/// a TCP listener on 127.0.0.1 (one JSON object per line, after a "hello" with the token), and a file queue
/// (one request per file in queue/&lt;clientId&gt;/in, the response appears in queue/&lt;clientId&gt;/out).
/// Announces itself with a discovery file so tools can find the port and token.
/// </summary>
internal static class BridgeServer
{
    private sealed record Job(BridgeRequest Request, TaskCompletionSource<BridgeResponse> Done);

    private const int MaxConnections = 8;
    private const int MaxJobsPerFrame = 16;
    private const double QueueScanSeconds = 0.2;

    private static readonly ConcurrentQueue<Job> Jobs = new();
    private static TcpListener? _listener;
    private static int _connections;
    private static double _sinceScan;
    private static DateTime _lastCleanup = DateTime.MinValue;
    private static string _token = "";
    private static string _instanceFile = "";
    private static string _queueIn = "", _queueOut = "";
    public static InstanceInfo Info { get; private set; } = new();

    public static void Start(int? clientId, string? fastMp)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            Log.Error("No SceneTree at mod init; bridge inactive");
            return;
        }

        string dataDir = OS.GetUserDataDir();
        _token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        int port = 0;
        foreach (int candidate in BridgeInfo.CandidatePorts(clientId))
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, candidate);
                l.Start();
                _listener = l;
                port = candidate;
                break;
            }
            catch (SocketException) { /* taken; try the next one */ }
        }
        if (_listener is null) Log.Warn("No free port; TCP is off, the file queue still works");

        string queue = BridgePaths.Queue(dataDir, clientId);
        _queueIn = Path.Combine(queue, "in");
        _queueOut = Path.Combine(queue, "out");
        Directory.CreateDirectory(_queueIn);
        Directory.CreateDirectory(_queueOut);

        int pid = OS.GetProcessId();
        Info = new InstanceInfo
        {
            Pid = pid,
            Port = port,
            Token = _token,
            ClientId = clientId,
            GameVersion = GameVersion(),
            BridgeVersion = BridgeInfo.BridgeVersion,
            Protocol = BridgeInfo.ProtocolVersion,
            QueueDir = queue,
            StartedAt = DateTimeOffset.Now.ToString("o"),
            FastMp = fastMp,
        };
        _instanceFile = BridgePaths.InstanceFile(dataDir, pid);
        Directory.CreateDirectory(Path.GetDirectoryName(_instanceFile)!);
        WriteAtomically(_instanceFile, Wire.Encode(Info));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();

        tree.ProcessFrame += Tick;
        if (_listener is not null) _ = Task.Run(AcceptLoop);
        GameEvents.Start();
        Log.Info($"Bridge {BridgeInfo.BridgeVersion} on 127.0.0.1:{port}, queue {queue}, game {Info.GameVersion}");
    }

    private static void Stop()
    {
        try { _listener?.Stop(); } catch { }
        try { if (File.Exists(_instanceFile)) File.Delete(_instanceFile); } catch { }
    }

    /// <summary>Main thread, once per frame. Each part is guarded on its own so one failure can't stop the others.</summary>
    private static void Tick()
    {
        Guarded("events", GameEvents.Poll);
        Guarded("queue", () =>
        {
            var tree = (SceneTree)Engine.GetMainLoop();
            _sinceScan += tree.Root.GetProcessDeltaTime();
            if (_sinceScan < QueueScanSeconds) return;
            _sinceScan = 0;
            ScanQueue();
        });
        Guarded("jobs", () =>
        {
            for (int i = 0; i < MaxJobsPerFrame && Jobs.TryDequeue(out var job); i++)
                _ = Run(job);
        });
    }

    private static readonly Dictionary<string, DateTime> LastFailure = new();

    private static void Guarded(string part, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            // A failure that repeats every frame is logged at most every 10 seconds.
            if (LastFailure.TryGetValue(part, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(10)) return;
            LastFailure[part] = DateTime.UtcNow;
            Log.Error($"Tick ({part}) failed", ex);
        }
    }

    private static async Task Run(Job job)
    {
        if (job.Request.Expired(DateTimeOffset.UtcNow))
        {
            job.Done.TrySetResult(BridgeResponse.Failure(job.Request.Id, "expired before the game got to it (the caller had already timed out); not run"));
            return;
        }
        try
        {
            var result = await Commands.Handle(job.Request.Cmd!, job.Request.Args);
            job.Done.TrySetResult(BridgeResponse.Success(job.Request.Id, result));
        }
        catch (BridgeError e)
        {
            job.Done.TrySetResult(BridgeResponse.Failure(job.Request.Id, e.Message, e.Retry));
        }
        catch (Exception e)
        {
            Log.Error($"Command {job.Request.Cmd} failed", e);
            job.Done.TrySetResult(BridgeResponse.Failure(job.Request.Id, $"{e.GetType().Name}: {e.Message}"));
        }
    }

    private static Task<BridgeResponse> Enqueue(BridgeRequest request)
    {
        var job = new Job(request, new TaskCompletionSource<BridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously));
        Jobs.Enqueue(job);
        return job.Done.Task;
    }

    // ---- TCP ----

    private static async Task AcceptLoop()
    {
        while (_listener is not null)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { return; }

            if (Interlocked.Increment(ref _connections) > MaxConnections)
            {
                Interlocked.Decrement(ref _connections);
                client.Dispose();
                continue;
            }
            _ = Task.Run(() => Serve(client));
        }
    }

    private static async Task Serve(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 8192);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                bool authed = false;

                while (await reader.ReadLineAsync() is { } line)
                {
                    if (line.Length == 0) continue;
                    if (line.Length > Wire.MaxLineLength)
                    {
                        await writer.WriteLineAsync(Wire.Encode(BridgeResponse.Failure(0, "request too long")));
                        return;
                    }

                    BridgeRequest? request;
                    try { request = Wire.Decode<BridgeRequest>(line); }
                    catch (Exception e) { await writer.WriteLineAsync(Wire.Encode(BridgeResponse.Failure(0, $"bad JSON: {e.Message}"))); continue; }
                    if (request is null || string.IsNullOrEmpty(request.Cmd))
                    {
                        await writer.WriteLineAsync(Wire.Encode(BridgeResponse.Failure(request?.Id ?? 0, "missing cmd")));
                        continue;
                    }

                    BridgeResponse response;
                    if (request.Cmd == "hello")
                    {
                        authed = CryptographicOperations.FixedTimeEquals(
                            Encoding.UTF8.GetBytes(Args.Str(request.Args, "token") ?? ""), Encoding.UTF8.GetBytes(_token));
                        response = authed
                            ? BridgeResponse.Success(request.Id, System.Text.Json.Nodes.JsonNode.Parse(Wire.Encode(Info with { Token = "" })))
                            : BridgeResponse.Failure(request.Id, "wrong token");
                    }
                    else if (!authed)
                    {
                        response = BridgeResponse.Failure(request.Id, "send hello with the token from the instance file first");
                    }
                    else
                    {
                        response = await Enqueue(request);
                    }
                    await writer.WriteLineAsync(Wire.Encode(response));
                }
            }
        }
        catch (IOException) { /* client went away */ }
        catch (Exception e) { Log.Error("Connection failed", e); }
        finally { Interlocked.Decrement(ref _connections); }
    }

    // ---- File queue ----

    /// <summary>
    /// Requests are <c>in/&lt;name&gt;.json</c> (write to a temp name, then rename to .json). The response is written
    /// to <c>out/&lt;name&gt;.json</c> the same way. No token: whoever can write here is already the local user.
    /// </summary>
    private static void ScanQueue()
    {
        if (DateTime.UtcNow - _lastCleanup > TimeSpan.FromMinutes(1))
        {
            _lastCleanup = DateTime.UtcNow;
            CleanOldResponses();
        }

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(_queueIn, "*.json").OrderBy(f => f, StringComparer.Ordinal).Take(MaxJobsPerFrame).ToList(); }
        catch { return; }

        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            string text;
            try
            {
                text = File.ReadAllText(file);
                File.Delete(file);
            }
            catch { continue; /* still being written; next scan */ }

            Task<BridgeResponse> reply;
            try
            {
                var request = Wire.Decode<BridgeRequest>(text);
                reply = request is null || string.IsNullOrEmpty(request.Cmd)
                    ? Task.FromResult(BridgeResponse.Failure(0, "missing cmd"))
                    : Enqueue(request);
            }
            catch (Exception e)
            {
                reply = Task.FromResult(BridgeResponse.Failure(0, $"bad JSON: {e.Message}"));
            }

            string outFile = Path.Combine(_queueOut, name);
            reply.ContinueWith(t =>
            {
                try { WriteAtomically(outFile, Wire.Encode(t.Result)); }
                catch (Exception e) { Log.Error($"Writing {outFile} failed", e); }
            }, TaskScheduler.Default);
        }
    }

    /// <summary>Responses nobody collected (some readers can't delete files) are removed after ten minutes.</summary>
    private static void CleanOldResponses()
    {
        try
        {
            foreach (var f in new DirectoryInfo(_queueOut).EnumerateFiles())
                if (DateTime.UtcNow - f.LastWriteTimeUtc > TimeSpan.FromMinutes(10)) f.Delete();
        }
        catch { /* best effort */ }
    }

    private static void WriteAtomically(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    private static string GameVersion()
    {
        try
        {
            string dir = Path.GetDirectoryName(OS.GetExecutablePath()) ?? "";
            string file = Path.Combine(dir, "release_info.json");
            if (!File.Exists(file)) return "unknown";
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file));
            return node?["version"]?.ToString() ?? "unknown";
        }
        catch { return "unknown"; }
    }
}

/// <summary>
/// A failure to report to the caller as-is (no stack trace in the log). <paramref name="retry"/>: "not yet", the same
/// request may work a moment later (see BridgeResponse.Retry).
/// </summary>
internal sealed class BridgeError(string message, bool retry = false) : Exception(message)
{
    public bool Retry { get; } = retry;

    public static BridgeError NotYet(string message) => new(message, retry: true);
}
