using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

string Arg(string key) => args[Array.IndexOf(args, key) + 1];
if (!new[] { "--game-root", "--key-source", "--project", "--output" }
    .All(key => Array.IndexOf(args, key) is >= 0 and var at && at + 1 < args.Length))
{
    Console.Error.WriteLine("usage: --game-root <dir> --key-source <exe> --project <sunrise.project.json> --output <folder>");
    Environment.ExitCode = 2;
    return;
}
using var catalog = PackageCatalog.Open(Arg("--game-root"), Arg("--key-source"), cacheOptions: new() { Enabled = false });
var project = StudioProject.Open(Arg("--project"));
string before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(project.ManifestPath)));
var result = ProjectBuilder.Build(project, catalog, Arg("--output"));
using var built = PackageCatalog.Open(result.OutputDirectory, catalog.KeySource, cacheOptions: new() { Enabled = false });
using var receipt = JsonDocument.Parse(File.ReadAllBytes(result.ReceiptPath));
var rows = receipt.RootElement.GetProperty("rootManifests").EnumerateArray().ToArray();
var roots = project.AddedAssets.Where(asset => asset.Native is { } native && (native.TypeInfo & 0xF000) == 0x2000).ToArray();
Check(rows.Length == roots.Length && roots.Length > 0, "Every weapon root needs a generated manifest.");
var reached = new HashSet<uint>();
var generated = new List<PackageManifest>();
foreach (var row in rows)
{
    uint root = Convert.ToUInt32(row.GetProperty("root").GetString(), 16);
    uint manifestTag = Convert.ToUInt32(row.GetProperty("manifest").GetString(), 16);
    uint sourceManifest = Convert.ToUInt32(row.GetProperty("sourceManifest").GetString(), 16);
    Check(built.TryGetEntry(root, out var entry) && (entry.TypeInfo & 0xF000) == 0x2000,
        "The built root lost its native manifest flag.");
    var map = PackageWriter.ReadRootManifests(Path.Combine(built.PackageDirectory, entry.PackageName + ".pkg"));
    Check(map.Single(pair => pair.RootTag == root).ManifestTag == manifestTag, "Native root lookup has no manifest.");
    byte[] data = built.Read(manifestTag);
    Check(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)) == manifestTag
        && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12)) == root, "Manifest identities do not match.");
    var manifest = PackageManifest.Read(data);
    generated.Add(manifest);
    var original = PackageManifest.Read(catalog.ReadOriginal(sourceManifest));
    uint[] retained = row.GetProperty("retainedDependencies").EnumerateArray()
        .Select(value => Convert.ToUInt32(value.GetString(), 16)).ToArray();
    Check(manifest.Lists(root) && manifest.Lists(manifestTag), "The generated manifest omits its root or itself.");
    Check(retained.All(manifest.Lists), "A retained native dependency is not listed.");
    // Only a wrapper that names a model entity at +0x10 carries weapon art; the sound wrapper does not.
    if (entry.Reference == 0x8080744A && built.TryGetEntry(BinaryPrimitives.ReadUInt32LittleEndian(built.Read(root).AsSpan(16)),
            out var wrapped) && wrapped.Reference == 0x80809C0F)
    {
        var missing = new uint[] { 0x815B8156, 0x815B8157, 0x80C70B8E }.Where(tag => !manifest.Lists(tag)).ToArray();
        Check(missing.Length == 0, $"Weapon root {root:X8} lost installed pixel shader or sampler "
            + string.Join(", ", missing.Select(tag => tag.ToString("X8"))) + ".");
    }
    foreach (var group in original.Groups)
    {
        var expected = group.Entries.Concat(retained.Where(tag => TagHashes.Package(tag) == group.PackageId)
            .Select(tag => TagHashes.Entry(tag))).ToHashSet();
        var actual = manifest.Groups.Single(current => current.PackageId == group.PackageId).Entries;
        Check(actual.SetEquals(expected), $"Manifest {manifestTag:X8} changed package {group.PackageId:X4}: extra "
            + string.Join(",", actual.Except(expected).Select(entry => entry.ToString("X"))) + ", missing "
            + string.Join(",", expected.Except(actual).Select(entry => entry.ToString("X"))) + ".");
    }
    foreach (var value in row.GetProperty("addedEntries").EnumerateArray())
    {
        uint tag = Convert.ToUInt32(value.GetString(), 16);
        Check(manifest.Lists(tag), "An authored root dependency is not in its native manifest.");
        reached.Add(tag);
    }
}
var direct = receipt.RootElement.GetProperty("manifestEdits").EnumerateArray()
    .Select(row => Convert.ToUInt32(row.GetProperty("added").GetString(), 16)).ToHashSet();
var globals = PackageManifest.Read(built.Read(0x81A2926F));
var shared = PackageManifest.Read(built.Read(0x80EE8CBD));
// The installed records each authored particle system draws with must load beside it.
var required = new Queue<uint>(project.AddedAssets.Where(asset => asset.Native?.Reference == 0x80806E28)
    .Select(asset => TagHashes.Make(asset.Native!.PackageId, asset.Native.EntryIndex!.Value))
    .SelectMany(system => new[] { 4, 8, 0x10, 0x14, 0x18, 0x1C }
        .Select(offset => BinaryPrimitives.ReadUInt32LittleEndian(built.Read(system).AsSpan(offset))))
    .Where(tag => catalog.TryGetEntry(tag, out _)));
