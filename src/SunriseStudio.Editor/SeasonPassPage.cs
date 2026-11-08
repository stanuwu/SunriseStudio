using System.Globalization;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Editor;

/** The reward ladders of the progression table: a rank grid with the selected cell's rows under it. */
public sealed partial class SeasonPassPage : IStudioPage, IStudioRowPage, IFramePreparedPage
{
    public const string PageId = "sunrise.season-pass";
    // A row that history or a link asked for. The first draw after the tables are read shows it.
    private (int Progression, int Row)? requested;
    private readonly InvestmentTablesLoader loader = new();
    private InvestmentIconCache icons = InvestmentIconCache.Unprepared;
    private readonly InvestmentFieldPopup popup = new();
    private readonly InvestmentPendingMarks pending = new();
    private readonly InvestmentRewardTree rewardTree = new();
    private readonly SeasonPassGrid grid = new();
    // The row the Rewards tree last showed. Selecting another row clears the tree's selected entry.
    private (int Progression, int Row) treeRow = (-1, -1);
    public string Id => PageId;
    public string Title => "Season pass";

    public bool CanOpen(StudioDocument document) =>
        AssetCategories.IsStructured(document.Entry) && document.Entry.Reference == C80807cd9.Class;

    public string? Row => requested is { } wanted ? RowOf(wanted.Progression, wanted.Row)
        : grid.SelectedRow >= 0 ? RowOf(grid.Progression, grid.SelectedRow) : null;

    public void ShowRow(string row) => requested = ParseRow(row);

    /** The row id that history, links and Live use for a reward row: "pass:<progression>:<row>". */
    internal static string RowOf(int progression, int row) =>
        string.Create(CultureInfo.InvariantCulture, $"pass:{progression}:{row}");

