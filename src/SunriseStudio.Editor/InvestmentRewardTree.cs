using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/**
 * One reward pool as a tree. A nested pool lists its entries only when opened, and the tree stops at a pool already on
 * the path or at a path deeper than Sunrise walks. Rows are a frame high, so item entries show their icon and nested pools
 * the pool glyph. The selected entry's facts draw under the table.
 */
internal sealed class InvestmentRewardTree
{
    private const ushort Absent = InvestmentCatalog.Absent;
    /** The pool glyph sits inside the icon's square, this share of its side in from each edge. */
    private const float PoolGlyphInset = .15f;
    private readonly List<int> path = [];
    private (int Pool, int Entry) selected = (-1, -1);

    /**
     * Why Sunrise would refuse the entry with the quantity typed in the popup, or empty. An entry grants through the same
     * resolver and placement as a pass row, so RewardRefusals decides. A draft that is not a number is left to the table edit.
     */
    internal static string QuantityRefusal(InvestmentCatalog catalog, InvestmentTables tables, InvestmentFieldPopup popup) =>
        popup.Table is { Kind: InvestmentTableFieldKind.PoolEntryQuantity } field && field.Row >= 0
            && field.Row < tables.Pools.Count
            && field.Element >= 0 && field.Element < tables.Pools[field.Row].Entries.Count && popup.Number() is { } quantity
            && tables.Pools[field.Row].Entries[field.Element] is var entry
            ? RewardRefusals.Grant(catalog, tables, entry.ItemIndex, quantity, entry.Sockets.Count != 0) : "";

    /** Forgets the selected entry when the page shows another pool. */
    public void Reset() => selected = (-1, -1);

