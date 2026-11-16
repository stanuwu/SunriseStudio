using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Authoring;

/**
 * Writes the Havok shape of a trigger volume with a convex floor. The client tests the player
 * against this shape, not against the volume row's polygon. The shape is an installed prism-list
 * shape array with its numbers replaced, so every Havok header, pointer and class stays as shipped.
 */
public static class TriggerShapeWriter
{
    /** An installed shape array that holds one list of two triangular prisms. */
    public const uint Template = 0x80B9B980;
    // Installed shape arrays with one list of 1, 3 and 13 triangular prisms.
    private const uint TriangleTemplate = 0x80BE643E, PentagonTemplate = 0x80B2E6AA, LargeTemplate = 0x814FC35F;
    /** The most corners a floor can have: the largest template holds 13 prisms. */
    public const int MaximumCorners = 15;

    /** The installed shape array a floor of this many corners is written over. */
    public static uint TemplateFor(int corners) => corners switch
    {
        3 => TriangleTemplate,
        4 => Template,
        5 => PentagonTemplate,
        >= 6 and <= MaximumCorners => LargeTemplate,
        _ => throw new ArgumentException($"A trigger volume floor has 3 to {MaximumCorners} corners."),
    };

    // hkpListShape: its children array, and the box around them.
    private const int ListChildren = 0x30, ListHalfExtents = 0x50, ListCentre = 0x60, ChildStride = 32;
    // hkpConvexVerticesShape: the box, the vertex blocks, the vertex count and the planes.
    private const int HalfExtents = 0x30, Centre = 0x40, VertexBlocks = 0x50, VertexCount = 0x60, Planes = 0x68;
    // A vertex block holds four points as x[4], y[4], z[4].
    private const int BlockSize = 48, BlockLane = 16, PrismVertices = 6, PrismBlocks = 2, PlaneSize = 16, PrismPlanes = 5;

    /**
     * @param template The bytes of the TemplateFor tag of this corner count.
     * @param corners The floor's corners in map space, in order around it; their Z is the floor.
     * @param height How far the volume reaches up from the floor.
     * @return The shape array with one shape: the prism over that floor.
     */
    public static byte[] Polygon(byte[] template, IReadOnlyList<Vector3> corners, float height)
    {
        ArgumentNullException.ThrowIfNull(corners);
        if (corners.Count is < 3 or > MaximumCorners || !(height > 0) || corners.Any(corner => corner.Z != corners[0].Z))
            throw new ArgumentException($"A trigger volume needs 3 to {MaximumCorners} floor corners at one height and a height above zero.");
        if (!Convex(corners)) throw new ArgumentException("The corners must go around a convex floor.");
        byte[] output = template.ToArray();
        var file = HavokPackfile.Parse(output);
        var (list, prisms) = Prisms(file);
        int fan = corners.Count - 2;
        if (prisms.Length < fan) throw new InvalidDataException("The trigger shape template holds too few prisms for the floor.");
        float floor = corners[0].Z, top = floor + height;
        // The floor splits into triangles that share the first corner. A spare prism repeats the last one.
        for (int index = 0; index < prisms.Length; index++)
        {
            int at = Math.Min(index, fan - 1);
            WritePrism(file, output, prisms[index], [corners[0], corners[at + 1], corners[at + 2]], floor, top);
        }
        WriteBox(output, file.Absolute(list + ListHalfExtents), file.Absolute(list + ListCentre), corners, floor, top);
        return output;
    }

    /** The template's list shape and its prisms; any other shape of file is refused. */
    private static (int List, int[] Prisms) Prisms(HavokPackfile file)
    {
        var (count, shapes) = file.Array(file.RootOffset);
        if (file.RootClass != "s_hkpShape_array_data" || count != 1 || file.Pointer(shapes) is not { } list
            || file.ClassAt(list) != "hkpListShape")
            throw new InvalidDataException("The trigger shape template is not one list shape.");
        var (children, rows) = file.Array(list + ListChildren);
        if (children < 1) throw new InvalidDataException("The trigger shape template holds no prism.");
        int[] prisms = new int[children];
        for (int index = 0; index < children; index++)
        {
            int prism = file.Pointer(rows + index * ChildStride) ?? -1;
            if (prism < 0 || file.ClassAt(prism) != CollisionShapes.ConvexVertices || file.I32(prism + VertexCount) != PrismVertices
                || file.Array(prism + VertexBlocks).Count != PrismBlocks || file.Array(prism + Planes).Count < PrismPlanes)
                throw new InvalidDataException("A trigger shape template child is not a six-vertex prism.");
            prisms[index] = prism;
        }
        return (list, prisms);
    }

