using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/** Item art rows share the item index and refer to immutable field and handle pools. */
public static class InvestmentItemArt
{
    public const uint TableTag = 0x81A28B23, ClassId = SItemArtTable.Class;
    private static readonly int ItemStride = Unsafe.SizeOf<SItemArtRow>(), FieldStride = Unsafe.SizeOf<SItemArtField>();
    // The four pools in descriptor order; none of their rows holds a pointer.
    private static readonly (int Field, uint Class, int Stride)[] Pools = ReadPools();
    // Indexes into Pools.
    private enum PoolId { Handles, Integers, Fields, Items }
    private static readonly int StatsBlockPointer = TagView.FieldOffset<ItemDefinition>("StatsItemBlock"),
        PerkRowsField = TagView.FieldOffset<SItemSandboxPerkBlock>("ItemPerks");

    public static int Count(ReadOnlyMemory<byte> table) => Pool(table.Span, PoolId.Items).Count;

    /** Type-five fields name sandbox displays whose text and icons the UI must load. */
    public static IReadOnlyList<ushort> ReadSandboxPerks(ReadOnlyMemory<byte> table, int itemIndex)
    {
        var span = table.Span;
        var fields = Fields(span, ItemOffset(span, itemIndex));
        var result = new List<ushort>();
        for (int index = 0; index < fields.Count; index++)
        {
            ref readonly var field = ref TagView.At<SItemArtField>(span, fields.Data + index * FieldStride);
            if (field.FieldType != 5) continue;
            var integers = Pool(span, PoolId.Integers);
            int first = field.FirstValue, count = field.ValueCount;
            if (first < 0 || first > integers.Count - count)
                throw new InvalidDataException("The item art perks leave the integer pool.");
            for (int element = 0; element < count; element++)
            {
                uint value = unchecked((uint)TagView.At<SI32Element>(span,
                    integers.Data + (first + element) * Unsafe.SizeOf<SI32Element>()).Value);
                if (value >= InvestmentItemClone.MaximumIndexRows)
                    throw new InvalidDataException("An item art perk index is outside the native range.");
                result.Add((ushort)value);
            }
        }
        return result;
    }

    /** Perk dependencies copy shared pools before changing an item's field list. */
    public static byte[] SetSandboxPerks(ReadOnlyMemory<byte> table, int itemIndex,
        IReadOnlyList<ushort> perks)
    {
        if (perks.Count > byte.MaxValue || perks.Any(index => index >= InvestmentItemClone.MaximumIndexRows))
            throw new InvalidDataException("The item art perk list exceeds the native range.");
        if (ReadSandboxPerks(table, itemIndex).SequenceEqual(perks)) return table.ToArray();
        var span = table.Span;
        var fields = Fields(span, ItemOffset(span, itemIndex));
        var allFields = Pool(span, PoolId.Fields);
        var replacement = new List<byte[]>();
        for (int index = 0; index < fields.Count; index++)
        {
            int at = fields.Data + index * FieldStride;
            if (TagView.At<SItemArtField>(span, at).FieldType != 5) replacement.Add(span.Slice(at, FieldStride).ToArray());
        }
        var values = new List<byte[]>();
        if (perks.Count > 0)
        {
            var integers = Pool(span, PoolId.Integers);
            byte[] field = new byte[FieldStride];
            ref var added = ref TagView.Edit<SItemArtField>(field);
            added.FirstValue = integers.Count;
            added.FieldType = 5; added.ValueCount = (byte)perks.Count;
            replacement.Add(field);
            foreach (ushort perk in perks)
            {
                byte[] value = new byte[Unsafe.SizeOf<SI32Element>()];
                TagView.Edit<SI32Element>(value).Value = perk;
                values.Add(value);
            }
        }
        if (replacement.Count > byte.MaxValue)
            throw new InvalidDataException("The item has too many art fields.");
        byte[] output = Append(table, integers: values, fields: replacement);
        ref var item = ref TagView.Edit<SItemArtRow>(output, ItemOffset(output, itemIndex));
        item.FirstField = allFields.Count;
        item.FieldCount = (byte)replacement.Count;
        return output;
    }

