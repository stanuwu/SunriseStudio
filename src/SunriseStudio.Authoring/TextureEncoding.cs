using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using SunriseStudio.Formats;

namespace SunriseStudio.Authoring;

/** The pixel formats an imported texture can be written in; each has stock headers to copy. */
public enum TextureEncoding
{
    /** DXGI 28, or 29 as sRGB; 32 bits per pixel. */
    Rgba8,
    /** DXGI 71, or 72 as sRGB; 4 bits per pixel, one bit of alpha. */
    Bc1,
    /** DXGI 77, or 78 as sRGB; 8 bits per pixel with interpolated alpha. */
    Bc3,
    /** DXGI 80; the red channel only, 4 bits per pixel. */
    Bc4,
    /** DXGI 83; red and green, 8 bits per pixel. */
    Bc5,
    /** DXGI 98, or 99 as sRGB; 8 bits per pixel. */
    Bc7
}

/** One encoded mip chain: its levels in order, largest first, as the native payload stores them. */
public sealed record EncodedTexture(uint Format, int Width, int Height, IReadOnlyList<byte[]> Mips)
{
    /** The payload: every level, largest first, with no padding between them. */
    public byte[] Payload() => Mips.SelectMany(level => level).ToArray();
}

/** Builds box-filtered mip chains and encodes them with BCnEncoder.NET. */
public static class TextureEncoder
{
    /** The DXGI format of an encoding; formats without an sRGB form ignore `srgb`. */
    public static uint Format(TextureEncoding encoding, bool srgb) => encoding switch
    {
        TextureEncoding.Rgba8 => srgb ? 29u : 28u,
        TextureEncoding.Bc1 => srgb ? 72u : 71u,
        TextureEncoding.Bc3 => srgb ? 78u : 77u,
        TextureEncoding.Bc4 => 80u,
        TextureEncoding.Bc5 => 83u,
        TextureEncoding.Bc7 => srgb ? 99u : 98u,
        _ => throw new ArgumentOutOfRangeException(nameof(encoding))
    };

    /** Levels of a full chain: floor(log2(max dimension)) + 1. */
    public static int FullMipCount(int width, int height) => 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)Math.Max(width, height));

    /**
     * Encodes `image` and, when `mips` is set, every smaller level down to 1 by 1.
     * @param srgb Colour channels average in linear light and the format takes its sRGB form.
     */
    public static EncodedTexture Encode(RgbaImage image, TextureEncoding encoding, bool srgb, bool mips = true,
        CompressionQuality quality = CompressionQuality.Balanced, CancellationToken token = default)
    {
        image.Validate();
        int count = mips ? FullMipCount(image.Width, image.Height) : 1;
        var levels = new List<byte[]>(count);
        var level = image;
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            levels.Add(EncodeLevel(level, encoding, quality));
            if (i + 1 < count) level = Half(level, srgb);
        }
        return new(Format(encoding, srgb), image.Width, image.Height, levels);
    }

    /** Bytes of one level: 4 by 4 blocks for BCn, rounded up, or 4 bytes per pixel. */
    public static long LevelBytes(TextureEncoding encoding, int width, int height) => encoding == TextureEncoding.Rgba8
        ? 4L * width * height
        : (long)((width + 3) / 4) * ((height + 3) / 4) * (encoding is TextureEncoding.Bc1 or TextureEncoding.Bc4 ? 8 : 16);

    private static byte[] EncodeLevel(RgbaImage level, TextureEncoding encoding, CompressionQuality quality)
    {
        if (encoding == TextureEncoding.Rgba8) return (byte[])level.Pixels.Clone();
        var encoder = new BcEncoder(encoding switch
        {
            TextureEncoding.Bc1 => CompressionFormat.Bc1WithAlpha,
            TextureEncoding.Bc3 => CompressionFormat.Bc3,
            TextureEncoding.Bc4 => CompressionFormat.Bc4,
            TextureEncoding.Bc5 => CompressionFormat.Bc5,
            _ => CompressionFormat.Bc7
        });
        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.OutputOptions.Quality = quality;
        byte[] encoded = encoder.EncodeToRawBytes(level.Pixels, level.Width, level.Height, PixelFormat.Rgba32)[0];
        if (encoded.LongLength != LevelBytes(encoding, level.Width, level.Height))
            throw new InvalidDataException("The texture encoder returned an unexpected level size.");
        return encoded;
    }

    /** The next level: each pixel averages its 2 by 2 source block; an odd edge repeats its last row or column. */
    private static RgbaImage Half(RgbaImage source, bool srgb)
    {
        int width = Math.Max(1, source.Width / 2), height = Math.Max(1, source.Height / 2);
        var output = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                for (int c = 0; c < 4; c++)
                {
                    float sum = 0;
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int sx = Math.Min(source.Width - 1, x * 2 + dx), sy = Math.Min(source.Height - 1, y * 2 + dy);
                            float value = source.Pixels[(sy * source.Width + sx) * 4 + c] / 255f;
                            sum += srgb && c < 3 ? ToLinear(value) : value;
                        }
                    float mean = sum / 4;
                    output[(y * width + x) * 4 + c] = (byte)MathF.Round(Math.Clamp(srgb && c < 3 ? ToSrgb(mean) : mean, 0, 1) * 255);
                }
        return new(width, height, output);
    }

    private static float ToLinear(float value) => value <= .04045f ? value / 12.92f : MathF.Pow((value + .055f) / 1.055f, 2.4f);
    private static float ToSrgb(float value) => value <= .0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1 / 2.4f) - .055f;
}
