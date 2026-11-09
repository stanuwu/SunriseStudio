// Solforge Phase 47B checks: MCP tool list and schemas over the stdio shim, find_object resolution of viewport
// labels, the attention log, project path rules, per-client config generation, engine command resolution, the
// Claude Code launch line, the shim's --context hook output, get_open_documents and the auto-allow prompt detector. No game install needed.
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Solforge.Mcp;

var checks = new List<string>();
string scratch = Path.Combine(Path.GetTempPath(), "solforge-mcp-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    FindObject();
    checks.Add("find_object resolves 'static mesh 7489', 'last city crater', '[182]' and tag hashes; numbers are exact; other maps never match");
    Attention();
    checks.Add("the attention log folds repeats, lists newest first, and 'the one I just hovered' wins a tie");
    ProjectPaths();
    checks.Add("project files resolve inside the project only; packages, manifest and build output are not editable");
    await AiAccessChecks.Run(Path.Combine(scratch, "ai-access"));
    checks.Add("AI folder grants cover nested descendants in MCP reads/search, reject sibling-prefix/traversal escapes and links, revoke immediately; worker actions use a bounded authoring allowlist and approval cancellation is audited correctly");
    await WorkerAccessChecks.Run(Path.Combine(scratch, "worker-access"), FindShim());
    checks.Add("native worker aggregate: planner/reviewer read-only metadata reaches HTTP; mutations are hidden and refused, executor authoring remains available, foreign worker tools require explicit read-only annotations");
    Configs();
    checks.Add("client configs: .mcp.json, Cline, Antigravity desktop and CLI get the one sol aggregate entry; an empty agy file merges without .bak; other servers survive; rewrite is idempotent");
    CommandResolution();
    checks.Add("engine commands resolve on PATH + PATHEXT like cmd: .cmd beats npm's bare script, folders with spaces, missing is null");
    ClaudeLaunch();
    checks.Add("Claude launch: --mcp-config, --settings hook, --append-system-prompt and --add-dir survive argv parsing with spaced paths; agy gets the same --add-dir folders");
    PiHarness();
    checks.Add("Pi harness: discovers pi-cust.cmd from a configured path, a folder, the Desktop build or PATH; launches with Solforge's prompt and --continue; its MCP file holds one sol aggregate entry and no carried credentials");
    AiConsoleAdapterChecks.Run(Path.Combine(scratch, "ai-consoles"));
    checks.Add("AI consoles: Claude Code, Cline, Codex and OpenCode are registered; quota detection filters ordinary text; Codex/OpenCode receive idempotent project MCP config while user settings survive");
    Providers();
    checks.Add("providers: Claude subscription is the default, subscription keys are stripped, and retired Cline providers have no presets");
    ResumeSessions();
    checks.Add("resume: claude --continue only when ~/.claude/projects/<folder> has a session; agy --conversation and cline --id pick the newest session for the same folder; none means a fresh start");
    DualEngines();
    checks.Add("handoff: Claude receives its first prompt and retained transcript context; retired delegates are absent from launch files");
    RetiredDelegates();
    checks.Add("retired provider delegates are never added or forwarded, including saved upstream definitions");
    ContextText();
    checks.Add("context summary: compact lines under 2 KB with map, hovered, selected values, open documents; nothing when paused");
    MissionObjects();
    checks.Add("mission slots: SDK slot ids and constants, parent/children/descendants, find by id, object id, name and registry-suffixed constant; find_object resolves slot names, ids and registry hashes");
    MissionScale();
    checks.Add("mission slots stay fast at EDZ scale: 120k slots index, page and resolve in seconds, pages carry total and next");
    CodeRefsCheck();
    checks.Add("code refs: slot id, quoted name and whole-word constants in this mission's SDK file and the server scripts; everywhere reads other missions; limit and folder discovery");
    UiControlChecks();
    checks.Add("ui controls: exact ids, label queries ranked (label, prefix, words, scope), kind/window filters, ties refused with the candidates, operations per kind, approve summaries");
    ViewportFeedChecks();
    checks.Add(await ScreenOcrChecks());
    PlayerSourceChecks();
    checks.Add("player sources: recorder lines (cam over a frozen or stale pos, menus and marks skipped, newest line decides), server /coop rows, yaw and facing, trail spacing, cap and teleport reset; get_player_position and wait_for_player_near reach, time out and refuse a bad target");
    checks.Add("viewport feed: luminance grid, unchanged captures add no frame and skip encoding, change text says how much and where, ids, since, ring capacity, wait_for_change wakes and times out");
    ReferenceCheck();
    checks.Add("reference library: found above the project, index.json plus unindexed front matter, ranked search with tag filter, read by path or url with paging, half-written index and path escapes are safe");
    await JobChecks();
    checks.Add("per-call deadline: a slow tool replies pending with a job id instead of blocking, a repeat call attaches without rerunning, mcp_job waits for and returns the result, lists and cancels jobs");
    await StdioRoundTrip();
    checks.Add("initialize, tools/list (all tools with object schemas), tools/call, get_open_documents, mission, read_panel and reference tools round trip over the stdio shim");
    checks.Add("ui and viewport tools: list_ui_controls pages, ui_* resolve before asking and wait for approval, an approved ui_set_checked reaches the host, get_viewport_frame sends JPEG plus text and skips the image for a known id, wait_for_viewport_change times out, /viewport pages need the view key");
    checks.Add("generic client: the official MCP Python SDK lists the tools and calls frame, control and approval-gated tools over streamable HTTP and over stdio (skipped only when Python or the mcp package is missing)");
    checks.Add("project tools: list_maps and other catalog reads reach the host and work while paused, find_world_objects stops while paused, host errors become tool errors, edit_bounds validates before asking, edit_placement waits for approval");
    SessionChecks(scratch);
    checks.Add("session context: SESSION.md lists project, map, skills and tools; the AGENTS.md block is appended once, replaced in place and keeps the user's text; skills copy only when they differ");
    await ContextHook();
    checks.Add("solforge-mcp --context prints the live context, and nothing (exit 0, fast) when paused or no Solforge runs");
    string dataProject = Path.Combine(scratch, "data-project");
    Directory.CreateDirectory(dataProject);
    await DecompChecks.Run(Path.Combine(scratch, "decomp"), new FixtureHost(dataProject));
    checks.Add("deep data: decomp index (calls, data refs, tag classes, names, cache, grep, rename), struct inference and header structs, image strings and vtables, generic tag layouts and class schemas, cursor paging, CSV, and the decomp/data/export tools");
    await ClassChecks.Run(Path.Combine(scratch, "classes"), new FixtureHost(dataProject));
    checks.Add("class layer: MSVC RTTI (type descriptors, locators, hierarchy) in a fixture image, demangling, vtable runs split at referenced starts, Class::vfN owners, constructors, inferred fields, the class cache, and get_class/get_vtables/get_struct/decomp_status");
    await AutomaticChecks.Run(Path.Combine(scratch, "automatic"), new FixtureHost(dataProject));
    checks.Add("automatic mode: /automatic parsing, start prompt, idle nudge with progress and remaining, repeat and stuck nudges change tactic, prompt and busy screens wait, goal_complete, goal_blocked, stop, nudge and time limits, nesting guard, saved goal resume, and the automatic_*/goal_* tools with no approve click");
    await McpSetChecks.Run(Path.Combine(scratch, "mcp-set"), FindShim());
    checks.Add("Sol MCP set: retired delegates are filtered; enabled stdio servers above the project and from user configs are carried; every client gets one sol aggregate; the aggregate lists and routes upstream tools, resources, resource templates and prompts; run copy of solforge-mcp; missing exe/script checks and the initialize probe");
    await MultiAgentChecks.Run();
    checks.Add("multi-agent: sessions from initialize clientInfo and the shim pid header, dead and idle sessions dropped, open_map/close_map refuse another session's recent map unless force (expiry, same session, UI change, mission-only claim), builds and deploys say who started them, map=<tag> reads answer from a background view without touching the UI map, get_sessions");
    await CommandChecks.Run(Path.Combine(scratch, "commands"), new FixtureHost(dataProject));
    checks.Add("command coverage: list_commands (catalog, query, paused), run_command, show_panel, set_viewport_options, set_camera, export_asset, import_asset, project_change and arm_file_dialog validate before one approve click; catalog and get_help tool map name only real tools; header controls open with ui_invoke/ui_set_checked");
    await MissionInsightChecks.Run(Path.Combine(scratch, "insight"), new FixtureHost(dataProject));
    checks.Add("mission insight: SDK module reader (slots, squad data, states, directives, dialogue cues and text, scenes, trigger volumes), mission_coverage by retail name and tag with prefab scope, squads with no data, cutscene state from tag bytes 0x0C-0x0F, dialogue inline, trigger map, retail timelines, script steps and saved beat marks");
    Approvals();
    checks.Add("auto-allow: Claude Code, agy and Cline allow prompts match only with a one-time Yes/Allow highlighted at the bottom; No, scrollback, quotes, persistent rows and free text never do");
    foreach (string check in checks) Console.WriteLine("PASS " + check);
    Console.WriteLine($"PASS checks={checks.Count}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("FAIL " + exception);
    return 1;
}
finally
{
    try { Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
}

static void Expect(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static ObjectEntry[] Fixture() =>
[
    new("w:1", "Botza District / Static mesh 7489 - Last city crater [182]", "Mesh", ObjectScope.Viewport, "Botza District", "w64_cosmodrome_0123", 0x80A1B2C3),
    new("w:2", "Botza District / Static mesh 748 - Rubble [12]", "Mesh", ObjectScope.Tree, "Botza District"),
    new("w:3", "Botza District / Static mesh 74890 - Crater rim [183]", "Mesh", ObjectScope.Tree, "Botza District"),
    new("w:4", "Botza District / Static mesh 50 - Tube [7]", "Mesh", ObjectScope.Viewport, "Botza District"),
    new("w:5", "Botza District / Static mesh 51 - Tube [8]", "Mesh", ObjectScope.Viewport, "Botza District"),
    new("w:6", "Moon / Static mesh 7489 - Last city crater [182]", "Mesh", ObjectScope.Tree, "Moon"),
    new("tag:80FFEE01", "Last city crater rock", "static model", ObjectScope.Asset, null, "w64_shared_01", 0x80FFEE01),
];

static void FindObject()
{
    var entries = Fixture();
    string Top(string query, IReadOnlyDictionary<string, float>? recency = null) =>
        ObjectIndex.Find(entries, query, "Botza District", recency) is [var first, ..] ? first.Entry.Id : "(none)";
    Expect(Top("static mesh 7489") == "w:1", "static mesh 7489 -> " + Top("static mesh 7489"));
    Expect(Top("do you see static mesh 7489?") == "w:1", "spoken question");
    Expect(Top("Static mesh 7489 - Last city crater [182]") == "w:1", "exact label");
    Expect(Top("last city crater") == "w:1", "words -> " + Top("last city crater"));
    Expect(Top("[182]") == "w:1", "[182] -> " + Top("[182]"));
    Expect(Top("0x80A1B2C3") == "w:1", "tag hash");
    Expect(Top("static mesh 748") == "w:2", "748 must not take 7489");
    Expect(ObjectIndex.Find(entries, "static mesh 7489", "Botza District").All(match => match.Entry.Id != "w:6"), "another map matched");
    Expect(ObjectIndex.Find(entries, "static mesh 7489", "Moon") is [{ Entry.Id: "w:6" }, ..], "map scoping on the Moon");
    Expect(ObjectIndex.Find(entries, "crater rock", "Botza District").Any(match => match.Entry.Scope == ObjectScope.Asset), "asset index row");
    Expect(ObjectIndex.Find(entries, "zebra", "Botza District").Count == 0, "nonsense matched");
    var recent = new Dictionary<string, float> { ["w:5"] = 1f };
    Expect(Top("that tube", recent) == "w:5", "recency breaks the tube tie");
    var json = ObjectIndex.ToJson(ObjectIndex.Find(entries, "static mesh 7489", "Botza District"));
    Expect(json[0]?["package"]?.ToString() == "w64_cosmodrome_0123" && json[0]?["tag"]?.ToString() == "0x80A1B2C3", "match JSON");
}

static void Attention()
{
    var time = DateTimeOffset.Parse("2026-10-15T12:00:00Z");
    var log = new AttentionLog(4) { Clock = () => time };
    log.Record(AttentionKind.Hover, "w:4", "Tube 50");
    log.Record(AttentionKind.Hover, "w:4", "Tube 50");
    log.Record(AttentionKind.Select, "w:1", "Static mesh 7489");
    log.Record(AttentionKind.Hover, "w:5", "Tube 51");
    var recent = log.Recent(10);
    Expect(recent.Count == 3 && recent[0].Id == "w:5" && recent[1].Kind == AttentionKind.Select, "order and folding");
    Expect(log.Recent(10, AttentionKind.Select) is [{ Id: "w:1" }], "kind filter");
    for (int i = 0; i < 10; i++) log.Record(AttentionKind.Focus, "f" + i, "F");
    Expect(log.Recent(100).Count == 4, "capacity");
    Expect(log.ToJson(1)[0]?["kind"]?.ToString() == "focus", "json kind");
    Expect(log.Recency()["f9"] > log.Recency()["f6"], "recency weights");
}

void ProjectPaths()
{
    string project = Path.Combine(scratch, "project");
    Directory.CreateDirectory(Path.Combine(project, "scripts"));
    File.WriteAllText(Path.Combine(project, "scripts", "freeroam.lua"), "-- crater 80A1B2C3\nreturn 1\n");
    Expect(ProjectFiles.Resolve(project, [], "scripts/freeroam.lua") is not null, "resolve");
    Expect(ProjectFiles.Resolve(project, [], "../outside.txt") is null, "escape");
    Expect(ProjectFiles.EditablePath(project, "scripts/new.lua", out _) is not null, "editable");
    foreach (string refused in new[] { "../x.lua", "C:/Windows/x.lua", "bin/x.lua", "data/a.pkg", "sunrise.project.json", "sdk:api.md" })
        Expect(ProjectFiles.EditablePath(project, refused, out _) is null, "not refused: " + refused);
    var hits = ProjectFiles.Search(project, [], "80a1b2c3", ".lua", 10);
    Expect(hits.Count == 1 && hits[0]?["line"]?.GetValue<int>() == 1, "search");
    Expect(ProjectFiles.Read(Path.Combine(project, "scripts", "freeroam.lua"), "scripts/freeroam.lua", 2, 5)["text"]?.ToString()
        .StartsWith("2\treturn 1", StringComparison.Ordinal) == true, "read offset");
}

void Configs()
{
    string project = Path.Combine(scratch, "config project");
    string home = Path.Combine(scratch, "home"), appData = Path.Combine(scratch, "appdata");
    Directory.CreateDirectory(project);
    File.WriteAllText(Path.Combine(project, ".mcp.json"), "{\"mcpServers\":{\"other\":{\"command\":\"x\"}},\"keep\":1}");
    Directory.CreateDirectory(Path.Combine(appData, "Code", "User", "globalStorage", "saoudrizwan.claude-dev"));
    // The agy installer leaves its config empty: it must merge as {} with no .bak.
    string agy = Path.Combine(home, ".gemini", "config", "mcp_config.json");
    Directory.CreateDirectory(Path.GetDirectoryName(agy)!);
    File.WriteAllText(agy, "");
    string shim = @"C:\Solforge\solforge-mcp.exe";
    var written = McpClientConfigs.WriteAll(project, shim, home, appData);
    Expect(written.Select(item => item.Client).SequenceEqual(
        ["Claude Code", "Cline", "Cline (VS Code)", "Antigravity (desktop)", "Antigravity CLI"]), "clients");
    Expect(written[^1].Path == agy && !File.Exists(agy + ".bak"), "agy file without .bak");
    var agyEntry = SolMcpServers.ClientEntry(project, shim, null);
    agyEntry["disabled"] = false;
    Expect(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(agy))?["mcpServers"]?["sol"], agyEntry), "agy entry");
    var claude = JsonNode.Parse(File.ReadAllText(Path.Combine(project, ".mcp.json")))!;
    Expect(claude["mcpServers"]?["other"]?["command"]?.ToString() == "x" && claude["keep"]?.GetValue<int>() == 1, "kept other entries");
    var entry = claude["mcpServers"]?["sol"];
    Expect(entry?["command"]?.ToString() == shim && entry?["type"]?.ToString() == "stdio", "claude entry");
    Expect(entry?["args"]?.AsArray().Select(node => node?.ToString()).SequenceEqual(["--sol", "--project", project]) == true, "project scope");
    foreach (var item in written.Skip(1))
        Expect(JsonNode.Parse(File.ReadAllText(item.Path))?["mcpServers"]?["sol"]?["command"]?.ToString() == shim, item.Client);
    var stamps = written.Select(item => File.GetLastWriteTimeUtc(item.Path)).ToArray();
    McpClientConfigs.WriteAll(project, shim, home, appData);
    Expect(written.Select(item => File.GetLastWriteTimeUtc(item.Path)).SequenceEqual(stamps), "idempotent");
    // Whitespace counts as empty; a damaged file is kept as .bak and replaced.
    var sol = new JsonObject { ["command"] = shim };
    string blank = Path.Combine(scratch, "blank.json"), damaged = Path.Combine(scratch, "damaged.json");
    File.WriteAllText(blank, "  \n");
    McpClientConfigs.Merge(blank, sol.DeepClone().AsObject());
    Expect(!File.Exists(blank + ".bak") && JsonNode.Parse(File.ReadAllText(blank))?["mcpServers"]?["sol"] is not null, "blank merges");
    File.WriteAllText(damaged, "{not json");
    McpClientConfigs.Merge(damaged, sol.DeepClone().AsObject());
    Expect(File.Exists(damaged + ".bak") && File.ReadAllText(damaged + ".bak") == "{not json"
        && JsonNode.Parse(File.ReadAllText(damaged))?["mcpServers"]?["sol"] is not null, "damaged kept as .bak");
}

async Task StdioRoundTrip()
{
    string project = Path.Combine(scratch, "project");
    string discovery = Path.Combine(scratch, "discovery");
    var host = new FixtureHost(project) { CodeRoots = MissionFixtures.CodeRoots(Path.Combine(scratch, "stdio-code")),
        ReferenceFolder = MissionFixtures.Reference(Path.Combine(scratch, "stdio-reference")) };
    var dispatcher = new McpDispatcher(SolforgeTools.Create(host), "0.1.0", SolforgeTools.Instructions);
    await using var server = new McpHttpServer(dispatcher);
    server.Start();
    McpDiscovery.Write(new McpDiscoveryEntry(Environment.ProcessId, server.Url, server.Token, project, DateTimeOffset.UtcNow), discovery);
    Expect(McpDiscovery.Find(project, discovery)?.Url == server.Url, "discovery");
    McpDiscovery.Write(new McpDiscoveryEntry(999999, "http://127.0.0.1:1/", "t", project, DateTimeOffset.UtcNow), discovery);
    Expect(McpDiscovery.CleanStale(discovery, pid => pid == Environment.ProcessId) == 1
        && !File.Exists(Path.Combine(discovery, "server-999999.json")) && File.Exists(Path.Combine(discovery, $"server-{Environment.ProcessId}.json")), "stale discovery cleaned");

    string shim = FindShim();
    var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    start.ArgumentList.Add(shim);
    start.ArgumentList.Add("--project");
    start.ArgumentList.Add(project);
    start.Environment["SOLFORGE_MCP_DISCOVERY"] = discovery;
    using var process = Process.Start(start)!;
    async Task<JsonNode> Call(int id, string method, JsonObject? parameters = null)
    {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
        await process.StandardInput.WriteLineAsync(request.ToJsonString());
        await process.StandardInput.FlushAsync();
        string? line;
        try { line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (TimeoutException) { line = null; }
        if (line is null)
        {
            // Stderr only ends once the shim exits, so read it only after stopping the shim.
            process.Kill();
            Expect(false, method + ": no reply; " + await process.StandardError.ReadToEndAsync());
        }
        var reply = JsonNode.Parse(line!)!;
        Expect(reply["id"]?.GetValue<int>() == id && reply["error"] is null, method + ": " + line);
        return reply["result"]!;
    }
    try
    {
        var initialized = await Call(1, "initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "checks", ["version"] = "1" } });
        Expect(initialized["serverInfo"]?["name"]?.ToString() == "sol" && initialized["capabilities"]?["tools"] is not null, "initialize");
        await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        var tools = (await Call(2, "tools/list"))["tools"]!.AsArray();
        string[] expected = ["get_context", "get_open_documents", "find_object", "get_object", "get_viewport_screenshot", "list_visible", "get_attention_log", "get_recent_activity",
            "read_project_file", "search_project", "search_reference", "read_reference", "get_tag", "get_package_entry", "get_live_state",
            "list_mission_objects", "get_mission_object", "read_panel",
            "select_object", "focus_camera", "highlight", "open_file", "apply_project_edit",
            "get_editor_screenshot", "start_model_workflow", "get_model_workflow", "cancel_model_workflow",
            "get_editor_state", "get_project", "list_project_files", "list_project_changes", "get_build_status", "get_settings",
            "list_maps", "list_missions", "list_packages", "list_package_entries", "search_assets", "list_mission_scripts",
            "find_world_objects", "get_placement", "search_items", "get_item", "search_perks", "list_vendors", "get_vendor",
            "list_creation_types", "describe_creation",
            "open_map", "open_asset", "set_viewport_rendering", "edit_placement", "edit_bounds", "revert_object_edit",
            "apply_item_edit", "apply_vendor_edit", "start_creation", "replace_texture_pixels", "build_project",
            "set_settings", "set_mod_details", "close_map", "close_asset", "open_project", "create_project", "close_project", "build_to_folder", "update_running_game", "reload_mission_scripts",
            .. SolforgeTools.DecompToolNames, .. SolforgeTools.PackageDataToolNames, .. SolforgeTools.SchemaToolNames, .. SolforgeTools.MediaToolNames, .. SolforgeTools.CuiToolNames,
            "list_content_families", "list_content", "get_content", "validate_content", "get_subclass_layout", "check_subclass_build", "get_help",
            "create_content", "update_content", "delete_content", "export_content",
            "list_ui_controls", "ui_invoke", "ui_set_value", "ui_set_checked", "ui_select",
            "get_viewport_frame", "get_viewport_frames", "wait_for_viewport_change", "read_screen_text", "get_viewport_stream",
            "get_player_position", "wait_for_player_near",
            "list_graph_node_types", "list_graphs", "get_graph", "compile_graph", "create_mission_script", "create_graph", "edit_graph", "apply_graph",
            "list_mission_lib_calls", "insert_mission_lib_call",
            "list_shaders", "get_shader", "list_animations", "get_animation", "list_audio", "get_audio", .. SolforgeTools.AutomaticTools,
            .. SolforgeTools.CommandToolNames, "mission_coverage", .. SolforgeTools.MissionHostToolNames, .. SolforgeTools.ItemSourceToolNames, "get_parhelion_context", "list_parhelion_weapon_recipes", "validate_parhelion_weapon_recipe", "start_parhelion_weapon_recipe",
            .. SolforgeTools.PerkReadTools, .. SolforgeTools.PerkActionTools, .. SolforgeTools.RetailToolNames, McpJobs.ToolName];

        string[] actions = ["select_object", "focus_camera", "highlight", "open_file", "apply_project_edit", "start_model_workflow", "open_map", "open_asset",
            "set_viewport_rendering", "edit_placement", "edit_bounds", "revert_object_edit", "apply_item_edit", "apply_vendor_edit",
            "start_creation", "replace_texture_pixels", "build_project", "set_settings", "set_mod_details", "close_map", "close_asset", "open_project", "create_project", "close_project", "build_to_folder", "update_running_game", "reload_mission_scripts", "rename_function", "ui_invoke", "ui_set_value", "ui_set_checked", "ui_select", "create_content", "update_content", "delete_content", "export_content", "create_mission_script", "create_graph", "edit_graph", "apply_graph", "insert_mission_lib_call", .. SolforgeTools.ItemSourceActionTools, "start_parhelion_weapon_recipe", .. SolforgeTools.PerkActionTools, .. SolforgeTools.RetailActionTools, .. SolforgeTools.CommandToolNames.Where(name => name != "list_commands")];
        Expect(tools.Select(tool => tool?["name"]?.ToString()).SequenceEqual(expected), "tool names: " + string.Join(",", tools.Select(tool => tool?["name"])));
        foreach (var tool in tools)
        {
            var schema = tool!["inputSchema"]!;
            Expect(schema["type"]?.ToString() == "object" && schema["properties"] is JsonObject, "schema of " + tool["name"]);
            foreach (var required in schema["required"]?.AsArray() ?? new JsonArray())
                Expect(schema["properties"]![required!.ToString()] is not null, $"{tool["name"]} requires an undeclared {required}");
            bool readOnly = tool["annotations"]?["readOnlyHint"]?.GetValue<bool>() == true;
            Expect(readOnly != actions.Contains(tool["name"]!.ToString()), "read-only flag of " + tool["name"]);
            foreach (var (name, property) in schema["properties"]!.AsObject())
                Expect((string?)property?["type"] != "array" || property?["items"] is JsonObject, $"{tool["name"]}.{name} array has items");
        }
        var found = await Call(3, "tools/call", new JsonObject { ["name"] = "find_object", ["arguments"] = new JsonObject { ["query"] = "static mesh 7489" } });
        var text = JsonNode.Parse(found["content"]![0]!["text"]!.ToString())!;
        Expect(text["matches"]?[0]?["id"]?.ToString() == "w:1", "find_object over stdio: " + text.ToJsonString());
        var denied = await Call(4, "tools/call", new JsonObject { ["name"] = "select_object", ["arguments"] = new JsonObject { ["id"] = "w:1" } });
        Expect(denied["isError"]?.GetValue<bool>() == true && host.Approvals == 1, "approval gate");
        var unknown = await Call(5, "ping");
        Expect(unknown is JsonObject, "ping");
        var documents = await Call(6, "tools/call", new JsonObject { ["name"] = "get_open_documents", ["arguments"] = new JsonObject() });
        var open = JsonNode.Parse(documents["content"]![0]!["text"]!.ToString())!;
        Expect(open["asset"]?["tag"]?.ToString() == "0x80A1B2C3" && open["script"]?["path"]?.ToString() == "scripts/freeroam.lua"
            && open["changes"]?["count"]?.GetValue<int>() == 3, "get_open_documents: " + open.ToJsonString());
        JsonNode Body(JsonNode result) { string text = result["content"]![0]!["text"]!.ToString(); try { return JsonNode.Parse(text)!; } catch (System.Text.Json.JsonException) { throw new InvalidOperationException("tool reply is not JSON: " + text); } }
        var slot = Body(await Call(20, "tools/call", new JsonObject { ["name"] = "get_mission_object",
            ["arguments"] = new JsonObject { ["id"] = "pf_event_multipoint.pf_placement[0].o_generator" } }));
        Expect(slot["slotId"]?.ToString() == "slot/80be5d9b/000014/0014/0004" && slot["prefab"]?.ToString() == "0x80BE5DA0"
            && slot["transform"]?["position"]?[0]?.GetValue<double>() == 1 && slot["code"]?["count"]?.GetValue<int>() == 3, "get_mission_object: " + slot.ToJsonString());
        var spoken = Body(await Call(21, "tools/call", new JsonObject { ["name"] = "find_object", ["arguments"] = new JsonObject { ["query"] = "o_generator" } }));
        Expect(spoken["matches"]?.AsArray().Count > 0 && spoken["matches"]?[0]?["id"]?.ToString()?.StartsWith("slot/80be5d9b/") == true && spoken["matches"]?[0]?["next"]?.ToString() == "get_mission_object",
            "find_object reaches slots: " + spoken.ToJsonString());
        var listed = Body(await Call(22, "tools/call", new JsonObject { ["name"] = "list_mission_objects", ["arguments"] = new JsonObject { ["limit"] = 2 } }));
        Expect(listed["total"]?.GetValue<int>() == 5 && listed["count"]?.GetValue<int>() == 2 && listed["next"]?.GetValue<int>() == 2, "list_mission_objects paging");
        var panel = Body(await Call(23, "tools/call", new JsonObject { ["name"] = "read_panel", ["arguments"] = new JsonObject { ["panel"] = "hierarchy" } }));
        Expect(panel["panel"]?.ToString() == "hierarchy" && host.Panels == "hierarchy", "read_panel reaches the host");
        var badPanel = await Call(24, "tools/call", new JsonObject { ["name"] = "read_panel", ["arguments"] = new JsonObject { ["panel"] = "nope" } });
        Expect(badPanel["isError"]?.GetValue<bool>() == true, "unknown panel");
        var reference = Body(await Call(25, "tools/call", new JsonObject { ["name"] = "search_reference", ["arguments"] = new JsonObject { ["query"] = "replication" } }));
        Expect(reference["hits"]?[0]?["path"]?.ToString() == "unreal/replication.md", "search_reference: " + reference.ToJsonString());
        var maps = Body(await Call(30, "tools/call", new JsonObject { ["name"] = "list_maps", ["arguments"] = new JsonObject { ["filter"] = "dark forest" } }));
        Expect(maps["rows"]?[0]?["name"]?.ToString() == "The EDZ" && host.Queries.Contains("list_maps"), "list_maps reaches the host: " + maps.ToJsonString());
        var failed = await Call(31, "tools/call", new JsonObject { ["name"] = "get_item", ["arguments"] = new JsonObject { ["item"] = "0x1" } });
        Expect(failed["isError"]?.GetValue<bool>() == true && failed["content"]![0]!["text"]!.ToString().Contains("loading", StringComparison.Ordinal), "host errors are tool errors");
        // Shader, animation and audio metadata reads go to the host's package readers; bad tags fail before reaching it.
        var shader = Body(await Call(60, "tools/call", new JsonObject { ["name"] = "get_shader",
            ["arguments"] = new JsonObject { ["tag"] = "0x80A1B2C3", ["disassembly"] = true } }));
        Expect(shader["profile"]?.ToString() == "ps_5_0" && shader["constantBuffers"]?[0]?["slot"]?.GetValue<int>() == 12
            && host.Queries.Contains("get_shader"), "get_shader reaches the host: " + shader.ToJsonString());
        int assetQueries = host.Queries.Count;
        foreach (string name in new[] { "get_shader", "get_animation", "get_audio" })
        {
            var noTag = await Call(61, "tools/call", new JsonObject { ["name"] = name, ["arguments"] = new JsonObject { ["tag"] = "speaker" } });
            Expect(noTag["isError"]?.GetValue<bool>() == true && host.Queries.Count == assetQueries, name + " checks its tag first");
        }
        var clip = await Call(62, "tools/call", new JsonObject { ["name"] = "get_animation", ["arguments"] = new JsonObject { ["tag"] = "0x80800001" } });
        Expect(clip["isError"]?.GetValue<bool>() == true && clip["content"]![0]!["text"]!.ToString().Contains("neither an animation", StringComparison.Ordinal),
            "get_animation host errors are tool errors");
        foreach (string name in new[] { "list_shaders", "list_animations", "list_audio" })
        {
            var tool = tools.First(row => row?["name"]?.ToString() == name)!;
            Expect(tool["inputSchema"]?["properties"]?["offset"] is not null && tool["annotations"]?["readOnlyHint"]?.GetValue<bool>() != false, name + " is a paged read");
            var readRows = Body(await Call(63, "tools/call", new JsonObject { ["name"] = name, ["arguments"] = new JsonObject { ["limit"] = 5 } }));
            Expect(readRows["rows"] is JsonArray && host.Queries.Contains(name), name + " reaches the host");
        }
        int asked = host.Approvals;
        var badBounds = await Call(32, "tools/call", new JsonObject { ["name"] = "edit_bounds", ["arguments"] = new JsonObject { ["id"] = "w:9", ["minimum"] = new JsonArray(1, 2) } });
        Expect(badBounds["isError"]?.GetValue<bool>() == true && host.Approvals == asked, "edit_bounds validates before asking");
        var move = await Call(33, "tools/call", new JsonObject { ["name"] = "edit_placement",
            ["arguments"] = new JsonObject { ["id"] = "w:9", ["position"] = new JsonArray(1, 2, 3) } });
        Expect(move["isError"]?.GetValue<bool>() == true && host.Approvals == asked + 1 && host.LastSummary?.Contains("[1,2,3]") == true,
            "edit_placement waits for approval: " + host.LastSummary);
        // T1.4/T1.5: compare_with_base and get_item_sources are host reads; revert_to_base checks its hash, then waits for approval.
        var compared = await Call(34, "tools/call", new JsonObject { ["name"] = "compare_with_base", ["arguments"] = new JsonObject { ["hash"] = "0x80A1B2C3", ["field"] = "0x40+8" } });
        var sources = await Call(35, "tools/call", new JsonObject { ["name"] = "get_item_sources", ["arguments"] = new JsonObject { ["item"] = "Ace of Spades", ["kind"] = "vendor" } });
        Expect(compared["isError"]?.GetValue<bool>() != true && sources["isError"]?.GetValue<bool>() != true
            && host.Queries.Contains("compare_with_base") && host.Queries.Contains("get_item_sources"), "compare_with_base and get_item_sources reach the host");
        Expect(tools.First(row => row?["name"]?.ToString() == "get_item_sources")!["inputSchema"]?["properties"]?["offset"] is not null, "get_item_sources is paged");
        asked = host.Approvals;
        var badRevert = await Call(36, "tools/call", new JsonObject { ["name"] = "revert_to_base", ["arguments"] = new JsonObject { ["hash"] = "speaker" } });
        Expect(badRevert["isError"]?.GetValue<bool>() == true && host.Approvals == asked, "revert_to_base validates its hash before asking");
        await Call(37, "tools/call", new JsonObject { ["name"] = "revert_to_base", ["arguments"] = new JsonObject { ["hash"] = "0x80A1B2C3", ["field"] = "0x40+8" } });
        Expect(host.Approvals == asked + 1 && host.LastSummary?.Contains("0x40+8") == true, "revert_to_base waits for approval: " + host.LastSummary);
        // UI control and live viewport tools.
        var controls = Body(await Call(40, "tools/call", new JsonObject { ["name"] = "list_ui_controls", ["arguments"] = new JsonObject { ["query"] = "grid" } }));
        Expect(controls["rows"]?[0]?["id"]?.ToString() == "main:00000003" && controls["rows"]?[0]?["checked"]?.GetValue<bool>() == true
            && controls["drawn"]?.GetValue<int>() == FixtureHost.Controls.Length, "list_ui_controls: " + controls.ToJsonString());
        asked = host.Approvals;
        var ambiguous = await Call(41, "tools/call", new JsonObject { ["name"] = "ui_invoke", ["arguments"] = new JsonObject { ["query"] = "Delete" } });
        Expect(ambiguous["isError"]?.GetValue<bool>() == true && host.Approvals == asked
            && ambiguous["content"]![0]!["text"]!.ToString().Contains("main:00000006", StringComparison.Ordinal), "a tied query is refused before asking");
        var wrongKind = await Call(42, "tools/call", new JsonObject { ["name"] = "ui_set_checked", ["arguments"] = new JsonObject { ["id"] = "main:00000001", ["checked"] = true } });
        Expect(wrongKind["isError"]?.GetValue<bool>() == true && host.Approvals == asked, "a button does not take ui_set_checked");
        var declined = await Call(43, "tools/call", new JsonObject { ["name"] = "ui_invoke", ["arguments"] = new JsonObject { ["query"] = "Build" } });
        Expect(declined["isError"]?.GetValue<bool>() == true && host.Approvals == asked + 1 && host.LastSummary == "Click button 'Build' in Toolbar"
            && host.Commands.Count == 0, "ui_invoke waits for approval: " + host.LastSummary);
        host.ApproveAll = true;
        var approved = await Call(44, "tools/call", new JsonObject { ["name"] = "ui_set_checked", ["arguments"] = new JsonObject { ["query"] = "grid", ["checked"] = false } });
        Expect(approved["isError"]?.GetValue<bool>() != true && host.Commands.Count == 1 && host.Commands[0] == new UiCommand("main:00000003", UiOperation.SetChecked, null, false),
            "an approved ui_set_checked reaches the host");
        var combo = await Call(45, "tools/call", new JsonObject { ["name"] = "ui_set_value", ["arguments"] = new JsonObject { ["query"] = "follow", ["kind"] = "combo", ["value"] = "Free" } });
        Expect(combo["isError"]?.GetValue<bool>() != true && host.Commands[^1].Operation == UiOperation.Select && host.Commands[^1].Value == "Free", "a combo's value is a select");
        host.ApproveAll = false;
        // Auto-approve: an action tool completes with no click and is logged; off, it still waits (declined here).
        host.AutoApprove = true;
        int clicks = host.Approvals;
        var auto = await Call(460, "tools/call", new JsonObject { ["name"] = "ui_invoke", ["arguments"] = new JsonObject { ["query"] = "Build" } });
        Expect(auto["isError"]?.GetValue<bool>() != true && host.Approvals == clicks && host.AutoLog.Count == 1, "auto-approve runs the action with no click");
        host.AutoApprove = false;
        var waits = await Call(461, "tools/call", new JsonObject { ["name"] = "ui_invoke", ["arguments"] = new JsonObject { ["query"] = "Build" } });
        Expect(waits["isError"]?.GetValue<bool>() == true && host.Approvals == clicks + 1 && host.AutoLog.Count == 1, "auto-approve off still waits for the click");
        var frame = await Call(46, "tools/call", new JsonObject { ["name"] = "get_viewport_frame", ["arguments"] = new JsonObject() });
        var frameText = Body(frame);
        Expect(frame["content"]?.AsArray().Count == 2 && frame["content"]![1]!["mimeType"]?.ToString() == "image/jpeg"
            && frameText["frames"]?[0]?["frame"]?.GetValue<long>() == 1 && frameText["frames"]?[0]?["change"]?.ToString() == "first frame"
            && frameText["objects"] is JsonArray, "get_viewport_frame: JPEG plus text: " + frameText.ToJsonString());
        var known = await Call(47, "tools/call", new JsonObject { ["name"] = "get_viewport_frame", ["arguments"] = new JsonObject { ["since"] = 1 } });
        Expect(known["content"]?.AsArray().Count == 1 && Body(known)["note"] is not null, "a known frame id returns text only");
        var still = Body(await Call(48, "tools/call", new JsonObject { ["name"] = "wait_for_viewport_change", ["arguments"] = new JsonObject { ["timeout"] = 0.2 } }));
        Expect(still["changed"]?.GetValue<bool>() == false && still["latest"]?.GetValue<long>() == 1, "wait_for_viewport_change times out on a still view");
        var streamInfo = Body(await Call(49, "tools/call", new JsonObject { ["name"] = "get_viewport_stream", ["arguments"] = new JsonObject() }));
        Expect(streamInfo["status"] is not null && streamInfo["fps"] is not null, "get_viewport_stream: " + streamInfo.ToJsonString());
        // Player position.
        var player = Body(await Call(50, "tools/call", new JsonObject { ["name"] = "get_player_position", ["arguments"] = new JsonObject() }));
        Expect(player["source"]?.ToString() == "recorder" && host.Queries.Contains("get_player_position"), "get_player_position reaches the host");
        var arrived = Body(await Call(51, "tools/call", new JsonObject { ["name"] = "wait_for_player_near",
            ["arguments"] = new JsonObject { ["target"] = "slot/generator", ["radius"] = 5, ["timeout"] = 10 } }));
        Expect(arrived["reached"]?.GetValue<bool>() == true && arrived["distance"]?.GetValue<double>() <= 5, "wait_for_player_near: " + arrived.ToJsonString());
        var away = Body(await Call(52, "tools/call", new JsonObject { ["name"] = "wait_for_player_near",
            ["arguments"] = new JsonObject { ["position"] = new JsonArray(500, 0, 0), ["radius"] = 1, ["timeout"] = 0.3 } }));
        Expect(away["reached"]?.GetValue<bool>() == false && away["note"]?.ToString() == "Timed out.", "wait_for_player_near times out");
        var missing = await Call(53, "tools/call", new JsonObject { ["name"] = "wait_for_player_near", ["arguments"] = new JsonObject { ["target"] = "missing" } });
        Expect(missing["isError"]?.GetValue<bool>() == true, "an unknown target is a tool error");
        var noGoal = await Call(54, "tools/call", new JsonObject { ["name"] = "wait_for_player_near", ["arguments"] = new JsonObject() });
        Expect(noGoal["isError"]?.GetValue<bool>() == true, "wait_for_player_near needs a target or position");
        host.Paused = true;
        var pausedMaps = await Call(34, "tools/call", new JsonObject { ["name"] = "list_maps", ["arguments"] = new JsonObject() });
        Expect(pausedMaps["isError"]?.GetValue<bool>() != true, "catalog reads work while paused");
        var pausedAudio = await Call(64, "tools/call", new JsonObject { ["name"] = "list_audio", ["arguments"] = new JsonObject() });
        Expect(pausedAudio["isError"]?.GetValue<bool>() != true, "shader, animation and audio reads work while paused");
        var pausedWorld = await Call(35, "tools/call", new JsonObject { ["name"] = "find_world_objects", ["arguments"] = new JsonObject() });
        Expect(pausedWorld["isError"]?.GetValue<bool>() == true, "world reads stop while paused");
        var pausedReference = await Call(26, "tools/call", new JsonObject { ["name"] = "read_reference", ["arguments"] = new JsonObject { ["path"] = "unreal/replication.md" } });
        Expect(pausedReference["isError"]?.GetValue<bool>() != true, "the reference library stays readable while paused");
        var pausedMission = await Call(27, "tools/call", new JsonObject { ["name"] = "list_mission_objects", ["arguments"] = new JsonObject() });
        Expect(pausedMission["isError"]?.GetValue<bool>() == true, "mission objects hidden while paused");
        var paused = await Call(7, "tools/call", new JsonObject { ["name"] = "get_open_documents", ["arguments"] = new JsonObject() });
        Expect(paused["isError"]?.GetValue<bool>() == true, "get_open_documents while paused");
        host.Paused = false;
    }
    finally
    {
        process.StandardInput.Close();
        if (!process.WaitForExit(5000)) process.Kill();
    }
    // Wrong token is refused.
    using var http = new HttpClient();
    var wrong = await http.PostAsync(server.Url, new StringContent("{}"));
    Expect((int)wrong.StatusCode == 401, "token check");
    // The local viewport pages take their own key, never the MCP token, and refuse framing.
    server.Viewport = host.Viewport;
    string root = server.Url[..server.Url.LastIndexOf("/mcp", StringComparison.Ordinal)];
    var noKey = await http.GetAsync(root + "/viewport.jpg");
    var tokenKey = await http.GetAsync(root + "/viewport.jpg?key=" + server.Token);
    var jpeg = await http.GetAsync(root + "/viewport.jpg?key=" + server.ViewKey);
    var page = await http.GetAsync(server.ViewportUrl);
    Expect((int)noKey.StatusCode == 401 && (int)tokenKey.StatusCode == 401 && (int)jpeg.StatusCode == 200
        && jpeg.Content.Headers.ContentType?.MediaType == "image/jpeg" && (await jpeg.Content.ReadAsByteArrayAsync()).SequenceEqual(FixtureHost.Jpeg)
        && jpeg.Headers.TryGetValues("X-Frame-Options", out var frameOptions) && frameOptions.Single() == "DENY"
        && (await page.Content.ReadAsStringAsync()).Contains("/viewport.mjpeg?key=" + server.ViewKey, StringComparison.Ordinal), "local viewport pages");
    await GenericClient(host, server, shim, project, discovery);
}

/** The official MCP Python SDK as a client nobody here wrote: over streamable HTTP and through the stdio shim. */
async Task GenericClient(FixtureHost host, McpHttpServer server, string shim, string project, string discovery)
{
    string? script = null;
    for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null && script is null; folder = folder.Parent)
        foreach (string candidate in new[] { Path.Combine(folder.FullName, "generic_client.py"),
                     Path.Combine(folder.FullName, "tests", "SunriseStudio.Mcp.Tests", "generic_client.py") })
            if (File.Exists(candidate)) { script = candidate; break; }
    Expect(script is not null, "generic_client.py not found");
    async Task<JsonNode?> Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("python") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(script!);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["SOLFORGE_MCP_DISCOVERY"] = discovery;
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        Process process;
        try { process = Process.Start(start)!; }
        catch (System.ComponentModel.Win32Exception) { Console.WriteLine("SKIP generic client: python is not on PATH"); return null; }
        using var _ = process;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000)) { process.Kill(); Expect(false, "generic client hung: " + await errors); }
        if (process.ExitCode == 3) { Console.WriteLine("SKIP generic client: pip install mcp"); return null; }
        Expect(process.ExitCode == 0, $"generic client ({arguments[0]}) failed: {await errors}");
        return JsonNode.Parse((await output).Trim().Split('\n')[^1]);
    }
    foreach (var result in new[] { await Run("http", server.Url, server.Token), await Run("stdio", "dotnet", shim, "--project", project) })
    {
        if (result is null) continue;
        var tools = result["tools"]!.AsArray().Select(node => node!.ToString()).ToArray();
        Expect(result["server"]?.ToString() == "sol" && tools.Contains("ui_invoke") && tools.Contains("wait_for_viewport_change")
            && result["schemas"]?.GetValue<bool>() == true, "generic client sees the tools: " + result.ToJsonString());
        Expect(result["mimes"]?[0]?.ToString() == "image/jpeg" && result["frameText"]?.ToString().Contains("\"frame\"", StringComparison.Ordinal) == true,
            "generic client gets the frame with its text");
        Expect(result["controls"]?.ToString().Contains("main:00000003", StringComparison.Ordinal) == true && result["deniedError"]?.GetValue<bool>() == true,
            "generic client lists controls and is held at the approval gate");
    }
}

