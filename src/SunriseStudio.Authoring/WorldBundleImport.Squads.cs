using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Tft;
using SunriseStudio.World;
using SunriseStudio.World.Sdk;

namespace SunriseStudio.Authoring;

public static partial class WorldBundleImport
{
    // The objects the bundle's squads live on: this name, then _ and the bundle bubble's index.
    private const string SquadOwner = "bundle_squads";
    // The combat box reaches this far past the outermost squad, below the lowest one, and above it.
    private const float CombatMargin = 40, CombatDepth = 10, CombatHeadroom = 10;

    /**
     * Places the `squads` of every bubble: each is a new squad a mission script places by name.
     * A squad gives a name, an installed enemy as a hex tag or a bundle creature as creature:name, a count and a position in bundle space.
     * @param bubbles Each bundle bubble's name hash in the destination and its bubble.json.
     * @return How many squads were added.
     */
    private static int PlaceSquads(StudioProject project, PackageCatalog catalog, RegistryIndex schema, string destination,
        IReadOnlyList<(uint NameHash, JsonObject Bubble)> bubbles, IReadOnlyDictionary<string, string> creatures, CancellationToken token)
    {
        if (!bubbles.Any(bubble => bubble.Bubble["squads"] is JsonArray { Count: > 0 })) return 0;
        var world = WorldFiles.Load(project.ContentDirectory, destination);
        uint scenario = WorldTemplate.SourceScenario(project.ContentDirectory, schema, destination)
            ?? throw new InvalidDataException($"Destination {destination} names no source scenario.");
        string installedPack = Path.Combine(catalog.GameRoot, "bin", "x64", "Sunrise", ModActivitySdk.FileName);
        var templates = WorldSquadAuthoring.InlineTemplates(installedPack, catalog, scenario);
        if (templates.Count == 0) throw new InvalidDataException("The source scenario has no squad spawner to copy.");
        int total = 0;
        for (int index = 0; index < bubbles.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var (nameHash, bubble) = bubbles[index];
            if (bubble["squads"] is not JsonArray list || list.Count == 0) continue;
            var (_, place) = Anchor(world, nameHash, bubble);
            var squads = list.OfType<JsonObject>().Select(item => new NewSquad((string)item["name"]!, SquadEntity((string)item["entity"]!, creatures),
                (int?)item["count"] ?? 1, Vector3.Transform(Vector(item["position"]), place))).ToList();
            // Each bubble's squads get their own owner, which loads only with that bubble; a shared owner
            // would exist once per bubble on the server.
            string owner = WorldSquadAuthoring.AddOwner(world, schema, catalog, destination, $"{SquadOwner}_{index}", token: token);
            int row = WorldBubbles.ScenarioRow(world, nameHash);
            // The owner's block names the bubble its squads load in; the copy still names its template's.
            world.Edit(owner, (_, fields) => ((JsonObject)((JsonArray)WorldFiles.Field(fields, "entry_filters"))[0]!)["bubble_index"] = row);
            WorldSquadAuthoring.AddMany(world, schema, catalog, destination, templates[0], squads, owner, token);
            // Squads only move inside a firing area of the objective a script assigns them; the box covers the bubble's.
            var middle = squads.Aggregate(Vector3.Zero, (sum, squad) => sum + squad.Position) / squads.Count;
            float floor = squads.Min(squad => squad.Position.Z) - CombatDepth;
            float reach = squads.Max(squad => MathF.Max(MathF.Abs(squad.Position.X - middle.X), MathF.Abs(squad.Position.Y - middle.Y))) + CombatMargin;
            WorldSquadAuthoring.AddCombatArea(world, schema, catalog, destination, owner, middle with { Z = floor }, reach, 2 * CombatDepth + CombatHeadroom, token);
            WorldSquadAuthoring.LoadOnlyIn(world, owner, row);
            total += squads.Count;
        }
        world.SaveChanged(project.ContentDirectory);
        return total;
    }

    private static uint Hex(string text) =>
        uint.Parse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
