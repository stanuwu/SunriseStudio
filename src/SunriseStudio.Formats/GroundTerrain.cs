using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/** A terrain material: the part technique (class 0x808071E8) and the group blend map at pixel t14. */
public sealed record GroundMaterial(uint Technique, uint Dyemap)
{
    /** A group dyemap of 0xFFFFFFFF binds nothing at t14. */
    public const uint NoDyemap = uint.MaxValue;
}

/** One 128-unit patch of a ground: its cell and the quads of each occupied 32-unit tile. */
public sealed record GroundPatch(int CellX, int CellY, IReadOnlyDictionary<(int X, int Y), List<(int X, int Y)>> Tiles);

/** The tags one patch's STerrain names: its four buffers and its material. */
public sealed record GroundPatchTags(uint Positions, uint Attributes, uint Indices, uint AlternateIndices, GroundMaterial Material);

/** One encoded patch: its STerrain and bounds table, its buffer payloads and its AO block. */
public sealed record TerrainPatchData(byte[] Terrain, byte[] Bounds, byte[] Positions, byte[] Attributes,
    byte[] Indices, byte[] AlternateIndices, byte[] Occlusion, (double[] Min, double[] Max) Box, int Index);

/**
 * Grounds as native terrain: one STerrain per 128-unit patch, one group per occupied 32-unit tile,
 * levels 0 to 3 with the same topology. Headers and group rows copy an installed STerrain's.
 */
public static class GroundTerrain
{
    // High word of a terrain's AO key ('kbrt') and of a group bounds visibility id ('aprt').
    public const uint KeyTag = 0x7472626B, BoundsTag = 0x74727061;
    private const double Patch = 128, Tile = 32, TexcoordScale = 63.0 / 16;
    // A strip restart, and the most a 16-bit index or part count holds.
    private const int MaxIndex = 0xFFFF;
    // Levels a group carries; the first three share the primary index buffer.
    private const int Levels = 4, PrimaryLevels = 3;
    /** The row sizes of the position and attribute buffers. */
    public static int PositionStride => Unsafe.SizeOf<PositionRow>();
    public static int AttributeStride => Unsafe.SizeOf<AttributeRow>();
    private static readonly (int, int)[] UOrder = [(0, 0), (1, 0), (1, 1), (0, 1)];

    /** A position row: x and y in 1/64 units, z in 1/8192 units split over z and w. */
    [StructLayout(LayoutKind.Sequential)]
    private struct PositionRow { public short X, Y, Z, W; }

    /** An attribute row: the SNORM16 normal with w at full, and the patch texcoord. */
    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeRow { public short NormalX, NormalY, NormalZ, NormalW; public Half U, V; }

    /** One level's index range in a group row; the four follow the group's UV transform. */
    [StructLayout(LayoutKind.Sequential)]
    private struct LevelRange { public uint Start, Count; }

    /** The patch cell and its quadrant mask; they follow the STerrain position offset. */
    [StructLayout(LayoutKind.Sequential)]
    private struct TerrainCell { public short X, Y; public uint Quadrants; }

    /** The alternate index count and the vertex count rounded to four; they end where UnkA8 starts. */
    [StructLayout(LayoutKind.Sequential)]
    private struct TerrainCounts { public uint AlternateIndices, PaddedVertices; }

    /** The AO key of a terrain index: 'kbrt' high word, index low word. */
    public static ulong RegistryKey(int index) => (ulong)KeyTag << 32 | (uint)index;

    /** Quad (x, y) as two upward triangles split on the same diagonal as the strip. */
    public static ((int, int, int), (int, int, int)) QuadTriangles(int columns, int x, int y)
    {
        int a = y * columns + x;
        return ((a, a + 1, a + columns), (a + 1, a + columns + 1, a + columns));
    }

    /** Patches row-major over cells; each tile's quads in grid order. */
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

    /** Tiles of a patch in group order: quadrants, then tiles, each (0,0), (1,0), (1,1), (0,1). */
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
     * Per-vertex AO, 255 for open sky: the mean over eight azimuths of cos^2 of the horizon elevation
     * within 16 units, over this ground's own heights only.
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

    /** 16-bit strips, one per run of quads in a grid column, joined by restarts. */
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