static void PlayerSourceChecks()
{
    var when = DateTimeOffset.UnixEpoch;
    var walking = PlayerSources.ParseRecorder("{\"t\":1,\"utc\":\"2026-10-15T18:31:49.044Z\",\"ui\":{\"in_world\":true},\"loc\":{\"activity\":\"The EDZ\",\"bubble\":\"Trostland\",\"slice\":\"3 x\"},\"pos\":[10,20,5],\"cam\":[10.5,20,6.7],\"yaw\":90,\"pitch\":0}", when);
    Expect(walking is { Source: "recorder", Activity: "The EDZ", Bubble: "Trostland" } && walking.Position == new Vector3(10, 20, 5) && walking.Yaw == 90
        && walking.Time.Year == 2026, "recorder pos");
    // A real snapshot (EDZ Trostland, third person, 2026-10-15 recorder): the camera orbits ~3 m off the body; pos is right.
    var thirdPerson = PlayerSources.ParseRecorder("{\"ui\":{\"in_world\":true},\"player\":{\"pos_valid\":true},\"pos\":[556.48,61.84,78.26],\"cam\":[553.6,62.9,78.9],\"yaw\":12}", when);
    Expect(thirdPerson is not null && Vector3.Distance(thirdPerson.Position, new Vector3(556.48f, 61.84f, 78.26f)) < 1e-3f, "a third-person camera does not replace pos");
    var invalid = PlayerSources.ParseRecorder("{\"player\":{\"pos_valid\":false},\"pos\":[5,5,5],\"cam\":[6,5,6.7]}", when);
    Expect(invalid is not null && Vector3.Distance(invalid.Position, new Vector3(6, 5, 5)) < 1e-3f, "an invalid pos uses the camera");
    Expect(PlayerSources.IsScenario("edz_freeroam:scenario_client", "EDZ_Freeroam") && !PlayerSources.IsScenario("edz_freeroam", "unknown")
        && !PlayerSources.IsScenario(null, "edz_freeroam"), "recorder activity matches scenario named tags");
    var frozen = PlayerSources.ParseRecorder("{\"ui\":{\"in_world\":true},\"pos\":[0,0,0],\"cam\":[100,50,11.7],\"yaw\":0}", when);
    Expect(frozen is not null && Vector3.Distance(frozen.Position, new Vector3(100, 50, 10)) < 1e-3f && frozen.Time == when, "a frozen pos gives way to the camera, lowered to the feet");
    var stale = PlayerSources.ParseRecorder("{\"pos\":[1,1,1],\"pos_stale_ms\":900,\"cam\":[1,1,2.7]}", when);
    Expect(stale is not null && Vector3.Distance(stale.Position, new Vector3(1, 1, 1)) < 1e-3f, "a stale pos uses the camera");
    Expect(PlayerSources.ParseRecorder("{\"ui\":{\"in_world\":false},\"pos\":null,\"cam\":null}", when) is null
        && PlayerSources.ParseRecorder("{\"mark\":\"x\",\"pos\":[1,2,3]}", when) is null && PlayerSources.ParseRecorder("not json", when) is null, "menus, marks and junk");
    string root = Path.Combine(Path.GetTempPath(), "solforge-recorder-" + Environment.ProcessId);
    Directory.CreateDirectory(Path.Combine(root, "20261015-120000"));
    try
    {
        File.WriteAllText(Path.Combine(root, "20261015-120000", "snapshots.jsonl"),
            "{\"ui\":{\"in_world\":true},\"pos\":[1,2,3]}\n{\"mark\":\"note\"}\n");
        Expect(PlayerSources.LatestRecorder(root)?.Position == new Vector3(1, 2, 3), "the newest snapshot, skipping marks");
        string empty = Path.Combine(root, "empty-session");
        Directory.CreateDirectory(empty);
        Directory.SetLastWriteTimeUtc(empty, DateTime.UtcNow.AddMinutes(1));
        Expect(PlayerSources.LatestRecorder(root)?.Position == new Vector3(1, 2, 3), "an empty session does not hide a recording");
        string other = Path.Combine(root, "other-root");
        Directory.CreateDirectory(Path.Combine(other, "session"));
        string otherSnapshot = Path.Combine(other, "session", "snapshots.jsonl");
        File.WriteAllText(otherSnapshot, "{\"ui\":{\"in_world\":false}}\n");
        File.SetLastWriteTimeUtc(otherSnapshot, DateTime.UtcNow.AddSeconds(1));
        Expect(PlayerSources.LatestRecorderRoot([root, other]) == other && PlayerSources.LatestRecorder(other) is null,
            "a newer menu in another root wins over stale in-world data");
        File.AppendAllText(Path.Combine(root, "20261015-120000", "snapshots.jsonl"), "{\"ui\":{\"in_world\":false},\"pos\":null}\n");
        Expect(PlayerSources.LatestRecorder(root) is null, "a menu after a position hides the old position");
    }
    finally { Directory.Delete(root, true); }
    var coop = PlayerSources.ParseCoop("{\"ok\":true,\"players\":[{\"id\":1,\"name\":\"wyndxgo\",\"room\":\"0x1234ABCD\",\"x\":1.5,\"y\":2,\"z\":3}," +
        "{\"id\":2,\"name\":\"loading\",\"room\":\"0x00000000\",\"x\":0,\"y\":0,\"z\":0}]}", when);
    Expect(coop.Count == 1 && coop[0] is { Source: "server", Player: "wyndxgo", Room: "0x1234ABCD" } && coop[0].Position == new Vector3(1.5f, 2, 3), "server /coop rows");
    Expect(MathF.Abs(PlayerSources.YawFromForward(new Vector3(0, 1, 0)) - 90) < 1e-3f && Vector3.Distance(PlayerSources.Facing(180), new Vector3(-1, 0, 0)) < 1e-3f, "yaw and facing");
    var trail = new PlayerTrail(capacity: 3, spacing: 1, teleport: 50);
    trail.Add(Vector3.Zero); trail.Add(new Vector3(.5f, 0, 0)); trail.Add(new Vector3(2, 0, 0)); trail.Add(new Vector3(4, 0, 0)); trail.Add(new Vector3(6, 0, 0));
    Expect(trail.Points.Count == 3 && trail.Points[0] == new Vector3(2, 0, 0), "trail spacing and capacity");
    trail.Add(new Vector3(500, 0, 0));
    Expect(trail.Points.Count == 1, "a teleport restarts the trail");
}

