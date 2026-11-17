using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Scene.Navigation;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using static SunriseStudio.Scene.Navigation.NavMeshReader;

namespace SunriseStudio.Authoring;

/** Adds links across gaps and down drops at the open edges of each baked layer. */
public static class NavLinkBaker
{
    // The layer word of a nav key in every installed link and point row: 0x2000 plus the layer shifted left 10.
    private const uint KeyBase = 0x2000;
    private const int KeyLayerShift = 10;
    // Agent floats read as radius, height and, at index 4, climb; the reading is not checked against code.
    internal const int AgentRadius = 0, AgentHeight = 1, AgentClimb = 4;
    // Probes step outward by half the grid step, from a takeoff point that far inside the edge. Assumed.
    private const float ProbeStep = Step / 2;
    // A probe reaches this far past two agent radii. Assumed.
    private const float ReachPastRadius = 7.5f;
    // The deepest drop a link takes, per layer. Assumed.
    private static readonly float[] Drop = [8.5f, 10, 12.5f];
    // A run of samples ends past this length, or when its landing height or distance moves this much. Assumed.
    private const float RunLength = 8, RunBreak = 1;
    // A link is skipped when the mesh already joins its ends within this many times the jump plus the slack. Assumed.
    private const float DetourFactor = 3, DetourSlack = 8;
    // A link whose corners span at most this height is taken both ways. Assumed.
    private const float BothWaysHeight = 5;
    // Installed link rows hold flags 1 and type 1 in all but 18 rows; the direction byte is 2 or 0.
    private const byte LinkFlags = 1, LinkType = 1;
    internal const byte BothWays = 2, OneWay = 0;
    // Lookup buckets, in metres; any size gives the same links.
    private const float Bucket = 4;
    // A polygon's link list index is a signed 16-bit number.
    private const int MaximumLinkLists = short.MaxValue;

    /** @return The nav key's layer word for `layer`. */
    public static uint Key(int layer) => KeyBase | (uint)layer << KeyLayerShift;

    /**
     * @param positions The source triangles the mesh was baked from, in map space; they block a probe.
     * @param agents Six floats per layer, as a firing point tag stores them.
     * @return The mesh with link rows, link lists and each polygon's list; trees and islands are left to NavMeshWriter.Complete.
     */
    public static NavMesh AddLinks(NavMesh mesh, IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices, float[][] agents)
    {
        var triangles = new Buckets<(Vector3 A, Vector3 B, Vector3 C)>();
        for (int t = 0; t + 2 < indices.Count; t += 3)
        {
            var triangle = (positions[indices[t]], positions[indices[t + 1]], positions[indices[t + 2]]);
            triangles.Add(triangle, Vector3.Min(triangle.Item1, Vector3.Min(triangle.Item2, triangle.Item3)),
                Vector3.Max(triangle.Item1, Vector3.Max(triangle.Item2, triangle.Item3)));
        }
        return new(mesh.Layers.Select(layer => layer.Polygons.Length == 0 ? layer : Layer(layer, triangles, agents[layer.Index])).ToArray());
    }

    private sealed record Sample(int Edge, int Polygon, int Target, bool Gap, float Distance, Vector3 Take, Vector3 Land);

