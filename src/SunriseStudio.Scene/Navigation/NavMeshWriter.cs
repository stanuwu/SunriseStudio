using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using static SunriseStudio.Scene.Navigation.NavMeshReader;

namespace SunriseStudio.Scene.Navigation;

/** Writes a nav mesh tag, class 0x80808CB8. A mesh read from a tag writes back to the same bytes. */
public static class NavMeshWriter
{
    // The tree's build type word of a layer with polygons.
    private const uint TreeBuilt = 1;
    // What the game's own baker left after the code: unset heap bytes.
    private static readonly byte[] DefaultFill = [0xCD, 0xCD, 0xCD];

    /**
     * @param mesh Three layers. A layer with polygons and no tree gets its islands and tree from Complete.
     * @return The tag's bytes.
     */
    public static byte[] Write(NavMesh mesh, RegistryIndex schema)
    {
        if (mesh.Layers.Length != LayerCount) throw new ArgumentException("A nav mesh holds three layers.", nameof(mesh));
        var root = new TagBlueprint { ClassId = Class, Bytes = new byte[System.Runtime.CompilerServices.Unsafe.SizeOf<C80808cb8>()] };
        for (int index = 0; index < LayerCount; index++)
        {
            var layer = mesh.Layers[index];
            if (layer.Index != index) throw new ArgumentException($"Layer {index} carries index {layer.Index}.", nameof(mesh));
            Layer(root, LayersAt + index * LayerSize, layer.Polygons.Length > 0 && layer.Tree is null ? Complete(layer) : layer);
        }
        byte[] tag = CanonicalTagWriter.Write(root, schema);
        // The tag starts with its own size.
        BinaryPrimitives.WriteUInt64LittleEndian(tag, (ulong)tag.Length);
        return tag;
    }

    /**
     * Gives a new layer what the client derives nothing of itself: an island number per polygon, the
     * island after links join islands, and the box tree. Link lists and links are kept as given.
     */
    public static NavLayer Complete(NavLayer layer)
    {
        var joined = layer.Polygons.Select(polygon => polygon.Neighbours.ToList()).ToArray();
        foreach (var link in layer.Links)
        {
            int from = BinaryPrimitives.ReadInt32LittleEndian(link.AsSpan(LinkStartPolygonAt)), to = BinaryPrimitives.ReadInt32LittleEndian(link.AsSpan(LinkEndPolygonAt));
            if ((uint)from >= joined.Length || (uint)to >= joined.Length) throw new ArgumentException("A nav link names a polygon outside its layer.", nameof(layer));
            joined[from].Add(to);
            joined[to].Add(from);
        }
        int[] island = Islands(layer.Polygons.Select(polygon => (IReadOnlyList<int>)polygon.Neighbours).ToArray());
        int[] linked = Islands(joined);
        var polygons = layer.Polygons.Select((polygon, index) =>
            polygon with { Island = (uint)island[index], LinkedIsland = (uint)linked[index] }).ToArray();
        var boxes = polygons.Select(polygon => (polygon.Vertices.Select(vertex => layer.Vertices[vertex]).Aggregate(Vector3.Min),
            polygon.Vertices.Select(vertex => layer.Vertices[vertex]).Aggregate(Vector3.Max))).ToArray();
        return layer with { Polygons = polygons, Tree = NavMopp.Build(boxes), TreeFill = DefaultFill };
    }

