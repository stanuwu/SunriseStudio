using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;
using static Checks;

/** Collision layer loading and collision imports, compared with the rule and the shape edit helpers. */
internal static class ImportChecks
{
    private const uint LeviathanMap = 0x80EDC19B, LeviathanCompound = 0x80EDC2D0;

    internal static void Run(PackageCatalog catalog)
    {
        var baseline = new CollisionSceneLoader(catalog).Load(new SceneDocument { TagHash = LeviathanCompound });
        int missing = baseline.Undecoded.GetValueOrDefault("child packfile missing");
        Check(baseline.Entities.Count == 1 && baseline.Placements.Count + missing == 5058
            && baseline.Placements.All(p => p.CompoundTag == LeviathanCompound),
            $"The compound asset loads {baseline.Placements.Count} placements and {missing} missing children.");
        Console.WriteLine($"PASS collision-layer-asset compound={LeviathanCompound:X8} placements={baseline.Placements.Count} "
            + $"pieces={baseline.Pieces.Count} triangles={baseline.PlacedTriangles} undecoded=[{Undecoded(baseline)}]");
        MapLayer(catalog);
        Unchanged(catalog, baseline);
        MovedInstance(catalog, baseline);
        MovedVertices(catalog, baseline);
        NewTopology(catalog, baseline);
        Refusals(catalog, baseline);
    }

    /** The map layer sees what the static reader sees, plus the shape array rows. */
    private static void MapLayer(PackageCatalog catalog)
    {
        var reader = new StaticCollisionReader(catalog);
        var lists = reader.MapObjectLists(LeviathanMap);
        var set = reader.ReadObjectLists(lists);
        var scene = new CollisionSceneLoader(catalog).LoadObjectLists(lists, []);
        int staticPlacements = scene.Placements.Count(p => scene.Entities[p.Entity].Source == CollisionSource.StaticEntity);
        int expected = set.Entities.Sum(entity => entity.Instances.Count(instance => instance.Shape is not null));
        Check(staticPlacements == expected, $"The layer places {staticPlacements} static shapes; the reader has {expected}.");
        long placed = set.Entities.SelectMany(entity => entity.Instances).Sum(instance => (long)(instance.Shape?.TriangleCount ?? 0));
        long layer = scene.Placements.Where(p => scene.Entities[p.Entity].Source == CollisionSource.StaticEntity)
            .Sum(p => (long)scene.Pieces[p.Piece].Shape.TriangleCount);
        Check(placed == layer, "The layer's static triangle count differs from the reader's.");
        var arrays = scene.Entities.Where(entity => entity.Source == CollisionSource.ShapeArray).ToArray();
        Console.WriteLine($"PASS collision-layer-map map={LeviathanMap:X8} entities={scene.Entities.Count} static={staticPlacements} "
            + $"shape-array-rows={arrays.Length} shape-array-shapes={arrays.Sum(entity => entity.PlacementCount)} "
            + $"placed-triangles={scene.PlacedTriangles} distinct-triangles={scene.DistinctTriangles} undecoded=[{Undecoded(scene)}]");
    }

    private static CollisionImportInput Input(CollisionScene scene, Func<CollisionPlacement, Matrix4x4>? world = null,
        Func<CollisionPiece, CollisionShapeInput?>? shape = null) => new(
        scene.Placements.Select(p => new CollisionPlacementInput(p.CompoundTag, p.Instance, scene.Pieces[p.Piece].Tag,
            scene.Pieces[p.Piece].Offset, world?.Invoke(p) ?? p.World)).ToArray(),
        scene.Pieces.Select(piece => shape?.Invoke(piece) ?? new CollisionShapeInput(piece.Tag, piece.Offset, piece.Shape.Vertices, piece.Shape.Indices)).ToArray(),
        new Dictionary<uint, string>());

    private static void Unchanged(PackageCatalog catalog, CollisionScene baseline)
    {
        var plan = CollisionImport.Plan(catalog, baseline, Input(baseline), catalog.ReadOriginal);
        Check(plan.ObjectEdits.Count == 0 && plan.Assets.Count == 0, "An unchanged import produced edits.");
        Console.WriteLine("PASS collision-import-unchanged edits=0 assets=0");
    }

