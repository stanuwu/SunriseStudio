using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using ManifestView = SunriseStudio.Tags.Generated.PackageManifest;

namespace SunriseStudio.Packages;

/** A PackageManifest tag: the entries a root tag loads with it, grouped by package. */
public sealed class PackageManifest
{
    public const uint ClassId = ManifestView.Class;
    private static readonly int GroupCountField = TagView.FieldOffset<ManifestView>("Fragment.ManifestArray");
    private static readonly int GroupArrayField = TagView.FieldOffset<ManifestView>("Fragment.ManifestArray.Relative");
    private static readonly int KeyCountField = TagView.FieldOffset<ManifestView>("Fragment.KeyCount");
    private static readonly int KeyArrayField = TagView.FieldOffset<ManifestView>("Fragment.KeyCount.Relative");
    private static readonly int TagSizeField = TagView.FieldOffset<ManifestView>(nameof(ManifestView.TagSize));
    private static readonly int GroupSize = Unsafe.SizeOf<ManifestGroup>();
    private static readonly int PackageIdField = TagView.FieldOffset<ManifestGroup>(nameof(ManifestGroup.PackageId));
    private static readonly int BitmapCountField = TagView.FieldOffset<ManifestGroup>("BitArray.NormalizeFlags");
    private static readonly int BitmapField = TagView.FieldOffset<ManifestGroup>("BitArray.NormalizeFlags.Relative");
    private static readonly int ListCountField = TagView.FieldOffset<ManifestGroup>(nameof(ManifestGroup.IndexArray));
    private static readonly int ListField = TagView.FieldOffset<ManifestGroup>("IndexArray.Relative");
    private static readonly int BitmapWordSize = Unsafe.SizeOf<U32ArrayRow>(), EntrySize = Unsafe.SizeOf<SU16Element>();

    /** One package's listed entries, from its bitmap and its entry list. */
    public sealed record Group(ushort PackageId, IReadOnlySet<int> Entries);

    public IReadOnlyList<Group> Groups { get; }

    private PackageManifest(IReadOnlyList<Group> groups) => Groups = groups;

    /** @return The offset of every group's u16 package id; Read has checked the array. */
    public static IReadOnlyList<int> GroupPackageOffsets(byte[] data)
    {
        long count = Read(data).Groups.Count;
        if (count == 0) return [];
        int array = Target(data, GroupArrayField, TagArray.HeaderSize);
        return Enumerable.Range(0, (int)count).Select(group => array + TagArray.HeaderSize + group * GroupSize + PackageIdField).ToArray();
    }

    /** @return Whether a group lists entry `tag`. */
    public bool Lists(uint tag)
    {
        var (package, entry) = Split(tag);
        return Groups.Any(group => group.PackageId == package && group.Entries.Contains(entry));
    }

