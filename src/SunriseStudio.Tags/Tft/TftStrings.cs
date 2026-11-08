using System.Buffers.Binary;
using System.Text;

namespace SunriseStudio.Tags.Tft;

/** The `strs` table: NUL-terminated UTF-8 strings, each stored once, in first-use order. */
internal sealed class TftStrings
{
    private readonly Dictionary<string, int> offsets = new(StringComparer.Ordinal);
    private readonly MemoryStream bytes = new();

    public int Count => offsets.Count;

    /** @return The string's byte offset in the table. */
    public int Add(string text)
    {
        if (text.Contains('\0')) throw new InvalidDataException("A .tft string holds a NUL.");
        if (offsets.TryGetValue(text, out int offset)) return offset;
        offset = (int)bytes.Length;
        offsets.Add(text, offset);
        bytes.Write(Encoding.UTF8.GetBytes(text));
        bytes.WriteByte(0);
        return offset;
    }

    public int this[string text] => offsets[text];

    public byte[] ToArray() => bytes.ToArray();

    /** @return The string that starts at `offset`; an offset that is not a string start throws. */
    public static string At(ReadOnlySpan<byte> table, uint offset)
    {
        if (offset >= table.Length || (offset > 0 && table[(int)offset - 1] != 0))
            throw new InvalidDataException($"String offset {offset} is not the start of a string.");
        var rest = table[(int)offset..];
        int end = rest.IndexOf((byte)0);
        if (end < 0) throw new InvalidDataException("The string table has no terminator.");
        return new UTF8Encoding(false, true).GetString(rest[..end]);
    }
}

/** Little-endian appends for the `.tft` writer. */
internal static class TftBytes
{
    public static void U16(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, checked((ushort)value));
        stream.Write(buffer);
    }

    public static void U32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    public static void I32(Stream stream, int value) => U32(stream, unchecked((uint)value));

    public static void U64(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    public static void Pad(Stream stream, int align)
    {
        while (stream.Length % align != 0) stream.WriteByte(0);
    }
}
