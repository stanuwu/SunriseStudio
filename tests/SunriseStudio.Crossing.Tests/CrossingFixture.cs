using System.Buffers.Binary;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

/**
 * A synthetic open map of three bubbles, A west, B east and C far west, each with a visibility owner. A has a
 * primary and an alternate layer; A and C are joined by an installed link with a zone on each side. Three
 * scenarios name the map's bubbles: all three, A alone, and A with a bubble of another map. Nothing here is game
 * data: every payload comes from Studio's own crossing writer.
 */
internal static class CrossingFixture
{
    internal const uint Base = 0x80A00000, Root = Base, Definition = Base + 1;
    internal const uint Scenario = Base + 26, ScenarioA = Base + 27, ScenarioForeign = Base + 28, Slice = Base + 29, NameForeign = 0xD4D4D4D4;
    internal const uint NameA = 0xA1A1A1A1, NameB = 0xB2B2B2B2, NameC = 0xC3C3C3C3, NameLink = 0x4C414321, NameSecond = 0x53454332;
    internal const ulong KeyA = 0x1111000000000001, KeyB = 0x2222000000000002, KeyC = 0x3333000000000003,
        KeyLink = 0x4C41430000000004, KeySecond = 0x5345430000000005;
    internal static readonly uint[] Names = [NameA, NameB, NameC];
    internal static uint BubbleTag(int slot) => Base + 2 + (uint)slot;
    internal static uint OwnerTag(int slot) => Base + 15 + (uint)slot;
    /** Payload and cells of each layer: A primary, A alternate, B primary, C primary. */
    internal static readonly (int Slot, string Mode, uint Payload, uint Cells)[] Layers =
        [(0, "Primary", Base + 18, Base + 19), (0, "Alternate", Base + 20, Base + 21), (1, "Primary", Base + 22, Base + 23), (2, "Primary", Base + 24, Base + 25)];
    internal static readonly CrossingCompiler.CellBox[] Bounds =
        [new(-40, -20, -10, 8, 20, 30), new(-8, -20, -10, 40, 20, 30), new(-80, -20, -10, -36, 20, 30)];

