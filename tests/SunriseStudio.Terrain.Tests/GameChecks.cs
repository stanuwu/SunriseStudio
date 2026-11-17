using System.Text.Json.Nodes;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using SunriseStudio.World;

/** A ground placed, sculpted and removed in a destination copied from the installation; nothing is written to it. */
internal static class GameChecks
{
    private const string Destination = "groundbed", Ground = "hill";
    // Trostland in the EDZ.
    private const uint Kept = 0xB8459D59;

    /** @param keySource An executable holding the package keys, when Sunrise has not exported them. */
    internal static void Run(string gameRoot, string? keySource, string temporary)
    {
        using var catalog = PackageCatalog.Open(gameRoot, keySource);
        var schema = StudioRegistry.Index;
        var project = StudioProject.Create(Path.Combine(temporary, "project"), "Ground project");
        uint scenario = catalog.Names.Single(x => x.Value == "edz_freeroam:scenario_client" && catalog.TryGetEntry(x.Key, out var e)
            && e.Reference == SScenario.Class).Key;
        WorldTemplate.Create(project.ContentDirectory, catalog, schema, scenario, new HashSet<uint> { Kept }, Destination);
        var world = WorldFiles.Load(project.ContentDirectory, Destination);
        uint bubble = WorldContent.FirstLiveBubble(world);
        var spawn = WorldContent.Spawns(world, bubble).First().Position;
        ushort package = Enumerable.Range(0, 0x10000).Select(x => (ushort)x).First(x => PackageWriter.IsNewPackageId(x) && !catalog.ContainsInstalledPackage(x));
        var grid = GroundChecks.Synthetic() with { Position = [Math.Round(spawn.X), Math.Round(spawn.Y), Math.Round(spawn.Z) - 2] };
        var material = Material(catalog);

        var (definition, additions, build) = GroundAssets.PrepareNew(project, catalog, schema, world, Destination, bubble, [], Ground, grid, material, package);
        GroundPlacement.Place(world, definition.Site, definition.Name, build.Terrains, build.CollisionOwner, build.Rows, build.Buffer);
        project.AddAssets(additions, catalog);
        world.SaveChanged(project.ContentDirectory);
        var grounds = GroundAssets.Grounds(project.AddedAssets, a => project.ReadAddedAsset(a.Id, catalog));
        GroundChecks.Check(grounds.Count == 1 && grounds[0].Native.Count == build.Entries.Count && grounds[0].Definition == definition with
            { Position = grounds[0].Definition.Position } && grounds[0].Heights.SequenceEqual(grid.Heights), "The saved ground differs from the prepared one.");
        Throws(() => GroundAssets.PrepareNew(project, catalog, schema, WorldFiles.Load(project.ContentDirectory, Destination), Destination, bubble,
            grounds, "again", grid, material, package), "A second ground took the same AO map.");
        Throws(() => GroundAssets.PrepareNew(project, catalog, schema, WorldFiles.Load(project.ContentDirectory, Destination), Destination, bubble,
            [], Ground, grid, material, package), "A ground name was taken twice.");

        var tags = Overlay(project, catalog, schema);
        uint parent = tags.Single(t => t.ClassId == SBubbleParent.Class && WorldFiles.Hash(WorldFiles.Field((JsonObject)WorldFiles.Field(
            world.Fields(t.Name), "metadata"), "bubble_name_hash")) == bubble).TagHash;
        var terrains = build.Terrains.Select(t => t.Terrain).ToHashSet();
        var scene = new AssetSceneLoader(catalog).LoadBubble(parent);
        int groups = terrains.Sum(t => new AssetSceneLoader(catalog).LoadMesh(t).Instances.Count);
        int drawn = scene.Instances.Count(i => i.Layer == SceneLayer.Terrain && terrains.Contains(i.Mesh.TagHash));
        GroundChecks.Check(groups > 0 && drawn == groups, $"The bubble draws {drawn} of the ground's {groups} terrain groups.");
        uint list = tags.Single(t => t.Name == GroundPlacement.CollisionList(Destination, Ground)).TagHash;
        var collision = new StaticCollisionReader(catalog).ReadObjectLists([list]);
        GroundChecks.Check(collision.Undecoded.Count == 0 && collision.Entities.Single().Instances.Single().Shape is { Indices.Length: > 0 },
            "The ground's collision did not decode.");
        uint map = tags.Single(t => t.Name == definition.Site.OcclusionMap).TagHash;
        byte[] mapData = catalog.Read(map);
        ref readonly var view = ref TagView.Checked<SOcclusionMap>(mapData, 0);
        var keys = TagView.Rows(mapData, in view.Offsets, UnknownOcclusionMapInstance.Class).ToArray().Select(r => r.InstanceKey).ToHashSet();
        GroundChecks.Check(view.ScalarBufferHeader == build.Buffer && build.Rows.All(r => keys.Contains(r.Key)), "The AO map lost the ground's rows.");
        Console.WriteLine($"PASS ground game bubble=0x{bubble:X8} patches={build.Terrains.Count} groups={groups} entries={build.Entries.Count} "
            + $"first_index={definition.Site.FirstIndex} map={definition.Site.OcclusionMap.Split('/')[^1]} collision=decoded");

        var sculpted = GroundBrush.Apply(grid, new(GroundBrushMode.Raise, [(grid.Position[0], grid.Position[1])], 30, 4));
        var edits = GroundAssets.Rebuild(catalog, world, grounds[0], sculpted, material);
        project.RestoreTransaction(new(new Dictionary<uint, byte[]>(), edits), new Dictionary<string, AddedAssetSnapshot?>(), catalog);
        var rebuilt = GroundAssets.Grounds(project.AddedAssets, a => project.ReadAddedAsset(a.Id, catalog)).Single();
        GroundChecks.Check(rebuilt.Heights.SequenceEqual(sculpted)
            && rebuilt.Native.Select(a => (a.Id, a.Native!.EntryIndex)).SequenceEqual(grounds[0].Native.Select(a => (a.Id, a.Native!.EntryIndex))),
            "A sculpt did not rewrite the same entries.");

        var removed = WorldFiles.Load(project.ContentDirectory, Destination);
        foreach (string file in GroundPlacement.Remove(removed, definition.Site, Ground, GroundAssets.Keys(rebuilt).ToHashSet()))
            File.Delete(WorldFiles.PathOf(project.ContentDirectory, file));
        removed.SaveChanged(project.ContentDirectory);
        string[] ids = [rebuilt.DefinitionAsset.Id, rebuilt.HeightsAsset.Id, .. rebuilt.Native.Select(a => a.Id)];
        project.RestoreTransaction(new(new Dictionary<uint, byte[]>(), new Dictionary<string, byte[]>()),
            ids.ToDictionary(id => id, _ => (AddedAssetSnapshot?)null), catalog);
        var after = Overlay(project, catalog, schema);
        byte[] restored = catalog.Read(after.Single(t => t.Name == definition.Site.OcclusionMap).TagHash);
        ref readonly var again = ref TagView.Checked<SOcclusionMap>(restored, 0);
        GroundChecks.Check(project.AddedAssets.Count == 0 && again.ScalarBufferHeader == definition.Site.OriginalBuffer
            && !TagView.Rows(restored, in again.Offsets, UnknownOcclusionMapInstance.Class).ToArray().Any(r => build.Rows.Any(b => b.Key == r.InstanceKey))
            && after.All(t => t.Name != GroundPlacement.TerrainList(Destination, Ground)), "Removing the ground left assets, lists or AO rows.");
        Console.WriteLine("PASS ground edit sculpt=in-place remove=clean");
    }

