using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

public sealed record InvestmentIndexRow(int Index, uint Hash, uint Tag);
public sealed record InvestmentBankRow(int Index, uint NameHash, uint ContainerTag);

// TODO: callers move to TagRelocator.ArrayGrowth file by file; delete this once none is left.
/** Rows to append to the typed array whose descriptor is at Field. */
public sealed record RowAppend(int Field, int Stride, IReadOnlyList<byte[]> Rows)
{
    /** Row offsets of the array descriptors each row owns; moved rows keep their targets. */
    public IReadOnlyList<int> RowArrays { get; init; } = [];
    /** Per new row, the offset of the row it was copied from, or -1 when it owns no array. */
    public IReadOnlyList<int>? RowSources { get; init; }
}

/** Byte steps that give a cloned item its own index rows, strings and icon. */
public static class InvestmentItemClone
{
    // Every index table's rows share the item index row layout.
    public static readonly int IndexStride = Unsafe.SizeOf<ItemIndexRow>(),
        IndexHash = TagView.FieldOffset<ItemIndexRow>(nameof(ItemIndexRow.ItemHash)),
        IndexTag = TagView.FieldOffset<ItemIndexRow>(nameof(ItemIndexRow.ItemDefinition));
    private static readonly int IndexPointer = TagView.FieldOffset<ItemIndexRow>(nameof(ItemIndexRow.Unk8));
    // Bank index rows come from the one string bank index table.
    public static readonly int BankStride = Unsafe.SizeOf<StringBankRow>();
    // Display record: icon index u16; {u16 bank, 2 kept bytes, u32 hash} for name, type and flavor.
    public static readonly int DisplayIcon = TagView.FieldOffset<ItemDisplayStrings>("DisplayProperties.IconIndex"),
        DisplayName = TagView.FieldOffset<ItemDisplayStrings>("DisplayProperties.NameBank"),
        DisplayType = TagView.FieldOffset<ItemDisplayStrings>("DisplayProperties.TypeBank"),
        DisplayFlavor = TagView.FieldOffset<ItemDisplayStrings>("DisplayProperties.DescriptionBank"),
        DisplayLength = TagView.FieldOffset<ItemDisplayStrings>("DisplayProperties.DescriptionHash") + sizeof(uint);
    // Each display string's hash sits this far after its bank; a pair spans both words.
    private static readonly int DisplayHash = TagView.FieldOffset<ItemDisplayStrings>("DisplayProperties.NameHash") - DisplayName,
        DisplayPair = DisplayHash + sizeof(uint);
    /** The client's index lookup sign-extends a 16-bit index, so rows at 0x8000 and above are unreachable. */
    public const int MaximumIndexRows = 0x8000;
    private static readonly int TableArray = TagView.FieldOffset<ItemIndexTable>(nameof(ItemIndexTable.Table));
    private static readonly int BankRowsField = TagView.FieldOffset<StringBankIndex>("Table");

    public static IReadOnlyList<InvestmentIndexRow> ReadIndexRows(ReadOnlyMemory<byte> table)
    {
        var span = table.Span;
        var rows = TagArray.Checked(span, TableArray, stride: IndexStride);
        var result = new InvestmentIndexRow[rows.Count];
        for (int index = 0; index < rows.Count; index++)
        {
            int row = rows.Data + index * IndexStride;
            result[index] = new InvestmentIndexRow(index, TagRead.U32(span, row + IndexHash), TagRead.U32(span, row + IndexTag));
        }
        return result;
    }

    public static IReadOnlyList<InvestmentBankRow> ReadBankRows(ReadOnlyMemory<byte> table)
    {
        var span = table.Span;
        var rows = TagArray.Checked(span, BankRowsField, stride: BankStride);
        var result = new InvestmentBankRow[rows.Count];
        for (int index = 0; index < rows.Count; index++)
        {
            ref readonly var row = ref TagView.At<StringBankRow>(span, rows.Data + index * BankStride);
            result[index] = new InvestmentBankRow(index, row.NameHash, row.Container);
        }
        return result;
    }

