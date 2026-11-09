using System.Buffers.Binary;

namespace SunriseStudio.Formats;

/** Item art rows share the item index and refer to immutable field and handle pools. */
public static class InvestmentItemArt
{
    public const uint TableTag = 0x81A28B23, ClassId = 0x808056CC;
    private const int HandlesField = 8, IntegersField = 24, FieldsField = 40, ItemsField = 56, ListsField = 72;
    private const int ItemStride = 16, FieldStride = 8, ListStride = 8;
    private const uint HandleClass = 0x80800014, IntegerClass = 0x80800007, FieldClass = 0x808056D6, ItemClass = 0x808056D1,
        ListClass = 0x808056D5;
    // The five pools in descriptor order; none of their rows holds a pointer. The last holds one {first field, field count}
    // row per socket-entry list, read by list index (Parhelion append_dense_list_presentation).
    private static readonly (int Field, uint Class, int Stride)[] Pools =
        [(HandlesField, HandleClass, 4), (IntegersField, IntegerClass, 4), (FieldsField, FieldClass, FieldStride),
         (ItemsField, ItemClass, ItemStride), (ListsField, ListClass, ListStride)];

    /** Number of item rows in the table. */
    public static int Count(ReadOnlyMemory<byte> table) => new TagReader(table).Array(ItemsField, ItemClass, ItemStride).Count;

