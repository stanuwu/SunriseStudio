using System.Numerics;
using ImGuiNET;

namespace SunriseStudio.Viewport;

/**
 * Motion widgets built on UiMotion (docs/UI-UE5.md §3.2). Every helper is decoration only: a click acts on the frame
 * it happens and the drawn value catches up. With ReduceMotion every helper returns its target at once.
 */
public static partial class UiMotion
{
    private const int HoveredFlag = 1, ActiveFlag = 2;

    /**
     * The eased fill for a button, frame or menu row (about 90 ms). It eases toward active, hover or rest using the
     * item's state from the previous frame; call TrackHover(id) right after the item to record it. Pushing the result
     * into ImGuiCol.Button, ButtonHovered and ButtonActive before the item gives a smooth hover (see PushButtonColors).
     */
    public static Vector4 HoverColor(uint id, Vector4 rest, Vector4 hover, Vector4 active)
    {
        int flags = GetFlags(id);
        var target = (flags & ActiveFlag) != 0 ? active : (flags & HoveredFlag) != 0 ? hover : rest;
        return EaseColor(id, target, 40f);
    }

    /** Records the last item's hover and active state for HoverColor(id, ...) on the next frame. */
    public static void TrackHover(uint id)
        => SetFlags(id, (ImGui.IsItemHovered() ? HoveredFlag : 0) | (ImGui.IsItemActive() ? ActiveFlag : 0));

    /** Pushes the eased HoverColor into the three button colours; call PopButtonColors(id) right after the item. */
    public static void PushButtonColors(uint id, Vector4 rest, Vector4 hover, Vector4 active)
    {
        var color = HoverColor(id, rest, hover, active);
        ImGui.PushStyleColor(ImGuiCol.Button, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, color);
    }

    /** Pops the three colours from PushButtonColors and records the item's hover state. */
    public static void PopButtonColors(uint id)
    {
        ImGui.PopStyleColor(3);
        TrackHover(id);
    }

    /**
     * Draws the 2 px Accent line of an ImGui tab bar on the active tab's top edge (the 1.2 compact tab style, the same
     * line area tabs and pop-out tabs draw), its x and width sprung so it slides between tabs (about 160 ms). barId
     * names the bar; activeMin and activeMax are the active tab's screen rect (ImGui.GetItemRect* after BeginTabItem).
     * BeginTabItem has already popped the bar's clip, so the line is clipped here to the window's content columns, minus
     * the scroll arrows when the active tab reaches under them and minus the list button when listButton is set
     * (ImGuiTabBarFlags.TabListPopupButton).
     */
    public static void TabUnderline(uint barId, Vector2 activeMin, Vector2 activeMax, bool listButton = false)
    {
        var at = SpringVec(Mix(barId, 0x7AB1u), new(activeMin.X, activeMax.X - activeMin.X), 520f, 46f);
        float thickness = 2 * SolTheme.Scale;
        if (at.Y < 1) return;
        var style = ImGui.GetStyle();
        // The window's current clip (its inner content rect) bounds the bar on both sides.
        var draw = ImGui.GetWindowDrawList();
        float left = draw.GetClipRectMin().X, right = draw.GetClipRectMax().X;
        // imgui_widgets.cpp TabBarLayout: arrows are 2 x (FontSize - 2) wide at the right; the list button FontSize + FramePadding.y at the left.
        float arrows = 2 * (ImGui.GetFontSize() - 2), list = ImGui.GetFontSize() + style.FramePadding.Y;
        if (activeMax.X > right - arrows - .5f || activeMin.X < left - .5f) right -= arrows;
        if (listButton) left += list;
        float x0 = MathF.Max(at.X, left), x1 = MathF.Min(at.X + at.Y, right);
        if (x1 - x0 < 1) return;
        draw.AddRectFilled(new(x0, activeMin.Y), new(x1, activeMin.Y + thickness), SolTheme.U32(SolTheme.Accent));
    }

    /**
     * A 0..1 spring for a drawer (the Content Browser drawer, the bottom dock reveal), about 200 ms. Layout uses
     * t * targetRatio; when it reaches 0 the caller removes the area, and it reinserts before opening. With
     * ReduceMotion it jumps.
     */
    public static float Drawer(uint id, bool open)
    {
        float target = open ? 1 : 0;
        return Math.Clamp(SpringFrom(Mix(id, 0xD7A3u), target, target, 600f, 49f), 0, 1);
    }

    /**
     * Content alpha for a panel's first show, a tab switch or a new pop-out (push it as ImGuiStyleVar.Alpha), about
     * 120 ms. It starts at 0 when the id is new or was evicted (unseen for 2 s); restart begins the fade again.
     */
    public static float FadeIn(uint id, bool restart = false)
    {
        uint key = Mix(id, 0xFADEu);
        if (restart) Reset(key);
        return EaseFrom(key, 0, 1, 25f);
    }