static void UiControlChecks()
{
    var controls = FixtureHost.Controls;
    Expect(UiControls.Resolve(controls, new JsonObject { ["id"] = "MAIN:00000003" }).Control?.Label == "Grid", "id is exact, case-insensitive");
    Expect(UiControls.Resolve(controls, new JsonObject { ["id"] = "main:0000FFFF" }).Error is not null, "unknown id");
    Expect(UiControls.Resolve(controls, new JsonObject { ["query"] = "build" }).Control?.Id == "main:00000001", "exact label beats a prefix");
    Expect(UiControls.Resolve(controls, new JsonObject { ["query"] = "build to" }).Control?.Id == "main:00000002", "prefix");
    Expect(UiControls.Resolve(controls, new JsonObject { ["query"] = "delete" }).Error?.Contains("main:00000007", StringComparison.Ordinal) == true, "a tie lists the candidates");
    Expect(UiControls.Resolve(controls, new JsonObject { ["query"] = "delete", ["window"] = "hierarchy" }).Control?.Id == "main:00000007", "window narrows a tie");
    Expect(UiControls.Resolve(controls, new JsonObject { ["query"] = "file folder" }).Control?.Id == "main:00000002", "words in scope and label");
    Expect(UiControls.Find(controls, null, "checkbox", null, false).Single().Id == "main:00000003", "kind filter");
    Expect(UiControls.Find(controls, null, null, null, false).All(control => control.Visible)
        && UiControls.Find(controls, null, null, null, true).Count == controls.Length, "hidden controls only with all");
    Expect(UiControls.Allows("checkbox", UiOperation.SetChecked) && !UiControls.Allows("button", UiOperation.SetValue)
        && UiControls.Allows("combo", UiOperation.Select) && UiControls.Allows("text", UiOperation.SetValue), "operations per kind");
    Expect(UiControls.Summary(controls[2], UiOperation.SetChecked, null, false) == "Turn off 'Grid' in Viewport", "summary");
    Expect(UiControls.ToJson(controls[3])["options"]?.AsArray().Count == 2 && UiControls.ToJson(controls[3])["name"]?.ToString() == "##follow", "json row");
}

