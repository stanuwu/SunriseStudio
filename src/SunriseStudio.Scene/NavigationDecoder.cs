using System.Numerics;

namespace SunriseStudio.Scene;

/** CB6 owns three fixed CB8 mesh layers and three CBA agent/point layers. All positions are Z up. */
public static class NavigationDecoder
{
    public static NavigationMesh Decode(byte[] root, byte[] mesh, byte[] graph)
    {
        var r = Sized(root, 48, true);
        var layers = DecodeMesh(mesh);
        var graphs = DecodeGraph(graph);
        var minimum = new Vector3(float.MaxValue); var maximum = -minimum;
        foreach (var layer in layers.Where(layer => layer.Faces.Length > 0))
        { minimum = Vector3.Min(minimum, layer.Minimum); maximum = Vector3.Max(maximum, layer.Maximum); }
        Require(r.V3(0x10) == minimum && r.V3(0x20) == maximum, "Navigation root bounds differ from its layers.");
        Require(r.F32(0x1C) == 1 && r.F32(0x2C) == 1, "Navigation root bounds have an unknown layout.");
        for (int i = 0; i < 3; i++)
        {
            foreach (var point in graphs[i].Points)
                if ((point.FaceKind & 0xE000) == 0x2000 && (point.FaceKind & 0x3FF) == 0)
                {
                    int layer = (int)(point.FaceKind >> 10 & 7);
                    Require(layer < 3 && point.Face < layers[layer].Faces.Length, "Navigation point leaves its face array.");
                }
        }
        return new(r.U32(8), r.U32(12), minimum, maximum, layers, graphs);
    }

    public static NavigationMeshLayer[] DecodeMesh(byte[] bytes)
    {
        var d = Sized(bytes, 0x280);
        var result = new NavigationMeshLayer[3];
        for (int i = 0; i < 3; i++)
        {
            // The layer begins at 0x10; its index is another 0x10 bytes into it.
            int s = 0x20 + i * 0xD0;
            var kind2 = Array(d, s + 8, null, 24);
            var faces = Array(d, s + 0x18, 0x80808CA2, 60); // face
            var vertices = Array(d, s + 0x28, 0x80809A5F, 6); // u16 xyz
            var lists = Array(d, s + 0x38, 0x80808C94, 16, 4, 0x80800007);
            var links = Array(d, s + 0x48, 0x80808C8C, 76);
            var tree = Array(d, s + 0x80, 0x80800009, 1);
            Require(d.U32(s) == i || faces.Count == 0, "Navigation mesh layer index is unknown.");
            var min = d.V3(s + 0x90); var max = d.V3(s + 0xA0);
            float cs = d.F32(s + 0xB0), ch = d.F32(s + 0xB4);
            Require(Finite(min) && Finite(max), "Navigation bounds are not finite.");
            Require(faces.Count == 0 || cs > 0 && ch > 0 && float.IsFinite(cs) && float.IsFinite(ch), "Navigation lattice is invalid.");
            var v = new TagData(vertices.Data);
            var points = Enumerable.Range(0, vertices.Count).Select(k => min + new Vector3(v.U16(k * 6) * cs,
                v.U16(k * 6 + 2) * cs, v.U16(k * 6 + 4) * ch)).ToArray();
            var f = new TagData(faces.Data);
            var polygons = new NavigationFace[faces.Count];
            for (int k = 0; k < faces.Count; k++)
            {
                int p = k * 60, count = f.U8(p + 48);
                Require(count is >= 3 and <= 6 && f.U8(p + 49) == 0, "Navigation face layout is unknown.");
                uint[] indices = Enumerable.Range(0, count).Select(j => f.U32(p + j * 4)).ToArray();
                Require(indices.All(index => index < points.Length), "Navigation face leaves its vertex array.");
                short slot = f.I16(p + 50);
                Require(slot >= -1 && slot < lists.Count, "Navigation face leaves its link lists.");
                polygons[k] = new(indices, Enumerable.Range(0, count).Select(j => f.U32(p + 24 + j * 4)).ToArray(),
                    slot, f.U32(p + 52), f.U32(p + 56));
            }
            var l = new TagData(links.Data);
            var decodedLinks = Enumerable.Range(0, links.Count).Select(k =>
            {
                int p = k * 76;
                Require(l.U8(p + 56) is 1 or 3, "Navigation link type is unknown.");
                return new NavigationLink(l.V3(p), l.V3(p + 12), l.V3(p + 24), l.V3(p + 36), l.U8(p + 49),
                    l.U8(p + 56), l.U32(p + 60), l.U32(p + 64), l.U32(p + 68), l.U32(p + 72));
            }).ToArray();
            foreach (var list in lists.Children)
            {
                var values = new TagData(list.Data);
                for (int k = 0; k < list.Count; k++) Require(values.U32(k * 4) < links.Count, "Navigation list leaves its links.");
            }
            result[i] = new(i, min, max, cs, ch, points, polygons, decodedLinks, [kind2, faces, vertices, lists, links, tree])
            { Header = bytes.AsMemory(s - 0x10, 0xD0).ToArray() };
        }
        return result;
    }

