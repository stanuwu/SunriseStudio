using System.Numerics;
using System.Text.Json.Nodes;
using SunriseStudio.Animation;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.World;

namespace SunriseStudio.Authoring;

public static partial class WorldBundleImport
{
    /**
     * Places the `entities` of one bundle bubble: each is a skinned GLB with named actions, built as
     * a new animated entity and placed in the bubble's own container. An entity with a `device`
     * field is built as a device and bound to the slot the field names.
     * @param packageId An unused package id for the entity models.
     * @param sounds The bundle's own sounds by name, from PlaceSounds; an event may name one.
     * @return How many entities were placed.
     */
    public static int PlaceEntities(StudioProject project, PackageCatalog catalog, RegistryIndex schema, string destination,
        uint bubbleNameHash, string bubbleFolder, ushort packageId, CancellationToken token = default,
        IReadOnlyDictionary<string, uint>? sounds = null)
    {
        var bubble = ReadBubble(bubbleFolder);
        if (bubble["entities"] is not JsonArray { Count: > 0 } items) return 0;
        var world = WorldFiles.Load(project.ContentDirectory, destination);
        var (turn, place) = Anchor(world, bubbleNameHash, bubble);
        int placed = 0;
        foreach (var item in items.OfType<JsonObject>())
        {
            token.ThrowIfCancellationRequested();
            string name = $"{destination}_{(string)bubble["name"]!}_{(string)item["name"]!}";
            var device = item["device"] as JsonObject;
            if ((device is null) == (item["plays"] is null))
                throw new InvalidDataException($"Entity {name} needs either plays or device.");
            var input = RiggedEntityGltf.Read(Path.Combine(bubbleFolder, (string)item["glb"]!), name, (string?)item["plays"] ?? "");
            if (item["events"] is JsonArray events) input = WithEvents(input, catalog, events, sounds);
            if ((string?)item["material"] is { } material)
                input = input with { Material = Convert.ToUInt32(material.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? material[2..] : material, 16) };
            var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, turn) * Rotation(item["rotation"]);
            var spot = new WorldEntitySpot(destination, bubbleNameHash, Vector3.Transform(Vector(item["position"]), place), rotation,
                (float?)item["scale"] ?? 1);
            if (device is null) WorldEntities.Place(project, catalog, schema, world, input, spot, packageId, token);
            else
                WorldEntities.PlaceDevice(project, catalog, schema, world, input, spot, packageId, (string)device["slot"]!,
                    (string)device["position"]!, token);
            placed++;
        }
        world.SaveChanged(project.ContentDirectory);
        return placed;
    }

    /**
     * The input with each listed event on the clip of its action. A sound is one of the bundle's own
     * by name, or an installed tag in hex; anything else is refused.
     */
    private static RiggedEntityInput WithEvents(RiggedEntityInput input, PackageCatalog catalog, JsonArray events,
        IReadOnlyDictionary<string, uint>? sounds)
    {
        var byAction = new Dictionary<string, List<ClipEvent>>(StringComparer.Ordinal);
        foreach (var item in events.OfType<JsonObject>())
        {
            string action = (string)item["action"]!;
            var clip = input.Clips.FirstOrDefault(value => value.Name == action)
                ?? throw new InvalidDataException($"Entity {input.Name} has no action named {action}.");
            int frame = (int)item["frame"]!;
            if (frame < 0 || frame >= clip.Bones[0].Count)
                throw new InvalidDataException($"Entity {input.Name}: action {action} has no frame {frame}.");
            bool sound = item["sound"] is not null;
            if (sound == (item["effect"] is not null))
                throw new InvalidDataException($"Entity {input.Name}: an event needs either sound or effect.");
            string text = (string)item[sound ? "sound" : "effect"]!;
            if (!(sound && sounds is not null && sounds.TryGetValue(text, out uint target)))
            {
                string digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
                uint expected = sound ? SWwiseEvent.Class : SEntity.Class;
                if (!uint.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out target)
                    || !catalog.TryGetEntry(target, out var entry) || entry.Reference != expected)
                    throw new InvalidDataException($"Entity {input.Name}: {text} is not {(sound ? "a bundle sound or an installed sound event" : "an installed entity")}.");
            }
            if (!byAction.TryGetValue(action, out var list)) byAction[action] = list = [];
            list.Add(ClipEvent.NewTagged(frame, target, detached: (bool?)item["detached"] ?? false));
        }
        return input with
        {
            Clips = input.Clips.Select(clip => byAction.TryGetValue(clip.Name, out var list) ? clip with { Events = list } : clip).ToArray(),
        };
    }
}
