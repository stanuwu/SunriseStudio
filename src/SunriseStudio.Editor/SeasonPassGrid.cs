using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/**
 * One progression's reward ladder as a page of rank cells per lane, with the rows no rank cell shows listed under it. It
 * holds the shown progression, the page of ranks and the chosen cell and row. Live passes an overlay that draws the game's
 * claim state over each cell, so the grid never reads the game.
 */
internal sealed class SeasonPassGrid
{
    public const int Regular = 0, Premium = 1;
    public static readonly string[] LaneNames = ["Regular", "Premium"];
    private const string PremiumHelp = "Rows that start with the pass ownership condition. The data names no tracks. " + StudioUi.MayBeInaccurate;
    // The in-game pass shows ten ranks at a time.
    private const int RanksPerPage = 10;
    /** Canvas at 70% opacity dims a cell the search does not match while its icon still shows through. */
    private static readonly Vector4 Unmatched = StudioTheme.Canvas with { W = .7f };
    private int firstRank = 1, jumpRank = -1;
    private string find = "";
    private bool revealOutside;
    private Ladder? cache;

    /** Each row's lane and class, the rank count and the rows no rank cell shows, cached per table snapshot and progression. */
    public sealed record Ladder(InvestmentTables Tables, int Progression, int[] Lanes, int[] Classes, int Ranks, int[] Outside);

    /** Draws over one cell, given the rows it shows, its corners and whether it is the chosen cell. */
    public delegate void CellOverlay(IReadOnlyList<int> rows, Vector2 min, Vector2 max, bool chosen);

    /** The shown progression's index, -1 before one is shown. */
    public int Progression { get; private set; } = -1;
    public int SelectedRank { get; private set; } = -1;
    public int SelectedLane { get; private set; }
    public int SelectedRow { get; private set; } = -1;
    public string SelectedTitle => $"Rank {SelectedRank}{InvestmentTableViews.Dot}{LaneNames[SelectedLane]}";

    /** Shows a progression from its first rank with nothing chosen. The progression already shown keeps its page and choice. */
    public void Show(int progression)
    {
        if (progression == Progression) return;
        Progression = progression; firstRank = 1; SelectedRank = -1; SelectedRow = -1;
    }

    /** Chooses a row of the shown progression and pages to its rank. */
    public void Select(InvestmentProgression shown, Ladder ladder, int row)
    {
        if (row < 0 || row >= shown.Rewards.Count) return;
        SelectedRow = row; SelectedRank = (int)shown.Rewards[row].Rank; SelectedLane = ladder.Lanes[row];
        jumpRank = SelectedRank;
    }

    public Ladder Prepare(InvestmentTables tables, InvestmentProgression shown)
    {
        if (cache is { } cached && ReferenceEquals(cached.Tables, tables) && cached.Progression == shown.Index) return cached;
        var rows = shown.Rewards;
        var lanes = rows.Select(row => InvestmentSeasonPass.LooksLikeOwnership(row.Conditions,
            tables.Unlocks) ? Premium : Regular).ToArray();
        var classes = rows.Select(row => row.Conditions.Members.SelectMany(member => member.Instructions)
            .Select(tables.Unlocks.ClassOf).FirstOrDefault(index => index >= 0, -1)).ToArray();
        int ranks = Math.Max(1, Math.Max(shown.Steps.Count,
            rows.Select(row => (int)Math.Min(row.Rank, InvestmentSeasonPass.MaximumRank)).DefaultIfEmpty(0).Max()));
        var outside = Enumerable.Range(0, rows.Count).Where(row => rows[row].Rank == 0 || rows[row].Rank > ranks).ToArray();
        return cache = new(tables, shown.Index, lanes, classes, ranks, outside);
    }

    /** Draws one page of ten ranks, paged like the in-game pass, then the rows outside the ranks. */
    public void Draw(InvestmentCatalog catalog, InvestmentIconCache icons, InvestmentProgression shown,
        Ladder ladder, CellOverlay? overlay)
    {
        DrawRanks(catalog, icons, shown, ladder, overlay);
        DrawOutside(catalog, icons, shown, ladder);
    }

