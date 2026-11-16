using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene.Navigation;

/**
 * One static firing point of a layer.
 * @param Normal The ground's normal; installed ones are 2 pi long.
 * @param Key The layer word of a nav key: 0x2000 plus the layer shifted left 10.
 */
public sealed record NavFiringPoint(Vector3 Normal, uint Key, int Polygon, Vector3 Position, float Metric)
{
    /** -1 in most installed points. */
    public int Cover { get; init; } = -1;
    public uint Flags { get; init; }
}

/**
 * A group of firing points.
 * @param Members Per member: its kind (0 for a point of this layer) and its row.
 * @param Representative The member that stands for the cluster.
 */
public sealed record NavFiringCluster(Vector3 Centre, float Radius, float MinimumZ, float MaximumZ,
    (uint Kind, int Row)[] Members, int Representative);

/**
 * A row of class 0x80808C83: one per nav link of the layer.
 * @param Key 0x6000 plus the layer shifted left 10.
 * @param Height The span of the link's four corners on Z.
 */
public sealed record NavFiringLink(uint Key, int Link, float Height, float Unknown12, float Unknown16, uint Direction);

/**
 * One layer of a firing point tag.
 * @param Agent The six floats each installed layer stores first: radius, height, slope in degrees, then three unread.
 */
public sealed record NavFiringLayer(int Index, float[] Agent, NavFiringPoint[] Points, NavFiringCluster[] Clusters, NavFiringLink[] Links)
{
    /** The box tree over the clusters; null when the layer has none. */
    public NavMoppCode? Tree { get; init; }
    public byte[] TreeFill { get; init; } = [];
}

/** A holder's second tag, class 0x80808CBA: the static firing points of three layers. */
public sealed record NavFiringPoints(NavFiringLayer[] Layers);

/** Reads and writes a firing point tag. Covers, paths and the other arrays are not modelled. */
public static class NavFiringPointTag
{
    public const uint Class = C80808cba.Class;
    // The tree's build type word; the registry does not name it.
    internal const int TreeBuiltAt = 96;
    // A cluster row names its representative member after the generated floats; the next word is -1 in installed rows.
    internal const int RepresentativeAt = 48, ClusterTailAt = 52;
    // A point row: face key and polygon after the normal; cover and flags after the metric.
    internal const int PointKeyAt = 16, PointPolygonAt = 20, PointCoverAt = 40, PointFlagsAt = 44;
    // A link row: its link index after the key, and its direction word last.
    internal const int LinkIndexAt = 4, LinkDirectionAt = 20;
    // A member row: its kind, then its row.
    internal const int MemberRowAt = 4;
    // The tree's build type word of a layer with clusters.
    private const uint TreeBuilt = 1;
    // A row count cap well above the largest installed array.
    private const int MaximumRows = 1 << 22;
    // What the game's own baker left after the code: unset heap bytes.
    private static readonly byte[] DefaultFill = [0xCD, 0xCD, 0xCD];

    internal static readonly int LayersAt = TagView.FieldOffset<C80808cba>(nameof(C80808cba.H672f3106));
    internal static readonly int LayerSize = Unsafe.SizeOf<G80808c7a0>();
    private static readonly int ClusterSize = Unsafe.SizeOf<C80809a4a>(), MemberSize = Unsafe.SizeOf<C80809a4f>(),
        PointSize = Unsafe.SizeOf<C80809a51>(), LinkRowSize = Unsafe.SizeOf<C80808c83>();
    private static readonly int ClustersField = TagView.FieldOffset<G80808c7a0>(nameof(G80808c7a0.Unk30)),
        PointsField = TagView.FieldOffset<G80808c7a0>(nameof(G80808c7a0.Unk50)),
        TreeField = TagView.FieldOffset<G80808c7a0>(nameof(G80808c7a0.Unk80)),
        LinksField = TagView.FieldOffset<G80808c7a0>(nameof(G80808c7a0.Unk90)),
        MembersField = TagView.FieldOffset<C80809a4a>(nameof(C80809a4a.H6139c90c));

    /** @return The three layers; a member that names a missing point is refused. */
    public static NavFiringPoints Read(ReadOnlySpan<byte> tag)
    {
        if (!TagView.Fits<C80808cba>(tag, 0)) throw new InvalidDataException("The firing point tag is too short.");
        var layers = new NavFiringLayer[NavMeshReader.LayerCount];
        for (int index = 0; index < layers.Length; index++) layers[index] = Layer(tag, LayersAt + index * LayerSize, index);
        return new(layers);
    }

    /** @return The six agent floats of each layer. */
    public static float[][] Agents(ReadOnlySpan<byte> tag)
    {
        if (!TagView.Fits<C80808cba>(tag, 0)) throw new InvalidDataException("The firing point tag is too short.");
        var agents = new float[NavMeshReader.LayerCount][];
        for (int index = 0; index < agents.Length; index++) agents[index] = Agent(TagView.At<G80808c7a0>(tag, LayersAt + index * LayerSize));
        return agents;
    }

