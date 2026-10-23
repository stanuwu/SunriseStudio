using System.Buffers.Binary;
using System.Numerics;

namespace SunriseStudio.Scene;

/** A class-0x80809756 cell's box from the payload's +0x80 record, and its portal range at +0x84. */
public readonly record struct VisibilityCellBox(Vector3 Minimum, Vector3 Maximum, int FirstPortal, int PortalCount);

/**
 * A version-0x12 point-query payload: bounds at +0x14, outer KD tree at +0x2C, tile table at +0xA0, result
 * bound at +0x7C, cell records at +0x80 and portals at +0x84. Queries follow the client's 1A6FC50 walk.
 */
public sealed class VisibilityPayload
{
    public const ushort Version = 0x12;
    public const int MaximumBytes = 64 * 1024 * 1024;
    private readonly byte[] bytes;
    private readonly PackedTree outer;
    private readonly uint table;
    private readonly Dictionary<int, (int Offset, PackedTree Tree)?> tiles = [];
    public (Vector3 Minimum, Vector3 Maximum) Bounds { get; }
    /** Global cells the payload resolves; its paired cell resource holds exactly this many. */
    public uint ResultBound { get; }

    public VisibilityPayload(byte[] payload)
    {
        if (payload.Length is < 0xA4 or > MaximumBytes || BinaryPrimitives.ReadUInt16LittleEndian(payload) != Version)
            throw new InvalidDataException("Unsupported visibility payload.");
        bytes = payload;
        Bounds = (Vector(0x14), Vector(0x20));
        outer = new PackedTree(payload, 0, U32(0x2C) >> 5, U32(0x30), U32(0x3C), U32(0x2C) >> 5, Bounds);
        table = U32(0xA0);
        ResultBound = U32(0x7C);
        if (outer.Count != 0 && (table == 0 || table + (long)outer.Count * 4 > payload.Length))
            throw new InvalidDataException("A visibility tile table leaves its payload.");
    }

    /** The global cell holding point, or uint.MaxValue when no cell does. */
    public uint Query(Vector3 point)
    {
        int node = outer.Query(point);
        if (node < 0) return uint.MaxValue;
        if (Tile(node) is not { } tile) return uint.MaxValue;
        int local = tile.Tree.Query(point);
        if (local < 0) return uint.MaxValue;
        uint cell = Classify(tile.Offset, tile.Tree.LocalLeaf(local), point);
        uint count = U32(tile.Offset + 0x34);
        if (cell == uint.MaxValue || cell >= (count & 0xFFFF) || count >> 16 == 0) return uint.MaxValue;
        uint index = U32(tile.Offset + (int)U32(tile.Offset + 0x38) + 0x10 + (int)cell * 0x24);
        if (index >= ResultBound) throw new InvalidDataException("A visibility tile names a cell beyond its result bound.");
        return index;
    }

    /** Cell boxes from the +0x80 records; empty when the payload has none. Bounds round outwards on the u16 grid. */
    public VisibilityCellBox[] CellBoxes()
    {
        uint records = U32(0x80), portals = U32(0x84);
        if (records == 0) return [];
        if (records < 0xA4 || records + (long)ResultBound * 20 > bytes.Length)
            throw new InvalidDataException("Visibility cell records leave their payload.");
        var boxes = new VisibilityCellBox[ResultBound];
        for (int cell = 0; cell < boxes.Length; cell++)
        {
            int at = (int)records + cell * 20;
            uint first = U32(at), count = U32(at + 4);
            // The native record swizzles the six bounds: y-, x-, x+, z-, z+, y+.
            ushort yLow = U16(at + 8), xLow = U16(at + 10), xHigh = U16(at + 12), zLow = U16(at + 14), zHigh = U16(at + 16), yHigh = U16(at + 18);
            if (xLow > xHigh || yLow > yHigh || zLow > zHigh) throw new InvalidDataException("A visibility cell box is inverted.");
            if (count != 0 && (portals < 0xA4 || portals + ((long)first + count) * 16 > bytes.Length))
                throw new InvalidDataException("Visibility cell portals leave their payload.");
            boxes[cell] = new(new(Coordinate(0, xLow), Coordinate(1, yLow), Coordinate(2, zLow)),
                new(Coordinate(0, xHigh), Coordinate(1, yHigh), Coordinate(2, zHigh)), (int)first, (int)count);
        }
        return boxes;
    }

