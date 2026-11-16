using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/** glTF materials compile onto one checked stock technique per static profile. */
internal static class OriginalAssetMaterials
{
    /**
     * A checked stock technique: its tag, its shader hashes, and the texture slots its pixel stage binds.
     * @param ConstantBytes The constant buffer tag's size; 0 when the constants are the TFX template rows.
     */
    private sealed record Technique(uint Tag, string VertexHash, string PixelHash, uint[] Slots, int ConstantBytes);

    // G-buffer opaque: colour, surface and normal; constants: emission, mask exponent, normal decode.
    private static readonly Technique Opaque = new(0x815B8158,
        "D67606C0E9766D10759890F5A32A555662887C25533D81A2951E2D4DEACD0D66",
        "9E97A5ACE9FCD8D9C743E287D1C0682C94F0B9E0EB037EA32593DDF53DCE5E13", [0, 1, 2], 48);
    // Colour times vertex colour times constant 0; normal at slot 1, decoded by constant 1.
    private static readonly Technique VertexColor = new(0x815AFA16,
        "BB63C36058C9066366929319EDBD956BB944854EFD4EC9F54DD77A30C4DD907E",
        "3DDCED3BAB0047DB4B70C4E1E5DDFCAF3B256C5E3730105D3C67386CEED07CE5", [0, 1], 32);
    // Colour, mask and normal; discards where mask red is below template row 0 X.
    private static readonly Technique AlphaTest = new(0x815B1718,
        "D67606C0E9766D10759890F5A32A555662887C25533D81A2951E2D4DEACD0D66",
        "BF69C7E61A910F5F02568D9C1BA474FC8E48CEAE68F07F0AE1A9A920EC27FD47", [0, 1, 2], 0);
    // The alpha-test shadow caster stock meshes pair with AlphaTest: the mask at slot 0, the same cutoff.
    private static readonly Technique AlphaShadow = new(0x815B1715,
        "90ADC506620155C7FA2A84FF5C056F46327F2BD33B97A4D85F880BAC5B8F2940",
        "65B088B5C6A0E09A0E78D00A74EDDDFDA9D271999DD44C252DA0D0C980D51A80", [0], 0);
    // glTF's default alpha cutoff.
    private const float DefaultCutoff = 0.5f;
    // Every texture is a header entry and a payload entry.
    private const int TextureEntries = 2;

    internal sealed record Prepared(uint MaterialTag, uint ShadowTag, StaticProfile Profile, IReadOnlyList<ProjectAssetAddition> Assets);

    /** The profile a glTF material draws with: MASK is alpha-test, else vertex colours pick VertexColor. */
    public static StaticProfile ProfileOf(JsonElement document, int? materialIndex, bool vertexColors)
    {
        var material = materialIndex is int index ? Entry(document, "materials", index) : default;
        return Text(material, "alphaMode", "OPAQUE") == "MASK" ? StaticProfile.AlphaTest
            : vertexColors ? StaticProfile.VertexColor : StaticProfile.Opaque;
    }

    /** The project entries one material of `profile` uses. */
    public static int EntryCount(StaticProfile profile) => profile switch
    {
        StaticProfile.Opaque => 3 + 3 * TextureEntries,
        StaticProfile.VertexColor => 3 + 2 * TextureEntries,
        _ => 2 + 3 * TextureEntries
    };

