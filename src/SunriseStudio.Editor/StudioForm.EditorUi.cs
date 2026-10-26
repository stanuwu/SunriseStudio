/**
 * File: the editor side of the Solforge AI UI channel (SOLFORGE-AI-APP-SPEC §6): AiHost implements IEditorUiHost
 * (Solforge.Mcp/EditorUiApi.cs), so the paired Solforge AI app can list and answer AI requests, read the AI log, sessions and
 * AI edits, and use the context features (publish, pins, folders, Ask, screenshot). Every read and change runs on the UI
 * thread through OnUi. Approvals stay a user decision: answering from the app is the same as clicking in the editor, and
 * nothing here auto-approves (AutoApproveRequests in ApproveAsync is unchanged). Also the pairing confirmation modal
 * (blocked for MCP UI automation) and AiAppAttached, which decides when the AI panel shows its fallback request cards.
 */
using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using Solforge.AiLauncher;
using Solforge.Mcp;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    /** A pairing waiting for the user's Allow or Deny. */
    private sealed class PendingPair(int pid, string image, bool recognized)
    {
        public int Pid { get; } = pid;
        public string Image { get; } = image;
        /** False for a "Solforge AI.exe" that is not the configured or discovered build. */
        public bool Recognized { get; } = recognized;
        public bool Opened;
        public TaskCompletionSource<bool> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private PendingPair? pendingPair;

    /** True while a paired Solforge AI holds the event stream or polled in the last 5 s; otherwise the panel shows the fallback. */
    private bool AiAppAttached => aiService?.EditorUi?.AppAttached == true;

    /** The UI channel record of an action request. */
    private static EditorRequestDto AiRequestDto(AiRequest request) =>
        new(request.Id, EditorRequestKinds.Action, request.Tool, request.Summary, AiSessionRef(request.Session), request.Worker,
            request.Created, request.Expires, null);

    /** Pending requests of every kind: actions, reviewed edits and the waiting workflow start (UI thread). */
    private List<EditorRequestDto> AiPendingRequests()
    {
        var list = new List<EditorRequestDto>();
        lock (aiRequests) list.AddRange(aiRequests.Select(AiRequestDto));
        lock (diffReviews) list.AddRange(diffReviews.Where(item => !item.Answer.Task.IsCompleted).Select(AiDiffDto));
        if (pendingWorkflowStart is { } start) list.Add(AiWorkflowDto(start));
        return list;
    }

    private int AiPendingCount()
    {
        int count;
        lock (aiRequests) count = aiRequests.Count;
        lock (diffReviews) count += diffReviews.Count(item => !item.Answer.Task.IsCompleted);
        return count + (pendingWorkflowStart is null ? 0 : 1);
    }

    private bool AiAnsweredRecently(string id)
    {
        lock (aiAnsweredIds) return aiAnsweredIds.Contains(id);
    }

    /** Approve or deny any pending request by id from Solforge AI (UI thread). */
    private EditorUiResult AnswerFromApp(string id, bool approve, EditorApproveBody? body)
    {
        AiRequest? action;
        lock (aiRequests) action = aiRequests.FirstOrDefault(item => item.Id == id);
        if (action is not null)
            return Answer(action, approve, "app")
                ? EditorUiResult.Ok(new EditorAnswerResultDto(id, approve ? EditorRequestOutcomes.Approved : EditorRequestOutcomes.Denied))
                : EditorUiResult.Fail(409, "answered", "The request was answered already.");
        PendingDiff? diff;
        lock (diffReviews) diff = diffReviews.FirstOrDefault(item => item.Id == id);
        if (diff is not null) return AnswerDiffFromApp(diff, approve);
        if (pendingWorkflowStart is { } start && start.Id == id) return AnswerWorkflowFromApp(start, approve, body);
        return AiAnsweredRecently(id) ? EditorUiResult.Fail(409, "answered", "The request was answered already.")
            : EditorUiResult.Fail(404, "gone", "No such request.");
    }

    /** GET /editor/context (UI thread). */
    private EditorContextDto AiContextDto()
    {
        var pinned = aiPinned.Select((pin, index) => new EditorPinDto(index, pin.Id, pin.Label)).ToArray();
        var recent = aiAttention.Recent(10).Select(item => new EditorAttentionDto(item.Time, item.Kind.ToString().ToLowerInvariant(), item.Label)).ToArray();
        bool canScreenshot = workspace.Project is not null && viewport is { IsRendering: true } && !CredentialDocumentShown();
        return new EditorContextDto(AiContextChip(), CanAskAi, canScreenshot, aiHost?.ContextJson ?? "{}", pinned, recent, !aiPaused);
    }

    /** GET /editor/folders (UI thread). */
    private EditorFoldersDto AiFoldersDto()
    {
        string? project = workspace.Project?.DirectoryPath;
        var hidden = settings.AiHiddenFolders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = project is null ? [] : AiFoundFolders(project).Select(folder => new EditorFoundFolderDto(folder, !hidden.Contains(folder))).ToArray();
        var added = settings.AiFolders.Select(folder => new EditorAddedFolderDto(folder, Directory.Exists(folder))).ToArray();
        return new EditorFoldersDto(project, found, added);
    }

    /** POST /editor/folders (UI thread): the same edits the Folders popup makes. */
    private EditorUiResult AiFolderOp(string op, string path)
    {
        string trimmed = path.Trim().Trim('"');
        switch (op)
        {
            case "add":
                if (!Directory.Exists(trimmed)) return EditorUiResult.Fail(422, "missing", "That folder does not exist.");
                string full = Path.GetFullPath(trimmed);
                if (settings.AiFolders.Contains(full, StringComparer.OrdinalIgnoreCase)) return EditorUiResult.Fail(409, "duplicate", "That folder is already added.");
                settings.AiFolders.Add(full);
                break;
            case "remove":
                if (settings.AiFolders.RemoveAll(item => string.Equals(item, trimmed, StringComparison.OrdinalIgnoreCase)) == 0)
                    return EditorUiResult.Fail(404, "gone", "That folder is not in the added list.");
                break;
            case "hide" or "show":
                if (workspace.Project?.DirectoryPath is not { } project || !AiFoundFolders(project).Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                    return EditorUiResult.Fail(404, "gone", "That folder is not a found folder of the open project.");
                settings.AiHiddenFolders.RemoveAll(item => string.Equals(item, trimmed, StringComparison.OrdinalIgnoreCase));
                if (op == "hide") settings.AiHiddenFolders.Add(trimmed);
                break;
            default:
                return EditorUiResult.Fail(400, "op", "Unknown folder operation.");
        }
        SaveSettings();
        EmitUiSoon(EditorUiEvents.ContextChanged);
        return EditorUiResult.Ok(AiFoldersDto());
    }

    /** Asks the user whether a Solforge AI the editor did not start may connect (UI thread starts it; the modal answers). */
    private Task<bool> BeginPairPrompt(int pid, string image, bool recognized, CancellationToken cancel)
    {
        var pending = new PendingPair(pid, image, recognized);
        _ = OnUi(() =>
        {
            pendingPair?.Answer.TrySetResult(false);
            pendingPair = pending;
            return true;
        });
        cancel.Register(() => pending.Answer.TrySetResult(false));
        return pending.Answer.Task;
    }

    /**
     * The pairing modal, drawn every frame (from DrawApprovalToast). Blocked for MCP UI automation, so an agent can never pair
     * an app with this editor; closing it denies.
     */
    private void DrawEditorPairModal()
    {
        const string id = "Connect Solforge AI###solforge-pair";
        if (pendingPair is not { } pending) { CloseOrphanModal(id); return; }
        if (pending.Answer.Task.IsCompleted) { pendingPair = null; CloseOrphanModal(id); return; }
        if (!pending.Opened) { ImGui.OpenPopup(id); pending.Opened = true; }
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(.5f, .5f));
        ImGui.SetNextWindowSize(new Vector2(520, 0) * scale, ImGuiCond.Appearing);
        bool open = true;
        if (!ImGui.BeginPopupModal(id, ref open, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (!open) Decide(false);
            return;
        }
        UiAuto.PushBlocked();
        try
        {
            ImGui.TextWrapped($"Solforge AI (pid {pending.Pid}) wants to connect to this editor.");
            ImGui.TextColored(SolTheme.Muted, pending.Image);
            if (!pending.Recognized)
                ImGui.TextColored(SolTheme.Warn, "This is not the Solforge AI build this editor knows (Settings path or the newest build). "
                    + "Deny unless you started Solforge AI from exactly this folder yourself.");
            ImGui.TextWrapped("Allowing it lets that app show and answer this editor's AI requests, read the AI log and sessions, and use the context features. Allow only if you started it.");
            ImGui.Spacing();
            if (SolTheme.PrimaryButton("Allow##pair-allow")) { Decide(true); ImGui.CloseCurrentPopup(); }
            ImGui.SameLine();
            if (ImGui.Button("Deny##pair-deny") || ImGui.IsKeyPressed(ImGuiKey.Escape)) { Decide(false); ImGui.CloseCurrentPopup(); }
        }
        finally { UiAuto.PopBlocked(); }
        ImGui.EndPopup();

        void Decide(bool allow)
        {
            if (!ReferenceEquals(pendingPair, pending)) return;
            pendingPair = null;
            pending.Answer.TrySetResult(allow);
            AppendAiLog($"Solforge AI (pid {pending.Pid}): connection {(allow ? "allowed" : "denied")}.");
        }
    }

    /** The editor UI channel members of the tool host (Solforge.Mcp.IEditorUiHost). */
    private sealed partial class AiHost : IEditorUiHost
    {
        private static readonly TimeSpan UiWait = TimeSpan.FromSeconds(10);

        public EditorProjectDto? Project => AiProjectDto(Owner.workspace.Project?.DirectoryPath);

        public bool LaunchedApp(int pid) => Owner.solforgeAi is { } host && host.IsRunning && host.ProcessId == pid;

        public bool IsSolforgeAiImage(string imagePath)
        {
            var settings = Owner.settings;
            string? builds = string.IsNullOrWhiteSpace(settings.SolforgeBuildsRoot) ? null : settings.SolforgeBuildsRoot;
            return AppDiscovery.IsAppImage(imagePath, settings.SolforgeAiPath, AppContext.BaseDirectory, builds, Owner.solforgeAi?.Layout?.AppExecutable);
        }

        public Task<bool> ConfirmPairAsync(int pid, string imagePath, bool recognized, CancellationToken cancel) =>
            Owner.BeginPairPrompt(pid, imagePath, recognized, cancel);

        public Task<EditorStateDto> GetStateAsync(CancellationToken cancel) => Owner.OnUi(() =>
        {
            var form = Owner;
            int sessions = form.aiService?.Sessions?.Sessions().Count ?? 0;
            return new EditorStateDto(Environment.ProcessId, typeof(StudioForm).Assembly.GetName().Version?.ToString(3) ?? "0",
                AiProjectDto(form.workspace.Project?.DirectoryPath), form.workspace.World?.Map.Name, form.aiPaused, form.settings.AutoApproveRequests,
                new EditorMcpDto(form.aiService?.Port ?? 0, sessions, form.aiService?.Requests ?? 0),
                !string.IsNullOrWhiteSpace(form.settings.CredentialFile), form.AiPendingCount(), 0);
        }).WaitAsync(UiWait, cancel);

        public async Task<IReadOnlyList<EditorRequestDto>> GetRequestsAsync(CancellationToken cancel) =>
            await Owner.OnUi(Owner.AiPendingRequests).WaitAsync(UiWait, cancel).ConfigureAwait(false);

        public Task<EditorUiResult> AnswerAsync(string id, bool approve, EditorApproveBody? body, CancellationToken cancel) =>
            Owner.OnUi(() => Owner.AnswerFromApp(id, approve, body)).WaitAsync(UiWait, cancel);

        public Task<EditorLogDto> GetLogAsync(long after, CancellationToken cancel)
        {
            var form = Owner;
            lock (form.aiRequestLog)
            {
                var entries = form.aiRequestLog.Where(entry => entry.Seq > after).Select(AiLogEntryDto).ToArray();
                var lines = form.aiLogLines.Where(line => line.Seq > after).Select(line => new EditorLogLineDto(line.Seq, line.Time, line.Text)).ToArray();
                return Task.FromResult(new EditorLogDto(entries, lines, form.aiLogSeq));
            }
        }

        public Task<object> GetSessionsAsync(CancellationToken cancel)
        {
            if (Owner.aiService?.Sessions is not { } registry) return Task.FromResult<object>(new JsonObject { ["sessions"] = new JsonArray() });
            var json = registry.ToJson(null, this);
            json.Remove("you");
            return Task.FromResult<object>(json);
        }

        public async Task<IReadOnlyList<EditorEditDto>> GetEditsAsync(CancellationToken cancel) => await Owner.OnUi(() =>
            (IReadOnlyList<EditorEditDto>)Owner.aiEdits.Select(edit => new EditorEditDto(edit.Id, edit.Shown, edit.Time, edit.Previous is null)).Reverse().ToArray())
            .WaitAsync(UiWait, cancel).ConfigureAwait(false);

        public Task<EditorUiResult> UndoEditAsync(string id, CancellationToken cancel) => Owner.OnUi(() =>
        {
            int index = Owner.aiEdits.FindIndex(edit => edit.Id == id);
            if (index < 0) return EditorUiResult.Fail(404, "gone", "No such AI edit (it may have been undone already).");
            try { Owner.UndoAiEdit(index); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { return EditorUiResult.Fail(409, "io", "The edit could not be undone: " + error.Message); }
            return EditorUiResult.Ok(new { id });
        }).WaitAsync(UiWait, cancel);

        public Task<EditorContextDto> GetContextAsync(CancellationToken cancel) => Owner.OnUi(Owner.AiContextDto).WaitAsync(UiWait, cancel);

        public Task<EditorUiResult> SetPublishAsync(bool on, CancellationToken cancel) => Owner.OnUi(() =>
        {
            Owner.SetAiPublish(on);
            return EditorUiResult.Ok(Owner.AiContextDto());
        }).WaitAsync(UiWait, cancel);

        public Task<EditorUiResult> PinAsync(string target, CancellationToken cancel) => Owner.OnUi(() =>
            Owner.PinAiObject(target == "selected") ? EditorUiResult.Ok(Owner.AiContextDto())
                : EditorUiResult.Fail(409, "nothing", target == "selected" ? "Nothing is selected in the editor." : "Nothing is hovered or selected in the editor."))
            .WaitAsync(UiWait, cancel);

        public Task<EditorUiResult> UnpinAsync(int index, CancellationToken cancel) => Owner.OnUi(() =>
        {
            if (index < 0 || index >= Owner.aiPinned.Count) return EditorUiResult.Fail(404, "gone", "No pin at that position.");
            Owner.aiPinned.RemoveAt(index);
            Owner.EmitUiSoon(EditorUiEvents.ContextChanged);
            return EditorUiResult.Ok(Owner.AiContextDto());
        }).WaitAsync(UiWait, cancel);

        public Task<EditorFoldersDto> GetFoldersAsync(CancellationToken cancel) => Owner.OnUi(Owner.AiFoldersDto).WaitAsync(UiWait, cancel);

        public Task<EditorUiResult> FolderOpAsync(string op, string path, CancellationToken cancel) =>
            Owner.OnUi(() => Owner.AiFolderOp(op, path)).WaitAsync(UiWait, cancel);

        public Task<EditorUiResult> AskAsync(CancellationToken cancel) => Owner.OnUi(() =>
        {
            if (!Owner.CanAskAi) return EditorUiResult.Fail(409, "nothing", Owner.aiPaused ? "Context sharing is paused." : "Nothing is selected or open in the editor.");
            var (text, subject) = Owner.AiAskText();
            Owner.aiAttention.Record(AttentionKind.Open, "ask", "Asked Solforge AI about " + subject);
            return EditorUiResult.Ok(new EditorAskDto(text));
        }).WaitAsync(UiWait, cancel);

        public async Task<EditorUiResult> ScreenshotAsync(CancellationToken cancel)
        {
            var saved = await Owner.OnUi(() =>
            {
                var form = Owner;
                if (form.workspace.Project is null || form.viewport is not { IsRendering: true }) return null;
                // Hook H9 (c): nothing is captured while the credential file is on screen.
                if (form.CredentialDocumentShown()) return null;
                form.aiScreenshotRequest = (null, true, true);
                form.aiScreenshotCopy = false;
                form.aiScreenshot = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
                return form.aiScreenshotSaved = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            }).WaitAsync(UiWait, cancel).ConfigureAwait(false);
            if (saved is null) return EditorUiResult.Fail(409, "unavailable", "No screenshot: open a project with the 3D viewport showing (and the credential file closed).");
            string? relative;
            try { relative = await saved.Task.WaitAsync(UiWait, cancel).ConfigureAwait(false); }
            catch (TimeoutException) { relative = null; }
            return relative is null ? EditorUiResult.Fail(503, "capture", "The editor did not draw a frame in time (busy or minimised); try again.")
                : EditorUiResult.Ok(new EditorScreenshotDto(relative));
        }
    }
}
