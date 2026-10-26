using System.Numerics;
using ImGuiNET;

namespace SunriseStudio.Viewport;

/**
 * Solforge Phase 10: axis views (Top/Front/Side orthographic), the corner world-axis gizmo and the
 * FPS / draw call / triangle overlay. Orbit, fly and the perspective/orthographic toggle are upstream.
 */
public sealed partial class AssetViewport
{
    private static readonly ImGuiKey[] ViewKeys = [ImGuiKey.Keypad1, ImGuiKey.Keypad3, ImGuiKey.Keypad7];
    private static readonly string[] AxisLabels = ["X", "Y", "Z"];
    private readonly ViewportStatistics statistics = new();
    private int statisticsDrawCalls;
    private long statisticsTriangles;

    /** Shows FPS, draw calls and triangles in the 3D view's lower left corner. */
    public bool ShowStatistics { get; set; }
    /** Shows the world axes in the 3D view's upper right corner. */
    public bool ShowAxisGizmo { get; set; } = true;
    public ViewportStatistics Statistics => statistics;

    /** Looks along one world axis; axis views are orthographic unless asked otherwise. */
    public void SetViewPreset(CameraViewPreset preset, bool orthographic = true)
    {
        if (Camera.HasExternalPose) return;
        SetPlayerCamera(false);
        var (yaw, pitch) = CameraViews.Angles(preset);
        Camera.SetView(yaw, pitch);
        Camera.Orthographic = orthographic;
    }

    /** Numpad 7, 1 and 3 pick Top, Front and Right (Ctrl the opposite side); Numpad 5 toggles orthographic. */
    private void HandleViewKeys()
    {
        if (Camera.HasExternalPose || PlayerCamera || Flying || ImGui.GetIO().WantTextInput) return;
        bool opposite = ImGui.GetIO().KeyCtrl;
        foreach (var key in ViewKeys)
        {
            if (!ImGui.IsKeyPressed(key, false)) continue;
            if (CameraViews.FromKeypad(key - ImGuiKey.Keypad0, opposite) is { } preset) SetViewPreset(preset);
            return;
        }
        if (ImGui.IsKeyPressed(ImGuiKey.Keypad5, false)) Camera.Orthographic = !Camera.Orthographic;
    }

    /** Toolbar menu with the axis views and the overlay toggles. */
    private void DrawViewMenu(Action<float> next)
    {
        const string label = "View", popup = "##view-presets-menu";
        next(ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2);
        ImGui.BeginDisabled(Camera.HasExternalPose);
        if (UiAuto.Button(label + "##view-presets")) ImGui.OpenPopup(popup);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Axis views: Numpad 7 top, 1 front, 3 right, Ctrl for the opposite side; Numpad 5 orthographic");
        if (!ImGui.BeginPopup(popup)) return;
        if (UiAuto.MenuItem("Perspective", "", !Camera.Orthographic)) Camera.Orthographic = false;
        if (UiAuto.MenuItem("Orthographic", "Numpad 5", Camera.Orthographic)) Camera.Orthographic = true;
        ImGui.Separator();
        foreach (var preset in CameraViews.All)
            if (UiAuto.MenuItem(CameraViews.Name(preset), ViewKeyText(preset))) SetViewPreset(preset);
        ImGui.Separator();
        if (UiAuto.MenuItem("Axis gizmo", "", ShowAxisGizmo)) ShowAxisGizmo = !ShowAxisGizmo;
        if (UiAuto.MenuItem("Statistics", "", ShowStatistics)) ShowStatistics = !ShowStatistics;
        ImGui.EndPopup();
    }

    private static string ViewKeyText(CameraViewPreset preset) => preset switch
    {
        CameraViewPreset.Top => "Numpad 7", CameraViewPreset.Bottom => "Ctrl+Numpad 7",
        CameraViewPreset.Front => "Numpad 1", CameraViewPreset.Back => "Ctrl+Numpad 1",
        CameraViewPreset.Right => "Numpad 3", CameraViewPreset.Left => "Ctrl+Numpad 3",
        _ => ""
    };

    /** Call right before the 3D image renders; EndViewportStatistics takes the counts after it. */
    private void BeginViewportStatistics()
    {
        statisticsDrawCalls = renderer.DrawCalls;
        statisticsTriangles = renderer.Triangles;
    }

    private void EndViewportStatistics()
    {
        int draws = renderer.DrawCalls >= statisticsDrawCalls ? renderer.DrawCalls - statisticsDrawCalls : renderer.DrawCalls;
        long triangles = renderer.Triangles - statisticsTriangles;
        // The game-shader path counts its own draws; the geometry path counts through the renderer.
        if (UseGameShaders && NativeError is null && nativeScene is { } native)
        {
            draws = Math.Max(draws, native.LastFrame.DrawCalls);
            triangles = Math.Max(triangles, native.LastFrame.Triangles);
        }
        statistics.Record(ImGui.GetIO().DeltaTime, draws, triangles);
    }

    /** Draws the corner overlays over the 3D image; never over a texture preview. */
    private void DrawViewOverlays(ImDrawListPtr draw, Vector2 origin, Vector2 size)
    {
        float scale = SolTheme.Scale;
        if (ShowAxisGizmo && !Camera.HasExternalPose)
        {
            float radius = 28 * scale;
            var centre = origin + new Vector2(size.X - radius - 14 * scale, radius + 14 * scale);
            // A translucent Panel disc with a BorderHi ring behind the axes (SolTheme tokens).
            draw.AddCircleFilled(centre, radius + 10 * scale, SolTheme.U32(SolTheme.Alpha(SolTheme.Panel, .55f)));
            draw.AddCircle(centre, radius + 10 * scale, SolTheme.U32(SolTheme.Alpha(SolTheme.BorderHi, .8f)), 0, Math.Max(1, scale));
            foreach (var axis in AxisGizmo.Layout(Camera.View, radius))
            {
                var color = GizmoColors[axis.Axis];
                if (axis.Negative) color = color with { W = .45f };
                var tip = centre + axis.Offset;
                if (!axis.Negative) draw.AddLine(centre, tip, SolTheme.U32(color), 2 * scale);
                draw.AddCircleFilled(tip, (axis.Negative ? 4 : 7) * scale, SolTheme.U32(color));
                if (!axis.Negative)
                {
                    var text = AxisLabels[axis.Axis];
                    draw.AddText(tip - ImGui.CalcTextSize(text) / 2, SolTheme.U32(SolTheme.Bg), text);
                }
            }
        }
        if (ShowStatistics)
        {
            string text = statistics.Text() + (Camera.Orthographic ? "\nOrthographic" : "");
            var at = origin + new Vector2(10 * scale, size.Y - ImGui.CalcTextSize(text).Y - 10 * scale);
            var pad = new Vector2(6 * scale, 4 * scale);
            SolTheme.Chip(draw, at - pad, at + ImGui.CalcTextSize(text) + pad, false);
            draw.AddText(at, SolTheme.U32(SolTheme.Text), text);
        }
    }
}
