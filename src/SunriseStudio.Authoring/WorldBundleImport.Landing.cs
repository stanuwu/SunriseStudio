using System.Numerics;
using System.Text.Json.Nodes;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.World;

namespace SunriseStudio.Authoring;

/**
 * One Director landing zone a bundle bubble asks for.
 * @param BubbleHash The destination row's own name hash; the client finds the slice set by it.
 * @param SpawnSetHash The bubble's named spawn set the zone lands on.
 * @param Position Where that spawn point stands in the destination, the zone's place on the map.
 */
public sealed record BundleLandingZone(string Name, uint BubbleHash, uint SpawnSetHash, Vector3 Position);

public static partial class WorldBundleImport
{
    /**
     * The landing zones of a placed bundle: one per entry of a bubble's `landings`, on that bubble's named
     * spawn set. Bundle bubble i is the destination's i-th live bubble, as PlaceBundle placed it.
     * @return The zones in bundle order; empty when no bubble asks for one.
     */
    public static IReadOnlyList<BundleLandingZone> LandingZones(StudioProject project, string destination, string bundleFolder)
    {
        var bundle = JsonNode.Parse(File.ReadAllText(Path.Combine(bundleFolder, "bundle.json")))!.AsObject();
        var names = ((JsonArray)bundle["bubbles"]!).Select(name => (string)name!).ToArray();
        var world = WorldFiles.Load(project.ContentDirectory, destination);
        var live = WorldContent.LiveBubbles(world);
        if (names.Length > live.Count)
            throw new InvalidDataException($"The bundle has {names.Length} bubbles; the destination has {live.Count} with states.");
        var placed = BundleSpawnRows(world);
        var zones = new List<BundleLandingZone>();
        for (int index = 0; index < names.Length; index++)
        {
            var bubble = ReadBubble(Path.Combine(bundleFolder, names[index]));
            var landings = (bubble["landings"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            if (landings.Count == 0) continue;
            var sets = WorldBubbles.SpawnSets(world, live[index]);
            foreach (var landing in landings)
            {
                string title = (string)landing["name"]!, spawn = (string)landing["spawn"]!;
                uint set = WorldContent.BundleSpawnSet(names[index], spawn);
                // The server finds a teleport's set among the bubble's own sets; another bubble's set lands nowhere.
                if (!placed.TryGetValue(set, out var position) || !sets.Contains(set))
                    throw new InvalidDataException($"{names[index]}: spawn point {spawn} is not a placed spawn set of its bubble; place the bundle first.");
                zones.Add(new(title, live[index], set, position));
            }
        }
        return zones;
    }

    /** Where each spawn set a bundle placed stands, by set hash. */
    private static Dictionary<uint, Vector3> BundleSpawnRows(WorldFiles world)
    {
        var rows = new Dictionary<uint, Vector3>();
        foreach (var file in world.Files.Where(file => file.ClassId == SSpawnPoints.Class && !WorldStrip.IsHostCopy(file)))
            foreach (var row in (world.Fields(file.Name)["spawn_points"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var p = (JsonArray)WorldFiles.Field(row, "position");
                rows.TryAdd(WorldFiles.Hash(WorldFiles.Field(row, "spawn_name_identity")),
                    new(WorldFiles.Float(p[0]), WorldFiles.Float(p[1]), WorldFiles.Float(p[2])));
            }
        return rows;
    }
}
