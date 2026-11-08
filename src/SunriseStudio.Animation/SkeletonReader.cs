using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Animation;

/** Typed resource pairs distinguish skeletons from other entity components. */
public sealed class SkeletonReader(PackageCatalog catalog)
{
    public Func<uint, string?>? NameResolver { get; set; }

    /** Accept an entity class or its skeleton config and preserve package bone indices. */
    public SkeletonRig Read(uint tag)
    {
        uint entityTag = 0;
        uint resourceTag = tag;
        var models = new HashSet<uint>();
        var clothModels = new List<ClothModelReference>();
        if (Class(tag) == SEntity.Class)
        {
            entityTag = tag;
            byte[] entity = catalog.Read(tag);
            var skeletons = new HashSet<uint>();
            foreach (ref readonly var component in AnimationControlReader.Rows<SEntity, SPatternComponent>(entity, 0,
                         nameof(SEntity.Components), SPatternComponent.Class, ReadMessages.Default))
            {
                uint configTag = component.Config;
                if (Class(configTag) != SEntityConfig.Class) continue;
                byte[] config = catalog.Read(configTag);
                if (ConfigTarget(config, nameof(SEntityConfig.Definition)) is not { } resource) continue;
                uint resourceClass = AnimationControlReader.ClassBefore(config, resource, ReadMessages.Default);
                if (resourceClass == SSkeletonResource.Class
                    && ConfigTarget(config, nameof(SEntityConfig.DefaultInstance)) is { } skeletonPrimary
                    && AnimationControlReader.ClassBefore(config, skeletonPrimary, ReadMessages.Default) == SSkeletonPrimary.Class)
                    skeletons.Add(configTag);
                if (resourceClass == SModelComponent.Class
                    && TagView.FitsThrough<SModelComponent>(config, resource, nameof(SModelComponent.Model)))
                {
                    uint model = AnimationControlReader.Member<SModelComponent, uint>(config, resource,
                        nameof(SModelComponent.Model), ReadMessages.Default);
                    if (Class(model) == SEntityModel.Class) models.Add(model);
                }
                if (resourceClass == SClothModelComponent.Class && ClothReference(configTag) is { } cloth) clothModels.Add(cloth);
            }
            if (skeletons.Count != 1)
                throw new NotSupportedException($"Entity 0x{tag:X8} has {skeletons.Count} supported skeleton resources.");
            resourceTag = skeletons.Single();
        }
        if (Class(resourceTag) != SEntityConfig.Class)
            throw new NotSupportedException("Skeleton input must be an entity or skeleton config tag.");
        byte[] data = catalog.Read(resourceTag);
        int? instance = ConfigTarget(data, nameof(SEntityConfig.DefaultInstance));
        int? definition = ConfigTarget(data, nameof(SEntityConfig.Definition));
        if (instance is not { } primary
            || AnimationControlReader.ClassBefore(data, primary, ReadMessages.Default) != SSkeletonPrimary.Class
            || definition is not { } secondary
            || AnimationControlReader.ClassBefore(data, secondary, ReadMessages.Default) != SSkeletonResource.Class)
            throw new NotSupportedException("Config does not contain the supported skeleton resource pair.");
        var hierarchy = AnimationControlReader.Rows<SSkeletonResource, SSkeletonBoneNode>(data, secondary,
            nameof(SSkeletonResource.Hierarchy), SSkeletonBoneNode.Class, ReadMessages.Default);
        var bind = AnimationControlReader.Rows<SSkeletonResource, SAnimationControlDefault>(data, secondary,
            nameof(SSkeletonResource.ObjectBindTransforms), SAnimationControlDefault.Class, ReadMessages.Default);
        var inverse = AnimationControlReader.Rows<SSkeletonResource, SAnimationControlDefault>(data, secondary,
            nameof(SSkeletonResource.InverseBindTransforms), SAnimationControlDefault.Class, ReadMessages.Default);
        if (hierarchy.Length is < 1 or > 4096 || bind.Length != hierarchy.Length
            || inverse.Length != hierarchy.Length)
            throw new InvalidDataException("Skeleton hierarchy and transform counts disagree.");
        var parents = new int[hierarchy.Length];
        for (int i = 0; i < parents.Length; i++) parents[i] = hierarchy[i].ParentIndex;
        int[] order = EvaluationOrder(parents);
        var objectTransforms = new BoneTransform[bind.Length];
        for (int i = 0; i < objectTransforms.Length; i++) objectTransforms[i] = Transform(in bind[i]);
        var inverseTransforms = new BoneTransform[inverse.Length];
        for (int i = 0; i < inverseTransforms.Length; i++) inverseTransforms[i] = Transform(in inverse[i]);
        var bones = new SkeletonBone[hierarchy.Length];
        float maximumError = 0;
        for (int i = 0; i < bones.Length; i++)
        {
            ref readonly var node = ref hierarchy[i];
            uint hash = node.BoneName;
            int child = node.FirstChildIndex, sibling = node.NextSiblingIndex;
            if (child < -1 || child >= bones.Length || sibling < -1 || sibling >= bones.Length)
                throw new InvalidDataException("Skeleton child link is outside the hierarchy.");
            var product = objectTransforms[i].Matrix * inverseTransforms[i].Matrix;
            maximumError = MathF.Max(maximumError, IdentityError(product));
            var local = parents[i] < 0 ? objectTransforms[i]
                : BoneTransform.FromMatrix(objectTransforms[i].Matrix * inverseTransforms[parents[i]].Matrix);
            bones[i] = new SkeletonBone(i, hash,
                NameResolver?.Invoke(hash) ?? BoneNameCandidates.Resolve(hash) ?? $"bone_{hash:X8}",
                parents[i], child, sibling, objectTransforms[i], inverseTransforms[i], local);
        }
        if (maximumError > 0.002f)
            throw new InvalidDataException($"Skeleton bind and inverse transforms disagree by {maximumError:G6}.");
        ValidateChildren(bones);
        return new SkeletonRig(entityTag, resourceTag, models.Order().ToArray(), bones, order,
            maximumError, clothModels.Distinct().ToArray());
    }

