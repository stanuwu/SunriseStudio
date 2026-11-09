// Client provenance and testkit health checks: build number parsing, matching the installed DLL to a source tree (exact, likely,
// weak, none, embedded), git state with files changed after the build, the testkit light rules, process parsing, handshake
// parsing, restart with fake process control, and the three MCP tools. No game, git repo, python or testkit needed;
// pass --live to also print the real status of this machine.
using System.Text.Json.Nodes;
using Solforge.Mcp;
using Solforge.Mcp.ClientProvenance;
using Solforge.Mcp.Testkit;

var checks = new List<string>();
string scratch = Path.Combine(Path.GetTempPath(), "solforge-provenance-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    Versions(); checks.Add("build number: last number of 0.6.0.173; junk and null give none; Comments key=value pairs parse");
    Matching(); checks.Add("match: identical build output is exact, build_number alone is weak, a stale counter loses, no tree is none, missing DLL reported");
    Embedded(); checks.Add("embedded tree name in the DLL Comments beats the counter and is exact");
    GitChecks(); checks.Add("git: branch, short HEAD, dirty count, files changed after the build; non-repo reports an error without failing");
    CounterFallback(); checks.Add("tree counter falls back to sunrise_version.h; resources found under Sunrise/ or the root");
    Light(); checks.Add("testkit light: red without script or handshake, amber without an instance or live endpoint; multiple healthy clients are allowed, transport green does not prove a game frame");
    Processes(); checks.Add("testkit processes: py launcher plus python child is one instance; pid|name|command lines parse");
    Probe(); checks.Add("handshake reply parsing: server name and tool count, missing serverInfo or tools fail");
    await Status(); checks.Add("status: injected processes, ports and handshake produce the light; missing script skips the handshake");
    await Restart(); checks.Add("restart: HTTP mode preserves stdio and other ports, explicit kill mode ends every pid, invalid mode and missing script refuse");
    await Tools(); checks.Add("MCP tools: get_client_build, testkit_status read freely; testkit_restart asks first, declined kills nothing, bad mode refused");
    if (args.Contains("--live")) await Live();
    foreach (string check in checks) Console.WriteLine("ok " + check);
    Console.WriteLine($"PASS checks={checks.Count}");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("FAIL " + error.Message);
    Console.Error.WriteLine(error.StackTrace);
    return 1;
}
finally
{
    if (scratch.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && Directory.Exists(scratch)) Directory.Delete(scratch, true);
}

static void Expect(bool condition, string what)
{
    if (!condition) throw new InvalidOperationException(what);
}

/** Makes a tree with an optional build counter and an optional built DLL holding the given bytes at the given time. */
string MakeTree(string name, int? counter, byte[]? built, DateTime? builtAt, bool headerOnly = false)
{
    string root = Path.Combine(scratch, name);
    string resources = Path.Combine(root, "Sunrise", "resources");
    Directory.CreateDirectory(resources);
    if (counter is not null && !headerOnly) File.WriteAllText(Path.Combine(resources, "build_number.txt"), counter + "\r\n");
    if (counter is not null && headerOnly) File.WriteAllText(Path.Combine(resources, "sunrise_version.h"), $"#define SUNRISE_VER_BUILD {counter}\r\n");
    if (built is not null)
    {
        string dir = Path.Combine(root, "build", "x64", "Release");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "steam_api64.dll");
        File.WriteAllBytes(path, built);
        if (builtAt is not null) File.SetLastWriteTimeUtc(path, builtAt.Value);
    }
    return root;
}

string MakeDll(string name, byte[] bytes, DateTime at)
{
    string path = Path.Combine(scratch, name);
    File.WriteAllBytes(path, bytes);
    File.SetLastWriteTimeUtc(path, at);
    return path;
}

Func<string, (string?, string?)> Ver(string? version, string? comments = null) => _ => (version, comments);
string? NoGit(string directory, string[] args) => null;

