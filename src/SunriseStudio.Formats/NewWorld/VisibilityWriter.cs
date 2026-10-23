namespace SunriseStudio.Formats.NewWorld;

/** An inert query volume: its cell records and its version-0x12 point-query partition. */
public sealed record InertQuery(byte[] Cells, byte[] Partition, double[] Bounds, int CellCount);

/**
 * The point-query owner of a bubble with no crossing: a rectilinear grid of inert cells over the query volume,
 * the KD partition that resolves every point to its cell, plain portals on every interior face and the empty
 * 80807320 owner (World Studio generated_query_bounds, generated_crossing.compile_inactive_candidate,
 * generated_tiling, native_partition, generated_visibility).
 */
public static class VisibilityWriter
{
    public const uint OwnerClass = 0x80807320, CellsClass = 0x80809756;

    private static double Number(double value)
    {
        double rounded = (float)value;
        if (!double.IsFinite(rounded)) throw new InvalidDataException("A finite native float is required.");
        return rounded;
    }

    private static double[] Bounds32(double[] bounds)
    {
        var result = bounds.Select(Number).ToArray();
        if (Enumerable.Range(0, 3).Any(i => result[i] >= result[i + 3]))
            throw new InvalidDataException("Query bounds need a positive extent after float rounding.");
        return result;
    }

    /** Coverage of the compiled geometry and arrivals, padded and rounded outward to float32. */
    public static double[] QueryBounds(double[][] geometryBox, IReadOnlyList<double[]> arrivals, double padding = 1.0)
    {
        var geometry = Bounds32([.. geometryBox[0], .. geometryBox[1]]);
        if (arrivals.Count == 0) throw new InvalidDataException("Query coverage requires compiled arrivals.");
        var points = arrivals.Select(p => p.Select(Number).ToArray()).ToArray();
        var low = Enumerable.Range(0, 3).Select(i => Math.Min(geometry[i], points.Min(p => p[i]))).ToArray();
        var high = Enumerable.Range(0, 3).Select(i => Math.Max(geometry[i + 3], points.Max(p => p[i]))).ToArray();
        var bounds = Bounds32([.. low.Select(v => GroundVisualWriter.Outward(v - padding, true)),
            .. high.Select(v => GroundVisualWriter.Outward(v + padding, false))]);
        if (Enumerable.Range(0, 3).Any(i => !(bounds[i] <= geometry[i] && geometry[i] <= geometry[i + 3] && geometry[i + 3] <= bounds[i + 3]))
            || points.Any(p => Enumerable.Range(0, 3).Any(i => !(bounds[i] <= p[i] && p[i] <= bounds[i + 3]))))
            throw new InvalidDataException("Query bounds omit compiled content.");
        return bounds;
    }

    /** Planes every 8 units, widened to keep at most 16 divisions per axis. */
    private static double[] Divisions(double low, double high)
    {
        double step = Math.Max(8.0, (high - low) / 16);
        int count = (int)FloorDivide(high - low, step);
        return Enumerable.Range(1, count).Select(k => low + (high - low) * k / (count + 1)).ToArray();
    }

    /** Python float floor division, which floors the exact quotient rather than the rounded one. */
    private static double FloorDivide(double a, double b)
    {
        double mod = a % b, div = (a - mod) / b;
        if (mod != 0 && (b < 0) != (mod < 0)) div -= 1.0;
        if (div == 0) return 0;
        double floor = Math.Floor(div);
        return div - floor > 0.5 ? floor + 1 : floor;
    }

    private static int Quantize(double[] bounds, int axis, double value, string rounding)
    {
        double scaled = (value - bounds[axis]) / (bounds[axis + 3] - bounds[axis]) * 65535;
        double rounded = rounding switch { "low" => Math.Floor(scaled), "high" => Math.Ceiling(scaled), _ => Math.Round(scaled) };
        return (int)Math.Max(0, Math.Min(65535, rounded));
    }

    private sealed record Grid(double[] Bounds, double[][] Planes)
    {
        public int[] Shape => Planes.Select(p => p.Length + 1).ToArray();
        public double[] Edges(int axis) => [Bounds[axis], .. Planes[axis], Bounds[axis + 3]];
        public int Index(int x, int y, int z) => x + Shape[0] * (y + Shape[1] * z);

