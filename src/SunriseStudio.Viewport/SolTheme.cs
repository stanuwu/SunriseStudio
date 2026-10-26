using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace SunriseStudio.Viewport;

/**
 * Sol design language, UE5 edition (docs/UI-UE5.md §2): the token set, the bundled Chakra Petch / Inter faces and
 * the draw helpers built on them. Pages take colours from here instead of writing their own. The token names from
 * the Sol website palette are kept so every call site compiles; their values are the UE5 editor greys and blue
 * accent. The token table lives in SolTheme.Tokens.cs.
 */
public static unsafe partial class SolTheme
{
    /** The DPI scale from the last Apply. */
    public static float Scale { get; private set; } = 1;
    /** Inter 600 at body size: panel section headings, emphasised labels and mode buttons. */
    public static ImFontPtr HeadingFont { get; private set; }
    /** Inter 600 at button size: primary and secondary buttons. */
    public static ImFontPtr ButtonFont { get; private set; }
    /** Inter 600 at label size: tabs, small upper-case section headers. */
    public static ImFontPtr LabelFont { get; private set; }
    /** Chakra Petch 600 at title size: start screen and dialog titles. */
    public static ImFontPtr TitleFont { get; private set; }
    /** Chakra Petch 700 at title size: the SOLFORGE wordmark and the loading card. */
    public static ImFontPtr BrandFont { get; private set; }
    /** Inter 600 at body size: emphasised body text. */
    public static ImFontPtr StrongFont { get; private set; }

    private static readonly string[] FaceNames = ["Inter-Regular", "Inter-SemiBold", "ChakraPetch-SemiBold", "ChakraPetch-Bold"];
    // Native copies of the embedded faces. The atlas reads them on every rebuild and never frees them.
    private static (IntPtr Data, int Length)[]? faces;
    private static bool facesTried;
    private static readonly string[] Glyphs = Enumerable.Range(0, 128).Select(code => ((char)code).ToString()).ToArray();

    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex { public Vector2 Position; public Vector2 Uv; public uint Color; }

    public static Vector4 Alpha(Vector4 color, float alpha) => color with { W = alpha };
    public static uint U32(Vector4 color) => ImGui.GetColorU32(color);

    /**
     * Installs the bundled faces for ImGuiController.HostFonts. The controller's body size is 16 px at
     * 96 DPI, so its ratio to 16 is the DPI scale. Returns null, adding nothing, when a face is missing.
     */
    public static ImFontPtr? AddFonts(ImFontAtlasPtr fonts, float controllerBodySize)
    {
        HeadingFont = default; ButtonFont = default; LabelFont = default; TitleFont = default; StrongFont = default; BrandFont = default;
        if (!LoadFaces() || faces is not { } loaded) return null;
        float scale = Math.Max(1, controllerBodySize / 16f);
        AddFace(fonts, loaded[0], BodySize * scale); // first face: ImGui's default font
        StrongFont = AddFace(fonts, loaded[1], BodySize * scale);
        // UE5 headings are the body face at body size, semibold: one atlas entry serves both.
        HeadingFont = StrongFont;
        ButtonFont = AddFace(fonts, loaded[1], ButtonSize * scale);
        LabelFont = AddFace(fonts, loaded[1], LabelSize * scale);
        TitleFont = AddFace(fonts, loaded[2], TitleSize * scale);
        BrandFont = AddFace(fonts, loaded[3], TitleSize * scale);
        return TitleFont;
    }

