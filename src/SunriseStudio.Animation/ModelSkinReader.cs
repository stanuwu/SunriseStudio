using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Animation;

public readonly record struct VertexSkinWeights(int Bone0, int Bone1, int Bone2, int Bone3,
    Vector4 Weights);

public enum SkinPaletteKind { Skeleton, ClothInput }

public sealed record ClothSkinProvenance(uint ConfigTag, uint AssetTag, uint HavokTag,
    uint DeclarationTag, int PaletteSize, float MaximumInverseBindError);

/** Vertex weights retain the original stream order before scene mesh compaction. */
public sealed record ModelSkinBinding(uint ModelTag, int MeshIndex, uint PositionHeaderTag,
    int PositionStride, IReadOnlyList<int> PartOffsets, IReadOnlyList<VertexSkinWeights> Vertices)
{
    public SkinPaletteKind PaletteKind { get; init; }
    public uint WeightHeaderTag { get; init; }
    public uint WeightPayloadTag { get; init; }
    public ClothSkinProvenance? Cloth { get; init; }
}

/** These pre-BL layouts are checked against shader inputs and byte-normalized weights. */
public sealed partial class ModelSkinReader(PackageCatalog catalog)
{
    /** Only a model referenced by the entity that supplied this rig is accepted. */
    public IReadOnlyList<ModelSkinBinding> Read(uint modelTag, SkeletonRig rig)
    {
        if (!rig.ModelTags.Contains(modelTag))
            throw new InvalidDataException("The model is not linked to the skeleton's entity.");
        return ReadModel(modelTag, rig);
    }

    /** Like Read, but for a model from another entity. The caller vouches that it uses the rig's bone order. */
    public IReadOnlyList<ModelSkinBinding> ReadExternal(uint modelTag, SkeletonRig rig) => ReadModel(modelTag, rig);

    /** Reads the weights packed in each mesh's position stream: 8, 12 or 16 bytes per vertex. */
    private IReadOnlyList<ModelSkinBinding> ReadModel(uint modelTag, SkeletonRig rig)
    {
        if (!catalog.TryGetEntry(modelTag, out var modelEntry) || modelEntry.Reference != SEntityModel.Class)
            throw new NotSupportedException("Skin input is not a build-86657 entity model.");
        byte[] model = catalog.Read(modelTag);
        var result = new List<ModelSkinBinding>();
        int meshIndex = 0;
        foreach (ref readonly var row in AnimationControlReader.Rows<SEntityModel, SEntityModelMesh>(model, 0,
                     nameof(SEntityModel.Meshes), SEntityModelMesh.Class, ReadMessages.Skin))
        {
            uint headerTag = row.VertexBuffer0[0].Unk0;
            if (!catalog.TryGetEntry(headerTag, out var headerEntry)
                || headerEntry.Type != 32 || headerEntry.SubType != 4)
                throw new InvalidDataException("Skin position stream header has an unexpected package type.");
            byte[] header = catalog.Read(headerTag);
            int size = checked((int)HeaderWord<uint>(header, nameof(VertexBufferHeader.ByteCount)));
            int stride = HeaderWord<ushort>(header, nameof(VertexBufferHeader.Stride));
            uint separateWeights = row.VertexBuffer0[2].Unk0;
            if (separateWeights is not (0 or uint.MaxValue))
                throw new NotSupportedException("Separate skin-weight streams are not decoded.");
            if (stride is not (8 or 12 or 16) || size == 0 || size % stride != 0)
                throw new NotSupportedException($"Entity skin position stride {stride} is not decoded.");
            byte[] positions = catalog.Read(headerEntry.Reference);
            if (size > positions.Length) throw new InvalidDataException("Skin position stream is truncated.");
            var weights = new VertexSkinWeights[size / stride];
            for (int i = 0; i < weights.Length; i++)
            {
                weights[i] = SkinPositionStream.Weights(positions, i, stride);
                Validate(weights[i], rig.Bones.Count);
            }
            result.Add(new ModelSkinBinding(modelTag, meshIndex++, headerTag, stride,
                Array.AsReadOnly(Parts(model, in row)), Array.AsReadOnly(weights)));
        }
        return result.AsReadOnly();
    }

    /** A zero weight can carry the package's unused-bone sentinel. */
    private static void Validate(VertexSkinWeights value, int boneCount)
    {
        Span<int> bones = stackalloc int[4] { value.Bone0, value.Bone1, value.Bone2, value.Bone3 };
        Span<float> weights = stackalloc float[4]
            { value.Weights.X, value.Weights.Y, value.Weights.Z, value.Weights.W };
        float sum = 0;
        for (int i = 0; i < 4; i++)
        {
            if (weights[i] > 0 && (uint)bones[i] >= boneCount)
                throw new NotSupportedException("A weighted bone does not belong to the entity's skeleton.");
            sum += weights[i];
        }
        if (MathF.Abs(sum - 1) > 0.00001f)
            throw new InvalidDataException("Packed skin weights do not sum to 255.");
    }

    /** @return Offset of each part row of `mesh`, a mesh row read in place in `model`. */
    private static int[] Parts(byte[] model, ref readonly SEntityModelMesh mesh) => TagView.RowOffsets(model,
        AnimationControlReader.Rows<SEntityModelPart>(model, TagView.OffsetOf(model, in mesh.Parts),
            SEntityModelPart.Class, ReadMessages.Skin));

    /** A vertex buffer header member; only its own bytes must be present. */
    private static T HeaderWord<T>(byte[] header, string member) where T : unmanaged =>
        AnimationControlReader.Member<VertexBufferHeader, T>(header, 0, member, ReadMessages.Skin);
}
