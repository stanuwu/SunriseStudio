using SunriseStudio.Tags.Generated;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

public sealed record MaterialSurface(uint TagHash, string Name, GameMaterialInfo Material,
    MaterialExecutionData? Execution, string? Error);
public sealed record ShaderSurface(uint TagHash, string Name, GameShaderInfo Shader,
    byte[] Bytecode, string Sha256)
{
    public GameShaderStage Stage => Shader.Stage;
    public string Profile => Shader.Profile;
    public string Disassembly => Shader.Disassembly;
    public IReadOnlyList<string> Declarations => Shader.Declarations;
}

/** Surface data is prepared on a worker and contains no GPU resources or inferred texture roles. */
public sealed class AssetSurface
{
    public uint TagHash { get; }
    public IReadOnlyList<MaterialSurface> Materials { get; }
    public IReadOnlyDictionary<uint, TextureData> Textures { get; }
    public IReadOnlyDictionary<uint, string> TextureNames { get; }
    public IReadOnlyDictionary<uint, string> TextureErrors { get; }
    public IReadOnlyList<ShaderSurface> Shaders { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public bool HasContent => Materials.Count != 0 || Textures.Count != 0 || Shaders.Count != 0;

    private AssetSurface(uint tag, List<MaterialSurface> materials, Dictionary<uint, TextureData> textures,
        Dictionary<uint, string> textureNames, Dictionary<uint, string> textureErrors,
        List<ShaderSurface> shaders, List<string> diagnostics)
    {
        TagHash = tag;
        Materials = materials.AsReadOnly();
        Textures = new ReadOnlyDictionary<uint, TextureData>(new Dictionary<uint, TextureData>(textures));
        TextureNames = new ReadOnlyDictionary<uint, string>(new Dictionary<uint, string>(textureNames));
        TextureErrors = new ReadOnlyDictionary<uint, string>(new Dictionary<uint, string>(textureErrors));
        Shaders = shaders.AsReadOnly();
        Diagnostics = diagnostics.AsReadOnly();
    }

    /** A copied texture dictionary keeps thumbnails alive when a native plan releases its CPU cache. */
    public static AssetSurface Read(PackageCatalog catalog, uint tag, SceneDocument? scene = null,
        Func<uint, string?>? nameResolver = null, CancellationToken token = default,
        IReadOnlyDictionary<uint, TextureData>? decodedTextures = null)
    {
        if (!catalog.TryGetEntry(tag, out var entry)) throw new KeyNotFoundException("Asset is not in the catalog.");
        var reader = new GameMaterialReader(catalog);
        var textureReader = new TextureDataReader(catalog);
        List<MaterialSurface> materials = [];
        Dictionary<uint, TextureData> textures = [];
        Dictionary<uint, string> textureNames = [], textureErrors = [];
        List<ShaderSurface> shaders = [];
        List<string> diagnostics = [];
        HashSet<uint> materialTags = [], shaderTags = [], textureTags = [];
        string Name(uint asset, string kind) => nameResolver?.Invoke(asset) ?? catalog.GetName(asset) ?? $"{kind} {asset:X8}";
        void Shader(GameShaderInfo shader)
        {
            if (!shaderTags.Add(shader.HeaderTag)) return;
            token.ThrowIfCancellationRequested();
            byte[] bytes = reader.ReadShaderBytecode(shader);
            shaders.Add(new(shader.HeaderTag, Name(shader.HeaderTag, shader.Stage + " shader"),
                shader, bytes, Convert.ToHexString(SHA256.HashData(bytes))));
        }
        void ShaderTag(uint shaderTag)
        {
            if (shaderTags.Contains(shaderTag)) return;
            try
            {
                if (!catalog.TryGetEntry(shaderTag, out var header) || header.Type != 33)
                    throw new NotSupportedException("Open a shader header for stage inspection.");
                var stage = header.SubType switch
                {
                    0 => GameShaderStage.Pixel, 1 => GameShaderStage.Vertex,
                    2 => GameShaderStage.Geometry, 6 => GameShaderStage.Compute,
                    _ => throw new NotSupportedException("This shader header subtype is not decoded.")
                };
                Shader(reader.ReadShader(shaderTag, stage));
            }
            catch (Exception error) when (Recoverable(error))
            { diagnostics.Add($"{Name(shaderTag, "Shader")}: {error.Message}"); }
        }
        void Material(uint materialTag)
        {
            if (!materialTags.Add(materialTag)) return;
            token.ThrowIfCancellationRequested();
            try
            {
                var material = reader.Read(materialTag);
                MaterialExecutionData? execution = null;
                string? error = null;
                try { execution = reader.ReadExecution(materialTag); }
                catch (Exception failure) when (Recoverable(failure)) { error = failure.Message; }
                materials.Add(new(materialTag, Name(materialTag, "Material"), material, execution, error));
                foreach (var shader in new[] { material.VertexShader, material.PixelShader, material.ComputeShader })
                    if (shader is not null) Shader(shader);
                foreach (var shader in material.AdditionalShaderHeaders) ShaderTag(shader.Tag);
                foreach (var texture in material.Textures) textureTags.Add(texture.TextureTag);
                if (error is not null) diagnostics.Add($"{Name(materialTag, "Material")}: {error}");
                diagnostics.AddRange(material.Diagnostics);
            }
            catch (Exception error) when (Recoverable(error))
            { diagnostics.Add($"{Name(materialTag, "Material")}: {error.Message}"); }
        }

        token.ThrowIfCancellationRequested();
        if (entry.Type is 8 or 16 && entry.Reference == STechnique.Class) Material(tag);
        if (entry.Type == 32 && entry.SubType is >= 1 and <= 3) textureTags.Add(tag);
        if (entry.Type == 33) ShaderTag(tag);
        if (entry.Type is 40 or 41)
        {
            foreach (var header in catalog.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (header.Type != entry.Type - 8 || header.SubType != entry.SubType || header.Reference != tag) continue;
                if (header.Type == 33) ShaderTag(header.TagHash);
                else if (header.SubType is >= 1 and <= 3) textureTags.Add(header.TagHash);
            }
        }
        if (entry.Type == 48)
            diagnostics.Add("Open the texture header to view it.");
        if (scene is not null)
        {
            foreach (uint material in scene.Instances.Select(x => x.Mesh.MaterialTag).Where(x => x is not (0 or uint.MaxValue)).Distinct())
                Material(material);
            foreach (uint dye in scene.Instances.Select(x => x.Mesh.Native)
                .Where(x => x?.Kind == NativeMeshKind.Terrain).Select(x => x!.TerrainDyeTexture).Distinct())
                if (catalog.TryGetEntry(dye, out var textureEntry) && textureEntry.Type == 32
                    && textureEntry.SubType is >= 1 and <= 3) textureTags.Add(dye);
        }
        if (decodedTextures is not null) textureTags.UnionWith(decodedTextures.Keys);
        foreach (uint texture in textureTags.Order())
        {
            token.ThrowIfCancellationRequested();
            textureNames[texture] = Name(texture, "Texture");
            try
            {
                textures[texture] = decodedTextures is not null && decodedTextures.TryGetValue(texture, out var reused)
                    ? reused : textureReader.Read(texture);
            }
            catch (Exception error) when (Recoverable(error))
            {
                textureErrors[texture] = error.Message;
                diagnostics.Add($"{textureNames[texture]}: {error.Message}");
            }
        }
        return new(tag, materials, textures, textureNames, textureErrors, shaders, diagnostics.Distinct().ToList());
    }

    private static bool Recoverable(Exception error) =>
        error is InvalidDataException or NotSupportedException or ArgumentException or OverflowException
            or KeyNotFoundException or IOException or SharpGen.Runtime.SharpGenException
        && !error.Message.StartsWith("Package block authentication failed", StringComparison.Ordinal);
}
