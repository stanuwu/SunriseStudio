using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Live;
using ItemKinds = SunriseStudio.Packages.InvestmentItemKinds;

namespace SunriseStudio.Editor;

/** One item's held rows and its Grant, Set quantity and Remove actions with their popups, shared by Live's Items and Inventory. */
internal sealed class LiveItemActions
{
    /** Why a held row has no actions: the installation's catalog does not list its item. */
    internal const string NotInCatalog = "Not in this installation's catalog.";
    private const string QuantityTitle = "Set quantity", RemoveTitle = "Remove item";
    private const string Choose = "Choose a held row above.";
    private const string Dot = InvestmentTableViews.Dot;
    /** The widest grant quantity the field shows: a grant carries a 32-bit count. */
    private const string WidestGrant = "2147483647";
    private uint grantItem;
    private int grantQuantity = 1;
    private HeldDraft? heldDraft;
    private string openHeldPopup = "";

    /** A popup's target as it was when the popup opened. The popup applies only while the game's row still matches it. */
    private sealed class HeldDraft(LiveInvestmentContext context, LiveHeld row, InvestmentItem item)
    {
        public LiveInvestmentContext Context { get; } = context;
        public LiveHeld Row { get; } = row;
        public InvestmentItem Item { get; } = item;
        public int Quantity { get; set; } = row.Quantity;
    }

    /**
     * Draws what the selected character holds of this item and the actions on it, in the same layout with or without a game.
     * @param opensOnAcquisition True when the game picks the item's rewards as it applies a grant.
     */
    public void Draw(StudioWorkspace workspace, InvestmentItem item, bool opensOnAcquisition)
    {
        var live = workspace.LiveInvestment;
        var missing = LiveGate.Missing(live, live.Inventory, LiveNeed.Inventory);
        var rows = missing is null ? live.Held?.Of(item.Hash) ?? [] : [];
        DrawHeld(live, item, rows, missing);
        DrawGrant(workspace, item, opensOnAcquisition);
        DrawSelected(workspace, item, live.Selected is { } chosen ? rows.FirstOrDefault(chosen.SameRow) : null, missing);
    }

    /** "Held 2", then one selectable line per held row. */
    private static void DrawHeld(LiveInvestmentSession live, InvestmentItem item, LiveHeld[] rows,
        (string Text, string Tip)? missing)
    {
        ImGui.TextDisabled("Held"); ImGui.SameLine();
        if (missing is { } absent) { LiveGate.Line(absent); return; }
        if (rows.Length == 0) { ImGui.TextUnformatted("Not held"); return; }
        ImGui.TextUnformatted($"{rows.Sum(row => (long)row.Quantity):N0}");
        StudioUi.Tip("Choose a row to change its quantity or remove it.");
        ImGui.Indent();
        foreach (var row in rows)
        {
            // Two stacks of one item differ only in their bucket.
            ImGui.PushID($"{LiveAction.HeldKey(row)} {row.Scope} {row.BucketId}");
            string line = HeldLine(row);
            if (ImGui.Selectable(line, live.Selected?.SameRow(row) == true, ImGuiSelectableFlags.None, ImGui.CalcTextSize(line)))
                live.Selected = row;
            StudioUi.Tip(HeldTip(item.Name, row));
            ImGui.PopID();
        }
        ImGui.Unindent();
    }

    /** Draws the grant quantity and the Grant button. The game grants a larger quantity one instance or stack at a time. */
    private void DrawGrant(StudioWorkspace workspace, InvestmentItem item, bool opensOnAcquisition)
    {
        var live = workspace.LiveInvestment;
        if (grantItem != item.Tag) { grantItem = item.Tag; grantQuantity = 1; }
        string key = LiveAction.ItemKey(item.Hash);
        string blocked = LiveGate.Reason(workspace, key);
        ImGui.AlignTextToFramePadding(); ImGui.TextDisabled("Quantity"); ImGui.SameLine();
        ImGui.SetNextItemWidth(InvestmentTableViews.InputWidth(WidestGrant));
        ImGui.BeginDisabled(blocked.Length != 0);
        int quantity = grantQuantity;
        if (ImGui.InputInt("##grant-quantity", ref quantity)) GrantQuantity = quantity;
        ImGui.EndDisabled();
        StudioUi.Tip(blocked);
        ImGui.SameLine();
        if (LiveGate.Button("Grant", blocked,
            "Grants through the game's normal acquisition.\n" + StudioUi.MayNotWork)) Grant(live, item);
        TargetText(live);
        if (opensOnAcquisition) ImGui.TextDisabled("Rewards are picked when the game applies it.");
        LiveOperationLine.Draw(live, live.LastFor(key));
    }

