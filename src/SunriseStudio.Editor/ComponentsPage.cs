using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** Every config of an entity in one place: the known fields first, then the generic read. */
public sealed partial class ComponentsPage : IStudioPage
{
    // A config tag can be listed by many entities; an edit reaches all of them.
    private const string SharedEdit = StudioUi.MayNotWork + " Changes every entity using this config.";
    private const string FieldEdit = "Enter applies. Changes every entity using this config.";
    private StudioDocument? requestedDocument;
    private long requestedOverlay = -1;
    private EntityComponentsState? draftState;
    // Typed values not yet applied, by config and field offset; cleared when the read changes.
    private readonly Dictionary<(uint Config, int Offset), string> drafts = [];

    public string Id => "sunrise.components";
    public string Title => "Components";
    public bool CanOpen(StudioDocument document) =>
        document.Entry.Reference == SEntity.Class && AssetCategories.IsStructured(document.Entry);

    /** Draw reads no package records; a worker reads the configs once per document and overlay. */
    public void Draw(StudioPageContext context)
    {
        var workspace = context.Workspace;
        if (workspace.Document is not { } document || workspace.Catalog is not { } catalog) return;
        ImGui.BeginChild("components-page");
        DrawHeader(workspace, document);
        DrawDamage(context);
        if (workspace.LoadedComponents is { } state) DrawSections(workspace, state);
        else
        {
            // One request per document and overlay; a failed read waits for Retry.
            if (!workspace.IsBusy && (!ReferenceEquals(requestedDocument, document) || requestedOverlay != catalog.OverlayRevision))
            {
                requestedDocument = document; requestedOverlay = catalog.OverlayRevision;
                workspace.Run(workspace.LoadComponents);
            }
            if (workspace.IsBusy) ImGui.TextDisabled("Reading components...");
            else
            {
                if (workspace.Error.Length != 0) ImGui.TextWrapped(workspace.Error);
                if (StudioUi.IconButton("components-retry", StudioIcon.Reset, "Read the configs again")) requestedDocument = null;
            }
        }
        ImGui.EndChild();
    }

    /** Draws the combatant damage parts; a separate file implements it. */
    partial void DrawDamage(StudioPageContext context);

