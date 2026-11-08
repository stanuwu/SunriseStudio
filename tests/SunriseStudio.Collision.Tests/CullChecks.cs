using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using static Checks;
using static StaticIsolationFixture;

/** Isolation keeps untouched baked masks and exposes only the original editable identities. */
internal static class CullChecks
{
    private static readonly ObjectEditCompiler Compiler = new([new InstanceBoundsRule(), new InstanceCullRule()]);

    internal static void Synthetic()
    {
        string folder = Path.Combine(Path.GetTempPath(), "sunrise-cull-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            ulong candidate = InstanceCullRule.FreshId(Table, 1, []);
            Write(folder, candidate);
            using var catalog = PackageCatalog.Open(folder, cacheOptions: new() { Enabled = false });
            var edit = Edit(catalog, Table, 1, 3);
            var compiled = Compiler.Compile([edit], catalog.ReadOriginal, catalog);
            Verify(catalog, compiled, Table, 1);
            BuildOnly(catalog, edit);
            Throws<InvalidDataException>(() => Compiler.Compile([edit], catalog.ReadOriginal, catalog,
                new("added-graph", new HashSet<ushort>()) { AddedNativeReferences = new HashSet<uint> { 0x808099D6 } }));
            Check(Compiler.Compile([edit], catalog.ReadOriginal, catalog,
                new("added-texture", new HashSet<ushort>()) { AddedNativeReferences = new HashSet<uint> { uint.MaxValue } })
                .GeneratedAssets.Count == compiled.GeneratedAssets.Count, "An unrelated added native resource prevented isolation.");
            catalog.SetOverlayEntries([new(new(0x80A02000, 0x808099D6, 8, 0, 24, "new-graph", 0x101, 0) { TypeInfo = 0x1000 })]);
            Throws<InvalidDataException>(() => Compiler.Compile([edit], catalog.ReadOriginal, catalog));
            Check(Compiler.Compile([edit], catalog.ReadOriginal, catalog,
                new("staged-removal", new HashSet<ushort>())).GeneratedAssets.Count == 4,
                "Explicit staged options inherited a removed graph overlay.");
            catalog.SetOverlayEntries([]);
            AddedGraph(catalog, edit, compiled);
            var again = Compiler.Compile([edit], catalog.ReadOriginal, catalog);
            Check(compiled.Assets.All(x => x.Value.SequenceEqual(again.Assets[x.Key]))
                && compiled.GeneratedAssets.Zip(again.GeneratedAssets).All(x => x.First.TagHash == x.Second.TagHash
                    && x.First.Data.SequenceEqual(x.Second.Data)), "Repeated compilation changed generated identities or bytes.");
            byte[] changedBounds = compiled.Assets[Bounds];
            ulong fresh = TagRows.VisibilityId(changedBounds, TagRows.Boxes(changedBounds)[1]);
            Check(fresh != candidate && !RowIds.Contains(fresh), "Fresh id collides with an occupied map key or bounds id.");
            var hidden = Compiler.Compile([Edit(catalog, Table, 2, 3)], catalog.ReadOriginal, catalog);
            Check(hidden.GeneratedAssets.Count == 0 && !hidden.Assets.ContainsKey(Owner), "An authored hidden row gained geometry.");
            byte[] dynamicOwner = catalog.ReadOriginal(Owner); dynamicOwner[0x20] = 0;
            var dynamic = Compiler.Compile([edit], tag => tag == Owner ? dynamicOwner : catalog.ReadOriginal(tag), catalog);
            Check(dynamic.GeneratedAssets.Count == 0 && !dynamic.Assets.ContainsKey(Owner)
                && dynamic.Assets.ContainsKey(Bounds), "An existing per-row owner did not repair its edited id.");
            Throws<InvalidDataException>(() => Compiler.Compile([edit],
                tag => tag == Owner ? dynamicOwner[..0x20] : catalog.ReadOriginal(tag), catalog));
            byte[] attached = catalog.ReadOriginal(List);
            BinaryPrimitives.WriteInt64LittleEndian(attached.AsSpan(48 + 0x80), 8);
            Throws<InvalidDataException>(() => Compiler.Compile([edit], tag => tag == List ? attached : catalog.ReadOriginal(tag), catalog));
            byte[] unknown = catalog.ReadOriginal(Table);
            BinaryPrimitives.WriteInt64LittleEndian(unknown.AsSpan(8), 1);
            Throws<InvalidDataException>(() => Compiler.Compile([edit], tag => tag == Table ? unknown : catalog.ReadOriginal(tag), catalog));
            byte[] configPlacement = ConfigPlacement();
            Throws<InvalidDataException>(() => Compiler.Compile([edit],
                tag => tag == SlotConfig ? configPlacement : catalog.ReadOriginal(tag), catalog));
            Check(Compiler.Compile([edit], catalog.ReadOriginal, catalog).GeneratedAssets.Count == compiled.GeneratedAssets.Count,
                "A staged config refusal contaminated later compilation in the same catalog generation.");
            Throws<InvalidDataException>(() => Compiler.Compile([Edit(catalog, LoneTable, 0, 3)], catalog.ReadOriginal, catalog));
            MapAdmission(catalog);
            Preview(catalog, compiled);
            string sharedFolder = Path.Combine(folder, "shared");
            Write(sharedFolder, sharedBounds: true);
            using (var sharedCatalog = PackageCatalog.Open(sharedFolder, cacheOptions: new() { Enabled = false }))
            {
                byte[] retained = sharedCatalog.ReadOriginal(Bounds);
                var refusal = Throws<InvalidDataException>(() => Compiler.Compile(
                    [Edit(sharedCatalog, Table, 1, 3)], sharedCatalog.ReadOriginal, sharedCatalog));
                Check(refusal.Message.Contains("shared", StringComparison.OrdinalIgnoreCase)
                    && retained.AsSpan().SequenceEqual(sharedCatalog.ReadOriginal(Bounds)),
                    "A second rendered table's shared bounds were not refused before publication.");
            }
            Console.WriteLine("PASS cull-isolation original-owner=unchanged hidden=retained compact=1 bounds=empty mode=1/1 aliases=original duplicate=absent dynamic-id=repaired refusals=checked");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    /** Added graph entries join the scopes: they get replacements where Studio can write, and refusals where it cannot. */
    private static void AddedGraph(PackageCatalog catalog, ObjectEdit edit, ObjectEditCompilation baseline)
    {
        const uint AddedList = 0x80A02000, AddedContainer = 0x80A02001, AddedTable = 0x80A02002;
        ObjectEditCompileOptions Options(params AddedObjectEntry[] entries) =>
            new("added-graph", new HashSet<ushort>())
            {
                AddedNativeReferences = entries.Select(x => x.Class).ToHashSet(),
                AddedEntries = entries
            };
        AddedObjectEntry Added(uint tag, uint type, byte[] bytes) => new(tag, type, 0x1000, WideHashAliases.Empty, () => bytes);
        var unrelated = Compiler.Compile([edit], catalog.ReadOriginal, catalog,
            Options(Added(AddedList, 0x808099D6, StaticPlacementList(Base + 30))));
        Check(unrelated.GeneratedAssets.Count == baseline.GeneratedAssets.Count,
            "An added list that places another owner changed the isolation.");
        // The installed container carries the added list through a raw change, which isolation can extend.
        byte[] container = ContainerMembers(List, MapList, AddedList);
        var joined = Compiler.Compile([edit], tag => tag == Container ? container : catalog.ReadOriginal(tag), catalog,
            Options(Added(AddedList, 0x808099D6, StaticPlacementList(Owner))));
        Check(joined.GeneratedAssets.Count == baseline.GeneratedAssets.Count + 1
            && joined.GeneratedAssets.Any(x => x.SourceTag == AddedList),
            "An added list that places the edited owner got no replacement list.");
        byte[] bubbleList = BubbleContainers(Container, AddedContainer);
        var refusal = Throws<InvalidDataException>(() => Compiler.Compile([edit],
            tag => tag == ContainerList ? bubbleList : catalog.ReadOriginal(tag), catalog,
            Options(Added(AddedList, 0x808099D6, StaticPlacementList(Owner)),
                Added(AddedContainer, 0x80808A54, ContainerMembers(AddedList)))));
        Check(refusal.Message.Contains("this project's container", StringComparison.Ordinal),
            "An added container that places the edited owner was not refused by name.");
        byte[] sharing = catalog.ReadOriginal(LoneTable);
        BinaryPrimitives.WriteUInt32LittleEndian(sharing.AsSpan(0x18), Bounds);
        var shared = Throws<InvalidDataException>(() => Compiler.Compile([edit], catalog.ReadOriginal, catalog,
            Options(Added(AddedTable, 0x8080966D, sharing))));
        Check(shared.Message.Contains("shared", StringComparison.OrdinalIgnoreCase),
            "An added table that shares the edited bounds was not refused.");
        Console.WriteLine("PASS cull-added-graph unrelated=unchanged placed=replaced added-container=refused shared-bounds=refused");
    }

    /** Owned source rows and masks remain byte exact outside the single edited row. */
    internal static void Owned(PackageCatalog catalog, uint table, int index)
    {
        var compiled = ObjectEditCompiler.Default.Compile([Edit(catalog, table, index, 3)], catalog.ReadOriginal, catalog);
        Verify(catalog, compiled, table, index);
        Console.WriteLine($"PASS cull-owned table={table:X8} row={index} original-owner=unchanged generated={compiled.GeneratedAssets.Count} mode=1/1 coarse-bounds=0");
    }

    /** A private copy of the captured recipe proves package readback, namespace conflicts and undo. */
    internal static void ExactProject(PackageCatalog catalog, string capture)
    {
        string folder = Path.Combine(Path.GetTempPath(), "sunrise-exact-isolation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        byte[] manifest = File.ReadAllBytes(Path.Combine(capture, StudioProject.ProjectFileName));
        try
        {
            File.WriteAllBytes(Path.Combine(folder, StudioProject.ProjectFileName), manifest);
            var project = StudioProject.Open(folder); project.ObjectEditCompiler = ObjectEditCompiler.Default;
            var edit = project.ObjectEdits.Single();
            var preparation = System.Diagnostics.Stopwatch.StartNew();
            ObjectEditCompiler.PrepareInstallation(catalog);
            preparation.Stop();
            Console.WriteLine($"isolation_prepare ms={preparation.Elapsed.TotalMilliseconds:F1}");
            var readsBefore = catalog.ReadStatistics;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var compiled = project.CompileObjectEdits(catalog);
            timer.Stop();
            var readsAfter = catalog.ReadStatistics;
            Console.WriteLine($"isolation_compile ms={timer.Elapsed.TotalMilliseconds:F1} entries={readsAfter.EntryReads - readsBefore.EntryReads} "
                + $"stored_bytes={readsAfter.StoredBytes - readsBefore.StoredBytes} decoded_bytes={readsAfter.DecodedBytes - readsBefore.DecodedBytes}");
            readsBefore = catalog.ReadStatistics; timer.Restart();
            _ = ObjectEditCompiler.Default.Compile(project.ObjectEdits, catalog.ReadOriginal, catalog,
                new(project.Mod?.Id ?? project.Name, new HashSet<ushort>()) { RawChangedTags = new HashSet<uint>() });
            timer.Stop(); readsAfter = catalog.ReadStatistics;
            Console.WriteLine($"isolation_compile_warm ms={timer.Elapsed.TotalMilliseconds:F1} entries={readsAfter.EntryReads - readsBefore.EntryReads} "
                + $"stored_bytes={readsAfter.StoredBytes - readsBefore.StoredBytes} decoded_bytes={readsAfter.DecodedBytes - readsBefore.DecodedBytes}");
            Check(readsAfter.EntryReads - readsBefore.EntryReads < 50_000,
                "Warm isolation rescanned the installed config payloads.");
            Verify(catalog, compiled, edit.Target.SourceTag, edit.Target.Index);
            var nativeMap = new InstanceVisibilityMap(catalog.ReadOriginal(0x80FAA377));
            byte[] before = catalog.ReadOriginal(0x80FAA005), after = compiled.Assets[0x80FAA005];
            int[] boxes = TagRows.Boxes(before);
            Check(boxes.Length == 6869 && boxes.Count(row => !nativeMap.Contains(TagRows.VisibilityId(before, row))) == 159,
                "The owned visibility baseline changed.");
            Check(boxes.Count(row => !nativeMap.Contains(TagRows.VisibilityId(after, row))) == 160,
                "The replacement did not hide exactly the edited original row.");
            Check(!compiled.Assets.ContainsKey(0x80FAA28F), "The owned original owner changed.");
            var generatedList = compiled.GeneratedAssets.Single(x => x.Native.Reference == 0x808099D6);
            byte[] list = generatedList.Data;
            int row = TagRows.Offsets<SMapPlacements, AuthoredPlacementEntry>(list, nameof(SMapPlacements.Placements),
                AuthoredPlacementEntry.Class).Single();
            int payload = TagView.FieldOffset<AuthoredPlacementEntry>(nameof(AuthoredPlacementEntry.Payload));
            int attachment = TagView.FieldOffset<AuthoredPlacementEntry>(nameof(AuthoredPlacementEntry.AttachmentPtrMustBe0));
            byte[] sourceList = catalog.ReadOriginal(0x80FAA290);
            Check(TagRows.Span(list, row, payload).SequenceEqual(sourceList.AsSpan(48, payload))
                && TagRows.Span(list, row + attachment, 16).SequenceEqual(sourceList.AsSpan(48 + attachment, 16)),
                "The isolated placement changed its class, QTS, scope or attachment lanes.");
            int auxiliary = checked(row + payload
                + (int)ObjectRows.Field<AuthoredPlacementEntry, RelativePointer<Unknown>>(list, row, nameof(AuthoredPlacementEntry.Payload)).Relative);
            Check(TagRows.Span(list, auxiliary - 8, 24).SequenceEqual(sourceList.AsSpan(192, 24))
                && TagRows.Span(list, auxiliary + 20, 4).SequenceEqual(sourceList.AsSpan(220, 4)),
                "The isolated constructor prefix, ordinal or opaque tail changed.");
            byte[] oldContainer = catalog.ReadOriginal(0x80FAA350), newContainer = compiled.Assets[0x80FAA350];
            var oldMembers = Members(oldContainer);
            var newMembers = Members(newContainer);
            int mask = TagView.FieldOffset<SMapContainer>(nameof(SMapContainer.BubbleMask));
            Check(TagRows.Span(newContainer, mask, 32).SequenceEqual(TagRows.Span(oldContainer, mask, 32))
                && newMembers.Take(oldMembers.Length).SequenceEqual(oldMembers)
                && newMembers.Length == oldMembers.Length + 1 && newMembers[^1] == generatedList.TagHash,
                "The derived list changed the original container mask or membership order.");
            var build = project.Build(catalog, Path.Combine(folder, "output"));
            using (var built = PackageCatalog.Open(build.OutputDirectory, cacheOptions: new() { Enabled = false }))
                foreach (var asset in compiled.GeneratedAssets)
                    Check(built.ReadOriginal(asset.TagHash).AsSpan().SequenceEqual(asset.Data), "Generated package readback differs from compilation.");
            var reopened = StudioProject.Open(folder); reopened.ObjectEditCompiler = ObjectEditCompiler.Default;
            var repeated = reopened.CompileObjectEdits(catalog);
            Check(compiled.GeneratedAssets.Zip(repeated.GeneratedAssets).All(x => x.First.TagHash == x.Second.TagHash
                && x.First.Data.SequenceEqual(x.Second.Data)), "Reopening changed the generated namespace or bytes.");
            ushort reserved = compiled.GeneratedAssets[0].Native.PackageId;
            var conflict = ObjectEditCompiler.Default.Compile(reopened.ObjectEdits, catalog.ReadOriginal, catalog,
                new(project.Mod?.Id ?? project.Name, new HashSet<ushort> { reserved }) { RawChangedTags = new HashSet<uint>() });
            Check(conflict.GeneratedAssets.Count == compiled.GeneratedAssets.Count
                && conflict.GeneratedAssets.All(x => x.Native.PackageId != reserved), "A reserved package namespace was reused.");
            reopened.RevertObjectEdit(edit.Key);
            Check(reopened.CompileObjectEdits(catalog).GeneratedAssets.Count == 0 && reopened.AddedAssets.Count == 0,
                "Undo retained generated assets in the source project.");
            Check(reopened.RecordObjectEdit(edit, catalog), "The original edit identity could not be restored.");
            Check(reopened.ObjectEdits.Single().Target == edit.Target
                && reopened.CompileObjectEdits(catalog).GeneratedAssets.Select(x => x.TagHash)
                    .SequenceEqual(compiled.GeneratedAssets.Select(x => x.TagHash)), "Redo lost the original target or deterministic namespace.");
            Check(File.ReadAllBytes(Path.Combine(capture, StudioProject.ProjectFileName)).AsSpan().SequenceEqual(manifest),
                "The captured project was changed by validation.");
            Console.WriteLine("PASS cull-exact rows=6869 authored-hidden=159 hidden-after=160 derived-rows=1 original-owner=1/0 derived-owner=1/1 bounds=empty container=preserved readback=exact namespace=stable reserved=avoided reopen=pass undo=pass redo=pass source=unchanged");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void Verify(PackageCatalog catalog, ObjectEditCompilation compiled, uint table, int index)
    {
        byte[] original = catalog.ReadOriginal(table);
        uint boundsTag = ObjectRows.Field<SStaticMeshInstances, uint>(original, 0, nameof(SStaticMeshInstances.BoundsTable));
        byte[] bounds = catalog.ReadOriginal(boundsTag), after = compiled.Assets[boundsTag];
        int[] rows = TagRows.Boxes(bounds);
        for (int i = 0; i < rows.Length; i++)
            if (i != index) Check(bounds.AsSpan(rows[i], 48).SequenceEqual(after.AsSpan(rows[i], 48)), $"Untouched bounds row {i} changed.");
        foreach (var owner in catalog.Entries.Where(x => x.Reference == 0x80806EF4))
            if (BitConverter.ToUInt32(catalog.ReadOriginal(owner.TagHash), 8) == table)
                Check(!compiled.Assets.ContainsKey(owner.TagHash), "Original table owner changed visibility mode.");
        var generated = compiled.GeneratedAssets;
        var compact = generated.Single(x => x.Native.Reference == 0x8080966D);
        byte[] data = compact.Data;
        int[] compactRows = TagRows.StaticInstances(data);
        Check(compactRows.Length == 1 && ObjectRows.Field<SStaticMeshInstances, long>(data, 0, "Instances.InstanceCount") == 1
            && ObjectRows.Field<SStaticMeshInstances, long>(data, 0, nameof(SStaticMeshInstances.TagByteLength)) == compact.Data.Length,
            "Compact native count/length differs from its typed rows.");
        int originalRow = TagRows.StaticInstances(original)[index];
        // The retained tag is compared with the four padding bytes after it.
        int retained = TagView.FieldOffset<SStaticMeshInstances>("Instances.Unk20");
        Check(compact.Data.AsSpan(compactRows[0], 48).SequenceEqual(compiled.Assets[table].AsSpan(originalRow, 48))
            && TagRows.Span(data, retained, 8).SequenceEqual(TagRows.Span(original, retained, 8))
            && ObjectRows.Field<SStaticMeshInstances, ulong>(data, 0, "Instances.AmbientOcclusionKey")
                == ObjectRows.Field<SStaticMeshInstances, ulong>(original, 0, "Instances.AmbientOcclusionKey"),
            "Compaction changed native row fields, the retained tag or AO key.");
        Check(compact.InstanceOrigins[0] == new PackageInstanceOrigin(table, index), "Compact row lost its source identity.");
        var empty = generated.Single(x => x.Native.Reference == 0x80809671);
        Check(empty.Data.Length == 24 && TagRows.Boxes(empty.Data).Length == 0,
            "The replacement allocates native per-row visibility entries.");
        Check(generated.Where(x => x.Native.Reference == 0x80806EF4)
            .All(x => x.Data[0x20] == 1 && x.Data[0x21] == 1 && BitConverter.ToUInt64(x.Data, 0x18) == ulong.MaxValue),
            "The replacement owner does not use the native aggregate-only profile.");
        int aggregate = TagView.FieldOffset<SStaticMeshInstances>("Instances.AggregateMinimumW1");
        Check(compiled.Assets[table].AsSpan(aggregate, 32).SequenceEqual(original.AsSpan(aggregate, 32)),
            "The retained original aggregate was widened by the isolated edit.");
    }

    private static uint[] Members(byte[] container) =>
        TagRows.Offsets<SMapContainer, ContainerMember>(container, nameof(SMapContainer.Contents), ContainerMember.Class)
            .Select(row => ObjectRows.Field<ContainerMember, uint>(container, row, nameof(ContainerMember.MemberTag))).ToArray();

    /** A build-only rule runs in a build compile and never in an editor compile. */
    private static void BuildOnly(PackageCatalog catalog, ObjectEdit edit)
    {
        var probe = new BuildProbe();
        var compiler = new ObjectEditCompiler([probe]);
        compiler.Compile([edit], catalog.ReadOriginal, catalog, new("editor", new HashSet<ushort>()));
        Check(probe.Runs == 0, "A build-only rule ran in an editor compile.");
        var built = compiler.Compile([edit], catalog.ReadOriginal, catalog, new("build", new HashSet<ushort>()) { Build = true });
        Check(probe.Runs == 1 && built.Notes.SequenceEqual(["probe"]), "A build-only rule did not run, or lost its note, in a build compile.");
        var editor = ObjectEditCompiler.Default.Compile([edit], catalog.ReadOriginal, catalog, new("editor", new HashSet<ushort>()));
        var build = ObjectEditCompiler.Default.Compile([edit], catalog.ReadOriginal, catalog, new("editor", new HashSet<ushort>()) { Build = true });
        Check(editor.Assets.Keys.SequenceEqual(build.Assets.Keys) && editor.Assets.All(pair => pair.Value.SequenceEqual(build.Assets[pair.Key])),
            "The occlusion rule changed a map that has no tome.");
    }

    private sealed class BuildProbe : IObjectEditRule
    {
        internal int Runs;
        public ObjectEditLayout Layout => ObjectEditLayout.StaticInstance;
        public bool BuildOnly => true;
        public void Apply(ObjectEditContext context, IReadOnlyList<ObjectEdit> edits) { Runs++; context.Note("probe"); }
    }

    /** Native probe semantics reject stale keys, negative values, bad indices and endless probes. */
    private static void MapAdmission(PackageCatalog catalog)
    {
        byte[] original = catalog.ReadOriginal(Map);
        int[] buckets = TagRows.Offsets<SVisibilityTomeMap, SVisibilityBucket>(original, "HAda0152e.Buckets", SVisibilityBucket.Class);
        int bucket = buckets.Single(row => ObjectRows.Field<SVisibilityBucket, ulong>(original, row, nameof(SVisibilityBucket.InstanceId)) == RowIds[1]);
        uint control = ObjectRows.Field<SVisibilityBucket, uint>(original, bucket,
            nameof(SVisibilityBucket.ControlBit30HasSlotBit31ContinueLow30Slot)), slot = control & 0x3FFFFFFF;
        int value = TagRows.Offsets<SVisibilityTomeMap, SI32Element>(original, "HAda0152e.Values", SI32Element.Class)[slot];
        Check(new InstanceVisibilityMap(original).Contains(RowIds[1]), "The occupied fixture key is not found.");
        byte[] unused = original.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(unused.AsSpan(bucket + 8), control & ~0x40000000u);
        Check(!new InstanceVisibilityMap(unused).Contains(RowIds[1]), "An unused key was admitted.");
        byte[] negative = original.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(negative.AsSpan(value), -1);
        Check(!new InstanceVisibilityMap(negative).Contains(RowIds[1]), "A negative tome value was admitted.");
        byte[] invalid = original.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(bucket + 8), 0x7FFFFFFF);
        Throws<InvalidDataException>(() => new InstanceVisibilityMap(invalid).Contains(RowIds[1]));
        byte[] loop = original.ToArray();
        foreach (int row in buckets)
            BinaryPrimitives.WriteUInt32LittleEndian(loop.AsSpan(row + 8), 0x80000000);
        Throws<InvalidDataException>(() => new InstanceVisibilityMap(loop).Contains(999));
    }

    /** Generated world geometry remains selectable as its original source and is not drawn twice. */
    private static void Preview(PackageCatalog catalog, ObjectEditCompilation compiled)
    {
        var overlays = compiled.GeneratedAssets.Select(asset =>
        {
            catalog.TryGetEntry(asset.SourceTag, out var original);
            return new PackageOverlayEntry(new(asset.TagHash, asset.Native.Reference, original.Type, original.SubType,
                asset.Data.Length, "generated", asset.Native.PackageId, asset.Native.EntryIndex!.Value) { TypeInfo = asset.Native.TypeInfo })
            { Generated = true, GeneratedSourceTag = asset.SourceTag, InstanceOrigins = asset.InstanceOrigins,
                PlacementOrigins = asset.PlacementOrigins, Replaces = asset.Replaces };
        }).ToArray();
        var generated = compiled.GeneratedAssets.ToDictionary(x => x.TagHash, x => x.Data);
        catalog.SetOverlayEntries(overlays);
        catalog.ReadOverride = tag => compiled.Assets.GetValueOrDefault(tag) ?? generated.GetValueOrDefault(tag);
        var mesh = new MeshData { TagHash = Mesh, Vertices = [new(Vector3.Zero, Vector3.UnitZ, default, Vector4.One)], Indices = [] };
        var loader = new AssetSceneLoader(catalog) { MeshCache = new Dictionary<uint, MeshData[]> { [Mesh] = [mesh] } };
        var scene = loader.LoadBubble(Bubble);
        Check(scene.Instances.Count == 3, $"Replacement preview count is {scene.Instances.Count}, expected3.");
        var replaced = scene.Instances.Single(x => x.InstanceSourceTag == Table && x.InstanceSourceIndex == 1);
        Check(replaced.PlacementSourceTag == List && replaced.PlacementSourceOffset == 48
            && SceneEdits.Describe(catalog, replaced).Target?.SourceTag == Table,
            "The replacement lost the original editable placement identity.");
        Check(loader.LoadMesh(Table).Instances.Count == 3, "Direct original-table inspection hid the editable source row.");
        catalog.ReadOverride = null; catalog.SetOverlayEntries([]);
    }

}
