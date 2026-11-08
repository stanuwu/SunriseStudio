using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Rendering;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** What one grid cell shows. Without an Icon, the cell draws an empty frame when IconRoom is set and Glyph otherwise. */
internal readonly record struct IconCell(string Name, uint NameColour, string Kind, GpuTexture? Icon, bool IconRoom,
    StudioIcon Glyph, string Badge, uint BadgeColour, float Progress, bool Unknown, bool Equipped, bool Complete,
    bool Active, bool Modified);

/** A clipped icon grid of Selectable cells. ImGui's own 2D navigation moves between them. */
internal sealed class IconGrid
{
    /** Icon side in pixels at UI scale 1, close to the game's inventory tile so item art shows at about its drawn size. */
    public const float IconSide = 56;
    /** The Progress of a cell that draws no progress strip. */
    public const float NoProgress = -1;
    // Room beside and above the icon, so neighbouring icons never touch.
    private const float Margin = 6;
    // Gap between the icon and the name.
    private const float NameGap = 2;
    // The room under the name fits the progress strip with one pixel to spare before the next row.
    private const float StripRoom = 4, StripHeight = 3;
    // Name text keeps clear of the cell border.
    private const float TextInset = 3;
    private const float BorderWidth = 2;
    // A glyph fills this share of the icon square, so it never reads as an item icon.
    private const float GlyphShare = .6f;
    // The backing under corner text is opaque enough to read over a bright icon and clear enough to show the icon through it.
    private const float BackingAlpha = .7f, BackingPad = 2;
    // Legs of the equipped corner triangle.
    private const float CornerSide = 12;
    // The modified dot matches the one on list rows and sits in the margin above and right of the icon.
    private const float DotRadius = 3, DotInset = Margin / 2;
    // Rows past each visible edge whose cells are asked for but not drawn, so their icons load before a wheel step shows them.
    private const int LookaheadRows = 1;

    private int scrollTo = -1;

    /** The first cell of the top grid row the last draw showed. */
    public int FirstVisible { get; private set; }

    /** Scrolls to the top on the next draw, for a list whose rows changed. */
    public void ResetScroll() => scrollTo = 0;

    /** Scrolls the grid row holding index to the top on the next draw. */
    public void ScrollTo(int index) => scrollTo = Math.Max(0, index);

    public static int Columns(float width, float cell) => Math.Max(1, (int)(width / cell));

    /** Top-left of cell index inside the grid for a column count, cell size and spacing. */
    public static Vector2 CellOrigin(int index, int columns, Vector2 cell, Vector2 spacing) =>
        new(index % columns * (cell.X + spacing.X), index / columns * (cell.Y + spacing.Y));

    /** One cell at a UI scale: the icon with its margins over two name lines and the progress strip. */
    public static Vector2 CellSize(float scale) => new((IconSide + 2 * Margin) * scale,
        (IconSide + Margin + NameGap + StripRoom) * scale + 2 * ImGui.GetTextLineHeight());

    /** Spacing between cells. Columns share the width they leave over so the grid fills the row; rows use rowSpacing. */
    public static Vector2 Spacing(float width, int columns, Vector2 cell, float rowSpacing) =>
        new(columns > 1 ? Math.Max(0, width - columns * cell.X) / (columns - 1) : 0, rowSpacing);

    /** The height count cells take in a grid this wide, so a caller can size the grid to its cells. */
    public static float Height(int count, float width, float scale)
    {
        var (_, columns, pitch, _) = Layout(width, scale);
        return (count + columns - 1) / columns * pitch;
    }

    /** Cell size, columns, row pitch and spacing in a grid this wide. */
    private static (Vector2 Size, int Columns, float Pitch, Vector2 Spacing) Layout(float width, float scale)
    {
        var size = CellSize(scale);
        float gap = ImGui.GetStyle().ItemSpacing.X;
        // Cells stay at least the style's item spacing apart, so names in neighbouring cells never run together.
        int columns = Columns(width + gap, size.X + gap);
        var spacing = Spacing(width, columns, size, ImGui.GetStyle().ItemSpacing.Y);
        return (size, columns, size.Y + spacing.Y, spacing);
    }