    public static NavigationGraphLayer[] DecodeGraph(byte[] bytes)
    {
        var d = Sized(bytes, 0x250);
        var result = new NavigationGraphLayer[3];
        for (int i = 0; i < 3; i++)
        {
            int s = 0x10 + i * 0xC0;
            var clusters = Array(d, s + 0x30, 0x80809A4A, 64, 8, 0x80809A4F);
            var covers = Array(d, s + 0x40, null, 80);
            var points = Array(d, s + 0x50, 0x80809A51, 48);
            var tree = Array(d, s + 0x80, 0x80800009, 1);
            var extensions = Array(d, s + 0x90, 0x80808C83, 24);
            var paths = Array(d, s + 0xA0, null, 16, 28);
            var references = Array(d, s + 0xB0, null, 24);
            Require(d.U32(s + 0x28) == i || points.Count == 0, "Navigation graph layer index is unknown.");
            var agent = Enumerable.Range(0, 6).Select(k => d.F32(s + 0x10 + 4 * k)).ToArray();
            bool empty = new[] { clusters, covers, points, tree, extensions, paths, references }.All(a => a.Count == 0);
            Require(agent.All(float.IsFinite) && (agent[0] > 0 && agent[1] > 0 || empty && agent.All(v => v == 0)), "Navigation agent is invalid.");
            var p = new TagData(points.Data);
            var decodedPoints = Enumerable.Range(0, points.Count).Select(k => new NavigationPoint(p.V3(k * 48),
                p.U32(k * 48 + 16), p.U32(k * 48 + 20), p.V3(k * 48 + 24), p.F32(k * 48 + 36),
                p.I32(k * 48 + 40), p.U32(k * 48 + 44))).ToArray();
            Require(decodedPoints.All(point => Finite(point.Position) && Finite(point.Normal) && float.IsFinite(point.Metric)),
                "Navigation point is not finite.");
            var c = new TagData(clusters.Data);
            var decodedClusters = Enumerable.Range(0, clusters.Count).Select(k => new NavigationCluster(c.V3(k * 64 + 16),
                c.F32(k * 64 + 28), c.F32(k * 64 + 32), c.F32(k * 64 + 36), c.I32(k * 64 + 48), clusters.Children[k])).ToArray();
            foreach (var cluster in decodedClusters)
            {
                Require(cluster.Representative >= 0 && cluster.Representative < cluster.Members.Count, "Navigation cluster representative is invalid.");
                var m = new TagData(cluster.Members.Data);
                for (int k = 0; k < cluster.Members.Count; k++)
                    if (m.U32(k * 8) == 0) Require(m.U32(k * 8 + 4) < points.Count, "Navigation cluster leaves its points.");
            }
            result[i] = new(i, agent, decodedPoints, decodedClusters, [clusters, covers, points, tree, extensions, paths, references])
            { Header = bytes.AsMemory(s, 0xC0).ToArray() };
        }
        return result;
    }

    private static TagData Sized(byte[] bytes, int minimum, bool exact = false)
    {
        Require(bytes.Length >= minimum && (!exact || bytes.Length == minimum), "Navigation resource is truncated.");
        var d = new TagData(bytes);
        Require(d.U64(0) == (ulong)bytes.Length, "Navigation resource size is invalid.");
        return d;
    }

    private static NavigationArray Array(TagData d, int field, uint? type, int stride, int nestedStride = 0, uint? nestedType = null)
    {
        try
        {
            long count = d.I64(field), relative = d.I64(field + 8);
            if (count == 0 && relative == 0) return new(0, stride, [], []);
            // Retail BVH bytecode can exceed TagData's million-row limit; bound it by its actual payload.
            Require(count >= 0 && count <= d.Length / stride && count * stride <= 64 * 1024 * 1024,
                "Navigation array count is out of range.");
            long header = checked(field + 8L + relative);
            Require(header >= 4 && header <= d.Length - 16 && count * stride <= d.Length - header - 16,
                "Navigation array leaves its resource.");
            int h = (int)header; uint cls = d.U32(h + 8);
            Require(d.U32(h - 4) == 0x80809FBD && d.I64(h) == count && d.U32(h + 12) == 0
                && (type is null || cls == type), "Navigation array has an unknown layout.");
            var children = nestedStride == 0 ? [] : Enumerable.Range(0, (int)count)
                .Select(i => Array(d, h + 16 + i * stride, nestedType, nestedStride)).ToArray();
            return new(cls, stride, d.Bytes.AsSpan(h + 16, checked((int)count * stride)).ToArray(), children);
        }
        catch (Exception error) when (error is ArgumentException or OverflowException or IndexOutOfRangeException)
        { throw new InvalidDataException("Navigation array leaves its resource.", error); }
    }

    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
