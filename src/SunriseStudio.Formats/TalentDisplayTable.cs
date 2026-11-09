/**
 * File: The client's talent display table (class 0x80805C3C, tag 0x81613D09 in 86657): one row per socket-entry list,
 * {list row hash, display record (class 0x80805C42)}, in the same order and with the same hashes as the
 * socket-entry-list table (0x8131932C). The display record holds the subclass screen's node rows (class 0x80805C46,
 * each naming a node record 0x80805C49) and its attunement rows. A list with no display row draws an empty talent
 * grid (no super, no nodes, neutral background). Parhelion keeps both tables the same length (subclass/tables.rs).
 */
using System.Buffers.Binary;

namespace SunriseStudio.Formats;

public static class TalentDisplayTable
{
    /** The table tag and classes in 86657. */
    public const uint TableTag = 0x81613D09, TableClass = 0x80805C3C, RowClass = 0x80805C40, DisplayClass = 0x80805C42;

    /**
     * Appends display rows for lists the socket-entry-list table has and this table lacks. Each new row takes the list's
     * row hash from the socket-entry-list table and shares its base list's display record (installed rows share
     * records too, e.g. lists 4, 8 and 12). rows: (list, base) pairs; lists must follow the existing rows in order.
     * @return The new table, and per appended list its (hash, display record).
     */
    public static (byte[] Table, IReadOnlyList<(int List, uint Hash, uint Display)> Added) AppendRows(
        ReadOnlyMemory<byte> table, ReadOnlyMemory<byte> listTable, IReadOnlyList<(int List, int Base)> rows)
    {
        var displays = InvestmentItemClone.ReadIndexRows(table);
        var lists = InvestmentItemClone.ReadIndexRows(listTable);
        if (displays.Count > lists.Count)
            throw new InvalidDataException($"The display table has {displays.Count} rows, more than the {lists.Count} lists.");
        int shared = Math.Min(displays.Count, lists.Count);
        for (int index = 0; index < shared; index++)
            if (displays[index].Hash != lists[index].Hash)
                throw new InvalidDataException($"Display row {index} hash 0x{displays[index].Hash:X8} does not match list row hash "
                    + $"0x{lists[index].Hash:X8}; nothing changed.");
        byte[] current = table.ToArray();
        var added = new List<(int, uint, uint)>();
        foreach (var (list, source) in rows.Where(row => row.List >= displays.Count).OrderBy(row => row.List))
        {
            int next = InvestmentItemClone.ReadIndexRows(current).Count;
            if (list != next) throw new InvalidDataException($"List {list} must follow the existing display rows (next is {next}).");
            if (list >= lists.Count) throw new InvalidDataException($"List {list} is not in the socket-entry-list table ({lists.Count} lists).");
            if (source < 0 || source >= displays.Count)
                throw new InvalidDataException($"Base list {source} has no installed display row.");
            uint hash = lists[list].Hash, display = displays[source].Tag;
            (current, int index) = InvestmentItemClone.AppendIndexRow(current, hash, display);
            if (index != list) throw new InvalidDataException($"Display row landed at {index}, expected {list}.");
            added.Add((list, hash, display));
        }
        return (current, added);
    }

    /**
     * Points one added list's display row at another display record (e.g. a project copy with its own node records), so
     * that list stops sharing its base list's record. Installed rows (list &lt; installedCount) are refused, so shipped
     * subclasses never change.
     * @param installedCount The installed table's row count.
     */
    public static byte[] SetRecord(ReadOnlyMemory<byte> table, int installedCount, int list, uint record)
    {
        var rows = new TagReader(table).Array(8, RowClass, RowStride);
        if (list < installedCount)
            throw new InvalidDataException($"List {list} is a shipped row (the installed table has {installedCount}); only added lists can change.");
        if (list >= rows.Count) throw new InvalidDataException($"List {list} has no display row ({rows.Count} rows).");
        if (record is 0 or uint.MaxValue) throw new InvalidDataException("Give a display record tag.");
        byte[] output = table.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(rows.Data + list * RowStride + RecordOffset), record);
        return output;
    }

    /** The display record one row names. */
    public static uint ReadRecord(ReadOnlyMemory<byte> table, int list)
    {
        var reader = new TagReader(table);
        var rows = reader.Array(8, RowClass, RowStride);
        if (list < 0 || list >= rows.Count) throw new InvalidDataException($"List {list} has no display row ({rows.Count} rows).");
        return reader.U32(rows.Data + list * RowStride + RecordOffset);
    }

    private const int RowStride = 24, RecordOffset = 16;

    /** The table's row count (u32 at the array descriptor +8). */
    public static int Count(ReadOnlySpan<byte> table) => (int)BinaryPrimitives.ReadUInt64LittleEndian(table[8..]);
}
