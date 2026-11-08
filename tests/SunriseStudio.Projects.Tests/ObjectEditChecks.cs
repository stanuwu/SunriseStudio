using System.Buffers.Binary;
using System.Text.Json.Nodes;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

internal static class ObjectEditChecks
{
    /** Manifest storage, merge, versioning and base checks, with a stand-in for the Authoring compiler. */
    public static void Run(string parent, PackageCatalog catalog, uint tag)
    {
        var project = StudioProject.Create(Path.Combine(parent, "object-edits"), "Object edits");
        var countingCompiler = new ScaleCompiler();
        project.ObjectEditCompiler = countingCompiler;
        Check(Manifest(project)["version"]!.GetValue<int>() == 1 && Manifest(project)["objectEdits"] is null,
            "A project without object edits did not stay version 1.");
        byte[] original = catalog.ReadOriginal(tag);
        var target = new ObjectEditTarget(tag, ObjectEditLayout.StaticInstance, 4, 8, 0, 0,
            BinaryDelta.Hash(original.AsSpan(4, 8)));
        var edit = new ObjectEdit(ObjectEditKind.PlacementTransform, target, "Tree", Position: [1, 2, 3]);
        MutableVectors(parent, catalog, edit);
        Check(project.RecordObjectEdit(edit, catalog), "The first object edit was not saved.");
        var published = project.CompileObjectEdits(catalog);
        Check(countingCompiler.Calls == 1, "Publishing an object edit repeated its validated compile.");
        project.Save();
        Check(ReferenceEquals(published, project.CompileObjectEdits(catalog)) && countingCompiler.Calls == 1,
            "Saving unchanged canonical metadata discarded a valid object compile.");
        Check(!project.RecordObjectEdit(edit, catalog), "An identical object edit saved again.");
        Check(project.RecordObjectEdit(edit with { Position = null, Scale = 3 }, catalog), "The scale edit was not saved.");
        _ = project.CompileObjectEdits(catalog);
        Check(countingCompiler.Calls == 2, "A changed edit compiled more than once or reused its prior result.");
        var merged = project.ObjectEdits.Single();
        Check(merged.Position!.SequenceEqual(new float[] { 1, 2, 3 }) && merged.Scale == 3 && merged.Name == "Tree",
            "A later edit of the same object did not merge into its one entry.");
        var json = Manifest(project);
        Check(json["version"]!.GetValue<int>() == 2 && json["objectEdits"]!.AsArray().Count == 1
            && json["objectEdits"]![0]!["rotation"] is null && json["objectEdits"]![0]!["target"]!["layout"]!.GetValue<string>() == "StaticInstance",
            "The manifest did not store one version-2 object edit with only its set values.");
        var aliasAsset = project.AddAssets([new("wide-object", [1], new(0xC00, 0x80809994, 0x1000))], catalog).Single();
        project.SetWideHashAliases(aliasAsset.Id, [0xFEDCBA9876543210], catalog);
        Check(Manifest(project)["version"]!.GetValue<int>() == 3 && project.ObjectEdits.Single() == merged,
            "Aliases did not preserve object edits in version 3.");
        project.SetWideHashAliases(aliasAsset.Id, [], catalog);
        Check(Manifest(project)["version"]!.GetValue<int>() == 2 && project.ObjectEdits.Single() == merged
            && !File.ReadAllText(project.ManifestPath).Contains("wideHashes"),
            "Removing aliases did not preserve version-2 object edits.");
        project.RemoveAddedAsset(aliasAsset.Id);
        var reopened = StudioProject.Open(project.DirectoryPath);
        Check(reopened.ObjectEdits.Single().Summary() == merged.Summary() && reopened.ObjectEdits.Single().Key == merged.Key,
            "The object edit did not survive a reopen.");
        Reject<InvalidOperationException>(() => reopened.CompileObjectEdits(catalog));
        reopened.ObjectEditCompiler = new ScaleCompiler();

        var first = reopened.CompileObjectEdits(catalog);
        Check(ReferenceEquals(first, reopened.CompileObjectEdits(catalog)), "An unchanged project compiled twice.");
        byte[] compiled = reopened.ReadCompiled(tag, catalog);
        Check(BinaryPrimitives.ReadSingleLittleEndian(compiled.AsSpan(4)) == 3
            && compiled.AsSpan(8).SequenceEqual(original.AsSpan(8)) && compiled[..4].SequenceEqual(original[..4]),
            "Compiled bytes did not start from the installed original.");
        var again = new ScaleCompiler().Compile(reopened.ObjectEdits, catalog.ReadOriginal, catalog);
        Check(again.Assets[tag].SequenceEqual(first.Assets[tag]), "Compiling the same edits gave different bytes.");
        SameCatalogBaseChange(parent, original, tag, edit);
        reopened.ObjectEditCompiler = null;
        Reject<InvalidOperationException>(() => reopened.CompileObjectEdits(catalog));
        reopened.ObjectEditCompiler = new ScaleCompiler(2);
        Check(BinaryPrimitives.ReadSingleLittleEndian(reopened.ReadCompiled(tag, catalog).AsSpan(4)) == 6,
            "Changing the object edit compiler reused an old compiled result.");
        reopened.ObjectEditCompiler = new ScaleCompiler();

        // A raw delta on the same bytes loses to the object edit; other raw bytes stay.
        byte[] raw = original.ToArray(); raw[4] ^= 0xFF; raw[^1] ^= 0xFF;
        reopened.ApplyEdit(tag, raw, catalog);
        byte[] layered = reopened.ReadCompiled(tag, catalog);
        Check(BinaryPrimitives.ReadSingleLittleEndian(layered.AsSpan(4)) == 3 && layered[^1] == raw[^1],
            "Object edits did not sit on top of raw deltas.");
        var build = reopened.Build(catalog);
        string receipt = File.ReadAllText(build.ReceiptPath);
        Check(build.ObjectEdits!.Single().Tags.SequenceEqual(new[] { tag }) && receipt.Contains("objectEdits", StringComparison.Ordinal)
            && receipt.Contains("scale 3", StringComparison.Ordinal), "The build receipt does not list the object edit.");
        reopened.Revert(tag, catalog);

        string manifest = File.ReadAllText(reopened.ManifestPath);
        var newer = JsonNode.Parse(manifest)!; newer["version"] = StudioProject.ManifestVersion + 1;
        File.WriteAllText(reopened.ManifestPath, newer.ToJsonString());
        Reject<InvalidDataException>(() => StudioProject.Open(reopened.DirectoryPath));
        var older = JsonNode.Parse(manifest)!; older["version"] = 1;
        File.WriteAllText(reopened.ManifestPath, older.ToJsonString());
        Reject<InvalidDataException>(() => StudioProject.Open(reopened.DirectoryPath));
        var duplicate = JsonNode.Parse(manifest)!;
        duplicate["objectEdits"]!.AsArray().Add(duplicate["objectEdits"]![0]!.DeepClone());
        File.WriteAllText(reopened.ManifestPath, duplicate.ToJsonString());
        Reject<InvalidDataException>(() => StudioProject.Open(reopened.DirectoryPath));
        File.WriteAllText(reopened.ManifestPath, manifest);

        string changedInstall = Path.Combine(parent, "object-edit-changed-base");
        Directory.CreateDirectory(Path.Combine(changedInstall, "packages"));
        byte[] changed = original.ToArray(); changed[5] ^= 1;
        PackageWriter.WriteNew(Path.Combine(changedInstall, "packages", "w64_test_0100_0.pkg"), 0x100,
            [new AuthoredPackageEntry(0x80800001, 0x100A, changed, "test:edited")]);
        using (var changedCatalog = PackageCatalog.Open(changedInstall))
        {
            Reject<InvalidDataException>(() => reopened.CompileObjectEdits(changedCatalog));
            Reject<InvalidDataException>(() => reopened.Build(changedCatalog));
            Reject<InvalidDataException>(() => reopened.RecordObjectEdit(edit with { Scale = 5 }, changedCatalog));
        }
        Check(reopened.ObjectEdits.Single().Scale == 3, "A refused edit changed the saved object edit.");
        reopened.RevertObjectEdit(merged.Key);
        Check(reopened.ObjectEdits.Count == 0 && Manifest(reopened)["version"]!.GetValue<int>() == 1
            && Manifest(reopened)["objectEdits"] is null, "Reverting the last object edit did not return to version 1.");
        Console.WriteLine("object_edits manifest=v2 merge=by_target base=refused layering=raw_then_object build=receipt revert=v1");
    }

