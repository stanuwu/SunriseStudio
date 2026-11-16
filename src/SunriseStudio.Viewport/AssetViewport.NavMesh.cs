using System.Numerics;
using System.Runtime.InteropServices;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

public sealed partial class AssetViewport
{
    private SceneDocument? navMeshScene;
    private readonly Dictionary<(PlacedNavMesh, int), LineVertex[]> navMeshLines = [];
    // A firing point draws as an upright tick this tall, in metres.
    private const float FiringPointTick = 1;
    private static readonly Vector4[] NavMeshColours = [new(.2f, 1, .5f, 1), new(.3f, .65f, 1, 1), new(1, .65f, .2f, 1)];

    /** The nav mesh layer the overlay draws: 0, 1 or 2. */
    public int NavMeshAgent { get; set; }

    /** Draws one layer of each placed nav mesh: its polygon edges and its firing points. Lines are built once per scene. */
    private void DrawNavMesh(Matrix4x4 view)
    {
        if (!ReferenceEquals(navMeshScene, Scene))
        {
            navMeshScene = Scene;
            navMeshLines.Clear();
        }
        overlayLines.Clear();
        int agent = Math.Clamp(NavMeshAgent, 0, NavMeshColours.Length - 1);
        foreach (var placement in Scene!.NavMeshes)
        {
            if (!Visibility.IsContentVisible(placement.Marker)) continue;
            if (!navMeshLines.TryGetValue((placement, agent), out var prepared))
                navMeshLines[(placement, agent)] = prepared = Lines(placement, agent);
            overlayLines.AddRange(prepared);
        }
        DrawOverlayLines(LineStream.NavMesh, CollectionsMarshal.AsSpan(overlayLines), view, OccludeOverlays);
    }

    private static LineVertex[] Lines(PlacedNavMesh placement, int agent)
    {
        var lines = new List<LineVertex>();
        var colour = NavMeshColours[agent];
        var layer = placement.Mesh.Layers[agent];
        var edges = new HashSet<(int, int)>();
        foreach (var polygon in layer.Polygons)
            for (int k = 0; k < polygon.Vertices.Length; k++)
            {
                int a = polygon.Vertices[k], b = polygon.Vertices[(k + 1) % polygon.Vertices.Length];
                if (!edges.Add((Math.Min(a, b), Math.Max(a, b)))) continue;
                lines.Add(new(Vector3.Transform(layer.Vertices[a], placement.Transform), colour));
                lines.Add(new(Vector3.Transform(layer.Vertices[b], placement.Transform), colour));
            }
        foreach (var point in placement.FiringPoints.Layers[agent].Points)
        {
            lines.Add(new(Vector3.Transform(point.Position, placement.Transform), colour));
            lines.Add(new(Vector3.Transform(point.Position + new Vector3(0, 0, FiringPointTick), placement.Transform), colour));
        }
        return lines.ToArray();
    }
}
