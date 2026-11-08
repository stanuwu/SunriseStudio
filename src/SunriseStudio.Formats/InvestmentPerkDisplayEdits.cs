using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

/** Item display rows pair by index with the item's sandbox perk rows. */
public static class InvestmentPerkDisplayEdits
{
    private static readonly int SizeField = TagView.FieldOffset<ItemDisplayStrings>(nameof(ItemDisplayStrings.TagSize)),
        BlockPointer = TagView.FieldOffset<ItemDisplayStrings>("StatsItemBlock"),
        Stride = Unsafe.SizeOf<SItemPerkDisplayRow>();

    public static int ReadCount(ReadOnlyMemory<byte> display) => Read(display).Count;

    /** Fresh perks have no inherited text override, visibility expression or activation-display mode. */
    public static byte[] Reset(ReadOnlyMemory<byte> display, int perkCount)
    {
        if (perkCount < 0 || perkCount > 4096)
            throw new InvalidDataException("The item has too many perk display rows.");
        var rows = Read(display);
        if (rows.Field < 0)
        {
            if (perkCount == 0) return display.ToArray();
            throw new NotSupportedException("The item needs an existing native perk display block.");
        }
        byte[] empty = new byte[Stride];
        ref var fresh = ref TagView.Edit<SItemPerkDisplayRow>(empty);
        fresh.Unknown0 = ushort.MaxValue;
        fresh.TextOverrideHash = StringHash.Empty;
        if (rows.Count == perkCount && Enumerable.Range(0, rows.Count)
            .All(index => display.Span.Slice(rows.Data + index * Stride, Stride).SequenceEqual(empty)))
            return display.ToArray();
        if (perkCount == 0)
        {
            byte[] cleared = display.ToArray();
            cleared.AsSpan(rows.Field, Unsafe.SizeOf<TagArray<SItemPerkDisplayRow>>()).Clear();
            return cleared;
        }
        byte[] output = display.ToArray();
        int first = TagRelocator.AppendArray(ref output, rows.Field, SItemPerkDisplayRow.Class, perkCount, checked(perkCount * Stride));
        for (int index = 0; index < perkCount; index++) empty.CopyTo(output, first + index * Stride);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(SizeField), output.Length);
        return output;
    }

    /** The native display block owns a typed 32-byte row array. */
    private static (int Field, int Count, int Data) Read(ReadOnlyMemory<byte> display)
    {
        var span = display.Span;
        if (display.Length < InvestmentItemClone.DisplayLength || TagRead.U64(span, SizeField) != (ulong)display.Length)
            throw new InvalidDataException("The item display size is invalid.");
        int block = TagPointer.Checked(span, BlockPointer);
        if (block < 0) return (-1, 0, 0);
        if (block < 4 || !TagView.Fits<SItemPerkDisplayBlock>(span, block)
            || new TagPointer(span, BlockPointer).TargetClass != SItemPerkDisplayBlock.Class)
            throw new InvalidDataException("The item perk display block has an unsupported layout.");
        int field = TagView.OffsetOf(span, in TagView.At<SItemPerkDisplayBlock>(span, block).ItemPerks);
        var rows = TagArray.Checked(span, field, SItemPerkDisplayRow.Class, Stride);
        if (rows.Count == 0 && new TagArray(span, field).HasHeader)
            throw new InvalidDataException("The empty perk display array has an unsupported pointer.");
        for (int index = 0; index < rows.Count; index++)
        {
            ref readonly var row = ref TagView.At<SItemPerkDisplayRow>(span, rows.Data + index * Stride);
            TagArray.Checked(span, TagView.OffsetOf(span, in row.HD61b5e99.ExpressionSteps), InvestmentExpressionRow.Class,
                Unsafe.SizeOf<InvestmentExpressionRow>());
        }
        return (field, rows.Count, rows.Data);
    }
}
