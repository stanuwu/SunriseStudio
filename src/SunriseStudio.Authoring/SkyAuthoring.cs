using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/** A sky made from a stock one-row ring: its new sky-objects tag and every entry it needs. */
public sealed record PreparedSky(uint SkyObjects, IReadOnlyList<ProjectAssetAddition> Assets);

/**
 * Clones the stock horizon ring and retextures it. The sky objects, the model wrapper, the entity
 * model and the technique stay deltas of the stock tags; the texture and constants are new.
 */
public static class SkyAuthoring
{
    /** One row naming one entity model whose parts draw one technique with one sRGB texture. */
    public const uint TemplateSky = 0x80BEBE62;
    private const uint DomeTechnique = 0x80C44C62;
    private const string DomeVertexShader = "957868A3C55420BE5EDD88BF902E73480904080E5D5DC97A8885A6E8A1F64CBD";
    // Colour = texture(uv * c0.xy + c0.zw) * c1 * c3 * c4.x, alpha = c2.x * c5.x; stock c2.x is 0.
    private const string DomePixelShader = "10559AE5115F5BE9FCDDD753FACC479656DA186B7BC95326D5FA1DC7CF925339";
    private const int DomeConstantBytes = 96;
    // The dome adds to the sky; the stock gain of 122 draws any panorama as white.
    private const float DomeGain = 4;
    // The ring's U grows by 4 per turn. V runs from its top edge, 28 degrees up, to 3.5 below.
    private const float DomeTurnU = 4;
    /** Sky objects, wrapper, model, technique, constants header and payload, texture header and payload. */
    public const int EntryCount = 8;

