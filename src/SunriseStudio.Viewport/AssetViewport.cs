using System.Buffers;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using ImGuiNET;
using SunriseStudio.Animation;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using Vortice.DXGI;

namespace SunriseStudio.Viewport;

public readonly record struct ViewportFrameTimings(double VisibilityMilliseconds, double NativeMilliseconds,
    double DisplayMilliseconds, double FallbackMilliseconds, double OverlayMilliseconds,
    double BackdropMilliseconds, double CullMilliseconds, double VisibleListMilliseconds,
    long VisibilityAllocationBytes);

/** The renderer and ImGui context belong to the host and must outlive this viewport. */
public sealed partial class AssetViewport(Dx11Renderer renderer) : IDisposable
{
    private readonly Dictionary<MeshData, GpuMesh> meshes = [];
    private readonly SceneSelection selection = new();
    private readonly Dictionary<MeshInstance, List<int>> nativeObjectHandles = [];
    private readonly List<int> visibleItems = [];
    private readonly List<MeshInstance> visibleMeshes = [];
    private readonly List<MeshInstance> visibleVolumes = [];
    private readonly List<(int Index, SceneMarker Marker)> visibleMarkers = [];
    private readonly List<ScenePointProxy> visiblePoints = [];
    private readonly List<LineVertex> overlayLines = [];
    private readonly List<LineVertex> selectedOverlayLines = [];
    private int[] nativeHandleOffsets = [], nativeHandleMap = [];
    private enum RenderItemKind : byte { NativeMesh, FallbackMesh, UnsupportedSky, Volume, Marker, Point }
    private RenderItemKind[] itemKinds = [];
    private int[] markerLineStarts = [];
    private LineVertex[] markerLines = [];
    private bool visibleGeometryCollected;
    private bool[] backdropItems = [];
    private uint[] itemBubbles = [];
    // Indices of backdrop items with a bubble: the only items a bubble change can show or hide.
    private int[] backdropIndices = [];
    // Per item: its decorator cell shifted left 5, ORed with the levels it draws at.
    // -1 when the item's handles do not share one decorator LOD test, and for a volume.
    private int[] itemDecorators = [];
    private bool anyItemDecorators;
    private bool renderIndexDirty = true;
    private Func<int, bool>? renderVisibility;
    private Predicate<object>? previewContextFilter;
    // The inputs of the last cull; the same inputs give the same visible lists.
    private readonly Vector4[] culledPlanes = new Vector4[6];
    private Vector3 culledEye;
    private float culledPixelsPerUnit;
    private bool culledOrthographic, cullReusable;
    private long culledRevision;
    private uint culledBubble;
    private SceneBackdropMode culledMode;
    private object? culledGrid, culledOffsets, culledHandles, culledKinds, culledBackdrop;
    private (bool Native, int Detail, int Centers) culledDecorators;
    // RenderItemVisible per grid cell for one policy; see ShownCells.
    private SceneGrid.ShownCells? shownCells;
    private (long Revision, uint Bubble, SceneBackdropMode Mode) shownKey;
    private object? shownGrid, shownKinds, shownBackdrop;
    /** Times the reusable cull prepared its shown items again; a camera move alone never does. */
    public int ShownCellBuildCount { get; private set; }
    // Caster hide state inputs; casterRevision changes when any of them does.
    private (long Visibility, uint Bubble, bool All, object? Bubbles) casterInputs = (-1, 0, false, null);
    private long casterRevision;
    // Canonical bubbles of backdrop shadow-stage parts; only these bubbles change a caster's answer.
    private HashSet<uint> casterBubbles = [];
    private readonly List<MeshInstance> selectedParts = [];
    private readonly List<object> selectedMembers = [];
    private readonly HashSet<object> selectedMemberSet = new(ReferenceEqualityComparer.Instance);
    private readonly List<object> selectionSources = [], selectedItems = [];
    private SceneSelectionMode selectionMode;
    private readonly Dictionary<(MeshData Mesh, bool Sky, bool Wire, FallbackTexture? Texture), List<Matrix4x4>> geometryBatches = [];
    private readonly List<MeshInstance> nativeFallback = [];
    private SceneGrid? sceneGrid;
    private SelectionOutlinePass? outline;
    private GpuMesh? markerBox;
    private bool gridDirty;
    private Vector2 pressPosition;
    private bool selecting;
    private bool doubleClickSelecting;
    private float lastAspect = 1;
    private uint atmosphereBubble;
    private float timeOfDay = NativeFrameInputs.ViewerTimeOfDay;
    private bool timeOfDayChosen;
    private NativeRenderTargets? target;
    private TextureData? textureData;
    private GpuTexture? texture;
    private int mip, slice, depthSlice;
    private bool wireframe, grid = true;
    private bool disposed;
    private int dragButton = -1;
    private string dragAction = "";
    private ShortcutButton selectButton;
    private long hoverFrame = -1;
    private static ShortcutMap? tipMap;
    private static int tipRevision;
    private static string[] toolbarTips = [];
    private float imageZoom = 1;
    private Vector2 imageOffset;
    private NativeSceneRenderer? nativeScene;
    private List<int> nativeHandles = [];
    private bool useGameShaders = true;
    private double simulationSeconds;
    private string? nativePoseError;
    private AnimationControlClip? framedAnimationClip;
    private bool animationPoseFramed;
    private SceneDocument? layerScene;
    private readonly HashSet<MeshData> posedMeshes = [];
    private IReadOnlySet<SceneLayer> presentLayers = new HashSet<SceneLayer>();

