using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Editing;

namespace SunriseStudio.Projects;

/**
 * Which bytes of a tag the game may patch: integer, float and vector values only. In a poke-safe
 * struct the tier is 3 (next spawn) or 2 (at once); elsewhere it is 4 (the next load).
 */
internal static class LivePoke
{
    /** The tier of a value outside every poke-safe struct. */
    public const int LoadTier = 4;

    /** @return The changed runs and their highest tier, or null when any changed byte is not a value. */
    public static (IReadOnlyList<LiveRange> Ranges, int Tier)? Ranges(byte[] before, byte[] after, uint classId, RegistryIndex schema)
    {
        if (before.Length != after.Length) return null;
        TagDocument document;
        try { document = TagDocument.Parse(after, classId, schema); }
        catch (InvalidDataException) { return null; }
        var tiers = new byte[after.Length];
        Mark(TagFieldTree.Build(document, schema), schema, 0, tiers);
        var ranges = new List<LiveRange>();
        int highest = 0;
        for (int at = 0; at < after.Length; at++)
        {
            if (before[at] == after[at]) continue;
            int end = at;
            for (; end < after.Length && before[end] != after[end]; end++)
            {
                if (tiers[end] == 0) return null;
                highest = Math.Max(highest, tiers[end]);
            }
            ranges.Add(new(at, after[at..end], before[at..end]));
            at = end;
        }
        return ranges.Count == 0 ? null : (ranges, highest);
    }

    private static void Mark(TagFieldRow row, RegistryIndex schema, int structTier, byte[] tiers)
    {
        int tier = structTier;
        if (row.Kind == TagFieldKind.Struct)
            tier = schema.Class(row.ClassId) is { PokeSafe: true } structure ? structure.LiveTier == 3 ? 3 : 2 : LoadTier;
        else if (row.Kind == TagFieldKind.Value && structTier != 0 && row.Edit is TagValueEdit.Integer or TagValueEdit.Float or TagValueEdit.Vector
            && row.Offset >= 0 && row.Size > 0 && row.Offset + row.Size <= tiers.Length)
            Array.Fill(tiers, (byte)structTier, row.Offset, row.Size);
        foreach (var child in row.Children) Mark(child, schema, tier, tiers);
    }
}
