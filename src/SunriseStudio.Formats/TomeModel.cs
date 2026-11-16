using System.Numerics;

namespace SunriseStudio.Formats;

/** A box in a 16-bit grid that spans its owner's box: the tile for cells, the tome for clusters. */
public readonly record struct TomeGridBox(ushort MinX, ushort MinY, ushort MinZ, ushort MaxX, ushort MaxY, ushort MaxZ)
{
    internal static TomeGridBox Unpack(uint a, uint b, uint c) =>
        new((ushort)(a >> 16), (ushort)a, (ushort)(b >> 16), (ushort)b, (ushort)(c >> 16), (ushort)c);

    internal (uint, uint, uint) Pack() =>
        ((uint)MinX << 16 | MinY, (uint)MinZ << 16 | MaxX, (uint)MaxY << 16 | MaxZ);
}

/** One 16-byte portal record; every bit has an accessor and the four words round-trip unchanged. */
public readonly record struct TomePortal(uint Link, uint IndexZ, uint XRange, uint YRange)
{
    public const uint OutsideTarget = 0x1FFFFFF;
    public int Face => (int)(Link >> 29);
    public int Axis => Face >> 1;
    public bool IsOutside => (Link >> 28 & 1) != 0;
    public bool IsGate => (Link >> 27 & 1) != 0;
    public bool IsHierarchy => (Link >> 26 & 1) != 0;
    public bool HasInternalFlag => (Link >> 25 & 1) != 0;
    /** The target slot for a tile portal, or the target cluster for a cluster portal. */
    public uint Target => Link & OutsideTarget;
    public int TargetCell => (int)(IndexZ >> 16);
    public ushort Z => (ushort)IndexZ;
    public ushort MinX => (ushort)(XRange >> 16);
    public ushort MaxX => (ushort)XRange;
    public ushort MinY => (ushort)(YRange >> 16);
    public ushort MaxY => (ushort)YRange;
    public int GateIndexOffset => (int)(XRange >> 12);
    public int GateIndexCount => (int)(XRange & 0xFFF);
    public int GeometryOffset => (int)(YRange >> 12);
    public int VertexCount => (int)(YRange & 0xFFF);

    /** Builds a plain axis-aligned portal; the rectangle axes follow the face axis. */
    public static TomePortal Plain(int face, uint targetSlot, int targetCell, ushort z,
        ushort minX, ushort maxX, ushort minY, ushort maxY, bool hierarchy = false)
    {
        if ((uint)face > 5 || targetSlot > OutsideTarget || (uint)targetCell > 0xFFFF)
            throw new ArgumentOutOfRangeException(nameof(face), "The portal does not fit its record.");
        uint link = (uint)face << 29 | (hierarchy ? 1u << 26 : 0) | targetSlot;
        return new(link, (uint)targetCell << 16 | z, (uint)minX << 16 | maxX, (uint)minY << 16 | maxY);
    }
}

/** A cell node; its first-portal index is not stored here because it is the running portal sum. */
public sealed class TomeCell
{
    public List<TomePortal> Portals { get; init; } = [];
    /** First run entry in the object lists. */
    public uint ObjectIndex { get; set; }
    /** Object count, the sum of run lengths, not the entry count. */
    public uint ObjectCount { get; set; }
    /** Leaf tiles: the cluster. Hierarchy tiles: one cluster, or the first cluster-list entry. */
    public uint ClusterIndex { get; set; }
    /** Zero when ClusterIndex is one cluster; otherwise the cluster count in the cluster lists. */
    public uint ClusterCount { get; set; }
    public TomeGridBox Bounds { get; set; }
}

/** A 3.3 BSP node; a leaf child holds a cell, 0xFFFF for none, and an inner child a node index. */
public readonly record struct TomeBspNode(uint Plane, bool FrontIsLeaf, bool BackIsLeaf, ushort Front, ushort Back)
{
    public const ushort NoCell = 0xFFFF;
}

/** An implicit breadth-first KD tree; codes 0 to 2 split on x, y or z and 3 marks a leaf. */
public sealed class TomeKdTree
{
    public const byte Leaf = 3;
    public List<byte> Codes { get; init; } = [];
    /**
     * Node i below this count splits here; later inner nodes split at their box middle.
     * A tile tree needs one per node: the client reads all of them and has no middle fallback there.
     */
    public List<float> Splits { get; init; } = [];