    /** Count of portal records with bit 27 set, which marks a polygon portal (vertices at +0x70). */
    public int PolygonPortals(IReadOnlyList<VisibilityCellBox> boxes)
    {
        uint portals = U32(0x84);
        int count = 0;
        foreach (var box in boxes)
            for (int i = 0; i < box.PortalCount; i++)
                if ((U32((int)portals + (box.FirstPortal + i) * 16) & 0x08000000) != 0) count++;
        return count;
    }

    /** The client's u16 interpolation, rounded after each float operation. */
    private float Coordinate(int axis, ushort value)
    {
        float low = Axis(Bounds.Minimum, axis), high = Axis(Bounds.Maximum, axis);
        float weight = value * (1f / 65535);
        return (1 - weight) * low + weight * high;
    }

    private static float Axis(Vector3 value, int axis) => axis == 0 ? value.X : axis == 1 ? value.Y : value.Z;

    private (int Offset, PackedTree Tree)? Tile(int node)
    {
        if (tiles.TryGetValue(node, out var known)) return known;
        uint offset = U32((int)table + node * 4);
        (int, PackedTree)? found = null;
        if (offset != 0)
        {
            if (offset + 0x4C > bytes.Length) throw new InvalidDataException("A visibility tile header leaves its payload.");
            int at = (int)offset;
            found = (at, new PackedTree(bytes, at, U32(at + 0x18) >> 5, U32(at + 0x1C), U32(at + 0x28), U32(at + 0x24),
                (VectorAt(at), VectorAt(at + 12))));
        }
        if (tiles.Count > 4096) tiles.Clear();
        tiles[node] = found;
        return found;
    }

