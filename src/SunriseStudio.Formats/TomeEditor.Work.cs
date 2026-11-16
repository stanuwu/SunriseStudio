using System.Numerics;

namespace SunriseStudio.Formats;

public static partial class TomeEditor
{
    /** One leaf tile's part of the edit: voxel labels, opened and closed voxels, and the cells a closed box splits. */
    private sealed class TileWork
    {
        private readonly EditContext context;
        private int[] original = [], entries = [], label = [], center = [];
        private readonly Dictionary<(int Node, string Cells), int> groups = [];
        private bool[] opened = [], closed = [];

        internal TileWork(EditContext context, int slot, TomeVoxelBox box)
        {
            (this.context, Slot, Box) = (context, slot, box);
            Tile = context.Tome.Tiles[slot]!;
            if (Tile.ViewTree is null) throw new InvalidDataException($"Leaf tile {slot} has no view tree.");
            TileVoxels = context.Grid.TileBox(Tile);
            OldCellCount = Tile.Cells.Count;
        }

        internal int Slot { get; }
        internal TomeTile Tile { get; }
        internal TomeVoxelBox Box { get; }
        internal TomeVoxelBox TileVoxels { get; }
        internal int OldCellCount { get; }
        /** Voxels that opened, and the box of the voxels that closed. */
        internal TomeVoxelBox OpenedBox { get; private set; }
        internal TomeVoxelBox ClosedBox { get; private set; }
        /** Cells the closed box cuts, each split along the box's planes. */
        internal HashSet<int> Split { get; } = [];
        internal Dictionary<(int Cell, int X, int Y, int Z), int> Slabs { get; } = [];
        /** Each split cell's world box before the split. */
        internal Dictionary<int, (Vector3 Min, Vector3 Max)> SplitBounds { get; } = [];
        internal List<(TomeVoxelBox Box, int Label)> Centers { get; } = [];

        internal bool Contains(int x, int y, int z) => Box.Contains(x, y, z);
        internal int Index(int x, int y, int z) => ((z - Box.MinZ) * Box.SizeY + (y - Box.MinY)) * Box.SizeX + (x - Box.MinX);
        internal int Original(int x, int y, int z) => Contains(x, y, z) ? original[Index(x, y, z)] : context.OriginalCell(Slot, x, y, z);
        internal int Label(int x, int y, int z) => Contains(x, y, z) ? label[Index(x, y, z)] : context.OriginalCell(Slot, x, y, z);
        internal bool Opened(int x, int y, int z) => Contains(x, y, z) && opened[Index(x, y, z)];
        internal bool Closed(int x, int y, int z) => Contains(x, y, z) && closed[Index(x, y, z)];
        internal bool InCenter(int x, int y, int z) => !ClosedBox.IsEmpty && ClosedBox.Contains(x, y, z);

        /** The closed voxels as a grid over the region, for cutting portals. */
        internal TomeVoxelGrid ClosedGrid
        {
            get
            {
                if (closedGrid is not null) return closedGrid;
                closedGrid = new TomeVoxelGrid(Box);
                for (int z = Box.MinZ; z < Box.MaxZ; z++)
                    for (int y = Box.MinY; y < Box.MaxY; y++)
                        for (int x = Box.MinX; x < Box.MaxX; x++)
                            if (closed[Index(x, y, z)]) closedGrid[x, y, z] = true;
                return closedGrid;
            }
        }
        private TomeVoxelGrid? closedGrid;
        internal int CenterAt(int x, int y, int z) => InCenter(x, y, z) ? center[Index(x, y, z)] : Solid;

