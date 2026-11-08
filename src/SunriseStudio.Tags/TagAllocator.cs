using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;

namespace SunriseStudio.Tags;

/**
 * Allocates a project's new tag hashes with TagHashes.Make, in the new-package range. A name keeps
 * its hash for good; a released name keeps its entry reserved, so live reload and saved references
 * never see a hash reused within a project.
 */
public sealed class TagAllocator
{
    public const uint TagBase = TagHashes.Base;
    public const int EntryBits = TagHashes.EntryBits;

    private readonly List<ushort> packages;
    private readonly SortedDictionary<string, uint> tags = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, uint> retired = new(StringComparer.Ordinal);

    /** @param packages Package ids this project owns, in allocation order; each must be a new-package id. */
    public TagAllocator(IEnumerable<ushort> packages)
    {
        this.packages = packages.ToList();
        if (this.packages.Count == 0 || this.packages.Distinct().Count() != this.packages.Count)
            throw new ArgumentException("A project needs at least one package id, each listed once.", nameof(packages));
        foreach (ushort id in this.packages)
            if (!PackageWriter.IsNewPackageId(id))
                throw new ArgumentOutOfRangeException(nameof(packages), $"0x{id:X4} is outside {PackageWriter.NewPackageIdRange}.");
    }

    public IReadOnlyList<ushort> Packages => packages;
    public IReadOnlyDictionary<string, uint> Tags => tags;
    /** Released names; their entries stay taken. */
    public IReadOnlyDictionary<string, uint> Retired => retired;

    /** Seed prefix of a mod's probe; Sunrise probes with the same text plus "#remap". */
    public const string ModSeed = "sunrise-mod-packages\n";

