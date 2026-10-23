using System.Numerics;

namespace SunriseStudio.Authoring;

public static partial class NavigationGenerator
{
    private sealed record Corner(int X, int Y, double Z, int Region);

    /** Monotone regions use eight-metre tiles, refined to two metres on nonplanar ground. */
    private static long[] Tiles(Grid grid, bool[] walk, int[][] con)
    {
        int n = walk.Length, size = 16, level = 0;
        long basis = Math.Max(grid.Width, grid.Height) + 1;
        var tile = new long[n]; var refine = walk.ToArray();
        while (true)
        {
            for (int i = 0; i < n; i++)
                if (refine[i]) tile[i] = (level * basis + grid.Spans[i].X / size) * basis + grid.Spans[i].Y / size;
            if (size <= 4 || !refine.Contains(true)) return tile;
            var seen = new bool[n]; var bad = new HashSet<long>();
            for (int i = 0; i < n; i++)
            {
                if (!refine[i] || seen[i]) continue;
                var group = new List<int>(); var queue = new Queue<int>(); queue.Enqueue(i); seen[i] = true;
                while (queue.TryDequeue(out int j))
                {
                    group.Add(j);
                    foreach (int k in con[j])
                        if (k >= 0 && refine[k] && !seen[k] && tile[k] == tile[j]) { seen[k] = true; queue.Enqueue(k); }
                }
                var points = group.Select(j => ((grid.Spans[j].X % size + .5) * Cell,
                    (grid.Spans[j].Y % size + .5) * Cell, grid.Spans[j].Top)).ToArray();
                var plane = Plane(points, 1e-9);
                if (points.Max(p => Math.Abs(plane.X * p.Item1 + plane.Y * p.Item2 + plane.Z - p.Item3)) > .2) bad.Add(tile[i]);
            }
            for (int i = 0; i < n; i++) refine[i] &= bad.Contains(tile[i]);
            size /= 2; level++;
        }
    }

    private static int[] Regions(Grid grid, bool[] walk, int[][] con, long[] tile)
    {
        var region = new int[walk.Length]; int next = 1;
        foreach (var row in Enumerable.Range(0, walk.Length).Where(i => walk[i]).GroupBy(i => grid.Spans[i].Y))
        {
            var sweepOf = new Dictionary<int, int>(); var sweeps = new Dictionary<int, (int Neighbour, int Count)>();
            var previous = new Dictionary<int, int>(); int rid = 1;
            foreach (int s in row)
            {
                int w = con[s][0], id = w >= 0 && tile[w] == tile[s] ? sweepOf[w] : 0;
                if (id == 0) { id = rid++; sweeps[id] = (0, 0); }
                int down = con[s][3];
                if (down >= 0 && tile[down] == tile[s] && region[down] > 0)
                {
                    int nr = region[down]; var sw = sweeps[id];
                    if (sw.Neighbour == 0 || sw.Neighbour == nr)
                    { sweeps[id] = (nr, sw.Count + 1); previous[nr] = previous.GetValueOrDefault(nr) + 1; }
                    else sweeps[id] = (-1, sw.Count);
                }
                sweepOf[s] = id;
            }
            var ids = sweeps.ToDictionary(p => p.Key, p => p.Value.Neighbour > 0 && previous.GetValueOrDefault(p.Value.Neighbour) == p.Value.Count ? p.Value.Neighbour : next++);
            foreach (int s in row) region[s] = ids[sweepOf[s]];
        }
        return region;
    }

    private static List<Corner> Contour(Grid grid, int[][] con, int[] region, int[] flags, int start)
    {
        int s = start, d = 0; while ((flags[s] & 1 << d) == 0) d++;
        int firstDirection = d; var points = new List<Corner>();
        for (int step = 0; step < 40_000; step++)
        {
            if ((flags[s] & 1 << d) != 0)
            {
                var span = grid.Spans[s]; int x = span.X, y = span.Y;
                if (d == 0) y++; else if (d == 1) { x++; y++; } else if (d == 2) x++;
                int dp = (d + 1) % 4, a = con[s][d], b = con[s][dp]; var group = new List<int> { s };
                if (a >= 0) { group.Add(a); if (con[a][dp] >= 0) group.Add(con[a][dp]); }
                if (b >= 0) { group.Add(b); if (con[b][d] >= 0) group.Add(con[b][d]); }
                double z = group.Max(j => grid.Spans[j].Surface.Height(grid.X + x * Cell, grid.Y + y * Cell));
                points.Add(new(x, y, z, a >= 0 ? region[a] : 0)); flags[s] &= ~(1 << d); d = dp;
            }
            else { s = con[s][d]; Require(s >= 0, "Navigation contour left its region."); d = (d + 3) % 4; }
            if (s == start && d == firstDirection) return points;
        }
        throw new InvalidDataException("Navigation contour exceeds 40,000 edges.");
    }

