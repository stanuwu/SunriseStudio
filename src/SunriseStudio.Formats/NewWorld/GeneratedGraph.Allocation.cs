using System.Buffers.Binary;

namespace SunriseStudio.Formats.NewWorld;

/** One allocated entry; Reference is the partner tag for a GPU header or data entry. */
public sealed record GeneratedEntry(string Key, uint Tag, int EntryIndex, uint Reference, uint TypeInfo, byte[] Data);

/** Allocated entries in key order, and each type-16 root's companion and closure keys. */
public sealed record GeneratedAllocation(IReadOnlyList<GeneratedEntry> Entries, IReadOnlyDictionary<string, uint> Tags,
    IReadOnlyDictionary<string, string> Companions, IReadOnlyDictionary<string, IReadOnlyList<string>> Closures);

public sealed partial class GeneratedGraph
{
    /** Dependency record class of every type-16 root's companion. */
    public const uint DependencyClass = 0x80809EF9;

    public static uint TagOf(ushort package, int entry) => checked(0x80800000u + ((uint)package << 13) + (uint)entry);

    /** Entry type info: file type at bit 9, subtype at bit 6, bit 17 on the GPU data families 40 and 41. */
    public static uint TypeInfoOf(int fileType, int subType) =>
        (uint)(fileType << 9 | subType << 6 | (fileType is 40 or 41 ? 1 << 17 : 0));

