using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Scene.Collision;

/**
 * A synthetic map with one static table of three rows and two models, placed in one of two containers,
 * and a static collision compound with one instance at each of the first two rows.
 */
internal static class StaticPlacementFixture
{
    internal const uint Base = 0x80A00000, Table = Base, Bounds = Base + 1, Owner = Base + 2, Root = Base + 3,
        Bubble = Base + 4, ContainerList = Base + 5, Container = Base + 6, List = Base + 7, Entity = Base + 8,
        Config = Base + 9, ModelA = Base + 10, ModelB = Base + 11, MapList = Base + 12, CollisionList = Base + 13,
        CollisionOwner = Base + 14, VisibilityMap = Base + 15, Compound = Base + 16, Shape = Base + 17, OtherContainer = Base + 18;
    internal const uint InstanceAuxiliary = 0x82556000, OwnerAuxiliary = 0x8246733C;
    internal const ulong AoKey = 0xF0EDE76F;
    /** Vertex counts that the rows' AO offsets step by: row 1 starts at align4(ModelA). */
    internal const int ModelAVertices = 24, ModelBVertices = 36;
    internal static readonly Vector3[] RowPositions = [new(0, 0, 0), new(10, 0, 0), new(20, 0, 0)];
    internal static readonly int[] RowModels = [0, 1, 0];
    internal static readonly uint[] RowAo = [0, 24, 60];