    public int LeafCount => Codes.Count(code => code == Leaf);

    /** Inner nodes in [0, i] for every node i. */
    public int[] Ranks()
    {
        var ranks = new int[Codes.Count];
        int rank = 0;
        for (int i = 0; i < ranks.Length; i++) ranks[i] = rank += Codes[i] != Leaf ? 1 : 0;
        return ranks;
    }

    /**
     * Descends to the leaf holding a point, as the runtime does.
     * @param min Box minimum of the tree.
     * @param max Box maximum of the tree.
     * @param point Point to find; both box ends are inclusive.
     * @param ranks Optional rank table from Ranks.
     * @return The node index and leaf number, or null outside the box.
     */
    public (int Node, int Leaf)? Find(Vector3 min, Vector3 max, Vector3 point, int[]? ranks = null)
    {
        if (Codes.Count == 0 || point.X < min.X || point.Y < min.Y || point.Z < min.Z
            || point.X > max.X || point.Y > max.Y || point.Z > max.Z) return null;
        ranks ??= Ranks();
        int node = 0;
        while (true)
        {
            int axis = Codes[node];
            if (axis == Leaf) return (node, node - ranks[node]);
            float split = node < Splits.Count ? Splits[node] : (Get(min, axis) + Get(max, axis)) * 0.5f;
            int right = ranks[node] << 1;
            if (Get(point, axis) < split) { max = Set(max, axis, split); node = right - 1; }
            else { min = Set(min, axis, split); node = right; }
        }
    }

    internal static float Get(Vector3 value, int axis) => axis == 0 ? value.X : axis == 1 ? value.Y : value.Z;
    internal static Vector3 Set(Vector3 value, int axis, float v) =>
        axis == 0 ? value with { X = v } : axis == 1 ? value with { Y = v } : value with { Z = v };
}

/** A tile; leaf tiles carry a view tree and cell map, hierarchy tiles only cells and portals. */
public sealed class TomeTile
{
    public const uint LeafFlag = 1, EmptyFlag = 2;
    /** Cell-map entry for a point in no cell. */
    public const int NoCell = -1;
    public Vector3 Min { get; set; }
    public Vector3 Max { get; set; }
    public uint Flags { get; set; }
    public float PortalExpand { get; set; }
    public List<TomeCell> Cells { get; init; } = [];
    public TomeKdTree? ViewTree { get; set; }
    /** One entry per view-tree leaf: a cell, NoCell, or BspReference of a node. */
    public List<int> CellMap { get; init; } = [];
    public List<TomeBspNode> Bsp { get; init; } = [];
    public List<Vector4> Planes { get; init; } = [];
    /** A stored value that differs from its derivation; null means the writer derives it. */
    public int? ClusterCountOverride { get; set; }
    public int? MapWidthOverride { get; set; }

    public bool IsLeaf => (Flags & LeafFlag) != 0;
    public static int BspReference(int node) => -2 - node;
    public static int BspNodeOf(int entry) => -2 - entry;

    /** Every cell a cell-map entry names, in the runtime's collection order; a point takes the first. */
    public List<int> CellsOf(int entry)
    {
        if (entry == NoCell) return [];
        if (entry >= 0) return [entry];
        var cells = new List<int>();
        var stack = new Stack<int>();
        stack.Push(BspNodeOf(entry));
        while (stack.Count > 0)
        {
            int index = stack.Pop();
            if ((uint)index >= Bsp.Count) throw new InvalidDataException("A BSP reference leaves its node list.");
            var node = Bsp[index];
            if (node.BackIsLeaf) { if (node.Back != TomeBspNode.NoCell) cells.Add(node.Back); }
            else stack.Push(node.Back);
            if (node.FrontIsLeaf) { if (node.Front != TomeBspNode.NoCell) cells.Add(node.Front); }
            else stack.Push(node.Front);
        }
        return cells;
    }

    /** Leaf tiles count their distinct cell clusters; hierarchy tiles store zero. */
    public int DerivedClusterCount() => IsLeaf ? Cells.Select(cell => cell.ClusterIndex).Distinct().Count() : 0;

