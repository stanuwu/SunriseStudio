using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace SunriseStudio.Viewport;

/**
 * Solforge design language, after the HTML mockup (solforge ui mockup html): the token set (SolTheme.Tokens.cs), the
 * faces (system UI body, Chakra Petch labels and buttons, Consolas code) and the draw helpers: chamfered frames and
 * fills (14/8/5 px cuts), framed panels, the amber angled underline of the active tab, upper-case tracked labels and
 * chamfered dialogs, popups and toasts. Pages take colours from here instead of writing their own.
 */
public static unsafe partial class SolTheme
{
    /** The DPI scale from the last Apply. */
    public static float Scale { get; private set; } = 1;
    /** Body semibold (Segoe UI Semibold) at body size: section headings, emphasised labels, mode buttons. */
    public static ImFontPtr HeadingFont { get; private set; }
    /** Chakra Petch 600 at button size: buttons and the menu bar titles (drawn upper-case, tracked). */
    public static ImFontPtr ButtonFont { get; private set; }
    /** Chakra Petch 600 at label size: tabs, panel titles and small upper-case section headers. */
    public static ImFontPtr LabelFont { get; private set; }
    /** Chakra Petch 500 at 11 px: menu group captions. */
    public static ImFontPtr CaptionFont { get; private set; }
    /** Chakra Petch 600 at title size: start screen and dialog titles. */
    public static ImFontPtr TitleFont { get; private set; }
    /** Chakra Petch 700 at title size: the SOLFORGE wordmark and the loading card. */
    public static ImFontPtr BrandFont { get; private set; }
    /** Chakra Petch 700 at 15 px: the wordmark in the menu bar. */
    public static ImFontPtr WordmarkFont { get; private set; }
    /** Body semibold at body size: emphasised body text. */
    public static ImFontPtr StrongFont { get; private set; }

    // Embedded faces (Assets/Fonts, SIL OFL 1.1). Inter is the body fallback when the system UI face is missing.
    private static readonly string[] FaceNames =
        ["Inter-Regular", "Inter-SemiBold", "ChakraPetch-SemiBold", "ChakraPetch-Bold", "ChakraPetch-Medium"];
    // The system UI faces (Windows ships them; they are read at run time, never redistributed).
    private static readonly string[] SystemFaces = ["segoeui.ttf", "seguisb.ttf"];
    // Native copies of the faces. The atlas reads them on every rebuild and never frees them.
    private static (IntPtr Data, int Length)[]? faces;
    private static (IntPtr Data, int Length)?[]? systemFaces;
    private static bool facesTried;
    private static readonly string[] Glyphs = Enumerable.Range(0, 128).Select(code => ((char)code).ToString()).ToArray();

    /**
     * Body glyph ranges: Latin through Latin Extended-B, general punctuation (dashes, ellipsis, bullets), arrows, the math
     * minus and the geometric shapes, so "…", "→" and "•" draw instead of "?". Lives for the process (the atlas keeps it).
     */
    private static readonly ushort* BodyRanges = MakeRanges([0x0020, 0x024F, 0x2000, 0x206F, 0x20AC, 0x20AC, 0x2190, 0x21FF, 0x2212, 0x2212, 0x25A0, 0x25FF, 0x2713, 0x2713]);

