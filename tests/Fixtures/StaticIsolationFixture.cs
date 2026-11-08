using System.Buffers.Binary;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

/** The fixture retains an authored hidden row and a separate aggregate-only replacement path. */
internal static class StaticIsolationFixture
{
    internal const uint Base = 0x80A00000, Table = Base, Bounds = Base + 1, Owner = Base + 2,
        Map = Base + 3, LoneTable = Base + 4, List = Base + 6, Entity = Base + 7,
        Config = Base + 8, Container = Base + 9, ContainerList = Base + 10,
        Bubble = Base + 11, Root = Base + 12, Mesh = Base + 13, MapList = Base + 14, SlotConfig = Base + 15;
    internal static readonly ulong[] RowIds = [101, 0xAB00000000000102, 103];
    internal const ulong BubbleWide = 0xABCDEF0123456789;
    internal static ObjectEdit Edit(PackageCatalog catalog, uint table, int index, float scale)
    {
        byte[] bytes = catalog.ReadOriginal(table);
        int row = checked(0x48 + (int)BitConverter.ToInt64(bytes, 0x48) + 16 + index * 48);
        return new(ObjectEditKind.PlacementTransform, new(table, ObjectEditLayout.StaticInstance, row, 48, index, 0,
            BinaryDelta.Hash(bytes.AsSpan(row, 48))), "Cull check", Scale: scale);
    }

