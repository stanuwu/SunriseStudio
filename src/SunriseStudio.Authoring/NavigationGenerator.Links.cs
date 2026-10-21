using System.Numerics;

namespace SunriseStudio.Authoring;

public static partial class NavigationGenerator
{
    private sealed record LinkSample(int Edge, int Face, int Target, int Kind, double Distance,
        (double X, double Y, double Z) Take, (double X, double Y, double Z) Land);

    /** Border probes form eight-metre runs, split by landing face; runtime traversal remains unverified. */
    private static Link[] Links(Grid grid, int layer, bool[] walk, int[] spanFace, List<int[]> faces,
        List<Vector3> vertices, int[][] neighbours, int[] components, CancellationToken cancellation)
    {
        var planes = FacePlanes(faces, vertices); var samples = new List<LinkSample?>();
        double reach = 2 * Agents[layer][0] + 7.5, drop = new[] { 8.5, 10, 12.5 }[layer]; int edge = 0;
        double Height(int face, double x, double y) => planes[face].X * x + planes[face].Y * y + planes[face].Z;
        for (int f = 0; f < faces.Count; f++)
            for (int e = 0; e < faces[f].Length; e++)
            {
                cancellation.ThrowIfCancellationRequested(); if (neighbours[f][e] >= 0) continue;
                int current = edge++; var a = vertices[faces[f][e]]; var b = vertices[faces[f][(e + 1) % faces[f].Length]];
                double dx = b.X - (double)a.X, dy = b.Y - (double)a.Y, length = Math.Sqrt(dx * dx + dy * dy);
                if (length < 1e-6) continue;
                double nx = dy / length, ny = -dx / length;
                int count = length <= .5 ? 1 : (int)Math.Floor((length - .5) / .5 + 1e-9) + 1;
                double start = (length - (count - 1) * .5) / 2;
                for (int sample = 0; sample < count; sample++)
                {
                    double t = (start + sample * .5) / length, px = a.X + dx * t, py = a.Y + dy * t;
                    double tx = px - nx * .25, ty = py - ny * .25, tz = Height(f, tx, ty);
                    int landing = -1, kind = 0; double landingDistance = 0; bool empty = false;
                    if (!Inside(faces[f], vertices, tx, ty)) { samples.Add(null); continue; }
                    for (double distance = .25; distance <= reach + 1e-9; distance += .25)
                    {
                        double x = px + nx * distance, y = py + ny * distance;
                        var column = grid.Columns.GetValueOrDefault(((int)Math.Floor((x - grid.X) / Cell), (int)Math.Floor((y - grid.Y) / Cell))) ?? [];
                        bool blocked = false, level = false; int best = -1; double bestZ = double.NegativeInfinity;
                        foreach (int i in column)
                        {
                            var s = grid.Spans[i]; blocked |= s.Top > tz + Step && s.Bottom < tz + Agents[layer][1];
                            level |= Math.Abs(s.Top - tz) <= Step;
                            int face = spanFace[i]; if (face < 0) continue;
                            double z = Height(face, x, y);
                            if (z <= tz + Step && z > bestZ) { best = i; bestZ = z; }
                        }
                        if (blocked) break;
                        empty |= !level; if (best < 0) continue;
                        int target = spanFace[best]; double h = tz - bestZ;
                        bool own = components[target] == components[f] && Math.Abs(h) <= Step;
                        if (own) continue;
                        bool gap = Math.Abs(h) <= Step && empty && components[target] > components[f];
                        bool mirror = Math.Abs(h) <= Step && empty && components[target] < components[f];
                        if (mirror) { landing = -1; kind = 0; }
                        if (gap || h > Step && h <= drop && kind == 0)
                        { landing = best; kind = gap ? 2 : 1; landingDistance = distance; }
                        if (Math.Abs(h) <= Step) break;
                    }
                    if (landing < 0) { samples.Add(null); continue; }
                    var span = grid.Spans[landing]; int end = spanFace[landing];
                    double qx = grid.X + (span.X + .5) * Cell, qy = grid.Y + (span.Y + .5) * Cell, qz = Height(end, qx, qy);
                    samples.Add(Inside(faces[end], vertices, qx, qy) ? new(current, f, end, kind, landingDistance, (tx, ty, tz), (qx, qy, qz)) : null);
                }
            }
        var runs = new List<List<LinkSample>>(); var run = new List<LinkSample>();
        foreach (var sample in samples)
        {
            if (sample is null) { if (run.Count > 0) runs.Add(run); run = []; continue; }
            if (run.Count > 0)
            {
                var last = run[^1]; double length = Math.Sqrt(Math.Pow(sample.Take.X - run[0].Take.X, 2) + Math.Pow(sample.Take.Y - run[0].Take.Y, 2));
                if (sample.Edge != last.Edge || sample.Kind != last.Kind || components[sample.Target] != components[last.Target]
                    || Math.Abs(sample.Land.Z - last.Land.Z) > 1 || Math.Abs(sample.Distance - last.Distance) > 1 || length > 8)
                { runs.Add(run); run = []; }
            }
            run.Add(sample);
        }
        if (run.Count > 0) runs.Add(run);
        var centres = faces.Select(f => ((double)f.Average(v => vertices[v].X), (double)f.Average(v => vertices[v].Y), (double)f.Average(v => vertices[v].Z))).ToArray();
        bool Detour(int start, HashSet<int> targets, double limit)
        {
            var best = new Dictionary<int, double> { [start] = 0 }; var queue = new PriorityQueue<int, (double, int)>(); queue.Enqueue(start, (0, start));
            while (queue.TryDequeue(out int f, out var priority))
            {
                if (targets.Contains(f)) return true;
                if (priority.Item1 > best[f]) continue;
                foreach (int next in neighbours[f])
                {
                    if (next < 0) continue; var a = centres[f]; var b = centres[next];
                    double distance = priority.Item1 + Math.Sqrt(Math.Pow(a.Item1 - b.Item1, 2) + Math.Pow(a.Item2 - b.Item2, 2) + Math.Pow(a.Item3 - b.Item3, 2));
                    if (distance <= limit && distance < best.GetValueOrDefault(next, double.PositiveInfinity))
                    { best[next] = distance; queue.Enqueue(next, (distance, next)); }
                }
            }
            return false;
        }
        var links = new List<Link>();
        Vector3 V((double X, double Y, double Z) p) => new((float)p.X, (float)p.Y, (float)p.Z);
        foreach (var group in runs)
        {
            if (group.Count < 2) continue; var first = group[0]; var last = group[^1];
            if (components[first.Target] == components[first.Face])
            {
                double direct = Math.Sqrt(Math.Pow(group.Average(p => p.Take.X - p.Land.X), 2) + Math.Pow(group.Average(p => p.Take.Y - p.Land.Y), 2)
                    + Math.Pow(group.Average(p => p.Take.Z - p.Land.Z), 2));
                if (Detour(first.Face, group.Select(p => p.Target).ToHashSet(), 3 * direct + 8)) continue;
            }
            var zs = group.SelectMany(p => new[] { p.Take.Z, p.Land.Z }).ToArray(); bool both = first.Kind == 2 || zs.Max() - zs.Min() <= 5;
            for (int start = 0; start < group.Count;)
            {
                int end = start; while (end + 1 < group.Count && group[end + 1].Target == group[start].Target) end++;
                links.Add(new(V(first.Take), V(last.Take), V(group[end].Land), V(group[start].Land), first.Face, group[end].Target, both));
                start = end + 1;
            }
        }
        Require(links.Count < 16_000, "Navigation produces too many link lists. Choose a smaller source.");
        return links.ToArray();
    }
}
