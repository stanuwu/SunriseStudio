using System.Text.Json.Nodes;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Tft;
using SunriseStudio.Tags.Views;
using SunriseStudio.World;
using SunriseStudio.World.Sdk;

/**
 * Bubbles added to the EDZ copy: a copy of Trostland after the last live row, one of an interior in
 * a row EDZ leaves empty, and one in a row the cut emptied. Each loads, builds and reaches the SDK.
 */
static class BubbleChecks
{
    private const string Destination = "bubblebed";
    // Trostland, Widow's Walk and the Atrium in EDZ: rows 51, 54 and 52.
    private static readonly uint[] Kept = [0xB8459D59, 0xC0064AEF, 0xC0064AED];
    // Rows 16 to 31 and 45 of edz_freeroam have no states and no bubble record.
    private const int EmptyEdzRow = 20;
    // Row 0 is a bubble the cut dropped.
    private const int CutRow = 0;

    public static void Run(string gameRoot)
    {
        using var catalog = PackageCatalog.Open(gameRoot);
        var schema = StudioRegistry.Index;
        uint scenario = WorldBuild.Named(catalog, "edz_freeroam:scenario_client");
        string scratch = Path.Combine(Path.GetTempPath(), "sunrise-bubbles-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = StudioProject.Create(Path.Combine(scratch, "project"), "Bubbles");
            string content = project.ContentDirectory;
            WorldTemplate.Create(content, catalog, schema, scenario, Kept.ToHashSet(), Destination);
            var before = WorldFiles.Load(content, Destination);
            uint town = Renamed(51), atrium = Renamed(52);
            int rowsBefore = Rows(before).Count, liveBefore = WorldContent.LiveBubbles(before).Count;

            var outdoor = WorldBubbles.AddBubble(content, Destination, town, "bubblebed_outdoor");
            var interior = WorldBubbles.AddBubble(content, Destination, atrium, "bubblebed_interior", EmptyEdzRow);
            var cut = WorldBubbles.AddBubble(content, Destination, atrium, "bubblebed_cut", CutRow);
            var world = WorldFiles.Load(content, Destination);
            WorldBuild.Check(outdoor.Row == rowsBefore && interior.Row == EmptyEdzRow && cut.Row == CutRow,
                $"The bubbles took rows {outdoor.Row}, {interior.Row} and {cut.Row}.");
            WorldBuild.Check(WorldContent.LiveBubbles(world).Count == liveBefore + 3 && Rows(world).Count == rowsBefore + 1,
                "The scenario did not gain three live rows.");
            Refusals(world, town);
            SpawnSets(world, [outdoor, interior, cut]);
            foreach (var (added, template) in new[] { (outdoor, town), (interior, atrium), (cut, atrium) })
                Shape(world, added, template);

            // The strip removes host copies a new bubble took, and leaves its spawn points. It runs in
            // memory only; the build below uses the unstripped files, which copy the most content.
            var stripped = WorldStrip.StripHost(world);
            WorldBuild.Check(!world.Files.Any(file => file.ClassId == SStaticMeshInstances.Class)
                && WorldContent.Spawns(world, outdoor.NameHash).Count > 0, "The strip kept a copied static or lost a new bubble's spawns.");
            Console.WriteLine($"world_bubbles_strip dropped_lists={stripped.DroppedLists} removed_files={stripped.RemovedFiles.Count}");
            world = WorldFiles.Load(content, Destination);

            var tags = WorldBuild.Compile(catalog, schema, world.Files, "bubble.checks");
            WorldBuild.Overlay(catalog, tags);
            try
            {
                uint scenarioTag = tags.Single(x => x.GameName == $"{Destination}:scenario_client").TagHash;
                uint mapTag = tags.Single(x => x.GameName == $"map:{Destination}:root").TagHash;
                var loaded = new SunriseStudio.Scene.WorldLoader(catalog).LoadMission(mapTag, scenarioTag);
                var bubbles = loaded.MapScene.Bubbles.Select(x => x.NameHash).ToHashSet();
                WorldBuild.Check(new[] { outdoor, interior, cut }.All(added => bubbles.Contains(added.NameHash)
                    && loaded.AllStates.Any(state => state.BubbleNameHash == added.NameHash)),
                    $"The loader does not list the new bubbles: {string.Join(',', bubbles.Select(x => $"0x{x:X8}"))}.");
                Console.WriteLine($"world_bubbles_load bubbles={bubbles.Count} states={loaded.AllStates.Count}");
            }
            finally
            {
                catalog.SetOverlayEntries([]);
                catalog.ReadOverride = null;
            }
            Build(catalog, schema, project, scratch, world, [outdoor, interior, cut]);
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }

    private static uint Renamed(int row) => StringBankWriter.Hash(WorldIdentity.BubbleName(Destination, row));

    private static List<JsonObject> Rows(WorldFiles world) =>
        ((JsonArray)world.Fields(world.Single(SScenario.Class).Name)["bubbles"]!).OfType<JsonObject>().ToList();

    /**
     * The sets a teleport may name, found by container mask as the server does. Trostland declares 21
     * sets, among them the arrival set 0x9617A6E7; the two interiors two each; a new bubble only its own.
     */
    private static void SpawnSets(WorldFiles world, IReadOnlyList<AddedBubble> added)
    {
        var town = WorldBubbles.SpawnSets(world, Renamed(51));
        var atrium = WorldBubbles.SpawnSets(world, Renamed(52));
        var walk = WorldBubbles.SpawnSets(world, Renamed(54));
        WorldBuild.Check(town.Count == 21 && town.Contains(0x9617A6E7) && atrium.SequenceEqual([0x2EA8FB98U, 0xD7BA9C17U])
            && walk.SequenceEqual([0xD7BA9C15U, 0xF8FFACDDU]),
            $"The stock spawn sets moved: {town.Count} in row 51, {string.Join(',', atrium.Select(x => $"0x{x:X8}"))} in row 52, "
            + $"{string.Join(',', walk.Select(x => $"0x{x:X8}"))} in row 54.");
        foreach (var item in added)
            WorldBuild.Check(WorldBubbles.SpawnSets(world, item.NameHash).SequenceEqual([item.SpawnSetHash])
                && !town.Concat(atrium).Concat(walk).Contains(item.SpawnSetHash),
                $"Row {item.Row} does not declare exactly its own set 0x{item.SpawnSetHash:X8}.");
        Console.WriteLine($"world_bubble_spawn_sets row51={town.Count} row52={atrium.Count} row54={walk.Count} added={added.Count}");
    }

    /** A live row, a taken name and a row past the map are refused, and the world is left unchanged. */
    private static void Refusals(WorldFiles world, uint template)
    {
        int count = world.Files.Count;
        foreach (var (row, name) in new (int?, string)[] { (51, "bubblebed_live"), (null, "bubblebed_outdoor"), (64, "bubblebed_far") })
        {
            bool refused = false;
            try { WorldBubbles.AddBubble(world, template, name, row); }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException or ArgumentException) { refused = true; }
            WorldBuild.Check(refused && world.Files.Count == count, $"AddBubble({row}, {name}) was not refused cleanly.");
        }
    }

