/**
 * File: The editor side of the MCP server: AiHost implements ISolforgeHost (Solforge.Mcp) by marshalling onto the UI
 * thread; context snapshots, approvals, edit tracking and screenshots live here. The server starts with the editor
 * (OnShown, hook H8) so solforge-mcp and Solforge AI attach whether or not a project or the AI panel is open. There is
 * no in-editor console any more: approvals raise the toolbar badge, a toast and the AI Requests tab, and screenshots
 * go to the clipboard as a path.
 */
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ImGuiNET;
using Solforge.Harness.Credentials;
using Solforge.Mcp;
using SunriseStudio.Formats;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/**
 * Solforge Phase 47B: the Solforge side of the MCP tools. Snapshots (context, visible objects) are
 * published from the UI thread a few times a second; everything else is marshalled onto the UI thread.
 */
public sealed partial class StudioForm
{
    /** How an AI action request was answered, as shown in the AI request log. */
    private enum AiRequestOutcome { Auto, Approved, Denied }
    /** One answered tool request, kept for the AI request log (newest last); Seq orders it with the AI log lines. */
    private sealed record AiRequestLogEntry(long Seq, DateTime Time, string Tool, string Summary, AiRequestOutcome Outcome, string? Session);
    /** One Solforge AI or workflow line of the AI log (AppendAiLog). */
    private sealed record AiLogLine(long Seq, DateTime Time, string Text);
    /**
     * A pending tool request waiting for the user to approve or decline (kind "action"). Id is 128-bit random hex; Expires is
     * Created + 5 minutes (expiry denies). Reason says who answered ("user" in the editor, "app" in Solforge AI, "stopped").
     */
    private sealed record AiRequest(string Id, string Tool, string Summary, DateTimeOffset Created, DateTimeOffset Expires, McpSession? Session,
        bool Worker, TaskCompletionSource<bool> Answer)
    {
        public string Reason { get; set; } = "user";
    }
    /** An edit the AI made (file, shown text, previous content) kept for undo; Id addresses it from Solforge AI. */
    private sealed record AiEdit(string Id, string Path, string Shown, string? Previous, DateTime Time);

    /** How long an action request waits before it counts as denied. */
    private static readonly TimeSpan AiRequestTimeout = TimeSpan.FromMinutes(5);
    /** Capacity of the AI log ring (request entries and lines each). */
    private const int AiLogCapacity = 500;
    private long aiLogSeq;
    private readonly List<AiLogLine> aiLogLines = [];
    /** Ids answered recently, so a late answer from Solforge AI gets 409 rather than 404. */
    private readonly LinkedList<string> aiAnsweredIds = [];

    /** A new 128-bit random hex id for requests and edits. */
    private static string NewAiId() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /** Publishes one event on the editor UI channel (no-op without the MCP service). */
    private void EmitUi(string type, object? data) => aiService?.EditorUi?.Emit(type, data);
    /** Publishes a coalesced data-less event on the editor UI channel. */
    private void EmitUiSoon(string type) => aiService?.EditorUi?.EmitSoon(type);

    /** Remembers an answered id and publishes request.answered, once per id. */
    private void NoteAiAnswered(string id, string kind, string outcome, string reason)
    {
        lock (aiAnsweredIds)
        {
            if (aiAnsweredIds.Contains(id)) return;
            aiAnsweredIds.AddLast(id);
            while (aiAnsweredIds.Count > 200) aiAnsweredIds.RemoveFirst();
        }
        EmitUi(EditorUiEvents.RequestAnswered, new EditorRequestAnsweredDto(id, kind, outcome, reason));
        EmitUiSoon(EditorUiEvents.StateChanged);
    }

    /** The session reference of a request, from the MCP session that asked. */
    private static EditorSessionRefDto? AiSessionRef(McpSession? session) =>
        session is null ? null : new EditorSessionRefDto(session.Id, session.Label, session.Client, session.Pid);

    private SolforgeMcpService? aiService;
    private string? aiServiceError, aiConfiguredProject;
    private AiHost? aiHost;
    private readonly List<AiRequest> aiRequests = [];
    private readonly List<AiEdit> aiEdits = [];
    /** Answered AI requests (auto-approved, approved or denied), shown as a log in the AI panel. */
    private readonly List<AiRequestLogEntry> aiRequestLog = [];
    private readonly List<(WorldObject Object, double Until)> aiHighlights = [];
    private TaskCompletionSource<byte[]?>? aiScreenshot;
    /** A requested screenshot: object id, whether to label, whether to send it to the AI. */
    private (string? Id, bool Labels, bool Send) aiScreenshotRequest;
    private double aiNextPublish;
    private FileSystemWatcher? aiWatcher;
    private DateTime aiReloadAfter = DateTime.MaxValue;
    /** Cached facts about the inspected object and when they were gathered. */
    private (object? Source, double At, JsonObject Facts) aiFacts = (null, 0, new JsonObject());
    private bool aiFrameHooked;

    /** Settings > AI: the MCP server switch; off stops it now, on starts it and rewrites the engine configs. */
    private void SetAiMcp(bool on)
    {
        settings.AiMcpEnabled = on;
        SaveSettings();
        aiServiceError = null;
        aiConfiguredProject = null;
        if (!on) StopAiService();
        else EnsureAiService();
    }

