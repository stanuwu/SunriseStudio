using System.Text;
using System.Text.Json.Nodes;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

/** Mod installs target a synthetic installation under the test folder, never the real game. */
internal static class ModInstallChecks
{
    private static readonly ModMetadata Example = new("dev.projectsunrise.example", "1.0.0", "Example",
        "What the mod does.", ["Name"], "https://example.com/mod",
        [new("dev.projectsunrise.base", "1.2.0", "1.9.0")], ["dev.other.mod"]);

    public static void Run(string parent)
    {
        Validation();
        ConfigValidation();
        ManifestBytes();
        UpdatePlan();
        string install = Path.Combine(parent, "mod-install");
        string packages = Path.Combine(install, "packages");
        Directory.CreateDirectory(packages);
        PackageWriter.WriteNew(Path.Combine(packages, "w64_mod_0100_0.pkg"), 0x100,
            [new(0x80800001, 0x100A, Encoding.UTF8.GetBytes("Installed patchable asset."), "mod:patchable")]);
        PackageWriter.WriteNew(Path.Combine(packages, "w64_mod_unp1_0.pkg"), 0x109,
            [new(0x80800001, 0x100A, Encoding.UTF8.GetBytes("Installed startup asset."), "mod:startup")]);
        using var catalog = PackageCatalog.Open(install);
        uint patchable = catalog.Entries.Single(entry => entry.PackageName == "w64_mod_0100_0").TagHash;
        uint startup = catalog.Entries.Single(entry => entry.PackageName == "w64_mod_unp1_0").TagHash;
        string mods = ModInstaller.ModsDirectory(install);
        ProjectMetadata(parent);
        PackageLimit(parent, catalog);
        StartupRefusal(parent, catalog, startup, mods);
        Install(parent, catalog, patchable, mods);
        ModUpdateSafetyChecks.Run(parent, catalog);
        ModSourceChecks.Run(parent, catalog);
        Check(Entries(packages) == "w64_mod_0100_0.pkg|w64_mod_unp1_0.pkg", "Install wrote into the packages folder.");
    }

