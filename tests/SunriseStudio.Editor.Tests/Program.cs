using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Buffers.Binary;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Editor;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sunrise-editor-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
try
{
    if (args is ["--spawn-points"]) { SpawnPointWorkflowChecks.Run(root); return 0; }
    if (args is ["--weapon-scene", var weaponGame, var weaponKeys, var weaponProject, var weaponOutput])
    {
        WeaponSceneUiChecks.Run(weaponGame, weaponKeys, weaponProject, weaponOutput);
        return 0;
    }
    if (args is ["--guardian-scene", var guardianGame, var guardianOutput])
    {
        GuardianUiChecks.Run(guardianGame, guardianOutput);
        return 0;
    }
    if (args is ["--guardian-export", var exportGame, var exportOutput])
    {
        GuardianExportChecks.Run(exportGame, exportOutput);
        return 0;
    }
    if (args is ["--navigation", var navigationGame, var navigationOutput])
    {
        NavigationChecks.Run(navigationGame, navigationOutput);
        return 0;
    }
    if (args is ["--isolated-objects"]) { IsolatedObjectWorkflowChecks.Run(root); return 0; }
    if (args is ["--groups"]) { GroupTransformWorkflowChecks.Run(root); SceneSelectionUiChecks.Run(root); return 0; }
    if (args is ["--groups-owned", var groupGame]) { GroupTransformWorkflowChecks.Owned(groupGame, root); return 0; }
    if (args is ["--decorator-owned", var decoratorGame])
    {
        PlacementUpdateWorkflowChecks.OwnedDecorator(decoratorGame, root);
        return 0;
    }
    if (args is ["--placement-owned", var placementGame, var placementMap, var placementMission])
    {
        PlacementUpdateWorkflowChecks.Owned(placementGame, root, Convert.ToUInt32(placementMap, 16),
            Convert.ToUInt32(placementMission, 16));
        return 0;
    }
    if (args is ["--layout", var layoutOutput])
    {
        LayoutChecks.Run(root);
        LayoutChecks.Ui(root, layoutOutput);
        return 0;
    }
    if (args is ["--config-bindings-owned", var configGame, var configOwnedOutput])
    {
        ConfigBindingChecks.Owned(root, configGame, configOwnedOutput);
        return 0;
    }
    if (args is ["--config-bindings", var configOutput])
    {
        ConfigBindingChecks.Run(root, configOutput);
        ConfigBindingUiChecks.Run(root, Path.Combine(configOutput, "bindings.png"));
        return 0;
    }
    if (args is ["--scene-selection"]) { SceneSelectionUiChecks.Run(root); return 0; }
    if (args is ["--browser-filter"]) { BrowserFilterChecks.Run(root); return 0; }
    if (args is ["--browser-added"]) { BrowserAddedChecks.Run(root); BrowserConfigChecks.Run(root); return 0; }
    if (args is ["--creation-history"]) { ImportHistoryChecks.Run(root); CreationSafetyChecks.Run(root); return 0; }
    if (args is ["--creation-history", var importGame]) { ImportHistoryChecks.Owned(root, importGame); return 0; }
    if (args is ["--browser-category"]) { BrowserCategoryChecks.Run(root); return 0; }
    if (args is ["--category-shell"]) { CategoryShellChecks.Run(root); return 0; }
    if (args is ["--part-navigation", var partGame]) { PartNavigationChecks.Run(partGame, root); return 0; }
    if (args is ["--collision-parts", var partsGame]) { CollisionPartWorkflowChecks.Run(partsGame, root); return 0; }
    if (args is ["--spawn-preview", var spawnGame]) { SpawnPreviewChecks.Run(spawnGame); return 0; }
    if (args is ["--wide-hashes", "--capture", var aliasCapture])
    {
        WideHashWorkflowChecks.Run(root);
        WideHashUiChecks.Run(root, aliasCapture);
        return 0;
    }
    if (args is ["--notices", "--capture", var noticesCapture]) { NoticesUiChecks.Run(root, noticesCapture); return 0; }
    if (args is ["--game-root", var entityGame, "--skin-entity", var entityFile, "--capture", var entityCapture])
    {
        SkinEntityUiChecks.Run(entityGame, entityFile, root, entityCapture);
        return 0;
    }
    if (args is ["--game-root", var skinGame, "--skin-file", var skinFile, "--capture", var skinCapture])
    {
        SkinImportUiChecks.Run(skinGame, skinFile, root, skinCapture);
        return 0;
    }
    CanvasChecks.Run(root);
    LayoutChecks.Run(root);
    IsolatedObjectWorkflowChecks.Run(root);
    NoticesUiChecks.Run(root);
    const uint first = 0x80A00000, second = first + 1;
    string install = Path.Combine(root, "install");
    Directory.CreateDirectory(Path.Combine(install, "packages"));
    byte[] original = Encoding.UTF8.GetBytes("Synthetic original data for editor transaction tests.");
    byte[] companion = Encoding.UTF8.GetBytes("Synthetic companion texture payload.");
    byte[] textureHeader = new byte[40];
    BinaryPrimitives.WriteUInt32LittleEndian(textureHeader, 4);
    BinaryPrimitives.WriteUInt32LittleEndian(textureHeader.AsSpan(4), 28);
    BinaryPrimitives.WriteUInt16LittleEndian(textureHeader.AsSpan(0x0C), 0xCAFE);
    foreach (int field in new[] { 0x0E, 0x10, 0x12, 0x14 })
        BinaryPrimitives.WriteUInt16LittleEndian(textureHeader.AsSpan(field), 1);
    textureHeader[0x17] = 1;
    BinaryPrimitives.WriteUInt32LittleEndian(textureHeader.AsSpan(0x24), uint.MaxValue);
    PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_editor_0100_0.pkg"), 0x100,
        [new(0x80800001, 0x100A, original, "Editor fixture"),
         new(0x80800002, 0x100A, companion, "Companion fixture"),
         new(first + 3, (32 << 9) | (1 << 6) | 10, textureHeader),
         new(uint.MaxValue, (40 << 9) | (1 << 6) | 10, [1, 2, 3, 255])]);
    using var workspace = new StudioWorkspace();
    workspace.OpenInstallation(install);
    Wait(workspace);
    Check(workspace.Assets.Length == 4 && !workspace.CanEdit, "View-only startup failed.");
    var sourceContext = new AssetOpenContext(PlacementTag: first, PlacementOffset: 16);
    workspace.Select(first, context: sourceContext); Wait(workspace);
    Check(workspace.Document!.Bytes.SequenceEqual(original), "Selected original differs.");
    Throws(() => workspace.ApplyBytes([1, 2, 3]));
    string projectDirectory = Path.Combine(root, "project");
    workspace.CreateProject(projectDirectory, "Editor workflow"); Wait(workspace);
    byte[] changed = (byte[])original.Clone(); changed[3] ^= 7;
    workspace.ApplyBytes(changed); Wait(workspace);
    Check(workspace.Document!.Bytes.SequenceEqual(changed) && workspace.CanUndo, "Edit was not adopted.");
    workspace.Undo(); Wait(workspace);
    Check(workspace.Document!.Bytes.SequenceEqual(original) && workspace.CanRedo, "Undo failed.");
    Check(workspace.Project!.Changes.Count == 0, "Undo retained an empty delta.");
    workspace.Redo(); Wait(workspace);
    Check(workspace.Document!.Bytes.SequenceEqual(changed), "Redo failed.");
    workspace.Select(second); Wait(workspace);
    Check(workspace.CanNavigateBack, "Related asset history did not retain the prior asset.");
    workspace.NavigateBack(); Wait(workspace);
    Check(workspace.Document!.Entry.TagHash == first && workspace.CanNavigateForward, "Back navigation lost its target.");
    workspace.NavigateForward(); Wait(workspace);
    Check(workspace.Document!.Entry.TagHash == second, "Forward navigation lost its target.");
    Check(workspace.CanUndo, "Asset navigation discarded project history.");
    workspace.Undo(); Wait(workspace);
    Check(workspace.Document!.Entry.TagHash == first && workspace.Document.Bytes.SequenceEqual(original),
        "Undo after navigation selected the wrong asset.");
    Check(workspace.Document.OpenContext == sourceContext, "Undo after navigation lost the asset owner context.");
    workspace.Redo(); Wait(workspace);
    byte[] companionChanged = (byte[])companion.Clone(); companionChanged[2] ^= 4;
    byte[] secondEdit = (byte[])changed.Clone(); secondEdit[7] ^= 2;
    workspace.ApplyAssetEdits(new Dictionary<uint, byte[]>
    { [first] = secondEdit, [second] = companionChanged }); Wait(workspace);
    Check(workspace.Project!.Changes.Count == 2, "Coupled edit did not save both assets.");
    workspace.Undo(); Wait(workspace);
    Check(workspace.Catalog!.Read(second).SequenceEqual(companion), "Coupled undo left a payload modified.");
    workspace.Redo(); Wait(workspace);
    Check(workspace.Catalog.Read(second).SequenceEqual(companionChanged), "Coupled redo lost a payload.");
    string manifest = workspace.Project.ManifestPath;
    workspace.CloseProject(); Wait(workspace);
    Check(!workspace.CanEdit && workspace.Document!.Bytes.SequenceEqual(original), "Close retained the overlay.");
    workspace.OpenProject(manifest); Wait(workspace);
    Check(workspace.Document!.Bytes.SequenceEqual(secondEdit), "Reopened delta differs.");
    workspace.Build(); Wait(workspace);
    Check(workspace.LastBuild!.PackageFiles.Count == 1, "Editor build produced no patch.");
    Check(workspace.Catalog.ReadOriginal(first).SequenceEqual(original), "Editor changed an original.");
    workspace.Revert(first); Wait(workspace);
    Check(workspace.Document!.Bytes.SequenceEqual(original), "Revert failed.");
    Check(workspace.Document.OpenContext == sourceContext, "Revert lost the asset owner context.");
    int beforeRevert = workspace.DocumentRevision;
    workspace.Revert(second); Wait(workspace);
    Check(workspace.DocumentRevision > beforeRevert && workspace.Document!.Entry.TagHash == first,
        "Reverting a dependency did not refresh the selected document.");
    workspace.ImportAsset((project, catalog) => new([new("owned.txt", Encoding.UTF8.GetBytes("Owned source"))]));
    Wait(workspace);
    Check(workspace.Document?.AddedAssetId is not null, "Worker import did not adopt its new asset.");
    workspace.ApplyBytes(Encoding.UTF8.GetBytes("Edited owned source")); Wait(workspace);
    Check(Encoding.UTF8.GetString(workspace.Document!.Bytes) == "Edited owned source", "Added source edit failed.");
    workspace.Undo(); Wait(workspace);
    Check(Encoding.UTF8.GetString(workspace.Document!.Bytes) == "Owned source", "Added source undo failed.");
    var derived = workspace.Project.AddDerivedAsset("Native variant", first, original,
        new(0x0C00, 0x80800001, 0x100A), workspace.Catalog);
    workspace.SelectAdded(derived.Id); Wait(workspace);
    workspace.ApplyBytes(changed); Wait(workspace);
    Check(workspace.Document!.Bytes.SequenceEqual(changed), "Derived overlay edit failed.");
    workspace.Undo(); Wait(workspace);
    Check(workspace.Document!.Bytes.SequenceEqual(original), "Derived overlay undo failed.");
    workspace.Build(); Wait(workspace);
    Check(workspace.LastBuild!.PackageFiles.Count == 1, "Derived output package was not built.");
    workspace.SetModMetadata(new("dev.test.editor", "1.0.0", "Editor workflow"));
    workspace.BuildAndInstall(); Wait(workspace);
    Check(workspace.LastInstall is { PackageFiles: 1, PatchFiles: 0, ScriptFiles: 0 } installed
        && File.Exists(Path.Combine(installed.ModDirectory, "packages", "w64_sunrise_0c00_0.pkg"))
        && installed.ModDirectory == Path.Combine(install, "bin", "x64", "Sunrise", "mods", "dev.test.editor"),
        "Editor build and install failed.");
    var pixels = new RgbaImage(2, 1, [255, 0, 0, 255, 0, 255, 0, 255]);
    var imported = TextureAssetImporter.ImportRgba(workspace.Project, workspace.Catalog, pixels,
        "Authored image", 0x0C01, first + 2);
    workspace.SelectAdded(imported.HeaderAsset.Id); Wait(workspace);
    Check(workspace.Document!.Texture?.Width == 2, "New native texture did not preview through the overlay.");
    var changedPixels = new RgbaImage(2, 1, [0, 0, 255, 255, 255, 255, 0, 255]);
    workspace.ApplyAssetEdits(TextureEdits.Apply(workspace.Catalog, imported.HeaderTag,
        PngInterchange.ToTexture(changedPixels, workspace.Document.Texture!))); Wait(workspace);
    Check(workspace.Document!.Texture!.Subresources[0].Data.SequenceEqual(changedPixels.Pixels),
        "New texture payload reimport did not refresh the preview.");
    workspace.Undo(); Wait(workspace);
    Check(workspace.Document!.Texture!.Subresources[0].Data.SequenceEqual(pixels.Pixels),
        "New texture payload undo failed.");
    workspace.CloseProject(); Wait(workspace);
    Check(workspace.LastBuild is null && workspace.LastInstall is null && workspace.Document is null,
        "Closed project retained build, install or added selection.");
    string preferences = Path.Combine(root, "settings.json");
    var settings = new StudioSettings { SunrisePath = install };
    Check(settings.LightingMode == SunriseStudio.Rendering.NativeLightingMode.Studio, "Editor lighting must default to Studio.");
    settings.LightingMode = SunriseStudio.Rendering.NativeLightingMode.Ambient;
    settings.RememberProject(manifest); settings.Save(preferences);
    Check(StudioSettings.Load(preferences).RecentProjects.Single() == manifest, "Settings roundtrip failed.");
    Check(StudioSettings.Load(preferences).LightingMode == settings.LightingMode, "Lighting preference did not roundtrip.");
    File.WriteAllText(preferences, "{\"GameShaders\":true}");
    Check(StudioSettings.Load(preferences).LightingMode == SunriseStudio.Rendering.NativeLightingMode.Studio,
        "Older preferences retained incomplete atmosphere lighting.");
    File.WriteAllText(preferences, "{\"LightingMode\":99}");
    Check(StudioSettings.Load(preferences).LightingMode == SunriseStudio.Rendering.NativeLightingMode.Studio,
        "Invalid lighting preference was accepted.");
    Check(StudioSettings.ResolveGameRoot(Path.Combine(install, "packages")) == install, "Installation folder resolution failed.");
    RunUiMutation(install, root, first + 2);
    WorldUiCheck.Run(root);
    SceneSelectionUiChecks.Run(root);
    TreeUiChecks.Run(root);
    LayoutChecks.Ui(root, Path.Combine(root, "layout.png"));
    BrowserCategoryChecks.Run(root);
    BrowserFilterChecks.Run(root);
    BrowserAddedChecks.Run(root);
    BrowserConfigChecks.Run(root);
    CategoryShellChecks.Run(root);
    ImportHistoryChecks.Run(root);
    CreationSafetyChecks.Run(root);
    BundleWorkflowChecks.Run(root);
    TransactionWorkflowChecks.Run(root);
    WideHashWorkflowChecks.Run(root);
    ConfigBindingChecks.Run(root);
    StaticPropInspectionChecks.Run(root, workspace);
    ObjectEditWorkflowChecks.Run(root);
    SpawnPointWorkflowChecks.Run(root);
    PlacementUpdateWorkflowChecks.Run(root);
    GroupTransformWorkflowChecks.Run(root);
    ReadinessChecks.Run(root);
    if (Array.IndexOf(args, "--game-root") is >= 0 and var gameIndex)
    {
        ConfigBindingChecks.Owned(root, args[gameIndex + 1], Path.Combine(root, "config-owned-artifacts"));
        ImportHistoryChecks.Owned(root, args[gameIndex + 1]);
        AssetNavigationChecks.Run(args[gameIndex + 1], root);
        PartNavigationChecks.Run(args[gameIndex + 1], root);
        CollisionUiChecks.Run(args[gameIndex + 1], root);
        CollisionAddWorkflowChecks.Run(args[gameIndex + 1], root);
        CollisionPartWorkflowChecks.Run(args[gameIndex + 1], root);
        SpawnPreviewChecks.Run(args[gameIndex + 1]);
        uint Tag(string name, uint fallback) => Array.IndexOf(args, name) is >= 0 and var at
            ? Convert.ToUInt32(args[at + 1], 16) : fallback;
        PlacementUpdateWorkflowChecks.Owned(args[gameIndex + 1], root, Tag("--map", 0x80EDC19B), Tag("--mission", 0x80B48062));
        PlacementUpdateWorkflowChecks.OwnedDecorator(args[gameIndex + 1], root);
        GroupTransformWorkflowChecks.Owned(args[gameIndex + 1], root);
    }
    Console.WriteLine("editor workflows=30 failures=0");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
