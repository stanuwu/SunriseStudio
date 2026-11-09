using System.Numerics;
using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/**
 * StudioForm part: panel layout drawing, maximizing and reset. Areas draw a compact tabbed header
 * (StudioForm.PanelTabs.cs) over their content; content fades in on first show and on a tab switch (UiMotion).
 */
public sealed partial class StudioForm
{
    private StudioLayout layout;
    private int? maximizedPanel;
    private Action? pendingLayoutChange;
    private bool layoutDirty;
    public StudioLayout EditorLayout => layout;
    /** The panel each area showed last frame, so a tab switch restarts the content fade. */
    private readonly Dictionary<int, StudioPanelKind> shownAreaPanels = [];

    /** Panels offered in editor UI and UI automation; the MCP-only edition omits its retired assistant workspace. */
    private static IEnumerable<StudioPanelKind> VisiblePanelChoices => StudioLayout.Pickable
        .Where(kind => kind != StudioPanelKind.Empty && (!McpOnlyUi || kind != StudioPanelKind.AI));

    // UE5 layout: Reset layout uses the default preset (Solforge (UE5)); View > Layouts keeps every older one.
    public void ResetLayout() => ApplyLayoutPreset(StudioLayout.DefaultPreset);

    /**
     * The panel's persisted, MCP-facing name (window.panel, run_command and the old menus name panels by it).
     * The UI shows DisplayTitle.
     */
    private static string PanelTitle(StudioPanelKind kind) => kind switch
    {
        StudioPanelKind.Browser => "Asset browser",
        StudioPanelKind.ObjectTree => "Object tree",
        StudioPanelKind.Changes => "Project changes",
        // The old Mission Graph and Node Graph ids open the unified Graph panel.
        StudioPanelKind.Graph or StudioPanelKind.MissionGraph or StudioPanelKind.NodeGraph => "Graph",
        StudioPanelKind.LocalServer => "Local Server",
        StudioPanelKind.Empty => "Choose editor",
        _ => kind.ToString()
    };

    /** The UE5-style title an area tab, the Panels menu and a pop-out window show (docs/UI-UE5.md §1.1). */
    private static string DisplayTitle(StudioPanelKind kind) => kind switch
    {
        StudioPanelKind.Workspace => "Viewport",
        StudioPanelKind.Browser => "Content Browser",
        StudioPanelKind.ObjectTree => "Outliner",
        StudioPanelKind.Inspector => "Details",
        StudioPanelKind.AI => "Solforge AI",
        StudioPanelKind.Output => "Output Log",
        _ => PanelTitle(kind)
    };

    /** The icon an area tab shows beside its title. */
    private static StudioIcon PanelIcon(StudioPanelKind kind) => kind switch
    {
        StudioPanelKind.Workspace => StudioIcon.Cube,
        StudioPanelKind.Browser => StudioIcon.ContentBrowser,
        StudioPanelKind.ObjectTree => StudioIcon.Outliner,
        StudioPanelKind.Inspector => StudioIcon.Details,
        StudioPanelKind.Changes => StudioIcon.Edit,
        StudioPanelKind.AI => StudioIcon.Sparkle,
        StudioPanelKind.Output => StudioIcon.OutputLog,
        StudioPanelKind.Place => StudioIcon.Place,
        StudioPanelKind.LocalServer => StudioIcon.Play,
        StudioPanelKind.Workflows => StudioIcon.Loop,
        StudioPanelKind.Graph or StudioPanelKind.MissionGraph or StudioPanelKind.NodeGraph => StudioIcon.Group,
        _ => StudioIcon.Plus
    };

