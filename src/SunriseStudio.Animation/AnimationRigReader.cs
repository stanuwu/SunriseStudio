using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using FrameView = SunriseStudio.Tags.Generated.AnimationFrameReference;

namespace SunriseStudio.Animation;

public sealed record AnimationFrameReference(short BoneIndex, short SecondBoneIndex, byte Kind,
    AnimationFrameReference? TranslationFrame, float AimWeight, short AimTarget, byte AimSource);

/** A clip whose keys meet these keys has its control translations multiplied by the scale. */
public sealed record AnimationRetarget(IReadOnlyList<uint> Keys, float TranslationScale);

public sealed record AnimationRigControl(int Index, uint NameHash, short BoneIndex, short ChainBoneIndex,
    byte CompositionMode, byte ChainMode, AnimationFrameReference RotationFrame,
    AnimationFrameReference TranslationFrame, BoneTransform DefaultValue)
{
    public byte PoleAxis { get; init; }
}

/** The authored control-to-bone table is separate from the render skeleton. */
public sealed class AnimationRigReader(PackageCatalog catalog)
{
    /** The rig and skeleton must come from the same entity construction list. */
    public AnimationRigBinding Read(SkeletonRig skeleton)
    {
        if (skeleton.EntityTag == 0)
            throw new NotSupportedException("Animation binding needs the entity that owns the skeleton.");
        byte[] entity = catalog.Read(skeleton.EntityTag);
        var candidates = new List<uint>();
        foreach (ref readonly var row in AnimationControlReader.Rows<SPatternComponent>(entity,
            TagView.FieldOffset<SEntity>(nameof(SEntity.Components)), SPatternComponent.Class))
        {
            uint tag = row.Config;
            if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != SEntityConfig.Class) continue;
            byte[] data = catalog.Read(tag);
            int primary = Target(data, "DefaultInstance"), secondary = Target(data, "Definition");
            if (AnimationControlReader.ClassBefore(data, primary) == SAnimationRigPrimary.Class
                && AnimationControlReader.ClassBefore(data, secondary) == SAnimationRigResource.Class)
                candidates.Add(tag);
        }
        if (candidates.Distinct().Count() != 1)
            throw new NotSupportedException($"Entity has {candidates.Distinct().Count()} supported animation rigs.");
        uint resourceTag = candidates[0];
        byte[] rig = catalog.Read(resourceTag);
        int resource = Target(rig, "Definition");
        var rows = AnimationControlReader.Rows<SAnimationControl>(rig,
            resource + TagView.FieldOffset<SAnimationRigResource>(nameof(SAnimationRigResource.Controls)), SAnimationControl.Class);
        var defaults = AnimationControlReader.Rows<SAnimationControlDefault>(rig,
            resource + TagView.FieldOffset<SAnimationRigResource>(nameof(SAnimationRigResource.ControlDefaults)), SAnimationControlDefault.Class);
        if (rows.Length == 0 || rows.Length != defaults.Length)
            throw new InvalidDataException("Animation control count differs from its authored default poses.");
        var controls = new AnimationRigControl[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            ref readonly var row = ref rows[i];
            ref readonly var value = ref defaults[i];
            short bone = row.BoneIndex, chain = row.ChainBoneIndex;
            byte composition = row.CompositionMode, chainMode = row.ChainMode;
            if (bone < 0 || bone >= skeleton.Bones.Count || chain < -1 || chain >= skeleton.Bones.Count
                || composition > 1 || chainMode is not (0 or 1 or 255))
                throw new NotSupportedException("Animation rig control uses an unsupported bone or composition mode.");
            var transform = new BoneTransform(Finite(value.Rotation), new Vector3(AnimationControlReader.Finite(value.Translation.X),
                AnimationControlReader.Finite(value.Translation.Y), AnimationControlReader.Finite(value.Translation.Z)),
                AnimationControlReader.Finite(value.Scale));
            SkeletonReader.CheckTransform(transform);
            controls[i] = new AnimationRigControl(i, row.ControlName, bone, chain, composition, chainMode,
                Frame(in row.RotationFrame, skeleton.Bones.Count), Frame(in row.TranslationFrame, skeleton.Bones.Count), transform)
            { PoleAxis = row.PoleAxis };
        }
        var links = new EntityAnimationCatalog(catalog).Read(skeleton.EntityTag);
        var clips = links.Select(x => x.ClipTag).ToHashSet();
        var groups = AnimationControlReader.Groups(rig, resource + TagView.FieldOffset<SAnimationRigResource>(nameof(SAnimationRigResource.ControlGroups)));
        if (groups.Any(group => group.ControlCount < 0)
            || groups.Sum(group => (long)group.ControlCount) != controls.Length)
            throw new InvalidDataException("Rig control groups disagree with its control count.");
        var ikRows = AnimationControlReader.Rows<SI16Element>(rig,
            resource + TagView.FieldOffset<SAnimationRigResource>(nameof(SAnimationRigResource.IkControls)), SI16Element.Class);
        var ikControls = new short[ikRows.Length];
        for (int i = 0; i < ikControls.Length; i++) ikControls[i] = ikRows[i].Value;
        if (ikControls.Any(index => index < 0 || index >= controls.Length)
            || ikControls.Distinct().Count() != ikControls.Length)
            throw new InvalidDataException("Animation IK list contains invalid control indices.");
        return new AnimationRigBinding(skeleton, resourceTag, controls, clips, groups,
            ikControls.Select(index => (int)index).ToArray(),
            AnimationControlReader.Member<SAnimationRigResource, byte>(rig, resource, nameof(SAnimationRigResource.RigFlag)) != 0,
            AnimationBoneCorrection.Read(catalog, links.Select(x => x.ConfigTag).Distinct(), skeleton.Bones.Count))
        {
            Retargets = ReadRetargets(AnimationControlReader.Member<SAnimationRigResource, uint>(rig, resource, nameof(SAnimationRigResource.RetargetTable)),
                controls.Length)
        };
    }

    /** Where a config pointer points; a null pointer gives the field itself. */
    private static int Target(byte[] config, string pointer) => TagView.TargetOrSelf(config,
        in AnimationControlReader.Member<SEntityConfig, RelativePointer<Unknown>>(config, 0, pointer));

    /**
     * The rig's retarget table: rows of keys and a translation scale for clips authored on another body.
     * Rows with a second factor, control offsets or control rotations are refused; no installed table has them.
     */
    private AnimationRetarget[] ReadRetargets(uint tag, int controlCount)
    {
        if (tag is 0 or uint.MaxValue || !catalog.TryGetEntry(tag, out var entry)) return [];
        if (entry.Reference != SAnimationRetargetTable.Class)
            throw new InvalidDataException($"Animation retarget link 0x{tag:X8} is not a class 0x{SAnimationRetargetTable.Class:X8} table.");
        byte[] table = catalog.Read(tag);
        var rows = AnimationControlReader.Rows<SAnimationRetargetRow>(table,
            TagView.FieldOffset<SAnimationRetargetTable>(nameof(SAnimationRetargetTable.RetargetRows)), SAnimationRetargetRow.Class);
        var result = new AnimationRetarget[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            ref readonly var row = ref rows[i];
            float scale = AnimationControlReader.Finite(row.TranslationScale);
            float second = AnimationControlReader.Finite(row.SecondFactor);
            var offsets = AnimationControlReader.Rows<SVec4Element>(table, TagView.OffsetOf(table, in row.ControlOffsets),
                SVec4Element.Class).ToArray();
            var rotations = AnimationControlReader.Rows<SQuatElement>(table, TagView.OffsetOf(table, in row.RetargetRotations),
                SQuatElement.Class).ToArray();
            if (!float.IsFinite(scale) || scale <= 0 || second != 1 || offsets.Length is not 0 && offsets.Length != controlCount
                || offsets.Any(at => AnimationControlReader.Finite(at.Offset.X) != 0 || AnimationControlReader.Finite(at.Offset.Y) != 0
                    || AnimationControlReader.Finite(at.Offset.Z) != 0 || AnimationControlReader.Finite(at.Offset.W) != 0)
                || rotations.Any(at => AnimationControlReader.Finite(at.Rotation.X) != 0 || AnimationControlReader.Finite(at.Rotation.Y) != 0
                    || AnimationControlReader.Finite(at.Rotation.Z) != 0 || AnimationControlReader.Finite(at.Rotation.W) != 1))
                throw new NotSupportedException("Animation retarget row uses fields that are not decoded.");
            result[i] = new AnimationRetarget(Array.AsReadOnly(AnimationControlReader.Keys(table,
                TagView.OffsetOf(table, in row.Key.SkeletonNames))), scale);
        }
        return result;
    }

    /** Quaternion components are checked in X, Y, Z, W order. */
    private static Quaternion Finite(Quaternion value) => new(AnimationControlReader.Finite(value.X),
        AnimationControlReader.Finite(value.Y), AnimationControlReader.Finite(value.Z), AnimationControlReader.Finite(value.W));

    /** An aim frame holds a nested frame; the nested frame supplies its translation. */
    private static AnimationFrameReference Frame(ref readonly FrameView frame, int bones)
    {
        short first = frame.BoneIndex, second = frame.SecondBoneIndex;
        byte kind = frame.Kind;
        if (first < -1 || first >= bones || second < -1 || second >= bones || kind > 6)
            throw new NotSupportedException("Animation reference frame uses an unsupported bone or kind.");
        if (kind != 6) return new(first, second, kind, null, 0, -1, 0);
        short target = frame.AimTarget;
        byte source = frame.AimSource;
        if (source > 2 || target < -1 || target >= bones)
            throw new NotSupportedException("Animation aim frame uses an unsupported source or depth.");
        return new(first, second, kind, Frame(in frame.TranslationFrame, bones),
            AnimationControlReader.Finite(frame.AimWeight), target, source);
    }

    /** A nested frame may not be an aim frame. */
    private static AnimationFrameReference Frame(ref readonly AnimationBasicFrameReference frame, int bones)
    {
        short first = frame.BoneIndex, second = frame.SecondBoneIndex;
        byte kind = frame.Kind;
        if (first < -1 || first >= bones || second < -1 || second >= bones || kind > 6)
            throw new NotSupportedException("Animation reference frame uses an unsupported bone or kind.");
        if (kind != 6) return new(first, second, kind, null, 0, -1, 0);
        throw new NotSupportedException("Animation aim frame uses an unsupported source or depth.");
    }
}