    /**
     * Untranslated glTF material features need an explicit skip before import.
     * @param materialIndex Checks only this material; null checks them all.
     */
    public static IReadOnlyList<string> Unsupported(JsonElement document, int? materialIndex = null)
    {
        var issues = new HashSet<string>();
        if (!document.TryGetProperty("materials", out var materials)) return [];
        IEnumerable<JsonElement> selected = materialIndex is int index ? new[] { materials[index] } : materials.EnumerateArray();
        foreach (var material in selected)
        {
            if (material.TryGetProperty("extensions", out var extensions) && extensions.EnumerateObject().Any())
                issues.Add("material extensions");
            string mode = Text(material, "alphaMode", "OPAQUE");
            if (mode == "BLEND") issues.Add("alpha blending");
            if (material.TryGetProperty("doubleSided", out var sided) && sided.GetBoolean()) issues.Add("double-sided shading");
            bool emits = material.TryGetProperty("emissiveTexture", out _) || Vector(material, "emissiveFactor", 3, 0).Any(value => value != 0);
            if (emits && mode == "MASK") issues.Add("emission on an alpha-masked material");
            if (material.TryGetProperty("occlusionTexture", out _)) issues.Add("occlusion textures");
            foreach (var info in TextureInfos(material))
            {
                if (Int(info, "texCoord", 0) != 0) issues.Add("material UV sets other than UV0");
                if (info.TryGetProperty("extensions", out var transform) && transform.EnumerateObject().Any()) issues.Add("texture transforms");
                int texture = Int(info, "index", -1);
                var item = Entry(document, "textures", texture);
                if (item.TryGetProperty("extensions", out var compressed) && compressed.EnumerateObject().Any()) issues.Add("texture extensions");
                if (item.TryGetProperty("sampler", out var samplerIndex))
                {
                    var sampler = Entry(document, "samplers", samplerIndex.GetInt32());
                    if (Int(sampler, "wrapS", 10497) != 10497 || Int(sampler, "wrapT", 10497) != 10497) issues.Add("non-repeat texture wrapping");
                    if (Int(sampler, "magFilter", 9729) != 9729 || Int(sampler, "minFilter", 9987) is not (9729 or 9985 or 9987))
                        issues.Add("nearest texture filtering");
                }
            }
        }
        return issues.Order().ToArray();
    }

    /**
     * User pixels and constants are owned additions; technique and resource headers remain deltas.
     * @param share Materials this import already prepared; a match comes back with no assets.
     */
    public static Prepared Prepare(PackageCatalog catalog, JsonElement document, int? materialIndex, StaticProfile profile,
        IReadOnlyDictionary<int, RgbaImage> images, string name, ushort package, int first,
        bool skipUnsupported, bool compress, CancellationToken token, MaterialShare? share = null)
    {
        var inputs = Inputs.Read(document, materialIndex, images, skipUnsupported, token) with { Compress = compress };
        // Sharing stays inside one package.
        string? key = share is null ? null : $"{package:X4}:{inputs.Key(profile)}";
        if (key is not null && share!.TryGet(key, out var known)) return known with { Assets = [] };
        if (first < 0 || first > TagHashes.EntriesPerPackage - EntryCount(profile))
            throw new InvalidDataException("The package has no room for this material.");
        uint Tag(int offset) => TagHashes.Make(package, first + offset);
        var prepared = profile switch
        {
            StaticProfile.Opaque => PrepareOpaque(catalog, inputs, name, package, first, Tag, token),
            StaticProfile.VertexColor => PrepareVertexColor(catalog, inputs, name, package, first, Tag, token),
            _ => PrepareAlphaTest(catalog, inputs, name, package, first, Tag, token)
        };
        if (key is not null) share!.Stage(key, prepared);
        return prepared;
    }

    /** Technique, constants, then colour, surface and normal; emission is constant 0 masked by surface alpha. */
    private static Prepared PrepareOpaque(PackageCatalog catalog, Inputs inputs, string name, ushort package, int first,
        Func<int, uint> tag, CancellationToken token)
    {
        var (entry, native) = Load(catalog, Opaque);
        var textures = new[]
        {
            Texture(catalog, inputs, inputs.Color, name + ".color", package, first + 3, TextureEncoding.Bc7, true, token),
            Texture(catalog, inputs, inputs.Surface, name + ".surface", package, first + 5, TextureEncoding.Bc7, false, token),
            Texture(catalog, inputs, inputs.Normal, name + ".normal", package, first + 7, TextureEncoding.Bc7, false, token)
        };
        Bind(native, Opaque, textures.Select(x => x.HeaderTag).ToArray());
        // The checked pixel shader reads three float4 registers: emission, its mask exponent, normal decode.
        var registers = new Vector4[3];
        registers[0] = new(inputs.Emission, 0);
        registers[1].X = 1;
        registers[2] = new(2, -1, 0, 0);
        var constants = Constants(catalog, native, Opaque, registers, name, package, first, tag);
        List<ProjectAssetAddition> additions =
            [new(name + ".material", native, new(package, entry.Reference, entry.TypeInfo, name, first), Opaque.Tag), .. constants];
        additions.AddRange(textures.SelectMany(texture => texture.Assets));
        return new(tag(0), 0, StaticProfile.Opaque, additions);
    }