    private static List<Corner> Simplify(List<Corner> raw)
    {
        int n = raw.Count; var selected = new List<int>();
        if (raw.Any(p => p.Region != 0))
            for (int i = 0; i < n; i++) if (raw[i].Region != raw[(i + 1) % n].Region) selected.Add(i);
        if (selected.Count == 0)
        {
            var order = Enumerable.Range(0, n).OrderBy(i => raw[i].X).ThenBy(i => raw[i].Y).ToArray();
            selected.Add(order[0]); if (order[^1] != order[0]) selected.Add(order[^1]);
        }
        bool Forward(Corner a, Corner b) => b.X > a.X || b.X == a.X && b.Y > a.Y;
        for (int i = 0; i < selected.Count;)
        {
            int ai = selected[i], bi = selected[(i + 1) % selected.Count]; var a = raw[ai]; var b = raw[bi];
            bool forward = Forward(a, b); int increment = forward ? 1 : n - 1, ci = forward ? (ai + 1) % n : (bi + n - 1) % n, end = forward ? bi : ai;
            if (!forward) (a, b) = (b, a);
            double worst = 0; int at = -1;
            if (raw[ci].Region == 0)
                while (ci != end)
                {
                    double dx = b.X - a.X, dy = b.Y - a.Y, length = dx * dx + dy * dy;
                    double t = length == 0 ? 0 : Math.Clamp(((raw[ci].X - a.X) * dx + (raw[ci].Y - a.Y) * dy) / length, 0, 1);
                    double error = Math.Pow(a.X + t * dx - raw[ci].X, 2) + Math.Pow(a.Y + t * dy - raw[ci].Y, 2);
                    if (error > worst) { worst = error; at = ci; }
                    ci = (ci + increment) % n;
                }
            if (at >= 0 && worst > 1.3 * 1.3) selected.Insert(i + 1, at); else i++;
        }
        for (int i = 0; i < selected.Count;)
        {
            int ai = selected[i], bi = selected[(i + 1) % selected.Count], at = -1; var a = raw[ai]; var b = raw[bi];
            int count = bi < ai ? bi + n - ai : bi - ai;
            if (raw[(ai + 1) % n].Region == 0)
            {
                if ((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y) > 24 * 24 && count > 1)
                    at = (ai + (Forward(a, b) ? count / 2 : (count + 1) / 2)) % n;
                if (at < 0)
                {
                    double worst = .5;
                    for (int step = 1; step < count; step++)
                    {
                        int c = (ai + step) % n; double error = Math.Abs(raw[c].Z - (a.Z + (double)step / count * (b.Z - a.Z)));
                        if (error > worst) { worst = error; at = c; }
                    }
                }
            }
            if (at >= 0) selected.Insert(i + 1, at); else i++;
        }
        var result = new List<Corner>();
        foreach (int i in selected) if (result.Count == 0 || result[^1].X != raw[i].X || result[^1].Y != raw[i].Y) result.Add(raw[i]);
        while (result.Count > 1 && result[0].X == result[^1].X && result[0].Y == result[^1].Y) result.RemoveAt(result.Count - 1);
        return result;
    }

    private static long Area(Corner a, Corner b, Corner c) => (long)(b.X - a.X) * (c.Y - a.Y) - (long)(c.X - a.X) * (b.Y - a.Y);
    private static bool Between(Corner a, Corner b, Corner c) => Area(a, b, c) == 0 && (a.X != b.X
        ? c.X >= Math.Min(a.X, b.X) && c.X <= Math.Max(a.X, b.X) : c.Y >= Math.Min(a.Y, b.Y) && c.Y <= Math.Max(a.Y, b.Y));
    private static bool Intersects(Corner a, Corner b, Corner c, Corner d, bool loose)
    {
        bool proper = Area(a, b, c) != 0 && Area(a, b, d) != 0 && Area(c, d, a) != 0 && Area(c, d, b) != 0
            && (Area(a, b, c) > 0) != (Area(a, b, d) > 0) && (Area(c, d, a) > 0) != (Area(c, d, b) > 0);
        return proper || !loose && (Between(a, b, c) || Between(a, b, d) || Between(c, d, a) || Between(c, d, b));
    }

