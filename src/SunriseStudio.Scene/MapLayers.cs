using System.Numerics;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

public sealed record MapLayerInfo(int Index, uint NameHash, string Name,
    SceneNameEvidence NameEvidence, uint DefinitionTag, int SourceOffset,
    IReadOnlyList<uint> ContainerTags);

public sealed record MapContainerInfo(uint TagHash, ReadOnlyMemory<byte> Mask,
    IReadOnlyList<int> MapGlobalIndices);

public sealed partial class AssetSceneLoader
{
    private readonly record struct SceneVisitKey(uint Tag, uint Bubble, Matrix4x4 Transform, uint Entry);
    private SceneVisitKey? activeMapVisit;
    private bool hasMapLayerCatalog;
    private readonly Dictionary<SceneVisitKey, HashSet<SceneVisitKey>> mapVisitChildren = [];
    private readonly Dictionary<SceneVisitKey, MapContainerInfo> mapContainerVisits = [];
    private readonly Dictionary<SceneVisitKey, (int FirstInstance, int EndInstance,
        int FirstMarker, int EndMarker)> mapVisitContent = [];

    /** Layer ordinals and name hashes come from the map definition's typed cells array. */
    public IReadOnlyList<MapLayerInfo> DescribeMapLayers(uint mapRootTag)
    {
        var (definitionTag, definition) = MapDefinition(mapRootTag, "Layer metadata requires a map root.");
        ref readonly var map = ref TagView.Checked<SMapLocationNames>(definition, 0);
        var cells = TagView.Rows(definition, in map.Slices, MapCell.Class);
        if (cells.Length > MaximumMapLayers) throw new InvalidDataException("Map layers exceed the container mask.");
        var layers = new MapLayerInfo[cells.Length];
        for (int index = 0; index < cells.Length; index++)
        {
            uint hash = cells[index].LayerNameHash;
            var resolved = ResolveHashName(hash);
            layers[index] = new MapLayerInfo(index, hash, resolved.Name, resolved.Evidence, definitionTag,
                TagView.OffsetOf(definition, in cells[index]), []);
        }
        return layers;
    }

    /** A mission loads each bubble's state layers and every layer in that bubble's row mask. */
    public IReadOnlySet<int> GetScenarioMapIndices(uint mapRootTag, uint scenarioTag)
    {
        var (_, definition) = MapDefinition(mapRootTag, "Scenario layer presets require a map root.");
        ref readonly var map = ref TagView.Checked<SMapLocationNames>(definition, 0);
        var cells = TagView.Rows(definition, in map.Slices, MapCell.Class);
        var bubbles = TagView.Rows(definition, in map.SliceSets, MapBubble.Class);
        var scenario = DescribeScenario(scenarioTag);
        bool sameBubbles = bubbles.Length == scenario.Bubbles.Count;
        for (int index = 0; sameBubbles && index < bubbles.Length; index++) sameBubbles = bubbles[index].BubbleNameHash == scenario.Bubbles[index].NameHash;
        if (!sameBubbles) throw new ArgumentException("Scenario bubble identities do not match the selected map.");
        var indices = new HashSet<int>();
        for (var ordinal = 0; ordinal < bubbles.Length; ++ordinal)
        {
            if (bubbles[ordinal].BubbleOrdinal != ordinal)
                throw new InvalidDataException("Map bubble ordinal does not match its array position.");
            foreach (var state in scenario.Bubbles[ordinal].States)
            {
                var index = checked((int)state.MapBubbleIndex);
                if ((uint)index >= cells.Length || index >= MaximumMapLayers)
                    throw new ArgumentException("Scenario layer index is absent from the selected map.");
                var owner = cells[index].OwnerBubble;
                if (owner == uint.MaxValue)
                {
                    int owners = 0;
                    for (int candidate = 0; candidate < bubbles.Length; candidate++)
                        if (InMask(bubbles[candidate].CellMask, index)) { owners++; owner = (uint)candidate; }
                    if (owners != 1)
                        throw new ArgumentException("Scenario layer has ambiguous map bubble ownership.");
                }
                if (owner != ordinal)
                    throw new ArgumentException("Scenario layer owner does not match the selected map.");
                indices.Add(index);
            }
            // The game loads every container that meets the bubble row's cell mask, which also holds the
            // bubble's shared and environment layers (atmosphere, time of day); 0x7FF7420E2CF0 reads it.
            if (scenario.Bubbles[ordinal].States.Count == 0) continue;
            for (var index = 0; index < cells.Length && index < MaximumMapLayers; ++index)
                if (InMask(bubbles[ordinal].CellMask, index)) indices.Add(index);
        }
        return indices;
    }

    /** A cell mask holds one bit per map layer, the 256 layers a container mask can name. */
    private const int MaximumMapLayers = 256;

    private static bool InMask(in Inline32<byte> mask, int index) => (mask[index / 8] & (1 << (index % 8))) != 0;

    /** @return The definition a map root names, checked to be one. */
    private (uint Tag, byte[] Bytes) MapDefinition(uint mapRootTag, string notRoot)
    {
        if (Class(mapRootTag) != SMapRoot.Class) throw new ArgumentException(notRoot);
        uint definitionTag = TagView.Checked<SMapRoot>(ReadBytes(mapRootTag), 0).MapDefinition;
        if (Class(definitionTag) != SMapLocationNames.Class)
            throw new InvalidDataException("Map layer definition has the wrong class.");
        return (definitionTag, ReadBytes(definitionTag));
    }

