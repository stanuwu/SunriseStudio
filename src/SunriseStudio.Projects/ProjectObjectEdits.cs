using System.Text.Json;
using System.Collections.Frozen;
using SunriseStudio.Packages;
using SunriseStudio.Tags;

namespace SunriseStudio.Projects;

public sealed partial class StudioProject
{
    private sealed record ObjectEditCache(ProjectManifest Manifest, PackageCatalog Catalog, long ReadGeneration,
        IObjectEditCompiler Compiler, string RecipeHash, ObjectEditCompilation Result);
    private ObjectEditCache? objectCache, buildCache;

    /** The host sets this; a project with object edits cannot be read or built without it. */
    public IObjectEditCompiler? ObjectEditCompiler { get; set; }

    public IReadOnlyList<ObjectEdit> ObjectEdits => manifest.ObjectEdits ?? [];

    public ObjectEdit? FindObjectEdit(string key) => ObjectEdits.FirstOrDefault(edit => edit.Key == key);

    /**
     * Reuse needs the same manifest, compiler and original-byte generation; callers copy before writing.
     * @param build Also runs the build-only rules; only a build or an install passes true.
     */
    public ObjectEditCompilation CompileObjectEdits(PackageCatalog catalog, bool build = false) => Compile(manifest, catalog, build);

    /** Project bytes as a build writes them, without the build-only derived data: raw deltas first, then object edits. */
    public byte[] ReadCompiled(uint tagHash, PackageCatalog catalog) =>
        CompileObjectEdits(catalog).Assets.TryGetValue(tagHash, out var bytes) ? bytes.ToArray() : ReadAsset(tagHash, catalog);

    public bool RecordObjectEdit(ObjectEdit edit, PackageCatalog catalog) => RecordObjectEdits([edit], catalog);

    /**
     * Merges by target and drops values the rows already hold. One compile saves all edits or none.
     * @return False when the saved edits did not change.
     */
    public bool RecordObjectEdits(IReadOnlyList<ObjectEdit> edits, PackageCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(edits);
        foreach (var edit in edits)
        {
            ArgumentNullException.ThrowIfNull(edit, nameof(edits));
            edit.Validate();
        }
        if (edits.Select(edit => edit.Key).Distinct(StringComparer.Ordinal).Count() != edits.Count)
            throw new InvalidDataException("One change edits an object twice.");
        var compiler = RequireCompiler();
        var states = new Dictionary<string, ObjectEdit?>(StringComparer.Ordinal);
        foreach (var edit in edits)
        {
            var previous = FindObjectEdit(edit.Key);
            var next = compiler.Normalize(ObjectEdit.Merge(previous, edit), tag => ReadAsset(tag, catalog), catalog);
            if (!Same(previous, next)) states[edit.Key] = next;
        }
        if (states.Count == 0) return false;
        RestoreObjectEdits(states, catalog);
        return true;
    }

    /** Sets or removes whole entries by key; a null state removes. The result must compile before it is saved. */
    public void RestoreObjectEdits(IReadOnlyDictionary<string, ObjectEdit?> states, PackageCatalog catalog)
    {
        RequireMutationAllowed();
        var next = Versioned(manifest with { ObjectEdits = MergeObjectStates(states) });
        Compile(next, catalog);
        ReplaceManifest(next);
    }

    /** The saved object edits with these whole entries set or removed, in key order. */
    private ObjectEdit[] MergeObjectStates(IReadOnlyDictionary<string, ObjectEdit?> states)
    {
        var edits = ObjectEdits.ToDictionary(edit => edit.Key, StringComparer.Ordinal);
        foreach (var (key, state) in states)
        {
            edits.Remove(key);
            if (state is null) continue;
            if (state.Key != key) throw new InvalidDataException("The saved object edit belongs to another object.");
            state.Validate();
            edits.Add(key, state);
        }
        return edits.Values.OrderBy(edit => edit.Key, StringComparer.Ordinal).ToArray();
    }

