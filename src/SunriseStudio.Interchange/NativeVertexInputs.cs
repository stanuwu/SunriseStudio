using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Interchange;

/** Vertex streams a pixel shader can read through its vertex shader, per preview vertex. */
internal static class NativeVertexInputs
{
    /** COLOR0 as four 0..1 lanes; null when the part has no readable colour stream. */
    internal static float[][]? Colors(MeshData mesh) =>
        Read(mesh, mesh.Native?.InputElements.FirstOrDefault(value => value is { Semantic: "COLOR", SemanticIndex: 0 }), 0, 4);

    /** Lanes z and w of the UV stream, the lowest TEXCOORD; null when it stores two lanes. */
    internal static float[][]? TexCoordZw(MeshData mesh) =>
        Read(mesh, mesh.Native?.InputElements.Where(value => value.Semantic == "TEXCOORD").MinBy(value => value.SemanticIndex), 2, 2);

    /** POSITION0 x, y and z before the model's offset and scale; null when the stream is unreadable. */
    internal static float[][]? RawPositions(MeshData mesh) =>
        Read(mesh, mesh.Native?.InputElements.FirstOrDefault(value => value is { Semantic: "POSITION", SemanticIndex: 0 }), 0, 3);

    /**
     * The offset an entity part's vertex shader adds over time, as a program on the part's own streams.
     * @param channels Object channel table the vertex constants read.
     * @return Null when the shader adds no time-driven offset, or the part lacks a stream the offset reads.
     */
    internal static BundleVertexMotion? Motion(PackageCatalog catalog, MeshData mesh, GameMaterialReader reader,
        IReadOnlyList<Vector4>? channels)
    {
        if (mesh.Native is not { Kind: NativeMeshKind.Entity, InputLayoutVerified: true } native
            || !catalog.TryGetEntry(mesh.MaterialTag, out var entry) || entry.Reference != STechnique.Class
            || reader.Read(mesh.MaterialTag) is not { VertexShader: { } shader } material) return null;
        var inputs = MaterialPreviewAnalyzer.CreateInputs(catalog);
        inputs.ObjectChannels = channels;
        var vertex = TfxEvaluator.Evaluate(reader.ReadExecution(mesh.MaterialTag).Vertex, inputs);
        if (!vertex.IsComplete || vertex.Terms.Count == 0) return null;
        var textures = material.Textures.Where(value => value.Stage == GameShaderStage.Vertex && value.DeclaredByShader
            && value.ShaderDimension == "texture2d" && value.ArraySize == 1 && value.Depth == 1).ToDictionary(value => value.Slot);
        var program = MaterialExpressionCompiler.CompileMotion(shader.Disassembly, vertex.Constants, vertex.Terms,
            new(native.PositionOffset.X, native.PositionOffset.Y, native.PositionOffset.Z, native.PositionScale.X),
            textures: textures.Keys.ToHashSet());
        if (program is null) return null;
        var sampled = program.Nodes.Where(node => node.Operation == "sample").DistinctBy(node => node.Slot).ToDictionary(node => node.Slot,
            node => new BundleMotionTexture(textures[node.Slot].TextureTag, textures[node.Slot].IsSrgb, vertex.Samplers.Any(pair =>
                pair.Key.Stage == GameShaderStage.Vertex && pair.Key.Slot == node.SamplerSlot
                && pair.Value.Description.AddressU == Vortice.Direct3D11.TextureAddressMode.Clamp)));
        var reads = program.Nodes.Select(node => node.Operation).ToHashSet();
        if (reads.Contains(ShaderVaryings.RawPosition) && RawPositions(mesh) is null || reads.Contains(ShaderVaryings.Color) && Colors(mesh) is null
            || reads.Contains(ShaderVaryings.UvZw) && TexCoordZw(mesh) is null) return null;
        return new("VertexProgram", [], [], []) { Program = program, Textures = sampled.Count == 0 ? null : sampled };
    }

    private static float[][]? Read(MeshData mesh, NativeInputElement? element, int first, int count)
    {
        if (mesh.Native is not { InputLayoutVerified: true } native || element is null || element.PerInstance
            || element.Slot >= native.VertexStreams.Length || mesh.SourceVertexIndices.Length != mesh.Vertices.Length) return null;
        int size = element.Format switch
        {
            NativeVertexFormat.R8G8B8A8UNorm => 1,
            NativeVertexFormat.R16G16B16A16SNorm or NativeVertexFormat.R16G16B16A16UNorm => 2,
            NativeVertexFormat.R32G32B32A32Float => 4,
            _ => 0
        };
        if (size == 0) return null;
        var stream = native.VertexStreams[element.Slot];
        var result = new float[mesh.Vertices.Length][];
        for (int vertex = 0; vertex < result.Length; vertex++)
        {
            long offset = (long)mesh.SourceVertexIndices[vertex] * stream.Stride + element.Offset;
            if (offset + 4L * size > stream.Data.Length) return null;
            var data = stream.Data.AsSpan((int)offset);
            var values = result[vertex] = new float[count];
            for (int lane = 0; lane < count; lane++)
            {
                int at = first + lane;
                values[lane] = element.Format switch
                {
                    NativeVertexFormat.R8G8B8A8UNorm => data[at] / 255f,
                    NativeVertexFormat.R16G16B16A16SNorm => Math.Max(BinaryPrimitives.ReadInt16LittleEndian(data[(at * 2)..]) / 32767f, -1),
                    NativeVertexFormat.R16G16B16A16UNorm => BinaryPrimitives.ReadUInt16LittleEndian(data[(at * 2)..]) / 65535f,
                    _ => BinaryPrimitives.ReadSingleLittleEndian(data[(at * 4)..])
                };
                if (!float.IsFinite(values[lane])) return null;
            }
        }
        return result;
    }
}
