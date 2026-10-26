using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using Solforge.Content;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/**
 * Window > Content editors (Studio.md §3.6, Phases 35-38): the project's content documents
 * (content/<family>/<id>.json) with a structured subclass editor, a JSON editor for every family,
 * validation with cross-document references, and Export to build/server. The MCP content tools
 * write the same files, so this window re-reads a document when its file changes.
 */
public sealed partial class StudioForm
{
    private bool contentOpen;
    private int contentFamily;
    private string? contentId;
    private ContentDocument? contentDocument;
    private DateTime contentStamp;
    private string contentDraft = "", contentNewId = "", contentNewName = "", contentNewElement = "arc", contentStatus = "";
    private int contentNewClass = 1;
    private bool contentDirty;
    private IReadOnlyList<ContentIssue> contentIssues = [];
    private static readonly string[] GuardianClassNames = ["titan", "hunter", "warlock"];
    private static readonly string[] SubclassRoleNames = Enum.GetNames<SubclassRole>();

    private void OpenContentEditors() => contentOpen = true;

    private ContentStore? ContentStoreOrNull() => workspace.Project?.DirectoryPath is { Length: > 0 } root ? new ContentStore(root) : null;

    private void DrawContentEditorsMenuItem()
    {
        if (UiAuto.MenuItem("Content editors", "", contentOpen)) contentOpen = !contentOpen;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Subclasses, abilities, damage types, loot pools, enemies, activities and the seasonal artifact");
    }

