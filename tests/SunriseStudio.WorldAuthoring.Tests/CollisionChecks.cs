using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene.Collision;
using SunriseStudio.Tags.Tft;
using SunriseStudio.World;

/** The bundle bubble's statics get one new static collision entity in the bubble they land in. */
static class CollisionChecks
{
    private const string Name = "testbed_bubble_a_collision";
    // Compressed mesh vertices land within a few millimetres; snorm16 render vertices within one.
    private const float BoundsTolerance = 0.02f;

    /** The owner, its compound, one shape per static and one for the grid's walls, and a list joined to the bubble. */
    public static uint Placed(StudioProject project, PackageCatalog catalog, uint bubble, int statics)
    {
        int shapes = statics + 1;
        byte[] Read(string suffix) => project.ReadAddedAsset(project.AddedAssets.Single(asset => asset.Name == Name + suffix).Id, catalog);
        var ownerAsset = project.AddedAssets.Single(asset => asset.Name == Name + ".owner");
        uint owner = TagHashes.Make(ownerAsset.Native!.PackageId, ownerAsset.Native.EntryIndex!.Value);
        var rows = CollisionOwnerRows.Read(Read(".owner"));
        var compound = StaticCompoundShape.Parse(HavokPackfile.Parse(Read(".compound.hkx")));
        WorldBuild.Check(rows.Shapes.Count == shapes && rows.RunStarts.Count == shapes && rows.Compound == owner + 1
            && rows.Shapes.SequenceEqual(Enumerable.Range(0, shapes).Select(i => owner + 2 + (uint)i)) && rows.Materials.Count > 0
            && compound.Instances.Count == shapes && compound.Instances.Select(x => x.ChildImport).SequenceEqual(Enumerable.Range(0, shapes).Select(i => $"i{i}")),
            "The new collision owner does not chain one compound, one shape per static and the walls.");
        var world = WorldFiles.Load(project.ContentDirectory, "testbed");
        string list = $"{WorldTemplate.Folder}/testbed/map_placements/{Name}.map_placements";
        WorldBuild.Check(WorldContent.Containers(world, bubble).Any(container =>
                ((System.Text.Json.Nodes.JsonArray)world.Fields(container)["contents"]!).Any(member => WorldFiles.Text(member!["member_tag"]) == list)),
            "The collision list does not join the bubble.");
        Console.WriteLine($"world_collision owner=0x{owner:X8} shapes={rows.Shapes.Count} materials={rows.Materials.Count} "
            + $"key_bits={compound.File.U8(compound.File.RootOffset + 0x30)}");
        return owner;
    }

    /**
     * The loaded map holds the entity, one instance per static, and the plate's instance covers the box
     * the plate draws in.
     */
    public static void Loaded(PackageCatalog catalog, SunriseStudio.Scene.WorldDocument loaded, IReadOnlyList<TftCompiledTag> tags,
        uint owner, IReadOnlyList<PlacedBundleStatic> statics)
    {
        var lists = loaded.MapScene.ReadTags.Where(tag => catalog.TryGetEntry(tag, out var entry) && entry.Reference == StaticCollisionReader.ObjectListClass).ToArray();
        var scene = new CollisionSceneLoader(catalog).LoadObjectLists(lists, []);
        var entity = scene.Entities.SingleOrDefault(x => x.Tag == owner);
        WorldBuild.Check(entity is not null, "The loaded map holds no collision entity with the new owner.");
        var placements = scene.Placements.Where(p => p.Entity == entity!.Index).OrderBy(p => p.Instance).ToList();
        // Every chunk of one static draws from its own table; the first static's chunks hold the plate.
        var plate = statics.First();
        uint table = tags.Single(x => x.Name == plate.Files[1]).TagHash;
        var drawn = loaded.MapScene.Instances.Where(x => x.InstanceSourceTag == table && x.Mesh.Native?.RenderStage == 0).ToList();
        var points = drawn.SelectMany(x => x.Mesh.Vertices.Select(v => Vector3.Transform(v.Position, x.Transform))).ToArray();
        var (min, max) = (points.Aggregate(Vector3.Min), points.Aggregate(Vector3.Max));
        var first = placements[0];
        float error = Math.Max((first.BoundsMin - min).Length(), (first.BoundsMax - max).Length());
        WorldBuild.Check(placements.Count == statics.Count + 1 && error < BoundsTolerance,
            $"The collision has {placements.Count} instances; the plate's box is {first.BoundsMin}..{first.BoundsMax}, drawn {min}..{max}.");
        // The fixture wall: cells X -3 to -2, Y -6 to 6, 3 m high, in the plate's frame.
        var corners = new[] { -3f, -2f }.SelectMany(x => new[] { -6f, 6f }.SelectMany(y => new[] { 0f, 3f }.Select(z =>
            Vector3.Transform(new Vector3(x, y, z), plate.Rotation) + plate.Position))).ToArray();
        var walls = placements[^1];
        float wallError = Math.Max((walls.BoundsMin - corners.Aggregate(Vector3.Min)).Length(), (walls.BoundsMax - corners.Aggregate(Vector3.Max)).Length());
        WorldBuild.Check(wallError < BoundsTolerance, $"The wall collision box is {walls.BoundsMin}..{walls.BoundsMax}, not the grid's wall.");
        Console.WriteLine($"world_collision_loaded instances={placements.Count} plate_box_error_m={error:F4} wall_box_error_m={wallError:F4}");
    }

    /** The build carries the owner, the compound and every shape as new entries. */
    public static void Built(ProjectBuildResult build, uint owner, int statics)
    {
        var built = build.Contents.NewEntries.Select(x => TagHashes.Make(x.PackageId, x.EntryIndex)).ToHashSet();
        WorldBuild.Check(Enumerable.Range(0, 3 + statics).All(i => built.Contains(owner + (uint)i)), "The build does not carry the collision owner, compound and shapes.");
        Console.WriteLine($"world_collision_build entries={3 + statics}");
    }
}
