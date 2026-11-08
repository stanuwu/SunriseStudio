using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Animation;

/** Type-zero correction rows distribute an authored rotation around a selected axis. */
internal sealed record AnimationBoneCorrection(int Target, int Parent, int Source, int Reference,
    float Weight, BoneTransform Local, Quaternion FirstRotation, Quaternion SecondRotation,
    Quaternion ReferenceRotation, Vector3 Axis)
{
    /** Corrections run after limb solving and before inverse-bind palette construction. */
    internal void Apply(BoneTransform[] pose)
    {
        var relative = Quaternion.Normalize(Quaternion.Conjugate(pose[Reference].Rotation) * pose[Source].Rotation);
        var change = Quaternion.Normalize(ReferenceRotation * relative);
        var axis = Vector3.Normalize(Axis);
        var swing = AnimationIkSolver.FromTo(axis, Vector3.Normalize(Vector3.Transform(axis, change)), Vector3.UnitZ);
        var twist = Quaternion.Normalize(Quaternion.Conjugate(swing) * change);
        float amount = Parent == Reference ? Math.Clamp(1 - Weight, 0, 1) : Weight;
        if (twist.W < 0) twist = new(-twist.X, -twist.Y, -twist.Z, -twist.W);
        var blended = Quaternion.Normalize(new Quaternion(twist.X * amount, twist.Y * amount,
            twist.Z * amount, 1 + (twist.W - 1) * amount));
        var rotation = Quaternion.Normalize(SecondRotation * FirstRotation * blended);
        if (Parent != Reference) rotation = Quaternion.Conjugate(rotation);
        pose[Target] = AnimationIkSolver.Compose(AnimationIkSolver.Compose(pose[Parent], Local),
            new BoneTransform(rotation, Vector3.Zero, 1));
    }

    /** The owning animation component stores fixed 256-byte correction rows. */
    internal static AnimationBoneCorrection[] Read(PackageCatalog catalog, IEnumerable<uint> tags, int boneCount)
    {
        var owners = tags.Distinct().ToArray();
        if (owners.Length > 1)
            throw new NotSupportedException("Multiple animation components need explicit correction-owner selection.");
        var result = new List<AnimationBoneCorrection>();
        foreach (uint tag in owners)
        {
            byte[] bytes = catalog.Read(tag);
            ref readonly var definition = ref AnimationControlReader.Member<SEntityConfig, RelativePointer<Unknown>>(
                bytes, 0, nameof(SEntityConfig.Definition));
            if (TagView.TargetOffset(bytes, in definition) is not { } resource
                || AnimationControlReader.ClassBefore(bytes, resource) != SAnimationCorrectionOwnerResource.Class)
                throw new NotSupportedException("Animation component has an unsupported bone-correction owner.");
            foreach (ref readonly var row in AnimationControlReader.Rows<SAnimationBoneCorrection>(bytes,
                resource + TagView.FieldOffset<SAnimationCorrectionOwnerResource>(nameof(SAnimationCorrectionOwnerResource.BoneCorrections)), SAnimationBoneCorrection.Class))
            {
                if (row.CorrectionType != 0)
                    throw new NotSupportedException("Animation bone-correction type is not decoded.");
                int target = row.TargetBone, parent = row.ParentBone;
                int source = row.SourceBone, reference = row.ReferenceBone;
                if (new[] { target, parent, source, reference }.Any(index => index >= boneCount))
                    throw new InvalidDataException("Bone correction refers outside its skeleton.");
                var local = new BoneTransform(Q(row.LocalRotation), V3(row.LocalTranslation),
                    AnimationControlReader.Finite(row.LocalScale));
                SkeletonReader.CheckTransform(local);
                float weight = AnimationControlReader.Finite(row.Weight);
                Vector3 axis = V3(row.TwistAxis);
                if (!float.IsFinite(weight) || weight < 0 || weight > 1
                    || !float.IsFinite(axis.LengthSquared()) || axis.LengthSquared() < 1e-8f)
                    throw new InvalidDataException("Bone correction has invalid axis or weight.");
                result.Add(new(target, parent, source, reference, weight, local,
                    Q(row.FirstRotation), Q(row.SecondRotation), Q(row.ReferenceRotation), axis));
            }
        }
        return result.ToArray();
    }

    private static Quaternion Q(Quaternion stored)
    {
        var value = new Quaternion(AnimationControlReader.Finite(stored.X), AnimationControlReader.Finite(stored.Y),
            AnimationControlReader.Finite(stored.Z), AnimationControlReader.Finite(stored.W));
        SkeletonReader.CheckTransform(new(value, Vector3.Zero, 1));
        return Quaternion.Normalize(value);
    }

    private static Vector3 V3(Vector3 stored) => new(
        AnimationControlReader.Finite(stored.X), AnimationControlReader.Finite(stored.Y), AnimationControlReader.Finite(stored.Z));
}
