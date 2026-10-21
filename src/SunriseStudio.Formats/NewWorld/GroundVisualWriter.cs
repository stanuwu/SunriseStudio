using System.Buffers.Binary;

namespace SunriseStudio.Formats.NewWorld;

/** A quantized static mesh: SNORM positions in the model transform, UV and normal details, 16-bit indices. */
public sealed record EncodedGroundMesh(byte[] Positions, byte[] Details, byte[] Indices, double[] Center, double Scale,
    float Radius, double[][] Bounds, int Triangles);

/** Static ground meshes with the flat-colour technique, their buffers and their identity placement. */
public static class GroundVisualWriter
{
    public const uint TechniqueClass = 0x808071E8, ModelClass = 0x808071A7, OpaqueClass = 0x80807194;

    /** Rounds to float32 as the native stores do. */
    public static double F32(double value) => (float)value;

    private static short Snorm(double value) => (short)Math.Max(-32767, Math.Min(32767, Math.Round(value * 32767)));

    /** Rounds a bound to float32 without shrinking the enclosed interval. */
    public static double Outward(double value, bool lower)
    {
        float rounded = (float)value;
        if (lower && rounded > value || !lower && rounded < value)
        {
            int bits = BitConverter.SingleToInt32Bits(rounded);
            bits += lower ? (rounded > 0 ? -1 : 1) : (rounded >= 0 ? 1 : -1);
            rounded = BitConverter.Int32BitsToSingle(bits);
        }
        return rounded;
    }

