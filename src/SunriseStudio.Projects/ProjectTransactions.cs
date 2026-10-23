using System.Text.Json;
using SunriseStudio.Packages;

namespace SunriseStudio.Projects;

public sealed partial class StudioProject
{
    private bool transactionFactoryActive;
    /** The edit factory links allocated asset identities without publishing an intermediate project. */
    public IReadOnlyList<AddedProjectAsset> ApplyTransaction(IReadOnlyList<ProjectAssetAddition> additions,
        Func<IReadOnlyList<AddedProjectAsset>, ProjectAssetEdits>? editFactory = null, PackageCatalog? catalog = null)
        => CommitTransaction(additions, editFactory, null, catalog);

    public AddedAssetSnapshot CaptureAddedAsset(string id, PackageCatalog? catalog = null) =>
        new(AddedAssets.First(asset => asset.Id == id), ReadAddedAsset(id, catalog));

    /**
     * Null states remove created assets; saved states restore the same IDs and native entry indices.
     * @param objectStates Object edits to set or remove by key in the same manifest write; the result must compile.
     * @param additions New assets created in the same manifest write.
     * @return The created assets.
     */
    public IReadOnlyList<AddedProjectAsset> RestoreTransaction(ProjectAssetEdits edits, IReadOnlyDictionary<string, AddedAssetSnapshot?> states,
        PackageCatalog? catalog = null, IReadOnlyDictionary<string, ObjectEdit?>? objectStates = null,
        IReadOnlyList<ProjectAssetAddition>? additions = null) =>
        CommitTransaction(additions ?? [], _ => edits, states, catalog, objectStates);