    private void DrawLayout(Vector2 position, Vector2 size)
    {
        UseProjectLayout();
        if (McpOnlyUi)
        {
            if (IsPoppedOut(StudioPanelKind.AI)) ClosePopout(StudioPanelKind.AI);
            if (layout.Find(StudioPanelKind.AI) is { } aiPanel)
            {
                if (aiPanel.Members.Count > 1) layout.CloseTab(aiPanel.Id, StudioPanelKind.AI);
                else if (aiPanel == layout.Root) layout.SetPanel(aiPanel.Id, StudioPanelKind.Empty);
                else layout.Remove(StudioPanelKind.AI);
                maximizedPanel = null;
                layoutDirty = true;
            }
        }
        if (!workspace.IsBusy && workspace.World is null)
        { focusWorldScene = focusMissionPage = false; }
        // A focus request shows a popped-out editor's window instead of docking a second copy.
        if ((focusAssets || focusLive || focusAssetPage || focusWorldScene || focusMissionPage)
            && !ShowPopout(StudioPanelKind.Workspace))
        {
            // Solforge: an open docks the editor only when it is missing and never moves a maximized area.
            bool absent = layout.Find(StudioPanelKind.Workspace) is null;
            layout.Show(StudioPanelKind.Workspace);
            layoutDirty |= absent;
        }
        if (focusMissionGraph && !ShowPopout(StudioPanelKind.Graph))
        {
            bool absent = layout.Find(StudioPanelKind.Graph) is null;
            layout.Show(StudioPanelKind.Graph);
            layoutDirty |= absent;
        }
        if (focusLocalServer && !ShowPopout(StudioPanelKind.LocalServer))
        {
            bool absent = layout.Find(StudioPanelKind.LocalServer) is null;
            var server = layout.Show(StudioPanelKind.LocalServer);
            layoutDirty |= absent;
            if (maximizedPanel is not null) maximizedPanel = server.Id;
        }
        if (focusHierarchy && !ShowPopout(StudioPanelKind.Browser))
        {
            bool absent = layout.Find(StudioPanelKind.Browser) is null;
            var browser = layout.Show(StudioPanelKind.Browser);
            if (browser.Collapsed == true) browser.Collapsed = false;
            layoutDirty |= absent;
            maximizedPanel = null;
        }
        if (focusAssetBrowser && !ShowPopout(StudioPanelKind.Browser) && layout.Find(StudioPanelKind.Browser) is { } shown)
        {
            StudioLayout.Select(shown, StudioPanelKind.Browser);
            if (shown.Collapsed == true) OpenFoldedArea(shown);
        }
        var root = maximizedPanel is { } id ? layout.Find(id) : null;
        if (root is not { IsLeaf: true }) { root = layout.Root; maximizedPanel = null; }
        if (layout.Find(StudioPanelKind.Graph) is null && !IsPoppedOut(StudioPanelKind.Graph))
            missionGraphActive = nodeGraphActive = false;
        drawerSeen = false;
        DrawLayoutNode(root, position, size);
        FinishDrawerFrame();
        DrawPanelDrops();
        if (pendingLayoutChange is { } change)
        {
            pendingLayoutChange = null;
            change();
            layoutDirty = true;
        }
        if (layoutDirty && !ImGui.IsMouseDown(ImGuiMouseButton.Left)) SaveLayout();
    }

