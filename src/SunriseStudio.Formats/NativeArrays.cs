using System.Buffers.Binary;

namespace SunriseStudio.Formats;

/**
 * Typed native arrays: rows follow {marker 0x80809FBD, u64 count, u32 class, u32 0}, and the owner's
 * {u64 count, i64 relative} descriptor puts the first row at descriptor + 24 + relative. No rows is a zero descriptor.
 */
public static class NativeArrays
{
    /** The word 20 bytes before the first row of every typed array. */
    public const uint Marker = 0x80809FBD;

    /** First row of an array appended at end in the installed placement: align16(end + 20). */
    public static int NativeStart(int end) => checked((end + 35) & ~15);

    /** First row of an array appended at end after 12 zero bytes and the prefix (World Studio map_definition.append_array). */
    public static int PaddedStart(int end) => checked(((end + 15) & ~15) + 32);

    /** Writes the prefix before first and points the descriptor at it; the rows are the caller's. */
    public static void Frame(Span<byte> payload, int descriptor, uint type, int count, int first)
    {
        if (count < 0 || first < 20 || descriptor < 0 || descriptor > payload.Length - 16 || first > payload.Length)
            throw new InvalidDataException("A typed array leaves its payload.");
        BinaryPrimitives.WriteUInt32LittleEndian(payload[(first - 20)..], Marker);
        BinaryPrimitives.WriteInt64LittleEndian(payload[(first - 16)..], count);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[(first - 8)..], type);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[(first - 4)..], 0);
        BinaryPrimitives.WriteInt64LittleEndian(payload[descriptor..], count);
        BinaryPrimitives.WriteInt64LittleEndian(payload[(descriptor + 8)..], first - descriptor - 24);
    }

    /**
     * Copies source with rows appended in the installed placement and the size word at +0 updated.
     * No rows leaves a zero descriptor, the size and every other byte as they were; first is then -1.
     */
    public static byte[] AppendNative(byte[] source, int descriptor, uint type, IReadOnlyList<byte[]> rows, out int first) =>
        Append(source, descriptor, type, rows, NativeStart(source.Length), out first);

    /** AppendNative with World Studio's padded placement. */
    public static byte[] AppendPadded(byte[] source, int descriptor, uint type, IReadOnlyList<byte[]> rows, out int first) =>
        Append(source, descriptor, type, rows, PaddedStart(source.Length), out first);

    private static byte[] Append(byte[] source, int descriptor, uint type, IReadOnlyList<byte[]> rows, int start, out int first)
    {
        if (descriptor < 0 || descriptor > source.Length - 16) throw new InvalidDataException("A typed array leaves its payload.");
        if (rows.Count == 0)
        {
            byte[] empty = source.ToArray();
            empty.AsSpan(descriptor, 16).Clear();
            first = -1;
            return empty;
        }
        byte[] output = new byte[checked(start + rows.Sum(row => row.Length))];
        source.CopyTo(output, 0);
        int at = first = start;
        foreach (byte[] row in rows) { row.CopyTo(output, at); at += row.Length; }
        Frame(output, descriptor, type, rows.Count, start);
        BinaryPrimitives.WriteInt64LittleEndian(output, output.Length);
        return output;
    }
}
