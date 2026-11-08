using System.Buffers.Binary;
using System.Numerics;

namespace SunriseStudio.Tags.Views;

/** Little-endian reads at an absolute tag offset; an offset outside the tag throws. */
public static class TagRead
{
    public static byte U8(ReadOnlySpan<byte> tag, int at) => tag[at];
    public static sbyte I8(ReadOnlySpan<byte> tag, int at) => unchecked((sbyte)tag[at]);
    public static ushort U16(ReadOnlySpan<byte> tag, int at) => BinaryPrimitives.ReadUInt16LittleEndian(tag.Slice(at, 2));
    public static short I16(ReadOnlySpan<byte> tag, int at) => BinaryPrimitives.ReadInt16LittleEndian(tag.Slice(at, 2));
    public static uint U32(ReadOnlySpan<byte> tag, int at) => BinaryPrimitives.ReadUInt32LittleEndian(tag.Slice(at, 4));
    public static int I32(ReadOnlySpan<byte> tag, int at) => BinaryPrimitives.ReadInt32LittleEndian(tag.Slice(at, 4));
    public static ulong U64(ReadOnlySpan<byte> tag, int at) => BinaryPrimitives.ReadUInt64LittleEndian(tag.Slice(at, 8));
    public static long I64(ReadOnlySpan<byte> tag, int at) => BinaryPrimitives.ReadInt64LittleEndian(tag.Slice(at, 8));
    public static Half F16(ReadOnlySpan<byte> tag, int at) => BinaryPrimitives.ReadHalfLittleEndian(tag.Slice(at, 2));
    public static float F32(ReadOnlySpan<byte> tag, int at) => BinaryPrimitives.ReadSingleLittleEndian(tag.Slice(at, 4));
    public static Vector2 Vec2(ReadOnlySpan<byte> tag, int at) => new(F32(tag, at), F32(tag, at + 4));
    public static Vector3 Vec3(ReadOnlySpan<byte> tag, int at) => new(F32(tag, at), F32(tag, at + 4), F32(tag, at + 8));
    public static Vector4 Vec4(ReadOnlySpan<byte> tag, int at) =>
        new(F32(tag, at), F32(tag, at + 4), F32(tag, at + 8), F32(tag, at + 12));
}
