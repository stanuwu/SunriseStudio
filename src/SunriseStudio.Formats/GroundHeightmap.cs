using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace SunriseStudio.Formats;

/** Heightmap samples 0..1, top row first (the ground's high-Y edge), and the range a PNG recorded. */
public sealed record GroundHeightmapImage(int Width, int Height, double[] Values, (double Low, double High)? SavedRange);

/**
 * 16-bit heightmaps as World Studio's ground_ops writes and reads them: a grayscale PNG with its height
 * range in a tEXt chunk keyed sunrise.heights, or little-endian unsigned 16-bit RAW (.r16, .raw).
 * One sample per ground point; 0..65535 spans the lowest to the highest point.
 */
public static class GroundHeightmap
{
    public const string RangeKey = "sunrise.heights";
    public const int MaximumSide = 4096;
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /** 16-bit values top row first, and the range they span relative to the ground's origin. */
    public static (ushort[] Values, double Low, double High) Export(GroundGrid grid)
    {
        double low = grid.Heights.Min(), high = grid.Heights.Max(), span = high - low;
        var values = new ushort[grid.PointCount];
        int at = 0;
        for (int r = grid.Rows - 1; r >= 0; r--)
            for (int c = 0; c < grid.Columns; c++)
                values[at++] = span <= 0 ? (ushort)0 : (ushort)Math.Round((grid.Height(c, r) - low) / span * 65535);
        return (values, low, high);
    }

