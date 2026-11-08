using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using Vortice.D3DCompiler;
using Vortice.DXGI;

namespace SunriseStudio.Rendering;

public enum GameShaderStage { Pixel = 0, Vertex = 1, Geometry = 2, Hull = 3, Domain = 4, Compute = 5 }

public sealed record ShaderTextureSlot(uint Slot, string Dimension);
public sealed record AdditionalShaderHeader(int FieldOffset, uint Tag, int PackageSubtype);

public sealed record GameShaderInfo(uint HeaderTag, uint BytecodeTag, GameShaderStage Stage,
    string Profile, string Disassembly, IReadOnlyList<ShaderTextureSlot> TextureSlots,
    IReadOnlyList<string> Declarations);

public sealed record MaterialTextureBinding(GameShaderStage Stage, uint Slot, uint TextureTag,
    bool DeclaredByShader, string? ShaderDimension, int Width, int Height, int Depth,
    int ArraySize, Format Format)
{
    public bool IsSrgb => Format is Format.R8G8B8A8_UNorm_SRgb or Format.B8G8R8A8_UNorm_SRgb
        or Format.B8G8R8X8_UNorm_SRgb or Format.BC1_UNorm_SRgb or Format.BC2_UNorm_SRgb
        or Format.BC3_UNorm_SRgb or Format.BC7_UNorm_SRgb;
    public bool IsPreviewCandidate => Stage == GameShaderStage.Pixel && DeclaredByShader
        && ShaderDimension == "texture2d" && Depth <= 1 && ArraySize <= 1;
}

public sealed record GameMaterialInfo(uint Tag, GameShaderInfo? VertexShader,
    GameShaderInfo? PixelShader, GameShaderInfo? ComputeShader,
    IReadOnlyList<MaterialTextureBinding> Textures, IReadOnlyList<string> Diagnostics)
{
    public IReadOnlyList<AdditionalShaderHeader> AdditionalShaderHeaders { get; init; } = [];
    public uint StateOverrides { get; init; }
    /** Rows with tag 0xFFFFFFFF; the native binder sets a null view at these slots. */
    public IReadOnlySet<(GameShaderStage Stage, uint Slot)> EmptyTextureSlots { get; init; } =
        new HashSet<(GameShaderStage, uint)>();
    public MaterialTextureBinding? PreviewTexture => Textures.Where(x => x.IsPreviewCandidate)
        .OrderByDescending(x => x.IsSrgb).ThenBy(x => x.Slot).FirstOrDefault();
}

/** Texture slots are checked against native DXBC declarations; color-map purpose stays unknown. */
public sealed partial class GameMaterialReader(PackageCatalog catalog)
{
    private readonly Dictionary<uint, GameMaterialInfo> materials = [];
    private readonly Dictionary<uint, GameShaderInfo> shaders = [];

