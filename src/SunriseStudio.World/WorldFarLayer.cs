using System.Text.Json.Nodes;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Tft;

namespace SunriseStudio.World;

/** One map bubble (slice set) of a world: its name hash, its ordinal and its 256-cell mask. */
public sealed record WorldMapBubble(uint NameHash, int Ordinal, byte[] CellMask);

/**
 * A far layer: a container a bubble loads when its cell mask meets the container's mask. It stands
 * in for other bubbles' geometry, so it must not load in a bubble whose cells it covers.
 */
public static class WorldFarLayer
{
    // A cell or container mask holds one bit per map cell.
    private const int MaskBytes = 32;
    // owner_bubble of a cell every bubble shares.
    private const uint Shared = uint.MaxValue;

    /** The map's slice sets in ordinal order. */
    public static IReadOnlyList<WorldMapBubble> Bubbles(WorldFiles world)
    {
        var map = world.Fields(world.Single(SMapLocationNames.Class).Name);
        return ((JsonArray)WorldFiles.Field(map, "slice_sets")).OfType<JsonObject>().Select(row => new WorldMapBubble(
            WorldFiles.Hash(WorldFiles.Field(row, "bubble_name_hash")), checked((int)(uint)WorldFiles.Field(row, "bubble_ordinal")),
            Mask(WorldFiles.Field(row, "cell_mask")))).ToArray();
    }

    /** The cells a bubble owns: those whose owner is its ordinal. Shared cells have no owner. */
    public static IReadOnlyList<int> OwnCells(WorldFiles world, int ordinal)
    {
        var map = world.Fields(world.Single(SMapLocationNames.Class).Name);
        return ((JsonArray)WorldFiles.Field(map, "slices")).OfType<JsonObject>()
            .Select((cell, index) => ((uint)WorldFiles.Field(cell, "owner_bubble"), index))
            .Where(x => x.Item1 != Shared && x.Item1 == ordinal).Select(x => x.index).ToArray();
    }

    /**
     * The candidates that may load a far layer standing in for `covered`: each bubble whose cell mask
     * holds none of the covered bubbles' own cells.
     */
    public static IReadOnlyList<uint> Loaders(WorldFiles world, IReadOnlyCollection<uint> covered, IReadOnlyCollection<uint> candidates)
    {
        var bubbles = Bubbles(world);
        var coveredCells = bubbles.Where(b => covered.Contains(b.NameHash)).SelectMany(b => OwnCells(world, b.Ordinal)).ToArray();
        if (coveredCells.Length == 0) throw new InvalidDataException("A covered bubble owns no map cell.");
        return bubbles.Where(b => candidates.Contains(b.NameHash) && !covered.Contains(b.NameHash)
            && coveredCells.All(cell => !Has(b.CellMask, cell))).Select(b => b.NameHash).ToArray();
    }

    /** The far container's mask: every own cell of the loaders, so it meets each loader and only them. */
    public static byte[] ContainerMask(WorldFiles world, IReadOnlyCollection<uint> loaders)
    {
        var mask = new byte[MaskBytes];
        foreach (var bubble in Bubbles(world).Where(b => loaders.Contains(b.NameHash)))
            foreach (int cell in OwnCells(world, bubble.Ordinal)) mask[cell / 8] |= (byte)(1 << (cell % 8));
        return mask;
    }

    /**
     * Adds a far container holding `lists`, copied from an own container of the first loader, and lists it
     * under every loader's bubble definition.
     * @param name The new container's file stem.
     * @return The container's file name.
     */
    public static string Add(WorldFiles world, string destination, string name, IReadOnlyCollection<uint> loaders, IReadOnlyList<string> lists)
    {
        if (loaders.Count == 0) throw new InvalidDataException("A far layer needs a bubble that loads it.");
        var mask = ContainerMask(world, loaders);
        // An added bubble's first container is one every bubble shares, so the copy starts from an own one.
        string template = WorldBubbles.OwnFirst(world, loaders.First())[0];
        string file = $"{WorldTemplate.Folder}/{destination}/map_container/{name}.map_container";
        var json = TftText.ToJson(world[template], out string? fallback);
        if (fallback is not null) throw new InvalidDataException($"{template} has no field view ({fallback}).");
        json["name"] = file;
        // A container is a manifest root; its manifest starts from the template's source manifest.
        var fields = (JsonObject)json["fields"]!;
        fields["bubble_mask"] = Convert.ToHexStringLower(mask);
        fields["contents"] = new JsonArray(lists.Select(list => (JsonNode)new JsonObject { ["member_tag"] = list }).ToArray());
        world.Add(TftText.FromJson(json));
        foreach (uint loader in loaders)
        {
            string definition = WorldContent.Definition(world, loader);
            world.Edit(definition, (_, f) => ((JsonArray)WorldFiles.Field(f, "bubble_children"))
                .Add(new JsonObject { ["narrow_tag"] = WorldFiles.HashText(uint.MaxValue), ["wide_tag"] = file }));
        }
        return file;
    }

    /** True when the 256-bit mask holds `cell`. */
    public static bool Has(byte[] mask, int cell) => (mask[cell / 8] & (1 << (cell % 8))) != 0;

    private static byte[] Mask(JsonNode node)
    {
        byte[] mask = Convert.FromHexString((string)node!);
        return mask.Length == MaskBytes ? mask : throw new InvalidDataException("A cell mask is not 32 bytes.");
    }
}
