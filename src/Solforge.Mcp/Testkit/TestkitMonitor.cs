/**
 * File: health and restart of the sol-testkit MCP (tools\testkit\mcp\sol_testkit_mcp.py). Today it is a stdio server:
 * the MCP client (Claude Code, from .mcp.json) spawns `py -3 sol_testkit_mcp.py` with SOL_PAD_PORT and SOL_LIVE_PORT and
 * owns the pipe, and the script binds the live capture port itself. So Solforge cannot respawn it for that client;
 * restart ends the stale process (the client reconnects with /mcp) or starts the script's own --http mode.
 */
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Solforge.Mcp.Testkit;

/** Where the testkit lives and which ports it uses. */
public sealed class TestkitOptions
{
    public string Script { get; init; } = DefaultScript();

    /** SOL_ROOT, else the first Sol root on the desktop that has the script (the tree moved to "Project - Sol\sol"). */
    private static string DefaultScript()
    {
        string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
        string[] roots = Environment.GetEnvironmentVariable("SOL_ROOT") is { Length: > 0 } root
            ? [root]
            : [Path.Combine(desktop, "Project - Sol", "sol"), Path.Combine(desktop, "sol")];
        string[] scripts = [.. roots.Select(sol => Path.Combine(sol, "02-server", "solserver", "solserverbase", "project sol", "tools",
            "testkit", "mcp", "sol_testkit_mcp.py"))];
        return scripts.FirstOrDefault(File.Exists) ?? scripts[0];
    }
    public string Python { get; init; } = "py";
    public string[] PythonArgs { get; init; } = ["-3"];
    public int PadPort { get; init; } = 48811;
    public int LivePort { get; init; } = 48830;
    /** Port for the script's own --http mode, when started from Solforge. */
    public int HttpPort { get; init; } = 48840;
    /** Seconds to wait for the handshake probe. */
    public int ProbeSeconds { get; init; } = 20;
}

/** One running testkit process. */
public sealed record TestkitProcess(int Pid, string Name, string CommandLine);

/** Health of the testkit: a light (green, amber, red), the facts behind it and how it is launched. */
public sealed record TestkitStatus(string Light, string Summary, bool ScriptExists, IReadOnlyList<TestkitProcess> Processes, int Instances,
    bool? HandshakeOk, string? HandshakeDetail, int? ToolCount, bool PadUp, bool LiveUp, string Launch)
{
    public JsonObject ToJson()
    {
        var rows = new JsonArray();
        foreach (var row in Processes) rows.Add(new JsonObject { ["pid"] = row.Pid, ["name"] = row.Name, ["command"] = row.CommandLine });
        return new JsonObject
        {
            ["light"] = Light, ["summary"] = Summary, ["script_exists"] = ScriptExists, ["instances"] = Instances, ["processes"] = rows,
            ["handshake_ok"] = HandshakeOk, ["handshake"] = HandshakeDetail, ["tool_count"] = ToolCount,
            ["pad_port_up"] = PadUp, ["live_port_up"] = LiveUp, ["launch"] = Launch,
        };
    }
}

/** Result of a restart request. */
public sealed record TestkitRestartResult(bool Ok, string Message, IReadOnlyList<int> Killed, int? StartedPid)
{
    public JsonObject ToJson()
    {
        var killed = new JsonArray();
        foreach (int pid in Killed) killed.Add(pid);
        return new JsonObject { ["ok"] = Ok, ["message"] = Message, ["killed"] = killed, ["started_pid"] = StartedPid };
    }
}

/** Checks and restarts the sol-testkit MCP. Process listing, port probes and the handshake are injectable for checks. */
public sealed class TestkitMonitor(TestkitOptions options)
{
    public TestkitOptions Options { get; } = options;
    public Func<CancellationToken, Task<IReadOnlyList<TestkitProcess>>>? ListProcesses { get; init; }
    public Func<int, bool>? PortUp { get; init; }
    public Func<CancellationToken, Task<(bool Ok, string Detail, int Tools)>>? Handshake { get; init; }
    public Func<int, bool>? Kill { get; init; }
    public Func<int?>? StartHttp { get; init; }

    public static TestkitMonitor CreateDefault() => new(new TestkitOptions());