    /** A cloned item's UI dependencies follow its authored sandbox perk array. */
    public static byte[] SynchronizeSandboxPerks(ReadOnlyMemory<byte> table, int itemIndex,
        ReadOnlyMemory<byte> definition)
    {
        var span = definition.Span;
        int block = TagPointer.Checked(span, StatsBlockPointer);
        if (block < 0) return SetSandboxPerks(table, itemIndex, []);
        if (block < TagView.ClassWordSize || new TagPointer(span, StatsBlockPointer).TargetClass != SItemSandboxPerkBlock.Class)
            throw new InvalidDataException("The item stats block has an unsupported layout.");
        int stride = Unsafe.SizeOf<ItemSandboxPerk>();
        var rows = TagArray.Checked(span, block + PerkRowsField, ItemSandboxPerk.Class, stride);
        var perks = new ushort[rows.Count];
        for (int index = 0; index < rows.Count; index++)
            perks[index] = TagView.At<ItemSandboxPerk>(span, rows.Data + index * stride).SandboxPerkIndex;
        return SetSandboxPerks(table, itemIndex, perks.Distinct().ToArray());
    }

    /** New item indices must grow this table in the same order as the item definitions. */
    public static byte[] CloneRow(ReadOnlyMemory<byte> table, int sourceItem, int targetItem)
    {
        var span = table.Span;
        var items = Pool(span, PoolId.Items);
        if (targetItem != items.Count || targetItem >= InvestmentItemClone.MaximumIndexRows)
            throw new InvalidDataException("Item art rows must follow the item index without gaps.");
        int row = ItemOffset(span, sourceItem);
        _ = Fields(span, row);
        return Append(table, items: [span.Slice(row, ItemStride).ToArray()]);
    }

    /** A static item icon is one type-one field containing one container handle. */
    public static uint ReadPrimaryIcon(ReadOnlyMemory<byte> table, int itemIndex)
    {
        var span = table.Span;
        var fields = Fields(span, ItemOffset(span, itemIndex));
        int primary = PrimaryField(span, fields);
        var handles = Pool(span, PoolId.Handles);
        int first = TagView.At<SItemArtField>(span, primary).FirstValue;
        if (first < 0 || first >= handles.Count)
            throw new InvalidDataException("The item art icon leaves the handle pool.");
        return TagView.At<TagReferenceElement>(span, handles.Data + first * Unsafe.SizeOf<TagReferenceElement>()).Tag;
    }

    /** Changing one item copies its field list so shared source fields keep their old targets. */
    public static byte[] SetPrimaryIcon(ReadOnlyMemory<byte> table, int itemIndex, uint containerTag)
    {
        if (containerTag is 0 or uint.MaxValue)
            throw new InvalidDataException("An item icon needs a container tag.");
        if (ReadPrimaryIcon(table, itemIndex) == containerTag) return table.ToArray();
        var span = table.Span;
        int row = ItemOffset(span, itemIndex);
        var fields = Fields(span, row);
        int primary = PrimaryField(span, fields);
        var handles = Pool(span, PoolId.Handles);
        var allFields = Pool(span, PoolId.Fields);
        byte[] handle = new byte[Unsafe.SizeOf<TagReferenceElement>()];
        TagView.Edit<TagReferenceElement>(handle).Tag = containerTag;
        var copies = new List<byte[]>();
        for (int index = 0; index < fields.Count; index++)
        {
            int at = fields.Data + index * FieldStride;
            byte[] field = span.Slice(at, FieldStride).ToArray();
            if (at == primary) TagView.Edit<SItemArtField>(field).FirstValue = handles.Count;
            copies.Add(field);
        }
        byte[] output = Append(table, handles: [handle], fields: copies);
        TagView.Edit<SItemArtRow>(output, ItemOffset(output, itemIndex)).FirstField = allFields.Count;
        return output;
    }

