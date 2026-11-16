using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;
using SunriseStudio.Tags.Generated;
using static Checks;
using static StaticPlacementFixture;

/** Added statics get their source's collision, select and move as their edit, and place at a world point. */
internal static class StaticCopyChecks
{
    private static readonly Quaternion Turn = new(0, 0, 0.70710677f, 0.70710677f);

    internal static void Synthetic()
    {
        string folder = Path.Combine(Path.GetTempPath(), "sunrise-static-copy-" + Guid.NewGuid().ToString("N"));
        try
        {
            Write(folder);
            using var catalog = PackageCatalog.Open(folder, cacheOptions: new() { Enabled = false });
            var scene = Load(catalog);
            Check(scene.Instances.Count == 3, "The fixture did not draw its three rows.");
            var world = WorldDocument.FromScene(scene);
            var row0 = scene.Instances.Single(item => item.InstanceSourceIndex == 0);
            var copy = SceneEdits.CreateCopyEdit(catalog, row0, [], new(3, 4, 0.25f), Turn, 1, world);
            var compiled = ObjectEditCompiler.Default.Compile([copy], catalog.ReadOriginal, catalog);
            Collision(catalog, copy, compiled);
            NoCollision(catalog, scene, world);
            Moves(catalog, copy, compiled);
            Place(catalog, world);
            Console.WriteLine("PASS static-copy collision=copied no-collision=noted select=edit move=in-place remove=refused place=cursor");
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    /** The copy's compound gains one instance at the copy's transform; the installed instances keep their bytes. */
    private static void Collision(PackageCatalog catalog, ObjectEdit copy, ObjectEditCompilation compiled)
    {
        var table = compiled.GeneratedAssets.Single(asset => asset.Native.Reference == SStaticMeshInstances.Class);
        Check(table.AddedInstances.Single().Value == new PackageAddedInstance(Table, copy.Target.Offset, 0, copy.Target.Identity, 0),
            "The generated row does not name its copy edit.");
        var before = StaticCompoundShape.Parse(HavokPackfile.Parse(catalog.ReadOriginal(Compound)));
        var after = StaticCompoundShape.Parse(HavokPackfile.Parse(compiled.Assets[Compound]));
        Check(after.Instances.Count == 3 && Enumerable.Range(0, 2).All(i =>
                after.File.Bytes.AsSpan(after.File.Absolute(after.Instances[i].Offset), 0x30)
                    .SequenceEqual(before.File.Bytes.AsSpan(before.File.Absolute(before.Instances[i].Offset), 0x30))),
            "The compound lost or changed an installed instance.");
        var added = after.Instances[2];
        Check(added.ChildImport == "i0" && Vector3.Distance(added.Transform.Translation, new(3, 4, 0.25f)) < 1e-4f
            && MathF.Abs(MathF.Abs(Quaternion.Dot(added.Transform.Rotation, Turn)) - 1) < 1e-5f
            && added.FilterInfo == before.Instances[0].FilterInfo, "The collision copy is not its source at the copy.");
        Check(after.LeafBoxes()[2].Contains(new(3, 4, 0.25f, 3, 4, 0.25f), 1e-4), "The collision copy's leaf does not hold it.");
        Check(compiled.Notes.Count == 0, "A copy with collision left a note.");
    }

    /** A row with no compound instance is copied without collision, and the compile says so. */
    private static void NoCollision(PackageCatalog catalog, SceneDocument scene, WorldDocument world)
    {
        var row2 = scene.Instances.Single(item => item.InstanceSourceIndex == 2);
        var copy = SceneEdits.CreateCopyEdit(catalog, row2, [], new(30, 0, 0), Quaternion.Identity, 1, world);
        var compiled = ObjectEditCompiler.Default.Compile([copy], catalog.ReadOriginal, catalog);
        Check(!compiled.Assets.ContainsKey(Compound) && compiled.Notes.Count == 1, "A row without collision changed the compound.");
    }

    /** The drawn copy edits as its AddInstance edit, and a move updates the world in place. */
    private static void Moves(PackageCatalog catalog, ObjectEdit copy, ObjectEditCompilation compiled)
    {
        try
        {
            Overlay(catalog, compiled);
            var scene = Load(catalog);
            var world = WorldDocument.FromScene(scene);
            var drawn = scene.Instances.Single(item => item.InstanceSourceTag != Table);
            var info = SceneEdits.Describe(catalog, drawn, world);
            Check(info.CanTransform && info.Addition?.Key == copy.Key && info.Target?.Key == copy.Target.Key
                && info.Position == new Vector3(3, 4, 0.25f), "The inspector does not edit the copy as its edit.");
            Check(TransformHandles.Read(catalog, drawn, world, out string reason) is not null, $"A copy gets no handles: {reason}");
            Check(SceneEdits.CreateTransformEdit(catalog, drawn, new(3, 4, 0.25f), null, 1, world) is null, "An unmoved copy saved an edit.");
            var moved = SceneEdits.CreateTransformEdit(catalog, drawn, new(5, 6, 0.25f), null, 1.5f, world)!;
            Check(moved.Kind == ObjectEditKind.AddInstance && moved.Key == copy.Key && moved.Rotation!.SequenceEqual(copy.Rotation!)
                && moved.Scale == 1.5f, "Moving a copy did not keep its edit and rotation.");
            Throws<NotSupportedException>(() => SceneEdits.CreateRemoveEdit(catalog, drawn, world));
            var again = SceneEdits.CreateCopyEdit(catalog, drawn, [copy], new(0, 9, 0), Quaternion.Identity, 1, world);
            Check(again.Target.Key == copy.Target.Key && again.Target.Identity == copy.Target.Identity + 1,
                "Copying a copy did not copy its source row.");
            var next = ObjectEditCompiler.Default.Compile([moved], catalog.ReadOriginal, catalog);
            Check(next.GeneratedAssets.Select(asset => asset.TagHash).SequenceEqual(compiled.GeneratedAssets.Select(asset => asset.TagHash)),
                "Moving a copy reallocated its generated entries.");
            var changed = Changed(compiled, next);
            Overlay(catalog, next);
            var update = WorldPlacementUpdate.Prepare(catalog, world, changed, out reason);
            Check(update is not null, $"Moving a copy needs a reload: {reason}");
            update!.Apply();
            Check(Vector3.Distance(drawn.Transform.Translation, new(5, 6, 0.25f)) < 1e-4f, "The in-place update did not move the copy.");
        }
        finally { Clear(catalog); }
    }

    /** A model placed at a world point joins the nearest static's container at that point. */
    private static void Place(PackageCatalog catalog, WorldDocument world)
    {
        var place = SceneEdits.CreatePlacementEdit(catalog, world, ModelB, [], new(12, 5, 1), "Placed");
        Check(place.Kind == ObjectEditKind.AddPlacement && place.Target.SourceTag == Container && place.Model == ModelB
            && place.Position!.SequenceEqual([12f, 5, 1]) && place.Target.Identity == 1, "Place in map chose the wrong container or point.");
        var placed = ObjectEditCompiler.Default.Compile([place], catalog.ReadOriginal, catalog);
        var table = placed.GeneratedAssets.Single(asset => asset.Native.Reference == SStaticMeshInstances.Class);
        Check(table.AddedInstances.Single().Value.Model == ModelB, "The placed row does not name its edit.");
        try
        {
            Overlay(catalog, placed);
            var scene = Load(catalog);
            var drawn = scene.Instances.Single(item => item.InstanceSourceTag == table.TagHash);
            var info = SceneEdits.Describe(catalog, drawn, WorldDocument.FromScene(scene));
            Check(info.Addition?.Key == place.Key && Vector3.Distance(drawn.Transform.Translation, new(12, 5, 1)) < 1e-4f,
                "The placed model is not drawn at the cursor or does not edit as its edit.");
        }
        finally { Clear(catalog); }
    }

    private static SceneDocument Load(PackageCatalog catalog)
    {
        MeshData Mesh(uint tag) => new() { TagHash = tag, Vertices = [new(Vector3.Zero, Vector3.UnitZ, Vector2.Zero, Vector4.One)], Indices = [] };
        var loader = new AssetSceneLoader(catalog)
        { MeshCache = new Dictionary<uint, MeshData[]> { [ModelA] = [Mesh(ModelA)], [ModelB] = [Mesh(ModelB)] } };
        return loader.LoadBubble(Bubble);
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
                Generated = true, GeneratedSourceTag = asset.SourceTag, InstanceOrigins = asset.InstanceOrigins,
                PlacementOrigins = asset.PlacementOrigins, Replaces = asset.Replaces, AddedInstances = asset.AddedInstances
            };
        })).ToArray();
        var generated = compiled.GeneratedAssets.ToDictionary(asset => asset.TagHash, asset => asset.Data);
        catalog.SetOverlayEntries(rows);
        catalog.ReadOverride = tag => compiled.Assets.GetValueOrDefault(tag)?.ToArray() ?? generated.GetValueOrDefault(tag)?.ToArray();
    }

    private static void Clear(PackageCatalog catalog) { catalog.ReadOverride = null; catalog.SetOverlayEntries([]); }

    private static uint[] Changed(ObjectEditCompilation before, ObjectEditCompilation after)
    {
        var old = before.Assets.Concat(before.GeneratedAssets.Select(asset => KeyValuePair.Create(asset.TagHash, asset.Data)))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var next = after.Assets.Concat(after.GeneratedAssets.Select(asset => KeyValuePair.Create(asset.TagHash, asset.Data)))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        return old.Keys.Union(next.Keys).Where(tag => !old.TryGetValue(tag, out var a) || !next.TryGetValue(tag, out var b)
            || !a.AsSpan().SequenceEqual(b)).ToArray();
    }
}
