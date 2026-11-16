using System.Numerics;

namespace SunriseStudio.Formats;

public static partial class TomeEditor
{
    private const int Solid = -1, Pending = -2, Outside = -3;

    /** The original tome as the edit found it, and the lookups every step shares. */
    private sealed class EditContext
    {
        private readonly Dictionary<int, int[]> ranks = [];
        private readonly Dictionary<(int Slot, int Cell), List<TomePortal>> originalPortals = [];
        private readonly Dictionary<(uint Slot, int Cell), HashSet<(uint, int)>> copyTargets = [];

        internal EditContext(Tome tome, Grid grid, TileTreeIndex tree)
        {
            (Tome, Grid, Tree) = (tome, grid, tree);
            for (int slot = 0; slot < tome.Tiles.Count; slot++)
            {
                if (tome.Tiles[slot] is not { } tile) continue;
                foreach (var cell in tile.Cells)
                    foreach (var plain in cell.Portals.Where(p => !p.IsHierarchy && !p.IsGate && !p.IsOutside))
                        foreach (var copy in cell.Portals.Where(p => p.IsHierarchy && !p.IsGate && p.Face == plain.Face
                            && p.Z == plain.Z && p.XRange == plain.XRange && p.YRange == plain.YRange))
                        {
                            var key = (plain.Target, plain.TargetCell);
                            if (!copyTargets.TryGetValue(key, out var set)) copyTargets[key] = set = [];
                            set.Add((copy.Target, copy.TargetCell));
                        }
            }
        }

        internal Tome Tome { get; }
        internal Grid Grid { get; }
        internal TileTreeIndex Tree { get; }
        internal Dictionary<int, TileWork> Works { get; } = [];

        /** Keeps a copy of a tile's portals before any step changes them. */
        internal void Snapshot(int slot)
        {
            var tile = Tome.Tiles[slot]!;
            for (int c = 0; c < tile.Cells.Count; c++) originalPortals[(slot, c)] = [.. tile.Cells[c].Portals];
        }

        /** The leaf slot holding a voxel, or -1 outside the tome. */
        internal int SlotAt(int x, int y, int z) => Tome.TileTree.Find(Tome.Min, Tome.Max, Grid.Center(x, y, z)) is { } leaf ? leaf.Node : -1;

        /** The original first cell at a voxel centre in a tile, or Solid. */
        internal int OriginalCell(int slot, int x, int y, int z)
        {
            int entry = OriginalEntry(slot, x, y, z);
            if (entry == TomeTile.NoCell) return Solid;
            var cells = Tome.Tiles[slot]!.CellsOf(entry);
            return cells.Count == 0 ? Solid : cells[0];
        }

        /** The original cell-map entry at a voxel centre: a cell, NoCell, or a BSP reference naming a cell group. */
        internal int OriginalEntry(int slot, int x, int y, int z)
        {
            var tile = Tome.Tiles[slot];
            if (tile?.ViewTree is null || tile.CellMap.Count == 0) return TomeTile.NoCell;
            if (!ranks.TryGetValue(slot, out var r)) ranks[slot] = r = tile.ViewTree.Ranks();
            var leaf = tile.ViewTree.Find(tile.Min, tile.Max, Grid.Center(x, y, z), r);
            return leaf is null ? TomeTile.NoCell : tile.CellMap[leaf.Value.Leaf];
        }

        /** Whether the original tome let a ray pass from one cell into another at a point on a face. */
        internal bool HadPortal(int fromSlot, int fromCell, int toSlot, int toCell, int face, Vector3 point)
        {
            if (!originalPortals.TryGetValue((fromSlot, fromCell), out var portals)) return false;
            var tile = Tome.Tiles[fromSlot]!;
            var g = ToGrid(tile, point);
            int axis = face >> 1, xa = (1 << axis) & 3, ya = (1 << xa) & 3;
            float tolerance = Grid.Size * 0.5f / TomeKdTree.Get(tile.Max - tile.Min, axis) * 65535f;
            foreach (var p in portals)
            {
                if (p.IsGate || p.IsOutside || p.IsHierarchy || p.Face != face || p.Target != (uint)toSlot || p.TargetCell != toCell) continue;
                if (MathF.Abs(p.Z - TomeKdTree.Get(g, axis)) > tolerance + 1) continue;
                float u = TomeKdTree.Get(g, xa), v = TomeKdTree.Get(g, ya);
                if (u >= p.MinX - 1 && u <= p.MaxX + 1 && v >= p.MinY - 1 && v <= p.MaxY + 1) return true;
            }
            return false;
        }

