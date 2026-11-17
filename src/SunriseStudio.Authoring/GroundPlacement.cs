using System.Globalization;
using System.Text.Json.Nodes;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Graph;
using SunriseStudio.Tags.Tft;
using SunriseStudio.World;

namespace SunriseStudio.Authoring;

/**
 * Where a ground sits in a project destination: the bubble, the container its lists join, the AO map
 * file that takes its rows, and the installed terrain it copies its layout from.
 * @param OriginalBuffer The AO buffer header the map named before the ground.
 */
public sealed record GroundSite(string Destination, uint BubbleNameHash, string Container, string OcclusionMap,
    uint OriginalBuffer, int FirstIndex, uint TerrainTemplate, uint BoundsTemplate);

/**
 * Places grounds in a bubble of a project destination through its world files. The ground adds its
 * own terrain and collision lists, and its rows go in the bubble's own AO map copy. No installed tag
 * changes; an installed bubble is refused, since its containers and AO map are shared.
 */
public static class GroundPlacement
{
    // Fields of the terrain, AO and collision placement rows and of the AO map.
    private const string Placements = "placements", Payload = "payload", PayloadClass = "@class";
    private const string TerrainField = "terrain", KeyField = "ambient_occlusion_key", BoundsField = "unk_1c";
    private const string MapField = "occlusion_map", BufferField = "scalar_buffer_header", RowsField = "offsets";
    private const string RowKey = "instance_key", RowOffset = "base_byte_offset", Contents = "contents", Member = "member_tag";

    /** The terrain list a ground adds. */
    public static string TerrainList(string destination, string ground) => ListFile(destination, $"{destination}_{ground}_terrain");

    /** The collision list a ground adds. */
    public static string CollisionList(string destination, string ground) => ListFile(destination, CollisionName(destination, ground));

    private static string CollisionName(string destination, string ground) => $"{destination}_{ground}_collision";

    private static string ListFile(string destination, string name) =>
        $"{WorldTemplate.Folder}/{destination}/map_placements/{name}.map_placements";

    /**
     * Finds the site for a new ground in a bubble and gives the bubble its own AO map file when it
     * names an installed one. Only `world` changes; the caller saves it.
     * @param usedIndices Terrain indices other grounds of the project already take in that map.
     */
    public static GroundSite Prepare(WorldFiles world, PackageCatalog catalog, RegistryIndex schema, string destination,
        uint bubbleNameHash, IReadOnlyCollection<int> usedIndices)
    {
        var containers = WorldBubbles.OwnFirst(world, bubbleNameHash);
        if (containers.Count == 0) throw new InvalidDataException($"Bubble 0x{bubbleNameHash:X8} has no container in this destination.");
        var (terrain, bounds) = TerrainTemplate(world, catalog);
        var maps = containers.SelectMany(container => Members(world, container)).Distinct()
            .Where(list => world.Contains(list) && world[list].ClassId == SMapPlacements.Class)
            .SelectMany(list => Rows(world, list)).Where(row => Is(row, SAmbientOcclusionPayload.Class))
            .Select(row => WorldFiles.Text(row[Payload]![MapField])).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        if (maps.Length != 1) throw new InvalidDataException($"Bubble 0x{bubbleNameHash:X8} names {maps.Length} AO maps; a ground needs exactly one.");
        string map = maps[0].StartsWith('#') ? OwnMap(world, catalog, schema, destination, maps[0]) : maps[0];
        var fields = world.Fields(map);
        uint buffer = (uint)Integer(WorldFiles.Field(fields, BufferField));
        if (!catalog.TryGetEntry(buffer, out _) || !catalog.ContainsInstalledPackage(TagHashes.Split(buffer).PackageId))
            throw new InvalidDataException($"The AO map {map} already takes another ground's rows; one ground per AO map.");
        int next = ((JsonArray)WorldFiles.Field(fields, RowsField)).OfType<JsonObject>().Select(row => Integer(row[RowKey]))
            .Where(key => key >> 32 == GroundTerrain.KeyTag).Select(key => (int)(key & 0xFFFF)).Concat(usedIndices).DefaultIfEmpty(-1).Max() + 1;
        return new(destination, bubbleNameHash, containers[0], map, buffer, next, terrain, bounds);
    }

