using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Authoring;

/**
 * Triangles to a compressed mesh exactly as World Studio's landscape/collision_cooker.py cooks
 * terrain carriers (walked in game on 2026-09-21, studio_0fabaf80e5ae): every vertex shared at
 * 21/21/22 bits over the outward-rounded domain, welding type 6, sections of at most 128 triangles
 * and 255 vertices by median splits, conservative 4-byte section and 5-byte top trees.
 */
public static class GroundCollisionCooker
{
    public const double DefaultMaximumError = 1.0 / 8192;
    // 1/(2^21-1), 1/(2^21-1), 1/(2^22-1) as the float32 bits stored in the executable at RVA 0x1F11EC0.
    private static readonly float[] SharedScale =
        [BitConverter.UInt32BitsToSingle(0x35000004), BitConverter.UInt32BitsToSingle(0x35000004), BitConverter.UInt32BitsToSingle(0x34800002)];
    // Section and top tree nodes: squared nibbles in steps of 1/226 of the parent box (RVA 0xF56C0).
    private static readonly float NodeScale = (float)(1.0 / 226);
    private const int MaxVertices = 65536, MaxTriangles = 262144, MaxSections = 32768;

    /** A cooked mesh, the largest vertex error and, per section, the source triangles it holds. */
    public sealed record Result(CompressedMesh Mesh, double MaximumError, IReadOnlyList<int[]> Sections);

    private sealed record Box(float[] Low, float[] High);

