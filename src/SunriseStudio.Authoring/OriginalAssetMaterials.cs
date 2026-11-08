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

/** Only the checked opaque shader profile receives translated glTF material inputs. */
internal static class OriginalAssetMaterials
{
    private const uint Template = 0x815B8158;
    // Entries: the technique, the constants header and data, then a header and data per texture.
    private const int EntryCount = 9;
    private const string PixelHash = "9E97A5ACE9FCD8D9C743E287D1C0682C94F0B9E0EB037EA32593DDF53DCE5E13";
    private const string VertexHash = "D67606C0E9766D10759890F5A32A555662887C25533D81A2951E2D4DEACD0D66";
    internal sealed record Prepared(uint MaterialTag, IReadOnlyList<ProjectAssetAddition> Assets);

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
            if (Text(material, "alphaMode", "OPAQUE") != "OPAQUE") issues.Add("alpha blending or masking");
            if (material.TryGetProperty("doubleSided", out var sided) && sided.GetBoolean()) issues.Add("double-sided shading");
            if (material.TryGetProperty("emissiveTexture", out _) || Vector(material, "emissiveFactor", 3, 0).Any(value => value != 0))
                issues.Add("emission");
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

    /** User pixels and constants are owned additions; material and resource headers remain deltas. */
    public static Prepared Prepare(PackageCatalog catalog, JsonElement document, int? materialIndex,
        IReadOnlyDictionary<int, RgbaImage> images, string name, ushort package, int first,
        bool skipUnsupported, CancellationToken token)
    {
        var material = materialIndex is int index ? Entry(document, "materials", index) : default;
        var pbr = Property(material, "pbrMetallicRoughness");
        float[] factor = Vector(pbr, "baseColorFactor", 4, 1);
        float metal = Unit(Number(pbr, "metallicFactor", 1)), rough = Unit(Number(pbr, "roughnessFactor", 1));
        foreach (float value in factor) Unit(value);
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
        float strength = Number(Property(material, "normalTexture"), "scale", 1);
        var color = Map(baseImage, [255, 255, 255, 255], (pixel, output) =>
        {
            for (int c = 0; c < 3; c++) output[c] = Byte(ToSrgb(ToLinear(pixel[c] / 255f) * factor[c]));
            output[3] = 255;
        }, token);
        var packed = Map(packedImage, [255, 255, 255, 255], (pixel, output) =>
        { output[0] = Byte(pixel[2] / 255f * metal); output[1] = Byte(1 - pixel[1] / 255f * rough); output[2] = 255; output[3] = 0; }, token);
        // glTF image V reverses the generated bitangent; its tangent normal Y must reverse too.
        // The shader rebuilds Z as positive, so a negative-Z texel reads as its mirror.
        var normalMap = Map(normalImage, [128, 128, 255, 255], (pixel, output) =>
        {
            double x = normalImage is null ? 0 : (pixel[0] / 255d * 2 - 1) * strength;
            double y = normalImage is null ? 0 : (pixel[1] / 255d * 2 - 1) * strength;
            double z = normalImage is null ? 1 : pixel[2] / 255d * 2 - 1;
            double length = Math.Sqrt(x * x + y * y + z * z);
            output[0] = Byte((float)((x / length + 1) / 2)); output[1] = Byte((float)((1 - y / length) / 2));
            output[2] = output[3] = 255;
        }, token);
        var templateEntry = Require(catalog, Template);
        if (templateEntry.Reference != STechnique.Class)
            throw new NotSupportedException("The installed opaque material template is not a technique.");
        byte[] native = catalog.ReadOriginal(Template);
        ref readonly var technique = ref TagView.Checked<STechnique>(native, 0);
        CheckShader(technique.VertexShader.Shader, VertexHash); CheckShader(technique.PixelShader.Shader, PixelHash);
        uint Tag(int offset) => TagHashes.Make(package, first + offset);
        if (first < 0 || first > TagHashes.EntriesPerPackage - EntryCount) throw new InvalidDataException("The package has no room for this material.");
        var textures = new[]
        {
            TextureAssetImporter.PrepareRgba(catalog, color, name + ".color", package, first + 3, srgb: true),
            TextureAssetImporter.PrepareRgba(catalog, packed, name + ".surface", package, first + 5, srgb: false),
            TextureAssetImporter.PrepareRgba(catalog, normalMap, name + ".normal", package, first + 7, srgb: false)
        };
        foreach (ref var row in TextureRows(native)) row.Texture = textures[row.TextureSlot].HeaderTag;
        ref var pixel = ref TagView.Edit<STechnique>(native).PixelShader;
        uint constantsTag = pixel.State.DynamicConstants.CbContainer;
        var constants = Require(catalog, constantsTag); var payload = Require(catalog, constants.Reference);
        byte[] constantsHeader = catalog.ReadOriginal(constantsTag);
        if (constants.Type != 32 || constants.SubType != 7 || payload.Type != 40 || payload.SubType != 7
            || !TagView.FitsThrough<ConstantBufferHeader>(constantsHeader, 0, nameof(ConstantBufferHeader.ByteCount))
            || TagView.At<uint>(constantsHeader, TagView.FieldOffset<ConstantBufferHeader>(nameof(ConstantBufferHeader.ByteCount))) != 48)
            throw new InvalidDataException("The opaque template has an unexpected constant buffer.");
        // The checked pixel shader reads three float4 registers; only these lanes are not zero.
        byte[] values = new byte[48];
        var registers = MemoryMarshal.Cast<byte, Vector4>(values);
        registers[1].X = 1;
        registers[2].X = 2;
        registers[2].Y = -1;
        pixel.State.DynamicConstants.CbContainer = Tag(1);
        List<ProjectAssetAddition> additions =
        [
            new(name + ".material", native, new(package, templateEntry.Reference, templateEntry.TypeInfo, name, first), Template),
            new(name + ".constants", constantsHeader, new(package, Tag(2), constants.TypeInfo, null, first + 1), constantsTag),
            new(name + ".constants.bin", values, new(package, Tag(1), payload.TypeInfo, null, first + 2))
        ];
        additions.AddRange(textures.SelectMany(texture => texture.Assets));
        return new(Tag(0), additions);

        void CheckShader(uint tag, string expected)
        {
            var shader = Require(catalog, tag);
            if (Convert.ToHexString(SHA256.HashData(catalog.ReadOriginal(shader.Reference))) != expected)
                throw new NotSupportedException("The installed opaque shader differs from the checked profile.");
        }
    }

