using SunriseStudio.Formats;
using SunriseStudio.Rendering;

namespace SunriseStudio.Editor;

/** Window and loading artwork are embedded separately. */
public static class StudioBrand
{
    /** Loads the embedded Studio window icon. */
    public static Icon CreateIcon()
    {
        using var stream = Open("SunriseStudio.Icon");
        using var source = new Icon(stream, 32, 32);
        return (Icon)source.Clone();
    }

    /** The logo draws at many sizes; a mip chain keeps its thin rays whole when it is small. */
    public static GpuTexture CreateTexture(Dx11Renderer renderer)
    {
        var levels = ReadMips("SunriseStudio.Logo", out int width, out int height);
        return renderer.CreateMippedTexture(width, height, levels);
    }

    /** An embedded PNG and its mip chain, largest level first; level n is max(1, size >> n) on each side. */
    public static List<byte[]> ReadMips(string name, out int width, out int height)
    {
        using var stream = Open(name);
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var image = PngInterchange.Read(bytes.ToArray());
        var levels = new List<byte[]> { image.Pixels.ToArray() };
        int w = width = image.Width, h = height = image.Height;
        while (w > 1 || h > 1)
        {
            levels.Add(HalveRgba(levels[^1], w, h));
            w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
        }
        return levels;
    }

    /** The logo resampled once to a square drawn at exactly this many pixels, so it stays sharp when small. */
    public static GpuTexture CreateTexture(Dx11Renderer renderer, int pixels)
    {
        using var stream = Open("SunriseStudio.Logo");
        using var source = new Bitmap(stream);
        using var target = new Bitmap(pixels, pixels, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(target))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            using var wrap = new System.Drawing.Imaging.ImageAttributes();
            wrap.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
            graphics.DrawImage(source, new Rectangle(0, 0, pixels, pixels), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, wrap);
        }
        var data = target.LockBits(new Rectangle(0, 0, pixels, pixels), System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var rgba = new byte[pixels * pixels * 4];
        try
        {
            for (int y = 0; y < pixels; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, rgba, y * pixels * 4, pixels * 4);
        }
        finally { target.UnlockBits(data); }
        // GDI+ stores BGRA.
        for (int i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
        return renderer.CreateTexture(pixels, pixels, rgba);
    }

    /** Box filter weighted by alpha, so transparent texels do not darken the edges. */
    private static byte[] HalveRgba(byte[] source, int width, int height)
    {
        int w = Math.Max(1, width / 2), h = Math.Max(1, height / 2);
        var result = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int i = (Math.Min(height - 1, y * 2 + dy) * width + Math.Min(width - 1, x * 2 + dx)) * 4;
                        float alpha = source[i + 3];
                        r += source[i] * alpha; g += source[i + 1] * alpha; b += source[i + 2] * alpha; a += alpha;
                    }
                int o = (y * w + x) * 4;
                if (a > 0) { result[o] = (byte)MathF.Round(r / a); result[o + 1] = (byte)MathF.Round(g / a); result[o + 2] = (byte)MathF.Round(b / a); }
                result[o + 3] = (byte)MathF.Round(a / 4);
            }
        return result;
    }

    /**
     * A vector stand-in for the logo (a sun disc in the Brand colour with a ring), for the menu bar's 16 px mark when
     * the texture is not made yet. The UE5 theme keeps the old Sol orange for the brand only (SolTheme.Brand).
     */
    public static void DrawMark(ImGuiNET.ImDrawListPtr draw, System.Numerics.Vector2 min, float size)
    {
        var centre = min + new System.Numerics.Vector2(size / 2);
        draw.AddCircleFilled(centre, size * .28f, SunriseStudio.Viewport.SolTheme.U32(SunriseStudio.Viewport.SolTheme.Brand), 16);
        draw.AddCircle(centre, size * .44f, SunriseStudio.Viewport.SolTheme.U32(SunriseStudio.Viewport.SolTheme.BrandHi), 20, Math.Max(1, size * .08f));
    }

    private static Stream Open(string name) => typeof(StudioBrand).Assembly.GetManifestResourceStream(name)
        ?? throw new InvalidDataException($"The embedded {name} artwork is missing.");
}