    /**
     * Cooks triangles with their winding kept. The mesh has no per-primitive channels, user data
     * 0xFFFFFFFF and tree type 3, as every retail terrain carrier payload stores (600 of 600).
     */
    public static Result Cook(IReadOnlyList<(double X, double Y, double Z)> vertices, IReadOnlyList<(int A, int B, int C)> triangles,
        double maximumError = DefaultMaximumError)
    {
        if (vertices.Count is < 3 or > MaxVertices) throw new InvalidDataException($"A collision mesh has 3 to {MaxVertices} vertices.");
        if (triangles.Count is < 1 or > MaxTriangles) throw new InvalidDataException($"A collision mesh has 1 to {MaxTriangles} triangles.");
        foreach (var (a, b, c) in triangles)
            if (a == b || b == c || a == c || new[] { a, b, c }.Any(v => v < 0 || v >= vertices.Count))
                throw new InvalidDataException("A collision triangle needs three distinct vertices.");
        var low = new double[3]; var high = new double[3];
        for (int axis = 0; axis < 3; axis++)
        {
            low[axis] = vertices.Min(v => Axis(v, axis));
            high[axis] = vertices.Max(v => Axis(v, axis));
        }
        var domain = new Box(low.Select(v => Outward(v, true)).ToArray(), high.Select(v => Outward(v, false)).ToArray());
        var steps = Enumerable.Range(0, 3).Select(a => F32((double)F32((double)domain.High[a] - domain.Low[a]) * SharedScale[a])).ToArray();
        var decoded = new float[vertices.Count][];
        var words = new ulong[vertices.Count];
        double worst = 0;
        for (int i = 0; i < vertices.Count; i++)
        {
            var codes = new long[3];
            var point = new float[3];
            for (int a = 0; a < 3; a++)
            {
                long maximum = (1L << (a == 2 ? 22 : 21)) - 1;
                long code = steps[a] != 0 ? Math.Min(maximum, Math.Max(0, (long)Math.Round((Axis(vertices[i], a) - domain.Low[a]) / steps[a]))) : 0;
                float value = F32((double)F32(code * (double)steps[a]) + domain.Low[a]);
                while (code != 0 && value > domain.High[a])
                {
                    code--;
                    value = F32((double)F32(code * (double)steps[a]) + domain.Low[a]);
                }
                if (value < domain.Low[a] || value > domain.High[a]) throw new InvalidDataException("A collision vertex leaves its quantized domain.");
                codes[a] = code;
                point[a] = value;
            }
            double dx = vertices[i].X - point[0], dy = vertices[i].Y - point[1], dz = vertices[i].Z - point[2];
            double error = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (error > maximumError) throw new InvalidDataException("A collision vertex moves more than the allowed error.");
            worst = Math.Max(worst, error);
            words[i] = (ulong)codes[0] | (ulong)codes[1] << 21 | (ulong)codes[2] << 42;
            decoded[i] = point;
        }
        var boxes = new Box[triangles.Count];
        for (int t = 0; t < triangles.Count; t++)
        {
            var (a, b, c) = triangles[t];
            var before = Normal(Point(vertices[a]), Point(vertices[b]), Point(vertices[c]));
            var after = Normal(Doubles(decoded[a]), Doubles(decoded[b]), Doubles(decoded[c]));
            if (before[0] * before[0] + before[1] * before[1] + before[2] * before[2] <= 0)
                throw new InvalidDataException("A collision triangle has zero area.");
            if (before[0] * after[0] + before[1] * after[1] + before[2] * after[2] <= 0)
                throw new InvalidDataException("Quantization collapses or reverses a collision triangle.");
            boxes[t] = Bounds(new[] { decoded[a], decoded[b], decoded[c] });
        }
        var groups = new List<int[]>();
        Partition(Enumerable.Range(0, triangles.Count).ToArray(), triangles, boxes, groups);
        if (groups.Count > MaxSections) throw new InvalidDataException("A collision mesh needs too many sections.");
        var mesh = new CompressedMesh
        {
            Prefix = Prefix(),
            ConvexRadius = 0,
            WeldingType = CompressedMesh.NoWelding,
            DomainMin = new(domain.Low[0], domain.Low[1], domain.Low[2]),
            DomainMinW = 1,
            DomainMax = new(domain.High[0], domain.High[1], domain.High[2]),
            DomainMaxW = 1
        };
        var sectionBoxes = new List<Box>();
        var primitives = new List<uint>();
        var sharedIndices = new List<ushort>();
        var runs = new List<CompressedMeshRun>();
        var sections = new List<CompressedMeshSection>();
        uint maxKey = 0;
        int keys = 0;
        foreach (var ids in groups)
        {
            var used = ids.SelectMany(t => new[] { triangles[t].A, triangles[t].B, triangles[t].C }).Distinct().Order().ToArray();
            var local = used.Select((v, i) => (v, i)).ToDictionary(x => x.v, x => x.i);
            var box = Bounds(used.Select(v => decoded[v]));
            var nodes = Tree(ids.Select(t => boxes[t]).ToArray(), box, 4, out _);
            var section = new CompressedMeshSection
            {
                Nodes = nodes,
                DomainMin = new(box.Low[0], box.Low[1], box.Low[2]),
                DomainMinW = 1,
                DomainMax = new(box.High[0], box.High[1], box.High[2]),
                DomainMaxW = 1,
                Codec = [0, 0, 0, 1, 1, 1],
                FirstPackedVertex = 0,
                SharedIndexStart = sharedIndices.Count,
                PackedCount = 0,
                FirstPrimitive = primitives.Count,
                PrimitiveCount = ids.Length,
                FirstRun = runs.Count,
                RunCount = 1,
                NumPackedVertices = 0,
                NumSharedIndices = used.Length
            };
            foreach (int t in ids)
            {
                var (a, b, c) = triangles[t];
                primitives.Add((uint)(local[a] | local[b] << 8 | local[c] << 16 | local[c] << 24));
            }
            runs.Add(new CompressedMeshRun(0, 0, checked((byte)ids.Length)));
            sharedIndices.AddRange(used.Select(v => checked((ushort)v)));
            int index = sections.Count;
            for (int p = 0; p < ids.Length; p++) { keys++; maxKey = Math.Max(maxKey, (uint)(index << 8 | p << 1)); }
            sections.Add(section);
            sectionBoxes.Add(box);
        }
        mesh.TopNodes = Tree(sectionBoxes.ToArray(), domain, 5, out var leaves);
        for (int s = 0; s < sections.Count; s++) sections[s].LeafIndex = leaves[s];
        mesh.Sections = sections;
        mesh.Primitives = primitives.ToArray();
        mesh.SharedIndices = sharedIndices.ToArray();
        mesh.SharedVertices = words;
        mesh.Runs = runs.ToArray();
        mesh.NumPrimitiveKeys = keys;
        mesh.MaxKeyValue = maxKey;
        mesh.BitsPerKey = CompressedMesh.BitLength(maxKey + 1);
        return new(mesh, worst, groups);
    }

