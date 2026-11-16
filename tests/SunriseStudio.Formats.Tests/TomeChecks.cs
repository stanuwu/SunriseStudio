using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Formats;
using SunriseStudio.Packages;

/** Tome bytes rebuild exactly from the model, and point lookup follows the stored trees. */
internal static class TomeChecks
{
    internal static void Run()
    {
        var tome = Sample();
        byte[] first = TomeWriter.Write(tome);
        var read = TomeReader.Read(first);
        byte[] second = TomeWriter.Write(read);
        Check(first.AsSpan().SequenceEqual(second), "A written tome did not rebuild byte for byte.");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(4)) == Crc32C(first.AsSpan(8)), "The tome CRC is not CRC-32C of bytes 8 onward.");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(8)) == first.Length && first.Length % 16 == 0, "The size field or block padding is wrong.");
        Check(read.Locate(new(1, 1, 1)) == (1, 0) && read.Locate(new(3, 1, 1)) == (2, 0), "A point did not land in its leaf tile.");
        Check(read.LocateCluster(new(3, 1, 1)) == 1 && read.Locate(new(9, 1, 1)).Slot == -1, "A cluster or outside lookup is wrong.");
        Check(read.Tiles[2]!.CellsOf(TomeTile.BspReference(0)).SequenceEqual([0]), "A BSP reference did not collect its cells.");
        Check(read.Tiles[0]!.Cells[0].Portals[0].IsHierarchy && read.Tiles[1]!.Cells[0].Portals[0].Target == 2, "Portal bits did not survive.");
        byte[] broken = first.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(broken.AsSpan(BinaryPrimitives.ReadInt32LittleEndian(first.AsSpan(0x88)) + 4), 5);
        Reject(() => TomeReader.Read(broken));
        Reject(() => TomeReader.Read(first.AsMemory(0, first.Length - 16)));
        tome.Tiles[1]!.MapWidthOverride = 1;
        tome.Tiles[1]!.CellMap[0] = 2;
        Reject(() => TomeWriter.Write(tome));
        Console.WriteLine($"PASS tome-synthetic bytes={first.Length} round-trip=exact crc=crc32c lookup=leaf,cluster,bsp refusals=checked");
    }

    /** A wall across a one-cell leaf tile splits its cell: no cell inside the wall, one cluster on both sides. */
    internal static void Edit()
    {
        var tome = Sample();
        tome.ComputationString = "3.3.6 T 32.0 SO 6.0 SH 0.125";
        var wall = Box(new(0.75f, 0, 0), new(1.0f, 2, 2));
        var report = TomeEditor.Apply(tome, new TomeOcclusionEdit { Added = [wall] });
        var bytes = TomeWriter.Write(tome);
        var read = TomeReader.Read(bytes);
        Check(TomeWriter.Write(read).AsSpan().SequenceEqual(bytes), "An edited tome did not rebuild byte for byte.");
        Check(report.CellsAdded > 0 && report.ClosedVoxels > 0, "The wall split no cell.");
        Check(read.Locate(new(0.875f, 1, 1)).Cell == -1, "A point inside the new wall still has a cell.");
        var left = read.Locate(new(0.3f, 1, 1));
        var right = read.Locate(new(1.6f, 1, 1));
        Check(left.Slot == 1 && right.Slot == 1 && left.Cell != right.Cell, "The wall left both sides in one cell.");
        Check(read.LocateCluster(new(0.3f, 1, 1)) == 0 && read.LocateCluster(new(1.6f, 1, 1)) == 0, "A split piece changed cluster.");
        Check(read.LocateCluster(new(3, 1, 1)) == 1, "A tile the edit does not touch changed.");
        var cube = Box(new(0.3f, 0.3f, 0.3f), new(1.2f, 1.2f, 1.2f));
        int Inside(List<Vector3> mesh)
        {
            var grid = new TomeVoxelGrid(new(0, 0, 0, 16, 16, 16));
            TomeVoxelizer.MarkInside(grid, mesh, Vector3.Zero, 0.125f);
            int count = 0;
            for (int z = 0; z < 16; z++) for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) if (grid[x, y, z]) count++;
            return count;
        }
        Check(Inside(cube) == 512 && Inside(cube[..^6]) == 0, "A closed box did not mark exactly its inside centres, or a box with a hole marked space.");
        // The right tile is one cell group beside a gate; a wall there splits the group's cell and keeps groups.
        var grouped = Sample();
        var groupWall = Box(new(3.0f, 0, 0), new(3.25f, 2, 2));
        TomeEditor.Apply(grouped, new TomeOcclusionEdit { Added = [groupWall] });
        var groupedRead = TomeReader.Read(TomeWriter.Write(grouped));
        var groupTile = groupedRead.Tiles[2]!;
        var (nearSlot, nearCell) = groupedRead.Locate(new(2.5f, 1, 1));
        var (farSlot, farCell) = groupedRead.Locate(new(3.6f, 1, 1));
        Check(groupedRead.Locate(new(3.125f, 1, 1)).Cell == -1 && nearSlot == 2 && farSlot == 2 && nearCell != farCell
            && groupTile.CellMap.Any(entry => entry < TomeTile.NoCell) && groupedRead.LocateCluster(new(3.6f, 1, 1)) == 1
            && groupedRead.Tiles[0]!.Cells[0].Portals.Count(portal => portal.IsGate) == 1,
            "A wall in a cell group did not split it, or lost the group or the gate.");
        var bare = Sample();
        Check(TomeEditor.BlockedShare(read, wall, []) >= 0.5 && TomeEditor.BlockedShare(bare, wall, []) < 0.5,
            "The blocking test did not tell the new wall from open space.");
        var opened = TomeReader.Read(bytes);
        opened.ComputationString = tome.ComputationString;
        var open = TomeEditor.Apply(opened, new TomeOcclusionEdit { Removed = [wall] });
        Check(open.OpenedVoxels > 0 && TomeReader.Read(TomeWriter.Write(opened)).Locate(new(0.875f, 1, 1)).Cell >= 0,
            "Removing the wall did not give its space a cell.");
        Console.WriteLine($"PASS tome-edit cells+={report.CellsAdded} portals+={report.PortalsAdded} closed={report.ClosedVoxels} reopened={open.OpenedVoxels}");
    }

    /**
     * A new tome over a closed box room: it rereads exactly, the room is its own cell and cluster,
     * its wall blocks, and a bake over the fixture bubble's bounds finishes in budget.
     */
    internal static void Baked()
    {
        var walls = new List<List<Vector3>>
        {
            Box(new(3.5f, 3.5f, 1.5f), new(12.5f, 12.5f, 2)), Box(new(3.5f, 3.5f, 6), new(12.5f, 12.5f, 6.5f)),
            Box(new(3.5f, 3.5f, 2), new(4, 12.5f, 6)), Box(new(12, 3.5f, 2), new(12.5f, 12.5f, 6)),
            Box(new(4, 3.5f, 2), new(12, 4, 6)), Box(new(4, 12, 2), new(12, 12.5f, 6))
        };
        var (tome, report) = TomeBaker.Bake(Vector3.Zero, new(16, 16, 8), 0.25f, walls, [(new(4, 4, 2), new(12, 12, 6))]);
        var bytes = TomeWriter.Write(tome);
        var read = TomeReader.Read(bytes);
        Check(TomeWriter.Write(read).AsSpan().SequenceEqual(bytes), "A baked tome did not rebuild byte for byte.");
        var inside = read.Locate(new(8, 8, 4));
        var outside = read.Locate(new(1, 1, 1));
        Check(inside.Cell >= 0 && outside.Cell >= 0 && inside != outside, "The room and the space outside it share a cell.");
        Check(read.Clusters.Count == 2 && read.LocateCluster(new(8, 8, 4)) == 1 && read.LocateCluster(new(1, 1, 1)) == 0,
            $"The room is not its own cluster: {read.Clusters.Count} clusters.");
        Check(read.Locate(new(8, 8, 1.75f)).Cell == -1 && TomeEditor.BlockedShare(read, walls[2], []) >= 0.5, "The room's floor or wall does not block.");
        // The fixture bubble: 32 by 32 by 32 m at the stock 0.5 m voxel, with its block and four profile blocks.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var blocks = new List<List<Vector3>> { Box(new(5, 3, 0), new(7, 5, 3)) };
        foreach (float x in new[] { -10f, -7, -4, -1 }) blocks.Add(Box(new(x, 9.25f, 0), new(x + 1.5f, 10.75f, 1.5f)));
        var (bubble, _) = TomeBaker.Bake(new(-16, -16, -8), new(16, 16, 24), 0.5f, blocks);
        var bubbleBytes = TomeWriter.Write(bubble);
        Check(TomeWriter.Write(TomeReader.Read(bubbleBytes)).AsSpan().SequenceEqual(bubbleBytes) && TomeEditor.BlockedShare(bubble, blocks[0], []) >= 0.5,
            "The bubble bake does not reread exactly or does not block its block.");
        Check(watch.Elapsed.TotalSeconds < 60, $"The bubble bake took {watch.Elapsed.TotalSeconds:F0} s, over its 60 s budget.");
        Console.WriteLine($"PASS tome-bake room=[cells={read.Tiles.Sum(t => t?.Cells.Count ?? 0)} clusters={read.Clusters.Count} closed={report.ClosedVoxels} bytes={bytes.Length}] "
            + $"bubble=[tiles={bubble.Tiles.Count(t => t != null)} cells={bubble.Tiles.Sum(t => t?.Cells.Count ?? 0)} bytes={bubbleBytes.Length} seconds={watch.Elapsed.TotalSeconds:F1}]");
    }

    /** A Leviathan slab opened, closed and moved keeps every cluster outside the edit and rebuilds exactly. */
    internal static void InstalledEdit(PackageCatalog catalog)
    {
        const uint leviathan = 0x80EDC2C2;
        if (!catalog.TryGetEntry(leviathan, out _)) { Console.WriteLine("SKIP tome-edit-installed tome=80EDC2C2 absent"); return; }
        byte[] original = catalog.ReadOriginal(leviathan);
        Vector3 min = new(-198.3271f, -1369.4146f, -20.4997f), max = new(-193.3271f, -1359.4146f, -19.9891f);
        var shift = new Vector3(0, 11, 0);
        var baseline = TomeReader.Read(original);
        var rng = new Random(2112);
        var points = Enumerable.Range(0, 4000).Select(_ => baseline.Min + new Vector3(rng.NextSingle(), rng.NextSingle(), rng.NextSingle()) * (baseline.Max - baseline.Min)).ToArray();
        foreach (var (name, removed, added) in new[] { ("open", true, false), ("close", false, true), ("move", true, true) })
        {
            var tome = TomeReader.Read(original);
            var edit = new TomeOcclusionEdit();
            if (removed) edit.Removed.Add(Box(min, max));
            if (added) edit.Added.Add(Box(min + shift, max + shift));
            var report = TomeEditor.Apply(tome, edit);
            var bytes = TomeWriter.Write(tome);
            var read = TomeReader.Read(bytes);
            Check(TomeWriter.Write(read).AsSpan().SequenceEqual(bytes), $"The {name} edit did not rebuild byte for byte.");
            Vector3 lo = Vector3.Min(min, min + shift) - new Vector3(0.5f), hi = Vector3.Max(max, max + shift) + new Vector3(0.5f);
            foreach (var p in points.Where(p => !(Vector3.Clamp(p, lo, hi) == p)))
                Check(read.LocateCluster(p) == baseline.LocateCluster(p), $"The {name} edit changed a cluster outside its region.");
            if (added) Check(read.Locate((min + max) * 0.5f + shift).Cell == -1, "The new solid kept a cell.");
            if (removed && !added) Check(read.LocateCluster((min + max) * 0.5f) >= 0, "The opened space has no cluster.");
            Check(read.Clusters.Count == baseline.Clusters.Count, "The edit added a cluster.");
            Console.WriteLine($"PASS tome-edit-installed mode={name} tiles={report.LeafTiles}+{report.HierarchyTiles} cells+={report.CellsAdded} portals+={report.PortalsAdded} opened={report.OpenedVoxels} closed={report.ClosedVoxels}");
        }
    }

    private static List<Vector3> Box(Vector3 lo, Vector3 hi)
    {
        var c = new Vector3[8];
        for (int i = 0; i < 8; i++) c[i] = new((i & 1) != 0 ? hi.X : lo.X, (i & 2) != 0 ? hi.Y : lo.Y, (i & 4) != 0 ? hi.Z : lo.Z);
        int[] faces = [0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3];
        return faces.Select(i => c[i]).ToList();
    }

    /** Every installed tome rebuilds byte for byte from its model. */
    internal static void Installed(PackageCatalog catalog)
    {
        int tomes = 0;
        foreach (var entry in catalog.Entries.Where(entry => entry.Type == 24))
        {
            byte[] data = catalog.ReadOriginal(entry.TagHash);
            if (!TomeReader.IsTome(data)) continue;
            byte[] rebuilt = TomeWriter.Write(TomeReader.Read(data));
            if (!rebuilt.AsSpan().SequenceEqual(data))
            {
                int at = 0;
                while (at < data.Length && at < rebuilt.Length && data[at] == rebuilt[at]) at++;
                throw new InvalidDataException($"Tome {entry.TagHash:X8} differs at 0x{at:X} after a rebuild.");
            }
            tomes++;
        }
        Check(tomes > 0, "No installed tome was found.");
        Console.WriteLine($"PASS tome-installed tomes={tomes} round-trip=exact");
    }

    /** Two leaf tiles under a split root with a hierarchy tile, one BSP-mapped leaf and a gate. */
    private static Tome Sample()
    {
        var root = new TomeKdTree { Codes = [0, 3, 3], Splits = [2, float.MaxValue, float.MaxValue] };
        TomeTile Leaf(Vector3 min, Vector3 max, uint cluster, int map, TomePortal portal) => new()
        {
            Min = min, Max = max, Flags = TomeTile.LeafFlag, PortalExpand = 0.125f,
            ViewTree = new() { Codes = [TomeKdTree.Leaf] }, CellMap = [map],
            Cells = [new() { ObjectIndex = cluster, ObjectCount = 1, ClusterIndex = cluster, Bounds = new(0, 0, 0, 65535, 65535, 65535), Portals = [portal] }]
        };
        var right = Leaf(new(2, 0, 0), new(4, 2, 2), 1, TomeTile.BspReference(0), TomePortal.Plain(0, 1, 0, 0, 0, 65535, 0, 65535));
        right.Bsp.Add(new(0, true, true, 0, TomeBspNode.NoCell));
        right.Planes.Add(new(1, 0, 0, -3));
        return new()
        {
            LodBaseDistance = 24, Min = Vector3.Zero, Max = new(4, 2, 2), TileTree = root,
            Objects = [new(new(0, 0, 0), new(1, 1, 1)), new(new(2, 0, 0), new(3, 1, 1))],
            UserIdGroups = [[7], [8, 9]],
            ObjectLists = [new(0, 1), new(1, 1)],
            ClusterLists = [new(0, 2)],
            GateIds = [42], GateVertices = [new(1.5f, 0, 0), new(2.5f, 2, 2)], GateIndices = [0],
            Clusters =
            [
                new() { Bounds = new(0, 0, 0, 32767, 65535, 65535), Portals = [TomePortal.Plain(1, 1, 0xFFFF, 0, 0, 0, 0, 0)] },
                new() { Bounds = new(32768, 0, 0, 65535, 65535, 65535), Portals = [TomePortal.Plain(0, 0, 0xFFFF, 0, 0, 0, 0, 0)] }
            ],
            LodLevels = [2.5f, -1, -1],
            Tiles =
            [
                new()
                {
                    Min = Vector3.Zero, Max = new(4, 2, 2),
                    Cells = [new() { ObjectIndex = 0, ObjectCount = 2, ClusterIndex = 0, ClusterCount = 2, Bounds = new(0, 0, 0, 65535, 65535, 65535),
                        Portals = [new(1u << 27 | 1u << 26, 0, 0u << 12 | 1, 0u << 12 | 2)] }]
                },
                Leaf(Vector3.Zero, new(2, 2, 2), 0, 0, TomePortal.Plain(1, 2, 0, 65535, 0, 65535, 0, 65535)),
                right
            ],
            ComputationString = "3.3.6 T 32.0 SO 6.0 SH 0.125"
        };
    }

    private static uint Crc32C(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? crc >> 1 ^ 0x82F63B78 : crc >> 1;
        }
        return ~crc;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Reject(Action run)
    {
        try { run(); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException) { return; }
        throw new InvalidOperationException("An invalid tome was accepted.");
    }
}
