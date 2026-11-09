// Sol MCP set checks: which upstreams an engine start collects (Solforge's own, .mcp.json above the project, the named
// workspace MCPs from the user's engine configs), the one client-facing sol aggregate, merging into agy and Cline files
// without touching unrelated servers, the solforge-mcp run copy, the aggregate tools/resources/prompts routing, and file/initialize checks.
using System.Diagnostics;
using System.Text.Json.Nodes;
using Solforge.Mcp;

static class McpSetChecks
{
    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("mcp set: " + message);
    }

    /** Entry point: runs every check in this class and throws on the first failure. @param shimDll the built shim. */
    public static async Task Run(string scratch, string shimDll)
    {
        string home = Path.Combine(scratch, "home"), top = Path.Combine(scratch, "sol top");
        string project = Path.Combine(top, "10-Project Sol", "Project Sol");
        string tiger = Path.Combine(top, "tiger", "tiger-mcp.exe"), script = Path.Combine(top, "testkit", "sol_testkit_mcp.py");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(Path.GetDirectoryName(tiger)!);
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(tiger, "");
        File.WriteAllText(script, "");
        string shim = Path.Combine(scratch, "run", "solforge-mcp-abc.exe");

        // The workspace .mcp.json: tiger-mcp is carried; its stale solforge, a disabled and a remote server are not.
        File.WriteAllText(Path.Combine(top, ".mcp.json"), new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["solforge"] = new JsonObject { ["command"] = @"C:\old\publish\solforge-mcp.exe" },
                ["tiger-mcp"] = new JsonObject { ["type"] = "stdio", ["command"] = tiger, ["args"] = new JsonArray("-v", "d1_roi"), ["env"] = new JsonObject() },
                ["off"] = new JsonObject { ["command"] = "x.exe", ["disabled"] = true },
                ["remote"] = new JsonObject { ["type"] = "http", ["url"] = "https://example.invalid/mcp" }
            }
        }.ToJsonString());
        // sol-testkit only in ~/.claude.json (project entry, forward slashes): found there with its env.
        File.WriteAllText(Path.Combine(home, ".claude.json"), new JsonObject
        {
            ["projects"] = new JsonObject
            {
                [project.Replace('\\', '/')] = new JsonObject
                {
                    ["mcpServers"] = new JsonObject
                    {
                        ["sol-testkit"] = new JsonObject
                        {
                            ["type"] = "stdio", ["command"] = "py", ["args"] = new JsonArray("-3", script),
                            ["env"] = new JsonObject { ["SOL_PAD_PORT"] = "48811" }
                        }
                    }
                }
            },
            ["mcpServers"] = new JsonObject { ["sol-testkit"] = new JsonObject { ["command"] = "loses.exe" }, ["mine"] = new JsonObject { ["command"] = tiger } }
        }.ToJsonString());

        var servers = SolMcpServers.Collect(project, shim, gemini: true, clinePass: true, home);
        Expect(servers.Select(server => server.Name).SequenceEqual(["solforge", "tiger-mcp", "sol-testkit", "mine"]),
            "collected: " + string.Join(", ", servers.Select(server => server.Name)));
        Expect(servers.Take(1).All(server => server.Owned && server.Command == shim) && servers.Skip(1).All(server => !server.Owned), "owned");
        Expect(servers[0].Args.SequenceEqual(["--project", project]), "shim args");
        Expect(servers[2].Command == "py" && servers[2].Env["SOL_PAD_PORT"]?.ToString() == "48811", "testkit from .claude.json project entry");
        var bare = SolMcpServers.Collect(project, shim, gemini: false, clinePass: false, home);
        Expect(bare.Select(server => server.Name).SequenceEqual(["solforge", "tiger-mcp", "sol-testkit", "mine"]), "no gemini or clinepass unless asked");

        // An engine never gets its own delegate.
        Expect(!SolMcpServers.For(SolMcpServers.Engine.Agy, servers).Any(server => server.Name == "gemini"), "agy without gemini");
        Expect(!SolMcpServers.For(SolMcpServers.Engine.Cline, servers).Any(server => server.Name == "clinepass"), "cline without clinepass");
        Expect(SolMcpServers.For(SolMcpServers.Engine.Claude, servers).Count() == 4, "claude gets all");

        // Claude's --mcp-config holds one aggregate, whose local upstream list carries the whole set.
        var claude = SolMcpServers.ClaudeConfig(servers)["mcpServers"]!.AsObject();
        string upstreamsFile = SolMcpServers.UpstreamFile(project);
        Expect(claude.Count == 1 && claude["sol"]?["type"]?.ToString() == "stdio" && claude["sol"]?["command"]?.ToString() == shim
            && claude["sol"]?["args"]?[0]?.ToString() == "--sol"
            && claude["sol"]?["args"]?.AsArray().Any(node => node?.ToString() == "--upstreams") == true
            && claude["sol"]?["env"] is JsonObject, "claude sol shape");
        var upstreams = JsonNode.Parse(File.ReadAllText(upstreamsFile))!["servers"]!.AsArray();
        Expect(upstreams.Any(node => node?["name"]?.ToString() == "tiger-mcp")
            && upstreams.Any(node => node?["name"]?.ToString() == "sol-testkit" && node?["env"]?["SOL_PAD_PORT"]?.ToString() == "48811")
            && upstreams.All(node => node?["name"]?.ToString() != "sol"), "local upstream list");
        string folder = Path.Combine(scratch, "claude-files");
        var files = EngineLaunch.WriteClaudeFiles(project, shim, folder, servers: servers);
        Expect(!files.Gemini && !files.ClinePass && JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(files.McpConfig)), SolMcpServers.ClaudeConfig(servers)),
            "claude files from the set");
        var plain = EngineLaunch.WriteClaudeFiles(project, shim, folder, servers: bare);
        Expect(!plain.Gemini && !plain.ClinePass && !EngineLaunch.ClaudeArguments(plain, []).Contains("ask_gemini"), "flags follow the set");

        // Cline: unrelated rea stays; the old separate Sol entries are replaced by the one aggregate.
        string cline = SolMcpServers.ClineConfig(home);
        Directory.CreateDirectory(Path.GetDirectoryName(cline)!);
        var rea = new JsonObject { ["command"] = "cmd.exe", ["args"] = new JsonArray("/c", "npx", "rea"), ["disabled"] = false, ["autoApprove"] = new JsonArray() };
        var userGemini = new JsonObject { ["command"] = "npx", ["args"] = new JsonArray("gemini-mcp") };
        var userTiger = new JsonObject { ["command"] = tiger, ["args"] = new JsonArray("-v", "d2") };
        File.WriteAllText(cline, new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["rea"] = rea.DeepClone(), ["gemini"] = userGemini.DeepClone(), ["tiger-mcp"] = userTiger.DeepClone(),
                ["solforge"] = new JsonObject { ["command"] = @"C:\old\publish\solforge-mcp.exe", ["args"] = new JsonArray("--project", "old") }
            },
            ["other"] = 1
        }.ToJsonString());
        var kept = SolMcpServers.MergeInto(cline, SolMcpServers.Engine.Cline, servers);
        var clineRoot = JsonNode.Parse(File.ReadAllText(cline))!;
        var clineServers = clineRoot["mcpServers"]!.AsObject();
        Expect(kept.Count == 0 && JsonNode.DeepEquals(clineServers["rea"], rea) && clineRoot["other"]?.GetValue<int>() == 1,
            "unrelated server and root keys untouched");
        Expect(clineServers["sol"]?["command"]?.ToString() == shim && clineServers["sol"]?["autoApprove"] is JsonArray
            && clineServers["sol"]?["args"]?[0]?.ToString() == "--sol", "sol added to Cline");
        Expect(clineServers["gemini"] is null && clineServers["tiger-mcp"] is null && clineServers["sol-testkit"] is null
            && clineServers["solforge"] is null && clineServers["clinepass"] is null, "separate Sol entries removed");
        string before = File.ReadAllText(cline);
        SolMcpServers.MergeInto(cline, SolMcpServers.Engine.Cline, servers);
        Expect(File.ReadAllText(cline) == before, "cline merge is idempotent");

        // agy: an older solforge-mcp clinepass entry is refreshed to the current shim.
        string agy = SolMcpServers.AgyConfig(home);
        Directory.CreateDirectory(Path.GetDirectoryName(agy)!);
        File.WriteAllText(agy, "{\"mcpServers\":{\"clinepass\":{\"command\":\"C:\\\\old\\\\solforge-mcp.exe\",\"args\":[\"--clinepass\"]}}}");
        Expect(SolMcpServers.MergeInto(agy, SolMcpServers.Engine.Agy, servers).Count == 0, "agy keeps nothing");
        var agyServers = JsonNode.Parse(File.ReadAllText(agy))!["mcpServers"]!.AsObject();
        Expect(agyServers["sol"]?["command"]?.ToString() == shim && agyServers["sol"]?["args"]?[0]?.ToString() == "--sol"
            && agyServers["sol"]?["disabled"]?.GetValue<bool>() == false && agyServers["gemini"] is null
            && agyServers["tiger-mcp"] is null && agyServers["clinepass"] is null, "agy sol entry");

        // WriteAll with the set: every client gets the aggregate; the desktop app does too.
        string home2 = Path.Combine(scratch, "home2"), appData = Path.Combine(scratch, "appdata2");
        McpClientConfigs.WriteAll(project, shim, home2, appData, servers);
        Expect(JsonNode.Parse(File.ReadAllText(SolMcpServers.AgyConfig(home2)))?["mcpServers"]?["sol"] is not null
            && JsonNode.Parse(File.ReadAllText(SolMcpServers.ClineConfig(home2)))?["mcpServers"]?["sol"] is not null, "WriteAll writes sol");
        Expect(JsonNode.Parse(File.ReadAllText(SolMcpServers.AgyConfig(home2)))?["mcpServers"]?["sol-testkit"] is null
            && JsonNode.Parse(File.ReadAllText(SolMcpServers.ClineConfig(home2)))?["mcpServers"]?["gemini"] is null, "no separate entries");
        // Claude's user file and its matching project entry are migrated too; unrelated servers stay.
        string home3 = Path.Combine(scratch, "home3");
        Directory.CreateDirectory(home3);
        string claudeUser = Path.Combine(home3, ".claude.json");
        File.WriteAllText(claudeUser, new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["solforge"] = new JsonObject { ["command"] = "old.exe" }, ["tiger-mcp"] = new JsonObject { ["command"] = "old-tiger.exe" },
                ["mine"] = new JsonObject { ["command"] = "mine.exe" }
            },
            ["projects"] = new JsonObject
            {
                [project] = new JsonObject
                {
                    ["mcpServers"] = new JsonObject { ["solforge"] = new JsonObject { ["command"] = "old.exe" }, ["mine"] = new JsonObject { ["command"] = "mine.exe" } }
                }
            }
        }.ToJsonString());
        McpClientConfigs.WriteAll(project, shim, home3, appData, servers);
        var claudeRoot = JsonNode.Parse(File.ReadAllText(claudeUser))!;
        var claudeUserServers = claudeRoot["mcpServers"]!.AsObject();
        var claudeProjectServers = claudeRoot["projects"]?[project]?["mcpServers"]!.AsObject()!;
        Expect(claudeUserServers["sol"] is not null && claudeUserServers["solforge"] is null && claudeUserServers["tiger-mcp"] is null
            && claudeUserServers["mine"]?["command"]?.ToString() == "mine.exe", "Claude user config migrated");
        Expect(claudeProjectServers["sol"] is not null && claudeProjectServers["solforge"] is null
            && claudeProjectServers["mine"]?["command"]?.ToString() == "mine.exe", "Claude project config migrated");
        var desktop = JsonNode.Parse(File.ReadAllText(Path.Combine(home2, ".gemini", "antigravity", "mcp_config.json")))!["mcpServers"]!.AsObject();
        Expect(desktop.Count == 1 && desktop["sol"] is not null, "desktop app gets sol");
        var pi = JsonNode.Parse(File.ReadAllText(Path.Combine(project, ".pi", "mcp.json")))!["mcpServers"]!.AsObject();
        var piArgs = pi["sol"]?["args"]?.AsArray().Select(node => node?.ToString()).ToArray() ?? [];
        Expect(pi.Count == 1 && pi["sol"]?["type"]?.ToString() == "stdio" && pi["sol"]?["exposure"]?.ToString() == "direct"
            && pi["sol"]?["command"]?.ToString() == shim
            && piArgs.Contains("--sol") && piArgs.Contains("--upstreams") && pi["gemini"] is null && pi["tiger-mcp"] is null,
            "Pi gets one sol aggregate");

        // File checks: py on PATH and existing paths pass; a missing exe, script or project is reported.
        string bin = Path.Combine(scratch, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "py.exe"), "");
        Directory.CreateDirectory(Path.GetDirectoryName(shim)!);
        File.WriteAllText(shim, "");
        Expect(SolMcpServers.CheckFiles(servers, bin, ".EXE").Count == 0, "all present: " + string.Join(" | ", SolMcpServers.CheckFiles(servers, bin, ".EXE")));
        File.Delete(script);
        File.Delete(shim);
        var problems = SolMcpServers.CheckFiles(servers, Path.Combine(scratch, "nothing"), ".EXE");
        Expect(problems.Count(line => line.StartsWith("solforge: ") || line.StartsWith("gemini: ") || line.StartsWith("clinepass: ")) == 1
            && problems.Any(line => line.StartsWith("sol-testkit: py not found")) && problems.Any(line => line.StartsWith("sol-testkit: " + script)),
            "problems: " + string.Join(" | ", problems));

        // Run copy: named after the published exe, reused, replaced when it changes, the last copy while it is locked.
        string publish = Path.Combine(scratch, "publish", "solforge-mcp.exe"), runs = Path.Combine(scratch, "runs");
        Directory.CreateDirectory(Path.GetDirectoryName(publish)!);
        File.WriteAllText(publish, "v1");
        string first = SolMcpServers.RunCopy(publish, runs);
        Expect(Path.GetDirectoryName(first) == runs && Path.GetFileName(first).StartsWith("solforge-mcp-") && File.ReadAllText(first) == "v1", "copy: " + first);
        Expect(SolMcpServers.RunCopy(publish, runs) == first, "reused");
        File.WriteAllText(publish, "v2 longer");
        File.SetLastWriteTimeUtc(publish, DateTime.UtcNow.AddMinutes(1));
        string second = SolMcpServers.RunCopy(publish, runs);
        Expect(second != first && File.ReadAllText(second) == "v2 longer", "new copy after a publish");
        File.WriteAllText(publish, "v3 is being written");
        using (new FileStream(publish, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            File.SetLastWriteTimeUtc(second, DateTime.UtcNow.AddMinutes(2));
            Expect(SolMcpServers.RunCopy(publish, runs) == second, "locked publish uses the last copy");
        }
        Expect(SolMcpServers.RunCopy(Path.Combine(scratch, "gone", "solforge-mcp.exe"), runs).EndsWith(@"gone\solforge-mcp.exe"), "missing stays missing");
        for (int i = 3; i < 7; i++)
        {
            File.WriteAllText(publish, new string('v', i * 3));
            SolMcpServers.RunCopy(publish, runs);
        }
        Expect(Directory.GetFiles(runs, "solforge-mcp-*.exe").Length <= 3 && Directory.GetFiles(runs, "*.tmp").Length == 0, "old copies pruned");

        // ~/.claude.json entries on an older run copy follow the current one (user `sol`, project entries); others stay.
        string home4 = Path.Combine(scratch, "home4");
        Directory.CreateDirectory(home4);
        string claude4 = Path.Combine(home4, ".claude.json");
        string current = Path.Combine(runs, "solforge-mcp-new0000001.exe"), old = Path.Combine(runs, "solforge-mcp-old0000001.exe");
        File.WriteAllText(claude4, new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["sol"] = new JsonObject { ["command"] = old, ["args"] = new JsonArray("--sol") },
                ["rea"] = new JsonObject { ["command"] = "cmd.exe" },
                ["elsewhere"] = new JsonObject { ["command"] = @"C:\other\solforge-mcp-old0000001.exe" }
            },
            ["projects"] = new JsonObject { ["C:/p"] = new JsonObject { ["mcpServers"] = new JsonObject { ["sol"] = new JsonObject { ["command"] = old } } } },
            ["keep"] = 7
        }.ToJsonString());
        Expect(StandaloneRegistration.RefreshRunCopies(current, claude4) == 2, "two stale run-copy entries re-pointed");
        var root4 = JsonNode.Parse(File.ReadAllText(claude4))!;
        Expect(root4["mcpServers"]?["sol"]?["command"]?.ToString() == current && root4["mcpServers"]?["sol"]?["args"]?[0]?.ToString() == "--sol"
            && root4["projects"]?["C:/p"]?["mcpServers"]?["sol"]?["command"]?.ToString() == current
            && root4["mcpServers"]?["rea"]?["command"]?.ToString() == "cmd.exe"
            && root4["mcpServers"]?["elsewhere"]?["command"]?.ToString() == @"C:\other\solforge-mcp-old0000001.exe"
            && root4["keep"]?.GetValue<int>() == 7, "run-copy refresh: " + root4.ToJsonString());
        Expect(StandaloneRegistration.RefreshRunCopies(current, claude4) == 0, "run-copy refresh is idempotent");

        // Upstream skips: a nested aggregate and a missing entry script are not started; a missing data file is.
        Expect(SolMcpServers.SkipReason("solforge-standalone", current, ["--sol", "--upstreams", "x.json"]) is not null, "nested aggregate skipped");
        Expect(SolMcpServers.SkipReason("solforge", current, ["--project", "p"]) is null, "solforge itself kept");
        Expect(SolMcpServers.SkipReason("destiny-modder", "node", [Path.Combine(scratch, "gone", "index.ts")]) is not null, "missing script skipped");
        Expect(SolMcpServers.SkipReason("graphify", "py", ["--graph", Path.Combine(scratch, "gone", "graph.json")]) is null, "missing data file still starts");
        Expect(SolMcpServers.SkipReason("x", Path.Combine(scratch, "gone", "x.exe"), []) is not null, "missing command skipped");
        Expect(SolMcpServers.IsNestedAggregate(new SolMcpServers.Server("solforge-standalone", current, ["--sol"], new JsonObject(), false, "t")), "nested aggregate detected");

        // npx pins resolve to an installed copy of that exact version only.
        string npxRoot = Path.Combine(scratch, "npx", "abc"), pkg = Path.Combine(npxRoot, "node_modules", "rea-agents");
        Directory.CreateDirectory(Path.Combine(pkg, "scripts"));
        File.WriteAllText(Path.Combine(pkg, "scripts", "rea.mjs"), "");
        File.WriteAllText(Path.Combine(pkg, "package.json"), "{\"name\":\"rea-agents\",\"version\":\"6.7.0\",\"bin\":{\"rea\":\"scripts/rea.mjs\",\"rea-agents\":\"scripts/rea.mjs\"}}");
        var npx = NpxLocal.Resolve("cmd.exe", ["/d", "/c", "npx", "-y", "rea-agents@6.7.0", "mcp"], [npxRoot]);
        Expect(npx is { } found && found.Command == "node" && found.Args.SequenceEqual([Path.Combine(pkg, "scripts", "rea.mjs"), "mcp"]),
            "npx pin resolved: " + (npx is null ? "null" : npx.Value.Command + " " + string.Join(" ", npx.Value.Args)));
        Expect(NpxLocal.Resolve("npx", ["-y", "rea-agents@6.7.0", "mcp"], [npxRoot]) is not null, "plain npx resolved");
        Expect(NpxLocal.Resolve("cmd.exe", ["/d", "/c", "npx", "-y", "rea-agents@6.8.0", "mcp"], [npxRoot]) is null, "other version not resolved");
        Expect(NpxLocal.Resolve("cmd.exe", ["/d", "/c", "npx", "-y", "rea-agents@latest", "mcp"], [npxRoot]) is null, "latest not resolved");
        Expect(NpxLocal.Resolve("node", ["x.js"], [npxRoot]) is null, "non-npx untouched");

        // Initialize probe: the real headless shim answers; a missing command and an early exit are reported.
        Expect(await SolMcpServers.Probe("dotnet", [shimDll], TimeSpan.FromSeconds(60)) is null, "shim answers initialize");
        Expect((await SolMcpServers.Probe(Path.Combine(scratch, "nothing.exe"), [], TimeSpan.FromSeconds(5)))?.StartsWith("could not start") == true, "missing command");
        Expect(await SolMcpServers.Probe("cmd.exe", ["/d", "/c", "exit"], TimeSpan.FromSeconds(10)) == "exited without answering initialize", "early exit");

        await AggregateCheck(scratch, shimDll);
    }

    /** A real `sol` session over two fake stdio upstreams: one server, both tools, calls routed back. */
    private static async Task AggregateCheck(string scratch, string shimDll)
    {
        if (EngineCommands.Resolve("py", EngineCommands.FreshPath()) is null)
        {
            Console.WriteLine("SKIP aggregate: python is not on PATH");
            return;
        }
        string folder = Path.Combine(scratch, "aggregate");
        Directory.CreateDirectory(folder);
        string fake = Path.Combine(folder, "fake_mcp.py");
        File.WriteAllText(fake, """
            import json, os, sys
            name = os.environ.get("FAKE_NAME", "fake")
            for line in sys.stdin:
                request = json.loads(line)
                method, request_id = request.get("method"), request.get("id")
                if request_id is None:
                    continue
                if method == "initialize":
                    result = {"protocolVersion": "2025-06-18", "capabilities": {"tools": {}, "resources": {}, "prompts": {}},
                              "serverInfo": {"name": name, "version": "1"}, "instructions": name + " instructions"}
                elif method == "tools/list":
                    result = {"tools": [{"name": "echo", "description": "Echo text",
                                         "inputSchema": {"type": "object", "properties": {"text": {"type": "string"}}}}]}
                elif method == "tools/call":
                    text = request.get("params", {}).get("arguments", {}).get("text", "")
                    if text == "crash":
                        with open(os.path.join(os.path.dirname(__file__), name + ".actions"), "a") as actions:
                            actions.write("executed\n")
                        os._exit(42)
                    if text == "pid":
                        text = str(os.getpid())
                    result = {"content": [{"type": "text", "text": name + ":" + text}], "isError": False}
                elif method == "resources/list":
                    result = {"resources": [{"uri": "fake://resource", "name": "resource", "mimeType": "text/plain"}]}
                elif method == "resources/read":
                    uri = request.get("params", {}).get("uri", "")
                    result = {"contents": [{"uri": uri, "mimeType": "text/plain", "text": name + ":resource:" + uri}]}
                elif method == "resources/templates/list":
                    result = {"resourceTemplates": [{"uriTemplate": "fake://item/{id}", "name": "item"}]}
                elif method == "prompts/list":
                    result = {"prompts": [{"name": "greet", "description": "Greet",
                                           "arguments": [{"name": "who", "required": True}]}]}
                elif method == "prompts/get":
                    who = request.get("params", {}).get("arguments", {}).get("who", "")
                    result = {"description": "Greet", "messages": [
                        {"role": "user", "content": {"type": "text", "text": name + ":" + who}}]}
                else:
                    print(json.dumps({"jsonrpc": "2.0", "id": request_id, "error": {"code": -32601, "message": "no"}}), flush=True)
                    continue
                print(json.dumps({"jsonrpc": "2.0", "id": request_id, "result": result}), flush=True)
            """);
        string upstreams = Path.Combine(folder, "upstreams.json");
        File.WriteAllText(upstreams, new JsonObject
        {
            ["servers"] = new JsonArray(
                new JsonObject { ["name"] = "fake", ["command"] = "py", ["args"] = new JsonArray("-3", fake), ["env"] = new JsonObject() },
                new JsonObject { ["name"] = "tiger-mcp", ["command"] = "py", ["args"] = new JsonArray("-3", fake), ["env"] = new JsonObject { ["FAKE_NAME"] = "tiger" } })
        }.ToJsonString());
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        start.ArgumentList.Add(shimDll);
        start.ArgumentList.Add("--sol");
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(folder);
        start.ArgumentList.Add("--upstreams");
        start.ArgumentList.Add(upstreams);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("aggregate did not start");
        try
        {
            async Task<JsonNode> Call(int id, string method, JsonObject? parameters = null)
            {
                await process.StandardInput.WriteLineAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method,
                    ["params"] = parameters?.DeepClone() ?? new JsonObject()
                }.ToJsonString());
                await process.StandardInput.FlushAsync();
                while (await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) is { } line)
                {
                    if (line.Length == 0) continue;
                    var reply = JsonNode.Parse(line)!;
                    if ((int?)reply["id"] != id) continue;
                    Expect(reply["error"] is null, method + ": " + line);
                    return reply["result"]!;
                }
                throw new InvalidOperationException(method + ": aggregate closed");
            }
            var initialized = await Call(1, "initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "aggregate-check", ["version"] = "1" }
            });
            Expect(initialized["serverInfo"]?["name"]?.ToString() == "sol", "aggregate server name");
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            var tools = (await Call(2, "tools/list"))["tools"]!.AsArray();
            Expect(tools.Select(tool => tool?["name"]?.ToString()).SequenceEqual(["fake_echo", "tiger_echo"]),
                "aggregate tools: " + string.Join(", ", tools.Select(tool => tool?["name"]?.ToString())));
            var echo = await Call(3, "tools/call", new JsonObject { ["name"] = "fake_echo", ["arguments"] = new JsonObject { ["text"] = "one" } });
            var tiger = await Call(4, "tools/call", new JsonObject { ["name"] = "tiger_echo", ["arguments"] = new JsonObject { ["text"] = "two" } });
            Expect(echo["content"]?[0]?["text"]?.ToString() == "fake:one" && tiger["content"]?[0]?["text"]?.ToString() == "tiger:two",
                "aggregate routes calls: " + echo.ToJsonString() + " / " + tiger.ToJsonString());
            Expect(initialized["capabilities"]?["resources"] is JsonObject && initialized["capabilities"]?["prompts"] is JsonObject,
                "aggregate advertises resources and prompts");
            var resources = (await Call(5, "resources/list"))["resources"]!.AsArray();
            Expect(resources.Count == 2 && resources.All(resource => resource?["uri"]?.ToString()?.StartsWith("sol://") == true),
                "aggregate resources: " + string.Join(", ", resources.Select(resource => resource?["uri"]?.ToString())));
            string resourceUri = resources[0]!["uri"]!.ToString();
            var read = await Call(6, "resources/read", new JsonObject { ["uri"] = resourceUri });
            Expect(read["contents"]?[0]?["text"]?.ToString() == "fake:resource:fake://resource"
                && read["contents"]?[0]?["uri"]?.ToString() == resourceUri, "aggregate resource read: " + read.ToJsonString());
            var templates = (await Call(7, "resources/templates/list"))["resourceTemplates"]!.AsArray();
            string template = templates[0]!["uriTemplate"]!.ToString();
            string expanded = template.Replace("{id}", "7", StringComparison.Ordinal);
            var fromTemplate = await Call(8, "resources/read", new JsonObject { ["uri"] = expanded });
            Expect(fromTemplate["contents"]?[0]?["text"]?.ToString() == "fake:resource:fake://item/7"
                && fromTemplate["contents"]?[0]?["uri"]?.ToString() == expanded, "aggregate resource template: " + fromTemplate.ToJsonString());
            var promptList = (await Call(9, "prompts/list"))["prompts"]!.AsArray();
            Expect(promptList.Select(prompt => prompt?["name"]?.ToString()).SequenceEqual(["fake_greet", "tiger_greet"]),
                "aggregate prompts: " + string.Join(", ", promptList.Select(prompt => prompt?["name"]?.ToString())));
            var prompt = await Call(10, "prompts/get", new JsonObject
            {
                ["name"] = "fake_greet", ["arguments"] = new JsonObject { ["who"] = "Ada" }
            });
            Expect(prompt["messages"]?[0]?["content"]?["text"]?.ToString() == "fake:Ada", "aggregate prompt get: " + prompt.ToJsonString());
            var pidReply = await Call(11, "tools/call", new JsonObject { ["name"] = "fake_echo", ["arguments"] = new JsonObject { ["text"] = "pid" } });
            int upstreamPid = int.Parse(pidReply["content"]![0]!["text"]!.ToString()[5..]);
            using (var ownedUpstream = Process.GetProcessById(upstreamPid))
            {
                ownedUpstream.Kill(true);
                await ownedUpstream.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            var reconnected = await Call(12, "tools/call", new JsonObject { ["name"] = "fake_echo", ["arguments"] = new JsonObject { ["text"] = "pid" } });
            Expect(reconnected["isError"]?.GetValue<bool>() != true && reconnected["content"]![0]!["text"]!.ToString() != "fake:" + upstreamPid,
                "a dead upstream reconnects before the next request");
            var interrupted = await Call(13, "tools/call", new JsonObject { ["name"] = "fake_echo", ["arguments"] = new JsonObject { ["text"] = "crash" } });
            Expect(interrupted["isError"]?.GetValue<bool>() == true && interrupted["content"]![0]!["text"]!.ToString().Contains("not replayed", StringComparison.Ordinal),
                "interrupted action reports uncertain outcome without replay");
            var recovered = await Call(14, "tools/call", new JsonObject { ["name"] = "fake_echo", ["arguments"] = new JsonObject { ["text"] = "recovered" } });
            Expect(recovered["content"]![0]!["text"]!.ToString() == "fake:recovered"
                && File.ReadAllLines(Path.Combine(folder, "fake.actions")).Length == 1,
                "the next call reconnects and the interrupted action ran only once");
        }
        finally
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(5000)) process.Kill();
        }
    }
}
