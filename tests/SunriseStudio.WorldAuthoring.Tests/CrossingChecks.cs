using System.Numerics;
using System.Text.Json.Nodes;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.World;

/**
 * Walk-through crossings: the cell-table and id-map writers rebuild installed EDZ tags byte for
 * byte, the EDZ copy's Trostland to Widow's Walk connector follows the rule AddConnector writes, and
 * two added bubbles get a crossing whose switch zones never overlap.
 */
static class CrossingChecks
{
    private const string Destination = "crossbed";
    // Trostland, Widow's Walk and the Atrium in EDZ: rows 51, 54 and 52.
    private static readonly uint[] Kept = [0xB8459D59, 0xC0064AEF, 0xC0064AED];
    // Trostland's and Widow's Walk's id maps, and two installed maps of the shape VisibilityIdMap.Open writes.
    private const uint TownMap = 0x80F2B40B, WalkMap = 0x80F2B7E1;
    private static readonly uint[] Maps = [TownMap, WalkMap, 0x80F2B53A, 0x80ED74AD, 0x80F2A12B, 0x80F0A00F];
    // The connector row that joins Trostland and Widow's Walk is named by a spawn set both declare.
    private const uint WalkConnectorName = 0xD7BA9C15;

    public static void Run(string gameRoot)
    {
        using var catalog = PackageCatalog.Open(gameRoot);
        var schema = StudioRegistry.Index;
        RoundTrips(catalog, schema);
        uint scenario = WorldBuild.Named(catalog, "edz_freeroam:scenario_client");
        Transitions(catalog, WorldTemplate.MapRootOf(catalog, schema, scenario));
        string scratch = Path.Combine(Path.GetTempPath(), "sunrise-crossings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = StudioProject.Create(Path.Combine(scratch, "project"), "Crossings");
            WorldTemplate.Create(project.ContentDirectory, catalog, schema, scenario, Kept.ToHashSet(), Destination);
            var world = WorldFiles.Load(project.ContentDirectory, Destination);
            Installed(world, catalog);
            Added(project, catalog, schema);
            Edited(project, catalog, schema);
            Bakes(catalog);
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }

    /**
     * Edit mode on two original bubbles keeps both installed maps and tomes, appends the new
     * connector to each installed table, and switches each side deep in the other's corridor.
     */
    private static void Edited(StudioProject project, PackageCatalog catalog, RegistryIndex schema)
    {
        uint town = Renamed(51), walk = Renamed(54);
        var world = WorldFiles.Load(project.ContentDirectory, Destination);
        int maps = world.Files.Count(f => f.ClassId == VisibilityIdMap.Class);
        // A line across the Trostland to Widow's Walk corridor, Trostland on its left.
        var crossing = new BundleCrossing("town", "walk", [new(471.8f, 184.7f, 70), new(432.2f, 115.3f, 70)], 8);
        var hashes = new Dictionary<string, uint> { ["town"] = town, ["walk"] = walk };
        var bounds = new Dictionary<string, (Vector3, Vector3)> { ["town"] = (new(300, 0, 0), new(700, 300, 150)), ["walk"] = (new(400, 50, 0), new(500, 220, 150)) };
        var placed = WorldCrossingImport.Place(project, catalog, schema, Destination, [crossing], hashes, bounds, Matrix4x4.Identity, 0, mode: CrossingMode.Edit);
        world = WorldFiles.Load(project.ContentDirectory, Destination);
        WorldBuild.Check(world.Files.Count(f => f.ClassId == VisibilityIdMap.Class) == maps && placed.Bubbles.Values.All(b => b.Mode == CrossingMode.Edit && b.Files.Count == 0),
            "Edit mode wrote a visibility map.");
        foreach (var (map, bubble, point) in new[] { (TownMap, town, new Vector3(433, 175, 74)), (WalkMap, walk, new Vector3(466, 142, 74)) })
        {
            var stock = VisibilityIdMap.Read(catalog.Read(map));
            WorldBuild.Check(project.ContainsEdit(stock.ZoneCrossing) && !project.ContainsEdit(stock.FineTome), $"Map 0x{map:X8}'s table was not edited, or its tome was.");
            var before = ZoneTable.Read(catalog.Read(stock.ZoneCrossing));
            var after = ZoneTable.Read(project.ReadAsset(stock.ZoneCrossing, catalog));
            var tome = TomeReader.Read(catalog.Read(stock.FineTome));
            int cluster = tome.LocateCluster(point);
            WorldBuild.Check(after.Records.Count == before.Records.Count && after.Connectors.Count == before.Connectors.Count + 1
                && cluster >= 0 && after.Records[cluster].Kind == ZoneRecord.Switch && after.Records[cluster].Connector == before.Connectors.Count,
                $"Map 0x{map:X8}'s edited table does not switch to the new connector at {point}.");
            Console.WriteLine($"world_crossing_edit map=0x{map:X8} switching={placed.Bubbles[bubble].Switching} note=\"{placed.Bubbles[bubble].Note}\"");
        }
    }

    /** Uniform and Retail bakes write tomes that read back, with one record per cluster. */
    private static void Bakes(PackageCatalog catalog)
    {
        var bounds = new Dictionary<uint, (Vector3, Vector3)> { [1] = (new(0, 0, 0), new(240, 120, 40)), [2] = (new(240, 0, 0), new(480, 120, 40)) };
        var border = new CrossingBorder(1, 2, [new(240, 0, 0), new(240, 120, 0)], 8);
        foreach (var (mode, bake) in new (string, (Tome Tome, ZoneRecord[] Records, float Width))[]
            { ("uniform", WorldCrossingImport.BakeUniform(bounds, 1, [border])), ("retail", WorldCrossingImport.BakeRetail(catalog, bounds, 1, [border])) })
        {
            byte[] bytes = TomeWriter.Write(bake.Tome);
            var back = TomeReader.Read(bytes);
            WorldBuild.Check(back.Clusters.Count == bake.Records.Length && TomeWriter.Write(back).AsSpan().SequenceEqual(bytes),
                $"The {mode} tome does not read back with one record per cluster.");
            Console.WriteLine($"world_crossing_bake mode={mode} clusters={bake.Records.Length} switching={bake.Records.Count(r => r.Kind == ZoneRecord.Switch)} width={bake.Width} bytes={bytes.Length}");
        }
    }

    /** Every installed table and map these readers take writes back to its own bytes. */
    private static void RoundTrips(PackageCatalog catalog, RegistryIndex schema)
    {
        int tables = 0;
        foreach (uint tag in Maps)
        {
            byte[] bytes = catalog.Read(tag);
            var map = VisibilityIdMap.Read(bytes);
            WorldBuild.Check(map.Write(schema).AsSpan().SequenceEqual(bytes), $"Id map 0x{tag:X8} does not write back to its bytes.");
            foreach (uint table in new[] { map.ZoneCrossing, map.Tome1Cells }.Where(catalog.ContainsTag))
            {
                byte[] tableBytes = catalog.Read(table);
                WorldBuild.Check(ZoneTable.Read(tableBytes).Write(schema).AsSpan().SequenceEqual(tableBytes), $"Cell table 0x{table:X8} does not write back to its bytes.");
                tables++;
            }
        }
        Console.WriteLine($"world_crossing_round_trip maps={Maps.Length} tables={tables}");
    }

    /**
     * Trostland and Widow's Walk share one kind-1 row: both slice sets list it, both declare its name
     * as a spawn set, and each zone-crossing table has one connector naming it and the other's own row.
     */
    private static void Installed(WorldFiles world, PackageCatalog catalog)
    {
        uint town = Renamed(51), walk = Renamed(54);
        var joined = WorldCrossings.Connectors(world, town).Where(c => c.NameHash == WalkConnectorName).ToList();
        WorldBuild.Check(joined.Count == 1 && joined[0].Other == 54, "Trostland's slice set does not list the Widow's Walk connector once.");
        var row = joined[0];
        WorldBuild.Check(WorldCrossings.Connectors(world, walk).Any(c => c.Row == row.Row), "Widow's Walk does not list the connector.");
        WorldBuild.Check(WorldBubbles.SpawnSets(world, town).Contains(WalkConnectorName) && WorldBubbles.SpawnSets(world, walk).Contains(WalkConnectorName),
            "The connector's name is not a spawn set of both bubbles.");
        WorldBuild.Check(new[] { 51, 54, 52 }.Select(Renamed).All(bubble => ParentMaskFollows(world, bubble)),
            "An installed bubble parent's mask differs from its slice set's mask.");
        foreach (var (map, to) in new[] { (TownMap, walk), (WalkMap, town) })
        {
            var table = ZoneTable.Read(catalog.Read(VisibilityIdMap.Read(catalog.Read(map)).ZoneCrossing));
            var (destination, index) = WorldCrossings.RegionRow(world, WorldBubbles.MapIndex(world, to));
            var match = table.Connectors.Where(c => c.ConnectorId == row.Id).ToList();
            WorldBuild.Check(match is [var only] && only.DestinationId == destination && only.ConnectorIndex == (ulong)row.Row && only.DestinationIndex == (ulong)index,
                $"Map 0x{map:X8} has no connector to row {row.Row} that reaches region row {index}.");
            var (ownId, ownIndex) = WorldCrossings.RegionRow(world, WorldBubbles.MapIndex(world, to == walk ? town : walk));
            WorldBuild.Check(table.OwnId == ownId && table.OwnIndex == (ulong)ownIndex, $"Map 0x{map:X8} does not name its own region row.");
        }
        Console.WriteLine($"world_crossing_installed connector_row={row.Row} id=0x{row.Id:X16}");
    }

    /**
     * Two Trostland copies side by side with one border at x = 0 of the first spawn point. Each
     * switches only past half the width on the other's side, and the build compiles the new files.
     */
    private static void Added(StudioProject project, PackageCatalog catalog, RegistryIndex schema)
    {
        uint town = Renamed(51);
        ushort package = Enumerable.Range(0, 0x10000).Select(x => (ushort)x)
            .First(x => PackageWriter.IsNewPackageId(x) && !catalog.ContainsInstalledPackage(x));
        var west = WorldBubbles.AddBubble(project.ContentDirectory, Destination, town, "crossbed_west");
        var east = WorldBubbles.AddBubble(project.ContentDirectory, Destination, town, "crossbed_east");
        var world = WorldFiles.Load(project.ContentDirectory, Destination);
        var spawn = WorldContent.Spawns(world, west.NameHash)[0].Position;
        // Bundle space is world space here; west lies left of a line running north.
        var bounds = new Dictionary<string, (Vector3, Vector3)>
        {
            ["west"] = (spawn + new Vector3(-120, -60, -20), spawn + new Vector3(0, 60, 40)),
            ["east"] = (spawn + new Vector3(0, -60, -20), spawn + new Vector3(120, 60, 40)),
        };
        var crossing = new BundleCrossing("west", "east", [spawn + new Vector3(0, -60, 0), spawn + new Vector3(0, 60, 0)], 8);
        var hashes = new Dictionary<string, uint> { ["west"] = west.NameHash, ["east"] = east.NameHash };
        int listsBefore = WorldCrossings.CollectedLists(world, west.NameHash);
        var placed = WorldCrossingImport.Place(project, catalog, schema, Destination, [crossing], hashes, bounds, Matrix4x4.Identity, package, mode: CrossingMode.Partition);
        world = WorldFiles.Load(project.ContentDirectory, Destination);

        var connector = placed.Connectors.Single();
        WorldBuild.Check(WorldCrossings.Connectors(world, west.NameHash).Any(c => c.Row == connector.Row && c.Other == east.Row)
            && WorldCrossings.Connectors(world, east.NameHash).Any(c => c.Row == connector.Row && c.Other == west.Row),
            "The new connector is not listed by both slice sets.");
        WorldBuild.Check(WorldBubbles.SpawnSets(world, west.NameHash).Contains(connector.NameHash) && WorldBubbles.SpawnSets(world, east.NameHash).Contains(connector.NameHash),
            "Both bubbles must declare the connector's spawn set.");
        WorldBuild.Check(ParentMaskFollows(world, west.NameHash) && ParentMaskFollows(world, east.NameHash),
            "A bubble parent's mask does not repeat its slice set's mask after the connector.");

        // Each side's statics and collision load in the other through one container both definitions list.
        foreach (var (bubble, other) in new[] { (west.NameHash, east.NameHash), (east.NameHash, west.NameHash) })
        {
            string file = $"{WorldTemplate.Folder}/{Destination}/map_container/stream_{bubble:x8}.map_container";
            WorldBuild.Check(world.Contains(file) && WorldContent.Containers(world, bubble).Contains(file) && WorldContent.Containers(world, other).Contains(file),
                $"Bubble 0x{bubble:X8} has no stream container both bubbles list.");
            Console.WriteLine($"world_crossing_stream bubble=0x{bubble:X8} lists={((System.Text.Json.Nodes.JsonArray)world.Fields(file)["contents"]!).Count}");
        }

        // Walk east to west across the border and read both tomes as the z-leg does.
        var tomes = new Dictionary<uint, (Tome Tome, ZoneTable Table)>();
        foreach (var (bubble, item) in placed.Bubbles)
        {
            var asset = project.AddedAssets.Single(a => a.Native?.PackageId == package && TagHashes.Make(package, a.Native.EntryIndex!.Value) == item.Tome);
            var tome = TomeReader.Read(project.ReadAddedAsset(asset.Id, catalog));
            string zoneFile = item.Files.Single(f => f.EndsWith("_zone_crossing.c_80809756", StringComparison.Ordinal));
            var table = ZoneTable.Read(world[zoneFile].Data);
            WorldBuild.Check(table.Records.Count == tome.Clusters.Count && table.Connectors.Count == 1, $"Bubble 0x{bubble:X8} has a record count unlike its cluster count.");
            var cells = ZoneTable.Read(world[item.Files.Single(f => f.EndsWith("_tome1_cells.c_80809756", StringComparison.Ordinal))].Data);
            WorldBuild.Check(cells.Environments.Count == 1 && cells.Music.Count == 1 && cells.Records.All(r => r.Environment == 0 && r.Music == 0)
                && tome.Objects.Count == 1, $"Bubble 0x{bubble:X8} has a tome-1 table without an environment and music, or a tome without its object.");
            tomes[bubble] = (tome, table);
        }
        float lastWestSwitch = float.MinValue, firstEastSwitch = float.MaxValue;
        for (float x = -60; x <= 60; x += 0.25f)
        {
            var point = spawn + new Vector3(x, 0, 1);
            if (Kind(tomes[west.NameHash], point) == ZoneRecord.Switch) firstEastSwitch = Math.Min(firstEastSwitch, x);
            if (Kind(tomes[east.NameHash], point) == ZoneRecord.Switch) lastWestSwitch = Math.Max(lastWestSwitch, x);
        }
        WorldBuild.Check(firstEastSwitch >= 4 - 1e-3f && firstEastSwitch <= 6 && lastWestSwitch <= -4 + 1e-3f && lastWestSwitch >= -6,
            $"West switches from x = {firstEastSwitch} and east up to x = {lastWestSwitch}; each should be 4 to 6 m past the line.");

        var tags = WorldBuild.Compile(catalog, schema, world.Files, "crossing.checks");
        foreach (var (bubble, item) in placed.Bubbles)
        {
            var built = tags.Single(t => t.Name == item.Files[0]);
            var map = VisibilityIdMap.Read(built.Data);
            WorldBuild.Check(map.FineTome == item.Tome && map.Bubble == (uint)WorldBubbles.ScenarioRow(world, bubble)
                && tags.Any(t => t.TagHash == map.ZoneCrossing) && tags.Any(t => t.TagHash == map.Tome1Cells),
                $"The built map of 0x{bubble:X8} does not name its tome, row and tables.");
            Console.WriteLine($"world_crossing_bubble row={WorldBubbles.ScenarioRow(world, bubble)} clusters={item.Clusters} switching={item.Switching} lists={item.Lists} tome_bytes={project.AddedAssets.Single(a => a.Name.EndsWith($"{bubble:x8}.tome", StringComparison.Ordinal)).Size}");
        }
        Console.WriteLine($"world_crossing_walk west_switches_from={firstEastSwitch} east_switches_to={lastWestSwitch} lists_before={listsBefore} connector_row={connector.Row}");
    }

    private static sbyte Kind((Tome Tome, ZoneTable Table) side, Vector3 point)
    {
        int cluster = side.Tome.LocateCluster(point);
        return cluster < 0 ? (sbyte)-1 : side.Table.Records[cluster].Kind;
    }

    /** The Transitions layer reads every placed EDZ map: one cell per cluster, and Trostland's switch cells. */
    private static void Transitions(PackageCatalog catalog, uint mapRoot)
    {
        var scene = new SunriseStudio.Scene.WorldLoader(catalog).LoadMap(mapRoot).Scene;
        int errors = scene.DecodeErrorCount;
        TransitionCells.Read(catalog, scene);
        var town = scene.Visibility.SingleOrDefault(placed => placed.MapTag == TownMap);
        int zones = scene.Markers.Count(marker => marker.Category == "Crossing");
        WorldBuild.Check(scene.DecodeErrorCount == errors && scene.Visibility.Count > 0 && scene.Visibility.All(placed => placed.Cells.Count > 0 || !HasTables(placed.MapTag))
            && town is not null && town.Cells.Any(cell => cell.Kind == ZoneRecord.Switch) && zones > 0,
            "The Transitions layer did not read every installed EDZ visibility map, or Trostland has no switch cells.");
        // A map whose fine tome or zone-crossing table is not installed draws no cells.
        bool HasTables(uint tag)
        {
            var map = VisibilityIdMap.Read(catalog.Read(tag));
            return catalog.ContainsTag(map.FineTome) && catalog.ContainsTag(map.ZoneCrossing);
        }
        Console.WriteLine($"world_crossing_transitions maps={scene.Visibility.Count} cells={scene.Visibility.Sum(placed => placed.Cells.Count)} zones={zones}");
    }
    /** The bubble parent's mask at +28 (metadata +12) equals the bubble's slice-set cell mask. */
    private static bool ParentMaskFollows(WorldFiles world, uint bubble)
    {
        var parent = world.Files.Single(file => file.ClassId == SBubbleParent.Class && WorldFiles.Hash(WorldFiles.Field(
            (JsonObject)WorldFiles.Field(world.Fields(file.Name), "metadata"), "bubble_name_hash")) == bubble);
        var metadata = (JsonObject)WorldFiles.Field(world.Fields(parent.Name), "metadata");
        // The 64-byte metadata's unnamed bytes, by offset.
        byte[] raw = new byte[64];
        foreach (var (key, value) in metadata["@unknown"] as JsonObject ?? [])
            Convert.FromHexString((string)value!).CopyTo(raw, int.Parse(key, System.Globalization.CultureInfo.InvariantCulture));
        byte[] stored = raw[12..44];
        var sets = (JsonArray)WorldFiles.Field(world.Fields(world.Single(SMapLocationNames.Class).Name), "slice_sets");
        byte[] mask = Convert.FromHexString(WorldFiles.Text(sets[WorldBubbles.ScenarioRow(world, bubble)]!["cell_mask"]) ?? "");
        return mask.Length == 32 && stored.AsSpan().SequenceEqual(mask);
    }

    private static uint Renamed(int row) => SunriseStudio.Packages.StringBankWriter.Hash(WorldIdentity.BubbleName(Destination, row));
}

