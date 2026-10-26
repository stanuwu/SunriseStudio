/**
 * File: Outgoing references of a retail tag found offline: every aligned u32 that is an indexed tag, and every
 * 16-byte WideHash whose 64-bit hash the lookup cache resolves. This is a heuristic scan (a hash word can look like a
 * tag), so results carry the offset and how they were found; tiger-mcp's get_tag_references gives incoming ones.
 */
namespace Solforge.Retail.Porting;

/** One outgoing reference: where it sits in the parent and the tag it names. Wide is true for a 64-bit WideHash. */
public sealed record RetailReference(int Offset, uint Tag, bool Wide);

/** Offline reference scanning. */
public static class RetailReferences
{
    /** Outgoing references of bytes (needs the index; without it only WideHash references are found). */
    public static IReadOnlyList<RetailReference> Scan(RetailSource source, ReadOnlySpan<byte> bytes, uint self = 0, int limit = 4096)
    {
        var result = new List<RetailReference>();
        var index = source.Index;
        var seen = new HashSet<(int, uint)>();
        for (int at = 0; at + 4 <= bytes.Length && result.Count < limit; at += 4)
        {
            uint value = RetailBytes.U32(bytes, at);
            if (value != self && RetailTag.Plausible(value) && index?.Contains(value) == true && seen.Add((at, value)))
                result.Add(new RetailReference(at, value, false));
            // WideHash: u32 hash32, u32 flag 0, u64 hash64 (8-aligned).
            if ((at & 7) == 0 && at + 16 <= bytes.Length && RetailBytes.U32(bytes, at + 4) == 0)
            {
                ulong wide = RetailBytes.U64(bytes, at + 8);
                if (wide != 0 && wide != ulong.MaxValue && source.Lookup.Resolve(wide) is { } tag && tag != self && seen.Add((at + 8, tag)))
                    result.Add(new RetailReference(at + 8, tag, true));
            }
        }
        return result;
    }

    /** Breadth-first walk of outgoing references from root, up to depth levels and maxTags tags. */
    public static IReadOnlyList<(uint Tag, int Depth, uint Parent)> Walk(RetailSource source, uint root, int depth, int maxTags, CancellationToken cancel = default)
    {
        var order = new List<(uint, int, uint)> { (root, 0, 0) };
        var seen = new HashSet<uint> { root };
        for (int i = 0; i < order.Count && order.Count < maxTags; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var (tag, level, _) = order[i];
            if (level >= depth) continue;
            // Buffers and other raw data have no references worth following (checked before reading: they can be huge).
            if (source.Index?.Find(tag) is { IsStruct: false }) continue;
            if (source.Read(tag) is not { } bytes) continue;
            foreach (var reference in Scan(source, bytes, tag))
            {
                if (!seen.Add(reference.Tag)) continue;
                order.Add((reference.Tag, level + 1, tag));
                if (order.Count >= maxTags) break;
            }
        }
        return order;
    }
}
