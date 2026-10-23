using System.Buffers.Binary;

namespace SunriseStudio.Authoring;

public static partial class CrossingCompiler
{
    /** A box as six float32 bounds: x-, y-, z-, x+, y+, z+. Arithmetic on them runs in double, as World Studio's does. */
    internal readonly record struct CellBox(float X0, float Y0, float Z0, float X1, float Y1, float Z1)
    {
        public float this[int index] => index switch { 0 => X0, 1 => Y0, 2 => Z0, 3 => X1, 4 => Y1, _ => Z1 };

        public CellBox With(int index, float value) => index switch
        {
            0 => this with { X0 = value },
            1 => this with { Y0 = value },
            2 => this with { Z0 = value },
            3 => this with { X1 = value },
            4 => this with { Y1 = value },
            _ => this with { Z1 = value }
        };

        /** The centre, each lane rounded to float32 once. */
        public float[] Centre() => [(float)(((double)X0 + X1) / 2), (float)(((double)Y0 + Y1) / 2), (float)(((double)Z0 + Z1) / 2)];

        public bool Contains(float[] point)
        {
            for (int a = 0; a < 3; a++) if (!(this[a] <= point[a] && point[a] <= this[a + 3])) return false;
            return true;
        }
    }

    /** Cells tile the volume on axis planes; 16 divisions plus six box planes per axis stay under this. */
    internal const int MaximumGridCells = 16384;

    /** The grid formed by interior planes per axis; cell index = x + nx * (y + ny * z). */
    internal sealed class CellGrid
    {
        public CellBox Bounds { get; }
        public float[][] Planes { get; }
        public int[] Shape { get; }

        /** Planes outside or on the bounds drop; duplicates after float32 rounding collapse; a collapsed u16 plane is refused. */
        public CellGrid(CellBox bounds, IReadOnlyList<double>[] planes)
        {
            Bounds = bounds;
            Planes = new float[3][];
            for (int axis = 0; axis < 3; axis++)
            {
                var values = planes[axis].Select(Number).Where(v => bounds[axis] < v && v < bounds[axis + 3]).Distinct().Order().ToArray();
                float[] edges = [bounds[axis], .. values, bounds[axis + 3]];
                for (int i = 1; i < edges.Length; i++)
                    if (Quantize(bounds, axis, edges[i - 1], Rounding.Plane) >= Quantize(bounds, axis, edges[i], Rounding.Plane))
                        throw new InvalidDataException("Crossing grid planes collapse on the native u16 portal grid.");
                Planes[axis] = values;
            }
            Shape = [Planes[0].Length + 1, Planes[1].Length + 1, Planes[2].Length + 1];
            if ((long)Shape[0] * Shape[1] * Shape[2] > MaximumGridCells)
                throw new InvalidDataException("The crossing grid exceeds 16,384 cells.");
        }

        public int Count => Shape[0] * Shape[1] * Shape[2];
        public int Index(int x, int y, int z) => x + Shape[0] * (y + Shape[1] * z);
        public float Edge(int axis, int i) => i == 0 ? Bounds[axis] : i == Shape[axis] ? Bounds[axis + 3] : Planes[axis][i - 1];

        /** Cells in index order with their grid coordinates. */
        public IEnumerable<(int Index, int[] At, CellBox Box)> Cells()
        {
            for (int z = 0; z < Shape[2]; z++)
                for (int y = 0; y < Shape[1]; y++)
                    for (int x = 0; x < Shape[0]; x++)
                        yield return (Index(x, y, z), [x, y, z], new(Edge(0, x), Edge(1, y), Edge(2, z), Edge(0, x + 1), Edge(1, y + 1), Edge(2, z + 1)));
        }

        /** Each interior face of a cell: the neighbour across it, its axis, whether it is the upper face, and the face box. */
        public IEnumerable<(int Neighbour, int Axis, bool Upper, CellBox Face)> Faces(int[] at, CellBox box)
        {
            for (int axis = 0; axis < 3; axis++)
                foreach (bool upper in new[] { false, true })
                {
                    int[] step = [at[0], at[1], at[2]];
                    step[axis] += upper ? 1 : -1;
                    if (step[axis] < 0 || step[axis] >= Shape[axis]) continue;
                    float plane = upper ? box[axis + 3] : box[axis];
                    yield return (Index(step[0], step[1], step[2]), axis, upper, box.With(axis, plane).With(axis + 3, plane));
                }
        }
    }

    internal enum Rounding { Low, High, Plane }

    /** A world coordinate on the payload's u16 grid; Plane rounds half to even. */
    internal static int Quantize(CellBox bounds, int axis, double value, Rounding rounding)
    {
        double scaled = (value - bounds[axis]) / ((double)bounds[axis + 3] - bounds[axis]) * 65535;
        double rounded = rounding switch { Rounding.Low => Math.Floor(scaled), Rounding.High => Math.Ceiling(scaled), _ => Math.Round(scaled) };
        return (int)Math.Max(0, Math.Min(65535, rounded));
    }

