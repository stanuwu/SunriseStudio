using ImGuiNET;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    private uint? configuredMission;
    private int missionStateMode;
    private readonly Dictionary<uint, int> selectedMissionStates = [];
    private bool missionStateDirty;
    private readonly HashSet<int> selectedMapLayers = [];
    private uint? configuredLayerMap;
    private WorldLoadOptions? configuredMissionOptions, configuredLayerOptions;
    private WorldDocument? cachedMissionWorld;
    private Dictionary<WorldStateKey, WorldObject[]> missionRows = [];
    private Dictionary<WorldStateKey, (string Name, WorldObject[] Rows)[]> missionGroups = [];
    private Dictionary<WorldStateKey, WorldObject> missionStates = [];
    private WorldObject[] unplacedMissionRows = [];
    private Dictionary<uint, WorldObject[]> sharedMissionRows = [];

    private void DrawMissionEditor()
    {
        if (workspace.World is not { MissionScene.Scenario: { } scenario } world) return;
        PrepareMissionRows();
        DrawMissionControls(world, scenario);
    }

    /** Cache ownership tables before the mission page becomes visible. */
    private void PrepareMissionRows()
    {
        if (workspace.World is not { } world)
        {
            cachedMissionWorld = null; missionRows.Clear(); missionGroups.Clear(); missionStates.Clear();
            sharedMissionRows.Clear(); unplacedMissionRows = [];
            return;
        }
        if (!ReferenceEquals(cachedMissionWorld, world))
        {
            cachedMissionWorld = world;
            var representatives = new HashSet<object>(ReferenceEqualityComparer.Instance);
            missionRows = world.Objects.Where(row => row.Kind != WorldObjectKind.State
                    && (row.Source is not MeshInstance mesh || representatives.Add(viewport!.Selection.ObjectRepresentative(mesh))))
                .SelectMany(row => row.Owners.Select(owner => (owner, row))).GroupBy(pair => pair.owner)
                .ToDictionary(group => group.Key, group => group.Select(pair => pair.row).ToArray());
            missionGroups = missionRows.ToDictionary(pair => pair.Key, pair => pair.Value.GroupBy(MissionGroup)
                .OrderBy(group => group.Key).Select(group => (group.Key, group.ToArray())).ToArray());
            missionStates = world.Objects.Where(row => row.Source is ScenarioStateInfo)
                .GroupBy(row => new WorldStateKey(row.MissionTag, ((ScenarioStateInfo)row.Source).BubbleNameHash,
                    ((ScenarioStateInfo)row.Source).Ordinal)).ToDictionary(group => group.Key, group => group.First());
            unplacedMissionRows = world.Objects.Where(row => row.MissionTag == world.MissionTag && row.BubbleHash == 0
                && row.Kind is WorldObjectKind.AuthoredObject or WorldObjectKind.Squad).ToArray();
            sharedMissionRows = world.Objects.Where(row => row.MissionTag == world.MissionTag && row.Owners.Count == 0
                    && row.Kind != WorldObjectKind.State)
                .GroupBy(row => row.BubbleHash).ToDictionary(group => group.Key, group => group.ToArray());
        }
    }

    /** State controls use the same authored ownership as scene selection. */
    private void DrawMissionControls(WorldDocument world, ScenarioInfo scenario)
    {
        if (configuredMission != world.MissionTag || !ReferenceEquals(configuredMissionOptions, world.Options))
        {
            configuredMission = world.MissionTag;
            configuredMissionOptions = world.Options;
            missionStateDirty = false;
            missionStateMode = (int)world.Options.ScenarioStates;
            selectedMissionStates.Clear();
            foreach (var bubble in scenario.Bubbles)
                selectedMissionStates[bubble.NameHash] = world.Options.StateOrdinals.GetValueOrDefault(bubble.NameHash,
                    bubble.States.FirstOrDefault(state => state.PackageEnabled)?.Ordinal ?? bubble.States.FirstOrDefault()?.Ordinal ?? 0);
        }
        ImGui.SetNextItemWidth(200 * scale);
        if (ImGui.Combo("Preview states", ref missionStateMode,
            ["All authored states", "Package-enabled states", "Selected states"], 3)) missionStateDirty = true;
        ImGui.SameLine();
        ImGui.BeginDisabled(workspace.IsBusy || !missionStateDirty);
        if (ImGui.Button("Apply states"))
        {
            workspace.ReloadWorld(world.Options with
            {
                ScenarioStates = (ScenarioStateMode)missionStateMode,
                StateOrdinals = new Dictionary<uint, int>(selectedMissionStates)
            });
            missionStateDirty = false;
        }
        ImGui.EndDisabled();
        ImGui.TextDisabled($"{scenario.Bubbles.Count} bubbles / {world.AllStates.Count} states");
        ImGui.BeginChild("mission-tree");
        foreach (var bubble in scenario.Bubbles)
        {
            ImGui.PushID(unchecked((int)bubble.NameHash));
            string bubbleName = BubbleTitle(bubble.NameHash, bubble.Name);
            if (ImGui.TreeNodeEx(bubbleName, ImGuiTreeNodeFlags.DefaultOpen))
            {
                bool visible = !hiddenBubbles.Contains(bubble.NameHash);
                if (ImGui.Checkbox("Visible", ref visible))
                { if (visible) hiddenBubbles.Remove(bubble.NameHash); else hiddenBubbles.Add(bubble.NameHash); }
                ImGui.SameLine();
                if (ImGui.SmallButton("Only this bubble"))
                {
                    hiddenBubbles.Clear();
                    foreach (var other in world.Scene.Bubbles.Select(value => world.CanonicalBubble(value.TagHash)).Distinct())
                        if (other != bubble.NameHash) hiddenBubbles.Add(other);
                    focusWorldScene = true;
                }
                if (missionStateMode == (int)ScenarioStateMode.Selected)
                {
                    int selected = selectedMissionStates.GetValueOrDefault(bubble.NameHash);
                    string title = bubble.States.FirstOrDefault(state => state.Ordinal == selected)?.Name ?? "No state";
                    if (ImGui.BeginCombo("State", title))
                    {
                        foreach (var state in bubble.States)
                            if (ImGui.Selectable(StateTitle(state) + "##" + state.Ordinal, state.Ordinal == selected))
                            { selectedMissionStates[bubble.NameHash] = state.Ordinal; missionStateDirty = true; }
                        ImGui.EndCombo();
                    }
                }
                foreach (var state in bubble.States)
                {
                    ImGui.PushID(state.Ordinal);
                    var objects = missionRows.GetValueOrDefault(new(world.MissionTag!.Value, bubble.NameHash, state.Ordinal), []);
                    if (ImGui.TreeNodeEx($"{StateTitle(state)} ({objects.Length:N0})###state"))
                    {
                        var stateKey = new WorldStateKey(world.MissionTag!.Value, state.BubbleNameHash, state.Ordinal);
                        var stateRow = missionStates.GetValueOrDefault(stateKey);
                        if (ImGui.SmallButton("State details") && stateRow is not null) SelectWorldObject(stateRow);
                        ImGui.SameLine();
                        if (ImGui.SmallButton("Open source")) OpenObjectAsset(state.EntryTag);
                        foreach (var group in missionGroups.GetValueOrDefault(stateKey, []))
                            if (ImGui.TreeNodeEx($"{group.Name} ({group.Rows.Length:N0})###{group.Name}"))
                            {
                                if (group.Name == "Spawns") StudioUi.Wip(StudioUi.MayNotWork, sameLine: false);
                                DrawMissionObjects(group.Rows);
                                ImGui.TreePop();
                            }
                        ImGui.TreePop();
                    }
                    ImGui.PopID();
                }
                var shared = sharedMissionRows.GetValueOrDefault(bubble.NameHash, []);
                if (shared.Length != 0 && ImGui.TreeNodeEx($"Shared content ({shared.Length:N0})###shared-content"))
                { DrawMissionObjects(shared); ImGui.TreePop(); }
                ImGui.TreePop();
            }
            ImGui.PopID();
        }
        var unplaced = unplacedMissionRows;
        if (unplaced.Length != 0 && ImGui.TreeNodeEx($"Shared mission objects ({unplaced.Length:N0})###shared-mission-objects"))
        { DrawMissionObjects(unplaced); ImGui.TreePop(); }
        ImGui.EndChild();
    }

    /** A clipped list of rows; a double click frames the selected object. */
    private unsafe void DrawMissionObjects(IReadOnlyList<WorldObject> rows)
    {
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(rows.Count);
        while (clipper.Step())
            for (int index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
            {
                var row = rows[index];
                ImGui.PushID(row.Id);
                if (ImGui.Selectable(row.Name, selectedWorldId == row.Id, ImGuiSelectableFlags.AllowDoubleClick))
                {
                    SelectWorldObject(row);
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && viewport!.SelectedObject is not null)
                    { viewport.FocusSelection(); focusWorldScene = true; }
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{row.BubbleName}\n{row.AssetTag:X8}");
                if (row.Source is SceneMarker { Layer: SceneLayer.Spawns } spawn && ImGui.BeginPopupContextItem("spawn-actions"))
                {
                    DrawSpawnPointActions(spawn);
                    ImGui.EndPopup();
                }
                ImGui.PopID();
            }
        clipper.End(); clipper.Destroy();
    }

    private static string MissionGroup(WorldObject row) => row.Source switch
    {
        SceneMarker marker => marker.Layer.ToString(),
        MeshInstance mesh => mesh.Layer.ToString(),
        AuthoredObjectInfo => "Objects and slots",
        AuthoredSquadInfo => "Squads",
        WorldLight => "Lights",
        _ => row.Kind.ToString()
    };

    private string StateTitle(ScenarioStateInfo state) => workspace.ResolveHash(state.NameHash) ?? state.Name;

    /** Scene counts, environment and map layers, then the names, load, collision and shader reports. */
    private void DrawWorldReport()
    {
        if (workspace.World is not { } world) return;
        ImGui.TextUnformatted($"{world.Scene.Instances.Count:N0} mesh parts / {world.Scene.Markers.Count:N0} markers");
        ImGui.TextUnformatted($"{world.Scene.AuthoredObjects.Count:N0} authored objects / {world.Scene.Squads.Count:N0} squads");
        ImGui.TextUnformatted($"{world.Scene.LightTables.Sum(table => table.Table.Rows.Count):N0} lights");
        DrawWorldRenderSettings();
        DrawMapLayers();
        if (workspace.Names is { } names && ImGui.CollapsingHeader("Names"))
        {
            var coverage = names.Coverage;
            ImGui.TextUnformatted($"{coverage.NamedTags:N0} named assets / {coverage.NamedHashes:N0} named hashes");
            ImGui.TextUnformatted($"{coverage.ContextualTags:N0} contextual / {coverage.AmbiguousTags:N0} ambiguous");
            ImGui.TextDisabled(coverage.Stage);
            foreach (string diagnostic in names.Diagnostics.Take(30)) ImGui.TextWrapped(diagnostic);
        }
        if (ImGui.CollapsingHeader("Load report"))
            foreach (string diagnostic in world.Scene.Diagnostics.Take(100)) ImGui.TextWrapped(diagnostic);
        DrawCollisionReport(viewport!);
        if (ImGui.CollapsingHeader("Shader report"))
        {
            ImGui.TextUnformatted($"{viewport!.NativePartCount:N0} native parts / {viewport.NativeFallbackPartCount:N0} visible fallback parts");
            if (viewport.NativeError is { } error) ImGui.TextWrapped(error);
            if (workspace.NativeError.Length != 0) ImGui.TextWrapped(workspace.NativeError);
            foreach (string diagnostic in viewport.NativeDiagnostics.Take(100)) ImGui.TextWrapped(diagnostic);
        }
    }

    /** Time of day, backdrop, exposure and the active lighting mode. */
    private void DrawWorldRenderSettings()
    {
        if (!ImGui.CollapsingHeader("Environment", ImGuiTreeNodeFlags.DefaultOpen)) return;
        float time = viewport!.TimeOfDay;
        if (ImGui.SliderFloat("Time of day", ref time, 0, 1)) viewport.TimeOfDay = time;
        int backdrop = (int)viewport.Backdrop.Mode;
        if (ImGui.Combo("Backdrop", ref backdrop, ["Follow cursor", "Fixed bubble", "All bubbles"], 3))
            viewport.Backdrop.Mode = (SceneBackdropMode)backdrop;
        if (viewport.Backdrop.Mode == SceneBackdropMode.Fixed && workspace.World is { } world)
        {
            var bubbles = world.Scene.Bubbles.GroupBy(bubble => world.CanonicalBubble(bubble.TagHash)).Select(group => group.First()).ToArray();
            string title = bubbles.FirstOrDefault(bubble => world.CanonicalBubble(bubble.TagHash) == viewport.Backdrop.BubbleHash)?.Name ?? "Choose bubble";
            if (ImGui.BeginCombo("Sky bubble", title))
            {
                foreach (var bubble in bubbles)
                    if (ImGui.Selectable(BubbleTitle(bubble.NameHash, bubble.Name) + "##" + bubble.TagHash)) viewport.Backdrop.BubbleHash = world.CanonicalBubble(bubble.TagHash);
                ImGui.EndCombo();
            }
        }
        if (viewport.NativeRenderer is { } native)
        {
            bool exposure = native.AutoExposure;
            if (ImGui.Checkbox("Auto exposure", ref exposure))
            {
                if (!exposure) native.ViewerStops = native.AppliedViewerStops;
                native.AutoExposure = exposure;
            }
            if (exposure) ImGui.TextDisabled($"Exposure {native.AppliedViewerStops:+0.00;-0.00;0.00} EV");
            else
            {
                float stops = native.ViewerStops;
                if (ImGui.SliderFloat("Exposure", ref stops, -4, 16, "%.2f EV")) native.ViewerStops = stops;
            }
            ImGui.TextDisabled(settings.LightingMode == SunriseStudio.Rendering.NativeLightingMode.Studio
                ? "Studio lighting" : native.SkyLightingActive ? "Atmospheric reflections" : "Ambient lighting");
            if (native.SkyLightingError is { } error && ImGui.IsItemHovered()) ImGui.SetTooltip(error);
        }
    }

    /** A checkbox per authored map layer; Apply reloads the world with the ticked ones. */
    private void DrawMapLayers()
    {
        if (workspace.World is not { } world || world.MapScene.MapLayers.Count == 0
            || !ImGui.CollapsingHeader("Authored map layers")) return;
        if (configuredLayerMap != world.Map.Tag || !ReferenceEquals(configuredLayerOptions, world.Options))
        {
            configuredLayerMap = world.Map.Tag;
            configuredLayerOptions = world.Options;
            selectedMapLayers.Clear();
            foreach (var layer in world.MapScene.MapLayers)
                if (world.Options.MapGlobalIndices is null || world.Options.MapGlobalIndices.Contains(layer.Index)) selectedMapLayers.Add(layer.Index);
        }
        foreach (var layer in world.MapScene.MapLayers)
        {
            bool selected = selectedMapLayers.Contains(layer.Index);
            string label = workspace.ResolveHash(layer.NameHash) ?? layer.Name;
            if (ImGui.Checkbox($"{label}##layer-{layer.Index}", ref selected))
            { if (selected) selectedMapLayers.Add(layer.Index); else selectedMapLayers.Remove(layer.Index); }
        }
        ImGui.BeginDisabled(workspace.IsBusy);
        if (ImGui.Button("Apply map layers")) workspace.ReloadWorld(world.Options with
        { MapGlobalIndices = selectedMapLayers.ToHashSet(), UseMissionMapLayers = false });
        ImGui.EndDisabled();
    }
}
