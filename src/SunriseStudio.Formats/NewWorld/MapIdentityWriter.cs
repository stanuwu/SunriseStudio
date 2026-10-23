using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SunriseStudio.Formats.NewWorld;

/** A new bubble's definition identity: its name hash and 64-bit self key. */
public sealed record NewBubbleIdentity(uint NameHash, ulong Key);

/** Map definitions (80809962) and the map graph above the tables: containers, branches, bubbles, the root. */
public static class MapIdentityWriter
{
    public const uint DefinitionClass = 0x80809962, ContainerClass = 0x80808A54, BranchClass = 0x808091E0,
        BubbleClass = 0x80807DAE, RootClass = 0x808091DE, LookupClass = 0x80809780, TableClass = 0x808099D6;
    private const uint KeyClass = 0x808098C4, BubbleRowClass = 0x808098C2, NameClass = 0x80800065;

    /** FNV-1 over UTF-8: multiply, then XOR each byte. */
    public static uint NameHash(string text)
    {
        uint value = 0x811C9DC5;
        foreach (byte b in Encoding.UTF8.GetBytes(text)) value = unchecked(value * 0x01000193) ^ b;
        return value;
    }

    /** The first 8 bytes of SHA-256 over UTF-8, little-endian. */
    public static ulong Key64(string text) =>
        BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /**
     * Slot i holds bubble i with self key index i and empty link lists, in the native layout of all 85 installed
     * definitions: header, key rows, bubble rows, then the name (World Studio map_definition.create).
     */
    public static byte[] Definition(string name, IReadOnlyList<NewBubbleIdentity> bubbles)
    {
        byte[] text = Encoding.UTF8.GetBytes(name);
        if (text.Length is < 1 or > 256 || name.Contains('\0')) throw new InvalidDataException("A map name needs 1 to 256 bytes.");
        if (bubbles.Count is < 1 or > 64) throw new InvalidDataException("A map holds 1 to 64 bubbles.");
        if (bubbles.Select(b => b.NameHash).Distinct().Count() != bubbles.Count || bubbles.Select(b => b.Key).Distinct().Count() != bubbles.Count)
            throw new InvalidDataException("Two bubbles share an identity.");
        var keys = new byte[24 * bubbles.Count];
        var rows = new byte[80 * bubbles.Count];
        for (int slot = 0; slot < bubbles.Count; slot++)
        {
            var key = keys.AsSpan(24 * slot);
            BinaryPrimitives.WriteUInt64LittleEndian(key, bubbles[slot].Key);
            BinaryPrimitives.WriteUInt32LittleEndian(key[8..], bubbles[slot].NameHash);
            BinaryPrimitives.WriteInt16LittleEndian(key[14..], (short)slot);
            BinaryPrimitives.WriteUInt32LittleEndian(key[16..], (uint)slot);
            var row = rows.AsSpan(80 * slot);
            BinaryPrimitives.WriteUInt32LittleEndian(row, bubbles[slot].NameHash);
            BinaryPrimitives.WriteUInt32LittleEndian(row[4..], (uint)slot);
            row[40 + slot / 8] = (byte)(1 << (slot % 8));
            BinaryPrimitives.WriteUInt32LittleEndian(row[72..], (uint)slot);
        }
        var raw = new NativeBuffer(48);
        raw.AppendNative(16, KeyClass, bubbles.Count, keys);
        int start = raw.AppendNative(32, BubbleRowClass, bubbles.Count, rows);
        for (int slot = 0; slot < bubbles.Count; slot++)
        {
            raw.AppendNative(start + 80 * slot + 8, 0x80809B9C, 0, []);
            raw.AppendNative(start + 80 * slot + 24, 0x80809B9C, 0, []);
        }
        byte[] stored = [.. text, 0];
        int at = (raw.Length + 12 + 7) & ~7;
        raw.AddZeros(at - raw.Length);
        raw.Put32(at - 12, NameClass); raw.Put64(at - 8, (ulong)stored.Length);
        raw.Add(stored);
        raw.PutI64(8, at - 8);
        raw.SetSize();
        return raw.ToArray();
    }