    /** Technique, constants, then colour and normal; the colour is already scaled by the base colour factor. */
    private static Prepared PrepareVertexColor(PackageCatalog catalog, Inputs inputs, string name, ushort package, int first,
        Func<int, uint> tag, CancellationToken token)
    {
        var (entry, native) = Load(catalog, VertexColor);
        var textures = new[]
        {
            Texture(catalog, inputs, inputs.Color, name + ".color", package, first + 3, TextureEncoding.Bc7, true, token),
            Texture(catalog, inputs, inputs.Normal, name + ".normal", package, first + 5, TextureEncoding.Bc7, false, token)
        };
        Bind(native, VertexColor, textures.Select(x => x.HeaderTag).ToArray());
        Vector4[] registers = [Vector4.One, new(2, -1, 0, 0)];
        var constants = Constants(catalog, native, VertexColor, registers, name, package, first, tag);
        List<ProjectAssetAddition> additions =
            [new(name + ".material", native, new(package, entry.Reference, entry.TypeInfo, name, first), VertexColor.Tag), .. constants];
        additions.AddRange(textures.SelectMany(texture => texture.Assets));
        return new(tag(0), 0, StaticProfile.VertexColor, additions);
    }

    /** Technique, colour, mask and normal, then the shadow technique that tests the same mask. */
    private static Prepared PrepareAlphaTest(PackageCatalog catalog, Inputs inputs, string name, ushort package, int first,
        Func<int, uint> tag, CancellationToken token)
    {
        var (entry, native) = Load(catalog, AlphaTest);
        var (shadowEntry, shadow) = Load(catalog, AlphaShadow);
        var textures = new[]
        {
            Texture(catalog, inputs, inputs.Color, name + ".color", package, first + 1, TextureEncoding.Bc7, true, token),
            Texture(catalog, inputs, inputs.Mask, name + ".mask", package, first + 3, TextureEncoding.Bc4, false, token),
            Texture(catalog, inputs, inputs.Normal, name + ".normal", package, first + 5, TextureEncoding.Bc7, false, token)
        };
        Bind(native, AlphaTest, textures.Select(x => x.HeaderTag).ToArray());
        Bind(shadow, AlphaShadow, [textures[1].HeaderTag]);
        SetCutoff(native, inputs.Cutoff);
        SetCutoff(shadow, inputs.Cutoff);
        List<ProjectAssetAddition> additions =
            [new(name + ".material", native, new(package, entry.Reference, entry.TypeInfo, name, first), AlphaTest.Tag)];
        additions.AddRange(textures.SelectMany(texture => texture.Assets));
        additions.Add(new(name + ".shadow", shadow, new(package, shadowEntry.Reference, shadowEntry.TypeInfo, null, first + 7), AlphaShadow.Tag));
        return new(tag(0), tag(7), StaticProfile.AlphaTest, additions);
    }

    /** The template's bytes after its class and both shader hashes check. */
    private static (PackageEntry Entry, byte[] Bytes) Load(PackageCatalog catalog, Technique technique)
    {
        var entry = Require(catalog, technique.Tag);
        if (entry.Reference != STechnique.Class)
            throw new NotSupportedException($"The installed material template {technique.Tag:X8} is not a technique.");
        byte[] native = catalog.ReadOriginal(technique.Tag);
        ref readonly var row = ref TagView.Checked<STechnique>(native, 0);
        CheckShader(catalog, row.VertexShader.Shader, technique.VertexHash);
        CheckShader(catalog, row.PixelShader.Shader, technique.PixelHash);
        return (entry, native);
    }

    private static void CheckShader(PackageCatalog catalog, uint tag, string expected)
    {
        var shader = Require(catalog, tag);
        if (Convert.ToHexString(SHA256.HashData(catalog.ReadOriginal(shader.Reference))) != expected)
            throw new NotSupportedException("An installed material shader differs from the checked profile.");
    }

    /** Points each pixel-stage texture row at the texture for its slot. */
    private static void Bind(byte[] native, Technique technique, uint[] textures)
    {
        foreach (ref var row in SlotRows(native, technique.Slots)) row.Texture = textures[Array.IndexOf(technique.Slots, row.TextureSlot)];
    }

