using System.Text.Json.Nodes;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Tft;
using SunriseStudio.World;
using SunriseStudio.World.Sdk;

/** The bundle destination with its host content stripped: only spawn, sky, lighting and environment volume lists stay. */
static class StripChecks
{
    private const string Destination = "bundlebed";

    /**
     * Strips the placed bundle destination, then checks its lists, its files, a second strip, a forced
     * rebake, a load through the world loader and the SDK squads.
     */
    public static void Run(PackageCatalog catalog, RegistryIndex schema, StudioProject project, string scratch)
    {
        string content = project.ContentDirectory;
        var before = WorldFiles.Load(content, Destination);
        var added = before.Files.Where(file => !WorldStrip.IsHostCopy(file)).Select(file => file.Name).ToHashSet(StringComparer.Ordinal);
        uint arrival = WorldContent.FirstLiveBubble(before);
        int spawns = WorldContent.Spawns(before, arrival).Count;
        int squadsBefore = SdkSquads(catalog, schema, content, scratch);
        var volumesBefore = VolumeRows(before);
        WorldBuild.Check(ParameterVolumes(catalog, volumesBefore, WorldContent.Spawns(before, arrival)[0].Position) > 0,
            "No host volume carries a render parameter set.");

        var result = WorldStrip.StripHost(content, Destination);
        var world = WorldFiles.Load(content, Destination);
        int onDisk = Directory.EnumerateFiles(Path.Combine(content, WorldTemplate.Folder, Destination), "*" + TftNames.FileExtension,
            SearchOption.AllDirectories).Count();
        WorldBuild.Check(result.DroppedLists > 0 && result.RemovedFiles.Count > result.DroppedLists && onDisk == world.Files.Count
            && WorldStrip.IsStripped(world.Single(SScenario.Class)),
            $"The strip dropped {result.DroppedLists} lists and {result.RemovedFiles.Count} files; {onDisk} files on disk, {world.Files.Count} held.");
        // Every file a bundle or an edit made stays; every host static, terrain and collision tag goes.
        WorldBuild.Check(added.All(name => world.Files.Any(file => file.Name == name)), "The strip removed a file the bundle made.");
        WorldBuild.Check(!world.Files.Any(file => WorldStrip.IsHostCopy(file)
                && (file.ClassId == SStaticMeshInstances.Class || file.ClassId == SMapResourceLink.Class)),
            "A host static table or resource link survived the strip.");
        var members = world.Files.Where(file => file.ClassId == SMapContainer.Class)
            .SelectMany(file => ((JsonArray)world.Fields(file.Name)["contents"]!).Select(row => WorldFiles.Text(row!["member_tag"])!)).ToList();
        WorldBuild.Check(members.All(name => world.Files.Any(file => file.Name == name)), "A container names a removed list.");
        foreach (string name in members.Where(name => WorldStrip.IsHostCopy(world[name])))
            foreach (var row in (world.Fields(name)["placements"] as JsonArray ?? []).OfType<JsonObject>())
                WorldBuild.Check(row["payload"] is JsonObject payload && WorldStrip.KeptPayloads.Contains(WorldFiles.Hash(payload["@class"]!))
                    || result.MixedLists > 0, $"{name} keeps a host row.");
        WorldBuild.Check(WorldContent.LiveBubbles(world).Count == WorldContent.LiveBubbles(before).Count
            && WorldContent.Spawns(world, arrival).Count == spawns && spawns > 0, "The strip lost a live bubble or a spawn point.");
        // Each live bubble's spawns sit on that bubble's first spawn, where its bundle bubble anchors.
        var anchors = WorldContent.LiveBubbles(before).Select(bubble => WorldContent.Spawns(before, bubble, hostOnly: true))
            .Where(list => list.Count > 0).Select(list => list[0].Position).ToList();
        // Host spawns gather; a bundle's named spawns keep their own places.
        var moved = world.Files.Where(file => file.ClassId == SSpawnPoints.Class && WorldStrip.IsHostCopy(file))
            .SelectMany(file => (world.Fields(file.Name)["spawn_points"] as JsonArray ?? []).OfType<JsonObject>()).ToList();
        WorldBuild.Check(moved.Count >= spawns && moved.All(row =>
            anchors.Any(anchor => System.Numerics.Vector3.Distance(Vec(WorldFiles.Field(row, "position")), anchor) < 1e-3f)),
            "A spawn row is not on a live bubble's anchor spawn.");
        WorldBuild.Check(WorldContent.Spawns(world, arrival, hostOnly: true).All(spawn =>
            System.Numerics.Vector3.Distance(spawn.Position, WorldContent.Spawns(before, arrival, hostOnly: true)[0].Position) < 1e-3f),
            "An arrival spawn is not on the arrival bubble's anchor.");
        // The volumes' render parameters set fog and exposure near the viewer; the defaults are hazy.
        WorldBuild.Check(VolumeRows(world).ToHashSet().SetEquals(volumesBefore) && volumesBefore.Count > 0,
            "The strip dropped a host environment volume.");
        var again = WorldStrip.StripHost(content, Destination);
        WorldBuild.Check(again.DroppedLists == 0 && again.RemovedFiles.Count == 0, "A second strip removed more.");
        Console.WriteLine($"world_strip dropped_lists={result.DroppedLists} kept_lists={result.KeptLists} mixed={result.MixedLists} "
            + $"removed_files={result.RemovedFiles.Count} files={world.Files.Count} spawns={spawns} "
            + $"rows={string.Join(",", result.DroppedRows.Select(row => $"{row.Key}:{row.Value}"))}");

        // The bake reads its stock collision template through the bundle's own collision list.
        var rebaked = WorldBundleImport.Rebake(project, catalog, force: true);
        WorldBuild.Check(rebaked.Count > 0 && rebaked.All(row => row.Baked), "A forced rebake after the strip did not bake.");

        var tags = WorldBuild.Compile(catalog, schema, world.Files, "strip.checks");
        WorldBuild.Overlay(catalog, tags, StaticChecks.Models(project, catalog));
        try
        {
            uint scenarioTag = tags.Single(x => x.GameName == $"{Destination}:scenario_client").TagHash;
            uint mapTag = tags.Single(x => x.GameName == $"map:{Destination}:root").TagHash;
            var loaded = new SunriseStudio.Scene.WorldLoader(catalog).LoadMission(mapTag, scenarioTag);
            WorldBuild.Check(loaded.MapScene.Bubbles.Count == WorldContent.LiveBubbles(world).Count && loaded.AllStates.Count > 0,
                "The stripped world does not load its bubbles and states.");
            Console.WriteLine($"world_strip_load bubbles={loaded.MapScene.Bubbles.Count} states={loaded.AllStates.Count} objects={loaded.Objects.Count}");
        }
        finally
        {
            catalog.SetOverlayEntries([]);
            catalog.ReadOverride = null;
        }

        int squadsAfter = SdkSquads(catalog, schema, content, scratch);
        int made = WorldSquadAuthoring.List(world).Count;
        WorldBuild.Check(squadsAfter == made && squadsBefore > squadsAfter,
            $"The stripped SDK has {squadsAfter} squads, not the {made} the bundle added ({squadsBefore} before).");
        Console.WriteLine($"world_strip_sdk squads_before={squadsBefore} squads_after={squadsAfter}");
    }