    private static void Validation()
    {
        Check(ModManifest.Validate(Example).Count == 0, "Valid example metadata was refused.");
        var minimal = new ModMetadata("a.b", "0.0.0", new string('n', 64));
        Check(ModManifest.Validate(minimal).Count == 0, "Shortest id or 64-byte name was refused.");
        Check(ModManifest.Validate(minimal with
        {
            Id = new string('a', 31) + "." + new string('b', 32), Version = "65535.65535.65535", Name = new string('\u00E9', 32)
        }).Count == 0, "Longest id, highest version or 64-byte UTF-8 name was refused.");
        foreach (string id in new[] { "", "ab", "a." + new string('b', 63), "Dev.example", "devexample", "dev..example",
            ".dev.example", "dev.example.", "dev.-example", "dev.ex ample", "dev.ex\u00E4mple" })
            Refused(minimal with { Id = id }, "Mod id");
        foreach (string version in new[] { "", "1.0", "1.0.0.0", "01.0.0", "1.00.0", "65536.0.0", "1.-1.0", "a.b.c",
            "1..0", " 1.0.0", "1.0.0 " })
            Refused(minimal with { Version = version }, "Version");
        Refused(minimal with { Name = "" }, "Name is required");
        Refused(minimal with { Name = new string('n', 65) }, "Name must be");
        Refused(minimal with { Name = new string('\u00E9', 33) }, "Name must be");
        Refused(minimal with { Name = "tab\tname" }, "control characters");
        Check(ModManifest.Validate(minimal with { Description = new string('d', 512), Src = new string('s', 256) }).Count == 0,
            "Description or source at its byte limit was refused.");
        Refused(minimal with { Description = new string('d', 513) }, "Description");
        Refused(minimal with { Src = new string('s', 257) }, "Source");
        Refused(minimal with { Authors = Enumerable.Repeat("x", 9).ToArray() }, "8 authors");
        Refused(minimal with { Authors = [""] }, "Author 1");
        Refused(minimal with { Authors = ["ok", new string('a', 65)] }, "Author 2");
        var dependency = new ModDependency("dev.base.mod", "1.0.0");
        ModDependency[] ManyDependencies(int count) =>
            Enumerable.Range(0, count).Select(index => dependency with { Id = $"dev.base{index}.mod" }).ToArray();
        string[] ManyIncompatible(int count) => Enumerable.Range(0, count).Select(index => $"dev.other{index}.mod").ToArray();
        Check(ModManifest.Validate(minimal with
        {
            Authors = Enumerable.Repeat("x", 8).ToArray(), Dependencies = ManyDependencies(32), Incompatible = ManyIncompatible(32)
        }).Count == 0, "Lists at their limits were refused.");
        Refused(minimal with { Dependencies = ManyDependencies(33) }, "32 dependencies");
        Refused(minimal with { Dependencies = [dependency with { Id = "a.b" }] }, "Dependency 1 cannot be the mod itself");
        Refused(minimal with { Dependencies = [dependency with { Id = "Base" }] }, "Dependency 1 id");
        Refused(minimal with { Dependencies = [dependency with { MinVersion = "" }] }, "Dependency 1 minimum version");
        Refused(minimal with { Dependencies = [dependency with { MaxVersion = "2.0" }] }, "Dependency 1 maximum version");
        Refused(minimal with { Incompatible = ManyIncompatible(33) }, "32 incompatible ids");
        Refused(minimal with { Incompatible = ["a.b"] }, "Incompatible id 1 cannot be the mod itself");
        Refused(minimal with { Incompatible = ["other"] }, "Incompatible id 1");
        foreach (var (minimum, maximum) in new[] { ("1.0.0", "1.0.0"), ("1.9.0", "1.10.0"), ("1.2.9", "1.3.0"), ("0.0.1", "65535.0.0") })
            Check(ModManifest.Validate(minimal with { Dependencies = [dependency with { MinVersion = minimum, MaxVersion = maximum }] })
                .Count == 0, $"Range {minimum} to {maximum} was refused.");
        foreach (var (minimum, maximum) in new[] { ("1.2.0", "1.1.9"), ("2.0.0", "1.65535.65535"), ("1.0.10", "1.0.9"), ("1.10.0", "1.9.0") })
            Refused(minimal with { Dependencies = [dependency with { MinVersion = minimum, MaxVersion = maximum }] },
                "Dependency dev.base.mod has maxVersion below minVersion.");
        var repeated = ModManifest.Validate(minimal with
            { Dependencies = [dependency, dependency with { MinVersion = "2.0.0" }, dependency] });
        Check(repeated.Count == 1 && repeated[0] == "Dependency dev.base.mod is listed twice.",
            "A repeated dependency needs exactly one message.");
        var incompatibleTwice = ModManifest.Validate(minimal with { Incompatible = ["dev.other.mod", "dev.other.mod"] });
        Check(incompatibleTwice.Count == 1 && incompatibleTwice[0] == "Incompatible id dev.other.mod is listed twice.",
            "A repeated incompatible id needs exactly one message.");
        var conflict = ModManifest.Validate(minimal with { Dependencies = [dependency], Incompatible = ["dev.other.mod", "dev.base.mod"] });
        Check(conflict.Count == 1 && conflict[0] == "dev.base.mod is both a dependency and incompatible.",
            "An id that is both a dependency and incompatible needs exactly one message.");
        var both = ModManifest.Validate(minimal with { Id = "Ab.cd", Version = "1" });
        Check(both.Count == 2 && both[0].StartsWith("Mod id", StringComparison.Ordinal)
            && both[1].StartsWith("Version", StringComparison.Ordinal), "Each failed rule needs its own message.");
        Console.WriteLine("mod_metadata ids=checked versions=checked lengths=utf8 lists=bounded self_reference=refused "
            + "ranges=numeric duplicates=refused dependency_incompatible=refused");
    }