    /** A finite float32; anything else is refused. */
    internal static float Number(double value)
    {
        float result = (float)value;
        if (!float.IsFinite(result)) throw new InvalidDataException("A crossing value is not a finite float.");
        return result;
    }

    private abstract record KdNode;
    private sealed record KdSplit(int Axis, float Position, KdNode Negative, KdNode Positive) : KdNode;
    private sealed record KdLeaf(int Cell) : KdNode;

    /** Balanced KD tree over the grid; split equality takes the positive branch, so a plane belongs to the upper cell. */
    private static KdNode GridTree(CellGrid grid)
    {
        KdNode Build(int[] low, int[] high)
        {
            int axis = 0;
            for (int a = 1; a < 3; a++) if (high[a] - low[a] > high[axis] - low[axis]) axis = a;
            if (high[axis] - low[axis] == 1) return new KdLeaf(grid.Index(low[0], low[1], low[2]));
            int middle = (low[axis] + high[axis]) / 2;
            int[] upperLow = [.. low], lowerHigh = [.. high];
            upperLow[axis] = middle; lowerHigh[axis] = middle;
            return new KdSplit(axis, grid.Planes[axis][middle - 1], Build(low, lowerHigh), Build(upperLow, high));
        }
        return Build([0, 0, 0], grid.Shape);
    }

    /** Two-bit axes, then the 65,536-, 256- and 16-node rank tables the client's 1A7C530 reads; each table is u32 aligned. */
    private static byte[] PackAxes(IReadOnlyList<int> axes)
    {
        int count = axes.Count;
        var bits = new byte[(count + 15) / 16 * 4];
        var prefix = new int[count + 1];
        for (int node = 0; node < count; node++)
        {
            bits[node / 4] |= (byte)(axes[node] << (node & 3) * 2);
            prefix[node + 1] = prefix[node] + (axes[node] != 3 ? 1 : 0);
        }
        var output = new List<byte>(bits);
        for (int p = 65536; p <= count; p += 65536) output.AddRange(BitConverter.GetBytes((uint)prefix[p]));
        var middle = new List<byte>();
        for (int p = 256; p <= count; p += 256) if ((p & 65535) != 0) middle.AddRange(BitConverter.GetBytes((ushort)(prefix[p] - prefix[p & ~65535])));
        while (middle.Count % 4 != 0) middle.Add(0);
        var fine = new List<byte>();
        for (int p = 16; p <= count; p += 16) if ((p & 255) != 0) fine.Add((byte)(prefix[p] - prefix[p & ~255]));
        while (fine.Count % 4 != 0) fine.Add(0);
        output.AddRange(middle); output.AddRange(fine);
        return [.. output];
    }

    private static int Append(List<byte> raw, ReadOnlySpan<byte> data, int alignment = 4)
    {
        while (raw.Count % alignment != 0) raw.Add(0);
        int at = raw.Count;
        raw.AddRange(data.ToArray());
        return at;
    }

