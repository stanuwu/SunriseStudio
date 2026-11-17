using System.Numerics;
using ImGuiNET;
using SunriseStudio.Authoring;
using SunriseStudio.Scene;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    /** A new spawn point copies this marker's row, name included, and takes only a new pose. */
    private void DrawSpawnPointActions(SceneMarker? marker)
    {
        if (marker?.Layer != SceneLayer.Spawns || workspace.World is null) return;
        ImGui.TextUnformatted("Spawn points");
        ImGui.BeginDisabled(!workspace.CanEdit);
        if (ImGui.Button("Add spawn point here")) AddSpawnPoint(marker, viewport!.Camera.Pivot, Quaternion.Identity);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Adds a copy of this spawn point at the 3D cursor.\n" + StudioUi.MayNotWork);
        if (ImGui.Button("Add at camera")) AddSpawnPoint(marker, viewport!.Camera.Position, Quaternion.Identity);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Adds a copy of this spawn point at the camera.\n" + StudioUi.MayNotWork);
        if (ImGui.Button("Duplicate spawn point")) AddSpawnPoint(marker, marker.Position, marker.Rotation);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Adds a copy of this spawn point in the same place.\n" + StudioUi.MayNotWork);
        ImGui.EndDisabled();
    }

    private void AddSpawnPoint(SceneMarker marker, Vector3 position, Quaternion rotation)
    {
        var world = inspectionWorld;
        workspace.Run(() => workspace.ApplyObjectEdit(SceneEdits.CreateSpawnPointEdit(workspace.Catalog!, marker,
            workspace.Project!.ObjectEdits, position, rotation, world, marker.Name), "Added spawn point"));
    }

    /** Spawn rows and the mission nodes above them offer the same actions as the Inspector. */
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
