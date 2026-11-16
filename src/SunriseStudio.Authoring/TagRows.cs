using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/** Writable access to the rows of a tag's typed array. */
internal static class TagRows
{
    /** The rows of an array field read through TagView.Checked or At; a malformed array or another element class throws. */
    public static Span<T> Writable<T>(byte[] bytes, ref readonly TagArray<T> field, uint elementClass) where T : unmanaged
    {
        int count = TagView.Rows(bytes, in field, elementClass).Length;
        var array = new TagArray(bytes, TagView.OffsetOf(bytes, in field));
        return count == 0 ? [] : MemoryMarshal.Cast<byte, T>(bytes.AsSpan(array.DataOffset, count * Unsafe.SizeOf<T>()));
    }

    /** A copy of the tag whose array at `field` holds `rows` of class `type`; the relocator keeps a size word current. */
    public static byte[] Replace<T>(byte[] source, int field, uint type, T[] rows) where T : unmanaged
    {
        if (field < TagView.SizeWordSize || field > source.Length - Unsafe.SizeOf<TagArray<Unknown>>())
            throw new InvalidDataException("Invalid native array replacement.");
        return SunriseStudio.Tags.Document.TagRelocator.ReplaceArray(source, field, type, Unsafe.SizeOf<T>(),
            MemoryMarshal.AsBytes(rows.AsSpan()).ToArray(), out _);
    }
}