    /**
     * Refuses raw payloads that stop the object edits from compiling.
     * Restoring requires the result to compile. Otherwise only a change that breaks working edits is refused.
     * @param raw Staged whole payloads by tag; other tags read the saved raw deltas.
     */
    private void RequireObjectEditsCompile(IReadOnlyList<ObjectEdit> edits, IReadOnlyDictionary<uint, byte[]> raw,
        PackageCatalog? catalog, bool restoring, IReadOnlyList<AddedProjectAsset>? stagedAssets = null)
    {
        bool additionsChanged = stagedAssets is not null && !manifest.AddedAssets.Where(asset => asset.Native is not null)
            .Select(asset => (asset.Native!.PackageId, asset.Native.Reference)).ToHashSet()
            .SetEquals(stagedAssets.Where(asset => asset.Native is not null)
                .Select(asset => (asset.Native!.PackageId, asset.Native.Reference)));
        if (edits.Count == 0 || raw.Count == 0 && !restoring && !additionsChanged) return;
        if (catalog is null) throw new InvalidOperationException("Object edits need the original installation.");
        byte[] ReadStaged(uint tag) => raw.TryGetValue(tag, out var bytes) ? bytes : ReadAsset(tag, catalog);
        var options = CompileOptions(stagedAssets is null ? manifest : manifest with { AddedAssets = stagedAssets }, ReadTagAllocator(), catalog) with
        { RawChangedTags = manifest.Changes.Select(change => change.TagHash).Concat(raw.Keys).ToFrozenSet() };
        if (restoring) { CompileOn(edits, ReadStaged, catalog, options: options); return; }
        ObjectEditCompilation before;
        // Edits that already fail do not block other saves; the build still refuses them.
        try { before = Compile(manifest, catalog); }
        catch (Exception error) when (IsCompileFailure(error)) { return; }
        try { CompileOn(edits, ReadStaged, catalog, options: options); }
        catch (Exception error) when (IsCompileFailure(error))
        {
            var names = before.Edits.Where(item => raw.ContainsKey(item.Edit.Target.SourceTag) || item.Tags.Any(raw.ContainsKey))
                .Select(item => item.Edit.Name ?? item.Edit.Key).DefaultIfEmpty("the project").ToArray();
            throw new InvalidDataException($"This change breaks the object edit of {string.Join(", ", names)}: {error.Message} "
                + "Revert that object edit first, or keep its row unchanged.", error);
        }
    }

    private static bool IsCompileFailure(Exception error) => error is InvalidDataException or NotSupportedException
        or KeyNotFoundException || error is InvalidOperationException and not ObjectDisposedException;

    /** Removing an edit needs no installation, so a stale edit can always be reverted. */
    public void RevertObjectEdit(string key)
    {
        RequireMutationAllowed();
        if (FindObjectEdit(key) is null) return;
        ReplaceManifest(manifest with { ObjectEdits = ObjectEdits.Where(edit => edit.Key != key).ToArray() });
    }

    /** Every target row must still hash to its installed bytes; a different base is refused like a delta. */
    private ObjectEditCompilation Compile(ProjectManifest source, PackageCatalog catalog, bool build = false)
    {
        var edits = source.ObjectEdits ?? [];
        if (edits.Count == 0) return ObjectEditCompilation.Empty;
        var compiler = RequireCompiler();
        long generation = catalog.ReadGeneration;
        string recipeHash = RecipeHash(edits);
        if ((build ? buildCache : objectCache) is { } cache && ReferenceEquals(cache.Manifest, source)
            && ReferenceEquals(cache.Catalog, catalog) && cache.ReadGeneration == generation
            && ReferenceEquals(cache.Compiler, compiler) && cache.RecipeHash == recipeHash)
            return cache.Result;
        var result = CompileOn(edits, tag => ReadRaw(source, tag, catalog), catalog, compiler, generation,
            CompileOptions(source, ReadTagAllocator(), catalog) with { Build = build });
        RequireRecipeHash(edits, recipeHash);
        var stored = new ObjectEditCache(source, catalog, generation, compiler, recipeHash, result);
        if (build) buildCache = stored; else objectCache = stored;
        return result;
    }

    /** Public edit vectors are mutable; cache identity includes their current values. */
    private static string RecipeHash(IReadOnlyList<ObjectEdit> edits) =>
        BinaryDelta.Hash(JsonSerializer.SerializeToUtf8Bytes(edits, ProjectJson.Options));

    private static void RequireRecipeHash(IReadOnlyList<ObjectEdit> edits, string expected)
    {
        try { if (RecipeHash(edits) == expected) return; }
        catch (Exception error) when (error is ArgumentException or JsonException or NotSupportedException)
        { throw new IOException("Object edit values changed during compilation.", error); }
        throw new IOException("Object edit values changed during compilation.");
    }

