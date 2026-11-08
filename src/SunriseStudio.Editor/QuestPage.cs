using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Rendering;

namespace SunriseStudio.Editor;

/** A quest's step chain on a read-only canvas, with the selected step's objectives, sources, completion and data beside it. */
public sealed class QuestPage : IStudioPage, IStudioRowPage, IFramePreparedPage, IDisposable
{
    public const string PageId = "sunrise.quest";
    private const string Dot = InvestmentTableViews.Dot;
    private const string StepKey = QuestChainView.StepKey;
    /** Icon side of the page header, at UI scale 1. */
    private const float HeaderIconSide = 48;
    /** The inspector's share of the page width, its width bounds at UI scale 1, and the largest share of the page it may take. */
    private const float InspectorShare = .34f, InspectorMinimum = 260, InspectorMaximum = 460, InspectorMostShare = .5f;
    /** Below this page width at UI scale 1, the inspector moves under the canvas, which then keeps this share of the height. */
    private const float NarrowWidth = 640, NarrowCanvasShare = .55f;
    /** The canvas and inspector keep at least this many font heights, so a short window still shows a few node rows. */
    private const float MinimumFonts = 16;
    private readonly InvestmentTablesLoader loader = new();
    private InvestmentIconCache icons = InvestmentIconCache.Unprepared;
    private readonly QuestChainView chain = new();
    private readonly PursuitInspector inspector = new();
    private string selected = "";
    // The step member that history or a link asked for, selected once the chain is known.
    private int? requested;
    // The entry and added asset the page last showed. An edit, undo or redo reads the same entry into a new document, which
    // keeps the selected step.
    private (uint Tag, string? AddedId)? shownDocument;
    public string Id => PageId;
    public string Title => "Quest";

    public bool CanOpen(StudioDocument document) => PursuitDocuments.Of(document).Shape == InvestmentPursuitShape.Quest;

    /** "step:<member>", the inspected step as history names it. */
    public string? Row => requested is { } wanted ? StepRow(wanted)
        : selected.StartsWith(StepKey, StringComparison.Ordinal) ? "step:" + selected[StepKey.Length..] : null;

