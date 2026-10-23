using System.Numerics;
using ImGuiNET;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using ImGuiIcons = SunriseStudio.Viewport.ImGuiIcons;
using StudioIcon = SunriseStudio.Viewport.StudioIcon;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    private bool browserDirty = true;
    private double searchChanged;
    private int projectRevision;
    private CancellationTokenSource? filterCancellation;
    private Task<BrowserAsset[]>? filterWork;
    private int filteredNameRevision;

    private const string AllCategories = "All categories", AllTypes = "All types";
    private const string Dot = " \u00B7 ";
    private string[] browserKinds = [];
    /** True while the browser lists the assets that have no category viewer. */
    private bool otherAssets;
    private bool focusAssetBrowser;

    /** The category whose list is shown; null on the home view and in All other assets. */
    private StudioCategory? OpenCategory => category == AllCategories ? null : StudioCategories.Find(category);

    /** A kind filter such as "Entity / Prop" selects that category and type. */
    private static (string Category, string Type) SplitFilter(string kind)
    {
        var (part, type) = AssetCategories.SplitKind(kind);
        return (part, type.Length == 0 ? AllTypes : type);
    }

    private void RebuildBrowser()
    {
        assetRevision = workspace.AssetRevision;
        if (workspace.Assets.Length == 0 && workspace.IsBusy) return;
        browserKinds = workspace.BrowserKinds;
        // The category menu of All other assets lists only kinds with no viewer.
        categories = [AllCategories, .. browserKinds.Select(kind => AssetCategories.SplitKind(kind).Category)
            .Where(name => StudioCategories.Find(name) is null)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        packageNames = workspace.BrowserPackages;
        if (!categories.Contains(category) && OpenCategory is null) category = AllCategories;
        if (!packageNames.Contains(package)) package = packageNames[0];
        RefreshAssetTypes();
        FilterBrowser();
    }

    /** Types follow the chosen category; a type that left it resets to all types. */
    private void RefreshAssetTypes()
    {
        assetTypes = [AllTypes, .. browserKinds.Select(AssetCategories.SplitKind)
            .Where(parts => parts.Category == category && parts.Type.Length != 0)
            .Select(parts => parts.Type).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        if (!assetTypes.Contains(assetType)) assetType = AllTypes;
    }

    /**
     * Search runs after input settles; scrolling never scans the full catalog.
     * A category view scans only that category's rows; the package filter belongs to All other assets.
     */
    private void FilterBrowser()
    {
        if (workspace.Project is null) changesOnly = false;
        filterCancellation?.Cancel();
        filterCancellation?.Dispose();
        filterCancellation = new();
        var token = filterCancellation.Token;
        string[] words = BrowserRowFilter.Words(search);
        var changes = workspace.Project?.Changes.Select(change => change.TagHash)
            .Concat(workspace.CompiledObjects.Assets.Keys)
            .Concat(workspace.Project.ConfigBindings.Select(binding => binding.TagHash))
            .Concat(workspace.Project.AddedAssets.Where(asset => asset.Native is not null)
                .Select(asset => asset.Native!.Tag()))
            .ToHashSet() ?? [];
        var open = OpenCategory;
        string? selectedCategory = category == AllCategories || open is not null ? null : category;
        string? selectedType = category == AllCategories || assetType == AllTypes ? null : assetType;
        bool otherView = open is null && (otherAssets || category != AllCategories);
        // The package menu shows only in All other assets, so it filters only there.
        string? selectedPackage = !otherView || package == "All packages" ? null : package;
        bool leftoversOnly = open is null && otherAssets;
        bool modifiedOnly = changesOnly;
        var rows = open is null ? workspace.Assets : workspace.CategoryRows.GetValueOrDefault(open.Name) ?? [];
        filteredNameRevision = workspace.NameRevision;
        browserDirty = false;
        if (words.Length == 0 && selectedCategory is null && selectedType is null && selectedPackage is null
            && !modifiedOnly && !leftoversOnly)
        {
            filtered = rows; filterWork = null;
            return;
        }
        bool Keep(BrowserAsset row) =>
            (selectedCategory is null || row.Category == selectedCategory)
            && (selectedType is null || row.Type == selectedType)
            && (selectedPackage is null || row.Entry.PackageName == selectedPackage)
            && !(leftoversOnly && row.InCategory)
            && (!modifiedOnly || changes.Contains(row.Entry.TagHash));
        (var inline, filterWork) = BrowserRowFilter.Start(rows, words, Keep, token);
        if (inline is not null) filtered = inline;
    }

    /** Shows one category's list; the search text stays and now filters inside it. */
    private void OpenBrowserCategory(string name)
    {
        category = name; assetType = AllTypes; otherAssets = false;
        RefreshAssetTypes(); FilterBrowser();
    }

    /** Back to the home view of categories; the search and Changed only clear, since either one shows Results. */
    private void CloseBrowserCategory()
    {
        category = AllCategories; assetType = AllTypes; otherAssets = false; changesOnly = false;
        search = ""; browserDirty = false;
        RefreshAssetTypes(); FilterBrowser();
    }

    /** View > Items: the Assets tab on the Weapons list, or the home view when there is none. */
    private void ShowItems()
    {
        focusAssetBrowser = true;
        if (StudioCategories.Find("Weapons") is not null) OpenBrowserCategory("Weapons");
        else CloseBrowserCategory();
    }

    private unsafe void DrawBrowser(Vector2 position, Vector2 size)
    {
        BeginPanel("Asset browser", position, size);
        TrackAreaFocus();
        if (ImGui.BeginTabBar("browser-sections"))
        {
            if (ImGui.BeginTabItem("Maps")) { DrawMapBrowser(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Missions")) { DrawMissionBrowser(); ImGui.EndTabItem(); }
            if (BeginEditorTab("Hierarchy", focusHierarchy))
            { focusHierarchy = false; DrawWorldHierarchy(); ImGui.EndTabItem(); }
            if (BeginEditorTab("Assets", focusAssetBrowser || options.Category is not null && !categoryTabSelected))
            { categoryTabSelected = true; focusAssetBrowser = false; DrawAssetBrowserContents(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
        ImGui.End();
    }

    private bool categoryTabSelected;

    /** Home view of categories, one category's list, All other assets, or search results; one search box for all. */
    private void DrawAssetBrowserContents()
    {
        PollAssetFilter();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##assetSearch", "Search items, enemies and assets", ref search, 256))
        { browserDirty = true; searchChanged = clock.Elapsed.TotalSeconds; }
        if (browserDirty && clock.Elapsed.TotalSeconds - searchChanged > 0.2) FilterBrowser();
        // Drawn before the home view, so the home view can list every changed asset.
        if (workspace.Project is not null && ImGui.Checkbox("Changed only", ref changesOnly)) FilterBrowser();
        if (OpenCategory is { } open)
        {
            DrawListHeader(open.Label, open.Tooltip);
            if (open.Name == InvestmentItemKinds.Bounties) StudioUi.Wip("Some rows here are not bounties.");
            DrawCategoryCreation(open.Name);
        }
        else if (otherAssets || category != AllCategories)
        { DrawListHeader("All other assets", "Assets with no category viewer"); DrawOtherCreation(); }
        else if (search.Length != 0 || changesOnly) DrawListHeader("Results", "Matches in every category");
        else { DrawCategoryHome(); return; }
        if (OpenCategory is null && (otherAssets || category != AllCategories)) DrawOtherFilters();
        else if (OpenCategory is not null) DrawTypeFilter(-1);
        ImGui.Separator();
        DrawAssetRows();
    }

    /** One line: a Back icon to the home view, the title and the row count. */
    private void DrawListHeader(string title, string tooltip)
    {
        if (StudioUi.IconButton("browser-home", StudioIcon.Back, "All categories")) CloseBrowserCategory();
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(title);
        StudioUi.Tip(tooltip);
        ImGui.SameLine(); ImGui.TextDisabled($"{filtered.Length:N0}");
        if (filterWork is not null) { ImGui.SameLine(); ImGui.TextDisabled("Searching"); }
    }

    /** Moves the cursor so a row this wide ends at the right edge; a row that does not fit takes the next line. */
    private static void AlignRight(float width)
    {
        ImGui.SameLine();
        if (ImGui.GetContentRegionAvail().X < width) ImGui.NewLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, ImGui.GetContentRegionAvail().X - width));
    }

    /** New and Clone of a category, right-aligned on its header line. */
    private void DrawCategoryCreation(string name)
    {
        var create = CreationTypes.For(name, CreationMode.New);
        var clone = CreationTypes.For(name, CreationMode.Clone);
        AlignRight(2 * CreationIconWidth());
        if (CreationIcon("list-new", StudioIcon.Plus, "New", create)) StartCreation(create);
        ImGui.SameLine();
        if (CreationIcon("list-clone", StudioIcon.Copy, "Clone", clone)) StartClone(name);
    }

    /** All other assets has no category, so its New lists the plain asset types. */
    private void DrawOtherCreation()
    {
        AlignRight(ImGui.GetFrameHeight());
        if (StudioUi.IconButton("other-new", StudioIcon.Plus, workspace.Project is null ? "Create or open a project first." : "New",
            workspace.CanEdit))
            ImGui.OpenPopup("other-new-menu");
        if (!ImGui.BeginPopup("other-new-menu")) return;
        var texture = CreationTypes.For(CreationTypes.TextureKey, CreationMode.New);
        CreationMenuItem("Texture", texture, () => StartCreation(texture));
        var prop = CreationTypes.For(CreationTypes.StaticPropKey, CreationMode.New);
        CreationMenuItem("Static prop", prop, () => StartCreation(prop));
        CreationMenuItem("Clone material", CreationTypes.For(CreationTypes.MaterialKey, CreationMode.Clone),
            () => StartClone(CreationTypes.MaterialKey));
        ImGui.Separator();
        var activity = CreationTypes.For(CreationTypes.ActivityKey, CreationMode.New);
        CreationMenuItem("Activity", activity, () => StartCreation(activity));
        foreach (var type in CreationTypes.Blocked) CreationMenuItem(type.Name, type, () => { });
        ImGui.EndPopup();
    }

    /** All other assets keeps the category, type and package menus over the kinds with no viewer. */
    private void DrawOtherFilters()
    {
        float half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2;
        ImGui.SetNextItemWidth(half);
        if (ImGui.BeginCombo("##category", category))
        {
            foreach (string value in categories)
                if (ImGui.Selectable(value, value == category))
                { category = value; assetType = AllTypes; otherAssets = true; RefreshAssetTypes(); FilterBrowser(); }
            ImGui.EndCombo();
        }
        StudioUi.Tip("Category");
        ImGui.SameLine();
        DrawTypeFilter(-1);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##package", package))
        {
            foreach (string value in packageNames)
                if (ImGui.Selectable(value, value == package)) { package = value; FilterBrowser(); }
            ImGui.EndCombo();
        }
        StudioUi.Tip("Package");
    }

    private void DrawTypeFilter(float width)
    {
        ImGui.SetNextItemWidth(width);
        ImGui.BeginDisabled(assetTypes.Length < 2);
        if (ImGui.BeginCombo("##asset-type", assetType))
        {
            foreach (string value in assetTypes)
                if (ImGui.Selectable(value, value == assetType)) { assetType = value; FilterBrowser(); }
            ImGui.EndCombo();
        }
        ImGui.EndDisabled();
        StudioUi.Tip("Type");
    }

    /** Categories with rows, grouped; counts come from the worker, so drawing scans no rows. */
    private void DrawCategoryHome()
    {
        ImGui.BeginChild("category-home", Vector2.Zero, ImGuiChildFlags.None);
        string? group = null;
        int inCategories = 0;
        foreach (var item in StudioCategories.All)
        {
            int count = workspace.CategoryCounts.GetValueOrDefault(item.Name);
            inCategories += count;
            if (count == 0) continue;
            if (item.Group != group) { group = item.Group; ImGui.SeparatorText(group.ToUpperInvariant()); }
            if (CategoryRow(item.Name, item.Icon, item.Label, count, item.Tooltip)) OpenBrowserCategory(item.Name);
        }
        if (group is not null) ImGui.Separator();
        if (CategoryRow("other-assets", StudioIcon.Folder, "All other assets", workspace.Assets.Length - inCategories,
            "Assets with no category viewer"))
        { otherAssets = true; FilterBrowser(); }
        ImGui.EndChild();
    }

    /** A full-width row: icon, label, and the count on the right. */
    private bool CategoryRow(string id, StudioIcon icon, string label, int count, string tooltip)
    {
        ImGui.PushID(id);
        float width = ImGui.GetContentRegionAvail().X, height = ImGui.GetFrameHeight();
        var start = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.Selectable("##category-row", false, ImGuiSelectableFlags.None, new(0, height));
        StudioUi.Tip(tooltip);
        var draw = ImGui.GetWindowDrawList();
        float line = ImGui.GetTextLineHeight(), textTop = start.Y + (height - line) / 2;
        uint text = ImGui.GetColorU32(ImGuiCol.Text);
        ImGuiIcons.Draw(draw, icon, new(start.X + 4 * scale, textTop), line, text);
        draw.AddText(new(start.X + 12 * scale + line, textTop), text, label);
        string number = count.ToString("N0");
        draw.AddText(new(start.X + width - ImGui.CalcTextSize(number).X - 4 * scale, textTop),
            ImGui.GetColorU32(ImGuiCol.TextDisabled), number);
        ImGui.PopID();
        return clicked;
    }

    /** Category rows show rarity colour and "Type . package", with no type that repeats the name; other rows show their kind. */
    private unsafe void DrawAssetRows()
    {
        bool inCategoryList = OpenCategory is not null;
        ImGui.BeginChild("asset-list", Vector2.Zero, ImGuiChildFlags.None);
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        float rowHeight = ImGui.GetTextLineHeightWithSpacing() * 2 + 7 * scale;
        clipper.Begin(filtered.Length, rowHeight);
        while (clipper.Step())
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var row = filtered[i];
                ImGui.PushID(unchecked((int)row.Entry.TagHash));
                var start = ImGui.GetCursorScreenPos();
                bool active = row.Entry.TagHash == workspace.Document?.Entry.TagHash;
                if (ImGui.Selectable("##asset", active, ImGuiSelectableFlags.None, new(0, rowHeight - 6 * scale)))
                    workspace.Open(row.Entry.TagHash);
                bool hovered = ImGui.IsItemHovered();
                var draw = ImGui.GetWindowDrawList();
                var textStart = start + new Vector2(7 * scale, 2 * scale);
                var end = start + new Vector2(ImGui.GetContentRegionAvail().X - 4, rowHeight);
                draw.PushClipRect(start, end, true);
                string name = row.Name;
                draw.AddText(textStart, row.Tier >= 0 ? ImGui.GetColorU32(StudioTheme.Rarity(row.Tier))
                    : ImGui.GetColorU32(ImGuiCol.Text), name);
                string type = row.Type == name ? "" : row.Type;
                // Results mix categories, so their rows name the full kind.
                string detail = !row.InCategory ? row.Kind : !inCategoryList ? row.Kind + Dot + row.Entry.PackageName
                    : type.Length == 0 ? row.Entry.PackageName : type + Dot + row.Entry.PackageName;
                draw.AddText(textStart + new Vector2(0, ImGui.GetTextLineHeightWithSpacing()),
                    ImGui.GetColorU32(ImGuiCol.TextDisabled), detail);
                if (workspace.IsModified(row.Entry.TagHash))
                    draw.AddCircleFilled(new(end.X - 5, start.Y + 8), 3, ImGui.GetColorU32(StudioTheme.Accent));
                draw.PopClipRect();
                if (hovered)
                    ImGui.SetTooltip($"{name}\n{row.Entry.PackageName}\n{row.Entry.TagHash:X8}");
                if (ImGui.BeginPopupContextItem("asset-context"))
                {
                    if (ImGui.MenuItem("Open")) workspace.Open(row.Entry.TagHash);
                    if (row.InCategory && ImGui.MenuItem("Open as asset")) workspace.Open(row.Entry.TagHash, StudioWorkspace.PlainOpen);
                    var clone = CreationTypes.For(CreationTypes.KeyFor(row.Entry, workspace), CreationMode.Clone);
                    if (clone.Available && (!workspace.IsProjectTag(row.Entry.TagHash) || clone.ClonesProject))
                        CreationMenuItem("Clone", clone, () => StartCreation(clone, row.Entry.TagHash, row.Name));
                    if (ImGui.MenuItem("Copy tag")) ImGui.SetClipboardText($"{row.Entry.TagHash:X8}");
                    ImGui.EndPopup();
                }
                ImGui.PopID();
            }
        clipper.End(); clipper.Destroy();
        ImGui.EndChild();
    }

    private void PollAssetFilter()
    {
        if (filteredNameRevision != workspace.NameRevision && search.Length != 0)
        { browserDirty = true; filteredNameRevision = workspace.NameRevision; }
        if (filterWork is not { IsCompleted: true } task) return;
        filterWork = null;
        try { filtered = task.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { workspace.Error = error.Message; }
    }

    /** Collision, group or object details when one is selected; else the open document, its part and property. */
    private void DrawInspector(Vector2 position, Vector2 size)
    {
        BeginPanel("Inspector", position, size);
        TrackAreaFocus();
        ImGui.TextUnformatted("INSPECTOR");
        ImGui.Separator();
        if (collisionInspected is not null && DrawCollisionInspector()) { ImGui.End(); return; }
        if (selectedGroupId is not null && DrawGroupInspector()) { ImGui.End(); return; }
        if (selectedObject is not null && DrawSelectedObjectInspector()) { ImGui.End(); return; }
        if (workspace.Document is { } document)
        {
            ImGui.TextWrapped(document.Name);
            ImGui.TextDisabled(document.Inspection.Kind == "Binary" ? StudioWorkspace.AssetKind(document.Entry)
                : document.Inspection.Kind);
            ImGui.Spacing();
            ImGui.TextDisabled("Package"); ImGui.TextWrapped(document.Entry.PackageName);
            ImGui.TextDisabled("Tag"); ImGui.SameLine(); ImGui.TextUnformatted($"{document.Entry.TagHash:X8}");
            ImGui.TextDisabled("Size"); ImGui.SameLine(); ImGui.TextUnformatted(FormatBytes(document.Bytes.Length));
            if (ImGui.CollapsingHeader("Native metadata"))
                StudioUi.Facts("native-metadata", [
                    ("Reference", $"{document.Entry.Reference:X8}", ""),
                    ("Type", $"{document.Entry.Type} / {document.Entry.SubType}", ""),
                    ("Entry", $"{document.Entry.EntryIndex}", "")]);
            ImGui.Separator();
            DrawAssetActions(document);
            if (workspace.Project?.ContainsEdit(document.Entry.TagHash) == true)
            {
                ImGui.TextColored(StudioTheme.Accent, "Modified");
                ImGui.BeginDisabled(workspace.IsBusy);
                if (ImGui.Button("Revert to original", new(-1, 0)))
                    workspace.Run(() => workspace.Revert(document.Entry.TagHash));
                ImGui.EndDisabled();
            }
            ImGui.Spacing();
            if (selectedMesh is { } part && document.Scene?.Instances.Contains(part) == true)
            {
                ImGui.SeparatorText("PART");
                ImGui.TextWrapped(part.Name);
                var partFacts = new List<(string, string, string)>
                {
                    ("Part", $"{part.Mesh.PartIndex}, LOD {part.Mesh.LodLevel}", ""),
                    ("Vertices", $"{part.Mesh.Vertices.Length:N0}", ""),
                    ("Triangles", $"{part.Mesh.Indices.Length / 3:N0}", "")
                };
                if (part.Mesh.Native is { } partNative)
                {
                    partFacts.Add(("Stage", SunriseStudio.Scene.RenderNames.GetStageName(partNative.RenderStage)
                        ?? $"{partNative.RenderStage}", $"Stage {partNative.RenderStage}"));
                    partFacts.Add(("Blend", SunriseStudio.Viewport.NativePartStates.Label(partNative), ""));
                }
                StudioUi.Facts("part-facts", partFacts);
                if (ImGui.Button("Frame part")) AssetPreviewViewport?.FocusSelection();
                if (part.Mesh.MaterialTag is not (0 or uint.MaxValue))
                {
                    ImGui.TextDisabled("Material");
                    if (ImGui.Selectable(workspace.DisplayName(part.Mesh.MaterialTag)))
                        OpenPartMaterial(part);
                }
            }
            if (selectedProperty is { } property)
            {
                ImGui.SeparatorText("PROPERTY");
                ImGui.TextWrapped(property.Name);
                if (property.Offset >= 0) ImGui.TextDisabled($"Offset  {property.Offset:X8}");
                var configured = workspace.ConfigBindingForProperty(property);
                ImGui.BeginDisabled(!workspace.CanEdit || !property.Editable || configured is not null);
                ImGui.SetNextItemWidth(-1);
                bool enter = ImGui.InputText("##propertyValue", ref propertyValue, 1024,
                    ImGuiInputTextFlags.EnterReturnsTrue);
                if (ImGui.Button("Apply", new(-1, 0)) || enter)
                    workspace.Run(() => workspace.ApplyInspectorProperty(property, propertyValue));
                ImGui.EndDisabled();
                if (configured is not null)
                {
                    ImGui.TextDisabled($"Bound to {configured.Key}");
                    StudioUi.Tip($"Default {workspace.ConfigSetting(configured)?.Default}. Applies after restart.");
                }
                if (!property.Editable) ImGui.TextDisabled("Read only");
            }
            if (document.Scene is { } scene && ImGui.CollapsingHeader("Scene"))
                StudioUi.Facts("scene-facts", [
                    ("Objects", $"{scene.Instances.Count:N0}", ""),
                    ("Markers", $"{scene.Markers.Count:N0}", ""),
                    ("Triangles", $"{scene.Instances.Sum(instance => (long)instance.Mesh.Indices.Length / 3):N0}", "")]);
            if (document.Scene is { } effectScene) DrawEffectControls(effectScene, null);
            if (document.Scene?.Diagnostics.Count > 0 && ImGui.CollapsingHeader("Load report"))
                StudioUi.Facts("load-report", document.Scene.Diagnostics.Take(100));
            if (AssetPreviewViewport is { Scene: { } previewScene } preview && preview.CollisionPossible(previewScene))
                DrawCollisionReport(preview);
            if (document.NativeError.Length != 0 && ImGui.CollapsingHeader("Shader report"))
                DrawErrorRow(document.NativeError);
            if (AssetPreviewViewport is { NativeSceneReady: true } native && ImGui.CollapsingHeader("Shader report##native-scene"))
            {
                if (native.NativeError is { } error) DrawErrorRow(error);
                StudioUi.Facts("shader-parts", [("Prepared parts", $"{native.NativePartCount:N0}", "")]);
                StudioUi.Facts("shader-report", native.NativeDiagnostics.Take(80));
            }
        }
        else ImGui.TextDisabled("No selection");
        ImGui.End();
    }

    /** One icon row; the Assets menu keeps the full names. */
    private void DrawAssetActions(StudioDocument document)
    {
        if (ImGuiIcons.Button("inspector-export", StudioIcon.Export, "Export")) ImGui.OpenPopup("inspector-export");
        if (ImGui.BeginPopup("inspector-export"))
        {
            if (workspace.World is not null && document.Entry.Reference is 0x808091DE or 0x80809994
                && ImGui.MenuItem("World to Blender...", "", false, !workspace.IsBusy)) ExportWorldScene();
            if (ImGui.MenuItem("Full asset to Blender...", "", false, CanExportBundle)) ExportFullAsset();
            MapBundleTooltip();
            if (ImGui.MenuItem("Asset file...")) ExportSelected();
            ImGui.EndPopup();
        }
        string? addedId = document.AddedAssetId;
        bool aliases = addedId is not null
            && workspace.Project?.AddedAssets.Any(asset => asset.Id == addedId && asset.Native is not null) == true;
        bool bindings = document.Entry.Reference == 0x80804825;
        ImGui.BeginDisabled(!workspace.CanEdit);
        ImGui.SameLine();
        if (ImGuiIcons.Button("inspector-import", StudioIcon.Import, addedId is null ? "Import as delta" : "Replace asset"))
            ImportDelta();
        if (aliases || bindings)
        {
            ImGui.SameLine();
            if (ImGui.Button("...##inspector-more", new(0, ImGui.GetFrameHeight()))) ImGui.OpenPopup("inspector-more");
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("More");
        }
        ImGui.EndDisabled();
        if (ImGui.BeginPopup("inspector-more"))
        {
            if (aliases && ImGui.MenuItem("Wide-hash aliases...")) BeginWideHashes(addedId!);
            if (bindings && ImGui.MenuItem("Config bindings...")) BeginConfigBindings();
            ImGui.EndPopup();
        }
    }

    /** One row of the Changes table; Source is the binding, object edit, delta or added asset. */
    private sealed record ChangeRow(string Id, string Label, string Tooltip, string Change, bool Accent,
        string ChangeTooltip, string Value, object Source);

    /** Every input the row text reads; rows rebuild only when one changes. */
    private readonly record struct ChangeRowsKey(StudioProject Project, int Revision, int Names, int Assets,
        ObjectEditCompilation Compiled, object? Mod, object Bindings, object Edits, object Changes, object Added, int Count);

    private ChangeRow[] changeRows = [];
    private ChangeRowsKey? changeRowsKey;

    /** Bindings, object edits, deltas and added assets, in that order; rebuilt only when the key changes. */
    private ChangeRow[] ChangeRows(StudioProject project)
    {
        var key = new ChangeRowsKey(project, workspace.ProjectRevision, workspace.NameRevision, workspace.AssetRevision,
            workspace.CompiledObjects, project.Mod, project.ConfigBindings, project.ObjectEdits, project.Changes, project.AddedAssets,
            project.ConfigBindings.Count + project.ObjectEdits.Count + project.Changes.Count + project.AddedAssets.Count);
        if (changeRowsKey == key) return changeRows;
        changeRowsKey = key;
        var compiled = new Dictionary<string, CompiledObjectEdit>(StringComparer.Ordinal);
        foreach (var item in workspace.CompiledObjects.Edits) compiled.TryAdd(item.Edit.Key, item);
        var rows = new List<ChangeRow>();
        foreach (var binding in project.ConfigBindings)
            rows.Add(new("config/" + binding.Identity, $"{workspace.DisplayName(binding.TagHash)}: {binding.Site.Name} = {binding.Key}",
                "Config binding. Game settings apply after restart.", "Config", false, "",
                workspace.ConfigSetting(binding)?.Default ?? "", binding));
        // One row per edited object; the tags its build rebuilds are not separate changes.
        foreach (var edit in project.ObjectEdits)
        {
            var tags = compiled.GetValueOrDefault(edit.Key)?.Tags ?? Array.Empty<uint>();
            rows.Add(new("object/" + edit.Key, $"{edit.Name ?? workspace.DisplayName(edit.Target.SourceTag)}: {edit.Summary()}",
                $"{edit.Target.Layout} row in {edit.Target.SourceTag:X8}\n" + (tags.Count == 0 ? "Not compiled"
                    : $"Writes {tags.Count} asset{(tags.Count == 1 ? "" : "s")}: "
                        + string.Join(", ", tags.Take(8).Select(tag => tag.ToString("X8"))) + (tags.Count > 8 ? ", ..." : "")),
                "Object", true, "", tags.Count == 1 ? "1 asset" : $"{tags.Count} assets", edit));
        }
        foreach (var change in project.Changes)
            rows.Add(new($"delta/{change.TagHash:X8}", workspace.DisplayName(change.TagHash), "", "Delta", true,
                workspace.CompiledObjects.Assets.ContainsKey(change.TagHash)
                    ? "Object edits also write this asset; they win on overlap." : "",
                FormatBytes(change.EditedSize), change));
        foreach (var addition in project.AddedAssets)
            rows.Add(new("added/" + addition.Id, addition.Name, "",
                addition.BaseTagHash is not null ? "Derived" : addition.Native is null ? "Source" : "Added", false, "",
                FormatBytes(addition.Size), addition));
        return changeRows = rows.ToArray();
    }

    private void DrawChangeRow(StudioProject project, ChangeRow row)
    {
        ImGui.PushID(row.Id);
        ImGui.TableNextRow(); ImGui.TableNextColumn();
        bool active = row.Source switch
        {
            ProjectAssetChange change => workspace.Document?.Entry.TagHash == change.TagHash,
            AddedProjectAsset addition => workspace.Document?.AddedAssetId == addition.Id,
            _ => false
        };
        if (ImGui.Selectable(row.Label, active, ImGuiSelectableFlags.SpanAllColumns))
            switch (row.Source)
            {
                case ProjectConfigBinding binding: workspace.Open(binding.TagHash); break;
                case ObjectEdit edit: SelectEditedObject(edit); break;
                case ProjectAssetChange change: workspace.Open(change.TagHash); break;
                case AddedProjectAsset addition: workspace.Run(() => workspace.SelectAdded(addition.Id)); break;
            }
        if (row.Tooltip.Length != 0 && ImGui.IsItemHovered()) ImGui.SetTooltip(row.Tooltip);
        if (ImGui.BeginPopupContextItem("change"))
        {
            DrawChangeMenu(project, row.Source);
            ImGui.EndPopup();
        }
        ImGui.TableNextColumn();
        if (row.Accent) ImGui.TextColored(StudioTheme.Accent, row.Change); else ImGui.TextUnformatted(row.Change);
        if (row.ChangeTooltip.Length != 0 && ImGui.IsItemHovered()) ImGui.SetTooltip(row.ChangeTooltip);
        ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Value);
        ImGui.PopID();
    }

    private void DrawChangeMenu(StudioProject project, object source)
    {
        switch (source)
        {
            case ProjectConfigBinding binding:
                if (ImGui.MenuItem("Unbind", "", false, workspace.CanEdit && workspace.Document is not null))
                    workspace.Run(() => workspace.SetConfigBindings(
                        project.ConfigBindings.Where(value => value.Identity != binding.Identity).ToArray()));
                break;
            case ObjectEdit edit:
                if (ImGui.MenuItem("Revert", "", false, workspace.CanEdit)) workspace.Run(() => workspace.RevertObjectEdit(edit.Key));
                break;
            case ProjectAssetChange change:
                if (ImGui.MenuItem("Revert", "", false, workspace.CanEdit)) workspace.Run(() => workspace.Revert(change.TagHash));
                break;
            case AddedProjectAsset addition:
                if (ImGui.MenuItem("Export")) ExportAdded(addition);
                if (ImGui.MenuItem("Wide-hash aliases...", "", false, workspace.CanEdit && addition.Native is not null))
                    BeginWideHashes(addition.Id);
                if (ImGui.MenuItem("Remove", "", false, workspace.CanEdit)) workspace.Run(() => workspace.RemoveAdded(addition.Id));
                break;
        }
    }

    /** The Changes tab lists the project's changes; the Build tab shows the last build and install. */
    private unsafe void DrawChanges(Vector2 position, Vector2 size)
    {
        BeginPanel("Project changes", position, size);
        TrackAreaFocus();
        if (ImGui.BeginTabBar("project-tabs"))
        {
            if (ImGui.BeginTabItem("Changes"))
            {
                if (workspace.ObjectEditError.Length != 0)
                    ImGui.TextColored(StudioTheme.Accent, "Object edits not applied: " + workspace.ObjectEditError);
                if (workspace.Project is not { } project) ImGui.TextDisabled("No project open");
                else if (project.Changes.Count == 0 && project.AddedAssets.Count == 0 && project.ObjectEdits.Count == 0
                    && project.ConfigBindings.Count == 0)
                    ImGui.TextDisabled("No changes");
                else if (ImGui.BeginTable("change-list", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                    ImGui.GetContentRegionAvail()))
                {
                    ImGui.TableSetupColumn("Asset", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Change", ImGuiTableColumnFlags.WidthFixed, 80 * scale);
                    ImGui.TableSetupColumn("Value / size", ImGuiTableColumnFlags.WidthFixed, 110 * scale);
                    ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
                    var rows = ChangeRows(project);
                    var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
                    clipper.Begin(rows.Length);
                    while (clipper.Step())
                        for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++) DrawChangeRow(project, rows[i]);
                    clipper.End(); clipper.Destroy();
                    ImGui.EndTable();
                }
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Build"))
            {
                if (workspace.LastInstall is { } install)
                {
                    ImGui.TextUnformatted($"Installed {Path.GetFileName(install.ModDirectory)}");
                    StudioUi.Facts("install-facts", [
                        ("Packages", $"{install.PackageFiles}", ""),
                        ("Patches", $"{install.PatchFiles}", ""),
                        ("Scripts", $"{install.ScriptFiles}", ""),
                        ("SDK modules", $"{install.SdkFiles}", "")]);
                    if (ImGui.Button("Open mod folder")) OpenFolder(install.ModDirectory);
                    ImGui.TextDisabled(install.ModDirectory);
                    ImGui.Separator();
                }
                if (workspace.LastBuild is { } build)
                {
                    if (ImGui.Button("Open output folder")) OpenFolder(build.OutputDirectory);
                    if (ImGui.TreeNode($"Package files ({build.PackageFiles.Count})###package-files"))
                    {
                        foreach (string path in build.PackageFiles) ImGui.TextUnformatted(Path.GetFileName(path));
                        ImGui.TreePop();
                    }
                    StudioUi.Facts("built-edits", (build.ObjectEdits ?? Array.Empty<CompiledObjectEdit>()).Select(built => (
                        built.Edit.Name ?? built.Edit.Key,
                        built.Tags.Count == 1 ? "1 asset" : $"{built.Tags.Count} assets",
                        $"{built.Edit.Summary()}\n" + string.Join(", ", built.Tags.Select(tag => tag.ToString("X8"))))));
                }
                else if (workspace.LastInstall is null) ImGui.TextDisabled("No build yet");
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
        ImGui.End();
    }

    /** An accent "Error" row with the first sentence; the full text is the tooltip. */
    private static void DrawErrorRow(string error)
    {
        ImGui.TextColored(StudioTheme.Accent, "Error");
        StudioUi.Tip(error);
        ImGui.SameLine();
        ImGui.TextUnformatted(FirstSentence(error));
        StudioUi.Tip(error);
    }

    /** Text up to the first ". ", or all of it. */
    private static string FirstSentence(string text)
    {
        int end = text.IndexOf(". ", StringComparison.Ordinal);
        return end < 0 ? text : text[..(end + 1)];
    }

    private static string FormatBytes(long count) => count switch
    {
        >= 1024 * 1024 => $"{count / (1024d * 1024):0.##} MB",
        >= 1024 => $"{count / 1024d:0.##} KB",
        _ => $"{count} B"
    };
}
