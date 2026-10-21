using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;
using static Checks;
using static StaticPlacementFixture;

/** The compiled fixture placement, for the parity check. */
internal sealed record PlacedFixture(ObjectEditCompilation Compiled, ObjectEdit[] Edits);

/** Two added statics compile to generated entries that Studio's own readers decode, with collision copies. */
internal static class PlacementChecks
{
    private static readonly Quaternion TurnA = new(0, 0, 0.70710677f, 0.70710677f);
    private static readonly Quaternion TurnB = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 6));

    internal static PlacedFixture Run(PackageCatalog catalog, string folder)
    {
        var scene = Load(catalog);
        Check(scene.Instances.Count == 3 && scene.Instances.All(item => item.InstanceSourceTag == Table
            && item.MapContainers.Select(container => container.TagHash).SequenceEqual([Container])),
            "The installed fixture did not decode its three rows through one container.");
        var world = WorldDocument.FromScene(scene);
        var row0 = scene.Instances.Single(item => item.InstanceSourceIndex == 0);
        var duplicate = StaticPlacements.Duplicate(catalog, row0, [], "Copy A");
        Check(duplicate.Target.Layout == ObjectEditLayout.AddedStaticInstance && duplicate.Target.Index == 0
            && duplicate.Target.Identity == 0 && duplicate.Added == new AddedPlacement(Container)
            && duplicate.Position!.SequenceEqual([0f, 0, 0]) && duplicate.Scale == 1, "Duplicate did not copy the selected row in place.");
        var editA = duplicate with { Position = [3, 4, 0.25f], Rotation = Floats(TurnA) };
        var placed = StaticPlacements.Place(catalog, world, ModelB, new(12, 5, 1), [editA], "Copy B");
        Check(placed.Target.Index == 1 && placed.Position!.SequenceEqual([12f, 5, 1]) && placed.Added!.Container == Container,
            "Place in map did not copy the model's only row at the cursor.");
        var editB = placed with { Rotation = Floats(TurnB), Scale = 2.5f };
        Check(StaticPlacements.Duplicate(catalog, row0, [editA, editB]).Target.Identity == 1,
            "A second copy of one row reused its copy number.");
        Throws<NotSupportedException>(() => StaticPlacements.Place(catalog, world, Table, Vector3.Zero, []));
        ObjectEdit[] edits = [editA, editB];
        foreach (var edit in edits) edit.Validate();

        var compiled = ObjectEditCompiler.Default.Compile(edits, catalog.ReadOriginal, catalog);
        var ordered = edits.OrderBy(edit => edit.Key, StringComparer.Ordinal).ToArray();
        VerifyCompiled(catalog, compiled, ordered);
        var again = ObjectEditCompiler.Default.Compile([editB, editA], catalog.ReadOriginal, catalog);
        Check(Same(compiled, again), "Compiling the same placements in another order changed bytes or tags.");
        Console.WriteLine($"PASS placement-compile generated={compiled.GeneratedAssets.Count} rows=2 models=2 container=+1 compound=+2 deterministic=yes");

        Redecode(catalog, compiled, ordered);
        Refusals(catalog, editA, editB);
        Coexistence(catalog, editA, editB);
        ProjectRoundTrip(catalog, folder, edits, compiled);
        return new(compiled, ordered);
    }

    private static SceneDocument Load(PackageCatalog catalog)
    {
        MeshData Mesh(uint tag) => new() { TagHash = tag, Vertices = [new(Vector3.Zero, Vector3.UnitZ, Vector2.Zero, Vector4.One)], Indices = [] };
        var loader = new AssetSceneLoader(catalog)
        { MeshCache = new Dictionary<uint, MeshData[]> { [ModelA] = [Mesh(ModelA)], [ModelB] = [Mesh(ModelB)] } };
        return loader.LoadBubble(Bubble);
    }

    private static void VerifyCompiled(PackageCatalog catalog, ObjectEditCompilation compiled, ObjectEdit[] ordered)
    {
        Check(compiled.Assets.Keys.Order().SequenceEqual([Container, Compound]), "Placement changed an asset other than its container and compound.");
        var generated = compiled.GeneratedAssets.ToDictionary(asset => asset.Native.Reference);
        Check(compiled.GeneratedAssets.Count == 4 && generated.Keys.Order().SequenceEqual([0x80806EF4u, 0x8080966Du, 0x80809671u, 0x808099D6u]),
            "Placement did not generate one table, bounds, owner and list.");
        var table = generated[0x8080966D]; var bounds = generated[0x80809671]; var owner = generated[0x80806EF4]; var list = generated[0x808099D6];
        Check(table.SourceTag == Table && bounds.SourceTag == Bounds && owner.SourceTag == Owner && list.SourceTag == List,
            "Generated entries name the wrong installed sources.");
        byte[] source = catalog.ReadOriginal(Table);
        int[] sourceRows = Rows(source, 0x40, 0x808071A3, 48);
        int[] rows = Rows(table.Data, 0x40, 0x808071A3, 48);
        Check(rows.Length == 2 && I64(table.Data, 0) == table.Data.Length && I64(table.Data, 0x38) == 2
            && U32(table.Data, 0x18) == bounds.TagHash && U64(table.Data, 0x50) == InstanceAuxiliary && U64(table.Data, 0x78) == AoKey
            && table.Data.AsSpan(0x30, 8).SequenceEqual(source.AsSpan(0x30, 8)), "The generated table header lanes differ from the source.");
        for (int i = 0; i < 2; i++)
        {
            var edit = ordered[i];
            byte[] expected = source.AsSpan(sourceRows[edit.Target.Index], 48).ToArray();
            WriteFloats(expected, 0, [.. edit.Rotation!, .. edit.Position!, edit.Scale!.Value]);
            Check(table.Data.AsSpan(rows[i], 48).SequenceEqual(expected), $"Generated row {i} is not its source row at the new transform.");
            Check(table.AddedInstances[i] == new PackageAddedInstance(Table, edit.Target.Index, edit.Target.Identity, Container, true),
                $"Generated row {i} lost its added placement identity.");
        }
        Check(table.InstanceOrigins.Count == 0 && list.Replaces.Count == 0, "An added row claimed or replaced an installed row.");
        int[] resources = Rows(table.Data, 0x58, 0x8080967D, 4), ranges = Rows(table.Data, 0x68, 0x80807190, 8);
        Check(resources.Select(at => U32(table.Data, at)).SequenceEqual(ordered.Select(edit => edit.Target.Index == 0 ? ModelA : ModelB))
            && ranges.Select(at => (U16(table.Data, at), U16(table.Data, at + 2), U16(table.Data, at + 4), U16(table.Data, at + 6)))
                .SequenceEqual([((ushort)1, (ushort)0, (ushort)0, (ushort)7), ((ushort)1, (ushort)1, (ushort)1, (ushort)7)]),
            "Generated resources or draw ranges are wrong.");
        int[] boxes = Rows(bounds.Data, 8, 0x80809673, 48);
        Vector3 low = new(float.PositiveInfinity), high = new(float.NegativeInfinity);
        for (int i = 0; i < 2; i++)
        {
            var edit = ordered[i];
            Vector3 min = V3(bounds.Data, boxes[i]), max = V3(bounds.Data, boxes[i] + 16);
            Check(F32(bounds.Data, boxes[i] + 12) == 1 && F32(bounds.Data, boxes[i] + 28) == 1 && U64(bounds.Data, boxes[i] + 32) == 0
                && U64(bounds.Data, boxes[i] + 40) == 0, $"Bounds row {i} has a baked id or W lanes other than 1.");
            var (center, half) = edit.Target.Index == 0 ? (new Vector3(0, 0, 0.5f), 0.5f) : (new Vector3(0, 0, 1), 1f);
            var matrix = Matrix4x4.CreateScale(edit.Scale!.Value) * Matrix4x4.CreateFromQuaternion(Quat(edit.Rotation!))
                * Matrix4x4.CreateTranslation(Vector(edit.Position!));
            for (int corner = 0; corner < 8; corner++)
            {
                var point = Vector3.Transform(center + half * new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1,
                    (corner & 4) == 0 ? -1 : 1), matrix);
                Check(Vector3.Min(point, min) == min && Vector3.Max(point, max) == max, $"Bounds row {i} does not hold its model.");
            }
            low = Vector3.Min(low, min); high = Vector3.Max(high, max);
        }
        Check(V3(table.Data, 0x80) == low && V3(table.Data, 0x90) == high && F32(table.Data, 0x8C) == 1 && F32(table.Data, 0x9C) == 1,
            "The generated aggregate box is not the union of its rows.");
        byte[] sourceOwner = catalog.ReadOriginal(Owner), expectedOwner = sourceOwner.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(expectedOwner.AsSpan(8), table.TagHash); expectedOwner[0x20] = 0;
        Check(owner.Data.AsSpan().SequenceEqual(expectedOwner), "The generated owner is not the source owner in current-bounds mode.");
        byte[] sourceList = catalog.ReadOriginal(List);
        int listRow = Rows(list.Data, 8, 0x808099D8, 144).Single(), sourceRow = Rows(sourceList, 8, 0x808099D8, 144).Single();
        int auxiliary = listRow + 0x78 + (int)I64(list.Data, listRow + 0x78);
        Check(list.Data.AsSpan(listRow, 0x78).SequenceEqual(sourceList.AsSpan(sourceRow, 0x78)) && U32(list.Data, auxiliary + 0x10) == owner.TagHash
            && list.PlacementOrigins.Single().Value == new PackagePlacementOrigin(List, sourceRow, 0),
            "The generated list does not copy the source placement for the generated owner.");
        byte[] container = compiled.Assets[Container], sourceContainer = catalog.ReadOriginal(Container);
        Check(Rows(container, 0x28, 0x80808BB0, 4).Select(at => U32(container, at)).SequenceEqual([List, MapList, CollisionList, list.TagHash])
            && container.AsSpan(8, 32).SequenceEqual(sourceContainer.AsSpan(8, 32)), "The container did not gain only the generated list.");
        var before = StaticCompoundShape.Parse(HavokPackfile.Parse(catalog.ReadOriginal(Compound)));
        var after = StaticCompoundShape.Parse(HavokPackfile.Parse(compiled.Assets[Compound]));
        var leaves = after.LeafBoxes();
        Check(after.Instances.Count == 4 && Enumerable.Range(0, 2).All(i => after.File.Bytes.AsSpan(after.File.Absolute(after.Instances[i].Offset), 0x30)
            .SequenceEqual(before.File.Bytes.AsSpan(before.File.Absolute(before.Instances[i].Offset), 0x30))),
            "The compound lost or changed an installed instance.");
        for (int i = 0; i < 2; i++)
        {
            var edit = ordered[i];
            var copy = after.Instances[2 + i];
            var expected = new QsTransform(Vector(edit.Position!), Quat(edit.Rotation!), new Vector3(edit.Scale!.Value));
            Check(copy.ChildImport == "i0" && Near(copy.Transform.Translation, expected.Translation)
                && Near(copy.Transform.Scale, expected.Scale) && MathF.Abs(MathF.Abs(Quaternion.Dot(copy.Transform.Rotation, expected.Rotation)) - 1) < 1e-5f,
                $"Collision copy {i} is not at its row's transform.");
            var installed = before.Instances[edit.Target.Index];
            Check(copy.FilterInfo == installed.FilterInfo, $"Collision copy {i} lost its source filter.");
            Vector3 moved(Vector3 point) => Vector3.Transform(point * expected.Scale, expected.Rotation) + expected.Translation;
            foreach (var point in new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY })
            {
                var at = moved(point);
                Check(leaves[2 + i].Contains(new(at.X, at.Y, at.Z, at.X, at.Y, at.Z), 1e-4), $"Collision copy {i} leaf does not hold its triangle.");
            }
        }
    }

    /** The overlay a workspace installs for these compiled bytes and generated entries. */
    private static void Overlay(PackageCatalog catalog, ObjectEditCompilation compiled)
    {
        var rows = compiled.Assets.Select(pair =>
        {
            catalog.TryGetEntry(pair.Key, out var entry);
            return new PackageOverlayEntry(entry! with { Size = pair.Value.Length });
        }).Concat(compiled.GeneratedAssets.Select(asset =>
        {
            catalog.TryGetEntry(asset.SourceTag, out var original);
            return new PackageOverlayEntry(new(asset.TagHash, asset.Native.Reference, original!.Type, original.SubType,
                asset.Data.Length, "generated", asset.Native.PackageId, asset.Native.EntryIndex!.Value)
            { TypeInfo = asset.Native.TypeInfo })
            {
                Generated = true,
                GeneratedSourceTag = asset.SourceTag,
                InstanceOrigins = asset.InstanceOrigins,
                PlacementOrigins = asset.PlacementOrigins,
                Replaces = asset.Replaces,
                AddedInstances = asset.AddedInstances
            };
        })).ToArray();
        var generated = compiled.GeneratedAssets.ToDictionary(asset => asset.TagHash, asset => asset.Data);
        catalog.SetOverlayEntries(rows);
        catalog.ReadOverride = tag => compiled.Assets.GetValueOrDefault(tag)?.ToArray() ?? generated.GetValueOrDefault(tag)?.ToArray();
    }

    private static void Clear(PackageCatalog catalog) { catalog.ReadOverride = null; catalog.SetOverlayEntries([]); }

    /** The scene decoder draws both copies; the inspector edits them; a move updates in place. */
    private static void Redecode(PackageCatalog catalog, ObjectEditCompilation compiled, ObjectEdit[] ordered)
    {
        try
        {
            Overlay(catalog, compiled);
            var scene = Load(catalog);
            var tableTag = compiled.GeneratedAssets.Single(asset => asset.Native.Reference == 0x8080966D).TagHash;
            var added = scene.Instances.Where(item => item.InstanceSourceTag == tableTag).OrderBy(item => item.InstanceSourceIndex).ToArray();
            Check(scene.Instances.Count == 5 && scene.Instances.Count(item => item.InstanceSourceTag == Table) == 3 && added.Length == 2,
                "The scene decoder did not draw the three installed rows and the two copies.");
            for (int i = 0; i < 2; i++)
            {
                Check(Near(added[i].Transform.Translation, Vector(ordered[i].Position!)) && added[i].Mesh.TagHash == (ordered[i].Target.Index == 0 ? ModelA : ModelB)
                    && added[i].MapContainers.Select(container => container.TagHash).SequenceEqual([Container]),
                    $"Copy {i} is drawn at the wrong place, model or container.");
                var info = SceneEdits.Describe(catalog, added[i]);
                Check(info.CanTransform && info.CanScale && info.Target?.Key == ordered[i].Key && info.Position == Vector(ordered[i].Position!)
                    && info.Scale == ordered[i].Scale, $"The inspector does not edit copy {i} as its saved placement.");
            }
            var world = WorldDocument.FromScene(scene);
            Check(TransformHandles.Read(catalog, added[0], world, out string reason) is not null, $"A copy gets no handles: {reason}");
            var moved = SceneEdits.CreateTransformEdit(catalog, added[0], new(5, 6, 0.25f), null, 1.5f, world)!;
            Check(moved.Kind == ObjectEditKind.AddedPlacement && moved.Key == ordered[0].Key && moved.Added == ordered[0].Added
                && moved.Rotation!.SequenceEqual(ordered[0].Rotation!) && moved.Scale == 1.5f,
                "Moving a copy did not keep its saved rotation and container.");
            Check(SceneEdits.CreateTransformEdit(catalog, added[0], Vector(ordered[0].Position!), null, ordered[0].Scale!.Value, world) is null,
                "An unchanged copy saved an edit.");
            var copyOfCopy = StaticPlacements.Duplicate(catalog, added[0], ordered);
            Check(copyOfCopy.Target.SourceTag == Table && copyOfCopy.Target.Index == ordered[0].Target.Index && copyOfCopy.Target.Identity == 1
                && copyOfCopy.Position!.SequenceEqual(ordered[0].Position!), "Duplicating a copy did not copy its installed source row.");

            var next = ObjectEditCompiler.Default.Compile([moved, ordered[1]], catalog.ReadOriginal, catalog);
            Check(next.GeneratedAssets.Select(asset => asset.TagHash).SequenceEqual(compiled.GeneratedAssets.Select(asset => asset.TagHash)),
                "Moving a copy reallocated its generated entries.");
            var changed = Changed(compiled, next);
            Overlay(catalog, next);
            var update = WorldPlacementUpdate.Prepare(catalog, world, changed, out reason);
            Check(update is not null, $"Moving a copy needs a reload: {reason}");
            update!.Apply();
            Check(Near(added[0].Transform.Translation, new(5, 6, 0.25f)) && MathF.Abs(added[0].Transform.GetDeterminant() - 1.5f * 1.5f * 1.5f) < 1e-3f,
                "The in-place update did not move the copy.");
            var third = ObjectEditCompiler.Default.Compile([moved, ordered[1], copyOfCopy], catalog.ReadOriginal, catalog);
            Overlay(catalog, third);
            Check(WorldPlacementUpdate.Prepare(catalog, world, Changed(next, third), out reason) is null && reason.Length > 0,
                "Adding a third copy did not ask for a reload.");
            Overlay(catalog, next);
            var removed = Changed(next, ObjectEditCompilation.Empty);
            Clear(catalog);
            Check(WorldPlacementUpdate.Prepare(catalog, world, removed, out reason) is null && reason.Length > 0,
                "Removing the copies did not ask for a reload.");
            Console.WriteLine("PASS placement-redecode scene=5 copies=2 inspector=added handles=yes move=in-place add=reload remove=reload duplicate-copy=source");
        }
        finally { Clear(catalog); }
    }

    private static uint[] Changed(ObjectEditCompilation before, ObjectEditCompilation after)
    {
        var old = before.GeneratedAssets.ToDictionary(asset => asset.TagHash, asset => asset.Data);
        var next = after.GeneratedAssets.ToDictionary(asset => asset.TagHash, asset => asset.Data);
        return before.Assets.Keys.Union(after.Assets.Keys).Where(tag => !before.Assets.TryGetValue(tag, out var a)
                || !after.Assets.TryGetValue(tag, out var b) || !a.AsSpan().SequenceEqual(b))
            .Concat(old.Keys.Union(next.Keys).Where(tag => !old.TryGetValue(tag, out var a) || !next.TryGetValue(tag, out var b)
                || !a.AsSpan().SequenceEqual(b))).Distinct().ToArray();
    }

    private static void Refusals(PackageCatalog catalog, ObjectEdit editA, ObjectEdit editB)
    {
        var compiler = ObjectEditCompiler.Default;
        Throws<InvalidDataException>(() => compiler.Compile([editA with { Added = new(OtherContainer) }], catalog.ReadOriginal, catalog));
        Throws<InvalidDataException>(() => compiler.Compile([editA with { Scale = 2000 }], catalog.ReadOriginal, catalog));
        Throws<InvalidDataException>(() => compiler.Compile([editA with { Rotation = [0, 0, 0.5f, 0.5f] }], catalog.ReadOriginal, catalog));
        Throws<InvalidDataException>(() => (editA with { Rotation = null }).Validate());
        Throws<InvalidDataException>(() => (editA with { Scale = -1 }).Validate());
        Throws<InvalidDataException>(() => (editA with { Added = null }).Validate());
        Throws<InvalidDataException>(() => (editA with { Kind = ObjectEditKind.PlacementTransform }).Validate());
        var visualOnly = compiler.Compile([editA with { Added = new(Container, false) }, editB], catalog.ReadOriginal, catalog);
        Check(StaticCompoundShape.Parse(HavokPackfile.Parse(visualOnly.Assets[Compound])).Instances.Count == 3,
            "A copy without collision still added a compound instance.");
        var none = compiler.Compile([editA with { Added = new(Container, false) }], catalog.ReadOriginal, catalog);
        Check(!none.Assets.ContainsKey(Compound) && none.GeneratedAssets.Count == 4, "A visual-only copy changed the compound.");
        var graph = new ObjectEditCompileOptions("graph", new HashSet<ushort>()) { AddedNativeReferences = new HashSet<uint> { 0x808099D6 } };
        Throws<InvalidDataException>(() => compiler.Compile([editA], catalog.ReadOriginal, catalog, graph));
        Console.WriteLine("PASS placement-refusals container=refused scale=refused rotation=refused validate=4 visual-only=compound-unchanged added-graph=refused");
    }

    /** An edit of an installed row of the same table isolates as before, beside the copies. */
    private static void Coexistence(PackageCatalog catalog, ObjectEdit editA, ObjectEdit editB)
    {
        byte[] source = catalog.ReadOriginal(Table);
        int row = Rows(source, 0x40, 0x808071A3, 48)[2];
        var scaled = new ObjectEdit(ObjectEditKind.PlacementTransform, new(Table, ObjectEditLayout.StaticInstance, row, 48, 2, 0,
            BinaryDelta.Hash(source.AsSpan(row, 48))), "Row 2", Scale: 3);
        var both = ObjectEditCompiler.Default.Compile([editA, editB, scaled], catalog.ReadOriginal, catalog);
        var lists = both.GeneratedAssets.Where(asset => asset.Native.Reference == 0x808099D6).ToArray();
        byte[] container = both.Assets[Container];
        var members = Rows(container, 0x28, 0x80808BB0, 4).Select(at => U32(container, at)).ToArray();
        Check(both.GeneratedAssets.Count == 8 && lists.Length == 2 && members.Length == 5 && lists.All(list => members.Contains(list.TagHash))
            && lists.Count(list => list.Replaces.Count != 0) == 1, "An isolated row edit and added copies did not share the container.");
        Console.WriteLine("PASS placement-coexistence isolated=1 added=2 generated=8 container-members=5");
    }

    /** The manifest stores the recipes only; a reopened project and a build give the same bytes. */
    private static void ProjectRoundTrip(PackageCatalog catalog, string folder, ObjectEdit[] edits, ObjectEditCompilation compiled)
    {
        var project = StudioProject.Create(Path.Combine(folder, "project"), "Placement");
        project.ObjectEditCompiler = ObjectEditCompiler.Default;
        Check(project.RecordObjectEdits(edits, catalog), "Recording two placements saved nothing.");
        var manifest = JsonNode.Parse(File.ReadAllText(project.ManifestPath))!;
        Check(manifest["version"]!.GetValue<int>() == 8 && manifest["objectEdits"]!.AsArray().Count == 2
            && manifest["objectEdits"]![0]!["added"]!["container"]!.GetValue<uint>() == Container,
            "The manifest did not save version 8 placement recipes.");
        Check(project.Changes.Count == 0 && project.AddedAssets.Count == 0, "The project stored game bytes for a placement.");
        Check(!project.RecordObjectEdits(edits, catalog), "Recording the same placements again changed the project.");
        var reopened = StudioProject.Open(project.ManifestPath);
        reopened.ObjectEditCompiler = ObjectEditCompiler.Default;
        var saved = project.CompileObjectEdits(catalog);
        Check(saved.GeneratedAssets.Zip(compiled.GeneratedAssets).All(pair => pair.First.Data.AsSpan().SequenceEqual(pair.Second.Data)
            || pair.First.Native.Reference is 0x8080966D or 0x80806EF4 or 0x808099D6), "The project compiled other placement bytes.");
        Check(Same(reopened.CompileObjectEdits(catalog), saved), "A reopened project compiled other bytes.");
        int readback = Readback(catalog, folder, saved);
        reopened.RevertObjectEdit(edits[0].Key);
        var single = reopened.CompileObjectEdits(catalog);
        Check(Rows(single.GeneratedAssets.Single(asset => asset.Native.Reference == 0x8080966D).Data, 0x40, 0x808071A3, 48).Length == 1,
            "Removing one copy left two generated rows.");
        var old = JsonNode.Parse(File.ReadAllText(reopened.ManifestPath))!; old["version"] = 7;
        File.WriteAllText(reopened.ManifestPath, old.ToJsonString());
        Throws<InvalidDataException>(() => StudioProject.Open(reopened.ManifestPath));
        Console.WriteLine($"PASS placement-project version=8 recipes=2 deltas=0 reopen=same package-readback={readback} remove=1-row version7=refused");
    }

    /**
     * Writes the compiled entries as a build lays them out: a patch of the map package and a new package per
     * generated namespace. The synthetic map has no dependency records, so ProjectBuilder itself refuses it.
     * @return The entries read back.
     */
    private static int Readback(PackageCatalog catalog, string folder, ObjectEditCompilation compiled)
    {
        string packages = Path.Combine(folder, "readback", "packages");
        Directory.CreateDirectory(packages);
        string source = Path.Combine(catalog.PackageDirectory, "w64_placement_0100_0.pkg");
        File.Copy(source, Path.Combine(packages, "w64_placement_0100_0.pkg"));
        PackageWriter.WritePatch(source, Path.Combine(packages, "w64_placement_0100_1.pkg"),
            compiled.Assets.ToDictionary(pair => (int)(pair.Key - Base), pair => pair.Value), Path.GetDirectoryName(catalog.PackageDirectory));
        foreach (var group in compiled.GeneratedAssets.GroupBy(asset => asset.Native.PackageId))
        {
            var entries = Enumerable.Range(0, group.Max(asset => asset.Native.EntryIndex!.Value) + 1)
                .Select(_ => new AuthoredPackageEntry(0, 0, [])).ToArray();
            foreach (var asset in group) entries[asset.Native.EntryIndex!.Value] = new(asset.Native.Reference, asset.Native.TypeInfo, asset.Data, asset.Native.Name);
            PackageWriter.WriteNew(Path.Combine(packages, $"w64_sunrise_{group.Key:x4}_0.pkg"), group.Key, entries);
        }
        using var built = PackageCatalog.Open(Path.Combine(folder, "readback"), cacheOptions: new() { Enabled = false });
        foreach (var asset in compiled.GeneratedAssets)
            Check(built.TryGetEntry(asset.TagHash, out var entry) && entry.Reference == asset.Native.Reference
                && built.ReadOriginal(asset.TagHash).AsSpan().SequenceEqual(asset.Data), $"Package readback of {asset.TagHash:X8} differs.");
        foreach (var (tag, bytes) in compiled.Assets)
            Check(built.ReadOriginal(tag).AsSpan().SequenceEqual(bytes), $"Package readback of {tag:X8} differs.");
        var scene = Load(built);
        Check(scene.Instances.Count == 5 && scene.DecodeErrorCount == 0, "The written packages do not draw the copies.");
        return compiled.GeneratedAssets.Count + compiled.Assets.Count;
    }

    private static bool Same(ObjectEditCompilation a, ObjectEditCompilation b) =>
        a.Assets.Keys.Order().SequenceEqual(b.Assets.Keys.Order()) && a.Assets.All(pair => pair.Value.AsSpan().SequenceEqual(b.Assets[pair.Key]))
        && a.GeneratedAssets.Count == b.GeneratedAssets.Count && a.GeneratedAssets.Zip(b.GeneratedAssets)
            .All(pair => pair.First.TagHash == pair.Second.TagHash && pair.First.Data.AsSpan().SequenceEqual(pair.Second.Data));

    internal static float[] Floats(Quaternion q) => [q.X, q.Y, q.Z, q.W];
    internal static Quaternion Quat(float[] v) => new(v[0], v[1], v[2], v[3]);
    internal static Vector3 Vector(float[] v) => new(v[0], v[1], v[2]);
    private static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-4f;

    internal static int[] Rows(byte[] bytes, int field, uint type, int stride)
    {
        long count = I64(bytes, field);
        if (count == 0) return [];
        int header = checked(field + 8 + (int)I64(bytes, field + 8));
        if (U32(bytes, header - 4) != 0x80809FBD || I64(bytes, header) != count || U32(bytes, header + 8) != type)
            throw new InvalidDataException("A typed array header is wrong.");
        return Enumerable.Range(0, (int)count).Select(i => header + 16 + i * stride).ToArray();
    }

    internal static ushort U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
    internal static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
    internal static ulong U64(byte[] b, int at) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at));
    internal static long I64(byte[] b, int at) => BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(at));
    internal static float F32(byte[] b, int at) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at));
    internal static Vector3 V3(byte[] b, int at) => new(F32(b, at), F32(b, at + 4), F32(b, at + 8));

    private static void WriteFloats(byte[] bytes, int at, float[] values)
    {
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + i * 4), values[i]);
    }
}