    /**
     * The installed table plus one copy of each pool that grew, at the end in field order.
     * Pools whose row count did not change are written back in place.
     * @param original The installed table.
     * @param current The edited table; every pool keeps its rows and row numbers.
     */
    public static byte[] Rebuild(ReadOnlyMemory<byte> original, ReadOnlyMemory<byte> current)
    {
        var before = new (int Count, int Data, uint Class)[Pools.Length];
        var after = new (int Count, int Data, uint Class)[Pools.Length];
        for (int index = 0; index < Pools.Length; index++) before[index] = Pool(original.Span, (PoolId)index);
        for (int index = 0; index < Pools.Length; index++) after[index] = Pool(current.Span, (PoolId)index);
        if (Enumerable.Range(0, Pools.Length).Any(index => after[index].Count < before[index].Count))
            throw new InvalidDataException("An item art pool lost rows.");
        InvestmentItemClone.RequireOnlyChanged(original.Span, current.Span,
            Pools.SelectMany(pool => InvestmentItemClone.ArraySpans(original, pool.Field, pool.Stride)),
            "The item art table changed outside its pools.");
        // Added rows join each pool once; installed rows then take their edited bytes where they live.
        var appends = new List<RowAppend>();
        for (int index = 0; index < Pools.Length; index++)
        {
            int stride = Pools[index].Stride, installed = before[index].Count;
            var added = Enumerable.Range(installed, after[index].Count - installed)
                .Select(row => current.Span.Slice(after[index].Data + row * stride, stride).ToArray()).ToArray();
            if (added.Length > 0) appends.Add(new(Pools[index].Field, stride, added));
        }
        byte[] output = InvestmentItemClone.AppendRows(original, appends, stockLength: original.Length).Table;
        for (int index = 0; index < Pools.Length; index++)
        {
            current.Span.Slice(after[index].Data, before[index].Count * Pools[index].Stride)
                .CopyTo(output.AsSpan(Pool(output, (PoolId)index).Data));
        }
        return output;
    }

    /** Grows several pools in one step, each moved at most once; Rebuild drops old copies. */
    private static byte[] Append(ReadOnlyMemory<byte> table, IReadOnlyList<byte[]>? handles = null,
        IReadOnlyList<byte[]>? integers = null, IReadOnlyList<byte[]>? fields = null, IReadOnlyList<byte[]>? items = null)
    {
        IReadOnlyList<byte[]>[] rows = [handles ?? [], integers ?? [], fields ?? [], items ?? []];
        return InvestmentItemClone.AppendRows(table, Pools.Select((pool, index) => new RowAppend(pool.Field, pool.Stride, rows[index]))
            .Where(append => append.Rows.Count > 0).ToArray()).Table;
    }

    private static (int Count, int Data, uint Class) Pool(ReadOnlySpan<byte> table, PoolId pool) =>
        TagArray.Checked(table, Pools[(int)pool].Field, Pools[(int)pool].Class, Pools[(int)pool].Stride);

    private static int ItemOffset(ReadOnlySpan<byte> table, int index)
    {
        var rows = Pool(table, PoolId.Items);
        if (index < 0 || index >= rows.Count)
            throw new InvalidDataException("The item has no native art row.");
        return rows.Data + index * ItemStride;
    }

    private static (int Count, int Data) Fields(ReadOnlySpan<byte> table, int row)
    {
        var fields = Pool(table, PoolId.Fields);
        ref readonly var item = ref TagView.At<SItemArtRow>(table, row);
        int first = item.FirstField, count = item.FieldCount;
        if (first < 0 || first > fields.Count - count)
            throw new InvalidDataException("The item art fields leave their pool.");
        return (count, fields.Data + first * FieldStride);
    }

    private static int PrimaryField(ReadOnlySpan<byte> table, (int Count, int Data) fields)
    {
        int result = -1;
        for (int index = 0; index < fields.Count; index++)
        {
            int at = fields.Data + index * FieldStride;
            ref readonly var field = ref TagView.At<SItemArtField>(table, at);
            if (field.FieldType != 1) continue;
            if (result >= 0 || field.ValueCount != 1)
                throw new NotSupportedException("Custom item icons need one primary art container.");
            result = at;
        }
        return result >= 0 ? result : throw new NotSupportedException("The item has no primary art container.");
    }

    /** The table root sits at offset 0, so field offsets are absolute. */
    private static (int Field, uint Class, int Stride)[] ReadPools() =>
    [
        (TagView.FieldOffset<SItemArtTable>("Handles"), TagReferenceElement.Class, Unsafe.SizeOf<TagReferenceElement>()),
        (TagView.FieldOffset<SItemArtTable>("UntypedIndices"), SI32Element.Class, Unsafe.SizeOf<SI32Element>()),
        (TagView.FieldOffset<SItemArtTable>("Fields"), SItemArtField.Class, Unsafe.SizeOf<SItemArtField>()),
        (TagView.FieldOffset<SItemArtTable>("Items"), SItemArtRow.Class, Unsafe.SizeOf<SItemArtRow>())
    ];
}
