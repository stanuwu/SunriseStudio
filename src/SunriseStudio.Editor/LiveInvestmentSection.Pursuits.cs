using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Live;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/**
 * One held pursuit on the board: its inventory row and item, the game's status when the game reported one, and what the
 * card shows. Progress is the fraction of objectives complete, or IconGrid.NoProgress when no strip is drawn.
 */
internal sealed record HeldPursuitCard(LiveHeld Row, InvestmentItem? Item, LiveHeldPursuit? Status, bool Bounty, float Progress,
    bool Unknown, bool Complete, bool Tracked, string Tip);

/** The tabs of the pursuit board: the held quests, the held bounties, and the shown quest's chain. */
internal enum PursuitBoardTab { Quests, Bounties, Chain }

internal sealed partial class LiveInvestmentSection
{
    // The in-game Quests screen lays out its slots two across and five down, and the Bounties screen four across and seven down.
    private const int QuestColumns = 2, QuestRows = 5, BountyColumns = 4, BountyRows = 7;
    // The padding around a card's icon and text, and the height of the progress strip along its bottom, at UI scale 1.
    private const float CardPad = 6, CardStrip = 3;
    // The outline width of a chosen card at UI scale 1. Other cards keep a one-pixel frame.
    private const float CardOutline = 2;
    // The inspector's share of the subtab width and its bounds at UI scale 1.
    private const float PursuitInspectorShare = .4f, PursuitInspectorMinimum = 260, PursuitInspectorMaximum = 460;
    // Below this subtab width at UI scale 1 the inspector moves under the board, which keeps this share of the height.
    private const float PursuitNarrowWidth = 640, PursuitBoardShare = .45f;
    private readonly PursuitInspector inspector = new();
    private readonly ItemDetailRead pursuitDetail = new();
    private readonly QuestChainView pursuitChain = new();
    private string pursuitSearch = "";
    // The inputs the board's cards were built from. The cards are rebuilt only when one of them changes.
    private (LiveInventorySnapshot? Inventory, LivePursuitSnapshot? Pursuits,
        InvestmentCatalog? Catalog, InvestmentTables? Tables,
        string Search) pursuitKey;
    private (HeldPursuitCard[] Quests, HeldPursuitCard[] Bounties) pursuitCards = ([], []);
    private HeldPursuitCard? pursuitContext;
    /** The tab the next board draw selects: one a row names, or the chosen card's tab when it is revealed. */
    private PursuitBoardTab? pursuitTabRequest;
    // The board tab last drawn, which the subtab's row records.
    private PursuitBoardTab pursuitTab;
    // The pursuit the inspector shows while the chosen held row is not a pursuit, such as one Open in Live or the chain chose.
    private uint? chosenPursuit;
    // A held copy that history asked for. With pendingFirstCopy, the next inventory snapshot chooses chosenPursuit's first copy.
    private HeldRow? pendingPursuit;
    private bool pendingFirstCopy;
    // Whether the next board draw opens the tab of the chosen card.
    private bool revealPursuit;

    /** The pursuit the inspector shows: the chosen held row when it is in the Pursuits bucket, otherwise chosenPursuit. */
    private uint? ShownPursuit => workspace.LiveInvestment.Selected is { BucketId: InvestmentPursuits.PursuitBucket } row ? row.DefinitionHash : chosenPursuit;

    /**
     * "quests", "bounties" or "chain", then "/held:<scope>:<instance>:<hash>:<bucket>" for a chosen held copy or "/item:<hash>"
     * for one not held.
     */
    private string PursuitsRow
    {
        get
        {
            string tab = pursuitTab switch { PursuitBoardTab.Bounties => "bounties",
                PursuitBoardTab.Chain => "chain", _ => "quests" };
            return workspace.LiveInvestment.Selected is { BucketId: InvestmentPursuits.PursuitBucket } row ? $"{tab}/{HeldKey(row)}"
                : chosenPursuit is { } hash ? $"{tab}/item:{hash:X8}" : tab;
        }
    }

