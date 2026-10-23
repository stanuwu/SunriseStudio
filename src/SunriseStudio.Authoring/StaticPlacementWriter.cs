using System.Numerics;
using SunriseStudio.Packages;

namespace SunriseStudio.Authoring;

/** A copy's table-space transform: unit quaternion, translation and one positive scale. */
internal readonly record struct StaticPose(Vector3 Position, Quaternion Rotation, float Scale)
{
    internal static StaticPose Identity { get; } = new(Vector3.Zero, Quaternion.Identity, 1);

    /** The limits of World Studio's static_pose.pose; a nonuniform scale has no field to go in. */
    internal void Require()
    {
        float[] position = [Position.X, Position.Y, Position.Z], rotation = [Rotation.X, Rotation.Y, Rotation.Z, Rotation.W];
        if (position.Any(v => !float.IsFinite(v) || MathF.Abs(v) > 1e6f) || rotation.Any(v => !float.IsFinite(v) || MathF.Abs(v) > 1.01f))
            throw new InvalidDataException("An added placement needs a finite position within 1,000,000 and a finite rotation.");
        if (Math.Abs(rotation.Sum(v => (double)v * v) - 1) > 2e-5)
            throw new InvalidDataException("An added placement rotation must be a unit quaternion.");
        if (!float.IsFinite(Scale) || Scale < 0.001f || Scale > 1000)
            throw new InvalidDataException("An added placement scale must be between 0.001 and 1000.");
    }
}

/**
 * Compact generated tables for added static copies. Rows copy their installed row, so AO offsets, the two
 * float lanes and range flags stay; the transform, bounds and aggregate box are the copy's own.
 */
internal static class StaticPlacementWriter
{
    /** One added row: the installed row it copies and its table-space transform. */
    internal readonly record struct Copy(int Index, StaticPose Pose);

    internal sealed record Table(byte[] Instances, byte[] Bounds, uint[] Models);

    /**
     * The table holds only the copies, in order; resources are listed in order of first use.
     * @param boundsTag The generated bounds table that +0x18 names.
     */
    internal static Table Compact(byte[] source, IReadOnlyList<Copy> copies, uint boundsTag, Func<uint, byte[]> readModel)
    {
        var data = new NativeRows(source);
        int[] rows = data.Rows(0x40, 0x808071A3, 48), resources = data.Rows(0x58, 0x8080967D, 4),
            ranges = data.Rows(0x68, 0x80807190, 8);
        // The same supported header profile as InstanceIsolationWriter.Compact: +0x30 sizes the inline 0x808071A0 header.
        if (data.I64(0) != source.Length || data.I64(0x30) != 112 || data.I64(0x38) != rows.Length
            || data.I64(8) != 0 || data.I64(0x10) != 0 || data.I64(0x20) != 0 || data.I64(0x28) != 0
            || copies.Count is 0 or > ushort.MaxValue)
            throw new InvalidDataException("The static table has no supported compact profile.");
        var mapped = new Dictionary<int, int>();
        var models = new List<uint>();
        var instances = new List<byte[]>();
        var groups = new List<byte[]>();
        var boxes = new List<byte[]>();
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        for (int compact = 0; compact < copies.Count; compact++)
        {
            var copy = copies[compact];
            if (copy.Index < 0 || copy.Index >= rows.Length) throw new InvalidDataException("The copied instance row is missing.");
            copy.Pose.Require();
            int[] covering = ranges.Where(range => data.U16(range + 2) <= copy.Index
                && copy.Index < data.U16(range + 2) + data.U16(range)).ToArray();
            if (covering.Length != 1) throw new InvalidDataException("The copied instance has no single draw range.");
            int resource = data.U16(covering[0] + 4);
            if (resource >= resources.Length) throw new InvalidDataException("The static range leaves its source arrays.");
            if (!mapped.TryGetValue(resource, out int index))
            {
                mapped.Add(resource, index = mapped.Count);
                models.Add(data.U32(resources[resource]));
            }
            byte[] row = data.Span(rows[copy.Index], 48).ToArray();
            var (position, rotation, scale) = (copy.Pose.Position, copy.Pose.Rotation, copy.Pose.Scale);
            // Quaternion at +0x00, translation at +0x10, uniform scale at +0x1C; +0x20 to +0x2F stay.
            NativeRows.Write(row, 0, rotation.X, rotation.Y, rotation.Z, rotation.W, position.X, position.Y, position.Z, scale);
            instances.Add(row);
            byte[] group = data.Span(covering[0], 8).ToArray();
            U16(group, 0, 1); U16(group, 2, (ushort)compact); U16(group, 4, checked((ushort)index));
            groups.Add(group);
            var (low, high) = PoseBounds.Moved(ModelBox(readModel(models[index])), StaticPose.Identity, copy.Pose);
            byte[] box = new byte[48];
            NativeRows.Write(box, 0, low.X, low.Y, low.Z, 1, high.X, high.Y, high.Z, 1);
            boxes.Add(box);
            min = Vector3.Min(min, low); max = Vector3.Max(max, high);
        }
        byte[] result = data.Span(0, 160).ToArray();
        InstanceIsolationWriter.U32(result, 0x18, boundsTag); InstanceIsolationWriter.I64(result, 0x38, copies.Count);
        result = InstanceIsolationWriter.Array(result, 0x40, 0x808071A3, instances);
        result = InstanceIsolationWriter.Array(result, 0x58, 0x8080967D, models.Select(InstanceIsolationWriter.Tag).ToArray());
        result = InstanceIsolationWriter.Array(result, 0x68, 0x80807190, groups);
        NativeRows.Write(result, 0x80, min.X, min.Y, min.Z, 1, max.X, max.Y, max.Z, 1);
        // A copy has no entry in the baked visibility maps; its bounds row id stays 0.
        byte[] bounds = InstanceIsolationWriter.Array(new byte[24], 8, 0x80809673, boxes);
        return new(result, bounds, models.ToArray());
    }

