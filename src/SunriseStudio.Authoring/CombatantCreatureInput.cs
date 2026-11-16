using System.Numerics;
using SunriseStudio.Animation;

namespace SunriseStudio.Authoring;

/** Fits a creature read from a file to the donor's size and facing before it is built. */
public static class CombatantCreatureInput
{
    /**
     * Scales the creature about its origin and turns it, keeping every bone at scale 1.
     * @param scale Metres per file unit.
     * @param rotation Applied after the creature's own transforms, in object space.
     * @return A new input; clips keep their bone order and frame counts.
     */
    public static RiggedEntityInput Transformed(RiggedEntityInput input, float scale, Quaternion rotation)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!float.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        rotation = Quaternion.Normalize(rotation);
        BoneTransform Root(BoneTransform value) => new(Quaternion.Normalize(Quaternion.Concatenate(value.Rotation, rotation)),
            Vector3.Transform(value.Translation, rotation) * scale, value.Scale);
        // A child's local rotation is unchanged by a turn of the whole body; only its offset scales.
        BoneTransform Child(BoneTransform value) => value with { Translation = value.Translation * scale };
        var bones = input.Bones.Select(bone => bone with { BindObject = Root(bone.BindObject) }).ToArray();
        var clips = input.Clips.Select(clip => clip with
        {
            Bones = clip.Bones.Select((track, bone) => (IReadOnlyList<BoneTransform>)track
                .Select(value => input.Bones[bone].Parent < 0 ? Root(value) : Child(value)).ToArray()).ToArray()
        }).ToArray();
        var geometry = input.Geometry with
        {
            Positions = input.Geometry.Positions.Select(position => Vector3.Transform(position, rotation) * scale).ToArray(),
            Normals = input.Geometry.Normals.Select(normal => Vector3.Transform(normal, rotation)).ToArray(),
        };
        return input with { Bones = bones, Clips = clips, Geometry = geometry };
    }

    /**
     * The same creature with one box per bone in place of its mesh, each box skinned to its bone alone.
     * It separates the mesh and material path from the skeleton and rig.
     * @param half Half the box's edge, in metres.
     */
    public static RiggedEntityInput WithBoxes(RiggedEntityInput input, float half = 0.04f)
    {
        ArgumentNullException.ThrowIfNull(input);
        var positions = new List<Vector3>(); var normals = new List<Vector3>(); var uvs = new List<Vector2>();
        var indices = new List<uint>(); var weights = new List<VertexSkinWeights>();
        Vector3[] axes = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        for (int bone = 0; bone < input.Bones.Count; bone++)
        {
            var centre = input.Bones[bone].BindObject.Translation;
            foreach (var normal in axes)
            {
                // Two axes across the face, in an order that keeps the winding outward.
                var u = MathF.Abs(normal.X) > 0 ? Vector3.UnitY : Vector3.UnitX;
                var v = Vector3.Cross(normal, u);
                uint first = (uint)positions.Count;
                foreach (var (a, b) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
                {
                    positions.Add(centre + (normal + u * a + v * b) * half);
                    normals.Add(normal);
                    uvs.Add(new((a + 1) / 2, (b + 1) / 2));
                    weights.Add(new(bone, 0, 0, 0, new(1, 0, 0, 0)));
                }
                indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
            }
        }
        return input with
        {
            Geometry = new(input.Name + "_boxes", [.. positions], [.. normals], [.. uvs], [.. indices]),
            Influences = weights,
        };
    }
}