    /** The per-mesh packfile of a cooked carrier payload, framed as installed per-mesh shapes are. */
    public static byte[] Payload(CompressedMesh mesh) => CollisionPackfileWriter.WriteCompressedMesh(mesh);

    /** Object bytes before the tree: dispatch type 4 at +0x11, user data 0xFFFFFFFF at +0x18, tree type 3 at +0x20. */
    private static byte[] Prefix()
    {
        var prefix = new byte[CompressedMesh.PrefixSize];
        prefix[0x11] = 4;
        prefix.AsSpan(0x18, 4).Fill(0xFF);
        prefix[0x20] = 3;
        return prefix;
    }

    /** Median splits on the longest axis of the triangle boxes until a group has 128 triangles and 255 vertices. */
    private static void Partition(int[] ids, IReadOnlyList<(int A, int B, int C)> triangles, Box[] boxes, List<int[]> groups)
    {
        int used = ids.SelectMany(t => new[] { triangles[t].A, triangles[t].B, triangles[t].C }).Distinct().Count();
        if (ids.Length <= 128 && used <= 255) { groups.Add(ids); return; }
        var box = Bounds(ids.SelectMany(t => new[] { boxes[t].Low, boxes[t].High }));
        int axis = ArgMax(a => (double)box.High[a] - box.Low[a]);
        var ordered = ids.OrderBy(t => (double)boxes[t].Low[axis] + boxes[t].High[axis]).ThenBy(t => t).ToArray();
        int middle = ordered.Length / 2;
        Partition(ordered[..middle], triangles, boxes, groups);
        Partition(ordered[middle..], triangles, boxes, groups);
    }

    /**
     * A conservative binary tree in node order: a leaf names its box, an inner node jumps to its right child.
     * Width 4 is a section tree (leaf << 1, jump | 1); width 5 the top tree (15-bit section, 0x80 | jump / 2).
     */
    private static byte[] Tree(Box[] boxes, Box domain, int width, out int[] leaves)
    {
        if (boxes.Length < 1 || boxes.Length > (width == 4 ? 128 : MaxSections)) throw new InvalidDataException("A collision tree exceeds its leaf encoding.");
        var nodes = new List<byte[]?>();
        var leafNodes = new int[boxes.Length];
        int Visit(int[] ids, Box parent)
        {
            int index = nodes.Count;
            nodes.Add(null);
            var desired = Bounds(ids.SelectMany(i => new[] { boxes[i].Low, boxes[i].High }));
            if (!Contains(parent, desired)) throw new InvalidDataException("A collision tree child leaves its parent.");
            var code = new int[3];
            for (int axis = 0; axis < 3; axis++)
            {
                int lowest = -1, highest = -1;
                for (int q = 0; q < 16; q++)
                {
                    var candidate = new int[3];
                    candidate[axis] = q << 4;
                    if (Decode(parent, candidate).Low[axis] <= desired.Low[axis]) lowest = q;
                    candidate[axis] = q;
                    if (Decode(parent, candidate).High[axis] >= desired.High[axis]) highest = q;
                }
                if (lowest < 0 || highest < 0) throw new InvalidDataException("A collision tree node has no conservative quantization.");
                code[axis] = lowest * 16 + highest;
            }
            if (index == 0) code = [0, 0, 0];
            var box = Decode(parent, code);
            if (!Contains(box, desired)) throw new InvalidDataException("A quantized collision tree node loses geometry.");
            byte[] payload;
            if (ids.Length == 1)
            {
                int leaf = ids[0];
                leafNodes[leaf] = index;
                payload = width == 4 ? [checked((byte)(leaf << 1))] : [(byte)(leaf >> 8), (byte)(leaf & 255)];
            }
            else
            {
                int axis = ArgMax(a => ids.Max(i => (double)boxes[i].Low[a] + boxes[i].High[a]) - ids.Min(i => (double)boxes[i].Low[a] + boxes[i].High[a]));
                var ordered = ids.OrderBy(i => (double)boxes[i].Low[axis] + boxes[i].High[axis]).ThenBy(i => i).ToArray();
                int middle = ordered.Length / 2;
                int left = Visit(ordered[..middle], box), right = Visit(ordered[middle..], box), jump = right - index;
                if (left != index + 1 || jump % 2 != 0) throw new InvalidDataException("Invalid collision tree construction.");
                payload = width == 4 ? [checked((byte)(jump | 1))] : [(byte)(128 | ((jump / 2) >> 8)), (byte)((jump / 2) & 255)];
            }
            nodes[index] = [(byte)code[0], (byte)code[1], (byte)code[2], .. payload];
            return index;
        }
        Visit(Enumerable.Range(0, boxes.Length).ToArray(), domain);
        leaves = leafNodes;
        return nodes.SelectMany(node => node!).ToArray();
    }

