using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Packages;

/** Where a package's misc region holds tag hashes: its named-tag rows and its wide-hash alias rows. */
public static class PackageMisc
{
    public const uint NameRowClass = PackageNamedTagRow.Class, AliasRowClass = Hash64ReferenceRow.Class;
    private static readonly int NameTag = TagView.FieldOffset<PackageNamedTagRow>(nameof(PackageNamedTagRow.Tag)),
        AliasTag = TagView.FieldOffset<Hash64ReferenceRow>(nameof(Hash64ReferenceRow.Tag));

    /** One named-tag or alias row array of a misc region. */
    public readonly record struct RowArray(int Start, int Count, bool Named)
    {
        public int Stride => Named ? Unsafe.SizeOf<PackageNamedTagRow>() : Unsafe.SizeOf<Hash64ReferenceRow>();
    }

    /**
     * Finds the typed name and alias arrays at every 8-byte aligned array field: a count, then a
     * pointer to a header behind the marker. Each array is listed once; rows past the region are skipped.
     */
    public static IReadOnlyList<RowArray> RowArrays(byte[] misc)
    {
        var found = new List<RowArray>();
        var seen = new HashSet<long>();
        for (int field = 0; field <= misc.Length - TagArray.FieldSize; field += 8)
        {
            ulong count = BinaryPrimitives.ReadUInt64LittleEndian(misc.AsSpan(field));
            long relative = BinaryPrimitives.ReadInt64LittleEndian(misc.AsSpan(field + TagArray.RelativeOffset));
            if (count == 0 || count > TagArray.MaximumReaderRows || relative < -field || relative > misc.Length) continue;
            long header = field + (long)TagArray.RelativeOffset + relative;
            if (header > misc.Length - TagArray.HeaderSize || !TagArray.MarkerAt(misc, header)) continue;
            uint element = BinaryPrimitives.ReadUInt32LittleEndian(misc.AsSpan((int)header + TagArray.ElementClassOffset));
            if (element is not (NameRowClass or AliasRowClass) || !seen.Add(header)) continue;
            var array = new RowArray((int)header + TagArray.HeaderSize, (int)count, element == NameRowClass);
            if (array.Start + (long)array.Count * array.Stride <= misc.Length) found.Add(array);
        }
        return found;
    }

    /** @return The misc offset of every tag field in the name and alias rows, ascending. */
    public static IReadOnlyList<int> TagFieldOffsets(byte[] misc)
    {
        var offsets = new SortedSet<int>();
        foreach (var array in RowArrays(misc))
            for (int row = 0; row < array.Count; row++)
                offsets.Add(array.Start + row * array.Stride + (array.Named ? NameTag : AliasTag));
        return offsets.ToArray();
    }
}
