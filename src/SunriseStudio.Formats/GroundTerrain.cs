using System.Buffers.Binary;

namespace SunriseStudio.Formats;

/** A retail terrain material: the part technique (class 0x808071E8) and the group blend map at pixel t14. */
public sealed record GroundMaterial(uint Technique, uint Dyemap)
{
    /** A group dyemap of 0xFFFFFFFF binds nothing at t14, as retail groups without one do. */
    public const uint NoDyemap = uint.MaxValue;
    public string Id => $"retail-{Technique:X8}";
}

/** One 128-unit patch of a ground: its cell and the quads of each occupied 32-unit tile. */
public sealed record GroundPatch(int CellX, int CellY, IReadOnlyDictionary<(int X, int Y), List<(int X, int Y)>> Tiles);

/**
 * One STerrain (class 0x8080714F) with its bounds (class 0x80809671) and buffer payloads. Reference
 * slots hold zero; the offsets name what the compiler writes there.
 */
public sealed record TerrainPatchData(byte[] Terrain, byte[] Bounds, byte[] Positions, byte[] Attributes,
    byte[] Indices, byte[] AlternateIndices, byte[] Occlusion, int[] TechniqueOffsets, int[] DyemapOffsets,
    int VertexCount, int IndexCount, int AlternateIndexCount, (double[] Min, double[] Max) Box, ushort Index);

/**
 * Generated grounds as retail terrain, as generated_native_terrain.py compiles them (in game on
 * 2026-09-26, studio_a7d0f7c5ccc6). One STerrain per 128-unit patch, one group per occupied 32-unit
 * tile, levels 0..3 with the same topology. Layout rules were measured on all 1,534 installed STerrain.
 */
public static class GroundTerrain
{
    public const uint TerrainClass = 0x8080714F, BoundsClass = 0x80809671, BoundsRowClass = 0x80809673;
    public const uint GroupClass = 0x80807154, PartClass = 0x80807152, TableClass = 0x808099D6, RowClass = 0x808099D8;
    public const uint ResourceClass = 0x8080714B, RegistryRowClass = 0x8080726A;
    // 'kbrt': high word of a terrain's render-registry key; 'aprt': high word of a group bounds key.
    public const uint KeyTag = 0x7472626B, BoundsTag = 0x74727061;
    // Installed tags referenced in place: terrain archetype (1,636 of 1,636 rows), depth prepass and shadow techniques (1,534 of 1,534).
    public const uint Archetype = 0x80C705F2, DepthTechnique = 0x815B67A5, ShadowTechnique = 0x815B67A8;
    /** Slot of the terrain table in a 23-slot geometry container (1,636 of 1,636). */
    public const int ContainerSlot = 7;
    private const double Patch = 128, Tile = 32, TexcoordScale = 63.0 / 16;
    private const int MaxIndex = 0xFFFF;
    private static readonly (int, int)[] UOrder = [(0, 0), (1, 0), (1, 1), (0, 1)];

    /** The registry key of a terrain index: 'kbrt' high word, index low word. */
    public static ulong RegistryKey(int index) => (ulong)KeyTag << 32 | (uint)index;

    /** Quad (x, y) as two upward triangles with generated_terrain.mesh's diagonal. */
    public static ((int, int, int), (int, int, int)) QuadTriangles(int columns, int x, int y)
    {
        int a = y * columns + x;
        return ((a, a + 1, a + columns), (a + 1, a + columns + 1, a + columns));
    }

