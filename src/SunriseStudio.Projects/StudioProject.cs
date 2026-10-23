using System.Text;
using System.Text.Json;
using SunriseStudio.Packages;

namespace SunriseStudio.Projects;

/** The manifest owns edits; source paths and decoded package bytes never enter it. */
public sealed partial class StudioProject
{
    public const string ProjectFileName = "sunrise.project.json";
    // The newest manifest version this build reads and writes.
    public const int ManifestVersion = 8;
    private const int MaximumManifestBytes = 16 * 1024 * 1024;
    private ProjectManifest manifest;
    public string DirectoryPath { get; }
    public string ManifestPath => Path.Combine(DirectoryPath, ProjectFileName);
    public string Name => manifest.Name;
    public IReadOnlyList<ProjectAssetChange> Changes => manifest.Changes;
    public IReadOnlyList<AddedProjectAsset> AddedAssets => manifest.AddedAssets;
    public ModMetadata? Mod => manifest.Mod;
    public IReadOnlyList<ProjectObjectName> ObjectNames => manifest.ObjectNames ?? [];
    /** Counts saved manifest changes; a failed save leaves it unchanged. */
    public int Revision { get; private set; }

    private StudioProject(string folder, ProjectManifest manifest) =>
        (DirectoryPath, this.manifest) = (Path.GetFullPath(folder), manifest);

    /** A new project must start in an empty directory. */
    public static StudioProject Create(string folder, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string path = Path.GetFullPath(folder);
        ProjectPaths.RejectLinks(path);
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new IOException("Choose an empty project folder.");
        Directory.CreateDirectory(path);
        var project = new StudioProject(path, new(1, name.Trim(), [], []));
        ProjectPaths.AtomicWrite(Path.Combine(path, ".gitignore"),
            Encoding.UTF8.GetBytes("/build/\n/exports/\n/.local/\n*.tmp\n*.pkg\n"));
        project.Save();
        return project;
    }

