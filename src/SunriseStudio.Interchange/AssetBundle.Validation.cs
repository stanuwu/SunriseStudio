using System.Buffers.Binary;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Interchange;

public static partial class AssetBundle
{
    /** Manifests may name only bounded, distinct files inside their own directory. */
    private static void ValidateManifest(AssetBundleManifest manifest, string root)
    {
        if (manifest.Format != Format || manifest.Version != 1 || string.IsNullOrWhiteSpace(manifest.Name)
            || manifest.NativeAssets is null || manifest.Meshes is null || manifest.Materials is null
            || manifest.Textures is null || manifest.Shaders is null || manifest.Animations is null || manifest.Notices is null
            || manifest.NativeAssets.Count is < 1 or > TagHashes.EntriesPerPackage)
            throw new InvalidDataException("Unsupported asset bundle manifest.");
        var assets = Unique(manifest.NativeAssets, value => value.TagHash);
        if (!assets.ContainsKey(manifest.RootTag) || !assets.ContainsKey(manifest.SelectedTag))
            throw new InvalidDataException("Bundle root is missing from its native sources.");
        _ = Unique(manifest.Meshes, value => value.Id);
        _ = Unique(manifest.Materials, value => value.TagHash);
        _ = Unique(manifest.Textures, value => value.TagHash);
        _ = Unique(manifest.Shaders, value => value.HeaderTag);
        _ = Unique(manifest.Animations, value => value.ClipTag);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void PathEntry(string? relative, string? hash = null)
        {
            if (relative is null) return;
            string full = BundlePaths.Resolve(root, relative);
            if (!paths.Add(full) || !File.Exists(full)) throw new InvalidDataException("Bundle file is missing or repeated.");
            if (hash is not null && (hash.Length != 64 || hash.Any(value => !Uri.IsHexDigit(value))))
                throw new InvalidDataException("Bundle hash is invalid.");
        }
        foreach (var asset in manifest.NativeAssets)
        {
            PathEntry(asset.Path, asset.Sha256);
            if (asset.References is null || asset.References.Any(value => value is null || value.Offset < -1))
                throw new InvalidDataException("Bundle dependency metadata is invalid.");
        }
        foreach (var mesh in manifest.Meshes)
        {
            PathEntry(mesh.Path, mesh.Sha256);
            if (!assets.ContainsKey(mesh.ModelTag) || mesh.Transform is null || mesh.Transform.Length != 16
                || mesh.Transform.Any(value => !float.IsFinite(value))) throw new InvalidDataException("Mesh source or transform is invalid.");
        }
        foreach (var material in manifest.Materials) { PathEntry(material.Path); Require(material.TagHash); }
        foreach (var texture in manifest.Textures)
        {
            Require(texture.TagHash);
            if (texture.Images is null) throw new InvalidDataException("Texture image list is missing.");
            if (!string.IsNullOrEmpty(texture.DdsPath)) PathEntry(texture.DdsPath, texture.DdsSha256);
            foreach (var image in texture.Images)
            {
                if (image is null || image.Mip < 0 || image.Slice < 0 || image.DepthSlice < 0)
                    throw new InvalidDataException("Texture subresource is invalid.");
                PathEntry(image.Path, image.Sha256);
            }
        }
        foreach (var shader in manifest.Shaders)
        { Require(shader.HeaderTag); Require(shader.BytecodeTag); PathEntry(shader.Path, shader.Sha256); PathEntry(shader.DisassemblyPath); }
        foreach (var clip in manifest.Animations)
        { Require(clip.ClipTag); PathEntry(clip.ControlsPath, clip.ControlsSha256); PathEntry(clip.PosePath); }
        PathEntry(manifest.ScenePath); PathEntry(manifest.ParticleMetadataPath);
        void Require(uint tag) { if (!assets.ContainsKey(tag)) throw new InvalidDataException("Converted asset is outside the native group."); }
    }

