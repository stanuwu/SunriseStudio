using System.Numerics;
using SunriseStudio.Scene;

namespace SunriseStudio.Authoring;

public sealed record NavigationTriangle(Vector3 A, Vector3 B, Vector3 C);
public sealed record NavigationGeometry(IReadOnlyList<NavigationTriangle> Triangles, bool Solid = false);
public sealed record GeneratedNavigation(byte[] Root, byte[] Mesh, byte[] Graph);

/** Half-metre spans use the World Studio slope, clearance, ledge, step and radius rules. */
public static partial class NavigationGenerator
{
    private const double Cell = .5, Step = .9;
    private static readonly double[][] Agents = [[.5, 2.75, 35, 1, .9, 1], [1.5, 4.25, 30, 4, 2.5, 2.5], [3, 7.5, 20, 4, 2.5, 2.5]];
    private static readonly (int X, int Y)[] Directions = [(-1, 0), (0, 1), (1, 0), (0, -1)];
    private sealed record Surface(double Bottom, double Top, double Nz, double X, double Y, double Z)
    {
        public double Height(double x, double y) => X * x + Y * y + Z;
    }
    private sealed class Span(int x, int y, Surface surface)
    {
        public int X = x, Y = y;
        public double Bottom = surface.Bottom, Top = surface.Top, Ceiling = double.PositiveInfinity;
        public Surface Surface = surface;
        public List<Surface> Contributors = [surface];
    }
    private sealed record Grid(double X, double Y, int Width, int Height, double Floor,
        List<Span> Spans, Dictionary<(int, int), List<int>> Columns);
    private sealed record Point((double X, double Y, double Z) Position, (double X, double Y, double Z) Normal, float Metric, int Face)
    { public bool Representative { get; set; } }
    private sealed record Cluster(int[] Members, (double X, double Y, double Z) Centre, double Radius, double MinimumZ, double MaximumZ);
    private sealed record Link(Vector3 Start0, Vector3 Start1, Vector3 End0, Vector3 End1, int Start, int End, bool Both);
    private sealed record Layer(Vector3[] Vertices, int[][] Faces, int[][] Neighbours, int[] Components,
        Vector3 Minimum, Vector3 Maximum, Point[] Points, Cluster[] Clusters, Link[] Links);

    /** Input winding is significant: upward faces walk, downward faces bound solid intervals. */
    public static GeneratedNavigation Generate(IReadOnlyList<NavigationGeometry> geometry, CancellationToken cancellation = default)
    {
        var grid = Rasterize(geometry, cancellation);
        var layers = Enumerable.Range(0, 3).Select(i => BuildLayer(grid, i, cancellation)).ToArray();
        var output = Write(layers);
        _ = NavigationDecoder.Decode(output.Root, output.Mesh, output.Graph);
        return output;
    }