    /** Polygons joined through `next` share the lowest index among them. */
    private static int[] Islands(IReadOnlyList<int>[] next)
    {
        int[] island = Enumerable.Repeat(-1, next.Length).ToArray();
        var queue = new Queue<int>();
        for (int start = 0; start < island.Length; start++)
        {
            if (island[start] >= 0) continue;
            island[start] = start;
            queue.Enqueue(start);
            while (queue.TryDequeue(out int at))
                foreach (int other in next[at])
                    if (other >= 0 && island[other] < 0) { island[other] = start; queue.Enqueue(other); }
        }
        return island;
    }
    /** Fills one layer struct at `at` of the root and adds its arrays. */
    private static void Layer(TagBlueprint root, int at, NavLayer layer)
    {
        var data = root.Bytes.AsSpan(at, LayerSize);
        BinaryPrimitives.WriteInt32LittleEndian(data[IndexAt..], layer.Index);
        Put(data[MinimumAt..], layer.Minimum);
        Put(data[MaximumAt..], layer.Maximum);
        // The fourth float of each bounds corner is 1.
        BinaryPrimitives.WriteSingleLittleEndian(data[(MinimumAt + 12)..], 1);
        BinaryPrimitives.WriteSingleLittleEndian(data[(MaximumAt + 12)..], 1);
        if (layer.Polygons.Length == 0)
        {
            if (layer.Vertices.Length > 0 || layer.Tree is not null) throw new ArgumentException("An empty layer holds only its bounds.");
            // An empty layer keeps a scale of 1 and no step.
            BinaryPrimitives.WriteSingleLittleEndian(data[TreeScaleAt..], 1);
            return;
        }
        var tree = layer.Tree ?? throw new ArgumentException("A layer with polygons needs its box tree.");
        BinaryPrimitives.WriteUInt32LittleEndian(data[TreeBuiltAt..], TreeBuilt);
        BinaryPrimitives.WriteSingleLittleEndian(data[TreeScaleAt..], tree.Scale);
        Put(data[TreeOffsetAt..], tree.Offset);
        BinaryPrimitives.WriteSingleLittleEndian(data[StepAt..], Step);
        BinaryPrimitives.WriteSingleLittleEndian(data[(StepAt + 4)..], Step);
        root.Links.Add(new(at + PolygonsField, TagLinkKind.Array, PolygonClass, layer.Polygons.Select(polygon => Polygon(polygon, layer)).ToList()));
        root.Links.Add(new(at + VerticesField, TagLinkKind.Array, VertexClass, layer.Vertices.Select(point => Vertex(point, layer)).ToList()));
        if (layer.LinkLists.Length > 0)
            root.Links.Add(new(at + LinkListsField, TagLinkKind.Array, LinkListClass, layer.LinkLists.Select(list =>
            {
                var row = new TagBlueprint { ClassId = LinkListClass, Bytes = new byte[LinkListSize] };
                row.Links.Add(new(0, TagLinkKind.Array, IndexClass, list.Select(index => Row(IndexClass, BitConverter.GetBytes(index))).ToList()));
                return row;
            }).ToList()));
        if (layer.Links.Length > 0)
            root.Links.Add(new(at + LinksField, TagLinkKind.Array, LinkClass, layer.Links.Select(link =>
                link.Length == LinkSize ? Row(LinkClass, link) : throw new ArgumentException("A nav link row is 76 bytes.")).ToList()));
        root.Links.Add(new(at + TreeField, TagLinkKind.Array, ByteClass,
            tree.Code.Concat(layer.TreeFill).Select(value => Row(ByteClass, [value])).ToList()));
    }

    private static TagBlueprint Polygon(NavPolygon polygon, NavLayer layer)
    {
        int corners = polygon.Vertices.Length;
        if (corners is < 3 or > MaximumCorners || polygon.Neighbours.Length != corners) throw new ArgumentException("A nav polygon has three to six corners.");
        byte[] row = new byte[PolygonSize];
        for (int corner = 0; corner < MaximumCorners; corner++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(corner * sizeof(uint)), corner < corners ? (uint)polygon.Vertices[corner] : UnusedCorner);
            if (corner < corners && polygon.Neighbours[corner] >= 0)
                BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(NeighboursAt + corner * sizeof(uint)),
                    NeighbourValid | (uint)layer.Index << NeighbourLayerShift | (uint)polygon.Neighbours[corner]);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(CornerCountAt), (ushort)corners);
        BinaryPrimitives.WriteInt16LittleEndian(row.AsSpan(LinkListAt), (short)polygon.LinkList);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(IslandAt), polygon.Island);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(LinkedIslandAt), polygon.LinkedIsland);
        return Row(PolygonClass, row);
    }

    /** A vertex as whole grid cells from the layer's minimum; a point off the grid is refused. */
    private static TagBlueprint Vertex(Vector3 point, NavLayer layer)
    {
        var cells = (point - layer.Minimum) / Step;
        var whole = Vector3.Round(cells);
        if (Vector3.Distance(cells, whole) > 1e-3f || whole.X is < 0 or > ushort.MaxValue || whole.Y is < 0 or > ushort.MaxValue
            || whole.Z is < 0 or > ushort.MaxValue)
            throw new ArgumentException($"Nav vertex {point} is off its layer's grid.");
        byte[] row = new byte[VertexSize];
        BinaryPrimitives.WriteUInt16LittleEndian(row, (ushort)whole.X);
        BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(2), (ushort)whole.Y);
        BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(4), (ushort)whole.Z);
        return Row(VertexClass, row);
    }

    private static TagBlueprint Row(uint classId, byte[] bytes) => new() { ClassId = classId, Bytes = bytes };

    private static void Put(Span<byte> target, Vector3 value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(target, value.X);
        BinaryPrimitives.WriteSingleLittleEndian(target[4..], value.Y);
        BinaryPrimitives.WriteSingleLittleEndian(target[8..], value.Z);
    }
}