    /** The source owner selecting current per-instance bounds (+0x20 = 0) for the generated table at +8. */
    internal static byte[] Owner(byte[] source, uint table)
    {
        if (source.Length != 40) throw new InvalidDataException("The static owner size is unsupported.");
        if (source[0x20] > 1) throw new InvalidDataException("The static owner has an unsupported visibility mode.");
        byte[] result = source.ToArray(); InstanceIsolationWriter.U32(result, 8, table);
        InstanceIsolationWriter.I64(result, 0x18, -1); result[0x20] = 0; return result;
    }

    /**
     * Every decoded position is center + scalar * a signed normalized value, so this cube holds the model.
     * Center at model +0x60, scalar at +0x6C, as the scene decoder reads them.
     */
    internal static (double[] Min, double[] Max) ModelBox(byte[] model)
    {
        if (model.Length < 0x90) throw new InvalidDataException("The static model is truncated.");
        var data = new NativeRows(model);
        Vector3 center = data.V3(0x60);
        float scale = data.F32(0x6C);
        if (!float.IsFinite(center.X) || !float.IsFinite(center.Y) || !float.IsFinite(center.Z)
            || MathF.Abs(center.X) > 1e6f || MathF.Abs(center.Y) > 1e6f || MathF.Abs(center.Z) > 1e6f
            || !float.IsFinite(scale) || !(scale > 0) || scale > 1e6f)
            throw new InvalidDataException("The static model has an invalid position range.");
        return ([(double)center.X - scale, (double)center.Y - scale, (double)center.Z - scale],
            [(double)center.X + scale, (double)center.Y + scale, (double)center.Z + scale]);
    }

    private static void U16(byte[] bytes, int at, ushort value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);
}

/**
 * A conservative table-space box under a change of pose, in double precision as World Studio's
 * static_pose.bounds computes it: the eight corners re-expressed, padded by a float32 arithmetic margin,
 * then rounded outward to float32.
 */
