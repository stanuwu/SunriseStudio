using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

/** Hash -> value maps such as 0x80EC3F60 and 0x80EC3F61: 8-byte {u32 hash, u32 value} rows at +8. */
public static class PackageHashMap
{
    /** The first row of a hash wins. Throws InvalidDataException when the map is missing or malformed. */
    public static Dictionary<uint, uint> Read(PackageCatalog catalog, uint tag, uint rowClass)
    {
        var map = ReadStructured(catalog, tag);
        var result = new Dictionary<uint, uint>();
        if (rowClass == SArtNameRow.Class)
        {
            ref readonly var entries = ref TagView.At<TagArray<SArtNameRow>>(map,
                TagView.FieldOffset<SArtNameMapTag0x80EC3F61>(nameof(SArtNameMapTag0x80EC3F61.TagEntries)));
            foreach (ref readonly var row in TagView.Rows(map, in entries, rowClass)) result.TryAdd(row.ArtName, row.ArtWrapper);
        }
        else
        {
            // Every other hash map shares the runtime name map's {u32 hash, u32 value} rows.
            ref readonly var entries = ref TagView.At<TagArray<SRuntimeNameRow>>(map, TagView.FieldOffset<SRuntimeNameMap>(nameof(SRuntimeNameMap.TagEntries)));
            foreach (ref readonly var row in TagView.Rows(map, in entries, rowClass)) result.TryAdd(row.NameHash, row.ProgramTag);
        }
        return result;
    }

    /** A structured table; it starts with its own u64 size. */
    internal static byte[] ReadStructured(PackageCatalog catalog, uint tag)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Type is not (8 or 16))
            throw new InvalidDataException($"Table 0x{tag:X8} is missing or not structured.");
        byte[] data = catalog.Read(tag);
        if (!TagView.HasSizeWord(data)) throw new InvalidDataException($"Table 0x{tag:X8} has a bad size header.");
        return data;
    }
}
