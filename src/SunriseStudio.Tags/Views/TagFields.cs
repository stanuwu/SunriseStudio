using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SunriseStudio.Tags.Views;

/** An element or target class the registry does not name. */
public readonly struct Unknown;

/** An array field as generated structs hold it; TagView.Rows resolves the rows. */
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct TagArray<T> where T : unmanaged
{
    public readonly long Count;
    /** From the field's second word to the array header. */
    public readonly long Relative;
}

/** A self-relative pointer as generated structs hold it; zero is null. TagView.TargetOffset resolves it. */
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct RelativePointer<T> where T : unmanaged
{
    public readonly long Relative;
    public bool IsNull => Relative == 0;
}

/**
 * Reads generated structs in place in tag bytes. A struct read with At is a reference into the tag,
 * so its array and pointer fields know where they are; a copied struct does not, and Rows or Target
 * on one of its fields throws.
 */
public static partial class TagView
{
    /** Every inline object and array header follows a u32 class word. */
    public const int ClassWordSize = 4;
    /** A tag may open with a u64 that holds its own byte length. */
    public const int SizeWordSize = 8;

    /** @return The first `align` boundary that leaves room for a class word after `end`. */
    public static int BodyAfter(int end, int align) => checked((end + ClassWordSize + (align - 1)) / align * align);

    /** True when the tag's first u64 holds its own length. */
    public static bool HasSizeWord(ReadOnlySpan<byte> tag) =>
        tag.Length >= SizeWordSize && BinaryPrimitives.ReadUInt64LittleEndian(tag) == (ulong)tag.Length;

    /** Writes the tag's length into its first u64. */
    public static void WriteSizeWord(Span<byte> tag) => BinaryPrimitives.WriteInt64LittleEndian(tag, tag.Length);

    /** A rewritten tag keeps a size word only when its source had one. */
    public static void CarrySizeWord(ReadOnlySpan<byte> source, Span<byte> output)
    {
        if (HasSizeWord(source)) WriteSizeWord(output);
    }

    /** @return The struct at `offset`, as a reference into the tag bytes. */
    public static ref readonly T At<T>(ReadOnlySpan<byte> tag, int offset = 0) where T : unmanaged =>
        ref MemoryMarshal.AsRef<T>(tag.Slice(offset, Unsafe.SizeOf<T>()));