    /**
     * Writes a new ground's lists and AO rows into the world.
     * @param terrains Each patch's index, STerrain and bounds tags.
     * @param rows The AO rows: key and byte offset in the ground's AO buffer.
     */
    public static void Place(WorldFiles world, GroundSite site, string ground, IReadOnlyList<(int Index, uint Terrain, uint Bounds)> terrains,
        uint collisionOwner, IReadOnlyList<(ulong Key, uint Offset)> rows, uint buffer)
    {
        string terrainList = TerrainList(site.Destination, ground);
        if (world.Contains(terrainList) || world.Contains(CollisionList(site.Destination, ground)))
            throw new InvalidOperationException($"The destination already holds lists for {ground}.");
        var template = world.Files.Where(file => file.ClassId == SMapPlacements.Class)
            .First(file => Rows(world, file.Name).Any(row => Is(row, STerrainPayload.Class) && TemplateOf(world, row) == site.TerrainTemplate));
        var json = TftText.ToJson(template, out string? fallback);
        if (fallback is not null) throw new InvalidDataException($"{template.Name} has no field view ({fallback}).");
        json["name"] = terrainList;
        json["info"] = null;
        var list = (JsonArray)WorldFiles.Field((JsonObject)json["fields"]!, Placements);
        var model = (JsonObject)list.OfType<JsonObject>().First(row => Is(row, STerrainPayload.Class)).DeepClone();
        list.Clear();
        foreach (var (index, terrain, bounds) in terrains)
        {
            var row = (JsonObject)model.DeepClone();
            var payload = (JsonObject)row[Payload]!;
            Set(payload, TerrainField, terrain, 4);
            Set(payload, BoundsField, bounds, 4);
            Set(payload, KeyField, GroundTerrain.RegistryKey(index), 8);
            list.Add(row);
        }
        world.Add(TftText.FromJson(json));
        WorldContent.Join(world, site.Container, terrainList);
        WorldContent.AddPayloadList(world, site.Destination, CollisionName(site.Destination, ground), SStaticCollisionPayload.Class,
            "collision_owner", collisionOwner, site.Container);
        world.Edit(site.OcclusionMap, (_, fields) =>
        {
            Set(fields, BufferField, buffer, 4);
            var offsets = (JsonArray)WorldFiles.Field(fields, RowsField);
            var model = offsets.OfType<JsonObject>().FirstOrDefault() ?? new JsonObject { [RowKey] = 0UL, [RowOffset] = 0U };
            foreach (var (key, offset) in rows)
            {
                var row = (JsonObject)model.DeepClone();
                Set(row, RowKey, key, 8);
                Set(row, RowOffset, offset, 4);
                offsets.Add(row);
            }
        });
    }

    /**
     * Takes a ground out of the world: its lists leave the container and the world, and the AO map
     * drops its rows and names its original buffer again.
     * @return The list files removed; the caller deletes them on disk.
     */
    public static IReadOnlyList<string> Remove(WorldFiles world, GroundSite site, string ground, IReadOnlyCollection<ulong> keys)
    {
        string[] lists = [TerrainList(site.Destination, ground), CollisionList(site.Destination, ground)];
        world.Edit(site.Container, (_, fields) =>
        {
            var members = (JsonArray)WorldFiles.Field(fields, Contents);
            foreach (var member in members.OfType<JsonObject>().Where(row => lists.Contains(WorldFiles.Text(row[Member]))).ToList())
                members.Remove(member);
        });
        foreach (string list in lists.Where(world.Contains)) world.Remove(list);
        world.Edit(site.OcclusionMap, (_, fields) =>
        {
            Set(fields, BufferField, site.OriginalBuffer, 4);
            var offsets = (JsonArray)WorldFiles.Field(fields, RowsField);
            foreach (var row in offsets.OfType<JsonObject>().Where(row => keys.Contains(Integer(row[RowKey]))).ToList()) offsets.Remove(row);
        });
        return lists;
    }