    /**
     * The ring with `image` wrapped once around the horizon; black adds nothing to the sky.
     * @param image sRGB colour; it encodes as BC7 with a full mip chain.
     */
    public static PreparedSky PrepareDome(PackageCatalog catalog, RgbaImage image, string name, ushort package, int first,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (first < 0 || first > TagHashes.EntriesPerPackage - EntryCount) throw new InvalidDataException("The package has no room for a sky.");
        uint Tag(int index) => TagHashes.Make(package, first + index);
        byte[] sky = Require(catalog, TemplateSky, SSkyObjects.Class);
        var rows = TagRows.Writable(sky, in TagView.Checked<SSkyObjects>(sky, 0).Placements, UnknownSkyEntry.Class);
        if (rows.Length != 1) throw new InvalidDataException("The template sky has more than one row.");
        uint wrapperTag = rows[0].ModelWrapper;
        byte[] wrapper = Require(catalog, wrapperTag, SSkyModelWrapper.Class);
        uint modelTag = TagView.Member<SSkyModelWrapper, uint>(wrapper, 0, nameof(SSkyModelWrapper.Model));
        byte[] model = Require(catalog, modelTag, SEntityModel.Class);
        byte[] technique = Require(catalog, DomeTechnique, STechnique.Class);
        ref readonly var stages = ref TagView.Checked<STechnique>(technique, 0);
        Hash(catalog, stages.VertexShader.Shader, DomeVertexShader);
        Hash(catalog, stages.PixelShader.Shader, DomePixelShader);

        rows[0].ModelWrapper = Tag(1);
        // The stock row only blocks light shafts; a visible sky draws in the transparents stage.
        rows[0].LightShaftPass = 0;
        rows[0].LightShaftPassAlt = 0;
        rows[0].HiddenInTransparents = 0;
        TagView.Edit<uint>(wrapper, TagView.FieldOffset<SSkyModelWrapper>(nameof(SSkyModelWrapper.Model))) = Tag(2);
        int parts = 0;
        foreach (ref readonly var mesh in TagView.Rows(model, in TagView.Checked<SEntityModel>(model, 0).Meshes, SEntityModelMesh.Class))
            foreach (ref var part in TagRows.Writable(model, in mesh.Parts, SEntityModelPart.Class))
            {
                // Parts of other stages keep their stock techniques.
                if (part.Material != DomeTechnique) continue;
                part.Material = Tag(3); parts++;
            }
        if (parts == 0) throw new InvalidDataException("No dome part draws the dome technique.");
        var textureRows = TagRows.Writable(technique, in TagView.Checked<STechnique>(technique, 0).PixelShader.State.StaticTextures.Textures,
            SMaterialTextureAssignment.Class);
        if (textureRows.Length != 1 || textureRows[0].TextureSlot != 0) throw new InvalidDataException("The dome technique binds other textures.");
        var texture = TextureAssetImporter.PrepareEncoded(catalog, image, name + ".dome", package, first + 6, TextureEncoding.Bc7, true, token: token);
        textureRows[0].Texture = texture.HeaderTag;

        ref var pixel = ref TagView.Edit<STechnique>(technique).PixelShader;
        uint constantsTag = pixel.State.DynamicConstants.CbContainer;
        if (!catalog.TryGetEntry(constantsTag, out var constants) || !catalog.TryGetEntry(constants.Reference, out var payload)
            || payload.Size != DomeConstantBytes)
            throw new InvalidDataException("The dome technique has an unexpected constant buffer.");
        byte[] header = catalog.ReadOriginal(constantsTag);
        byte[] values = catalog.ReadOriginal(constants.Reference);
        // Register 0 is the UV scale and offset, 1 the colour gain and 2 X the opacity.
        var registers = MemoryMarshal.Cast<byte, Vector4>(values);
        registers[0] = new Vector4(1 / DomeTurnU, 1, 0, 0);
        registers[1] = new Vector4(DomeGain, DomeGain, DomeGain, registers[1].W);
        registers[2].X = 1;
        pixel.State.DynamicConstants.CbContainer = Tag(4);
        var skyEntry = Entry(catalog, TemplateSky);
        var wrapperEntry = Entry(catalog, wrapperTag);
        var modelEntry = Entry(catalog, modelTag);
        var techniqueEntry = Entry(catalog, DomeTechnique);
        ProjectAssetAddition[] assets = [
            new(name + ".sky", sky, new(package, skyEntry.Reference, skyEntry.TypeInfo, name, first), TemplateSky),
            new(name + ".wrapper", wrapper, new(package, wrapperEntry.Reference, wrapperEntry.TypeInfo, null, first + 1), wrapperTag),
            new(name + ".model", model, new(package, modelEntry.Reference, modelEntry.TypeInfo, null, first + 2), modelTag),
            new(name + ".material", technique, new(package, techniqueEntry.Reference, techniqueEntry.TypeInfo, null, first + 3), DomeTechnique),
            new(name + ".constants", header, new(package, Tag(5), constants.TypeInfo, null, first + 4), constantsTag),
            new(name + ".constants.bin", values, new(package, Tag(4), payload.TypeInfo, null, first + 5)),
            .. texture.Assets
        ];
        return new(Tag(0), assets);
    }

    /** The installed bytes of a stock tag of `expected` class. */
    private static byte[] Require(PackageCatalog catalog, uint tag, uint expected)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != expected || !catalog.ContainsInstalledPackage(entry.PackageId))
            throw new NotSupportedException($"The sky template tag {tag:X8} is not installed with class {expected:X8}.");
        return catalog.ReadOriginal(tag);
    }

    private static PackageEntry Entry(PackageCatalog catalog, uint tag) =>
        catalog.TryGetEntry(tag, out var entry) ? entry : throw new NotSupportedException($"The sky template tag {tag:X8} is not installed.");

    private static void Hash(PackageCatalog catalog, uint shader, string expected)
    {
        if (!catalog.TryGetEntry(shader, out var header)
            || Convert.ToHexString(SHA256.HashData(catalog.ReadOriginal(header.Reference))) != expected)
            throw new NotSupportedException("The installed dome shader differs from the checked one.");
    }

}
