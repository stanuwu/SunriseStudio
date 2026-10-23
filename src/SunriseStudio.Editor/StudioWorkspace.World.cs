using SunriseStudio.Packages;
using SunriseStudio.Scene;

namespace SunriseStudio.Editor;

public sealed partial class StudioWorkspace
{
    private CancellationTokenSource? operationCancellation;
    private volatile string operationProgress = "";
    private StudioLoadProgress loadingProgress = new("Choose Sunrise");
    public StudioLoadProgress LoadingProgress => Volatile.Read(ref loadingProgress);
    private long publishedNameRevision;
    private WorldLoader? cachedWorldLoader;
    public StudioNameIndex? Names { get; private set; }
    public WorldCatalog? WorldCatalog { get; private set; }
    public WorldDocument? World { get; private set; }
    public int WorldRevision { get; private set; }
    public int SceneRevision { get; private set; }
    public int NameRevision { get; private set; }
    public SceneDocument? ActiveScene => World?.Scene ?? Document?.Scene;
    public SceneDocument? NativePlanScene { get; private set; }
    // Work labels the busy checks below compare; BeginWork and ReportProgress use the same constants.
    private const string OpeningPackages = "Opening packages", ReadingNames = "Reading names",
        ReadingWorlds = "Reading maps and missions", PreparingShaders = "Preparing game shaders";
    public bool IsNaming => IsBusy && (Status is OpeningPackages or ReadingNames);
    public bool IsCatalogingWorlds => IsBusy && operationProgress == ReadingWorlds;
    public bool IsPreparingShaders => IsBusy && Status == PreparingShaders;
    public string NativeError { get; private set; } = "";
    public string BackgroundStatus => IsBusy ? operationProgress : "";
    private CancellationToken OperationToken => operationCancellation?.Token ?? lifetime.Token;

    private void ReportProgress(string message)
    {
        operationProgress = message;
        Volatile.Write(ref loadingProgress, StudioLoadProgress.FromMessage(message));
    }

    private void ReportProgress(string stage, int completed, int total)
    {
        operationProgress = stage;
        Volatile.Write(ref loadingProgress, new(stage, completed, total));
    }

    public string DisplayName(PackageEntry entry) => Names?.Describe(entry)
        ?? Catalog?.GetName(entry.TagHash) ?? FriendlyKind(entry);
    public string DisplayName(uint tag) => Names?.Describe(tag)
        ?? Catalog?.GetName(tag) ?? $"Asset {tag:X8}";
    public string? ResolveName(uint tag) => Names?.Describe(tag) ?? Catalog?.GetName(tag);
    public string? ResolveHash(uint hash) => Names?.ResolveHash(hash) ?? Catalog?.GetName(hash);
    /** A clip keeps its "Clip N" label unless the index holds a real name for it. */
    public string? ResolveClipName(uint tag) => Names?.Lookup(tag) is { Confidence: not StudioNameConfidence.Fallback } info
        ? info.DisplayName : null;

    public void OpenMap(uint mapTag, WorldLoadOptions? options = null) =>
        LoadWorld(mapTag, null, options ?? new(), false);

    public void OpenMission(uint mapTag, uint missionTag, WorldLoadOptions? options = null) =>
        LoadWorld(mapTag, missionTag, options ?? new(), false);

    public void ReloadWorld(WorldLoadOptions? options = null) => ReloadWorld(options, "Reload requested.");

    private void ReloadWorld(WorldLoadOptions? options, string reason)
    {
        if (World is not { } world) return;
        LoadWorld(world.Map.Tag, world.MissionTag, options ?? world.Options, true, reason);
    }

    /** How the last change reached the open world: in place or by reload, why, and how long. */
    public sealed record WorldRefresh(bool Incremental, string Reason, double Milliseconds);

    public WorldRefresh? LastWorldRefresh { get; private set; }

    // Tags changed since the world was decoded or last moved; null when a change is unknown.
    private HashSet<uint>? worldChanges = [];
    private readonly List<WorldPlacementUpdate> placementUpdates = [];

    // Tags changed since the collision layer last took them; null when a change is unknown.
    private HashSet<uint>? collisionChanges = [];

    private void NoteWorldChanges(IEnumerable<uint>? changedTags)
    {
        if (changedTags is null) { worldChanges = null; collisionChanges = null; }
        else { worldChanges?.UnionWith(changedTags); collisionChanges?.UnionWith(changedTags); }
    }

    /** Tags whose read bytes changed since the last call; null when a change is not known. */
    public IReadOnlyCollection<uint>? TakeCollisionChanges()
    {
        var result = collisionChanges;
        collisionChanges = [];
        return result;
    }

    /** Placement updates applied since the last call, oldest first. A world load drops them. */
    public IReadOnlyList<WorldPlacementUpdate> TakePlacementUpdates()
    {
        var result = placementUpdates.ToArray();
        placementUpdates.Clear();
        return result;
    }

    /** A placement-only change moves the loaded objects; any other change reloads the world. */
    private void RefreshWorld()
    {
        if (World is null) return;
        string reason = worldChanges is null ? "The change is not known." : "";
        if (worldChanges is { } changed && TryMoveWorld(changed, out reason)) return;
        ReloadWorld(null, reason);
    }

