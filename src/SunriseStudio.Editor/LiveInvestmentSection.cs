using ImGuiNET;
using SunriseStudio.Live;
using SunriseStudio.Rendering;

namespace SunriseStudio.Editor;

/** Live's Investment section. Its subtabs show what the attached game holds and the actions Studio sent it. */
internal sealed partial class LiveInvestmentSection(StudioWorkspace workspace) : ILiveSection, IFramePreparedPage
{
    internal const string ItemsView = "items", InventoryView = "inventory", PursuitsView = "pursuits",
        SeasonPassView = "season-pass",
        ActivityView = "activity";
    private const string ItemPage = InvestmentPage.PageId;
    private string view = ItemsView;
    private string? requestedView;
    // Reads the item catalog and investment tables the subtabs share, without drawing a wait line.
    private readonly InvestmentTablesLoader loader = new();
    private InvestmentIconCache icons = InvestmentIconCache.Unprepared;
    private readonly LiveItemActions actions = new();
    // The UI scale of the frame being drawn.
    private float scale = 1;

    public string Id => "investment";
    public string Title => "Investment";
    // A subtab that Show asked for opens on the next draw. Until then history records it as the view.
    public string View => requestedView ?? view;
    public string? Row => View switch
    {
        ItemsView => ItemsRow, InventoryView => InventoryRow, PursuitsView => PursuitsRow, SeasonPassView => SeasonPassRow,
        ActivityView => ActivityRow, _ => null
    };

    // The session opens its link once LiveGate draws a live area, so attaching only for the World map reads no account.
    public void Attach(LiveProcess process) => workspace.LiveInvestment.AttachOnUse(process);

    public void Detach(bool gameClosed) => workspace.LiveInvestment.Detach(gameClosed);

    public void Poll(LiveSectionContext context) => workspace.LiveInvestment.Poll(Environment.TickCount64);

    public void Show(string view, string? row)
    {
        requestedView = view;
        if (row is null) return;
        if (view == ItemsView) ShowItemsRow(row);
        else if (view == InventoryView) ShowInventoryRow(row);
        else if (view == PursuitsView) ShowPursuitsRow(row);
        else if (view == SeasonPassView) ShowSeasonPassRow(row);
        else if (view == ActivityView) ShowActivityRow(row);
    }

    public void Draw(LiveSectionContext context)
    {
        if (workspace.Catalog is null) { ImGui.TextDisabled("Open a Sunrise installation"); return; }
        scale = context.Scale;
        icons = context.Icons;
        var catalog = loader.RequestCatalog(workspace);
        if (!ImGui.BeginTabBar("live-investment")) return;
        if (Subtab("Items", ItemsView)) { DrawItems(context, catalog, loader.Peek(workspace)); ImGui.EndTabItem(); }
        if (Subtab("Inventory", InventoryView)) { DrawInventory(context, catalog, loader.Peek(workspace)); ImGui.EndTabItem(); }
        if (Subtab("Pursuits", PursuitsView)) { DrawPursuits(context, catalog, loader.Peek(workspace)); ImGui.EndTabItem(); }
        if (Subtab("Season pass", SeasonPassView)) { DrawSeasonPass(context, catalog, loader.Peek(workspace)); ImGui.EndTabItem(); }
        if (Subtab("Activity", ActivityView)) { DrawActivity(context, catalog); ImGui.EndTabItem(); }
        ImGui.EndTabBar();
        actions.DrawPopups(workspace.LiveInvestment);
        DrawPassPopups(workspace.LiveInvestment);
    }

    /** Begins one subtab. A requested subtab is focused once, and the open subtab becomes the shown view. */
    private bool Subtab(string title, string id)
    {
        bool focus = requestedView == id;
        if (!StudioForm.BeginEditorTab(title, focus)) return false;
        if (focus) requestedView = null;
        view = id;
        return true;
    }

    public void PrepareFrame(StudioWorkspace workspace, Dx11Renderer renderer)
    {
        pursuitDetail.Prepare();
        pursuitChain.Prepare();
        inspector.PrepareFrame(workspace);
    }

    public void Dispose() { pursuitDetail.Dispose(); pursuitChain.Dispose(); }
}
