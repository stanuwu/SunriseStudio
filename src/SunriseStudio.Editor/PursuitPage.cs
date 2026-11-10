using ImGuiNET;
using SunriseStudio.Formats;

namespace SunriseStudio.Editor;

/**
 * A single-step pursuit in the Pursuits bucket, with its objectives, sources, completion and data as sections. The page is
 * titled Pursuit because not every such item is a bounty, and the item's own type line says what it is.
 */
public sealed class PursuitPage : IStudioPage
{
    public const string PageId = "sunrise.pursuit";
    private const string Dot = InvestmentTableViews.Dot;
    /** Icon side of the page header at UI scale 1, as on the Quest page. */
    private const float HeaderIconSide = 48;
    private readonly InvestmentTablesLoader loader = new();
    private InvestmentIconCache icons = InvestmentIconCache.Unprepared;
    private readonly PursuitInspector inspector = new();
    public string Id => PageId;
    public string Title => "Pursuit";

    public bool CanOpen(StudioDocument document) =>
        PursuitDocuments.Of(document) == (InvestmentPursuitShape.Single, InvestmentPursuits.PursuitBucket);

    public void Draw(StudioPageContext context)
    {
        var workspace = context.Workspace;
        icons = InvestmentIconCache.Of(context);
        if (!loader.Ready(workspace, out var catalog, out var tables)) return;
        workspace.RequestInvestmentDetail();
        if (!workspace.InvestmentDetailCurrent)
        {
            if (workspace.IsBusy) { ImGui.TextDisabled("Reading item"); return; }
            InvestmentTablesLoader.Failed(workspace, workspace.RetryInvestmentDetail);
            return;
        }
        if (workspace.SelectedInvestmentItem is not { } detail) { ImGui.TextDisabled("Not an item"); return; }
        var item = detail.Item;
        float side = HeaderIconSide * InvestmentTableViews.PageScale;
        icons.Draw(catalog, item.Index, side);
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.TextColored(StudioTheme.Rarity(item.Tier),
            StudioUi.Fit(item.Name, ImGui.GetContentRegionAvail().X - StudioLiveLink.Room()));
        StudioUi.Tip($"{item.Name}\nItem {item.Index}{Dot}{item.Hash:X8}");
        StudioLiveLink.Draw(context, StudioLiveLink.PursuitTarget(item));
        ImGui.TextDisabled(StudioUi.Fit(item.TypeName, ImGui.GetContentRegionAvail().X));
        ImGui.EndGroup();
        if (item.Description.Length != 0) ImGui.TextWrapped(item.Description);
        inspector.Draw(context, catalog, tables, detail, tabbed: false);
    }
}
