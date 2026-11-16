using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/** Combatant display names: the actor definition holds only a hash, and every installed enemy name is in one bank. */
public static class CombatantNameBank
{
    /** activity_global_strings, the one installed bank that holds every enemy display name. */
    public const uint Bank = 0x80B9E738;

    /** The hash a display name is stored under. */
    public static uint Hash(string name) => StringBankWriter.Hash(name);

    /**
     * The bank's container and English data with these names added; hashes already holding the same text are kept.
     * Every language slot then names the English data, so other languages read English names from this bank.
     * @param read Current bytes of a tag, with any project edit applied.
     * @return The edited tags; empty when every name is already present.
     */
    public static IReadOnlyDictionary<uint, byte[]> WithNames(PackageCatalog catalog, Func<uint, byte[]> read, IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(read);
        byte[] original = catalog.ReadOriginal(Bank);
        uint dataTag = TagRead.U32(original, TagView.FieldOffset<SLocalizedStrings>(nameof(SLocalizedStrings.LanguageData)));
        var template = StringBankTemplate.Read(Bank, original, dataTag, catalog.ReadOriginal(dataTag),
            tag => catalog.TryGetEntry(tag, out var entry) && entry.Reference == StringBankWriter.DataClass);
        var rows = StringBankWriter.Read(read(Bank), read(dataTag));
        var texts = rows.Where(row => row.Text.Length != 0).ToList();
        var empty = rows.Where(row => row.Text.Length == 0).Select(row => row.Hash).ToArray();
        var known = rows.ToDictionary(row => row.Hash, row => row.Text);
        int added = 0;
        foreach (string name in names.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A display name cannot be empty.", nameof(names));
            uint hash = Hash(name);
            if (known.TryGetValue(hash, out string? held))
            {
                if (held != name) throw new InvalidDataException($"Display name \"{name}\" hashes to 0x{hash:X8}, which the bank holds as \"{held}\".");
                continue;
            }
            known[hash] = name;
            texts.Add(new(hash, name));
            added++;
        }
        if (added == 0) return new Dictionary<uint, byte[]>();
        var (container, data) = StringBankWriter.Write(template, dataTag, texts, empty);
        return new Dictionary<uint, byte[]> { [Bank] = container, [dataTag] = data };
    }
}
