using ImGuiNET;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Viewport;

/** Box and content switches per scene layer, shared by the world scene and the asset view. */
public static class SceneLayerMenu
{
    // Box is the placement marker; content is what the placement draws. Lights draw no content yet.
    private static readonly (SceneLayer Layer, bool Box, bool Content)[] LayerKinds =
    [
        (SceneLayer.Geometry, false, true), (SceneLayer.Terrain, false, true), (SceneLayer.Objects, true, true),
        (SceneLayer.Skybox, false, true), (SceneLayer.Distant, false, true), (SceneLayer.Volumes, true, true),
        (SceneLayer.Effects, true, true), (SceneLayer.Spawns, true, false), (SceneLayer.Squads, true, false),
        (SceneLayer.Lights, true, false), (SceneLayer.Transitions, true, false), (SceneLayer.Collision, true, true),
        (SceneLayer.Navigation, true, true)
    ];

    /**
     * Lists every layer, or only the layers in present when it is given.
     * @param collision The viewport that draws collision; without it the Collision row is left out.
     */
    public static void Draw(SceneVisibility visibility, IReadOnlySet<SceneLayer>? present = null, AssetViewport? collision = null)
    {
        if (!ImGui.BeginTable("layer-choices", 3, ImGuiTableFlags.SizingFixedFit)) return;
        ImGui.TableSetupColumn("Kind");
        ImGui.TableSetupColumn("Box");
        ImGui.TableSetupColumn("Content");
        ImGui.TableHeadersRow();
        foreach (var (layer, box, content) in LayerKinds)
        {
            if (present is not null && !present.Contains(layer)) continue;
            if (layer == SceneLayer.Collision && collision is null) continue;
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(layer.ToString());
            if (layer == SceneLayer.Collision)
            {
                ImGui.TableNextColumn();
                bool boxes = visibility.ShowCollisionBoxes;
                if (ImGui.Checkbox("##box-Collision", ref boxes)) visibility.ShowCollisionBoxes = boxes;
                ImGui.TableNextColumn();
                bool shown = visibility.ShowCollisionContent;
                if (ImGui.Checkbox("##content-Collision", ref shown)) visibility.ShowCollisionContent = shown;
                continue;
            }
            ImGui.TableNextColumn(); if (box) Choice(visibility, layer, $"##box-{layer}", visibility.HiddenOverlayLayers);
            ImGui.TableNextColumn(); if (content) Choice(visibility, layer, $"##content-{layer}", visibility.HiddenGeometryLayers);
        }
        ImGui.EndTable();
        bool inactiveParts = visibility.ShowInactiveParts;
        if (ImGui.Checkbox("Inactive parts", ref inactiveParts)) visibility.ShowInactiveParts = inactiveParts;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Show parts that start hidden or have no material");
        if (collision is not null && (present is null || present.Contains(SceneLayer.Collision))) DrawCollisionOptions(collision);
        if (collision is not null && (present is null || present.Contains(SceneLayer.Navigation)))
        {
            int agent = collision.NavigationAgent;
            if (ImGui.Combo("Navigation radius", ref agent, "0.5 m\01.5 m\03 m\0")) collision.NavigationAgent = agent;
        }
    }

    /** Edge and solid switches, and what the collision layer holds or why it is empty. */
    private static void DrawCollisionOptions(AssetViewport viewport)
    {
        ImGui.Separator();
        bool edges = viewport.CollisionEdges, solid = viewport.CollisionSolid;
        if (ImGui.Checkbox("Collision edges", ref edges)) viewport.CollisionEdges = edges;
        if (ImGui.Checkbox("Solid collision", ref solid)) viewport.CollisionSolid = solid;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Opaque faces that hide what is behind them; triggers stay translucent");
        bool inactive = viewport.ShowInactiveCollision;
        if (ImGui.Checkbox("Inactive bodies", ref inactive)) viewport.ShowInactiveCollision = inactive;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Show bodies that start disabled");
        foreach (var role in Enum.GetValues<CollisionRole>())
        {
            bool shown = !viewport.HiddenCollisionRoles.Contains(role);
            if (role != default) ImGui.SameLine();
            if (ImGui.Checkbox($"{role}##collision-role", ref shown))
            { if (shown) viewport.HiddenCollisionRoles.Remove(role); else viewport.HiddenCollisionRoles.Add(role); }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(role switch
                {
                    CollisionRole.World => "Blocks movement",
                    CollisionRole.Hitbox => "Takes weapon hits",
                    CollisionRole.Trigger => "Detects entry",
                    _ => "Other shapes"
                });
        }
        if (viewport.CollisionStatus.Length > 0) ImGui.TextDisabled(viewport.CollisionStatus);
        if (viewport.Collision is not { } scene) return;
        ImGui.TextDisabled($"{scene.Placements.Count:N0} shapes placed / {scene.Pieces.Count:N0} distinct");
        ImGui.TextDisabled($"{viewport.VisibleCollisionCount:N0} drawn / {viewport.VisibleCollisionTriangles:N0} triangles");
        if (scene.Undecoded.Count > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip("No triangles: " + string.Join(", ", scene.Undecoded.Select(item => $"{item.Key} {item.Value}")));
    }

    /** The layers a scene holds, from its meshes and markers. */
    public static IReadOnlySet<SceneLayer> Present(SceneDocument scene)
    {
        var layers = new HashSet<SceneLayer>();
        foreach (var instance in scene.Instances) layers.Add(instance.Layer);
        foreach (var marker in scene.Markers) layers.Add(marker.Layer);
        if (scene.Navigation.Count > 0) layers.Add(SceneLayer.Navigation);
        return layers;
    }

    private static void Choice(SceneVisibility visibility, SceneLayer layer, string label, HashSet<SceneLayer> hidden)
    {
        ImGui.PushID(label);
        bool visible = !hidden.Contains(layer) && !visibility.HiddenLayers.Contains(layer);
        if (ImGui.Checkbox(label, ref visible))
        {
            visibility.HiddenLayers.Remove(layer);
            if (visible) hidden.Remove(layer); else hidden.Add(layer);
        }
        ImGui.PopID();
    }
}