        /**
         * Labels every voxel of the region: removed-only voxels open, added voxels close, and opened
         * voxels that had no cell join the nearest open cell.
         * @return Voxels that opened and voxels that closed.
         */
        internal (long Opened, long Closed) Classify(TomeVoxelGrid removed, TomeVoxelGrid added, TomeVoxelGrid kept)
        {
            int count = (int)Box.Count;
            (original, entries, label, opened, closed) = (new int[count], new int[count], new int[count], new bool[count], new bool[count]);
            long openCount = 0, closeCount = 0;
            var queue = new Queue<(int, int, int)>();
            var openedBox = new TomeVoxelBox(0, 0, 0, 0, 0, 0);
            var closedBox = openedBox;
            for (int z = Box.MinZ; z < Box.MaxZ; z++)
                for (int y = Box.MinY; y < Box.MaxY; y++)
                    for (int x = Box.MinX; x < Box.MaxX; x++)
                    {
                        int at = Index(x, y, z), entry = context.OriginalEntry(Slot, x, y, z);
                        var cells = entry == TomeTile.NoCell ? [] : Tile.CellsOf(entry);
                        int cell = cells.Count == 0 ? Solid : cells[0];
                        (original[at], entries[at]) = (cell, entry);
                        closed[at] = added[x, y, z];
                        opened[at] = removed[x, y, z] && !kept[x, y, z] && !closed[at];
                        if (opened[at]) { openCount++; openedBox = openedBox.Union(new(x, y, z, x + 1, y + 1, z + 1)); }
                        if (closed[at]) { closeCount++; closedBox = closedBox.Union(new(x, y, z, x + 1, y + 1, z + 1)); }
                        label[at] = closed[at] ? Solid : cell != Solid ? cell : opened[at] ? Pending : Solid;
                        if (label[at] >= 0) queue.Enqueue((x, y, z));
                    }
            (OpenedBox, ClosedBox) = (openedBox, closedBox);
            Flood(queue, label, at => label[at] == Pending);
            if (label.Contains(Pending))
            {
                // Opened space with no open neighbour in the region takes the nearest label across solid voxels.
                var nearest = label.ToArray();
                for (int i = 0; i < count; i++) if (label[i] >= 0) queue.Enqueue(Coordinates(i));
                if (queue.Count == 0)
                {
                    if (OldCellCount == 0) throw new NotSupportedException($"Tile {Slot} has no cell for opened space.");
                    int first = Array.IndexOf(label, Pending);
                    nearest[first] = NearestCell(Coordinates(first));
                    queue.Enqueue(Coordinates(first));
                }
                Flood(queue, nearest, at => nearest[at] < 0);
                for (int i = 0; i < count; i++) if (label[i] == Pending) label[i] = nearest[i];
            }
            return (openCount, closeCount);
        }

        private int NearestCell((int X, int Y, int Z) voxel)
        {
            var point = context.Grid.Center(voxel.X, voxel.Y, voxel.Z);
            return Enumerable.Range(0, OldCellCount).MinBy(cell =>
            {
                var b = Tile.Cells[cell].Bounds;
                return Vector3.DistanceSquared(point, Vector3.Clamp(point, Tile.GridToWorld(b.MinX, b.MinY, b.MinZ), Tile.GridToWorld(b.MaxX, b.MaxY, b.MaxZ)));
            });
        }

        private void Flood(Queue<(int X, int Y, int Z)> queue, int[] target, Func<int, bool> admit)
        {
            while (queue.Count > 0)
            {
                var (x, y, z) = queue.Dequeue();
                int value = target[Index(x, y, z)];
                foreach (var (dx, dy, dz) in Steps)
                {
                    int nx = x + dx, ny = y + dy, nz = z + dz;
                    if (!Box.Contains(nx, ny, nz)) continue;
                    int at = Index(nx, ny, nz);
                    if (!admit(at)) continue;
                    target[at] = value;
                    queue.Enqueue((nx, ny, nz));
                }
            }
        }

