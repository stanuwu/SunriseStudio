namespace SunriseStudio.Editor;

public sealed partial class StudioWorkspace
{
    /** The investment link to a running game, shared by every page and the browser. */
    public LiveInvestmentSession LiveInvestment { get; } = new();
}