    /**
     * The cloth model and Havok cloth asset a component config names.
     * @return Null unless the config pairs a 0x80807273 primary with a 0x80807286 cloth resource.
     */
    public ClothModelReference? ClothReference(uint configTag)
    {
        if (Class(configTag) != SEntityConfig.Class) return null;
        byte[] config = catalog.Read(configTag);
        if (ConfigTarget(config, nameof(SEntityConfig.Definition)) is not { } resource
            || AnimationControlReader.ClassBefore(config, resource, ReadMessages.Default) != SClothModelComponent.Class
            || !TagView.FitsThrough<SClothModelComponent>(config, resource, nameof(SClothModelComponent.HavokClothAsset))
            || ConfigTarget(config, nameof(SEntityConfig.DefaultInstance)) is not { } instance
            || AnimationControlReader.ClassBefore(config, instance, ReadMessages.Default) != SClothModelPrimary.Class)
            return null;
        uint model = AnimationControlReader.Member<SClothModelComponent, uint>(config, resource,
            nameof(SClothModelComponent.Model), ReadMessages.Default);
        uint asset = AnimationControlReader.Member<SClothModelComponent, uint>(config, resource,
            nameof(SClothModelComponent.HavokClothAsset), ReadMessages.Default);
        return Class(model) == SEntityModel.Class && Class(asset) == SSkeletonAsset.Class ? new(configTag, model, asset) : null;
    }

    private uint Class(uint tag) => catalog.TryGetEntry(tag, out var entry) ? entry.Reference : 0;

    /**
     * @param messages Null means ReadMessages.Default.
     * @return The object a config pointer names; null for a null pointer.
     */
    internal static int? ConfigTarget(byte[] config, string member, ReadMessages? messages = null) =>
        TagView.TargetOffset(config, in AnimationControlReader.Member<SEntityConfig, RelativePointer<Unknown>>(config, 0,
            member, messages ?? ReadMessages.Default));

    private static BoneTransform Transform(ref readonly SAnimationControlDefault value)
    {
        var transform = new BoneTransform(value.Rotation, value.Translation, value.Scale);
        CheckTransform(transform);
        return transform;
    }

    /** A parent cycle is rejected before evaluating any pose. */
    private static int[] EvaluationOrder(int[] parents)
    {
        var states = new byte[parents.Length];
        var order = new List<int>(parents.Length);
        void Visit(int index)
        {
            if (states[index] == 2) return;
            if (states[index] == 1) throw new InvalidDataException("Skeleton parent cycle.");
            states[index] = 1;
            int parent = parents[index];
            if (parent < -1 || parent >= parents.Length)
                throw new InvalidDataException("Skeleton parent index is outside the hierarchy.");
            if (parent >= 0) Visit(parent);
            states[index] = 2;
            order.Add(index);
        }
        for (int i = 0; i < parents.Length; i++) Visit(i);
        return order.ToArray();
    }

    /** Every child must occur once in its parent's sibling chain. */
    private static void ValidateChildren(SkeletonBone[] bones)
    {
        var seen = new bool[bones.Length];
        foreach (var parent in bones)
        {
            int child = parent.FirstChildIndex;
            while (child >= 0)
            {
                if (seen[child] || bones[child].ParentIndex != parent.Index)
                    throw new InvalidDataException("Skeleton child chain disagrees with its parent indices.");
                seen[child] = true;
                child = bones[child].NextSiblingIndex;
            }
        }
        if (bones.Any(b => b.ParentIndex >= 0 && !seen[b.Index]))
            throw new InvalidDataException("Skeleton parent omits a child from its chain.");
    }

    internal static void CheckTransform(BoneTransform value)
    {
        float length = value.Rotation.LengthSquared();
        if (!float.IsFinite(length) || MathF.Abs(length - 1) > 0.01f
            || !float.IsFinite(value.Scale) || value.Scale <= 0
            || !float.IsFinite(value.Translation.X) || !float.IsFinite(value.Translation.Y)
            || !float.IsFinite(value.Translation.Z))
            throw new InvalidDataException("Bone transform contains invalid rotation, scale, or translation.");
    }

    private static float IdentityError(Matrix4x4 m)
    {
        float[] values = [m.M11 - 1, m.M12, m.M13, m.M14, m.M21, m.M22 - 1, m.M23, m.M24,
            m.M31, m.M32, m.M33 - 1, m.M34, m.M41, m.M42, m.M43, m.M44 - 1];
        return values.Max(MathF.Abs);
    }
}
