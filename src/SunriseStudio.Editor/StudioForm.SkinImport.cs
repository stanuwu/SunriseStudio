using System.Globalization;
using ImGuiNET;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    private bool skinImportDialog, skinReviewPending, skinCreateAsset;
    private string skinImportPath = "", skinImportName = "", skinImportPackage = "0AA0";
    private uint skinImportEntity;
    private int skinTemplateIndex, skinPreparedRevision;
    private long skinPreparedOverlayRevision;
    private StudioProject? skinPreparedProject;
    private PackageCatalog? skinPreparedCatalog;
    private IReadOnlyList<CompatibleSkinTemplate> skinTemplates = [];
    private PreparedCompatibleSkin? preparedSkin;
    private PreparedCompatibleEntity? preparedSkinEntity;
    private string? skinCreatedAssetId;
    private StudioProject? skinCreatedProject;

    private bool CanReplaceSkin => workspace.CanEdit && workspace.Document?.Animation?.Rig.EntityTag is > 0
        && workspace.Document.AddedAssetId is null;

    private void BeginSkinImport()
    {
        if (!CanReplaceSkin) return;
        uint entity = workspace.Document!.Animation!.Rig.EntityTag;
        PickFile("Skinned glTF model|*.gltf;*.glb", path => workspace.Run(() =>
        {
            skinImportPath = path; skinImportName = Path.GetFileNameWithoutExtension(path);
            skinImportEntity = entity; ClearSkinReview(); skinTemplateIndex = 0; skinCreateAsset = false;
            skinImportPackage = workspace.DefaultPackageText;
            workspace.ReadCompatibleSkinTemplates(entity, templates =>
            { skinTemplates = templates; skinImportDialog = true; });
        }));
    }

    private void ClearSkinReview() { preparedSkin = null; preparedSkinEntity = null; }

    /** A review belongs to one project, catalog and source revision. */
    private void CaptureSkinReviewIdentity()
    {
        skinPreparedRevision = workspace.ProjectRevision;
        skinPreparedOverlayRevision = workspace.Catalog!.OverlayRevision;
        skinPreparedProject = workspace.Project;
        skinPreparedCatalog = workspace.Catalog;
    }

    private bool SkinReviewIdentityMatches() => skinPreparedRevision == workspace.ProjectRevision
        && ReferenceEquals(skinPreparedProject, workspace.Project) && ReferenceEquals(skinPreparedCatalog, workspace.Catalog)
        && skinPreparedOverlayRevision == workspace.Catalog?.OverlayRevision;

    /** The transaction refresh finishes before navigation starts its own document worker. */
    private void ApplySkinEntity(PreparedCompatibleEntity entity)
    {
        var project = workspace.Project!;
        workspace.Run(() =>
        {
            var added = workspace.ApplyAssetTransaction(entity.Additions,
                _ => CompatibleSkinnedMesh.EntityEdits(project, workspace.Catalog!, entity), "Created skinned entity");
            skinCreatedAssetId = added[^1].Id; skinCreatedProject = project;
        });
    }

    /** Creation and replacement publish their entire native group in one undoable action. */
    private void DrawSkinImportDialog()
    {
        if (skinCreatedAssetId is { } created && !workspace.IsBusy)
        {
            skinCreatedAssetId = null;
            if (ReferenceEquals(skinCreatedProject, workspace.Project)
                && workspace.Project!.AddedAssets.Any(asset => asset.Id == created))
                workspace.Run(() => workspace.SelectAdded(created));
            skinCreatedProject = null;
        }
        if (skinReviewPending && !workspace.IsBusy) { skinReviewPending = false; skinImportDialog = true; }
        if (skinImportDialog) { ImGui.OpenPopup("Import skinned model"); skinImportDialog = false; }
        CenterModal(540);
        if (!ImGui.BeginPopupModal("Import skinned model", ImGuiWindowFlags.AlwaysAutoResize)) return;
        if ((preparedSkin is not null || preparedSkinEntity is not null) && !SkinReviewIdentityMatches()) ClearSkinReview();
        if (skinTemplates.Count == 0 || (uint)skinTemplateIndex >= skinTemplates.Count)
        { ImGui.CloseCurrentPopup(); ImGui.EndPopup(); return; }
        ImGui.TextUnformatted(Path.GetFileName(skinImportPath));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(skinImportPath);
        ImGui.TextDisabled("Keeps rig, clips and material");
        StudioUi.Tip(StudioUi.MayNotWork);
        var selected = skinTemplates[skinTemplateIndex];
        bool canCreate = CompatibleSkinnedMesh.SupportsEntityCreation(skinImportEntity, selected.ModelTag);
        if (ImGui.RadioButton("Replace model", !skinCreateAsset)) { skinCreateAsset = false; ClearSkinReview(); }
        ImGui.SameLine(); ImGui.BeginDisabled(!canCreate);
        if (ImGui.RadioButton("Create asset", skinCreateAsset)) { skinCreateAsset = true; ClearSkinReview(); }
        ImGui.EndDisabled();
        if (!canCreate && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Create asset supports the installed Hob rig profile");
        string Label(int index) => $"{workspace.DisplayName(skinTemplates[index].ModelTag)} / Part {index + 1}";
        ImGui.SetNextItemWidth(470 * scale);
        if (ImGui.BeginCombo("Template", Label(skinTemplateIndex)))
        {
            for (int index = 0; index < skinTemplates.Count; index++)
            {
                if (ImGui.Selectable(Label(index), index == skinTemplateIndex))
                {
                    skinTemplateIndex = index; ClearSkinReview();
                    if (!CompatibleSkinnedMesh.SupportsEntityCreation(skinImportEntity, skinTemplates[index].ModelTag))
                        skinCreateAsset = false;
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(workspace.DisplayName(skinTemplates[index].MaterialTag));
            }
            ImGui.EndCombo();
        }
        ImGui.SetNextItemWidth(470 * scale);
        if (ImGui.InputText("Name", ref skinImportName, 256)) ClearSkinReview();
        ImGui.SetNextItemWidth(100 * scale);
        if (ImGui.InputText("Package ID", ref skinImportPackage, 4, ImGuiInputTextFlags.CharsHexadecimal)) ClearSkinReview();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Unused package for the new native entries");
        bool valid = ushort.TryParse(skinImportPackage, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort package)
            && PackageWriter.IsNewPackageId(package) && workspace.Catalog?.ContainsInstalledPackage(package) == false
            && !string.IsNullOrWhiteSpace(skinImportName);
        var reviewed = preparedSkinEntity?.Skin ?? preparedSkin;
        if (reviewed is not null)
        {
            ImGui.Separator();
            ImGui.TextUnformatted($"{reviewed.VertexCount:N0} vertices / {reviewed.TriangleCount:N0} triangles / {reviewed.ConsumedBones} bones");
            if (preparedSkinEntity is not null)
            {
                ImGui.TextUnformatted($"New entity / {reviewed.RetainedSourceParts.Count} render passes / 9 entries");
                ImGui.TextDisabled("Uses the Hob rig and clips");
                ImGui.TextDisabled("Saved as deltas");
            }
            else
            {
                ImGui.TextUnformatted($"Replaces all model geometry / {reviewed.RetainedSourceParts.Count} render passes");
                if (reviewed.RemovedSourceParts > 0) ImGui.TextColored(StudioTheme.Accent, $"Removes {reviewed.RemovedSourceParts:N0} template parts");
                ImGui.TextDisabled("Model and buffers saved as deltas");
            }
        }
        if (workspace.Error.Length != 0)
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 470 * scale);
            ImGui.TextUnformatted(workspace.Error); ImGui.PopTextWrapPos();
        }
        ImGui.Spacing();
        ImGui.BeginDisabled(!workspace.CanEdit || !valid);
        string button = reviewed is null ? "Review import" : skinCreateAsset ? "Create asset" : "Replace model";
        if (ImGui.Button(button, new(170 * scale, 0)))
        {
            if (reviewed is null)
            {
                selected = skinTemplates[skinTemplateIndex];
                int first = workspace.Project!.AddedAssets.Where(asset => asset.Native?.PackageId == package)
                    .Select(asset => asset.Native!.EntryIndex ?? -1).DefaultIfEmpty(-1).Max() + 1;
                var options = new CompatibleSkinReplacementOptions(skinImportEntity, selected.ModelTag,
                    selected.PartOffset, skinImportName, package, first);
                CaptureSkinReviewIdentity();
                if (skinCreateAsset)
                    workspace.Run(() => workspace.PrepareCompatibleSkinEntity(skinImportPath, options, prepared =>
                    { if (SkinReviewIdentityMatches()) preparedSkinEntity = prepared; }));
                else
                    workspace.Run(() => workspace.PrepareCompatibleSkin(skinImportPath, options, prepared =>
                    { if (SkinReviewIdentityMatches()) preparedSkin = prepared; }));
                skinReviewPending = workspace.IsBusy;
            }
            else if (preparedSkinEntity is { } entity)
            {
                ApplySkinEntity(entity);
                if (workspace.Error.Length == 0) { ClearSkinReview(); ImGui.CloseCurrentPopup(); }
            }
            else if (preparedSkin is { } replacement)
            {
                workspace.Run(() => workspace.ApplyAssetTransaction(replacement.Additions,
                    _ => CompatibleSkinnedMesh.Edits(workspace.Catalog!, replacement), "Replaced skinned model"));
                if (workspace.Error.Length == 0) { ClearSkinReview(); ImGui.CloseCurrentPopup(); }
            }
        }
        ImGui.EndDisabled(); ImGui.SameLine();
        if (ImGui.Button("Cancel", new(110 * scale, 0))) { ClearSkinReview(); skinReviewPending = false; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }
}
