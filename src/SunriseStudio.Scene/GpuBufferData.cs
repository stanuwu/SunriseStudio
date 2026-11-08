using System.Buffers.Binary;
using System.Numerics;

namespace SunriseStudio.Scene;

/** GPU vertex and index buffer payloads (package type 40); they hold no tag fields. */
internal static class GpuBufferData
{
    /** Maps a snorm16 lane to [-1, 1]. */
    public static float Snorm(short value) => Math.Max(value / 32767f, -1f);

    /** Two snorm16 lanes starting at `offset`. */
    public static Vector2 Snorm2(byte[] data, int offset) =>
        new(Snorm(I16(data, offset)), Snorm(I16(data, offset + sizeof(short))));

    /** Three snorm16 lanes starting at `offset`. */
    public static Vector3 Snorm3(byte[] data, int offset) =>
        new(Snorm(I16(data, offset)), Snorm(I16(data, offset + sizeof(short))), Snorm(I16(data, offset + 2 * sizeof(short))));

    /** Four raw int16 lanes starting at `offset`. */
    public static (short X, short Y, short Z, short W) Short4(byte[] data, int offset) =>
        (I16(data, offset), I16(data, offset + sizeof(short)), I16(data, offset + 2 * sizeof(short)), I16(data, offset + 3 * sizeof(short)));

    /** Three float32 lanes starting at `offset`. */
    public static Vector3 Float3(byte[] data, int offset) =>
        new(F32(data, offset), F32(data, offset + sizeof(float)), F32(data, offset + 2 * sizeof(float)));

    /** @param wide True for 32-bit indices, false for 16-bit ones. */
    public static uint Index(byte[] data, int offset, bool wide) =>
        wide ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, sizeof(ushort)));

    private static short I16(byte[] data, int offset) => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, sizeof(short)));

    private static float F32(byte[] data, int offset) =>
        BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, sizeof(uint))));
}
