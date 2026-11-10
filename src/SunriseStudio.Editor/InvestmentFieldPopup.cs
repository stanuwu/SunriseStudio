using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

/** One scalar field draft and its popup. The draft is dropped when the document, project or overlay it was opened on changes. */
internal sealed class InvestmentFieldPopup
{
    public const string Title = "Edit investment field";
    private AssetProperty? field;
    private string value = "";
    private bool open;
    private StudioDocument? document;
    private StudioProject? project;
    private long overlay = -1;
    public AssetProperty? Field => field;
    public StudioDocument? Document => document;
    public string Value { get => value; set => this.value = value; }

    public void Begin(StudioWorkspace workspace, AssetProperty edited)
    {
        field = edited; value = edited.Value; open = true;
        document = workspace.Document; project = workspace.Project; overlay = workspace.Catalog?.OverlayRevision ?? -1;
    }

    /** Drops the draft when its document, project or overlay changes, even while the popup is not drawn. */
    public void Invalidate(StudioWorkspace workspace) { if (document is not null && !Current(workspace)) Clear(); }

    public void Clear() { field = null; document = null; project = null; open = false; }

    private bool Current(StudioWorkspace workspace) => ReferenceEquals(document, workspace.Document)
        && ReferenceEquals(project, workspace.Project) && overlay == workspace.Catalog?.OverlayRevision;

    /**
     * Draws the open popup. A session that cannot edit shows the same draft with Save disabled.
     * @param input Draws field-specific choices above the value. It returns true when it drew the value input itself.
     * @param preview Draws what the typed value means, under the input.
     */
    public void Draw(StudioWorkspace workspace, Func<AssetProperty, bool>? input = null, Action<AssetProperty>? preview = null)
    {
        if (open) { ImGui.OpenPopup(Title); open = false; }
        if (!ImGui.BeginPopupModal(Title, ImGuiWindowFlags.AlwaysAutoResize)) return;
        if (!Current(workspace)) { Clear(); ImGui.CloseCurrentPopup(); ImGui.EndPopup(); return; }
        if (field is { } shown)
        {
            ImGui.TextUnformatted(shown.Name);
            if (input?.Invoke(shown) != true) { ImGui.SetNextItemWidth(350); ImGui.InputText("##investmentValue", ref value, 64); }
            preview?.Invoke(shown);
            ImGui.BeginDisabled(!workspace.CanEdit);
            if (ImGui.Button("Save delta") && Save(workspace)) ImGui.CloseCurrentPopup();
            ImGui.EndDisabled(); ImGui.SameLine();
            if (ImGui.Button("Cancel")) { ImGui.CloseCurrentPopup(); Clear(); }
            if (workspace.Project is null) ImGui.TextDisabled("Create or open a project to edit");
        }
        ImGui.EndPopup();
    }

    /**
     * @param workspace Owns the selected document and project.
     * @return True after a successful save clears the draft.
     */
    public bool Save(StudioWorkspace workspace)
    {
        if (field is not { } edited) return false;
        workspace.Run(() => workspace.ApplyInvestmentEdit(edited, value));
        if (workspace.Error.Length != 0) return false;
        Clear();
        return true;
    }
}
