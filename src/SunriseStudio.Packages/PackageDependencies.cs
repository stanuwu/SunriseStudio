using System.Buffers.Binary;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Packages;

/** One package's entries that a dependency record loads, as encoded run words. */
public sealed class PackageDependencyGroup
{
    public ushort PackageId { get; }
    internal ushort[] Words { get; private set; }

    internal PackageDependencyGroup(ushort packageId, ushort[] words) => (PackageId, Words) = (packageId, words);

    /** @return Every entry index the group marks. */
    public SortedSet<int> Entries()
    {
        var entries = new SortedSet<int>();
        int pending = 0;
        foreach (ushort word in Words)
        {
            int run, start;
            if (pending != 0) { run = pending; start = word & 0x1FFF; pending = 0; }
            else if ((word & 0xE000) != 0) { run = word >> 13; start = word & 0x1FFF; }
            else { pending = word + 1; continue; }
            for (int i = 0; i < run; i++) entries.Add(start + i);
        }
        if (pending != 0) throw new InvalidDataException("A dependency run has no start word.");
        return entries;
    }

    internal void Add(IEnumerable<int> entries)
    {
        var all = Entries();
        all.UnionWith(entries);
        Words = Encode(all);
    }

    /** A run of 1 to 7 is one word; a longer run is a length word, then a start word. */
    internal static ushort[] Encode(IEnumerable<int> entries)
    {
        var words = new List<ushort>();
        int[] sorted = entries.Distinct().Order().ToArray();
        for (int i = 0; i < sorted.Length;)
        {
            if (sorted[i] is < 0 or > PackageDependencyTable.MaximumEntry)
                throw new InvalidDataException($"Entry {sorted[i]} is outside the dependency range.");
            int start = sorted[i], run = 1;
            while (i + run < sorted.Length && sorted[i + run] == start + run) run++;
            if (run <= 7) words.Add((ushort)(run << 13 | start));
            else { words.Add((ushort)(run - 1)); words.Add((ushort)start); }
            i += run;
        }
        return words.ToArray();
    }
}

/** One dependency record: package groups, child records and keyed records, in that order. */
public sealed class PackageDependencyRecord
{
    // Record word flags; the low 29 bits are the body's offset in the stream.
    internal const uint GroupsFlag = 0x80000000, KeysFlag = 0x40000000, ChildrenFlag = 0x20000000, OffsetMask = 0x1FFFFFFF;

    public int Index { get; }
    public IReadOnlyList<PackageDependencyGroup> Groups => groups;
    public IReadOnlyList<ushort> Children { get; }
    public IReadOnlyList<ulong> Keys { get; }
    internal uint Flags { get; private set; }
    internal int Offset { get; }
    internal bool Empty => Flags == 0;
    private readonly List<PackageDependencyGroup> groups;

    internal PackageDependencyRecord(int index, uint flags, int offset, List<PackageDependencyGroup> groups,
        ushort[] children, ulong[] keys) =>
        (Index, Flags, Offset, this.groups, Children, Keys) = (index, flags, offset, groups, children, keys);

    /** @return True when this record's own groups mark the entry; children are not followed. */
    public bool Lists(ushort packageId, int entry) =>
        groups.Any(group => group.PackageId == packageId && group.Entries().Contains(entry));

    /** Marks more entries of one package; a new group keeps the record's package order. */
    internal void Add(ushort packageId, IEnumerable<int> entries)
    {
        var group = groups.FirstOrDefault(item => item.PackageId == packageId);
        if (group is not null) { group.Add(entries); return; }
        int at = groups.FindIndex(item => item.PackageId > packageId);
        group = new PackageDependencyGroup(packageId, PackageDependencyGroup.Encode(entries));
        if (at < 0) groups.Add(group); else groups.Insert(at, group);
        Flags |= GroupsFlag;
    }
}

/**
 * The misc block's dependency records: which entries of which packages load together.
 * The client reads them at 0x7FF741FF75B0. Edits change the model; Write encodes the stream once.
 */
public sealed class PackageDependencyTable
{
    /** Run start words keep 13 bits, the package entry limit. */
    public const int MaximumEntry = 0x1FFF;
    // Misc fields: block size, root record count, then the record word array.
    private const int SizeField = 0, RootCountField = 0x30, CountField = 0x40, ArrayField = 0x48;
    private const uint ArrayMarker = TagArray.Marker, RecordWordClass = U32ArrayRow.Class;

    public IReadOnlyList<PackageDependencyRecord> Records => records;
    /** Records below this index are root records; child indices are relative to it. */
    public int RootCount { get; }
    private readonly PackageDependencyRecord[] records;
    private readonly int words, stream;

    private PackageDependencyTable(PackageDependencyRecord[] records, int rootCount, int words, int stream) =>
        (this.records, RootCount, this.words, this.stream) = (records, rootCount, words, stream);

