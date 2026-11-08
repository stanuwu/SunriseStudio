using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Rendering;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/**
 * Item icons for grids and lists, read on a worker as they are drawn and uploaded a few per frame (see IconStream). StudioForm
 * owns the one cache and prepares it with the last catalog read. An icon that cannot be read shows as the empty frame. The
 * item page reports its own icon errors.
 */
public sealed class InvestmentIconCache : IDisposable
{
    /** A cache that is never prepared, for a page drawn without the window's cache. It draws the empty frame for every item. */
    internal static readonly InvestmentIconCache Unprepared = new();

    /** The page context's cache, or Unprepared when the page is drawn without one. */
    internal static InvestmentIconCache Of(StudioPageContext context) => context.Icons ?? Unprepared;
    private readonly IconStream<InvestmentIcon, GpuTexture> stream = new();
    private InvestmentCatalog? catalog;

    /** Requests the item's icon, unless the item belongs to another catalog. @return Its texture once uploaded, else null. */
    public GpuTexture? Get(InvestmentCatalog source, int item) =>
        ReferenceEquals(source, catalog) && item >= 0 && item < source.Items.Count ? stream.Get(item) : null;

    /**
     * Draws the item's icon as a square ImGui item of side pixels. A frame stands in while it loads or when it has none, and an
     * index that names no item, such as -1, leaves the square empty.
     */
    public void Draw(InvestmentCatalog source, int item, float side)
    {
        Draw(ImGui.GetWindowDrawList(), source, item, ImGui.GetCursorScreenPos(), side);
        ImGui.Dummy(new(side));
    }

    /** Draws the item's icon, or the frame, at start in the given draw list without adding an ImGui item. */
    public void Draw(ImDrawListPtr draw, InvestmentCatalog source, int item, Vector2 start, float side)
    {
        if (item < 0 || item >= source.Items.Count) return;
        var end = start + new Vector2(side);
        if (Get(source, item) is { } texture) draw.AddImage(texture.ImGuiId, start, end);
        else draw.AddRectFilled(start, end, ImGui.GetColorU32(ImGuiCol.FrameBg));
    }

    /** Runs between frames. A new catalog restarts the stream, then what the worker read uploads within the per-frame budget. */
    public void Prepare(InvestmentCatalog? current, Dx11Renderer renderer)
    {
        if (!ReferenceEquals(current, catalog))
        {
            catalog = current;
            stream.Reset(current is null ? null : () =>
            {
                var reader = new InvestmentIconReader(current);
                return (item, token) => reader.Read(current.Items[item], token);
            });
        }
        stream.Prepare(image =>
        {
            try { return AssetViewport.CreatePreviewTexture(renderer, image.Texture); }
            catch (Exception error) when (error is not OutOfMemoryException) { return null; }
        });
    }

    public void Dispose() { stream.Dispose(); catalog = null; }
}