    private void DrawRanks(InvestmentCatalog catalog, InvestmentIconCache icons, InvestmentProgression shown,
        Ladder ladder, CellOverlay? overlay)
    {
        var style = ImGui.GetStyle();
        float laneWidth = ImGui.CalcTextSize(LaneNames[Premium] + " (?)").X + style.ItemSpacing.X * 2;
        // Cells are sized so ten ranks fill the pane's width, but never smaller than a one-frame icon.
        float frame = style.FramePadding.X * 2;
        float fit = (ImGui.GetContentRegionAvail().X - laneWidth) / RanksPerPage - style.CellPadding.X * 2;
        float cell = Math.Max(fit, ImGui.GetFrameHeight() + frame), icon = cell - frame;
        if (jumpRank >= 1) { firstRank = (jumpRank - 1) / RanksPerPage * RanksPerPage + 1; jumpRank = -1; }
        firstRank = Math.Clamp(firstRank, 1, ladder.Ranks);
        int last = Math.Min(ladder.Ranks, firstRank + RanksPerPage - 1);
        if (StudioUi.IconButton("pass-previous", StudioIcon.Back, "Previous ranks",
            firstRank > 1)) firstRank = Math.Max(1, firstRank - RanksPerPage);
        ImGui.SameLine(); ImGui.AlignTextToFramePadding(); ImGui.TextUnformatted($"Ranks {firstRank}-{last}");
        ImGui.SameLine();
        if (StudioUi.IconButton("pass-next", StudioIcon.Forward, "Next ranks", last < ladder.Ranks)) firstRank = last + 1;
        ImGui.SameLine(); ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##find-reward", "Find reward", ref find, 128)
            && find.Length != 0) Find(catalog, shown, ladder);
        var flags = ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoPadOuterX;
        if (!ImGui.BeginTable("pass-grid", last - firstRank + 2, flags)) return;
        ImGui.TableSetupColumn("##lane", ImGuiTableColumnFlags.WidthFixed, laneWidth);
        for (int rank = firstRank; rank <= last; rank++) ImGui.TableSetupColumn($"{rank}",
            ImGuiTableColumnFlags.WidthFixed, cell);
        ImGui.TableNextRow(); ImGui.TableNextColumn();
        for (int rank = firstRank; rank <= last; rank++)
        {
            ImGui.TableNextColumn();
            string number = rank.ToString(CultureInfo.InvariantCulture);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (cell - ImGui.CalcTextSize(number).X) / 2);
            ImGui.TextDisabled(number);
            StudioUi.Tip($"Rank {rank}: {shown.Threshold(rank):N0} XP");
        }
        for (int lane = Regular; lane <= Premium; lane++)
        {
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (icon - ImGui.GetTextLineHeight()) / 2);
            ImGui.TextUnformatted(LaneNames[lane]);
            if (lane == Premium) StudioUi.Help(PremiumHelp);
            ImGui.PushID(lane);
            for (int rank = firstRank; rank <= last; rank++)
            {
                ImGui.TableNextColumn();
                ImGui.PushID(rank);
                Cell(catalog, icons, shown, ladder, rank, lane, new(cell, cell + ImGui.GetTextLineHeight()), overlay);
                ImGui.PopID();
            }
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    /** Pages to the lowest rank with a match. When only rows outside the grid match, opens that list and chooses the first. */
    private void Find(InvestmentCatalog catalog, InvestmentProgression shown, Ladder ladder)
    {
        var matches = Enumerable.Range(0, shown.Rewards.Count).Where(row => Matches(catalog, shown.Rewards[row])).ToArray();
        int match = matches.Select(row => (int)shown.Rewards[row].Rank).Where(rank => rank >= 1
            && rank <= ladder.Ranks).DefaultIfEmpty(-1).Min();
        if (match >= 1) jumpRank = match;
        else if (matches.Length != 0) { Choose(shown, ladder, matches[0]); revealOutside = true; }
    }

    /** Draws the icon and quantity of the cell's first row, with a +N badge when several rows share the rank and lane. */
    private void Cell(InvestmentCatalog catalog, InvestmentIconCache icons, InvestmentProgression shown,
        Ladder ladder, int rank, int lane,
        Vector2 size, CellOverlay? overlay)
    {
        var rows = Rows(shown, ladder, rank, lane);
        var min = ImGui.GetCursorScreenPos();
        if (ImGui.InvisibleButton("cell", size) && rows.Count != 0) { SelectedRank = rank; SelectedLane = lane; SelectedRow = rows[0]; }
        bool hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        var max = min + size;
        float rounding = ImGui.GetStyle().FrameRounding, iconSize = size.X - ImGui.GetStyle().FramePadding.X * 2;
        bool chosen = SelectedRank == rank && SelectedLane == lane;
        bool matches = find.Length == 0 || rows.Any(row => Matches(catalog, shown.Rewards[row]));
        draw.AddRectFilled(min, max, ImGui.GetColorU32(hovered ? ImGuiCol.FrameBgHovered : ImGuiCol.FrameBg), rounding);
        if (chosen) draw.AddRect(min, max, ImGui.GetColorU32(StudioTheme.Accent), rounding, ImDrawFlags.None, 2);
        if (rows.Count == 0) { StudioUi.Tip("No reward at this rank."); return; }
        var first = shown.Rewards[rows[0]];
        var iconMin = min + new Vector2(ImGui.GetStyle().FramePadding.X, ImGui.GetStyle().FramePadding.X);
        if (first.Error.Length != 0)
            ImGuiIcons.Draw(draw, StudioIcon.Warning, iconMin + new Vector2(iconSize * .2f), iconSize * .6f,
                ImGui.GetColorU32(StudioTheme.Caution));
        else icons.Draw(draw, catalog, (int)first.ItemIndex, iconMin, iconSize);
        if (first.Quantity > 1)
        {
            string quantity = $"x{first.Quantity:N0}";
            draw.AddText(new(min.X + (size.X - ImGui.CalcTextSize(quantity).X) / 2, max.Y - ImGui.GetTextLineHeight()),
                ImGui.GetColorU32(ImGuiCol.TextDisabled), quantity);
        }
        if (rows.Count > 1)
        {
            string badge = $"+{rows.Count - 1}";
            var at = new Vector2(max.X - ImGui.CalcTextSize(badge).X - 2, min.Y + 1);
            draw.AddText(at, ImGui.GetColorU32(StudioTheme.Accent), badge);
        }
        overlay?.Invoke(rows, min, max, chosen);
        if (!matches) draw.AddRectFilled(min, max, ImGui.GetColorU32(Unmatched), rounding);
        if (hovered) StudioUi.Tip(string.Join("\n",
            rows.Select(row => RowLine(catalog, shown.Rewards[row], ladder.Classes[row]))));
    }

    /** Rows whose rank no cell shows, each with its item's icon, so they can still be selected and fixed. */
    private void DrawOutside(InvestmentCatalog catalog, InvestmentIconCache icons, InvestmentProgression shown, Ladder ladder)
    {
        if (ladder.Outside.Length == 0) return;
        if (revealOutside) { ImGui.SetNextItemOpen(true); revealOutside = false; }
        if (!ImGui.CollapsingHeader($"Outside ranks 1-{ladder.Ranks} ({ladder.Outside.Length})###pass-outside")) return;
        foreach (int index in ladder.Outside)
        {
            var row = shown.Rewards[index];
            if (IconList.Line($"##outside{index}", icons, catalog, ItemOf(row),
                $"Rank {row.Rank}{InvestmentTableViews.Dot}{RowLine(catalog, row, ladder.Classes[index])}",
                    ImGui.GetColorU32(ImGuiCol.Text), index == SelectedRow && SelectedRank == row.Rank))
                Choose(shown, ladder, index);
        }
    }

    private void Choose(InvestmentProgression shown, Ladder ladder, int row)
    {
        SelectedRank = (int)shown.Rewards[row].Rank; SelectedLane = ladder.Lanes[row]; SelectedRow = row;
    }

    /** The chosen cell's rows, empty while no cell is chosen. A chosen row outside the cell moves to the cell's first row. */
    public List<int> SelectedRows(InvestmentProgression shown, Ladder ladder)
    {
        var rows = Rows(shown, ladder, SelectedRank, SelectedLane);
        if (rows.Count != 0 && !rows.Contains(SelectedRow)) SelectedRow = rows[0];
        return rows;
    }

    /** Draws one chip per row, after its item's icon, when several rows share the chosen cell. Clicking a chip chooses its row. */
    public void DrawRowChips(InvestmentCatalog catalog, InvestmentIconCache icons, InvestmentProgression shown,
        IReadOnlyList<int> rows, Ladder ladder)
    {
        if (rows.Count < 2) return;
        ImGui.AlignTextToFramePadding(); ImGui.TextDisabled("Rows");
        foreach (int index in rows)
        {
            ImGui.SameLine();
            icons.Draw(catalog, ItemOf(shown.Rewards[index]), ImGui.GetFrameHeight());
            ImGui.SameLine();
            string chip = $"Row {index}" + (ladder.Classes[index] >= 0 ? "  " + InvestmentTableViews.ClassName(ladder.Classes[index]) : "");
            if (ImGui.Selectable(chip + $"##row{index}", index == SelectedRow, ImGuiSelectableFlags.None,
                ImGui.CalcTextSize(chip))) SelectedRow = index;
        }
    }

    /** The row's item index for its icon; an index past the items draws no icon. */
    private static int ItemOf(InvestmentSeasonPassRow row) => (int)Math.Min(row.ItemIndex, int.MaxValue);

    public static List<int> Rows(InvestmentProgression shown, Ladder ladder, int rank, int lane) =>
        Enumerable.Range(0, shown.Rewards.Count).Where(row => shown.Rewards[row].Rank == rank
            && ladder.Lanes[row] == lane).ToList();

    private bool Matches(InvestmentCatalog catalog, InvestmentSeasonPassRow row) => row.ItemIndex < catalog.Items.Count
        && catalog.Items[(int)row.ItemIndex].Name.Contains(find, StringComparison.OrdinalIgnoreCase);

    /** The item and quantity, as "<item> x1 (<class>)" when the row needs a class, or why the row cannot be read. */
    public static string RowLine(InvestmentCatalog catalog, InvestmentSeasonPassRow row,
        int characterClass) => row.Error.Length != 0
        ? $"Row {row.Index} is not readable: {row.Error}"
        : $"{InvestmentTableViews.ItemLabel(catalog, (int)row.ItemIndex)} x{row.Quantity:N0}"
            + (characterClass >= 0 ? $" ({InvestmentTableViews.ClassName(characterClass)})" : "");
}