    /** @return The table, or null when the misc block has no dependency records. */
    public static PackageDependencyTable? Read(byte[] misc)
    {
        ArgumentNullException.ThrowIfNull(misc);
        if (misc.Length < ArrayField + 8) return null;
        long count = I64(misc, CountField);
        if (count == 0) return null;
        int header = checked((int)(ArrayField + I64(misc, ArrayField)));
        // The array header holds its capacity; the field count is the number in use.
        if (count < 0 || header < 4 || header + 16 > misc.Length || U32(misc, header - 4) != ArrayMarker
            || I64(misc, header) < count || U32(misc, header + 8) != RecordWordClass)
            throw new InvalidDataException("The dependency record array is malformed.");
        int stream = checked((int)Align8(I64(misc, SizeField)));
        if (header + 16 + I64(misc, header) * 4 > stream || stream > misc.Length)
            throw new InvalidDataException("The dependency record words overlap their stream.");
        var records = new PackageDependencyRecord[count];
        for (int i = 0; i < count; i++)
        {
            uint word = U32(misc, header + 16 + 4 * i);
            int offset = (int)(word & PackageDependencyRecord.OffsetMask);
            records[i] = word == 0 ? new(i, 0, 0, [], [], [])
                : Parse(misc, i, word & ~PackageDependencyRecord.OffsetMask, stream, offset, out _);
        }
        var live = records.Where(record => !record.Empty).OrderBy(record => record.Offset).ToArray();
        for (int i = 0; i < live.Length; i++)
        {
            Parse(misc, live[i].Index, live[i].Flags, stream, live[i].Offset, out int end);
            int next = i + 1 < live.Length ? live[i + 1].Offset : misc.Length - stream;
            if (end != next) throw new NotSupportedException("Dependency records do not tile their stream.");
        }
        var table = new PackageDependencyTable(records, checked((int)I64(misc, RootCountField)), header + 16, stream);
        if (!table.Write(misc).AsSpan().SequenceEqual(misc))
            throw new NotSupportedException("The dependency encoder does not reproduce this package's records.");
        return table;
    }

    /** @return The misc offset of every group's u16 package id, record by record. */
    public static IReadOnlyList<int> GroupPackageOffsets(byte[] misc)
    {
        if (Read(misc) is not { } table) return [];
        var offsets = new List<int>();
        foreach (var record in table.records.Where(record => !record.Empty && (record.Flags & PackageDependencyRecord.GroupsFlag) != 0))
        {
            int at = checked(table.stream + record.Offset);
            int count = U16(misc, at);
            at += 2;
            for (int group = 0; group < count; group++)
            {
                offsets.Add(at);
                at += 4 + 2 * U16(misc, at + 2);
            }
        }
        return offsets;
    }

    /** Marks more entries in one record; nothing is encoded until Write. */
    public void Add(int record, ushort packageId, IEnumerable<int> entries) => records[record].Add(packageId, entries);

    /** @return The misc block with every record re-encoded in its original order. */
    public byte[] Write(byte[] misc)
    {
        var body = new List<byte>();
        var offsets = new int[records.Length];
        foreach (var record in records.Where(record => !record.Empty).OrderBy(record => record.Offset))
        {
            offsets[record.Index] = body.Count;
            Encode(record, body);
        }
        if (body.Count > PackageDependencyRecord.OffsetMask) throw new InvalidDataException("Dependency records are too large.");
        byte[] output = new byte[checked(stream + body.Count)];
        misc.AsSpan(0, stream).CopyTo(output);
        body.CopyTo(output, stream);
        foreach (var record in records)
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(words + 4 * record.Index),
                record.Empty ? 0 : record.Flags | (uint)offsets[record.Index]);
        return output;
    }

    /** Reads one record's groups, children and keys; end is the stream offset after its body. */
    private static PackageDependencyRecord Parse(byte[] misc, int index, uint flags, int stream, int offset, out int end)
    {
        int at = checked(stream + offset);
        var groups = new List<PackageDependencyGroup>();
        ushort[] children = [];
        ulong[] keys = [];
        if ((flags & PackageDependencyRecord.GroupsFlag) != 0)
        {
            int count = U16(misc, at); at += 2;
            for (int i = 0; i < count; i++)
            {
                ushort package = U16(misc, at), length = U16(misc, at + 2); at += 4;
                var words = new ushort[length];
                for (int k = 0; k < length; k++) words[k] = U16(misc, at + 2 * k);
                at += 2 * length;
                groups.Add(new(package, words));
            }
        }
        if ((flags & PackageDependencyRecord.ChildrenFlag) != 0)
        {
            int count = U16(misc, at); at += 2;
            children = new ushort[count];
            for (int i = 0; i < count; i++) children[i] = U16(misc, at + 2 * i);
            at += 2 * count;
        }
        if ((flags & PackageDependencyRecord.KeysFlag) != 0)
        {
            int count = U16(misc, at);
            at = checked(stream + (int)Align8(at + 2 - stream));
            keys = new ulong[count];
            for (int i = 0; i < count; i++) keys[i] = BinaryPrimitives.ReadUInt64LittleEndian(misc.AsSpan(at + 8 * i));
            at += 8 * count;
        }
        end = at - stream;
        return new(index, flags, offset, groups, children, keys);
    }

    /** Key lists start on an 8-byte boundary of the stream; the gap is zero. */
    private static void Encode(PackageDependencyRecord record, List<byte> body)
    {
        if ((record.Flags & PackageDependencyRecord.GroupsFlag) != 0)
        {
            Put16(body, record.Groups.Count);
            foreach (var group in record.Groups)
            {
                Put16(body, group.PackageId);
                Put16(body, group.Words.Length);
                foreach (ushort word in group.Words) Put16(body, word);
            }
        }
        if ((record.Flags & PackageDependencyRecord.ChildrenFlag) != 0)
        {
            Put16(body, record.Children.Count);
            foreach (ushort child in record.Children) Put16(body, child);
        }
        if ((record.Flags & PackageDependencyRecord.KeysFlag) != 0)
        {
            Put16(body, record.Keys.Count);
            while (body.Count % 8 != 0) body.Add(0);
            foreach (ulong key in record.Keys)
                for (int shift = 0; shift < 64; shift += 8) body.Add((byte)(key >> shift));
        }
    }

    private static void Put16(List<byte> body, int value)
    {
        if (value is < 0 or > ushort.MaxValue) throw new InvalidDataException("A dependency count exceeds 16 bits.");
        body.Add((byte)value); body.Add((byte)(value >> 8));
    }

    private static long Align8(long value) => (value + 7) & ~7L;
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static long I64(byte[] bytes, int offset) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
}