    /** The row, its states and entries, the map rows and the copied content all name the new bubble. */
    private static void Shape(WorldFiles world, AddedBubble added, uint template)
    {
        var row = Rows(world)[added.Row];
        var states = ((JsonArray)row["states"]!).OfType<JsonObject>().ToList();
        WorldBuild.Check(WorldFiles.Hash(row["name_hash"]!) == added.NameHash && states.Count > 0 && states.All(state =>
                WorldFiles.Hash(state["bubble_name_hash"]!) == added.NameHash && WorldBubbles.Number(state["map_bubble_index"]) == added.RegionRow
                && WorldBubbles.Number(world.Fields(WorldFiles.Text(state["entry"])!)["index"]) == added.Row),
            $"Row {added.Row} does not name its bubble in every state.");
        var names = world.Fields(world.Single(SMapLocationNames.Class).Name);
        var region = (JsonObject)((JsonArray)names["slices"]!)[added.RegionRow]!;
        var set = (JsonObject)((JsonArray)names["slice_sets"]!)[added.Row]!;
        WorldBuild.Check(WorldBubbles.Number(region["owner_bubble"]) == added.Row && WorldFiles.Hash(set["bubble_name_hash"]!) == added.NameHash
            && set["h_0d537865"] is null or JsonArray { Count: 0 }, $"The map definition does not give row {added.Row} its own slice set.");
        var entry = (JsonObject)((JsonArray)world.Fields(world.Single(SMapRoot.Class).Name)["map_entries"]!)[added.Row]!;
        string parent = WorldFiles.Text(entry["wide_tag"])!;
        WorldBuild.Check(world[parent].ClassId == SBubbleParent.Class && WorldContent.Definition(world, added.NameHash) != WorldContent.Definition(world, template),
            $"Map root row {added.Row} does not name the new bubble's own parent.");
        // Every container the new bubble owns carries only its region bit; shared ones keep theirs.
        var own = WorldContent.Containers(world, added.NameHash).Where(name => added.Files.Contains(name)).ToList();
        string bit = Convert.ToHexStringLower(MaskOf(added.RegionRow));
        WorldBuild.Check(own.Count > 0 && own.All(name => WorldFiles.Text(world.Fields(name)["bubble_mask"]) == bit),
            $"A container of row {added.Row} carries another bubble's bit.");
        var spawns = WorldContent.Spawns(world, added.NameHash);
        WorldBuild.Check(spawns.Count > 0 && spawns.All(spawn => spawn.Identity == added.SpawnSetHash),
            $"Row {added.Row} has no spawn point of its own set 0x{added.SpawnSetHash:X8}.");
        WorldBuild.Check(!world.Reached(own).Any(name => world[name].ClassId == SMapPlacements.Class
                && (world.Fields(name)["placements"] as JsonArray ?? []).OfType<JsonObject>().Any(item => WorldFiles.Text(item["payload"]?["@class"]) == "0x80807246")),
            $"Row {added.Row} loads its template's visibility map.");
        WorldBuild.Check(added.Files.All(name => world[name].SourceTag is null), "A copy stands in for an installed tag.");
        Console.WriteLine($"world_bubble row={added.Row} region={added.RegionRow} slice_set={added.SliceSet} name=0x{added.NameHash:X8} "
            + $"spawn_set=0x{added.SpawnSetHash:X8} files={added.Files.Count} containers={own.Count} spawns={spawns.Count}");
    }