// Screen OCR: request lines, reply parsing, summaries and region rules run anywhere; the live part reads a fixture with
// Windows.Media.Ocr on the CPU (Windows only; skipped elsewhere or when no OCR language is installed).
static async Task<string> ScreenOcrChecks()
{
    string script = ScreenOcr.ScriptText();
    Expect(script.Contains("Windows.Media.Ocr") && script.Contains("$Serve"), "ocr script embedded");
    Expect(ScreenOcr.ParseRegion(null, out string? none) is null && none is null, "no region");
    Expect(ScreenOcr.ParseRegion(JsonNode.Parse("[10, 20.4, 300, 40]"), out _) is [10, 20, 300, 40], "array region");
    Expect(ScreenOcr.ParseRegion(JsonValue.Create("5, 6 7,8"), out _) is [5, 6, 7, 8], "string region");
    foreach (var bad in new[] { "[1, 2, 3]", "[0, 0, 0, 5]", "[-1, 0, 5, 5]", "[1, \"x\", 5, 5]", "\"a,b,c,d\"" }.Select(json => JsonNode.Parse(json)!))
        Expect(ScreenOcr.ParseRegion(bad, out string? error) is null && error is not null, "bad region " + bad.ToJsonString());
    var request = JsonNode.Parse(ScreenOcr.Request("frame.png", [1, 2, 3, 4], 50, " en-US "))!.AsObject();
    Expect(Path.IsPathRooted(request["path"]!.ToString()) && request["region"]!.AsArray().Count == 4 && request["scale"]!.GetValue<double>() == 8
        && request["lang"]!.ToString() == "en-US", "ocr request " + request.ToJsonString());
    Expect(JsonNode.Parse(ScreenOcr.Request("f.png", null, 0, null))!.AsObject().Count == 1, "minimal ocr request");
    Expect(!ScreenOcr.Ok(ScreenOcr.Parse("WARNING: something")) && !ScreenOcr.Ok(ScreenOcr.Parse("{\"ok\":false,\"error\":\"x\"}")), "ocr failures");
    var reply = ScreenOcr.Parse("{\"ok\":true,\"width\":640,\"height\":160,\"region\":[0,0,640,160],\"scale\":2,\"lang\":\"en-US\",\"text\":\"QUESTS Launch now\"," +
        "\"lines\":[{\"text\":\"QUESTS\",\"box\":[29,38,81,18],\"words\":[{\"text\":\"QUESTS\",\"box\":[29,38,81,18]}]}," +
        "{\"text\":\"Launch now\",\"box\":[28,106,148,14],\"words\":[{\"text\":\"Launch\",\"box\":[28,106,60,14]},{\"text\":\"now\",\"box\":[96,106,40,14]}]}]}");
    var all = ScreenOcr.Summarize(reply, false, null);
    Expect(all["lineCount"]!.GetValue<int>() == 2 && all["lines"]![0]!["words"] is null && all["text"]!.ToString() == "QUESTS Launch now", "ocr summary");
    var launch = ScreenOcr.Summarize(reply, true, "launch");
    Expect(launch["lineCount"]!.GetValue<int>() == 1 && launch["lines"]![0]!["words"]!.AsArray().Count == 2 && launch["text"]!.ToString() == "Launch now",
        "ocr contains filter");
    Expect(ScreenOcr.Summarize(ScreenOcr.Failure("nope"), true, null)["ok"]!.GetValue<bool>() == false, "ocr failure summary");

    string fixture = Path.Combine(AppContext.BaseDirectory, "ocr-sample.png");
    if (!ScreenOcr.Available || !File.Exists(fixture)) return "screen OCR: region and request rules, reply summaries and filters (live OCR skipped: not Windows or no fixture)";
    using var ocr = new ScreenOcr();
    var full = await ocr.ReadFileAsync(fixture, null, 0, null, CancellationToken.None);
    if (!ScreenOcr.Ok(full) && (full["error"]?.ToString() ?? "").Contains("No Windows OCR language"))
        return "screen OCR: region and request rules, reply summaries and filters (live OCR skipped: no OCR language installed)";
    string text = full["text"]?.ToString() ?? "";
    Expect(ScreenOcr.Ok(full) && text.Contains("QUESTS") && text.Contains("DESTINATIONS") && text.Contains("Invite Friends"), "live ocr text: " + full.ToJsonString());
    var quests = full["lines"]!.AsArray().First(line => line!["text"]!.ToString() == "QUESTS")!["box"]!.AsArray();
    Expect(Math.Abs(quests[0]!.GetValue<double>() - 29) < 6 && Math.Abs(quests[1]!.GetValue<double>() - 38) < 6, "live ocr box " + quests.ToJsonString());
    var right = await ocr.ReadFileAsync(fixture, [300, 0, 340, 80], 0, null, CancellationToken.None);
    var rightLines = right["lines"]!.AsArray();
    Expect(ScreenOcr.Ok(right) && rightLines.Count == 1 && rightLines[0]!["text"]!.ToString() == "DESTINATIONS"
        && rightLines[0]!["box"]![0]!.GetValue<double>() > 340, "live ocr region keeps image coordinates: " + right.ToJsonString());
    var bytes = await ocr.ReadAsync(await File.ReadAllBytesAsync(fixture), null, 0, "en-US", CancellationToken.None);
    Expect(ScreenOcr.Ok(bytes) && bytes["text"]!.ToString().Contains("1234"), "live ocr from bytes");
    return "screen OCR: region and request rules, reply summaries and filters; Windows.Media.Ocr reads the fixture's HUD labels with " +
        "image-pixel boxes, a region reads only its part in full-image coordinates, and one helper process serves several requests";
}

static void ViewportFeedChecks()
{
    static byte[] Image(int width, int height, Func<int, int, byte> shade)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                byte value = shade(x, y);
                pixels[(y * width + x) * 4] = value; pixels[(y * width + x) * 4 + 1] = value; pixels[(y * width + x) * 4 + 2] = value; pixels[(y * width + x) * 4 + 3] = 255;
            }
        return pixels;
    }
    var dark = ViewportFeed.Measure(Image(64, 36, (_, _) => 10), 64, 36, 64 * 4);
    Expect(dark.Length == ViewportFeed.GridWidth * ViewportFeed.GridHeight && dark.All(value => value == 10), "grid of a flat image");
    var noisy = ViewportFeed.Measure(Image(64, 36, (x, y) => (byte)(10 + (x + y) % 2)), 64, 36, 64 * 4);
    var corner = ViewportFeed.Measure(Image(64, 36, (x, y) => x >= 48 && y < 9 ? (byte)200 : (byte)10), 64, 36, 64 * 4);
    var feed = new ViewportFeed(3);
    int encoded = 0;
    (byte[], int, int) Encode() { encoded++; return ([0xFF, 0xD8, 0xFF, (byte)encoded], 64, 36); }
    var first = feed.Offer(dark, 1, Encode);
    Expect(first?.Id == 1 && first.Change == "first frame" && encoded == 1, "first frame");
    Expect(feed.Offer(noisy, 2, Encode) is null && encoded == 1 && feed.LastCapture is not null, "noise is not a change and is not encoded");
    var moved = feed.Offer(corner, 3, Encode);
    Expect(moved?.Id == 2 && moved.Change.Contains("top-right", StringComparison.Ordinal) && moved.Change.Contains("brighter", StringComparison.Ordinal)
        && moved.Change.StartsWith("6.9% of the view changed", StringComparison.Ordinal), "change text: " + moved?.Change); // 8x5 changed cells of the 32x18 grid: row 4 is half covered and still counts.
    feed.Offer(dark, 4, Encode);
    feed.Offer(corner, 5, Encode);
    Expect(feed.Latest?.Id == 4 && feed.Since(0, 10).Select(frame => frame.Id).SequenceEqual([2L, 3L, 4L]) && feed.Since(2, 1).Single().Id == 4, "ids, since and capacity");
    var waiting = feed.WaitForChangeAsync(4, TimeSpan.FromSeconds(5), CancellationToken.None);
    Expect(!waiting.IsCompleted, "wait blocks on a still view");
    feed.Offer(dark, 6, Encode);
    Expect(waiting.Wait(2000) && waiting.Result?.Id == 5, "wait wakes on a change");
    Expect(feed.WaitForChangeAsync(5, TimeSpan.FromMilliseconds(50), CancellationToken.None).Result is null, "wait times out");
    Expect(feed.WaitForChangeAsync(3, TimeSpan.Zero, CancellationToken.None).Result?.Id == 5, "an old id returns the newest frame at once");
    Expect(new ViewportFrame(1, DateTimeOffset.UnixEpoch, 1, 1, [0xFF, 0xD8, 0xFF], 1, "x").Jpeg.Length == 3
        && McpToolResult.MimeType([0xFF, 0xD8, 0xFF]) == "image/jpeg" && McpToolResult.MimeType([0x89, 0x50]) == "image/png", "image MIME types");
}

void CommandResolution()
{
    string bin = Path.Combine(scratch, "fake bin"), other = Path.Combine(scratch, "other");
    Directory.CreateDirectory(bin);
    Directory.CreateDirectory(other);
    File.WriteAllText(Path.Combine(bin, "fakeengine"), "#!/bin/sh\n");
    File.WriteAllText(Path.Combine(bin, "fakeengine.cmd"), "@echo off\r\n");
    File.WriteAllText(Path.Combine(other, "fakeengine.exe"), "");
    string path = $"{Path.Combine(scratch, "missing")};\"{bin}\";{other}";
    Expect(EngineCommands.Resolve("fakeengine", path, ".COM;.EXE;.BAT;.CMD") == Path.Combine(bin, "fakeengine.cmd"), "PATH order, then PATHEXT");
    Expect(EngineCommands.Resolve("fakeengine", path, ".EXE") == Path.Combine(other, "fakeengine.exe"), "PATHEXT filter");
    Expect(EngineCommands.Resolve("fakeengine.cmd", path) == Path.Combine(bin, "fakeengine.cmd"), "name with extension");
    Expect(EngineCommands.Resolve(Path.Combine(other, "fakeengine"), "") == Path.Combine(other, "fakeengine.exe"), "explicit folder");
    Expect(EngineCommands.Resolve("agy", path) is null && EngineCommands.Resolve(" ", path) is null, "missing engine");
    Expect(EngineCommands.Join([$"{bin};{other}", $"{other}\\;{bin}", ""]) == $"{bin};{other}", "PATH join keeps the first of each folder");
}