    /** Checks each target row's installed hash, then compiles; the sources must not change meanwhile. */
    private ObjectEditCompilation CompileOn(IReadOnlyList<ObjectEdit> edits, Func<uint, byte[]> readRaw,
        PackageCatalog catalog, IObjectEditCompiler? compiler = null, long? readGeneration = null,
        ObjectEditCompileOptions? options = null)
    {
        compiler ??= RequireCompiler();
        var baseline = manifest;
        options ??= CompileOptions(baseline, ReadTagAllocator(), catalog);
        long generation = readGeneration ?? catalog.ReadGeneration;
        string recipeHash = RecipeHash(edits);
        try
        {
            foreach (var edit in edits)
            {
                edit.Validate();
                byte[] original = catalog.ReadOriginal(edit.Target.SourceTag);
                if (edit.Target.Offset > original.Length - edit.Target.Length
                    || BinaryDelta.Hash(original.AsSpan(edit.Target.Offset, edit.Target.Length)) != edit.Target.OriginalSha256)
                    throw new InvalidDataException($"The installed row of {edit.Name ?? edit.Key} changed. "
                        + "Revert its object edit or restore the package version.");
            }
            var result = compiler.Compile(edits, readRaw, catalog, options);
            ValidateGeneratedAssets(result, options, catalog);
            return result;
        }
        finally
        {
            RequireRecipeHash(edits, recipeHash);
            if (catalog.ReadGeneration != generation || !ReferenceEquals(ObjectEditCompiler, compiler)
                || !ReferenceEquals(baseline, manifest))
                throw new IOException("Object edit sources or compiler changed during compilation.");
        }
    }

    /**
     * Moves generated object data of a project from before the mod probe onto it. Its generated
     * tags change, so a mod installed from it must be installed again.
     */
    // TODO: call from an editor command that opts an older project in; no such command exists yet.
    public void UseModProbeForGeneratedData()
    {
        if (ReadTagAllocator() is not { IdentityGeneratedPackages: true } allocator) return;
        allocator.IdentityGeneratedPackages = false;
        SaveTagAllocator(allocator);
        objectCache = null;
        buildCache = null;
    }

    /** Without allocation.json the project predates the mod probe and keeps its old ids. */
    private ObjectEditCompileOptions CompileOptions(ProjectManifest source, TagAllocator? allocation, PackageCatalog catalog) =>
        new(source.Mod?.Id ?? source.Name, source.AddedAssets.Where(asset => asset.Native is not null)
            .Select(asset => asset.Native!.PackageId).ToFrozenSet())
        {
            AddedNativeReferences = source.AddedAssets.Where(asset => asset.Native is not null)
                .Select(asset => asset.Native!.Reference).ToFrozenSet(),
            AddedEntries = source.AddedAssets.Where(asset => asset.Native is not null)
                .Select(asset => new AddedObjectEntry(TagHashes.Make(asset.Native!.PackageId, asset.Native!.EntryIndex!.Value), asset.Native!.Reference, asset.Native.TypeInfo,
                    asset.Native.WideHashes ?? WideHashAliases.Empty, () => ReadAddedAsset(asset.Id, catalog))).ToArray(),
            RawChangedTags = source.Changes.Select(change => change.TagHash).ToFrozenSet(),
            ModProbe = allocation is { IdentityGeneratedPackages: false },
            TagPackages = (allocation?.Packages ?? []).ToFrozenSet(),
        };

    /** A compiler cannot replace installed or authored entries with its private generated assets. */
    private static void ValidateGeneratedAssets(ObjectEditCompilation compiled, ObjectEditCompileOptions options,
        PackageCatalog catalog)
    {
        HashSet<uint> tags = [];
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (var asset in compiled.GeneratedAssets)
        {
            if (asset is null || asset.Native is null || asset.Data is null || string.IsNullOrWhiteSpace(asset.Id)
                || string.IsNullOrWhiteSpace(asset.Name) || !ids.Add(asset.Id))
                throw new InvalidDataException("Generated object asset metadata is invalid.");
            ValidateNative(asset.Native);
            if (catalog.ContainsInstalledPackage(asset.Native.PackageId) || options.ReservedPackageIds.Contains(asset.Native.PackageId)
                || !tags.Add(asset.TagHash) || compiled.Assets.ContainsKey(asset.TagHash)
                || !catalog.TryGetEntry(asset.SourceTag, out var source) || !catalog.ContainsInstalledPackage(source.PackageId))
                throw new InvalidDataException("Generated object assets overlap content or lost their source.");
        }
    }

    /** Raw deltas of one manifest; object edits never change the raw deltas they compile on. */
    private byte[] ReadRaw(ProjectManifest source, uint tag, PackageCatalog catalog)
    {
        if (!ReferenceEquals(source.Changes, manifest.Changes))
            throw new InvalidOperationException("Object edits compile on the saved raw deltas.");
        return ReadAsset(tag, catalog);
    }

    private IObjectEditCompiler RequireCompiler() => ObjectEditCompiler
        ?? throw new InvalidOperationException("This project has object edits. Open it in Sunrise Studio to read or build it.");

    /** The display name is not part of the edit. */
    private static bool Same(ObjectEdit? left, ObjectEdit? right) =>
        left is null || right is null ? left is null && right is null
            : JsonSerializer.Serialize(left with { Name = null }, ProjectJson.Options)
                == JsonSerializer.Serialize(right with { Name = null }, ProjectJson.Options);
}