    /** Only verified native reference fields may point at cloned dependency entries. */
    private static PreparedAssetBundleImport PrepareClone(StudioProject project, PackageCatalog catalog,
        AssetBundleManifest manifest, Dictionary<uint, byte[]> baseline, Dictionary<uint, byte[]> edits,
        AssetBundleImportOptions options, string fingerprint, List<BundleNotice> notices)
    {
        if (!PackageWriter.IsNewPackageId(options.PackageId))
            throw new InvalidDataException($"Choose a package ID from {PackageWriter.NewPackageIdRange}.");
        if (catalog.Entries.Any(value => value.PackageId == options.PackageId))
            throw new InvalidDataException("New asset packages cannot replace an installed package.");
        int first = project.AddedAssets.Where(value => value.Native?.PackageId == options.PackageId && value.Native.EntryIndex is not null)
            .Select(value => value.Native!.EntryIndex!.Value + 1).DefaultIfEmpty(0).Max();
        if (first + manifest.NativeAssets.Count > TagHashes.EntriesPerPackage) throw new InvalidDataException("The target package has no room for this dependency group.");
        var remap = manifest.NativeAssets.Select((asset, index) => (asset.TagHash,
            NewTag: TagHashes.Make(options.PackageId, first + index)))
            .ToDictionary(value => value.TagHash, value => value.NewTag);
        var additions = new List<ProjectAssetAddition>();
        NotSupportedException Unsupported(uint tag, string detail)
        {
            bool entity = catalog.TryGetEntry(manifest.RootTag, out var root) && root.Reference == SEntity.Class;
            var error = new NotSupportedException(entity
                ? "This entity cannot be copied as a new asset yet. Use Apply deltas."
                : "This asset cannot be copied as a new asset yet. Use Apply deltas.");
            error.Data["SourceTag"] = tag.ToString("X8"); error.Data["Detail"] = detail;
            return error;
        }
        foreach (var asset in manifest.NativeAssets)
        {
            byte[] bytes = edits.GetValueOrDefault(asset.TagHash, baseline[asset.TagHash]).ToArray();
            if (!catalog.TryGetEntry(asset.TagHash, out var sourceEntry)) throw new InvalidDataException("Clone source is missing.");
            var validation = new List<BundleNotice>();
            var references = NativeReferences(catalog, sourceEntry, baseline[asset.TagHash], default, validation);
            if (validation.Count != 0) throw Unsupported(asset.TagHash, validation[0].Message);
            if (references.Any(value => value.Verified && !remap.ContainsKey(value.TargetTag)))
                throw new InvalidDataException("The bundle omits a verified native dependency.");
            var fields = references.Where(value => value.Verified).GroupBy(value => value.Offset).ToArray();
            foreach (var group in fields)
                if (group.Select(value => value.TargetTag).Distinct().Count() != 1)
                    throw new InvalidDataException("Native reference fields disagree.");
            var verified = fields.ToDictionary(value => value.Key, value => value.First());
            foreach (var candidate in references.Where(value => !value.Verified && remap.ContainsKey(value.TargetTag)))
                if (!verified.TryGetValue(candidate.Offset, out var typed) || typed.TargetTag != candidate.TargetTag)
                    throw Unsupported(asset.TagHash, $"Dependency at {candidate.Offset:X} has no verified native field.");
            uint reference = asset.Reference;
            foreach (var field in verified.Values)
            {
                if (field.Offset == -1)
                {
                    if (reference != field.TargetTag || asset.Type is not (32 or 33 or 34 or 40 or 41 or 42 or 48))
                        throw new InvalidDataException("Metadata payload reference changed.");
                    if (remap.TryGetValue(reference, out uint replacement)) reference = replacement;
                    continue;
                }
                if (field.Offset > bytes.Length - 4 || field.Offset > baseline[asset.TagHash].Length - 4
                    || BinaryPrimitives.ReadUInt32LittleEndian(baseline[asset.TagHash].AsSpan(field.Offset)) != field.TargetTag)
                    throw new InvalidDataException("Native reference offset does not match the source.");
                uint current = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(field.Offset));
                if (remap.TryGetValue(current, out uint mapped)) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(field.Offset), mapped);
                else if (current is not (0 or uint.MaxValue) && !catalog.ContainsTag(current))
                    throw new InvalidDataException("Edited native dependency does not exist.");
            }
            string name = asset.TagHash == manifest.RootTag && !string.IsNullOrWhiteSpace(options.Name)
                ? options.Name.Trim() : asset.Name;
            additions.Add(new(name, bytes, new(options.PackageId, reference, asset.TypeInfo, name,
                first + additions.Count), asset.BaseTagHash));
        }
        notices.Add(new("New asset", "Derived group", "Root and dependencies get new tags together."));
        return new(AssetBundleImportMode.NewAsset, remap[manifest.RootTag], fingerprint,
            new Dictionary<uint, byte[]>(), new Dictionary<string, byte[]>(), additions, notices)
        { SourceFingerprints = manifest.NativeAssets.ToDictionary(value => value.TagHash, value => value.Sha256) };
    }
}
