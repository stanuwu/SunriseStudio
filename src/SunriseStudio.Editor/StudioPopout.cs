using ImGuiNET;
using SunriseStudio.Rendering;

namespace SunriseStudio.Editor;

/** One editor in its own window. The main window's frame draws it; closing the window puts the editor back. */
internal sealed class StudioPopout : Form
{
    private WindowTarget? target;
    private ImGuiController? gui;

    public StudioPanelKind Panel { get; }
    public WindowTarget Target => target ?? throw new InvalidOperationException("The window is not attached.");
    public ImGuiController Gui => gui ?? throw new InvalidOperationException("The window is not attached.");
    public bool IsAttached => target is not null && gui is not null;
    /** Set when the user closes the window; the main frame then docks the editor again. */
    public bool PopInRequested { get; private set; }

    /** Marks that the user wants the editor docked back into the main window. */
    public void RequestPopIn() => PopInRequested = true;

    /** The themed frame; its close button docks the editor again. */
    public WindowChrome Chrome { get; }

    /** The interface scale this window was last laid out at. */
    private float uiScale = 1;

    public StudioPopout(StudioPanelKind panel, string title, Icon icon, Rectangle? bounds, float scale)
    {
        Panel = panel;
        Chrome = new(this);
        Text = $"{title} - Solforge";
        Icon = icon;
        uiScale = scale > 0 && float.IsFinite(scale) ? scale : 1;
        MinimumSize = new Size(Scaled(320), Scaled(240));
        KeyPreview = true;
        ShowInTaskbar = false;
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        if (bounds is { } screen && Screen.AllScreens.Any(display => display.WorkingArea.IntersectsWith(screen)))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = screen;
        }
        else
        {
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Scaled(900), Scaled(640));
        }
    }

    private int Scaled(float value) => Math.Max(1, (int)Math.Round(value * uiScale));

    /** Call after Show, on the window thread; the renderer and the shared controller must outlive the window. */
    public void Attach(Dx11Renderer renderer, ImGuiController shared, float scale)
    {
        // The caller may be inside the main window's frame, so its context stays current.
        var caller = ImGui.GetCurrentContext();
        try
        {
            target = renderer.CreateWindowTarget(Handle, ClientSize.Width, ClientSize.Height);
            gui = new ImGuiController(shared, this);
            StudioTheme.Apply(scale);
        }
        finally { ImGui.SetCurrentContext(caller); }
    }

    /** Reapplies the theme and the window's scaled minimum/size at a new DPI or interface scale. */
    public void Rescale(float scale)
    {
        if (gui is null || scale <= 0 || !float.IsFinite(scale)) return;
        float factor = scale / uiScale;
        uiScale = scale;
        MinimumSize = new Size(Scaled(320), Scaled(240));
        if (Math.Abs(factor - 1) > .001f && WindowState == FormWindowState.Normal)
        {
            // Keep the window centred on the same point while its content grows or shrinks with the interface.
            var bounds = Bounds;
            int width = Math.Max(MinimumSize.Width, (int)Math.Round(bounds.Width * factor));
            int height = Math.Max(MinimumSize.Height, (int)Math.Round(bounds.Height * factor));
            Bounds = new Rectangle(bounds.Left + (bounds.Width - width) / 2, bounds.Top + (bounds.Height - height) / 2,
                width, height);
        }
        var caller = ImGui.GetCurrentContext();
        try { gui.MakeCurrent(); StudioTheme.Apply(scale); }
        finally { ImGui.SetCurrentContext(caller); }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Chrome.Attach();
    }

    protected override void WndProc(ref Message m)
    {
        if (Chrome.WndProc(ref m)) return;
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e) { }
    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Tab or Keys.Left or Keys.Right or Keys.Up or Keys.Down
        || base.IsInputKey(keyData);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The user's close docks the editor; the main window disposes this one itself.
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; PopInRequested = true; }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            gui?.Dispose(); gui = null;
            target?.Dispose(); target = null;
        }
        base.Dispose(disposing);
    }
}
