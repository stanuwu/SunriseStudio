using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

public sealed partial class InvestmentPage
{
    private bool addingPerk, openAddRow;
    private string rowSearch = "";
    private int rowIndex = -1, rowValue;
    private StudioDocument? rowDocument;
    private StudioProject? rowProject;
    private long rowOverlayRevision;

    /** Draft ownership expires even when its tab or popup is not being drawn. */
    private void InvalidateDrafts(StudioWorkspace workspace)
    {
        InvalidateCostDraft(workspace);
        popup.Invalidate(workspace);
        if (rowDocument is not null && (!ReferenceEquals(rowDocument, workspace.Document)
            || !ReferenceEquals(rowProject, workspace.Project) || rowOverlayRevision != workspace.Catalog?.OverlayRevision))
            ClearRow();
    }

    private void ClearRow() { rowDocument = null; rowProject = null; openAddRow = false; }

    private void AddRowButton(StudioPageContext context, bool perk)
    {
        ImGui.BeginDisabled(!context.Workspace.CanEdit);
        if (ImGui.SmallButton(perk ? "+##perk" : "+##stat"))
            BeginRow(context, perk);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(perk ? "Add perk" : "Add stat");
        ImGui.EndDisabled();
    }

    private void BeginRow(StudioPageContext context, bool perk)
    {
        addingPerk = perk; rowIndex = -1; rowValue = 0; rowSearch = ""; openAddRow = true;
        rowDocument = context.Workspace.Document; rowProject = context.Workspace.Project;
        rowOverlayRevision = context.Workspace.Catalog!.OverlayRevision;
    }

    /** A draft is tied to one document and project overlay. */
    private unsafe void DrawAddRow(StudioPageContext context, InvestmentCatalog catalog)
    {
        string title = addingPerk ? "Add perk" : "Add stat";
        if (openAddRow) { ImGui.OpenPopup(title); openAddRow = false; }
        if (!ImGui.BeginPopupModal(title, ImGuiWindowFlags.AlwaysAutoResize)) return;
        var workspace = context.Workspace;
        if (!ReferenceEquals(rowDocument, workspace.Document) || !ReferenceEquals(rowProject, workspace.Project)
            || rowOverlayRevision != workspace.Catalog?.OverlayRevision)
        { Close(); ImGui.EndPopup(); return; }
        ImGui.SetNextItemWidth(360);
        ImGui.InputTextWithHint("##row-search", addingPerk ? "Find perk" : "Find stat", ref rowSearch, 256);
        ImGui.BeginChild("row-choices", new(360, 220), ImGuiChildFlags.Borders);
        var choices = RowChoices(context, catalog);
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(choices.Length);
        while (clipper.Step())
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++) Choice(choices[i]);
        clipper.End(); clipper.Destroy();
        ImGui.EndChild();
        if (!addingPerk)
        {
            ImGui.SetNextItemWidth(210); ImGui.InputInt("Value", ref rowValue);
            if (rowValue == 0 && ImGui.IsItemHovered()) ImGui.SetTooltip("Zero uses the stat's level curve when one exists.");
        }
        ImGui.BeginDisabled(rowIndex < 0 || !workspace.CanEdit);
        if (ImGui.Button("Add") && SaveRowDraft(workspace)) ImGui.CloseCurrentPopup();
        ImGui.EndDisabled(); ImGui.SameLine();
        if (ImGui.Button("Cancel")) Close();
        ImGui.EndPopup();

        void Choice(RowChoice choice)
        {
            ImGui.PushID(choice.Index);
            if (ImGui.Selectable(choice.Name, rowIndex == choice.Index)) rowIndex = choice.Index;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(choice.Description.Length == 0 ? $"{choice.Name}\nIndex {choice.Index}"
                    : $"{choice.Name}\n{choice.Description}\nIndex {choice.Index}");
            ImGui.PopID();
        }
        void Close() { ClearRow(); ImGui.CloseCurrentPopup(); }
    }

    private readonly record struct RowChoice(int Index, string Name, string Description);
    private RowChoice[] rowChoices = [];
    private (string Search, bool Perk, InvestmentCatalog? Catalog, int Names) rowChoicesKey;

    /** The filtered choices, rebuilt only when the search, the list kind, the catalog or the names change. */
    private RowChoice[] RowChoices(StudioPageContext context, InvestmentCatalog catalog)
    {
        int names = context.Workspace.NameRevision;
        if (rowChoicesKey.Search == rowSearch && rowChoicesKey.Perk == addingPerk
            && ReferenceEquals(rowChoicesKey.Catalog, catalog) && rowChoicesKey.Names == names) return rowChoices;
        rowChoicesKey = (rowSearch, addingPerk, catalog, names);
        var all = addingPerk
            ? Enumerable.Range(0, catalog.PerkCount).Select(i => new RowChoice(i, PerkLabel(context, catalog, i),
                catalog.PerkDefinitions[i].Description))
            : catalog.StatDefinitions.Select(stat => new RowChoice(stat.Index, stat.Name, ""));
        return rowChoices = all.Where(choice => rowSearch.Length == 0
            || choice.Name.Contains(rowSearch, StringComparison.OrdinalIgnoreCase)
            || choice.Description.Contains(rowSearch, StringComparison.OrdinalIgnoreCase)
            || choice.Index.ToString().Contains(rowSearch, StringComparison.Ordinal)).ToArray();
    }

    /**
     * @param workspace Owns the selected document and project.
     * @return True after a successful save clears the draft.
     */
    private bool SaveRowDraft(StudioWorkspace workspace)
    {
        if (addingPerk) workspace.Run(() => workspace.AddInvestmentPerk(checked((ushort)rowIndex)));
        else workspace.Run(() => workspace.AddInvestmentStat(checked((byte)rowIndex), rowValue));
        if (workspace.Error.Length != 0) return false;
        ClearRow();
        return true;
    }
}
