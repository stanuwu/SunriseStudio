using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene.Navigation;

/**
 * One nav polygon.
 * @param Vertices Indices into the layer's vertices, in order around the polygon; three to six.
 * @param Neighbours One per edge, the edge from vertex i to vertex i + 1: the polygon across it, or -1.
 */
public sealed record NavPolygon(int[] Vertices, int[] Neighbours)
{
    /** The layer's link list of this polygon, or -1 when no link touches it. */
    public int LinkList { get; init; } = -1;
    /** Polygons joined through neighbours share this number; it is the index of one of them. */
    public uint Island { get; init; }
    /** The island number after islands joined by links are merged. */
    public uint LinkedIsland { get; init; }
}

/**
 * One layer of a nav mesh, in the nav mesh's own space.
 * @param Minimum The grid's origin: every vertex is a whole number of 0.5 m cells from it.
 */
public sealed record NavLayer(int Index, Vector3 Minimum, Vector3 Maximum, Vector3[] Vertices, NavPolygon[] Polygons)
{
    /** Per polygon that a link touches: the indices of those links. */
    public int[][] LinkLists { get; init; } = [];
    /** The link rows, 76 bytes each, kept as stored. */
    public byte[][] Links { get; init; } = [];
    /** The box tree over the polygons; null for an empty layer, and for a new layer until it is written. */
    public NavMoppCode? Tree { get; init; }
    /** What the layer stores after the tree's code; three bytes in every installed layer. */
    public byte[] TreeFill { get; init; } = [];
}

/** A nav mesh tag: three layers, some of them empty. */
public sealed record NavMesh(NavLayer[] Layers);

/** Reads a nav mesh tag, class 0x80808CB8. */
public static class NavMeshReader
{
    public const uint Class = C80808cb8.Class, HolderClass = 0x80808CB6;
    public const int LayerCount = 3;

    internal const uint PolygonClass = 0x80808CA2, VertexClass = 0x80809A5F, LinkListClass = 0x80808C94, LinkClass = 0x80808C8C,
        ByteClass = 0x80800009, IndexClass = 0x80800007;
    internal const int PolygonSize = 60, VertexSize = 6, LinkListSize = 16, LinkSize = 76;
    // A polygon row: six vertex indices, six neighbours, the vertex count, the link list, two island numbers.
    internal const int MaximumCorners = 6, NeighboursAt = 24, CornerCountAt = 48, LinkListAt = 50, IslandAt = 52, LinkedIslandAt = 56;
    // A link row: four corners, a flags byte at 48, a direction byte at 49, its own index, a type byte, then a key and a polygon at each end.
    public const int LinkFlagsAt = 48, LinkDirectionAt = 49, LinkIndexAt = 52, LinkTypeAt = 56, LinkStartKeyAt = 60, LinkStartPolygonAt = 64, LinkEndKeyAt = 68, LinkEndPolygonAt = 72;
    internal const uint UnusedCorner = uint.MaxValue, NeighbourValid = 0x80000000;
    // A neighbour holds its layer above the polygon index.
    internal const int NeighbourLayerShift = 28, NeighbourLayerMask = 7, NeighbourIndexMask = (1 << NeighbourLayerShift) - 1;
    // Layer fields the registry does not name yet: the index, the tree's build type, scale and offset, the bounds, the grid step.
    internal const int IndexAt = 16, TreeBuiltAt = 112, TreeScaleAt = 116, TreeOffsetAt = 128, MinimumAt = 160, MaximumAt = 176, StepAt = 192;
    /** The grid step of every installed nav mesh, on all three axes. */
    public const float Step = 0.5f;
    // The largest installed array holds 137,619 rows; the cap leaves room above it.
    private const int MaximumRows = 1 << 22;

    internal static readonly int LayersAt = TagView.FieldOffset<C80808cb8>(nameof(C80808cb8.H00dfc4ce));
    internal static readonly int LayerSize = System.Runtime.CompilerServices.Unsafe.SizeOf<G80808c880>();
    internal static readonly int PolygonsField = TagView.FieldOffset<G80808c880>(nameof(G80808c880.Unk28)),
        VerticesField = TagView.FieldOffset<G80808c880>(nameof(G80808c880.Unk38)),
        LinkListsField = TagView.FieldOffset<G80808c880>(nameof(G80808c880.Unk48)),
        LinksField = TagView.FieldOffset<G80808c880>(nameof(G80808c880.Unk58)),
        TreeField = TagView.FieldOffset<G80808c880>(nameof(G80808c880.Unk90));

    /**
     * @param tag The bytes of a nav mesh tag.
     * @return Its three layers; a vertex or neighbour that leaves its layer is refused.
     */
    public static NavMesh Read(ReadOnlySpan<byte> tag)
    {
        if (!TagView.Fits<C80808cb8>(tag, 0)) throw new InvalidDataException("The nav mesh tag is too short.");
        var layers = new NavLayer[LayerCount];
        for (int index = 0; index < LayerCount; index++) layers[index] = Layer(tag, LayersAt + index * LayerSize, index);
        return new(layers);
    }

