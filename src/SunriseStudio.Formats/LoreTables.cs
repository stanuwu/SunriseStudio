/**
 * File: The lore tables (86657) and an item's lore index, as Parhelion's item/lore.rs writes them.
 *
 * Lore definitions (investment root slot 52, tag 0x81319348, rows class 0x808077F4, 16 bytes: u64 16, u32 row hash) and
 * lore display (globals child 34, tag 0x81613CF2, rows class 0x80805ABA, 40 bytes: u64 40, u32 row hash, then three
 * localized references {u32 bank index, u32 string hash} at +12 title, +20 subtitle (the bold tagline), +28 body, and a
 * u16 at +36) run in step: same count, same row hashes. An item names its lore row through the block its +0x28
 * relative pointer reaches (class 0x808077ED, u16 lore index); for the subclass items that block sits at +0x1FC.
 */
using System.Buffers.Binary;

namespace SunriseStudio.Formats;

public static class LoreTables
{
    public const uint DefinitionsTag = 0x81319348, DisplayTag = 0x81613CF2;
    public const uint DefinitionRowClass = 0x808077F4, DisplayRowClass = 0x80805ABA, ItemBlockClass = 0x808077ED;
    public const int DefinitionStride = 16, DisplayStride = 40, RowHash = 8, Title = 12, Subtitle = 20, Body = 28;
    private const int TableArray = 8, ItemBlockPointer = 0x28;

    /** The row hashes of both tables; throws unless they are in step. */
    public static IReadOnlyList<uint> RowHashes(ReadOnlyMemory<byte> definitions, ReadOnlyMemory<byte> display)
    {
        var first = Hashes(definitions, DefinitionRowClass, DefinitionStride);
        var second = Hashes(display, DisplayRowClass, DisplayStride);
        if (!first.SequenceEqual(second)) throw new InvalidDataException("The lore definition and display rows are not in step.");
        return first;
    }

    private static uint[] Hashes(ReadOnlyMemory<byte> table, uint rowClass, int stride)
    {
        var reader = new TagReader(table);
        var (count, data, _) = reader.Array(TableArray, rowClass, stride);
        if (data + count * stride != table.Length) throw new InvalidDataException("A lore table does not end with its rows.");
        return Enumerable.Range(0, count).Select(index =>
        {
            int row = data + index * stride;
            if (reader.U64(row) != (ulong)stride) throw new InvalidDataException($"Lore row {index} has a changed size marker.");
            return reader.U32(row + RowHash);
        }).ToArray();
    }

    /** The u16 lore index an item names, and where it sits; null when the item has no lore block. */
    public static (int Index, int Offset)? ReadItemLore(ReadOnlySpan<byte> item)
    {
        if (item.Length < ItemBlockPointer + 8) return null;
        long relative = BinaryPrimitives.ReadInt64LittleEndian(item[ItemBlockPointer..]);
        if (relative == 0) return null;
        long block = ItemBlockPointer + relative;
        if (block < 4 || block + 8 > item.Length || BinaryPrimitives.ReadUInt32LittleEndian(item[(int)(block - 4)..]) != ItemBlockClass)
            throw new InvalidDataException("The item's lore block has an unknown class.");
        return (BinaryPrimitives.ReadUInt16LittleEndian(item[(int)block..]), (int)block);
    }

    /**
     * Appends one lore row to both tables, copied from the template row (so it keeps that row's other fields), with a
     * new row hash and the three string references, and points the item at it. The item must already have a lore block.
     * @return Both tables, the item, and the new row index.
     */
    public static (byte[] Definitions, byte[] Display, byte[] Item, int Index) AddItemLore(ReadOnlyMemory<byte> definitions,
        ReadOnlyMemory<byte> display, ReadOnlyMemory<byte> item, int template, uint rowHash, uint bank,
        uint title, uint subtitle, uint body)
    {
        var hashes = RowHashes(definitions, display);
        if (template < 0 || template >= hashes.Count) throw new InvalidDataException($"Template lore row {template} does not exist.");
        if (hashes.Contains(rowHash) || rowHash is 0 or uint.MaxValue) throw new InvalidDataException("The lore row hash is already used.");
        int index = hashes.Count;
        if (index >= ushort.MaxValue) throw new InvalidDataException("The lore table is full.");
        var lore = ReadItemLore(item.Span) ?? throw new InvalidDataException("The item has no lore block to point at the new row.");

        var definitionRows = new TagReader(definitions).Array(TableArray, DefinitionRowClass, DefinitionStride);
        byte[] definitionRow = definitions.Slice(definitionRows.Data + template * DefinitionStride, DefinitionStride).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(definitionRow.AsSpan(RowHash), rowHash);
        byte[] newDefinitions = InvestmentItemClone.AppendRow(definitions, TableArray, DefinitionStride, definitionRow);

        var displayRows = new TagReader(display).Array(TableArray, DisplayRowClass, DisplayStride);
        byte[] displayRow = display.Slice(displayRows.Data + template * DisplayStride, DisplayStride).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(displayRow.AsSpan(RowHash), rowHash);
        foreach (var (field, hash) in new[] { (Title, title), (Subtitle, subtitle), (Body, body) })
        {
            BinaryPrimitives.WriteUInt32LittleEndian(displayRow.AsSpan(field), bank);
            BinaryPrimitives.WriteUInt32LittleEndian(displayRow.AsSpan(field + 4), hash);
        }
        byte[] newDisplay = InvestmentItemClone.AppendRow(display, TableArray, DisplayStride, displayRow);

        byte[] newItem = item.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(newItem.AsSpan(lore.Offset), (ushort)index);
        var after = RowHashes(newDefinitions, newDisplay);
        if (after.Count != index + 1 || after[index] != rowHash) throw new InvalidDataException("The lore row did not append.");
        return (newDefinitions, newDisplay, newItem, index);
    }

    /** Rewrites the title, subtitle and body references of one display row; nothing else changes. */
    public static byte[] SetReferences(ReadOnlyMemory<byte> display, int index, uint bank, uint title, uint subtitle, uint body)
    {
        var rows = new TagReader(display).Array(TableArray, DisplayRowClass, DisplayStride);
        if (index < 0 || index >= rows.Count) throw new InvalidDataException($"Lore row {index} does not exist.");
        byte[] output = display.ToArray();
        int row = rows.Data + index * DisplayStride;
        foreach (var (field, hash) in new[] { (Title, title), (Subtitle, subtitle), (Body, body) })
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(row + field), bank);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(row + field + 4), hash);
        }
        return output;
    }

    /** The {bank, hash} reference at a display row field. */
    public static (uint Bank, uint Hash) ReadReference(ReadOnlyMemory<byte> display, int index, int field)
    {
        var reader = new TagReader(display);
        var rows = reader.Array(TableArray, DisplayRowClass, DisplayStride);
        if (index < 0 || index >= rows.Count) throw new InvalidDataException($"Lore row {index} does not exist.");
        int row = rows.Data + index * DisplayStride;
        return (reader.U32(row + field), reader.U32(row + field + 4));
    }
}
