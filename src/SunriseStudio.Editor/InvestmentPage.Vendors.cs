using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Projects;
using ItemKinds = SunriseStudio.Packages.InvestmentItemKinds;

namespace SunriseStudio.Editor;

public sealed partial class InvestmentPage
{
    private InvestmentSale? costSale;
    private InvestmentCost? costRow;
    private StudioDocument? costDocument;
    private StudioProject? costProject;
    private long costOverlayRevision;
    private bool openCostEditor;
    private int costItem = -1;
    private string costQuantity = "";
    private readonly ItemCatalogView costPicker = new();

    private void InvalidateCostDraft(StudioWorkspace workspace)
    {
        if (costDocument is not null && (!ReferenceEquals(costDocument, workspace.Document)
            || !ReferenceEquals(costProject, workspace.Project) || costOverlayRevision != workspace.Catalog?.OverlayRevision))
            ClearCostDraft();
    }

    private void ClearCostDraft()
    { costDocument = null; costProject = null; costSale = null; costRow = null; openCostEditor = false; }

    private void DrawCostRow(StudioPageContext context, InvestmentSale sale, InvestmentCost cost)
    {
        if (ImGui.SmallButton($"{cost.Quantity:N0} {cost.Name}##cost")) BeginCostEditor(context, sale, cost);
        if (cost.Conditional)
        {
            ImGui.SameLine(); ImGui.TextDisabled("Conditional");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Base quantity; native expressions remain attached");
        }
        ImGui.SameLine(); ImGui.BeginDisabled(!context.Workspace.CanEdit);
        if (CostAction("duplicate", true, "Duplicate cost")) context.Workspace.Run(() => context.Workspace.DuplicateInvestmentCost(sale, cost));
        ImGui.SameLine();
        if (CostAction("remove", false, "Remove cost")) context.Workspace.Run(() => context.Workspace.RemoveInvestmentCost(sale, cost));
        ImGui.EndDisabled();
    }

    private static void DrawSaleActions(StudioPageContext context, InvestmentSale sale)
    {
        ImGui.BeginDisabled(!context.Workspace.CanEdit);
        if (CostAction("duplicate-sale", true, "Duplicate sale")) context.Workspace.Run(() => context.Workspace.DuplicateInvestmentSale(sale));
        ImGui.SameLine();
        if (CostAction("remove-sale", false, "Remove sale")) context.Workspace.Run(() => context.Workspace.RemoveInvestmentSale(sale));
        ImGui.EndDisabled();
    }

    /** Primitive icons keep these controls independent of an installed symbol font. */
    private static bool CostAction(string id, bool duplicate, string tooltip)
    {
        Vector2 point = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton(id, new(22, 20));
        var draw = ImGui.GetWindowDrawList();
        if (ImGui.IsItemHovered()) draw.AddRectFilled(point, point + new Vector2(22, 20), ImGui.GetColorU32(ImGuiCol.ButtonHovered), 3);
        uint color = ImGui.GetColorU32(ImGuiCol.Text);
        if (duplicate)
        {
            draw.AddRect(point + new Vector2(5, 4), point + new Vector2(14, 12), color);
            draw.AddRect(point + new Vector2(8, 7), point + new Vector2(17, 15), color);
        }
        else draw.AddLine(point + new Vector2(6, 10), point + new Vector2(16, 10), color, 2);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(tooltip);
        return clicked;
    }

    private void BeginCostEditor(StudioPageContext context, InvestmentSale sale, InvestmentCost cost)
    {
        costSale = sale; costRow = cost; costItem = cost.ItemIndex;
        costQuantity = cost.Quantity.ToString(CultureInfo.InvariantCulture);
        // The picker opens on the currencies for a currency cost, since most costs are one, and on every item otherwise.
        bool currency = context.Workspace.Investment is { } items && (uint)costItem < items.Items.Count
            && items.Items[costItem].Category == InvestmentCategory.Currencies;
        costPicker.Chosen = costItem; costPicker.Reset(currency ? ItemKinds.Currencies : ItemCatalogView.AllItems);
        openCostEditor = true;
        costDocument = context.Workspace.Document; costProject = context.Workspace.Project;
        costOverlayRevision = context.Workspace.Catalog!.OverlayRevision;
    }

    /** Payment edits retain the selected sale identity and wait for explicit application. */
    private void DrawCostEditor(StudioPageContext context, InvestmentCatalog catalog)
    {
        if (openCostEditor) { ImGui.OpenPopup("Edit cost"); openCostEditor = false; }
        if (!ImGui.BeginPopupModal("Edit cost", ImGuiWindowFlags.AlwaysAutoResize)) return;
        InvalidateCostDraft(context.Workspace);
        if (costSale is not { } sale || costRow is not { } row)
        { ImGui.CloseCurrentPopup(); ImGui.EndPopup(); return; }
        if (costPicker.DrawPicker("cost-items", context.Workspace, catalog,
            InvestmentIconCache.Of(context), default) is { } chosen)
            costItem = chosen.Index;
        if ((uint)costItem < catalog.Items.Count) ImGui.TextUnformatted(catalog.Items[costItem].Name);
        ImGui.SetNextItemWidth(180); ImGui.InputText(row.Conditional ? "Base quantity" : "Quantity", ref costQuantity, 10, ImGuiInputTextFlags.CharsDecimal);
        bool valid = (uint)costItem < catalog.Items.Count && uint.TryParse(costQuantity, NumberStyles.None, CultureInfo.InvariantCulture, out _);
        ImGui.BeginDisabled(!context.Workspace.CanEdit || !valid);
        if (ImGui.Button("Save delta") && SaveCostDraft(context.Workspace)) ImGui.CloseCurrentPopup();
        ImGui.EndDisabled(); ImGui.SameLine();
        if (ImGui.Button("Cancel")) { ClearCostDraft(); ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    /**
     * @param workspace Owns the selected document and project.
     * @return True after a successful save clears the draft.
     */
    private bool SaveCostDraft(StudioWorkspace workspace)
    {
        if (costSale is not { } sale || costRow is not { } row) return false;
        workspace.Run(() => workspace.EditInvestmentCost(sale, row, checked((ushort)costItem),
            uint.Parse(costQuantity, CultureInfo.InvariantCulture)));
        if (workspace.Error.Length != 0) return false;
        ClearCostDraft();
        return true;
    }
}
