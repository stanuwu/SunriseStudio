namespace SunriseStudio.Editor;

public sealed partial class StudioWorkspace
{
    /** The investment link to a running game, shared by the Live sections. */
    public LiveInvestmentSession LiveInvestment { get; } = new();
}
