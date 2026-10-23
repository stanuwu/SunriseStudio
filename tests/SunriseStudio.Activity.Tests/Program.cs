using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SunriseStudio.Authoring;
using SunriseStudio.Editor;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

string root = Path.Combine(Environment.GetEnvironmentVariable("SUNRISE_ACTIVITY_OUTPUT") ?? Path.GetTempPath(), "activity-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidDataException(message); }
void Reject(Action action, string message)
{
    checks++;
    try { action(); } catch (InvalidDataException) { return; }
    throw new InvalidDataException(message);
}
try
{
    byte[] definitions = ActivityFixture.Definitions(), clients = ActivityFixture.Clients();
    var addition = new ActivityTableAddition(ActivityFixture.TemplateHash, 0x87654321, "new_activity", 9, 2);
    var tables = ActivityRegistrationTables.Append(definitions, clients, [addition]);
    Check(ActivityRegistrationTables.ReadDefinitions(tables.Definitions).Count == 3, "Definition append failed.");
    Check(ActivityRegistrationTables.ReadClients(tables.Clients).Count == 3, "Client append failed.");
    Check(ActivityRegistrationTables.ReadDefinitions(tables.Definitions).Take(2).SequenceEqual(ActivityRegistrationTables.ReadDefinitions(definitions)), "Original definitions changed.");
    Check(tables.Definitions.AsSpan(24, definitions.Length - 24).SequenceEqual(definitions.AsSpan(24)), "Original policy bytes changed.");
    Check(tables.Clients.AsSpan(40, clients.Length - 40).SequenceEqual(clients.AsSpan(40)), "Original client bytes changed.");
    Reject(() => ActivityRegistrationTables.Append(definitions, clients, [addition with { TemplateHash = 99 }]), "Missing template accepted.");
    Reject(() => ActivityRegistrationTables.Append(definitions, clients, [addition with { Hash = ActivityFixture.TemplateHash }]), "Hash collision accepted.");
    Reject(() => ActivityRegistrationTables.Append(definitions, clients, [addition with { Name = "Bad-Name" }]), "Invalid name accepted.");
    Reject(() => ActivityRegistrationTables.Append(definitions, clients, [addition, addition]), "Duplicate registration accepted.");
    var maximum = Enumerable.Range(0, 4093).Select(i => addition with { Hash = 0x66000000 + (uint)i, Name = "capacity_" + i }).ToArray();
    var full = ActivityRegistrationTables.Append(definitions, clients, maximum);
    Check(ActivityRegistrationTables.ReadDefinitions(full.Definitions).Count == 4095 && ActivityRegistrationTables.ReadClients(full.Clients).Count == 4095, "Maximum catalog did not decode.");
    Reject(() => ActivityRegistrationTables.Append(full.Definitions, full.Clients, [addition]), "Catalog capacity overflow accepted.");
    var broken = definitions.ToArray(); BinaryPrimitives.WriteInt64LittleEndian(broken.AsSpan(16), long.MaxValue);
    Reject(() => ActivityRegistrationTables.ReadDefinitions(broken), "Overflowing index accepted.");
    foreach (byte type in ActivityRegistrationCompiler.ProvenTypes.Keys) ActivityRegistrationCompiler.ValidateType(ActivityFixture.Types(false), ActivityFixture.Types(true), type);
    foreach (byte type in new byte[] { 1, 16, 18, 53 }) Reject(() => ActivityRegistrationCompiler.ValidateType(ActivityFixture.Types(false), ActivityFixture.Types(true), type), "Unproven/PvP/orbit type accepted.");
    byte[] scenario = ActivityFixture.Scenario();
    byte[] rebound = ActivityRegistrationIdentity.RebindScenario(scenario, "template", "new_activity", "map_copy");
    byte[] slice = ActivityRegistrationIdentity.RebindSlice(ActivityFixture.Slice(), "map_copy", ActivityFixture.Base + 5);
    var text = ActivityRegistrationIdentity.LocalizedText(new Dictionary<uint, string> { [ActivityRegistrationIdentity.Hash("map_copy")] = "New World é" }, 0x80A00001);
    var parity = new Dictionary<string, byte[]> { ["definitions"] = tables.Definitions, ["clients"] = tables.Clients,
        ["scenario"] = rebound, ["slice"] = slice, ["text-container"] = text.Container, ["text-data"] = text.Data };
    foreach (var (key, bytes) in parity) File.WriteAllBytes(Path.Combine(root, key + ".actual.bin"), bytes);
    File.WriteAllBytes(Path.Combine(root, "definitions.input.bin"), definitions); File.WriteAllBytes(Path.Combine(root, "clients.input.bin"), clients);
    File.WriteAllBytes(Path.Combine(root, "scenario.input.bin"), scenario); File.WriteAllBytes(Path.Combine(root, "slice.input.bin"), ActivityFixture.Slice());
    // The oracle and World Studio live outside this repository; WORLD_STUDIO_SOURCE and SUNRISE_PARITY_DRIVERS name them.
    string? oracle = Environment.GetEnvironmentVariable("WORLD_STUDIO_SOURCE"), drivers = Environment.GetEnvironmentVariable("SUNRISE_PARITY_DRIVERS");
    if (string.IsNullOrWhiteSpace(oracle) || string.IsNullOrWhiteSpace(drivers))
    {
        oracle = null;
        Console.WriteLine("SKIP activity python-parity: set WORLD_STUDIO_SOURCE and SUNRISE_PARITY_DRIVERS; frozen hashes still run.");
    }
    else
    {
        var process = new ProcessStartInfo(Environment.GetEnvironmentVariable("PYTHON") ?? "python") { UseShellExecute = false };
        foreach (string arg in new[] { "-I", Path.Combine(drivers, "activity_oracle.py"), oracle, root }) process.ArgumentList.Add(arg);
        using var child = Process.Start(process)!; child.WaitForExit(); Check(child.ExitCode == 0, "Python parity oracle failed.");
        foreach (var (key, bytes) in parity) Check(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(root, key + ".expected.bin"))), key + " differs from Python.");
    }
    string goldenPath = Path.Combine(AppContext.BaseDirectory, "registration-parity.json");
    if (File.Exists(goldenPath))
    {
        var golden = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(goldenPath))!;
        foreach (var (key, bytes) in parity) Check(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() == golden[key], key + " differs from frozen Python golden.");
    }
    else Check(oracle is not null, "Frozen parity golden is missing.");
    using (var golden = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "activity_registration_golden.json"))))
    {
        var display = ActivityRegistrationTables.ReadClients(clients)[0];
        int at = display.Offset + (int)BinaryPrimitives.ReadInt64LittleEndian(clients.AsSpan(display.Offset));
        Check(Convert.ToHexString(SHA256.HashData(clients.AsSpan(at, 36))).ToLowerInvariant()
            == golden.RootElement.GetProperty("children").GetProperty("empty_display").GetProperty("body_sha256").GetString(), "Native display profile differs from golden.");
        int record = ActivityRegistrationTables.ReadDefinitions(definitions)[0].Offset;
        at = record + 32 + (int)BinaryPrimitives.ReadInt64LittleEndian(definitions.AsSpan(record + 32));
        Check(Convert.ToHexString(SHA256.HashData(definitions.AsSpan(at, 72))).ToLowerInvariant()
            == golden.RootElement.GetProperty("children").GetProperty("matchmaking").GetProperty("body_sha256").GetString(), "Native matchmaking profile differs from golden.");
    }
    string install = Path.Combine(root, "install"); ActivityFixture.Create(install);
    using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
    void Wait()
    {
        var watch = Stopwatch.StartNew();
        while (workspace.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(60)) { workspace.Poll(); Thread.Sleep(1); }
        workspace.Poll(); Check(!workspace.IsBusy && workspace.Error.Length == 0, "Workspace: " + workspace.Error);
    }
    workspace.OpenInstallation(install); Wait();
    workspace.Select(ActivityFixture.Base); Wait();
    workspace.CreateProject(Path.Combine(root, "project"), "Activity fixture"); Wait();
    var workflow = new ActivityWorkflow(workspace);
    CreationField Field(string name) => workflow.Fields.Single(field => field.Label == name);
    Field("Template activity").Tag = ActivityFixture.Base;
    Field("Map root").Tag = ActivityFixture.Base + 2;
    workflow.Update(workspace);
    Field("Internal name").Text = "new_activity"; Field("Display name").Text = "New World";
    Check(workflow.Problems(workspace).Count == 0, string.Join("; ", workflow.Problems(workspace)));
    Check(CreationTypes.For(CreationTypes.ActivityKey, CreationMode.New).Available, "Activity creation is blocked.");
    workflow.Create(workspace); Wait();
    var project = workspace.Project!; var catalog = workspace.Catalog!;
    Check(project.AddedActivities.Count == 1 && project.Changes.Count == 2, "Registration did not atomically publish its two deltas and recipe.");
    using (var manifest = JsonDocument.Parse(File.ReadAllBytes(project.ManifestPath)))
        Check(manifest.RootElement.GetProperty("version").GetInt32() == 8 && manifest.RootElement.GetProperty("features").EnumerateArray()
            .Select(name => name.GetString()).SequenceEqual([ProjectFeatures.AddedActivities]), "Manifest version did not advance with its feature.");
    var recipe = project.AddedActivities[0];
    byte[] savedManifest = File.ReadAllBytes(project.ManifestPath);
    var later = System.Text.Json.Nodes.JsonNode.Parse(savedManifest)!;
    later["features"] = new System.Text.Json.Nodes.JsonArray(ProjectFeatures.AddedActivities, "later-feature");
    string laterPath = Path.Combine(root, "later-project", StudioProject.ProjectFileName);
    Directory.CreateDirectory(Path.GetDirectoryName(laterPath)!); File.WriteAllText(laterPath, later.ToJsonString());
    Reject(() => StudioProject.Open(laterPath), "A manifest naming an unknown feature opened.");
    Reject(() => project.ApplyTransaction([], _ => new(new Dictionary<uint, byte[]>()) { AddedActivities = [recipe with { InternalName = "invalid name" }] }, catalog),
        "Invalid registration transaction succeeded.");
    Check(File.ReadAllBytes(project.ManifestPath).AsSpan().SequenceEqual(savedManifest), "Failed registration changed the manifest.");
    var compiled = project.CompileActivities(catalog); int expectedAssets = project.AddedAssets.Count;
    var info = new AssetSceneLoader(catalog).DescribeScenario(recipe.Tag(1));
    Check(info.Bubbles.Count == 1 && info.Bubbles[0].States.Count == 1, "Studio scenario reader failed.");
    uint container = BinaryPrimitives.ReadUInt32LittleEndian(compiled[recipe.Tag(1)].AsSpan(40));
    Check(LocalizedStrings.LoadBanks(catalog, [container]).Get(container, ActivityRegistrationIdentity.Hash(recipe.ScenarioStem)) == "New World", "Studio localized-string reader failed.");
    workspace.Undo(); Wait(); Check(project.AddedActivities.Count == 0 && project.AddedAssets.Count == 0 && project.Changes.Count == 0, "Undo left a partial registration.");
    workspace.Redo(); Wait(); Check(project.AddedActivities.Count == 1 && project.AddedAssets.Count == expectedAssets, "Redo lost the recipe or assets.");
    _ = project.CompileActivities(catalog);
    var reopen = StudioProject.Open(project.ManifestPath); reopen.ActivityRegistrationCompiler = ActivityRegistrationCompiler.Default;
    Check(reopen.CompileActivities(catalog).Count == compiled.Count, "Reopened recipe failed to compile.");
    var missing = recipe with { TemplateActivity = 0x77777777 };
    Reject(() => ActivityRegistrationCompiler.Prepare(project, catalog, missing), "Non-installed template accepted.");
    var invalidName = recipe with { InternalName = new string('a', 40) };
    Reject(invalidName.Validate, "Oversized internal name accepted.");
    Reject(() => (recipe with { ScenarioStem = new string('a', 55) }).Validate(), "Oversized native map name accepted.");
    var secondWorkflow = new ActivityWorkflow(workspace);
    secondWorkflow.Fields.Single(field => field.Label == "Template activity").Tag = recipe.Tag();
    // Required fields filled to reach semantic validation of the non-installed source.
    foreach (var field in secondWorkflow.Fields.Where(field => field.Kind == CreationFieldKind.Text)) field.Text = "test";
    secondWorkflow.Fields.Single(field => field.Label == "Map root").Tag = ActivityFixture.Base + 2;
    secondWorkflow.Fields.Single(field => field.Label == "Template row").Text = "12345678";
    secondWorkflow.Fields.Single(field => field.Label == "Type").Text = "9";
    secondWorkflow.Fields.Single(field => field.Label == "Destination index").Text = "2";
    Check(secondWorkflow.Problems(workspace).Any(problem => problem.Contains("installed template", StringComparison.Ordinal)), "Workflow accepted a project template.");
    string manifestBefore = File.ReadAllText(project.ManifestPath);
    var built = ProjectBuilder.Build(project, catalog, Path.Combine(root, "build"));
    Check(File.ReadAllText(project.ManifestPath) == manifestBefore, "Build changed the source project.");
    string verification = Path.Combine(root, "readback", "packages"); Directory.CreateDirectory(verification);
    foreach (string path in Directory.EnumerateFiles(catalog.PackageDirectory, "*.pkg").Concat(built.PackageFiles)) File.Copy(path, Path.Combine(verification, Path.GetFileName(path)), true);
    using var readback = PackageCatalog.Open(Path.GetDirectoryName(verification)!, cacheOptions: new() { Enabled = false });
    foreach (var (tag, bytes) in compiled) Check(readback.Read(tag).AsSpan().SequenceEqual(bytes), $"Package readback differs at {tag:X8}.");
    Check(ActivityRegistrationTables.ReadDefinitions(readback.Read(ActivityRegistrationTables.DefinitionTag)).Count == 3, "Built table did not decode.");
    Check(PackageManifest.Read(readback.Read(0x81327D65)).Lists(recipe.Tag()), "Investment globals manifest missed the name-based activity dependency.");
    using var receipt = JsonDocument.Parse(File.ReadAllBytes(built.ReceiptPath));
    Check(receipt.RootElement.GetProperty("dependencyEdits").GetArrayLength() >= expectedAssets, "Dependency records missed registration assets.");
    Check(receipt.RootElement.GetProperty("addedAssetsInNoRecord").GetArrayLength() == 0, "Registration has unlisted assets.");
    Check(new AssetSceneLoader(readback).DescribeScenario(recipe.Tag(1)).Bubbles.Count == 1, "Built scenario did not decode.");
    Check(readback.NamedTags.Any(row => row.TagHash == recipe.Tag() && row.Name == recipe.InternalName), "Activity native name was not published.");
    var copied = StudioProject.Create(Path.Combine(root, "copy-project"), "Whole map copy");
    copied.ActivityRegistrationCompiler = ActivityRegistrationCompiler.Default;
    copied.AddDerivedAsset("map-copy.bin", ActivityFixture.Base + 2, catalog.ReadOriginal(ActivityFixture.Base + 2),
        new(0xAAA, 0x808091DE, 0x200A, "map:map_copy:root", 0), catalog);
    var copiedRecipe = recipe with { PackageId = 0xAAA, FirstEntryIndex = 1, MapRootTag = 0x81D54000, ScenarioStem = "map_copy", InternalName = "copied_activity" };
    var copiedPlan = ActivityRegistrationCompiler.Prepare(copied, catalog, copiedRecipe);
    var copiedTables = ActivityRegistrationTables.Append(definitions, clients, [copiedPlan.Row]);
    copied.ApplyTransaction(copiedPlan.Additions, _ => new(new Dictionary<uint, byte[]> { [ActivityRegistrationTables.DefinitionTag] = copiedTables.Definitions,
        [ActivityRegistrationTables.ClientTag] = copiedTables.Clients }) { AddedActivities = [copiedRecipe] }, catalog);
    Check(copied.CompileActivities(catalog).Count == copiedPlan.Additions.Count + 2, "Whole-map copy registration failed.");
    var copyBuild = ProjectBuilder.Build(copied, catalog, Path.Combine(root, "copy-build"));
    using var copyReceipt = JsonDocument.Parse(File.ReadAllBytes(copyBuild.ReceiptPath));
    Check(copyReceipt.RootElement.GetProperty("addedAssetsInNoRecord").GetArrayLength() == 0, "Named map root has no dependency record.");
    int gameArg = Array.IndexOf(args, "--game-root"), keysArg = Array.IndexOf(args, "--key-source");
    if (gameArg >= 0) InstalledChecks.Run(args[gameArg + 1], keysArg >= 0 ? args[keysArg + 1] : null, root);
    Console.WriteLine($"activity checks={checks} tables=byte-identical scenario=byte-identical text=byte-identical transaction=pass package-readback=pass output={root}");
}
catch (Exception error) { Console.Error.WriteLine(error); Console.Error.WriteLine("Fixture output: " + root); Environment.ExitCode = 1; }
