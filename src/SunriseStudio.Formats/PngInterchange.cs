using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

public sealed record RgbaImage(int Width, int Height, byte[] Pixels)
{
    public void Validate()
    {
        if (Width is < 1 or > 16384 || Height is < 1 or > 16384
            || (long)Width * Height > 64 * 1024 * 1024 || Pixels.Length != checked(Width * Height * 4))
            throw new InvalidDataException("Invalid RGBA image dimensions or byte count.");
    }
}

/** PNG import accepts non-interlaced 8-bit grayscale, RGB, indexed and RGBA images. */
public static class PngInterchange
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static void Write(string path, RgbaImage image) => File.WriteAllBytes(path, Write(image));
    public static RgbaImage Read(string path) => Read(File.ReadAllBytes(path));

    /** PNG uses network byte order and a checksum for each chunk. */
    public static byte[] Write(RgbaImage image)
    {
        image.Validate();
        using var result = new MemoryStream();
        result.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)image.Height);
        header[8] = 8; header[9] = 6;
        Chunk(result, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, true))
            for (int y = 0; y < image.Height; y++)
            {
                zlib.WriteByte(0);
                zlib.Write(image.Pixels.AsSpan(y * image.Width * 4, image.Width * 4));
            }
        Chunk(result, "IDAT", compressed.ToArray());
        Chunk(result, "IEND", []);
        return result.ToArray();
    }

    /** Decompressed bytes are bounded by the image header before any pixel allocation. */
    public static RgbaImage Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8 || !bytes[..8].SequenceEqual(Signature))
            throw new InvalidDataException("Invalid PNG signature.");
        int width = 0, height = 0, channels = 0, color = 0;
        byte[] palette = [], alpha = [];
        bool headerSeen = false, ended = false;
        using var compressed = new MemoryStream();
        for (int p = 8; p < bytes.Length;)
        {
            if (p > bytes.Length - 12) throw new InvalidDataException("Truncated PNG chunk.");
            int count = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes[p..]));
            if (count > bytes.Length - p - 12) throw new InvalidDataException("PNG chunk leaves the file.");
            var type = bytes.Slice(p + 4, 4);
            var data = bytes.Slice(p + 8, count);
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(bytes[(p + 8 + count)..]);
            if (Crc(bytes.Slice(p + 4, count + 4)) != crc) throw new InvalidDataException("PNG checksum mismatch.");
            string name = Encoding.ASCII.GetString(type);
            if (!headerSeen && name != "IHDR") throw new InvalidDataException("PNG header must come first.");
            switch (name)
            {
                case "IHDR":
                    if (headerSeen || count != 13) throw new InvalidDataException("Invalid PNG header.");
                    width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data));
                    height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]));
                    color = data[9];
                    channels = color switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
                    if (width is < 1 or > 16384 || height is < 1 or > 16384 || (long)width * height > 64 * 1024 * 1024)
                        throw new InvalidDataException("PNG dimensions exceed image limits.");
                    if (data[8] != 8 || channels == 0 || data[10] != 0 || data[11] != 0 || data[12] != 0)
                        throw new NotSupportedException("PNG needs non-interlaced 8-bit pixels.");
                    headerSeen = true;
                    break;
                case "PLTE": palette = data.ToArray(); break;
                case "tRNS": alpha = data.ToArray(); break;
                case "IDAT": compressed.Write(data); break;
                case "IEND":
                    if (count != 0 || p + 12 != bytes.Length) throw new InvalidDataException("Invalid PNG end chunk.");
                    ended = true; break;
                default:
                    if ((type[0] & 32) == 0) throw new NotSupportedException($"Unsupported PNG chunk {name}.");
                    break;
            }
            p += checked(count + 12);
        }
        if (!ended || !headerSeen) throw new InvalidDataException("PNG is incomplete.");
        if (color == 3 && (palette.Length == 0 || palette.Length % 3 != 0 || palette.Length > 768))
            throw new InvalidDataException("Invalid PNG palette.");
        int row = checked(width * channels);
        var filtered = new byte[checked((row + 1) * height)];
        compressed.Position = 0;
        using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress))
        {
            zlib.ReadExactly(filtered);
            if (zlib.ReadByte() != -1) throw new InvalidDataException("PNG contains excess pixel data.");
        }
        var unpacked = new byte[checked(row * height)];
        for (int y = 0; y < height; y++)
        {
            int filter = filtered[y * (row + 1)];
            if (filter > 4) throw new InvalidDataException("Invalid PNG filter.");
            for (int x = 0; x < row; x++)
            {
                int a = x >= channels ? unpacked[y * row + x - channels] : 0;
                int b = y > 0 ? unpacked[(y - 1) * row + x] : 0;
                int c = y > 0 && x >= channels ? unpacked[(y - 1) * row + x - channels] : 0;
                int predictor = filter switch { 1 => a, 2 => b, 3 => (a + b) / 2, 4 => Paeth(a, b, c), _ => 0 };
                unpacked[y * row + x] = unchecked((byte)(filtered[y * (row + 1) + 1 + x] + predictor));
            }
        }
        var pixels = new byte[checked(width * height * 4)];
        for (int i = 0; i < width * height; i++)
        {
            int p = i * channels, q = i * 4;
            byte r, g, b, a = 255;
            if (color == 3)
            {
                int index = unpacked[p];
                if (index >= palette.Length / 3) throw new InvalidDataException("PNG palette index is invalid.");
                r = palette[index * 3]; g = palette[index * 3 + 1]; b = palette[index * 3 + 2];
                if (index < alpha.Length) a = alpha[index];
            }
            else if (color is 0 or 4)
            {
                r = g = b = unpacked[p];
                if (color == 4) a = unpacked[p + 1];
                else if (alpha.Length == 2 && r == BinaryPrimitives.ReadUInt16BigEndian(alpha)) a = 0;
            }
            else
            {
                r = unpacked[p]; g = unpacked[p + 1]; b = unpacked[p + 2];
                if (color == 6) a = unpacked[p + 3];
                else if (alpha.Length == 6 && r == BinaryPrimitives.ReadUInt16BigEndian(alpha)
                    && g == BinaryPrimitives.ReadUInt16BigEndian(alpha.AsSpan(2))
                    && b == BinaryPrimitives.ReadUInt16BigEndian(alpha.AsSpan(4))) a = 0;
            }
            pixels[q] = r; pixels[q + 1] = g; pixels[q + 2] = b; pixels[q + 3] = a;
        }
        return new(width, height, pixels);
    }

    public static TextureData ToTexture(RgbaImage image)
    {
        image.Validate();
        return new(0, 28, image.Width, image.Height, 1, 1, 1, 0, TextureKind.Texture2D,
            "RGBA8", [new(0, 0, image.Width, image.Height, 1, image.Width * 4,
                image.Width * image.Height * 4, (byte[])image.Pixels.Clone())]);
    }

    /** PNG replacement preserves the original format and requires a single uncompressed 2D mip. */
    public static TextureData ToTexture(RgbaImage image, TextureData original)
    {
        image.Validate();
        if (original.Kind != TextureKind.Texture2D || original.ArraySize != 1 || original.MipCount != 1
            || original.Width != image.Width || original.Height != image.Height)
            throw new NotSupportedException("PNG reimport needs the original dimensions and a single 2D mip. Use DDS for mip chains.");
        if (original.Format is not (28 or 29 or 87 or 91))
            throw new NotSupportedException("PNG reimport supports RGBA8 and BGRA8 textures. Use DDS for compressed textures.");
        var pixels = (byte[])image.Pixels.Clone();
        if (original.Format is 87 or 91)
            for (int i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        return original with { Subresources = [original.Subresources[0] with { Data = pixels }] };
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    internal static void Chunk(Stream stream, string name, ReadOnlySpan<byte> data)
    {
        var record = new byte[checked(data.Length + 12)];
        BinaryPrimitives.WriteUInt32BigEndian(record, (uint)data.Length);
        Encoding.ASCII.GetBytes(name, record.AsSpan(4, 4));
        data.CopyTo(record.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(data.Length + 8), Crc(record.AsSpan(4, data.Length + 4)));
        stream.Write(record);
    }

    internal static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0);
        }
        return ~crc;
    }
}