void ClaudeLaunch()
{
    string top = Path.Combine(scratch, "launch root");
    string project = Path.Combine(top, "10-Project Sol", "Project Sol");
    string game = Path.Combine(scratch, "Sunrise Game");
    Directory.CreateDirectory(Path.Combine(project, "scripts"));
    Directory.CreateDirectory(game);
    File.WriteAllText(Path.Combine(top, "10-Project Sol", "CLAUDE.md"), "rules");
    string shim = Path.Combine(scratch, "Solforge App", "solforge-mcp.exe");
    var files = EngineLaunch.WriteClaudeFiles(project, shim, Path.Combine(scratch, "mcp"));
    var config = JsonNode.Parse(File.ReadAllText(files.McpConfig))!["mcpServers"]?["sol"];
    Expect(config?["command"]?.ToString() == shim && config?["type"]?.ToString() == "stdio"
        && config?["args"]?.AsArray().Select(node => node?.ToString()).SequenceEqual(["--sol", "--project", project]) == true, "mcp-config entry");
    string? hook = JsonNode.Parse(File.ReadAllText(files.Settings))?["hooks"]?["UserPromptSubmit"]?[0]?["hooks"]?[0]?["command"]?.ToString();
    Expect(hook == $"\"{shim.Replace('\\', '/')}\" --context --project \"{project.Replace('\\', '/')}\"", "prompt hook: " + hook);
    // The scratch folder stands in for the user profile: ancestors stop below it.
    var folders = EngineLaunch.AddDirs(project, [game, Path.Combine(project, "scripts"), Path.Combine(scratch, "gone"), null, game + "\\"], scratch);
    string ancestor = Path.Combine(top, "10-Project Sol");
    Expect(folders.SequenceEqual([game, ancestor]), "add-dirs: " + string.Join(" | ", folders));
    Directory.CreateDirectory(Path.Combine(top, ".claude"));
    Expect(EngineLaunch.AddDirs(project, [game], scratch).SequenceEqual([game, top]), "a .claude ancestor covers the nearer one");
    string arguments = EngineLaunch.ClaudeArguments(files, folders);
    string[] argv = Argv("claude.exe " + arguments);
    Expect(argv.Skip(1).SequenceEqual(["--mcp-config", files.McpConfig, "--settings", files.Settings, "--append-system-prompt",
        EngineLaunch.SystemPrompt, "--add-dir", game, "--add-dir", ancestor]), "argv: " + string.Join(" | ", argv));
    Expect(!EngineLaunch.SystemPrompt.Contains('"') && !EngineLaunch.SystemPrompt.Contains('%'), "prompt is safe through cmd");
    string line = EngineLaunch.CmdLine(@"C:\Program Files\nodejs\claude.cmd", arguments);
    Expect(line.StartsWith("cmd.exe /d /s /k \"\"C:\\Program Files\\nodejs\\claude.cmd\" --mcp-config ", StringComparison.Ordinal)
        && line.EndsWith(ancestor + "\"\"", StringComparison.Ordinal), "cmd line: " + line);
    Expect(EngineLaunch.Quote(@"C:\a b\") == "\"C:\\a b\\\\\"" && EngineLaunch.Quote("plain") == "plain", "quoting");
    Expect(EngineLaunch.ClaudeArguments(null, []).StartsWith("--append-system-prompt", StringComparison.Ordinal), "no config files");
    // agy gets the same folders, quoted: both scratch paths hold spaces.
    string agy = EngineLaunch.AgyArguments(folders);
    Expect(agy == $"--add-dir \"{game}\" --add-dir \"{ancestor}\"", "agy arguments: " + agy);
    Expect(Argv("agy.exe " + agy).Skip(1).SequenceEqual(["--add-dir", game, "--add-dir", ancestor]), "agy argv");
    Expect(EngineLaunch.AgyArguments([]).Length == 0, "agy without folders");
}

void PiHarness()
{
    string desktop = Path.Combine(scratch, "pi desktop");
    string launcher = Path.Combine(desktop, "pi-cust", "pi-cust-harness-build", "build-1", "pi-cust.cmd");
    Directory.CreateDirectory(Path.GetDirectoryName(launcher)!);
    File.WriteAllText(launcher, "@echo off\r\n");
    string? prior = Environment.GetEnvironmentVariable("PI_CUST_HARNESS");
    string? priorPath = Environment.GetEnvironmentVariable("PI_CUST_HARNESS_PATH");
    try
    {
        Environment.SetEnvironmentVariable("PI_CUST_HARNESS", null);
        Environment.SetEnvironmentVariable("PI_CUST_HARNESS_PATH", null);
        Expect(EngineCommands.FindPiHarness(launcher, "", desktop) == launcher, "configured launcher");
        Expect(EngineCommands.FindPiHarness(Path.GetDirectoryName(launcher), "", Path.Combine(scratch, "none")) == launcher, "launcher folder");
        Expect(EngineCommands.FindPiHarness(null, "", desktop) == launcher, "default Desktop build");
        Expect(EngineCommands.FindPiHarness(null, "", Path.Combine(scratch, "none")) is null, "missing launcher");
    }
    finally
    {
        Environment.SetEnvironmentVariable("PI_CUST_HARNESS", prior);
        Environment.SetEnvironmentVariable("PI_CUST_HARNESS_PATH", priorPath);
    }
    string fresh = EngineLaunch.PiArguments(false), resumed = EngineLaunch.PiArguments(true);
    Expect(fresh.StartsWith("--append-system-prompt ", StringComparison.Ordinal) && !fresh.Contains("--continue", StringComparison.Ordinal)
        && resumed.EndsWith("--continue", StringComparison.Ordinal), "Pi launch arguments");
    Expect(Argv("pi-cust.cmd " + fresh).Skip(1).SequenceEqual(["--append-system-prompt", EngineLaunch.PiSystemPrompt]),
        "Pi system prompt argv");
    Expect(!EngineLaunch.PiSystemPrompt.Contains('"') && !EngineLaunch.PiSystemPrompt.Contains('%'), "Pi prompt is safe through cmd");
    Expect(EngineLaunch.SkipPermissionsArguments("pi-cust").Length == 0, "Pi has no skip-permissions flag");
    string folder = Path.Combine(scratch, "pi project");
    var servers = new[]
    {
        new SolMcpServers.Server("solforge", @"C:\Solforge\solforge-mcp.exe", ["--project", folder], new JsonObject(), true, "Solforge"),
        new("gemini", @"C:\Solforge\solforge-mcp.exe", ["--gemini"], new JsonObject(), true, "Solforge"),
        new("mine", "mine.exe", [], new JsonObject { ["TOKEN"] = "secret" }, false, ".mcp.json")
    };
    McpClientConfigs.WritePi(folder, servers);
    string piText = File.ReadAllText(Path.Combine(folder, ".pi", "mcp.json"));
    var pi = JsonNode.Parse(piText)!["mcpServers"]!.AsObject();
    var piArgs = pi["sol"]?["args"]?.AsArray().Select(node => node?.ToString()).ToArray() ?? [];
    Expect(pi.Count == 1 && pi["sol"]?["type"]?.ToString() == "stdio" && pi["sol"]?["exposure"]?.ToString() == "direct"
        && pi["sol"]?["command"]?.ToString() == @"C:\Solforge\solforge-mcp.exe"
        && piArgs.Contains("--sol") && piArgs.Contains("--project") && piArgs.Contains("--upstreams")
        && pi["gemini"] is null && pi["mine"] is null && !piText.Contains("secret", StringComparison.Ordinal), "Pi MCP config");
}

void Providers()
{
    var subscription = EngineProviders.Resolve("claude", null)!;
    Expect(subscription.Id == EngineProviders.ClaudeSubscription, "Claude subscription default");
    Expect(EngineProviders.Environment(subscription, null).Values.All(value => value.Length == 0), "subscription strips API keys");
    Expect(!EngineProviders.For("cline").Any(), "retired engine has no presets");
}

void DualEngines()
{
    string project = Path.Combine(scratch, "dual project"), shim = Path.Combine(scratch, "solforge-mcp.exe");
    Directory.CreateDirectory(project);
    var plain = EngineLaunch.WriteClaudeFiles(project, shim, Path.Combine(scratch, "dual-mcp-a"));
    Expect(JsonNode.Parse(File.ReadAllText(plain.McpConfig))!["mcpServers"]?["gemini"] is null
        && !EngineLaunch.ClaudeArguments(plain, []).Contains("ask_gemini"), "no gemini unless asked");
    Expect(Argv("claude.exe " + EngineLaunch.WithPrompt("claude", "--add-dir x", EngineLaunch.HandoffPrompt)).Skip(1)
        .SequenceEqual([EngineLaunch.HandoffPrompt, "--add-dir", "x"]), "claude first prompt goes first");
    Expect(Argv("agy.exe " + EngineLaunch.WithPrompt("agy", "--add-dir x", "go on")).Skip(1).SequenceEqual(["--add-dir", "x", "-i", "go on"])
        && EngineLaunch.WithPrompt("cline", "a", "go") == "a" && EngineLaunch.WithPrompt("claude", "a", null) == "a", "agy -i, others none");
    static string Line(string type, JsonNode content, string? flag = null)
    {
        var entry = new JsonObject { ["type"] = type, ["message"] = new JsonObject { ["content"] = content } };
        if (flag is not null) entry[flag] = true;
        return entry.ToJsonString();
    }
    static JsonArray Part(string type, string text) => new(new JsonObject { ["type"] = type, [type == "text" ? "text" : "content"] = text });
    string[] transcript =
    [
        Line("user", "make a hand cannon"), Line("user", "meta", "isMeta"), Line("user", "<command-name>/mcp</command-name>"),
        Line("user", Part("tool_result", "x")), Line("assistant", Part("text", "side"), "isSidechain"),
        Line("assistant", Part("text", "Cloned it.")), "broken"
    ];
    var (requests, reply) = EngineLaunch.ClaudeTranscript(transcript);
    Expect(requests.SequenceEqual(["make a hand cannon"]) && reply == "Cloned it.", "claude transcript: " + string.Join(" | ", requests) + " / " + reply);
    string[] history =
    [
        new JsonObject { ["display"] = "one", ["workspace"] = project, ["conversationId"] = "g1" }.ToJsonString(),
        new JsonObject { ["display"] = "/mcp", ["workspace"] = project, ["conversationId"] = "g1", ["type"] = "slash_command" }.ToJsonString(),
        new JsonObject { ["display"] = "other", ["workspace"] = project, ["conversationId"] = "g0" }.ToJsonString(),
        new JsonObject { ["display"] = "two", ["workspace"] = project + "\\", ["conversationId"] = "g1" }.ToJsonString()
    ];
    Expect(EngineLaunch.AgyRequests(project, "g1", history).SequenceEqual(["one", "two"]), "agy requests");
    string home = Path.Combine(scratch, "dual home"), sessions = EngineLaunch.ClaudeSessionsDir(project, home);
    Directory.CreateDirectory(sessions);
    File.WriteAllLines(Path.Combine(sessions, "abc.jsonl"), transcript);
    string note = File.ReadAllText(EngineLaunch.WriteHandoff(project, "claude", "Claude Code", "Antigravity", home));
    Expect(note.Contains("from Claude Code to Antigravity") && note.Contains("Claude Code session: abc") && note.Contains("- make a hand cannon")
        && note.Contains("Cloned it."), "handoff note: " + note);
    Expect(File.ReadAllText(EngineLaunch.WriteHandoff(project, "agy", "Antigravity", "Claude Code", home)).Contains("(none recorded)"),
        "handoff without a session");
}

void RetiredDelegates()
{
    string project = Path.Combine(scratch, "retired-project");
    Directory.CreateDirectory(project);
    var servers = SolMcpServers.Collect(project, "shim.exe", true, true, Path.Combine(scratch, "empty-home"));
    Expect(servers.All(server => !SolMcpServers.IsRetired(server)), "retired delegates removed");
    Expect(!SolMcpServers.For(SolMcpServers.Engine.Claude,
        [new("clinepass", "shim.exe", ["--clinepass"], new JsonObject(), true, "old")]).Any(), "saved delegates filtered");
}

void ResumeSessions()
{
    string home = Path.Combine(scratch, "resume home");
    string folder = @"C:\Users\Seth\Desktop\sol\09-solforge\publish", other = @"C:\Other";
    string dir = EngineLaunch.ClaudeSessionsDir(folder + "\\", home);
    Expect(Path.GetFileName(dir) == "C--Users-Seth-Desktop-sol-09-solforge-publish", "claude sessions dir: " + dir);
    Expect(EngineLaunch.ClaudeSession(folder, home) is null, "no claude session on a first run");
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "old.jsonl"), "{}");
    File.SetLastWriteTimeUtc(Path.Combine(dir, "old.jsonl"), DateTime.UtcNow.AddHours(-1));
    File.WriteAllText(Path.Combine(dir, "6f24feaa.jsonl"), "{}");
    Expect(EngineLaunch.ClaudeSession(folder, home) == "6f24feaa", "newest claude session");
    Expect(EngineLaunch.ResumeArguments("claude", "6f24feaa") == "--continue", "claude resumes with --continue");
    Expect(EngineLaunch.ResumeArguments("claude", null) == "" && EngineLaunch.ResumeArguments("cline", "") == "", "no session, no flag");
    Expect(EngineLaunch.JoinArguments("--add-dir x", "", "--continue") == "--add-dir x --continue"
        && EngineLaunch.JoinArguments("", "--continue") == "--continue", "join");
    string[] agyLines =
    [
        new JsonObject { ["display"] = "a", ["workspace"] = folder, ["conversationId"] = "first" }.ToJsonString(),
        new JsonObject { ["display"] = "/mcp", ["workspace"] = folder, ["type"] = "slash_command" }.ToJsonString(),
        "not json",
        new JsonObject { ["display"] = "b", ["workspace"] = folder.ToLowerInvariant() + "\\", ["conversationId"] = "second" }.ToJsonString(),
        new JsonObject { ["display"] = "c", ["workspace"] = other, ["conversationId"] = "elsewhere" }.ToJsonString()
    ];
    Expect(EngineLaunch.AgySession(folder, agyLines) == "second", "agy: last conversation for this workspace");
    Expect(EngineLaunch.AgySession(@"C:\Nowhere", agyLines) is null, "agy: none for another folder");
    Expect(EngineLaunch.AgySession(folder, home) is null, "agy: no history file");
    Expect(EngineLaunch.ResumeArguments("agy", "second") == "--conversation second", "agy resume flag");
    string cline = "loading\n" + new JsonArray(
        new JsonObject { ["sessionId"] = "old", ["cwd"] = folder, ["startedAt"] = "2026-10-14T10:00:00.000Z" },
        new JsonObject { ["sessionId"] = "sub", ["cwd"] = folder, ["startedAt"] = "2026-10-15T12:00:00.000Z", ["isSubagent"] = true },
        new JsonObject { ["sessionId"] = "new", ["cwd"] = "x", ["workspaceRoot"] = folder, ["startedAt"] = "2026-10-15T11:00:00.000Z" },
        new JsonObject { ["sessionId"] = "away", ["cwd"] = other, ["startedAt"] = "2026-10-15T13:00:00.000Z" }).ToJsonString();
    Expect(EngineLaunch.ClineSession(folder, cline) == "new", "cline: newest top-level session for this folder");
    Expect(EngineLaunch.ClineSession(@"C:\Nowhere", cline) is null && EngineLaunch.ClineSession(folder, "error: no history") is null, "cline: none");
    Expect(EngineLaunch.ResumeArguments("cline", "1792083166619_vt4lw") == "--id 1792083166619_vt4lw", "cline resume flag");
}

static void ContextText()
{
    var context = JsonNode.Parse(FixtureHost.Context)!;
    string text = ContextSummary.Format(context);
    Expect(text.Length is > 0 and <= ContextSummary.Limit, "length " + text.Length);
    foreach (string part in new[] { "Map: Botza District", "Hovered: Botza District / Static mesh 7489", "tag 0x80A1B2C3",
        "Values: Name=Last city crater", "script scripts/freeroam.lua", "Recent: select" })
        Expect(text.Contains(part, StringComparison.Ordinal), $"missing '{part}' in:\n{text}");
    Expect(ContextSummary.Format(JsonNode.Parse("{\"paused\":true}")) == "" && ContextSummary.Format(null) == "", "paused prints nothing");
    var big = context.DeepClone().AsObject();
    var pinned = new JsonArray();
    for (int i = 0; i < 400; i++) pinned.Add(new JsonObject { ["id"] = "w:" + i, ["label"] = new string('x', 400) });
    big["pinned"] = pinned;
    big["recent"] = null;
    Expect(ContextSummary.Format(big).Length <= ContextSummary.Limit + 3, "capped");
}

