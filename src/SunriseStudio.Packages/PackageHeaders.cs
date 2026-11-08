using System.Runtime.InteropServices;

namespace SunriseStudio.Packages;

/** Header of a type 32 subtype 1-3 texture; its entry Reference is the resident mip payload (type 40). */
[StructLayout(LayoutKind.Explicit, Size = 40)]
public struct TextureHeader
{
    /** Every supported texture header holds this Marker. */
    public const ushort MarkerValue = 0xCAFE;
    /** StreamedTag when no mips stream from another tag. */
    public const uint NoStreamedTag = uint.MaxValue;

    /** Resident payload bytes plus the streamed tag's bytes. */
    [FieldOffset(0x00)] public uint ByteCount;
    /** A DXGI format. */
    [FieldOffset(0x04)] public uint Format;
    [FieldOffset(0x0C)] public ushort Marker;
    [FieldOffset(0x0E)] public ushort Width;
    [FieldOffset(0x10)] public ushort Height;
    [FieldOffset(0x12)] public ushort Depth;
    /** 6 on a cube. */
    [FieldOffset(0x14)] public ushort ArraySize;
    [FieldOffset(0x16)] public byte Unk16;
    [FieldOffset(0x17)] public byte MipCount;
    /** The largest mips, which live in StreamedTag; non-zero exactly when StreamedTag is set. */
    [FieldOffset(0x18)] public byte StreamedMipCount;
    [FieldOffset(0x19)] public byte Unk19;
    [FieldOffset(0x1B)] public byte Unk1b;
    /** A type 48 tag, or NoStreamedTag. */
    [FieldOffset(0x24)] public uint StreamedTag;
}

/** Header of a type 32 subtype 4 vertex buffer; its entry Reference is the vertex payload (type 40). */
[StructLayout(LayoutKind.Explicit, Size = 12)]
public struct VertexBufferHeader
{
    /** Every vertex buffer header holds this End. */
    public const uint EndValue = 0xDEADBEEF;

    /** Equals the payload length. */
    [FieldOffset(0)] public uint ByteCount;
    /** The payload length is a whole number of strides. */
    [FieldOffset(4)] public ushort Stride;
    /** 0 on every installed header. */
    [FieldOffset(6)] public ushort Unk6;
    [FieldOffset(8)] public uint End;
}

/** Header of a type 32 subtype 6 index buffer; its entry Reference is the index payload (type 40). */
[StructLayout(LayoutKind.Explicit, Size = 24)]
public struct IndexBufferHeader
{
    /** Every installed index buffer header holds 1 at +0. */
    [FieldOffset(0)] public byte Unk0;
    /** 1 for 32-bit indices, 0 for 16-bit ones. */
    [FieldOffset(1)] public byte Wide;
    /** Equals the payload length. */
    [FieldOffset(8)] public ulong ByteCount;
    /** VertexBufferHeader.EndValue on installed headers. */
    [FieldOffset(16)] public uint End;
}

/** Header of a type 32 subtype 7 constant buffer; its entry Reference is the constant payload (type 40). */
[StructLayout(LayoutKind.Explicit, Size = 16)]
public struct ConstantBufferHeader
{
    /** Equals the payload length. */
    [FieldOffset(0)] public uint ByteCount;
}

/** Header of a type 33 shader; its entry Reference is the bytecode (type 41). */
[StructLayout(LayoutKind.Explicit, Size = 40)]
public struct ShaderHeader
{
    /** Equals the header's own length, 40. */
    [FieldOffset(0)] public ulong HeaderSize;
    /** Equals the bytecode length. */
    [FieldOffset(8)] public uint BytecodeSize;
    [FieldOffset(12)] public uint UnkC;
}