    /** The STerrain and bounds table of the first terrain row the world places from installed tags. */
    private static (uint Terrain, uint Bounds) TerrainTemplate(WorldFiles world, PackageCatalog catalog)
    {
        foreach (var file in world.Files.Where(file => file.ClassId == SMapPlacements.Class))
            foreach (var row in Rows(world, file.Name).Where(row => Is(row, STerrainPayload.Class)))
            {
                uint terrain = TemplateOf(world, row);
                if (Installed(world, row[Payload]![BoundsField]) is not uint bounds) continue;
                if (catalog.TryGetEntry(terrain, out var entry) && entry.Reference == STerrain.Class
                    && catalog.ContainsInstalledPackage(entry.PackageId)
                    && catalog.TryGetEntry(bounds, out var box) && box.Reference == SInstanceBoundsTable.Class)
                    return (terrain, bounds);
            }
        throw new InvalidDataException("The destination places no installed terrain to copy a layout from.");
    }

    /** The installed tag a reference names: itself, or the source of the world file it names. */
    private static uint? Installed(WorldFiles world, JsonNode? node) =>
        WorldFiles.Text(node) is { } text && !text.StartsWith('#') ? world.Contains(text) ? world[text].SourceTag ?? world[text].CopyOf : null
            : (uint)Integer(node);

    private static uint TemplateOf(WorldFiles world, JsonObject row) => Installed(world, row[Payload]![TerrainField]) ?? 0;

    /** Copies an installed AO map into the world and points every list that names it at the copy. */
    private static string OwnMap(WorldFiles world, PackageCatalog catalog, RegistryIndex schema, string destination, string installed)
    {
        uint tag = (uint)Integer(JsonValue.Create(installed));
        string type = WorldTemplate.TypeName(schema, SOcclusionMap.Class);
        string file = $"{WorldTemplate.Folder}/{destination}/{type}/{tag:x8}_ground.{type}";
        if (!world.Contains(file)) world.Add(TftClone.Clone([tag], new CatalogTagSource(catalog), schema, _ => file).Single());
        foreach (string list in world.Files.Where(x => x.ClassId == SMapPlacements.Class).Select(x => x.Name).ToList())
        {
            if (!Rows(world, list).Any(row => Names(row, installed))) continue;
            world.Edit(list, (_, fields) =>
            {
                foreach (var row in ((JsonArray)WorldFiles.Field(fields, Placements)).OfType<JsonObject>())
                    if (Names(row, installed)) row[Payload]![MapField] = file;
            });
        }
        return file;
    }

    private static bool Names(JsonObject row, string map) =>
        Is(row, SAmbientOcclusionPayload.Class) && WorldFiles.Text(row[Payload]![MapField]) == map;

    private static IEnumerable<string> Members(WorldFiles world, string container) =>
        (world.Fields(container)[Contents] as JsonArray ?? []).OfType<JsonObject>().Select(row => WorldFiles.Text(row[Member])).OfType<string>();

    /** The rows of a list; a list with no field view has none here. */
    private static IEnumerable<JsonObject> Rows(WorldFiles world, string list)
    {
        JsonArray? rows;
        try { rows = world.Fields(list)[Placements] as JsonArray; }
        catch (InvalidDataException) { rows = null; }
        return (rows ?? []).OfType<JsonObject>();
    }

    private static bool Is(JsonObject row, uint payloadClass) =>
        row[Payload] is JsonObject payload && WorldFiles.Text(payload[PayloadClass]) == WorldFiles.HashText(payloadClass);

    /** A text-form integer: a number, hex with `0x`, or an installed reference `#0x`. */
    private static ulong Integer(JsonNode? node)
    {
        if (node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.Number) return value.GetValue<ulong>();
        string text = (WorldFiles.Text(node) ?? throw new InvalidDataException("An integer field is missing.")).TrimStart('#');
        return text.StartsWith("0x", StringComparison.Ordinal)
            ? ulong.Parse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
            : throw new InvalidDataException($"\"{text}\" is not an integer.");
    }

    /** Writes an integer in the form the field holds: a number, hex of `bytes`, or else an installed reference. */
    private static void Set(JsonObject target, string field, ulong value, int bytes)
    {
        string? text = WorldFiles.Text(WorldFiles.Field(target, field));
        string digits = value.ToString("X" + bytes * 2, CultureInfo.InvariantCulture);
        target[field] = text is null ? JsonValue.Create(value) : text.StartsWith("0x", StringComparison.Ordinal) ? "0x" + digits : (JsonNode)("#0x" + digits);
    }
}
