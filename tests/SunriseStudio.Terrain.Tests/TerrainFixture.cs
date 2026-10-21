using System.Buffers.Binary;
using SunriseStudio.Packages;

/** A fake installation with one map, one bubble and the containers and registry a ground is placed in. */
internal static class TerrainFixture
{
    internal const ushort Package = 0x100;
    internal const uint Base = 0x80A00000, BubbleHash = 0x33333333;
    internal const uint Map = Base, Bubble = Base + 1, Branch = Base + 2, Geometry = Base + 3, Companion = Base + 4;
    internal const uint RegistryContainer = Base + 5, EmptyTable = Base + 6, RegistryTable = Base + 7, Registry = Base + 8;
    internal const uint RegistryHeader = Base + 9, RegistryData = Base + 10, Technique = Base + 11, Dyemap = Base + 12, Definition = Base + 13;
    // Installed registry rows: terrain indices 5 and 17, so the next index is 18.
    internal static readonly int[] InstalledIndices = [5, 17];
    internal const int RegistryDataSize = 64;

    /** The installation's entries; replaced entries swap in edited bytes. */
    internal static AuthoredPackageEntry[] Entries(IReadOnlyDictionary<uint, byte[]>? replaced = null)
    {
        var entries = new AuthoredPackageEntry[14];
        void Entry(uint tag, uint type, byte[] data, uint typeInfo = 0x100A, string? name = null) =>
            entries[tag - Base] = new(type, typeInfo, replaced?.GetValueOrDefault(tag) ?? data, name);
        var map = new Bytes(0x20); map.U32(8, Definition); int rows = map.Array(0x10, 0x80807D53, 16, 1); map.U32(rows, Bubble);
        Entry(Map, 0x808091DE, map.ToArray(), name: "map:terrain:test");
        // One layer owned by the one bubble, whose row mask at +40 holds it.
        var definition = new Bytes(0x40);
        int layers = definition.Array(0x10, 0x808098C4, 24, 1); definition.U32(layers + 8, 100);
        int bubbles = definition.Array(0x20, 0x808098C2, 80, 1); definition.U32(bubbles, BubbleHash); definition.U8(bubbles + 40, 1);
        Entry(Definition, 0x80809962, definition.ToArray());
        var bubble = new Bytes(0x20); bubble.U32(8, Branch); bubble.U32(0x18, BubbleHash);
        Entry(Bubble, 0x80807DAE, bubble.ToArray(), name: "bubble:terrain:test");
        var branch = new Bytes(0x20); int children = branch.Array(8, 0x808084C1, 16, 3);
        branch.U32(children, Geometry); branch.U32(children + 16, Companion); branch.U32(children + 32, RegistryContainer);
        Entry(Branch, 0x808091E0, branch.ToArray());
        Entry(Geometry, 0x80808A54, Container(23, EmptyTable));
        Entry(Companion, 0x80808A54, Container(1, EmptyTable));
        Entry(RegistryContainer, 0x80808A54, Container(1, RegistryTable));
        var empty = new Bytes(0x18);
        Entry(EmptyTable, 0x808099D6, empty.ToArray());
        var table = new Bytes(0x20); int row = table.Array(8, 0x808099D8, 144, 1);
        int resource = table.Allocate(0x30) + 16;
        table.U32(resource - 4, 0x80806F38); table.I64(row + 0x78, resource - row - 0x78); table.U32(resource + 0x10, Registry);
        Entry(RegistryTable, 0x808099D6, table.ToArray());
        var registry = new Bytes(120);
        registry.U32(8, RegistryHeader); registry.U32(32, uint.MaxValue); registry.U32(56, uint.MaxValue); registry.U32(0x70, 3);
        int registryRows = registry.Array(16, 0x8080726A, 16, InstalledIndices.Length);
        for (int i = 0; i < InstalledIndices.Length; i++)
        {
            registry.U64(registryRows + 16 * i, 0x7472626BUL << 32 | (uint)InstalledIndices[i]);
            registry.U32(registryRows + 16 * i + 8, (uint)(32 * i));
        }
        Entry(Registry, 0x80807268, registry.ToArray());
        // Vertex buffer header: u32 size, u16 stride 1, 0xDEADBEEF; not a sized resource.
        var header = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, RegistryDataSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 0xDEADBEEF);
        Entry(RegistryHeader, RegistryData, header, 0x411B);
        Entry(RegistryData, RegistryHeader, InstalledOcclusion(), 0x2511B);
        Entry(Technique, 0x808071E8, new byte[0x40]);
        Entry(Dyemap, 0x80809AD8, new byte[0x40]);
        foreach (var (tag, _) in replaced ?? new Dictionary<uint, byte[]>())
            if (tag - Base >= entries.Length) throw new InvalidDataException("Fixture edit outside the installation.");
        return entries;
    }

    internal static byte[] InstalledOcclusion() => Enumerable.Range(0, RegistryDataSize).Select(i => (byte)(200 + i % 7)).ToArray();

    internal static void Write(string folder, IReadOnlyDictionary<uint, byte[]>? replaced = null,
        IReadOnlyList<AuthoredPackageEntry>? ground = null, ushort groundPackage = 0)
    {
        Directory.CreateDirectory(Path.Combine(folder, "packages"));
        PackageWriter.WriteNew(Path.Combine(folder, "packages", "w64_terrain_0100_0.pkg"), Package, Entries(replaced));
        if (ground is not null)
            PackageWriter.WriteNew(Path.Combine(folder, "packages", $"w64_ground_{groundPackage:x4}_0.pkg"), groundPackage, ground);
    }

    private static byte[] Container(int members, uint first)
    {
        var container = new Bytes(0x30); container.U8(8, 1);
        int slots = container.Array(0x28, 0x80808BB0, 4, members);
        for (int i = 0; i < members; i++) container.U32(slots + 4 * i, first);
        return container.ToArray();
    }

    private sealed class Bytes(int initial)
    {
        private readonly List<byte> bytes = new(new byte[initial]);
        internal byte[] ToArray()
        {
            var result = bytes.ToArray();
            BinaryPrimitives.WriteUInt64LittleEndian(result, (ulong)result.Length);
            return result;
        }
        internal int Allocate(int count)
        {
            while (bytes.Count % 16 != 0) bytes.Add(0);
            int start = bytes.Count; bytes.AddRange(new byte[count]); return start;
        }
        internal int Array(int field, uint type, int stride, int count)
        {
            int header = Allocate(32 + stride * count); U32(header + 12, 0x80809FBD);
            I64(header + 16, count); U32(header + 24, type);
            I64(field, count); I64(field + 8, header + 16 - field - 8); return header + 32;
        }
        internal void U8(int p, byte x) => bytes[p] = x;
        internal void U32(int p, uint x) => Put(p, BitConverter.GetBytes(x));
        internal void U64(int p, ulong x) => Put(p, BitConverter.GetBytes(x));
        internal void I64(int p, long x) => Put(p, BitConverter.GetBytes(x));
        private void Put(int p, byte[] value) { for (int i = 0; i < value.Length; i++) bytes[p + i] = value[i]; }
    }
}
