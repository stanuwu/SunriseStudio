using System.Globalization;
using System.Numerics;

namespace SunriseStudio.Formats;

/**
 * Bakes a new tome over a whole region: an open tome of equal leaf tiles, each one cell and one
 * cluster joined by portals to every neighbour, then the occluders carved in by TomeEditor.
 */
public static class TomeBaker
{
    // Installed tomes use a 24 m LOD base distance; a leaf's level is the cube root of its volume over it.
    private const float LodBaseDistance = 24;
    /** A leaf tile's edge in voxels; a power of two keeps every middle split on the voxel grid. */
    public const int TileVoxels = 64;
    // The client reads a tile-tree split for every node and never derives one; a leaf stores this.
    private const float LeafSplit = float.MaxValue;

    /**
     * The tome for a region.
     * @param min The region's minimum; the tome grows from it in whole tiles.
     * @param max The region's maximum.
     * @param voxel The smallest hole in metres; the editor voxelises at this size.
     * @param occluders Closed meshes, three world positions per triangle.
     */
    public static (Tome Tome, TomeEditReport Report) Bake(Vector3 min, Vector3 max, float voxel, IReadOnlyList<IReadOnlyList<Vector3>> occluders,
        IReadOnlyList<(Vector3 Min, Vector3 Max)>? rooms = null)
    {
        var tome = Open(min, max, voxel);
        var report = TomeEditor.Apply(tome, new TomeOcclusionEdit { Added = occluders.ToList() });
        if (rooms is { Count: > 0 }) Rooms(tome, rooms);
        return (tome, report);
    }

    /**
     * Gives each room box one cluster: every cell whose centre lies in the box joins it. Cluster
     * portals are then rebuilt from the cell portals that cross a cluster boundary.
     */
    private static void Rooms(Tome tome, IReadOnlyList<(Vector3 Min, Vector3 Max)> rooms)
    {
        var first = tome.Clusters.Count;
        foreach (var (lo, hi) in rooms) tome.Clusters.Add(new TomeCluster { Bounds = TomeBox(tome, lo, hi) });
        Vector3 Center(TomeTile tile, TomeGridBox box) =>
            (tile.GridToWorld(box.MinX, box.MinY, box.MinZ) + tile.GridToWorld(box.MaxX, box.MaxY, box.MaxZ)) * 0.5f;
        foreach (var tile in tome.Tiles)
            if (tile is { IsLeaf: true })
                foreach (var cell in tile.Cells)
                {
                    var center = Center(tile, cell.Bounds);
                    int room = Enumerable.Range(0, rooms.Count).FirstOrDefault(i => Vector3.Clamp(center, rooms[i].Min, rooms[i].Max) == center, -1);
                    if (room >= 0) cell.ClusterIndex = (uint)(first + room);
                }
        foreach (var cluster in tome.Clusters) cluster.Portals.Clear();
        for (int slot = 0; slot < tome.Tiles.Count; slot++)
            if (tome.Tiles[slot] is { IsLeaf: true } tile)
                foreach (var cell in tile.Cells)
                    foreach (var portal in cell.Portals.Where(p => !p.IsOutside && !p.IsHierarchy && !p.IsGate))
                    {
                        var target = tome.Tiles[(int)portal.Target];
                        if (target is not { IsLeaf: true } || portal.TargetCell >= target.Cells.Count) continue;
                        uint to = target.Cells[portal.TargetCell].ClusterIndex;
                        if (to == cell.ClusterIndex) continue;
                        var (min, max) = TomeEditor.PortalBox(tile, portal);
                        tome.Clusters[(int)cell.ClusterIndex].Portals.Add(ClusterPortal(tome, portal.Face, (int)to, min, max));
                    }
    }

    /** An open tome: every leaf tile is one cell and one cluster, and every shared face is a portal. */
    public static Tome Open(Vector3 min, Vector3 max, float voxel)
    {
        if (!(voxel > 0) || !(max.X > min.X && max.Y > min.Y && max.Z > min.Z)) throw new ArgumentException("The region and voxel size must be positive.");
        float tile = voxel * TileVoxels;
        int Count(float extent) => (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, MathF.Ceiling(extent / tile)));
        var tiles = new[] { Count(max.X - min.X), Count(max.Y - min.Y), Count(max.Z - min.Z) };
        var size = new Vector3(tiles[0], tiles[1], tiles[2]) * tile;
        var tome = new Tome
        {
            LodBaseDistance = LodBaseDistance, Min = min, Max = min + size, UserIds = [],
            ComputationString = string.Create(CultureInfo.InvariantCulture,
                $"3.3.6 T {tile:0.0} SO {tile / 5:0.0} SH {voxel:0.000} BF 90 F 4 OG 4.0000 CS {tile:0.0}")
        };

