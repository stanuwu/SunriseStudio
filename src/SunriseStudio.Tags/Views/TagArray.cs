using System.Buffers.Binary;

namespace SunriseStudio.Tags.Views;

/**
 * A Tiger array field: {i64 count, i64 rel}. rel is self-relative from field + 8 to a header
 * {u64 count, u32 element class, u32 0} that follows the array marker; rows start at header + 16.
 */
public readonly ref struct TagArray
{
    /** The word before every array header. */
    public const uint Marker = 0x80809FBD;
    /** Checked reads refuse a larger count before allocating rows. */
    public const int MaximumCheckedCount = 1_048_576;
    /** The row cap the hand-checked readers use; they refuse a larger count. */
    public const int MaximumReaderRows = 1_000_000;
    /** Rows start this far after the header. */
    public const int HeaderSize = 16;
    /** The element class word sits this far into the header; the zero word follows it. */
    public const int ElementClassOffset = 8;
    /** The header's last word; it is always zero. */
    public const int ZeroWordOffset = 12;
    /** Headers start on this boundary, with the marker just before. */
    public const int HeaderAlignment = 16;
    /** An array field: {i64 count, i64 relative}. */
    public const int FieldSize = 16;
    /** The relative word sits this far into the field and counts from its own offset. */
    public const int RelativeOffset = 8;

    private readonly ReadOnlySpan<byte> tag;

    /** @param at Absolute offset of the 16-byte array field in the tag. */
    public TagArray(ReadOnlySpan<byte> tag, int at)
    {
        this.tag = tag;
        FieldOffset = at;
    }

    public int FieldOffset { get; }
    public long Count => TagRead.I64(tag, FieldOffset);
    /** An array with no rows may have no header at all. */
    public bool HasHeader => TagRead.I64(tag, FieldOffset + RelativeOffset) != 0;
    public int HeaderOffset => checked((int)(FieldOffset + RelativeOffset + TagRead.I64(tag, FieldOffset + RelativeOffset)));
    public uint ElementClass => HasHeader ? TagRead.U32(tag, HeaderOffset + ElementClassOffset) : 0;
    public int DataOffset => HeaderOffset + HeaderSize;
    /** The header the relative word names, in long arithmetic; it never throws on a garbage relative word. */
    public TagArrayHeader Header => new(tag, FieldOffset + (long)RelativeOffset + TagRead.I64(tag, FieldOffset + RelativeOffset));

    /** @return The absolute offset of row `index`; throws when it is outside the array. */
    public int RowOffset(long index, int stride)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
        return checked((int)(DataOffset + index * stride));
    }

    /** True when the header carries the marker, repeats the count and has its zero word. */
    public bool IsWellFormed =>
        Count == 0 && !HasHeader
        || Count >= 0 && HasHeader && HeaderOffset >= TagView.ClassWordSize && HeaderOffset + HeaderSize <= tag.Length
           && TagRead.U32(tag, HeaderOffset - TagView.ClassWordSize) == Marker && TagRead.I64(tag, HeaderOffset) == Count
           && TagRead.U32(tag, HeaderOffset + ZeroWordOffset) == 0;

    /** True when `Count` rows of `stride` bytes fit between the data start and the tag end. */
    public bool RowsFit(int stride) => stride <= 0 || Count <= (tag.Length - (long)DataOffset) / stride;

    /** @return True when the marker sits just before `header`, inside the tag; never throws. */
    public static bool MarkerAt(ReadOnlySpan<byte> tag, long header) =>
        header >= TagView.ClassWordSize && header <= tag.Length
        && BinaryPrimitives.ReadUInt32LittleEndian(tag[(int)(header - TagView.ClassWordSize)..]) == Marker;

    /**
     * Reads an array field, checked: a zero count is empty, anything else needs the marker, a
     * repeated count, rows inside the tag and, when given, the element class.
     * @param elementClass Zero accepts any class.
     * @param stride Zero skips the row bounds check.
     * @param maximumCount A larger count is refused before any row is read.
     * @return The row count, the first row and the element class; an empty array reports `elementClass`.
     */
    public static (int Count, int Data, uint Class) Checked(ReadOnlySpan<byte> tag, int field, uint elementClass = 0, int stride = 0,
        int maximumCount = MaximumCheckedCount)
    {
        if (!Inside(tag, field, FieldSize)) throw new InvalidDataException("Truncated array descriptor.");
        ulong count = BinaryPrimitives.ReadUInt64LittleEndian(tag[field..]);
        if (count == 0) return (0, 0, elementClass);
        int header = TagPointer.Checked(tag, field + RelativeOffset);
        if (count > (ulong)maximumCount || header < TagView.ClassWordSize || !Inside(tag, header, HeaderSize)
            || BinaryPrimitives.ReadUInt32LittleEndian(tag[(header - TagView.ClassWordSize)..]) != Marker)
            throw new InvalidDataException("Invalid typed array header.");
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(tag[(header + ElementClassOffset)..]);
        if (BinaryPrimitives.ReadUInt64LittleEndian(tag[header..]) != count) throw new InvalidDataException("Array counts disagree.");
        if (elementClass != 0 && type != elementClass)
            throw new InvalidDataException($"Unexpected array class {type:X8}.");
        int data = checked(header + HeaderSize);
        if (stride > 0 && !Inside(tag, data, checked((int)count * stride)))
            throw new InvalidDataException("Array elements leave the asset.");
        return ((int)count, data, type);
    }

    /** @return False where Checked throws InvalidDataException or OverflowException; `result` is then default. */
    public static bool TryChecked(ReadOnlySpan<byte> tag, int field, uint elementClass, int stride,
        out (int Count, int Data, uint Class) result, int maximumCount = MaximumCheckedCount)
    {
        try
        {
            result = Checked(tag, field, elementClass, stride, maximumCount);
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            result = default;
            return false;
        }
    }

    /** Writes a header block: the marker before `header`, the count, the element class and the zero word. */
    public static void WriteHeader(Span<byte> tag, int header, long count, uint elementClass)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(tag[(header - TagView.ClassWordSize)..], Marker);
        BinaryPrimitives.WriteInt64LittleEndian(tag[header..], count);
        BinaryPrimitives.WriteUInt32LittleEndian(tag[(header + ElementClassOffset)..], elementClass);
        BinaryPrimitives.WriteUInt32LittleEndian(tag[(header + ZeroWordOffset)..], 0);
    }

    /** Copies the element class word and the zero word of one header into another. */
    public static void CopyHeaderWords(ReadOnlySpan<byte> source, int sourceHeader, Span<byte> target, int targetHeader) =>
        source.Slice(sourceHeader + ElementClassOffset, HeaderSize - ElementClassOffset)
            .CopyTo(target[(targetHeader + ElementClassOffset)..]);

    /** Points the array field at `field` to the header at `header`. */
    public static void WriteField(Span<byte> tag, int field, long count, int header)
    {
        BinaryPrimitives.WriteInt64LittleEndian(tag[field..], count);
        BinaryPrimitives.WriteInt64LittleEndian(tag[(field + RelativeOffset)..], header - field - (long)RelativeOffset);
    }

    private static bool Inside(ReadOnlySpan<byte> tag, int offset, int count) =>
        offset >= 0 && count >= 0 && offset <= tag.Length - count;
}