    /**
     * Encodes one patch. The STerrain copies `template`'s header and first group row and changes only
     * what the patch sets; the bounds table copies `boundsTemplate`'s header.
     * @param index The patch's terrain index, unique among its AO map's keys.
     */
    public static TerrainPatchData EncodePatch(GroundGrid grid, (double X, double Y, double Z)[] points, short[] normals,
        byte[] occlusion, GroundPatch patch, GroundPatchTags tags, int index, ReadOnlySpan<byte> template, ReadOnlySpan<byte> boundsTemplate)
    {
        if (index is < 0 or > MaxIndex) throw new InvalidDataException("A terrain index is 16 bits.");
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
        var attributes = vertices.Select(v => new AttributeRow
        {
            NormalX = normals[v * 3], NormalY = normals[v * 3 + 1], NormalZ = normals[v * 3 + 2], NormalW = short.MaxValue,
            U = (Half)((points[v].X - originX) / Patch), V = (Half)((points[v].Y - originY) / Patch)
        }).ToArray();

        ref readonly var source = ref TagView.Checked<STerrain>(template, 0);
        var templateGroups = TagView.Rows(template, in source.Groups, UnknownTerrainGroup.Class);
        if (templateGroups.IsEmpty) throw new InvalidDataException("The template terrain has no group row to copy.");
        var groupTemplate = templateGroups[0];
        var primary = new List<int>();
        var alternate = new List<int>();
        var groups = new UnknownTerrainGroup[order.Length];
        var groupLevels = new LevelRange[order.Length][];
        var parts = new List<UnknownTerrainPart>();
        var boxes = new SInstanceBoundsRow[order.Length];
        for (int g = 0; g < order.Length; g++)
        {
            var tile = order[g];
            var strip = Strip(columns, patch.Tiles[tile], local);
            if (strip.Count > MaxIndex) throw new InvalidDataException("A terrain group exceeds the part index count.");
            var levels = new LevelRange[Levels];
            for (int level = 0; level < PrimaryLevels; level++)
            {
                levels[level] = new() { Start = (uint)primary.Count, Count = (uint)strip.Count };
                primary.AddRange(strip);
            }
            levels[PrimaryLevels] = new() { Start = (uint)alternate.Count, Count = (uint)strip.Count };
            alternate.AddRange(strip);
            groupLevels[g] = levels;
            double[] glow = [double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity];
            double[] ghigh = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
            foreach (var quad in patch.Tiles[tile]) foreach (int v in Corners(columns, quad)) Extend(glow, ghigh, points[v]);
            double[] extent = [(ghigh[0] - glow[0]) / 2, (ghigh[1] - glow[1]) / 2, (ghigh[2] - glow[2]) / 2];
            var group = groupTemplate;
            group.Unk0 = (float)((glow[0] + ghigh[0]) / 2);
            group.Unk4 = (float)((glow[1] + ghigh[1]) / 2);
            group.Unk8 = (float)((glow[2] + ghigh[2]) / 2);
            group.UnkC = SingleUp(Math.Sqrt(extent[0] * extent[0] + extent[1] * extent[1] + extent[2] * extent[2]));
            group.Unk10 = (float)extent[0]; group.Unk14 = (float)extent[1]; group.Unk18 = (float)extent[2];
            group.UvTransform = new((float)TexcoordScale, (float)TexcoordScale,
                (float)(1.0 / 128 - tile.X * 63.0 / 64), (float)(1.0 / 128 - tile.Y * 63.0 / 64));
            group.DyeTexture = tags.Material.Dyemap;
            groups[g] = group;
            for (int level = 0; level < Levels; level++)
                parts.Add(new UnknownTerrainPart
                {
                    Material = tags.Material.Technique, IndexStart = levels[level].Start, IndexCount = (ushort)levels[level].Count,
                    GroupIndex = (byte)g, LodLevel = (byte)level
                });
            boxes[g] = new SInstanceBoundsRow
            {
                MinimumW1 = new((float)glow[0], (float)glow[1], (float)glow[2], 1),
                MaximumW1 = new((float)ghigh[0], (float)ghigh[1], (float)ghigh[2], 1),
                VisibilityId = (ulong)BoundsTag << 32 | (uint)(index * 16 + g)
            };
        }

        byte[] terrain = template[..Unsafe.SizeOf<STerrain>()].ToArray();
        ref var header = ref TagView.Edit<STerrain>(terrain);
        header.Unk10 = (float)low[0]; header.Unk14 = (float)low[1]; header.Unk18 = (float)low[2]; header.Unk1c = 1;
        header.Unk20 = (float)high[0]; header.Unk24 = (float)high[1]; header.Unk28 = (float)high[2]; header.Unk2c = 1;
        header.PositionOffset = new((float)offset[0], (float)offset[1], (float)offset[2], (float)offset[3]);
        header.PositionBuffer[0].Unk0 = tags.Positions;
        header.PositionBuffer[1].Unk0 = tags.Attributes;
        header.IndexBuffer = tags.Indices;
        header.Unk90[0].Unk0 = tags.Positions;
        header.Unk90[1].Unk0 = tags.Attributes;
        header.Unk98 = tags.AlternateIndices;
        uint quadrants = 0;
        foreach (var (tx, ty) in order) quadrants |= 1u << (tx / 2 + 2 * (ty / 2));
        var cell = new TerrainCell { X = checked((short)(2 * patch.CellX)), Y = checked((short)(2 * patch.CellY)), Quadrants = quadrants };
        MemoryMarshal.Write(terrain.AsSpan(TagView.FieldOffset<STerrain>(nameof(STerrain.PositionOffset)) + Unsafe.SizeOf<Vector4>()), in cell);
        var counts = new TerrainCounts { AlternateIndices = (uint)alternate.Count, PaddedVertices = (uint)((vertices.Count + 3) & ~3) };
        MemoryMarshal.Write(terrain.AsSpan(TagView.FieldOffset<STerrain>(nameof(STerrain.UnkA8)) - Unsafe.SizeOf<TerrainCounts>()), in counts);
        int groupRows = Rows(ref terrain, TagView.FieldOffset<STerrain>(nameof(STerrain.Groups)), UnknownTerrainGroup.Class, groups);
        int levelsAt = TagView.FieldOffset<UnknownTerrainGroup>(nameof(UnknownTerrainGroup.UvTransform)) + Unsafe.SizeOf<Vector4>();
        for (int g = 0; g < groups.Length; g++)
            MemoryMarshal.AsBytes(groupLevels[g].AsSpan()).CopyTo(terrain.AsSpan(groupRows + g * Unsafe.SizeOf<UnknownTerrainGroup>() + levelsAt));
        Rows(ref terrain, TagView.FieldOffset<STerrain>(nameof(STerrain.Parts)), UnknownTerrainPart.Class, parts.ToArray());
        TagView.CarrySizeWord(template, terrain);

        byte[] bounds = boundsTemplate[..Unsafe.SizeOf<SInstanceBoundsTable>()].ToArray();
        Rows(ref bounds, TagView.FieldOffset<SInstanceBoundsTable>(nameof(SInstanceBoundsTable.Boxes)), SInstanceBoundsRow.Class, boxes);
        TagView.CarrySizeWord(boundsTemplate, bounds);

        var block = vertices.Select(v => occlusion[v]).ToArray();
        var padded = new byte[(block.Length + 3) & ~3];
        block.CopyTo(padded, 0);
        return new(terrain, bounds, positions, MemoryMarshal.AsBytes(attributes.AsSpan()).ToArray(), U16(primary), U16(alternate),
            [.. padded, .. padded], (low, high), index);
    }

