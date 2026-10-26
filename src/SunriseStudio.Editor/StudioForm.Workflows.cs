/**
 * File: Model workflows in the editor (UI-UE5 §5.4): the per-project HarnessWorkflowService, the IModelWorkflows the MCP
 * workflow tools use (ISolforgeHost.Workflows on AiHost), the native confirm dialog every start goes through
 * (DrawStartWorkflowDialog, hook H10; never auto-approved, blocked for MCP UI automation), the Workflows panel
 * (DrawWorkflowStatusPanel, hook H7) and the summary line in the Solforge AI status card. No model is called on settings
 * open, validation or panel refresh: only a confirmed start runs the configured ladders.
 */
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImGuiNET;
using Solforge.AiLauncher;
using Solforge.Harness;
using Solforge.Harness.Credentials;
using Solforge.Harness.Runtime;
using Solforge.Mcp;
using SunriseStudio.Rendering;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    /**
     * A start waiting for the user's confirm: the task, SmallFix, where it came from and the answer. Id addresses it on the UI
     * channel (kind "workflow"); Reason says who answered ("user" in the editor, "app" in Solforge AI, "stopped", "cancelled").
     */
    private sealed class PendingWorkflowStart(string task, bool smallFix, bool fromMcp, McpSession? session = null)
    {
        public string Id { get; } = NewAiId();
        public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
        public McpSession? Session { get; } = session;
        public string Reason { get; set; } = "user";
        public string Task = task;
        public bool SmallFix = smallFix;
        public bool FromMcp { get; } = fromMcp;
        public bool Opened;
        public TaskCompletionSource<(bool Confirmed, string Task, bool SmallFix)> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private HarnessWorkflowService? modelWorkflows;
    private string? workflowProject, selectedWorkflow, workflowError;
    private ModelWorkflowBridge? modelWorkflowBridge;
    private PendingWorkflowStart? pendingWorkflowStart;
    private (string? Id, double At, JsonObject? State) workflowView;
    private readonly Dictionary<string, string> workflowStatuses = new(StringComparer.Ordinal);
    private readonly HttpClient harnessHttp = new(new HttpClientHandler { AllowAutoRedirect = false, UseDefaultCredentials = false }) { Timeout = TimeSpan.FromMinutes(5) };

    /** %LOCALAPPDATA%\Solforge\workflows\<projectKey>. */
    private static string WorkflowHistory(string project) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Solforge", "workflows", ProjectKey.Key(project, "workflow"));

    /** The open project's workflow service; null (workflowError says why) with no project or no MCP host. */
    private HarnessWorkflowService? WorkflowService()
    {
        string? project = workspace.Project?.DirectoryPath;
        if (project is null) { workflowError = "Open a project to run model workflows."; return null; }
        if (modelWorkflows is not null && SameFolder(workflowProject, project)) return modelWorkflows;
        EnsureAiService();
        if (aiHost is not { } host) { workflowError = "The MCP service is off (Settings > AI), so workflow workers cannot reach the editor."; return null; }
        CloseModelWorkflows(project);
        string root = project, history = WorkflowHistory(project);
        var gateway = new EditorGateway(host, root);
        string shim = solforgeAiShim ?? SolforgeMcpService.FindShim() ?? Path.Combine(AppContext.BaseDirectory, "solforge-mcp.exe");
        var service = new HarnessWorkflowService(root, history,
            () => Task.FromResult(WorkflowSettings()),
            configured =>
            {
                // Resolved per outgoing request from the editor's credential file only, never stored, logged or shown. The keys
                // saved inside old Solforge AI profiles are NOT read (that fallback awaits the user's explicit approval).
                // The editor's credential file first, then the keys the user saved inside Solforge AI.
                string? ResolveKey(string name) => CredentialEnvironment.Value(configured.CredentialFilePath, name) ?? AppCredentialStore.Value(name);
                var api = new OpenAiCompatibleWorker(harnessHttp, gateway, ResolveKey);
                string path = EngineCommands.FreshPath();
                var subscription = new SubscriptionModelWorker(root, gateway, engine => EngineCommands.Resolve(engine, path), shim, history);
                return new Dictionary<HarnessTransport, IModelWorker>
                {
                    [HarnessTransport.OpenAiCompatible] = api, [HarnessTransport.CodexSubscription] = subscription,
                    [HarnessTransport.ClaudeSubscription] = subscription, [HarnessTransport.GoogleSubscription] = subscription
                };
            });
        service.Updated += id => NoteWorkflowUpdate(service, id);
        modelWorkflows = service;
        workflowProject = root;
        workflowError = null;
        return service;
    }

    /** The saved model team with the editor's credential file, shared resources and Workbench copied in. */
    private HarnessSettings WorkflowSettings()
    {
        var copy = (LoadHarnessSettings(reload: true) ?? new HarnessSettings()).Clone();
        copy.CredentialFilePath = string.IsNullOrWhiteSpace(settings.CredentialFile) ? null : settings.CredentialFile;
        copy.SharedResourcesPath = settings.SharedResourcesRoot;
        copy.WorkbenchRoot = string.IsNullOrWhiteSpace(settings.WorkbenchRoot) ? null : settings.WorkbenchRoot;
        return copy;
    }

    /** Stops the workflows of a project the editor left (cancellation keeps their task and completed stages). */
    private void CloseModelWorkflows(string? keepProject)
    {
        if (modelWorkflows is not { } owned || SameFolder(workflowProject, keepProject)) return;
        modelWorkflows = null; workflowProject = null; selectedWorkflow = null; workflowView = default;
        if (pendingWorkflowStart is { } waiting) { waiting.Reason = "cancelled"; FinishWorkflowStart(waiting, false); }
        _ = owned.StopAsync();
    }

    /** Journal events go to the AI log (hook H16) when a run's status changes. Called on worker threads. */
    private void NoteWorkflowUpdate(HarnessWorkflowService service, string id)
    {
        try
        {
            string status = service.Get(id).GetProperty("Status").GetString() ?? "";
            lock (workflowStatuses)
            {
                if (workflowStatuses.TryGetValue(id, out var previous) && previous == status) return;
                workflowStatuses[id] = status;
            }
            AppendAiLog($"Workflow {id[..8]}: {status}");
        }
        catch (Exception error) when (error is IOException or ArgumentException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException) { }
    }

    /** One line for the Solforge AI status card: the active or last workflow, falling back to persisted history. */
    private void DrawWorkflowSummaryLine()
    {
        string text;
        try
        {
            if (workspace.Project?.DirectoryPath is not { } project) text = "Workflows: no project open.";
            else
            {
                string? id = selectedWorkflow ?? modelWorkflows?.RecentIds().FirstOrDefault() ?? PersistedWorkflowIds(project).FirstOrDefault();
                text = id is null ? "Workflows: none recorded for this project."
                    : "Workflow " + id[..8] + " · " + (WorkflowState(id)?["Status"]?.ToString() ?? "unknown");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { text = "Workflow history is unavailable."; }
        ImGui.PushStyleColor(ImGuiCol.Text, SolTheme.Muted);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Window > Panels > Workflows shows the stages, models and outputs.");
    }

    /** Run ids saved for a project, newest first (no service needed). */
    private static IEnumerable<string> PersistedWorkflowIds(string project)
    {
        string folder = WorkflowHistory(project);
        if (!Directory.Exists(folder)) return [];
        return new DirectoryInfo(folder).EnumerateFiles("*.json").OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => Path.GetFileNameWithoutExtension(file.Name)).Where(id => Guid.TryParseExact(id, "N", out _)).Take(20).ToArray();
    }

    /** A run's snapshot as JSON (live or persisted), refreshed at most once a second; null when unreadable. */
    private JsonObject? WorkflowState(string id)
    {
        double now = ImGui.GetTime();
        if (workflowView.Id == id && now - workflowView.At < 1) return workflowView.State;
        JsonObject? state = null;
        try
        {
            if (WorkflowService() is { } service) state = JsonNode.Parse(service.Get(id).GetRawText()) as JsonObject;
        }
        catch (Exception error) when (error is IOException or ArgumentException or JsonException or InvalidDataException or UnauthorizedAccessException) { }
        workflowView = (id, now, state);
        return state;
    }

    /** Opens the confirm dialog for a start the user begins from the panel. */
    private void BeginWorkflowStart()
    {
        if (pendingWorkflowStart is not null) return;
        var pending = new PendingWorkflowStart("", false, fromMcp: false);
        pendingWorkflowStart = pending;
        AnnounceWorkflowStart(pending);
        _ = pending.Answer.Task.ContinueWith(async answer =>
        {
            var (confirmed, task, small) = answer.Result;
            if (!confirmed) return;
            try
            {
                if (await OnUi(WorkflowService).ConfigureAwait(false) is { } service)
                {
                    string id = await service.StartAsync(task, small).ConfigureAwait(false);
                    selectedWorkflow = id;
                    AppendAiLog($"Workflow {id[..8]} started from the Workflows panel.");
                }
            }
            catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or JsonException)
            { workflowError = "Could not start the workflow: " + error.Message; }
        }, TaskScheduler.Default);
    }

    /**
     * Hook H10: the start confirm (UI-UE5 §5.4): the task, SmallFix, and the routed models per role in ladder order.
     * Drawn every frame after the layout; opens itself when a start waits. Confirm is blocked for MCP UI automation.
     */
    public void DrawStartWorkflowDialog()
    {
        const string id = "Run a model workflow###solforge-start-workflow";
        // Answered in Solforge AI or cancelled: close the modal if it is still open, so it never blocks input.
        if (pendingWorkflowStart is not { } pending) { CloseOrphanModal(id); return; }
        if (!pending.Opened) { ImGui.OpenPopup(id); pending.Opened = true; }
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.Appearing, new Vector2(.5f, .5f));
        ImGui.SetNextWindowSize(new Vector2(640, 520) * scale, ImGuiCond.Appearing);
        bool open = true;
        if (!ImGui.BeginPopupModal(id, ref open))
        {
            if (!open || !ImGui.IsPopupOpen(id)) Finish(false);
            return;
        }
        UiAuto.PushBlocked();
        try
        {
            ImGui.TextWrapped(pending.FromMcp
                ? "An MCP client asks to start your configured model team. It runs the models below and may use your subscription or API quota."
                : "Runs your configured Planner -> Executor -> Reviewer ladders (SmallFix -> Reviewer for a small task). It may use your subscription or API quota.");
            ImGui.Spacing();
            ImGui.TextDisabled("Task");
            var flags = pending.FromMcp ? ImGuiInputTextFlags.ReadOnly : ImGuiInputTextFlags.None;
            ImGui.InputTextMultiline("##workflow-task", ref pending.Task, 32768, new Vector2(-1, 150 * scale), flags);
            ImGui.Checkbox("Small task: SmallFix -> Reviewer (no planner)", ref pending.SmallFix);
            ImGui.Spacing();
            ImGui.TextDisabled("Routed models (in ladder order; disabled models are skipped)");
            var team = WorkflowSettings();
            var errors = HarnessSettingsValidator.Validate(team);
            HarnessRole[] roles = pending.SmallFix
                ? [HarnessRole.SmallFix, HarnessRole.SubagentReviewer, HarnessRole.Reviewer]
                : [HarnessRole.Planner, HarnessRole.Executor, HarnessRole.SubagentReviewer, HarnessRole.Reviewer, HarnessRole.SmallFix];
            bool routable = true;
            if (ImGui.BeginTable("workflow-routes", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthFixed, 140 * scale);
                ImGui.TableSetupColumn("Models");
                foreach (var role in roles)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(RoleLabel(role));
                    ImGui.TableNextColumn();
                    var ladder = team.Route(role).ModelIds.Select(model => team.Models.FirstOrDefault(item => item.Id == model)).OfType<HarnessModel>().ToList();
                    if (ladder.Count == 0)
                    {
                        bool optional = role is HarnessRole.SubagentReviewer || (role == HarnessRole.SmallFix && !pending.SmallFix);
                        ImGui.TextColored(optional ? SolTheme.Muted : SolTheme.Bad, optional ? "none (skipped)" : "none: this role pauses the run");
                        if (!optional) routable = false;
                    }
                    else ImGui.TextWrapped(string.Join(" -> ", ladder.Select(model => model.DisplayName + (model.Enabled ? "" : " (disabled)"))));
                }
                ImGui.EndTable();
            }
            foreach (string error in errors) ImGui.TextColored(SolTheme.Bad, error);
            if (!routable) ImGui.TextColored(SolTheme.Warn, "A required role has no models: the run would pause at that stage. Configure it in Settings > Models & workflows.");
            ImGui.Spacing();
            bool valid = errors.Count == 0 && pending.Task.Trim().Length is > 0 and <= 32768;
            ImGui.BeginDisabled(!valid);
            if (SolTheme.PrimaryButton("Confirm and run##workflow-confirm")) { Finish(true); ImGui.CloseCurrentPopup(); }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Cancel##workflow-cancel") || ImGui.IsKeyPressed(ImGuiKey.Escape)) { Finish(false); ImGui.CloseCurrentPopup(); }
        }
        finally { UiAuto.PopBlocked(); }
        ImGui.EndPopup();

        void Finish(bool confirmed) => FinishWorkflowStart(pending, confirmed);
    }

    /** Ends a waiting start (UI thread): clears it, completes its answer and tells Solforge AI. */
    private void FinishWorkflowStart(PendingWorkflowStart pending, bool confirmed)
    {
        if (!ReferenceEquals(pendingWorkflowStart, pending)) return;
        pendingWorkflowStart = null;
        pending.Answer.TrySetResult((confirmed, pending.Task, pending.SmallFix));
        NoteAiAnswered(pending.Id, EditorRequestKinds.Workflow, confirmed ? EditorRequestOutcomes.Approved
            : pending.Reason is "cancelled" ? EditorRequestOutcomes.Cancelled : EditorRequestOutcomes.Denied, pending.Reason);
    }

    /** request.added for a new waiting start. */
    private void AnnounceWorkflowStart(PendingWorkflowStart pending)
    {
        EmitUi(EditorUiEvents.RequestAdded, AiWorkflowDto(pending));
        EmitUiSoon(EditorUiEvents.StateChanged);
    }

    /** Roles a start runs, as the confirm dialog lists them. */
    private static HarnessRole[] WorkflowRoles(bool smallFix) => smallFix
        ? [HarnessRole.SmallFix, HarnessRole.SubagentReviewer, HarnessRole.Reviewer]
        : [HarnessRole.Planner, HarnessRole.Executor, HarnessRole.SubagentReviewer, HarnessRole.Reviewer, HarnessRole.SmallFix];

    /** The UI channel record of a waiting start: the routed ladders and the validation problems, as the dialog shows them. */
    private EditorRequestDto AiWorkflowDto(PendingWorkflowStart pending)
    {
        var team = WorkflowSettings();
        var problems = HarnessSettingsValidator.Validate(team).ToList();
        var routes = new List<EditorRoutedRoleDto>();
        foreach (var role in WorkflowRoles(pending.SmallFix))
        {
            var ladder = team.Route(role).ModelIds.Select(model => team.Models.FirstOrDefault(item => item.Id == model)).OfType<HarnessModel>()
                .Select(model => model.DisplayName + (model.Enabled ? "" : " (disabled)")).ToArray();
            bool optional = role is HarnessRole.SubagentReviewer || (role == HarnessRole.SmallFix && !pending.SmallFix);
            routes.Add(new EditorRoutedRoleDto(RoleLabel(role), ladder, !optional));
        }
        var expires = pending.Created + TimeSpan.FromDays(1);
        return new EditorRequestDto(pending.Id, EditorRequestKinds.Workflow, "start_model_workflow",
            pending.FromMcp ? "An MCP client asks to start your configured model team." : "Start a model workflow from the Workflows panel.",
            AiSessionRef(pending.Session), pending.Session?.IsWorker == true, pending.Created, expires,
            new EditorWorkflowDetailDto(pending.Task, pending.SmallFix, pending.FromMcp, !pending.FromMcp, routes, problems));
    }

    /**
     * Confirm or cancel a waiting start from Solforge AI (UI thread). A confirm may carry SmallFix and, for a start the user began
     * in the editor, an edited task; it is refused (422) for the reasons the dialog's Confirm button is disabled.
     */
    private EditorUiResult AnswerWorkflowFromApp(PendingWorkflowStart pending, bool approve, EditorApproveBody? body)
    {
        if (!approve)
        {
            pending.Reason = "app";
            FinishWorkflowStart(pending, false);
            return EditorUiResult.Ok(new EditorAnswerResultDto(pending.Id, EditorRequestOutcomes.Denied));
        }
        string task = !pending.FromMcp && body?.Task is { } edited ? edited : pending.Task;
        bool small = body?.SmallFix ?? pending.SmallFix;
        if (task.Trim().Length is 0 or > 32768) return EditorUiResult.Fail(422, "task", "The task must be 1 to 32768 characters.");
        if (HarnessSettingsValidator.Validate(WorkflowSettings()) is { Count: > 0 } errors)
            return EditorUiResult.Fail(422, "settings", "The model settings are invalid: " + string.Join(" ", errors));
        pending.Task = task;
        pending.SmallFix = small;
        pending.Reason = "app";
        FinishWorkflowStart(pending, true);
        return EditorUiResult.Ok(new EditorAnswerResultDto(pending.Id, EditorRequestOutcomes.Approved));
    }

    /** True when an atlas font was built (the monospace face can be missing in a minimal build). */
    private static unsafe bool FontLoaded(ImFontPtr font) => font.NativePtr != null;

    /** "SubagentReviewer" -> "Subagent reviewer". */
    private static string RoleLabel(HarnessRole role) => role switch
    {
        HarnessRole.SmallFix => "SmallFix",
        HarnessRole.SubagentPlanner => "Subagent planner",
        HarnessRole.SubagentExecutor => "Subagent executor",
        HarnessRole.SubagentReviewer => "Subagent reviewer",
        _ => role.ToString()
    };

    /**
     * Hook H7: the Workflows panel: the run picker, a 1 s refresh of the journal, the stage list (role, selected and attempted
     * models, verdict), expandable outputs in the monospace font with smooth scrolling, Start and Stop.
     */
    public void DrawWorkflowStatusPanel()
    {
        ImGui.PushID("solforge-workflows");
        var service = WorkflowService();
        if (service is null)
        {
            ImGui.TextColored(SolTheme.Muted, workflowError ?? "Workflows are unavailable.");
            ImGui.PopID();
            return;
        }
        string[] ids;
        try { ids = service.RecentIds(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ids = []; }
        selectedWorkflow ??= ids.FirstOrDefault();
        UiAuto.PushBlocked();
        try
        {
            ImGui.BeginDisabled(pendingWorkflowStart is not null);
            if (SolTheme.PrimaryButton("Start workflow...##workflow-start")) BeginWorkflowStart();
            ImGui.EndDisabled();
        }
        finally { UiAuto.PopBlocked(); }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Min(260 * scale, ImGui.GetContentRegionAvail().X));
        if (ImGui.BeginCombo("##workflow-run", selectedWorkflow is null ? "No runs yet" : "Run " + selectedWorkflow[..8]))
        {
            foreach (string id in ids)
                if (ImGui.Selectable("Run " + id[..8], id == selectedWorkflow)) { selectedWorkflow = id; workflowView = default; }
            ImGui.EndCombo();
        }
        if (workflowError is { Length: > 0 } problem) ImGui.TextColored(SolTheme.Bad, problem);
        if (selectedWorkflow is not { } selected || WorkflowState(selected) is not { } state)
        {
            ImGui.TextColored(SolTheme.Muted, "No workflow selected. Start one here or with start_model_workflow from Solforge AI.");
            ImGui.PopID();
            return;
        }
        string status = state["Status"]?.ToString() ?? "";
        bool active = status is "Queued" or "Running" or "Cancelling";
        ImGui.SameLine();
        ImGui.BeginDisabled(!active || status == "Cancelling");
        if (ImGui.Button("Stop##workflow-stop")) { service.Cancel(selected); workflowView = default; }
        ImGui.EndDisabled();
        ImGui.TextColored(active ? SolTheme.Warn : status is "AwaitingAcceptance" ? SolTheme.Ok : status is "Failed" ? SolTheme.Bad : SolTheme.Text, status);
        ImGui.SameLine();
        ImGui.TextColored(SolTheme.Muted, (state["SmallFix"]?.GetValue<bool>() == true ? "SmallFix · " : "") + "updated " + Time(state["UpdatedUtc"]));
        if (state["Message"]?.ToString() is { Length: > 0 } message) { ImGui.PushStyleColor(ImGuiCol.Text, SolTheme.Muted); ImGui.TextWrapped(message); ImGui.PopStyleColor(); }
        if (state["PersistenceWarning"]?.ToString() is { Length: > 0 } warning) ImGui.TextColored(SolTheme.Warn, warning);
        if (ImGui.CollapsingHeader("Task##workflow-task-header")) ImGui.TextWrapped(state["Input"]?.ToString() ?? "");

        if (ImGui.BeginTable("workflow-stages", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Stage");
            ImGui.TableSetupColumn("Role");
            ImGui.TableSetupColumn("Model");
            ImGui.TableSetupColumn("Tried");
            ImGui.TableSetupColumn("Status / verdict");
            ImGui.TableHeadersRow();
            int index = 0;
            foreach (var step in state["Steps"] as JsonArray ?? [])
            {
                ImGui.PushID(index++);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                bool expanded = ImGui.TreeNodeEx(step?["Phase"]?.ToString() ?? "stage", ImGuiTreeNodeFlags.SpanAllColumns);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(step?["Role"]?.ToString() ?? "");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(step?["Model"]?.ToString() ?? "-");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(string.Join(", ", (step?["AttemptedModels"] as JsonArray ?? []).Select(model => model?.ToString())));
                ImGui.TableNextColumn();
                string stepStatus = step?["Status"]?.ToString() ?? "";
                ImGui.TextColored(stepStatus is "Completed" ? SolTheme.Ok : stepStatus is "Running" ? SolTheme.Warn : stepStatus is "Failed" or "InvalidVerdict" ? SolTheme.Bad : SolTheme.Text,
                    stepStatus + (step?["Verdict"]?.ToString() is { Length: > 0 } verdict ? " · " + verdict : ""));
                if (expanded)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    if (step?["Message"]?.ToString() is { Length: > 0 } note) ImGui.TextColored(SolTheme.Muted, note);
                    string output = step?["Output"]?.ToString() ?? "(no output)";
                    if (ImGui.BeginChild("output", new Vector2(ImGui.GetContentRegionAvail().X, 200 * scale), ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.HorizontalScrollbar))
                    {
                        UiMotion.SmoothScroll();
                        bool mono = gui is not null && FontLoaded(gui.MonospaceFont);
                        if (mono) ImGui.PushFont(gui!.MonospaceFont);
                        ImGui.TextUnformatted(output);
                        if (mono) ImGui.PopFont();
                    }
                    ImGui.EndChild();
                    if (step?["OutputTruncated"]?.GetValue<bool>() == true) ImGui.TextColored(SolTheme.Warn, "Output truncated at the retained-output limit.");
                    ImGui.TreePop();
                }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        if (ImGui.CollapsingHeader("Journal##workflow-journal"))
        {
            if (ImGui.BeginChild("journal", new Vector2(0, 180 * scale), ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollWithMouse))
            {
                UiMotion.SmoothScroll();
                foreach (var entry in (state["Journal"] as JsonArray ?? []).Reverse())
                    ImGui.TextWrapped($"{Time(entry?["AtUtc"])}  {entry?["Event"]}  {entry?["Role"]} {entry?["Model"]}  {entry?["Message"]}".ReplaceLineEndings(" "));
            }
            ImGui.EndChild();
        }
        ImGui.PopID();

        static string Time(JsonNode? node) => node is not null && DateTimeOffset.TryParse(node.ToString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var at) ? at.ToLocalTime().ToString("HH:mm:ss") : "-";
    }

    /** ISolforgeHost.Workflows for the MCP tools (start/get/cancel_model_workflow). */
    private sealed partial class AiHost
    {
        public IModelWorkflows? Workflows => Owner.modelWorkflowBridge ??= new ModelWorkflowBridge(Owner);
    }

    /** The MCP side of the workflows: every start waits for the native confirm on the UI thread. */
    private sealed class ModelWorkflowBridge(StudioForm form) : IModelWorkflows
    {
        public async Task<JsonObject> StartAsync(string task, bool smallFix, CancellationToken cancel)
        {
            var session = McpSessionRegistry.Current;
            var pending = await form.OnUi(() =>
            {
                if (form.aiPaused) return null;
                if (form.pendingWorkflowStart is not null) return null;
                if (form.WorkflowService() is null) return null;
                var created = new PendingWorkflowStart(task, smallFix, fromMcp: true, session);
                form.pendingWorkflowStart = created;
                form.AnnounceWorkflowStart(created);
                return created;
            }).ConfigureAwait(false);
            if (pending is null)
                return new JsonObject { ["error"] = form.aiPaused ? "Editor context sharing is paused by the user."
                    : form.workflowError ?? "A workflow start is already waiting for the user's confirmation in Solforge." };
            using var registration = cancel.Register(() =>
            {
                // The MCP call ended: the dialog closes (UI thread) and Solforge AI drops the request.
                pending.Reason = "cancelled";
                _ = form.OnUi(() => { form.FinishWorkflowStart(pending, false); return true; });
                pending.Answer.TrySetResult((false, "", false));
            });
            var (confirmed, confirmedTask, small) = await pending.Answer.Task.ConfigureAwait(false);
            if (!confirmed) return new JsonObject { ["error"] = "The user declined the workflow start in Solforge." };
            try
            {
                var service = await form.OnUi(form.WorkflowService).ConfigureAwait(false);
                if (service is null) return new JsonObject { ["error"] = form.workflowError ?? "Workflows are unavailable." };
                string id = await service.StartAsync(confirmedTask, small, cancel).ConfigureAwait(false);
                form.selectedWorkflow = id;
                return new JsonObject
                {
                    ["run_id"] = id, ["status"] = "Started",
                    ["note"] = "Read get_model_workflow for actual stage progress and results; edits still need the user's review."
                };
            }
            catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or JsonException)
            {
                return new JsonObject { ["error"] = "The workflow could not start: " + error.Message };
            }
        }

        public JsonObject Get(string runId)
        {
            try
            {
                if (form.modelWorkflows is not { } service) return new JsonObject { ["error"] = "No workflow service for the open project yet." };
                return JsonNode.Parse(service.Get(runId).GetRawText()) as JsonObject ?? new JsonObject { ["error"] = "Invalid workflow history." };
            }
            catch (Exception error) when (error is ArgumentException or IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                return new JsonObject { ["error"] = error.Message };
            }
        }

        public bool Cancel(string runId) => form.modelWorkflows?.Cancel(runId) ?? false;

        public IReadOnlyList<string> RecentIds()
        {
            try { return form.modelWorkflows?.RecentIds() ?? (form.workspace.Project?.DirectoryPath is { } project ? [.. PersistedWorkflowIds(project)] : []); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
        }
    }
}