internal static class PoseBounds
{
    internal static (Vector3 Min, Vector3 Max) Moved((double[] Min, double[] Max) box, StaticPose before, StaticPose after)
    {
        before.Require(); after.Require();
        for (int axis = 0; axis < 3; axis++)
            if (!double.IsFinite(box.Min[axis]) || !double.IsFinite(box.Max[axis]) || box.Min[axis] > box.Max[axis])
                throw new InvalidDataException("The source instance bounds are invalid.");
        double[,] inverse = Inverse(Matrix(before)), next = Matrix(after), relative = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                double sum = 0;
                for (int k = 0; k < 3; k++) sum += next[i, k] * inverse[k, j];
                relative[i, j] = sum;
            }
        double[] from = Position(before), to = Position(after);
        var points = new double[8][];
        // Corner order of itertools.product((0, 1), repeat=3): the last axis changes fastest.
        for (int corner = 0; corner < 8; corner++)
        {
            int[] side = [corner >> 2 & 1, corner >> 1 & 1, corner & 1];
            double[] p = new double[3], moved = new double[3];
            for (int axis = 0; axis < 3; axis++) p[axis] = (side[axis] == 0 ? box.Min : box.Max)[axis] - from[axis];
            for (int axis = 0; axis < 3; axis++)
            {
                double sum = 0;
                for (int j = 0; j < 3; j++) sum += relative[axis, j] * p[j];
                moved[axis] = to[axis] + sum;
            }
            points[corner] = moved;
        }
        double largest = 1;
        foreach (double v in points.SelectMany(p => p).Concat(box.Min).Concat(box.Max)) largest = Math.Max(largest, Math.Abs(v));
        double margin = 2e-5 * largest;
        float[] low = new float[3], high = new float[3];
        for (int axis = 0; axis < 3; axis++)
        {
            low[axis] = Outward(points.Min(p => p[axis]) - margin, true);
            high[axis] = Outward(points.Max(p => p[axis]) + margin, false);
        }
        if (low.Concat(high).Any(v => !float.IsFinite(v) || MathF.Abs(v) > 1e6f))
            throw new InvalidDataException("The added instance bounds exceed the supported range.");
        return (new(low[0], low[1], low[2]), new(high[0], high[1], high[2]));
    }

    /** Rounds to float32 without shrinking the interval, one unit in the last place outward when needed. */
    internal static float Outward(double value, bool lower)
    {
        float rounded = (float)value;
        if (lower && rounded > value || !lower && rounded < value)
        {
            uint bits = BitConverter.SingleToUInt32Bits(rounded);
            bits = lower ? (rounded > 0 ? bits - 1 : bits + 1) : (rounded >= 0 ? bits + 1 : bits - 1);
            rounded = BitConverter.UInt32BitsToSingle(bits);
        }
        return rounded;
    }

    private static double[] Position(StaticPose pose) => [pose.Position.X, pose.Position.Y, pose.Position.Z];

    /** Rotation times scale, column j scaled by the scale of axis j, from the stored quaternion as it is. */
    private static double[,] Matrix(StaticPose pose)
    {
        double x = pose.Rotation.X, y = pose.Rotation.Y, z = pose.Rotation.Z, w = pose.Rotation.W, s = pose.Scale;
        return new double[,]
        {
            { (1 - 2 * (y * y + z * z)) * s, 2 * (x * y - z * w) * s, 2 * (x * z + y * w) * s },
            { 2 * (x * y + z * w) * s, (1 - 2 * (x * x + z * z)) * s, 2 * (y * z - x * w) * s },
            { 2 * (x * z - y * w) * s, 2 * (y * z + x * w) * s, (1 - 2 * (x * x + y * y)) * s }
        };
    }

    private static double[,] Inverse(double[,] m)
    {
        double a = m[0, 0], b = m[0, 1], c = m[0, 2], d = m[1, 0], e = m[1, 1], f = m[1, 2], g = m[2, 0], h = m[2, 1], i = m[2, 2];
        var adjugate = new double[,]
        {
            { e * i - f * h, c * h - b * i, b * f - c * e },
            { f * g - d * i, a * i - c * g, c * d - a * f },
            { d * h - e * g, b * g - a * h, a * e - b * d }
        };
        double determinant = a * adjugate[0, 0] + b * adjugate[1, 0] + c * adjugate[2, 0];
        if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-12)
            throw new InvalidDataException("The source pose cannot be inverted.");
        var result = new double[3, 3];
        for (int r = 0; r < 3; r++)
            for (int col = 0; col < 3; col++) result[r, col] = adjugate[r, col] / determinant;
        return result;
    }
}
