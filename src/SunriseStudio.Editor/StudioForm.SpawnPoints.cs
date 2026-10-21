using System.Numerics;
using ImGuiNET;
using SunriseStudio.Authoring;
using SunriseStudio.Scene;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    /** Arrival additions inherit the selected native name; routing remains owned by the mission. */
    private void DrawSpawnPointActions(SceneMarker? marker)
    {
        if (marker?.Layer != SceneLayer.Spawns || workspace.World is null) return;
        ImGui.TextUnformatted("Arrival points");
        StudioUi.Wip(StudioUi.MayNotWork);
        ImGui.TextWrapped("May not work in game.");
        ImGui.BeginDisabled(!workspace.CanEdit);
        if (ImGui.Button("Add spawn point here")) AddSpawnPoint(marker, viewport!.Camera.Pivot, Quaternion.Identity);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Add an arrival at the 3D cursor with this spawn name hash.");
        if (ImGui.Button("Add at camera")) AddSpawnPoint(marker, viewport!.Camera.Position, Quaternion.Identity);
        if (ImGui.Button("Duplicate spawn point")) AddSpawnPoint(marker, marker.Position, marker.Rotation);
        ImGui.EndDisabled();
    }

    private void AddSpawnPoint(SceneMarker marker, Vector3 position, Quaternion rotation) => workspace.Run(() =>
        workspace.ApplyObjectEdit(SpawnPointAuthoring.Create(workspace.Catalog!, workspace.Project!.ObjectEdits,
            marker.SourceTag, position, rotation, marker.NameHash), "Added arrival point"));

    /** Spawn rows and their containing mission tree expose the same actions as the Inspector. */
    private void DrawSpawnTreeMenu(SceneHierarchyNode? node)
    {
        if (node is null || workspace.World is null) return;
        if (node.Representative?.Source is SceneMarker { Layer: SceneLayer.Spawns } marker)
        {
            if (!ImGui.BeginPopupContextItem("spawn-actions")) return;
            DrawSpawnPointActions(marker);
            ImGui.EndPopup();
        }
        else if (node.Kind == SceneHierarchyKind.Mission || node.Name == "Spawns")
        {
            if (!ImGui.BeginPopupContextItem("spawn-banks")) return;
            var spawns = Descendants(node).Select(child => child.Representative?.Source)
                .OfType<SceneMarker>().Where(item => item.Layer == SceneLayer.Spawns)
                .DistinctBy(item => (item.SourceTag, item.NameHash)).ToArray();
            if (spawns.Length == 0) ImGui.TextDisabled("No spawn bank in this mission");
            foreach (var spawn in spawns)
                if (ImGui.BeginMenu($"{spawn.Name} ({spawn.SourceTag:X8})"))
                { DrawSpawnPointActions(spawn); ImGui.EndMenu(); }
            ImGui.EndPopup();
        }
    }

    private static IEnumerable<SceneHierarchyNode> Descendants(SceneHierarchyNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in Descendants(child)) yield return descendant;
    }
}
