using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;

namespace SunriseStudio.Editor;

internal sealed partial class LiveInvestmentSection
{
    // The detail column's share of the subtab and its bounds at UI scale 1.
    private const float DetailShare = .34f, DetailMinimum = 260, DetailMaximum = 380;
    // The detail header's icon side at UI scale 1.
    private const float HeaderIcon = 72;
    // The shared item catalog view. A click chooses the item this subtab acts on and does not open it in Assets.
    private readonly ItemCatalogView itemList = new();
    // The chosen item's definition hash, which history records as "item:<hash>".
    private uint? chosenItem;

    private string? ItemsRow => chosenItem is { } hash ? $"item:{hash:X8}" : null;

    private void ShowItemsRow(string row)
    {
        if (row.StartsWith("item:", StringComparison.Ordinal)
            && uint.TryParse(row.AsSpan(5), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash))
        { chosenItem = hash; itemList.Reset(); }
    }

    /** The item catalog with what the character holds, and beside it the chosen item's held rows and actions. */
    private void DrawItems(LiveSectionContext context, InvestmentCatalog? catalog, InvestmentTables? tables)
    {
        if (catalog is null) { loader.Waiting(workspace, "Reading items"); return; }
        var options = new ItemCatalogOptions(Held: LiveGate.Held(workspace.LiveInvestment));
        float toggle = ImGui.GetFrameHeight();
        itemList.DrawBar(workspace, catalog, options, toggle + ImGui.GetStyle().ItemSpacing.X);
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - toggle));
        if (IconList.GridToggle("live-items-grid", context.Grid, "")) context.SetGrid(!context.Grid);
        var chosen = chosenItem is { } hash ? catalog.FindByHash(hash) : null;
        if (chosen is not null) itemList.Chosen = chosen.Index;
        var available = ImGui.GetContentRegionAvail();
        float detail = Math.Clamp(available.X * DetailShare, DetailMinimum * scale, DetailMaximum * scale);
        ImGui.BeginChild("live-item-list",
            new(available.X - detail - ImGui.GetStyle().ItemSpacing.X, 0), ImGuiChildFlags.Borders);
        if (itemList.DrawList("live-item-rows", workspace, catalog, icons, scale, context.Grid,
            Vector2.Zero, options) is { } clicked)
            chosenItem = clicked.Hash;
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("live-item-detail", Vector2.Zero, ImGuiChildFlags.None);
        if (chosen is not null) DrawItemDetail(context, catalog, tables, chosen);
        else ImGui.TextDisabled("Choose an item to see what the character holds and grant it.");
        ImGui.EndChild();
    }

    /** Draws the icon, name and Open button with the rarity and type under them, then the description, held rows and actions. */
    private void DrawItemDetail(LiveSectionContext context, InvestmentCatalog catalog,
        InvestmentTables? tables, InvestmentItem item)
    {
        icons.Draw(catalog, item.Index, HeaderIcon * scale);
        ImGui.SameLine();
        ImGui.BeginGroup();
        float open = ImGui.CalcTextSize("Open").X + 2 * ImGui.GetStyle().FramePadding.X;
        float room = ImGui.GetContentRegionAvail().X - open - ImGui.GetStyle().ItemSpacing.X;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(StudioTheme.Rarity(item.Tier), StudioUi.Fit(item.Name, room));
        StudioUi.Tip($"{item.Name}\nItem {item.Index}\n{item.Hash:X8}");
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - open));
        if (ImGui.Button("Open")) context.OpenAsset(item.Tag, ItemPage, null);
        StudioUi.Tip("Open in Assets");
        ImGui.TextDisabled(StudioUi.Fit($"{item.Rarity}  {item.TypeName}", ImGui.GetContentRegionAvail().X));
        ImGui.EndGroup();
        if (item.Description.Length != 0) ImGui.TextWrapped(item.Description);
        ImGui.Separator();
        actions.Draw(workspace, item, tables?.OpensOnAcquisition(item.Index) == true);
    }
}
