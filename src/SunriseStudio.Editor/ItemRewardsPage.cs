using System.Globalization;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Rendering;

namespace SunriseStudio.Editor;

/** How a Rewards row draws its text. */
internal enum RewardLineStyle { Normal, Muted, Failure }
/** What a Rewards row's button opens; None draws no button. */
internal enum RewardLinkKind { None, Item, Pool }

/**
 * One Rewards row: text, a muted detail, the tooltip, the item whose colour and icon it uses (-1 for none) and its link.
 * Target names the link's item or pool index.
 */
internal readonly record struct RewardLine(string Text, string Detail = "", string Tip = "", int Item = -1,
    RewardLinkKind Link = RewardLinkKind.None, int Target = -1, RewardLineStyle Style = RewardLineStyle.Normal);

/** What the Rewards page shows for one item, built once per detail and table snapshot. */
internal sealed record RewardsModel(IReadOnlyList<RewardLine> DrawTable, (string Value, string Tip)? AcquiredFlag,
    IReadOnlyList<RewardLine> Previews);

/** An item's rewards: its draw table as a tree and its preview. */
public sealed class ItemRewardsPage : IStudioPage, IFramePreparedPage
{
    public const string PageId = "sunrise.item-rewards";
    private const string Dot = InvestmentTableViews.Dot;
    private const string DrawTableTip = "Extracted: what opening this item can give. Activity reward mappings are not in this install.";
    private const string PreviewTip = InvestmentTableViews.PreviewTip;
    private readonly InvestmentTablesLoader loader = new();
    private readonly InvestmentFieldPopup popup = new();
    private readonly InvestmentPendingMarks pending = new();
    private readonly InvestmentRewardTree tree = new();
    private InvestmentIconCache icons = InvestmentIconCache.Unprepared;
    private (InvestmentItemDetail? Detail, InvestmentTables? Tables) modelKey;
    private RewardsModel? model;
    private uint shownItem;
    public string Id => PageId;
    public string Title => "Rewards";

    // The item and display index tables carry the item class but are not items.
    public bool CanOpen(StudioDocument document) => document.Entry.Reference == InvestmentCatalog.ItemClass
        && document.Entry.TagHash is not (InvestmentCatalog.ItemIndexTag or InvestmentCatalog.DisplayIndexTag);

