using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

/** A nav holder names a nav mesh of three layers and a firing point tag of three layers. Positions are Z up. */
public static class NavigationDecoder
{
    /** Layer fields the generated views do not name; offsets are from the layer start. */
    internal const int MeshLayerIndex = 16, MeshTreeBuild = 112, MeshMinimum = 160, MeshMaximum = 176;
    internal const int GraphLayerIndex = 40, GraphTreeBuild = 96;
    /** A box tree is byte code; a large layer holds more bytes than the checked row cap. */
    private const int MaximumTreeBytes = 64 * 1024 * 1024;

    public static NavigationMesh Decode(byte[] holder, byte[] mesh, byte[] graph)
    {
        ref readonly var h = ref Sized<C80808cb6>(holder);
        var layers = DecodeMesh(mesh);
        var graphs = DecodeGraph(graph);
        foreach (var layer in graphs)
            foreach (var point in layer.Points)
                if ((point.FaceKind & 0xE000) == 0x2000 && (point.FaceKind & 0x3FF) == 0)
                {
                    int index = (int)(point.FaceKind >> 10 & 7);
                    Require(index < layers.Length && point.Face < layers[index].Faces.Length, "Navigation point leaves its face array.");
                }
        return new(h.Unk8, h.UnkC, new(h.Unk10, h.Unk14, h.Unk18), new(h.Unk20, h.Unk24, h.Unk28), layers, graphs);
    }

    /** @return The byte offset of layer `index` in a nav mesh tag. */
    internal static int MeshLayer(int index) => TagView.FieldOffset<C80808cb8>($"{nameof(C80808cb8.H00dfc4ce)}.{nameof(C80808c88.Unk0)}[{index}]");

    /** @return The byte offset of layer `index` in a firing point tag. */
    internal static int GraphLayer(int index) => TagView.FieldOffset<C80808cba>($"{nameof(C80808cba.H672f3106)}.{nameof(C80808c7a.Unk0)}[{index}]");