    private static JsonNode Manifest(StudioProject project) => JsonNode.Parse(File.ReadAllText(project.ManifestPath))!;

    private static void Check(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    /** Writes Scale as a float at the target offset; Normalize keeps every value. */
    private sealed class ScaleCompiler(float multiplier = 1) : IObjectEditCompiler
    {
        public int Calls { get; private set; }
        public ObjectEditCompilation Compile(IReadOnlyList<ObjectEdit> edits, Func<uint, byte[]> readRaw, PackageCatalog catalog)
        {
            Calls++;
            var assets = new Dictionary<uint, byte[]>();
            var compiled = new List<CompiledObjectEdit>();
            foreach (var edit in edits.OrderBy(edit => edit.Key, StringComparer.Ordinal))
            {
                uint tag = edit.Target.SourceTag;
                if (!assets.TryGetValue(tag, out var bytes)) assets[tag] = bytes = readRaw(tag).ToArray();
                if (edit.Scale is float scale) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(edit.Target.Offset), scale * multiplier);
                compiled.Add(new(edit, [tag]));
            }
            return new(assets, compiled);
        }

        public ObjectEdit? Normalize(ObjectEdit edit, Func<uint, byte[]> readRaw, PackageCatalog catalog) =>
            edit.HasValues ? edit : null;
    }

