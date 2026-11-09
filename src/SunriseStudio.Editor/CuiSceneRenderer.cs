using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Numerics;
using System.Runtime.InteropServices;
using SunriseStudio.Formats;
using SunriseStudio.Packages;

namespace SunriseStudio.Editor;

/** Draws a composed CUI screen to a PNG with GDI+: bound bitmaps, text, labelled placeholders and the subclass grid overlay. */
public static class CuiSceneRenderer
{
    /** What to draw: a subtree (null for the whole screen), a pixel scale and an optional subclass grid overlay. */
    public sealed record Options(int? Node = null, float Scale = 1, IReadOnlyList<CuiSubclassSlot>? Overlay = null,
        bool Labels = false, bool ShowHidden = false);

    /** Result: the PNG bytes, canvas size and how many nodes were drawn, with and without resolved art. */
    public sealed record Result(byte[] Png, int Width, int Height, int Drawn, int WithArt, int Placeholders, int Hidden);

    public static Result Render(CuiPreviewData preview, IReadOnlyList<CuiSceneNode> nodes, Options options)
    {
        var byId = nodes.ToDictionary(node => node.Id);
        var children = nodes.Where(node => node.Id != 0).GroupBy(node => byId.ContainsKey(node.ParentId) ? node.ParentId : 0)
            .ToDictionary(group => group.Key, group => group.OrderBy(node => node.Id).ToList());
        // Subtree to draw, in tree order (parents before children, siblings by ID).
        List<CuiSceneNode> order = [];
        void Walk(int id, int depth)
        {
            if (depth > 256) return;
            foreach (var child in children.GetValueOrDefault(id, [])) { order.Add(child); Walk(child.Id, depth + 1); }
        }
        if (options.Node is { } root)
        {
            if (!byId.TryGetValue(root, out var start)) throw new InvalidDataException($"The screen has no node {root}.");
            order.Add(start); Walk(root, 0);
        }
        else Walk(0, 0);

        var view = new RectangleF(0, 0, 1920, 1080);
        if (options.Node is not null)
        {
            var rects = order.Where(node => node.Rectangle is not null && node.Visible).Select(node => node.Rectangle!.Value).ToList();
            if (rects.Count > 0)
            {
                float x0 = rects.Min(r => r.X), y0 = rects.Min(r => r.Y), x1 = rects.Max(r => r.X + r.Z), y1 = rects.Max(r => r.Y + r.W);
                view = new(x0 - 16, y0 - 16, Math.Max(32, x1 - x0 + 32), Math.Max(32, y1 - y0 + 32));
            }
        }
        float scale = Math.Clamp(options.Scale, .1f, 4);
        if (options.Node is not null) scale = Math.Min(scale, Math.Min(3840 / view.Width, 2160 / view.Height));
        int width = Math.Max(1, (int)MathF.Ceiling(view.Width * scale)), height = Math.Max(1, (int)MathF.Ceiling(view.Height * scale));
        if ((long)width * height > 40_000_000) throw new InvalidDataException("The render would be too large; lower scale.");

        using var canvas = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(canvas);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.FromArgb(255, 18, 22, 28));
        g.ScaleTransform(scale, scale);
        g.TranslateTransform(-view.X, -view.Y);
        using (var frame = new Pen(Color.FromArgb(120, 100, 88, 80), 2 / scale)) g.DrawRectangle(frame, 0, 0, 1920, 1080);