void Versions()
{
    Expect(ClientBuildInfo.BuildOf("0.6.0.173") == 173, "build of 0.6.0.173");
    Expect(ClientBuildInfo.BuildOf(" 0.6.0.21 ") == 21, "trim");
    Expect(ClientBuildInfo.BuildOf(null) is null && ClientBuildInfo.BuildOf("abc") is null, "junk");
    var pairs = ClientBuildInfo.ParseComments("tree=sol-0.6 git=abc1234; dirty=2");
    Expect(pairs["tree"] == "sol-0.6" && pairs["GIT"] == "abc1234" && pairs["dirty"] == "2", "comment pairs");
    Expect(ClientBuildInfo.ParseComments(null).Count == 0, "null comments");
}

void Matching()
{
    var t0 = new DateTime(2026, 10, 16, 13, 0, 0, DateTimeKind.Utc);
    byte[] bytes = new byte[4096];
    new Random(7).NextBytes(bytes);
    string dll = MakeDll("match.dll", bytes, t0);
    string good = MakeTree("m-good", 173, bytes, t0);
    string stale = MakeTree("m-stale", 99, [1, 2, 3], t0.AddDays(-5));
    string counterOnly = MakeTree("m-counter", 173, null, null);
    var trees = new List<SourceTree> { new("stale", stale), new("counter", counterOnly), new("good", good), new("missing", Path.Combine(scratch, "nope")) };

    var report = ClientBuildInfo.Inspect(dll, trees, Ver("0.6.0.173"), NoGit);
    Expect(report.Exists && report.Build == 173 && report.Version == "0.6.0.173" && report.Size == 4096, "dll facts");
    Expect(report.Trees.Count == 3, "missing tree skipped");
    Expect(report.Best?.Tree.Name == "good" && report.Confidence == "exact", "identical output is exact: " + report.Confidence);
    Expect(report.Best!.HashMatches && report.Best.SizeMatches && report.Best.TimeMatches, "hash, size and time evidence");
    var weak = report.Trees.Single(row => row.Tree.Name == "counter");
    Expect(weak.Score == 30 && !weak.SizeMatches, "counter only scores 30");
    Expect(report.Trees.Single(row => row.Tree.Name == "stale").Score == 0, "stale counter and different output score 0");

    var weakReport = ClientBuildInfo.Inspect(dll, [new("counter", counterOnly)], Ver("0.6.0.173"), NoGit);
    Expect(weakReport.Confidence == "weak" && weakReport.Best?.Tree.Name == "counter", "build_number alone is weak");

    string sameSizeOtherTime = MakeTree("m-time", 5, bytes, t0.AddHours(-3));
    var timeOff = ClientBuildInfo.Inspect(dll, [new("time", sameSizeOtherTime)], Ver("0.6.0.173"), NoGit);
    Expect(timeOff.Best is { HashMatches: true, TimeMatches: false } && timeOff.Confidence == "exact", "identical bytes at another time still exact");

    var none = ClientBuildInfo.Inspect(dll, [new("stale", stale)], Ver("0.6.0.173"), NoGit);
    Expect(none.Best is null && none.Confidence == "none" && none.Summary.Contains("no known source tree", StringComparison.Ordinal), "none");

    var absent = ClientBuildInfo.Inspect(Path.Combine(scratch, "absent.dll"), trees, Ver("0.6.0.1"), NoGit);
    Expect(!absent.Exists && absent.Confidence == "none" && absent.ToJson()["exists"]!.GetValue<bool>() == false, "missing dll");

    var json = report.ToJson();
    Expect(json["matched_tree"]!.GetValue<string>() == "good" && json["candidates"] is JsonArray { Count: 3 }, "json shape");
}

void Embedded()
{
    var t0 = new DateTime(2026, 10, 16, 14, 0, 0, DateTimeKind.Utc);
    string dll = MakeDll("embed.dll", [9, 9, 9, 9], t0);
    string a = MakeTree("e-a", 173, null, null);
    string b = MakeTree("e-b", 12, null, null);
    var report = ClientBuildInfo.Inspect(dll, [new("a", a), new("sol-0.6", b)], Ver("0.6.0.173", "tree=sol-0.6 git=abc1234"), NoGit);
    Expect(report.Best?.Tree.Name == "sol-0.6" && report.Best.EmbeddedMatches && report.Confidence == "exact", "embedded tree wins over the counter");
    Expect(report.Comments == "tree=sol-0.6 git=abc1234", "comments reported");
}