    /** Appends `rows` as the array at `field`. @return The first row's offset. */
    private static int Rows<T>(ref byte[] tag, int field, uint elementClass, T[] rows) where T : unmanaged
    {
        int bytes = rows.Length * Unsafe.SizeOf<T>();
        int first = TagRelocator.AppendArray(ref tag, field, elementClass, rows.Length, bytes);
        MemoryMarshal.AsBytes(rows.AsSpan()).CopyTo(tag.AsSpan(first, bytes));
        return first;
    }

    /** Positions as (x, y) * 64 and z * 8192 less the offset, z split over two signed words. */
    public static byte[] EncodePositions(IEnumerable<(double X, double Y, double Z)> points, double[] offset)
    {
        var output = new List<PositionRow>();
        foreach (var (x, y, z) in points)
        {
            double[] native = [x * 64 - offset[0], y * 64 - offset[1], z * 8192 - offset[2] - offset[3] * 65536];
            if (native.Any(v => !double.IsFinite(v) || Math.Abs(v - Math.Round(v)) >= 1e-7))
                throw new InvalidDataException("A terrain position cannot be represented exactly.");
            long px = (long)Math.Round(native[0]), py = (long)Math.Round(native[1]), combined = (long)Math.Round(native[2]);
            long pw = FloorDiv(combined + 32768, 65536), pz = combined - pw * 65536;
            if (new[] { px, py, pz, pw }.Any(v => v is < short.MinValue or > short.MaxValue))
                throw new InvalidDataException("A terrain position exceeds its packed signed lanes.");
            output.Add(new PositionRow { X = (short)px, Y = (short)py, Z = (short)pz, W = (short)pw });
        }
        return MemoryMarshal.AsBytes(output.ToArray().AsSpan()).ToArray();
    }

    /** A 16-bit index buffer header for `size` payload bytes. */
    public static byte[] IndexHeader(int size) =>
        Bytes(new IndexBufferHeader { Unk0 = 1, Wide = 0, ByteCount = (ulong)size, End = VertexBufferHeader.EndValue });

    /** A vertex buffer header for `size` payload bytes of `stride`-byte rows. */
    public static byte[] VertexHeader(int size, int stride) =>
        Bytes(new VertexBufferHeader { ByteCount = (uint)size, Stride = (ushort)stride, End = VertexBufferHeader.EndValue });

    private static byte[] Bytes<T>(T value) where T : unmanaged
    {
        var bytes = new byte[Unsafe.SizeOf<T>()];
        MemoryMarshal.Write(bytes, in value);
        return bytes;
    }

    /** float32 rounded up, so a stored radius never shrinks below its double. */
    internal static float SingleUp(double value)
    {
        float stored = (float)value;
        return stored < value ? MathF.BitIncrement(stored) : stored;
    }

    private static void Extend(double[] low, double[] high, (double X, double Y, double Z) p)
    {
        low[0] = Math.Min(low[0], p.X); low[1] = Math.Min(low[1], p.Y); low[2] = Math.Min(low[2], p.Z);
        high[0] = Math.Max(high[0], p.X); high[1] = Math.Max(high[1], p.Y); high[2] = Math.Max(high[2], p.Z);
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