    /**
     * Draws count cells in a child of the given height, where 0 fills the rest. The cell callback is also called for the
     * LookaheadRows past each visible edge, which are not drawn.
     * @return The cell clicked or activated, else -1.
     */
    public unsafe int Draw(string id, int count, Func<int, IconCell> cell, float scale, float height,
        out int hovered, out int context)
    {
        hovered = context = -1;
        int chosen = -1;
        ImGui.BeginChild(id, new(0, height), ImGuiChildFlags.None);
        var (size, columns, pitch, spacing) = Layout(ImGui.GetContentRegionAvail().X, scale);
        if (scrollTo >= 0) { ImGui.SetScrollY(scrollTo / columns * pitch); scrollTo = -1; }
        FirstVisible = Math.Clamp((int)(ImGui.GetScrollY() / pitch) * columns, 0, Math.Max(0, count - 1));
        var top = ImGui.GetCursorPos();
        var draw = ImGui.GetWindowDrawList();
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin((count + columns - 1) / columns, pitch);
        while (clipper.Step())
            for (int row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
                for (int index = row * columns; index < Math.Min(count, (row + 1) * columns); index++)
                {
                    ImGui.SetCursorPos(top + CellOrigin(index, columns, size, spacing));
                    SelectableCell(draw, index, cell(index), size, scale, true, ref chosen, ref hovered, ref context);
                }
        clipper.End(); clipper.Destroy();
        AskAhead(count, columns, pitch, cell);
        ImGui.EndChild();
        return chosen;
    }

    /**
     * Draws count square cells laid out like a game screen of columns by rows. Each cell is as large as one full screen in the
     * child allows, up to a grid cell's width, and cells past one screen scroll. Cells draw no names; the caller's tooltips
     * carry them.
     * @return The cell clicked or activated, else -1.
     */
    public static int DrawScreen(string id, int count, int columns, int rows, Func<int, IconCell> cell, float scale,
        out int hovered, out int context)
    {
        hovered = context = -1;
        int chosen = -1;
        ImGui.BeginChild(id, Vector2.Zero, ImGuiChildFlags.None);
        var spacing = ImGui.GetStyle().ItemSpacing;
        var room = ImGui.GetContentRegionAvail();
        float full = IconSide + 2 * Margin;
        float side = Math.Clamp(Math.Min((room.X - (columns - 1) * spacing.X) / columns,
            (room.Y - (rows - 1) * spacing.Y) / rows),
            1, full * scale);
        var top = ImGui.GetCursorPos();
        var draw = ImGui.GetWindowDrawList();
        for (int index = 0; index < count; index++)
        {
            ImGui.SetCursorPos(top + CellOrigin(index, columns, new(side), spacing));
            SelectableCell(draw, index, cell(index), new(side), side / full, false, ref chosen, ref hovered, ref context);
        }
        ImGui.EndChild();
        return chosen;
    }

    /** Draws one cell's Selectable at the cursor and the cell over it, recording a click, hover or right-click against index. */
    private static void SelectableCell(ImDrawListPtr draw, int index, in IconCell value, Vector2 size, float scale, bool names,
        ref int chosen, ref int hovered, ref int context)
    {
        ImGui.PushID(index);
        var start = ImGui.GetCursorScreenPos();
        if (ImGui.Selectable("##cell", value.Active, ImGuiSelectableFlags.None, size)) chosen = index;
        if (ImGui.IsItemHovered()) hovered = index;
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Right) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByPopup))
            context = index;
        DrawCell(draw, value, start, size, scale, names);
        ImGui.PopID();
    }

    /** Asks for the cells in the LookaheadRows above and below the rows the child shows. */
    private static void AskAhead(int count, int columns, float pitch, Func<int, IconCell> cell)
    {
        int rows = (count + columns - 1) / columns;
        int first = (int)(ImGui.GetScrollY() / pitch), last = (int)((ImGui.GetScrollY() + ImGui.GetWindowHeight()) / pitch);
        for (int row = Math.Max(0, first - LookaheadRows); row < Math.Min(rows, last + 1 + LookaheadRows); row++)
            if (row < first || row > last)
                for (int index = row * columns; index < Math.Min(count, (row + 1) * columns); index++) cell(index);
    }

    /** Draws the icon, frame or glyph, the name when names is set, the corner marks, border and strip, clipped to the cell. */
    private static void DrawCell(ImDrawListPtr draw, in IconCell value, Vector2 start, Vector2 size, float scale, bool names)
    {
        var end = start + size;
        draw.PushClipRect(start, end, true);
        float side = IconSide * scale;
        var icon = start + new Vector2((size.X - side) / 2, Margin * scale);
        var iconEnd = icon + new Vector2(side);
        if (value.Icon is { } texture) draw.AddImage(texture.ImGuiId, icon, iconEnd);
        else if (value.IconRoom) draw.AddRectFilled(icon, iconEnd, ImGui.GetColorU32(ImGuiCol.FrameBg));
        else
        {
            float glyph = side * GlyphShare;
            ImGuiIcons.Draw(draw, value.Glyph, icon + new Vector2((side - glyph) / 2), glyph,
                ImGui.GetColorU32(ImGuiCol.TextDisabled));
        }
        if (names)
        {
            float inset = TextInset * scale, line = ImGui.GetTextLineHeight();
            var text = new Vector2(start.X + inset, iconEnd.Y + NameGap * scale);
            float textWidth = size.X - 2 * inset;
            var (first, second) = value.Kind.Length != 0 ? (StudioUi.Fit(value.Name, textWidth),
                StudioUi.Fit(value.Kind, textWidth))
                : NameLines(value.Name, textWidth);
            draw.AddText(text, value.NameColour, first);
            draw.AddText(text + new Vector2(0, line),
                value.Kind.Length != 0 ? ImGui.GetColorU32(ImGuiCol.TextDisabled) : value.NameColour, second);
        }
        if (value.Badge.Length != 0) Backed(draw, value.Badge, new(icon.X, iconEnd.Y), false, value.BadgeColour, scale);
        if (value.Unknown) Backed(draw, "?", iconEnd, true, ImGui.GetColorU32(ImGuiCol.TextDisabled), scale);
        if (value.Equipped)
            draw.AddTriangleFilled(icon, icon + new Vector2(CornerSide * scale, 0), icon + new Vector2(0, CornerSide * scale),
                ImGui.GetColorU32(ImGuiCol.CheckMark));
        if (value.Modified)
            draw.AddCircleFilled(new(end.X - DotInset * scale, start.Y + DotInset * scale), DotRadius * scale,
                ImGui.GetColorU32(StudioTheme.Accent));
        if (value.Active || value.Complete)
        {
            float border = BorderWidth * scale;
            draw.AddRect(start + new Vector2(border / 2), end - new Vector2(border / 2),
                ImGui.GetColorU32(value.Active ? StudioTheme.Accent : StudioTheme.Good), 0, ImDrawFlags.None, border);
        }
        if (value.Progress >= 0 && !value.Unknown)
        {
            float strip = StripHeight * scale, done = Math.Clamp(value.Progress, 0, 1);
            var stripStart = new Vector2(icon.X, end.Y - strip);
            // The track is lighter than an empty frame, so a known 0% shows as a strip and never as no progress.
            draw.AddRectFilled(stripStart, new(iconEnd.X, end.Y), ImGui.GetColorU32(ImGuiCol.Border));
            draw.AddRectFilled(stripStart, new(icon.X + side * done, end.Y),
                ImGui.GetColorU32(done >= 1 ? StudioTheme.Good : StudioTheme.Accent));
        }
        draw.PopClipRect();
    }

    /** Corner text on a dark backing, against the icon's bottom-left corner or, when right is set, its bottom-right. */
    private static void Backed(ImDrawListPtr draw, string text, Vector2 corner, bool right, uint colour, float scale)
    {
        var size = ImGui.CalcTextSize(text);
        float pad = BackingPad * scale, width = size.X + 2 * pad;
        var start = new Vector2(right ? corner.X - width : corner.X, corner.Y - size.Y);
        draw.AddRectFilled(start, start + new Vector2(width, size.Y),
            ImGui.GetColorU32(StudioTheme.Canvas with { W = BackingAlpha }));
        draw.AddText(start + new Vector2(pad, 0), colour, text);
    }

    /**
     * Splits a name over two lines between words. The words that fit go on the first line and the rest on the second, which
     * ends in "..." when it is too long. A first word wider than a line is cut with "..." on the first line.
     */
    internal static (string First, string Second) NameLines(string name, float width)
    {
        if (ImGui.CalcTextSize(name).X <= width) return (name, "");
        int split = name.IndexOf(' ');
        if (split < 0) return (StudioUi.Fit(name, width), "");
        for (int next = name.IndexOf(' ', split + 1); next > 0 && ImGui.CalcTextSize(name[..next]).X <= width;
            next = name.IndexOf(' ', next + 1))
            split = next;
        return (StudioUi.Fit(name[..split], width), StudioUi.Fit(name[(split + 1)..].TrimStart(), width));
    }
}

