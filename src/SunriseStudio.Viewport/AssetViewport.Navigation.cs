using System.Numerics;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

public sealed partial class AssetViewport
{
    private SceneDocument? navigationScene;
    private readonly Dictionary<(PlacedNavigation, int), LineVertex[]> navigationLines = [];
    public int NavigationAgent { get; set; }

    /** Navigation outlines are prepared per scene and use the layer's content and bubble visibility. */
    private void DrawNavigation(Matrix4x4 view)
    {
        if (!ReferenceEquals(navigationScene, Scene))
        {
            navigationScene = Scene; navigationLines.Clear();
        }
        overlayLines.Clear();
        foreach (var placement in Scene!.Navigation)
        {
            if (!Visibility.IsContentVisible(placement.Marker)) continue;
            int agent = Math.Clamp(NavigationAgent, 0, 2);
            if (!navigationLines.TryGetValue((placement, agent), out var prepared))
            {
                var lines = new List<LineVertex>();
                Vector4[] colours = [new(.2f, 1, .5f, 1), new(.3f, .65f, 1, 1), new(1, .65f, .2f, 1)];
                foreach (var layer in placement.Mesh.Layers.Where(l => l.Index == agent))
                {
                    var edges = new HashSet<(uint, uint)>();
                    foreach (var face in layer.Faces)
                        for (int k = 0; k < face.Vertices.Length; k++)
                        {
                            uint a = face.Vertices[k], b = face.Vertices[(k + 1) % face.Vertices.Length];
                            if (!edges.Add((Math.Min(a, b), Math.Max(a, b)))) continue;
                            lines.Add(new(Vector3.Transform(layer.Vertices[a], placement.Transform), colours[layer.Index]));
                            lines.Add(new(Vector3.Transform(layer.Vertices[b], placement.Transform), colours[layer.Index]));
                        }
                }
                navigationLines[(placement, agent)] = prepared = lines.ToArray();
            }
            overlayLines.AddRange(prepared);
        }
        DrawOverlayLines(LineStream.Navigation, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(overlayLines), view, OccludeOverlays);
    }
}