    /** One plus the bit length of the largest direct cell, BSP leaf cell or BSP node count. */
    public int DerivedMapWidth()
    {
        if (ViewTree is null) return 0;
        int top = Math.Max(Bsp.Count, CellMap.Where(entry => entry >= 0).DefaultIfEmpty(0).Max());
        foreach (var node in Bsp)
        {
            if (node.FrontIsLeaf && node.Front != TomeBspNode.NoCell) top = Math.Max(top, node.Front);
            if (node.BackIsLeaf && node.Back != TomeBspNode.NoCell) top = Math.Max(top, node.Back);
        }
        return 1 + TomeBits.Length((uint)top);
    }

    public Vector3 GridToWorld(ushort x, ushort y, ushort z) =>
        Min + new Vector3(x, y, z) * ((Max - Min) * (1f / 65535f));
}

public sealed class TomeCluster
{
    public List<TomePortal> Portals { get; init; } = [];
    public TomeGridBox Bounds { get; set; }
}

/** A run of consecutive ids: Value, Value + 1, up to Count ids. */
public readonly record struct TomeRun(uint Value, uint Count);

public readonly record struct TomeObjectBounds(Vector3 Min, Vector3 Max);

/** Umbra tome format 18; every byte the writer emits is derived from these fields. */
public sealed class Tome
{
    public const int Version = 18;
    public const ushort Magic = 0xD600;
    public float LodBaseDistance { get; set; }
    public uint Flags { get; set; }
    public Vector3 Min { get; set; }
    public Vector3 Max { get; set; }
    public TomeKdTree TileTree { get; set; } = new();
    public List<TomeObjectBounds> Objects { get; init; } = [];
    /** Per-object user ids; null when the tome stores one id per object instead. */
    public List<List<uint>>? UserIdGroups { get; set; }
    public List<uint>? UserIds { get; set; }
    public List<TomeRun> ObjectLists { get; init; } = [];
    public List<TomeRun> ClusterLists { get; init; } = [];
    public List<uint> GateIds { get; init; } = [];
    public List<Vector3> GateVertices { get; init; } = [];
    public List<int> GateIndices { get; init; } = [];
    public List<TomeCluster> Clusters { get; init; } = [];
    /** One per tile-tree node; slots without a tile hold -1. */
    public List<float> LodLevels { get; init; } = [];
    /** One per tile-tree node; the slot number is the node index. */
    public List<TomeTile?> Tiles { get; init; } = [];
    public string ComputationString { get; set; } = "";
    /** Stored values that differ from their derivation; null means the writer derives them. */
    public (int Value, int Count)? ObjectListWidthOverride { get; set; }
    public (int Value, int Count)? ClusterListWidthOverride { get; set; }
    public int? LeafTileCountOverride { get; set; }
    public int? SlotPathBitsOverride { get; set; }

    /** Global cell number of each slot's first cell; the last entry is the total. */
    public int[] CellStarts()
    {
        var starts = new int[Tiles.Count + 1];
        for (int i = 0; i < Tiles.Count; i++) starts[i + 1] = starts[i] + (Tiles[i]?.Cells.Count ?? 0);
        return starts;
    }

    /**
     * Finds the cell holding a point, as the runtime does.
     * @param point World position.
     * @return The slot and tile-local cell; Slot is -1 outside the tome and Cell -1 in no cell.
     */
    public (int Slot, int Cell) Locate(Vector3 point)
    {
        var leaf = TileTree.Find(Min, Max, point);
        if (leaf is null) return (-1, -1);
        int slot = leaf.Value.Node;
        var tile = Tiles[slot];
        if (tile?.ViewTree is null || tile.CellMap.Count == 0) return (slot, -1);
        var view = tile.ViewTree.Find(tile.Min, tile.Max, point);
        if (view is null) return (slot, -1);
        var cells = tile.CellsOf(tile.CellMap[view.Value.Leaf]);
        return (slot, cells.Count == 0 ? -1 : cells[0]);
    }

    /** The cluster the runtime reports for a point, or -1. */
    public int LocateCluster(Vector3 point)
    {
        var (slot, cell) = Locate(point);
        if (cell < 0) return -1;
        var tile = Tiles[slot]!;
        int count = tile.ClusterCountOverride ?? tile.DerivedClusterCount();
        return count == 0 ? -1 : (int)tile.Cells[cell].ClusterIndex;
    }

    public Vector3 GridToWorld(ushort x, ushort y, ushort z) =>
        Min + new Vector3(x, y, z) * ((Max - Min) * (1f / 65535f));
}