    private static float[] Agent(in G80808c7a0 layer) => [layer.Unk10, layer.Unk14, layer.Unk18, layer.Unk1c, layer.Unk20, layer.Unk24];

    private static NavFiringLayer Layer(ReadOnlySpan<byte> tag, int at, int index)
    {
        ref readonly var layer = ref TagView.At<G80808c7a0>(tag, at);
        var points = TagArray.Checked(tag, at + PointsField, C80809a51.Class, PointSize, MaximumRows);
        var clusters = TagArray.Checked(tag, at + ClustersField, C80809a4a.Class, ClusterSize, MaximumRows);
        var links = TagArray.Checked(tag, at + LinksField, C80808c83.Class, LinkRowSize, MaximumRows);
        var tree = TagArray.Checked(tag, at + TreeField, ExpressionCode.Class, 1, int.MaxValue);
        var pointRows = new NavFiringPoint[points.Count];
        for (int row = 0; row < pointRows.Length; row++)
        {
            int p = points.Data + row * PointSize;
            ref readonly var point = ref TagView.At<C80809a51>(tag, p);
            pointRows[row] = new(TagRead.Vec3(tag, p), TagRead.U32(tag, p + PointKeyAt), TagRead.I32(tag, p + PointPolygonAt),
                new(point.Unk18, point.Unk1c, point.Unk20), point.Unk24)
            { Cover = TagRead.I32(tag, p + PointCoverAt), Flags = TagRead.U32(tag, p + PointFlagsAt) };
        }
        var clusterRows = new NavFiringCluster[clusters.Count];
        for (int row = 0; row < clusterRows.Length; row++)
        {
            int c = clusters.Data + row * ClusterSize;
            ref readonly var cluster = ref TagView.At<C80809a4a>(tag, c);
            var members = TagArray.Checked(tag, c + MembersField, C80809a4f.Class, MemberSize, MaximumRows);
            var list = new (uint, int)[members.Count];
            for (int k = 0; k < list.Length; k++)
            {
                int m = members.Data + k * MemberSize;
                list[k] = (TagRead.U32(tag, m), TagRead.I32(tag, m + MemberRowAt));
                if (list[k].Item1 == 0 && (uint)list[k].Item2 >= pointRows.Length)
                    throw new InvalidDataException($"Firing cluster {row} names point {list[k].Item2}.");
            }
            int representative = TagRead.I32(tag, c + RepresentativeAt);
            if (representative < 0 || representative >= list.Length && list.Length > 0)
                throw new InvalidDataException($"Firing cluster {row} names member {representative}.");
            clusterRows[row] = new(new(cluster.Unk10, cluster.Unk14, cluster.Unk18), cluster.Unk1c, cluster.Unk20, cluster.Unk24,
                list, representative);
        }
        var linkRows = new NavFiringLink[links.Count];
        for (int row = 0; row < linkRows.Length; row++)
        {
            int l = links.Data + row * LinkRowSize;
            ref readonly var link = ref TagView.At<C80808c83>(tag, l);
            linkRows[row] = new(TagRead.U32(tag, l), TagRead.I32(tag, l + LinkIndexAt), link.Unk8, link.UnkC, link.Unk10,
                TagRead.U32(tag, l + LinkDirectionAt));
        }
        NavMoppCode? code = null;
        byte[] fill = [];
        if (tree.Count > 0)
        {
            var stored = tag.Slice(tree.Data, tree.Count);
            int end = NavMopp.End(stored);
            if (end > stored.Length) throw new InvalidDataException("The firing point box tree runs past its bytes.");
            code = new(stored[..end].ToArray(), new(layer.Unk70, layer.Unk74, layer.Unk78), layer.Unk64);
            fill = stored[end..].ToArray();
        }
        return new(index, Agent(layer), pointRows, clusterRows, linkRows) { Tree = code, TreeFill = fill };
    }

    /**
     * Writes the layers over an installed bare firing point struct, which carries the agent floats and layer indices.
     * @param template An installed firing point tag with no arrays.
     * @return The tag's bytes. A layer with clusters and no tree gets one over its cluster boxes.
     */
    public static byte[] Write(NavFiringPoints points, ReadOnlySpan<byte> template, RegistryIndex schema)
    {
        if (points.Layers.Length != NavMeshReader.LayerCount) throw new ArgumentException("A firing point tag holds three layers.", nameof(points));
        if (template.Length != Unsafe.SizeOf<C80808cba>()) throw new ArgumentException("The template must be a bare firing point struct.", nameof(template));
        var root = new TagBlueprint { ClassId = Class, Bytes = template.ToArray() };
        foreach (var layer in points.Layers) Layer(root, LayersAt + layer.Index * LayerSize, layer);
        byte[] tag = CanonicalTagWriter.Write(root, schema);
        // The tag starts with its own size.
        BinaryPrimitives.WriteUInt64LittleEndian(tag, (ulong)tag.Length);
        return tag;
    }

