using System.Security.Cryptography;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

public sealed record PreparedWeaponMaterials(IReadOnlyList<ProjectAssetAddition> Assets,
    IReadOnlyDictionary<int, uint> MaterialTags, int NextEntryIndex);

/** Weapon materials keep the opaque PBR pixel shader and use the checked matrix vertex shader. */
public static class WeaponAssetMaterialCompiler
{
    private const string VertexHash = "4D3706BDD7372EEDF5D57D289C1956F2C833DBBC2A2A7E2A0D2E9E4852B534B6";

    /** Only visual-part materials enter the rigid weapon shader path; particle materials remain separate. */
    public static PreparedWeaponMaterials PrepareUsed(PackageCatalog catalog, string path, string name,
        ushort packageId, int firstEntry, uint vertexShaderTag, IReadOnlyList<int> materialIndices)
    {
        RequireVertexShader(catalog, vertexShaderTag);
        using var document = GltfInterchange.ReadJson(path);
        var images = GltfInterchange.ReadImages(path).Where(image => image.MimeType == "image/png")
            .ToDictionary(image => image.Index, image => PngInterchange.Read(image.Bytes));
        var assets = new List<ProjectAssetAddition>();
        var tags = new Dictionary<int, uint>();
        int cursor = firstEntry;
        foreach (int index in materialIndices.Distinct())
        {
            if (index >= 0)
            {
                var unsupported = OriginalAssetMaterials.Unsupported(document.RootElement, index);
                if (unsupported.Count != 0)
                    throw new NotSupportedException("Weapon material cannot encode " + string.Join(", ", unsupported) + ".");
            }
            var prepared = OriginalAssetMaterials.Prepare(catalog, document.RootElement,
                index < 0 ? null : index, images, name + ".material" + tags.Count,
                packageId, cursor, false, CancellationToken.None);
            var material = prepared.Assets.Single(asset => asset.Native?.Reference == STechnique.Class);
            TagView.Edit<uint>(material.Data, TagView.FieldOffset<STechnique>("VertexShader.Shader")) = vertexShaderTag;
            tags.Add(index, prepared.MaterialTag);
            assets.AddRange(prepared.Assets);
            cursor = checked(cursor + prepared.Assets.Count);
        }
        return new(assets, tags, cursor);
    }

    private static void RequireVertexShader(PackageCatalog catalog, uint tag)
    {
        if (!catalog.TryGetEntry(tag, out var header) || header.Type != 33 || header.SubType != 1
            || Convert.ToHexString(SHA256.HashData(catalog.Read(header.Reference))) != VertexHash)
            throw new NotSupportedException("Weapon materials need the checked matrix vertex shader.");
    }
}
