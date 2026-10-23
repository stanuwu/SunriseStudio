using System.Numerics;
using System.Text;
using ImGuiNET;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    private string mapSearch = "", missionSearch = "", hierarchySearch = "";
    private bool focusHierarchy, focusWorldScene, focusMissionPage, focusAssetPage, focusWorldReport, inspectAsset;
    private bool assetPageVisible;
    private WorldMissionEntry? pendingMission;
    private WorldDocument? inspectionWorld;
    private int observedWorldRevision = -1, observedNameRevision = -1;
    private object? selectedObject, isolatedObject;
    private readonly HashSet<object> isolatedMembers = new(ReferenceEqualityComparer.Instance);
    private string? selectedWorldId;
    private readonly Dictionary<string, ScenePointProxy> pointProxies = [];
    private readonly HashSet<uint> hiddenBubbles = [];
    private bool showMap = true, showMission = true;

    private static unsafe bool BeginEditorTab(string label, bool selected = false)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(label + '\0');
        fixed (byte* text = bytes)
            return ImGuiNative.igBeginTabItem(text, null,
                selected ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None) != 0;
    }

    /** Maps by name; choosing the open map only shows its scene. */
    private unsafe void DrawMapBrowser()
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##mapSearch", "Find map", ref mapSearch, 256);
        if (workspace.WorldCatalog is null)
        { ImGui.TextDisabled(workspace.Catalog is null ? "Open Sunrise" : "Reading maps..."); return; }
        ImGui.BeginChild("map-list");
        var rows = MatchingMaps();
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(rows.Length);
        while (clipper.Step())
            for (int index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
            {
                var (map, label) = rows[index];
                ImGui.PushID(unchecked((int)map.Tag));
                ImGui.BeginDisabled(workspace.IsBusy);
                if (ImGui.Selectable(label, workspace.World?.Map.Tag == map.Tag))
                {
                    // The open map is already loaded; choosing it again only shows its scene.
                    if (workspace.World is not { MissionTag: null } open || open.Map.Tag != map.Tag) workspace.OpenMap(map.Tag);
                    focusWorldScene = true;
                }
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{label}\n{map.BubbleHashes.Count} bubbles\n{map.Tag:X8}");
                ImGui.PopID();
            }
        clipper.End(); clipper.Destroy();
        ImGui.EndChild();
    }

    /** Missions, of the open map when one is open; a mission on several maps asks which map to open. */
    private unsafe void DrawMissionBrowser()
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##missionSearch", "Find mission", ref missionSearch, 256);
        if (workspace.WorldCatalog is null)
        { ImGui.TextDisabled("Reading missions..."); return; }
        if (workspace.World is { } current) ImGui.TextDisabled(workspace.DisplayName(current.Map.Tag));
        ImGui.BeginChild("mission-list");
        bool chooseMap = false;
        var rows = MatchingMissions();
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(rows.Length);
        while (clipper.Step())
            for (int index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
            {
                var (mission, label) = rows[index];
                ImGui.PushID(unchecked((int)mission.Tag));
                ImGui.BeginDisabled(workspace.IsBusy || mission.MapTags.Count == 0);
                bool openMission = workspace.World?.MissionTag == mission.Tag;
                if (ImGui.Selectable(label, openMission))
                {
                    // The open mission is already loaded; choosing it again only shows its scene.
                    if (!openMission)
                    {
                        if (workspace.World is { } selected && mission.MapTags.Contains(selected.Map.Tag))
                            workspace.OpenMission(selected.Map.Tag, mission.Tag);
                        else if (mission.MapTags.Count == 1) workspace.OpenMission(mission.MapTags[0], mission.Tag);
                        else { pendingMission = mission; chooseMap = true; }
                    }
                    focusWorldScene = true;
                }
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{label}\n{mission.BubbleHashes.Count} bubbles\n{mission.Tag:X8}");
                ImGui.PopID();
            }
        clipper.End(); clipper.Destroy();
        ImGui.EndChild();
        // Open at this ID level; the popup ID inside the list child would not match BeginPopup.
        if (chooseMap) ImGui.OpenPopup("Choose mission map");
        // A click outside closes the popup, and that also drops the pending mission.
        if (!ImGui.BeginPopup("Choose mission map")) { pendingMission = null; return; }
        foreach (uint map in pendingMission?.MapTags ?? [])
            if (ImGui.Selectable(workspace.DisplayName(map) + "##" + map)) { workspace.OpenMission(map, pendingMission!.Tag); break; }
        ImGui.Selectable("Cancel");
        ImGui.EndPopup();
    }

    /** The Scene, Mission, Asset and Report tabs of the open world. */
    private void DrawWorldDocument()
    {
        var world = workspace.World!;
        ImGui.TextUnformatted(workspace.DisplayName(world.MissionTag ?? world.Map.Tag));
        ImGui.SameLine(); ImGui.TextDisabled(world.MissionTag is null ? "Map" : "Mission");
        if (!ImGui.BeginTabBar("world-pages")) return;
        if (BeginEditorTab("Scene", focusWorldScene))
        {
            EnterWorldPage(false);
            focusWorldScene = false;
            shownPage = "world:scene";
            DrawSceneFilters();
            DrawSceneWithTree();
            ImGui.EndTabItem();
        }
        if (world.MissionTag is not null && BeginEditorTab("Mission", focusMissionPage))
        { EnterWorldPage(false); focusMissionPage = false; shownPage = "world:mission"; DrawMissionEditor(); ImGui.EndTabItem(); }
        if (BeginEditorTab("Asset", focusAssetPage))
        {
            EnterWorldPage(true);
            focusAssetPage = false;
            shownPage = "world:asset";
            DrawWorldAsset();
            ImGui.EndTabItem();
        }
        if (BeginEditorTab("Report", focusWorldReport))
        { EnterWorldPage(false); focusWorldReport = false; shownPage = "world:report"; DrawWorldReport(); ImGui.EndTabItem(); }
        ImGui.EndTabBar();
    }

    private void EnterWorldPage(bool asset)
    {
        if (assetPageVisible != asset) inspectAsset = asset;
        assetPageVisible = asset;
    }

    private void DrawWorldAsset()
    {
        if (workspace.Document is not { } document) { ImGui.TextDisabled("Select an asset"); return; }
        DrawAssetHeader(document);
        DrawDocumentPages(document, "world:asset:", new(workspace, assetViewport!, property =>
            { selectedProperty = property; propertyValue = property.Value; inspectAsset = true; },
            mesh => { assetViewport!.SetSelection(mesh); inspectAsset = true; }, selectedMesh, gui!.MonospaceFont));
    }

    /** Tree toggle, bubble and layer menus and the map and mission checks; a filter that does not fit wraps. */
    private void DrawSceneFilters()
    {
        var world = workspace.World!;
        float left = ImGui.GetCursorPosX(), right = left + ImGui.GetContentRegionAvail().X;
        void Next(float width)
        {
            ImGui.SameLine();
            if (ImGui.GetCursorPosX() + width > right) ImGui.NewLine();
        }
        float CheckWidth(string text) => ImGui.CalcTextSize(text).X + ImGui.GetFrameHeight()
            + ImGui.GetStyle().ItemInnerSpacing.X;
        bool treeVisible = settings.ShowSceneTree;
        if (ImGuiIcons.Toggle("scene-tree", StudioIcon.Tree, "Object tree", ref treeVisible)) settings.ShowSceneTree = treeVisible;
        Next(150 * scale);
        ImGui.SetNextItemWidth(150 * scale);
        if (ImGui.BeginCombo("##bubbleVisibility", "Bubbles", ImGuiComboFlags.HeightLarge))
        {
            if (ImGui.Selectable("Show all")) hiddenBubbles.Clear();
            foreach (var bubble in world.Scene.Bubbles.GroupBy(bubble => world.CanonicalBubble(bubble.TagHash)).Select(group => group.First()))
            {
                uint canonical = world.CanonicalBubble(bubble.TagHash);
                bool visible = !hiddenBubbles.Contains(canonical);
                string name = BubbleTitle(canonical, bubble.Name);
                if (ImGui.Checkbox($"{name}##{canonical:X8}", ref visible))
                { if (visible) hiddenBubbles.Remove(canonical); else hiddenBubbles.Add(canonical); }
            }
            ImGui.EndCombo();
        }
        Next(130 * scale);
        ImGui.SetNextItemWidth(130 * scale);
        if (ImGui.BeginCombo("##layerVisibility", "Layers"))
        {
            SceneLayerMenu.Draw(viewport!.Visibility, null, viewport);
            ImGui.EndCombo();
        }
        if (world.MissionTag is not null)
        {
            Next(CheckWidth("Map")); ImGui.Checkbox("Map", ref showMap);
            Next(CheckWidth("Mission")); ImGui.Checkbox("Mission", ref showMission);
        }
        if (isolatedObject is not null)
        { Next(ImGui.CalcTextSize("Show all").X + ImGui.GetStyle().FramePadding.X * 2); if (ImGui.Button("Show all")) isolatedObject = null; }
    }

    private void DrawWorldHierarchy() => DrawObjectTree("browser-tree");

    // WorldVisibility's inputs at the last stamp; visibilityStamp changes when any of them does.
    private readonly HashSet<uint> stampedBubbles = [];
    private readonly HashSet<object> stampedMembers = new(ReferenceEqualityComparer.Instance);
    private object? stampedTree, stampedIsolated, stampedWorld, stampedInspection;
    private (long Tree, long Live, bool Map, bool Mission, uint? MissionTag, int World) stampedValues;
    private long visibilityStamp;

    /** Changes whenever WorldVisibility may answer differently for any object. */
    private long WorldVisibilityStamp()
    {
        var values = (sceneTree?.Revision ?? 0, liveInspection.TreeRevision, showMap, showMission,
            workspace.World?.MissionTag, workspace.WorldRevision);
        if (values == stampedValues && ReferenceEquals(stampedTree, sceneTree) && ReferenceEquals(stampedIsolated, isolatedObject)
            && ReferenceEquals(stampedWorld, workspace.World) && ReferenceEquals(stampedInspection, inspectionWorld)
            && stampedBubbles.SetEquals(hiddenBubbles) && stampedMembers.SetEquals(isolatedMembers)) return visibilityStamp;
        stampedValues = values; stampedTree = sceneTree; stampedIsolated = isolatedObject;
        stampedWorld = workspace.World; stampedInspection = inspectionWorld;
        stampedBubbles.Clear(); stampedBubbles.UnionWith(hiddenBubbles);
        stampedMembers.Clear(); stampedMembers.UnionWith(isolatedMembers);
        return ++visibilityStamp;
    }

    /** The viewport's per-object filter: tree hiding, isolation, hidden bubbles and the map and mission checks. */
    private bool WorldVisibility(object source)
    {
        object actual = source is ScenePointProxy proxy ? proxy.Details ?? proxy : source;
        if (liveInspection.FindNode(actual, SceneSelectionMode.MeshPart) is { } live)
            return live.Visible && (isolatedObject is null || isolatedMembers.Contains(actual));
        // With no hidden node every node is visible.
        if (sceneTree is { HiddenNodeIds.Count: > 0 } tree && !tree.IsVisible(actual)) return false;
        if (isolatedObject is not null && !isolatedMembers.Contains(actual)) return false;
        if (hiddenBubbles.Count != 0)
        {
            uint bubble = actual switch
            {
                MeshInstance mesh => mesh.BubbleHash,
                SceneMarker marker => marker.BubbleHash,
                _ => inspectionWorld?.Find(actual)?.BubbleHash ?? 0
            };
            if (hiddenBubbles.Contains(workspace.World?.CanonicalBubble(bubble) ?? bubble)) return false;
        }
        if (workspace.World?.MissionTag is null) return true;
        uint mission = actual switch
        {
            MeshInstance mesh => mesh.ActivityTag,
            SceneMarker marker => marker.ActivityTag,
            _ => inspectionWorld?.Find(actual)?.MissionTag ?? 0
        };
        return mission == 0 ? showMap : showMission;
    }

    /** Rebuilds the inspection world, its point proxies and the scene tree from the active scene. */
    private void RebuildWorldInspection()
    {
        inspectionWorld = workspace.World ?? (workspace.ActiveScene is { } scene
            ? WorldDocument.FromScene(scene, workspace.Names) : null);
        pointProxies.Clear();
        if (inspectionWorld is not null)
            foreach (var row in inspectionWorld.Objects.Where(row => row.Position is not null
                && row.Source is not (MeshInstance or SceneMarker)))
            {
                var proxy = new ScenePointProxy(row.Id, row.Name, row.Position!.Value,
                    row.Extents ?? new Vector3(.25f), row.Kind == WorldObjectKind.Light ? SceneLayer.Lights : SceneLayer.Squads,
                    row.BubbleHash, row.Source);
                pointProxies.Add(row.Id, proxy);
                viewport!.AddPointProxy(proxy);
            }
        objectInspection = null;
        observedWorldRevision = workspace.WorldRevision;
        observedNameRevision = workspace.NameRevision;
        RebuildSceneTree();
    }

    /** Follows a placement edit in place; scene, uploads, camera and selection stay. */
    private void AdoptPlacementUpdates()
    {
        var updates = workspace.TakePlacementUpdates();
        if (updates.Count == 0 || workspace.World is not { } world || !ReferenceEquals(viewport!.Scene, world.Scene)) return;
        viewport.UpdatePlacements(updates.SelectMany(update => update.Instances).Distinct().ToArray(),
            updates.SelectMany(update => update.Markers).Distinct().ToArray());
        inspectionWorld = world;
        var rows = updates.SelectMany(update => update.Rows).Select(row => row.Id).Distinct()
            .Select(id => world.Find(id)).OfType<WorldObject>().ToArray();
        foreach (var row in rows)
            if (pointProxies.TryGetValue(row.Id, out var old) && row.Position is { } position)
            {
                var proxy = old with { Position = position, Extents = row.Extents ?? new Vector3(.25f) };
                pointProxies[row.Id] = proxy;
                viewport.ReplacePointProxy(old, proxy);
            }
        sceneTree?.ReplaceRows(rows);
        cachedMissionWorld = null;
        objectInspection = null; editTarget = null; sceneEditInfo = null;
        if (selectedGroupId is { } group) SelectObjectGroup(group);
        else if (selectedWorldId is not null && world.Find(selectedWorldId) is { } selected) SelectWorldObject(selected);
    }

    private void RefreshWorldLabels()
    {
        if (observedWorldRevision == workspace.WorldRevision && observedNameRevision == workspace.NameRevision) return;
        observedWorldRevision = workspace.WorldRevision; observedNameRevision = workspace.NameRevision;
        if (inspectionWorld is not null && workspace.Names is { } names)
            inspectionWorld = workspace.World ?? inspectionWorld.Relabel(names);
        RebuildSceneTree();
        if (selectedWorldId is not null && inspectionWorld?.Objects.FirstOrDefault(row => row.Id == selectedWorldId) is { } selected)
        { selectedObject = selected.Source; objectInspection = inspectionWorld.Inspect(selected.Source); }
        SelectPendingCopy();
    }

    /** Selects a world row and unhides the bubble, layer and map or mission it needs to show. */
    private void SelectWorldObject(WorldObject row)
    {
        TrackTreeSelection(row.Source, true);
        hiddenBubbles.Remove(row.BubbleHash);
        if (row.MissionTag == 0) showMap = true; else showMission = true;
        if (row.Source is MeshInstance instance)
        {
            viewport!.Visibility.HiddenLayers.Remove(instance.Layer);
            viewport.Visibility.HiddenGeometryLayers.Remove(instance.Layer);
            if (viewport.SelectionMode == SceneSelectionMode.MeshPart)
                viewport.Visibility.DetailLevel = instance.Mesh.LodLevel;
        }
        else if (row.Source is SceneMarker marker)
        { viewport!.Visibility.HiddenLayers.Remove(marker.Layer); viewport.Visibility.HiddenOverlayLayers.Remove(marker.Layer); }
        else if (pointProxies.TryGetValue(row.Id, out var visiblePoint))
        { viewport!.Visibility.HiddenLayers.Remove(visiblePoint.Layer); viewport.Visibility.HiddenOverlayLayers.Remove(visiblePoint.Layer); }
        selectedWorldId = row.Id;
        if (pointProxies.TryGetValue(row.Id, out var point)) viewport!.SetSelection(point);
        else if (row.Source is MeshInstance or SceneMarker) viewport!.SetSelection(row.Source);
        else viewport!.SetSelection(null);
        OnViewportSelection(viewport!.SelectedObject ?? row.Source);
    }

    /** Adopts a viewport selection and drops the group and edit state of the old one. */
    private void OnViewportSelection(object? value)
    {
        var row = value is ScenePointProxy proxy ? inspectionWorld?.Objects.FirstOrDefault(item => item.Id == proxy.Key)
            : value is null ? null : inspectionWorld?.Find(value);
        selectedObject = row?.Source ?? (value is ScenePointProxy point ? point.Details : value);
        selectedWorldId = row?.Id;
        selectedGroupId = null;
        if (value is not null) collisionInspected = null;
        TrackTreeSelection(selectedObject, false);
        if (workspace.World is null) selectedMesh = selectedObject as MeshInstance;
        inspectAsset = false;
        objectInspection = selectedObject is null ? null : inspectionWorld?.Inspect(selectedObject);
        sceneEditInfo = null; editTarget = null;
    }

    private string BubbleTitle(uint hash, string fallback) => workspace.ResolveHash(hash) is { } title
        && !title.StartsWith("0x", StringComparison.Ordinal) ? title : fallback;

    private static bool MatchesText(string filter, params string[] values) =>
        filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(word => values.Any(value => value.Contains(word, StringComparison.OrdinalIgnoreCase)));
}