    /** Stage fields are specific to the installed pre-Beyond-Light material class. */
    public GameMaterialInfo Read(uint materialTag)
    {
        if (materials.TryGetValue(materialTag, out var cached)) return cached;
        if (!catalog.TryGetEntry(materialTag, out var entry) || entry.Reference != STechnique.Class)
            throw new NotSupportedException($"Tag 0x{materialTag:X8} is not a supported material.");
        byte[] bytes = catalog.Read(materialTag);
        if (!TagView.FitsThrough<STechnique>(bytes, 0, "ComputeShader.State.StaticSamplers.Samplers")
            || Technique<ulong>(bytes, nameof(STechnique.TagSize)) != (ulong)bytes.Length)
            throw new InvalidDataException("Material size does not match its header.");
        var textures = new List<MaterialTextureBinding>();
        var emptySlots = new HashSet<(GameShaderStage, uint)>();
        var diagnostics = new List<string>();
        GameShaderInfo? ReadStage(GameShaderStage stage, string field)
        {
            uint shaderTag = Technique<uint>(bytes, $"{field}.Shader");
            GameShaderInfo? shader = null;
            if (shaderTag is not (0 or uint.MaxValue))
            {
                try { shader = ReadShader(shaderTag, stage); }
                catch (Exception error) when ((error is IOException or InvalidDataException or ArgumentException
                    or NotSupportedException or KeyNotFoundException or SharpGen.Runtime.SharpGenException)
                    && !error.Message.StartsWith("Package block authentication failed", StringComparison.Ordinal))
                { diagnostics.Add($"{stage} shader 0x{shaderTag:X8}: {error.Message}"); }
            }
            foreach (var row in TextureRows(bytes,
                in Technique<TagArray<SMaterialTextureAssignment>>(bytes, $"{field}.State.StaticTextures.Textures")))
            {
                uint slot = row.TextureSlot, textureTag = row.Texture;
                if (slot >= 128) throw new InvalidDataException("Texture slot exceeds the DX11 limit.");
                if (textureTag is 0 or uint.MaxValue)
                {
                    if (textureTag == uint.MaxValue) emptySlots.Add((stage, slot));
                    diagnostics.Add($"{stage} texture t{slot} has no authored texture tag.");
                    continue;
                }
                try
                {
                    if (!catalog.TryGetEntry(textureTag, out var textureEntry)
                        || textureEntry.Type != 32 || textureEntry.SubType is < 1 or > 3)
                        throw new InvalidDataException("Binding does not reference a texture header.");
                    byte[] header = catalog.Read(textureTag);
                    if (header.Length < Unsafe.SizeOf<TextureHeader>()
                        || MemoryMarshal.Read<TextureHeader>(header).Marker != TextureHeader.MarkerValue)
                        throw new InvalidDataException("Texture header marker is invalid.");
                    var texture = MemoryMarshal.Read<TextureHeader>(header);
                    var declaration = shader?.TextureSlots.FirstOrDefault(x => x.Slot == slot);
                    textures.Add(new(stage, slot, textureTag, declaration is not null,
                        declaration?.Dimension, texture.Width, texture.Height,
                        texture.Depth, texture.ArraySize, (Format)texture.Format));
                    if (shader is not null && declaration is null)
                        diagnostics.Add($"{stage} texture t{slot} is absent from the shader declarations.");
                }
                catch (Exception error) when ((error is IOException or InvalidDataException or KeyNotFoundException)
                    && !error.Message.StartsWith("Package block authentication failed", StringComparison.Ordinal))
                { diagnostics.Add($"{stage} texture t{slot} 0x{textureTag:X8}: {error.Message}"); }
            }
            return shader;
        }
        var vertex = ReadStage(GameShaderStage.Vertex, nameof(STechnique.VertexShader));
        var pixel = ReadStage(GameShaderStage.Pixel, nameof(STechnique.PixelShader));
        var compute = ReadStage(GameShaderStage.Compute, nameof(STechnique.ComputeShader));
        var additional = new List<AdditionalShaderHeader>();
        foreach (string stageField in (string[])[nameof(STechnique.HAb56cff8), nameof(STechnique.H91e27e07),
            nameof(STechnique.GeometryShader)])
        {
            int field = TagView.FieldOffset<STechnique>($"{stageField}.Shader");
            uint shaderTag = Technique<uint>(bytes, $"{stageField}.Shader");
            if (shaderTag is 0 or uint.MaxValue) continue;
            if (!catalog.TryGetEntry(shaderTag, out var shaderEntry) || shaderEntry.Type != 33)
                throw new InvalidDataException($"Material stage field 0x{field:X} has an invalid shader header.");
            additional.Add(new(field, shaderTag, shaderEntry.SubType));
        }
        return materials[materialTag] = new(materialTag, vertex, pixel, compute, textures, diagnostics)
        {
            AdditionalShaderHeaders = additional, StateOverrides = Technique<uint>(bytes, nameof(STechnique.StateOverrides)),
            EmptyTextureSlots = emptySlots
        };
    }

    /** A material may end after the compute sampler array, short of the whole STechnique, so members are read alone. */
    private static ref readonly T Technique<T>(byte[] bytes, string member) where T : unmanaged =>
        ref TagView.Member<STechnique, T>(bytes, 0, member);

    /** Shader header and payload types must agree with the DXBC stage token. */
    public unsafe GameShaderInfo ReadShader(uint shaderTag, GameShaderStage expectedStage)
    {
        if (shaders.TryGetValue(shaderTag, out var cached))
        {
            if (cached.Stage != expectedStage) throw new InvalidDataException("Shader stage mismatch.");
            return cached;
        }
        // Package compute subtype 6 differs from the DXBC compute stage value 5.
        byte packageSubtype = expectedStage == GameShaderStage.Compute ? (byte)6 : (byte)expectedStage;
        if (!catalog.TryGetEntry(shaderTag, out var headerEntry) || headerEntry.Type != 33
            || headerEntry.SubType != packageSubtype)
            throw new InvalidDataException("Shader header type does not match its material stage.");
        byte[] header = catalog.Read(shaderTag);
        // Only HeaderSize and BytecodeSize must be present, not the whole ShaderHeader.
        if (!TagView.FitsThrough<ShaderHeader>(header, 0, nameof(ShaderHeader.BytecodeSize))
            || TagView.Member<ShaderHeader, ulong>(header, 0, nameof(ShaderHeader.HeaderSize)) != (ulong)header.Length)
            throw new InvalidDataException("Shader header is truncated.");
        if (!catalog.TryGetEntry(headerEntry.Reference, out var payloadEntry)
            || payloadEntry.Type != 41 || payloadEntry.SubType != headerEntry.SubType)
            throw new InvalidDataException("Shader payload type does not match its header.");
        byte[] bytes = catalog.Read(payloadEntry.TagHash);
        if (TagView.Member<ShaderHeader, uint>(header, 0, nameof(ShaderHeader.BytecodeSize)) != bytes.Length)
            throw new InvalidDataException("Shader bytecode length does not match its header.");
        string profile = ValidateShaderContainer(bytes, expectedStage);
        string disassembly;
        fixed (byte* pointer = bytes)
        {
            using var blob = Compiler.Disassemble((IntPtr)pointer, (nuint)bytes.Length,
                DisasmFlags.None, null);
            disassembly = blob.AsString();
        }
        var slots = TextureDeclaration().Matches(disassembly).Select(match =>
            new ShaderTextureSlot(uint.Parse(match.Groups["slot"].Value),
                match.Groups["kind"].Value)).ToArray();
        string[] declarations = disassembly.Split('\n').Select(x => x.TrimEnd('\r'))
            .Where(x => x.StartsWith("dcl_", StringComparison.Ordinal)).ToArray();
        return shaders[shaderTag] = new(shaderTag, payloadEntry.TagHash, expectedStage,
            profile, disassembly, slots, declarations);
    }