        /** Finds the cells the closed box cuts and splits the open voxels inside it into boxes, one new cell each. */
        internal void PrepareSplit()
        {
            if (ClosedBox.IsEmpty) return;
            center = Enumerable.Repeat(Solid, (int)Box.Count).ToArray();
            var b = ClosedBox;
            // The cells to split are the ones the new solid lands in, so closed voxels count with their old cell.
            for (int z = b.MinZ; z < b.MaxZ; z++)
                for (int y = b.MinY; y < b.MaxY; y++)
                    for (int x = b.MinX; x < b.MaxX; x++)
                    {
                        int at = Index(x, y, z), cell = label[at] >= 0 ? label[at] : original[at];
                        if (cell >= 0) Split.Add(cell);
                        // A camera in a cell group starts from every cell in it, so new solid there splits them all.
                        if (entries[at] < TomeTile.NoCell) Split.UnionWith(Tile.CellsOf(entries[at]));
                    }
            foreach (int cell in Split) SplitBounds[cell] = (CellMin(Tile, cell), CellMax(Tile, cell));
            for (int z = b.MinZ; z < b.MaxZ; z++)
                for (int y = b.MinY; y < b.MaxY; y++)
                    for (int x = b.MinX; x < b.MaxX; x++)
                    {
                        int at = Index(x, y, z), cell = label[at];
                        if (cell < 0 || center[at] != Solid) continue;
                        int x1 = x + 1, y1 = y + 1, z1 = z + 1;
                        while (x1 < b.MaxX && Free(x1, y, z, cell)) x1++;
                        while (y1 < b.MaxY && Enumerable.Range(x, x1 - x).All(i => Free(i, y1, z, cell))) y1++;
                        while (z1 < b.MaxZ && Enumerable.Range(y, y1 - y).All(j => Enumerable.Range(x, x1 - x).All(i => Free(i, j, z1, cell)))) z1++;
                        var box = new TomeVoxelBox(x, y, z, x1, y1, z1);
                        int id = NewCell(Tile.Cells[cell], context.Grid.Corner(x, y, z), context.Grid.Corner(x1, y1, z1));
                        Centers.Add((box, cell));
                        for (int k = z; k < z1; k++) for (int j = y; j < y1; j++) for (int i = x; i < x1; i++) center[Index(i, j, k)] = id;
                    }
        }

        /**
         * Creates every slab a split cell's box reaches, before any portal refers to them. Slabs the cell
         * itself does not fill still join its pieces, so any two of them stay connected as the cell was.
         */
        internal void CreateSlabs()
        {
            foreach (int cell in Split.Order())
            {
                var (min, max) = SplitBounds[cell];
                foreach (var point in SlabPoints(min, max)) SlabCell(cell, point);
            }
            SlabsCreated = true;
        }

        private bool SlabsCreated { get; set; }

        /** One point in each slab a box reaches, the closed box itself excluded. */
        private IEnumerable<Vector3> SlabPoints(Vector3 min, Vector3 max)
        {
            var ranges = new List<(float Lo, float Hi)>[3];
            for (int axis = 0; axis < 3; axis++)
            {
                float lo = TomeKdTree.Get(min, axis), hi = TomeKdTree.Get(max, axis);
                float a = Plane(axis, low: true), b = Plane(axis, low: false);
                ranges[axis] = [];
                foreach (var (l, h) in new[] { (lo, Math.Min(hi, a)), (Math.Max(lo, a), Math.Min(hi, b)), (Math.Max(lo, b), hi) })
                    if (h > l) ranges[axis].Add((l, h));
            }
            foreach (var x in ranges[0])
                foreach (var y in ranges[1])
                    foreach (var z in ranges[2])
                    {
                        var point = new Vector3((x.Lo + x.Hi) * 0.5f, (y.Lo + y.Hi) * 0.5f, (z.Lo + z.Hi) * 0.5f);
                        if ((SlabIndex(point, 0), SlabIndex(point, 1), SlabIndex(point, 2)) != (1, 1, 1)) yield return point;
                    }
        }

        private bool Free(int x, int y, int z, int cell) => label[Index(x, y, z)] == cell && center[Index(x, y, z)] == Solid;

