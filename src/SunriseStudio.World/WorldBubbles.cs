using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json.Nodes;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Graph;
using SunriseStudio.Tags.Tft;

namespace SunriseStudio.World;

/**
 * A bubble WorldBubbles.AddBubble made.
 * @param Row Its scenario row; the client loads it as slice set 8 * Row.
 * @param RegionRow Its kind-0 region row: the map index its states and container masks name.
 * @param SpawnSetHash The one spawn set its cloned spawn points carry; a teleport names it.
 * @param Files The files it added.
 */
public sealed record AddedBubble(int Row, uint NameHash, int RegionRow, uint SpawnSetHash, IReadOnlyList<string> Files)
{
    public int SliceSet => Row * WorldBubbles.SliceSetsPerBubble;
}

/**
 * Gives a world copy a new bubble in a scenario row that has no states. The new bubble is a copy of
 * a template bubble's own map content and shares its state registry. It has no visibility map until
 * WorldCrossings gives it one; without that the player reaches it by a teleport.
 */
public static class WorldBubbles
{
    /** The client numbers 64 bubbles per scenario: slice set index 0 to 0x1FF, 8 per bubble. */
    public const int Capacity = 64;
    public const int SliceSetsPerBubble = 8;
    /** The scenario's info key listing added bubbles; the activity SDK copies each one's template rows. */
    public const string InfoKey = "added_bubbles";
    // The placement payload that loads a bubble's visibility map, keyed by the template's row.
    private const string VisibilityPayload = "0x80807246";
    // Region row kinds of the map definition: a bubble, a connector, a shared load region.
    private const int BubbleRow = 0, SharedRow = 2;
    // Container and slice-set masks are 256 bits, one per region row.
    private const int MaskBytes = 32;

    /** Classes a new bubble copies from its template; every other file it reaches stays shared. */
    private static readonly HashSet<uint> CopiedClasses =
    [
        SBubbleDefinition.Class, SMapContainer.Class, SMapPlacements.Class, SStaticMeshInstances.Class,
        SInstanceBoundsTable.Class, SMapResourceLink.Class, SSpawnPoints.Class,
    ];

    /** One added bubble the scenario info lists. */
    public sealed record Added(int Row, int Template, string Name, int RegionRow);

    /** The added bubbles the scenario lists, from its info. */
    public static IReadOnlyList<Added> List(TftFile scenario)
    {
        if (scenario.Info is null) return [];
        JsonNode? info;
        try { info = JsonNode.Parse(scenario.Info); }
        catch (System.Text.Json.JsonException) { return []; }
        return (info?[InfoKey] as JsonArray ?? []).OfType<JsonObject>()
            .Select(item => new Added((int)item["row"]!, (int)item["template"]!, (string)item["name"]!, (int)item["region"]!)).ToArray();
    }

    /** Adds a bubble to a project destination on disk; see AddBubble. */
    public static AddedBubble AddBubble(string contentDirectory, string destination, uint templateBubble, string name, int? row = null)
    {
        var world = WorldFiles.Load(contentDirectory, destination);
        var added = AddBubble(world, templateBubble, name, row);
        world.SaveChanged(contentDirectory);
        return added;
    }