    /** The progression and row a RowOf id names, or null for any other id. */
    internal static (int Progression, int Row)? ParseRow(string row) =>
        row.Split(':') is ["pass", var progression, var index]
        && int.TryParse(progression, NumberStyles.None, CultureInfo.InvariantCulture, out int shown)
        && int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out int chosen) ? (shown, chosen) : null;

    public void PrepareFrame(StudioWorkspace workspace, Dx11Renderer renderer)
    {
        popup.Invalidate(workspace);
    }

    public void Draw(StudioPageContext context)
    {
        var workspace = context.Workspace;
        icons = InvestmentIconCache.Of(context);
        if (!loader.Ready(workspace, out var catalog, out var tables)) return;
        if (workspace.Document?.Entry.TagHash != tables.TableTag(InvestmentRootSlots.Progressions))
        {
            ImGui.TextDisabled("Not the progression table");
            StudioUi.Tip("The investment root names another progression table.");
            return;
        }
        if (tables.PassCandidates.Count == 0) { ImGui.TextDisabled("No progression has reward rows"); return; }
        int focusRow = -1;
        if (requested is { } wanted && tables.PassCandidates.Any(row => row.Index == wanted.Progression))
        { grid.Show(wanted.Progression); focusRow = wanted.Row; }
        requested = null;
        if (!tables.PassCandidates.Any(row => row.Index == grid.Progression)) grid.Show(Largest(tables).Index);
        var shown = tables.Progressions[grid.Progression];
        var ladder = grid.Prepare(tables, shown);
        if (focusRow >= 0) grid.Select(shown, ladder, focusRow);
        DrawHeader(context, tables, shown, ladder);
        grid.Draw(catalog, icons, shown, ladder, null);
        DrawDetail(context, catalog, tables, shown, ladder);
        popup.Draw(workspace, _ => ItemInput(workspace, catalog, tables), _ => ItemPreview(catalog, tables),
            _ => FieldRefusal(catalog, tables));
    }

    /** The progression with the most reward rows, which the page shows until another is chosen. */
    internal static InvestmentProgression Largest(InvestmentTables tables) => tables.PassCandidates.MaxBy(row => row.Rewards.Count)!;

    /** "Progression 12 · 220 rows". */
    internal static string Label(InvestmentProgression row) => $"Progression {row.Index}{InvestmentTableViews.Dot}{InvestmentTableViews.Count(row.Rewards.Count, "row")}";

    /** Draws the progression choice, the rank count and total XP, then the Open in Live link for the selected row. */
    private void DrawHeader(StudioPageContext context, InvestmentTables tables,
        InvestmentProgression shown, SeasonPassGrid.Ladder ladder)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Season pass");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.CalcTextSize("Progression 000" + InvestmentTableViews.Dot + "0000 rows").X + ImGui.GetFrameHeight() * 2);
        if (ImGui.BeginCombo("##progression", Label(shown)))
        {
            foreach (var candidate in tables.PassCandidates)
                if (ImGui.Selectable(Label(candidate), candidate.Index == grid.Progression)) grid.Show(candidate.Index);
            ImGui.EndCombo();
        }
        StudioUi.Help("The game picks its season pass itself. This shows the largest reward ladder until another is chosen; Live shows the game's.");
        ImGui.SameLine();
        string totals = $"{InvestmentTableViews.Count(ladder.Ranks, "rank")}{InvestmentTableViews.Dot}{shown.Threshold(shown.Steps.Count):N0} XP total";
        string fitted = StudioUi.Fit(totals, ImGui.GetContentRegionAvail().X - StudioLiveLink.Room());
        ImGui.TextDisabled(fitted);
        StudioUi.Tip((fitted == totals ? "" : totals + "\n") + $"Progression {shown.Index}\n0x{shown.Hash:X8}\nScope: {shown.ScopeLabel}"
            + (shown.Error.Length != 0 ? "\n" + shown.Error : ""));
        StudioLiveLink.Draw(context, StudioLiveLink.PassTarget(grid.Progression, grid.SelectedRow));
    }

    /** Draws the selected cell: its row chips, the row's item and description, the editable item and quantity, and the rank. */
    private void DrawDetail(StudioPageContext context, InvestmentCatalog catalog,
        InvestmentTables tables, InvestmentProgression shown,
        SeasonPassGrid.Ladder ladder)
    {
        var rows = grid.SelectedRows(shown, ladder);
        if (rows.Count == 0) return;
        StudioUi.Heading(grid.SelectedTitle, "Extracted: pass reward");
        grid.DrawRowChips(catalog, icons, shown, rows, ladder);
        if (treeRow != (grid.Progression, grid.SelectedRow)) { treeRow = (grid.Progression, grid.SelectedRow); rewardTree.Reset(); }
        var row = shown.Rewards[grid.SelectedRow];
        var workspace = context.Workspace;
        float size = ImGui.GetFrameHeight() * 2;
        if (row.ItemIndex < catalog.Items.Count)
        {
            var item = catalog.Items[(int)row.ItemIndex];
            icons.Draw(catalog, item.Index, size);
            ImGui.SameLine(); ImGui.BeginGroup();
            ImGui.TextColored(StudioTheme.Rarity(item.Tier), item.Name);
            StudioUi.Tip($"Item {item.Index}\n{item.Hash:X8}");
            ImGui.TextDisabled($"{item.Rarity}  {item.TypeName}");
            ImGui.EndGroup();
            ImGui.SameLine();
            if (ImGui.Button("Open item")) workspace.Select(item.Tag);
            if (item.Description.Length != 0) ImGui.TextWrapped(item.Description);
        }
        else ImGui.TextColored(StudioTheme.Failure, InvestmentTableViews.ItemLabel(catalog, (int)row.ItemIndex));
        float label = ImGui.CalcTextSize("Required rank").X + ImGui.GetStyle().ItemSpacing.X * 2;
        EditLine("Item", row.ItemField, $"Row {row.Index}", label,
            InvestmentTableViews.ItemLabel(catalog, (int)Math.Min(row.ItemIndex, int.MaxValue)));
        EditLine("Quantity", row.QuantityField, $"Row {row.Index}", label);
        // A new rank would move the reward onto another rank's cell, so the rank is shown, not edited.
        ImGui.AlignTextToFramePadding(); ImGui.TextDisabled("Required rank"); ImGui.SameLine(label);
        ImGui.TextUnformatted(row.Rank.ToString(CultureInfo.InvariantCulture));
        var facts = new List<(string Name, string Value, string Tip)> { ClaimFact(tables, row) };
        var conditions = InvestmentTableViews.ConditionLines(tables.Unlocks, row.Conditions);
        string conditionTip = string.Join("\n",
            row.Conditions.Members.SelectMany(tables.Unlocks.Problems).Prepend(string.Join("\n", conditions)));
        for (int i = 0; i < conditions.Count; i++) facts.Add((i == 0 ? "Conditions" : "", conditions[i], conditionTip));
        if (row.Error.Length != 0) facts.Add(("Problem", row.Error, row.Error));
        StudioUi.Facts("pass-row", facts);
        int pool = row.ItemIndex < catalog.Items.Count ? tables.WrapperPool((int)row.ItemIndex) : -1;
        if (pool >= 0 && ImGui.CollapsingHeader($"Rewards (Pool {pool})###pass-rewards"))
            rewardTree.Draw(context, catalog, tables, pool, popup, pending, icons, "pass-rewards");
        if (row.Sockets.Count != 0 && ImGui.CollapsingHeader($"Socket overrides ({row.Sockets.Count})###pass-sockets"))
            StudioUi.Facts("pass-sockets", row.Sockets.Select(socket =>
            {
                var (text, tip) = InvestmentTableViews.SocketOverride(catalog, socket);
                return ($"Socket type {socket.SocketType}", text, tip);
            }));

        void EditLine(string name, InvestmentTableField field, string owner, float width, string? display = null)
        {
            ImGui.PushID(name);
            ImGui.AlignTextToFramePadding(); ImGui.TextDisabled(name); ImGui.SameLine(width);
            if (ImGui.SmallButton((display ?? field.Property.Value) + "##value"))
            {
                popup.Begin(workspace, field, owner);
                if (field.Kind == InvestmentTableFieldKind.PassRewardItem) BeginItemEdit();
            }
            pending.Dot(workspace, field);
            ImGui.PopID();
        }
    }

    /** The row's claim flag by its installed label, or None. */
    internal static (string Name, string Value, string Tip) ClaimFact(InvestmentTables tables, InvestmentSeasonPassRow row) =>
        ("Claim flag", row.ClaimSlot == 0 ? "None" : tables.Unlocks.FlagLabel(row.ClaimSlot).Text,
            row.ClaimSlot == 0 ? "The row has no claim flag." : tables.Unlocks.FlagLabel(row.ClaimSlot).Detail);
}
