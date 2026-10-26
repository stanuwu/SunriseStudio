using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/**
 * The 3D viewport render switch (toolbar, View menu, Ctrl+Shift+R, Settings > Rendering). Off stops every 3D draw in the
 * scene and asset viewports and shrinks their frame targets; panels, the Live link and the MCP tools keep running. It sits
 * above the Destiny 2 auto-suspend: off wins, and on still lets the auto-suspend pause rendering while the game runs.
 */
public sealed partial class StudioForm
{
    private long renderToggleFrame = -1, settingsShortcutFrame = -1;

    /** Turns viewport rendering on or off and persists the choice. */
    public void SetViewportRendering(bool on)
    {
        if (settings.ViewportRendering == on) return;
        settings.ViewportRendering = on;
        if (viewport is not null) viewport.RenderingOff = !on;
        if (assetViewport is not null) assetViewport.RenderingOff = !on;
        SaveSettings();
    }

    /**
     * Seconds between frames: the game-sharing caps first, then the user's Settings caps; 0 draws every vsync. While UI
     * motion runs (or within MotionTail of the last change or input) the rendering-off cap lifts to vsync so easing
     * stays smooth; the Live and Destiny 2 caps and the user's caps still win.
     */
    private double FrameInterval(bool suspended, bool renderingOff)
    {
        bool focused = ContainsFocus;
        bool motion = MotionActive;
        int cap = livePage.IsAttached ? (focused ? 30 : 10)
            : suspended ? (focused ? 15 : 5)
            : renderingOff && !motion ? (focused ? 30 : 10) : 0;
        int user = focused ? settings.FpsFocused : settings.FpsBackground;
        if (user > 0) cap = cap == 0 ? user : Math.Min(cap, user);
        return cap > 0 ? 1.0 / cap : 0;
    }

    /** Popout windows run the same handler; one press toggles once. */
    private void HandleRenderingShortcuts()
    {
        if (ShortcutInput.Pressed(ShortcutActions.ToggleViewportRendering) && renderToggleFrame != ShortcutInput.Frame)
        {
            renderToggleFrame = ShortcutInput.Frame;
            SetViewportRendering(!settings.ViewportRendering);
        }
        if (ShortcutInput.Pressed(ShortcutActions.OpenSettings) && settingsShortcutFrame != ShortcutInput.Frame)
        {
            settingsShortcutFrame = ShortcutInput.Frame;
            preferencesOpen = true;
        }
    }

    private void DrawRenderingMenuItem()
    {
        bool on = settings.ViewportRendering;
        if (UiAuto.MenuItem("3D viewport rendering", ShortcutInput.Text(ShortcutActions.ToggleViewportRendering), ref on)) SetViewportRendering(on);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Off stops all 3D drawing to save GPU and memory; panels and the AI tools keep working.");
    }

    private static readonly string[] QualityNames = ["Low", "Medium", "High", "Full"];
    private static readonly string[] QualityTips =
    [
        "Quality: Low\nFlat grey geometry, coarsest levels and a short far cull. No game shading, decals or effects.",
        "Quality: Medium\nColour-textured geometry at coarse levels. No game shading, decals or effects.",
        "Quality: High\nGame shaders and lighting at the game's distance levels. No decals or particle effects.",
        "Quality: Full\nEverything: most detailed level at every distance, game shading, decals and particle effects."
    ];

    /** Map viewport quality (toolbar, set_viewport_rendering); saved at once. */
    public void SetViewportQuality(ViewportQuality quality)
    {
        if (!Enum.IsDefined(quality)) return;
        settings.ViewportQuality = quality;
        if (viewport is not null) viewport.Quality = quality;
        SaveSettings();
    }

    /** Game shading draws only at High or Full, so turning it on raises a lower quality. */
    private void SetGameShaders(bool on)
    {
        settings.GameShaders = on;
        PrepareGameShaders();
        if (on && settings.ViewportQuality < ViewportQuality.High) SetViewportQuality(ViewportQuality.High);
        else SaveSettings();
    }

    private void DrawQualityToolbarCombo()
    {
        int quality = (int)settings.ViewportQuality;
        ImGui.SetNextItemWidth(ImGui.CalcTextSize("Quality: Medium").X + ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X * 2);
        // UE5 toolbar combos read as buttons: the raised Surface fill with an eased hover instead of the recessed Input well.
        uint key = ImGui.GetID("##quality");
        var fill = UiMotion.HoverColor(key, SolTheme.Surface, SolTheme.Hover, SolTheme.Pressed);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, fill);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, fill);
        ImGui.PushStyleColor(ImGuiCol.Button, fill);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, fill);
        // The colours stay pushed until EndCombo, so the popup window's begin and end see the same style stack.
        if (UiAuto.BeginCombo("##quality", "Quality: " + QualityNames[quality]))
        {
            SolTheme.PopupShadow();
            for (int i = 0; i < QualityNames.Length; i++)
                if (UiAuto.Selectable(QualityNames[i], i == quality) && i != quality) SetViewportQuality((ViewportQuality)i);
            UiAuto.EndCombo();
        }
        ImGui.PopStyleColor(4);
        UiMotion.TrackHover(key);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(QualityTips[quality]);
    }

    /** Why the map is not drawn with game shading and lighting, or null when it is. */
    private string? GameShaderProblem()
    {
        if (!settings.GameShaders) return "Game shaders are off, so the map draws as plain geometry and Lighting has no effect.";
        if (settings.ViewportQuality < ViewportQuality.High)
            return $"Quality {settings.ViewportQuality} draws plain geometry; pick High or Full for game shading and lighting.";
        if (viewport is not { } view) return null;
        if (view.Wireframe) return "Wireframe is on in the scene toolbar; it replaces game shading.";
        if (!view.UseGameShaders)
            return "Game shading failed for this scene" + (view.NativeError is { Length: > 0 } error ? ": " + error : "")
                + "\nShowing textured fallback geometry. Toggle Game shaders to retry.";
        return null;
    }

    /** Toolbar: an eye that is lit while the 3D viewport renders. */
    private void DrawRenderingToolbarButton()
    {
        bool on = settings.ViewportRendering;
        string keys = ShortcutInput.Text(ShortcutActions.ToggleViewportRendering);
        string tip = (on ? "3D viewport rendering is on. Click to turn it off and save GPU and memory."
            : "3D viewport rendering is off. Click to turn it on.") + (keys.Length > 0 ? $" ({keys})" : "");
        if (destiny2Detected && settings.PauseRenderWhenGameRunning && !livePage.IsAttached && on)
            tip += "\nDestiny 2 is running, so rendering is paused anyway until the game closes.";
        if (ImGuiIcons.Button("tb-render", on ? StudioIcon.Eye : StudioIcon.EyeOff, tip, on)) SetViewportRendering(!on);
    }
}
