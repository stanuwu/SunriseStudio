using System.Globalization;
using ImGuiNET;
using SunriseStudio.Formats;

namespace SunriseStudio.Editor;

/**
 * Editing a pass row's item and quantity: the item picker, what a replacement means in game, and the edits Sunrise would
 * refuse.
 */
public sealed partial class SeasonPassPage
{
    internal const string SocketOwnerRefusal = "Sunrise refuses this claim: a row with socket overrides must grant its own item once.";
    internal const string PerkShapeRefusal = "Sunrise refuses this row: an automatic perk row needs quantity 1 and no socket overrides.";
    internal const string BecomesAutomatic = "This row becomes an automatic perk row: the game grants it at rank-up, with no claim.";
    internal const string StopsAutomatic = "This row stops being an automatic perk row and needs a claim.";
    private const string OneProgression = "The game serves one progression; edits to the others have no effect in game.";
    private readonly ItemCatalogView itemPicker = new();

    /** Why Sunrise would refuse the row after the replacement (empty when it would not), and the notes to read before saving. */
    internal sealed record ItemReplacement(string Refusal, IReadOnlyList<(string Text, bool MayNotWork)> Lines);

    /**
     * True when Sunrise grants a row of this item at rank-up with no claim: a perk-bucket item without a pool that has an
     * acquisition flag (state_progression_runtime.cpp progress_flag). Sunrise keeps the item's +0xDA flag only when it binds
     * to the account bank, so a row whose flag binds elsewhere needs a claim.
     */
    internal static bool Automatic(InvestmentCatalog catalog, InvestmentTables tables,
        int item) => item >= 0 && item < catalog.Items.Count
        && catalog.Items[item].Bucket == RewardRefusals.PerkBucket && tables.WrapperPool(item) < 0
        && RewardRefusals.AccountAcquisition(tables, item);

    /**
     * Why Sunrise refuses a row of this item, quantity and socket overrides, or empty when it accepts it. The claim checks run in
     * state_account_reward_grant_runtime.cpp resolve_pass order: the resolver's wrapper quantity, the socket owner, then the
     * placement (RewardRefusals). The rank-up check of an automatic perk row (state_progression_runtime.cpp
     * grant_progress_flags) names the row's own shape before the placement does.
     */
    internal static string RowRefusal(InvestmentCatalog catalog, InvestmentTables tables, int item, long quantity, bool sockets)
    {
        bool opens = tables.OpensOnAcquisition(item);
        if (opens && quantity != 1) return RewardRefusals.WrapperQuantity;
        if (opens && sockets) return SocketOwnerRefusal;
        if (Automatic(catalog, tables, item) && (quantity != 1 || sockets)) return PerkShapeRefusal;
        return RewardRefusals.Grant(catalog, tables, item, quantity, sockets);
    }

    /** RowRefusal for the row with its item replaced. The row keeps its quantity, sockets, conditions and claim flag. */
    internal static ItemReplacement Replacement(InvestmentCatalog catalog, InvestmentTables tables,
        InvestmentSeasonPassRow row, int item)
    {
        bool sockets = row.Sockets.Count != 0;
        bool before = Automatic(catalog, tables, (int)Math.Min(row.ItemIndex, int.MaxValue)),
            after = Automatic(catalog, tables, item);
        string refusal = RowRefusal(catalog, tables, item, row.Quantity, sockets);
        var lines = new List<(string, bool)>();
        if (row.ClaimSlot != 0) lines.Add(("The row keeps its claim flag. An account that claimed it sees the new item as claimed.", false));
        if (row.Conditions.Members.Count != 0 || row.Conditions.Error.Length != 0)
            lines.Add(("Its conditions stay: " + InvestmentTableViews.ConditionLines(tables.Unlocks, row.Conditions)[0], false));
        if (!sockets && after != before) lines.Add((after ? BecomesAutomatic : StopsAutomatic, true));
        if (item >= 0 && item < catalog.Items.Count && catalog.Items[item] is { Instanced: false, MaximumStack: > 1 } stack
            && row.Quantity > stack.MaximumStack)
            lines.Add(($"Quantity {row.Quantity:N0} is above this item's maximum stack {stack.MaximumStack:N0}; the game may refuse the claim.", true));
        return new(refusal, lines);
    }