    /**
     * Draws the pool's tree, then the selected entry's facts.
     * @param popup Opens table-owned weight and quantity edits.
     * @param icons The owner's icon cache, prepared with catalog.
     * @param id Table id, unique within the window.
     */
    public void Draw(StudioPageContext context, InvestmentCatalog catalog, InvestmentTables tables, int pool,
        InvestmentFieldPopup popup, InvestmentPendingMarks pending, InvestmentIconCache icons, string id)
    {
        if (pool < 0 || pool >= tables.Pools.Count)
        {
            ImGui.TextColored(StudioTheme.Failure, $"Pool {pool} (unreadable)");
            StudioUi.Tip("The reward pool table has no such row.");
            return;
        }
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV;
        if (!ImGui.BeginTable(id, 4, flags)) return;
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 3);
        ImGui.TableSetupColumn("Weight", ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthStretch, .8f);
        ImGui.TableSetupColumn("Notes", ImGuiTableColumnFlags.WidthStretch, 2);
        ImGui.TableHeadersRow();
        path.Clear();
        var root = tables.Pools[pool];
        ImGui.TableNextRow(); ImGui.TableNextColumn();
        ImGui.SetNextItemOpen(true, ImGuiCond.Once);
        bool open = ImGui.TreeNodeEx($"Pool {pool}##{id}-root",
            ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.FramePadding);
        StudioUi.Tip($"Pool {pool}\n0x{root.Hash:X8}");
        ImGui.TableSetColumnIndex(3);
        PoolNotes(root);
        if (open)
        {
            path.Add(pool);
            foreach (var entry in root.Entries) EntryRow(context, catalog, tables, entry, popup, pending, icons);
            path.Clear();
            ImGui.TreePop();
        }
        ImGui.EndTable();
        if (selected.Pool >= 0 && selected.Pool < tables.Pools.Count
            && selected.Entry < tables.Pools[selected.Pool].Entries.Count)
            Details(context, catalog, tables, tables.Pools[selected.Pool].Entries[selected.Entry], icons, id);
    }

    private void EntryRow(StudioPageContext context, InvestmentCatalog catalog,
        InvestmentTables tables, InvestmentRewardEntry entry,
        InvestmentFieldPopup popup, InvestmentPendingMarks pending, InvestmentIconCache icons)
    {
        ImGui.PushID(entry.Index);
        ImGui.TableNextRow(); ImGui.TableNextColumn();
        bool nested = entry.NestedPool != Absent;
        bool readable = !nested || entry.NestedPool < tables.Pools.Count;
        bool cycle = nested && path.Contains(entry.NestedPool);
        bool deep = nested && !cycle && path.Count >= InvestmentRewards.ServerTraversalDepth;
        bool expandable = nested && readable && !cycle && !deep && tables.Pools[entry.NestedPool].Entries.Count != 0;
        string item = InvestmentTableViews.ItemLabel(catalog, entry.ItemIndex);
        string label = !nested ? item : !readable ? $"Pool {entry.NestedPool} (unreadable)"
            : cycle ? $"Pool {entry.NestedPool} (cycle)" : deep ? "Too deep" : $"Pool {entry.NestedPool}";
        bool failed = !readable || cycle || deep || !nested && entry.ItemIndex != Absent
            && entry.ItemIndex >= catalog.Items.Count;
        var flags = ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.FramePadding
            | (expandable ? ImGuiTreeNodeFlags.None : ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen)
            | (selected == (entry.Pool, entry.Index) ? ImGuiTreeNodeFlags.Selected : ImGuiTreeNodeFlags.None);
        var colour = failed ? StudioTheme.Failure : !nested && entry.ItemIndex < catalog.Items.Count
            ? StudioTheme.Rarity(catalog.Items[entry.ItemIndex].Tier) : ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        var start = ImGui.GetCursorScreenPos();
        float width = ImGui.GetContentRegionAvail().X;
        // The label draws over the node's row, so its icon room is the same while the icon loads and for entries without one.
        bool open = ImGui.TreeNodeEx("##entry", flags);
        Label(icons, catalog, start, width, nested ? -1 : entry.ItemIndex, nested, label, ImGui.GetColorU32(colour));
        if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen()) selected = (entry.Pool, entry.Index);
        StudioUi.Tip(cycle ? "Contains itself: " + string.Join(" > ", path.Append(entry.NestedPool).Select(row => $"Pool {row}"))
            : deep ? $"The game refuses reward pools nested more than {InvestmentRewards.ServerTraversalDepth} deep."
            : !readable ? "The reward pool table has no such row."
            : failed ? "The item index has no such row." : $"Pool {entry.Pool}, entry {entry.Index}");
        ImGui.TableNextColumn();
        string row = $"Pool {entry.Pool}, entry {entry.Index}{InvestmentTableViews.Dot}" + (nested ? $"Pool {entry.NestedPool}" : item);
        ImGui.AlignTextToFramePadding();
        if (ImGui.SmallButton(InvestmentTableViews.Weight(entry.Weight) + "##weight")) popup.Begin(context.Workspace,
            entry.WeightField, row);
        StudioUi.Tip("Relative weight among the rows a selection draws from");
        pending.Dot(context.Workspace, entry.WeightField);
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        if (nested)
        {
            ImGui.TextDisabled("1");
            StudioUi.Tip("A nested pool always gives one pick.");
        }
        else
        {
            if (ImGui.SmallButton(entry.Quantity.ToString(CultureInfo.InvariantCulture) + "##quantity"))
                popup.Begin(context.Workspace, entry.QuantityField, row);
            pending.Dot(context.Workspace, entry.QuantityField);
        }
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        EntryNotes(tables, entry, expandable);
        if (open && expandable)
        {
            path.Add(entry.NestedPool);
            foreach (var child in tables.Pools[entry.NestedPool].Entries) EntryRow(context, catalog, tables,
                child, popup, pending, icons);
            path.RemoveAt(path.Count - 1);
            ImGui.TreePop();
        }
        ImGui.PopID();
    }

    /** Draws an entry's icon, or the pool glyph for a nested pool, then its label over the node's frame-high row. */
    private static void Label(InvestmentIconCache icons, InvestmentCatalog catalog, Vector2 row, float width,
        int item, bool pool, string label,
        uint colour)
    {
        var draw = ImGui.GetWindowDrawList();
        float side = ImGui.GetFrameHeight();
        var at = row + new Vector2(ImGui.GetTreeNodeToLabelSpacing(), 0);
        if (pool) ImGuiIcons.Draw(draw, StudioIcon.Tree, at + new Vector2(side * PoolGlyphInset), side * (1 - 2 * PoolGlyphInset),
            ImGui.GetColorU32(ImGuiCol.TextDisabled));
        else icons.Draw(draw, catalog, item, at, side);
        float text = at.X + side + ImGui.GetStyle().ItemSpacing.X;
        draw.AddText(new(text, row.Y + ImGui.GetStyle().FramePadding.Y), colour, StudioUi.Fit(label, row.X + width - text));
    }

    private static void PoolNotes(InvestmentRewardPool pool)
    {
        ImGui.AlignTextToFramePadding();
        if (pool.Error.Length == 0) { ImGui.TextDisabled(InvestmentTableViews.Count(pool.Entries.Count, "entry", "entries")); return; }
        ImGui.TextColored(StudioTheme.Failure, "Not readable");
        StudioUi.Tip(pool.Error);
    }

    /** Draws the entry's short notes joined with middle dots, each with its own tooltip. */
    private static void EntryNotes(InvestmentTables tables, InvestmentRewardEntry entry, bool expandable)
    {
        bool first = true;
        void Note(string text, string tip, bool failure = false)
        {
            if (!first) { ImGui.SameLine(0, 0); ImGui.TextDisabled(InvestmentTableViews.Dot); ImGui.SameLine(0, 0); }
            first = false;
            if (failure) ImGui.TextColored(StudioTheme.Failure, text);
            else ImGui.TextDisabled(text);
            StudioUi.Tip(tip);
        }
        if (expandable) Note(InvestmentTableViews.Count(tables.Pools[entry.NestedPool].Entries.Count, "entry", "entries"), "");
        if (!entry.Condition.IsEmpty) Note("Conditional", InvestmentTableViews.ConditionTip(tables.Unlocks, entry.Condition));
        if (entry.Modifiers.Count != 0) Note($"Weight rules ({entry.Modifiers.Count})",
            "The first rule whose condition holds replaces the weight.");
        if (entry.Sockets.Count != 0) Note($"Sockets ({entry.Sockets.Count})", "Socket overrides applied to the granted item");
        if (entry.Slot84Reference != Absent) Note($"Site {entry.Slot84Reference}", SiteTip(tables, entry));
        if (entry.Error.Length != 0) Note("Problem", entry.Error, true);
    }

    /**
     * What Sunrise does with an entry's reward site. It skips the reference when the build omits root slot 84, and otherwise
     * refuses the draw (state/rewards/reward_resolver.cpp draw, package_reward_build.cpp supplementalMissing).
     */
    internal static string SiteTip(InvestmentTables tables, InvestmentRewardEntry entry) =>
        tables.Slot84.State != InvestmentSlotState.Omitted
            ? $"References reward site {entry.Slot84Reference} in root slot 84. Sunrise refuses a draw that picks this row."
            : $"References reward site {entry.Slot84Reference} in root slot 84, which this install does not ship. Sunrise skips the reference, so "
                + (entry.ItemIndex != Absent ? "the row grants its item." : entry.NestedPool != Absent ? "the row draws its pool."
                    : "a draw that picks this row grants nothing.");

    /** The selected entry's facts: item, amounts, category, conditions, weight rules and sockets. */
    private void Details(StudioPageContext context, InvestmentCatalog catalog,
        InvestmentTables tables, InvestmentRewardEntry entry,
        InvestmentIconCache icons, string id)
    {
        var unlocks = tables.Unlocks;
        var rows = new List<(string Name, string Value, string Tip)>();
        if (entry.ItemIndex < catalog.Items.Count)
        {
            icons.Draw(catalog, entry.ItemIndex, ImGui.GetFrameHeight());
            ImGui.SameLine(); ImGui.AlignTextToFramePadding();
            ImGui.TextColored(StudioTheme.Rarity(catalog.Items[entry.ItemIndex].Tier), catalog.Items[entry.ItemIndex].Name);
            ImGui.SameLine();
            if (ImGui.SmallButton("Open item##reward-entry")) context.Workspace.Select(catalog.Items[entry.ItemIndex].Tag);
        }
        else if (entry.NestedPool != Absent) rows.Add(("Pool", $"Pool {entry.NestedPool}", "Draws one entry from this pool"));
        else rows.Add(("Item", InvestmentTableViews.ItemLabel(catalog, entry.ItemIndex), ""));
        rows.Add(("Quantity", entry.Quantity.ToString("N0", CultureInfo.InvariantCulture), ""));
        rows.Add(("Weight", InvestmentTableViews.Weight(entry.Weight),
            "Relative weight; conditions, class and earlier picks change the odds."));
        rows.Add(("Category", entry.AnyCategory ? "Any" : $"0x{entry.Category:X8}", entry.AnyCategory
            ? "Matches every selection." : "Matches selections of this category."));
        rows.Add(("Condition", entry.Condition.IsEmpty ? "None" : unlocks.Format(entry.Condition),
            entry.Condition.IsEmpty ? "" : InvestmentTableViews.ConditionTip(unlocks, entry.Condition)));
        Lines("Weight rules", entry.Modifiers.Select(modifier => modifier.ValueIndex != Absent
            ? ($"Weight value {modifier.ValueIndex} not supported", "The game refuses weights read from a value.")
            : ((modifier.Value < 0 ? "Base weight" : InvestmentTableViews.Weight(modifier.Value)) + " when " + unlocks.Format(modifier.Condition),
                InvestmentTableViews.ConditionTip(unlocks, modifier.Condition))));
        Lines("Sockets", entry.Sockets.Select(socket =>
        {
            var (text, tip) = InvestmentTableViews.SocketOverride(catalog, socket);
            return ($"Socket type {socket.SocketType}: {text}", tip);
        }));
        rows.Add(("Unread", $"+2 0x{entry.Unknown2:X4}{InvestmentTableViews.Dot}+12 0x{entry.Unknown12:X16}{InvestmentTableViews.Dot}+28 0x{entry.Unknown28:X8}",
            "Populated bytes Sunrise does not read"));
        if (entry.Error.Length != 0) rows.Add(("Problem", entry.Error, entry.Error));
        StudioUi.Facts(id + "-entry", rows);

        // Only a list's first row carries its name.
        void Lines(string name, IEnumerable<(string Value, string Tip)> values)
        {
            bool head = true;
            foreach (var (value, tip) in values) { rows.Add((head ? name : "", value, tip)); head = false; }
        }
    }
}
