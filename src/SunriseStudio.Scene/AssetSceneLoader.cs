using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

/** Reads typed package edges; unknown classes remain visible in the diagnostics. */
public sealed partial class AssetSceneLoader(PackageCatalog catalog)
{
    private IDictionary<uint, MeshData[]> meshes = new Dictionary<uint, MeshData[]>();
    private bool sharedMeshes;
    /** Decoded meshes kept across loads; the owner removes a tag when its bytes change. */
    public IDictionary<uint, MeshData[]>? MeshCache
    {
        init { if (value is not null) { meshes = value; sharedMeshes = true; } }
    }
    // The placed terrain resource row while its terrain tag is visited; its +0x10 word is the AO key.
    private ReadOnlyMemory<byte> terrainResource;
    private readonly Dictionary<uint, byte[]> tags = [];
    private readonly HashSet<SceneVisitKey> visited = [];
    private CancellationToken cancellation;
    private SceneDocument scene = new();
    private readonly Dictionary<uint, int> unrenderedPlacements = [];
    private readonly HashSet<string> diagnosticSet = [];
    public Func<uint, string?>? NameResolver { get; set; }
    public Action<string>? Progress { get; set; }
    /** Also decode coarser static mesh levels of detail for distance selection. */
    public bool IncludeLowerLods { get; set; }
    /** Reads tag bytes in place of the catalog; null reads the catalog. */
    public Func<uint, byte[]>? Source { get; init; }
    /** Decodes only the static instances whose bounds row meets this world box; null decodes all. */
    public (Vector3 Min, Vector3 Max)? Region { get; init; }

    public SceneDocument LoadMap(uint tag, CancellationToken token = default) => Load(tag, token);
    public SceneDocument LoadBubble(uint tag, CancellationToken token = default) => Load(tag, token);
    public SceneDocument LoadMesh(uint tag, CancellationToken token = default) => Load(tag, token);
    public SceneDocument LoadScenario(uint tag, SceneLoadOptions selection, CancellationToken token = default) => Load(tag, selection, token);

    public SceneDocument Load(uint tag, CancellationToken token = default) => Load(tag, new SceneLoadOptions(), token);

    /** A loader owns one worker operation at a time. */
    public SceneDocument Load(uint tag, SceneLoadOptions selection, CancellationToken token = default)
    {
        options = selection;
        cancellation = token;
        activeEntryIndex = uint.MaxValue;
        staticPlacement = null;
        visited.Clear();
        activeMapVisit = null;
        mapVisitChildren.Clear();
        mapContainerVisits.Clear();
        mapVisitContent.Clear();
        volumeConfigs.Clear();
        tags.Clear();
        if (!sharedMeshes) meshes.Clear();
        nativeInputs.Clear();
        variantMeshes.Clear();
        initialMaterials.Clear();
        diagnosticSet.Clear();
        initialWarnings.Clear();
        lightTables.Clear();
        lightParameters.Clear();
        inlineNames.Clear();
        inlineNameSources.Clear();
        slotNames.Clear();
        authoredPlacements.Clear();
        authoredSquads.Clear();
        authoredRules.Clear();
        deviceLinks.Clear();
        volumeNameLinks.Clear();
        placedModels.Clear();
        placedModelNames.Clear();
        unrenderedPlacements.Clear();
        stateRanges.Clear();
        squadMembers.Clear();
        scene = new SceneDocument { Name = Name(tag), TagHash = tag };
        hasMapLayerCatalog = Class(tag) == SMapRoot.Class;
        if (hasMapLayerCatalog)
            scene.MapLayers.AddRange(DescribeMapLayers(tag));
        ValidateMapSelection();
        if (Class(tag) == SScenario.Class)
        {
            scene.Scenario = DescribeScenario(tag);
            ValidateScenarioSelection(scene.Scenario);
        }
        Visit(tag, 0, Matrix4x4.Identity, 0);
        AssignMapContainers();
        ResolveSquadAnchors();
        ResolveDeviceNames();
        ReadPlacedClassModels();
        ResolveAuthoredNames();
        AssignOcclusionOwners();
        FinishMapLayers();
        AddEffectMarkers();
        TagSceneItems();
        if (Class(tag) == SEntity.Class)
        {
            try { scene.Attachments = DescribeEntityAttachments(tag); }
            catch (Exception error) when (error is InvalidDataException or ArgumentException
                or OverflowException or IndexOutOfRangeException or IOException or KeyNotFoundException)
            {
                scene.DecodeErrorCount++;
                scene.Diagnostics.Add($"Cannot decode attachment metadata: {error.Message}");
            }
        }
        foreach (var item in unrenderedPlacements.OrderByDescending(p => p.Value))
        {
            scene.Diagnostics.Add($"Unrendered placement resource class 0x{item.Key:X8}: {item.Value} placements shown as points.");
            scene.UnsupportedCount += item.Value;
        }
        return scene;
    }