/**
 * An array header read at its own offset, for walks that start at a header rather than a field.
 * Nothing here throws on a header outside the tag; read Count and ElementClass after IsWellFormed.
 */
public readonly ref struct TagArrayHeader
{
    private readonly ReadOnlySpan<byte> tag;

    /** @param offset Absolute offset of the {u64 count, u32 element class, u32 0} header. */
    public TagArrayHeader(ReadOnlySpan<byte> tag, long offset)
    {
        this.tag = tag;
        Offset = offset;
    }

    public long Offset { get; }
    public long DataOffset => Offset + TagArray.HeaderSize;
    public long Count => IsInside ? TagRead.I64(tag, (int)Offset) : 0;
    public uint ElementClass => IsInside ? TagRead.U32(tag, (int)Offset + TagArray.ElementClassOffset) : 0;

    /** True when the header is inside the tag, follows the marker and holds a count of zero or more. The zero word is not checked. */
    public bool IsWellFormed => IsInside && TagArray.MarkerAt(tag, Offset) && Count >= 0;

    /** True when the header is inside the tag and its zero word holds zero. */
    public bool HasZeroWord => IsInside && TagRead.U32(tag, (int)Offset + TagArray.ZeroWordOffset) == 0;

    /** True when `Count` rows of `stride` bytes fit between the data start and the tag end. */
    public bool RowsFit(int stride) => stride <= 0 || Count <= (tag.Length - DataOffset) / stride;

    private bool IsInside => Offset >= TagView.ClassWordSize && Offset <= tag.Length - TagArray.HeaderSize;
}
