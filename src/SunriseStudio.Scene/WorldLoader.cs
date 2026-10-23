using SunriseStudio.Packages;

namespace SunriseStudio.Scene;

/** Map scenery and mission content remain separate sources in one persistent world context. */
public sealed class WorldLoader(PackageCatalog catalog, StudioNameIndex? names = null)
{
    private WorldCatalog? index;
    public Action<string>? Progress { get; set; }

    /** Missions are matched by authored bubble identities, including missions without a known map. */
    public WorldCatalog BuildCatalog(CancellationToken token = default)
    {
        if (index is not null) return index;
        var maps = MapCatalog.Build(catalog, Describe, token);
        var matches = maps.Maps.SelectMany(map => maps.ActivitiesFor(map.Tag)
            .Select(mission => (Mission: mission, Map: map.Tag))).GroupBy(x => x.Mission.Tag)
            .ToDictionary(x => x.Key, x => x.ToArray());
        var missions = new List<WorldMissionEntry>();
        var diagnostics = maps.Errors.ToList();
        foreach (var entry in catalog.Entries.Where(x => x.Reference == 0x80809994))
        {
            token.ThrowIfCancellationRequested();
            if (matches.TryGetValue(entry.TagHash, out var owned))
                missions.Add(new(entry.TagHash, Describe(entry.TagHash), owned[0].Mission.BubbleHashes,
                    owned.Select(x => x.Map).Distinct().ToArray()));
            else
            {
                try
                {
                    var data = new TagData(catalog.Read(entry.TagHash));
                    missions.Add(new(entry.TagHash, Describe(entry.TagHash),
                        data.Rows(0x50, 0x8080924D, 24).Select(data.U32).ToArray(), []));
                }
                catch (Exception error) when (error is InvalidDataException or ArgumentException or IOException)
                { diagnostics.Add($"Mission {entry.TagHash:X8}: {error.Message}"); }
            }
        }
        index = new(maps.Maps, missions.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray(), diagnostics);
        return index;
    }

    public WorldDocument LoadMap(uint mapTag, WorldLoadOptions? options = null, CancellationToken token = default)
    {
        var settings = Freeze(options ?? new());
        var map = BuildCatalog(token).Maps.FirstOrDefault(x => x.Tag == mapTag)
            ?? throw new InvalidDataException("The selected asset is not an indexed map.");
        ValidateBubbles(map, settings);
        var loader = CreateLoader(settings);
        var scene = loader.Load(mapTag, new SceneLoadOptions
            { MapGlobalIndices = settings.MapGlobalIndices, BubbleNameHashes = settings.BubbleNameHashes }, token);
        names?.NameScene(scene);
        return Compose(map, scene, null, settings, token);
    }

    public WorldDocument LoadMission(uint mapTag, uint missionTag, WorldLoadOptions? options = null,
        CancellationToken token = default) => LoadMission(LoadMap(mapTag, options, token), missionTag, options, token);

    /** A mission selection replaces only its overlay when the base map options still match. */
    public WorldDocument LoadMission(WorldDocument world, uint missionTag, WorldLoadOptions? options = null,
        CancellationToken token = default)
    {
        var settings = Freeze(options ?? world.Options);
        ValidateBubbles(world.Map, settings);
        var mission = BuildCatalog(token).Missions.FirstOrDefault(x => x.Tag == missionTag)
            ?? throw new InvalidDataException("The selected asset is not an indexed mission.");
        if (!mission.MapTags.Contains(world.Map.Tag))
            throw new InvalidDataException("Mission bubbles do not belong to the selected map.");
        var loader = CreateLoader(settings);
        IReadOnlySet<int>? layers = settings.MapGlobalIndices;
        if (settings.UseMissionMapLayers)
        {
            // A mission whose bubbles do not match the map's layer table keeps the chosen layers.
            try { layers = loader.GetScenarioMapIndices(world.Map.Tag, missionTag); }
            catch (Exception error) when (error is ArgumentException or InvalidDataException) { }
            // Every layer is the same as no filter, so the loaded map scene stays in use.
            if (layers is not null && world.MapScene.MapLayers.All(layer => layers.Contains(layer.Index))) layers = null;
            // The layer panel reads the loaded layers from the options.
            settings = settings with { MapGlobalIndices = layers };
        }
        SceneDocument mapScene = world.MapScene;
        // A map scene decoded for fewer bubbles than now requested must be decoded again.
        bool bubblesMissing = world.Options.BubbleNameHashes is { } decoded
            && (settings.BubbleNameHashes is null || !settings.BubbleNameHashes.IsSubsetOf(decoded));
        if (settings.IncludeLowerLods != world.Options.IncludeLowerLods || !Same(layers, world.Options.MapGlobalIndices)
            || bubblesMissing)
            mapScene = loader.Load(world.Map.Tag, new SceneLoadOptions
                { MapGlobalIndices = layers, BubbleNameHashes = settings.BubbleNameHashes }, token);
        IReadOnlySet<uint>? bubbles = settings.BubbleNameHashes is null ? null
            : settings.BubbleNameHashes.Where(mission.BubbleHashes.Contains).ToHashSet();
        var overlay = loader.LoadScenario(missionTag, new SceneLoadOptions
        {
            ScenarioStates = settings.ScenarioStates, StateOrdinals = settings.StateOrdinals,
            BubbleNameHashes = bubbles
        }, token);
        names?.NameScene(mapScene);
        names?.NameScene(overlay);
        return Compose(world.Map, mapScene, overlay, settings, token);
    }

