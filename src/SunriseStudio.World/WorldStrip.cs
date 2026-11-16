using System.Text.Json.Nodes;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Tft;

namespace SunriseStudio.World;

/**
 * What StripHost removed from a world.
 * @param DroppedRows Rows of the dropped lists, by payload class as 0x text, or "entity" for a row with no payload.
 * @param MixedLists Host lists kept whole because they mix kept and dropped rows.
 * @param RemovedFiles Files the world no longer holds: the dropped lists and what only they reached.
 */
public sealed record WorldStripResult(IReadOnlyDictionary<string, int> DroppedRows, int DroppedLists, int KeptLists, int MixedLists,
    IReadOnlyList<string> RemovedFiles);

/**
 * Removes a copied world's host content: installed placement lists of statics, terrain, collision,
 * nav, props, decals, lights, sounds or placed entities, and host objects with any slot not of a kept
 * type. Spawns, sky, lighting, AO and environment volumes stay; spawn rows move onto the bundle anchor.
 */
public static class WorldStrip
{
    /** The scenario's info key that marks a stripped world; the activity SDK then copies no installed squad. */
    public const string InfoKey = "host_stripped";

    /** Payload classes a stripped world keeps; any other row is host content. */
    public static IReadOnlySet<uint> KeptPayloads { get; } = new HashSet<uint>
    {
        0x80809160, // spawn points: the player's arrival and the bundle's anchor
        0x80806F91, // sky
        0x80807086, // atmosphere
        0x80806F61, // time of day
        0x80806B7F, // cubemap probes
        0x80806FAD, // environment volumes: near the viewer they override fog and exposure
        0x80806F38, // AO maps: an added static reads its ambient light from its template's map
        // Unknown one-per-bubble or map-origin resources; kept so the bubble loads as before.
        0x80806B87, 0x80806F78, 0x8080899C, 0x80806DF1,
    };

    /**
     * Slot types of the host objects a stripped world keeps: the players object, navigation and
     * music sensors. Any other slot is host activity content: squads, triggers, volumes, devices.
     */
    public static IReadOnlySet<int> KeptSlotTypes { get; } = new HashSet<int> { 11, 13, 16, 17, 18, 19, 35, 41, 53, 68 };

    /** True when every slot of a host object is of a kept type; an object with no slot stays. */
    private static bool KeepsObject(WorldFiles world, string name)
    {
        JsonObject fields;
        try { fields = world.Fields(name); }
        catch (InvalidDataException) { return true; }
        var slots = (fields["slot_table"] as JsonObject)?["slots"] as JsonArray ?? [];
        return slots.OfType<JsonObject>().All(slot => WorldBubbles.Number(slot["slot_type"]) is long type && KeptSlotTypes.Contains((int)type));
    }

    /** A file copied from the installation keeps the installed tag as its name; an added bubble's copy adds _NAME. */
    public static bool IsHostCopy(TftFile file)
    {
        string stem = Path.GetFileNameWithoutExtension(file.Name.Split('/')[^1]);
        return file.SourceTag is uint source && stem == source.ToString("x8")
            || file.CopyOf is uint copied && stem.StartsWith(copied.ToString("x8") + "_", StringComparison.Ordinal);
    }

    /** True when StripHost marked the world's scenario. */
    public static bool IsStripped(TftFile scenario)
    {
        if (scenario.Info is null) return false;
        try { return JsonNode.Parse(scenario.Info)?[InfoKey] is JsonValue value && value.TryGetValue(out bool on) && on; }
        catch (System.Text.Json.JsonException) { return false; }
    }

    /**
     * Strips a project destination on disk: drops host lists from its containers, deletes the files
     * no longer reached and marks its scenario.
     */
    public static WorldStripResult StripHost(string contentDirectory, string destination)
    {
        var world = WorldFiles.Load(contentDirectory, destination);
        var result = StripHost(world);
        world.SaveChanged(contentDirectory);
        foreach (string name in result.RemovedFiles) File.Delete(WorldFiles.PathOf(contentDirectory, name));
        return result;
    }

