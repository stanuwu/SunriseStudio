using SunriseStudio.Packages;
using SunriseStudio.Tags;
using SunriseStudio.Tags.Tft;
using SunriseStudio.Tags.Views;
using ManifestView = SunriseStudio.Tags.Generated.PackageManifest;

namespace SunriseStudio.Projects;

public static partial class ProjectBuilder
{
    /** One new entry, from an added asset or a `.tft` file; SourceRoot is the installed tag it was cloned from. */
    private sealed record RootManifestInput(uint Tag, uint Reference, uint TypeInfo, ushort PackageId, int EntryIndex, string Name,
        uint? SourceRoot, byte[] Data)
    {
        public static RootManifestInput Of(AddedProjectAsset asset, byte[] data) => new(TagHashes.Make(asset.Native!.PackageId, asset.Native!.EntryIndex!.Value),
            asset.Native!.Reference, asset.Native.TypeInfo, asset.Native.PackageId,
            asset.Native.EntryIndex ?? throw new InvalidDataException("Native entry has no index."), asset.Name, asset.BaseTagHash, data);

        public static RootManifestInput Of(TftCompiledTag tag) => new(tag.TagHash, tag.ClassId, tag.TypeInfo,
            TagHashes.Split(tag.TagHash).PackageId, TagHashes.Split(tag.TagHash).EntryIndex, tag.Name, tag.SourceTag, tag.Data);
    }
    private sealed record RootManifestAsset(ushort PackageId, int EntryIndex, uint RootTag,
        uint ManifestTag, uint SourceManifest, AuthoredPackageEntry Entry, IReadOnlyList<uint> AddedTags,
        IReadOnlyList<uint> RetainedTags);

    /** Cloned roots retain their source dependencies and add their new asset closure. */
    private static IReadOnlyList<RootManifestAsset> PlanRootManifests(PackageCatalog catalog,
        IReadOnlyList<RootManifestInput> inputs, IReadOnlyList<AddedEntry> added,
        IReadOnlyDictionary<uint, byte[]> edited)
    {
        var supplied = inputs.Where(input => input.Reference == PackageManifest.ClassId
                && TagView.FitsThrough<ManifestView>(input.Data, 0, "RootTag"))
            .Select(input => TagView.At<uint>(input.Data, TagView.FieldOffset<ManifestView>("RootTag"))).ToHashSet();
        var roots = inputs.Where(input => (input.TypeInfo & 0xF000) == 0x2000 && !supplied.Contains(input.Tag)).ToArray();
        if (roots.Length == 0) return [];
        var authored = added.ToDictionary(asset => asset.Tag);
        var staged = inputs.ToDictionary(input => input.Tag);
        PackageEntry? Resolve(uint tag)
        {
            if (!staged.TryGetValue(tag, out var input))
                return catalog.TryGetEntry(tag, out var existing) ? existing : null;
            return new(tag, input.Reference, (int)((input.TypeInfo >> 9) & 127), (int)((input.TypeInfo >> 6) & 7),
                input.Data.Length, $"w64_sunrise_{input.PackageId:x4}_0", input.PackageId, input.EntryIndex) { TypeInfo = input.TypeInfo };
        }
        var edges = PointerEdges(added.Select(asset => (asset.Tag, asset.Data)), added).ToHashSet();
        foreach (var asset in added)
            if (authored.ContainsKey(asset.Class) && asset.Class != asset.Tag)
                edges.Add((asset.Tag, asset.Class));
        var outgoing = edges.GroupBy(edge => edge.From).ToDictionary(group => group.Key,
            group => group.Select(edge => edge.To).ToArray());
        var next = inputs.GroupBy(input => input.PackageId).ToDictionary(group => group.Key, group => group.Max(input => input.EntryIndex) + 1);
        var maps = new Dictionary<string, IReadOnlyList<PackageRootManifest>>(StringComparer.OrdinalIgnoreCase);
        var output = new List<RootManifestAsset>();
        foreach (var input in roots)
        {
            uint root = input.Tag;
            if (input.SourceRoot is not { } sourceRoot || !catalog.TryGetEntry(sourceRoot, out var rootEntry)
                || (rootEntry.TypeInfo & 0xF000) != 0x2000)
                throw new NotSupportedException($"Manifest root {root:X8} needs a checked source root.");
            string path = Path.Combine(catalog.PackageDirectory, rootEntry.PackageName + ".pkg");
            if (!maps.TryGetValue(path, out var pairs)) maps.Add(path, pairs = PackageWriter.ReadRootManifests(path));
            uint manifest = pairs.SingleOrDefault(pair => pair.RootTag == sourceRoot)?.ManifestTag
                ?? throw new InvalidDataException($"Source root {sourceRoot:X8} has no native manifest mapping.");
            if (!catalog.TryGetEntry(manifest, out var manifestEntry) || manifestEntry.Reference != PackageManifest.ClassId)
                throw new InvalidDataException("The source root manifest has the wrong class.");
            byte[] data = catalog.ReadOriginal(manifest);
            _ = PackageManifest.Read(data);
            if (!TagView.Fits<ManifestView>(data, 0) || TagView.At<ManifestView>(data).SelfTag != manifest
                || TagView.At<ManifestView>(data).RootTag != sourceRoot)
                throw new InvalidDataException("The source manifest identity does not match its root map.");
            var closure = new HashSet<uint>();
            var pending = new Queue<uint>(); pending.Enqueue(root);
            while (pending.TryDequeue(out uint current))
            {
                if (closure.Contains(current)) continue;
                var entry = Resolve(current) ?? throw new InvalidDataException($"Root dependency {current:X8} is missing.");
                if (!PackageNativeDependencies.IsResident(entry.TypeInfo)) continue;
                closure.Add(current);
                if (outgoing.TryGetValue(current, out var links))
                    foreach (uint link in links) pending.Enqueue(link);
                byte[] bytes = authored.TryGetValue(current, out var own) ? own.Data
                    : edited.TryGetValue(current, out var changed) ? changed : catalog.Read(current);
                foreach (uint dependency in PackageNativeDependencies.Read(catalog, current, entry.Reference,
                    entry.TypeInfo, bytes, Resolve)) pending.Enqueue(dependency);
            }
            int index = next[input.PackageId]++;
            if (index >= TagHashes.EntriesPerPackage) throw new InvalidDataException("The new package has no room for its root manifests.");
            uint newManifest = TagHashes.Make(input.PackageId, index);
            closure.Add(newManifest);
            foreach (var group in closure.GroupBy(tag => checked((ushort)TagHashes.Package(tag))).OrderBy(group => group.Key))
                data = PackageManifest.AddEntries(data, group.Key, group.Select(TagHashes.Entry));
            ref var identity = ref TagView.Edit<ManifestView>(data);
            identity.SelfTag = newManifest;
            identity.RootTag = root;
            output.Add(new(input.PackageId, index, root, newManifest, manifest,
                new(PackageManifest.ClassId, manifestEntry.TypeInfo, data, input.Name + ".manifest"),
                closure.Where(tag => authored.ContainsKey(tag) || tag == newManifest).Order().ToArray(),
                closure.Where(tag => !authored.ContainsKey(tag) && tag != newManifest).Order().ToArray()));
        }
        return output;
    }
}
