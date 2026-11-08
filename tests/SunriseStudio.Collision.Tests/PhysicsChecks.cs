using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;
using SunriseStudio.Tags.Generated;
using static Checks;

/** Sphere and capsule triangles, entity physics on bones, and trigger rows that draw one shape. */
internal static class PhysicsChecks
{
    private const uint LeviathanMap = 0x80EDC19B, EntityClass = 0x80809C0F, ConfigClass = 0x80809C36;
    // The physics packfile whose bone-0 capsules run from z 0.85 to 1.65 with radius 0.55.
    private const uint StandInPackfile = 0x80C1B1B0;

    internal static void Synthetic()
    {
        var a = new Vector3(0, 0, .85f); var b = new Vector3(0, 0, 1.65f);
        var capsule = CollisionShapes.CapsuleShape(a, b, .55f);
        Closed(capsule, "capsule");
        float capsuleError = capsule.Vertices.Max(v => MathF.Abs(SegmentDistance(v, a, b) - .55f));
        Check(capsuleError < 1e-5f && capsule.BoundsMin == new Vector3(-.55f, -.55f, .3f) && capsule.BoundsMax == new Vector3(.55f, .55f, 2.2f),
            $"The capsule is off its surface by {capsuleError} or has the wrong bounds.");
        var center = new Vector3(1, 2, 3);
        var sphere = CollisionShapes.SphereShape(center, 2);
        Closed(sphere, "sphere");
        float sphereError = sphere.Vertices.Max(v => MathF.Abs(Vector3.Distance(v, center) - 2));
        Check(sphere.Class == CollisionShapes.Sphere && sphere.Undecoded is null && sphereError < 1e-5f, $"The sphere is off its surface by {sphereError}.");
        var box = CollisionShapes.BoxShape(new(1, 2, 3));
        Closed(box, "box");
        InlineShapes();
        Console.WriteLine($"PASS collision-shapes capsule=[vertices={capsule.Vertices.Length} triangles={capsule.TriangleCount} closed=yes "
            + $"surface-error={capsuleError:0.#######}] sphere=[triangles={sphere.TriangleCount} closed=yes surface-error={sphereError:0.#######}] box=closed");
    }

    internal static void Owned(PackageCatalog catalog)
    {
        Entity(catalog);
        Triggers(catalog);
        InlineEntities(catalog);
    }

    /** Native axis selectors distinguish world Z, local X and local Z. */
    private static void InlineShapes()
    {
        byte[] variant = new byte[32]; variant[0] = 3;
        BitConverter.GetBytes(2f).CopyTo(variant, 4);
        BitConverter.GetBytes(8f).CopyTo(variant, 8);
        BitConverter.GetBytes(.25f).CopyTo(variant, 12);
        var owner = Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(12, 4, -3);
        for (byte axis = 0; axis <= 2; axis++)
        {
            variant[16] = axis;
            var cylinder = InlineTriggerShape.Decode(variant, 0);
            Closed(cylinder.Shape, "inline cylinder");
            var expectedMin = new Vector3(-2, -2, -2);
            var expectedMax = axis == 1 ? new Vector3(6, 2, 2) : new Vector3(2, 2, 6);
            Check(cylinder.Shape.BoundsMin == expectedMin && cylinder.Shape.BoundsMax == expectedMax
                && cylinder.WorldAligned == (axis == 0), "The inline cylinder axis or flat-cap bounds differ.");
            Check(cylinder.WorldTransform(owner) == (axis == 0 ? Matrix4x4.CreateTranslation(owner.Translation) : owner),
                "The inline cylinder retained or discarded the wrong rotation.");
        }
        variant[0] = 0;
        BitConverter.GetBytes(2f).CopyTo(variant, 16); BitConverter.GetBytes(4f).CopyTo(variant, 20); BitConverter.GetBytes(6f).CopyTo(variant, 24);
        Check(InlineTriggerShape.Decode(variant, 0).WorldTransform(owner) == Matrix4x4.CreateTranslation(owner.Translation),
            "A world-aligned box retained the owner's rotation.");
        variant[0] = 1;
        Check(InlineTriggerShape.Decode(variant, 0).WorldTransform(owner) == owner, "An oriented box lost its rotation.");
        Console.WriteLine("PASS collision-inline-shapes cylinder=closed caps=flat split=offset axes=worldZ,localX,localZ box=world-or-local");
    }

