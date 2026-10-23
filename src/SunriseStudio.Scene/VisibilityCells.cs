using System.Buffers.Binary;
using System.Numerics;

namespace SunriseStudio.Scene;

/** One 20-byte class-0x80809778 cell: representative point, ramp at +12, state at +16, zone at +17, tail word at +18. */
public readonly record struct VisibilityCell(Vector3 Point, float Ramp, sbyte State, sbyte Zone, ushort Tail);

/** One 40-byte class-0x80809777 zone: {index, key} of the shared link, {index, key} of the destination, speed at +32. */
public sealed record VisibilityZone(ulong LinkIndex, ulong LinkKey, ulong DestinationIndex, ulong DestinationKey, sbyte Speed);

/**
 * A class-0x80809756 cell resource. Header +8 is the self key's map index, +16 the self key; the typed cells
 * are at +24 and the zones at +40. Zone index -1 goes with state 0 and no other state.
 */
public sealed record VisibilityCells(uint SelfIndex, ulong SelfKey, IReadOnlyList<VisibilityCell> Cells,
    IReadOnlyList<VisibilityZone> Zones, IReadOnlyList<int> CellOffsets, IReadOnlyList<int> ZoneOffsets)
{
    public const uint ResourceClass = 0x80809756, CellClass = 0x80809778, ZoneClass = 0x80809777;
    public const int HeaderSize = 136, CellSize = 20, ZoneSize = 40, MaximumZones = 128;

    /** Refuses a cell resource whose arrays overlap, leave it, or pair a state with the wrong zone. */
    public static VisibilityCells Decode(byte[] bytes)
    {
        if (bytes.Length < HeaderSize || BinaryPrimitives.ReadUInt64LittleEndian(bytes) != (ulong)bytes.Length)
            throw new InvalidDataException("A visibility cell resource has the wrong size header.");
        var data = new TagData(bytes);
        int[] cells = data.Rows(24, CellClass, CellSize), zones = data.Rows(40, ZoneClass, ZoneSize);
        if (zones.Length > MaximumZones) throw new InvalidDataException("A cell resource holds more than 128 zones.");
        var spans = new List<(int Start, int End)>();
        if (cells.Length > 0) spans.Add((cells[0] - 20, cells[^1] + CellSize));
        if (zones.Length > 0) spans.Add((zones[0] - 20, zones[^1] + ZoneSize));
        spans.Sort();
        if (spans.Any(span => span.Start < HeaderSize) || spans.Count == 2 && spans[0].End > spans[1].Start)
            throw new InvalidDataException("Visibility cell arrays overlap.");
        var rows = cells.Select(at => new VisibilityCell(data.V3(at), data.F32(at + 12), unchecked((sbyte)data.U8(at + 16)),
            unchecked((sbyte)data.U8(at + 17)), data.U16(at + 18))).ToArray();
        foreach (var cell in rows)
            if (cell.Zone == -1 != (cell.State == 0) || cell.Zone < -1 || cell.Zone >= zones.Length)
                throw new InvalidDataException("A visibility cell's state and zone disagree.");
        var zoneRows = zones.Select(at => new VisibilityZone(data.U64(at), data.U64(at + 8), data.U64(at + 16),
            data.U64(at + 24), unchecked((sbyte)data.U8(at + 32)))).ToArray();
        return new(data.U32(8), data.U64(16), rows, zoneRows, cells, zones);
    }
}

/**
 * Class 0x80807320 visibility owner: root slot at +0x50, primary query payload at +8 with its cells at +0xA4,
 * alternate payload at +0xC with its cells at +0xA8. 0xFFFFFFFF marks an absent layer.
 */
public sealed record VisibilityOwner(uint RootSlot, uint PrimaryPayload, uint AlternatePayload, uint PrimaryCells, uint AlternateCells)
{
    public const uint ResourceClass = 0x80807320;
    public const int MinimumSize = 208;

    /** The layers with both a payload and cells, primary first. */
    public IEnumerable<(string Mode, uint Payload, uint Cells)> Layers()
    {
        if (PrimaryPayload != uint.MaxValue && PrimaryCells != uint.MaxValue) yield return ("Primary", PrimaryPayload, PrimaryCells);
        if (AlternatePayload != uint.MaxValue && AlternateCells != uint.MaxValue) yield return ("Alternate", AlternatePayload, AlternateCells);
    }

    public static VisibilityOwner Decode(byte[] bytes)
    {
        if (bytes.Length < MinimumSize || BinaryPrimitives.ReadUInt64LittleEndian(bytes) != (ulong)bytes.Length)
            throw new InvalidDataException("A visibility owner has the wrong size header.");
        var data = new TagData(bytes);
        uint slot = data.U32(0x50);
        if (slot >= 64) throw new InvalidDataException("A visibility owner's root slot is outside 0..63.");
        return new(slot, data.U32(8), data.U32(0xC), data.U32(0xA4), data.U32(0xA8));
    }
}