    private static byte[] MaskOf(int bit)
    {
        var mask = new byte[32];
        mask[bit / 8] = (byte)(1 << (bit % 8));
        return mask;
    }

    /** A build resolves each new map root row, and the SDK gives each new row its template's states at its own slice set. */
    private static void Build(PackageCatalog catalog, RegistryIndex schema, StudioProject project, string scratch, WorldFiles world,
        IReadOnlyList<AddedBubble> added)
    {
        var build = project.Build(catalog, tagSchema: schema);
        string packages = Path.Combine(scratch, "built", "packages");
        Directory.CreateDirectory(packages);
        foreach (string file in build.PackageFiles) File.Copy(file, Path.Combine(packages, Path.GetFileName(file)));
        int builtRows;
        using (var built = PackageCatalog.Open(Path.GetDirectoryName(packages)!))
        {
            // The client refuses a world whose scenario row count differs from the count the server sends.
            byte[] scenarioTag = built.Read(built.Names.Single(x => x.Value == $"{Destination}:scenario_client").Key);
            builtRows = TagView.Rows(scenarioTag, in TagView.Checked<SScenario>(scenarioTag, 0).Bubbles, ScenarioBubble.Class).Length;
            byte[] root = built.Read(built.Names.Single(x => x.Value == $"map:{Destination}:root").Key);
            var rows = TagView.Rows(root, in TagView.Checked<SMapRoot>(root, 0).MapEntries, MapBubbleRef.Class);
            var wide = new List<ulong>();
            foreach (ref readonly var row in rows) wide.Add(row.WideTag);
            int resolved = wide.Count(hash => built.Resolve64(hash) is not null);
            WorldBuild.Check(resolved == Kept.Length + added.Count && added.All(item => built.Resolve64(wide[item.Row]) is not null),
                $"{resolved} built map root rows resolve; {Kept.Length + added.Count} should.");
        }
        WorldBuild.Check(build.ActivitySdkPack is not null, "The build wrote no activity SDK pack.");
        using var pack = ActivitySdkPack.Open(build.ActivitySdkPack!);
        var scenario = pack.Rows<SdkScenario>(SdkSection.Scenarios).Single(row => pack.Text(row.Name) == Destination);
        var bubbles = pack.Rows<SdkBubble>(SdkSection.Bubbles).Skip((int)scenario.Bubbles.First).Take((int)scenario.Bubbles.Count).ToArray();
        var states = pack.Rows<SdkState>(SdkSection.States);
        WorldBuild.Check(builtRows == Rows(world).Count && bubbles.Length == builtRows,
            $"The built scenario has {builtRows} rows, the SDK {bubbles.Length}, the world {Rows(world).Count}.");
        foreach (var item in added)
        {
            var bubble = bubbles[item.Row];
            var own = Enumerable.Range((int)bubble.States.First, (int)bubble.States.Count).Select(index => states[index]).ToList();
            // The new row shares its template's registry, so both rows name one built tag set.
            int template = WorldBubbles.List(world.Single(SScenario.Class)).Single(row => row.Row == item.Row).Template;
            var source = bubbles[template];
            var shared = Enumerable.Range((int)source.States.First, (int)source.States.Count).Select(index => states[index].RegistryTag).ToList();
            WorldBuild.Check(bubble.NameHash == item.NameHash && own.Count == shared.Count && own.Select((state, index) =>
                    state.EntryIndex == item.Row && state.SliceSetIndex == item.SliceSet && state.MapBubbleIndex == item.RegionRow
                    && state.RegistryTag == shared[index] && !catalog.ContainsTag(state.RegistryTag)).All(x => x),
                $"The SDK row {item.Row} does not carry the new bubble's states.");
        }
        Console.WriteLine($"world_bubbles_sdk bubbles={bubbles.Length} live={bubbles.Count(row => row.States.Count > 0)} states={states.Length}");
    }
}
