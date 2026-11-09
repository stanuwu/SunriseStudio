using System.Diagnostics;
using System.Text.Json.Nodes;
using Solforge.Mcp;
using Solforge.Harness;
using Solforge.Harness.Shared;
using Solforge.Mcp.Headless;

/** Exercise real MCP read/search handlers and grant revocation without touching user folders or live processes. */
static class AiAccessChecks
{
    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("AI access: " + message);
    }

    public static async Task Run(string scratch)
    {
        string project = Path.Combine(scratch, "project"), allowed = Path.Combine(scratch, "allowed"), sibling = allowed + "-sibling";
        string nested = Path.Combine(allowed, "one", "two", "three");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(sibling);
        string document = Path.Combine(nested, "deep.txt"), outside = Path.Combine(sibling, "outside.txt");
        File.WriteAllText(document, "nested-grant-evidence\n");
        File.WriteAllText(outside, "outside-grant-evidence\n");
        File.WriteAllText(Path.Combine(allowed, "top.txt"), "top-only-evidence\n");
        var host = new FixtureHost(project) { AllowedReadFolders = [allowed] };
        var tools = SolforgeTools.Create(host).ToDictionary(tool => tool.Name);
        Task<McpToolResult> Read(string path) => tools["read_project_file"].Handler(new JsonObject { ["path"] = path }, CancellationToken.None);
        var read = await Read(document);
        Expect(!read.IsError && read.Texts[0].Contains("nested-grant-evidence"), "enabled parent grants deep descendant read");
        Expect((await Read(outside)).IsError, "a sibling prefix is not a descendant");
        Expect((await Read(Path.Combine(allowed, "..", "allowed-sibling", "outside.txt"))).IsError, "absolute traversal cannot escape a grant");
        Expect((await Read("../allowed/one/two/three/deep.txt")).IsError, "external roots need an unambiguous absolute path");
        var search = await tools["search_project"].Handler(new JsonObject { ["query"] = "nested-grant-evidence", ["folder"] = Path.Combine(allowed, "one") }, CancellationToken.None);
        Expect(!search.IsError && JsonNode.Parse(search.Texts[0])!.AsArray().Count == 1 && search.Texts[0].Contains("deep.txt"), "search recurses in an allowed subtree");
        var narrow = await tools["search_project"].Handler(new JsonObject { ["query"] = "top-only-evidence", ["folder"] = Path.Combine(allowed, "one") }, CancellationToken.None);
        Expect(!narrow.IsError && JsonNode.Parse(narrow.Texts[0])!.AsArray().Count == 0, "folder scoping excludes other descendants of the grant");
        var duplicate = ProjectFiles.Search(project, [], "nested-grant-evidence", ".txt", 20, _ => false, [allowed, Path.Combine(allowed, "one")]);
        Expect(duplicate.Count == 1, "overlapping grants do not duplicate hits");
        var filtered = ProjectFiles.Search(project, [], "nested-grant-evidence", ".txt", 20, file => file == document, [allowed]);
        Expect(filtered.Count == 0, "configured credential skip is honored for extra roots too");
        host.AllowedReadFolders = [];
        Expect((await Read(document)).IsError, "grant removal affects the same tool handler immediately");
        Expect(ProjectFiles.Resolve(project, [], document) is null, "external folder access is not implicit");
        Expect(ProjectFiles.EditablePath(project, document, out _) is null, "a read grant does not expand overlay edits");
        string settingsFile = Path.Combine(scratch, "settings.json");
        File.WriteAllText(settingsFile, new JsonObject { ["AiFolders"] = new JsonArray(allowed) }.ToJsonString());
        var headless = new HeadlessSolforgeHost(null, settingsFile);
        var dispatcher = HeadlessSolforgeHost.CreateDispatcher(headless, "fixture");
        string readRequest = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "read_project_file", ["arguments"] = new JsonObject { ["path"] = document } } }.ToJsonString();
        var standaloneRead = JsonNode.Parse((await dispatcher.HandleAsync(readRequest, CancellationToken.None))!)!;
        Expect(standaloneRead["result"]?["isError"]?.GetValue<bool>() != true, "persisted explicit read grant works with no project/editor open");
        File.WriteAllText(settingsFile, "{\"AiFolders\":[]}");
        var standaloneRevoked = JsonNode.Parse((await dispatcher.HandleAsync(readRequest, CancellationToken.None))!)!;
        Expect(standaloneRevoked["result"]?["isError"]?.GetValue<bool>() == true, "standalone grant revocation affects the existing dispatcher");
        await LinkEscape(allowed, sibling, outside);

        var names = tools.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (string name in WorkerSessionPolicy.AllowedWriteTools)
            Expect(names.Contains(name) && tools[name].RequiresApproval && WorkerSessionPolicy.Allows(name, true), "explicit action exists and retains its gate: " + name);
        foreach (string name in new[] { "start_model_workflow", "get_model_workflow", "cancel_model_workflow", "automatic_start", "goal_progress" })
            Expect(!WorkerSessionPolicy.Allows(name, false), "worker cannot manage parent orchestration: " + name);
        foreach (string name in new[] { "run_command", "ui_invoke", "set_settings", "build_project", "local_server_restart", "local_server_deploy", "testkit_restart", "unknown_mutation" })
            Expect(!WorkerSessionPolicy.Allows(name, true), "worker cannot use broad or shared actions: " + name);
        Expect(WorkerSessionPolicy.Allows("get_editor_state", false) && WorkerSessionPolicy.Allows("get_viewport_frame", false), "task inspection remains available");

        bool enabled = true;
        int prompts = 0, audit = 0;
        var gate = new ApprovalGate(() => enabled, (_, _) => audit++);
        Task<bool> Ask() { prompts++; return Task.FromResult(false); }
        Expect(await gate.AskAsync("apply_project_edit", "fixture", Ask) && prompts == 0 && audit == 1, "enabled preference approves and records once");
        enabled = false;
        Expect(!await gate.AskAsync("apply_project_edit", "fixture", Ask) && prompts == 1 && audit == 1, "revoked preference returns to manual review");
        enabled = true;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool stopped = false;
        try { await gate.AskAsync("apply_project_edit", "fixture", Ask, cancelled.Token); }
        catch (OperationCanceledException) { stopped = true; }
        Expect(stopped && prompts == 1 && audit == 1, "cancelled work neither auto-approves nor records a false approval");

        string shared = Path.Combine(scratch, "ai stuff", "ai-mcps-skills-consilidated"), bundle = Path.Combine(shared, "skills", "fixture");
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "SKILL.md"), "fixture skill");
        Expect(SharedHarnessResources.DiscoverSharedCollectionRoot(projectRoot: nested) == shared, "shared collection migration finds the known workspace layout");
        var explicitMissing = new HarnessSettings { SharedResourcesPath = Path.Combine(scratch, "deliberately-missing"), WorkbenchRoot = "" };
        Expect(SharedHarnessResources.Inspect(explicitMissing, projectRoot: nested).SharedSkillDirectory is null,
            "an invalid custom collection is not silently replaced");
        explicitMissing.SharedResourcesPath = "";
        Expect(SharedHarnessResources.Inspect(explicitMissing, projectRoot: nested).SharedSkillDirectory is null,
            "an explicitly disabled collection remains disabled");
    }

    private static async Task LinkEscape(string allowed, string outsideFolder, string outsideFile)
    {
        string link = Path.Combine(allowed, "escape-link");
        if (OperatingSystem.IsWindows())
        {
            // Directory junctions need no developer-mode privilege. Both endpoints are disposable test fixtures.
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "/d", "/c", "mklink", "/J", link, outsideFolder }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("Could not create the fixture junction.");
            await process.WaitForExitAsync();
            Expect(process.ExitCode == 0, "fixture junction created");
        }
        else Directory.CreateSymbolicLink(link, outsideFolder);
        try
        {
            Expect(ProjectFiles.Resolve(null, [], Path.Combine(link, Path.GetFileName(outsideFile)), readFolders: [allowed]) is null,
                "a nested junction/symlink cannot leave the granted root");
            var results = ProjectFiles.Search(null, [], "outside-grant-evidence", null, 10, _ => false, [allowed]);
            Expect(results.Count == 0, "recursive search does not follow a link outside the grant");
        }
        finally { Directory.Delete(link); }
    }
}
