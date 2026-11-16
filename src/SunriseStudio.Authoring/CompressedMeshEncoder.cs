using System.Numerics;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Authoring;

/**
 * Triangles to encode as a compressed mesh. Filter and user data indices are per triangle and
 * index the palettes; with an empty palette the index itself is the value.
 */
public sealed record CompressedMeshSource(IReadOnlyList<Vector3> Vertices, IReadOnlyList<int> Indices)
{
    public IReadOnlyList<byte>? FilterIndices { get; init; }
    public IReadOnlyList<byte>? UserDataIndices { get; init; }
    public IReadOnlyList<uint> FilterPalette { get; init; } = [];
    public IReadOnlyList<uint> UserDataPalette { get; init; } = [];
    public float ConvexRadius { get; init; }
    /** Object bytes before the tree of the replaced mesh; null uses the installed defaults. */
    public byte[]? Prefix { get; init; }
    /** Largest packed vertex step; a section with a coarser step stores its vertices as shared. */
    public float MaxPackedStep { get; init; } = 0.004f;
}

/** An encoded mesh and, per decoded triangle, the source triangle it came from. */
public sealed record EncodedCompressedMesh(CompressedMesh Mesh, int[] SourceTriangles, int DroppedTriangles);

/**
 * Builds a compressed mesh from triangles: sections of at most 128 primitives and 255 vertices,
 * packed 11/11/10 vertices over each section's box, shared 21/21/22 vertices over the mesh domain
 * for vertices used by several sections, section and top trees, and one data run per value range.
 */
public static class CompressedMeshEncoder
{
    // A primitive index is a byte, so a section names at most 255 vertices.
    private const int MaxSectionVertices = 255;
    // A section's shared indices are u16 above its page base.
    private const int PageSize = 65536;
    private const uint SharedMaxXY = 0x1FFFFF, SharedMaxZ = 0x3FFFFF;

    private sealed class Primitive
    {
        public required int[] Vertices { get; init; }
        public required int First { get; init; }
        public int Second { get; init; } = -1;
        public required uint Value { get; init; }
        public Vector3 Center { get; set; }
    }

