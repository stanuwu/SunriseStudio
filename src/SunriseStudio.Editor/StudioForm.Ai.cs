/**
 * File: the AI side of the editor that does not depend on a console: what the AI sees (the context chip and popup,
 * pins, the attention log, Publish context), the folders popup, the MCP status dot and "Ask AI about this". The
 * in-editor AI console/terminal was removed (docs/UI-UE5.md §4.3); the AI now runs in the separate Solforge AI app
 * (StudioForm.SolforgeAi.cs, launched from the toolbar pill) and reaches the editor through the MCP server.
 * Related: StudioForm.SolforgeAiPanel.cs (the AI panel), StudioForm.Ai.Host.cs (ISolforgeHost, approvals, edits,
 * screenshots), .Ai.Query.cs (project reads), .Ai.Assets.cs, .Ai.Media.cs, .Ai.Stream.cs. The terminal classes in Ai/
 * stay compiled for their checks but nothing in the editor uses them.
 */
using System.Numerics;
using System.Runtime.CompilerServices;
using ImGuiNET;
using Solforge.Mcp;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    /** The console list is gone; get_settings still reports an "aiEngine", now the external app (StudioForm.Ai.Query.cs). */
    private sealed record AiEngine(string Name, string Command);
    private static readonly AiEngine[] AiEngines = [new("Solforge AI (separate app)", "")];
    private const int aiEngine = 0;
    private const string AiContextPopup = "ai-context-popup", AiFoldersPopup = "ai-folders-popup";
    /** Log of what the AI looked at, shown in the context dropdown. */
    private readonly AttentionLog aiAttention = new();
    private readonly List<(string Id, string Label)> aiPinned = [];
    private string aiNewFolder = "";
    /** A note from AiWriteSession (StudioForm.Ai.Query.cs) when the session files could not be written; the AI panel shows it. */
    private (string Text, bool Ok)? aiInstallNote;
    private volatile bool aiPaused;
    private long aiShortcutFrame = -1;
    private object? aiHoverCandidate, aiLastHover, aiLastSelected;
    private double aiHoverSince;
    private Vector3 aiLastPivot;

    /**
     * Shows the Solforge AI panel (Window > Solforge AI panel, the approval toast and badge, window.ai_workspace and
     * show_panel ai_workspace), or its own window when it is popped out. It never launches the app.
     */
    public void OpenAiWorkspace()
    {
        if (McpOnlyUi) return;
        if (ShowPopout(StudioPanelKind.AI, true)) return;
        var node = layout.Show(StudioPanelKind.AI);
        if (node.Collapsed == true) node.Collapsed = false;
        maximizedPanel = null;
        layoutDirty = true;
    }

    /** Runs every frame: attention, the context snapshot, and Ctrl+Shift+A (launch or focus Solforge AI). */
    private void HandleAiShortcuts()
    {
        TrackAiAttention();
        PublishAiContext();
        if (!ShortcutInput.Pressed(ShortcutActions.OpenAiWorkspace) || aiShortcutFrame == ShortcutInput.Frame) return;
        aiShortcutFrame = ShortcutInput.Frame;
        // Same id and binding as before; it now means "Launch or focus Solforge AI" (a user action, never an MCP call).
        LaunchOrFocusSolforgeAi();
    }
    /** The chip: the hovered or selected object, else the open script, asset or map. */
    private string AiContextChip()
    {
        if (aiPaused) return "Context paused";
        if ((viewport?.HoveredObject ?? viewport?.SelectedObject) is not null) return "Selected: " + AiContextShort();
        if (missionGraph.ScriptPath is { } script) return "Script: " + Path.GetFileName(script);
        if (workspace.Document is { } document) return "Asset: " + document.Name;
        if (workspace.World?.Map.Name is { } map) return "Map: " + map;
        return "Context: nothing selected";
    }

    private bool CanAskAi => !aiPaused
        && (viewport?.SelectedObject is not null || missionGraph.ScriptPath is not null || workspace.Document is not null || workspace.World is not null);

    /** Copies a question about the current focus to the clipboard, to paste into Solforge AI (the console is gone). */
    private void AskAiAboutContext()
    {
        var (text, subject) = AiAskText();
        CopyText(text);
        ShowInfoToast("Copied: paste into Solforge AI");
        aiAttention.Record(AttentionKind.Open, "ask", "Copied a question about " + subject);
    }

    /** The "Ask about this" question and its subject (UI thread); Solforge AI's Editor > Context puts it into its composer. */
    private (string Text, string Subject) AiAskText()
    {
        string subject;
        if (viewport?.SelectedObject is { } selected)
            subject = $"the object I selected in Solforge, {AiLabel(selected)} (id {AiId(selected)}). Use get_context and get_object";
        else if (missionGraph.ScriptPath is { } script)
            subject = $"the mission script I have open, {AiShownPath(script)}. Use get_open_documents and read_panel mission_graph";
        else if (workspace.Document is { } document)
            subject = $"the asset I have open, {document.Name} (tag 0x{document.Entry.TagHash:X8}). Use get_open_documents and read_panel inspector";
        else subject = $"the map I have open, {workspace.World?.Map.Name}. Use get_context and read_panel report";
        return ($"Tell me about {subject}, then say what I could change about it.", subject);
    }

    /** Publish context on or off (paused cancels pending edit reviews, as before); Solforge AI hears the change. */
    private void SetAiPublish(bool on)
    {
        if (aiPaused == !on) return;
        aiPaused = !on;
        EmitUiSoon(Solforge.Mcp.EditorUiEvents.ContextChanged);
        EmitUiSoon(Solforge.Mcp.EditorUiEvents.StateChanged);
    }

    /** The MCP status as a coloured dot; the text is its tooltip. */
    private void DrawAiStatusDot()
    {
        string status = "MCP off";
        AiServiceStatus(ref status);
        var color = status.StartsWith("MCP :", StringComparison.Ordinal) ? SolTheme.Ok
            : status.StartsWith("MCP error", StringComparison.Ordinal) ? SolTheme.Bad : SolTheme.Dim;
        float dot = ImGui.GetFrameHeight();
        ImGui.SameLine();
        ImGui.Dummy(new(dot));
        var center = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) / 2;
        ImGui.GetWindowDrawList().AddCircleFilled(center, dot * .22f, SolTheme.U32(color));
        StudioUi.Tip(status);
    }

    /** Draws a coloured one-line note. */
    private static void AiNote(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    /** Pixel width of a button with this label. */
    private static float ButtonWidth(string label) => ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2;

    /** The Context button's text: the hovered or selected object's short name, else the map. */
    private string AiContextShort()
    {
        if (aiPaused) return "paused";
        if ((viewport?.HoveredObject ?? viewport?.SelectedObject) is { } focus)
        {
            string label = AiLabel(focus);
            int slash = label.LastIndexOf(" / ", StringComparison.Ordinal);
            if (slash >= 0) label = label[(slash + 3)..];
            int dash = label.IndexOf(" - ", StringComparison.Ordinal);
            return dash > 0 ? label[..dash] : label;
        }
        return workspace.World?.Map.Name ?? workspace.Document?.Name ?? "nothing selected";
    }

    /** A width capped to the window (and the screen of a popped-out panel), so the popup never clips; values wrap. */
    private void DrawAiContextPopup()
    {
        if (!ImGui.IsPopupOpen(AiContextPopup)) return;
        var display = ImGui.GetIO().DisplaySize;
        float width = Math.Clamp(display.X - 16 * scale, 120 * scale, 400 * scale);
        ImGui.SetNextWindowSize(new(width, 0));
        ImGui.SetNextWindowSizeConstraints(new(width, 0), new(width, Math.Max(160 * scale, display.Y * .75f)));
        if (!ImGui.BeginPopup(AiContextPopup)) return;
        DrawAiContext();
        ImGui.EndPopup();
    }

    /** Draws the Context dropdown: what the AI currently sees. */
    private void DrawAiContext()
    {
        bool publish = !aiPaused;
        if (UiAuto.Checkbox("Publish context", ref publish)) SetAiPublish(publish);
        StudioUi.Tip("Off pauses everything the AI can see about the scene (privacy).");
        SolTheme.SectionHeader("CONTEXT (LIVE)");
        if (aiPaused) ImGui.TextColored(SolTheme.Sun, "Paused: the AI sees nothing.");
        var view = viewport;
        AiRow("Hovered", view?.HoveredObject is { } hovered ? AiLabel(hovered) : null);
        AiRow("Selected", view?.SelectedObject is { } selected ? AiLabel(selected) : null);
        if (view is not null)
        {
            var eye = view.Camera.Position;
            AiRow("Camera", $"{eye.X:0.#}, {eye.Y:0.#}, {eye.Z:0.#}" + (workspace.World?.Map.Name is { } map ? $" - {map}" : ""));
        }
        AiRow("Open file", missionGraph.ScriptPath is { } script ? Path.GetFileName(script) : null);
        if (SolTheme.PrimaryButton("Pin")) PinAiObject();
        StudioUi.Tip("Keep the hovered (or selected) object in the AI's context.");
        ImGui.SameLine();
        if (UiAuto.Button("Copy screenshot path")) SendAiScreenshot();
        StudioUi.Tip("Save the viewport as a PNG in the project (.solforge/screenshots) and copy its path, to paste into Solforge AI.");
        if (aiPinned.Count > 0)
        {
            SolTheme.SectionHeader("PINNED");
            for (int i = 0; i < aiPinned.Count; i++)
            {
                ImGui.PushID(i);
                if (UiAuto.SmallButton("x")) { aiPinned.RemoveAt(i--); EmitUiSoon(Solforge.Mcp.EditorUiEvents.ContextChanged); ImGui.PopID(); continue; }
                ImGui.SameLine();
                ImGui.TextWrapped(aiPinned[i].Label);
                ImGui.PopID();
            }
        }
        DrawAiEdits();
        SolTheme.SectionHeader("RECENT (ATTENTION LOG)");
        foreach (var item in aiAttention.Recent(10))
        {
            ImGui.TextColored(SolTheme.Faint, $"{(DateTimeOffset.UtcNow - item.Time).TotalSeconds,4:0}s {item.Kind.ToString().ToLowerInvariant()}");
            ImGui.SameLine();
            ImGui.TextWrapped(item.Label);
        }
    }

    /** Draws one name/value row in the context dropdown. */
    private void AiRow(string name, string? value)
    {
        ImGui.TextColored(SolTheme.Dim, name);
        ImGui.SameLine(90 * scale);
        ImGui.TextWrapped(value ?? "-");
    }

    /** Found folders with a switch each, folders added by hand, and a path box; kept in the settings. */
    private void DrawAiFoldersPopup()
    {
        if (!ImGui.IsPopupOpen(AiFoldersPopup)) return;
        var display = ImGui.GetIO().DisplaySize;
        float width = Math.Clamp(display.X - 16 * scale, 120 * scale, 460 * scale);
        ImGui.SetNextWindowSize(new(width, 0));
        ImGui.SetNextWindowSizeConstraints(new(width, 0), new(width, Math.Max(160 * scale, display.Y * .75f)));
        if (!ImGui.BeginPopup(AiFoldersPopup)) return;
        DrawAiFolders();
        ImGui.EndPopup();
    }

    /** The Folders list, shared by the Solforge AI panel's popup and Settings > AI. */
    private void DrawAiFolders()
    {
        ImGui.TextWrapped("Each enabled folder includes every subfolder. Solforge MCP file reads and searches use these grants immediately; external CLI folder arguments refresh when the client starts. Use an absolute path for files outside the project.");
        SolTheme.SectionHeader("FOUND");
        if (workspace.Project?.DirectoryPath is not { } project) ImGui.TextDisabled("Open a project first.");
        else
        {
            var found = AiFoundFolders(project);
            if (found.Count == 0) ImGui.TextDisabled("Nothing open outside the project.");
            foreach (string folder in found)
            {
                ImGui.PushID(folder);
                bool on = !settings.AiHiddenFolders.Contains(folder, StringComparer.OrdinalIgnoreCase);
                if (UiAuto.Checkbox("##on", ref on))
                {
                    settings.AiHiddenFolders.RemoveAll(item => string.Equals(item, folder, StringComparison.OrdinalIgnoreCase));
                    if (!on) settings.AiHiddenFolders.Add(folder);
                    SaveSettings();
                    EmitUiSoon(Solforge.Mcp.EditorUiEvents.ContextChanged);
                }
                ImGui.SameLine();
                ImGui.TextWrapped(folder);
                ImGui.PopID();
            }
        }
        SolTheme.SectionHeader("ADDED");
        for (int i = 0; i < settings.AiFolders.Count; i++)
        {
            ImGui.PushID(i);
            if (UiAuto.SmallButton("x")) { settings.AiFolders.RemoveAt(i--); SaveSettings(); EmitUiSoon(Solforge.Mcp.EditorUiEvents.ContextChanged); ImGui.PopID(); continue; }
            ImGui.SameLine();
            if (Directory.Exists(settings.AiFolders[i])) ImGui.TextWrapped(settings.AiFolders[i]);
            else ImGui.TextColored(SolTheme.Faint, settings.AiFolders[i] + " (missing)");
            ImGui.PopID();
        }
        string add = aiNewFolder.Trim().Trim('"');
        ImGui.SetNextItemWidth(-(ButtonWidth("Add") + ImGui.GetStyle().ItemSpacing.X));
        UiAuto.InputTextWithHint("##ai-folder", "folder path", ref aiNewFolder, 512);
        ImGui.SameLine();
        ImGui.BeginDisabled(!Directory.Exists(add) || settings.AiFolders.Contains(add, StringComparer.OrdinalIgnoreCase));
        if (UiAuto.Button("Add")) { settings.AiFolders.Add(Path.GetFullPath(add)); aiNewFolder = ""; SaveSettings(); EmitUiSoon(Solforge.Mcp.EditorUiEvents.ContextChanged); }
        ImGui.EndDisabled();
    }

    /** Display label of an inspected object. */
    private string AiLabel(object source) =>
        viewport?.ObjectName?.Invoke(source) ?? inspectionWorld?.Find(source)?.Name ?? source.ToString() ?? "object";

    /** Stable id of an inspected object. */
    private string AiId(object source) =>
        inspectionWorld?.Find(source)?.Id ?? "runtime:" + RuntimeHelpers.GetHashCode(source).ToString("x8");

    /** Pins the hovered (else selected) object, or only the selected one, into the AI context; false when there is none. */
    private bool PinAiObject(bool selectedOnly = false)
    {
        if ((selectedOnly ? viewport?.SelectedObject : viewport?.HoveredObject ?? viewport?.SelectedObject) is not { } source) return false;
        string id = AiId(source), label = AiLabel(source);
        if (aiPinned.All(pin => pin.Id != id)) aiPinned.Add((id, label));
        aiAttention.Record(AttentionKind.Pin, id, label);
        EmitUiSoon(Solforge.Mcp.EditorUiEvents.ContextChanged);
        return true;
    }

    /** Hover counts after 300 ms on one object; selection at once; a camera jump onto the selection is a focus. */
    private void TrackAiAttention()
    {
        if (aiPaused || viewport is not { } view) return;
        double now = ImGui.GetTime();
        var hovered = view.HoveredObject;
        if (!ReferenceEquals(hovered, aiHoverCandidate)) { aiHoverCandidate = hovered; aiHoverSince = now; }
        else if (hovered is not null && !ReferenceEquals(hovered, aiLastHover) && now - aiHoverSince >= .3)
        {
            aiLastHover = hovered;
            aiAttention.Record(AttentionKind.Hover, AiId(hovered), AiLabel(hovered));
        }
        var selected = view.SelectedObject;
        if (!ReferenceEquals(selected, aiLastSelected))
        {
            aiLastSelected = selected;
            if (selected is not null) aiAttention.Record(AttentionKind.Select, AiId(selected), AiLabel(selected));
        }
        var pivot = view.Camera.Pivot;
        if (pivot != aiLastPivot)
        {
            if (selected is not null && view.SelectedBounds is { IsFinite: true } bounds && Vector3.Distance(pivot, bounds.Center) < .01f
                && Vector3.Distance(aiLastPivot, bounds.Center) >= .01f)
                aiAttention.Record(AttentionKind.Focus, AiId(selected), AiLabel(selected));
            aiLastPivot = pivot;
        }
    }

    /** Hook implemented in StudioForm.Ai.Host.cs: starts the MCP service when needed. */
    partial void EnsureAiService();
    /** Hook implemented in StudioForm.Ai.Host.cs: stops the MCP service. */
    partial void StopAiService();
    /** Hook implemented in StudioForm.Ai.Host.cs: publishes discovery and writes the MCP client configs for a folder. */
    partial void PrepareAiEngine(string folder);
    /** Hook implemented in StudioForm.Ai.Host.cs: reports the MCP service status text. */
    partial void AiServiceStatus(ref string status);
    /** Hook implemented in StudioForm.Ai.Host.cs: saves a viewport screenshot and copies its path to the clipboard. */
    partial void SendAiScreenshot();
    /** Hook implemented in StudioForm.Ai.Host.cs: draws the request log and pending approval requests. */
    partial void DrawAiRequests();
    /** Hook implemented in StudioForm.Ai.Host.cs: draws the AI edit list with undo. */
    partial void DrawAiEdits();
}