        // Breadth-first KD tree: each inner node halves the axis with the most tiles left.
        var boxes = new List<(int[] Lo, int[] Hi)> { (new[] { 0, 0, 0 }, tiles.ToArray()) };
        var leafOf = new Dictionary<(int, int, int), int>();
        for (int node = 0; node < boxes.Count; node++)
        {
            var (lo, hi) = boxes[node];
            int axis = Enumerable.Range(0, 3).MaxBy(a => hi[a] - lo[a]);
            if (hi[axis] - lo[axis] == 1)
            {
                tome.TileTree.Codes.Add(TomeKdTree.Leaf);
                tome.TileTree.Splits.Add(LeafSplit);
                leafOf[(lo[0], lo[1], lo[2])] = node;
                continue;
            }
            tome.TileTree.Codes.Add((byte)axis);
            int middle = (lo[axis] + hi[axis]) / 2;
            tome.TileTree.Splits.Add(TomeKdTree.Get(tome.Min, axis) + middle * tile);
            var leftHi = hi.ToArray(); leftHi[axis] = middle;
            var rightLo = lo.ToArray(); rightLo[axis] = middle;
            boxes.Add((lo, leftHi));
            boxes.Add((rightLo, hi));
        }

        Vector3 World(int[] grid) => tome.Min + new Vector3(grid[0], grid[1], grid[2]) * tile;
        int cluster = 0;
        var clusterOf = new Dictionary<int, int>();
        for (int node = 0; node < boxes.Count; node++)
        {
            if (tome.TileTree.Codes[node] != TomeKdTree.Leaf) { tome.Tiles.Add(null); tome.LodLevels.Add(-1); continue; }
            var (lo, hi) = boxes[node];
            var leaf = new TomeTile
            {
                Min = World(lo), Max = World(hi), Flags = TomeTile.LeafFlag,
                ViewTree = new TomeKdTree { Codes = [TomeKdTree.Leaf] }, CellMap = [0]
            };
            leaf.Cells.Add(new TomeCell { ClusterIndex = (uint)cluster, Bounds = new TomeGridBox(0, 0, 0, 65535, 65535, 65535) });
            tome.Tiles.Add(leaf);
            var extent = leaf.Max - leaf.Min;
            tome.LodLevels.Add(MathF.Cbrt(extent.X * extent.Y * extent.Z) / LodBaseDistance);
            clusterOf[node] = cluster++;
            tome.Clusters.Add(new TomeCluster { Bounds = TomeBox(tome, leaf.Min, leaf.Max) });
        }