    /** Adds a load diagnostic once per load. */
    private void AddDiagnostic(string message)
    {
        if (diagnosticSet.Add(message)) scene.Diagnostics.Add(message);
    }

    // A tag hash is not an FNV name hash, so the name resolver does not apply to it.
    private string Name(uint tag) => catalog.GetName(tag) ?? $"0x{tag:X8}";
    private uint Class(uint tag) => catalog.TryGetEntry(tag, out var entry) ? entry.Reference : 0;
    private byte[] ReadBytes(uint tag)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!tags.TryGetValue(tag, out var data))
        {
            tags[tag] = data = Source?.Invoke(tag) ?? catalog.Read(tag);
            scene.ReadTagSet.Add(tag);
            if (catalog.TryGetEntry(tag, out var entry))
            {
                if (entry.Type is 8 or 16) CollectInlineNames(tag, data);
                if (entry.Type is 8 or 16 && WorldPlacementUpdate.SnapshotClasses.Contains(entry.Reference))
                    scene.PlacementSources[tag] = data;
            }
        }
        return data;
    }

    private uint Resolve(uint narrow, ulong wide) => narrow == uint.MaxValue
        ? catalog.Resolve64(wide) ?? throw new InvalidDataException($"Wide tag 0x{wide:X16} is not in the package table.")
        : narrow;

    /** An unresolved reference cannot suppress later siblings in the same authored array. */
    private void VisitReference(uint owner, int row, uint narrow, ulong wide, uint bubble, Matrix4x4 transform, int depth)
    {
        uint child;
        try { child = Resolve(narrow, wide); }
        catch (InvalidDataException error)
        {
            scene.DecodeErrorCount++;
            scene.Diagnostics.Add($"Missing reference in {Name(owner)} at 0x{row:X}: {error.Message}");
            return;
        }
        Visit(child, bubble, transform, depth);
    }

    /** Traversal follows only class-specific fields and rejects cyclic resource chains. */
    private void Visit(uint tag, uint bubble, Matrix4x4 transform, int depth)
    {
        cancellation.ThrowIfCancellationRequested();
        if (depth > 24) throw new InvalidDataException("Resource chain exceeds the traversal limit.");
        var key = new SceneVisitKey(tag, bubble, transform, activeEntryIndex);
        if (activeMapVisit is { } parent)
        {
            if (!mapVisitChildren.TryGetValue(parent, out var children))
                mapVisitChildren[parent] = children = [];
            children.Add(key);
        }
        if (!visited.Add(key)) return;
        if (!catalog.TryGetEntry(tag, out var entry))
        {
            scene.DecodeErrorCount++;
            scene.Diagnostics.Add($"Missing tag 0x{tag:X8}.");
            return;
        }
        Progress?.Invoke($"Reading {Name(tag)}");
        var previousVisit = activeMapVisit;
        activeMapVisit = key;
        var firstInstance = scene.Instances.Count;
        var firstMarker = scene.Markers.Count;
        try
        {
            var data = ReadBytes(tag);
            switch (entry.Reference)
            {
                case SMapRoot.Class:
                {
                    var entries = SceneReads.Rows<SMapRoot, MapBubbleRef>(data, 0, nameof(SMapRoot.MapEntries), MapBubbleRef.Class);
                    foreach (ref readonly var row in entries)
                        VisitReference(tag, TagView.OffsetOf(data, in row), row.NarrowTag, row.WideTag, 0, transform, depth + 1);
                    break;
                }
                case SScenario.Class:
                    VisitScenario(tag, transform, depth);
                    break;
                case SScenarioStateEntry.Class:
                    Visit(TagView.Member<SScenarioStateEntry, uint>(data, 0, nameof(SScenarioStateEntry.Link)),
                        bubble, transform, depth + 1);
                    break;
                case SScenarioTagSet.Class:
                    foreach (var set in new[] { nameof(SScenarioTagSet.TagSetA), nameof(SScenarioTagSet.TagSetB), nameof(SScenarioTagSet.TagSetC) })
                        foreach (ref readonly var row in SceneReads.Rows<SScenarioTagSet, RegistryObjectRef>(data, 0, set, RegistryObjectRef.Class))
                            Visit(row.Tag, bubble, transform, depth + 1);
                    break;
                case SAuthoredObject.Class:
                    VisitAuthoredObject(tag, data, bubble, transform, depth);
                    break;
                case SBubbleParent.Class:
                {
                    bubble = tag;
                    var nameHash = TagView.Member<SBubbleParent, uint>(data, 0, "Metadata.BubbleNameHash");
                    var authored = NameResolver?.Invoke(nameHash) ?? Name(tag);
                    scene.Bubbles.Add(new BubbleInfo(tag, nameHash, GameNames?.BubbleTitle(nameHash, authored) ?? authored));
                    // A bubble outside the selection is listed but its content is not decoded.
                    if (options.BubbleNameHashes is null || options.BubbleNameHashes.Contains(nameHash))
                        Visit(TagView.Member<SBubbleParent, uint>(data, 0, nameof(SBubbleParent.BubbleLink)), bubble, transform, depth + 1);
                    break;
                }
                case SBubbleDefinition.Class:
                {
                    var children = SceneReads.Rows<SBubbleDefinition, SBubbleChildRef>(data, 0, nameof(SBubbleDefinition.BubbleChildren),
                        SBubbleChildRef.Class);
                    foreach (ref readonly var row in children)
                        VisitReference(tag, TagView.OffsetOf(data, in row), row.NarrowTag, row.WideTag, bubble, transform, depth + 1);
                    break;
                }
                case SMapContainer.Class:
                    VisitMapContainer(tag, data, bubble, transform, depth);
                    break;
                case SMapPlacements.Class:
                    Placements(tag, data, bubble, transform, depth);
                    break;
                case SMapResourceLink.Class:
                    Visit(TagView.Member<SMapResourceLink, uint>(data, 0, nameof(SMapResourceLink.InstanceTable)),
                        bubble, transform, depth + 1);
                    break;
                case SStaticMeshInstances.Class:
                    Instances(tag, data, bubble, transform);
                    break;
                case SLightCollection.Class:
                    scene.LightTables.Add(new PlacedLightTable(bubble, tag, -1, -1,
                        transform, ReadLightTable(tag, cancellation), null));
                    break;
                case SStaticMesh.Class:
                    var staticPlacement = new SceneObjectPlacement(tag, -1, transform);
                    foreach (var mesh in StaticMesh(tag))
                        scene.Instances.Add(new MeshInstance { Mesh = mesh, Transform = transform,
                            ObjectPlacement = staticPlacement, Name = Name(tag), BubbleHash = bubble });
                    break;
                case STerrain.Class:
                    var terrainPlacement = new SceneObjectPlacement(tag, -1, transform);
                    foreach (var mesh in TerrainMesh(tag))
                        scene.Instances.Add(new MeshInstance
                        {
                            Mesh = mesh,
                            ObjectPlacement = terrainPlacement,
                            Transform = transform,
                            Name = Name(tag),
                            BubbleHash = bubble,
                            Layer = SceneLayer.Terrain,
                            NativeInstanceData = terrainResource
                        });
                    break;
                case SEntityModel.Class:
                    var modelPlacement = new SceneObjectPlacement(tag, -1, transform);
                    foreach (var mesh in EntityMesh(tag))
                        scene.Instances.Add(new MeshInstance
                        {
                            Mesh = mesh,
                            ObjectPlacement = modelPlacement,
                            Transform = transform,
                            Name = Name(tag),
                            BubbleHash = bubble,
                            Layer = SceneLayer.Objects,
                            InitiallyVisible = mesh.NeverDrawn ? false : null,
                            InitialVisibilityReason = mesh.NeverDrawn ? MeshData.HiddenPartReason : null
                        });
                    AddDiagnostic("Entity mesh uses its bind pose; animation and material variants are not evaluated.");
                    break;
                case SEntity.Class:
                    ClassMeshes(tag, bubble, transform);
                    break;
                case SEntityLink.Class:
                    Visit(TagView.Member<SEntityLink, uint>(data, 0, nameof(SEntityLink.Next)), bubble, transform, depth + 1);
                    break;
                case SEntityConfig.Class:
                    ReadConfigVolumes(tag, bubble, Name(tag));
                    ReadConfigPoints(tag, data, bubble);
                    ReadSquadConfig(tag, data, bubble);
                    break;
                case SSkyObjects.Class:
                    SkyEntities(tag, data, bubble, transform);
                    break;
                case SSpawnPoints.Class:
                    SpawnPoints(tag, data, bubble);
                    break;
                case SAuthoredObjectSet.Class:
                {
                    var direct = TagView.Member<SAuthoredObjectSet, uint>(data, 0, nameof(SAuthoredObjectSet.DirectTarget));
                    if (direct != uint.MaxValue)
                        Visit(direct, bubble, transform, depth + 1);
                    else
                        foreach (ref readonly var row in SceneReads.Rows<SAuthoredObjectSet, SAuthoredObjectSetTarget>(data, 0,
                                     nameof(SAuthoredObjectSet.Objects), SAuthoredObjectSetTarget.Class))
                            Visit(row.Redirect, bubble, transform, depth + 1);
                    break;
                }
                default:
                    scene.UnsupportedCount++;
                    scene.Diagnostics.Add($"Unsupported class 0x{entry.Reference:X8}: {Name(tag)}.");
                    break;
            }
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException
                                    or IndexOutOfRangeException or IOException or KeyNotFoundException)
        {
            scene.DecodeErrorCount++;
            scene.Diagnostics.Add($"Cannot decode {Name(tag)}: {error.Message}");
        }
        finally
        {
            mapVisitContent[key] = (firstInstance, scene.Instances.Count, firstMarker, scene.Markers.Count);
            activeMapVisit = previousVisit;
        }
    }

    /** Quaternion normalization matches the native row-vector placement convention. */
    private static Matrix4x4 Placement(Quaternion q, Vector3 position, float scale)
    {
        if (!Finite(position) || !float.IsFinite(scale) || !float.IsFinite(q.LengthSquared()))
            throw new InvalidDataException("Placement contains non-finite values.");
        q = q.LengthSquared() > 0.0001f ? Quaternion.Normalize(q) : Quaternion.Identity;
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(position);
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /** The visit's transform applies after the instance row's own placement. */
    internal static Matrix4x4 InstanceTransform(byte[] data, int row, Matrix4x4 parent)
    {
        ref readonly var instance = ref TagView.At<SStaticInstanceTransform>(data, row);
        return Placement(instance.Rotation, instance.Position, instance.Scale) * parent;
    }

    /** The visit's transform applies after the placement row's own placement. */
    internal static Matrix4x4 RowTransform(byte[] data, int row, Matrix4x4 parent) => EntryPlacement(data, row) * parent;

    /** Reads only the row's QTS, so a row cut short after it still places. */
    internal static Matrix4x4 EntryPlacement(byte[] data, int row)
    {
        var rotation = TagView.Member<AuthoredPlacementEntry, Quaternion>(data, row, nameof(AuthoredPlacementEntry.Rotation));
        var position = TagView.Member<AuthoredPlacementEntry, Vector3>(data, row, nameof(AuthoredPlacementEntry.Position));
        var scale = TagView.Member<AuthoredPlacementEntry, float>(data, row, nameof(AuthoredPlacementEntry.UniformScale));
        return Placement(rotation, position, scale);
    }

    /** Offset and class of a placement row's payload; both are zero without one. */
    internal static (int Offset, uint Class) PlacementResource(byte[] data, int row)
    {
        ref readonly var payload = ref TagView.At<AuthoredPlacementEntry>(data, row).Payload;
        var resource = TagView.TargetOffset(data, in payload) ?? 0;
        return (resource, resource > TagView.ClassWordSize ? TagView.TargetClass(data, in payload) ?? 0 : 0);
    }

    /** A row whose resource names decoded content visits it; any other row becomes a marker. */
    internal bool VisitsChild(byte[] data, int resource, uint resourceClass) => resourceClass switch
    {
        SStaticInstancesPayload.Class => TagView.FitsThrough<SStaticInstancesPayload>(data, resource,
            nameof(SStaticInstancesPayload.StaticOwner)),
        SSpawnPointsPayload.Class => TagView.FitsThrough<SSpawnPointsPayload>(data, resource, nameof(SSpawnPointsPayload.SpawnPoints)),
        SSkyPayload.Class => TagView.FitsThrough<SSkyPayload>(data, resource, nameof(SSkyPayload.SkyObjects)),
        STerrainPayload.Class => TagView.Fits<STerrainPayload>(data, resource),
        // A placed model with a physics shape at +0x40; Farm and other maps place their water this way.
        UnknownPlacedModelPayload.Class => TagView.FitsThrough<UnknownPlacedModelPayload>(data, resource,
                nameof(UnknownPlacedModelPayload.Model))
            && Class(TagView.Member<UnknownPlacedModelPayload, uint>(data, resource, nameof(UnknownPlacedModelPayload.Model)))
                == SEntityModel.Class,
        _ => false
    };

    /** The child tag of a spawn, sky or placed-model payload that VisitsChild accepted. */
    private static uint PayloadChild(byte[] data, int resource, uint resourceClass) => resourceClass switch
    {
        SSpawnPointsPayload.Class => TagView.Member<SSpawnPointsPayload, uint>(data, resource, nameof(SSpawnPointsPayload.SpawnPoints)),
        SSkyPayload.Class => TagView.Member<SSkyPayload, uint>(data, resource, nameof(SSkyPayload.SkyObjects)),
        UnknownPlacedModelPayload.Class => TagView.Member<UnknownPlacedModelPayload, uint>(data, resource,
            nameof(UnknownPlacedModelPayload.Model)),
        _ => throw new InvalidDataException($"Payload class 0x{resourceClass:X8} names no child.")
    };

    /** Spawn rows hold world coordinates. */
    internal static (Vector3 Position, Quaternion Rotation) SpawnPose(byte[] data, int row)
    {
        ref readonly var spawn = ref TagView.At<SSpawnPointRow>(data, row);
        return (spawn.Position, spawn.Rotation);
    }

    /** Ranges select exact resource and transform rows; geometry stays shared between instances. */
    private void Instances(uint tag, byte[] data, uint bubble, Matrix4x4 parent)
    {
        var transforms = SceneReads.Rows<SStaticMeshInstances, SStaticInstanceTransform>(data, 0, "Instances.Transforms",
            SStaticInstanceTransform.Class);
        var resources = SceneReads.Rows<SStaticMeshInstances, SStaticInstanceResource>(data, 0, "Instances.Resources",
            SStaticInstanceResource.Class);
        var ranges = SceneReads.Rows<SStaticMeshInstances, SStaticMeshInstanceGroup>(data, 0, "Instances.Ranges",
            SStaticMeshInstanceGroup.Class);
        var inRegion = InRegion(data, transforms.Length, parent);
        var placements = new Dictionary<int, SceneObjectPlacement>();
        foreach (ref readonly var range in ranges)
        {
            cancellation.ThrowIfCancellationRequested();
            var count = range.Count;
            var first = range.FirstInstance;
            var resourceIndex = range.ResourceIndex;
            if (resourceIndex >= resources.Length || first + count > transforms.Length)
                throw new InvalidDataException("Instance range leaves its typed array.");
            if (inRegion is not null && !inRegion.AsSpan(first, count).Contains(true)) continue;
            var resource = resources[resourceIndex].StaticMesh;
            if (Class(resource) != SStaticMesh.Class)
            {
                scene.UnsupportedCount++;
                scene.Diagnostics.Add($"Unsupported instance resource {Name(resource)}.");
                continue;
            }
            MeshData[] parts;
            try { parts = StaticMesh(resource); }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException or IOException)
            {
                scene.DecodeErrorCount++;
                scene.Diagnostics.Add($"Cannot decode mesh {Name(resource)}: {error.Message}");
                continue;
            }
            for (var i = (int)first; i < first + count; ++i)
            {
                if (inRegion is not null && !inRegion[i] || ReplacedInstance(tag, i)) continue;
                var origin = InstanceOrigin(tag, i);
                var transformRow = TagView.OffsetOf(data, in transforms[i]);
                var transform = InstanceTransform(data, transformRow, parent);
                if (!placements.TryGetValue(i, out var objectPlacement))
                    placements[i] = objectPlacement = new SceneObjectPlacement(origin.Tag, origin.Index, transform);
                foreach (var part in parts)
                    scene.Instances.Add(new MeshInstance
                    {
                        Mesh = part,
                        ObjectPlacement = objectPlacement,
                        Transform = transform,
                        InstanceParent = parent,
                        Name = Name(resource),
                        BubbleHash = bubble,
                        InstanceSourceTag = origin.Tag,
                        InstanceSourceIndex = origin.Index,
                        PlacementSourceTag = staticPlacement?.SourceTag ?? 0,
                        PlacementSourceOffset = staticPlacement?.SourceOffset ?? -1,
                        NativeInstanceData = data.AsMemory(transformRow, Unsafe.SizeOf<SStaticInstanceTransform>())
                    });
            }
        }
    }

    /**
     * Which rows of an instance table meet the region, by their bounds rows in table space.
     * @return Null when there is no region, or when the bounds table does not match its rows.
     */
    private bool[]? InRegion(byte[] table, int count, Matrix4x4 parent)
    {
        if (Region is not { } region) return null;
        ReadOnlySpan<SInstanceBoundsRow> boxes;
        try
        {
            var bounds = ReadBytes(TagView.Member<SStaticMeshInstances, uint>(table, 0, nameof(SStaticMeshInstances.BoundsTable)));
            boxes = SceneReads.Rows<SInstanceBoundsTable, SInstanceBoundsRow>(bounds, 0, nameof(SInstanceBoundsTable.Boxes),
                SInstanceBoundsRow.Class);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or KeyNotFoundException) { return null; }
        if (boxes.Length != count) return null;
        var result = new bool[count];
        for (int i = 0; i < count; i++)
        {
            ref readonly var box = ref boxes[i];
            Vector3 min = new(box.MinimumW1.X, box.MinimumW1.Y, box.MinimumW1.Z);
            Vector3 max = new(box.MaximumW1.X, box.MaximumW1.Y, box.MaximumW1.Z), low = new(float.PositiveInfinity), high = -low;
            for (int corner = 0; corner < 8; corner++)
            {
                var point = Vector3.Transform(new Vector3((corner & 1) == 0 ? min.X : max.X,
                    (corner & 2) == 0 ? min.Y : max.Y, (corner & 4) == 0 ? min.Z : max.Z), parent);
                low = Vector3.Min(low, point); high = Vector3.Max(high, point);
            }
            // A box that is not finite is kept, so the filter never drops geometry it cannot place.
            result[i] = !(low.X > region.Max.X || low.Y > region.Max.Y || low.Z > region.Max.Z
                || high.X < region.Min.X || high.Y < region.Min.Y || high.Z < region.Min.Z);
        }
        return result;
    }

    /** Placement resources retain their authored transform and their exact source identity. */
    private void Placements(uint tag, byte[] data, uint bubble, Matrix4x4 parent, int depth)
    {
        var ordinal = 0;
        foreach (ref readonly var entry in SceneReads.Rows<SMapPlacements, AuthoredPlacementEntry>(data, 0,
                     nameof(SMapPlacements.Placements), AuthoredPlacementEntry.Class))
        {
            cancellation.ThrowIfCancellationRequested();
            var row = TagView.OffsetOf(data, in entry);
            var transform = RowTransform(data, row, parent);
            var classTag = entry.ClassTag;
            var nameHash = entry.NameHash;
            authoredPlacements.Add(new PlacementFact(entry.PlacementIdentity, tag, row, classTag,
                bubble, activeEntryIndex, transform.Translation, entry.Rotation, nameHash));
            var className = catalog.GetName(classTag);
            var label = className ?? $"Placement {ordinal}";
            var (resource, resourceClass) = PlacementResource(data, row);
            var occlusion = resourceClass == SAmbientOcclusionPayload.Class
                && TagView.FitsThrough<SAmbientOcclusionPayload>(data, resource, nameof(SAmbientOcclusionPayload.OcclusionMap));
            var lights = resourceClass == SLightsPayload.Class
                && TagView.FitsThrough<SLightsPayload>(data, resource, nameof(SLightsPayload.LightCollection));
            var cubemap = resourceClass == UnknownCubemapPayload.Class;
            var atmosphere = resourceClass == UnknownAtmospherePayload.Class && TagView.Fits<UnknownAtmospherePayload>(data, resource);
            if (atmosphere)
                scene.Atmospheres.Add(new PlacedAtmosphere(bubble, tag, row,
                    data.AsSpan(resource, Unsafe.SizeOf<UnknownAtmospherePayload>()).ToArray()));
            // The time-of-day loader 0x7FF742E05F50 reads these three fields.
            var timeOfDay = resourceClass == UnknownTimeOfDayPayload.Class
                && TagView.FitsThrough<UnknownTimeOfDayPayload>(data, resource, nameof(UnknownTimeOfDayPayload.StartTime));
            if (timeOfDay)
                scene.TimesOfDay.Add(new PlacedTimeOfDay(bubble, tag,
                    TagView.Member<UnknownTimeOfDayPayload, uint>(data, resource, nameof(UnknownTimeOfDayPayload.CurveSet)),
                    TagView.Member<UnknownTimeOfDayPayload, float>(data, resource, nameof(UnknownTimeOfDayPayload.DaySeconds)),
                    TagView.Member<UnknownTimeOfDayPayload, float>(data, resource, nameof(UnknownTimeOfDayPayload.StartTime))));
            if (occlusion)
            {
                var map = TagView.Member<SAmbientOcclusionPayload, uint>(data, resource, nameof(SAmbientOcclusionPayload.OcclusionMap));
                if (Class(map) != SOcclusionMap.Class)
                    throw new InvalidDataException("Placed AO resource has the wrong map class.");
                scene.OcclusionOwners.Add(new NativeOcclusionOwner(bubble, tag, row, map));
            }
            if (VisitsChild(data, resource, resourceClass) && resourceClass == STerrainPayload.Class)
            {
                terrainResource = data.AsMemory(resource, Unsafe.SizeOf<STerrainPayload>());
                try { Visit(TagView.At<STerrainPayload>(data, resource).Terrain, bubble, transform, depth + 1); }
                finally { terrainResource = default; }
            }
            else if (VisitsChild(data, resource, resourceClass) && resourceClass == SStaticInstancesPayload.Class)
                VisitStaticPlacement(tag, row, ordinal,
                    TagView.Member<SStaticInstancesPayload, uint>(data, resource, nameof(SStaticInstancesPayload.StaticOwner)),
                    bubble, transform, depth + 1);
            else if (VisitsChild(data, resource, resourceClass))
                Visit(PayloadChild(data, resource, resourceClass), bubble, transform, depth + 1);
            else if (resourceClass == UnknownDecoratorPlacementPayload.Class && TagView.FitsThrough<UnknownDecoratorPlacementPayload>(data, resource,
                         nameof(UnknownDecoratorPlacementPayload.DecoratorPayload))
                     && TagView.Member<UnknownDecoratorPlacementPayload, uint>(data, resource,
                         nameof(UnknownDecoratorPlacementPayload.DecoratorPayload)) is var decorator
                     && Class(decorator) == DecoratorTags.PayloadClass && TryDecorators(decorator, bubble, transform)) { }
            else
            {
                if (!occlusion && !lights && !cubemap && !atmosphere && !timeOfDay)
                    unrenderedPlacements[resourceClass] = unrenderedPlacements.GetValueOrDefault(resourceClass) + 1;
                var marker = new SceneMarker
                {
                    ObjectPlacement = new SceneObjectPlacement(tag, row, transform) { EntryIndex = ordinal },
                    PlacementParent = parent,
                    Name = label,
                    NameHash = nameHash,
                    NameEvidence = className != null ? SceneNameEvidence.PackageClass : SceneNameEvidence.Unknown,
                    NameSourceTag = className != null ? classTag : 0,
                    SourceTag = tag,
                    SourceOffset = row,
                    PlacementIdentity = entry.PlacementIdentity,
                    Semantic = occlusion ? "ambient occlusion resource"
                        : lights ? "chunked lights resource" : atmosphere ? "atmosphere resource"
                        : timeOfDay ? "time-of-day resource" : cubemap ? "cubemap resource"
                        : "authored placement",
                    TagHash = classTag,
                    BubbleHash = bubble,
                    Layer = SceneLayer.Objects,
                    Position = transform.Translation,
                    Extents = new Vector3(0.25f),
                    Rotation = entry.Rotation,
                    Color = new Vector4(0.8f, 0.75f, 0.2f, 1)
                };
                scene.Markers.Add(marker);
                if (lights)
                    ReadPlacedLights(TagView.Member<SLightsPayload, uint>(data, resource, nameof(SLightsPayload.LightCollection)),
                        marker, resource, transform);
                if (cubemap)
                    ReadPlacedCubemap(tag, resource, transform, marker);
                QueuePlacedClassModels(marker, transform);
                ReadClassVolumes(classTag, bubble, label);
            }
            ordinal++;
        }
    }

    /** Spawn positions use the package's world coordinates. */
    private void SpawnPoints(uint tag, byte[] data, uint bubble)
    {
        foreach (ref readonly var point in SceneReads.Rows<SSpawnPoints, SSpawnPointRow>(data, 0, nameof(SSpawnPoints.SpawnPoints),
                     SSpawnPointRow.Class))
        {
            var row = TagView.OffsetOf(data, in point);
            var hash = point.SpawnNameIdentity;
            var pose = SpawnPose(data, row);
            scene.Markers.Add(new SceneMarker
            {
                Name = NameResolver?.Invoke(hash) ?? (hash == 0x2EA8FB98 ? "default" : $"Spawn 0x{hash:X8}"),
                NameHash = hash,
                SourceTag = tag,
                SourceOffset = row,
                Semantic = "spawn point",
                TagHash = tag,
                BubbleHash = bubble,
                Layer = SceneLayer.Spawns,
                Position = pose.Position,
                Rotation = pose.Rotation,
                Extents = new Vector3(0.35f),
                Color = new Vector4(0.2f, 1, 0.4f, 1)
            });
        }
    }
}

