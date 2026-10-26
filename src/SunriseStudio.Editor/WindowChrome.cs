using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/**
 * A themed frame for one window. The window keeps its sizing styles, so Windows still snaps,
 * animates and shadows it; the client area covers the whole window and the hit test returns the
 * resize edges and the caption. UE5 chrome: the caption is the empty part of the 30 px menu bar (CaptionHeight), the
 * window buttons fill that row's height at its right end, and their hover fill eases in (UiMotion).
 */
internal sealed class WindowChrome(Form form)
{
    private const int WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_NCACTIVATE = 0x0086;
    private const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
        HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const int SM_CXSIZEFRAME = 32, SM_CYSIZEFRAME = 33, SM_CXPADDEDBORDER = 92;
    // The resize band along each edge, in 96-DPI pixels.
    private const float ResizeBand = 6;

    /** Pixels from the top that drag the window where the last frame had no item under the pointer. */
    public float CaptionHeight { get; set; }
    public float Scale { get; set; } = 1;
    // Set at the end of each frame; the hit test between frames reads it.
    private bool overItem;
    // The pointer was over a window button this frame; the button pressed and not yet released, or -1.
    private bool buttonsHovered;
    private int pressedButton = -1;
    private readonly (Vector2 Min, Vector2 Max)[] buttonRects = new (Vector2, Vector2)[3];

    /** Minimize, maximize and close as last drawn, window-relative: each the ImGui item's own rectangle. */
    public IReadOnlyList<(Vector2 Min, Vector2 Max)> ButtonRects => buttonRects;

