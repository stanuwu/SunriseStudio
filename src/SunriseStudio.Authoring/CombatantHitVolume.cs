using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Animation;
using SunriseStudio.Scene.Collision;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/**
 * Moves a donor's hit bodies onto its root bone as one box around a creature.
 * The root bone is the entity's own frame in every clip, so the box follows the creature.
 */
internal static class CombatantHitVolume
{
    // A body row whose low mode bits are 2 is a hit body.
    private const int HitMode = 2, ModeMask = 3;
    // Bone 0 is the entity root; the creature's clips place it relative to this bone.
    private const ushort RootBone = 0;
    // The other hit bodies shrink about the centre to this fraction, so the outer box is hit first.
    private const float InnerFraction = 0.5f;
    // hkpConvexVerticesShape: the box, the vertex blocks and the planes.
    private const int HalfExtents = 0x30, Centre = 0x40, VertexBlocks = 0x50, Planes = 0x68;
    // A vertex block holds four points as x[4], y[4], z[4].
    private const int BlockSize = 48, BlockLane = 16, PlaneSize = 16, BoxCorners = 8, BoxFaces = 6;

    /**
     * The box around the creature's skinned mesh over every frame of its idle clip, in entity space.
     * @param creature The creature, already scaled and turned as the entity draws it.
     * @param idle The clip the creature holds when a shared donor clip plays.
     */
    public static (Vector3 Min, Vector3 Max) IdleBox(RiggedEntityInput creature, string idle)
    {
        var clip = creature.Clips.First(value => value.Name == idle);
        var inverse = creature.Bones.Select(bone => Matrix4x4.Invert(bone.BindObject.Matrix, out var value) ? value
            : throw new InvalidDataException("A creature bind transform is singular.")).ToArray();
        var positions = creature.Geometry.Positions;
        if (creature.Influences.Count != positions.Length) throw new InvalidDataException("The creature has no weights for every vertex.");
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        var objects = new Matrix4x4[creature.Bones.Count];
        var skin = new Matrix4x4[creature.Bones.Count];
        for (int frame = 0; frame < clip.Bones[0].Count; frame++)
        {
            // Clip tracks are parent-relative; a parent comes before its children.
            for (int bone = 0; bone < objects.Length; bone++)
            {
                int parent = creature.Bones[bone].Parent;
                objects[bone] = parent < 0 ? clip.Bones[bone][frame].Matrix : clip.Bones[bone][frame].Matrix * objects[parent];
                skin[bone] = inverse[bone] * objects[bone];
            }
            for (int vertex = 0; vertex < positions.Length; vertex++)
            {
                var weights = creature.Influences[vertex];
                var at = Vector3.Transform(positions[vertex], skin[weights.Bone0]) * weights.Weights.X
                    + Vector3.Transform(positions[vertex], skin[weights.Bone1]) * weights.Weights.Y
                    + Vector3.Transform(positions[vertex], skin[weights.Bone2]) * weights.Weights.Z
                    + Vector3.Transform(positions[vertex], skin[weights.Bone3]) * weights.Weights.W;
                min = Vector3.Min(min, at);
                max = Vector3.Max(max, at);
            }
        }
        return (min, max);
    }

