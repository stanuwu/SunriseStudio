using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/** Display stat groups are shared by many items, so one item's curve change gets its own group. */
public static class InvestmentStatGroups
{
    public const uint TableClass = SStatGroupTable.Class;
    private const uint GroupClass = SStatGroup.Class, StatClass = SStatGroupRow.Class, CurveClass = SStatCurvePoint.Class;
    private static readonly int GroupStride = Unsafe.SizeOf<SStatGroup>(), StatStride = Unsafe.SizeOf<SStatGroupRow>();
    private static readonly int PointStride = Unsafe.SizeOf<SStatCurvePoint>();
    private static readonly int GroupsField = TagView.FieldOffset<SStatGroupTable>(nameof(SStatGroupTable.Table));
    private static readonly int StatsField = TagView.FieldOffset<SStatGroup>(nameof(SStatGroup.Stats));
    private static readonly int SpareField = TagView.FieldOffset<SStatGroup>(nameof(SStatGroup.SpareArray));
    private static readonly int CurveField = TagView.FieldOffset<SStatGroupRow>(nameof(SStatGroupRow.DisplayCurve));
    private static readonly int DisplayStatField = TagView.FieldOffset<ItemDisplayStrings>(nameof(ItemDisplayStrings.TalentGridItemBlock));
    // The native group index is a signed byte checked against the table's row count.
    public const int MaximumGroups = 128;

    public static int Count(ReadOnlyMemory<byte> table) => TagArray.Checked(table.Span, GroupsField, GroupClass, GroupStride).Count;

    public static IReadOnlyList<uint> Hashes(ReadOnlyMemory<byte> table)
    {
        var groups = TagArray.Checked(table.Span, GroupsField, GroupClass, GroupStride);
        return Enumerable.Range(0, groups.Count).Select(index => GroupHash(table.Span, groups.Data, index)).ToArray();
    }

    /**
     * The display's stat group, or null when it has no stat block or no group.
     * A stat block pointer that leaves the tag throws.
     */
    public static int? ReadDisplayGroup(ReadOnlyMemory<byte> display)
    {
        var span = display.Span;
        if (!TagView.FitsThrough<ItemDisplayStrings>(span, 0, nameof(ItemDisplayStrings.TalentGridItemBlock))) return null;
        int block = TagPointer.Checked(span, DisplayStatField);
        if (block < 4 || !TagView.Fits<SItemDisplayStatBlock>(span, block)
            || TagView.ClassAt(span, block) != SItemDisplayStatBlock.Class) return null;
        byte group = TagView.At<SItemDisplayStatBlock>(span, block).StatGroupIndex;
        return group == byte.MaxValue ? null : group;
    }

    /** An item display selects its stat group by the group byte of its stat block. */
    public static byte[] SetDisplayGroup(ReadOnlyMemory<byte> display, int group)
    {
        if (group is < 0 or >= MaximumGroups) throw new InvalidDataException("The stat group index is outside the native range.");
        var span = display.Span;
        int block = TagPointer.Checked(span, DisplayStatField);
        if (block < 4 || !TagView.Fits<SItemDisplayStatBlock>(span, block)
            || TagView.ClassAt(span, block) != SItemDisplayStatBlock.Class)
            throw new InvalidDataException("The item display has no stat block.");
        byte[] result = display.ToArray();
        TagView.Edit<SItemDisplayStatBlock>(result, block).StatGroupIndex = (byte)group;
        return result;
    }

    /**
     * Appends a copy of `sourceGroup` with its own stat rows and the given curves.
     * @param hash Identity of the new group; it must not name another group.
     * @return The table and the new group's index.
     */
    public static (byte[] Table, int Index) AddGroup(ReadOnlyMemory<byte> table, int sourceGroup, uint hash,
        IReadOnlyDictionary<byte, IReadOnlyList<InvestmentStatPoint>> curves)
    {
        var groups = TagArray.Checked(table.Span, GroupsField, GroupClass, GroupStride);
        if (sourceGroup < 0 || sourceGroup >= groups.Count) throw new InvalidDataException("The source stat group is outside the table.");
        if (groups.Count + 1 > MaximumGroups) throw new InvalidDataException("The stat group table is full.");
        if (Enumerable.Range(0, groups.Count).Any(index => GroupHash(table.Span, groups.Data, index) == hash))
            throw new InvalidDataException("Another stat group already uses this identity.");
        int source = groups.Data + sourceGroup * GroupStride;
        byte[] row = table.Span.Slice(source, GroupStride).ToArray();
        TagView.Edit<SStatGroup>(row).GroupHash = hash;
        byte[] output = InvestmentItemClone.AppendRow(table, GroupsField, GroupStride, row, [StatsField, SpareField], source);
        return (SetCurves(output, groups.Count, curves), groups.Count);
    }