    private static void Put32(List<byte> raw, int at, uint value)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(word, value);
        for (int i = 0; i < 4; i++) raw[at + i] = word[i];
    }

    private static void PutFloat(List<byte> raw, int at, float value) => Put32(raw, at, BitConverter.SingleToUInt32Bits(value));

    /**
     * One tile holding a KD partition over global cells 0..resultBound-1, in the version-0x12 layout the
     * client's 1A6FC50 query reads. Nodes are serialized breadth first; the tile's local cells follow leaf order.
     */
    private static List<byte> CompilePartition(CellBox bounds, KdNode tree, int resultBound)
    {
        var axes = new List<int>(); var positions = new List<float>(); var leaves = new List<int>();
        var global = new List<int>(); var local = new Dictionary<int, int>();
        var pending = new Queue<(KdNode Node, CellBox Region)>();
        pending.Enqueue((tree, bounds));
        while (pending.TryDequeue(out var item))
        {
            if (axes.Count >= 1_000_000) throw new InvalidDataException("The crossing partition exceeds the node budget.");
            if (item.Node is KdSplit split)
            {
                if (!(item.Region[split.Axis] < split.Position && split.Position < item.Region[split.Axis + 3]))
                    throw new InvalidDataException("A crossing split leaves its region.");
                axes.Add(split.Axis); positions.Add(split.Position);
                pending.Enqueue((split.Negative, item.Region.With(split.Axis + 3, split.Position)));
                pending.Enqueue((split.Positive, item.Region.With(split.Axis, split.Position)));
                continue;
            }
            int cell = ((KdLeaf)item.Node).Cell;
            if (cell < 0 || cell >= resultBound) throw new InvalidDataException("A crossing leaf names a cell outside the payload.");
            if (!local.TryGetValue(cell, out int index))
            {
                if (global.Count == 65535) throw new InvalidDataException("A crossing tile holds more than 65,535 cells.");
                local[cell] = index = global.Count; global.Add(cell);
            }
            axes.Add(3); positions.Add(0); leaves.Add(index);
        }
        int width = Math.Max(1, BitLength(Math.Max(0, global.Count - 1)) + 1);
        var packed = new byte[(leaves.Count * width + 31) / 32 * 4 + 4];
        for (int i = 0; i < leaves.Count; i++)
        {
            long bit = (long)i * width;
            int at = (int)(bit / 32 * 4);
            ulong word = BinaryPrimitives.ReadUInt64LittleEndian(packed.AsSpan(at)) | (ulong)leaves[i] << (int)(bit & 31);
            BinaryPrimitives.WriteUInt64LittleEndian(packed.AsSpan(at), word);
        }
        var tile = new List<byte>(new byte[0x50]);
        for (int i = 0; i < 6; i++) PutFloat(tile, i * 4, bounds[i]);
        Put32(tile, 0x18, (uint)(axes.Count * 32 + width));
        Put32(tile, 0x1C, (uint)Append(tile, PackAxes(axes)));
        Put32(tile, 0x20, (uint)Append(tile, packed));
        var splits = new byte[positions.Count * 4];
        for (int i = 0; i < positions.Count; i++) BinaryPrimitives.WriteSingleLittleEndian(splits.AsSpan(i * 4), positions[i]);
        int splitAt = Append(tile, splits);
        Put32(tile, 0x24, (uint)positions.Count); Put32(tile, 0x28, (uint)splitAt);
        var cells = new byte[global.Count * 36];
        for (int i = 0; i < global.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(cells.AsSpan(i * 36 + 16), (uint)global[i]);
        int cellsAt = Append(tile, cells);
        Put32(tile, 0x34, (uint)(0x10000 + global.Count)); Put32(tile, 0x38, (uint)cellsAt);
        var raw = new List<byte>(new byte[0xA4]);
        raw[0] = 0x12;
        for (int i = 0; i < 6; i++) PutFloat(raw, 0x14 + i * 4, bounds[i]);
        int outer = Append(raw, PackAxes([3]));
        Put32(raw, 0x2C, 32); Put32(raw, 0x30, (uint)outer);
        Put32(raw, 0x3C, (uint)Append(raw, new byte[4]));
        Put32(raw, 0x7C, (uint)resultBound);
        int table = Append(raw, new byte[4]);
        Put32(raw, 0xA0, (uint)table);
        Put32(raw, table, (uint)Append(raw, tile.ToArray(), 16));
        return raw;
    }

    private static int BitLength(int value) => value == 0 ? 0 : 32 - int.LeadingZeroCount(value);

    /** The grid's partition plus a record per cell (+0x80) and one plain portal per interior face each way (+0x84). */
    internal static byte[] CompileGrid(CellGrid grid)
    {
        var partition = CompilePartition(grid.Bounds, GridTree(grid), grid.Count);
        var records = new List<byte>(); var portals = new List<byte>();
        int portalCount = 0;
        foreach (var (_, at, box) in grid.Cells())
        {
            int start = portalCount;
            foreach (var (neighbour, axis, upper, face) in grid.Faces(at, box))
            {
                int a = (axis + 1) % 3, b = (axis + 2) % 3;
                // Bits 30-31 hold the axis, bit 29 a neighbour on the + side; bit 27 (polygon portal) is never set.
                uint word = (uint)neighbour | (upper ? 1u << 29 : 0) | (uint)axis << 30;
                portals.AddRange(BitConverter.GetBytes(word));
                foreach (int value in new[] { Quantize(grid.Bounds, axis, face[axis], Rounding.Plane), 0xFFFF,
                    Quantize(grid.Bounds, a, face[a + 3], Rounding.High), Quantize(grid.Bounds, a, face[a], Rounding.Low),
                    Quantize(grid.Bounds, b, face[b + 3], Rounding.High), Quantize(grid.Bounds, b, face[b], Rounding.Low) })
                    portals.AddRange(BitConverter.GetBytes((ushort)value));
                portalCount++;
            }
            records.AddRange(BitConverter.GetBytes((uint)start));
            records.AddRange(BitConverter.GetBytes((uint)(portalCount - start)));
            int[] low = [.. Enumerable.Range(0, 3).Select(i => Quantize(grid.Bounds, i, box[i], Rounding.Low))];
            int[] high = [.. Enumerable.Range(0, 3).Select(i => Quantize(grid.Bounds, i, box[i + 3], Rounding.High))];
            // The native record swizzles the six bounds: y-, x-, x+, z-, z+, y+.
            foreach (int value in new[] { low[1], low[0], high[0], low[2], high[2], high[1] })
                records.AddRange(BitConverter.GetBytes((ushort)value));
        }
        Put32(partition, 0x80, (uint)Append(partition, records.ToArray(), 16));
        Put32(partition, 0x84, (uint)Append(partition, portals.ToArray(), 16));
        if (partition.Count > 64 * 1024 * 1024) throw new InvalidDataException("The crossing partition exceeds 64 MiB.");
        return [.. partition];
    }
}
