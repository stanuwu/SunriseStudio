using System.Buffers.Binary;

namespace SunriseStudio.Formats;

/** Add/remove edits for item stat and perk arrays. */
public sealed partial class InvestmentCatalog
{
    private const uint StatsBlockClass = 0x808077B9, StatRowClass = 0x80803033, PerkRowClass = 0x808077BC;
    /** The editor bounds array work without changing the native index spaces. */
    private const int MaximumAuthoredRows = 4096;

    /** Returns the item tag with a new stat row (stat index and value) added; throws if the stat is not in this installation. */
    public byte[] AddStat(uint tag, ReadOnlyMemory<byte> current, byte index, int value)
    {
        if (!statNames.ContainsKey(index)) throw new InvalidDataException("Choose a stat from this installation.");
        byte[] row = new byte[40]; row[0] = index;
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(4), value);
        return RewriteStatRows(tag, current, false, row, null);
    }

    /** Returns the item tag with the stat row removed; throws if the item changed since it was read. */
    public byte[] RemoveStat(uint tag, ReadOnlyMemory<byte> current, InvestmentStat stat)
    {
        if (!ReadItem(tag, current).Stats.Contains(stat)) throw new InvalidDataException("This stat changed. Reopen its editor.");
        return RewriteStatRows(tag, current, false, null, stat.Field.Offset - 4);
    }

    /** Returns the item tag with a sandbox perk row added; throws if the perk index is not in this installation. */
    public byte[] AddPerk(uint tag, ReadOnlyMemory<byte> current, ushort index)
    {
        if (index >= PerkCount) throw new InvalidDataException("Choose a sandbox perk from this installation.");
        byte[] row = new byte[24]; BinaryPrimitives.WriteUInt16LittleEndian(row, index);
        return RewriteStatRows(tag, current, true, row, null);
    }

    /** Returns the item tag with the perk row removed; throws if the item changed since it was read. */
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
        var data = new TagReader(current);
        if (!data.Has(0, 188) || data.U64(0) != (ulong)current.Length)
            throw new InvalidDataException("Item size header is invalid.");
        int block = data.Relative(112);
        int stride = perks ? 24 : 40, field = perks ? 16 : 0;
        uint rowClass = perks ? PerkRowClass : StatRowClass;
        (int Count, int Data, uint Class) rows = (0, 0, rowClass);
        if (block >= 0)
        {
            if (block < 4 || !data.Has(block, 32) || data.U32(block - 4) != StatsBlockClass)
                throw new InvalidDataException("Item stats block has an unsupported layout.");
            data.Array(block + (perks ? 0 : 16), perks ? StatRowClass : PerkRowClass, perks ? 40 : 24);
            rows = data.Array(block + field, rowClass, stride);
        }
        if (removed is { } old && (old < rows.Data || (old - rows.Data) % stride != 0 || (old - rows.Data) / stride >= rows.Count))
            throw new InvalidDataException("The removed row is outside its array.");
        int count = rows.Count + (added is null ? 0 : 1) - (removed is null ? 0 : 1);
        if (count < 0 || count > MaximumAuthoredRows)
            throw new InvalidDataException($"The editor supports up to {MaximumAuthoredRows} rows per stat or perk array.");
        int nextBlock = checked((current.Length + 11) & ~7);
        int header = checked(nextBlock + 40), start = checked(header + 16);
        byte[] output = new byte[checked(start + count * stride)]; current.Span.CopyTo(output);
        Write32(nextBlock - 4, StatsBlockClass);
        if (block >= 0)
        {
            current.Span.Slice(block, 32).CopyTo(output.AsSpan(nextBlock));
            Rebase(block + 8, nextBlock + 8);
            Rebase(block + 24, nextBlock + 24);
        }
        Write64(112, nextBlock - 112);
        Write64(nextBlock + field, count);
        Write64(nextBlock + field + 8, count == 0 ? 0 : header - nextBlock - field - 8);
        if (count > 0)
        {
            TagArrayRelocation.WriteHeader(output, header, count, rowClass);
            int target = start;
            for (int i = 0; i < rows.Count; i++)
            {
                int source = rows.Data + i * stride;
                if (source == removed) continue;
                current.Span.Slice(source, stride).CopyTo(output.AsSpan(target));
                Expression(source + 8, target + 8);
                if (!perks) Expression(source + 24, target + 24);
                target += stride;
            }
            if (added is not null) added.CopyTo(output, target);
        }
        Write64(0, output.Length);
        return output;

        void Expression(int source, int target)
        {
            data.Array(source, 0x80807D31, 8);
            Rebase(source + 8, target + 8);
        }
        void Rebase(int source, int target) => TagArrayRelocation.Rebase(data, output, source, target);
        void Write64(int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(at), value);
        void Write32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at), value);
    }
}
