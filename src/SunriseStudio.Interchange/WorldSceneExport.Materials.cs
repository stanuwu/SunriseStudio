using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;

namespace SunriseStudio.Interchange;

public static partial class WorldSceneExport
{
    /**
     * One material per key; a key with shader inputs previews with the part's dye bit, dye buffers and textures.
     * A composed plate atlas is written once, as PNG, however many materials bind it.
     */
    private static (List<WorldMaterial> Materials, List<WorldTexture> Textures) Materials(PackageCatalog catalog,
        IReadOnlyList<MaterialKey> keys, IReadOnlyDictionary<string, BundleShaderInputs> shaderInputs, string root, bool enabled,
        List<BundleNotice> notices, CancellationToken token, Action<string>? progress)
    {
        List<WorldMaterial> output = []; HashSet<uint> textures = [];
        var composed = new Dictionary<uint, TextureData>();
        var inputs = enabled ? MaterialPreviewAnalyzer.CreateInputs(catalog) : null;
        var native = new Dictionary<uint, MaterialDocument>();
        var previews = new Dictionary<(uint Tag, uint Dye, string Inputs), BundleMaterialPreview>();
        var formats = new Dictionary<uint, int>();
        var reader = new GameMaterialReader(catalog); var stateWords = new Dictionary<uint, uint>();
        var volumeAtlases = new Dictionary<uint, (string Path, string Sha256)?>();
        foreach (var key in keys)
        {
            uint tag = key.Tag; int stage = key.Stage;
            token.ThrowIfCancellationRequested();
            string name = catalog.GetName(tag) ?? $"Material {tag:X8}";
            int id = output.Count;
            if (!enabled || tag is 0 or uint.MaxValue)
            { output.Add(new(id, tag, stage, name, null, null, false, "Opaque")); continue; }
            progress?.Invoke($"Preparing world material {id + 1}/{keys.Count}");
            var part = key.Inputs.Length == 0 ? null : shaderInputs[key.Inputs];
            if (part is not null) name += $" ({part.Label}{(part.DyeBit != 0 ? ", dye 1" : "")})";
            bool publishing = false;
            try
            {
                if (!native.TryGetValue(tag, out var document))
                {
                    document = MaterialInterchange.Describe(catalog, tag); native.Add(tag, document);
                }
                var overrides = new List<MaterialPreviewAnalyzer.TextureInput>();
                if (key.TerrainDye is not (0 or uint.MaxValue))
                    overrides.Add(new(14, key.TerrainDye, PackageFormat(catalog, key.TerrainDye, formats)));
                foreach (var (slot, texture) in part?.Textures ?? new Dictionary<uint, uint>())
                    overrides.Add(new(slot, texture, part!.ComposedTextures.TryGetValue(texture, out var atlas)
                        ? atlas.Format : PackageFormat(catalog, texture, formats)));
                foreach (var value in overrides)
                    document = document with { Slots = document.Slots.Where(slot => slot.Stage != "Pixel" || slot.Slot != value.Slot)
                        .Append(new("Pixel", value.Slot, -1, value.Tag, value.Tag)).ToArray() };
                if (!previews.TryGetValue((tag, key.TerrainDye, key.Inputs), out var preview))
                {
                    var stages = keys.Where(value => value.Tag == tag && value.TerrainDye == key.TerrainDye && value.Inputs == key.Inputs)
                        .Select(value => value.Stage).ToArray();
                    inputs!.Vectors[MaterialPreviewAnalyzer.GearDyeBit] = new Vector4(part?.DyeBit ?? 0);
                    inputs.ObjectChannels = part?.ObjectChannels;
                    try
                    {
                        preview = MaterialPreviewAnalyzer.Read(catalog, tag, inputs, stages, overrides, part?.ConstantBuffers, part?.ForcedBlend);
                    }
                    finally { inputs.Vectors[MaterialPreviewAnalyzer.GearDyeBit] = Vector4.Zero; inputs.ObjectChannels = null; }
                    if (overrides.Count != 0) preview = preview with { SlotTextures = overrides.ToDictionary(value => value.Slot, value => value.Tag) };
                    preview = AssetBundle.WithVolumeAtlases(catalog, root, preview, volumeAtlases, notices);
                    previews.Add((tag, key.TerrainDye, key.Inputs), preview);
                }
                string blend = preview.StageBlendModes?.GetValueOrDefault(stage) ?? preview.BlendMode;
                if (preview.BlendVariants?.TryGetValue(blend, out var variant) == true) preview = variant;
                preview = preview with { BlendMode = blend, BlendVariants = null, StageBlendModes = null };
                bool needsBasis = NeedsBasis(preview);
                if (!stateWords.TryGetValue(tag, out uint states)) stateWords.Add(tag, states = reader.Read(tag).StateOverrides);
                string path = $"materials/{id:D5}.json";
                publishing = true;
                WriteJson(BundlePaths.Resolve(root, path), new WorldMaterialDocument(document, preview));
                output.Add(new(id, tag, stage, name, path, FileHash(BundlePaths.Resolve(root, path)), needsBasis,
                    preview.Mode == "No pixel shader" ? "NoColor" : blend)
                    {
                        TextureOverrides = overrides.Count == 0 ? null : overrides.ToDictionary(value => value.Slot, value => value.Tag),
                        BackfaceCulling = MaterialPreviewAnalyzer.CullsClockwise(states, stage)
                    });
                foreach (uint texture in document.Slots.Where(slot => slot.Stage == "Pixel").Select(slot => slot.TextureTag))
                    if (part?.ComposedTextures.TryGetValue(texture, out var atlas) == true) composed.TryAdd(texture, atlas);
                    else if (texture is not (0 or uint.MaxValue)) textures.Add(texture);
                if (preview.Notes.Contains(MaterialPreviewAnalyzer.UnresolvedBlendNote))
                    notices.Add(new("Material", "Left out", MaterialPreviewAnalyzer.UnresolvedBlendNote, tag));
            }
            catch (Exception error) when (!publishing && Recoverable(error))
            {
                output.Add(new(id, tag, stage, name, null, null, false, "Opaque"));
                notices.Add(new("Material", "Preview unavailable", error.Message, tag));
            }
        }
        List<WorldTexture> images = [];
        foreach (uint tag in textures.Except(composed.Keys).Order())
        {
            token.ThrowIfCancellationRequested();
            progress?.Invoke($"Converting world texture {images.Count + 1}/{textures.Count}");
            bool publishing = false;
            try
            {
                var texture = new TextureDataReader(catalog).Read(tag);
                if (texture.Kind != TextureKind.Texture2D || texture.ArraySize != 1 || texture.Depth != 1)
                    throw new NotSupportedException("World material preview needs a single 2D texture.");
                var image = BundleTextureCodec.Decode(texture);
                byte[] png = PngInterchange.Write(image);
                publishing = true;
                images.Add(WriteTexture(root, tag, png, image.Width, image.Height, texture.Format));
            }
            catch (Exception error) when (!publishing && Recoverable(error)) { notices.Add(new("Texture", "Preview unavailable", error.Message, tag)); }
        }
        // A plate atlas exists only in the draw plan, so only its first mip is written.
        foreach (var (tag, atlas) in composed.OrderBy(pair => pair.Key))
        {
            var image = BundleTextureCodec.Decode(atlas);
            images.Add(WriteTexture(root, tag, PngInterchange.Write(image), image.Width, image.Height, atlas.Format));
        }
        return (output, images);
    }

