using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

/**
 * Moves loaded objects after an edit that changed only placement fields.
 * New values come from the decoder's own row readers; any other change needs a full reload.
 */
public sealed class WorldPlacementUpdate
{
    private const uint InstanceTable = SStaticMeshInstances.Class, PlacementList = SMapPlacements.Class, Config = SEntityConfig.Class,
        LightTable = SLightCollection.Class, SpawnList = SSpawnPoints.Class, OwnerRecord = SMapResourceLink.Class,
        InstanceBounds = SInstanceBoundsTable.Class, Container = SMapContainer.Class;

    /** Classes whose decoded bytes the world keeps; an edit is diffed against them. */
    internal static readonly IReadOnlySet<uint> SnapshotClasses = new HashSet<uint>
    {
        InstanceTable, PlacementList, Config, LightTable, SpawnList, OwnerRecord, Container,
        DecoratorTags.PayloadClass, DecoratorTags.BlockClass, DecoratorTags.QuantizationClass
    };

    // No viewer path reads these; object edits rewrite them for the game.
    private static readonly IReadOnlySet<uint> ViewerIgnored = new HashSet<uint> { InstanceBounds };

    /** True for entries whose changes a placement update can take; other entries need a reload. */
    public static bool IsPlacementData(PackageEntry entry) =>
        entry.Type is 8 or 16 && SnapshotClasses.Contains(entry.Reference) || IsViewerIgnored(entry);

    /** Entries no viewer path reads: instance bounds, and Havok packfiles, which hold collision. */
    public static bool IsViewerIgnored(PackageEntry entry) =>
        entry.Type == 8 && ViewerIgnored.Contains(entry.Reference) || entry is { Type: 26, SubType: 7 };

    private enum RowKind { Instance, Placement, Slot, Trigger, Light, Spawn, Decorator }

    private readonly WorldDocument world;
    // Every scene of the world, and the decoded ones whose objects the composed scene repeats.
    private readonly SceneDocument[] scenes, decoded;
    private readonly List<Action> writes = [];
    private readonly Dictionary<uint, byte[]> sources = [];
    private readonly HashSet<object> moved = new(ReferenceEqualityComparer.Instance);
    private readonly List<MeshInstance> instances = [];
    private readonly List<SceneMarker> markers = [];
    private ILookup<(uint Tag, int Index), MeshInstance> byInstanceRow = Array.Empty<MeshInstance>().ToLookup(item => (0u, 0));
    private ILookup<(uint Tag, int Offset), MeshInstance> byPlacementRow = Array.Empty<MeshInstance>().ToLookup(item => (0u, 0));
    // Block, quantization and buffer tags of the decorator payloads the world draws; built on first use.
    private Dictionary<uint, DecoratorTags>? decoratorParts;
    private ILookup<(uint Payload, int Row), MeshInstance>? byDecoratorRow;
    private PackageCatalog? catalog;

    private WorldPlacementUpdate(WorldDocument world)
    {
        this.world = world;
        scenes = new[] { world.MapScene, world.MissionScene, world.Scene }.OfType<SceneDocument>()
            .Distinct(ReferenceEqualityComparer.Instance).Cast<SceneDocument>().ToArray();
        decoded = world.MissionScene is { } mission && !ReferenceEquals(mission, world.MapScene)
            ? [world.MapScene, mission] : [world.MapScene];
    }

    /** Instances whose transform changed, including the parts of placed class models. */
    public IReadOnlyList<MeshInstance> Instances => instances;
    public IReadOnlyList<SceneMarker> Markers => markers;
    /** World rows whose position or extents changed; set by Apply. */
    public IReadOnlyList<WorldObject> Rows { get; private set; } = [];
    /** Placement tables whose bytes changed. */
    public IReadOnlyCollection<uint> Tags => sources.Keys;

