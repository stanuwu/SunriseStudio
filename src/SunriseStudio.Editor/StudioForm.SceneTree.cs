using System.Numerics;
using ImGuiNET;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    private SceneHierarchy? sceneTree;
    private (bool World, uint Tag, string? Added)? treeIdentity;
    private readonly HashSet<string> expandedTreeNodes = new(StringComparer.Ordinal);
    private SceneHierarchyNode[] treeRows = [];
    private sealed record TreeDisplayRow(SceneHierarchyNode? Authored, LiveTreeNode? Live)
    {
        public string Id => Authored?.Id ?? Live!.Id;
        public string Name => Authored?.Name ?? Live!.Name;
        public int Depth => Authored?.Depth ?? Live!.Depth;
        public bool Visible => Authored?.Visible ?? Live!.Visible;
        public int ChildCount => Authored?.Children.Count ?? Live!.Children.Count;
        public bool ParentHidden => Authored is { } authored ? authored.Parent?.Visible == false : Live!.Parent?.Visible == false;
    }
    private TreeDisplayRow[] displayTreeRows = [];
    private long liveTreeRevision = -1;
    private string? selectedTreeId, scrollTreeId;
    private long treeSelectionRevision;
    private readonly Dictionary<string, long> treeScrollRevisions = [];
    private string treeSearch = "";
    private bool treeRowsDirty = true;
    public SceneHierarchy? ObjectTree => sceneTree;
    public string? SelectedTreeNodeId => selectedTreeId;

    /** View hiding stays local and survives reload only within the same map or asset. */
    private void RebuildSceneTree()
    {
        if (inspectionWorld is not { } world)
        { sceneTree = null; treeRows = []; treeIdentity = null; selectedTreeId = null; return; }
        var key = workspace.World is { } map ? (true, map.Map.Tag, null)
            : (false, workspace.Document?.Entry.TagHash ?? world.Scene.TagHash, workspace.Document?.AddedAssetId);
        bool retain = treeIdentity == key;
        var hidden = retain ? sceneTree?.HiddenNodeIds.ToArray() : null;
        sceneTree = SceneHierarchy.Build(world, hidden);
        treeIdentity = key;
        if (!retain)
        {
            expandedTreeNodes.Clear();
            expandedTreeNodes.Add(sceneTree.Root.Id);
            foreach (var node in sceneTree.Root.Children) expandedTreeNodes.Add(node.Id);
            hierarchySearch = "";
        }
        treeRowsDirty = true;
        TrackTreeSelection(selectedObject, false);
    }

    /** Scene selection expands its ownership path before scrolling either tree panel. */
    private void TrackTreeSelection(object? source, bool reveal)
    {
        if (source is not null && liveInspection.FindNode(source, viewport!.SelectionMode) is { } live)
        {
            if (reveal) liveInspection.Reveal(live);
            selectedTreeId = scrollTreeId = live.Id; treeSelectionRevision++;
            foreach (var ancestor in liveInspection.PathTo(live)) expandedTreeNodes.Add(ancestor.Id);
            hierarchySearch = ""; treeRowsDirty = true; return;
        }
        var node = source is null ? null : sceneTree?.Find(source);
        selectedTreeId = node?.Id;
        scrollTreeId = node?.Id;
        treeSelectionRevision++;
        if (node is null || sceneTree is null) return;
        if (reveal) sceneTree.Reveal(node);
        foreach (var ancestor in sceneTree.PathTo(node)) expandedTreeNodes.Add(ancestor.Id);
        hierarchySearch = "";
        treeRowsDirty = true;
    }

    public void SetSceneNodeHidden(string id, bool hidden)
    {
        if (liveInspection.Find(id) is not null)
        {
            if (!liveInspection.SetHidden(id, hidden)) return;
            if (selectedObject is not null && !liveInspection.IsVisible(selectedObject)
                && !viewport!.SelectedMembers.Any(liveInspection.IsVisible))
            { viewport!.SetSelection(null); OnViewportSelection(null); }
            return;
        }
        if (sceneTree?.SetHidden(id, hidden) != true) return;
        if (selectedObject is not null && !sceneTree.IsVisible(selectedObject))
        { viewport!.SetSelection(null); OnViewportSelection(null); }
    }

    /** The sidebar uses the same cached hierarchy and visibility as the main browser. */
    private void DrawSceneWithTree()
    {
        var size = ImGui.GetContentRegionAvail();
        UpdateGizmo();
        if (!settings.ShowSceneTree || sceneTree is null || size.X < 400 * scale)
        { viewport!.DrawImGui(size); return; }
        float minimum = 160 * scale, maximum = size.X - 240 * scale;
        float treeWidth = Math.Clamp(settings.SceneTreeWidth * scale, minimum, maximum);
        float divider = 6 * scale;
        ImGui.BeginChild("scene-tree-panel", new(treeWidth, size.Y), ImGuiChildFlags.Borders);
        DrawObjectTree("scene-tree");
        ImGui.EndChild();
        ImGui.SameLine(0, 0);
        ImGui.InvisibleButton("scene-tree-divider", new(divider, size.Y));
        if (ImGui.IsItemActive()) settings.SceneTreeWidth = Math.Clamp(treeWidth + ImGui.GetIO().MouseDelta.X, minimum, maximum) / scale;
        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) settings.SceneTreeWidth = 280;
        if (ImGui.IsItemHovered() || ImGui.IsItemActive()) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
        var dividerStart = ImGui.GetItemRectMin() + new Vector2(divider / 2, 0);
        ImGui.GetWindowDrawList().AddLine(dividerStart, dividerStart + new Vector2(0, size.Y),
            ImGui.GetColorU32(ImGui.IsItemActive() || ImGui.IsItemHovered() ? StudioTheme.Accent : StudioTheme.Panel), 2 * scale);
        ImGui.SameLine(0, 0);
        ImGui.BeginChild("scene-view", new(0, size.Y), ImGuiChildFlags.None);
        viewport!.DrawImGui(ImGui.GetContentRegionAvail());
        ImGui.EndChild();
    }

    /** Expansion and search rebuild visible rows once; scrolling visits only clipped rows. */
    private void PrepareTreeRows()
    {
        if (!treeRowsDirty && treeSearch == hierarchySearch && liveTreeRevision == liveInspection.TreeRevision) return;
        treeRowsDirty = false; treeSearch = hierarchySearch;
        liveTreeRevision = liveInspection.TreeRevision;
        if (sceneTree is null) { treeRows = []; displayTreeRows = []; return; }
        HashSet<SceneHierarchyNode>? matching = null;
        if (hierarchySearch.Length != 0)
        {
            matching = [];
            foreach (var node in sceneTree.Nodes)
                if (MatchesText(hierarchySearch, node.Name, node.Kind.ToString(), node.Representative?.AssetTag.ToString("X8") ?? ""))
                    for (var parent = node; parent is not null && matching.Add(parent); parent = parent.Parent) { }
        }
        var rows = new List<SceneHierarchyNode>();
        void Visit(SceneHierarchyNode node)
        {
            if (matching is not null && !matching.Contains(node)) return;
            rows.Add(node);
            if (matching is not null || expandedTreeNodes.Contains(node.Id))
                foreach (var child in node.Children) Visit(child);
        }
        Visit(sceneTree.Root);
        treeRows = rows.ToArray();
        var display = treeRows.Select(node => new TreeDisplayRow(node, null)).ToList();
        if (liveInspection.Objects.Count > 0)
        {
            HashSet<LiveTreeNode>? liveMatches = null;
            if (hierarchySearch.Length > 0)
            {
                liveMatches = [];
                foreach (var node in liveInspection.Nodes)
                    if (MatchesText(hierarchySearch, node.Name, node.Id,
                        node.Source is { } item ? liveInspection.Find(item)?.Observation.ClassTag.ToString("X8") ?? "" : ""))
                        for (var parent = node; parent is not null && liveMatches.Add(parent); parent = parent.Parent) { }
            }
            void VisitLive(LiveTreeNode node)
            {
                if (liveMatches is not null && !liveMatches.Contains(node)) return;
                display.Add(new(null, node));
                if (liveMatches is not null || expandedTreeNodes.Contains(node.Id))
                    foreach (var child in node.Children) VisitLive(child);
            }
            VisitLive(liveInspection.Root);
        }
        displayTreeRows = display.ToArray();
    }

    /** Hide buttons change view state without editing package placements. */
    private unsafe void DrawObjectTree(string id)
    {
        if (sceneTree is null) { ImGui.TextDisabled("Open a map or asset"); return; }
        ImGui.PushID(id);
        DrawObjectGroups();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##search", "Find object", ref hierarchySearch, 256)) treeRowsDirty = true;
        ImGui.BeginDisabled(selectedObject is null);
        if (ImGuiIcons.Button("tree-selection", StudioIcon.Focus, "Reveal selection")) TrackTreeSelection(selectedObject, false);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGuiIcons.Button("tree-show-all", StudioIcon.Eye, "Show all objects"))
        { sceneTree.ShowAll(); liveInspection.ShowAll(); }
        ImGui.SameLine();
        if (ImGuiIcons.Button("tree-collapse", StudioIcon.Tree, "Collapse tree"))
        { expandedTreeNodes.Clear(); expandedTreeNodes.Add(sceneTree.Root.Id); expandedTreeNodes.Add(LiveInspectionIndex.RootId); treeRowsDirty = true; }
        PrepareTreeRows();
        float rowHeight = MathF.Ceiling(ImGui.GetTextLineHeight() + 10 * scale);
        float iconSize = rowHeight - 4 * scale;
        if (ImGui.BeginTable("nodes", 2, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp,
            ImGui.GetContentRegionAvail()))
        {
            ImGui.TableSetupColumn("Object", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Visibility", ImGuiTableColumnFlags.WidthFixed, iconSize + 2 * scale);
            if (scrollTreeId is { } selected && treeScrollRevisions.GetValueOrDefault(id, -1) != treeSelectionRevision)
            {
                int index = Array.FindIndex(displayTreeRows, row => row.Id == selected);
                if (index >= 0) ImGui.SetScrollY(Math.Max(0, index * rowHeight - ImGui.GetWindowHeight() * .4f));
                treeScrollRevisions[id] = treeSelectionRevision;
            }
            var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
            clipper.Begin(displayTreeRows.Length, rowHeight);
            while (clipper.Step())
                for (int index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
                {
                    var node = displayTreeRows[index];
                    ImGui.PushID(node.Id);
                    ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
                    ImGui.TableNextColumn();
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Min(node.Depth, 8) * 10 * scale);
                    bool expanded = expandedTreeNodes.Contains(node.Id);
                    var chevron = ImGui.GetCursorScreenPos();
                    if (node.ChildCount != 0)
                    {
                        if (ImGui.InvisibleButton("expand", new(12 * scale, iconSize)))
                        { if (expanded) expandedTreeNodes.Remove(node.Id); else expandedTreeNodes.Add(node.Id); treeRowsDirty = true; }
                        ImGuiIcons.Draw(ImGui.GetWindowDrawList(), expanded ? StudioIcon.Collapse : StudioIcon.Expand,
                            chevron + new Vector2(2 * scale, (iconSize - 8 * scale) / 2), 8 * scale, ImGui.GetColorU32(ImGuiCol.TextDisabled));
                    }
                    else ImGui.Dummy(new(12 * scale, iconSize));
                    ImGui.SameLine(0, 2 * scale);
                    var symbol = ImGui.GetCursorScreenPos();
                    ImGui.Dummy(new(14 * scale, iconSize));
                    ImGuiIcons.Draw(ImGui.GetWindowDrawList(), node.Authored is { } authoredIcon ? NodeIcon(authoredIcon.Kind)
                        : node.Live?.Parent is null ? StudioIcon.Group : StudioIcon.Cube,
                        symbol + new Vector2(1 * scale, (iconSize - 12 * scale) / 2), 12 * scale,
                        ImGui.GetColorU32(node.Visible ? StudioTheme.Muted : new Vector4(.28f, .31f, .36f, 1)));
                    ImGui.SameLine(0, 4 * scale);
                    bool dimmed = !node.Visible;
                    if (dimmed) ImGui.PushStyleColor(ImGuiCol.Text, StudioTheme.Muted);
                    int prefix = node.Authored is not null ? node.Name.LastIndexOf(" / ", StringComparison.Ordinal) : -1;
                    string label = node.Authored?.Representative is { } representative && ProjectName(representative) is { } named
                        ? named : prefix < 0 ? node.Name : node.Name[(prefix + 3)..];
                    bool clicked = ImGui.Selectable(label, selectedTreeId == node.Id, ImGuiSelectableFlags.AllowDoubleClick,
                        new(0, iconSize));
                    bool doubled = ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
                    if (clicked)
                    {
                        if (selectedTreeId == node.Id && !doubled)
                        { viewport!.SetSelection(null); OnViewportSelection(null); }
                        else if (node.Live is { Source: not null } live)
                        {
                            SelectLiveTreeNode(live);
                            if (doubled) viewport!.FocusSelection();
                        }
                        else if (node.Authored?.Representative is { } row)
                        {
                            SelectWorldObject(row);
                            if (doubled) viewport!.FocusSelection();
                        }
                        else
                        {
                            viewport!.SetSelection(null); OnViewportSelection(null); selectedTreeId = node.Id;
                            if (expanded) expandedTreeNodes.Remove(node.Id); else expandedTreeNodes.Add(node.Id);
                            treeRowsDirty = true;
                        }
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(node.Name);
                    DrawSpawnTreeMenu(node.Authored);
                    if (dimmed) ImGui.PopStyleColor();
                    ImGui.TableNextColumn();
                    bool ownHidden = node.Live is not null ? liveInspection.IsHidden(node.Id) : sceneTree.IsHidden(node.Id);
                    bool inherited = node.ParentHidden;
                    ImGui.BeginDisabled(inherited);
                    if (ImGuiIcons.Button("visibility", ownHidden || inherited ? StudioIcon.EyeOff : StudioIcon.Eye,
                        inherited ? "Hidden by parent" : ownHidden ? "Show object and children" : "Hide object and children",
                        size: iconSize)) SetSceneNodeHidden(node.Id, !ownHidden);
                    ImGui.EndDisabled();
                    ImGui.PopID();
                }
            clipper.End(); clipper.Destroy();
            ImGui.EndTable();
        }
        ImGui.PopID();
    }

    private static StudioIcon NodeIcon(SceneHierarchyKind kind) => kind switch
    {
        SceneHierarchyKind.Root or SceneHierarchyKind.Bubble or SceneHierarchyKind.Map => StudioIcon.Map,
        SceneHierarchyKind.Placement or SceneHierarchyKind.AuthoredObject => StudioIcon.Cube,
        SceneHierarchyKind.Marker => StudioIcon.Pin,
        SceneHierarchyKind.Light => StudioIcon.Light,
        SceneHierarchyKind.Squad => StudioIcon.Group,
        _ => StudioIcon.Folder
    };
}
