/**
 * File: Copies installed tags into a project package (the MCP clone_tags tool): each copy gets a new entry, references
 * among the copied set point at the copies, and requested values and string hashes are swapped.
 */
using System.Buffers.Binary;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor.Creation;

/** The copies: source tag to new tag, and the project additions and edits that carry them. */
public sealed record PreparedTagClone(string Name, IReadOnlyDictionary<uint, uint> Tags, IReadOnlyDictionary<uint, int> Swaps,
    IReadOnlyList<ProjectAssetAddition> Assets, IReadOnlyDictionary<uint, byte[]> Changes);

/**
 * A copy is the source bytes with every 4-byte-aligned value swapped: a copied tag becomes its copy, a value in replace
 * becomes its new value, and a string hash in strings becomes the hash of a new project string with that text. Nothing
 * points at the copies until another edit names them.
 */
public static class TagCloneAuthoring
{
    public const int MaximumTags = 32;

    /** Plans the copies on a private catalog; nothing reaches the project until the workspace commits the result. */
    public static PreparedTagClone Prepare(StudioProject project, PackageCatalog source, IReadOnlyList<uint> tags, string name,
        IReadOnlyDictionary<uint, uint> replace, IReadOnlyDictionary<uint, string> strings, ushort packageId)
    {
        if (tags.Count == 0 || tags.Count > MaximumTags || tags.Distinct().Count() != tags.Count)
            throw new InvalidDataException($"Give 1 to {MaximumTags} different tags.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200) throw new InvalidDataException("Give a name of 1 to 200 characters.");
        if (strings.Values.Any(text => text.Length is 0 or > 4000)) throw new InvalidDataException("Strings need 1 to 4000 characters.");
        using var plan = new ExoticWeaponAuthoring.PlanCatalog(source, project, packageId);
        var catalog = plan.Catalog;
        foreach (uint tag in tags)
            if (!catalog.TryGetEntry(tag, out _)) throw new InvalidDataException($"No tag 0x{tag:X8}.");
        var values = new Dictionary<uint, uint>(replace);
        if (strings.Count != 0)
        {
            var texts = strings.Values.Distinct(StringComparer.Ordinal)
                .Select(text => new StringBankText(StringBankWriter.Hash(text), text)).ToArray();
            var banks = ProjectStringBank.Plan(catalog, project, packageId, plan.NextEntry, texts, [], new());
            plan.Add(banks.Additions); plan.Edit(banks.Edits);
            foreach (var (old, text) in strings) values[old] = StringBankWriter.Hash(text);
        }
        int first = plan.NextEntry;
        var copies = tags.Select((tag, index) => (tag, copy: ProjectTags.Tag(packageId, first + index)))
            .ToDictionary(pair => pair.tag, pair => pair.copy);
        foreach (var (tag, copy) in copies) values[tag] = copy;
        var swaps = new Dictionary<uint, int>();
        var additions = new List<ProjectAssetAddition>();
        for (int index = 0; index < tags.Count; index++)
        {
            uint tag = tags[index];
            catalog.TryGetEntry(tag, out var entry);
            byte[] bytes = catalog.Read(tag);
            int swapped = 0;
            for (int at = 0; at + 4 <= bytes.Length; at += 4)
                if (values.TryGetValue(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)), out uint to))
                { BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), to); swapped++; }
            swaps[tag] = swapped;
            additions.Add(new($"{name}.{tag:X8}", bytes,
                new(packageId, entry.Reference, entry.TypeInfo, $"{name} {tag:X8}", first + index), tag));
        }
        plan.Add(additions);
        return new(name, copies, swaps, plan.Assets.ToArray(), plan.Changes.ToDictionary());
    }
}