    /**
     * Computes the update without changing the world. Call where no reader uses the world.
     * @param changedTags Every tag whose read bytes changed since the world was decoded.
     * @param reason Why a full reload is needed; empty on success.
     * @return Null when the change is not placement-only.
     */
    public static WorldPlacementUpdate? Prepare(PackageCatalog catalog, WorldDocument world,
        IEnumerable<uint> changedTags, out string reason)
    {
        var update = new WorldPlacementUpdate(world);
        try { reason = update.Read(catalog, changedTags.Distinct().Order().ToArray()); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException
            or IndexOutOfRangeException or KeyNotFoundException or IOException)
        { reason = error.Message; }
        return reason.Length == 0 ? update : null;
    }

    /** Writes the new values into objects and rows; call on the thread that owns the world. */
    public void Apply()
    {
        foreach (var write in writes) write();
        foreach (var scene in scenes)
            foreach (var (tag, bytes) in sources)
                if (scene.PlacementSources.ContainsKey(tag)) scene.PlacementSources[tag] = bytes;
        Rows = world.RefreshPlacements(moved);
    }

    private string Read(PackageCatalog catalog, uint[] changedTags)
    {
        this.catalog = catalog;
        var loader = new AssetSceneLoader(catalog);
        var tags = changedTags.ToHashSet();
        var candidates = SourceInstances().Where(item => tags.Contains(item.InstanceSourceTag) || tags.Contains(item.PlacementSourceTag)).ToArray();
        byInstanceRow = candidates.Where(item => item.InstanceSourceTag != 0)
            .ToLookup(item => (item.InstanceSourceTag, item.InstanceSourceIndex));
        byPlacementRow = candidates.Where(item => item.PlacementSourceTag != 0)
            .ToLookup(item => (item.PlacementSourceTag, item.PlacementSourceOffset));
        var generated = catalog.GetOverlayEntries().Where(row => row.Generated).ToDictionary(row => row.Entry.TagHash);
        foreach (uint tag in changedTags)
        {
            // A removed generated entry only drew rows that its source table draws again.
            if (!catalog.TryGetEntry(tag, out var entry))
            {
                if (IsGeneratedTag(tag)) continue;
                return $"Asset {tag:X8} is not installed.";
            }
            if (IsViewerIgnored(entry)) continue;
            // The viewer draws decorators from their block records, never from their vertex buffer.
            if (entry.Type == 40 && DecoratorPart(catalog, tag) is { } bufferOwner && tag == bufferOwner.Buffer) continue;
            if (entry.Type is not (8 or 16) || !SnapshotClasses.Contains(entry.Reference)) return $"Asset {tag:X8} is not placement data.";
            if (entry.Reference == DecoratorTags.QuantizationClass)
            {
                if (QuantizationChange(catalog, tag) is { Length: > 0 } refused) return refused;
                continue;
            }
            // Generated rows copy edited source rows; the source row moves the instance they draw.
            if (generated.TryGetValue(tag, out var made))
            {
                if (made.Entry.Reference == PlacementList && UnmatchedReplacement(made) is { Length: > 0 } unmatched) return unmatched;
                continue;
            }
            if (Snapshot(tag) is not { } old) continue;
            byte[] bytes = catalog.Read(tag);
            if (entry.Reference == Container)
            {
                if (ContainerChange(catalog, tag, old, bytes, generated) is { Length: > 0 } refused) return refused;
                sources[tag] = bytes;
                continue;
            }
            if (bytes.Length != old.Length) return $"Asset {tag:X8} changed size.";
            var changed = ChangedBytes(old, bytes);
            if (changed.Count == 0) continue;
            var before = old;
            var data = bytes;
            if (!SameScans(tag, entry, before, data)) return $"Asset {tag:X8} changed decoded structure.";
            var rows = PlacementRanges(tag, entry.Reference, data);
            var touched = new SortedSet<(RowKind Kind, int Row)>();
            foreach (int offset in changed)
            {
                if (Covering(rows, offset) is { } row) touched.Add(row);
                else if (!Ignored(entry.Reference, offset)) return $"Asset {tag:X8} changed a field that is not a placement.";
            }
            sources[tag] = bytes;
            foreach (var (kind, row) in touched)
                if (Move(loader, tag, kind, row, data) is { Length: > 0 } refused) return refused;
        }
        return "";
    }

    private byte[]? Snapshot(uint tag)
    {
        foreach (var scene in scenes)
            if (scene.PlacementSources.TryGetValue(tag, out var bytes)) return bytes;
        return null;
    }