    private static NavLayer Layer(NavLayer layer, Buckets<(Vector3 A, Vector3 B, Vector3 C)> triangles, float[] agent)
    {
        float radius = agent[AgentRadius], height = agent[AgentHeight], climb = agent[AgentClimb];
        float reach = 2 * radius + ReachPastRadius, drop = Drop[layer.Index];
        var polygons = layer.Polygons;
        var planes = polygons.Select(polygon => NavGeometry.Plane(polygon.Vertices.Select(v => layer.Vertices[v]).ToArray())).ToArray();
        var islands = NavGeometry.Islands(polygons);
        var lookup = new Buckets<int>();
        for (int p = 0; p < polygons.Length; p++)
            lookup.Add(p, polygons[p].Vertices.Select(v => layer.Vertices[v]).Aggregate(Vector3.Min),
                polygons[p].Vertices.Select(v => layer.Vertices[v]).Aggregate(Vector3.Max));
        bool Inside(int p, float x, float y) => NavGeometry.Inside(polygons[p].Vertices.Select(v => layer.Vertices[v]).ToArray(), x, y);
        float Height(int p, float x, float y) => planes[p].X * x + planes[p].Y * y + planes[p].Z;

        var samples = new List<Sample?>();
        int edge = 0;
        for (int f = 0; f < polygons.Length; f++)
            for (int e = 0; e < polygons[f].Vertices.Length; e++)
            {
                if (polygons[f].Neighbours[e] >= 0) continue;
                int current = edge++;
                var a = layer.Vertices[polygons[f].Vertices[e]];
                var b = layer.Vertices[polygons[f].Vertices[(e + 1) % polygons[f].Vertices.Length]];
                float dx = b.X - a.X, dy = b.Y - a.Y, length = MathF.Sqrt(dx * dx + dy * dy);
                if (length < 1e-6f) continue;
                // Polygons wind anticlockwise seen from above, so the outward normal is the edge turned right.
                float nx = dy / length, ny = -dx / length;
                int count = length <= Step ? 1 : (int)MathF.Floor((length - Step) / Step + 1e-6f) + 1;
                float start = (length - (count - 1) * Step) / 2;
                for (int s = 0; s < count; s++)
                {
                    float t = (start + s * Step) / length, px = a.X + dx * t, py = a.Y + dy * t;
                    float tx = px - nx * ProbeStep, ty = py - ny * ProbeStep;
                    if (!Inside(f, tx, ty)) { samples.Add(null); continue; }
                    float tz = Height(f, tx, ty);
                    int landing = -1;
                    bool gapLanding = false, empty = false;
                    float landingDistance = 0;
                    var land = Vector3.Zero;
                    for (float distance = ProbeStep; distance <= reach + 1e-6f; distance += ProbeStep)
                    {
                        float x = px + nx * distance, y = py + ny * distance;
                        bool blocked = false, level = false;
                        var from = new Vector2(px + nx * (distance - ProbeStep), py + ny * (distance - ProbeStep));
                        foreach (var triangle in triangles.At(x, y))
                            if (NavGeometry.HeightIn(triangle, x, y) is { } z)
                            {
                                blocked |= z > tz + climb && z < tz + height;
                                level |= MathF.Abs(z - tz) <= climb;
                            }
                        // A wall seen from above is a segment; the step from the last probe point must not cross it.
                        foreach (var triangle in triangles.At(from.X, from.Y).Concat(triangles.At(x, y)))
                            blocked |= NavGeometry.HeightIn(triangle, x, y) is null
                                && NavGeometry.Crosses(triangle, from, new(x, y), tz + climb, tz + height);
                        if (blocked) break;
                        empty |= !level;
                        int best = -1;
                        float bestZ = float.NegativeInfinity;
                        foreach (int p in lookup.At(x, y))
                            if (Inside(p, x, y) && Height(p, x, y) is var z && z <= tz + climb && z > bestZ) (best, bestZ) = (p, z);
                        if (best < 0) continue;
                        float h = tz - bestZ;
                        bool flat = MathF.Abs(h) <= climb;
                        if (islands[best] == islands[f] && flat) continue;
                        // A gap is linked once, from the lower island number; the other side drops what it found.
                        if (flat && empty && islands[best] < islands[f]) landing = -1;
                        bool gap = flat && empty && islands[best] > islands[f];
                        if (gap || h > climb && h <= drop && landing < 0)
                            (landing, gapLanding, landingDistance, land) = (best, gap, distance, new(x, y, bestZ));
                        if (flat) break;
                    }
                    samples.Add(landing < 0 ? null : new(current, f, landing, gapLanding, landingDistance, new(tx, ty, tz), land));
                }
            }

        var runs = new List<List<Sample>>();
        var run = new List<Sample>();
        foreach (var sample in samples)
        {
            if (sample is null) { if (run.Count > 0) runs.Add(run); run = []; continue; }
            if (run.Count > 0)
            {
                var last = run[^1];
                float span = Vector2.Distance(new(sample.Take.X, sample.Take.Y), new(run[0].Take.X, run[0].Take.Y));
                if (sample.Edge != last.Edge || sample.Gap != last.Gap || islands[sample.Target] != islands[last.Target]
                    || MathF.Abs(sample.Land.Z - last.Land.Z) > RunBreak || MathF.Abs(sample.Distance - last.Distance) > RunBreak || span > RunLength)
                { runs.Add(run); run = []; }
            }
            run.Add(sample);
        }
        if (run.Count > 0) runs.Add(run);

        var centres = polygons.Select(polygon => polygon.Vertices.Select(v => layer.Vertices[v]).Aggregate(Vector3.Zero, (sum, v) => sum + v)
            / polygon.Vertices.Length).ToArray();
        bool Detour(int start, HashSet<int> targets, float limit)
        {
            var best = new Dictionary<int, float> { [start] = 0 };
            var queue = new PriorityQueue<int, (float, int)>();
            queue.Enqueue(start, (0, start));
            while (queue.TryDequeue(out int f, out var priority))
            {
                if (targets.Contains(f)) return true;
                if (priority.Item1 > best[f]) continue;
                foreach (int next in polygons[f].Neighbours)
                {
                    if (next < 0) continue;
                    float distance = priority.Item1 + Vector3.Distance(centres[f], centres[next]);
                    if (distance <= limit && distance < best.GetValueOrDefault(next, float.PositiveInfinity))
                    { best[next] = distance; queue.Enqueue(next, (distance, next)); }
                }
            }
            return false;
        }

        var rows = new List<byte[]>();
        var lists = new List<List<int>>();
        int[] slots = Enumerable.Repeat(-1, polygons.Length).ToArray();
        void Link(Vector3 start0, Vector3 start1, Vector3 end0, Vector3 end1, int from, int to, bool both)
        {
            int index = rows.Count;
            byte[] row = new byte[System.Runtime.CompilerServices.Unsafe.SizeOf<C80808c8c>()];
            ref var corners = ref TagView.Edit<C80808c8c>(row);
            (corners.Unk0, corners.Unk4, corners.Unk8) = (start0.X, start0.Y, start0.Z);
            (corners.UnkC, corners.Unk10, corners.Unk14) = (start1.X, start1.Y, start1.Z);
            (corners.Unk18, corners.Unk1c, corners.Unk20) = (end0.X, end0.Y, end0.Z);
            (corners.Unk24, corners.Unk28, corners.Unk2c) = (end1.X, end1.Y, end1.Z);
            row[LinkFlagsAt] = LinkFlags;
            row[LinkDirectionAt] = both ? BothWays : OneWay;
            BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(LinkIndexAt), index);
            row[LinkTypeAt] = LinkType;
            BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(LinkStartKeyAt), Key(layer.Index));
            BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(LinkStartPolygonAt), from);
            BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(LinkEndKeyAt), Key(layer.Index));
            BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(LinkEndPolygonAt), to);
            rows.Add(row);
            foreach (int polygon in new[] { from, to }.Distinct())
            {
                if (slots[polygon] < 0) { slots[polygon] = lists.Count; lists.Add([]); }
                lists[slots[polygon]].Add(index);
            }
        }
        foreach (var group in runs)
        {
            if (group.Count < 2) continue;
            var first = group[0];
            var last = group[^1];
            if (islands[first.Target] == islands[first.Polygon])
            {
                float direct = (group.Aggregate(Vector3.Zero, (sum, p) => sum + p.Take - p.Land) / group.Count).Length();
                if (Detour(first.Polygon, group.Select(p => p.Target).ToHashSet(), DetourFactor * direct + DetourSlack)) continue;
            }
            var zs = group.SelectMany(p => new[] { p.Take.Z, p.Land.Z }).ToArray();
            bool both = first.Gap || zs.Max() - zs.Min() <= BothWaysHeight;
            for (int start = 0; start < group.Count;)
            {
                int end = start;
                while (end + 1 < group.Count && group[end + 1].Target == group[start].Target) end++;
                Link(first.Take, last.Take, group[end].Land, group[start].Land, first.Polygon, group[end].Target, both);
                start = end + 1;
            }
        }
        if (lists.Count > MaximumLinkLists) throw new InvalidDataException("The nav layer has more link lists than a polygon can name.");
        return layer with
        {
            Polygons = polygons.Select((polygon, index) => polygon with { LinkList = slots[index] }).ToArray(),
            Links = rows.ToArray(), LinkLists = lists.Select(list => list.ToArray()).ToArray(), Tree = null, TreeFill = [],
        };
    }

    /** Items bucketed by their XY box. */
    internal sealed class Buckets<T>
    {
        private readonly Dictionary<(int, int), List<T>> cells = [];

        public void Add(T item, Vector3 minimum, Vector3 maximum)
        {
            for (int y = Cell(minimum.Y); y <= Cell(maximum.Y); y++)
                for (int x = Cell(minimum.X); x <= Cell(maximum.X); x++)
                {
                    if (!cells.TryGetValue((x, y), out var list)) cells[(x, y)] = list = [];
                    list.Add(item);
                }
        }

        public IReadOnlyList<T> At(float x, float y) => cells.TryGetValue((Cell(x), Cell(y)), out var list) ? list : [];

        private static int Cell(float value) => (int)MathF.Floor(value / Bucket);
    }
}