    /** The material of the first installed STerrain. */
    private static GroundMaterial Material(PackageCatalog catalog)
    {
        var entry = catalog.Entries.First(e => e.Reference == STerrain.Class);
        byte[] data = catalog.Read(entry.TagHash);
        ref readonly var terrain = ref TagView.Checked<STerrain>(data, 0);
        return new(TagView.Rows(data, in terrain.Parts, UnknownTerrainPart.Class)[0].Material,
            TagView.Rows(data, in terrain.Groups, UnknownTerrainGroup.Class)[0].DyeTexture);
    }

    /** Compiles the project's content and shows it and the added assets through the catalog, as an open project does. */
    private static IReadOnlyList<SunriseStudio.Tags.Tft.TftCompiledTag> Overlay(StudioProject project, PackageCatalog catalog,
        SunriseStudio.Packages.Registry.RegistryIndex schema)
    {
        catalog.ReadOverride = null;
        catalog.SetOverlayEntries([]);
        var reserved = project.AddedAssets.Where(a => a.Native is not null).Select(a => a.Native!.PackageId).ToArray();
        var tags = ProjectBuilder.CompileContent(project, catalog, schema, reserved);
        var data = tags.ToDictionary(t => t.TagHash, t => t.Data);
        var rows = tags.Select(tag =>
        {
            var (id, index) = TagHashes.Split(tag.TagHash);
            return new PackageOverlayEntry(new PackageEntry(tag.TagHash, tag.ClassId, (int)(tag.TypeInfo >> 9 & 127), (int)(tag.TypeInfo >> 6 & 7),
                tag.Data.Length, $"w64_sunrise_{id:x4}_0", id, index) { TypeInfo = tag.TypeInfo }, tag.Name)
            { WideHashes = tag.WideHash == 0 ? WideHashAliases.Empty : new([tag.WideHash]) };
        }).ToList();
        foreach (var asset in project.AddedAssets.Where(a => a.Native is not null))
        {
            var native = asset.Native!;
            uint tag = TagHashes.Make(native.PackageId, native.EntryIndex!.Value);
            data[tag] = project.ReadAddedAsset(asset.Id, catalog);
            rows.Add(new(new PackageEntry(tag, native.Reference, (int)(native.TypeInfo >> 9 & 127), (int)(native.TypeInfo >> 6 & 7),
                data[tag].Length, $"w64_sunrise_{native.PackageId:x4}_0", native.PackageId, native.EntryIndex.Value) { TypeInfo = native.TypeInfo }, asset.Name));
        }
        catalog.SetOverlayEntries(rows);
        catalog.ReadOverride = tag => data.GetValueOrDefault(tag);
        return tags;
    }

    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { return; }
        throw new InvalidDataException(message);
    }
}