    public void PrepareFrame(StudioWorkspace workspace, Dx11Renderer renderer) => popup.Invalidate(workspace);

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
        if (!ReferenceEquals(modelKey.Detail, detail) || !ReferenceEquals(modelKey.Tables, tables))
        { modelKey = (detail, tables); model = Build(catalog, tables, detail); }
        if (shownItem != detail.Item.Tag) { shownItem = detail.Item.Tag; tree.Reset(); }
        DrawDrawTable(context, catalog, tables, detail, model!);
        StudioUi.Heading("PREVIEW", PreviewTip);
        if (detail.Pursuit?.Previews.Count > 0) ImGui.TextDisabled("Shown in the game's pursuit screen; not a grant.");
        DrawLines(context, catalog, tables, "previews", model!.Previews);
        popup.Draw(workspace, refusal: _ => InvestmentRewardTree.QuantityRefusal(catalog, tables, popup));
    }

    /** Draws the draw table's summary and pool lines, its acquisition flag, then the pool as a tree. */
    private void DrawDrawTable(StudioPageContext context, InvestmentCatalog catalog,
        InvestmentTables tables, InvestmentItemDetail detail,
        RewardsModel rewards)
    {
        StudioUi.Heading("DRAW TABLE", DrawTableTip);
        if (detail.Wrapper is not { } wrapper) { DrawLines(context, catalog, tables, "draw", rewards.DrawTable); return; }
        DrawLine(context, catalog, tables, rewards.DrawTable[0]);
        StudioUi.Help("Weights are relative. Conditions, class and earlier picks change the odds, so Studio shows no chances.");
        if (wrapper.Error.Length != 0) ImGui.TextColored(StudioTheme.Failure, wrapper.Error);
        DrawLine(context, catalog, tables, rewards.DrawTable[1]);
        if (rewards.AcquiredFlag is { } flag) StudioUi.Facts("item-rewards-flag", [("Acquired flag", flag.Value, flag.Tip)]);
        tree.Draw(context, catalog, tables, wrapper.Pool, popup, pending, icons, "item-rewards");
    }

    /** Draws one section's lines, clipped to the visible rows. Every line is one frame high. */
    private unsafe void DrawLines(StudioPageContext context, InvestmentCatalog catalog, InvestmentTables tables, string id,
        IReadOnlyList<RewardLine> lines)
    {
        ImGui.PushID(id);
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(lines.Count, ImGui.GetFrameHeightWithSpacing());
        while (clipper.Step())
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                ImGui.PushID(i);
                DrawLine(context, catalog, tables, lines[i]);
                ImGui.PopID();
            }
        clipper.End(); clipper.Destroy();
        ImGui.PopID();
    }

    /**
     * Draws the item's icon, the text, the muted detail and the link at the right edge. The text is shortened to leave room
     * for the link. A line without an item keeps the icon's room, so the texts of a section line up.
     */
    private void DrawLine(StudioPageContext context, InvestmentCatalog catalog, InvestmentTables tables, RewardLine line)
    {
        var style = ImGui.GetStyle();
        float start = ImGui.GetCursorPosX(), width = ImGui.GetContentRegionAvail().X;
        // A muted line, such as a section's empty line, keeps no icon room, so the muted lines of all sections line up.
        if (line.Style != RewardLineStyle.Muted)
        {
            float side = ImGui.GetFrameHeight();
            icons.Draw(catalog, line.Item, side);
            ImGui.SameLine();
        }
        string link = LinkLabel(line.Link);
        float linkWidth = link.Length == 0 ? 0 : ImGui.CalcTextSize(link).X + style.FramePadding.X * 2;
        float room = start + width - ImGui.GetCursorPosX() - (link.Length == 0 ? 0 : linkWidth + style.ItemSpacing.X);
        ImGui.AlignTextToFramePadding();
        string text = StudioUi.Fit(line.Text, room);
        if (line.Style == RewardLineStyle.Failure) ImGui.TextColored(StudioTheme.Failure, text);
        else if (line.Style == RewardLineStyle.Muted) ImGui.TextDisabled(text);
        else if (line.Item >= 0
            && line.Item < catalog.Items.Count) ImGui.TextColored(StudioTheme.Rarity(catalog.Items[line.Item].Tier), text);
        else ImGui.TextUnformatted(text);
        StudioUi.Tip(text == line.Text ? line.Tip : $"{line.Text}\n{line.Tip}".TrimEnd());
        if (line.Detail.Length != 0 && text == line.Text)
        {
            ImGui.SameLine();
            string detail = StudioUi.Fit(line.Detail, room - ImGui.CalcTextSize(text).X - style.ItemSpacing.X);
            ImGui.TextDisabled(detail);
            StudioUi.Tip(detail == line.Detail ? line.Tip : $"{line.Detail}\n{line.Tip}".TrimEnd());
        }
        if (link.Length == 0) return;
        ImGui.SameLine(start + width - linkWidth);
        if (ImGui.SmallButton(link)) Follow(context, catalog, tables, line);
    }

    private static string LinkLabel(RewardLinkKind link) => link switch
    {
        RewardLinkKind.Item => "Open",
        RewardLinkKind.Pool => "Open in Reward pools",
        _ => ""
    };

    /** Opens the line's item, or its pool on the Reward pools page with the pool selected. */
    private static void Follow(StudioPageContext context, InvestmentCatalog catalog, InvestmentTables tables, RewardLine line)
    {
        var workspace = context.Workspace;
        switch (line.Link)
        {
            case RewardLinkKind.Item: workspace.Open(catalog.Items[line.Target].Tag); break;
            case RewardLinkKind.Pool:
                workspace.Open(tables.TableTag(InvestmentRootSlots.Pools));
                context.ShowPage?.Invoke(RewardPoolsPage.PageId, RewardPoolsPage.RowOf(line.Target));
                break;
        }
    }

    /** Builds every section's rows for one item. A link to the pool table is offered only when that table is present. */
    internal static RewardsModel Build(InvestmentCatalog catalog, InvestmentTables tables, InvestmentItemDetail detail)
    {
        var (drawTable, flag) = DrawTableLines(tables, detail);
        return new(drawTable, flag, PreviewLines(catalog, tables, detail));
    }

    /** The wrapper's summary and pool lines, or one line saying the item has no draw table or it could not be read. */
    private static (RewardLine[] Lines,
        (string, string)? Flag) DrawTableLines(InvestmentTables tables, InvestmentItemDetail detail)
    {
        if (detail.Wrapper is not { } wrapper)
            return detail.Diagnostics.FirstOrDefault(line => line.StartsWith("Rewards: ",
                StringComparison.Ordinal)) is { } failure
                ? ([new("Draw table not read.", Tip: failure["Rewards: ".Length..], Style: RewardLineStyle.Failure)], null)
                : ([new("No draw table", Tip: "This item has no wrapper block, so opening it gives nothing from the client data.",
                    Style: RewardLineStyle.Muted)], null);
        var pool = wrapper.Pool < tables.Pools.Count ? tables.Pools[wrapper.Pool] : null;
        string summary = (wrapper.OpensOnAcquisition ? "Opens when acquired" : "Kept as an item when acquired")
            + string.Concat(wrapper.Selections.Select(selection => Dot + $"Draws {selection.Count} from "
                + (selection.Category == InvestmentRewards.AnyCategory ? "rows marked Any" : $"category 0x{selection.Category:X8}")));
        string tip = (wrapper.OpensOnAcquisition ? "Sunrise picks the rewards when it grants the item, with its own draw, not the retail one."
            : "The item stays in the inventory until it is opened.") + string.Concat(wrapper.Selections.Select(selection => "\nExtracted."
                + (pool is null ? "" : $" Base total {InvestmentTableViews.Weight((float)InvestmentRewardGraph.BaseTotal(pool, selection.Category))}: "
                    + "the weights this selection draws from, before conditions, class and earlier picks.")));
        bool usable = pool is not null && InvestmentTableViews.Usable(tables.TableTag(InvestmentRootSlots.Pools));
        RewardLine poolLine = new($"Pool {wrapper.Pool}" + (pool is null ? " (unreadable)" : Dot + InvestmentTableViews.Count(pool.Entries.Count, "entry", "entries")),
            Tip: pool is null ? "The reward pool table has no such row." : $"Pool {wrapper.Pool}\n0x{pool.Hash:X8}",
            Link: usable ? RewardLinkKind.Pool : RewardLinkKind.None, Target: wrapper.Pool,
            Style: pool is null ? RewardLineStyle.Failure : RewardLineStyle.Muted);
        (string, string)? flag = wrapper.AcquiredFlagSlot == InvestmentCatalog.Absent ? ("None",
            "The wrapper names no acquisition flag.")
            : (tables.Unlocks.FlagLabel(wrapper.AcquiredFlagSlot).Text,
                tables.Unlocks.FlagLabel(wrapper.AcquiredFlagSlot).Detail);
        return ([new(summary, Tip: tip), poolLine], flag);
    }

    /** The reward previews the pursuit screen shows, each with its quantity and condition. */
    private static RewardLine[] PreviewLines(InvestmentCatalog catalog, InvestmentTables tables, InvestmentItemDetail detail)
    {
        var previews = detail.Pursuit?.Previews ?? [];
        if (previews.Count == 0) return [new("No preview", Style: RewardLineStyle.Muted)];
        return [.. previews.Select(preview =>
        {
            bool known = preview.ItemIndex < catalog.Items.Count;
            string detailText = $"x{preview.Quantity.ToString(CultureInfo.InvariantCulture)}" + (preview.Condition is null ? "" : Dot + "Conditional");
            return new RewardLine(InvestmentTableViews.ItemLabel(catalog, preview.ItemIndex), detailText,
                preview.Condition is { } condition ? InvestmentTableViews.ConditionTip(tables.Unlocks, condition) : "",
                known ? preview.ItemIndex : -1, known ? RewardLinkKind.Item : RewardLinkKind.None, preview.ItemIndex,
                Style: known ? RewardLineStyle.Normal : RewardLineStyle.Failure);
        })];
    }

}