    /** Type-five fields name sandbox displays whose text and icons the UI must load. */
    public static IReadOnlyList<ushort> ReadSandboxPerks(ReadOnlyMemory<byte> table, int itemIndex)
    {
        var reader = new TagReader(table);
        var fields = Fields(reader, ItemOffset(reader, itemIndex));
        var result = new List<ushort>();
        for (int index = 0; index < fields.Count; index++)
        {
            int at = fields.Data + index * FieldStride;
            if (reader.U8(at + 4) != 5) continue;
            var integers = reader.Array(IntegersField, IntegerClass, 4);
            int first = reader.I32(at), count = reader.U8(at + 5);
            if (first < 0 || first > integers.Count - count)
                throw new InvalidDataException("The item art perks leave the integer pool.");
            for (int element = 0; element < count; element++)
            {
                uint value = reader.U32(integers.Data + (first + element) * 4);
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
        var reader = new TagReader(table);
        var fields = Fields(reader, ItemOffset(reader, itemIndex));
        var allFields = reader.Array(FieldsField, FieldClass, FieldStride);
        var replacement = new List<byte[]>();
        for (int index = 0; index < fields.Count; index++)
        {
            int at = fields.Data + index * FieldStride;
            if (reader.U8(at + 4) != 5) replacement.Add(table.Span.Slice(at, FieldStride).ToArray());
        }
        var values = new List<byte[]>();
        if (perks.Count > 0)
        {
            var integers = reader.Array(IntegersField, IntegerClass, 4);
            byte[] field = new byte[FieldStride];
            BinaryPrimitives.WriteInt32LittleEndian(field, integers.Count);
            field[4] = 5; field[5] = (byte)perks.Count;
            replacement.Add(field);
            foreach (ushort perk in perks)
            {
                byte[] value = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(value, perk);
                values.Add(value);
            }
        }
        if (replacement.Count > byte.MaxValue)
            throw new InvalidDataException("The item has too many art fields.");
        byte[] output = Append(table, integers: values, fields: replacement);
        int target = ItemOffset(new TagReader(output), itemIndex);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(target + 8), allFields.Count);
        output[target + 12] = (byte)replacement.Count;
        return output;
    }

    /** A cloned item's UI dependencies follow its authored sandbox perk array. */
    public static byte[] SynchronizeSandboxPerks(ReadOnlyMemory<byte> table, int itemIndex,
        ReadOnlyMemory<byte> definition)
    {
        var reader = new TagReader(definition);
        int block = reader.Relative(112);
        if (block < 0) return SetSandboxPerks(table, itemIndex, []);
        if (block < 4 || reader.U32(block - 4) != 0x808077B9)
            throw new InvalidDataException("The item stats block has an unsupported layout.");
        var rows = reader.Array(block + 16, 0x808077BC, 24);
        var perks = Enumerable.Range(0, rows.Count).Select(index => reader.U16(rows.Data + index * 24))
            .Distinct().ToArray();
        return SetSandboxPerks(table, itemIndex, perks);
    }

    /** New item indices must grow this table in the same order as the item definitions. */
    public static byte[] CloneRow(ReadOnlyMemory<byte> table, int sourceItem, int targetItem)
    {
        var reader = new TagReader(table);
        var items = reader.Array(ItemsField, ItemClass, ItemStride);
        if (targetItem != items.Count || targetItem >= InvestmentItemClone.MaximumIndexRows)
            throw new InvalidDataException("Item art rows must follow the item index without gaps.");
        int row = ItemOffset(reader, sourceItem);
        _ = Fields(reader, row);
        return Append(table, items: [table.Span.Slice(row, ItemStride).ToArray()]);
    }

    /** Number of per-list presentation rows (one per socket-entry list, by list index). */
    public static int ListCount(ReadOnlyMemory<byte> table) => new TagReader(table).Array(ListsField, ListClass, ListStride).Count;

    /** The raw {first field, field count} of one list's presentation row. */
    public static (int First, int Count) ReadListRow(ReadOnlyMemory<byte> table, int list)
    {
        var reader = new TagReader(table);
        var lists = reader.Array(ListsField, ListClass, ListStride);
        if (list < 0 || list >= lists.Count) throw new InvalidDataException($"List {list} has no presentation row.");
        return (reader.I32(lists.Data + list * ListStride), reader.I32(lists.Data + list * ListStride + 4));
    }

    /**
     * Appends presentation rows for new socket-entry lists, each a copy of its base list's row (sharing its fields), as
     * Parhelion's append_dense_list_presentation does. Rows must follow the existing ones without gaps.
     */
    public static byte[] AppendListRows(ReadOnlyMemory<byte> table, IReadOnlyList<(int List, int Base)> rows)
    {
        var reader = new TagReader(table);
        var lists = reader.Array(ListsField, ListClass, ListStride);
        var fields = reader.Array(FieldsField, FieldClass, FieldStride);
        var copies = new List<byte[]>();
        for (int index = 0; index < rows.Count; index++)
        {
            var (list, source) = rows[index];
            if (list != lists.Count + index)
                throw new InvalidDataException($"List row {list} must follow the {lists.Count + index} existing rows.");
            if (source < 0 || source >= lists.Count)
                throw new InvalidDataException($"Base list {source} has no presentation row.");
            int at = lists.Data + source * ListStride;
            int first = reader.I32(at), count = reader.I32(at + 4);
            if (first < 0 || count < 0 || first > fields.Count - count)
                throw new InvalidDataException($"Base list {source}'s row leaves the field pool.");
            copies.Add(table.Span.Slice(at, ListStride).ToArray());
        }
        return copies.Count == 0 ? table.ToArray() : Append(table, lists: copies);
    }

    /** The handles of one list's handle fields (type 1 = node icons, type 9 = text banks), in field order. */
    public static IReadOnlyList<(byte Type, uint Handle)> ReadListHandles(ReadOnlyMemory<byte> table, int list)
    {
        var reader = new TagReader(table);
        var (first, count) = ReadListRow(table, list);
        var fields = reader.Array(FieldsField, FieldClass, FieldStride);
        var handles = reader.Array(HandlesField, HandleClass, 4);
        if (first < 0 || count < 0 || first > fields.Count - count)
            throw new InvalidDataException($"List {list}'s row leaves the field pool.");
        var result = new List<(byte, uint)>();
        for (int index = 0; index < count; index++)
        {
            int at = fields.Data + (first + index) * FieldStride;
            byte type = reader.U8(at + 4);
            if (type is not (1 or 9)) continue;
            int start = reader.I32(at), length = reader.U8(at + 5);
            if (start < 0 || start > handles.Count - length) throw new InvalidDataException($"List {list}'s handles leave their pool.");
            for (int element = 0; element < length; element++) result.Add((type, reader.U32(handles.Data + (start + element) * 4)));
        }
        return result;
    }

    /**
     * Gives one added list its own field list: every field is copied, and each handle field (type 1 node icons, type 9
     * text banks) that names a mapped handle gets its own handle copies with the map applied. Other lists, including the
     * base list whose fields this list shared, keep theirs. Shipped rows (list &lt; installedLists) are refused.
     */
    public static byte[] ForkListFields(ReadOnlyMemory<byte> table, int installedLists, int list, IReadOnlyDictionary<uint, uint> handleMap)
    {
        if (list < installedLists)
            throw new InvalidDataException($"List {list} is a shipped list (the installed table has {installedLists}); only added lists can change.");
        var reader = new TagReader(table);
        var lists = reader.Array(ListsField, ListClass, ListStride);
        if (list >= lists.Count) throw new InvalidDataException($"List {list} has no presentation row.");
        int row = lists.Data + list * ListStride;
        int first = reader.I32(row), count = reader.I32(row + 4);
        var fields = reader.Array(FieldsField, FieldClass, FieldStride);
        var handles = reader.Array(HandlesField, HandleClass, 4);
        if (first < 0 || count < 0 || count > byte.MaxValue || first > fields.Count - count)
            throw new InvalidDataException($"List {list}'s row leaves the field pool.");
        var newHandles = new List<byte[]>();
        var newFields = new List<byte[]>();
        int mapped = 0;
        for (int index = 0; index < count; index++)
        {
            int at = fields.Data + (first + index) * FieldStride;
            byte[] field = table.Span.Slice(at, FieldStride).ToArray();
            if (field[4] is 1 or 9)
            {
                int start = BinaryPrimitives.ReadInt32LittleEndian(field), length = field[5];
                if (start < 0 || start > handles.Count - length) throw new InvalidDataException($"List {list}'s handles leave their pool.");
                var values = Enumerable.Range(0, length).Select(element => reader.U32(handles.Data + (start + element) * 4)).ToArray();
                if (values.Any(handleMap.ContainsKey))
                {
                    BinaryPrimitives.WriteInt32LittleEndian(field, handles.Count + newHandles.Count);
                    foreach (uint value in values)
                    {
                        uint next = handleMap.TryGetValue(value, out uint swapped) ? swapped : value;
                        if (next != value) mapped++;
                        byte[] handle = new byte[4];
                        BinaryPrimitives.WriteUInt32LittleEndian(handle, next);
                        newHandles.Add(handle);
                    }
                }
            }
            newFields.Add(field);
        }
        if (mapped == 0) throw new InvalidDataException($"None of the given handles is in list {list}'s icon or text fields; nothing changed.");
        byte[] output = Append(table, handles: newHandles, fields: newFields);
        var after = new TagReader(output).Array(ListsField, ListClass, ListStride);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(after.Data + list * ListStride), fields.Count);
        return output;
    }

