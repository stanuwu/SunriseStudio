using System.Buffers.Binary;

namespace SunriseStudio.Projects;

internal sealed record SidecarShape(uint Stride, IReadOnlyList<SidecarShapeField> Fields);
internal sealed record SidecarDiff(uint Offset, byte[] Old);
internal sealed record SidecarEntry(uint Index, SidecarEntryKind Kind, uint StockSize, uint BuiltSize, byte[] StockSha256,
    IReadOnlyList<SidecarDiff> Diffs, IReadOnlyList<SidecarOp> Ops, IReadOnlyList<SidecarSite> Sites);
internal sealed record SidecarFile(ushort PackageId, ushort PatchId, byte[] MetadataDigest, IReadOnlyList<SidecarTarget> Targets,
    IReadOnlyList<SidecarShape> Shapes, IReadOnlyList<SidecarEntry> Entries);

/** The `.merge` sidecar format. Little-endian, packed records, nothing after the last entry; the records are generated. */
internal static partial class MergeSidecarFormat
{
    /** A sidecar sits beside its package under the same stem with this extension. */
    public const string Extension = ".merge";
    /** The package header digest a sidecar binds to. */
    public const int PackageDigestOffset = 0x118;
    private const byte OpFlags = UniqueKey | Ordered;
    private const long NoPointer = -1;

    /** Encodes the file, then reads it back so an encoder fault cannot ship. */
    public static byte[] Write(SidecarFile file)
    {
        var output = new List<byte>();
        if (file.MetadataDigest.Length != DigestSize) throw new ArgumentException("The package digest is 20 bytes.", nameof(file));
        Put(output, new SidecarFileHeader(Magic, Version, 0, file.PackageId, file.PatchId, file.MetadataDigest, (uint)file.Targets.Count,
            (uint)file.Shapes.Count, (uint)file.Entries.Count));
        foreach (var target in file.Targets) Put(output, target);
        foreach (var shape in file.Shapes)
        {
            Put(output, new SidecarShapeHeader(shape.Stride, (uint)shape.Fields.Count));
            foreach (var field in shape.Fields) Put(output, field);
        }
        foreach (var entry in file.Entries)
        {
            if (entry.StockSha256.Length != Sha256Size) throw new ArgumentException("The stock digest is 32 bytes.", nameof(file));
            Put(output, new SidecarEntryHeader(entry.Index, entry.Kind, entry.StockSize, entry.BuiltSize, entry.StockSha256,
                (uint)entry.Diffs.Count, (uint)entry.Ops.Count, (uint)entry.Sites.Count));
            foreach (var diff in entry.Diffs)
            {
                Put(output, new SidecarDiffHeader(diff.Offset, (uint)diff.Old.Length));
                output.AddRange(diff.Old);
            }
            foreach (var op in entry.Ops) Put(output, op);
            foreach (var site in entry.Sites) Put(output, site);
        }
        if (output.Count > ByteLimit) throw new InvalidDataException("The merge sidecar exceeds its size limit.");
        byte[] bytes = output.ToArray();
        Read(bytes);
        return bytes;
    }