    /** The cylinder comes from the embedded config resource, without a placement payload variant. */
    private static void InlineEntities(PackageCatalog catalog)
    {
        const uint localEntity = 0x80C0094A, worldEntity = 0x80C599F4, sourceList = 0x80C42237;
        var local = new CollisionSceneLoader(catalog).Load(new SceneDocument { TagHash = localEntity });
        var placed = local.Placements.Single();
        var shape = local.Pieces[placed.Piece].Shape;
        Check(shape.Class == CollisionShapes.Cylinder && shape.TriangleCount > 0 && shape.Undecoded is null
            && Vector3.Distance(shape.BoundsMin, new(-4, -4, -1)) < 1e-5f
            && Vector3.Distance(shape.BoundsMax, new(4, 4, 2)) < 1e-5f
            && local.ReadTags.Contains(0x80C00948), "The owned inline cylinder's config dimensions were not used.");
        var owner = Matrix4x4.CreateScale(3) * Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(40, 20, 10);
        var scene = new SceneDocument();
        foreach (uint tag in new[] { localEntity, worldEntity })
            scene.Markers.Add(new SceneMarker { TagHash = tag, SourceTag = sourceList, SourceOffset = TagHashes.Entry(tag),
                ObjectPlacement = new SceneObjectPlacement(sourceList, TagHashes.Entry(tag), owner) });
        var all = new CollisionSceneLoader(catalog).Load(scene);
        var localPlaced = all.Placements.Single(p => all.Entities[p.Entity].Tag == localEntity);
        var worldPlaced = all.Placements.Single(p => all.Entities[p.Entity].Tag == worldEntity);
        Check(Vector3.Distance(Vector3.TransformNormal(Vector3.UnitZ, localPlaced.World), Vector3.UnitX) < 1e-5f
            && worldPlaced.World == Matrix4x4.CreateTranslation(owner.Translation),
            "Placed inline cylinders used placement scale or the wrong rotation policy.");
        var rows = (Dictionary<uint, PlacedPayload[]>)typeof(CollisionScene).GetProperty("ListRows",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(all)!;
        rows[sourceList] = StaticCollisionReader.PlacedPayloads(catalog.Read(sourceList), sourceList)
            .Where(row => row.Marker is SStaticCollisionPayload.Class or TriggerShapeReference.Class).ToArray();
        Check(CollisionSceneUpdate.Apply(catalog, all, [sourceList], out var reason) is null && reason.Contains("inline trigger"),
            "An inline class placement stayed stale when its list changed without a collision payload change.");
        Console.WriteLine("PASS collision-inline-owned config=80C00948 entity=80C0094A radius=4 length=3 split=1/3 placed-scale=ignored world-axis=80C599F4");
    }

    /** The NPC entity whose physics packfile is the stand-in capsules: bodies on bones, hitboxes with regions. */
    private static void Entity(PackageCatalog catalog)
    {
        var configs = catalog.Entries.Where(e => e.Reference == ConfigClass && e.PackageName.StartsWith("w64_npcs", StringComparison.Ordinal))
            .Where(e => PhysicsPackfile(catalog.Read(e.TagHash)) == StandInPackfile).Select(e => e.TagHash).ToHashSet();
        Check(configs.Count > 0, $"No w64_npcs physics config names packfile {StandInPackfile:X8}.");
        uint entityTag = catalog.Entries.Where(e => e.Reference == EntityClass)
            .OrderBy(e => e.PackageName.StartsWith("w64_npcs", StringComparison.Ordinal) ? 0 : 1).Select(e => e.TagHash)
            .First(tag => Configs(catalog.Read(tag)).Any(configs.Contains));
        var scene = new CollisionSceneLoader(catalog).Load(new SceneDocument { TagHash = entityTag });
        var entity = scene.Entities.Single(e => e.Source == CollisionSource.EntityPhysics);
        var placements = scene.Placements.Where(p => p.Entity == entity.Index).ToArray();
        int hitboxes = placements.Count(p => p.Role == CollisionRole.Hitbox), world = placements.Count(p => p.Role == CollisionRole.World);
        var regions = placements.Where(p => p.Role == CollisionRole.Hitbox).Select(p => CollisionRegions.Name(p.Region))
            .Where(name => name.Length > 0).Distinct().Order().ToArray();
        Check(placements.Length > 0 && hitboxes > 0 && regions.Any(name => !name.StartsWith("0x", StringComparison.Ordinal)),
            $"Entity {entityTag:X8} has {placements.Length} bodies, {hitboxes} hitboxes, regions [{string.Join(",", regions)}].");
        Check(placements.All(p => float.IsFinite(p.World.M41 + p.World.M42 + p.World.M43)), "A physics body has no finite world transform.");
        // Bone 0 of a stand-in sits at the origin, so its capsules keep their authored span.
        var standIn = placements.Where(p => p.Bone == 0 && scene.Pieces[p.Piece].Shape.Class == CollisionShapes.Capsule).ToArray();
        Check(standIn.Any(p => MathF.Abs(p.BoundsMin.Z - .3f) < 1e-2f && MathF.Abs(p.BoundsMax.Z - 2.2f) < 1e-2f),
            "No bone-0 capsule spans z 0.3 to 2.2.");
        var bones = placements.Select(p => p.Bone).Where(bone => bone >= 0).Distinct().Count();
        Console.WriteLine($"PASS collision-entity-physics entity={entityTag:X8} packfile={StandInPackfile:X8} bodies={placements.Length} "
            + $"world={world} hitboxes={hitboxes} trigger={placements.Count(p => p.Role == CollisionRole.Trigger)} "
            + $"other={placements.Count(p => p.Role == CollisionRole.Other)} bones={bones} regions=[{string.Join(",", regions)}] "
            + $"stand-in-capsule=z0.3..2.2 notes={scene.Notes.Count}");
    }

    /** Each Leviathan trigger row draws one shape of its array, and rows of one array draw different shapes. */
    private static void Triggers(PackageCatalog catalog)
    {
        var lists = new StaticCollisionReader(catalog).MapObjectLists(LeviathanMap);
        var scene = new CollisionSceneLoader(catalog).LoadObjectLists(lists, []);
        var rows = scene.Entities.Where(e => e.Source == CollisionSource.ShapeArray).ToArray();
        Check(rows.Length > 0, "Leviathan has no trigger rows.");
        var shapes = new Dictionary<uint, HashSet<int>>();
        int placed = 0;
        foreach (var row in rows)
        {
            var own = scene.Placements.Skip(row.FirstPlacement).Take(row.PlacementCount).ToArray();
            Check(own.All(p => p.Role == CollisionRole.Trigger), "A trigger row placed a shape that is not a trigger.");
            var offsets = own.Select(p => p.Instance >= 0 ? p.CompoundOffset : scene.Pieces[p.Piece].Offset).Distinct().ToArray();
            Check(offsets.Length <= 1, $"Trigger row {row.ListTag:X8}+{row.ListRow:X} draws {offsets.Length} shapes.");
            if (offsets.Length == 1 && !(shapes.TryGetValue(row.Tag, out var used) ? used : shapes[row.Tag] = []).Add(offsets[0]))
                Check(false, $"Two trigger rows of array {row.Tag:X8} draw the same shape.");
            placed += own.Length;
        }
        Console.WriteLine($"PASS collision-trigger-rows map={LeviathanMap:X8} rows={rows.Length} arrays={shapes.Count} placements={placed} "
            + "one-shape-per-row=yes distinct-per-array=yes role=trigger");
    }

    /** Config +0x18 payload class 0x80808A0C holds the physics packfile at +0x380; 0 for other configs. */
    private static uint PhysicsPackfile(byte[] config)
    {
        if (config.Length < 0x20) return 0;
        long relative = BitConverter.ToInt64(config, 0x18);
        long payload = 0x18 + relative;
        return relative != 0 && payload >= 4 && payload + 0x384 <= config.Length && BitConverter.ToUInt32(config, (int)payload - 4) == 0x80808A0C
            ? BitConverter.ToUInt32(config, (int)payload + 0x380) : 0;
    }

    /** Config tags of a class definition's component rows; none when the array does not read. */
    private static IEnumerable<uint> Configs(byte[] definition)
    {
        try
        {
            return TagRows.Offsets<SEntity, SPatternComponent>(definition, nameof(SEntity.Components), SPatternComponent.Class)
                .Select(row => ObjectRows.Field<SPatternComponent, uint>(definition, row,
                    nameof(SPatternComponent.Config))).ToArray();
        }
        catch (InvalidDataException) { return []; }
    }

    private static void Closed(CollisionShape shape, string name)
    {
        var edges = new Dictionary<(int, int), int>();
        for (int i = 0; i < shape.Indices.Length; i += 3)
            for (int k = 0; k < 3; k++)
            {
                int x = shape.Indices[i + k], y = shape.Indices[i + (k + 1) % 3];
                var key = (Math.Min(x, y), Math.Max(x, y));
                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
        Check(shape.Indices.Length > 0 && edges.Values.All(count => count == 2), $"The {name} mesh is not closed.");
    }

    private static float SegmentDistance(Vector3 point, Vector3 a, Vector3 b)
    {
        var line = b - a;
        float t = Math.Clamp(Vector3.Dot(point - a, line) / line.LengthSquared(), 0, 1);
        return Vector3.Distance(point, a + t * line);
    }
}