    /**
     * One native triangle list with smooth normals. Quantization is checked against the decoded topology, so no
     * triangle collapses (World Studio generated_visual.encode_mesh, default UVs).
     */
    public static EncodedGroundMesh Encode(IReadOnlyList<double[]> vertices, IReadOnlyList<int[]> triangles)
    {
        if (vertices.Count is < 3 or > 65535 || triangles.Count is < 1 or > 200000)
            throw new InvalidDataException("A generated ground needs 3 to 65535 vertices and a bounded triangle list.");
        foreach (var tri in triangles)
            if (tri.Length != 3 || tri.Distinct().Count() != 3 || tri.Any(i => i < 0 || i >= vertices.Count))
                throw new InvalidDataException("A generated ground triangle index is invalid.");
        var lower = Enumerable.Range(0, 3).Select(a => vertices.Min(v => v[a])).ToArray();
        var upper = Enumerable.Range(0, 3).Select(a => vertices.Max(v => v[a])).ToArray();
        var center = Enumerable.Range(0, 3).Select(a => F32((lower[a] + upper[a]) * 0.5)).ToArray();
        double extent = vertices.Max(v => Enumerable.Range(0, 3).Max(a => Math.Abs(v[a] - center[a])));
        if (!(extent > 0)) throw new InvalidDataException("A generated ground has zero extent.");
        double scale = F32(extent);
        if (scale < extent) scale = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits((float)scale) + 1);
        var quantized = vertices.Select(v => Enumerable.Range(0, 3).Select(a => Snorm((v[a] - center[a]) / scale)).ToArray()).ToArray();
        var decoded = quantized.Select(q => Enumerable.Range(0, 3).Select(a => center[a] + scale * q[a] / 32767).ToArray()).ToArray();
        var normals = vertices.Select(_ => new double[3]).ToArray();
        var used = new HashSet<int>();
        foreach (var tri in triangles)
        {
            var original = Face(vertices, tri);
            var normal = Face(decoded, tri);
            if (!(original[0] * normal[0] + original[1] * normal[1] + original[2] * normal[2] > 0))
                throw new InvalidDataException("A generated ground has a degenerate or collapsed triangle.");
            foreach (int index in tri)
            {
                used.Add(index);
                for (int a = 0; a < 3; a++) normals[index][a] += normal[a];
            }
        }
        if (used.Count != vertices.Count) throw new InvalidDataException("A generated ground has unused vertices.");
        var positions = new byte[8 * vertices.Count];
        var details = new byte[12 * vertices.Count];
        for (int i = 0; i < vertices.Count; i++)
        {
            var n = normals[i];
            double length = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
            if (!(length > 0)) throw new InvalidDataException("A generated ground has cancelling vertex normals.");
            var q = quantized[i];
            Put16s(positions.AsSpan(8 * i), q[0], q[1], q[2], 32767);
            Put16s(details.AsSpan(12 * i), q[0], q[1], Snorm(n[0] / length), Snorm(n[1] / length), Snorm(n[2] / length), 0);
        }
        var indices = new byte[6 * triangles.Count];
        for (int t = 0; t < triangles.Count; t++)
            for (int k = 0; k < 3; k++) BinaryPrimitives.WriteUInt16LittleEndian(indices.AsSpan(6 * t + 2 * k), (ushort)triangles[t][k]);
        double radius = decoded.Max(p => Distance(center, p)) + scale / 32767;
        return new(positions, details, indices, center, scale, (float)radius, PositionBounds(positions, center, scale), triangles.Count);
    }

    /** Conservative world bounds of SNORM positions: one quantization step outward, rounded outward to float32. */
    public static double[][] PositionBounds(byte[] positions, double[] center, double scale)
    {
        int count = positions.Length / 8;
        var decoded = new double[count][];
        for (int i = 0; i < count; i++)
        {
            if (BinaryPrimitives.ReadInt16LittleEndian(positions.AsSpan(8 * i + 6)) != 32767)
                throw new InvalidDataException("Unsupported generated position encoding.");
            decoded[i] = Enumerable.Range(0, 3)
                .Select(a => center[a] + scale * BinaryPrimitives.ReadInt16LittleEndian(positions.AsSpan(8 * i + 2 * a)) / 32767).ToArray();
        }
        return
        [
            Enumerable.Range(0, 3).Select(a => Outward(decoded.Min(p => p[a]) - scale / 32767, true)).ToArray(),
            Enumerable.Range(0, 3).Select(a => Outward(decoded.Max(p => p[a]) + scale / 32767, false)).ToArray()
        ];
    }

    /** Euclidean distance; the radius is rounded to float32 after it, so the last double bit cannot reach the bytes in practice. */
    private static double Distance(double[] a, double[] b)
    {
        double x = a[0] - b[0], y = a[1] - b[1], z = a[2] - b[2];
        return Math.Sqrt(x * x + y * y + z * z);
    }

    private static double[] Face(IReadOnlyList<double[]> points, int[] tri)
    {
        var a = points[tri[0]]; var b = points[tri[1]]; var c = points[tri[2]];
        double ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2], vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];
        return [uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx];
    }

    private static void Put16s(Span<byte> target, params short[] values)
    {
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(target[(2 * i)..], values[i]);
    }

    /** A GPU buffer header and its data: stride headers are vertex buffers (subtype 4), others index buffers (6). */
    public static string AddBuffer(GeneratedGraph graph, string key, byte[] data, int stride = 0)
    {
        int subtype = stride != 0 ? 4 : 6;
        var header = new NativeBuffer(stride != 0 ? 12 : 24);
        if (stride != 0) { header.Put32(0, (uint)data.Length); header.Put16(4, (ushort)stride); header.Put32(8, 0xDEADBEEF); }
        else { header.Put8(0, 1); header.Put64(8, (ulong)data.Length); header.Put32(16, 0xDEADBEEF); }
        graph.Add(key, header.ToArray(), 0, 32, subtype);
        graph.Add(key + "_data", data, 0, 40, subtype);
        graph.Buffer(key, key + "_data");
        return key;
    }

    /**
     * The observed static-material technique profile (0x408 bytes). A colour technique adds the pixel stage and
     * its TFX program that stores the colour into cb0 element 0; a depth technique has the vertex stage only.
     */
    public static string AddTechnique(GeneratedGraph graph, string key, string vertexShader, string? pixelShader = null,
        float[]? color = null)
    {
        bool pixel = pixelShader is not null;
        var raw = new NativeBuffer(0x408);
        raw.Put32(8, pixel ? 1u : 2); raw.Put32(12, pixel ? 2u : 0); raw.Put32(24, 0x203);
        raw.Put32(28, pixel ? 0x80000203 : 0x80008203);
        raw.Put32(0x20, pixel ? 0x00810000u : 0); raw.Put32(0x24, pixel ? 0x007F7F40u : 0x00010180); raw.Put32(0x28, uint.MaxValue);
        for (int at = 0x48; at < 0x408; at += 0xA0)
        {
            raw.Put32(at, uint.MaxValue);
            raw.PutI32(at + 0x80, -1); raw.Put32(at + 0x84, uint.MaxValue);
        }
        raw.Put32(0x48, 0);
        raw.Put64(0x60, 0xCBF29CE484222325);
        if (pixel)
        {
            if (color is not { Length: 3 } || color.Any(v => !float.IsFinite(v) || v is < 0 or > 1))
                throw new InvalidDataException("A generated material needs a finite RGB colour.");
            raw.Put32(0x2C8, 0);
            raw.Put64(0x2E0, 0xCBF29CE484222325);
            raw.AppendPadded(0x2E8, 0x80800009, 1, [[0x34], [0x00], [0x43], [0x00]]);
            raw.AppendPadded(0x2F8, 0x80800090, 16, [NativeRow.F32s(color[0], color[1], color[2], 1)]);
            raw.AppendPadded(0x318, 0x80800090, 16, [NativeRow.F32s(color[0], color[1], color[2], 1)]);
            raw.Put32(0x33C, 16);
            raw.PutI32(0x348, 0);
        }
        raw.SetSize();
        graph.Add(key, raw.ToArray(), TechniqueClass);
        graph.Reference(key, 0x48, vertexShader);
        if (pixel) graph.Reference(key, 0x2C8, pixelShader!);
        return key;
    }

    /** Opaque geometry (index, position and detail buffers, input layouts 120 and 122) and its 0x90-byte model. */
    public static string AddMesh(GeneratedGraph graph, string key, EncodedGroundMesh mesh, string depth, string material)
    {
        string index = AddBuffer(graph, key + "_ib", mesh.Indices);
        string position = AddBuffer(graph, key + "_v0", mesh.Positions, 8);
        string detail = AddBuffer(graph, key + "_v1", mesh.Details, 12);
        var opaque = new NativeBuffer(0x40);
        static byte[] Layout(byte stage, byte layout) => [0, 0, stage, 0, layout, 0, 0x02, 0x01];
        opaque.AppendPadded(8, 0x8080719B, 8, [Layout(12, 120), Layout(0, 122)]);
        var part = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(part.AsSpan(4), (uint)(mesh.Triangles * 3));
        part[10] = 2; part[11] = 3;
        opaque.AppendPadded(0x18, 0x8080719A, 12, [part]);
        int buffers = opaque.AppendPadded(0x28, 0x80807199, 16, [new byte[16]])[0];
        opaque.Put64(0x38, 1);
        opaque.SetSize();
        graph.Add(key + "_opaque", opaque.ToArray(), OpaqueClass);
        graph.Reference(key + "_opaque", buffers, index);
        graph.Reference(key + "_opaque", buffers + 4, position);
        graph.Reference(key + "_opaque", buffers + 8, detail);
        var model = new NativeBuffer(0x90);
        var techniques = model.AppendPadded(0x10, 0x80800014, 4, [new byte[4], new byte[4]]);
        float radius = mesh.Radius;
        model.PutF32s(0x38, radius, radius, radius);
        model.PutF32s(0x50, (float)mesh.Center[0], (float)mesh.Center[1], (float)mesh.Center[2], radius);
        model.PutF32s(0x60, (float)mesh.Center[0], (float)mesh.Center[1], (float)mesh.Center[2], (float)mesh.Scale);
        model.PutF32s(0x70, 1, 1, 0, 0);
        model.Put32(0x80, uint.MaxValue);
        model.SetSize();
        graph.Add(key, model.ToArray(), ModelClass);
        graph.Reference(key, 8, key + "_opaque");
        graph.Reference(key, techniques[0], depth);
        graph.Reference(key, techniques[1], material);
        return key;
    }

    /**
     * Identity placement of world-space meshes: bounds rows, one instance row per mesh with its auxiliary words,
     * mesh references, one instance range per mesh and the outer box (World Studio generated_placement).
     */
    public static (string Placement, double[][] Outer) AddPlacement(GeneratedGraph graph, string prefix,
        IReadOnlyList<(string Key, double[][] Bounds)> meshes, uint instanceAuxiliary, uint parentAuxiliary)
    {
        if (meshes.Count is < 1 or > 65535) throw new InvalidDataException("A placement holds 1 to 65535 meshes.");
        var boxes = meshes.Select(mesh => CheckedBounds(mesh.Bounds)).ToArray();
        string parent = prefix + "_placement", instances = prefix + "_instances", bounds = prefix + "_bounds";
        var distinct = meshes.Select(mesh => mesh.Key).Distinct().ToList();
        var boundsRaw = new NativeBuffer(24);
        boundsRaw.AppendPadded(8, 0x80809673, 48, boxes.Select(box =>
        {
            var row = new byte[48];
            NativeRow.F32s((float)box[0][0], (float)box[0][1], (float)box[0][2], 1, (float)box[1][0], (float)box[1][1],
                (float)box[1][2], 1).CopyTo(row, 0);
            return row;
        }).ToArray());
        boundsRaw.SetSize();
        var raw = new NativeBuffer(160);
        raw.Put64(0x30, 112); raw.Put64(0x38, (ulong)meshes.Count);
        var instance = new byte[48];
        NativeRow.F32s(0, 0, 0, 1, 0, 0, 0, 1, 1, 1).CopyTo(instance, 0);
        raw.AppendPadded(0x40, 0x808071A3, 48, meshes.Select(_ => instance).ToArray());
        raw.Put32(0x50, instanceAuxiliary);
        var references = raw.AppendPadded(0x58, 0x8080967D, 4, distinct.Select(_ => new byte[4]).ToArray());
        raw.AppendPadded(0x68, 0x80807190, 8, meshes.Select((mesh, i) =>
            NativeRow.U16s(1, (ushort)i, (ushort)distinct.IndexOf(mesh.Key), 7)).ToArray());
        double[][] outer =
        [
            Enumerable.Range(0, 3).Select(a => boxes.Min(box => box[0][a])).ToArray(),
            Enumerable.Range(0, 3).Select(a => boxes.Max(box => box[1][a])).ToArray()
        ];
        raw.PutF32s(0x80, (float)outer[0][0], (float)outer[0][1], (float)outer[0][2], 1, (float)outer[1][0], (float)outer[1][1],
            (float)outer[1][2], 1);
        raw.SetSize();
        var placement = new NativeBuffer(40);
        placement.Put32(0x18, uint.MaxValue); placement.Put32(0x1C, uint.MaxValue);
        placement.Put32(0x24, parentAuxiliary);
        graph.Add(bounds, boundsRaw.ToArray(), 0x80809671);
        graph.Add(instances, raw.ToArray(), 0x8080966D);
        graph.Add(parent, placement.ToArray(), 0x80806EF4);
        graph.Reference(instances, 0x18, bounds);
        graph.Reference(parent, 8, instances);
        for (int i = 0; i < distinct.Count; i++) graph.Reference(instances, references[i], distinct[i]);
        return (parent, outer);
    }

    /** Finite ordered bounds rounded outward to float32. */
    public static double[][] CheckedBounds(double[][] box)
    {
        if (box.Length != 2 || box.Any(side => side.Length != 3 || side.Any(v => !double.IsFinite(v) || Math.Abs(v) > 1e6))
            || Enumerable.Range(0, 3).Any(a => box[0][a] > box[1][a]))
            throw new InvalidDataException("Invalid generated placement bounds.");
        return [box[0].Select(v => Outward(v, true)).ToArray(), box[1].Select(v => Outward(v, false)).ToArray()];
    }
}