    /** Patches in retail order: row-major over cells; each tile's quads in grid order. */
    public static IReadOnlyList<GroundPatch> Patches(GroundGrid grid, (double X, double Y, double Z)[] points)
    {
        var patches = new Dictionary<(int, int), Dictionary<(int, int), List<(int, int)>>>();
        int columns = grid.Columns;
        for (int y = 0; y < grid.Rows - 1; y++)
            for (int x = 0; x < columns - 1; x++)
            {
                var a = points[y * columns + x];
                var b = points[(y + 1) * columns + x + 1];
                double cx = (a.X + b.X) / 2, cy = (a.Y + b.Y) / 2;
                int px = (int)Math.Floor(cx / Patch), py = (int)Math.Floor(cy / Patch);
                int tx = Math.Min(3, Math.Max(0, (int)Math.Floor((cx - px * Patch) / Tile)));
                int ty = Math.Min(3, Math.Max(0, (int)Math.Floor((cy - py * Patch) / Tile)));
                if (!patches.TryGetValue((px, py), out var tiles)) patches[(px, py)] = tiles = [];
                if (!tiles.TryGetValue((tx, ty), out var quads)) tiles[(tx, ty)] = quads = [];
                quads.Add((x, y));
            }
        return patches.OrderBy(p => p.Key.Item2).ThenBy(p => p.Key.Item1)
            .Select(p => new GroundPatch(p.Key.Item1, p.Key.Item2, p.Value.ToDictionary(t => t.Key, t => t.Value))).ToArray();
    }

    /** Tiles of a patch in retail group order: quadrants, then tiles, each (0,0), (1,0), (1,1), (0,1). */
    public static (int X, int Y)[] TileOrder(GroundPatch patch) =>
        patch.Tiles.Keys.OrderBy(Rank).ToArray();

    private static int Rank((int X, int Y) tile) =>
        Array.IndexOf(UOrder, (tile.X / 2, tile.Y / 2)) * 4 + Array.IndexOf(UOrder, (tile.X % 2, tile.Y % 2));

    private static (double, double, double) Face((double X, double Y, double Z)[] points, (int A, int B, int C) tri)
    {
        var (a, b, c) = (points[tri.A], points[tri.B], points[tri.C]);
        (double X, double Y, double Z) u = (b.X - a.X, b.Y - a.Y, b.Z - a.Z), v = (c.X - a.X, c.Y - a.Y, c.Z - a.Z);
        return (u.Y * v.Z - u.Z * v.Y, u.Z * v.X - u.X * v.Z, u.X * v.Y - u.Y * v.X);
    }

    /** Area-weighted vertex normals over the whole ground as SNORM16, so patch seams agree. */
    public static short[] Normals(GroundGrid grid, (double X, double Y, double Z)[] points)
    {
        var sums = new double[points.Length * 3];
        for (int y = 0; y < grid.Rows - 1; y++)
            for (int x = 0; x < grid.Columns - 1; x++)
            {
                var (first, second) = QuadTriangles(grid.Columns, x, y);
                foreach (var tri in new[] { first, second })
                {
                    var n = Face(points, tri);
                    if (!(n.Item3 > 0)) throw new InvalidDataException("A ground triangle does not face up.");
                    foreach (int i in new[] { tri.Item1, tri.Item2, tri.Item3 })
                    {
                        sums[i * 3] += n.Item1; sums[i * 3 + 1] += n.Item2; sums[i * 3 + 2] += n.Item3;
                    }
                }
            }
        var result = new short[points.Length * 3];
        for (int i = 0; i < points.Length; i++)
        {
            double length = Math.Sqrt(sums[i * 3] * sums[i * 3] + sums[i * 3 + 1] * sums[i * 3 + 1] + sums[i * 3 + 2] * sums[i * 3 + 2]);
            for (int axis = 0; axis < 3; axis++)
                result[i * 3 + axis] = (short)Math.Max(-32767, Math.Min(32767, Math.Round(sums[i * 3 + axis] / length * 32767)));
        }
        return result;
    }