    /** Native disassembly only receives a bounded, internally consistent DXBC container. */
    private static string ValidateShaderContainer(byte[] bytes, GameShaderStage expectedStage)
    {
        var fault = DxbcContainer.ReadHeader(bytes, out uint chunks);
        if (fault == DxbcContainer.Fault.Header)
            throw new InvalidDataException("Shader payload has no valid DXBC header.");
        if (fault == DxbcContainer.Fault.ChunkTable)
            throw new InvalidDataException("DXBC chunk table exceeds its payload.");
        string? profile = null;
        for (int i = 0; i < chunks; i++)
        {
            fault = DxbcContainer.ReadChunk(bytes, i, out var chunk);
            if (fault == DxbcContainer.Fault.ChunkOffset)
                throw new InvalidDataException("DXBC chunk offset is out of range.");
            if (fault == DxbcContainer.Fault.ChunkSize)
                throw new InvalidDataException("DXBC chunk exceeds its payload.");
            if (chunk.Kind is not (DxbcContainer.ShaderChunk or DxbcContainer.ExtendedShaderChunk)) continue;
            var version = DxbcContainer.ProgramVersion(bytes, in chunk)
                ?? throw new InvalidDataException("Shader instruction chunk is truncated.");
            if (version.Type != (uint)expectedStage)
                throw new InvalidDataException("DXBC shader stage differs from its material field.");
            string prefix = expectedStage switch
            { GameShaderStage.Vertex => "vs", GameShaderStage.Pixel => "ps", GameShaderStage.Geometry => "gs",
                GameShaderStage.Hull => "hs", GameShaderStage.Domain => "ds", _ => "cs" };
            profile = $"{prefix}_{version.Major}_{version.Minor}";
        }
        return profile ?? throw new InvalidDataException("DXBC has no shader instruction chunk.");
    }

    /** At most 128 texture rows, one per DX11 slot. */
    private static SMaterialTextureAssignment[] TextureRows(byte[] bytes,
        ref readonly TagArray<SMaterialTextureAssignment> array) =>
        MemoryMarshal.Cast<byte, SMaterialTextureAssignment>(CheckedArray(bytes, in array,
            SMaterialTextureAssignment.Class, Unsafe.SizeOf<SMaterialTextureAssignment>(), 128,
            "Material texture count is invalid.", "Material texture array has an invalid header.")).ToArray();

    /**
     * Reads an array field; it needs the marker, a repeated count, the class and rows in the tag.
     * @param maximum A larger count throws countError before the header is read.
     * @return The row bytes; empty when the count and the link are both zero.
     */
    private static ReadOnlySpan<byte> CheckedArray<T>(byte[] bytes, ref readonly TagArray<T> array,
        uint elementClass, int stride, long maximum, string countError, string headerError) where T : unmanaged
    {
        if (array.Count == 0 && array.Relative == 0) return [];
        if (array.Count < 0 || array.Count > maximum) throw new InvalidDataException(countError);
        long header = checked(TagView.OffsetOf(bytes, in array.Relative) + array.Relative);
        var view = new TagArray(bytes, TagView.OffsetOf(bytes, in array));
        if (header < 0 || header > bytes.Length || !view.IsWellFormed || view.ElementClass != elementClass
            || !view.RowsFit(stride))
            throw new InvalidDataException(headerError);
        return bytes.AsSpan(view.DataOffset, checked((int)array.Count * stride));
    }


    [GeneratedRegex(@"^dcl_resource_(?<kind>\w+)[^\r\n]*?\bt(?<slot>\d+)\b", RegexOptions.Multiline)]
    private static partial Regex TextureDeclaration();
}