    /** The opaque template's pixel-stage texture rows: exactly one each for slots 0, 1 and 2. */
    private static Span<SMaterialTextureAssignment> TextureRows(byte[] bytes) => SlotRows(bytes, Opaque.Slots);

    /** The pixel-stage texture rows, which must be exactly one per slot of `slots`. */
    private static Span<SMaterialTextureAssignment> SlotRows(byte[] bytes, uint[] slots)
    {
        ref readonly var field = ref TagView.Checked<STechnique>(bytes, 0).PixelShader.State.StaticTextures.Textures;
        var array = new TagArray(bytes, TagView.OffsetOf(bytes, in field));
        if (!array.IsWellFormed || array.ElementClass != SMaterialTextureAssignment.Class)
            throw new InvalidDataException("The material template has an invalid texture array.");
        var rows = TagRows.Writable(bytes, in field, SMaterialTextureAssignment.Class);
        if (!rows.ToArray().Select(row => row.TextureSlot).Order().SequenceEqual(slots))
            throw new InvalidDataException("The material template has unexpected texture slots.");
        return rows;
    }

    /** A new constant buffer header at entry 1 and payload at entry 2; the technique's pixel stage points at the header. */
    private static ProjectAssetAddition[] Constants(PackageCatalog catalog, byte[] native, Technique technique, Vector4[] registers,
        string name, ushort package, int first, Func<int, uint> tag)
    {
        ref var pixel = ref TagView.Edit<STechnique>(native).PixelShader;
        uint constantsTag = pixel.State.DynamicConstants.CbContainer;
        var constants = Require(catalog, constantsTag); var payload = Require(catalog, constants.Reference);
        byte[] header = catalog.ReadOriginal(constantsTag);
        if (constants.Type != 32 || constants.SubType != 7 || payload.Type != 40 || payload.SubType != 7
            || !TagView.FitsThrough<ConstantBufferHeader>(header, 0, nameof(ConstantBufferHeader.ByteCount))
            || TagView.At<uint>(header, TagView.FieldOffset<ConstantBufferHeader>(nameof(ConstantBufferHeader.ByteCount))) != technique.ConstantBytes
            || registers.Length * Unsafe.SizeOf<Vector4>() != technique.ConstantBytes)
            throw new InvalidDataException($"The material template {technique.Tag:X8} has an unexpected constant buffer.");
        pixel.State.DynamicConstants.CbContainer = tag(1);
        byte[] values = MemoryMarshal.AsBytes(registers.AsSpan()).ToArray();
        return [
            new(name + ".constants", header, new(package, tag(2), constants.TypeInfo, null, first + 1), constantsTag),
            new(name + ".constants.bin", values, new(package, tag(1), payload.TypeInfo, null, first + 2))
        ];
    }

    /** Sets the cutoff in TFX template row 0 X, which both alpha-test pixel shaders compare the mask against. */
    private static void SetCutoff(byte[] native, float cutoff)
    {
        ref readonly var field = ref TagView.Checked<STechnique>(native, 0).PixelShader.State.DynamicConstants.CbTemplate;
        var rows = TagRows.Writable(native, in field, ExpressionConstant.Class);
        if (rows.Length == 0) throw new InvalidDataException("The alpha-test template has no constant rows.");
        rows[0].Value.X = cutoff;
    }

    private static PreparedTexture Texture(PackageCatalog catalog, Inputs inputs, RgbaImage image, string name, ushort package, int index,
        TextureEncoding encoding, bool srgb, CancellationToken token) =>
        TextureAssetImporter.PrepareEncoded(catalog, image, name, package, index, inputs.Compress ? encoding : TextureEncoding.Rgba8, srgb, token: token);


    /** The glTF material translated to texture pixels and constants every profile draws from. */
    private sealed record Inputs(RgbaImage Color, RgbaImage Surface, RgbaImage Normal, RgbaImage Mask, Vector3 Emission, float Cutoff)
    {
        /** False writes every texture as RGBA8. */
        public bool Compress { get; init; } = true;

        /** A hash of everything a material of `profile` is built from. */
        public string Key(StaticProfile profile)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            float[] values = [(int)profile, Compress ? 1 : 0, Emission.X, Emission.Y, Emission.Z, Cutoff];
            hash.AppendData(MemoryMarshal.AsBytes(values.AsSpan()));
            foreach (var image in new[] { Color, Surface, Normal, Mask })
            {
                int[] size = [image.Width, image.Height];
                hash.AppendData(MemoryMarshal.AsBytes(size.AsSpan()));
                hash.AppendData(image.Pixels);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }

