using System.Numerics;
using System.Text.Json.Nodes;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.Scene.Collision;
using SunriseStudio.Tags.Generated;
using SunriseStudio.World;

namespace SunriseStudio.Authoring;

/** One model a bundle bubble placed in a destination: the project model and the world files that place it. */
public sealed record PlacedBundleStatic(string Name, uint ModelTag, string Container, IReadOnlyList<string> Files,
    Vector3 Position, Quaternion Rotation, float PositionError = 0, float Scale = 1);

/** What a whole bundle placed in a destination. */
public sealed record PlacedBundle(int Statics, int Lights, int FarModels, bool Sky, int Entities = 0)
{
    /** New sound events made from the bundle's wav files. */
    public int Sounds { get; init; }
    /** Creatures built over enemies from the bundle's glb files. */
    public int Creatures { get; init; }
    /** Trigger volumes moved to a bundle position. */
    public int Volumes { get; init; }
    /** New squads a mission script places by name. */
    public int Squads { get; init; }
    /** New box triggers a mission script arms and hears by name. */
    public int Doors { get; init; }
    /** Named spawn points, each its own spawn set a teleport names; see WorldContent.BundleSpawnSet. */
    public int Spawns { get; init; }
    /** The new packages the bundle's assets went in, in fill order; one when they fit one package. */
    public IReadOnlyList<ushort> Packages { get; init; } = [];
}
/**
 * Places the statics of one World Import Bundle bubble (`bubble.json` plus glb) in a bubble of a
 * project destination. The bundle's first spawn point lands on the destination bubble's first spawn
 * point, facing the same way, so the bundle reads around where the player arrives.
 */
public static partial class WorldBundleImport
{
    // Installed placement lists hold up to 873 rows; merged chunk lists stay well under that.
    private const int MergedRows = 512;

    /**
     * Each static imports with its glTF materials, its LOD files and as many chunk models as snorm16
     * precision needs; every chunk model gets its own placement at the static's transform.
     * @param bubbleFolder The bundle folder holding bubble.json.
     * @param packageId An unused package id for the imported models.
     * @return One row per placed model.
     */
    public static IReadOnlyList<PlacedBundleStatic> PlaceBubble(StudioProject project, PackageCatalog catalog, RegistryIndex schema,
        string destination, uint bubbleNameHash, string bubbleFolder, ushort packageId, CancellationToken token = default) =>
        PlaceBubble(project, catalog, schema, destination, bubbleNameHash, bubbleFolder, new BundlePackages(project, catalog, packageId), true, token);

