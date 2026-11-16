using System.Numerics;
using System.Text.Json.Nodes;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.World;

namespace SunriseStudio.Authoring;

public static partial class WorldBundleImport
{
    /**
     * Places the trigger volumes one bundle bubble lists, each a player trigger of the source activity.
     * Alone it moves and keeps its shape; a `size` makes a new box, and `corners` with a `height` a
     * new prism over that convex floor.
     * @param packageId The project package that takes the new shapes.
     * @return How many volumes were placed.
     */
    public static int PlaceVolumes(StudioProject project, PackageCatalog catalog, RegistryIndex schema, string destination,
        uint bubbleNameHash, string bubbleFolder, ushort packageId, CancellationToken token = default)
    {
        var bubble = ReadBubble(bubbleFolder);
        if (bubble["volumes"] is not JsonArray { Count: > 0 } items) return 0;
        var world = WorldFiles.Load(project.ContentDirectory, destination);
        var (turn, place) = Anchor(world, bubbleNameHash, bubble);
        int placed = 0;
        foreach (var item in items.OfType<JsonObject>())
        {
            token.ThrowIfCancellationRequested();
            string trigger = (string)item["trigger"]!;
            var floor = Vector3.Transform(Vector(item["position"]), place);
            if (item["size"] is not null && item["corners"] is not null)
                throw new InvalidDataException($"Volume {trigger} gives both size and corners.");
            if (item["size"] is null && item["corners"] is null)
                WorldVolumes.Move(world, schema, catalog, destination, trigger, floor, token);
            else
            {
                // The floor turns about its position: the bundle's turn, then the volume's own.
                float yaw = turn + ((float?)item["yaw"] ?? 0f) * MathF.PI / 180;
                var spin = Matrix4x4.CreateRotationZ(yaw);
                Vector3 Corner(float x, float y) => floor + Vector3.Transform(new Vector3(x, y, 0), spin);
                Vector3[] corners;
                float height;
                if (item["size"] is not null)
                {
                    var size = Vector(item["size"]);
                    height = size.Z;
                    corners = [Corner(-size.X / 2, -size.Y / 2), Corner(size.X / 2, -size.Y / 2), Corner(size.X / 2, size.Y / 2), Corner(-size.X / 2, size.Y / 2)];
                }
                else
                {
                    height = (float?)item["height"] ?? throw new InvalidDataException($"Volume {trigger} gives corners and no height.");
                    corners = item["corners"]!.AsArray().Select(point => Corner((float)point![0]!, (float)point[1]!)).ToArray();
                }
                uint templateTag = TriggerShapeWriter.TemplateFor(corners.Length);
                if (!catalog.TryGetEntry(templateTag, out var template))
                    throw new InvalidDataException("The trigger shape template is not installed.");
                byte[] shape = TriggerShapeWriter.Polygon(catalog.Read(templateTag), corners, height);
                int entry = project.NextEntry(packageId);
                if (entry >= TagHashes.EntriesPerPackage) throw new InvalidDataException("The bundle's volumes need a free package entry each.");
                project.AddAssets([new($"{destination}.{trigger}.trigger_shape", shape,
                    new(packageId, template.Reference, template.TypeInfo, null, entry), templateTag)], catalog);
                WorldVolumes.Reshape(world, schema, catalog, destination, trigger, TagHashes.Make(packageId, entry), corners, height, token);
            }
            placed++;
        }
        world.SaveChanged(project.ContentDirectory);
        return placed;
    }
}
