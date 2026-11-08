using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/** Socket types: parallel gameplay and display tables, both indexed by the socket's type number. */
public static class InvestmentSocketTypes
{
    /** Root blob slot 94; SocketTypeRow rows hold the insertion rules and plug whitelist. */
    public const uint GameplayTable = 0x8131932F;
    /** investment_globals slot 58; its rows lead with the same hash as the gameplay rows. */
    public const uint DisplayTable = 0x81613D06;
    private static readonly int GameplayRowsField = TagView.FieldOffset<C80807ab5>("Table"),
        DisplayRowsField = TagView.FieldOffset<C80805945>("Table");
    private static readonly int GameplayStride = Unsafe.SizeOf<SocketTypeRow>(), DisplayStride = Unsafe.SizeOf<C80805949>();
    // The plug whitelist holds 8-byte rows led by a plug category hash.
    private static readonly int WhitelistStride = Unsafe.SizeOf<C80807ac0>();
    // Gameplay rows own these arrays; the second is the plug whitelist.
    private static readonly int[] RowArrays =
    [
        TagView.FieldOffset<SocketTypeRow>("H0db6e645.ExpressionSteps"), TagView.FieldOffset<SocketTypeRow>("H8127180a"),
        TagView.FieldOffset<SocketTypeRow>("H321354a2"), TagView.FieldOffset<SocketTypeRow>("HE0b00974")
    ];
    private static readonly int WhitelistField = RowArrays[1];

    public static int Count(ReadOnlyMemory<byte> gameplay) => TagArray.Checked(gameplay.Span, GameplayRowsField, stride: GameplayStride).Count;

    public static IReadOnlyList<uint> Whitelist(ReadOnlyMemory<byte> gameplay, int index)
    {
        var span = gameplay.Span;
        var rows = TagArray.Checked(span, GameplayRowsField, stride: GameplayStride);
        if (index < 0 || index >= rows.Count) throw new InvalidDataException("The socket type is outside the table.");
        var list = TagArray.Checked(span, rows.Data + index * GameplayStride + WhitelistField, stride: WhitelistStride);
        var result = new uint[list.Count];
        for (int row = 0; row < list.Count; row++) result[row] = TagView.At<C80807ac0>(span, list.Data + row * WhitelistStride).Unk0;
        return result;
    }

    /** Every plug category any socket type accepts. */
    public static IReadOnlySet<uint> Categories(ReadOnlyMemory<byte> gameplay) =>
        Enumerable.Range(0, Count(gameplay)).SelectMany(index => Whitelist(gameplay, index)).ToHashSet();

    public static IReadOnlySet<uint> Hashes(ReadOnlyMemory<byte> gameplay)
    {
        var span = gameplay.Span;
        var rows = TagArray.Checked(span, GameplayRowsField, stride: GameplayStride);
        var hashes = new HashSet<uint>();
        for (int index = 0; index < rows.Count; index++)
            hashes.Add(TagView.At<SocketTypeRow>(span, rows.Data + index * GameplayStride).Unk0);
        return hashes;
    }

    /**
     * Appends a copy of one socket type to both tables under a new hash, with its own plug whitelist.
     * The copy keeps the source's insertion rule and unlock expression.
     * @param stockLength Installed gameplay table length, so moved rows leave no dead copy.
     * @return The grown tables and the new type number, which is the same in both.
     */
    public static (byte[] Gameplay, byte[] Display, int Index) Append(ReadOnlyMemory<byte> gameplay,
        ReadOnlyMemory<byte> display, int copyFrom, uint hash, IReadOnlyList<uint> whitelist,
        int stockLength)
    {
        var rows = TagArray.Checked(gameplay.Span, GameplayRowsField, stride: GameplayStride);
        var displayRows = TagArray.Checked(display.Span, DisplayRowsField, stride: DisplayStride);
        if (rows.Count != displayRows.Count) throw new InvalidDataException("The socket type tables differ in length.");
        if (copyFrom < 0 || copyFrom >= rows.Count) throw new InvalidDataException("The socket type to copy is outside the table.");
        if (rows.Count >= short.MaxValue) throw new InvalidDataException("The socket type table has no index left.");
        if (Hashes(gameplay).Contains(hash)) throw new InvalidDataException("The socket type hash is already used.");
        if (whitelist.Count == 0) throw new InvalidDataException("A socket type needs at least one plug category.");
        int source = rows.Data + copyFrom * GameplayStride;
        var sourceList = TagArray.Checked(gameplay.Span, source + WhitelistField, stride: WhitelistStride);
        if (sourceList.Count == 0) throw new InvalidDataException("The source socket type has no whitelist row to copy.");
        byte[] row = gameplay.Span.Slice(source, GameplayStride).ToArray();
        TagView.Edit<SocketTypeRow>(row).Unk0 = hash;
        byte[] entries = new byte[whitelist.Count * WhitelistStride];
        for (int i = 0; i < whitelist.Count; i++)
        {
            gameplay.Span.Slice(sourceList.Data, WhitelistStride).CopyTo(entries.AsSpan(i * WhitelistStride));
            TagView.Edit<C80807ac0>(entries, i * WhitelistStride).Unk0 = whitelist[i];
        }
        // Block before rows: the rows stay last, so the next append reuses their bytes and leaves no dead copy.
        var (output, blocks) = InvestmentItemClone.AppendRows(gameplay,
            [new(GameplayRowsField, GameplayStride, [row]) { RowArrays = RowArrays, RowSources = [source] }],
            [TagRelocator.TypedBlock(whitelist.Count, InvestmentItemClone.ArrayClassData(gameplay.Span, sourceList.Data),
                entries)], stockLength);
        int index = rows.Count;
        int descriptor = TagArray.Checked(output, GameplayRowsField, stride: GameplayStride).Data
            + index * GameplayStride + WhitelistField;
        TagArray.WriteField(output, descriptor, whitelist.Count, blocks[0] + TagRelocator.TypedBlockHeader);

        int displaySource = displayRows.Data + copyFrom * DisplayStride;
        byte[] displayRow = display.Span.Slice(displaySource, DisplayStride).ToArray();
        TagView.Edit<C80805949>(displayRow).Unk0 = hash;
        byte[] displayOutput = InvestmentItemClone.AppendRow(display, DisplayRowsField, DisplayStride, displayRow);
        if (!Whitelist(output, index).SequenceEqual(whitelist) || !Whitelist(output, copyFrom).SequenceEqual(Whitelist(gameplay, copyFrom)))
            throw new InvalidDataException("The new socket type's whitelist did not read back.");
        return (output, displayOutput, index);
    }