    /**
     * Draws the chosen held row with Set quantity and Remove, or why no row is chosen. A row whose item the installation does
     * not list keeps its line, with its actions disabled.
     * @param open When given, the line names the item and gets an Open button that calls it.
     */
    public void DrawSelected(StudioWorkspace workspace, InvestmentItem? item, LiveHeld? row,
        (string Text, string Tip)? missing, Action? open = null)
    {
        var live = workspace.LiveInvestment;
        ImGui.TextDisabled("Selected"); ImGui.SameLine();
        if (missing is { } line) LiveGate.Line(line);
        else if (row is null) ImGui.TextDisabled(Choose);
        else
        {
            string name = item?.Name ?? $"Item 0x{row.DefinitionHash:X8}";
            ImGui.TextUnformatted((open is null ? "" : name + InvestmentTableViews.Dot) + HeldLine(row));
            StudioUi.Tip(HeldTip(name, row));
        }
        if (open is not null && row is not null)
        {
            ImGui.SameLine();
            ImGui.BeginDisabled(item is null);
            if (ImGui.SmallButton("Open")) open();
            ImGui.EndDisabled();
            StudioUi.Tip(item is null ? NotInCatalog : "Open in Assets");
        }
        var controls = Controls(workspace, item, row, missing);
        ImGui.Indent();
        if (controls.Quantity && LiveGate.Button(QuantityTitle, controls.Blocked, StudioUi.MayNotWork))
            OpenHeld(workspace, row!, item!, remove: false);
        if (controls.Quantity && controls.Remove) ImGui.SameLine();
        if (controls.Remove && LiveGate.Button("Remove", controls.Blocked,
            StudioUi.MayNotWork)) OpenHeld(workspace, row!, item!, remove: true);
        if (row is not null) LiveOperationLine.Draw(live, live.LastFor(LiveAction.HeldKey(row)));
        ImGui.Unindent();
    }

    /** Which of Set quantity and Remove a held row draws, and why both are disabled, or empty when they can run. */
    internal readonly record struct HeldControls(bool Quantity, bool Remove, string Blocked);

    /**
     * The chosen row's Set quantity and Remove, decided once for the buttons and for OpenHeld. Only a stack has a quantity to
     * set, because an instance is always one item. Sunrise removes a character's instance through the dismantle path
     * (queuez_investment_push.cpp publish_item_removal) but refuses to remove an account row's instance
     * (state_account_quantity_runtime.cpp set_quantity, unsupported_removal), so that row draws no Remove.
     * @param row The chosen row, or null while none is chosen; both buttons then draw disabled.
     * @param missing The inventory's missing line, which is the reason while no row is chosen.
     */
    internal static HeldControls Controls(StudioWorkspace workspace, InvestmentItem? item, LiveHeld? row,
        (string Text, string Tip)? missing)
    {
        string key = row is null ? "" : LiveAction.HeldKey(row);
        string blocked = LiveGate.Reason(workspace, key);
        if (blocked.Length == 0 && row is null) blocked = missing?.Text ?? Choose;
        if (blocked.Length == 0 && item is null) blocked = NotInCatalog;
        if (blocked.Length == 0 && row!.Equipped) blocked = LiveInvestmentProtocol.Describe("equipped");
        bool stack = item is null || item.MaximumStack > 1 && !item.Instanced;
        bool accountInstance = row is { Scope: LiveInventoryScope.Profile, Instance: not 0 };
        return new(stack, !accountInstance, blocked);
    }

    /** The quantity Grant sends for the item drawn last, at least 1. */
    internal int GrantQuantity { get => grantQuantity; set => grantQuantity = Math.Max(1, value); }

    /** What Grant does: asks the game to grant the quantity of this item to the selected character. */
    internal LiveOperation Grant(LiveInvestmentSession live, InvestmentItem item) =>
        live.Submit(new LiveGrantItem(item.Hash, item.Name, grantQuantity));

    /**
     * What Set quantity or Remove does: opens its popup on the row as the game reported it, when the row's controls draw the
     * action and nothing blocks it. @return True when the popup opens.
     */
    internal bool OpenHeld(StudioWorkspace workspace, LiveHeld row, InvestmentItem item, bool remove)
    {
        var controls = Controls(workspace, item, row, null);
        if (controls.Blocked.Length != 0 || !(remove ? controls.Remove : controls.Quantity)) return false;
        heldDraft = new(workspace.LiveInvestment.Hello!.Context, row, item);
        openHeldPopup = remove ? RemoveTitle : QuantityTitle;
        return true;
    }

