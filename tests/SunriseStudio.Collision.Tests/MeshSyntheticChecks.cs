using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Scene.Collision;
using static Checks;

/** Hull, mesh tree, encoder, writer and vertex move checks on built data. */
internal static class MeshSyntheticChecks
{
    internal static void Run()
    {
        Hulls();
        Trees();
        var cube = Cube();
        var grid = Grid(25, 20, shuffle: false);
        var loose = Grid(25, 20, shuffle: true, spacing: 0.25f);
        Convex();
        Console.WriteLine($"PASS collision-mesh-synthetic {cube} {grid} {loose}");
        Paged();
    }

    /** A 1415 by 1415 vertex terrain grid: its sections are coarse, so it needs many shared-vertex pages. */
    private static void Paged()
    {
        const int side = 1414;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var vertices = new Vector3[(side + 1) * (side + 1)];
        for (int y = 0; y <= side; y++)
            for (int x = 0; x <= side; x++)
                vertices[y * (side + 1) + x] = new Vector3(x, y, 6 * MathF.Sin(x * 0.05f) * MathF.Cos(y * 0.07f));
        var indices = new int[side * side * 6];
        int at = 0;
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                int a = y * (side + 1) + x, b = a + 1, c = a + side + 2, d = a + side + 1;
                (indices[at], indices[at + 1], indices[at + 2], indices[at + 3], indices[at + 4], indices[at + 5]) = (a, b, c, a, c, d);
                at += 6;
            }
        var (mesh, summary, _) = RoundTrip(new CompressedMeshSource(vertices, indices), "paged-grid");
        int pages = mesh.Sections.Max(section => section.Page) + 1;
        Check(vertices.Length > 2_000_000 && pages > 1 && mesh.SharedVertices.Length > 65536, $"paged-grid: {pages} pages, {mesh.SharedVertices.Length} shared vertices.");
        Console.WriteLine($"PASS collision-mesh-paged vertices={vertices.Length} pages={pages} {summary} seconds={watch.Elapsed.TotalSeconds:F0}");
    }

    /** A cube, a square, a line and 64 points on a sphere. */
    private static void Hulls()
    {
        var corners = Enumerable.Range(0, 8).Select(i => new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1)).ToArray();
        CheckHull(corners, 12);
        CheckHull([Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.One with { Z = 0 }], 2);
        Check(ConvexHull.Triangles([Vector3.Zero, Vector3.UnitX, 2 * Vector3.UnitX]).Length == 0, "A line has hull triangles.");
        var random = new Random(3);
        var sphere = Enumerable.Range(0, 64).Select(_ => Vector3.Normalize(new Vector3(random.NextSingle() - 0.5f,
            random.NextSingle() - 0.5f, random.NextSingle() - 0.5f)) * 3).ToArray();
        CheckHull(sphere, 2 * 64 - 4);
        Check(ConvexHull.Planes(corners).Length == 6, "A cube does not have six planes.");
    }

    /** Every hull edge has one twin, faces wind outward, and every point is inside every face. */
    private static void CheckHull(Vector3[] points, int triangles)
    {
        int[] t = ConvexHull.Triangles(points);
        Check(t.Length == 3 * triangles, $"Hull has {t.Length / 3} triangles, not {triangles}.");
        var center = points.Aggregate(Vector3.Zero, (a, b) => a + b) / points.Length;
        var edges = new HashSet<(int, int)>();
        for (int i = 0; i < t.Length; i += 3)
        {
            Vector3 a = points[t[i]], b = points[t[i + 1]], c = points[t[i + 2]];
            var n = Vector3.Cross(b - a, c - a);
            bool flat = points.All(p => MathF.Abs(Vector3.Dot(Vector3.Normalize(n), p - a)) < 1e-5f);
            if (!flat)
            {
                Check(Vector3.Dot(n, (a + b + c) / 3 - center) > 0, "A hull face winds inward.");
                Check(points.All(p => Vector3.Dot(Vector3.Normalize(n), p - a) < 1e-4f), "A point lies outside a hull face.");
            }
            foreach (var e in new[] { (t[i], t[i + 1]), (t[i + 1], t[i + 2]), (t[i + 2], t[i]) }) Check(edges.Add(e), "A hull edge repeats.");
        }
        if (triangles > 2) Check(edges.All(e => edges.Contains((e.Item2, e.Item1))), "A hull edge has no twin.");
    }

    /** Built trees of both kinds reach each leaf once and enclose its box. */
    private static void Trees()
    {
        var random = new Random(11);
        foreach (var (kind, count) in new[] { (MeshTreeKind.Section, 1), (MeshTreeKind.Section, 2), (MeshTreeKind.Section, 128),
            (MeshTreeKind.Top, 1), (MeshTreeKind.Top, 3), (MeshTreeKind.Top, 700) })
        {
            var boxes = Enumerable.Range(0, count).Select(_ =>
            {
                double x = random.NextDouble() * 50, y = random.NextDouble() * 50, z = random.NextDouble() * 50;
                return new CompoundBox(x, y, z, x + random.NextDouble(), y + random.NextDouble(), z + random.NextDouble());
            }).ToArray();
            var root = boxes.Aggregate((a, b) => a.Union(b));
            byte[] nodes = MeshTree.Build(kind, boxes, root, out int[] order);
            Check(nodes.Length == (2 * count - 1) * MeshTree.NodeSize(kind), "A built tree is not 2n - 1 nodes.");
            Check(order.Order().SequenceEqual(Enumerable.Range(0, count)), "A built tree misses a leaf.");
            var leaves = MeshTree.Walk(kind, nodes, root, count).Where(n => n.Right < 0).ToArray();
            Check(leaves.Length == count && leaves.Select(n => n.Leaf).Distinct().Count() == count, "A built tree repeats a leaf.");
            foreach (var leaf in leaves) Check(leaf.Box.Contains(boxes[order[leaf.Leaf]]), "A built leaf does not enclose its box.");
            if (kind == MeshTreeKind.Section)
                foreach (var leaf in leaves)
                    Check(MeshTree.SectionNodeBox(nodes, root, leaf.Index) == leaf.Box, "Descent and walk disagree on a leaf box.");
        }
    }

    /** Twelve triangles as six quads, encoded, written, read back and compared. */
    private static string Cube()
    {
        var corners = Enumerable.Range(0, 8).Select(i => new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1) * 2 - Vector3.One).ToArray();
        int[] faces = [0, 2, 3, 1, 4, 5, 7, 6, 0, 1, 5, 4, 2, 6, 7, 3, 0, 4, 6, 2, 1, 3, 7, 5];
        var indices = new List<int>();
        for (int f = 0; f < 24; f += 4) indices.AddRange([faces[f], faces[f + 1], faces[f + 2], faces[f], faces[f + 2], faces[f + 3]]);
        var source = new CompressedMeshSource(corners, indices) { FilterPalette = [0x00010203], UserDataPalette = [0] };
        var (mesh, error, _) = RoundTrip(source, "cube");
        Check(mesh.Sections.Count == 1 && mesh.Primitives.Length == 6, "The cube is not six quads in one section.");
        return $"cube=[{error}]";
    }

    /** A wavy grid of 2 * cells triangles with per-row filter and user data. */
    private static string Grid(int columns, int rows, bool shuffle, float spacing = 2)
    {
        var vertices = new List<Vector3>();
        for (int y = 0; y <= rows; y++)
            for (int x = 0; x <= columns; x++)
                vertices.Add(new Vector3(spacing * x, spacing * y, spacing * MathF.Sin(x * 0.4f) * MathF.Cos(y * 0.3f)));
        var triangles = new List<(int A, int B, int C, int Row)>();
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < columns; x++)
            {
                int a = y * (columns + 1) + x, b = a + 1, c = a + columns + 2, d = a + columns + 1;
                triangles.Add((a, b, c, y));
                triangles.Add((a, c, d, y));
            }
        if (shuffle) triangles = triangles.OrderBy(t => (t.A * 7919 + t.C * 104729) % 1009).ToList();
        var source = new CompressedMeshSource(vertices, triangles.SelectMany(t => new[] { t.A, t.B, t.C }).ToArray())
        {
            FilterIndices = triangles.Select(t => (byte)(t.Row % 3)).ToArray(),
            UserDataIndices = triangles.Select(t => (byte)(t.Row / 10)).ToArray(),
            FilterPalette = [0x1234, 0x5678, 0x9ABC], UserDataPalette = [7, 8]
        };
        var (mesh, error, _) = RoundTrip(source, shuffle ? "loose-grid" : "grid");

        // Moving every decoded vertex keeps every array size and the file size.
        byte[] before = CollisionPackfileWriter.WriteCompressedMesh(mesh);
        var file = HavokPackfile.Parse(before);
        var shape = CollisionShapes.Decode(file, file.RootOffset);
        var turn = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, 2, 3)), 0.6f);
        Vector3 Move(Vector3 v) => Vector3.Transform(v * 1.5f, turn) + new Vector3(100, -40, 7);
        var moved = CollisionShapeEdits.MoveVertices(CompressedMesh.Read(file, file.RootOffset), shape.Vertices.Select(Move).ToArray());
        byte[] after = CollisionPackfileWriter.WriteCompressedMesh(moved);
        Check(after.Length == before.Length, "Moving vertices changed the file size.");
        var movedFile = HavokPackfile.Parse(after);
        var movedShape = CollisionShapes.Decode(movedFile, movedFile.RootOffset);
        double tolerance = Tolerance(CompressedMesh.Read(movedFile, movedFile.RootOffset));
        double worst = 0;
        for (int i = 0; i < shape.Indices.Length; i++)
            worst = Math.Max(worst, MaxAxis(movedShape.Vertices[movedShape.Indices[i]], Move(shape.Vertices[shape.Indices[i]])));
        Check(worst <= tolerance, $"A moved vertex is {worst} off, above {tolerance}.");
        return $"{(shuffle ? "loose-grid" : "grid")}=[{error} moved-max={worst:0.#####} bytes=same-size]";
    }

    /** A written convex cube reads back with twelve triangles and its radius. */
    private static void Convex()
    {
        var corners = Enumerable.Range(0, 8).Select(i => new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1) * 4).ToArray();
        byte[] bytes = CollisionPackfileWriter.WriteConvexVertices(corners, ConvexHull.Planes(corners), 0.05f);
        var file = HavokPackfile.Parse(bytes);
        var shape = CollisionShapes.Decode(file, file.RootOffset);
        Check(shape.TriangleCount == 12 && shape.Vertices.Length == 8 && shape.BoundsMax == new Vector3(4.05f), "A written convex shape reads back wrong.");
        Check(CollisionPackfileWriter.WriteConvexVertices(corners, ConvexHull.Planes(corners), 0.05f).SequenceEqual(bytes), "The convex writer is not deterministic.");
    }

    /**
     * Encodes, writes, parses and decodes a source, and compares every triangle with its source.
     * @return The read-back mesh, a summary, and the largest vertex error.
     */
    internal static (CompressedMesh Mesh, string Summary, double Worst) RoundTrip(CompressedMeshSource source, string name)
    {
        var encoded = CompressedMeshEncoder.Encode(source);
        byte[] bytes = CollisionPackfileWriter.WriteCompressedMesh(encoded.Mesh);
        var file = HavokPackfile.Parse(bytes);
        var mesh = CompressedMesh.Read(file, file.RootOffset);
        Check(CollisionPackfileWriter.WriteCompressedMesh(mesh).SequenceEqual(bytes), $"{name}: a read-back mesh does not write the same bytes.");
        var problems = mesh.Check();
        Check(problems.Count == 0, $"{name}: {string.Join("; ", problems.Take(3))}");
        var shape = CollisionShapes.Decode(file, file.RootOffset);
        double worst = Compare(shape, encoded, source, mesh, name);
        return (mesh, $"triangles={shape.TriangleCount} dropped={encoded.DroppedTriangles} sections={mesh.Sections.Count} "
            + $"quads={mesh.Primitives.Count(p => CompressedMesh.TypeOf(p) == CompressedPrimitiveType.Quad)} shared={mesh.SharedVertices.Length} "
            + $"runs={mesh.Runs.Length} max-error={worst:0.#####} check=clean rewrite=same-bytes", worst);
    }

    /**
     * Each decoded triangle matches its source triangle, up to rotation, within the quantization
     * step; its filter and user data match; and the decoded triangles are the source triangles.
     */
    internal static double Compare(CollisionShape shape, EncodedCompressedMesh encoded, CompressedMeshSource source, CompressedMesh mesh, string name)
    {
        Check(shape.TriangleCount == encoded.SourceTriangles.Length, $"{name}: triangle count differs from the encoder's map.");
        int kept = source.Indices.Count / 3 - encoded.DroppedTriangles;
        Check(encoded.SourceTriangles.Distinct().Count() == kept && encoded.SourceTriangles.Length == kept, $"{name}: triangles are lost or repeated.");
        double tolerance = Tolerance(mesh), worst = 0;
        for (int i = 0; i < shape.TriangleCount; i++)
        {
            int t = encoded.SourceTriangles[i];
            double best = double.MaxValue;
            for (int r = 0; r < 3; r++)
            {
                double e = 0;
                for (int k = 0; k < 3; k++)
                    e = Math.Max(e, MaxAxis(shape.Vertices[shape.Indices[3 * i + k]], source.Vertices[source.Indices[3 * t + (k + r) % 3]]));
                best = Math.Min(best, e);
            }
            worst = Math.Max(worst, best);
            uint key = shape.TriangleKeys[i];
            var run = mesh.RunOf(mesh.Sections[(int)(key >> 8)], (int)(key >> 1) & 0x7F);
            Check(run.FilterIndex == (source.FilterIndices?[t] ?? 0) && run.UserDataIndex == (source.UserDataIndices?[t] ?? 0),
                $"{name}: triangle {i} lost its filter or user data.");
        }
        Check(worst <= tolerance, $"{name}: a vertex is {worst} off, above {tolerance}.");
        return worst;
    }

    /** Half the coarsest quantization step of the mesh, plus float slack. */
    internal static double Tolerance(CompressedMesh mesh)
    {
        var extent = mesh.DomainMax - mesh.DomainMin;
        double step = Math.Max(Math.Max(extent.X, extent.Y) / 0x1FFFFF, extent.Z / 0x3FFFFF);
        foreach (var section in mesh.Sections) step = Math.Max(step, section.Codec.Skip(3).Max());
        double magnitude = Math.Max(1, new[] { mesh.DomainMin, mesh.DomainMax }.Max(v => Math.Max(Math.Abs(v.X), Math.Max(Math.Abs(v.Y), Math.Abs(v.Z)))));
        return step / 2 + 4 * magnitude * Math.Pow(2, -23);
    }

    internal static double MaxAxis(Vector3 a, Vector3 b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Max(Math.Abs(a.Y - b.Y), Math.Abs(a.Z - b.Z)));
}
