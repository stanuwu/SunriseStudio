using System.Numerics;
using SunriseStudio.Animation;
using SunriseStudio.Formats;

namespace SunriseStudio.Authoring;

/** A stand-in skin for an installed skeleton: one box per bone, each held by one bone. */
public static class EnemyPlaceholderMesh
{
    // A box is this share of its bone's length wide, between the two limits in metres.
    private const float WidthShare = 0.3f, MinimumWidth = 0.02f, MaximumWidth = 0.12f;
    // A bone shorter than this gets a cube at its own position.
    private const float ShortBone = 0.01f;

    /**
     * Builds the boxes in the rig's bind pose. A bone with a parent gets a box from the parent to
     * itself, held by the parent; the root gets a cube.
     * @param within A box every vertex is moved into; a model replacement must stay in the installed model's bounds.
     * @return The geometry in object space and one rigid influence per vertex, in package bone order.
     */
    public static (InterchangeMesh Geometry, VertexSkinWeights[] Influences) Boxes(SkeletonRig rig, string name,
        (Vector3 Minimum, Vector3 Maximum)? within = null)
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var indices = new List<uint>();
        var influences = new List<VertexSkinWeights>();
        foreach (var bone in rig.Bones)
        {
            var end = bone.BindObject.Translation;
            var start = bone.ParentIndex >= 0 ? rig.Bones[bone.ParentIndex].BindObject.Translation : end;
            float length = Vector3.Distance(start, end);
            int holder = bone.ParentIndex >= 0 && length >= ShortBone ? bone.ParentIndex : bone.Index;
            float width = Math.Clamp(length * WidthShare, MinimumWidth, MaximumWidth);
            if (length < ShortBone) { start = end - new Vector3(0, 0, width / 2); end += new Vector3(0, 0, width / 2); }
            AddBox(positions, normals, indices, start, end, width);
            while (influences.Count < positions.Count) influences.Add(new(holder, 0, 0, 0, new(1, 0, 0, 0)));
        }
        if (within is { } bounds)
            for (int vertex = 0; vertex < positions.Count; vertex++) positions[vertex] = Vector3.Clamp(positions[vertex], bounds.Minimum, bounds.Maximum);
        return (new(name, positions.ToArray(), normals.ToArray(), new Vector2[positions.Count], indices.ToArray()), influences.ToArray());
    }

    /** Adds a box along start to end with a square cross-section, four vertices per face. */
    private static void AddBox(List<Vector3> positions, List<Vector3> normals, List<uint> indices, Vector3 start, Vector3 end, float width)
    {
        var axis = Vector3.Normalize(end - start);
        var side = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX));
        var up = Vector3.Cross(axis, side);
        side *= width / 2; up *= width / 2;
        // Each face: its outward normal and its corners, counter-clockwise seen from outside.
        (Vector3 Normal, Vector3[] Corners)[] faces =
        [
            (Vector3.Normalize(side), [start + side - up, end + side - up, end + side + up, start + side + up]),
            (-Vector3.Normalize(side), [start - side + up, end - side + up, end - side - up, start - side - up]),
            (Vector3.Normalize(up), [start + side + up, end + side + up, end - side + up, start - side + up]),
            (-Vector3.Normalize(up), [start - side - up, end - side - up, end + side - up, start + side - up]),
            (axis, [end + side - up, end - side - up, end - side + up, end + side + up]),
            (-axis, [start + side + up, start - side + up, start - side - up, start + side - up]),
        ];
        foreach (var (normal, corners) in faces)
        {
            uint first = (uint)positions.Count;
            positions.AddRange(corners);
            for (int corner = 0; corner < 4; corner++) normals.Add(normal);
            indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }
    }
}