    private static ushort* MakeRanges(ReadOnlySpan<ushort> pairs)
    {
        var ranges = (ushort*)NativeMemory.AllocZeroed((nuint)(pairs.Length + 1), sizeof(ushort));
        pairs.CopyTo(new Span<ushort>(ranges, pairs.Length));
        return ranges;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex { public Vector2 Position; public Vector2 Uv; public uint Color; }

    // ImGui internals (exported by cimgui, which ImGui.NET ships): the current window and its display order.
    [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr igGetCurrentWindow();
    [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)] private static extern void igBringWindowToDisplayBack(IntPtr window);
    private static bool internalsMissing;

    /**
     * Moves the current window to the back of the display order without focusing it. Tiled panels call this every frame,
     * so the panel frame overlay, floating windows, popups and toasts always draw above them.
     */
    public static void SendToBack()
    {
        if (internalsMissing) return;
        try
        {
            var window = igGetCurrentWindow();
            if (window != IntPtr.Zero) igBringWindowToDisplayBack(window);
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException) { internalsMissing = true; }
    }

    public static Vector4 Alpha(Vector4 color, float alpha) => color with { W = alpha };
    public static uint U32(Vector4 color) => ImGui.GetColorU32(color);

    /**
     * Installs the faces for ImGuiController.HostFonts. The controller's body size is 16 px at 96 DPI, so its ratio to 16
     * is the DPI scale. Returns null, adding nothing, when an embedded face is missing.
     */
    public static ImFontPtr? AddFonts(ImFontAtlasPtr fonts, float controllerBodySize)
    {
        HeadingFont = default; ButtonFont = default; LabelFont = default; TitleFont = default; StrongFont = default; BrandFont = default;
        CaptionFont = default; WordmarkFont = default;
        if (!LoadFaces() || faces is not { } loaded) return null;
        float scale = Math.Max(1, controllerBodySize / 16f);
        var body = systemFaces?[0] ?? loaded[0];
        var bodyStrong = systemFaces?[1] ?? loaded[1];
        AddFace(fonts, body, BodySize * scale, BodyRanges); // first face: ImGui's default font
        StrongFont = AddFace(fonts, bodyStrong, BodySize * scale, BodyRanges);
        HeadingFont = StrongFont;
        ButtonFont = AddFace(fonts, loaded[2], ButtonSize * scale);
        // Labels and buttons share one size, so one atlas entry serves both.
        LabelFont = LabelSize == ButtonSize ? ButtonFont : AddFace(fonts, loaded[2], LabelSize * scale);
        CaptionFont = AddFace(fonts, loaded[4], 11 * scale);
        WordmarkFont = AddFace(fonts, loaded[3], 15 * scale);
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
            loaded[i] = Native(bytes);
        }
        faces = loaded;
        // Segoe UI and Segoe UI Semibold, the mockup's system-ui body; both or neither, so weights match.
        try
        {
            string folder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            var paths = SystemFaces.Select(name => Path.Combine(folder, name)).ToArray();
            if (paths.All(File.Exists) && paths.All(path => new FileInfo(path).Length is > 0 and < 16 << 20))
                systemFaces = paths.Select(path => ((IntPtr, int)?)Native(File.ReadAllBytes(path))).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { systemFaces = null; }
        return true;
    }

    private static (IntPtr Data, int Length) Native(byte[] bytes)
    {
        var data = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, data, bytes.Length);
        return (data, bytes.Length);
    }