    /** How the testkit is started today, for the panel and the tool. */
    public string LaunchDescription() =>
        $"stdio MCP server: the client (Claude Code) runs `{Options.Python} {string.Join(' ', Options.PythonArgs)} {Options.Script}` from .mcp.json with " +
        $"SOL_PAD_PORT={Options.PadPort} SOL_LIVE_PORT={Options.LivePort}; the script binds the live capture port {Options.LivePort} itself. " +
        $"Each client owns its stdio worker. The aggregate reconnects a dead worker on the next call; direct clients must reconnect (/mcp). " +
        "Multiple client instances are normal, and capture status returns each instance's actual stream URL. `--http <port>` serves tools separately.";

    /** Checks the script, the running processes, the ports and a throwaway handshake, and turns them into a light. */
    public async Task<TestkitStatus> StatusAsync(CancellationToken cancel = default)
    {
        bool exists = File.Exists(Options.Script);
        var processes = await (ListProcesses?.Invoke(cancel) ?? ListAsync(cancel)).ConfigureAwait(false);
        int instances = LogicalInstances(processes);
        var probePort = PortUp ?? TcpUp;
        bool pad = probePort(Options.PadPort), live = probePort(Options.LivePort);
        bool? ok = null; string? detail = null; int? tools = null;
        if (exists)
        {
            var (good, text, count) = await (Handshake?.Invoke(cancel) ?? ProbeAsync(cancel)).ConfigureAwait(false);
            ok = good; detail = text; tools = good ? count : null;
        }
        var (light, summary) = Judge(exists, ok, instances, live, pad);
        return new(light, summary, exists, processes, instances, ok, detail, tools, pad, live, LaunchDescription());
    }

    /** Transport-only light: red on a missing script/failed handshake, amber with no client/default endpoint, else green. */
    public static (string Light, string Summary) Judge(bool scriptExists, bool? handshakeOk, int instances, bool liveUp, bool padUp)
    {
        if (!scriptExists) return ("red", "sol_testkit_mcp.py not found");
        if (handshakeOk != true) return ("red", "testkit script fails its MCP handshake (python or numpy/Pillow missing?)");
        string pad = padUp ? "pad server up" : "pad server off";
        if (instances == 0) return ("amber", $"script healthy but no testkit running; the client must reconnect (/mcp) or Start HTTP ({pad})");
        if (!liveUp) return ("amber", $"running but live capture port is not answering ({pad})");
        return ("green", $"{instances} MCP client instance(s), handshake ok, default capture endpoint up ({pad}); check capture/launch_status for a fresh game frame");
    }

    /** python.exe processes count; py.exe is only the launcher in front of them, so it counts only when alone. */
    public static int LogicalInstances(IReadOnlyList<TestkitProcess> processes)
    {
        int python = processes.Count(row => row.Name.StartsWith("python", StringComparison.OrdinalIgnoreCase));
        return python > 0 ? python : processes.Count;
    }

    /**
     * Explicit "kill" ends every testkit worker. "http" restarts only HTTP workers on HttpPort and preserves stdio clients.
     */
    public async Task<TestkitRestartResult> RestartAsync(string mode, CancellationToken cancel = default)
    {
        if (!File.Exists(Options.Script)) return new(false, "sol_testkit_mcp.py not found: " + Options.Script, [], null);
        if (mode is not ("kill" or "http")) return new(false, "mode must be kill or http", [], null);
        bool http = string.Equals(mode, "http", StringComparison.OrdinalIgnoreCase);
        var processes = await (ListProcesses?.Invoke(cancel) ?? ListAsync(cancel)).ConfigureAwait(false);
        var killer = Kill ?? KillTree;
        var killed = new List<int>();
        foreach (var row in processes.Where(row => !http || Regex.IsMatch(row.CommandLine,
            @"(?:^|\s)--http\s+[""']?" + Options.HttpPort + @"[""']?(?:\s|$)", RegexOptions.CultureInvariant)))
            if (killer(row.Pid)) killed.Add(row.Pid);
        if (!http)
            return new(true, killed.Count == 0 ? "No testkit was running; reconnect it from the client (/mcp)."
                : $"Ended {killed.Count} testkit process(es). The client reconnects with /mcp (Claude Code respawns it).", killed, null);
        await Task.Delay(500, cancel).ConfigureAwait(false);
        int? pid = StartHttp is { } startHttp ? startHttp() : StartHttpProcess();
        return pid is null ? new(false, "Could not start the HTTP testkit; stdio clients were preserved.", killed, null)
            : new(true, $"Started the testkit HTTP server on http://127.0.0.1:{Options.HttpPort}/mcp (pid {pid}); stdio client connections were preserved. Capture prefers port {Options.LivePort}, using an available loopback port if occupied.", killed, pid);
    }

