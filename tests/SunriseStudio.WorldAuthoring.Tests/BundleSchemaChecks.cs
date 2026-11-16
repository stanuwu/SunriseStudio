using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SunriseStudio.World.Import;

/** The World Import Bundle schema accepts the testbed bundle, refuses broken copies by path, and names every field the importer reads. */
static partial class BundleSchemaChecks
{
    // Bracketed names in the importer that are world-file fields, not bundle fields.
    private static readonly HashSet<string> WorldFileFields = ["payload", "collision_owner", "contents", "member_tag", "wide_tag", "bubble_index"];

    /**
     * Runs every schema check.
     * @param studioRoot The repository root; found from the program folder when null.
     * @param scratch Where the broken copies go; the system temp folder when null.
     */
    public static void Run(string? studioRoot = null, string? scratch = null)
    {
        string root = studioRoot ?? FindRoot();
        var schema = WorldBundleSchema.Load(SchemaFile(root));
        string fixture = Path.Combine(root, "tests", "Fixtures", "testbed", "bundle");
        var issues = schema.CheckBundle(fixture);
        WorldBuild.Check(issues.Count == 0, "The testbed bundle fails its schema:\n" + string.Join("\n", issues));
        Console.WriteLine("bundle_schema fixture=testbed issues=0");
        Broken(schema, fixture, scratch ?? Path.GetTempPath());
        Coverage(schema, root);
    }

    /** Validates any bundle folder and prints each issue; returns the issue count. */
    public static int Report(string bundleFolder, string? studioRoot = null)
    {
        string root = studioRoot ?? FindRoot();
        var issues = WorldBundleSchema.Load(SchemaFile(root)).CheckBundle(bundleFolder);
        foreach (var issue in issues) Console.WriteLine($"bundle_schema issue {issue}");
        Console.WriteLine($"bundle_schema folder={Path.GetFileName(bundleFolder.TrimEnd('\\', '/'))} issues={issues.Count}");
        return issues.Count;
    }

    /** The schema source file, so a check reads the file under edit rather than the embedded copy. */
    private static string SchemaFile(string root) => Path.Combine(root, "src", "SunriseStudio.World", "Import", WorldBundleSchema.FileName);