        /** A new cell copying a parent's cluster and objects, bounded by a world box inside the parent. */
        internal int NewCell(TomeCell parent, Vector3 min, Vector3 max)
        {
            if (Tile.Cells.Count >= MaximumTileCells) throw new NotSupportedException($"The occluder edit needs more than {MaximumTileCells} cells in tile {Slot}.");
            Tile.Cells.Add(new TomeCell
            {
                ObjectIndex = parent.ObjectIndex, ObjectCount = parent.ObjectCount, ClusterIndex = parent.ClusterIndex,
                ClusterCount = parent.ClusterCount, Bounds = GridBox(min, max)
            });
            return Tile.Cells.Count - 1;
        }

        /**
         * The cell a split cell's point now belongs to.
         * @param cell A cell in Split.
         * @param point A world point outside the closed box.
         * @return The slab cell on that side of the box's planes; the cell keeps its own number for its first slab.
         */
        internal int SlabCell(int cell, Vector3 point)
        {
            var (ix, iy, iz) = (SlabIndex(point, 0), SlabIndex(point, 1), SlabIndex(point, 2));
            if ((ix, iy, iz) == (1, 1, 1)) throw new InvalidOperationException("A slab point lies inside the closed box.");
            if (Slabs.TryGetValue((cell, ix, iy, iz), out int id)) return id;
            // Once every slab exists, a point past the cell's box belongs to the nearest one. A cell whose
            // stored box lies inside the closed box's planes got no slab and keeps its own number.
            if (SlabsCreated)
            {
                var own = Slabs.Where(pair => pair.Key.Cell == cell).ToList();
                return own.Count == 0 ? cell
                    : own.MinBy(pair => Math.Abs(pair.Key.X - ix) + Math.Abs(pair.Key.Y - iy) + Math.Abs(pair.Key.Z - iz)).Value;
            }
            var parent = Tile.Cells[cell];
            var (lo, hi) = SplitBounds[cell];
            for (int axis = 0; axis < 3; axis++)
            {
                int index = axis == 0 ? ix : axis == 1 ? iy : iz;
                float planeLow = Plane(axis, low: true), planeHigh = Plane(axis, low: false);
                if (index == 0) hi = TomeKdTree.Set(hi, axis, Math.Min(TomeKdTree.Get(hi, axis), planeLow));
                else if (index == 1) { lo = TomeKdTree.Set(lo, axis, Math.Max(TomeKdTree.Get(lo, axis), planeLow)); hi = TomeKdTree.Set(hi, axis, Math.Min(TomeKdTree.Get(hi, axis), planeHigh)); }
                else lo = TomeKdTree.Set(lo, axis, Math.Max(TomeKdTree.Get(lo, axis), planeHigh));
            }
            hi = Vector3.Max(lo, hi);
            id = Slabs.Keys.Any(key => key.Cell == cell) ? NewCell(parent, lo, hi) : cell;
            if (id == cell) parent.Bounds = GridBox(lo, hi);
            Slabs[(cell, ix, iy, iz)] = id;
            return id;
        }

        /** 0 below the closed box on an axis, 1 across it, 2 above it. */
        internal int SlabIndex(Vector3 point, int axis)
        {
            float v = TomeKdTree.Get(point, axis);
            return v < Plane(axis, low: true) ? 0 : v >= Plane(axis, low: false) ? 2 : 1;
        }

        internal float Plane(int axis, bool low) =>
            TomeKdTree.Get(context.Grid.Origin, axis) + (low ? ClosedBox.Min(axis) : ClosedBox.Max(axis)) * context.Grid.Size;

        /**
         * The cell a world point belongs to after the edit.
         * @param point World point inside this tile.
         * @param entry The original cell-map entry there.
         * @return A cell, NoCell, or a BSP reference left unchanged.
         */
        internal int NewEntry(Vector3 point, int entry)
        {
            var v = context.Grid.Box(point, point);
            int x = v.MinX, y = v.MinY, z = v.MinZ;
            if (Contains(x, y, z))
            {
                if (Closed(x, y, z)) return TomeTile.NoCell;
                if (InCenter(x, y, z)) return CenterAt(x, y, z) is var c && c >= 0 ? c : TomeTile.NoCell;
                if (entries[Index(x, y, z)] is var group && group < TomeTile.NoCell) return Group(group, point);
                int cell = Label(x, y, z);
                return cell < 0 ? TomeTile.NoCell : Split.Contains(cell) ? SlabCell(cell, point) : cell;
            }
            return entry >= 0 && Split.Contains(entry) ? SlabCell(entry, point) : entry < TomeTile.NoCell ? Group(entry, point) : entry;
        }

