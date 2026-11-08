using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Editor;

/** Every reward pool: a searchable list, the selected pool as a tree, and what in the tables uses it. */
public sealed class RewardPoolsPage : IStudioPage, IStudioRowPage, IFramePreparedPage
{
    public const string PageId = "sunrise.reward-pools";
    // A pool that history or a link asked for. The first draw after the tables are read selects it.
    private int? requested;
    private readonly InvestmentTablesLoader loader = new();
    private readonly InvestmentFieldPopup popup = new();
    private readonly InvestmentPendingMarks pending = new();
    private readonly InvestmentRewardTree tree = new();
    private InvestmentIconCache icons = InvestmentIconCache.Unprepared;
    private int selected = -1;
    private bool scrollToSelected;
    private string search = "";
    private PoolIndex? index;
    private (PoolIndex? Index, string Search) shownKey;
    private int[] shown = [];
    public string Id => PageId;
    public string Title => "Reward pools";

    /**
     * Every pool's label, the item its label names (-1 for none) and its problems, built once per table snapshot. The search
     * text is built on the first search.
     */
    private sealed class PoolIndex(InvestmentTables tables, string[] labels, int[] labelItems,
        IReadOnlyList<(int Pool, string Text)> problems)
    {
        private string[]? search;
        public InvestmentTables Tables { get; } = tables;
        public string[] Labels { get; } = labels;
        public int[] LabelItems { get; } = labelItems;
        public IReadOnlyList<(int Pool, string Text)> Problems { get; } = problems;
        public string[] Search(InvestmentCatalog catalog) => search ??= Enumerable.Range(0, Tables.Pools.Count)
            .Select(pool => SearchText(catalog, Tables, pool, Labels[pool])).ToArray();
    }

    /** The pool's label, index and hash, then the name, index, hash and description of every item it or its nested pools give. */
    internal static string SearchText(InvestmentCatalog catalog, InvestmentTables tables, int pool, string label) =>
        string.Join("\n", Members(tables,
            pool).Where(item => item < catalog.Items.Count).Distinct().Select(item => catalog.Items[item])
            .Select(item => $"{item.Name}\n{item.Index}\n{item.Hash:X8}\n{item.Description}")
            .Prepend($"{label}\n{pool}\n{tables.Pools[pool].Hash:X8}"));

    private static IEnumerable<int> Members(InvestmentTables tables, int root)
    {
        var visited = new HashSet<int> { root };
        var pending = new Stack<int>([root]);
        while (pending.TryPop(out int pool))
            foreach (var entry in tables.Pools[pool].Entries)
            {
                if (entry.ItemIndex != ushort.MaxValue) yield return entry.ItemIndex;
                if (entry.NestedPool < tables.Pools.Count && visited.Add(entry.NestedPool)) pending.Push(entry.NestedPool);
            }
    }

    public bool CanOpen(StudioDocument document) =>
        AssetCategories.IsStructured(document.Entry) && document.Entry.Reference == RewardPoolTable.Class;

    public string? Row => requested is { } wanted ? RowOf(wanted) : selected >= 0 ? RowOf(selected) : null;