    private static bool LoadFaces()
    {
        if (facesTried) return faces is not null;
        facesTried = true;
        var loaded = new (IntPtr Data, int Length)[FaceNames.Length];
        for (int i = 0; i < FaceNames.Length; i++)
        {
            using var stream = typeof(SolTheme).Assembly.GetManifestResourceStream("Solforge.Fonts." + FaceNames[i]);
            if (stream is null || stream.Length is <= 0 or > 4 << 20)
            {
                for (int j = 0; j < i; j++) Marshal.FreeHGlobal(loaded[j].Data);
                return false;
            }
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            var data = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, data, bytes.Length);
            loaded[i] = (data, bytes.Length);
        }
        faces = loaded;
        return true;
    }

    private static ImFontPtr AddFace(ImFontAtlasPtr fonts, (IntPtr Data, int Length) face, float pixels)
    {
        ImFontConfigPtr config = ImGuiNative.ImFontConfig_ImFontConfig();
        config.FontDataOwnedByAtlas = false;
        var font = fonts.AddFontFromMemoryTTF(face.Data, face.Length, MathF.Round(pixels), config);
        config.Destroy();
        return font;
    }

    /** Pushes a bundled face when it is loaded; pop only when this returns true. */
    public static bool PushFont(ImFontPtr font)
    {
        if (font.NativePtr == null) return false;
        ImGui.PushFont(font);
        return true;
    }

    /** UE5 colours, shapes and sizes on the current context; safe to call again on a DPI change. */
    public static void Apply(float scale)
    {
        Scale = Math.Max(1, scale);
        var style = ImGui.GetStyle();
        // Start from ImGui's defaults, so a second call does not scale the sizes twice.
        var defaults = ImGuiNative.ImGuiStyle_ImGuiStyle();
        *style.NativePtr = *defaults;
        ImGuiNative.ImGuiStyle_destroy(defaults);
        ImGui.StyleColorsDark();
        style.WindowPadding = new(8, 8);
        // The spec's compact padding (docs/UI-UE5.md §2.2); with 13 px body text a frame is about 19-20 px tall.
        style.FramePadding = new(6, 3);
        style.ItemSpacing = new(6, 4);
        style.ItemInnerSpacing = new(4, 4);
        style.IndentSpacing = 14;
        style.CellPadding = new(4, 2);
        // Rounded controls, square areas: panels meet edge to edge.
        style.WindowRounding = 0;
        style.ChildRounding = 0;
        style.PopupRounding = RoundingPopup;
        style.FrameRounding = Rounding;
        style.GrabRounding = Rounding;
        style.TabRounding = RoundingTab;
        style.ScrollbarRounding = RoundingScrollbar;
        style.WindowBorderSize = 0;
        style.ChildBorderSize = 1;
        style.PopupBorderSize = 1;
        // The Input fill gives fields their contrast, so frames carry no border.
        style.FrameBorderSize = 0;
        style.TabBorderSize = 0;
        style.TabBarBorderSize = 1;
        style.TabBarOverlineSize = 2;
        style.ScrollbarSize = 10;
        style.GrabMinSize = 8;
        style.SeparatorTextBorderSize = 1;
        style.SeparatorTextPadding = new(0, 4);
        var colors = style.Colors;
        void Set(ImGuiCol slot, Vector4 value) => colors[(int)slot] = value;
        var clear = Vector4.Zero;
        Set(ImGuiCol.Text, Text);
        Set(ImGuiCol.TextDisabled, Dim);
        Set(ImGuiCol.WindowBg, Panel);
        Set(ImGuiCol.ChildBg, Panel);
        Set(ImGuiCol.PopupBg, Popup);
        Set(ImGuiCol.Border, BorderHi);
        Set(ImGuiCol.BorderShadow, clear);
        Set(ImGuiCol.FrameBg, Input);
        Set(ImGuiCol.FrameBgHovered, InputHover);
        Set(ImGuiCol.FrameBgActive, Input);
        Set(ImGuiCol.TitleBg, Bar);
        Set(ImGuiCol.TitleBgActive, Bar);
        Set(ImGuiCol.TitleBgCollapsed, Bar);
        Set(ImGuiCol.MenuBarBg, Bar);
        Set(ImGuiCol.ScrollbarBg, clear);
        Set(ImGuiCol.ScrollbarGrab, ScrollGrab);
        Set(ImGuiCol.ScrollbarGrabHovered, ScrollGrabHover);
        Set(ImGuiCol.ScrollbarGrabActive, ScrollGrabActive);
        Set(ImGuiCol.CheckMark, Accent);
        Set(ImGuiCol.SliderGrab, Accent);
        Set(ImGuiCol.SliderGrabActive, AccentHover);
        Set(ImGuiCol.Button, Surface);
        Set(ImGuiCol.ButtonHovered, Hover);
        Set(ImGuiCol.ButtonActive, Pressed);
        // Selected rows are a blue wash; hovered rows the hover grey.
        Set(ImGuiCol.Header, Selection);
        Set(ImGuiCol.HeaderHovered, Hover);
        Set(ImGuiCol.HeaderActive, Selection);
        Set(ImGuiCol.Separator, Line);
        Set(ImGuiCol.SeparatorHovered, Alpha(Accent, .6f));
        Set(ImGuiCol.SeparatorActive, Accent);
        Set(ImGuiCol.ResizeGrip, clear);
        Set(ImGuiCol.ResizeGripHovered, Alpha(Accent, .5f));
        Set(ImGuiCol.ResizeGripActive, Accent);
        Set(ImGuiCol.Tab, Header);
        Set(ImGuiCol.TabHovered, Hover);
        Set(ImGuiCol.TabSelected, Panel);
        Set(ImGuiCol.TabSelectedOverline, Accent);
        Set(ImGuiCol.TabDimmed, Header);
        Set(ImGuiCol.TabDimmedSelected, Panel);
        Set(ImGuiCol.TabDimmedSelectedOverline, Alpha(Accent, .5f));
        Set(ImGuiCol.PlotLines, Muted);
        Set(ImGuiCol.PlotLinesHovered, Accent);
        Set(ImGuiCol.PlotHistogram, Accent);
        Set(ImGuiCol.PlotHistogramHovered, AccentHover);
        Set(ImGuiCol.TableHeaderBg, Header);
        Set(ImGuiCol.TableBorderStrong, Line);
        Set(ImGuiCol.TableBorderLight, TableLine);
        Set(ImGuiCol.TableRowBg, clear);
        Set(ImGuiCol.TableRowBgAlt, White(.015f));
        Set(ImGuiCol.TextLink, AccentHover);
        Set(ImGuiCol.TextSelectedBg, Selection);
        Set(ImGuiCol.DragDropTarget, Accent);
        Set(ImGuiCol.NavCursor, Accent);
        Set(ImGuiCol.NavWindowingHighlight, Accent);
        Set(ImGuiCol.NavWindowingDimBg, new(0, 0, 0, .5f));
        Set(ImGuiCol.ModalWindowDimBg, new(0, 0, 0, .5f));
        style.ScaleAllSizes(Scale);
        ImGui.GetIO().FontGlobalScale = 1;
    }

    // ---- Draw helpers ----

    /**
     * Formerly the launcher's chamfer polygon. UE5 shapes are rounded rectangles: the path is a rounded rect whose
     * radius is cut when it is positive (already scaled), otherwise the control rounding. Callers do not change.
     */
    public static void ChamferPath(ImDrawListPtr draw, Vector2 min, Vector2 max, float cut)
    {
        float radius = cut > 0 ? cut : Rounding * Scale;
        radius = Math.Clamp(radius, 0, Math.Min(max.X - min.X, max.Y - min.Y) * .5f);
        draw.PathRect(min, max, radius);
    }

    /** A rounded box; a zero colour skips the fill or the border. Cut is in unscaled pixels; 0 uses the control rounding. */
    public static void ChamferFrame(ImDrawListPtr draw, Vector2 min, Vector2 max, float cut, uint fill, uint border = 0)
    {
        if (max.X - min.X < 1 || max.Y - min.Y < 1) return;
        if (fill != 0) { ChamferPath(draw, min, max, cut * Scale); draw.PathFillConvex(fill); }
        if (border != 0)
        {
            // Half-pixel inset keeps the 1 px stroke crisp and inside the box.
            ChamferPath(draw, min + new Vector2(.5f), max - new Vector2(.5f), cut * Scale);
            draw.PathStroke(border, ImDrawFlags.Closed, 1);
        }
    }

    /** A rounded box filled with the accent ramp (accent hover -> accent -> accent pressed, left to right). */
    public static void ChamferGradient(ImDrawListPtr draw, Vector2 min, Vector2 max, float cut, float alpha = 1)
    {
        if (max.X - min.X < 1 || max.Y - min.Y < 1) return;
        int first = draw.VtxBuffer.Size;
        ChamferPath(draw, min, max, cut * Scale);
        uint opaque = (uint)Math.Clamp(alpha * 255, 0, 255) << 24 | 0x00FFFFFF;
        draw.PathFillConvex(opaque);
        // Recolour the fill's vertices (and its anti-aliased fringe) by x, keeping each vertex's alpha.
        var vertices = (Vertex*)(void*)draw.VtxBuffer.Data;
        float width = max.X - min.X;
        for (int i = first; i < draw.VtxBuffer.Size; i++)
        {
            float t = Math.Clamp((vertices[i].Position.X - min.X) / width, 0, 1);
            var color = t < .5f ? Vector4.Lerp(SunGradStart, Sun, t * 2) : Vector4.Lerp(Sun, SunGradEnd, (t - .5f) * 2);
            uint rgb = ImGui.ColorConvertFloat4ToU32(color with { W = 1 }) & 0x00FFFFFF;
            vertices[i].Color = (vertices[i].Color & 0xFF000000) | rgb;
        }
    }

    /**
     * A progress pill: the accent ramp along x (or the brand ramp for the loading card), drawn as slices so the
     * middle stop holds. A sheen in [0, 1) sweeps a pale band across it; a negative sheen leaves it still.
     */
    public static void ProgressFill(ImDrawListPtr draw, Vector2 min, Vector2 max, float sheen = -1, bool brand = false)
    {
        float width = max.X - min.X, height = max.Y - min.Y, radius = height / 2;
        if (width < 1 || height < 1) return;
        Vector4 start = brand ? BrandEdge : AccentHover, middle = brand ? Brand : Accent, end = brand ? Brand2 : AccentActive;
        if (width <= height) { draw.AddRectFilled(min, max, U32(start), radius); return; }
        float band = Math.Max(height * 8, width * .18f), center = min.X - band + (width + band * 2) * sheen;
        Vector4 At(float x)
        {
            float t = (x - min.X) / width, u = (x - center) / band;
            var color = t < .5f ? Vector4.Lerp(start, middle, t * 2) : Vector4.Lerp(middle, end, (t - .5f) * 2);
            return sheen < 0 || u <= -1 || u >= 1 ? color : Vector4.Lerp(color, TextStrong, (1 - u * u) * (1 - u * u) * .45f);
        }
        float left = min.X + radius, right = max.X - radius, y = min.Y + radius;
        draw.AddCircleFilled(new(left, y), radius, U32(At(left)), 12);
        draw.AddCircleFilled(new(right, y), radius, U32(At(right)), 12);
        int slices = Math.Clamp((int)((right - left) / (3 * Scale)), 1, 128);
        for (int i = 0; i < slices; i++)
        {
            float x0 = left + (right - left) * i / slices, x1 = left + (right - left) * (i + 1) / slices;
            uint a = U32(At(x0)), b = U32(At(x1));
            draw.AddRectFilledMultiColor(new(x0, min.Y), new(x1, max.Y), a, b, b, a);
        }
    }

    /**
     * The panel fill behind the current window's content. PanelTop and PanelBottom both equal Panel, so this is a
     * flat fill (UE5 chrome has no gradients). A window that pushed its own background (a splitter, an overlay) keeps it.
     */
    public static void PanelGradient()
    {
        if (ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg] != Panel) return;
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        uint top = U32(PanelTop), bottom = U32(PanelBottom);
        if (top == bottom) ImGui.GetWindowDrawList().AddRectFilled(min, max, top);
        else ImGui.GetWindowDrawList().AddRectFilledMultiColor(min, max, top, top, bottom, bottom);
    }

    /** Flat bar fill with a 1 px border line on top: the status bar. */
    public static void StatusBackground()
    {
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, max, U32(Bar));
        draw.AddLine(min, new(max.X, min.Y), U32(Border), 1);
    }

    /**
     * The soft popup shadow: two rings outside the current window (25 % and 10 % black), drawn on its own draw list
     * with the clip lifted so the ring can sit outside it. Call right after BeginPopup, BeginMenu, BeginCombo or
     * BeginTooltip returns true.
     */
    public static void PopupShadow()
    {
        var min = ImGui.GetWindowPos();
        PopupShadow(ImGui.GetWindowDrawList(), min, min + ImGui.GetWindowSize());
    }

    /** The popup shadow around a rectangle; only the ring outside the rectangle is drawn, so its contents stay clear. */
    public static void PopupShadow(ImDrawListPtr draw, Vector2 min, Vector2 max)
    {
        if (max.X - min.X < 1 || max.Y - min.Y < 1) return;
        float s = Scale, radius = RoundingPopup * s;
        var drop = new Vector2(0, 1 * s);
        draw.PushClipRectFullScreen();
        draw.AddRect(min - new Vector2(1.5f * s) + drop, max + new Vector2(1.5f * s) + drop, U32(new(0, 0, 0, .25f)), radius + 1.5f * s, ImDrawFlags.None, 2 * s);
        draw.AddRect(min - new Vector2(3.5f * s) + drop * 2, max + new Vector2(3.5f * s) + drop * 2, U32(new(0, 0, 0, .10f)), radius + 3.5f * s, ImDrawFlags.None, 3 * s);
        draw.PopClipRect();
    }

    /** Draws text with letter spacing, as the upper-case labels; returns its width. */
    public static float TrackedText(ImDrawListPtr draw, Vector2 position, uint color, string text, float tracking)
    {
        float x = position.X;
        for (int i = 0; i < text.Length; i++)
        {
            string glyph = text[i] < Glyphs.Length ? Glyphs[text[i]] : text[i].ToString();
            draw.AddText(new(x, position.Y), color, glyph);
            x += ImGui.CalcTextSize(glyph).X + (i + 1 < text.Length ? tracking : 0);
        }
        return x - position.X;
    }

    /** The brand shimmer at t; the gradient tiles, so moving t sweeps it as the launcher's 6 s shimmer does. */
    public static Vector4 Shimmer(float t)
    {
        t = (t % 1 + 1) % 1 * 4;
        int stop = Math.Min(3, (int)t);
        var (from, to) = stop switch { 0 => (BrandEdge, Brand), 1 => (Brand, Brand2), 2 => (Brand2, Brand), _ => (Brand, BrandEdge) };
        return Vector4.Lerp(from, to, t - stop);
    }

    /**
     * Tracked text filled with the brand shimmer, as the launcher's h1 span: the text spans half the gradient
     * (background-size 200%) and phase in [0, 1) is the sweep. Returns its width.
     */
    public static float ShimmerText(ImDrawListPtr draw, Vector2 position, string text, float tracking, float phase)
    {
        int first = draw.VtxBuffer.Size;
        float width = Math.Max(1, TrackedText(draw, position, uint.MaxValue, text, tracking));
        var vertices = (Vertex*)(void*)draw.VtxBuffer.Data;
        for (int i = first; i < draw.VtxBuffer.Size; i++)
        {
            float t = Math.Clamp((vertices[i].Position.X - position.X) / width, 0, 1);
            uint rgb = ImGui.ColorConvertFloat4ToU32(Shimmer(t * .5f - phase) with { W = 1 }) & 0x00FFFFFF;
            vertices[i].Color = (vertices[i].Color & 0xFF000000) | rgb;
        }
        return width;
    }

    public static float TrackedWidth(string text, float tracking)
    {
        float width = 0;
        for (int i = 0; i < text.Length; i++)
            width += ImGui.CalcTextSize(text[i] < Glyphs.Length ? Glyphs[text[i]] : text[i].ToString()).X;
        return width + tracking * Math.Max(0, text.Length - 1);
    }

    /** An 11 px semibold upper-case label in muted grey over a 1 px separator: a group header. */
    public static void SectionHeader(string text, string tooltip = "")
    {
        bool font = PushFont(LabelFont);
        string label = text.ToUpperInvariant();
        float tracking = ImGui.GetFontSize() * .08f;
        var draw = ImGui.GetWindowDrawList();
        ImGui.Dummy(new(0, Space2));
        var start = ImGui.GetCursorScreenPos();
        float width = Math.Max(1, ImGui.GetContentRegionAvail().X);
        float height = ImGui.GetTextLineHeight() + Space3;
        ImGui.Dummy(new(width, height));
        TrackedText(draw, start, U32(Muted), label, tracking);
        draw.AddLine(new(start.X, start.Y + height - 1), new(start.X + width, start.Y + height - 1), U32(Line), 1);
        if (font) ImGui.PopFont();
        if (tooltip.Length != 0 && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
    }

    /**
     * A collapsing group drawn as a UE5 Details category: a Header strip, an 11 px semibold upper-case label and a
     * chevron that rotates by animation. The label's id is kept.
     */
    public static bool CollapsingSection(string label, ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.None)
    {
        int split = label.IndexOf("##", StringComparison.Ordinal);
        string shown = (split < 0 ? label : label[..split]).ToUpperInvariant();
        bool font = PushFont(LabelFont);
        ImGui.PushStyleColor(ImGuiCol.Header, Header);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Hover);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, Pressed);
        // A clear text colour hides ImGui's own arrow; the chevron and the tracked label are drawn below.
        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
        // The visible text is drawn tracked below; the whole original label stays in the id, so ids stay unique.
        bool open = UiAuto.CollapsingHeader("##" + label, flags);
        ImGui.PopStyleColor(4);
        uint id = ImGui.GetItemID();
        bool hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var draw = ImGui.GetWindowDrawList();
        float size = ImGui.GetFontSize();
        float arrow = size + ImGui.GetStyle().FramePadding.X * 2;
        float textTop = min.Y + (max.Y - min.Y - ImGui.GetTextLineHeight()) / 2;
        var chevronCenter = new Vector2(min.X + ImGui.GetStyle().FramePadding.X + size * .5f, (min.Y + max.Y) * .5f);
        var tint = UiMotion.EaseColor(UiMotion.Mix(id, 0x7E11u), hovered ? TextStrong : Text);
        UiMotion.DrawChevron(draw, chevronCenter, size * .5f, UiMotion.Chevron(id, open), U32(tint));
        TrackedText(draw, new(min.X + arrow, textTop), U32(tint), shown, size * .08f);
        if (font) ImGui.PopFont();
        return open;
    }

    /** The primary button: rounded Accent fill, white semibold label; its fill eases between rest, hover and pressed. */
    public static bool PrimaryButton(string label, Vector2 size = default)
    {
        int split = label.IndexOf("##", StringComparison.Ordinal);
        string shown = split < 0 ? label : label[..split];
        // One body-font frame tall, like the controls beside it; the smaller button face would make it shorter.
        if (size.Y <= 0) size.Y = ImGui.GetFrameHeight();
        bool font = PushFont(ButtonFont);
        float textWidth = ImGui.CalcTextSize(shown).X;
        if (size.X <= 0) size.X = textWidth + PrimaryPadding * 2 * Scale;
        bool clicked = UiAuto.InvisibleButton(label, Vector2.Max(size, Vector2.One));
        bool hovered = ImGui.IsItemHovered(), held = ImGui.IsItemActive();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var draw = ImGui.GetWindowDrawList();
        var fill = UiMotion.EaseColor(ImGui.GetItemID(), held ? AccentActive : hovered ? AccentHover : Accent, 35f);
        ChamferFrame(draw, min, max, 0, U32(fill));
        var textPosition = new Vector2(min.X + (max.X - min.X - textWidth) / 2, min.Y + (max.Y - min.Y - ImGui.GetTextLineHeight()) / 2);
        draw.AddText(textPosition, U32(AccentInk), shown);
        if (font) ImGui.PopFont();
        return clicked;
    }

    /** Horizontal padding each side of a PrimaryButton label, in unscaled pixels. */
    private const float PrimaryPadding = 14;

    /** The width PrimaryButton gives the label, for laying out a row before drawing it. */
    public static float PrimaryButtonWidth(string label)
    {
        int split = label.IndexOf("##", StringComparison.Ordinal);
        string shown = split < 0 ? label : label[..split];
        bool font = PushFont(ButtonFont);
        float width = ImGui.CalcTextSize(shown).X + PrimaryPadding * 2 * Scale;
        if (font) ImGui.PopFont();
        return width;
    }

    /** A small mode button (Object / Asset): no fill, its label easing to strong text and an accent underline when active. */
    public static bool ModeButton(string label, bool active)
    {
        bool font = PushFont(HeadingFont);
        uint id = ImGui.GetID(label);
        var text = UiMotion.EaseColor(UiMotion.Mix(id, 0x40DEu), active ? TextStrong : Muted);
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Text, text);
        bool clicked = UiAuto.SmallButton(label);
        ImGui.PopStyleColor(2);
        if (font) ImGui.PopFont();
        if (active) Underline();
        return clicked;
    }

    /** A 2 px accent line under the last item: the active tab or mode. */
    public static void Underline()
    {
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        ImGui.GetWindowDrawList().AddRectFilled(new(min.X, max.Y - 2 * Scale), max, U32(Accent));
    }

    /** A 2 px accent bar on the left of a row: the selected list row. */
    public static void LeftBar(ImDrawListPtr draw, Vector2 min, Vector2 max)
        => draw.AddRectFilled(min, new(min.X + 2 * Scale, max.Y), U32(Accent));

    /** Formerly the launcher's title notch; UE5 headers carry a thin accent tick instead. */
    public static void TitleNotch(ImDrawListPtr draw, Vector2 min, float height)
        => draw.AddRectFilled(min, new(min.X + 2 * Scale, min.Y + height), U32(Accent), Scale);

    /** Space a panel header leaves before its title. */
    public static float HeaderIndent => Space3;

    /** The compact panel header height (22 px at 100 %). */
    public static float PanelHeaderHeight => PanelHeaderSize * Scale;

    /**
     * A compact UE5 panel header: the header window reads as a Header strip with one active tab (Panel fill, 2 px
     * Accent top line, joined to the body) holding the 11 px semibold title. The title is drawn where the next item
     * (an editor combo with an empty preview) sits, clipped to itemWidth.
     */
    public static void PanelHeader(string title, float itemWidth)
    {
        var draw = ImGui.GetWindowDrawList();
        var windowMin = ImGui.GetWindowPos();
        var windowMax = windowMin + ImGui.GetWindowSize();
        draw.AddRectFilled(windowMin, windowMax, U32(Header));
        var at = ImGui.GetCursorScreenPos();
        bool font = PushFont(LabelFont);
        float textWidth = ImGui.CalcTextSize(title).X;
        float tabWidth = Math.Min(textWidth + Space4 * 2, Math.Max(Space4 * 2, itemWidth + HeaderIndent));
        var tabMin = new Vector2(windowMin.X + Math.Max(0, at.X - windowMin.X - Space2), windowMin.Y);
        var tabMax = new Vector2(tabMin.X + tabWidth, windowMax.Y);
        draw.AddRectFilled(tabMin, tabMax, U32(Panel), RoundingTab * Scale, ImDrawFlags.RoundCornersTop);
        draw.AddRectFilled(tabMin, new(tabMax.X, tabMin.Y + 2 * Scale), U32(Accent), RoundingTab * Scale, ImDrawFlags.RoundCornersTop);
        float frame = ImGui.GetFrameHeight();
        var textAt = new Vector2(tabMin.X + Space4, at.Y + (frame - ImGui.GetTextLineHeight()) / 2);
        draw.PushClipRect(tabMin, tabMax, true);
        draw.AddText(textAt, U32(TextStrong), title);
        draw.PopClipRect();
        if (font) ImGui.PopFont();
        ImGui.SetCursorScreenPos(new(at.X + HeaderIndent, at.Y));
    }

    /**
     * One compact tab of a tabbed area header: Header fill and muted label when inactive, Panel fill with a 2 px Accent
     * top line and strong label when active. hover in [0, 1] (an eased value) lifts an inactive tab toward Hover.
     * accentLine false leaves the top line to the caller (area tabs draw one sprung line that slides between tabs);
     * activeBlend in [0, 1], when given, eases the fill from the inactive fill to Panel instead of switching it.
     */
    public static void PanelTab(ImDrawListPtr draw, Vector2 min, Vector2 max, string label, bool active, float hover = 0,
        bool accentLine = true, float activeBlend = -1)
    {
        float radius = RoundingTab * Scale;
        var resting = Vector4.Lerp(Header, Hover, Math.Clamp(hover, 0, 1));
        var fill = activeBlend >= 0 ? Vector4.Lerp(resting, Panel, Math.Clamp(activeBlend, 0, 1)) : active ? Panel : resting;
        draw.AddRectFilled(min, max, U32(fill), radius, ImDrawFlags.RoundCornersTop);
        if (active && accentLine) draw.AddRectFilled(min, new(max.X, min.Y + 2 * Scale), U32(Accent), radius, ImDrawFlags.RoundCornersTop);
        bool font = PushFont(LabelFont);
        var textAt = new Vector2(min.X + Space4, min.Y + (max.Y - min.Y - ImGui.GetTextLineHeight()) / 2);
        draw.PushClipRect(min, max, true);
        draw.AddText(textAt, U32(active ? TextStrong : Vector4.Lerp(Muted, Text, Math.Clamp(hover, 0, 1))), label);
        draw.PopClipRect();
        if (font) ImGui.PopFont();
    }

    /** Colours for a header combo that reads as a title; pop 3 after the combo (and its EndCombo). */
    public static void PushHeaderCombo()
    {
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
    }

    /** A viewport label chip: translucent panel fill, highlight border, accent border when the pointer is over it. */
    public static void Chip(ImDrawListPtr draw, Vector2 min, Vector2 max, bool hovered)
        => ChamferFrame(draw, min, max, 0, U32(Alpha(Panel, .92f)), U32(hovered ? Accent : BorderHi));

    /** Icon tint: text at rest, strong text on hover, accent when toggled on. */
    public static uint IconTint(bool active, bool hovered) => U32(IconTintColor(active, hovered));

    /** The icon tint as a colour, for easing between states. */
    public static Vector4 IconTintColor(bool active, bool hovered) => active ? Accent : hovered ? TextStrong : Text;
}
