using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Animation;

/** Cloth indices are accepted only when their authored inverse binds match the owning skeleton. */
internal sealed class HavokSkinBinding
{
    internal uint RawTag { get; }
    internal int PaletteSize { get; }
    internal float MaximumError { get; }
    internal HashSet<int> VerifiedBones { get; } = [];

    /** Local and global fixups resolve package pointers without loading the Havok runtime. */
    internal HavokSkinBinding(PackageCatalog catalog, uint assetTag, SkeletonRig rig)
    {
        if (!catalog.TryGetEntry(assetTag, out var asset) || asset.Reference != SSkeletonAsset.Class)
            throw new InvalidDataException("Cloth asset has an unexpected class.");
        RawTag = AnimationControlReader.Member<SSkeletonAsset, uint>(catalog.Read(assetTag), 0,
            nameof(SSkeletonAsset.HavokRawPackfile));
        var file = new HavokClothPackfile(catalog.Read(RawTag));
        var transformSets = file.Objects.Where(o => o.Name == "hclTransformSetDefinition").ToArray();
        if (transformSets.Length != 1)
            throw new NotSupportedException("Cloth input needs exactly one authored transform set.");
        PaletteSize = file.TransformCount(transformSets[0]);
        if (PaletteSize != rig.Bones.Count)
            throw new NotSupportedException("Cloth transform count differs from its entity skeleton.");
        foreach (var item in file.Objects.Where(o => o.Name.Contains("Skin", StringComparison.Ordinal)))
        {
            if (item.Name != "hclObjectSpaceSkinPNTOperator")
                throw new NotSupportedException($"Cloth skin operator {item.Name} is not decoded.");
            var skin = file.ReadSkinOperator(item);
            if (skin.TransformSet != 0)
                throw new NotSupportedException("Cloth skin operator selects another transform set.");
            int count = checked((int)skin.InverseBindCount);
            if (count == 0 || count > rig.Bones.Count || count != skin.TransformSubsetCount)
                throw new InvalidDataException("Cloth transform subset and inverse-bind counts disagree.");
            var (matrices, indices) = file.SkinArrays(skin, count);
            for (int i = 0; i < count; i++)
            {
                int bone = file.TransformIndex(indices, i);
                if (bone >= rig.Bones.Count) throw new InvalidDataException("Cloth transform index is outside the rig.");
                var actual = file.InverseBind(matrices, i);
                var expected = rig.Bones[bone].InverseBindObject.Matrix;
                float[] differences = [actual.M11 - expected.M11, actual.M12 - expected.M12,
                    actual.M13 - expected.M13, actual.M14 - expected.M14, actual.M21 - expected.M21,
                    actual.M22 - expected.M22, actual.M23 - expected.M23, actual.M24 - expected.M24,
                    actual.M31 - expected.M31, actual.M32 - expected.M32, actual.M33 - expected.M33,
                    actual.M34 - expected.M34, actual.M41 - expected.M41, actual.M42 - expected.M42,
                    actual.M43 - expected.M43, actual.M44 - expected.M44];
                float error = differences.Max(MathF.Abs);
                if (error > 0.0002f)
                    throw new NotSupportedException("Cloth inverse bind differs from its skeleton bone.");
                MaximumError = MathF.Max(MaximumError, error);
                VerifiedBones.Add(bone);
            }
        }
        if (VerifiedBones.Count == 0) throw new NotSupportedException("Cloth has no supported skin operators.");
    }
}
