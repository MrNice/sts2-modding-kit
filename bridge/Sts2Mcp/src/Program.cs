using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sts2Mcp;

// sts2-mcp            run the MCP server on stdio (what an agent starts)
// sts2-mcp doctor     print the setup check
// sts2-mcp setup      build the bridge for the installed game and print how to register the server
// sts2-mcp stop       close test copies and clean up the mods folder
// sts2-mcp launch [players] [modFolder...]          start test copies (as the launch tool does)
// sts2-mcp call <cmd> [jsonArgs] [player]           send one bridge command, e.g. call state '{"detail":"full"}' client
// sts2-mcp plan <steps.json> [player]               run a plan (as the plan tool does); screenshots saved next to the file

var paths = GamePaths.Discover();
string command = args.FirstOrDefault()?.ToLowerInvariant() ?? "";

switch (command)
{
    case "--version" or "version":
        Console.WriteLine(typeof(Tools).Assembly.GetName().Version?.ToString(3));
        return 0;

    case "doctor" or "setup":
    {
        var registry = new InstanceRegistry(paths);
        var report = await Diagnostics.RunAsync(paths, registry, buildBridge: true, CancellationToken.None);
        Console.WriteLine(report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        if (command == "setup")
        {
            Console.WriteLine();
            Console.WriteLine("Register the server with your agent, for example:");
            Console.WriteLine("  Claude Code:  claude mcp add sts2 -- sts2-mcp");
            Console.WriteLine("  Codex CLI:    add to ~/.codex/config.toml:  [mcp_servers.sts2]  command = \"sts2-mcp\"");
            Console.WriteLine("  Other agents: a stdio MCP server, command \"sts2-mcp\", no arguments.");
        }
        return report["problems"]?.ToString() == "none" ? 0 : 1;
    }

    case "stop":
    {
        var registry = new InstanceRegistry(paths);
        var result = await new Launcher(paths, registry).StopAsync(CancellationToken.None);
        Console.WriteLine(result.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    case "launch":
    {
        var registry = new InstanceRegistry(paths);
        int players = args.Length > 1 && int.TryParse(args[1], out int n) ? n : 2;
        var mods = args.Skip(int.TryParse(args.ElementAtOrDefault(1), out _) ? 2 : 1).ToList();
        try
        {
            var result = await new Launcher(paths, registry).LaunchAsync(players, mods, allowRunning: false,
                s => { Console.WriteLine(s); return Task.CompletedTask; }, CancellationToken.None);
            Console.WriteLine(result.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (ToolError e) { Console.Error.WriteLine($"error: {e.Message}"); return 1; }
    }

    case "call":
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: sts2-mcp call <cmd> [jsonArgs] [player]"); return 2; }
        var registry = new InstanceRegistry(paths);
        try
        {
            var callArgs = args.Length > 2 ? System.Text.Json.Nodes.JsonNode.Parse(args[2]) as System.Text.Json.Nodes.JsonObject : null;
            var result = await registry.Resolve(args.ElementAtOrDefault(3)).CallAsync(args[1], callArgs, TimeSpan.FromSeconds(30), CancellationToken.None);
            Console.WriteLine(result?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "null");
            return 0;
        }
        catch (ToolError e) { Console.Error.WriteLine($"error: {e.Message}"); return 1; }
    }

    case "plan":
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: sts2-mcp plan <steps.json> [player]"); return 2; }
        var registry = new InstanceRegistry(paths);
        try
        {
            var steps = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(args[1])) as System.Text.Json.Nodes.JsonArray
                        ?? throw new ToolError("the file must hold a JSON array of steps");
            var outcome = await new PlanRunner(registry).RunAsync(steps, args.ElementAtOrDefault(2),
                new Progress<ModelContextProtocol.ProgressNotificationValue>(p => Console.Error.WriteLine(p.Message)), CancellationToken.None);
            Console.WriteLine(outcome.Report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            string dir = Path.GetDirectoryName(Path.GetFullPath(args[1]))!;
            for (int i = 0; i < outcome.Images.Count; i++)
            {
                string file = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(args[1])}-shot{i + 1}.png");
                File.WriteAllBytes(file, outcome.Images[i].Data);
                Console.WriteLine($"screenshot {i + 1}: {outcome.Images[i].Caption} -> {file}");
            }
            return outcome.Report["ok"]?.GetValue<bool>() == true ? 0 : 1;
        }
        catch (ToolError e) { Console.Error.WriteLine($"error: {e.Message}"); return 1; }
    }

    case "":
        break;

    default:
        Console.Error.WriteLine("usage: sts2-mcp [doctor | setup | launch [players] [modFolder...] | call <cmd> [json] [player] | plan <steps.json> [player] | stop | --version]\n       (no arguments: run the MCP server on stdio)");
        return 2;
}

var builder = Host.CreateApplicationBuilder(args);
// stdout carries the MCP protocol; all logging goes to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton<InstanceRegistry>();
builder.Services.AddSingleton<Launcher>();
builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "sts2-mcp", Version = typeof(Tools).Assembly.GetName().Version?.ToString(3) ?? "0" };
        o.ServerInstructions = Instructions.Text;
    })
    .WithStdioServerTransport()
    .WithTools<Tools>();

await builder.Build().RunAsync();
return 0;
