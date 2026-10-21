using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

internal static class InstalledChecks
{
    internal static void Run(string game, string? keys, string root)
    {
        using var catalog = PackageCatalog.Open(game, keys, cacheOptions: new() { Enabled = false });
        byte[] definitions = catalog.ReadOriginal(ActivityRegistrationTables.DefinitionTag), clients = catalog.ReadOriginal(ActivityRegistrationTables.ClientTag);
        var template = ActivityRegistrationTables.ReadDefinitions(definitions).Single(row => row.Hash == 0x953F1741); // Luna free roam
        uint source = catalog.NamedTags.First(row => row.ClassHash == 0x80808AAE && row.Name == template.Name).TagHash;
        byte[] sourceRoot = catalog.ReadOriginal(source);
        uint scenarioTag = BinaryPrimitives.ReadUInt32LittleEndian(sourceRoot.AsSpan(64));
        byte[] scenario = catalog.ReadOriginal(scenarioTag);
        int stemAt = checked(48 + (int)BinaryPrimitives.ReadInt64LittleEndian(scenario.AsSpan(48)));
        string stem = Encoding.ASCII.GetString(scenario, stemAt, Array.IndexOf(scenario, (byte)0, stemAt) - stemAt);
        uint map = catalog.NamedTags.First(row => row.ClassHash == 0x808091DE && row.Name == "map:" + stem + ":root").TagHash;
        ushort package = Enumerable.Range(0x100, 0xC00).Select(value => (ushort)value).First(value => !catalog.ContainsInstalledPackage(value));
        var project = StudioProject.Create(Path.Combine(root, "installed-project"), "Installed activity read-only check");
        project.ActivityRegistrationCompiler = ActivityRegistrationCompiler.Default;
        var recipe = new AddedActivity(template.Hash, "studio_registration_check", "Registration check", template.Type, template.Destination, map, stem, package, 0);
        var plan = ActivityRegistrationCompiler.Prepare(project, catalog, recipe);
        var tables = ActivityRegistrationTables.Append(definitions, clients, [plan.Row]);
        string output = Path.Combine(root, "installed-parity"); Directory.CreateDirectory(output);
        foreach (var (name, bytes) in new[] { ("definitions.input", definitions), ("clients.input", clients),
            ("definitions.actual", tables.Definitions), ("clients.actual", tables.Clients) }) File.WriteAllBytes(Path.Combine(output, name + ".bin"), bytes);
        File.WriteAllText(Path.Combine(output, "request.json"), JsonSerializer.Serialize(new { template = template.Hash, hash = plan.Row.Hash, name = recipe.InternalName }));
        if (Environment.GetEnvironmentVariable("WORLD_STUDIO_SOURCE") is { Length: > 0 } oracle
            && Environment.GetEnvironmentVariable("SUNRISE_PARITY_DRIVERS") is { Length: > 0 } drivers)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("PYTHON") ?? "python") { UseShellExecute = false };
            foreach (string arg in new[] { "-I", Path.Combine(drivers, "activity_oracle.py"), oracle, output, "--installed" }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!; process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidDataException("Installed Python table parity failed.");
        }
        project.ApplyTransaction(plan.Additions, _ => new(new Dictionary<uint, byte[]> {
            [ActivityRegistrationTables.DefinitionTag] = tables.Definitions, [ActivityRegistrationTables.ClientTag] = tables.Clients }) { AddedActivities = [recipe] }, catalog);
        var compiled = project.CompileActivities(catalog);
        Console.WriteLine($"activity installed-originals={ActivityRegistrationTables.ReadDefinitions(definitions).Count} appended={ActivityRegistrationTables.ReadDefinitions(tables.Definitions).Count} assets={plan.Additions.Count} deterministic-compile={compiled.Count} output={output}");
    }
}