    /**
     * Reads the layer that starts at `at`.
     * @param index The layer's place in the tag; its neighbours must name this layer.
     */
    private static NavLayer Layer(ReadOnlySpan<byte> tag, int at, int index)
    {
        var polygons = TagArray.Checked(tag, at + PolygonsField, PolygonClass, PolygonSize, MaximumRows);
        var vertices = TagArray.Checked(tag, at + VerticesField, VertexClass, VertexSize, MaximumRows);
        var linkLists = TagArray.Checked(tag, at + LinkListsField, LinkListClass, LinkListSize, MaximumRows);
        var links = TagArray.Checked(tag, at + LinksField, LinkClass, LinkSize, MaximumRows);
        var tree = TagArray.Checked(tag, at + TreeField, ByteClass, 1, int.MaxValue);
        Vector3 minimum = Vector(tag, at + MinimumAt), maximum = Vector(tag, at + MaximumAt);
        var points = new Vector3[vertices.Count];
        for (int row = 0; row < points.Length; row++)
        {
            var data = tag.Slice(vertices.Data + row * VertexSize, VertexSize);
            points[row] = minimum + Step * new Vector3(BinaryPrimitives.ReadUInt16LittleEndian(data),
                BinaryPrimitives.ReadUInt16LittleEndian(data[2..]), BinaryPrimitives.ReadUInt16LittleEndian(data[4..]));
        }
        var rows = new NavPolygon[polygons.Count];
        for (int row = 0; row < rows.Length; row++)
            rows[row] = Polygon(tag.Slice(polygons.Data + row * PolygonSize, PolygonSize), row, index, points.Length, rows.Length, linkLists.Count);
        var lists = new int[linkLists.Count][];
        for (int row = 0; row < lists.Length; row++)
        {
            var list = TagArray.Checked(tag, linkLists.Data + row * LinkListSize, IndexClass, sizeof(int), MaximumRows);
            lists[row] = new int[list.Count];
            for (int k = 0; k < list.Count; k++) lists[row][k] = BinaryPrimitives.ReadInt32LittleEndian(tag[(list.Data + k * sizeof(int))..]);
        }
        var linkRows = new byte[links.Count][];
        for (int row = 0; row < linkRows.Length; row++) linkRows[row] = tag.Slice(links.Data + row * LinkSize, LinkSize).ToArray();
        // The stored count runs past the code; the walk finds where the code ends.
        NavMoppCode? code = null;
        byte[] fill = [];
        if (tree.Count > 0)
        {
            var stored = tag.Slice(tree.Data, tree.Count);
            int end = CodeEnd(stored);
            code = new(stored[..end].ToArray(), Vector(tag, at + TreeOffsetAt), BinaryPrimitives.ReadSingleLittleEndian(tag[(at + TreeScaleAt)..]));
            fill = stored[end..].ToArray();
        }
        return new(index, minimum, maximum, points, rows) { LinkLists = lists, Links = linkRows, Tree = code, TreeFill = fill };
    }

    /** One polygon row; an index that leaves the layer is refused. */
    private static NavPolygon Polygon(ReadOnlySpan<byte> data, int row, int layer, int vertexCount, int polygonCount, int linkListCount)
    {
        int corners = BinaryPrimitives.ReadUInt16LittleEndian(data[CornerCountAt..]);
        if (corners is < 3 or > MaximumCorners) throw new InvalidDataException($"Nav polygon {row} has {corners} corners.");
        int[] own = new int[corners], across = new int[corners];
        for (int corner = 0; corner < MaximumCorners; corner++)
        {
            uint vertex = BinaryPrimitives.ReadUInt32LittleEndian(data[(corner * sizeof(uint))..]);
            uint neighbour = BinaryPrimitives.ReadUInt32LittleEndian(data[(NeighboursAt + corner * sizeof(uint))..]);
            if (corner >= corners)
            {
                if (vertex != UnusedCorner || neighbour != 0) throw new InvalidDataException($"Nav polygon {row} uses a corner past its count.");
                continue;
            }
            if (vertex >= vertexCount) throw new InvalidDataException($"Nav polygon {row} names vertex {vertex}.");
            own[corner] = (int)vertex;
            across[corner] = -1;
            if (neighbour == 0) continue;
            int other = (int)(neighbour & NeighbourIndexMask);
            if ((neighbour & NeighbourValid) == 0 || (int)(neighbour >> NeighbourLayerShift & NeighbourLayerMask) != layer || other >= polygonCount)
                throw new InvalidDataException($"Nav polygon {row} names neighbour {neighbour:X8}.");
            across[corner] = other;
        }
        int linkList = BinaryPrimitives.ReadInt16LittleEndian(data[LinkListAt..]);
        if (linkList < -1 || linkList >= linkListCount) throw new InvalidDataException($"Nav polygon {row} names link list {linkList}.");
        return new(own, across)
        {
            LinkList = linkList, Island = BinaryPrimitives.ReadUInt32LittleEndian(data[IslandAt..]),
            LinkedIsland = BinaryPrimitives.ReadUInt32LittleEndian(data[LinkedIslandAt..]),
        };
    }

    /** Where the code ends: just past the furthest instruction any branch reaches. */
    private static int CodeEnd(ReadOnlySpan<byte> stored)
    {
        // The tree's last instruction in address order is a leaf; the fill after it is not code.
        int end = NavMopp.End(stored);
        if (end > stored.Length) throw new InvalidDataException("The nav layer's box tree runs past its bytes.");
        return end;
    }

    private static Vector3 Vector(ReadOnlySpan<byte> tag, int at) => new(BinaryPrimitives.ReadSingleLittleEndian(tag[at..]),
        BinaryPrimitives.ReadSingleLittleEndian(tag[(at + 4)..]), BinaryPrimitives.ReadSingleLittleEndian(tag[(at + 8)..]));
}