    internal static void Create(string folder)
    {
        var entries = new AuthoredPackageEntry[30];
        void Entry(int index, uint type, byte[] data, string? name = null, uint info = 0x100A) => entries[index] = new(type, info, data, name);
        var root = new Bytes(0x20); root.U32(8, Definition);
        int rows = root.Array(0x10, 0x80807D53, 16, 3);
        for (int slot = 0; slot < 3; slot++) root.U32(rows + slot * 16, BubbleTag(slot));
        Entry(0, 0x808091DE, root.ToArray(), "map:crossing:root");
        byte[] definition = DefinitionBytes();
        Entry(1, 0x80809962, definition);
        var identity = MapIdentity.Decode(definition);
        for (int slot = 0; slot < 3; slot++)
        {
            var bubble = new Bytes(0x60); bubble.U32(8, Base + 5 + (uint)slot); bubble.U32(0x18, Names[slot]);
            bubble.Put(0x1C, identity.Bubbles[slot].Mask);
            Entry(2 + slot, 0x80807DAE, bubble.ToArray(), $"bubble:crossing:{slot}");
            var list = new Bytes(0x20); int reference = list.Array(8, 0x808084C1, 16, 1); list.U32(reference, Base + 8 + (uint)slot);
            Entry(5 + slot, 0x808091E0, list.ToArray());
            var container = new Bytes(0x40); container.Put(8, identity.Bubbles[slot].Mask);
            int member = container.Array(0x28, 0x80808BB0, 4, 1); container.U32(member, Base + 11 + (uint)slot);
            Entry(8 + slot, 0x80808A54, container.ToArray());
            var placements = new Bytes(0x20); int row = placements.Array(8, 0x808099D8, 144, 1);
            placements.U32(row, Base + 14); placements.F32(row + 0x1C, 1); placements.F32(row + 0x2C, 1);
            placements.U64(row + 0x70, 900 + (ulong)slot); placements.U32(row + 0x64, 0x700 + (uint)slot);
            int record = placements.Allocate(0x30) + 16;
            placements.U32(record - 4, 0x80807246); placements.I64(row + 0x78, record - row - 0x78);
            placements.U32(record + 0x10, OwnerTag(slot));
            Entry(11 + slot, 0x808099D6, placements.ToArray());
            var layers = Layers.Where(layer => layer.Slot == slot).ToArray();
            Entry(15 + slot, 0x80807320, Owner(slot, layers[0].Payload, layers[0].Cells,
                layers.Length > 1 ? layers[1].Payload : uint.MaxValue, layers.Length > 1 ? layers[1].Cells : uint.MaxValue));
        }
        Entry(14, 0x80809C0F, new byte[0x40], "crossing:placed-class");
        foreach (var (slot, mode, payload, cells) in Layers)
        {
            var grid = Grid(Bounds[slot], mode == "Alternate" ? 6 : 8);
            Entry((int)(payload - Base), uint.MaxValue, CrossingCompiler.CompileGrid(grid), info: 24 << 9);
            var self = identity.SelfKey(identity.Bubbles[slot]);
            var boxes = grid.Cells().Select(cell => cell.Box).ToArray();
            byte[] raw = CrossingCompiler.InactiveCells((uint)self.Index, self.Value, [.. boxes.Select(box => box.Centre())]);
            // A's westmost cells and C's eastmost cells cross to each other through the installed link.
            int[] zoned = slot switch
            {
                0 => [.. Enumerable.Range(0, boxes.Length).Where(i => boxes[i].Centre()[0] < -30)],
                2 => [.. Enumerable.Range(0, boxes.Length).Where(i => boxes[i].Centre()[0] > -40)],
                _ => []
            };
            if (zoned.Length > 0)
                raw = CrossingCompiler.WriteConnections(raw, identity, [new(KeyLink, slot == 0 ? KeyC : KeyA)],
                    [.. zoned.Select(cell => new CrossingCompiler.CellEdit(cell, 0, CrossingCompiler.CellMode.Immediate))], self.Value);
            Entry((int)(cells - Base), VisibilityCells.ResourceClass, raw);
        }
        Entry(26, 0x80809994, ScenarioBytes([(NameA, 2), (NameB, 1), (NameC, 1)], identity), "crossing:scenario");
        Entry(27, 0x80809994, ScenarioBytes([(NameA, 1)], identity), "crossing:scenario-a");
        Entry(28, 0x80809994, ScenarioBytes([(NameA, 1), (NameForeign, 1)], identity), "crossing:scenario-foreign");
        Entry(29, 0x8080925B, new Bytes(0x30).ToArray(), "crossing:slice");
        Directory.CreateDirectory(Path.Combine(folder, "packages"));
        PackageWriter.WriteNew(Path.Combine(folder, "packages", "w64_crossing_0100_0.pkg"), 0x100, entries);
    }

    /** Planes every step units from the low bound. */
    internal static CrossingCompiler.CellGrid Grid(CrossingCompiler.CellBox bounds, double step)
    {
        var planes = new IReadOnlyList<double>[3];
        for (int a = 0; a < 3; a++)
        {
            var list = new List<double>();
            for (double value = bounds[a] + step; value < bounds[a + 3]; value += step) list.Add(value);
            planes[a] = list;
        }
        return new(bounds, planes);
    }