    /** Opening validates references before any referenced file can be read. */
    public static StudioProject Open(string manifestOrFolder)
    {
        if (!Directory.Exists(manifestOrFolder)
            && !string.Equals(Path.GetFileName(manifestOrFolder), ProjectFileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Choose {ProjectFileName} or its project folder.");
        string folder = Directory.Exists(manifestOrFolder)
            ? Path.GetFullPath(manifestOrFolder) : Path.GetDirectoryName(Path.GetFullPath(manifestOrFolder))!;
        string path = Path.Combine(folder, ProjectFileName);
        ProjectPaths.RejectLinks(path);
        if (new FileInfo(path).Length > MaximumManifestBytes) throw new InvalidDataException("Project manifest is too large.");
        byte[] json = File.ReadAllBytes(path);
        RequireKnownFeatures(json);
        var data = JsonSerializer.Deserialize<ProjectManifest>(json, ProjectJson.Options)
            ?? throw new InvalidDataException("Project manifest is empty.");
        if (data.Version > ManifestVersion) throw new InvalidDataException("This project needs a newer Sunrise Studio.");
        if (data.Version < 1 || string.IsNullOrWhiteSpace(data.Name) || data.Changes is null || data.AddedAssets is null
            || data.Version == 1 && data.ObjectEdits is not null || data.Version == 2 && data.ObjectEdits is null)
            throw new InvalidDataException("Unsupported project manifest.");
        RequireFeatures(data);
        foreach (var edit in data.ObjectEdits ?? []) edit.Validate();
        if (data.AddedActivities is not null && !data.HasFeature(ProjectFeatures.AddedActivities))
            throw new InvalidDataException("Activity registrations require project manifest version 8.");
        ValidateActivities(data.AddedActivities ?? [], data.AddedAssets);
        data = data with { AddedActivities = data.AddedActivities is { Count: > 0 }
            ? Array.AsReadOnly(data.AddedActivities.ToArray()) : null };
        if ((data.ObjectEdits ?? []).Select(edit => edit.Key).Distinct().Count() != (data.ObjectEdits?.Count ?? 0))
            throw new InvalidDataException("Project contains two edits of one object.");
        if (data.Changes.Select(x => x.TagHash).Distinct().Count() != data.Changes.Count
            || data.AddedAssets.Select(x => x.Id).Distinct().Count() != data.AddedAssets.Count)
            throw new InvalidDataException("Project contains duplicate assets.");
        foreach (var change in data.Changes)
        {
            ValidateHash(change.OriginalSha256);
            ValidateHash(change.ResultSha256);
            if (change.OriginalSize < 0 || change.EditedSize < 0) throw new InvalidDataException("Invalid asset size.");
            RequireFile(folder, change.DeltaPath, "deltas/");
        }
        foreach (var asset in data.AddedAssets)
        {
            ValidateHash(asset.Sha256);
            if (asset.Size < 0 || string.IsNullOrWhiteSpace(asset.Id)) throw new InvalidDataException("Invalid added asset.");
            ValidateNative(asset.Native, saved: true);
            if (asset.BaseTagHash is not null)
            {
                ValidateHash(asset.OriginalSha256!);
                if (asset.OriginalSize is null or < 0 || asset.Native is null)
                    throw new InvalidDataException("Invalid derived asset base.");
            }
            else if (asset.OriginalSha256 is not null || asset.OriginalSize is not null)
                throw new InvalidDataException("Added asset has no base tag.");
            RequireFile(folder, asset.Path, asset.BaseTagHash is null ? "assets/" : "deltas/");
        }
        if (data.AddedAssets.Where(x => x.Native is not null)
            .GroupBy(x => (x.Native!.PackageId, x.Native.EntryIndex)).Any(x => x.Count() > 1))
            throw new InvalidDataException("Project contains duplicate native entry indices.");
        bool aliases = data.AddedAssets.Any(asset => asset.Native?.WideHashes is { Count: > 0 });
        if (aliases && data.Version < 3)
            throw new InvalidDataException("Authored wide hashes require project manifest version 3.");
        RequireWideHashes(data.AddedAssets, null, requireCatalog: false);
        if (data.ConfigBindings is { Count: > 0 } && data.Version < 4)
            throw new InvalidDataException("Config bindings require project manifest version 4.");
        if (data.ObjectNames is { Count: > 0 } names)
        {
            if (data.Version < 5) throw new InvalidDataException("Object names require project manifest version 5.");
            foreach (var name in names) name.Validate();
            if (names.Select(name => name.Key).Distinct(StringComparer.Ordinal).Count() != names.Count)
                throw new InvalidDataException("Project names one object twice.");
        }
        if (data.ObjectGroups is { Count: > 0 } groups)
        {
            if (data.Version < 6) throw new InvalidDataException("Object groups require project manifest version 6.");
            if (data.Version < 7 && groups.Any(group => group?.CollisionParts is not null))
                throw new InvalidDataException("Collision parts in groups require project manifest version 7.");
            foreach (var group in groups)
            {
                if (group is null) throw new InvalidDataException("Unsupported project manifest.");
                group.Validate();
            }
            if (groups.Select(group => group.Id).Distinct(StringComparer.Ordinal).Count() != groups.Count)
                throw new InvalidDataException("Project has two groups with one id.");
            RequireOneGroupEach(groups);
        }
        data = data with { ObjectGroups = data.ObjectGroups is { Count: > 0 } saved
            ? Array.AsReadOnly(saved.Select(Frozen).ToArray()) : null };
        data = data with { Mod = FreezeConfigDeclarations(data.Mod) };
        RequireConfigBindings(data.ConfigBindings ?? [], data.Mod);
        data = data with { ConfigBindings = data.ConfigBindings is { Count: > 0 }
            ? Array.AsReadOnly(data.ConfigBindings.ToArray()) : null };
        data = data with { AddedAssets = data.AddedAssets.Select(asset => asset.Native?.WideHashes is { Count: 0 }
            ? asset with { Native = asset.Native with { WideHashes = null } } : asset).ToArray() };
        return new StudioProject(folder, data);
    }

    public bool ContainsEdit(uint tagHash) => Changes.Any(x => x.TagHash == tagHash);

    /** Catalog originals bypass the editor overlay to avoid recursive reconstruction. */
    public byte[] ReadAsset(uint tagHash, PackageCatalog catalog)
    {
        byte[] original = catalog.ReadOriginal(tagHash);
        var change = Changes.FirstOrDefault(x => x.TagHash == tagHash);
        if (change is null) return original;
        var delta = ReadDelta(change);
        if (delta.BaseSha256 != change.OriginalSha256 || delta.BaseLength != change.OriginalSize
            || delta.ResultSha256 != change.ResultSha256 || delta.ResultLength != change.EditedSize)
            throw new InvalidDataException("Delta does not match the project manifest.");
        return delta.Apply(original);
    }

    /** Each edit is measured against the installed original, never the previous edit. */
    public void ApplyEdit(uint tagHash, ReadOnlySpan<byte> edited, PackageCatalog catalog) =>
        ApplyEdits(new Dictionary<uint, byte[]> { [tagHash] = edited.ToArray() }, catalog);

    /** Coupled assets become visible together through one atomic manifest replacement. */
    public void ApplyEdits(IReadOnlyDictionary<uint, byte[]> edits, PackageCatalog catalog,
        IReadOnlyDictionary<string, byte[]>? addedEdits = null)
        => ApplyTransaction([], _ => new(edits, addedEdits), catalog);

    /** Removing a change restores catalog bytes without writing the installation. */
    public void Revert(uint tagHash) => Revert(tagHash, null);

    /** Removes a raw change once object edits and config bindings still compile without it. */
    public void Revert(uint tagHash, PackageCatalog? catalog)
    {
        RequireMutationAllowed();
        var change = Changes.FirstOrDefault(x => x.TagHash == tagHash);
        if (change is null) return;
        catalog?.InvalidateReadCache();
        if (ObjectEdits.Count != 0)
        {
            if (catalog is null) throw new InvalidOperationException("Open the original installation to validate object edits before reverting raw data.");
            RequireObjectEditsCompile(ObjectEdits,
                new Dictionary<uint, byte[]> { [tagHash] = catalog.ReadOriginal(tagHash) }, catalog, restoring: false);
        }
        if (ConfigBindings.Any(binding => binding.TagHash == tagHash))
        {
            if (catalog is null) throw new InvalidOperationException("Open the original installation to validate bound assets.");
            RequireConfigBindingEdits(new(new Dictionary<uint, byte[]> { [tagHash] = catalog.ReadOriginal(tagHash) }), null, catalog);
        }
        ReplaceManifest(manifest with { Changes = Changes.Where(x => x.TagHash != tagHash).ToArray() });
        TryDeleteUnreferenced(change.DeltaPath);
    }

    /** Whole files are reserved for assets the user adds to the project. */
    public AddedProjectAsset AddAsset(string name, ReadOnlySpan<byte> bytes, NativeAssetMetadata? metadata = null) =>
        AddAssets([new(name, bytes.ToArray(), metadata)])[0];

    /** Clones refer to installed originals; even unchanged clones store no original payload. */
    public AddedProjectAsset AddDerivedAsset(string name, uint originalTag, ReadOnlySpan<byte> editedBytes,
        NativeAssetMetadata nativeMetadata, PackageCatalog catalog) =>
        AddAssets([new(name, editedBytes.ToArray(), nativeMetadata, originalTag)], catalog)[0];

    /** Coupled native entries are validated and published through one manifest replacement. */
    public IReadOnlyList<AddedProjectAsset> AddAssets(IReadOnlyList<ProjectAssetAddition> additions,
        PackageCatalog? catalog = null)
        => ApplyTransaction(additions, catalog: catalog);

    /** Derived entries can be reconstructed only from their matching installation. */
    public byte[] ReadAddedAsset(string id, PackageCatalog? catalog = null)
    {
        var asset = AddedAssets.First(x => x.Id == id);
        byte[] bytes = File.ReadAllBytes(ProjectPaths.Resolve(DirectoryPath, asset.Path));
        if (asset.BaseTagHash is uint baseTag)
        {
            if (catalog is null) throw new InvalidOperationException("Open the original installation to read a derived asset.");
            var delta = JsonSerializer.Deserialize<BinaryDelta>(bytes, ProjectJson.Options)
                ?? throw new InvalidDataException("Derived asset delta is empty.");
            if (delta.BaseSha256 != asset.OriginalSha256 || delta.BaseLength != asset.OriginalSize)
                throw new InvalidDataException("Derived asset base does not match the manifest.");
            bytes = delta.Apply(catalog.ReadOriginal(baseTag));
        }
        if (bytes.LongLength != asset.Size || BinaryDelta.Hash(bytes) != asset.Sha256)
            throw new InvalidDataException("Added asset verification failed. Import the changed file again.");
        return bytes;
    }

    /** Derived edits stay relative to their first installed base across repeated saves. */
    public void UpdateAddedAsset(string id, ReadOnlySpan<byte> bytes, PackageCatalog? catalog = null)
    {
        RequireMutationAllowed();
        catalog?.InvalidateReadCache();
        var asset = AddedAssets.First(x => x.Id == id);
        var (updated, stored) = PrepareAddedUpdate(asset, bytes, catalog);
        RequireConfigBindingEdits(new(new Dictionary<uint, byte[]>(), new Dictionary<string, byte[]> { [id] = bytes.ToArray() }), null, catalog);
        PublishTransaction(manifest with { AddedAssets = AddedAssets.Select(x => x.Id == id ? updated : x).ToArray() },
            new Dictionary<string, byte[]> { [updated.Path] = stored }, [asset.Path]);
    }

    /** Staging checks the saved bytes and base before choosing a new immutable storage path. */
    private (AddedProjectAsset Asset, byte[] Stored) PrepareAddedUpdate(AddedProjectAsset asset,
        ReadOnlySpan<byte> bytes, PackageCatalog? catalog)
    {
        ReadAddedAsset(asset.Id, catalog);
        ValidateNative(asset.Native, saved: true);
        string hash = BinaryDelta.Hash(bytes);
        string relative = $"assets/{asset.Id}-{hash}.bin";
        byte[] stored = bytes.ToArray();
        if (asset.BaseTagHash is uint baseTag)
        {
            if (catalog is null) throw new InvalidOperationException("Open the original installation to edit a derived asset.");
            byte[] original = catalog.ReadOriginal(baseTag);
            if (original.Length != asset.OriginalSize || BinaryDelta.Hash(original) != asset.OriginalSha256)
                throw new InvalidDataException("Derived asset original changed. Restore its package version.");
            stored = JsonSerializer.SerializeToUtf8Bytes(BinaryDelta.Create(original, bytes), ProjectJson.Options);
            relative = $"deltas/added-{asset.Id}-{BinaryDelta.Hash(stored)}.json";
        }
        var updated = asset with { Path = relative, Sha256 = hash, Size = bytes.Length };
        return (updated, stored);
    }

    /** Only storage no longer referenced by the saved manifest can be removed. */
    public void RemoveAddedAsset(string id)
    {
        RequireMutationAllowed();
        if (ConfigBindings.Any(binding => binding.AddedAssetId == id))
            throw new InvalidDataException("Unbind the config settings before removing this asset.");
        var asset = AddedAssets.First(x => x.Id == id);
        ReplaceManifest(manifest with { AddedAssets = AddedAssets.Where(x => x.Id != id).ToArray() });
        TryDeleteUnreferenced(asset.Path);
    }

    public void Save()
    {
        ReplaceManifest(manifest);
    }

    /** Only valid mod details are saved; null clears them. */
    public void SetMod(ModMetadata? metadata)
    {
        RequireMutationAllowed();
        if (metadata is not null) ModManifest.Require(metadata);
        metadata = FreezeConfigDeclarations(metadata);
        RequireConfigBindings(ConfigBindings, metadata);
        ReplaceManifest(manifest with { Mod = metadata });
    }

    /** A blank name removes the object's entry. */
    public void SetObjectName(string key, string? name)
    {
        RequireMutationAllowed();
        var names = ObjectNames.Where(entry => entry.Key != key).ToList();
        if (!string.IsNullOrWhiteSpace(name))
        {
            var entry = new ProjectObjectName(key, name.Trim());
            entry.Validate();
            names.Add(entry);
        }
        names.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        ReplaceManifest(manifest with { ObjectNames = names.Count == 0 ? null : names.AsReadOnly() });
    }

    private static ModMetadata? FreezeConfigDeclarations(ModMetadata? metadata) => metadata?.Config is { } config
        ? metadata with { Config = Array.AsReadOnly(config.ToArray()) } : metadata;

    /** Failed saves keep the in-memory manifest at its last saved state. */
    private void ReplaceManifest(ProjectManifest updated)
    {
        RequireMutationAllowed();
        updated = Versioned(updated);
        ProjectPaths.AtomicWrite(ManifestPath, SerializeManifest(updated));
        manifest = updated; Revision++;
    }

    /** Capability versions fall back only after every field unknown to the older format is removed. */
    private static ProjectManifest Versioned(ProjectManifest value)
    {
        bool objects = value.ObjectEdits is { Count: > 0 };
        var assets = value.AddedAssets.Any(asset => asset.Native?.WideHashes is { Count: 0 })
            ? value.AddedAssets.Select(asset => asset.Native?.WideHashes is { Count: 0 }
                ? asset with { Native = asset.Native with { WideHashes = null } } : asset).ToArray()
            : value.AddedAssets;
        bool aliases = assets.Any(asset => asset.Native?.WideHashes is { Count: > 0 });
        bool bindings = value.ConfigBindings is { Count: > 0 };
        bool names = value.ObjectNames is { Count: > 0 };
        bool groups = value.ObjectGroups is { Count: > 0 };
        bool parts = groups && value.ObjectGroups!.Any(group => group.CollisionParts is not null);
        bool activities = value.AddedActivities is { Count: > 0 };
        var features = new List<string>();
        if (activities) features.Add(ProjectFeatures.AddedActivities);
        features.Sort(StringComparer.Ordinal);
        int version = features.Count > 0 ? 8 : parts ? 7 : groups ? 6 : names ? 5 : bindings ? 4 : aliases ? 3 : objects ? 2 : 1;
        if (value.Version == version && ReferenceEquals(assets, value.AddedAssets) && (value.Features ?? []).SequenceEqual(features)
            && (objects || value.ObjectEdits is null) && (bindings || value.ConfigBindings is null)
            && (names || value.ObjectNames is null) && (groups || value.ObjectGroups is null)
            && (activities || value.AddedActivities is null)) return value;
        return value with { Version = version, Features = features.Count == 0 ? null : features.AsReadOnly(), AddedAssets = assets,
            ObjectEdits = objects ? value.ObjectEdits : null,
            ConfigBindings = bindings ? value.ConfigBindings : null, ObjectNames = names ? value.ObjectNames : null,
            ObjectGroups = groups ? value.ObjectGroups : null, AddedActivities = activities ? value.AddedActivities : null };
    }

    /** A manifest naming a feature this build does not know is refused before its fields are read. */
    private static void RequireKnownFeatures(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Array
            && features.EnumerateArray().Any(name => name.ValueKind == JsonValueKind.String
                && !ProjectFeatures.Known.Contains(name.GetString()!, StringComparer.Ordinal)))
            throw new InvalidDataException("This project needs a newer Sunrise Studio.");
    }

    /** Features belong to version 8, each named once. */
    private static void RequireFeatures(ProjectManifest data)
    {
        if (data.Features is null) return;
        if (data.Version < 8) throw new InvalidDataException("Named features require project manifest version 8.");
        if (data.Features.Any(name => name is null) || data.Features.Distinct(StringComparer.Ordinal).Count() != data.Features.Count)
            throw new InvalidDataException("Unsupported project manifest.");
    }

    private static byte[] SerializeManifest(ProjectManifest value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, ProjectJson.Options);
        if (bytes.Length > MaximumManifestBytes) throw new InvalidDataException("Project manifest is too large.");
        return bytes;
    }