    /**
     * Encodes the triangles. Triangles that repeat a vertex index are dropped.
     * @return The mesh, which passes CompressedMesh.Check, and the source of each decoded triangle.
     */
    public static EncodedCompressedMesh Encode(CompressedMeshSource source)
    {
        if (source.Indices.Count % 3 != 0) throw new ArgumentException("Indices must come in threes.", nameof(source));
        if (source.FilterPalette.Count > 256 || source.UserDataPalette.Count > 256) throw new ArgumentException("A palette holds at most 256 values.");
        var (primitives, dropped) = Primitives(source);
        if (primitives.Count == 0) throw new ArgumentException("No triangle with three distinct vertices.", nameof(source));
        var positions = source.Vertices;
        foreach (var primitive in primitives)
            primitive.Center = primitive.Vertices.Aggregate(Vector3.Zero, (sum, v) => sum + positions[v]) / primitive.Vertices.Length;

        var groups = new List<List<Primitive>>();
        Partition(primitives, groups);
        if (groups.Count > MeshTree.MaxSections) throw new NotSupportedException("The mesh needs more sections than a top leaf can name.");

        var used = primitives.SelectMany(p => p.Vertices).Distinct().ToArray();
        var domainMin = used.Select(v => positions[v]).Aggregate(Vector3.Min);
        var domainMax = used.Select(v => positions[v]).Aggregate(Vector3.Max);
        // Pad the domain so float rounding of decoded vertices stays inside it.
        (domainMin, domainMax) = PaddedDomain(domainMin, domainMax);
        var mesh = new CompressedMesh
        {
            Prefix = source.Prefix?.ToArray() ?? DefaultPrefix(source.FilterPalette.Count > 0 || source.UserDataPalette.Count > 0),
            ConvexRadius = source.ConvexRadius, WeldingType = CompressedMesh.NoWelding,
            HasPerPrimitiveCollisionFilterInfo = source.FilterPalette.Count > 0, HasPerPrimitiveUserData = source.UserDataPalette.Count > 0,
            FilterPalette = source.FilterPalette.ToArray(), UserDataPalette = source.UserDataPalette.ToArray(),
            DomainMin = domainMin, DomainMax = domainMax
        };

        // A vertex used by several sections is shared, and so is every vertex of a coarse section.
        var owners = new Dictionary<int, int>();
        for (int g = 0; g < groups.Count; g++)
            foreach (int v in groups[g].SelectMany(p => p.Vertices).Distinct())
                owners[v] = owners.TryGetValue(v, out int other) && other != g ? -1 : g;
        var coarse = new bool[groups.Count];
        for (int g = 0; g < groups.Count; g++)
        {
            var own = groups[g].SelectMany(p => p.Vertices).Distinct().Where(v => owners[v] == g).Select(v => positions[v]).ToArray();
            if (own.Length == 0) continue;
            var extent = own.Aggregate(Vector3.Max) - own.Aggregate(Vector3.Min);
            coarse[g] = extent.X / 2047 > source.MaxPackedStep || extent.Y / 2047 > source.MaxPackedStep || extent.Z / 1023 > source.MaxPackedStep;
        }
        bool IsShared(int v) => owners[v] < 0 || coarse[owners[v]];

        // Top tree topology comes from the source boxes; its codes are refit to the decoded boxes.
        var sourceBoxes = groups.Select(g => Box(g.SelectMany(p => p.Vertices).Select(v => positions[v]))).ToArray();
        mesh.TopNodes = MeshTree.Build(MeshTreeKind.Top, sourceBoxes, mesh.Domain, out int[] sectionOrder);

        // A section reads shared vertices page * 65536 to page * 65536 + 65535; a vertex the window lacks is stored again.
        var sharedNumber = new Dictionary<int, int>();
        int page = 0;
        var sharedVertices = new List<ulong>();
        var sharedIndices = new List<ushort>();
        var packed = new List<uint>();
        var primitiveWords = new List<uint>();
        var runs = new List<CompressedMeshRun>();
        var sourceTriangles = new List<int>();
        foreach (int g in sectionOrder)
        {
            var group = groups[g];
            var vertices = group.SelectMany(p => p.Vertices).Distinct().ToArray();
            var own = vertices.Where(v => !IsShared(v)).ToArray();
            var common = vertices.Where(IsShared).ToArray();
            var slot = new Dictionary<int, int>();
            foreach (int v in own) slot[v] = slot.Count;
            foreach (int v in common) slot[v] = slot.Count;
            var section = new CompressedMeshSection
            {
                FirstPackedVertex = packed.Count, PackedCount = own.Length, NumPackedVertices = own.Length,
                SharedIndexStart = sharedIndices.Count, NumSharedIndices = common.Length,
                FirstPrimitive = primitiveWords.Count, PrimitiveCount = group.Count, FirstRun = runs.Count
            };
            section.Codec = Codec(own.Select(v => positions[v]).ToArray());
            foreach (int v in own) packed.Add(Pack(section.Codec, positions[v]));
            bool InWindow(int v) => sharedNumber.TryGetValue(v, out int n) && n >= page * PageSize && n < (page + 1) * PageSize;
            if (sharedVertices.Count + common.Count(v => !InWindow(v)) > (page + 1) * PageSize)
            {
                if (++page > byte.MaxValue) throw new NotSupportedException("The mesh needs more than 256 pages of shared vertices.");
                // The new page starts at its base; the few slots below it stay unused.
                while (sharedVertices.Count < page * PageSize) sharedVertices.Add(0);
            }
            section.Page = page;
            foreach (int v in common)
            {
                if (!InWindow(v))
                {
                    sharedNumber[v] = sharedVertices.Count;
                    sharedVertices.Add(PackShared(mesh, positions[v]));
                }
                sharedIndices.Add((ushort)(sharedNumber[v] - page * PageSize));
            }
            // Leaf boxes hold the decoded vertices, so the tree encloses what the client reads.
            Vector3 Decoded(int v) => slot[v] < own.Length
                ? CompressedMesh.DecodePacked(section.Codec, packed[section.FirstPackedVertex + slot[v]])
                : mesh.DecodeShared(sharedVertices[sharedNumber[v]]);
            var boxes = group.Select(p => Box(p.Vertices.Select(Decoded))).ToArray();
            var domain = boxes.Aggregate((a, b) => a.Union(b));
            section.DomainMin = domain.Min;
            section.DomainMax = domain.Max;
            section.Nodes = MeshTree.Build(MeshTreeKind.Section, boxes, section.Domain, out int[] order);
            for (int k = 0; k < order.Length; k++)
            {
                var primitive = group[order[k]];
                int[] s = primitive.Vertices.Select(v => slot[v]).ToArray();
                primitiveWords.Add((uint)(s[0] | s[1] << 8 | s[2] << 16 | (s.Length == 4 ? s[3] : s[2]) << 24));
                sourceTriangles.Add(primitive.First);
                if (primitive.Second >= 0) sourceTriangles.Add(primitive.Second);
                if (runs.Count > section.FirstRun && runs[^1].Value == primitive.Value && runs[^1].Count < byte.MaxValue)
                    runs[^1] = runs[^1] with { Count = (byte)(runs[^1].Count + 1) };
                else runs.Add(new CompressedMeshRun(primitive.Value, (byte)k, 1));
            }
            section.RunCount = runs.Count - section.FirstRun;
            mesh.Sections.Add(section);
        }
        mesh.PackedVertices = packed.ToArray();
        mesh.SharedVertices = sharedVertices.ToArray();
        mesh.SharedIndices = sharedIndices.ToArray();
        mesh.Primitives = primitiveWords.ToArray();
        mesh.Runs = runs.ToArray();
        var topLeaves = MeshTree.Walk(MeshTreeKind.Top, mesh.TopNodes, mesh.Domain, mesh.Sections.Count).Where(n => n.Right < 0);
        foreach (var leaf in topLeaves) mesh.Sections[leaf.Leaf].LeafIndex = leaf.Index;
        mesh.TopNodes = MeshTree.Refit(MeshTreeKind.Top, mesh.TopNodes, mesh.Domain, mesh.Sections.Select(s => s.Domain).ToArray());
        SetKeys(mesh);
        var problems = mesh.Check();
        if (problems.Count > 0) throw new InvalidDataException($"Encoded mesh breaks an invariant: {problems[0]}");
        return new EncodedCompressedMesh(mesh, sourceTriangles.ToArray(), dropped);
    }