        public static Inputs Read(JsonElement document, int? materialIndex, IReadOnlyDictionary<int, RgbaImage> images,
            bool skipUnsupported, CancellationToken token)
        {
            var material = materialIndex is int index ? Entry(document, "materials", index) : default;
            var pbr = Property(material, "pbrMetallicRoughness");
            float[] factor = Vector(pbr, "baseColorFactor", 4, 1);
            float metal = Unit(Number(pbr, "metallicFactor", 1)), rough = Unit(Number(pbr, "roughnessFactor", 1));
            foreach (float value in factor) Unit(value);
            float[] emissive = Vector(material, "emissiveFactor", 3, 0);
            foreach (float value in emissive) Unit(value);
            float cutoff = Unit(Number(material, "alphaCutoff", DefaultCutoff));
            RgbaImage? Texture(JsonElement owner, string field)
            {
                if (!owner.TryGetPropertySafe(field, out var info)) return null;
                if (Int(info, "texCoord", 0) != 0 || info.TryGetProperty("extensions", out _))
                {
                    if (skipUnsupported) return null;
                    throw new NotSupportedException("This material needs unsupported texture coordinates.");
                }
                var texture = Entry(document, "textures", Int(info, "index", -1));
                int image = Int(texture, "source", -1);
                if (images.TryGetValue(image, out var pixels)) return pixels;
                if (skipUnsupported) return null;
                throw new NotSupportedException("Original material textures require PNG images.");
            }
            var baseImage = Texture(pbr, "baseColorTexture");
            var packedImage = Texture(pbr, "metallicRoughnessTexture");
            var normalImage = Texture(material, "normalTexture");
            var emissiveImage = Texture(material, "emissiveTexture");
            float strength = Number(Property(material, "normalTexture"), "scale", 1);
            byte Tinted(ReadOnlySpan<byte> texel, int channel) => Byte(ToSrgb(ToLinear(texel[channel] / 255f) * factor[channel]));
            var color = Map(baseImage, [255, 255, 255, 255], (pixel, output) =>
                Rgba(output, Tinted(pixel, 0), Tinted(pixel, 1), Tinted(pixel, 2), 255), token);
            var mask = Map(baseImage, [255, 255, 255, 255], (pixel, output) =>
                output[0] = output[1] = output[2] = output[3] = Byte(pixel[3] / 255f * factor[3]), token);
            // Surface alpha is the emission mask: the largest linear channel of the emissive texture.
            bool emits = emissive.Any(value => value != 0);
            var surface = Map(packedImage, [255, 255, 255, 255], (pixel, output) =>
                Rgba(output, Byte(pixel[2] / 255f * metal), Byte(1 - pixel[1] / 255f * rough), 255, 0), token);
            if (emits) surface = Masked(surface, emissiveImage);
            // glTF image V reverses the generated bitangent; its tangent normal Y must reverse too.
            // The shader rebuilds Z as positive, so a negative-Z texel reads as its mirror.
            var normal = Map(normalImage, [128, 128, 255, 255], (pixel, output) =>
            {
                double x = normalImage is null ? 0 : (pixel[0] / 255d * 2 - 1) * strength;
                double y = normalImage is null ? 0 : (pixel[1] / 255d * 2 - 1) * strength;
                double z = normalImage is null ? 1 : pixel[2] / 255d * 2 - 1;
                double length = Math.Sqrt(x * x + y * y + z * z);
                Rgba(output, Byte((float)((x / length + 1) / 2)), Byte((float)((1 - y / length) / 2)), 255, 255);
            }, token);
            return new(color, surface, normal, mask, new(emissive[0], emissive[1], emissive[2]), cutoff);
        }