    /**
     * Gives `group` a private copy of its stat rows with the given curves replaced.
     * Only a group no installed item uses may be changed this way.
     */
    public static byte[] SetCurves(ReadOnlyMemory<byte> table, int group,
        IReadOnlyDictionary<byte, IReadOnlyList<InvestmentStatPoint>> curves)
    {
        var groups = TagArray.Checked(table.Span, GroupsField, GroupClass, GroupStride);
        if (group < 0 || group >= groups.Count) throw new InvalidDataException("The stat group is outside the table.");
        int row = groups.Data + group * GroupStride;
        var rows = TagArray.Checked(table.Span, row + StatsField, StatClass, StatStride);
        var present = Enumerable.Range(0, rows.Count)
            .Select(index => TagView.At<SStatGroupRow>(table.Span, rows.Data + index * StatStride).StatIndex).ToHashSet();
        foreach (var (stat, points) in curves)
        {
            if (!present.Contains(stat)) throw new InvalidDataException($"The stat group has no row for stat {stat}.");
            if (points.Count == 0 || points.Zip(points.Skip(1)).Any(pair => pair.Second.Input <= pair.First.Input))
                throw new InvalidDataException("Stat curve inputs must increase.");
        }
        byte[] result = table.ToArray();
        int rowHeader = TagRelocator.AppendObject(ref result, TagArray.Marker, TagArray.HeaderSize + rows.Count * StatStride);
        var curveHeaders = new Dictionary<byte, int>();
        foreach (var (stat, points) in curves)
            curveHeaders[stat] = TagRelocator.AppendObject(ref result, TagArray.Marker, TagArray.HeaderSize + points.Count * PointStride);
        TagArray.WriteHeader(result, rowHeader, rows.Count, StatClass);
        for (int i = 0; i < rows.Count; i++)
        {
            int from = rows.Data + i * StatStride, to = rowHeader + TagArray.HeaderSize + i * StatStride;
            table.Span.Slice(from, StatStride).CopyTo(result.AsSpan(to));
            ref readonly var source = ref TagView.At<SStatGroupRow>(result, from);
            ref readonly var copy = ref TagView.At<SStatGroupRow>(result, to);
            byte stat = source.StatIndex;
            if (curveHeaders.TryGetValue(stat, out int curve))
                TagArray.WriteField(result, TagView.OffsetOf(result, in copy.DisplayCurve), curves[stat].Count, curve);
            // The copied curve keeps its installed target.
            else if (source.DisplayCurve.Count != 0)
                I64(result, TagView.OffsetOf(result, in copy.DisplayCurve.Relative), source.DisplayCurve.Relative + from - to);
        }
        foreach (var (stat, points) in curves)
        {
            int header = curveHeaders[stat];
            TagArray.WriteHeader(result, header, points.Count, CurveClass);
            for (int i = 0; i < points.Count; i++)
            {
                ref var point = ref TagView.Edit<SStatCurvePoint>(result, header + TagArray.HeaderSize + i * PointStride);
                point.Input = points[i].Input;
                point.Output = points[i].Output;
            }
        }
        int statsRelative = TagView.OffsetOf(result, in TagView.At<SStatGroup>(result, row).Stats.Relative);
        I64(result, statsRelative, rowHeader - statsRelative);
        if (TagView.At<SStatGroupTable>(table.Span).TagSize == (ulong)table.Length)
            TagView.Edit<SStatGroupTable>(result).TagSize = (ulong)result.Length;
        return result;
    }