        Dictionary<uint, Bitmap?> bitmaps = [];
        int drawn = 0, art = 0, placeholders = 0, hidden = 0;
        try
        {
            using var outline = new Pen(Color.FromArgb(60, 120, 150, 180), 1 / scale);
            using var small = new Font("Segoe UI", Math.Max(6, 11 / scale), FontStyle.Regular, GraphicsUnit.Pixel);
            using var labelBrush = new SolidBrush(Color.FromArgb(200, 190, 205, 220));
            foreach (var node in order)
            {
                if (node.Rectangle is not { } r) continue;
                if (!node.Visible && !options.ShowHidden) { hidden++; continue; }
                var rect = new RectangleF(r.X, r.Y, r.Z, r.W);
                if (rect.Width <= 0 || rect.Height <= 0 || !rect.IntersectsWith(view)) continue;
                drawn++;
                if (node.TextureTag != 0 && Bitmap(preview, node.TextureTag, bitmaps) is { } image)
                {
                    using var attributes = new ImageAttributes();
                    var t = node.Tint ?? new Vector4(1, 1, 1, 1);
                    attributes.SetColorMatrix(new ColorMatrix([[t.X, 0, 0, 0, 0], [0, t.Y, 0, 0, 0], [0, 0, t.Z, 0, 0], [0, 0, 0, Math.Clamp(t.W, 0, 1), 0], [0, 0, 0, 0, 1]]));
                    g.DrawImage(image, Rectangle.Round(rect), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
                    art++;
                    continue;
                }
                if (node.Text.Length != 0)
                {
                    using var font = new Font("Segoe UI", Math.Clamp(rect.Height * .7f, 6, 96), FontStyle.Regular, GraphicsUnit.Pixel);
                    using var brush = new SolidBrush(Color.FromArgb(235, 232, 238, 245));
                    g.DrawString(node.Text, font, brush, rect, new StringFormat { Trimming = StringTrimming.EllipsisCharacter });
                    continue;
                }
                bool bitmapWithoutArt = node.TypeName == "Bitmap";
                bool unknown = !node.Known;
                if (unknown || bitmapWithoutArt)
                {
                    placeholders++;
                    using var fill = new SolidBrush(Color.FromArgb(unknown ? 22 : 14, 200, 140, 60));
                    using var dashed = new Pen(Color.FromArgb(140, 200, 140, 60), 1 / scale) { DashStyle = DashStyle.Dash };
                    g.FillRectangle(fill, rect); g.DrawRectangle(dashed, rect.X, rect.Y, rect.Width, rect.Height);
                    if (rect.Width * scale > 70 && rect.Height * scale > 16)
                        g.DrawString(unknown ? node.TypeName : "Bitmap (no art)", small, labelBrush, rect.X + 2, rect.Y + 1);
                }
                else g.DrawRectangle(outline, rect.X, rect.Y, rect.Width, rect.Height);
                if (options.Labels && rect.Width * scale > 90 && rect.Height * scale > 16)
                    g.DrawString(node.Id.ToString(), small, labelBrush, rect.X + 2, rect.Y + rect.Height - small.Height / 1.1f);
            }
            if (options.Overlay is { Count: > 0 } slots)
            {
                using var fill = new SolidBrush(Color.FromArgb(70, 70, 130, 230));
                using var pen = new Pen(Color.FromArgb(230, 120, 180, 255), 2 / scale);
                using var text = new SolidBrush(Color.White);
                using var font = new Font("Segoe UI", Math.Max(8, 12 / scale), FontStyle.Bold, GraphicsUnit.Pixel);
                var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                foreach (var slot in slots)
                {
                    var box = new RectangleF(slot.Rectangle.X, slot.Rectangle.Y, slot.Rectangle.Z, slot.Rectangle.W);
                    PointF[] diamond = [new(box.X + box.Width / 2, box.Y), new(box.Right, box.Y + box.Height / 2),
                        new(box.X + box.Width / 2, box.Bottom), new(box.X, box.Y + box.Height / 2)];
                    g.FillPolygon(fill, diamond); g.DrawPolygon(pen, diamond);
                    var rect = RectangleF.Inflate(box, -box.Width * .16f, -box.Height * .16f);
                    g.DrawString(slot.Label, font, text, rect, format);
                }
            }
        }
        finally { foreach (var image in bitmaps.Values) image?.Dispose(); }
        using var stream = new MemoryStream();
        canvas.Save(stream, ImageFormat.Png);
        return new(stream.ToArray(), width, height, drawn, art, placeholders, hidden);
    }

    private static Bitmap? Bitmap(CuiPreviewData preview, uint texture, Dictionary<uint, Bitmap?> cache)
    {
        if (cache.TryGetValue(texture, out var known)) return known;
        Bitmap? bitmap = null;
        try
        {
            if (preview.Textures.TryGetValue(texture, out var data))
            {
                var pixels = TexturePixels.Decode(data);
                bitmap = new Bitmap(pixels.Width, pixels.Height, PixelFormat.Format32bppArgb);
                var bits = bitmap.LockBits(new(0, 0, pixels.Width, pixels.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                var bgra = new byte[pixels.Pixels.Length];
                for (int i = 0; i + 3 < bgra.Length; i += 4)
                { bgra[i] = pixels.Pixels[i + 2]; bgra[i + 1] = pixels.Pixels[i + 1]; bgra[i + 2] = pixels.Pixels[i]; bgra[i + 3] = pixels.Pixels[i + 3]; }
                Marshal.Copy(bgra, 0, bits.Scan0, bgra.Length);
                bitmap.UnlockBits(bits);
            }
        }
        catch (Exception error) when (error is NotSupportedException or InvalidDataException or ArgumentException) { bitmap?.Dispose(); bitmap = null; }
        cache[texture] = bitmap;
        return bitmap;
    }
}
