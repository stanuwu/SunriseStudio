using System.Numerics;
using ImGuiNET;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    private StudioLayout layout;
    private int? maximizedPanel;
    private Action? pendingLayoutChange;
    private bool layoutDirty;
    public StudioLayout EditorLayout => layout;

    public void ResetLayout()
    {
        foreach (var popout in popouts.ToArray()) ClosePopout(popout.Panel);
        layout = StudioLayout.Default();
        maximizedPanel = null;
        settings.BrowserWidth = settings.InspectorWidth = 300;
        settings.ChangesHeight = 190;
        settings.SceneTreeWidth = 280;
        settings.ShowSceneTree = true;
        layoutDirty = true;
    }

    private static string PanelTitle(StudioPanelKind kind) => kind switch
    {
        StudioPanelKind.Browser => "Asset browser",
        StudioPanelKind.ObjectTree => "Object tree",
        StudioPanelKind.Changes => "Project changes",
        StudioPanelKind.MissionGraph => "Mission Graph",
        StudioPanelKind.Empty => "Choose editor",
        _ => kind.ToString()
    };

    private void DrawLayout(Vector2 position, Vector2 size)
    {
        if (!workspace.IsBusy && workspace.World is null)
        { focusWorldScene = focusMissionPage = false; }
        // A focus request shows a popped-out editor's window instead of docking a second copy.
        if ((focusAssets || focusLive || focusAssetPage || focusWorldScene || focusMissionPage)
            && !ShowPopout(StudioPanelKind.Workspace))
        {
            bool absent = layout.Find(StudioPanelKind.Workspace) is null;
            var editor = layout.Show(StudioPanelKind.Workspace);
            layoutDirty |= absent;
            if (maximizedPanel is not null) maximizedPanel = editor.Id;
        }
        if (focusMissionGraph && !ShowPopout(StudioPanelKind.MissionGraph))
        {
            bool absent = layout.Find(StudioPanelKind.MissionGraph) is null;
            var graph = layout.Show(StudioPanelKind.MissionGraph);
            layoutDirty |= absent;
            if (maximizedPanel is not null) maximizedPanel = graph.Id;
        }
        if (focusHierarchy && !ShowPopout(StudioPanelKind.Browser))
        {
            bool absent = layout.Find(StudioPanelKind.Browser) is null;
            layout.Show(StudioPanelKind.Browser);
            layoutDirty |= absent;
            maximizedPanel = null;
        }
        var root = maximizedPanel is { } id ? layout.Find(id) : null;
        if (root is not { IsLeaf: true }) { root = layout.Root; maximizedPanel = null; }
        if (layout.Find(StudioPanelKind.MissionGraph) is null && !IsPoppedOut(StudioPanelKind.MissionGraph))
            missionGraphActive = false;
        DrawLayoutNode(root, position, size);
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
        if (node.IsLeaf) { DrawLayoutPanel(node, position, size); return; }
        bool columns = node.Axis == StudioSplitAxis.Columns;
        float divider = Math.Min(5 * scale, columns ? size.X : size.Y);
        float span = Math.Max(0, (columns ? size.X : size.Y) - divider);
        var firstMin = StudioLayout.MinimumSize(node.First!, scale);
        var secondMin = StudioLayout.MinimumSize(node.Second!, scale);
        float a = columns ? firstMin.X : firstMin.Y, b = columns ? secondMin.X : secondMin.Y;
        float shrink = Math.Min(1, span / (a + b));
        float cut = Math.Clamp(span * node.Ratio, a * shrink, Math.Max(a * shrink, span - b * shrink));
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
        float header = Math.Min(size.Y, ImGui.GetFrameHeight() + 8 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4 * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        BeginPanel($"area-{node.Id}-header", position, new(size.X, header), ImGuiWindowFlags.NoScrollbar);
        if (node.Panel != StudioPanelKind.Empty) TrackAreaFocus(node.Panel == StudioPanelKind.MissionGraph);
        float button = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(Math.Max(button, size.X - button - 16 * scale));
        if (ImGui.BeginCombo("##editor-type", PanelTitle(node.Panel)))
        {
            foreach (var kind in Enum.GetValues<StudioPanelKind>())
            {
                if (ImGui.Selectable(PanelTitle(kind), node.Panel == kind))
                    pendingLayoutChange = () => { ClosePopout(kind); layout.SetPanel(node.Id, kind); };
                if (kind != node.Panel && kind != StudioPanelKind.Empty && ImGui.IsItemHovered())
                {
                    if (layout.Find(kind) is not null) ImGui.SetTooltip("Swap with the open editor");
                    else if (IsPoppedOut(kind)) ImGui.SetTooltip("Bring back from its own window");
                }
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine(0, 4 * scale);
        if (ImGui.Button("...", new(button, 0))) ImGui.OpenPopup("Area");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Area options");
        if (ImGui.BeginPopup("Area"))
        {
            bool canSplit = layout.Panels.Count < StudioLayout.MaximumPanels;
            if (ImGui.MenuItem("Split left / right", "", false, canSplit && size.X >= 280 * scale))
                pendingLayoutChange = () => { layout.Split(node.Id, StudioSplitAxis.Columns); maximizedPanel = null; };
            if (ImGui.MenuItem("Split top / bottom", "", false, canSplit && size.Y >= 220 * scale))
                pendingLayoutChange = () => { layout.Split(node.Id, StudioSplitAxis.Rows); maximizedPanel = null; };
            ImGui.Separator();
            if (ImGui.MenuItem(maximizedPanel is null ? "Maximize area" : "Restore areas"))
                pendingLayoutChange = () => maximizedPanel = maximizedPanel is null ? node.Id : null;
            if (ImGui.MenuItem("Pop out window", "", false, node.Panel != StudioPanelKind.Empty && renderer is not null))
                pendingLayoutChange = () => PopOut(node);
            if (ImGui.MenuItem("Close area", "", false, node != layout.Root))
                pendingLayoutChange = () => { layout.Close(node.Id); maximizedPanel = null; };
            ImGui.EndPopup();
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
        position.Y += header;
        size.Y = Math.Max(1, size.Y - header);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        DrawPanelContent(node.Panel, node.Id, position, size);
        ImGui.PopStyleVar();
    }

    /** Draws one editor below its header; a docked area and a popped-out window share it. */
    private void DrawPanelContent(StudioPanelKind panel, int area, Vector2 position, Vector2 size)
    {
        switch (panel)
        {
            case StudioPanelKind.Workspace: DrawDocument(position, size); break;
            case StudioPanelKind.Browser: DrawBrowser(position, size); break;
            case StudioPanelKind.Inspector: DrawInspector(position, size); break;
            case StudioPanelKind.Changes: DrawChanges(position, size); break;
            case StudioPanelKind.MissionGraph:
                if (focusMissionGraph) ImGui.SetNextWindowFocus();
                BeginPanel("Mission graph area", position, size);
                if (focusMissionGraph || ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
                    missionGraphActive = true;
                focusMissionGraph = false;
                missionGraph.Draw(workspace);
                ImGui.End();
                break;
            case StudioPanelKind.ObjectTree:
                BeginPanel("Object tree", position, size);
                TrackAreaFocus();
                DrawObjectTree("layout-tree");
                ImGui.End();
                break;
            case StudioPanelKind.Preview:
                BeginPanel("Preview area", position, size);
                TrackAreaFocus();
                if (viewport is not null && workspace.World is null
                    && workspace.Document is { Scene: not null } or { Texture: not null })
                {
                    DrawPreviewChoice(workspace.Document, ImGui.GetContentRegionAvail().X);
                    viewport.DrawImGui(ImGui.GetContentRegionAvail());
                }
                else ImGui.TextDisabled("No model");
                ImGui.End();
                break;
            default:
                BeginPanel($"area-{area}-empty", position, size);
                ImGui.End();
                break;
        }
    }

    private void DrawLayoutMenu()
    {
        if (ImGui.BeginMenu("Panels"))
        {
            foreach (var kind in Enum.GetValues<StudioPanelKind>().Where(kind => kind != StudioPanelKind.Empty))
                if (ImGui.MenuItem(PanelTitle(kind), "", layout.Find(kind) is not null || IsPoppedOut(kind))
                    && !ShowPopout(kind, true))
                {
                    layout.Show(kind);
                    maximizedPanel = null;
                    layoutDirty = true;
                }
            ImGui.EndMenu();
        }
        if (ImGui.MenuItem("Restore areas", "", false, maximizedPanel is not null)) maximizedPanel = null;
        if (ImGui.MenuItem("Reset layout")) ResetLayout();
    }

    private void SaveLayout()
    {
        settings.Layout = layout.Save();
        layoutDirty = false;
        SaveSettings();
    }

    private void TrackAreaFocus(bool mission = false)
    {
        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)) missionGraphActive = mission;
    }
}