    private static void DrawHeader(StudioWorkspace workspace, StudioDocument document)
    {
        var index = workspace.Names?.GameNames?.Components;
        ImGui.TextUnformatted(document.Name);
        if (ImGui.BeginTable("entity-facts", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Fact", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 8);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
            Row("Display name", index?.CombatantName(document.Entry.TagHash) ?? "None");
            Row("Internal name", ComponentRoles.InternalNameHash(document.Bytes) is { } hash
                ? workspace.ResolveHash(hash) is { } text ? $"{text} (0x{hash:X8})" : $"0x{hash:X8}" : "None");
            Row("Object type", ComponentRoles.ObjectType(document.Bytes) ?? "None");
            Row("Package", document.Entry.PackageName);
            ImGui.EndTable();
        }
        static void Row(string name, string value)
        {
            ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TextDisabled(name);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(value);
        }
    }

    /** One header per role; a role listed more than once gets one node per config under its first place. */
    private void DrawSections(StudioWorkspace workspace, EntityComponentsState state)
    {
        if (!ReferenceEquals(draftState, state)) { drafts.Clear(); draftState = state; }
        bool actor = workspace.Document is { } document
            && workspace.Names?.GameNames?.Components?.HasActorGroup(document.Entry.TagHash) == true;
        StudioUi.Heading($"{state.Sections.Count} configs", actor ? "AI, weapons, loot and faction are not read yet." : "");
        if (state.Sections.Count == 0) ImGui.TextDisabled("This entity lists no configs.");
        int index = 0;
        foreach (var role in state.Sections.GroupBy(section => section.Role))
        {
            var sections = role.ToArray();
            ImGui.PushID(index++);
            if (sections.Length == 1) DrawSection(workspace, sections[0], role.Key, false);
            else if (ImGui.CollapsingHeader($"{role.Key} ({sections.Length})###role"))
            {
                ImGui.Indent();
                for (int k = 0; k < sections.Length; k++)
                {
                    ImGui.PushID(k);
                    DrawSection(workspace, sections[k], $"Config {k + 1}", true);
                    ImGui.PopID();
                }
                ImGui.Unindent();
            }
            ImGui.PopID();
        }
    }

    private void DrawSection(StudioWorkspace workspace, ComponentSection section, string label, bool nested)
    {
        string id = $"{label}###cfg{section.Config:X8}";
        bool open = nested ? ImGui.TreeNodeEx(id, ImGuiTreeNodeFlags.NoTreePushOnOpen) : ImGui.CollapsingHeader(id);
        StudioUi.Tip($"{section.Name}\n0x{section.Config:X8}");
        if (!open) return;
        ImGui.Indent();
        if (StudioUi.IconButton("open-config", StudioIcon.Open, "Open this config as an asset", !workspace.IsBusy))
            workspace.Run(() => workspace.Select(section.Config));
        ImGui.SameLine(); ImGui.TextDisabled($"0x{section.Config:X8}");
        if (section.Error.Length != 0) ImGui.TextColored(StudioTheme.Accent, section.Error);
        else
        {
            var nodes = section.Inspection?.Sections ?? [];
            StudioUi.Heading("Fields", section.Editable ? SharedEdit : "");
            DrawTable(workspace, section, section.Fields);
            for (int i = 0; i < nodes.Count; i++)
            {
                ImGui.PushID(i);
                DrawNode(workspace, section, nodes[i]);
                ImGui.PopID();
            }
        }
        ImGui.Unindent();
    }

    private void DrawNode(StudioWorkspace workspace, ComponentSection section, AssetNode node)
    {
        if (!ImGui.TreeNode(node.Name)) return;
        DrawTable(workspace, section, node.Properties);
        if (node.Children is { } children)
            for (int i = 0; i < children.Count; i++)
            {
                ImGui.PushID(i);
                DrawNode(workspace, section, children[i]);
                ImGui.PopID();
            }
        ImGui.TreePop();
    }

    private void DrawTable(StudioWorkspace workspace, ComponentSection section, IReadOnlyList<AssetProperty> properties)
    {
        if (properties.Count == 0 || !ImGui.BeginTable("fields", 2,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn("Field", ImGuiTableColumnFlags.WidthStretch, 0.4f);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 0.6f);
        for (int i = 0; i < properties.Count; i++)
        {
            ImGui.PushID(i);
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            ImGui.TextUnformatted(properties[i].Name);
            ImGui.TableNextColumn();
            DrawValue(workspace, section, properties[i]);
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    /** An editable field applies on Enter, as one undo step on its config. */
    private void DrawValue(StudioWorkspace workspace, ComponentSection section, AssetProperty property)
    {
        if (!ComponentFields.IsEditable(property))
        {
            ImGui.TextUnformatted(property.Value);
            StudioUi.Tip(property.Evidence.Length != 0 ? property.Evidence : property.Value);
            return;
        }
        var key = (section.Config, property.Offset);
        string draft = drafts.GetValueOrDefault(key, property.Value);
        ImGui.BeginDisabled(!workspace.CanEdit);
        ImGui.SetNextItemWidth(-1);
        bool apply = ImGui.InputText("##value", ref draft, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.EndDisabled();
        // Long evidence is dropped; the field name already says what it is.
        string evidence = property.Evidence.Length is > 0 and < 60 ? property.Evidence + "\n" : "";
        StudioUi.Tip(workspace.Project is null ? "Open a project to edit." : $"{evidence}{FieldEdit}\n{StudioUi.MayNotWork}");
        drafts[key] = draft;
        if (apply) workspace.Run(() => workspace.ApplyComponentEdit(section.Config, property, draft));
    }
}
