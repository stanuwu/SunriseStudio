using System.Numerics;
using SunriseStudio.Animation;
using SunriseStudio.Live;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed record LivePoseDraw(IReadOnlyDictionary<MeshInstance, NativeLivePalette> Palettes,
    IReadOnlyDictionary<MeshInstance, RenderVertex[]> Geometry)
{
    public int TotalParts { get; init; }
    public IReadOnlyDictionary<string, int> SkippedReasons { get; init; } = new Dictionary<string, int>();
}

/** Native model IDs and authored stream indices join a captured palette to one selected instance. */
public sealed class LivePoseModelBinding
{
    private readonly IReadOnlyList<ModelSkinBinding> bindings;
    public uint ModelTag { get; }
    private LivePoseModelBinding(uint model, IReadOnlyList<ModelSkinBinding> bindings)
    { ModelTag = model; this.bindings = bindings; }

    /** Reads the entity's rig and the model's skin streams; refuses an installed skinning scope that differs. */
    public static LivePoseModelBinding Prepare(PackageCatalog catalog, uint entity, uint model, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var scope = new GameMaterialReader(catalog).ReadScopeExecution(0x80B356CF);
        if (scope.ScopeIndex != 7 || scope.Vertex.ConstantBufferSlot != 11 || scope.Pixel.ConstantBufferSlot != 11)
            throw new NotSupportedException("Installed skinning scope differs from the captured contract.");
        var rig = new SkeletonReader(catalog).Read(entity);
        token.ThrowIfCancellationRequested();
        var result = new ModelSkinReader(catalog).Read(model, rig);
        token.ThrowIfCancellationRequested();
        return new(model, result);
    }

    /** World-space skinning uses the captured rows directly and never multiplies by placement again. */
    public LivePoseDraw Build(LiveDraw pose, IReadOnlyList<MeshInstance> instances, NativeSceneRenderer renderer)
    {
        if (pose.ModelTag != ModelTag) throw new InvalidDataException("Captured pose belongs to a different model.");
        NativeEntityPalette kind = pose.Kind switch
        { LivePaletteKind.Matrix3x4 => NativeEntityPalette.Matrix, LivePaletteKind.DualQuaternion => NativeEntityPalette.DualQuaternion,
            _ => throw new NotSupportedException("This captured palette form has no admitted viewer shader.") };
        var palettes = new Dictionary<MeshInstance, NativeLivePalette>();
        var geometry = new Dictionary<MeshInstance, RenderVertex[]>();
        var skipped = new Dictionary<string, int>(); int total = 0;
        foreach (var instance in instances.Where(part => part.Mesh.TagHash == ModelTag))
        {
            total++;
            try
            {
                var mesh = instance.Mesh;
                if (!renderer.CanApplyLivePalette(instance, kind))
                    throw new NotSupportedException("A selected model part has no matching native skinning shader.");
                var skin = bindings.SingleOrDefault(value => value.PartOffsets.Contains(mesh.PartIndex));
                if (skin is null || skin.PaletteKind != SkinPaletteKind.Skeleton
                    || mesh.SourceVertexIndices.Length != mesh.Vertices.Length)
                    throw new NotSupportedException("A selected model part has no verified skeleton weight stream.");
                if (mesh.Native is not { Kind: NativeMeshKind.Entity } native || ModelRows(native.SourceHeader) is not { } model
                    || model.Position != pose.FixedRows[0] || model.Uv != pose.FixedRows[1])
                    throw new NotSupportedException("Captured model constants differ from the loaded geometry.");
                var vertices = new RenderVertex[mesh.Vertices.Length];
                var minimum = new Vector3(float.PositiveInfinity); var maximum = new Vector3(float.NegativeInfinity);
                for (int i = 0; i < vertices.Length; i++)
                {
                    uint source = mesh.SourceVertexIndices[i];
                    if (source >= skin.Vertices.Count) throw new InvalidDataException("Live skin vertex leaves its source stream.");
                    var original = mesh.Vertices[i];
                    var position = SkinPosition(original.Position, skin.Vertices[(int)source], pose);
                    vertices[i] = new(position, Vector3.Zero, original.TexCoord);
                    minimum = Vector3.Min(minimum, position); maximum = Vector3.Max(maximum, position);
                }
                RebuildNormals(vertices, mesh.Indices);
                var rows = new Vector4[8 + pose.Palette.Count];
                for (int i = 0; i < 3; i++) rows[5 + i] = pose.FixedRows[i];
                for (int i = 0; i < pose.Palette.Count; i++) rows[8 + i] = pose.Palette[i];
                palettes.Add(instance, new(kind, Array.AsReadOnly(rows), (minimum + maximum) * .5f));
                geometry.Add(instance, vertices);
            }
            catch (NotSupportedException error)
            { skipped[error.Message] = skipped.GetValueOrDefault(error.Message) + 1; }
        }
        if (geometry.Count == 0) throw new NotSupportedException(skipped.Keys.FirstOrDefault()
            ?? "The selected instance has no parts for the captured model.");
        return new(palettes, geometry) { TotalParts = total, SkippedReasons = skipped };
    }

