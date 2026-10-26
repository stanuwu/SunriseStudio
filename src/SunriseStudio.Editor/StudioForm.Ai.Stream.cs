using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Solforge.Mcp;
using SunriseStudio.Rendering;

namespace SunriseStudio.Editor;

/**
 * The MCP live viewport: while anyone watches (a frame tool call in the last two minutes or a local browser
 * stream), every drawn frame steps the renderer's staging ring (Dx11Renderer.GrabCycle) at the feed's rate, and
 * finished copies of the viewport region go to one worker that measures the change, scales and JPEG-encodes them into
 * the ViewportFeed. A worker still busy drops the frame rather than queueing; nothing runs while nobody watches or
 * while context publishing is paused.
 */
public sealed partial class StudioForm
{
    private long aiStreamFrame, aiStreamNextAt;
    private int aiStreamBusy;
    private bool aiStreamIdle = true;

    /** Copies the viewport region for the live feed when someone is watching. */
    private void StreamAiViewport()
    {
        if (aiHost is not { } host || renderer is null) return;
        var feed = host.Viewport;
        feed.Paused = aiPaused;
        bool blocked = aiPaused;
        if (blocked || !feed.Wanted)
        {
            if (blocked && feed.Latest is not null) feed.Clear();
            feed.Status = aiPaused ? "paused (context publishing is off in the AI workspace)" : "idle";
            if (!aiStreamIdle) { renderer.ReleaseGrabs(); aiStreamIdle = true; }
            return;
        }
        aiStreamIdle = false;
        aiStreamFrame++;
        Rectangle? crop = null;
        long now = Environment.TickCount64;
        if (viewport is { IsRendering: true } view && view.ScreenSize.X >= 2 && view.ScreenSize.Y >= 2)
        {
            feed.Status = "capturing";
            if (now >= aiStreamNextAt)
            {
                crop = Rectangle.Round(new RectangleF(view.ScreenOrigin.X, view.ScreenOrigin.Y, view.ScreenSize.X, view.ScreenSize.Y));
                aiStreamNextAt = now + 1000 / feed.Fps;
            }
        }
        else feed.Status = viewport is null ? "no viewport (open a map with open_map)" : "the 3D viewport is off or hidden (set_viewport_rendering)";
        if (renderer.GrabCycle(crop) is not { } pixels) return;
        if (Interlocked.CompareExchange(ref aiStreamBusy, 1, 0) != 0) return;
        long frame = aiStreamFrame;
        int width = feed.MaxWidth, quality = feed.Quality;
        _ = Task.Run(() =>
        {
            try { EncodeAiFrame(feed, pixels, frame, width, quality); }
            catch (Exception exception) when (exception is ArgumentException or ExternalException or InvalidOperationException or OutOfMemoryException)
            { feed.Status = "capture error: " + exception.Message; }
            finally { Volatile.Write(ref aiStreamBusy, 0); }
        });
    }

    /** Scales and JPEG-encodes a grabbed frame into the feed on a worker. */
    private static void EncodeAiFrame(ViewportFeed feed, Dx11Renderer.GrabbedPixels pixels, long frame, int maxWidth, int quality)
    {
        byte[] grid = ViewportFeed.Measure(pixels.Bgra, pixels.Width, pixels.Height, pixels.Width * 4);
        feed.Offer(grid, frame, () =>
        {
            var handle = GCHandle.Alloc(pixels.Bgra, GCHandleType.Pinned);
            try
            {
                // 32bppRgb ignores the back buffer's alpha, which may be zero.
                using var source = new Bitmap(pixels.Width, pixels.Height, pixels.Width * 4, PixelFormat.Format32bppRgb, handle.AddrOfPinnedObject());
                int width = Math.Min(maxWidth, pixels.Width);
                int height = Math.Max(1, (int)Math.Round(pixels.Height * (double)width / pixels.Width));
                using var scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                using (var graphics = Graphics.FromImage(scaled))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    graphics.DrawImage(source, 0, 0, width, height);
                }
                using var stream = new MemoryStream();
                using var parameters = new EncoderParameters(1);
                parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                scaled.Save(stream, JpegCodec, parameters);
                return (stream.ToArray(), width, height);
            }
            finally { handle.Free(); }
        });
    }

    /** The JPEG encoder used for frames. */
    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
}
