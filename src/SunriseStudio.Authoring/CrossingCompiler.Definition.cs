using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SunriseStudio.Formats;
using SunriseStudio.Scene;

namespace SunriseStudio.Authoring;

public static partial class CrossingCompiler
{
    /** Class of the length-prefixed name at the end of a map definition. */
    private const uint MapNameClass = 0x80800065;

    /**
     * A definition in the native layout all 85 installed ones use: a 48-byte header, key rows, bubble rows, each
     * bubble's first and second list in slot order, then the name. List descriptors inside bubble rows are rewritten.
     */
    internal static byte[] SerializeDefinition(IReadOnlyList<byte[]> keys, IReadOnlyList<byte[]> bubbles,
        IReadOnlyList<(ushort[] First, ushort[] Second)> lists, byte[] name)
    {
        byte[] output = new byte[48];
        output = TypedArray(output, 16, MapIdentity.KeyClass, 24, keys, out _);
        output = TypedArray(output, 32, MapIdentity.BubbleClass, 80, bubbles, out int start);
        for (int slot = 0; slot < lists.Count; slot++)
            foreach (var (delta, indices) in new[] { (8, lists[slot].First), (24, lists[slot].Second) })
                output = TypedArray(output, start + 80 * slot + delta, MapIdentity.IndexClass, 2,
                    [.. indices.Select(index => BitConverter.GetBytes(index))], out _);
        byte[] text = [.. name, 0];
        int at = (output.Length + 12 + 7) & ~7;
        byte[] result = new byte[at + text.Length];
        output.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(at - 12), MapNameClass);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(at - 8), (ulong)text.Length);
        text.CopyTo(result, at);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(8), at - 8);
        BinaryPrimitives.WriteUInt64LittleEndian(result, (ulong)result.Length);
        return result;
    }

    /** NativeArrays.AppendNative over records of one size; an empty array is a zero descriptor, as installed. */
    private static byte[] TypedArray(byte[] source, int field, uint type, int stride, IReadOnlyList<byte[]> rows, out int first)
    {
        if (rows.Any(row => row.Length != stride)) throw new InvalidDataException("A map record has the wrong size.");
        return NativeArrays.AppendNative(source, field, type, rows, out first);
    }

    /** The definition's own records serialized again; null when the bytes hold anything the records do not. */
    private static (List<byte[]> Keys, List<byte[]> Bubbles, List<(ushort[], ushort[])> Lists)? NativeRecords(byte[] raw, MapIdentity identity)
    {
        var keys = identity.KeyOffsets.Select(at => raw[at..(at + 24)]).ToList();
        var bubbles = identity.BubbleOffsets.Select(at => raw[at..(at + 80)]).ToList();
        var lists = identity.Bubbles.Select(bubble => (bubble.FirstLinks.ToArray(), bubble.SecondLinks.ToArray())).ToList();
        return SerializeDefinition(keys, bubbles, lists, identity.Name).AsSpan().SequenceEqual(raw) ? (keys, bubbles, lists) : null;
    }

    /** True when the definition is exactly the native serialization of its own records, as all 85 installed ones are. */
    internal static bool IsNativeDefinition(byte[] raw) => NativeRecords(raw, MapIdentity.Decode(raw)) is not null;

    /**
     * World Studio's link identity for a bubble pair: names sorted, prefix "{namespace}/link/{a}/{b}", key the first
     * eight bytes of SHA-256 of prefix + ":key", name the FNV-1 hash of the prefix.
     */
    internal static (ulong Key, uint NameHash, string Prefix) LinkIdentity(string space, string first, string second)
    {
        string[] ids = [first, second];
        Array.Sort(ids, StringComparer.Ordinal);
        string prefix = $"{space}/link/{ids[0]}/{ids[1]}";
        ulong key = BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(prefix + ":key")));
        uint hash = 0x811C9DC5;
        foreach (byte value in Encoding.UTF8.GetBytes(prefix)) hash = unchecked(hash * 0x01000193) ^ value;
        return (key, hash, prefix);
    }

    /** Appends one flag-1 key listed first by both bubbles and sets its bit in both masks; every other record stays. */
    internal static byte[] AppendLink(byte[] raw, uint firstSlot, uint secondSlot, uint nameHash, ulong key)
    {
        var identity = MapIdentity.Decode(raw);
        if (firstSlot == secondSlot || firstSlot >= identity.Bubbles.Count || secondSlot >= identity.Bubbles.Count)
            throw new InvalidDataException("A crossing link needs two bubbles of the map.");
        if (identity.Keys.Count >= MapIdentity.MaximumKeys) throw new InvalidDataException("The map definition already holds 256 keys.");
        if (identity.Keys.Any(existing => existing.Value == key || existing.NameHash == nameHash))
            throw new InvalidDataException($"The crossing link identity {key:X16} collides with a key of the map.");
        var records = NativeRecords(raw, identity)
            ?? throw new InvalidDataException("The map definition holds bytes outside its records; Studio cannot add a link to it.");
        ushort index = (ushort)identity.Keys.Count;
        var row = new byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(row, key);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(8), nameHash);
        BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(row.AsSpan(14), (short)index);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(16), uint.MaxValue);
        records.Keys.Add(row);
        foreach (uint slot in new[] { firstSlot, secondSlot })
        {
            records.Bubbles[(int)slot][40 + index / 8] |= (byte)(1 << index % 8);
            var (first, second) = records.Lists[(int)slot];
            records.Lists[(int)slot] = ([.. first, index], second);
        }
        byte[] result = SerializeDefinition(records.Keys, records.Bubbles, records.Lists, identity.Name);
        var after = MapIdentity.Decode(result);
        for (int i = 0; i < identity.Keys.Count; i++)
            if (after.Keys[i] != identity.Keys[i]) throw new InvalidDataException("Adding a link changed an existing map key.");
        for (int slot = 0; slot < identity.Bubbles.Count; slot++)
        {
            var (old, now) = (identity.Bubbles[slot], after.Bubbles[slot]);
            bool linked = slot == firstSlot || slot == secondSlot;
            if (now.NameHash != old.NameHash || now.SelfIndex != old.SelfIndex || !now.SecondLinks.SequenceEqual(old.SecondLinks)
                || !now.FirstLinks.SequenceEqual(linked ? [.. old.FirstLinks, index] : old.FirstLinks))
                throw new InvalidDataException("Adding a link changed an unrelated bubble.");
        }
        return result;
    }

    /** A class-0x80807DAE bubble resource with the definition's new mask at +28; its name at +24 must match. */
    internal static byte[] RebindBubbleMask(byte[] raw, MapBubble before, MapBubble after)
    {
        if (raw.Length < 80 || BinaryPrimitives.ReadUInt64LittleEndian(raw) != (ulong)raw.Length)
            throw new InvalidDataException("A bubble resource has the wrong size header.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(24)) != before.NameHash || !raw.AsSpan(28, 32).SequenceEqual(before.Mask))
            throw new InvalidDataException("A bubble resource's name or mask differs from its map definition.");
        byte[] output = [.. raw];
        after.Mask.CopyTo(output, 28);
        return output;
    }
}
