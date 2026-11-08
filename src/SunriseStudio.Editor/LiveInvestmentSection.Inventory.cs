using System.Globalization;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Live;
using SunriseStudio.Viewport;
using ItemKinds = SunriseStudio.Packages.InvestmentItemKinds;

namespace SunriseStudio.Editor;

internal sealed partial class LiveInvestmentSection
{
    private const string Dot = InvestmentTableViews.Dot;
    private const string SlotHelp = "Sunrise pairs equipment slots with buckets itself; the game reports the pairing.";
    // The inventory need, worded for the equipment slot fact: "Live shows the equipment slot once a game is attached."
    private static readonly LiveNeed EquipmentSlot = LiveNeed.Inventory with { See = "the equipment slot" };
    // Lines reserved under the held cells for the chosen row: its line, its buttons and its last action.
    private const int SelectedLines = 3;
    private readonly BucketList buckets = new();
    private readonly IconGrid heldGrid = new();
    private string inventorySearch = "";
    // The bucket and search the held rows last showed. A change scrolls them back to the top.
    private (int Bucket, string Search) heldFilter = (BucketList.AllBuckets, "");
    private LiveHeld? heldContext;
    // A held row that history asked for. The next inventory snapshot chooses it when it holds that row and drops it either way.
    private HeldRow? pendingHeld;

    /** A held row as a row key names it. Matches tells whether a row of a later snapshot is that row, as LiveHeld.SameRow does. */
    private readonly record struct HeldRow(LiveInventoryScope Scope, ulong Instance, uint Hash, byte Bucket)
    {
        public bool Matches(LiveHeld row) =>
            row.Scope == Scope && row.Instance == Instance && row.DefinitionHash == Hash && row.BucketId == Bucket;
    }

    /** "bucket:<id>" or "bucket:all", plus "/held:<scope>:<instance>:<hash>:<bucket>" while a shown held row is chosen. */
    private string InventoryRow
    {
        get
        {
            string bucket = buckets.Selected == BucketList.AllBuckets ? "bucket:all" : $"bucket:{buckets.Selected}";
            var live = workspace.LiveInvestment;
            return live.Selected is { } row && (buckets.Selected == BucketList.AllBuckets || row.BucketId == buckets.Selected)
                ? $"{bucket}/{HeldKey(row)}" : bucket;
        }
    }

    private void ShowInventoryRow(string row)
    {
        string[] parts = row.Split('/');
        if (parts[0] == "bucket:all") buckets.Select(BucketList.AllBuckets);
        else if (parts[0].StartsWith("bucket:", StringComparison.Ordinal)
            && byte.TryParse(parts[0].AsSpan(7), CultureInfo.InvariantCulture, out byte id))
            buckets.Select(id);
        if (parts.Length > 1 && ParseHeld(parts[1]) is { } held) pendingHeld = held;
    }

    /** A held row's part of a row key: "held:<scope>:<instance>:<hash>:<bucket>". */
    private static string HeldKey(LiveHeld row) =>
        string.Create(CultureInfo.InvariantCulture,
            $"held:{(int)row.Scope}:{row.Instance:X16}:{row.DefinitionHash:X8}:{row.BucketId}");

    private static HeldRow? ParseHeld(string key) =>
        key.Split(':') is ["held", var scope, var instance, var hash, var bucket]
            && int.TryParse(scope, CultureInfo.InvariantCulture, out int kind)
            && ulong.TryParse(instance, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong copy)
            && uint.TryParse(hash, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint definition)
            && byte.TryParse(bucket, CultureInfo.InvariantCulture, out byte id)
            ? new HeldRow((LiveInventoryScope)kind, copy, definition, id) : null;

