using System.Globalization;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

/** One scalar field draft and its popup. The draft is dropped when the document, project or overlay it was opened on changes. */
internal sealed class InvestmentFieldPopup
{
    public const string Title = "Edit investment field";
    /** What saving a table field means for the game. A refusal wraps no narrower than this line. */
    public const string InstallLine = "Saved in the project. The game loads it after Build and install and a restart.";
    private AssetProperty? field;
    private InvestmentTableField? table;
    private string value = "", label = "";
    private bool open;
    private StudioDocument? document;
    private StudioProject? project;
    private long overlay = -1;
    public AssetProperty? Field => field;
    /** The root table that owns the field, or null for a field of the open document. */
    public InvestmentTableField? Table => table;
    public StudioDocument? Document => document;
    public string Value { get => value; set => this.value = value; }

    /** The draft as the decimal or 0x integer AssetEdit writes, or null when it is not one. */
    public long? Number()
    {
        string text = value.Trim();
        bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        return long.TryParse(hex ? text[2..] : text,
            hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture,
            out long number) ? number : null;
    }

    public void Begin(StudioWorkspace workspace, AssetProperty edited) => Begin(workspace, edited, null, "");

    /** @param row Names the row for the popup, for example "Pool 412, entry 3". */
    public void Begin(StudioWorkspace workspace, InvestmentTableField edited, string row) => Begin(workspace,
        edited.Property, edited, row);

    private void Begin(StudioWorkspace workspace, AssetProperty edited, InvestmentTableField? owner, string row)
    {
        field = edited; table = owner; label = row; value = edited.Value; open = true;
        document = workspace.Document; project = workspace.Project; overlay = workspace.Catalog?.OverlayRevision ?? -1;
    }

    /** Drops the draft when its document, project or overlay changes, even while the popup is not drawn. */
    public void Invalidate(StudioWorkspace workspace) { if (document is not null && !Current(workspace)) Clear(); }

    public void Clear() { field = null; table = null; document = null; project = null; open = false; }

    private bool Current(StudioWorkspace workspace) => ReferenceEquals(document, workspace.Document)
        && ReferenceEquals(project, workspace.Project) && overlay == workspace.Catalog?.OverlayRevision;

    /**
     * Draws the open popup. A session that cannot edit shows the same draft with Save disabled.
     * @param input Draws field-specific choices above the value. It returns true when it drew the value input itself.
     * @param preview Draws what the typed value means, under the input.
     * @param refusal Why the game would refuse the typed value. A refusal disables Save and shows above it.
     */
    public void Draw(StudioWorkspace workspace, Func<AssetProperty, bool>? input = null, Action<AssetProperty>? preview = null,
        Func<AssetProperty, string>? refusal = null)
    {
        if (open) { ImGui.OpenPopup(Title); open = false; }
        if (!ImGui.BeginPopupModal(Title, ImGuiWindowFlags.AlwaysAutoResize)) return;
        if (!Current(workspace)) { Clear(); ImGui.CloseCurrentPopup(); ImGui.EndPopup(); return; }
        if (field is { } shown)
        {
            ImGui.TextUnformatted(shown.Name);
            if (label.Length != 0) ImGui.TextDisabled(label);
            if (input?.Invoke(shown) != true) { ImGui.SetNextItemWidth(350); ImGui.InputText("##investmentValue", ref value, 64); }
            preview?.Invoke(shown);
            if (table is { } owned)
            {
                if (owned.Kind == InvestmentTableFieldKind.PoolEntryWeight) ImGui.TextDisabled("Weights are relative; they are not chances.");
                ImGui.TextDisabled(InstallLine);
                StudioUi.Help(StudioUi.MayNotWork);
            }
            string refused = refusal?.Invoke(shown) ?? "";
            if (refused.Length != 0)
            {
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Math.Max(ImGui.CalcTextSize(InstallLine).X,
                    ImGui.GetContentRegionAvail().X));
                ImGui.TextColored(StudioTheme.Caution, refused);
                ImGui.PopTextWrapPos();
            }
            ImGui.BeginDisabled(!workspace.CanEdit || refused.Length != 0);
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
        if (table is { } owned) workspace.Run(() => workspace.ApplyInvestmentTableEdit(owned, value));
        else workspace.Run(() => workspace.ApplyInvestmentEdit(edited, value));
        if (workspace.Error.Length != 0) return false;
        Clear();
        return true;
    }
}
