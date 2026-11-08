using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Tft;

namespace SunriseStudio.World;

/**
 * Cuts a copied world to some bubbles. Rows are positional: a dropped bubble keeps its scenario row
 * with no states and its map root row with a wide hash that names nothing; rows after the last kept
 * bubble go. The map definition is kept whole.
 */
public static class WorldCut
{
    /** @param bubbles Bubble name hashes to keep; each must be a scenario bubble and a copied map root row. */
    public static void KeepBubbles(WorldFiles world, IReadOnlySet<uint> bubbles)
    {
        var scenario = world.Single(SScenario.Class);
        var mapRoot = world.Single(SMapRoot.Class);
        world.Edit(scenario.Name, (_, fields) =>
        {
            var rows = (JsonArray)WorldFiles.Field(fields, "bubbles");
            bool Kept(JsonNode? row) => bubbles.Contains(WorldFiles.Hash(WorldFiles.Field((JsonObject)row!, "name_hash")));
            if (rows.Count(Kept) != bubbles.Count) throw new InvalidDataException("A kept bubble is not in the scenario.");
            while (!Kept(rows[^1])) rows.RemoveAt(rows.Count - 1);
            foreach (var row in rows.OfType<JsonObject>().Where(row => !Kept(row))) row["states"] = new JsonArray();
        });
        world.Edit(mapRoot.Name, (_, fields) =>
        {
            var rows = (JsonArray)WorldFiles.Field(fields, "map_entries");
            int kept = 0;
            for (int index = 0; index < rows.Count; index++)
            {
                var row = (JsonObject)rows[index]!;
                if (CopiedTarget(row) is not var (key, target)) continue;
                if (bubbles.Contains(BubbleOf(world, target))) kept++;
                else row[key] = TftNames.InstalledWide(AbsentHash(mapRoot.Name, index));
            }
            if (kept != bubbles.Count) throw new InvalidDataException("A kept bubble has no copied map root row.");
        });
        world.Prune([scenario.Name, mapRoot.Name, .. world.Files.Where(x => x.ClassId == ActivityRoot.Class).Select(x => x.Name)]);
    }

    /**
     * A wide hash no package defines, stable per map root and row: the first 8 bytes of SHA-256 of
     * the seed, the map root's name and the row. Zero and all-ones mean "no tag", so they become 1.
     */
    public static ulong AbsentHash(string mapRoot, int row)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes($"sunrise-absent-bubble\n{mapRoot}\n{row}"));
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(digest);
        return value is 0 or ulong.MaxValue ? 1 : value;
    }

    /** The member of a map root row that names a copied bubble parent; null for a row that names none. */
    private static (string Key, string Target)? CopiedTarget(JsonObject row)
    {
        var named = row.Select(x => (x.Key, Value: WorldFiles.Text(x.Value))).Where(x => x.Value is { } text
            && !text.StartsWith('#') && !text.StartsWith("0x", StringComparison.Ordinal)).ToList();
        return named switch
        {
            [] => null,
            [var one] => (one.Key, one.Value!),
            _ => throw new InvalidDataException("A map root row names two copied tags."),
        };
    }

    /** The bubble a copied bubble parent loads. */
    private static uint BubbleOf(WorldFiles world, string bubbleParent) =>
        WorldFiles.Hash(WorldFiles.Field((JsonObject)WorldFiles.Field(world.Fields(bubbleParent), "metadata"), "bubble_name_hash"));
}