    /** Draws the bar, the bucket list with held counts, then the chosen bucket's cells or every held row, then the chosen row. */
    private void DrawInventory(LiveSectionContext context, InvestmentCatalog? catalog, InvestmentTables? tables)
    {
        var live = workspace.LiveInvestment;
        var missing = LiveGate.Missing(live, live.Inventory, LiveNeed.Inventory);
        var inventory = missing is null ? live.Inventory.Value : null;
        var held = inventory is null ? null : live.Held;
        var all = held?.All ?? [];
        TakePendingHeld(live, inventory, all);
        DrawInventoryBar(context, live, inventory, all.Length, missing);
        if (tables is null || catalog is null)
        {
            loader.Waiting(workspace, catalog is null ? "Reading items" : "Reading reward tables");
            return;
        }
        var index = buckets.Index(catalog, tables);
        var others = (inventory?.Buckets ?? []).Where(row => tables.Bucket(row.BucketId) is null)
            .Select(row => (row.BucketId, (int)row.SlotCount)).OrderBy(row => row.BucketId).ToArray();
        // A chosen bucket that neither the installed table nor the game's snapshot lists falls back to all held rows.
        if (buckets.Selected is >= 0 and <= byte.MaxValue && tables.Bucket((byte)buckets.Selected) is null
            && inventory is not null && others.All(row => row.BucketId != buckets.Selected))
            buckets.Select(BucketList.AllBuckets);
        ImGui.BeginChild("inventory-buckets",
            new(BucketsPage.ListWidth(ImGui.GetContentRegionAvail().X), 0), ImGuiChildFlags.Borders);
        buckets.Draw(index, new("Held", id => LiveGate.Cell(inventory is null ? null
            : $"{(id == BucketList.AllBuckets ? all.Length : held?.ByBucket.GetValueOrDefault((byte)id)?.Length ?? 0):N0}",
                missing),
            "All held items", "Every row the character and the account hold", others));
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("inventory-bucket", System.Numerics.Vector2.Zero, ImGuiChildFlags.None);
        var words = BrowserRowFilter.Words(inventorySearch);
        LiveHeld[] Shown(LiveHeld[] rows) => words.Length == 0 ? rows
            : rows.Where(row => words.All(word => HeldName(catalog, row.DefinitionHash).Contains(word,
                StringComparison.OrdinalIgnoreCase))).ToArray();
        if (buckets.Selected == BucketList.AllBuckets)
        {
            var rows = Shown(all);
            // Held pursuit rows show their progress, which comes from the pursuit snapshot.
            if (rows.Any(row => row.BucketId == InvestmentPursuits.PursuitBucket))
                live.Want(LiveViewKind.Pursuits);
            DrawHeldList(context, live, catalog, rows, missing);
            DrawChosenHeld(context, live, catalog, rows, missing);
        }
        else
        {
            byte id = (byte)buckets.Selected;
            var inBucket = held?.ByBucket.GetValueOrDefault(id) ?? [];
            var rows = Shown(inBucket);
            int capacity = tables.Bucket(id) is { } bucket ? DrawBucketHeader(context, live, tables, bucket, inventory)
                : DrawReportedHeader(inventory, id);
            if (missing is { } line) LiveGate.Line(line);
            else ImGui.TextDisabled(inBucket.Length == 0 ? "Nothing held here." : $"{inBucket.Length:N0} of {capacity:N0} held");
            DrawHeldCells(context, live, catalog, rows, words.Length == 0 ? capacity : 0);
            DrawChosenHeld(context, live, catalog, rows, missing);
        }
        ImGui.EndChild();
    }

