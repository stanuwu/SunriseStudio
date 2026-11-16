using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using SunriseStudio.World;

/**
 * The Testbed's Director node: a planet tile on the top page, a graph in both halves, a map page and
 * three landing zones, each read back the way the client reads it.
 */
static class DirectorChecks
{
    private const ushort Package = 0x0123;
    private const int First = 40;
    private static readonly TextReference Name = new(7, 0x1111AAAA), Description = new(7, 0x2222AAAA), Short = new(7, 0x3333AAAA);

    public static void Run(string gameRoot)
    {
        using var catalog = PackageCatalog.Open(gameRoot);
        var schema = StudioRegistry.Index;
        WorldBuild.Check(StringBankWriter.Hash("edz") == 0x29930BA4, "FNV-1 of edz is not the map name EDZ's zones carry.");

        var edz = ActivityRows.FreeRoam(catalog.ReadOriginal(ActivityRows.ActivityTable)).Single(x => x.Name == "edz_freeroam");
        var activity = ActivityRows.Append(catalog.ReadOriginal, new ActivityRequest("testbed", edz, "worldauthoring-tests/testbed"));
        byte[] Read(uint tag) => activity.Edits.TryGetValue(tag, out var edited) ? edited : catalog.ReadOriginal(tag);

        DirectorZone Zone(int index, uint bubble, uint spawn, Vector3 at) =>
            new(new(7, 0x4444AA00u + (uint)index), new(7, 0x5555AA00u + (uint)index), bubble, spawn, at);
        var zones = new[]
        {
            Zone(0, 0xB8459D59, 0x9617A6E7, new(525, 5, 73)),
            Zone(1, 0xC0064AEF, 0xD7BA9C15, new(462, 146, 74)),
            Zone(2, 0xC0064AED, 0x92FF9A33, new(525, 148, 73)),
        };
        var request = new DirectorRequest("testbed", activity.ActivityIndex, Name, Description, Short,
            Image(648, 324), Image(677, 594), new(300, 350), new(450, 450), new(0.28f, 0.5f), zones);
        var plan = DirectorAuthoring.Plan(catalog, Read, schema, request, Package, First);

        WorldBuild.Check(plan.GraphIndex == 26 && plan.Assets.Count == DirectorAuthoring.EntryCount,
            $"The plan made graph {plan.GraphIndex} with {plan.Assets.Count} tags.");
        uint Tag(int index) => TagHashes.Make(Package, First + index);
        byte[] Asset(int index) => plan.Assets.Single(asset => asset.Native!.EntryIndex == First + index).Data;

        CheckIndex(plan.Edits[DirectorAuthoring.GraphIndexTag], plan.GraphHash, Tag(9));
        CheckIndex(plan.Edits[DirectorAuthoring.DefinitionIndexTag], plan.GraphHash, Tag(10));
        CheckTile(plan.Edits[DirectorAuthoring.SolarGraph], plan);
        CheckGraphs(Asset(9), Asset(10), Tag(11), activity.ActivityIndex);
        _ = TagDocument.Parse(Asset(9), 0x80805E79, schema);
        CheckLinked(plan.Edits[ActivityRows.ActivityTextTable], ActivityRows.TextRecordOffset(plan.Edits[ActivityRows.ActivityTextTable],
            activity.ActivityIndex) + TagView.FieldOffset<ActivityTextRecord>(nameof(ActivityTextRecord.H1c2a8c12)), "activity");
        CheckLinked(plan.Edits[ActivityRows.DestinationTextTable], ActivityRows.DestinationTextRowOffset(
            plan.Edits[ActivityRows.DestinationTextTable], activity.DestinationIndex)
            + TagView.FieldOffset<DestinationTextRow>(nameof(DestinationTextRow.H1c2a8c12)), "destination");
        CheckZones(plan, zones, activity.ActivityIndex);
        // The template rows keep their own arrays: EDZ's activity, its destination and Trostland's zone.
        CheckSame(Read(ActivityRows.ActivityTextTable), plan.Edits[ActivityRows.ActivityTextTable],
            table => ActivityRows.TextRecordOffset(table, edz.Index) + TagView.FieldOffset<ActivityTextRecord>(nameof(ActivityTextRecord.H1c2a8c12)),
            24, "EDZ's activity graph list");
        CheckSame(Read(ActivityRows.DestinationTextTable), plan.Edits[ActivityRows.DestinationTextTable],
            table => ActivityRows.DestinationTextRowOffset(table, edz.Destination)
                + TagView.FieldOffset<DestinationTextRow>(nameof(DestinationTextRow.H1c2a8c12)), 24, "EDZ's destination graph list");
        CheckSame(catalog.ReadOriginal(DirectorAuthoring.MapNodes), plan.Edits[DirectorAuthoring.MapNodes],
            table => TagArray.Checked(table, 8, stride: 72).Data + 60 * 72 + 56, 80, "Trostland's zone locations");
        CheckPages(catalog, plan, Asset(6), Asset(7), Asset(8), Tag(2), Tag(5), Tag(6), Tag(7), Tag(8));
        CheckPlanet(catalog, Asset(6), Tag(6), plan.Edits[DirectorAuthoring.SolarResource], request.PlanetSize);
        CheckMapArt(Asset(8), Tag(8));
        // EDZ's map image (300, 500, 2710 x 2377 px) sits inside the 3174 x 2874 px container the transform spans.
        byte[] transform = Asset(11);
        float sx = request.MapSize.X / 2710, sy = request.MapSize.Y / 2377;
        bool Near(int at, float expected) => MathF.Abs(BinaryPrimitives.ReadSingleLittleEndian(transform.AsSpan(at)) - expected) < 0.05f;
        WorldBuild.Check(Near(16, request.MapOrigin.X - 300 * sx) && Near(20, request.MapOrigin.Y + 500 * sy) && Near(64, 3174 * sx)
            && Near(68, 2874 * sy), "The map transform does not put the image on the map rectangle.");
        // A smaller planet scales the sphere, its rings and its caption offset together.
        var small = DirectorAuthoring.Plan(catalog, Read, schema, request with { PlanetSize = 0.2f }, Package, First);
        CheckPlanet(catalog, small.Assets.Single(asset => asset.Native!.EntryIndex == First + 6).Data, Tag(6),
            small.Edits[DirectorAuthoring.SolarResource], 0.2f);

        foreach (var (tag, bytes) in plan.Edits)
            _ = TagDocument.Parse(bytes, catalog.TryGetEntry(tag, out var entry) ? entry!.Reference : 0, schema);
        var edits = new Dictionary<uint, byte[]>(activity.Edits);
        foreach (var (tag, bytes) in plan.Edits) edits[tag] = bytes;
        Build(catalog, schema, edits);
        Console.WriteLine("Director checks passed.");
    }

