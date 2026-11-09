using System.Buffers.Binary;

namespace SunriseStudio.Formats;

/** Item display rows pair by index with the item's sandbox perk rows. */
public static class InvestmentPerkDisplayEdits
{
    private const uint BlockClass = 0x80805D08, RowClass = 0x80805D0A;
    // Every typed array header carries this class word; a row's +8 array holds unlock expression steps.
    private const uint ArrayMarker = 0x80809FBD, ExpressionClass = 0x80807D31;
    private const int BlockPointer = 96, Stride = 32;

    /** Number of perk display rows. */
    public static int ReadCount(ReadOnlyMemory<byte> display) => Read(display).Count;

    /** Fresh perks have no inherited text override, visibility expression or activation-display mode. */
    public static byte[] Reset(ReadOnlyMemory<byte> display, int perkCount)
    {
        if (perkCount < 0 || perkCount > 4096)
            throw new InvalidDataException("The item has too many perk display rows.");
        var rows = Read(display);
        if (rows.Field < 0)
        {
            if (perkCount == 0) return display.ToArray();
            throw new NotSupportedException("The item needs an existing native perk display block.");
        }
        byte[] empty = new byte[Stride];
        BinaryPrimitives.WriteUInt16LittleEndian(empty, ushort.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(empty.AsSpan(4), InvestmentItemClone.EmptyStringHash);
        if (rows.Count == perkCount && Enumerable.Range(0, rows.Count)
            .All(index => display.Span.Slice(rows.Data + index * Stride, Stride).SequenceEqual(empty)))
            return display.ToArray();
        if (perkCount == 0)
        {
            byte[] cleared = display.ToArray();
            cleared.AsSpan(rows.Field, 16).Clear();
            return cleared;
        }
        int header = checked((display.Length + 19) & ~15);
        byte[] output = new byte[checked(header + 16 + perkCount * Stride)];
        display.Span.CopyTo(output);
        TagArrayRelocation.WriteHeader(output, header, perkCount, RowClass);
        for (int index = 0; index < perkCount; index++) empty.CopyTo(output, header + 16 + index * Stride);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(rows.Field), perkCount);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(rows.Field + 8), header - rows.Field - 8L);
        BinaryPrimitives.WriteInt64LittleEndian(output, output.Length);
        return output;
    }

    /**
     * Resizes the display rows to newCount, keeping authored rows (text overrides, visibility and unlock expressions).
     * The row at removedIndex is dropped, then the array is trimmed or padded with empty rows at the end.
     */
    public static byte[] Resize(byte[] display, int newCount, int? removedIndex = null)
    {
        if (newCount < 0 || newCount > 4096)
            throw new InvalidDataException("The item has too many perk display rows.");
        var rows = Read(display);
        if (rows.Field < 0)
            throw new InvalidDataException("The item display has no perk display block, so its perk rows cannot be kept in step.");
        if (removedIndex is { } gone && (gone < 0 || gone >= rows.Count))
            throw new InvalidDataException("The removed perk display row is outside its array.");
        if (removedIndex is null && rows.Count == newCount) return (byte[])display.Clone();
        if (newCount == 0)
        {
            byte[] cleared = (byte[])display.Clone();
            cleared.AsSpan(rows.Field, 16).Clear();
            return cleared;
        }
        var data = new TagReader(display);
        int header = checked((display.Length + 19) & ~15);
        byte[] output = new byte[checked(header + 16 + newCount * Stride)];
        display.CopyTo(output, 0);
        TagArrayRelocation.WriteHeader(output, header, newCount, RowClass);
        int target = header + 16;
        for (int index = 0; index < rows.Count && target < output.Length; index++)
        {
            if (index == removedIndex) continue;
            int source = rows.Data + index * Stride;
            display.AsSpan(source, Stride).CopyTo(output.AsSpan(target));
            TagArrayRelocation.Rebase(data, output, source + 16, target + 16);
            target += Stride;
        }
        for (; target < output.Length; target += Stride)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(target), ushort.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(target + 4), InvestmentItemClone.EmptyStringHash);
        }
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(rows.Field), newCount);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(rows.Field + 8), header - rows.Field - 8L);
        BinaryPrimitives.WriteInt64LittleEndian(output, output.Length);
        return output;
    }

    /** The native display block owns a typed 32-byte row array at +8. */
    private static (int Field, int Count, int Data) Read(ReadOnlyMemory<byte> display)
    {
        var reader = new TagReader(display);
        if (display.Length < InvestmentItemClone.DisplayLength || reader.U64(0) != (ulong)display.Length)
            throw new InvalidDataException("The item display size is invalid.");
        int block = reader.Relative(BlockPointer);
        if (block < 0) return (-1, 0, 0);
        if (block < 4 || !reader.Has(block, 24) || reader.U32(block - 4) != BlockClass)
            throw new InvalidDataException("The item perk display block has an unsupported layout.");
        var rows = reader.Array(block + 8, RowClass, Stride);
        if (rows.Count == 0 && reader.I64(block + 16) != 0)
            throw new InvalidDataException("The empty perk display array has an unsupported pointer.");
        for (int index = 0; index < rows.Count; index++)
            reader.Array(rows.Data + index * Stride + 8, ExpressionClass, 8);
        return (block + 8, rows.Count, rows.Data);
    }
}
