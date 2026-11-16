using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.World;
using SunriseStudio.World.Entities;

namespace SunriseStudio.Authoring;

public static partial class WorldBundleImport
{
    // The client's physics world is the union of the third box of every bounds tag a bubble parent names, plus 128 m.
    private const int PhysicsBox = 2, BoxCount = 3, BoxBytes = 32, LaneBytes = 16;
    // Metres added around a bubble's box: wide sideways and up, small down so the floor stays the template's.
    private const float BoundsMargin = 256, BoundsFloorMargin = 64;
    // A copied bubble parent names the template's bounds tags by wide hash, as "##0x" and 16 hex digits.
    private const string WidePrefix = "##0x", NoWideTag = "0x0000000000000000";

    /**
     * Gives each bundle bubble's bubble parent one more bounds tag whose physics box covers the bubble's
     * `bounds` in destination space, with a margin. Its other two boxes are empty, so they change no union.
     * The floor stays where the template's boxes put it, below the ground.
     * @return The new bounds files, one per bundle bubble with `bounds`.
     */
    public static IReadOnlyList<string> PlaceBounds(StudioProject project, PackageCatalog catalog, RegistryIndex schema, string destination,
        string bundleFolder)
    {
        var bundle = JsonNode.Parse(File.ReadAllText(Path.Combine(bundleFolder, "bundle.json")))!.AsObject();
        var names = ((JsonArray)bundle["bubbles"]!).Select(name => (string)name!).ToArray();
        var world = WorldFiles.Load(project.ContentDirectory, destination);
        var live = WorldContent.LiveBubbles(world);
        var parents = world.Files.Where(file => file.ClassId == SBubbleParent.Class)
            .ToDictionary(file => WorldFiles.Hash(WorldFiles.Field((JsonObject)WorldFiles.Field(world.Fields(file.Name), "metadata"), "bubble_name_hash")),
                file => file.Name);
        var added = new List<string>();
        for (int index = 0; index < names.Length && index < live.Count; index++)
        {
            var bubble = ReadBubble(Path.Combine(bundleFolder, names[index]));
            if (bubble["bounds"] is not JsonObject bounds) continue;
            if (!parents.TryGetValue(live[index], out string? parent)) throw new InvalidDataException($"Bubble 0x{live[index]:X8} has no bubble parent.");
            var refs = (JsonArray)WorldFiles.Field((JsonObject)WorldFiles.Field(world.Fields(parent), "metadata"), "bounds_refs");
            var template = refs.OfType<JsonObject>().FirstOrDefault()
                ?? throw new InvalidDataException($"{parent} names no bounds tag to copy.");
            string wide = WorldFiles.Text(template["wide_tag"]) ?? "";
            if (!wide.StartsWith(WidePrefix, StringComparison.Ordinal) || !catalog.TryResolve64(Convert.ToUInt64(wide[WidePrefix.Length..], 16), out var entry))
                throw new InvalidDataException($"{parent} names its first bounds tag by {wide}, which is not installed.");
            var (_, place) = Anchor(world, live[index], bubble);
            var (low, high) = Placed(Vector(bounds["min"]), Vector(bounds["max"]), place);
            byte[] data = BoundsData(catalog.Read(entry.TagHash), low - new Vector3(BoundsMargin, BoundsMargin, BoundsFloorMargin),
                high + new Vector3(BoundsMargin));
            uint scratch = EntityBuilder.ScratchTag(0, EntityBuilder.SeedTags - 1 - index);
            string file = WorldContent.AddTags(world, schema, catalog, destination, [new WorldTag(scratch, entry.Reference, entry.TypeInfo, data, entry.TagHash)],
                new Dictionary<uint, string> { [scratch] = $"bundle_bounds_{FileName(names[index])}" })[0];
            world.Edit(parent, (_, fields) =>
            {
                var row = (JsonObject)template.DeepClone();
                row["bounds"] = file;
                row["wide_tag"] = NoWideTag;
                ((JsonArray)WorldFiles.Field((JsonObject)WorldFiles.Field(fields, "metadata"), "bounds_refs")).Add(row);
            });
            added.Add(file);
        }
        world.SaveChanged(project.ContentDirectory);
        return added;
    }

    /** The box around a bundle-space box's eight corners once placed. */
    private static (Vector3 Low, Vector3 High) Placed(Vector3 min, Vector3 max, Matrix4x4 place)
    {
        var corners = Enumerable.Range(0, 8).Select(corner => Vector3.Transform(new Vector3((corner & 1) != 0 ? max.X : min.X,
            (corner & 2) != 0 ? max.Y : min.Y, (corner & 4) != 0 ? max.Z : min.Z), place)).ToArray();
        return (corners.Aggregate(Vector3.Min), corners.Aggregate(Vector3.Max));
    }

    /** A copy of an installed bounds tag: an empty first and second box, and the physics box from low to high. */
    private static byte[] BoundsData(byte[] installed, Vector3 low, Vector3 high)
    {
        if (installed.Length < BoxCount * BoxBytes) throw new InvalidDataException("The installed bounds tag is shorter than three boxes.");
        byte[] data = (byte[])installed.Clone();
        for (int box = 0; box < BoxCount; box++)
        {
            // An empty box is +max to -max, so a min/max union keeps the other bubbles' boxes as they are.
            var (from, to) = box == PhysicsBox ? (low, high) : (new Vector3(float.MaxValue), new Vector3(-float.MaxValue));
            Put(data, box * BoxBytes, from);
            Put(data, box * BoxBytes + LaneBytes, to);
        }
        return data;
    }

    /** Writes x, y and z; the fourth lane keeps its installed value. */
    private static void Put(byte[] data, int at, Vector3 value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(at), value.X);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(at + 4), value.Y);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(at + 8), value.Z);
    }
}