    /** The quantity the open Set quantity popup applies, from 1 to the item's maximum stack. */
    internal void SetHeldQuantity(int quantity)
    {
        if (heldDraft is { } draft) draft.Quantity = Math.Clamp(quantity, 1, Math.Max(1, draft.Item.MaximumStack));
    }

    /**
     * What the popup's Apply or Remove does: asks the game to set the row it opened on to the drafted quantity, or to remove
     * it, and closes the draft. @return The request, or null when no popup is open.
     */
    internal LiveOperation? ApplyHeld(LiveInvestmentSession live, bool remove)
    {
        if (heldDraft is not { } draft) return null;
        heldDraft = null;
        return live.Submit(new LiveSetQuantity(draft.Row, draft.Item.Name, remove ? 0 : draft.Quantity));
    }

    /** Draws the Set quantity and Remove popups, once a frame. A popup cannot apply after its row changed in the game. */
    public void DrawPopups(LiveInvestmentSession live)
    {
        if (openHeldPopup.Length != 0) { ImGui.OpenPopup(openHeldPopup); openHeldPopup = ""; }
        foreach (string title in new[] { QuantityTitle, RemoveTitle })
        {
            if (!ImGui.BeginPopupModal(title, ImGuiWindowFlags.AlwaysAutoResize)) continue;
            if (heldDraft is not { } draft || live.Hello?.Context != draft.Context)
            {
                heldDraft = null;
                ImGui.CloseCurrentPopup(); ImGui.EndPopup(); continue;
            }
            var current = live.Held?.Of(draft.Row.DefinitionHash).FirstOrDefault(draft.Row.SameRow);
            bool changed = current is null || current.Quantity != draft.Row.Quantity
                || current.MutationSerial != draft.Row.MutationSerial;
            string who = live.CharacterName;
            if (title == QuantityTitle)
            {
                ImGui.TextDisabled($"{draft.Item.Name}{Dot}serial {draft.Row.MutationSerial}{Dot}{who}");
                ImGui.TextUnformatted($"Now {draft.Row.Quantity:N0}");
                int quantity = draft.Quantity;
                ImGui.SetNextItemWidth(InvestmentTableViews.InputWidth($"{draft.Item.MaximumStack:N0}"));
                if (ImGui.InputInt("##held-quantity", ref quantity)) SetHeldQuantity(quantity);
                ImGui.SameLine(); ImGui.TextDisabled($"Maximum stack {draft.Item.MaximumStack:N0}");
            }
            else
            {
                ImGui.TextUnformatted($"Remove {draft.Item.Name} from {who}?");
                ImGui.TextDisabled($"Serial {draft.Row.MutationSerial}{Dot}x{draft.Row.Quantity:N0}{Dot}{HeldBucket(draft.Row)}");
            }
            ImGui.BeginDisabled(changed);
            if (ImGui.Button(title == QuantityTitle ? "Apply" : "Remove"))
            {
                ApplyHeld(live, remove: title == RemoveTitle);
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) { heldDraft = null; ImGui.CloseCurrentPopup(); }
            if (changed) ImGui.TextColored(StudioTheme.Caution, "This changed in the game. Reopen to edit.");
            ImGui.EndPopup();
        }
    }

    /**
     * Draws "to Warlock" after the last item, or under it when the line has no room, with the account and character ids in
     * the tooltip. Draws nothing while no character is selected.
     */
    internal static void TargetText(LiveInvestmentSession live, string preposition = "to")
    {
        if (live.Hello is not { } hello || live.Character is not { } character) return;
        string text = $"{preposition} {InvestmentTableViews.ClassName(character.CharacterClass)}";
        ImGui.SameLine();
        if (ImGui.GetContentRegionAvail().X < ImGui.CalcTextSize(text).X) ImGui.NewLine();
        ImGui.TextDisabled(text);
        StudioUi.Tip($"Account 0x{hello.Context.AccountSoid:X16}\nCharacter 0x{character.Soid:X16}");
    }

    internal static string HeldBucket(LiveHeld row) => row.BucketId != LiveInvestmentProtocol.NoBucket ? ItemKinds.BucketLabel(row.BucketId)
        : row.Scope == LiveInventoryScope.Profile ? "Account" : "No bucket";

    /** A held row as Items and Inventory describe it, such as "Kinetic · x1 · Equipped". HeldTip adds its ids and serial. */
    internal static string HeldLine(LiveHeld row) => $"{HeldBucket(row)}{Dot}x{row.Quantity:N0}" + (row.Equipped ? Dot + "Equipped" : "");

    internal static string HeldTip(string name, LiveHeld row) =>
        $"{name}\nInstance 0x{row.Instance:X16}\nSerial {row.MutationSerial}\nBucket {row.BucketId}";
}