    /** A build must recheck installed row bytes even when the project and catalog instances stay open. */
    private static void SameCatalogBaseChange(string parent, byte[] original, uint tag, ObjectEdit edit)
    {
        string installation = Path.Combine(parent, "object-edit-replaced-base");
        Directory.CreateDirectory(Path.Combine(installation, "packages"));
        string package = Path.Combine(installation, "packages", "w64_test_0100_0.pkg");
        void Write(byte[] data)
        {
            string staging = Path.Combine(installation, "stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            string next = Path.Combine(staging, Path.GetFileName(package));
            PackageWriter.WriteNew(next, 0x100,
                [new AuthoredPackageEntry(0x80800001, 0x100A, data, "test:edited")]);
            File.WriteAllBytes(package, File.ReadAllBytes(next));
            File.Delete(next);
            Directory.Delete(staging);
        }
        Write(original);
        using var catalog = PackageCatalog.Open(installation);
        var project = StudioProject.Create(Path.Combine(parent, "object-edit-replaced-project"), "Base replacement");
        project.ObjectEditCompiler = new ScaleCompiler();
        project.RecordObjectEdit(edit with { Scale = 3 }, catalog);
        var first = project.CompileObjectEdits(catalog);
        byte[] manifest = File.ReadAllBytes(project.ManifestPath);
        byte[] changed = original.ToArray(); changed[5] ^= 1;
        Write(changed);
        string output = Path.Combine(parent, "object-edit-refused-build");
        Reject<InvalidDataException>(() => project.Build(catalog, output));
        Check(!Directory.Exists(output) && File.ReadAllBytes(project.ManifestPath).SequenceEqual(manifest),
            "A changed installed row published build output or changed the project.");
        Reject<InvalidDataException>(() => project.CompileObjectEdits(catalog));
        Write(original);
        catalog.InvalidateReadCache();
        var restored = project.CompileObjectEdits(catalog);
        Check(!ReferenceEquals(first, restored) && first.Assets[tag].SequenceEqual(restored.Assets[tag]),
            "Restoring the installed source did not rebuild the original compiled bytes.");
        Check(ReferenceEquals(restored, project.CompileObjectEdits(catalog)), "An unchanged read generation did not reuse the compile.");
        project.ObjectEditCompiler = new CallbackCompiler(() => catalog.InvalidateReadCache());
        Reject<IOException>(() => project.CompileObjectEdits(catalog));
        byte[] raw = original.ToArray(); raw[^1] ^= 1;
        Reject<IOException>(() => project.ApplyEdit(tag, raw, catalog));
        Check(File.ReadAllBytes(project.ManifestPath).SequenceEqual(manifest) && project.Changes.Count == 0,
            "Mid-compile invalidation was treated as a preexisting broken edit and published a raw delta.");
        project.ObjectEditCompiler = new CallbackCompiler(() => { Write(changed); catalog.InvalidateReadCache(); });
        string changedDuringBuild = Path.Combine(parent, "object-edit-inflight-refused-build");
        Reject<IOException>(() => project.Build(catalog, changedDuringBuild));
        Check(!Directory.Exists(changedDuringBuild) && File.ReadAllBytes(project.ManifestPath).SequenceEqual(manifest),
            "A source changed after baseline validation but still published a build.");
        Write(original); catalog.InvalidateReadCache();
        var replacementCompiler = new ScaleCompiler(2);
        project.ObjectEditCompiler = new CallbackCompiler(() => project.ObjectEditCompiler = replacementCompiler);
        Reject<IOException>(() => project.CompileObjectEdits(catalog));
        Check(BinaryPrimitives.ReadSingleLittleEndian(project.ReadCompiled(tag, catalog).AsSpan(4)) == 6,
            "A compiler changed during compilation and left a result under the wrong cache identity.");
        using (var disposedDuringCompile = PackageCatalog.Open(installation))
        {
            project.ObjectEditCompiler = new CallbackCompiler(after: disposedDuringCompile.Dispose);
            Reject<ObjectDisposedException>(() => project.ApplyEdit(tag, raw, disposedDuringCompile));
            Check(File.ReadAllBytes(project.ManifestPath).SequenceEqual(manifest) && project.Changes.Count == 0,
                "Disposed source admission published a raw transaction.");
        }
        project.ObjectEditCompiler = new ScaleCompiler();
        project.CompileObjectEdits(catalog);
        catalog.Dispose();
        Reject<ObjectDisposedException>(() => project.CompileObjectEdits(catalog));
        _ = catalog.ReadStatistics;
        Console.WriteLine("object_edit_cache compiler=checked changed_source=refused restored_source=rebuilt");
    }

    private sealed class CallbackCompiler(Action? before = null, Action? after = null) : IObjectEditCompiler
    {
        private readonly ScaleCompiler inner = new();
        public ObjectEditCompilation Compile(IReadOnlyList<ObjectEdit> edits, Func<uint, byte[]> readRaw, PackageCatalog catalog)
        {
            before?.Invoke();
            var result = inner.Compile(edits, readRaw, catalog);
            after?.Invoke();
            return result;
        }
        public ObjectEdit? Normalize(ObjectEdit edit, Func<uint, byte[]> readRaw, PackageCatalog catalog) =>
            inner.Normalize(edit, readRaw, catalog);
    }

    private static void MutableVectors(string parent, PackageCatalog catalog, ObjectEdit edit)
    {
        var project = StudioProject.Create(Path.Combine(parent, "object-vector-cache"), "Mutable vectors");
        var compiler = new VectorCompiler();
        project.ObjectEditCompiler = compiler;
        float[] position = [1, 2, 3];
        project.RecordObjectEdit(edit with { Position = position }, catalog);
        position[0] = 5;
        project.Save();
        Check(BinaryPrimitives.ReadSingleLittleEndian(project.ReadCompiled(edit.Target.SourceTag, catalog)
            .AsSpan(edit.Target.Offset)) == 5 && compiler.Calls == 2,
            "Saved mutable vectors retained stale compiled bytes.");
        project.ObjectEdits.Single().Position![0] = 6;
        Check(BinaryPrimitives.ReadSingleLittleEndian(project.ReadCompiled(edit.Target.SourceTag, catalog)
            .AsSpan(edit.Target.Offset)) == 6 && compiler.Calls == 3,
            "A plain compile reused stale vector values without a save.");
        byte[] manifest = File.ReadAllBytes(project.ManifestPath);
        project.ObjectEditCompiler = new VectorCompiler(() => position[0]++);
        Reject<IOException>(() => project.RecordObjectEdit(edit with { Position = position, Scale = 4 }, catalog));
        Check(File.ReadAllBytes(project.ManifestPath).SequenceEqual(manifest),
            "An inflight recipe mutation published a new manifest.");
        byte[] raw = catalog.ReadOriginal(edit.Target.SourceTag); raw[^1] ^= 1;
        Reject<IOException>(() => project.RestoreTransaction(new(new Dictionary<uint, byte[]>
            { [edit.Target.SourceTag] = raw }), new Dictionary<string, AddedAssetSnapshot?>(), catalog,
            new Dictionary<string, ObjectEdit?> { [edit.Key] = project.ObjectEdits.Single() }));
        Check(File.ReadAllBytes(project.ManifestPath).SequenceEqual(manifest) && project.Changes.Count == 0,
            "A staged recipe mutation published a raw transaction.");
        project.ObjectEditCompiler = new VectorCompiler(() =>
        {
            position[0]++;
            throw new InvalidDataException("Fixture compiler failure after mutation.");
        });
        Reject<IOException>(() => project.ApplyEdit(edit.Target.SourceTag, raw, catalog));
        Check(File.ReadAllBytes(project.ManifestPath).SequenceEqual(manifest) && project.Changes.Count == 0,
            "A failed compiler hid a recipe mutation and published a raw transaction.");
        project.ObjectEditCompiler = new VectorCompiler(() =>
        {
            catalog.InvalidateReadCache();
            throw new InvalidDataException("Fixture compiler failure after source change.");
        });
        Reject<IOException>(() => project.ApplyEdit(edit.Target.SourceTag, raw, catalog));
        Check(File.ReadAllBytes(project.ManifestPath).SequenceEqual(manifest) && project.Changes.Count == 0,
            "A failed compiler hid a source change and published a raw transaction.");
        float[] rotation = [0, 0, 0, 1];
        project.ObjectEditCompiler = new VectorCompiler();
        project.RecordObjectEdit(edit with { Position = [1, 2, 3], Rotation = rotation }, catalog);
        rotation[3] = 2;
        Reject<InvalidDataException>(() => project.CompileObjectEdits(catalog));
        rotation[3] = 1;
        project.ObjectEditCompiler = new VectorCompiler(() => project.ObjectEdits.Single().Position![0] = float.NaN);
        Reject<IOException>(() => project.CompileObjectEdits(catalog));
    }

    /** Additions and removals store their kind, identity and model, and keep one entry each. */
    public static void Structural(string parent, PackageCatalog catalog, uint tag)
    {
        var project = StudioProject.Create(Path.Combine(parent, "object-structural"), "Structural edits");
        project.ObjectEditCompiler = new VectorCompiler();
        byte[] original = catalog.ReadOriginal(tag);
        var row = new ObjectEditTarget(tag, ObjectEditLayout.StaticInstance, 4, 8, 0, 0, BinaryDelta.Hash(original.AsSpan(4, 8)));
        var container = new ObjectEditTarget(tag, ObjectEditLayout.Container, 0, original.Length, -1, 2, BinaryDelta.Hash(original));
        var edits = new[]
        {
            new ObjectEdit(ObjectEditKind.RemoveInstance, row),
            new ObjectEdit(ObjectEditKind.AddInstance, row with { Identity = 1 }, Position: [1, 2, 3]),
            new ObjectEdit(ObjectEditKind.AddPlacement, container, Position: [4, 5, 6], Model: 0x80A03000),
        };
        foreach (var edit in edits) Check(project.RecordObjectEdit(edit, catalog), $"The {edit.Kind} edit was not saved.");
        var stored = Manifest(project)["objectEdits"]!.AsArray();
        Check(stored.Count == 3 && stored.Any(x => x!["kind"]!.GetValue<string>() == "AddPlacement"
            && x["model"]!.GetValue<uint>() == 0x80A03000 && x["target"]!["layout"]!.GetValue<string>() == "Container"),
            "The manifest did not store the added placement with its model.");
        var reopened = StudioProject.Open(project.DirectoryPath);
        Check(reopened.ObjectEdits.Select(x => x.Key).Order().SequenceEqual(edits.Select(x => x.Key).Order())
            && reopened.ObjectEdits.Single(x => x.Kind == ObjectEditKind.AddPlacement).Model == 0x80A03000,
            "Structural edits did not survive a reopen.");
        reopened.ObjectEditCompiler = new VectorCompiler();
        reopened.RevertObjectEdit(edits[1].Key);
        Check(reopened.ObjectEdits.Count == 2 && reopened.FindObjectEdit(edits[0].Key) is not null,
            "Reverting a copy removed another edit of its row.");
        Console.WriteLine("object_edits structural=stored reopen=same revert=by_key");
    }

    private sealed class VectorCompiler(Action? after = null) : IObjectEditCompiler
    {
        public int Calls { get; private set; }
        public ObjectEditCompilation Compile(IReadOnlyList<ObjectEdit> edits, Func<uint, byte[]> readRaw, PackageCatalog catalog)
        {
            Calls++;
            // Structural edits generate data in the real compiler; this stand-in only lists them.
            if (edits.Any(x => x.Structural)) return new(new Dictionary<uint, byte[]>(), edits.Select(x => new CompiledObjectEdit(x, [])).ToArray());
            var edit = edits.Single();
            byte[] data = readRaw(edit.Target.SourceTag).ToArray();
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(edit.Target.Offset), edit.Position![0]);
            after?.Invoke();
            return new(new Dictionary<uint, byte[]> { [edit.Target.SourceTag] = data }, [new(edit, [edit.Target.SourceTag])]);
        }
        public ObjectEdit? Normalize(ObjectEdit edit, Func<uint, byte[]> readRaw, PackageCatalog catalog) => edit;
    }
}
