using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Solforge.Mcp;

/** Real shim -> loopback MCP fixture plus a disposable foreign stdio server; no live editor or game is contacted. */
static class WorkerAccessChecks
{
    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("worker access: " + message);
    }

    public static async Task Run(string scratch, string shimDll)
    {
        Directory.CreateDirectory(scratch);
        string project = Path.Combine(scratch, "project"), discovery = Path.Combine(scratch, "discovery");
        Directory.CreateDirectory(project);
        int writes = 0;
        McpTool[] native =
        [
            new("read_project_file", "Fixture read", McpJson.Schema(), false, (_, _) => Task.FromResult(McpToolResult.Text("read"))),
            new("apply_project_edit", "Fixture edit", McpJson.Schema(), true, (_, _) => { writes++; return Task.FromResult(McpToolResult.Text("edited")); })
        ];
        var registry = new McpSessionRegistry();
        await using var server = new McpHttpServer(new McpDispatcher(native, "fixture", "fixture"))
            { Sessions = registry, CurrentProject = () => project };
        server.Start();
        McpDiscovery.Write(new McpDiscoveryEntry(Environment.ProcessId, server.Url, server.Token, project, DateTimeOffset.UtcNow), discovery);
        bool python = EngineCommands.Resolve("py", EngineCommands.FreshPath()) is not null;
        string foreign = Path.Combine(scratch, "foreign.py"), marker = Path.Combine(scratch, "mutation-marker.txt");
        File.WriteAllText(foreign, """
            import json, os, sys
            for line in sys.stdin:
                r = json.loads(line)
                if 'id' not in r: continue
                method = r.get('method')
                if method == 'initialize':
                    result = {'protocolVersion':'2025-06-18','serverInfo':{'name':'fixture','version':'1'},'capabilities':{'tools':{}}}
                elif method == 'tools/list':
                    result = {'tools':[
                        {'name':'inspect','description':'Read fixture','inputSchema':{'type':'object'},'annotations':{'readOnlyHint':True}},
                        {'name':'mutate','description':'Write fixture','inputSchema':{'type':'object'},'annotations':{'readOnlyHint':False}},
                        {'name':'unknown','description':'Undeclared fixture','inputSchema':{'type':'object'}}]}
                elif method == 'tools/call':
                    name = r.get('params',{}).get('name')
                    if name != 'inspect':
                        with open(os.path.join(os.path.dirname(__file__),'mutation-marker.txt'),'a') as f: f.write(name+'\n')
                    result = {'content':[{'type':'text','text':name}],'isError':False}
                else: result = {}
                print(json.dumps({'jsonrpc':'2.0','id':r['id'],'result':result}),flush=True)
            """);
        var upstreamList = new JsonArray(new JsonObject { ["name"] = "solforge", ["command"] = "ignored", ["args"] = new JsonArray("--project", project) });
        if (python) upstreamList.Add(new JsonObject { ["name"] = "fixture", ["command"] = "py", ["args"] = new JsonArray("-3", foreign) });
        string upstreams = Path.Combine(scratch, "upstreams.json");
        File.WriteAllText(upstreams, new JsonObject { ["servers"] = upstreamList }.ToJsonString());

        foreach (bool readOnly in new[] { true, false })
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { shimDll, "--sol", readOnly ? "--worker-readonly" : "--worker", "--project", project, "--upstreams", upstreams })
                start.ArgumentList.Add(arg);
            start.Environment["SOLFORGE_MCP_DISCOVERY"] = discovery;
            using var process = Process.Start(start) ?? throw new IOException("Worker shim fixture failed to start.");
            _ = process.StandardError.ReadToEndAsync();
            try
            {
                int next = 0;
                async Task<JsonObject> Call(string method, JsonObject? parameters = null)
                {
                    int id = ++next;
                    await process.StandardInput.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id,
                        ["method"] = method, ["params"] = parameters ?? new JsonObject() }.ToJsonString());
                    await process.StandardInput.FlushAsync();
                    while (await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) is { } line)
                    {
                        var reply = JsonNode.Parse(line)!.AsObject();
                        if ((int?)reply["id"] == id) return reply;
                    }
                    throw new IOException("Worker shim fixture closed.");
                }
                await Call("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "worker-policy-check" } });
                await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
                var list = await Call("tools/list");
                var names = list["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.ToString()).ToHashSet(StringComparer.Ordinal);
                Expect(names.Contains("read_project_file"), "inspection remains listed");
                Expect(names.Contains("apply_project_edit") == !readOnly, "only executor lists project edits");
                var session = registry.Sessions().Single(item => item.Pid == process.Id);
                Expect(session.IsWorker && session.IsReadOnlyWorker == readOnly, "role metadata reaches the editor HTTP session");
                int before = writes;
                var action = await Call("tools/call", new JsonObject { ["name"] = "apply_project_edit", ["arguments"] = new JsonObject() });
                if (readOnly)
                {
                    Expect(action["error"] is not null || action["result"]?["isError"]?.GetValue<bool>() == true, "forged planning edit refused");
                    Expect(writes == before, "planning edit never executed");
                    using var http = new HttpClient();
                    using var request = new HttpRequestMessage(HttpMethod.Post, server.Url) { Content = new StringContent(
                        "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"apply_project_edit\",\"arguments\":{}}}", Encoding.UTF8, "application/json") };
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.Token);
                    request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session.Id);
                    using var response = await http.SendAsync(request);
                    var refused = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
                    Expect(refused["result"]?["isError"]?.GetValue<bool>() == true && writes == before, "HTTP enforces read-only independently of tool-list filtering");
                }
                else Expect(action["result"]?["isError"]?.GetValue<bool>() != true && writes == before + 1, "executor authoring reaches the native handler");
                if (python)
                {
                    Expect(names.Contains("fixture_inspect") && !names.Contains("fixture_mutate") && !names.Contains("fixture_unknown"), "foreign workers receive explicit read-only tools only");
                    var inspected = await Call("tools/call", new JsonObject { ["name"] = "fixture_inspect", ["arguments"] = new JsonObject() });
                    Expect(inspected["result"]?["content"]?[0]?["text"]?.ToString() == "inspect", "declared foreign read executes");
                    foreach (string tool in new[] { "fixture_mutate", "fixture_unknown" })
                    {
                        var refused = await Call("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = new JsonObject() });
                        Expect(refused["error"] is not null || refused["result"]?["isError"]?.GetValue<bool>() == true, "forged foreign mutation refused");
                    }
                    Expect(!File.Exists(marker), "foreign mutations were never executed");
                }
            }
            finally
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(5000)) process.Kill(true);
            }
        }
    }
}
