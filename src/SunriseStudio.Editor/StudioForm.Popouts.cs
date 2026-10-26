using System.Numerics;
using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** Editors in their own windows. Each window has its own ImGui context; the main tick draws them after itself. */
public sealed partial class StudioForm
{
    private readonly List<StudioPopout> popouts = [];

    // The old Mission Graph and Node Graph ids (saved popouts, callers) name the unified Graph panel.
    private bool IsPoppedOut(StudioPanelKind kind) => popouts.Any(popout => popout.Panel == StudioLayout.Canonical(kind));

    /** Reopens the saved windows; an editor the layout already shows stays docked. */
    private void RestorePopouts()
    {
        if (options.Smoke) { settings.Popouts = []; return; }
        foreach (var state in settings.Popouts.ToArray())
            if (layout.Find(state.Panel) is null && !IsPoppedOut(state.Panel))
            {
                // A window that cannot open docks its editor; the editor must stay reachable.
                try { OpenPopout(state.Panel, new Rectangle(state.X, state.Y, state.Width, state.Height)); }
                catch (Exception error) when (error is not OutOfMemoryException)
                { workspace.Error = error.Message; layout.Show(state.Panel); }
            }
        RememberPopouts();
    }


    /** The area closes and its editor moves to a new window; the last area stays, empty. */
    private void PopOut(StudioLayoutNode node)
    {
        var kind = node.Panel;
        if (kind == StudioPanelKind.Empty || IsPoppedOut(kind)) return;
        // A tab leaves its area (the other tabs stay); a lone panel closes its area.
        if (node.Members.Count > 1) layout.CloseTab(node.Id, kind);
        else if (node == layout.Root) node.Panel = StudioPanelKind.Empty;
        else layout.Close(node.Id);
        maximizedPanel = null;
        layoutDirty = true;
        OpenPopout(kind, null);
    }

    private void OpenPopout(StudioPanelKind kind, Rectangle? bounds)
    {
        kind = StudioLayout.Canonical(kind);
        var popout = new StudioPopout(kind, DisplayTitle(kind), studioIcon, bounds, scale);
        popout.Show(this);
        try { popout.Attach(renderer!, gui!, scale); }
        catch { popout.Dispose(); throw; }
        popouts.Add(popout);
        RememberPopouts();
    }

    /** Puts the editor back into the main window. */
    private void PopIn(StudioPopout popout)
    {
        var kind = popout.Panel;
        ClosePopout(kind);
        if (layout.Find(kind) is null) layout.Show(kind);
        layoutDirty = true;
    }

    /** Closes the editor's window, if it has one, without docking it. */
    private void ClosePopout(StudioPanelKind kind)
    {
        kind = StudioLayout.Canonical(kind);
        if (popouts.FirstOrDefault(popout => popout.Panel == kind) is not { } popout) return;
        popouts.Remove(popout);
        popout.Dispose();
        RememberPopouts();
    }

    /**
     * Shows a popped-out editor's window; a minimized one is restored.
     * @param activate Also gives the window keyboard focus.
     * @return False when the editor is docked.
     */
    private bool ShowPopout(StudioPanelKind kind, bool activate = false)
    {
        kind = StudioLayout.Canonical(kind);
        if (popouts.FirstOrDefault(popout => popout.Panel == kind) is not { } popout) return false;
        if (popout.WindowState == FormWindowState.Minimized) popout.WindowState = FormWindowState.Normal;
        if (activate) popout.Activate();
        return true;
    }

