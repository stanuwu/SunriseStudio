using System.Numerics;

namespace SunriseStudio.Viewport;

/**
 * The Solforge token table, from the HTML mockup (solforge ui mockup html/styles.css :root): near-black surfaces, white
 * hairlines at 7/10/24 % alpha, the amber accent #ffb648 and chamfered corners of 14/8/5 px. Every chrome colour in the
 * editor comes from here. The older names (Sun, Line2, Step, ...) stay as aliases so every call site compiles.
 */
public static partial class SolTheme
{
    private static Vector4 Hex(uint rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1);
    private static Vector4 White(float alpha) => new(1, 1, 1, alpha);
    /** hsl(0, 0%, percent): a neutral grey. */
    private static Vector4 Grey(float percent) => new(percent / 100f, percent / 100f, percent / 100f, 1);

    // ---- Surfaces (--bg, --panel, --panel-2, --inset) ----
    /** Window background, the gaps between panels, viewport letterbox: hsl(0,0%,5%). */
    public static readonly Vector4 Bg = Grey(5);
    /** Alias of Bg. */
    public static readonly Vector4 Canvas = Bg;
    /** Menu bar, toolbar and status bar: the panel fill, as the mockup's top bar. */
    public static readonly Vector4 Bg2 = Grey(9);
    /** Panel bodies, popups and dialogs: hsl(0,0%,9%). */
    public static readonly Vector4 Panel = Grey(9);
    /** Hover rows, the open menu title, node headers: hsl(0,0%,13%) (--panel-2). */
    public static readonly Vector4 Panel2 = Grey(13);
    /** Both stops equal Panel, so PanelGradient draws a flat fill. */
    public static readonly Vector4 PanelTop = Panel;
    public static readonly Vector4 PanelBottom = Panel;
    /** The menu and status bars. */
    public static readonly Vector4 Bar = Bg2;
    /** Panel headers and table headers: the panel fill (the mockup separates them with a hairline, not a tone). */
    public static readonly Vector4 Header = Panel;
    /** Buttons at rest: no fill, as the mockup's outlined buttons (the 1 px LineStrong frame carries them). */
    public static readonly Vector4 Surface = new(1, 1, 1, 0);
    /** Hover fill (alias Hover): --panel-2. */
    public static readonly Vector4 Surface2 = Panel2;
    public static readonly Vector4 Hover = Surface2;
    /** Active (pressed) fill: the accent wash. */
    public static readonly Vector4 Pressed = new(1, 182 / 255f, 72 / 255f, .24f);
    /** Text inputs, combos, search boxes, logs: hsl(0,0%,6.5%) (--inset). */
    public static readonly Vector4 Input = Grey(6.5f);
    public static readonly Vector4 Inset = Input;
    /** A hovered input: one step lighter than Input. */
    public static readonly Vector4 InputHover = Grey(8);
    /** Popup, menu and dialog background (--panel). */
    public static readonly Vector4 Popup = Panel;
    /** Toasts: hsl(0,0%,12%). */
    public static readonly Vector4 ToastFill = Grey(12);
    /** Panel frames and separators: white at 10 % (--line; alias Line). */
    public static readonly Vector4 Border = White(.10f);
    public static readonly Vector4 Line = Border;
    /** Soft separators inside panels: white at 7 % (--line-soft). */
    public static readonly Vector4 LineSoft = White(.07f);
    /** Control frames, menu and dialog frames: white at 24 % (--line-strong; aliases BorderHi, Line2). */
    public static readonly Vector4 LineStrong = White(.24f);
    public static readonly Vector4 BorderHi = LineStrong;
    public static readonly Vector4 Line2 = BorderHi;
    /** Light table row borders (--line-soft). */
    public static readonly Vector4 TableLine = LineSoft;
    /** Scrollbar grab at rest, hovered and dragged. */
    public static readonly Vector4 ScrollGrab = White(.16f);
    public static readonly Vector4 ScrollGrabHover = White(.26f);
    public static readonly Vector4 ScrollGrabActive = new(1, 182 / 255f, 72 / 255f, .7f);
    /** Formerly the Sol background steps; now the flat Header and Surface fills. */
    public static readonly Vector4 Step = Header;
    public static readonly Vector4 ButtonFill = Surface;
    /** The window close button's hover: a muted red, never the accent. */
    public static readonly Vector4 CloseHover = Hex(0xc42b1c);

    // ---- Text (--text, --text-dim) ----
    /** Body text: hsl(0,0%,93%). */
    public static readonly Vector4 Text = Grey(93);
    /** Titles, selected rows: white. */
    public static readonly Vector4 TextStrong = Hex(0xffffff);
    /** Secondary labels, shortcuts, panel titles: hsl(0,0%,66%) (--text-dim; alias TextMuted). About 7:1 on Panel. */
    public static readonly Vector4 Muted = Grey(66);
    public static readonly Vector4 TextMuted = Muted;
    /** Faint secondary text and disabled glyphs (alias TextDim): hsl(0,0%,55%), still about 5:1 on Panel. */
    public static readonly Vector4 Dim = Grey(55);
    public static readonly Vector4 TextDim = Dim;
    /** Grid lines and placeholder glyphs. */
    public static readonly Vector4 Faint = Grey(23);

