using System.Buffers.Binary;
using SunriseStudio.Tags.Document;
using SunriseStudio.Packages.Registry;

namespace SunriseStudio.Tags.Graph;

/**
 * One clone: old hash to new hash, the new tags' bytes and classes, and every 4-byte aligned word in
 * a clone that still holds a member's old hash outside the fields the registry describes.
 */
public sealed record CloneResult(IReadOnlyDictionary<uint, uint> Map, IReadOnlyDictionary<uint, byte[]> Tags,
    IReadOnlyDictionary<uint, uint> Classes, IReadOnlyList<(uint Tag, int Offset, uint OldHash)> Unmapped)
{
    /** New wide hash of each copy another member names by wide hash; the build writes them as aliases. */
    public IReadOnlyDictionary<uint, ulong> WideHashes { get; init; } = new Dictionary<uint, ulong>();
}

/** Deep clone with remap: a set of tags becomes new tags whose references to each other follow the copy. */
public static class TagCloner
{
    /**
     * @param members Tags to clone, usually a ContentGraph.DependencyClosure filtered to one package or class set.
     * @param name Allocation name per old tag; the allocator keeps it stable across rebuilds.
     * @param wideHash New wide hash for a member another member names by wide hash; null refuses such a clone.
     */
    public static CloneResult Clone(IReadOnlyCollection<uint> members, ITagSource source, Func<uint, uint> classOf, RegistryIndex schema,
        TagAllocator allocator, Func<uint, string> name, Func<uint, ulong>? wideHash = null)
    {
        var map = members.Order().ToDictionary(x => x, x => allocator.Allocate(name(x)));
        var wide = new Dictionary<uint, ulong>();
        byte[] wideBytes = new byte[8];
        var tags = new Dictionary<uint, byte[]>();
        var classes = new Dictionary<uint, uint>();
        var unmapped = new List<(uint, int, uint)>();
        foreach (var (old, copy) in map)
        {
            uint classId = classOf(old);
            var document = TagDocument.Parse(source.Read(old), classId, schema);
            var rewritten = new HashSet<int>();
            foreach (var reference in document.References.Where(x => !x.IsNull))
            {
                uint? target = reference.Kind == TagReferenceKind.WideRef ? source.ResolveWide(reference.Value) : (uint)reference.Value;
                if (target is not { } member || !map.TryGetValue(member, out uint replacement)) continue;
                if (reference.Kind == TagReferenceKind.WideRef)
                {
                    if (wideHash is null)
                        throw new NotSupportedException($"0x{old:X8} names member 0x{member:X8} by wide hash, and the clone has no wide hashes to give.");
                    if (!wide.TryGetValue(replacement, out ulong value)) wide[replacement] = value = wideHash(member);
                    BinaryPrimitives.WriteUInt64LittleEndian(wideBytes, value);
                    document.Write(reference.Offset, wideBytes);
                    rewritten.Add(reference.Offset + 4);
                }
                else document.WriteU32(reference.Offset, replacement);
                rewritten.Add(reference.Offset);
            }
            byte[] bytes = document.Encode();
            for (int offset = 0; offset + 4 <= bytes.Length; offset += 4)
            {
                uint word = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
                if (!rewritten.Contains(offset) && map.ContainsKey(word)) unmapped.Add((copy, offset, word));
            }
            tags[copy] = bytes;
            classes[copy] = classId;
        }
        return new(map, tags, classes, unmapped) { WideHashes = wide };
    }
}