    public ScenarioInfo DescribeMission(uint missionTag) => CreateLoader(new()).DescribeScenario(missionTag);
    public WorldDocument DescribeScene(SceneDocument scene) => WorldDocument.FromScene(scene, names);

    // Decoding depends on the level-of-detail choice, so each choice has its own cache.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, MeshData[]>[] meshCaches = [new(), new()];

    /** Drops decoded meshes; a null tag list drops all of them. */
    public void ForgetMeshes(IEnumerable<uint>? tags)
    {
        foreach (var cache in meshCaches)
            if (tags is null) cache.Clear();
            else foreach (uint tag in tags) cache.TryRemove(tag, out _);
    }

    private AssetSceneLoader CreateLoader(WorldLoadOptions settings) => new(catalog)
    {
        MeshCache = meshCaches[settings.IncludeLowerLods ? 1 : 0],
        IncludeLowerLods = settings.IncludeLowerLods,
        NameResolver = names is null ? null : names.ResolveHash,
        GameNames = names?.GameNames,
        Progress = Progress
    };

    /** Composition copies collection membership while retaining shared mesh and source object instances. */
    private WorldDocument Compose(MapEntry map, SceneDocument mapScene, SceneDocument? mission,
        WorldLoadOptions options, CancellationToken token)
    {
        var combined = new SceneDocument
        {
            Name = mission?.Name ?? mapScene.Name, TagHash = map.Tag, Scenario = mission?.Scenario,
            Attachments = mapScene.Attachments, AttachmentPreview = mapScene.AttachmentPreview
        };
        var aliases = new Dictionary<uint, uint>();
        foreach (var bubble in mapScene.Bubbles)
        { aliases[bubble.TagHash] = bubble.NameHash; aliases[bubble.NameHash] = bubble.NameHash; }
        if (mission?.Scenario is { } described)
            foreach (var bubble in described.Bubbles) aliases[bubble.NameHash] = bubble.NameHash;
        uint Canonical(uint hash) => aliases.GetValueOrDefault(hash, hash);
        bool Visible(uint bubble) => options.BubbleNameHashes is null || bubble == 0
            || options.BubbleNameHashes.Contains(Canonical(bubble));
        var diagnosticSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in mission is null ? new[] { mapScene } : new[] { mapScene, mission })
        {
            token.ThrowIfCancellationRequested();
            if (source.Scenario is not null)
            {
                foreach (var item in source.Instances) if (item.ActivityTag == 0) item.ActivityTag = source.TagHash;
                foreach (var item in source.Markers) if (item.ActivityTag == 0) item.ActivityTag = source.TagHash;
            }
            combined.Instances.AddRange(source.Instances.Where(x => Visible(x.BubbleHash)));
            combined.Markers.AddRange(source.Markers.Where(x => Visible(x.BubbleHash)));
            combined.Bubbles.AddRange(source.Bubbles.Where(x => combined.Bubbles.All(y => y.TagHash != x.TagHash)));
            foreach (var line in source.Diagnostics) if (diagnosticSet.Add(line)) combined.Diagnostics.Add(line);
            combined.MaterialWarnings.AddRange(source.MaterialWarnings);
            combined.ScenarioStates.AddRange(source.ScenarioStates);
            combined.AuthoredObjects.AddRange(source.AuthoredObjects.Where(x => combined.AuthoredObjects.All(y => y.TagHash != x.TagHash)));
            combined.Squads.AddRange(source.Squads);
            combined.LightTables.AddRange(source.LightTables.Where(x => Visible(x.BubbleHash)));
            combined.Cubemaps.AddRange(source.Cubemaps.Where(x => Visible(x.BubbleHash)));
            combined.OcclusionOwners.AddRange(source.OcclusionOwners);
            combined.Atmospheres.AddRange(source.Atmospheres);
            combined.TimesOfDay.AddRange(source.TimesOfDay);
            combined.Visibility.AddRange(source.Visibility.Where(x => Visible(x.BubbleHash)));
            combined.MapContainers.AddRange(source.MapContainers.Where(x => combined.MapContainers.All(y => y.TagHash != x.TagHash)));
            combined.MapLayers.AddRange(source.MapLayers.Where(x => combined.MapLayers.All(y => y.DefinitionTag != x.DefinitionTag || y.Index != x.Index)));
            combined.DecodeErrorCount += source.DecodeErrorCount;
            combined.UnsupportedCount += source.UnsupportedCount;
            combined.ReadTagSet.UnionWith(source.ReadTagSet);
            combined.SquadSourceRows.UnionWith(source.SquadSourceRows);
            foreach (var (tag, bytes) in source.PlacementSources) combined.PlacementSources.TryAdd(tag, bytes);
        }
        var owners = mission?.Scenario is { } scenario
            ? ReadOwners(scenario, token) : new Dictionary<uint, IReadOnlyList<WorldStateKey>>();
        var objects = WorldInspection.CreateObjects(combined, aliases, Describe, owners);
        return new WorldDocument
        {
            Map = map, Activities = BuildCatalog(token).MissionsFor(map.Tag), MapScene = mapScene,
            MissionScene = mission, Scene = combined, Options = options, BubbleAliases = aliases, Objects = objects
        };
    }

    /** Registry array edges retain each authored object's exact bubble and state ownership. */
    private Dictionary<uint, IReadOnlyList<WorldStateKey>> ReadOwners(ScenarioInfo scenario, CancellationToken token)
    {
        var result = new Dictionary<uint, HashSet<WorldStateKey>>();
        var cached = new Dictionary<uint, TagData>();
        foreach (var state in scenario.Bubbles.SelectMany(x => x.States))
        {
            var pending = new Stack<uint>();
            var visited = new HashSet<uint>();
            pending.Push(state.RegistryTag);
            while (pending.TryPop(out uint tag))
            {
                token.ThrowIfCancellationRequested();
                if (!visited.Add(tag) || !catalog.TryGetEntry(tag, out var entry)) continue;
                if (!result.TryGetValue(tag, out var owners)) result[tag] = owners = [];
                owners.Add(new(scenario.TagHash, state.BubbleNameHash, state.Ordinal));
                if (entry.Reference is not (0x8080925E or 0x80809468 or 0x80809B14 or 0x80809462 or 0x8080925B)) continue;
                if (!cached.TryGetValue(tag, out var data)) cached[tag] = data = new(catalog.Read(tag));
                switch (entry.Reference)
                {
                    case 0x8080925E:
                        foreach (int descriptor in new[] { 8, 24, 40 })
                            foreach (int row in data.Rows(descriptor, 0x80809260, 4)) pending.Push(data.U32(row));
                        break;
                    case 0x80809468:
                        if (data.U32(8) != uint.MaxValue) pending.Push(data.U32(8));
                        else foreach (int row in data.Rows(16, 0x80809B13, 4)) pending.Push(data.U32(row));
                        break;
                    case 0x80809B14: pending.Push(data.U32(12)); break;
                    case 0x8080925B: pending.Push(data.U32(20)); break;
                    case 0x80809462:
                        foreach (int row in data.Rows(56, 0x80809464, 24))
                        {
                            if (data.I32(row) != -1 && data.I32(row) != state.EntryIndex) continue;
                            foreach (int handle in data.Rows(row + 8, 0x80809466, 4)) pending.Push(data.U32(handle));
                        }
                        break;
                }
            }
        }
        return result.ToDictionary(x => x.Key, x => (IReadOnlyList<WorldStateKey>)x.Value.ToArray());
    }

    private string Describe(uint tag) => names?.Describe(tag) ?? catalog.GetName(tag) ?? $"0x{tag:X8}";
    private static bool Same<T>(IReadOnlySet<T>? a, IReadOnlySet<T>? b) =>
        a is null ? b is null : b is not null && a.SetEquals(b);
    private static void ValidateBubbles(MapEntry map, WorldLoadOptions options)
    {
        if (options.BubbleNameHashes is not null && options.BubbleNameHashes.Any(x => !map.BubbleHashes.Contains(x)))
            throw new ArgumentException("Bubble selection includes a bubble outside the map.");
    }
    private static WorldLoadOptions Freeze(WorldLoadOptions options) => options with
    {
        StateOrdinals = new Dictionary<uint, int>(options.StateOrdinals),
        BubbleNameHashes = options.BubbleNameHashes?.ToHashSet(),
        MapGlobalIndices = options.MapGlobalIndices?.ToHashSet()
    };
}
