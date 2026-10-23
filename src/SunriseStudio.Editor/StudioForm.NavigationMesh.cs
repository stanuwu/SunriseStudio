using ImGuiNET;
using SunriseStudio.Authoring;
using SunriseStudio.Scene;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    /** Each bubble offers its collision or a placed authored terrain/static ground as the source. */
    private void DrawGenerateNavigation(BubbleInfo bubble)
    {
        if (!ImGui.BeginMenu($"Generate navigation##{bubble.TagHash:X8}", workspace.CanEdit)) return;
        StudioUi.Wip(NavigationPlacement.Limitations);
        if (ImGui.MenuItem("Bubble collision")) Generate();
        if (workspace.World is { } world && workspace.Project is { } project)
        {
            var tags = world.Scene.Instances.Where(i => i.BubbleHash == bubble.TagHash
                && i.Layer is SceneLayer.Terrain or SceneLayer.Geometry).Select(i => i.Mesh.TagHash).ToHashSet();
            foreach (var asset in project.AddedAssets.Where(a => a.Native is { } n && tags.Contains(n.Tag())))
                if (ImGui.MenuItem($"Ground: {asset.Name}##{asset.Id}"))
                    Generate(asset.Native!.Tag());
        }
        ImGui.EndMenu();
        void Generate(uint? ground = null)
        {
            workspace.Run(() => workspace.GenerateNavigation(bubble.TagHash, ground));
            viewport!.Visibility.HiddenGeometryLayers.Remove(SceneLayer.Navigation);
            viewport.Visibility.HiddenLayers.Remove(SceneLayer.Navigation);
        }
    }
}
