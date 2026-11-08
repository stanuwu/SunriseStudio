using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Interchange;

public static partial class AssetBundle
{
    /** All source identities and edits are checked before any project file is written. */
    public static PreparedAssetBundleImport PrepareImport(StudioProject project, PackageCatalog catalog,
        string manifestPath, AssetBundleImportOptions options, CancellationToken token = default,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        string path = Path.GetFullPath(manifestPath), root = Path.GetDirectoryName(path)!;
        if (Path.GetFileName(path) == EditsName) path = Path.Combine(root, ManifestName);
        BundlePaths.RejectLinks(path);
        var manifest = BundlePaths.Read<AssetBundleManifest>(path);
        if (manifest.Context?.Guardian is not null)
            throw new InvalidDataException("Guardian exports are view only.");
        ValidateManifest(manifest, root);
        string fingerprint = BundlePaths.FileHash(project.ManifestPath);
        var assets = manifest.NativeAssets.ToDictionary(value => value.TagHash);
        var baseline = new Dictionary<uint, byte[]>();
        var addedByTag = project.AddedAssets.Where(value => value.Native?.EntryIndex is not null)
            .ToDictionary(value => NativeTag(value.Native!), value => value);
        foreach (var asset in manifest.NativeAssets)
        {
            token.ThrowIfCancellationRequested();
            if (!catalog.TryGetEntry(asset.TagHash, out var entry) || entry.Reference != asset.Reference
                || entry.TypeInfo != asset.TypeInfo || entry.Type != asset.Type || entry.SubType != asset.SubType)
                throw new InvalidDataException($"Source metadata changed: {asset.TagHash:X8}.");
            uint? expectedBase = asset.TagHash;
            string? expectedId = null;
            if (addedByTag.TryGetValue(asset.TagHash, out var added))
            { expectedBase = added.BaseTagHash; expectedId = added.Id; }
            if (asset.BaseTagHash != expectedBase || asset.AddedAssetId != expectedId)
                throw new InvalidDataException("Bundle provenance does not match the current project.");
            byte[] bytes = catalog.Read(asset.TagHash);
            if (BundlePaths.Hash(bytes) != asset.Sha256 || expectedBase is uint baseTag
                && BundlePaths.Hash(catalog.ReadOriginal(baseTag)) != asset.BaseSha256)
                throw new InvalidDataException($"Source asset changed: {asset.TagHash:X8}. Export it again.");
            baseline.Add(asset.TagHash, bytes);
        }
        var edits = new Dictionary<uint, byte[]>();
        void Merge(uint tag, byte[] bytes)
        {
            if (!baseline.TryGetValue(tag, out var source))
                throw new InvalidDataException($"Edited asset {tag:X8} is outside the exported dependency group.");
            if (source.AsSpan().SequenceEqual(bytes)) return;
            if (!edits.TryGetValue(tag, out var prior)) { edits.Add(tag, bytes.ToArray()); return; }
            if (prior.AsSpan().SequenceEqual(bytes)) return;
            if (bytes.Length != source.Length || prior.Length != source.Length)
                throw new InvalidDataException("Two edits disagree on a resized native asset.");
            for (int index = 0; index < bytes.Length; index++)
            {
                if (bytes[index] == source[index]) continue;
                if (prior[index] != source[index] && prior[index] != bytes[index])
                    throw new InvalidDataException($"Edits conflict in {tag:X8} at byte {index:X}.");
                prior[index] = bytes[index];
            }
        }
        void MergeAll(IReadOnlyDictionary<uint, byte[]> values)
        { foreach (var value in values) Merge(value.Key, value.Value); }
        foreach (var asset in manifest.NativeAssets)
        {
            token.ThrowIfCancellationRequested();
            Merge(asset.TagHash, File.ReadAllBytes(BundlePaths.Resolve(root, asset.Path)));
        }
        AssetBundleEdits? working = null;
        string editsPath = Path.Combine(root, EditsName);
        if (File.Exists(editsPath))
        {
            working = BundlePaths.Read<AssetBundleEdits>(editsPath);
            if (working.Format != "sunrise-asset-bundle-edits" || working.Version != 1
                || working.SourceManifestSha256 != BundlePaths.FileHash(path)
                || working.Meshes is null || working.Materials is null || working.Textures is null || working.Animations is null)
                throw new InvalidDataException("Edited bundle does not match its source manifest.");
        }
        progress?.Invoke("Validating geometry edits");
        if (working?.Meshes.Count > 0)
        {
            var meshes = manifest.Meshes.ToDictionary(value => value.Id);
            var scene = new AssetSceneLoader(catalog) { IncludeLowerLods = true }.Load(manifest.RootTag, token);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edit in working.Meshes)
            {
                token.ThrowIfCancellationRequested();
                if (edit is null || !seen.Add(edit.Id) || !meshes.TryGetValue(edit.Id, out var info))
                    throw new InvalidDataException("A mesh was added, duplicated or lost its source identity.");
                string sourcePath = BundlePaths.Resolve(root, info.Path);
                if (BundlePaths.FileHash(sourcePath) != info.Sha256)
                    throw new InvalidDataException("Baseline geometry changed. Export edits through the Blender bundle tool.");
                var geometry = BundlePaths.Read<BundleGeometry>(sourcePath);
                var original = scene.Instances.FirstOrDefault(value => value.Mesh.TagHash == info.ModelTag
                    && value.Mesh.PartIndex == info.PartIndex && value.ModelSourceConfigTag == info.ConfigTag)?.Mesh
                    ?? throw new InvalidDataException("Native mesh source no longer resolves.");
                if (edits.ContainsKey(info.ModelTag) || info.ConfigTag != 0 && edits.ContainsKey(info.ConfigTag)
                    || original.Native is { } native && (edits.ContainsKey(native.IndexHeaderTag)
                        || native.VertexStreams.Any(value => edits.ContainsKey(value.HeaderTag))))
                    throw new NotSupportedException("Import geometry and raw native layout changes in separate transactions.");
                MergeAll(BundleMeshCodec.Apply(catalog, original, geometry, edit));
            }
            if (seen.Count != meshes.Count) throw new InvalidDataException("Removing native mesh parts is not supported.");
        }
        progress?.Invoke("Validating materials and textures");
        var materialEdits = Unique(working?.Materials ?? [], value => value.TagHash);
        foreach (var material in manifest.Materials)
        {
            string relative = materialEdits.Remove(material.TagHash, out var change) ? change.Path : material.Path;
            string materialPath = BundlePaths.Resolve(root, relative);
            var document = BundlePaths.Read<MaterialDocument>(materialPath);
            if (document.TagHash != material.TagHash) throw new InvalidDataException("Material edit changed its identity.");
            MergeAll(MaterialInterchange.Import(catalog, materialPath, importTextures: false));
        }
        if (materialEdits.Count != 0) throw new InvalidDataException("Material edit is outside the bundle.");
        var textureEdits = working?.Textures ?? [];
        if (textureEdits.Any(value => value is null) || textureEdits.GroupBy(value => (value.TagHash, value.Mip, value.Slice, value.DepthSlice)).Any(group => group.Count() != 1))
            throw new InvalidDataException("Texture edits contain duplicate subresources.");
        var knownTextures = manifest.Textures.Select(value => value.TagHash).ToHashSet();
        if (textureEdits.Any(value => !knownTextures.Contains(value.TagHash))) throw new InvalidDataException("Texture edit is outside the bundle.");
        foreach (var texture in manifest.Textures)
        {
            token.ThrowIfCancellationRequested();
            var explicitEdits = textureEdits.Where(value => value.TagHash == texture.TagHash).ToArray();
            if (texture.DdsPath.Length == 0 && explicitEdits.Length == 0) continue;
            var original = new TextureDataReader(catalog).Read(texture.TagHash);
            string ddsPath = texture.DdsPath.Length == 0 ? "" : BundlePaths.Resolve(root, texture.DdsPath);
            if (ddsPath.Length != 0 && BundlePaths.FileHash(ddsPath) != texture.DdsSha256)
                MergeAll(TextureEdits.Apply(catalog, texture.TagHash, DdsInterchange.Read(File.ReadAllBytes(ddsPath))));
            var images = new List<BundleTextureEdit>();
            foreach (var image in texture.Images)
                if (BundlePaths.FileHash(BundlePaths.Resolve(root, image.Path)) != image.Sha256)
                    images.Add(new(texture.TagHash, image.Path, image.Mip, image.Slice, image.DepthSlice));
            foreach (var edit in explicitEdits)
                if (!images.Contains(edit)) images.Add(edit);
            foreach (var edit in images)
            {
                string imagePath = BundlePaths.Resolve(root, edit.Path);
                byte[] bytes = File.ReadAllBytes(imagePath);
                TextureData replacement = Path.GetExtension(imagePath).ToLowerInvariant() switch
                {
                    ".png" => BundleTextureCodec.Apply(original, PngInterchange.Read(bytes), edit.Mip, edit.Slice, edit.DepthSlice),
                    ".dds" => DdsInterchange.Read(bytes),
                    _ => throw new InvalidDataException("Texture edits need PNG or DDS files.")
                };
                MergeAll(TextureEdits.Apply(catalog, texture.TagHash, replacement));
            }
        }
        progress?.Invoke("Validating animation and particle edits");
        var animationEdits = Unique(working?.Animations ?? [], value => value.ClipTag);
        foreach (var animation in manifest.Animations)
        {
            token.ThrowIfCancellationRequested();
            string? relative = animationEdits.Remove(animation.ClipTag, out var edit) ? edit.Path : animation.ControlsPath;
            if (relative is null) continue;
            string controls = BundlePaths.Resolve(root, relative);
            if (relative == animation.ControlsPath && BundlePaths.FileHash(controls) == animation.ControlsSha256) continue;
            BundlePaths.ValidateGltf(controls);
            Merge(animation.ClipTag, AnimationAssetImporter.PrepareGltf(catalog, animation.ClipTag, controls).Data);
        }
        if (animationEdits.Count != 0) throw new InvalidDataException("Animation edit is outside the bundle.");
        if (working?.Emitters is { Count: > 0 } emitterEdits)
        {
            if (manifest.ParticleMetadataPath is null) throw new InvalidDataException("Bundle has no editable particle emitters.");
            var particleData = BundlePaths.Read<BundleParticles>(BundlePaths.Resolve(root, manifest.ParticleMetadataPath));
            var allowed = particleData.Emitters.Select(value => value.TagHash).ToHashSet();
            if (emitterEdits.Any(value => value is null || !allowed.Contains(value.TagHash)))
                throw new InvalidDataException("Emitter edit is outside the bundle.");
            MergeAll(BundleParticlesCodec.Import(catalog, emitterEdits));
        }
        foreach (var shader in manifest.Shaders)
        {
            string shaderPath = BundlePaths.Resolve(root, shader.Path);
            if (BundlePaths.FileHash(shaderPath) != shader.Sha256)
                throw new NotSupportedException("Edited DXBC sidecars need native shader compilation and reflection validation; import native payload edits explicitly.");
        }
        var collision = PrepareCollision(project, catalog, manifest, root, working?.Collision, token, progress);
        if (options.Mode == AssetBundleImportMode.NewAsset && (collision.ObjectEdits.Count > 0 || collision.Assets.Count > 0
            || collision.AddedShapes.Count > 0))
            throw new NotSupportedException("Collision edits import as deltas only. Choose Apply deltas.");
        if (fingerprint != BundlePaths.FileHash(project.ManifestPath))
            throw new InvalidOperationException("Project changed while the bundle was being prepared.");
        var notices = manifest.Notices.ToList();
        notices.AddRange(collision.Notes.Select(note => new BundleNotice("Collision", "Imported", note)));
        if (options.Mode == AssetBundleImportMode.NewAsset)
            return PrepareClone(project, catalog, manifest, baseline, edits, options, fingerprint, notices);
        if (options.Mode != AssetBundleImportMode.ApplyDeltas) throw new InvalidDataException("Unknown bundle import mode.");
        KeepObjectValuesOut(project, catalog, edits);
        // Collision bytes start from the raw deltas already, so they skip the object value rebase.
        foreach (var (tag, bytes) in collision.Assets)
            if (!edits.TryAdd(tag, bytes)) throw new NotSupportedException($"Import collision and raw edits of {tag:X8} separately.");
        var existingEdits = new Dictionary<uint, byte[]>(); var addedEdits = new Dictionary<string, byte[]>();
        foreach (var edit in edits)
            if (addedByTag.TryGetValue(edit.Key, out var added)) addedEdits.Add(added.Id, edit.Value);
            else existingEdits.Add(edit.Key, edit.Value);
        return new(options.Mode, manifest.RootTag, fingerprint, existingEdits, addedEdits, [], notices)
        {
            SourceFingerprints = manifest.NativeAssets.ToDictionary(value => value.TagHash, value => value.Sha256)
                .Concat(collision.SourceFingerprints).GroupBy(pair => pair.Key).ToDictionary(group => group.Key, group => group.First().Value),
            ObjectEdits = collision.ObjectEdits, CollisionShapes = collision.Additions
        };
    }

    /** The collision section's edits, planned against the collision the current project holds. */
    private static CollisionImportPlan PrepareCollision(StudioProject project, PackageCatalog catalog, AssetBundleManifest manifest,
        string root, BundleCollisionEdits? edits, CancellationToken token, Action<string>? progress)
    {
        if (edits is null || (edits.Placements?.Count ?? 0) == 0 && (edits.Shapes?.Count ?? 0) == 0 && (edits.Added?.Count ?? 0) == 0)
            return new([], new Dictionary<uint, byte[]>(), []);
        progress?.Invoke("Validating collision edits");
        if (manifest.CollisionPath is null) throw new InvalidDataException("The bundle has no collision section.");
        string path = BundlePaths.Resolve(root, manifest.CollisionPath);
        if (BundlePaths.FileHash(path) != manifest.CollisionSha256)
            throw new InvalidDataException("The collision section changed on disk. Export the bundle again.");
        var document = BundlePaths.Read<BundleCollision>(path);
        var scene = new AssetSceneLoader(catalog).Load(manifest.RootTag, token);
        var baseline = new CollisionSceneLoader(catalog).Load(scene, token);
        var added = project.AddedAssets.Where(value => value.Native?.EntryIndex is not null)
            .ToDictionary(value => NativeTag(value.Native!), value => value.Id);
        byte[] ReadRaw(uint tag) => added.TryGetValue(tag, out var id) ? project.ReadAddedAsset(id, catalog) : project.ReadAsset(tag, catalog);
        return CollisionImport.Plan(catalog, baseline, BundleCollisionCodec.Input(document, edits), ReadRaw,
            CollisionImport.ShapePackage(project, catalog), project.Changes.Select(change => change.TagHash).Concat(added.Keys).ToArray());
    }

    /** The prepared project snapshot must still match at the transaction boundary. */
    public static AssetBundleImportResult ApplyImport(StudioProject project, PackageCatalog catalog,
        PreparedAssetBundleImport prepared)
    {
        if (BundlePaths.FileHash(project.ManifestPath) != prepared.ProjectFingerprint)
            throw new InvalidOperationException("Project changed after import preparation. Prepare the bundle again.");
        foreach (var source in prepared.SourceFingerprints)
            if (BundlePaths.Hash(catalog.Read(source.Key)) != source.Value)
                throw new InvalidOperationException("Source asset changed after import preparation.");
        if (prepared.Mode == AssetBundleImportMode.NewAsset)
        {
            var added = project.AddAssets(prepared.Additions, catalog);
            string? rootId = added.FirstOrDefault(value => value.Native is not null && NativeTag(value.Native) == prepared.RootTag)?.Id;
            return new(prepared.RootTag, rootId, 0, added, prepared.Notices);
        }
        IReadOnlyList<AddedProjectAsset> created = [];
        if (prepared.ObjectEdits.Count == 0 && prepared.CollisionShapes.Count == 0) project.ApplyEdits(prepared.Edits, catalog, prepared.AddedEdits);
        else
        {
            var compiler = project.ObjectEditCompiler;
            if (compiler is null && prepared.ObjectEdits.Count > 0)
                throw new InvalidOperationException("Collision edits need the object edit compiler.");
            byte[] ReadStaged(uint tag) => prepared.Edits.TryGetValue(tag, out var bytes) ? bytes : project.ReadAsset(tag, catalog);
            var states = prepared.ObjectEdits.ToDictionary(edit => edit.Key,
                edit => compiler!.Normalize(ObjectEdit.Merge(project.FindObjectEdit(edit.Key), edit), ReadStaged, catalog), StringComparer.Ordinal);
            created = project.RestoreTransaction(new(prepared.Edits, prepared.AddedEdits), new Dictionary<string, AddedAssetSnapshot?>(), catalog,
                states.Count > 0 ? states : null, prepared.CollisionShapes);
        }
        string? existingId = project.AddedAssets.FirstOrDefault(value => value.Native?.EntryIndex is not null
            && NativeTag(value.Native) == prepared.RootTag)?.Id;
        return new(prepared.RootTag, existingId, prepared.Edits.Count + prepared.AddedEdits.Count, created, prepared.Notices);
    }

    /**
     * Bundles hold compiled bytes. A byte left as compiled takes its raw value, so a raw delta never
     * holds an object edit value, and the object edit still wins where both write.
     */
    private static void KeepObjectValuesOut(StudioProject project, PackageCatalog catalog, Dictionary<uint, byte[]> edits)
    {
        if (project.ObjectEdits.Count == 0 || edits.Count == 0) return;
        ObjectEditCompilation compiled;
        // Edits that do not compile are not in the catalog view either, so the bundle holds raw bytes.
        try { compiled = project.CompileObjectEdits(catalog); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException
            or InvalidOperationException or KeyNotFoundException) { return; }
        foreach (uint tag in edits.Keys.ToArray())
        {
            if (!compiled.Assets.TryGetValue(tag, out var objects)) continue;
            byte[] edited = edits[tag], raw = project.ReadAsset(tag, catalog);
            if (edited.Length != objects.Length || raw.Length != objects.Length)
                throw new NotSupportedException($"Object edits write {tag:X8}, and the bundle changes its size. "
                    + "Revert those object edits before importing it.");
            for (int index = 0; index < edited.Length; index++)
                if (edited[index] == objects[index]) edited[index] = raw[index];
            if (edited.AsSpan().SequenceEqual(raw)) edits.Remove(tag);
        }
    }

    private static Dictionary<TKey, TValue> Unique<TValue, TKey>(IEnumerable<TValue> values, Func<TValue, TKey> key) where TKey : notnull
    {
        var output = new Dictionary<TKey, TValue>();
        foreach (var value in values)
            if (value is null || !output.TryAdd(key(value), value)) throw new InvalidDataException("Bundle repeats an edit identity.");
        return output;
    }

    private static uint NativeTag(NativeAssetMetadata value) => TagHashes.Make(value.PackageId, value.EntryIndex!.Value);
}