    /** Null when every row's undecoded bytes are zero, else the first row that differs. */
    public static string? UndecodedIndexBytes(ReadOnlyMemory<byte> table)
    {
        var span = table.Span;
        var rows = TagArray.Checked(span, TableArray, stride: IndexStride);
        for (int index = 0; index < rows.Count; index++)
        {
            var row = span.Slice(rows.Data + index * IndexStride, IndexStride);
            if (!TagView.At<ItemIndexRow>(row).Unk8.IsNull
                || row[(IndexHash + sizeof(uint))..IndexPointer].ContainsAnyExcept((byte)0)
                || row[(IndexTag + sizeof(uint))..].ContainsAnyExcept((byte)0))
                return $"Index row {index} has non-zero bytes outside its hash and tag. Their meaning is not decoded.";
        }
        return null;
    }

    /**
     * Appends an index row whose undecoded bytes are zero, like every installed row.
     * @return The table and the new row's index.
     */
    public static (byte[] Table, int Index) AppendIndexRow(ReadOnlyMemory<byte> table, uint hash, uint tag)
    {
        if (UndecodedIndexBytes(table) is { } problem) throw new InvalidDataException(problem);
        int index = TagArray.Checked(table.Span, TableArray, stride: IndexStride).Count;
        if (index + 1 > MaximumIndexRows) throw new InvalidDataException("The index table has no reachable row left.");
        byte[] row = new byte[IndexStride];
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(IndexHash), hash);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(IndexTag), tag);
        return (AppendRow(table, TableArray, IndexStride, row), index);
    }

    public static (byte[] Table, int Index) AppendBankRow(ReadOnlyMemory<byte> table, uint nameHash, uint containerTag)
    {
        int index = TagArray.Checked(table.Span, BankRowsField, stride: BankStride).Count;
        byte[] row = new byte[BankStride];
        ref var added = ref TagView.Edit<StringBankRow>(row);
        added.NameHash = nameHash;
        added.Container = containerTag;
        return (AppendRow(table, BankRowsField, BankStride, row), index);
    }

    /**
     * Adds one row to a typed array without moving any other field.
     * @param field Offset of the array descriptor.
     * @param row Row bytes; its length is the stride.
     * @param rowArrays Row offsets of the array descriptors each row owns; moved rows keep their targets.
     * @param rowSource Offset of the row `row` was copied from, whose array offsets it still holds.
     * @return Rows at the blob end grow in place; other arrays move to a new block at the end.
     */
    public static byte[] AppendRow(ReadOnlyMemory<byte> table, int field, int stride, ReadOnlySpan<byte> row,
        IReadOnlyList<int>? rowArrays = null, int rowSource = -1) =>
        AppendRows(table, [new(field, stride, [row.ToArray()]) { RowArrays = rowArrays ?? [], RowSources = [rowSource] }]).Table;

    /** TagRelocator.GrowArrays with RowAppend rows. */
    public static (byte[] Table, int[] Blocks) AppendRows(ReadOnlyMemory<byte> table, IReadOnlyList<RowAppend> appends,
        IReadOnlyList<byte[]>? blocks = null, int stockLength = int.MaxValue) =>
        TagRelocator.GrowArrays(table, appends.Select(append => new ArrayGrowth(append.Field, append.Stride, append.Rows)
            { RowArrays = append.RowArrays, RowSources = append.RowSources }).ToArray(), blocks, stockLength);

    /**
     * A table rebuild may start from the installed bytes only when the edit left the rest alone.
     * @param kept Installed ranges the edit may change, as start and length.
     * @param problem The error text when a byte outside them changed.
     */
    public static void RequireOnlyChanged(ReadOnlySpan<byte> original, ReadOnlySpan<byte> current,
        IEnumerable<(int Start, int Length)> kept, string problem)
    {
        if (current.Length < original.Length) throw new InvalidDataException(problem);
        var open = new bool[original.Length];
        foreach (var (start, length) in kept) open.AsSpan(start, length).Fill(true);
        for (int offset = 0; offset < original.Length; offset++)
            if (!open[offset] && original[offset] != current[offset]) throw new InvalidDataException(problem);
    }

    /** @return The installed ranges a table's size word, descriptor and typed array span. */
    public static IEnumerable<(int Start, int Length)> ArraySpans(ReadOnlyMemory<byte> original, int field, int stride)
    {
        if (TagView.HasSizeWord(original.Span)) yield return (0, TagView.SizeWordSize);
        yield return (field, TagArray.FieldSize);
        var rows = TagArray.Checked(original.Span, field, stride: stride);
        // The run starts at the marker word before the header.
        const int framing = TagView.ClassWordSize + TagArray.HeaderSize;
        if (rows.Count > 0) yield return (rows.Data - framing, framing + rows.Count * stride);
    }

    /** @return The class word and zero word of the array header whose rows start at `data`. */
    internal static ReadOnlySpan<byte> ArrayClassData(ReadOnlySpan<byte> tag, int data) =>
        tag.Slice(data - TagArray.HeaderSize + TagArray.ElementClassOffset, TagArray.HeaderSize - TagArray.ElementClassOffset);

    public static (uint Bank, uint Hash) ReadDisplayString(ReadOnlySpan<byte> display, int offset)
    {
        RequireDisplay(display, offset);
        return (TagRead.U16(display, offset), TagRead.U32(display, offset + DisplayHash));
    }

    /** The bank is a u16; the 2 bytes after it are kept. */
    public static byte[] SetDisplayString(ReadOnlySpan<byte> display, int offset, uint bank, uint hash)
    {
        RequireDisplay(display, offset);
        if (offset != DisplayName && offset != DisplayType && offset != DisplayFlavor) throw new ArgumentOutOfRangeException(nameof(offset));
        if (bank > ushort.MaxValue) throw new InvalidDataException("A display string bank must fit 16 bits.");
        byte[] output = display.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(offset), (ushort)bank);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset + DisplayHash), hash);
        return output;
    }

    public static ushort ReadDisplayIcon(ReadOnlySpan<byte> display)
    {
        RequireDisplay(display, DisplayIcon);
        return TagRead.U16(display, DisplayIcon);
    }

    /** The icon index selects a shared icon row; 0xFFFF means no icon. */
    public static byte[] SetDisplayIcon(ReadOnlySpan<byte> display, ushort icon)
    {
        RequireDisplay(display, DisplayIcon);
        byte[] output = display.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(DisplayIcon), icon);
        return output;
    }

    /** A name hash for a new bank row; a table already sorted by name hash stays sorted. */
    public static uint NewBankNameHash(string key, IReadOnlyList<InvestmentBankRow> rows)
    {
        bool sorted = true;
        for (int i = 1; i < rows.Count && sorted; i++) sorted = rows[i - 1].NameHash < rows[i].NameHash;
        var used = rows.Select(row => row.NameHash).ToHashSet();
        uint last = rows.Count == 0 ? 0 : rows[^1].NameHash;
        for (int attempt = 0; attempt < 65536; attempt++)
        {
            uint hash = StringBankWriter.Hash(attempt == 0 ? key : $"{key}#{attempt}");
            if (hash is 0 or uint.MaxValue or StringHash.Empty || used.Contains(hash)) continue;
            if (!sorted || hash > last) return hash;
        }
        throw new InvalidDataException("No bank name hash keeps the bank index order.");
    }

    // Every installed item definition repeats its own hash here.
    public static readonly int DefinitionHashField = TagView.FieldOffset<ItemDefinition>("DefinitionHash");

    /** A copied definition must name its own hash, not the base item's. */
    public static byte[] SetDefinitionHash(ReadOnlySpan<byte> definition, uint hash)
    {
        if (!TagView.FitsThrough<ItemDefinition>(definition, 0, "DefinitionHash"))
            throw new InvalidDataException("The item definition is truncated.");
        byte[] result = definition.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(DefinitionHashField), hash);
        return result;
    }

    /** Offsets of 4-byte-aligned words equal to a value, such as an item's own hash. */
    public static IReadOnlyList<int> FindAlignedValue(ReadOnlySpan<byte> bytes, uint value)
    {
        var offsets = new List<int>();
        for (int offset = 0; offset + 4 <= bytes.Length; offset += 4)
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]) == value) offsets.Add(offset);
        return offsets;
    }

    private static void RequireDisplay(ReadOnlySpan<byte> display, int offset)
    {
        if (display.Length < DisplayLength || offset < 0 || offset + DisplayPair > display.Length)
            throw new InvalidDataException("The item display record is truncated.");
    }
}
