using System.Buffers.Binary;
using SunriseStudio.Formats;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Authoring;

/**
 * Collision of a ground in the retail STerrain form, as generated_terrain_collision.py writes it
 * (census of all 1,515 installed 0x80808A15 wrappers): one compressed-mesh payload and one simple
 * carrier (class 0x808092DB) per terrain group, a static compound of `tK` instances with userData K,
 * and a wrapper whose +0x18 lists the carriers.
 */
public static class GroundCollision
{
    public const uint WrapperClass = 0x80808A15, CarrierClass = 0x808092DB, CarrierArrayClass = 0x808092D9;
    public const uint SurfaceHandleClass = 0x80808BC5, PaletteClass = 0x80800006, ShapesClass = 0x80800014;
    public const uint CollisionResourceClass = 0x80808A13;
    // Installed surface most static-collision wrappers list (1,254 of 1,515), referenced in place.
    public const uint RetailSurface = 0x80C70D6A;
    // Installed archetype every static-collision placement row names, referenced in place.
    public const uint CollisionArchetype = 0x80C70CE9;
    // Instance +0x38: every installed tK terrain instance stores filter info 0x037FFCC7 with mask 0 (22,470 of 22,470).
    public const uint TerrainFilterInfo = 0x037FFCC7;
    private const uint CompoundSignature = 0xE198603F;
    private const int InstanceSize = 0x50, RootSize = 0xA0;

    /** One terrain group's collision triangles, in patch then group order. */
    public sealed record TilePayload(int CellX, int CellY, (int X, int Y) Tile,
        (double X, double Y, double Z)[] Vertices, (int A, int B, int C)[] Triangles);

    /** Each group's quads as the render's two triangles each, vertices in first use. */
    public static IReadOnlyList<TilePayload> Payloads(GroundGrid grid, (double X, double Y, double Z)[] points, IReadOnlyList<GroundPatch> patches)
    {
        var output = new List<TilePayload>();
        foreach (var patch in patches)
            foreach (var tile in GroundTerrain.TileOrder(patch))
            {
                var local = new Dictionary<int, int>();
                var vertices = new List<(double, double, double)>();
                var triangles = new List<(int, int, int)>();
                foreach (var (x, y) in patch.Tiles[tile].OrderBy(q => q.Y).ThenBy(q => q.X))
                {
                    var (first, second) = GroundTerrain.QuadTriangles(grid.Columns, x, y);
                    foreach (var tri in new[] { first, second })
                    {
                        int Local(int v)
                        {
                            if (local.TryGetValue(v, out int at)) return at;
                            local[v] = vertices.Count; vertices.Add(points[v]); return vertices.Count - 1;
                        }
                        int a = Local(tri.Item1), b = Local(tri.Item2), c = Local(tri.Item3);
                        triangles.Add((a, b, c));
                    }
                }
                output.Add(new(patch.CellX, patch.CellY, tile, vertices.ToArray(), triangles.ToArray()));
            }
        return output;
    }

    /** The payload's __data__ local fixup table offset, which carrier +0x0C stores (22,470 of 22,470). */
    public static uint LocalFixups(byte[] payload) => checked((uint)HavokPackfile.Parse(payload).DataLength);