    private static void ConfigValidation()
    {
        var minimal = new ModMetadata("a.b", "0.0.0", "Config");
        var entry = new ModConfigEntry("a", ModConfigType.Int, "0");
        ModMetadata With(params ModConfigEntry[] entries) => minimal with { Config = entries };
        ModConfigEntry[] Many(int count) => Enumerable.Range(0, count).Select(index => entry with { Key = $"k{index}" }).ToArray();
        Check(ModManifest.Validate(With(Many(32))).Count == 0, "32 config entries were refused.");
        Refused(With(Many(33)), "32 config entries");
        foreach (string key in new[] { "a", "a_1", "z9", new string('k', 32) })
            Check(ModManifest.Validate(With(entry with { Key = key })).Count == 0, $"Config key {key} was refused.");
        foreach (string key in new[] { "A", "1a", "_a", "a-b", "a b", "a.b", new string((char)0xE9, 1), new string('k', 33) })
            Refused(With(entry with { Key = key }), "Config entry 1 key must be");
        Refused(With(entry with { Key = "" }), "Config entry 1 key is required");
        Refused(With(entry, entry with { Type = ModConfigType.Bool, Default = "true" }), "Config key a is listed twice.");
        Refused(With(entry with { Type = (ModConfigType)3 }), "Config entry 1 type");
        Refused(With(entry with { Default = "" }), "Config entry 1 default is required");
        foreach (string value in new[] { "0", "-2147483648", "2147483647", "+7" })
            Check(ModManifest.Validate(With(entry with { Default = value })).Count == 0, $"Int default {value} was refused.");
        foreach (string value in new[] { "2147483648", "-2147483649", "1.5", "1e3", " 1", "1 ", "0x10", "one" })
            Refused(With(entry with { Default = value }), "Config entry 1 default must be a whole number");
        var real = entry with { Type = ModConfigType.Float };
        foreach (string value in new[] { "0", "-0.5", "1e-3", "1E+20", "3.4028235e38" })
            Check(ModManifest.Validate(With(real with { Default = value })).Count == 0, $"Float default {value} was refused.");
        foreach (string value in new[] { "NaN", "Infinity", "-Infinity", "1e39", "3.5e38", "1,5", " 1", "abc" })
            Refused(With(real with { Default = value }), "Config entry 1 default must be a finite number");
        var flag = entry with { Type = ModConfigType.Bool };
        foreach (string value in new[] { "true", "false" })
            Check(ModManifest.Validate(With(flag with { Default = value })).Count == 0, $"Bool default {value} was refused.");
        foreach (string value in new[] { "True", "FALSE", "1", "0", "yes" })
            Refused(With(flag with { Default = value }), "Config entry 1 default must be true or false");
        foreach (string name in new[] { "", new string('n', 64), new string((char)0xE9, 32) })
            Check(ModManifest.Validate(With(entry with { Name = name })).Count == 0, "A config name within its limit was refused.");
        Refused(With(entry with { Name = new string('n', 65) }), "Config entry 1 name must be");
        Refused(With(entry with { Name = new string((char)0xE9, 33) }), "Config entry 1 name must be");
        Refused(With(entry with { Name = "tab\tname" }), "Config entry 1 name cannot contain control characters");
        Refused(minimal with { Config = [null!] }, "Config entry 1 is empty");
        Console.WriteLine("mod_config keys=checked types=checked defaults=typed names=utf8 count=bounded duplicates=refused");
    }

