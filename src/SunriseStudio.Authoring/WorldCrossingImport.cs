using System.Numerics;
using System.Text.Json.Nodes;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.Scene.Collision;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Graph;
using SunriseStudio.Tags.Tft;
using SunriseStudio.Tags.Views;
using SunriseStudio.World;

namespace SunriseStudio.Authoring;

/**
 * One bundle crossing, in bundle space: `From` is the bubble whose bubble.json lists it.
 * @param Line The border, with `From` on its left seen from above.
 */
public sealed record BundleCrossing(string From, string To, IReadOnlyList<Vector3> Line, float Width);

/** What WorldCrossingImport.Place made. */
public sealed record PlacedCrossings(IReadOnlyList<CrossingConnector> Connectors, IReadOnlyDictionary<uint, PlacedCrossingBubble> Bubbles);

/**
 * One bubble's visibility map and cost.
 * @param Tome The new tome, or the installed one an edit kept; 0 when no map was touched.
 * @param Clusters Tome-0 clusters, one zone-crossing record each.
 * @param Switching Clusters that switch to a neighbour; 0 means the crossing cannot fire here.
 * @param Lists Non-empty lists the client collects when the bubble loads; the client allows 1024.
 */
public sealed record PlacedCrossingBubble(CrossingMode Mode, uint Tome, int Clusters, int Switching, int Lists, IReadOnlyList<string> Files, string Note);

/** How Place gives a bubble its zone crossing; Uniform is the default. */
public enum CrossingMode
{
    /** An installed map keeps its tome and gets records in its own table; a bubble without one gets a Uniform map. */
    Edit,
    /** A new map whose tome has 2 m cells near borders and 64 m cells elsewhere. */
    Partition,
    /** A new map whose tome is one grid of equal cubes, the TomeBaker.Open shape. */
    Uniform,
    /** A new map whose tome is the installed 4-tile tome of map 0x80ED74AD, stretched over the bubble. */
    Retail,
}

/** Walk-through crossings between bundle bubbles: connectors, streaming and each bubble's tome and zone-crossing table. */
public static class WorldCrossingImport
{
    /** Default border width: each side switches 4 m past the line. */
    public const float DefaultWidth = 8;
    // Leaf tiles near a border; it bounds how far past its line a side switches.
    private const float FineTile = 2;
    // Leaf tiles elsewhere; the z-leg reads only X and Y, so tall coarse tiles are enough.
    private const float CoarseTile = 64;
    // Height a bubble's tome reaches above and below its bounds, so a jumping camera stays in a cell.
    private const float HeightMargin = 64;
    // Metres a crossing's spawn points stand past each side's switch line.
    private const float SpawnStep = 2;
    // The installed empty class-0x80807323 tag a tome-bearing map with no objects names.
    private const uint EmptyBoxes = 0x80C485E6;
    // The installed map whose 4-tile open tome Retail stretches.
    private const uint RetailMap = 0x80ED74AD;
    // Uniform's smallest cube edge, and the most cubes it lets one tome hold.
    private const float UniformTile = 8;
    private const int UniformTiles = 8192;
    // A tome-1 record's "none": the client dereferences any other environment's tag without a null check.
    private const byte NoAudio = 0xFF;
    // Crossing widths either side of a line in which spawn points vote on its direction.
    private const float SideCheckWidths = 3;

