using System.Numerics;
using System.Text.Json.Nodes;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.World;

namespace SunriseStudio.Authoring;

/** One static a bundle bubble placed in a destination: the project model and the world files that place it. */
public sealed record PlacedBundleStatic(string Name, uint ModelTag, string Container, IReadOnlyList<string> Files,
    Vector3 Position, Quaternion Rotation);

/**
 * Places the statics of one World Import Bundle bubble (`bubble.json` plus glb) in a bubble of a
 * project destination. The bundle's first spawn point lands on the destination bubble's first spawn
 * point, facing the same way, so the bundle reads around where the player arrives.
 */
public static class WorldBundleImport
{
    /**
     * @param bubbleFolder The bundle folder holding bubble.json.
     * @param packageId An unused package id for the imported models.
     * @return One row per placed static.
     */
    public static IReadOnlyList<PlacedBundleStatic> PlaceBubble(StudioProject project, PackageCatalog catalog, RegistryIndex schema,
        string destination, uint bubbleNameHash, string bubbleFolder, ushort packageId)
    {
        var bubble = JsonNode.Parse(File.ReadAllText(Path.Combine(bubbleFolder, "bubble.json")))!.AsObject();
        var world = WorldFiles.Load(project.ContentDirectory, destination);
        var anchor = WorldContent.Spawns(world, bubbleNameHash).FirstOrDefault()
            ?? throw new InvalidDataException($"Bubble 0x{bubbleNameHash:X8} reaches no spawn point to anchor the bundle to.");
        var spawn = (bubble["spawn_points"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()
            ?? throw new InvalidDataException("The bundle bubble has no spawn point to anchor it with.");
        // Facing is degrees about +Z from +X; the stock spawn is a turn about +Z.
        float turn = 2 * MathF.Atan2(anchor.Rotation.Z, anchor.Rotation.W) - (float)(spawn["facing"] ?? 0.0) * MathF.PI / 180;
        var place = Matrix4x4.CreateTranslation(-Vector(spawn["position"])) * Matrix4x4.CreateRotationZ(turn)
            * Matrix4x4.CreateTranslation(anchor.Position);
        var containers = WorldContent.Containers(world, bubbleNameHash);
        var placed = new List<PlacedBundleStatic>();
        foreach (var item in ((JsonArray)bubble["statics"]!).OfType<JsonObject>())
        {
            string name = $"{destination}_{(string)bubble["name"]!}_{(string?)item["name"] ?? Path.GetFileNameWithoutExtension((string)item["glb"]!)}";
            if (project.AddedAssets.Any(asset => asset.Name == name))
                throw new InvalidOperationException($"The project already holds {name}; remove it before placing the bundle again.");
            var model = MeshAssetImporter.ImportFile(project, catalog, Path.Combine(bubbleFolder, (string)item["glb"]!), name, packageId);
            byte[] modelData = project.ReadAddedAsset(model.ModelAsset.Id, catalog);
            var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, turn) * Rotation(item["rotation"]);
            var position = Vector3.Transform(Vector(item["position"]), place);
            float scale = (float?)item["scale"] ?? 1;
            var (container, tags) = Template(world, catalog, containers, model.ModelTag, modelData, position, rotation, scale,
                $"{destination}/{bubbleNameHash:X8}/{name}");
            var files = WorldContent.AddTags(world, schema, catalog, destination,
                tags.Select(tag => new WorldTag(tag.Tag, tag.Class, tag.TypeInfo, tag.Data)).ToArray());
            // The list is the last tag; joining it is what makes the bubble load the static.
            WorldContent.Join(world, container, files[^1]);
            placed.Add(new(name, model.ModelTag, container, files, position, rotation));
        }
        world.SaveChanged(project.ContentDirectory);
        return placed;
    }

    /** The first container of the bubble whose installed source holds a static placement to copy. */
    private static (string Container, IReadOnlyList<PlacedStaticTag> Tags) Template(WorldFiles world, PackageCatalog catalog,
        IReadOnlyList<string> containers, uint model, byte[] modelData, Vector3 position, Quaternion rotation, float scale, string identity)
    {
        foreach (string container in containers)
        {
            if (world[container].SourceTag is not uint source) continue;
            try { return (container, StaticPlacement.Build(catalog, source, model, modelData, position, rotation, scale, identity)); }
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