    private static string FindRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(SchemaFile(folder.FullName)) && Directory.Exists(Path.Combine(folder.FullName, "src", "SunriseStudio.Authoring")))
                return folder.FullName;
        throw new DirectoryNotFoundException("The Studio repository root was not found above the program folder.");
    }

    private sealed record Case(string Name, Action<string> Break, string File, string Path, string Message);

    private static readonly Case[] Cases =
    [
        new("static without glb", f => Edit(f, "bubble_a", b => Item(b, "statics", 2).Remove("glb")), "bubble_a/bubble.json", "statics[2].glb", "missing"),
        new("static glb file gone", f => File.Delete(Path.Combine(f, "bubble_a", "static.glb")), "bubble_a/bubble.json", "statics[0].glb", "file not found: static.glb"),
        new("short position", f => Edit(f, "bubble_a", b => Item(b, "statics", 0)["position"] = new JsonArray(1, 2)), "bubble_a/bubble.json", "statics[0].position", "needs at least 3 items"),
        new("surface not hex", f => Edit(f, "bubble_a", b => Item(b, "statics", 0)["surface"] = "zz"), "bubble_a/bubble.json", "statics[0].surface", "\"zz\" does not match"),
        new("collision a number", f => Edit(f, "bubble_a", b => Item(b, "statics", 0)["collision"] = 3), "bubble_a/bubble.json", "statics[0].collision", "must be a"),
        new("too many lod distances", f => Edit(f, "bubble_a", b => Item(b, "statics", 2)["lod_distances"] = new JsonArray(30, 60)), "bubble_a/bubble.json", "statics[2].lod_distances", "has 2 entries for 1 lods"),
        new("duplicate static name", f => Edit(f, "bubble_a", b => Item(b, "statics", 1)["name"] = "terrain_and_block"), "bubble_a/bubble.json", "statics[1].name", "terrain_and_block is used twice"),
        new("no spawn point", f => Edit(f, "bubble_a", b => b.Remove("spawn_points")), "bubble_a/bubble.json", "spawn_points", "missing (needed with"),
        new("landing names no spawn point", f => Edit(f, "bubble_a", b => b["landings"] = new JsonArray(new JsonObject { ["name"] = "Square", ["spawn"] = "nowhere" })),
            "bubble_a/bubble.json", "landings[0].spawn", "nowhere is not a spawn point of this bubble"),
        new("landing without a name", f => Edit(f, "bubble_a", b => b["landings"] = new JsonArray(new JsonObject { ["spawn"] = "arrival" })), "bubble_a/bubble.json", "landings[0].name", "missing"),
        new("spawn name used twice", f => Edit(f, "bubble_a", b => ((JsonArray)b["spawn_points"]!).Add(((JsonArray)b["spawn_points"]!)[0]!.DeepClone())),
            "bubble_a/bubble.json", "spawn_points[1].name", "arrival is used twice"),
        new("permissions without statics", f => Edit(f, "bubble_a", b => b.Remove("statics")), "bubble_a/bubble.json", "statics", "missing (needed with permissions)"),
        new("permissions cell zero", f => Edit(f, "bubble_a", b => ((JsonObject)b["permissions"]!)["cell"] = 0), "bubble_a/bubble.json", "permissions.cell", "must be above 0"),
        new("light field typo", f => Edit(f, "bubble_a", b => { var light = Item(b, "lights", 0); light["postion"] = light["position"]!.DeepClone(); light.Remove("position"); }),
            "bubble_a/bubble.json", "lights[0].postion", "unknown field"),
        new("entity with plays and device", f => Edit(f, "bubble_a", b => Item(b, "entities", 5)["plays"] = "open"), "bubble_a/bubble.json", "entities[5]", "give only one of: plays, device"),
        new("entity with neither", f => Edit(f, "bubble_a", b => Item(b, "entities", 0).Remove("plays")), "bubble_a/bubble.json", "entities[0]", "needs one of: plays, device"),
        new("entity name upper case", f => Edit(f, "bubble_a", b => Item(b, "entities", 0)["name"] = "Npc"), "bubble_a/bubble.json", "entities[0].name", "\"Npc\" does not match"),
        new("bubble name upper case with entities", f => Edit(f, "bubble_a", b => b["name"] = "Bubble_A"), "bubble_a/bubble.json", "name", "\"Bubble_A\" does not match"),
        new("event frame negative", f => Edit(f, "bubble_a", b => Item(Item(b, "entities", 5), "events", 0)["frame"] = -1), "bubble_a/bubble.json", "entities[5].events[0].frame", "must be at least 0"),
        new("event frame fractional", f => Edit(f, "bubble_a", b => Item(Item(b, "entities", 5), "events", 0)["frame"] = 2.5), "bubble_a/bubble.json", "entities[5].events[0].frame", "must be an integer"),
        new("event sound unknown", f => Edit(f, "bubble_a", b => Item(Item(b, "entities", 5), "events", 0)["sound"] = "door_shut"), "bubble_a/bubble.json", "entities[5].events[0].sound", "door_shut is not a bundle sound"),
        new("volume size and corners", f => Edit(f, "bubble_a", b => Item(b, "volumes", 1)["corners"] = Square()), "bubble_a/bubble.json", "volumes[1]", "may not give all of: size, corners"),
        new("volume corners without height", f => Edit(f, "bubble_a", b => { var v = Item(b, "volumes", 1); v.Remove("size"); v["corners"] = Square(); }), "bubble_a/bubble.json", "volumes[1].height", "missing (needed with corners)"),
        new("volume corners not convex", f => Edit(f, "bubble_a", b => { var v = Item(b, "volumes", 1); v.Remove("size"); v["height"] = 3; v["corners"] = Bowtie(); }), "bubble_a/bubble.json", "volumes[1].corners", "do not go around a convex floor"),
        new("volume width zero", f => Edit(f, "bubble_a", b => Item(b, "volumes", 1)["size"] = new JsonArray(0, 3, 4)), "bubble_a/bubble.json", "volumes[1].size[0]", "may not be 0"),
        new("bundle without bubbles", f => EditBundle(f, b => b.Remove("bubbles")), "bundle.json", "bubbles", "missing"),
        new("bundle bubble folder gone", f => EditBundle(f, b => ((JsonArray)b["bubbles"]!).Add("bubble_d")), "bundle.json", "bubbles[3]", "no bubble.json in folder bubble_d"),
        new("far covers unknown bubble", f => EditBundle(f, b => Item(b, "far", 0)["covers"] = new JsonArray("bubble_x")), "bundle.json", "far[0].covers[0]", "bubble_x is not a listed bubble"),
        new("far covers bubble without bounds", f => Edit(f, "bubble_b", b => b.Remove("bounds")), "bundle.json", "far[0].covers[0]", "bubble bubble_b has no bounds"),
        new("far margin negative", f => EditBundle(f, b => Item(b, "far", 0)["margin"] = -1), "bundle.json", "far[0].margin", "must be at least 0"),
        new("sky panorama gone", f => File.Delete(Path.Combine(f, "sky", "dome.png")), "bundle.json", "sky.panorama", "file not found: sky/dome.png"),
        new("duplicate sound name", f => EditBundle(f, b => ((JsonArray)b["sounds"]!).Add(Item(b, "sounds", 0).DeepClone())), "bundle.json", "sounds[1].name", "door_open is used twice"),
        new("sound like not hex", f => EditBundle(f, b => Item(b, "sounds", 0)["like"] = "door"), "bundle.json", "sounds[0].like", "\"door\" does not match"),
        new("squad without entity", f => Edit(f, "bubble_b", b => b["squads"] = new JsonArray(new JsonObject { ["name"] = "group", ["position"] = new JsonArray(32, 0, 0) })),
            "bubble_b/bubble.json", "squads[0].entity", "missing"),
        new("creature clip key unknown", f => EditBundle(f, b => b["creatures"] = Creature(f, clip: "dance")), "bundle.json", "creatures[0].clips.dance", "field name: "),
        new("creature attack unknown", f => EditBundle(f, b => { b["creatures"] = Creature(f); Item(b, "creatures", 0)["attack"] = "bite"; }), "bundle.json", "creatures[0].attack", "must be one of"),
        new("creature glb gone", f => EditBundle(f, b => { b["creatures"] = Creature(f); File.Delete(Path.Combine(f, "creatures", "hound.glb")); }), "bundle.json", "creatures[0].glb", "file not found: creatures/hound.glb"),
        new("squad names unknown creature", f => Edit(f, "bubble_b", b => b["squads"] = JsonNode.Parse("""[{"name": "wild", "entity": "creature:badger", "position": [32, 0, 0]}]""")),
            "bubble_b/bubble.json", "squads[0].entity", "badger is not a bundle creature"),
        new("provisional music track wav gone", f => EditBundle(f, b => b["music"] = new JsonObject { ["tracks"] = new JsonArray(new JsonObject { ["name"] = "zone", ["wav"] = "music/zone.wav" }) }),
            "bundle.json", "music.tracks[0].wav", "file not found: music/zone.wav"),
        new("crossing line of one point", f => Edit(f, "bubble_b", b => b["crossings"] = JsonNode.Parse("""[{"to": "bubble_c", "line": [[0, 0, 0]]}]""")),
            "bubble_b/bubble.json", "crossings[0].line", "needs at least 2 items"),
        new("crossing too narrow", f => Edit(f, "bubble_b", b => b["crossings"] = JsonNode.Parse("""[{"to": "bubble_c", "line": [[0, 0, 0], [0, 9, 0]], "width": 1}]""")),
            "bubble_b/bubble.json", "crossings[0].width", "must be at least 4"),
        new("door without size", f => Edit(f, "bubble_b", b => b["doors"] = JsonNode.Parse("""[{"name": "gate", "position": [32, 0, 0]}]""")),
            "bubble_b/bubble.json", "doors[0].size", "missing"),
        new("door name in two bubbles", f => { Edit(f, "bubble_b", b => b["doors"] = JsonNode.Parse("""[{"name": "gate", "position": [32, 0, 0], "size": [2, 1, 3]}]"""));
            Edit(f, "bubble_c", b => b["doors"] = JsonNode.Parse("""[{"name": "gate", "position": [0, 0, 0], "size": [2, 1, 3]}]""")); },
            "bubble_c/bubble.json", "doors[0].name", "gate is already a door of this bundle"),
        new("map without shared space", f => EditBundle(f, b => b["map"] = Map()), "bundle.json", "map", "needs space shared"),
        new("map without landings", f => EditBundle(f, b => { b["space"] = "shared"; b["map"] = Map(); }), "bundle.json", "map", "needs a bubble with landings"),
        new("map corner short", f => EditBundle(f, b => { b["space"] = "shared"; b["map"] = Map(); ((JsonObject)b["map"]!)["min"] = new JsonArray(1); }),
            "bundle.json", "map.min", "needs at least 2 items"),
        new("planet size zero", f => EditBundle(f, b => b["planet"] = new JsonObject { ["size"] = 0 }), "bundle.json", "planet.size", "must be above 0"),
    ];

    /** Each case breaks a fresh copy of the fixture one way and must report its path. */
    private static void Broken(WorldBundleSchema schema, string fixture, string scratch)
    {
        string work = Path.Combine(scratch, $"bundle_schema_{Environment.ProcessId}");
        try
        {
            foreach (var test in Cases)
            {
                if (Directory.Exists(work)) Directory.Delete(work, true);
                Copy(fixture, work);
                test.Break(work);
                var issues = schema.CheckBundle(work);
                bool hit = issues.Any(issue => issue.File == test.File && issue.Path == test.Path && issue.Message.StartsWith(test.Message, StringComparison.Ordinal));
                WorldBuild.Check(hit, $"Broken bundle \"{test.Name}\" did not report {test.File}: {test.Path}: {test.Message}. It reported:\n"
                    + string.Join("\n", issues));
                Console.WriteLine($"bundle_schema broken=\"{test.Name}\" -> {issues.First(issue => issue.File == test.File && issue.Path == test.Path)}");
            }
            // A well-formed squad list is accepted.
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Copy(fixture, work);
            Edit(work, "bubble_b", b => b["squads"] = JsonNode.Parse("""
                [{"name": "group", "entity": "0x80A00001", "count": 3, "position": [32, 0, 0]}]
                """));
            var accepted = schema.CheckBundle(work);
            WorldBuild.Check(accepted.Count == 0, "A well-formed squad is refused:\n" + string.Join("\n", accepted));
            // A creature and a squad that names it are accepted.
            EditBundle(work, b => b["creatures"] = Creature(work));
            Edit(work, "bubble_b", b => b["squads"] = JsonNode.Parse("""
                [{"name": "wild", "entity": "creature:hound", "count": 2, "position": [32, 0, 0]}]
                """));
            accepted = schema.CheckBundle(work);
            WorldBuild.Check(accepted.Count == 0, "A well-formed creature is refused:\n" + string.Join("\n", accepted));
            Edit(work, "bubble_b", b => b["doors"] = JsonNode.Parse("""
                [{"name": "trail:0", "position": [32, 0, 0], "size": [2, 1, 3], "yaw": 90}, {"name": "trail:1", "position": [40, 0, 0], "size": [2, 1, 3]}]
                """));
            accepted = schema.CheckBundle(work);
            WorldBuild.Check(accepted.Count == 0, "A well-formed door list is refused:\n" + string.Join("\n", accepted));
            Edit(work, "bubble_a", b => b["landings"] = new JsonArray(new JsonObject { ["name"] = "Square", ["spawn"] = "arrival" }));
            accepted = schema.CheckBundle(work);
            WorldBuild.Check(accepted.Count == 0, "A well-formed landing is refused:\n" + string.Join("\n", accepted));
            EditBundle(work, b => { b["space"] = "shared"; b["map"] = Map(); b["planet"] = new JsonObject { ["size"] = 0.2 }; });
            accepted = schema.CheckBundle(work);
            WorldBuild.Check(accepted.Count == 0, "A well-formed map and planet are refused:\n" + string.Join("\n", accepted));
            Console.WriteLine($"bundle_schema broken_cases={Cases.Length} squad=accepted creature=accepted doors=accepted landing=accepted map=accepted");
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
    }

    /** Every field name the importer reads from bundle or bubble JSON is a schema field. */
    private static void Coverage(WorldBundleSchema schema, string root)
    {
        string authoring = Path.Combine(root, "src", "SunriseStudio.Authoring");
        var sources = Directory.GetFiles(authoring, "WorldBundleImport*.cs").Append(Path.Combine(authoring, "BundleDirector.cs"))
            .Append(Path.Combine(authoring, "WorldCrossingImport.cs"))
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        WorldBuild.Check(sources.Length > 0, "No importer sources were found.");
        var read = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string source in sources)
            foreach (Match match in FieldRead().Matches(File.ReadAllText(source)))
                read.Add(match.Groups[1].Value);
        WorldBuild.Check(read.Contains("statics") && read.Contains("glb") && read.Contains("panorama"), "The field scan found none of the known fields; the pattern no longer fits the importer.");
        var fields = schema.FieldNames();
        var bundleFields = read.Where(name => !WorldFileFields.Contains(name)).ToArray();
        var missing = bundleFields.Where(name => !fields.Contains(name)).ToArray();
        Console.WriteLine($"bundle_schema reader_sources={string.Join(",", sources.Select(Path.GetFileName))}");
        Console.WriteLine($"bundle_schema reader_fields({bundleFields.Length})={string.Join(",", bundleFields)}");
        Console.WriteLine($"bundle_schema skipped_world_file_fields={string.Join(",", read.Where(WorldFileFields.Contains))}");
        Console.WriteLine($"bundle_schema schema_only_fields={string.Join(",", fields.Where(name => !read.Contains(name)))}");
        WorldBuild.Check(missing.Length == 0, $"The schema lacks fields the importer reads: {string.Join(", ", missing)}.");
    }

    // A JSON field read as node["name"]; the importer reads every bundle field this way.
    [GeneratedRegex("""\["([a-z_][a-z0-9_]*)"\]""")]
    private static partial Regex FieldRead();

    /** One creature over the Dreg; writes an empty glb so the x-file check finds it. */
    private static JsonArray Creature(string bundle, string clip = "melee")
    {
        Directory.CreateDirectory(Path.Combine(bundle, "creatures"));
        File.WriteAllBytes(Path.Combine(bundle, "creatures", "hound.glb"), []);
        return (JsonArray)JsonNode.Parse($$$"""
            [{"name": "hound", "glb": "creatures/hound.glb", "donor": "0x80C1AF8A", "scale": 0.015, "yaw": 90,
              "clips": {"idle": "Hound_Idle", "{{{clip}}}": "Hound_Attack"},
              "bones": {"head": "Head"}, "follow": {"pelvis": "Waist", "head": "Head"},
              "capsule": {"radius": 0.45, "height": 1.2},
              "display_name": "Hound", "grenades": false, "attack": "gun", "projectile": "0x80C1C197", "projectile_speed": 20}]
            """)!;
    }

    /** A map rectangle with no image. */
    private static JsonObject Map() => new() { ["min"] = new JsonArray(-100, -100), ["max"] = new JsonArray(100, 50) };

    private static JsonArray Square() => new(new JsonArray(-1, -1), new JsonArray(1, -1), new JsonArray(1, 1), new JsonArray(-1, 1));

    private static JsonArray Bowtie() => new(new JsonArray(-1, -1), new JsonArray(1, 1), new JsonArray(1, -1), new JsonArray(-1, 1));

    private static JsonObject Item(JsonObject node, string list, int index) => (JsonObject)((JsonArray)node[list]!)[index]!;

    private static void Edit(string bundle, string bubble, Action<JsonObject> change) => EditFile(Path.Combine(bundle, bubble, "bubble.json"), change);

    private static void EditBundle(string bundle, Action<JsonObject> change) => EditFile(Path.Combine(bundle, "bundle.json"), change);

    private static void EditFile(string path, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        change(node);
        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (string folder in Directory.GetDirectories(from)) Copy(folder, Path.Combine(to, Path.GetFileName(folder)));
    }
}