    public void ShowRow(string row)
    {
        if (row.Split(':') is ["step", var text]
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int member)) requested = member;
    }

    /** The row history records for a step of the shown chain. */
    internal static string StepRow(int member) => string.Create(CultureInfo.InvariantCulture, $"step:{member}");

    public void PrepareFrame(StudioWorkspace workspace, Dx11Renderer renderer)
    {
        chain.Prepare();
        inspector.PrepareFrame(workspace);
    }

    public void Dispose() => chain.Dispose();

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
        if (workspace.SelectedInvestmentItem is not { } detail || workspace.Catalog is not { } packages) { ImGui.TextDisabled("Not an item"); return; }
        var item = detail.Item;
        // A step names its owner. An item that names none, such as an owner without objectives, is its own owner.
        int owner = QuestChainView.OwnerOf(tables, item.Index);
        var known = detail.Pursuit?.Chain is { } read && read.OwnerItem == owner ? read : null;
        var steps = chain.Want(catalog, packages, owner, known);
        // A newly opened step selects itself. An owner without objectives selects its first step once the chain is known.
        var opened = workspace.Document is { } document ? (document.Entry.TagHash,
            document.AddedAssetId) : ((uint, string?)?)null;
        if (shownDocument != opened || selected.Length == 0 && detail.Pursuit is null && steps is { Steps.Count: > 0 })
        {
            shownDocument = opened;
            selected = QuestChainView.KeyOf(steps, item.Index) is { Length: > 0 } open ? open
                : detail.Pursuit is null && steps is { Steps.Count: > 0 } ? StepKey + steps.Steps[0].Member : "";
        }
        if (requested is { } wanted && steps is not null)
        {
            if (steps.Steps.Any(step => step.Member == wanted)) selected = StepKey + wanted.ToString(CultureInfo.InvariantCulture);
            requested = null;
        }
        DrawHeader(context, catalog, tables, item, steps);
        var built = chain.Model(catalog, tables, steps, owner, item.Index);
        chain.DrawTools("quest", owner, built);
        DrawSplit(context, catalog, tables, detail, steps, built, owner);
    }

    /** Icon, name and type line of the open item, with its step count, a help mark naming the chain value, and Open in Live. */
    private void DrawHeader(StudioPageContext context, InvestmentCatalog catalog, InvestmentTables tables,
        InvestmentItem item, InvestmentQuestSet? steps)
    {
        float side = HeaderIconSide * InvestmentTableViews.PageScale;
        icons.Draw(catalog, item.Index, side);
        ImGui.SameLine();
        ImGui.BeginGroup();
        string count = steps is null ? "Quest" : $"Quest{Dot}{InvestmentTableViews.Count(steps.Steps.Count, "step")}";
        // The name gives way to the step count, its help and Open in Live on the same line.
        float room = ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(count + " (?)").X - ImGui.GetStyle().ItemSpacing.X * 3
            - StudioLiveLink.Room();
        string name = StudioUi.Fit(item.Name, room);
        ImGui.TextColored(StudioTheme.Rarity(item.Tier), name);
        StudioUi.Tip($"{item.Name}\nItem {item.Index}{Dot}{item.Hash:X8}");
        ImGui.SameLine();
        ImGui.TextDisabled(count);
        // A step names its owner, but the owner's set may still leave it out. The canvas then has no node for it.
        bool outside = steps is { Steps.Count: > 0 } && steps.OwnerItem != item.Index
            && steps.Steps.All(row => row.ItemIndex != item.Index);
        if (steps is not null)
            StudioUi.Help((steps.ModeDecoded ? "Each step's value identifies it; it is not a progress count." : $"Mode {steps.Mode}. Only mode 1 is decoded.")
                + "\nChain value: " + (steps.ValueSlot == ushort.MaxValue ? "None" : tables.Unlocks.ValueLabel(steps.ValueSlot).Text)
                + (outside ? "\nThis item names this quest as its owner but is not one of its steps." : ""));
        StudioLiveLink.Draw(context, StudioLiveLink.PursuitTarget(item));
        ImGui.TextDisabled(StudioUi.Fit(item.TypeName, ImGui.GetContentRegionAvail().X));
        ImGui.EndGroup();
    }

    /** Canvas and inspector side by side, or stacked in a narrow page. The inspector always shows a step. */
    private void DrawSplit(StudioPageContext context, InvestmentCatalog catalog,
        InvestmentTables tables, InvestmentItemDetail detail,
        InvestmentQuestSet? steps, QuestModel? built, int owner)
    {
        float scale = InvestmentTableViews.PageScale;
        var size = Vector2.Max(ImGui.GetContentRegionAvail(), new(0, MinimumFonts * ImGui.GetFontSize()));
        bool narrow = size.X < NarrowWidth * scale;
        float inspectorWidth = narrow ? size.X
            : Math.Min(Math.Clamp(size.X * InspectorShare, InspectorMinimum * scale,
                InspectorMaximum * scale), size.X * InspectorMostShare);
        var canvasSize = narrow ? new Vector2(size.X,
            size.Y * NarrowCanvasShare) : new(size.X - inspectorWidth - ImGui.GetStyle().ItemSpacing.X, size.Y);
        ImGui.BeginChild("quest-canvas", canvasSize, ImGuiChildFlags.Borders,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        var workspace = context.Workspace;
        chain.Draw(catalog, steps, built, owner, icons, ref selected, step => workspace.Open(catalog.Items[step.ItemIndex].Tag));
        ImGui.EndChild();
        if (!narrow) ImGui.SameLine();
        ImGui.BeginChild("quest-inspector", narrow ? Vector2.Zero : new(inspectorWidth, size.Y), ImGuiChildFlags.Borders);
        DrawInspector(context, catalog, tables, detail, steps);
        ImGui.EndChild();
        chain.DrawTooltip(built);
    }

    /** The selected step's inspector, or the open item's when no other step is selected. */
    private void DrawInspector(StudioPageContext context, InvestmentCatalog catalog,
        InvestmentTables tables, InvestmentItemDetail open,
        InvestmentQuestSet? steps)
    {
        var step = steps is null ? null : QuestChainView.Member(steps, selected);
        if (step is null || step.ItemIndex == open.Item.Index) { inspector.Draw(context, catalog, tables, open, tabbed: true); return; }
        var read = chain.Steps.Steps?.ElementAtOrDefault(step.Member);
        if (read is null) { ImGui.TextDisabled("Reading."); return; }
        if (read.Detail is not { } detail)
        {
            ImGui.TextColored(StudioTheme.Failure, "Step not read.");
            StudioUi.Tip(read.Error);
            return;
        }
        inspector.Draw(context, catalog, tables, detail, tabbed: true);
    }
}
