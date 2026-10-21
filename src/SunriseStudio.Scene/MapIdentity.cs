using System.Buffers.Binary;

namespace SunriseStudio.Scene;

/**
 * One 24-byte class-0x808098C4 key: value, name hash at +8, flags at +12 (0 bubble, 1 shared link, 2 second-list
 * link), index at +14, root slot at +16 (0xFFFFFFFF for a link) and a tail word at +20.
 */
public sealed record MapKey(ulong Value, uint NameHash, ushort Flags, short Index, uint RootSlot, uint Tail);

/**
 * One 80-byte class-0x808098C2 bubble: name hash, self key index at +4, first-list indices at +8, second-list
 * indices at +24, the 256-bit mask at +40 and the root slot at +72. The mask holds the self key and both lists.
 */
public sealed record MapBubble(uint NameHash, uint SelfIndex, IReadOnlyList<ushort> FirstLinks,
    IReadOnlyList<ushort> SecondLinks, byte[] Mask, uint RootSlot);

/** A class-0x80809962 map definition: keys at +16, bubbles at +32, name at +8 + the relative word there. */
public sealed record MapIdentity(IReadOnlyList<MapKey> Keys, IReadOnlyList<MapBubble> Bubbles,
    IReadOnlyList<int> KeyOffsets, IReadOnlyList<int> BubbleOffsets, int NameOffset, byte[] Name)
{
    public const uint ResourceClass = 0x80809962, KeyClass = 0x808098C4, BubbleClass = 0x808098C2, IndexClass = 0x80809B9C;
    public const int MaximumKeys = 256, MaximumBubbles = 64;

    public MapKey? Key(ulong value) => Keys.FirstOrDefault(key => key.Value == value);
    public MapBubble? Bubble(uint nameHash) => Bubbles.FirstOrDefault(bubble => bubble.NameHash == nameHash);
    /** The self key of a named bubble. */
    public MapKey SelfKey(MapBubble bubble) => Keys[(int)bubble.SelfIndex];

    /** Flag-1 keys both bubbles list first; installed pairs share exactly one. */
    public IReadOnlyList<MapKey> SharedLinks(MapBubble first, MapBubble second) =>
        first.FirstLinks.Intersect(second.FirstLinks).Order().Select(index => Keys[index]).ToArray();

    /** Refuses duplicate identities, a mask that is not the self key and both lists, and overlapping records. */
    public static MapIdentity Decode(byte[] bytes)
    {
        if (bytes.Length < 48 || BinaryPrimitives.ReadUInt64LittleEndian(bytes) != (ulong)bytes.Length)
            throw new InvalidDataException("A map definition has the wrong size header.");
        var data = new TagData(bytes);
        int[] keyRows = data.Rows(16, KeyClass, 24), bubbleRows = data.Rows(32, BubbleClass, 80);
        if (keyRows.Length > MaximumKeys || bubbleRows.Length > MaximumBubbles)
            throw new InvalidDataException("A map definition exceeds 256 keys or 64 bubbles.");
        var keys = keyRows.Select(at => new MapKey(data.U64(at), data.U32(at + 8), data.U16(at + 12), data.I16(at + 14),
            data.U32(at + 16), data.U32(at + 20))).ToArray();
        if (keys.Select(key => key.Value).Distinct().Count() != keys.Length) throw new InvalidDataException("A map key is listed twice.");
        for (int i = 0; i < keys.Length; i++)
            if (keys[i].Index != i || keys[i].Flags > 2 || keys[i].Tail > 1)
                throw new InvalidDataException("A map key has an unknown index, flag or tail.");
        var spans = new List<(int Start, int End)> { (0, 48) };
        void Span(int[] rows, int stride) { if (rows.Length > 0) spans.Add((rows[0] - 20, rows[^1] + stride)); }
        Span(keyRows, 24); Span(bubbleRows, 80);
        var bubbles = new List<MapBubble>();
        for (int slot = 0; slot < bubbleRows.Length; slot++)
        {
            int at = bubbleRows[slot];
            uint name = data.U32(at), self = data.U32(at + 4);
            if (data.U32(at + 72) != slot || data.U32(at + 76) != 0 || self >= keys.Length)
                throw new InvalidDataException("A map bubble's root slot or self key is invalid.");
            var own = keys[self];
            if (own.NameHash != name || own.Flags != 0 || own.RootSlot != slot || own.Tail != 0)
                throw new InvalidDataException("A map bubble's self key does not match its name and slot.");
            var lists = new List<ushort[]>();
            foreach (var (delta, flags) in new[] { (8, 1), (24, 2) })
            {
                int[] rows = data.Rows(at + delta, IndexClass, 2);
                Span(rows, 2);
                ushort[] indices = rows.Select(data.U16).ToArray();
                if (indices.Distinct().Count() != indices.Length || indices.Any(i => i >= keys.Length
                    || keys[i].Flags != flags || keys[i].RootSlot != uint.MaxValue))
                    throw new InvalidDataException("A map bubble lists an invalid link.");
                lists.Add(indices);
            }
            var expected = new byte[32];
            foreach (int index in lists[0].Concat(lists[1]).Select(i => (int)i).Append((int)self)) expected[index / 8] |= (byte)(1 << index % 8);
            byte[] mask = bytes.AsSpan(at + 40, 32).ToArray();
            if (!mask.AsSpan().SequenceEqual(expected)) throw new InvalidDataException("A map bubble's mask is not its self key and links.");
            bubbles.Add(new(name, self, lists[0], lists[1], mask, (uint)slot));
        }
        if (bubbles.Select(b => b.NameHash).Distinct().Count() != bubbles.Count || bubbles.Select(b => b.SelfIndex).Distinct().Count() != bubbles.Count)
            throw new InvalidDataException("A map bubble is listed twice.");
        foreach (var key in keys)
            if (key.Flags == 0 ? key.RootSlot >= bubbles.Count || bubbles[(int)key.RootSlot].SelfIndex != key.Index : key.RootSlot != uint.MaxValue)
                throw new InvalidDataException("A map key's root slot does not match its role.");
        int name0 = checked((int)(8 + data.I64(8)));
        if (name0 < 48 || name0 >= bytes.Length) throw new InvalidDataException("A map name leaves its definition.");
        int end = Array.IndexOf(bytes, (byte)0, name0);
        if (end < 0) throw new InvalidDataException("A map name is not terminated.");
        spans.Add((name0, end + 1));
        spans.Sort();
        for (int i = 1; i < spans.Count; i++)
            if (spans[i - 1].End > spans[i].Start) throw new InvalidDataException("Map definition records overlap.");
        return new(keys, bubbles, keyRows, bubbleRows, name0, bytes[name0..end]);
    }
}
