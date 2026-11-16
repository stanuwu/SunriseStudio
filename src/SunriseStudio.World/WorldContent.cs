using System.Numerics;
using System.Text.Json.Nodes;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Graph;
using SunriseStudio.Tags.Tft;

namespace SunriseStudio.World;

/**
 * One tag made outside a world, keyed by a scratch tag the other new tags reference.
 * @param Source The installed tag a copy was made from; 0 for a tag with no source.
 */
public sealed record WorldTag(uint Tag, uint Class, uint TypeInfo, byte[] Data, uint Source = 0);

/** One spawn point a world places, in map space. */
public sealed record WorldSpawn(Vector3 Position, Quaternion Rotation, uint Identity);

/** Adds content to a world copy: new tags as files, and placement lists joined to a bubble's containers. */
public static class WorldContent
{
    /** The container files of the bubble with this name hash, in the order its definition lists them. */
    public static IReadOnlyList<string> Containers(WorldFiles world, uint bubbleNameHash)
    {
        string definition = Definition(world, bubbleNameHash);
        var containers = world.Files.Where(file => file.ClassId == SMapContainer.Class).Select(file => file.Name).ToHashSet();
        return ((JsonArray)WorldFiles.Field(world.Fields(definition), "bubble_children")).OfType<JsonObject>()
            .SelectMany(child => child.Select(pair => WorldFiles.Text(pair.Value)))
            .Where(name => name is not null && containers.Contains(name)).Select(name => name!).Distinct().ToList();
    }

    /** The bubble definition file of the bubble with this name hash. */
    public static string Definition(WorldFiles world, uint bubbleNameHash)
    {
        var parent = world.Files.Where(file => file.ClassId == SBubbleParent.Class).SingleOrDefault(file =>
                WorldFiles.Hash(WorldFiles.Field((JsonObject)WorldFiles.Field(world.Fields(file.Name), "metadata"), "bubble_name_hash"))
                == bubbleNameHash)
            ?? throw new InvalidDataException($"The world has no bubble 0x{bubbleNameHash:X8}.");
        return WorldFiles.Text(WorldFiles.Field(world.Fields(parent.Name), "bubble_link"))
            ?? throw new InvalidDataException($"Bubble 0x{bubbleNameHash:X8} names no copied definition.");
    }

    /** The name hash of the first scenario row that has states. */
    public static uint FirstLiveBubble(WorldFiles world) =>
        LiveBubbles(world).DefaultIfEmpty(0U).First() is var hash and not 0 ? hash : throw new InvalidDataException("The world has no bubble with states.");

    /** The name hashes of the scenario rows that have states, in row order. */
    public static IReadOnlyList<uint> LiveBubbles(WorldFiles world) =>
        ((JsonArray)WorldFiles.Field(world.Fields(world.Single(SScenario.Class).Name), "bubbles")).OfType<JsonObject>()
            .Where(row => ((JsonArray)WorldFiles.Field(row, "states")).Count > 0)
            .Select(row => WorldFiles.Hash(WorldFiles.Field(row, "name_hash"))).ToArray();