    private static ImFontPtr AddFace(ImFontAtlasPtr fonts, (IntPtr Data, int Length) face, float pixels, ushort* ranges = null)
    {
        ImFontConfigPtr config = ImGuiNative.ImFontConfig_ImFontConfig();
        config.FontDataOwnedByAtlas = false;
        var font = fonts.AddFontFromMemoryTTF(face.Data, face.Length, MathF.Round(pixels), config, (IntPtr)ranges);
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

    /** The mockup's colours, square ImGui widgets with 1 px frames, and sizes on the current context; safe to call again on a DPI change. */
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
        // 13 px body text in a 26 px control, as the mockup's inputs and buttons.
        style.FramePadding = new(8, 4);
        style.ItemSpacing = new(6, 5);
        style.ItemInnerSpacing = new(6, 4);
        style.IndentSpacing = 14;
        style.CellPadding = new(6, 3);
        // ImGui's own widgets cannot be chamfered; square corners keep them in the same family as the chamfered chrome.
        style.WindowRounding = 0;
        style.ChildRounding = 0;
        style.PopupRounding = RoundingPopup;
        style.FrameRounding = Rounding;
        style.GrabRounding = Rounding;
        style.TabRounding = RoundingTab;
        style.ScrollbarRounding = RoundingScrollbar;
        style.WindowBorderSize = 0;
        style.ChildBorderSize = 1;
        // Popups draw their own chamfered frame (PopupShadow); a plain popup keeps a 1 px LineStrong frame.
        style.PopupBorderSize = 1;
        // Outlined controls: a 1 px LineStrong frame on buttons, fields and checkboxes, as the mockup.
        style.FrameBorderSize = 1;
        style.TabBorderSize = 0;
        style.TabBarBorderSize = 1;
        // The active tab is marked by the amber underline (UiMotion.TabUnderline), not ImGui's overline.
        style.TabBarOverlineSize = 0;
        style.ScrollbarSize = 10;
        style.GrabMinSize = 8;
        style.SeparatorTextBorderSize = 1;
        style.SeparatorTextPadding = new(0, 4);
        style.DisabledAlpha = .5f;
        var colors = style.Colors;
        void Set(ImGuiCol slot, Vector4 value) => colors[(int)slot] = value;
        var clear = Vector4.Zero;
        Set(ImGuiCol.Text, Text);
        // Shortcuts in menus and secondary text read --text-dim (about 7:1 on the panel fill).
        Set(ImGuiCol.TextDisabled, Muted);
        Set(ImGuiCol.WindowBg, Panel);
        Set(ImGuiCol.ChildBg, clear);
        Set(ImGuiCol.PopupBg, Popup);
        Set(ImGuiCol.Border, LineStrong);
        Set(ImGuiCol.BorderShadow, clear);
        Set(ImGuiCol.FrameBg, Input);
        Set(ImGuiCol.FrameBgHovered, InputHover);
        Set(ImGuiCol.FrameBgActive, Input);
        Set(ImGuiCol.TitleBg, Panel);
        Set(ImGuiCol.TitleBgActive, Panel);
        Set(ImGuiCol.TitleBgCollapsed, Panel);
        Set(ImGuiCol.MenuBarBg, Bar);
        Set(ImGuiCol.ScrollbarBg, clear);
        Set(ImGuiCol.ScrollbarGrab, ScrollGrab);
        Set(ImGuiCol.ScrollbarGrabHovered, ScrollGrabHover);
        Set(ImGuiCol.ScrollbarGrabActive, ScrollGrabActive);
        Set(ImGuiCol.CheckMark, Accent);
        Set(ImGuiCol.SliderGrab, Accent);
        Set(ImGuiCol.SliderGrabActive, AccentHover);
        // Buttons are outlined (no fill); hover is the amber wash, pressed a stronger one.
        Set(ImGuiCol.Button, Surface);
        Set(ImGuiCol.ButtonHovered, AccentSoft);
        Set(ImGuiCol.ButtonActive, Pressed);
        // Selected rows are an amber wash; hovered rows --panel-2.
        Set(ImGuiCol.Header, Selection);
        Set(ImGuiCol.HeaderHovered, Panel2);
        Set(ImGuiCol.HeaderActive, Selection);
        Set(ImGuiCol.Separator, Line);
        Set(ImGuiCol.SeparatorHovered, Alpha(Accent, .6f));
        Set(ImGuiCol.SeparatorActive, Accent);
        Set(ImGuiCol.ResizeGrip, clear);
        Set(ImGuiCol.ResizeGripHovered, Alpha(Accent, .5f));
        Set(ImGuiCol.ResizeGripActive, Accent);
        Set(ImGuiCol.Tab, clear);
        Set(ImGuiCol.TabHovered, Panel2);
        Set(ImGuiCol.TabSelected, clear);
        Set(ImGuiCol.TabSelectedOverline, Accent);
        Set(ImGuiCol.TabDimmed, clear);
        Set(ImGuiCol.TabDimmedSelected, clear);
        Set(ImGuiCol.TabDimmedSelectedOverline, Alpha(Accent, .5f));
        Set(ImGuiCol.PlotLines, Muted);
        Set(ImGuiCol.PlotLinesHovered, Accent);
        Set(ImGuiCol.PlotHistogram, Accent);
        Set(ImGuiCol.PlotHistogramHovered, AccentHover);
        Set(ImGuiCol.TableHeaderBg, Panel);
        Set(ImGuiCol.TableBorderStrong, Line);
        Set(ImGuiCol.TableBorderLight, LineSoft);
        Set(ImGuiCol.TableRowBg, clear);
        Set(ImGuiCol.TableRowBgAlt, White(.02f));
        Set(ImGuiCol.TextLink, AccentHover);
        Set(ImGuiCol.TextSelectedBg, Selection);
        Set(ImGuiCol.DragDropTarget, Accent);
        // Keyboard focus stays visible: a 2 px amber ring, as the mockup's :focus-visible outline.
        Set(ImGuiCol.NavCursor, Accent);
        Set(ImGuiCol.NavWindowingHighlight, Accent);
        Set(ImGuiCol.NavWindowingDimBg, new(0, 0, 0, .5f));
        // The dialog backdrop: hsla(0,0%,0%,.66).
        Set(ImGuiCol.ModalWindowDimBg, new(0, 0, 0, .66f));
        // Collapsing headers stay neutral (--panel-2), so only selected rows carry the amber wash.
        UiAuto.HeaderColors = (Panel2, Grey(16), Pressed);
        style.ScaleAllSizes(Scale);
        ImGui.GetIO().FontGlobalScale = 1;
    }

    // ---- Chamfers ----

    /**
     * The chamfer outline (the mockup's clip-path polygon): top-left and bottom-right corners cut by cut pixels (already
     * scaled, clamped to half the short side). A cut of 0 or less is a plain rectangle.
     */
    public static void ChamferPath(ImDrawListPtr draw, Vector2 min, Vector2 max, float cut)
    {
        cut = Math.Clamp(cut, 0, Math.Min(max.X - min.X, max.Y - min.Y) * .5f);
        if (cut <= .5f) { draw.PathRect(min, max); return; }
        draw.PathLineTo(new(min.X + cut, min.Y));
        draw.PathLineTo(new(max.X, min.Y));
        draw.PathLineTo(new(max.X, max.Y - cut));
        draw.PathLineTo(new(max.X - cut, max.Y));
        draw.PathLineTo(new(min.X, max.Y));
        draw.PathLineTo(new(min.X, min.Y + cut));
    }