    public void ShowRow(string row)
    {
        if (row.Split(':') is ["pool", var text]
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int pool)) requested = pool;
    }

    /** The row id that history and links use for a pool: "pool:<index>". */
    internal static string RowOf(int pool) => string.Create(CultureInfo.InvariantCulture, $"pool:{pool}");

    public void PrepareFrame(StudioWorkspace workspace, Dx11Renderer renderer)
    {
        popup.Invalidate(workspace);
    }

    public void Draw(StudioPageContext context)
    {
        var workspace = context.Workspace;
        icons = InvestmentIconCache.Of(context);
        if (!loader.Ready(workspace, out var catalog, out var tables)) return;
        if (workspace.Document?.Entry.TagHash != tables.TableTag(InvestmentRootSlots.Pools))
        {
            ImGui.TextDisabled("Not the reward pool table");
            StudioUi.Tip("The investment root names another reward pool table.");
            return;
        }
        var pools = Index(catalog, tables);
        if (requested is { } wanted && wanted < tables.Pools.Count && wanted != selected) { selected = wanted; scrollToSelected = true; tree.Reset(); }
        requested = null;
        ImGui.TextUnformatted("Reward pools");
        ImGui.SameLine();
        ImGui.TextDisabled($"{tables.Pools.Count:N0} pools{InvestmentTableViews.Dot}{tables.Pools.Sum(pool => pool.Entries.Count):N0} entries");
        StudioUi.Tip($"Longest nesting: {tables.RewardGraph.MaximumDepth}");
        if (InvestmentTableViews.Problems("pool-problems", pools.Problems, selected) is var problem and >= 0)
        { selected = problem; scrollToSelected = true; tree.Reset(); }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##find-pool", "Find pool, item or hash", ref search, 256);
        var available = ImGui.GetContentRegionAvail();
        float listWidth = Math.Clamp(available.X * .35f,
            Math.Min(available.X, ImGui.GetFontSize() * 14), ImGui.GetFontSize() * 26);
        ImGui.BeginChild("pool-list", new(listWidth, 0), ImGuiChildFlags.Borders);
        DrawList(catalog, pools);
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("pool-detail", Vector2.Zero, ImGuiChildFlags.None);
        if (selected >= 0 && selected < tables.Pools.Count) DrawPool(context, catalog, pools, selected);
        else ImGui.TextDisabled("Choose a pool");
        ImGui.EndChild();
        popup.Draw(workspace, refusal: _ => InvestmentRewardTree.QuantityRefusal(catalog, tables, popup));
    }

    private PoolIndex Index(InvestmentCatalog catalog, InvestmentTables tables)
    {
        if (index is { } cached && ReferenceEquals(cached.Tables, tables)) return cached;
        var named = Enumerable.Range(0, tables.Pools.Count).Select(pool => Labelled(catalog, tables, pool)).ToArray();
        var problems = new List<(int, string)>();
        foreach (var pool in tables.Pools)
        {
            if (pool.Error.Length != 0) problems.Add((pool.Index, $"Pool {pool.Index}: {pool.Error}"));
            foreach (var entry in pool.Entries.Where(entry => entry.Error.Length != 0))
                problems.Add((pool.Index, $"Pool {pool.Index}, entry {entry.Index}: {entry.Error}"));
            if (tables.RewardGraph.Depths[pool.Index] > InvestmentRewards.ServerTraversalDepth)
                problems.Add((pool.Index, $"Pool {pool.Index} nests more than {InvestmentRewards.ServerTraversalDepth} deep."));
        }
        foreach (var cycle in tables.RewardGraph.Cycles.Where(cycle => cycle.Count != 0))
            problems.Add((cycle[0],
                "Contains itself: " + string.Join(" > ", cycle.Append(cycle[0]).Select(pool => $"Pool {pool}"))));
        return index = new(tables, named.Select(row => row.Text).ToArray(), named.Select(row => row.Item).ToArray(), problems);
    }

    /** The first named item that opens the pool, else up to three of its named items, else its index. */
    internal static string Label(InvestmentCatalog catalog, InvestmentTables tables,
        int pool) => Labelled(catalog, tables, pool).Text;

    /** The label and the first item it names, whose icon the list shows. The item is -1 when the label names none. */
    private static (string Text, int Item) Labelled(InvestmentCatalog catalog, InvestmentTables tables, int pool)
    {
        var sources = tables.RewardGraph.WrappersOf(pool).Where(item => catalog.Items[item].Named).ToArray();
        if (sources.Length != 0) return (catalog.Items[sources[0]].Name + (sources.Length > 1 ? $" (+{sources.Length - 1})" : ""),
            sources[0]);
        var members = tables.Pools[pool].Entries.Where(entry => entry.ItemIndex < catalog.Items.Count
            && catalog.Items[entry.ItemIndex].Named)
            .Select(entry => (int)entry.ItemIndex).DistinctBy(item => catalog.Items[item].Name).Take(3).ToArray();
        return members.Length != 0 ? ("Contains: " + string.Join(", ", members.Select(item => catalog.Items[item].Name)),
            members[0]) : ($"Pool {pool}", -1);
    }

    private unsafe void DrawList(InvestmentCatalog catalog, PoolIndex pools)
    {
        if (!ReferenceEquals(shownKey.Index, pools) || shownKey.Search != search)
        {
            shownKey = (pools, search);
            shown = search.Length == 0 ? Enumerable.Range(0,
                pools.Labels.Length).ToArray() : Enumerable.Range(0, pools.Labels.Length)
                .Where(pool => pools.Search(catalog)[pool].Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        if (shown.Length == 0) { ImGui.TextDisabled("No pool matches"); return; }
        float rowHeight = ImGui.GetTextLineHeightWithSpacing();
        if (scrollToSelected && Array.IndexOf(shown, selected) is >= 0 and var at)
            ImGui.SetScrollY(Math.Max(0, at * rowHeight - ImGui.GetWindowHeight() / 2));
        scrollToSelected = false;
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(shown.Length, rowHeight);
        while (clipper.Step())
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                int pool = shown[i];
                if (IconList.Line($"##pool{pool}", icons, catalog, pools.LabelItems[pool], pools.Labels[pool],
                    ImGui.GetColorU32(ImGuiCol.Text),
                        pool == selected)) { selected = pool; tree.Reset(); }
                StudioUi.Tip($"{pools.Labels[pool]}\nPool {pool} / 0x{pools.Tables.Pools[pool].Hash:X8}");
            }
        clipper.End(); clipper.Destroy();
    }

    /** Draws the pool tree, then the items, pool entries and season pass rows that reach the pool. */
    private void DrawPool(StudioPageContext context, InvestmentCatalog catalog, PoolIndex pools, int pool)
    {
        var tables = pools.Tables;
        var row = tables.Pools[pool];
        ImGui.TextUnformatted($"Pool {pool}{InvestmentTableViews.Dot}0x{row.Hash:X8}{InvestmentTableViews.Dot}{InvestmentTableViews.Count(row.Entries.Count, "entry", "entries")}");
        StudioUi.Help("Extracted: what opening each wrapping item can give. "
            + "Weights are relative. Conditions, class and earlier picks change the odds, so Studio shows no chances.");
        tree.Draw(context, catalog, tables, pool, popup, pending, icons, "pool-tree");
        StudioUi.Heading("USED BY",
            "Only items that wrap a pool are known to open it; activity reward mappings are not in this install.");
        var workspace = context.Workspace;
        float size = ImGui.GetFrameHeight();
        bool any = false;
        foreach (int item in tables.RewardGraph.WrappersOf(pool))
        {
            any = true;
            var source = catalog.Items[item];
            ImGui.PushID(item);
            icons.Draw(catalog, item, size);
            ImGui.SameLine(); ImGui.AlignTextToFramePadding();
            ImGui.TextColored(StudioTheme.Rarity(source.Tier), source.Name);
            ImGui.SameLine(); ImGui.TextDisabled(tables.OpensOnAcquisition(item) ? "Opens when acquired" : "Kept as an item when acquired");
            if (tables.AcquiredFlagSlot(item) is var flag and not ushort.MaxValue)
            {
                var label = tables.Unlocks.FlagLabel(flag);
                ImGui.SameLine(0, 0); ImGui.TextDisabled(InvestmentTableViews.Dot + "acquired flag " + label.Text);
                StudioUi.Tip(label.Detail);
            }
            ImGui.SameLine();
            if (ImGui.Button("Open")) workspace.Select(source.Tag);
            ImGui.PopID();
        }
        foreach (var (parent, entry) in tables.RewardGraph.ParentsOf(pool))
        {
            any = true;
            ImGui.PushID($"parent{parent}-{entry}");
            ImGui.AlignTextToFramePadding(); ImGui.TextUnformatted($"{pools.Labels[parent]}, entry {entry}");
            StudioUi.Tip($"Pool {parent} draws this pool as one pick.");
            ImGui.SameLine();
            if (ImGui.Button("Open")) context.ShowPage?.Invoke(PageId, RowOf(parent));
            ImGui.PopID();
        }
        uint passTable = tables.TableTag(InvestmentRootSlots.Progressions);
        foreach (var (progression, index) in tables.PassRowsOfPool(pool))
        {
            any = true;
            var passRow = tables.Progressions[progression].Rewards[index];
            ImGui.PushID($"pass{progression}-{index}");
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"Season pass rank {passRow.Rank.ToString(CultureInfo.InvariantCulture)}{InvestmentTableViews.Dot}Progression {progression}");
            StudioUi.Tip($"Row {index}: {InvestmentTableViews.ItemLabel(catalog, (int)passRow.ItemIndex)}");
            ImGui.SameLine();
            if (InvestmentTableViews.Usable(passTable) && ImGui.Button("Open"))
            {
                workspace.Open(passTable);
                context.ShowPage?.Invoke(SeasonPassPage.PageId, SeasonPassPage.RowOf(progression, index));
            }
            ImGui.PopID();
        }
        if (!any) ImGui.TextDisabled("No known source in the client data");
    }
}
