using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Authoring;

/**
 * One compound instance as the writer lays it out: its 0x50 bytes with the shape pointer left zero,
 * and its child, an import name `iN` or `tN` or the data offset of an in-file object.
 */
public sealed record CompoundInstanceRow(byte[] Bytes, string? Import, int? ChildObject)
{
    public QsTransform Transform => new(Read(0x00), new Quaternion(Read(0x10), F(0x1C)), Read(0x20));
    public uint Flags => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(0x0C)) & 0xFF;
    public uint FilterInfo => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(0x38));
    public uint ChildMask => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(0x3C));
    public ulong UserData => BinaryPrimitives.ReadUInt64LittleEndian(Bytes.AsSpan(0x40));

    /** Writes the transform and flags 2, 4 and 8 from it; flag 1 is the child's convexity. */
    public void SetTransform(QsTransform transform, bool convex) =>
        CollisionCompoundWriter.WriteInstanceTransform(Bytes, 0, transform, StaticCompoundShape.Flags(transform, convex));

    /** Writes the filter, the child mask and the user data. */
    internal void SetPaletteData(uint filter, uint mask, ulong userData)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(0x38), filter);
        BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(0x3C), mask);
        BinaryPrimitives.WriteUInt64LittleEndian(Bytes.AsSpan(0x40), userData);
    }

    private Vector3 Read(int at) => new(F(at), F(at + 4), F(at + 8));
    private float F(int at) => BinaryPrimitives.ReadSingleLittleEndian(Bytes.AsSpan(at));
}

/**
 * Re-emits a root hkpStaticCompoundShape packfile with appended instances and a new tree. The class
 * names, the object bytes and the in-file child objects are kept; every array, fixup and import moves.
 */
public static class CollisionCompoundWriter
{
    private const int ObjectSize = 0xA0, KeyBits = 0x30, Instances = 0x38, ExtraInfos = 0x48, DisabledKeys = 0x58, Tree = 0x70;

    /** The compound's stored child key width, the signed byte at root +0x30. */
    internal static sbyte ChildKeyBits(HavokPackfile file) => (sbyte)file.U8(file.RootOffset + KeyBits);

    /** The user data word of the Havok shape object at `shape`. */
    internal static int ShapeUserData(HavokPackfile file, int shape) => file.I32(shape + 0x18);