    private void RememberPopouts() => settings.Popouts = popouts.Select(popout =>
    {
        var bounds = popout.WindowState == FormWindowState.Normal ? popout.Bounds : popout.RestoreBounds;
        return new StudioPopoutState(popout.Panel, bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }).ToList();

    /** Draws each window with its own target and context, after the main window's frame. */
    private void DrawPopouts(float delta)
    {
        foreach (var popout in popouts.ToArray())
        {
            if (popout.PopInRequested) { PopIn(popout); continue; }
            var size = popout.ClientSize;
            if (!popout.IsAttached || popout.WindowState == FormWindowState.Minimized || size.Width <= 0 || size.Height <= 0)
                continue;
            renderer!.BeginWindowFrame(popout.Target, size.Width, size.Height, StudioTheme.Canvas);
            try
            {
                popout.Gui.NewFrame(delta);
                // Each pop-out has its own ImGui context and so its own UiMotion states (hook H2).
                UiMotion.BeginFrame();
                ShortcutInput.BeginFrame(popout.Gui.IsInputActive && EditorReady && !workspace.IsBusy);
                popoutDrawing = popout;
                // Solforge: busy or loading, a popped-out editor stays drawn; DrawPanelContent holds the editing ones.
                if (EditorReady && !workspace.IsBusy) { HandleShortcuts(); DrawPopoutContent(popout, size); HandleViewShortcuts(); }
                else if (EditorReady || LoadOverlay) DrawPopoutContent(popout, size);
                popout.Chrome.EndFrame();
                UiMotion.EndFrame();
                popout.Gui.Render();
            }
            finally { popoutDrawing = null; renderer.EndWindowFrame(popout.Target); }
        }
        // Code after the frame expects the main window's context.
        gui?.MakeCurrent();
    }

    private void DrawPopoutContent(StudioPopout popout, Size client)
    {
        var size = new Vector2(client.Width, client.Height);
        // UE5 style: a 30 px Header strip with the panel as one active tab, Pop in and the window buttons.
        float header = MathF.Round(30 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(SolTheme.Space3, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, SolTheme.Header);
        BeginPanel("popout-header", Vector2.Zero, new(size.X, header), ImGuiWindowFlags.NoScrollbar);
        ImGui.PopStyleColor();
        // The header is the caption wherever no control sits; its close button docks the editor.
        popout.Chrome.Scale = scale;
        popout.Chrome.CaptionHeight = header;
        var draw = ImGui.GetWindowDrawList();
        var origin = ImGui.GetWindowPos();
        draw.AddRectFilled(origin, origin + new Vector2(size.X, header), SolTheme.U32(SolTheme.Header));
        draw.AddLine(origin + new Vector2(0, header - .5f), origin + new Vector2(size.X, header - .5f), SolTheme.U32(SolTheme.Border), 1);
        string title = DisplayTitle(popout.Panel);
        bool labelFont = SolTheme.PushFont(SolTheme.LabelFont);
        float iconSize = MathF.Round(12 * scale);
        float tabWidth = ImGui.CalcTextSize(title).X + iconSize + SolTheme.Space2 + SolTheme.Space4 * 2;
        var tabMin = origin + new Vector2(SolTheme.Space3, 6 * scale);
        var tabMax = new Vector2(tabMin.X + tabWidth, origin.Y + header);
        SolTheme.PanelTab(draw, tabMin, tabMax, "", true);
        float midY = (tabMin.Y + tabMax.Y) / 2;
        ImGuiIcons.Draw(draw, PanelIcon(popout.Panel), new(tabMin.X + SolTheme.Space4, midY - iconSize / 2), iconSize,
            SolTheme.U32(popout.Panel == StudioPanelKind.AI ? SolTheme.Brand : SolTheme.TextStrong));
        draw.AddText(new(tabMin.X + SolTheme.Space4 + iconSize + SolTheme.Space2, midY - ImGui.GetTextLineHeight() / 2),
            SolTheme.U32(SolTheme.TextStrong), title);
        if (labelFont) ImGui.PopFont();
        string label = "Pop in";
        float width = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2;
        float buttons = 3 * 46 * scale;
        ImGui.SetCursorPos(new(MathF.Max(tabMax.X - origin.X + SolTheme.Space4, size.X - buttons - width - 8 * scale),
            (header - ImGui.GetFrameHeight()) / 2));
        if (UiAuto.Button(label)) popout.RequestPopIn();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Put this editor back into the main window");
        popout.Chrome.DrawButtons(header, popout.RequestPopIn);
        ImGui.End();
        ImGui.PopStyleVar(2);
        // A new window's content fades in (decoration only; input works from the first frame).
        float alpha = UiMotion.FadeIn(UiMotion.Id("popout-content-fade"));
        bool faded = alpha < .999f;
        if (faded) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, Math.Max(.05f, alpha) * ImGui.GetStyle().Alpha);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        try { DrawPanelContent(popout.Panel, 0, new(0, header), new(size.X, MathF.Max(1, size.Y - header))); }
        finally { ImGui.PopStyleVar(faded ? 2 : 1); }
    }
}
