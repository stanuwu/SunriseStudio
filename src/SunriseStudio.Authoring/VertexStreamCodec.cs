using System.Buffers.Binary;
using System.Numerics;

namespace SunriseStudio.Authoring;

/** GPU vertex stream rows of the entity mesh profiles Studio writes; it holds no tag data. */
internal static class VertexStreamCodec
{
    // Position row: snorm16 X, Y, Z, then a short W lane.
    public const int PositionStride = 8;
    // Attribute row: snorm16 UV, then a 16-byte snorm16 normal and tangent frame.
    public const int AttributeStride = 20;
    // Declaration 139 adds a half2 TEXCOORD2 after the attribute row.
    public const int WeaponAttributeStride = 24;
    // Declaration 16: X, Y, Z of a position row, then four weight bytes and four joint bytes.
    public const int SkinPositionStride = 16;
    // One skin influence: four weight bytes, then four joint bytes.
    public const int InfluenceStride = 8;
    // The W lane of a declaration-139 position holds the matrix-palette index.
    private const int PaletteLane = 6;
    private const int Snorm16 = sizeof(short), PositionXyz = 3 * Snorm16;
    private const int FrameOffset = 2 * Snorm16, FrameLength = 16;
    private const int TexCoord2Offset = AttributeStride;

    /** @return The palette index of `vertex` in a position stream of `stride`. */
    public static short ReadPaletteIndex(byte[] positions, int stride, uint vertex) =>
        BinaryPrimitives.ReadInt16LittleEndian(positions.AsSpan(checked((int)vertex * stride + PaletteLane)));

    /** Writes the palette index of `vertex` in a declaration-139 position stream. */
    public static void WritePaletteIndex(byte[] positions, int vertex, short index) =>
        BinaryPrimitives.WriteInt16LittleEndian(positions.AsSpan(vertex * PositionStride + PaletteLane), index);

    /** Copies attribute row `vertex` into a declaration-139 attribute stream with TEXCOORD2 set to (1, 1). */
    public static void WriteWeaponAttribute(byte[] attributes, byte[] weaponAttributes, int vertex)
    {
        attributes.AsSpan(vertex * AttributeStride, AttributeStride).CopyTo(weaponAttributes.AsSpan(vertex * WeaponAttributeStride));
        BinaryPrimitives.WriteUInt16LittleEndian(weaponAttributes.AsSpan(vertex * WeaponAttributeStride + TexCoord2Offset),
            BitConverter.HalfToUInt16Bits((Half)1));
        BinaryPrimitives.WriteUInt16LittleEndian(weaponAttributes.AsSpan(vertex * WeaponAttributeStride + TexCoord2Offset + sizeof(ushort)),
            BitConverter.HalfToUInt16Bits((Half)1));
    }

    /**
     * Interleaves `count` position rows and skin influences into a declaration-16 position stream.
     * @return The stream; each row's W lane stays zero.
     */
    public static byte[] SkinPositions(byte[] positions, byte[] influences, int count)
    {
        var result = new byte[checked(count * SkinPositionStride)];
        for (int vertex = 0; vertex < count; vertex++)
        {
            positions.AsSpan(vertex * PositionStride, PositionXyz).CopyTo(result.AsSpan(vertex * SkinPositionStride));
            influences.AsSpan(vertex * InfluenceStride, InfluenceStride)
                .CopyTo(result.AsSpan(vertex * SkinPositionStride + PositionStride));
        }
        return result;
    }

    /** @return The snorm16 X, Y, Z at `at`, each in -1..1. */
    public static Vector3 ReadSnorm3(byte[] data, int at) =>
        new(ReadSnorm(data, at), ReadSnorm(data, at + Snorm16), ReadSnorm(data, at + 2 * Snorm16));

    /** @return The snorm16 U, V at `at`, each in -1..1. */
    public static Vector2 ReadSnorm2(byte[] data, int at) => new(ReadSnorm(data, at), ReadSnorm(data, at + Snorm16));

    /** Writes X, Y, Z as snorm16 at `at`; values outside -1..1 clamp. */
    public static void WriteSnorm3(byte[] data, int at, Vector3 value)
    {
        WriteSnorm(data, at, value.X); WriteSnorm(data, at + Snorm16, value.Y); WriteSnorm(data, at + 2 * Snorm16, value.Z);
    }

    /** Writes U, V as snorm16 at `at`; values outside -1..1 clamp. */
    public static void WriteSnorm2(byte[] data, int at, Vector2 value)
    {
        WriteSnorm(data, at, value.X); WriteSnorm(data, at + Snorm16, value.Y);
    }

    /** @return A copy of the normal and tangent frame of the attribute row at `at`. */
    public static byte[] ReadFrame(byte[] attributes, int at) => attributes.AsSpan(at + FrameOffset, FrameLength).ToArray();

    /** Writes the normal and tangent frame of the attribute row at `at`. */
    public static void WriteFrame(byte[] attributes, int at, byte[] frame) => frame.CopyTo(attributes, at + FrameOffset);

    private static float ReadSnorm(byte[] data, int at) => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(at)) / 32767f;
    private static void WriteSnorm(byte[] data, int at, float value) =>
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(at), (short)MathF.Round(Math.Clamp(value, -1, 1) * 32767));
}
