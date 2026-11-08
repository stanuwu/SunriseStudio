using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using Vortice.Direct3D11;

namespace SunriseStudio.Rendering;

public sealed record MaterialSampler(uint HeaderTag, uint PayloadTag, SamplerDescription Description);

public sealed record MaterialStageData(GameShaderStage Stage, GameShaderInfo? Shader,
    byte[] TfxBytecode, Vector4[] TfxConstants, Vector4[] ConstantTemplate,
    uint ConstantSourceTag, IReadOnlyList<MaterialSampler> Samplers);

public sealed record MaterialExecutionData(GameMaterialInfo Material,
    MaterialStageData Vertex, MaterialStageData Pixel, MaterialStageData Compute);

public sealed partial class GameMaterialReader
{
    /** Authored data stays separate from runtime externs and GPU binding slots. */
    public MaterialExecutionData ReadExecution(uint tag)
    {
        var material = Read(tag);
        byte[] bytes = catalog.Read(tag);
        return new(material,
            ReadRuntimeStage(bytes, GameShaderStage.Vertex, material.VertexShader,
                in Technique<ShaderStageConstants>(bytes, "VertexShader.State.DynamicConstants")),
            ReadRuntimeStage(bytes, GameShaderStage.Pixel, material.PixelShader,
                in Technique<ShaderStageConstants>(bytes, "PixelShader.State.DynamicConstants")),
            ReadRuntimeStage(bytes, GameShaderStage.Compute, material.ComputeShader,
                in Technique<ShaderStageConstants>(bytes, "ComputeShader.State.DynamicConstants")));
    }

    public byte[] ReadShaderBytecode(GameShaderInfo shader) => catalog.Read(shader.BytecodeTag);

    /** An external constant-buffer entry takes precedence over inline vectors. */
    private MaterialStageData ReadRuntimeStage(byte[] bytes, GameShaderStage stage,
        GameShaderInfo? shader, in ShaderStageConstants source)
    {
        byte[] bytecode = ReadTypedArray(bytes, in source.Bytecode, ExpressionCode.Class);
        var expressionConstants = ReadVectors(bytes, in source.ConstantBuffer);
        byte[] samplerRows = ReadTypedArray(bytes, in source.HandleBuffer, SSamplerReference.Class);
        var samplers = new List<MaterialSampler>();
        foreach (var row in MemoryMarshal.Cast<byte, SSamplerReference>(samplerRows))
            samplers.Add(ReadSampler(row.SamplerHeader));
        uint container = source.CbContainer;
        Vector4[] constants;
        if (container is not (0 or uint.MaxValue))
        {
            if (!catalog.TryGetEntry(container, out var entry) || entry.Type != 32 || entry.SubType != 7)
                throw new InvalidDataException("Material constant source is not a constant-buffer header.");
            byte[] header = catalog.Read(container);
            // Only ByteCount must be present, not the whole ConstantBufferHeader.
            if (!TagView.FitsThrough<ConstantBufferHeader>(header, 0, nameof(ConstantBufferHeader.ByteCount))
                || !catalog.TryGetEntry(entry.Reference, out var payload) || payload.Type != 40 || payload.SubType != 7)
                throw new InvalidDataException("Material constant-buffer payload type is invalid.");
            byte[] data = catalog.Read(entry.Reference);
            uint byteCount = TagView.At<uint>(header,
                TagView.FieldOffset<ConstantBufferHeader>(nameof(ConstantBufferHeader.ByteCount)));
            if (byteCount != data.Length || data.Length % 16 != 0)
                throw new InvalidDataException("Material constant-buffer length is invalid.");
            constants = MemoryMarshal.Cast<byte, Vector4>(data).ToArray();
        }
        else
        {
            constants = ReadVectors(bytes, in source.CbTemplate);
            container = 0;
        }
        return new(stage, shader, bytecode, expressionConstants, constants, container, samplers);
    }

    /** The payload must match the 52-byte native D3D11 sampler descriptor. */
    private MaterialSampler ReadSampler(uint tag)
    {
        if (!catalog.TryGetEntry(tag, out var header) || header.Type != 34 || header.SubType != 1
            || !catalog.TryGetEntry(header.Reference, out var payload)
            || payload.Type != 42 || payload.SubType != 1)
            throw new InvalidDataException($"Sampler 0x{tag:X8} has an invalid resource type.");
        byte[] bytes = catalog.Read(payload.TagHash);
        if (bytes.Length != 52 || Marshal.SizeOf<SamplerDescription>() != 52)
            throw new InvalidDataException("Sampler payload does not fit the native descriptor.");
        var description = MemoryMarshal.Read<SamplerDescription>(bytes);
        if (!Enum.IsDefined(description.Filter) || !Enum.IsDefined(description.AddressU)
            || !Enum.IsDefined(description.AddressV) || !Enum.IsDefined(description.AddressW)
            || !Enum.IsDefined(description.ComparisonFunc) || description.MaxAnisotropy is < 1 or > 16
            || !float.IsFinite(description.MipLODBias) || !float.IsFinite(description.MinLOD)
            || !float.IsFinite(description.MaxLOD) || description.MinLOD > description.MaxLOD)
            throw new InvalidDataException("Sampler payload contains an invalid Direct3D value.");
        return new(tag, payload.TagHash, description);
    }

    private static Vector4[] ReadVectors(byte[] bytes, ref readonly TagArray<ExpressionConstant> array) =>
        MemoryMarshal.Cast<byte, Vector4>(ReadTypedArray(bytes, in array, ExpressionConstant.Class)).ToArray();

    private static byte[] ReadTypedArray<T>(byte[] bytes, ref readonly TagArray<T> array, uint elementClass)
        where T : unmanaged =>
        CheckedArray(bytes, in array, elementClass, Unsafe.SizeOf<T>(), TagArray.MaximumReaderRows, "Material array count is invalid.",
            $"Material array 0x{TagView.OffsetOf(bytes, in array):X} has an invalid header.").ToArray();

}