    /**
     * Reads every bubble's `crossings`. A pair may be listed once, by either bubble.
     * @param bubbles Each bundle bubble's parsed bubble.json, by folder name.
     */
    public static IReadOnlyList<BundleCrossing> Read(IReadOnlyDictionary<string, JsonObject> bubbles)
    {
        var result = new List<BundleCrossing>();
        var pairs = new HashSet<(string, string)>();
        foreach (var (name, bubble) in bubbles)
            foreach (var item in (bubble["crossings"] as JsonArray ?? []).OfType<JsonObject>())
            {
                string to = (string?)item["to"] ?? throw new InvalidDataException($"{name}: a crossing has no to.");
                if (!bubbles.ContainsKey(to) || to == name) throw new InvalidDataException($"{name}: crossing to {to} names no other bundle bubble.");
                var key = string.CompareOrdinal(name, to) < 0 ? (name, to) : (to, name);
                if (!pairs.Add(key)) throw new InvalidDataException($"{name}: the crossing with {to} is listed twice.");
                var line = ((JsonArray?)item["line"] ?? []).Select(point => new Vector3((float)point![0]!, (float)point[1]!, (float)point[2]!)).ToArray();
                if (line.Length < 2) throw new InvalidDataException($"{name}: the crossing to {to} needs a line of two points or more.");
                float width = (float?)item["width"] ?? DefaultWidth;
                if (!(width >= 2 * FineTile)) throw new InvalidDataException($"{name}: a crossing width is at least {2 * FineTile} m.");
                // Most of the bubble's spawn points near the line stand left of it; farther off, a bubble may wrap round.
                var near = (bubble["spawn_points"] as JsonArray ?? []).OfType<JsonObject>().Select(point => point["position"]).OfType<JsonArray>()
                    .Select(spawn => WorldCrossings.Side(line, new((float)spawn[0]!, (float)spawn[1]!), width))
                    .Where(side => side is { } distance && MathF.Abs(distance) <= SideCheckWidths * width).ToList();
                if (near.Count(side => side > 0) > near.Count(side => side < 0))
                    throw new InvalidDataException($"{name}: the crossing line to {to} runs the wrong way; {name} must lie on its left.");
                result.Add(new(name, to, line, width));
            }
        return result;
    }

