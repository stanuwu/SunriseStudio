using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags;
using SunriseStudio.Tags.Tft;
using SunriseStudio.World;
using SunriseStudio.World.Sdk;

namespace SunriseStudio.Projects;

/** Builds are disposable local outputs; the manifest remains a delta-only source project. */
public static partial class ProjectBuilder
{
    /**
     * Every edited or derived asset is verified before any package output is written.
     * Pass invalidate false only when the caller cleared the read cache itself just before.
     */
    public static ProjectBuildResult Build(StudioProject project, PackageCatalog catalog, string? outputDirectory = null,
        bool invalidate = true, RegistryIndex? tagSchema = null)
    {
        if (invalidate) catalog.InvalidateReadCache();
        project.ValidateWideHashAliases(catalog);
        // Derived values are rebuilt here from the installed originals, never read from an older build.
        var objects = project.CompileObjectEdits(catalog, build: true);
        var config = project.CompileConfigBindings(catalog);
        var tags = CompileContent(project, catalog, tagSchema, project.AddedAssets.Where(x => x.Native is not null)
            .Select(x => x.Native!.PackageId).Concat(objects.GeneratedAssets.Select(x => x.Native.PackageId)));
        if (project.Changes.Count == 0 && objects.Assets.Count == 0 && config.Assets.Count == 0
            && project.AddedAssets.Count == 0 && objects.GeneratedAssets.Count == 0 && tags.Count == 0)
            throw new InvalidOperationException("The project has no changes to build.");
        var unencoded = project.AddedAssets.Where(x => x.Native is null).ToArray();
        if (project.Changes.Count == 0 && objects.Assets.Count == 0 && config.Assets.Count == 0
            && objects.GeneratedAssets.Count == 0 && project.AddedAssets.All(x => x.Native is null) && tags.Count == 0)
            throw new InvalidOperationException("The project has no native changes to build.");
        var edited = new Dictionary<uint, byte[]>();
        var addedTags = project.AddedAssets.Where(asset => asset.Native is not null)
            .Select(asset => TagHashes.Make(asset.Native!.PackageId, asset.Native!.EntryIndex!.Value)).ToHashSet();
        foreach (uint tag in project.Changes.Select(change => change.TagHash).Union(objects.Assets.Keys)
            .Union(config.Assets.Keys).Where(tag => !addedTags.Contains(tag)).Order())
        {
            if (!catalog.TryGetEntry(tag, out _))
                throw new InvalidDataException($"Original asset {tag:X8} is missing.");
            byte[] data = config.Assets.TryGetValue(tag, out var configured) ? configured
                : objects.Assets.TryGetValue(tag, out var compiled) ? compiled.ToArray() : project.ReadAsset(tag, catalog);
            // An entry equal to its installed original needs no patch.
            if (!data.AsSpan().SequenceEqual(catalog.ReadOriginal(tag))) edited[tag] = data;
        }
        var added = project.AddedAssets.Where(x => x.Native is not null)
            .Select(x => (Asset: x, Data: config.Assets.TryGetValue(TagHashes.Make(x.Native!.PackageId, x.Native!.EntryIndex!.Value), out var configured)
                ? configured : project.ReadAddedAsset(x.Id, catalog))).ToArray();
        var reserved = added.Select(item => item.Asset.Native!.PackageId).ToHashSet();
        if (objects.GeneratedAssets.Any(asset => reserved.Contains(asset.Native.PackageId)))
            throw new InvalidDataException("Generated packages overlap a project asset package.");
        foreach (var item in added)
            if (catalog.ContainsInstalledPackage(item.Asset.Native!.PackageId))
                throw new InvalidOperationException($"Package id {item.Asset.Native.PackageId:X4} is already installed.");
            else if (!PackageWriter.IsNewPackageId(item.Asset.Native.PackageId))
                throw new InvalidOperationException($"{item.Asset.Name} uses package id {item.Asset.Native.PackageId:X4}. "
                    + $"New packages need an id from {PackageWriter.NewPackageIdRange}.");
        string buildRoot = Path.GetFullPath(outputDirectory ?? Path.Combine(project.DirectoryPath, "build",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]));
        if (ProjectPaths.IsWithin(buildRoot, catalog.GameRoot))
            throw new IOException("Build output cannot be inside the original installation.");
        ProjectPaths.RejectLinks(buildRoot);
        if (Directory.Exists(buildRoot) && Directory.EnumerateFileSystemEntries(buildRoot).Any())
            throw new IOException("Build output must be an empty folder.");
        var generated = objects.GeneratedAssets;
        var addedEntries = added.Select(item => new AddedEntry(item.Asset.Id, TagHashes.Make(item.Asset.Native!.PackageId, item.Asset.Native!.EntryIndex!.Value),
            item.Asset.Native!.Reference, item.Data, item.Asset.Native!.WideHashes ?? WideHashAliases.Empty,
            item.Asset.Native!.TypeInfo)).ToArray();
        // Tags compiled from .tft files join manifests and dependency records like added assets.
        addedEntries = [.. addedEntries, .. tags.Select(tag => new AddedEntry(TagFileId(tag.Name), tag.TagHash, tag.ClassId, tag.Data,
            tag.WideHash == 0 ? WideHashAliases.Empty : new([tag.WideHash]), tag.TypeInfo))];
        var rootManifests = PlanRootManifests(catalog, [.. added.Select(item => RootManifestInput.Of(item.Asset, item.Data)),
            .. tags.Select(RootManifestInput.Of)], addedEntries, edited);
        var manifests = PlanManifests(catalog, edited, addedEntries);
        foreach (var (tag, data) in manifests.Data) edited.Add(tag, data);
        var replacements = new Dictionary<string, Dictionary<int, byte[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tag, data) in edited)
        {
            catalog.TryGetEntry(tag, out var entry);
            string source = Path.Combine(catalog.PackageDirectory, entry!.PackageName + ".pkg");
            if (!replacements.TryGetValue(source, out var group)) replacements.Add(source, group = []);
            group.Add(entry.EntryIndex, data);
        }
        var dependencies = PlanDependencies(catalog, edited, generated, addedEntries);
        var changedMisc = dependencies.Misc.Where(pair => !pair.Value.AsSpan().SequenceEqual(PackageWriter.ReadMisc(pair.Key)))
            .ToDictionary(StringComparer.OrdinalIgnoreCase);
        // Checked before any folder is made, so a no-op build leaves nothing behind.
        if (replacements.Count == 0 && changedMisc.Count == 0 && added.Length == 0 && generated.Count == 0 && tags.Count == 0)
            throw new InvalidOperationException("Every edited entry equals its installed original; nothing was built.");
        string packageRoot = Path.Combine(buildRoot, "packages");
        Directory.CreateDirectory(packageRoot);
        var artifacts = new List<PackageBuildArtifact>();
        var builtAssets = new List<BuiltProjectAsset>();
        var replacedPackages = new List<string>();
        var inheritedPackages = new List<string>();
        foreach (string source in replacements.Keys.Union(changedMisc.Keys, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal))
        {
            var edits = replacements.GetValueOrDefault(source) ?? [];
            byte[]? misc = changedMisc.GetValueOrDefault(source);
            string name = Path.GetFileNameWithoutExtension(source);
            if (name.Contains("_unp1_", StringComparison.OrdinalIgnoreCase))
            {
                if (misc is not null) throw new NotSupportedException($"{name} cannot carry dependency edits.");
                string replacement = Path.Combine(packageRoot, name + ".pkg");
                artifacts.Add(PackageWriter.WriteReplacement(source, replacement, edits,
                    catalog.ReadOriginal, catalog.GameRoot));
                replacedPackages.Add(Path.GetFileName(replacement));
                continue;
            }
            int separator = name.LastIndexOf('_');
            if (separator < 0 || !ushort.TryParse(name[(separator + 1)..], out ushort patch))
                throw new InvalidDataException("Package family has no patch suffix.");
            string output = Path.Combine(packageRoot, name[..separator] + $"_{patch + 1}.pkg");
            artifacts.Add(PackageWriter.WritePatch(source, output, edits, catalog.GameRoot, misc));
            inheritedPackages.AddRange(PackageWriter.GetRequiredPredecessors(source));
        }
        string? profile = null;
        if (added.Length > 0 || generated.Count > 0 || tags.Count > 0)
            profile = PackageProfile.Newest(catalog.PackageDirectory)
                ?? throw new NotSupportedException("No pre-BL package header profile is installed.");
        foreach (var group in added.GroupBy(x => x.Asset.Native!.PackageId).OrderBy(x => x.Key))
        {
            var rootAssets = rootManifests.Where(asset => asset.PackageId == group.Key).ToArray();
            int count = Math.Max(group.Max(x => x.Asset.Native!.EntryIndex ?? throw new InvalidDataException("Native entry has no index.")) + 1,
                rootAssets.Select(asset => asset.EntryIndex + 1).DefaultIfEmpty(0).Max());
            var entries = Enumerable.Range(0, count).Select(_ => new AuthoredPackageEntry(0, 0, [])).ToArray();
            string output = Path.Combine(packageRoot, $"w64_sunrise_{group.Key:x4}_0.pkg");
            foreach (var item in group)
            {
                var native = item.Asset.Native!;
                int index = native.EntryIndex!.Value;
                entries[index] = new(native.Reference, native.TypeInfo, item.Data, native.Name)
                { WideHashes = native.WideHashes ?? WideHashAliases.Empty };
                uint tag = TagHashes.Make(native.PackageId, index);
                builtAssets.Add(new(item.Asset.Id, tag, output));
            }
            foreach (var asset in rootAssets) entries[asset.EntryIndex] = asset.Entry;
            artifacts.Add(PackageWriter.WriteNew(output, group.Key, entries, profile, catalog.GameRoot));
        }
        var generatedAssets = new List<BuiltProjectAsset>();
        foreach (var group in generated.GroupBy(asset => asset.Native.PackageId).OrderBy(group => group.Key))
        {
            int count = group.Max(asset => asset.Native.EntryIndex!.Value) + 1;
            var entries = Enumerable.Range(0, count).Select(_ => new AuthoredPackageEntry(0, 0, [])).ToArray();
            string output = Path.Combine(packageRoot, $"w64_sunrise_{group.Key:x4}_0.pkg");
            foreach (var asset in group)
            {
                var native = asset.Native;
                entries[native.EntryIndex!.Value] = new(native.Reference, native.TypeInfo, asset.Data, native.Name);
                generatedAssets.Add(new(asset.Id, asset.TagHash, output));
            }
            artifacts.Add(PackageWriter.WriteNew(output, group.Key, entries, profile, catalog.GameRoot));
        }
        var builtTags = new List<BuiltProjectAsset>();
        foreach (var group in tags.GroupBy(tag => TagHashes.Split(tag.TagHash).PackageId).OrderBy(group => group.Key))
        {
            var rootAssets = rootManifests.Where(asset => asset.PackageId == group.Key).ToArray();
            var entries = Enumerable.Range(0, Math.Max(group.Max(tag => TagHashes.Split(tag.TagHash).EntryIndex) + 1,
                    rootAssets.Select(asset => asset.EntryIndex + 1).DefaultIfEmpty(0).Max()))
                .Select(_ => new AuthoredPackageEntry(0, 0, [])).ToArray();
            string output = Path.Combine(packageRoot, $"w64_sunrise_{group.Key:x4}_0.pkg");
            foreach (var tag in group)
            {
                entries[TagHashes.Split(tag.TagHash).EntryIndex] = new(tag.ClassId, tag.TypeInfo, tag.Data, tag.GameName ?? tag.Name)
                { WideHashes = tag.WideHash == 0 ? WideHashAliases.Empty : new([tag.WideHash]) };
                builtTags.Add(new(tag.Name, tag.TagHash, output));
            }
            foreach (var asset in rootAssets) entries[asset.EntryIndex] = asset.Entry;
            artifacts.Add(PackageWriter.WriteNew(output, group.Key, entries, profile, catalog.GameRoot));
        }
        var addedData = new Dictionary<uint, byte[]>();
        foreach (var tag in tags) addedData[tag.TagHash] = tag.Data;
        foreach (var item in added) addedData[TagHashes.Make(item.Asset.Native!.PackageId, item.Asset.Native!.EntryIndex!.Value)] = item.Data;
        foreach (var asset in rootManifests)
            addedData[TagHashes.Make(asset.PackageId, asset.EntryIndex)] = asset.Entry.Data;
        foreach (var asset in generated) addedData[asset.TagHash] = asset.Data;
        var sidecars = new MergeSidecarRequest(catalog, buildRoot, artifacts, edited, addedData,
            manifests.Data.Keys.ToHashSet());
        WriteMergeSidecars(sidecars);
        string receipt = Path.Combine(buildRoot, "build-receipt.json");
        ProjectPaths.AtomicWrite(receipt, JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1,
            project = project.Name,
            packages = artifacts,
            addedAssets = builtAssets,
            generatedAssets,
            tagFiles = builtTags,
            configBindings = config.Sites,
            objectEdits = objects.Edits.Select(x => new
            {
                target = x.Edit.Key,
                name = x.Edit.Name,
                change = x.Edit.Summary(),
                tags = x.Tags.Select(tag => tag.ToString("X8")).ToArray()
            }).ToArray(),
            objectEditNotes = objects.Notes,
            sourceAssetsNotBuilt = unencoded.Select(x => new { x.Id, x.Name }).ToArray(),
            dependencyEdits = dependencies.Rows,
            dependencyTablesSkipped = dependencies.Skipped,
            addedAssetsInNoRecord = dependencies.Unlisted,
            manifestEdits = manifests.Rows,
            rootManifests = rootManifests.Select(asset => new { root = asset.RootTag.ToString("X8"),
                manifest = asset.ManifestTag.ToString("X8"), sourceManifest = asset.SourceManifest.ToString("X8"),
                addedEntries = asset.AddedTags.Select(tag => tag.ToString("X8")).ToArray(),
                retainedDependencies = asset.RetainedTags.Select(tag => tag.ToString("X8")).ToArray() }),
            requiredPredecessors = inheritedPackages
                .Select(Path.GetFileName).Distinct().Order(StringComparer.Ordinal).ToArray(),
            replacesInstalledFiles = replacedPackages,
            mergeSidecars = sidecars.Written,
            trust = "Sunrise custom-package trust is required. Stock signatures cannot be regenerated."
        }, ProjectJson.Options));
        var newEntries = added.Select(item => new BuiltEntry(item.Asset.Native!.PackageId, item.Asset.Native.EntryIndex!.Value,
                item.Asset.Native.Reference, item.Asset.Native.TypeInfo, item.Data))
            .Concat(rootManifests.Select(asset => new BuiltEntry(asset.PackageId, asset.EntryIndex, asset.Entry.Reference, asset.Entry.TypeInfo,
                asset.Entry.Data)))
            .Concat(generated.Select(asset => new BuiltEntry(asset.Native.PackageId, asset.Native.EntryIndex!.Value, asset.Native.Reference,
                asset.Native.TypeInfo, asset.Data)))
            .Concat(tags.Select(tag => new BuiltEntry(TagHashes.Split(tag.TagHash).PackageId, TagHashes.Split(tag.TagHash).EntryIndex,
                tag.ClassId, tag.TypeInfo, tag.Data)))
            .ToArray();
        var contents = new ProjectBuildContents(edited, newEntries, changedMisc.ToDictionary(pair => Path.GetFileName(pair.Key), pair => pair.Value,
            StringComparer.OrdinalIgnoreCase));
        var own = newEntries.Select(entry => entry.PackageId).Where(id => !catalog.ContainsInstalledPackage(id)).ToHashSet();
        var referenceMap = ReferenceMapBuilder.Build(project.Mod?.Id ?? project.Name, own, contents, artifacts.Select(x => (x.Path, x.PackageId)).ToArray(),
            catalog, tagSchema ?? StudioRegistry.Index);
        File.WriteAllBytes(Path.Combine(buildRoot, ReferenceMap.FileName), referenceMap.Write());
        string? sdkPack = WriteActivitySdk(project, catalog, tagSchema ?? StudioRegistry.Index, tags, buildRoot);
        return new(buildRoot, artifacts.Select(x => x.Path).ToArray(), builtAssets, receipt, objects.Edits)
        {
            ConfigBindings = config.Sites, Notes = objects.Notes, TagFiles = builtTags, Contents = contents, ReferenceMap = referenceMap,
            ActivitySdkPack = sdkPack,
        };
    }

    /**
     * Writes the activity SDK rows of the project's destinations; the server builds their roster from them.
     * @return The pack path, or null when the project has no destination.
     */
    private static string? WriteActivitySdk(StudioProject project, PackageCatalog catalog, RegistryIndex schema,
        IReadOnlyList<TftCompiledTag> tags, string buildRoot)
    {
        if (!Directory.Exists(project.ContentDirectory)
            || WorldTemplate.ProjectDestinations(project.ContentDirectory, schema).Count == 0) return null;
        string installed = Path.Combine(catalog.GameRoot, "bin", "x64", "Sunrise", ModActivitySdk.FileName);
        if (!File.Exists(installed))
            throw new InvalidOperationException("Sunrise has not generated its activity SDK yet. Start the game once, then build again.");
        byte[]? pack = ModActivitySdk.Build(project.ContentDirectory, schema,
            tags.ToDictionary(tag => tag.Name, tag => tag.TagHash, StringComparer.Ordinal), installed);
        if (pack is null) return null;
        string path = Path.Combine(buildRoot, ModActivitySdk.FileName);
        File.WriteAllBytes(path, pack);
        return path;
    }

    private static string TagFileId(string name) => "tft:" + name;

    /**
     * Compiles the project's `content/` tree, for a build and for the editor. The allocation table is
     * saved as soon as hashes are handed out, so a failed build still keeps them stable.
     * @param reserved Package ids the project's other new entries use.
     */
    public static IReadOnlyList<TftCompiledTag> CompileContent(StudioProject project, PackageCatalog catalog, RegistryIndex? schema,
        IEnumerable<ushort> reserved)
    {
        if (!Directory.Exists(project.ContentDirectory)) return [];
        ProjectPaths.RejectLinks(project.ContentDirectory);
        var taken = reserved.ToHashSet();
        bool Taken(ushort id) => taken.Contains(id) || catalog.ContainsInstalledPackage(id);
        var allocator = project.OpenTagAllocator(Taken);
        allocator.Grow = () => allocator.NextPackage(project.Mod?.Id ?? project.Name, Taken);
        foreach (ushort id in allocator.Packages.Where(Taken))
            throw new InvalidOperationException($"Package id {id:X4} in {StudioProject.AllocationFileName} is installed or used by another project entry.");
        schema ??= StudioRegistry.Index;
        // Every compiled tag gets a wide hash, so another tag can name it the way the game names bubbles.
        string modId = project.Mod?.Id ?? project.Name;
        var claimed = project.AddedAssets.SelectMany(x => x.Native?.WideHashes ?? WideHashAliases.Empty).ToHashSet();
        var minted = new Dictionary<string, ulong>(StringComparer.Ordinal);
        ulong WideHash(string name)
        {
            if (minted.TryGetValue(name, out ulong known)) return known;
            ulong value = TagAllocator.WideHash(modId, name, x => catalog.HasInstalledWideHashClaim(x) || claimed.Contains(x));
            claimed.Add(value);
            return minted[name] = value;
        }
        var compiler = new TftCompiler(project.ContentDirectory, allocator, schema, schema.Registry.BuildId, WideHash);
        if (compiler.Files().Count == 0) return [];
        var tags = compiler.CompileAll();
        project.SaveTagAllocator(allocator);
        return tags;
    }

    /** One new entry added to one installed dependency record; Source is the entry that brought it in. */
    public sealed record DependencyEdit(string Package, int Record, string Source, string Added);

    /** Each manifest addition records the reference or socket that requires it. */
    public sealed record ManifestEdit(string Manifest, string Package, string Source, string Added,
        string Reason = "reference");

    private sealed record AddedEntry(string Id, uint Tag, uint Class, byte[] Data, WideHashAliases WideHashes,
        uint TypeInfo);

    /** One `.merge` file written next to its package. */
    public sealed record MergeSidecarArtifact(string Path, string PackagePath, string Sha256);

    /**
     * What a build hands the merge sidecar writer, after every package is written.
     * Edited: final installed edits, manifests included. Added: every entry of a new package.
     */
    private sealed record MergeSidecarRequest(PackageCatalog Catalog, string BuildRoot,
        IReadOnlyList<PackageBuildArtifact> Artifacts, IReadOnlyDictionary<uint, byte[]> Edited,
        IReadOnlyDictionary<uint, byte[]> Added, IReadOnlySet<uint> Manifests)
    {
        public List<MergeSidecarArtifact> Written { get; } = [];
    }

    /** Writes the merge sidecars and lists them in request.Written. */
    static partial void WriteMergeSidecars(MergeSidecarRequest request);

    private sealed record ManifestPlan(Dictionary<uint, byte[]> Data, List<ManifestEdit> Rows);

    // The manifests that list each installed tag; a full manifest scan takes most of a minute.
    private static readonly ConditionalWeakTable<PackageCatalog, Dictionary<uint, uint[]>> ManifestsListing = new();

    /**
     * A tag in a manifest loads with the manifest's root. An added entry joins every manifest that lists an
     * entry pointing at it, when that manifest already lists entries of the added entry's class.
     */
    private static ManifestPlan PlanManifests(PackageCatalog catalog, IReadOnlyDictionary<uint, byte[]> edited,
        IReadOnlyList<AddedEntry> added)
    {
        var plan = new ManifestPlan([], []);
        var resident = added.Where(asset => PackageNativeDependencies.IsResident(asset.TypeInfo)).ToArray();
        var edges = PointerEdges(edited.Select(pair => (pair.Key, pair.Value))
            .Concat(resident.Select(asset => (asset.Tag, asset.Data))), resident);
        var nativeEdges = NativeDependencyEdges(catalog, edited, added);
        var socketDependencies = PlanSocketDependencies(catalog, edited, added);
        var sources = edges.Concat(nativeEdges).Select(edge => edge.From).Where(edited.ContainsKey).ToHashSet();
        if (sources.Count == 0 && socketDependencies.Count == 0) return plan;
        var cache = ManifestsListing.GetOrCreateValue(catalog);
        lock (cache)
        {
            if (sources.Any(tag => !cache.ContainsKey(tag)))
            {
                var found = sources.ToDictionary(tag => tag, _ => new List<uint>());
                foreach (var entry in catalog.Entries.Where(entry => entry.Reference == PackageManifest.ClassId
                    && catalog.ContainsInstalledPackage(entry.PackageId)))
                {
                    var manifest = PackageManifest.Read(catalog.ReadOriginal(entry.TagHash));
                    foreach (uint tag in sources.Where(manifest.Lists)) found[tag].Add(entry.TagHash);
                }
                foreach (var (tag, list) in found) cache[tag] = list.ToArray();
            }
        }
        var classes = added.ToDictionary(asset => asset.Tag, asset => asset.Class);
        foreach (uint tag in nativeEdges.Select(edge => edge.To).Where(tag => !classes.ContainsKey(tag)).Distinct())
            classes.Add(tag, catalog.TryGetEntry(tag, out var target) ? target.Reference
                : throw new InvalidDataException($"Native dependency {tag:X8} has no package entry."));
        var closureEdges = edges.Concat(nativeEdges).Distinct().ToArray();
        var candidates = sources.SelectMany(tag => cache[tag]).Concat(socketDependencies.Count == 0
            ? [] : new[] { SocketDependencyManifest }).Distinct().Order();
        foreach (uint manifestTag in candidates)
        {
            if (edited.ContainsKey(manifestTag))
                throw new NotSupportedException($"Manifest {manifestTag:X8} is edited by the project and needs new entries too.");
            catalog.TryGetEntry(manifestTag, out var manifestEntry);
            byte[] data = catalog.ReadOriginal(manifestTag);
            var manifest = PackageManifest.Read(data);
            var listedClasses = new Dictionary<uint, bool>();
            bool ListsClass(uint cls)
            {
                if (listedClasses.TryGetValue(cls, out bool known)) return known;
                return listedClasses[cls] = manifest.Groups.Any(group => group.Entries.Any(entry =>
                    catalog.TryGetEntry(TagHashes.Make(group.PackageId, entry), out var listed)
                    && listed.Reference == cls));
            }
            var joined = manifestTag == SocketDependencyManifest
                ? new Dictionary<uint, uint>(socketDependencies) : new Dictionary<uint, uint>();
            var socketClosure = joined.Keys.ToHashSet();
            var nativeClosure = new HashSet<uint>();
            for (bool changed = true; changed;)
            {
                changed = false;
                foreach (var (from, to) in closureEdges)
                    if (!joined.ContainsKey(to) && !manifest.Lists(to)
                        && (manifest.Lists(from) || joined.ContainsKey(from))
                        && (nativeEdges.Contains((from, to)) || socketClosure.Contains(from) || ListsClass(classes[to])))
                    {
                        joined.Add(to, from); changed = true;
                        if (socketClosure.Contains(from)) socketClosure.Add(to);
                        if (nativeEdges.Contains((from, to))) nativeClosure.Add(to);
                    }
            }
            if (joined.Count == 0) continue;
            foreach (var package in joined.Keys.GroupBy(tag => (ushort)TagHashes.Package(tag)).OrderBy(group => group.Key))
                data = PackageManifest.AddEntries(data, package.Key, package.Select(TagHashes.Entry));
            plan.Data.Add(manifestTag, data);
            plan.Rows.AddRange(joined.OrderBy(pair => pair.Key).Select(pair => new ManifestEdit(manifestTag.ToString("X8"),
                manifestEntry!.PackageName, pair.Value.ToString("X8"), pair.Key.ToString("X8"),
                socketClosure.Contains(pair.Key) ? "socket dependency"
                    : nativeClosure.Contains(pair.Key) ? "native dependency" : "reference")));
        }
        return plan;
    }

    /** A pointer is an aligned 32-bit tag or 64-bit wide hash of an added entry. */
    private static (uint From, uint To)[] PointerEdges(IEnumerable<(uint Tag, byte[] Data)> nodes, IReadOnlyList<AddedEntry> added)
    {
        var tags = added.Select(asset => asset.Tag).ToHashSet();
        var aliases = new Dictionary<ulong, uint>();
        foreach (var asset in added) foreach (ulong wide in asset.WideHashes) aliases.TryAdd(wide, asset.Tag);
        var edges = new HashSet<(uint, uint)>();
        foreach (var (from, data) in nodes)
        {
            for (int i = 0; i + 4 <= data.Length; i += 4)
                if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i)) is uint tag && tags.Contains(tag) && tag != from)
                    edges.Add((from, tag));
            if (aliases.Count == 0) continue;
            for (int i = 0; i + 8 <= data.Length; i += 8)
                if (aliases.TryGetValue(BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(i)), out uint tag) && tag != from)
                    edges.Add((from, tag));
        }
        return edges.ToArray();
    }

    private sealed record DependencyPlan(Dictionary<string, byte[]> Misc, List<DependencyEdit> Rows, int Skipped,
        object[] Unlisted);

    /** Generated entries join source records; added entries follow references from listed entries. */
    private static DependencyPlan PlanDependencies(PackageCatalog catalog, IReadOnlyDictionary<uint, byte[]> edited,
        IReadOnlyList<GeneratedObjectAsset> generated, IReadOnlyList<AddedEntry> added)
    {
        var misc = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<DependencyEdit>();
        if (generated.Count == 0 && added.Count == 0) return new(misc, rows, 0, []);

        // Only installed entries that can bring a new entry in need their records found.
        var wanted = generated.Select(asset => asset.SourceTag).Concat(edited.Keys).ToHashSet();
        var wantedPackages = wanted.Select(tag => TagHashes.Split(tag).PackageId).ToHashSet();
        var listing = new Dictionary<uint, List<(string Path, int Record)>>();
        var tables = new Dictionary<string, (PackageDependencyTable Table, byte[] Original)>(StringComparer.OrdinalIgnoreCase);
        int skipped = 0;
        var paths = catalog.Entries.Where(entry => catalog.ContainsInstalledPackage(entry.PackageId))
            .Select(entry => entry.PackageName).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => Path.Combine(catalog.PackageDirectory, name + ".pkg")).Order(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            byte[] original = PackageWriter.ReadMisc(path);
            PackageDependencyTable? table;
            try { table = PackageDependencyTable.Read(original); }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException) { skipped++; continue; }
            if (table is null) continue;
            bool kept = false;
            foreach (var record in table.Records)
                foreach (var group in record.Groups.Where(group => wantedPackages.Contains(group.PackageId)))
                    foreach (int entry in group.Entries())
                    {
                        uint tag = TagHashes.Make(group.PackageId, entry);
                        if (!wanted.Contains(tag)) continue;
                        if (!listing.TryGetValue(tag, out var list)) listing.Add(tag, list = []);
                        list.Add((path, record.Index)); kept = true;
                    }
            if (kept) tables.Add(path, (table, original));
        }

        // Each record maps a new entry to the entry that brought it in; the first one found is kept.
        var joins = new Dictionary<(string Path, int Record), Dictionary<uint, uint>>();
        var reach = new Dictionary<uint, HashSet<(string Path, int Record)>>();
        bool Join(uint tag, uint source, (string Path, int Record) record)
        {
            if (!reach.TryGetValue(tag, out var records)) reach.Add(tag, records = []);
            if (!records.Add(record)) return false;
            if (!joins.TryGetValue(record, out var set)) joins.Add(record, set = []);
            set.TryAdd(tag, source);
            return true;
        }
        foreach (var asset in generated)
        {
            var records = listing.GetValueOrDefault(asset.SourceTag)
                ?? throw new InvalidDataException($"No dependency record lists {asset.SourceTag:X8}, the source of "
                    + $"generated {asset.Id}. The game would never load {asset.TagHash:X8}.");
            foreach (var record in records) Join(asset.TagHash, asset.SourceTag, record);
        }

        // Chains of added entries spread until no record changes. A record loads only what it lists, so
        // the installed entries an added entry draws with join it too, unless it already lists them.
        var native = NativeDependencyEdges(catalog, edited, added);
        var edges = PointerEdges(edited.Select(pair => (pair.Key, pair.Value))
            .Concat(generated.Select(asset => (asset.TagHash, asset.Data)))
            .Concat(added.Select(asset => (asset.Tag, asset.Data))), added).Concat(native).Distinct().ToArray();
        bool Lists((string Path, int Record) record, uint tag) =>
            tables[record.Path].Table.Records[record.Record].Lists(TagHashes.Split(tag).PackageId, TagHashes.Split(tag).EntryIndex);
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var (from, to) in edges)
            {
                IEnumerable<(string Path, int Record)> records = edited.ContainsKey(from)
                    ? listing.GetValueOrDefault(from) ?? [] : reach.GetValueOrDefault(from) ?? [];
                foreach (var record in records.ToArray())
                    if (!native.Contains((from, to)) || !Lists(record, to)) changed |= Join(to, from, record);
            }
        }

        foreach (var group in joins.GroupBy(pair => pair.Key.Path).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var (table, original) = tables[group.Key];
            foreach (var (record, set) in group.OrderBy(pair => pair.Key.Record))
            {
                foreach (var package in set.Keys.GroupBy(tag => TagHashes.Split(tag).PackageId).OrderBy(package => package.Key))
                    table.Add(record.Record, package.Key, package.Select(tag => TagHashes.Split(tag).EntryIndex).Order());
                rows.AddRange(set.OrderBy(pair => pair.Key).Select(pair => new DependencyEdit(Path.GetFileName(group.Key),
                    record.Record, pair.Value.ToString("X8"), pair.Key.ToString("X8"))));
            }
            misc.Add(group.Key, table.Write(original));
        }
        object[] unlisted = added.Where(asset => !reach.ContainsKey(asset.Tag))
            .Select(asset => (object)new { id = asset.Id, tag = asset.Tag.ToString("X8") }).ToArray();
        return new(misc, rows, skipped, unlisted);
    }
}
