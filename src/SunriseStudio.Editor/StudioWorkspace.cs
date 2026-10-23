using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed record BrowserAsset(PackageEntry Entry, string Kind, StudioNameIndex Names)
{
    public string Name => Names.Describe(Entry);
    public string SearchText => $"{Name} {Kind} {Tags} {Entry.PackageName} {Entry.TagHash:X8} {Entry.Reference:X8}";
    public string Category { get; } = AssetCategories.SplitKind(Kind).Category;
    /** Empty when the category has no types. */
    public string Type { get; } = AssetCategories.SplitKind(Kind).Type;
    /** Extra search words, such as a combatant or prompt name. */
    public string Tags { get; init; } = "";
    /** Item rarity that colours the row; -1 for none. */
    public int Tier { get; init; } = -1;
    /** True when the row belongs to a browser category with its own viewer. */
    public bool InCategory { get; } = StudioCategories.ForKind(Kind) is not null;
}

/** Folder, when set, is where the result was written. Details, when set, is the tooltip. */
public sealed record WorkNotice(string Text, bool Failed, string? Folder = null, string? Details = null);

/** Tags of the entries a project adds to new packages. */
internal static class ProjectTags
{
    // New-package tags start at 0x80800000; the package id sits above a 13-bit entry index.
    public static uint Tag(ushort package, int entry) => checked(0x80800000U + ((uint)package << 13) + (uint)entry);

    public static uint Tag(this NativeAssetMetadata native) =>
        Tag(native.PackageId, native.EntryIndex ?? throw new InvalidDataException("A project asset needs an entry index."));
}

/** Added asset names are file names, so path characters are replaced. */
internal static class AssetNames
{
    // Longer names are cut to this many characters.
    private const int MaximumLength = 96;

    public static string FileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string clean = new(name.Trim().Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return clean.Length == 0 ? "asset" : clean[..Math.Min(clean.Length, MaximumLength)];
    }
}

public sealed record StudioDocument(PackageEntry Entry, string Name, byte[] Bytes,
    AssetInspection Inspection, SceneDocument? Scene, TextureData? Texture, string PreviewError,
    AssetAnimationSource? Animation = null, string? AddedAssetId = null,
    NativeScenePlan? NativePlan = null, string NativeError = "", AssetSurface? Surface = null,
    AssetOpenContext? OpenContext = null, AssetConnections? Connections = null, CuiPreviewData? Cui = null)
{
    /** Tags the inspection and preview scene read; null when the document reads no package tag. */
    public IReadOnlySet<uint>? SourceTags { get; init; }
    public IReadOnlyList<ConfigBindingCandidate> ConfigSites { get; init; } = [];
    /** Browser kind of the entry; it picks the category viewer. Empty for a plain open. */
    public string Kind { get; init; } = "";
    /** The assembled guardian of a guardian view; null for every other document. */
    public PreparedGuardian? Guardian { get; init; }
}

