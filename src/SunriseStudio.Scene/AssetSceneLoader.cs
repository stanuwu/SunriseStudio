using System.Numerics;
using SunriseStudio.Packages;

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
    private readonly Dictionary<uint, TagData> tags = [];
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
        hasMapLayerCatalog = Class(tag) == 0x808091DE;
        if (hasMapLayerCatalog)
            scene.MapLayers.AddRange(DescribeMapLayers(tag));
        ValidateMapSelection();
        if (Class(tag) == 0x80809994)
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
        if (Class(tag) == 0x80809C0F)
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
    private TagData Read(uint tag)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!tags.TryGetValue(tag, out var data))
        {
            tags[tag] = data = new TagData(Source?.Invoke(tag) ?? catalog.Read(tag));
            scene.ReadTagSet.Add(tag);
            if (catalog.TryGetEntry(tag, out var entry))
            {
                if (entry.Type is 8 or 16) CollectInlineNames(tag, data);
                if (entry.Type is 8 or 16 && WorldPlacementUpdate.SnapshotClasses.Contains(entry.Reference))
                    scene.PlacementSources[tag] = data.Bytes;
            }
        }
        return data;
    }

    private uint Resolve(TagData data, int row) => data.U32(row) == uint.MaxValue
        ? catalog.Resolve64(data.U64(row + 8)) ?? throw new InvalidDataException($"Wide tag 0x{data.U64(row + 8):X16} is not in the package table.")
        : data.U32(row);

    /** An unresolved reference cannot suppress later siblings in the same authored array. */
    private void VisitReference(uint owner, TagData data, int row, uint bubble, Matrix4x4 transform, int depth)
    {
        uint child;
        try { child = Resolve(data, row); }
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
            var data = Read(tag);
            switch (entry.Reference)
            {
                case 0x808091DE:
                    foreach (var row in data.Rows(0x10, 0x80807D53, 16))
                        VisitReference(tag, data, row, 0, transform, depth + 1);
                    break;
                case 0x80809994:
                    VisitScenario(tag, transform, depth);
                    break;
                case 0x8080925B:
                    Visit(data.U32(20), bubble, transform, depth + 1);
                    break;
                case 0x8080925E:
                    foreach (var descriptor in new[] { 8, 24, 40 })
                        foreach (var row in data.Rows(descriptor, 0x80809260, 4))
                            Visit(data.U32(row), bubble, transform, depth + 1);
                    break;
                case 0x80809462:
                    VisitAuthoredObject(tag, data, bubble, transform, depth);
                    break;
                case 0x80807DAE:
                    bubble = tag;
                    var nameHash = data.U32(0x18);
                    var authored = NameResolver?.Invoke(nameHash) ?? Name(tag);
                    scene.Bubbles.Add(new BubbleInfo(tag, nameHash, GameNames?.BubbleTitle(nameHash, authored) ?? authored));
                    // A bubble outside the selection is listed but its content is not decoded.
                    if (options.BubbleNameHashes is null || options.BubbleNameHashes.Contains(nameHash))
                        Visit(data.U32(8), bubble, transform, depth + 1);
                    break;
                case 0x808091E0:
                    foreach (var row in data.Rows(8, 0x808084C1, 16))
                        VisitReference(tag, data, row, bubble, transform, depth + 1);
                    break;
                case 0x80808A54:
                    VisitMapContainer(tag, data, bubble, transform, depth);
                    break;
                case 0x808099D6:
                    Placements(tag, data, bubble, transform, depth);
                    break;
                case 0x80806EF4:
                    Visit(data.U32(8), bubble, transform, depth + 1);
                    break;
                case 0x8080966D:
                    Instances(tag, data, bubble, transform);
                    break;
                case 0x8080713A:
                    scene.LightTables.Add(new PlacedLightTable(bubble, tag, -1, -1,
                        transform, ReadLightTable(tag, cancellation), null));
                    break;
                case 0x808071A7:
                    var staticPlacement = new SceneObjectPlacement(tag, -1, transform);
                    foreach (var mesh in StaticMesh(tag))
                        scene.Instances.Add(new MeshInstance { Mesh = mesh, Transform = transform,
                            ObjectPlacement = staticPlacement, Name = Name(tag), BubbleHash = bubble });
                    break;
                case 0x8080714F:
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
                case 0x808073A5:
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
                case 0x80809C0F:
                    ClassMeshes(tag, bubble, transform);
                    break;
                case 0x80809B14:
                    Visit(data.U32(12), bubble, transform, depth + 1);
                    break;
                case 0x80809C36:
                    ReadConfigVolumes(tag, bubble, Name(tag));
                    ReadConfigPoints(tag, data, bubble);
                    ReadSquadConfig(tag, data, bubble);
                    break;
                case 0x80806F95:
                    SkyEntities(tag, data, bubble, transform);
                    break;
                case 0x80809162:
                    SpawnPoints(tag, data, bubble);
                    break;
                case 0x80809468:
                    if (data.U32(8) != uint.MaxValue)
                        Visit(data.U32(8), bubble, transform, depth + 1);
                    else
                        foreach (var row in data.Rows(16, 0x80809B13, 4))
                            Visit(data.U32(row), bubble, transform, depth + 1);
                    break;
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
    internal static Matrix4x4 Placement(TagData data, int offset)
    {
        var q = data.Q4(offset);
        var position = data.V3(offset + 16);
        var scale = data.F32(offset + 28);
        if (!Finite(position) || !float.IsFinite(scale) || !float.IsFinite(q.LengthSquared()))
            throw new InvalidDataException("Placement contains non-finite values.");
        q = q.LengthSquared() > 0.0001f ? Quaternion.Normalize(q) : Quaternion.Identity;
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(position);
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /** A static instance row holds its QTS at +0; the visit's transform applies after it. */
    internal static Matrix4x4 InstanceTransform(TagData data, int row, Matrix4x4 parent) => Placement(data, row) * parent;

    /** A 0x808099D8 row holds its QTS at +0x10; the visit's transform applies after it. */
    internal static Matrix4x4 RowTransform(TagData data, int row, Matrix4x4 parent) => Placement(data, row + 0x10) * parent;

    /** Offset and class of a placement row's +0x78 resource; both are zero without one. */
    internal static (int Offset, uint Class) PlacementResource(TagData data, int row)
    {
        var field = row + 0x78;
        var resource = data.I64(field) != 0 ? data.Relative(field) : 0;
        return (resource, resource > 4 && data.Has(resource - 4, 4) ? data.U32(resource - 4) : 0);
    }

    /** A row whose resource names decoded content visits it; any other row becomes a marker. */
    internal bool VisitsChild(TagData data, int resource, uint resourceClass) => resourceClass switch
    {
        0x808071B3 or 0x80809160 or 0x80806F91 => data.Has(resource, 0x14),
        0x8080714B => data.Has(resource, 0x20),
        // A placed model with a physics shape at +0x40; Farm and other maps place their water this way.
        0x80806DE0 => data.Has(resource, 0x14) && Class(data.U32(resource + 0x10)) == 0x808073A5,
        _ => false
    };

    /** Spawn rows hold world coordinates: rotation at +0, position at +16. */
    internal static (Vector3 Position, Quaternion Rotation) SpawnPose(TagData data, int row) =>
        (data.V3(row + 16), data.Q4(row));

    /** Ranges select exact resource and transform rows; geometry stays shared between instances. */
    private void Instances(uint tag, TagData data, uint bubble, Matrix4x4 parent)
    {
        var transforms = data.Rows(0x40, 0x808071A3, 48);
        var resources = data.Rows(0x58, 0x8080967D, 4);
        var ranges = data.Rows(0x68, 0x80807190, 8);
        var inRegion = InRegion(data, transforms.Length, parent);
        var placements = new Dictionary<int, SceneObjectPlacement>();
        foreach (var row in ranges)
        {
            cancellation.ThrowIfCancellationRequested();
            var count = data.U16(row);
            var first = data.U16(row + 2);
            var resourceIndex = data.U16(row + 4);
            if (resourceIndex >= resources.Length || first + count > transforms.Length)
                throw new InvalidDataException("Instance range leaves its typed array.");
            if (inRegion is not null && !inRegion.AsSpan(first, count).Contains(true)) continue;
            var resource = data.U32(resources[resourceIndex]);
            if (Class(resource) != 0x808071A7)
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
                var transform = InstanceTransform(data, transforms[i], parent);
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
                        NativeInstanceData = data.Bytes.AsMemory(transforms[i], 48)
                    });
            }
        }
    }

    /**
     * Which rows of an instance table meet the region, by their bounds rows in table space.
     * @return Null when there is no region, or when the bounds table does not match its rows.
     */
    private bool[]? InRegion(TagData table, int count, Matrix4x4 parent)
    {
        if (Region is not { } region) return null;
        int[] boxes;
        TagData bounds;
        try
        {
            bounds = Read(table.U32(0x18));
            boxes = bounds.Rows(8, 0x80809673, 48);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or KeyNotFoundException) { return null; }
        if (boxes.Length != count) return null;
        var result = new bool[count];
        for (int i = 0; i < count; i++)
        {
            Vector3 min = bounds.V3(boxes[i]), max = bounds.V3(boxes[i] + 16), low = new(float.PositiveInfinity), high = -low;
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
    private void Placements(uint tag, TagData data, uint bubble, Matrix4x4 parent, int depth)
    {
        var ordinal = 0;
        foreach (var row in data.Rows(8, 0x808099D8, 144))
        {
            cancellation.ThrowIfCancellationRequested();
            var transform = RowTransform(data, row, parent);
            var classTag = data.U32(row);
            var nameHash = data.U32(row + 0x64);
            authoredPlacements.Add(new PlacementFact(data.U64(row + 0x70), tag, row, classTag,
                bubble, activeEntryIndex, transform.Translation, data.Q4(row + 0x10), nameHash));
            var className = catalog.GetName(classTag);
            var label = className ?? $"Placement {ordinal}";
            var (resource, resourceClass) = PlacementResource(data, row);
            var occlusion = resourceClass == 0x80806F38 && data.Has(resource, 0x14);
            var lights = resourceClass == 0x80806F5A && data.Has(resource, 0x14);
            var cubemap = resourceClass == 0x80806B7F;
            // An inline 0x80807246 record names the bubble's 0x80807320 visibility owner at +0x10.
            var visibility = resourceClass == 0x80807246 && data.Has(resource, 0x14);
            // The atmosphere loader 0x7FF742EF7B60 copies resource bytes +0x10 to +0xF0.
            var atmosphere = resourceClass == 0x80807086 && data.Has(resource, 0xF0);
            if (atmosphere)
                scene.Atmospheres.Add(new PlacedAtmosphere(bubble, tag, row, data.Bytes.AsSpan(resource + 0x10, 0xE0).ToArray()));
            // The time-of-day loader 0x7FF742E05F50 reads resource +0x10, +0x14 and +0x18.
            var timeOfDay = resourceClass == 0x80806F61 && data.Has(resource, 0x1C);
            if (timeOfDay)
                scene.TimesOfDay.Add(new PlacedTimeOfDay(bubble, tag, data.U32(resource + 0x10), data.F32(resource + 0x14),
                    data.F32(resource + 0x18)));
            if (occlusion)
            {
                var map = data.U32(resource + 0x10);
                if (Class(map) != 0x80807268)
                    throw new InvalidDataException("Placed AO resource has the wrong map class.");
                scene.OcclusionOwners.Add(new NativeOcclusionOwner(bubble, tag, row, map));
            }
            if (VisitsChild(data, resource, resourceClass) && resourceClass == 0x8080714B)
            {
                terrainResource = data.Bytes.AsMemory(resource, 0x20);
                try { Visit(data.U32(resource + 0x18), bubble, transform, depth + 1); }
                finally { terrainResource = default; }
            }
            else if (VisitsChild(data, resource, resourceClass) && resourceClass == 0x808071B3)
                VisitStaticPlacement(tag, row, ordinal, data.U32(resource + 0x10), bubble, transform, depth + 1);
            else if (VisitsChild(data, resource, resourceClass))
                Visit(data.U32(resource + 0x10), bubble, transform, depth + 1);
            else if (resourceClass == DecoratorResourceClass && data.Has(resource, 0x14)
                && Class(data.U32(resource + 0x10)) == DecoratorPayloadClass && TryDecorators(data.U32(resource + 0x10), bubble, transform)) { }
            else
            {
                if (!occlusion && !lights && !cubemap && !atmosphere && !timeOfDay && !visibility)
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
                    PlacementIdentity = data.U64(row + 0x70),
                    Semantic = occlusion ? "ambient occlusion resource"
                        : lights ? "chunked lights resource" : atmosphere ? "atmosphere resource"
                        : timeOfDay ? "time-of-day resource" : cubemap ? "cubemap resource"
                        : visibility ? "visibility resource" : "authored placement",
                    TagHash = classTag,
                    BubbleHash = bubble,
                    Layer = SceneLayer.Objects,
                    Position = transform.Translation,
                    Extents = new Vector3(0.25f),
                    Rotation = data.Q4(row + 0x10),
                    Color = new Vector4(0.8f, 0.75f, 0.2f, 1)
                };
                scene.Markers.Add(marker);
                if (lights)
                    ReadPlacedLights(data.U32(resource + 0x10), marker, resource, transform);
                if (cubemap)
                    ReadPlacedCubemap(tag, resource, transform, marker);
                if (visibility)
                {
                    try { ReadPlacedVisibility(data.U32(resource + 0x10), tag, row, bubble); }
                    catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException
                        or IndexOutOfRangeException or KeyNotFoundException)
                    {
                        scene.DecodeErrorCount++;
                        scene.Diagnostics.Add($"Cannot decode visibility of {Name(tag)}: {error.Message}");
                    }
                }
                QueuePlacedClassModels(marker, transform);
                ReadClassVolumes(classTag, bubble, label);
            }
            ordinal++;
        }
    }

    /** Spawn positions use the package's world coordinates. */
    private void SpawnPoints(uint tag, TagData data, uint bubble)
    {
        foreach (var row in data.Rows(8, 0x80809164, 48))
        {
            var hash = data.U32(row + 32);
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