        /** Adds a plain portal, and for a portal into another leaf tile the hierarchy copies its target carries. */
        internal int AddPortal(int fromSlot, TomeCell from, int toSlot, int toCell, int toOriginalCell, int face, Vector3 min, Vector3 max)
        {
            var tile = Tome.Tiles[fromSlot]!;
            from.Portals.Add(Make(tile, face, (uint)toSlot, toCell, min, max, hierarchy: !Tome.Tiles[toSlot]!.IsLeaf));
            int added = 1;
            if (toSlot != fromSlot && copyTargets.TryGetValue(((uint)toSlot, toOriginalCell), out var targets))
                foreach (var (slot, cell) in targets) { from.Portals.Add(Make(tile, face, slot, cell, min, max, hierarchy: true)); added++; }
            return added;
        }

        /** Adds a cluster portal when the two clusters have none yet; returns how many were added. */
        internal int EnsureClusterPortal(int from, int to, int face, Vector3 min, Vector3 max)
        {
            if (from == to) return 0;
            var cluster = Tome.Clusters[from];
            if (cluster.Portals.Any(p => !p.IsGate && p.Target == (uint)to)) return 0;
            int axis = face >> 1, xa = (1 << axis) & 3, ya = (1 << xa) & 3;
            var g0 = TomeGrid(min);
            var g1 = TomeGrid(max);
            ushort z = (ushort)Math.Clamp(MathF.Round(TomeKdTree.Get(g0, axis)), 0, 65535);
            cluster.Portals.Add(TomePortal.Plain(face, (uint)to, 0xFFFF, z, FloorU(TomeKdTree.Get(g0, xa)), CeilU(TomeKdTree.Get(g1, xa)),
                FloorU(TomeKdTree.Get(g0, ya)), CeilU(TomeKdTree.Get(g1, ya))));
            return 1;
        }

        /** Grows a cluster box, in the tome grid, to enclose a world box. */
        internal void GrowCluster(int cluster, Vector3 min, Vector3 max)
        {
            var c = Tome.Clusters[cluster];
            var g0 = TomeGrid(min);
            var g1 = TomeGrid(max);
            var b = c.Bounds;
            c.Bounds = new(Math.Min(b.MinX, FloorU(g0.X)), Math.Min(b.MinY, FloorU(g0.Y)), Math.Min(b.MinZ, FloorU(g0.Z)),
                Math.Max(b.MaxX, CeilU(g1.X)), Math.Max(b.MaxY, CeilU(g1.Y)), Math.Max(b.MaxZ, CeilU(g1.Z)));
        }

        /** Gives a cell a new object slice holding its old objects plus every object overlapping a world box. */
        internal void AddObjects(TomeCell cell, Vector3 min, Vector3 max)
        {
            var ids = new SortedSet<uint>(Slice(cell));
            int before = ids.Count;
            for (int i = 0; i < Tome.Objects.Count; i++)
            {
                var o = Tome.Objects[i];
                if (o.Min.X <= max.X && min.X <= o.Max.X && o.Min.Y <= max.Y && min.Y <= o.Max.Y && o.Min.Z <= max.Z && min.Z <= o.Max.Z)
                    ids.Add((uint)i);
            }
            if (ids.Count == before) return;
            cell.ObjectIndex = (uint)Tome.ObjectLists.Count;
            cell.ObjectCount = (uint)ids.Count;
            uint? start = null, previous = null;
            foreach (uint id in ids)
            {
                if (previous is uint p && id == p + 1) { previous = id; continue; }
                if (start is uint s) Tome.ObjectLists.Add(new(s, previous!.Value - s + 1));
                start = previous = id;
            }
            if (start is uint last) Tome.ObjectLists.Add(new(last, previous!.Value - last + 1));
        }

