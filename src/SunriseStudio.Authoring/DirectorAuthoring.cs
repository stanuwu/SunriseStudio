using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Views;
using SunriseStudio.World;

namespace SunriseStudio.Authoring;

/** One landing zone: a fast-travel point on the destination map. */
public sealed record DirectorZone(TextReference Name, TextReference Action, uint BubbleHash, uint SpawnSetHash, Vector3 Position);

/**
 * What a destination needs in the Director: its planet tile and its map page.
 * @param ActivityIndex The free-roam activity row the landing zones launch.
 * @param MapOrigin World x, y of the map image's top-left corner.
 * @param MapSize World width and height the map image covers.
 * @param TilePosition The planet's centre on the top page, in 1080-pixel units from the top-left corner.
 * @param PlanetSize The sphere's diameter on the top page, in 1080-pixel units; its rings and caption scale with it.
 * @param PlanetDepth The planet's own parallax layer depth; the stock layers use 1.01 to 1.12, larger moves more.
 */
public sealed record DirectorRequest(string Destination, int ActivityIndex, TextReference Name, TextReference Description,
    TextReference ShortName, RgbaImage Planet, RgbaImage Map, Vector2 MapOrigin, Vector2 MapSize, Vector2 TilePosition,
    IReadOnlyList<DirectorZone> Zones, float PlanetSize = DirectorAuthoring.PlanetSize, float PlanetDepth = DirectorAuthoring.PlanetDepth);

/** New tags and installed-tag edits that put one destination in the Director. */
public sealed record DirectorPlan(IReadOnlyList<ProjectAssetAddition> Assets, IReadOnlyDictionary<uint, byte[]> Edits,
    int GraphIndex, uint GraphHash, uint TileHash, IReadOnlyList<uint> ZoneHashes);

/** Writes a new planet tile, destination graph, map page and landing zones; installed rows are never reused. */
public static partial class DirectorAuthoring
{
    /** A new planet's default sphere diameter, in 1080-pixel units; EDZ's is 0.285. */
    public const float PlanetSize = 0.3f;
    /** A new planet's default parallax depth: between Mars's 1.05 and EDZ's 1.08 layer, and unused by any stock layer. */
    public const float PlanetDepth = 1.06f;
    /** Tags a new entry claims, in order. */
    public const int EntryCount = 12;

