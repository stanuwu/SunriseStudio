using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;

namespace SunriseStudio.Animation;

/** Skin weights packed in a GPU position stream; this is vertex data, not a tag. */
public static class SkinPositionStream
{
    /** Stride of a position stream of four snorm16 words; word 3 holds the palette index. */
    public const int PaletteStride = 8;

    /** @return The i16 palette index of vertex `vertex` in a stream of PaletteStride rows. */
    public static int PaletteIndex(ReadOnlySpan<byte> positions, uint vertex) =>
        BinaryPrimitives.ReadInt16LittleEndian(positions[checked((int)vertex * PaletteStride + 6)..]);

    /**
     * @param stride 8 holds one i16 bone at +6; 12 and 16 hold two or four byte bones after byte weights at +8.
     * @return The weights of vertex `vertex`; byte weights are divided by 255.
     */
    internal static VertexSkinWeights Weights(byte[] positions, int vertex, int stride)
    {
        int offset = vertex * stride;
        return stride switch
        {
            8 => new VertexSkinWeights(I16(positions, offset + 6), 0, 0, 0, new Vector4(1, 0, 0, 0)),
            12 => new VertexSkinWeights(positions[offset + 8], positions[offset + 9], 0, 0,
                new Vector4(positions[offset + 10], positions[offset + 11], 0, 0) / 255f),
            16 => new VertexSkinWeights(positions[offset + 12], positions[offset + 13],
                positions[offset + 14], positions[offset + 15], new Vector4(positions[offset + 8],
                    positions[offset + 9], positions[offset + 10], positions[offset + 11]) / 255f),
            _ => throw new UnreachableException()
        };
    }

    private static ReadOnlySpan<byte> Slice(byte[] data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length)
            throw new InvalidDataException(ReadMessages.Skin.Range);
        return data.AsSpan(offset, length);
    }

    private static short I16(byte[] data, int offset) => BinaryPrimitives.ReadInt16LittleEndian(Slice(data, offset, 2));
}
