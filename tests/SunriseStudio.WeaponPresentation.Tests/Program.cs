using System.Buffers.Binary;
using System.Security.Cryptography;
using SunriseStudio.Animation;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;

int rootAt = Array.IndexOf(args, "--game-root");
if (rootAt < 0 || rootAt + 1 >= args.Length)
{
    Console.Error.WriteLine("usage: --game-root <dir>");
    Environment.ExitCode = 2;
    return;
}
using var catalog = PackageCatalog.Open(args[rootAt + 1], cacheOptions: new() { Enabled = false });
const ushort package = 0xB1D;
uint Tag(int entry) => TagHashes.Make(package, entry);
var prepared = WeaponPresentationAuthoring.Prepare(catalog, package, 20, "Presentation fixture", Tag(0), Tag(1),
    [new("fixture_idle", Tag(2), 2), new("fixture_inspect", Tag(3), 106)]);
Check(prepared.Assets.Count == 8, "Named clips did not clone the native query table.");
var bytes = prepared.Assets.ToDictionary(asset => Tag(asset.Native!.EntryIndex!.Value), asset => asset.Data);
var overlays = prepared.Assets.Select(asset => new PackageOverlayEntry(new PackageEntry(
    Tag(asset.Native!.EntryIndex!.Value), asset.Native.Reference,
    (int)((asset.Native.TypeInfo >> 9) & 127), (int)((asset.Native.TypeInfo >> 6) & 7),
    asset.Data.Length, "w64_presentation_fixture_0b1d_0.pkg", package, asset.Native.EntryIndex.Value)
    { TypeInfo = asset.Native.TypeInfo }, asset.Name)).ToList();
uint[] sources = [0x81A6B4F0, 0x81A6B4F1, 0x81A6B4F2, 0x81A6B4F0];
for (int i = 0; i < sources.Length; i++)
{
    catalog.TryGetEntry(sources[i], out var entry);
    bytes[Tag(i)] = catalog.Read(sources[i]);
    overlays.Add(new(entry with { TagHash = Tag(i), PackageId = package, EntryIndex = i,
        PackageName = "w64_presentation_fixture_0b1d_0.pkg" }));
}
catalog.SetOverlayEntries(overlays);
catalog.ReadOverride = tag => bytes.GetValueOrDefault(tag);
var clips = new EntityAnimationCatalog(catalog).Read(prepared.PresentationEntityTag);
Check(clips.Count == 118 && clips[113].ClipTag == Tag(0) && clips[114].ClipTag == Tag(1)
    && clips[116].ClipTag == Tag(2) && clips[117].ClipTag == Tag(3), "Native clip graph did not reopen.");
for (int i = 0; i < sources.Length; i++) WeaponPresentationAuthoring.ValidateClip(catalog, Tag(i));
byte[] query = bytes[Tag(27)], definition = bytes[prepared.AnimationDefinitionTag];
byte[] rebound = WeaponPresentationAuthoring.BindClip(catalog, definition, 114, Tag(0));
var allowed = Enumerable.Range(ArrayData(definition, 8) + 114 * 4, 4).ToHashSet();
int namedCount = checked((int)I64(definition, 104)), namedStart = ArrayData(definition, 104), reboundNames = 0;
for (int i = 0; i < namedCount; i++)
{
    int row = namedStart + i * 32;
    if (BinaryPrimitives.ReadInt16LittleEndian(definition.AsSpan(row + 24)) != 114) continue;
    allowed.UnionWith(Enumerable.Range(row + 20, 4)); reboundNames++;
    Check(BinaryPrimitives.ReadSingleLittleEndian(rebound.AsSpan(row + 20))
        == new AnimationControlReader(catalog).Read(Tag(0)).DurationSeconds, "Rebinding left an old name duration.");
}
Check(reboundNames > 0 && U32(rebound, ArrayData(rebound, 8) + 114 * 4) == Tag(0)
    && Enumerable.Range(0, definition.Length).All(i => allowed.Contains(i) || definition[i] == rebound[i]),
    "Rebinding changed unrelated animation definition fields.");
foreach (var (name, hash) in prepared.AddedNameHashes)
{
    int row = Resolve(query, hash);
    int nameAt = ArrayData(definition, 104) + row * 32;
    int clipIndex = BinaryPrimitives.ReadInt16LittleEndian(definition.AsSpan(nameAt + 24));
    Check(U32(definition, nameAt + 16) == hash && clipIndex == prepared.AddedClipIndices[name],
        "Native name lookup selected another clip row.");
}
byte[] originalQuery = catalog.ReadOriginal(WeaponPresentationAuthoring.QueryTemplate);
int originalCount = checked((int)I64(originalQuery, 24));
for (int i = 0; i < originalCount; i++)
    Check(Target(originalQuery, ArrayData(originalQuery, 24) + i * 16 + 8)
        == Target(query, ArrayData(query, 24) + i * 16 + 8), "An existing animation query target moved.");
foreach (var source in prepared.SourceHashes)
    Check(Convert.ToHexString(SHA256.HashData(catalog.ReadOriginal(source.Key))) == source.Value,
        "Presentation authoring changed an installed source.");
Console.WriteLine("weapon_presentation clone=pass owners=pass root_links=pass clips=118 named_lookup=pass rebind=pass query_targets=preserved source=unchanged");
catalog.ReadOverride = null;
catalog.SetOverlayEntries([]);
TemplateLayoutChecks.Run(catalog);

/** Native query kind one selects an entry from its weighted definition-row list. */
static int Resolve(byte[] query, uint hash)
{
    int count = checked((int)I64(query, 8)), rows = ArrayData(query, 8);
    for (int i = 1; i < count; i++) Check(U32(query, rows + (i - 1) * 8) < U32(query, rows + i * 8), "Query keys are unsorted.");
    int lo = 0, hi = count - 1;
    while (lo <= hi)
    {
        int middle = lo + (hi - lo) / 2;
        uint key = U32(query, rows + middle * 8);
        if (hash < key) hi = middle - 1;
        else if (hash > key) lo = middle + 1;
        else
        {
            int index = checked((int)U32(query, rows + middle * 8 + 4));
            int node = ArrayData(query, 24) + index * 16;
            Check(query[node] == 1, "New animation query is not a direct weighted selector.");
            int payload = Target(query, node + 8);
            Check(I64(query, payload) == 1, "New animation query has multiple results.");
            return checked((int)U32(query, ArrayData(query, payload)));
        }
    }
    throw new InvalidDataException("Named animation query is missing.");
}

static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
static long I64(byte[] bytes, int at) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at));
static int Target(byte[] bytes, int at) => checked(at + (int)I64(bytes, at));
static int ArrayData(byte[] bytes, int field) => Target(bytes, field + 8) + 16;