    public static byte[] WriteRaw(GroundGrid grid)
    {
        var (values, _, _) = Export(grid);
        var bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2 * i), values[i]);
        return bytes;
    }

    /** A 16-bit grayscale PNG that records its range, so importing it back needs none. */
    public static byte[] WritePng(GroundGrid grid)
    {
        var (values, low, high) = Export(grid);
        using var output = new MemoryStream();
        output.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)grid.Columns);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)grid.Rows);
        header[8] = 16;
        PngInterchange.Chunk(output, "IHDR", header);
        string text = JsonSerializer.Serialize(new SortedDictionary<string, double>(StringComparer.Ordinal)
        {
            ["columns"] = grid.Columns,
            ["depth_m"] = grid.Depth,
            ["high"] = Math.Round(high, 5),
            ["low"] = Math.Round(low, 5),
            ["rows"] = grid.Rows,
            ["width_m"] = grid.Width
        });
        PngInterchange.Chunk(output, "tEXt", [.. Encoding.Latin1.GetBytes(RangeKey), 0, .. Encoding.Latin1.GetBytes(text)]);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true))
        {
            var line = new byte[1 + grid.Columns * 2];
            for (int y = 0; y < grid.Rows; y++)
            {
                for (int x = 0; x < grid.Columns; x++) BinaryPrimitives.WriteUInt16BigEndian(line.AsSpan(1 + 2 * x), values[y * grid.Columns + x]);
                zlib.Write(line);
            }
        }
        PngInterchange.Chunk(output, "IDAT", compressed.ToArray());
        PngInterchange.Chunk(output, "IEND", []);
        return output.ToArray();
    }

    /** Reads a PNG (8- or 16-bit grayscale, RGB or RGBA; colour reads red) or RAW file. */
    public static GroundHeightmapImage Read(string path, int? columns = null, int? rows = null)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new InvalidDataException($"No heightmap file at {path}.");
        if (info.Length > 64 * 1024 * 1024) throw new InvalidDataException("The heightmap file is larger than 64 MB.");
        byte[] data = File.ReadAllBytes(path);
        if (data.AsSpan().StartsWith(Signature)) return ReadPng(data);
        if (Path.GetExtension(path).ToLowerInvariant() is not (".r16" or ".raw"))
            throw new InvalidDataException("Heightmaps are 16-bit PNG, .r16 or .raw files.");
        return ReadRaw(data, columns, rows);
    }

    /** Without a size a RAW heightmap must be square. */
    public static GroundHeightmapImage ReadRaw(ReadOnlySpan<byte> data, int? columns = null, int? rows = null)
    {
        if (data.Length % 2 != 0) throw new InvalidDataException("A 16-bit RAW heightmap has an even number of bytes.");
        int count = data.Length / 2;
        if (columns is null && rows is null)
        {
            int side = (int)Math.Sqrt(count);
            while ((long)side * side > count) side--;
            while ((long)(side + 1) * (side + 1) <= count) side++;
            if (side * side != count) throw new InvalidDataException($"This RAW file holds {count} samples, which is not square. Give its columns and rows.");
            columns = rows = side;
        }
        if (columns is not { } width || rows is not { } height || (long)width * height != count)
            throw new InvalidDataException($"This RAW file holds {count} samples, not {columns} x {rows}.");
        if (width is < 1 or > MaximumSide || height is < 1 or > MaximumSide)
            throw new InvalidDataException($"A heightmap is at most {MaximumSide} samples on a side.");
        var values = new double[count];
        for (int i = 0; i < count; i++) values[i] = BinaryPrimitives.ReadUInt16LittleEndian(data[(2 * i)..]) / 65535.0;
        return new(width, height, values, null);
    }

    public static GroundHeightmapImage ReadPng(ReadOnlySpan<byte> data)
    {
        if (!data.StartsWith(Signature)) throw new InvalidDataException("Not a PNG file.");
        int width = 0, height = 0, depth = 0, colour = -1;
        using var compressed = new MemoryStream();
        var text = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int at = 8; at + 12 <= data.Length;)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[at..]));
            if (length < 0 || at + 12 + length > data.Length) throw new InvalidDataException("The PNG file is cut short.");
            string kind = Encoding.ASCII.GetString(data.Slice(at + 4, 4));
            var body = data.Slice(at + 8, length);
            if (PngInterchange.Crc(data.Slice(at + 4, length + 4)) != BinaryPrimitives.ReadUInt32BigEndian(data[(at + 8 + length)..]))
                throw new InvalidDataException("PNG checksum mismatch.");
            if (kind == "IHDR")
            {
                if (length != 13) throw new InvalidDataException("Invalid PNG header.");
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(body));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]));
                depth = body[8]; colour = body[9];
                if (body[12] != 0) throw new InvalidDataException("Interlaced PNG files are not read. Save it without interlacing.");
            }
            else if (kind == "IDAT") compressed.Write(body);
            else if (kind == "tEXt" && body.IndexOf((byte)0) is var zero and >= 0)
                text[Encoding.Latin1.GetString(body[..zero])] = Encoding.Latin1.GetString(body[(zero + 1)..]);
            else if (kind == "IEND") break;
            at += 12 + length;
        }
        int channels = colour switch { 0 => 1, 2 => 3, 4 => 2, 6 => 4, _ => 0 };
        if (channels == 0 || depth is not (8 or 16))
            throw new InvalidDataException("Use an 8- or 16-bit grayscale, RGB or RGBA PNG. Paletted images are not read.");
        if (width is < 1 or > MaximumSide || height is < 1 or > MaximumSide)
            throw new InvalidDataException($"A heightmap is at most {MaximumSide} pixels on a side.");
        int size = depth / 8, bpp = channels * size, stride = width * bpp;
        compressed.Position = 0;
        var raw = new byte[checked(height * (stride + 1))];
        using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress))
        {
            int read = 0;
            while (read < raw.Length && zlib.Read(raw, read, raw.Length - read) is var n and > 0) read += n;
            if (read < raw.Length) throw new InvalidDataException("The PNG image data is cut short.");
        }
        var previous = new byte[stride];
        var values = new double[width * height];
        double top = depth == 16 ? 65535.0 : 255.0;
        for (int y = 0; y < height; y++)
        {
            int kind = raw[y * (stride + 1)];
            var line = raw.AsSpan(y * (stride + 1) + 1, stride);
            for (int i = 0; i < stride; i++)
            {
                int left = i >= bpp ? line[i - bpp] : 0, up = previous[i], corner = i >= bpp ? previous[i - bpp] : 0;
                line[i] = kind switch
                {
                    0 => line[i],
                    1 => (byte)(line[i] + left),
                    2 => (byte)(line[i] + up),
                    3 => (byte)(line[i] + (left + up) / 2),
                    4 => (byte)(line[i] + Paeth(left, up, corner)),
                    _ => throw new InvalidDataException("The PNG uses an unknown row filter.")
                };
            }
            for (int x = 0; x < width; x++)
            {
                int at = x * bpp;
                values[y * width + x] = (size == 2 ? line[at] << 8 | line[at + 1] : line[at]) / top;
            }
            line.CopyTo(previous);
        }
        (double, double)? range = null;
        if (text.TryGetValue(RangeKey, out var saved))
            try
            {
                using var json = JsonDocument.Parse(saved);
                range = (json.RootElement.GetProperty("low").GetDouble(), json.RootElement.GetProperty("high").GetDouble());
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { range = null; }
        return new(width, height, values, range);
    }

    /** The grid's heights from an image mapped onto [low, high], resampled onto the grid's own points. */
    public static double[] Apply(GroundGrid grid, GroundHeightmapImage image, double low, double high)
    {
        if (!double.IsFinite(low) || !double.IsFinite(high) || low > high)
            throw new InvalidDataException("The low height must be finite and not above the high one.");
        var output = new double[grid.PointCount];
        for (int r = 0; r < grid.Rows; r++)
        {
            double fy = (double)(grid.Rows - 1 - r) / (grid.Rows - 1) * (image.Height - 1);
            for (int c = 0; c < grid.Columns; c++)
            {
                double fx = (double)c / (grid.Columns - 1) * (image.Width - 1);
                output[r * grid.Columns + c] = low + (high - low) * GroundBrush.Bilinear(image.Values, image.Width, image.Height, fx, fy);
            }
        }
        GroundBrush.CheckHeights(output, "This heightmap range");
        return output;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