    /** SmoothScroll for the current child window, keyed by its own id stack. */
    public static void SmoothScroll() => SmoothScroll(ImGui.GetID("##ui-motion-scroll"));

    /**
     * Eases the current window's vertical scroll (about 120 ms). Call right after BeginChild. With
     * ImGuiWindowFlags.NoScrollWithMouse the wheel is read here; without it ImGui's own wheel step becomes the target.
     * Scrollbar drags and programmatic scrolls (SetScrollHere) are followed at once.
     */
    public static void SmoothScroll(uint id)
    {
        float actual = ImGui.GetScrollY(), max = ImGui.GetScrollMaxY();
        ref var state = ref Raw(Mix(id, 0x5C201u), new(actual, actual, actual, ImGui.GetScrollX()), out bool created);
        if (created) return;
        float target = state.Value.X, now = state.Value.Y, written = state.Value.Z, writtenX = state.Value.W;
        var io = ImGui.GetIO();
        bool hovered = !io.KeyCtrl && ImGui.IsWindowHovered();
        float wheel = hovered ? io.MouseWheel : 0;
        // Tilt wheel and Shift+wheel scroll sideways, never vertically. ImGui does that itself unless the window passes
        // NoScrollWithMouse (which turns off both axes); then scroll X has not moved since the last frame and it is done here.
        float sideways = hovered ? io.MouseWheelH + (io.KeyShift ? io.MouseWheel : 0) : 0;
        if (io.KeyShift) wheel = 0;
        float actualX = ImGui.GetScrollX();
        if (sideways != 0 && ImGui.GetScrollMaxX() > 0 && MathF.Abs(actualX - writtenX) < .5f)
        {
            float stepX = MathF.Min(2 * ImGui.GetFontSize(), ImGui.GetWindowWidth() * .67f);
            actualX = Math.Clamp(actualX - sideways * stepX, 0, ImGui.GetScrollMaxX());
            ImGui.SetScrollX(actualX);
        }
        if (MathF.Abs(actual - written) > 1.5f)
        {
            // Something else moved the scroll: ImGui's wheel handling (ease toward it), or a drag or jump (follow it).
            if (wheel != 0) target = actual;
            else target = now = actual;
        }
        else if (wheel != 0)
        {
            float step = MathF.Min(5 * ImGui.GetFontSize(), ImGui.GetWindowHeight() * .67f);
            target -= wheel * step;
        }
        target = Math.Clamp(target, 0, Math.Max(0, max));
        now = ReduceMotion ? target : StepTo(now, target, 25f, .5f);
        if (MathF.Abs(now - actual) > .01f) ImGui.SetScrollY(now);
        state.Value = new(target, now, now, actualX);
    }

    /** Alpha and upward offset (8 px at rest) for a toast, notice or badge: in about 150 ms, out about 250 ms. */
    public static (float Alpha, float Offset) Toast(uint id, bool visible)
    {
        float alpha = EaseFrom(Mix(id, 0x70A5u), 0, visible ? 1 : 0, visible ? 22f : 13f);
        return (alpha, (1 - alpha) * 8 * SolTheme.Scale);
    }

    /** The collapsing-header chevron angle in radians: 0 closed (pointing right) to pi/2 open (pointing down), about 120 ms. */
    public static float Chevron(uint id, bool open) => Ease(Mix(id, 0xC4E7u), open ? 1 : 0, 25f) * MathF.PI / 2;

    /** Draws a chevron pointing right, rotated by angle (Chevron's value), centred at center with the given half size. */
    public static void DrawChevron(ImDrawListPtr draw, Vector2 center, float halfSize, float angle, uint color)
    {
        float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
        Vector2 P(float x, float y) => center + new Vector2(x * cos - y * sin, x * sin + y * cos) * halfSize;
        float stroke = Math.Max(1, 1.5f * SolTheme.Scale);
        draw.PathLineTo(P(-.3f, -.6f));
        draw.PathLineTo(P(.35f, 0));
        draw.PathLineTo(P(-.3f, .6f));
        draw.PathStroke(color, ImDrawFlags.None, stroke);
    }

    /** A status dot's colour, eased between states, its alpha pulsing while pulsing is set (launching, stopping). */
    public static Vector4 Dot(uint id, Vector4 color, bool pulsing = false)
    {
        var eased = EaseColor(Mix(id, 0xD07u), color, 14f);
        if (pulsing) eased.W *= .4f + .6f * Pulse(1.2f);
        return eased;
    }

    /** Draws a status dot (filled, or a ring when hollow) with the eased Dot colour. */
    public static void Dot(ImDrawListPtr draw, Vector2 center, float radius, uint id, Vector4 color, bool pulsing = false, bool hollow = false)
    {
        uint tint = SolTheme.U32(Dot(id, color, pulsing));
        if (hollow) draw.AddCircle(center, radius, tint, 16, Math.Max(1, 1.25f * SolTheme.Scale));
        else draw.AddCircleFilled(center, radius, tint, 16);
    }
}
