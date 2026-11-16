using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/** The AO byte an added static model reads as its one scalar. */
internal static class StaticOcclusion
{
    /** Full AO; zero draws the model with no ambient light. */
    private const byte Open = 255;

    private static readonly ConditionalWeakTable<PackageCatalog, Dictionary<ulong, List<(uint Buffer, uint Base)>>> Maps = new();

    /**
     * The instance byte offset whose scalar is brightest in every AO map that holds the key.
     * @return Null when no map holds the key.
     */
    public static uint? Brightest(PackageCatalog catalog, ulong key)
    {
        if (!Index(catalog).TryGetValue(key, out var rows)) return null;
        var buffers = rows.Select(row => (Data: Payload(catalog, row.Buffer), row.Base)).ToArray();
        long end = buffers.Min(buffer => buffer.Data.LongLength - buffer.Base);
        uint best = 0;
        int bestValue = -1;
        // The shader fetches the scalar at a four-byte-aligned address.
        for (uint offset = 0; offset < end; offset += 4)
        {
            int value = buffers.Min(buffer => buffer.Data[buffer.Base + offset]);
            if (value <= bestValue) continue;
            (best, bestValue) = (offset, value);
            if (value == Open) break;
        }
        return bestValue < 0 ? null : best;
    }

    private static Dictionary<ulong, List<(uint Buffer, uint Base)>> Index(PackageCatalog catalog) =>
        Maps.GetValue(catalog, owner =>
        {
            var index = new Dictionary<ulong, List<(uint, uint)>>();
            foreach (var entry in owner.Entries)
            {
                if (entry.Reference != SOcclusionMap.Class) continue;
                byte[] data = owner.ReadOriginal(entry.TagHash);
                ref readonly var map = ref TagView.Checked<SOcclusionMap>(data, 0);
                foreach (ref readonly var row in TagView.Rows(data, in map.Offsets, UnknownOcclusionMapInstance.Class))
                {
                    if (!index.TryGetValue(row.InstanceKey, out var list)) index[row.InstanceKey] = list = [];
                    list.Add((map.ScalarBufferHeader, row.BaseByteOffset));
                }
            }
            return index;
        });

    private static byte[] Payload(PackageCatalog catalog, uint header) =>
        catalog.TryGetEntry(header, out var entry) ? catalog.ReadOriginal(entry.Reference)
            : throw new InvalidDataException($"The AO buffer header {header:X8} is missing.");
}