    /**
     * Adds a bubble named `name` in a scenario row with no states, copied from a live template bubble.
     * @param templateBubble Name hash of a scenario row with states and a copied bubble parent.
     * @param name Lowercase letters, digits and _; its FNV-1 is the bubble's name hash.
     * @param row The scenario row; null takes the first free row after the last live one, else the first free row.
     * @return The new bubble.
     */
    public static AddedBubble AddBubble(WorldFiles world, uint templateBubble, string name, int? row = null)
    {
        if (WorldIdentity.NameProblem(name) is { } problem) throw new ArgumentException(problem, nameof(name));
        var scenario = world.Single(SScenario.Class);
        var mapRoot = world.Single(SMapRoot.Class);
        var mapNames = world.Single(SMapLocationNames.Class);
        string destination = scenario.Name.Split('/')[1];
        var rows = ((JsonArray)WorldFiles.Field(world.Fields(scenario.Name), "bubbles")).OfType<JsonObject>().ToList();
        var names = world.Fields(mapNames.Name);
        var regions = ((JsonArray)WorldFiles.Field(names, "slices")).OfType<JsonObject>().ToList();
        var sets = ((JsonArray)WorldFiles.Field(names, "slice_sets")).OfType<JsonObject>().ToList();

        int template = rows.FindIndex(item => WorldFiles.Hash(WorldFiles.Field(item, "name_hash")) == templateBubble && States(item).Count > 0);
        if (template < 0) throw new InvalidDataException($"The world has no bubble 0x{templateBubble:X8} with states.");
        int target = row ?? FreeRow(rows, sets.Count);
        if (target < 0 || target >= Math.Min(Capacity, sets.Count)) throw new ArgumentOutOfRangeException(nameof(row), $"No free scenario row below {Math.Min(Capacity, sets.Count)}.");
        if (target < rows.Count && States(rows[target]).Count > 0) throw new InvalidOperationException($"Scenario row {target} already has states.");
        int region = RegionRowOf(regions, target), templateRegion = RegionRowOf(regions, template);

        uint hash = StringBankWriter.Hash(name), spawnSet = StringBankWriter.Hash(name + "_arrival");
        var taken = rows.Select(item => WorldFiles.Hash(WorldFiles.Field(item, "name_hash")))
            .Concat(sets.Select(set => WorldFiles.Hash(WorldFiles.Field(set, "bubble_name_hash"))));
        if (taken.Contains(hash)) throw new InvalidDataException($"The bubble name {name} collides with a bubble of the map; choose another.");
        var mapRows = ((JsonArray)WorldFiles.Field(world.Fields(mapRoot.Name), "map_entries")).OfType<JsonObject>().ToList();
        if (target >= mapRows.Count || mapRows[target].Any(pair => WorldFiles.Text(pair.Value) is { } text && !text.StartsWith('#') && !text.StartsWith("0x", StringComparison.Ordinal)))
            throw new InvalidDataException($"Map root row {target} is missing or already names a bubble.");

        // The template's own content carries its region bit; a container without it is shared and stays shared.
        string definition = WorldContent.Definition(world, templateBubble);
        var all = WorldContent.Containers(world, templateBubble);
        var own = OwnCopies(world, all.Where(container => HasBit(Mask(world, container), templateRegion)).ToList());
        var dropped = all.Where(container => HasBit(Mask(world, container), templateRegion) && !own.Contains(container)).ToHashSet();
        var visibility = world.Reached(own).Where(file => world[file].ClassId == SMapPlacements.Class && IsVisibilityList(world, file)).ToHashSet();
        var copied = world.Reached(own).Where(file => CopiedClasses.Contains(world[file].ClassId) && !visibility.Contains(file)).Append(definition).ToHashSet();
        var renamed = copied.ToDictionary(file => file, file => CopyName(file, name), StringComparer.Ordinal);
        if (renamed.Values.Any(world.Contains)) throw new InvalidDataException($"The world already holds a bubble named {name}.");
        foreach (string file in copied.Order(StringComparer.Ordinal)) world.Add(Copy(world[file], renamed));
        // An own container the copy skips must not stay listed, or the copy would load the template's.
        world.Edit(renamed[definition], (_, fields) =>
        {
            var children = (JsonArray)WorldFiles.Field(fields, "bubble_children");
            for (int index = children.Count - 1; index >= 0; index--)
                if (WorldFiles.Text(children[index]?["wide_tag"]) is { } child && dropped.Contains(child)) children.RemoveAt(index);
        });

        string parent = world.Files.Single(file => file.ClassId == SBubbleParent.Class && ParentBubble(world, file.Name) == templateBubble).Name;
        string newParent = CopyName(parent, name);
        world.Add(Copy(world[parent], new Dictionary<string, string>(renamed) { [definition] = renamed[definition] }, newParent));
        world.Edit(newParent, (_, fields) => ((JsonObject)WorldFiles.Field(fields, "metadata"))["bubble_name_hash"] = WorldFiles.HashText(hash));
        var mask = Bits(region);
        foreach (string container in copied.Where(file => world[file].ClassId == SMapContainer.Class))
            world.Edit(renamed[container], (_, fields) =>
            {
                fields["bubble_mask"] = mask;
                var members = (JsonArray)WorldFiles.Field(fields, "contents");
                for (int index = members.Count - 1; index >= 0; index--)
                    if (WorldFiles.Text(members[index]?["member_tag"]) is { } member && visibility.Contains(member)) members.RemoveAt(index);
            });
        foreach (string points in copied.Where(file => world[file].ClassId == SSpawnPoints.Class))
            world.Edit(renamed[points], (_, fields) =>
            {
                foreach (var point in (fields["spawn_points"] as JsonArray ?? []).OfType<JsonObject>()) point["spawn_name_identity"] = WorldFiles.HashText(spawnSet);
            });

        var states = new JsonArray();
        var added = new List<string>(renamed.Values) { newParent };
        foreach (var state in States(rows[template]))
        {
            string entry = WorldFiles.Text(WorldFiles.Field(state, "entry")) ?? throw new InvalidDataException("A template state names no copied entry.");
            string newEntry = CopyName(entry, name);
            world.Add(Copy(world[entry], new Dictionary<string, string>(), newEntry));
            world.Edit(newEntry, (_, fields) => fields["index"] = target);
            added.Add(newEntry);
            var copy = (JsonObject)state.DeepClone();
            copy["bubble_name_hash"] = WorldFiles.HashText(hash);
            copy["map_bubble_index"] = region;
            copy["entry"] = newEntry;
            states.Add(copy);
        }
        world.Edit(scenario.Name, (json, fields) =>
        {
            var bubbles = (JsonArray)WorldFiles.Field(fields, "bubbles");
            // Rows are positional, so a padding row keeps its slice set's name and has no states.
            while (bubbles.Count <= target)
                bubbles.Add(new JsonObject { ["name_hash"] = WorldFiles.Field(sets[bubbles.Count], "bubble_name_hash").DeepClone(), ["states"] = new JsonArray() });
            bubbles[target]!["name_hash"] = WorldFiles.HashText(hash);
            bubbles[target]!["states"] = states;
            json["info"] = WithAdded(WorldFiles.Text(json["info"]), new(target, template, name, region));
        });
        world.Edit(mapNames.Name, (_, fields) =>
        {
            var set = (JsonObject)((JsonArray)WorldFiles.Field(fields, "slice_sets"))[target]!;
            var shared = sets[template]["h_0f1887e1"] is JsonArray list ? (JsonArray)list.DeepClone() : new JsonArray();
            // Own row and the template's shared load regions; no connector, so no bubble streams beside it.
            set["bubble_name_hash"] = WorldFiles.HashText(hash);
            set["cell_mask"] = Bits([region, .. shared.OfType<JsonObject>().Select(RowIndex).Where(index => Kind(regions[index]) == SharedRow)]);
            set["h_0d537865"] = new JsonArray();
            set["h_0f1887e1"] = shared;
        });
        world.Edit(mapRoot.Name, (_, fields) =>
        {
            var entry = (JsonObject)((JsonArray)WorldFiles.Field(fields, "map_entries"))[target]!;
            entry["wide_tag"] = newParent;
            entry["narrow_tag"] = WorldFiles.HashText(uint.MaxValue);
        });
        return new(target, hash, region, spawnSet, added);
    }