    /** Applies every format rule the runtime parser applies, but not the package binding; any break throws. */
    public static SidecarFile Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > ByteLimit) throw Bad("exceeds its size limit");
        var reader = new Cursor(bytes);
        var header = TakeFileHeader(ref reader, out bool headerClear);
        if (header.Magic != Magic) throw Bad("has the wrong magic");
        if (header.Version is not (Version or OldestVersion) || header.Flags != 0) throw Bad("has an unsupported version or flags");
        if (!headerClear) throw Bad("has a nonzero reserved header field");
        var targets = new List<SidecarTarget>();
        for (uint i = 0; i < header.TargetCount; i++)
        {
            var target = TakeTarget(ref reader);
            if (target.ModCount < target.StockCount) throw Bad("has a target whose rows count down");
            if (targets.Any(other => other.Table == target.Table && other.Descriptor == target.Descriptor))
                throw Bad("repeats a target");
            targets.Add(target);
        }
        uint shapeCount = header.ShapeCount;
        var shapes = new List<SidecarShape>();
        for (uint i = 0; i < shapeCount; i++)
        {
            var shapeHeader = TakeShapeHeader(ref reader);
            if (shapeHeader.Stride == 0) throw Bad("has a shape with no stride");
            reader.Need(checked((long)shapeHeader.FieldCount * ShapeFieldSize));
            var fields = new List<SidecarShapeField>();
            long end = 0;
            for (uint f = 0; f < shapeHeader.FieldCount; f++)
            {
                var field = TakeShapeField(ref reader, out bool fieldClear);
                int size = FieldSize(field.Kind);
                if (size == 0) throw Bad("has an unknown field kind");
                if (!fieldClear || field.RowOffset < end || field.RowOffset + (long)size > shapeHeader.Stride)
                    throw Bad("has overlapping or oversized fields");
                if (field.Kind != MergeFieldKind.RowPointer && field.Ref != NoElementShape && field.Ref <= i)
                    throw Bad("has an element or object shape that is not later");
                if (field.Kind == MergeFieldKind.ObjectPointer && field.Ref != NoElementShape && header.Version == OldestVersion)
                    throw Bad("has an object pointer that names a shape");
                end = field.RowOffset + (long)size;
                fields.Add(field);
            }
            shapes.Add(new(shapeHeader.Stride, fields));
        }
        foreach (var shape in shapes)
            foreach (var field in shape.Fields)
                if (field.Kind != MergeFieldKind.RowPointer && field.Ref != NoElementShape && field.Ref >= shapeCount)
                    throw Bad("names a missing element or object shape");
        // Per shape, the largest op a row pointer in it or in what it owns names; owned shapes come later.
        var reach = new long[shapes.Count];
        for (int index = shapes.Count - 1; index >= 0; index--)
        {
            reach[index] = NoPointer;
            foreach (var field in shapes[index].Fields)
                if (field.Kind == MergeFieldKind.RowPointer) reach[index] = Math.Max(reach[index], field.Ref);
                else if (field.Ref != NoElementShape)
                    reach[index] = Math.Max(reach[index], reach[field.Ref]);
        }
        var entries = new List<SidecarEntry>();
        for (uint i = 0; i < header.EntryCount; i++)
        {
            var entry = TakeEntryHeader(ref reader, out bool entryClear);
            if (!entryClear) throw Bad("has a nonzero reserved entry field");
            if (entries.Count > 0 && entries[^1].Index >= entry.Index) throw Bad("has entries out of order");
            if (entry.Kind > SidecarEntryKind.Added) throw Bad("has an unknown entry kind");
            var diffs = new List<SidecarDiff>();
            long diffEnd = 0;
            for (uint d = 0; d < entry.DiffCount; d++)
            {
                var diff = TakeDiffHeader(ref reader);
                if (diff.Length == 0 || diff.Offset < diffEnd || diff.Offset + (long)diff.Length > entry.StockSize)
                    throw Bad("has a diff out of order or range");
                diffEnd = diff.Offset + (long)diff.Length;
                diffs.Add(new(diff.Offset, reader.Bytes(checked((int)diff.Length))));
            }
            var ops = new List<SidecarOp>();
            for (uint o = 0; o < entry.OpCount; o++)
            {
                var op = TakeOp(ref reader, out bool opClear);
                if (!opClear) throw Bad("has a nonzero reserved op field");
                if (op.Kind is not (MergeOpKind.Append or MergeOpKind.SortedMap) || op.KeyWidth is not (0 or KeyWidth)
                    || (op.Flags & ~OpFlags) != 0 || op.Shape >= shapeCount || op.ModCount < op.StockCount || op.ModCount > op.Limit
                    || op.ParentOp != TopLevelOp && op.ParentOp >= o)
                    throw Bad("has an invalid op");
                uint stride = shapes[(int)op.Shape].Stride;
                bool keyed = op.Flags != 0 || op.Kind == MergeOpKind.SortedMap;
                if (keyed && op.KeyWidth == 0 || op.KeyWidth != 0 && op.KeyOffset + (long)op.KeyWidth > stride)
                    throw Bad("has an op key outside its row");
                long descriptorEnd = op.Descriptor + (long)FieldSize(MergeFieldKind.OwnedArray);
                bool placed = op.ParentOp == TopLevelOp
                    ? descriptorEnd <= entry.BuiltSize
                    : op.ParentRow < ops[(int)op.ParentOp].ModCount
                        && descriptorEnd <= shapes[(int)ops[(int)op.ParentOp].Shape].Stride;
                if (!placed) throw Bad("has an op array outside its entry or parent row");
                if (reach[op.Shape] >= entry.OpCount) throw Bad("has a row pointer to a missing op");
                ops.Add(op);
            }
            var sites = new List<SidecarSite>();
            long siteEnd = 0;
            for (uint s = 0; s < entry.SiteCount; s++)
            {
                var site = TakeSite(ref reader);
                if (site.Width is not (ByteSite or ShortSite or WordSite) || (site.Flags & ~SignedSite) != 0 || site.Offset < siteEnd
                    || site.Offset + (long)site.Width > entry.BuiltSize)
                    throw Bad("has a site out of order or range");
                bool local = (site.Target & OpLocalTarget) != 0;
                if (local ? (site.Target & ~OpLocalTarget) >= entry.OpCount : site.Target >= header.TargetCount)
                    throw Bad("has a site with no target");
                siteEnd = site.Offset + (long)site.Width;
                sites.Add(site);
            }
            bool valid = entry.Kind switch
            {
                SidecarEntryKind.Opaque => entry.DiffCount == 0 && entry.OpCount == 0,
                // Only ops move pointers into added bytes, so a table without ops cannot grow.
                SidecarEntryKind.Table => entry.StockSize > 0 && entry.BuiltSize >= entry.StockSize
                    && (entry.OpCount != 0 || entry.BuiltSize == entry.StockSize),
                SidecarEntryKind.Manifest => entry.DiffCount == 0 && entry.OpCount == 0 && entry.SiteCount == 0,
                _ => entry.StockSize == 0 && entry.DiffCount == 0 && entry.OpCount == 0
            };
            if (!valid) throw Bad("breaks an entry kind rule");
            entries.Add(new(entry.Index, entry.Kind, entry.StockSize, entry.BuiltSize, entry.StockSha256, diffs, ops, sites));
        }
        if (!reader.AtEnd) throw Bad("has trailing bytes");
        return new(header.PackageId, header.PatchId, header.MetadataDigest, targets, shapes, entries);
    }

    private static InvalidDataException Bad(string problem) => new($"The merge sidecar {problem}.");
    private static void Put16(List<byte> output, ushort value) { output.Add((byte)value); output.Add((byte)(value >> 8)); }
    private static void Put32(List<byte> output, uint value) { Put16(output, (ushort)value); Put16(output, (ushort)(value >> 16)); }

    private ref struct Cursor(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> bytes = bytes;
        private int position;
        public readonly bool AtEnd => position == bytes.Length;
        public readonly void Need(long count)
        {
            if (count < 0 || position + count > bytes.Length) throw Bad("is truncated");
        }
        public byte U8() { Need(1); return bytes[position++]; }
        public ushort U16() { Need(2); var value = BinaryPrimitives.ReadUInt16LittleEndian(bytes[position..]); position += 2; return value; }
        public uint U32() { Need(4); var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[position..]); position += 4; return value; }
        public byte[] Bytes(int count) { Need(count); var value = bytes.Slice(position, count).ToArray(); position += count; return value; }
    }
}