    /** A chamfered box; a zero colour skips the fill or the 1 px frame. Cut is in unscaled pixels; 0 is the control cut (5 px). */
    public static void ChamferFrame(ImDrawListPtr draw, Vector2 min, Vector2 max, float cut, uint fill, uint border = 0)
    {
        if (max.X - min.X < 1 || max.Y - min.Y < 1) return;
        float pixels = (cut > 0 ? cut : CutTiny) * Scale;
        if (fill != 0) { ChamferPath(draw, min, max, pixels); draw.PathFillConvex(fill); }
        if (border != 0)
        {
            // Half-pixel inset keeps the 1 px stroke crisp and inside the box; the cut shrinks with it so the diagonal stays parallel.
            ChamferPath(draw, min + new Vector2(.5f), max - new Vector2(.5f), Math.Max(0, pixels - .2f));
            draw.PathStroke(border, ImDrawFlags.Closed, 1);
        }
    }

    /** A chamfered box filled with the accent ramp (accent hover -> accent -> accent pressed, left to right). */
    public static void ChamferGradient(ImDrawListPtr draw, Vector2 min, Vector2 max, float cut, float alpha = 1)
    {
        if (max.X - min.X < 1 || max.Y - min.Y < 1) return;
        int first = draw.VtxBuffer.Size;
        ChamferPath(draw, min, max, (cut > 0 ? cut : CutTiny) * Scale);
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
     * The panel frame of a tiled area (the mockup's .panel): the corners outside the chamfer painted in the window
     * background, then the 1 px Line ring. Drawn on the frame overlay, above the area's own windows.
     */
    public static void PanelFrame(ImDrawListPtr draw, Vector2 min, Vector2 max)
    {
        if (max.X - min.X < 4 || max.Y - min.Y < 4) return;
        float cut = Math.Min(CutSmall * Scale, Math.Min(max.X - min.X, max.Y - min.Y) * .5f);
        uint bg = U32(Bg);
        draw.AddTriangleFilled(min, new(min.X + cut, min.Y), new(min.X, min.Y + cut), bg);
        draw.AddTriangleFilled(max, new(max.X, max.Y - cut), new(max.X - cut, max.Y), bg);
        ChamferPath(draw, min + new Vector2(.5f), max - new Vector2(.5f), Math.Max(0, cut - .2f));
        draw.PathStroke(U32(Line), ImDrawFlags.Closed, 1);
    }

    /**
     * Hides the current window's own background (its first rectangle, drawn by Begin) and draws a chamfered fill and a
     * 1 px frame in its place, under everything drawn after this call. A window whose first primitive is not its
     * background rectangle keeps it; only the frame is added then. Used by popups (PopupShadow) and toasts.
     */
    public static void ChamferWindow(float cut, Vector4 fill, Vector4 frame)
    {
        var draw = ImGui.GetWindowDrawList();
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        bool hidden = HideBackground(draw, min, max);
        draw.PushClipRectFullScreen();
        ChamferFrame(draw, min, max, cut, hidden ? U32(fill) : 0, U32(frame));
        draw.PopClipRect();
        if (hidden) DrawScrollGrab(draw, min, max);
    }

    /**
     * Makes the window's own background transparent: its first four vertices when they are the window rectangle, and the
     * square 1 px frame Begin drew right after it (the vertices that follow while they lie on the window's outline).
     */
    private static bool HideBackground(ImDrawListPtr draw, Vector2 min, Vector2 max)
    {
        int count = draw.VtxBuffer.Size;
        if (count < 4) return false;
        var vertices = (Vertex*)(void*)draw.VtxBuffer.Data;
        Vector2 low = vertices[0].Position, high = vertices[0].Position;
        for (int i = 1; i < 4; i++) { low = Vector2.Min(low, vertices[i].Position); high = Vector2.Max(high, vertices[i].Position); }
        if (Vector2.Distance(low, min) > 1.5f || Vector2.Distance(high, max) > 1.5f) return false;
        for (int i = 0; i < 4; i++) vertices[i].Color &= 0x00FFFFFF;
        bool OnOutline(Vector2 p) => MathF.Abs(p.X - min.X) <= 1.6f || MathF.Abs(p.X - max.X) <= 1.6f
            || MathF.Abs(p.Y - min.Y) <= 1.6f || MathF.Abs(p.Y - max.Y) <= 1.6f;
        for (int i = 4; i < Math.Min(count, 4 + 48) && OnOutline(vertices[i].Position); i++) vertices[i].Color &= 0x00FFFFFF;
        return true;
    }

    /** The vertical scroll grab of a window whose own scrollbar the chamfered fill covers. */
    private static void DrawScrollGrab(ImDrawListPtr draw, Vector2 min, Vector2 max)
    {
        float scrollMax = ImGui.GetScrollMaxY();
        if (scrollMax <= 0) return;
        float height = max.Y - min.Y, visible = height / (height + scrollMax);
        float grab = Math.Max(16 * Scale, (height - 8 * Scale) * visible);
        float top = min.Y + 4 * Scale + (height - 8 * Scale - grab) * (ImGui.GetScrollY() / scrollMax);
        draw.AddRectFilled(new(max.X - 6 * Scale, top), new(max.X - 3 * Scale, top + grab), U32(ScrollGrabHover));
    }

    /**
     * The progress bar (the mockup's .bar): a flat accent ramp along x (or the brand ramp for the loading card), drawn as
     * slices so the middle stop holds. A sheen in [0, 1) sweeps a pale band across it; a negative sheen leaves it still.
     */
    public static void ProgressFill(ImDrawListPtr draw, Vector2 min, Vector2 max, float sheen = -1, bool brand = false)
    {
        float width = max.X - min.X, height = max.Y - min.Y;
        if (width < 1 || height < 1) return;
        Vector4 start = brand ? BrandEdge : AccentHover, middle = brand ? Brand : Accent, end = brand ? Brand2 : AccentActive;
        float band = Math.Max(height * 8, width * .18f), center = min.X - band + (width + band * 2) * sheen;
        Vector4 At(float x)
        {
            float t = (x - min.X) / width, u = (x - center) / band;
            var color = t < .5f ? Vector4.Lerp(start, middle, t * 2) : Vector4.Lerp(middle, end, (t - .5f) * 2);
            return sheen < 0 || u <= -1 || u >= 1 ? color : Vector4.Lerp(color, TextStrong, (1 - u * u) * (1 - u * u) * .45f);
        }
        int slices = Math.Clamp((int)(width / (3 * Scale)), 1, 128);
        for (int i = 0; i < slices; i++)
        {
            float x0 = min.X + width * i / slices, x1 = min.X + width * (i + 1) / slices;
            uint a = U32(At(x0)), b = U32(At(x1));
            draw.AddRectFilledMultiColor(new(x0, min.Y), new(x1, max.Y), a, b, b, a);
        }
    }

    /** The panel fill behind the current window's content; a window that pushed its own background (a splitter, an overlay) keeps it. */
    public static void PanelGradient()
    {
        if (ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg] != Panel) return;
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        uint top = U32(PanelTop), bottom = U32(PanelBottom);
        if (top == bottom) ImGui.GetWindowDrawList().AddRectFilled(min, max, top);
        else ImGui.GetWindowDrawList().AddRectFilledMultiColor(min, max, top, top, bottom, bottom);
    }

    /** The status bar: the panel fill with a 1 px Line on top (the mockup's .statusbar). */
    public static void StatusBackground()
    {
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, max, U32(Bar));
        draw.AddLine(min + new Vector2(0, .5f), new(max.X, min.Y + .5f), U32(Border), 1);
    }