    /** Call after the window's frame, with its ImGui context current. */
    public void EndFrame()
    {
        overItem = buttonsHovered || ImGui.IsAnyItemHovered() || ImGui.IsAnyItemActive() || ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId);
        buttonsHovered = false;
        if (!Maximized && !Minimized)
            ImGui.GetForegroundDrawList().AddRect(Vector2.Zero, new(form.ClientSize.Width, form.ClientSize.Height),
                ImGui.GetColorU32(ImGuiCol.Border));
    }

    /** Extends the frame by a pixel so DWM keeps the shadow. Call once the handle exists. */
    public void Attach()
    {
        var margins = new Margins { Bottom = 1 };
        DwmExtendFrameIntoClientArea(form.Handle, ref margins);
    }

    /**
     * Hides the window from the screen while it still renders, for headless runs. Window opacity cannot do
     * this: the compositor ignores it for a Direct3D swap chain.
     */
    public void Cloak()
    {
        const int DWMWA_CLOAK = 13;
        int on = 1;
        DwmSetWindowAttribute(form.Handle, DWMWA_CLOAK, ref on, sizeof(int));
    }

    /** @return True when the message is answered; for WM_NCACTIVATE the caller still runs the base. */
    public bool WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_NCCALCSIZE:
                // Both forms carry the proposed window rectangle first. A maximized window extends past
                // the screen by its frame; keep the client on screen. WindowState is not yet current here.
                if (Maximized)
                {
                    var rect = Marshal.PtrToStructure<Rect>(m.LParam);
                    uint dpi = GetDpiForWindow(form.Handle);
                    int padding = GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
                    int x = GetSystemMetricsForDpi(SM_CXSIZEFRAME, dpi) + padding, y = GetSystemMetricsForDpi(SM_CYSIZEFRAME, dpi) + padding;
                    rect.Left += x; rect.Top += y; rect.Right -= x; rect.Bottom -= y;
                    Marshal.StructureToPtr(rect, m.LParam, false);
                }
                m.Result = 0;
                return true;
            case WM_NCHITTEST:
                long packed = m.LParam.ToInt64();
                var point = form.PointToClient(new Point(unchecked((short)packed), unchecked((short)(packed >> 16))));
                m.Result = HitTest(point);
                return true;
            case WM_NCACTIVATE:
                // -1 stops Windows from painting the hidden standard frame on activation.
                m.LParam = -1;
                return false;
        }
        return false;
    }

    private nint HitTest(Point point)
    {
        if (!Maximized)
        {
            int band = (int)MathF.Ceiling(ResizeBand * Scale);
            bool left = point.X < band, right = point.X >= form.ClientSize.Width - band;
            bool top = point.Y < band, bottom = point.Y >= form.ClientSize.Height - band;
            if (top && left) return HTTOPLEFT;
            if (top && right) return HTTOPRIGHT;
            if (bottom && left) return HTBOTTOMLEFT;
            if (bottom && right) return HTBOTTOMRIGHT;
            if (left) return HTLEFT;
            if (right) return HTRIGHT;
            if (top) return HTTOP;
            if (bottom) return HTBOTTOM;
        }
        return point.Y >= 0 && point.Y < CaptionHeight && !overItem ? HTCAPTION : HTCLIENT;
    }

    /** Width of the minimize, maximize and close buttons together. */
    public float ButtonsWidth => 3 * 46 * Scale;

    /**
     * Minimize, maximize or restore, and close, at the right end of the current window's row.
     * @param close What the close button does; the window's own close when null.
     */
    public void DrawButtons(float height, Action? close = null)
    {
        float width = ButtonsWidth / 3;
        var draw = ImGui.GetWindowDrawList();
        var origin = ImGui.GetWindowPos();
        float windowWidth = ImGui.GetWindowWidth();
        uint glyph = ImGui.GetColorU32(ImGuiCol.Text);
        float stroke = MathF.Max(1, Scale);
        bool windowHovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        for (int button = 0; button < 3; button++)
        {
            // Each button is a fixed rectangle from the bar's top to its bottom at the right end. Its glyph, hover and
            // click come from that rectangle, not from the layout cursor: on a menu bar row the cursor follows the row's
            // line, which put maximize and close 11 px below minimize, half under the bar's lower edge.
            Vector2 min = origin + new Vector2(windowWidth - (3 - button) * width, 0), max = min + new Vector2(width, height);
            Vector2 center = (min + max) / 2;
            ImGui.SetCursorScreenPos(min);
            // The item keeps the hit test from treating the button as caption.
            ImGui.InvisibleButton($"##window-button-{button}", max - min);
            buttonRects[button] = (ImGui.GetItemRectMin() - origin, ImGui.GetItemRectMax() - origin);
            bool hovered = windowHovered && ImGui.IsMouseHoveringRect(min, max);
            buttonsHovered |= ImGui.IsMouseHoveringRect(min, max, false);
            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) pressedButton = button;
            if (pressedButton == button && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                pressedButton = -1;
                if (hovered)
                    form.BeginInvoke(button switch
                    {
                        0 => () => form.WindowState = FormWindowState.Minimized,
                        1 => () => form.WindowState = Maximized ? FormWindowState.Normal : FormWindowState.Maximized,
                        _ => close ?? form.Close
                    });
            }
            // The hover fill fades in and out; close turns the Windows red, the others the Hover token.
            float fade = UiMotion.Ease(ImGui.GetItemID(), hovered ? 1 : 0, 30f);
            if (fade > .01f)
                draw.AddRectFilled(min, max, ImGui.GetColorU32(SolTheme.Alpha(button == 2 ? new Vector4(0.77f, 0.17f, 0.11f, 1) : SolTheme.Hover, fade)));
            if (button == 2 && fade > .5f) glyph = ImGui.GetColorU32(SolTheme.TextStrong);
            else glyph = ImGui.GetColorU32(ImGuiCol.Text);
            float half = 5 * Scale;
            switch (button)
            {
                case 0:
                    draw.AddLine(center - new Vector2(half, 0), center + new Vector2(half, 0), glyph, stroke);
                    break;
                case 1 when Maximized:
                    draw.AddRect(center - new Vector2(half - 2 * Scale, half), center + new Vector2(half, half - 2 * Scale), glyph, 0, 0, stroke);
                    draw.AddRect(center - new Vector2(half, half - 2 * Scale), center + new Vector2(half - 2 * Scale, half), glyph, 0, 0, stroke);
                    break;
                case 1:
                    draw.AddRect(center - new Vector2(half), center + new Vector2(half), glyph, 0, 0, stroke);
                    break;
                default:
                    draw.AddLine(center - new Vector2(half), center + new Vector2(half), glyph, stroke);
                    draw.AddLine(center + new Vector2(-half, half), center + new Vector2(half, -half), glyph, stroke);
                    break;
            }
        }
    }

    // Windows' own state; WinForms updates WindowState only after the size messages.
    private bool Maximized => form.IsHandleCreated && IsZoomed(form.Handle);
    private bool Minimized => form.IsHandleCreated && IsIconic(form.Handle);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
