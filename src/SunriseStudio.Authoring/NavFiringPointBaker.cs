using System.Numerics;
using SunriseStudio.Scene.Navigation;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using static SunriseStudio.Scene.Navigation.NavMeshReader;

namespace SunriseStudio.Authoring;

/** Places static firing points and their clusters on a baked nav mesh, and a link row per nav link. */
public static class NavFiringPointBaker
{
    // A point's metric by its distance in metres from the nearest open edge, per layer, as (distance, metric) knots. Assumed.
    private static readonly (float Distance, float Metric)[][] MetricCurves =
    [
        [(.25f, 2), (.7f, 2), (1.25f, 4), (1.76f, 5), (2.25f, 6), (2.75f, 7), (3.47f, 8.5f), (4.43f, 10.5f), (5.42f, 12), (6.75f, 13)],
        [(.25f, 4), (.74f, 4.5f), (1.41f, 5), (1.59f, 6), (2.25f, 8), (2.76f, 9), (3.47f, 11), (4.43f, 13), (5.45f, 15)],
        [(.24f, 8), (1.23f, 8), (1.72f, 8.5f), (2.25f, 10), (2.86f, 11), (3.18f, 12.5f), (4.43f, 16), (5.48f, 18), (6.75f, 20.5f)],
    ];
    // Metrics are kept to half steps up to this. Assumed.
    private const float MetricStep = .5f, MaximumMetric = 32;
    // The fewest metres between two points, per layer. Assumed.
    private static readonly float[] Spacing = [2.4f, 3.9f, 4.6f];
    // The furthest a cluster member lies from its seed, per layer. Assumed.
    private static readonly float[] ClusterReach = [5.5f, 8, 9];
    // Installed point normals are 2 pi long.
    private const float NormalLength = MathF.Tau;
    // Installed points carry flags 0x208, or 0x20C on many; 0x20C is taken to mark a cluster's first member. Assumed.
    private const uint PointFlags = 0x208, RepresentativeFlags = 0x20C;
    // Every installed link row keys 0x6000 plus the layer shifted left 10.
    private const uint LinkKeyBase = 0x6000;
    private const int LinkKeyLayerShift = 10;
    // The commonest installed pair after a link row's height; their meaning is unread. Assumed.
    private const float LinkUnknown12 = 1;
    private static readonly float LinkUnknown16 = BitConverter.Int32BitsToSingle(0x3E033333);
    // Direction word 3 for a link taken both ways, else 1. Assumed: installed rows agree with the link's direction byte in two of three.
    private const uint BothWays = 3, OneWay = 1;

    /**
     * @param mesh A completed mesh: its polygons carry islands.
     * @param agents Six floats per layer, as a firing point tag stores them.
     */
    public static NavFiringPoints Bake(NavMesh mesh, float[][] agents) =>
        new(mesh.Layers.Select(layer => Layer(layer, agents[layer.Index])).ToArray());

    private static NavFiringLayer Layer(NavLayer layer, float[] agent)
    {
        if (layer.Polygons.Length == 0) return new(layer.Index, agent, [], [], []);
        var points = Points(layer, agent);
        var clusters = Clusters(layer, points, agent[NavLinkBaker.AgentRadius]);
        foreach (var cluster in clusters)
            points[cluster.Members[cluster.Representative].Row] = points[cluster.Members[cluster.Representative].Row] with { Flags = RepresentativeFlags };
        return new(layer.Index, agent, points.ToArray(), clusters, layer.Links.Select((link, index) => Link(link, index, layer.Index)).ToArray());
    }

