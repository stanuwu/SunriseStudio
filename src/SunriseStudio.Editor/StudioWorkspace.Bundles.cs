using System.Numerics;
using SunriseStudio.Interchange;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioWorkspace
{
    public AssetBundleExportResult? LastAssetExport { get; private set; }
    public AssetBundleImportResult? LastAssetImport { get; private set; }
    public int TransferRevision { get; private set; }
    public bool LastTransferWasExport { get; private set; }
    public WorldSceneExportResult? LastWorldExport { get; private set; }

    public void ExportWorldScene(string folder)
    {
        RequireIdle();
        if (World is not { } world || Catalog is not { } catalog) return;
        var options = new WorldSceneExportOptions(Project);
        BeginWork("Exporting Blender world", () =>
        {
            var result = WorldSceneExport.Export(catalog, world.Scene, folder,
                options with { ShaderInputs = WorldShaderInputs(catalog, world.Scene), PartLods = DecoratorLods(catalog, world.Scene),
                    DecoratorInputs = DecoratorInputs(catalog, world.Scene) },
                OperationToken, ReportProgress);
            return () =>
            {
                LastWorldExport = result;
                Status = $"World exported: {result.Manifest.Statistics.Instances:N0} placements";
            };
        });
    }

    /**
     * The first LOD level that draws each SpeedTree part; the far billboard is the last level.
     * @return Parts whose levels do not read keep the scene's LOD.
     */
    private static Dictionary<MeshInstance, int> DecoratorLods(PackageCatalog catalog, SceneDocument scene)
    {
        var decorators = new NativeDecorators(catalog);
        var result = new Dictionary<MeshInstance, int>(ReferenceEqualityComparer.Instance);
        foreach (var instance in scene.Instances)
        {
            // A ground cell already has its LOD from its mesh order.
            if (instance.CellBounds is not null || instance.ObjectPlacement is not { } owner || instance.Mesh.Native is not { } native
                || !catalog.TryGetEntry(owner.SourceTag, out var entry) || entry.Reference != DecoratorTags.PayloadClass) continue;
            try { result[instance] = decorators.Levels(instance.Mesh.TagHash, instance.Mesh.PartIndex, native.RenderStage).First; }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException
                or IndexOutOfRangeException or IOException) { }
        }
        return result;
    }

    /**
     * Each decorator part's per-record shader inputs, read as the viewport's decorator draw reads them.
     * @return Parts whose payload, AO or LOD does not read are left out; Blender then reads zero.
     */
    private static Dictionary<MeshInstance, IReadOnlyList<WorldDecoratorInputs>> DecoratorInputs(PackageCatalog catalog,
        SceneDocument scene)
    {
        var decorators = new NativeDecorators(catalog)
        {
            SceneMap = catalog.TryGetEntry(scene.TagHash, out var sceneEntry) && sceneEntry.Reference == SMapRoot.Class ? scene.TagHash : 0
        };
        var maps = scene.OcclusionOwners.GroupBy(owner => owner.BubbleHash)
            .ToDictionary(group => group.Key, group => (IReadOnlySet<uint>)group.Select(owner => owner.MapTag).ToHashSet());
        var slices = NativeDecorators.SliceIndices(scene);
        var result = new Dictionary<MeshInstance, IReadOnlyList<WorldDecoratorInputs>>(ReferenceEqualityComparer.Instance);
        foreach (var instance in scene.Instances)
        {
            uint payload = instance.ObjectPlacement?.SourceTag ?? (instance.CellBounds is null ? 0 : instance.InstanceSourceTag);
            if (instance.Mesh.Native is null || !catalog.TryGetEntry(payload, out var entry) || entry.Reference != DecoratorTags.PayloadClass)
                continue;
            try
            {
                var part = decorators.Read(instance, maps.GetValueOrDefault(instance.BubbleHash) ?? new HashSet<uint>(),
                    slices.GetValueOrDefault(instance.BubbleHash) ?? new HashSet<int>());
                result[instance] = Enumerable.Range(part.Record, part.Count).Select(record => part.Payload.ShaderInputs(record))
                    .Select(value => new WorldDecoratorInputs(value.Record, value.Occlusion)).ToArray();
            }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException
                or IndexOutOfRangeException or IOException or KeyNotFoundException) { }
        }
        return result;
    }

    /**
     * Dye bits, dye buffers and plates of the scene's gear parts, from a draw plan of those parts alone,
     * and the object channel table of each entity part whose material reads channels.
     * @return Null when no part reads either; parts that draw as their plain material are left out.
     */
    private Dictionary<MeshInstance, BundleShaderInputs>? WorldShaderInputs(PackageCatalog catalog, SceneDocument scene)
    {
        var result = new Dictionary<MeshInstance, BundleShaderInputs>(ReferenceEqualityComparer.Instance);
        var bits = new Dictionary<uint, uint>();
        uint GearBits(uint material) => bits.TryGetValue(material, out uint value) ? value
            : bits[material] = NativeGearPlating.GearBits(catalog, material);
        var gear = new SceneDocument { Name = scene.Name, TagHash = scene.TagHash };
        gear.Instances.AddRange(scene.Instances.Where(instance => instance.Mesh.Native is { Kind: NativeMeshKind.Entity } native
            && (NativeGearDye.PartBit(instance) != 0 || GearBits(native.MaterialTag) != 0)));
        if (gear.Instances.Count != 0)
        {
            NativeGearPlating.SetDyes(gear, NativeGearPlating.DyesFor(scene));
            ReportProgress("Composing dyes and plate textures");
            var plan = AssetViewport.PrepareNativeScene(catalog, gear, OperationToken, ReportProgress, textureBudgetBytes: long.MaxValue);
            foreach (var (instance, inputs) in GuardianShaderInputs.Read(plan))
                if (inputs.DyeBit != 0 || inputs.ConstantBuffers.Count != 0 || inputs.Textures.Count != 0) result[instance] = inputs;
        }
        // Each placed entity draws with its model's rest channel values, as the viewport does.
        ReportProgress("Reading object channels");
        foreach (var (instance, table) in NativeScenePlan.ChannelTablesByPart(catalog, scene))
        {
            string key = $"channels {NativeScenePlan.ChannelKey(table)}";
            result[instance] = result.TryGetValue(instance, out var inputs)
                ? inputs with { Key = $"{inputs.Key}|{key}", ObjectChannels = table }
                : new(key, instance.Name.Split(':')[0], 0, new Dictionary<uint, Vector4[]>(), new Dictionary<uint, uint>(),
                    new Dictionary<uint, TextureData>()) { ObjectChannels = table };
        }
        return result.Count == 0 ? null : result;
    }

    /** Full working copies remain outside the project; all linked data uses one source context. */
    public void ExportAssetBundle(string folder)
    {
        RequireIdle();
        if (Document is not { } document || Catalog is not { } catalog) return;
        // Clips use the same names as the Animations tab.
        var options = new AssetBundleExportOptions(document.OpenContext, Project, NameResolver: tag =>
            catalog.TryGetEntry(tag, out var entry) && entry.Reference == SAnimationClip.Class ? ResolveClipName(tag) : ResolveName(tag))
        { Scene = EntityInputs(document.Scene) };
        if (document.Guardian is { } guardian) { ExportGuardianBundle(folder, document, guardian, catalog, options); return; }
        BeginWork("Exporting Blender asset", () =>
        {
            var result = AssetBundle.Export(catalog, document.Entry.TagHash, folder, options, OperationToken, ReportProgress);
            return () =>
            {
                LastAssetExport = result; LastTransferWasExport = true; TransferRevision++;
                Status = "Blender asset exported";
            };
        });
    }

    /** The spawn, variant, parameter picks and channel values the open entity shows, so the export matches the view. */
    private static SceneLoadOptions? EntityInputs(SceneDocument? scene) => scene is null ? null : new()
    {
        PreviewSpawn = scene.PreviewSpawn, PreviewVariant = scene.PreviewVariant,
        PreviewParameters = scene.PreviewParameters, ChannelValues = scene.ChannelValues
    };

    /** A complete bundle is validated before its edits and additions become one project transaction. */
    public void ImportAssetBundle(string path, AssetBundleImportOptions options)
    {
        RequireEditable();
        var project = Project!;
        var catalog = Catalog!;
        var selected = Document;
        BeginWork("Importing Blender asset", () =>
        {
            var prepared = AssetBundle.PrepareImport(project, catalog, path, options, OperationToken, ReportProgress);
            var previous = prepared.Edits.Keys.ToDictionary(tag => tag, ReadRawBytes);
            var previousObjects = prepared.ObjectEdits.ToDictionary(edit => edit.Key, edit => project.FindObjectEdit(edit.Key),
                StringComparer.Ordinal);
            bool retainUndo = selected is not null && prepared.Additions.Count == 0;
            foreach (string id in prepared.AddedEdits.Keys)
            {
                uint tag = nativeAdditions.FirstOrDefault(pair => pair.Value.Id == id).Key;
                if (tag == 0) retainUndo = false;
                else previous[tag] = catalog.Read(tag);
            }
            OperationToken.ThrowIfCancellationRequested();
            return () =>
            {
                if (prepared.Edits.Count == 0 && prepared.AddedEdits.Count == 0 && prepared.Additions.Count == 0
                    && prepared.ObjectEdits.Count == 0 && prepared.CollisionShapes.Count == 0)
                {
                    LastAssetImport = new(prepared.RootTag, nativeAdditions.GetValueOrDefault(prepared.RootTag)?.Id,
                        0, [], prepared.Notices);
                    LastTransferWasExport = false; TransferRevision++; Status = "No changes";
                    return;
                }
                BeforeAssetMutation?.Invoke();
                BeginWork("Saving imported assets", () =>
                {
                    AssetBundleImportResult result;
                    try { result = AssetBundle.ApplyImport(project, catalog, prepared); }
                    catch (Exception error)
                    {
                        return () =>
                        {
                            DocumentRevision++; SceneRevision++;
                            throw new InvalidOperationException(error.Message, error);
                        };
                    }
                    return () =>
                    {
                        RefreshOverlay(); ProjectRevision++;
                        // New collision shapes are undone by removing them in the same step.
                        var created = prepared.CollisionShapes.Count > 0
                            ? result.AddedAssets.ToDictionary(asset => asset.Id, _ => (SunriseStudio.Projects.AddedAssetSnapshot?)null, StringComparer.Ordinal)
                            : null;
                        if (retainUndo && (previous.Count > 0 || previousObjects.Count > 0 || created is not null))
                            PushUndo(new(selected!.Entry.TagHash, previous, selected.AddedAssetId, selected.OpenContext, created,
                                ObjectStates: previousObjects.Count > 0 ? previousObjects : null));
                        else
                        {
                            if (result.AddedAssets.Count > 0) undo.Clear();
                            redo.Clear();
                        }
                        LastAssetImport = result; LastTransferWasExport = false; TransferRevision++;
                        if (options.Mode == AssetBundleImportMode.ApplyDeltas && World is not null) ReloadWorld();
                        else if (result.RootAddedAssetId is { } added) SelectAdded(added);
                        else Select(result.RootTag, context: selected?.OpenContext);
                    };
                });
            };
        });
    }
}