    // ---- Accent (--accent, --accent-hi, --accent-ink, --accent-soft) ----
    /** Selection, focus, the active tab underline, primary buttons: #ffb648. Sun is the old name. */
    public static readonly Vector4 Accent = Hex(0xffb648);
    public static readonly Vector4 Sun = Accent;
    /** Primary hover and highlighted menu text: #ffcb7a. SunHi is the old name. */
    public static readonly Vector4 AccentHover = Hex(0xffcb7a);
    public static readonly Vector4 AccentHi = AccentHover;
    public static readonly Vector4 SunHi = AccentHover;
    /** Primary pressed: a deeper amber. Sun2 is the old name. */
    public static readonly Vector4 AccentActive = Hex(0xe89a2c);
    public static readonly Vector4 Sun2 = AccentActive;
    /** The accent ramp ends for ChamferGradient. */
    public static readonly Vector4 SunGradStart = AccentHover;
    public static readonly Vector4 SunGradEnd = AccentActive;
    /** Text on an Accent fill: #141006 (about 11:1 on Accent). */
    public static readonly Vector4 AccentInk = Hex(0x141006);
    /** The amber wash of hovered menu items, pressed toggles and selected rows: rgba(255,182,72,.14). */
    public static readonly Vector4 AccentSoft = new(1, 182 / 255f, 72 / 255f, .14f);
    /** Selected rows and text selection; a hovered selected row. */
    public static readonly Vector4 Selection = new(1, 182 / 255f, 72 / 255f, .22f);
    public static readonly Vector4 SelectionHover = AccentSoft;

    // ---- Brand ----
    /** The brand mark, the Solforge AI glyph and the loading card: the accent itself. */
    public static readonly Vector4 Brand = Accent;
    public static readonly Vector4 BrandHi = AccentHover;
    /** The deep end of the brand ramp. */
    public static readonly Vector4 Brand2 = Hex(0xff7a2f);
    /** The light ends of the brand shimmer (#ffe0a0). SunShimmerEdge is the old name. */
    public static readonly Vector4 BrandEdge = Hex(0xffe0a0);
    public static readonly Vector4 SunShimmerEdge = BrandEdge;

    // ---- State (--ok, --warn, --bad, --info) ----
    /** Running, success: #5fd38a. */
    public static readonly Vector4 Ok = Hex(0x5fd38a);
    /** Launching, pending approvals: the accent. */
    public static readonly Vector4 Warn = Hex(0xffb648);
    /** Errors, failed: #f07a7a. */
    public static readonly Vector4 Bad = Hex(0xf07a7a);
    /** Hashes, data pins, information: #86b4e6. */
    public static readonly Vector4 Info = Hex(0x86b4e6);
    // ---- Node graphs (the mockup's .graph-wrap and .node) ----
    /** Graph canvas: hsl(0,0%,6.5%). */
    public static readonly Vector4 GraphCanvas = Grey(6.5f);
    /** Node body and header: hsl(0,0%,12%) and hsl(0,0%,17%). */
    public static readonly Vector4 NodeBody = Grey(12), NodeHead = Grey(17);
    /** Node outline at rest: white at 22 %. */
    public static readonly Vector4 NodeLine = White(.22f);
    /** Exec wires: #9a9a9a; data wires use Info. */
    public static readonly Vector4 Wire = Hex(0x9a9a9a);

    /** Axis colours of the X/Y/Z fields. */
    public static readonly Vector4 AxisX = Hex(0xe0675a), AxisY = Hex(0x6cbd66), AxisZ = Hex(0x5f8fe0);

    // ---- Type scale and shapes, in pixels at 96 DPI ----
    // Body text is the system UI face (Segoe UI) at 13 px; labels, tabs, menus and buttons are Chakra Petch 600,
    // upper-case and letter-spaced (.08-.1 em); titles Chakra Petch; code Consolas.
    public const float TitleSize = 17, HeadingSize = 13, BodySize = 13, HintSize = 12, LabelSize = 12, ButtonSize = 12, MenuSize = 12.5f;
    /** Letter spacing of upper-case labels (menus, buttons: .08 em; tabs and panel titles: .1 em). */
    public const float TrackButton = .08f, TrackLabel = .1f;
    /** Chamfer cuts (--cut, --cut-sm, --cut-xs): dialogs, panels and toasts, controls. */
    public const float Cut = 14, CutSmall = 8, CutTiny = 5;
    /** Control heights: standard and compact. */
    public const float ControlHeight = 26, ControlSmall = 22;
    /** ImGui's own widgets cannot be chamfered, so they are square: no rounding anywhere. */
    public const float Rounding = 0, RoundingPopup = 0, RoundingTab = 0, RoundingScrollbar = 0;
    /** The panel header height, unscaled (the mockup's 30 px panel head). */
    public const float PanelHeaderSize = 30;

    // ---- Spacing scale: 2, 4, 6, 8, 12, 16, 24 px times the DPI scale ----
    public static float Space1 => 2 * Scale;
    public static float Space2 => 4 * Scale;
    public static float Space3 => 6 * Scale;
    public static float Space4 => 8 * Scale;
    public static float Space5 => 12 * Scale;
    public static float Space6 => 16 * Scale;
    public static float Space7 => 24 * Scale;
}