        public IEnumerable<(int Index, int[] At, double[] Box)> Cells()
        {
            var shape = Shape;
            var ex = Edges(0); var ey = Edges(1); var ez = Edges(2);
            for (int z = 0; z < shape[2]; z++)
                for (int y = 0; y < shape[1]; y++)
                    for (int x = 0; x < shape[0]; x++)
                        yield return (Index(x, y, z), [x, y, z], [ex[x], ey[y], ez[z], ex[x + 1], ey[y + 1], ez[z + 1]]);
        }
    }

    private static Grid MakeGrid(double[] bounds, double[][] planes)
    {
        bounds = Bounds32(bounds);
        var result = new double[3][];
        for (int axis = 0; axis < 3; axis++)
        {
            var values = planes[axis].Select(Number).Where(v => bounds[axis] < v && v < bounds[axis + 3]).Distinct().Order().ToArray();
            double[] edges = [bounds[axis], .. values, bounds[axis + 3]];
            var quantized = edges.Select(v => Quantize(bounds, axis, v, "plane")).ToArray();
            if (quantized.Zip(quantized.Skip(1)).Any(pair => pair.First >= pair.Second))
                throw new InvalidDataException("Grid planes collapse on the native portal grid.");
            result[axis] = values;
        }
        var grid = new Grid(bounds, result);
        if (grid.Shape.Aggregate(1, (a, b) => a * b) > 16384) throw new InvalidDataException("The query grid exceeds its cell budget.");
        return grid;
    }

    /** A KD node: a split on an axis at a position, or a leaf naming a global cell. */
    private abstract record KdNode;
    private sealed record Split(int Axis, double Position, KdNode Negative, KdNode Positive) : KdNode;
    private sealed record Leaf(int Cell) : KdNode;

    private static KdNode Tree(Grid grid)
    {
        KdNode Build(int[] lo, int[] hi)
        {
            var spans = Enumerable.Range(0, 3).Select(a => hi[a] - lo[a]).ToArray();
            int axis = 0;
            for (int a = 1; a < 3; a++) if (spans[a] > spans[axis]) axis = a;
            if (spans[axis] == 1) return new Leaf(grid.Index(lo[0], lo[1], lo[2]));
            int mid = (lo[axis] + hi[axis]) / 2;
            var upperLow = lo.ToArray(); upperLow[axis] = mid;
            var lowerHigh = hi.ToArray(); lowerHigh[axis] = mid;
            return new Split(axis, grid.Planes[axis][mid - 1], Build(lo, lowerHigh), Build(upperLow, hi));
        }
        return Build([0, 0, 0], grid.Shape);
    }

    /** Two-bit axes followed by the three native rank tables. */
    private static byte[] PackAxes(IReadOnlyList<int> axes)
    {
        int count = axes.Count;
        var bits = new byte[(count + 15) / 16 * 4];
        var prefix = new List<int> { 0 };
        for (int node = 0; node < count; node++)
        {
            bits[node / 4] |= (byte)(axes[node] << ((node & 3) * 2));
            prefix.Add(prefix[^1] + (axes[node] != 3 ? 1 : 0));
        }
        var coarse = new NativeBuffer(0);
        for (int p = 65536; p <= count; p += 65536) coarse.Add(NativeRow.U32s((uint)prefix[p]));
        var middle = new NativeBuffer(0);
        for (int p = 256; p <= count; p += 256) if ((p & 65535) != 0) middle.Add(NativeRow.U16s((ushort)(prefix[p] - prefix[p & ~65535])));
        var fine = new NativeBuffer(0);
        for (int p = 16; p <= count; p += 16) if ((p & 255) != 0) fine.Add([(byte)(prefix[p] - prefix[p & ~255])]);
        middle.Pad(4); fine.Pad(4);
        return [.. bits, .. coarse.ToArray(), .. middle.ToArray(), .. fine.ToArray()];
    }

    private static int Append(NativeBuffer raw, ReadOnlySpan<byte> data, int alignment = 4)
    {
        raw.Pad(alignment);
        return raw.Add(data);
    }