    /** The synthetic map has one visible edited row and one authored hidden row. */
    internal static void Write(string folder, ulong candidate = 0x12345678, bool paddedConfig = false,
        bool sharedBounds = false)
    {
        if (paddedConfig && sharedBounds) throw new ArgumentException("Choose one extended fixture profile.");
        var entries = new List<AuthoredPackageEntry>();
        foreach (uint boundsTag in new[] { Bounds, LoneTable + 1 })
        {
            var table = new Bytes(160); table.U64(0x30, 112); table.U64(0x38, 3);
            int rows = table.Array(0x40, 0x808071A3, 48, 3);
            for (int i = 0; i < 3; i++)
            { table.F32(rows + i * 48 + 12, 1); table.F32(rows + i * 48 + 28, 1); table.U32(rows + i * 48 + 40, (uint)(100 + i * 4)); }
            int resource = table.Array(0x58, 0x8080967D, 4, 1); table.U32(resource, Mesh);
            int range = table.Array(0x68, 0x80807190, 8, 1); table.U16(range, 3); table.U16(range + 6, 7);
            table.U32(0x18, sharedBounds ? Bounds : boundsTag); table.U64(0x50, 0x82556000); table.U64(0x78, 0xF0EDE76F);
            table.V4(0x80, -1, -1, -1, 1); table.V4(0x90, 1, 1, 1, 1);
            var boxes = new Bytes(24); int box = boxes.Array(8, 0x80809673, 48, 3);
            for (int i = 0; i < 3; i++)
            {
                boxes.V4(box + i * 48, -1, -1, -1, 1); boxes.V4(box + i * 48 + 16, 1, 1, 1, 1);
                boxes.U64(box + i * 48 + 32, RowIds[i]); boxes.U64(box + i * 48 + 40, 0x5555);
            }
            entries.Add(new(0x8080966D, 0x100A, table.ToArray())); entries.Add(new(0x80809671, 0x100A, boxes.ToArray()));
            if (boundsTag != Bounds) continue;
            var owner = new Bytes(40); owner.U32(8, Table); owner.U64(0x18, ulong.MaxValue);
            owner.U8(0x20, 1); owner.U32(0x24, 0x8246733C); entries.Add(new(0x80806EF4, 0x100A, owner.ToArray(false)));
            entries.Add(new(0x80807320, 0x100A, MapBytes([RowIds[0], RowIds[1], candidate])));
        }
        entries.Add(new(0x808099D6, 0x100A, Placement(0x808071B3, Owner)));
        var entity = new Bytes(160); int actor = entity.Array(0x10, 0x80809C04, 12, 1); entity.U32(actor, Config);
        entries.Add(new(0x80809C0F, 0x100A, entity.ToArray()));
        var config = new Bytes(64); config.U64(0x10, 20); config.U32(32, 0x808071B2);
        entries.Add(new(0x80809C36, 0x100A, config.ToArray()));
        var container = new Bytes(56); container.U8(8, 1); int members = container.Array(0x28, 0x80808BB0, 4, sharedBounds ? 3 : 2);
        container.U32(members, List); container.U32(members + 4, MapList);
        if (sharedBounds) container.U32(members + 8, Base + 17);
        entries.Add(new(0x80808A54, 0x100A, container.ToArray()));
        var list = new Bytes(24); int child = list.Array(8, 0x808084C1, 16, 1); list.U32(child, Container);
        entries.Add(new(0x808091E0, 0x100A, list.ToArray()));
        var bubble = new Bytes(80); bubble.U32(8, ContainerList);
        entries.Add(new(0x80807DAE, 0x100A, bubble.ToArray())
        { WideHashes = paddedConfig ? new([BubbleWide]) : WideHashAliases.Empty });
        var root = new Bytes(32); child = root.Array(0x10, 0x80807D53, 16, 1);
        root.U32(child, paddedConfig ? uint.MaxValue : Bubble);
        if (paddedConfig) root.U64(child + 8, BubbleWide);
        entries.Add(new(0x808091DE, 0x100A, root.ToArray()));
        entries.Add(new(0x808071A7, 0x100A, new byte[24]));
        entries.Add(new(0x808099D6, 0x100A, Placement(0x80807246, Map)));
        byte[] padding = new byte[32]; if (paddedConfig) BinaryPrimitives.WriteUInt32LittleEndian(padding.AsSpan(5), Owner);
        entries.Add(new(0x80809C36, 0x100A, padding));
        if (sharedBounds)
        {
            var otherOwner = new Bytes(40); otherOwner.U32(8, LoneTable);
            otherOwner.U64(0x18, ulong.MaxValue); otherOwner.U8(0x20, 1); otherOwner.U32(0x24, 0x8246733C);
            entries.Add(new(0x80806EF4, 0x100A, otherOwner.ToArray(false)));
            entries.Add(new(0x808099D6, 0x100A, Placement(0x808071B3, Base + 16)));
        }
        if (paddedConfig)
        {
            var malformed = new Bytes(24); malformed.U64(8, 1);
            entries.Add(new(0x808099D6, 0x100A, malformed.ToArray()));
        }
        Directory.CreateDirectory(Path.Combine(folder, "packages"));
        PackageWriter.WriteNew(Path.Combine(folder, "packages", "w64_cull_0100_0.pkg"), 0x100, entries);
    }

    internal static byte[] ContainerMembers(params uint[] tags)
    {
        var container = new Bytes(56); container.U8(8, 1);
        int rows = container.Array(0x28, 0x80808BB0, 4, tags.Length);
        for (int index = 0; index < tags.Length; index++) container.U32(rows + index * 4, tags[index]);
        return container.ToArray();
    }

    /** A placement list with one static placement of `owner`. */
    internal static byte[] StaticPlacementList(uint owner) => Placement(0x808071B3, owner);

    /** A bubble's container list naming `containers`. */
    internal static byte[] BubbleContainers(params uint[] containers)
    {
        var list = new Bytes(24); int child = list.Array(8, 0x808084C1, 16, containers.Length);
        for (int index = 0; index < containers.Length; index++) list.U32(child + index * 16, containers[index]);
        return list.ToArray();
    }