    /** A host volume placement: the volume tag (0x80806FAF) and the row's pose. */
    private sealed record VolumeRow(uint Tag, System.Numerics.Vector3 Position, System.Numerics.Quaternion Rotation, float Scale);

    /** Host volume rows (payload 0x80806FAD) in placement lists the world's containers name. */
    private static List<VolumeRow> VolumeRows(WorldFiles world)
    {
        var listed = world.Files.Where(file => file.ClassId == SMapContainer.Class)
            .SelectMany(file => ((JsonArray)world.Fields(file.Name)["contents"]!).Select(row => WorldFiles.Text(row!["member_tag"])!))
            .ToHashSet(StringComparer.Ordinal);
        var rows = new List<VolumeRow>();
        foreach (string name in listed.Where(name => world.Files.Any(file => file.Name == name) && WorldStrip.IsHostCopy(world[name])))
            foreach (var row in (world.Fields(name)["placements"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (row["payload"] is not JsonObject payload || WorldFiles.Text(payload["@class"]) != "0x80806FAD") continue;
                uint tag = WorldFiles.Text(payload["unk_10"]) is string text && text.StartsWith("#0x", StringComparison.Ordinal)
                    ? uint.Parse(text.AsSpan(3), System.Globalization.NumberStyles.AllowHexSpecifier) : 0u;
                var r = (JsonArray)WorldFiles.Field(row, "rotation");
                rows.Add(new(tag, Vec(WorldFiles.Field(row, "position")), new((float)r[0]!, (float)r[1]!, (float)r[2]!, (float)r[3]!),
                    row["uniform_scale"] is JsonNode scale ? (float)scale : 1f));
            }
        return rows;
    }

    /**
     * Prints each volume tag's payload kinds and the classes behind them, and each render parameter set
     * with the arrival anchor's distance to its box. Kind 3 payloads feed the render parameter overrides.
     * @return How many placed kind 3 payloads the volumes carry.
     */
    private static int ParameterVolumes(PackageCatalog catalog, List<VolumeRow> volumes, System.Numerics.Vector3 anchor)
    {
        var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int sets = 0;
        // Several lists place the same volume at the same pose; each pose counts once.
        foreach (var placed in volumes.Where(row => row.Tag != 0).Distinct())
        {
            byte[] bytes = catalog.Read(placed.Tag);
            ref readonly var head = ref SunriseStudio.Tags.Views.TagView.Checked<C80806faf>(bytes, 0);
            var volumeRows = SunriseStudio.Tags.Views.TagView.Rows(bytes, in head.HE05ad0f0.Volumes, C8080738b.Class);
            // The box is in the placement's frame; the anchor is moved into it.
            var placedLocal = System.Numerics.Vector3.Transform(anchor - placed.Position, System.Numerics.Quaternion.Inverse(placed.Rotation))
                / placed.Scale;
            for (int i = 0; i < volumeRows.Length; i++)
            {
                var sector = volumeRows[i].Sector;
                // A volume row's box is centred on its own position at +0x10; every row read has no rotation.
                var local = placedLocal - new System.Numerics.Vector3(volumeRows[i].Unk10, volumeRows[i].Unk14, volumeRows[i].Unk18);
                var gap = System.Numerics.Vector3.Max(System.Numerics.Vector3.Zero,
                    System.Numerics.Vector3.Max(sector.BoundsMin - local, local - sector.BoundsMax)).Length() * placed.Scale;
                var payloads = SunriseStudio.Tags.Views.TagView.Rows(bytes, in volumeRows[i].H5e478a3d.Payloads);
                byte[] raw = System.Runtime.InteropServices.MemoryMarshal.AsBytes(payloads).ToArray();
                for (int p = 0; p < payloads.Length; p++)
                {
                    uint target = payloads[p].Unk4;
                    string key = $"{raw[p * 8]}:" + (catalog.TryGetEntry(target, out var entry) ? $"0x{entry.Reference:X8}" : "none");
                    kinds[key] = kinds.GetValueOrDefault(key) + 1;
                    if (raw[p * 8] != 3) continue;
                    sets++;
                    byte[] channel = catalog.Read(target);
                    ref readonly var set = ref SunriseStudio.Tags.Views.TagView.Checked<C80808586>(channel, 0);
                    var overrides = SunriseStudio.Tags.Views.TagView.Rows(channel, in set.ChannelOverrides);
                    byte[] rawOverrides = System.Runtime.InteropServices.MemoryMarshal.AsBytes(overrides).ToArray();
                    var indices = Enumerable.Range(0, overrides.Length).Select(o => BitConverter.ToInt32(rawOverrides, o * 24)).Distinct().Order();
                    Console.WriteLine($"world_strip_parameter_volume tag=0x{placed.Tag:X8} row={i} set=0x{target:X8} "
                        + $"anchor_gap={gap:F1} blend={volumeRows[i].H5e478a3d.Unk18} channels={string.Join(",", indices)}");
                }
            }
        }
        Console.WriteLine($"world_strip_volumes rows={volumes.Count} tags={volumes.Select(row => row.Tag).Distinct().Count()} "
            + $"anchor={anchor} kinds={string.Join(",", kinds.Select(x => $"{x.Key}:{x.Value}"))}");
        return sets;
    }
    private static System.Numerics.Vector3 Vec(JsonNode node) => new((float)node[0]!, (float)node[1]!, (float)node[2]!);

    /** The destination's squad rows in an SDK pack built from every world of the project. */
    private static int SdkSquads(PackageCatalog catalog, RegistryIndex schema, string content, string scratch)
    {
        var files = WorldTemplate.ProjectDestinations(content, schema).SelectMany(name => WorldFiles.Load(content, name).Files).ToList();
        var built = WorldBuild.Compile(catalog, schema, files, "strip.sdk").ToDictionary(tag => tag.Name, tag => tag.TagHash, StringComparer.Ordinal);
        string installed = Path.Combine(catalog.GameRoot, "bin", "x64", "Sunrise", ModActivitySdk.FileName);
        byte[] image = ModActivitySdk.Build(content, schema, built, installed, catalog.Read(ActivityRows.ActivityTable))!;
        string path = Path.Combine(scratch, $"strip-{Guid.NewGuid():N}.pack");
        File.WriteAllBytes(path, image);
        using var pack = ActivitySdkPack.Open(path);
        int scenario = Array.FindIndex(pack.Rows<SdkScenario>(SdkSection.Scenarios), row => pack.Text(row.Name) == Destination);
        return pack.Rows<SdkSquad>(SdkSection.Squads).Count(row => row.ScenarioIndex == scenario);
    }
}
