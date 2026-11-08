using SunriseStudio.Formats;
using SunriseStudio.Packages;

namespace SunriseStudio.Interchange;

public static partial class AssetBundle
{
    /** Candidate references remain labelled metadata; only typed edges expand the dependency group. */
    private static IReadOnlyList<BundleNativeAsset> ExportNative(PackageCatalog catalog, string root,
        IEnumerable<uint> seeds, AssetBundleExportOptions options, List<BundleNotice> notices,
        CancellationToken token, Action<string>? progress)
    {
        var queue = new Queue<uint>(seeds.Where(catalog.ContainsTag));
        var seen = new HashSet<uint>(); var output = new List<BundleNativeAsset>();
        var added = options.Project?.AddedAssets.Where(asset => asset.Native?.EntryIndex is not null)
            .ToDictionary(asset => TagHashes.Make(asset.Native!.PackageId, asset.Native.EntryIndex!.Value)) ?? [];
        while (queue.TryDequeue(out uint tag))
        {
            token.ThrowIfCancellationRequested();
            if (!seen.Add(tag) || !catalog.TryGetEntry(tag, out var entry)) continue;
            if (seen.Count > options.MaximumAssets) throw new InvalidDataException("Asset dependency group exceeds the requested limit.");
            progress?.Invoke($"Preserving connected asset {seen.Count}");
            byte[] bytes = catalog.Read(tag);
            var refs = NativeReferences(catalog, entry, bytes, token, notices);
            foreach (var link in refs.Where(value => value.Verified && value.TargetTag != tag)) queue.Enqueue(link.TargetTag);
            uint? baseTag = tag; string? baseHash = null; string? addedId = null;
            if (added.TryGetValue(tag, out var addition)) { baseTag = addition.BaseTagHash; addedId = addition.Id; }
            if (baseTag is uint baseline) baseHash = BundlePaths.Hash(catalog.ReadOriginal(baseline));
            string path = $"native/{tag:X8}.bin";
            Directory.CreateDirectory(Path.Combine(root, "native")); File.WriteAllBytes(BundlePaths.Resolve(root, path), bytes);
            output.Add(new(tag, options.NameResolver?.Invoke(tag) ?? catalog.GetName(tag) ?? AssetCategories.CategoryOf(entry), entry.Reference, entry.TypeInfo,
                entry.Type, entry.SubType, path, BundlePaths.Hash(bytes), baseTag, baseHash, addedId,
                refs.Distinct().ToArray()));
        }
        return output.OrderBy(asset => asset.TagHash).ToArray();
    }

    /** DDS keeps every native subresource; PNGs expose individually editable mip and slice images. */
    private static BundleTexture ExportTexture(PackageCatalog catalog, string root, uint tag,
        List<BundleNotice> notices, CancellationToken token)
    {
        string ddsPath = $"textures/{tag:X8}.dds";
        var images = new List<BundleImage>();
        try
        {
            var texture = new TextureDataReader(catalog).Read(tag);
            Directory.CreateDirectory(Path.Combine(root, "textures"));
            byte[] dds = DdsInterchange.Write(texture); File.WriteAllBytes(BundlePaths.Resolve(root, ddsPath), dds);
            string? failure = null;
            foreach (var sub in texture.Subresources)
                for (int depth = 0; depth < sub.Depth; depth++)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var image = BundleTextureCodec.Decode(texture, sub.MipLevel, sub.ArraySlice, depth);
                        string path = $"textures/{tag:X8}_m{sub.MipLevel}_s{sub.ArraySlice}_z{depth}.png";
                        byte[] png = PngInterchange.Write(image); File.WriteAllBytes(BundlePaths.Resolve(root, path), png);
                        images.Add(new(sub.MipLevel, sub.ArraySlice, depth, path, BundlePaths.Hash(png)));
                    }
                    catch (NotSupportedException error) { failure = error.Message; }
                }
            if (failure is not null) notices.Add(new("Texture", "DDS preserved", failure, tag));
            return new(tag, ddsPath, BundlePaths.Hash(dds), images, failure);
        }
        catch (Exception error) when (Recoverable(error))
        {
            notices.Add(new("Texture", "Native preserved", error.Message, tag));
            return new(tag, "", "", [], error.Message);
        }
    }
}