    // The placement payload that names an AO map, and the map's bubble row field.
    private const string OcclusionPayload = "0x80806F38", SelectorField = "selector";

    /**
     * Gives an added bubble its own copy of each installed AO map its lists name, with the copy's row
     * as selector. The client takes an AO map only when its selector is a loaded bubble row.
     * @param name The name the bubble was added with.
     * @return How many maps were copied.
     */
    public static int OwnOcclusionMaps(WorldFiles world, PackageCatalog catalog, RegistryIndex schema, AddedBubble added, string name)
    {
        string destination = world.Single(SScenario.Class).Name.Split('/')[1];
        var copies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string list in added.Files.Where(file => world[file].ClassId == SMapPlacements.Class))
        {
            var maps = ListRows(world, list).Where(row => Payload(row) == OcclusionPayload)
                .Select(row => WorldFiles.Text(row["payload"]?["occlusion_map"])).OfType<string>().Where(map => map.StartsWith('#')).ToList();
            if (maps.Count == 0) continue;
            foreach (string map in maps.Where(map => !copies.ContainsKey(map)))
            {
                uint tag = WorldFiles.Hash(map[1..]);
                string type = WorldTemplate.TypeName(schema, SOcclusionMap.Class);
                string file = $"{WorldTemplate.Folder}/{destination}/{type}/{tag:x8}_{name}.{type}";
                world.Add(TftClone.Clone([tag], new CatalogTagSource(catalog), schema, _ => file).Single());
                world.Edit(file, (_, fields) =>
                {
                    WorldFiles.Field(fields, SelectorField);
                    fields[SelectorField] = added.Row;
                });
                copies.Add(map, file);
            }
            world.Edit(list, (_, fields) =>
            {
                foreach (var row in ((JsonArray)WorldFiles.Field(fields, "placements")).OfType<JsonObject>())
                    if (Payload(row) == OcclusionPayload && row["payload"] is JsonObject payload
                        && WorldFiles.Text(payload["occlusion_map"]) is { } map && copies.TryGetValue(map, out string? copy))
                        payload["occlusion_map"] = copy;
            });
        }
        return copies.Count;
    }

    /**
     * The spawn sets a teleport into a bubble may name, as the server finds them: every container
     * whose mask has the bubble's map index, every member list whose first row places spawn points,
     * and every set hash at point +32 in those points.
     * @param bubbleHash Name hash of a scenario row with states.
     * @return The set hashes, ascending.
     */
    public static IReadOnlyList<uint> SpawnSets(WorldFiles world, uint bubbleHash)
    {
        int region = MapIndex(world, bubbleHash);
        var sets = new SortedSet<uint>();
        var names = world.Files.Select(file => file.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var container in world.Files.Where(file => file.ClassId == SMapContainer.Class))
        {
            if (!HasBit(Mask(world, container.Name), region)) continue;
            foreach (var member in (world.Fields(container.Name)["contents"] as JsonArray ?? []).OfType<JsonObject>())
                if (WorldFiles.Text(member["member_tag"]) is { } list && names.Contains(list) && SpawnPointsOf(world, list, names) is { } points)
                    foreach (var point in (world.Fields(points)["spawn_points"] as JsonArray ?? []).OfType<JsonObject>())
                        sets.Add(WorldFiles.Hash(WorldFiles.Field(point, "spawn_name_identity")));
        }
        return sets.ToArray();
    }

    // Placement payloads that light a bubble: cubemap probes and environment volumes.
    private const string CubemapPayload = "0x80806B7F", EnvironmentPayload = "0x80806FAD";

    /** Payloads a copy keeps its template's container for: cubemaps, environment, AO, atmosphere, time of day and the one-per-bubble resources. */
    private static readonly HashSet<string> LightingPayloads =
        [CubemapPayload, EnvironmentPayload, "0x80806F38", "0x80807086", "0x80806F61", "0x80806B87", "0x80806F78", "0x80806DF1"];

    /**
     * The live bubble whose own containers place the most cubemap probes and environment volumes.
     * A bubble copied from it takes that lighting; a small bubble's lighting can leave its copies black.
     */
    public static uint LightingTemplate(WorldFiles world) => WorldContent.LiveBubbles(world).MaxBy(bubble =>
    {
        int region = MapIndex(world, bubble);
        return WorldContent.Containers(world, bubble).Where(container => HasBit(Mask(world, container), region))
            .Sum(container => Rows(world, container).Count(row => Payload(row) is CubemapPayload or EnvironmentPayload));
    });

    /**
     * The own containers a copy takes: the first, those with lighting rows and the first with spawn
     * points. The rest hold host content, which a strip drops anyway.
     * @param own The template's containers whose mask carries its region; content added to the copy joins them.
     */
    private static HashSet<string> OwnCopies(WorldFiles world, IReadOnlyList<string> own)
    {
        var names = world.Files.Select(file => file.Name).ToHashSet(StringComparer.Ordinal);
        var kept = own.Where((container, index) => index == 0 || Rows(world, container).Any(row => Payload(row) is { } kind && LightingPayloads.Contains(kind)))
            .ToHashSet(StringComparer.Ordinal);
        if (own.FirstOrDefault(container => Lists(world, container).Any(list => SpawnPointsOf(world, list, names) is not null)) is { } spawns) kept.Add(spawns);
        return kept;
    }

    /** The placement lists a container names directly. */
    private static IEnumerable<string> Lists(WorldFiles world, string container) =>
        (world.Fields(container)["contents"] as JsonArray ?? []).OfType<JsonObject>().Select(member => WorldFiles.Text(member["member_tag"]))
            .OfType<string>().Where(list => world.Contains(list) && world[list].ClassId == SMapPlacements.Class);

    /** The rows of the placement lists a container names directly; an unreadable list adds none. */
    private static IEnumerable<JsonObject> Rows(WorldFiles world, string container) => Lists(world, container).SelectMany(list => ListRows(world, list));

    /** The rows of one placement list; an unreadable list has none. */
    private static JsonObject[] ListRows(WorldFiles world, string list)
    {
        try { return (world.Fields(list)["placements"] as JsonArray ?? []).OfType<JsonObject>().ToArray(); }
        catch (InvalidDataException) { return []; }
    }

    private static string? Payload(JsonObject row) => WorldFiles.Text(row["payload"]?["@class"]);

    /**
     * Makes every state of a bubble private, so it runs in the player's own instance.
     * @return True when a state changed.
     */
    public static bool SetPrivate(WorldFiles world, uint bubbleHash)
    {
        bool changed = false;
        world.Edit(world.Single(SScenario.Class).Name, (_, fields) =>
        {
            foreach (var row in ((JsonArray)WorldFiles.Field(fields, "bubbles")).OfType<JsonObject>()
                .Where(item => WorldFiles.Hash(WorldFiles.Field(item, "name_hash")) == bubbleHash))
                foreach (var state in States(row).Where(state => (Number(state["public_flag"]) ?? 0) != 0))
                {
                    state["public_flag"] = 0;
                    changed = true;
                }
        });
        return changed;
    }

    /** The bubble's scenario row, which an authored object's bubble block names. */
    public static int ScenarioRow(WorldFiles world, uint bubbleHash)
    {
        var rows = ((JsonArray)WorldFiles.Field(world.Fields(world.Single(SScenario.Class).Name), "bubbles")).OfType<JsonObject>().ToList();
        int row = rows.FindIndex(item => WorldFiles.Hash(WorldFiles.Field(item, "name_hash")) == bubbleHash && States(item).Count > 0);
        return row >= 0 ? row : throw new InvalidDataException($"The world has no bubble 0x{bubbleHash:X8} with states.");
    }

    /** The bubble's map index: its region row, which its states name and its own containers' masks carry. */
    public static int MapIndex(WorldFiles world, uint bubbleHash)
    {
        var row = ((JsonArray)WorldFiles.Field(world.Fields(world.Single(SScenario.Class).Name), "bubbles")).OfType<JsonObject>()
            .FirstOrDefault(item => WorldFiles.Hash(WorldFiles.Field(item, "name_hash")) == bubbleHash && States(item).Count > 0)
            ?? throw new InvalidDataException($"The world has no bubble 0x{bubbleHash:X8} with states.");
        return (int)(Number(States(row)[0]["map_bubble_index"]) ?? throw new InvalidDataException("A state has no map index."));
    }

    /** The bubble's first container whose mask carries its map index, so what it lists loads only there and counts as declared by it. */
    public static string OwnContainer(WorldFiles world, uint bubbleHash)
    {
        int region = MapIndex(world, bubbleHash);
        return WorldContent.Containers(world, bubbleHash).FirstOrDefault(container => HasBit(Mask(world, container), region))
            ?? throw new InvalidDataException($"Bubble 0x{bubbleHash:X8} has no container with its own map index {region}.");
    }

    /**
     * The bubble's containers, those whose mask carries its map index first. Content added to one
     * bubble goes there: a shared container loads in every slice set, and the client collects at
     * most 1024 non-empty lists in one load.
     */
    public static IReadOnlyList<string> OwnFirst(WorldFiles world, uint bubbleHash)
    {
        int region = MapIndex(world, bubbleHash);
        return WorldContent.Containers(world, bubbleHash).OrderBy(container => HasBit(Mask(world, container), region) ? 0 : 1).ToList();
    }

    // The placement payload that places a spawn points tag.
    private const string SpawnPayload = "0x80809160";

    /** The spawn points file a list's first row places; null for any other list. The server reads only the first row. */
    private static string? SpawnPointsOf(WorldFiles world, string list, IReadOnlySet<string> names)
    {
        if (world[list].ClassId != SMapPlacements.Class) return null;
        JsonArray? rows;
        try { rows = world.Fields(list)["placements"] as JsonArray; }
        catch (InvalidDataException) { return null; }
        return rows is [JsonObject first, ..] && first["payload"] is JsonObject payload && WorldFiles.Text(payload["@class"]) == SpawnPayload
            && WorldFiles.Text(payload["spawn_points"]) is { } points && names.Contains(points) ? points : null;
    }

    /** The first free row after the last live row, else the first free row; -1 when none. */
    private static int FreeRow(IReadOnlyList<JsonObject> rows, int sets)
    {
        int last = rows.Select((item, index) => States(item).Count > 0 ? index : -1).Max();
        int limit = Math.Min(Capacity, sets);
        for (int index = last + 1; index < limit; index++) if (index >= rows.Count || States(rows[index]).Count == 0) return index;
        for (int index = 0; index < limit; index++) if (index >= rows.Count || States(rows[index]).Count == 0) return index;
        return -1;
    }

    /** A row's states; an empty array may read back as null. */
    private static IReadOnlyList<JsonObject> States(JsonObject row) => (row["states"] as JsonArray ?? []).OfType<JsonObject>().ToArray();

    /** The kind-0 region row that names scenario row `bubble`; its index is the bubble's map index. */
    private static int RegionRowOf(IReadOnlyList<JsonObject> regions, int bubble)
    {
        for (int index = 0; index < regions.Count; index++)
            if (Kind(regions[index]) == BubbleRow && Number(regions[index]["owner_bubble"]) == bubble) return index;
        throw new InvalidDataException($"The map has no region row for bubble {bubble}.");
    }

    /** A JSON number of any stored width; null for a missing value. */
    public static long? Number(JsonNode? node) =>
        node is null ? null : long.Parse(node.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

    /** A region row's u16 kind at +12, which the field view keeps as unknown bytes. */
    private static int Kind(JsonObject region) => BinaryPrimitives.ReadUInt16LittleEndian(Raw(region, 24).AsSpan(12));

    /** A u16 row index list element, kept as unknown bytes. */
    private static int RowIndex(JsonObject element) => BinaryPrimitives.ReadUInt16LittleEndian(Raw(element, 2));

    /** The unknown bytes of a viewed struct, zero where the view shows a field. */
    private static byte[] Raw(JsonObject item, int size)
    {
        var bytes = new byte[size];
        foreach (var (key, value) in item["@unknown"] as JsonObject ?? [])
        {
            byte[] patch = Convert.FromHexString((string)value!);
            int at = int.Parse(key, CultureInfo.InvariantCulture);
            patch.AsSpan(0, Math.Min(patch.Length, Math.Max(0, size - at))).CopyTo(bytes.AsSpan(Math.Min(at, size)));
        }
        return bytes;
    }

    private static byte[] Mask(WorldFiles world, string container) => Convert.FromHexString(WorldFiles.Text(world.Fields(container)["bubble_mask"]) ?? "");

    private static bool HasBit(byte[] mask, int bit) => bit / 8 < mask.Length && (mask[bit / 8] >> (bit % 8) & 1) != 0;

    /** A 256-bit mask with these bits, as the text form writes it. */
    private static string Bits(params int[] bits)
    {
        var mask = new byte[MaskBytes];
        foreach (int bit in bits) mask[bit / 8] |= (byte)(1 << (bit % 8));
        return Convert.ToHexStringLower(mask);
    }

    /** True for a list whose every row loads a visibility map. */
    private static bool IsVisibilityList(WorldFiles world, string list)
    {
        JsonArray? rows;
        try { rows = world.Fields(list)["placements"] as JsonArray; }
        catch (InvalidDataException) { return false; }
        return rows is { Count: > 0 } && rows.All(item => WorldFiles.Text(item?["payload"]?["@class"]) == VisibilityPayload);
    }

    private static uint ParentBubble(WorldFiles world, string parent) =>
        WorldFiles.Hash(WorldFiles.Field((JsonObject)WorldFiles.Field(world.Fields(parent), "metadata"), "bubble_name_hash"));

    /** world/DEST/TYPE/STEM.TYPE becomes world/DEST/TYPE/STEM_NAME.TYPE. */
    private static string CopyName(string file, string name)
    {
        int dot = file.LastIndexOf('.');
        return $"{file[..dot]}_{name}{file[dot..]}";
    }

    /**
     * A copy of a file under a new name, its references to renamed files moved. A copy names its
     * installed source as copy_of, so it never stands in for that tag.
     */
    private static TftFile Copy(TftFile file, IReadOnlyDictionary<string, string> renamed, string? name = null)
    {
        uint? source = file.SourceTag ?? file.CopyOf;
        return new TftFile
        {
            Name = name ?? renamed[file.Name], TagType = file.TagType, ClassId = file.ClassId, TypeInfo = file.TypeInfo,
            BuildHash = file.BuildHash, Layout = file.Layout, Data = file.Data, Meta = file.Meta,
            References = file.References.Select(reference => renamed.TryGetValue(reference.Target, out string? to) ? reference with { Target = to } : reference).ToList(),
            Info = source is uint tag ? TftFile.CopyInfo(tag) : file.Info,
        };
    }

    /** The scenario info with one more added bubble. */
    private static string WithAdded(string? info, Added added)
    {
        JsonObject root;
        try { root = info is null ? new JsonObject() : JsonNode.Parse(info) as JsonObject ?? new JsonObject(); }
        catch (System.Text.Json.JsonException) { root = new JsonObject(); }
        if (root[InfoKey] is not JsonArray list) root[InfoKey] = list = new JsonArray();
        list.Add(new JsonObject { ["row"] = added.Row, ["template"] = added.Template, ["name"] = added.Name, ["region"] = added.RegionRow });
        return root.ToJsonString();
    }
}