    internal static void Write(string folder)
    {
        var entries = new AuthoredPackageEntry[19];
        void Entry(uint tag, uint type, byte[] data, uint typeInfo = 8 << 9) => entries[tag - Base] = new(type, typeInfo, data);
        var table = new Bytes(160); table.U64(0x30, 112); table.U64(0x38, 3);
        int rows = table.Array(0x40, 0x808071A3, 48, 3);
        for (int i = 0; i < 3; i++)
        {
            int row = rows + i * 48;
            table.F32(row + 12, 1); table.V3(row + 16, RowPositions[i]); table.F32(row + 28, 1);
            table.F32(row + 32, 1); table.F32(row + 36, 1); table.U32(row + 40, RowAo[i]);
        }
        int resources = table.Array(0x58, 0x8080967D, 4, 2); table.U32(resources, ModelA); table.U32(resources + 4, ModelB);
        int ranges = table.Array(0x68, 0x80807190, 8, 3);
        for (int i = 0; i < 3; i++)
        { table.U16(ranges + i * 8, 1); table.U16(ranges + i * 8 + 2, (ushort)i); table.U16(ranges + i * 8 + 4, (ushort)RowModels[i]); table.U16(ranges + i * 8 + 6, 7); }
        table.U32(0x18, Bounds); table.U64(0x50, InstanceAuxiliary); table.U64(0x78, AoKey);
        table.V4(0x80, -1, -1, 0, 1); table.V4(0x90, 21, 1, 2, 1);
        Entry(Table, 0x8080966D, table.ToArray());
        var boxes = new Bytes(24); int box = boxes.Array(8, 0x80809673, 48, 3);
        for (int i = 0; i < 3; i++)
        {
            boxes.V4(box + i * 48, RowPositions[i].X - 1, -1, 0, 1); boxes.V4(box + i * 48 + 16, RowPositions[i].X + 1, 1, 2, 1);
            boxes.U64(box + i * 48 + 32, (ulong)(101 + i));
        }
        Entry(Bounds, 0x80809671, boxes.ToArray());
        var owner = new Bytes(40); owner.U32(8, Table); owner.U64(0x18, ulong.MaxValue); owner.U8(0x20, 1); owner.U32(0x24, OwnerAuxiliary);
        Entry(Owner, 0x80806EF4, owner.ToArray(false));
        var root = new Bytes(32); int child = root.Array(0x10, 0x80807D53, 16, 1); root.U32(child, Bubble);
        Entry(Root, 0x808091DE, root.ToArray());
        var bubble = new Bytes(32); bubble.U32(8, ContainerList); Entry(Bubble, 0x80807DAE, bubble.ToArray());
        var containers = new Bytes(24); child = containers.Array(8, 0x808084C1, 16, 2);
        containers.U32(child, Container); containers.U32(child + 16, OtherContainer);
        Entry(ContainerList, 0x808091E0, containers.ToArray());
        Entry(Container, 0x80808A54, ContainerMembers(List, MapList, CollisionList));
        Entry(OtherContainer, 0x80808A54, ContainerMembers(MapList));
        Entry(List, 0x808099D6, Placement(0x808071B3, Owner, Entity));
        var entity = new Bytes(160); int actor = entity.Array(0x10, 0x80809C04, 12, 1); entity.U32(actor, Config);
        Entry(Entity, 0x80809C0F, entity.ToArray());
        var config = new Bytes(64); config.U64(0x10, 20); config.U32(32, 0x808071B2); Entry(Config, 0x80809C36, config.ToArray());
        Entry(ModelA, 0x808071A7, Model(new(0, 0, 0.5f), 0.5f));
        Entry(ModelB, 0x808071A7, Model(new(0, 0, 1), 1));
        Entry(MapList, 0x808099D6, Placement(0x80807246, VisibilityMap, Entity));
        Entry(CollisionList, 0x808099D6, Placement(StaticCollisionReader.CollisionMarker, CollisionOwner, 0));
        Entry(CollisionOwner, StaticCollisionReader.OwnerClass, CollisionOwnerBytes(Compound, Shape));
        Entry(VisibilityMap, 0x80807320, MapBytes([101, 102, 103]));
        uint packfile = (26u << 9) | (7u << 6);
        entries[Compound - Base] = new(uint.MaxValue, packfile, CompoundBytes());
        var mesh = CompressedMeshEncoder.Encode(new CompressedMeshSource([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [0, 1, 2]));
        entries[Shape - Base] = new(uint.MaxValue, packfile, CollisionPackfileWriter.WriteCompressedMesh(mesh.Mesh));
        Directory.CreateDirectory(Path.Combine(folder, "packages"));
        PackageWriter.WriteNew(Path.Combine(folder, "packages", "w64_placement_0100_0.pkg"), 0x100, entries);
    }

    /** The compound's child is one triangle; instance i sits at row i's world transform. */
    internal static byte[] CompoundBytes()
    {
        var builder = new HavokPackfileBuilder();
        var rows = new List<CompoundInstanceRow>();
        var leaves = new List<CompoundBox>();
        for (int i = 0; i < 2; i++)
        {
            var row = new CompoundInstanceRow(new byte[StaticCompoundShape.InstanceSize], "i0", null);
            var transform = new QsTransform(RowPositions[i], Quaternion.Identity, Vector3.One);
            row.SetTransform(transform, false);
            BinaryPrimitives.WriteUInt32LittleEndian(row.Bytes.AsSpan(0x38), 0x0000002A);
            BinaryPrimitives.WriteUInt32LittleEndian(row.Bytes.AsSpan(0x3C), 0xFFFFFFFF);
            BinaryPrimitives.WriteUInt64LittleEndian(row.Bytes.AsSpan(0x40), 0x10000);
            rows.Add(row);
            var at = RowPositions[i];
            leaves.Add(new(at.X, at.Y, at.Z, at.X + 1, at.Y + 1, at.Z));
        }
        var (nodes, min, max) = CompoundTree.Build(leaves);
        byte[] root = new byte[0xA0]; root[0x30] = 1;
        WriteFloats(root, 0x80, min.X, min.Y, min.Z); WriteFloats(root, 0x90, max.X, max.Y, max.Z);
        builder.PlaceObject(root, StaticCompoundShape.ClassName);
        builder.Array(0x38, rows.SelectMany(row => row.Bytes).ToArray(), rows.Count);
        for (int i = 0; i < rows.Count; i++) builder.Import(0xA0 + i * StaticCompoundShape.InstanceSize + 0x30, "i0");
        builder.Array(0x70, nodes, nodes.Length / CompoundTree.NodeSize);
        return builder.Build([(0, StaticCompoundShape.ClassName)], 0, 0);
    }

    /** The scene decoder reads only +0x60 (center) and +0x6C (scalar) here; buffers come from a mesh cache. */
    private static byte[] Model(Vector3 center, float scale)
    {
        var model = new Bytes(0x90); model.V3(0x60, center); model.F32(0x6C, scale);
        return model.ToArray(false);
    }

    internal static byte[] ContainerMembers(params uint[] tags)
    {
        var container = new Bytes(56); container.U8(8, 1);
        int rows = container.Array(0x28, 0x80808BB0, 4, tags.Length);
        for (int index = 0; index < tags.Length; index++) container.U32(rows + index * 4, tags[index]);
        return container.ToArray();
    }

    private static byte[] Placement(uint marker, uint target, uint entity)
    {
        var list = new Bytes(24); int row = list.Array(8, 0x808099D8, 144, 1);
        list.U32(row, entity); list.F32(row + 0x1C, 1); list.F32(row + 0x2C, 1); list.U64(row + 0x70, ulong.MaxValue);
        int prefix = list.Append(32); list.U32(prefix + 4, marker); list.U32(prefix + 24, target);
        list.U64(row + 0x78, checked((ulong)(prefix + 8 - row - 0x78))); return list.ToArray();
    }

    private static byte[] CollisionOwnerBytes(uint compound, uint shape)
    {
        byte[] bytes = new byte[132];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, bytes.Length);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(0x48), 1);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(0x50), 0x20);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x58), compound);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x6C), 0x80809FBD);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(0x70), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x78), 0x80800014);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x80), shape);
        return bytes;
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

    private static void WriteFloats(byte[] bytes, int at, params float[] values)
    {
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + i * 4), values[i]);
    }

    /** Fixture array headers are built independently of the production writers. */
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
        internal void V3(int p, Vector3 v) { F32(p, v.X); F32(p + 4, v.Y); F32(p + 8, v.Z); }
        internal void V4(int p, float x, float y, float z, float w) { F32(p, x); F32(p + 4, y); F32(p + 8, z); F32(p + 12, w); }
        internal void F32(int p, float x) => U32(p, BitConverter.SingleToUInt32Bits(x));
        internal void U8(int p, byte x) => bytes[p] = x;
        internal void U16(int p, ushort x) => Put(p, BitConverter.GetBytes(x));
        internal void U32(int p, uint x) => Put(p, BitConverter.GetBytes(x));
        internal void U64(int p, ulong x) => Put(p, BitConverter.GetBytes(x));
        private void Put(int p, byte[] value) { for (int i = 0; i < value.Length; i++) bytes[p + i] = value[i]; }
    }
}
