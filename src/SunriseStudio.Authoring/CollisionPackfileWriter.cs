using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Authoring;

/**
 * Lays out a Havok 2012 packfile data section: blocks aligned to 16 and padded with 0x7F, local
 * and global pointer fixups, and one virtual fixup per object naming its class.
 */
public sealed class HavokPackfileBuilder
{
    // The only header the game ships: version 9, 8-byte little-endian pointers, 2012.2 contents.
    private const uint Magic0 = 0x57E0E057, Magic1 = 0x10C0C010;
    private const int FileVersion = 9, SectionHeaderSize = 0x30, HeaderSize = 0x40;
    private const string ContentsVersion = "hk_2012.2.0-r1";
    // Data padding is 0x7F; fixup and class name tables pad with 0xFF.
    private const byte DataPad = 0x7F, TablePad = 0xFF;

    private readonly List<byte> data = [];
    private readonly List<(int Source, int Target)> local = [];
    private readonly List<(int Source, int Target)> global = [];
    private readonly List<(int Offset, string Class)> objects = [];
    private readonly List<(int Field, string Name)> imports = [];

    public int Length => data.Count;

    /** Records a pointer field that the loader fills from another packfile, named as `iN` or `tN`. */
    public void Import(int field, string name) => imports.Add((field, name));

    /** Appends a block at the next 16-byte boundary. @return Its data offset. */
    public int Place(ReadOnlySpan<byte> bytes)
    {
        Align();
        int offset = data.Count;
        data.AddRange(bytes.ToArray());
        return offset;
    }

    /** Places an object and records its class. */
    public int PlaceObject(ReadOnlySpan<byte> bytes, string className)
    {
        int offset = Place(bytes);
        objects.Add((offset, className));
        return offset;
    }

    /** Records a pointer field at source that the loader fills with the address of target. */
    public void Local(int source, int target) => local.Add((source, target));

    /** Records a pointer to an object in the data section. */
    public void Global(int source, int target) => global.Add((source, target));