    /**
     * Drops every host list with only host rows from every container, removes the files that only
     * those lists reached, and marks the scenario. A list that mixes kept and host rows stays whole,
     * since rows are found by index. A second call drops nothing more.
     */
    public static WorldStripResult StripHost(WorldFiles world)
    {
        var scenario = world.Single(SScenario.Class);
        string[] roots = [scenario.Name, world.Single(SMapRoot.Class).Name,
            .. world.Files.Where(file => file.ClassId == ActivityRoot.Class).Select(file => file.Name)];
        var before = world.Reached(roots);
        var rows = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        int kept = 0, mixed = 0;
        foreach (var list in world.Files.Where(file => file.ClassId == SMapPlacements.Class && IsHostCopy(file)).ToList())
        {
            // Some installed lists hold no row array; they place nothing.
            var kinds = (world.Fields(list.Name)["placements"] as JsonArray ?? []).OfType<JsonObject>().Select(Kind).ToList();
            int host = kinds.Count(kind => kind is not uint payload || !KeptPayloads.Contains(payload));
            if (host == 0 || host != kinds.Count)
            {
                kept++;
                if (host != 0) mixed++;
                continue;
            }
            dropped.Add(list.Name);
            foreach (var kind in kinds)
            {
                string key = kind is uint payload ? WorldFiles.HashText(payload) : "entity";
                rows[key] = rows.GetValueOrDefault(key) + 1;
            }
        }
        foreach (var container in world.Files.Where(file => file.ClassId == SMapContainer.Class).ToList())
        {
            var members = (JsonArray)WorldFiles.Field(world.Fields(container.Name), "contents");
            if (!members.Any(member => WorldFiles.Text(member?["member_tag"]) is string name && dropped.Contains(name))) continue;
            world.Edit(container.Name, (_, fields) =>
            {
                var contents = (JsonArray)WorldFiles.Field(fields, "contents");
                for (int index = contents.Count - 1; index >= 0; index--)
                    if (WorldFiles.Text(contents[index]?["member_tag"]) is string name && dropped.Contains(name)) contents.RemoveAt(index);
            });
        }
        var objects = world.Files.Where(file => file.ClassId == SAuthoredObject.Class && IsHostCopy(file) && !KeepsObject(world, file.Name))
            .Select(file => file.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var set in world.Files.Where(file => file.ClassId == SScenarioTagSet.Class && file.References.Any(x => objects.Contains(x.Target))).ToList())
            world.Edit(set.Name, (_, fields) =>
            {
                foreach (var list in fields.Select(pair => pair.Value).OfType<JsonArray>())
                    for (int index = list.Count - 1; index >= 0; index--)
                        if (WorldFiles.Text(list[index]?["tag"]) is string name && objects.Contains(name)) list.RemoveAt(index);
            });
        if (objects.Count > 0) rows["object"] = objects.Count;
        var after = world.Reached(roots);
        var removed = before.Where(name => !after.Contains(name)).Order(StringComparer.Ordinal).ToList();
        foreach (string name in removed) world.Remove(name);
        GatherSpawns(world);
        Mark(world, scenario.Name);
        return new(rows, dropped.Count, kept, mixed, removed);
    }

    /**
     * Moves each live bubble's host spawn rows onto that bubble's first spawn, where a bundle bubble anchors.
     * The host ground under the other spawns is gone, and the client never spawns a player over nothing.
     * Spawn files no live bubble reaches go to the first live bubble's anchor.
     */
    public static void GatherSpawns(WorldFiles world)
    {
        var anchors = new Dictionary<string, WorldSpawn>(StringComparer.Ordinal);
        WorldSpawn? fallback = null;
        foreach (uint bubble in WorldContent.LiveBubbles(world))
        {
            if (WorldContent.Spawns(world, bubble, hostOnly: true) is not [var anchor, ..]) continue;
            fallback ??= anchor;
            foreach (string name in SpawnFiles(world, bubble)) anchors.TryAdd(name, anchor);
        }
        if (fallback is null) return;
        // A spawn file a bundle made already stands where the bundle put it, on the bundle's ground.
        foreach (var file in world.Files.Where(file => file.ClassId == SSpawnPoints.Class && IsHostCopy(file)).ToList())
        {
            if (world.Fields(file.Name)["spawn_points"] is not JsonArray { Count: > 0 }) continue;
            var anchor = anchors.GetValueOrDefault(file.Name, fallback);
            float[] position = [anchor.Position.X, anchor.Position.Y, anchor.Position.Z];
            float[] rotation = [anchor.Rotation.X, anchor.Rotation.Y, anchor.Rotation.Z, anchor.Rotation.W];
            world.Edit(file.Name, (_, fields) =>
            {
                foreach (var row in ((JsonArray)fields["spawn_points"]!).OfType<JsonObject>())
                {
                    var p = (JsonArray)WorldFiles.Field(row, "position");
                    var r = (JsonArray)WorldFiles.Field(row, "rotation");
                    for (int i = 0; i < 3; i++) p[i] = position[i];
                    for (int i = 0; i < 4; i++) r[i] = rotation[i];
                }
            });
        }
    }

    /** The spawn point files the bubble's containers reach through project references, in reach order. */
    private static List<string> SpawnFiles(WorldFiles world, uint bubble)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(WorldContent.Containers(world, bubble).Reverse());
        var found = new List<string>();
        while (pending.TryPop(out string? name))
        {
            if (!reached.Add(name)) continue;
            var file = world[name];
            if (file.ClassId == SSpawnPoints.Class) found.Add(name);
            foreach (var reference in file.References.Where(x => x.IsProjectTag)) pending.Push(reference.Target);
        }
        return found;
    }

    /** A row's payload class; null for a row that places an entity with no payload. */
    private static uint? Kind(JsonObject row) =>
        row["payload"] is JsonObject payload && WorldFiles.Text(payload["@class"]) is string text ? WorldFiles.Hash(text) : null;

    private static void Mark(WorldFiles world, string scenarioName)
    {
        var scenario = world[scenarioName];
        var info = scenario.Info is null ? new JsonObject() : JsonNode.Parse(scenario.Info) as JsonObject ?? new JsonObject();
        info[InfoKey] = true;
        world.Replace(new TftFile
        {
            Name = scenario.Name, TagType = scenario.TagType, GameName = scenario.GameName, ClassId = scenario.ClassId,
            TypeInfo = scenario.TypeInfo, BuildHash = scenario.BuildHash, Layout = scenario.Layout, Data = scenario.Data,
            References = scenario.References, Meta = scenario.Meta, Info = info.ToJsonString(),
        });
    }
}
