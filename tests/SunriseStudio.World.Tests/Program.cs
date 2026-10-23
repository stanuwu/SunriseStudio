using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

string temporary = Path.Combine(Path.GetTempPath(), "sunrise-world-tests-" + Guid.NewGuid().ToString("N"));
try
{
    SpawnPointChecks.Run(temporary);
    string installation = Path.Combine(temporary, "fixture");
    WorldFixture.Create(installation);
    using var catalog = PackageCatalog.Open(installation);
    CubemapChecks.Synthetic(catalog);
    HierarchyChecks.Synthetic();
    var names = StudioNameIndex.Create(catalog);
    var loader = new WorldLoader(catalog, names);
    var index = loader.BuildCatalog();
    Check(index.Maps.Count == 1 && index.Missions.Count == 1 && index.MissionsFor(WorldFixture.Base).Count == 1, "World index lost map ownership.");
    var map = loader.LoadMap(WorldFixture.Base);
    Check(map.MapScene.Markers.Count == 2 && map.Scene.LightTables.Count == 1 && map.Objects.Any(x => x.Kind == WorldObjectKind.Light), "Map sources were omitted.");
    var all = loader.LoadMission(map, WorldFixture.Base + 4);
    HierarchyChecks.Mission(all);
    Check(ReferenceEquals(map.MapScene, all.MapScene) && all.AllStates.Count == 3 && all.Scene.ScenarioStates.Count == 3, "Mission replaced the map or discarded states.");
    Check(all.Scene.AuthoredObjects.Count == 3 && all.Scene.Markers.Count(x => x.Layer == SceneLayer.Spawns) == 3
        && all.Scene.Markers.Count(x => x.Layer == SceneLayer.Volumes) == 1, "Mission content was omitted.");
    Check(all.CanonicalBubble(WorldFixture.Base + 2) == WorldFixture.BubbleA, "Bubble identity aliases were lost.");
    var selected = loader.LoadMission(all, WorldFixture.Base + 4, new WorldLoadOptions
    {
        ScenarioStates = ScenarioStateMode.Selected,
        StateOrdinals = new Dictionary<uint, int> { [WorldFixture.BubbleA] = 1 },
        BubbleNameHashes = new HashSet<uint> { WorldFixture.BubbleA }
    });
    Check(selected.AllStates.Count == 3 && selected.Scene.ScenarioStates.Count == 1
        && selected.Scene.Markers.Count(x => x.Layer == SceneLayer.Spawns) == 1
        && selected.Scene.AuthoredObjects.Count == 1, "Selected states discarded definitions or loaded inactive objects.");
    Check(selected.Objects.Where(x => x.Kind == WorldObjectKind.AuthoredObject).Single().Owners
        .Contains(new(WorldFixture.Base + 4, WorldFixture.BubbleA, 1)), "Authored object ownership was lost.");
    var relabeled = selected.Relabel(names);
    Check(ReferenceEquals(relabeled.Scene, selected.Scene) && ReferenceEquals(relabeled.Scene.Markers[0], selected.Scene.Markers[0]), "Relabel replaced viewport objects.");
    var oldLight = selected.Objects.Single(x => x.Kind == WorldObjectKind.Light).Source;
    Check(ReferenceEquals(relabeled.Find(new SelectionProxy(oldLight))?.Source, oldLight), "Relabel lost a light proxy source.");
    var cached = relabeled.Inspect(relabeled.Scene.Markers[0]);
    Check(ReferenceEquals(cached, relabeled.Inspect(relabeled.Scene.Markers[0])), "Unchanged inspection was rebuilt.");
    var standalone = WorldDocument.FromScene(map.MapScene, names);
    Check(!standalone.HasMapContext && standalone.Inspect(map.MapScene.Markers[0]) is { Properties.Count: > 8 }, "Standalone inspection is missing.");
    var sharedMesh = new MeshData { TagHash = WorldFixture.Base + 5 };
    MeshInstance Placed(uint owner) => new() { Mesh = sharedMesh, InstanceSourceTag = owner, InstanceSourceIndex = 0,
        BubbleHash = WorldFixture.BubbleA, ActivityTag = WorldFixture.Base + 4, StateOrdinal = 1 };
    var firstPlacement = Placed(WorldFixture.Base + 6);
    var secondPlacement = Placed(WorldFixture.Base + 7);
    var identityScene = new SceneDocument(); identityScene.Instances.AddRange([firstPlacement, secondPlacement, secondPlacement]);
    var identities = WorldDocument.FromScene(identityScene).Objects;
    var filteredScene = new SceneDocument(); filteredScene.Instances.AddRange([Placed(WorldFixture.Base + 7), Placed(WorldFixture.Base + 7)]);
    var filteredIds = WorldDocument.FromScene(filteredScene).Objects;
    Check(identities[1].Id == filteredIds[0].Id && identities[2].Id == filteredIds[1].Id
        && identities.Select(x => x.Id).Distinct().Count() == 3,
        "Unrelated placement filtering changed stable identities or merged duplicate rows.");
    secondPlacement.Name = "Relabeled object";
    Check(WorldDocument.FromScene(identityScene).Objects[1].Id == identities[1].Id, "Names changed placement identity.");
    Throws<ArgumentException>(() => loader.LoadMap(WorldFixture.Base, new() { BubbleNameHashes = new HashSet<uint> { 0xDEADBEEF } }));
    Console.WriteLine("PASS world map-and-mission=retained bubbles=owned states=selected definitions=complete labels=stable");

    var project = StudioProject.Create(Path.Combine(temporary, "project"), "World edits");
    project.ObjectEditCompiler = ObjectEditCompiler.Default;
    // Decoders see raw deltas with object edits on top, as the editor overlay does.
    catalog.ReadOverride = tag => project.ContainsEdit(tag) || project.CompileObjectEdits(catalog).Assets.ContainsKey(tag)
        ? project.ReadCompiled(tag, catalog) : null;
    var marker = map.Scene.Markers.Single(x => x.SourceTag == WorldFixture.Base + 6);
    var placement = SceneEdits.Describe(catalog, marker, map);
    Check(placement.CanTransform && placement.CanScale && placement.CoordinateSpace == "Local"
        && placement.Target?.Layout == ObjectEditLayout.Placement, "Typed placement writer unavailable.");
    var placementEdit = SceneEdits.CreateTransformEdit(catalog, marker, new(7, 8, 9), Quaternion.Identity, 3, map, "Marker");
    Check(placementEdit is { Rotation: null, Scale: 3, Position: not null }, "An unchanged rotation was recorded.");
    Check(project.RecordObjectEdit(placementEdit!, catalog), "The placement edit was not saved.");
    AssertChangedOnly(catalog.ReadOriginal(placement.SourceTag), project.ReadCompiled(placement.SourceTag, catalog),
        placement.SourceOffset + 16, 32);
    var moved = loader.LoadMap(WorldFixture.Base).Scene.Markers.Single(x => x.SourceTag == marker.SourceTag);
    Check(moved.Position == new Vector3(7, 8, 9), "Placement object edit did not reach world decoding.");
    var spawn = selected.Scene.Markers.Single(x => x.Layer == SceneLayer.Spawns);
    var spawnInfo = SceneEdits.Describe(catalog, spawn, selected);
    Check(spawnInfo.CanTransform && !spawnInfo.CanScale && spawnInfo.CoordinateSpace == "World", "Spawn edit contract is wrong.");
    Throws<NotSupportedException>(() => SceneEdits.CreateTransformEdit(catalog, spawn, Vector3.Zero, Quaternion.Identity, 2, selected));
    var volume = selected.Scene.Markers.Single(x => x.Layer == SceneLayer.Volumes);
    var bounds = SceneEdits.Describe(catalog, volume, selected);
    Check(bounds.CanBounds && !bounds.CanTransform, "Trigger edit contract is wrong.");
    project.RecordObjectEdit(SceneEdits.CreateBoundsEdit(catalog, volume, new(-4), new(4), selected)!, catalog);
    var updated = loader.LoadMission(WorldFixture.Base, WorldFixture.Base + 4);
    Check(updated.Scene.Markers.Single(x => x.Layer == SceneLayer.Volumes).Extents == new Vector3(4), "Bounds object edit did not reach trigger decoding.");
    var light = (WorldLight)map.Objects.Single(x => x.Kind == WorldObjectKind.Light).Source;
    var lightInfo = SceneEdits.Describe(catalog, light, map);
    Check(lightInfo.CanTransform && lightInfo.CoordinateSpace == "World", "Light world transform contract is wrong.");
    project.RecordObjectEdit(SceneEdits.CreateTransformEdit(catalog, light, new(20, 30, 40), Quaternion.Identity, 2, map)!, catalog);
    Check(loader.LoadMap(WorldFixture.Base).Scene.LightTables.Single().Table.Rows[0].Translation == new Vector3(20, 30, 40), "Light edit did not reach decoding.");

    uint tableTag = WorldFixture.Base + 20, boundsTag = WorldFixture.Base + 24;
    byte[] instanceData = catalog.ReadOriginal(tableTag), installedBounds = catalog.ReadOriginal(boundsTag);
    int transformRow = checked(0x48 + (int)BinaryPrimitives.ReadInt64LittleEndian(instanceData.AsSpan(0x48)) + 16);
    int box = checked(8 + 8 + (int)BinaryPrimitives.ReadInt64LittleEndian(installedBounds.AsSpan(16)) + 16);
    MeshInstance Instance(int index) => new()
    {
        Mesh = new MeshData(), InstanceSourceTag = tableTag, InstanceSourceIndex = index,
        NativeInstanceData = catalog.Read(tableTag).AsMemory(transformRow + index * 48, 48),
        Transform = Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(101, 2, 3)
    };
    Vector3 Lane(byte[] bytes, int at) => new(BitConverter.ToSingle(bytes, at), BitConverter.ToSingle(bytes, at + 4),
        BitConverter.ToSingle(bytes, at + 8));
    bool Near(Vector3 value, Vector3 expected) => Vector3.Distance(value, expected) < 1e-4f;
    bool Box(byte[] bytes, int row, Vector3 min, Vector3 max) => Near(Lane(bytes, row), min) && Near(Lane(bytes, row + 16), max)
        && BitConverter.ToSingle(bytes, row + 12) == 1 && BitConverter.ToSingle(bytes, row + 28) == 1;
    var instance = Instance(0);
    var instanceInfo = SceneEdits.Describe(catalog, instance);
    Check(instanceInfo.CanTransform && instanceInfo.Position == new Vector3(1, 2, 3), "Instance editor confused native local and world position.");
    var tree = SceneEdits.CreateTransformEdit(catalog, instance, new(9, 8, 7), null, 4, name: "Tree")!;
    Check(tree is { Rotation: null, Scale: 4 } && tree.Target.Index == 0, "The instance edit recorded the wrong fields.");
    byte[] manifestBeforeOverflow = File.ReadAllBytes(project.ManifestPath);
    var overflowing = SceneEdits.CreateTransformEdit(catalog, instance, Vector3.Zero, null, float.MaxValue)!;
    overflowing.Validate();
    Throws<InvalidDataException>(() => project.RecordObjectEdit(overflowing, catalog));
    Check(File.ReadAllBytes(project.ManifestPath).SequenceEqual(manifestBeforeOverflow)
        && project.FindObjectEdit(tree.Key) is null
        && catalog.Read(tableTag).SequenceEqual(instanceData) && catalog.Read(boundsTag).SequenceEqual(installedBounds),
        "Overflowing derived bounds changed the project or scene bytes.");
    project.RecordObjectEdit(tree, catalog);
    var compiled = project.CompileObjectEdits(catalog);
    // Old row scale 2 at (1,2,3), new scale 4 at (9,8,7): table point p moves to 2p + (7,4,1).
    byte[] editedTable = compiled.Assets[tableTag], editedBounds = compiled.Assets[boundsTag];
    for (int i = 0; i < instanceData.Length; i++)
        if ((i < transformRow + 16 || i >= transformRow + 32) && (i < 0x80 || i >= 0xA0))
            Check(instanceData[i] == editedTable[i], "Instance edit changed an unrelated byte.");
    Check(Box(editedBounds, box, new(5, 2, -1), new(9, 6, 3)) && Box(editedBounds, box + 48, new(19, -1, -1), new(21, 1, 1)),
        "Instance bounds row did not follow the edited transform.");
    Check(Near(Lane(editedTable, 0x80), new(-1)) && Near(Lane(editedTable, 0x90), new(21, 6, 3)),
        "Instance table aggregate does not hold the moved box.");
    Check(compiled.Edits.Single(x => x.Edit.Key == tree.Key).Tags.SequenceEqual(new[] { tableTag, boundsTag }),
        "The instance edit does not list the tags its rule wrote.");
    Check(SceneEdits.CreateTransformEdit(catalog, Instance(0), new(9, 8, 7), null, 4) is null, "An unchanged transform recorded an edit.");
    Check(!SceneEdits.Describe(catalog, instance).CanTransform, "Stale instance snapshot remained editable.");

    // A second tree in the same table composes into one output for each tag.
    project.RecordObjectEdit(SceneEdits.CreateTransformEdit(catalog, Instance(1), new(30, 0, 0), null, 2, name: "Second tree")!, catalog);
    compiled = project.CompileObjectEdits(catalog);
    Check(Box(compiled.Assets[boundsTag], box, new(5, 2, -1), new(9, 6, 3)) && Box(compiled.Assets[boundsTag], box + 48, new(28, -2, -2), new(32, 2, 2))
        && Near(Lane(compiled.Assets[tableTag], 0x80), new(-1, -2, -2)) && Near(Lane(compiled.Assets[tableTag], 0x90), new(32, 6, 3)),
        "Two instance edits in one table did not compose.");
    var reversed = ObjectEditCompiler.Default.Compile(project.ObjectEdits.Reverse().ToArray(), tag => project.ReadAsset(tag, catalog), catalog);
    Check(reversed.Assets.Keys.SequenceEqual(compiled.Assets.Keys) && reversed.Assets.All(x => x.Value.SequenceEqual(compiled.Assets[x.Key])),
        "Compiling the same edits in another order gave different bytes.");

    // A later edit of one tree replaces only the values it sets.
    project.RecordObjectEdit(SceneEdits.CreateTransformEdit(catalog, Instance(0), new(10, 8, 7), null, 4)!, catalog);
    Check(project.ObjectEdits.Count(x => x.Target.SourceTag == tableTag) == 2 && project.FindObjectEdit(tree.Key) is { Scale: 4, Name: "Tree" },
        "A second apply on one tree did not merge into its entry.");
    Check(Box(project.CompileObjectEdits(catalog).Assets[boundsTag], box, new(6, 2, -1), new(10, 6, 3)), "The merged edit was not rebuilt from the installed box.");
    project.RevertObjectEdit(tree.Key);
    compiled = project.CompileObjectEdits(catalog);
    Check(compiled.Assets[tableTag].AsSpan(transformRow, 48).SequenceEqual(instanceData.AsSpan(transformRow, 48))
        && compiled.Assets[boundsTag].AsSpan(box, 48).SequenceEqual(installedBounds.AsSpan(box, 48)),
        "Reverting one tree did not restore its installed row and box.");
    project.RevertObjectEdit(project.ObjectEdits.Single(x => x.Target.SourceTag == tableTag).Key);
    compiled = project.CompileObjectEdits(catalog);
    Check(!compiled.Assets.ContainsKey(tableTag) && !compiled.Assets.ContainsKey(boundsTag)
        && catalog.Read(tableTag).SequenceEqual(instanceData) && catalog.Read(boundsTag).SequenceEqual(installedBounds),
        "Reverting every instance edit did not restore the installed bytes.");

    // An older build scaled the row without its box. The object edit rebuilds the box from the installed original.
    var stale = StudioProject.Create(Path.Combine(temporary, "stale"), "Stale box");
    stale.ObjectEditCompiler = ObjectEditCompiler.Default;
    byte[] scaledRow = instanceData.ToArray();
    BinaryPrimitives.WriteSingleLittleEndian(scaledRow.AsSpan(transformRow + 16), 9);
    BinaryPrimitives.WriteSingleLittleEndian(scaledRow.AsSpan(transformRow + 20), 8);
    BinaryPrimitives.WriteSingleLittleEndian(scaledRow.AsSpan(transformRow + 24), 7);
    BinaryPrimitives.WriteSingleLittleEndian(scaledRow.AsSpan(transformRow + 28), 4);
    stale.ApplyEdit(tableTag, scaledRow, catalog);
    stale.RecordObjectEdit(tree with { Name = null }, catalog);
    Check(Box(stale.CompileObjectEdits(catalog).Assets[boundsTag], box, new(5, 2, -1), new(9, 6, 3)),
        "An object edit over a stale raw row did not rebuild the box.");
    string changedInstall = Path.Combine(temporary, "changed-fixture");
    WorldFixture.Create(changedInstall, new Vector3(1, 2, 4));
    using (var changed = PackageCatalog.Open(changedInstall))
    {
        Throws<InvalidDataException>(() => stale.CompileObjectEdits(changed));
        Throws<InvalidDataException>(() => stale.Build(changed));
    }
    Throws<InvalidDataException>(() => SceneEdits.CreateTransformEdit(catalog, marker, new(float.NaN), Quaternion.Identity, 1, map));
    Throws<InvalidDataException>(() => SceneEdits.CreateBoundsEdit(catalog, volume, Vector3.One, -Vector3.One, selected));
    catalog.ReadOverride = null;
    Console.WriteLine("PASS world object-edits placement=decoded spawn-scale=rejected trigger-bounds=decoded light-world=decoded "
        + "instance-bounds=derived composed=one-table revert=installed stale-base=refused");
    PlacementUpdateChecks.Synthetic(temporary);

    int gameIndex = Array.IndexOf(args, "--game-root");
    if (gameIndex >= 0)
    {
        using var owned = PackageCatalog.Open(args[gameIndex + 1]);
        var ownedLoader = new WorldLoader(owned, StudioNameIndex.Create(owned));
        var ownedIndex = ownedLoader.BuildCatalog();
        Check(ownedIndex.Maps.Count > 0 && ownedIndex.Missions.Count > 0, "Owned world catalog is empty.");
        int mapIndex = Array.IndexOf(args, "--map"), missionIndex = Array.IndexOf(args, "--mission");
        var candidate = mapIndex < 0
            ? ownedIndex.Maps.OrderBy(x => x.BubbleHashes.Count).First(x => ownedIndex.MissionsFor(x.Tag).Count > 0)
            : ownedIndex.Maps.Single(x => x.Tag == Convert.ToUInt32(args[mapIndex + 1], 16));
        var mission = missionIndex < 0 ? ownedIndex.MissionsFor(candidate.Tag).First()
            : ownedIndex.MissionsFor(candidate.Tag).Single(x => x.Tag == Convert.ToUInt32(args[missionIndex + 1], 16));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        double previous = 0;
        ownedLoader.Progress = _ =>
        {
            if (watch.Elapsed.TotalSeconds - previous < 10) return;
            previous = watch.Elapsed.TotalSeconds;
            Console.WriteLine($"WORLD_LOAD seconds={previous:F1}");
        };
        var loaded = ownedLoader.LoadMission(candidate.Tag, mission.Tag, new() { IncludeLowerLods = false });
        CubemapChecks.Owned(loaded.Scene);
        // Every decoded part carries its material's +0x20 word, and the inspector shows it.
        int stateParts = 0, overridden = 0;
        foreach (var part in loaded.Scene.Instances.Where(x => x.Mesh.Native is not null).DistinctBy(x => x.Mesh.Native).Take(400))
        {
            var native = part.Mesh.Native!;
            uint expected = owned.TryGetEntry(native.MaterialTag, out var material) && material.Reference == 0x808071E8
                ? BinaryPrimitives.ReadUInt32LittleEndian(owned.Read(native.MaterialTag).AsSpan(0x20)) : 0;
            Check(native.MaterialStateOverrides == expected, $"Part {native.MaterialTag:X8} carries 0x{native.MaterialStateOverrides:X8}, not 0x{expected:X8}.");
            Check(loaded.Inspect(part)?.Properties.Any(p => p is { Group: "Renderer", Name: "Material states" }
                && p.Value == $"0x{expected:X8}") == true, "The inspector does not show the material state word.");
            stateParts++;
            if ((expected & 0x80808080) != 0) overridden++;
        }
        Check(stateParts > 0, "No owned part has native data.");
        Console.WriteLine($"PASS owned-material-states parts={stateParts} with-overrides={overridden} inspector=shown");
        var hierarchyWatch = System.Diagnostics.Stopwatch.StartNew();
        HierarchyChecks.Mission(loaded);
        Console.WriteLine($"HIERARCHY_CHECK elapsed_ms={hierarchyWatch.Elapsed.TotalMilliseconds:F1}");
        Check(loaded.AllStates.Count > 0 && loaded.Scene.Instances.Count + loaded.Scene.Markers.Count > 0, "Owned world load produced no selectable data.");
        Check(loaded.Objects.Any(x => x.Kind == WorldObjectKind.State), "Owned mission states are missing.");
        var editable = loaded.Scene.Instances.FirstOrDefault(x => x.InstanceSourceTag != 0 && SceneEdits.Describe(owned, x, loaded).CanTransform);
        if (editable is not null)
        {
            var info = SceneEdits.Describe(owned, editable, loaded);
            Vector3 shifted = info.Position + Vector3.UnitX;
            var shift = SceneEdits.CreateTransformEdit(owned, editable, shifted, null, info.Scale, loaded)!;
            var replacement = ObjectEditCompiler.Default.Compile([shift], owned.ReadOriginal, owned).Assets;
            owned.ReadOverride = tag => replacement.TryGetValue(tag, out var bytes) ? bytes : null;
            var isolated = new AssetSceneLoader(owned).Load(editable.InstanceSourceTag);
            var movedInstance = isolated.Instances.First(x => x.InstanceSourceIndex == editable.InstanceSourceIndex);
            Check(Vector3.Distance(movedInstance.Transform.Translation, shifted) < 0.001f,
                "Owned instance edit did not reach native placement decoding.");
            owned.ReadOverride = null;
        }
        Console.WriteLine($"PASS owned-world map={candidate.Tag:X8} mission={mission.Tag:X8} instances={loaded.Scene.Instances.Count} markers={loaded.Scene.Markers.Count} states={loaded.AllStates.Count}");
        ownedLoader.Progress = null;
        PlacementUpdateChecks.Owned(owned, ownedLoader, candidate.Tag, mission.Tag, temporary);
        owned.ReadOverride = null;
    }
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
finally
{
    if (Directory.Exists(temporary) && Path.GetFullPath(temporary).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(temporary, true);
}
return 0;
static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
static void Throws<T>(Action action) where T : Exception
{ try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
static void AssertChangedOnly(byte[] before, byte[] after, int start, int length)
{
    Check(before.Length == after.Length, "Placement edit resized its source.");
    for (int i = 0; i < before.Length; i++)
        if (i < start || i >= start + length) Check(before[i] == after[i], "Placement edit changed an unrelated byte.");
}

internal sealed record SelectionProxy(object Key) : ISceneSelectionProxy;
