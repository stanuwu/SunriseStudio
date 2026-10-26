using System.Runtime.InteropServices;
using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/**
 * Steady frame cadence while the UI animates (docs/UI-UE5.md §3.3). The WinForms timer (Interval 1, about 15.6 ms
 * resolution) plus Present(1) gives an uneven 32-64 fps beat that makes easing look jerky. While UiMotion reports
 * motion (or within MotionTail of the last change or input) frames come from an Application.Idle loop instead,
 * paced by the vsync wait in Present. When nothing moves the loop stops and the existing timer drives frames, so
 * idle CPU stays as it was. The loop never runs past the game-sharing or user caps: it only runs while
 * FrameInterval is 0 (every vsync).
 */
public sealed partial class StudioForm
{
    /** Seconds after the last motion or input during which frames stay at vsync. */
    private const double MotionTail = .25;
    /** A paced frame shorter than this did not wait for vsync (an occluded window); the timer takes over. */
    private const double MinPacedFrame = .002;
    private const uint PeekNoRemove = 0;

    private bool framePacing;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint first, uint last, uint remove);

    /** True while a UI value is moving or the last change or input was under MotionTail ago. */
    private static bool MotionActive => UiMotion.Animating || UiMotion.Now - UiMotion.LastActivity < MotionTail;

    /** Applies the saved Reduce motion and speed to UiMotion; called on start and whenever Settings change them. */
    private void ApplyMotionSettings()
    {
        UiMotion.ReduceMotion = settings.ReduceMotion;
        UiMotion.Speed = settings.UiAnimationSpeed;
    }

    /** Starts the idle-loop pacing (from OnShown). Smoke runs keep the plain timer. */
    private void StartFramePacing()
    {
        ApplyMotionSettings();
        if (framePacing || options.Smoke) return;
        framePacing = true;
        Application.Idle += PaceFrames;
    }

    /** Stops the idle-loop pacing (from the close path); safe to call twice. */
    private void StopFramePacing()
    {
        if (!framePacing) return;
        framePacing = false;
        Application.Idle -= PaceFrames;
    }

    /** Whether the current caps allow a frame every vsync, with the same inputs TickFrame gives FrameInterval. */
    private bool PacedAtVsync()
    {
        bool suspended = settings.PauseRenderWhenGameRunning && destiny2Detected && !livePage.IsAttached;
        return FrameInterval(suspended, !settings.ViewportRendering) <= 0;
    }

    /** Draws frames back to back while the UI animates and no window message is waiting. */
    private void PaceFrames(object? sender, EventArgs e)
    {
        while (framePacing && !closed && !IsDisposed && WindowState != FormWindowState.Minimized
            && MotionActive && PacedAtVsync() && !PeekMessage(out _, IntPtr.Zero, 0, 0, PeekNoRemove))
        {
            double before = lastFrame, started = clock.Elapsed.TotalSeconds;
            TickFrame();
            // Nothing drawn (busy, a native dialog, a cap): leave it to the timer instead of spinning.
            if (lastFrame == before) break;
            if (clock.Elapsed.TotalSeconds - started < MinPacedFrame) break;
        }
    }

    /** Settings > Interface: Reduce motion and the animation speed (hook H3). */
    private void DrawMotionSettings()
    {
        bool reduce = settings.ReduceMotion;
        if (UiAuto.Checkbox("Reduce motion", ref reduce))
        {
            settings.ReduceMotion = reduce;
            ApplyMotionSettings();
            SaveSettings();
        }
        SettingNote("Hover fades, tab slides, the Content Browser drawer, toasts and smooth scrolling jump straight to their end. "
            + "The first run follows the Windows \"Animation effects\" setting. Applies now.");
        float speed = settings.UiAnimationSpeed;
        ImGui.BeginDisabled(reduce);
        ImGui.SetNextItemWidth(220 * scale);
        if (UiAuto.SliderFloat("Animation speed", ref speed, .5f, 2f, "%.2fx"))
        {
            settings.UiAnimationSpeed = speed;
            ApplyMotionSettings();
            SaveSettings();
        }
        ImGui.EndDisabled();
        SettingNote("1x is the designed timing; higher is faster. While anything animates the interface draws every vsync, "
            + "unless the Live link, a running Destiny 2 or your FPS caps hold it lower. Applies now.");
    }
}