    private static List<NavFiringPoint> Points(NavLayer layer, float[] agent)
    {
        var curve = MetricCurves[layer.Index];
        float reach = curve[^1].Distance, height = agent[NavLinkBaker.AgentHeight];
        // Open edges, bucketed by box, for the distance to the nearest one.
        var edges = new NavLinkBaker.Buckets<(Vector3 A, Vector3 B)>();
        foreach (var polygon in layer.Polygons)
            for (int e = 0; e < polygon.Vertices.Length; e++)
                if (polygon.Neighbours[e] < 0)
                {
                    var a = layer.Vertices[polygon.Vertices[e]];
                    var b = layer.Vertices[polygon.Vertices[(e + 1) % polygon.Vertices.Length]];
                    edges.Add((a, b), Vector3.Min(a, b) - new Vector3(reach), Vector3.Max(a, b) + new Vector3(reach));
                }
        float Metric(Vector3 at)
        {
            float distance = reach;
            foreach (var (a, b) in edges.At(at.X, at.Y))
                if (MathF.Abs((a.Z + b.Z) / 2 - at.Z) <= height) distance = MathF.Min(distance, Segment(at, a, b));
            float value = curve[^1].Metric;
            if (distance < curve[0].Distance) value = curve[0].Metric;
            else
                for (int k = 1; k < curve.Length; k++)
                    if (distance < curve[k].Distance)
                    {
                        var (low, high) = (curve[k - 1], curve[k]);
                        value = low.Metric + (high.Metric - low.Metric) * (distance - low.Distance) / (high.Distance - low.Distance);
                        break;
                    }
            return Math.Clamp(MathF.Round(value / MetricStep) * MetricStep, 0, MaximumMetric);
        }
        // One candidate per grid cell centre inside each polygon.
        var candidates = new List<(Vector3 Position, Vector3 Normal, int Polygon, float Metric)>();
        for (int p = 0; p < layer.Polygons.Length; p++)
        {
            var corners = layer.Polygons[p].Vertices.Select(v => layer.Vertices[v]).ToArray();
            var plane = NavGeometry.Plane(corners);
            var low = corners.Aggregate(Vector3.Min);
            var high = corners.Aggregate(Vector3.Max);
            var normal = Vector3.Normalize(new(-plane.X, -plane.Y, 1)) * NormalLength;
            int x0 = (int)MathF.Floor((low.X - layer.Minimum.X) / Step), x1 = (int)MathF.Ceiling((high.X - layer.Minimum.X) / Step);
            int y0 = (int)MathF.Floor((low.Y - layer.Minimum.Y) / Step), y1 = (int)MathF.Ceiling((high.Y - layer.Minimum.Y) / Step);
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    float px = layer.Minimum.X + (x + .5f) * Step, py = layer.Minimum.Y + (y + .5f) * Step;
                    if (!NavGeometry.Inside(corners, px, py)) continue;
                    var at = new Vector3(px, py, plane.X * px + plane.Y * py + plane.Z);
                    candidates.Add((at, normal, p, Metric(at)));
                }
        }
        float spacing = Spacing[layer.Index];
        var taken = new NavLinkBaker.Buckets<Vector3>();
        var points = new List<NavFiringPoint>();
        foreach (var candidate in candidates.OrderByDescending(c => c.Metric).ThenBy(c => c.Position.Y).ThenBy(c => c.Position.X))
        {
            var xy = new Vector2(candidate.Position.X, candidate.Position.Y);
            if (taken.At(xy.X, xy.Y).Any(other => Vector2.DistanceSquared(new(other.X, other.Y), xy) < spacing * spacing)) continue;
            taken.Add(candidate.Position, candidate.Position - new Vector3(spacing), candidate.Position + new Vector3(spacing));
            points.Add(new(candidate.Normal, NavLinkBaker.Key(layer.Index), candidate.Polygon, candidate.Position, candidate.Metric) { Flags = PointFlags });
        }
        return points;
    }

    /** Groups each point with the untaken points of its island within reach; the first member stands for the group. */
    private static NavFiringCluster[] Clusters(NavLayer layer, List<NavFiringPoint> points, float radius)
    {
        float reach = ClusterReach[layer.Index];
        var near = new NavLinkBaker.Buckets<int>();
        for (int i = 0; i < points.Count; i++) near.Add(i, points[i].Position - new Vector3(reach), points[i].Position + new Vector3(reach));
        bool[] taken = new bool[points.Count];
        var clusters = new List<NavFiringCluster>();
        for (int i = 0; i < points.Count; i++)
        {
            if (taken[i]) continue;
            var seed = points[i];
            uint island = layer.Polygons[seed.Polygon].Island;
            var members = near.At(seed.Position.X, seed.Position.Y).Where(j => !taken[j] && layer.Polygons[points[j].Polygon].Island == island
                && Vector2.DistanceSquared(new(points[j].Position.X, points[j].Position.Y), new(seed.Position.X, seed.Position.Y)) <= reach * reach)
                .Order().ToArray();
            foreach (int j in members) taken[j] = true;
            var low = members.Select(j => points[j].Position).Aggregate(Vector3.Min);
            var high = members.Select(j => points[j].Position).Aggregate(Vector3.Max);
            var centre = (low + high) / 2;
            float extent = members.Max(j => Vector2.Distance(new(points[j].Position.X, points[j].Position.Y), new(centre.X, centre.Y)));
            clusters.Add(new(centre, extent + radius, low.Z, high.Z, members.Select(j => (0u, j)).ToArray(), 0));
        }
        return clusters.ToArray();
    }

    /** A link row: its key, the link, and the span of the link's corners on Z. */
    private static NavFiringLink Link(byte[] link, int index, int layer)
    {
        ref readonly var corners = ref TagView.At<C80808c8c>(link);
        float[] zs = [corners.Unk8, corners.Unk14, corners.Unk20, corners.Unk2c];
        return new(LinkKeyBase | (uint)layer << LinkKeyLayerShift, index, zs.Max() - zs.Min(), LinkUnknown12, LinkUnknown16,
            link[LinkDirectionAt] == NavLinkBaker.BothWays ? BothWays : OneWay);
    }

    private static float Segment(Vector3 point, Vector3 a, Vector3 b)
    {
        Vector2 p = new(point.X, point.Y), s = new(a.X, a.Y), e = new(b.X, b.Y);
        var d = e - s;
        float t = Math.Clamp(Vector2.Dot(p - s, d) / MathF.Max(1e-12f, d.LengthSquared()), 0, 1);
        return Vector2.Distance(p, s + t * d);
    }
}