    /** Overwrites bytes already placed. */
    public void Patch(int offset, ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++) data[offset + i] = bytes[i];
    }

    /**
     * Places array data and points the hkArray field at it. Empty arrays keep a null pointer.
     * @param field Data offset of the 16-byte hkArray field, already placed.
     */
    public void Array(int field, ReadOnlySpan<byte> bytes, int count)
    {
        WriteArrayHeader(field, count);
        if (count == 0) return;
        Local(field, Place(bytes));
    }

    /** Writes the hkArray size and capacity; the pointer stays zero on disk. */
    public void WriteArrayHeader(int field, int count)
    {
        var header = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), count);
        // Capacity equals size, with the do-not-free flag the loader expects for packfile arrays.
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 0x80000000u | (uint)count);
        Patch(field, header);
    }

    /**
     * The finished packfile.
     * @param classes Class name table in order, with each class's signature.
     * @param root Data offset of the contents root object.
     * @param userTag The packfile role; 1 is a per-mesh shape.
     */
    public byte[] Build(IReadOnlyList<(uint Signature, string Name)> classes, int root, int userTag)
    {
        Align();
        var names = new List<byte>();
        var nameOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (signature, name) in classes)
        {
            var entry = new byte[5];
            BinaryPrimitives.WriteUInt32LittleEndian(entry, signature);
            entry[4] = 9;
            names.AddRange(entry);
            nameOffsets[name] = names.Count;
            names.AddRange(Encoding.ASCII.GetBytes(name));
            names.Add(0);
        }
        Pad(names, TablePad);
        string rootClass = objects.Single(x => x.Offset == root).Class;
        var (tables, offsets) = Tables(nameOffsets);

        int namesStart = HeaderSize + 3 * SectionHeaderSize, dataStart = namesStart + names.Count;
        var file = new byte[dataStart + data.Count + tables.Count];
        var span = file.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic0);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], Magic1);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], userTag);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], FileVersion);
        span[16] = 8; span[17] = 1; span[18] = 0; span[19] = 1;
        // Three sections: class names, an empty types section and the data section.
        BinaryPrimitives.WriteInt32LittleEndian(span[0x14..], 3);
        BinaryPrimitives.WriteInt32LittleEndian(span[0x18..], 2);
        BinaryPrimitives.WriteInt32LittleEndian(span[0x1C..], root);
        BinaryPrimitives.WriteInt32LittleEndian(span[0x20..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(span[0x24..], nameOffsets[rootClass]);
        Encoding.ASCII.GetBytes(ContentsVersion).CopyTo(span[0x28..]);
        span[0x37] = TablePad;
        BinaryPrimitives.WriteInt32LittleEndian(span[0x38..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(span[0x3C..], -1);
        Section(span, 0, "__classnames__", namesStart, [names.Count, names.Count, names.Count, names.Count, names.Count, names.Count]);
        Section(span, 1, "__types__", dataStart, [0, 0, 0, 0, 0, 0]);
        Section(span, 2, "__data__", dataStart, offsets);
        names.CopyTo(file, namesStart);
        data.CopyTo(file, dataStart);
        tables.CopyTo(file, dataStart + data.Count);
        return file;
    }

    /**
     * The finished packfile with the header and class name section of a source packfile.
     * The root object must sit at the source's root offset, and every class must be named there.
     */
    public byte[] BuildLike(HavokPackfile source)
    {
        Align();
        if (objects.Count == 0 || objects[0].Offset != source.RootOffset || objects[0].Class != source.RootClass)
            throw new InvalidOperationException("The first object must be the source's root.");
        var (tables, offsets) = Tables(source.ClassNameOffsets);
        int dataStart = source.DataStart;
        var file = new byte[dataStart + data.Count + tables.Count];
        source.Bytes.AsSpan(0, dataStart).CopyTo(file);
        int section = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x18));
        var header = file.AsSpan(HeaderSize + section * SectionHeaderSize, SectionHeaderSize);
        if (BinaryPrimitives.ReadInt32LittleEndian(header[0x14..]) != dataStart)
            throw new InvalidDataException("The source's data section does not start after its header.");
        for (int i = 0; i < 6; i++) BinaryPrimitives.WriteInt32LittleEndian(header[(0x18 + 4 * i)..], offsets[i]);
        data.CopyTo(file, dataStart);
        tables.CopyTo(file, dataStart + data.Count);
        return file;
    }

    /**
     * The fixup, virtual, export and import tables after the data, each padded to 16 with 0xFF.
     * Imports are {i32 field, name} padded to 4, in descending field order, then -1, as installed files hold them.
     * @return The table bytes and the six section offsets relative to the data start.
     */
    private (List<byte> Tables, int[] Offsets) Tables(IReadOnlyDictionary<string, int> nameOffsets)
    {
        var tables = new List<byte>();
        foreach (var (source, target) in local) { Int(tables, source); Int(tables, target); }
        Pad(tables, TablePad);
        int globalStart = tables.Count;
        foreach (var (source, target) in global) { Int(tables, source); Int(tables, 2); Int(tables, target); }
        Pad(tables, TablePad);
        int virtualStart = tables.Count;
        foreach (var (offset, name) in objects)
        {
            if (!nameOffsets.TryGetValue(name, out int nameOffset)) throw new InvalidOperationException($"Class {name} is not in the name table.");
            Int(tables, offset);
            Int(tables, 0);
            Int(tables, nameOffset);
        }
        Pad(tables, TablePad);
        int exports = tables.Count;
        foreach (var (field, name) in imports.OrderByDescending(x => x.Field))
        {
            Int(tables, field);
            tables.AddRange(Encoding.ASCII.GetBytes(name));
            tables.Add(0);
            while (tables.Count % 4 != 0) tables.Add(TablePad);
        }
        // A non-empty import table ends with field -1.
        if (imports.Count > 0) Int(tables, -1);
        Pad(tables, TablePad);
        int d = data.Count;
        return (tables, [d, d + globalStart, d + virtualStart, d + exports, d + exports, d + tables.Count]);
    }

    private void Align()
    {
        while (data.Count % 16 != 0) data.Add(DataPad);
    }

    private static void Pad(List<byte> bytes, byte pad)
    {
        while (bytes.Count % 16 != 0) bytes.Add(pad);
    }

    private static void Int(List<byte> bytes, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        bytes.AddRange(buffer.ToArray());
    }

    /** A section header: 19-byte tag, 0xFF, absolute start, then six offsets relative to it. */
    private static void Section(Span<byte> file, int index, string tag, int start, int[] offsets)
    {
        var header = file.Slice(HeaderSize + index * SectionHeaderSize, SectionHeaderSize);
        Encoding.ASCII.GetBytes(tag).CopyTo(header);
        header[19] = TablePad;
        BinaryPrimitives.WriteInt32LittleEndian(header[0x14..], start);
        for (int i = 0; i < 6; i++) BinaryPrimitives.WriteInt32LittleEndian(header[(0x18 + 4 * i)..], offsets[i]);
    }
}