    /** Node bounds as the client decodes them, in float32 at each step. */
    private static Box Decode(Box parent, int[] code)
    {
        var low = new float[3]; var high = new float[3];
        for (int axis = 0; axis < 3; axis++)
        {
            float step = F32((double)F32((double)parent.High[axis] - parent.Low[axis]) * NodeScale);
            int lo = code[axis] >> 4, hi = code[axis] & 15;
            low[axis] = F32(parent.Low[axis] + (double)F32(lo * lo * (double)step));
            high[axis] = F32(parent.High[axis] - (double)F32(hi * hi * (double)step));
        }
        return new(low, high);
    }

    private static bool Contains(Box outer, Box inner)
    {
        for (int i = 0; i < 3; i++)
            if (!(outer.Low[i] <= inner.Low[i] && inner.Low[i] <= inner.High[i] && inner.High[i] <= outer.High[i])) return false;
        return true;
    }

    private static Box Bounds(IEnumerable<float[]> points)
    {
        var low = new[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
        var high = new[] { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
        foreach (var p in points)
            for (int a = 0; a < 3; a++) { low[a] = Math.Min(low[a], p[a]); high[a] = Math.Max(high[a], p[a]); }
        return new(low, high);
    }

    /** float32 of a bound rounded so the enclosed interval never shrinks. */
    internal static float Outward(double value, bool lower)
    {
        float rounded = (float)value;
        if (lower && rounded > value || !lower && rounded < value)
        {
            int bits = BitConverter.SingleToInt32Bits(rounded);
            bits += lower ? (rounded > 0 ? -1 : 1) : (rounded >= 0 ? 1 : -1);
            rounded = BitConverter.Int32BitsToSingle(bits);
        }
        return rounded;
    }

    private static int ArgMax(Func<int, double> value)
    {
        int best = 0;
        for (int a = 1; a < 3; a++) if (value(a) > value(best)) best = a;
        return best;
    }

    private static double[] Normal(double[] a, double[] b, double[] c)
    {
        double[] u = [b[0] - a[0], b[1] - a[1], b[2] - a[2]], v = [c[0] - a[0], c[1] - a[1], c[2] - a[2]];
        return [u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0]];
    }

    private static float F32(double value) => (float)value;
    private static double Axis((double X, double Y, double Z) v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
    private static double[] Point((double X, double Y, double Z) v) => [v.X, v.Y, v.Z];
    private static double[] Doubles(float[] v) => [v[0], v[1], v[2]];
}
