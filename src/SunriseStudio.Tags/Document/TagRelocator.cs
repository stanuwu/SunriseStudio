using System.Buffers.Binary;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Tags.Document;

/**
 * The one way a tag grows. Installed bytes never shift: a grown array moves to an aligned block at
 * the end, pointers into it or inside it are rebased, and the old rows stay unreferenced. A size
 * word at +0 that held the length follows it, except under AppendObject and AppendArray.
 */
public static class TagRelocator
{
    /**
     * Points `field` at a new array of `rows` placed at the end; the old array is left in place unreferenced.
     * @param first Receives the offset of the first new row.
     */
    public static byte[] ReplaceArray(ReadOnlySpan<byte> source, int field, uint elementClass, int stride, ReadOnlySpan<byte> rows, out int first)
    {
        if (stride <= 0 || rows.Length % stride != 0) throw new ArgumentException("Rows must be whole strides.", nameof(rows));
        if (field < 0 || field > source.Length - TagArray.FieldSize) throw new ArgumentOutOfRangeException(nameof(field));
        byte[] output = source.ToArray();
        first = AppendArray(ref output, field, elementClass, rows.Length / stride, rows.Length);
        rows.CopyTo(output.AsSpan(first));
        TagView.CarrySizeWord(source, output);
        return output;
    }

