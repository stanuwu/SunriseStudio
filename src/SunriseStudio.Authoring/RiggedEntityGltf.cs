using System.Numerics;
using SunriseStudio.Animation;
using SunriseStudio.Formats;

namespace SunriseStudio.Authoring;

/** Reads a skinned GLB with named actions as the input of RiggedEntityBuilder. */
public static class RiggedEntityGltf
{
    // glTF Y-up to model Z-up, the conversion GltfInterchange.ReadSkinned applies to the bind pose.
    private static readonly Matrix4x4 Space = new(1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1);
    private const int FramesPerSecond = 30;

    /**
     * One skinned mesh, its armature and every named action; an action becomes a clip of its name.
     * Joint names must be unique. Joints are reordered so each parent comes before its children.
     * @param plays The action the entity plays by itself.
     */
    public static RiggedEntityInput Read(string path, string name, string plays)
    {
        var skinned = GltfInterchange.ReadSkinned(path);
        var joints = skinned.Joints;
        if (joints.Select(joint => joint.Name).Distinct(StringComparer.Ordinal).Count() != joints.Count || joints.Any(joint => joint.Name.Length == 0))
            throw new InvalidDataException("Each joint needs its own name.");
        // Parents first, otherwise in file order.
        var order = new List<int>();
        var placed = new bool[joints.Count];
        while (order.Count < joints.Count)
        {
            int before = order.Count;
            for (int index = 0; index < joints.Count; index++)
                if (!placed[index] && (joints[index].ParentIndex < 0 || placed[joints[index].ParentIndex])) { placed[index] = true; order.Add(index); }
            if (order.Count == before) throw new InvalidDataException("The joint hierarchy has a cycle.");
        }
        int[] moved = new int[joints.Count];
        for (int index = 0; index < order.Count; index++) moved[order[index]] = index;
        var bones = order.Select(old => new RiggedBone(joints[old].Name, joints[old].ParentIndex < 0 ? -1 : moved[joints[old].ParentIndex],
            BoneTransform.FromMatrix(joints[old].BindObject))).ToArray();
        int Bone(int old, float weight) => weight > 0 ? moved[old] : 0;
        var influences = skinned.Influences.Select(value => new VertexSkinWeights(Bone(value.Bone0, value.Weights.X), Bone(value.Bone1, value.Weights.Y),
            Bone(value.Bone2, value.Weights.Z), Bone(value.Bone3, value.Weights.W), value.Weights)).ToArray();

        string[] names = bones.Select(bone => bone.Name).ToArray();
        var clips = new List<RiggedClip>();
        foreach (var (action, seconds) in WeaponGltfAnimationReader.Actions(path))
        {
            int frames = Math.Max(2, (int)MathF.Round(seconds * FramesPerSecond) + 1);
            var sampled = WeaponGltfAnimationReader.ReadSkins(path, action, names, frames, 1, Space, meanScale: true);
            var tracks = bones.Select(_ => new BoneTransform[frames]).ToArray();
            for (int frame = 0; frame < frames; frame++)
            {
                var pose = new Matrix4x4[bones.Length];
                for (int bone = 0; bone < bones.Length; bone++)
                {
                    pose[bone] = bones[bone].BindObject.Matrix * sampled.Skins[frame][bone];
                    Matrix4x4 local = pose[bone];
                    if (bones[bone].Parent >= 0)
                    {
                        if (!Matrix4x4.Invert(pose[bones[bone].Parent], out var inverse)) throw new InvalidDataException($"Action {action} collapses a joint.");
                        local *= inverse;
                    }
                    tracks[bone][frame] = BoneTransform.FromMatrix(local);
                }
            }
            clips.Add(new(action, tracks));
        }
        if (clips.Count == 0) throw new InvalidDataException("The GLB holds no named action.");
        return new(name, bones, skinned.Geometry, influences, clips, plays);
    }
}