    public static NavigationMeshLayer[] DecodeMesh(byte[] bytes)
    {
        Sized<C80808cb8>(bytes);
        var result = new NavigationMeshLayer[3];
        for (int i = 0; i < result.Length; i++)
        {
            int s = MeshLayer(i);
            ref readonly var layer = ref TagView.At<G80808c880>(bytes, s);
            var third = Array(bytes, s + TagView.FieldOffset<G80808c880>(nameof(G80808c880.Unk20)) - TagArray.RelativeOffset, 0, 24);
            var faces = Array(bytes, TagView.OffsetOf(bytes, in layer.Unk28), C80808ca2.Class, Unsafe.SizeOf<C80808ca2>());
            var vertices = Array(bytes, TagView.OffsetOf(bytes, in layer.Unk38), C80809a5f.Class, Unsafe.SizeOf<C80809a5f>());
            var lists = Array(bytes, TagView.OffsetOf(bytes, in layer.Unk48), C80808c94.Class, Unsafe.SizeOf<C80808c94>(),
                TagView.FieldOffset<C80808c94>(nameof(C80808c94.LinkIndices)), SI32Element.Class, Unsafe.SizeOf<SI32Element>());
            var links = Array(bytes, TagView.OffsetOf(bytes, in layer.Unk58), C80808c8c.Class, Unsafe.SizeOf<C80808c8c>());
            var tree = Array(bytes, TagView.OffsetOf(bytes, in layer.Unk90), ExpressionCode.Class, 1, maximumCount: MaximumTreeBytes);
            Require(TagRead.U32(bytes, s + MeshLayerIndex) == i || faces.Count == 0, "Navigation mesh layer index is unknown.");
            var min = TagRead.Vec3(bytes, s + MeshMinimum); var max = TagRead.Vec3(bytes, s + MeshMaximum);
            float cs = layer.UnkC0, ch = layer.UnkC4;
            Require(Finite(min) && Finite(max), "Navigation bounds are not finite.");
            Require(faces.Count == 0 || cs > 0 && ch > 0 && float.IsFinite(cs) && float.IsFinite(ch), "Navigation lattice is invalid.");
            // A vertex is three u16 lattice steps from the layer minimum.
            var v = vertices.Data;
            var points = Enumerable.Range(0, vertices.Count).Select(k => min + new Vector3(TagRead.U16(v, k * 6) * cs,
                TagRead.U16(v, k * 6 + 2) * cs, TagRead.U16(v, k * 6 + 4) * ch)).ToArray();
            var f = faces.Data;
            var polygons = new NavigationFace[faces.Count];
            for (int k = 0; k < faces.Count; k++)
            {
                // Six vertex words, six neighbour words, a u8 count, a pad byte, the list slot and two island words.
                int p = k * faces.Stride, count = f[p + 48];
                Require(count is >= 3 and <= 6 && f[p + 49] == 0, "Navigation face layout is unknown.");
                uint[] indices = Enumerable.Range(0, count).Select(j => TagRead.U32(f, p + j * 4)).ToArray();
                Require(indices.All(index => index < points.Length), "Navigation face leaves its vertex array.");
                short slot = TagRead.I16(f, p + 50);
                Require(slot >= -1 && slot < lists.Count, "Navigation face leaves its link lists.");
                polygons[k] = new(indices, Enumerable.Range(0, count).Select(j => TagRead.U32(f, p + 24 + j * 4)).ToArray(),
                    slot, TagRead.U32(f, p + 52), TagRead.U32(f, p + 56));
            }
            var l = links.Data;
            var decodedLinks = Enumerable.Range(0, links.Count).Select(k =>
            {
                int p = k * links.Stride;
                ref readonly var link = ref TagView.At<C80808c8c>(l, p);
                Require(l[p + 56] is 1 or 3, "Navigation link type is unknown.");
                return new NavigationLink(new(link.Unk0, link.Unk4, link.Unk8), new(link.UnkC, link.Unk10, link.Unk14),
                    new(link.Unk18, link.Unk1c, link.Unk20), new(link.Unk24, link.Unk28, link.Unk2c), l[p + 49],
                    l[p + 56], TagRead.U32(l, p + 60), TagRead.U32(l, p + 64), TagRead.U32(l, p + 68), TagRead.U32(l, p + 72));
            }).ToArray();
            foreach (var list in lists.Children)
                for (int k = 0; k < list.Count; k++)
                    Require(TagView.At<SI32Element>(list.Data, k * list.Stride).Value is var link && link >= 0 && link < links.Count,
                        "Navigation list leaves its links.");
            result[i] = new(i, min, max, cs, ch, points, polygons, decodedLinks, [third, faces, vertices, lists, links, tree]);
        }
        return result;
    }