    private delegate void PixelMap(ReadOnlySpan<byte> source, Span<byte> target);
    /** The pixel stage's texture rows: exactly three, one per slot 0, 1 and 2. */
    private static Span<SMaterialTextureAssignment> TextureRows(byte[] bytes)
    {
        ref readonly var field = ref TagView.Checked<STechnique>(bytes, 0).PixelShader.State.StaticTextures.Textures;
        long count = field.Count;
        var array = new TagArray(bytes, TagView.OffsetOf(bytes, in field));
        long header = array.HeaderOffset;
        if (count != 3 || header < 4 || header > bytes.Length - TagArray.HeaderSize - count * Unsafe.SizeOf<SMaterialTextureAssignment>()
            || !TagArray.MarkerAt(bytes, header) || TagRead.I64(bytes, (int)header) != count
            || array.ElementClass != SMaterialTextureAssignment.Class)
            throw new InvalidDataException("The opaque template has an invalid texture array.");
        var rows = MemoryMarshal.Cast<byte, SMaterialTextureAssignment>(
            bytes.AsSpan(array.DataOffset, (int)count * Unsafe.SizeOf<SMaterialTextureAssignment>()));
        uint[] slots = [rows[0].TextureSlot, rows[1].TextureSlot, rows[2].TextureSlot];
        if (!slots.Order().SequenceEqual(new uint[] { 0, 1, 2 }))
            throw new InvalidDataException("The opaque template has unexpected texture slots.");
        return rows;
    }
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