    /** Draws the search box across the line, then the held count and character, then the rows or grid toggle at the right. */
    private void DrawInventoryBar(LiveSectionContext context, LiveInvestmentSession live,
        LiveInventorySnapshot? inventory, int held,
        (string Text, string Tip)? missing)
    {
        string who = live.Character is { } character ? Dot + InvestmentTableViews.ClassName(character.CharacterClass) : "";
        string count = inventory is null ? missing?.Text ?? "" : $"{held:N0} held{who}";
        float toggle = ImGui.GetFrameHeight(), counted = ImGui.CalcTextSize(count).X, spacing = ImGui.GetStyle().ItemSpacing.X;
        ImGui.SetNextItemWidth(Math.Max(ItemCatalogView.MenuFonts * ImGui.GetFontSize(),
            ImGui.GetContentRegionAvail().X - counted - toggle - 2 * spacing));
        ImGui.InputTextWithHint("##find-held", "Find held item", ref inventorySearch, 256);
        ImGui.SameLine();
        if (missing is { } line) LiveGate.Line(line); else ImGui.TextDisabled(count);
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - toggle));
        if (IconList.GridToggle("live-inventory-grid", context.Grid, "")) context.SetGrid(!context.Grid);
    }

    /**
     * Draws the bucket's title with Open in Pursuits (for the Pursuits bucket) and Open in Buckets, then a line with where
     * its rows live and its equipment slot.
     * @return The bucket's capacity.
     */
    private int DrawBucketHeader(LiveSectionContext context, LiveInvestmentSession live,
        InvestmentTables tables, InvestmentBucket bucket,
        LiveInventorySnapshot? inventory)
    {
        const string open = "Open in Buckets", board = "Open in Pursuits";
        var style = ImGui.GetStyle();
        float Room(string label) => ImGui.CalcTextSize(label).X + 2 * style.FramePadding.X + style.ItemSpacing.X;
        bool pursuits = bucket.Id == InvestmentPursuits.PursuitBucket;
        BucketsPage.DrawTitle(bucket, Room(open) + (pursuits ? Room(board) : 0));
        if (pursuits)
        {
            ImGui.SameLine();
            if (ImGui.Button(board)) { Show(PursuitsView, null); revealPursuit = true; }
            StudioUi.Tip("The quests and bounties held here, with their objectives.");
        }
        ImGui.SameLine();
        if (ImGui.Button(open)) OpenInBuckets(context, tables, bucket.Id);
        DrawFactLine([.. BucketsPage.PlaceFacts(bucket), EquipmentFact(live, bucket.Id, inventory)]);
        return Math.Clamp(bucket.SlotCount, 0, ushort.MaxValue);
    }

    /** Draws the header of a bucket only the game reports. @return The game's slot count for it. */
    private static int DrawReportedHeader(LiveInventorySnapshot? inventory, byte id)
    {
        ImGui.TextUnformatted(ItemKinds.BucketLabel(id));
        ImGui.TextDisabled("Not in the installed bucket table");
        return inventory?.Buckets.FirstOrDefault(row => row.BucketId == id)?.SlotCount ?? 0;
    }

    /** Draws facts on one line as a muted name and a value with the detail on hover. A fact that does not fit starts a new line. */
    private static void DrawFactLine(IEnumerable<(string Name, string Value, string Tip)> facts)
    {
        bool first = true;
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        foreach (var (name, value, tip) in facts)
        {
            if (!first)
            {
                ImGui.SameLine();
                float width = ImGui.CalcTextSize("·").X + ImGui.CalcTextSize(name).X + ImGui.CalcTextSize(value).X + 2 * spacing;
                if (ImGui.GetContentRegionAvail().X < width) ImGui.NewLine();
                else { ImGui.TextDisabled("·"); ImGui.SameLine(); }
            }
            first = false;
            ImGui.TextDisabled(name); ImGui.SameLine(); ImGui.TextUnformatted(value);
            StudioUi.Tip(tip);
        }
    }

    /** The equipment slot the game reports for the bucket, or the missing line. Studio does not copy Sunrise's slot pairing. */
    private static (string, string, string) EquipmentFact(LiveInvestmentSession live, byte bucket,
        LiveInventorySnapshot? inventory)
    {
        if (inventory is null)
        {
            var missing = LiveGate.Missing(live, live.Inventory, EquipmentSlot);
            return ("Equipment slot", missing?.Text ?? LiveGate.NoValue,
                SlotHelp + (missing?.Tip is { Length: > 0 } tip ? "\n" + tip : ""));
        }
        var reported = inventory.Buckets.FirstOrDefault(row => row.BucketId == bucket);
        string value = reported is null ? "Not reported by the game"
            : reported.EquipmentSlot == LiveInvestmentProtocol.NoEquipmentSlot ? "None" : $"{reported.EquipmentSlot}";
        return ("Equipment slot", value, SlotHelp);
    }

    /** Draws a cell per bucket slot, or only the matches while searching, leaving room for the chosen row below. */
    private void DrawHeldCells(LiveSectionContext context, LiveInvestmentSession live, InvestmentCatalog catalog,
        LiveHeld[] rows, int capacity)
    {
        int cells = Math.Max(rows.Length, capacity);
        if (cells == 0) return;
        float width = ImGui.GetContentRegionAvail().X;
        float room = ImGui.GetContentRegionAvail().Y - SelectedLines * ImGui.GetFrameHeightWithSpacing();
        DrawHeldGrid(context, live, catalog, rows, capacity,
            Math.Min(IconGrid.Height(cells, width, scale), Math.Max(IconGrid.Height(1, width, scale), room)));
    }

    /** Draws every held row as rows or a grid, leaving room for the chosen row below. */
    private void DrawHeldList(LiveSectionContext context, LiveInvestmentSession live, InvestmentCatalog catalog, LiveHeld[] rows,
        (string Text, string Tip)? missing)
    {
        if (missing is { } line) LiveGate.Line(line);
        else if (rows.Length == 0) ImGui.TextDisabled(inventorySearch.Length != 0 ? "No matching items." : "Nothing held here.");
        float height = Math.Max(IconList.RowHeight(scale),
            ImGui.GetContentRegionAvail().Y - SelectedLines * ImGui.GetFrameHeightWithSpacing());
        if (context.Grid) DrawHeldGrid(context, live, catalog, rows, 0, height); else DrawHeldRows(context, live, catalog, rows, height);
    }

    /** Draws held rows with the icon, the name in rarity colour and the HeldDetail line. */
    private unsafe void DrawHeldRows(LiveSectionContext context, LiveInvestmentSession live, InvestmentCatalog catalog,
        LiveHeld[] rows, float height)
    {
        ImGui.BeginChild("held-list", new(0, height), ImGuiChildFlags.None);
        if (heldFilter != (buckets.Selected, inventorySearch)) { heldFilter = (buckets.Selected, inventorySearch); ImGui.SetScrollY(0); }
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(rows.Length, IconList.RowHeight(scale));
        while (clipper.Step())
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var row = rows[i];
                var item = catalog.FindByHash(row.DefinitionHash);
                ImGui.PushID(i);
                string name = HeldName(catalog, row.DefinitionHash);
                if (IconList.Row(icons, scale, live.Selected?.SameRow(row) == true, catalog, item, true, name, HeldColour(item),
                    HeldDetail(live, item, row), "", false))
                    live.Selected = row;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(HeldRowTip(item, name, row));
                if (ImGui.BeginPopupContextItem("held-row-context"))
                {
                    DrawHeldContext(context, live, item, row);
                    ImGui.EndPopup();
                }
                ImGui.PopID();
            }
        clipper.End(); clipper.Destroy();
        ImGui.EndChild();
    }

    /**
     * Draws held rows as grid cells with the icon, the name, "x3" for a quantity above one and the equipped corner. Free
     * cells follow up to capacity, which is 0 for none, and the HeldDetail line shows in the tooltip.
     */
    private void DrawHeldGrid(LiveSectionContext context, LiveInvestmentSession live, InvestmentCatalog catalog,
        LiveHeld[] rows, int capacity, float height)
    {
        if (heldFilter != (buckets.Selected, inventorySearch)) { heldFilter = (buckets.Selected, inventorySearch); heldGrid.ResetScroll(); }
        uint badge = ImGui.GetColorU32(StudioTheme.Accent);
        IconCell Cell(int index)
        {
            if (index >= rows.Length) return new("", 0, "", null, true, StudioIcon.Cube, "", 0, IconGrid.NoProgress,
                false, false, false, false, false);
            var row = rows[index];
            var item = catalog.FindByHash(row.DefinitionHash);
            return new(HeldName(catalog, row.DefinitionHash), HeldColour(item), "",
                item is null ? null : icons.Get(catalog, item.Index), true,
                StudioIcon.Cube, row.Quantity > 1 ? $"x{row.Quantity:N0}" : "", badge,
                    IconGrid.NoProgress, false, row.Equipped, false,
                live.Selected?.SameRow(row) == true, false);
        }
        int chosen = heldGrid.Draw("held-grid", Math.Max(rows.Length, capacity), Cell, scale, height,
            out int hovered, out int menu);
        if (hovered >= 0 && hovered < rows.Length)
        {
            var row = rows[hovered];
            var item = catalog.FindByHash(row.DefinitionHash);
            ImGui.SetTooltip(HeldRowTip(item, HeldName(catalog, row.DefinitionHash), row) + "\n" + HeldDetail(live, item, row));
        }
        if (chosen >= 0 && chosen < rows.Length) live.Selected = rows[chosen];
        if (menu >= 0 && menu < rows.Length) { heldContext = rows[menu]; ImGui.OpenPopup("held-context"); }
        DrawHeldMenu(context, live, catalog);
    }

    /** The context menu a grid cell opened. */
    private void DrawHeldMenu(LiveSectionContext context, LiveInvestmentSession live, InvestmentCatalog catalog)
    {
        if (heldContext is not { } menu || !ImGui.BeginPopup("held-context")) return;
        DrawHeldContext(context, live, catalog.FindByHash(menu.DefinitionHash), menu);
        ImGui.EndPopup();
    }

    /** Draws the chosen held row and its actions when it is among the shown rows. */
    private void DrawChosenHeld(LiveSectionContext context, LiveInvestmentSession live,
        InvestmentCatalog catalog, LiveHeld[] rows,
        (string Text, string Tip)? missing)
    {
        var row = live.Selected is { } chosen ? rows.FirstOrDefault(chosen.SameRow) : null;
        var item = row is null ? null : catalog.FindByHash(row.DefinitionHash);
        actions.DrawSelected(workspace, item, row, missing, () => OpenHeld(context, live, item!, row!, ItemPage));
    }

    /** Once an inventory snapshot is here, chooses the held row history asked for when the snapshot holds it. */
    private void TakePendingHeld(LiveInvestmentSession live, LiveInventorySnapshot? inventory, LiveHeld[] all)
    {
        if (pendingHeld is not { } wanted || inventory is null) return;
        pendingHeld = null;
        if (all.FirstOrDefault(wanted.Matches) is { } found)
            live.Selected = found;
    }

    /** Chooses a held row and opens its item on page. */
    private static void OpenHeld(LiveSectionContext context, LiveInvestmentSession live,
        InvestmentItem item, LiveHeld row, string page)
    {
        live.Selected = row;
        context.OpenAsset(item.Tag, page, null);
    }

    private void DrawHeldContext(LiveSectionContext context, LiveInvestmentSession live, InvestmentItem? item,
        LiveHeld row, string page = ItemPage)
    {
        if (item is not null && ImGui.MenuItem("Open in Assets")) OpenHeld(context, live, item, row, page);
        var tables = loader.Peek(workspace);
        if (ImGui.MenuItem("Open in Buckets", "", false, tables?.Root is not null && tables.Bucket(row.BucketId) is not null))
            OpenInBuckets(context, tables!, row.BucketId);
        if (ImGui.MenuItem("Copy hash")) ImGui.SetClipboardText($"{row.DefinitionHash:X8}");
    }

    /** Opens the investment root on the Buckets page with this bucket chosen. */
    private static void OpenInBuckets(LiveSectionContext context, InvestmentTables tables, byte bucket)
    {
        if (tables.Root is not { } root) return;
        context.OpenAsset(root.Tag, BucketsPage.PageId, BucketsPage.RowOf(bucket));
    }

    private static uint HeldColour(InvestmentItem? item) =>
        item is null ? ImGui.GetColorU32(ImGuiCol.TextDisabled) : ImGui.GetColorU32(StudioTheme.Rarity(item.Tier));

    private static string HeldRowTip(InvestmentItem? item, string name, LiveHeld row) => item is null
        ? $"Item 0x{row.DefinitionHash:X8}\n{LiveItemActions.NotInCatalog}"
        : LiveItemActions.HeldTip(name,
            row) + (row.BucketId == InvestmentPursuits.PursuitBucket ? "\n" + StudioUi.MayBeInaccurate : "");

    private static string HeldName(InvestmentCatalog? catalog,
        uint hash) => catalog?.FindByHash(hash)?.Name ?? $"Item 0x{hash:X8}";

    /** "Kinetic · x1 · Equipped", or "Pursuits · Quest step · 2 of 3 objectives" for a held pursuit. */
    private static string HeldDetail(LiveInvestmentSession live, InvestmentItem? item, LiveHeld row)
    {
        string bucket = LiveItemActions.HeldBucket(row), plain = LiveItemActions.HeldLine(row);
        if (row.BucketId != InvestmentPursuits.PursuitBucket || live.Pursuits.Value is not { } pursuits) return plain;
        if (pursuits.Held.FirstOrDefault(entry => entry.Instance == row.Instance
            && entry.ItemHash == row.DefinitionHash) is not { } held)
            return plain;
        string kind = held.Flags.HasFlag(LivePursuitFlags.Bounty) ? "Bounty" : item?.TypeName is { Length: > 0 } type ? type : "Quest step";
        return bucket + Dot + kind + Dot + InvestmentTableViews.PursuitStatus(held);
    }
}
