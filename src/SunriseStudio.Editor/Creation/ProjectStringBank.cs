using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Editor.Creation;

/** The one string bank a project package holds; new strings join it, and the first use creates it. */
public sealed class ProjectStringBank
{
    public int BankIndex { get; private init; }
    /** New container and data entries; empty when the package already has a bank. */
    public IReadOnlyList<ProjectAssetAddition> Additions { get; private init; } = [];
    /** Tags the additions receive, in order. */
    public IReadOnlyList<uint> AddedTags { get; private init; } = [];
    /** Rewritten bank payloads and the bank index table, keyed by tag. */
    public IReadOnlyDictionary<uint, byte[]> Edits { get; private init; } = new Dictionary<uint, byte[]>();

    /**
     * Adds strings to the package's bank, creating the bank and its bank index row when none exists.
     * @param firstEntry Next free entry index in the package; a new bank takes two.
     * @param preferredTemplates Installed banks tried first as the layout template.
     */
    public static ProjectStringBank Plan(PackageCatalog packages, StudioProject project, ushort packageId, int firstEntry,
        IReadOnlyList<StringBankText> strings, IEnumerable<uint> preferredTemplates, InvestmentSources sources)
    {
        byte[] bankTable = packages.Read(sources.StringBanks);
        var rows = InvestmentItemClone.ReadBankRows(bankTable);
        var owned = project.AddedAssets.Where(asset => asset.Native is { } native && native.PackageId == packageId
            && native.Reference == StringBankWriter.ContainerClass).ToArray();
        if (owned.Length > 1) throw new InvalidDataException($"Package {packageId:X4} holds more than one string bank.");
        if (owned.Length == 1) return Extend(packages, project, owned[0], rows, strings);

        var template = StringBankWriter.FindTemplate(packages, preferredTemplates.Concat(rows.Select(row => row.ContainerTag)), strings);
        if (!packages.TryGetEntry(template.ContainerTag, out var containerEntry) || !packages.TryGetEntry(template.DataTag, out var dataEntry))
            throw new InvalidDataException("The template string bank is missing.");
        if (firstEntry + 1 >= TagHashes.EntriesPerPackage) throw new InvalidDataException("The package has no room for a string bank.");
        uint containerTag = TagHashes.Make(packageId, firstEntry), dataTag = TagHashes.Make(packageId, firstEntry + 1);
        var (container, data) = StringBankWriter.Write(template, dataTag, strings);
        uint nameHash = InvestmentItemClone.NewBankNameHash($"sunrise_strings_{packageId:x4}", rows);
        var (table, index) = InvestmentItemClone.AppendBankRow(bankTable, nameHash, containerTag);
        string stem = $"strings_{packageId:x4}";
        return new()
        {
            BankIndex = index,
            Additions =
            [
                new($"{stem}.strings", container,
                    new(packageId, StringBankWriter.ContainerClass, containerEntry.TypeInfo, stem, firstEntry), template.ContainerTag),
                new($"{stem}_en.strings", data,
                    new(packageId, StringBankWriter.DataClass, dataEntry.TypeInfo, stem + "_en", firstEntry + 1), template.DataTag)
            ],
            AddedTags = [containerTag, dataTag],
            Edits = new Dictionary<uint, byte[]> { [sources.StringBanks] = table }
        };
    }

    /** The existing bank is rebuilt from its installed template with the old and new strings. */
    private static ProjectStringBank Extend(PackageCatalog packages, StudioProject project, AddedProjectAsset owner,
        IReadOnlyList<InvestmentBankRow> rows, IReadOnlyList<StringBankText> strings)
    {
        uint containerTag = TagHashes.Make(owner.Native!.PackageId, owner.Native.EntryIndex ?? 0);
        byte[] container = packages.Read(containerTag);
        const string languageData = nameof(SLocalizedStrings.LanguageData);
        if (!TagView.FitsThrough<SLocalizedStrings>(container, 0, languageData))
            throw new InvalidDataException("The project string bank is truncated.");
        uint dataTag = TagView.At<uint>(container, TagView.FieldOffset<SLocalizedStrings>(languageData));
        var data = project.AddedAssets.FirstOrDefault(asset => asset.Native is { } native
            && TagHashes.Make(native.PackageId, native.EntryIndex ?? 0) == dataTag)
            ?? throw new InvalidDataException("The project string bank's English data is missing.");
        if (owner.BaseTagHash is not uint baseContainer || data.BaseTagHash is not uint baseData)
            throw new InvalidDataException("The project string bank has no installed template.");
        var template = StringBankTemplate.Read(baseContainer, packages.ReadOriginal(baseContainer), baseData,
            packages.ReadOriginal(baseData), tag => packages.TryGetEntry(tag, out var entry) && entry.Reference == StringBankWriter.DataClass);
        var merged = StringBankWriter.Read(container, packages.Read(dataTag)).ToList();
        var known = merged.ToDictionary(row => row.Hash, row => row.Text);
        foreach (var value in strings)
        {
            if (known.TryGetValue(value.Hash, out string? text))
            {
                if (text != value.Text) throw new InvalidDataException($"String hash 0x{value.Hash:X8} already names other text in this bank.");
                continue;
            }
            known.Add(value.Hash, value.Text); merged.Add(value);
        }
        var (nextContainer, nextData) = StringBankWriter.Write(template, dataTag, merged);
        int index = rows.FirstOrDefault(row => row.ContainerTag == containerTag)?.Index
            ?? throw new InvalidDataException("The project string bank has no bank index row.");
        return new() { BankIndex = index, Edits = new Dictionary<uint, byte[]> { [containerTag] = nextContainer, [dataTag] = nextData } };
    }
}
