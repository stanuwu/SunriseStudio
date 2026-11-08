using System.Numerics;
using System.Security.Cryptography;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Interchange;

/** Shader motion runs before skinning and uses the original per-vertex colour channels. */
internal static class NativeVertexMotion
{
    internal static BundleVertexMotion? Read(PackageCatalog catalog, MeshData mesh, GameMaterialReader reader)
    {
        if (mesh.Native is not { Kind: NativeMeshKind.Entity, InputLayoutVerified: true } native) return null;
        if (!catalog.TryGetEntry(mesh.MaterialTag, out var entry) || entry.Reference != STechnique.Class) return null;
        var material = reader.Read(mesh.MaterialTag);
        if (material.VertexShader is not { } shader) return null;
        string hash = Convert.ToHexString(SHA256.HashData(reader.ReadShaderBytecode(shader)));
        if (hash is not ("37866ACE594BCD1397E9DC34B6B70BE634408B76ACA03AD225F878FD59D394F7"
            or "38B015EF0F189F417133F90ED8755D78333143956639CF1C70E80D3132D12E77")) return null;
        var color = native.InputElements.SingleOrDefault(value => value.Semantic == "COLOR" && value.SemanticIndex == 0);
        if (color is null || color.PerInstance || color.Format != NativeVertexFormat.R8G8B8A8UNorm)
            throw new NotSupportedException("Native vertex motion needs its authored colour stream.");
        var stream = native.VertexStreams[color.Slot];
        float[][] colors = mesh.SourceVertexIndices.Select(index =>
        {
            int offset = checked((int)index * stream.Stride + color.Offset);
            return Enumerable.Range(0, 4).Select(lane => stream.Data[offset + lane] / 255f).ToArray();
        }).ToArray();
        var execution = reader.ReadExecution(mesh.MaterialTag);
        var parameters = MaterialPreviewAnalyzer.CreateInputs(catalog).Parameters;
        Vector4[] At(double seconds)
        {
            var evaluated = TfxEvaluator.Evaluate(execution.Vertex, NativeInspectionMaterialInputs.Create(seconds, parameters));
            if (!evaluated.IsComplete || evaluated.Constants.Length != 5)
                throw new NotSupportedException("Native vertex motion has unresolved material inputs.");
            return evaluated.Constants;
        }
        var start = At(0);
        var atOne = At(1);
        var rates = start.Select((value, index) => atOne[index] - value).ToArray();
        foreach (double seconds in new[] { .25, 2.0, 17.0 })
        {
            var sample = At(seconds);
            for (int row = 0; row < sample.Length; row++)
                if (Vector4.Distance(sample[row], start[row] + rates[row] * (float)seconds) > .0001f)
                    throw new NotSupportedException("Native vertex motion uses a non-linear material clock.");
        }
        float[][] Vectors(Vector4[] source) => source.Select(value => new[] { value.X, value.Y, value.Z, value.W }).ToArray();
        if (start.Concat(rates).Any(value => !float.IsFinite(value.X + value.Y + value.Z + value.W)))
            throw new InvalidDataException("Native vertex motion contains nonfinite constants.");
        return new("VertexWave5", colors, Vectors(start), Vectors(rates));
    }
}