/** List rows with item icons, shared by the browser, the item catalog, Live's Inventory and the table pages so they look alike. */
internal static class IconList
{
    /** One row's height at a UI scale. */
    public static float RowHeight(float scale) => ImGui.GetTextLineHeightWithSpacing() * 2 + 7 * scale;

    /**
     * One two-line row of a list: the item's icon, the name, a muted detail line, then a badge and the modified dot on the
     * right. The row's Selectable stays the last item, so callers can add its tooltip and menu.
     * @param iconColumn True for item rows, which keep the icon's room whether or not the icon loaded or exists.
     * @return True when the row was clicked.
     */
    public static bool Row(InvestmentIconCache icons, float scale, bool active, InvestmentCatalog? catalog, InvestmentItem? item,
        bool iconColumn, string name, uint colour, string detail, string badge, bool modified)
    {
        float rowHeight = RowHeight(scale);
        var start = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.Selectable("##row", active, ImGuiSelectableFlags.None, new(0, rowHeight - 6 * scale));
        var draw = ImGui.GetWindowDrawList();
        var end = start + new Vector2(ImGui.GetContentRegionAvail().X - 4, rowHeight);
        draw.PushClipRect(start, end, true);
        float iconSize = rowHeight - 10 * scale;
        var iconStart = start + new Vector2(4 * scale, 2 * scale);
        if (catalog is not null && item is not null) icons.Draw(draw, catalog, item.Index, iconStart, iconSize);
        // The room does not depend on the icon, so names in one list line up and never shift while icons load.
        var textStart = start + new Vector2(iconColumn ? 10 * scale + iconSize : 7 * scale, 2 * scale);
        float nameEnd = end.X;
        if (badge.Length != 0)
        {
            // Right of the badge stays clear for the modified dot.
            float width = ImGui.CalcTextSize(badge).X;
            nameEnd = end.X - width - 18 * scale;
            draw.AddText(new(end.X - width - 12 * scale, textStart.Y), ImGui.GetColorU32(StudioTheme.Accent), badge);
        }
        draw.PushClipRect(start, new(nameEnd, end.Y), true);
        draw.AddText(textStart, colour, name);
        draw.PopClipRect();
        draw.AddText(textStart + new Vector2(0, ImGui.GetTextLineHeightWithSpacing()),
            ImGui.GetColorU32(ImGuiCol.TextDisabled), detail);
        if (modified) draw.AddCircleFilled(new(end.X - 5, start.Y + 8), 3, ImGui.GetColorU32(StudioTheme.Accent));
        draw.PopClipRect();
        return clicked;
    }

