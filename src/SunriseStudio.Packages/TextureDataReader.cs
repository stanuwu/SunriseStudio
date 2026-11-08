using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Packages;

public enum TextureKind { Texture2D, Cube, Volume }

public sealed record TextureSubresourceData(int MipLevel, int ArraySlice, int Width, int Height,
    int Depth, int RowPitch, int SlicePitch, byte[] Data);

/** Subresources use D3D11 order: mip level plus array slice times mip count. */
public sealed record TextureData(uint TagHash, int Format, int Width, int Height, int Depth,
    int ArraySize, int MipCount, int LargeMipCount, TextureKind Kind, string LayoutStatus,
    IReadOnlyList<TextureSubresourceData> Subresources)
{
    public bool IsCube => Kind == TextureKind.Cube;
    public bool IsVolume => Kind == TextureKind.Volume;
    public long ByteCount => Subresources.Sum(x => (long)x.Data.Length);
}

/** Package data stores each mip's slices together, with streamed mips in a separate tag. */
public sealed class TextureDataReader(PackageCatalog catalog)
{
    /**
     * Decode every authored mip and reject unaccounted bytes rather than discarding them.
     * @param residentOnly Leave out the streamed mips in the StreamedTag tag, as the game draws before they stream in.
     */
    public TextureData Read(uint tag, bool residentOnly = false)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Type != 32
            || entry.SubType is < 1 or > 3)
            throw new NotSupportedException("Tag is not a pre-BL texture header.");
        byte[] header = catalog.Read(tag);
        if (header.Length != Unsafe.SizeOf<TextureHeader>() || U16(header, 0x0C) != 0xCAFE)
            throw new InvalidDataException("Texture header differs from the supported pre-BL layout.");
        int format = checked((int)U32(header, 4));
        int width = U16(header, 0x0E), height = U16(header, 0x10);
        int depth = U16(header, 0x12), arraySize = U16(header, 0x14);
        int mipCount = header[0x17], largeMips = header[0x18];
        TextureKind kind = entry.SubType switch
        {
            1 => TextureKind.Texture2D,
            2 => TextureKind.Cube,
            3 => TextureKind.Volume,
            _ => throw new NotSupportedException("Unknown texture package subtype.")
        };
        ValidateDimensions(kind, width, height, depth, arraySize, mipCount);
        if (kind == TextureKind.Texture2D && arraySize != 1)
            throw new NotSupportedException("Authored 2D array ordering has not been verified for this build.");
        if (kind == TextureKind.Cube && arraySize != 6)
            throw new NotSupportedException("Cube-array ordering has not been verified for this build.");
        uint largeTag = TagView.At<TextureHeader>(header).StreamedTag;
        bool hasLarge = largeTag is not (0 or uint.MaxValue);
        if (largeMips > mipCount || hasLarge != (largeMips > 0))
            throw new InvalidDataException("Texture streamed-mip count disagrees with its payload reference.");
        int slices = kind == TextureKind.Volume ? 1 : arraySize;
        long largeBytes = 0;
        for (int mip = 0; mip < largeMips; mip++) largeBytes += MipBytes(format, kind, width, height, depth, mip) * slices;
        bool skipLarge = residentOnly && largeMips > 0;
        byte[] tail = catalog.Read(entry.Reference);
        byte[] large = hasLarge && !skipLarge ? catalog.Read(largeTag) : [];
        if ((skipLarge ? largeBytes : large.Length) + tail.Length != U32(header, 0))
            throw new InvalidDataException("Texture payload sizes disagree with the declared total.");
        int first = skipLarge ? largeMips : 0, mips = mipCount - first;
        var output = new TextureSubresourceData[checked(mips * slices)];
        int largeOffset = 0, tailOffset = 0;
        for (int mip = first; mip < mipCount; mip++)
        {
            int w = Math.Max(1, width >> mip), h = Math.Max(1, height >> mip);
            int d = kind == TextureKind.Volume ? Math.Max(1, depth >> mip) : 1;
            var (rowPitch, slicePitch) = GetPitch(format, w, h);
            int byteCount = checked(slicePitch * d);
            byte[] source = mip < largeMips ? large : tail;
            ref int cursor = ref (mip < largeMips ? ref largeOffset : ref tailOffset);
            for (int slice = 0; slice < slices; slice++)
            {
                if (cursor > source.Length - byteCount)
                    throw new InvalidDataException($"Texture mip {mip}, slice {slice} exceeds its payload.");
                output[mip - first + slice * mips] = new TextureSubresourceData(mip - first, slice,
                    w, h, d, rowPitch, slicePitch, source.AsSpan(cursor, byteCount).ToArray());
                cursor = checked(cursor + byteCount);
            }
        }
        if (largeOffset != large.Length || tailOffset != tail.Length)
            throw new InvalidDataException("Texture mip layout leaves unaccounted payload bytes.");
        string status = kind switch
        {
            _ when skipLarge => "Resident mips only; the streamed mips at header +0x24 are left out.",
            TextureKind.Cube => "Mip-major faces verified against installed cube mip images.",
            TextureKind.Volume => "Volume kind and depth slices follow the package header; native kind verification is pending.",
            _ => "All authored mip bytes accounted for across streamed and resident payloads."
        };
        return new TextureData(tag, format, Math.Max(1, width >> first), Math.Max(1, height >> first),
            kind == TextureKind.Volume ? Math.Max(1, depth >> first) : depth, arraySize, mips, largeMips - first,
            kind, status, Array.AsReadOnly(output));
    }

    /** Bytes of the streamed mips (header +0x18 of them), from the header alone. */
    public long StreamedByteCount(uint tag)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Type != 32 || entry.SubType is < 1 or > 3)
            throw new NotSupportedException("Tag is not a pre-BL texture header.");
        byte[] header = catalog.Read(tag);
        if (header.Length != Unsafe.SizeOf<TextureHeader>() || U16(header, 0x0C) != 0xCAFE)
            throw new InvalidDataException("Texture header differs from the supported pre-BL layout.");
        var kind = entry.SubType == 3 ? TextureKind.Volume : entry.SubType == 2 ? TextureKind.Cube : TextureKind.Texture2D;
        int format = checked((int)U32(header, 4)), slices = kind == TextureKind.Volume ? 1 : U16(header, 0x14);
        long bytes = 0;
        for (int mip = 0; mip < Math.Min((int)header[0x18], header[0x17]); mip++)
            bytes += MipBytes(format, kind, U16(header, 0x0E), U16(header, 0x10), U16(header, 0x12), mip) * slices;
        return bytes;
    }

    private static long MipBytes(int format, TextureKind kind, int width, int height, int depth, int mip)
    {
        var (_, slicePitch) = GetPitch(format, Math.Max(1, width >> mip), Math.Max(1, height >> mip));
        return (long)slicePitch * (kind == TextureKind.Volume ? Math.Max(1, depth >> mip) : 1);
    }
    /** BC images retain one complete block at dimensions smaller than four pixels. */
    public static (int RowPitch, int SlicePitch) GetPitch(int format, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        int blockBytes = format switch
        {
            >= 70 and <= 72 or >= 79 and <= 81 => 8,
            >= 73 and <= 78 or >= 82 and <= 84 or >= 94 and <= 99 => 16,
            _ => 0
        };
        if (blockBytes != 0)
        {
            int row = checked(Math.Max(1, (width + 3) / 4) * blockBytes);
            return (row, checked(row * Math.Max(1, (height + 3) / 4)));
        }
        int bytes = format switch
        {
            >= 1 and <= 4 => 16,
            >= 5 and <= 8 => 12,
            >= 9 and <= 22 => 8,
            >= 23 and <= 47 or 67 or >= 87 and <= 93 => 4,
            >= 48 and <= 59 or 85 or 86 or 115 => 2,
            >= 60 and <= 65 => 1,
            _ => throw new NotSupportedException($"DXGI texture format {format} has no supported byte layout.")
        };
        int pitch = checked(width * bytes);
        return (pitch, checked(pitch * height));
    }

    /** Array and depth extents have different D3D11 limits and cannot be interchanged. */
    private static void ValidateDimensions(TextureKind kind, int width, int height, int depth,
        int arraySize, int mipCount)
    {
        if (width is < 1 or > 16384 || height is < 1 or > 16384 || depth < 1
            || arraySize is < 1 or > 2048)
            throw new InvalidDataException("Texture dimensions are outside supported D3D11 limits.");
        if (kind == TextureKind.Volume && (width > 2048 || height > 2048 || depth > 2048 || arraySize != 1))
            throw new InvalidDataException("Volume dimensions or array count are invalid.");
        if (kind != TextureKind.Volume && depth != 1)
            throw new InvalidDataException("A 2D or cube texture cannot carry volume depth.");
        if (kind == TextureKind.Cube && (width != height || arraySize % 6 != 0))
            throw new InvalidDataException("Cube textures require square faces in groups of six.");
        int maximum = Math.Max(width, Math.Max(height, kind == TextureKind.Volume ? depth : 1));
        int maximumMips = 1;
        while ((maximum >>= 1) != 0) maximumMips++;
        if (mipCount < 1 || mipCount > maximumMips)
            throw new InvalidDataException("Texture mip count exceeds its dimensions.");
    }

    private static ushort U16(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
}
