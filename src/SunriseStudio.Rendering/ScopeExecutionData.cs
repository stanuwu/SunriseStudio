using System.Text;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Rendering;

public sealed record ScopeTextureBinding(uint Slot, uint TextureTag);
public sealed record ScopeStageData(GameShaderStage Stage, uint ConstantBufferSlot,
    MaterialStageData Data, IReadOnlyList<ScopeTextureBinding> Textures);
public sealed record GameScopeExecution(uint Tag, uint ScopeIndex, string Name,
    ScopeStageData Pixel, ScopeStageData Vertex);

public sealed partial class GameMaterialReader
{
    /** Pixel and vertex blocks are decoded; other stages remain unsupported. */
    public GameScopeExecution ReadScopeExecution(uint tag)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != SScope.Class)
            throw new InvalidDataException($"Tag 0x{tag:X8} is not a supported render scope.");
        byte[] bytes = catalog.Read(tag);
        if (!TagView.Fits<SScope>(bytes, 0) || TagView.At<SScope>(bytes).TagSize != (ulong)bytes.Length)
            throw new InvalidDataException("Scope size does not match its header.");
        ref readonly var scope = ref TagView.At<SScope>(bytes);
        long nameOffset = checked(TagView.OffsetOf(bytes, in scope.Common.Name) + scope.Common.Name.Relative);
        if (nameOffset < 0 || nameOffset >= bytes.Length)
            throw new InvalidDataException("Scope name offset leaves its tag.");
        int end = bytes.AsSpan((int)nameOffset).IndexOf((byte)0);
        if (end < 0 || end > 256) throw new InvalidDataException("Scope name is not terminated.");
        string name = Encoding.UTF8.GetString(bytes, (int)nameOffset, end);
        return new(tag, scope.Common.ScopeIndex, name,
            ReadScopeStage(bytes, GameShaderStage.Pixel, in scope.HBd01623f),
            ReadScopeStage(bytes, GameShaderStage.Vertex, in scope.HF7733063));
    }

    /** Scope records carry the constant-buffer slot next to the external template tag. */
    private ScopeStageData ReadScopeStage(byte[] bytes, GameShaderStage stage, in C80807207 source)
    {
        var data = ReadRuntimeStage(bytes, stage, null, in source.DynamicConstants);
        uint slot = source.DynamicConstants.CbSlot;
        if (data.ConstantTemplate.Length != 0 && slot >= 14)
            throw new InvalidDataException("Scope constant-buffer slot exceeds the Direct3D limit.");
        var textures = TextureRows(bytes, in source.StaticTextures.Textures).Select(row =>
            new ScopeTextureBinding(row.TextureSlot, row.Texture)).ToArray();
        if (textures.Any(texture => texture.Slot >= 128))
            throw new InvalidDataException("Scope texture slot exceeds the Direct3D limit.");
        return new(stage, slot, data, textures);
    }
}
