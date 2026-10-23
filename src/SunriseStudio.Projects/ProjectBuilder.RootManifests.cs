using System.Buffers.Binary;
using SunriseStudio.Packages;

namespace SunriseStudio.Projects;

public static partial class ProjectBuilder
{
    private sealed record RootManifestInput(AddedProjectAsset Asset, byte[] Data);
    private sealed record RootManifestAsset(ushort PackageId, int EntryIndex, uint RootTag,
        uint ManifestTag, uint SourceManifest, AuthoredPackageEntry Entry, IReadOnlyList<uint> AddedTags,
        IReadOnlyList<uint> RetainedTags);

    /** Cloned roots retain their source dependencies and add their new asset closure. */
    private static IReadOnlyList<RootManifestAsset> PlanRootManifests(PackageCatalog catalog,
        IReadOnlyList<RootManifestInput> inputs, IReadOnlyList<AddedEntry> added,
        IReadOnlyDictionary<uint, byte[]> edited, IReadOnlyList<AddedActivity> activities)
    {
        var supplied = inputs.Where(input => input.Asset.Native!.Reference == PackageManifest.ClassId
                && input.Data.Length >= 16)
            .Select(input => BinaryPrimitives.ReadUInt32LittleEndian(input.Data.AsSpan(12))).ToHashSet();
        var roots = inputs.Where(input => (input.Asset.Native!.TypeInfo & 0xF000) == 0x2000
            && !supplied.Contains(StudioProject.NativeTag(input.Asset.Native))).ToArray();
        if (roots.Length == 0) return [];
        var authored = added.ToDictionary(asset => asset.Tag);
        var staged = inputs.ToDictionary(input => StudioProject.NativeTag(input.Asset.Native!), input => input.Asset.Native!);
        PackageEntry? Resolve(uint tag)
        {
            if (!staged.TryGetValue(tag, out var native))
                return catalog.TryGetEntry(tag, out var existing) ? existing : null;
            return new(tag, native.Reference, (int)((native.TypeInfo >> 9) & 127), (int)((native.TypeInfo >> 6) & 7),
                authored[tag].Data.Length, $"w64_sunrise_{native.PackageId:x4}_0", native.PackageId, native.EntryIndex!.Value)
                { TypeInfo = native.TypeInfo };
        }
        var edges = PointerEdges(added.Select(asset => (asset.Tag, asset.Data)), added).ToHashSet();
        foreach (var activity in activities)
            edges.Add((activity.Tag(), activity.MapRootTag));
        foreach (var asset in added)
            if (authored.ContainsKey(asset.Class) && asset.Class != asset.Tag)
                edges.Add((asset.Tag, asset.Class));
        var outgoing = edges.GroupBy(edge => edge.From).ToDictionary(group => group.Key,
            group => group.Select(edge => edge.To).ToArray());
        var next = inputs.GroupBy(input => input.Asset.Native!.PackageId).ToDictionary(group => group.Key,
            group => group.Max(input => input.Asset.Native!.EntryIndex!.Value) + 1);
        var maps = new Dictionary<string, IReadOnlyList<PackageRootManifest>>(StringComparer.OrdinalIgnoreCase);
        var output = new List<RootManifestAsset>();
        foreach (var input in roots)
        {
            var native = input.Asset.Native!;
            uint root = StudioProject.NativeTag(native);
            if (input.Asset.BaseTagHash is not { } sourceRoot || !catalog.TryGetEntry(sourceRoot, out var rootEntry)
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
            if (data.Length < 48 || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)) != manifest
                || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12)) != sourceRoot)
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
            int index = next[native.PackageId]++;
            if (index >= 8192) throw new InvalidDataException("The new package has no room for its root manifests.");
            uint newManifest = checked(0x80800000u + ((uint)native.PackageId << 13) + (uint)index);
            closure.Add(newManifest);
            foreach (var group in closure.GroupBy(tag => checked((ushort)((tag - 0x80800000u) >> 13))).OrderBy(group => group.Key))
                data = PackageManifest.AddEntries(data, group.Key, group.Select(tag => (int)(tag & 8191)));
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), newManifest);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), root);
            output.Add(new(native.PackageId, index, root, newManifest, manifest,
                new(PackageManifest.ClassId, manifestEntry.TypeInfo, data, input.Asset.Name + ".manifest"),
                closure.Where(tag => authored.ContainsKey(tag) || tag == newManifest).Order().ToArray(),
                closure.Where(tag => !authored.ContainsKey(tag) && tag != newManifest).Order().ToArray()));
        }
        return output;
    }
}