    private static void Layer(TagBlueprint root, int at, NavFiringLayer layer)
    {
        if (layer.Points.Length == 0) return;
        var data = root.Bytes.AsSpan(at, LayerSize);
        var tree = layer.Tree ?? (layer.Clusters.Length == 0 ? null : NavMopp.Build(layer.Clusters.Select(cluster =>
            (new Vector3(cluster.Centre.X - cluster.Radius, cluster.Centre.Y - cluster.Radius, cluster.MinimumZ),
                new Vector3(cluster.Centre.X + cluster.Radius, cluster.Centre.Y + cluster.Radius, cluster.MaximumZ))).ToArray()));
        root.Links.Add(new(at + PointsField, TagLinkKind.Array, C80809a51.Class, layer.Points.Select(Point).ToList()));
        if (layer.Clusters.Length > 0)
            root.Links.Add(new(at + ClustersField, TagLinkKind.Array, C80809a4a.Class, layer.Clusters.Select(Cluster).ToList()));
        if (layer.Links.Length > 0)
            root.Links.Add(new(at + LinksField, TagLinkKind.Array, C80808c83.Class, layer.Links.Select(Link).ToList()));
        if (tree is null) return;
        BinaryPrimitives.WriteUInt32LittleEndian(data[TreeBuiltAt..], TreeBuilt);
        ref var view = ref TagView.Edit<G80808c7a0>(data);
        view.Unk64 = tree.Scale;
        (view.Unk70, view.Unk74, view.Unk78) = (tree.Offset.X, tree.Offset.Y, tree.Offset.Z);
        byte[] fill = layer.Tree is null ? DefaultFill : layer.TreeFill;
        root.Links.Add(new(at + TreeField, TagLinkKind.Array, ExpressionCode.Class,
            tree.Code.Concat(fill).Select(value => Row(ExpressionCode.Class, [value])).ToList()));
    }

    private static TagBlueprint Point(NavFiringPoint point)
    {
        byte[] row = new byte[PointSize];
        BinaryPrimitives.WriteSingleLittleEndian(row, point.Normal.X);
        BinaryPrimitives.WriteSingleLittleEndian(row.AsSpan(4), point.Normal.Y);
        ref var view = ref TagView.Edit<C80809a51>(row);
        view.Unk8 = point.Normal.Z;
        (view.Unk18, view.Unk1c, view.Unk20, view.Unk24) = (point.Position.X, point.Position.Y, point.Position.Z, point.Metric);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(PointKeyAt), point.Key);
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(PointPolygonAt), point.Polygon);
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(PointCoverAt), point.Cover);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(PointFlagsAt), point.Flags);
        return Row(C80809a51.Class, row);
    }

    private static TagBlueprint Cluster(NavFiringCluster cluster)
    {
        var row = new TagBlueprint { ClassId = C80809a4a.Class, Bytes = new byte[ClusterSize] };
        ref var view = ref TagView.Edit<C80809a4a>(row.Bytes);
        (view.Unk10, view.Unk14, view.Unk18) = (cluster.Centre.X, cluster.Centre.Y, cluster.Centre.Z);
        (view.Unk1c, view.Unk20, view.Unk24) = (cluster.Radius, cluster.MinimumZ, cluster.MaximumZ);
        BinaryPrimitives.WriteInt32LittleEndian(row.Bytes.AsSpan(RepresentativeAt), cluster.Representative);
        BinaryPrimitives.WriteUInt32LittleEndian(row.Bytes.AsSpan(ClusterTailAt), uint.MaxValue);
        row.Links.Add(new(MembersField, TagLinkKind.Array, C80809a4f.Class, cluster.Members.Select(member =>
        {
            byte[] bytes = new byte[MemberSize];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, member.Kind);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(MemberRowAt), member.Row);
            return Row(C80809a4f.Class, bytes);
        }).ToList()));
        return row;
    }

    private static TagBlueprint Link(NavFiringLink link)
    {
        byte[] row = new byte[LinkRowSize];
        BinaryPrimitives.WriteUInt32LittleEndian(row, link.Key);
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(LinkIndexAt), link.Link);
        ref var view = ref TagView.Edit<C80808c83>(row);
        (view.Unk8, view.UnkC, view.Unk10) = (link.Height, link.Unknown12, link.Unknown16);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(LinkDirectionAt), link.Direction);
        return Row(C80808c83.Class, row);
    }

    private static TagBlueprint Row(uint classId, byte[] bytes) => new() { ClassId = classId, Bytes = bytes };
}
