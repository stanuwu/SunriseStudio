using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

public readonly record struct InvestmentString(uint Bank, uint Hash);
public sealed record InvestmentLoreRow(int Index, uint Hash, InvestmentString Title, InvestmentString Subtitle,
    InvestmentString Body);

/** An item's lore block holds a u16 row index into two parallel tables of the same length. */
public static class InvestmentLore
{
    /** investment_globals slot 34; LoreTextRow rows hold the lore strings. */
    public const uint ClientTable = 0x81613CF2;
    /** Root blob +840; LoreRow rows. Its reader has no bounds check, so both tables grow together. */
    public const uint ExeTable = 0x81319348;
    public const ushort None = 0xFFFF;
    private static readonly int ClientRowsField = TagView.FieldOffset<C80805ab6>("Table"),
        ExeRowsField = TagView.FieldOffset<C808077ee>("Table");
    private static readonly int ClientStride = Unsafe.SizeOf<LoreTextRow>(), ExeStride = Unsafe.SizeOf<LoreRow>();
    // The item definition points at its lore block; the block's class marker precedes it.
    private static readonly int DefinitionBlock = TagView.FieldOffset<ItemDefinition>("LoreItemBlock");
    /** An empty subtitle names no bank and the empty-string hash. */
    public static readonly InvestmentString Empty = new(None, StringHash.Empty);

    /** The lore block's offset, or null when the item has none. */
    public static int? BlockOffset(ReadOnlySpan<byte> definition)
    {
        if (!TagView.Fits<RelativePointer<SItemLoreBlock>>(definition, DefinitionBlock)) return null;
        ref readonly var pointer = ref TagView.At<RelativePointer<SItemLoreBlock>>(definition, DefinitionBlock);
        if (pointer.IsNull) return null;
        long at = DefinitionBlock + pointer.Relative;
        if (at < TagView.ClassWordSize || at > definition.Length - TagView.ClassWordSize || TagView.TargetClass(definition, in pointer) != SItemLoreBlock.Class)
            throw new InvalidDataException("The item's lore block is not where its pointer says.");
        return (int)at;
    }

    public static ushort? ReadIndex(ReadOnlySpan<byte> definition) =>
        BlockOffset(definition) is { } at ? TagView.At<SItemLoreBlock>(definition, at).LoreTextRow : null;

    public static byte[] SetIndex(ReadOnlySpan<byte> definition, ushort index)
    {
        int at = BlockOffset(definition) ?? throw new InvalidDataException("The item has no lore block.");
        byte[] output = definition.ToArray();
        TagView.Edit<SItemLoreBlock>(output, at).LoreTextRow = index;
        return output;
    }

    public static int Count(ReadOnlyMemory<byte> client) => TagArray.Checked(client.Span, ClientRowsField, stride: ClientStride).Count;

    public static InvestmentLoreRow ReadRow(ReadOnlyMemory<byte> client, int index)
    {
        var span = client.Span;
        var rows = TagArray.Checked(span, ClientRowsField, stride: ClientStride);
        if (index < 0 || index >= rows.Count) throw new InvalidDataException("The lore row is outside the table.");
        ref readonly var row = ref TagView.At<LoreTextRow>(span, rows.Data + index * ClientStride);
        return new(index, row.LoreHash, new(row.TitleBank, row.TitleHash), new(row.SubtitleBank, row.SubtitleHash),
            new(row.BodyBank, row.BodyHash));
    }

    public static IReadOnlySet<uint> Hashes(ReadOnlyMemory<byte> client)
    {
        var span = client.Span;
        var rows = TagArray.Checked(span, ClientRowsField, stride: ClientStride);
        var hashes = new HashSet<uint>();
        for (int index = 0; index < rows.Count; index++)
            hashes.Add(TagView.At<LoreTextRow>(span, rows.Data + index * ClientStride).LoreHash);
        return hashes;
    }

    /**
     * Appends a copy of one row to both tables, under a new lore hash.
     * @return The grown tables and the new row's index, which is the same in both.
     */
    public static (byte[] Client, byte[] Exe, int Index) Append(ReadOnlyMemory<byte> client, ReadOnlyMemory<byte> exe,
        int copyFrom, uint hash)
    {
        var clientRows = TagArray.Checked(client.Span, ClientRowsField, stride: ClientStride);
        var exeRows = TagArray.Checked(exe.Span, ExeRowsField, stride: ExeStride);
        if (clientRows.Count != exeRows.Count) throw new InvalidDataException("The two lore tables differ in length.");
        if (copyFrom < 0 || copyFrom >= clientRows.Count) throw new InvalidDataException("The lore row to copy is outside the table.");
        if (clientRows.Count >= short.MaxValue) throw new InvalidDataException("The lore table has no index left.");
        if (Hashes(client).Contains(hash)) throw new InvalidDataException("The lore hash is already used.");
        byte[] clientRow = client.Span.Slice(clientRows.Data + copyFrom * ClientStride, ClientStride).ToArray();
        byte[] exeRow = exe.Span.Slice(exeRows.Data + copyFrom * ExeStride, ExeStride).ToArray();
        TagView.Edit<LoreTextRow>(clientRow).LoreHash = hash;
        TagView.Edit<LoreRow>(exeRow).LoreHash = hash;
        return (InvestmentItemClone.AppendRow(client, ClientRowsField, ClientStride, clientRow),
            InvestmentItemClone.AppendRow(exe, ExeRowsField, ExeStride, exeRow), clientRows.Count);
    }

    public static byte[] SetStrings(ReadOnlyMemory<byte> client, int index, InvestmentString title,
        InvestmentString subtitle, InvestmentString body)
    {
        var rows = TagArray.Checked(client.Span, ClientRowsField, stride: ClientStride);
        if (index < 0 || index >= rows.Count) throw new InvalidDataException("The lore row is outside the table.");
        byte[] output = client.ToArray();
        // Each bank is a u16; the two bytes after it keep their stored values.
        ref var row = ref TagView.Edit<LoreTextRow>(output, rows.Data + index * ClientStride);
        row.TitleBank = Bank(title); row.TitleHash = title.Hash;
        row.SubtitleBank = Bank(subtitle); row.SubtitleHash = subtitle.Hash;
        row.BodyBank = Bank(body); row.BodyHash = body.Hash;
        return output;
    }

    private static ushort Bank(InvestmentString value) => value.Bank <= ushort.MaxValue
        ? (ushort)value.Bank : throw new InvalidDataException("A lore string bank must fit 16 bits.");
}