    /** Split bounds shrink proportionally when the window cannot fit every area's minimum. */
    private void DrawLayoutNode(StudioLayoutNode node, Vector2 position, Vector2 size)
    {
        if (node.IsLeaf)
        {
            // A lone folded area has no splitter to size its strip; keep a real strip at the top instead of
            // stretching the fold over the whole document area.
            if (node == layout.Root && IsFolded(node) && maximizedPanel != node.Id)
            {
                float strip = Math.Min(size.Y, FoldStrip(true));
                DrawLayoutPanel(node, position, new(size.X, strip));
                if (size.Y > strip)
                {
                    ImGui.SetNextWindowPos(position + new Vector2(0, strip));
                    ImGui.SetNextWindowSize(new(size.X, size.Y - strip));
                    BeginPanel($"area-{node.Id}-folded-rest", position + new Vector2(0, strip),
                        new(size.X, size.Y - strip), ImGuiWindowFlags.NoScrollbar);
                    ImGui.End();
                }
                return;
            }
            DrawLayoutPanel(node, position, size);
            return;
        }
        bool columns = node.Axis == StudioSplitAxis.Columns;
        float divider = Math.Min(5 * scale, columns ? size.X : size.Y);
        float span = Math.Max(0, (columns ? size.X : size.Y) - divider);
        var firstMin = StudioLayout.MinimumSize(node.First!, scale);
        var secondMin = StudioLayout.MinimumSize(node.Second!, scale);
        float a = columns ? firstMin.X : firstMin.Y, b = columns ? secondMin.X : secondMin.Y;
        float shrink = Math.Min(1, span / (a + b));
        float cut = Math.Clamp(span * node.Ratio, a * shrink, Math.Max(a * shrink, span - b * shrink));
        // A folded side panel keeps its ratio for when it opens again and takes only a strip now.
        bool firstFolded = IsFolded(node.First!), secondFolded = IsFolded(node.Second!);
        if (firstFolded != secondFolded)
        {
            float whole = columns ? size.X : size.Y, strip = Math.Min(whole, FoldStrip(columns));
            cut = firstFolded ? strip : whole - strip;
            var firstPart = columns ? new Vector2(cut, size.Y) : new Vector2(size.X, cut);
            var rest = columns ? new Vector2(cut, 0) : new Vector2(0, cut);
            DrawLayoutNode(node.First!, position, firstPart);
            DrawLayoutNode(node.Second!, position + rest, Vector2.Max(Vector2.One, size - rest));
            return;
        }
        // The Content Browser drawer slides its area in and out (UiMotion spring); the layout only changes at the end.
        cut = DrawerCut(node, cut, span);
        var firstSize = columns ? new Vector2(cut, size.Y) : new Vector2(size.X, cut);
        var offset = columns ? new Vector2(cut, 0) : new Vector2(0, cut);
        var splitterSize = columns ? new Vector2(divider, size.Y) : new Vector2(size.X, divider);
        DrawLayoutNode(node.First!, position, firstSize);
        Splitter($"layout-{node.Id}", position + offset, splitterSize, delta =>
        {
            if (span <= 0) return;
            node.Ratio = Math.Clamp((cut + (columns ? delta.X : delta.Y)) / span, .01f, .99f);
            layoutDirty = true;
        });
        var secondOffset = columns ? new Vector2(cut + divider, 0) : new Vector2(0, cut + divider);
        DrawLayoutNode(node.Second!, position + secondOffset, Vector2.Max(Vector2.One, size - secondOffset));
    }