        private IEnumerable<uint> Slice(TomeCell cell)
        {
            uint remaining = cell.ObjectCount;
            for (int i = (int)cell.ObjectIndex; remaining > 0; i++)
            {
                if (i >= Tome.ObjectLists.Count) throw new InvalidDataException("A cell's object slice leaves the object lists.");
                var run = Tome.ObjectLists[i];
                for (uint k = 0; k < run.Count && remaining > 0; k++, remaining--) yield return run.Value + k;
            }
        }

        private Vector3 TomeGrid(Vector3 world) => (world - Tome.Min) / ((Tome.Max - Tome.Min) * (1f / 65535f));
    }

    /** A portal from a tile's cell; z rounds to the nearest grid step and the rectangle rounds outward. */
    internal static TomePortal Make(TomeTile tile, int face, uint toSlot, int toCell, Vector3 min, Vector3 max, bool hierarchy)
    {
        int axis = face >> 1, xa = (1 << axis) & 3, ya = (1 << xa) & 3;
        var g0 = ToGrid(tile, min);
        var g1 = ToGrid(tile, max);
        ushort z = (ushort)Math.Clamp(MathF.Round(TomeKdTree.Get(g0, axis)), 0, 65535);
        return TomePortal.Plain(face, toSlot, toCell, z, FloorU(TomeKdTree.Get(g0, xa)), CeilU(TomeKdTree.Get(g1, xa)),
            FloorU(TomeKdTree.Get(g0, ya)), CeilU(TomeKdTree.Get(g1, ya)), hierarchy);
    }

    /** The world box a portal covers, flat on its axis. */
    internal static (Vector3 Min, Vector3 Max) PortalBox(TomeTile tile, TomePortal portal)
    {
        int axis = portal.Axis, xa = (1 << axis) & 3, ya = (1 << xa) & 3;
        var g0 = new float[3]; var g1 = new float[3];
        g0[axis] = g1[axis] = portal.Z;
        g0[xa] = portal.MinX; g1[xa] = portal.MaxX; g0[ya] = portal.MinY; g1[ya] = portal.MaxY;
        var scale = (tile.Max - tile.Min) / 65535f;
        return (tile.Min + new Vector3(g0[0], g0[1], g0[2]) * scale, tile.Min + new Vector3(g1[0], g1[1], g1[2]) * scale);
    }

    private static Vector3 ToGrid(TomeTile tile, Vector3 world) => (world - tile.Min) / (tile.Max - tile.Min) * 65535f;
    private static ushort FloorU(float v) => (ushort)Math.Clamp(MathF.Floor(v), 0, 65535);
    private static ushort CeilU(float v) => (ushort)Math.Clamp(MathF.Ceiling(v), 0, 65535);

    private static bool Overlap(Vector3 aMin, Vector3 aMax, Vector3 bMin, Vector3 bMax) =>
        aMin.X < bMax.X && bMin.X < aMax.X && aMin.Y < bMax.Y && bMin.Y < aMax.Y && aMin.Z < bMax.Z && bMin.Z < aMax.Z;

    /** Splits a face of voxels into rectangles of equal key, greedily by rows then columns. */
    private static IEnumerable<(int U0, int U1, int V0, int V1, T Key)> Rectangles<T>(T[] keys, int width, int height) where T : IEquatable<T>
    {
        var done = new bool[keys.Length];
        for (int v = 0; v < height; v++)
            for (int u = 0; u < width; u++)
            {
                if (done[v * width + u]) continue;
                var key = keys[v * width + u];
                int ue = u + 1, ve = v + 1;
                while (ue < width && !done[v * width + ue] && keys[v * width + ue].Equals(key)) ue++;
                while (ve < height && Enumerable.Range(u, ue - u).All(k => !done[ve * width + k] && keys[ve * width + k].Equals(key))) ve++;
                for (int j = v; j < ve; j++) for (int k = u; k < ue; k++) done[j * width + k] = true;
                yield return (u, ue, v, ve, key);
            }
    }
}
