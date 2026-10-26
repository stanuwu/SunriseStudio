/**
 * File: UI motion settings (docs/UI-UE5.md §3.3): Reduce motion and the animation speed, shown in Settings > Interface
 * by StudioForm.DrawMotionSettings. Needs StudioSettings to be partial (hook H1).
 */
using System.Text.Json.Serialization;

namespace SunriseStudio.Editor;

public sealed partial class StudioSettings
{
    /** The saved Reduce motion choice; null until the first read, which takes the Windows "Show animations" setting. */
    [JsonPropertyName("ReduceMotion")]
    public bool? ReduceMotionSaved { get; set; }

    /**
     * Reduce motion: every UI animation snaps to its target, pulses hold still and the drawer appears at once. On first
     * run it follows Windows (Settings > Accessibility > Visual effects > Animation effects) and is then saved.
     */
    [JsonIgnore]
    public bool ReduceMotion
    {
        get => ReduceMotionSaved ??= LoadingBackdrop.ReducedMotion;
        set => ReduceMotionSaved = value;
    }

    private float uiAnimationSpeed = 1;

    /** UI animation speed multiplier, 0.5 (slower) to 2 (faster); 1 is the designed timing. */
    public float UiAnimationSpeed
    {
        get => uiAnimationSpeed;
        set => uiAnimationSpeed = float.IsFinite(value) ? Math.Clamp(value, .5f, 2f) : 1;
    }
}