async Task ContextHook()
{
    string project = Path.Combine(scratch, "project");
    string discovery = Path.Combine(scratch, "hook discovery");
    var host = new FixtureHost(project);
    var dispatcher = new McpDispatcher(SolforgeTools.Create(host), "0.1.0", SolforgeTools.Instructions);
    await using var server = new McpHttpServer(dispatcher);
    server.Start();
    McpDiscovery.Write(new McpDiscoveryEntry(Environment.ProcessId, server.Url, server.Token, project, DateTimeOffset.UtcNow), discovery);
    async Task<(int Code, string Output, TimeSpan Time)> Run(string folder)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in new[] { FindShim(), "--context", "--project", project }) start.ArgumentList.Add(arg);
        start.Environment["SOLFORGE_MCP_DISCOVERY"] = folder;
        var clock = Stopwatch.StartNew();
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(20000)) { process.Kill(); Expect(false, "--context hung"); }
        return (process.ExitCode, await output, clock.Elapsed);
    }
    var live = await Run(discovery);
    Expect(live.Code == 0 && live.Output.Contains("Map: Botza District", StringComparison.Ordinal)
        && live.Output.Contains("Selected: Botza District / Static mesh 7489", StringComparison.Ordinal)
        && live.Output.Length <= ContextSummary.Limit + 3, "--context output: " + live.Output);
    host.Paused = true;
    var paused = await Run(discovery);
    Expect(paused.Code == 0 && paused.Output.Length == 0, "--context while paused: " + paused.Output);
    string empty = Path.Combine(scratch, "no solforge");
    Directory.CreateDirectory(empty);
    var none = await Run(empty);
    Expect(none.Code == 0 && none.Output.Length == 0 && none.Time < TimeSpan.FromSeconds(10), $"--context without Solforge: {none.Output} in {none.Time}");
}

static void SessionChecks(string scratch)
{
    string project = Path.Combine(scratch, "session project");
    Directory.CreateDirectory(project);
    var facts = new SessionFacts("Dark Forest", @"D:\Destiny 2", "The EDZ", "0x80001234", null, "scripts/edz.lua", true,
        ["solforge-map-bounds", "solforge-new-weapon"], ["get_editor_state", "list_maps"]);
    string session = SessionContext.Write(project, facts);
    string text = File.ReadAllText(session);
    Expect(text.Contains("Project: Dark Forest") && text.Contains("Open map: The EDZ (mission 0x80001234)") && text.Contains("- solforge-map-bounds")
        && text.Contains("get_editor_state, list_maps"), "SESSION.md: " + text);
    string agents = Path.Combine(project, "AGENTS.md");
    File.WriteAllText(agents, "# My rules\n\nKeep this.\n");
    SessionContext.Write(project, facts);
    SessionContext.Write(project, facts with { Skills = ["solforge-new-armor"] });
    string merged = File.ReadAllText(agents);
    Expect(merged.StartsWith("# My rules\n\nKeep this.", StringComparison.Ordinal) && merged.Split(SessionContext.Begin).Length == 2
        && merged.Contains("- solforge-new-armor") && !merged.Contains("- solforge-new-weapon"), "AGENTS.md block: " + merged);
    string bundled = Path.Combine(scratch, "bundled"), target = Path.Combine(scratch, "skills");
    Directory.CreateDirectory(Path.Combine(bundled, "solforge-new-weapon"));
    Directory.CreateDirectory(Path.Combine(bundled, "other-skill"));
    File.WriteAllText(Path.Combine(bundled, "solforge-new-weapon", "SKILL.md"), "---\nname: solforge-new-weapon\n---\n");
    File.WriteAllText(Path.Combine(bundled, "other-skill", "SKILL.md"), "x");
    Expect(SkillSync.Sync(bundled, target).SequenceEqual(["solforge-new-weapon"]), "first sync copies");
    Expect(SkillSync.Sync(bundled, target).Count == 0, "unchanged skills are not copied again");
    Expect(SkillSync.Names(target).SequenceEqual(["solforge-new-weapon"]) && !Directory.Exists(Path.Combine(target, "other-skill")), "only solforge-* skills");
}

static string[] Argv(string commandLine)
{
    IntPtr list = NativeArgs.CommandLineToArgvW(commandLine, out int count);
    try
    {
        var argv = new string[count];
        for (int i = 0; i < count; i++) argv[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(list, i * IntPtr.Size))!;
        return argv;
    }
    finally { NativeArgs.LocalFree(list); }
}