    /**
     * A static instance edit moves one compound instance through the rule. The same transform as a
     * direct collision edit must give the same compound bytes, and the static edit wins over it.
     */
    private static void MovedInstance(PackageCatalog catalog, CollisionScene baseline)
    {
        var context = new ObjectEditContext(catalog, catalog.ReadOriginal);
        ushort package = (ushort)TagHashes.Package(LeviathanCompound);
        var index = new StaticCollisionRule.CollisionIndex(context, package);
        ObjectEdit? staticEdit = null;
        int instance = -1;
        foreach (uint table in catalog.Entries.Where(x => x.PackageId == package && x.Reference == 0x8080966D).Select(x => x.TagHash).Order())
        {
            var owners = index.TableOwners(table);
            if (owners.Count != 1) continue;
            byte[] bytes = catalog.ReadOriginal(table);
            var placement = TagRows.Placement(catalog.ReadOriginal(owners[0].ListTag), owners[0].Row);
            int[] rows = TagRows.StaticInstances(bytes);
            for (int i = 0; i < rows.Length && staticEdit is null; i++)
            {
                var row = TagRows.StaticInstance(bytes, rows[i]);
                IReadOnlyList<(StaticCollisionRule.Entity Entity, int Instance)> matches;
                try { matches = index.Find(StaticCollisionRule.Compose(placement, row)); }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException) { continue; }
                if (matches.Count != 1 || matches[0].Entity.Owner.CompoundTag != LeviathanCompound
                    || index.RowsCopying(matches[0].Entity, matches[0].Instance).Count != 1) continue;
                instance = matches[0].Instance;
                var rowTarget = new ObjectEditTarget(table, ObjectEditLayout.StaticInstance, rows[i], 48, i, 0, BinaryDelta.Hash(bytes.AsSpan(rows[i], 48)));
                var turn = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.4f) * row.Rotation);
                var moved = row.Translation + new Vector3(1.5f, -0.5f, 0.25f);
                staticEdit = new ObjectEdit(ObjectEditKind.PlacementTransform, rowTarget, "Static", [moved.X, moved.Y, moved.Z],
                    [turn.X, turn.Y, turn.Z, turn.W], 1.25f);
            }
            if (staticEdit is not null) break;
        }
        Check(staticEdit is not null, "No Leviathan row has its own instance in the compound.");
        byte[] byRule = ObjectEditCompiler.Default.Compile([staticEdit!], catalog.ReadOriginal, catalog).Assets[LeviathanCompound];
        var target = StaticCompoundShape.Parse(HavokPackfile.Parse(byRule)).Instances[instance].Transform;

        // The import sees the new world transform and plans one collision instance edit.
        var old = baseline.Placements.Single(p => p.Instance == instance);
        var plan = CollisionImport.Plan(catalog, baseline,
            Input(baseline, p => p.Instance == instance ? target.ToMatrix() * baseline.Entities[p.Entity].Frame : p.World), catalog.ReadOriginal);
        Check(plan.ObjectEdits.Count == 1 && plan.Assets.Count == 0, "A moved instance did not plan one object edit.");
        var edit = plan.ObjectEdits[0];
        Check(edit.Target.Layout == ObjectEditLayout.CollisionInstance && edit.Target.SourceTag == LeviathanCompound
            && edit.Target.Index == instance, "The planned edit targets another row.");
        Check(Vector3.Distance(ObjectEditCompiler.Vector(edit.Position!), target.Translation) < 1e-3f
            && MathF.Abs(edit.Scale!.Value - target.Scale.X) < 1e-4f, "The planned edit holds another transform.");
        byte[] byImport = ObjectEditCompiler.Default.Compile([edit], catalog.ReadOriginal, catalog).Assets[LeviathanCompound];
        var imported = StaticCompoundShape.Parse(HavokPackfile.Parse(byImport));
        var shape = baseline.Pieces[old.Piece].Shape;
        Check(imported.LeafBoxes()[instance].Contains(StaticCollisionRule.MovedBox(shape.BoundsMin, shape.BoundsMax,
            imported.Instances[instance].Transform), 1e-4), "The imported instance's leaf does not enclose its shape.");

        // The exact same values as a direct collision edit give the rule's bytes.
        var exact = edit with { Position = [target.Translation.X, target.Translation.Y, target.Translation.Z],
            Rotation = [target.Rotation.X, target.Rotation.Y, target.Rotation.Z, target.Rotation.W], Scale = target.Scale.X };
        byte[] direct = ObjectEditCompiler.Default.Compile([exact], catalog.ReadOriginal, catalog).Assets[LeviathanCompound];
        Check(direct.AsSpan().SequenceEqual(byRule), "A direct collision edit differs from the static instance rule's bytes.");

        // With both edits, the static instance edit wins and the collision edit writes nothing.
        var other = edit with { Position = [target.Translation.X + 5, target.Translation.Y, target.Translation.Z] };
        var both = ObjectEditCompiler.Default.Compile([staticEdit!, other], catalog.ReadOriginal, catalog);
        Check(both.Assets[LeviathanCompound].AsSpan().SequenceEqual(byRule) && both.Edits.Single(x => x.Edit.Key == other.Key).Tags.Count == 0,
            "A collision edit overrode the static instance edit of the same object.");
        Console.WriteLine($"PASS collision-import-instance compound={LeviathanCompound:X8} instance={instance} edits=1 layout=CollisionInstance "
            + $"bytes=rule size={byImport.Length}=={catalog.ReadOriginal(LeviathanCompound).Length} static-wins=yes");
    }

    /** Moved vertices keep the topology and the file size, and match the shape edit helpers byte for byte. */
    private static void MovedVertices(PackageCatalog catalog, CollisionScene baseline)
    {
        var mesh = baseline.Pieces.First(p => p.Shape.Class == CollisionShapes.CompressedMesh && p.Shape.CustomPrimitives == 0 && p.Shape.TriangleCount > 20);
        var convex = baseline.Pieces.First(p => p.Shape.Class == CollisionShapes.ConvexVertices && p.Offset == HavokPackfile.Parse(catalog.ReadOriginal(p.Tag)).RootOffset);
        // Every vertex moves up 2 cm, so the shape grows past its old box and the compound tree is refit.
        var meshPositions = mesh.Shape.Vertices.Select(v => v + new Vector3(0, 0, .02f)).ToArray();
        var convexPositions = convex.Shape.Vertices.Select((v, i) => i == 0 ? v * 1.01f : v).ToArray();
        var plan = CollisionImport.Plan(catalog, baseline, Input(baseline, shape: piece =>
            piece == mesh ? new(piece.Tag, piece.Offset, meshPositions, piece.Shape.Indices)
            : piece == convex ? new(piece.Tag, piece.Offset, convexPositions, piece.Shape.Indices) : null), catalog.ReadOriginal);
        Check(plan.ObjectEdits.Count == 0, "A shape edit planned object edits.");
        var meshFile = HavokPackfile.Parse(catalog.ReadOriginal(mesh.Tag));
        byte[] expectedMesh = CollisionPackfileWriter.WriteCompressedMesh(
            CollisionShapeEdits.MoveVertices(CompressedMesh.Read(meshFile, meshFile.RootOffset), meshPositions));
        Check(plan.Assets[mesh.Tag].AsSpan().SequenceEqual(expectedMesh), "The mesh edit differs from CollisionShapeEdits.MoveVertices.");
        Check(expectedMesh.Length == meshFile.Bytes.Length, "A vertex move changed the mesh file size.");
        byte[] expectedConvex = CollisionShapeEdits.MoveConvexVertices(HavokPackfile.Parse(catalog.ReadOriginal(convex.Tag)), convexPositions);
        Check(plan.Assets[convex.Tag].AsSpan().SequenceEqual(expectedConvex), "The convex edit differs from CollisionShapeEdits.MoveConvexVertices.");
        var decoded = CollisionShapes.Decode(HavokPackfile.Parse(plan.Assets[mesh.Tag]), meshFile.RootOffset);
        float error = decoded.Vertices.Zip(meshPositions).Max(pair => Vector3.Distance(pair.First, pair.Second));
        Check(decoded.Indices.SequenceEqual(mesh.Shape.Indices) && error < .01f, $"The moved mesh decodes {error} m away.");
        // The compound that places the grown mesh is refit, and each of its leaves encloses the grown shape.
        Check(plan.Assets.TryGetValue(LeviathanCompound, out var refit), "The compound was not refit around the grown mesh.");
        var compound = StaticCompoundShape.Parse(HavokPackfile.Parse(refit!));
        var (min, max) = CollisionShapes.Bounds(HavokPackfile.Parse(plan.Assets[mesh.Tag]), meshFile.RootOffset);
        var leaves = compound.LeafBoxes();
        foreach (var placement in baseline.Placements.Where(p => p.Piece == mesh.Index))
            Check(leaves[placement.Instance].Contains(StaticCollisionRule.MovedBox(min, max, compound.Instances[placement.Instance].Transform), 1e-4),
                $"Leaf {placement.Instance} does not enclose the grown mesh.");
        Console.WriteLine($"PASS collision-import-vertices mesh={mesh.Tag:X8} vertices={meshPositions.Length} bytes=MoveVertices size-kept=yes "
            + $"max-error={error:0.####} convex={convex.Tag:X8} bytes=MoveConvexVertices compound-refit={LeviathanCompound:X8}");
    }

    /** New topology re-encodes the mesh; its per-mesh entry changes size. */
    private static void NewTopology(PackageCatalog catalog, CollisionScene baseline)
    {
        var mesh = baseline.Pieces.First(p => p.Shape.Class == CollisionShapes.CompressedMesh && p.Shape.CustomPrimitives == 0 && p.Shape.TriangleCount > 20);
        var indices = mesh.Shape.Indices.Take(mesh.Shape.Indices.Length - 3).ToArray();
        var plan = CollisionImport.Plan(catalog, baseline, Input(baseline, shape: piece =>
            piece == mesh ? new(piece.Tag, piece.Offset, piece.Shape.Vertices, indices) : null), catalog.ReadOriginal);
        byte[] written = plan.Assets[mesh.Tag];
        var file = HavokPackfile.Parse(written);
        var decoded = CollisionShapes.Decode(file, file.RootOffset);
        Check(decoded.TriangleCount == mesh.Shape.TriangleCount - 1, $"The re-encoded mesh holds {decoded.TriangleCount} triangles.");
        Check(CompressedMesh.Read(file, file.RootOffset).Check().Count == 0, "The re-encoded mesh breaks an invariant.");
        Console.WriteLine($"PASS collision-import-topology mesh={mesh.Tag:X8} triangles={mesh.Shape.TriangleCount}->{decoded.TriangleCount} "
            + $"size={catalog.ReadOriginal(mesh.Tag).Length}->{written.Length} invariants=pass");
    }

    private static void Refusals(PackageCatalog catalog, CollisionScene baseline)
    {
        var added = Input(baseline);
        added = added with { Placements = [.. added.Placements, added.Placements[0] with { Instance = 99999 }] };
        var error = Throws<NotSupportedException>(() => CollisionImport.Plan(catalog, baseline, added, catalog.ReadOriginal));
        var first = baseline.Placements[0];
        var skewed = Input(baseline, p => p == first ? Matrix4x4.CreateScale(1, 2, 1) * p.World : p.World);
        Throws<NotSupportedException>(() => CollisionImport.Plan(catalog, baseline, skewed, catalog.ReadOriginal));
        var stale = Input(baseline) with { Sources = new Dictionary<uint, string> { [LeviathanCompound] = new string('0', 64) } };
        Throws<InvalidDataException>(() => CollisionImport.Plan(catalog, baseline, stale, catalog.ReadOriginal));
        Console.WriteLine($"PASS collision-import-refusals unknown-instance=refused non-uniform-scale=refused stale-export=refused message=\"{error.Message[..40]}...\"");
    }

    private static string Undecoded(CollisionScene scene) => string.Join(",", scene.Undecoded.Select(x => $"{x.Key}={x.Value}"));
}