    private static void ManifestBytes()
    {
        const string expected = "{\n"
            + "  \"manifestVersion\": 2,\n"
            + "  \"id\": \"dev.projectsunrise.example\",\n"
            + "  \"version\": \"1.0.0\",\n"
            + "  \"name\": \"Example\",\n"
            + "  \"description\": \"What the mod does.\",\n"
            + "  \"authors\": [\n    \"Name\"\n  ],\n"
            + "  \"src\": \"https://example.com/mod\",\n"
            + "  \"dependencies\": [\n    {\n      \"id\": \"dev.projectsunrise.base\",\n"
            + "      \"minVersion\": \"1.2.0\",\n      \"maxVersion\": \"1.9.0\"\n    }\n  ],\n"
            + "  \"incompatible\": [\n    \"dev.other.mod\"\n  ]\n}\n";
        Check(ModManifest.Serialize(Example).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(expected)), "mod.json bytes differ.");
        const string minimal = "{\n  \"manifestVersion\": 2,\n  \"id\": \"a.b\",\n  \"version\": \"0.1.0\",\n"
            + "  \"name\": \"Caf\u00E9 \\\"q\\\"\"\n}\n";
        byte[] written = ModManifest.Serialize(new("a.b", "0.1.0", "Caf\u00E9 \"q\"", "", [], "", [], [], false, []));
        Check(written.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(minimal)), "Empty optional fields were written or text was escaped.");
        const string configured = "{\n  \"manifestVersion\": 2,\n  \"id\": \"a.b\",\n  \"version\": \"0.1.0\",\n  \"name\": \"Config\",\n"
            + "  \"incompatible\": [\n    \"dev.other.mod\"\n  ],\n"
            + "  \"cheat\": true,\n"
            + "  \"config\": [\n"
            + "    {\n      \"key\": \"respawn_ms\",\n      \"type\": \"int\",\n      \"default\": -5000,\n"
            + "      \"name\": \"Respawn delay\"\n    },\n"
            + "    {\n      \"key\": \"scale\",\n      \"type\": \"float\",\n      \"default\": 0.5\n    },\n"
            + "    {\n      \"key\": \"hard_mode\",\n      \"type\": \"bool\",\n      \"default\": true\n    }\n"
            + "  ]\n}\n";
        byte[] withConfig = ModManifest.Serialize(new("a.b", "0.1.0", "Config", Incompatible: ["dev.other.mod"], Cheat: true,
            Config: [new("respawn_ms", ModConfigType.Int, "-5000", "Respawn delay"), new("scale", ModConfigType.Float, "0.5", ""),
                new("hard_mode", ModConfigType.Bool, "true")]));
        Check(withConfig.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(configured)), "Cheat or config bytes differ.");
        Throws<InvalidDataException>(() => ModManifest.Serialize(Example with { Id = "bad" }));
        Throws<InvalidDataException>(() => ModManifest.Serialize(Example with { Config = [new("a", ModConfigType.Int, "1.5")] }));
        Console.WriteLine("mod_manifest version=2 bytes=exact bom=none optional_empty=omitted utf8=raw cheat=after_incompatible "
            + "config=typed_defaults");
    }

    /** The update decision is pure: only script bytes may differ from what the running game read at boot. */
    private static void UpdatePlan()
    {
        byte[] manifest = [1, 2, 3], script = [4], other = [5];
        var packages = new Dictionary<string, string>(StringComparer.Ordinal) { ["packages/w64_sunrise_0c00_0.pkg"] = "AA" };
        Dictionary<string, byte[]> Scripts(params (string Path, byte[] Data)[] files) =>
            files.ToDictionary(file => file.Path, file => file.Data, StringComparer.OrdinalIgnoreCase);
        var installed = new ModFolderContents(manifest, packages, Scripts(("alpha/alpha.lua", script), ("gamma/gamma.lua", script)));
        var same = ModInstaller.PlanUpdate(installed, installed with { Manifest = [1, 2, 3] });
        Check(same.Restart is null && same.Writes.Count == 0 && same.Deletes.Count == 0, "An unchanged mod planned work.");
        var scripts = ModInstaller.PlanUpdate(installed, installed with
            { Scripts = Scripts(("alpha/alpha.lua", other), ("beta/beta.lua", script)) });
        Check(scripts.Restart is null && string.Join('|', scripts.Writes.Select(write => write.RelativePath)) == "alpha/alpha.lua|beta/beta.lua"
            && scripts.Writes[0].Data.SequenceEqual(other) && string.Join('|', scripts.Deletes) == "gamma/gamma.lua",
            "Script changes were planned wrongly.");
        var caseOnly = ModInstaller.PlanUpdate(installed, installed with
            { Scripts = Scripts(("Alpha/Alpha.lua", script), ("gamma/gamma.lua", script)) });
        Check(caseOnly.Restart is null && caseOnly.Writes.Count == 0 && caseOnly.Deletes.Count == 0,
            "A case-only path change deleted or rewrote a script.");
        const string close = ". Close the game and use Build and install.";
        string Restart(ModFolderContents from, ModFolderContents to)
        {
            var plan = ModInstaller.PlanUpdate(from, to);
            Check(plan.Writes.Count == 0 && plan.Deletes.Count == 0, "A restart plan still writes scripts.");
            return plan.Restart ?? "";
        }
        Dictionary<string, string> Packages(params (string Path, string Hash)[] files) =>
            files.ToDictionary(file => file.Path, file => file.Hash, StringComparer.Ordinal);
        Check(Restart(installed, installed with { Manifest = [1, 2, 4] }) == "The mod details changed" + close,
            "A changed mod.json did not need a restart.");
        foreach (var changed in new[]
        {
            Packages(("packages/w64_sunrise_0c00_0.pkg", "AB")), Packages(("patches/w64_sunrise_0c00_0.pkg", "AA")),
            Packages(("packages/w64_sunrise_0c00_0.pkg", "AA"), ("patches/w64_mod_0100_1.pkg", "CC")), Packages(),
            Packages(("packages/w64_sunrise_0c00_0.pkg", "AA"), ("packages/w64_sunrise_0c00_0.merge", "DD"))
        })
            Check(Restart(installed, installed with { Packages = changed }) == "The packages or patches changed" + close,
                "A changed package set did not need a restart.");
        Check(Restart(installed, installed with { Packages = Packages(("packages/w64_sunrise_0c00_0.pkg", "aa")) }) == "",
            "Hash letter case needed a restart.");
        Check(Restart(installed, installed with { Scripts = null }) == "The mod no longer has mission scripts" + close
            && Restart(installed with { Scripts = null }, installed) == "The mod now has mission scripts" + close,
            "A changed scripts folder did not need a restart.");
        Check(Restart(installed, installed with { Manifest = [9], Scripts = null })
            == "The mod details changed. The mod no longer has mission scripts" + close, "Restart reasons were not all named.");
        Console.WriteLine("mod_update_plan scripts=in_place manifest=restart packages=restart scripts_root=restart case=kept");
    }

    private static void ProjectMetadata(string parent)
    {
        var project = StudioProject.Create(Path.Combine(parent, "mod-metadata"), "Metadata project");
        byte[] unset = File.ReadAllBytes(project.ManifestPath);
        Check(project.Mod is null && !Encoding.UTF8.GetString(unset).Contains("\"mod\"", StringComparison.Ordinal),
            "A project without mod details wrote a mod member.");
        Throws<InvalidDataException>(() => project.SetMod(Example with { Version = "1.0" }));
        Check(File.ReadAllBytes(project.ManifestPath).SequenceEqual(unset), "Invalid mod details changed the manifest.");
        project.SetMod(Example);
        var reopened = StudioProject.Open(project.DirectoryPath).Mod!;
        Check(reopened.Id == Example.Id && reopened.Version == Example.Version && reopened.Name == Example.Name
            && reopened.Description == Example.Description && reopened.Authors!.SequenceEqual(Example.Authors!)
            && reopened.Src == Example.Src && reopened.Dependencies!.SequenceEqual(Example.Dependencies!)
            && reopened.Incompatible!.SequenceEqual(Example.Incompatible!), "Mod details changed across reopen.");
        string saved = File.ReadAllText(project.ManifestPath);
        Check(!reopened.Cheat && reopened.Config is null && !saved.Contains("\"cheat\"", StringComparison.Ordinal)
            && !saved.Contains("\"config\"", StringComparison.Ordinal), "Unset cheat or config was written to the project.");
        var configured = Example with
        {
            Cheat = true,
            Config = [new("respawn_ms", ModConfigType.Int, "5000", "Respawn delay"), new("hard_mode", ModConfigType.Bool, "false")]
        };
        project.SetMod(configured);
        var configuredReopened = StudioProject.Open(project.DirectoryPath).Mod!;
        Check(configuredReopened.Cheat && configuredReopened.Config!.SequenceEqual(configured.Config!),
            "Cheat or config changed across reopen.");
        project.SetMod(null);
        Check(File.ReadAllBytes(project.ManifestPath).SequenceEqual(unset) && StudioProject.Open(project.DirectoryPath).Mod is null,
            "Clearing mod details left a mod member.");
        Console.WriteLine("mod_project saved=reopened unset=omitted invalid=refused cheat_config=reopened");
    }

    private static void PackageLimit(string parent, PackageCatalog catalog)
    {
        string output = Path.Combine(parent, "package-limit");
        PackageWriter.WriteNew(Path.Combine(output, "w64_sunrise_0cff_0.pkg"), 0x0CFF, [new(0x80800001, 0x100A, [1])]);
        Throws<InvalidDataException>(() => PackageWriter.WriteNew(Path.Combine(output, "w64_sunrise_0d00_0.pkg"), 0x0D00,
            [new(0x80800001, 0x100A, [1])]));
        var project = StudioProject.Create(Path.Combine(parent, "package-limit-project"), "Package limit");
        Throws<InvalidDataException>(() => project.AddAsset("above.bin", [1], new(0x0D00, 0x80800001, 0x100A)));
        project.AddAsset("legacy.bin", [1, 2], new(0x0C00, 0x80800001, 0x100A));
        var manifest = JsonNode.Parse(File.ReadAllText(project.ManifestPath))!;
        manifest["addedAssets"]![0]!["native"]!["packageId"] = 0x1900;
        File.WriteAllText(project.ManifestPath, manifest.ToJsonString());
        var legacy = StudioProject.Open(project.DirectoryPath);
        var refused = Throws<InvalidOperationException>(() => legacy.Build(catalog));
        Check(refused.Message.Contains("1900", StringComparison.Ordinal) && refused.Message.Contains("0CFF", StringComparison.Ordinal)
            && !Directory.Exists(Path.Combine(project.DirectoryPath, "build")), "An old package id was built or not named.");
        Console.WriteLine("package_id_limit last=0CFF above=refused old_project=opens old_build=refused");
    }

    private static void StartupRefusal(string parent, PackageCatalog catalog, uint startup, string mods)
    {
        var project = StudioProject.Create(Path.Combine(parent, "mod-startup"), "Startup edit");
        project.SetMod(Example);
        byte[] edited = catalog.ReadOriginal(startup);
        edited[0] ^= 1;
        project.ApplyEdit(startup, edited, catalog);
        var refused = Throws<InvalidOperationException>(() => ModInstaller.BuildAndInstall(project, catalog, []));
        Check(refused.Message.Contains("w64_mod_unp1_0", StringComparison.Ordinal), "Startup refusal does not name the package.");
        Check(!Directory.Exists(Path.Combine(project.DirectoryPath, "build")) && !Directory.Exists(mods), "A refused install wrote output.");
        var build = project.Build(catalog);
        Check(Path.GetFileName(build.PackageFiles.Single()) == "w64_mod_unp1_0.pkg", "Build packages lost the startup replacement.");
        var receipt = Throws<InvalidOperationException>(() =>
            ModInstaller.Install(Example, build, [], catalog.GameRoot, catalog.ContainsInstalledPackage));
        Check(receipt.Message.Contains("w64_mod_unp1_0", StringComparison.Ordinal) && !Directory.Exists(mods),
            "A startup replacement reached the mods folder.");
        Console.WriteLine("mod_startup install=refused build=replacement receipt=refused");
    }

    private static void Install(string parent, PackageCatalog catalog, uint patchable, string mods)
    {
        var project = StudioProject.Create(Path.Combine(parent, "mod-project"), "Mod project");
        byte[] controller = Encoding.UTF8.GetBytes("return {}\n");
        ModScriptFile[] scripts = [new("alpha/alpha.lua", controller)];
        Throws<InvalidOperationException>(() => ModInstaller.BuildAndInstall(project, catalog, scripts));
        project.SetMod(Example);
        byte[] edited = catalog.ReadOriginal(patchable);
        edited[0] ^= 1;
        project.ApplyEdit(patchable, edited, catalog);
        project.AddAsset("new-native.bin", [5, 6, 7], new(0x0C00, 0x80800003, 0x100A, "mod:new"));
        Throws<InvalidDataException>(() => ModInstaller.BuildAndInstall(project, catalog, [new("alpha/beta.more.lua", controller)]));
        Throws<InvalidDataException>(() => ModInstaller.BuildAndInstall(project, catalog, [new("../x.lua", controller)]));
        Throws<InvalidDataException>(() => ModInstaller.BuildAndInstall(project, catalog, [scripts[0], scripts[0]]));
        Check(!Directory.Exists(Path.Combine(project.DirectoryPath, "build")), "A refused script list built packages.");

        var first = ModInstaller.BuildAndInstall(project, catalog, scripts);
        string target = Path.Combine(mods, Example.Id);
        Check(first.ModDirectory == target && first.PackageFiles == 1 && first.PatchFiles == 1 && first.ScriptFiles == 1,
            "Install counts differ.");
        Check(Entries(target) == ".studio-live.json|.studio-sources.json|mod.json|packages|patches|refmap.bin|scripts"
            && Entries(Path.Combine(target, "patches")) == "w64_mod_0100_1.merge|w64_mod_0100_1.pkg"
            && Entries(Path.Combine(target, "packages")) == "w64_sunrise_0c00_0.merge|w64_sunrise_0c00_0.pkg",
            "Built packages or their merge sidecars were sorted into the wrong folders.");
        foreach (string built in first.Build!.PackageFiles)
        {
            string folder = Path.GetFileName(built) == "w64_mod_0100_1.pkg" ? "patches" : "packages";
            foreach (string file in new[] { built, Path.ChangeExtension(built, ".merge") })
                Check(File.ReadAllBytes(Path.Combine(target, folder, Path.GetFileName(file))).SequenceEqual(File.ReadAllBytes(file)),
                    "An installed package or sidecar differs from its build output.");
        }
        SidecarInstall(project, catalog, first.Build, target, scripts);
        Check(File.ReadAllBytes(Path.Combine(target, "mod.json")).SequenceEqual(ModManifest.Serialize(Example, null, [0x0C00])), "Installed mod.json differs.");
        Check(File.ReadAllBytes(Path.Combine(target, "scripts", "alpha", "alpha.lua")).SequenceEqual(controller), "Script layout differs.");
        Check(Entries(mods) == Example.Id, "Staging left a folder in mods.");
        Console.WriteLine("mod_install patches=installed_id packages=new_id scripts=stem_layout sdk=absent staging=removed");

        File.WriteAllText(Path.Combine(target, "stale.txt"), "old");
        project.SetMod(Example with { Version = "1.1.0" });
        ModInstaller.BuildAndInstall(project, catalog, []);
        Check(!File.Exists(Path.Combine(target, "stale.txt")) && Entries(target) == ".studio-live.json|.studio-sources.json|mod.json|packages|patches|refmap.bin"
            && Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(target, "mod.json"))).Contains("\"1.1.0\"", StringComparison.Ordinal),
            "Reinstall did not replace the previous folder.");
        Check(Entries(mods) == Example.Id, "Swap left the previous install beside the new one.");

        byte[] installed = File.ReadAllBytes(Path.Combine(target, "mod.json"));
        project.SetMod(Example with { Version = "2.0.0" });
        using (new FileStream(Path.Combine(target, "mod.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var held = Throws<IOException>(() => ModInstaller.BuildAndInstall(project, catalog, scripts));
            Check(held.Message == "Close the game, then install again.", "A held install gave the wrong message.");
        }
        Check(File.ReadAllBytes(Path.Combine(target, "mod.json")).SequenceEqual(installed) && Entries(mods) == Example.Id,
            "A held install was not left in place.");

        var staged = ModInstaller.Stage(Example with { Version = "3.0.0" }, null, scripts, catalog.GameRoot, catalog.ContainsInstalledPackage);
        Check(Path.GetFileName(staged.StagingDirectory).StartsWith("." + Example.Id + ".installing-", StringComparison.Ordinal),
            "Staging is not hidden from the loader.");
        using (new FileStream(Path.Combine(staged.StagingDirectory, "mod.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failed = Throws<IOException>(() => ModInstaller.Swap(staged.StagingDirectory, staged.ModDirectory));
            Check(failed.Message == "Close the game, then install again.", "A failed swap gave the wrong message.");
        }
        Check(File.ReadAllBytes(Path.Combine(target, "mod.json")).SequenceEqual(installed)
            && !Directory.EnumerateDirectories(mods, "*.old-*").Any(), "A failed swap did not restore the previous install.");
        if (Directory.Exists(staged.StagingDirectory)) Directory.Delete(staged.StagingDirectory, true);
        Console.WriteLine("mod_swap replace=complete held_install=kept failed_swap=restored");

        var scriptsOnly = StudioProject.Create(Path.Combine(parent, "mod-scripts"), "Scripts only");
        scriptsOnly.SetMod(new("dev.projectsunrise.scripts", "1.0.0", "Scripts only"));
        Throws<InvalidOperationException>(() => ModInstaller.BuildAndInstall(scriptsOnly, catalog, []));
        var onlyScripts = ModInstaller.BuildAndInstall(scriptsOnly, catalog, scripts);
        Check(onlyScripts.Build is null && Entries(onlyScripts.ModDirectory) == ".studio-live.json|.studio-sources.json|mod.json|scripts",
            "A scripts-only mod created package folders.");
        Update(parent, catalog, scriptsOnly, onlyScripts.ModDirectory);
    }

    /** The game reads merge sidecars at boot: a changed one needs a restart, and one changed after the build is refused. */
    private static void SidecarInstall(StudioProject project, PackageCatalog catalog, ProjectBuildResult build, string target,
        ModScriptFile[] scripts)
    {
        var same = ModInstaller.BuildAndUpdate(project, catalog, scripts);
        Check(same.Written == 0 && same.Deleted == 0, "An unchanged rebuild planned script work.");
        string installed = Path.Combine(target, "patches", "w64_mod_0100_1.merge");
        byte[] original = File.ReadAllBytes(installed);
        File.WriteAllBytes(installed, [.. original, 0]);
        var restart = Throws<InvalidOperationException>(() => ModInstaller.BuildAndUpdate(project, catalog, scripts));
        Check(restart.Message == "The packages or patches changed. Close the game and use Build and install.",
            "A changed merge sidecar did not need a restart.");
        File.WriteAllBytes(installed, original);
        string built = Path.ChangeExtension(build.PackageFiles.Single(file => Path.GetFileName(file) == "w64_mod_0100_1.pkg"), ".merge");
        byte[] builtBytes = File.ReadAllBytes(built);
        File.WriteAllBytes(built, [.. builtBytes, 0]);
        var stale = Throws<IOException>(() => ModInstaller.Install(Example, build, scripts, catalog.GameRoot, catalog.ContainsInstalledPackage));
        Check(stale.Message == "w64_mod_0100_1.merge changed after the build." && File.ReadAllBytes(installed).SequenceEqual(original),
            "A merge sidecar changed after the build was installed.");
        File.WriteAllBytes(built, builtBytes);
        Console.WriteLine("mod_sidecars stage=beside_package hash=checked update=restart_on_change");
    }

    /** Updates run against the scripts-only mod the install checks leave in place. */
    private static void Update(string parent, PackageCatalog catalog, StudioProject project, string target)
    {
        byte[] changed = Encoding.UTF8.GetBytes("return { changed = true }\n"), added = Encoding.UTF8.GetBytes("return {}\n");
        string root = Path.Combine(target, "scripts");
        byte[] manifest = File.ReadAllBytes(Path.Combine(target, "mod.json"));
        var update = ModInstaller.BuildAndUpdate(project, catalog, [new("alpha/alpha.lua", changed), new("beta/beta.lua", added)]);
        Check(update.ModId == "dev.projectsunrise.scripts" && update.ModDirectory == target && update.Written == 2
            && update.Deleted == 0 && update.Build is null, "Update counts differ.");
        Check(File.ReadAllBytes(Path.Combine(root, "alpha", "alpha.lua")).SequenceEqual(changed)
            && File.ReadAllBytes(Path.Combine(root, "beta", "beta.lua")).SequenceEqual(added)
            && Entries(root) == "alpha|beta" && Entries(Path.Combine(root, "alpha")) == "alpha.lua"
            && Entries(Path.Combine(root, "beta")) == "beta.lua" && Entries(target) == ".studio-live.json|.studio-sources.json|mod.json|scripts"
            && File.ReadAllBytes(Path.Combine(target, "mod.json")).SequenceEqual(manifest), "Update wrote the wrong files.");
        var removal = ModInstaller.BuildAndUpdate(project, catalog, [new("beta/beta.lua", added)]);
        Check(removal.Written == 0 && removal.Deleted == 1 && Entries(root) == "beta", "Update kept a dropped script.");
        project.SetMod(project.Mod! with { Version = "1.0.1" });
        var restart = Throws<InvalidOperationException>(() => ModInstaller.BuildAndUpdate(project, catalog, [new("beta/beta.lua", changed)]));
        Check(restart.Message == "The mod details changed. Close the game and use Build and install."
            && File.ReadAllBytes(Path.Combine(root, "beta", "beta.lua")).SequenceEqual(added)
            && File.ReadAllBytes(Path.Combine(target, "mod.json")).SequenceEqual(manifest), "A refused update changed files.");
        var fresh = StudioProject.Create(Path.Combine(parent, "mod-fresh"), "Fresh");
        fresh.SetMod(new("dev.projectsunrise.fresh", "1.0.0", "Fresh"));
        var missing = Throws<InvalidOperationException>(() => ModInstaller.BuildAndUpdate(fresh, catalog, [new("beta/beta.lua", added)]));
        Check(missing.Message == "Install the mod once, then start the game."
            && !Directory.Exists(Path.Combine(ModInstaller.ModsDirectory(catalog.GameRoot), "dev.projectsunrise.fresh")),
            "An uninstalled mod was updated.");
        Console.WriteLine("mod_update scripts=replaced dropped=deleted manifest=restart uninstalled=refused");
    }

    private static string Entries(string directory) => string.Join('|',
        Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal));

    private static void Refused(ModMetadata metadata, string fragment) =>
        Check(ModManifest.Validate(metadata).Any(problem => problem.Contains(fragment, StringComparison.Ordinal)),
            $"Metadata was not refused for '{fragment}'.");

    private static void Check(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }

    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