    /**
     * The installed gameplay table plus one copy of the rows and of each added whitelist.
     * Type numbers stay; old copies a saved project still carries are dropped.
     * @param original The installed table.
     * @param current The edited table; Studio only appends socket types.
     */
    public static byte[] Rebuild(ReadOnlyMemory<byte> original, ReadOnlyMemory<byte> current)
    {
        var before = TagArray.Checked(original.Span, GameplayRowsField, stride: GameplayStride);
        var after = TagArray.Checked(current.Span, GameplayRowsField, stride: GameplayStride);
        if (after.Count < before.Count) throw new InvalidDataException("The socket type table lost rows.");
        InvestmentItemClone.RequireOnlyChanged(original.Span, current.Span,
            InvestmentItemClone.ArraySpans(original, GameplayRowsField, GameplayStride), "The socket type table changed outside its rows.");
        for (int row = 0; row < before.Count; row++)
            if (!SameRow(original.Span, before.Data + row * GameplayStride, current.Span, after.Data + row * GameplayStride))
                throw new InvalidDataException("An installed socket type changed; Studio only appends socket types.");
        if (after.Count == before.Count) return original.ToArray();
        var rows = new List<byte[]>();
        var blocks = new List<byte[]>();
        var links = new List<(int Row, int Field, int Target, bool Block)>();
        // The installed rows run from the marker word before their header.
        (int Start, int End) installedRows = (before.Data - TagArray.HeaderSize - TagView.ClassWordSize,
            before.Data + before.Count * GameplayStride);
        for (int row = before.Count; row < after.Count; row++)
        {
            int at = after.Data + row * GameplayStride;
            byte[] bytes = current.Span.Slice(at, GameplayStride).ToArray();
            foreach (int field in RowArrays)
            {
                long relative = TagView.At<TagArray<Unknown>>(bytes, field).Relative;
                if (relative == 0) continue;
                BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(field + TagArray.RelativeOffset), 0);
                int header = checked((int)(at + field + TagArray.RelativeOffset + relative));
                if (header < original.Length && (header < installedRows.Start || header >= installedRows.End))
                { links.Add((rows.Count, field, header, false)); continue; }
                // Studio gives an added type only its own whitelist; other arrays stay the source's.
                if (field != WhitelistField || header < original.Length)
                    throw new NotSupportedException("An added socket type owns an array Studio did not write.");
                var list = TagArray.Checked(current.Span, at + field, stride: WhitelistStride);
                links.Add((rows.Count, field, blocks.Count, true));
                blocks.Add(TagRelocator.TypedBlock(list.Count, InvestmentItemClone.ArrayClassData(current.Span, list.Data),
                    current.Span.Slice(list.Data, list.Count * WhitelistStride)));
            }
            rows.Add(bytes);
        }
        var (output, starts) = InvestmentItemClone.AppendRows(original,
            [new(GameplayRowsField, GameplayStride, rows) { RowArrays = RowArrays, RowSources = rows.Select(_ => -1).ToArray() }],
            blocks, original.Length);
        int data = TagArray.Checked(output, GameplayRowsField, stride: GameplayStride).Data;
        foreach (var (row, field, target, block) in links)
        {
            int descriptor = data + (before.Count + row) * GameplayStride + field;
            int header = block ? starts[target] + TagRelocator.TypedBlockHeader : target;
            BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(descriptor + TagArray.RelativeOffset), header - descriptor - TagArray.RelativeOffset);
        }
        return output;
    }

    /** Two rows match when their bytes match and each owned array names the same header. */
    private static bool SameRow(ReadOnlySpan<byte> first, int a, ReadOnlySpan<byte> second, int b)
    {
        for (int offset = 0; offset < GameplayStride; offset++)
        {
            int field = Array.FindIndex(RowArrays, start => offset >= start + TagArray.RelativeOffset && offset < start + TagArray.FieldSize);
            if (field >= 0) { offset = RowArrays[field] + TagArray.FieldSize - 1; continue; }
            if (first[a + offset] != second[b + offset]) return false;
        }
        foreach (int field in RowArrays)
        {
            long x = TagView.At<TagArray<Unknown>>(first, a + field).Relative;
            long y = TagView.At<TagArray<Unknown>>(second, b + field).Relative;
            if ((x == 0) != (y == 0) || x != 0 && a + x != b + y) return false;
        }
        return true;
    }
}