    /**
     * Grows the tag by a class word and `length` zero bytes; the bytes start at the first
     * `alignment`-aligned offset with room for the class word after the old end.
     * @param alignment A power of two.
     * @return The offset of the first new byte after the class word.
     */
    public static int AppendObject(ref byte[] tag, uint classId, int length, int alignment = TagArray.HeaderAlignment)
    {
        if (alignment <= 0 || (alignment & (alignment - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(alignment));
        int at = TagView.BodyAfter(tag.Length, alignment);
        Array.Resize(ref tag, checked(at + length));
        TagView.WriteClassWord(tag, at, classId);
        return at;
    }

    /**
     * Appends `count` zeroed rows, `rowBytes` long in all, and points `field` at them. The old
     * rows stay in place, unreferenced; the size word is left alone.
     * @return The offset of the first row.
     */
    public static int AppendArray(ref byte[] tag, int field, uint elementClass, long count, int rowBytes, int alignment = TagArray.HeaderAlignment)
    {
        int header = AppendObject(ref tag, TagArray.Marker, checked(TagArray.HeaderSize + rowBytes), alignment);
        TagArray.WriteHeader(tag, header, count, elementClass);
        TagArray.WriteField(tag, field, count, header);
        return header + TagArray.HeaderSize;
    }

    /**
     * Appends rows to the array whose descriptor is at `field`. An array that ends the tag grows in
     * place; any other moves to the end. Pointers in the moved rows and pointers into them are rebased.
     * @param rowSources Per new row, the offset of the existing row it was copied from, or -1; a new
     *   row's pointers are rebased as copies of its source's. A row with no source must hold null pointers.
     * @param elementClass Element class for an array that is empty and has no header yet.
     */
    public static byte[] AppendRows(TagDocument document, RegistryIndex schema, int field, IReadOnlyList<byte[]> rows,
        IReadOnlyList<int>? rowSources = null, uint? elementClass = null)
    {
        if (rowSources is not null && rowSources.Count != rows.Count) throw new ArgumentException("Each new row needs a source entry.", nameof(rowSources));
        ReadOnlySpan<byte> source = document.Bytes;
        var array = new TagArray(source, field);
        uint element = array.HasHeader ? array.ElementClass : elementClass ?? throw new InvalidDataException("An empty array carries no element class; pass one.");
        int stride = schema.Class(element)?.Size ?? 0;
        if (stride <= 0) throw new InvalidDataException($"Element class 0x{element:X8} has no size in the registry.");
        if (rows.Any(x => x.Length != stride)) throw new ArgumentException("Each row must be one stride long.", nameof(rows));
        var rowPointers = PointerOffsets(schema, element, 0, 0).ToArray();
        for (int index = 0; index < rows.Count; index++)
            if ((rowSources?[index] ?? -1) < 0 && rowPointers.Any(offset => TagRead.I64(rows[index], offset) != 0))
                throw new ArgumentException("A row that holds pointers needs the row it was copied from.", nameof(rows));

        bool hasHeader = array.HasHeader;
        int oldCount = (int)array.Count, oldHeader = hasHeader ? array.HeaderOffset : -1;
        int oldData = oldHeader + TagArray.HeaderSize, oldEnd = hasHeader ? oldData + oldCount * stride : -1;
        bool inPlace = hasHeader && oldEnd == source.Length;
        int header = inPlace ? oldHeader : Header(source.Length), data = header + TagArray.HeaderSize, newCount = oldCount + rows.Count;
        byte[] output = new byte[checked(data + newCount * stride)];
        source.CopyTo(output);
        if (!inPlace && oldCount > 0) source.Slice(oldData, oldCount * stride).CopyTo(output.AsSpan(data));
        WriteArray(output, field, header, newCount, element);
        int delta = data - oldData;
        bool Moved(long target) => !inPlace && hasHeader && target >= oldHeader - TagView.ClassWordSize && target < oldEnd;

        // Every pointer outside the moved rows that pointed into them now points at the new copy.
        foreach (var site in document.Pointers)
        {
            int at = site.RelativeOffset;
            if (at >= oldData && at < oldEnd || at == field + TagArray.RelativeOffset) continue;
            long relative = TagRead.I64(source, at), target = at + relative;
            if (relative != 0 && Moved(target))
                BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(at), target + delta - at);
        }
        if (!inPlace)
            foreach (var site in document.Pointers.Where(x => x.RelativeOffset >= oldData && x.RelativeOffset < oldEnd))
                Rebase(source, output, site.RelativeOffset, site.RelativeOffset + delta, Moved, delta);
        for (int index = 0; index < rows.Count; index++)
        {
            int at = data + (oldCount + index) * stride;
            rows[index].CopyTo(output.AsSpan(at));
            int from = rowSources?[index] ?? -1;
            if (from < 0) continue;
            foreach (int offset in rowPointers) Rebase(source, output, from + offset, at + offset, Moved, delta);
        }
        TagView.CarrySizeWord(source, output);
        return output;
    }

    /**
     * Gives the array at `field` a new row list, written as a new array at the end. A row is a copy
     * of an existing row (its pointers rebased) or new bytes with null pointers. A pointer into a
     * kept row follows it; one into a dropped row keeps naming the old bytes, which stay in place.
     * @param rows Per new row: the index of the row it copies, or -1 with its bytes in `added`.
     * @param elementClass Element class for an array that has no header yet.
     */
    public static byte[] RewriteArray(TagDocument document, RegistryIndex schema, int field, IReadOnlyList<int> rows,
        IReadOnlyList<byte[]>? added = null, uint? elementClass = null)
    {
        ReadOnlySpan<byte> source = document.Bytes;
        var array = new TagArray(source, field);
        uint element = array.HasHeader ? array.ElementClass : elementClass ?? throw new InvalidDataException("An empty array carries no element class; pass one.");
        int stride = schema.Class(element)?.Size ?? 0;
        if (stride <= 0) throw new InvalidDataException($"Element class 0x{element:X8} has no size in the registry.");
        bool hasHeader = array.HasHeader;
        int oldCount = hasHeader ? (int)array.Count : 0, oldHeader = hasHeader ? array.HeaderOffset : -1, oldData = oldHeader + TagArray.HeaderSize;
        int newCount = rows.Count, addedIndex = 0;
        var rowPointers = PointerOffsets(schema, element, 0, 0).ToArray();
        int header = Header(source.Length), data = header + TagArray.HeaderSize;
        byte[] output = new byte[checked(data + newCount * stride)];
        source.CopyTo(output);
        WriteArray(output, field, header, newCount, element);

        // Where each kept old row lands; the first copy of a row is the one outside pointers follow.
        var landing = new Dictionary<int, int>();
        for (int index = 0; index < newCount; index++)
            if (rows[index] >= 0) landing.TryAdd(rows[index], data + index * stride);
        long Map(long target)
        {
            if (!hasHeader || target < oldHeader - TagView.ClassWordSize || target >= oldData + (long)oldCount * stride) return target;
            if (target < oldData) return target - oldHeader + header;
            int row = (int)((target - oldData) / stride);
            return landing.TryGetValue(row, out int at) ? at + (target - oldData) % stride : target;
        }

        for (int index = 0; index < newCount; index++)
        {
            int at = data + index * stride, from = rows[index];
            if (from >= 0)
            {
                if (from >= oldCount) throw new ArgumentOutOfRangeException(nameof(rows), $"Row {from} does not exist.");
                source.Slice(oldData + from * stride, stride).CopyTo(output.AsSpan(at));
                foreach (int offset in rowPointers)
                {
                    long relative = TagRead.I64(source, oldData + from * stride + offset);
                    long target = relative == 0 ? 0 : Map(oldData + from * stride + offset + relative);
                    BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(at + offset), relative == 0 ? 0 : target - (at + offset));
                }
                continue;
            }
            byte[] bytes = added is not null && addedIndex < added.Count ? added[addedIndex++]
                : throw new ArgumentException("Each new row needs its bytes.", nameof(added));
            if (bytes.Length != stride) throw new ArgumentException("Each row must be one stride long.", nameof(added));
            if (rowPointers.Any(offset => TagRead.I64(bytes, offset) != 0))
                throw new ArgumentException("A new row must hold null pointers.", nameof(added));
            bytes.CopyTo(output.AsSpan(at));
        }
        foreach (var site in document.Pointers)
        {
            int at = site.RelativeOffset;
            long relative = TagRead.I64(source, at);
            // The old rows stay as they were; their copies were rebased above.
            if (relative == 0 || at == field + TagArray.RelativeOffset || (hasHeader && at >= oldData && at < oldData + (long)oldCount * stride)) continue;
            long target = Map(at + relative);
            if (target != at + relative) BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(at), target - at);
        }
        TagView.CarrySizeWord(source, output);
        return output;
    }

    // GrowArrays places each block on a 16-byte boundary.
    private const int BlockAlignment = 16;

    /** A typed block's array header offset; the marker is the 4 bytes before it. */
    public const int TypedBlockHeader = 16;

    /** A GrowArrays block holding one typed array: count, 8 class bytes, then rows. */
    public static byte[] TypedBlock(long count, ReadOnlySpan<byte> classData, ReadOnlySpan<byte> rows)
    {
        if (classData.Length != 8) throw new ArgumentException("Array class data is 8 bytes.", nameof(classData));
        byte[] block = new byte[TypedBlockHeader + TagArray.HeaderSize + rows.Length];
        TagArray.WriteHeader(block, TypedBlockHeader, count, 0);
        classData.CopyTo(block.AsSpan(TypedBlockHeader + TagArray.ElementClassOffset));
        rows.CopyTo(block.AsSpan(TypedBlockHeader + TagArray.HeaderSize));
        return block;
    }

    /**
     * Appends rows to typed arrays found by offset; each array moves at most once and installed bytes
     * never shift. Arrays that end the tag back to back are rewritten in place as one run.
     * @param appends Arrays to grow; one with no rows moves only when it is part of that run.
     * @param blocks New bytes placed 16-byte aligned before the rewritten arrays.
     * @param stockLength Installed tag length; past it, an array that ends the tag grows in place.
     * @return The tag and the start offset of each block, in order.
     */
    public static (byte[] Tag, int[] Blocks) GrowArrays(ReadOnlyMemory<byte> tag, IReadOnlyList<ArrayGrowth> appends,
        IReadOnlyList<byte[]>? blocks = null, int stockLength = int.MaxValue)
    {
        blocks ??= [];
        if (appends.Select(append => append.Field).Distinct().Count() != appends.Count)
            throw new ArgumentException("Each array may appear once.", nameof(appends));
        var found = appends.Select((append, index) => Grown.Read(tag.Span, append) with { Index = index }).ToArray();
        // The tail: arrays of this call that end the tag back to back, laid out as below.
        var tail = new List<int>();
        for (int boundary = tag.Length, next = 0; next >= 0;)
        {
            next = Array.FindIndex(found, array => !tail.Contains(array.Index) && (tail.Count == 0
                ? array.End == boundary : NextStart(array.End) == boundary && Zero(tag.Span[array.End..boundary])));
            if (next < 0) break;
            var candidate = found[next];
            if (candidate.Start < stockLength)
            {
                // An installed array keeps its header and grows over Studio's bytes only.
                if (blocks.Count == 0 && (tail.Count == 0 || candidate.End >= stockLength)) tail.Insert(0, next);
                break;
            }
            tail.Insert(0, next);
            boundary = candidate.Start;
        }
        while (tail.Count > 0 && appends[tail[0]].Rows.Count == 0) tail.RemoveAt(0);
        var order = tail.Concat(found.Where(array => appends[array.Index].Rows.Count > 0 && !tail.Contains(array.Index))
            .Select(array => array.Index)).ToArray();
        if (order.Length == 0 && blocks.Count == 0) return (tag.ToArray(), []);
        int cut = tail.Count > 0 ? found[tail[0]].Start : tag.Length;
        if (appends.Any(append => append.Field + TagArray.FieldSize > cut))
            throw new NotSupportedException("An array descriptor lies inside the rewritten end of the table.");

        int position = cut;
        int[] blockStarts = new int[blocks.Count];
        for (int index = 0; index < blocks.Count; index++)
        {
            blockStarts[index] = checked((position + BlockAlignment - 1) & ~(BlockAlignment - 1));
            position = checked(blockStarts[index] + blocks[index].Length);
        }
        int[] headers = new int[order.Length];
        for (int index = 0; index < order.Length; index++)
        {
            var array = found[order[index]];
            // The first rewritten array keeps its header so a lone array at the end grows in place.
            headers[index] = index == 0 && tail.Count > 0 && blocks.Count == 0 ? array.Header : Header(position);
            position = checked(headers[index] + TagArray.HeaderSize + array.NewCount * appends[order[index]].Stride);
        }
        var source = tag.Span;
        byte[] output = new byte[position];
        source[..cut].CopyTo(output);
        for (int index = 0; index < blocks.Count; index++) blocks[index].CopyTo(output, blockStarts[index]);
        for (int index = 0; index < order.Length; index++)
        {
            var array = found[order[index]];
            var append = appends[order[index]];
            int header = headers[index], data = header + TagArray.HeaderSize, stride = append.Stride;
            // The class word and the zero word are copied, not rewritten.
            TagArray.WriteHeader(output, header, array.NewCount, 0);
            TagArray.CopyHeaderWords(source, array.Header, output, header);
            source.Slice(array.Data, array.Count * stride).CopyTo(output.AsSpan(data));
            for (int row = 0; row < array.Count; row++)
                RebaseRow(output, tag.Length, cut, append.RowArrays, array.Data + row * stride, data + row * stride);
            for (int row = 0; row < append.Rows.Count; row++)
            {
                int at = data + (array.Count + row) * stride;
                append.Rows[row].CopyTo(output, at);
                int from = append.RowSources?[row] ?? -1;
                if (from >= 0) RebaseRow(output, tag.Length, cut, append.RowArrays, from, at);
            }
            TagArray.WriteField(output, append.Field, array.NewCount, header);
        }
        TagView.CarrySizeWord(source, output);
        return (output, blockStarts);

        static int NextStart(int end) => checked(Header(end) - TagView.ClassWordSize);
        static bool Zero(ReadOnlySpan<byte> bytes) => !bytes.ContainsAnyExcept((byte)0);
    }

    /** One array of a GrowArrays call, read and checked before any byte is written. */
    private sealed record Grown(int Index, int Count, int Data, int Header, int Start, int End, int NewCount)
    {
        public static Grown Read(ReadOnlySpan<byte> tag, ArrayGrowth append)
        {
            if (append.Stride <= 0 || append.Rows.Any(row => row.Length != append.Stride))
                throw new ArgumentException("Each row must be one stride long.", nameof(append));
            if (append.RowSources is { } sources && sources.Count != append.Rows.Count)
                throw new ArgumentException("Each new row needs a source entry.", nameof(append));
            for (int row = 0; row < append.Rows.Count; row++)
                if ((append.RowSources?[row] ?? -1) < 0 && append.RowArrays.Any(offset =>
                    TagRead.I64(append.Rows[row], offset + TagArray.RelativeOffset) != 0))
                    throw new ArgumentException("A row that owns arrays needs the row it was copied from.", nameof(append));
            var rows = TagArray.Checked(tag, append.Field, stride: append.Stride);
            if (rows.Count == 0) throw new InvalidDataException("An empty array carries no row type to extend.");
            int count = checked(rows.Count + append.Rows.Count);
            if (count > TagArray.MaximumCheckedCount)
                throw new InvalidDataException("The table has reached the editor's row limit.");
            int header = rows.Data - TagArray.HeaderSize;
            return new(-1, rows.Count, rows.Data, header, header - TagView.ClassWordSize,
                checked(rows.Data + rows.Count * append.Stride), count);
        }
    }

    /**
     * A row copied from `from` to `to` keeps each array target; an offset of 0 means no array.
     * A target inside the rewritten end of the tag would be overwritten, so it is refused.
     */
    private static void RebaseRow(byte[] output, int length, int cut, IReadOnlyList<int> rowArrays, int from, int to)
    {
        foreach (int offset in rowArrays)
        {
            int at = to + offset + TagArray.RelativeOffset;
            long relative = TagRead.I64(output, at);
            if (relative == 0) continue;
            long target = checked(from + offset + TagArray.RelativeOffset + relative);
            if (target >= cut && target < length)
                throw new NotSupportedException("A moved row owns an array inside the rewritten end of the table.");
            BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(at), target - at);
        }
    }

    /** A pointer copied from `from` to `to` keeps its target, or follows it when the target moved. */
    private static void Rebase(ReadOnlySpan<byte> source, byte[] output, int from, int to, Func<long, bool> moved, int delta)
    {
        long relative = TagRead.I64(source, from);
        if (relative == 0) { BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(to), 0); return; }
        long target = from + relative;
        if (moved(target)) target += delta;
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(to), target - to);
    }

    /** Offsets of the relative words of every pointer in a class, nested structs included. */
    public static IEnumerable<int> PointerOffsets(RegistryIndex schema, uint classId, int baseOffset, int depth)
    {
        if (depth > TagDocument.MaximumDepth || schema.Class(classId) is not { } structure) yield break;
        foreach (var field in structure.Fields)
        {
            switch (schema.TypeName(field))
            {
                case "array": yield return baseOffset + field.Offset + TagArray.RelativeOffset; break;
                case "relptr" or "string_ptr" or "inline_string" or "blockptr": yield return baseOffset + field.Offset; break;
                case "struct" when field.Struct is { } nested:
                    foreach (int offset in PointerOffsets(schema, nested, baseOffset + field.Offset, depth + 1)) yield return offset;
                    break;
                case "inline_array" when field.Struct is { } element && field.Count is > 0 and int count && field.Length is > 0 and int length:
                    for (int index = 0; index < count; index++)
                        foreach (int offset in PointerOffsets(schema, element, baseOffset + field.Offset + index * (length / count), depth + 1))
                            yield return offset;
                    break;
            }
        }
    }

    /** The first aligned array header with room for the marker after `length`. */
    private static int Header(int length) => TagView.BodyAfter(length, TagArray.HeaderAlignment);

    private static void WriteArray(byte[] output, int field, int header, long count, uint elementClass)
    {
        TagArray.WriteField(output, field, count, header);
        TagArray.WriteHeader(output, header, count, elementClass);
    }
}

/** Rows to append to the typed array whose field is at Field; each row is one stride long. */
public sealed record ArrayGrowth(int Field, int Stride, IReadOnlyList<byte[]> Rows)
{
    /** Row offsets of the array fields each row owns; moved rows keep their targets. */
    public IReadOnlyList<int> RowArrays { get; init; } = [];
    /** Per new row, the offset of the row it was copied from, or -1 when it owns no array. */
    public IReadOnlyList<int>? RowSources { get; init; }
}
