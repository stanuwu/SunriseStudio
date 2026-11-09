/**
 * File: Gives a subclass item its own socket-entry list (the MCP fork_subclass_grid tool): the talent-grid writer.
 *
 * A subclass item names one row of the socket-entry-list table (investment root slot 97, tag 0x8131932C in 86657)
 * through a u16 behind its relative pointer at +128. A mod subclass cloned from a shipped one (the Stasis Behemoth,
 * Shadebinder and Revenant) still names its base's row, so any change to its nodes would change the base too. This
 * copies the list definition and every pool tag its entries name (entry +56) into the project, points the copied
 * definition at the copied pools, appends a table row for it with a new hash, and points the item at that row. The
 * copies start byte-identical apart from those references; their pools (ability hash records and the 0x80807C5F perk
 * arrays) can then be edited without touching the base subclass.
 */
using System.Buffers.Binary;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor.Creation;

/** The fork: old and new list rows, the copies, and the project additions and edits that carry them. */
public sealed record PreparedSubclassGrid(string Name, uint ItemTag, int OldList, int NewList, uint DefinitionTag,
    IReadOnlyDictionary<uint, uint> Tags, IReadOnlyList<ProjectAssetAddition> Assets, IReadOnlyDictionary<uint, byte[]> Changes);

public static class SubclassGridAuthoring
{
    /** Investment root slot 97 in 86657: the socket-entry-list index table. */
    public const uint SocketEntryListTable = 0x8131932C;
    /** An item definition holds the relative pointer to its 12-byte socket-entry-list block here. */
    private const int ItemListBlockField = 128;
    /** A socket-entry-list definition holds its entry array descriptor here; entries are 64 bytes. */
    private const int EntryArrayField = 16;
    private const int EntryStride = 64;
    /** Each entry names its pool tag here. */
    private const int EntryPoolOffset = 56;
    /** The client's index lookup is signed 16-bit, as for items. */
    private const int MaximumRows = 0x8000;

    /** Reads the list index an item names, and the offset of the u16 that holds it. */
    public static (int List, int Offset) ReadItemList(ReadOnlySpan<byte> item)
    {
        if (item.Length < ItemListBlockField + 8) throw new InvalidDataException("The item is too short to name a socket-entry list.");
        long relative = BinaryPrimitives.ReadInt64LittleEndian(item[ItemListBlockField..]);
        long block = ItemListBlockField + relative;
        if (relative == 0 || block < 0 || block + 12 > item.Length)
            throw new InvalidDataException("The item names no socket-entry list (is it a subclass?).");
        return (BinaryPrimitives.ReadUInt16LittleEndian(item[(int)block..]), (int)block);
    }

    /** Every distinct pool tag the entries of a list definition name, in entry order. */
    public static IReadOnlyList<uint> ReadPoolTags(ReadOnlySpan<byte> definition)
    {
        if (definition.Length < EntryArrayField + 16) throw new InvalidDataException("The list definition is too short.");
        long count = BinaryPrimitives.ReadInt64LittleEndian(definition[EntryArrayField..]);
        long relative = BinaryPrimitives.ReadInt64LittleEndian(definition[(EntryArrayField + 8)..]);
        long data = EntryArrayField + 8 + relative + 16;
        if (count is < 0 or > 64 || data < 0 || data + count * EntryStride > definition.Length)
            throw new InvalidDataException("The list definition's entry array does not read.");
        var pools = new List<uint>();
        for (long index = 0; index < count; index++)
        {
            uint pool = BinaryPrimitives.ReadUInt32LittleEndian(definition[(int)(data + index * EntryStride + EntryPoolOffset)..]);
            if (pool != 0 && !pools.Contains(pool)) pools.Add(pool);
        }
        return pools;
    }

    /** Plans the fork on a private catalog; nothing reaches the project until the workspace commits the result. */
    public static PreparedSubclassGrid Prepare(StudioProject project, PackageCatalog source, uint itemTag, string name, ushort packageId)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200) throw new InvalidDataException("Give a name of 1 to 200 characters.");
        using var plan = new ExoticWeaponAuthoring.PlanCatalog(source, project, packageId);
        var catalog = plan.Catalog;
        if (!catalog.TryGetEntry(itemTag, out var itemEntry) || itemEntry.Reference != InvestmentTags.ItemClass)
            throw new InvalidDataException($"0x{itemTag:X8} is not an item definition.");
        byte[] item = catalog.Read(itemTag);
        var (oldList, listOffset) = ReadItemList(item);
        byte[] table = catalog.Read(SocketEntryListTable);
        var rows = InvestmentItemClone.ReadIndexRows(table);
        if (oldList >= rows.Count) throw new InvalidDataException($"The item names list {oldList}; the table has {rows.Count} rows.");
        if (rows.Count >= MaximumRows) throw new InvalidDataException("The socket-entry-list table is full.");
        uint definitionTag = rows[oldList].Tag;
        if (!catalog.TryGetEntry(definitionTag, out _)) throw new InvalidDataException($"List {oldList}'s definition 0x{definitionTag:X8} is missing.");
        var pools = ReadPoolTags(catalog.Read(definitionTag));
        var tags = new List<uint> { definitionTag };
        tags.AddRange(pools.Where(pool => pool != definitionTag));
        if (tags.Count > TagCloneAuthoring.MaximumTags)
            throw new InvalidDataException($"The list names {tags.Count - 1} pools; at most {TagCloneAuthoring.MaximumTags - 1} fit one fork.");
        foreach (uint tag in tags)
            if (!catalog.TryGetEntry(tag, out _)) throw new InvalidDataException($"Pool 0x{tag:X8} is missing.");

        // Copy the definition and its pools; inside the copies every 4-byte value naming a copied tag is swapped.
        int first = plan.NextEntry;
        var copies = tags.Select((tag, index) => (tag, copy: ProjectTags.Tag(packageId, first + index)))
            .ToDictionary(pair => pair.tag, pair => pair.copy);
        var additions = new List<ProjectAssetAddition>();
        for (int index = 0; index < tags.Count; index++)
        {
            uint tag = tags[index];
            catalog.TryGetEntry(tag, out var entry);
            byte[] bytes = catalog.Read(tag);
            for (int at = 0; at + 4 <= bytes.Length; at += 4)
                if (copies.TryGetValue(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)), out uint to))
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), to);
            additions.Add(new($"{name}.{tag:X8}", bytes,
                new(packageId, entry.Reference, entry.TypeInfo, $"{name} {tag:X8}", first + index), tag));
        }
        plan.Add(additions);

        // A table row for the copied definition, then the item names it.
        uint hash = InvestmentItemClone.NewDefinitionHash($"sunrise:subclass-grid:{project.Name}:{packageId:X4}:{first}:{name}",
            rows.Select(row => row.Hash).ToHashSet());
        var (nextTable, newList) = InvestmentItemClone.AppendIndexRow(table, hash, copies[definitionTag]);
        if (newList >= MaximumRows) throw new InvalidDataException("The new list row is past the reachable range.");
        BinaryPrimitives.WriteUInt16LittleEndian(item.AsSpan(listOffset), checked((ushort)newList));
        plan.Edit(new Dictionary<uint, byte[]> { [SocketEntryListTable] = nextTable, [itemTag] = item });
        return new(name, itemTag, oldList, newList, copies[definitionTag], copies, plan.Assets.ToArray(), plan.Changes.ToDictionary());
    }
}