    public static NavigationGraphLayer[] DecodeGraph(byte[] bytes)
    {
        Sized<C80808cba>(bytes);
        var result = new NavigationGraphLayer[3];
        for (int i = 0; i < result.Length; i++)
        {
            int s = GraphLayer(i);
            ref readonly var layer = ref TagView.At<G80808c7a0>(bytes, s);
            var clusters = Array(bytes, TagView.OffsetOf(bytes, in layer.Unk30), C80809a4a.Class, Unsafe.SizeOf<C80809a4a>(),
                TagView.FieldOffset<C80809a4a>(nameof(C80809a4a.H6139c90c)), C80809a4f.Class, Unsafe.SizeOf<C80809a4f>());
            var covers = Array(bytes, TagView.OffsetOf(bytes, in layer.Unk40), 0, Unsafe.SizeOf<C80809a4e>());
            var points = Array(bytes, TagView.OffsetOf(bytes, in layer.Unk50), C80809a51.Class, Unsafe.SizeOf<C80809a51>());
            var tree = Array(bytes, TagView.OffsetOf(bytes, in layer.Unk80), ExpressionCode.Class, 1, maximumCount: MaximumTreeBytes);
            var extensions = Array(bytes, TagView.OffsetOf(bytes, in layer.Unk90), C80808c83.Class, Unsafe.SizeOf<C80808c83>());
            var paths = Array(bytes, TagView.OffsetOf(bytes, in layer.UnkA0), 0, 16, 0, 0, 28);
            var references = Array(bytes, TagView.OffsetOf(bytes, in layer.UnkB0), 0, 24);
            Require(TagRead.U32(bytes, s + GraphLayerIndex) == i || points.Count == 0, "Navigation graph layer index is unknown.");
            float[] agent = Agent(bytes, i);
            bool empty = new[] { clusters, covers, points, tree, extensions, paths, references }.All(a => a.Count == 0);
            Require(agent.All(float.IsFinite) && (agent[0] > 0 && agent[1] > 0 || empty && agent.All(v => v == 0)), "Navigation agent is invalid.");
            var p = points.Data;
            var decodedPoints = Enumerable.Range(0, points.Count).Select(k =>
            {
                // A point row: a normal, a face key and face, then the position, a metric, a cover index and flags.
                int o = k * points.Stride;
                ref readonly var row = ref TagView.At<C80809a51>(p, o);
                return new NavigationPoint(TagRead.Vec3(p, o), TagRead.U32(p, o + 16), TagRead.U32(p, o + 20),
                    new(row.Unk18, row.Unk1c, row.Unk20), row.Unk24, TagRead.I32(p, o + 40), TagRead.U32(p, o + 44));
            }).ToArray();
            Require(decodedPoints.All(point => Finite(point.Position) && Finite(point.Normal) && float.IsFinite(point.Metric)),
                "Navigation point is not finite.");
            var c = clusters.Data;
            var decodedClusters = Enumerable.Range(0, clusters.Count).Select(k =>
            {
                ref readonly var row = ref TagView.At<C80809a4a>(c, k * clusters.Stride);
                // The representative member index follows the generated floats.
                return new NavigationCluster(new(row.Unk10, row.Unk14, row.Unk18), row.Unk1c, row.Unk20, row.Unk24,
                    TagRead.I32(c, k * clusters.Stride + 48), clusters.Children[k]);
            }).ToArray();
            foreach (var cluster in decodedClusters)
            {
                Require(cluster.Representative >= 0 && cluster.Representative < cluster.Members.Count, "Navigation cluster representative is invalid.");
                var m = cluster.Members.Data;
                for (int k = 0; k < cluster.Members.Count; k++)
                    if (TagRead.U32(m, k * 8) == 0) Require(TagRead.U32(m, k * 8 + 4) < points.Count, "Navigation cluster leaves its points.");
            }
            result[i] = new(i, agent, decodedPoints, decodedClusters, [clusters, covers, points, tree, extensions, paths, references]);
        }
        return result;
    }

    /** @return The six agent floats of firing point layer `index`. */
    public static float[] Agent(byte[] graph, int index)
    {
        ref readonly var layer = ref TagView.Checked<G80808c7a0>(graph, GraphLayer(index));
        return [layer.Unk10, layer.Unk14, layer.Unk18, layer.Unk1c, layer.Unk20, layer.Unk24];
    }

    /** The tag must hold its own length in its first word and fit its generated view. */
    private static ref readonly T Sized<T>(byte[] bytes) where T : unmanaged
    {
        Require(TagView.Fits<T>(bytes, 0) && TagView.HasSizeWord(bytes), "Navigation resource is truncated.");
        return ref TagView.At<T>(bytes, 0);
    }

    /** Reads one array field; a nested array field in each row reads with its own class and stride. */
    private static NavigationArray Array(byte[] bytes, int field, uint type, int stride, int nestedField = 0, uint nestedType = 0,
        int nestedStride = 0, int maximumCount = TagArray.MaximumCheckedCount)
    {
        try
        {
            var (count, data, cls) = TagArray.Checked(bytes, field, type, stride, maximumCount);
            if (count == 0) return new(0, stride, [], []);
            var children = nestedStride == 0 ? [] : Enumerable.Range(0, count)
                .Select(i => Array(bytes, data + i * stride + nestedField, nestedType, nestedStride)).ToArray();
            return new(cls, stride, bytes.AsSpan(data, checked(count * stride)).ToArray(), children);
        }
        catch (Exception error) when (error is ArgumentException or OverflowException or IndexOutOfRangeException)
        { throw new InvalidDataException("Navigation array leaves its resource.", error); }
    }

    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