    /** Keys A, B, C, the A-C link and a second-list key A and B share; the native layout of every installed definition. */
    internal static byte[] DefinitionBytes()
    {
        static byte[] Key(ulong value, uint name, ushort flags, short index, uint slot)
        {
            var row = new byte[24];
            BinaryPrimitives.WriteUInt64LittleEndian(row, value); BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(8), name);
            BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(12), flags); BinaryPrimitives.WriteInt16LittleEndian(row.AsSpan(14), index);
            BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(16), slot);
            return row;
        }
        byte[][] keys = [Key(KeyA, NameA, 0, 0, 0), Key(KeyB, NameB, 0, 1, 1), Key(KeyC, NameC, 0, 2, 2),
            Key(KeyLink, NameLink, 1, 3, uint.MaxValue), Key(KeySecond, NameSecond, 2, 4, uint.MaxValue)];
        (ushort[], ushort[])[] lists = [([3], [4]), ([], [4]), ([3], [])];
        var bubbles = new byte[3][];
        for (int slot = 0; slot < 3; slot++)
        {
            bubbles[slot] = new byte[80];
            BinaryPrimitives.WriteUInt32LittleEndian(bubbles[slot], Names[slot]);
            BinaryPrimitives.WriteUInt32LittleEndian(bubbles[slot].AsSpan(4), (uint)slot);
            foreach (int index in lists[slot].Item1.Concat(lists[slot].Item2).Select(i => (int)i).Append(slot))
                bubbles[slot][40 + index / 8] |= (byte)(1 << index % 8);
            BinaryPrimitives.WriteUInt32LittleEndian(bubbles[slot].AsSpan(72), (uint)slot);
        }
        return CrossingCompiler.SerializeDefinition(keys, bubbles, lists, "crossing_fixture"u8.ToArray());
    }

    /**
     * Scenario 0x80809994: bubbles at +0x50 (0x8080924D, 24 bytes), each with states at +8 (0x8080924F, 76 bytes) that
     * repeat the bubble's global index at +28 and mask at +32. A bubble of another map borrows A's identity.
     */
    private static byte[] ScenarioBytes(IReadOnlyList<(uint Name, int States)> bubbles, MapIdentity identity)
    {
        var scenario = new Bytes(0x60);
        int rows = scenario.Array(0x50, 0x8080924D, 24, bubbles.Count);
        for (int i = 0; i < bubbles.Count; i++)
        {
            var (name, count) = bubbles[i];
            var owner = identity.Bubble(name) ?? identity.Bubbles[0];
            scenario.U32(rows + 24 * i, name);
            int states = scenario.Array(rows + 24 * i + 8, 0x8080924F, 76, count);
            for (int s = 0; s < count; s++)
            {
                int at = states + 76 * s;
                scenario.U32(at, 1); scenario.U32(at + 4, name ^ (uint)(0x100 + s)); scenario.U32(at + 12, 1);
                scenario.U32(at + 28, owner.SelfIndex); scenario.Put(at + 32, owner.Mask);
                scenario.U32(at + 64, name); scenario.U32(at + 68, Slice);
            }
        }
        return scenario.ToArray();
    }

    /** The shipped empty owner layout: cull rows at +0x20, root slot +0x50, payloads +8/+0xC, cells +0xA4/+0xA8. */
    private static byte[] Owner(int slot, uint payload, uint cells, uint alternatePayload, uint alternateCells)
    {
        var owner = new byte[256];
        void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(owner.AsSpan(at), value);
        BinaryPrimitives.WriteUInt64LittleEndian(owner, 256);
        U32(0x08, payload); U32(0x0C, alternatePayload); U32(0x10, uint.MaxValue); U32(0x18, 0xDEADBEEF);
        U32(0x20, 1); U32(0x28, 0xB8); U32(0x50, (uint)slot); U32(0x6C, 0x3F800000); U32(0xA0, uint.MaxValue);
        U32(0xA4, cells); U32(0xA8, alternateCells); U32(0xC4, uint.MaxValue); U32(0xDC, 0x80809FBD); U32(0xE0, 1); U32(0xE8, 0x8080733E);
        return owner;
    }

    private sealed class Bytes(int initial)
    {
        private readonly List<byte> bytes = new(new byte[initial]);
        internal byte[] ToArray()
        {
            byte[] result = [.. bytes];
            BinaryPrimitives.WriteUInt64LittleEndian(result, (ulong)result.Length);
            return result;
        }
        internal int Allocate(int count)
        {
            while (bytes.Count % 16 != 0) bytes.Add(0);
            int start = bytes.Count; bytes.AddRange(new byte[count]); return start;
        }
        internal int Array(int field, uint type, int stride, int count)
        {
            int header = Allocate(32 + stride * count); U32(header + 12, 0x80809FBD);
            I64(header + 16, count); U32(header + 24, type);
            I64(field, count); I64(field + 8, header + 16 - field - 8); return header + 32;
        }
        internal void F32(int p, float x) => U32(p, BitConverter.SingleToUInt32Bits(x));
        internal void U32(int p, uint x) => Put(p, BitConverter.GetBytes(x));
        internal void U64(int p, ulong x) => Put(p, BitConverter.GetBytes(x));
        internal void I64(int p, long x) => Put(p, BitConverter.GetBytes(x));
        internal void Put(int p, byte[] value) { for (int i = 0; i < value.Length; i++) bytes[p + i] = value[i]; }
    }
}
