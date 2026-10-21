using System.Buffers.Binary;
using SunriseStudio.Scene;

namespace SunriseStudio.Authoring;

public static partial class CrossingCompiler
{
    /** Every one of 314,803 installed inactive cells stores ramp -1. */
    internal const float InertRamp = -1;

    /** A shared flag-1 link toward a named flag-0 destination; speed selector 0 or 1. */
    internal sealed record Connection(ulong LinkKey, ulong DestinationKey, sbyte Speed = 0);

    internal enum CellMode { Inactive, Immediate, Timed }

    /** Immediate writes state 2 and ramp 0; Timed writes state 3, 4 or 5 and a ramp target. */
    internal sealed record CellEdit(int Cell, int Connection, CellMode Mode, float Ramp = 0, sbyte? State = null);

    /** The unbaked 80809756 profile: header +8 key index, +16 self key, +0x78..+0x87 0xFF, cells at +0xA0, all inactive. */
    internal static byte[] InactiveCells(uint selfIndex, ulong selfKey, IReadOnlyList<float[]> points)
    {
        if (points.Count is < 1 or > 65535) throw new InvalidDataException("A crossing needs 1 to 65,535 cells.");
        var raw = new byte[160 + points.Count * VisibilityCells.CellSize];
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(8), selfIndex);
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(16), selfKey);
        raw.AsSpan(0x78, 16).Fill(0xFF);
        // Four bytes of padding and the typed header put the cells at +0xA0, as the shipped single-cell profile has them.
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(24), (ulong)points.Count);
        BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(32), 160 - 24 - 24);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(140), NativeRows.ArrayMarker);
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(144), (ulong)points.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(152), VisibilityCells.CellClass);
        for (int i = 0; i < points.Count; i++)
        {
            int at = 160 + i * VisibilityCells.CellSize;
            for (int a = 0; a < 3; a++) BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(at + a * 4), Number(points[i][a]));
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(at + 12), InertRamp);
            raw[at + 16] = 0; raw[at + 17] = 0xFF; raw[at + 18] = 0xFF; raw[at + 19] = 0xFF;
        }
        BinaryPrimitives.WriteUInt64LittleEndian(raw, (ulong)raw.Length);
        var decoded = VisibilityCells.Decode(raw);
        if (decoded.SelfKey != selfKey) throw new InvalidDataException("Fresh crossing cells lost their self key.");
        return raw;
    }

    /** {link index, link, destination index, destination, speed, seven zero bytes}, the shape of all 1,346 installed zones. */
    private static byte[] ZoneRow(MapIdentity identity, Connection connection)
    {
        var link = identity.Key(connection.LinkKey);
        var destination = identity.Key(connection.DestinationKey);
        if (link is not { Flags: 1 } || destination is not { Flags: 0 })
            throw new InvalidDataException("A crossing zone needs a shared link key and a named destination key.");
        var row = new byte[VisibilityCells.ZoneSize];
        BinaryPrimitives.WriteUInt64LittleEndian(row, (ulong)link.Index);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(8), link.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(16), (ulong)destination.Index);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(24), destination.Value);
        row[32] = unchecked((byte)connection.Speed);
        return row;
    }

    /** Cells decode, name a bubble self key of this definition, and keep every zone key's index beside it. */
    private static VisibilityCells DecodeCells(byte[] raw, MapIdentity identity, ulong expectedSelf)
    {
        var cells = VisibilityCells.Decode(raw);
        if (cells.SelfKey != expectedSelf) throw new InvalidDataException("Visibility cells belong to a different bubble key.");
        if (identity.Key(cells.SelfKey) is not { Flags: 0 } own || (uint)own.Index != cells.SelfIndex)
            throw new InvalidDataException("A visibility self key index differs from its map definition.");
        foreach (var zone in cells.Zones)
            if (identity.Key(zone.LinkKey) is not { } link || (ulong)link.Index != zone.LinkIndex
                || identity.Key(zone.DestinationKey) is not { } destination || (ulong)destination.Index != zone.DestinationIndex)
                throw new InvalidDataException("A visibility zone key is absent from its map definition or has another index.");
        return cells;
    }

    /**
     * Assigns cells to zones; identical zone rows are reused, so applying the same connection twice is stable.
     * Every connection needs its link in both endpoint first lists. Cell count, order and unread fields stay.
     */
    internal static byte[] WriteConnections(byte[] raw, MapIdentity identity, IReadOnlyList<Connection> connections,
        IReadOnlyList<CellEdit> edits, ulong expectedSelf)
    {
        var before = DecodeCells(raw, identity, expectedSelf);
        if (connections.Count > 128 || edits.Count == 0) throw new InvalidDataException("A crossing needs cell edits and at most 128 connections.");
        var own = identity.Bubbles[(int)identity.Key(before.SelfKey)!.RootSlot];
        var rows = before.ZoneOffsets.Select(at => raw.AsSpan(at, VisibilityCells.ZoneSize).ToArray()).ToList();
        var zoneIndices = new List<int>();
        foreach (var connection in connections)
        {
            byte[] row = ZoneRow(identity, connection);
            var link = identity.Key(connection.LinkKey)!;
            var destination = identity.Key(connection.DestinationKey)!;
            if (destination.Value == before.SelfKey) throw new InvalidDataException("A crossing cannot lead to its own bubble.");
            var target = identity.Bubbles[(int)destination.RootSlot];
            if (!own.FirstLinks.Contains((ushort)link.Index) || !target.FirstLinks.Contains((ushort)link.Index))
                throw new InvalidDataException("A crossing link is not listed by both of its bubbles.");
            int index = rows.FindIndex(existing => existing.AsSpan().SequenceEqual(row));
            if (index < 0)
            {
                if (rows.Count >= VisibilityCells.MaximumZones) throw new InvalidDataException("A cell resource holds at most 128 zones.");
                rows.Add(row); index = rows.Count - 1;
            }
            zoneIndices.Add(index);
        }
        byte[] output = [.. raw];
        var selected = new HashSet<int>();
        foreach (var edit in edits)
        {
            if ((uint)edit.Cell >= (uint)before.Cells.Count) throw new InvalidDataException("A crossing cell is outside its payload.");
            if (!selected.Add(edit.Cell)) throw new InvalidDataException("A crossing cell is edited twice.");
            float target = Number(edit.Ramp);
            if (!(target >= 0)) throw new InvalidDataException("A crossing ramp target must be nonnegative.");
            if (edit.State is { } chosen && (edit.Mode != CellMode.Timed || chosen is < 3 or > 5))
                throw new InvalidDataException("Only a timed cell may choose state 3, 4 or 5.");
            int at = before.CellOffsets[edit.Cell];
            if (edit.Mode == CellMode.Inactive)
            {
                if (target != 0) throw new InvalidDataException("An inactive cell cannot carry a ramp target.");
                Inert(output, at);
                continue;
            }
            if ((uint)edit.Connection >= (uint)connections.Count) throw new InvalidDataException("A crossing cell names an absent connection.");
            if (edit.Mode == CellMode.Immediate && target != 0) throw new InvalidDataException("An immediate cell does not use a ramp target.");
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(at + 12), target);
            output[at + 16] = (byte)(edit.Mode == CellMode.Immediate ? 2 : edit.State ?? 3);
            output[at + 17] = (byte)zoneIndices[edit.Connection];
        }
        if (rows.Count > before.Zones.Count) output = PlaceZones(output, rows);
        BinaryPrimitives.WriteUInt64LittleEndian(output, (ulong)output.Length);
        var after = DecodeCells(output, identity, expectedSelf);
        if (!after.CellOffsets.SequenceEqual(before.CellOffsets)) throw new InvalidDataException("Crossing authoring moved the cells.");
        return output;
    }

    /** Removes the listed zones; their cells become inactive with the inert ramp. Other zones keep their bytes, merged when equal. */
    internal static byte[] RemoveZones(byte[] raw, IReadOnlySet<int> removed, ulong expectedSelf)
    {
        var before = VisibilityCells.Decode(raw);
        if (before.SelfKey != expectedSelf) throw new InvalidDataException("Visibility cells belong to a different bubble key.");
        var rows = new List<byte[]>();
        var remap = new int[before.Zones.Count];
        for (int index = 0; index < before.Zones.Count; index++)
        {
            if (removed.Contains(index)) { remap[index] = -1; continue; }
            byte[] row = raw.AsSpan(before.ZoneOffsets[index], VisibilityCells.ZoneSize).ToArray();
            int found = rows.FindIndex(existing => existing.AsSpan().SequenceEqual(row));
            if (found < 0) { rows.Add(row); found = rows.Count - 1; }
            remap[index] = found;
        }
        byte[] output = [.. raw];
        foreach (int at in before.CellOffsets)
        {
            sbyte zone = unchecked((sbyte)raw[at + 17]);
            if (zone == -1) continue;
            if (remap[zone] == -1) Inert(output, at);
            else output[at + 17] = (byte)remap[zone];
        }
        output = PlaceZones(output, rows);
        var after = VisibilityCells.Decode(output);
        if (!after.CellOffsets.SequenceEqual(before.CellOffsets)) throw new InvalidDataException("Removing zones moved the cells.");
        return output;
    }

    private static void Inert(byte[] output, int at)
    {
        BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(at + 12), InertRamp);
        output[at + 16] = 0; output[at + 17] = 0xFF;
    }

    /**
     * The zone array replaced in the installed layout of all 784 cell resources: cells at align16(136 + 20), zones at
     * align16(cells end + 20), and the optional arrays at +56/+72 moved as one block by a multiple of 16.
     */
    internal static byte[] PlaceZones(byte[] raw, IReadOnlyList<byte[]> rows)
    {
        static int Align(int end) => (end + 35) & ~15;
        var arrays = new SortedDictionary<int, int>();
        foreach (int descriptor in new[] { 24, 40, 56, 72, 88, 104 })
        {
            long count = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(descriptor));
            long start = descriptor + 24 + BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(descriptor + 8));
            if (count != 0 && start >= 20 && start <= raw.Length && BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan((int)start - 20)) == NativeRows.ArrayMarker
                && BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan((int)start - 16)) == count)
                arrays[descriptor] = (int)start;
        }
        if (!arrays.ContainsKey(24) || !arrays.Values.SequenceEqual(arrays.Values.Order()))
            throw new InvalidDataException("The cell resource is not in the installed layout.");
        int end = VisibilityCells.HeaderSize;
        bool optional = false;
        foreach (var (descriptor, start) in arrays)
        {
            if (start != Align(end)) throw new InvalidDataException("The cell resource is not in the installed layout.");
            if (descriptor > 40) { optional = true; break; }
            end = start + (int)BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(descriptor)) * (descriptor == 24 ? VisibilityCells.CellSize : VisibilityCells.ZoneSize);
        }
        if (!optional && raw.Length != end) throw new InvalidDataException("The cell resource is not in the installed layout.");
        int cellsEnd = arrays[24] + (int)BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(24)) * VisibilityCells.CellSize;
        int[] later = [.. arrays.Keys.Where(descriptor => descriptor > 40)];
        byte[] tail = later.Length > 0 ? raw[(arrays[later[0]] - 20)..] : [];
        byte[] output = raw[..cellsEnd];
        if (rows.Count > 0)
            output = NativeRows.Append(output, 40, VisibilityCells.ZoneClass, VisibilityCells.ZoneSize, [.. rows.SelectMany(row => row)], out _);
        else
        {
            BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(40), 0);
            BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(48), 0);
        }
        if (later.Length > 0)
        {
            int start = Align(output.Length), delta = start - arrays[later[0]];
            if (delta % 16 != 0) throw new InvalidDataException("Optional visibility arrays must move by a multiple of 16.");
            var moved = new byte[start - 20 + tail.Length];
            output.CopyTo(moved, 0); tail.CopyTo(moved, start - 20);
            foreach (int descriptor in later)
                BinaryPrimitives.WriteInt64LittleEndian(moved.AsSpan(descriptor + 8),
                    BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(descriptor + 8)) + delta);
            output = moved;
        }
        BinaryPrimitives.WriteUInt64LittleEndian(output, (ulong)output.Length);
        return output;
    }
}
