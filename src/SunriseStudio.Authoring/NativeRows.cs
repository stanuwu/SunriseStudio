using System.Buffers.Binary;

namespace SunriseStudio.Authoring;

/** Bounds-checked little-endian reads over one tag payload, and its typed array headers. Activity registration reads through it. */
internal sealed class NativeRows(byte[] bytes)
{
    internal byte[] Bytes { get; } = bytes;
    internal uint U32(int p) => BinaryPrimitives.ReadUInt32LittleEndian(Span(p, 4));
    internal int I32(int p) => BinaryPrimitives.ReadInt32LittleEndian(Span(p, 4));
    internal ushort U16(int p) => BinaryPrimitives.ReadUInt16LittleEndian(Span(p, 2));
    internal ulong U64(int p) => BinaryPrimitives.ReadUInt64LittleEndian(Span(p, 8));
    internal long I64(int p) => BinaryPrimitives.ReadInt64LittleEndian(Span(p, 8));

    internal ReadOnlySpan<byte> Span(int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > Bytes.Length - count) throw new InvalidDataException("Placement range leaves its source.");
        return Bytes.AsSpan(offset, count);
    }

    /** Row offsets of the typed array whose count and relative pointer sit at field. */
    internal int[] Rows(int field, uint type, int stride)
    {
        long count = I64(field), relative = I64(field + 8);
        if (count == 0 && relative == 0) return [];
        int header = checked((int)(field + 8 + relative));
        if (count is < 0 or > 1_000_000 || U32(header - 4) != ArrayMarker || I64(header) != count
            || U32(header + 8) != type || U32(header + 12) != 0)
            throw new InvalidDataException("Placement array layout is invalid.");
        Span(header + 16, checked((int)count * stride));
        return Enumerable.Range(0, (int)count).Select(i => checked(header + 16 + i * stride)).ToArray();
    }

    // The word just before every typed array header.
    internal const uint ArrayMarker = 0x80809FBD;
}