static string FindShim()
{
    for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
    {
        string bin = Path.Combine(folder.FullName, "src", "Solforge.Mcp.Shim", "bin");
        if (!Directory.Exists(bin)) continue;
        return Directory.EnumerateFiles(bin, "solforge-mcp.dll", SearchOption.AllDirectories)
            .OrderByDescending(path => path.Contains("Release", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(path => File.GetLastWriteTimeUtc(path)).FirstOrDefault()
            ?? throw new FileNotFoundException("Build src/Solforge.Mcp.Shim first.");
    }
    throw new DirectoryNotFoundException("Repository root not found above " + AppContext.BaseDirectory);
}

static void Approvals()
{
    string[] screen(params string[] bottom) => [.. Enumerable.Repeat("", 6), .. bottom];
    const string Nav = "   ↑/↓ Navigate · tab Amend", Esc = "  esc to cancel                                   accept-edits · Gemini 3.8 Flash · high";
    string[] agy(int pointed = 1, string tool = "solforge/get_viewport_screenshot")
    {
        string[] options =
        [
            "Yes, allow tool call", $"Yes, and always allow tool '{tool}' in this conversation", $"Yes, and always allow tool '{tool}' (Persist to settings.json)",
            "No, deny tool call", $"No, and always deny tool '{tool}' in this conversation", $"No, and always deny tool '{tool}' (Persist to settings.json)"
        ];
        return screen([
            "  " + tool, "  Allow calling this tool?",
            .. options.Select((label, i) => (i + 1 == pointed ? "> " : "  ") + $"{i + 1}. {label}"),
            Nav, Esc]);
    }
    var first = ApprovalPrompts.Find("agy", agy());
    Expect(first is { Choice: "Yes, allow tool call", Question: "Allow calling this tool?" }, "agy tool call");
    Expect(ApprovalPrompts.Find("agy", agy())?.Signature == first!.Signature, "agy: same prompt, same signature");
    Expect(ApprovalPrompts.Find("agy", agy(tool: "solforge/find_object"))?.Signature is { } next && next != first.Signature, "agy: next prompt differs");
    Expect(ApprovalPrompts.Find("agy", agy(4)) is null, "agy: No highlighted");
    Expect(ApprovalPrompts.Find("agy", agy(2)) is null, "agy: always-allow highlighted");
    Expect(ApprovalPrompts.Find("claude", agy()) is null && ApprovalPrompts.Find("custom", agy()) is null, "other engines' layouts");
    Expect(ApprovalPrompts.Find("agy", screen("Run this command?", "> 1. Yes, run command", "  2. Yes, and always allow in this conversation for commands",
        "     that start with 'dotnet'", "  3. Yes, and always allow for commands that start with 'dotnet' (Persist to settings.json)", "  4. No, cancel", Nav, Esc))?.Choice
        == "Yes, run command", "agy command, wrapped option");
    // Seth's screen: the terminal hard-wraps option 2 back to column 0, and the footer carries ctrl+g.
    Expect(ApprovalPrompts.Find("agy", screen("Requesting permission for:", "   dotnet run", "", "Run this command?", "> 1. Yes, run command",
        "  2. Yes, and always allow in this conversation for commands that start with 'dotnet", "run'",
        "  3. Yes, and always allow for commands that start with 'dotnet run' (Persist to", "settings.json)", "  4. No, cancel", "",
        "  ↑/↓ Navigate · tab Amend · ctrl+g edit/expand command", "esc to cancel                         accept-edits · Gemini 3.8 Flash · high"))?.Choice
        == "Yes, run command", "agy command, hard-wrapped options and ctrl+g footer");
    foreach (var (asked, allow, decline) in new[]
    {
        ("Allow access to this URL?", "Yes, allow access", "No, deny access"), ("Allow access to this file?", "Yes, allow access", "No, deny access"),
        ("Allow creation of this file?", "Yes, allow creation", "No, deny creation"), ("Grant requested permission?", "Yes, grant permission", "No, deny permission"),
        ("Do you want to proceed?", "Yes, proceed", "No, cancel")
    })
        Expect(ApprovalPrompts.Find("agy", screen("Requesting permission for:", "  target", asked, "> 1. " + allow, "  2. " + decline, Nav, Esc)) is { } found
            && found.Question == asked && found.Choice == allow, "agy " + asked);
    Expect(ApprovalPrompts.Find("agy", screen("Accept this file edit?", "> 1. Yes, accept this change", "  2. No, reject this change",
        "Esc disabled during file edits — press 1 to accept or 2 to reject.")) is { Choice: "Yes, accept this change" }, "agy file edit");
    Expect(ApprovalPrompts.Find("agy", screen("Allow administrator elevation?", "> 1. Yes, elevate", "  2. No, deny", Esc)) is null, "agy elevation stays manual");
    Expect(ApprovalPrompts.Find("agy", screen("Run this command?", "  dotnet test", " 🔓 Allow sandbox bypass for command execution?",
        "⚠️ Confirm the command is safe to run outside of the sandbox with full network and disk access.", "> 1. Yes, run command", "  2. No, cancel", Nav, Esc)) is null,
        "agy sandbox bypass under a stale question");
    Expect(ApprovalPrompts.Find("agy", screen("Do you trust the contents of this project?", "> 1. Yes, I trust this folder", "  2. No, exit", "enter Confirm · esc to cancel")) is null, "agy trust");
    Expect(ApprovalPrompts.Find("agy", screen("Question 1/1:", "Do you want to proceed?", "> 1. Yes, proceed", "  2. No, cancel", "  3. Write-in", Nav)) is null, "agy model question");
    Expect(ApprovalPrompts.Find("agy", screen("Allow calling this tool?", "> 1. Yes, allow tool call", "  2. No, deny tool call", Esc, "", "> ")) is null, "agy: options not at the bottom");
    Expect(ApprovalPrompts.Find("agy", screen("Allow calling this tool?", Nav, Esc)) is null, "agy: question without options");
    Expect(ApprovalPrompts.Find("agy", [.. agy(), "", "Done, the screenshot is attached.", "> "]) is null, "agy: prompt in scrollback, output below");
    Expect(ApprovalPrompts.Find("agy", screen("Allow calling this tool?", "> 1. Yes, allow tool call", "  3. No, deny tool call", Nav, Esc)) is null, "agy: numbers out of order");

    string[] claude(string pointed = "❯ 1. Yes", string question = "Do you want to proceed?", string command = "dotnet build Solforge.sln",
        string footer = "   Esc to cancel · Tab to amend") => screen(
        "╭──────────────────────────────────────────╮",
        "│ Bash command                              │",
        "│   " + command,
        "│ " + question,
        "│ " + pointed,
        "│   2. Yes, and don’t ask again for dotnet commands in C:/work",
        "│   3. No",
        "╰──────────────────────────────────────────╯",
        footer);
    var bash = ApprovalPrompts.Find("claude", claude());
    Expect(bash is { Choice: "Yes" }, "claude bash");
    Expect(ApprovalPrompts.Find("claude", claude())?.Signature == bash!.Signature, "claude: same prompt, same signature");
    Expect(ApprovalPrompts.Find("claude", claude(command: "git status"))?.Signature is { } status
        && ApprovalPrompts.Find("claude", claude(command: "git diff"))?.Signature is { } diff && status != diff, "claude: next command differs");
    Expect(ApprovalPrompts.Find("claude", claude("> 1. Yes")) is { Choice: "Yes" }, "claude ASCII pointer");
    Expect(ApprovalPrompts.Find("claude", claude(footer: "Esc to cancel · Tab to amend · ctrl+e to explain")) is { Choice: "Yes" }, "claude explain footer");
    foreach (string asked in new[] { "make this edit to", "create", "overwrite", "write to" })
        Expect(ApprovalPrompts.Find("claude", claude(question: $"Do you want to {asked} Foo.cs?")) is { Choice: "Yes" }, "claude " + asked);
    Expect(ApprovalPrompts.Find("claude", claude(question: "Do you want to allow Claude to fetch this content?")) is { Choice: "Yes" }, "claude fetch");
    Expect(ApprovalPrompts.Find("claude", screen("Do you want to make this edit to", "a/long/path/Foo.cs?", "❯ 1. Yes", "  2. No", "Esc to cancel"))
        is { Question: "Do you want to make this edit to a/long/path/Foo.cs?" }, "claude wrapped question");
    Expect(ApprovalPrompts.Find("claude", screen("Allow Claude in Chrome to click on example.com?", "❯ 1. Allow", "  2. Deny (esc)", "Esc to cancel")) is { Choice: "Allow" }, "claude chrome");
    Expect(ApprovalPrompts.Find("claude", screen("Do you want to proceed?", "  1. Yes", "  2. Yes, and don’t ask again for: git", "❯ 3. No", "Esc to cancel")) is null, "claude: No highlighted");
    Expect(ApprovalPrompts.Find("claude", screen("Do you want to proceed?", "❯ No", "  Yes", "Esc to cancel")) is null, "claude default-to-no");
    Expect(ApprovalPrompts.Find("claude", screen("Do you want to proceed?", "❯ 1. Yes", "  2. Maybe", "Esc to cancel")) is null, "claude: no decline sibling");
    Expect(ApprovalPrompts.Find("claude", screen("Do you want to proceed?", "❯ 1. Yes", "❯ 2. No", "Esc to cancel")) is null, "claude: two pointers");
    Expect(ApprovalPrompts.Find("claude", screen("Allow this read outside the working directories?", "❯ 1. Yes, and keep allowing any reads outside the working directories",
        "  2. No, and ask again next time", "Esc to cancel")) is null, "claude persistent first row");
    Expect(ApprovalPrompts.Find("claude", screen("Network request outside of sandbox", "Do you want to allow this connection?", "❯ 1. Yes", "  2. No", "Esc to cancel")) is null,
        "claude sandbox network stays manual");
    Expect(ApprovalPrompts.Find("claude", screen("Quick safety check: Is this a project you created or one you trust?", "❯ No, exit", "  Yes, I trust this folder",
        "Enter to confirm · Esc to cancel")) is null, "claude trust");
    Expect(ApprovalPrompts.Find("claude", screen("Do you want to create a new branch?", "❯ 1. Yes", "  2. No", "Enter to select · ↑/↓ to navigate · Esc to cancel")) is null,
        "claude model question, navigate footer");
    Expect(ApprovalPrompts.Find("claude", screen("Do you want to create a new branch?", "❯ 1. Yes", "  2. No", "Esc to cancel")) is null, "claude model question, not a file");
    Expect(ApprovalPrompts.Find("claude", screen("Do you want to proceed?", "❯ 1. Yes", "  2. No", "  3. Type something.", "  4. Chat about this", "Esc to cancel")) is null,
        "claude AskUserQuestion rows");
    Expect(ApprovalPrompts.Find("claude", screen("Do you want to proceed?", "Esc to cancel")) is null, "claude: question without options");
    Expect(ApprovalPrompts.Find("claude", screen("Do you want to proceed?", "Bash ran: dotnet test", "❯ 1. Yes", "  2. No", "Esc to cancel")) is null, "claude: text between question and options");
    Expect(ApprovalPrompts.Find("claude", screen("● The dialog asks: Do you want to proceed?", "  ❯ 1. Yes", "  2. No", "Esc to cancel then pick one")) is null, "claude: quoted question");
    Expect(ApprovalPrompts.Find("claude", screen("Do you want to proceed?", "❯ 1. Yes", "  2. No", "Esc to cancel", "",
        "● Done. The build passed.", "", "> Yes", "  ? for shortcuts")) is null, "claude: question in scrollback, output below");
    Expect(ApprovalPrompts.Find("claude", screen("● Should I go on?", "", "> Yes", "  ? for shortcuts")) is null, "claude: free-text input line");
    Expect(ApprovalPrompts.Find("claude", screen("● Should I go on?", "> Yes", "Esc to cancel")) is null, "claude: typed Yes above a footer");
    Expect(ApprovalPrompts.Find("claude", screen("Do you want to proceed?", "  1. Yes", "  2. No", "> Yes", "Esc to cancel")) is null, "claude: typed Yes under options");

    const uint Selected = 0x79b8ff;
    string[] cline(params string[] panel) => [.. screen([.. panel, "", " Auto-approve all disabled (Shift+Tab)"]).Select(line => line.PadRight(80))];
    uint[][] shades(string[] rows, uint fill, params string[] marked)
    {
        var cells = rows.Select(line => Enumerable.Repeat(fill, line.Length).ToArray()).ToArray();
        int row = Array.FindIndex(rows, line => line.Contains("[y] Approve"));
        foreach (string button in marked)
        {
            int start = rows[row].IndexOf(button, StringComparison.Ordinal) - 1;
            for (int i = start; i < start + (button == "[y]" ? 13 : 10); i++) cells[row][i] = Selected;
        }
        return cells;
    }
    const string Buttons = " [y] Approve   [n] Deny ";
    string[] asks = cline(" Cline needs permission", " Approve tool call?", " run_commands", "   $ dotnet test", Buttons);
    string[] after = cline(" Cline needs permission", " run_commands", "   $ dotnet test", " Approve tool call?", Buttons);
    string[] json = cline(" Cline needs permission", " Approve tool call?", " write_to_file", "   {", "     \"path\": \"scripts/a.lua\",",
        "     \"content\": \"-- new\"", "   }", Buttons);
    Expect(ApprovalPrompts.Find("cline", asks, shades(asks, 0, "[y]")) is { Choice: "Approve" }, "cline approve highlighted");
    Expect(ApprovalPrompts.Find("cline", after, shades(after, 0, "[y]")) is { Choice: "Approve" }, "cline question under the detail");
    Expect(ApprovalPrompts.Find("cline", json, shades(json, 0, "[y]")) is { Choice: "Approve" }, "cline multi-line detail");
    Expect(ApprovalPrompts.Find("cline", asks, shades(asks, 0x202020, "[y]")) is { Choice: "Approve" }, "cline filled panel");
    Expect(ApprovalPrompts.Find("cline", asks, shades(asks, 0, "[n]")) is null, "cline: Deny highlighted");
    Expect(ApprovalPrompts.Find("cline", asks, shades(asks, 0, "[y]", "[n]")) is null, "cline: both highlighted");
    Expect(ApprovalPrompts.Find("cline", asks) is null, "cline needs colours");
    string[] scrolled = [.. asks, .. Enumerable.Range(0, 6).Select(i => $" output line {i}".PadRight(80))];
    Expect(ApprovalPrompts.Find("cline", scrolled, shades(scrolled, 0, "[y]")) is null, "cline: panel in scrollback");
    string[] bare = cline(" Cline needs permission", " Approve tool call?", " run_commands");
    Expect(ApprovalPrompts.Find("cline", bare, bare.Select(line => new uint[line.Length]).ToArray()) is null, "cline: question without buttons");
    string[] followUp = cline(" Cline is asking a question", " Run the tests?", "> Yes", "  No", "  Type a response...");
    Expect(ApprovalPrompts.Find("cline", followUp, followUp.Select(line => Enumerable.Repeat(Selected, line.Length).ToArray()).ToArray()) is null, "cline follow-up question");
}

void MissionObjects()
{
    Expect(MissionSlots.SlotId(0x80fd58b9, 4, 20) == "slot/80fd58b9/000014/0014/0004", "slot id format");
    Expect(MissionSlots.TryParseSlotId("slot/80be0b1a/000006/0006/0047", out uint registry, out int type, out int index)
        && registry == 0x80BE0B1A && type == 71 && index == 6, "slot id parse");
    Expect(MissionSlots.ParentName("pf_event_multipoint.pf_placement[0].o_glimmer[0]") == "pf_event_multipoint.pf_placement[0]", "parent name");
    Expect(MissionSlots.ParentName("a.b[x.y]") == "a" && MissionSlots.ParentName("top") is null, "brackets do not split");
    Expect(MissionSlots.ConstantName("pf_event_multipoint.pf_placement[0].o_capture_ring") == "PF_EVENT_MULTIPOINT_PF_PLACEMENT_0_O_CAPTURE_RING", "constant");
    var mission = MissionFixtures.Snapshot();
    var generator = mission.Find("slot/80BE5D9B/000014/0014/0004");
    Expect(generator?.Name == "pf_event_multipoint.pf_placement[0].o_generator", "find by slot id (any case)");
    Expect(mission.Find("marker:gen") == generator && mission.Find("pf_event_multipoint.pf_placement[0].o_generator") == generator, "find by object id and name");
    Expect(mission.Find("PF_EVENT_MULTIPOINT_PF_PLACEMENT_0_O_GENERATOR_80BE5D9B") == generator, "find by server constant");
    Expect(mission.Find("PF_EVENT_MULTIPOINT_PF_PLACEMENT_0_O_GENERATOR_80BE0B1A") is null, "wrong registry suffix");
    var glimmer = mission.Find("pf_event_multipoint.pf_placement[0].o_glimmer[0]")!;
    Expect(mission.Children(glimmer) is [{ Name: "pf_event_multipoint.pf_placement[0].o_glimmer[0].fx" }], "direct children");
    Expect(mission.Parent(mission.Find("pf_event_multipoint.pf_placement[0].o_glimmer[0].fx")!) == glimmer, "parent slot");
    var details = MissionSlots.Describe(mission, generator!, 0, 10);
    Expect(details["parent"]?["note"] is not null && details["parent"]?["name"]?.ToString() == "pf_event_multipoint.pf_placement[0]", "parent without a slot");
    Expect(details["prefabObject"]?["id"]?.ToString() == "object:80BE5DA0" && details["zone"]?.ToString() == "Trostland", "prefab and zone");
    var all = mission.Entries(new HashSet<string> { "marker:gen" }).ToArray();
    Expect(all.Single(entry => entry.Id == generator!.SlotId).Scope == ObjectScope.Viewport, "placed slot on screen ranks as viewport");
    string Top(string query) => ObjectIndex.Find(all, query, "EDZ") is [var first, ..] ? first.Entry.Id : "(none)";
    Expect(Top("o_generator") == generator!.SlotId, "spoken slot name -> " + Top("o_generator"));
    Expect(Top("that generator") == generator!.SlotId, "that generator -> " + Top("that generator"));
    Expect(Top("slot/80be5d9b/000015/0015/0004") == glimmer.SlotId, "exact slot id");
    Expect(Top("0x80BE0B1A") == mission.Find("m_public_event_sensor")!.SlotId, "registry hash");
    Expect(Top("placement 1 generator") == mission.Find("pf_event_multipoint.pf_placement[1].o_generator")!.SlotId, "placement number");
    var page = MissionSlots.List(mission, "generator", false, 0, 1);
    Expect(page["total"]?.GetValue<int>() == 2 && page["count"]?.GetValue<int>() == 1 && page["next"]?.GetValue<int>() == 1, "list paging");
    Expect(MissionSlots.List(mission, null, true, 0, 50)["total"]?.GetValue<int>() == 1, "placed only");
}

static void MissionScale()
{
    var clock = Stopwatch.StartNew();
    var slots = Enumerable.Range(0, 120_000).Select(i => new MissionSlot(MissionSlots.SlotId(0x80B00000u + (uint)(i / 200), 4, i % 200),
        $"pf_prefab_{i / 200}.pf_placement[{i % 10}].o_thing_{i}", 0x80B00000u + (uint)(i / 200), 4, i % 200, 0x80C00000u + (uint)(i / 200))
        { ObjectId = i % 3 == 0 ? $"marker:{i}" : null, Position = i % 3 == 0 ? [i, 0, 0] : null });
    var mission = new MissionSnapshot("EDZ", 0x80B2F00A, "edz_freeroam", slots);
    var entries = mission.Entries().ToArray();
    var found = ObjectIndex.Find(entries, "o_thing_119999", "EDZ");
    var page = MissionSlots.List(mission, "o_thing_5", true, 100, 50);
    var child = MissionSlots.Describe(mission, mission.Slots[0], 0, 10);
    Expect(found is [{ Entry.Label: "pf_prefab_599.pf_placement[9].o_thing_119999" }, ..], "scale find");
    Expect(page["count"]?.GetValue<int>() == 50 && page["rows"]?.AsArray().Count == 50, "scale page");
    Expect(child["children"]?["total"] is not null, "scale describe");
    Expect(clock.Elapsed < TimeSpan.FromSeconds(20), "120k slots took " + clock.Elapsed);
}

void CodeRefsCheck()
{
    var roots = MissionFixtures.CodeRoots(Path.Combine(scratch, "code"));
    var generator = MissionFixtures.Snapshot().Find("marker:gen")!;
    var needles = CodeRefs.Needles(generator);
    Expect(CodeRefs.Match("x = mission.Slot.PF_EVENT_MULTIPOINT_PF_PLACEMENT_0_O_GENERATOR_80BE5D9B,", needles) == "constant with registry", "server constant");
    Expect(CodeRefs.Match("PF_EVENT_MULTIPOINT_PF_PLACEMENT_0_O_GENERATOR_X = 1", needles) is null, "identifier boundary");
    Expect(CodeRefs.Match("local id = \"SLOT/80BE5D9B/000014/0014/0004\"", needles) == "slot id", "slot id any case");
    var narrow = CodeRefs.Search(roots, needles, 0x80B2F00A, false, 50, TimeSpan.FromSeconds(10), CancellationToken.None);
    Expect(narrow["count"]?.GetValue<int>() == 3, "this mission's SDK file and the server: " + narrow.ToJsonString());
    var hits = narrow["hits"]!.AsArray();
    Expect(hits.Any(hit => hit!["root"]?.ToString() == "server-scripts" && hit["line"]?.GetValue<int>() == 2)
        && hits.All(hit => hit?["path"]?.ToString() != "other_80000001.lua"), "server line and no other mission");
    var wide = CodeRefs.Search(roots, needles, 0x80B2F00A, true, 50, TimeSpan.FromSeconds(10), CancellationToken.None);
    Expect(wide["count"]?.GetValue<int>() == 4, "everywhere reads other missions");
    var capped = CodeRefs.Search(roots, needles, 0x80B2F00A, true, 1, TimeSpan.FromSeconds(10), CancellationToken.None);
    Expect(capped["count"]?.GetValue<int>() == 1 && capped["truncated"]?.GetValue<bool>() == true, "limit");
    string sol = Path.Combine(scratch, "layout", "sol");
    string game = Path.Combine(sol, "01-clients", "sol-client"), project = Path.Combine(sol, "09-solforge", "project");
    Directory.CreateDirectory(Path.Combine(game, "bin", "x64", "Sunrise", "sdk", "lua", "missions"));
    Directory.CreateDirectory(Path.Combine(sol, "02-server", "solserver", "solserverbase", "Destiny-Sol-main", "mission-scripts"));
    Directory.CreateDirectory(project);
    var found = CodeRefs.Locate(game, project, []);
    Expect(found.Select(root => root.Name).SequenceEqual(["sdk-missions", "server-scripts", "project"]) && found[0].ByMission
        && found[1].Folder.EndsWith("mission-scripts"), "locate: " + string.Join(", ", found.Select(root => root.Name + "=" + root.Folder)));
    string canonical = Path.Combine(sol, "02-server", "solserver", "solserverbase", "Destiny-Sol", "02-server", "mission-scripts");
    Directory.CreateDirectory(canonical);
    Expect(CodeRefs.Locate(game, project, []).Single(root => root.Name == "server-scripts").Folder == canonical,
        "current Destiny-Sol checkout wins over the legacy script copy");
    // Settings > Paths (set_settings serverScripts) wins over the guessed folder; scripts:<path> reads inside it only.
    string chosen = Path.Combine(scratch, "layout", "chosen-scripts");
    Directory.CreateDirectory(chosen);
    File.WriteAllText(Path.Combine(chosen, "edz.lua"), "-- edz\n");
    var picked = CodeRefs.Locate(game, project, [], chosen);
    Expect(picked.Any(root => root.Name == "server-scripts" && root.Folder == Path.GetFullPath(chosen)), "locate prefers the settings folder");
    Expect(ProjectFiles.Resolve(project, [], "scripts:edz.lua", chosen) == Path.Combine(chosen, "edz.lua")
        && ProjectFiles.Resolve(project, [], "scripts:../x.lua", chosen) is null && ProjectFiles.Resolve(project, [], "scripts:edz.lua") is null,
        "scripts: prefix");
    Expect(ProjectFiles.EditablePath(project, "scripts:edz.lua", out _) is null, "scripts: is read-only");
    Expect(SolforgeTools.SettingsProblem(new JsonObject { ["serverScripts"] = chosen, ["fpsFocused"] = 60, ["uiScale"] = 1.2 }) is null,
        "valid settings");
    Expect(SolforgeTools.SettingsProblem(new JsonObject { ["serverScripts"] = Path.Combine(chosen, "missing") }) is { } missing
        && missing.Contains("No folder"), "missing folder refused");
    Expect(SolforgeTools.SettingsProblem(new JsonObject { ["fpsFocused"] = 999 }) is not null, "range refused");
    Expect(SolforgeTools.SettingsProblem(new JsonObject { ["aiAutoAllow"] = true }) is { } ai && ai.Contains("Settings > AI"), "AI settings stay the user's");
    Expect(SolforgeTools.SettingsProblem(new JsonObject { ["lowMemoryMode"] = "yes" }) is not null, "type refused");
}

void ReferenceCheck()
{
    string folder = MissionFixtures.Reference(Path.Combine(scratch, "refs", "sol", "08-docs", "reference"));
    string project = Path.Combine(scratch, "refs", "sol", "09-solforge", "project");
    Directory.CreateDirectory(project);
    Expect(ReferenceLibrary.Locate([null, project]) == Path.GetFullPath(folder), "locate the library above the project");
    var pages = ReferenceLibrary.Load(folder);
    Expect(pages.Count == 2 && pages.Any(page => page.Path == "lua-coroutines.md" && page.Url == "https://www.lua.org/pil/9.html" && page.Tags.Contains("lua")),
        "front matter of an unindexed page: " + string.Join("; ", pages));
    var search = ReferenceLibrary.Search(folder, "replication", null, 5);
    Expect(search["hits"]?[0]?["path"]?.ToString() == "unreal/replication.md" && search["hits"]?[0]?["url"]?.ToString() == "https://docs.unrealengine.com/replication"
        && search["hits"]?[0]?["snippet"]?.ToString()?.Contains("Replication") == true, "search: " + search.ToJsonString());
    Expect(ReferenceLibrary.Search(folder, "coroutines yield", "lua", 5)["hits"]?[0]?["path"]?.ToString() == "lua-coroutines.md", "tag search");
    Expect(ReferenceLibrary.Search(folder, "coroutines", "unreal", 5)["count"]?.GetValue<int>() == 0, "tag filter excludes");
    var read = ReferenceLibrary.Read(folder, null, "http://lua.org/pil/9.html/", 1, 2);
    Expect(read is not null && read["path"]?.ToString() == "lua-coroutines.md" && read["next"]?.GetValue<int>() == 3, "read by url with paging");
    Expect(ReferenceLibrary.Read(folder, "../../secret.md", null, 1, 10) is null, "path escape refused");
    File.WriteAllText(Path.Combine(folder, "index.json"), "[{\"path\": \"unreal/repl");
    Expect(ReferenceLibrary.Load(folder).Count == 2, "half-written index.json");
}

static async Task JobChecks()
{
    int runs = 0;
    var gate = new TaskCompletionSource();
    var slow = new McpTool("slow_tool", "waits for the gate", McpJson.Schema(("x", "integer", "anything", false)), false, async (args, cancel) =>
    {
        Interlocked.Increment(ref runs);
        McpJobs.Report("halfway");
        await gate.Task.WaitAsync(cancel);
        return McpToolResult.Text("done " + McpJson.Int(args, "x", 0));
    });
    var forever = new McpTool("forever_tool", "never ends unless cancelled", McpJson.Schema(), false, async (_, cancel) =>
    {
        await Task.Delay(Timeout.Infinite, cancel);
        return McpToolResult.Text("unreachable");
    });
    var jobs = new McpJobs { Deadline = TimeSpan.FromMilliseconds(300) };
    var dispatcher = new McpDispatcher([slow, forever], "0.1.0", "checks", jobs);
    Expect(dispatcher.Tools.Select(tool => tool.Name).SequenceEqual(["slow_tool", "forever_tool", McpJobs.ToolName]), "mcp_job is listed last");
    async Task<JsonNode> Call(string name, JsonObject arguments)
    {
        string? reply = await dispatcher.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = name, ["arguments"] = arguments } }.ToJsonString(), CancellationToken.None);
        return JsonNode.Parse(reply!)!["result"]!;
    }
    static string Text(JsonNode result) => result["content"]![0]!["text"]!.ToString();
    var clock = Stopwatch.StartNew();
    var first = JsonNode.Parse(Text(await Call("slow_tool", new JsonObject { ["x"] = 7 })))!;
    Expect(clock.Elapsed < TimeSpan.FromSeconds(10) && first["pending"]?.GetValue<bool>() == true && first["job"]?.ToString() is { Length: > 0 }
        && first["progress"]?.ToString() == "halfway", "pending reply: " + first.ToJsonString());
    string job = first["job"]!.ToString();
    var again = JsonNode.Parse(Text(await Call("slow_tool", new JsonObject { ["x"] = 7 })))!;
    Expect(again["job"]?.ToString() == job && again["attached"]?.GetValue<bool>() == true && runs == 1, "a repeat attaches: " + again.ToJsonString());
    var listed = JsonNode.Parse(Text(await Call(McpJobs.ToolName, new JsonObject())))!;
    Expect(listed["jobs"]?.AsArray().Any(row => row?["job"]?.ToString() == job && row["state"]?.ToString() == "running") == true, "jobs listed: " + listed.ToJsonString());
    gate.SetResult();
    var done = await Call(McpJobs.ToolName, new JsonObject { ["job"] = job, ["wait"] = 5 });
    Expect(Text(done) == "done 7" && done["isError"]?.GetValue<bool>() == false, "mcp_job returns the tool's result: " + done.ToJsonString());
    var fresh = await Call("slow_tool", new JsonObject { ["x"] = 7 });
    Expect(Text(fresh) == "done 7" && runs == 2, "after collection a repeat runs again");
    var quick = await Call("slow_tool", new JsonObject { ["x"] = 8 });
    Expect(Text(quick) == "done 8", "a call inside the deadline answers directly");
    var stuck = JsonNode.Parse(Text(await Call("forever_tool", new JsonObject())))!;
    Expect(stuck["pending"]?.GetValue<bool>() == true, "forever_tool goes pending");
    await Call(McpJobs.ToolName, new JsonObject { ["job"] = stuck["job"]!.ToString(), ["cancel"] = true });
    var cancelled = await Call(McpJobs.ToolName, new JsonObject { ["job"] = stuck["job"]!.ToString(), ["wait"] = 5 });
    Expect(cancelled["isError"]?.GetValue<bool>() == true && Text(cancelled).Contains("cancelled", StringComparison.Ordinal), "cancel stops the job: " + cancelled.ToJsonString());
    Expect(jobs.DeadlineFor(new JsonObject { ["timeout"] = 30 }) == TimeSpan.FromSeconds(45) && jobs.DeadlineFor(new JsonObject()) == jobs.Deadline
        && jobs.DeadlineFor(new JsonObject { ["timeout"] = 9999 }) == jobs.MaximumDeadline, "a tool's own timeout extends the deadline, capped");
}