    /**
     * Accepts "quests", "bounties" or "chain", optionally followed by a held copy or an item after a slash, or "item:<hash>"
     * alone as Open in Live sends it. A tab alone shows the board with nothing chosen.
     */
    private void ShowPursuitsRow(string row)
    {
        string[] parts = row.Split('/');
        bool tab = parts[0] is "quests" or "bounties" or "chain";
        if (tab) pursuitTabRequest = parts[0] switch { "quests" => PursuitBoardTab.Quests,
            "bounties" => PursuitBoardTab.Bounties, _ => PursuitBoardTab.Chain };
        string chosen = parts.Length > 1 ? parts[1] : parts[0];
        if (ParseHeld(chosen) is { } held) { pendingPursuit = held; pendingFirstCopy = false; chosenPursuit = held.Hash; }
        else if (chosen.StartsWith("item:", StringComparison.Ordinal)
            && uint.TryParse(chosen.AsSpan(5), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash))
        { pendingPursuit = null; pendingFirstCopy = true; chosenPursuit = hash; }
        else if (tab && parts.Length == 1) { pendingPursuit = null; pendingFirstCopy = false; chosenPursuit = null; }
        else return;
        // The requested pursuit replaces another chosen held pursuit. Chosen rows in other buckets stay for Items and Inventory.
        var live = workspace.LiveInvestment;
        if (live.Selected is { BucketId: InvestmentPursuits.PursuitBucket } other
            && other.DefinitionHash != chosenPursuit) live.Selected = null;
        revealPursuit = chosenPursuit is not null;
    }

    /** Draws the search box, then the board of held quests and bounties with the inspector beside it, or under it when narrow. */
    private void DrawPursuits(LiveSectionContext context, InvestmentCatalog? catalog, InvestmentTables? tables)
    {
        var live = workspace.LiveInvestment;
        var missing = LiveGate.Missing(live, live.Inventory, LiveNeed.Inventory);
        var inventory = missing is null ? live.Inventory.Value : null;
        var index = inventory is null ? null : live.Held;
        TakePendingPursuit(live, inventory, index?.All ?? []);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##find-pursuit", "Find pursuit", ref pursuitSearch, 256);
        if (tables is null || catalog is null)
        {
            loader.Waiting(workspace, catalog is null ? "Reading items" : "Reading reward tables");
            return;
        }
        var held = index?.ByBucket.GetValueOrDefault(InvestmentPursuits.PursuitBucket) ?? [];
        var words = BrowserRowFilter.Words(pursuitSearch);
        var rows = words.Length == 0 ? held
            : held.Where(row => words.All(word => HeldName(catalog, row.DefinitionHash).Contains(word,
                StringComparison.OrdinalIgnoreCase))).ToArray();
        var size = ImGui.GetContentRegionAvail();
        bool narrow = size.X < PursuitNarrowWidth * scale;
        float detail = narrow ? size.X
            : Math.Clamp(size.X * PursuitInspectorShare, PursuitInspectorMinimum * scale, PursuitInspectorMaximum * scale);
        ImGui.BeginChild("pursuit-board",
            narrow ? new(size.X, size.Y * PursuitBoardShare) : new(size.X - detail - ImGui.GetStyle().ItemSpacing.X, 0),
            ImGuiChildFlags.None);
        DrawPursuitBoard(context, live, catalog, tables, inventory, rows, held, missing);
        ImGui.EndChild();
        if (!narrow) ImGui.SameLine();
        ImGui.BeginChild("pursuit-inspector", Vector2.Zero, ImGuiChildFlags.Borders);
        DrawPursuitInspector(context, catalog, tables);
        ImGui.EndChild();
    }

    /**
     * Once an inventory snapshot is here, chooses the held copy that history or Open in Live asked for. When the character
     * holds no such copy, chosenPursuit alone stays shown.
     */
    private void TakePendingPursuit(LiveInvestmentSession live, LiveInventorySnapshot? inventory, LiveHeld[] all)
    {
        if (pendingPursuit is null && !pendingFirstCopy || inventory is null) return;
        var found = pendingPursuit is { } wanted
            ? all.FirstOrDefault(wanted.Matches)
            : all.FirstOrDefault(row => row.BucketId == InvestmentPursuits.PursuitBucket && row.DefinitionHash == chosenPursuit);
        pendingPursuit = null; pendingFirstCopy = false;
        if (found is not null) { live.Selected = found; revealPursuit = true; }
    }

