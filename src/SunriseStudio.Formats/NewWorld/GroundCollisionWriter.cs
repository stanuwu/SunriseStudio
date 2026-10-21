using System.Buffers.Binary;
using System.Text;

namespace SunriseStudio.Formats.NewWorld;

/** A cooked triangle shape and the fields the compound reads from it. */
public sealed record CookedShape(byte[] Packfile, double[][] Domain, int KeyBits);

/**
 * Fresh Havok collision for generated grounds: a version-9 hkpBvCompressedMeshShape per ground, one
 * hkpStaticCompoundShape over them and the 80808A15 wrapper that binds surfaces and i&lt;N&gt; imports
 * (World Studio landscape/collision_cooker, generated_compound, collision_wrapper).
 */
public static class GroundCollisionWriter
{
    public const uint WrapperClass = 0x80808A15;
    private const double MaxError = 1.0 / 8192;
    // 1/(2^21-1), 1/(2^21-1), 1/(2^22-1) as the float32 values stored in the executable.
    private static readonly double[] SharedScale =
        [BitConverter.Int32BitsToSingle(0x35000004), BitConverter.Int32BitsToSingle(0x35000004), BitConverter.Int32BitsToSingle(0x34800002)];
    private static readonly byte[] Layout = [0x09, 0, 0, 0, 0x08, 0x01, 0, 0x01];
    // Collision filter, user data and user string palettes, tree nodes, sections, primitives, shared vertex
    // indices, packed vertices, shared vertices and primitive data runs, as offsets from the +0x28 subobject.
    private static readonly (string Name, int Offset)[] Slots =
    [
        ("collision_filter_palette", 0x10), ("user_data_palette", 0x20), ("user_string_palette", 0x30), ("tree_nodes", 0x48),
        ("sections", 0x88), ("primitives", 0x98), ("shared_vertex_indices", 0xA8), ("packed_vertices", 0xB8),
        ("shared_vertices", 0xC8), ("primitive_data_runs", 0xD8)
    ];
    private const int Base = 0x28;

    private static double F32(double value) => (float)value;
    private static double Outward(double value, bool lower) => GroundVisualWriter.Outward(value, lower);

    private static double[][] Bounds(IEnumerable<double[]> points)
    {
        var list = points.ToArray();
        return [Enumerable.Range(0, 3).Select(a => list.Min(p => p[a])).ToArray(), Enumerable.Range(0, 3).Select(a => list.Max(p => p[a])).ToArray()];
    }