    private static Grid Rasterize(IReadOnlyList<NavigationGeometry> geometry, CancellationToken cancellation)
    {
        var all = geometry.SelectMany(g => g.Triangles).ToArray();
        Require(all.Length <= 2_000_000, "Navigation source exceeds two million triangles.");
        var min = new Vector3(float.PositiveInfinity); var max = -min;
        foreach (var t in all)
            foreach (var p in new[] { t.A, t.B, t.C })
            {
                Require(float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z), "Navigation source is not finite.");
                min = Vector3.Min(min, p); max = Vector3.Max(max, p);
            }
        if (all.Length == 0) return new(0, 0, 1, 1, -10, [], []);
        double x0 = Math.Floor(min.X / 32d) * 32, y0 = Math.Floor(min.Y / 32d) * 32;
        double width = Math.Ceiling((max.X - x0) / Cell) + 1, height = Math.Ceiling((max.Y - y0) / Cell) + 1;
        Require(width * height <= 4_000_000, "Navigation source exceeds four million raster columns. Choose a smaller ground asset.");
        var contributions = new Dictionary<(int X, int Y), List<Surface>>();
        void Add(int x, int y, Surface surface)
        {
            if (!contributions.TryGetValue((x, y), out var values)) contributions[(x, y)] = values = [];
            values.Add(surface);
        }
        foreach (var g in geometry)
        {
            var hits = new Dictionary<(int X, int Y, int Sample), List<Surface>>();
            foreach (var t in g.Triangles)
            {
                cancellation.ThrowIfCancellationRequested();
                // Double arithmetic keeps sub-sample boundary decisions independent of float SIMD rounding.
                double ux = t.B.X - (double)t.A.X, uy = t.B.Y - (double)t.A.Y, uz = t.B.Z - (double)t.A.Z;
                double vx = t.C.X - (double)t.A.X, vy = t.C.Y - (double)t.A.Y, vz = t.C.Z - (double)t.A.Z;
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (length < 1e-12) continue;
                nx /= length; ny /= length; nz /= length;
                var lo = Vector3.Min(t.A, Vector3.Min(t.B, t.C)); var hi = Vector3.Max(t.A, Vector3.Max(t.B, t.C));
                bool vertical = Math.Abs(nz) < .05;
                if (vertical && !g.Solid) continue;
                double margin = vertical ? Cell : 0;
                int left = Math.Max(0, (int)Math.Floor((lo.X - margin - x0) / Cell));
                int right = Math.Min((int)width - 1, (int)Math.Floor((hi.X + margin - x0) / Cell));
                int bottom = Math.Max(0, (int)Math.Floor((lo.Y - margin - y0) / Cell));
                int top = Math.Min((int)height - 1, (int)Math.Floor((hi.Y + margin - y0) / Cell));
                for (int y = bottom; y <= top; y++)
                    for (int x = left; x <= right; x++)
                    {
                        if (vertical)
                        {
                            double cx = x0 + (x + .5) * Cell, cy = y0 + (y + .5) * Cell;
                            if (Math.Min(Distance(cx, cy, t.A, t.B), Math.Min(Distance(cx, cy, t.B, t.C), Distance(cx, cy, t.C, t.A))) <= Cell * .7072)
                                Add(x, y, new(lo.Z, hi.Z, nz, 0, 0, hi.Z));
                            continue;
                        }
                        for (int a = 0; a < 3; a++)
                            for (int b = 0; b < 3; b++)
                            {
                                double px = x0 + (x + (a * 2 + 1) / 6d) * Cell, py = y0 + (y + (b * 2 + 1) / 6d) * Cell;
                                double determinant = (t.B.Y - (double)t.C.Y) * (t.A.X - (double)t.C.X) + (t.C.X - (double)t.B.X) * (t.A.Y - (double)t.C.Y);
                                double w0 = ((t.B.Y - (double)t.C.Y) * (px - t.C.X) + (t.C.X - (double)t.B.X) * (py - t.C.Y)) / determinant;
                                double w1 = ((t.C.Y - (double)t.A.Y) * (px - t.C.X) + (t.A.X - (double)t.C.X) * (py - t.C.Y)) / determinant;
                                if (w0 < -1e-9 || w1 < -1e-9 || 1 - w0 - w1 < -1e-9) continue;
                                double sx = -nx / nz, sy = -ny / nz, sz = t.A.Z - sx * t.A.X - sy * t.A.Y;
                                double z = sx * px + sy * py + sz;
                                var surface = new Surface(z, z, nz, sx, sy, sz);
                                Add(x, y, surface);
                                if (!g.Solid) continue;
                                var key = (x, y, a * 3 + b);
                                if (!hits.TryGetValue(key, out var samples)) hits[key] = samples = [];
                                samples.Add(surface);
                            }
                    }
            }
            foreach (var (key, samples) in hits)
            {
                samples.Sort((a, b) => a.Top.CompareTo(b.Top));
                for (int i = 0; i + 1 < samples.Count; i++)
                    if (samples[i].Nz < 0) Add(key.X, key.Y, samples[i + 1] with { Bottom = samples[i].Top });
            }
        }
        var spans = new List<Span>(); var columns = new Dictionary<(int, int), List<int>>();
        foreach (var (key, values) in contributions.OrderBy(p => p.Key.Y).ThenBy(p => p.Key.X))
        {
            var indices = new List<int>();
            foreach (var value in values.OrderBy(v => v.Bottom))
            {
                Span? previous = indices.Count > 0 ? spans[indices[^1]] : null;
                if (previous is null || value.Bottom > previous.Top + 1e-9)
                { indices.Add(spans.Count); spans.Add(new(key.X, key.Y, value)); }
                else
                {
                    previous.Contributors.Add(value);
                    if (value.Top > previous.Top + 1e-9) { previous.Top = value.Top; previous.Surface = value; }
                }
            }
            for (int i = 0; i + 1 < indices.Count; i++) spans[indices[i]].Ceiling = spans[indices[i + 1]].Bottom;
            columns[key] = indices;
        }
        Require(spans.Count <= 2_000_000, "Navigation source exceeds two million spans.");
        return new(x0, y0, (int)width, (int)height, min.Z - 10, spans, columns);
    }

    private static Layer? BuildLayer(Grid grid, int layer, CancellationToken cancellation)
    {
        var spans = grid.Spans; int n = spans.Count; var agent = Agents[layer];
        double slope = Math.Cos(agent[2] * Math.PI / 180), clearance = agent[1];
        bool[] original = spans.Select(s => s.Contributors.Any(c => c.Nz >= slope - 1e-7 && c.Top >= s.Top - Step)).ToArray();
        var walk = original.ToArray();
        foreach (var indices in grid.Columns.Values)
            for (int k = 1; k < indices.Count; k++)
                if (original[indices[k - 1]] && !walk[indices[k]] && spans[indices[k]].Top - spans[indices[k - 1]].Top <= Step)
                    walk[indices[k]] = true;
        for (int i = 0; i < n; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var s = spans[i]; double lowest = double.PositiveInfinity, nearMin = s.Top, nearMax = s.Top;
            foreach (var (dx, dy) in Directions)
            {
                var neighbours = grid.Columns.GetValueOrDefault((s.X + dx, s.Y + dy));
                double first = neighbours is { Count: > 0 } ? spans[neighbours[0]].Bottom : double.PositiveInfinity;
                double floor = grid.Floor - Step;
                if (Math.Min(s.Ceiling, first) - Math.Max(s.Top, floor) > clearance) lowest = Math.Min(lowest, floor - s.Top);
                foreach (int j in neighbours ?? [])
                {
                    var t = spans[j];
                    if (Math.Min(s.Ceiling, t.Ceiling) - Math.Max(s.Top, t.Top) <= clearance) continue;
                    lowest = Math.Min(lowest, t.Top - s.Top);
                    if (Math.Abs(t.Top - s.Top) <= Step) { nearMin = Math.Min(nearMin, t.Top); nearMax = Math.Max(nearMax, t.Top); }
                }
            }
            walk[i] &= lowest >= -Step && nearMax - nearMin <= Step && s.Ceiling - s.Top >= clearance;
        }
        var connections = Enumerable.Range(0, n).Select(_ => new[] { -1, -1, -1, -1 }).ToArray();
        for (int i = 0; i < n; i++)
            for (int d = 0; d < 4; d++)
            {
                var s = spans[i]; var (dx, dy) = Directions[d];
                foreach (int j in grid.Columns.GetValueOrDefault((s.X + dx, s.Y + dy)) ?? [])
                    if (walk[i] && walk[j] && Math.Abs(spans[j].Top - s.Top) <= Step && Math.Min(s.Ceiling, spans[j].Ceiling) - Math.Max(s.Top, spans[j].Top) >= clearance)
                    { connections[i][d] = j; break; }
            }
        int threshold = 2 * (int)Math.Ceiling(agent[0] / Cell - 1e-9);
        var distance = Chamfer(walk, connections, threshold + 1);
        for (int i = 0; i < n; i++) walk[i] &= distance[i] >= threshold;
        for (int i = 0; i < n; i++)
            for (int d = 0; d < 4; d++)
                if (!walk[i] || connections[i][d] >= 0 && !walk[connections[i][d]]) connections[i][d] = -1;
        if (!walk.Contains(true)) return null;

        var (vertices, faces) = Polygons(grid, walk, connections, cancellation);
        if (faces.Count == 0) return null;
        Require(faces.Count <= 250_000, "Navigation layer exceeds 250,000 faces. Choose a smaller source.");
        var neighboursByFace = faces.Select(f => Enumerable.Repeat(-1, f.Length).ToArray()).ToArray();
        var edges = new Dictionary<(int, int), List<(int Face, int Edge)>>();
        for (int i = 0; i < faces.Count; i++)
            for (int e = 0; e < faces[i].Length; e++)
            {
                var key = (faces[i][e], faces[i][(e + 1) % faces[i].Length]);
                if (!edges.TryGetValue(key, out var owners)) edges[key] = owners = [];
                owners.Add((i, e));
            }
        foreach (var (key, owners) in edges)
            if (owners.Count == 1 && edges.TryGetValue((key.Item2, key.Item1), out var other) && other.Count == 1)
                neighboursByFace[owners[0].Face][owners[0].Edge] = other[0].Face;
        int[] components = Components(neighboursByFace);
        var minimum = vertices.Aggregate(Vector3.Min); var maximum = vertices.Aggregate(Vector3.Max);
        float margin = (float)(agent[0] + 1.5);
        var bmin = new Vector3((float)Math.Floor(minimum.X / 32d + 1e-9) * 32 - margin,
            (float)Math.Floor(minimum.Y / 32d + 1e-9) * 32 - margin, (float)Math.Floor(minimum.Z / 32d + 1e-9) * 32 - margin);
        var bmax = new Vector3((float)Math.Ceiling(maximum.X / 32d - 1e-9) * 32 + margin,
            (float)Math.Ceiling(maximum.Y / 32d - 1e-9) * 32 + margin,
            (float)(Math.Ceiling((Math.Ceiling(maximum.Z / 32d - 1e-9) * 32 + margin + agent[1]) * 2 - 1e-9) / 2));
        for (int i = 0; i < vertices.Count; i++)
        {
            var v = vertices[i];
            vertices[i] = new((float)(bmin.X + Math.Round((v.X - bmin.X) / Cell) * Cell),
                (float)(bmin.Y + Math.Round((v.Y - bmin.Y) / Cell) * Cell), (float)(bmin.Z + Math.Round((v.Z - bmin.Z) / Cell) * Cell));
        }
        var spanFace = AssignFaces(grid, layer, walk, faces, vertices);
        var points = Points(grid, layer, walk, connections, spanFace);
        var clusters = Clusters(points, components, layer);
        var links = Links(grid, layer, walk, spanFace, faces, vertices, neighboursByFace, components, cancellation);
        return new(vertices.ToArray(), faces.ToArray(), neighboursByFace, components, bmin, bmax, points, clusters, links);
    }

    private static int[] Chamfer(bool[] walk, int[][] connections, int iterations)
    {
        var distance = walk.Select((w, i) => !w || connections[i].Contains(-1) ? 0 : 1_000_000).ToArray();
        for (int pass = 0; pass < iterations; pass++)
        {
            var next = distance.ToArray(); bool changed = false;
            for (int i = 0; i < walk.Length; i++)
                for (int d = 0; d < 4; d++)
                {
                    int j = connections[i][d]; if (j < 0) continue;
                    next[i] = Math.Min(next[i], distance[j] + 2);
                    int k = connections[j][(d + 1) % 4];
                    if (k >= 0) next[i] = Math.Min(next[i], distance[k] + 3);
                    changed |= next[i] != distance[i];
                }
            distance = next; if (!changed) break;
        }
        return distance;
    }

    private static int[] Components(int[][] neighbours)
    {
        int[] parent = Enumerable.Range(0, neighbours.Length).ToArray();
        int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        for (int i = 0; i < parent.Length; i++)
            foreach (int j in neighbours[i])
                if (j >= 0) { int a = Find(i), b = Find(j); parent[Math.Max(a, b)] = Math.Min(a, b); }
        return parent.Select((_, i) => Find(i)).ToArray();
    }

    private static double Distance(double x, double y, Vector3 a, Vector3 b)
    {
        double dx = b.X - (double)a.X, dy = b.Y - (double)a.Y;
        double t = Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / Math.Max(1e-12, dx * dx + dy * dy), 0, 1);
        return Math.Sqrt(Math.Pow(x - a.X - t * dx, 2) + Math.Pow(y - a.Y - t * dy, 2));
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
