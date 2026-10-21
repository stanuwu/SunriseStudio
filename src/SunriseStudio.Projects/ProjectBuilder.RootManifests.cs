using System.Buffers.Binary;
using SunriseStudio.Packages;
using SunriseStudio.Tags;
using SunriseStudio.Tags.Tft;
using SunriseStudio.Tags.Views;
using ManifestView = SunriseStudio.Tags.Generated.PackageManifest;

namespace SunriseStudio.Projects;

public static partial class ProjectBuilder
{
    /** One new entry, from an added asset or a `.tft` file; SourceRoot is the installed tag it was cloned from. */
    private sealed record RootManifestInput(uint Tag, uint Reference, uint TypeInfo, ushort PackageId, int EntryIndex, string? Name,
        uint? SourceRoot, byte[] Data)
    {
        public static RootManifestInput Of(AddedProjectAsset asset, byte[] data) => new(TagHashes.Make(asset.Native!.PackageId, asset.Native!.EntryIndex!.Value),
            asset.Native!.Reference, asset.Native.TypeInfo, asset.Native.PackageId,
            asset.Native.EntryIndex ?? throw new InvalidDataException("Native entry has no index."), asset.Name, asset.BaseTagHash, data);

        public static RootManifestInput Of(TftCompiledTag tag) => new(tag.TagHash, tag.ClassId, tag.TypeInfo,
            TagHashes.Split(tag.TagHash).PackageId, TagHashes.Split(tag.TagHash).EntryIndex, tag.GameName, tag.SourceTag, tag.Data);
    }
    private sealed record RootManifestAsset(ushort PackageId, int EntryIndex, uint RootTag,
        uint ManifestTag, uint SourceManifest, AuthoredPackageEntry Entry, IReadOnlyList<uint> AddedTags,
        IReadOnlyList<uint> RetainedTags);

    // A larger tag is bulk data, not a place tag references are read from.
    private const int NamedTagLimit = 4 << 20;

    // A bubble definition is loaded on demand with its own manifest. The map root reads every
    // bubble parent at load, so a closure still runs through the other root classes.
    private static bool EndsClosure(PackageEntry entry) => entry.Reference == Tags.Generated.SBubbleDefinition.Class;

    /**
     * A cloned root keeps the source's entries that its own bytes still reach or that the walk
     * cannot see, and adds its new closure. A closure stops at every other bubble definition, as
     * an installed manifest does: a bubble's content must load with the bubble, not with the map.
     */
    private static IReadOnlyList<RootManifestAsset> PlanRootManifests(PackageCatalog catalog,
        IReadOnlyList<RootManifestInput> inputs, IReadOnlyList<AddedEntry> added,
        IReadOnlyDictionary<uint, byte[]> edited, IReadOnlyList<AddedActivity> activities)
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
        foreach (var activity in activities)
            edges.Add((activity.Tag(), activity.MapRootTag));
        foreach (var asset in added)
            if (authored.ContainsKey(asset.Class) && asset.Class != asset.Tag)
                edges.Add((asset.Tag, asset.Class));
        var outgoing = edges.GroupBy(edge => edge.From).ToDictionary(group => group.Key,
            group => group.Select(edge => edge.To).ToArray());
        var next = inputs.GroupBy(input => input.PackageId).ToDictionary(group => group.Key, group => group.Max(input => input.EntryIndex) + 1);
        var maps = new Dictionary<string, IReadOnlyList<PackageRootManifest>>(StringComparer.OrdinalIgnoreCase);
        var output = new List<RootManifestAsset>();
        // The tags a tag's bytes name, by hash or by wide hash; an installed tag's are shared by every clone.
        var names = new Dictionary<uint, uint[]>();
        uint[] Names(uint tag, byte[]? own)
        {
            if (names.TryGetValue(tag, out var known)) return known;
            var found = new HashSet<uint>();
            byte[] bytes = own ?? (catalog.TryGetEntry(tag, out var entry) && entry.Size <= NamedTagLimit ? catalog.ReadOriginal(tag) : []);
            for (int i = 0; i + 4 <= bytes.Length; i += 4)
                if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) is uint value && value != tag
                    && (authored.ContainsKey(value) || catalog.TryGetEntry(value, out _))) found.Add(value);
            for (int i = 0; i + 8 <= bytes.Length; i += 8)
                if (catalog.TryResolve64(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(i)), out var target) && target.TagHash != tag)
                    found.Add(target.TagHash);
            return names[tag] = [.. found];
        }
        // The installed entries of a source manifest that a root reaches through such names.
        HashSet<uint> Reach(uint start, PackageManifest listed)
        {
            var reached = new HashSet<uint>();
            var seen = new HashSet<uint>();
            var queue = new Queue<uint>(); queue.Enqueue(start);
            while (queue.TryDequeue(out uint current))
            {
                if (!seen.Add(current)) continue;
                if (authored.TryGetValue(current, out var asset))
                {
                    if (current != start && asset.Class == Tags.Generated.SBubbleDefinition.Class) continue;
                    if (outgoing.TryGetValue(current, out var links)) foreach (uint link in links) queue.Enqueue(link);
                    foreach (uint name in Names(current, asset.Data)) queue.Enqueue(name);
                    continue;
                }
                if (current != start && !listed.Lists(current)) continue;
                if (!catalog.TryGetEntry(current, out var entry) || (current != start && EndsClosure(entry))) continue;
                reached.Add(current);
                foreach (uint name in Names(current, null)) queue.Enqueue(name);
            }
            return reached;
        }
        var sourceReaches = new Dictionary<uint, HashSet<uint>>();
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
            var listed = PackageManifest.Read(data);
            if (!TagView.Fits<ManifestView>(data, 0) || TagView.At<ManifestView>(data).SelfTag != manifest
                || TagView.At<ManifestView>(data).RootTag != sourceRoot)
                throw new InvalidDataException("The source manifest identity does not match its root map.");
            var closure = new HashSet<uint>();
            var pending = new Queue<uint>(); pending.Enqueue(root);
            while (pending.TryDequeue(out uint current))
            {
                if (closure.Contains(current)) continue;
                var entry = Resolve(current) ?? throw new InvalidDataException($"Root dependency {current:X8} is missing.");
                if (!PackageNativeDependencies.IsResident(entry.TypeInfo) || (current != root && EndsClosure(entry))) continue;
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
            // An entry the source reached and the clone no longer does would still load its tag. The
            // source root and its manifest are two of them, and the root would load all it lists.
            if (!sourceReaches.TryGetValue(sourceRoot, out var sourceReach)) sourceReaches.Add(sourceRoot, sourceReach = Reach(sourceRoot, listed));
            var kept = Reach(root, listed);
            foreach (var group in sourceReach.Append(manifest).Where(tag => !kept.Contains(tag) && !closure.Contains(tag))
                .GroupBy(tag => checked((ushort)TagHashes.Package(tag))))
                data = PackageManifest.RemoveEntries(data, group.Key, group.Select(TagHashes.Entry));
            ref var identity = ref TagView.Edit<ManifestView>(data);
            identity.SelfTag = newManifest;
            identity.RootTag = root;
            output.Add(new(input.PackageId, index, root, newManifest, manifest,
                new(PackageManifest.ClassId, manifestEntry.TypeInfo, data, input.Name is null ? null : input.Name + ".manifest"),
                closure.Where(tag => authored.ContainsKey(tag) || tag == newManifest).Order().ToArray(),
                closure.Where(tag => !authored.ContainsKey(tag) && tag != newManifest).Order().ToArray()));
        }
        return output;
    }
}