/** Plane fits, containment and islands over nav polygons. */
internal static class NavGeometry
{
    /** @return z = X x + Y y + Z fitted to the points by least squares. */
    public static Vector3 Plane(IReadOnlyList<Vector3> points)
    {
        double ox = points.Average(p => p.X), oy = points.Average(p => p.Y), oz = points.Average(p => p.Z);
        double xx = 0, xy = 0, yy = 0, xz = 0, yz = 0;
        foreach (var p in points)
        {
            double x = p.X - ox, y = p.Y - oy, z = p.Z - oz;
            xx += x * x; xy += x * y; yy += y * y; xz += x * z; yz += y * z;
        }
        double determinant = xx * yy - xy * xy;
        double a = Math.Abs(determinant) > 1e-12 ? (xz * yy - yz * xy) / determinant : xx > 1e-12 ? xz / xx : 0;
        double b = Math.Abs(determinant) > 1e-12 ? (yz * xx - xz * xy) / determinant : yy > 1e-12 ? yz / yy : 0;
        return new((float)a, (float)b, (float)(oz - a * ox - b * oy));
    }

    /** True when (x, y) lies inside or on an anticlockwise polygon seen from above. */
    public static bool Inside(IReadOnlyList<Vector3> corners, float x, float y)
    {
        for (int e = 0; e < corners.Count; e++)
        {
            var a = corners[e];
            var b = corners[(e + 1) % corners.Count];
            if ((b.X - a.X) * (y - a.Y) - (x - a.X) * (b.Y - a.Y) < -1e-6f) return false;
        }
        return true;
    }