    /** Explicit selections use mask intersection; a null selection keeps every container. */
    private void VisitMapContainer(uint tag, byte[] data, uint bubble, Matrix4x4 transform, int depth)
    {
        ref readonly var stored = ref TagView.Checked<SMapContainer>(data, 0);
        byte[] mask = ((ReadOnlySpan<byte>)stored.BubbleMask).ToArray();
        var indices = Enumerable.Range(0, MaximumMapLayers).Where(i => (mask[i / 8] & (1 << (i % 8))) != 0).ToArray();
        var container = scene.MapContainers.FirstOrDefault(c => c.TagHash == tag);
        if (container == null)
        {
            container = new MapContainerInfo(tag, mask, indices);
            scene.MapContainers.Add(container);
        }
        mapContainerVisits[activeMapVisit!.Value] = container;
        var members = TagView.Rows(data, in stored.Contents, ContainerMember.Class);
        if (options.MapGlobalIndices != null && !indices.Any(options.MapGlobalIndices.Contains))
        {
            foreach (var member in members) ReadUnselectedOcclusionOwner(member.MemberTag, bubble);
            return;
        }
        foreach (var member in members)
            Visit(member.MemberTag, bubble, transform, depth + 1);
    }

    /** AO dependencies remain available even when their container's geometry is filtered out. */
    private void ReadUnselectedOcclusionOwner(uint tag, uint bubble)
    {
        if (Class(tag) != SMapPlacements.Class) return;
        byte[] data = ReadBytes(tag);
        ref readonly var placements = ref TagView.Checked<SMapPlacements>(data, 0);
        var rows = TagView.Rows(data, in placements.Placements, AuthoredPlacementEntry.Class);
        for (int index = 0; index < rows.Length; index++)
        {
            ref readonly var row = ref rows[index];
            if (TagView.TargetOffset(data, in row.Payload) is not { } auxiliary
                || !TagView.Fits<SAmbientOcclusionPayload>(data, auxiliary)
                || TagView.TargetClass(data, in row.Payload) != SAmbientOcclusionPayload.Class) continue;
            uint map = TagView.At<SAmbientOcclusionPayload>(data, auxiliary).OcclusionMap;
            if (Class(map) != SOcclusionMap.Class)
                throw new InvalidDataException("Placed AO dependency has the wrong map class.");
            scene.OcclusionOwners.Add(new NativeOcclusionOwner(bubble, tag, TagView.OffsetOf(data, in row), map));
        }
    }

    /** Shared graph nodes retain every container route without duplicating geometry. */
    private void AssignMapContainers()
    {
        foreach (var (root, container) in mapContainerVisits)
        {
            var reached = new HashSet<SceneVisitKey>();
            var pending = new Stack<SceneVisitKey>();
            pending.Push(root);
            MapContainerInfo[] single = [container];
            while (pending.TryPop(out var node))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!reached.Add(node)) continue;
                if (mapVisitContent.TryGetValue(node, out var range))
                {
                    for (var i = range.FirstInstance; i < range.EndInstance; ++i)
                        scene.Instances[i].MapContainers = Add(scene.Instances[i].MapContainers);
                    for (var i = range.FirstMarker; i < range.EndMarker; ++i)
                        scene.Markers[i].MapContainers = Add(scene.Markers[i].MapContainers);
                }
                if (mapVisitChildren.TryGetValue(node, out var children))
                    foreach (var child in children) pending.Push(child);
            }
            IReadOnlyList<MapContainerInfo> Add(IReadOnlyList<MapContainerInfo> previous)
            {
                if (previous.Count == 0) return single;
                return previous.Any(c => c.TagHash == container.TagHash) ? previous
                    : previous.Append(container).ToArray();
            }
        }
    }

    /** Filtering never discards the available layer catalog. */
    private void FinishMapLayers()
    {
        if (!hasMapLayerCatalog)
            foreach (var index in scene.MapContainers.SelectMany(c => c.MapGlobalIndices).Distinct())
                if (scene.MapLayers.All(l => l.Index != index))
                    scene.MapLayers.Add(new MapLayerInfo(index, 0, $"Map layer {index}",
                        SceneNameEvidence.Unknown, 0, -1, []));
        scene.MapLayers.Sort((a, b) => a.Index.CompareTo(b.Index));
        for (var i = 0; i < scene.MapLayers.Count; ++i)
        {
            var layer = scene.MapLayers[i];
            var resolved = layer.NameHash == 0 ? default : ResolveHashName(layer.NameHash);
            scene.MapLayers[i] = layer with
            {
                Name = layer.NameHash == 0 ? layer.Name : resolved.Name,
                NameEvidence = layer.NameHash == 0 ? layer.NameEvidence : resolved.Evidence,
                ContainerTags = scene.MapContainers.Where(c => c.MapGlobalIndices.Contains(layer.Index))
                    .Select(c => c.TagHash).ToArray()
            };
        }
        ValidateMapSelection();
    }

    private void ValidateMapSelection()
    {
        if (options.MapGlobalIndices == null) return;
        foreach (var index in options.MapGlobalIndices)
            if (index < 0 || index >= 256 || scene.MapLayers.Count != 0
                && scene.MapLayers.All(l => l.Index != index))
                throw new ArgumentException($"Map has no authored layer {index}.");
    }
}