    /** One tile holding the KD partition with global cell indices. */
    private static byte[] Partition(double[] bounds, KdNode tree, int resultBound)
    {
        var axes = new List<int>(); var positions = new List<float>(); var leaves = new List<int>();
        var globalCells = new List<int>(); var indices = new Dictionary<int, int>();
        var pending = new Queue<(KdNode Node, double[] Region, int Depth)>();
        pending.Enqueue((tree, bounds, 0));
        while (pending.TryDequeue(out var item))
        {
            if (item.Depth > 255) throw new InvalidDataException("The KD tree exceeds its depth budget.");
            if (item.Node is Split split)
            {
                double position = Number(split.Position);
                if (!(item.Region[split.Axis] < position && position < item.Region[split.Axis + 3]))
                    throw new InvalidDataException("A KD split must lie inside its bounds after float rounding.");
                axes.Add(split.Axis); positions.Add((float)position);
                var negative = item.Region.ToArray(); var positive = item.Region.ToArray();
                negative[split.Axis + 3] = position; positive[split.Axis] = position;
                pending.Enqueue((split.Negative, negative, item.Depth + 1));
                pending.Enqueue((split.Positive, positive, item.Depth + 1));
            }
            else
            {
                int cell = ((Leaf)item.Node).Cell;
                if (cell < 0 || cell >= resultBound) throw new InvalidDataException("A cell index leaves the paired global cells.");
                if (!indices.TryGetValue(cell, out int local)) { indices[cell] = local = globalCells.Count; globalCells.Add(cell); }
                axes.Add(3); positions.Add(0); leaves.Add(local);
            }
        }
        int width = Math.Max(1, BitLength(Math.Max(0, globalCells.Count - 1)) + 1);
        var packed = new byte[(leaves.Count * width + 31) / 32 * 4 + 4];
        for (int index = 0; index < leaves.Count; index++)
        {
            int bit = index * width, at = bit / 32 * 4;
            ulong word = BitConverter.ToUInt64(packed, at) | (ulong)leaves[index] << (bit & 31);
            BitConverter.GetBytes(word).CopyTo(packed, at);
        }
        var tile = new NativeBuffer(0x50);
        tile.PutF32s(0, bounds.Select(v => (float)v).ToArray());
        tile.Put32(0x18, (uint)(axes.Count * 32 + width));
        tile.Put32(0x1C, (uint)Append(tile, PackAxes(axes)));
        tile.Put32(0x20, (uint)Append(tile, packed));
        int positionsAt = Append(tile, NativeRow.F32s([.. positions]));
        tile.Put32(0x24, (uint)positions.Count); tile.Put32(0x28, (uint)positionsAt);
        int cellsAt = Append(tile, globalCells.SelectMany(c => { var row = new byte[36]; BitConverter.GetBytes(c).CopyTo(row, 16); return row; }).ToArray());
        tile.Put32(0x34, 0x10000u + (uint)globalCells.Count); tile.Put32(0x38, (uint)cellsAt);
        var raw = new NativeBuffer(0xA4);
        raw.Put16(0, 0x12);
        raw.PutF32s(0x14, bounds.Select(v => (float)v).ToArray());
        int rootAxes = Append(raw, PackAxes([3]));
        raw.Put32(0x2C, 32); raw.Put32(0x30, (uint)rootAxes);
        raw.Put32(0x3C, (uint)Append(raw, NativeRow.F32s(0)));
        raw.Put32(0x7C, (uint)resultBound);
        int table = Append(raw, new byte[4]);
        raw.Put32(0xA0, (uint)table);
        raw.Put32(table, (uint)Append(raw, tile.ToArray(), 16));
        return raw.ToArray();
    }

