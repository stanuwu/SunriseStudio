/**
 * File: Reviewed before/after edits (UI-UE5 §5.5, §5.6). apply_project_edit asks ISolforgeHost.ApproveEditAsync, which this
 * file implements on AiHost: the edit is queued (QueueDiffReview) and DrawDiffReviewModal (hook H10) shows a side-by-side
 * line diff (LCS; removed lines on Bad at 18 %, added lines on Ok at 18 %, one table so both sides scroll together) with a
 * 3-minute countdown and Approve/Reject. A file that changed on disk, paused context sharing or a disconnected client
 * cancels the review; it is never auto-approved and its buttons are blocked for MCP UI automation. Approval lets the tool
 * write through the editor's action path, which records the edit under AI edits with Undo.
 * Also here: get_editor_screenshot (the uncropped back buffer) and the credential-document check for captures.
 */
using System.Numerics;
using ImGuiNET;
using Solforge.Harness.Credentials;
using Solforge.Mcp;
using SunriseStudio.Rendering;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    /** One side-by-side row: left (before) and right (after) text, each null when that side has no line. */
    private readonly record struct DiffRow(string? Left, string? Right, bool Removed, bool Added);

    /**
     * An edit waiting for review (kind "edit" on the UI channel). Rows are computed off the UI thread. Reason says who or what
     * answered it ("user", "app", "paused", "stale", "timeout", "stopped", "cancelled").
     */
    private sealed class PendingDiff(string summary, string path, string? before, string after, DateTime expires, CancellationToken cancel)
    {
        public string Id { get; } = NewAiId();
        public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
        public McpSession? Session { get; } = McpSessionRegistry.Current;
        public string Reason { get; set; } = "user";
        public string Summary { get; } = summary;
        public string Path { get; } = path;
        public string? Before { get; } = before;
        public string After { get; } = after;
        public DateTime Expires { get; } = expires;
        public CancellationToken Cancel { get; } = cancel;
        public volatile IReadOnlyList<DiffRow>? Rows;
        public int Removed, Added;
        public bool Opened;
        public TaskCompletionSource<bool> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly List<PendingDiff> diffReviews = [];
    private TaskCompletionSource<byte[]?>? editorScreenshot;
    private bool editorScreenshotHooked;

    /**
     * Queues a reviewed edit and waits for the user's answer (false on Reject, expiry, a stale file, paused sharing or a
     * disconnect). At most eight wait at once.
     */
    private async Task<bool> QueueDiffReview(string summary, string path, string? before, string after, TimeSpan expiresIn, CancellationToken cancel)
    {
        var pending = new PendingDiff(summary, path, before, after, DateTime.UtcNow + expiresIn, cancel);
        lock (diffReviews)
        {
            if (diffReviews.Count >= 8) return false;
            diffReviews.Add(pending);
        }
        EmitUi(EditorUiEvents.RequestAdded, AiDiffDto(pending));
        EmitUiSoon(EditorUiEvents.StateChanged);
        _ = Task.Run(() =>
        {
            var rows = DiffRows(before ?? "", after);
            pending.Removed = rows.Count(row => row.Removed);
            pending.Added = rows.Count(row => row.Added);
            pending.Rows = rows;
            // Solforge AI re-reads the request list once the rows exist.
            EmitUiSoon(EditorUiEvents.StateChanged);
        });
        string outcome = EditorRequestOutcomes.Denied;
        try
        {
            using var registration = cancel.Register(() => { pending.Reason = "cancelled"; pending.Answer.TrySetResult(false); });
            bool approved = await pending.Answer.Task.WaitAsync(expiresIn + TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            outcome = approved ? EditorRequestOutcomes.Approved : pending.Reason is "timeout" ? EditorRequestOutcomes.Expired
                : pending.Reason is "cancelled" or "stale" or "paused" ? EditorRequestOutcomes.Cancelled : EditorRequestOutcomes.Denied;
            RecordAiRequest("apply_project_edit", summary + (pending.Reason is "user" or "app" ? "" : $" ({pending.Reason})"),
                approved ? AiRequestOutcome.Approved : AiRequestOutcome.Denied, pending.Session?.Label);
            return approved;
        }
        catch (TimeoutException)
        {
            pending.Reason = "timeout";
            outcome = EditorRequestOutcomes.Expired;
            RecordAiRequest("apply_project_edit", summary + " (review expired)", AiRequestOutcome.Denied, pending.Session?.Label);
            return false;
        }
        finally
        {
            lock (diffReviews) diffReviews.Remove(pending);
            NoteAiAnswered(pending.Id, EditorRequestKinds.Edit, outcome, pending.Reason);
        }
    }

    /** Most diff rows sent to Solforge AI per request (the editor's modal shows all). */
    private const int AiDiffRowLimit = 4000;

    /** The UI channel record of a reviewed edit. */
    private static EditorRequestDto AiDiffDto(PendingDiff pending)
    {
        var rows = pending.Rows;
        var shown = rows?.Take(AiDiffRowLimit).Select(row => new EditorDiffRowDto(row.Left, row.Right, row.Removed, row.Added)).ToArray();
        return new EditorRequestDto(pending.Id, EditorRequestKinds.Edit, "apply_project_edit", pending.Summary, AiSessionRef(pending.Session),
            pending.Session?.IsWorker == true, pending.Created, new DateTimeOffset(pending.Expires, TimeSpan.Zero),
            new EditorEditDetailDto(pending.Path, pending.Before is null, pending.Removed, pending.Added, shown, rows is not null && rows.Count > AiDiffRowLimit));
    }

    /**
     * Approve or deny a reviewed edit from Solforge AI (UI thread). Approve needs the finished comparison and re-checks the file
     * and the Publish context switch, exactly as the modal does; a stale or paused review is cancelled instead.
     */
    private EditorUiResult AnswerDiffFromApp(PendingDiff pending, bool approve)
    {
        if (pending.Answer.Task.IsCompleted) return EditorUiResult.Fail(409, "answered", "The edit was answered already.");
        if (approve)
        {
            if (pending.Rows is null) return EditorUiResult.Fail(409, "comparing", "The editor is still comparing the files; try again.");
            if (aiPaused)
            {
                pending.Reason = "paused";
                pending.Answer.TrySetResult(false);
                return EditorUiResult.Fail(409, "paused", "Context sharing is paused, so the review was cancelled.");
            }
            if (!string.Equals(DiffCurrent(pending), pending.Before, StringComparison.Ordinal))
            {
                pending.Reason = "stale";
                pending.Answer.TrySetResult(false);
                return EditorUiResult.Fail(409, "stale", "The file changed since the edit was proposed, so the review was cancelled.");
            }
        }
        pending.Reason = "app";
        return pending.Answer.TrySetResult(approve)
            ? EditorUiResult.Ok(new EditorAnswerResultDto(pending.Id, approve ? EditorRequestOutcomes.Approved : EditorRequestOutcomes.Denied))
            : EditorUiResult.Fail(409, "answered", "The edit was answered already.");
    }

    /** The current text of the reviewed file (null when it does not exist or cannot be read). */
    private string? DiffCurrent(PendingDiff pending)
    {
        if (ProjectFiles.EditablePath(workspace.Project?.DirectoryPath, pending.Path, out _) is not { } full) return null;
        try { return File.Exists(full) ? File.ReadAllText(full) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private double diffCheckedAt;

    /** Hook H10: the review modal, drawn every frame after the layout. */
    public unsafe void DrawDiffReviewModal()
    {
        PendingDiff? pending;
        lock (diffReviews) pending = diffReviews.FirstOrDefault(item => !item.Answer.Task.IsCompleted);
        const string id = "Review AI edit###solforge-diff-review";
        // Answered elsewhere (Solforge AI), cancelled or gone: close a modal that is still open so it never blocks input.
        if (pending is null) { CloseOrphanModal(id); return; }
        // Stale, paused or expired: cancel without asking (checked twice a second; reading the file is not free).
        double now = ImGui.GetTime();
        if (aiPaused || DateTime.UtcNow >= pending.Expires)
        {
            pending.Reason = aiPaused ? "paused" : "timeout";
            pending.Answer.TrySetResult(false);
            CloseOrphanModal(id);
            return;
        }
        if (now - diffCheckedAt > .5)
        {
            diffCheckedAt = now;
            if (!string.Equals(DiffCurrent(pending), pending.Before, StringComparison.Ordinal))
            {
                pending.Reason = "stale";
                pending.Answer.TrySetResult(false);
                CloseOrphanModal(id);
                return;
            }
        }
        if (!pending.Opened) { ImGui.OpenPopup(id); pending.Opened = true; }
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.Appearing, new Vector2(.5f, .5f));
        ImGui.SetNextWindowSize(new Vector2(Math.Min(1100 * scale, viewport.Size.X * .9f), Math.Min(720 * scale, viewport.Size.Y * .85f)), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal(id, ImGuiWindowFlags.NoSavedSettings))
        {
            pending.Opened = false;
            return;
        }
        UiAuto.PushBlocked();
        try
        {
            var left = pending.Expires - DateTime.UtcNow;
            ImGui.TextColored(SolTheme.Warn, $"Solforge AI wants to edit {pending.Path}");
            ImGui.SameLine();
            ImGui.TextColored(SolTheme.Muted, $"  expires in {(int)left.TotalMinutes}:{left.Seconds:00}");
            ImGui.TextWrapped(pending.Summary);
            int waiting;
            lock (diffReviews) waiting = diffReviews.Count - 1;
            if (waiting > 0) ImGui.TextColored(SolTheme.Muted, $"{waiting} more edit request(s) waiting.");
            if (pending.Rows is not { } rows)
            {
                ImGui.TextColored(SolTheme.Muted, "Comparing...");
            }
            else
            {
                ImGui.TextColored(SolTheme.Muted, (pending.Before is null ? "New file. " : "") + $"{pending.Removed} line(s) removed, {pending.Added} added.");
                float footer = ImGui.GetFrameHeightWithSpacing() * 1.6f;
                var size = new Vector2(0, Math.Max(120 * scale, ImGui.GetContentRegionAvail().Y - footer));
                if (ImGui.BeginTable("diff", 2, ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame, size))
                {
                    ImGui.TableSetupScrollFreeze(0, 1);
                    ImGui.TableSetupColumn("Before");
                    ImGui.TableSetupColumn("After");
                    ImGui.TableHeadersRow();
                    bool mono = gui is not null && FontLoaded(gui.MonospaceFont);
                    if (mono) ImGui.PushFont(gui!.MonospaceFont);
                    uint removed = SolTheme.U32(SolTheme.Alpha(SolTheme.Bad, .18f)), added = SolTheme.U32(SolTheme.Alpha(SolTheme.Ok, .18f));
                    // Only the visible rows are drawn, so a long file stays smooth.
                    var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
                    clipper.Begin(rows.Count);
                    while (clipper.Step())
                        for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                        {
                            var row = rows[i];
                            ImGui.TableNextRow();
                            ImGui.TableSetColumnIndex(0);
                            if (row.Removed) ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, removed);
                            ImGui.TextUnformatted(row.Left ?? "");
                            ImGui.TableSetColumnIndex(1);
                            if (row.Added) ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, added);
                            ImGui.TextUnformatted(row.Right ?? "");
                        }
                    clipper.End(); clipper.Destroy();
                    if (mono) ImGui.PopFont();
                    ImGui.EndTable();
                }
            }
            ImGui.BeginDisabled(pending.Rows is null);
            if (SolTheme.PrimaryButton("Approve##diff-approve")) { pending.Answer.TrySetResult(true); ImGui.CloseCurrentPopup(); }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Reject##diff-reject")) { pending.Answer.TrySetResult(false); ImGui.CloseCurrentPopup(); }
            ImGui.SameLine();
            ImGui.TextColored(SolTheme.Muted, "Approving writes the file; AI edits in the Solforge AI panel can undo it.");
        }
        finally { UiAuto.PopBlocked(); }
        ImGui.EndPopup();
    }

    /** Closes a modal whose request was answered elsewhere: ImGui keeps an opened popup until it is closed from inside it. */
    private static void CloseOrphanModal(string id)
    {
        if (!ImGui.IsPopupOpen(id)) return;
        if (ImGui.BeginPopupModal(id, ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    /**
     * Side-by-side rows of a line diff: the common prefix and suffix are kept as is and the middle is aligned with a
     * longest common subsequence (bounded: past 4 million cells the middle shows as one removed and one added block).
     */
    private static List<DiffRow> DiffRows(string before, string after)
    {
        string[] a = before.Length == 0 ? [] : before.ReplaceLineEndings("\n").Split('\n');
        string[] b = after.Length == 0 ? [] : after.ReplaceLineEndings("\n").Split('\n');
        var rows = new List<DiffRow>(Math.Max(a.Length, b.Length) + 8);
        int start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start]) start++;
        for (int i = 0; i < start; i++) rows.Add(new(a[i], b[i], false, false));
        int endA = a.Length, endB = b.Length;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1]) { endA--; endB--; }
        int n = endA - start, m = endB - start;
        if ((long)n * m <= 4_000_000)
        {
            var table = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    table[i, j] = a[start + i] == b[start + j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
            int x = 0, y = 0;
            while (x < n || y < m)
            {
                if (x < n && y < m && a[start + x] == b[start + y]) { rows.Add(new(a[start + x], b[start + y], false, false)); x++; y++; }
                else if (y < m && (x == n || table[x, y + 1] >= table[x + 1, y])) { rows.Add(new(null, b[start + y], false, true)); y++; }
                else { rows.Add(new(a[start + x], null, true, false)); x++; }
            }
        }
        else
        {
            for (int i = 0; i < n; i++) rows.Add(new(a[start + i], null, true, false));
            for (int j = 0; j < m; j++) rows.Add(new(null, b[start + j], false, true));
        }
        for (int i = endA, j = endB; i < a.Length && j < b.Length; i++, j++) rows.Add(new(a[i], b[j], false, false));
        return rows;
    }

    /** True while the configured credential file is the mission script or node graph the Graph panel shows. */
    private bool CredentialDocumentShown() =>
        CredentialGuard.IsCredentialPath(missionGraph.ScriptPath) || CredentialGuard.IsCredentialPath(nodeGraph.FilePath);

    /** get_editor_screenshot: the next frame's whole back buffer as a PNG (no crop, no labels); null when no frame comes. */
    private async Task<byte[]?> CaptureEditorScreenshotAsync(CancellationToken cancel)
    {
        var pending = await OnUi(() =>
        {
            if (!editorScreenshotHooked) { FrameRendered += CaptureEditorScreenshot; editorScreenshotHooked = true; }
            return editorScreenshot ??= new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }).ConfigureAwait(false);
        try { return await pending.Task.WaitAsync(TimeSpan.FromSeconds(10), cancel).ConfigureAwait(false); }
        catch (TimeoutException) { return null; }
    }

    /** After a frame is drawn and before Present: the uncropped back buffer. */
    private void CaptureEditorScreenshot()
    {
        if (editorScreenshot is not { } pending || renderer is null) return;
        editorScreenshot = null;
        if (CredentialDocumentShown()) { pending.TrySetResult(null); return; }
        string temporary = Path.Combine(Path.GetTempPath(), $"solforge-editor-{Environment.ProcessId}.png");
        try
        {
            renderer.CapturePng(temporary);
            pending.TrySetResult(File.ReadAllBytes(temporary));
        }
        catch (Exception exception) when (exception is IOException or System.Runtime.InteropServices.ExternalException or ArgumentException or UnauthorizedAccessException)
        { pending.TrySetResult(null); }
        finally
        {
            try { File.Delete(temporary); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    /** The reviewed-edit, editor-capture and credential members of ISolforgeHost (UI-UE5 §5.5, §5.6, §5.3). */
    private sealed partial class AiHost
    {
        public Task<bool> ApproveEditAsync(string summary, string path, string? before, string after, TimeSpan expiresIn, CancellationToken cancel) =>
            Owner.QueueDiffReview(summary, path, before, after, expiresIn, cancel);

        public Task<byte[]?> EditorScreenshotAsync(CancellationToken cancel) => Owner.CaptureEditorScreenshotAsync(cancel);

        public bool CredentialDocumentVisible => Owner.CredentialDocumentShown();
    }
}