    /** Window thread only. On false the caller reloads, which replaces any object already moved. */
    private bool TryMoveWorld(HashSet<uint> changed, out string reason)
    {
        var world = World!;
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        var update = WorldPlacementUpdate.Prepare(Catalog!, world, changed, out reason);
        if (update is null) return false;
        var plan = NativePlan is { } prepared && ReferenceEquals(NativePlanScene, world.Scene) ? prepared : null;
        update.Apply();
        // Objects already moved; the reload after a refused plan update replaces them.
        if (plan is not null && !plan.UpdatePlacements(update.Instances, out reason)) return false;
        worldChanges = [];
        placementUpdates.Add(update);
        var document = Document;
        if (document is not null && DependsOn(document, changed)) RereadDocument(document);
        LastWorldRefresh = new(true, "", System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds);
        return true;
    }

    /** A document is kept only when every change is placement data it did not read. */
    private bool DependsOn(StudioDocument document, IReadOnlySet<uint>? changed) =>
        changed is null || changed.Any(tag => !Catalog!.TryGetEntry(tag, out var entry)
            || !WorldPlacementUpdate.IsPlacementData(entry))
        || document.SourceTags?.Overlaps(changed) == true;

    /** The asset tab reads its document again while the world stays in place. */
    private void RereadDocument(StudioDocument prior)
    {
        if (Catalog is not { } catalog) return;
        var project = Project;
        BeginWork($"Reading {prior.Name}", () =>
        {
            StudioDocument? document = null;
            if (prior.AddedAssetId is { } id && project?.AddedAssets.FirstOrDefault(asset => asset.Id == id) is { } added)
                document = ReadAddedDocument(project, added, prior.OpenContext);
            else if (prior.AddedAssetId is null && catalog.TryGetEntry(prior.Entry.TagHash, out var entry))
                document = ReadDocument(catalog, entry, DisplayName(entry), entry.Reference is not (0x808091DE or 0x80809994),
                    prior.OpenContext);
            return () =>
            {
                Document = document;
                DocumentRevision++;
            };
        }, blocking: false, keepNotice: true);
    }

    /** World loading keeps the asset inspector separate from the map's scene and mission overlay. */
    private void LoadWorld(uint mapTag, uint? missionTag, WorldLoadOptions options, bool keepDocument, string reason = "")
    {
        if (IsBusy || Catalog is not { } catalog) return;
        // Another world's textures are released; a reload of the same world reuses them.
        if (!keepDocument) { worldTextures.Clear(); cachedWorldLoader?.ForgetMeshes(null); }
        var names = Names;
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        var priorDocument = keepDocument ? Document : null;
        var connections = Project?.BubbleConnections ?? [];
        // The asset tab keeps a document that no change reached.
        var keptDocument = priorDocument is not null && !DependsOn(priorDocument, worldChanges) ? priorDocument : null;
        BeginWork(missionTag is null ? $"Loading {DisplayName(mapTag)}" : $"Loading {DisplayName(missionTag.Value)}", () =>
        {
            int entriesRead = 0;
            ReportProgress("Reading scene");
            var loader = cachedWorldLoader ?? new WorldLoader(catalog, names);
            loader.Progress = _ => ReportProgress("Reading scene", ++entriesRead, 0);
            var world = missionTag is uint mission
                ? loader.LoadMission(mapTag, mission, options, OperationToken)
                : loader.LoadMap(mapTag, options, OperationToken);
            var exits = AddCrossingExits(world, connections);
            uint documentTag = priorDocument?.AddedAssetId is null && priorDocument is not null
                ? priorDocument.Entry.TagHash : missionTag ?? mapTag;
            StudioDocument? document = keptDocument;
            if (document is null && priorDocument?.AddedAssetId is { } id && Project is { } project
                && project.AddedAssets.FirstOrDefault(asset => asset.Id == id) is { } added)
                document = ReadAddedDocument(project, added, priorDocument.OpenContext);
            else if (document is null && catalog.TryGetEntry(documentTag, out var entry))
                document = ReadDocument(catalog, entry, DisplayName(entry), entry.Reference is not (0x808091DE or 0x80809994),
                    priorDocument?.OpenContext);
            var native = PrepareScene(world.Scene, out string nativeError);
            var worlds = loader.BuildCatalog(OperationToken);
            return () =>
            {
                World = world;
                crossingExits = exits;
                // This world holds every change so far; a failed load keeps them pending.
                worldChanges = [];
                placementUpdates.Clear();
                cachedWorldLoader = loader;
                WorldCatalog = worlds;
                Document = document;
                if (!keepDocument && document is not null) RememberAsset(document);
                AdoptNative(native, world.Scene); NativeError = nativeError;
                WorldRevision++; SceneRevision++; DocumentRevision++;
                if (keepDocument)
                    LastWorldRefresh = new(false, reason, System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds);
                Status = world.Name;
            };
        // A reload of the open world keeps the editor in view; a new world shows the loading screen.
        }, blocking: !keepDocument, keepNotice: keepDocument);
    }

    public void CloseWorld()
    {
        RequireIdle();
        BeforeAssetMutation?.Invoke();
        World = null;
        crossingExits = [];
        worldChanges = [];
        placementUpdates.Clear();
        worldTextures.Clear();
        cachedWorldLoader?.ForgetMeshes(null);
        Document = null;
        NativePlan = null; NativePlanScene = null;
        SceneRevision++; WorldRevision++; DocumentRevision++;
        Status = "World closed";
    }

    public void CancelShaderPreparation()
    {
        if (IsPreparingShaders) operationCancellation?.Cancel();
    }
}