    /** Draws the shown pursuit in the inspector's live mode. Its item is read in the background, and Open shows it in Assets. */
    private void DrawPursuitInspector(LiveSectionContext context, InvestmentCatalog catalog, InvestmentTables tables)
    {
        if (ShownPursuit is not { } hash) { ImGui.TextDisabled("Choose a quest or bounty."); return; }
        if (catalog.FindByHash(hash) is not { } item) { ImGui.TextDisabled($"Item 0x{hash:X8}"); StudioUi.Tip(LiveItemActions.NotInCatalog); return; }
        if (workspace.Catalog is not { } packages) return;
        pursuitDetail.Want(catalog, packages, item.Index);
        if (pursuitDetail.Result is not { } read) { ImGui.TextDisabled("Reading item"); return; }
        if (read.Detail is not { } detail) { ImGui.TextColored(StudioTheme.Failure, "Item not read."); StudioUi.Tip(read.Error); return; }
        var page = new StudioPageContext(workspace, context.Viewport, _ => { }, ShowPage: context.ShowPage, Icons: context.Icons);
        inspector.Draw(page, catalog, tables, detail, tabbed: true,
            live: new(() => context.OpenAsset(item.Tag, PursuitPageOf(tables, item), null)));
    }

    /**
     * Draws the Pursuits bucket as a board, with quests as cards, bounties as icon cells and the bucket's free slots after
     * each tab's entries. While no inventory is read, every installed slot draws free under the line that says why.
     */
    private void DrawPursuitBoard(LiveSectionContext context, LiveInvestmentSession live,
        InvestmentCatalog catalog, InvestmentTables tables,
        LiveInventorySnapshot? inventory, LiveHeld[] rows, LiveHeld[] heldRows, (string Text, string Tip)? inventoryMissing)
    {
        int held = heldRows.Length;
        var progressMissing = inventoryMissing is null ? LiveGate.Missing(live, live.Pursuits, LiveNeed.Pursuits) : null;
        var pursuits = inventoryMissing is null && progressMissing is null ? live.Pursuits.Value : null;
        if (pursuitKey != (inventory, pursuits, catalog, tables, pursuitSearch))
        {
            pursuitKey = (inventory, pursuits, catalog, tables, pursuitSearch);
            pursuitCards = PursuitCards(catalog, tables, rows, pursuits);
        }
        var (quests, bounties) = pursuitCards;
        // The Chain tab shows whichever pursuit is chosen, so revealing a chosen card does not switch away from it.
        if (revealPursuit && (pursuitTabRequest ?? pursuitTab) == PursuitBoardTab.Chain) revealPursuit = false;
        if (revealPursuit && live.Selected is { } chosen
            && quests.Concat(bounties).FirstOrDefault(card => card.Row.SameRow(chosen)) is { } shown)
        {
            pursuitTabRequest = shown.Bounty ? PursuitBoardTab.Bounties : PursuitBoardTab.Quests;
            revealPursuit = false;
        }
        int capacity = tables.Bucket(InvestmentPursuits.PursuitBucket) is { SlotCount: > 0 } bucket ? Math.Min(bucket.SlotCount,
            ushort.MaxValue) : 0;
        // Quests and bounties share the bucket, so each tab shows the slots neither holds. Without an inventory every slot is free.
        int free = inventory is null ? capacity : Math.Max(0, capacity - held);
        var missing = inventoryMissing ?? progressMissing;
        string Count(int count) => inventory is null ? LiveGate.NoValue : $"{count:N0}";
        var request = pursuitTabRequest;
        pursuitTabRequest = null;
        if (!ImGui.BeginTabBar("##pursuit-board")) return;
        if (StudioForm.BeginEditorTab($"Quests ({Count(quests.Length)})###quests", request == PursuitBoardTab.Quests))
        {
            pursuitTab = PursuitBoardTab.Quests;
            BoardLine(missing, quests.Length, held, capacity, "No quests held.");
            DrawQuestCards(context, live, catalog, tables, quests, free);
            ImGui.EndTabItem();
        }
        if (StudioForm.BeginEditorTab($"Bounties ({Count(bounties.Length)})###bounties", request == PursuitBoardTab.Bounties))
        {
            pursuitTab = PursuitBoardTab.Bounties;
            BoardLine(missing, bounties.Length, held, capacity, "No bounties held.");
            DrawBountyCells(context, live, catalog, tables, bounties, free);
            ImGui.EndTabItem();
        }
        if (StudioForm.BeginEditorTab("Chain###chain", request == PursuitBoardTab.Chain))
        {
            pursuitTab = PursuitBoardTab.Chain;
            DrawPursuitChain(context, live, catalog, tables, inventory is null ? null : heldRows, inventoryMissing);
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    /**
     * Draws the shown quest's chain with the steps the character holds tinted. A click shows a step in the inspector, held
     * or not, and Open shows it on the Quest page. Without an inventory the chain still draws under the reason, with nothing
     * held.
     * @param held The held pursuits, or null while the game does not say.
     */
    private void DrawPursuitChain(LiveSectionContext context, LiveInvestmentSession live,
        InvestmentCatalog catalog, InvestmentTables tables,
        LiveHeld[]? held, (string Text, string Tip)? missing)
    {
        var item = ShownPursuit is { } hash ? catalog.FindByHash(hash) : null;
        bool quest = item is not null && tables.PursuitShape(item.Index) == InvestmentPursuitShape.Quest
            && workspace.Catalog is not null;
        int owner = quest ? QuestChainView.OwnerOf(tables, item!.Index) : -1;
        var known = pursuitDetail.Result?.Detail?.Pursuit?.Chain is { } read && read.OwnerItem == owner ? read : null;
        var steps = quest ? pursuitChain.Want(catalog, workspace.Catalog!, owner, known) : null;
        var holds = held is null || steps is null ? null
            : held.Select(row => catalog.FindByHash(row.DefinitionHash)?.Index ?? -1).Where(index => steps.Steps.Any(step => step.ItemIndex == index)).ToHashSet();
        if (missing is { } line) LiveGate.Line(line);
        else if (!quest) ImGui.TextDisabled(item is null ? "No quest chosen" : "Not in a quest chain");
        else if (steps is not null) ImGui.TextDisabled($"{holds!.Count:N0} of {InvestmentTableViews.Count(steps.Steps.Count, "step")} held");
        else ImGui.TextDisabled("Reading quest.");
        var built = quest ? pursuitChain.Model(catalog, tables, steps, owner, item!.Index, holds) : null;
        pursuitChain.DrawTools("pursuit-chain", owner, built);
        ImGui.BeginChild("pursuit-chain", Vector2.Zero, ImGuiChildFlags.Borders,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (quest)
        {
            // The selection follows the shown pursuit, and a click on another step shows that step.
            string shown = QuestChainView.KeyOf(steps, item!.Index), selected = shown;
            pursuitChain.Draw(catalog, steps, built, owner, icons, ref selected,
                step => context.OpenAsset(catalog.Items[step.ItemIndex].Tag, QuestPage.PageId, QuestPage.StepRow(step.Member)));
            if (selected != shown && steps is not null && QuestChainView.Member(steps, selected) is { } chosen
                && chosen.ItemIndex < catalog.Items.Count)
                ChooseChainStep(live, catalog.Items[chosen.ItemIndex], held);
        }
        else ImGui.TextDisabled(item is null ? "Choose a quest on Quests to see its chain here." : "This pursuit is not a quest step.");
        ImGui.EndChild();
        pursuitChain.DrawTooltip(built);
    }

    /** Shows a step chosen on the chain: its held copy when the character holds one, otherwise the step itself. */
    internal void ChooseChainStep(LiveInvestmentSession live, InvestmentItem step, LiveHeld[]? held)
    {
        if (held?.FirstOrDefault(row => row.DefinitionHash == step.Hash) is { } copy) { ChooseHeldPursuit(copy); return; }
        if (live.Selected is { BucketId: InvestmentPursuits.PursuitBucket }) live.Selected = null;
        chosenPursuit = step.Hash;
    }

    /** One line under the tabs: why live data is missing, the bucket's fill, or that nothing of this kind is held. */
    private void BoardLine((string Text, string Tip)? missing, int shown, int held, int capacity, string none)
    {
        if (missing is { } line) { LiveGate.Line(line); return; }
        ImGui.TextDisabled(shown == 0 ? pursuitSearch.Length != 0 ? "No matching items." : none
            : capacity > 0 ? $"{held:N0} of {capacity:N0} held" : $"{held:N0} held");
    }

    /**
     * Draws quest cards two across like the in-game screen. Empty slots follow to the end of the ten-card screen the last
     * card falls on, as far as the bucket has room. Only visible card rows are drawn.
     */
    private unsafe void DrawQuestCards(LiveSectionContext context, LiveInvestmentSession live,
        InvestmentCatalog? catalog, InvestmentTables? tables,
        HeldPursuitCard[] cards, int free)
    {
        int count = cards.Length;
        int slots = ScreenSlots(count, free, QuestColumns * QuestRows);
        ImGui.BeginChild("pursuit-quests", Vector2.Zero, ImGuiChildFlags.None);
        var style = ImGui.GetStyle();
        float width = ImGui.GetContentRegionAvail().X;
        var size = new Vector2((width - style.ItemSpacing.X * (QuestColumns - 1)) / QuestColumns,
            2 * ImGui.GetFrameHeight() + 2 * ImGui.GetTextLineHeight() + CardStrip * scale);
        var spacing = style.ItemSpacing;
        var top = ImGui.GetCursorPos();
        var draw = ImGui.GetWindowDrawList();
        uint empty = ImGui.GetColorU32(ImGuiCol.FrameBg);
        int rows = (slots + QuestColumns - 1) / QuestColumns;
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(rows, size.Y + spacing.Y);
        while (clipper.Step())
            for (int row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
                for (int index = row * QuestColumns; index < Math.Min(slots, (row + 1) * QuestColumns); index++)
                {
                    ImGui.SetCursorPos(top + IconGrid.CellOrigin(index, QuestColumns, size, spacing));
                    var start = ImGui.GetCursorScreenPos();
                    if (index >= count)
                    {
                        ImGui.Dummy(size);
                        draw.AddRectFilled(start, start + size, empty, style.FrameRounding);
                        continue;
                    }
                    ImGui.PushID(index);
                    var card = cards[index];
                    bool chosen = live.Selected?.SameRow(card.Row) == true;
                    if (ImGui.Selectable("##card", chosen, ImGuiSelectableFlags.None, size)) ChooseHeldPursuit(card.Row);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(card.Tip);
                    if (ImGui.BeginPopupContextItem("quest-context"))
                    {
                        DrawHeldContext(context, live, card.Item, card.Row, PursuitPageOf(tables, card.Item));
                        ImGui.EndPopup();
                    }
                    DrawQuestCard(draw, catalog, card, start, size, chosen);
                    ImGui.PopID();
                }
        clipper.End(); clipper.Destroy();
        ImGui.EndChild();
    }

    /** Draws one card clipped to its frame: icon, name in rarity colour, two description lines, tracked pin and progress strip. */
    private void DrawQuestCard(ImDrawListPtr draw, InvestmentCatalog? catalog, HeldPursuitCard card,
        Vector2 start, Vector2 size, bool chosen)
    {
        var end = start + size;
        draw.PushClipRect(start, end, true);
        float pad = CardPad * scale, strip = CardStrip * scale, line = ImGui.GetTextLineHeight();
        float side = size.Y - 2 * pad - strip;
        var icon = start + new Vector2(pad);
        if (catalog is not null && card.Item is { } item && icons.Get(catalog, item.Index) is { } texture)
            draw.AddImage(texture.ImGuiId, icon, icon + new Vector2(side));
        else draw.AddRectFilled(icon, icon + new Vector2(side), ImGui.GetColorU32(ImGuiCol.FrameBg));
        var text = new Vector2(icon.X + side + pad, start.Y + pad);
        float textWidth = end.X - pad - text.X;
        if (card.Tracked)
            ImGuiIcons.Draw(draw, StudioIcon.Pin, new(end.X - pad - line, text.Y), line, ImGui.GetColorU32(StudioTheme.Accent));
        draw.AddText(text, HeldColour(card.Item),
            StudioUi.Fit(HeldName(catalog, card.Row.DefinitionHash), textWidth - (card.Tracked ? line + pad : 0)));
        var (first, second) = IconGrid.NameLines((card.Item?.Description ?? "").ReplaceLineEndings(" "), textWidth);
        uint muted = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        draw.AddText(text + new Vector2(0, line), muted, first);
        draw.AddText(text + new Vector2(0, 2 * line), muted, second);
        if (card.Unknown)
        {
            float mark = ImGui.CalcTextSize("?").X;
            draw.AddText(new(end.X - pad - mark, end.Y - strip - line), muted, "?");
        }
        if (card.Progress >= 0 && !card.Unknown)
        {
            float done = Math.Clamp(card.Progress, 0, 1), rounding = ImGui.GetStyle().FrameRounding;
            var stripStart = new Vector2(start.X, end.Y - strip);
            draw.AddRectFilled(stripStart, end, ImGui.GetColorU32(ImGuiCol.Border), rounding, ImDrawFlags.RoundCornersBottom);
            // A zero-width rounded rect still draws its corner, so no progress draws the track alone.
            if (done > 0)
                draw.AddRectFilled(stripStart, new(start.X + size.X * done, end.Y),
                    ImGui.GetColorU32(done >= 1 ? StudioTheme.Good : StudioTheme.Accent),
                    rounding, ImDrawFlags.RoundCornersBottom);
        }
        float outline = chosen ? CardOutline * scale : 1;
        draw.AddRect(start + new Vector2(outline / 2), end - new Vector2(outline / 2),
            chosen ? ImGui.GetColorU32(StudioTheme.Accent) : ImGui.GetColorU32(ImGuiCol.Border),
                ImGui.GetStyle().FrameRounding, ImDrawFlags.None, outline);
        draw.PopClipRect();
    }

    /**
     * The number of slots to draw: the held entries, then free slots to the end of the in-game screen the last entry falls
     * on, as far as the bucket has room. With nothing held, that is one screen.
     */
    private static int ScreenSlots(int held, int free, int perScreen) =>
        Math.Min(held + free, Math.Max(1, (held + perScreen - 1) / perScreen) * perScreen);

    /**
     * Draws bounties as square icon cells with progress and completion, four across and seven down like the in-game screen,
     * then empty cells to the end of that screen. One screen fits the board without scrolling, and names show in tooltips.
     */
    private void DrawBountyCells(LiveSectionContext context, LiveInvestmentSession live,
        InvestmentCatalog? catalog, InvestmentTables? tables,
        HeldPursuitCard[] cards, int free)
    {
        IconCell Cell(int index)
        {
            if (index >= cards.Length) return new("", 0, "", null, true, StudioIcon.Cube, "", 0, IconGrid.NoProgress,
                false, false, false, false, false);
            var card = cards[index];
            return new(HeldName(catalog, card.Row.DefinitionHash), HeldColour(card.Item), "",
                catalog is null || card.Item is null ? null : icons.Get(catalog, card.Item.Index), true, StudioIcon.Cube,
                "", 0, card.Progress, card.Unknown, false, card.Complete, live.Selected?.SameRow(card.Row) == true, false);
        }
        int chosen = IconGrid.DrawScreen("pursuit-bounties",
            ScreenSlots(cards.Length, free, BountyColumns * BountyRows), BountyColumns,
            BountyRows, Cell, scale, out int hovered, out int opened);
        if (hovered >= 0 && hovered < cards.Length) ImGui.SetTooltip(cards[hovered].Tip);
        if (chosen >= 0 && chosen < cards.Length) ChooseHeldPursuit(cards[chosen].Row);
        if (opened >= 0 && opened < cards.Length) { pursuitContext = cards[opened]; ImGui.OpenPopup("bounty-context"); }
        if (pursuitContext is { } menu && ImGui.BeginPopup("bounty-context"))
        {
            DrawHeldContext(context, live, menu.Item, menu.Row, PursuitPageOf(tables, menu.Item));
            ImGui.EndPopup();
        }
    }

    /** Makes a held pursuit chosen on the board the chosen held row, which the inspector shows. */
    internal void ChooseHeldPursuit(LiveHeld row)
    {
        workspace.LiveInvestment.Selected = row;
        chosenPursuit = row.DefinitionHash;
    }

    /** The page a held pursuit opens on. An item neither pursuit page reads opens on the item page. */
    private static string PursuitPageOf(InvestmentTables? tables, InvestmentItem? item) =>
        item is null || tables is null ? ItemPage : tables.PursuitShape(item.Index) switch
        {
            InvestmentPursuitShape.Quest => QuestPage.PageId,
            InvestmentPursuitShape.Single => PursuitPage.PageId,
            _ => ItemPage
        };

    /**
     * Builds the board's cards from the held rows of the Pursuits bucket, highest mutation serial first. The game's bounty
     * flag sorts each row into quests or bounties. A row the game has not reported, including every row while no pursuit
     * progress is read, is sorted by its installed lifetime, the rule the game applies.
     */
    internal static (HeldPursuitCard[] Quests,
        HeldPursuitCard[] Bounties) PursuitCards(InvestmentCatalog? catalog, InvestmentTables? tables,
        IEnumerable<LiveHeld> rows, LivePursuitSnapshot? pursuits)
    {
        var reported = (pursuits?.Held ?? []).DistinctBy(held => (held.Instance, held.ItemHash))
            .ToDictionary(held => (held.Instance, held.ItemHash));
        var cards = rows.OrderByDescending(row => row.MutationSerial).ThenBy(row => row.Instance)
            .Select(row => PursuitCard(catalog, tables, row, reported.GetValueOrDefault((row.Instance, row.DefinitionHash))))
            .ToArray();
        return (cards.Where(card => !card.Bounty).ToArray(), cards.Where(card => card.Bounty).ToArray());
    }

    /** Builds one card. Progress counts only objectives the game knows, and unknown progress gets a mark, not an empty strip. */
    private static HeldPursuitCard PursuitCard(InvestmentCatalog? catalog, InvestmentTables? tables,
        LiveHeld row, LiveHeldPursuit? status)
    {
        var item = catalog?.FindByHash(row.DefinitionHash);
        bool bounty = status?.Flags.HasFlag(LivePursuitFlags.Bounty) ?? (item is not null && tables?.Expires(item.Index) == true);
        bool known = status?.Flags.HasFlag(LivePursuitFlags.ObjectivesKnown) == true;
        bool complete = known && status!.Flags.HasFlag(LivePursuitFlags.ObjectivesComplete);
        int done = status?.Objectives.Count(objective => objective.Flags.HasFlag(LivePursuitObjectiveFlags.Complete)) ?? 0;
        float progress = !known ? IconGrid.NoProgress : status!.Objectives.Count == 0 ? complete ? 1 : 0 : (float)done / status.Objectives.Count;
        bool tracked = status is not null && InvestmentTableViews.Tracked(status);
        bool unknown = status is not null && !known;
        var tip = new List<string> { item?.Name ?? $"Item 0x{row.DefinitionHash:X8}" };
        if (item?.Description is { Length: > 0 } description) tip.Add(description);
        if (known) tip.Add($"{done} of {status!.Objectives.Count} objectives");
        else if (unknown) tip.Add("Progress unknown");
        if (tracked) tip.Add("Tracked");
        if (bounty) tip.Add(StudioUi.MayBeInaccurate);
        return new(row, item, status, bounty, progress, unknown, complete, tracked, string.Join("\n", tip));
    }
}