    /** Starts the MCP HTTP service; OnShown calls it at startup and the AI panel and launches call it again (a no-op). */
    partial void EnsureAiService()
    {
        if (!settings.AiMcpEnabled || aiService is not null || aiServiceError is not null) return;
        try
        {
            aiHost ??= new AiHost(this);
            if (!aiFrameHooked) { FrameRendered += CaptureAiScreenshot; FrameRendered += StreamAiViewport; aiFrameHooked = true; }
            aiService = SolforgeMcpService.Start(aiHost, typeof(StudioForm).Assembly.GetName().Version?.ToString(3) ?? "0");
            UiAuto.Enabled = true;
        }
        catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException or UnauthorizedAccessException)
        { aiServiceError = exception.Message; }
    }

    /** Stops the MCP service and clears its state. */
    partial void StopAiService()
    {
        aiWatcher?.Dispose();
        // Every pending request is denied, and Solforge AI hears it before the channel closes.
        AiRequest[] actions;
        lock (aiRequests) actions = [.. aiRequests];
        foreach (var request in actions)
        {
            request.Reason = "stopped";
            if (request.Answer.TrySetResult(false))
                NoteAiAnswered(request.Id, EditorRequestKinds.Action, EditorRequestOutcomes.Denied, "stopped");
        }
        PendingDiff[] diffs;
        lock (diffReviews) diffs = [.. diffReviews];
        foreach (var diff in diffs)
        {
            diff.Reason = "stopped";
            if (diff.Answer.TrySetResult(false)) NoteAiAnswered(diff.Id, EditorRequestKinds.Edit, EditorRequestOutcomes.Denied, "stopped");
        }
        if (pendingWorkflowStart is { FromMcp: true } start)
        {
            start.Reason = "stopped";
            FinishWorkflowStart(start, false);
        }
        UiAuto.Enabled = false;
        if (aiService is { } service) { aiService = null; service.DisposeAsync().AsTask().Wait(1000); }
    }

    /** PATH read fresh from Windows once (the delegates' CLI lookup); the console's per-start refresh is gone. */
    private string aiFreshPath = "";
    private string AiFreshPath => aiFreshPath.Length > 0 ? aiFreshPath : aiFreshPath = EngineCommands.FreshPath();

    /** Reports the MCP service status text. */
    partial void AiServiceStatus(ref string status)
    {
        if (!settings.AiMcpEnabled) status = "MCP off (Settings > AI)";
        else if (aiServiceError is not null) status = "MCP error: " + aiServiceError;
        else if (aiService is { } service) status = $"MCP :{service.Port}  {service.Requests} calls" + (aiPaused ? "  (paused)" : "");
    }

    /**
     * Points discovery at the project and writes the per-client MCP configs (.mcp.json and the Claude Code, Cline and
     * Agy configs), so external clients and Solforge AI get the sol MCP for this project.
     */
    partial void PrepareAiEngine(string folder)
    {
        EnsureAiService();
        if (aiService is null || workspace.Project is null) return;
        aiService.Publish(folder);
        aiConfiguredProject = folder;
        if (AiMcpServers(folder) is { } servers)
        {
            try { aiService.WriteClientConfigs(folder, servers[0].Command, servers); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { aiServiceError = "config: " + exception.Message; }
            CheckAiMcp(servers);
        }
        else
        {
            aiServiceError = "solforge-mcp.exe not found beside Solforge";
            aiMcpWarning = "MCP: solforge-mcp.exe is not beside Solforge.exe, so the sol MCP server will not connect. Publish Solforge again.";
        }
        WatchAiProject(folder);
    }

    /**
     * The upstreams of the one `sol` MCP server for an engine start (solforge, gemini, clinepass, then the workspace's
     * sol-testkit, tiger-mcp and other .mcp.json servers), all run from a copy of the published shim that a publish
     * cannot replace or kill. Null when there is no shim.
     */
    private IReadOnlyList<SolMcpServers.Server>? AiMcpServers(string folder)
    {
        if (SolforgeMcpService.FindShim() is not { } published) return null;
        string shim = SolMcpServers.RunCopy(published, Path.Combine(McpDiscovery.Folder, "bin"));
        // Both delegates run the user's own CLI login, never a copied token; unavailable CLIs simply omit their tool.
        string path = AiFreshPath;
        return SolMcpServers.Collect(folder, shim, gemini: EngineCommands.Resolve("agy", path) is not null,
            clinePass: EngineCommands.Resolve("cline", path) is not null);
    }

    /** Shown in the AI panel: MCP servers whose exe, script or folder is missing, or a shim that does not answer. */
    private volatile string? aiMcpWarning;
    private int aiMcpCheck;

    /** Checks every server's files and that solforge-mcp answers initialize, off the UI thread. */
    private void CheckAiMcp(IReadOnlyList<SolMcpServers.Server> servers)
    {
        int run = Interlocked.Increment(ref aiMcpCheck);
        aiMcpWarning = null;
        string path = AiFreshPath;
        _ = Task.Run(async () =>
        {
            var problems = SolMcpServers.CheckFiles(servers, path).ToList();
            var solforge = servers[0];
            if (File.Exists(solforge.Command)
                && await SolMcpServers.Probe(solforge.Command, solforge.Args, TimeSpan.FromSeconds(15)) is { } error)
                problems.Add($"{solforge.Name}: {error}");
            if (run != Volatile.Read(ref aiMcpCheck)) return;
            aiMcpWarning = problems.Count == 0 ? null
                : "MCP servers that will not connect (fix the path or republish Solforge, then Re-check):\n  " + string.Join("\n  ", problems);
        });
    }

    /** Draws the MCP warning, if any (the Solforge AI status card and panel). */
    private void DrawAiMcpWarning()
    {
        if (aiMcpWarning is { } warning) AiNote(SolTheme.Warn, warning);
    }

    /** The found folders the user has not hidden, plus the ones they added. */
    private IReadOnlyList<string> AiAddDirs(string folder)
    {
        var hidden = settings.AiHiddenFolders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folders = AiFoundFolders(folder).Where(found => !hidden.Contains(found)).Concat(settings.AiFolders.Where(Directory.Exists));
        return EngineLaunch.AddDirs(folder, folders, ancestors: false);
    }

    /** Replace the read grant snapshot on the UI thread; MCP reads never enumerate mutable settings lists. */
    private void RefreshAiReadFolders()
    {
        if (aiHost is null) return;
        aiHost.AllowedReadFolders = workspace.Project?.DirectoryPath is { } project ? AiAddDirs(project)
            : settings.AiFolders.Where(Directory.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /**
     * Folders Solforge has open outside the project: the game install, the open script's and the last export's
     * folders, and each ancestor of the project holding a .claude folder or CLAUDE.md.
     */
    private IReadOnlyList<string> AiFoundFolders(string project) => EngineLaunch.AddDirs(project,
    [
        workspace.Catalog?.GameRoot ?? settings.SunrisePath,
        missionGraph.ScriptPath is { } script ? Path.GetDirectoryName(script) : null,
        workspace.Notice?.Folder, workspace.LastBuild?.OutputDirectory, workspace.LastInstall?.ModDirectory
    ]);

    /** The last open project Solforge AI was told about (hook H11), and since when no project has been open. */
    private string? solforgeAiNotifiedProject;
    private double aiNoProjectSince = -1;

    /**
     * Once a project is open the server runs and the client configs are written, whether or not the AI panel or editor
     * is drawn. Every project change (SOLFORGE-AI-APP-SPEC §6.4) is published: discovery points at the project (null on
     * close), project.changed goes to Solforge AI, which follows the editor to it (the app is never quit for a switch),
     * and an opened project is written to last-project.json for a standalone start. A closed project is passed on only
     * after 2 s without one, because reopening the installation adopts Project = null for a moment and that is not a switch.
     */
    private void StartAiForProject()
    {
        string? project = workspace.Project?.DirectoryPath;
        double now = clock.Elapsed.TotalSeconds;
        bool changed = false;
        if (project is not null)
        {
            aiNoProjectSince = -1;
            if (!string.Equals(project, solforgeAiNotifiedProject, StringComparison.OrdinalIgnoreCase))
            {
                solforgeAiNotifiedProject = project;
                changed = true;
                SolforgeAiProjectChanged(project);
                try { AiWriteSession(project); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { aiServiceError ??= "session: " + error.Message; }
            }
        }
        else if (solforgeAiNotifiedProject is not null)
        {
            if (aiNoProjectSince < 0) aiNoProjectSince = now;
            else if (now - aiNoProjectSince >= 2)
            {
                solforgeAiNotifiedProject = null; aiNoProjectSince = -1;
                SolforgeAiProjectChanged(null);
                aiService?.Publish(null);
                aiConfiguredProject = null;
                changed = true;
            }
        }
        if (project is not null && project != aiConfiguredProject) PrepareAiEngine(project);
        if (changed) AiProjectPublished(project);
    }

    /** project.changed on the UI channel and, for an opened project, %LOCALAPPDATA%\Solforge\last-project.json. */
    private void AiProjectPublished(string? project)
    {
        var dto = AiProjectDto(project);
        EmitUi(EditorUiEvents.ProjectChanged, new { project = dto });
        EmitUiSoon(EditorUiEvents.StateChanged);
        if (dto is null) return;
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Solforge");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "last-project.json"), temporary = path + ".tmp";
            File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(new { path = dto.Path, name = dto.Name, updated = DateTimeOffset.UtcNow }));
            File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { AppendAiLog("Solforge AI: last-project.json not written: " + error.Message); }
    }

    /** The UI channel's project record, or null. */
    private static EditorProjectDto? AiProjectDto(string? project) =>
        string.IsNullOrWhiteSpace(project) ? null : new EditorProjectDto(project, Path.GetFileName(Path.TrimEndingDirectorySeparator(project)));

    private string aiLastChip = "";

    /** Runs every frame from HandleAiShortcuts: MCP start, context snapshot, highlight outlines, hot reload. */
    private void PublishAiContext()
    {
        double now = ImGui.GetTime();
        DrawAiHighlights(now);
        if (DateTime.UtcNow >= aiReloadAfter) { aiReloadAfter = DateTime.MaxValue; ReloadAiScript(); }
        StartAiForProject();
        if (aiHost is null || now < aiNextPublish) return;
        aiNextPublish = now + .25;
        RefreshAiReadFolders();
        aiHost.ContextJson = aiPaused ? "{\"paused\":true}" : BuildAiContext().ToJsonString(McpJson.Indented);
        aiHost.CurrentMap = workspace.World?.Map.Name;
        if (now >= aiHost.NextVisible) { aiHost.NextVisible = now + 1; aiHost.Visible = aiPaused ? [] : BuildAiVisible(); }
        // Solforge AI's Context tab refreshes when what the chip says changes (coalesced to 4 Hz at most).
        string chip = AiContextChip();
        if (chip != aiLastChip) { aiLastChip = chip; EmitUiSoon(EditorUiEvents.ContextChanged); }
        AiPrewarm();
    }

    /** Builds the context snapshot (open documents, selection, camera, visible objects) the AI sees. */
    private JsonObject BuildAiContext()
    {
        /** Builds one id/label JSON entry. */
        static JsonObject Item(string id, string label) => new() { ["id"] = id, ["label"] = label };
        // Mission objects carry their slot id, slot name, type, position and owning prefab.
        JsonObject WithSlot(JsonObject item, object source)
        {
            if (AiSlotFacts(source) is { } slot) item["slot"] = slot;
            return item;
        }
        var view = viewport;
        var context = new JsonObject
        {
            ["project"] = workspace.Project?.DirectoryPath,
            ["aiReadFolders"] = new JsonArray((aiHost?.AllowedReadFolders ?? []).Select(folder => (JsonNode)folder).ToArray()),
            ["aiFoldersIncludeSubfolders"] = true,
            ["map"] = workspace.World?.Map.Name,
            ["mission"] = workspace.World?.MissionTag is { } mission ? $"0x{mission:X8}" : null,
            // Hook H9 (b): the credential file is never named or read into the context.
            ["openFile"] = missionGraph.ScriptPath is { } script && workspace.Project is { } open && !CredentialGuard.IsCredentialPath(script)
                ? Path.GetRelativePath(open.DirectoryPath, script).Replace('\\', '/') : null,
            // Which model the unified Graph panel shows: mission_script, node_graph:material|weapon|mission, or null.
            ["graph_kind"] = AiGraphKind(),
            ["hovered"] = view?.HoveredObject is { } hovered ? WithSlot(Item(AiId(hovered), AiLabel(hovered)), hovered) : null,
        };
        var selected = new JsonArray();
        if (view is not null) foreach (var item in view.SelectedItems.Take(20)) selected.Add(WithSlot(Item(AiId(item), AiLabel(item)), item));
        if (view?.SelectedItems is [var first, ..] && selected[0] is JsonObject primary)
            foreach (var (name, value) in AiFacts(first)) primary[name] = value?.DeepClone();
        context["selected"] = selected;
        if (view is not null)
        {
            var eye = view.Camera.Position;
            var pivot = view.Camera.Pivot;
            context["camera"] = new JsonObject
            {
                ["position"] = new JsonArray(Math.Round(eye.X, 2), Math.Round(eye.Y, 2), Math.Round(eye.Z, 2)),
                ["target"] = new JsonArray(Math.Round(pivot.X, 2), Math.Round(pivot.Y, 2), Math.Round(pivot.Z, 2)),
                ["zone"] = NearestBubble(pivot)
            };
        }
        var pinned = new JsonArray();
        foreach (var (id, label) in aiPinned) pinned.Add(Item(id, label));
        context["pinned"] = pinned;
        context["recent"] = aiAttention.ToJson(5);
        context["open"] = BuildAiOpenSummary();
        return context;
    }

    /** Kind, tag, package and the first inspector values of a selected object; cached for a second, as inspecting is not free. */
    private JsonObject AiFacts(object source)
    {
        double now = ImGui.GetTime();
        if (ReferenceEquals(aiFacts.Source, source) && now - aiFacts.At < 1) return aiFacts.Facts;
        var facts = new JsonObject();
        if (inspectionWorld?.Find(source) is { } item)
        {
            facts["kind"] = item.Kind.ToString();
            if (item.AssetTag != 0) { facts["tag"] = $"0x{item.AssetTag:X8}"; facts["package"] = AiPackage(item.AssetTag); }
            if (inspectionWorld?.Inspect(source) is { } inspection)
            {
                var values = new JsonObject();
                foreach (var property in inspection.Properties)
                {
                    if (values.Count >= 12) break;
                    if (!values.ContainsKey(property.Name)) values[property.Name] = property.Value;
                }
                facts["values"] = values;
            }
            aiFacts = (source, now, facts);
        }
        return facts;
    }

    /** The short form of get_open_documents that rides along in get_context. */
    private JsonObject BuildAiOpenSummary()
    {
        var open = new JsonObject();
        if (workspace.Document is { } document)
            open["asset"] = $"{document.Name} 0x{document.Entry.TagHash:X8} ({document.Entry.PackageName})";
        if (missionGraph.ScriptPath is { } script && !CredentialGuard.IsCredentialPath(script))
            open["script"] = AiShownPath(script) + (missionGraph.IsDirty ? " (unsaved)" : "");
        if (AiGraphKind() is { } graphKind) open["graph_kind"] = graphKind;
        if (tagDiff is { } diff) open["tagDiff"] = $"0x{diff.TagHash:X8}, {diff.ChangedBytes} bytes differ";
        if (workspace.Project is { } project)
            open["changes"] = project.Changes.Count + project.AddedAssets.Count + project.ObjectEdits.Count + project.ConfigBindings.Count;
        open["status"] = workspace.Notice?.Text ?? workspace.Status;
        if (workspace.Error.Length > 0) open["error"] = workspace.Error;
        return open;
    }

    /** get_open_documents: everything open in Solforge right now, read on the UI thread. */
    private JsonObject BuildAiOpenDocuments()
    {
        if (aiPaused) return new JsonObject { ["paused"] = true };
        var open = new JsonObject { ["project"] = workspace.Project?.DirectoryPath };
        if (workspace.World is { } world)
            open["map"] = new JsonObject { ["name"] = world.Map.Name, ["mission"] = world.MissionTag is { } mission ? $"0x{mission:X8}" : null };
        if (workspace.Document is { } document)
        {
            var entry = document.Entry;
            var inspector = new JsonArray();
            AddAiInspector(inspector, document.Inspection.Sections, "", 150);
            var references = new JsonArray();
            foreach (var reference in document.Inspection.References.Take(40))
                references.Add(new JsonObject { ["name"] = reference.Name, ["tag"] = $"0x{reference.TagHash:X8}" });
            open["asset"] = new JsonObject
            {
                ["name"] = document.Name, ["tag"] = $"0x{entry.TagHash:X8}", ["type"] = entry.Type, ["subType"] = entry.SubType,
                ["package"] = entry.PackageName, ["kind"] = document.Inspection.Kind, ["title"] = document.Inspection.Title,
                ["page"] = shownPage, ["edited"] = workspace.Project?.ContainsEdit(entry.TagHash) ?? false,
                ["inspector"] = inspector, ["references"] = references
            };
        }
        var selection = new JsonArray();
        if (viewport is { } view)
            foreach (var source in view.SelectedItems.Take(5))
            {
                var row = new JsonObject { ["id"] = AiId(source), ["label"] = AiLabel(source) };
                if (inspectionWorld?.Find(source) is { } item)
                {
                    row["kind"] = item.Kind.ToString();
                    row["tag"] = item.AssetTag != 0 ? $"0x{item.AssetTag:X8}" : null;
                    row["package"] = AiPackage(item.AssetTag);
                    row["zone"] = item.BubbleName;
                    row["position"] = item.Position is { } p ? new JsonArray(p.X, p.Y, p.Z) : null;
                }
                if (AiSlotFacts(source) is { } slot) row["slot"] = slot;
                if (inspectionWorld?.Inspect(source) is { } inspection)
                {
                    var properties = new JsonArray();
                    foreach (var property in inspection.Properties.Take(100))
                        properties.Add(new JsonObject { ["group"] = property.Group, ["name"] = property.Name, ["value"] = property.Value });
                    row["properties"] = properties;
                }
                selection.Add(row);
            }
        open["selection"] = selection;
        if (missionGraph.ScriptPath is { } script && !CredentialGuard.IsCredentialPath(script))
            open["script"] = new JsonObject { ["path"] = AiShownPath(script), ["fullPath"] = script, ["unsaved"] = missionGraph.IsDirty };
        open["graph_kind"] = AiGraphKind();
        if (!CredentialDocumentShown() && AiGraphSummary() is { } graph) open["graph"] = graph;
        if (tagDiff is { } diff)
            open["tagDiff"] = new JsonObject
            {
                ["tag"] = $"0x{diff.TagHash:X8}", ["name"] = workspace.DisplayName(diff.TagHash), ["baseBytes"] = diff.Base.Length,
                ["authoredBytes"] = diff.Authored.Length, ["changedBytes"] = diff.ChangedBytes, ["changedWords"] = diff.ChangedWords
            };
        if (workspace.Project is { } project)
        {
            var rows = ChangeRows(project);
            var changes = new JsonArray();
            foreach (var row in rows.Take(200)) changes.Add(new JsonObject { ["label"] = row.Label, ["change"] = row.Change, ["value"] = row.Value });
            open["changes"] = new JsonObject { ["count"] = rows.Length, ["rows"] = changes };
        }
        var status = new JsonObject
        {
            ["status"] = workspace.Status, ["error"] = workspace.Error.Length > 0 ? workspace.Error : null, ["busy"] = workspace.IsBusy,
            ["lastBuild"] = workspace.LastBuild?.OutputDirectory, ["lastInstall"] = workspace.LastInstall?.ModDirectory
        };
        if (workspace.Notice is { } notice)
            status["notice"] = new JsonObject { ["text"] = notice.Text, ["failed"] = notice.Failed, ["folder"] = notice.Folder, ["details"] = notice.Details };
        open["status"] = status;
        return open;
    }

    /** Adds inspector rows for asset nodes, up to a limit. */
    private static void AddAiInspector(JsonArray rows, IReadOnlyList<AssetNode> nodes, string path, int limit)
    {
        foreach (var node in nodes)
        {
            string section = path.Length == 0 ? node.Name : path + " / " + node.Name;
            foreach (var property in node.Properties)
            {
                if (rows.Count >= limit) return;
                rows.Add(new JsonObject { ["section"] = section, ["name"] = property.Name, ["value"] = property.Value });
            }
            if (node.Children is { } children) AddAiInspector(rows, children, section, limit);
        }
    }

    /** Project-relative with forward slashes when inside the project, else the full path. */
    private string AiShownPath(string path) =>
        workspace.Project?.DirectoryPath is { } root
        && Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(root, path).Replace('\\', '/') : path;

    /** The nearest bubble (named volume) to a point, or null. */
    private string? NearestBubble(Vector3 point) => inspectionWorld?.Objects
        .Where(item => item.Position is not null && item.BubbleName.Length > 0)
        .MinBy(item => Vector3.DistanceSquared(item.Position!.Value, point))?.BubbleName;

    /** Builds the list of objects currently visible in the viewport. */
    private List<VisibleObject> BuildAiVisible()
    {
        var visible = new List<VisibleObject>();
        if (viewport is not { } view || inspectionWorld is not { } world) return visible;
        var origin = view.ScreenOrigin;
        foreach (var item in world.Objects)
        {
            if (item.Position is not { } center || view.ScreenRect(center, item.Extents ?? new Vector3(.5f)) is not { } rect) continue;
            visible.Add(new VisibleObject(item.Id, AiLabel(item.Source), item.Kind.ToString(), rect.Min.X - origin.X, rect.Min.Y - origin.Y,
                rect.Max.X - rect.Min.X, rect.Max.Y - rect.Min.Y));
            if (visible.Count >= 2000) break;
        }
        return visible;
    }

    /** A world object id, or a slot id the cached mission snapshot places. */
    private WorldObject? AiObject(string id)
    {
        if (id.StartsWith("slot/", StringComparison.OrdinalIgnoreCase)
            && aiMission is { Snapshot.IsCompletedSuccessfully: true } cached && ReferenceEquals(cached.World, inspectionWorld)
            && cached.Snapshot.Result?.Find(id)?.ObjectId is { } placed)
            id = placed;
        return inspectionWorld?.Find(id);
    }

    /** Name of the package holding a tag, or null. */
    private string? AiPackage(uint tag) =>
        tag != 0 && workspace.Catalog is { } catalog && catalog.TryGetEntry(tag, out var entry) ? entry.PackageName : null;

    /** Draws highlight markers the AI asked for in the viewport. */
    private void DrawAiHighlights(double now)
    {
        aiHighlights.RemoveAll(item => item.Until < now);
        if (aiHighlights.Count == 0 || viewport is not { } view) return;
        var draw = ImGui.GetForegroundDrawList();
        foreach (var (item, _) in aiHighlights)
            if (item.Position is { } center && view.ScreenRect(center, item.Extents ?? new Vector3(.5f)) is { } rect)
            {
                draw.AddRect(rect.Min, rect.Max, SolTheme.U32(SolTheme.Sun), 0, ImDrawFlags.None, 2.5f);
                draw.AddText(rect.Min + new Vector2(0, -ImGui.GetFontSize() - 2), SolTheme.U32(SolTheme.Sun), AiLabel(item.Source));
            }
    }

    /** One AI LOG line's outcome word and colour. */
    private static (string Word, Vector4 Color) AiOutcome(AiRequestOutcome outcome) => outcome switch
    {
        AiRequestOutcome.Auto => ("auto", SolTheme.Muted),
        AiRequestOutcome.Approved => ("approved", SolTheme.Ok),
        _ => ("denied", SolTheme.Bad)
    };

    /**
     * The requests still waiting for an answer (approve/deny cards, unchanged logic), then the last 3 AI LOG lines with
     * a link to the full log, which lives in the Output Log under its "MCP" filter now.
     */
    partial void DrawAiRequests()
    {
        AiRequest[] pending;
        lock (aiRequests) pending = [.. aiRequests];
        AiRequestLogEntry[] log;
        lock (aiRequestLog) log = [.. aiRequestLog];
        SolTheme.SectionHeader(pending.Length > 0 ? $"AI REQUESTS ({pending.Length})" : "AI REQUESTS");
        if (pending.Length == 0) ImGui.TextDisabled("Nothing waits for approval.");
        foreach (var request in pending)
        {
            ImGui.PushID(request.Id);
            var start = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X;
            var draw = ImGui.GetWindowDrawList();
            draw.ChannelsSplit(2);
            draw.ChannelsSetCurrent(1);
            ImGui.SetCursorScreenPos(start + new Vector2(SolTheme.Space4, SolTheme.Space3));
            ImGui.BeginGroup();
            ImGui.TextColored(SolTheme.Warn, request.Tool);
            if (request.Session is { } asker) { ImGui.SameLine(); ImGui.TextColored(SolTheme.Muted, asker.Label + (request.Worker ? " (workflow worker)" : "")); }
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Math.Max(40, width - SolTheme.Space4 * 2));
            ImGui.TextWrapped(request.Summary);
            ImGui.PopTextWrapPos();
            if (SolTheme.PrimaryButton("Approve")) Answer(request, true);
            ImGui.SameLine();
            if (UiAuto.Button("Deny")) Answer(request, false);
            ImGui.EndGroup();
            var end = new Vector2(start.X + width, ImGui.GetItemRectMax().Y + SolTheme.Space3);
            draw.ChannelsSetCurrent(0);
            SolTheme.ChamferFrame(draw, start, end, 0, SolTheme.U32(SolTheme.Alpha(SolTheme.Warn, .08f)), SolTheme.U32(SolTheme.Alpha(SolTheme.Warn, .45f)));
            draw.ChannelsMerge();
            ImGui.SetCursorScreenPos(new(start.X, end.Y + SolTheme.Space2));
            ImGui.Dummy(new(width, 0));
            ImGui.PopID();
        }
        SolTheme.SectionHeader("AI LOG");
        if (log.Length == 0) ImGui.TextDisabled("No MCP actions yet.");
        for (int i = log.Length - 1; i >= Math.Max(0, log.Length - 3); i--)
        {
            var item = log[i];
            var (outcome, color) = AiOutcome(item.Outcome);
            string text = $"{item.Time:HH:mm:ss}  {outcome,-8}  {item.Tool}: {item.Summary}".ReplaceLineEndings(" ");
            ImGui.TextColored(color, StudioUi.Fit(text, ImGui.GetContentRegionAvail().X));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(text);
        }
        if (UiAuto.SmallButton("More in Output Log##ai-log-more")) ShowOutputLog("MCP");
    }

    /** Adds one answered request to the AI log, keeping the newest 500, and publishes it as log.appended. */
    private void RecordAiRequest(string tool, string summary, AiRequestOutcome outcome, string? session = null)
    {
        AiRequestLogEntry entry;
        lock (aiRequestLog)
        {
            entry = new(Interlocked.Increment(ref aiLogSeq), DateTime.Now, tool, summary, outcome, session);
            aiRequestLog.Add(entry);
            if (aiRequestLog.Count > AiLogCapacity) aiRequestLog.RemoveAt(0);
        }
        EmitUi(EditorUiEvents.LogAppended, new { entries = new[] { AiLogEntryDto(entry) }, lines = Array.Empty<EditorLogLineDto>() });
    }

    /** Adds one AI log line (AppendAiLog), keeping the newest 500, and publishes it as log.appended. */
    private void RecordAiLogLine(string text)
    {
        AiLogLine line;
        lock (aiRequestLog)
        {
            line = new(Interlocked.Increment(ref aiLogSeq), DateTime.Now, text);
            aiLogLines.Add(line);
            if (aiLogLines.Count > AiLogCapacity) aiLogLines.RemoveAt(0);
        }
        EmitUi(EditorUiEvents.LogAppended, new { entries = Array.Empty<EditorLogEntryDto>(), lines = new[] { new EditorLogLineDto(line.Seq, line.Time, line.Text) } });
    }

    private static EditorLogEntryDto AiLogEntryDto(AiRequestLogEntry entry) =>
        new(entry.Seq, entry.Time, entry.Tool, entry.Summary, AiOutcome(entry.Outcome).Word, entry.Session);

    /** Draws the list of AI edits with an undo button for each. */
    partial void DrawAiEdits()
    {
        if (aiEdits.Count == 0) return;
        SolTheme.SectionHeader("AI EDITS");
        for (int i = aiEdits.Count - 1; i >= Math.Max(0, aiEdits.Count - 8); i--)
        {
            var edit = aiEdits[i];
            ImGui.PushID(i);
            if (UiAuto.SmallButton("Undo")) { UndoAiEdit(i); ImGui.PopID(); break; }
            ImGui.SameLine();
            ImGui.TextWrapped($"{edit.Time:HH:mm:ss} {edit.Shown}");
            ImGui.PopID();
        }
    }

    /**
     * Completes a pending request with the user's answer (reason "user" in the editor, "app" from Solforge AI) and records it in
     * the AI log. False when it was answered already (whichever side answers first wins).
     */
    private bool Answer(AiRequest request, bool approved, string reason = "user")
    {
        lock (aiRequests) if (!aiRequests.Remove(request)) return false;
        request.Reason = reason;
        RecordAiRequest(request.Tool, request.Summary, approved ? AiRequestOutcome.Approved : AiRequestOutcome.Denied, request.Session?.Label);
        return request.Answer.TrySetResult(approved);
    }

    /** Restores the file as it was before an AI edit. */
    private void UndoAiEdit(int index)
    {
        var edit = aiEdits[index];
        if (edit.Previous is null) File.Delete(edit.Path);
        else File.WriteAllText(edit.Path, edit.Previous);
        aiEdits.RemoveAt(index);
        aiAttention.Record(AttentionKind.Open, "file:" + edit.Shown, "Undid AI edit of " + edit.Shown);
        EmitUiSoon(EditorUiEvents.EditsChanged);
    }

    /** Saves the viewport as a PNG in the project and copies its path (the console it used to paste into is gone). */
    partial void SendAiScreenshot()
    {
        if (workspace.Project is null || viewport is null) return;
        // Hook H9 (c): nothing is captured while the credential file is on screen.
        if (CredentialDocumentShown()) { ShowInfoToast("No screenshot while the credential file is open."); return; }
        aiScreenshotRequest = (null, true, true);
        aiScreenshotCopy = true;
        aiScreenshot = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /** Whether the next saved screenshot's path goes to the clipboard (false for Solforge AI's Screenshot, which gets the path). */
    private bool aiScreenshotCopy = true;
    /** Solforge AI's pending Screenshot: the saved project-relative path, or null when nothing was saved. */
    private TaskCompletionSource<string?>? aiScreenshotSaved;

    /** After a frame is drawn and before Present: the back buffer cropped to the viewport or an object. */
    private void CaptureAiScreenshot()
    {
        if (aiScreenshot is not { } pending || renderer is null) return;
        aiScreenshot = null;
        var (id, labels, send) = aiScreenshotRequest;
        var saved = aiScreenshotSaved;
        aiScreenshotSaved = null;
        try
        {
            string temporary = Path.Combine(Path.GetTempPath(), $"solforge-ai-{Environment.ProcessId}.png");
            renderer.CapturePng(temporary);
            byte[]? png = CropAiScreenshot(temporary, id, labels);
            File.Delete(temporary);
            string? relative = null;
            if (send && png is not null && workspace.Project is { } project)
            {
                string folder = Path.Combine(project.DirectoryPath, ".solforge", "screenshots");
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, $"viewport-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                File.WriteAllBytes(path, png);
                relative = Path.GetRelativePath(project.DirectoryPath, path).Replace('\\', '/');
                if (aiScreenshotCopy)
                {
                    CopyText(relative);
                    ShowInfoToast("Screenshot saved; its path is on the clipboard for Solforge AI");
                }
                else ShowInfoToast("Screenshot saved; its path went to Solforge AI");
            }
            aiScreenshotCopy = true;
            pending.TrySetResult(png);
            saved?.TrySetResult(relative);
        }
        catch (Exception exception) when (exception is IOException or ExternalException or ArgumentException or UnauthorizedAccessException)
        { aiScreenshotCopy = true; pending.TrySetResult(null); saved?.TrySetResult(null); }
    }

    /** Crops a screenshot to an object (optionally with labels); null when it cannot. */
    private byte[]? CropAiScreenshot(string path, string? id, bool labels)
    {
        if (viewport is not { } view || view.ScreenSize.X < 2) return null;
        using var full = new Bitmap(path);
        var area = new RectangleF(view.ScreenOrigin.X, view.ScreenOrigin.Y, view.ScreenSize.X, view.ScreenSize.Y);
        var objects = aiHost?.Visible ?? [];
        if (id is not null)
        {
            if (objects.FirstOrDefault(item => item.Id == id) is not { } target) return null;
            const float pad = 48;
            area = RectangleF.Intersect(area, new RectangleF(view.ScreenOrigin.X + target.X - pad, view.ScreenOrigin.Y + target.Y - pad,
                target.Width + 2 * pad, target.Height + 2 * pad));
        }
        var crop = Rectangle.Intersect(Rectangle.Round(area), new Rectangle(0, 0, full.Width, full.Height));
        if (crop.Width < 2 || crop.Height < 2) return null;
        using var image = full.Clone(crop, PixelFormat.Format32bppArgb);
        if (labels)
        {
            using var graphics = Graphics.FromImage(image);
            using var pen = new Pen(Color.FromArgb(255, 255, 182, 72), 2);
            using var font = new Font("Segoe UI", 9f);
            using var brush = new SolidBrush(Color.FromArgb(255, 255, 216, 138));
            using var back = new SolidBrush(Color.FromArgb(180, 8, 9, 13));
            foreach (var item in objects.Where(item => id is null ? item.Width * item.Height > 400 : item.Id == id).Take(40))
            {
                var box = new RectangleF(view.ScreenOrigin.X + item.X - crop.X, view.ScreenOrigin.Y + item.Y - crop.Y, item.Width, item.Height);
                graphics.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
                var size = graphics.MeasureString(item.Label, font);
                graphics.FillRectangle(back, box.X, box.Y - size.Height, size.Width, size.Height);
                graphics.DrawString(item.Label, font, brush, box.X, box.Y - size.Height);
            }
        }
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    /** Watches the project folder so AI file changes show up and scripts reload. */
    private void WatchAiProject(string folder)
    {
        aiWatcher?.Dispose();
        aiWatcher = new FileSystemWatcher(folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
        aiWatcher.Changed += (_, e) => OnAiFileChanged(e.FullPath);
        aiWatcher.Created += (_, e) => OnAiFileChanged(e.FullPath);
        aiWatcher.EnableRaisingEvents = true;
    }

    /** A write to the open mission script (AI or editor) reloads it after a short settle, unless it has unsaved edits. */
    private void OnAiFileChanged(string path)
    {
        if (missionGraph.ScriptPath is { } script && string.Equals(Path.GetFullPath(path), Path.GetFullPath(script), StringComparison.OrdinalIgnoreCase))
            aiReloadAfter = DateTime.UtcNow.AddMilliseconds(300);
    }

    /** Reloads the open mission script after a file change. */
    private void ReloadAiScript()
    {
        if (missionGraph.ScriptPath is not { } script || missionGraph.IsDirty) return;
        missionGraph.OpenScript(script, workspace);
    }

    /** Runs work on the UI thread and returns its result as a Task. */
    private Task<T> OnUi<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            BeginInvoke((Action)(() =>
            {
                try { done.TrySetResult(work()); }
                catch (Exception exception) { done.TrySetException(exception); }
            }));
        }
        catch (InvalidOperationException exception) { done.TrySetException(exception); }
        return done.Task;
    }

    /**
     * Runs an editor action tool (select, focus, highlight, open, edit) on the UI thread and returns the result text.
     */
    private string RunAiAction(string tool, JsonObject args)
    {
        string? id = McpJson.String(args, "id");
        switch (tool)
        {
            case "select_object" or "focus_camera":
                if (id is null || AiObject(id) is not { } item) return "No object with that id in the open map.";
                SelectSceneObject(item.Source, tool == "focus_camera");
                aiAttention.Record(tool == "focus_camera" ? AttentionKind.Focus : AttentionKind.Select, item.Id, AiLabel(item.Source));
                return (tool == "focus_camera" ? "Focused on " : "Selected ") + AiLabel(item.Source);
            case "highlight":
                double until = ImGui.GetTime() + Math.Clamp(McpJson.Int(args, "seconds", 8), 1, 60);
                int count = 0;
                foreach (var node in args["ids"] as JsonArray ?? new JsonArray())
                    if (node?.ToString() is { } each && AiObject(each) is { } found) { aiHighlights.Add((found, until)); count++; }
                return $"Highlighted {count} object(s).";
            case "open_file":
                string shown = McpJson.String(args, "path") ?? "";
                if (ProjectFiles.Resolve(workspace.Project?.DirectoryPath, aiHost?.SdkRoots ?? [], shown) is not { } path) return "No such file.";
                aiAttention.Record(AttentionKind.Open, "file:" + shown, shown);
                if (path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                {
                    missionGraph.OpenScript(path, workspace);
                    focusMissionGraph = true;
                    return $"Opened {shown} in the Graph panel (graph_kind mission_script).";
                }
                if (path.EndsWith(Solforge.Graphs.GraphDocument.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    if (nodeGraph.IsDirty && !string.Equals(nodeGraph.FilePath, path, StringComparison.OrdinalIgnoreCase))
                        return "The Graph panel's node graph has unsaved changes; ask the user to save or revert it first.";
                    nodeGraph.OpenFile(path);
                    focusMissionGraph = true;
                    return $"Opened {shown} in the Graph panel (graph_kind {AiGraphKind() ?? "none"}).";
                }
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
                return $"Opened {shown}.";
            case "apply_project_edit":
                string relative = McpJson.String(args, "path") ?? "";
                if (ProjectFiles.EditablePath(workspace.Project?.DirectoryPath, relative, out string? reason) is not { } target) return reason ?? "Refused.";
                string? previous = File.Exists(target) ? File.ReadAllText(target) : null;
                string next;
                if (McpJson.String(args, "content") is { } content) next = content;
                else
                {
                    string find = McpJson.String(args, "find") ?? "", replace = McpJson.String(args, "replace") ?? "";
                    if (previous is null) return "The file does not exist; send content instead.";
                    int at = find.Length == 0 ? -1 : previous.IndexOf(find, StringComparison.Ordinal);
                    if (at < 0 || previous.IndexOf(find, at + 1, StringComparison.Ordinal) >= 0) return "The find text must occur exactly once.";
                    next = previous[..at] + replace + previous[(at + find.Length)..];
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, next);
                aiEdits.Add(new AiEdit(NewAiId(), target,relative.Replace('\\', '/'), previous, DateTime.Now));
                aiAttention.Record(AttentionKind.Open, "file:" + relative, "AI edited " + relative);
                EmitUiSoon(EditorUiEvents.EditsChanged);
                return $"Wrote {relative} ({next.Length} chars). It is listed under AI edits in the Solforge AI panel, where Undo restores it.";
            default:
                return RunAiProjectAction(tool, args) ?? "Unknown action.";
        }
    }

    /** Details of an asset tag for get_tag, or null. */
    private JsonObject? AiAssetDetails(uint? hash)
    {
        if (hash is not { } tag || workspace.Catalog is not { } catalog || !catalog.TryGetEntry(tag, out var entry)) return null;
        var asset = workspace.Assets.FirstOrDefault(item => item.Entry.TagHash == tag);
        return new JsonObject
        {
            ["tag"] = $"0x{entry.TagHash:X8}", ["reference"] = $"0x{entry.Reference:X8}", ["type"] = entry.Type, ["subType"] = entry.SubType,
            ["size"] = entry.Size, ["package"] = entry.PackageName, ["packageId"] = entry.PackageId, ["entryIndex"] = entry.EntryIndex,
            ["name"] = asset?.Name, ["kind"] = asset?.Kind
        };
    }

    /** The tool host; members the tools read off the UI thread are swapped whole. */
    private sealed partial class AiHost(StudioForm form) : ISolforgeHost
    {
        /** The owning form, for the other AiHost parts (primary-constructor parameters are visible in this part only). */
        private StudioForm Owner => form;
        private volatile string contextJson = "{}";
        private volatile IReadOnlyList<VisibleObject> visible = [];
        public string ContextJson { get => contextJson; set => contextJson = value; }
        // A map-scoped call (MultiAgentTools, map=<tag>) reads a background copy: nothing of it is on screen.
        public IReadOnlyList<VisibleObject> Visible { get => Scoped is null ? visible : []; set => visible = value; }
        private string? currentMap;
        public string? CurrentMap { get => Scoped is { } view ? view.Name : currentMap; set => currentMap = value; }
        public double NextVisible { get; set; }
        public bool Paused => form.aiPaused;
        public string? ProjectRoot => form.workspace.Project?.DirectoryPath;
        public IReadOnlyList<string> SdkRoots => ProjectRoot is { } root
            ? new[] { Path.Combine(root, "sdk"), Path.Combine(root, "docs") }.Where(Directory.Exists).ToArray() : [];
        private volatile IReadOnlyList<string> readFolders = [];
        public IReadOnlyList<string> AllowedReadFolders { get => readFolders; set => readFolders = value; }
        /** ISolforgeHost: Settings > Paths server mission scripts folder, read live so set_settings applies at once. */
        public string? ServerScriptsRoot => form.settings.ScriptsPath is { Length: > 0 } scripts && Directory.Exists(scripts) ? scripts : null;
        public AttentionLog Attention => form.aiAttention;

        public IReadOnlyList<CodeRoot> CodeRoots => form.aiCodeRoots;
        public string? ReferenceFolder => ReferenceLibrary.Locate(form.aiReferenceStarts);

        /** ISolforgeHost: fuzzy object search. */
        public Task<IReadOnlyList<ObjectEntry>> ObjectsAsync(string query, CancellationToken cancel) =>
            Scoped is { } view ? form.AiScopedObjectsAsync(view, cancel) : form.AiObjectsAsync(query, cancel);
        /** ISolforgeHost: details of one object. */
        public Task<JsonObject?> GetObjectAsync(string id, CancellationToken cancel) =>
            Scoped is { } view ? form.AiScopedObjectDetailsAsync(view, id, cancel) : form.AiObjectDetailsAsync(id, cancel);
        /** ISolforgeHost: snapshot of the open mission. */
        public Task<MissionSnapshot?> MissionAsync(CancellationToken cancel) =>
            Scoped is { } view ? form.AiScopedMissionAsync(view, cancel) : form.AiMissionAsync(cancel);
        /** ISolforgeHost: reads the contents of a panel. */
        public Task<JsonObject> ReadPanelAsync(string panel, PanelQuery query, CancellationToken cancel) => form.AiReadPanelAsync(panel, query, cancel);
        /** ISolforgeHost: lists the open documents. */
        public Task<JsonObject> GetOpenDocumentsAsync(CancellationToken cancel) => form.OnUi(form.BuildAiOpenDocuments).WaitAsync(cancel);
        /** ISolforgeHost: details of a tag. */
        public Task<JsonObject?> GetTagAsync(uint hash, CancellationToken cancel) => form.OnUi(() => form.AiAssetDetails(hash)).WaitAsync(cancel);
        /** ISolforgeHost: one package entry. */
        public Task<JsonObject?> GetPackageEntryAsync(string id, CancellationToken cancel) => form.OnUi(() =>
        {
            int slash = id.LastIndexOf('/');
            if (slash > 0 && int.TryParse(id[(slash + 1)..], out int index))
            {
                string package = id[..slash];
                var asset = form.workspace.Assets.FirstOrDefault(item => item.Entry.EntryIndex == index
                    && string.Equals(item.Entry.PackageName, package, StringComparison.OrdinalIgnoreCase));
                return asset is null ? null : form.AiAssetDetails(asset.Entry.TagHash);
            }
            return form.AiAssetDetails(SolforgeTools.ParseHash(id));
        }).WaitAsync(cancel);
        /** ISolforgeHost: state of the in-game Live link. */
        public JsonObject LiveState() => form.OnUi(() => form.livePage.AiState()).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

        /** ISolforgeHost: a viewport screenshot, optionally cropped to an object. */
        public async Task<byte[]?> ScreenshotAsync(string? objectId, bool labels, CancellationToken cancel)
        {
            // Nothing is drawn while the 3D viewport is off or paused for the game; nothing is captured while the
            // credential file is on screen (hook H9 c; the tools refuse through CredentialDocumentVisible too).
            if (!await form.OnUi(() => form.viewport is { IsRendering: true } && !form.CredentialDocumentShown()).ConfigureAwait(false)) return null;
            var pending = await form.OnUi(() =>
            {
                form.aiScreenshotRequest = (objectId, labels, false);
                return form.aiScreenshot ??= new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            }).ConfigureAwait(false);
            return await pending.Task.WaitAsync(TimeSpan.FromSeconds(10), cancel).ConfigureAwait(false);
        }

        /**
         * ISolforgeHost: asks the user to approve an action; false when declined. The request never opens a window or
         * takes focus (hook H9 a): it raises the toolbar badge and the approval toast, and docks the AI panel as a tab
         * when the layout has none. Enabled auto-approval covers permitted worker actions too; worker tool restrictions
         * and validation still run before the approval gate.
         */
        public async Task<bool> ApproveAsync(string tool, string summary, CancellationToken cancel)
        {
            bool worker = McpSessionRegistry.Current?.IsWorker == true;
            var session = McpSessionRegistry.Current;
            cancel.ThrowIfCancellationRequested();
            // Only the user's editor preference enables automatic approval; clients cannot change it through MCP.
            var gate = new ApprovalGate(() => form.settings.AutoApproveRequests, (t, s) =>
                form.RecordAiRequest(t, s, AiRequestOutcome.Auto, session?.Label));
            return await gate.AskAsync(tool, summary, async () =>
            {
                var created = DateTimeOffset.UtcNow;
                var request = new AiRequest(NewAiId(), tool, summary, created, created + AiRequestTimeout, session, worker,
                    new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
                lock (form.aiRequests) form.aiRequests.Add(request);
                form.EmitUi(EditorUiEvents.RequestAdded, AiRequestDto(request));
                form.EmitUiSoon(EditorUiEvents.StateChanged);
                await form.OnUi(() => { form.DockAiPanelForApproval(); return true; }).ConfigureAwait(false);
                string outcome = EditorRequestOutcomes.Denied, reason = "user";
                try
                {
                    bool approved = await request.Answer.Task.WaitAsync(AiRequestTimeout, cancel).ConfigureAwait(false);
                    (outcome, reason) = (approved ? EditorRequestOutcomes.Approved : EditorRequestOutcomes.Denied, request.Reason);
                    return approved;
                }
                catch (TimeoutException)
                {
                    // Expiry is a deny, recorded in the AI log like one.
                    (outcome, reason) = (EditorRequestOutcomes.Expired, "timeout");
                    bool removed;
                    lock (form.aiRequests) removed = form.aiRequests.Remove(request);
                    if (removed) form.RecordAiRequest(tool, summary + " (expired)", AiRequestOutcome.Denied, session?.Label);
                    return false;
                }
                catch (OperationCanceledException)
                {
                    (outcome, reason) = (EditorRequestOutcomes.Cancelled, "cancelled");
                    throw;
                }
                finally
                {
                    lock (form.aiRequests) form.aiRequests.Remove(request);
                    form.NoteAiAnswered(request.Id, EditorRequestKinds.Action, outcome, reason);
                }
            }, cancel).ConfigureAwait(false);
        }

        /** ISolforgeHost: runs an editor action tool. */
        public Task<string> RunActionAsync(string tool, JsonObject args, CancellationToken cancel) =>
            form.OnUi(() => { cancel.ThrowIfCancellationRequested(); return form.RunAiAction(tool, args); }).WaitAsync(cancel);

        /** ISolforgeHost: runs a project query tool (see StudioForm.Ai.Query.cs). */
        public Task<JsonObject> QueryAsync(string tool, JsonObject args, CancellationToken cancel) =>
            Scoped is { } view && tool is "find_world_objects" or "get_placement" ? form.AiScopedQueryAsync(view, tool, args, cancel)
                : form.AiQueryAsync(tool, args, cancel);

        /** Live viewport frames for the frame tools and streams. */
        public ViewportFeed Viewport { get; } = new();

        /** ISolforgeHost: lists the editor UI controls. */
        public async Task<IReadOnlyList<UiControl>> UiControlsAsync(CancellationToken cancel)
        {
            if (!UiAuto.Enabled) return [];
            var drawn = await UiAuto.SnapshotAsync(cancel).ConfigureAwait(false);
            return drawn.Select(control => new UiControl($"{control.Surface}:{control.Id:X8}", control.Kind, UiAuto.Visible(control.Label),
                control.Label, control.Window, control.Scope, [control.X, control.Y, control.Width, control.Height], control.Visible,
                control.Value, control.Checked, control.Options)).ToArray();
        }

        /** ISolforgeHost: performs a UI command (click, set value, select) on a control. */
        public async Task<(bool Ok, string Message)> UiCommandAsync(UiCommand command, CancellationToken cancel)
        {
            int colon = command.ControlId.LastIndexOf(':');
            if (colon <= 0 || !uint.TryParse(command.ControlId[(colon + 1)..], System.Globalization.NumberStyles.HexNumber, null, out uint id))
                return (false, "Not a control id; use one from list_ui_controls.");
            var operation = command.Operation switch
            {
                UiOperation.SetValue => UiAuto.Op.SetValue,
                UiOperation.SetChecked => UiAuto.Op.SetChecked,
                UiOperation.Select => UiAuto.Op.Select,
                _ => UiAuto.Op.Invoke
            };
            var pending = UiAuto.Enqueue(new UiAuto.Command(command.ControlId[..colon], id, operation, command.Value, command.Checked));
            try { return await pending.WaitAsync(TimeSpan.FromSeconds(8), cancel).ConfigureAwait(false); }
            catch (TimeoutException) { return (false, "Solforge did not draw a frame in time (busy or minimised); try again."); }
        }
    }
}