    public ViewerCamera Camera { get; } = new();
    public SceneVisibility Visibility { get; } = new();
    public SceneBackdrop Backdrop { get; } = new();
    public bool AutoAtmosphere { get; set; } = true;
    public Action<string, int, int>? PreparationProgress { get; set; }
    public bool MeasureFrameTimes { get; set; }
    /** Draws static parts at DetailLevel at any distance, with no far cull; off follows the game's distance rule. */
    public bool FullDetail { get; set; }
    /** Diagnostic comparisons can disable spatial reuse while preserving every visibility policy. */
    public bool CacheSpatialCandidates { get; set; } = true;
    public ViewportFrameTimings LastFrameTimings { get; private set; }
    /** The first selected object; null when nothing is selected. */
    public object? SelectedObject { get; private set; }
    /** Every selected object, in selection order; one entry except for a multi-object selection. */
    public IReadOnlyList<object> SelectedItems => selectedItems;
    public IReadOnlyList<MeshInstance> SelectedMeshParts => selectedParts;
    public IReadOnlyList<object> SelectedMembers => selectedMembers;
    public SelectionBounds SelectedBounds
    {
        get
        {
            if (selectedItems.Count == 1) return Bounds(selectedItems[0]);
            var bounds = SelectionBounds.Empty;
            foreach (var item in selectedItems)
                if (Bounds(item) is { IsFinite: true } next) bounds = bounds.Include(next.Minimum).Include(next.Maximum);
            return bounds;
            SelectionBounds Bounds(object item) =>
                selectionMode == SceneSelectionMode.Object ? selection.GroupBounds(item) : selection.GetBounds(item);
        }
    }
    public SceneSelectionMode SelectionMode
    {
        get => selectionMode;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (selectionMode == value) return;
            selectionMode = value;
            ApplySelection(selectionSources.ToArray(), true);
        }
    }
    public event Action<object?>? SelectionChanged;
    public SceneSelection Selection => selection;
    public int VisibleObjectCount => visibleItems.Count;
    /** Borrowed scene indexes remain valid until the next render or scene change. */
    public IReadOnlyList<int> VisibleIndices => visibleItems;
    public bool ShowCursor { get; set; } = true;
    public bool ShowCursorToggle { get; set; } = true;
    /** True when this viewport was under the pointer in the current frame. */
    public bool IsHovered => hoverFrame == ShortcutInput.Frame;
    public bool OccludeOverlays { get; set; } = true;
    public bool ShowGameShaderToggle { get; set; } = true;
    public SceneDocument? Scene { get; private set; }
    public bool HasContent => Scene is not null || textureData is not null;
    public bool Wireframe { get => wireframe; set => wireframe = value; }
    public bool ShowGrid { get => grid; set => grid = value; }
    public TextureData? Texture => textureData;
    public int MeshCount => meshes.Count;
    public bool NativeSceneReady => nativeScene is not null;
    public NativeSceneRenderer? NativeRenderer => nativeScene;
    /** Each scene starts at its authored time of day; a value set here holds across scenes. */
    public float TimeOfDay { get => timeOfDay; set { timeOfDay = value; timeOfDayChosen = true; } }
    public int NativeFallbackPartCount { get; private set; }
    public bool UseGameShaders { get => useGameShaders; set => useGameShaders = value; }
    public int NativePartCount => nativeScene?.ReadyPartCount ?? 0;
    public string? NativeError { get; private set; }
    public AssetAnimationPreview? Animation { get; private set; }

    /** Stop pending catalog reads without releasing textures borrowed by the current ImGui frame. */
    public void DetachAnimation()
    {
        if (Animation is null) return;
        Animation.Dispose();
        Animation = null;
        framedAnimationClip = null;
        animationPoseFramed = false;
        // Native parts return to the bind pose; plain meshes keep theirs until the scene reloads.
        nativeScene?.SetEntityPose(_ => null);
    }

    /** Animation preparation runs on a worker; adoption uploads only through the host renderer. */
    public void SetAnimation(AssetAnimationSource source)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Scene is null) throw new InvalidOperationException("Load geometry before animation.");
        var next = new AssetAnimationPreview(source, Scene, (mesh, vertices) =>
        {
            if (meshes.TryGetValue(mesh, out var gpu)) { renderer.UpdateMeshVertices(gpu, vertices); posedMeshes.Add(mesh); }
            selection.SetPose(mesh, vertices.Select(x => x.Position).ToArray());
            gridDirty = true;
        }, UpdateAnimationPose);
        Animation?.Dispose();
        Animation = next;
        framedAnimationClip = null;
        animationPoseFramed = false;
        Animation.ResetBindPose();
    }

    /** A new clip frames its evaluated pose once; playback preserves the inspection camera. */
    private void UpdateAnimationPose()
    {
        UpdateNativePose();
        if (Animation is not { } animation || animationPoseFramed
            && ReferenceEquals(framedAnimationClip, animation.Clip)) return;
        framedAnimationClip = animation.Clip;
        animationPoseFramed = true;
        if (!Camera.PivotFree) return;
        if (Scene?.Instances.Count > 0) Frame();
        else if (animation.DisplayPose.Rig.Bones.Count > 0)
        {
            var positions = animation.DisplayPose.ObjectTransforms.Select(x => x.Translation).ToArray();
            Camera.ResetReach();
            Camera.Frame(positions.Aggregate(new Vector3(float.PositiveInfinity), Vector3.Min),
                positions.Aggregate(new Vector3(float.NegativeInfinity), Vector3.Max), lastAspect);
        }
    }

    private void UpdateNativePose()
    {
        if (nativeScene is null || Animation is null) return;
        try { nativeScene.SetEntityPose(Animation.SkinFor, Animation.ClothStreamFor); nativePoseError = null; }
        catch (NotSupportedException error)
        {
            NativeError = nativePoseError = error.Message;
            useGameShaders = false;
        }
    }

    /** Ambient intensity is an inspection value; the installed default is zero. */
    public static NativeScenePlan PrepareNativeScene(PackageCatalog catalog, SceneDocument scene,
        CancellationToken token = default, Action<string>? progress = null,
        long textureBudgetBytes = NativeScenePlan.DefaultTextureBudgetBytes, bool assetPreview = false,
        IDictionary<uint, TextureData>? textureCache = null) =>
        NativeScenePlan.Prepare(catalog, scene, token, progress, textureBudgetBytes,
            new Dictionary<uint, Vector4> { [NativeSceneRenderer.AmbientIntensityKey] = Vector4.UnitX }, assetPreview,
            textureCache);

    /** The plan must describe the current scene; GPU uploads run on the host's graphics thread. */
    public void SetNativeScene(NativeScenePlan plan)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Scene is null) throw new InvalidOperationException("Load geometry before game shaders.");
        NativeSceneRenderer next;
        try { next = new NativeSceneRenderer(renderer, plan, target?.Width ?? 640, target?.Height ?? 480,
            preparationProgress: PreparationProgress); }
        catch (Exception error)
        {
            NativeError = error.Message;
            throw;
        }
        ClearLivePoseGeometry();
        nativeScene?.ClearLivePalettes();
        ReleaseRuntimeClasses();
        LivePrototypeRevision++;
        nativeScene?.Dispose();
        nativeScene = next;
        next.DrawForwardEffects = DrawEffectsNative;
        // Casters follow the colour parts' filters, including the backdrop bubble: bubbles can share world space.
        next.ShadowCasterFilter = item => Visibility.IsCasterVisible(item) && Backdrop.IsVisible(item);
        next.ShadowCasterRevision = ShadowCasterRevision;
        // Lights follow the backdrop's bubble, as the parts of their table do.
        next.LightFilter = bubble => Backdrop.Mode == SceneBackdropMode.All || Backdrop.ActiveBubbleHash == 0 || bubble == 0
            || Backdrop.CanonicalBubble(bubble) == Backdrop.ActiveBubbleHash;
        atmosphereBubble = 0;
        nativeHandles.Clear();
        nativeObjectHandles.Clear();
        renderIndexDirty = true;
        nativeFallback.Clear();
        NativeFallbackPartCount = 0;
        int indexedParts = 0;
        int totalParts = next.ReadyPartCount;
        PreparationProgress?.Invoke("Indexing native parts", 0, totalParts);
        foreach (var part in next.PartHandles())
        {
            if (!nativeObjectHandles.TryGetValue(part.Instance, out var handles))
                nativeObjectHandles.Add(part.Instance, handles = []);
            handles.Add(part.Handle);
            if (++indexedParts % 8192 == 0 || indexedParts == totalParts)
                PreparationProgress?.Invoke("Indexing native parts", indexedParts, totalParts);
        }
        BuildRenderIndex();
        NativeError = next.ReadyPartCount == 0 ? "No supported game-shader draws for this asset." : null;
        nativePoseError = null;
        UpdateNativePose();
        if (liveMeshes.Count > 0) nativeScene.RefreshLiveTransforms(liveMeshes);
    }

    /**
     * Call on the host's graphics thread. Mesh buffers are created on a worker while this thread
     * indexes the scene; a failure leaves the viewport empty and rethrows.
     */
    public void SetScene(SceneDocument scene)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var kept = new Dictionary<MeshData, GpuMesh>();
        var uploads = new List<MeshData>();
        foreach (var mesh in scene.Instances.Select(x => x.Mesh).Distinct())
        {
            if (mesh.Vertices.Length == 0 || mesh.Indices.Length == 0) continue;
            // A reload that kept the decoded mesh keeps its upload; an animated upload holds a pose.
            if (!posedMeshes.Contains(mesh) && meshes.Remove(mesh, out var gpu)) kept.Add(mesh, gpu);
            else uploads.Add(mesh);
        }
        var created = new GpuMesh?[uploads.Count];
        int uploaded = 0;
        bool stop = false;
        // Buffer creation uses only the device, which is free-threaded; binding stays on this thread.
        var work = Task.Run(() =>
        {
            if (uploads.Count == 0) return;
            var vertices = ArrayPool<RenderVertex>.Shared.Rent(uploads.Max(mesh => mesh.Vertices.Length));
            try
            {
                for (int index = 0; index < uploads.Count && !Volatile.Read(ref stop); index++)
                {
                    var mesh = uploads[index];
                    for (int vertex = 0; vertex < mesh.Vertices.Length; vertex++)
                    {
                        ref readonly var source = ref mesh.Vertices[vertex];
                        vertices[vertex] = new(source.Position, source.Normal, source.TexCoord);
                    }
                    created[index] = renderer.CreateMesh(vertices.AsSpan(0, mesh.Vertices.Length), mesh.Indices);
                    Interlocked.Increment(ref uploaded);
                }
            }
            finally { ArrayPool<RenderVertex>.Shared.Return(vertices); }
        });
        try
        {
            Clear();
            Scene = scene;
            if (!timeOfDayChosen) timeOfDay = NativeScenePlan.SkySource(scene).StartTime;
            selection.SetScene(scene, PreparationProgress is null ? null :
                (done, total) => PreparationProgress?.Invoke("Indexing scene", done, total));
            PreparationProgress?.Invoke("Preparing view", 0, 3);
            Visibility.SetScene(scene);
            PreparationProgress?.Invoke("Preparing view", 1, 3);
            Backdrop.SetScene(scene, selection);
            PreparationProgress?.Invoke("Preparing view", 2, 3);
            Visibility.ContextFilter = previewContextFilter ??= PreviewContextVisible;
            // A packfile or collision owner has nothing else to show.
            if (scene.Instances.Count == 0 && scene.Markers.Count == 0 && CollisionPossible(scene)) Visibility.ShowCollisionContent = true;
            gridDirty = true;
            Frame();
            PreparationProgress?.Invoke("Preparing view", 3, 3);
            // Progress presents a loading frame, so report at most every 50 ms.
            while (!work.Wait(50)) PreparationProgress?.Invoke("Uploading geometry", Volatile.Read(ref uploaded), uploads.Count);
            PreparationProgress?.Invoke("Uploading geometry", uploads.Count, uploads.Count);
        }
        catch
        {
            Volatile.Write(ref stop, true);
            try { work.Wait(); } catch (AggregateException) { }
            foreach (var gpu in created) gpu?.Dispose();
            foreach (var gpu in kept.Values) gpu.Dispose();
            Clear();
            if (work.Exception is { } failure) ExceptionDispatchInfo.Capture(failure.InnerExceptions[0]).Throw();
            throw;
        }
        foreach (var pair in kept) meshes.Add(pair.Key, pair.Value);
        for (int index = 0; index < uploads.Count; index++) meshes.Add(uploads[index], created[index]!);
    }

    /** Texture pixels stay in memory only; the project stores their edits separately. */
    public void SetTexture(TextureData data)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var first = data.Subresources.First(s => s.MipLevel == 0 && s.ArraySlice == 0);
        var next = renderer.CreateTexture(first.Width, first.Height, (Format)data.Format,
            first.Data.AsSpan(0, first.SlicePitch), first.RowPitch);
        Clear();
        textureData = data;
        texture = next;
        Frame();
    }

    /** Selection changes release GPU resources before the host replaces package data. */
    public void Clear()
    {
        ClearOverlayLines();
        ClearCollision();
        if (livePoseMeshes.Count > 0) ClearLivePoseGeometry();
        if (liveIndices.Count > 0) ClearLiveTransforms();
        ResetRuntimeClasses();
        Animation?.Dispose();
        Animation = null;
        framedAnimationClip = null;
        animationPoseFramed = false;
        nativeScene?.Dispose();
        nativeScene = null;
        nativeHandles.Clear();
        nativeObjectHandles.Clear();
        nativeFallback.Clear();
        NativeFallbackPartCount = 0;
        selectedParts.Clear();
        geometryBatches.Clear();
        visibleItems.Clear(); visibleMeshes.Clear(); visibleVolumes.Clear(); visibleMarkers.Clear(); visiblePoints.Clear();
        cullReusable = false;
        nativeHandleOffsets = []; nativeHandleMap = []; backdropItems = []; itemBubbles = []; backdropIndices = []; itemLod = [];
        itemDecorators = []; anyItemDecorators = false;
        itemKinds = [];
        markerLineStarts = []; markerLines = [];
        transitionLines.Clear(); transitionScene = null;
        renderIndexDirty = true;
        sceneGrid = null;
        selection.SetScene(new SceneDocument());
        Backdrop.SetScene(new SceneDocument(), selection);
        SetSelection(null);
        HoveredObject = null;
        NativeError = null;
        nativePoseError = null;
        foreach (var mesh in meshes.Values) mesh.Dispose();
        meshes.Clear();
        posedMeshes.Clear();
        texture?.Dispose();
        texture = null;
        textureData = null;
        Scene = null;
        mip = slice = depthSlice = 0;
        dragButton = -1;
        imageZoom = 1;
        imageOffset = Vector2.Zero;
    }

    /** Outliner and viewport share the original scene object reference. It clears a collision selection. */
    public void SetSelection(object? item) => SetSelectedItems(item is null ? [] : [item]);

    /** Selects several objects; SelectedObject is the first. Clears a collision selection. */
    public void SetSelectedItems(IReadOnlyList<object> items)
    {
        SetCollisionSelection(-1);
        ApplySelection(items, false);
    }

    /** Object mode reports one placement per item; part mode keeps the exact hits. */
    private void ApplySelection(IReadOnlyList<object> items, bool force)
    {
        foreach (var item in items)
            if (item is not (MeshInstance or SceneMarker or ScenePointProxy))
                throw new ArgumentException("Selection must have scene geometry or a point proxy.", nameof(items));
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var picked = items.Select(item => (Item: item,
                Resolved: selectionMode == SceneSelectionMode.Object ? selection.ResolveObject(item) : item))
            .Where(pair => seen.Add(pair.Resolved)).ToArray();
        bool same = picked.Length == selectedItems.Count
            && picked.Select(pair => pair.Resolved).Zip(selectedItems).All(pair => ReferenceEquals(pair.First, pair.Second));
        selectionSources.Clear();
        selectionSources.AddRange(items);
        if (!force && same) return;
        selectedItems.Clear();
        selectedItems.AddRange(picked.Select(pair => pair.Resolved));
        SelectedObject = selectedItems.FirstOrDefault();
        selectedParts.Clear();
        selectedMembers.Clear();
        selectedMemberSet.Clear();
        foreach (var (item, _) in picked)
        {
            if (selectionMode == SceneSelectionMode.Object)
            {
                selectedParts.AddRange(selection.MeshParts(item));
                selectedMembers.AddRange(selection.ObjectMembers(item));
            }
            else
            {
                if (item is MeshInstance mesh) selectedParts.Add(mesh);
                selectedMembers.Add(item);
            }
        }
        selectedMemberSet.UnionWith(selectedMembers);
        SelectionChanged?.Invoke(SelectedObject);
    }

    public bool IsVisible(object item) => Visibility.IsVisible(item);

    public void AddPointProxy(ScenePointProxy point)
    {
        selection.AddProxy(point);
        gridDirty = true;
    }

    /** F fits the selected object's full placement while preserving the viewing direction. */
    public void FocusSelection()
    {
        if (!Camera.PivotFree) return;
        if (SelectedCollisionPlacement is not null)
        {
            var (min, max) = CollisionSelectionBounds();
            Camera.Frame(min, max, lastAspect);
            return;
        }
        if (SelectedObject is null) { Frame(); return; }
        var bounds = SelectedBounds;
        Camera.Frame(bounds.Minimum, bounds.Maximum, lastAspect);
    }

    /** The orbit center moves to the object while the camera eye stays in place. */
    public void SetCursorToSelection()
    {
        if (!Camera.PivotFree) return;
        if (SelectedCollisionPlacement is not null)
        {
            var (min, max) = CollisionSelectionBounds();
            Camera.EnterEditor((min + max) * .5f);
        }
        else if (SelectedObject is not null) Camera.EnterEditor(SelectedBounds.Center);
    }

    /** Ordinary geometry controls framing; authored bounds are used only for scenes without meshes. */
    public void Frame()
    {
        Camera.ResetReach();
        if (!Camera.PivotFree) return;
        if (Scene is not null)
        {
            var bounds = FramingBounds();
            if (!bounds.IsFinite) bounds = new(-Vector3.One, Vector3.One);
            Camera.Frame(bounds.Minimum, bounds.Maximum, lastAspect);
        }
        imageZoom = 1;
        imageOffset = Vector2.Zero;
    }

    /** Bubble identities may be either package tags or canonical name hashes. */
    public bool FrameBubble(uint bubbleHash)
    {
        var bounds = FramingBounds(Backdrop.CanonicalBubble(bubbleHash));
        if (!bounds.IsFinite) return false;
        Camera.Frame(bounds.Minimum, bounds.Maximum, lastAspect);
        return true;
    }

    /** Spawn focus uses a local area so distant world bounds cannot pull the camera outside the map. */
    public void FrameArea(Vector3 center, float radius = 20)
    {
        if (!float.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
        Camera.Frame(center - new Vector3(radius), center + new Vector3(radius), lastAspect);
    }

    /** Markers and volume meshes cannot widen the bounds of ordinary map geometry. */
    public SelectionBounds FramingBounds(uint? canonicalBubble = null)
    {
        var bounds = SelectionBounds.Empty;
        foreach (var item in selection.Objects.OfType<MeshInstance>())
        {
            if (item.Layer is not (SceneLayer.Geometry or SceneLayer.Terrain or SceneLayer.Objects) ||
                Backdrop.IsBackdrop(item) || !Matches(item, item.BubbleHash)) continue;
            Include(item);
        }
        if (bounds.IsFinite) return bounds;
        foreach (var item in selection.Objects)
        {
            uint bubble = item switch { MeshInstance mesh => mesh.BubbleHash,
                SceneMarker marker => marker.BubbleHash, ScenePointProxy point => point.BubbleHash, _ => 0 };
            if (Backdrop.IsBackdrop(item) || !Matches(item, bubble)) continue;
            Include(item);
        }
        return bounds;
        bool Matches(object item, uint bubble) => Visibility.IsVisible(item) &&
            (canonicalBubble is null || Backdrop.CanonicalBubble(bubble) == canonicalBubble);
        void Include(object item)
        {
            var next = selection.GetBounds(item);
            if (next.IsFinite) bounds = bounds.Include(next.Minimum).Include(next.Maximum);
        }
    }

    /** Draw inside an ImGui window after NewFrame and before the host renders ImGui. */
    public void DrawImGui(Vector2 size)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (size.X < 1 || size.Y < 1) return;
        var start = ImGui.GetCursorPosY();
        Animation?.Tick(ImGui.GetIO().DeltaTime);
        DrawToolbar();
        Animation?.DrawImGui();
        size.Y = MathF.Max(1, size.Y - (ImGui.GetCursorPosY() - start));
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##asset-viewport", size,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight |
            ImGuiButtonFlags.MouseButtonMiddle);
        bool hovered = ImGui.IsItemHovered();
        // A handle drag owns the mouse, so it neither selects nor moves the camera.
        if (!ProcessGizmo(hovered, origin, size)) ProcessInput(hovered, origin, size);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(new Vector4(.055f, .065f, .08f, 1)));
        draw.PushClipRect(origin, origin + size, true);
        if (texture is not null)
        {
            float fit = MathF.Min(size.X / texture.Width, size.Y / texture.Height) * imageZoom;
            var imageSize = new Vector2(texture.Width, texture.Height) * fit;
            var imageStart = origin + (size - imageSize) / 2 + imageOffset;
            draw.AddImage(texture.ImGuiId, imageStart, imageStart + imageSize);
        }
        else
        {
            simulationSeconds += ImGui.GetIO().DeltaTime;
            var image = RenderImage(Math.Max(1, (int)size.X), Math.Max(1, (int)size.Y), simulationSeconds);
            draw.AddImage(image.ImGuiId, origin, origin + size);
            Animation?.DrawImGuiBones(draw, origin, size, Camera.ViewProjection(size.X / size.Y));
            DrawObjectNames(draw, origin, size, hovered);
            DrawGizmo(draw, origin, size, hovered);
        }
        draw.PopClipRect();
    }

    /** Toolbar controls affect this viewport alone. */
    private void DrawToolbar()
    {
        var style = ImGui.GetStyle();
        float available = ImGui.GetContentRegionAvail().X;
        float right = ImGui.GetCursorScreenPos().X + available;
        float button = ImGui.GetFrameHeight();
        var tips = ToolbarTips();
        // A followed camera is placed by the live link each frame; controls that move it are off.
        bool locked = textureData is null && !Camera.PivotFree;
        ImGui.BeginDisabled(locked);
        if (ImGuiIcons.Button("frame-all", StudioIcon.Frame, textureData is null ? tips[0] : tips[1])) Frame();
        ImGui.EndDisabled();
        if (textureData is { } data)
        {
            Slider("Mip");
            bool changed = ImGui.SliderInt("Mip", ref mip, 0, data.MipCount - 1);
            if (data.ArraySize > 1)
            {
                Slider("Face");
                changed |= ImGui.SliderInt("Face", ref slice, 0, data.ArraySize - 1);
            }
            var resource = data.Subresources.First(s => s.MipLevel == mip && s.ArraySlice == slice);
            depthSlice = Math.Clamp(depthSlice, 0, resource.Depth - 1);
            if (resource.Depth > 1)
            {
                Slider("Slice");
                changed |= ImGui.SliderInt("Slice", ref depthSlice, 0, resource.Depth - 1);
            }
            if (changed)
            {
                var next = renderer.CreateTexture(resource.Width, resource.Height, (Format)data.Format,
                    resource.Data.AsSpan(depthSlice * resource.SlicePitch, resource.SlicePitch), resource.RowPitch);
                texture?.Dispose();
                texture = next;
            }
            string dimensions = $"{resource.Width} x {resource.Height}";
            Next(ImGui.CalcTextSize(dimensions).X);
            ImGui.TextDisabled(dimensions);
        }
        else
        {
            if (ShowTransformTools)
            {
                string unavailable = GizmoUnavailableReason.Length > 0 ? "\n" + GizmoUnavailableReason : "";
                foreach (var (tool, icon, tip) in new[] { (TransformTool.Select, StudioIcon.Select, "Select"),
                    (TransformTool.Move, StudioIcon.Move, "Move along an axis" + unavailable),
                    (TransformTool.Rotate, StudioIcon.Rotate, "Rotate around an axis" + unavailable),
                    (TransformTool.Scale, StudioIcon.Scale, "Scale; the game stores one uniform scale" + unavailable) })
                {
                    Next(button);
                    if (ImGuiIcons.Button("tool-" + tool, icon, tip, Tool == tool)) Tool = tool;
                }
            }
            ImGui.BeginDisabled(SelectedObject is null && SelectedCollision < 0 || locked);
            Next(button);
            if (ImGuiIcons.Button("focus", StudioIcon.Focus, tips[2])) FocusSelection();
            Next(button);
            if (ImGuiIcons.Button("set-cursor", StudioIcon.Pin, tips[3])) SetCursorToSelection();
            ImGui.EndDisabled();
            Next(button);
            ImGuiIcons.Toggle("grid", StudioIcon.Grid, "Grid", ref grid);
            if (ShowCursorToggle)
            {
                Next(button);
                bool cursor = ShowCursor;
                if (ImGuiIcons.Toggle("cursor", StudioIcon.Cursor, tips[4], ref cursor)) ShowCursor = cursor;
            }
            Next(button);
            ImGuiIcons.Toggle("wireframe", StudioIcon.Wireframe, "Wireframe", ref wireframe);
            Next(button);
            bool orthographic = Camera.Orthographic;
            ImGui.BeginDisabled(Camera.HasExternalPose);
            if (ImGuiIcons.Toggle("orthographic", StudioIcon.Ortho, "Orthographic view", ref orthographic))
            {
                SetPlayerCamera(false);
                Camera.Orthographic = orthographic;
            }
            ImGui.EndDisabled();
            if (Scene is not null) DrawPlayerControls(Next);
            if (ShowGameShaderToggle)
            {
                Next(button);
                ImGuiIcons.Toggle("game-shaders", StudioIcon.Shader, "Game shaders", ref useGameShaders);
            }
            if (SelectionMode == SceneSelectionMode.MeshPart && Visibility.AvailableDetailLevels.Count > 0)
            {
                Next(110);
                ImGui.SetNextItemWidth(110);
                if (ImGui.BeginCombo("##asset-lod", $"LOD {Visibility.DetailLevel}"))
                {
                    foreach (int level in Visibility.AvailableDetailLevels)
                        if (ImGui.Selectable($"LOD {level}", Visibility.DetailLevel == level))
                        {
                            Visibility.DetailLevel = level;
                            if (SelectedObject is MeshInstance part && !Visibility.IsVisible(part)) SetSelection(null);
                        }
                    ImGui.EndCombo();
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Native level of detail");
            }
            // The world scene draws its own layer menu; asset views select parts.
            if (SelectionMode == SceneSelectionMode.MeshPart && Scene is { } scene)
            {
                if (!ReferenceEquals(layerScene, scene))
                {
                    layerScene = scene;
                    var layers = new HashSet<SceneLayer>(SceneLayerMenu.Present(scene));
                    if (CollisionPossible(scene)) layers.Add(SceneLayer.Collision);
                    presentLayers = layers;
                }
                if (presentLayers.Count > 1 || presentLayers.Contains(SceneLayer.Effects) || presentLayers.Contains(SceneLayer.Collision))
                {
                    Next(100);
                    ImGui.SetNextItemWidth(100);
                    if (ImGui.BeginCombo("##asset-layers", "Layers"))
                    {
                        SceneLayerMenu.Draw(Visibility, presentLayers, this);
                        ImGui.EndCombo();
                    }
                }
            }
        }
        void Next(float width)
        {
            if (ImGui.GetItemRectMax().X + style.ItemSpacing.X + width <= right) ImGui.SameLine();
        }
        void Slider(string label)
        {
            float text = ImGui.CalcTextSize(label).X + style.ItemInnerSpacing.X;
            float width = MathF.Min(100, MathF.Max(1, available - text));
            Next(width + text);
            ImGui.SetNextItemWidth(width);
        }
    }

    /** Tooltips name the current bindings; they are rebuilt only after a binding changes. */
    private static string[] ToolbarTips()
    {
        var map = ShortcutInput.Map;
        if (ReferenceEquals(tipMap, map) && tipRevision == map.Revision) return toolbarTips;
        tipMap = map;
        tipRevision = map.Revision;
        string fit = string.Join(" or ", new[] { map.Text(ShortcutActions.FrameSelection2D), map.Text(ShortcutActions.FrameAll2D) }
            .Where(text => text.Length > 0));
        toolbarTips =
        [
            "Frame all" + Line(ShortcutActions.Orbit3D, "orbit") + Line(ShortcutActions.Pan3D, "pan")
                + Line(ShortcutActions.Zoom3D, "zoom") + Line(ShortcutActions.FrameSelection3D, "frame selection")
                + Line(ShortcutActions.FrameAll3D, "frame all"),
            "Fit image" + Line(ShortcutActions.Pan2D, "pan") + Line(ShortcutActions.Zoom2D, "zoom")
                + (fit.Length > 0 ? $"\n{fit}: fit" : ""),
            Named("Frame selection", ShortcutActions.FrameSelection3D),
            Named("Set cursor to selection", ShortcutActions.SetCursor3D),
            Named("3D cursor", ShortcutActions.ToggleCursor3D)
        ];
        return toolbarTips;
        string Line(string id, string label) => map.Text(id) is { Length: > 0 } text ? $"\n{text}: {label}" : "";
        string Named(string label, string id) => map.Text(id) is { Length: > 0 } text ? $"{label} ({text})" : label;
    }

    /** Native failures preserve the decoded geometry preview and expose the reason to the host. */
    public GpuTexture RenderImage(int width, int height, double timeSeconds = 0)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (texture is not null) return texture;
        long stamp = MeasureFrameTimes ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        LastFrameTimings = default;
        PollFallbackTextures();
        PollCollision();
        FallbackTexturedPartCount = FallbackGreyPartCount = 0;
        lastAspect = (float)width / height;
        UpdateVisibility(width, height);
        UpdateEffects(width, height, timeSeconds);
        if (MeasureFrameTimes)
        {
            LastFrameTimings = LastFrameTimings with { VisibilityMilliseconds = Elapsed(stamp) };
            stamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        // Wireframe draws every part as plain edges, so it replaces game shading while it is on.
        if (useGameShaders && !wireframe && nativeScene is not null && NativePartCount > 0)
        {
            try
            {
                nativeScene.Resize(width, height);
                nativeScene.TimeOfDay = Math.Clamp(TimeOfDay, 0, 1);
                nativeScene.Render(Camera, timeSeconds, nativeHandles, new Vector4(.0015f, .0025f, .005f, 1));
                if (MeasureFrameTimes)
                {
                    LastFrameTimings = LastFrameTimings with { NativeMilliseconds = Elapsed(stamp) };
                    stamp = System.Diagnostics.Stopwatch.GetTimestamp();
                }
                NativeError = nativePoseError ?? nativeScene.LightingError;
                var image = nativeScene.DisplayTexture();
                if (MeasureFrameTimes)
                {
                    LastFrameTimings = LastFrameTimings with { DisplayMilliseconds = Elapsed(stamp) };
                    stamp = System.Diagnostics.Stopwatch.GetTimestamp();
                }
                var depth = nativeScene.SceneDepth;
                NativeFallbackPartCount = nativeFallback.Count;
                if (nativeFallback.Count > 0 && depth is not null)
                {
                    outline ??= new SelectionOutlinePass(renderer);
                    (image, depth) = outline.CopyScene(image, depth);
                    DrawGeometryBatches(nativeFallback, Camera.ViewProjection((float)width / height), true);
                }
                if (MeasureFrameTimes)
                {
                    LastFrameTimings = LastFrameTimings with { FallbackMilliseconds = Elapsed(stamp) };
                    stamp = System.Diagnostics.Stopwatch.GetTimestamp();
                }
                var result = DrawSelectionAndOverlays(image, depth, width, height);
                if (MeasureFrameTimes) LastFrameTimings = LastFrameTimings with { OverlayMilliseconds = Elapsed(stamp) };
                return result;
            }
            catch (Exception error) when (NativeScenePlan.Recoverable(error)
                || error is InvalidOperationException or SharpGen.Runtime.SharpGenException)
            {
                NativeError = error.Message;
                useGameShaders = false;
            }
            finally { renderer.RestoreMainTarget(); }
        }
        NativeFallbackPartCount = 0;
        RenderScene(width, height);
        try { return DrawSelectionAndOverlays(target!.Textures[0], target.DepthShaderView, width, height); }
        finally { renderer.RestoreMainTarget(); }
    }

    private static double Elapsed(long stamp) => System.Diagnostics.Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;

    /**
     * Drag ownership starts over the image and survives movement outside its bounds.
     * A texture uses the 2D shortcuts; a scene uses the 3D shortcuts.
     */
    private void ProcessInput(bool hovered, Vector2 origin, Vector2 size)
    {
        if (!ShortcutInput.Enabled) { dragButton = -1; selecting = false; return; }
        var io = ImGui.GetIO();
        bool image = textureData is not null;
        var view = image ? ShortcutSection.View2D : ShortcutSection.View3D;
        if (hovered)
        {
            hoverFrame = ShortcutInput.Frame;
            ShortcutInput.Hover(view);
            foreach (var button in ShortcutInput.Buttons)
            {
                if (!ImGui.IsMouseClicked((ImGuiMouseButton)button)) continue;
                var actions = ShortcutInput.Mouse(view, button);
                if (actions.FirstOrDefault(IsDragAction) is { } drag) { dragButton = (int)button; dragAction = drag; }
                if (actions.Contains(ShortcutActions.Select3D))
                {
                    selecting = true;
                    selectButton = button;
                    pressPosition = io.MousePos;
                    doubleClickSelecting = ImGui.IsMouseDoubleClicked((ImGuiMouseButton)button);
                }
            }
            if (image)
            {
                if (!Camera.HasExternalPose && (ShortcutInput.Pressed(ShortcutActions.FrameSelection2D)
                    || ShortcutInput.Pressed(ShortcutActions.FrameAll2D))) Frame();
                imageZoom = Math.Clamp(imageZoom * MathF.Pow(1.15f, ShortcutInput.Wheel(view, ShortcutActions.Zoom2D)), .05f, 40);
            }
            else
            {
                if (!Camera.HasExternalPose)
                {
                    float wheel = ShortcutInput.Wheel(view, ShortcutActions.Zoom3D);
                    if (PlayerCamera) ScalePlayerSpeed(wheel);
                    else Camera.Zoom(wheel);
                }
                if (Camera.PivotFree)
                {
                    if (ShortcutInput.Pressed(ShortcutActions.FrameSelection3D)) FocusSelection();
                    // A movement key may also be bound to frame all, as A is in the Maya preset.
                    if (!PlayerCamera && ShortcutInput.Pressed(ShortcutActions.FrameAll3D)) Frame();
                    if (ShortcutInput.Pressed(ShortcutActions.SetCursor3D)) SetCursorToSelection();
                }
                if (ShowCursorToggle && ShortcutInput.Pressed(ShortcutActions.ToggleCursor3D)) ShowCursor = !ShowCursor;
                if (ShortcutInput.Pressed(ShortcutActions.TogglePlayer3D)) SetPlayerCamera(!Camera.Player);
            }
        }
        if (!image && PlayerCamera) MovePlayer(hovered || dragButton >= 0);
        if (selecting && ImGui.IsMouseReleased((ImGuiMouseButton)selectButton))
        {
            selecting = false;
            if (hovered && Vector2.Distance(pressPosition, io.MousePos) < 4 && !image)
            {
                SelectAt(io.MousePos - origin, size, doubleClickSelecting);
            }
        }
        if (dragButton < 0) return;
        if (Camera.HasExternalPose) { dragButton = -1; return; }
        if (!ImGui.IsMouseDown((ImGuiMouseButton)dragButton)) { dragButton = -1; return; }
        // Modifiers pressed during a drag can switch it, as Shift turns an orbit into a pan.
        string action = ShortcutInput.Mouse(view, (ShortcutButton)dragButton).FirstOrDefault(IsDragAction) ?? dragAction;
        if (image)
        {
            if (action == ShortcutActions.Pan2D) imageOffset += io.MouseDelta;
            else if (action == ShortcutActions.Zoom2D)
                imageZoom = Math.Clamp(imageZoom * MathF.Pow(1.15f, ShortcutInput.DragZoom(io.MouseDelta)), .05f, 40);
        }
        // The player camera turns in place on orbit and pan drags, so Shift for sprint keeps the look.
        else if (PlayerCamera)
        {
            if (action is ShortcutActions.Orbit3D or ShortcutActions.Pan3D) Camera.Look(io.MouseDelta.X, io.MouseDelta.Y);
        }
        else if (action == ShortcutActions.Pan3D) { if (Camera.PivotFree) Camera.Pan(io.MouseDelta.X, io.MouseDelta.Y, (int)size.Y); }
        else if (action == ShortcutActions.Orbit3D) Camera.Orbit(io.MouseDelta.X, io.MouseDelta.Y);
        else if (action == ShortcutActions.Zoom3D) Camera.Zoom(ShortcutInput.DragZoom(io.MouseDelta));
    }

    private static bool IsDragAction(string id) => id is ShortcutActions.Orbit3D or ShortcutActions.Pan3D
        or ShortcutActions.Zoom3D or ShortcutActions.Pan2D or ShortcutActions.Zoom2D;

    /** The color image is unbound before ImGui samples it. */
    private void RenderScene(int width, int height)
    {
        if (!visibleGeometryCollected)
        {
            foreach (int index in visibleItems)
                if (itemKinds[index] <= RenderItemKind.UnsupportedSky)
                    visibleMeshes.Add((MeshInstance)selection.Objects[index]);
            visibleGeometryCollected = true;
        }
        if (target is null || target.Width != width || target.Height != height)
        {
            var next = renderer.CreateNativeTargets(width, height, [Format.R8G8B8A8_UNorm], Format.D32_Float, true);
            target?.Dispose();
            target = next;
        }
        renderer.BeginOffscreen(target, new Vector4(.055f, .065f, .08f, 1));
        try
        {
            var view = Camera.ViewProjection((float)width / height);
            // With game shaders on, every part here stands in for a native draw that is not available.
            DrawGeometryBatches(visibleMeshes, view, useGameShaders);
            DrawEffectsPlain(target, view);
        }
        finally { renderer.RestoreMainTarget(); }
    }

    /**
     * Native fallbacks share the copied scene depth; volume meshes remain wireframe.
     * @param textured Draws solid parts with their material's colour texture once it has loaded.
     */
    private void DrawGeometryBatches(IEnumerable<MeshInstance> instances, Matrix4x4 view, bool textured = false)
    {
        foreach (var batch in geometryBatches.Values) batch.Clear();
        foreach (var instance in instances)
        {
            bool wire = wireframe || instance.Layer == SceneLayer.Volumes;
            if (livePoseMeshes.TryGetValue(instance, out var pose))
            {
                renderer.DrawMesh(pose.Active, Matrix4x4.Identity, view, new(.73f, .78f, .84f, 1),
                    wireframe: wire, skybox: instance.Layer == SceneLayer.Skybox);
                continue;
            }
            var texture = textured && !wire ? FallbackTextureFor(instance) : null;
            if (textured && !wire)
            {
                if (texture is null) FallbackGreyPartCount++;
                else FallbackTexturedPartCount++;
            }
            var key = (instance.Mesh, instance.Layer == SceneLayer.Skybox, wire, texture);
            if (!geometryBatches.TryGetValue(key, out var batch)) geometryBatches.Add(key, batch = []);
            batch.Add(instance.RenderTransform);
        }
        foreach (var (key, transforms) in geometryBatches)
            if (transforms.Count > 0 && meshes.TryGetValue(key.Mesh, out var mesh))
                renderer.DrawMeshInstanced(mesh, CollectionsMarshal.AsSpan(transforms), view,
                    key.Texture is null ? new Vector4(.73f, .78f, .84f, 1) : Vector4.One, key.Texture?.Texture,
                    key.Wire, key.Sky, key.Texture?.Cutout ?? false);
    }

    /** Shared bounds drive both native and diagnostic visibility. */
    private void UpdateVisibility(int width, int height)
    {
        long phase = MeasureFrameTimes ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        long allocated = MeasureFrameTimes ? GC.GetAllocatedBytesForCurrentThread() : 0;
        // An orthographic view has no distance, and a chosen detail level shows that level.
        Visibility.GameLod = !FullDetail && !Camera.Orthographic && Visibility.DetailLevel == 0;
        Visibility.Prepare(selection.Objects, selection.AuthoredBoundsRevision);
        var lodView = StaticLod.View(Camera.Position, Camera.Projection((float)width / height).M11);
        if (nativeScene is not null) nativeScene.CasterLod = Visibility.GameLod ? lodView : null;
        var context = Visibility.ContextFilter;
        // The preview context passes every authored non-backdrop mesh, and so every anchor.
        bool previewContext = context is null || ReferenceEquals(context, previewContextFilter);
        Backdrop.Update(Camera.Pivot, Visibility, previewContext);
        if (AutoAtmosphere && nativeScene is not null && Backdrop.Mode != SceneBackdropMode.All &&
            atmosphereBubble != Backdrop.ActiveBubbleHash)
        {
            atmosphereBubble = Backdrop.ActiveBubbleHash;
            nativeScene.AtmosphereIndex = -1;
            for (int index = 0; index < nativeScene.Plan.Atmospheres.Count; index++)
                if (Backdrop.CanonicalBubble(nativeScene.Plan.Atmospheres[index].BubbleHash) == atmosphereBubble)
                { nativeScene.AtmosphereIndex = index; break; }
        }
        if (MeasureFrameTimes)
        {
            LastFrameTimings = LastFrameTimings with { BackdropMilliseconds = Elapsed(phase) };
            phase = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        EnsureSceneGrid();
        if (renderIndexDirty || nativeHandleOffsets.Length != selection.Objects.Count + 1) BuildRenderIndex();
        var planes = SceneGrid.FrustumPlanes(Camera.ViewProjection((float)width / height));
        float pixelsPerUnit = Camera.Orthographic ? height / Camera.ViewHeight
            : height / (2 * MathF.Tan(Camera.VerticalFieldOfView((float)width / height) / 2));
        // Live, runtime and posed items move without a revision, so only a fully cached frame is reused.
        bool reusable = CacheSpatialCandidates && previewContext && Visibility.FiltersCached && liveIndices.Count == 0
            && runtimeIndices.Count == 0 && activeRuntimeIndices.Count == 0 && livePoseIndices.Count == 0;
        if (reusable && cullReusable && SameCull(planes, pixelsPerUnit))
        {
            if (MeasureFrameTimes) LastFrameTimings = LastFrameTimings with
            {
                CullMilliseconds = Elapsed(phase),
                VisibilityAllocationBytes = GC.GetAllocatedBytesForCurrentThread() - allocated
            };
            return;
        }
        visibleItems.Clear();
        visibleMeshes.Clear(); visibleVolumes.Clear(); visibleMarkers.Clear(); visiblePoints.Clear();
        visibleGeometryCollected = false;
        nativeHandles.Clear();
        nativeFallback.Clear();
        renderVisibility ??= index => (liveIndexSet.Count == 0 || !liveIndexSet.Contains(index))
            && (runtimeIndices.Count == 0 || !runtimeIndices.Contains(index))
            && (livePoseIndices.Count == 0 || !livePoseIndices.Contains(index)) && RenderItemVisible(index);
        // The reusable cull leaves out the decorator items CollectVisible would drop; it needs this frame's levels.
        bool decoratorLodReady = reusable && anyItemDecorators && DecoratorLodInputs().Native;
        if (decoratorLodReady) nativeScene!.PrepareDecoratorLod(Camera.Position);
        if (reusable)
        {
            sceneGrid!.QueryShown(planes, ShownCells(), visibleItems, decoratorLodReady ? nativeScene!.DecoratorCellLevels : null);
            SceneCullQueryCount++;
        }
        else if (CacheSpatialCandidates)
        {
            int previousCullQueries = sceneGrid!.CandidateBuildCount;
            sceneGrid.QueryCached(planes, Camera.Position, pixelsPerUnit, Camera.Orthographic, 0,
                renderVisibility, visibleItems);
            SceneCullQueryCount += sceneGrid.CandidateBuildCount - previousCullQueries;
        }
        else
        {
            sceneGrid!.Query(planes, Camera.Position, pixelsPerUnit, Camera.Orthographic, 0,
                renderVisibility, visibleItems);
            SceneCullQueryCount++;
        }
        QueryLive(planes, RenderItemVisible, visibleItems);
        QueryRuntime(planes, RenderItemVisible, visibleItems);
        QueryLivePose(planes, RenderItemVisible, visibleItems);
        if (MeasureFrameTimes)
        {
            LastFrameTimings = LastFrameTimings with { CullMilliseconds = Elapsed(phase) };
            phase = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        CollectVisible(Visibility.GameLod ? lodView : null, decoratorLodReady);
        visibleMarkers.Sort(static (a, b) => a.Index.CompareTo(b.Index));
        cullReusable = reusable;
        if (reusable)
        {
            planes.CopyTo(culledPlanes, 0);
            culledEye = Camera.Position; culledPixelsPerUnit = pixelsPerUnit; culledOrthographic = Camera.Orthographic;
            culledRevision = Visibility.Revision; culledBubble = Backdrop.ActiveBubbleHash; culledMode = Backdrop.Mode;
            culledGrid = sceneGrid; culledOffsets = nativeHandleOffsets; culledHandles = nativeHandleMap;
            culledKinds = itemKinds; culledBackdrop = backdropItems; culledDecorators = DecoratorLodInputs();
        }
        if (MeasureFrameTimes) LastFrameTimings = LastFrameTimings with
        {
            VisibleListMilliseconds = Elapsed(phase),
            VisibilityAllocationBytes = GC.GetAllocatedBytesForCurrentThread() - allocated
        };
    }

    /**
     * The grid's items RenderItemVisible shows in a reusable frame; rebuilt when the hide state, the
     * backdrop bubble, its mode, the grid or the render index changes.
     */
    private SceneGrid.ShownCells ShownCells()
    {
        (long Revision, uint Bubble, SceneBackdropMode Mode) key = (Visibility.Revision, Backdrop.ActiveBubbleHash, Backdrop.Mode);
        bool sameIndex = shownCells is not null && ReferenceEquals(shownGrid, sceneGrid)
            && ReferenceEquals(shownKinds, itemKinds) && ReferenceEquals(shownBackdrop, backdropItems);
        if (sameIndex && key == shownKey) return shownCells!;
        // A reusable frame has no live, runtime or posed items, so RenderItemVisible only reads.
        if (sameIndex && key.Revision == shownKey.Revision)
        {
            // Only the bubble or its mode moved, so only backdrop items can change their answer.
            bool wasAll = shownKey.Mode == SceneBackdropMode.All || shownKey.Bubble == 0;
            bool all = key.Mode == SceneBackdropMode.All || key.Bubble == 0;
            uint previous = shownKey.Bubble;
            IEnumerable<int> changed = wasAll != all ? backdropIndices
                : wasAll ? [] : backdropIndices.Where(i => itemBubbles[i] == previous || itemBubbles[i] == key.Bubble);
            shownCells = sceneGrid!.PrepareShown(RenderItemVisible, shownCells!, changed);
        }
        else shownCells = sceneGrid!.PrepareShown(RenderItemVisible, anyItemDecorators ? itemDecorators : null);
        shownKey = key; shownGrid = sceneGrid; shownKinds = itemKinds; shownBackdrop = backdropItems;
        ShownCellBuildCount++;
        return shownCells;
    }

    /** True when every input of the last cull is unchanged; the visible lists then still hold. */
    private bool SameCull(Vector4[] planes, float pixelsPerUnit) =>
        planes.AsSpan().SequenceEqual(culledPlanes) && culledEye == Camera.Position && culledPixelsPerUnit == pixelsPerUnit
        && culledOrthographic == Camera.Orthographic && culledRevision == Visibility.Revision
        && culledBubble == Backdrop.ActiveBubbleHash && culledMode == Backdrop.Mode && ReferenceEquals(culledGrid, sceneGrid)
        && ReferenceEquals(culledOffsets, nativeHandleOffsets) && ReferenceEquals(culledHandles, nativeHandleMap)
        && ReferenceEquals(culledKinds, itemKinds) && ReferenceEquals(culledBackdrop, backdropItems)
        && culledDecorators == DecoratorLodInputs();

    /** The inputs of the decorator test in CollectVisible; it runs only while game shading draws the frame. */
    private (bool Native, int Detail, int Centers) DecoratorLodInputs() =>
        useGameShaders && !wireframe && nativeScene is { } scene && NativePartCount > 0
            ? (true, scene.DecoratorLodInputs.Detail, scene.DecoratorLodInputs.Centers) : default;

    /**
     * The caster filter's revision: it changes with hide state, and with the backdrop bubble only
     * when the old or the new bubble holds a backdrop caster.
     * @return Null when a live predicate or a runtime slot can change casters without a revision.
     */
    private long? ShadowCasterRevision()
    {
        var context = Visibility.ContextFilter;
        if (!Visibility.FiltersCached || runtimeObjects.Count != 0 || renderIndexDirty
            || context is not null && !ReferenceEquals(context, previewContextFilter)) return null;
        // A backdrop caster shows when every bubble shows or when its own bubble is active.
        bool all = Backdrop.Mode == SceneBackdropMode.All || Backdrop.ActiveBubbleHash == 0;
        uint bubble = !all && casterBubbles.Contains(Backdrop.ActiveBubbleHash) ? Backdrop.ActiveBubbleHash : 0;
        var inputs = (Visibility.Revision, bubble, all, (object?)casterBubbles);
        if (inputs != casterInputs) { casterInputs = inputs; casterRevision++; }
        return casterRevision;
    }

    /** Dense handle ranges preserve draw order without hashing every visible placement each frame. */
    private void BuildRenderIndex()
    {
        var objects = selection.Objects;
        nativeHandleOffsets = new int[objects.Count + 1];
        itemKinds = new RenderItemKind[objects.Count];
        markerLineStarts = new int[objects.Count];
        var preparedLines = new List<LineVertex>((Scene?.Markers.Count ?? 0) * 24);
        backdropItems = new bool[objects.Count];
        itemBubbles = new uint[objects.Count];
        itemLod = new StaticLodInstance[objects.Count];
        casterBubbles = [];
        itemDecorators = new int[objects.Count];
        Array.Fill(itemDecorators, -1);
        anyItemDecorators = false;
        var backdropList = new List<int>();
        var flattened = new List<int>(nativeScene?.ReadyPartCount ?? 0);
        for (int index = 0; index < objects.Count; index++)
        {
            var item = objects[index];
            nativeHandleOffsets[index] = flattened.Count;
            backdropItems[index] = Backdrop.IsBackdrop(item);
            if (item is not MeshInstance instance)
            {
                itemKinds[index] = item is SceneMarker ? RenderItemKind.Marker : RenderItemKind.Point;
                if (item is SceneMarker marker)
                {
                    markerLineStarts[index] = preparedLines.Count;
                    AppendMarkerLines(marker, preparedLines);
                }
                continue;
            }
            itemBubbles[index] = instance.BubbleHash == 0 ? 0 : Backdrop.CanonicalBubble(instance.BubbleHash);
            if (backdropItems[index] && itemBubbles[index] != 0)
            {
                backdropList.Add(index);
                if (instance.Mesh.Native?.RenderStage == NativeScenePlan.ShadowStage) casterBubbles.Add(itemBubbles[index]);
            }
            itemLod[index] = StaticLod.Place(instance);
            if (nativeObjectHandles.TryGetValue(instance, out var handles))
            {
                flattened.AddRange(handles);
                if (instance.Layer != SceneLayer.Volumes && SharedDecorator(handles) is >= 0 and var decorator)
                { itemDecorators[index] = decorator; anyItemDecorators = true; }
            }
            itemKinds[index] = instance.Layer == SceneLayer.Volumes ? RenderItemKind.Volume
                : nativeHandleOffsets[index] != flattened.Count ? RenderItemKind.NativeMesh
                : instance.Layer == SceneLayer.Skybox ? RenderItemKind.UnsupportedSky : RenderItemKind.FallbackMesh;
        }
        nativeHandleOffsets[objects.Count] = flattened.Count;
        nativeHandleMap = flattened.ToArray();
        backdropIndices = backdropList.ToArray();
        markerLines = preparedLines.ToArray();
        renderIndexDirty = false;
    }

    /** The packed decorator test handles all share, as itemDecorators holds it; -1 when they do not share one. */
    private int SharedDecorator(List<int> handles)
    {
        if (nativeScene is null || handles.Count == 0) return -1;
        var first = nativeScene.HandleDecorator(handles[0]);
        // The cell must fit above the five level bits.
        if (first.Cell < 0 || first.Cell >= 1 << 26) return -1;
        foreach (int handle in handles) if (nativeScene.HandleDecorator(handle) != first) return -1;
        return first.Cell << 5 | first.Levels;
    }

    /**
     * Drops static parts outside their distance level and decorator parts that fail their LOD test,
     * then fills the native handles and the overlay lists from the kept items in order.
     * A long list runs in parallel chunks; a list with preview items runs as one chunk on this thread.
     * @param lod The view for the static level rule; null keeps every item.
     * @param decoratorLodReady PrepareDecoratorLod already ran for this camera.
     */
    private void CollectVisible(StaticLodView? lod, bool decoratorLodReady)
    {
        // Only live and runtime items carry a preview transform; only this thread may ask the renderer.
        bool previews = liveIndexSet.Count != 0 || runtimeIndices.Count != 0;
        int count = visibleItems.Count;
        int chunks = previews || count < CollectItemsPerTask * 2 ? 1 : (count + CollectItemsPerTask - 1) / CollectItemsPerTask;
        if (lodKeep.Length < count) lodKeep = new bool[Math.Max(count, lodKeep.Length * 2)];
        if (keptItems.Length < count) keptItems = new int[Math.Max(count, keptItems.Length * 2)];
        if (chunkTotals.Length < chunks * 2) chunkTotals = new int[chunks * 2];
        while (chunkOthers.Count < chunks) chunkOthers.Add([]);
        // Each item's handles appear once in the map, so the map length bounds the visible handles.
        CollectionsMarshal.SetCount(nativeHandles, nativeHandleMap.Length);
        var items = visibleItems; var lods = itemLod; var keep = lodKeep; var kinds = itemKinds;
        var offsets = nativeHandleOffsets; var map = nativeHandleMap; var totals = chunkTotals; var output = keptItems;
        var view = lod.GetValueOrDefault();
        var decorators = anyItemDecorators && DecoratorLodInputs().Native ? itemDecorators : null;
        if (decorators is not null && !decoratorLodReady) nativeScene!.PrepareDecoratorLod(Camera.Position);
        byte[] cellLevels = decorators is null ? [] : nativeScene!.DecoratorCellLevels;
        int Start(int chunk) => chunks == 1 ? chunk * count : Math.Min(count, chunk * CollectItemsPerTask);
        bool PreviewFallback(int index) => previews && ((MeshInstance)selection.Objects[index]).PreviewTransform is not null
            && nativeScene?.SupportsLiveTransform((MeshInstance)selection.Objects[index]) == false;
        // Totals hold each chunk's kept items and handles, then their first positions.
        void Count(int chunk)
        {
            int kept = 0, handles = 0;
            for (int i = Start(chunk), end = Start(chunk + 1); i < end; i++)
            {
                int index = items[i];
                ref readonly var placed = ref lods[index];
                keep[i] = lod is null || placed.Levels == 0
                    || StaticLod.Draws(placed.Levels, StaticLod.ColorLevel(StaticLod.Value(placed, view)));
                if (!keep[i]) continue;
                if (kinds[index] == RenderItemKind.NativeMesh && !PreviewFallback(index))
                {
                    if (decorators is not null && decorators[index] is >= 0 and var decorator
                        && (cellLevels[decorator >> 5] & decorator & 31) == 0) { keep[i] = false; continue; }
                    handles += offsets[index + 1] - offsets[index];
                }
                kept++;
            }
            totals[chunk * 2] = kept; totals[chunk * 2 + 1] = handles;
        }
        void Write(int chunk)
        {
            int next = totals[chunk * 2], handle = totals[chunk * 2 + 1];
            var handleSpan = CollectionsMarshal.AsSpan(nativeHandles);
            var others = chunkOthers[chunk];
            others.Clear();
            for (int i = Start(chunk), end = Start(chunk + 1); i < end; i++)
            {
                if (!keep[i]) continue;
                int index = items[i];
                output[next++] = index;
                if (kinds[index] != RenderItemKind.NativeMesh || PreviewFallback(index)) others.Add(index);
                else for (int k = offsets[index], stop = offsets[index + 1]; k < stop; k++) handleSpan[handle++] = map[k];
            }
        }
        if (chunks == 1) Count(0); else Parallel.For(0, chunks, Count);
        int keptTotal = 0, handleTotal = 0;
        for (int chunk = 0; chunk < chunks; chunk++)
        {
            int kept = totals[chunk * 2], handles = totals[chunk * 2 + 1];
            totals[chunk * 2] = keptTotal; totals[chunk * 2 + 1] = handleTotal;
            keptTotal += kept; handleTotal += handles;
        }
        if (chunks == 1) Write(0); else Parallel.For(0, chunks, Write);
        CollectionsMarshal.SetCount(nativeHandles, handleTotal);
        output.AsSpan(0, keptTotal).CopyTo(CollectionsMarshal.AsSpan(visibleItems));
        CollectionsMarshal.SetCount(visibleItems, keptTotal);
        for (int chunk = 0; chunk < chunks; chunk++)
            foreach (int index in chunkOthers[chunk])
                switch (kinds[index])
                {
                    case RenderItemKind.NativeMesh or RenderItemKind.FallbackMesh:
                        nativeFallback.Add((MeshInstance)selection.Objects[index]);
                        break;
                    case RenderItemKind.Volume:
                        visibleVolumes.Add((MeshInstance)selection.Objects[index]);
                        break;
                    case RenderItemKind.Marker:
                        visibleMarkers.Add((index, (SceneMarker)selection.Objects[index]));
                        break;
                    case RenderItemKind.Point:
                        visiblePoints.Add((ScenePointProxy)selection.Objects[index]);
                        break;
                }
    }

    // Items per parallel task of CollectVisible.
    private const int CollectItemsPerTask = 16384;
    private bool[] lodKeep = [];
    // CollectVisible scratch: kept items in order, two totals per chunk, each chunk's items without handles.
    private int[] keptItems = [];
    private int[] chunkTotals = [];
    private readonly List<List<int>> chunkOthers = [];
    // Each item's placed static level inputs; Levels zero for every other item.
    private StaticLodInstance[] itemLod = [];

    private bool RenderItemVisible(int index)
    {
        object item = selection.Objects[index];
        if (!Visibility.PreparedRenderVisible(index, item)) return false;
        var context = Visibility.ContextFilter;
        if (!ReferenceEquals(context, previewContextFilter)) return context?.Invoke(item) ?? true;
        return (Backdrop.Mode == SceneBackdropMode.All || Backdrop.ActiveBubbleHash == 0 ||
            !backdropItems[index] || itemBubbles[index] == 0 || itemBubbles[index] == Backdrop.ActiveBubbleHash)
            && (runtimeIndices.Count == 0 || !runtimeIndices.Contains(index) || activeRuntimeObjects.Contains(item));
    }

    /** Outlines are composited over either renderer without changing its material shading. */
    private GpuTexture DrawSelectionAndOverlays(GpuTexture source,
        Vortice.Direct3D11.ID3D11ShaderResourceView? depth, int width, int height)
    {
        LastOverlayTimings = default;
        OverlayGpuMark?.Invoke(0);
        long phase = MeasureFrameTimes ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        outline ??= new SelectionOutlinePass(renderer);
        outline.Begin(width, height, depth);
        var view = Camera.ViewProjection((float)width / height);
        foreach (var instance in selectedParts)
            if (Visibility.IsVisible(instance) && DiagnosticMesh(instance, out var mesh, out var transform))
                outline.Draw(mesh, transform * view, true);
        foreach (var selected in selectedItems)
        {
            // An item with mesh parts is outlined by its parts; any other item gets a box.
            bool parts = selectionMode == SceneSelectionMode.Object ? selection.MeshParts(selected).Count > 0
                : selected is MeshInstance;
            if (!parts && Visibility.IsVisible(selected))
            {
                markerBox ??= CreateMarkerBox();
                Matrix4x4 transform;
                if (selected is SceneMarker marker)
                    transform = Matrix4x4.CreateScale(Vector3.Max(Vector3.Abs(marker.RenderExtents), new Vector3(.05f))) *
                        Matrix4x4.CreateFromQuaternion(marker.RenderRotation) * Matrix4x4.CreateTranslation(marker.RenderPosition);
                else
                {
                    var bounds = selection.GetBounds(selected);
                    transform = Matrix4x4.CreateScale(Vector3.Max((bounds.Maximum - bounds.Minimum) * .5f, new Vector3(.05f))) *
                        Matrix4x4.CreateTranslation(bounds.Center);
                }
                outline.Draw(markerBox, transform * view, true);
            }
        }
        DrawCollisionOutline(view);
        RecordOverlayPhase(1, ref phase);
        var result = outline.Composite(source);
        RecordOverlayPhase(2, ref phase);
        DrawCollision(view, width, height);
        RecordOverlayPhase(3, ref phase);
        if (grid) DrawGrid(view);
        RecordOverlayPhase(4, ref phase);
        DrawVolumes(view);
        RecordOverlayPhase(5, ref phase);
        if (Scene is not null) DrawMarkers(view);
        DrawTransitions(view);
        RecordOverlayPhase(6, ref phase);
        DrawPointProxies(view);
        RecordOverlayPhase(7, ref phase);
        if (ShowCursor) DrawCursor(view, height);
        RecordOverlayPhase(8, ref phase);
        return result;
    }

    private GpuMesh CreateMarkerBox()
    {
        var vertices = Enumerable.Range(0, 8).Select(index => new RenderVertex(new Vector3(
            (index & 1) == 0 ? -1 : 1, (index & 2) == 0 ? -1 : 1, (index & 4) == 0 ? -1 : 1),
            Vector3.UnitZ, Vector2.Zero)).ToArray();
        return renderer.CreateMesh(vertices, [0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5,
            0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3]);
    }

    /** Point proxies remain pickable without pretending their metadata contains a mesh. */
    private void DrawPointProxies(Matrix4x4 view)
    {
        overlayLines.Clear();
        selectedOverlayLines.Clear();
        var color = new Vector4(.6f, .85f, 1, 1);
        foreach (var point in visiblePoints)
        {
            var lines = selectedMemberSet.Contains(point) ? selectedOverlayLines : overlayLines;
            var extent = Vector3.Max(Vector3.Abs(point.Extents), new Vector3(.15f));
            for (int axis = 0; axis < 3; axis++)
            {
                var offset = Vector3.Zero;
                offset[axis] = extent[axis];
                lines.Add(new(point.Position - offset, color));
                lines.Add(new(point.Position + offset, color));
            }
        }
        DrawOverlayLines(LineStream.Points, CollectionsMarshal.AsSpan(overlayLines), view, OccludeOverlays);
        DrawOverlayLines(LineStream.SelectedPoints, CollectionsMarshal.AsSpan(selectedOverlayLines), view, false);
    }

    /** The cursor's world axes and rings stay visible through scene depth. */
    private void DrawCursor(Matrix4x4 view, int height)
    {
        // A fixed pixel radius keeps the orbit center readable at every zoom level.
        const float radiusPixels = 12;
        const int ringSegments = 32;
        float radius = Camera.ViewHeight / height * radiusPixels;
        overlayLines.Clear();
        for (int axis = 0; axis < 3; axis++)
        {
            var direction = Vector3.Zero; direction[axis] = radius * 1.5f;
            var color = axis switch { 0 => new Vector4(1, .2f, .2f, 1),
                1 => new Vector4(.25f, 1, .35f, 1), _ => new Vector4(.3f, .6f, 1, 1) };
            overlayLines.Add(new(Camera.Pivot - direction, color));
            overlayLines.Add(new(Camera.Pivot + direction, color));
            for (int segment = 0; segment < ringSegments; segment++)
            {
                var tint = (segment / 4 & 1) == 0 ? new Vector4(1, 1, 1, 1) : color;
                overlayLines.Add(new(RingPoint(segment), tint));
                overlayLines.Add(new(RingPoint(segment + 1), tint));
            }
            Vector3 RingPoint(int segment)
            {
                float angle = MathF.Tau * segment / ringSegments;
                var offset = Vector3.Zero;
                offset[(axis + 1) % 3] = MathF.Cos(angle) * radius;
                offset[(axis + 2) % 3] = MathF.Sin(angle) * radius;
                return Camera.Pivot + offset;
            }
        }
        DrawOverlayLines(LineStream.Cursor, CollectionsMarshal.AsSpan(overlayLines), view, false);
    }

    private void DrawVolumes(Matrix4x4 view)
    {
        foreach (var instance in visibleVolumes)
            if (DiagnosticMesh(instance, out var mesh, out var transform))
                renderer.DrawMesh(mesh, transform, view, new(.73f, .78f, .84f, 1),
                    wireframe: true, skybox: true,
                    depthTest: OccludeOverlays && !selectedMemberSet.Contains(instance));
    }

    /** Grid spacing follows camera distance so both props and maps stay readable. */
    private void DrawGrid(Matrix4x4 view)
    {
        float step = MathF.Pow(10, MathF.Floor(MathF.Log10(MathF.Max(.01f, Camera.Distance / 8))));
        float x = MathF.Floor(Camera.Pivot.X / step) * step;
        float y = MathF.Floor(Camera.Pivot.Y / step) * step;
        float extent = step * 20;
        overlayLines.Clear();
        for (int i = -20; i <= 20; i++)
        {
            float px = x + i * step, py = y + i * step;
            var color = new Vector4(.19f, .22f, .27f, 1);
            overlayLines.Add(new(new(px, y - extent, 0), px == 0 ? new(.35f, .62f, .38f, 1) : color));
            overlayLines.Add(new(new(px, y + extent, 0), overlayLines[^1].Color));
            overlayLines.Add(new(new(x - extent, py, 0), py == 0 ? new(.7f, .3f, .27f, 1) : color));
            overlayLines.Add(new(new(x + extent, py, 0), overlayLines[^1].Color));
        }
        DrawOverlayLines(LineStream.Grid, CollectionsMarshal.AsSpan(overlayLines), view);
    }

    /** Unselected marker boxes respect scene depth without writing it. */
    private void DrawMarkers(Matrix4x4 view)
    {
        overlayLines.Clear();
        selectedOverlayLines.Clear();
        foreach (var (index, marker) in visibleMarkers)
        {
            var lines = selectedMemberSet.Contains(marker) ? selectedOverlayLines : overlayLines;
            int start = lines.Count;
            lines.EnsureCapacity(start + 24);
            CollectionsMarshal.SetCount(lines, start + 24);
            markerLines.AsSpan(markerLineStarts[index], 24).CopyTo(CollectionsMarshal.AsSpan(lines)[start..]);
        }
        DrawOverlayLines(LineStream.Markers, CollectionsMarshal.AsSpan(overlayLines), view, OccludeOverlays);
        DrawOverlayLines(LineStream.SelectedMarkers, CollectionsMarshal.AsSpan(selectedOverlayLines), view, false);
    }

    /** Appends the marker's box: 12 edges, 24 vertices. */
    private static void AppendMarkerLines(SceneMarker marker, List<LineVertex> lines)
    {
        var transform = Matrix4x4.CreateFromQuaternion(marker.RenderRotation);
        var extent = Vector3.Max(Vector3.Abs(marker.RenderExtents), new Vector3(.05f));
        for (int corner = 0; corner < 8; corner++)
        {
            var a = Corner(corner);
            for (int bit = 1; bit <= 4; bit <<= 1)
                if ((corner & bit) == 0)
                {
                    lines.Add(new(a, marker.Color));
                    lines.Add(new(Corner(corner | bit), marker.Color));
                }
        }
        Vector3 Corner(int index) => marker.RenderPosition + Vector3.Transform(new Vector3(
            (index & 1) == 0 ? -extent.X : extent.X,
            (index & 2) == 0 ? -extent.Y : extent.Y,
            (index & 4) == 0 ? -extent.Z : extent.Z), transform);
    }

    public void Dispose()
    {
        if (disposed) return;
        Clear();
        DisposeEffects();
        DisposeFallbackTextures();
        collisionPass?.Dispose();
        outline?.Dispose();
        markerBox?.Dispose();
        target?.Dispose();
        target = null;
        disposed = true;
    }
}
