using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

public sealed partial class InvestmentCatalog
{
    /** The editor bounds array work without changing the native index spaces. */
    private const int MaximumAuthoredRows = 4096;

    public byte[] AddStat(uint tag, ReadOnlyMemory<byte> current, byte index, int value)
    {
        if (!statNames.ContainsKey(index)) throw new InvalidDataException("Choose a stat from this installation.");
        byte[] row = new byte[Unsafe.SizeOf<SItemStatRow>()];
        ref var stat = ref TagView.Edit<SItemStatRow>(row).ItemStat;
        stat.StatIndex = index; stat.Value = value;
        return RewriteStatRows(tag, current, false, row, null);
    }

    public byte[] RemoveStat(uint tag, ReadOnlyMemory<byte> current, InvestmentStat stat)
    {
        if (!ReadItem(tag, current).Stats.Contains(stat)) throw new InvalidDataException("This stat changed. Reopen its editor.");
        return RewriteStatRows(tag, current, false, null, stat.Field.Offset - TagView.FieldOffset<SItemStatRow>("ItemStat.Value"));
    }

    public byte[] AddPerk(uint tag, ReadOnlyMemory<byte> current, ushort index)
    {
        if (index >= PerkCount) throw new InvalidDataException("Choose a sandbox perk from this installation.");
        byte[] row = new byte[Unsafe.SizeOf<ItemSandboxPerk>()];
        TagView.Edit<ItemSandboxPerk>(row).SandboxPerkIndex = index;
        return RewriteStatRows(tag, current, true, row, null);
    }

    public byte[] RemovePerk(uint tag, ReadOnlyMemory<byte> current, InvestmentPerk perk)
    {
        if (!ReadItem(tag, current).Perks.Contains(perk)) throw new InvalidDataException("This perk changed. Reopen its editor.");
        return RewriteStatRows(tag, current, true, null, perk.Field.Offset);
    }

    /** Appended blocks keep unrelated data and shared expressions at their original addresses. */
    private byte[] RewriteStatRows(uint tag, ReadOnlyMemory<byte> current, bool perks, byte[]? added, int? removed)
    {
        if (FindItem(tag) is not { } item || item.Tag != tag)
            throw new InvalidDataException("Open an item definition to change its rows.");
        RequireClass(catalog, tag, ItemClass);
        var data = current.Span;
        if (!TagView.FitsThrough<ItemDefinition>(data, 0, "InventoryItemBlock.Instanced")
            || TagView.At<ulong>(data, TagView.FieldOffset<ItemDefinition>("TagSize")) != (ulong)current.Length)
            throw new InvalidDataException("Item size header is invalid.");
        int pointer = TagView.FieldOffset<ItemDefinition>("StatsItemBlock");
        int block = TagPointer.Checked(data, pointer);
        int stride = perks ? Unsafe.SizeOf<ItemSandboxPerk>() : Unsafe.SizeOf<SItemStatRow>();
        uint rowClass = perks ? ItemSandboxPerk.Class : SItemStatRow.Class;
        (int Count, int Data, uint Class) rows = (0, 0, rowClass);
        if (block >= 0)
        {
            if (block < TagView.ClassWordSize || !TagView.Fits<SItemSandboxPerkBlock>(data, block) || TagView.ClassAt(data, block) != SItemSandboxPerkBlock.Class)
                throw new InvalidDataException("Item stats block has an unsupported layout.");
            ref readonly var lists = ref TagView.At<SItemSandboxPerkBlock>(data, block);
            if (perks) TagArray.Checked(data, TagView.OffsetOf(data, in lists.ItemStats), SItemStatRow.Class, Unsafe.SizeOf<SItemStatRow>());
            else TagArray.Checked(data, TagView.OffsetOf(data, in lists.ItemPerks), ItemSandboxPerk.Class, Unsafe.SizeOf<ItemSandboxPerk>());
            rows = perks ? TagArray.Checked(data, TagView.OffsetOf(data, in lists.ItemPerks), rowClass, stride)
                : TagArray.Checked(data, TagView.OffsetOf(data, in lists.ItemStats), rowClass, stride);
        }
        if (removed is { } old && (old < rows.Data || (old - rows.Data) % stride != 0 || (old - rows.Data) / stride >= rows.Count))
            throw new InvalidDataException("The removed row is outside its array.");
        int count = rows.Count + (added is null ? 0 : 1) - (removed is null ? 0 : 1);
        if (count < 0 || count > MaximumAuthoredRows)
            throw new InvalidDataException($"The editor supports up to {MaximumAuthoredRows} rows per stat or perk array.");
        // The block, a pad word, the array marker, the array header, then the rows.
        int blockBytes = checked(Unsafe.SizeOf<SItemSandboxPerkBlock>() + sizeof(uint) + TagView.ClassWordSize);
        byte[] output = current.ToArray();
        int nextBlock = TagRelocator.AppendObject(ref output, SItemSandboxPerkBlock.Class,
            checked(blockBytes + TagArray.HeaderSize + count * stride), 8);
        int header = checked(nextBlock + blockBytes), start = checked(header + TagArray.HeaderSize);
        if (block >= 0)
        {
            data.Slice(block, Unsafe.SizeOf<SItemSandboxPerkBlock>()).CopyTo(output.AsSpan(nextBlock));
            ref readonly var lists = ref TagView.At<SItemSandboxPerkBlock>(data, block);
            Rebase(data, TagView.OffsetOf(data, in lists.ItemStats.Relative), nextBlock - block);
            Rebase(data, TagView.OffsetOf(data, in lists.ItemPerks.Relative), nextBlock - block);
        }
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(pointer), nextBlock - pointer);
        ref readonly var next = ref TagView.At<SItemSandboxPerkBlock>(output, nextBlock);
        int field = perks ? TagView.OffsetOf(output, in next.ItemPerks) : TagView.OffsetOf(output, in next.ItemStats);
        if (count == 0) output.AsSpan(field, TagArray.FieldSize).Clear();
        else TagArray.WriteField(output, field, count, header);
        if (count > 0)
        {
            TagArray.WriteHeader(output, header, count, rowClass);
            int target = start;
            for (int i = 0; i < rows.Count; i++)
            {
                int source = rows.Data + i * stride;
                if (source == removed) continue;
                data.Slice(source, stride).CopyTo(output.AsSpan(target));
                if (perks) Expression(data, in TagView.At<ItemSandboxPerk>(data, source).HEb6220de.ExpressionSteps, target - source);
                else
                {
                    ref readonly var row = ref TagView.At<SItemStatRow>(data, source);
                    Expression(data, in row.ItemStat.HA46186f1.ExpressionSteps, target - source);
                    Expression(data, in row.HEb6220de.ExpressionSteps, target - source);
                }
                target += stride;
            }
            if (added is not null) added.CopyTo(output, target);
        }
        TagView.Edit<ulong>(output, TagView.FieldOffset<ItemDefinition>("TagSize")) = (ulong)output.Length;
        return output;

        void Expression(ReadOnlySpan<byte> source, ref readonly TagArray<InvestmentExpressionRow> steps, int delta)
        {
            TagArray.Checked(source, TagView.OffsetOf(source, in steps), InvestmentExpressionRow.Class, Unsafe.SizeOf<InvestmentExpressionRow>());
            Rebase(source, TagView.OffsetOf(source, in steps.Relative), delta);
        }
        // The pointer at `at` lands `delta` bytes later in the output and keeps its target.
        void Rebase(ReadOnlySpan<byte> source, int at, int delta)
        {
            int address = TagPointer.Checked(source, at);
            BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(at + delta), address < 0 ? 0 : address - (at + delta));
        }
    }
}