Check(required.Count > 0, "No authored particle system names an installed dependency.");
var checkedDependencies = new HashSet<uint>();
while (required.TryDequeue(out uint tag))
{
    if (!checkedDependencies.Add(tag)) continue;
    Check(shared.Lists(tag), $"The particle manifest does not load retained dependency {tag:X8}.");
    Check(catalog.TryGetEntry(tag, out var entry), "A retained dependency has no installed entry.");
    foreach (uint child in PackageNativeDependencies.Read(catalog, tag, entry.Reference, entry.TypeInfo, catalog.Read(tag)))
        required.Enqueue(child);
}
// Every manifest the build wrote: the root manifests and each edited manifest.
var listings = generated.Concat(receipt.RootElement.GetProperty("manifestEdits").EnumerateArray()
    .Select(row => Convert.ToUInt32(row.GetProperty("manifest").GetString(), 16)).Distinct()
    .Select(tag => PackageManifest.Read(built.Read(tag)))).ToArray();
// A dependency loads in every manifest that lists its owner.
int Closed(uint reference, string kind, Func<uint, IEnumerable<uint>> dependencies)
{
    int checks = 0;
    foreach (uint tag in project.AddedAssets.Where(asset => asset.Native?.Reference == reference).Select(Tag))
        foreach (uint dependency in dependencies(tag).Where(dependency => dependency is not (0 or uint.MaxValue)))
            foreach (var listing in listings.Where(listing => listing.Lists(tag)))
            {
                Check(listing.Lists(dependency), $"{kind} {tag:X8} is resident without {dependency:X8}.");
                checks++;
            }
    return checks;
}
int schemaChecks = Closed(0x80809C36, "Config", tag => [U32(built.Read(tag), 0x44)]);
int bankChecks = Closed(0x80809802, "Sound event", tag => [U32(built.Read(tag), 0x14)]);
// The output holds only the built packages; installed configs resolve through the game catalog.
PackageEntry? Installed(uint tag) => built.TryGetEntry(tag, out var own) ? own : catalog.TryGetEntry(tag, out var stock) ? stock : null;
int configChecks = Closed(0x80809C0F, "Entity", tag => built.TryGetEntry(tag, out var entity)
    ? PackageNativeDependencies.Read(built, tag, entity.Reference, entity.TypeInfo, built.Read(tag), Installed) : []);
// Socket plugs come from the receipt; their sources are the items that socket them.
var socketEdits = receipt.RootElement.GetProperty("manifestEdits").EnumerateArray().Where(row =>
    row.GetProperty("manifest").GetString() == "81A2926F"
    && row.GetProperty("reason").GetString() == "socket dependency").ToArray();
var plugEntries = socketEdits.Select(row => Convert.ToUInt32(row.GetProperty("added").GetString(), 16)).ToHashSet();
var socketOwners = socketEdits.Select(row => Convert.ToUInt32(row.GetProperty("source").GetString(), 16)).ToHashSet();
var displays = ItemDisplays(built);
socketOwners.UnionWith(socketOwners.Where(displays.ContainsKey).Select(owner => displays[owner]).ToArray());
var authoredItems = project.AddedAssets.Where(asset => asset.Native?.Reference == 0x80807BEA).Select(Tag).ToArray();
var authoredPlugParts = project.AddedAssets.Where(asset => asset.Native?.Reference is 0x80807BEA or 0x80805CE1)
    .Select(Tag).ToHashSet();
Check(authoredItems.Length < 2 || plugEntries.Count > 0, "No authored socket plug is preloaded.");
Check(plugEntries.All(globals.Lists), "The custom socket plug definitions or displays are not preloaded.");
Check(plugEntries.IsSubsetOf(authoredPlugParts), "The socket dependency scope changed unrelated assets.");
Check(plugEntries.Where(displays.ContainsKey).All(item => plugEntries.Contains(displays[item])),
    "A socket plug is preloaded without its display.");
Check(!socketOwners.Except(plugEntries).Any(globals.Lists),
    "Socket preloading bypassed the class filter for the ordinary weapon item.");
foreach (var asset in project.AddedAssets.Where(asset => asset.Native is not null))
{
    uint tag = Tag(asset);
    bool requested = asset.Native!.Reference == 0x80809A8A || socketOwners.Contains(tag);
    Check(requested || direct.Contains(tag) || reached.Contains(tag), $"Asset {asset.Name} has no checked load path.");
}
Check(before == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(project.ManifestPath))), "Build changed the source project.");
Console.WriteLine($"root_manifests count={rows.Length} native_map=pass flags=preserved old_groups=preserved closure={reached.Count} config_schemas={schemaChecks} event_banks={bankChecks} entity_configs={configChecks} socket_preloads={plugEntries.Count} class_scope=pass source=unchanged");

static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
static uint Tag(AddedProjectAsset asset) =>
    TagHashes.Make(asset.Native!.PackageId, asset.Native.EntryIndex!.Value);

// Item definition and display index rows pair up by row number; each names its tag at +16.
static Dictionary<uint, uint> ItemDisplays(PackageCatalog catalog)
{
    byte[] definitions = catalog.Read(0x81327CCB), displays = catalog.Read(0x81613CF1);
    int[] definitionRows = Rows(definitions, 8, 0x80807BE8, 24), displayRows = Rows(displays, 8, 0x80805CDF, 24);
    Check(definitionRows.Length == displayRows.Length, "Item definition and display tables do not match.");
    var result = new Dictionary<uint, uint>();
    for (int index = 0; index < definitionRows.Length; index++)
        result.TryAdd(U32(definitions, definitionRows[index] + 16), U32(displays, displayRows[index] + 16));
    return result;
}

static int[] Rows(byte[] data, int field, uint type, int stride)
{
    long count = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(field));
    if (count == 0) return [];
    int header = checked((int)(field + 8 + BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(field + 8))));
    Check(U32(data, header - 4) == 0x80809FBD && BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(header)) == count
        && U32(data, header + 8) == type, "An item index table has an unexpected layout.");
    return Enumerable.Range(0, checked((int)count)).Select(index => header + 16 + index * stride).ToArray();
}