finally
{
    if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(root).StartsWith("sunrise-editor-", StringComparison.Ordinal))
        throw new IOException("Test cleanup left its temporary root.");
    Directory.Delete(root, true);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Throws(Action action)
{
    try { action(); }
    catch (InvalidOperationException) { return; }
    throw new Exception("A view-only edit was accepted.");
}

static void Wait(StudioWorkspace workspace)
{
    var timer = Stopwatch.StartNew();
    while (workspace.IsBusy && timer.Elapsed.TotalSeconds < 30)
    {
        workspace.Poll();
        ReadinessChecks.AssertPublished(workspace);
        Thread.Sleep(5);
    }
    if (workspace.IsBusy) throw new TimeoutException("Editor worker timed out.");
    if (workspace.Error.Length != 0) throw new Exception(workspace.Error);
}

/** A texture edit after drawing must retain the borrowed image until ImGui consumes it. */
static void RunUiMutation(string install, string root, uint textureTag)
{
    var project = StudioProject.Create(Path.Combine(root, "ui-project"), "UI texture mutation");
    Exception? failure = null;
    var page = new TextureMutationPage();
    var thread = new Thread(() =>
    {
        try
        {
            Application.EnableVisualStyles();
            using var form = new StudioForm(new(Game: install, Project: project.ManifestPath,
                Tag: textureTag, Warp: true, Smoke: true, Frames: 4,
                Capture: Path.Combine(root, "ui-mutation.png"), SettingsPath: Path.Combine(root, "ui-settings.json")));
            form.Pages.Register(page, first: true);
            Application.Run(form);
            if (form.Failure is { } error) throw new Exception(error);
        }
        catch (Exception error) { failure = error; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if (!thread.Join(TimeSpan.FromSeconds(60))) throw new TimeoutException("UI mutation smoke test timed out.");
    if (failure is not null) throw failure;
    Check(page.Applied, "UI mutation page was not reached.");
    Check(StudioProject.Open(project.ManifestPath).Changes.Count == 1, "UI texture edit was not saved.");
    Console.WriteLine("editor_ui texture-after-draw=pass project-delta=pass warp=pass");
}

sealed class TextureMutationPage : IStudioPage
{
    public string Id => "tests.texture-mutation";
    public string Title => "Texture mutation";
    public bool Applied { get; private set; }
    public bool CanOpen(StudioDocument document) => document.Texture is not null;

    public void Draw(StudioPageContext context)
    {
        context.Viewport.DrawImGui(ImGuiNET.ImGui.GetContentRegionAvail());
        if (Applied || !context.Workspace.CanEdit) return;
        Applied = true;
        context.Workspace.ApplyAssetEdits(new Dictionary<uint, byte[]>
        { [context.Workspace.Document!.Entry.Reference] = [4, 5, 6, 255] });
    }
}

static class WorldUiCheck
{
    public static void Run(string root)
    {
        string install = Path.Combine(root, "world-install");
        WorldFixture.Create(install);
        var project = StudioProject.Create(Path.Combine(root, "world-ui-project"), "Mission editor");
        Exception? failure = null;
        int stage = 0;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new StudioForm(new(Game: install, Project: project.ManifestPath,
                    Map: WorldFixture.Base, Mission: WorldFixture.Base + 4, Warp: true, Smoke: true, Frames: 24,
                    Capture: Path.Combine(root, "world-ui.png"), SettingsPath: Path.Combine(root, "world-ui-settings.json")));
                Vector3 original = default;
                form.FrameRendered += () =>
                {
                    var work = form.Workspace;
                    if (work.IsBusy || work.World is not { } world || form.SceneViewport?.Scene != world.Scene) return;
                    if (stage == 0)
                    {
                        var marker = world.Scene.Markers.First(value => value.Layer == SceneLayer.Spawns);
                        original = marker.Position;
                        form.SelectSceneObject(marker, true);
                        form.SceneViewport.SetCursorToSelection();
                        Require(form.SelectedObjectDetails is { Properties.Count: > 8 }, "Selected object details were not linked.");
                        Require(form.SceneViewport.UseGameShaders, "Game shaders were not requested by default.");
                        var node = form.ObjectTree?.Find(marker) ?? throw new Exception("Selected object has no tree row.");
                        Require(form.SelectedTreeNodeId == node.Id, "Scene selection was not reflected in the tree.");
                        var branch = node.Parent ?? node;
                        form.SetSceneNodeHidden(branch.Id, true);
                        Require(!form.SceneViewport.IsVisible(marker) && form.SceneViewport.SelectedObject is null,
                            "Hiding a tree branch retained its selected child in the scene.");
                        form.SetSceneNodeHidden(branch.Id, false);
                        Require(form.SceneViewport.IsVisible(marker), "Showing a branch did not restore its child.");
                        form.SelectSceneObject(marker, true);
                        form.ShowMissionEditor();
                        stage++;
                    }
                    else if (stage == 1)
                    {
                        var marker = (SceneMarker)form.SelectedObjectDetails!.Object.Source;
                        work.ApplyObjectEdit(SceneEdits.CreateTransformEdit(work.Catalog!, marker,
                            original + Vector3.UnitX, marker.Rotation, 1, world, "Spawn"));
                        Require(work.Project!.ObjectEdits.Count == 1 && work.Project.Changes.Count == 0,
                            "A placement apply saved a byte delta instead of one object edit.");
                        stage++;
                    }
                    else if (stage == 2)
                    {
                        Require(form.SelectedObjectDetails?.Object.Source is SceneMarker marker
                            && marker.Position == original + Vector3.UnitX, "Reload lost the selected moved marker.");
                        Require(world.AllStates.Count == 3, "Edit discarded mission states.");
                        work.Select(WorldFixture.Base + 6);
                        stage++;
                    }
                    else if (stage == 3)
                    {
                        Require(work.World?.MissionTag == WorldFixture.Base + 4, "Opening a source lost the world.");
                        work.Undo(); stage++;
                    }
                    else if (stage == 4)
                    {
                        Require(form.SelectedObjectDetails?.Object.Source is SceneMarker marker && marker.Position == original,
                            "Undo lost world selection or its original position.");
                        work.ImportAsset((p, _) => new([new("owned.txt", [1, 2, 3])]));
                        stage++;
                    }
                    else if (stage == 5)
                    {
                        Require(work.Document?.AddedAssetId is not null && ReferenceEquals(form.SceneViewport.Scene, world.Scene),
                            "Import blanked the world or lost the added document.");
                        work.ApplyBytes([4, 5, 6]); stage++;
                    }
                    else if (stage == 6)
                    {
                        work.Undo(); stage++;
                    }
                    else if (stage == 7)
                    {
                        Require(work.Document?.AddedAssetId is not null && work.Document.Bytes.SequenceEqual(new byte[] { 1, 2, 3 }),
                            "World reload lost the added inspector or undo bytes.");
                        var light = world.Objects.Single(row => row.Kind == WorldObjectKind.Light);
                        form.SelectSceneObject(light.Source, true);
                        work.ApplyObjectEdit(SceneEdits.CreateTransformEdit(work.Catalog!, light.Source, new(7, 8, 9), null, 2, world, "Light"));
                        stage++;
                    }
                    else if (stage == 8)
                    {
                        // The light moves in place: same scene, a moved proxy that stays selected.
                        var proxy = form.SceneViewport.Selection.Objects.OfType<SunriseStudio.Viewport.ScenePointProxy>()
                            .Single(point => point.Details is WorldLight);
                        Require(work.LastWorldRefresh is { Incremental: true } && ReferenceEquals(form.SceneViewport.Scene, world.Scene)
                            && proxy.Position == new Vector3(7, 8, 9) && ReferenceEquals(form.SceneViewport.SelectedObject, proxy),
                            "A light edit did not move its proxy in place.");
                        stage++;
                    }
                };
                Application.Run(form);
                if (form.Failure is { } error) throw new Exception(error);
                Require(stage == 9, "World UI workflow did not finish.");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(90))) throw new TimeoutException("World UI smoke timed out.");
        if (failure is not null) throw failure;
        Console.WriteLine("editor_world selection=pass details=pass mission-ui=pass delta=pass undo=pass retained-map=pass light-in-place=pass");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
