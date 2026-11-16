using System.Diagnostics;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Tft;
using SunriseStudio.Tags.Views;
using SunriseStudio.World;
using SunriseStudio.World.Sdk;

/**
 * The EDZ free-roam scenario and map root, written as project `.tft` files and compiled as a build
 * does, keep no reference to the tags they copy. Cut to three bubbles and renamed, they load as
 * their own destination and build into a package.
 */
static class TemplateChecks
{
    private const string ModId = "worldauthoring-tests";
    // Trostland, Widow's Walk and the Atrium in EDZ.
    private static readonly uint[] Kept = [0xB8459D59, 0xC0064AEF, 0xC0064AED];
    // The testbed scenario's row count, which the SDK pack must repeat.
    private static int ExpectedRows;
    // The kept bubbles' hashes after the copy renamed every row.
    private static HashSet<uint> RenamedKept = [];

    private static uint Renamed(int row) => StringBankWriter.Hash(WorldIdentity.BubbleName("testbed", row));

    /** The four fields that name a bubble carry only the copy's names; the kept EDZ hashes are gone from them. */
    private static void BubbleNames(WorldFiles world, IReadOnlySet<uint> renamed)
    {
        var parents = world.Files.Where(x => x.ClassId == SBubbleParent.Class).Select(x => WorldFiles.Hash(
            ((System.Text.Json.Nodes.JsonObject)world.Fields(x.Name)["metadata"]!)["bubble_name_hash"]!)).ToHashSet();
        var sets = ((System.Text.Json.Nodes.JsonArray)world.Fields(world.Single(SMapLocationNames.Class).Name)["slice_sets"]!)
            .Select(x => WorldFiles.Hash(x!["bubble_name_hash"]!)).ToHashSet();
        WorldBuild.Check(parents.SetEquals(renamed) && renamed.All(sets.Contains) && !Kept.Any(sets.Contains),
            "A bubble parent or a map slice set keeps an EDZ bubble name.");
    }

    public static void Run(string gameRoot)
    {
        var clock = Stopwatch.StartNew();
        using var catalog = PackageCatalog.Open(gameRoot);
        var schema = StudioRegistry.Index;
        StaticChecks.Dependencies(catalog);
        uint scenario = WorldBuild.Named(catalog, "edz_freeroam:scenario_client");
        uint map = WorldTemplate.MapRootOf(catalog, schema, scenario);
        WorldBuild.Check(WorldTemplate.DevName(catalog, schema, scenario) == "edz" && map == WorldBuild.Named(catalog, "map:edz:root"),
            "The map root does not follow the scenario's dev name.");
        var bubbles = WorldTemplate.Bubbles(catalog, scenario, map);
        WorldBuild.Check(bubbles.Count == 64 && bubbles.Count(x => x.Copyable) == 47 && Kept.All(x => bubbles.Any(b => b.NameHash == x && b.Copyable))
            && bubbles.Single(x => x.NameHash == Kept[0]).SliceSet == 51, "The template bubble list is wrong.");

        var files = WorldTemplate.Files(catalog, schema, "edz", [scenario, map]);
        var copied = files.Select(WorldBuild.Source).ToHashSet();
        WorldBuild.Check(copied.Contains(scenario) && copied.Contains(map), "The roots were not copied.");
        var tags = WorldBuild.Compile(catalog, schema, files, ModId);
        WorldBuild.Check(tags.Count == files.Count, "A file did not compile.");
        var (wide, stock, classes) = WorldBuild.Verify(catalog, schema, tags, copied);
        Console.WriteLine($"world_template files={files.Count} project_wide_references={wide} stock_references={stock} "
            + $"stock_classes={classes} seconds={clock.Elapsed.TotalSeconds:F0}");

        Testbed(catalog, schema, scenario, copied);
    }

