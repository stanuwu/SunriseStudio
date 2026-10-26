using System.Numerics;

namespace SunriseStudio.Viewport;

/**
 * The UE5 token table (docs/UI-UE5.md §2.1 and §2.2). The Sol website names (Bg, Panel, Sun, Line, ...) are kept so
 * every call site compiles; they now hold the UE5 editor greys and blue accent. The old Sol orange is Brand, kept for
 * the brand mark, the Solforge AI glyph and the loading card.
 */
public static partial class SolTheme
{
    private static Vector4 Hex(uint rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1);
    private static Vector4 White(float alpha) => new(1, 1, 1, alpha);

    // ---- Surfaces ----
    /** Window background, viewport letterbox, recessed wells. */
    public static readonly Vector4 Bg = Hex(0x151515);
    /** Alias of Bg. */
    public static readonly Vector4 Canvas = Bg;
    /** Menu bar and status bar. */
    public static readonly Vector4 Bg2 = Hex(0x1a1a1a);
    /** Panel bodies and the active tab. */
    public static readonly Vector4 Panel = Hex(0x1f1f1f);
    /** Both stops equal Panel, so PanelGradient draws a flat fill. */
    public static readonly Vector4 PanelTop = Panel;
    public static readonly Vector4 PanelBottom = Panel;
    /** The menu and status bars (Bg2). */
    public static readonly Vector4 Bar = Bg2;
    /** Inactive tabs, panel headers, table headers. */
    public static readonly Vector4 Header = Hex(0x2a2a2a);
    /** Buttons and frames at rest. */
    public static readonly Vector4 Surface = Hex(0x2a2a2a);
    /** Hover fill (alias Hover). */
    public static readonly Vector4 Surface2 = Hex(0x383838);
    public static readonly Vector4 Hover = Surface2;
    /** Active (pressed) fill. */
    public static readonly Vector4 Pressed = Hex(0x454545);
    /** Text inputs, combos, search boxes. */
    public static readonly Vector4 Input = Hex(0x0f0f0f);
    /** A hovered input: one step lighter than Input. */
    public static readonly Vector4 InputHover = Hex(0x161616);
    /** Popup and menu background. */
    public static readonly Vector4 Popup = Hex(0x242424);
    /** 1 px separators between areas (alias Line). */
    public static readonly Vector4 Border = Hex(0x0b0b0b);
    public static readonly Vector4 Line = Border;
    /** 1 px inner highlight on raised controls and popups (alias Line2). */
    public static readonly Vector4 BorderHi = Hex(0x333333);
    public static readonly Vector4 Line2 = BorderHi;
    /** Light table row borders. */
    public static readonly Vector4 TableLine = Hex(0x262626);
    /** Scrollbar grab at rest, hovered and dragged: neutral greys, as UE5. */
    public static readonly Vector4 ScrollGrab = Hex(0x424242);
    public static readonly Vector4 ScrollGrabHover = Hex(0x505050);
    public static readonly Vector4 ScrollGrabActive = Hex(0x5e5e5e);
    /** Formerly the Sol background steps; now the flat Header and Surface fills. */
    public static readonly Vector4 Step = Header;
    public static readonly Vector4 ButtonFill = Surface;

    // ---- Text ----
    /** Body text. */
    public static readonly Vector4 Text = Hex(0xc8c8c8);
    /** Titles, selected rows. */
    public static readonly Vector4 TextStrong = Hex(0xececec);
    /** Secondary labels (alias TextMuted). */
    public static readonly Vector4 Muted = Hex(0x8a8a8a);
    public static readonly Vector4 TextMuted = Muted;
    /** Disabled text (alias TextDim). */
    public static readonly Vector4 Dim = Hex(0x5e5e5e);
    public static readonly Vector4 TextDim = Dim;
    /** Grid lines and placeholder glyphs. */
    public static readonly Vector4 Faint = Hex(0x3a3a3a);

    // ---- Accent ----
    /** Selection, focus, active tab top line, primary buttons. Sun is the old name. */
    public static readonly Vector4 Accent = Hex(0x0070e0);
    public static readonly Vector4 Sun = Accent;
    /** Primary hover. SunHi is the old name. */
    public static readonly Vector4 AccentHover = Hex(0x1a85ff);
    public static readonly Vector4 SunHi = AccentHover;
    /** Primary pressed. Sun2 is the old name. */
    public static readonly Vector4 AccentActive = Hex(0x0058b3);
    public static readonly Vector4 Sun2 = AccentActive;
    /** The accent ramp ends for ChamferGradient (formerly the sun-grad stops). */
    public static readonly Vector4 SunGradStart = AccentHover;
    public static readonly Vector4 SunGradEnd = AccentActive;
    /** Text on an Accent fill. */
    public static readonly Vector4 AccentInk = Hex(0xffffff);
    /** Selected rows and text selection; a hovered selected row. */
    public static readonly Vector4 Selection = Accent with { W = .35f };
    public static readonly Vector4 SelectionHover = Accent with { W = .20f };

    // ---- Brand (the old Sol orange) ----
    /** Brand mark, Solforge AI glyph and the loading card only. */
    public static readonly Vector4 Brand = Hex(0xf2a33a);
    public static readonly Vector4 BrandHi = Hex(0xffc061);
    /** The deep end of the brand ramp (the launcher's sun-2). */
    public static readonly Vector4 Brand2 = Hex(0xff7a2f);
    /** The light ends of the brand shimmer (#ffe0a0). SunShimmerEdge is the old name. */
    public static readonly Vector4 BrandEdge = Hex(0xffe0a0);
    public static readonly Vector4 SunShimmerEdge = BrandEdge;

    // ---- State ----
    /** Running, success. */
    public static readonly Vector4 Ok = Hex(0x6cbf43);
    /** Launching, pending approvals. */
    public static readonly Vector4 Warn = Hex(0xe3a33a);
    /** Errors, failed. */
    public static readonly Vector4 Bad = Hex(0xe05252);

    // ---- Type scale and shapes, in pixels at 96 DPI ----
    // Body, labels, headings and buttons are Inter; titles and the brand wordmark Chakra Petch.
    public const float TitleSize = 17, HeadingSize = 13, BodySize = 13, HintSize = 12, LabelSize = 11, ButtonSize = 12;
    /** The old chamfer cuts are 0: the chamfer helpers draw rounded rects with the control rounding instead. */
    public const float Cut = 0, CutSmall = 0, CutTiny = 0;
    /** Control heights: standard and compact. */
    public const float ControlHeight = 26, ControlSmall = 22;
    /** Rounding: frames, buttons and grabs; popups, menus and tooltips; tabs (top corners); scrollbars. */
    public const float Rounding = 3, RoundingPopup = 4, RoundingTab = 3, RoundingScrollbar = 6;
    /** The compact panel header height, unscaled. */
    public const float PanelHeaderSize = 22;

    // ---- Spacing scale: 2, 4, 6, 8, 12, 16, 24 px times the DPI scale ----
    public static float Space1 => 2 * Scale;
    public static float Space2 => 4 * Scale;
    public static float Space3 => 6 * Scale;
    public static float Space4 => 8 * Scale;
    public static float Space5 => 12 * Scale;
    public static float Space6 => 16 * Scale;
    public static float Space7 => 24 * Scale;
}