    /**
     * The spawn points the bubble's containers reach through copied tags.
     * @param hostOnly True skips spawn files a bundle made, so the first point is the host anchor.
     */
    public static IReadOnlyList<WorldSpawn> Spawns(WorldFiles world, uint bubbleNameHash, bool hostOnly = false)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(Containers(world, bubbleNameHash));
        var spawns = new List<WorldSpawn>();
        while (pending.TryPop(out string? name))
        {
            if (!reached.Add(name)) continue;
            var file = world[name];
            if (file.ClassId == SSpawnPoints.Class && (!hostOnly || WorldStrip.IsHostCopy(file)) && world.Fields(name)["spawn_points"] is JsonArray rows)
                foreach (var row in rows.OfType<JsonObject>())
                {
                    var p = (JsonArray)WorldFiles.Field(row, "position");
                    var r = (JsonArray)WorldFiles.Field(row, "rotation");
                    spawns.Add(new(new(WorldFiles.Float(p[0]), WorldFiles.Float(p[1]), WorldFiles.Float(p[2])),
                        new(WorldFiles.Float(r[0]), WorldFiles.Float(r[1]), WorldFiles.Float(r[2]), WorldFiles.Float(r[3])),
                        WorldFiles.Hash(WorldFiles.Field(row, "spawn_name_identity"))));
                }
            foreach (var reference in file.References.Where(x => x.IsProjectTag)) pending.Push(reference.Target);
        }
        return spawns;
    }

    /** The spawn set a named bundle spawn point gets: FNV-1 over the UTF-8 of "BUBBLE:SPAWN", BUBBLE being the bundle's bubble folder. */
    public static uint BundleSpawnSet(string bubble, string spawn) => StringBankWriter.Hash($"{bubble}:{spawn}");

    // The placement payload that places a spawn points tag; the server reads only a list's first row.
    private const uint SpawnPayload = 0x80809160;

    /**
     * Adds spawn points, one row each, in one spawn points file placed by a one-row list copied from a
     * spawn list the world holds, and joins the list to `container`. One file keeps the container small.
     * @param name Names both new files; a world that holds them already refuses.
     * @param points Each row's set hash, position and rotation.
     * @return The spawn points file and the list.
     */
    public static (string Points, string List) AddSpawnSets(WorldFiles world, string destination, string name,
        IReadOnlyList<(uint SetHash, Vector3 Position, Quaternion Rotation)> points, string container)
    {
        if (points.Count == 0) throw new ArgumentException("A spawn file needs a point.", nameof(points));
        string pointsFile = $"{WorldTemplate.Folder}/{destination}/spawn_points/{name}.spawn_points";
        string list = $"{WorldTemplate.Folder}/{destination}/map_placements/{name}.map_placements";
        if (world.Contains(pointsFile) || world.Contains(list)) throw new InvalidOperationException($"The world already holds {name}.");
        var template = world.Files.Where(file => file.ClassId == SMapPlacements.Class).Select(file => SpawnList(world, file.Name))
            .FirstOrDefault(found => found is { } pair && world.Fields(pair.Points)["spawn_points"] is JsonArray { Count: > 0 })
            ?? throw new InvalidDataException("The world holds no spawn list with a point to copy.");
        var pointsJson = TftText.ToJson(world[template.Points], out string? fallback);
        if (fallback is not null) throw new InvalidDataException($"{template.Points} has no field view ({fallback}).");
        pointsJson["name"] = pointsFile;
        pointsJson["info"] = null;
        var rows = (JsonArray)WorldFiles.Field((JsonObject)pointsJson["fields"]!, "spawn_points");
        var model = (JsonObject)rows[0]!.DeepClone();
        rows.Clear();
        foreach (var (setHash, position, turn) in points)
        {
            var row = (JsonObject)model.DeepClone();
            var rotation = Quaternion.Normalize(turn);
            if (!float.IsFinite(position.X + position.Y + position.Z) || !float.IsFinite(rotation.X + rotation.Y + rotation.Z + rotation.W))
                throw new InvalidDataException($"Spawn set 0x{setHash:X8} has a position or rotation that is not a finite number.");
            var p = (JsonArray)WorldFiles.Field(row, "position");
            // Adding 0 turns -0 into 0, which the text form writes as a number; a turn about Z gives -0 in X and Y.
            p[0] = position.X + 0f; p[1] = position.Y + 0f; p[2] = position.Z + 0f;
            row["rotation"] = new JsonArray(rotation.X + 0f, rotation.Y + 0f, rotation.Z + 0f, rotation.W + 0f);
            row["spawn_name_identity"] = WorldFiles.HashText(setHash);
            rows.Add(row);
        }
        world.Add(TftText.FromJson(pointsJson));
        var listJson = TftText.ToJson(world[template.List], out fallback);
        if (fallback is not null) throw new InvalidDataException($"{template.List} has no field view ({fallback}).");
        listJson["name"] = list;
        listJson["info"] = null;
        ((JsonObject)((JsonArray)WorldFiles.Field((JsonObject)listJson["fields"]!, "placements"))[0]!["payload"]!)["spawn_points"] = pointsFile;
        world.Add(TftText.FromJson(listJson));
        Join(world, container, list);
        return (pointsFile, list);
    }

    /** A one-row list that places a spawn points file of this world, with that file; null for any other list. */
    private static (string List, string Points)? SpawnList(WorldFiles world, string list)
    {
        JsonArray? rows;
        try { rows = world.Fields(list)["placements"] as JsonArray; }
        catch (InvalidDataException) { return null; }
        return rows is [JsonObject only] && only["payload"] is JsonObject payload && WorldFiles.Text(payload["@class"]) == WorldFiles.HashText(SpawnPayload)
            && WorldFiles.Text(payload["spawn_points"]) is { } points && world.Contains(points) ? (list, points) : null;
    }

    // Type word bits that mark a manifest root entry.
    private const uint RootTypeMask = 0xF000, RootType = 0x2000;

    /**
     * Adds new tags as files of the world. A reference between them becomes a project name; any
     * other reference stays a raw tag, as a reference to stock content does.
     * @param shared Tags that take a fixed name; one the world already holds is named, not added again.
     * @return Each tag's file name, in the order given.
     */
    public static IReadOnlyList<string> AddTags(WorldFiles world, RegistryIndex schema, PackageCatalog catalog, string destination,
        IReadOnlyList<WorldTag> tags, IReadOnlyDictionary<uint, string>? shared = null)
    {
        // Scratch tags repeat between calls, so a name the world holds takes the next free suffix.
        var names = new Dictionary<uint, string>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var held = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            string type = WorldTemplate.TypeName(schema, tag.Class);
            if (shared is not null && shared.TryGetValue(tag.Tag, out string? fixedName))
            {
                string file = $"{WorldTemplate.Folder}/{destination}/{type}/{fixedName}.{type}";
                if (world.Contains(file)) held.Add(file);
                names.Add(tag.Tag, file);
                taken.Add(file);
                continue;
            }
            string name = $"{WorldTemplate.Folder}/{destination}/{type}/{tag.Tag:x8}.{type}";
            for (int suffix = 1; world.Contains(name) || taken.Contains(name); suffix++)
                name = $"{WorldTemplate.Folder}/{destination}/{type}/{tag.Tag:x8}_{suffix}.{type}";
            names.Add(tag.Tag, name);
            taken.Add(name);
        }
        string Name(uint tag) => names[tag];
        var files = TftClone.Clone(tags.Select(x => x.Tag).ToArray(), new ScratchSource(catalog, tags), schema, Name);
        // A manifest root is built from its source's manifest, so a copied root names its source.
        var sources = tags.Where(tag => tag.Source != 0 && (tag.TypeInfo & RootTypeMask) == RootType).ToDictionary(tag => Name(tag.Tag), tag => tag.Source);
        foreach (var file in files)
        {
            if (held.Contains(file.Name))
            {
                if (!world[file.Name].Data.AsSpan().SequenceEqual(file.Data))
                    throw new InvalidDataException($"{file.Name} already holds different content.");
                continue;
            }
            world.Add(new TftFile
            {
                Name = file.Name, TagType = file.TagType, ClassId = file.ClassId, TypeInfo = file.TypeInfo, BuildHash = file.BuildHash,
                Layout = file.Layout, Data = file.Data, References = file.References,
                Info = sources.TryGetValue(file.Name, out uint source) ? TftFile.CloneInfo(source) : null,
            });
        }
        return tags.Select(x => Name(x.Tag)).ToArray();
    }

    /**
     * Adds a one-row placement list whose payload of `payloadClass` names `tag` in `field`, copied from
     * such a list the world holds, and joins it to `container`.
     * @return The new list's file name.
     */
    public static string AddPayloadList(WorldFiles world, string destination, string name, uint payloadClass, string field, uint tag,
        string container)
    {
        var template = world.Files.Where(file => file.ClassId == SMapPlacements.Class).FirstOrDefault(file =>
                world.Fields(file.Name)["placements"] is JsonArray and [JsonObject row]
                && row["payload"] is JsonObject payload && WorldFiles.Text(payload["@class"]) == WorldFiles.HashText(payloadClass))
            ?? throw new InvalidDataException($"The world holds no one-row list with a 0x{payloadClass:X8} payload to copy.");
        return PayloadList(world, world[template.Name], destination, name, payloadClass, field, tag, container);
    }

    /**
     * Adds a copy of an installed placement list with one of its references pointed at another tag,
     * and joins it to `container`. For a list whose payload has no field view. A list of this name
     * the world already holds is pointed at the new tag in place.
     * @param from The installed tag the template names.
     * @param to The tag the copy names in its place.
     * @return The new list's file name.
     */
    public static string AddListCopy(WorldFiles world, RegistryIndex schema, PackageCatalog catalog, string destination, string name,
        uint templateList, uint from, uint to, string container)
    {
        string file = $"{WorldTemplate.Folder}/{destination}/map_placements/{name}.map_placements";
        var copy = TftClone.Clone([templateList], new CatalogTagSource(catalog), schema, _ => file).Single();
        string old = TftNames.Installed(from);
        if (copy.ClassId != SMapPlacements.Class || copy.References.Count(reference => reference.Target == old) != 1)
            throw new InvalidDataException($"0x{templateList:X8} is not a placement list that names 0x{from:X8} once.");
        var references = copy.References.Select(reference =>
            reference.Target == old ? reference with { Target = TftNames.Installed(to) } : reference).ToList();
        if (world.Contains(file)) world.Replace(WorldDevices.With(copy, copy.Data, references));
        else
        {
            world.Add(WorldDevices.With(copy, copy.Data, references));
            Join(world, container, file);
        }
        return file;
    }

    private static string PayloadList(WorldFiles world, TftFile template, string destination, string name, uint payloadClass, string field,
        uint tag, string container)
    {
        string file = $"{WorldTemplate.Folder}/{destination}/map_placements/{name}.map_placements";
        var json = TftText.ToJson(template, out string? fallback);
        if (fallback is not null) throw new InvalidDataException($"{template.Name} has no field view ({fallback}).");
        json["name"] = file;
        var rows = (JsonArray)WorldFiles.Field((JsonObject)json["fields"]!, "placements");
        if (rows is not [JsonObject only] || only["payload"] is not JsonObject found || WorldFiles.Text(found["@class"]) != WorldFiles.HashText(payloadClass))
            throw new InvalidDataException($"{template.Name} is not a one-row list with a 0x{payloadClass:X8} payload.");
        var target = (JsonObject)rows[0]!["payload"]!;
        if (target[field] is null) throw new InvalidDataException($"The 0x{payloadClass:X8} payload has no {field}.");
        target[field] = "#" + WorldFiles.HashText(tag);
        world.Add(TftText.FromJson(json));
        Join(world, container, file);
        return file;
    }

    /**
     * Adds a one-row placement list that places `entityFile`, a file of this world or `#0x...` for an
     * installed entity, and joins it to `container`. The row is copied from the first placement row
     * the world holds, without its payload.
     * @param identity Names the placement; the same name gives the same placement identity.
     * @return The new list's file name.
     */
    public static string AddEntityList(WorldFiles world, string destination, string name, string entityFile, Vector3 position,
        Quaternion rotation, float scale, string identity, string container)
    {
        if (!entityFile.StartsWith('#') && !world.Contains(entityFile))
            throw new InvalidDataException($"The world has no {entityFile}.");
        if (!float.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        var template = world.Files.Where(file => file.ClassId == SMapPlacements.Class)
                .FirstOrDefault(file => world.Fields(file.Name)["placements"] is JsonArray { Count: > 0 })
            ?? throw new InvalidDataException("The world holds no placement row to copy.");
        string file = $"{WorldTemplate.Folder}/{destination}/map_placements/{name}.map_placements";
        var json = TftText.ToJson(world[template.Name], out string? fallback);
        if (fallback is not null) throw new InvalidDataException($"{template.Name} has no field view ({fallback}).");
        json["name"] = file;
        var rows = (JsonArray)WorldFiles.Field((JsonObject)json["fields"]!, "placements");
        var row = (JsonObject)rows[0]!.DeepClone();
        rows.Clear();
        rotation = Quaternion.Normalize(rotation);
        row["class_tag"] = entityFile;
        row["payload"] = null;
        row["position"] = new JsonArray(position.X, position.Y, position.Z);
        row["rotation"] = new JsonArray(rotation.X, rotation.Y, rotation.Z, rotation.W);
        row["uniform_scale"] = scale;
        row["name_hash"] = WorldFiles.HashText(0x811C9DC5);
        // An installed entity row carries flags 4: bit 0 clear, so the class decides replication.
        row["placement_flags"] = 4;
        row["placement_identity"] = PlacementIdentity(identity);
        rows.Add(row);
        world.Add(TftText.FromJson(json));
        Join(world, container, file);
        return file;
    }

    /** The identity a placement row carries for a name; a device slot that holds the same value drives the placed object. */
    public static ulong PlacementIdentity(string text)
    {
        ulong hash = 0xCBF29CE484222325;
        foreach (byte value in System.Text.Encoding.UTF8.GetBytes(text)) hash = (hash * 0x100000001B3) ^ value;
        return hash;
    }

    /** Adds a placement list to a container's members. */
    public static void Join(WorldFiles world, string container, string list) =>
        world.Edit(container, (_, fields) => ((JsonArray)WorldFiles.Field(fields, "contents")).Add(new JsonObject { ["member_tag"] = list }));

    /** The new tags, and the installation for the tags they reference. */
    private sealed class ScratchSource(PackageCatalog catalog, IReadOnlyList<WorldTag> tags) : ITagSource
    {
        public IEnumerable<PackageEntry> Entries => tags.Select(tag =>
        {
            var (package, index) = TagHashes.Split(tag.Tag);
            return new PackageEntry(tag.Tag, tag.Class, (int)((tag.TypeInfo >> 9) & 127), (int)((tag.TypeInfo >> 6) & 7),
                tag.Data.Length, $"w64_sunrise_{package:x4}_0", package, index) { TypeInfo = tag.TypeInfo };
        });
        public byte[] Read(uint tag) => tags.FirstOrDefault(x => x.Tag == tag)?.Data ?? catalog.Read(tag);
        public bool Contains(uint tag) => tags.Any(x => x.Tag == tag) || catalog.ContainsTag(tag);
        public uint? ResolveWide(ulong hash) => catalog.Resolve64(hash);
    }
}