    /** Generated entries take tags in the new package range, so a removed one still reads as generated. */
    private static bool IsGeneratedTag(uint tag) =>
        TagHashes.IsTag(tag) && PackageWriter.IsNewPackageId((int)TagHashes.Package(tag));

    /**
     * Accepts a container whose only change adds or drops generated placement lists.
     * An added list must replace draws the world already holds, so the composed scene stays the same.
     * @return Why the change needs a reload; empty when it is accepted.
     */
    private string ContainerChange(PackageCatalog catalog, uint tag, byte[] old, byte[] bytes,
        IReadOnlyDictionary<uint, PackageOverlayEntry> generated)
    {
        // The member array is rewritten at the end; the installed body and other header fields stay.
        int body = Math.Min(catalog.ReadOriginal(tag).Length, Math.Min(old.Length, bytes.Length));
        int header = Unsafe.SizeOf<SMapContainer>();
        if (body < header
            || !((ReadOnlySpan<byte>)TagView.At<SMapContainer>(old).BubbleMask).SequenceEqual(TagView.At<SMapContainer>(bytes).BubbleMask)
            || !old.AsSpan(header, body - header).SequenceEqual(bytes.AsSpan(header, body - header)))
            return $"Asset {tag:X8} changed more than its generated members.";
        uint[] before = Members(old), after = Members(bytes);
        if (!before.Where(member => !IsGeneratedTag(member)).SequenceEqual(after.Where(member => !IsGeneratedTag(member))))
            return $"Asset {tag:X8} changed its installed members.";
        foreach (uint added in after.Except(before))
        {
            if (!generated.TryGetValue(added, out var list) || list.Entry.Reference != PlacementList || list.Replaces.Count == 0)
                return $"Asset {tag:X8} gained a member that is not a generated placement list.";
            if (UnmatchedReplacement(list) is { Length: > 0 } unmatched) return unmatched;
        }
        return "";

        static uint[] Members(byte[] value)
        {
            ref readonly var container = ref TagView.Checked<SMapContainer>(value, 0);
            var rows = TagView.Rows(value, in container.Contents, ContainerMember.Class);
            var members = new uint[rows.Length];
            for (int i = 0; i < rows.Length; i++) members[i] = rows[i].MemberTag;
            return members;
        }
    }

    /** A generated list may only take over draws the world already holds. */
    private string UnmatchedReplacement(PackageOverlayEntry list)
    {
        foreach (var replaced in list.Replaces)
            if (!SourceInstances().Any(item => item.InstanceSourceTag == replaced.SourceTag
                && item.InstanceSourceIndex == replaced.SourceIndex && item.PlacementSourceTag == replaced.PlacementSourceTag
                && item.PlacementSourceOffset == replaced.PlacementSourceOffset))
                return $"Asset {list.Entry.TagHash:X8} replaces an instance the world does not draw.";
        return "";
    }

    /** Offsets of every byte that differs. */
    private static List<int> ChangedBytes(byte[] before, byte[] after)
    {
        var result = new List<int>();
        for (int offset = 0; offset < before.Length;)
        {
            offset += before.AsSpan(offset).CommonPrefixLength(after.AsSpan(offset));
            for (; offset < before.Length && before[offset] != after[offset]; offset++) result.Add(offset);
        }
        return result;
    }

    /** A word scan can find structure in any field, so its results must not change. */
    private static bool SameScans(uint tag, PackageEntry entry, byte[] before, byte[] after)
    {
        if (entry.Type is 8 or 16 && !AssetSceneLoader.InlineNames(before).SequenceEqual(AssetSceneLoader.InlineNames(after)))
            return false;
        if (entry.Reference != Config) return true;
        return AssetSceneLoader.DescriptorCandidates(tag, before).SequenceEqual(AssetSceneLoader.DescriptorCandidates(tag, after))
            && AssetSceneLoader.TriggerLinkOffsets(before).SequenceEqual(AssetSceneLoader.TriggerLinkOffsets(after))
            && AssetSceneLoader.TriggerLinkOffsets(after).All(offset => SameClientRef(before, after,
                offset + TagBlock.RecordHeaderSize));
    }