    /**
     * Our per-vertex occlusion bake (255 = open sky): the mean over eight azimuths of cos^2 of the
     * horizon elevation within 16 units, over this ground's own heights. Retail bakes the whole map.
     */
    public static byte[] Occlusion(GroundGrid grid, (double X, double Y, double Z)[] points, double reach = 16, int directions = 8)
    {
        int columns = grid.Columns, rows = grid.Rows;
        double x0 = points[0].X, y0 = points[0].Y;
        double dx = points[1].X - x0, dy = points[columns].Y - y0, step = Math.Min(dx, dy);
        double? HeightAt(double x, double y)
        {
            double fx = (x - x0) / dx, fy = (y - y0) / dy;
            if (fx < 0 || fy < 0 || fx > columns - 1 || fy > rows - 1) return null;
            int ix = Math.Min((int)fx, columns - 2), iy = Math.Min((int)fy, rows - 2);
            double tx = fx - ix, ty = fy - iy;
            double H(int i, int j) => points[j * columns + i].Z;
            return (H(ix, iy) * (1 - tx) + H(ix + 1, iy) * tx) * (1 - ty) + (H(ix, iy + 1) * (1 - tx) + H(ix + 1, iy + 1) * tx) * ty;
        }
        var result = new byte[points.Length];
        for (int p = 0; p < points.Length; p++)
        {
            var point = points[p];
            double total = 0;
            for (int k = 0; k < directions; k++)
            {
                double angle = 2 * Math.PI * k / directions, ux = Math.Cos(angle), uy = Math.Sin(angle);
                double slope = 0, distance = step;
                while (distance <= reach)
                {
                    if (HeightAt(point.X + ux * distance, point.Y + uy * distance) is not { } h) break;
                    slope = Math.Max(slope, (h - point.Z) / distance);
                    distance += step;
                }
                total += 1 / (1 + slope * slope);
            }
            result[p] = (byte)Math.Max(0, Math.Min(255, Math.Round(255 * total / directions)));
        }
        return result;
    }

    /** Signed 16-bit strips, one per run of quads in a grid column, joined by restarts. */
    private static List<int> Strip(int columns, List<(int X, int Y)> quads, Dictionary<int, int> local)
    {
        var output = new List<int>();
        var byColumn = new Dictionary<int, List<int>>();
        foreach (var (x, y) in quads)
        {
            if (!byColumn.TryGetValue(x, out var ys)) byColumn[x] = ys = [];
            ys.Add(y);
        }
        foreach (int x in byColumn.Keys.Order())
        {
            var ys = byColumn[x].Order().ToList();
            var runs = new List<(int, int)>();
            int start = ys[0];
            for (int i = 0; i < ys.Count; i++)
                if (i + 1 == ys.Count || ys[i + 1] != ys[i] + 1) { runs.Add((start, ys[i])); if (i + 1 < ys.Count) start = ys[i + 1]; }
            foreach (var (first, last) in runs)
            {
                if (output.Count != 0) output.Add(MaxIndex);
                for (int y = first; y < last + 2; y++)
                {
                    output.Add(local[y * columns + x]);
                    output.Add(local[y * columns + x + 1]);
                }
            }
        }
        return output;
    }

    private static int[] Corners(int columns, (int X, int Y) quad) =>
        [quad.Y * columns + quad.X, quad.Y * columns + quad.X + 1, (quad.Y + 1) * columns + quad.X, (quad.Y + 1) * columns + quad.X + 1];