    private static WorldTexture WriteTexture(string root, uint tag, byte[] png, int width, int height, int format)
    {
        string path = $"textures/{tag:X8}.png";
        string full = BundlePaths.Resolve(root, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, png);
        return new(tag, path, BundlePaths.Hash(png), width, height, format);
    }

    /** The DXGI format in a 2D texture's 40-byte header. */
    private static int PackageFormat(PackageCatalog catalog, uint tag, Dictionary<uint, int> formats)
    {
        if (formats.TryGetValue(tag, out int format)) return format;
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Type != 32 || entry.SubType != 1)
            throw new InvalidDataException($"Texture binding 0x{tag:X8} does not reference a 2D texture.");
        byte[] header = catalog.Read(tag);
        if (header.Length != Unsafe.SizeOf<TextureHeader>()) throw new InvalidDataException($"Texture 0x{tag:X8} has an invalid header.");
        formats.Add(tag, format = checked((int)MemoryMarshal.Read<TextureHeader>(header).Format));
        return format;
    }

    /** Native world-basis attributes need each placement's actual object transform. */
    private static bool NeedsBasis(BundleMaterialPreview preview)
    {
        if (preview.Recipe is not null || preview.Palette is not null || preview.BlendMode == "NoColor"
            || preview.Program is not { } program || !program.Outputs.TryGetValue("Normal", out var normal)) return false;
        HashSet<int> visited = []; var pending = new Stack<int>(normal);
        while (pending.TryPop(out int index))
        {
            if ((uint)index >= program.Nodes.Count) throw new InvalidDataException("Material preview node index is invalid.");
            if (!visited.Add(index)) continue;
            var node = program.Nodes[index];
            if (node.Operation is "basis" or "frontface") return true;
            foreach (int input in node.Inputs) pending.Push(input);
        }
        return false;
    }
}