void GitChecks()
{
    var t0 = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
    string dll = MakeDll("git.dll", [5, 5], t0);
    string root = MakeTree("g-tree", 7, null, null);
    string older = Path.Combine(root, "old.cpp"), newer = Path.Combine(root, "new.cpp");
    File.WriteAllText(older, "x"); File.SetLastWriteTimeUtc(older, t0.AddHours(-1));
    File.WriteAllText(newer, "x"); File.SetLastWriteTimeUtc(newer, t0.AddHours(1));
    string? Git(string directory, string[] args) => string.Join(' ', args) switch
    {
        "rev-parse --abbrev-ref HEAD" => "sunrise-0.6",
        "rev-parse --short HEAD" => "a345a58",
        "rev-parse --show-toplevel" => root.Replace('\\', '/'),
        "status --porcelain" => " M old.cpp\n M new.cpp\n?? other.txt\n",
        _ => null,
    };
    var report = ClientBuildInfo.Inspect(dll, [new("g", root)], Ver("0.6.0.7"), Git);
    var state = report.Best!.Git;
    Expect(state.Branch == "sunrise-0.6" && state.Head == "a345a58" && state.DirtyFiles == 3 && state.ChangedAfterBuild == 1 && state.Error is null, "git fields");
    Expect(report.Summary.Contains("sunrise-0.6", StringComparison.Ordinal) && report.Summary.Contains("1 changed after", StringComparison.Ordinal), "summary mentions branch and later changes");
    var notRepo = ClientBuildInfo.Inspect(dll, [new("g", root)], Ver("0.6.0.7"), NoGit);
    Expect(notRepo.Best!.Git.Error is not null && notRepo.Best.Git.DirtyFiles == 0 && notRepo.Best.Tree.Name == "g", "non-repo does not fail the match");
}

void CounterFallback()
{
    string header = MakeTree("c-header", 44, null, null, headerOnly: true);
    Expect(ClientBuildInfo.TreeBuild(header) == 44, "sunrise_version.h fallback");
    string rootRes = Path.Combine(scratch, "c-root");
    Directory.CreateDirectory(Path.Combine(rootRes, "resources"));
    File.WriteAllText(Path.Combine(rootRes, "resources", "build_number.txt"), "12");
    Expect(ClientBuildInfo.TreeBuild(rootRes) == 12, "resources at the root");
    Expect(ClientBuildInfo.TreeBuild(Path.Combine(scratch, "none")) is null, "no counter");
    Expect(ClientBuildInfo.BuiltDll(rootRes) is null, "no build output");
}

void Light()
{
    Expect(TestkitMonitor.Judge(false, null, 0, false, false).Light == "red", "no script");
    Expect(TestkitMonitor.Judge(true, false, 1, true, true).Light == "red", "handshake fails");
    Expect(TestkitMonitor.Judge(true, true, 0, false, false).Light == "amber", "not running");
    Expect(TestkitMonitor.Judge(true, true, 2, true, true).Light == "green", "multiple MCP clients are normal");
    Expect(TestkitMonitor.Judge(true, true, 1, false, true).Light == "amber", "live port down");
    var green = TestkitMonitor.Judge(true, true, 1, true, false);
    Expect(green.Light == "green" && green.Summary.Contains("pad server off", StringComparison.Ordinal), "green, pad off is not a fault");
}

void Processes()
{
    var launcher = new TestkitProcess(1, "py.exe", "py -3 sol_testkit_mcp.py");
    var python = new TestkitProcess(2, "python.exe", "python.exe sol_testkit_mcp.py");
    Expect(TestkitMonitor.LogicalInstances([launcher, python]) == 1, "py plus python is one");
    Expect(TestkitMonitor.LogicalInstances([launcher]) == 1 && TestkitMonitor.LogicalInstances([]) == 0, "launcher alone, none");
    Expect(TestkitMonitor.LogicalInstances([python, python with { Pid = 3 }]) == 2, "two pythons");
    var rows = TestkitMonitor.ParseProcessLines("9928|py.exe|py  \"-3\" \"C:/x/sol_testkit_mcp.py\"\r\nbad line\r\n10684|python.exe|\"C:\\p\\python.exe\" \"C:/x/a|b.py\"\r\n").ToList();
    Expect(rows.Count == 2 && rows[0].Pid == 9928 && rows[1].Name == "python.exe" && rows[1].CommandLine.EndsWith("a|b.py\"", StringComparison.Ordinal), "process lines");
}