    /** Writes an instance's transform and flag words at `at`; `flags` omits the base flag. */
    internal static void WriteInstanceTransform(byte[] bytes, int at, QsTransform transform, uint flags)
    {
        var (t, q, s) = (transform.Translation, transform.Rotation, transform.Scale);
        WriteFloats(bytes, at, t.X, t.Y, t.Z);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 0x0C), StaticCompoundShape.FlagBase | flags);
        WriteFloats(bytes, at + 0x10, q.X, q.Y, q.Z, q.W);
        WriteFloats(bytes, at + 0x20, s.X, s.Y, s.Z);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 0x2C), StaticCompoundShape.FlagBase);
    }

    /** Writes the floats in a row; a range outside the bytes throws InvalidDataException. */
    internal static void WriteFloats(byte[] bytes, int offset, params float[] values)
    {
        if (offset < 0 || offset > bytes.Length - values.Length * 4) throw new InvalidDataException("Placement range leaves its source.");
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + i * 4), values[i]);
    }

    /** A native shape key holds the instance index and child key in one u32. */
    internal static void RequireKeyCapacity(int instanceCount, int childBits)
    {
        if (instanceCount < 1 || childBits < 1 || Math.Max(1, CompressedMesh.BitLength((uint)instanceCount)) + childBits > 32)
            throw new NotSupportedException($"A compound with {instanceCount} instances cannot hold {childBits}-bit child keys.");
    }

    /**
     * The compound's instances as writer rows.
     * @return Null and a reason when the file holds a layout the writer does not reproduce.
     */
    public static (List<CompoundInstanceRow>? Rows, string? Refusal) Rows(StaticCompoundShape compound)
    {
        var file = compound.File;
        int root = file.RootOffset;
        if (file.I32(root + ExtraInfos + 8) != 0) return (null, "it has instance extra infos");
        if (file.I32(root + DisabledKeys + 8) != 0) return (null, "it has disabled shape keys");
        var fields = new HashSet<int>([root + Instances, root + Tree]);
        if (file.LocalFixups.Keys.Any(field => !fields.Contains(field))) return (null, "it has an array this writer does not know");
        var rows = new List<CompoundInstanceRow>(compound.Instances.Count);
        var pointers = new HashSet<int>();
        foreach (var instance in compound.Instances)
        {
            int field = instance.Offset + 0x30;
            pointers.Add(field);
            if (instance.ChildImport is null && instance.ChildObject is null) return (null, $"instance {instance.Index} has no child");
            var bytes = file.Bytes.AsSpan(file.Absolute(instance.Offset), StaticCompoundShape.InstanceSize).ToArray();
            rows.Add(new CompoundInstanceRow(bytes, instance.ChildImport, instance.ChildObject));
        }
        if (file.GlobalFixups.Any(fixup => !pointers.Contains(fixup.Source)) || file.Imports.Keys.Any(field => !pointers.Contains(field)))
            return (null, "a pointer outside the instance array is fixed up");
        foreach (var (offset, size) in ChildObjects(file))
            if (file.LocalFixups.Keys.Concat(file.GlobalFixups.Select(x => x.Source)).Concat(file.Imports.Keys)
                .Any(field => field >= offset && field < offset + size))
                return (null, $"in-file object 0x{offset:X} holds pointers");
        return (rows, null);
    }

    /**
     * Writes the compound with these rows and tree. Rows past the source count are appended; the
     * source's own rows keep their offsets, so object edits that name them still find them.
     * @param childKeyBits The new +0x30; null keeps the stored value.
     */
    public static byte[] Write(StaticCompoundShape source, IReadOnlyList<CompoundInstanceRow> rows, byte[] nodes,
        Vector3 domainMin, Vector3 domainMax, sbyte? childKeyBits = null)
    {
        var file = source.File;
        int root = file.RootOffset;
        if (rows.Count < source.Instances.Count) throw new NotSupportedException("The compound writer only appends instances.");
        if (nodes.Length != (2 * rows.Count - 1) * CompoundTree.NodeSize) throw new ArgumentException("The tree needs 2n - 1 nodes.", nameof(nodes));
        var builder = new HavokPackfileBuilder();
        var shape = file.Bytes.AsSpan(file.Absolute(root), ObjectSize).ToArray();
        // Array pointers stay zero on disk; the fixups fill them.
        foreach (int field in new[] { Instances, ExtraInfos, Tree }) shape.AsSpan(field, 8).Clear();
        if (childKeyBits is sbyte bits) shape[KeyBits] = (byte)bits;
        WriteFloats(shape, Tree + 0x10, domainMin.X, domainMin.Y, domainMin.Z);
        WriteFloats(shape, Tree + 0x20, domainMax.X, domainMax.Y, domainMax.Z);
        int at = builder.PlaceObject(shape, StaticCompoundShape.ClassName);
        if (at != root) throw new NotSupportedException("The compound is not the first object of its packfile.");
        var instances = new byte[StaticCompoundShape.InstanceSize * rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            rows[i].Bytes.CopyTo(instances, StaticCompoundShape.InstanceSize * i);
            instances.AsSpan(StaticCompoundShape.InstanceSize * i + 0x30, 8).Clear();
        }
        // The instance array must start where the old one did, or edits of source rows would miss.
        int newData = root + ObjectSize;
        if (source.Instances.Count > 0 && file.LocalFixups.GetValueOrDefault(root + Instances, -1) != newData)
            throw new NotSupportedException("The compound's instance array does not follow its object.");
        builder.Array(root + Instances, instances, rows.Count);
        builder.Array(root + Tree, nodes, nodes.Length / CompoundTree.NodeSize);

        var moved = new Dictionary<int, int>();
        foreach (var (offset, size) in ChildObjects(file))
            moved[offset] = builder.PlaceObject(file.Bytes.AsSpan(file.Absolute(offset), size), file.ClassAt(offset)!);
        int Field(int row) => newData + StaticCompoundShape.InstanceSize * row + 0x30;
        // Source globals keep their order, so an unchanged compound writes the same bytes.
        foreach (var (fixup, target) in file.GlobalFixups) builder.Global(fixup, moved[target]);
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Import is { } name) builder.Import(Field(i), name);
            else if (i >= source.Instances.Count)
                builder.Global(Field(i), moved.TryGetValue(rows[i].ChildObject ?? -1, out int child)
                    ? child : throw new InvalidDataException("An added instance names an object that is not in the compound."));
        }
        return builder.BuildLike(file);
    }

    /** Every object other than the root, with its bytes up to the next object or the data end. */
    private static IEnumerable<(int Offset, int Size)> ChildObjects(HavokPackfile file)
    {
        var offsets = file.Objects.Keys.Where(offset => offset != file.RootOffset).Order().ToArray();
        for (int i = 0; i < offsets.Length; i++)
            yield return (offsets[i], (i + 1 < offsets.Length ? offsets[i + 1] : file.DataLength) - offsets[i]);
    }
}