    private static bool SameClientRef(byte[] before, byte[] after, int at)
    {
        ref readonly var left = ref TagView.At<ClientRef>(before, at);
        ref readonly var right = ref TagView.At<ClientRef>(after, at);
        return left.TargetKey == right.TargetKey && left.TargetSlotType == right.TargetSlotType
            && left.TargetSlotIndex == right.TargetSlotIndex;
    }

    /** Byte ranges each decoded row reads for its placement, sorted by start. */
    private List<(int Start, int End, RowKind Kind, int Row)> PlacementRanges(uint tag, uint type, byte[] data)
    {
        var result = new List<(int, int, RowKind, int)>();
        // Each range runs from the first placement field to the end of the last one.
        switch (type)
        {
            case InstanceTable:
            {
                ref readonly var table = ref TagView.Checked<SStaticMeshInstances>(data, 0);
                var instanceRows = TagView.Rows(data, in table.Instances.Transforms, SStaticInstanceTransform.Class);
                for (int index = 0; index < instanceRows.Length; index++)
                    result.Add((TagView.OffsetOf(data, in instanceRows[index].Rotation),
                        TagView.OffsetOf(data, in instanceRows[index].Scale) + sizeof(float), RowKind.Instance, index));
                break;
            }
            case PlacementList:
            {
                ref readonly var list = ref TagView.Checked<SMapPlacements>(data, 0);
                foreach (ref readonly var row in TagView.Rows(data, in list.Placements, AuthoredPlacementEntry.Class))
                    result.Add((TagView.OffsetOf(data, in row.Rotation), TagView.OffsetOf(data, in row.UniformScale) + sizeof(float),
                        RowKind.Placement, TagView.OffsetOf(data, in row)));
                break;
            }
            case LightTable:
            {
                ref readonly var table = ref TagView.Checked<SLightCollection>(data, 0);
                var lightRows = TagView.Rows(data, in table.LightTransforms, SAnimationControlDefault.Class);
                for (int index = 0; index < lightRows.Length; index++)
                    result.Add((TagView.OffsetOf(data, in lightRows[index].Rotation),
                        TagView.OffsetOf(data, in lightRows[index].Scale) + sizeof(float), RowKind.Light, index));
                break;
            }
            case SpawnList:
            {
                ref readonly var list = ref TagView.Checked<SSpawnPoints>(data, 0);
                foreach (ref readonly var row in TagView.Rows(data, in list.SpawnPoints, SSpawnPointRow.Class))
                    result.Add((TagView.OffsetOf(data, in row.Rotation), TagView.OffsetOf(data, in row.Unk1c) + sizeof(float),
                        RowKind.Spawn, TagView.OffsetOf(data, in row)));
                break;
            }
            case DecoratorTags.BlockClass:
            {
                ref readonly var block = ref TagView.Checked<SDecoratorBlock>(data, 0);
                var records = TagView.Rows(data, in block.Records, DecoratorTags.RecordClass);
                for (int index = 0; index < records.Length; index++)
                {
                    int start = TagView.OffsetOf(data, in records[index]);
                    result.Add((start, start + DecoratorRecords.Size, RowKind.Decorator, index));
                }
                break;
            }
            case Config:
                // Only rows the world decoded have a placement to move.
                foreach (var marker in SourceMarkers().Where(marker => marker.SourceTag == tag).DistinctBy(marker => (marker.Layer, marker.SourceOffset)))
                    if (marker.Layer == SceneLayer.Volumes)
                        result.Add((marker.SourceOffset + TagView.FieldOffset<STriggerVolumeRow>("Sector.BoundsMin"),
                            marker.SourceOffset + TagView.FieldOffset<STriggerVolumeRow>("Sector.BoundsMaxW") + sizeof(float),
                            RowKind.Trigger, marker.SourceOffset));
                    else if (marker is { Layer: SceneLayer.Objects, Slot.Type: 4 })
                        result.Add((marker.SourceOffset + TagView.FieldOffset<AuthoredPlacementEntry>(nameof(AuthoredPlacementEntry.Rotation)),
                            marker.SourceOffset + TagView.FieldOffset<AuthoredPlacementEntry>(nameof(AuthoredPlacementEntry.UniformScale))
                                + sizeof(float),
                            RowKind.Slot, marker.SourceOffset));
                break;
        }
        result.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return result;
    }