    /** Shortest-ear triangulation and longest-edge convex merging preserve the oracle's face ordering. */
    private static List<int[]> Triangulate(List<Corner> points, List<int> ring)
    {
        var indices = ring.ToList(); var triangles = new List<int[]>();
        bool Cone(int i, int j, bool loose)
        {
            int n = indices.Count; var a = points[indices[i]]; var b = points[indices[j]];
            var a0 = points[indices[(i + n - 1) % n]]; var a1 = points[indices[(i + 1) % n]];
            return Area(a, a1, a0) >= 0 ? loose ? Area(a, b, a0) >= 0 && Area(b, a, a1) >= 0 : Area(a, b, a0) > 0 && Area(b, a, a1) > 0
                : !(Area(a, b, a1) >= 0 && Area(b, a, a0) >= 0);
        }
        bool Diagonal(int i, int j, bool loose)
        {
            if (!Cone(i, j, loose) || !Cone(j, i, loose)) return false;
            var a = points[indices[i]]; var b = points[indices[j]];
            bool Same(Corner p, Corner q) => p.X == q.X && p.Y == q.Y;
            for (int k = 0; k < indices.Count; k++)
            {
                int next = (k + 1) % indices.Count; if (k == i || k == j || next == i || next == j) continue;
                var c = points[indices[k]]; var d = points[indices[next]];
                if (Same(a, c) || Same(a, d) || Same(b, c) || Same(b, d)) continue;
                if (Intersects(a, b, c, d, loose)) return false;
            }
            return true;
        }
        while (indices.Count > 3)
        {
            int n = indices.Count, best = -1; long length = long.MaxValue;
            foreach (bool loose in new[] { false, true })
            {
                for (int i = 0; i < n; i++)
                    if (Diagonal(i, (i + 2) % n, loose))
                    {
                        var a = points[indices[i]]; var b = points[indices[(i + 2) % n]];
                        long d = (long)(b.X - a.X) * (b.X - a.X) + (long)(b.Y - a.Y) * (b.Y - a.Y);
                        if (d < length) { length = d; best = i; }
                    }
                if (best >= 0) break;
            }
            Require(best >= 0, "Navigation contour cannot be triangulated.");
            triangles.Add([indices[best], indices[(best + 1) % n], indices[(best + 2) % n]]); indices.RemoveAt((best + 1) % n);
        }
        triangles.Add(indices.ToArray());
        return triangles.Where(p => Area(points[p[0]], points[p[1]], points[p[2]]) > 0).ToList();
    }

    private static List<int[]> Merge(List<int[]> polygons, List<Corner> points)
    {
        (long Score, int A, int B) Value(int[] a, int[] b)
        {
            if (a.Length + b.Length - 2 > 6) return (-1, 0, 0);
            for (int ea = 0; ea < a.Length; ea++)
                for (int eb = 0; eb < b.Length; eb++)
                {
                    int a0 = a[ea], a1 = a[(ea + 1) % a.Length];
                    if (b[eb] != a1 || b[(eb + 1) % b.Length] != a0) continue;
                    if (Area(points[a[(ea + a.Length - 1) % a.Length]], points[a0], points[b[(eb + 2) % b.Length]]) <= 0
                        || Area(points[b[(eb + b.Length - 1) % b.Length]], points[a1], points[a[(ea + 2) % a.Length]]) <= 0) return (-1, 0, 0);
                    var joined = Join(a, b, ea, eb).Select(v => (points[v].X * Cell, points[v].Y * Cell, points[v].Z)).ToArray();
                    var plane = Plane(joined);
                    if (joined.Max(p => Math.Abs(plane.X * p.Item1 + plane.Y * p.Item2 + plane.Z - p.Item3)) > .5) return (-1, 0, 0);
                    long dx = points[a1].X - points[a0].X, dy = points[a1].Y - points[a0].Y;
                    return (dx * dx + dy * dy, ea, eb);
                }
            return (-1, 0, 0);
        }
        int[] Join(int[] a, int[] b, int ea, int eb) => Enumerable.Range(0, a.Length - 1).Select(i => a[(ea + 1 + i) % a.Length])
            .Concat(Enumerable.Range(0, b.Length - 1).Select(i => b[(eb + 1 + i) % b.Length])).ToArray();
        while (true)
        {
            var edges = new Dictionary<(int, int), List<int>>();
            for (int i = 0; i < polygons.Count; i++)
                for (int e = 0; e < polygons[i].Length; e++)
                {
                    int a = polygons[i][e], b = polygons[i][(e + 1) % polygons[i].Length]; var key = (Math.Min(a, b), Math.Max(a, b));
                    if (!edges.TryGetValue(key, out var owners)) edges[key] = owners = []; owners.Add(i);
                }
            var candidates = edges.Values.Where(p => p.Count == 2).Select(p => (A: p[0], B: p[1])).Distinct()
                .Select(p => (p.A, p.B, Value(polygons[p.A], polygons[p.B]).Score)).Where(p => p.Score > 0)
                .OrderByDescending(p => p.Score).ThenBy(p => p.A).ThenBy(p => p.B).ToArray();
            if (candidates.Length == 0) return polygons;
            var touched = new HashSet<int>(); var dead = new HashSet<int>();
            foreach (var p in candidates)
            {
                if (touched.Contains(p.A) || touched.Contains(p.B)) continue;
                var value = Value(polygons[p.A], polygons[p.B]); if (value.Score <= 0) continue;
                polygons[p.A] = Join(polygons[p.A], polygons[p.B], value.A, value.B);
                touched.Add(p.A); touched.Add(p.B); dead.Add(p.B);
            }
            polygons = polygons.Where((_, i) => !dead.Contains(i)).ToList();
        }
    }

