using System.Numerics;

namespace SunriseStudio.Authoring;

public static partial class NavigationGenerator
{
    private static readonly (double X, double Y)[][] MetricCurves =
    [
        [(.25, 2), (.7, 2), (1.25, 4), (1.76, 5), (2.25, 6), (2.75, 7), (3.47, 8.5), (4.43, 10.5), (5.42, 12), (6.75, 13)],
        [(.25, 4), (.74, 4.5), (1.41, 5), (1.59, 6), (2.25, 8), (2.76, 9), (3.47, 11), (4.43, 13), (5.45, 15)],
        [(.24, 8), (1.23, 8), (1.72, 8.5), (2.25, 10), (2.86, 11), (3.18, 12.5), (4.43, 16), (5.48, 18), (6.75, 20.5)]
    ];

    private static bool Inside(int[] face, List<Vector3> vertices, double x, double y)
    {
        for (int e = 0; e < face.Length; e++)
        {
            var a = vertices[face[e]]; var b = vertices[face[(e + 1) % face.Length]];
            if ((b.X - (double)a.X) * (y - a.Y) - (x - a.X) * (b.Y - (double)a.Y) < -1e-9) return false;
        }
        return true;
    }

    private static (double X, double Y, double Z)[] FacePlanes(List<int[]> faces, List<Vector3> vertices) => faces
        .Select(f => Plane(f.Select(v => ((double)vertices[v].X, (double)vertices[v].Y, (double)vertices[v].Z)).ToArray())).ToArray();

    private static int[] AssignFaces(Grid grid, int layer, bool[] walk, List<int[]> faces, List<Vector3> vertices)
    {
        var assigned = Enumerable.Repeat(-1, walk.Length).ToArray(); var planes = FacePlanes(faces, vertices);
        for (int f = 0; f < faces.Count; f++)
        {
            var min = faces[f].Select(v => vertices[v]).Aggregate(Vector3.Min); var max = faces[f].Select(v => vertices[v]).Aggregate(Vector3.Max);
            int left = Math.Max(0, (int)Math.Floor((min.X - grid.X) / Cell)), right = Math.Min(grid.Width - 1, (int)Math.Floor((max.X - grid.X) / Cell));
            int bottom = Math.Max(0, (int)Math.Floor((min.Y - grid.Y) / Cell)), top = Math.Min(grid.Height - 1, (int)Math.Floor((max.Y - grid.Y) / Cell));
            var plane = planes[f];
            for (int y = bottom; y <= top; y++)
                for (int x = left; x <= right; x++)
                {
                    double px = grid.X + (x + .5) * Cell, py = grid.Y + (y + .5) * Cell;
                    if (!Inside(faces[f], vertices, px, py)) continue;
                    double z = plane.X * px + plane.Y * py + plane.Z, distance = double.PositiveInfinity; int best = -1;
                    foreach (int i in grid.Columns.GetValueOrDefault((x, y)) ?? [])
                    {
                        double d = Math.Abs(grid.Spans[i].Top - z);
                        if (walk[i] && assigned[i] < 0 && d < distance && d <= Agents[layer][1]) { best = i; distance = d; }
                    }
                    if (best >= 0) assigned[best] = f;
                }
        }
        return assigned;
    }

    private static Point[] Points(Grid grid, int layer, bool[] walk, int[][] connections, int[] spanFace)
    {
        var curve = MetricCurves[layer];
        var distance = Chamfer(walk, connections, (int)Math.Ceiling(2 * curve[^1].X / Cell) + 4);
        double Metric(int i)
        {
            double d = Math.Min(distance[i], 100_000) * Cell / 2 + Cell / 2;
            double value = curve[^1].Y;
            if (d < curve[0].X) value = curve[0].Y;
            else for (int k = 1; k < curve.Length; k++)
                    if (d < curve[k].X)
                    { var a = curve[k - 1]; var b = curve[k]; value = a.Y + (b.Y - a.Y) * (d - a.X) / (b.X - a.X); break; }
            return Math.Clamp(Math.Round(value * 2) / 2, 0, 32);
        }
        double spacing = new[] { 2.4, 3.9, 4.6 }[layer];
        var buckets = new Dictionary<(int, int), List<Point>>(); var points = new List<Point>();
        foreach (int i in Enumerable.Range(0, walk.Length).Where(i => spanFace[i] >= 0).OrderByDescending(Metric).ThenBy(i => grid.Spans[i].Y).ThenBy(i => grid.Spans[i].X))
        {
            var s = grid.Spans[i]; double x = grid.X + (s.X + .5) * Cell, y = grid.Y + (s.Y + .5) * Cell;
            int bx = (int)Math.Floor(x / spacing), by = (int)Math.Floor(y / spacing); bool accept = true;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    foreach (var p in buckets.GetValueOrDefault((bx + dx, by + dy)) ?? [])
                        if (Math.Pow(p.Position.X - x, 2) + Math.Pow(p.Position.Y - y, 2) < spacing * spacing - 1e-9) accept = false;
            if (!accept) continue;
            double length = Math.Sqrt(s.Surface.X * s.Surface.X + s.Surface.Y * s.Surface.Y + 1);
            var normal = (-s.Surface.X / length, -s.Surface.Y / length, 1 / length);
            var point = new Point((x, y, s.Surface.Height(x, y)), normal, (float)Metric(i), spanFace[i]);
            if (!buckets.TryGetValue((bx, by), out var bucket)) buckets[(bx, by)] = bucket = [];
            bucket.Add(point); points.Add(point);
        }
        return points.ToArray();
    }

    private static Cluster[] Clusters(Point[] points, int[] components, int layer)
    {
        double reach = new[] { 5.5, 8, 9 }[layer]; var taken = new bool[points.Length]; var clusters = new List<Cluster>();
        var buckets = Enumerable.Range(0, points.Length).GroupBy(i => ((int)Math.Floor(points[i].Position.X / reach),
            (int)Math.Floor(points[i].Position.Y / reach))).ToDictionary(g => g.Key, g => g.ToArray());
        for (int i = 0; i < points.Length; i++)
        {
            if (taken[i]) continue;
            var seed = points[i]; int bx = (int)Math.Floor(seed.Position.X / reach), by = (int)Math.Floor(seed.Position.Y / reach);
            var members = new List<int>();
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    foreach (int j in buckets.GetValueOrDefault((bx + dx, by + dy)) ?? [])
                        if (!taken[j] && components[points[j].Face] == components[seed.Face]
                            && Math.Pow(points[j].Position.X - (double)seed.Position.X, 2) + Math.Pow(points[j].Position.Y - (double)seed.Position.Y, 2) <= reach * reach)
                            members.Add(j);
            members.Sort(); foreach (int j in members) taken[j] = true;
            var min = (X: members.Min(j => points[j].Position.X), Y: members.Min(j => points[j].Position.Y), Z: members.Min(j => points[j].Position.Z));
            var max = (X: members.Max(j => points[j].Position.X), Y: members.Max(j => points[j].Position.Y), Z: members.Max(j => points[j].Position.Z));
            var centre = (X: (min.X + max.X) / 2, Y: (min.Y + max.Y) / 2, Z: (min.Z + max.Z) / 2);
            double radius = members.Max(j => Math.Sqrt(Math.Pow(points[j].Position.X - (double)centre.X, 2) + Math.Pow(points[j].Position.Y - (double)centre.Y, 2))) + Agents[layer][0];
            points[members[0]].Representative = true;
            clusters.Add(new(members.ToArray(), centre, radius, min.Z, max.Z));
        }
        return clusters.ToArray();
    }

}