/** Workers read assets; the window thread adopts results and owns project mutations. */
public sealed partial class StudioWorkspace : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private Task<Action>? work;
    private bool disposed;
    /** ObjectStates holds object edits by key; a null state means no edit. */
    private sealed record EditFrame(uint Selection, IReadOnlyDictionary<uint, byte[]> Bytes,
        string? AddedAssetId = null, AssetOpenContext? Context = null,
        IReadOnlyDictionary<string, AddedAssetSnapshot?>? AddedStates = null,
        IReadOnlyDictionary<string, ObjectEdit?>? ObjectStates = null,
        IReadOnlyList<ProjectConfigBinding>? ConfigStates = null,
        ImportSelection? ReverseSelection = null);
    private sealed record ImportSelection(uint Tag, string? AddedId, AssetOpenContext? Context);
    private readonly Stack<EditFrame> undo = [];
    private readonly Stack<EditFrame> redo = [];
    private Dictionary<uint, AddedProjectAsset> nativeAdditions = [];
    public AssetInspectionService Inspectors { get; } = new();
    public PackageCatalog? Catalog { get; private set; }
    public StudioCacheOptions CacheOptions { get; set; } = new();
    public StudioProject? Project { get; private set; }
    public StudioDocument? Document { get; private set; }
    public BrowserAsset[] Assets { get; private set; } = [];
    /** Every asset kind present, sorted; the browser splits each into category and type. */
    public string[] BrowserKinds { get; private set; } = [];
    public string[] BrowserPackages { get; private set; } = ["All packages"];
    public string Status { get; private set; } = "Choose a Sunrise installation";
    public string Error { get; set; } = "";
    public bool IsBusy => work is not null;
    private bool workBlocks = true;
    /** True while work runs that the editor cannot be used during. */
    public bool IsBlocking => work is not null && workBlocks;
    /** The result of the last background command, shown until dismissed. */
    public WorkNotice? Notice { get; private set; }
    public void DismissNotice() => Notice = null;
    public bool CanEdit => Project is not null && Catalog is not null && !IsBusy;
    public bool CanUndo => CanEdit && undo.Count > 0;
    public bool CanRedo => CanEdit && redo.Count > 0;
    public int AssetRevision { get; private set; }
    public int ProjectRevision { get; private set; }
    public int DocumentRevision { get; private set; }
    public NativeScenePlan? NativePlan { get; private set; }
    public int NativeRevision { get; private set; }
    public event Action? Changed;
    public event Action? BeforeAssetMutation;

    /** Opening an installation never writes to its package directory. */
    public void OpenInstallation(string path, string? keys = null)
    {
        if (IsBusy) return;
        string root = StudioSettings.ResolveGameRoot(path);
        BeforeAssetMutation?.Invoke();
        BeginWork(OpeningPackages, () =>
        {
            var catalog = PackageCatalog.Open(root, string.IsNullOrWhiteSpace(keys) ? null : keys, CacheOptions);
            try
            {
                catalog.EnsureKeys();
                var names = StudioNameIndex.Create(catalog);
                LoadNames(names);
                ReportProgress("Preparing object edits");
                ObjectEditCompiler.PrepareInstallation(catalog, OperationToken,
                    (completed, total) => ReportProgress("Preparing object edits", completed, total));
                ReportProgress(ReadingWorlds);
                var loader = new WorldLoader(catalog, names);
                var worlds = loader.BuildCatalog(OperationToken);
                var rows = CreateRows(catalog, names);
                return () =>
                {
                    if (disposed) { catalog.Dispose(); return; }
                    Catalog?.Dispose();
                    Catalog = catalog;
                    PerkBehaviorEditor.StartCensus(catalog);
                    lock (spawnIndexLock) spawnIndex = null;
                    lock (guardianLock) guardianAssembler = null;
                    worldTextures.Clear();
                    Names = names;
                    cachedWorldLoader = loader;
                    ResetBrowserRows(rows);
                    nativeAdditions = [];
                    Project = null;
                    compiledObjects = ObjectEditCompilation.Empty; ObjectEditError = "";
                    LastBuild = null; LastInstall = null;
                    Document = null; pendingOpen = null;
                    ResetAssetVisits();
                    World = null; worldChanges = []; placementUpdates.Clear();
                    WorldCatalog = worlds; WorldRevision++; SceneRevision++; NameRevision++;
                    NativePlan = null; NativePlanScene = null; NativeError = ""; NativeRevision++;
                    undo.Clear(); redo.Clear();
                    AssetRevision++; DocumentRevision++;
                    Status = $"{rows.Assets.Length:N0} assets";
                };
            }
            catch { catalog.Dispose(); throw; }
        });
    }

    /** Publish refreshed names and catalogs together after all names are resolved. */
    public void IndexNames()
    {
        if (Catalog is not { } catalog || IsBusy) return;
        BeginWork(ReadingNames, () =>
        {
            var names = StudioNameIndex.Create(catalog);
            LoadNames(names);
            ReportProgress(ReadingWorlds);
            var loader = new WorldLoader(catalog, names);
            var worlds = loader.BuildCatalog(OperationToken);
            var rows = CreateRows(catalog, names);
            return () =>
            {
                Names = names; WorldCatalog = worlds;
                cachedWorldLoader = loader;
                ResetBrowserRows(rows);
                RefreshAddedBrowserRows();
                if (Document is { AddedAssetId: null, Guardian: null } document)
                    Document = document with { Name = names.Describe(document.Entry) };
                if (ActiveScene is { } scene) names.NameScene(scene);
                if (Document?.Scene is { } assetScene && !ReferenceEquals(assetScene, ActiveScene)) names.NameScene(assetScene);
                if (World is not null) World = World.Relabel(names);
                AssetRevision++; NameRevision++; WorldRevision++;
                Status = "Names ready";
            };
        });
    }

    private void LoadNames(StudioNameIndex names) => names.Load(OperationToken, _ =>
    {
        var progress = names.LoadingProgress;
        ReportProgress(progress.Stage, progress.Completed, progress.Total);
    });

    public static string AssetKind(PackageEntry entry) => AssetInspectionService.Classify(entry);

    private static string FriendlyKind(PackageEntry entry)
    {
        string kind = AssetCategories.KindLabel(entry);
        if (kind.StartsWith("class_", StringComparison.Ordinal))
            return AssetKind(entry) is "Other" ? "Asset" : AssetKind(entry);
        if (kind.StartsWith("s_", StringComparison.Ordinal) || kind.StartsWith("c_", StringComparison.Ordinal)) kind = kind[2..];
        kind = kind.Replace('_', ' ');
        return kind.Length == 0 ? "Asset" : char.ToUpperInvariant(kind[0]) + kind[1..];
    }

    /** The selected entry remains inspectable when its specialized preview is unsupported. */
    public void Select(uint tag, bool retainHistory = false, AssetOpenContext? context = null) =>
        SelectAsset(tag, context ?? (retainHistory && Document?.Entry.TagHash == tag ? Document.OpenContext : null));

    /**
     * Opens an asset the user chose. Choosing the open document, or the open map, reads nothing again.
     * Select rereads on purpose; edits call it to show their result.
     */
    public void Open(uint tag, AssetOpenContext? context = null)
    {
        // A choice made while work runs opens when the work ends; the last choice wins.
        if (IsBusy) { pendingOpen = (tag, context); return; }
        // The default only decides "already open"; Select gets the caller's context so its fallback runs.
        var target = context ?? DefaultContext(tag);
        if (tag != 0 && Document is { } document && document.Entry.TagHash == tag
            && SameView(document.OpenContext, target)) return;
        if (World is { MissionTag: null } world && world.Map.Tag == tag && target is null) return;
        Select(tag, context: context);
    }

    private (uint Tag, AssetOpenContext? Context)? pendingOpen;

    // A weapon view stores the animation owner it found; the request never carries one.
    private static bool SameView(AssetOpenContext? open, AssetOpenContext? target) =>
        open == target
        || open is { } held && target is { WeaponItemTag: > 0, EntityTag: 0 } && held with { EntityTag = 0 } == target;

    /** An empty context opens an asset plainly, with the flat tab bar and no category viewer. */
    public static readonly AssetOpenContext PlainOpen = new();

    /** Rereads the open entity with this authored spawn row; null is the native default. */
    public void PreviewSpawn(SpawnInput? spawn)
    {
        if (Document is not { Scene: { } scene } document || scene.SpawnChoices.Count == 0 && scene.EntityVariants.Count == 0
            || document.AddedAssetId != nativeAdditions.GetValueOrDefault(document.Entry.TagHash)?.Id) return;
        SelectAsset(document.Entry.TagHash, document.OpenContext, 0, new SpawnPick(spawn, Channels: scene.ChannelValues));
    }

    /** Rereads the open entity, which has no authored spawn, as a spawn asking for this variant. */
    public void PreviewVariant(uint variant)
    {
        if (Document is not { Scene.EntityVariants.Count: > 0 } document
            || document.AddedAssetId != nativeAdditions.GetValueOrDefault(document.Entry.TagHash)?.Id) return;
        SelectAsset(document.Entry.TagHash, document.OpenContext, variant,
            new SpawnPick(null, Channels: document.Scene!.ChannelValues));
    }

    /** Rereads the open entity with these variant parameter values over its spawn's own; empty uses the spawn's. */
    public void PreviewParameters(IReadOnlyDictionary<uint, uint> picks)
    {
        if (Document is not { Scene: { EntityParameters.Count: > 0 } scene } document
            || document.AddedAssetId != nativeAdditions.GetValueOrDefault(document.Entry.TagHash)?.Id) return;
        SelectAsset(document.Entry.TagHash, document.OpenContext, scene.PreviewVariant,
            new SpawnPick(scene.PreviewSpawn, picks, scene.ChannelValues), keepView: true);
    }

    /** Rereads the open entity with these object channel values by name; an empty set restores the rest values. */
    public void PreviewChannels(IReadOnlyDictionary<uint, float> values)
    {
        if (Document is not { Scene: { ObjectChannels.Count: > 0 } scene } document
            || document.AddedAssetId != nativeAdditions.GetValueOrDefault(document.Entry.TagHash)?.Id) return;
        SelectAsset(document.Entry.TagHash, document.OpenContext, scene.PreviewVariant,
            new SpawnPick(scene.PreviewSpawn, scene.PreviewParameters, values), keepView: true);
    }

    /**
     * A spawn the user chose; null Input is the native default. No pick opens the first spawn.
     * Parameters and Channels are the user's variant parameter picks and channel values.
     */
    private sealed record SpawnPick(SpawnInput? Input, IReadOnlyDictionary<uint, uint>? Parameters = null,
        IReadOnlyDictionary<uint, float>? Channels = null);
    private readonly object spawnIndexLock = new();
    private (PackageCatalog Catalog, AuthoredSpawnIndex Index)? spawnIndex;

    /** Built once per catalog, on the first entity opened; project edits do not rebuild it. */
    private AuthoredSpawnIndex SpawnIndex(PackageCatalog catalog)
    {
        lock (spawnIndexLock)
        {
            if (spawnIndex is { } built && ReferenceEquals(built.Catalog, catalog)) return built.Index;
            ReportProgress("Indexing authored spawns");
            var index = AuthoredSpawnIndex.Build(catalog, OperationToken,
                (done, total) => ReportProgress("Indexing authored spawns", done, total));
            spawnIndex = (catalog, index);
            return index;
        }
    }

    /** An entity opens on its first authored spawn unless a spawn or a variant was chosen. */
    private SceneLoadOptions PreviewOptions(PackageCatalog catalog, uint tag, uint previewVariant, SpawnPick? spawn)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != 0x80809C0F)
            return new() { PreviewVariant = previewVariant };
        var spawns = SpawnIndex(catalog).For(tag);
        return new()
        {
            PreviewVariant = previewVariant, AuthoredSpawns = spawns,
            PreviewSpawn = spawn is not null ? spawn.Input : spawns.Count != 0 ? spawns[0].First : null,
            PreviewParameters = spawn?.Parameters ?? new Dictionary<uint, uint>(),
            ChannelValues = spawn?.Channels ?? new Dictionary<uint, float>()
        };
    }

    /** @param keepView Keeps the open document on screen while this one reads, in place of the loading screen. */
    private void SelectAsset(uint tag, AssetOpenContext? context = null, uint previewVariant = 0, SpawnPick? spawn = null,
        bool keepView = false)
    {
        if (Catalog is not { } catalog || IsBusy) return;
        if (context is { WeaponItemTag: > 0, WeaponEffectClipTag: 0 }
            && tag != context.WeaponItemTag && catalog.TryGetEntry(tag, out var requested)
            && requested.Reference == 0x80808F49)
        {
            context = context with { SelectedAnimationClipTag = tag };
            tag = context.WeaponItemTag;
        }
        if (!catalog.TryGetEntry(tag, out var entry)) { Error = $"Asset {tag:X8} is not installed."; return; }
        string? addedId = nativeAdditions.GetValueOrDefault(tag)?.Id;
        if (World is null && entry.Reference == 0x808091DE) { OpenMap(tag); return; }
        if (World is null && entry.Reference == 0x80809994 && WorldCatalog is { } worlds)
        {
            var mission = worlds.Missions.FirstOrDefault(row => row.Tag == tag);
            if (mission?.MapTags.FirstOrDefault() is > 0 and var map) { OpenMission(map, tag); return; }
        }
        string name = DisplayName(entry);
        // No context opens the category view; an explicit empty context is a plain open.
        context ??= DefaultContext(tag);
        bool preview = World is null || entry.Reference is not (0x808091DE or 0x80809994);
        BeginWork($"Opening {name}", () =>
        {
            var document = ReadDocument(catalog, entry, name, preview, context, previewVariant, spawn)
                with { AddedAssetId = addedId };
            return () =>
            {
                Document = document;
                RememberAsset(document);
                if (World is null) { AdoptNative(document.NativePlan, document.Scene); NativeError = document.NativeError; SceneRevision++; }
                DocumentRevision++;
                Status = name;
            };
        // An open world stays in view while the asset tab reads; with no world the scene must load first.
        }, blocking: World is null && !keepView, keepNotice: World is not null || keepView);
    }

    /** The category view a plain open of this tag gets; null for a normal asset. */
    private AssetOpenContext? DefaultContext(uint tag) =>
        Catalog is { } catalog && Names is { } names && catalog.TryGetEntry(tag, out var entry)
            ? StudioCategories.DefaultContext(BrowserKind(entry, names).Kind, tag) : null;

    private string DocumentKind(PackageEntry entry) => Names is { } names ? BrowserKind(entry, names).Kind : AssetKind(entry);

    private StudioDocument ReadDocument(PackageCatalog catalog, PackageEntry entry, string name, bool loadPreview = true,
        AssetOpenContext? context = null, uint previewVariant = 0, SpawnPick? spawn = null) =>
        ReadDocumentContent(catalog, entry, name, loadPreview, context, previewVariant, spawn)
            with { Kind = context == PlainOpen ? "" : DocumentKind(entry) };

    private StudioDocument ReadDocumentContent(PackageCatalog catalog, PackageEntry entry, string name, bool loadPreview,
        AssetOpenContext? context, uint previewVariant, SpawnPick? spawn)
    {
        if (loadPreview && context is { Guardian: not null })
            return ReadGuardianDocument(catalog, entry, name, context);
        if (loadPreview && context is { WeaponItemTag: > 0 })
            return context.WeaponEffectClipTag != 0
                ? ReadWeaponEffectDocument(catalog, entry, name, context)
                : ReadWeaponModelDocument(catalog, entry, name, context);
        ReportProgress("Reading asset");
        byte[] bytes = catalog.Read(entry.TagHash);
        var sources = new HashSet<uint> { entry.TagHash };
        byte[] ReadTracked(uint tag)
        {
            lock (sources) sources.Add(tag);
            return catalog.Read(tag);
        }
        var inspection = Inspectors.Inspect(new(entry.TagHash, entry.Reference, bytes, name,
            ResolveName, catalog.ContainsTag, ReadTracked));
        var connectionsReader = new AssetConnectionReader(catalog);
        // One read of the package links; the preview scene's links are added once the scene loads.
        // A plain open is not passed on, so each link opens in its own category view.
        var links = connectionsReader.ReadDraft(entry.TagHash, context == PlainOpen ? null : context, ResolveName,
            Names?.GetAssetOwners(entry.TagHash), OperationToken);
        SceneDocument? scene = null;
        TextureData? texture = null;
        AssetAnimationSource? animation = null;
        CuiPreviewData? cui = null;
        IReadOnlyList<ConfigBindingCandidate> configSites = [];
        string previewError = "";
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            if (!loadPreview) return new(entry, name, bytes, inspection, null, null, "",
                OpenContext: context, Connections: connectionsReader.Complete(links, null, inspection, OperationToken))
            { SourceTags = sources };
            if (CuiDocument.Supports(entry.Reference))
            {
                ReportProgress("Reading interface");
                cui = CuiPreviewData.Read(catalog, entry.TagHash, entry.Reference, bytes, ResolveHash, OperationToken);
                configSites = CuiConfigBindingCompiler.Default.FindSites(entry.TagHash, entry.Reference, bytes);
                if (cui is not null && Project is { ConfigBindings.Count: > 0 } configuredProject)
                {
                    var config = configuredProject.CompileConfigBindings(catalog);
                    byte[] ReadConfigured(uint tag) => config.Assets.TryGetValue(tag, out var value) ? value : catalog.Read(tag);
                    var configured = CuiPreviewData.Read(catalog, entry.TagHash, entry.Reference,
                        ReadConfigured(entry.TagHash), ResolveHash, OperationToken, ReadConfigured);
                    if (configured is not null) cui = cui.WithConfiguration(configured);
                }
            }
            if (entry.Type == 32 && entry.SubType is >= 1 and <= 3)
                texture = new TextureDataReader(catalog).Read(entry.TagHash);
            else if (links.PreviewTag != entry.TagHash || AssetKind(entry) is "Map" or "Bubble" or "Activity" or "Static mesh"
                or "Entity / Definition" or "Entity model" or "Terrain")
                scene = new AssetSceneLoader(catalog) { NameResolver = ResolveHash, GameNames = Names?.GameNames,
                    IncludeLowerLods = true, Progress = _ => ReportProgress("Reading geometry") }
                    .Load(links.PreviewTag, PreviewOptions(catalog, links.PreviewTag, previewVariant, spawn), OperationToken);
            // A Havok packfile or a collision owner has no geometry; its empty scene carries the collision layer.
            else if (entry is { Type: 26, SubType: 7 } || entry.Reference == SunriseStudio.Scene.Collision.StaticCollisionReader.OwnerClass)
                scene = new SceneDocument { TagHash = entry.TagHash, Name = name };
            if (scene is not null) Names?.NameScene(scene);
            if (scene is not null && links.AnimationEntityTag != 0)
            {
                try { animation = AssetAnimationSource.Prepare(catalog, links.AnimationEntityTag, OperationToken, ResolveHash,
                    ResolveClipName); }
                catch (Exception error) when (error is not OperationCanceledException)
                { scene.Diagnostics.Add(error.Message); }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            previewError = error.Message;
        }
        var native = PrepareScene(scene, out string nativeError, assetPreview: true);
        ReportProgress("Reading materials and shaders");
        var surface = AssetSurface.Read(catalog, entry.TagHash, scene: scene, nameResolver: ResolveName,
            token: OperationToken, decodedTextures: native?.Textures);
        var connections = connectionsReader.Complete(links, scene, inspection, OperationToken);
        if (scene is not null) sources.UnionWith(scene.ReadTags);
        return new(entry, name, bytes, inspection, scene, texture, previewError, animation,
            NativePlan: native, NativeError: nativeError, Surface: surface, OpenContext: context, Connections: connections, Cui: cui)
        { SourceTags = sources, ConfigSites = configSites };
    }

    /** Added source files are owned project content; derived entries stay reconstructed in memory. */
    public void SelectAdded(string id, AssetOpenContext? context = null) => SelectAddedAsset(id, context: context);

    private void SelectAddedAsset(string id, AssetOpenContext? context = null)
    {
        if (Project is not { } project || IsBusy) return;
        RefreshOverlay([]);
        var asset = project.AddedAssets.FirstOrDefault(item => item.Id == id)
            ?? throw new KeyNotFoundException("Added asset is missing.");
        // A native addition opens in its category view, as an installed asset does.
        if (asset.Native is { EntryIndex: not null } native) context ??= DefaultContext(native.Tag());
        BeginWork($"Opening {asset.Name}", () =>
        {
            var document = ReadAddedDocument(project, asset, context);
            return () =>
            {
                Document = document;
                RememberAsset(document);
                if (World is null) { AdoptNative(document.NativePlan, document.Scene); NativeError = document.NativeError; SceneRevision++; }
                DocumentRevision++; Status = asset.Name;
            };
        }, blocking: World is null, keepNotice: World is not null);
    }

    private StudioDocument ReadAddedDocument(StudioProject project, AddedProjectAsset asset, AssetOpenContext? context = null)
    {
        byte[] bytes = project.ReadAddedAsset(asset.Id, Catalog);
        var metadata = asset.Native;
        int index = metadata?.EntryIndex ?? 0;
        uint tag = metadata is null ? 0 : ProjectTags.Tag(metadata.PackageId, index);
        var entry = new PackageEntry(tag, metadata?.Reference ?? 0,
            (int)((metadata?.TypeInfo ?? 0) >> 9 & 127), (int)((metadata?.TypeInfo ?? 0) >> 6 & 7),
            bytes.Length, project.Name, metadata?.PackageId ?? 0, index)
        { TypeInfo = metadata?.TypeInfo ?? 0 };
        if (metadata is not null && Catalog is { } catalog)
            return ReadDocument(catalog, entry, asset.Name, World is null || entry.Reference is not (0x808091DE or 0x80809994), context)
                with
            { AddedAssetId = asset.Id };
        var inspection = Inspectors.Inspect(new(tag, entry.Reference, bytes, asset.Name,
            ResolveName, Catalog is null ? null : Catalog.ContainsTag, Catalog is null ? null : Catalog.Read));
        SceneDocument? scene = null;
        TextureData? texture = null;
        string error = "";
        string path = Path.Combine(project.DirectoryPath, asset.Path);
        try
        {
            string extension = Path.GetExtension(asset.Name).ToLowerInvariant();
            if (extension is ".obj" or ".glb" or ".gltf")
            {
                var imported = extension == ".obj" ? ObjInterchange.Read(path) : GltfInterchange.Read(path);
                scene = new SceneDocument { Name = asset.Name };
                foreach (var mesh in imported)
                    scene.Instances.Add(new MeshInstance { Name = mesh.Name, Mesh = ObjInterchange.ToMesh(mesh) });
            }
            else if (extension == ".dds") texture = DdsInterchange.Read(path);
            else if (extension == ".png") texture = PngInterchange.ToTexture(PngInterchange.Read(path));
        }
        catch (Exception failure) { error = failure.Message; }
        return new(entry, asset.Name, bytes, inspection, scene, texture, error, AddedAssetId: asset.Id, OpenContext: context);
    }

    public void CreateProject(string folder, string name)
    {
        RequireIdle();
        AdoptProject(StudioProject.Create(folder, name));
    }

    public void OpenProject(string path)
    {
        RequireIdle();
        AdoptProject(StudioProject.Open(path));
    }

    /** Decoders see project bytes while delta creation always reads the installed original. */
    private void AdoptProject(StudioProject? project)
    {
        if (project is not null && Catalog is { } catalog) project.ValidateWideHashAliases(catalog);
        BeforeAssetMutation?.Invoke();
        if (project is not null) project.ObjectEditCompiler ??= ObjectEditCompiler.Default;
        if (project is not null) project.ConfigBindingCompiler ??= CuiConfigBindingCompiler.Default;
        Project = project;
        LastBuild = null; LastInstall = null;
        ProjectRevision++;
        RefreshOverlay();
        undo.Clear(); redo.Clear();
        if (World is not null) ReloadWorld();
        else if (Document is { AddedAssetId: null } document) Select(document.Entry.TagHash, context: document.OpenContext);
        else if (Document is { AddedAssetId: not null }) { Document = null; DocumentRevision++; }
        else Status = project?.Name ?? "View only";
        Changed?.Invoke();
    }

    public void CloseProject()
    {
        RequireIdle();
        AdoptProject(null);
    }

    /** A change is saved before the visible document is replaced. */
    public void ApplyBytes(byte[] edited, string action = "Saved delta")
    {
        var current = Document ?? throw new InvalidOperationException("Select an asset first.");
        if (current.Bytes.AsSpan().SequenceEqual(edited)) return;
        if (current.AddedAssetId is { } id && current.Entry.TagHash == 0)
        {
            RequireEditable();
            MutateProject(() => Project!.UpdateAddedAsset(id, edited, Catalog!));
            PushUndo(new(current.Entry.TagHash,
                new Dictionary<uint, byte[]> { [current.Entry.TagHash] = current.Bytes }, id));
            ProjectRevision++;
            SelectAdded(id, current.OpenContext);
            Changed?.Invoke();
            return;
        }
        ApplyAssetEdits(new Dictionary<uint, byte[]> { [current.Entry.TagHash] = edited }, action);
    }

    /** Related payloads are saved as one project transaction and one undo step. */
    public void ApplyAssetEdits(IReadOnlyDictionary<uint, byte[]> edited, string action = "Saved deltas")
    {
        RequireEditable();
        var current = Document ?? throw new InvalidOperationException("Select an asset first.");
        var previous = edited.Keys.ToDictionary(tag => tag, ReadRawBytes);
        var changed = edited.Select(pair => (pair.Key, Value: RebaseOnRaw(pair.Key, pair.Value, previous[pair.Key])))
            .Where(pair => !pair.Value.AsSpan().SequenceEqual(previous[pair.Key]))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        if (changed.Count == 0) { Status = "No changes"; return; }
        foreach (uint tag in previous.Keys.Except(changed.Keys).ToArray()) previous.Remove(tag);
        MutateProject(() => ApplyMixedEdits(changed), changed.Keys);
        ProjectRevision++;
        PushUndo(new(current.Entry.TagHash, previous, current.AddedAssetId, current.OpenContext));
        if (World is not null) RefreshWorld();
        else if (current.AddedAssetId is { } addedId) SelectAdded(addedId, current.OpenContext);
        else Select(current.Entry.TagHash, true);
        Status = action;
        Changed?.Invoke();
    }

    /** Undo stores transient working bytes in memory, never in the project directory. */
    public void Undo()
    {
        if (!CanUndo) return;
        var previous = undo.Peek();
        var next = ReadFrame(previous);
        RestoreFrame(previous);
        ProjectRevision++;
        undo.Pop(); redo.Push(next);
        SelectFrame(previous);
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (!CanRedo) return;
        var next = redo.Peek();
        var previous = ReadFrame(next);
        RestoreFrame(next);
        ProjectRevision++;
        redo.Pop(); undo.Push(previous);
        SelectFrame(next);
        Changed?.Invoke();
    }

    public void Revert(uint tag)
    {
        RequireEditable();
        if (!Project!.ContainsEdit(tag)) return;
        byte[] previous = ReadRawBytes(tag);
        MutateProject(() => Project!.Revert(tag, Catalog), [tag]);
        PushUndo(new(tag, new Dictionary<uint, byte[]> { [tag] = previous },
            Context: Document?.Entry.TagHash == tag ? Document.OpenContext : null));
        ProjectRevision++;
        if (World is not null) RefreshWorld();
        else if (Document is { AddedAssetId: { } addedId }) SelectAdded(addedId, Document.OpenContext);
        else if (Document is { } selected) Select(selected.Entry.TagHash, context: selected.OpenContext);
        Changed?.Invoke();
    }

    /** Keeps the newest 32 steps; a new step clears redo. */
    private void PushUndo(EditFrame frame)
    {
        undo.Push(frame);
        if (undo.Count > 32)
        {
            var keep = undo.Take(32).Reverse().ToArray();
            undo.Clear();
            foreach (var item in keep) undo.Push(item);
        }
        redo.Clear();
    }

    /** A null tag list means the change is unknown, so the world catalog is rebuilt. */
    private void MutateProject(Action action, IEnumerable<uint>? changedTags = null)
    {
        BeforeAssetMutation?.Invoke();
        var project = Project; int saved = project?.Revision ?? 0;
        try { action(); RefreshOverlay(changedTags); }
        catch
        {
            // A failure that saved nothing leaves every view valid; reloading them would close open dialogs.
            if (!ReferenceEquals(project, Project) || project?.Revision != saved) { DocumentRevision++; SceneRevision++; }
            throw;
        }
    }

    // The world catalog reads only map roots, map definitions and scenarios.
    private static readonly HashSet<uint> WorldCatalogClasses = [0x808091DE, 0x80809962, 0x80809994];

    /** Overlay metadata lets package decoders follow new project entries without writing packages. */
    private void RefreshOverlay(IEnumerable<uint>? changedTags = null)
    {
        if (Catalog is not { } catalog) return;
        uint[] previousCategoryTags = changedTags is null
            ? catalog.GetOverlayEntries().Select(row => row.Entry.TagHash).Concat(compiledObjects.Assets.Keys).ToArray() : [];
        var previousObjects = compiledObjects;
        compiledObjects = CompileObjects(catalog);
        bool generatedChanged = !SameGeneratedMembership(previousObjects, compiledObjects);
        if (changedTags is not null)
            changedTags = changedTags.Concat(ChangedObjectTags(previousObjects, compiledObjects)).Distinct().ToArray();
        // Generated tags are in the changed set; the placement update checks what they draw.
        NoteWorldChanges(changedTags);
        if (generatedChanged || changedTags is null || changedTags.Any(tag => !catalog.TryGetEntry(tag, out var changed)
            || WorldCatalogClasses.Contains(changed.Reference)))
            cachedWorldLoader = null;
        // Textures and buffers are not type 8 or 16 tag data; a tag data or collision change keeps them.
        bool buffersChanged = changedTags is null
            || changedTags.Any(tag => !catalog.TryGetEntry(tag, out var changed)
                || changed.Type is not (8 or 16) && !WorldPlacementUpdate.IsViewerIgnored(changed));
        if (buffersChanged) worldTextures.Clear();
        cachedWorldLoader?.ForgetMeshes(buffersChanged ? null : changedTags);
        if (Project is not { } project)
        {
            catalog.ReadOverride = null;
            catalog.SetOverlayEntries([]);
            nativeAdditions = [];
            RefreshComponentRows(changedTags ?? previousCategoryTags);
            RefreshAddedBrowserRows();
            return;
        }
        var rows = new List<PackageOverlayEntry>();
        var additions = new Dictionary<uint, AddedProjectAsset>();
        foreach (var change in project.Changes.Where(change => !compiledObjects.Assets.ContainsKey(change.TagHash)))
        {
            if (!catalog.TryGetEntry(change.TagHash, out var entry))
                throw new InvalidDataException($"Original asset {change.TagHash:X8} is missing.");
            rows.Add(new(entry with { Size = change.EditedSize }));
        }
        foreach (var (tag, bytes) in compiledObjects.Assets)
        {
            if (!catalog.TryGetEntry(tag, out var entry))
                throw new InvalidDataException($"Compiled original asset {tag:X8} is missing.");
            rows.Add(new(entry with { Size = bytes.Length }));
        }
        foreach (var asset in project.AddedAssets.Where(asset => asset.Native is not null))
        {
            var metadata = asset.Native!;
            int index = metadata.EntryIndex!.Value;
            uint tag = ProjectTags.Tag(metadata.PackageId, index);
            var entry = new PackageEntry(tag, metadata.Reference, (int)(metadata.TypeInfo >> 9 & 127),
                (int)(metadata.TypeInfo >> 6 & 7), checked((int)asset.Size),
                $"w64_sunrise_{metadata.PackageId:x4}_0", metadata.PackageId, index)
            { TypeInfo = metadata.TypeInfo };
            rows.Add(new(entry, asset.Name) { WideHashes = metadata.WideHashes ?? WideHashAliases.Empty });
            additions.Add(tag, asset);
        }
        var generated = compiledObjects.GeneratedAssets.ToDictionary(asset => asset.TagHash);
        foreach (var asset in generated.Values)
        {
            var metadata = asset.Native;
            var entry = new PackageEntry(asset.TagHash, metadata.Reference, (int)(metadata.TypeInfo >> 9 & 127),
                (int)(metadata.TypeInfo >> 6 & 7), asset.Data.Length,
                $"w64_sunrise_{metadata.PackageId:x4}_0", metadata.PackageId, metadata.EntryIndex!.Value)
            { TypeInfo = metadata.TypeInfo };
            rows.Add(new(entry, asset.Name)
            {
                Generated = true, GeneratedSourceTag = asset.SourceTag,
                InstanceOrigins = asset.InstanceOrigins, PlacementOrigins = asset.PlacementOrigins,
                Replaces = asset.Replaces, AddedInstances = asset.AddedInstances
            });
        }
        catalog.SetOverlayEntries(rows);
        nativeAdditions = additions;
        var objects = compiledObjects;
        // Object edits sit on top of raw deltas; the compiled bytes are shared, so each read gets a copy.
        catalog.ReadOverride = tag => nativeAdditions.TryGetValue(tag, out var added)
            ? project.ReadAddedAsset(added.Id, catalog)
            : generated.TryGetValue(tag, out var derived) ? derived.Data.ToArray()
            : objects.Assets.TryGetValue(tag, out var compiled) ? compiled.ToArray()
            : project.ContainsEdit(tag) ? project.ReadAsset(tag, catalog) : null;
        RefreshComponentRows(changedTags ?? previousCategoryTags.Concat(rows.Select(row => row.Entry.TagHash))
            .Concat(objects.Assets.Keys));
        RefreshAddedBrowserRows();
    }

    /** Undo captures only affected raw payloads, object edits and asset membership. */
    private EditFrame ReadFrame(EditFrame frame)
    {
        var bytes = frame.Selection == 0 && frame.AddedAssetId is { } id && frame.Bytes.ContainsKey(0)
            ? new Dictionary<uint, byte[]> { [0] = Project!.ReadAddedAsset(id, Catalog) }
            : frame.Bytes.Keys.ToDictionary(tag => tag, ReadRawBytes);
        var states = frame.AddedStates?.Keys.ToDictionary(id => id, id =>
            Project!.AddedAssets.Any(asset => asset.Id == id) ? Project.CaptureAddedAsset(id, Catalog) : null,
            StringComparer.Ordinal);
        var objects = frame.ObjectStates?.Keys.ToDictionary(key => key, Project!.FindObjectEdit, StringComparer.Ordinal);
        var next = frame with { Bytes = bytes, AddedStates = states, ObjectStates = objects,
            ConfigStates = frame.ConfigStates is null ? null : Project!.ConfigBindings.ToArray() };
        return frame.ReverseSelection is not { } selection ? next : next with
        {
            Selection = selection.Tag, AddedAssetId = selection.AddedId, Context = selection.Context,
            ReverseSelection = new(frame.Selection, frame.AddedAssetId, frame.Context)
        };
    }

    private void RestoreFrame(EditFrame frame)
    {
        if (frame.ConfigStates is { } config)
        {
            MutateProject(() => Project!.SetConfigBindings(config, Catalog!), []);
            return;
        }
        if (frame.ObjectStates is { Count: > 0 } objects && frame.Bytes.Count == 0)
        {
            MutateProject(() => Project!.RestoreObjectEdits(objects, Catalog!), []);
            return;
        }
        MutateProject(() =>
        {
            var edits = frame.Selection == 0 && frame.AddedAssetId is { } id && frame.Bytes.ContainsKey(0)
                ? new ProjectAssetEdits(new Dictionary<uint, byte[]>(), new Dictionary<string, byte[]> { [id] = frame.Bytes[0] })
                : SplitAssetEdits(frame.Bytes);
            Project!.RestoreTransaction(edits, frame.AddedStates ?? new Dictionary<string, AddedAssetSnapshot?>(), Catalog,
                frame.ObjectStates);
        }, frame.AddedAssetId is null && frame.AddedStates is not { Count: > 0 } ? frame.Bytes.Keys : null);
    }

    private void ApplyMixedEdits(IReadOnlyDictionary<uint, byte[]> edited)
    {
        var split = SplitAssetEdits(edited);
        Project!.ApplyEdits(split.Assets, Catalog!, split.AddedAssets);
    }

    private ProjectAssetEdits SplitAssetEdits(IReadOnlyDictionary<uint, byte[]> edited)
    {
        var installed = new Dictionary<uint, byte[]>();
        var added = new Dictionary<string, byte[]>();
        foreach (var (tag, bytes) in edited)
        {
            if (nativeAdditions.TryGetValue(tag, out var asset)) added.Add(asset.Id, bytes);
            else installed.Add(tag, bytes);
        }
        return new(installed, added);
    }

    private void SelectFrame(EditFrame frame)
    {
        if (frame.ReverseSelection is not null)
        {
            if (frame.AddedAssetId is { } added) SelectAdded(added, frame.Context);
            else if (frame.Selection != 0) Select(frame.Selection, context: frame.Context);
            else
            {
                Document = null; DocumentRevision++;
                if (World is null) { AdoptNative(null, null); NativeError = ""; SceneRevision++; }
            }
        }
        else if (World is not null) RefreshWorld();
        else if (frame.AddedAssetId is { } id) SelectAdded(id, frame.Context);
        else if (frame.Selection != 0) Select(frame.Selection, context: frame.Context);
    }

    public void RemoveAdded(string id)
    {
        RequireEditable();
        MutateProject(() => Project!.RemoveAddedAsset(id));
        ProjectRevision++;
        undo.Clear(); redo.Clear();
        if (World is not null) ReloadWorld();
        else if (Document?.AddedAssetId == id) { Document = null; DocumentRevision++; SceneRevision++; }
        else if (Document is { AddedAssetId: { } selectedId }) SelectAdded(selectedId, Document.OpenContext);
        else if (Document is { } selected) Select(selected.Entry.TagHash, context: selected.OpenContext);
        Changed?.Invoke();
    }

    public void Build(string? output = null)
    {
        RequireEditable();
        CompactSavedTables();
        var project = Project!;
        var catalog = Catalog!;
        BeginWork("Building packages", () =>
        {
            var result = project.Build(catalog, output);
            return () =>
            {
                LastBuild = result; Status = "Packages built";
                Notice = new($"Built {result.PackageFiles.Count:N0} package files.", false, result.OutputDirectory,
                    result.Notes.Count > 0 ? string.Join("\n", result.Notes) : null);
                Changed?.Invoke();
            };
        }, blocking: false);
    }

    public ProjectBuildResult? LastBuild { get; private set; }

    public void Execute(string description, Action operation) => BeginWork(description, () =>
    {
        operation();
        return () => { Status = "Done"; };
    });

    public void ImportEdits(Func<IReadOnlyDictionary<uint, byte[]>> decode)
    {
        RequireEditable();
        BeginWork("Reading imported changes", () =>
        {
            var edits = decode();
            return () => ApplyAssetEdits(edits);
        });
    }

    public void PrepareNative(SceneDocument? previewScene = null)
    {
        if (Catalog is not { } catalog || ActiveScene is not { } scene || IsBusy) return;
        scene = previewScene ?? scene;
        BeginWork(PreparingShaders, () =>
        {
            var plan = PrepareScene(scene, out string error, assetPreview: World is null || !ReferenceEquals(scene, World.Scene));
            return () => { AdoptNative(plan, scene); NativeError = error; Error = error; Status = "Game shaders ready"; };
        });
    }

    /** Decoded world textures, kept so an edit's reload does not decode them again. */
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, TextureData> worldTextures = new();

    /** Unsupported shader inputs retain geometry with a completed diagnostic. */
    private NativeScenePlan? PrepareScene(SceneDocument? scene, out string error, bool assetPreview = false)
    {
        error = "";
        if (Catalog is not { } catalog || scene is not { Instances.Count: > 0 }
            || !scene.Instances.Any(instance => instance.Mesh.Native is not null)) return null;
        try
        {
            return AssetViewport.PrepareNativeScene(catalog, scene, OperationToken,
                ReportProgress, textureBudgetBytes: long.MaxValue, assetPreview: assetPreview,
                textureCache: assetPreview ? null : worldTextures);
        }
        catch (Exception failure) when (failure is not OperationCanceledException and not OutOfMemoryException)
        { error = failure.Message; return null; }
    }

    private void AdoptNative(NativeScenePlan? plan, SceneDocument? scene)
    { NativePlan = plan; NativePlanScene = scene; NativeRevision++; }

    public void Run(Action action)
    {
        try { Error = ""; action(); }
        catch (Exception error) { Error = error.Message; }
    }

    private void RequireIdle()
    {
        if (IsBusy) throw new InvalidOperationException("Wait for the current operation.");
    }

    private void RequireEditable()
    {
        RequireIdle();
        if (Project is null) throw new InvalidOperationException("Create or open a project first.");
        if (Catalog is null) throw new InvalidOperationException("Open your Sunrise installation first.");
    }

    /**
     * A command the user started runs in the background; blocking work replaces the editor with the loading screen.
     * @param keepNotice Keeps the current notice; background work otherwise clears it.
     */
    private void BeginWork(string status, Func<Action> operation, bool blocking = true, bool keepNotice = false)
    {
        RequireIdle();
        Error = "";
        Status = status;
        workBlocks = blocking;
        if (!blocking && !keepNotice) Notice = null;
        operationProgress = "";
        Volatile.Write(ref loadingProgress, new(status));
        operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        work = Task.Run(operation, operationCancellation.Token);
    }

    /** Completed workers publish their state only between editor frames. */
    public void Poll()
    {
        if (work is not { IsCompleted: true } pending) return;
        var cancellation = operationCancellation;
        operationCancellation = null;
        work = null;
        string command = Status;
        bool background = !workBlocks;
        workBlocks = true;
        try
        {
            var adopt = pending.GetAwaiter().GetResult();
            cancellation?.Token.ThrowIfCancellationRequested();
            adopt();
            if (Names is { } names && publishedNameRevision != names.Revision)
            { publishedNameRevision = names.Revision; NameRevision++; }
            ContinueNavigation();
            Changed?.Invoke();
        }
        catch (OperationCanceledException) { Status = "Cancelled"; navigationTarget = null; pendingOpen = null; }
        catch (Exception error)
        {
            navigationTarget = null;
            Error = error.Message; Status = "Operation failed";
            if (background) Notice = new($"{command} failed: {error.Message}", true);
        }
        finally { cancellation?.Dispose(); }
        if (pendingOpen is { } next && !IsBusy && navigationTarget is null)
        {
            pendingOpen = null;
            Open(next.Tag, next.Context);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        if (work is { } pending)
        {
            try { pending.GetAwaiter().GetResult()(); }
            catch (Exception) { }
        }
        Catalog?.Dispose();
        operationCancellation?.Dispose();
        lifetime.Dispose();
    }
}
