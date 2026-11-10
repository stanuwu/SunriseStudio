using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/** Objective progress descriptions, read from the objective display table. */
internal static class InvestmentObjectiveText
{
    /**
     * The description's string bank, a 16-bit index in the word just before the row's string hash. The row layout names only
     * the objective hash and the string hash.
     */
    private static readonly int DescriptionBank = TagView.FieldOffset<C808059f0>(nameof(C808059f0.Unk1c)) - sizeof(uint);

    /**
     * Rows join objectives by index and must repeat the objective's hash. A missing or mismatched row gives no text.
     * @param tag The display table to read, or 0 to find the one installed entry of its class, as InvestmentRoot.Locate finds
     * the root.
     * @param text Resolves a (bank, string hash) pair, or returns "" when the bank or string is absent.
     * @return One description per objective, "" when there is none.
     */
    internal static string[] Read(PackageCatalog catalog, uint tag, IReadOnlyList<InvestmentObjective> objectives,
        Func<uint, uint, string> text, List<string> diagnostics, CancellationToken token)
    {
        if (objectives.Count == 0) return [];
        if (tag == 0)
        {
            var tables = catalog.Entries.Where(entry => entry.Type is 8 or 16
                && entry.Reference == C808059ec.Class).Take(2).ToArray();
            if (tables.Length != 1)
                throw new InvalidDataException(tables.Length == 0 ? "The objective display table is not installed."
                    : "The objective display table is ambiguous.");
            tag = tables[0].TagHash;
        }
        if (!catalog.ContainsTag(tag)) throw new InvalidDataException("The objective display table is not installed.");
        InvestmentCatalog.RequireClass(catalog, tag, C808059ec.Class);
        ReadOnlySpan<byte> table = catalog.Read(tag);
        int stride = Unsafe.SizeOf<C808059f0>();
        var rows = TagArray.Checked(table, TagView.FieldOffset<C808059ec>("Table"), C808059f0.Class, stride);
        var descriptions = new string[objectives.Count];
        int unmatched = 0;
        for (int index = 0; index < descriptions.Length; index++)
        {
            if ((index & 255) == 0) token.ThrowIfCancellationRequested();
            int at = rows.Data + index * stride;
            if (index >= rows.Count || TagView.At<C808059f0>(table, at).Unk0 != objectives[index].Hash) { descriptions[index] = ""; unmatched++; continue; }
            descriptions[index] = text(TagRead.U16(table, at + DescriptionBank), TagView.At<C808059f0>(table, at).Unk1c);
        }
        if (unmatched != 0) diagnostics.Add($"Objective text: {unmatched} objectives have no display row with their hash.");
        return descriptions;
    }
}