    private static void Testbed(PackageCatalog catalog, RegistryIndex schema, uint scenario, IReadOnlySet<uint> copied)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "sunrise-testbed-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = SunriseStudio.Projects.StudioProject.Create(Path.Combine(scratch, "project"), "Testbed");
            string content = project.ContentDirectory;
            var (folder, count) = WorldTemplate.Create(content, catalog, schema, scenario, Kept.ToHashSet(), "testbed");
            bool refused = false;
            try { WorldTemplate.Create(content, catalog, schema, scenario, Kept.ToHashSet(), "edz_freeroam"); }
            catch (InvalidOperationException) { refused = true; }
            WorldBuild.Check(refused, "A destination named like an installed one was created.");
            var files = Directory.EnumerateFiles(folder, "*" + TftNames.FileExtension, SearchOption.AllDirectories)
                .Select(path => TftFile.Read(File.ReadAllBytes(path))).ToList();
            var world = new WorldFiles(files);
            var scenarioFile = world.Single(SScenario.Class);
            var mapFile = world.Single(SMapRoot.Class);
            // The client's rule: the scenario's dev name picks the map root by name.
            WorldBuild.Check(files.Count == count && scenarioFile.GameName == "testbed:scenario_client"
                && WorldFiles.Text(world.Fields(scenarioFile.Name)["dev_name"]) == "testbed" && mapFile.GameName == "map:testbed:root",
                "The testbed names do not follow the client's lookup.");
            // The client loads scenario row k from map slice set k, so rows stay where EDZ has them:
            // Trostland is row 51, and the rows before it without states.
            var rows = (System.Text.Json.Nodes.JsonArray)world.Fields(scenarioFile.Name)["bubbles"]!;
            var withStates = new List<int>();
            for (int row = 0; row < rows.Count; row++)
                foreach (var state in ((System.Text.Json.Nodes.JsonArray)rows[row]!["states"]!).OfType<System.Text.Json.Nodes.JsonObject>())
                {
                    string entry = WorldFiles.Text(state["entry"])!;
                    WorldBuild.Check((uint)world.Fields(entry)["index"]! == (uint)row, $"{entry} does not name bubble row {row}.");
                    withStates.Add(row);
                }
            var source = WorldTemplate.Bubbles(catalog, scenario, WorldTemplate.MapRootOf(catalog, schema, scenario));
            var expected = Kept.Select(hash => source.ToList().FindIndex(row => row.NameHash == hash)).Order().ToArray();
            WorldBuild.Check(rows.Count == expected[^1] + 1 && withStates.Distinct().Order().SequenceEqual(expected)
                && expected.Contains(51) && WorldFiles.Hash(rows[51]!["name_hash"]!) == Renamed(51),
                $"The testbed scenario rows moved: {rows.Count} rows, states at {string.Join(',', withStates.Distinct())}.");
            ExpectedRows = rows.Count;
            RenamedKept = expected.Select(Renamed).ToHashSet();
            BubbleNames(world, RenamedKept);
            WorldBuild.Check(WorldContent.FirstLiveBubble(world) == Renamed(expected[0]),
                "The arrival bubble a bundle lands in is not the first row with states.");