    /** 1A7C2C0 then 1A7C1C0: a packed leaf is a cell or a plane tree; plane distance rounds after each MULSS/ADDSS. */
    private uint Classify(int tile, int index, Vector3 point)
    {
        uint value = Leaf(tile, index);
        if (value == uint.MaxValue || value < 0x80000000) return value;
        uint node = value & 0x7FFFFFFF;
        for (int step = 0; step < 4096; step++)
        {
            long at = tile + (long)U32(tile + 0x40) + 8 * node;
            if (at + 8 > bytes.Length) throw new InvalidDataException("A visibility plane node leaves its tile.");
            ulong packed = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan((int)at));
            uint flags = (uint)packed;
            long plane = tile + (long)U32(tile + 0x48) + (flags & 0x3FFFFFFF) * 16;
            if (plane + 16 > bytes.Length) throw new InvalidDataException("A visibility plane leaves its tile.");
            float a = F32((int)plane), b = F32((int)plane + 4), c = F32((int)plane + 8), d = F32((int)plane + 12);
            if (!float.IsFinite(a) || !float.IsFinite(b) || !float.IsFinite(c) || !float.IsFinite(d))
                throw new InvalidDataException("A visibility plane is not finite.");
            float x = point.X * a, y = point.Y * b, z = point.Z * c;
            float distance = x + y; distance += z; distance += d;
            bool positive = distance >= 0;
            node = (uint)(packed >> (positive ? 48 : 32)) & 0xFFFF;
            if ((flags & (1u << (positive ? 31 : 30))) != 0) return node == 0xFFFF ? uint.MaxValue : node;
        }
        throw new InvalidDataException("A visibility plane tree is cyclic or too deep.");
    }

    private uint Leaf(int tile, int index)
    {
        uint offset = U32(tile + 0x20);
        if (offset == 0) return uint.MaxValue;
        int width = (int)(U32(tile + 0x18) & 31);
        if (width == 0) return 0;
        long bit = (long)width * index, at = tile + offset + bit / 32 * 4;
        if (at + 8 > bytes.Length) throw new InvalidDataException("A packed visibility leaf crosses its tile.");
        ulong word = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan((int)at));
        ulong mask = (1UL << width) - 1;
        uint value = (uint)((word >> (int)(bit & 31)) & mask);
        if ((value & (1u << (width - 1))) == 0) return value;
        return value == mask ? uint.MaxValue : (value & (uint)(mask >> 1)) | 0x80000000;
    }

    private Vector3 Vector(int at) => VectorAt(at);
    private Vector3 VectorAt(int at) => new(F32(at), F32(at + 4), F32(at + 8));
    private uint U32(int at) => at >= 0 && at <= bytes.Length - 4 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at))
        : throw new InvalidDataException("A visibility field leaves its payload.");
    private ushort U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at, 2));
    private float F32(int at) => BitConverter.UInt32BitsToSingle(U32(at));

    /** Two-bit axes with prefix ranks; axis 3 is a leaf. Splits past SplitCount fall at the midpoint. */
    private sealed class PackedTree
    {
        private readonly byte[] raw;
        private readonly int[] axes, prefix;
        private readonly int splits, splitCount;
        private readonly (Vector3 Minimum, Vector3 Maximum) bounds;
        public int Count { get; }

        public PackedTree(byte[] raw, int origin, uint count, uint bits, uint splitPlanes, uint splitCount,
            (Vector3 Minimum, Vector3 Maximum) bounds)
        {
            this.raw = raw;
            this.bounds = bounds;
            if (count > 1_000_000) throw new InvalidDataException("A visibility tree exceeds the node budget.");
            Count = (int)count;
            if (!float.IsFinite(bounds.Minimum.X + bounds.Minimum.Y + bounds.Minimum.Z + bounds.Maximum.X + bounds.Maximum.Y + bounds.Maximum.Z)
                || bounds.Minimum.X > bounds.Maximum.X || bounds.Minimum.Y > bounds.Maximum.Y || bounds.Minimum.Z > bounds.Maximum.Z)
                throw new InvalidDataException("A visibility tree has invalid bounds.");
            long bitsAt = origin + (long)bits;
            if (count != 0 && (bits == 0 || bitsAt + (count + 15) / 16 * 4 > raw.Length))
                throw new InvalidDataException("A packed visibility tree leaves its payload.");
            if (splitCount > count) throw new InvalidDataException("A visibility tree has more splits than nodes.");
            splits = checked(origin + (int)splitPlanes);
            this.splitCount = (int)splitCount;
            if (splitCount != 0 && splits + (long)splitCount * 4 > raw.Length)
                throw new InvalidDataException("Visibility split planes leave their payload.");
            axes = new int[count];
            prefix = new int[count + 1];
            for (int i = 0; i < count; i++)
            {
                axes[i] = (int)(BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan((int)(bitsAt + i / 16 * 4))) >> (i & 15) * 2) & 3;
                prefix[i + 1] = prefix[i] + (axes[i] != 3 ? 1 : 0);
            }
        }

        /** The leaf node holding point, or -1 outside the tree. */
        public int Query(Vector3 point)
        {
            if (Count == 0 || Outside(point)) return -1;
            Vector3 low = bounds.Minimum, high = bounds.Maximum;
            int node = 0;
            for (int step = 0; step < Math.Min(Count, 256); step++)
            {
                if ((uint)node >= (uint)Count) throw new InvalidDataException("A visibility tree child leaves the tree.");
                int axis = axes[node];
                if (axis == 3) return node;
                float split = node < splitCount ? BitConverter.UInt32BitsToSingle(
                    BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(splits + node * 4))) : (Get(low, axis) + Get(high, axis)) * 0.5f;
                if (!float.IsFinite(split)) throw new InvalidDataException("A visibility split is not finite.");
                int rank = prefix[node + 1];
                if (Get(point, axis) >= split) { node = rank * 2; low = Set(low, axis, split); }
                else { node = rank * 2 - 1; high = Set(high, axis, split); }
            }
            throw new InvalidDataException("A visibility tree walk exceeds its depth budget.");
        }

        public int LocalLeaf(int node)
        {
            if ((uint)node >= (uint)Count || axes[node] != 3) throw new InvalidDataException("Expected a visibility leaf node.");
            return node - prefix[node + 1];
        }

        private bool Outside(Vector3 p) => p.X < bounds.Minimum.X || p.Y < bounds.Minimum.Y || p.Z < bounds.Minimum.Z
            || p.X > bounds.Maximum.X || p.Y > bounds.Maximum.Y || p.Z > bounds.Maximum.Z;
        private static float Get(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
        private static Vector3 Set(Vector3 v, int axis, float value) => axis == 0 ? v with { X = value }
            : axis == 1 ? v with { Y = value } : v with { Z = value };
    }
}