    private static (List<Vector3> Vertices, List<int[]> Faces) Polygons(Grid grid, bool[] walk, int[][] con, CancellationToken cancellation)
    {
        var region = Regions(grid, walk, con, Tiles(grid, walk, con)); var flags = new int[walk.Length];
        for (int i = 0; i < walk.Length; i++)
            if (walk[i])
            {
                for (int d = 0; d < 4; d++) if (con[i][d] < 0 || region[con[i][d]] != region[i]) flags[i] |= 1 << d;
                if (flags[i] == 15) flags[i] = 0;
            }
        var contours = new SortedDictionary<int, List<List<Corner>>>();
        for (int i = 0; i < walk.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested(); if (flags[i] == 0) continue;
            var raw = Contour(grid, con, region, flags, i); if (raw.Count < 3) continue;
            var ring = Simplify(raw); ring.Reverse(); if (ring.Count < 3) continue;
            long area = Enumerable.Range(0, ring.Count).Sum(k => (long)ring[k].X * ring[(k + 1) % ring.Count].Y - (long)ring[k].Y * ring[(k + 1) % ring.Count].X);
            Require(area > 0, "Navigation region contains an unsupported hole contour.");
            if (!contours.TryGetValue(region[i], out var values)) contours[region[i]] = values = []; values.Add(ring);
        }
        var points = new List<Corner>(); var weld = new Dictionary<(int, int), List<int>>(); var polygons = new List<int[]>();
        foreach (var rings in contours.Values)
            foreach (var contour in rings)
            {
                var ring = new List<int>();
                foreach (var p in contour)
                {
                    if (!weld.TryGetValue((p.X, p.Y), out var values)) weld[(p.X, p.Y)] = values = [];
                    int v = values.FirstOrDefault(i => Math.Abs(points[i].Z - p.Z) <= 2 * Cell, -1);
                    if (v < 0) { v = points.Count; points.Add(p); values.Add(v); }
                    if (ring.Count == 0 || ring[^1] != v) ring.Add(v);
                }
                if (ring.Count > 1 && ring[0] == ring[^1]) ring.RemoveAt(ring.Count - 1);
                if (ring.Count >= 3) polygons.AddRange(Merge(Triangulate(points, ring), points));
            }
        var vertices = new List<Vector3>(); var remap = new Dictionary<int, int>();
        foreach (var p in polygons)
            for (int k = 0; k < p.Length; k++)
            {
                int old = p[k];
                if (!remap.TryGetValue(old, out int v))
                { remap[old] = v = vertices.Count; var q = points[old]; vertices.Add(new((float)(grid.X + q.X * Cell), (float)(grid.Y + q.Y * Cell), (float)q.Z)); }
                p[k] = v;
            }
        return (vertices, polygons);
    }

    /** Centred normal equations fit z=ax+by+c without a dependency on a numerical runtime. */
    private static (double X, double Y, double Z) Plane((double X, double Y, double Z)[] points, double ridge = 0)
    {
        double ox = points.Average(p => p.X), oy = points.Average(p => p.Y), oz = points.Average(p => p.Z);
        double xx = ridge, xy = 0, yy = ridge, xz = 0, yz = 0;
        foreach (var p in points)
        { double x = p.X - ox, y = p.Y - oy, z = p.Z - oz; xx += x * x; xy += x * y; yy += y * y; xz += x * z; yz += y * z; }
        double determinant = xx * yy - xy * xy;
        double a = Math.Abs(determinant) > 1e-12 ? (xz * yy - yz * xy) / determinant : xx > 1e-12 ? xz / xx : 0;
        double b = Math.Abs(determinant) > 1e-12 ? (yz * xx - xz * xy) / determinant : yy > 1e-12 ? yz / yy : 0;
        return (a, b, oz - a * ox - b * oy);
    }
}
