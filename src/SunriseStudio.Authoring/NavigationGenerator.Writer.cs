using System.Buffers.Binary;
using System.Numerics;

namespace SunriseStudio.Authoring;

public static partial class NavigationGenerator
{
    /** Array framing reuses the native append writer; empty arrays have zero descriptors and no header. */
    private sealed class Blob(int size)
    {
        public byte[] Bytes = new byte[size];
        public int Array(int field, uint type, int stride, byte[] rows)
        {
            if (rows.Length == 0) { Bytes.AsSpan(field, 16).Clear(); return 0; }
            Bytes = NativeRows.Append(Bytes, field, type, stride, rows, out int first); return first;
        }
        public void U32(int p, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(p), v);
        public void I16(int p, short v) => BinaryPrimitives.WriteInt16LittleEndian(Bytes.AsSpan(p), v);
        public void U16(int p, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(p), v);
        public void F32(int p, float v) => BinaryPrimitives.WriteSingleLittleEndian(Bytes.AsSpan(p), v);
        public void V3(int p, Vector3 v) { F32(p, v.X); F32(p + 4, v.Y); F32(p + 8, v.Z); }
        public void V3(int p, (double X, double Y, double Z) v) => V3(p, new Vector3((float)v.X, (float)v.Y, (float)v.Z));
        public byte[] Done() { BinaryPrimitives.WriteInt64LittleEndian(Bytes, Bytes.Length); return Bytes; }
    }