    /** Starts `py -3 script --http port` without a window. */
    private int? StartHttpProcess()
    {
        try
        {
            var start = new ProcessStartInfo(Options.Python) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(Options.Script)! };
            foreach (string arg in Options.PythonArgs) start.ArgumentList.Add(arg);
            start.ArgumentList.Add(Options.Script);
            start.ArgumentList.Add("--http");
            start.ArgumentList.Add(Options.HttpPort.ToString());
            start.Environment["SOL_PAD_PORT"] = Options.PadPort.ToString();
            start.Environment["SOL_LIVE_PORT"] = Options.LivePort.ToString();
            return Process.Start(start)?.Id;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    private static bool KillTree(int pid)
    {
        try { using var process = Process.GetProcessById(pid); process.Kill(true); return true; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    /** True when something accepts a TCP connection on 127.0.0.1:port within half a second. */
    public static bool TcpUp(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", port).Wait(500) && client.Connected;
        }
        catch (Exception error) when (error is SocketException or AggregateException or ObjectDisposedException) { return false; }
    }

    /** Lists python/py processes whose command line runs the testkit script (PowerShell CIM; the probe excludes itself). */
    private static async Task<IReadOnlyList<TestkitProcess>> ListAsync(CancellationToken cancel)
    {
        const string Script = "Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like '*sol_testkit_mcp.py*' -and $_.ProcessId -ne $PID " +
            "-and $_.Name -match '^(py|pythonw?)\\.exe$' } | ForEach-Object { \"$($_.ProcessId)|$($_.Name)|$($_.CommandLine)\" }";
        var rows = new List<TestkitProcess>();
        try
        {
            var start = new ProcessStartInfo("powershell") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command", Script }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process is null) return rows;
            string output = await process.StandardOutput.ReadToEndAsync(cancel).ConfigureAwait(false);
            await process.WaitForExitAsync(cancel).ConfigureAwait(false);
            rows.AddRange(ParseProcessLines(output));
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
        return rows;
    }

    /** Parses the `pid|name|command` lines of the process listing. */
    public static IEnumerable<TestkitProcess> ParseProcessLines(string output)
    {
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.TrimEnd('\r').Split('|', 3);
            if (parts.Length == 3 && int.TryParse(parts[0], out int pid)) yield return new(pid, parts[1], parts[2]);
        }
    }

    /** Starts a throwaway copy with live capture off, sends initialize and tools/list, and reports whether it answers. */
    private async Task<(bool Ok, string Detail, int Tools)> ProbeAsync(CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(Options.ProbeSeconds));
        Process? process = null;
        try
        {
            var start = new ProcessStartInfo(Options.Python)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (string arg in Options.PythonArgs) start.ArgumentList.Add(arg);
            start.ArgumentList.Add(Options.Script);
            start.Environment["SOL_LIVE_AUTOSTART"] = "0";
            process = Process.Start(start);
            if (process is null) return (false, "could not start " + Options.Python, 0);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"solforge-probe\",\"version\":\"1\"}}}").ConfigureAwait(false);
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}").ConfigureAwait(false);
            await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            string? first = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            string? second = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (first is null || second is null)
                return (false, "the script exited before answering: " + Tail(await StderrOrEmpty(stderr).ConfigureAwait(false)), 0);
            return ParseProbe(first, second);
        }
        catch (OperationCanceledException) { return (false, $"no answer within {Options.ProbeSeconds}s", 0); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        { return (false, error.Message, 0); }
        finally
        {
            if (process is not null)
            {
                try { process.Kill(true); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                process.Dispose();
            }
        }
    }

    private static async Task<string> StderrOrEmpty(Task<string> task)
    {
        try { return await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException) { return ""; }
    }

    private static string Tail(string text) => text.Length <= 400 ? text.Trim() : text[^400..].Trim();

    /** Checks the initialize and tools/list replies. */
    public static (bool Ok, string Detail, int Tools) ParseProbe(string initialize, string toolsList)
    {
        try
        {
            var init = JsonNode.Parse(initialize);
            string? server = init?["result"]?["serverInfo"]?["name"]?.GetValue<string>();
            if (server is null) return (false, "initialize had no serverInfo: " + Tail(initialize), 0);
            int count = (JsonNode.Parse(toolsList)?["result"]?["tools"] as JsonArray)?.Count ?? -1;
            return count < 0 ? (false, "tools/list had no tools array", 0) : (true, $"{server} answered with {count} tools", count);
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        { return (false, "reply was not JSON: " + Tail(initialize), 0); }
    }
}
