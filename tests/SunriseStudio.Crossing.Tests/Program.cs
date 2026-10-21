using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using static CrossingFixture;

string temporary = Path.Combine(Path.GetTempPath(), "sunrise-crossing-tests-" + Guid.NewGuid().ToString("N"));
try
{
    int gameIndex = Array.IndexOf(args, "--game-root");
    int keysIndex = Array.IndexOf(args, "--keys");
    if (gameIndex >= 0)
    {
        using var game = PackageCatalog.Open(args[gameIndex + 1], keysIndex >= 0 ? args[keysIndex + 1] : null);
        GameChecks.Run(game);
        if (args.Contains("--game-only")) return 0;
    }
    string installation = Path.Combine(temporary, "fixture");
    Create(installation);
    using var catalog = PackageCatalog.Open(installation, cacheOptions: new() { Enabled = false });

    // The Transitions layer reads every owner, both of A's layers and the installed A-C zones.
    var loader = new WorldLoader(catalog);
    var map = loader.LoadMap(Root);
    Check(map.Scene.Visibility.Count == 3 && map.Scene.Visibility.Single(v => v.OwnerTag == OwnerTag(0)).Layers.Count == 2,
        "The Transitions layer did not read every visibility owner and layer.");
    var zoneMarkers = map.Scene.Markers.Where(m => m.Layer == SceneLayer.Transitions && m.Category == "Crossing").ToArray();
    Check(zoneMarkers.Length == 2 && zoneMarkers.All(m => m.Name.Contains($"{KeyLink:X16}")) && map.Scene.DecodeErrorCount == 0,
        "Installed crossing zones were not drawn with their link key.");
    Console.WriteLine($"PASS transitions owners=3 layers=4 zones=2 markers={map.Scene.Markers.Count(m => m.Layer == SceneLayer.Transitions)}");

    ProjectCrossingBox ExitA() => new([3, 0, 1], [1, 4, 2]);
    ProjectCrossingBox ExitB() => new([-3, 0, 1], [1, 4, 2]);
    ProjectBubbleConnection Join(string id, CrossingProfile profile, InheritedZonePolicy policy) =>
        new(id, Root, NameA, NameB, ExitA(), ExitB(), profile, policy);

    // Retail: both bubbles rebuild their query volume as one zone; A's crossing to C is removed by policy.
    var retail = Join("a-b", CrossingProfile.Retail, InheritedZonePolicy.Deactivate);
    var compiled = CrossingCompiler.Compile(catalog, [retail]);
    var again = CrossingCompiler.Compile(catalog, [retail]);
    Check(compiled.Assets.Count == 11 && compiled.Assets.All(pair => again.Assets[pair.Key].AsSpan().SequenceEqual(pair.Value)),
        "Retail compilation is not repeatable or touched the wrong entries.");
    var identity = MapIdentity.Decode(compiled.Assets[Definition]);
    var report = compiled.Reports.Single();
    var link = identity.Keys[^1];
    Check(report.NewLink && link.Flags == 1 && link.Index == 5 && link.Value == report.LinkKey
        && identity.Bubbles[0].FirstLinks.SequenceEqual(new ushort[] { 3, 5 }) && identity.Bubbles[1].FirstLinks.SequenceEqual(new ushort[] { 5 })
        && identity.Bubbles[2].FirstLinks.SequenceEqual(new ushort[] { 3 }), "The shared link is not in both first lists only.");
    foreach (int slot in new[] { 0, 1 })
        Check(compiled.Assets[BubbleTag(slot)].AsSpan(28, 32).SequenceEqual(identity.Bubbles[slot].Mask),
            "A bubble resource mask does not follow its definition.");
    // Scenario states repeat their bubble's mask: A's and B's follow in every scenario of the map; C and other maps stay.
    byte[] fullScenario = compiled.Assets[Scenario], installedScenario = catalog.ReadOriginal(Scenario);
    Check(StateMask(fullScenario, 0, 0).SequenceEqual(identity.Bubbles[0].Mask) && StateMask(fullScenario, 0, 1).SequenceEqual(identity.Bubbles[0].Mask)
        && StateMask(fullScenario, 1, 0).SequenceEqual(identity.Bubbles[1].Mask)
        && StateMask(fullScenario, 2, 0).SequenceEqual(StateMask(installedScenario, 2, 0))
        && Enumerable.Range(0, fullScenario.Length).Count(i => fullScenario[i] != installedScenario[i]) <= 3 * 32
        && StateMask(compiled.Assets[ScenarioA], 0, 0).SequenceEqual(identity.Bubbles[0].Mask) && !compiled.Assets.ContainsKey(ScenarioForeign),
        "Scenario states do not follow their bubbles' masks.");
    (sbyte State, float Ramp, VisibilityZone? Zone) At(CrossingCompilation result, int layer, Vector3 point)
    {
        var (_, _, payloadTag, cellsTag) = Layers[layer];
        var payload = new VisibilityPayload(result.Assets.GetValueOrDefault(payloadTag) ?? catalog.ReadOriginal(payloadTag));
        var cells = VisibilityCells.Decode(result.Assets.GetValueOrDefault(cellsTag) ?? catalog.ReadOriginal(cellsTag));
        var cell = cells.Cells[(int)payload.Query(point)];
        return (cell.State, cell.Ramp, cell.Zone < 0 ? null : cells.Zones[cell.Zone]);
    }
    foreach (int layer in new[] { 0, 1 })
    {
        var trigger = At(compiled, layer, new(3, 0, 1));
        Check(trigger.State == 2 && trigger.Ramp == 0 && trigger.Zone?.LinkKey == link.Value && trigger.Zone.DestinationKey == KeyB,
            "A's own exit is not the trigger toward B.");
        Check(At(compiled, layer, new(0, 0, 1)).State == 3 && At(compiled, layer, new(-3, 0, 1)) is (4, > 0, _)
            && At(compiled, layer, new(-30, 0, 1)) is (5, > 10, _), "A's middle, landing and rest do not follow the retail layout.");
    }
    Check(At(compiled, 2, new(-3, 0, 1)) is (2, _, { DestinationKey: KeyA }) && At(compiled, 2, new(3, 0, 1)).State == 4
        && At(compiled, 3, new(-38, 0, 1)) is (2, _, { DestinationKey: KeyA }), "B's layout is wrong or C changed.");
    Check(VisibilityCells.Decode(compiled.Assets[Layers[0].Cells]).Zones.Count == 1, "Deactivate kept A's crossing to C.");
    Console.WriteLine($"PASS retail link={link.Value:X16} index={link.Index} cells={report.Source.Cells}/{report.Destination.Cells} speed={report.Speed} repeatable=yes");

    // Simple: installed cells stay; the exit cell triggers, the other exit's cell is a landing band; Keep holds A-C.
    // Exit boxes inside one cell of every installed layer, so each selects exactly one cell.
    var simple = Join("a-b", CrossingProfile.Simple, InheritedZonePolicy.Keep) with
    {
        SourceExit = new([3.5f, 0, 0.5f], [1, 1, 1]),
        DestinationExit = new([-2, 0, 0.5f], [1, 1, 1])
    };
    var kept = CrossingCompiler.Compile(catalog, [simple]);
    Check(!kept.Assets.ContainsKey(Layers[0].Payload) && kept.Assets.Count == 8, "The simple profile changed a payload.");
    foreach (int layer in new[] { 0, 1 })
    {
        var cells = VisibilityCells.Decode(kept.Assets[Layers[layer].Cells]);
        var before = VisibilityCells.Decode(catalog.ReadOriginal(Layers[layer].Cells));
        Check(cells.Zones.Count == 2 && cells.Zones[0] == before.Zones[0] && cells.Cells.Count == before.Cells.Count,
            "Keep changed A's installed crossing to C.");
        Check(At(kept, layer, new(3.5f, 0, 0.5f)) is (2, _, { DestinationKey: KeyB }) && At(kept, layer, new(-2, 0, 0.5f)) is (4, 82.6f, _)
            && At(kept, layer, new(-39, 0, 1)) is (2, _, { DestinationKey: KeyC }), "The simple cells or the kept zone are wrong.");
        int changed = Enumerable.Range(0, cells.Cells.Count).Count(i => cells.Cells[i] != before.Cells[i]);
        Check(changed == 2, $"The simple profile changed {changed} cells, not the exit and landing cells.");
    }
    var dropped = CrossingCompiler.Compile(catalog, [simple with { Policy = InheritedZonePolicy.Deactivate }]);
    Check(VisibilityCells.Decode(dropped.Assets[Layers[0].Cells]).Zones.Count == 1
        && At(dropped, 0, new(-39, 0, 1)).State == 0, "Deactivate kept A's crossing to C.");
    Console.WriteLine("PASS simple exit=1 landing=1 keep=installed-zone-kept deactivate=installed-zone-removed payloads=unchanged");

    // Everything World Studio refuses is refused by name.
    void Refused(string label, params ProjectBubbleConnection[] connections)
    {
        try { CrossingCompiler.Compile(catalog, connections); }
        catch (InvalidDataException error) { Console.WriteLine($"PASS refused {label}: {error.Message}"); return; }
        throw new InvalidDataException($"{label} was not refused.");
    }
    Refused("door", retail with { Travel = CrossingTravel.Door });
    Refused("teleport", retail with { Travel = CrossingTravel.Teleport });
    Refused("scripted", retail with { Travel = CrossingTravel.Scripted });
    Refused("entry point", retail with { EntryPoint = 0x2EA8FB98 });
    Refused("retarget", retail with { Policy = InheritedZonePolicy.Retarget });
    Refused("polygon portal", retail with { SourceExit = ExitA() with { Rotation = [0, 0, 0.7071068f, 0.7071068f] } });
    Refused("second zone per pair", retail, retail with { Id = "b-a", SourceBubble = NameB, DestinationBubble = NameA });
    Refused("retail keep over installed zones", retail with { Policy = InheritedZonePolicy.Keep });
    Refused("retail bubble in two connections", retail, new ProjectBubbleConnection("a-c", Root, NameA, NameC,
        new([-39, 0, 1], [0.5f, 2, 2]), new([-37, 0, 1], [0.5f, 2, 2]), CrossingProfile.Retail, InheritedZonePolicy.Deactivate));
    Refused("exits apart on two axes", retail with { DestinationExit = new([-3, 9, 1], [1, 4, 2]) });
    Refused("exit outside the query volume", retail with { SourceExit = new([7.5f, 0, 1], [1, 4, 2]) });
    Refused("exit selecting no cell", simple with { SourceExit = new([3, 0, 100], [1, 1, 1]) });

    // The project stores the record and the deltas in one manifest write; the build carries them as a patch.
    var project = StudioProject.Create(Path.Combine(temporary, "project"), "Crossings");
    catalog.ReadOverride = tag => project.ContainsEdit(tag) ? project.ReadAsset(tag, catalog) : null;
    var edits = CrossingCompiler.Edits(catalog, project.BubbleConnections, [retail], out var applied);
    project.SetBubbleConnections([retail], edits, catalog);
    catalog.InvalidateReadCache();
    var reopened = StudioProject.Open(project.DirectoryPath);
    string manifest = File.ReadAllText(project.ManifestPath);
    Check(reopened.BubbleConnections.Single() is { Id: "a-b", Profile: CrossingProfile.Retail } && project.Changes.Count == 11
        && manifest.Contains("\"version\": 8") && !manifest.Contains("deltas/added"), "The connection or its deltas were not saved at version 8.");
    using (var saved = JsonDocument.Parse(manifest))
        Check(saved.RootElement.GetProperty("features").EnumerateArray().Select(name => name.GetString()).SequenceEqual([ProjectFeatures.BubbleConnections]),
            "The manifest does not name the bubble-connections feature.");
    var later = System.Text.Json.Nodes.JsonNode.Parse(manifest)!;
    later["features"] = new System.Text.Json.Nodes.JsonArray(ProjectFeatures.BubbleConnections, "later-feature");
    string laterFolder = Path.Combine(temporary, "later-project");
    Directory.CreateDirectory(laterFolder);
    File.WriteAllText(Path.Combine(laterFolder, StudioProject.ProjectFileName), later.ToJsonString());
    try { StudioProject.Open(laterFolder); throw new InvalidOperationException("A manifest naming an unknown feature opened."); }
    catch (InvalidDataException error) { Check(error.Message == "This project needs a newer Sunrise Studio.", "An unknown feature was refused for another reason."); }
    foreach (var (tag, bytes) in applied.Assets)
        Check(reopened.ReadAsset(tag, catalog).AsSpan().SequenceEqual(bytes), $"The saved delta of {tag:X8} does not rebuild the compiled bytes.");
    var edited = new WorldLoader(catalog).LoadMap(Root);
    var ownA = edited.Scene.Visibility.Single(v => v.OwnerTag == OwnerTag(0));
    Check(ownA.Layers.All(l => l.Cells.Zones.Count == 1 && l.Cells.Zones[0].LinkKey == link.Value && l.Boxes.Count == l.Cells.Cells.Count)
        && edited.Scene.Markers.Count(m => m.Category == "Crossing") == 3 && edited.Scene.DecodeErrorCount == 0,
        "Studio's loader did not read the compiled crossing back.");
    var build = reopened.Build(catalog, Path.Combine(temporary, "build"));
    string merged = Path.Combine(temporary, "built");
    Directory.CreateDirectory(Path.Combine(merged, "packages"));
    foreach (string file in Directory.GetFiles(Path.Combine(installation, "packages")).Concat(build.PackageFiles))
        File.Copy(file, Path.Combine(merged, "packages", Path.GetFileName(file)), true);
    using (var built = PackageCatalog.Open(merged, cacheOptions: new() { Enabled = false }))
    {
        foreach (var (tag, bytes) in applied.Assets)
            Check(built.ReadOriginal(tag).AsSpan().SequenceEqual(bytes), $"Built package readback of {tag:X8} differs from compilation.");
        var rebuilt = new WorldLoader(built).LoadMap(Root);
        Check(rebuilt.Scene.Visibility.Count == 3 && rebuilt.Scene.DecodeErrorCount == 0, "The built package does not decode.");
    }
    Console.WriteLine($"PASS project version=8 changes={project.Changes.Count} reopen=exact loader=re-decoded build={build.PackageFiles.Count} readback=exact");
    var removed = CrossingCompiler.Edits(catalog, project.BubbleConnections, [], out var none);
    project.SetBubbleConnections([], removed, catalog);
    Check(project.Changes.Count == 0 && project.BubbleConnections.Count == 0 && none.Assets.Count == 0
        && !File.ReadAllText(project.ManifestPath).Contains("bubbleConnections"), "Removing the connection left changes behind.");
    Console.WriteLine("PASS remove changes=0 manifest=clean");

    // World Studio and the parity driver live outside this repository; WORLD_STUDIO_SOURCE and SUNRISE_PARITY_DRIVERS name them.
    string? oracle = Environment.GetEnvironmentVariable("WORLD_STUDIO_SOURCE"), drivers = Environment.GetEnvironmentVariable("SUNRISE_PARITY_DRIVERS");
    if (string.IsNullOrWhiteSpace(oracle) || string.IsNullOrWhiteSpace(drivers))
        Console.WriteLine("SKIP parity: set WORLD_STUDIO_SOURCE and SUNRISE_PARITY_DRIVERS to compare with World Studio's Python.");
    else Parity.Run(catalog, oracle, Path.Combine(drivers, "crossing_parity.py"), Path.Combine(temporary, "parity"), [retail],
        [simple], [simple with { Policy = InheritedZonePolicy.Deactivate }],
        [retail with { SourceExit = new([5, 1, 2], [1.5f, 6, 3]), DestinationExit = new([-6, -1, 4], [0.75f, 5, 4]) }]);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
finally
{
    try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); } catch (IOException) { }
}