    /** A static item icon is one type-one field containing one container handle. */
    public static uint ReadPrimaryIcon(ReadOnlyMemory<byte> table, int itemIndex)
    {
        var reader = new TagReader(table);
        var fields = Fields(reader, ItemOffset(reader, itemIndex));
        int primary = PrimaryField(reader, fields);
        var handles = reader.Array(HandlesField, HandleClass, 4);
        int first = reader.I32(primary);
        if (first < 0 || first >= handles.Count)
            throw new InvalidDataException("The item art icon leaves the handle pool.");
        return reader.U32(handles.Data + first * 4);
    }

    /** Changing one item copies its field list so shared source fields keep their old targets. */
    public static byte[] SetPrimaryIcon(ReadOnlyMemory<byte> table, int itemIndex, uint containerTag)
    {
        if (containerTag is 0 or uint.MaxValue)
            throw new InvalidDataException("An item icon needs a container tag.");
        if (ReadPrimaryIcon(table, itemIndex) == containerTag) return table.ToArray();
        var reader = new TagReader(table);
        int row = ItemOffset(reader, itemIndex);
        var fields = Fields(reader, row);
        int primary = PrimaryField(reader, fields);
        var handles = reader.Array(HandlesField, HandleClass, 4);
        var allFields = reader.Array(FieldsField, FieldClass, FieldStride);
        byte[] handle = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(handle, containerTag);
        var copies = new List<byte[]>();
        for (int index = 0; index < fields.Count; index++)
        {
            int at = fields.Data + index * FieldStride;
            byte[] field = table.Span.Slice(at, FieldStride).ToArray();
            if (at == primary) BinaryPrimitives.WriteInt32LittleEndian(field, handles.Count);
            copies.Add(field);
        }
        byte[] output = Append(table, handles: [handle], fields: copies);
        int target = ItemOffset(new TagReader(output), itemIndex);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(target + 8), allFields.Count);
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
        var stock = new TagReader(original);
        var edited = new TagReader(current);
        var before = Pools.Select(pool => stock.Array(pool.Field, pool.Class, pool.Stride)).ToArray();
        var after = Pools.Select(pool => edited.Array(pool.Field, pool.Class, pool.Stride)).ToArray();
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
        var rebuilt = new TagReader(output);
        for (int index = 0; index < Pools.Length; index++)
        {
            var pool = Pools[index];
            current.Span.Slice(after[index].Data, before[index].Count * pool.Stride)
                .CopyTo(output.AsSpan(rebuilt.Array(pool.Field, pool.Class, pool.Stride).Data));
        }
        return output;
    }

    /** Grows several pools in one step, each moved at most once; Rebuild drops old copies. */
    private static byte[] Append(ReadOnlyMemory<byte> table, IReadOnlyList<byte[]>? handles = null,
        IReadOnlyList<byte[]>? integers = null, IReadOnlyList<byte[]>? fields = null, IReadOnlyList<byte[]>? items = null,
        IReadOnlyList<byte[]>? lists = null)
    {
        IReadOnlyList<byte[]>[] rows = [handles ?? [], integers ?? [], fields ?? [], items ?? [], lists ?? []];
        return InvestmentItemClone.AppendRows(table, Pools.Select((pool, index) => new RowAppend(pool.Field, pool.Stride, rows[index]))
            .Where(append => append.Rows.Count > 0).ToArray()).Table;
    }

    private static int ItemOffset(TagReader reader, int index)
    {
        var rows = reader.Array(ItemsField, ItemClass, ItemStride);
        if (index < 0 || index >= rows.Count)
            throw new InvalidDataException("The item has no native art row.");
        return rows.Data + index * ItemStride;
    }

    private static (int Count, int Data) Fields(TagReader reader, int row)
    {
        var fields = reader.Array(FieldsField, FieldClass, FieldStride);
        int first = reader.I32(row + 8), count = reader.U8(row + 12);
        if (first < 0 || first > fields.Count - count)
            throw new InvalidDataException("The item art fields leave their pool.");
        return (count, fields.Data + first * FieldStride);
    }

    private static int PrimaryField(TagReader reader, (int Count, int Data) fields)
    {
        int result = -1;
        for (int index = 0; index < fields.Count; index++)
        {
            int at = fields.Data + index * FieldStride;
            if (reader.U8(at + 4) != 1) continue;
            if (result >= 0 || reader.U8(at + 5) != 1)
                throw new NotSupportedException("Custom item icons need one primary art container.");
            result = at;
        }
        return result >= 0 ? result : throw new NotSupportedException("The item has no primary art container.");
    }
}