    private static int BitLength(int value) => value == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)value);

    /** A grid over the query volume whose cells hold state 0, zone -1 and ramp -1, so no transition is requested. */
    public static InertQuery Inert(int globalIndex, ulong selfKey, double[] bounds)
    {
        bounds = Bounds32(bounds);
        var grid = MakeGrid(bounds, Enumerable.Range(0, 3).Select(a => Divisions(bounds[a], bounds[a + 3])).ToArray());
        var boxes = grid.Cells().Select(cell => cell.Box).ToArray();
        var cells = new NativeBuffer(140);
        cells.Put32(8, (uint)globalIndex);
        cells.Put64(16, selfKey);
        foreach (int at in new[] { 0x78, 0x7C, 0x80, 0x84 }) cells.Put32(at, uint.MaxValue);
        cells.Add(NativeRow.U32s(NativeBuffer.ArrayMarker, (uint)boxes.Length, 0, 0x80809778, 0));
        cells.PutI64(24, boxes.Length); cells.PutI64(32, 160 - 24 - 24);
        foreach (var box in boxes)
        {
            var row = new byte[20];
            NativeRow.F32s((float)Number((box[0] + box[3]) / 2), (float)Number((box[1] + box[4]) / 2),
                (float)Number((box[2] + box[5]) / 2), -1).CopyTo(row, 0);
            row[17] = 0xFF; row[18] = 0xFF; row[19] = 0xFF;
            cells.Add(row);
        }
        cells.SetSize();
        int count = boxes.Length;
        var partition = new NativeBuffer(Partition(grid.Bounds, Tree(grid), count));
        var records = new NativeBuffer(0);
        var portals = new NativeBuffer(0);
        int portalCount = 0;
        foreach (var (index, at, box) in grid.Cells())
        {
            int start = portalCount;
            for (int axis = 0; axis < 3; axis++)
                foreach (bool upper in new[] { false, true })
                {
                    var step = at.ToArray();
                    step[axis] += upper ? 1 : -1;
                    if (step[axis] < 0 || step[axis] >= grid.Shape[axis]) continue;
                    var face = box.ToArray();
                    face[axis] = face[axis + 3] = upper ? box[axis + 3] : box[axis];
                    int a = (axis + 1) % 3, b = (axis + 2) % 3;
                    uint word = (uint)grid.Index(step[0], step[1], step[2]) | (upper ? 1u << 29 : 0) | (uint)axis << 30;
                    portals.Add(NativeRow.U32s(word));
                    portals.Add(NativeRow.U16s((ushort)Quantize(grid.Bounds, axis, face[axis], "plane"), 0xFFFF,
                        (ushort)Quantize(grid.Bounds, a, face[a + 3], "high"), (ushort)Quantize(grid.Bounds, a, face[a], "low"),
                        (ushort)Quantize(grid.Bounds, b, face[b + 3], "high"), (ushort)Quantize(grid.Bounds, b, face[b], "low")));
                    portalCount++;
                }
            var low = Enumerable.Range(0, 3).Select(i => (ushort)Quantize(grid.Bounds, i, box[i], "low")).ToArray();
            var high = Enumerable.Range(0, 3).Select(i => (ushort)Quantize(grid.Bounds, i, box[i + 3], "high")).ToArray();
            records.Add(NativeRow.U32s((uint)start, (uint)(portalCount - start)));
            // Native swizzle: y-, x-, x+, z-, z+, y+.
            records.Add(NativeRow.U16s(low[1], low[0], high[0], low[2], high[2], high[1]));
        }
        partition.Put32(0x80, (uint)Append(partition, records.ToArray(), 16));
        partition.Put32(0x84, (uint)Append(partition, portals.ToArray(), 16));
        return new(cells.ToArray(), partition.ToArray(), bounds, count);
    }

    /** The empty owner layout: query at +8, cells at +0xA4, root slot at +0x50 and the auxiliary word at +0xC8. */
    public static byte[] Owner(int slot, uint auxiliary)
    {
        var raw = new NativeBuffer(256);
        foreach (int at in new[] { 0x0C, 0x10, 0xA0, 0xA8, 0xC4 }) raw.Put32(at, uint.MaxValue);
        foreach (var (at, value) in new (int, uint)[] { (0x18, 0xDEADBEEF), (0x20, 1), (0x28, 0xB8), (0x50, (uint)slot),
            (0x6C, 0x3F800000), (0x70, 0x7F7FFFFF), (0x74, 0x7F7FFFFF), (0x78, 0x7F7FFFFF), (0x7C, 0x3F800000),
            (0x80, 0xFF7FFFFF), (0x84, 0xFF7FFFFF), (0x88, 0xFF7FFFFF), (0x8C, 0x3F800000), (0xC8, auxiliary),
            (0xDC, NativeBuffer.ArrayMarker), (0xE0, 1), (0xE8, 0x8080733E) })
            raw.Put32(at, value);
        raw.SetSize();
        return raw.ToArray();
    }

    /** Adds the owner, its cells and its raw query blob (file type 24, reference FFFFFFFF) under prefix. */
    public static string Add(GeneratedGraph graph, string prefix, int slot, InertQuery query, uint auxiliary)
    {
        string owner = prefix + "_owner", cells = prefix + "_cells", blob = prefix + "_query";
        graph.Add(cells, query.Cells, CellsClass);
        graph.Add(blob, query.Partition, uint.MaxValue, 24);
        graph.Add(owner, Owner(slot, auxiliary), OwnerClass);
        graph.Reference(owner, 8, blob);
        graph.Reference(owner, 0xA4, cells);
        return owner;
    }
}