    /**
     * One text line of a list: a Selectable, the item's icon at the text's height and the label fitted after it. Every line keeps
     * the icon's room, whether or not the icon loaded or the line names an item, so labels line up. The Selectable stays the
     * last item, so callers can add its tooltip.
     * @return True when the line was clicked.
     */
    public static bool Line(string id, InvestmentIconCache icons, InvestmentCatalog catalog, int item,
        string label, uint colour, bool selected)
    {
        var start = ImGui.GetCursorScreenPos();
        float width = ImGui.GetContentRegionAvail().X, side = ImGui.GetTextLineHeight();
        bool clicked = ImGui.Selectable(id, selected);
        var draw = ImGui.GetWindowDrawList();
        icons.Draw(draw, catalog, item, start, side);
        float indent = side + ImGui.GetStyle().ItemSpacing.X;
        draw.AddText(start + new Vector2(indent, 0), colour, StudioUi.Fit(label, width - indent));
        return clicked;
    }

    /**
     * The rows-or-grid toggle of a list header. A non-empty refusal disables it and becomes its tooltip.
     * @return True when it was clicked while enabled.
     */
    public static bool GridToggle(string id, bool grid, string refusal)
    {
        ImGui.BeginDisabled(refusal.Length != 0);
        bool clicked = ImGuiIcons.Button(id, StudioIcon.Grid,
            refusal.Length != 0 ? refusal : grid ? "Show as a list" : "Show as a grid", active: grid);
        ImGui.EndDisabled();
        return clicked && refusal.Length == 0;
    }
}