    /**
     * Turns the current popup into the mockup's menu frame: a chamfered (8 px) panel fill with a 1 px LineStrong frame
     * in place of ImGui's square background. Call right after BeginPopup, BeginMenu, BeginCombo or BeginTooltip returns
     * true. (The name is kept from the soft shadow it used to draw.)
     */
    public static void PopupShadow() => ChamferWindow(CutSmall, Popup, LineStrong);

    /** The mockup's toast: an 8 px chamfered hsl(0,0%,12%) fill with a 1 px amber frame, in place of the window's background. */
    public static void ToastFrame() => ChamferWindow(CutSmall, ToastFill, Accent);

    /** A chamfered frame around a rectangle, for a popup drawn by hand. */
    public static void PopupShadow(ImDrawListPtr draw, Vector2 min, Vector2 max)
    {
        if (max.X - min.X < 1 || max.Y - min.Y < 1) return;
        draw.PushClipRectFullScreen();
        ChamferFrame(draw, min, max, CutSmall, 0, U32(LineStrong));
        draw.PopClipRect();
    }

    // ---- Dialogs ----

    /** Window padding of a dialog's body (the mockup's 14 px 18 px), unscaled. */
    private const float DialogPadX = 18, DialogPadY = 14;

    /**
     * BeginPopupModal drawn as the mockup's dialog: a chamfered (14 px) panel with a 1 px LineStrong frame, an upper-case
     * Chakra Petch title over a hairline and the close cross when open is given. ImGui still owns the window (moving,
     * the title bar's hit area, the close button's click); its own decorations are drawn transparent and covered.
     */
    public static bool BeginDialog(string name, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
    {
        PushDialog();
        bool open = ImGui.BeginPopupModal(name, flags);
        PopDialog(open, name, closable: false);
        return open;
    }

    /** BeginDialog with ImGui's close button (open is set false when it is clicked). */
    public static bool BeginDialog(string name, ref bool open, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
    {
        PushDialog();
        bool shown = ImGui.BeginPopupModal(name, ref open, flags);
        PopDialog(shown, name, closable: true);
        return shown;
    }

    private const int DialogColors = 9;

    private static void PushDialog()
    {
        // The title bar is as tall as the mockup's dialog head: body line plus 2 x 9 px.
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(DialogPadX * Scale, 9 * Scale));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(DialogPadX * Scale, DialogPadY * Scale));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 0f);
        var clear = Vector4.Zero;
        ImGui.PushStyleColor(ImGuiCol.PopupBg, clear);
        ImGui.PushStyleColor(ImGuiCol.TitleBg, clear);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, clear);
        ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed, clear);
        ImGui.PushStyleColor(ImGuiCol.Text, clear);
        ImGui.PushStyleColor(ImGuiCol.Button, clear);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, clear);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, clear);
        ImGui.PushStyleColor(ImGuiCol.Border, clear);
    }

    private static void PopDialog(bool open, string name, bool closable)
    {
        ImGui.PopStyleColor(DialogColors);
        // The frame padding was read by Begin for the title bar; contents use the normal one.
        float titleHeight = ImGui.GetFontSize() + 9 * Scale * 2;
        ImGui.PopStyleVar(3);
        if (!open) return;
        // The window padding stays the dialog's for its contents (WindowPadding is read at Begin).
        var draw = ImGui.GetWindowDrawList();
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        draw.PushClipRectFullScreen();
        ChamferFrame(draw, min, max, Cut, U32(Panel), 0);
        draw.AddLine(new(min.X, min.Y + titleHeight - .5f), new(max.X, min.Y + titleHeight - .5f), U32(Line), 1);
        string title = UiTitle(name).ToUpperInvariant();
        bool font = PushFont(LabelFont);
        float textY = min.Y + (titleHeight - ImGui.GetTextLineHeight()) / 2;
        float right = max.X - (closable ? titleHeight : 0) - DialogPadX * Scale * .5f;
        draw.PushClipRect(min, new(right, min.Y + titleHeight), true);
        TrackedText(draw, new(min.X + DialogPadX * Scale, textY), U32(TextStrong), title, ImGui.GetFontSize() * TrackLabel);
        draw.PopClipRect();
        if (font) ImGui.PopFont();
        if (closable)
        {
            // ImGui's close button: FontSize square, FramePadding.x from the right edge of the title bar.
            float size = ImGui.GetFontSize();
            var buttonMin = new Vector2(max.X - DialogPadX * Scale - size, min.Y + 9 * Scale);
            bool hovered = ImGui.IsMouseHoveringRect(buttonMin, buttonMin + new Vector2(size)) && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows);
            ImGuiIcons.Draw(draw, StudioIcon.Close, buttonMin, size, U32(hovered ? Accent : Muted));
        }
        ChamferFrame(draw, min, max, Cut, 0, U32(LineStrong));
        draw.PopClipRect();
        DrawScrollGrab(draw, new(min.X, min.Y + titleHeight), max);
    }

    /** The text ImGui shows for a window or popup name: before "###" or "##". */
    private static string UiTitle(string name)
    {
        int hash = name.IndexOf("##", StringComparison.Ordinal);
        return hash < 0 ? name : name[..hash];
    }

    // ---- Text ----

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

    public static float TrackedWidth(string text, float tracking)
    {
        float width = 0;
        for (int i = 0; i < text.Length; i++)
            width += ImGui.CalcTextSize(text[i] < Glyphs.Length ? Glyphs[text[i]] : text[i].ToString()).X;
        return width + tracking * Math.Max(0, text.Length - 1);
    }

    /** Width of an upper-case Chakra Petch label at the given font and tracking (in em). */
    public static float LabelWidth(ImFontPtr font, string text, float trackEm)
    {
        bool pushed = PushFont(font);
        float width = TrackedWidth(text.ToUpperInvariant(), ImGui.GetFontSize() * trackEm);
        if (pushed) ImGui.PopFont();
        return width;
    }

    /** Draws an upper-case Chakra Petch label at the given font and tracking (in em); returns its width. */
    public static float Label(ImDrawListPtr draw, ImFontPtr font, Vector2 position, uint color, string text, float trackEm)
    {
        bool pushed = PushFont(font);
        float width = TrackedText(draw, position, color, text.ToUpperInvariant(), ImGui.GetFontSize() * trackEm);
        if (pushed) ImGui.PopFont();
        return width;
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

    /** A 12 px Chakra Petch upper-case label in --text-dim over a soft hairline: a group header (the mockup's .group > h3). */
    public static void SectionHeader(string text, string tooltip = "")
    {
        bool font = PushFont(LabelFont);
        string label = text.ToUpperInvariant();
        float tracking = ImGui.GetFontSize() * TrackLabel;
        var draw = ImGui.GetWindowDrawList();
        ImGui.Dummy(new(0, Space2));
        var start = ImGui.GetCursorScreenPos();
        float width = Math.Max(1, ImGui.GetContentRegionAvail().X);
        float height = ImGui.GetTextLineHeight() + Space3;
        ImGui.Dummy(new(width, height));
        TrackedText(draw, start, U32(Muted), label, tracking);
        draw.AddLine(new(start.X, start.Y + height - 1), new(start.X + width, start.Y + height - 1), U32(LineSoft), 1);
        if (font) ImGui.PopFont();
        if (tooltip.Length != 0 && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
    }

    /**
     * A collapsing group: the panel fill, a chevron that rotates by animation and a Chakra Petch upper-case label (the
     * mockup's collapsible panel head). The label's id is kept.
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
        draw.AddLine(new(min.X, max.Y - .5f), new(max.X, max.Y - .5f), U32(LineSoft), 1);
        float size = ImGui.GetFontSize();
        float arrow = size + ImGui.GetStyle().FramePadding.X * 2;
        float textTop = min.Y + (max.Y - min.Y - ImGui.GetTextLineHeight()) / 2;
        var chevronCenter = new Vector2(min.X + ImGui.GetStyle().FramePadding.X + size * .5f, (min.Y + max.Y) * .5f);
        var tint = UiMotion.EaseColor(UiMotion.Mix(id, 0x7E11u), hovered ? TextStrong : Muted);
        UiMotion.DrawChevron(draw, chevronCenter, size * .45f, UiMotion.Chevron(id, open), U32(tint));
        TrackedText(draw, new(min.X + arrow, textTop), U32(tint), shown, size * TrackLabel);
        if (font) ImGui.PopFont();
        return open;
    }

    /**
     * The primary button (the mockup's .tb-btn.primary): a chamfered amber fill with an upper-case Chakra Petch label in
     * --accent-ink; its fill eases between rest, hover (--accent-hi) and pressed.
     */
    public static bool PrimaryButton(string label, Vector2 size = default)
    {
        int split = label.IndexOf("##", StringComparison.Ordinal);
        string shown = (split < 0 ? label : label[..split]).ToUpperInvariant();
        // One body-font frame tall, like the controls beside it; the smaller button face would make it shorter.
        if (size.Y <= 0) size.Y = ImGui.GetFrameHeight();
        bool font = PushFont(ButtonFont);
        float tracking = ImGui.GetFontSize() * TrackButton;
        float textWidth = TrackedWidth(shown, tracking);
        if (size.X <= 0) size.X = textWidth + PrimaryPadding * 2 * Scale;
        bool clicked = UiAuto.InvisibleButton(label, Vector2.Max(size, Vector2.One));
        bool hovered = ImGui.IsItemHovered(), held = ImGui.IsItemActive();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var draw = ImGui.GetWindowDrawList();
        var fill = UiMotion.EaseColor(ImGui.GetItemID(), held ? AccentActive : hovered ? AccentHover : Accent, 35f);
        ChamferFrame(draw, min, max, CutTiny, U32(fill));
        var textPosition = new Vector2(MathF.Round(min.X + (max.X - min.X - textWidth) / 2), MathF.Round(min.Y + (max.Y - min.Y - ImGui.GetTextLineHeight()) / 2));
        TrackedText(draw, textPosition, U32(AccentInk), shown, tracking);
        if (font) ImGui.PopFont();
        return clicked;
    }

    /** Horizontal padding each side of a PrimaryButton label, in unscaled pixels (the mockup's 12 px). */
    private const float PrimaryPadding = 12;

    /** The width PrimaryButton gives the label, for laying out a row before drawing it. */
    public static float PrimaryButtonWidth(string label)
    {
        int split = label.IndexOf("##", StringComparison.Ordinal);
        string shown = (split < 0 ? label : label[..split]).ToUpperInvariant();
        bool font = PushFont(ButtonFont);
        float width = TrackedWidth(shown, ImGui.GetFontSize() * TrackButton) + PrimaryPadding * 2 * Scale;
        if (font) ImGui.PopFont();
        return width;
    }

    /** A small mode button (Object / Asset): no fill, its label easing to strong text and the amber underline when active. */
    public static bool ModeButton(string label, bool active)
    {
        bool font = PushFont(HeadingFont);
        uint id = ImGui.GetID(label);
        var text = UiMotion.EaseColor(UiMotion.Mix(id, 0x40DEu), active ? TextStrong : Muted);
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Text, text);
        bool clicked = UiAuto.SmallButton(label);
        ImGui.PopStyleColor(3);
        if (font) ImGui.PopFont();
        if (active) Underline();
        return clicked;
    }

    /** The amber underline under the last item: the active tab or mode. */
    public static void Underline()
    {
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        AngledUnderline(ImGui.GetWindowDrawList(), min.X, max.X, max.Y, 0);
    }

    /**
     * The mockup's active-tab mark: a 2 px amber bar on the bottom edge whose ends slope in by 2 px (clip-path
     * polygon(0 100%, 2px 0, calc(100% - 2px) 0, 100% 100%)), inset from both sides of the tab.
     */
    public static void AngledUnderline(ImDrawListPtr draw, float left, float right, float bottom, float inset, float alpha = 1)
    {
        float x0 = left + inset * Scale, x1 = right - inset * Scale, height = 2 * Scale, slope = 2 * Scale;
        if (x1 - x0 < slope * 2 + 1) return;
        uint color = U32(Alpha(Accent, alpha));
        draw.AddQuadFilled(new(x0, bottom), new(x0 + slope, bottom - height), new(x1 - slope, bottom - height), new(x1, bottom), color);
    }

    /** A 2 px accent bar on the left of a row: the selected list row (the mockup's border-left). */
    public static void LeftBar(ImDrawListPtr draw, Vector2 min, Vector2 max)
        => draw.AddRectFilled(min, new(min.X + 2 * Scale, max.Y), U32(Accent));

    /** A thin accent tick at the start of a header. */
    public static void TitleNotch(ImDrawListPtr draw, Vector2 min, float height)
        => draw.AddRectFilled(min, new(min.X + 2 * Scale, min.Y + height), U32(Accent));

    /** Space a panel header leaves before its title. */
    public static float HeaderIndent => Space5;

    /** The panel header height (30 px at 100 %). */
    public static float PanelHeaderHeight => PanelHeaderSize * Scale;

    /**
     * A panel header (the mockup's .panel-head): the panel fill with a hairline under it and the title in upper-case
     * Chakra Petch, --text-dim, tracked .1 em. The title is drawn where the next item (an editor combo with an empty
     * preview) sits, clipped to itemWidth.
     */
    public static void PanelHeader(string title, float itemWidth)
    {
        var draw = ImGui.GetWindowDrawList();
        var windowMin = ImGui.GetWindowPos();
        var windowMax = windowMin + ImGui.GetWindowSize();
        draw.AddRectFilled(windowMin, windowMax, U32(Header));
        draw.AddLine(new(windowMin.X, windowMax.Y - .5f), new(windowMax.X, windowMax.Y - .5f), U32(Line), 1);
        var at = ImGui.GetCursorScreenPos();
        float frame = ImGui.GetFrameHeight();
        bool font = PushFont(LabelFont);
        var textAt = new Vector2(at.X, at.Y + (frame - ImGui.GetTextLineHeight()) / 2);
        draw.PushClipRect(windowMin, new(Math.Min(windowMax.X, at.X + itemWidth + HeaderIndent), windowMax.Y), true);
        TrackedText(draw, textAt, U32(Muted), title.ToUpperInvariant(), ImGui.GetFontSize() * TrackLabel);
        draw.PopClipRect();
        if (font) ImGui.PopFont();
        ImGui.SetCursorScreenPos(new(at.X + HeaderIndent, at.Y));
    }

    /**
     * One tab of an area header (the mockup's .tablist tab): no fill, an upper-case Chakra Petch label in --text-dim, or
     * --text when active or hovered, and the amber angled underline under the active tab. hover in [0, 1] (an eased
     * value) lifts an inactive label toward --text. accentLine false leaves the underline to the caller (area tabs draw
     * one sprung line that slides between tabs); activeBlend in [0, 1] eases the label colour instead of switching it.
     */
    public static void PanelTab(ImDrawListPtr draw, Vector2 min, Vector2 max, string label, bool active, float hover = 0,
        bool accentLine = true, float activeBlend = -1)
    {
        float on = activeBlend >= 0 ? Math.Clamp(activeBlend, 0, 1) : active ? 1 : 0;
        if (hover > 0 && on < 1) draw.AddRectFilled(min, max, U32(Alpha(Panel2, Math.Clamp(hover, 0, 1) * (1 - on) * .6f)));
        if (active && accentLine) AngledUnderline(draw, min.X, max.X, max.Y, 10);
        if (label.Length == 0) return;
        bool font = PushFont(LabelFont);
        var textAt = new Vector2(min.X + Space5, MathF.Round(min.Y + (max.Y - min.Y - ImGui.GetTextLineHeight()) / 2));
        var color = Vector4.Lerp(Vector4.Lerp(Muted, Text, Math.Clamp(hover, 0, 1)), TextStrong, on);
        draw.PushClipRect(min, max, true);
        TrackedText(draw, textAt, U32(color), label.ToUpperInvariant(), ImGui.GetFontSize() * TrackLabel);
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

    /** A viewport label chip (the mockup's .vp-title): translucent panel fill, chamfered, amber frame when the pointer is over it. */
    public static void Chip(ImDrawListPtr draw, Vector2 min, Vector2 max, bool hovered)
        => ChamferFrame(draw, min, max, CutTiny, U32(Alpha(Panel, .94f)), U32(hovered ? Accent : Line));

    /** Icon tint: text at rest, amber on hover and when toggled on (the mockup's .icon-btn). */
    public static uint IconTint(bool active, bool hovered) => U32(IconTintColor(active, hovered));

    /** The icon tint as a colour, for easing between states. */
    public static Vector4 IconTintColor(bool active, bool hovered) => active || hovered ? Accent : Text;
}