    /**
     * Gives every type-16 root a computed companion, allocates all entries in key order from firstEntry and patches
     * every edge. A companion lists its root's closure: targets reached through edges that are not load
     * boundaries, the boundary targets themselves, and the installed closures of every expanded holder
     * (World Studio map_allocation.compile_generated).
     */
    public GeneratedAllocation Allocate(ushort package, int firstEntry, IReadOnlyList<string> roots)
    {
        if (roots.Count == 0 || roots.Distinct().Count() != roots.Count || roots.Any(root => !nodes.ContainsKey(root)))
            throw new InvalidDataException("Expected distinct generated entry points.");
        var outgoing = nodes.Keys.ToDictionary(key => key, _ => new List<(string Target, bool Expand)>(), StringComparer.Ordinal);
        foreach (var ((owner, _), edge) in edges.OrderBy(pair => pair.Key.Owner, StringComparer.Ordinal).ThenBy(pair => pair.Key.Offset))
            outgoing[owner].Add((edge.Target, !edge.LoadBoundary));
        foreach (var node in nodes.Values)
            if (node.Partner is { } partner) outgoing[node.Key].Add((partner, true));
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var walk = new Stack<string>(roots);
        while (walk.TryPop(out string? key))
            if (reached.Add(key)) foreach (var (target, _) in outgoing[key]) walk.Push(target);
        if (reached.Count != nodes.Count) throw new InvalidDataException("Disconnected generated resources would be exported.");
        var companions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in nodes.Values.Where(node => node.FileType == 16))
        {
            string companion = node.Key + "_deps";
            if (nodes.ContainsKey(companion)) throw new InvalidDataException("Generated dependency key collision.");
            companions[node.Key] = companion;
        }
        var ordered = nodes.Keys.Concat(companions.Values).Order(StringComparer.Ordinal).ToArray();
        if (firstEntry < 0 || firstEntry + ordered.Length > 8192) throw new InvalidDataException("Generated graph exceeds package capacity.");
        var tags = new Dictionary<string, uint>(StringComparer.Ordinal);
        for (int i = 0; i < ordered.Length; i++) tags[ordered[i]] = TagOf(package, firstEntry + i);
        var data = nodes.ToDictionary(pair => pair.Key, pair => pair.Value.Data.ToArray(), StringComparer.Ordinal);
        var closures = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (owner, companion) in companions)
        {
            var closure = new HashSet<string>(StringComparer.Ordinal) { owner, companion };
            var expanded = new HashSet<string>(StringComparer.Ordinal) { owner };
            var pending = new Stack<(string Target, bool Expand)>(outgoing[owner]);
            while (pending.TryPop(out var item))
            {
                closure.Add(item.Target);
                if (!item.Expand || !expanded.Add(item.Target)) continue;
                foreach (var next in outgoing[item.Target]) pending.Push(next);
            }
            var groups = new SortedDictionary<int, SortedSet<int>>();
            void Note(uint tag)
            {
                int group = (int)((tag - 0x80800000u) >> 13), index = (int)(tag & 8191);
                if (!groups.TryGetValue(group, out var set)) groups[group] = set = [];
                set.Add(index);
            }
            foreach (string key in closure) Note(tags[key]);
            foreach (var ((holder, _), names) in externals)
                if (expanded.Contains(holder)) foreach (uint tag in names) Note(tag);
            foreach (var (holder, names) in installed)
                if (expanded.Contains(holder)) foreach (uint tag in names) Note(tag);
            data[companion] = EncodeDependencies(tags[companion], tags[owner], groups);
            closures[owner] = closure.Order(StringComparer.Ordinal).ToArray();
        }
        foreach (var ((owner, offset), edge) in edges)
        {
            var raw = data[owner];
            if (BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset)) != 0)
                throw new InvalidDataException("Declared reference preimage does not match its target.");
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(offset), tags[edge.Target]);
        }
        var entries = new List<GeneratedEntry>();
        for (int i = 0; i < ordered.Length; i++)
        {
            string key = ordered[i];
            var node = nodes.GetValueOrDefault(key);
            uint reference = node is null ? DependencyClass : node.Partner is { } partner ? tags[partner] : node.Reference;
            uint typeInfo = node is null ? TypeInfoOf(8, 0) : TypeInfoOf(node.FileType, node.SubType);
            var bytes = data[key];
            if (bytes.Length is < 1 or > 4 * 1024 * 1024) throw new InvalidDataException("A new resource must hold 1 byte to 4 MiB.");
            entries.Add(new(key, tags[key], firstEntry + i, reference, typeInfo, bytes));
        }
        return new(entries, tags, companions, closures);
    }

    /**
     * The installed dependency record layout: a 48-byte header {size, self, owner}, group rows of 40 bytes sorted by
     * package, each with a bitset of max/32+1 words when a u16 index list would be larger, else sorted u16 indices
     * (World Studio root_dependencies.encode; no wide keys).
     */
    public static byte[] EncodeDependencies(uint self, uint owner, IReadOnlyDictionary<int, SortedSet<int>> groups)
    {
        var raw = new NativeBuffer(48);
        raw.Put32(8, self); raw.Put32(12, owner);
        var items = groups.Where(pair => pair.Value.Count != 0).OrderBy(pair => pair.Key).ToArray();
        if (items.Length > 8192) throw new InvalidDataException("Too many dependency packages.");
        int groupAt = raw.AppendNative(16, 0x80809EFB, items.Length, new byte[40 * items.Length]);
        for (int index = 0; index < items.Length; index++)
        {
            var (package, ids) = items[index];
            if (package is < 0x100 or > 0x19FF || ids.Min < 0 || ids.Max >= 8192)
                throw new InvalidDataException("Invalid dependency package or index.");
            int at = groupAt + index * 40;
            raw.Put16(at, (ushort)package);
            int words = ids.Max / 32 + 1;
            if (2 * ids.Count > 4 * words)
            {
                var bits = new uint[words];
                foreach (int id in ids) bits[id / 32] |= 1u << (id % 32);
                raw.AppendNative(at + 8, 0x8080000B, words, NativeRow.U32s(bits));
            }
            else raw.AppendNative(at + 24, 0x8080000A, ids.Count, NativeRow.U16s(ids.Select(id => (ushort)id).ToArray()));
        }
        raw.AppendNative(32, 0x8080013D, 0, []);
        raw.SetSize();
        return raw.ToArray();
    }
}