    private static void WritePrism(HavokPackfile file, byte[] output, int prism, Vector3[] triangle, float floor, float top)
    {
        // Stored order: two corners at the top, the same two at the floor, then the third corner twice.
        Vector3 At(Vector3 corner, float z) => new(corner.X, corner.Y, z);
        Vector3[] points =
        [
            At(triangle[0], top), At(triangle[1], top), At(triangle[0], floor), At(triangle[1], floor),
            At(triangle[2], top), At(triangle[2], floor), At(triangle[2], floor), At(triangle[2], floor),
        ];
        int blocks = file.Absolute(file.Array(prism + VertexBlocks).Data);
        for (int index = 0; index < points.Length; index++)
        {
            int block = blocks + BlockSize * (index / 4), lane = sizeof(float) * (index % 4);
            Put(output, block + lane, points[index].X);
            Put(output, block + BlockLane + lane, points[index].Y);
            Put(output, block + 2 * BlockLane + lane, points[index].Z);
        }
        var (count, planes) = file.Array(prism + Planes);
        var rows = new List<Vector4> { new(0, 0, -1, floor), new(0, 0, 1, -top) };
        for (int edge = 0; edge < 3; edge++)
        {
            Vector3 from = triangle[edge], to = triangle[(edge + 1) % 3], other = triangle[(edge + 2) % 3];
            var normal = Vector2.Normalize(new Vector2(to.Y - from.Y, from.X - to.X));
            // The normal points away from the triangle's third corner.
            if (Vector2.Dot(normal, new Vector2(other.X - from.X, other.Y - from.Y)) > 0) normal = -normal;
            rows.Add(new(normal.X, normal.Y, 0, -(normal.X * from.X + normal.Y * from.Y)));
        }
        // The template has more planes than a prism has faces; a repeated face changes nothing.
        for (int index = 0; index < count; index++)
        {
            var row = rows[Math.Min(index, rows.Count - 1)];
            int at = file.Absolute(planes + index * PlaneSize);
            Put(output, at, row.X); Put(output, at + 4, row.Y); Put(output, at + 8, row.Z); Put(output, at + 12, row.W);
        }
        WriteBox(output, file.Absolute(prism + HalfExtents), file.Absolute(prism + Centre), triangle, floor, top);
    }

    /** Writes the half extents and the centre of the box around the corners; each keeps its fourth float. */
    private static void WriteBox(byte[] output, int halfExtents, int centre, IReadOnlyList<Vector3> corners, float floor, float top)
    {
        var min = new Vector3(corners.Min(corner => corner.X), corners.Min(corner => corner.Y), floor);
        var max = new Vector3(corners.Max(corner => corner.X), corners.Max(corner => corner.Y), top);
        Vector3 half = (max - min) / 2, middle = (max + min) / 2;
        Put(output, halfExtents, half.X); Put(output, halfExtents + 4, half.Y); Put(output, halfExtents + 8, half.Z);
        Put(output, centre, middle.X); Put(output, centre + 4, middle.Y); Put(output, centre + 8, middle.Z);
    }

    /** True when the corners turn one way all around, with no three in a line. */
    private static bool Convex(IReadOnlyList<Vector3> corners)
    {
        int sign = 0;
        for (int index = 0; index < corners.Count; index++)
        {
            Vector3 a = corners[index], b = corners[(index + 1) % corners.Count], c = corners[(index + 2) % corners.Count];
            float cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (MathF.Abs(cross) < 1e-6f) return false;
            int turn = MathF.Sign(cross);
            if (sign != 0 && turn != sign) return false;
            sign = turn;
        }
        return true;
    }

    private static void Put(byte[] output, int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(at), value);
}