    private static double[] Normal(double[] a, double[] b, double[] c)
    {
        double ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2], vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];
        return [uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx];
    }

    /** Six-byte and five-byte tree node bounds: squared nibbles in steps of 1/226 of the parent box. */
    internal static double[][] DecodeBounds(double[][] parent, int[] code)
    {
        var lo = new double[3]; var hi = new double[3];
        for (int axis = 0; axis < 3; axis++)
        {
            double step = F32(F32(parent[1][axis] - parent[0][axis]) * F32(1.0 / 226));
            int low = code[axis] >> 4, high = code[axis] & 15;
            lo[axis] = F32(parent[0][axis] + F32(low * low * step));
            hi[axis] = F32(parent[1][axis] - F32(high * high * step));
        }
        return [lo, hi];
    }

    internal static bool Contains(double[][] outer, double[][] inner) =>
        Enumerable.Range(0, 3).All(i => outer[0][i] <= inner[0][i] && inner[0][i] <= inner[1][i] && inner[1][i] <= outer[1][i]);

    private static double[][] Union(double[][] a, double[][] b) =>
        [Enumerable.Range(0, 3).Select(i => Math.Min(a[0][i], b[0][i])).ToArray(), Enumerable.Range(0, 3).Select(i => Math.Max(a[1][i], b[1][i])).ToArray()];

    /** The largest conservative nibble pair per axis; throws when none encloses the wanted box. */
    private static int[] Quantize(double[][] parent, double[][] wanted)
    {
        var code = new int[3];
        for (int axis = 0; axis < 3; axis++)
        {
            int low = -1, high = -1;
            for (int q = 0; q < 16; q++)
            {
                var candidate = new int[3];
                candidate[axis] = q << 4;
                if (DecodeBounds(parent, candidate)[0][axis] <= wanted[0][axis]) low = q;
                candidate[axis] = q;
                if (DecodeBounds(parent, candidate)[1][axis] >= wanted[1][axis]) high = q;
            }
            if (low < 0 || high < 0) throw new InvalidDataException("No conservative collision node quantization.");
            code[axis] = low * 16 + high;
        }
        return code;
    }

    private static int ArgMax(Func<int, double> value)
    {
        int best = 0;
        for (int a = 1; a < 3; a++) if (value(a) > value(best)) best = a;
        return best;
    }

    /** Section (width 4) or top-level (width 5) bounding-volume tree over boxes inside domain. */
    private static (byte[] Nodes, Dictionary<int, int> Leaves, int Depth) Tree(IReadOnlyList<double[][]> boxes, double[][] domain, int width)
    {
        if (boxes.Count < 1 || boxes.Count > (width == 4 ? 128 : 32768)) throw new InvalidDataException("A collision tree exceeds its leaf encoding.");
        var nodes = new List<byte[]?>();
        var leaves = new Dictionary<int, int>();
        int maximumDepth = 0;
        int Visit(IReadOnlyList<int> ids, double[][] parent, int depth)
        {
            maximumDepth = Math.Max(maximumDepth, depth);
            int index = nodes.Count;
            nodes.Add(null);
            var desired = Bounds(ids.SelectMany(i => boxes[i]));
            if (!Contains(parent, desired)) throw new InvalidDataException("Collision child bounds exceed their parent.");
            var code = Quantize(parent, desired);
            if (index == 0) code = [0, 0, 0];
            var box = DecodeBounds(parent, code);
            if (!Contains(box, desired)) throw new InvalidDataException("A quantized collision node loses geometry.");
            byte[] payload;
            if (ids.Count == 1)
            {
                int leaf = ids[0];
                leaves[leaf] = index;
                payload = width == 4 ? [(byte)(leaf << 1)] : [(byte)(leaf >> 8), (byte)(leaf & 255)];
            }
            else
            {
                double Sum(int i, int a) => boxes[i][0][a] + boxes[i][1][a];
                int axis = ArgMax(a => ids.Max(i => Sum(i, a)) - ids.Min(i => Sum(i, a)));
                var ordered = ids.OrderBy(i => Sum(i, axis)).ThenBy(i => i).ToArray();
                int middle = ordered.Length / 2;
                int left = Visit(ordered[..middle], box, depth + 1);
                int right = Visit(ordered[middle..], box, depth + 1);
                int jump = right - index;
                if (left != index + 1 || jump % 2 != 0) throw new InvalidDataException("Invalid collision tree construction.");
                payload = width == 4 ? [(byte)(jump | 1)] : [(byte)(128 | ((jump / 2) >> 8)), (byte)((jump / 2) & 255)];
            }
            nodes[index] = [.. code.Select(value => (byte)value), .. payload];
            return index;
        }
        Visit(Enumerable.Range(0, boxes.Count).ToArray(), domain, 0);
        return (nodes.SelectMany(node => node!).ToArray(), leaves, maximumDepth);
    }

    private sealed record Part(byte[] Nodes, double[][] Bounds, int[] Shared, byte[][] Primitives);

    /** Cooks one triangle shape; every vertex lands within 1/8192 of its input and no triangle reverses. */
    public static CookedShape Cook(IReadOnlyList<double[]> vertices, IReadOnlyList<int[]> triangles)
    {
        if (vertices.Count is < 3 or > 65536 || triangles.Count is < 1 or > 262144)
            throw new InvalidDataException("A collision candidate needs 3 to 65536 vertices and 1 to 262144 triangles.");
        foreach (var tri in triangles)
            if (tri.Length != 3 || tri.Distinct().Count() != 3 || tri.Any(v => v < 0 || v >= vertices.Count))
                throw new InvalidDataException("A collision triangle needs three distinct valid vertex indices.");
        var raw = Bounds(vertices);
        double[][] domain = [raw[0].Select(v => Outward(v, true)).ToArray(), raw[1].Select(v => Outward(v, false)).ToArray()];
        var steps = Enumerable.Range(0, 3).Select(a => F32(F32(domain[1][a] - domain[0][a]) * SharedScale[a])).ToArray();
        var decoded = new List<double[]>();
        var words = new List<ulong>();
        foreach (var point in vertices)
        {
            var codes = new long[3]; var value = new double[3];
            for (int a = 0; a < 3; a++)
            {
                long maximum = (1L << (a == 2 ? 22 : 21)) - 1;
                long code = steps[a] != 0 ? Math.Min(maximum, Math.Max(0, (long)Math.Round((point[a] - domain[0][a]) / steps[a]))) : 0;
                double v = F32(F32(code * steps[a]) + domain[0][a]);
                while (code != 0 && v > domain[1][a]) { code--; v = F32(F32(code * steps[a]) + domain[0][a]); }
                if (!(domain[0][a] <= v && v <= domain[1][a])) throw new InvalidDataException("A collision vertex exceeds its quantized domain.");
                codes[a] = code; value[a] = v;
            }
            double dx = point[0] - value[0], dy = point[1] - value[1], dz = point[2] - value[2];
            if (Math.Sqrt(dx * dx + dy * dy + dz * dz) > MaxError) throw new InvalidDataException("A collision vertex error exceeds 1/8192.");
            words.Add((ulong)codes[0] | (ulong)codes[1] << 21 | (ulong)codes[2] << 42);
            decoded.Add(value);
        }
        var triBoxes = new List<double[][]>();
        foreach (var tri in triangles)
        {
            var before = Normal(vertices[tri[0]], vertices[tri[1]], vertices[tri[2]]);
            var after = Normal(decoded[tri[0]], decoded[tri[1]], decoded[tri[2]]);
            if (!(before[0] * before[0] + before[1] * before[1] + before[2] * before[2] > 0))
                throw new InvalidDataException("A collision triangle has zero area.");
            if (!(before[0] * after[0] + before[1] * after[1] + before[2] * after[2] > 0))
                throw new InvalidDataException("Quantization collapses or reverses a collision triangle.");
            triBoxes.Add(Bounds(tri.Select(v => decoded[v])));
        }
        var groups = new List<int[]>();
        void Partition(int[] ids)
        {
            if (ids.Length <= 128 && ids.SelectMany(i => triangles[i]).Distinct().Count() <= 255) { groups.Add(ids); return; }
            var box = Bounds(ids.SelectMany(i => triBoxes[i]));
            int axis = ArgMax(a => box[1][a] - box[0][a]);
            var ordered = ids.OrderBy(i => triBoxes[i][0][axis] + triBoxes[i][1][axis]).ThenBy(i => i).ToArray();
            int middle = ordered.Length / 2;
            Partition(ordered[..middle]);
            Partition(ordered[middle..]);
        }
        Partition(Enumerable.Range(0, triangles.Count).ToArray());
        var parts = new List<Part>();
        foreach (var ids in groups)
        {
            var used = ids.SelectMany(i => triangles[i]).Distinct().Order().ToArray();
            var local = used.Select((v, i) => (v, i)).ToDictionary(pair => pair.v, pair => pair.i);
            var box = Bounds(used.Select(i => decoded[i]));
            var (nodes, _, _) = Tree(ids.Select(i => triBoxes[i]).ToArray(), box, 4);
            parts.Add(new(nodes, box, used, ids.Select(i => new[] { (byte)local[triangles[i][0]], (byte)local[triangles[i][1]],
                (byte)local[triangles[i][2]], (byte)local[triangles[i][2]] }).ToArray()));
        }
        var (top, leaves, _) = Tree(parts.Select(part => part.Bounds).ToArray(), domain, 5);
        int keys = parts.Sum(part => part.Primitives.Length);
        int maximumKey = parts.Select((part, i) => (i << 8) | ((part.Primitives.Length - 1) << 1)).Max();
        return new(Serialize(domain, words, parts, top, leaves, keys, maximumKey), domain, BitLength(maximumKey + 1));
    }

    private static int BitLength(long value) => value == 0 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)value);

    /** A standalone pointer-free version-9 packfile with local fixups for the shape's arrays. */
    private static byte[] Serialize(double[][] domain, List<ulong> words, List<Part> parts, byte[] top,
        Dictionary<int, int> leaves, int keyCount, int maximumKey)
    {
        var data = new NativeBuffer(Base + 0xE8);
        var fixups = new SortedDictionary<int, int>();
        int Put(byte[] blob) { data.Pad(16, 0x7F); return data.Add(blob); }
        void Array(int at, int count, int target)
        {
            data.Put64(at, 0); data.Put32(at + 8, (uint)count); data.Put32(at + 12, (uint)count | 0x80000000);
            if (count != 0) fixups[at] = target;
        }
        int topAt = Put(top);
        int sectionsAt = Put(new byte[parts.Count * 96]);
        var primitives = new List<byte[]>(); var sharedIndices = new List<int>(); var runs = new List<byte[]>();
        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            int at = sectionsAt + i * 96;
            int nodeAt = Put(part.Nodes);
            Array(at, part.Nodes.Length / 4, nodeAt);
            data.PutF32s(at + 0x10, (float)part.Bounds[0][0], (float)part.Bounds[0][1], (float)part.Bounds[0][2], 1);
            data.PutF32s(at + 0x20, (float)part.Bounds[1][0], (float)part.Bounds[1][1], (float)part.Bounds[1][2], 1);
            data.PutF32s(at + 0x30, 0, 0, 0, 1, 1, 1);
            int count = part.Primitives.Length;
            // Attribute channels are disabled, so every primitive carries word 0 and one run covers the section.
            var sectionRuns = new List<(uint Value, int First, int Size)> { (0, 0, count) };
            data.Put32(at + 0x48, 0);
            data.Put32(at + 0x4C, (uint)sharedIndices.Count << 8);
            data.Put32(at + 0x50, (uint)primitives.Count << 8 | (uint)count);
            data.Put32(at + 0x54, (uint)runs.Count << 8 | (uint)sectionRuns.Count);
            data.Put8(at + 0x58, 0); data.Put8(at + 0x59, (byte)part.Shared.Length); data.Put16(at + 0x5A, (ushort)leaves[i]);
            primitives.AddRange(part.Primitives);
            sharedIndices.AddRange(part.Shared);
            foreach (var (value, first, size) in sectionRuns)
            {
                var run = new byte[8];
                BinaryPrimitives.WriteUInt32LittleEndian(run, value); run[4] = (byte)first; run[5] = (byte)size;
                runs.Add(run);
            }
        }
        var targets = new Dictionary<string, int> { ["tree_nodes"] = topAt, ["sections"] = sectionsAt };
        targets["primitives"] = Put(primitives.SelectMany(p => p).ToArray());
        targets["shared_vertex_indices"] = Put(NativeRow.U16s(sharedIndices.Select(i => (ushort)i).ToArray()));
        targets["shared_vertices"] = Put(words.SelectMany(BitConverter.GetBytes).ToArray());
        targets["primitive_data_runs"] = Put(runs.SelectMany(r => r).ToArray());
        var counts = new Dictionary<string, int>
        {
            ["tree_nodes"] = top.Length / 5,
            ["sections"] = parts.Count,
            ["primitives"] = primitives.Count,
            ["shared_vertex_indices"] = sharedIndices.Count,
            ["shared_vertices"] = words.Count,
            ["primitive_data_runs"] = runs.Count
        };
        foreach (var (name, offset) in Slots) Array(Base + offset, counts.GetValueOrDefault(name), targets.GetValueOrDefault(name));
        // hkcdShape +0x10: type 0, dispatch type 4, as every installed mesh child stores it.
        data.Put8(0x11, 4);
        data.PutF32(Base + 8, 0); data.Put8(Base + 12, 6);
        data.PutF32s(Base + 0x58, (float)domain[0][0], (float)domain[0][1], (float)domain[0][2], 1);
        data.PutF32s(Base + 0x68, (float)domain[1][0], (float)domain[1][1], (float)domain[1][2], 1);
        data.PutI32(Base + 0x78, keyCount); data.PutI32(Base + 0x7C, BitLength(maximumKey + 1)); data.Put32(Base + 0x80, (uint)maximumKey);
        data.Pad(16, 0x7F);
        var local = new NativeBuffer(0);
        foreach (var (at, target) in fixups) local.Add(NativeRow.U32s((uint)at, (uint)target));
        local.Pad(16, 0xFF);
        return Packfile("hkpBvCompressedMeshShape", data.ToArray(), local.ToArray(), []);
    }

    /** Header, class name table, data, local fixups, one virtual fixup at 0 and optional imports. */
    private static byte[] Packfile(string className, byte[] data, byte[] local, byte[] imports)
    {
        byte[] virtualFixup = [.. NativeRow.U32s(0, 0, 0), 0xFF, 0xFF, 0xFF, 0xFF];
        var names = new NativeBuffer(0);
        names.Add(Encoding.ASCII.GetBytes(className + "\0"));
        names.Pad(16, 0xFF);
        int start = 64 + 48 * 3, end = data.Length + local.Length + virtualFixup.Length;
        var header = new NativeBuffer(0);
        header.Add(NativeRow.U32s(0x57E0E057, 0x10C0C010, 0)); header.Add(Layout); header.Add(NativeRow.U32s(3, 2, 0, 0, 0));
        header.Add([.. Encoding.ASCII.GetBytes("hk_2012.2.0-r1\0"), 0xFF]); header.Add(new byte[8]);
        var tables = new (string Name, int Begin, int[] Offsets)[]
        {
            ("__classnames__", start, Enumerable.Repeat(names.Length, 6).ToArray()),
            ("__types__", start + names.Length, new int[6]),
            ("__data__", start + names.Length, [data.Length, data.Length + local.Length, data.Length + local.Length, end, end, end + imports.Length])
        };
        foreach (var (name, begin, offsets) in tables)
        {
            var label = new byte[20];
            Encoding.ASCII.GetBytes(name).CopyTo(label, 0);
            label[19] = 0xFF;
            header.Add(label);
            header.Add(NativeRow.U32s([(uint)begin, .. offsets.Select(o => (uint)o)]));
        }
        return [.. header.ToArray(), .. names.ToArray(), .. data, .. local, .. virtualFixup, .. imports];
    }

    /**
     * A static compound over the shapes: identity composite instances whose i&lt;N&gt; imports bind each child, the
     * retail child filter word 037FFCC7 with mask 0 and the six-byte bounds tree.
     */
    public static byte[] Compound(IReadOnlyList<CookedShape> shapes)
    {
        if (shapes.Count is < 1 or > 4096) throw new InvalidDataException("A compound holds 1 to 4096 shapes.");
        int bits = shapes.Max(shape => shape.KeyBits);
        if (bits is < 1 or > 31 || shapes.Count > (int)(0xFFFFFFFFu >> bits))
            throw new InvalidDataException("The compound exhausts the native shape-key space.");
        var boxes = shapes.Select(shape => shape.Domain).ToArray();
        var bounds = boxes.Skip(1).Aggregate(boxes[0], Union);
        byte[] nodes = TreeBytes(boxes, bounds);
        int count = shapes.Count;
        var data = new NativeBuffer(160 + count * 80);
        data.Put32(0x10, 0x400);
        data.Put32(0x18, uint.MaxValue);
        data.Put8(0x20, 2);
        data.Put8(0x30, (byte)bits);
        data.Put32(0x40, (uint)count); data.Put32(0x44, (uint)count | 0x80000000);
        data.Put32(0x54, 0x80000000);
        data.Put32(0x78, (uint)(nodes.Length / 6)); data.Put32(0x7C, (uint)(nodes.Length / 6) | 0x80000000);
        data.PutF32s(0x80, (float)bounds[0][0], (float)bounds[0][1], (float)bounds[0][2]);
        data.PutF32s(0x90, (float)bounds[1][0], (float)bounds[1][1], (float)bounds[1][2]);
        var imports = new NativeBuffer(0);
        for (int index = 0; index < count; index++)
        {
            int at = 160 + index * 80;
            data.Put32(at + 12, 0x3F000000);
            data.PutF32s(at + 16, 0, 0, 0, 1);
            data.PutF32s(at + 32, 1, 1, 1, 0.5f);
            data.Put32(at + 56, 0x037FFCC7); data.Put32(at + 60, 0);
            data.Put32(at + 64, 0x10000u | (uint)index);
            imports.Add(NativeRow.U32s((uint)(at + 48)));
            imports.Add(Encoding.ASCII.GetBytes($"i{index}\0"));
            imports.Pad(4, 0xFF);
        }
        int nodeAt = data.Length;
        data.Add(nodes);
        data.Pad(16);
        imports.Pad(16, 0xFF);
        return Packfile("hkpStaticCompoundShape", data.ToArray(), NativeRow.U32s(0x38, 160, 0x70, (uint)nodeAt), imports.ToArray());
    }

    /** The six-byte compound tree: the root keeps the whole bounds, children quantize inside their parent. */
    private static byte[] TreeBytes(IReadOnlyList<double[][]> bounds, double[][] rootBounds)
    {
        var nodes = new List<byte[]?>();
        void Emit(int[] ids, double[][] parent, bool root)
        {
            var wanted = ids.Skip(1).Aggregate(bounds[ids[0]], (box, i) => Union(box, bounds[i]));
            int[] code; double[][] decoded;
            if (root) { code = [0, 0, 0]; decoded = rootBounds; }
            else
            {
                code = Quantize(parent, wanted);
                decoded = DecodeBounds(parent, code);
                if (!Contains(decoded, wanted)) throw new InvalidDataException("Quantization lost a compound bound.");
            }
            int index = nodes.Count;
            nodes.Add(null);
            int payload;
            if (ids.Length == 1) payload = ids[0];
            else
            {
                int axis = ArgMax(a => wanted[1][a] - wanted[0][a]);
                var sorted = ids.OrderBy(i => bounds[i][0][axis] + bounds[i][1][axis]).ToArray();
                int half = sorted.Length / 2;
                Emit(sorted[..half], decoded, false);
                int right = nodes.Count;
                if ((right - index) % 2 != 0) throw new InvalidDataException("Invalid full binary compound subtree.");
                Emit(sorted[half..], decoded, false);
                payload = (right - index) / 2 | 0x800000;
            }
            nodes[index] = [(byte)code[0], (byte)code[1], (byte)code[2], (byte)((payload >> 16) & 255), (byte)(payload & 255), (byte)((payload >> 8) & 255)];
        }
        Emit(Enumerable.Range(0, bounds.Count).ToArray(), rootBounds, true);
        return nodes.SelectMany(node => node!).ToArray();
    }

    /**
     * The 80808A15 wrapper: one zero surface slot per surface at +8, signed-short palettes at +0x28 with their
     * starts at +0x38, one zero shape slot per child at +0x48, the collection at +0x58 and the auxiliary word at +0x5C.
     * Returns the bytes and the surface and shape slot offsets.
     */
    public static (byte[] Data, int[] SurfaceSlots, int[] ShapeSlots) Wrapper(int surfaces, IReadOnlyList<int[]> palettes, uint auxiliary)
    {
        var starts = new List<short>(); var materials = new List<short>();
        foreach (var palette in palettes)
        {
            if (palette.Length == 0 || palette.Any(v => v < 0 || v >= surfaces)) throw new InvalidDataException("Invalid collision surface palette.");
            starts.Add((short)materials.Count); materials.AddRange(palette.Select(v => (short)v));
        }
        if (materials.Distinct().Count() != surfaces) throw new InvalidDataException("A collision wrapper holds an unused surface.");
        var raw = new NativeBuffer(96);
        var surfaceSlots = raw.AppendPadded(8, 0x80808BC5, 4, Enumerable.Range(0, surfaces).Select(_ => new byte[4]).ToArray());
        raw.AppendPadded(0x28, 0x80800006, 2, materials.Select(v => BitConverter.GetBytes(v)).ToArray());
        raw.AppendPadded(0x38, 0x80800006, 2, starts.Select(v => BitConverter.GetBytes(v)).ToArray());
        var shapeSlots = raw.AppendPadded(0x48, 0x80800014, 4, palettes.Select(_ => new byte[4]).ToArray());
        raw.Put32(0x5C, auxiliary);
        raw.SetSize();
        return (raw.ToArray(), surfaceSlots, shapeSlots);
    }
}