    /**
     * Gives each crossing a connector row and spawn set, each bubble with a crossing its own
     * visibility map, tome and zone-crossing table, and, with `stream`, makes each bubble load its
     * neighbours' streamable lists.
     * @param hashes Bundle bubble name to the destination bubble's name hash.
     * @param bounds Bundle bubble name to its bounds in bundle space; every crossing bubble needs one.
     * @param place Bundle space to world space, the same for every bubble of a shared-space bundle.
     * @param packageId The project package that takes the tomes.
     * @param mode How each bubble gets its visibility map.
     */
    public static PlacedCrossings Place(StudioProject project, PackageCatalog catalog, RegistryIndex schema, string destination,
        IReadOnlyList<BundleCrossing> crossings, IReadOnlyDictionary<string, uint> hashes,
        IReadOnlyDictionary<string, (Vector3 Min, Vector3 Max)> bounds, Matrix4x4 place, ushort packageId, bool stream = true,
        CrossingMode mode = CrossingMode.Uniform)
    {
        if (crossings.Count == 0) return new([], new Dictionary<uint, PlacedCrossingBubble>());
        var world = WorldFiles.Load(project.ContentDirectory, destination);
        var template = Template(world, catalog, schema);
        var borders = crossings.Select(c => new CrossingBorder(hashes[c.From], hashes[c.To],
            c.Line.Select(point => Vector3.Transform(point, place)).ToArray(), c.Width)).ToList();
        var boxes = bounds.ToDictionary(pair => hashes[pair.Key], pair => Transform(pair.Value, place));
        var connectors = new List<CrossingConnector>();
        foreach (var (crossing, border) in crossings.Zip(borders))
        {
            string name = WorldCrossings.ConnectorName(crossing.From, crossing.To);
            var connector = WorldCrossings.AddConnector(world, border.From, border.To, name);
            connectors.Add(connector);
            Spawns(world, destination, name, border, connector.NameHash);
        }
        // A public neighbour is a second host instance. The client leaves it when the player walks out
        // of a crossing's range, and the link to the player's own instance does not survive that.
        foreach (uint bubble in hashes.Values.Distinct()) WorldBubbles.SetPrivate(world, bubble);
        var placed = new Dictionary<uint, PlacedCrossingBubble>();
        int entry = project.NextEntry(packageId);
        // A bubble with no border needs its own tome too: the template's covers only the template's ground.
        foreach (uint bubble in borders.SelectMany(b => new[] { b.From, b.To }).Concat(hashes.Values.Where(boxes.ContainsKey)).Distinct())
        {
            // Each border seen from this bubble, so its neighbour lies right of the line.
            var own = new List<(CrossingBorder Border, CrossingConnector Connector)>();
            for (int index = 0; index < borders.Count; index++)
            {
                var border = borders[index];
                if (border.From == bubble) own.Add((border, connectors[index]));
                else if (border.To == bubble) own.Add((new(bubble, border.From, border.Line.Reverse().ToArray(), border.Width), connectors[index]));
            }
            var connectorRows = own.Select(pair =>
            {
                var (destinationId, destinationIndex) = WorldCrossings.RegionRow(world, WorldBubbles.MapIndex(world, pair.Border.To));
                return new ZoneConnector((ulong)pair.Connector.Row, pair.Connector.Id, (ulong)destinationIndex, destinationId, 0, new byte[7]);
            }).ToList();
            var ownBorders = own.Select(pair => pair.Border).ToList();
            var installed = InstalledMap(world, catalog, bubble);
            if (mode == CrossingMode.Edit && installed is { } stock)
            {
                placed[bubble] = EditInstalled(project, catalog, schema, stock, ownBorders, connectorRows);
                continue;
            }
            var bake = mode == CrossingMode.Edit ? CrossingMode.Uniform : mode;
            var (tome, records, width) = bake switch
            {
                CrossingMode.Uniform => BakeUniform(boxes, bubble, ownBorders),
                CrossingMode.Retail => BakeRetail(catalog, boxes, bubble, ownBorders),
                _ => Bake(boxes, bubble, ownBorders),
            };
            WorldCrossings.RemoveCellGeometryLists(world, bubble);
            byte[] tomeBytes = TomeWriter.Write(tome);
            var (ownId, ownIndex) = WorldCrossings.RegionRow(world, WorldBubbles.MapIndex(world, bubble));
            var zone = new ZoneTable { OwnIndex = (ulong)ownIndex, OwnId = ownId, Tags = template.Tags.ToArray() };
            zone.Records.AddRange(records);
            zone.Connectors.AddRange(connectorRows);
            var cells = new ZoneTable { OwnIndex = zone.OwnIndex, OwnId = zone.OwnId, Tags = template.CellTags.ToArray() };
            cells.Records.AddRange(records.Select(record => record with { Environment = NoAudio, Music = NoAudio }));
            cells.Connectors.AddRange(connectorRows);
            uint row = (uint)WorldBubbles.ScenarioRow(world, bubble);
            uint emptyBoxes = catalog.ContainsTag(EmptyBoxes) ? EmptyBoxes : uint.MaxValue;
            int switching = records.Count(r => r.Kind == ZoneRecord.Switch);
            string size = $"{bake} tome, {tomeBytes.Length} bytes, switch {width / 2:0.#} m past each line";
            // The client has two map slots, so the new map must be the bubble's only one. A clone
            // still lists the map of the bubble it was copied from.
            WorldCrossings.RemoveVisibilityLists(world, bubble);
            string name = $"{destination}_{bubble:x8}";
            if (entry + 4 > TagHashes.EntriesPerPackage) throw new InvalidDataException("The crossings need four free package entries per new bubble.");
            uint tomeTag = TagHashes.Make(packageId, entry++), mapTag = TagHashes.Make(packageId, entry++);
            uint zoneTag = TagHashes.Make(packageId, entry++), cellTag = TagHashes.Make(packageId, entry++);
            var map = VisibilityIdMap.Open(row, tomeTag, zoneTag, cellTag, emptyBoxes, template.TailA, template.TailB, tome.Min, tome.Max);
            project.AddAssets(
            [
                new($"{name}.tome", tomeBytes, new(packageId, template.TomeEntry.Reference, template.TomeEntry.TypeInfo, null, TagHashes.Entry(tomeTag))),
                new($"{name}.visibility", map.Write(schema), new(packageId, VisibilityIdMap.Class, template.MapTypeInfo, null, TagHashes.Entry(mapTag))),
                new($"{name}.zone_crossing", zone.Write(schema), new(packageId, ZoneTable.Class, template.TableTypeInfo, null, TagHashes.Entry(zoneTag))),
                new($"{name}.tome1_cells", cells.Write(schema), new(packageId, ZoneTable.Class, template.TableTypeInfo, null, TagHashes.Entry(cellTag))),
            ], catalog);
            string list = WorldCrossings.AddVisibilityList(world, catalog, destination, bubble, name, template.List, $"#0x{mapTag:X8}");
            placed[bubble] = new(bake, tomeTag, records.Length, switching, 0, [list], size);
        }
        if (stream)
            foreach (uint bubble in placed.Keys) WorldCrossings.Stream(world, destination, bubble, connectors);
        foreach (uint bubble in placed.Keys.ToList())
        {
            int lists = WorldCrossings.CollectedLists(world, bubble);
            if (lists > WorldCrossings.MaximumLists)
                throw new InvalidDataException($"Bubble 0x{bubble:X8} would collect {lists} lists with its neighbours; the client allows {WorldCrossings.MaximumLists}.");
            placed[bubble] = placed[bubble] with { Lists = lists };
        }
        world.SaveChanged(project.ContentDirectory);
        return new PlacedCrossings(connectors, placed);
    }