    private static GeneratedNavigation Write(Layer?[] layers)
    {
        var mesh = new Blob(0x280); var graph = new Blob(0x250);
        var minimum = new Vector3(float.MaxValue); var maximum = -minimum;
        for (int li = 0; li < 3; li++)
        {
            int s = 0x20 + li * 0xD0; mesh.U32(s, (uint)li);
            var layer = layers[li];
            if (layer is null)
            { mesh.F32(s + 0x64, 1); mesh.V3(s + 0x90, new Vector3(float.MaxValue)); mesh.V3(s + 0xA0, new Vector3(-float.MaxValue)); mesh.F32(s + 0x9C, 1); mesh.F32(s + 0xAC, 1); continue; }
            minimum = Vector3.Min(minimum, layer.Minimum); maximum = Vector3.Max(maximum, layer.Maximum);
            var lists = new List<List<int>>(); var slots = Enumerable.Repeat(-1, layer.Faces.Length).ToArray();
            var linked = layer.Neighbours.Select(n => n.ToList()).ToArray();
            for (int k = 0; k < layer.Links.Length; k++)
            {
                var link = layer.Links[k]; linked[link.Start].Add(link.End); linked[link.End].Add(link.Start);
                foreach (int f in new[] { link.Start, link.End })
                {
                    if (slots[f] < 0) { slots[f] = lists.Count; lists.Add([]); }
                    lists[slots[f]].Add(k);
                }
            }
            Require(lists.Count < short.MaxValue, "Navigation link lists exceed the face slot.");
            int[] components = Components(linked.Select(n => n.ToArray()).ToArray());
            var faces = new Blob(layer.Faces.Length * 60);
            for (int f = 0; f < layer.Faces.Length; f++)
            {
                int p = f * 60; var indices = layer.Faces[f];
                for (int v = 0; v < 6; v++)
                {
                    faces.U32(p + v * 4, v < indices.Length ? (uint)indices[v] : uint.MaxValue);
                    int other = v < indices.Length ? layer.Neighbours[f][v] : -1;
                    faces.U32(p + 24 + v * 4, other < 0 ? 0 : 0x80000000 | (uint)li << 28 | (uint)other);
                }
                faces.Bytes[p + 48] = (byte)indices.Length; faces.I16(p + 50, (short)slots[f]);
                faces.U32(p + 52, (uint)layer.Components[f]); faces.U32(p + 56, (uint)components[f]);
            }
            mesh.Array(s + 0x18, 0x80808CA2, 60, faces.Bytes);
            var vertices = new Blob(layer.Vertices.Length * 6);
            for (int v = 0; v < layer.Vertices.Length; v++)
            {
                var q = (layer.Vertices[v] - layer.Minimum) * 2;
                for (int k = 0; k < 3; k++)
                { Require(q[k] >= 0 && q[k] <= ushort.MaxValue, "Navigation exceeds its 16-bit lattice."); vertices.U16(v * 6 + k * 2, checked((ushort)Math.Round(q[k]))); }
            }
            mesh.Array(s + 0x28, 0x80809A5F, 6, vertices.Bytes);
            int listAt = mesh.Array(s + 0x38, 0x80808C94, 16, new byte[lists.Count * 16]);
            for (int i = 0; i < lists.Count; i++)
            {
                var values = new Blob(lists[i].Count * 4);
                for (int k = 0; k < lists[i].Count; k++) values.U32(k * 4, (uint)lists[i][k]);
                mesh.Array(listAt + i * 16, 0x80800007, 4, values.Bytes);
            }
            var links = new Blob(layer.Links.Length * 76);
            for (int k = 0; k < layer.Links.Length; k++)
            {
                int p = k * 76; var link = layer.Links[k];
                links.V3(p, link.Start0); links.V3(p + 12, link.Start1); links.V3(p + 24, link.End0); links.V3(p + 36, link.End1);
                links.Bytes[p + 48] = 1; links.Bytes[p + 49] = link.Both ? (byte)2 : (byte)0;
                links.U32(p + 52, (uint)k); links.Bytes[p + 56] = 1;
                links.U32(p + 60, 0x2000 | (uint)li << 10); links.U32(p + 64, (uint)link.Start);
                links.U32(p + 68, 0x2000 | (uint)li << 10); links.U32(p + 72, (uint)link.End);
            }
            mesh.Array(s + 0x48, 0x80808C8C, 76, links.Bytes);
            var lo = layer.Faces.Select(f => f.Select(v => layer.Vertices[v]).Aggregate(Vector3.Min)).ToArray();
            var hi = layer.Faces.Select(f => f.Select(v => layer.Vertices[v]).Aggregate(Vector3.Max)).ToArray();
            var vmin = layer.Vertices.Aggregate(Vector3.Min); var vmax = layer.Vertices.Aggregate(Vector3.Max);
            var origin = new Vector3((float)(vmin.X - .05), (float)(vmin.Y - .05), (float)(vmin.Z - .05));
            float scale = (float)(254 * 65536d / Enumerable.Range(0, 3).Max(k => vmax[k] + .05 - (vmin[k] - .05)));
            mesh.U32(s + 0x60, 1); mesh.F32(s + 0x64, scale); mesh.V3(s + 0x70, origin);
            mesh.Array(s + 0x80, 0x80800009, 1, Tree(lo.Select(v => new[] { (double)v.X, v.Y, v.Z }).ToArray(), hi.Select(v => new[] { (double)v.X, v.Y, v.Z }).ToArray(), origin, scale));
            mesh.V3(s + 0x90, layer.Minimum); mesh.F32(s + 0x9C, 1); mesh.V3(s + 0xA0, layer.Maximum); mesh.F32(s + 0xAC, 1);
            mesh.F32(s + 0xB0, .5f); mesh.F32(s + 0xB4, .5f);
        }
        for (int li = 0; li < 3; li++)
        {
            int s = 0x10 + li * 0xC0;
            for (int k = 0; k < 6; k++) graph.F32(s + 0x10 + k * 4, (float)Agents[li][k]);
            graph.U32(s + 0x28, (uint)li);
            if (layers[li] is not { } layer || layer.Points.Length == 0) continue;
            int at = graph.Array(s + 0x30, 0x80809A4A, 64, new byte[layer.Clusters.Length * 64]);
            for (int k = 0; k < layer.Clusters.Length; k++)
            {
                int p = at + k * 64; var c = layer.Clusters[k];
                graph.V3(p + 16, c.Centre); graph.F32(p + 28, (float)c.Radius); graph.F32(p + 32, (float)c.MinimumZ); graph.F32(p + 36, (float)c.MaximumZ); graph.U32(p + 52, uint.MaxValue);
                var members = new Blob(c.Members.Length * 8);
                for (int j = 0; j < c.Members.Length; j++) members.U32(j * 8 + 4, (uint)c.Members[j]);
                graph.Array(p, 0x80809A4F, 8, members.Bytes);
            }
            var points = new Blob(layer.Points.Length * 48);
            for (int k = 0; k < layer.Points.Length; k++)
            {
                var p = layer.Points[k]; int o = k * 48;
                points.V3(o, (p.Normal.X * (2 * Math.PI), p.Normal.Y * (2 * Math.PI), p.Normal.Z * (2 * Math.PI))); points.U32(o + 16, 0x2000 | (uint)li << 10); points.U32(o + 20, (uint)p.Face);
                points.V3(o + 24, p.Position); points.F32(o + 36, p.Metric); points.U32(o + 40, uint.MaxValue); points.U32(o + 44, p.Representative ? 0x20Cu : 0x208u);
            }
            graph.Array(s + 0x50, 0x80809A51, 48, points.Bytes);
            var lo = layer.Clusters.Select(c => new[] { c.Centre.X - c.Radius, c.Centre.Y - c.Radius, c.MinimumZ }).ToArray();
            var hi = layer.Clusters.Select(c => new[] { c.Centre.X + c.Radius, c.Centre.Y + c.Radius, c.MaximumZ }).ToArray();
            var origin = new Vector3((float)lo.Min(v => v[0]), (float)lo.Min(v => v[1]), (float)lo.Min(v => v[2]));
            float scale = (float)(254 * 65536d / Math.Max(.001, Enumerable.Range(0, 3).Max(k => hi.Max(v => v[k]) - origin[k])));
            graph.U32(s + 0x60, 1); graph.F32(s + 0x64, scale); graph.V3(s + 0x68, origin);
            graph.Array(s + 0x80, 0x80800009, 1, Tree(lo, hi, origin, scale));
            var extensions = new Blob(layer.Links.Length * 24);
            for (int k = 0; k < layer.Links.Length; k++)
            {
                var link = layer.Links[k]; int p = k * 24;
                var zs = new[] { link.Start0.Z, link.Start1.Z, link.End0.Z, link.End1.Z };
                extensions.U32(p, 0x6000 | (uint)li << 10); extensions.U32(p + 4, (uint)k);
                extensions.F32(p + 8, zs.Max() - zs.Min()); extensions.F32(p + 12, 1); extensions.U32(p + 16, 0x3E033333); extensions.U32(p + 20, link.Both ? 3u : 1u);
            }
            graph.Array(s + 0x90, 0x80808C83, 24, extensions.Bytes);
        }
        var root = new Blob(48); root.V3(16, minimum); root.F32(28, 1); root.V3(32, maximum); root.F32(44, 1);
        return new(root.Done(), mesh.Done(), graph.Done());
    }