    /** @return The triangle's height at (x, y), or null when (x, y) is outside it seen from above. */
    public static float? HeightIn((Vector3 A, Vector3 B, Vector3 C) t, float x, float y)
    {
        float determinant = (t.B.Y - t.C.Y) * (t.A.X - t.C.X) + (t.C.X - t.B.X) * (t.A.Y - t.C.Y);
        if (MathF.Abs(determinant) < 1e-9f) return null;
        float w0 = ((t.B.Y - t.C.Y) * (x - t.C.X) + (t.C.X - t.B.X) * (y - t.C.Y)) / determinant;
        float w1 = ((t.C.Y - t.A.Y) * (x - t.C.X) + (t.A.X - t.C.X) * (y - t.C.Y)) / determinant;
        float w2 = 1 - w0 - w1;
        return w0 < -1e-6f || w1 < -1e-6f || w2 < -1e-6f ? null : w0 * t.A.Z + w1 * t.B.Z + w2 * t.C.Z;
    }

    /** True when a wall triangle, seen from above as a segment, crosses the step from `from` to `to` between heights `low` and `high`. */
    public static bool Crosses((Vector3 A, Vector3 B, Vector3 C) t, Vector2 from, Vector2 to, float low, float high)
    {
        if (MathF.Max(t.A.Z, MathF.Max(t.B.Z, t.C.Z)) <= low || MathF.Min(t.A.Z, MathF.Min(t.B.Z, t.C.Z)) >= high) return false;
        Vector2 a = new(t.A.X, t.A.Y), b = new(t.B.X, t.B.Y), c = new(t.C.X, t.C.Y);
        return Intersects(from, to, a, b) || Intersects(from, to, b, c) || Intersects(from, to, c, a);
    }

    private static bool Intersects(Vector2 p, Vector2 q, Vector2 r, Vector2 s)
    {
        static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        float d1 = Cross(r, s, p), d2 = Cross(r, s, q), d3 = Cross(p, q, r), d4 = Cross(p, q, s);
        return (d1 > 0) != (d2 > 0) && (d3 > 0) != (d4 > 0) && d1 != 0 && d2 != 0;
    }

    /** @return Per polygon, the lowest polygon index reached through neighbours. */
    public static int[] Islands(IReadOnlyList<NavPolygon> polygons)
    {
        int[] island = Enumerable.Repeat(-1, polygons.Count).ToArray();
        var queue = new Queue<int>();
        for (int start = 0; start < island.Length; start++)
        {
            if (island[start] >= 0) continue;
            island[start] = start;
            queue.Enqueue(start);
            while (queue.TryDequeue(out int at))
                foreach (int other in polygons[at].Neighbours)
                    if (other >= 0 && island[other] < 0) { island[other] = start; queue.Enqueue(other); }
        }
        return island;
    }
}