        /** The surface map with alpha set from `emission`, sampled nearest at the surface map's size; no texture is a full mask. */
        private static RgbaImage Masked(RgbaImage surface, RgbaImage? emission)
        {
            int width = Math.Max(surface.Width, emission?.Width ?? 1), height = Math.Max(surface.Height, emission?.Height ?? 1);
            var output = new byte[checked(width * height * 4)];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int target = (y * width + x) * 4;
                    int s = (y * surface.Height / height * surface.Width + x * surface.Width / width) * 4;
                    surface.Pixels.AsSpan(s, 3).CopyTo(output.AsSpan(target, 3));
                    if (emission is null) { output[target + 3] = 255; continue; }
                    int e = (y * emission.Height / height * emission.Width + x * emission.Width / width) * 4;
                    float value = Math.Max(ToLinear(emission.Pixels[e] / 255f), Math.Max(ToLinear(emission.Pixels[e + 1] / 255f), ToLinear(emission.Pixels[e + 2] / 255f)));
                    output[target + 3] = Byte(value);
                }
            return new(width, height, output);
        }
    }

    private delegate void PixelMap(ReadOnlySpan<byte> source, Span<byte> target);
    /** Writes one RGBA8 pixel. */
    private static void Rgba(Span<byte> target, byte r, byte g, byte b, byte a) => (target[0], target[1], target[2], target[3]) = (r, g, b, a);
    private static RgbaImage Map(RgbaImage? source, byte[] fallback, PixelMap mapping, CancellationToken token)
    {
        int width = source?.Width ?? 1, height = source?.Height ?? 1;
        byte[] input = source?.Pixels ?? fallback, output = new byte[checked(width * height * 4)];
        for (int index = 0; index < output.Length; index += 4)
        { if ((index & 0xFFFF) == 0) token.ThrowIfCancellationRequested(); mapping(input.AsSpan(index, 4), output.AsSpan(index, 4)); }
        return new(width, height, output);
    }
    private static IEnumerable<JsonElement> TextureInfos(JsonElement material)
    {
        foreach (string name in new[] { "normalTexture", "occlusionTexture", "emissiveTexture" })
            if (material.TryGetProperty(name, out var info)) yield return info;
        if (material.TryGetProperty("pbrMetallicRoughness", out var pbr))
            foreach (string name in new[] { "baseColorTexture", "metallicRoughnessTexture" })
                if (pbr.TryGetProperty(name, out var info)) yield return info;
    }
    private static JsonElement Entry(JsonElement document, string array, int index)
    {
        if (index < 0 || !document.TryGetProperty(array, out var values) || values.ValueKind != JsonValueKind.Array || index >= values.GetArrayLength())
            throw new InvalidDataException($"Invalid glTF {array} index.");
        return values[index];
    }
    private static JsonElement Property(JsonElement value, string name) => value.TryGetPropertySafe(name, out var property) ? property : default;
    private static bool TryGetPropertySafe(this JsonElement value, string name, out JsonElement property)
    { property = default; return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out property); }
    private static int Int(JsonElement value, string name, int fallback) => value.TryGetPropertySafe(name, out var property) ? property.GetInt32() : fallback;
    private static string Text(JsonElement value, string name, string fallback) => value.TryGetPropertySafe(name, out var property) ? property.GetString() ?? fallback : fallback;
    private static float Number(JsonElement value, string name, float fallback)
    {
        float result = value.TryGetPropertySafe(name, out var property) ? property.GetSingle() : fallback;
        return float.IsFinite(result) ? result : throw new InvalidDataException("Material value is not finite.");
    }
    private static float[] Vector(JsonElement value, string name, int count, float fallback)
    {
        if (!value.TryGetPropertySafe(name, out var property)) return Enumerable.Repeat(fallback, count).ToArray();
        float[] result = property.EnumerateArray().Select(component => component.GetSingle()).ToArray();
        if (result.Length != count || result.Any(component => !float.IsFinite(component))) throw new InvalidDataException("Material vector is invalid.");
        return result;
    }
    private static float Unit(float value) => value is >= 0 and <= 1 ? value : throw new InvalidDataException("Material factor must be in 0..1.");
    private static PackageEntry Require(PackageCatalog catalog, uint tag) => catalog.TryGetEntry(tag, out var entry)
        ? entry : throw new NotSupportedException($"Required material template entry {tag:X8} is not installed.");
    private static byte Byte(float value) => checked((byte)MathF.Round(Math.Clamp(value, 0, 1) * 255));
    private static float ToLinear(float value) => value <= .04045f ? value / 12.92f : MathF.Pow((value + .055f) / 1.055f, 2.4f);
    private static float ToSrgb(float value) => value <= .0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1 / 2.4f) - .055f;
}