    /** One patch as encode_patch writes it; index is the patch's terrain index, unique in its bubbles' registries. */
    public static TerrainPatchData EncodePatch(GroundGrid grid, (double X, double Y, double Z)[] points, short[] normals,
        byte[] occlusion, GroundPatch patch, GroundMaterial material, int index)
    {
        if (index is < 0 or > 0xFFFF) throw new InvalidDataException("A terrain index is 16 bits.");
        int columns = grid.Columns;
        var order = TileOrder(patch);
        if (order.Length is < 1 or > 16) throw new InvalidDataException("A terrain patch holds 1 to 16 tile groups.");
        var local = new Dictionary<int, int>();
        var vertices = new List<int>();
        foreach (var tile in order)
            foreach (var quad in patch.Tiles[tile].OrderBy(q => q.Y).ThenBy(q => q.X))
                foreach (int v in Corners(columns, quad))
                    if (local.TryAdd(v, vertices.Count)) vertices.Add(v);
        if (vertices.Count > MaxIndex) throw new InvalidDataException("A terrain patch exceeds 16-bit vertices.");
        double[] low = [double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity];
        double[] high = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
        foreach (int v in vertices) Extend(low, high, points[v]);
        long totalZ = (long)Math.Round(low[2] * 8192) + 32768;
        long zOffset = Mod(totalZ - 32768, 65536) + 32768;
        double[] offset = [low[0] * 64 + 32768, low[1] * 64 + 32768, zOffset, Math.Floor((totalZ - zOffset) / 65536.0)];
        var positions = EncodePositions(vertices.Select(v => points[v]), offset);
        double originX = patch.CellX * Patch, originY = patch.CellY * Patch;
        var attributes = new byte[vertices.Count * 12];
        for (int i = 0; i < vertices.Count; i++)
        {
            int v = vertices[i], at = i * 12;
            for (int axis = 0; axis < 3; axis++) BinaryPrimitives.WriteInt16LittleEndian(attributes.AsSpan(at + 2 * axis), normals[v * 3 + axis]);
            BinaryPrimitives.WriteInt16LittleEndian(attributes.AsSpan(at + 6), 32767);
            BinaryPrimitives.WriteHalfLittleEndian(attributes.AsSpan(at + 8), (Half)((points[v].X - originX) / Patch));
            BinaryPrimitives.WriteHalfLittleEndian(attributes.AsSpan(at + 10), (Half)((points[v].Y - originY) / Patch));
        }
        var primary = new List<int>();
        var alternate = new List<int>();
        var groups = new List<byte[]>();
        var parts = new List<byte[]>();
        var boundRows = new List<byte[]>();
        for (int g = 0; g < order.Length; g++)
        {
            var tile = order[g];
            var strip = Strip(columns, patch.Tiles[tile], local);
            if (strip.Count > MaxIndex) throw new InvalidDataException("A terrain group exceeds the native part index count.");
            var levels = new List<(int Start, int Count)>();
            for (int level = 0; level < 3; level++) { levels.Add((primary.Count, strip.Count)); primary.AddRange(strip); }
            levels.Add((alternate.Count, strip.Count));
            alternate.AddRange(strip);
            double[] glow = [double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity];
            double[] ghigh = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
            foreach (var quad in patch.Tiles[tile]) foreach (int v in Corners(columns, quad)) Extend(glow, ghigh, points[v]);
            double[] center = [(glow[0] + ghigh[0]) / 2, (glow[1] + ghigh[1]) / 2, (glow[2] + ghigh[2]) / 2];
            double[] extent = [(ghigh[0] - glow[0]) / 2, (ghigh[1] - glow[1]) / 2, (ghigh[2] - glow[2]) / 2];
            float radius = SingleUp(Math.Sqrt(extent[0] * extent[0] + extent[1] * extent[1] + extent[2] * extent[2]));
            var group = new byte[0x60];
            Floats(group, 0, center[0], center[1], center[2]);
            BinaryPrimitives.WriteSingleLittleEndian(group.AsSpan(12), radius);
            Floats(group, 0x10, extent[0], extent[1], extent[2]);
            Floats(group, 0x20, TexcoordScale, TexcoordScale, 1.0 / 128 - tile.X * 63.0 / 64, 1.0 / 128 - tile.Y * 63.0 / 64);
            for (int level = 0; level < 4; level++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(group.AsSpan(0x30 + 8 * level), (uint)levels[level].Start);
                BinaryPrimitives.WriteUInt32LittleEndian(group.AsSpan(0x34 + 8 * level), (uint)levels[level].Count);
            }
            groups.Add(group);
            for (int level = 0; level < 4; level++)
            {
                var part = new byte[12];
                BinaryPrimitives.WriteUInt32LittleEndian(part.AsSpan(4), (uint)levels[level].Start);
                BinaryPrimitives.WriteUInt16LittleEndian(part.AsSpan(8), (ushort)levels[level].Count);
                part[10] = (byte)g; part[11] = (byte)level;
                parts.Add(part);
            }
            var bound = new byte[48];
            Floats(bound, 0, glow[0], glow[1], glow[2], 1);
            Floats(bound, 16, ghigh[0], ghigh[1], ghigh[2], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(bound.AsSpan(32), (uint)(index * 16 + g));
            BinaryPrimitives.WriteUInt32LittleEndian(bound.AsSpan(36), BoundsTag);
            boundRows.Add(bound);
        }
        uint quadrants = 0;
        foreach (var (tx, ty) in order) quadrants |= 1u << (tx / 2 + 2 * (ty / 2));
        var raw = new byte[0xB0];
        Floats(raw, 0x10, low[0], low[1], low[2], 1);
        Floats(raw, 0x20, high[0], high[1], high[2], 1);
        Floats(raw, 0x30, offset);
        BinaryPrimitives.WriteInt16LittleEndian(raw.AsSpan(0x40), checked((short)(2 * patch.CellX)));
        BinaryPrimitives.WriteInt16LittleEndian(raw.AsSpan(0x42), checked((short)(2 * patch.CellY)));
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x44), quadrants);
        for (int i = 0; i < 3; i++) BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x48 + 4 * i), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0xA0), (uint)alternate.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0xA4), (uint)((vertices.Count + 3) & ~3));
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0xA8), uint.MaxValue);
        raw = NativeArray.Append(raw, 0x58, GroupClass, groups, out int groupStart);
        raw = NativeArray.Append(raw, 0x80, PartClass, parts, out int partStart);
        if (groupStart != 0xD0) throw new InvalidDataException("Terrain groups must start at 0xD0.");
        int[] dyemaps = Enumerable.Range(0, groups.Count).Select(i => groupStart + 0x60 * i + 0x50).ToArray();
        if (material.Dyemap == GroundMaterial.NoDyemap)
            foreach (int at in dyemaps) BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(at), uint.MaxValue);
        var bounds = NativeArray.Append(new byte[0x18], 8, BoundsRowClass, boundRows, out _);
        var block = vertices.Select(v => occlusion[v]).ToArray();
        var padded = new byte[(block.Length + 3) & ~3];
        block.CopyTo(padded, 0);
        return new(raw, bounds, positions, attributes, U16(primary), U16(alternate), [.. padded, .. padded],
            Enumerable.Range(0, parts.Count).Select(i => partStart + 12 * i).ToArray(),
            material.Dyemap == GroundMaterial.NoDyemap ? [] : dyemaps,
            vertices.Count, primary.Count, alternate.Count, (low, high), (ushort)index);
    }

    /**
     * A terrain map table of count rows in the installed layout (371 of 371): 48-byte head, 144-byte
     * rows, then a 40-byte slot per row holding {4 zero bytes, class 0x8080714B, 32-byte resource}.
     * @return The table and, per row, the row offset and its resource offset.
     */
    public static (byte[] Bytes, (int Row, int Resource)[] Rows) Table(int count)
    {
        if (count is < 1 or > 4096) throw new InvalidDataException("A terrain table holds 1 to 4096 rows.");
        var raw = new byte[48 + 144 * count + 40 * count];
        BinaryPrimitives.WriteUInt64LittleEndian(raw, (ulong)raw.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(8), (ulong)count);
        BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(28), NativeArray.Marker);
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(32), (ulong)count);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(40), RowClass);
        var rows = new (int, int)[count];
        for (int i = 0; i < count; i++)
        {
            int row = 48 + 144 * i, at = 48 + 144 * count + 40 * i + 8;
            Floats(raw, row + 0x10, 0, 0, 0, 1, 0, 0, 0, 1);
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(row + 0x30), ulong.MaxValue);
            for (int k = 0; k < 4; k++) BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(row + 0x40 + 4 * k), uint.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(row + 0x54), uint.MaxValue);
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(row + 0x5C), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(row + 0x60), uint.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(row + 0x64), 0x811C9DC5);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(row + 0x68), 0x304);
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(row + 0x70), ulong.MaxValue);
            BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(row + 0x78), at - (row + 0x78));
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(at - 4), ResourceClass);
            rows[i] = (row, at);
        }
        return (raw, rows);
    }

    /** Resource +0x10: u16 terrain index, u16 0, 'kbrt'; +0x18 STerrain tag, +0x1C bounds tag. */
    public static void WriteResource(byte[] table, int resource, int index, uint terrain, uint bounds)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(resource + 0x10), checked((ushort)index));
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(resource + 0x12), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(resource + 0x14), KeyTag);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(resource + 0x18), terrain);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(resource + 0x1C), bounds);
    }

    /** A bank-0 render-registry row (class 0x8080726A): key, first element of its occlusion block, 0. */
    public static byte[] RegistryRow(ulong key, uint first)
    {
        var row = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(row, key);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(8), first);
        return row;
    }

    /** Positions as landscape/codec.encode_positions: (x, y) * 64 and z * 8192 less the offset, z split over two words. */
    public static byte[] EncodePositions(IEnumerable<(double X, double Y, double Z)> points, double[] offset)
    {
        var output = new List<byte>();
        Span<byte> row = stackalloc byte[8];
        foreach (var (x, y, z) in points)
        {
            double[] native = [x * 64 - offset[0], y * 64 - offset[1], z * 8192 - offset[2] - offset[3] * 65536];
            if (native.Any(v => !double.IsFinite(v) || Math.Abs(v - Math.Round(v)) >= 1e-7))
                throw new InvalidDataException("A terrain position cannot be represented exactly.");
            long px = (long)Math.Round(native[0]), py = (long)Math.Round(native[1]), combined = (long)Math.Round(native[2]);
            long pw = FloorDiv(combined + 32768, 65536), pz = combined - pw * 65536;
            if (new[] { px, py, pz, pw }.Any(v => v is < short.MinValue or > short.MaxValue))
                throw new InvalidDataException("A terrain position exceeds its packed signed lanes.");
            BinaryPrimitives.WriteInt16LittleEndian(row, (short)px);
            BinaryPrimitives.WriteInt16LittleEndian(row[2..], (short)py);
            BinaryPrimitives.WriteInt16LittleEndian(row[4..], (short)pz);
            BinaryPrimitives.WriteInt16LittleEndian(row[6..], (short)pw);
            output.AddRange(row.ToArray());
        }
        return output.ToArray();
    }

    /** Index buffer header bytes 0 = 1 and 1 = 0 (16-bit); the size sits at +8. */
    public static byte[] IndexHeader(int size)
    {
        var header = new byte[24];
        header[0] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), (ulong)size);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 0xDEADBEEF);
        return header;
    }

    /** Vertex buffer header: size, stride, 0, 0xDEADBEEF. */
    public static byte[] VertexHeader(int size, int stride)
    {
        var header = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)size);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)stride);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 0xDEADBEEF);
        return header;
    }

    /** float32 rounded up, so a stored radius never shrinks below its double. */
    internal static float SingleUp(double value)
    {
        float stored = (float)value;
        return stored < value ? BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(stored) + 1) : stored;
    }

    private static void Extend(double[] low, double[] high, (double X, double Y, double Z) p)
    {
        low[0] = Math.Min(low[0], p.X); low[1] = Math.Min(low[1], p.Y); low[2] = Math.Min(low[2], p.Z);
        high[0] = Math.Max(high[0], p.X); high[1] = Math.Max(high[1], p.Y); high[2] = Math.Max(high[2], p.Z);
    }

    private static void Floats(byte[] bytes, int at, params double[] values)
    {
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + 4 * i), (float)values[i]);
    }

    private static byte[] U16(List<int> values)
    {
        var bytes = new byte[values.Count * 2];
        for (int i = 0; i < values.Count; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2 * i), (ushort)values[i]);
        return bytes;
    }

    private static long Mod(long value, long modulus) => ((value % modulus) + modulus) % modulus;
    private static long FloorDiv(long value, long divisor) => (long)Math.Floor(value / (double)divisor);
}

/** Typed arrays in native placement: rows at align16(end + 20) after {marker, u64 count, u32 class, u32 0}. */
public static class NativeArray
{
    public const uint Marker = 0x80809FBD;

    /** Copies source with rows appended; an empty list leaves a zero descriptor. */
    public static byte[] Append(byte[] source, int descriptor, uint type, IReadOnlyList<byte[]> rows, out int start)
    {
        if (rows.Count == 0)
        {
            var empty = source.ToArray();
            empty.AsSpan(descriptor, 16).Clear();
            start = -1;
            return empty;
        }
        start = (source.Length + 20 + 15) & ~15;
        var output = new byte[start + rows.Sum(row => row.Length)];
        source.CopyTo(output, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(start - 20), Marker);
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(start - 16), (ulong)rows.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(start - 8), type);
        int at = start;
        foreach (var row in rows) { row.CopyTo(output, at); at += row.Length; }
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(descriptor), (ulong)rows.Count);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(descriptor + 8), start - descriptor - 24);
        BinaryPrimitives.WriteUInt64LittleEndian(output, (ulong)output.Length);
        return output;
    }
}
