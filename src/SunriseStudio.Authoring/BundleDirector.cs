using System.Numerics;
using System.Text.Json.Nodes;
using SunriseStudio.Projects;
using SunriseStudio.World;

namespace SunriseStudio.Authoring;

/**
 * The Director setup a placed bundle gives.
 * @param Zones One zone per landing, in bundle order.
 * @param MapOrigin The map's top-left corner in the destination, or null to fit the map to the zones.
 * @param MapSize The map's width and height in metres, or null with MapOrigin.
 * @param MapFile The bundle's map PNG, or null.
 * @param PlanetFile The bundle's planet PNG, or null.
 * @param PlanetSize The bundle's planet size, or null.
 */
public sealed record BundleDirectorSetup(IReadOnlyList<BundleLandingZone> Zones, Vector2? MapOrigin, Vector2? MapSize,
    string? MapFile, string? PlanetFile, float? PlanetSize);

/** Reads the Director fields of a placed bundle: its landings, `map` and `planet`. */
public static class BundleDirector
{
    // Bubbles that anchor on their own spawn points all land on one place; zones that far apart stay readable.
    private const float ZoneSpacing = 30;

    /**
     * The Director setup of a bundle placed in a project destination. In a bundle without a shared space
     * the zones sit on a grid, since every bubble lands on the same anchor.
     * @param bundleFolder The folder that holds bundle.json.
     * @return The setup; no zones when no bubble has landings.
     * @throws InvalidDataException When `map` has no shared space or no landing to place it by.
     */
    public static BundleDirectorSetup Read(StudioProject project, string destination, string bundleFolder)
    {
        var zones = WorldBundleImport.LandingZones(project, destination, bundleFolder);
        var bundle = JsonNode.Parse(File.ReadAllText(Path.Combine(bundleFolder, "bundle.json")))!.AsObject();
        bool shared = (string?)bundle["space"] == "shared";
        if (!shared) zones = Spread(zones);
        Vector2? origin = null, size = null;
        string? mapFile = null;
        if (bundle["map"] is JsonObject map)
        {
            if (!shared) throw new InvalidDataException("bundle.json: map needs space shared.");
            if (zones.Count == 0) throw new InvalidDataException("bundle.json: map needs a landing to place it by.");
            var shift = Shift(bundleFolder, bundle, zones[0]);
            Vector2 low = Pair(map["min"]) + shift, high = Pair(map["max"]) + shift;
            // The map's top edge is its largest y.
            origin = new(low.X, high.Y);
            size = high - low;
            if ((string?)map["png"] is { } png) mapFile = Path.Combine(bundleFolder, png);
        }
        var planet = bundle["planet"] as JsonObject;
        string? planetFile = (string?)planet?["png"] is { } file ? Path.Combine(bundleFolder, file) : null;
        return new(zones, origin, size, mapFile, planetFile, (float?)planet?["size"]);
    }

    /** The zones on a square grid around their own places, ZoneSpacing apart, first row at the top. */
    private static IReadOnlyList<BundleLandingZone> Spread(IReadOnlyList<BundleLandingZone> zones)
    {
        int columns = (int)MathF.Ceiling(MathF.Sqrt(Math.Max(1, zones.Count)));
        float middle = (columns - 1) / 2f;
        return zones.Select((zone, index) => zone with
        {
            Position = zone.Position + new Vector3((index % columns - middle) * ZoneSpacing, (middle - index / columns) * ZoneSpacing, 0),
        }).ToArray();
    }

    /**
     * How far a shared bundle space moved on X and Y: the zone's place in the destination minus its
     * spawn point's place in the bundle. A shared space never turns.
     */
    private static Vector2 Shift(string bundleFolder, JsonObject bundle, BundleLandingZone zone)
    {
        foreach (string bubble in ((JsonArray)bundle["bubbles"]!).Select(name => (string)name!))
        {
            var spawns = JsonNode.Parse(File.ReadAllText(Path.Combine(bundleFolder, bubble, "bubble.json")))!["spawn_points"] as JsonArray ?? [];
            foreach (var spawn in spawns.OfType<JsonObject>())
                if ((string?)spawn["name"] is { } name && WorldContent.BundleSpawnSet(bubble, name) == zone.SpawnSetHash)
                    return new Vector2(zone.Position.X, zone.Position.Y) - Pair(spawn["position"]);
        }
        throw new InvalidDataException($"No bundle spawn point gives landing {zone.Name}.");
    }

    private static Vector2 Pair(JsonNode? node) => new((float)node![0]!, (float)node[1]!);
}
