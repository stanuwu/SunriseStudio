using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

/** CuiPage part: adds, duplicates, reparents and deletes widgets and keeps their name labels. */
public sealed partial class CuiPage
{
    private sealed record WidgetLabels(int Version, uint Hierarchy, Dictionary<ushort, string> Names);
    private CuiDocument? structureDocument;
    private CuiWidgetAuthoring? structure;
    private string structureError = "", widgetName = "Frame";
    private int widgetKind, widgetParent, structureSelected = -1;
    private Vector4 widgetRectangle = new(20, 20, 240, 80), structureRectangle;
    private (CuiDocument Root, int Id, CuiLayoutRectangle? Rectangle)? structureLayout;
    private int pendingWidgetSelection = -1;
    private bool focusComposed;
    private bool isolateComposed, revealComposed;
    private CuiComposition? isolatedScene;
    private int isolatedSelection = -1;
    private HashSet<int> isolatedNodes = [];
    private StudioProject? labelProject;
    private int labelRevision = -1;
    private AddedProjectAsset? labelAsset;
    private WidgetLabels? labels;

    private string StructureName(CuiComposedNode node) => labels?.Names.GetValueOrDefault((ushort)node.Id) ?? SceneNode(node.Id)?.Label ?? node.Definition.Name;

    /** Structural edits are scoped to the open hierarchy, with a private root resource when needed. */
    private void DrawStructureToolbar(StudioPageContext context, CuiComposition scene)
    {
        if (!ReferenceEquals(structureDocument, document))
        {
            structureDocument = document; structure = null; structureError = ""; structureSelected = -1;
            try { structure = new(document!, scene); }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException) { structureError = error.Message; }
        }
        try { ReadLabels(context); }
        catch (Exception error) when (error is InvalidDataException or JsonException or IOException)
        { structureError = error.Message; structure = null; }
        ImGui.SameLine();
        if (UiAuto.Button("Frame widget")) focusComposed = true;
        ImGui.SameLine();
        UiAuto.Checkbox("Isolate", ref isolateComposed);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Preview the selected widget and its children");
        ImGui.SameLine();
        ImGui.BeginDisabled(!context.Workspace.CanEdit || structure is null);
        if (UiAuto.Button("Add widget"))
        {
            widgetParent = structure!.Parents.Any(node => node.Id == selectedComposed) ? selectedComposed : 0;
            widgetName = ((CuiWidgetKind)widgetKind).ToString(); ImGui.OpenPopup("cui-create-widget");
        }
        if (structureError.Length > 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(structureError);
        bool local = structure?.LocalWidgets.Any(node => node.Id == selectedComposed) == true;
        ImGui.SameLine(); ImGui.BeginDisabled(!local);
        if (UiAuto.Button("Duplicate")) DuplicateWidget(context, scene);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Copy stored profile properties. May not work in game.");
        ImGui.SameLine();
        if (UiAuto.Button("Delete")) DeleteWidget(context);
        ImGui.EndDisabled();
        if (ImGui.BeginPopup("cui-create-widget"))
        {
            string[] kinds = Enum.GetNames<CuiWidgetKind>();
            if (UiAuto.Combo("Type", ref widgetKind, kinds, kinds.Length)) widgetName = kinds[widgetKind];
            UiAuto.InputText("Name", ref widgetName, 80);
            ParentPicker("Parent", ref widgetParent);
            var position = new Vector2(widgetRectangle.X, widgetRectangle.Y);
            if (UiAuto.InputFloat2("Position", ref position)) { widgetRectangle.X = position.X; widgetRectangle.Y = position.Y; }
            var dimensions = new Vector2(widgetRectangle.Z, widgetRectangle.W);
            if (UiAuto.InputFloat2("Size", ref dimensions)) { widgetRectangle.Z = Math.Max(1, dimensions.X); widgetRectangle.W = Math.Max(1, dimensions.Y); }
            ImGui.BeginDisabled(string.IsNullOrWhiteSpace(widgetName));
            if (UiAuto.Button("Create"))
            {
                context.Workspace.Run(() => ApplyStructure(context, structure!.Create((CuiWidgetKind)widgetKind,
                    WidgetHash(widgetName), (ushort)widgetParent, widgetRectangle), widgetName.Trim(), "Created widget"));
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled(); ImGui.SameLine(); if (UiAuto.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        ImGui.EndDisabled();
    }

    /** Only local core widgets can be copied or removed. */
    private bool CanChangeWidget(StudioPageContext context) => context.Workspace.CanEdit && structure is not null
        && structure.LocalWidgets.Any(node => node.Id == selectedComposed);

    private void DuplicateWidget(StudioPageContext context, CuiComposition scene)
    {
        var selected = scene.Nodes.Single(node => node.Id == selectedComposed);
        string sourceName = labels?.Names.GetValueOrDefault((ushort)selected.Id) ??
            (selected.Definition.TypeHash == CuiTypeRegistry.TextType ? "Text" :
                selected.Definition.TypeHash == CuiTypeRegistry.BitmapType ? "Bitmap" :
                selected.NativeTypes.IsContainer(selected.Definition.TypeHash, selected.Definition.AuxiliaryTypeHash) ? "Container" : "Frame");
        string name = sourceName[..Math.Min(sourceName.Length, 72)] + " copy";
        context.Workspace.Run(() => ApplyStructure(context,
            structure!.Duplicate((ushort)selected.Id, WidgetHash(name), checked((ushort)selected.ParentId)), name, "Duplicated widget"));
    }

    private void DeleteWidget(StudioPageContext context) => context.Workspace.Run(() =>
        ApplyStructure(context, structure!.Remove((ushort)selectedComposed), null, "Removed widget"));

    private void ParentPicker(string label, ref int selected)
    {
        if (structure is null) return;
        int current = selected;
        if (!UiAuto.BeginCombo(label, structure.Parents.FirstOrDefault(node => node.Id == current) is { } parent ? StructureName(parent) : "Screen")) return;
        foreach (var node in structure.Parents)
        {
            ImGui.PushID(node.Id);
            if (UiAuto.Selectable(StructureName(node), selected == node.Id)) selected = node.Id;
            ImGui.PopID();
        }
        UiAuto.EndCombo();
    }

    /** The composed view edits only its local default-profile properties. */
    private void DrawStructureProperties(StudioPageContext context, CuiComposedNode selected)
    {
        if (structure is null || document is null || composition is null || !structure.LocalWidgets.Any(node => node.Id == selected.Id)) return;
        var root = composition.Resources[document.RootResource];
        // The root layout is parsed once per root and selection, not every frame.
        if (structureLayout is not { } layout || !ReferenceEquals(layout.Root, root) || layout.Id != selected.Id)
        {
            var component = root.Objects.Where(owner => owner.IsDefaultProfile).SelectMany(owner => owner.Components)
                .LastOrDefault(component => component.Key == (ulong)selected.Id);
            structureLayout = layout = (root, selected.Id,
                component is null ? null : CuiLayout.Read(root).FirstOrDefault(row => ReferenceEquals(row.Component, component)));
        }
        var rectangle = layout.Rectangle;
        ImGui.BeginDisabled(!context.Workspace.CanEdit);
        if (structureSelected != selected.Id)
        { structureSelected = selected.Id; structureRectangle = rectangle?.Rectangle ?? Vector4.Zero; widgetParent = selected.ParentId; }
        ParentPicker("Parent", ref widgetParent);
        ImGui.BeginDisabled(widgetParent == selected.ParentId);
        if (UiAuto.Button("Set parent")) context.Workspace.Run(() =>
            ApplyStructure(context, structure.Reparent((ushort)selected.Id, (ushort)widgetParent), null, "Changed widget parent"));
        ImGui.EndDisabled();
        if (rectangle is not null)
        {
            for (int axis = 0; axis < 4; axis++)
            {
                float value = structureRectangle[axis];
                if (UiAuto.DragFloat(axis switch { 0 => "X", 1 => "Y", 2 => "Width", _ => "Height" }, ref value, 1)) structureRectangle[axis] = value;
            }
            if (UiAuto.Button("Apply layout")) context.Workspace.Run(() => Save(CuiLayout.Apply(root.Data.Span, root, rectangle, structureRectangle), "Saved widget layout"));
            ImGui.SameLine(); if (UiAuto.Button("Reset layout")) structureRectangle = rectangle.Rectangle;
            DrawVisualPropertiesCore(context, rectangle, preview?.ComposedContent.GetValueOrDefault(selected.Id), root, Save);
        }
        ImGui.EndDisabled();
        void Save(byte[] bytes, string action) => ApplyStructure(context,
            new(document.Data.ToArray(), bytes, (ushort)selected.Id, new HashSet<ushort>()), null, action);
    }

    /** User-authored labels are plain project data; copied native resources remain derived deltas. */
    private void ReadLabels(StudioPageContext context)
    {
        if (ReferenceEquals(labelProject, context.Workspace.Project) && labelRevision == context.Workspace.ProjectRevision
            && labels?.Hierarchy == document?.TagHash) return;
        labelProject = context.Workspace.Project; labelRevision = context.Workspace.ProjectRevision; labelAsset = null;
        labels = new(1, document!.TagHash, []);
        if (labelProject is null) return;
        string name = LabelName(document.TagHash);
        var candidates = labelProject.AddedAssets.Where(asset => asset.Native is null && asset.Name == name).Take(2).ToArray();
        if (candidates.Length > 1) throw new InvalidDataException("This project has duplicate interface label files.");
        labelAsset = candidates.FirstOrDefault();
        if (labelAsset is null) return;
        if (labelAsset.Size > 1024 * 1024) throw new InvalidDataException("Interface labels exceed the supported size.");
        var parsed = JsonSerializer.Deserialize<WidgetLabels>(labelProject.ReadAddedAsset(labelAsset.Id, context.Workspace.Catalog));
        if (parsed is not { Version: 1 } || parsed.Hierarchy != document.TagHash || parsed.Names is null || parsed.Names.Count > 5120
            || parsed.Names.Any(pair => pair.Key >= 5120 || string.IsNullOrWhiteSpace(pair.Value) || pair.Value.Length > 120))
            throw new InvalidDataException("Interface labels are invalid.");
        labels = parsed;
    }

    private void ApplyStructure(StudioPageContext context, CuiWidgetMutation mutation, string? name, string action)
    {
        var workspace = context.Workspace;
        var project = workspace.Project ?? throw new InvalidOperationException("Create or open a project first.");
        var catalog = workspace.Catalog!;
        var root = composition!.Resources[document!.RootResource];
        structure!.RequireUnchanged(catalog.Read(document.TagHash), catalog.Read(root.TagHash));
        if (name is null && mutation.RemovedNodes.Count == 0 && mutation.Hierarchy.AsSpan().SequenceEqual(document.Data.Span)
            && mutation.Resource.AsSpan().SequenceEqual(root.Data.Span)) return;
        ReadLabels(context);
        var nextNames = labels!.Names.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (ushort removed in mutation.RemovedNodes) nextNames.Remove(removed);
        if (name is not null) nextNames[mutation.SelectedNode] = name;
        bool changeLabels = name is not null || mutation.RemovedNodes.Any(id => labels.Names.ContainsKey(id));
        byte[] labelBytes = JsonSerializer.SerializeToUtf8Bytes(new WidgetLabels(1, document.TagHash, nextNames));
        var owner = project.AddedAssets.FirstOrDefault(asset => asset.Native is not null && Tag(asset) == root.TagHash);
        bool isolate = owner?.Name != RootName(document.TagHash);
        List<ProjectAssetAddition> additions = [];
        if (isolate)
        {
            if (!catalog.TryGetEntry(root.TagHash, out var entry)) throw new InvalidDataException("The interface root is missing.");
            // Isolated roots use the top 256 new-package ids.
            int available = Enumerable.Range(SunriseStudio.Packages.PackageWriter.LastNewPackageId - 0xFF, 0x100)
                .FirstOrDefault(id => !catalog.ContainsInstalledPackage((ushort)id)
                    && project.AddedAssets.Where(asset => asset.Native?.PackageId == id).Select(asset => asset.Native!.EntryIndex!.Value)
                        .DefaultIfEmpty(-1).Max() < 8191, -1);
            if (available < 0) throw new InvalidDataException("No unused authoring package slot is available.");
            ushort package = checked((ushort)available);
            additions.Add(new(RootName(document.TagHash), mutation.Resource,
                new(package, entry.Reference, entry.TypeInfo, "Interface root"), owner?.BaseTagHash ?? (owner is null ? root.TagHash : null)));
        }
        if (changeLabels && labelAsset is null) additions.Add(new(LabelName(document.TagHash), labelBytes));
        workspace.ApplyAssetTransaction(additions, allocated =>
        {
            var hierarchyBytes = mutation.Hierarchy.ToArray();
            Dictionary<uint, byte[]> edits = [];
            if (isolate) BinaryPrimitives.WriteUInt32LittleEndian(hierarchyBytes.AsSpan(28), Tag(allocated[0]));
            else edits.Add(root.TagHash, mutation.Resource);
            edits.Add(document.TagHash, hierarchyBytes);
            return new ProjectAssetEdits(edits, changeLabels && labelAsset is not null ? new Dictionary<string, byte[]> { [labelAsset.Id] = labelBytes } : null);
        }, action);
        pendingWidgetSelection = mutation.SelectedNode;
        if (name is not null) { focusComposed = true; isolateComposed = true; }
    }

    private IReadOnlySet<int>? ComposedVisibility(CuiComposition scene)
    {
        if (!isolateComposed || selectedComposed < 0) return null;
        if (ReferenceEquals(isolatedScene, scene) && isolatedSelection == selectedComposed) return isolatedNodes;
        isolatedScene = scene; isolatedSelection = selectedComposed; isolatedNodes = [];
        var children = scene.Nodes.GroupBy(node => node.ParentId).ToDictionary(group => group.Key, group => group.Select(node => node.Id).ToArray());
        Stack<int> pending = new(); pending.Push(selectedComposed);
        while (pending.TryPop(out int node))
            if (isolatedNodes.Add(node) && children.TryGetValue(node, out var nested))
                foreach (int child in nested) pending.Push(child);
        return isolatedNodes;
    }

    private static string RootName(uint tag) => $"cui-root-{tag:X8}.cui";
    private static string LabelName(uint tag) => $"cui-labels-{tag:X8}.json";
    private static uint Tag(AddedProjectAsset asset) => asset.Native!.Tag();
    private static uint WidgetHash(string name) => StringBankWriter.Hash(name.Trim());
}