void Probe()
{
    var ok = TestkitMonitor.ParseProbe("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"serverInfo\":{\"name\":\"sol-testkit\"}}}",
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"tools\":[{\"name\":\"a\"},{\"name\":\"b\"}]}}");
    Expect(ok.Ok && ok.Tools == 2 && ok.Detail.Contains("sol-testkit", StringComparison.Ordinal), "good reply");
    Expect(!TestkitMonitor.ParseProbe("{\"error\":{}}", "{}").Ok, "no serverInfo");
    Expect(!TestkitMonitor.ParseProbe("{\"result\":{\"serverInfo\":{\"name\":\"x\"}}}", "{\"result\":{}}").Ok, "no tools");
    Expect(!TestkitMonitor.ParseProbe("Traceback (most recent call last)", "").Ok, "not json");
}

TestkitMonitor MakeMonitor(string script, IReadOnlyList<TestkitProcess> processes, Func<int, bool> ports, (bool, string, int) handshake, Func<int, bool>? kill = null) =>
    new(new TestkitOptions { Script = script })
    {
        ListProcesses = _ => Task.FromResult(processes),
        PortUp = ports,
        Handshake = _ => Task.FromResult(handshake),
        Kill = kill,
    };

async Task Status()
{
    string script = Path.Combine(scratch, "sol_testkit_mcp.py");
    File.WriteAllText(script, "# fake");
    var python = new TestkitProcess(2, "python.exe", "python.exe sol_testkit_mcp.py");
    var green = await MakeMonitor(script, [python], _ => true, (true, "sol-testkit answered with 17 tools", 17)).StatusAsync();
    Expect(green.Light == "green" && green.ToolCount == 17 && green.Instances == 1 && green.PadUp && green.LiveUp, "green");
    Expect(green.Launch.Contains("stdio", StringComparison.Ordinal) && green.Launch.Contains(".mcp.json", StringComparison.Ordinal), "launch described");
    var json = green.ToJson();
    Expect(json["light"]!.GetValue<string>() == "green" && json["tool_count"]!.GetValue<int>() == 17 && json["processes"] is JsonArray { Count: 1 }, "json");
    var down = await MakeMonitor(script, [], port => port != 48830, (true, "ok", 3)).StatusAsync();
    Expect(down.Light == "amber" && !down.LiveUp && down.Instances == 0, "amber without a running testkit");
    var broken = await MakeMonitor(script, [python], _ => true, (false, "ModuleNotFoundError: numpy", 0)).StatusAsync();
    Expect(broken.Light == "red" && broken.ToolCount is null && broken.HandshakeDetail!.Contains("numpy", StringComparison.Ordinal), "red on failed handshake");
    var missing = await MakeMonitor(Path.Combine(scratch, "nope.py"), [], _ => false, (true, "never called", 1)).StatusAsync();
    Expect(missing.Light == "red" && !missing.ScriptExists && missing.HandshakeOk is null, "missing script skips handshake");
}

async Task Restart()
{
    string script = Path.Combine(scratch, "sol_testkit_mcp.py");
    File.WriteAllText(script, "# fake");
    var killed = new List<int>();
    var procs = new[] { new TestkitProcess(1, "py.exe", "x"), new TestkitProcess(2, "python.exe", "x") };
    var result = await MakeMonitor(script, procs, _ => true, (true, "", 1), pid => { killed.Add(pid); return true; }).RestartAsync("kill");
    Expect(result.Ok && result.Killed.SequenceEqual(new[] { 1, 2 }) && killed.Count == 2 && result.StartedPid is null && result.Message.Contains("/mcp", StringComparison.Ordinal), "kill mode");
    var none = await MakeMonitor(script, [], _ => true, (true, "", 1), _ => true).RestartAsync("kill");
    Expect(none.Ok && none.Killed.Count == 0 && none.Message.Contains("No testkit", StringComparison.Ordinal), "nothing running");
    var refused = await MakeMonitor(Path.Combine(scratch, "nope.py"), procs, _ => true, (true, "", 1), pid => { killed.Add(pid); return true; }).RestartAsync("kill");
    Expect(!refused.Ok && killed.Count == 2, "missing script kills nothing");
    var invalid = await MakeMonitor(script, procs, _ => true, (true, "", 1), pid => { killed.Add(pid); return true; }).RestartAsync("invalid");
    Expect(!invalid.Ok && killed.Count == 2, "invalid mode kills nothing");
    killed.Clear();
    var httpMonitor = new TestkitMonitor(new TestkitOptions { Script = script })
    {
        ListProcesses = _ => Task.FromResult<IReadOnlyList<TestkitProcess>>([
            new(3, "python.exe", "python sol_testkit_mcp.py"),
            new(4, "python.exe", "python sol_testkit_mcp.py --http 48840"),
            new(5, "python.exe", "python sol_testkit_mcp.py --http 48841")]),
        Kill = pid => { killed.Add(pid); return true; },
        StartHttp = () => 6,
    };
    var http = await httpMonitor.RestartAsync("http");
    Expect(http.Ok && http.StartedPid == 6 && killed.SequenceEqual([4]), "HTTP restart preserves stdio clients and other HTTP ports");
}

