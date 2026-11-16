using System.Numerics;
using System.Text.Json.Nodes;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.World;

namespace SunriseStudio.Authoring;

public static partial class WorldBundleImport
{
    // The objects the bundle's doors live on: this name, then _2, _3 and so on, one per bubble and MaximumDoorsPerOwner doors.
    private const string DoorOwner = "bundle_doors";

    /**
     * Places the `doors` of every bubble: each is a new box trigger a mission script arms and hears
     * as a player trigger. Every door gets its own Havok box shape in the project package.
     * @param bubbles Each bundle bubble's name hash in the destination and its bubble.json.
     * @return How many doors were placed.
     */
    private static int PlaceDoors(StudioProject project, PackageCatalog catalog, RegistryIndex schema, string destination,
        IReadOnlyList<(uint NameHash, JsonObject Bubble)> bubbles, ushort packageId, CancellationToken token)
    {
        if (!bubbles.Any(bubble => bubble.Bubble["doors"] is JsonArray { Count: > 0 })) return 0;
        var world = WorldFiles.Load(project.ContentDirectory, destination);
        // A door's trigger and volume load only with their bubble, so each bubble's doors get their own owners.
        var batches = new List<(int Row, NewDoor[] Doors)>();
        foreach (var (nameHash, bubble) in bubbles)
        {
            if (bubble["doors"] is not JsonArray list || list.Count == 0) continue;
            var (turn, place) = Anchor(world, nameHash, bubble);
            var doors = list.OfType<JsonObject>().Select(item => new NewDoor((string)item["name"]!, Vector3.Transform(Vector(item["position"]), place),
                Vector(item["size"]), turn + ((float?)item["yaw"] ?? 0f) * MathF.PI / 180)).ToArray();
            int row = WorldBubbles.ScenarioRow(world, nameHash);
            batches.AddRange(doors.Chunk(WorldSquadAuthoring.MaximumDoorsPerOwner).Select(chunk => (row, chunk)));
        }
        uint shapeTemplate = TriggerShapeWriter.TemplateFor(4);
        if (!catalog.TryGetEntry(shapeTemplate, out var template)) throw new InvalidDataException("The trigger shape template is not installed.");
        byte[] templateBytes = catalog.Read(shapeTemplate);
        int entry = project.NextEntry(packageId);
        var shapes = new List<ProjectAssetAddition>();
        for (int batch = 0; batch < batches.Count; batch++)
        {
            var (row, doors) = batches[batch];
            string owner = WorldSquadAuthoring.AddOwner(world, schema, catalog, destination, batch == 0 ? DoorOwner : $"{DoorOwner}_{batch + 1}", token: token);
            int next = 0;
            WorldSquadAuthoring.AddDoors(world, schema, catalog, destination, owner, doors, (corners, height) =>
            {
                if (entry >= TagHashes.EntriesPerPackage) throw new InvalidDataException("The bundle's doors need a free package entry each.");
                string name = doors[next++].Name;
                shapes.Add(new($"{destination}.door.{name}.trigger_shape", TriggerShapeWriter.Polygon(templateBytes, corners, height),
                    new(packageId, template.Reference, template.TypeInfo, null, entry), shapeTemplate));
                return TagHashes.Make(packageId, entry++);
            }, token, row);
        }
        project.AddAssets(shapes, catalog);
        world.SaveChanged(project.ContentDirectory);
        return batches.Sum(batch => batch.Doors.Length);
    }
}
