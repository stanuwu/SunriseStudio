using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Live;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** One Live section. Attach and Detach run for every section when the Live tab attaches or detaches. */
internal interface ILiveSection : IDisposable
{
    /** The section's id, such as "world" or "investment", which names it in the shown page id and in history. */
    string Id { get; }
    string Title { get; }
    /** Attaches to the chosen game. A section opens its links here or when it is first drawn. */
    void Attach(LiveProcess process);
    /** gameClosed says the game exited rather than the user detaching. */
    void Detach(bool gameClosed);
    /** Once a frame, before drawing. */
    void Poll(LiveSectionContext context);
    void Draw(LiveSectionContext context);
    /** The shown subtab id, or empty for a section without subtabs. History records it with Row. */
    string View => "";
    string? Row => null;
    /** Shows a subtab and selects row once that subtab draws. A null row keeps the current one. */
    void Show(string view, string? row) { }
}

/**
 * What a section may use of the window: the workspace, the scene view, the shared grid choice and item icon cache, plus two
 * navigations. OpenAsset(tag, page, row) opens an asset with that row chosen, as a link does, on the named page or on the
 * shown page when page is null. ShowPage(page, row) shows Assets on a page of the open asset, as an asset page's links do
 * after opening one.
 */
internal sealed record LiveSectionContext(StudioWorkspace Workspace, AssetViewport Viewport, Action? DrawScene, float Scale,
    bool Grid, Action<bool> SetGrid, Action<uint, string?, string?> OpenAsset, Action<string, string?> ShowPage,
    InvestmentIconCache Icons);

/**
 * An asset page's way into Live. Attached says whether Live has a game attached, and Open(view, row) focuses Live on that
 * Investment subtab with the row chosen.
 */
public sealed record StudioLiveLink(bool Attached, Action<string, string?> Open)
{
    private const string Label = "Open in Live";

    /** The room the button takes at the end of a header line, with the spacing before it. */
    public static float Room() => ImGui.CalcTextSize(Label).X + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetStyle().ItemSpacing.X;

    /** An item page's target: Items with the item chosen. */
    internal static (string View,
        string? Row) ItemTarget(InvestmentItem item) => (LiveInvestmentSection.ItemsView, $"item:{item.Hash:X8}");

    /** A quest or pursuit page's target: Pursuits showing the step, with its first held copy chosen. */
    internal static (string View,
        string? Row) PursuitTarget(InvestmentItem item) => (LiveInvestmentSection.PursuitsView, $"item:{item.Hash:X8}");

    /** The season pass page's target: Season pass with the chosen row, if any. */
    internal static (string View, string? Row) PassTarget(int progression, int row) =>
        (LiveInvestmentSection.SeasonPassView, row >= 0 ? SeasonPassPage.RowOf(progression, row) : null);

    /** "Open in Live" at the right end of the current line, always drawn and disabled with the reason until a game is attached. */
    internal static void Draw(StudioPageContext context, (string View, string? Row) target)
    {
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0,
            ImGui.GetContentRegionAvail().X - Room() + ImGui.GetStyle().ItemSpacing.X));
        var live = context.Live;
        if (LiveGate.Button(Label, live is { Attached: true } ? "" : LiveGate.AttachSentence,
            "Show this in Live > Investment.", link: true))
            live!.Open(target.View, target.Row);
    }
}
