/**
 * File: the Solforge AI panel (StudioPanelKind.AI, persisted name "AI"), repurposed now that the in-editor console is
 * gone (docs/UI-UE5.md §4.2). Since the panel move (SOLFORGE-AI-APP-SPEC §6.6) the AI REQUESTS, AI LOG, SESSIONS and
 * CONTEXT panels live in Solforge AI › Editor. Top to bottom: agent C's status card (hook H6: launch/focus/stop, status
 * reply, app path, MCP port and calls, shared-resources preflight, workflow summary), the connection line and a pointer to
 * the app. While a paired app is attached: the pending count, the Publish context privacy switch and an optional
 * "show requests here too". While none is attached (the minimal fallback): the pending approval cards and the compact
 * context row (chip and popup with the AI edits, Publish context, Folders, Ask about this, screenshot path). Sessions and
 * the AI log are only in the app. Everything here is blocked for MCP UI automation, as the AI workspace was. Also the
 * small info toast and the approval docking.
 */
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    /** The info toast's text and the clock second it goes away. */
    private (string Text, double Until) infoToast = ("", -1);

    /** Draws the Solforge AI panel. */
    private void DrawSolforgeAiPanel(Vector2 position, Vector2 size)
    {
        BeginPanel("AI workspace", position, size);
        // The MCP never lists or drives this panel (approvals, launch and stop, context publishing).
        UiAuto.PushBlocked();
        try
        {
            TrackAreaFocus();
            EnsureAiService();
            ImGui.BeginChild("solforge-ai-body", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollWithMouse);
            UiMotion.SmoothScroll();
            DrawSolforgeAiStatusCard();
            DrawEditorLinkLine();
            if (aiInstallNote is { } note) AiNote(note.Ok ? SolTheme.Ok : SolTheme.Warn, note.Text);
            DrawAiFallback();
            ImGui.EndChild();
        }
        finally { UiAuto.PopBlocked(); }
        ImGui.End();
    }

    /** Show the fallback block even while a paired Solforge AI is attached (the user's choice in this panel). */
    private bool aiFallbackPinned;

    /**
     * The pointer line and the pairing state: requests, log, sessions and context live in Solforge AI › Editor once an app is
     * paired; a pairing waiting for the user is called out here as well as in its modal.
     */
    private void DrawEditorLinkLine()
    {
        var ui = aiService?.EditorUi;
        int paired = ui?.PairedApps.Count ?? 0;
        string link = ui is null ? "Editor UI channel off (the MCP service is not running)."
            : ui.PairPending ? "A Solforge AI is asking to connect: answer the prompt."
            : AiAppAttached ? "Connected to Solforge AI" + (paired > 1 ? $" ({paired} apps)" : "") + "."
            : paired > 0 ? "Solforge AI is paired but not listening right now."
            : "Solforge AI is not connected to this editor.";
        CardLine(ui?.PairPending == true ? SolTheme.Warn : AiAppAttached ? SolTheme.Ok : SolTheme.Muted, link);
        CardLine(SolTheme.Text, "Requests, log, sessions and context are in Solforge AI › Editor.");
    }

    /**
     * SOLFORGE-AI-APP-SPEC §6.6 fallback: while no paired Solforge AI holds the event stream or polled in the last 5 s (or the
     * user pinned it open), the AI REQUESTS cards with Approve/Deny are drawn here, so a request never waits unseen. The edit
     * review and workflow start modals always stay in the editor.
     */
    private void DrawAiFallback()
    {
        bool attached = AiAppAttached;
        if (attached)
        {
            // Panel move: while Solforge AI shows them, only the pending count, a pointer and the privacy switch stay here.
            int pending = AiPendingCount();
            if (pending > 0) CardLine(SolTheme.Warn, $"{pending} request(s) wait for approval in Solforge AI › Editor.");
            bool publish = !aiPaused;
            if (UiAuto.Checkbox("Publish context", ref publish)) SetAiPublish(publish);
            StudioUi.Tip("Off pauses everything the AI can see about the scene (privacy).");
            if (!aiFallbackPinned)
            {
                if (pending > 0 && UiAuto.SmallButton("Show requests here too##ai-fallback-show")) aiFallbackPinned = true;
                return;
            }
            if (UiAuto.SmallButton("Hide (Solforge AI shows them)##ai-fallback-hide")) aiFallbackPinned = false;
            DrawAiRequests();
            return;
        }
        // No app attached: the minimal fallback, so a request never waits unseen, plus the context row (its popup holds the
        // AI edits with Undo) for use without the app.
        DrawAiRequests();
        DrawAiContextSection();
    }

    /** What the AI sees: the chip (click for the popup, right-click to copy a question), Publish context, folders and pins. */
    private void DrawAiContextSection()
    {
        SolTheme.SectionHeader("CONTEXT", "What Solforge publishes to MCP clients about what you look at.");
        var style = ImGui.GetStyle();
        string chip = StudioUi.Fit(AiContextChip(), Math.Max(60 * scale, ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight() - style.FramePadding.X * 2 - style.ItemSpacing.X));
        ImGui.PushStyleColor(ImGuiCol.Button, SolTheme.Input);
        bool openContext = UiAuto.Button(chip + "###ai-context");
        ImGui.PopStyleColor();
        if (openContext) ImGui.OpenPopup(AiContextPopup);
        StudioUi.Tip("What the AI sees now. Click for details, pins and screenshots; right-click to copy a question about it.");
        if (ImGui.BeginPopupContextItem("ai-context-ask"))
        {
            SolTheme.PopupShadow();
            ImGui.BeginDisabled(!CanAskAi);
            if (UiAuto.MenuItem("Ask about this (copy for Solforge AI)")) AskAiAboutContext();
            ImGui.EndDisabled();
            ImGui.EndPopup();
        }
        DrawAiContextPopup();
        DrawAiStatusDot();
        bool publish = !aiPaused;
        if (UiAuto.Checkbox("Publish context", ref publish)) SetAiPublish(publish);
        StudioUi.Tip("Off pauses everything the AI can see about the scene (privacy).");
        ImGui.SameLine();
        if (UiAuto.Button("Folders...")) ImGui.OpenPopup(AiFoldersPopup);
        StudioUi.Tip("Folders outside the project that AI clients may read");
        DrawAiFoldersPopup();
        ImGui.BeginDisabled(!CanAskAi);
        if (UiAuto.Button("Ask about this")) AskAiAboutContext();
        ImGui.EndDisabled();
        StudioUi.Tip("Copies a question about the selection, open script, asset or map, to paste into Solforge AI.");
        ImGui.SameLine();
        ImGui.BeginDisabled(workspace.Project is null || viewport is not { IsRendering: true });
        if (UiAuto.Button("Copy screenshot path")) SendAiScreenshot();
        ImGui.EndDisabled();
        StudioUi.Tip("Saves the viewport as a PNG in the project (.solforge/screenshots) and copies its path.");
        if (aiPinned.Count > 0)
        {
            ImGui.TextDisabled($"{aiPinned.Count} pinned: " + string.Join(", ", aiPinned.Take(3).Select(pin => pin.Label))
                + (aiPinned.Count > 3 ? ", ..." : ""));
            StudioUi.Tip("Manage pins in the context popup (click the chip).");
        }
    }

    /**
     * Hook H9 (a), on the UI thread when an MCP request waits: docks the AI panel as a tab of the bottom area when the
     * layout has none, keeping that area's shown tab (no focus change). The toolbar badge and the toast read the queue.
     */
    private void DockAiPanelForApproval()
    {
        if (layout.Find(StudioPanelKind.AI) is not null || IsPoppedOut(StudioPanelKind.AI)) return;
        var bottom = layout.Find(StudioPanelKind.Output) ?? layout.Find(StudioPanelKind.Changes) ?? layout.Find(StudioPanelKind.Browser);
        if (bottom is not null)
        {
            var shown = bottom.Panel;
            if (layout.AddTab(bottom.Id, StudioPanelKind.AI) && layout.Find(StudioPanelKind.AI) is { } docked)
                StudioLayout.Select(docked, shown);
        }
        else layout.Show(StudioPanelKind.AI);
        layoutDirty = true;
    }

    /** A short confirmation (copied, saved) that fades in above the status bar for 3 seconds. */
    private void ShowInfoToast(string text) => infoToast = (text, clock.Elapsed.TotalSeconds + 3);

    /** Draws the info toast; never takes focus. */
    private void DrawInfoToast()
    {
        bool visible = clock.Elapsed.TotalSeconds < infoToast.Until;
        var (alpha, rise) = UiMotion.Toast(0x1F0_70A5u, visible);
        if (alpha <= .01f || infoToast.Text.Length == 0) return;
        ImGui.SetNextWindowPos(new(ClientSize.Width / 2f, ClientSize.Height - StatusBarHeight - SolTheme.Space5 + rise), ImGuiCond.Always, new(.5f, 1));
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, alpha * ImGui.GetStyle().Alpha);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(SolTheme.Space5, SolTheme.Space3));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 4 * scale);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, SolTheme.Popup);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoInputs;
        if (ImGui.Begin("##info-toast", flags))
        {
            SolTheme.PopupShadow();
            ImGui.TextColored(SolTheme.TextStrong, infoToast.Text);
        }
        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);
    }

    /** Puts text on the Windows clipboard (UI thread); a clipboard another app holds is reported, never thrown. */
    private void CopyText(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { ShowInfoToast("The clipboard is busy; try again."); }
    }
}
