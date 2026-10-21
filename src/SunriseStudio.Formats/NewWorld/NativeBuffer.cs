using System.Buffers.Binary;

namespace SunriseStudio.Formats.NewWorld;

/** A growing little-endian resource payload with the typed-array placements native writers use. */
public sealed class NativeBuffer
{
    /** The word 20 bytes before every typed array's rows. */
    public const uint ArrayMarker = NativeArrays.Marker;
    private byte[] bytes;

    public NativeBuffer(int size) { bytes = new byte[Math.Max(size, 64)]; Length = size; }

    public NativeBuffer(ReadOnlySpan<byte> source) : this(source.Length) => source.CopyTo(bytes);

    public int Length { get; private set; }

    public byte[] ToArray() => bytes.AsSpan(0, Length).ToArray();

    public ReadOnlySpan<byte> Span(int offset, int count)
    {
        Check(offset, count);
        return bytes.AsSpan(offset, count);
    }

    public uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Span(offset, 4));

    public void Put8(int offset, byte value) { Check(offset, 1); bytes[offset] = value; }
    public void Put16(int offset, ushort value) { Check(offset, 2); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value); }
    public void PutI16(int offset, short value) { Check(offset, 2); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(offset), value); }
    public void Put32(int offset, uint value) { Check(offset, 4); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value); }
    public void PutI32(int offset, int value) { Check(offset, 4); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value); }
    public void Put64(int offset, ulong value) { Check(offset, 8); BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value); }
    public void PutI64(int offset, long value) { Check(offset, 8); BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), value); }
    public void PutF32(int offset, float value) { Check(offset, 4); BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), value); }

    public void PutBytes(int offset, ReadOnlySpan<byte> value) { Check(offset, value.Length); value.CopyTo(bytes.AsSpan(offset)); }

    public void PutF32s(int offset, params float[] values)
    {
        for (int i = 0; i < values.Length; i++) PutF32(offset + 4 * i, values[i]);
    }

    /** The u64 size word at +0 equals the payload length. */
    public void SetSize() => Put64(0, (ulong)Length);

    /** Appends bytes and returns where they start. */
    public int Add(ReadOnlySpan<byte> value)
    {
        int at = Length;
        Grow(value.Length);
        value.CopyTo(bytes.AsSpan(at));
        return at;
    }

    /** Appends zero bytes and returns where they start. */
    public int AddZeros(int count)
    {
        int at = Length;
        Grow(count);
        return at;
    }

    /** Appends a byte value until the length is a multiple of alignment. */
    public void Pad(int alignment, byte fill = 0)
    {
        int count = (alignment - Length % alignment) % alignment;
        int at = AddZeros(count);
        if (fill != 0) bytes.AsSpan(at, count).Fill(fill);
    }

    /**
     * Rows at a 16-byte boundary after a 32-byte {12 zero, marker, u64 count, u32 class, u32 0} prefix; an empty
     * array is a zero descriptor (World Studio map_definition.append_array).
     */
    public int[] AppendPadded(int descriptor, uint type, int stride, IReadOnlyList<byte[]> rows)
    {
        if (rows.Any(row => row.Length != stride)) throw new InvalidDataException("Typed array row size differs.");
        if (rows.Count == 0) { PutI64(descriptor, 0); PutI64(descriptor + 8, 0); return []; }
        int start = NativeArrays.PaddedStart(Length);
        AddZeros(start - Length);
        foreach (var row in rows) Add(row);
        NativeArrays.Frame(bytes.AsSpan(0, Length), descriptor, type, rows.Count, start);
        return Enumerable.Range(0, rows.Count).Select(i => start + i * stride).ToArray();
    }

    /**
     * Rows at align16(end + 20) after {marker, u64 count, u32 class, u32 0}, the placement of installed
     * definitions, roots and dependency records; an empty array is a zero descriptor. Returns the first row or -1.
     */
    public int AppendNative(int descriptor, uint type, int count, ReadOnlySpan<byte> rows)
    {
        if (count == 0) { PutI64(descriptor, 0); PutI64(descriptor + 8, 0); return -1; }
        int start = NativeArrays.NativeStart(Length);
        AddZeros(start - Length);
        Add(rows);
        NativeArrays.Frame(bytes.AsSpan(0, Length), descriptor, type, count, start);
        return start;
    }

    /** AppendNative over equal rows; returns each row's offset. */
    public int[] AppendNativeRows(int descriptor, uint type, int stride, IReadOnlyList<byte[]> rows)
    {
        if (rows.Any(row => row.Length != stride)) throw new InvalidDataException("Typed array row size differs.");
        int start = AppendNative(descriptor, type, rows.Count, rows.SelectMany(row => row).ToArray());
        return Enumerable.Range(0, rows.Count).Select(i => start + i * stride).ToArray();
    }

    /** A 24-byte {4 zero, marker, u64 count, u32 class, u32 0} prefix right after a header ending at 8 mod 16. */
    public int AppendCompact(int descriptor, uint type, IReadOnlyList<byte[]> rows)
    {
        if (Length % 16 != 8) throw new InvalidDataException("Compact array header is not at 8 mod 16.");
        int start = AddZeros(24) + 24;
        foreach (var row in rows) Add(row);
        NativeArrays.Frame(bytes.AsSpan(0, Length), descriptor, type, rows.Count, start);
        SetSize();
        return start;
    }

    private void Grow(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        int need = checked(Length + count);
        if (need > bytes.Length) Array.Resize(ref bytes, Math.Max(need, bytes.Length * 2));
        Length = need;
    }

    private void Check(int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > Length - count)
            throw new InvalidDataException("A native write leaves its payload.");
    }
}

/** Little-endian row builders for fixed native records. */
public static class NativeRow
{
    public static byte[] Zero(int size) => new byte[size];

    public static byte[] U32s(params uint[] values)
    {
        byte[] row = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(4 * i), values[i]);
        return row;
    }

    public static byte[] U16s(params ushort[] values)
    {
        byte[] row = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(2 * i), values[i]);
        return row;
    }

    public static byte[] F32s(params float[] values)
    {
        byte[] row = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(row.AsSpan(4 * i), values[i]);
        return row;
    }

    /** A wide reference row in direct mode: {tag placeholder, 1, u64 0}. */
    public static byte[] Wide() => U32s(0, 1, 0, 0);
}
