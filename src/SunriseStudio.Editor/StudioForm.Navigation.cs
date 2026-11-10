using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    // Page ids: "live", "live:<section>[:<subtab>]", "asset:<page>", "world:scene", "world:mission", "world:report", "world:asset:<page>".
    private string? shownPage;
    private StudioView? restoringView;
    private int restoringFrames, requestFrames;
    // A restored or requested tab can take a few frames to open. After this many frames the view is recorded as it is.
    private const int RestoreFrameLimit = 30;
    // The row a link or history asks a page for, given to the page when it draws.
    private (string Page, string Row)? pendingRow;
    // Set when a link or history chooses a row and cleared once the view is recorded, so that row is recorded as a new place.
    private bool linkedRow;
    // The row page drawn this frame, whose row the view records.
    private IStudioRowPage? shownRowPage;

    private void HandleNavigationShortcuts()
    {
        if (ShortcutInput.Pressed(ShortcutActions.NavigateBack)) NavigateBack();
        if (ShortcutInput.Pressed(ShortcutActions.NavigateForward)) NavigateForward();
    }

    private void NavigateBack() { if (workspace.CanNavigateBack) workspace.Run(workspace.NavigateBack); }
    private void NavigateForward() { if (workspace.CanNavigateForward) workspace.Run(workspace.NavigateForward); }

    private void DrawNavigationButtons()
    {
        ImGui.BeginDisabled(!workspace.CanNavigateBack);
        string back = ShortcutInput.Text(ShortcutActions.NavigateBack);
        if (ImGuiIcons.Button("navigate-back", StudioIcon.Back, back.Length == 0 ? "Back" : $"Back ({back})")) NavigateBack();
        ImGui.EndDisabled(); ImGui.SameLine();
        ImGui.BeginDisabled(!workspace.CanNavigateForward);
        string forward = ShortcutInput.Text(ShortcutActions.NavigateForward);
        if (ImGuiIcons.Button("navigate-forward", StudioIcon.Forward, forward.Length == 0 ? "Forward" : $"Forward ({forward})"))
            NavigateForward();
        ImGui.EndDisabled();
    }

    /** Runs after the frame is drawn: applies a view that navigation arrived at, else records the shown one. */
    private void TrackNavigation()
    {
        if (workspace.IsBusy) return;
        if (workspace.TakePendingView() is { } arrived)
        {
            restoringView = arrived; restoringFrames = 0;
            ApplyView(arrived);
            return;
        }
        var view = new StudioView(shownPage, selectedWorldId, ShownRow());
        if (restoringView is { } target)
        {
            bool done = (target.Page is null || target.Page == view.Page)
                && target.Selection == view.Selection && target.Row == view.Row;
            if (!done && ++restoringFrames < RestoreFrameLimit) return;
            restoringView = null; linkedRow = false; liveTab.TakeLinkedRow();
        }
        // A tab a link asked for opens a frame or two later. Until then the frame still shows the place being left.
        if (!TabRequested()) requestFrames = 0;
        else if (requestFrames < RestoreFrameLimit) { requestFrames++; return; }
        workspace.NoteView(view, linkedRow | liveTab.TakeLinkedRow());
        linkedRow = false;
    }

    private bool TabRequested() => focusAssets || focusLive || liveTab.Requested
        || focusDocumentPage is not null || pendingRow is not null;

    /** The chosen row of the page or Live section drawn this frame. */
    internal string? ShownRow() => shownPage is null ? null
        : shownPage.StartsWith("live", StringComparison.Ordinal) ? liveTab.Row : shownRowPage?.Row;

    /** Opens the recorded tab and selects the recorded object; a missing object clears the selection. */
    private void ApplyView(StudioView view)
    {
        switch (view.Page)
        {
            case { } page when page.StartsWith("live", StringComparison.Ordinal):
                focusLive = true;
                // The page is "live:<section>[:<subtab>]", or a bare "live" that names no section.
                var parts = page.Split(':', 3);
                if (parts.Length > 1) liveTab.Show(parts[1], parts.Length > 2 ? parts[2] : "", view.Row);
                break;
            case "world:scene": focusWorldScene = true; break;
            case "world:mission": focusMissionPage = true; break;
            case "world:report": focusWorldReport = true; break;
            case { } page when page.StartsWith("world:asset", StringComparison.Ordinal):
                focusAssetPage = true;
                string? assetPage = page.Length > "world:asset:".Length ? page["world:asset:".Length..] : null;
                if (assetPage is not null) focusDocumentPage = assetPage;
                pendingRow = assetPage is not null && view.Row is { } kept ? (assetPage, kept) : null;
                break;
            case { } page when page.StartsWith("asset:", StringComparison.Ordinal):
                focusAssets = true; focusDocumentPage = page["asset:".Length..];
                pendingRow = view.Row is { } recorded ? (focusDocumentPage, recorded) : null;
                break;
        }
        if (view.Selection is { } id && inspectionWorld?.Find(id) is { } row) SelectWorldObject(row);
        else if (selectedWorldId is not null)
        {
            viewport?.SetSelection(null);
            OnViewportSelection(null);
        }
    }
}