    private void DrawContentEditors()
    {
        if (!contentOpen) return;
        ImGui.SetNextWindowSize(new(Math.Min(1100 * scale, ClientSize.Width - 40), Math.Min(700 * scale, ClientSize.Height - 40)), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Content editors", ref contentOpen)) { ImGui.End(); return; }
        var store = ContentStoreOrNull();
        if (store is null) { ImGui.TextDisabled("Open or create a project first; content lives in <project>\\content."); ImGui.End(); return; }
        try
        {
            ReloadContentIfChanged(store);
            if (ImGui.BeginChild("content-list", new(260 * scale, 0), ImGuiChildFlags.Borders | ImGuiChildFlags.ResizeX)) DrawContentList(store);
            ImGui.EndChild();
            ImGui.SameLine();
            if (ImGui.BeginChild("content-body", Vector2.Zero)) DrawContentBody(store);
            ImGui.EndChild();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            contentStatus = error.Message;
        }
        ImGui.End();
    }

    private void DrawContentList(ContentStore store)
    {
        string[] titles = ContentFamilies.All.Select(family => family.Title).ToArray();
        ImGui.SetNextItemWidth(-1);
        if (UiAuto.Combo("##content-family", ref contentFamily, titles, titles.Length)) SelectContent(null, null);
        var family = ContentFamilies.All[Math.Clamp(contentFamily, 0, titles.Length - 1)];
        var (label, color) = family.Support switch
        {
            RuntimeSupport.Live => ("Live in game", SolTheme.Ok),
            RuntimeSupport.Partial => ("Partly live", StudioTheme.Accent),
            _ => ("Authoring only", StudioTheme.Muted)
        };
        ImGui.TextColored(color, label);
        if (ImGui.IsItemHovered()) { ImGui.BeginTooltip(); ImGui.PushTextWrapPos(420 * scale); ImGui.TextUnformatted(family.Notes); ImGui.PopTextWrapPos(); ImGui.EndTooltip(); }
        ImGui.Separator();
        foreach (var (_, id, _) in store.List(family.Id))
            if (UiAuto.Selectable(id, id == contentId && contentDocument?.Family == family.Id)) SelectContent(store, (family.Id, id));
        ImGui.Separator();
        ImGui.TextDisabled("New " + family.Title.ToLowerInvariant());
        ImGui.SetNextItemWidth(-1); UiAuto.InputTextWithHint("##content-new-name", "Name", ref contentNewName, 96);
        ImGui.SetNextItemWidth(-1); UiAuto.InputTextWithHint("##content-new-id", "id (blank: from the name)", ref contentNewId, 64);
        if (family.Id is ContentFamilies.Subclass or ContentFamilies.Ability)
        {
            ImGui.SetNextItemWidth(-1); UiAuto.Combo("##content-new-class", ref contentNewClass, GuardianClassNames, GuardianClassNames.Length);
            ImGui.SetNextItemWidth(-1); UiAuto.InputTextWithHint("##content-new-element", "element (arc, solar, void...)", ref contentNewElement, 32);
        }
        if (UiAuto.Button("Create", new(-1, 0)) && contentNewName.Trim().Length > 0)
        {
            string id = contentNewId.Trim().Length > 0 ? contentNewId.Trim() : SubclassInterop.Slug(contentNewName);
            if (store.Exists(family.Id, id)) contentStatus = $"{family.Id} '{id}' already exists.";
            else
            {
                var document = ContentStore.NewDocument(family.Id, id, contentNewName.Trim(),
                    SubclassInterop.ParseClass(GuardianClassNames[Math.Clamp(contentNewClass, 0, 2)]), contentNewElement.Trim().ToLowerInvariant());
                store.Save(document);
                contentNewId = contentNewName = "";
                SelectContent(store, (family.Id, id));
                contentStatus = $"Created {family.Id} '{id}'.";
            }
        }
        if (UiAuto.Button("Export all to build\\server", new(-1, 0))) ExportContent(store);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("loot_pools.lua and sol_custom_data.lua (server/scripts), subclass_nodes.tsv (server/data)");
    }

    private void ExportContent(ContentStore store)
    {
        var problems = store.ValidateAll().Where(row => row.Issue.Error).ToList();
        if (problems.Count > 0) { contentStatus = $"Export stopped: {problems.Count} error(s), first {problems[0].Family} '{problems[0].Id}': {problems[0].Issue}"; return; }
        string output = Path.Combine(workspace.Project!.DirectoryPath, "build");
        var written = ContentExport.WriteTo(output, ContentExport.Build(store.LoadAll()));
        contentStatus = written.Count == 0 ? "Nothing to export." : $"Wrote {written.Count} file(s) to {output}.";
    }

    private void SelectContent(ContentStore? store, (string Family, string Id)? row)
    {
        contentDocument = null; contentId = null; contentDraft = ""; contentDirty = false; contentIssues = [];
        if (store is null || row is not { } pick) return;
        contentDocument = store.Load(pick.Family, pick.Id);
        contentId = pick.Id;
        contentStamp = File.GetLastWriteTimeUtc(store.PathOf(pick.Family, pick.Id));
        contentDraft = ContentJson.Serialize(contentDocument);
        contentIssues = store.ValidateAll(pick.Family, pick.Id).Select(result => result.Issue).ToList();
    }

    /** An MCP tool or another program changed the open file: take its version unless this window has unsaved edits. */
    private void ReloadContentIfChanged(ContentStore store)
    {
        if (contentDocument is null || contentId is null || contentDirty) return;
        string path = store.PathOf(contentDocument.Family, contentId);
        if (!File.Exists(path)) { SelectContent(null, null); return; }
        if (File.GetLastWriteTimeUtc(path) != contentStamp) SelectContent(store, (contentDocument.Family, contentId));
    }

    private void DrawContentBody(ContentStore store)
    {
        if (contentStatus.Length > 0) { ImGui.TextWrapped(contentStatus); ImGui.Separator(); }
        if (contentDocument is not { } document) { ImGui.TextDisabled("Pick a document or create one."); return; }
        ImGui.TextColored(StudioTheme.Accent, $"{document.Family} / {document.Id}");
        ImGui.SameLine();
        if (UiAuto.Button(contentDirty ? "Save*" : "Save")) SaveContent(store, document);
        ImGui.SameLine();
        if (UiAuto.Button("Validate")) contentIssues = store.ValidateAll(document.Family, document.Id).Select(row => row.Issue).ToList();
        ImGui.SameLine();
        if (UiAuto.Button("Revert")) SelectContent(store, (document.Family, document.Id));
        ImGui.SameLine();
        if (UiAuto.Button("Delete") && ImGui.GetIO().KeyShift) { store.Delete(document.Family, document.Id); SelectContent(null, null); contentStatus = "Deleted."; return; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Shift+click to delete the file");
        float issuesHeight = Math.Min(contentIssues.Count, 6) * ImGui.GetTextLineHeightWithSpacing() + ImGui.GetFrameHeightWithSpacing();
        if (ImGui.BeginTabBar("content-tabs"))
        {
            if (document is SubclassDefinition subclass && UiAuto.BeginTabItem("Subclass"))
            {
                if (ImGui.BeginChild("subclass-editor", new(0, -issuesHeight))) DrawSubclassEditor(subclass);
                ImGui.EndChild();
                UiAuto.EndTabItem();
            }
            if (UiAuto.BeginTabItem("JSON"))
            {
                if (UiAuto.InputTextMultiline("##content-json", ref contentDraft, 1 << 20, new(-1, -issuesHeight - ImGui.GetFrameHeightWithSpacing()))) contentDirty = true;
                if (UiAuto.Button("Apply JSON"))
                {
                    try
                    {
                        var parsed = ContentJson.Parse(document.Family, contentDraft);
                        if (parsed.Id != document.Id) contentStatus = "Keep the id; create a new document to rename.";
                        else { contentDocument = parsed; contentDirty = true; contentIssues = parsed.Validate(); contentStatus = "Applied; Save to write the file."; }
                    }
                    catch (JsonException error) { contentStatus = "JSON: " + error.Message; }
                }
                UiAuto.EndTabItem();
            }
            ImGui.EndTabBar();
        }
        ImGui.Separator();
        if (contentIssues.Count == 0) ImGui.TextColored(SolTheme.Ok, "No issues.");
        foreach (var issue in contentIssues.Take(50))
            ImGui.TextColored(issue.Error ? SolTheme.Bad : StudioTheme.Accent, issue.ToString());
    }

    private void SaveContent(ContentStore store, ContentDocument document)
    {
        store.Save(document);
        contentDirty = false;
        contentStamp = File.GetLastWriteTimeUtc(store.PathOf(document.Family, document.Id));
        contentDraft = ContentJson.Serialize(document);
        contentIssues = store.ValidateAll(document.Family, document.Id).Select(row => row.Issue).ToList();
        contentStatus = $"Saved {document.Family} '{document.Id}'.";
    }

    private void DrawSubclassEditor(SubclassDefinition subclass)
    {
        bool changed = false;
        string name = subclass.Name, description = subclass.Description, element = subclass.Element, item = subclass.ItemHash ?? "", baseItem = subclass.BaseItemHash ?? "";
        if (UiAuto.InputText("Name", ref name, 96)) { subclass.Name = name; changed = true; }
        if (UiAuto.InputTextMultiline("Description", ref description, 2048, new(0, 3 * ImGui.GetTextLineHeightWithSpacing()))) { subclass.Description = description; changed = true; }
        int classIndex = Math.Clamp((int)subclass.Class, 0, 2);
        ImGui.SetNextItemWidth(160 * scale);
        if (UiAuto.Combo("Class", ref classIndex, GuardianClassNames, GuardianClassNames.Length)) { subclass.Class = (GuardianClass)classIndex; changed = true; }
        ImGui.SameLine(); ImGui.SetNextItemWidth(140 * scale);
        if (UiAuto.InputText("Element", ref element, 32)) { subclass.Element = element.Trim().ToLowerInvariant(); changed = true; }
        ImGui.SetNextItemWidth(160 * scale);
        if (UiAuto.InputText("Item hash", ref item, 16)) { subclass.ItemHash = item.Trim().Length > 0 ? item.Trim() : null; changed = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("The subclass item (Clone subclass makes one); keys subclass_nodes.tsv rows");
        ImGui.SameLine(); ImGui.SetNextItemWidth(160 * scale);
        if (UiAuto.InputText("Base item", ref baseItem, 16)) { subclass.BaseItemHash = baseItem.Trim().Length > 0 ? baseItem.Trim() : null; changed = true; }
        var limits = subclass.Limits;
        int nodes = limits.SelectableNodes, perks = limits.Perks, perTree = limits.PerTree, aspects = limits.Aspects;
        ImGui.SetNextItemWidth(90 * scale); if (UiAuto.InputInt("Nodes", ref nodes)) { limits.SelectableNodes = nodes; changed = true; }
        ImGui.SameLine(); ImGui.SetNextItemWidth(90 * scale); if (UiAuto.InputInt("Perks", ref perks)) { limits.Perks = perks; changed = true; }
        ImGui.SameLine(); ImGui.SetNextItemWidth(90 * scale); if (UiAuto.InputInt("Per path", ref perTree)) { limits.PerTree = perTree; changed = true; }
        ImGui.SameLine(); ImGui.SetNextItemWidth(90 * scale); if (UiAuto.InputInt("Aspects", ref aspects)) { limits.Aspects = aspects; changed = true; }
        ImGui.TextDisabled($"Server today: {SubclassLimits.ServerNodes} nodes, {SubclassLimits.ServerPerks} perks, {SubclassLimits.ServerPerTree} per path. Names and descriptions are live via subclass_nodes.tsv.");

        foreach (var column in subclass.Columns)
            changed |= DrawNodeGroup("Column: " + column.Name, "col-" + column.Id, column.Nodes, SubclassRole.Perk, subclass);
        foreach (var tree in subclass.Trees)
        {
            string treeName = tree.Name;
            ImGui.PushID("tree-name-" + tree.Index);
            ImGui.SetNextItemWidth(260 * scale);
            if (UiAuto.InputText($"Path {tree.Index} name", ref treeName, 96)) { tree.Name = treeName; changed = true; }
            ImGui.PopID();
            changed |= DrawNodeGroup($"Path {tree.Index}: {tree.Name}", "tree-" + tree.Index, tree.Nodes, SubclassRole.Perk, subclass);
        }
        if (subclass.Trees.Count < 3 && UiAuto.Button("Add path"))
        {
            int index = Enumerable.Range(1, 3).First(i => subclass.Trees.All(tree => tree.Index != i));
            subclass.Trees.Add(new() { Index = index, Name = $"Path {index}" });
            changed = true;
        }
        changed |= DrawNodeGroup("Aspects (Abilities 3.0)", "aspects", subclass.Aspects, SubclassRole.Aspect, subclass);
        changed |= DrawNodeGroup("Fragments (Abilities 3.0)", "fragments", subclass.Fragments, SubclassRole.Fragment, subclass);
        if (subclass.Aspects.Count == 0 && subclass.Fragments.Count == 0 && UiAuto.Button("Add Abilities 3.0 starter aspects and fragments"))
        { SubclassTemplates.AddAbilities3(subclass); changed = true; }
        if (subclass.DefaultBuild is { } build)
        {
            var check = SubclassRules.CheckBuild(subclass, build);
            ImGui.TextColored(check.HasErrors ? SolTheme.Bad : SolTheme.Ok, check.HasErrors ? "Default build: " + check.First(issue => issue.Error) : "Default build is valid.");
        }
        if (changed) { contentDirty = true; contentIssues = subclass.Validate(); contentDraft = ContentJson.Serialize(subclass); }
    }

    /** One table of nodes; returns true when anything changed. */
    private bool DrawNodeGroup(string title, string id, List<SubclassNode> nodes, SubclassRole newRole, SubclassDefinition subclass)
    {
        bool changed = false;
        ImGui.PushID(id);
        if (UiAuto.CollapsingHeader($"{title} ({nodes.Count})###{id}", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (nodes.Count > 0 && ImGui.BeginTable("nodes", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Id", ImGuiTableColumnFlags.WidthFixed, 110 * scale);
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 1);
                ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthFixed, 100 * scale);
                ImGui.TableSetupColumn("Entry", ImGuiTableColumnFlags.WidthFixed, 70 * scale);
                ImGui.TableSetupColumn("Description", ImGuiTableColumnFlags.WidthStretch, 2);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 30 * scale);
                ImGui.TableHeadersRow();
                for (int i = 0; i < nodes.Count; i++)
                {
                    var node = nodes[i];
                    ImGui.PushID(i);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.SetNextItemWidth(-1);
                    string nodeId = node.Id; if (UiAuto.InputText("##id", ref nodeId, 64)) { node.Id = nodeId.Trim(); changed = true; }
                    ImGui.TableNextColumn(); ImGui.SetNextItemWidth(-1);
                    string nodeName = node.Name; if (UiAuto.InputText("##name", ref nodeName, 96)) { node.Name = nodeName; changed = true; }
                    ImGui.TableNextColumn(); ImGui.SetNextItemWidth(-1);
                    int role = (int)node.Role;
                    if (UiAuto.Combo("##role", ref role, SubclassRoleNames, SubclassRoleNames.Length)) { node.Role = (SubclassRole)role; changed = true; }
                    ImGui.TableNextColumn(); ImGui.SetNextItemWidth(-1);
                    int entry = node.Entry ?? -1;
                    if (UiAuto.InputInt("##entry", ref entry, 0)) { node.Entry = entry < 0 ? null : entry; changed = true; }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(node.Entry is { } e ? $"Shipped slot: {SubclassLayout.SlotOf(e)}" : "-1: a new node with no game entry");
                    ImGui.TableNextColumn(); ImGui.SetNextItemWidth(-1);
                    string text = node.Description; if (UiAuto.InputText("##description", ref text, 1024)) { node.Description = text; changed = true; }
                    ImGui.TableNextColumn();
                    if (UiAuto.SmallButton("x")) { nodes.RemoveAt(i); changed = true; ImGui.PopID(); break; }
                    ImGui.PopID();
                }
                ImGui.EndTable();
            }
            if (UiAuto.SmallButton("Add node"))
            {
                int n = nodes.Count + 1;
                string baseId = id + "-" + n;
                while (subclass.FindNode(baseId) is not null) baseId = id + "-" + ++n;
                nodes.Add(new() { Id = baseId, Name = "New node", Role = newRole, FragmentSlots = newRole == SubclassRole.Aspect ? 1 : 0 });
                changed = true;
            }
        }
        ImGui.PopID();
        return changed;
    }
}