static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }

/** The 32-byte mask of a scenario state: bubbles at +0x50, states at bubble +8, mask at state +32. */
static byte[] StateMask(byte[] scenario, int bubble, int state)
{
    int Rows(int descriptor) => descriptor + 24 + (int)BinaryPrimitives.ReadInt64LittleEndian(scenario.AsSpan(descriptor + 8));
    int row = Rows(0x50) + 24 * bubble, at = Rows(row + 8) + 76 * state;
    return scenario[(at + 32)..(at + 64)];
}

/**
 * Writes the fixture's inputs and Studio's outputs, then runs World Studio's Python compiler on the same inputs. Python
 * rebinds a scenario against its whole map, so the A-only scenario is Studio's alone.
 */
internal static class Parity
{
    internal static void Run(PackageCatalog catalog, string oracle, string script, string folder, params ProjectBubbleConnection[][] cases)
    {
        Directory.CreateDirectory(folder);
        var random = new Random(20261021);
        var distances = Enumerable.Range(0, 4000).Select(_ => new[] { Next(), Next(), Next(), Next(), Next(), Next() }).ToArray();
        double Next() => (float)(random.NextDouble() * 400 - 200);
        File.WriteAllText(Path.Combine(folder, "distances.json"), JsonSerializer.Serialize(distances.Select(row => new
        {
            p = row[..3],
            q = row[3..],
            bits = BitConverter.DoubleToInt64Bits(CrossingCompiler.Distance(row[..3], row[3..])).ToString("X16")
        })));
        for (int index = 0; index < cases.Length; index++)
        {
            string path = Path.Combine(folder, $"case-{index}");
            Directory.CreateDirectory(path);
            var compiled = CrossingCompiler.Compile(catalog, cases[index]);
            foreach (uint tag in new[] { Definition, Scenario, ScenarioA, ScenarioForeign }.Concat(Enumerable.Range(0, 3).Select(BubbleTag))
                .Concat(Layers.SelectMany(l => new[] { l.Payload, l.Cells })))
                File.WriteAllBytes(Path.Combine(path, $"{tag:X8}.bin"), catalog.ReadOriginal(tag));
            foreach (var (tag, bytes) in compiled.Assets) File.WriteAllBytes(Path.Combine(path, $"out-{tag:X8}.bin"), bytes);
            File.WriteAllText(Path.Combine(path, "spec.json"), JsonSerializer.Serialize(new
            {
                space = $"{Root:X8}",
                definition = $"{Definition:X8}",
                bubbles = Enumerable.Range(0, 3).Select(slot => new { name = $"{Names[slot]:X8}", slot, tag = $"{BubbleTag(slot):X8}" }),
                scenarios = new[] { $"{Scenario:X8}" },
                studio_only = new[] { $"{ScenarioA:X8}" },
                layers = Layers.Select(l => new { l.Slot, l.Mode, payload = $"{l.Payload:X8}", cells = $"{l.Cells:X8}" }),
                connections = cases[index].Select(c => new
                {
                    c.Id,
                    source = $"{c.SourceBubble:X8}",
                    destination = $"{c.DestinationBubble:X8}",
                    profile = c.Profile.ToString(),
                    policy = c.Policy.ToString(),
                    source_exit = new { center = c.SourceExit.Center, half = c.SourceExit.HalfExtents },
                    destination_exit = new { center = c.DestinationExit.Center, half = c.DestinationExit.HalfExtents }
                }),
                outputs = compiled.Assets.Keys.Order().Select(tag => $"{tag:X8}")
            }));
        }
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("PYTHON") ?? "python") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-I", "-B", script, oracle, folder, cases.Length.ToString() }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Console.Write(output);
        if (process.ExitCode != 0) throw new InvalidDataException("World Studio parity failed: " + error);
        Console.WriteLine($"PASS parity cases={cases.Length} oracle={oracle}");
    }
}