    /**
     * The installed table plus one live copy of each array Studio added; group indices stay.
     * @param original The installed table.
     * @param current The edited table; it keeps every installed group in order.
     */
    public static byte[] Rebuild(ReadOnlyMemory<byte> original, ReadOnlyMemory<byte> current)
    {
        var before = TagArray.Checked(original.Span, GroupsField, GroupClass, GroupStride);
        var after = TagArray.Checked(current.Span, GroupsField, GroupClass, GroupStride);
        // The checked group field above holds the whole root, so At cannot throw here.
        bool sized = TagView.FitsThrough<SStatGroupTable>(original.Span, 0, nameof(SStatGroupTable.TagSize))
            && TagView.At<SStatGroupTable>(original.Span).TagSize == (ulong)original.Length;
        if (before.Count == 0 || after.Count < before.Count || current.Length < original.Length)
            throw new InvalidDataException("The stat group table lost installed groups.");
        for (int index = 0; index < before.Count; index++)
            if (GroupHash(original.Span, before.Data, index) != GroupHash(current.Span, after.Data, index))
                throw new InvalidDataException("An installed stat group changed its identity.");
        InvestmentItemClone.RequireOnlyChanged(original.Span, current.Span,
            InvestmentItemClone.ArraySpans(original, GroupsField, GroupStride),
            "The stat group table changed outside its groups.");

        var output = new List<byte>(current.Length);
        output.AddRange(original.Span);
        var copies = new Dictionary<int, int>();
        // Group rows keep their installed header when the count holds or they end the installed table.
        bool inPlace = after.Count == before.Count || before.Data + before.Count * GroupStride == original.Length;
        int groups = Copy(after.Data, after.Count, GroupStride, inPlace ? before.Data : -1);
        TagArray.WriteField(CollectionsMarshal.AsSpan(output), GroupsField, after.Count, groups - TagArray.HeaderSize);
        for (int index = 0; index < after.Count; index++)
        {
            int from = after.Data + index * GroupStride, to = groups + index * GroupStride;
            if (TagPointer.Checked(current.Span, from + SpareField + TagArray.RelativeOffset) >= original.Length)
                throw new NotSupportedException("A stat group's spare array lies outside the installed table.");
            Keep(from + SpareField, to + SpareField, 0, 0);
            int stats = Keep(from + StatsField, to + StatsField, StatClass, StatStride);
            if (stats < 0) continue;
            var rows = TagArray.Checked(current.Span, from + StatsField, StatClass, StatStride);
            for (int row = 0; row < rows.Count; row++)
                Keep(rows.Data + row * StatStride + CurveField, stats + row * StatStride + CurveField, CurveClass, PointStride);
        }
        byte[] result = output.ToArray();
        if (sized) TagView.Edit<SStatGroupTable>(result).TagSize = (ulong)result.Length;
        return result;

        // Points the descriptor at `to` where the one at `from` points; an array Studio added is copied.
        // Returns the copy's data offset, or -1 when nothing was copied.
        int Keep(int from, int to, uint type, int stride)
        {
            int header = TagPointer.Checked(current.Span, from + TagArray.RelativeOffset);
            var array = TagArray.Checked(current.Span, from, type, stride);
            int relative = to + TagArray.RelativeOffset;
            if (header < 0 || header >= original.Length && array.Count == 0) { Set(relative, 0); return -1; }
            if (header < original.Length) { Set(relative, header - relative); return -1; }
            int data = Copy(array.Data, array.Count, stride);
            Set(relative, data - TagArray.HeaderSize - relative);
            return data;
        }
        // A Studio array two descriptors share is copied once.
        int Copy(int from, int count, int stride, int at = -1)
        {
            if (copies.TryGetValue(from, out int known)) return known;
            int data = at >= 0 ? at : Align(output.Count + TagView.ClassWordSize) + TagArray.HeaderSize;
            int end = checked(data + count * stride);
            if (end > output.Count) output.AddRange(new byte[end - output.Count]);
            var span = CollectionsMarshal.AsSpan(output);
            // The class word and the zero word are copied from the old header.
            TagArray.WriteHeader(span, data - TagArray.HeaderSize, count, 0);
            TagArray.CopyHeaderWords(current.Span, from - TagArray.HeaderSize, span, data - TagArray.HeaderSize);
            current.Span.Slice(from, count * stride).CopyTo(span[data..]);
            return copies[from] = data;
        }
        void Set(int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(CollectionsMarshal.AsSpan(output)[at..], value);
    }

    private static uint GroupHash(ReadOnlySpan<byte> table, int data, int index) =>
        TagView.At<SStatGroup>(table, data + index * GroupStride).GroupHash;

    private static int Align(int value) => (value + 15) & ~15;
    private static void I64(byte[] bytes, int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value);
}