    internal static byte[] ConfigPlacement()
    {
        var config = new Bytes(160);
        const int descriptor = 32;
        int instance = config.Append(24) + 8; config.U32(instance - 4, 0x80809927);
        config.U64(16, (ulong)(instance - 16)); config.U64(24, descriptor - 24);
        config.U32(descriptor, SlotConfig); config.U32(descriptor + 4, 0x80809927);
        config.U32(descriptor + 8, (uint)instance); config.U16(descriptor + 52, 4);
        int row = config.Array(descriptor + 0x58, 0x808099D8, 144, 1);
        config.U32(row, Entity); config.F32(row + 0x1C, 1); config.F32(row + 0x2C, 1);
        config.U64(row + 0x70, ulong.MaxValue);
        int prefix = config.Append(32); config.U32(prefix + 4, 0x808071B3); config.U32(prefix + 24, Owner);
        config.U64(row + 0x78, checked((ulong)(prefix + 8 - row - 0x78)));
        return config.ToArray();
    }

    private static byte[] Placement(uint marker, uint target)
    {
        var list = new Bytes(24); int row = list.Array(8, 0x808099D8, 144, 1);
        list.U32(row, Entity); list.F32(row + 0x1C, 1); list.F32(row + 0x2C, 1); list.U64(row + 0x70, ulong.MaxValue);
        int prefix = list.Append(32); list.U32(prefix + 4, marker); list.U32(prefix + 24, target);
        list.U64(row + 0x78, checked((ulong)(prefix + 8 - row - 0x78))); return list.ToArray();
    }

    private static byte[] MapBytes(ulong[] keys)
    {
        var map = new Bytes(192); int buckets = map.Array(0x20, 0x8080733E, 16, 8), values = map.Array(0x30, 0x80800007, 4, keys.Length);
        var occupied = new bool[8];
        for (int slot = 0; slot < keys.Length; slot++)
        {
            ulong x = unchecked((keys[slot] << 18) + ~keys[slot]), y = unchecked(21 * (x ^ (x >> 31))), z = unchecked(65 * (y ^ (y >> 11)));
            int index = (int)(((uint)z ^ (uint)(z >> 22)) & 7);
            while (occupied[index]) index = (index + 1) & 7;
            occupied[index] = true; map.U64(buckets + index * 16, keys[slot]);
            map.U32(buckets + index * 16 + 8, 0xC0000000u | (uint)slot); map.U32(values + slot * 4, (uint)slot);
        }
        return map.ToArray();
    }

    /** Fixture array headers are built independently of the production compact writer. */
    private sealed class Bytes(int initial)
    {
        private readonly List<byte> bytes = new(new byte[initial]);
        internal byte[] ToArray(bool length = true) { if (length) U64(0, (ulong)bytes.Count); return bytes.ToArray(); }
        internal int Append(int count) { int at = bytes.Count; bytes.AddRange(new byte[count]); return at; }
        internal int Array(int field, uint type, int stride, int count)
        {
            while ((bytes.Count + 4) % 16 != 0) bytes.Add(0);
            int prefix = Append(20 + stride * count), header = prefix + 4;
            U32(prefix, 0x80809FBD); U64(header, (ulong)count); U32(header + 8, type);
            U64(field, (ulong)count); U64(field + 8, (ulong)(header - field - 8)); return header + 16;
        }
        internal void V4(int p, float x, float y, float z, float w) { F32(p, x); F32(p + 4, y); F32(p + 8, z); F32(p + 12, w); }
        internal void F32(int p, float x) => U32(p, BitConverter.SingleToUInt32Bits(x));
        internal void U8(int p, byte x) => bytes[p] = x;
        internal void U16(int p, ushort x) => Put(p, BitConverter.GetBytes(x));
        internal void U32(int p, uint x) => Put(p, BitConverter.GetBytes(x));
        internal void U64(int p, ulong x) => Put(p, BitConverter.GetBytes(x));
        private void Put(int p, byte[] value) { for (int i = 0; i < value.Length; i++) bytes[p + i] = value[i]; }
    }
}
