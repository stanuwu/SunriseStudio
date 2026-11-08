using System.Globalization;
using ImGuiNET;
using SunriseStudio.Formats;

namespace SunriseStudio.Editor.Creation;

public sealed partial class VendorWorkflow
{
    private sealed class CostDraft(InvestmentCost cost, int index)
    {
        public int Source = index;
        public uint? Item = cost.ItemIndex;
        public string Label = cost.Name, Quantity = cost.Quantity.ToString(CultureInfo.InvariantCulture);
    }
    private sealed class SaleDraft(InvestmentSale row)
    {
        public int Source = row.Index, Category = row.Category;
        public uint? Item = row.ItemIndex;
        public string Label = row.Name;
        public List<CostDraft> Costs = row.Costs.Select((cost, index) => new CostDraft(cost, index)).ToList();
    }
    private readonly List<SaleDraft> sales = [];
    private uint newSale;
    private string saleSearch = "";

    private IReadOnlyList<InvestmentVendorSaleDraft> Drafts()
    {
        var result = new List<InvestmentVendorSaleDraft>();
        foreach (var row in sales)
        {
            if (row.Item is not { } item || item >= workspace.Investment!.Items.Count)
                throw new InvalidDataException("Choose an item for every sale.");
            var costs = new List<InvestmentVendorCostDraft>();
            foreach (var cost in row.Costs)
            {
                if (cost.Item is not { } payment || payment >= workspace.Investment.Items.Count
                    || !uint.TryParse(cost.Quantity, NumberStyles.None, CultureInfo.InvariantCulture, out uint quantity))
                    throw new InvalidDataException("Choose each payment item and an unsigned quantity.");
                costs.Add(new(cost.Source, checked((ushort)payment), quantity));
            }
            if (costs.Count > InvestmentVendorAuthoring.MaximumCosts)
                throw new InvalidDataException("A sale supports at most four payment items in this runtime.");
            result.Add(new(row.Source, checked((ushort)item), row.Category, costs));
        }
        return result;
    }

    private CreationChoice[] items = [];
    private (int Investment, int Names) itemsKey = (-1, -1);

    /** Sale and payment candidates; the choice array is rebuilt only when investment or names change. */
    private IEnumerable<CreationChoice> SearchItems(string search)
    {
        if (workspace.Investment is not { } investment) return [];
        var key = (workspace.InvestmentRevision, workspace.NameRevision);
        if (key != itemsKey)
        {
            itemsKey = key;
            items = [.. investment.Items.Select(item => new CreationChoice(checked((uint)item.Index), item.Name, item.TypeName))];
        }
        return search.Length == 0 ? items : items.Where(choice => choice.Label.Contains(search, StringComparison.OrdinalIgnoreCase)
            || choice.Detail.Contains(search, StringComparison.OrdinalIgnoreCase)
            || choice.Value.ToString(CultureInfo.InvariantCulture) == search);
    }

    /** Sale drafts with their item, category and static prices; Add sale copies a base sale policy. */
    private void DrawStock()
    {
        if (detail is null) { ImGui.TextDisabled("Choose a base vendor"); return; }
        ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##sale-filter", "Find sale", ref saleSearch, 128);
        for (int index = 0; index < sales.Count; index++)
        {
            var row = sales[index];
            if (saleSearch.Length != 0 && !row.Label.Contains(saleSearch, StringComparison.OrdinalIgnoreCase)) continue;
            ImGui.PushID(index);
            bool expanded = ImGui.CollapsingHeader($"{row.Label}##sale");
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove")) { sales.RemoveAt(index); CreationWidgets.Reset(); ImGui.PopID(); break; }
            if (expanded)
            {
                CreationWidgets.Picker("sale-item", ref row.Item, ref row.Label, SearchItems, workspace.InvestmentRevision, 90);
                int count = BaseCategoryCount();
                var categories = new List<CreationChoice> { new(uint.MaxValue, "No category") };
                categories.AddRange(Enumerable.Range(0, count).Select(i => new CreationChoice((uint)i, $"Base category {i + 1}")));
                uint category = unchecked((uint)row.Category);
                if (CreationWidgets.Choice("Category", ref category, categories)) row.Category = unchecked((int)category);
                for (int k = 0; k < row.Costs.Count; k++)
                {
                    var cost = row.Costs[k]; ImGui.PushID(k);
                    CreationWidgets.Picker("payment", ref cost.Item, ref cost.Label, SearchItems, workspace.InvestmentRevision, 70);
                    bool conditional = cost.Source >= 0 && detail.Sales[row.Source].Costs[cost.Source].Conditional;
                    ImGui.SetNextItemWidth(170 * CreationWidgets.Scale);
                    ImGui.InputText(conditional ? "Base quantity" : "Quantity", ref cost.Quantity, 32, ImGuiInputTextFlags.CharsDecimal);
                    ImGui.SameLine();
                    bool remove = ImGui.SmallButton("Remove price");
                    ImGui.PopID();
                    if (remove) { row.Costs.RemoveAt(k); CreationWidgets.Reset(); break; }
                }
                ImGui.BeginDisabled(row.Costs.Count >= InvestmentVendorAuthoring.MaximumCosts);
                if (ImGui.SmallButton("Add static price"))
                    row.Costs.Add(new(new(0, "Choose item", 1, null!, null!, false), -1) { Item = null });
                ImGui.EndDisabled();
                if (row.Costs.Count == 0) ImGui.TextDisabled("No payment items");
            }
            ImGui.PopID();
        }
        if (detail.Sales.Count != 0)
        {
            var policies = detail.Sales.Select(row => new CreationChoice((uint)row.Index, $"{row.Index + 1}: {row.Name}")).ToArray();
            CreationWidgets.Choice("Sale policy", ref newSale, policies);
            ImGui.SameLine();
            if (ImGui.SmallButton("Add sale") && newSale < detail.Sales.Count) sales.Add(new(detail.Sales[(int)newSale]));
        }
        else ImGui.TextDisabled("This base has no sale policy to copy");
    }

    private void DrawPreparation()
    {
        if (interactionSite is not null) ImGui.TextDisabled(interaction.TagLabel);
        if (error.Length == 0 && interactionError.Length == 0 && (!TextChanged || textError.Length == 0)) return;
        if (error.Length != 0) ImGui.TextWrapped(error);
        if (interactionError.Length != 0) ImGui.TextWrapped(interactionError);
        if (TextChanged && textError.Length != 0) ImGui.TextWrapped(textError);
        if (ImGui.SmallButton("Retry preparation"))
        {
            if (error.Length != 0) loadedBase = null;
            if (interactionError.Length != 0) loadedInteraction = null;
            error = interactionError = textError = "";
        }
    }
}