    /**
     * PlaceBubble with the statics spilling to the next package when one fills.
     * @param occlusion False leaves the destination's occlusion bake to the caller, which runs it once.
     */
    public static IReadOnlyList<PlacedBundleStatic> PlaceBubble(StudioProject project, PackageCatalog catalog, RegistryIndex schema,
        string destination, uint bubbleNameHash, string bubbleFolder, BundlePackages packages, bool occlusion, CancellationToken token = default)
    {
        var bubble = ReadBubble(bubbleFolder);
        var world = WorldFiles.Load(project.ContentDirectory, destination);
        var (turn, place) = Anchor(world, bubbleNameHash, bubble);
        var containers = WorldBubbles.OwnFirst(world, bubbleNameHash);
        var placed = new List<PlacedBundleStatic>();
        int last = 0;
        // Every static is prepared first and the bubble's assets join the project in one step: each
        // addition rewrites the project manifest, whose size grows with every asset already added.
        var prepared = new List<(JsonObject Item, PreparedBatch Batch)>();
        foreach (var item in ((JsonArray)bubble["statics"]!).OfType<JsonObject>())
        {
            string name = $"{destination}_{(string)bubble["name"]!}_{(string?)item["name"] ?? Path.GetFileNameWithoutExtension((string)item["glb"]!)}";
            if (project.AddedAssets.Any(asset => asset.Name.StartsWith(name + ".", StringComparison.Ordinal)))
                throw new InvalidOperationException($"The project already holds {name}; remove it before placing the bundle again.");
            var input = Lods(bubbleFolder, item);
            var batch = packages.Fill(package => StaticBatchImporter.Prepare(catalog, input, name, package, packages.NextEntry(package), token: token,
                share: packages.Materials));
            packages.Claim(packages.Current, batch.NextEntry);
            prepared.Add((item, batch));
        }
        var added = project.AddAssets(prepared.SelectMany(pair => pair.Batch.Assets).ToArray(), catalog)
            .ToDictionary(asset => asset.Name, StringComparer.Ordinal);
        foreach (var (item, batch) in prepared)
        {
            var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, turn) * Rotation(item["rotation"]);
            var position = Vector3.Transform(Vector(item["position"]), place);
            float scale = (float?)item["scale"] ?? 1;
            var units = new List<(BatchModel Model, string Container, IReadOnlyList<string> Files)>();
            foreach (var model in batch.Models)
            {
                byte[] modelData = project.ReadAddedAsset(added[model.Name + ".model"].Id, catalog);
                var (container, tags) = Template(world, catalog, containers, model.ModelTag, modelData, position, rotation, scale,
                    $"{destination}/{bubbleNameHash:X8}/{model.Name}", ref last);
                units.Add((model, container, WorldContent.AddTags(world, schema, catalog, destination,
                    tags.Select(tag => new WorldTag(tag.Tag, tag.Class, tag.TypeInfo, tag.Data)).ToArray())));
            }
            // Chunk lists merge into lists of up to MergedRows rows, so a static adds few container
            // members; installed containers hold at most 23.
            foreach (var group in units.Chunk(MergedRows))
            {
                string list = group[0].Files[^1];
                if (group.Length > 1)
                    world.Edit(list, (_, fields) =>
                    {
                        var rows = (JsonArray)WorldFiles.Field(fields, "placements");
                        foreach (var unit in group.Skip(1))
                            foreach (var row in (JsonArray)WorldFiles.Field(world.Fields(unit.Files[^1]), "placements")) rows.Add(row!.DeepClone());
                    });
                // Joining the list is what makes the bubble load the static.
                WorldContent.Join(world, group[0].Container, list);
                foreach (var (model, _, files) in group)
                    placed.Add(new(model.Name, model.ModelTag, group[0].Container, [.. files.SkipLast(1), list], position, rotation, model.PositionError, scale));
            }
        }
        world.SaveChanged(project.ContentDirectory);
        Bake(project, catalog, destination, bubbleNameHash, bubbleFolder, packages.Room(BakeEntries(bubble)), occlusion);
        return placed;
    }

    /**
     * Adds a new static collision entity for `meshes` to the bubble's own container. Its triangles
     * take the material and filter of the nearest triangle of the bubble's own collision.
     * @return The collision placement list's file name and the new owner tag.
     */
    public static (string List, uint Owner) PlaceCollision(StudioProject project, PackageCatalog catalog, string destination,
        uint bubbleNameHash, string name, IReadOnlyList<CollisionBakeMesh> meshes, ushort packageId)
    {
        if (project.AddedAssets.Any(asset => asset.Name.StartsWith(name + ".", StringComparison.Ordinal)))
            throw new InvalidOperationException($"The project already holds {name}; remove it before placing the collision again.");
        var world = WorldFiles.Load(project.ContentDirectory, destination);
        var containers = WorldBubbles.OwnFirst(world, bubbleNameHash);
        var (baseline, template) = CollisionTemplate(world, catalog, bubbleNameHash);
        int first = project.NextEntry(packageId);
        var prepared = CollisionBake.CreateCompound(catalog, baseline, template, meshes, name, packageId, first);
        project.AddAssets(prepared.Assets, catalog);
        string list = WorldContent.AddPayloadList(world, destination, name, SStaticCollisionPayload.Class, "collision_owner", prepared.Owner,
            containers.First());
        world.SaveChanged(project.ContentDirectory);
        return (list, prepared.Owner);
    }

    /** The static's collision glb: its `collision` file, none when that is false, else its render glb. */
    private static string? CollisionGlb(string bubbleFolder, JsonObject item) => item["collision"] switch
    {
        null => Path.Combine(bubbleFolder, (string)item["glb"]!),
        JsonValue value when value.TryGetValue(out bool on) => on ? Path.Combine(bubbleFolder, (string)item["glb"]!) : null,
        var file => Path.Combine(bubbleFolder, (string)file!)
    };

    /** Every triangle of a glb as one mesh in its own space. */
    private static CollisionBakeMesh CollisionMesh(string name, string glb, QsTransform transform)
    {
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        foreach (var mesh in GltfInterchange.Read(glb))
        {
            int start = vertices.Count;
            vertices.AddRange(mesh.Positions);
            indices.AddRange(mesh.Indices.Select(index => start + (int)index));
        }
        return new(name, vertices, indices, transform);
    }

    /**
     * Places a whole bundle: bundle bubble i lands in the destination's i-th bubble with states. Each
     * bubble's statics and lights go in its own bubble, far tiles in far containers, and the sky in the
     * first bubble.
     * @param bundleFolder The folder holding bundle.json.
     */
    public static PlacedBundle PlaceBundle(StudioProject project, PackageCatalog catalog, RegistryIndex schema, string destination,
        string bundleFolder, ushort packageId, CancellationToken token = default)
    {
        // Every step loads and saves the world; one session reads it from disk once.
        using var session = WorldFiles.Session();
        var bundle = JsonNode.Parse(File.ReadAllText(Path.Combine(bundleFolder, "bundle.json")))!.AsObject();
        var names = ((JsonArray)bundle["bubbles"]!).Select(name => (string)name!).ToArray();
        var live = WorldContent.LiveBubbles(WorldFiles.Load(project.ContentDirectory, destination));
        if (names.Length > live.Count)
            throw new InvalidDataException($"The bundle has {names.Length} bubbles; the destination has {live.Count} with states.");
        var hashes = names.Select((name, i) => (name, live[i])).ToDictionary(x => x.name, x => x.Item2);
        var bubbles = names.ToDictionary(name => name, name => ReadBubble(Path.Combine(bundleFolder, name)));
        int statics = 0, lights = 0, far = 0, entities = 0, volumes = 0;
        var staticLists = new Dictionary<string, IReadOnlyList<PlacedBundleStatic>>(StringComparer.Ordinal);
        // Each step takes its package from here, so a full package hands over to the next one.
        var packages = new BundlePackages(project, catalog, packageId);
        var sounds = PlaceSounds(project, catalog, destination, bundleFolder, bundle, packages.Room(BundlePackages.StepReserve));
        WorldMusic.PlaceBundleMusic(project, catalog, schema, destination, bundleFolder, bundle, packages.Room(BundlePackages.StepReserve), token);
        foreach (string name in names)
        {
            token.ThrowIfCancellationRequested();
            string folder = Path.Combine(bundleFolder, name);
            // A bubble starts where it has room, so its assets mostly share one package.
            packages.Room(BundlePackages.BubbleReserve);
            if (bubbles[name]["statics"] is JsonArray { Count: > 0 } || bubbles[name]["permissions"] is JsonObject)
            {
                var placed = PlaceBubble(project, catalog, schema, destination, hashes[name], folder, packages, false, token);
                staticLists[name] = placed;
                statics += placed.Count;
            }
            entities += PlaceEntities(project, catalog, schema, destination, hashes[name], folder, packages.Room(BundlePackages.BubbleReserve), token, sounds);
            volumes += PlaceVolumes(project, catalog, schema, destination, hashes[name], folder, packages.Room(BundlePackages.StepReserve), token);
            foreach (var light in (bubbles[name]["lights"] as JsonArray ?? []).OfType<JsonObject>())
            {
                PlaceLight(project, catalog, destination, hashes[name], folder, $"{name}_{(string)light["name"]!}", Vector(light["position"]),
                    packages.Room(BundlePackages.StepReserve));
                lights++;
            }
        }
        PlaceNeighbours(project, catalog, schema, destination, names, hashes, bubbles, staticLists);
        // Occlusion re-applies every baked bubble, so it runs once for the whole bundle.
        BakeDestinationOcclusion(project, catalog, destination);
        int doorSpawns = names.Sum(name => PlaceSpawns(project, destination, hashes[name], name, bubbles[name]).Count);
        var creatures = PlaceCreatures(project, catalog, schema, destination, bundleFolder, bundle, packages.Room(BundlePackages.BubbleReserve), token);
        int squads = PlaceSquads(project, catalog, schema, destination, names.Select(name => (hashes[name], bubbles[name])).ToArray(), creatures, token);
        int doors = PlaceDoors(project, catalog, schema, destination, names.Select(name => (hashes[name], bubbles[name])).ToArray(),
            packages.Room(BundlePackages.BubbleReserve), token);
        // Walked crossings come last of the bubble steps: they move each bubble's statics and collision
        // lists into a container its neighbours stream too.
        var crossings = WorldCrossingImport.Read(bubbles);
        if (crossings.Count > 0)
        {
            var (_, place) = Anchor(WorldFiles.Load(project.ContentDirectory, destination), hashes[names[0]], bubbles[names[0]]);
            var bounds = names.Where(name => bubbles[name]["bounds"] is JsonObject)
                .ToDictionary(name => name, name => (Vector(bubbles[name]["bounds"]!["min"]), Vector(bubbles[name]["bounds"]!["max"])));
            WorldCrossingImport.Place(project, catalog, schema, destination, crossings, hashes, bounds, place,
                packages.Room(BundlePackages.StepReserve));
        }
        PlaceBounds(project, catalog, schema, destination, bundleFolder);

        var outdoor = names.Where(name => (string?)bubbles[name]["kind"] == "outdoor").Select(name => hashes[name]).ToArray();
        foreach (var tile in (bundle["far"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var covers = ((JsonArray)tile["covers"]!).Select(name => (string)name!).ToArray();
            var boxes = covers.Select(name => (JsonObject)bubbles[name]["bounds"]!).ToArray();
            var clip = new FarClip(boxes.Select(box => Vector(box["min"])).Aggregate(Vector3.Min), boxes.Select(box => Vector(box["max"])).Aggregate(Vector3.Max),
                (float?)tile["margin"] ?? 2, (float?)tile["sink"] ?? 0.5f);
            far += PlaceFar(project, catalog, schema, destination, hashes[names[0]], Path.Combine(bundleFolder, names[0]), (string)tile["name"]!,
                Path.Combine(bundleFolder, (string)tile["glb"]!), clip, covers.Select(name => hashes[name]).ToArray(), outdoor,
                packages.Room(BundlePackages.BubbleReserve), token).Count;
        }
        bool sky = bundle["sky"] is JsonObject skyNode;
        if (sky)
            PlaceSky(project, catalog, destination, hashes[names[0]],
                PngInterchange.Read(Path.Combine(bundleFolder, (string)bundle["sky"]!["panorama"]!)), packages.Room(BundlePackages.StepReserve), token);
        return new(statics, lights, far, sky, entities) { Sounds = sounds.Count, Volumes = volumes, Squads = squads, Doors = doors, Creatures = creatures.Count, Spawns = doorSpawns,
            Packages = packages.Used.Where(package => packages.NextEntry(package) > 0).ToArray() };
    }

    /**
     * Makes each bubble's statics draw in its `neighbours` too. Each neighbour gets its own placement
     * from its own container; a placement built for one bubble draws black in another.
     */
    private static void PlaceNeighbours(StudioProject project, PackageCatalog catalog, RegistryIndex schema, string destination,
        IReadOnlyList<string> names, IReadOnlyDictionary<string, uint> hashes, IReadOnlyDictionary<string, JsonObject> bubbles,
        IReadOnlyDictionary<string, IReadOnlyList<PlacedBundleStatic>> statics)
    {
        WorldFiles? world = null;
        Dictionary<string, string>? models = null;
        foreach (string name in names)
        {
            var loaders = (bubbles[name]["neighbours"] as JsonArray ?? []).Select(node => (string)node!).Where(hashes.ContainsKey)
                .Select(neighbour => hashes[neighbour]).Distinct().ToArray();
            if (loaders.Length == 0 || !statics.TryGetValue(name, out var placed) || placed.Count == 0) continue;
            world ??= WorldFiles.Load(project.ContentDirectory, destination);
            models ??= project.AddedAssets.Where(asset => asset.Name.EndsWith(".model", StringComparison.Ordinal))
                .ToDictionary(asset => asset.Name, asset => asset.Id, StringComparer.Ordinal);
            foreach (uint loader in loaders)
            {
                var containers = WorldBubbles.OwnFirst(world, loader);
                int last = 0;
                var lists = new List<(string Container, string List)>();
                foreach (var item in placed)
                {
                    byte[] modelData = project.ReadAddedAsset(models[item.Name + ".model"], catalog);
                    var (container, tags) = Template(world, catalog, containers, item.ModelTag, modelData, item.Position, item.Rotation, item.Scale,
                        $"{destination}/{loader:X8}/near/{item.Name}", ref last);
                    lists.Add((container, WorldContent.AddTags(world, schema, catalog, destination,
                        tags.Select(tag => new WorldTag(tag.Tag, tag.Class, tag.TypeInfo, tag.Data)).ToArray())[^1]));
                }
                // The neighbour's lists merge like a bubble's own, so they add few container members.
                foreach (var group in lists.GroupBy(row => row.Container).SelectMany(rows => rows.Chunk(MergedRows)))
                {
                    string list = group[0].List;
                    if (group.Length > 1)
                        world.Edit(list, (_, fields) =>
                        {
                            var rows = (JsonArray)WorldFiles.Field(fields, "placements");
                            foreach (var unit in group.Skip(1))
                                foreach (var row in (JsonArray)WorldFiles.Field(world.Fields(unit.List), "placements")) rows.Add(row!.DeepClone());
                        });
                    WorldContent.Join(world, group[0].Container, list);
                }
            }
        }
        world?.SaveChanged(project.ContentDirectory);
    }
    /**
     * Places far geometry for `covered` in a new far container that every candidate bubble not
     * streaming the covered cells loads. The glb is in the anchor bubble's bundle space; its triangles
     * outside the clip area go, and the rest sinks below the near geometry.
     * @return One row per placed far model; Container is the far container.
     */
    public static IReadOnlyList<PlacedBundleStatic> PlaceFar(StudioProject project, PackageCatalog catalog, RegistryIndex schema,
        string destination, uint anchorBubble, string anchorFolder, string name, string glb, FarClip clip,
        IReadOnlyCollection<uint> covered, IReadOnlyCollection<uint> candidates, ushort packageId, CancellationToken token = default)
    {
        var bubble = ReadBubble(anchorFolder);
        var world = WorldFiles.Load(project.ContentDirectory, destination);
        var loaders = WorldFarLayer.Loaders(world, covered, candidates);
        if (loaders.Count == 0) throw new InvalidDataException("No candidate bubble may load this far layer; each streams the covered cells.");
        var (turn, place) = Anchor(world, anchorBubble, bubble);
        string modelName = $"{destination}_far_{name}";
        if (project.AddedAssets.Any(asset => asset.Name.StartsWith(modelName + ".", StringComparison.Ordinal)))
            throw new InvalidOperationException($"The project already holds {modelName}; remove it before placing the far layer again.");
        int first = project.NextEntry(packageId);
        var batch = StaticBatchImporter.Prepare(catalog, new([glb], []), modelName, packageId, first,
            geometry: mesh => FarMesh.Clip(mesh, clip), token: token);
        var added = project.AddAssets(batch.Assets, catalog);
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, turn);
        var position = Vector3.Transform(Vector3.Zero, place);
        var containers = WorldContent.Containers(world, anchorBubble);
        var lists = new List<(BatchModel Model, IReadOnlyList<string> Files)>();
        int last = 0;
        foreach (var model in batch.Models)
        {
            byte[] modelData = project.ReadAddedAsset(added.Single(asset => asset.Name == model.Name + ".model").Id, catalog);
            var (_, tags) = Template(world, catalog, containers, model.ModelTag, modelData, position, rotation, 1,
                $"{destination}/far/{model.Name}", ref last);
            lists.Add((model, WorldContent.AddTags(world, schema, catalog, destination,
                tags.Select(tag => new WorldTag(tag.Tag, tag.Class, tag.TypeInfo, tag.Data)).ToArray())));
        }
        string container = WorldFarLayer.Add(world, destination, $"far_{name}", loaders, lists.Select(x => x.Files[^1]).ToArray());
        world.SaveChanged(project.ContentDirectory);
        return lists.Select(x => new PlacedBundleStatic(x.Model.Name, x.Model.ModelTag, container, x.Files, position, rotation,
            x.Model.PositionError)).ToArray();
    }

    /**
     * Adds a sky ring with `panorama` as its texture to the bubble's first container.
     * @return The sky placement list's file name and the new sky-objects tag.
     */
    public static (string List, uint SkyObjects) PlaceSky(StudioProject project, PackageCatalog catalog, string destination,
        uint bubbleNameHash, RgbaImage panorama, ushort packageId, CancellationToken token = default)
    {
        string name = $"{destination}_sky";
        if (project.AddedAssets.Any(asset => asset.Name.StartsWith(name + ".", StringComparison.Ordinal)))
            throw new InvalidOperationException($"The project already holds {name}; remove it before placing the sky again.");
        var world = WorldFiles.Load(project.ContentDirectory, destination);
        int first = project.NextEntry(packageId);
        var sky = SkyAuthoring.PrepareDome(catalog, panorama, name, packageId, first, token);
        project.AddAssets(sky.Assets, catalog);
        string list = WorldContent.AddPayloadList(world, destination, name, SSkyPayload.Class, "sky_objects", sky.SkyObjects,
            WorldContent.Containers(world, bubbleNameHash).First());
        world.SaveChanged(project.ContentDirectory);
        return (list, sky.SkyObjects);
    }

    /**
     * Adds a local light copied from a stock row at `position`, in bundle space of the anchor bubble.
     * @return The light placement list's file name and the new light table tag.
     */
    public static (string List, uint LightTable) PlaceLight(StudioProject project, PackageCatalog catalog, string destination,
        uint anchorBubble, string anchorFolder, string name, Vector3 position, ushort packageId)
    {
        string assetName = $"{destination}_light_{name}";
        if (project.AddedAssets.Any(asset => asset.Name.StartsWith(assetName + ".", StringComparison.Ordinal)))
            throw new InvalidOperationException($"The project already holds {assetName}; remove it before placing the light again.");
        var bubble = ReadBubble(anchorFolder);
        var world = WorldFiles.Load(project.ContentDirectory, destination);
        var (turn, place) = Anchor(world, anchorBubble, bubble);
        int first = project.NextEntry(packageId);
        var light = LightAuthoring.Prepare(catalog, Vector3.Transform(position, place), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, turn),
            assetName, packageId, first);
        project.AddAssets(light.Assets, catalog);
        string list = WorldContent.AddPayloadList(world, destination, assetName, SLightsPayload.Class, "light_collection", light.LightTable,
            WorldBubbles.OwnFirst(world, anchorBubble)[0]);
        world.SaveChanged(project.ContentDirectory);
        return (list, light.LightTable);
    }

    /**
     * Gives every named spawn point of a bundle bubble its own one-point spawn set at its anchored place,
     * in the bubble's own container, so a teleport into the bubble can name it.
     * @param bubbleName The bubble's folder name in the bundle, the first part of each set's hashed name.
     * @return Each spawn's name and set hash, in bubble.json order.
     */
    public static IReadOnlyList<(string Name, uint SetHash)> PlaceSpawns(StudioProject project, string destination, uint bubbleNameHash,
        string bubbleName, JsonObject bubble)
    {
        var named = (bubble["spawn_points"] as JsonArray ?? []).OfType<JsonObject>().Where(spawn => (string?)spawn["name"] is { Length: > 0 }).ToList();
        if (named.Count == 0) return [];
        var duplicate = named.GroupBy(spawn => (string)spawn["name"]!).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"Bubble {bubbleName} names two spawn points {duplicate.Key}.");
        var world = WorldFiles.Load(project.ContentDirectory, destination);
        var (turn, place) = Anchor(world, bubbleNameHash, bubble);
        string container = WorldBubbles.OwnContainer(world, bubbleNameHash);
        var placed = named.Select(spawn => ((string)spawn["name"]!, WorldContent.BundleSpawnSet(bubbleName, (string)spawn["name"]!))).ToList();
        WorldContent.AddSpawnSets(world, destination, FileName($"{destination}_spawns_{bubbleName}"), named.Select((spawn, index) =>
            (placed[index].Item2, Vector3.Transform(Vector(spawn["position"]), place),
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, turn + (float)(spawn["facing"] ?? 0.0) * MathF.PI / 180))).ToArray(), container);
        world.SaveChanged(project.ContentDirectory);
        return placed;
    }

    /** A spawn file name from bundle names: lowercase, and _ for anything a tag name may not hold. */
    private static string FileName(string text) =>
        new(text.ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' ? c : '_').ToArray());

    // The key ReadBubble gives a shared-space bubble: the first spawn of the bundle's first bubble.
    private const string SharedSpawn = "@shared_spawn";

    /**
     * A bubble folder's bubble.json. In a bundle whose `space` is `shared`, every bubble anchors like
     * the bundle's first bubble, so all bubbles keep their places relative to each other.
     */
    private static JsonObject ReadBubble(string bubbleFolder)
    {
        var bubble = JsonNode.Parse(File.ReadAllText(Path.Combine(bubbleFolder, "bubble.json")))!.AsObject();
        string bundleFile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(bubbleFolder))!, "bundle.json");
        if (!File.Exists(bundleFile)) return bubble;
        var bundle = JsonNode.Parse(File.ReadAllText(bundleFile))!.AsObject();
        if ((string?)bundle["space"] != "shared" || bundle["bubbles"] is not JsonArray { Count: > 0 } names) return bubble;
        // An `anchor` names the bundle-space point that lands on the destination's first spawn.
        if (bundle["anchor"] is JsonArray { Count: 3 } anchor)
        {
            bubble[SharedSpawn] = new JsonObject { ["position"] = anchor.DeepClone() };
            return bubble;
        }
        var first = JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(bundleFile)!, (string)names[0]!, "bubble.json")))!;
        bubble[SharedSpawn] = (first["spawn_points"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?.DeepClone()
            ?? throw new InvalidDataException("The bundle's first bubble has no spawn point to anchor the shared space with.");
        return bubble;
    }

    /**
     * The turn and transform that land the bundle bubble's first spawn on the destination bubble's
     * first spawn. A shared-space bubble lands the bundle's first spawn on the destination's first bubble, unturned.
     */
    private static (float Turn, Matrix4x4 Place) Anchor(WorldFiles world, uint bubbleNameHash, JsonObject bubble)
    {
        var shared = bubble[SharedSpawn] as JsonObject;
        if (shared is not null) bubbleNameHash = WorldContent.FirstLiveBubble(world);
        var anchor = WorldContent.Spawns(world, bubbleNameHash, hostOnly: true).FirstOrDefault()
            ?? throw new InvalidDataException($"Bubble 0x{bubbleNameHash:X8} reaches no spawn point to anchor the bundle to.");
        var spawn = shared ?? (bubble["spawn_points"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()
            ?? throw new InvalidDataException("The bundle bubble has no spawn point to anchor it with.");
        // Facing is degrees about +Z from +X; the stock spawn is a turn about +Z.
        // A shared space keeps the bundle's axes, so a map of it stays north-up.
        float turn = shared is not null ? 0 : 2 * MathF.Atan2(anchor.Rotation.Z, anchor.Rotation.W) - (float)(spawn["facing"] ?? 0.0) * MathF.PI / 180;
        var place = Matrix4x4.CreateTranslation(-Vector(spawn["position"])) * Matrix4x4.CreateRotationZ(turn)
            * Matrix4x4.CreateTranslation(anchor.Position);
        return (turn, place);
    }

    /** The static's glb, then its `lods` files, with its `lod_distances` and `draw_distance`. */
    private static StaticLodInput Lods(string bubbleFolder, JsonObject item)
    {
        var levels = new List<string> { Path.Combine(bubbleFolder, (string)item["glb"]!) };
        if (item["lods"] is JsonArray lods) levels.AddRange(lods.Select(lod => Path.Combine(bubbleFolder, (string)lod!)));
        var distances = item["lod_distances"] is JsonArray values ? values.Select(value => (float)value!).ToArray() : [];
        return new(levels, distances, (float?)item["draw_distance"]);
    }

    /**
     * The first of the containers whose installed source holds a static placement to copy.
     * @param last The index tried first; receives the index used.
     */
    private static (string Container, IReadOnlyList<PlacedStaticTag> Tags) Template(WorldFiles world, PackageCatalog catalog,
        IReadOnlyList<string> containers, uint model, byte[] modelData, Vector3 position, Quaternion rotation, float scale, string identity,
        ref int last)
    {
        // The container that served the last model is tried first; a failed try reads its whole source.
        int first = last;
        foreach (int index in containers.Select((_, index) => index).OrderBy(index => index == first ? 0 : 1))
        {
            string container = containers[index];
            if ((world[container].SourceTag ?? world[container].CopyOf) is not uint source) continue;
            try
            {
                var tags = StaticPlacement.Build(catalog, source, model, modelData, position, rotation, scale, identity);
                last = index;
                return (container, tags);
            }
            catch (InvalidDataException) { }
        }
        throw new InvalidDataException("No container of the bubble holds a static placement the bundle can copy.");
    }

    private static Vector3 Vector(JsonNode? node)
    {
        var values = (JsonArray)node!;
        return new((float)values[0]!, (float)values[1]!, (float)values[2]!);
    }

    private static Quaternion Rotation(JsonNode? node) => node is JsonArray values
        ? Quaternion.Normalize(new((float)values[0]!, (float)values[1]!, (float)values[2]!, (float)values[3]!)) : Quaternion.Identity;
}
