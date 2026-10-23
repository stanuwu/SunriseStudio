using ImGuiNET;
using SunriseStudio.Authoring;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Packages;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    private readonly CreationDialog creation = new();
    private bool pendingInvestmentEdit;
    /** A Duplicate source that is opening; Duplicate copies the open document. */
    private uint? pendingDuplicate;
    private (StudioDocument? Document, int Assets, string Key) documentKey;

    /**
     * Runs a route, or opens the type's workflow.
     * @param source Clone base; preset into the workflow's CloneSource. An item's display record gives its item.
     */
    private void StartCreation(CreationType type, uint? source = null, string sourceLabel = "")
    {
        if (!type.Available || !workspace.CanEdit) return;
        if (source is { } picked && workspace.CloneBaseOf(picked) is var owner && owner != picked)
        { source = owner; sourceLabel = workspace.DisplayName(owner); }
        if (type.Name == CreationTypes.Duplicate)
        {
            if (source is { } tag && workspace.Document?.Entry.TagHash != tag) { pendingDuplicate = tag; workspace.Open(tag); }
            else BeginDuplicate();
            return;
        }
        if (type.Factory is not { } factory) return;
        workspace.Run(() =>
        {
            var workflow = factory(workspace);
            if (source is { } tag && workflow.CloneSource is { } field)
            { field.Tag = tag; field.TagLabel = sourceLabel.Length != 0 ? sourceLabel : workspace.DisplayName(tag); }
            creation.Open(workflow);
        });
    }

    /** Clone of a key, based on the open document when it has that key and the type accepts it. */
    private void StartClone(string key)
    {
        var document = workspace.Document;
        var clone = CreationTypes.For(key, CreationMode.Clone);
        bool fits = document is not null && (document.AddedAssetId is null || clone.ClonesProject) && DocumentKey(document) == key;
        StartCreation(clone, fits ? document!.Entry.TagHash : null, fits ? document!.Name : "");
    }

    private void NewMissionScript() { missionGraph.RequestNewScript(); focusMissionGraph = true; }

    /** Category or asset key of a document; KeyFor reruns only when the document or the rows change. */
    private string DocumentKey(StudioDocument document)
    {
        if (!ReferenceEquals(documentKey.Document, document) || documentKey.Assets != workspace.AssetRevision)
            documentKey = (document, workspace.AssetRevision, CreationTypes.KeyFor(document.Entry, workspace));
        return documentKey.Key;
    }

    /** The caller has checked that the project can be edited. Ids keep their old names; preferences store them. */
    private void HandleCreateShortcuts()
    {
        if (ShortcutInput.Pressed(ShortcutActions.CreateTexture))
            StartCreation(CreationTypes.For(CreationTypes.TextureKey, CreationMode.New));
        if (ShortcutInput.Pressed(ShortcutActions.CreateStaticProp))
            StartCreation(CreationTypes.For(CreationTypes.StaticPropKey, CreationMode.New));
        if (ShortcutInput.Pressed(ShortcutActions.CloneMaterial)) StartClone(CreationTypes.MaterialKey);
        if (ShortcutInput.Pressed(ShortcutActions.CloneWeapon)) StartClone(InvestmentItemKinds.Weapons);
        if (ShortcutInput.Pressed(ShortcutActions.CloneArmor)) StartClone(InvestmentItemKinds.Armor);
        if (ShortcutInput.Pressed(ShortcutActions.ClonePlug)) StartClone(InvestmentItemKinds.Mods);
        if (ShortcutInput.Pressed(ShortcutActions.OpenInterfaceList)) { focusAssetBrowser = true; OpenBrowserCategory(StudioCategories.CuiName); }
        if (ShortcutInput.Pressed(ShortcutActions.CreateMissionScript)) NewMissionScript();
    }

    /** Why creation cannot start now; null when it can. */
    private string? CreationRefusal(CreationType type) =>
        !type.Available ? type.Blocker
        : workspace.Project is null ? "Create or open a project first."
        : !workspace.CanEdit ? "Working" : null;

    /**
     * An icon for one creation type; a disabled icon shows why.
     * @param refusal Why this source cannot be used; null when it can.
     * @return True when clicked.
     */
    private bool CreationIcon(string id, StudioIcon icon, string verb, CreationType type, string? refusal = null)
    {
        string? reason = CreationRefusal(type) ?? refusal;
        string tip = reason ?? (type.Summary.Length == 0 ? verb : $"{verb}: {type.Summary}");
        return StudioUi.IconButton(id, icon, tip, reason is null);
    }

    /** Width of an icon, so a row can be right-aligned before it is drawn. */
    private static float CreationIconWidth() => ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X;

    /** A menu item for one creation type; its tooltip is the refusal or the summary. */
    private void CreationMenuItem(string label, CreationType type, Action start)
    {
        string? reason = CreationRefusal(type);
        if (ImGui.MenuItem(label + (reason is null ? "..." : ""), "", false, reason is null)) start();
        StudioUi.Tip(reason ?? type.Summary);
    }

    /** New, Clone and Edit for the open document, on one line; dictionary lookups only. */
    private void DrawCreationBar(StudioDocument document)
    {
        string key = DocumentKey(document);
        bool added = document.AddedAssetId is not null;
        var create = CreationTypes.For(key, CreationMode.New);
        if (CreationIcon("doc-new", StudioIcon.Plus, "New", create)) StartCreation(create);
        ImGui.SameLine();
        var clone = CreationTypes.For(key, CreationMode.Clone);
        if (CreationIcon("doc-clone", StudioIcon.Copy, "Clone", clone, added && !clone.ClonesProject ? "Clone an installed one" : null))
            StartCreation(clone, document.Entry.TagHash, document.Name);
        ImGui.SameLine();
        var route = CreationTypes.RouteFor(document);
        string? reason = workspace.Project is null ? "Create or open a project first."
            : route == CreationTypes.EditRoute.None ? "No edit workflow for this asset."
            : !workspace.CanEdit ? "Working" : null;
        string tip = reason ?? (added ? "Edit in workflow" : "Edit in workflow\nStores a delta of the installed asset");
        if (StudioUi.IconButton("doc-edit", StudioIcon.Edit, tip, reason is null)) EditInWorkflow();
    }

    private void OpenPerkBehaviorPicker() => workspace.Run(() =>
        creation.Open(new PerkBehaviorPickerWorkflow(workspace)));

    /** Items, vendors, materials and textures open their workflow; CUI screens and perks open their page. */
    private void EditInWorkflow()
    {
        if (workspace.Document is not { } document) return;
        switch (CreationTypes.RouteFor(document))
        {
            case CreationTypes.EditRoute.Item or CreationTypes.EditRoute.Vendor:
                if (workspace.Investment is null) { pendingInvestmentEdit = true; workspace.Run(workspace.LoadInvestment); }
                else OpenInvestmentEdit();
                break;
            case CreationTypes.EditRoute.Material:
                workspace.Run(() => creation.Open(new MaterialWorkflow(workspace, document)));
                break;
            case CreationTypes.EditRoute.Texture:
                workspace.Run(() => creation.Open(new TextureWorkflow(workspace, document)));
                break;
            case CreationTypes.EditRoute.CuiPage:
                ShowAssetPage("sunrise.cui");
                break;
            case CreationTypes.EditRoute.PerkPage:
                ShowAssetPage("sunrise.perk-behavior");
                break;
        }
    }

    private void OpenInvestmentEdit()
    {
        if (workspace.Investment is not { } investment || workspace.Document is not { } document) return;
        uint tag = document.Entry.TagHash;
        if (CreationTypes.RouteFor(document) == CreationTypes.EditRoute.Vendor)
        {
            // A vendor opens from either of its paired records.
            if (investment.Vendors.FirstOrDefault(row => row.Tag == tag || row.DisplayTag == tag) is not { } vendor)
            { workspace.Error = "This record is not in the vendor index."; return; }
            workspace.Run(() => creation.Open(new VendorWorkflow(workspace, vendor)));
            return;
        }
        if (investment.FindItem(tag) is not { } item)
        { workspace.Error = "This record is not in the investment index."; return; }
        string category = workspace.CategoryNameOf(item.Tag) ?? InvestmentItemKinds.Other;
        workspace.Run(() => creation.Open(new ItemWorkflow(workspace, category, item)));
    }

    private void DrawCreation()
    {
        if (pendingInvestmentEdit && !workspace.IsBusy) { pendingInvestmentEdit = false; OpenInvestmentEdit(); }
        if (pendingDuplicate is { } duplicate && !workspace.IsBusy)
        {
            pendingDuplicate = null;
            if (workspace.Document?.Entry.TagHash == duplicate) BeginDuplicate();
        }
        if (!workspace.IsBusy && workspace.TakeWeaponAssetReimportRequest() is { } weapon)
            workspace.Run(() => creation.Open(new WeaponAssetReimportWorkflow(workspace, weapon)));
        if (!workspace.IsBusy && workspace.TakePendingFiringEffects() is { } firing)
            workspace.Run(() => workspace.ApplyFiringEffects(firing.Weapon, firing.Path, onlyIfEmitters: true));
        if (!workspace.IsBusy && workspace.TakeWeaponArmsExportRequest() is { } arms)
            SaveFile("glTF binary|*.glb", "first-person-arms.glb", path => workspace.Execute("Exporting first-person arms",
                () => WeaponArmsRig.Export(workspace.Catalog!, [("fire", arms.Fire), ("reload", arms.Reload)], path)));
        creation.Draw(workspace, scale, PickFile, AdoptCreation);
    }

    /** Opens what a finished workflow names, once the workspace is idle. */
    private void AdoptCreation(CreationFollowUp next)
    {
        if (next.AddedAssetId is { } id && workspace.Project?.AddedAssets.Any(asset => asset.Id == id) == true)
            workspace.Run(() => workspace.SelectAdded(id));
        else if (next.Tag is { } tag) workspace.Run(() => workspace.Select(tag));
        if (next.Page is { } page) ShowAssetPage(page);
        if (next.MapGroup is { } group && workspace.Project?.AddedAssets.FirstOrDefault(asset => asset.Group == group
            && asset.Native?.Name == "map:" + group + ":root") is { Native: { } root })
        { workspace.OpenMap(root.Tag()); focusWorldScene = true; }
    }
}