/** A small EDZ-like mission and the Lua folders that name its slots. */
static class MissionFixtures
{
    /** Builds a small EDZ-like mission snapshot. */
    public static MissionSnapshot Snapshot(string map = "EDZ") => new(map, 0x80B2F00A, "edz_freeroam",
    [
        new MissionSlot(MissionSlots.SlotId(0x80BE5D9B, 4, 20), "pf_event_multipoint.pf_placement[0].o_generator", 0x80BE5D9B, 4, 20, 0x80BE5DA0)
        {
            ObjectId = "marker:gen", Label = "Generator", Kind = "Marker", Zone = "Trostland", Position = [1, 2, 3], Rotation = [0, 0, 0, 1],
            PrefabObjectId = "object:80BE5DA0", PrefabName = "pf_event_multipoint", Placements = ["marker:gen"]
        },
        new MissionSlot(MissionSlots.SlotId(0x80BE5D9B, 4, 21), "pf_event_multipoint.pf_placement[0].o_glimmer[0]", 0x80BE5D9B, 4, 21, 0x80BE5DA0),
        new MissionSlot(MissionSlots.SlotId(0x80BE5D9B, 4, 22), "pf_event_multipoint.pf_placement[0].o_glimmer[0].fx", 0x80BE5D9B, 4, 22, 0x80BE5DA0),
        new MissionSlot(MissionSlots.SlotId(0x80BE5D9B, 4, 23), "pf_event_multipoint.pf_placement[1].o_generator", 0x80BE5D9B, 4, 23, 0x80BE5DA0),
        new MissionSlot(MissionSlots.SlotId(0x80BE0B1A, 71, 6), "m_public_event_sensor", 0x80BE0B1A, 71, 6, 0x80BE0B20),
    ]);

    /** Returns the SDK and server Lua roots for the fixture folder. */
    public static IReadOnlyList<CodeRoot> CodeRoots(string folder)
    {
        string sdk = Path.Combine(folder, "missions"), server = Path.Combine(folder, "mission-scripts", "edz_freeroam");
        Directory.CreateDirectory(sdk);
        Directory.CreateDirectory(server);
        File.WriteAllLines(Path.Combine(sdk, "edz_freeroam_80b2f00a.lua"),
        [
            "local slots = {",
            "    PF_EVENT_MULTIPOINT_PF_PLACEMENT_0_O_GENERATOR = { id = \"slot/80be5d9b/000014/0014/0004\", name = \"pf_event_multipoint.pf_placement[0].o_generator\" },",
            "    PF_EVENT_MULTIPOINT_PF_PLACEMENT_0_O_GENERATOR_LIGHT = { id = \"slot/80be5d9b/000030/0030/0004\" },",
            "    objective = slots.PF_EVENT_MULTIPOINT_PF_PLACEMENT_0_O_GENERATOR,",
            "}"
        ]);
        File.WriteAllLines(Path.Combine(sdk, "other_80000001.lua"), ["x = \"slot/80be5d9b/000014/0014/0004\""]);
        File.WriteAllLines(Path.Combine(server, "content.lua"),
            ["return {", "    objective = mission.Slot.PF_EVENT_MULTIPOINT_PF_PLACEMENT_0_O_GENERATOR_80BE5D9B,", "}"]);
        return [new CodeRoot("sdk-missions", sdk, true), new CodeRoot("server-scripts", Path.Combine(folder, "mission-scripts"))];
    }

    /** Writes a small reference library under the folder and returns its path. */
    public static string Reference(string folder)
    {
        Directory.CreateDirectory(Path.Combine(folder, "unreal"));
        File.WriteAllText(Path.Combine(folder, "unreal", "replication.md"),
            "---\nurl: https://docs.unrealengine.com/replication\ntitle: Actor Replication\ntags: [unreal, networking]\n---\n# Actor Replication\n\nReplication sends actor state from server to clients.\n");
        File.WriteAllText(Path.Combine(folder, "lua-coroutines.md"),
            "---\nurl: https://www.lua.org/pil/9.html\ntitle: Coroutines\ntags: [lua]\n---\n# Coroutines\n\nA coroutine can yield.\nResume it later.\n");
        File.WriteAllText(Path.Combine(folder, "index.json"),
            "[{\"title\": \"Actor Replication\", \"url\": \"https://docs.unrealengine.com/replication\", \"tags\": [\"unreal\", \"networking\"], " +
            "\"summary\": \"How replication works\", \"path\": \"unreal/replication.md\"}, {\"title\": \"Not saved yet\", \"path\": \"missing.md\"}]");
        return folder;
    }
}

static class NativeArgs
{
    /** Windows API that splits a command line into arguments; free the result with LocalFree. */
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    /** Windows API that frees the argument block from CommandLineToArgvW. */
    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr memory);
}

/** A fixed scene: the find_object fixture, no viewport, and an approver that always declines. */
sealed class FixtureHost(string project) : ISolforgeHost
{
    public const string Context = """
        {"map":"Botza District","mission":"0x80001234","camera":{"position":[1,2,3],"zone":"crater"},
         "hovered":{"id":"w:1","label":"Botza District / Static mesh 7489 - Last city crater [182]"},
         "selected":[{"id":"w:1","label":"Botza District / Static mesh 7489 - Last city crater [182]","kind":"Mesh",
           "tag":"0x80A1B2C3","package":"w64_cosmodrome_0123","values":{"Name":"Last city crater","Scale":"1.5"}}],
         "open":{"asset":"crater rock 0x80FFEE01 (w64_shared_01)","script":"scripts/freeroam.lua","changes":3,"status":"Ready"},
         "pinned":[{"id":"w:4","label":"Tube 50"}],
         "recent":[{"kind":"select","id":"w:1","label":"Static mesh 7489","secondsAgo":2.5}]}
        """;
    public int Approvals, PlayerReads;
    public bool ApproveAll, AutoApprove;
    public List<string> AutoLog = [];
    public bool Paused { get; set; }
    public AutomaticWatchdog? Automatic { get; set; }
    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0xFF, 0xD9];
    public static readonly UiControl[] Controls =
    [
        new("main:00000001", "button", "Build", "Build", "Toolbar", "", [10, 10, 60, 20], true, null, null, null),
        new("main:00000002", "menu_item", "Build to folder...", "Build to folder...", "Main menu", "File", [10, 40, 120, 20], true, null, null, null),
        new("main:00000003", "checkbox", "Grid", "Grid", "Viewport", "", [10, 70, 60, 20], true, null, true, null),
        new("main:00000004", "combo", "", "##follow", "Viewport", "", [10, 100, 90, 20], true, "Orbit", null, ["Orbit", "Free"]),
        new("main:00000005", "text", "Package ID", "Package ID", "Inspector", "", [10, 130, 90, 20], true, "0123", null, null),
        new("main:00000006", "button", "Delete", "Delete##asset", "Inspector", "", [10, 160, 60, 20], true, null, null, null),
        new("main:00000007", "button", "Delete", "Delete##node", "Hierarchy", "", [10, 190, 60, 20], true, null, null, null),
        new("main:00000008", "button", "Far away", "Far away", "Inspector", "", [10, 2000, 60, 20], false, null, null, null)
    ];
    public readonly List<UiCommand> Commands = [];
    public ViewportFeed Viewport { get; } = CreateFeed();
    private static ViewportFeed CreateFeed()
    {
        var feed = new ViewportFeed();
        feed.Offer(new byte[ViewportFeed.GridWidth * ViewportFeed.GridHeight], 1, () => (Jpeg, 64, 36));
        return feed;
    }
    /** Fake host member: returns canned or recorded data so the MCP tools run without Solforge. */
    public Task<IReadOnlyList<UiControl>> UiControlsAsync(CancellationToken cancel) => Task.FromResult<IReadOnlyList<UiControl>>(Controls);
    /** Fake host member: returns canned or recorded data so the MCP tools run without Solforge. */
    public Task<(bool Ok, string Message)> UiCommandAsync(UiCommand command, CancellationToken cancel)
    {
        lock (Commands) Commands.Add(command);
        return Task.FromResult((true, "done"));
    }
    public string? ProjectRoot => project;
    public IReadOnlyList<string> SdkRoots => [];
    public IReadOnlyList<string> AllowedReadFolders { get; set; } = [];
    public string? CurrentMap => "Botza District";
    public AttentionLog Attention { get; } = new();
    public string ContextJson => Context;
    public IReadOnlyList<VisibleObject> Visible => [];
    /** Fake host member: returns canned or recorded data so the MCP tools run without Solforge. */
    public Task<IReadOnlyList<ObjectEntry>> ObjectsAsync(string query, CancellationToken cancel) =>
        Task.FromResult<IReadOnlyList<ObjectEntry>>(
        [
            new("w:1", "Botza District / Static mesh 7489 - Last city crater [182]", "Mesh", ObjectScope.Viewport, "Botza District"),
            new("w:3", "Botza District / Static mesh 74890 - Crater rim [183]", "Mesh", ObjectScope.Tree, "Botza District")
        ]);
    /** Fake host member: returns canned or recorded data so the MCP tools run without Solforge. */
    public Task<JsonObject?> GetObjectAsync(string id, CancellationToken cancel) => Task.FromResult<JsonObject?>(null);
    /** Fake host member: returns canned or recorded data so the MCP tools run without Solforge. */
    public Task<JsonObject> GetOpenDocumentsAsync(CancellationToken cancel) => Task.FromResult(new JsonObject
    {
        ["asset"] = new JsonObject { ["tag"] = "0x80A1B2C3", ["type"] = 8, ["package"] = "w64_cosmodrome_0123", ["name"] = "crater" },
        ["script"] = new JsonObject { ["path"] = "scripts/freeroam.lua", ["unsaved"] = false },
        ["changes"] = new JsonObject { ["count"] = 3, ["rows"] = new JsonArray() }
    });
    /** Fake host member: returns canned or recorded data so the MCP tools run without Solforge. */
    public Task<byte[]?> ScreenshotAsync(string? objectId, bool labels, CancellationToken cancel) => Task.FromResult<byte[]?>(null);
    /** Fake host member: returns canned or recorded data so the MCP tools run without Solforge. */
    public Task<JsonObject?> GetTagAsync(uint hash, CancellationToken cancel) => Task.FromResult<JsonObject?>(null);
    /** Fake host member: returns canned or recorded data so the MCP tools run without Solforge. */
    public Task<JsonObject?> GetPackageEntryAsync(string id, CancellationToken cancel) => Task.FromResult<JsonObject?>(null);
    /** Fake host member: returns canned or recorded data so the MCP tools run without Solforge. */
    public JsonObject LiveState() => new() { ["attached"] = false };
    public IReadOnlyList<CodeRoot> CodeRoots { get; set; } = [];
    public string? ReferenceFolder { get; set; }
    public string? Panels;
    /** Fake host member: returns canned or recorded data so the MCP tools run without Solforge. */
    public Task<MissionSnapshot?> MissionAsync(CancellationToken cancel) => Task.FromResult<MissionSnapshot?>(MissionFixtures.Snapshot(CurrentMap!));
    /** Fake host member: returns canned or recorded data so the MCP tools run without Solforge. */
    public Task<JsonObject> ReadPanelAsync(string panel, PanelQuery query, CancellationToken cancel)
    {
        Panels = panel;
        return Task.FromResult(new JsonObject { ["total"] = 0, ["rows"] = new JsonArray() });
    }
    /** Fake host member: returns canned or recorded data so the MCP tools run without Solforge. */
    public Task<bool> ApproveAsync(string tool, string summary, CancellationToken cancel) =>
        new ApprovalGate(() => AutoApprove, (t, s) => AutoLog.Add(t + ": " + s)).AskAsync(tool, summary, () => { LastSummary = summary; Interlocked.Increment(ref Approvals); return Task.FromResult(ApproveAll); });
    /** Fake host member: returns canned or recorded data so the MCP tools run without Solforge. */
    public Task<string> RunActionAsync(string tool, JsonObject args, CancellationToken cancel) => Task.FromResult("ran");
    public readonly List<string> Queries = [];
    public string? LastSummary;
    /** Fake host member: returns canned or recorded data so the MCP tools run without Solforge. */
    public Task<JsonObject> QueryAsync(string tool, JsonObject args, CancellationToken cancel)
    {
        lock (Queries) Queries.Add(tool);
        return Task.FromResult(tool switch
        {
            // The player walks 4 m closer to the generator (at x = 20) on every read.
            "get_player_position" when McpJson.String(args, "target") is "missing" => new JsonObject { ["error"] = "No object or mission slot 'missing' on the open map." },
            "get_player_position" => new JsonObject
            {
                ["found"] = true, ["source"] = "recorder", ["position"] = new JsonArray(Math.Min(20, 4 * Interlocked.Increment(ref PlayerReads)), 0, 0),
                ["target"] = McpJson.String(args, "target") is { } target ? new JsonObject { ["id"] = target, ["position"] = new JsonArray(20, 0, 0) } : null
            },
            "list_maps" => new JsonObject { ["total"] = 1, ["rows"] = new JsonArray(new JsonObject { ["name"] = "The EDZ", ["zones"] = new JsonArray("Dark Forest") }) },
            "get_item" => new JsonObject { ["error"] = "Solforge is reading the item catalog (loading); call again shortly." },
            "get_shader" => new JsonObject
            {
                ["tag"] = McpJson.String(args, "tag"), ["kind"] = "shader", ["stage"] = "pixel", ["profile"] = "ps_5_0",
                ["constantBuffers"] = new JsonArray(new JsonObject { ["slot"] = 12, ["float4s"] = 34, ["bytes"] = 544 }),
                ["disassembly"] = McpJson.Bool(args, "disassembly", false) ? new JsonObject { ["total"] = 1, ["text"] = "ps_5_0" } : null
            },
            "get_animation" => new JsonObject { ["error"] = "That tag is neither an animation clip nor an entity; use list_animations." },
            _ => new JsonObject { ["total"] = 0, ["rows"] = new JsonArray() }
        });
    }
}