    /** @return Where a field read through At sits in the tag. */
    public static int OffsetOf<T>(ReadOnlySpan<byte> tag, ref readonly T field) where T : unmanaged
    {
        long offset = (long)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(tag), ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in field)));
        if (offset < 0 || offset > tag.Length - Unsafe.SizeOf<T>())
            throw new ArgumentException("The field is not inside this tag; read its struct with TagView.At, not a copy.");
        return (int)offset;
    }

    /** @return The rows of an array field; empty for an empty array. A malformed header, or rows past the tag, throw. */
    public static ReadOnlySpan<T> Rows<T>(ReadOnlySpan<byte> tag, ref readonly TagArray<T> array) where T : unmanaged
    {
        var view = new TagArray(tag, OffsetOf(tag, in array));
        if (!view.IsWellFormed) throw new InvalidDataException($"The array at {view.FieldOffset} is malformed.");
        if (view.Count == 0) return [];
        if (view.Count > (tag.Length - view.DataOffset) / Unsafe.SizeOf<T>())
            throw new InvalidDataException($"The array at {view.FieldOffset} runs past the tag.");
        return MemoryMarshal.Cast<byte, T>(tag.Slice(view.DataOffset, checked((int)view.Count * Unsafe.SizeOf<T>())));
    }

    /** @return The rows of an array field whose header must name `elementClass`; another class throws. */
    public static ReadOnlySpan<T> Rows<T>(ReadOnlySpan<byte> tag, ref readonly TagArray<T> array, uint elementClass) where T : unmanaged
    {
        var view = new TagArray(tag, OffsetOf(tag, in array));
        if (view.HasHeader && view.IsWellFormed && view.ElementClass != elementClass)
            throw new InvalidDataException($"The array at {view.FieldOffset} holds class 0x{view.ElementClass:X8}, not 0x{elementClass:X8}.");
        return Rows(tag, in array);
    }

    /** @return The struct at `offset` as a writable reference; InvalidDataException when it does not fit. */
    public static ref T Edit<T>(Span<byte> tag, int offset = 0) where T : unmanaged
    {
        if (!Fits<T>(tag, offset)) throw new InvalidDataException($"A {typeof(T).Name} at {offset} does not fit the {tag.Length}-byte tag.");
        return ref MemoryMarshal.AsRef<T>(tag.Slice(offset, Unsafe.SizeOf<T>()));
    }

    /** @return The writable rows of an array field read through Edit; Rows checks them first. */
    public static Span<T> EditRows<T>(Span<byte> tag, ref readonly TagArray<T> array, uint elementClass) where T : unmanaged
    {
        var rows = Rows(tag, in array, elementClass);
        if (rows.IsEmpty) return [];
        int start = (int)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(tag), ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(rows)));
        return MemoryMarshal.Cast<byte, T>(tag.Slice(start, rows.Length * Unsafe.SizeOf<T>()));
    }

    /** @return True when a whole T starting at `offset` lies inside the tag. */
    public static bool Fits<T>(ReadOnlySpan<byte> tag, int offset) where T : unmanaged =>
        offset >= 0 && offset <= tag.Length - Unsafe.SizeOf<T>();

    /** @return The struct at `offset`, or InvalidDataException when it does not fit the tag. */
    public static ref readonly T Checked<T>(ReadOnlySpan<byte> tag, int offset) where T : unmanaged
    {
        if (!Fits<T>(tag, offset)) throw new InvalidDataException($"A {typeof(T).Name} at {offset} does not fit the {tag.Length}-byte tag.");
        return ref At<T>(tag, offset);
    }

    /** @return The absolute offset a pointer names, or null for a null pointer. */
    public static int? TargetOffset<T>(ReadOnlySpan<byte> tag, ref readonly RelativePointer<T> pointer) where T : unmanaged =>
        pointer.IsNull ? null : checked((int)(OffsetOf(tag, in pointer) + pointer.Relative));

    /** @return The class word before an object a pointer names, or null when there is none inside the tag. */
    public static uint? TargetClass<T>(ReadOnlySpan<byte> tag, ref readonly RelativePointer<T> pointer) where T : unmanaged =>
        TargetOffset(tag, in pointer) is { } target ? ClassAt(tag, target) : null;

    /** @return The pointer field's offset plus its relative word; a null pointer gives the field itself. */
    public static int TargetOrSelf<T>(ReadOnlySpan<byte> tag, ref readonly RelativePointer<T> pointer) where T : unmanaged =>
        checked((int)(OffsetOf(tag, in pointer) + pointer.Relative));

    /** @return The class word just before the object at `objectOffset`, or null when that word is outside the tag. */
    public static uint? ClassAt(ReadOnlySpan<byte> tag, int objectOffset) =>
        objectOffset >= ClassWordSize && objectOffset <= tag.Length
            ? BinaryPrimitives.ReadUInt32LittleEndian(tag[(objectOffset - ClassWordSize)..]) : null;

    /** Writes the class word just before the object at `objectOffset`. */
    public static void WriteClassWord(Span<byte> tag, int objectOffset, uint classId) =>
        BinaryPrimitives.WriteUInt32LittleEndian(tag[(objectOffset - ClassWordSize)..], classId);

    /** Points a pointer field at `target`; null writes the null pointer. The field must be read from these bytes. */
    public static void SetTarget<T>(Span<byte> tag, ref readonly RelativePointer<T> pointer, int? target) where T : unmanaged
    {
        int at = OffsetOf(tag, in pointer);
        BinaryPrimitives.WriteInt64LittleEndian(tag[at..], target is { } value ? (long)value - at : 0);
    }

    /** Writes an array field's count and points it at `header`; a null header writes the null array word. */
    public static void SetArray<T>(Span<byte> tag, ref readonly TagArray<T> array, long count, int? header) where T : unmanaged
    {
        int at = OffsetOf(tag, in array);
        if (header is { } value) TagArray.WriteField(tag, at, count, value);
        else
        {
            BinaryPrimitives.WriteInt64LittleEndian(tag[at..], count);
            BinaryPrimitives.WriteInt64LittleEndian(tag[(at + TagArray.RelativeOffset)..], 0);
        }
    }

    /**
     * Reads an array field with TagArray.Checked's checks and messages; the element class is not checked on an empty array.
     * @param elementClass Zero accepts any class.
     * @param maximumCount A larger count is refused before any row is read.
     */
    public static ReadOnlySpan<T> CheckedRows<T>(ReadOnlySpan<byte> tag, ref readonly TagArray<T> array, uint elementClass,
        int maximumCount = TagArray.MaximumCheckedCount) where T : unmanaged
    {
        int stride = Unsafe.SizeOf<T>();
        var (count, data, _) = TagArray.Checked(tag, OffsetOf(tag, in array), elementClass, stride, maximumCount);
        return count == 0 ? [] : MemoryMarshal.Cast<byte, T>(tag.Slice(data, count * stride));
    }

    /** @return The absolute offset of each row, for rows from Rows or CheckedRows over these bytes. */
    public static int[] RowOffsets<T>(ReadOnlySpan<byte> tag, ReadOnlySpan<T> rows) where T : unmanaged
    {
        var offsets = new int[rows.Length];
        for (int index = 0; index < rows.Length; index++) offsets[index] = OffsetOf(tag, in rows[index]);
        return offsets;
    }
}