    /** Every payload is staged before the manifest makes any part of a coupled change visible. */
    private IReadOnlyList<AddedProjectAsset> CommitTransaction(IReadOnlyList<ProjectAssetAddition> additions,
        Func<IReadOnlyList<AddedProjectAsset>, ProjectAssetEdits>? editFactory,
        IReadOnlyDictionary<string, AddedAssetSnapshot?>? states, PackageCatalog? catalog,
        IReadOnlyDictionary<string, ObjectEdit?>? objectStates = null)
    {
        RequireMutationAllowed();
        catalog?.InvalidateReadCache();
        var baseline = manifest;
        var changes = Changes.ToDictionary(value => value.TagHash);
        var assets = AddedAssets.Where(asset => states?.ContainsKey(asset.Id) != true).ToList();
        var writes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var obsolete = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, snapshot) in states ?? new Dictionary<string, AddedAssetSnapshot?>())
        {
            var current = AddedAssets.FirstOrDefault(asset => asset.Id == id);
            if (current is not null) obsolete.Add(current.Path);
            if (snapshot is null) continue;
            if (snapshot.Asset.Id != id || snapshot.Asset.Size != snapshot.Data.LongLength
                || snapshot.Asset.Sha256 != BinaryDelta.Hash(snapshot.Data))
                throw new InvalidDataException("The saved added-asset state changed.");
            var restored = StageAddition(new(snapshot.Asset.Name, snapshot.Data, snapshot.Asset.Native,
                snapshot.Asset.BaseTagHash), id, assets, catalog);
            if (restored.Asset.OriginalSha256 != snapshot.Asset.OriginalSha256
                || restored.Asset.OriginalSize != snapshot.Asset.OriginalSize)
                throw new InvalidDataException("The saved added asset belongs to a different original.");
            assets.Add(restored.Asset); writes.Add(restored.Asset.Path, restored.Stored);
        }
        var created = new List<AddedProjectAsset>();
        foreach (var addition in additions)
        {
            var next = StageAddition(addition, Guid.NewGuid().ToString("N"), assets, catalog);
            assets.Add(next.Asset); created.Add(next.Asset); writes.Add(next.Asset.Path, next.Stored);
        }
        RequireWideHashes(assets, catalog);
        ProjectAssetEdits edits;
        transactionFactoryActive = true;
        try { edits = editFactory?.Invoke(created.AsReadOnly()) ?? new(new Dictionary<uint, byte[]>()); }
        finally { transactionFactoryActive = false; }
        if (!ReferenceEquals(manifest, baseline))
            throw new InvalidOperationException("The edit factory changed the project during staging.");
        if (edits.Assets.Count > 0 && catalog is null)
            throw new InvalidOperationException("Package edits need the original installation.");
        foreach (var (tag, bytes) in edits.Assets.OrderBy(pair => pair.Key))
        {
            ArgumentNullException.ThrowIfNull(bytes);
            if (!catalog!.TryGetEntry(tag, out var entry)) throw new KeyNotFoundException("Asset is not installed.");
            byte[] original = catalog.ReadOriginal(tag);
            var current = changes.GetValueOrDefault(tag);
            if (current is not null && current.OriginalSha256 != BinaryDelta.Hash(original))
                throw new InvalidDataException("Original asset changed. Revert or restore its package version.");
            if (current is not null) obsolete.Add(current.DeltaPath);
            if (original.AsSpan().SequenceEqual(bytes)) { changes.Remove(tag); continue; }
            var delta = BinaryDelta.Create(original, bytes);
            byte[] stored = JsonSerializer.SerializeToUtf8Bytes(delta, ProjectJson.Options);
            string relative = $"deltas/{tag:X8}-{BinaryDelta.Hash(stored)}.json";
            writes.Add(relative, stored);
            changes[tag] = new(tag, entry.PackageName, delta.BaseSha256, original.Length,
                bytes.Length, relative, delta.ResultSha256);
        }
        foreach (var (id, bytes) in edits.AddedAssets ?? new Dictionary<string, byte[]>())
        {
            ArgumentNullException.ThrowIfNull(bytes);
            if (states?.ContainsKey(id) == true || created.Any(asset => asset.Id == id))
                throw new InvalidDataException("An asset cannot be restored or created and edited in the same transaction.");
            int index = assets.FindIndex(asset => asset.Id == id);
            if (index < 0) throw new KeyNotFoundException("Added asset is not in the project.");
            var current = assets[index];
            var next = PrepareAddedUpdate(current, bytes, catalog);
            assets[index] = next.Asset; writes.Add(next.Asset.Path, next.Stored); obsolete.Add(current.Path);
        }
        var objects = objectStates is null ? ObjectEdits : MergeObjectStates(objectStates);
        var activities = edits.AddedActivities ?? AddedActivities;
        ValidateActivities(activities, assets);
        RequireConfigBindingEdits(edits, states, catalog);
        RequireObjectEditsCompile(objects, edits.Assets, catalog, restoring: objectStates is not null, assets.ToArray());
        PublishTransaction(baseline with { Changes = changes.Values.OrderBy(value => value.TagHash).ToArray(),
            AddedAssets = assets.ToArray(), ObjectEdits = objectStates is null ? baseline.ObjectEdits : objects,
            AddedActivities = activities.Count == 0 ? null : Array.AsReadOnly(activities.ToArray()) },
            writes, obsolete);
        return created.AsReadOnly();
    }

    /** Allocation preserves explicit indices and stores derived payloads only as deltas. */
    private (AddedProjectAsset Asset, byte[] Stored) StageAddition(ProjectAssetAddition addition, string id,
        IReadOnlyList<AddedProjectAsset> assets, PackageCatalog? catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addition.Name);
        ArgumentNullException.ThrowIfNull(addition.Data);
        if (string.IsNullOrWhiteSpace(id) || id.Contains('/') || id.Contains('\\') || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("Invalid added asset identity.");
        var metadata = PrepareNativeMetadata(addition.Native, assets);
        if (metadata is not null && catalog?.ContainsInstalledPackage(metadata.PackageId) == true)
            throw new InvalidDataException($"Package id {metadata.PackageId:X4} is already installed.");
        string name = Path.GetFileName(addition.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\0'))
            throw new InvalidDataException("Added assets need a nonempty filename without null characters.");
        byte[] stored = addition.Data.ToArray();
        string hash = BinaryDelta.Hash(stored), relative = $"assets/{id}-{hash}.bin";
        string? baseHash = null; int? baseLength = null;
        if (addition.BaseTagHash is uint baseTag)
        {
            if (catalog is null || metadata is null)
                throw new InvalidDataException("Derived additions need native metadata and the original installation.");
            byte[] original = catalog.ReadOriginal(baseTag);
            var delta = BinaryDelta.Create(original, stored);
            baseHash = delta.BaseSha256; baseLength = delta.BaseLength;
            stored = JsonSerializer.SerializeToUtf8Bytes(delta, ProjectJson.Options);
            relative = $"deltas/added-{id}-{BinaryDelta.Hash(stored)}.json";
        }
        return (new(id, name, relative, hash, addition.Data.Length, metadata,
            addition.BaseTagHash, baseHash, baseLength), stored);
    }

    /** Failed publication removes only newly staged files and leaves the saved manifest unchanged. */
    private void PublishTransaction(ProjectManifest next, IReadOnlyDictionary<string, byte[]> writes,
        IEnumerable<string> obsolete)
    {
        var created = new List<string>();
        try
        {
            foreach (var (relative, bytes) in writes)
            {
                string path = ProjectPaths.Resolve(DirectoryPath, relative);
                if (File.Exists(path))
                {
                    if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                        throw new InvalidDataException("An immutable project payload has changed.");
                    continue;
                }
                ProjectPaths.AtomicWrite(path, bytes); created.Add(relative);
            }
            ReplaceManifest(next);
        }
        catch { foreach (string relative in created) TryDeleteUnreferenced(relative); throw; }
        foreach (string relative in obsolete) TryDeleteUnreferenced(relative);
    }

    private void TryDeleteUnreferenced(string relative)
    {
        try { DeleteUnreferenced(relative); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private void RequireMutationAllowed()
    {
        if (configCompilationActive) throw new InvalidOperationException("A config compiler cannot change the project.");
        if (transactionFactoryActive) throw new InvalidOperationException("An edit factory cannot change the project.");
    }
}