    /** The 256-bit self mask of a slot: its key index i is slot i in a new definition. */
    public static byte[] SelfMask(int slot)
    {
        var mask = new byte[32];
        mask[slot / 8] = (byte)(1 << (slot % 8));
        return mask;
    }

    /** 56-byte header, mask at +8, compact 0x80808BB0 slot array at +40; table i is referenced at +80+4i. */
    public static string AddContainer(GeneratedGraph graph, string key, IReadOnlyList<string> tables, byte[] mask, int fileType)
    {
        foreach (string table in tables) Require(graph, table, TableClass, 8);
        if (tables.Count is not (1 or 2 or 23)) throw new InvalidDataException("A container has 1, 2 or 23 slots.");
        var raw = new NativeBuffer(56);
        raw.PutBytes(8, mask);
        raw.AppendCompact(40, 0x80808BB0, Enumerable.Range(0, tables.Count).Select(_ => new byte[4]).ToArray());
        graph.Add(key, raw.ToArray(), ContainerClass, fileType);
        for (int i = 0; i < tables.Count; i++) graph.Reference(key, 80 + 4 * i, tables[i]);
        return key;
    }

    /** 24-byte header and a compact 0x808084C1 array of wide container rows at +48+16i. */
    public static string AddBranch(GeneratedGraph graph, string key, IReadOnlyList<string> containers)
    {
        foreach (string container in containers) Require(graph, container, ContainerClass, 8, 16);
        if (containers.Distinct().Count() != containers.Count) throw new InvalidDataException("A branch lists one container twice.");
        var raw = new NativeBuffer(24);
        raw.AppendCompact(8, 0x808084C1, containers.Select(_ => NativeRow.Wide()).ToArray());
        graph.Add(key, raw.ToArray(), BranchClass, 16);
        for (int i = 0; i < containers.Count; i++) graph.Reference(key, 48 + 16 * i, containers[i], wide: true);
        return key;
    }

    /** 80 bytes: branch at +8 behind a load boundary, {64, name hash} at +16, mask at +28, no bounds records. */
    public static string AddBubble(GeneratedGraph graph, string key, string branch, uint nameHash, int slot)
    {
        Require(graph, branch, BranchClass, 16);
        var raw = new NativeBuffer(80);
        raw.Put64(16, 64); raw.Put32(24, nameHash);
        raw.PutBytes(28, SelfMask(slot));
        raw.SetSize();
        graph.Add(key, raw.ToArray(), BubbleClass, 16);
        graph.Reference(key, 8, branch, loadBoundary: true);
        return key;
    }

    /** 32 bytes: definition at +8, lookup at +12 and one wide 0x80807D53 slot row per bubble behind a load boundary. */
    public static string AddRoot(GeneratedGraph graph, string key, IReadOnlyList<string> bubbles, string definition, string lookup)
    {
        foreach (string bubble in bubbles) Require(graph, bubble, BubbleClass, 16);
        Require(graph, definition, DefinitionClass, 8);
        Require(graph, lookup, LookupClass, 8, 16);
        var raw = new NativeBuffer(32);
        var rows = raw.AppendPadded(16, 0x80807D53, 16, bubbles.Select(_ => NativeRow.Wide()).ToArray());
        raw.SetSize();
        graph.Add(key, raw.ToArray(), RootClass, 16);
        graph.Reference(key, 8, definition);
        graph.Reference(key, 12, lookup);
        for (int i = 0; i < bubbles.Count; i++) graph.Reference(key, rows[i], bubbles[i], wide: true, loadBoundary: true);
        return key;
    }

    /** The observed empty named-resource lookup: both arrays empty. */
    public static byte[] EmptyLookup()
    {
        var raw = new NativeBuffer(40);
        raw.SetSize();
        return raw.ToArray();
    }

    internal static void Require(GeneratedGraph graph, string key, uint type, params int[] fileTypes)
    {
        var node = graph.Node(key);
        if (node.Reference != type || !fileTypes.Contains(node.FileType) || node.SubType != 0)
            throw new InvalidDataException($"{key} has an unexpected class or entry family.");
    }
}