    private void DrawLayoutPanel(StudioLayoutNode node, Vector2 position, Vector2 size)
    {
        areaFrames.Add((position, position + size));
        if (IsFolded(node) && maximizedPanel != node.Id) { DrawFoldedPanel(node, position, size); return; }
        float header = Math.Min(size.Y, AreaHeaderHeight);
        DrawAreaHeader(node, position, size, header);
        position.Y += header;
        size.Y = Math.Max(1, size.Y - header);
        RecordDropArea(node.Id, position, position + size, header: false);
        // Content fades in on first show and when the area switches to another tab (decoration only).
        bool switched = shownAreaPanels.TryGetValue(node.Id, out var last) && last != node.Panel;
        shownAreaPanels[node.Id] = node.Panel;
        float alpha = UiMotion.FadeIn(unchecked(0x51A0_0000u + (uint)node.Id), switched);
        bool faded = alpha < .999f;
        if (faded) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, Math.Max(.05f, alpha) * ImGui.GetStyle().Alpha);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        try { DrawPanelContent(node.Panel, node.Id, position, size); }
        finally { ImGui.PopStyleVar(faded ? 2 : 1); }
    }

    /** Draws one editor below its header; a docked area and a popped-out window share it. */
    private void DrawPanelContent(StudioPanelKind panel, int area, Vector2 position, Vector2 size)
    {
        // Solforge: while work runs, browsing, the object tree, the logs and the AI stay live; editors that change the
        // project wait (their workspace calls would throw "Wait for the current operation"). A load swaps the document for a spinner.
        bool hold = workspace.IsBusy && panel is not (StudioPanelKind.AI or StudioPanelKind.Browser
            or StudioPanelKind.ObjectTree or StudioPanelKind.Empty or StudioPanelKind.Output or StudioPanelKind.Workflows);
        if (hold) ImGui.BeginDisabled();
        try { DrawPanelArea(panel, area, position, size); }
        finally { if (hold) ImGui.EndDisabled(); }
    }

    private void DrawPanelArea(StudioPanelKind panel, int area, Vector2 position, Vector2 size)
    {
        switch (panel)
        {
            case StudioPanelKind.Workspace:
                if (EditorReady) DrawDocument(position, size);
                else DrawAreaLoading("Document", position, size);
                break;
            case StudioPanelKind.Browser when browserPreparing: DrawAreaLoading("Asset browser loading", position, size); break;
            case StudioPanelKind.Browser: DrawBrowser(position, size); break;
            case StudioPanelKind.Inspector: DrawInspector(position, size); break;
            case StudioPanelKind.Changes: DrawChanges(position, size); break;
            // The AI panel no longer holds a console: Solforge AI's status card, requests, edits, presence and context.
            case StudioPanelKind.AI: DrawSolforgeAiPanel(position, size); break;
            case StudioPanelKind.LocalServer: DrawLocalServerPanel(position, size); break;
            case StudioPanelKind.Graph or StudioPanelKind.MissionGraph or StudioPanelKind.NodeGraph:
                DrawGraphPanel(position, size);
                break;
            case StudioPanelKind.ObjectTree:
                BeginPanel("Object tree", position, size);
                TrackAreaFocus();
                DrawObjectTree("layout-tree");
                ImGui.End();
                break;
            case StudioPanelKind.Place: DrawPlacePanel(position, size); break;
            case StudioPanelKind.Output: DrawOutputLog(position, size); break;
            case StudioPanelKind.Workflows:
                BeginPanel("Workflows", position, size);
                TrackAreaFocus();
                DrawWorkflowStatusPanel();
                ImGui.End();
                break;
            default:
                BeginPanel($"area-{area}-empty", position, size);
                DrawEmptyArea(area);
                ImGui.End();
                break;
        }
    }

    /** An empty area offers its editors as buttons, so a fresh split is one click from useful. */
    private void DrawEmptyArea(int area)
    {
        if (area <= 0) return;
        ImGui.SetCursorPos(new(SolTheme.Space5, SolTheme.Space5));
        ImGui.TextDisabled("Choose an editor for this area, or drag a tab here.");
        foreach (var kind in VisiblePanelChoices)
        {
            ImGui.SetCursorPosX(SolTheme.Space5);
            if (UiAuto.Selectable(DisplayTitle(kind) + "##empty-" + kind, false, ImGuiSelectableFlags.None, new(220 * scale, 0)))
                pendingLayoutChange = () => { ClosePopout(kind); layout.SetPanel(area, kind); };
        }
    }

    private void DrawLayoutMenu()
    {
        if (UiAuto.BeginMenu("Panels"))
        {
            SolTheme.PopupShadow();
            foreach (var kind in VisiblePanelChoices)
                if (UiAuto.MenuItem(DisplayTitle(kind), "", layout.Find(kind) is not null || IsPoppedOut(kind))
                    && !ShowPopout(kind, true))
                {
                    var shown = layout.Show(kind);
                    if (shown.Collapsed == true) shown.Collapsed = false;
                    maximizedPanel = null;
                    layoutDirty = true;
                }
            UiAuto.EndMenu();
        }
        if (UiAuto.MenuItem("Restore areas", "", false, maximizedPanel is not null)) maximizedPanel = null;
        if (UiAuto.MenuItem("Reset layout")) ResetLayout();
    }

    private void SaveLayout()
    {
        settings.Layout = layout.Save();
        if (workspace.Project?.DirectoryPath is { } project) settings.ProjectLayouts[ProjectLayoutKey(project)] = settings.Layout.Value;
        layoutDirty = false;
        SaveSettings();
    }

    private string? layoutProject;

    private static string ProjectLayoutKey(string folder) => Path.GetFullPath(folder).TrimEnd('\\', '/').ToUpperInvariant();

    /** Each project keeps its own layout; opening one restores what it had, else keeps the current layout for it. */
    private void UseProjectLayout()
    {
        string? project = workspace.Project?.DirectoryPath;
        // Solforge: a moment without a project (reopening the installation adopts Project = null) is not a project
        // switch; restoring the saved copy then closed popped-out editors and dropped unsaved area sizes.
        if (project is null || project == layoutProject) return;
        layoutProject = project;
        if (options.Smoke) return;
        if (settings.ProjectLayouts.TryGetValue(ProjectLayoutKey(project), out var saved))
        {
            foreach (var popout in popouts.ToArray()) ClosePopout(popout.Panel);
            layout = StudioLayout.Restore(saved, settings.BrowserWidth, settings.InspectorWidth, settings.ChangesHeight);
            maximizedPanel = null;
            browserSlot = null;
            CancelDrawer();
        }
        else layoutDirty = true;
    }

    /**
     * A side panel shows as a strip when the user folded it, or while it is empty unless the user pinned it open.
     * A tabbed area folds while empty only when every tab is empty.
     */
    private bool IsFolded(StudioLayoutNode node) => node.IsLeaf && StudioLayoutNode.Collapsible(node.Panel)
        && (node.Collapsed ?? node.Members.All(PanelEmpty)) && !options.Smoke;

    private bool PanelEmpty(StudioPanelKind kind) => kind switch
    {
        StudioPanelKind.Graph or StudioPanelKind.MissionGraph or StudioPanelKind.NodeGraph => GraphPanelEmpty,
        StudioPanelKind.Browser => workspace.Assets.Length == 0 && workspace.Catalog is null,
        StudioPanelKind.Changes => workspace.Project is null,
        StudioPanelKind.ObjectTree => sceneTree is null,
        StudioPanelKind.Inspector => workspace.Document is null && selectedObject is null,
        _ => false
    };

    private float FoldStrip(bool columns) => ImGui.GetFrameHeight() + (columns ? 10 : 8) * scale;

    /** The areas drawn this frame (header top to body bottom), framed by DrawAreaFrames. */
    private readonly List<(Vector2 Min, Vector2 Max)> areaFrames = [];

    /**
     * The mockup's framed panels: one input-less overlay window over the tiled areas paints each area's chamfered corners
     * in the page background and its 1 px Line frame. The areas send themselves to the back every frame
     * (SolTheme.SendToBack), so this overlay stays above them and below floating windows, popups, dialogs and toasts.
     */
    private void DrawAreaFrames()
    {
        if (areaFrames.Count == 0) return;
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoBackground
            | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(new(ClientSize.Width, ClientSize.Height));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (ImGui.Begin("##area-frames", flags))
        {
            var draw = ImGui.GetWindowDrawList();
            draw.PushClipRectFullScreen();
            foreach (var (min, max) in areaFrames) SolTheme.PanelFrame(draw, min, max);
            draw.PopClipRect();
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }

    /** The strip: an expand button and the area's panel names; clicking anywhere on it opens the area. */
    private void DrawFoldedPanel(StudioLayoutNode node, Vector2 position, Vector2 size)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4 * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, SolTheme.Header);
        BeginPanel($"area-{node.Id}-folded", position, size, ImGuiWindowFlags.NoScrollbar);
        ImGui.PopStyleColor();
        RecordDropArea(node.Id, position, position + size, header: true);
        bool open = UiAuto.Button("+", new(ImGui.GetFrameHeight(), 0));
        // A folded tabbed area lists every panel it holds.
        string title = string.Join(" · ", node.Members.Select(DisplayTitle));
        if (title.Length == 0) title = DisplayTitle(node.Panel);
        StudioUi.Tip($"Open {title}" + (node.Collapsed is null ? " (folded while empty; its area menu can keep it open)" : ""));
        if (size.X < size.Y)
        {
            // Columns: the name runs down the strip, one letter per line.
            var draw = ImGui.GetWindowDrawList();
            uint color = ImGui.GetColorU32(ImGuiCol.TextDisabled);
            var at = ImGui.GetCursorScreenPos() + new Vector2((ImGui.GetFrameHeight() - ImGui.CalcTextSize("M").X) / 2, 4 * scale);
            foreach (char letter in title.ToUpperInvariant())
            {
                if (at.Y + ImGui.GetTextLineHeight() > position.Y + size.Y - 4 * scale) break;
                if (letter != ' ') draw.AddText(at, color, letter.ToString());
                at.Y += ImGui.GetTextLineHeight() * (letter == ' ' ? .5f : .95f);
            }
        }
        else { ImGui.SameLine(); ImGui.AlignTextToFramePadding(); ImGui.TextDisabled(title); }
        if (!open && ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) open = true;
        if (open) pendingLayoutChange = () => OpenFoldedArea(node);
        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    /** Opens a folded area; the Content Browser's area slides open like the drawer. */
    private void OpenFoldedArea(StudioLayoutNode node)
    {
        node.Collapsed = false;
        if (node.Holds(StudioPanelKind.Browser)) StartDrawer(node.Id, open: true, folds: true);
    }

    private void TrackAreaFocus(bool graph = false)
    {
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)) return;
        if (graph) SetGraphActive();
        else missionGraphActive = nodeGraphActive = false;
    }
}