    /**
     * Every new-package id once, in probe order: SHA-256 of the seed picks the start in the
     * new-package range, then ids run upward, wrapping.
     */
    public static IEnumerable<ushort> Probe(string seed)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        int range = PackageWriter.LastNewPackageId - PackageWriter.FirstNewPackageId + 1;
        uint first = BinaryPrimitives.ReadUInt32LittleEndian(digest) % (uint)range;
        for (int step = 0; step < range; step++)
            yield return (ushort)(PackageWriter.FirstNewPackageId + (first + step) % range);
    }

    /** The first package a mod takes: the first id of its probe that is not taken. */
    public static ushort PreferredPackage(string modId, Func<ushort, bool> taken) =>
        FirstFree(Probe(ModSeed + modId), taken);

    /** @return The next id of the mod's probe this allocator does not hold yet. */
    public ushort NextPackage(string modId, Func<ushort, bool> taken) =>
        FirstFree(Probe(ModSeed + modId), id => taken(id) || packages.Contains(id));

    /** Seed prefix of a new tag's wide hash; the mod id and the tag's name follow it. */
    public const string WideSeed = "sunrise-wide-hash\n";

    /**
     * A wide hash for a new tag: the first 8 bytes of SHA-256 of the seed, mod id and name, then the
     * next value up while that one is taken. Zero and all-ones mean "no tag", so they are skipped.
     */
    public static ulong WideHash(string modId, string name, Func<ulong, bool> taken)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(WideSeed + modId + "\n" + name));
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(digest);
        while (value is 0 or ulong.MaxValue || taken(value)) value++;
        return value;
    }

    /** Called when every package is full; returns the next package id to use. Null makes a full allocator throw. */
    public Func<ushort>? Grow { get; set; }

    /**
     * True for a project created before generated object data took mod-probe ids: it keeps the old
     * seed until the user opts in, since its ids are already in shipped mods and saved remaps.
     */
    public bool IdentityGeneratedPackages { get; set; }

    private static ushort FirstFree(IEnumerable<ushort> order, Func<ushort, bool> taken)
    {
        foreach (ushort id in order)
            if (!taken(id)) return id;
        throw new InvalidOperationException("Every new-package id is taken.");
    }

    /** @return The name's hash; a new name takes the next entry after every entry ever used, package by package. */
    public uint Allocate(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (tags.TryGetValue(name, out uint existing)) return existing;
        if (retired.ContainsKey(name)) throw new InvalidOperationException($"\"{name}\" was released; its hash stays retired.");
        var used = tags.Values.Concat(retired.Values).Select(TagHashes.Split).ToLookup(x => x.PackageId, x => x.EntryIndex);
        foreach (ushort package in packages)
        {
            int next = used[package].DefaultIfEmpty(-1).Max() + 1;
            if (next >= TagHashes.EntriesPerPackage) continue;
            uint hash = TagHashes.Make(package, next);
            tags[name] = hash;
            return hash;
        }
        if (Grow is null)
            throw new InvalidOperationException($"Every package of this project holds {TagHashes.EntriesPerPackage} entries; the project needs another package id.");
        ushort grown = Grow();
        if (!PackageWriter.IsNewPackageId(grown) || packages.Contains(grown))
            throw new InvalidOperationException($"Package 0x{grown:X4} cannot extend this project.");
        packages.Add(grown);
        return tags[name] = TagHashes.Make(grown, 0);
    }

    /** Releases a name; its entry is never handed out again. */
    public void Release(string name)
    {
        if (!tags.Remove(name, out uint hash)) throw new KeyNotFoundException($"\"{name}\" has no tag.");
        retired[name] = hash;
    }

    /**
     * allocation.json: {"packages": ["0x0412"], "tags": {name: "0x80882004"}, "retired": {...}}, keys
     * sorted, plus "generated": "identity" for a project that keeps its old generated-data ids.
     */
    public byte[] ToJson()
    {
        var root = new JsonObject
        {
            ["packages"] = new JsonArray(packages.Select(x => (JsonNode)$"0x{x:X4}").ToArray()),
            ["tags"] = new JsonObject(tags.Select(x => KeyValuePair.Create(x.Key, (JsonNode?)StableJson.HexText(x.Value)))),
            ["retired"] = new JsonObject(retired.Select(x => KeyValuePair.Create(x.Key, (JsonNode?)StableJson.HexText(x.Value)))),
        };
        if (IdentityGeneratedPackages) root["generated"] = "identity";
        return StableJson.Write(StableJson.Sorted(root));
    }

    /** Reads allocation.json; every stored hash must belong to one of the listed packages. */
    public static TagAllocator Parse(ReadOnlySpan<byte> json)
    {
        var root = JsonNode.Parse(json.ToArray()) as JsonObject ?? throw new InvalidDataException("allocation.json is not an object.");
        try
        {
            var allocator = new TagAllocator(((JsonArray)root["packages"]!).Select(x => (ushort)Hex((string)x!)));
            foreach (var (name, value) in (JsonObject)root["tags"]!) allocator.tags[name] = Hex((string)value!);
            if (root["retired"] is JsonObject retiredNames)
                foreach (var (name, value) in retiredNames) allocator.retired[name] = Hex((string)value!);
            allocator.IdentityGeneratedPackages = root["generated"] switch
            {
                null => false,
                JsonValue value when (string?)value == "identity" => true,
                _ => throw new InvalidDataException("allocation.json \"generated\" must be \"identity\" or absent."),
            };
            var owned = allocator.packages.ToHashSet();
            var all = allocator.tags.Values.Concat(allocator.retired.Values).ToList();
            if (all.Any(hash => !owned.Contains(TagHashes.Split(hash).PackageId)) || all.Distinct().Count() != all.Count)
                throw new InvalidDataException("allocation.json names a hash outside its packages, or one hash twice.");
            return allocator;
        }
        catch (Exception error) when (error is InvalidCastException or NullReferenceException or FormatException or OverflowException)
        {
            throw new InvalidDataException("allocation.json has the wrong shape: " + error.Message, error);
        }
    }

    private static uint Hex(string text) => text.StartsWith("0x", StringComparison.Ordinal)
        ? uint.Parse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
        : throw new FormatException($"Expected 0x hex, got \"{text}\".");
}
