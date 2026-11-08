using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using SunriseStudio.Authoring;
using SunriseStudio.Editor;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

internal static class ConfigBindingChecks
{
    private const uint Tag = 0x80A00000, Class = 0x80804825;
    public static void Run(string parent, string? output = null)
    {
        byte[] source = ConfigBindingFixture.Create();
        string install = Path.Combine(parent, "config-install"), packages = Path.Combine(install, "packages");
        Directory.CreateDirectory(packages);
        string originalPackage = Path.Combine(packages, "w64_config_0100_0.pkg");
        PackageWriter.WriteNew(originalPackage, 0x100, [new(Class, 0x1000, source, "Config widget")]);
        using var catalog = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
        var project = StudioProject.Create(Path.Combine(parent, "config-project"), "Config test");
        project.ConfigBindingCompiler = CuiConfigBindingCompiler.Default;
        var mod = new ModMetadata("test.config", "1.0.0", "Typed config", Config:
            [new("threshold", ModConfigType.Int, "-123456789"), new("fraction", ModConfigType.Float, "0.125"),
             new("position", ModConfigType.Float, "10"), new("auto_width", ModConfigType.Bool, "true")]);
        project.SetMod(mod);
        SharedOwners(source, mod);
        var candidates = CuiConfigBindingCompiler.Default.FindSites(Tag, Class, source);
        var chosen = new[]
        {
            (Site: candidates.First(value => value.Type == ModConfigType.Int), Key: "threshold"),
            (Site: candidates.First(value => value.Type == ModConfigType.Float && value.Site.Profile == ConfigBindingLimits.ComparisonProfile), Key: "fraction"),
            (Site: candidates.First(value => value.Type == ModConfigType.Float && value.Site.OwnerIndex == 0), Key: "position"),
            (Site: candidates.First(value => value.Type == ModConfigType.Bool), Key: "auto_width")
        };
        var bindings = chosen.Select(value => new ProjectConfigBinding(value.Key, value.Site.Type, Tag, null,
            Class, BinaryDelta.Hash(source), source.Length, value.Site.Site)).ToArray();
        project.SetConfigBindings(bindings, catalog);
        Check(project.Changes.Count == 0 && project.AddedAssets.Count == 0 && project.ReadAsset(Tag, catalog).SequenceEqual(source), "bindings keep raw project bytes");
        Check(JsonNode.Parse(File.ReadAllText(project.ManifestPath))!["version"]!.GetValue<int>() == 4, "project v4");
        var compiled = project.CompileConfigBindings(catalog); byte[] bytes = compiled.Assets[Tag];
        Check(compiled.Sites.Count == 4 && compiled.Sites.All(site => site.SourceSha256 == BinaryDelta.Hash(bytes)), "shared final entry digest");
        Check(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(compiled.Sites.Single(site => site.Key == "threshold").Offset)) == -123456789, "full signed int default");
        Check(BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(compiled.Sites.Single(site => site.Key == "fraction").Offset)) == .125f, "fraction preserved");
        Check(bytes[compiled.Sites.Single(site => site.Key == "auto_width").Offset] == 1, "bool default");
        var oldDocument = CuiDocument.Read(Tag, Class, source); var nextDocument = CuiDocument.Read(Tag, Class, bytes);
        var originalX = oldDocument.Objects[0].Components[0].Properties[0];
        var nextX = nextDocument.Objects[0].Components[0].Properties[0];
        var siblingX = nextDocument.Objects[1].Components[0].Properties[0];
        Check(nextX.ValueOffset != originalX.ValueOffset && nextX.ValueOffset != siblingX.ValueOffset, "equal default still isolated");
        byte[] runtime = bytes.ToArray(); BinaryPrimitives.WriteSingleLittleEndian(runtime.AsSpan(nextX.ValueOffset), 999);
        Check(BinaryPrimitives.ReadSingleLittleEndian(runtime.AsSpan(siblingX.ValueOffset)) == 10, "runtime value leaves sibling unchanged");
        var oldComparisons = CuiBindings.Read(source); var comparisons = CuiBindings.Read(bytes);
        Check(comparisons[0].Transform.Offset != comparisons[1].Transform.Offset
            && comparisons[1].Transform.Operand == oldComparisons[1].Transform.Operand, "shared comparison isolated");
        byte[] manifest = File.ReadAllBytes(project.ManifestPath);
        Reject(() => project.SetMod(mod with { Config = mod.Config!.Where(value => value.Key != "threshold").ToArray() }));
        byte[] bad = source.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(oldComparisons[0].Transform.Offset + 4), 8);
        Reject(() => project.ApplyEdit(Tag, bad, catalog));
        Check(File.ReadAllBytes(project.ManifestPath).SequenceEqual(manifest) && project.Changes.Count == 0, "stale target refusal before publication");
        var build = project.Build(catalog);
        Check(build.PackageFiles.Count == 1 && build.ConfigBindings.SequenceEqual(compiled.Sites), "bound-only original forces patch");
        byte[] serialized = ModManifest.Serialize(mod, build.ConfigBindings);
        var json = JsonNode.Parse(serialized)!;
        Check(json["manifestVersion"]!.GetValue<int>() == 3 && json["configBindings"]!.AsArray().Count == 4, "mod v3 sites");
        foreach (var site in json["configBindings"]!.AsArray())
            Check(site!["profile"]!.GetValue<string>() == ConfigBindingLimits.PropertyProfile
                ? site["bindingOffset"] is null : site["propertyOffset"] is null && site["poolOffset"] is null, "profile fields only");
        var reopened = StudioProject.Open(project.DirectoryPath); reopened.ConfigBindingCompiler = CuiConfigBindingCompiler.Default;
        Check(reopened.CompileConfigBindings(catalog).Assets[Tag].SequenceEqual(bytes), "reopened compilation exact");
        string verify = Path.Combine(parent, "config-built"), verifyPackages = Path.Combine(verify, "packages");
        Directory.CreateDirectory(verifyPackages);
        foreach (string file in new[] { originalPackage }.Concat(build.PackageFiles)) File.Copy(file, Path.Combine(verifyPackages, Path.GetFileName(file)));
        using (var built = PackageCatalog.Open(verify, cacheOptions: new() { Enabled = false }))
            Check(built.Read(Tag).SequenceEqual(bytes), "built entry matches preview compilation");
        Refusals(project, catalog, bindings, compiled, mod);
        CompileRaces(project, catalog, bindings, mod, source, originalPackage, parent);
        Added(parent, catalog, source, mod);
        Startup(parent, source, mod, bindings);
        var rectangle = CuiLayout.Read(oldDocument)[1];
        byte[] relocated = CuiLayout.Apply(source, oldDocument, rectangle, rectangle.Rectangle with { Y = 55 });
        project.ApplyEdit(Tag, relocated, catalog);
        Check(project.CompileConfigBindings(catalog).Sites.Count == 4, "unrelated pool relocation keeps binding");
        project.SetConfigBindings([], catalog);
        Check(!File.ReadAllText(project.ManifestPath).Contains("configBindings") && JsonNode.Parse(ModManifest.Serialize(mod))!["manifestVersion"]!.GetValue<int>() == 2, "empty omission and downgrade");
        if (output is not null)
        {
            Directory.CreateDirectory(output); File.WriteAllBytes(Path.Combine(output, "source.bin"), source);
            File.WriteAllBytes(Path.Combine(output, "entry.bin"), bytes); File.WriteAllBytes(Path.Combine(output, "mod.json"), serialized);
            File.Copy(build.PackageFiles.Single(), Path.Combine(output, Path.GetFileName(build.PackageFiles.Single())), true);
            File.Copy(originalPackage, Path.Combine(output, Path.GetFileName(originalPackage)), true);
        }
        Workflow(parent, install);
        LogicalAliases(parent);
        Console.WriteLine("config_bindings types=exact isolation=pass project=metadata_only original=forced_plaintext build=pass relocation=pass undo=pass");
    }

    private static void Refusals(StudioProject project, PackageCatalog catalog, ProjectConfigBinding[] bindings,
        ConfigBindingCompilation compiled, ModMetadata mod)
    {
        byte[] saved = File.ReadAllBytes(project.ManifestPath);
        Reject(() => project.SetConfigBindings([.. bindings, bindings[0]], catalog));
        Reject(() => project.SetConfigBindings([bindings[0] with { Key = "missing" }], catalog));
        Reject(() => project.SetConfigBindings([bindings[0] with { Type = ModConfigType.Float }], catalog));
        Reject(() => project.SetConfigBindings(Enumerable.Range(0, 257).Select(i => bindings[0] with
            { Site = bindings[0].Site with { RowOffset = i } }).ToArray(), catalog));
        var site = compiled.Sites.First(value => value.Profile == ConfigBindingLimits.PropertyProfile);
        Reject(() => ModManifest.Serialize(mod, [site with { Offset = site.Size }]));
        Reject(() => ModManifest.Serialize(mod, [site with { Size = ConfigBindingLimits.MaximumEntryBytes + 1 }]));
        Reject(() => ModManifest.Serialize(mod, [site, site]));
        Reject(() => ModManifest.Serialize(mod, [site with { BindingOffset = 8 }]));
        Reject(() => ModManifest.Serialize(mod, [site with { Profile = "unknown" }]));
        Reject(() => ModManifest.Serialize(mod, [site with { Tag = TagHashes.Make(0x1A00, 0) }]));
        using (var locked = new FileStream(project.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bool refused = false;
            try { project.SetConfigBindings([], catalog); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { refused = true; }
            Check(refused, "locked manifest refusal");
        }
        Check(File.ReadAllBytes(project.ManifestPath).SequenceEqual(saved) && project.ConfigBindings.SequenceEqual(bindings), "failed save leaves metadata unchanged");
        var old = JsonNode.Parse(saved)!; old["version"] = 3; File.WriteAllText(project.ManifestPath, old.ToJsonString());
        Reject(() => StudioProject.Open(project.DirectoryPath)); File.WriteAllBytes(project.ManifestPath, saved);
        var original = bindings[0];
        Reject(() => project.SetConfigBindings([original with { OriginalSha256 = new string('0', 64) }], catalog));
    }

    private static void CompileRaces(StudioProject project, PackageCatalog catalog, ProjectConfigBinding[] bindings,
        ModMetadata mod, byte[] source, string packagePath, string parent)
    {
        byte[] saved = File.ReadAllBytes(project.ManifestPath), originalPackage = File.ReadAllBytes(packagePath);
        byte[] changed = source.ToArray(); var document = CuiDocument.Read(Tag, Class, source);
        BinaryPrimitives.WriteSingleLittleEndian(changed.AsSpan(document.Objects[0].Components[0].Properties[1].ValueOffset), 21);
        string replacement = Path.Combine(parent, "w64_race_0100_0.pkg");
        PackageWriter.WriteNew(replacement, 0x100, [new(Class, 0x1000, changed, "Config widget")]);
        bool boundaryChanged = false, boundaryRefused = false;
        var boundary = new BoundaryBindings(bindings, () =>
        { File.Copy(replacement, packagePath, true); catalog.InvalidateReadCache(); boundaryChanged = true; });
        var compile = typeof(StudioProject).GetMethod("CompileConfigBindings", BindingFlags.NonPublic | BindingFlags.Instance)!;
        try { compile.Invoke(project, [boundary, catalog, new Dictionary<uint, byte[]>(), null]); }
        catch (TargetInvocationException error) when (error.InnerException is IOException or InvalidDataException) { boundaryRefused = true; }
        finally { File.WriteAllBytes(packagePath, originalPackage); catalog.InvalidateReadCache(); }
        Check(boundaryChanged && boundaryRefused, "source changes at the baseline-read boundary are refused");
        bool replaced = false;
        project.ConfigBindingCompiler = new CallbackCompiler(() =>
        {
            File.Copy(replacement, packagePath, true); catalog.InvalidateReadCache(); replaced = true;
        });
        try { RejectIo(() => project.SetConfigBindings(bindings[..3], catalog)); }
        finally { File.WriteAllBytes(packagePath, originalPackage); catalog.InvalidateReadCache(); }
        Check(replaced && File.ReadAllBytes(project.ManifestPath).SequenceEqual(saved)
            && project.ConfigBindings.SequenceEqual(bindings), "mid-compile original replacement refused without publication");
        var declarations = mod.Config!.ToArray(); project.SetMod(mod with { Config = declarations });
        declarations[0] = declarations[0] with { Default = "99" };
        Check(project.Mod!.Config![0].Default == "-123456789", "stored default declarations are copied");
        saved = File.ReadAllBytes(project.ManifestPath);
        project.ConfigBindingCompiler = new CallbackCompiler(() => project.SetMod(project.Mod! with
            { Config = project.Mod!.Config!.Select(value => value with { Default = value.Type == ModConfigType.Bool ? "false" : "1" }).ToArray() }));
        Reject(() => project.SetConfigBindings(bindings[..3], catalog));
        Check(File.ReadAllBytes(project.ManifestPath).SequenceEqual(saved) && project.ConfigBindings.SequenceEqual(bindings)
            && project.Mod!.Config![0].Default == "-123456789", "compiler cannot publish changed defaults");
        project.ConfigBindingCompiler = CuiConfigBindingCompiler.Default;
        Check(project.CompileConfigBindings(catalog).Sites.Count == 4, "compiler refusal permits retry");
    }

    private sealed class CallbackCompiler(Action callback) : IConfigBindingCompiler
    {
        public IReadOnlyList<ConfigBindingCandidate> FindSites(uint tag, uint classHash, ReadOnlyMemory<byte> bytes) =>
            CuiConfigBindingCompiler.Default.FindSites(tag, classHash, bytes);
        public ConfigBindingCompilation Compile(IReadOnlyList<ProjectConfigBinding> bindings,
            IReadOnlyList<ModConfigEntry> settings, Func<uint, byte[]> readSource)
        { callback(); return CuiConfigBindingCompiler.Default.Compile(bindings, settings, readSource); }
    }

    /** Runs the boundary action after the second full enumeration, between the compile's baseline reads. */
    private sealed class BoundaryBindings(ProjectConfigBinding[] values, Action boundary) : IReadOnlyList<ProjectConfigBinding>
    {
        private int enumerations;
        public int Count => values.Length;
        public ProjectConfigBinding this[int index] => values[index];
        public IEnumerator<ProjectConfigBinding> GetEnumerator()
        {
            int pass = ++enumerations;
            foreach (var value in values) yield return value;
            if (pass == 2) boundary();
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static void LogicalAliases(string parent)
    {
        byte[] source = ConfigBindingFixture.Create(); var initial = CuiDocument.Read(Tag, Class, source);
        var x = initial.Objects[0].Components[0].Properties[0]; var y = initial.Objects[0].Components[0].Properties[1];
        BinaryPrimitives.WriteInt64LittleEndian(source.AsSpan(y.RecordOffset + 16), x.ValueOffset - y.RecordOffset - 16);
        string install = Path.Combine(parent, "config-alias-install"); Directory.CreateDirectory(Path.Combine(install, "packages"));
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_alias_0100_0.pkg"), 0x100, [new(Class, 0x1000, source)]);
        using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
        workspace.OpenInstallation(install); Wait(workspace); workspace.Select(Tag); Wait(workspace);
        workspace.CreateProject(Path.Combine(parent, "config-alias-project"), "Aliases"); Wait(workspace);
        workspace.SetModMetadata(new("test.alias", "1.0.0", "Alias config", Config: [new("x", ModConfigType.Float, "123.5")]));
        var candidate = workspace.Document!.ConfigSites.Single(site => site.Site.OwnerIndex == 0 && site.Site.RowOffset == x.RecordOffset);
        workspace.SetConfigBindings([workspace.CreateConfigBinding(candidate, "x")]); Wait(workspace);
        AssetProperty Property(int owner, int row) => Fields(workspace.Document!.Inspection.Sections).Single(property =>
            property.CuiOwner is { } identity && identity.ObjectIndex == owner && identity.PropertyOffset == row);
        var px = Property(0, x.RecordOffset); var py = Property(0, y.RecordOffset);
        Check(px.Offset == py.Offset && workspace.ConfigBindingForProperty(px) is not null
            && workspace.ConfigBindingForProperty(py) is null, "shared raw address does not bind another logical property");
        workspace.ApplyInspectorProperty(py, "77.25"); Wait(workspace);
        var raw = CuiDocument.Read(Tag, Class, workspace.Document!.Bytes);
        Check(BinaryPrimitives.ReadSingleLittleEndian(raw.Data.Span[raw.Objects[0].Components[0].Properties[0].ValueOffset..]) == 10
            && BinaryPrimitives.ReadSingleLittleEndian(raw.Data.Span[raw.Objects[0].Components[0].Properties[1].ValueOffset..]) == 77.25f,
            "unbound alias edits detach instead of changing the bound literal");
        int otherRow = raw.Objects[1].Components[0].Properties[0].RecordOffset;
        var other = Property(1, otherRow);
        Check(workspace.ConfigBindingForProperty(other) is null, "other owner remains unbound");
        workspace.ApplyInspectorProperty(other, "55"); Wait(workspace);
        Reject(() => workspace.ApplyInspectorProperty(Property(0, x.RecordOffset), "9"));
        workspace.Undo(); Wait(workspace); workspace.Redo(); Wait(workspace);
        Check(workspace.Project!.ConfigBindings.Count == 1, "unbound alias undo preserves binding");

        static IEnumerable<AssetProperty> Fields(IEnumerable<AssetNode> nodes) => nodes.SelectMany(node =>
            node.Properties.Concat(Fields(node.Children ?? [])));
    }

    private static void RejectIo(Action action)
    {
        try { action(); } catch (IOException) { return; }
        throw new InvalidDataException("Changing compilation source was accepted.");
    }

    private static void Added(string parent, PackageCatalog catalog, byte[] source, ModMetadata mod)
    {
        var project = StudioProject.Create(Path.Combine(parent, "config-added-project"), "Added config target");
        project.ConfigBindingCompiler = CuiConfigBindingCompiler.Default; project.SetMod(mod);
        var asset = project.AddDerivedAsset("private-interface", Tag, source, new(0xC00, Class, 0x1000), catalog);
        string payload = Path.Combine(project.DirectoryPath, asset.Path);
        byte[] stored = File.ReadAllBytes(payload); DateTime stamp = File.GetLastWriteTimeUtc(payload);
        var candidate = CuiConfigBindingCompiler.Default.FindSites(0x82000000, Class, source).First(site => site.Type == ModConfigType.Bool);
        project.SetConfigBindings([new("auto_width", ModConfigType.Bool, 0x82000000, asset.Id, Class, null, null, candidate.Site)], catalog);
        Reject(() => project.RemoveAddedAsset(asset.Id));
        Check(File.ReadAllBytes(payload).SequenceEqual(stored) && File.GetLastWriteTimeUtc(payload) == stamp, "added binding leaves delta unchanged");
        var build = project.Build(catalog);
        Check(build.ConfigBindings.Single().Tag == 0x82000000 && project.ReadAddedAsset(asset.Id, catalog).SequenceEqual(source), "added target builds by stable identity");
        project.SetConfigBindings([], catalog); project.RemoveAddedAsset(asset.Id);
        var legacy = project.AddAsset("legacy", source, new(0xC00, Class, 0x1000));
        var json = JsonNode.Parse(File.ReadAllText(project.ManifestPath))!;
        json["addedAssets"]![0]!["native"]!["packageId"] = 0xD00;
        File.WriteAllText(project.ManifestPath, json.ToJsonString());
        project = StudioProject.Open(project.DirectoryPath); project.ConfigBindingCompiler = CuiConfigBindingCompiler.Default;
        byte[] before = File.ReadAllBytes(project.ManifestPath);
        Reject(() => project.SetConfigBindings([new("auto_width", ModConfigType.Bool,
            TagHashes.Make(0xD00, 0), legacy.Id, Class, null, null, candidate.Site)], catalog));
        Check(File.ReadAllBytes(project.ManifestPath).SequenceEqual(before), "legacy added id binding refused without migration");
        project.RemoveAddedAsset(legacy.Id);
        var occupied = project.AddAsset("occupied", source, new(0x100, Class, 0x1000));
        before = File.ReadAllBytes(project.ManifestPath);
        Reject(() => project.SetConfigBindings([new("auto_width", ModConfigType.Bool, Tag, occupied.Id,
            Class, null, null, candidate.Site)], catalog));
        Check(File.ReadAllBytes(project.ManifestPath).SequenceEqual(before), "installed added id binding refused before save");
    }

    private static void Workflow(string parent, string install)
    {
        using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
        workspace.OpenInstallation(install); Wait(workspace); workspace.Select(Tag); Wait(workspace);
        workspace.CreateProject(Path.Combine(parent, "config-ui-project"), "Config bindings"); Wait(workspace);
        workspace.SetModMetadata(new("test.config", "1.0.0", "Config", Config: [new("x", ModConfigType.Float, "123.5")]));
        var raw = workspace.Document!.Bytes.ToArray();
        var draft = new ConfigBindingDraft(workspace);
        var x = draft.Sites.First(site => site.Type == ModConfigType.Float && site.Site.OwnerIndex == 0);
        draft.Bind(workspace, x, "x"); draft.Save(workspace); Wait(workspace);
        Check(workspace.Document!.Bytes.SequenceEqual(raw) && workspace.Project!.Changes.Count == 0, "preview leaves editable bytes raw");
        var cui = workspace.Document.Cui!; var component = cui.Document!.Objects[0].Components[0];
        Check(cui.ConfiguredRectangles[component].X == 123.5f, "default visual preview");
        workspace.Undo(); Wait(workspace); Check(workspace.Project!.ConfigBindings.Count == 0, "undo metadata");
        workspace.Redo(); Wait(workspace); Check(workspace.Project!.ConfigBindings.Count == 1, "redo metadata");
        var stale = new ConfigBindingDraft(workspace);
        workspace.SetModMetadata(workspace.Project.Mod! with { Name = "Changed" }); Wait(workspace);
        Reject(() => stale.Save(workspace));
        string manifest = workspace.Project.ManifestPath; workspace.CloseProject(); Wait(workspace);
        workspace.OpenProject(manifest); Wait(workspace);
        Check(workspace.Project!.ConfigBindings.Count == 1 && workspace.Document!.Bytes.SequenceEqual(raw), "workspace reopen");
    }

    private static void SharedOwners(byte[] source, ModMetadata mod)
    {
        var initial = CuiDocument.Read(Tag, Class, source);
        byte[] shared = source.ToArray();
        int firstField = initial.Objects[0].Offset + 40, secondField = initial.Objects[1].Offset + 40;
        long target = firstField + BinaryPrimitives.ReadInt64LittleEndian(shared.AsSpan(firstField));
        BinaryPrimitives.WriteInt64LittleEndian(shared.AsSpan(secondField), target - secondField);
        var before = CuiDocument.Read(Tag, Class, shared);
        Check(before.Objects[0].Components[0].Offset == before.Objects[1].Components[0].Offset, "fixture shares component rows");
        var site = CuiConfigBindingCompiler.Default.FindSites(Tag, Class, shared)
            .First(candidate => candidate.Type == ModConfigType.Bool && candidate.Site.OwnerIndex == 0);
        var binding = new ProjectConfigBinding("auto_width", ModConfigType.Bool, Tag, null, Class,
            BinaryDelta.Hash(shared), shared.Length, site.Site);
        var result = CuiConfigBindingCompiler.Default.Compile([binding], mod.Config!, _ => shared).Assets[Tag];
        var after = CuiDocument.Read(Tag, Class, result);
        var own = after.Objects[0].Components[0]; var sibling = after.Objects[1].Components[0];
        var selected = own.Properties.Single(property => property.Selector == 0x41A);
        var untouched = sibling.Properties.Single(property => property.Selector == 0x41A);
        Check(own.Offset != sibling.Offset && selected.RecordOffset != untouched.RecordOffset
            && selected.ValueOffset != untouched.ValueOffset && result[selected.ValueOffset] == 1 && result[untouched.ValueOffset] == 0,
            "shared component, reference row and value all isolated");
    }

    private static void Startup(string parent, byte[] source, ModMetadata mod, ProjectConfigBinding[] bindings)
    {
        string install = Path.Combine(parent, "config-startup"); Directory.CreateDirectory(Path.Combine(install, "packages"));
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_test_unp1_0100_0.pkg"), 0x100, [new(Class, 0x1000, source)]);
        using var catalog = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
        var project = StudioProject.Create(Path.Combine(parent, "config-startup-project"), "Startup refused");
        project.ConfigBindingCompiler = CuiConfigBindingCompiler.Default; project.SetMod(mod);
        byte[] before = File.ReadAllBytes(project.ManifestPath);
        Reject(() => project.SetConfigBindings(bindings, catalog));
        Check(File.ReadAllBytes(project.ManifestPath).SequenceEqual(before), "startup refusal before publication");
    }

    public static void Owned(string parent, string gameRoot, string output)
    {
        const uint tag = 0x80BC722A;
        using var catalog = PackageCatalog.Open(gameRoot, cacheOptions: new() { Enabled = false });
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != Class) throw new InvalidDataException("Owned CUI fixture missing.");
        byte[] raw = catalog.ReadOriginal(tag); string rawHash = BinaryDelta.Hash(raw);
        var candidates = CuiConfigBindingCompiler.Default.FindSites(tag, entry.Reference, raw);
        var integer = candidates.First(site => site.Type == ModConfigType.Int);
        var real = candidates.First(site => site.Type == ModConfigType.Float && site.Site.Profile == ConfigBindingLimits.PropertyProfile);
        var flag = candidates.First(site => site.Type == ModConfigType.Bool);
        var mod = new ModMetadata("test.owned_config", "1.0.0", "Owned CUI config", Config:
            [new("threshold", ModConfigType.Int, "-23"), new("position", ModConfigType.Float, "17.125"),
             new("auto_width", ModConfigType.Bool, "false")]);
        var project = StudioProject.Create(Path.Combine(parent, "config-owned-project"), "Owned bindings");
        project.ConfigBindingCompiler = CuiConfigBindingCompiler.Default; project.SetMod(mod);
        var sites = new[] { (integer, "threshold"), (real, "position"), (flag, "auto_width") };
        project.SetConfigBindings(sites.Select(pair => new ProjectConfigBinding(pair.Item2, pair.Item1.Type,
            tag, null, Class, rawHash, raw.Length, pair.Item1.Site)).ToArray(), catalog);
        var compiled = project.CompileConfigBindings(catalog);
        var build = project.Build(catalog);
        string verify = Path.Combine(parent, "config-owned-built"), packages = Path.Combine(verify, "packages");
        Directory.CreateDirectory(packages);
        File.Copy(build.PackageFiles.Single(), Path.Combine(packages, Path.GetFileName(build.PackageFiles.Single())));
        using (var reopened = PackageCatalog.Open(verify, cacheOptions: new() { Enabled = false }))
            Check(reopened.Read(tag).SequenceEqual(compiled.Assets[tag]), "owned plaintext entry reopen");
        Check(project.Changes.Count == 0 && project.AddedAssets.Count == 0 && BinaryDelta.Hash(catalog.ReadOriginal(tag)) == rawHash,
            "owned binding stores metadata and leaves original unchanged");
        var reopenedProject = StudioProject.Open(project.DirectoryPath); reopenedProject.ConfigBindingCompiler = CuiConfigBindingCompiler.Default;
        Check(reopenedProject.CompileConfigBindings(catalog).Assets[tag].SequenceEqual(compiled.Assets[tag]), "owned deterministic reopen");
        Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(output, "entry.bin"), compiled.Assets[tag]);
        File.WriteAllBytes(Path.Combine(output, "mod.json"), ModManifest.Serialize(mod, build.ConfigBindings));
        File.WriteAllText(Path.Combine(output, "identity.json"), System.Text.Json.JsonSerializer.Serialize(new
        { tag = tag.ToString("X8"), sourcePackage = entry.PackageName, originalSha256 = rawHash, originalBytes = raw.Length,
            projectBytes = new FileInfo(project.ManifestPath).Length, projectPayloadFiles = 0, sites = compiled.Sites }));
        Console.WriteLine($"config_bindings_owned tag={tag:X8} types=3 project=metadata_only original=unchanged build=plaintext reopen=exact");
    }

    private static void Wait(StudioWorkspace workspace)
    {
        var timer = Stopwatch.StartNew();
        while (workspace.IsBusy && timer.Elapsed.TotalSeconds < 30) { workspace.Poll(); Thread.Sleep(1); }
        workspace.Poll(); Check(!workspace.IsBusy && workspace.Error.Length == 0, workspace.Error);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException("Config bindings: " + message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { return; }
        throw new InvalidDataException("Invalid config binding change was accepted.");
    }
}
