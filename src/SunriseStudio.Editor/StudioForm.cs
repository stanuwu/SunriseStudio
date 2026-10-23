using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Rendering;
using SunriseStudio.Viewport;
using SunriseStudio.Scene;

namespace SunriseStudio.Editor;

/** The window thread owns all ImGui calls and GPU resources. */
public sealed partial class StudioForm : Form
{
    private readonly StudioOptions options;
    private readonly StudioSettings settings;
    private readonly string settingsPath;
    private readonly StudioWorkspace workspace = new();
    private readonly StudioPageRegistry pages = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1 };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private Dx11Renderer? renderer;
    private ImGuiController? gui;
    private GpuTexture? loadingLogo, toolbarLogo;
    private readonly Icon studioIcon;
    private readonly WindowChrome chrome;
    private StudioLoadProgress? presentationProgress;
    private double lastLoadingFrame;
    private bool guiFrameActive;
    private AssetViewport? viewport;
    private AssetViewport? assetViewport;
    private double lastFrame;
    private bool drawing, nativeDialog, setup, settingsDialog, projectDialog, closed;
    private bool initialProjectOpened, initialTagOpened;
    private bool initialNativePrepared, initialClipSelected;
    private bool initialCenterPicked, initialMissionView;
    private bool initialPageSelected;
    private string? focusDocumentPage;
    private readonly MissionGraphPage missionGraph = new();
    private readonly LivePage livePage = new();
    private bool missionGraphActive, focusMissionGraph, focusAssets, focusLive, initialMissionScript;
    private int documentRevision = -1, assetRevision = -1, smokeFrames;
    private int nativeRevision;
    private int sceneRevision = -1;
    private bool browserPreparing;
    private float scale = 1;
    // The monitor DPI from the last DPI change, applied at the next frame; 0 when none is waiting.
    private int pendingDpi;
    private static readonly string BodyFontPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf");
    private string location = "", keySource = "", projectName = "", projectFolder = "";
    private string search = "", category = AllCategories, assetType = AllTypes, package = "All packages";
    private string[] categories = [AllCategories], assetTypes = [AllTypes], packageNames = ["All packages"];
    private BrowserAsset[] filtered = [];
    private AssetProperty? selectedProperty;
    private string propertyValue = "";
    private MeshInstance? selectedMesh;
    private (uint Tag, string? AddedId)? adoptedAsset;
    private uint? adoptedMap;
    private StudioDocument? adoptedDocument;
    private bool changesOnly;
    public string? Failure { get; private set; }
    public StudioPageRegistry Pages => pages;
    public StudioWorkspace Workspace => workspace;
    public AssetViewport? SceneViewport => viewport;
    public AssetViewport? AssetPreviewViewport => workspace.World is null ? viewport : assetViewport;
    public bool EditorReady { get; private set; }
    public WorldObjectInspection? SelectedObjectDetails => objectInspection;
    public event Action? FrameRendered;

    public void SelectSceneObject(object source, bool focus = false)
    {
        if (TrySelectLiveSceneObject(source, focus)) return;
        if (inspectionWorld?.Find(source) is not { } row) return;
        SelectWorldObject(row);
        if (focus && viewport?.SelectedObject is not null) viewport.FocusSelection();
    }

    public void ShowMissionEditor() => focusMissionPage = true;
    public void ShowAssetPage(string page)
    {
        if (page.Equals("Mission Graph", StringComparison.OrdinalIgnoreCase)) { focusMissionGraph = true; return; }
        if (page.Equals("Live", StringComparison.OrdinalIgnoreCase)) { focusLive = true; return; }
        focusAssets = true; focusDocumentPage = page; focusAssetPage = true; inspectAsset = true;
    }

    public StudioForm(StudioOptions options)
    {
        this.options = options;
        settingsPath = options.SettingsPath ?? StudioSettings.DefaultPath;
        settings = StudioSettings.Load(settingsPath);
        layout = StudioLayout.Restore(settings.Layout, settings.BrowserWidth, settings.InspectorWidth, settings.ChangesHeight);
        missionGraph.LayoutDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!, "mission-layouts");
        ShortcutInput.Map = ShortcutMap.FromElement(settings.Shortcuts);
        ApplyCacheSettings();
        location = options.Game ?? settings.SunrisePath;
        keySource = options.KeySource ?? settings.KeySource;
        if (StartupCategory() is { } named) category = named.Name;
        else if (options.Category is { } kind) (category, assetType) = SplitFilter(kind);
        Text = $"Sunrise Studio {StudioVersion.Text}";
        chrome = new(this);
        Icon = studioIcon = StudioBrand.CreateIcon();
        ClientSize = new Size(1540, 960);
        MinimumSize = new Size(1100, 720);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        if (options.Smoke) ShowInTaskbar = false;
        timer.Tick += (_, _) => TickFrame();
        workspace.BeforeAssetMutation += () =>
        {
            livePage.CancelPreparation();
            viewport?.DetachAnimation();
            assetViewport?.DetachAnimation();
            viewport?.CancelCollisionLoad();
            assetViewport?.CancelCollisionLoad();
        };
    }

    protected override void OnPaint(PaintEventArgs e) { }
    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Tab or Keys.Left or Keys.Right or Keys.Up or Keys.Down
        || base.IsInputKey(keyData);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        chrome.Attach();
        if (options.Smoke) chrome.Cloak();
    }

    /** A smoke run never takes focus from the user's own windows. */
    protected override bool ShowWithoutActivation => options.Smoke;

    protected override void WndProc(ref Message m)
    {
        if (chrome.WndProc(ref m)) return;
        base.WndProc(ref m);
    }

    /** Windows has already resized the window; fonts and sizes follow at the next frame. */
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        pendingDpi = e.DeviceDpiNew;
    }

    /** Rebuilds fonts and style for a new monitor DPI. Runs between frames, before any window draws. */
    private void ApplyPendingDpi()
    {
        if (pendingDpi == 0 || gui is null) return;
        float next = Math.Max(1, pendingDpi / 96f);
        pendingDpi = 0;
        if (next == scale) return;
        scale = next;
        gui.RebuildFonts(BodyFontPath, 16 * scale);
        gui.MakeCurrent();
        StudioTheme.Apply(scale);
        missionGraph.CodeFont = gui.MonospaceFont;
        foreach (var popout in popouts) popout.Rescale(scale);
    }

    /** The initial picker stays open until the chosen folder resolves to installed packages. */
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            scale = Math.Max(1, DeviceDpi / 96f);
            renderer = new Dx11Renderer(Handle, ClientSize.Width, ClientSize.Height, options.Warp);
            loadingLogo = StudioBrand.CreateTexture(renderer);
            gui = new ImGuiController(renderer, this, BodyFontPath, 16 * scale);
            missionGraph.CodeFont = gui.MonospaceFont;
            StudioTheme.Apply(scale);
            viewport = new AssetViewport(renderer)
                { UseGameShaders = settings.GameShaders, ShowGameShaderToggle = false, FullDetail = settings.FullDetail };
            assetViewport = new AssetViewport(renderer)
                { UseGameShaders = settings.GameShaders, ShowGameShaderToggle = false, FullDetail = settings.FullDetail };
            viewport.EffectControls = assetViewport.EffectControls = effectControls;
            viewport.ObjectName = source => ProjectName(source) ?? sceneTree?.Find(source)?.Name ?? SceneObjectName(source);
            assetViewport.ObjectName = SceneObjectName;
            viewport.ShowCursor = settings.ShowAssetCursor;
            assetViewport.ShowCursor = settings.ShowAssetCursor;
            viewport.PreparationProgress = ReportPreparation;
            assetViewport.PreparationProgress = ReportPreparation;
            viewport.SelectionChanged += OnViewportSelection;
            RestorePopouts();
            assetViewport.SelectionMode = SceneSelectionMode.MeshPart;
            assetViewport.SelectionChanged += value =>
            {
                selectedMesh = value as MeshInstance;
                inspectAsset = true;
                if (value is not null) collisionInspected = null;
            };
            var sceneView = viewport; var assetView = assetViewport;
            sceneView.CollisionSelectionChanged += placement => OnCollisionSelection(sceneView, placement);
            assetView.CollisionSelectionChanged += placement => OnCollisionSelection(assetView, placement);
            if (location.Length == 0) setup = true;
            else
            {
                try { workspace.OpenInstallation(location, keySource); }
                catch (Exception error) { workspace.Error = error.Message; setup = true; }
            }
            lastFrame = clock.Elapsed.TotalSeconds;
            timer.Start();
        }
        catch (Exception error) { Failure = error.ToString(); Close(); }
    }

    /** Worker results are adopted before the frame; native dialogs cannot reenter rendering. */
    private void TickFrame()
    {
        if (drawing || nativeDialog || closed || renderer is null || gui is null || viewport is null
            || WindowState == FormWindowState.Minimized) return;
        // An attached game shares this GPU; Studio draws at most 30 frames a second, and 10 in the background.
        if (livePage.IsAttached && !options.Smoke
            && clock.Elapsed.TotalSeconds - lastFrame < (ContainsFocus ? 1 / 30.0 : 1 / 10.0)) return;
        drawing = true;
        long profileStart = Stopwatch.GetTimestamp();
        long profileBytes = options.ProfileFrames is null ? 0 : GC.GetAllocatedBytesForCurrentThread();
        try
        {
            workspace.Poll();
            foreach (var audio in pages.Pages.OfType<AudioPage>()) audio.Update(workspace);
            if (changesOnly && projectRevision != workspace.ProjectRevision) browserDirty = true;
            projectRevision = workspace.ProjectRevision;
            PollStartup();
            AdoptDocument();
            AdoptPlacementUpdates();
            PollCollisionLayer();
            livePage.Poll(workspace, viewport);
            PollStartup();
            RefreshWorldLabels();
            PrepareMissionRows();
            PrepareWorldBrowser();
            if (nativeRevision != workspace.NativeRevision)
            {
                nativeRevision = workspace.NativeRevision;
                if (workspace.NativePlan is { } plan && ReferenceEquals(workspace.NativePlanScene, viewport.Scene)) workspace.Run(() =>
                {
                    viewport.SetNativeScene(plan);
                    viewport.NativeRenderer!.LightingMode = settings.LightingMode;
                    WarmViewport(viewport);
                    // Promote the load's objects behind the loading screen, so the first collection after it is short.
                    GC.Collect(1, GCCollectionMode.Forced, blocking: true);
                });
            }
            presentationProgress = null;
            if (assetRevision != workspace.AssetRevision) { RebuildBrowser(); browserPreparing = true; }
            PollAssetFilter();
            if (filterWork is null) browserPreparing = false;
            viewport.Animation?.Tick(0);
            assetViewport?.Animation?.Tick(0);
            UpdateLiveInspection();
            foreach (var page in pages.Pages.OfType<InvestmentPage>()) page.PrepareDetail(workspace, renderer);
            EditorReady = !setup && !workspace.IsBlocking && !browserPreparing && workspace.Catalog is not null
                && viewport.Animation?.IsLoading != true && assetViewport?.Animation?.IsLoading != true;
            double now = clock.Elapsed.TotalSeconds;
            ApplyPendingDpi();
            renderer.Resize(ClientSize.Width, ClientSize.Height);
            guiFrameActive = true;
            shownPage = null;
            float frameDelta = (float)Math.Clamp(now - lastFrame, 0.001, 0.1);
            gui.NewFrame(frameDelta);
            ShortcutInput.BeginFrame(gui.IsInputActive && EditorReady && !workspace.IsBusy);
            lastFrame = now;
            renderer.BeginFrame(StudioTheme.Canvas);
            long drawStart = Stopwatch.GetTimestamp();
            if (EditorReady && workspace.IsBusy)
            {
                // A background command keeps the editor in view but takes no input until it ends.
                ImGui.BeginDisabled(); DrawEditor(); ImGui.EndDisabled();
            }
            else if (EditorReady) { HandleShortcuts(); DrawEditor(); HandleViewShortcuts(); }
            else DrawLoading();
            chrome.EndFrame();
            long submitStart = Stopwatch.GetTimestamp();
            gui.Render();
            if (EditorReady && options.ProfileFrames is not null)
                RecordFrameProfile(profileStart, drawStart, submitStart, profileBytes);
            guiFrameActive = false;
            CaptureLoadingFrame();
            DrawPopouts(frameDelta);
            if (EditorReady) TrackNavigation();
            if (options.PickCenter && !initialCenterPicked && EditorReady
                && (!options.WaitForNames || !workspace.IsNaming) && viewport.NativeRenderer is { } pickingRenderer)
            {
                initialCenterPicked = true;
                var viewSize = new Vector2(pickingRenderer.Targets.Width, pickingRenderer.Targets.Height);
                viewport.SelectAt(viewSize / 2, viewSize);
                focusHierarchy = true;
            }
            if (options.MissionView && !initialMissionView && EditorReady && workspace.World?.MissionTag is not null)
            { initialMissionView = true; ShowMissionEditor(); }
            FrameRendered?.Invoke();
            if (closed) return;
            if (options.Smoke && (EditorReady || !workspace.IsBusy && (workspace.Error.Length != 0 || setup))
                && (!options.WaitForNames || !workspace.IsNaming && !workspace.IsCatalogingWorlds)
                && ++smokeFrames >= options.Frames)
            {
                if (options.Capture is { } output)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    renderer.CapturePng(output);
                }
                if (workspace.Error.Length != 0) Failure = workspace.Error;
                Console.WriteLine($"studio frames={smokeFrames} assets={workspace.Assets.Length} " +
                    $"meshes={viewport.MeshCount} effect_particles={viewport.EffectParticleCount} " +
                    $"adapter={renderer.AdapterName.Replace(' ', '_')}");
                if (workspace.Catalog?.DiskCache is { } cache)
                {
                    var statistics = cache.Statistics;
                    Console.WriteLine($"studio_cache state={workspace.Names?.CacheStatus.Replace(' ', '_')} " +
                        $"bytes={statistics.StoredBytes} hits={statistics.Hits}");
                    if (statistics.LastError is { } cacheError)
                        Console.WriteLine($"studio_cache error={System.Text.Json.JsonSerializer.Serialize(cacheError)}");
                }
                Close();
                return;
            }
            if (options.Smoke && clock.Elapsed.TotalMinutes > 5)
                throw new TimeoutException("Studio smoke test timed out.");
            renderer.EndFrame(!options.Smoke);
        }
        catch (Exception error)
        {
            Failure = error.ToString();
            if (options.Smoke) Close();
            else { workspace.Error = error.Message; timer.Stop(); MessageBox.Show(this, error.Message, Text); Close(); }
        }
        finally { drawing = false; guiFrameActive = false; }
    }

    /** Runs each command-line open once, in order, when the workspace is idle. */
    private void PollStartup()
    {
        if (workspace.IsBusy || workspace.Catalog is null) return;
        if (!initialProjectOpened && options.Project is { } project)
        {
            initialProjectOpened = true;
            workspace.Run(() => workspace.OpenProject(project));
            return;
        }
        if (!initialTagOpened && options.Map is { } map)
        {
            initialTagOpened = true;
            if (options.Mission is { } mission) workspace.OpenMission(map, mission);
            else workspace.OpenMap(map);
            return;
        }
        if (!initialTagOpened && (options.Tag is not null || options.Category is not null))
        {
            // One lookup at startup; a category name opens its first row, a full kind its first match.
            initialTagOpened = true;
            uint? initialTag = options.Tag ?? (StartupCategory() is { } named
                ? workspace.CategoryRows.GetValueOrDefault(named.Name)?.FirstOrDefault()
                : workspace.Assets.FirstOrDefault(asset => asset.Kind.Equals(options.Category, StringComparison.OrdinalIgnoreCase)))
                ?.Entry.TagHash;
            if (initialTag is { } tag) { workspace.Select(tag); return; }
        }
        if (!initialNativePrepared && options.NativeScene && workspace.ActiveScene is not null)
        { initialNativePrepared = true; settings.GameShaders = true; viewport!.UseGameShaders = true; }
        if (!initialClipSelected && options.AnimationClip is { } clip && viewport?.Animation is { } animation)
        { initialClipSelected = true; animation.SelectClip(clip); }
        if (!initialMissionScript && options.MissionScript is { } script && workspace.Catalog is not null)
        {
            initialMissionScript = true;
            missionGraph.OpenScript(script, workspace);
            focusMissionGraph = true;
        }
        if (!initialPageSelected && options.Page is { } page
            && (workspace.Document is not null || page is "Mission Graph" or "Live"))
        { initialPageSelected = true; ShowAssetPage(page); }
    }

    /** The category --category names, matched without case; null when it names a kind instead. */
    private StudioCategory? StartupCategory() => options.Category is { } name
        ? StudioCategories.All.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) : null;

    /** Loads a new document or scene into the views; a reload of the same map or asset keeps camera and selection. */
    private void AdoptDocument()
    {
        if (documentRevision == workspace.DocumentRevision && sceneRevision == workspace.SceneRevision) return;
        bool sceneChanged = sceneRevision != workspace.SceneRevision;
        // A world reload can keep the asset tab's document; its uploads then stay.
        bool documentChanged = !ReferenceEquals(adoptedDocument, workspace.Document);
        adoptedDocument = workspace.Document;
        sceneRevision = workspace.SceneRevision;
        documentRevision = workspace.DocumentRevision;
        var nextAsset = workspace.Document is { } document
            ? (document.Entry.TagHash, document.AddedAssetId) : ((uint, string?)?)null;
        bool preserveView = workspace.World is { } world ? adoptedMap == world.Map.Tag
            : adoptedMap is null && viewport!.Scene is not null && adoptedAsset is not null && nextAsset == adoptedAsset;
        var eye = viewport!.Camera.Position;
        var pivot = viewport.Camera.Pivot;
        bool orthographic = viewport.Camera.Orthographic;
        var priorMesh = viewport.SelectedObject as MeshInstance;
        string? priorObject = selectedWorldId, priorGroup = selectedGroupId;
        RememberCursorPreferences();
        adoptedAsset = nextAsset;
        adoptedMap = workspace.World?.Map.Tag;
        selectedProperty = null;
        selectedMesh = null;
        if (documentChanged && workspace.Document?.Connections?.SelectedClipTag is > 0) ShowAssetPage("sunrise.preview");
        try
        {
            ReportPreparation("Preparing asset views", 0, 0);
            if (workspace.World is null) assetViewport!.Clear();
            if (documentChanged)
                foreach (var page in pages.Pages.OfType<IPreparedStudioPage>()) page.Prepare(workspace.Document, renderer!);
            if (sceneChanged)
            {
                isolatedObject = null; isolatedMembers.Clear();
                // A reload keeps the old uploads until SetScene takes the unchanged ones; a new scene frees them first.
                if (!preserveView || workspace.ActiveScene is null) viewport!.Clear();
                if (!preserveView)
                {
                    viewport.Visibility.HiddenLayers.Clear(); viewport.Visibility.HiddenBubbles.Clear();
                    viewport.Visibility.HiddenGeometryLayers.Clear(); viewport.Visibility.HiddenOverlayLayers.Clear();
                    viewport.Visibility.ShowCollisionContent = viewport.Visibility.ShowCollisionBoxes = false;
                }
                viewport.SelectionMode = workspace.World is null ? SceneSelectionMode.MeshPart : SceneSelectionMode.Object;
                viewport.ShowCursor = workspace.World is null ? settings.ShowAssetCursor : settings.ShowSceneCursor;
                viewport.OccludeOverlays = workspace.World is not null;
                viewport.UseGameShaders = settings.GameShaders;
                viewport.Catalog = workspace.Catalog;
                if (workspace.ActiveScene is { } scene) viewport.SetScene(scene);
                else if (workspace.Document?.Texture is { } texture) viewport.SetTexture(texture);
                if (workspace.World is null) AdoptAssetAnimation(viewport);
                RebuildWorldInspection();
                if (preserveView)
                {
                    viewport.Camera.LookFrom(eye, pivot);
                    viewport.Camera.Orthographic = orthographic;
                    if (priorGroup is not null && workspace.World is not null) SelectObjectGroup(priorGroup);
                    else if (priorObject is not null && inspectionWorld?.Objects.FirstOrDefault(item => item.Id == priorObject) is { } row)
                        SelectWorldObject(row);
                    else if (priorMesh is not null && workspace.ActiveScene?.Instances.FirstOrDefault(instance =>
                        instance.Mesh.TagHash == priorMesh.Mesh.TagHash && instance.Mesh.PartIndex == priorMesh.Mesh.PartIndex
                        && instance.InstanceSourceTag == priorMesh.InstanceSourceTag
                        && instance.InstanceSourceIndex == priorMesh.InstanceSourceIndex
                        && instance.PlacementSourceTag == priorMesh.PlacementSourceTag
                        && instance.PlacementSourceOffset == priorMesh.PlacementSourceOffset
                        && instance.PlacementIdentity == priorMesh.PlacementIdentity
                        && instance.BubbleHash == priorMesh.BubbleHash && instance.ActivityTag == priorMesh.ActivityTag
                        && instance.StateOrdinal == priorMesh.StateOrdinal) is { } restored)
                        viewport.SetSelection(restored);
                }
                else
                {
                    hiddenBubbles.Clear();
                    var spawn = workspace.World?.MissionScene?.Markers
                        .Where(marker => marker.Layer == SceneLayer.Spawns)
                        .OrderByDescending(marker => workspace.World.AllStates.Any(state => state.PackageEnabled
                            && state.BubbleNameHash == marker.BubbleHash && state.Ordinal == marker.StateOrdinal)).FirstOrDefault()
                        ?? workspace.World?.MapScene.Markers.FirstOrDefault(marker => marker.Layer == SceneLayer.Spawns);
                    if (workspace.World is not null)
                    {
                        viewport.Visibility.HiddenOverlayLayers.Add(SceneLayer.Transitions);
                        viewport.Visibility.HiddenGeometryLayers.Add(SceneLayer.Transitions);
                        viewport.Visibility.HiddenOverlayLayers.Add(SceneLayer.Squads);
                        viewport.Visibility.HiddenOverlayLayers.Add(SceneLayer.Effects);
                        viewport.Visibility.HiddenOverlayLayers.Add(SceneLayer.Lights);
                    }
                    if (spawn is not null)
                    {
                        var forward = Vector3.Transform(Vector3.UnitX, spawn.Rotation);
                        forward.Z = 0;
                        forward = forward.LengthSquared() > .0001f ? Vector3.Normalize(forward) : Vector3.UnitX;
                        var spawnEye = spawn.Position + Vector3.UnitZ * 1.7f;
                        viewport.Camera.LookFrom(spawnEye, spawnEye + forward * 10);
                    }
                }
                viewport.Visibility.Filter = WorldVisibility;
                viewport.Visibility.FilterRevision = WorldVisibilityStamp;
                if (workspace.NativePlan is null) WarmViewport(viewport);
            }
            if (workspace.World is not null && documentChanged)
            {
                assetViewport!.Clear();
                assetViewport.ShowCursor = settings.ShowAssetCursor;
                assetViewport.UseGameShaders = settings.GameShaders;
                assetViewport.Catalog = workspace.Catalog;
                if (workspace.Document?.Texture is { } texture) assetViewport.SetTexture(texture);
                else if (workspace.Document?.Scene is { } scene) assetViewport.SetScene(scene);
                AdoptAssetAnimation(assetViewport);
                if (workspace.Document?.NativePlan is { } plan)
                {
                    assetViewport.SetNativeScene(plan);
                    assetViewport.NativeRenderer!.LightingMode = settings.LightingMode;
                }
                WarmViewport(assetViewport);
            }
        }
        catch (Exception error) { workspace.Error = error.Message; }
        Text = workspace.Project is { } project ? $"{project.Name} - Sunrise Studio" : "Sunrise Studio";
    }

    private static string? SceneObjectName(object source) => source switch
    {
        MeshInstance mesh => mesh.Name,
        SceneMarker marker => marker.Name,
        ScenePointProxy point => point.Name,
        _ => null
    };

    private void RememberCursorPreferences()
    {
        if (viewport is not null)
        {
            if (adoptedMap is not null) settings.ShowSceneCursor = viewport.ShowCursor;
            else settings.ShowAssetCursor = viewport.ShowCursor;
        }
        if (adoptedMap is not null && assetViewport is not null) settings.ShowAssetCursor = assetViewport.ShowCursor;
    }

    /** Clip navigation prepares the selected owner's pose before the editor is revealed; a guardian holds its weapon. */
    private void AdoptAssetAnimation(AssetViewport target)
    {
        if (workspace.Document?.Animation is not { } source) return;
        target.SetAnimation(source);
        if (workspace.Document.Connections?.SelectedClipTag is > 0 and var clip)
            target.Animation!.SelectClip(clip);
        else if (workspace.Document.Guardian is { PoseClipTag: > 0 and var hold })
            target.Animation!.SelectClip(hold, play: false);
    }

    /** Menu shortcuts work anywhere; text input and the Shortcuts window suppress them. */
    private void HandleShortcuts()
    {
        if (ShortcutInput.Pressed(ShortcutActions.NewProject)) projectDialog = true;
        if (ShortcutInput.Pressed(ShortcutActions.OpenProject)) OpenProjectDialog();
        if (ShortcutInput.Pressed(ShortcutActions.Undo)) UndoActive();
        if (ShortcutInput.Pressed(ShortcutActions.Redo)) RedoActive();
        if (ShortcutInput.Pressed(ShortcutActions.SaveProject)) SaveActiveDocument();
        HandleNavigationShortcuts();
        if (!workspace.CanEdit) return;
        if (ShortcutInput.Pressed(ShortcutActions.BuildPackages)) workspace.Run(() => workspace.Build());
        if (ShortcutInput.Pressed(ShortcutActions.BuildAndInstall)) InstallMod();
        if (ShortcutInput.Pressed(ShortcutActions.UpdateRunningGame)) UpdateRunningGame();
        HandleCreateShortcuts();
    }

    /** These 3D actions act on the scene tree, so they need the scene viewport under the pointer. */
    private void HandleViewShortcuts()
    {
        if (viewport?.IsHovered != true) return;
        if (workspace.World is not null && ShortcutInput.Pressed(ShortcutActions.ToggleTree3D))
            settings.ShowSceneTree = !settings.ShowSceneTree;
        if (ShortcutInput.Pressed(ShortcutActions.HideSelection3D) && selectedTreeId is { } id) SetSceneNodeHidden(id, true);
        if (ShortcutInput.Pressed(ShortcutActions.ShowAll3D) && sceneTree is not null)
        { sceneTree.ShowAll(); liveInspection.ShowAll(); }
    }

    // An installed mission script is editable with no project open, so the page decides for itself.
    private bool CanUndoActive => missionGraphActive ? missionGraph.CanUndo : workspace.CanEdit && workspace.CanUndo;
    private bool CanRedoActive => missionGraphActive ? missionGraph.CanRedo : workspace.CanEdit && workspace.CanRedo;
    private bool CanSaveActive => missionGraphActive ? missionGraph.CanSave : workspace.CanEdit;
    private void UndoActive()
    { if (CanUndoActive) workspace.Run(missionGraphActive ? missionGraph.Undo : workspace.Undo); }
    private void RedoActive()
    { if (CanRedoActive) workspace.Run(missionGraphActive ? missionGraph.Redo : workspace.Redo); }
    private void SaveActiveDocument() => workspace.Run(() =>
    {
        if (!CanSaveActive) return;
        if (missionGraphActive) missionGraph.SaveCurrent(workspace);
        else workspace.Project?.Save();
    });

    private void PrepareGameShaders()
    {
        viewport!.UseGameShaders = settings.GameShaders;
        assetViewport!.UseGameShaders = settings.GameShaders;
    }

    /** Prepare both shading modes before the editor becomes visible. */
    private void WarmViewport(AssetViewport target)
    {
        if (!target.HasContent) return;
        ReportPreparation("Preparing first frame", 0, 0);
        bool requested = target.UseGameShaders;
        if (target.NativeSceneReady) target.UseGameShaders = true;
        target.NativeRenderer?.RequestExposureWarmup();
        try { target.RenderImage(Math.Max(1, ClientSize.Width / 2), Math.Max(1, ClientSize.Height / 2)); }
        finally { if (!requested) target.UseGameShaders = false; }
    }

    /** Area layout is local; projects keep no window or machine preferences. */
    private void DrawEditor()
    {
        DrawMenu();
        float menu = ImGui.GetFrameHeight();
        float status = 27 * scale, toolbar = 42 * scale;
        float width = ClientSize.Width, height = ClientSize.Height;
        // The menu bar and the toolbar are the caption wherever no control sits.
        chrome.Scale = scale;
        chrome.CaptionHeight = menu + toolbar;
        DrawToolbar(menu, toolbar);
        float top = menu + toolbar;
        float contentHeight = Math.Max(100, height - top - status);
        DrawLayout(new(0, top), new(width, contentHeight));
        DrawStatus(new(0, height - status), new(width, status));
        DrawDialogs();
    }

    // Panels tile the window; a clicked panel must not rise above its neighbours.
    private static readonly ImGuiWindowFlags PanelFlags = ImGuiWindowFlags.NoTitleBar |
        ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse |
        ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus;

    private static bool BeginPanel(string id, Vector2 position, Vector2 size,
        ImGuiWindowFlags extra = ImGuiWindowFlags.None)
    {
        ImGui.SetNextWindowPos(position);
        ImGui.SetNextWindowSize(size);
        return ImGui.Begin(id, PanelFlags | extra);
    }

    private static void Splitter(string id, Vector2 position, Vector2 size, Action<Vector2> drag)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        // ImGui grows a window to WindowMinSize; a splitter must stay its own few pixels wide.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, StudioTheme.Canvas);
        BeginPanel($"split-{id}", position, size, ImGuiWindowFlags.NoScrollbar);
        ImGui.InvisibleButton($"##{id}", size);
        if (ImGui.IsItemActive()) drag(ImGui.GetIO().MouseDelta);
        if (ImGui.IsItemHovered() || ImGui.IsItemActive())
            ImGui.SetMouseCursor(size.X < size.Y ? ImGuiMouseCursor.ResizeEW : ImGuiMouseCursor.ResizeNS);
        ImGui.End(); ImGui.PopStyleColor(); ImGui.PopStyleVar(3);
    }

    private void DrawDocument(Vector2 position, Vector2 size)
    {
        if (focusAssets || focusLive) ImGui.SetNextWindowFocus();
        BeginPanel("Document", position, size);
        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)) missionGraphActive = false;
        if (ImGui.BeginTabBar("workbench"))
        {
            bool assets = BeginEditorTab("Assets", focusAssets);
            if (assets)
            {
                focusAssets = false;
                DrawAssetDocument();
                ImGui.EndTabItem();
            }
            if (BeginEditorTab("Live", focusLive))
            {
                focusLive = false;
                shownPage = "live";
                livePage.Draw(workspace, viewport!, DrawSceneWithTree);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
        ImGui.End();
    }

    /** The open world, the open asset with its pages, or the start screen. */
    private void DrawAssetDocument()
    {
        if (workspace.World is null) focusAssetPage = false;
        if (workspace.World is not null) DrawWorldDocument();
        else if (workspace.Document is not { } document)
        {
            // Start below the tab bar; a fixed window position would put the title under the tabs.
            ImGui.SetCursorPos(new(28 * scale, ImGui.GetCursorPosY() + 24 * scale));
            if (loadingLogo is { } mark)
            {
                ImGui.Image(mark.ImGuiId, new(72 * scale));
                ImGui.SetCursorPosX(28 * scale);
            }
            ImGui.PushFont(gui!.TitleFont);
            ImGui.TextColored(StudioTheme.Accent, "SUNRISE STUDIO");
            ImGui.PopFont();
            ImGui.SetCursorPosX(28 * scale); ImGui.TextDisabled("Select an asset to begin");
            ImGui.Spacing();
            ImGui.SetCursorPosX(28 * scale);
            if (ImGui.Button("New project")) projectDialog = true;
            ImGui.SameLine();
            if (ImGui.Button("Open project")) OpenProjectDialog();
        }
        else
        {
            DrawAssetHeader(document);
            if (document.PreviewError.Length != 0)
            {
                ImGui.TextDisabled("Preview notes");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(document.PreviewError);
            }
            DrawDocumentPages(document, "asset:", new(workspace, viewport!, property =>
                { selectedProperty = property; propertyValue = property.Value; },
                mesh => viewport!.SetSelection(mesh), selectedMesh, gui!.MonospaceFont, ExportFullAsset));
        }
    }

    // Last tab a category bar drew; the model pane hides while that tab draws its own view.
    private (string Bar, string Page) documentTab = ("", "");

    /**
     * Draws a document's pages: a flat tab bar, or for a category document the model pane beside its tabs.
     * @param shownPrefix Prefix of the shown-page id the navigation history records.
     */
    private void DrawDocumentPages(StudioDocument document, string shownPrefix, StudioPageContext context)
    {
        DrawCreationBar(document);
        string bar = shownPrefix + "pages";
        if (StudioCategories.ForKind(document.Kind) is not { } category)
        {
            DrawPageTabs(bar, pages.Pages.Where(page => page.CanOpen(document)), shownPrefix, context);
            return;
        }
        bool hasView = document.Scene is not null || document.Texture is not null;
        bool modelView = category.ModelPane && hasView;
        // The pane is the preview, so a request to show it is already met.
        if (modelView && focusDocumentPage is { } focus && (focus.Equals(StudioCategories.PreviewPageId, StringComparison.OrdinalIgnoreCase)
            || focus.Equals("Scene", StringComparison.OrdinalIgnoreCase)))
            focusDocumentPage = null;
        if (category.ModelPane && !hasView && document.PreviewError.Length != 0)
        {
            ImGui.TextDisabled("Model"); ImGui.SameLine(); ImGui.TextDisabled("not readable");
            StudioUi.Tip(document.PreviewError);
        }
        var tabs = StudioCategories.Tabs(category, pages.Pages, document, modelView);
        if (category.Sections.Length != 0 && !tabs.Any(page => page.Id == category.Sections[0]))
            ImGui.TextDisabled($"No {category.Label} view for this type");
        // A bar keeps its selected tab when the next document has that tab too.
        bool ownView = documentTab.Bar == bar && StudioCategories.OwnViewPages.Contains(documentTab.Page)
            && tabs.Any(page => page.Id == documentTab.Page);
        if (!modelView || ownView)
        {
            // Same child as the pane layout, so the tab bar keeps one id and its selection.
            ImGui.BeginChild("category-pages", Vector2.Zero, ImGuiChildFlags.None);
            if (DrawPageTabs(bar, tabs, shownPrefix, context) is { } shown) documentTab = (bar, shown);
            ImGui.EndChild();
            return;
        }
        var available = ImGui.GetContentRegionAvail();
        bool wide = available.X > 900 * scale;
        ImGui.BeginChild("model-pane", wide ? new(available.X * .55f, available.Y)
            : new(available.X, MathF.Max(150 * scale, available.Y * .45f)), ImGuiChildFlags.None);
        pages.Pages.FirstOrDefault(page => page.Id == StudioCategories.PreviewPageId)?.Draw(context);
        ImGui.EndChild();
        if (wide) ImGui.SameLine();
        ImGui.BeginChild("category-pages", Vector2.Zero, ImGuiChildFlags.None);
        if (DrawPageTabs(bar, tabs, shownPrefix, context) is { } drawn) documentTab = (bar, drawn);
        ImGui.EndChild();
    }

    /**
     * Tabs keep their full labels; tabs that do not fit scroll and stay in the list button.
     * @return Id of the page drawn this frame; null when none drew.
     */
    private string? DrawPageTabs(string bar, IEnumerable<IStudioPage> tabs, string shownPrefix, StudioPageContext context)
    {
        if (!ImGui.BeginTabBar(bar, ImGuiTabBarFlags.FittingPolicyScroll | ImGuiTabBarFlags.TabListPopupButton)) return null;
        string? drawn = null;
        foreach (var page in tabs)
            if (BeginDocumentPage(page))
            {
                shownPage = shownPrefix + page.Id;
                drawn = page.Id;
                page.Draw(context);
                ImGui.EndTabItem();
            }
        ImGui.EndTabBar();
        return drawn;
    }

    /** Consume page focus only when the requested tab opens. */
    private bool BeginDocumentPage(IStudioPage page)
    {
        bool focus = string.Equals(focusDocumentPage, page.Id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(focusDocumentPage, page.Title, StringComparison.OrdinalIgnoreCase);
        bool open = BeginEditorTab($"{page.Title}##{page.Id}", focus);
        if (focus && open) focusDocumentPage = null;
        return open;
    }

    /** Background progress first, then the notice, then the error or status text. */
    private void DrawStatus(Vector2 position, Vector2 size)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12, 5) * scale);
        BeginPanel("Status", position, size, ImGuiWindowFlags.NoScrollbar);
        if (workspace.IsBusy && !workspace.IsBlocking)
        {
            // A background command shows its stage and a moving bar in place of the status text.
            var progress = workspace.LoadingProgress;
            ImGui.TextColored(StudioTheme.Accent, $"{workspace.Status}: {progress.Stage}");
            ImGui.SameLine();
            DrawProgressBar(progress, 160 * scale, ImGui.GetTextLineHeight() * .5f);
            ImGui.End(); ImGui.PopStyleVar();
            return;
        }
        if (workspace.Notice is { } notice)
        {
            ImGui.TextColored(notice.Failed ? new Vector4(1, .45f, .35f, 1) : StudioTheme.Accent, notice.Text);
            StudioUi.Tip(notice.Details ?? notice.Text);
            if (notice.Folder is { } folder && Directory.Exists(folder))
            {
                ImGui.SameLine();
                if (ImGuiIcons.Button("status-folder", StudioIcon.Folder, "Open folder", size: ImGui.GetTextLineHeight()))
                    Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Dismiss")) workspace.DismissNotice();
            ImGui.End(); ImGui.PopStyleVar();
            return;
        }
        string message = workspace.Error.Length != 0 ? workspace.Error : workspace.Status;
        if (workspace.Error.Length == 0 && workspace.BackgroundStatus.Length != 0)
            message += "  |  " + workspace.BackgroundStatus;
        ImGui.TextColored(workspace.Error.Length != 0 ? new Vector4(1, .45f, .35f, 1)
            : StudioTheme.Muted, message);
        StudioUi.Tip(message);
        ImGui.End(); ImGui.PopStyleVar();
    }

    /** Closing a tool cannot silently discard an unsaved Lua draft. */
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!ConfirmMissionDraft()) e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        closed = true;
        RememberCursorPreferences();
        try { WriteFrameProfile(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Failure ??= error.Message; }
        timer.Stop(); timer.Dispose();
        filterCancellation?.Cancel(); filterCancellation?.Dispose();
        RememberPopouts();
        foreach (var popout in popouts) popout.Dispose();
        popouts.Clear();
        livePage.Dispose();
        foreach (var page in pages.Pages.OfType<IDisposable>()) page.Dispose();
        assetViewport?.Dispose(); viewport?.Dispose(); workspace.Dispose();
        loadingLogo?.Dispose(); toolbarLogo?.Dispose(); gui?.Dispose(); renderer?.Dispose(); studioIcon.Dispose();
        if (!options.Smoke)
        {
            try { settings.Save(settingsPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        base.OnFormClosed(e);
    }

    /** A settings write that fails shows in the status line; it must not close Studio. */
    private void SaveSettings()
    {
        if (options.Smoke) return;
        try { settings.Save(settingsPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { workspace.Error = error.Message; }
    }
}
