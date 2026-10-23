using ImGuiNET;
using SunriseStudio.Authoring;
using SunriseStudio.Scene;

namespace SunriseStudio.Editor;

/** Project names for scene objects: one name covers every part of an editable row. */
public sealed partial class StudioForm
{
    private readonly Dictionary<WorldObject, string> nameKeys = new(ReferenceEqualityComparer.Instance);
    private WorldDocument? nameKeyWorld;
    private Dictionary<string, string> projectNames = new(StringComparer.Ordinal);
    private int projectNamesRevision = -1;
    private string nameDraft = "", nameDraftKey = "";

    /** An editable row keys by its edit target, so all its parts share a name; others key by world row. */
    private string ObjectNameKey(WorldObject row)
    {
        if (!ReferenceEquals(nameKeyWorld, inspectionWorld)) { nameKeys.Clear(); nameKeyWorld = inspectionWorld; }
        if (nameKeys.TryGetValue(row, out var key)) return key;
        string? target = workspace.Catalog is { } catalog
            ? SceneEdits.Describe(catalog, row.Source, inspectionWorld, workspace.Project?.ObjectEdits).Target?.Key : null;
        return nameKeys[row] = target is null ? "row:" + row.Id : "edit:" + target;
    }

    private IReadOnlyDictionary<string, string> ProjectNames()
    {
        if (projectNamesRevision != workspace.ProjectRevision)
        {
            projectNamesRevision = workspace.ProjectRevision;
            var names = (workspace.Project?.ObjectNames ?? []).ToDictionary(entry => entry.Key, entry => entry.Name, StringComparer.Ordinal);
            // Tree rows rebuild only when a name changed, not on every project revision.
            if (names.Count != projectNames.Count || names.Any(entry => projectNames.GetValueOrDefault(entry.Key) != entry.Value))
                treeRowsDirty = true;
            projectNames = names;
        }
        return projectNames;
    }

    /** The project's name for this world row, if it has one. */
    private string? ProjectName(WorldObject? row) =>
        row is not null && ProjectNames() is { Count: > 0 } names && names.TryGetValue(ObjectNameKey(row), out var name) ? name : null;

    /** The project's name for a scene object the viewport hands over. */
    private string? ProjectName(object source) =>
        ProjectNames().Count > 0 && inspectionWorld?.Find(source) is { } row ? ProjectName(row) : null;

    /** The inspector's name field; it saves when the field loses focus. */
    private void DrawObjectNameField(WorldObject row)
    {
        string key = ObjectNameKey(row);
        if (nameDraftKey != key) { nameDraftKey = key; nameDraft = ProjectName(row) ?? ""; }
        ImGui.BeginDisabled(!workspace.CanEdit);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##object-name", workspace.CanEdit ? "Name in this project" : "Create a project to name objects",
            ref nameDraft, 200);
        if (ImGui.IsItemDeactivatedAfterEdit() && nameDraft.Trim() != (ProjectName(row) ?? ""))
        {
            string text = nameDraft;
            workspace.Run(() => workspace.SetObjectName(key, text));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(key.StartsWith("edit:", StringComparison.Ordinal)
                ? "Names this placement in this project only; all its parts share the name. Clear it to remove."
                : "Names this object in this project only. Clear it to remove.");
        ImGui.EndDisabled();
    }
}