        /** Every cell a voxel's original entry names: the cells of its group, or its one cell. */
        internal IReadOnlyList<int> GroupCells(int x, int y, int z)
        {
            if (!Contains(x, y, z)) return context.OriginalCell(Slot, x, y, z) is var c && c >= 0 ? [c] : [];
            int at = Index(x, y, z);
            return entries[at] < TomeTile.NoCell ? Tile.CellsOf(entries[at]) : label[at] >= 0 ? [label[at]] : [];
        }

        /**
         * A cell group at a point outside the closed box, with each split cell replaced by its piece there.
         * Equal groups share one copy of the BSP nodes.
         */
        internal int Group(int entry, Vector3 point)
        {
            int node = TomeTile.BspNodeOf(entry);
            if (!Tile.CellsOf(entry).Any(Split.Contains)) return entry;
            var cells = new List<int>();
            int Map(int cell) => Split.Contains(cell) ? SlabCell(cell, point) : cell;
            Collect(node);
            string key = string.Join(",", cells);
            if (groups.TryGetValue((node, key), out int copy)) return TomeTile.BspReference(copy);
            copy = Clone(node, Map);
            groups[(node, key)] = copy;
            return TomeTile.BspReference(copy);

            void Collect(int at)
            {
                var source = Tile.Bsp[at];
                if (source.BackIsLeaf) { if (source.Back != TomeBspNode.NoCell) cells.Add(Map(source.Back)); } else Collect(source.Back);
                if (source.FrontIsLeaf) { if (source.Front != TomeBspNode.NoCell) cells.Add(Map(source.Front)); } else Collect(source.Front);
            }
        }

        private int Clone(int node, Func<int, int> map)
        {
            var source = Tile.Bsp[node];
            int index = Tile.Bsp.Count;
            Tile.Bsp.Add(source);
            ushort Child(bool leaf, ushort child) => leaf ? child == TomeBspNode.NoCell ? child : checked((ushort)map(child)) : checked((ushort)Clone(child, map));
            Tile.Bsp[index] = source with { Front = Child(source.FrontIsLeaf, source.Front), Back = Child(source.BackIsLeaf, source.Back) };
            return index;
        }

        /** The cell a voxel belongs to after the edit, for portal building. */
        internal int NewCellAt(int x, int y, int z)
        {
            if (InCenter(x, y, z)) return CenterAt(x, y, z);
            if (Closed(x, y, z)) return Solid;
            int cell = Label(x, y, z);
            return cell >= 0 && Split.Contains(cell) ? SlabCell(cell, context.Grid.Center(x, y, z)) : cell;
        }

        internal TomeGridBox GridBox(Vector3 min, Vector3 max)
        {
            var g0 = ToGrid(Tile, min);
            var g1 = ToGrid(Tile, max);
            return new(FloorU(g0.X), FloorU(g0.Y), FloorU(g0.Z), CeilU(g1.X), CeilU(g1.Y), CeilU(g1.Z));
        }

        private (int, int, int) Coordinates(int index) =>
            (Box.MinX + index % Box.SizeX, Box.MinY + index / Box.SizeX % Box.SizeY, Box.MinZ + index / (Box.SizeX * Box.SizeY));
    }

    private static readonly (int, int, int)[] Steps = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];
    private static Vector3 CellMin(TomeTile tile, int cell) { var b = tile.Cells[cell].Bounds; return tile.GridToWorld(b.MinX, b.MinY, b.MinZ); }
    private static Vector3 CellMax(TomeTile tile, int cell) { var b = tile.Cells[cell].Bounds; return tile.GridToWorld(b.MaxX, b.MaxY, b.MaxZ); }
}
