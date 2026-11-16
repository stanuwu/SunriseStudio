using System.Numerics;

namespace SunriseStudio.Animation;

/** How far a converted clip lands from the pose it was given, over every frame and bone. */
public sealed record CombatantPoseError(int Frames, int Bones, float MaximumPosition, float MaximumRotationDegrees,
    int WorstFrame, int WorstBone);

/** How far converted control values lie from a stock clip's own values, over its authored controls. */
public sealed record CombatantControlError(int Controls, float MaximumTranslation, float MaximumRotationDegrees,
    int WorstControl);

/** Turns object-space skeleton poses into the control values a combatant's own rig plays. */
public static class CombatantAnimationConverter
{
    /**
     * Control values for each frame of a pose clip on the donor's skeleton and rig.
     * @param binding The donor entity's rig; poses use its skeleton's bone order.
     * @param poses Object transforms per frame, one per skeleton bone, before twist corrections.
     * @return One value per rig control per frame, in control order.
     */
    public static BoneTransform[][] PosesToControls(AnimationRigBinding binding, SkeletonRig skeleton,
        IReadOnlyList<IReadOnlyList<BoneTransform>> poses, int iterations = DefaultIterations)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(poses);
        var scratch = skeleton.CreateBindPose();
        return poses.Select(pose => Solve(binding, scratch, pose, iterations)).ToArray();
    }

    /** Refinement passes; limb solving moves the limb roots, so one inverse pass misses the knees and elbows. */
    public const int DefaultIterations = 12;

    /**
     * Control values whose solved pose meets the targets. Each pass moves the inverse's targets by what the
     * limb solve left over.
     */
    private static BoneTransform[] Solve(AnimationRigBinding binding, SkeletonPose scratch,
        IReadOnlyList<BoneTransform> targets, int iterations)
    {
        var aim = targets.ToArray();
        var values = binding.Solve(aim);
        for (int pass = 0; pass < iterations; pass++)
        {
            var reached = Pose(binding, scratch, values);
            for (int bone = 0; bone < aim.Length; bone++)
            {
                var delta = Quaternion.Normalize(targets[bone].Rotation * Quaternion.Conjugate(reached[bone].Rotation));
                aim[bone] = new BoneTransform(Quaternion.Normalize(delta * aim[bone].Rotation),
                    aim[bone].Translation + targets[bone].Translation - reached[bone].Translation, aim[bone].Scale);
            }
            values = binding.Solve(aim);
        }
        return values;
    }

    /**
     * Parent-relative samples per bone, the form a one-control-per-bone rig plays.
     * @param skeleton The new skeleton; poses use its bone order.
     * @param poses Object transforms per frame, one per bone.
     * @return Samples per bone, then per frame, as RigClipCompiler takes them.
     */
    public static BoneTransform[][] PosesToBoneControls(SkeletonRig skeleton, IReadOnlyList<IReadOnlyList<BoneTransform>> poses)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(poses);
        var result = new BoneTransform[skeleton.Bones.Count][];
        for (int bone = 0; bone < result.Length; bone++)
        {
            result[bone] = new BoneTransform[poses.Count];
            int parent = skeleton.Bones[bone].ParentIndex;
            for (int frame = 0; frame < poses.Count; frame++)
            {
                var pose = poses[frame];
                if (pose.Count != skeleton.Bones.Count) throw new ArgumentException("Pose bone count differs from the skeleton.");
                result[bone][frame] = parent < 0 ? pose[bone] : Relative(pose[parent], pose[bone]);
            }
        }
        return result;
    }

    /**
     * Rig control values one frame of a base clip gives, the way the game samples it.
     * @param clip A pose mode 0 clip the rig links.
     * @param frame Whole frame at 30 per second.
     * @return One value per control; controls the clip does not write keep the rig defaults.
     */
    public static BoneTransform[] SampleControls(AnimationRigBinding binding, AnimationControlClip clip, int frame)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.PoseMode != 0) throw new NotSupportedException("Only a base clip gives a whole pose.");
        int[] map = binding.ChannelMap(clip);
        float scale = binding.TranslationScale(clip);
        var values = binding.Controls.Select(control => control.DefaultValue).ToArray();
        foreach (var sample in clip.Sample(frame / 30f))
        {
            int control = (uint)sample.ControlIndex < (uint)map.Length ? map[sample.ControlIndex] : -1;
            if (control < 0) continue;
            var old = values[control];
            values[control] = new BoneTransform(sample.HasRotation ? sample.Rotation : old.Rotation,
                sample.HasTranslation ? sample.Translation * scale : old.Translation, sample.HasScale ? sample.Scale : old.Scale);
        }
        return values;
    }

    /** Controls the clip writes through the rig's channel map. */
    public static int[] AuthoredControls(AnimationRigBinding binding, AnimationControlClip clip)
    {
        int[] map = binding.ChannelMap(clip);
        return clip.Tracks.Select(track => (uint)track.ControlIndex < (uint)map.Length ? map[track.ControlIndex] : -1)
            .Where(control => control >= 0).Distinct().Order().ToArray();
    }

    /**
     * The pose the game would draw for control values, after limb solving and before twist corrections.
     * @return Object transforms per bone, scale dropped.
     */
    public static BoneTransform[] Pose(AnimationRigBinding binding, SkeletonPose pose, IReadOnlyList<BoneTransform> values)
    {
        binding.ApplyValues(values, pose);
        var skin = pose.ClothInputSkinMatrices;
        var bones = pose.Rig.Bones;
        var result = new BoneTransform[bones.Count];
        for (int bone = 0; bone < result.Length; bone++)
            result[bone] = BoneTransform.FromMatrix(bones[bone].BindObject.Matrix * skin[bone]);
        return result;
    }

    /**
     * Samples a stock clip to poses, converts them back and compares both in pose space and in control space.
     * @param iterations Refinement passes; 0 is the plain inverse.
     * @return The pose error, and the control error over the controls the clip writes.
     */
    public static (CombatantPoseError Pose, CombatantControlError Control) RoundTrip(AnimationRigBinding binding,
        SkeletonRig skeleton, AnimationControlClip clip, int iterations = DefaultIterations)
    {
        var scratch = skeleton.CreateBindPose();
        var stock = Enumerable.Range(0, clip.FrameCount).Select(frame => SampleControls(binding, clip, frame)).ToArray();
        var poses = stock.Select(values => (IReadOnlyList<BoneTransform>)Pose(binding, scratch, values)).ToArray();
        var converted = PosesToControls(binding, skeleton, poses, iterations);
        float position = 0, angle = 0;
        int worstFrame = 0, worstBone = 0;
        for (int frame = 0; frame < poses.Length; frame++)
        {
            var again = Pose(binding, scratch, converted[frame]);
            for (int bone = 0; bone < again.Length; bone++)
            {
                float p = Vector3.Distance(again[bone].Translation, poses[frame][bone].Translation);
                float a = Degrees(again[bone].Rotation, poses[frame][bone].Rotation);
                if (p > position) { position = p; worstFrame = frame; worstBone = bone; }
                angle = Math.Max(angle, a);
            }
        }
        int[] authored = AuthoredControls(binding, clip);
        float translation = 0, rotation = 0;
        int worstControl = -1;
        for (int frame = 0; frame < poses.Length; frame++)
            foreach (int control in authored)
            {
                float t = Vector3.Distance(stock[frame][control].Translation, converted[frame][control].Translation);
                float r = Degrees(stock[frame][control].Rotation, converted[frame][control].Rotation);
                if (Math.Max(t, r / 100) > Math.Max(translation, rotation / 100)) worstControl = control;
                translation = Math.Max(translation, t);
                rotation = Math.Max(rotation, r);
            }
        return (new(poses.Length, skeleton.Bones.Count, position, angle, worstFrame, worstBone),
            new(authored.Length, translation, rotation, worstControl));
    }

    /** The angle between two rotations, sign of the quaternion ignored. */
    public static float Degrees(Quaternion a, Quaternion b)
    {
        float dot = Math.Clamp(Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0, 1);
        return 2 * MathF.Acos(dot) * 180 / MathF.PI;
    }

    private static BoneTransform Relative(BoneTransform parent, BoneTransform child)
    {
        if (!Matrix4x4.Invert(parent.Matrix, out var inverse)) throw new InvalidDataException("Parent transform is singular.");
        return BoneTransform.FromMatrix(child.Matrix * inverse);
    }
}