/** Scene array reads with the scene readers' checks and messages. */
internal static class SceneReads
{
    /** @return The rows of the array `member` of the TOwner at `owner`; Rows(tag, field, class) checks them. */
    public static ReadOnlySpan<TRow> Rows<TOwner, TRow>(ReadOnlySpan<byte> tag, int owner, string member, uint? elementClass)
        where TOwner : unmanaged where TRow : unmanaged =>
        Rows<TRow>(tag, owner + TagView.FieldOffset<TOwner>(member), elementClass);

    /**
     * Rows of the array field at `field`. A zero count with no header is empty; otherwise the count, the
     * header, the element class and the row extent must all hold.
     * @param elementClass Null accepts any class.
     */
    public static ReadOnlySpan<TRow> Rows<TRow>(ReadOnlySpan<byte> tag, int field, uint? elementClass) where TRow : unmanaged
    {
        var array = new TagArray(tag, field);
        long count = array.Count;
        if (count == 0 && !array.HasHeader) return [];
        if (count < 0 || count > TagArray.MaximumReaderRows) throw new InvalidDataException("Array count is out of range.");
        int stride = Unsafe.SizeOf<TRow>();
        if (!array.IsWellFormed || elementClass is { } expected && array.ElementClass != expected || !array.RowsFit(stride))
            throw new InvalidDataException($"Array 0x{field:X} does not match class 0x{elementClass ?? 0:X8}.");
        return MemoryMarshal.Cast<byte, TRow>(tag.Slice(array.DataOffset, (int)count * stride));
    }
}