    /**
     * The project build merges every edited table as a table, and records each index a new row holds
     * into another table's added rows as a site.
     */
    private static void Build(PackageCatalog catalog, RegistryIndex schema, IReadOnlyDictionary<uint, byte[]> edits)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "sunrise-director-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = SunriseStudio.Projects.StudioProject.Create(Path.Combine(scratch, "project"), "Testbed");
            project.ApplyEdits(edits, catalog);
            project.Build(catalog, tagSchema: schema);
            string dump = Directory.EnumerateFiles(scratch, "merge-sidecars.json", SearchOption.AllDirectories).Single();
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(dump));
            var entries = json.RootElement.GetProperty("sidecars").EnumerateArray()
                .SelectMany(x => x.GetProperty("entries").EnumerateArray()).ToDictionary(x => x.GetProperty("tag").GetString()!);
            // The top page is an interface file; no recipe merges those, so it ships whole.
            var whole = new HashSet<uint> { DirectorAuthoring.SolarResource, DirectorAuthoring.SolarHierarchy };
            foreach (uint tag in edits.Keys)
            {
                var entry = entries[tag.ToString("X8")];
                string expected = whole.Contains(tag) ? "Opaque" : "Table";
                WorldBuild.Check(entry.GetProperty("kind").GetString() == expected,
                    $"Table 0x{tag:X8} is {entry.GetProperty("kind").GetString()}: {entry.GetProperty("reason")}.");
            }
            int Sites(uint tag, string rule) => entries[tag.ToString("X8")].GetProperty("sites").EnumerateArray()
                .Where(site => site.GetProperty("rule").GetString() == rule).Sum(site => site.GetProperty("count").GetInt32());
            WorldBuild.Check(Sites(DirectorAuthoring.SolarGraph, "graph") == 1 && Sites(ActivityRows.ActivityTextTable, "graph") == 1
                && Sites(ActivityRows.DestinationTextTable, "graph") == 1, "A new graph index is not a site.");
            WorldBuild.Check(Sites(DirectorAuthoring.Placements, "map node") == 3 && Sites(DirectorAuthoring.Placements, "activity") == 3,
                "A placement's map node or activity is not a site.");
            var groups = json.RootElement.GetProperty("pairGroups").EnumerateArray().Select(x => x.GetProperty("group").GetString()).ToHashSet();
            WorldBuild.Check(groups.IsSupersetOf(["activity", "destination", "graph", "map node"]),
                $"The build grows pair groups {string.Join(',', groups)}.");
            Console.WriteLine($"world_director_build tables={edits.Count} kinds=Table groups={string.Join(',', groups.Order())}");
        }
        finally
        {
            try { Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void CheckIndex(byte[] table, uint hash, uint tag)
    {
        var rows = TagArray.Checked(table, 8, stride: 24);
        int last = rows.Data + (rows.Count - 1) * 24;
        WorldBuild.Check(rows.Count == 27 && U32(table, last) == hash && U32(table, last + 16) == tag, "An index lacks the new graph row.");
    }

    private static void CheckTile(byte[] solar, DirectorPlan plan)
    {
        var rows = TagArray.Checked(solar, 64, stride: 64);
        int row = rows.Data + (rows.Count - 1) * 64;
        var entries = TagArray.Checked(solar, row + 8, stride: 24);
        WorldBuild.Check(rows.Count == 16 && U32(solar, row) == plan.TileHash && entries.Count == 1 && solar[entries.Data] == plan.GraphIndex
            && BinaryPrimitives.ReadUInt16LittleEndian(solar.AsSpan(entries.Data + 2)) == 0xFFFF
            && U32(solar, row + 44) == Name.Hash && U32(solar, row + 52) == Description.Hash && U32(solar, row + 60) == Short.Hash,
            "The top page's tile row does not open the new graph.");
        var edz = TagArray.Checked(solar, rows.Data + 64 + 8, stride: 24);
        WorldBuild.Check(solar[edz.Data] == 2, "EDZ's tile row changed.");
    }

    private static void CheckGraphs(byte[] display, byte[] definition, uint transform, int activity)
    {
        var nodes = TagArray.Checked(display, 80, stride: 80);
        WorldBuild.Check(nodes.Count == 1 && U32(display, 144) == transform, "The display graph is not one node over the new transform.");
        // The node lists one activity row, not EDZ's four, and names itself after the destination and its first zone.
        WorldBuild.Check(TagArray.Checked(display, nodes.Data, stride: 8).Count == 1
            && U32(display, nodes.Data + 60) == Name.Bank && U32(display, nodes.Data + 64) == Name.Hash
            && U32(display, nodes.Data + 72) == 0x5555AA00, "The destination's map node keeps EDZ's patrol look.");
        uint node = U32(display, nodes.Data + 32);
        var definitionNodes = TagArray.Checked(definition, 8, stride: 40);
        var activities = TagArray.Checked(definition, definitionNodes.Data + 8, stride: 24);
        WorldBuild.Check(definitionNodes.Count == 1 && U32(definition, definitionNodes.Data) == node && activities.Count == 1
            && U32(definition, activities.Data) == node && BinaryPrimitives.ReadInt32LittleEndian(definition.AsSpan(activities.Data + 4)) == activity,
            "The definition graph's node does not list the destination's activity.");
    }

    private static void CheckLinked(byte[] table, int field, string what)
    {
        var entries = TagArray.Checked(table, field, stride: 24);
        WorldBuild.Check(entries.Count == 1 && table[entries.Data] == 26, $"The {what} row does not name the new graph.");
    }

    private static void CheckZones(DirectorPlan plan, IReadOnlyList<DirectorZone> zones, int activity)
    {
        byte[] exe = plan.Edits[DirectorAuthoring.MapNodes], client = plan.Edits[DirectorAuthoring.MapNodeTexts];
        byte[] placements = plan.Edits[DirectorAuthoring.Placements];
        var exeRows = TagArray.Checked(exe, 8, stride: 72);
        var clientRows = TagArray.Checked(client, 8, stride: 24);
        var placementRows = TagArray.Checked(placements, 8, stride: 40);
        WorldBuild.Check(exeRows.Count == 956 && clientRows.Count == 956 && placementRows.Count == 930, "The zone tables lack three rows each.");
        for (int zone = 0; zone < zones.Count; zone++)
        {
            int row = exeRows.Data + (953 + zone) * 72, text = clientRows.Data + (953 + zone) * 24;
            var locations = TagArray.Checked(exe, row + 56, stride: 80);
            var entries = TagArray.Checked(client, text + 8, stride: 28);
            int place = placementRows.Data + (927 + zone) * 40;
            WorldBuild.Check(U32(exe, row) == plan.ZoneHashes[zone] && U32(client, text) == plan.ZoneHashes[zone]
                && locations.Count == 1 && U32(exe, locations.Data + 48) == StringBankWriter.Hash("testbed")
                && U32(exe, locations.Data + 52) == zones[zone].BubbleHash
                // The location's destination row (+16) picks the map page; the testbed is row 48.
                && exe[locations.Data + 16] == 48
                && BinaryPrimitives.ReadSingleLittleEndian(exe.AsSpan(locations.Data + 32)) == zones[zone].Position.X
                && entries.Count == 1 && U32(client, entries.Data + 20) == zones[zone].SpawnSetHash
                && U32(client, entries.Data + 16) == zones[zone].Action.Hash
                && BinaryPrimitives.ReadInt64LittleEndian(placements.AsSpan(place)) == 953 + zone
                && U32(placements, place + 24) == DirectorAuthoring.FastTravelKind
                && BinaryPrimitives.ReadInt64LittleEndian(placements.AsSpan(place + 32)) == activity,
                $"Landing zone {zone} does not read back.");
        }
    }

    private static void CheckPages(PackageCatalog catalog, DirectorPlan plan, byte[] tile, byte[] pageHierarchy, byte[] page,
        uint planet, uint map, uint tileTag, uint pageHierarchyTag, uint pageTag)
    {
        var tileDocument = CuiDocument.Read(tileTag, CuiResource.Class, tile);
        WorldBuild.Check(Values(tileDocument, tile).Contains(plan.TileHash) && Values(tileDocument, tile).Contains(planet)
            && !Values(tileDocument, tile).Contains(0x97F35C05), "The tile does not name its row and planet texture.");
        WorldBuild.Check(TagView.At<SCuiHierarchy>(pageHierarchy).CuiResource == pageTag, "The map page hierarchy names another resource.");
        var pageDocument = CuiDocument.Read(pageTag, CuiResource.Class, page);
        WorldBuild.Check(Values(pageDocument, page).Contains(plan.GraphHash) && Values(pageDocument, page).Contains(map)
            && !Values(pageDocument, page).Contains(0x06882FD0), "The map page does not name the new graph and map image.");

        foreach (uint library in DirectorAuthoring.ArtLibraries)
        {
            byte[] bytes = plan.Edits[library];
            var rows = TagArray.Checked(bytes, 16, stride: 24);
            // The new row is appended, and its key sorts after every installed one.
            var keys = Enumerable.Range(0, rows.Count).Select(index => U32(bytes, rows.Data + index * 24)).ToArray();
            var entries = TagArray.Checked(bytes, rows.Data + 66 * 24 + 8, stride: 8);
            WorldBuild.Check(rows.Count == 67 && keys.Zip(keys.Skip(1)).All(pair => pair.First < pair.Second)
                && entries.Count == 1 && U32(bytes, entries.Data + 4) == pageHierarchyTag,
                "An art library lacks the map page row, or an installed row moved.");
            // The caption looks its name up in the library's string containers, so the name's bank joins them.
            var containers = TagArray.Checked(bytes, 32, stride: 8);
            var bankRows = InvestmentItemClone.ReadBankRows(catalog.ReadOriginal(new InvestmentSources().StringBanks));
            WorldBuild.Check(containers.Count == 8 && U32(bytes, containers.Data + 7 * 8) == bankRows.Single(row => row.Index == Name.Bank).ContainerTag
                && U32(bytes, containers.Data + 2 * 8) == 0x80B46D69, "An art library does not list the name's string bank.");
        }

        var program = CuiDocument.Read(DirectorAuthoring.SolarHierarchy, SCuiHierarchy.Class, plan.Edits[DirectorAuthoring.SolarHierarchy]);
        var top = CuiDocument.Read(DirectorAuthoring.SolarResource, CuiResource.Class, plan.Edits[DirectorAuthoring.SolarResource]);
        // Nodes are numbered in build order: after the last installed import at 4231 come copies of EDZ's
        // parallax layer (3155 to 3161) at 4232 to 4238, its anchor (3680) at 4239, and its tile (3162 to 3291) at 4240 to 4369.
        var layer = top.Imports[^3];
        var anchor = top.Imports[^2];
        var added = top.Imports[^1];
        WorldBuild.Check(program.Hierarchy.Count == 4269 + 138 && program.Hierarchy.Select(edge => (int)edge.Id).Distinct().Count() == 4407
            && program.Hierarchy.All(edge => edge.Id < 4407) && layer.TagHash == 0x80BC6829 && layer.FirstNode == 4231 && layer.NodeCount == 7
            && anchor.TagHash == 0x80BC6907 && anchor.FirstNode == 4238 && anchor.NodeCount == 1
            && added.TagHash == tileTag && added.FirstNode == 4239 && added.NodeCount == 130,
            "The top page does not build the new layer, anchor and tile after its last import.");
        var parents = program.Hierarchy.ToDictionary(edge => (int)edge.Id, edge => (int)edge.Parent);
        WorldBuild.Check(program.Hierarchy.Count(edge => edge.Id is >= 4240 and <= 4369 && edge.Parent is < 4240 or > 4369) == 1
            && parents[4240 + (3229 - 3162)] == 4238 && parents[4239] == 4238 && parents[3680] == 3161
            && parents[4232] == parents[3155] && parents[4238] == 4232 && parents[3229] == 3161,
            "The new tile does not hang in its own parallax layer.");
        // The panel (node 1882) drives the layers it lists by anchor, in depth order; the new one joins between 1.05 and 1.08.
        byte[] topEdited = plan.Edits[DirectorAuthoring.SolarResource];
        var panel = top.Objects.Where(owner => owner.IsDefaultProfile).SelectMany(owner => owner.Components).Last(row => row.Key == 1882);
        var list = panel.Properties.Where(row => row.Path.Count == 2 && row.Path[0] == 0x600 && (row.Path[1] & 0xFFFF) == 6)
            .OrderBy(row => row.Path[1] >> 16).Select(row => (int)BinaryPrimitives.ReadUInt16LittleEndian(topEdited.AsSpan(row.ValueOffset))).ToArray();
        int listCount = BinaryPrimitives.ReadInt32LittleEndian(topEdited.AsSpan(panel.Properties.Last(row => row.Path is [0x600, 0]).ValueOffset));
        WorldBuild.Check(listCount == 8 && list.SequenceEqual([2320, 2871, 3154, 4239, 3680, 3688, 3696, 3704]),
            $"The parallax panel lists {listCount}: {string.Join(',', list)}.");
        // The layer runs at its own depth, and its edge sits among the stock layers by depth: after 1.05 (2872), before 1.08 (3155).
        var order = program.Hierarchy.Select(edge => (int)edge.Id).ToList();
        WorldBuild.Check(Value(top, plan.Edits[DirectorAuthoring.SolarResource], 4233, 0x206) == DirectorAuthoring.PlanetDepth
            && Value(top, plan.Edits[DirectorAuthoring.SolarResource], 3156, 0x206) == 1.08f
            && order.IndexOf(2872) < order.IndexOf(4232) && order.IndexOf(4232) < order.IndexOf(3155),
            "The new parallax layer has the wrong depth or draw order.");
        var stock = CuiDocument.Read(DirectorAuthoring.SolarResource, CuiResource.Class, catalog.ReadOriginal(DirectorAuthoring.SolarResource));
        var stockProgram = CuiDocument.Read(DirectorAuthoring.SolarHierarchy, SCuiHierarchy.Class,
            catalog.ReadOriginal(DirectorAuthoring.SolarHierarchy));
        WorldBuild.Check(top.Components.Count(row => row.Key is >= 4232 and <= 4369) == stock.Components.Count(row => row.Key is >= 3155 and <= 3291 || row.Key == 3680),
            "The new layer and slot lack the page's overrides for EDZ's.");
        WorldBuild.Check(top.Nodes.Select(node => (int)node.Id).SequenceEqual(stock.Nodes.Select(node => node.Id + 138)),
            "The page's own nodes did not move past the new imports.");
        WorldBuild.Check(stockProgram.Hierarchy.Where(edge => edge.Id >= 4232).All(edge => parents[edge.Id + 138]
            == (edge.Parent is >= 4232 and < 0x7FFF ? edge.Parent + 138 : edge.Parent)), "A page-local edge lost its parent.");
        // The panel (node 1882) binds graph 19's hash into the tile's row component, EDZ's node 3275.
        byte[] topBytes = plan.Edits[DirectorAuthoring.SolarResource];
        var bindings = TagArray.Checked(topBytes, TagView.FieldOffset<CuiResource>("BindingSet.Bindings"), CuiBindingRow.Class,
            Unsafe.SizeOf<CuiBindingRow>());
        var bound = Enumerable.Range(0, bindings.Count)
            .Select(index => TagView.At<CuiBindingRow>(topBytes, bindings.Data + index * Unsafe.SizeOf<CuiBindingRow>())).ToArray();
        WorldBuild.Check(bound.Length == 23 && bound[^1].SourceNode == 1882 && bound[^1].TargetNode == 4240 + (3275 - 3162)
            && bound[^1].TargetNodeCopy == bound[^1].TargetNode && bound[^1].SourceSelector == 3 && bound[^1].TargetSelector == 1,
            "The new tile's row component is not bound to the page's graph.");
    }

    /**
     * The new tile draws only the given planet: Earth's clouds are hidden, the material has no tint
     * weight, the caption names the destination, and the sphere, rings and offsets follow `size`.
     */
    private static void CheckPlanet(PackageCatalog catalog, byte[] tile, uint tileTag, byte[] top, float size)
    {
        byte[] stockBytes = catalog.ReadOriginal(DirectorAuthoring.TileTemplate);
        var stock = CuiDocument.Read(DirectorAuthoring.TileTemplate, CuiResource.Class, stockBytes);
        var document = CuiDocument.Read(tileTag, CuiResource.Class, tile);
        float scale = size / Value(stock, stockBytes, 118, 0x208);
        // Written lengths sit on whole pixels, so each is within half a pixel of the exact scale.
        bool Near(float actual, float expected) => MathF.Abs(actual - expected) <= 0.5f / 1080 + 1e-6f;
        bool Whole(float length) => MathF.Abs(length * 1080 - MathF.Round(length * 1080)) < 1e-3f;
        WorldBuild.Check(new ulong[] { 71, 72, 73, 96, 102, 107, 108, 110, 117, 118, 121, 122, 125, 126, 127, 128, 129 }.All(node => document.Objects
            .Where(owner => owner.IsDefaultProfile).SelectMany(owner => owner.Components).Where(row => row.Key == node).SelectMany(row => row.Properties)
            .Where(row => row.PoolDescriptor == CuiDocument.FloatsB).All(row => Whole(BinaryPrimitives.ReadSingleLittleEndian(tile.AsSpan(row.ValueOffset))))),
            "A planet length is off the pixel grid.");
        WorldBuild.Check(Near(Value(document, tile, 118, 0x208), size) && Near(Value(document, tile, 118, 0x209), size),
            $"The sphere is not {size} high.");
        foreach (var (node, path) in new (ulong, uint[])[] { (117, [0x206]), (125, [0x210, 2, 1]), (128, [0x208]), (107, [0x210, 2, 1]),
            (108, [0x210, 2, 0]), (96, [0x208]) })
            WorldBuild.Check(Near(Value(document, tile, node, path), Value(stock, stockBytes, node, path) * scale),
                $"Node {node} {string.Join('/', path.Select(x => x.ToString("X")))} did not scale with the planet.");
        WorldBuild.Check(Near(Value(document, tile, 72, 0x208), size) && Near(Value(document, tile, 72, 0x210, 3, 0), size)
            && Near(Value(document, tile, 72, 0x210, 4, 1), size), "Node 72 keeps EDZ's 960 x 800 size limits.");
        WorldBuild.Check(Value(document, tile, 122, 0x205) == 0 && Value(document, tile, 122, 0x208) == 0
            && tile[Property(document, 122, 0x201).ValueOffset] == 0, "Earth's cloud layer still draws.");
        WorldBuild.Check(Value(document, tile, 119, 0x214) == 0
            && BinaryPrimitives.ReadSingleLittleEndian(tile.AsSpan(Property(document, 119, 0x210).ValueOffset + 12)) == 0,
            "The planet material keeps Earth's tint weight.");
        WorldBuild.Check(U32(tile, Property(document, 107, 0x405).ValueOffset) == Name.Hash && !Values(document, tile).Contains(0xC2414AE4),
            "The caption still reads Earth.");

        // The top page sizes Earth's ring halves (EDZ node 3287) itself; the copy (4365) scales, EDZ's does not.
        byte[] stockTop = catalog.ReadOriginal(DirectorAuthoring.SolarResource);
        var stockPage = CuiDocument.Read(DirectorAuthoring.SolarResource, CuiResource.Class, stockTop);
        var page = CuiDocument.Read(DirectorAuthoring.SolarResource, CuiResource.Class, top);
        float ring = Value(stockPage, stockTop, 3287, 0x208);
        WorldBuild.Check(Near(Value(page, top, 4240 + 3287 - 3162, 0x208), ring * scale) && Value(page, top, 3287, 0x208) == ring,
            "The top page's ring size did not follow the planet, or EDZ's moved.");
    }

    /** The map page keeps none of EDZ's place names, overlay art or cloud layers, and names the destination. */
    private static void CheckMapArt(byte[] page, uint pageTag)
    {
        var document = CuiDocument.Read(pageTag, CuiResource.Class, page);
        uint[] places = [0xE6231953, 0x83DEC359, 0xFA5BA353, 0xE5C5ADF0, 0xB8459D59, 0x5F0646B6, 0x29DFF622, 0xD69ADAC7, 0xFAB4C6CF,
            0xDF617463];
        WorldBuild.Check(!Values(document, page).Overlaps(places), "The map page still names an EDZ place.");
        foreach (ulong node in (ulong[])[2163, 2165, 2166, 2169, 2175, 2177, 2188, 2190, 2194, 2196, 2197])
            WorldBuild.Check(Value(document, page, node, 0x205) == 0 && Value(document, page, node, 0x208) == 0,
                $"EDZ's map node {node} still draws.");
        WorldBuild.Check(U32(page, Property(document, 1945, 0x405).ValueOffset) == Name.Hash, "The map page title is not the destination.");
    }

    private static CuiProperty Property(CuiDocument document, ulong node, params uint[] path) => document.Objects
        .Where(owner => owner.IsDefaultProfile).SelectMany(owner => owner.Components).Where(row => row.Key == node)
        .SelectMany(row => row.Properties).Last(row => row.Path.SequenceEqual(path));

    private static float Value(CuiDocument document, byte[] bytes, ulong node, params uint[] path) =>
        BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(Property(document, node, path).ValueOffset));

    /** The rows of the array at `field` read the same in both tables. */
    private static void CheckSame(byte[] before, byte[] after, Func<byte[], int> field, int stride, string what)
    {
        var old = TagArray.Checked(before, field(before), stride: stride);
        var now = TagArray.Checked(after, field(after), stride: stride);
        WorldBuild.Check(old.Count == now.Count && before.AsSpan(old.Data, old.Count * stride).SequenceEqual(after.AsSpan(now.Data, now.Count * stride)),
            $"{what} changed.");
    }

    private static HashSet<uint> Values(CuiDocument document, byte[] bytes) => document.Properties
        .Where(property => property.Fields.Count == 1 && property.ValueOffset >= 0 && property.ValueOffset + 4 <= bytes.Length)
        .Select(property => U32(bytes, property.ValueOffset)).ToHashSet();

    private static RgbaImage Image(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int index = 0; index < width * height; index++) pixels[index * 4 + 3] = 255;
        return new(width, height, pixels);
    }

    private static uint U32(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
}