    private BinaryDelta ReadDelta(ProjectAssetChange change) =>
        JsonSerializer.Deserialize<BinaryDelta>(File.ReadAllBytes(ProjectPaths.Resolve(DirectoryPath, change.DeltaPath)),
            ProjectJson.Options) ?? throw new InvalidDataException("Delta is empty.");

    internal static void ValidateHash(string hash)
    {
        if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid project checksum.");
    }

    /** Saved entries may keep an id above the new-package limit so old projects open; the build refuses it. */
    private static void ValidateNative(NativeAssetMetadata? metadata, bool saved = false)
    {
        if (metadata is null) return;
        if (!saved && !PackageWriter.IsNewPackageId(metadata.PackageId))
            throw new InvalidDataException($"New packages need an id from {PackageWriter.NewPackageIdRange}.");
        if (metadata.PackageId < PackageWriter.FirstNewPackageId
            || metadata.EntryIndex is null or < 0 or >= 8192 || metadata.Name?.Contains('\0') == true)
            throw new InvalidDataException("Invalid native asset metadata.");
    }

    private static NativeAssetMetadata? PrepareNativeMetadata(NativeAssetMetadata? metadata,
        IReadOnlyList<AddedProjectAsset> assets)
    {
        if (metadata is null) return null;
        int next = assets.Where(x => x.Native?.PackageId == metadata.PackageId)
            .Select(x => x.Native!.EntryIndex ?? -1).DefaultIfEmpty(-1).Max() + 1;
        metadata = metadata with { EntryIndex = metadata.EntryIndex ?? next,
            WideHashes = metadata.WideHashes is { Count: > 0 } ? metadata.WideHashes : null };
        ValidateNative(metadata);
        if (assets.Any(x => x.Native?.PackageId == metadata.PackageId && x.Native.EntryIndex == metadata.EntryIndex))
            throw new InvalidDataException("Native entry index is already used.");
        return metadata;
    }

    private static void RequireFile(string folder, string relative, string prefix)
    {
        if (!relative.StartsWith(prefix, StringComparison.Ordinal)
            || !File.Exists(ProjectPaths.Resolve(folder, relative)))
            throw new InvalidDataException("Project asset file is missing or outside its storage folder.");
    }

    private void DeleteUnreferenced(string? relative)
    {
        if (relative is null) return;
        string path = ProjectPaths.Resolve(DirectoryPath, relative);
        if (Changes.Any(change => SameStorage(change.DeltaPath)) || AddedAssets.Any(asset => SameStorage(asset.Path))) return;
        if (File.Exists(path)) File.Delete(path);

        bool SameStorage(string other) => string.Equals(ProjectPaths.Resolve(DirectoryPath, other), path,
            StringComparison.OrdinalIgnoreCase);
    }

    public ProjectBuildResult Build(PackageCatalog catalog, string? outputDirectory = null, bool invalidate = true) =>
        ProjectBuilder.Build(this, catalog, outputDirectory, invalidate);
}