    /** Recomputes the key count, the largest key and its width from the primitives. */
    public static void SetKeys(CompressedMesh mesh)
    {
        var keys = mesh.Keys().ToArray();
        mesh.NumPrimitiveKeys = keys.Length;
        mesh.MaxKeyValue = keys.Length == 0 ? 0 : keys.Max();
        mesh.BitsPerKey = CompressedMesh.BitLength(mesh.MaxKeyValue + 1);
    }

    /** Offset [0..2] is the minimum; step [3..5] spreads the extent over 2047, 2047 and 1023. */
    public static float[] Codec(IReadOnlyList<Vector3> points)
    {
        if (points.Count == 0) return new float[6];
        var min = points.Aggregate(Vector3.Min);
        var max = points.Aggregate(Vector3.Max);
        return [min.X, min.Y, min.Z, (max.X - min.X) / 2047f, (max.Y - min.Y) / 2047f, (max.Z - min.Z) / 1023f];
    }

    /** A packed vertex in 11, 11 and 10 bits against a section codec. */
    public static uint Pack(float[] codec, Vector3 v) =>
        Quantize(v.X, codec[0], codec[3], 2047) | Quantize(v.Y, codec[1], codec[4], 2047) << 11 | Quantize(v.Z, codec[2], codec[5], 1023) << 22;

    /** A shared vertex in 21, 21 and 22 bits over the mesh domain. */
    public static ulong PackShared(CompressedMesh mesh, Vector3 v)
    {
        var (min, max) = (mesh.DomainMin, mesh.DomainMax);
        return QuantizeRange(v.X, min.X, max.X, SharedMaxXY) | (ulong)QuantizeRange(v.Y, min.Y, max.Y, SharedMaxXY) << 21
            | (ulong)QuantizeRange(v.Z, min.Z, max.Z, SharedMaxZ) << 42;
    }

    internal static uint Quantize(float value, float offset, float step, uint max) =>
        step <= 0 ? 0 : (uint)Math.Clamp(Math.Round(((double)value - offset) / step), 0, max);

    internal static uint QuantizeRange(double value, double min, double max, uint steps) =>
        max <= min ? 0 : (uint)Math.Clamp(Math.Round((value - min) / (max - min) * steps), 0, steps);

    internal static CompoundBox Box(IEnumerable<Vector3> points)
    {
        var list = points.ToArray();
        return CompoundBox.From(list.Aggregate(Vector3.Min), list.Aggregate(Vector3.Max));
    }