    /**
     * Plans every tag the Director needs for one destination.
     * @param read Reads a tag with the project's earlier edits applied, including a new string bank's index row.
     * @param firstEntry First of EntryCount free entries in the package.
     */
    public static DirectorPlan Plan(PackageCatalog catalog, Func<uint, byte[]> read, RegistryIndex schema, DirectorRequest request,
        ushort packageId, int firstEntry)
    {
        if (request.Zones.Count == 0) throw new InvalidDataException("A destination map needs at least one landing zone.");
        if (firstEntry < 0 || firstEntry > TagHashes.EntriesPerPackage - EntryCount)
            throw new InvalidDataException($"The package needs {EntryCount} free entries for a Director node.");
        uint Tag(int index) => TagHashes.Make(packageId, firstEntry + index);
        string stem = $"director_{request.Destination}";
        var edits = new Dictionary<uint, byte[]>();
        byte[] Current(uint tag) => edits.TryGetValue(tag, out var edited) ? edited : read(tag);

        // Hashes the new rows use; none may collide with an installed row.
        var used = Hashes(Current(GraphIndexTag), RowStride).Concat(Hashes(Current(SolarGraph), LinkedStride, LinkedField))
            .Concat(Hashes(Current(MapNodes), MapNodeStride)).ToHashSet();
        uint graphHash = StringHash.NewDefinitionHash($"sunrise:director:graph:{request.Destination}", used);
        uint tileHash = StringHash.NewDefinitionHash($"sunrise:director:tile:{request.Destination}", used);
        uint nodeHash = StringHash.NewDefinitionHash($"sunrise:director:node:{request.Destination}", used);
        uint artKey = ArtKey(request.Destination, ArtLibraries.SelectMany(library => ArtKeys(Current(library))).Max());
        var zoneHashes = request.Zones.Select((_, index) =>
            StringHash.NewDefinitionHash($"sunrise:director:zone:{request.Destination}:{index}", used)).ToArray();

        var assets = new List<ProjectAssetAddition>();
        // Entries 0-1 planet texture, 2 planet bitmap, 3-4 map texture, 5 map bitmap, 6 tile, 7-8 map page,
        // 9 display graph, 10 definition graph, 11 map transform.
        uint planetBitmap = Bitmap(catalog, assets, request.Planet, $"{stem}.planet", packageId, firstEntry, PlanetBitmapTemplate);
        uint mapBitmap = Bitmap(catalog, assets, request.Map, $"{stem}.map", packageId, firstEntry + 3, MapBitmapTemplate);

        int graphIndex = new TagArray(Current(GraphIndexTag), RootField).HasHeader
            ? TagArray.Checked(Current(GraphIndexTag), RootField, stride: RowStride).Count : 0;
        if (graphIndex >= MaximumGraphs) throw new InvalidDataException($"The client holds at most {MaximumGraphs} destination graphs.");

        float scale = PlanetScale(read(TileTemplate), request.PlanetSize);
        assets.Add(Copy(catalog, TileTemplate, $"{stem}.tile", packageId, firstEntry + 6,
            Tile(read(TileTemplate), read(TileBase), tileHash, planetBitmap, request, scale)));
        var (pageHierarchy, pageResource) = (Tag(7), Tag(8));
        assets.Add(Copy(catalog, MapHierarchyTemplate, $"{stem}.map_page", packageId, firstEntry + 7,
            PageHierarchy(read(MapHierarchyTemplate), pageResource)));
        assets.Add(Copy(catalog, MapResourceTemplate, $"{stem}.map_page_resource", packageId, firstEntry + 8,
            PageResource(read(MapResourceTemplate), graphHash, mapBitmap, request.Name)));
        uint transform = Tag(11);
        assets.Add(Copy(catalog, DisplayGraphTemplate, $"{stem}.graph", packageId, firstEntry + 9,
            DisplayGraph(read(DisplayGraphTemplate), schema, request, artKey, nodeHash, transform)));
        assets.Add(Copy(catalog, DefinitionGraphTemplate, $"{stem}.graph_definition", packageId, firstEntry + 10,
            DefinitionGraph(read(DefinitionGraphTemplate), schema, nodeHash, request.ActivityIndex)));
        assets.Add(Copy(catalog, TransformTemplate, $"{stem}.map_transform", packageId, firstEntry + 11,
            Transform(read(TransformTemplate), read(MapResourceTemplate), request.MapOrigin, request.MapSize)));

        edits[GraphIndexTag] = IndexRow(Current(GraphIndexTag), GraphIndexClass, schema, graphHash, Tag(9));
        edits[DefinitionIndexTag] = IndexRow(Current(DefinitionIndexTag), DefinitionIndexClass, schema, graphHash, Tag(10));
        edits[SolarGraph] = LinkedRow(Current(SolarGraph), schema, tileHash, graphIndex, request);
        uint textBank = TextContainer(Current(new InvestmentSources().StringBanks), request.Name);
        foreach (uint library in ArtLibraries)
            edits[library] = ArtStrings(ArtRow(Current(library), schema, artKey, pageHierarchy), schema, textBank);
        var (page, hierarchy) = SolarSlot(Current(SolarResource), Current(SolarHierarchy), schema, Tag(6), scale,
            request.PlanetDepth);
        edits[SolarResource] = page;
        edits[SolarHierarchy] = hierarchy;
        int destination = ActivityRows.DestinationOf(Current(ActivityRows.ActivityTable), request.ActivityIndex);
        int firstNode = TagArray.Checked(Current(MapNodes), RootField, stride: MapNodeStride).Count;
        edits[MapNodes] = ExeZones(Current(MapNodes), schema, request, zoneHashes, destination);
        edits[MapNodeTexts] = ClientZones(Current(MapNodeTexts), schema, request, zoneHashes);
        edits[Placements] = PlacementRows(Current(Placements), schema, firstNode, request);
        // The activity's own list wins; the destination row is the client's fallback, so both name the graph.
        edits[ActivityRows.ActivityTextTable] = ActivityGraph(Current(ActivityRows.ActivityTextTable), schema,
            RootClass(catalog, ActivityRows.ActivityTextTable), request.ActivityIndex, graphIndex);
        edits[ActivityRows.DestinationTextTable] = DestinationGraph(Current(ActivityRows.DestinationTextTable), schema,
            RootClass(catalog, ActivityRows.DestinationTextTable), destination, graphIndex);
        return new(assets, edits, graphIndex, graphHash, tileHash, zoneHashes);
    }
}