/** Writes per-mesh shape packfiles (user tag 1) the way the installed ones are laid out. */
public static class CollisionPackfileWriter
{
    // Class name tables of installed per-mesh packfiles: Havok's reflection classes, then the root.
    private static readonly (uint, string)[] ReflectionClasses =
        [(0x33D42383, "hkClass"), (0xB0EFA719, "hkClassMember"), (0x8A3609CF, "hkClassEnum"), (0xCE6F8A6C, "hkClassEnumItem")];
    private const uint CompressedMeshSignature = 0x7A192C95, ConvexVerticesSignature = 0xFFAFC3AA;
    private const int PerMeshUserTag = 1;

    /** A per-mesh packfile whose root is the given compressed mesh. Array sizes follow the mesh. */
    public static byte[] WriteCompressedMesh(CompressedMesh mesh)
    {
        if (mesh.UserStringCount != 0) throw new NotSupportedException("Compressed mesh user strings are not written.");
        var builder = new HavokPackfileBuilder();
        var shape = new byte[CompressedMesh.ObjectSize];
        mesh.Prefix.AsSpan(0, CompressedMesh.PrefixSize).CopyTo(shape);
        BinaryPrimitives.WriteSingleLittleEndian(shape.AsSpan(0x30), mesh.ConvexRadius);
        shape[0x34] = mesh.WeldingType;
        shape[0x35] = (byte)(mesh.HasPerPrimitiveCollisionFilterInfo ? 1 : 0);
        shape[0x36] = (byte)(mesh.HasPerPrimitiveUserData ? 1 : 0);
        // Array pointers are zero on disk; the fixups fill them.
        shape.AsSpan(0x38, 0x30).Clear();
        int tree = CompressedMesh.TreeOffset;
        Vector(shape, tree + 0x10, mesh.DomainMin, mesh.DomainMinW);
        Vector(shape, tree + 0x20, mesh.DomainMax, mesh.DomainMaxW);
        BinaryPrimitives.WriteInt32LittleEndian(shape.AsSpan(tree + 0x30), mesh.NumPrimitiveKeys);
        BinaryPrimitives.WriteInt32LittleEndian(shape.AsSpan(tree + 0x34), mesh.BitsPerKey);
        BinaryPrimitives.WriteUInt32LittleEndian(shape.AsSpan(tree + 0x38), mesh.MaxKeyValue);
        int root = builder.PlaceObject(shape, CompressedMesh.ClassName);

        // Member order, and a section's node array right after the section array.
        builder.Array(root + 0x38, U32(mesh.FilterPalette), mesh.FilterPalette.Length);
        builder.Array(root + 0x48, U32(mesh.UserDataPalette), mesh.UserDataPalette.Length);
        builder.WriteArrayHeader(root + 0x58, 0);
        builder.Array(root + tree, mesh.TopNodes, mesh.TopNodes.Length / MeshTree.TopNodeSize);
        var sections = new byte[CompressedMesh.SectionSize * mesh.Sections.Count];
        for (int s = 0; s < mesh.Sections.Count; s++) WriteSection(sections.AsSpan(CompressedMesh.SectionSize * s), mesh.Sections[s]);
        builder.WriteArrayHeader(root + tree + 0x40, mesh.Sections.Count);
        if (mesh.Sections.Count > 0)
        {
            int sectionData = builder.Place(sections);
            builder.Local(root + tree + 0x40, sectionData);
            for (int s = 0; s < mesh.Sections.Count; s++)
            {
                var nodes = mesh.Sections[s].Nodes;
                builder.Array(sectionData + CompressedMesh.SectionSize * s, nodes, nodes.Length / MeshTree.SectionNodeSize);
            }
        }
        builder.Array(root + tree + 0x50, U32(mesh.Primitives), mesh.Primitives.Length);
        var indices = new byte[2 * mesh.SharedIndices.Length];
        for (int i = 0; i < mesh.SharedIndices.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(indices.AsSpan(2 * i), mesh.SharedIndices[i]);
        builder.Array(root + tree + 0x60, indices, mesh.SharedIndices.Length);
        builder.Array(root + tree + 0x70, U32(mesh.PackedVertices), mesh.PackedVertices.Length);
        var shared = new byte[8 * mesh.SharedVertices.Length];
        for (int i = 0; i < mesh.SharedVertices.Length; i++) BinaryPrimitives.WriteUInt64LittleEndian(shared.AsSpan(8 * i), mesh.SharedVertices[i]);
        builder.Array(root + tree + 0x80, shared, mesh.SharedVertices.Length);
        var runs = new byte[8 * mesh.Runs.Length];
        for (int i = 0; i < mesh.Runs.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(runs.AsSpan(8 * i), mesh.Runs[i].Value);
            runs[8 * i + 4] = mesh.Runs[i].First;
            runs[8 * i + 5] = mesh.Runs[i].Count;
        }
        builder.Array(root + tree + 0x90, runs, mesh.Runs.Length);
        return builder.Build([.. ReflectionClasses, (CompressedMeshSignature, CompressedMesh.ClassName)], root, PerMeshUserTag);
    }

    /**
     * A per-mesh packfile whose root is a convex vertices shape with no connectivity.
     * @param prefix The first 0x30 object bytes of the shape it replaces, or null for a new shape.
     * @param planes Outward planes; every vertex satisfies dot(normal, v) + w <= 0.
     */
    public static byte[] WriteConvexVertices(IReadOnlyList<Vector3> vertices, IReadOnlyList<Vector4> planes, float radius,
        byte[]? prefix = null)
    {
        if (vertices.Count == 0) throw new ArgumentException("A convex shape needs vertices.", nameof(vertices));
        var builder = new HavokPackfileBuilder();
        var shape = new byte[0x80];
        if (prefix is not null) prefix.AsSpan(0, 0x30).CopyTo(shape);
        // Installed shapes carry dispatch byte 4 at +0x11, like the compressed mesh.
        else shape[0x11] = 4;
        BinaryPrimitives.WriteSingleLittleEndian(shape.AsSpan(0x20), radius);
        var min = vertices.Aggregate(Vector3.Min);
        var max = vertices.Aggregate(Vector3.Max);
        Vector(shape, 0x30, (max - min) * 0.5f, 0);
        Vector(shape, 0x40, (max + min) * 0.5f, 0);
        BinaryPrimitives.WriteInt32LittleEndian(shape.AsSpan(0x60), vertices.Count);
        int root = builder.PlaceObject(shape, CollisionShapes.ConvexVertices);
        // Four vertices per block as x[4], y[4], z[4]; spare lanes repeat the last vertex.
        int blocks = (vertices.Count + 3) / 4;
        var packed = new byte[48 * blocks];
        for (int i = 0; i < 4 * blocks; i++)
        {
            var v = vertices[Math.Min(i, vertices.Count - 1)];
            int block = 48 * (i / 4), lane = 4 * (i % 4);
            BinaryPrimitives.WriteSingleLittleEndian(packed.AsSpan(block + lane), v.X);
            BinaryPrimitives.WriteSingleLittleEndian(packed.AsSpan(block + 16 + lane), v.Y);
            BinaryPrimitives.WriteSingleLittleEndian(packed.AsSpan(block + 32 + lane), v.Z);
        }
        builder.Array(root + 0x50, packed, blocks);
        var planeBytes = new byte[16 * planes.Count];
        for (int i = 0; i < planes.Count; i++)
            Vector(planeBytes, 16 * i, new Vector3(planes[i].X, planes[i].Y, planes[i].Z), planes[i].W);
        builder.Array(root + 0x68, planeBytes, planes.Count);
        return builder.Build([.. ReflectionClasses, (ConvexVerticesSignature, CollisionShapes.ConvexVertices)], root, PerMeshUserTag);
    }

    /** One 0x60-byte section record; its node array pointer stays zero for the fixup. */
    private static void WriteSection(Span<byte> at, CompressedMeshSection section)
    {
        if (section.PrimitiveCount > 0xFF || section.PackedCount > 0xFF || section.RunCount > 0xFF)
            throw new InvalidDataException("A section count does not fit its byte.");
        BinaryPrimitives.WriteInt32LittleEndian(at[8..], section.Nodes.Length / MeshTree.SectionNodeSize);
        BinaryPrimitives.WriteUInt32LittleEndian(at[12..], 0x80000000u | (uint)(section.Nodes.Length / MeshTree.SectionNodeSize));
        Vector(at, 0x10, section.DomainMin, section.DomainMinW);
        Vector(at, 0x20, section.DomainMax, section.DomainMaxW);
        for (int i = 0; i < 6; i++) BinaryPrimitives.WriteSingleLittleEndian(at[(0x30 + 4 * i)..], section.Codec[i]);
        BinaryPrimitives.WriteInt32LittleEndian(at[0x48..], section.FirstPackedVertex);
        BinaryPrimitives.WriteUInt32LittleEndian(at[0x4C..], (uint)(section.SharedIndexStart << 8 | section.PackedCount));
        BinaryPrimitives.WriteUInt32LittleEndian(at[0x50..], (uint)(section.FirstPrimitive << 8 | section.PrimitiveCount));
        BinaryPrimitives.WriteUInt32LittleEndian(at[0x54..], (uint)(section.FirstRun << 8 | section.RunCount));
        at[0x58] = (byte)section.NumPackedVertices;
        at[0x59] = (byte)section.NumSharedIndices;
        BinaryPrimitives.WriteUInt16LittleEndian(at[0x5A..], (ushort)section.LeafIndex);
        at[0x5C] = (byte)section.Page;
        at[0x5D] = (byte)section.Flags;
        at[0x5E] = (byte)section.LayerData;
        at[0x5F] = (byte)section.Unused;
    }

    private static void Vector(Span<byte> at, int offset, Vector3 value, float w)
    {
        BinaryPrimitives.WriteSingleLittleEndian(at[offset..], value.X);
        BinaryPrimitives.WriteSingleLittleEndian(at[(offset + 4)..], value.Y);
        BinaryPrimitives.WriteSingleLittleEndian(at[(offset + 8)..], value.Z);
        BinaryPrimitives.WriteSingleLittleEndian(at[(offset + 12)..], w);
    }

    private static byte[] U32(uint[] values)
    {
        var bytes = new byte[4 * values.Length];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4 * i), values[i]);
        return bytes;
    }
}