    /** Retail BVH axis splits use big-endian offsets; the walker needs three trailing slack bytes. */
    private static byte[] Tree(double[][] minimum, double[][] maximum, Vector3 origin, float scale)
    {
        var lo = minimum.Select(v => Enumerable.Range(0, 3).Select(k => (int)Math.Clamp(Math.Floor((v[k] - (double)origin[k]) * scale / 65536), 0, 255)).ToArray()).ToArray();
        var hi = maximum.Select(v => Enumerable.Range(0, 3).Select(k => (int)Math.Clamp(Math.Floor((v[k] - (double)origin[k]) * scale / 65536) + 1, 0, 255)).ToArray()).ToArray();
        byte[] Node(int[] items)
        {
            if (items.Length == 1)
            {
                uint i = (uint)items[0];
                if (i < 256) return [0x50, (byte)i];
                if (i < 65536) return [0x51, (byte)(i >> 8), (byte)i];
                return [0x53, (byte)(i >> 24), (byte)(i >> 16), (byte)(i >> 8), (byte)i];
            }
            int axis = Enumerable.Range(0, 3).OrderByDescending(a => items.Max(i => hi[i][a]) - items.Min(i => lo[i][a])).First();
            var sorted = items.OrderBy(i => lo[i][axis] + hi[i][axis]).ThenBy(i => i).ToArray();
            var left = sorted[..(sorted.Length / 2)]; var right = sorted[(sorted.Length / 2)..];
            byte[] lb = Node(left), rb = Node(right);
            int high = left.Max(i => hi[i][axis]), low = right.Min(i => lo[i][axis]);
            if (lb.Length < 65536) return [(byte)(0x23 + axis), (byte)high, (byte)low, 0, 0, (byte)(lb.Length >> 8), (byte)lb.Length, .. lb, .. rb];
            Require(lb.Length < 1 << 24, "Navigation BVH jump exceeds 24 bits.");
            return [(byte)(0x23 + axis), (byte)high, (byte)low, 0, 4, 0, 0, 7, (byte)(lb.Length >> 16), (byte)(lb.Length >> 8), (byte)lb.Length, .. lb, .. rb];
        }
        return [0x28, 0, (byte)Math.Min(hi.Max(v => v[2]) + 1, 255), 0x27, 0, (byte)Math.Min(hi.Max(v => v[1]) + 1, 255),
            0x26, 0, (byte)Math.Min(hi.Max(v => v[0]) + 1, 255), .. Node(Enumerable.Range(0, lo.Length).ToArray()), 0, 0, 0];
    }
}