    /** Ranges do not overlap; only the last one starting at or before the offset can hold it. */
    private static (RowKind, int)? Covering(List<(int Start, int End, RowKind Kind, int Row)> rows, int offset)
    {
        int low = 0, high = rows.Count - 1, found = -1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (rows[middle].Start <= offset) { found = middle; low = middle + 1; }
            else high = middle - 1;
        }
        return found >= 0 && offset < rows[found].End ? (rows[found].Kind, rows[found].Row) : null;
    }

    /** Bytes an object edit rewrites that no viewer path reads. */
    private static bool Ignored(uint type, int offset) => type switch
    {
        InstanceTable => offset >= AggregateBoxStart && offset < AggregateBoxEnd,
        OwnerRecord => offset == CullingMode,
        DecoratorTags.PayloadClass => offset >= PayloadBoxStart && offset < PayloadBoxEnd,
        _ => false
    };

    // Each table is the tag's root object, so a member offset is its tag offset.
    private static readonly int AggregateBoxStart = TagView.FieldOffset<SStaticMeshInstances>("Instances.AggregateMinimumW1");
    private static readonly int AggregateBoxEnd =
        TagView.FieldOffset<SStaticMeshInstances>("Instances.AggregateMaximumW1") + Unsafe.SizeOf<Vector4>();
    private static readonly int CullingMode = TagView.FieldOffset<SMapResourceLink>(nameof(SMapResourceLink.VisibilityMode0DynamicIds1Map));
    private static readonly int PayloadBoxStart = TagView.FieldOffset<SDecoratorPayload>(nameof(SDecoratorPayload.BoundsMinimumW1));
    private static readonly int PayloadBoxEnd =
        TagView.FieldOffset<SDecoratorPayload>(nameof(SDecoratorPayload.BoundsMaximumW1)) + Unsafe.SizeOf<Vector4>();

    private IEnumerable<SceneMarker> SourceMarkers() => decoded.SelectMany(scene => scene.Markers);
    private IEnumerable<MeshInstance> SourceInstances() => decoded.SelectMany(scene => scene.Instances);
    private IEnumerable<PlacedLightTable> SourceLights() => decoded.SelectMany(scene => scene.LightTables);
    private bool FeedsSquads(uint tag, int row) => scenes.Any(scene => scene.SquadSourceRows.Contains((tag, row)));

    /** @return Why the row cannot move in place; empty when its update was queued. */
    private string Move(AssetSceneLoader loader, uint tag, RowKind kind, int row, byte[] data)
    {
        switch (kind)
        {
            case RowKind.Instance:
                ref readonly var table = ref TagView.Checked<SStaticMeshInstances>(data, 0);
                int offset = TagView.OffsetOf(data,
                    in TagView.Rows(data, in table.Instances.Transforms, SStaticInstanceTransform.Class)[row]);
                foreach (var instance in byInstanceRow[(tag, row)])
                {
                    var transform = AssetSceneLoader.InstanceTransform(data, offset, instance.InstanceParent);
                    var bytes = data.AsMemory(offset, Unsafe.SizeOf<SStaticInstanceTransform>());
                    MoveInstance(instance, transform, bytes);
                }
                return "";
            case RowKind.Placement:
                return MovePlacement(loader, tag, row, data);
            case RowKind.Slot:
                if (FeedsSquads(tag, row)) return "The placement anchors a squad.";
                var (slotPosition, slotRotation) = AssetSceneLoader.SlotPose(data, row);
                var model = AssetSceneLoader.EntryPlacement(data, row);
                foreach (var marker in SourceMarkers().Where(item => item is { Layer: SceneLayer.Objects, Slot.Type: 4 }
                    && item.SourceTag == tag && item.SourceOffset == row))
                    MoveMarker(marker, slotPosition, slotRotation, marker.Extents);
                foreach (var instance in byPlacementRow[(tag, row)])
                    MoveInstance(instance, model, instance.NativeInstanceData);
                return "";
            case RowKind.Trigger:
                var box = AssetSceneLoader.TriggerBox(in TagView.At<STriggerVolumeRow>(data, row));
                foreach (var marker in SourceMarkers().Where(item => item.Layer == SceneLayer.Volumes
                    && item.SourceTag == tag && item.SourceOffset == row))
                    MoveMarker(marker, box.Position, marker.Rotation, box.Extents);
                return "";
            case RowKind.Light:
                ref readonly var lights = ref TagView.Checked<SLightCollection>(data, 0);
                int transformRow = TagView.OffsetOf(data,
                    in TagView.Rows(data, in lights.LightTransforms, SAnimationControlDefault.Class)[row]);
                var pose = AssetSceneLoader.LightPose(data, transformRow);
                foreach (var light in SourceLights().Select(table => table.Table).Where(table => table.TagHash == tag)
                    .Distinct(ReferenceEqualityComparer.Instance).Cast<AuthoredLightTable>().Select(table => table.Rows[row]))
                {
                    if (light.TransformOffset != transformRow) return "The light table layout changed.";
                    moved.Add(light);
                    writes.Add(() =>
                    {
                        light.Rotation = pose.Rotation; light.Translation = pose.Translation;
                        light.Scale = pose.Scale; light.Transform = pose.Transform;
                    });
                }
                return "";
            case RowKind.Spawn:
                var spawn = AssetSceneLoader.SpawnPose(data, row);
                foreach (var marker in SourceMarkers().Where(item => item.Layer == SceneLayer.Spawns
                    && item.SourceTag == tag && item.SourceOffset == row))
                    MoveMarker(marker, spawn.Position, spawn.Rotation, marker.Extents);
                return "";
            case RowKind.Decorator:
                return DecoratorPart(catalog!, tag) is { } owner ? MoveDecorator(owner, row, data)
                    : $"Asset {tag:X8} is not drawn by a decorator payload the world holds.";
        }
        return "Unknown placement row.";
    }

    /** The tags of the drawn decorator payload that owns this block, quantization or buffer. */
    private DecoratorTags? DecoratorPart(PackageCatalog catalog, uint tag)
    {
        if (decoratorParts is null)
        {
            decoratorParts = [];
            var payloads = SourceInstances().Select(item => item.ObjectPlacement?.SourceTag ?? 0).Distinct()
                .Where(payload => catalog.TryGetEntry(payload, out var entry) && entry.Reference == DecoratorTags.PayloadClass);
            foreach (uint payload in payloads)
            {
                var tags = DecoratorTags.Read(catalog, payload);
                foreach (uint part in new[] { tags.Block, tags.Quantization, tags.Buffer }) decoratorParts[part] = tags;
            }
        }
        return decoratorParts.GetValueOrDefault(tag);
    }

    /** New quantization moves every instance of its payload, since each record decodes through it. */
    private string QuantizationChange(PackageCatalog catalog, uint tag)
    {
        byte[] bytes = catalog.Read(tag);
        if (Snapshot(tag) is not { } old || old.AsSpan().SequenceEqual(bytes)) return "";
        if (DecoratorPart(catalog, tag) is not { } owner) return $"Asset {tag:X8} is not drawn by a decorator payload the world holds.";
        sources[tag] = bytes;
        byte[] block = catalog.Read(owner.Block);
        int count = DecoratorRecordRows(block).Length;
        for (int row = 0; row < count; row++) MoveDecorator(owner, row, block);
        return "";
    }

    private static ReadOnlySpan<SDecoratorRecord> DecoratorRecordRows(byte[] block) =>
        TagView.Rows(block, in TagView.Checked<SDecoratorBlock>(block, 0).Records, DecoratorTags.RecordClass);

    /** A record decodes through the payload's current quantization, then the placement that drew it. */
    private string MoveDecorator(DecoratorTags owner, int row, byte[] block)
    {
        byDecoratorRow ??= SourceInstances().Where(item => item.ObjectPlacement is not null)
            .ToLookup(item => (item.ObjectPlacement!.SourceTag, item.ObjectPlacement.SourceIndex));
        var quantization = DecoratorQuantization.Read(catalog!.Read(owner.Quantization));
        var record = block.AsMemory(TagView.OffsetOf(block, in DecoratorRecordRows(block)[row]), DecoratorRecords.Size);
        var local = DecoratorRecords.Decode(record.Span, quantization).Transform;
        foreach (var instance in byDecoratorRow[(owner.Payload, row)])
        {
            var transform = local * instance.InstanceParent;
            MoveInstance(instance, transform, record);
            var identity = instance.ObjectPlacement!;
            if (moved.Add(identity)) writes.Add(() => identity.WorldTransform = transform);
        }
        return "";
    }

    /** A placement row moves its marker, class model, light table and effect markers. */
    private string MovePlacement(AssetSceneLoader loader, uint tag, int row, byte[] data)
    {
        if (FeedsSquads(tag, row)) return "The placement anchors a squad.";
        var (resource, resourceClass) = AssetSceneLoader.PlacementResource(data, row);
        if (loader.VisitsChild(data, resource, resourceClass)) return "The placement moves decoded content below it.";
        // Cubemap resources embed world matrices that the row does not move.
        if (resourceClass == UnknownCubemapPayload.Class) return "The placement is a reflection probe.";
        var rotation = TagView.At<AuthoredPlacementEntry>(data, row).Rotation;
        var lights = SourceLights().ToArray();
        var models = byPlacementRow[(tag, row)].ToArray();
        foreach (var scene in decoded)
        {
            var owners = scene.Markers.Where(item => item.ObjectPlacement is { } placement
                && placement.SourceTag == tag && placement.SourceIndex == row).ToArray();
            var effects = scene.Markers.Where(item => item.Layer == SceneLayer.Effects
                && item.SourceTag == tag && item.SourceOffset == row).ToArray();
            var withEffects = owners.Where(item => item.Semantic == "authored placement").ToArray();
            if (effects.Length != 0 && (withEffects.Length == 0 || effects.Length % withEffects.Length != 0))
                return "The placement's effects cannot be matched to it.";
            foreach (var owner in owners)
            {
                var transform = AssetSceneLoader.RowTransform(data, row, owner.PlacementParent);
                var position = transform.Translation;
                MoveMarker(owner, position, rotation, owner.Extents);
                var identity = owner.ObjectPlacement!;
                writes.Add(() => identity.WorldTransform = transform);
                foreach (var table in lights.Where(table => ReferenceEquals(table.OwnerMarker, owner)))
                    writes.Add(() => table.PlacementTransform = transform);
                foreach (var model in models.Where(item => ReferenceEquals(item.ObjectPlacement, identity)))
                    MoveInstance(model, transform, model.NativeInstanceData);
                int index = Array.IndexOf(withEffects, owner);
                if (index < 0 || effects.Length == 0) continue;
                int count = effects.Length / withEffects.Length;
                foreach (var effect in effects.Skip(index * count).Take(count))
                {
                    if (effect.Effect is not { } description) { MoveMarker(effect, position, rotation, effect.Extents); continue; }
                    var (at, turn) = AssetSceneLoader.EffectPose(description.Local * description.BoneTransform * transform);
                    MoveMarker(effect, at, turn, effect.Extents);
                    writes.Add(() => description.Place(transform));
                }
            }
        }
        if (models.Any(model => !moved.Contains(model))) return "A placed model has no decoded owner.";
        return "";
    }

    private void MoveInstance(MeshInstance instance, Matrix4x4 transform, ReadOnlyMemory<byte> row)
    {
        if (!moved.Add(instance)) return;
        instances.Add(instance);
        var identity = instance.ObjectPlacement;
        writes.Add(() =>
        {
            instance.SetAuthored(transform, row);
            // Rows and class models own their identity; a placement marker moves its own.
            if (identity is not null && (identity.SourceIndex < 0 || instance.InstanceSourceTag == identity.SourceTag))
                identity.WorldTransform = transform;
        });
    }

    private void MoveMarker(SceneMarker marker, Vector3 position, Quaternion rotation, Vector3 extents)
    {
        if (!moved.Add(marker)) return;
        markers.Add(marker);
        writes.Add(() => marker.SetAuthored(position, rotation, extents));
    }
}