    /**
     * Installed object bytes before the tree: dispatch byte 4 at +0x11, tree type 3 at +0x20, and
     * user data 0xFFFFFFFF at +0x18 on meshes with palettes.
     */
    public static byte[] DefaultPrefix(bool palettes)
    {
        var prefix = new byte[CompressedMesh.PrefixSize];
        prefix[0x11] = 4;
        prefix[0x20] = 3;
        if (palettes) prefix.AsSpan(0x18, 4).Fill(0xFF);
        return prefix;
    }

    /** Triangles; a triangle and the next one sharing its 0-2 edge merge into a quad. */
    private static (List<Primitive>, int Dropped) Primitives(CompressedMeshSource source)
    {
        int count = source.Indices.Count / 3, dropped = 0;
        var result = new List<Primitive>();
        uint ValueOf(int t) => (uint)((source.FilterIndices?[t] ?? 0) | (source.UserDataIndices?[t] ?? 0) << 8);
        int[] Triangle(int t) => [source.Indices[3 * t], source.Indices[3 * t + 1], source.Indices[3 * t + 2]];
        bool Valid(int[] t) => t[0] != t[1] && t[1] != t[2] && t[0] != t[2] && t.All(v => v >= 0 && v < source.Vertices.Count);
        for (int t = 0; t < count; t++)
        {
            var first = Triangle(t);
            if (!Valid(first)) { dropped++; continue; }
            if (t + 1 < count && Valid(Triangle(t + 1)) && ValueOf(t + 1) == ValueOf(t) && Quad(first, Triangle(t + 1)) is { } quad)
            {
                result.Add(new Primitive { Vertices = quad, First = t, Second = t + 1, Value = ValueOf(t) });
                t++;
                continue;
            }
            result.Add(new Primitive { Vertices = first, First = t, Value = ValueOf(t) });
        }
        return (result, dropped);
    }

    /** A quad (a, b, c, d) decodes as (a, b, c) then (a, c, d); both triangles may be rotated. */
    private static int[]? Quad(int[] first, int[] second)
    {
        for (int r = 0; r < 3; r++)
        {
            int a = first[r], b = first[(r + 1) % 3], c = first[(r + 2) % 3];
            for (int q = 0; q < 3; q++)
                if (second[q] == a && second[(q + 1) % 3] == c)
                {
                    int d = second[(q + 2) % 3];
                    if (d != a && d != b && d != c) return [a, b, c, d];
                }
        }
        return null;
    }

    private static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    /** Padding must fit native float bounds without overflowing an intermediate product. */
    internal static (Vector3 Min, Vector3 Max) PaddedDomain(Vector3 min, Vector3 max)
    {
        double pad = 4.0 * MaxAbs(min, max) * Math.Pow(2, -23);
        var low = new Vector3((float)(min.X - pad), (float)(min.Y - pad), (float)(min.Z - pad));
        var high = new Vector3((float)(max.X + pad), (float)(max.Y + pad), (float)(max.Z + pad));
        if (!float.IsFinite(low.X) || !float.IsFinite(low.Y) || !float.IsFinite(low.Z)
            || !float.IsFinite(high.X) || !float.IsFinite(high.Y) || !float.IsFinite(high.Z))
            throw new InvalidDataException("Padded collision bounds cannot be represented as finite floats.");
        return (low, high);
    }

    internal static float MaxAbs(Vector3 a, Vector3 b) =>
        new[] { 1f, MathF.Abs(a.X), MathF.Abs(a.Y), MathF.Abs(a.Z), MathF.Abs(b.X), MathF.Abs(b.Y), MathF.Abs(b.Z) }.Max();

    /** Median splits on the longest axis of primitive centers until each group fits one section. */
    private static void Partition(List<Primitive> primitives, List<List<Primitive>> groups)
    {
        if (primitives.Count <= MeshTree.MaxPrimitives && primitives.SelectMany(p => p.Vertices).Distinct().Count() <= MaxSectionVertices)
        {
            groups.Add(primitives);
            return;
        }
        var min = primitives.Select(p => p.Center).Aggregate(Vector3.Min);
        var extent = primitives.Select(p => p.Center).Aggregate(Vector3.Max) - min;
        int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        var sorted = primitives.OrderBy(p => Axis(p.Center, axis)).ThenBy(p => p.First).ToList();
        int half = sorted.Count / 2;
        Partition(sorted[..half], groups);
        Partition(sorted[half..], groups);
    }
}
