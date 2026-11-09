using System.Buffers.Binary;

namespace SunriseStudio.Formats;

/** Shared pieces of "copy an array to the tag end" edits: typed array headers and relative pointer rebasing. */
internal static class TagArrayRelocation
{
    /** Marker word that precedes every typed array header. */
    public const uint ArrayMarker = 0x80809FBD;

    /** Writes the marker (header - 4), the count (header) and the element class (header + 8) of a typed array. */
    public static void WriteHeader(byte[] output, int header, long count, uint elementClass)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(header - 4), ArrayMarker);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(header), count);
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(header + 8), elementClass);
    }

    /** Copies the relative pointer at source to target, re-aimed at the same address; null pointers stay null. */
    public static void Rebase(TagReader data, byte[] output, int source, int target)
    {
        int address = data.Relative(source);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(target), address < 0 ? 0 : address - target);
    }
}