    /**
     * A bubble's open tome over its bounds and its neighbours', fine near its borders, and the
     * zone-crossing record of each cluster.
     */
    public static (Tome Tome, ZoneRecord[] Records, float Width) Bake(IReadOnlyDictionary<uint, (Vector3 Min, Vector3 Max)> bounds, uint bubble,
        IReadOnlyList<CrossingBorder> borders)
    {
        var (min, max) = Reach(bounds, bubble, borders, HeightMargin);
        bool Refine(Vector3 lo, Vector3 hi)
        {
            var center = (lo + hi) * 0.5f;
            float half = new Vector2(hi.X - lo.X, hi.Y - lo.Y).Length() * 0.5f;
            return borders.Any(border => WorldCrossings.Side(border.Line, new(center.X, center.Y), border.Width + half) is float side
                && MathF.Abs(side) <= border.Width * 0.5f + 2 * FineTile + half);
        }
        var tome = TomeBaker.Partition(min, max, FineTile, CoarseTile, Refine);
        TomeBaker.Finish(tome);
        return (tome, WorldCrossings.Classify(Clusters(tome), borders.Select((border, index) => (border, index)).ToList()), borders.Min(b => b.Width));
    }

    /**
     * A grid of equal cubes, as TomeBaker.Open makes, over the bubble's bounds and its neighbours'.
     * The cube grows until the grid fits UniformTiles; each border's width grows to keep a cell of gap.
     */
    public static (Tome Tome, ZoneRecord[] Records, float Width) BakeUniform(IReadOnlyDictionary<uint, (Vector3 Min, Vector3 Max)> bounds, uint bubble,
        IReadOnlyList<CrossingBorder> borders)
    {
        var (min, max) = Reach(bounds, bubble, borders, UniformTile);
        float tile = UniformTile;
        int Tiles(float t) => new[] { max.X - min.X, max.Y - min.Y, max.Z - min.Z }
            .Aggregate(1, (count, extent) => count * (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, MathF.Ceiling(extent / t))));
        while (Tiles(tile) > UniformTiles) tile *= 2;
        var tome = TomeBaker.Open(min, max, tile / TomeBaker.TileVoxels);
        TomeBaker.Finish(tome);
        var wide = borders.Select(b => b with { Width = MathF.Max(b.Width, 2 * tile + 2) }).ToList();
        return (tome, WorldCrossings.Classify(Clusters(tome), wide.Select((border, index) => (border, index)).ToList()),
            wide.Count == 0 ? 0 : wide.Min(b => b.Width));
    }

    /**
     * The installed 4-tile open tome of map 0x80ED74AD, stretched over the bubble's bounds and its
     * neighbours'. Only world positions change; every grid value stays as installed.
     */
    public static (Tome Tome, ZoneRecord[] Records, float Width) BakeRetail(PackageCatalog catalog, IReadOnlyDictionary<uint, (Vector3 Min, Vector3 Max)> bounds,
        uint bubble, IReadOnlyList<CrossingBorder> borders)
    {
        var (min, max) = Reach(bounds, bubble, borders, UniformTile);
        var tome = TomeReader.Read(catalog.Read(VisibilityIdMap.Read(catalog.Read(RetailMap)).FineTome));
        var (from, to) = (tome.Min, tome.Max);
        Vector3 Map(Vector3 p) => min + (p - from) / (to - from) * (max - min);
        tome.Min = min;
        tome.Max = max;
        // A split is a world position on its node's axis; a leaf's stored value is not one.
        void MapSplits(TomeKdTree tree)
        {
            for (int i = 0; i < tree.Splits.Count; i++)
                if (tree.Codes[i] is var axis and not TomeKdTree.Leaf)
                    tree.Splits[i] = min[axis] + (tree.Splits[i] - from[axis]) / (to[axis] - from[axis]) * (max[axis] - min[axis]);
        }
        MapSplits(tome.TileTree);
        foreach (var tile in tome.Tiles.OfType<TomeTile>())
        {
            tile.Min = Map(tile.Min);
            tile.Max = Map(tile.Max);
            if (tile.ViewTree is { } view) MapSplits(view);
        }
        for (int i = 0; i < tome.Objects.Count; i++) tome.Objects[i] = new(Map(tome.Objects[i].Min), Map(tome.Objects[i].Max));
        for (int i = 0; i < tome.GateVertices.Count; i++) tome.GateVertices[i] = Map(tome.GateVertices[i]);
        return (tome, WorldCrossings.Classify(Clusters(tome), borders.Select((border, index) => (border, index)).ToList()), borders.Min(b => b.Width));
    }

    /** The box over a bubble's bounds and its neighbours', grown by `height` above and below. */
    private static (Vector3 Min, Vector3 Max) Reach(IReadOnlyDictionary<uint, (Vector3 Min, Vector3 Max)> bounds, uint bubble,
        IReadOnlyList<CrossingBorder> borders, float height)
    {
        var reach = borders.Select(b => b.To).Append(bubble).Select(hash => bounds.TryGetValue(hash, out var box) ? box
            : throw new InvalidDataException($"Bubble 0x{hash:X8} has a crossing and no bounds.")).ToList();
        return (reach.Select(box => box.Min).Aggregate(Vector3.Min) - new Vector3(0, 0, height),
            reach.Select(box => box.Max).Aggregate(Vector3.Max) + new Vector3(0, 0, height));
    }

    /** Each cluster's world box, in cluster order. */
    private static List<(Vector3 Min, Vector3 Max)> Clusters(Tome tome) => tome.Clusters.Select(cluster =>
        (tome.GridToWorld(cluster.Bounds.MinX, cluster.Bounds.MinY, cluster.Bounds.MinZ),
         tome.GridToWorld(cluster.Bounds.MaxX, cluster.Bounds.MaxY, cluster.Bounds.MaxZ))).ToList();

    /** The installed map a bubble's own lists place, its tome-0 tag and its zone-crossing table tag; null for none. */
    private static (uint Map, uint Tome, uint Table)? InstalledMap(WorldFiles world, PackageCatalog catalog, uint bubble)
    {
        foreach (uint map in WorldCrossings.VisibilityMaps(world, bubble))
        {
            if (!catalog.TryGetEntry(map, out var entry) || entry.Reference != VisibilityIdMap.Class) continue;
            var stock = VisibilityIdMap.Read(catalog.Read(map));
            if (catalog.ContainsTag(stock.FineTome) && catalog.ContainsTag(stock.ZoneCrossing)) return (map, stock.FineTome, stock.ZoneCrossing);
        }
        return null;
    }

    /**
     * Adds the crossings to an installed map's own zone-crossing table: its connectors gain the new
     * ones, and each cluster of its tome that a border puts in a switch or near band takes the new
     * record. Every other record, and the tome, stay as installed.
     */
    private static PlacedCrossingBubble EditInstalled(StudioProject project, PackageCatalog catalog, RegistryIndex schema,
        (uint Map, uint Tome, uint Table) stock, IReadOnlyList<CrossingBorder> borders, IReadOnlyList<ZoneConnector> connectors)
    {
        var tome = TomeReader.Read(project.ReadAsset(stock.Tome, catalog));
        var table = ZoneTable.Read(project.ReadAsset(stock.Table, catalog));
        if (table.Records.Count != tome.Clusters.Count)
            throw new InvalidDataException($"Table 0x{stock.Table:X8} has {table.Records.Count} records for {tome.Clusters.Count} clusters.");
        int first = table.Connectors.Count;
        if (first + connectors.Count > sbyte.MaxValue) throw new InvalidDataException($"Table 0x{stock.Table:X8} has no room for more connectors.");
        table.Connectors.AddRange(connectors);
        var ours = WorldCrossings.Classify(Clusters(tome), borders.Select((border, index) => (border, first + index)).ToList());
        int switching = 0, changed = 0;
        for (int i = 0; i < ours.Length; i++)
        {
            if (ours[i].Kind is not (ZoneRecord.Switch or ZoneRecord.Near)) continue;
            table.Records[i] = ours[i] with { Point = table.Records[i].Point };
            changed++;
            if (ours[i].Kind == ZoneRecord.Switch) switching++;
        }
        project.ApplyEdit(stock.Table, table.Write(schema), catalog);
        return new(CrossingMode.Edit, stock.Tome, ours.Length, switching, 0, [],
            $"installed map 0x{stock.Map:X8} kept; table 0x{stock.Table:X8} edited, {changed} records changed"
            + (switching == 0 ? "; its tome has no cell past any switch line, so no crossing fires here" : ""));
    }

    /** A spawn point of the connector's set on each side, past that side's switch line, facing away from the border. */
    private static void Spawns(WorldFiles world, string destination, string name, CrossingBorder border, uint set)
    {
        var line = border.Line;
        float total = 0;
        for (int i = 0; i + 1 < line.Count; i++) total += Vector2.Distance(new(line[i].X, line[i].Y), new(line[i + 1].X, line[i + 1].Y));
        float walked = 0;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            var a = line[i]; var b = line[i + 1];
            float length = Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y));
            if (walked + length < total / 2 && i + 2 < line.Count) { walked += length; continue; }
            var middle = Vector3.Lerp(a, b, length > 0 ? (total / 2 - walked) / length : 0);
            var along = Vector2.Normalize(new Vector2(b.X - a.X, b.Y - a.Y));
            var right = new Vector3(along.Y, -along.X, 0);
            float step = border.Width * 0.5f + SpawnStep;
            foreach (var (bubble, sign, side) in new[] { (border.From, -1f, "a"), (border.To, 1f, "b") })
            {
                var facing = right * sign;
                var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.Atan2(facing.Y, facing.X));
                WorldContent.AddSpawnSets(world, destination, $"{name.Replace(':', '_')}_{side}", [(set, middle + facing * step, turn)],
                    WorldBubbles.OwnContainer(world, bubble));
            }
            return;
        }
    }

    /** World box of a bundle box. */
    private static (Vector3 Min, Vector3 Max) Transform((Vector3 Min, Vector3 Max) box, Matrix4x4 place)
    {
        var corners = Enumerable.Range(0, 8).Select(i => Vector3.Transform(new Vector3(
            (i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y, (i & 4) == 0 ? box.Min.Z : box.Max.Z), place)).ToArray();
        return (corners.Aggregate(Vector3.Min), corners.Aggregate(Vector3.Max));
    }

    /** What a new map copies from an installed one: its list, its table tags and tail words, and the package words. */
    private sealed record MapTemplate(TftFile List, uint[] Tags, uint[] CellTags, uint TailA, uint TailB,
        uint MapTypeInfo, uint TableTypeInfo, PackageEntry TomeEntry);

    /**
     * The visibility list to copy and the installed map it names: the world's own list, or else
     * one an installed source of a world container lists, as a stripped world keeps none.
     */
    private static MapTemplate Template(WorldFiles world, PackageCatalog catalog, RegistryIndex schema)
    {
        var list = WorldCrossings.VisibilityList(world);
        uint map = 0;
        if (list is not null)
            map = list.References.Where(r => r.Kind == TftReferenceKind.TagRef && r.Target.StartsWith('#')).Select(r => (uint)TftNames.InstalledHash(r.Target))
                .FirstOrDefault(tag => catalog.TryGetEntry(tag, out var e) && e.Reference == VisibilityIdMap.Class);
        if (map == 0)
        {
            list = null;
            foreach (var container in world.Files.Where(file => file.ClassId == SMapContainer.Class && file.SourceTag is not null))
            {
                uint source = container.SourceTag!.Value;
                if (!catalog.TryGetEntry(source, out _)) continue;
                byte[] bytes = catalog.Read(source);
                foreach (var member in TagView.Rows(bytes, in TagView.Checked<SMapContainer>(bytes, 0).Contents, ContainerMember.Class).ToArray())
                {
                    if (!catalog.TryGetEntry(member.MemberTag, out var entry) || entry.Reference != SMapPlacements.Class) continue;
                    var payloads = StaticCollisionReader.PlacedPayloads(catalog.Read(member.MemberTag), member.MemberTag).ToList();
                    if (payloads is not [{ Marker: SPlacementVisibilityConstructor.Class } only]) continue;
                    list = TftClone.Clone([member.MemberTag], new CatalogTagSource(catalog), schema, _ => "world/scratch/map_placements/visibility.map_placements").Single();
                    map = only.Tag;
                    break;
                }
                if (list is not null) break;
            }
        }
        if (list is null || !catalog.TryGetEntry(map, out var mapEntry)) throw new InvalidDataException("The world reaches no visibility list to copy.");
        var installed = VisibilityIdMap.Read(catalog.Read(map));
        if (!catalog.TryGetEntry(installed.ZoneCrossing, out var tableEntry) || !catalog.TryGetEntry(installed.FineTome, out var tomeEntry))
            throw new InvalidDataException($"Visibility map 0x{map:X8} has no zone-crossing table or tome.");
        var table = ZoneTable.Read(catalog.Read(installed.ZoneCrossing));
        // The tome-1 table whose tag words are copied: the template's, or the retail open-tome map's when it has no audio rows.
        var cells = new[] { installed.Tome1Cells, VisibilityIdMap.Read(catalog.Read(RetailMap)).Tome1Cells }.Where(catalog.ContainsTag)
            .Select(tag => ZoneTable.Read(catalog.Read(tag))).First(t => t.Environments.Count > 0 && t.Music.Count > 0);
        return new(list, table.Tags, cells.Tags, installed.TailA, installed.TailB, mapEntry.TypeInfo, tableEntry.TypeInfo, tomeEntry);
    }
}