        // Each face between two leaf tiles is a portal both ways, for the cells and for the clusters.
        foreach (var ((x, y, z), node) in leafOf)
            for (int axis = 0; axis < 3; axis++)
            {
                var at = new[] { x, y, z };
                at[axis]++;
                if (at[axis] >= tiles[axis] || !leafOf.TryGetValue((at[0], at[1], at[2]), out int next)) continue;
                var a = tome.Tiles[node]!;
                var b = tome.Tiles[next]!;
                var faceMin = TomeKdTree.Set(a.Min, axis, TomeKdTree.Get(a.Max, axis));
                a.Cells[0].Portals.Add(TomeEditor.Make(a, axis * 2 + 1, (uint)next, 0, faceMin, a.Max, hierarchy: false));
                b.Cells[0].Portals.Add(TomeEditor.Make(b, axis * 2, (uint)node, 0, b.Min, TomeKdTree.Set(b.Max, axis, TomeKdTree.Get(b.Min, axis)), hierarchy: false));
                tome.Clusters[clusterOf[node]].Portals.Add(ClusterPortal(tome, axis * 2 + 1, clusterOf[next], faceMin, a.Max));
                tome.Clusters[clusterOf[next]].Portals.Add(ClusterPortal(tome, axis * 2, clusterOf[node], b.Min,
                    TomeKdTree.Set(b.Max, axis, TomeKdTree.Get(b.Min, axis))));
            }
        return tome;
    }

    /**
     * An open tome whose leaf tiles shrink where `refine` asks; each leaf is one cell and one cluster.
     * @param min The region's minimum; the tome grows from it in whole fine tiles.
     * @param fine The smallest leaf edge in metres.
     * @param coarse The largest leaf edge; a larger node always splits.
     * @param refine True when a node box must split further, down to `fine`.
     */
    public static Tome Partition(Vector3 min, Vector3 max, float fine, float coarse, Func<Vector3, Vector3, bool> refine)
    {
        if (!(fine > 0) || !(coarse >= fine) || !(max.X > min.X && max.Y > min.Y && max.Z > min.Z))
            throw new ArgumentException("The region, the fine size and the coarse size must be positive.");
        int Count(float extent) => (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, MathF.Ceiling(extent / fine)));
        var units = new[] { Count(max.X - min.X), Count(max.Y - min.Y), Count(max.Z - min.Z) };
        var tome = new Tome
        {
            LodBaseDistance = LodBaseDistance, Min = min, Max = min + new Vector3(units[0], units[1], units[2]) * fine, UserIds = [],
            ComputationString = string.Create(CultureInfo.InvariantCulture,
                $"3.3.6 T {fine:0.0} SO {fine / 5:0.0} SH {fine / TileVoxels:0.000} BF 90 F 4 OG 4.0000 CS {fine:0.0}")
        };
        Vector3 World(int[] grid) => tome.Min + new Vector3(grid[0], grid[1], grid[2]) * fine;

        // Breadth-first KD tree: a node over the coarse size splits its longest axis; one that needs
        // refining splits its longer horizontal axis down to the fine size.
        var boxes = new List<(int[] Lo, int[] Hi)> { (new[] { 0, 0, 0 }, units.ToArray()) };
        var leaves = new List<int>();
        for (int node = 0; node < boxes.Count; node++)
        {
            var (lo, hi) = boxes[node];
            int axis = Enumerable.Range(0, 3).MaxBy(a => hi[a] - lo[a]);
            bool split = (hi[axis] - lo[axis]) * fine > coarse;
            if (!split)
            {
                axis = hi[0] - lo[0] >= hi[1] - lo[1] ? 0 : 1;
                split = hi[axis] - lo[axis] > 1 && refine(World(lo), World(hi));
            }
            if (!split)
            {
                tome.TileTree.Codes.Add(TomeKdTree.Leaf);
                tome.TileTree.Splits.Add(LeafSplit);
                leaves.Add(node);
                continue;
            }
            tome.TileTree.Codes.Add((byte)axis);
            int middle = (lo[axis] + hi[axis]) / 2;
            tome.TileTree.Splits.Add(TomeKdTree.Get(tome.Min, axis) + middle * fine);
            var leftHi = hi.ToArray(); leftHi[axis] = middle;
            var rightLo = lo.ToArray(); rightLo[axis] = middle;
            boxes.Add((lo, leftHi));
            boxes.Add((rightLo, hi));
        }
        if (leaves.Count > 1 << 20) throw new NotSupportedException($"The partition needs {leaves.Count} tiles; raise the fine size.");

        var clusterOf = new Dictionary<int, int>();
        for (int node = 0; node < boxes.Count; node++)
        {
            if (tome.TileTree.Codes[node] != TomeKdTree.Leaf) { tome.Tiles.Add(null); tome.LodLevels.Add(-1); continue; }
            var leaf = new TomeTile
            {
                Min = World(boxes[node].Lo), Max = World(boxes[node].Hi), Flags = TomeTile.LeafFlag,
                ViewTree = new TomeKdTree { Codes = [TomeKdTree.Leaf] }, CellMap = [0]
            };
            clusterOf[node] = tome.Clusters.Count;
            leaf.Cells.Add(new TomeCell { ClusterIndex = (uint)tome.Clusters.Count, Bounds = new TomeGridBox(0, 0, 0, 65535, 65535, 65535) });
            tome.Tiles.Add(leaf);
            var extent = leaf.Max - leaf.Min;
            tome.LodLevels.Add(MathF.Cbrt(extent.X * extent.Y * extent.Z) / LodBaseDistance);
            tome.Clusters.Add(new TomeCluster { Bounds = TomeBox(tome, leaf.Min, leaf.Max) });
        }
        Faces(tome, boxes, leaves, clusterOf, fine);
        return tome;
    }

    // Installed open tomes widen a leaf's portals by its shortest edge over this.
    private const float PortalExpandDivisor = 256;
    // The outside target, with the outside and internal flags, that installed tomes put on a boundary face.
    private const uint OutsideLink = 1u << 28 | 1u << 25 | TomePortal.OutsideTarget;

    /**
     * Gives an open tome what the installed open tome of map 0x80ED74AD has: an outside portal on
     * every box face, each leaf's portal expansion, and one object over the whole box seen from
     * every cell, since no installed tome has no objects.
     * @param userId The one object's user id; no id map key names it, so it draws nothing.
     */
    public static void Finish(Tome tome, uint userId = 1)
    {
        foreach (var tile in tome.Tiles.OfType<TomeTile>().Where(tile => tile.IsLeaf))
        {
            var extent = tile.Max - tile.Min;
            tile.PortalExpand = MathF.Min(extent.X, MathF.Min(extent.Y, extent.Z)) / PortalExpandDivisor;
            for (int face = 0; face < 6; face++)
            {
                int axis = face >> 1;
                bool high = (face & 1) == 1;
                float edge = TomeKdTree.Get(high ? tile.Max : tile.Min, axis);
                if (edge != TomeKdTree.Get(high ? tome.Max : tome.Min, axis)) continue;
                foreach (var cell in tile.Cells)
                    cell.Portals.Add(new(OutsideLink | (uint)face << 29, high ? 65535u : 0u, 65535u, 65535u));
            }
            foreach (var cell in tile.Cells) { cell.ObjectIndex = 0; cell.ObjectCount = 1; }
        }
        tome.Objects.Clear();
        tome.Objects.Add(new(tome.Min, tome.Max));
        tome.UserIds = null;
        tome.UserIdGroups = [[userId]];
        tome.ObjectLists.Clear();
        tome.ObjectLists.Add(new(0, 1));
    }

    /** Adds a portal both ways, for cells and clusters, on every face rectangle two leaves share. */
    private static void Faces(Tome tome, List<(int[] Lo, int[] Hi)> boxes, List<int> leaves, Dictionary<int, int> clusterOf, float fine)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            int xa = (1 << axis) & 3, ya = (1 << xa) & 3;
            // Leaves keyed by the grid plane of their low face on this axis.
            var byLow = leaves.GroupBy(node => boxes[node].Lo[axis]).ToDictionary(group => group.Key, group => group.ToList());
            foreach (int node in leaves)
            {
                var (lo, hi) = boxes[node];
                if (!byLow.TryGetValue(hi[axis], out var next)) continue;
                foreach (int other in next)
                {
                    var (olo, ohi) = boxes[other];
                    int u0 = Math.Max(lo[xa], olo[xa]), u1 = Math.Min(hi[xa], ohi[xa]);
                    int v0 = Math.Max(lo[ya], olo[ya]), v1 = Math.Min(hi[ya], ohi[ya]);
                    if (u0 >= u1 || v0 >= v1) continue;
                    var at = new int[3]; var to = new int[3];
                    at[axis] = to[axis] = hi[axis];
                    at[xa] = u0; to[xa] = u1; at[ya] = v0; to[ya] = v1;
                    var faceMin = tome.Min + new Vector3(at[0], at[1], at[2]) * fine;
                    var faceMax = tome.Min + new Vector3(to[0], to[1], to[2]) * fine;
                    var a = tome.Tiles[node]!;
                    var b = tome.Tiles[other]!;
                    a.Cells[0].Portals.Add(TomeEditor.Make(a, axis * 2 + 1, (uint)other, 0, faceMin, faceMax, hierarchy: false));
                    b.Cells[0].Portals.Add(TomeEditor.Make(b, axis * 2, (uint)node, 0, faceMin, faceMax, hierarchy: false));
                    tome.Clusters[clusterOf[node]].Portals.Add(ClusterPortal(tome, axis * 2 + 1, clusterOf[other], faceMin, faceMax));
                    tome.Clusters[clusterOf[other]].Portals.Add(ClusterPortal(tome, axis * 2, clusterOf[node], faceMin, faceMax));
                }
            }
        }
    }

    /** A cluster portal in the tome grid: target cluster, cell 0xFFFF, plane on the face axis. */
    private static TomePortal ClusterPortal(Tome tome, int face, int target, Vector3 min, Vector3 max)
    {
        int axis = face >> 1, xa = (1 << axis) & 3, ya = (1 << xa) & 3;
        var g0 = (min - tome.Min) / (tome.Max - tome.Min) * 65535f;
        var g1 = (max - tome.Min) / (tome.Max - tome.Min) * 65535f;
        ushort U(float v, bool up) => (ushort)Math.Clamp(up ? MathF.Ceiling(v) : MathF.Floor(v), 0, 65535);
        return TomePortal.Plain(face, (uint)target, 0xFFFF, U(TomeKdTree.Get(g0, axis), false),
            U(TomeKdTree.Get(g0, xa), false), U(TomeKdTree.Get(g1, xa), true), U(TomeKdTree.Get(g0, ya), false), U(TomeKdTree.Get(g1, ya), true));
    }

    private static TomeGridBox TomeBox(Tome tome, Vector3 min, Vector3 max)
    {
        var g0 = (min - tome.Min) / (tome.Max - tome.Min) * 65535f;
        var g1 = (max - tome.Min) / (tome.Max - tome.Min) * 65535f;
        ushort D(float v) => (ushort)Math.Clamp(MathF.Floor(v), 0, 65535);
        ushort Up(float v) => (ushort)Math.Clamp(MathF.Ceiling(v), 0, 65535);
        return new(D(g0.X), D(g0.Y), D(g0.Z), Up(g1.X), Up(g1.Y), Up(g1.Z));
    }
}