    /** Dual-quaternion weights follow the first indexed quaternion's hemisphere, as the admitted VS does. */
    public static Vector3 SkinPosition(Vector3 point, VertexSkinWeights skin, LiveDraw pose)
    {
        Span<int> bones = stackalloc int[4] { skin.Bone0, skin.Bone1, skin.Bone2, skin.Bone3 };
        Span<float> weights = stackalloc float[4] { skin.Weights.X, skin.Weights.Y, skin.Weights.Z, skin.Weights.W };
        if (pose.Kind == LivePaletteKind.Matrix3x4)
        {
            var result = Vector3.Zero; var position = new Vector4(point, 1);
            for (int i = 0; i < 4; i++)
            {
                if (weights[i] == 0) continue;
                if ((uint)bones[i] >= pose.ModelBones) throw new InvalidDataException("Vertex bone exceeds the captured palette.");
                int offset = bones[i] * 3;
                result += new Vector3(Vector4.Dot(position, pose.Palette[offset]),
                    Vector4.Dot(position, pose.Palette[offset + 1]), Vector4.Dot(position, pose.Palette[offset + 2])) * weights[i];
            }
            return result;
        }
        if (pose.Kind != LivePaletteKind.DualQuaternion) throw new NotSupportedException("Unsupported live skinning form.");
        if ((uint)bones[0] >= pose.ModelBones) throw new InvalidDataException("Quaternion reference bone is outside the captured palette.");
        Vector4 reference = pose.Palette[bones[0] * 2];
        Vector4 rotation = Vector4.Zero, dual = Vector4.Zero;
        for (int i = 0; i < 4; i++)
        {
            if (weights[i] == 0) continue;
            if ((uint)bones[i] >= pose.ModelBones) throw new InvalidDataException("Vertex bone exceeds the captured palette.");
            var real = pose.Palette[bones[i] * 2];
            float weight = weights[i] * (Vector4.Dot(reference, real) >= 0 ? 1 : -1);
            rotation += real * weight; dual += pose.Palette[bones[i] * 2 + 1] * weight;
        }
        float length = rotation.Length();
        if (!float.IsFinite(length) || length < 1e-8f) throw new InvalidDataException("Captured quaternion blend has no rotation.");
        rotation /= length; dual /= length;
        var q = new Vector3(rotation.X, rotation.Y, rotation.Z); var d = new Vector3(dual.X, dual.Y, dual.Z);
        return point + 2 * Vector3.Cross(q, rotation.W * point + Vector3.Cross(q, point))
            + 2 * (rotation.W * d - dual.W * q + Vector3.Cross(q, d));
    }

    /** The model's position offset and UV transform rows, as a draw's fixed rows 0 and 1; null when the header ends first. */
    internal static (Vector4 Position, Vector4 Uv)? ModelRows(byte[] header)
    {
        if (!TagView.FitsThrough<SEntityModel>(header, 0, nameof(SEntityModel.UvOffsetV))) return null;
        return (new(Member<Vector3>(header, nameof(SEntityModel.PositionOffset)), Member<float>(header, nameof(SEntityModel.PositionScaleW))),
            new(Member<float>(header, nameof(SEntityModel.UvScaleU)), Member<float>(header, nameof(SEntityModel.UvScaleV)),
                Member<float>(header, nameof(SEntityModel.UvOffsetU)), Member<float>(header, nameof(SEntityModel.UvOffsetV))));

        static T Member<T>(byte[] header, string name) where T : unmanaged => TagView.At<T>(header, TagView.FieldOffset<SEntityModel>(name));
    }

    private static void RebuildNormals(RenderVertex[] vertices, uint[] indices)
    {
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            uint a = indices[i], b = indices[i + 1], c = indices[i + 2];
            var normal = Vector3.Cross(vertices[b].Position - vertices[a].Position, vertices[c].Position - vertices[a].Position);
            vertices[a] = vertices[a] with { Normal = vertices[a].Normal + normal };
            vertices[b] = vertices[b] with { Normal = vertices[b].Normal + normal };
            vertices[c] = vertices[c] with { Normal = vertices[c].Normal + normal };
        }
        for (int i = 0; i < vertices.Length; i++) vertices[i] = vertices[i] with
        { Normal = vertices[i].Normal.LengthSquared() > 1e-12f ? Vector3.Normalize(vertices[i].Normal) : Vector3.UnitZ };
    }
}