    /**
     * Binds every hit body of a physics config to the root bone and writes its convex shape as a box.
     * The first hit body takes the whole box; the others take a smaller box inside it.
     * @param config The physics model config that names the packfile.
     * @param packfile The packfile the config names, already copied.
     * @param packfileTag The installed tag the config names for that packfile.
     * @param min The box's low corner in entity space, as IdleBox gives it.
     * @param max The box's high corner in entity space.
     * @return The config and the packfile with the hit bodies moved.
     */
    public static (byte[] Config, byte[] Packfile) Write(byte[] config, byte[] packfile, uint packfileTag, Vector3 min, Vector3 max)
    {
        byte[] edited = (byte[])config.Clone();
        ref readonly var view = ref TagView.Checked<SEntityConfig>(edited, 0);
        if (view.Definition.IsNull || TagView.TargetClass(edited, in view.Definition) != UnknownPhysicsModelPayload.Class)
            throw new InvalidDataException("The physics holder is not a physics model config.");
        int payload = TagView.TargetOffset(edited, in view.Definition)!.Value;
        if (TagView.Member<UnknownPhysicsModelPayload, uint>(edited, payload, nameof(UnknownPhysicsModelPayload.PhysicsPackfile)) != packfileTag)
            throw new InvalidDataException("The physics model config names another packfile.");
        ref readonly var array = ref TagView.Member<UnknownPhysicsModelPayload, TagArray<UnknownPhysicsBodyRow>>(edited, payload,
            nameof(UnknownPhysicsModelPayload.Bodies));
        var rows = TagView.EditRows(edited, in array, UnknownPhysicsBodyRow.Class);
        byte[] output = (byte[])packfile.Clone();
        var file = HavokPackfile.Parse(output);
        var (count, shapes) = file.Array(file.RootOffset + 0x20);
        Vector3 centre = (min + max) / 2, half = (max - min) / 2;
        int moved = 0;
        for (int row = 0; row < rows.Length; row++)
        {
            if ((rows[row].BodyMode & ModeMask) != HitMode) continue;
            int shape = rows[row].ShapeIndex;
            if (shape < 0 || shape >= count || file.Pointer(shapes + 8 * shape) is not int at || file.ClassAt(at) != CollisionShapes.ConvexVertices)
                throw new InvalidDataException($"Hit body {row} has no convex vertices shape.");
            var size = moved == 0 ? half : half * InnerFraction;
            WriteBox(file, output, at, centre - size, centre + size);
            rows[row].Bone = RootBone;
            moved++;
        }
        if (moved == 0) throw new InvalidDataException("The physics model config has no hit body.");
        return (edited, output);
    }

    /** Overwrites a convex vertices shape with a box; every vertex lane and plane row is reused. */
    private static void WriteBox(HavokPackfile file, byte[] output, int shape, Vector3 min, Vector3 max)
    {
        var (blockCount, blocks) = file.Array(shape + VertexBlocks);
        var (planeCount, planes) = file.Array(shape + Planes);
        if (blockCount * 4 < BoxCorners || planeCount < BoxFaces) throw new InvalidDataException("A hit shape is too small to hold a box.");
        for (int lane = 0; lane < blockCount * 4; lane++)
        {
            int corner = lane % BoxCorners;
            var point = new Vector3((corner & 1) != 0 ? max.X : min.X, (corner & 2) != 0 ? max.Y : min.Y, (corner & 4) != 0 ? max.Z : min.Z);
            int block = file.Absolute(blocks + BlockSize * (lane / 4)) + sizeof(float) * (lane % 4);
            Put(output, block, point.X);
            Put(output, block + BlockLane, point.Y);
            Put(output, block + 2 * BlockLane, point.Z);
        }
        // Outward normal and offset, so a point on the face gives zero.
        Vector4[] faces =
        [
            new(1, 0, 0, -max.X), new(-1, 0, 0, min.X), new(0, 1, 0, -max.Y),
            new(0, -1, 0, min.Y), new(0, 0, 1, -max.Z), new(0, 0, -1, min.Z),
        ];
        // Spare plane rows repeat a face, which changes nothing.
        for (int index = 0; index < planeCount; index++)
        {
            var face = faces[index % BoxFaces];
            int at = file.Absolute(planes + index * PlaneSize);
            Put(output, at, face.X); Put(output, at + 4, face.Y); Put(output, at + 8, face.Z); Put(output, at + 12, face.W);
        }
        Vector3 half = (max - min) / 2, centre = (max + min) / 2;
        int extents = file.Absolute(shape + HalfExtents), middle = file.Absolute(shape + Centre);
        Put(output, extents, half.X); Put(output, extents + 4, half.Y); Put(output, extents + 8, half.Z);
        Put(output, middle, centre.X); Put(output, middle + 4, centre.Y); Put(output, middle + 8, centre.Z);
    }

    private static void Put(byte[] bytes, int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value);
}