            var statics = StaticChecks.Place(project, catalog, schema, Renamed(51));
            var placed = statics[0];
            uint collisionOwner = CollisionChecks.Placed(project, catalog, Renamed(51), statics.Count);
            OcclusionChecks.Placed(project, catalog, placed);
            files = Directory.EnumerateFiles(folder, "*" + TftNames.FileExtension, SearchOption.AllDirectories)
                .Select(path => TftFile.Read(File.ReadAllBytes(path))).ToList();
            WorldBuild.Check(files.Count == count + statics.Sum(x => x.Files.Count) + 2, "The placed statics did not add four files each, one collision list and one nav list.");
            uint covered = RenamedKept.First(hash => hash != Renamed(51));
            var far = FarChecks.Place(project, catalog, schema, Renamed(51), covered, RenamedKept, scratch);
            var sky = SkyChecks.Place(project, catalog, Renamed(51));
            var light = LightChecks.Place(project, catalog, Renamed(51));
            files = Directory.EnumerateFiles(folder, "*" + TftNames.FileExtension, SearchOption.AllDirectories)
                .Select(path => TftFile.Read(File.ReadAllBytes(path))).ToList();
            count = files.Count - statics.Sum(x => x.Files.Count) - 1;
            var tags = WorldBuild.Compile(catalog, schema, files, ModId);
            var (wide, stock, classes) = WorldBuild.Verify(catalog, schema, tags, copied);
            WorldBuild.Overlay(catalog, tags, StaticChecks.Models(project, catalog));
            uint scenarioTag = tags.Single(x => x.GameName == "testbed:scenario_client").TagHash;
            uint mapTag = tags.Single(x => x.GameName == "map:testbed:root").TagHash;
            var loader = new SunriseStudio.Scene.WorldLoader(catalog);
            var index = loader.BuildCatalog();
            WorldBuild.Check(index.Maps.Any(x => x.Tag == mapTag) && index.MissionsFor(mapTag).Any(x => x.Tag == scenarioTag),
                "The world index does not list the testbed map and scenario.");
            var loaded = loader.LoadMission(mapTag, scenarioTag);
            var bubbles = loaded.MapScene.Bubbles.Select(x => x.NameHash).ToHashSet();
            WorldBuild.Check(bubbles.SetEquals(RenamedKept), $"The testbed loads bubbles {string.Join(',', bubbles.Select(x => $"0x{x:X8}"))}.");
            WorldBuild.Check(loaded.AllStates.Count > 0 && loaded.AllStates.All(x => RenamedKept.Contains(x.BubbleNameHash))
                && loaded.Objects.Count > 0, "The testbed scenario lost its states or the map its objects.");
            Console.WriteLine($"world_testbed files={tags.Count} bubbles={bubbles.Count} states={loaded.AllStates.Count} "
                + $"objects={loaded.Objects.Count} project_wide_references={wide} stock_references={stock} stock_classes={classes}");
            StaticChecks.Loaded(catalog, loaded, tags, placed, Renamed(51));
            CollisionChecks.Loaded(catalog, loaded, tags, collisionOwner, statics);
            FarChecks.Loaded(loaded, tags, far, new WorldFiles(files), covered);
            SkyChecks.Loaded(loaded, sky.SkyObjects);
            LightChecks.Loaded(loaded, light.LightTable);
            catalog.SetOverlayEntries([]);
            catalog.ReadOverride = null;
            Build(catalog, project, tags.Count, scratch, placed, collisionOwner, statics.Count);
            BakeChecks.Run(project, catalog, collisionOwner);
            BundleChecks.Run(catalog, schema, project, scenario, Kept.ToHashSet());
            StripChecks.Run(catalog, schema, project, scratch);
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }

    /** The testbed saved into a project builds new packages that carry its names and resolve its bubbles. */
    private static void Build(PackageCatalog catalog, SunriseStudio.Projects.StudioProject project, int count, string scratch,
        PlacedBundleStatic placed, uint collisionOwner, int statics)
    {
        var build = project.Build(catalog, tagSchema: StudioRegistry.Index);
        WorldBuild.Check(build.TagFiles.Count == count, $"The project built {build.TagFiles.Count} of {count} world tags.");
        StaticChecks.Built(build, placed);
        CollisionChecks.Built(build, collisionOwner, statics);
        string packages = Path.Combine(scratch, "built", "packages");
        Directory.CreateDirectory(packages);
        foreach (string file in build.PackageFiles) File.Copy(file, Path.Combine(packages, Path.GetFileName(file)));
        using var built = PackageCatalog.Open(Path.GetDirectoryName(packages)!);
        uint mapTag = built.Names.Single(x => x.Value == "map:testbed:root").Key;
        WorldBuild.Check(built.Names.Count(x => x.Value == "testbed:scenario_client") == 1, "The built scenario has no game name.");
        // The client registers an activity by the tag named after it, before it loads the scenario.
        uint scenarioTag = built.Names.Single(x => x.Value == "testbed:scenario_client").Key;
        uint rootTag = built.Names.Single(x => x.Value == "testbed").Key;
        byte[] activity = built.Read(rootTag);
        var targets = TagView.Rows(activity, in TagView.Checked<ActivityRoot>(activity, 0).BoundTags, ActivityRootTagRow.Class);
        WorldBuild.Check(built.TryGetEntry(rootTag, out var rootEntry) && rootEntry.Reference == ActivityRoot.Class
            && targets.Length != 0 && targets[0].Tag == scenarioTag
            && TagView.Checked<ActivityRoot>(activity, 0).Unk8 == StringBankWriter.Hash("testbed")
            && built.Names.Values.All(x => !x.StartsWith("edz_freeroam", StringComparison.Ordinal)),
            "The built activity root is missing, keeps the stock name, or does not name the built scenario.");
        byte[] root = built.Read(mapTag);
        var rows = TagView.Rows(root, in TagView.Checked<SMapRoot>(root, 0).MapEntries, MapBubbleRef.Class);
        int resolved = 0;
        foreach (ref readonly var row in rows) if (built.Resolve64(row.WideTag) is not null) resolved++;
        WorldBuild.Check(rows.Length == 64 && resolved == Kept.Length, $"{resolved} of the built map root rows resolve; {Kept.Length} should.");
        Console.WriteLine($"world_testbed_build packages={build.PackageFiles.Count} tags={build.TagFiles.Count} map_rows={rows.Length} resolved={resolved}");
        Sdk(build, scenarioTag, rootTag);
    }

    /** The server builds the testbed's roster from these rows; they must name the built tags and the new bubble rows. */
    private static void Sdk(SunriseStudio.Projects.ProjectBuildResult build, uint scenarioTag, uint rootTag)
    {
        WorldBuild.Check(build.ActivitySdkPack is not null, "The build wrote no activity SDK pack.");
        using var pack = ActivitySdkPack.Open(build.ActivitySdkPack!);
        var activities = pack.Rows<SdkActivity>(SdkSection.Activities);
        var scenarios = pack.Rows<SdkScenario>(SdkSection.Scenarios);
        var bubbles = pack.Rows<SdkBubble>(SdkSection.Bubbles);
        var states = pack.Rows<SdkState>(SdkSection.States);
        var occurrences = pack.Rows<SdkOccurrence>(SdkSection.Occurrences);
        var objects = pack.Rows<SdkObject>(SdkSection.Objects);
        var tags = build.TagFiles.Select(tag => tag.TagHash).ToHashSet();
        WorldBuild.Check(activities is [var activity] && pack.Text(activity.InternalName) == "testbed"
            && activity.ActivityIndex == uint.MaxValue && activity.Flags == 0xF && activity.ScenarioIndex == 0
            && activity.SelectedScenarioTag == scenarioTag && activity.SelectedActivityRootTag == rootTag,
            "The SDK activity row does not name the testbed.");
        WorldBuild.Check(scenarios is [var scenario] && scenario.Tag == scenarioTag && pack.Text(scenario.Name) == "testbed"
            && bubbles.Length == ExpectedRows && bubbles.Count(bubble => bubble.States.Count > 0) == Kept.Length
            && bubbles.Select((bubble, row) => bubble.BubbleOrdinal == row).All(x => x),
            "The SDK scenario or bubble rows are wrong.");
        WorldBuild.Check(states.Length > 0 && states.All(state => state.SliceSetIndex == 8 * bubbles[state.BubbleIndex].BubbleOrdinal
            && state.EntryIndex == bubbles[state.BubbleIndex].BubbleOrdinal && tags.Contains(state.RegistryTag)),
            "An SDK state keeps its EDZ slice set or registry tag.");
        WorldBuild.Check(occurrences.Length > 0 && occurrences.All(row => row.ObjectIndex < objects.Length && row.StateIndex < states.Length)
            && objects.Any(item => tags.Contains(item.ObjectTag)), "The SDK occurrences do not reach built objects.");
        // The kept bubbles' squads come along; each row must point inside this pack, in the server's search order.
        var squads = pack.Rows<SdkSquad>(SdkSection.Squads);
        var members = pack.Rows<SdkSquadMember>(SdkSection.SquadMembers);
        var anchors = pack.Rows<SdkSquadAnchor>(SdkSection.SquadAnchors);
        var classes = pack.Rows<SdkActorClass>(SdkSection.ActorClasses);
        var slots = pack.Rows<SdkSlot>(SdkSection.Slots);
        bool squadsJoin = squads.Select((squad, row) => squad.ScenarioIndex == 0 && squad.SlotIndex < slots.Length
            && slots[squad.SlotIndex].ObjectIndex == squad.ObjectIndex && squad.OccurrenceIndex < occurrences.Length
            && occurrences[squad.OccurrenceIndex].ObjectIndex == squad.ObjectIndex && pack.Text(squad.Id).Length > 0
            && squad.Members.Count > 0 && squad.Members.First + squad.Members.Count <= members.Length
            && squad.Anchors.First + squad.Anchors.Count <= anchors.Length
            && Enumerable.Range((int)squad.Members.First, (int)squad.Members.Count).All(at => members[at].SquadIndex == row
                && members[at].MemberOrdinal == at - squad.Members.First
                && (members[at].ActorClassIndex == uint.MaxValue || members[at].ActorClassIndex < classes.Length))
            && Enumerable.Range((int)squad.Anchors.First, (int)squad.Anchors.Count).All(at => anchors[at].SquadIndex == row)).All(x => x);
        WorldBuild.Check(squads.Length > 0 && squadsJoin && classes.Length > 0
            && classes.Select(item => item.RsatTag).Distinct().Count() == classes.Length && classes.All(item => item.Descriptors.Count == 0),
            $"The SDK squad rows do not join inside the pack ({squads.Length} squads, {classes.Length} actor classes).");
        // Rows copied as words: every slot and actor class they name must be a row of this pack, in rising order.
        var (groups, groupWords) = pack.Words(SdkSection.CombatObjectiveGroups);
        var (edges, edgeWords) = pack.Words(SdkSection.AuthoredSceneSquadEdges);
        var (texts, textWords) = pack.Words(SdkSection.Texts);
        var (stateNames, stateWords) = pack.Words(SdkSection.ActorStateNames);
        var (bindings, bindingWords) = pack.Words(SdkSection.ActorSequenceBindings);
        var (sequenceTables, tableWords) = pack.Words(SdkSection.ActorSequenceTables);
        uint[] Column(uint[] words, int rowWords, int word) => Enumerable.Range(0, words.Length / rowWords).Select(row => words[row * rowWords + word]).ToArray();
        bool Rising(uint[] values) => values.Zip(values.Skip(1), (a, b) => a <= b).All(x => x);
        var groupSlots = Column(groups, groupWords, 0);
        var edgeSlots = Column(edges, edgeWords, 2);
        var nameClasses = Column(stateNames, stateWords, 0);
        int tableCount = sequenceTables.Length / tableWords;
        WorldBuild.Check(groupSlots.Length > 0 && groupSlots.All(slot => slot < slots.Length) && Rising(groupSlots)
            && edgeSlots.All(slot => slot < slots.Length) && Column(edges, edgeWords, 3).All(slot => slot < slots.Length) && Rising(edgeSlots)
            && texts.Length / textWords == slots.Sum(slot => slot.Aliases.Count)
            && slots.All(slot => slot.Aliases.First + slot.Aliases.Count <= texts.Length / textWords)
            && nameClasses.All(row => row < classes.Length) && Rising(nameClasses)
            && Column(bindings, bindingWords, 0).All(row => row < classes.Length)
            && Column(bindings, bindingWords, 4).Concat(Column(bindings, bindingWords, 5)).All(row => row == uint.MaxValue || row < tableCount),
            "An SDK row copied by slot or actor class points outside the pack or is out of order.");
        Console.WriteLine($"world_testbed_sdk_rows objective_groups={groupSlots.Length} scene_edges={edgeSlots.Length} alias_texts={texts.Length / textWords} "
            + $"state_names={nameClasses.Length} sequence_bindings={bindings.Length / bindingWords} sequence_tables={tableCount}");
        Console.WriteLine($"world_testbed_sdk_squads squads={squads.Length} members={members.Length} anchors={anchors.Length} actor_classes={classes.Length}");
        Console.WriteLine($"world_testbed_sdk bubbles={bubbles.Length} states={states.Length} occurrences={occurrences.Length} "
            + $"objects={objects.Length} built_objects={objects.Count(item => tags.Contains(item.ObjectTag))} bytes={new FileInfo(build.ActivitySdkPack!).Length}");
    }
}