async Task Tools()
{
    string script = Path.Combine(scratch, "sol_testkit_mcp.py");
    File.WriteAllText(script, "# fake");
    var killed = new List<int>();
    var monitor = MakeMonitor(script, [new TestkitProcess(5, "python.exe", "x")], _ => true, (true, "ok", 4), pid => { killed.Add(pid); return true; });
    bool approve = false;
    var approvals = new List<string>();
    var testkit = TestkitTools.Create(monitor, (_, summary, _) => { approvals.Add(summary); return Task.FromResult(approve); });
    var build = ClientBuildTools.Create();
    Expect(testkit.Select(tool => tool.Name).SequenceEqual(TestkitTools.Names) && build.Select(tool => tool.Name).SequenceEqual(ClientBuildTools.Names), "tool names");
    Expect(testkit.Concat(build).All(tool => tool.InputSchema["type"]?.ToString() == "object"), "object schemas");
    Expect(!build[0].RequiresApproval && !testkit[0].RequiresApproval && testkit[1].RequiresApproval, "only restart asks");

    var status = await testkit[0].Handler(new JsonObject(), default);
    Expect(!status.IsError && status.Texts[0].Contains("\"light\": \"green\"", StringComparison.Ordinal), "testkit_status");
    var declined = await testkit[1].Handler(new JsonObject(), default);
    Expect(declined.IsError && killed.Count == 0 && approvals.Count == 1, "declined kills nothing");
    var badMode = await testkit[1].Handler(new JsonObject { ["mode"] = "nuke" }, default);
    Expect(badMode.IsError && approvals.Count == 1, "bad mode refused before asking");
    approve = true;
    var restarted = await testkit[1].Handler(new JsonObject { ["mode"] = "kill" }, default);
    Expect(!restarted.IsError && killed.SequenceEqual(new[] { 5 }), "approved restart kills");
    killed.Clear();
    var safeMonitor = new TestkitMonitor(new TestkitOptions { Script = script })
    {
        ListProcesses = _ => Task.FromResult<IReadOnlyList<TestkitProcess>>([new(5, "python.exe", "python sol_testkit_mcp.py")]),
        Kill = pid => { killed.Add(pid); return true; },
        StartHttp = () => 6,
    };
    var safeTools = TestkitTools.Create(safeMonitor, (_, _, _) => Task.FromResult(true));
    var safeRestart = await safeTools[1].Handler(new JsonObject(), default);
    Expect(!safeRestart.IsError && killed.Count == 0, "default HTTP restart preserves stdio clients");

    var missing = await build[0].Handler(new JsonObject { ["dll"] = Path.Combine(scratch, "gone.dll") }, default);
    Expect(!missing.IsError && missing.Texts[0].Contains("\"exists\": false", StringComparison.Ordinal), "get_client_build on a missing DLL");
}

async Task Live()
{
    var status = await TestkitMonitor.CreateDefault().StatusAsync();
    Console.WriteLine("live testkit: " + status.ToJson().ToJsonString(McpJson.Indented));
    var build = ClientBuildInfo.Inspect(ClientBuildInfo.DefaultDll(), ClientBuildInfo.DefaultTrees());
    Console.WriteLine("live client: " + build.ToJson().ToJsonString(McpJson.Indented));
}