    /**
     * Reads every group; each offset must land inside the data.
     * @throws InvalidDataException when an offset or a count leaves the data.
     */
    public static PackageManifest Read(byte[] data)
    {
        if (!TagView.FitsThrough<ManifestView>(data, 0, "Fragment.KeyCount")) throw new InvalidDataException("The manifest is too short.");
        long count = TagRead.I64(data, GroupCountField);
        if (count == 0) return new([]);
        int array = Target(data, GroupArrayField, TagArray.HeaderSize);
        if (count < 0 || count > (data.Length - array - TagArray.HeaderSize) / GroupSize)
            throw new InvalidDataException("The manifest group array is malformed.");
        RequireArrayHeader(data, array, count, ManifestGroup.Class);
        var groups = new List<Group>();
        for (int g = 0; g < count; g++)
        {
            int row = array + TagArray.HeaderSize + g * GroupSize;
            var entries = new SortedSet<int>();
            long words = TagRead.I64(data, row + BitmapCountField);
            if (words is < 0 or > int.MaxValue) throw new InvalidDataException("A manifest bitmap count is invalid.");
            if (TagRead.I64(data, row + BitmapField) != 0)
            {
                int header = Target(data, row + BitmapField, TagArray.HeaderSize + words * (long)BitmapWordSize);
                RequireArrayHeader(data, header, words, U32ArrayRow.Class);
                int bitmap = header + TagArray.HeaderSize;
                for (int w = 0; w < words; w++)
                {
                    uint bits = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(bitmap + BitmapWordSize * w));
                    for (int bit = 0; bit < 32; bit++) if ((bits & (1u << bit)) != 0) entries.Add(w * 32 + bit);
                }
            }
            else if (words != 0) throw new InvalidDataException("A manifest bitmap has no data pointer.");
            long listed = TagRead.I64(data, row + ListCountField);
            if (listed < 0 || listed > PackageDependencyTable.MaximumEntry + 1)
                throw new InvalidDataException("A manifest entry count is invalid.");
            if (TagRead.I64(data, row + ListField) != 0)
            {
                int header = Target(data, row + ListField, TagArray.HeaderSize + listed * EntrySize);
                RequireArrayHeader(data, header, listed, SU16Element.Class);
                int list = header + TagArray.HeaderSize;
                for (int i = 0; i < listed; i++)
                    entries.Add(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(list + EntrySize * i)) & PackageDependencyTable.MaximumEntry);
            }
            else if (listed != 0) throw new InvalidDataException("A manifest entry list has no data pointer.");
            groups.Add(new(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(row + PackageIdField)), entries));
        }
        return new(groups);
    }

    /**
     * Adds a group for a package the manifest does not list yet, kept in package order.
     * Every offset that crosses the new row is moved; the entry list is appended at the end.
     * @return The new manifest bytes.
     */
    public static byte[] AddGroup(byte[] data, ushort packageId, IEnumerable<int> entries)
    {
        var manifest = Read(data);
        if (manifest.Groups.Any(group => group.PackageId == packageId))
            throw new NotSupportedException($"The manifest already lists package {packageId:X4}.");
        ushort[] list = entries.Distinct().Order().Select(entry => checked((ushort)entry)).ToArray();
        if (list.Length == 0 || list[^1] > PackageDependencyTable.MaximumEntry)
            throw new ArgumentOutOfRangeException(nameof(entries), "A manifest group needs entries from 0 to 8191.");
        long count = TagRead.I64(data, GroupCountField);
        int array = count == 0 ? -1 : Target(data, GroupArrayField, TagArray.HeaderSize);
        if (array < 0) throw new NotSupportedException("A manifest with no groups has no group array to grow.");
        int index = manifest.Groups.TakeWhile(group => group.PackageId < packageId).Count();
        int insert = array + TagArray.HeaderSize + index * GroupSize;

        // Every offset field the reader follows, as (field, target) before the insert.
        var fields = new List<(int Field, int Target)> { (GroupArrayField, array) };
        if (TagRead.I64(data, KeyCountField) != 0 && TagRead.I64(data, KeyArrayField) != 0)
            fields.Add((KeyArrayField, KeyArrayField + (int)TagRead.I64(data, KeyArrayField)));
        for (int g = 0; g < count; g++)
        {
            int row = array + TagArray.HeaderSize + g * GroupSize;
            foreach (int field in new[] { row + BitmapField, row + ListField })
                if (TagRead.I64(data, field) != 0) fields.Add((field, field + (int)TagRead.I64(data, field)));
        }
        int Shift(int offset) => offset >= insert ? offset + GroupSize : offset;

        int end = Align(data.Length + GroupSize);
        int listHeader = end + TagArray.HeaderSize;
        byte[] result = new byte[listHeader + TagArray.HeaderSize + list.Length * EntrySize];
        data.AsSpan(0, insert).CopyTo(result);
        data.AsSpan(insert).CopyTo(result.AsSpan(insert + GroupSize));
        foreach (var (field, target) in fields)
            WriteI64(result, Shift(field), Shift(target) - Shift(field));

        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(insert + PackageIdField), packageId);
        WriteI64(result, insert + ListCountField, list.Length);
        WriteI64(result, insert + ListField, listHeader - (insert + ListField));
        TagArray.WriteHeader(result, listHeader, list.Length, SU16Element.Class);
        for (int i = 0; i < list.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(listHeader + TagArray.HeaderSize + EntrySize * i), list[i]);

        WriteI64(result, GroupCountField, count + 1);
        WriteI64(result, array, count + 1);
        WriteI64(result, TagSizeField, result.Length);
        return result;
    }

    /** Merged groups keep every old entry while their bitmap and list become one typed list. */
    public static byte[] AddEntries(byte[] data, ushort packageId, IEnumerable<int> entries)
    {
        var manifest = Read(data);
        int index = manifest.Groups.ToList().FindIndex(group => group.PackageId == packageId);
        if (index < 0) return AddGroup(data, packageId, entries);
        int[] requested = entries.Distinct().ToArray();
        if (requested.Any(entry => entry is < 0 or > PackageDependencyTable.MaximumEntry))
            throw new ArgumentOutOfRangeException(nameof(entries), "Manifest entries must fit thirteen bits.");
        var group = manifest.Groups[index];
        int[] combined = group.Entries.Concat(requested).Distinct().Order().ToArray();
        if (combined.Length == group.Entries.Count) return data.ToArray();
        int row = Target(data, GroupArrayField, TagArray.HeaderSize) + TagArray.HeaderSize + index * GroupSize;
        int header = Align(checked(data.Length + TagView.ClassWordSize));
        byte[] result = new byte[checked(header + TagArray.HeaderSize + combined.Length * EntrySize)];
        data.CopyTo(result, 0);
        WriteI64(result, row + BitmapCountField, 0);
        WriteI64(result, row + BitmapField, 0);
        WriteI64(result, row + ListCountField, combined.Length);
        WriteI64(result, row + ListField, header - row - ListField);
        TagArray.WriteHeader(result, header, combined.Length, SU16Element.Class);
        for (int i = 0; i < combined.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(header + TagArray.HeaderSize + i * EntrySize), checked((ushort)combined[i]));
        WriteI64(result, TagSizeField, result.Length);
        return result;
    }

    private static (ushort Package, int Entry) Split(uint tag) => ((ushort)TagHashes.Package(tag), TagHashes.Entry(tag));

    /** Native arrays require their marker, repeated count and element class to agree. */
    private static void RequireArrayHeader(byte[] data, int header, long count, uint type)
    {
        // The header's zero word is not checked.
        var view = new TagArrayHeader(data, header);
        if (!view.IsWellFormed || view.Count != count || view.ElementClass != type)
            throw new InvalidDataException("A manifest typed array header is malformed.");
    }

    /** @return The offset a relative field points at, after checking `size` bytes fit there. */
    private static int Target(byte[] data, int field, long size)
    {
        long target = field + TagRead.I64(data, field);
        if (size < 0 || target < 0 || target > data.Length - size)
            throw new InvalidDataException("A manifest offset leaves the data.");
        return (int)target;
    }

    private static int Align(int value) => (value + 7) & ~7;
    private static void WriteI64(byte[] data, int offset, long value) =>
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offset), value);
}