    internal void BeginItemEdit() => itemPicker.Reset();

    /** The typed item index, or -1 when the draft names no item of this installation. */
    private int TypedItem(InvestmentCatalog catalog) => popup.Number() is { } item && item >= 0
        && item < catalog.Items.Count ? (int)item : -1;

    /** The row the popup's item or quantity draft edits, or null when it edits another field or the row is gone. */
    private InvestmentSeasonPassRow? EditedRow(InvestmentTables tables) =>
        popup.Table is { Kind: InvestmentTableFieldKind.PassRewardItem or InvestmentTableFieldKind.PassRewardQuantity } field
            && field.Row >= 0 && field.Row < tables.Progressions.Count
            && field.Element >= 0 && field.Element < tables.Progressions[field.Row].Rewards.Count
            ? tables.Progressions[field.Row].Rewards[field.Element] : null;

    /** The row the item draft edits, or null when the popup edits another field or the row is gone. */
    private InvestmentSeasonPassRow? ItemRow(InvestmentTables tables) =>
        popup.Table?.Kind == InvestmentTableFieldKind.PassRewardItem ? EditedRow(tables) : null;

    /** Draws the item catalog for the index input, with the typed item chosen. Clicking an item types its index. */
    private bool ItemInput(StudioWorkspace workspace, InvestmentCatalog catalog, InvestmentTables tables)
    {
        if (ItemRow(tables) is null) return false;
        itemPicker.Chosen = TypedItem(catalog);
        if (itemPicker.DrawPicker("pass-items", workspace, catalog, icons, default) is { } item)
            popup.Value = item.Index.ToString(CultureInfo.InvariantCulture);
        return false;
    }

    /** Lines wrap at the widest fixed sentence with its help mark, so only installed text such as a condition wraps. */
    private static float LineWidth() =>
        new[] { InvestmentFieldPopup.InstallLine, BecomesAutomatic,
            OneProgression }.Max(line => ImGui.CalcTextSize(line + " (?)").X);

    /** Draws the typed item, what replacing the row's item with it means, and the note that the game serves one progression. */
    private void ItemPreview(InvestmentCatalog catalog, InvestmentTables tables)
    {
        if (ItemRow(tables) is not { } row) return;
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + LineWidth());
        int item = TypedItem(catalog);
        if (item < 0) ImGui.TextColored(StudioTheme.Failure, $"No item {popup.Value.Trim()} in this installation.");
        else
        {
            ImGui.TextColored(StudioTheme.Rarity(catalog.Items[item].Tier), catalog.Items[item].Name);
            ImGui.SameLine(); ImGui.TextDisabled($"{catalog.Items[item].TypeName}{InvestmentTableViews.Dot}Item {item}");
            foreach (var (text, mayNotWork) in Replacement(catalog, tables, row, item).Lines)
            {
                ImGui.TextDisabled(text);
                if (mayNotWork) UnwrappedHelp();
            }
        }
        ImGui.TextDisabled(OneProgression);
        UnwrappedHelp();
        ImGui.PopTextWrapPos();
    }

    /** Draws the may-not-work mark after a line. LineWidth leaves room for it, so it never wraps. */
    private static void UnwrappedHelp()
    {
        ImGui.PushTextWrapPos(-1);
        StudioUi.Help(StudioUi.MayNotWork);
        ImGui.PopTextWrapPos();
    }

    /**
     * Why Sunrise would refuse the row after the typed edit: its item replaced, or its quantity changed for the same item.
     * A draft that is not a number is left to the table edit, which names the range.
     */
    private string FieldRefusal(InvestmentCatalog catalog, InvestmentTables tables)
    {
        // A pool entry quantity in the row's reward tree has its own refusal owner.
        if (EditedRow(tables) is not { } row) return InvestmentRewardTree.QuantityRefusal(catalog, tables, popup);
        if (popup.Table!.Kind == InvestmentTableFieldKind.PassRewardItem)
            return TypedItem(catalog) is >= 0 and var item ? Replacement(catalog, tables, row, item).Refusal : "";
        return popup.Number() is { } quantity
            ? RowRefusal(catalog, tables, (int)Math.Min(row.ItemIndex, int.MaxValue), quantity, row.Sockets.Count != 0) : "";
    }
}