    /**
     * A simple one-surface carrier, 132 bytes: +0x0C the payload's local fixup offset, +0x10 1, +0x14 the
     * payload tag, +0x18 0 (retail ids name packages the client does not install), +0x20 the surface handle.
     * @param surface Offset of the surface handle slot.
     */
    public static byte[] Carrier(byte[] payload, out int surface)
    {
        var raw = new byte[96];
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x0C), LocalFixups(payload));
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x10), 1);
        raw = NativeArray.Append(raw, 0x20, SurfaceHandleClass, [new byte[4]], out surface);
        return raw;
    }

    /**
     * Class 0x80808A15 with one surface and only carriers: +0x08 surfaces, +0x18 carriers, empty
     * palettes and static shapes, +0x58 the compound, +0x5C 0 (verified live on 2026-09-24).
     */
    public static (byte[] Bytes, int Surface, int[] Carriers) Wrapper(int carriers)
    {
        if (carriers is < 1 or > 4096) throw new InvalidDataException("A ground wrapper holds 1 to 4096 carriers.");
        var raw = NativeArray.Append(new byte[96], 8, SurfaceHandleClass, [new byte[4]], out int surface);
        raw = NativeArray.Append(raw, 0x18, CarrierArrayClass, Enumerable.Range(0, carriers).Select(_ => new byte[4]).ToArray(), out int first);
        return (raw, surface, Enumerable.Range(0, carriers).Select(i => first + 4 * i).ToArray());
    }

    /**
     * A static compound over the payloads, as generated_compound.compile_shapes then retail_imports
     * write it: identity instances importing `tK`, filter 0x037FFCC7, mask 0, userData K, and a 6-byte tree.
     */
    public static byte[] Compound(IReadOnlyList<CompressedMesh> payloads)
    {
        if (payloads.Count is < 1 or > 4096) throw new InvalidDataException("A compound holds 1 to 4096 shapes.");
        int bits = payloads.Max(mesh => mesh.BitsPerKey);
        if (bits is < 1 or > 31 || payloads.Count > (uint.MaxValue >> bits)) throw new InvalidDataException("The compound exhausts the shape-key space.");
        var boxes = payloads.Select(mesh => (Low: new[] { mesh.DomainMin.X, mesh.DomainMin.Y, mesh.DomainMin.Z },
            High: new[] { mesh.DomainMax.X, mesh.DomainMax.Y, mesh.DomainMax.Z })).ToArray();
        var bounds = (Low: boxes[0].Low.ToArray(), High: boxes[0].High.ToArray());
        foreach (var box in boxes.Skip(1)) bounds = Union(bounds, box);
        var nodes = TreeBytes(boxes, bounds);
        int count = payloads.Count;
        var root = new byte[RootSize];
        // +0x10 serialized shape word: dispatch type 4. +0x18 0xFFFFFFFF: A32670 descends only past an invalid material selector.
        BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(0x10), 0x400);
        BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(0x18), uint.MaxValue);
        root[0x20] = 2;
        root[0x30] = (byte)bits;
        BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(0x54), 0x80000000);
        for (int i = 0; i < 3; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(root.AsSpan(0x80 + 4 * i), bounds.Low[i]);
            BinaryPrimitives.WriteSingleLittleEndian(root.AsSpan(0x90 + 4 * i), bounds.High[i]);
        }
        var instances = new byte[count * InstanceSize];
        for (int i = 0; i < count; i++)
        {
            var at = instances.AsSpan(i * InstanceSize);
            // Identity pose; the high bits of translation.W and scale.W are the constructor's stored metadata.
            BinaryPrimitives.WriteUInt32LittleEndian(at[12..], 0x3F000000);
            BinaryPrimitives.WriteSingleLittleEndian(at[0x1C..], 1);
            BinaryPrimitives.WriteSingleLittleEndian(at[0x20..], 1);
            BinaryPrimitives.WriteSingleLittleEndian(at[0x24..], 1);
            BinaryPrimitives.WriteSingleLittleEndian(at[0x28..], 1);
            BinaryPrimitives.WriteSingleLittleEndian(at[0x2C..], .5f);
            BinaryPrimitives.WriteUInt32LittleEndian(at[0x38..], TerrainFilterInfo);
            BinaryPrimitives.WriteUInt32LittleEndian(at[0x40..], (uint)i);
        }
        var builder = new HavokPackfileBuilder();
        int rootAt = builder.PlaceObject(root, StaticCompoundShape.ClassName);
        builder.WriteArrayHeader(rootAt + 0x38, count);
        builder.Local(rootAt + 0x38, builder.Place(instances));
        builder.WriteArrayHeader(rootAt + 0x70, nodes.Length / CompoundTree.NodeSize);
        // The node block keeps the zero padding compile_shapes gives it.
        builder.Local(rootAt + 0x70, builder.Place([.. nodes, .. new byte[(16 - nodes.Length % 16) % 16]]));
        for (int i = 0; i < count; i++) builder.Import(RootSize + InstanceSize * i + 0x30, $"t{i}");
        return builder.Build([.. CollisionPackfileWriter.ReflectionClasses, (CompoundSignature, StaticCompoundShape.ClassName)], rootAt, 0);
    }

    /**
     * The installed one-row collision table layout (366 of 366): row at 0x30 naming the archetype,
     * its 24-byte 0x80808A13 resource at 200 with the wrapper tag at +0x10 (table offset 216).
     */
    public static byte[] CollisionTable()
    {
        var raw = new byte[224];
        BinaryPrimitives.WriteUInt64LittleEndian(raw, (ulong)raw.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(8), 1);
        BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(28), NativeArray.Marker);
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(32), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(40), GroundTerrain.RowClass);
        int row = 48;
        for (int i = 0; i < 8; i++) BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(row + 16 + 4 * i), i is 3 or 7 ? 1 : 0);
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(row + 0x30), ulong.MaxValue);
        for (int k = 0; k < 4; k++) BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(row + 0x40 + 4 * k), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(row + 0x54), uint.MaxValue);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(row + 0x5C), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(row + 0x60), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(row + 0x64), 0x811C9DC5);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(row + 0x68), 0x304);
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(row + 0x70), ulong.MaxValue);
        BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(row + 0x78), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(196), CollisionResourceClass);
        return raw;
    }

    /** Offset of the wrapper tag in CollisionTable. */
    public const int CollisionTableWrapper = 216;

    /** The six-byte compound tree of compound_append.tree_bytes: longest-axis median splits over child boxes. */
    private static byte[] TreeBytes((float[] Low, float[] High)[] bounds, (float[] Low, float[] High) root)
    {
        var nodes = new List<byte[]?>();
        void Emit(int[] ids, (float[] Low, float[] High) parent, bool isRoot)
        {
            var wanted = (Low: bounds[ids[0]].Low.ToArray(), High: bounds[ids[0]].High.ToArray());
            foreach (int index in ids.Skip(1)) wanted = Union(wanted, bounds[index]);
            var (code, decoded) = isRoot ? (new byte[3], root) : Quantize(parent, wanted);
            int at = nodes.Count;
            nodes.Add(null);
            int payload;
            if (ids.Length == 1) payload = ids[0];
            else
            {
                int axis = 0;
                for (int a = 1; a < 3; a++)
                    if ((double)wanted.High[a] - wanted.Low[a] > (double)wanted.High[axis] - wanted.Low[axis]) axis = a;
                ids = ids.OrderBy(i => (double)bounds[i].Low[axis] + bounds[i].High[axis]).ToArray();
                int half = ids.Length / 2;
                Emit(ids[..half], decoded, false);
                int right = nodes.Count;
                if ((right - at) % 2 != 0) throw new InvalidDataException("Invalid full binary compound subtree.");
                Emit(ids[half..], decoded, false);
                payload = (right - at) / 2 | 0x800000;
            }
            nodes[at] = [code[0], code[1], code[2], (byte)((payload >> 16) & 255), (byte)(payload & 255), (byte)((payload >> 8) & 255)];
        }
        Emit(Enumerable.Range(0, bounds.Length).ToArray(), root, true);
        return nodes.SelectMany(node => node!).ToArray();
    }

    private static (byte[] Code, (float[] Low, float[] High) Decoded) Quantize((float[] Low, float[] High) parent, (float[] Low, float[] High) wanted)
    {
        var code = new byte[3];
        for (int axis = 0; axis < 3; axis++)
        {
            int lowest = -1, highest = -1;
            for (int q = 0; q < 16; q++)
            {
                var candidate = new byte[3];
                candidate[axis] = (byte)(q << 4);
                if (DecodeBounds(parent, candidate).Low[axis] <= wanted.Low[axis]) lowest = q;
                candidate[axis] = (byte)q;
                if (DecodeBounds(parent, candidate).High[axis] >= wanted.High[axis]) highest = q;
            }
            if (lowest < 0 || highest < 0) throw new InvalidDataException("A compound tree child exceeds its parent.");
            code[axis] = (byte)(lowest * 16 + highest);
        }
        var decoded = DecodeBounds(parent, code);
        for (int i = 0; i < 3; i++)
            if (!(decoded.Low[i] <= wanted.Low[i] && wanted.High[i] <= decoded.High[i]))
                throw new InvalidDataException("Compound tree quantization lost a bound.");
        return (code, decoded);
    }

    /** Compound node bounds: squared nibbles in steps of 1/226 of the parent box, float32 at each step. */
    private static (float[] Low, float[] High) DecodeBounds((float[] Low, float[] High) parent, byte[] code)
    {
        var low = new float[3]; var high = new float[3];
        float scale = (float)(1.0 / 226);
        for (int axis = 0; axis < 3; axis++)
        {
            float step = (float)((double)(float)((double)parent.High[axis] - parent.Low[axis]) * scale);
            int lo = code[axis] >> 4, hi = code[axis] & 15;
            low[axis] = (float)(parent.Low[axis] + (double)(float)(lo * lo * (double)step));
            high[axis] = (float)(parent.High[axis] - (double)(float)(hi * hi * (double)step));
        }
        return (low, high);
    }

    private static (float[] Low, float[] High) Union((float[] Low, float[] High) a, (float[] Low, float[] High) b) =>
        (Enumerable.Range(0, 3).Select(i => Math.Min(a.Low[i], b.Low[i])).ToArray(),
            Enumerable.Range(0, 3).Select(i => Math.Max(a.High[i], b.High[i])).ToArray());
}
