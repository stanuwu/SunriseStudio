/**
 * File: Reader for the tiger-pkg lookup cache tiger-mcp writes for d2_ren (lookup_cache_d2_ren_w64.bin). It is the
 * only offline source for resolving 64-bit WideHash references to tags, and it carries the engine's named tags.
 */
using System.Text;

namespace Solforge.Retail;

/** A named tag from the lookup cache (for example activity names such as "crucible_anomaly_2.iron_banner"). */
public sealed record RetailNamedTag(uint Tag, uint Class, string Name);

/**
 * The parts of the lookup cache Solforge uses. Format (VERIFIED on the 139 MB d2_ren cache, it ends exactly at EOF):
 * "TLI0", then bincode with fixed little-endian integers and u64 lengths:
 * HashMap&lt;u16, Vec&lt;UEntryHeader(18 bytes)&gt;&gt; (skipped), HashMap&lt;u64, {u32 hash32, u32 reference}&gt; tag64 entries,
 * HashMap&lt;u32, u64&gt; tag32 -> tag64, Vec&lt;{u32 hash, u32 class, String}&gt; named tags, HashMap&lt;u16, u32&gt; redaction levels.
 */
public sealed class RetailLookupCache
{
    private readonly Dictionary<ulong, uint> tag64;
    private readonly Dictionary<uint, ulong> tag32;
    private readonly Dictionary<uint, RetailNamedTag> namedByTag;
    private readonly Lazy<Dictionary<uint, List<RetailNamedTag>>> namedByClass;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, Dictionary<string, uint>> firstByName = new();

    public IReadOnlyList<RetailNamedTag> Named { get; }
    public int Tag64Count => tag64.Count;

    private RetailLookupCache(Dictionary<ulong, uint> tag64, Dictionary<uint, ulong> tag32, List<RetailNamedTag> named)
    {
        this.tag64 = tag64; this.tag32 = tag32; Named = named;
        namedByTag = new();
        foreach (var row in named) namedByTag.TryAdd(row.Tag, row);
        namedByClass = new(() =>
        {
            var result = new Dictionary<uint, List<RetailNamedTag>>();
            foreach (var row in named)
            {
                if (!result.TryGetValue(row.Class, out var list)) result[row.Class] = list = [];
                list.Add(row);
            }
            return result;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /** Named tags of one class, in file order (an index built once, so lookups do not scan every named tag). */
    public IReadOnlyList<RetailNamedTag> NamedOfClass(uint type) =>
        namedByClass.Value.TryGetValue(type, out var list) ? list : [];

    /** The first named tag of a class with exactly this name (ordinal, as the destination activity lists spell them), or null. */
    public uint? FindNamed(uint type, string name)
    {
        var names = firstByName.GetOrAdd(type, key =>
        {
            var map = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (var row in NamedOfClass(key)) map.TryAdd(row.Name, row.Tag);
            return map;
        });
        return names.TryGetValue(name, out uint tag) ? tag : null;
    }

    /** An empty cache (no file): 64-bit hashes stay unresolved. */
    public static RetailLookupCache Empty { get; } = new(new Dictionary<ulong, uint>(), new Dictionary<uint, ulong>(), []);

    /** The 32-bit tag of a 64-bit hash, or null. */
    public uint? Resolve(ulong hash64) => tag64.TryGetValue(hash64, out uint tag) ? tag : null;
    /** The 64-bit hash of a tag, or null when it has none. */
    public ulong? Hash64Of(uint tag) => tag32.TryGetValue(tag, out ulong hash) ? hash : null;
    /** The engine name of a tag, or null. */
    public RetailNamedTag? NameOf(uint tag) => namedByTag.GetValueOrDefault(tag);

    /** Reads the cache file. Throws InvalidDataException when it is not a TLI0 cache. */
    public static RetailLookupCache Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        if (reader.ReadUInt32() != 0x30494C54) throw new InvalidDataException(path + " is not a tiger-pkg lookup cache (no TLI0 magic).");
        ulong packages = reader.ReadUInt64();
        if (packages > 0x10000) throw new InvalidDataException("Implausible package count in the lookup cache.");
        for (ulong i = 0; i < packages; i++)
        {
            reader.ReadUInt16();
            ulong entries = reader.ReadUInt64();
            if (entries > 0x10000) throw new InvalidDataException("Implausible entry count in the lookup cache.");
            stream.Seek((long)entries * 18, SeekOrigin.Current);
        }
        ulong count64 = Count(reader, 50_000_000);
        var tag64 = new Dictionary<ulong, uint>((int)count64);
        for (ulong i = 0; i < count64; i++)
        {
            ulong hash = reader.ReadUInt64();
            uint tag = reader.ReadUInt32();
            reader.ReadUInt32(); // reference (class) of the tag
            tag64[hash] = tag;
        }
        ulong count32 = Count(reader, 50_000_000);
        var tag32 = new Dictionary<uint, ulong>((int)count32);
        for (ulong i = 0; i < count32; i++) { uint tag = reader.ReadUInt32(); tag32[tag] = reader.ReadUInt64(); }
        ulong namedCount = Count(reader, 10_000_000);
        var named = new List<RetailNamedTag>((int)namedCount);
        for (ulong i = 0; i < namedCount; i++)
        {
            uint tag = reader.ReadUInt32(), type = reader.ReadUInt32();
            ulong length = Count(reader, 1 << 20);
            named.Add(new RetailNamedTag(tag, type, Encoding.UTF8.GetString(reader.ReadBytes((int)length))));
        }
        return new RetailLookupCache(tag64, tag32, named);
    }

    private static ulong Count(BinaryReader reader, ulong max)
    {
        ulong value = reader.ReadUInt64();
        return value <= max ? value : throw new InvalidDataException("Implausible length in the lookup cache.");
    }
}
