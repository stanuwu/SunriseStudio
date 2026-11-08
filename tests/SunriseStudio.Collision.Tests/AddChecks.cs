using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;
using static Checks;

/** New collision shapes and instances: the writers, the added rows, the material path and a project build. */
internal static class AddChecks
{
    private const uint LeviathanCompound = 0x80EDC2D0, LeviathanOwner = 0x80EDC2D1;
    private const ushort ShapePackage = 0x0C10;

    internal static void Run(PackageCatalog catalog, string gameRoot)
    {
        Rewrite(catalog, [Package(LeviathanCompound), 0x03D5]);
        var baseline = new CollisionSceneLoader(catalog).Load(new SceneDocument { TagHash = LeviathanCompound });
        GrowAndCopy(catalog, baseline);
        var (plan, _) = Add(catalog, baseline);
        RuleOnTop(catalog, plan);
        Overlay(catalog, plan, () => SecondImport(catalog, plan));
        Build(catalog, plan, gameRoot);
        Refusals(catalog, baseline);
    }

    private static ushort Package(uint tag) => (ushort)TagHashes.Package(tag);

    /** Every owner and compound in the packages writes back to its own bytes. */
    private static void Rewrite(PackageCatalog catalog, ushort[] packages)
    {
        int owners = 0, compounds = 0, refused = 0;
        foreach (var entry in catalog.Entries.Where(e => packages.Contains(e.PackageId) && e.Reference == StaticCollisionReader.OwnerClass))
        {
            byte[] bytes = catalog.ReadOriginal(entry.TagHash);
            var owner = CollisionOwnerRows.Read(bytes);
            Check(owner.Write().AsSpan().SequenceEqual(bytes), $"Owner {entry.TagHash:X8} does not write back.");
            owners++;
            byte[] original = catalog.ReadOriginal(owner.Compound);
            var file = HavokPackfile.Parse(original);
            if (file.RootClass != StaticCompoundShape.ClassName) continue;
            var compound = StaticCompoundShape.Parse(file);
            var (rows, refusal) = CollisionCompoundWriter.Rows(compound);
            if (rows is null) { refused++; Console.WriteLine($"NOTE compound {owner.Compound:X8} refused: {refusal}"); continue; }
            byte[] written = CollisionCompoundWriter.Write(compound, rows, compound.ReadNodes(), compound.DomainMin, compound.DomainMax);
            Check(written.AsSpan().SequenceEqual(original), $"Compound {owner.Compound:X8} does not write back: length "
                + $"{written.Length} vs {original.Length}, first difference at 0x{written.AsSpan().CommonPrefixLength(original):X}.");
            compounds++;
        }
        Check(owners > 0 && compounds > 0 && refused == 0, $"{owners} owners, {compounds} compounds, {refused} refused.");
        Console.WriteLine($"PASS collision-add-rewrite packages={string.Join(",", packages.Select(p => p.ToString("X4")))} owners={owners} "
            + $"compounds={compounds} identical=yes");
    }

    /** A cube's 8 corners and 12 outward triangles, half extent h. */
    private static (Vector3[] Vertices, int[] Indices) Cube(float h)
    {
        var corners = Enumerable.Range(0, 8).Select(i => new Vector3((i & 1) == 0 ? -h : h, (i & 2) == 0 ? -h : h, (i & 4) == 0 ? -h : h)).ToArray();
        int[] indices = [0, 2, 3, 0, 3, 1, 4, 5, 7, 4, 7, 6, 0, 1, 5, 0, 5, 4, 2, 6, 7, 2, 7, 3, 0, 4, 6, 0, 6, 2, 1, 3, 7, 1, 7, 5];
        return (corners, indices);
    }

    private static CollisionImportInput Unchanged(CollisionScene scene) => new(
        scene.Placements.Select(p => new CollisionPlacementInput(p.CompoundTag, p.Instance, scene.Pieces[p.Piece].Tag,
            scene.Pieces[p.Piece].Offset, p.World)).ToArray(),
        scene.Pieces.Select(piece => new CollisionShapeInput(piece.Tag, piece.Offset, piece.Shape.Vertices, piece.Shape.Indices)).ToArray(),
        new Dictionary<uint, string>());

    /** A copied instance must enclose geometry changed in the same import. */
    private static void GrowAndCopy(PackageCatalog catalog, CollisionScene baseline)
    {
        var anchor = baseline.Placements.First(p => baseline.Pieces[p.Piece].Shape.Class == CollisionShapes.ConvexVertices);
        var piece = baseline.Pieces[anchor.Piece];
        var input = Unchanged(baseline) with
        {
            Shapes = [new(piece.Tag, piece.Offset, piece.Shape.Vertices.Select(v => v * 3).ToArray(), piece.Shape.Indices)],
            Placements = [new(LeviathanCompound, anchor.Instance, piece.Tag, piece.Offset,
                anchor.World * Matrix4x4.CreateTranslation(10, 0, 0)) { Added = true }]
        };
        var plan = CollisionImport.Plan(catalog, baseline, input, catalog.ReadOriginal);
        var compound = StaticCompoundShape.Parse(HavokPackfile.Parse(plan.Assets[LeviathanCompound]));
        var file = HavokPackfile.Parse(plan.Assets[piece.Tag]);
        var (min, max) = CollisionShapes.Bounds(file, file.RootOffset);
        Check(compound.LeafBoxes()[^1].Contains(StaticCollisionRule.MovedBox(min, max, compound.Instances[^1].Transform)),
            "A copied instance excludes geometry enlarged in the same import.");
        Console.WriteLine("PASS collision-grow-copy bounds=written-shape");
    }

    /**
     * Adds a convex cube with a chosen material, a mesh cube that takes the nearest materials, and a
     * copy of an existing mesh instance, then checks every invariant of an added shape.
     */
    private static (CollisionImportPlan Plan, uint[] MeshMaterials) Add(PackageCatalog catalog, CollisionScene baseline)
    {
        var owner = CollisionOwnerRows.Read(catalog.ReadOriginal(LeviathanOwner));
        var file = HavokPackfile.Parse(catalog.ReadOriginal(LeviathanCompound));
        var installed = StaticCompoundShape.Parse(file);
        var (source, _) = CollisionCompoundWriter.Rows(installed);
        // Anchor the new shapes next to a mesh instance whose material resolves through the owner.
        var anchor = baseline.Placements.First(p => baseline.Pieces[p.Piece].Shape.Class == CollisionShapes.CompressedMesh
            && source![p.Instance].UserData >> 16 == 1 && owner.RunStarts[(int)(source[p.Instance].UserData & 0xFFFF)] >= 0
            && baseline.Pieces[p.Piece].Shape.TriangleCount > 20);
        var center = (anchor.BoundsMin + anchor.BoundsMax) / 2;
        var (vertices, indices) = Cube(0.5f);
        uint chosen = owner.Materials[3];
        var convexWorld = Matrix4x4.CreateFromYawPitchRoll(0.3f, 0, 0) * Matrix4x4.CreateTranslation(center + new Vector3(0, 0, 2));
        var meshWorld = Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(center + new Vector3(3, 0, 0));
        var copyWorld = anchor.World * Matrix4x4.CreateTranslation(0, 4, 0);
        var input = Unchanged(baseline);
        input = input with
        {
            Placements = [.. input.Placements,
                new(0, -1, 0, 0, convexWorld) { Added = true, NewShape = new("convex cube", vertices, indices, true, chosen) },
                new(LeviathanCompound, -1, 0, 0, meshWorld) { Added = true, NewShape = new("mesh cube", vertices, indices, false, null) },
                new(LeviathanCompound, anchor.Instance, baseline.Pieces[anchor.Piece].Tag, baseline.Pieces[anchor.Piece].Offset, copyWorld) { Added = true }]
        };
        Check(!catalog.ContainsInstalledPackage(ShapePackage), "The test package id is installed.");
        var plan = CollisionImport.Plan(catalog, baseline, input, catalog.ReadOriginal, new CollisionShapePackage(ShapePackage, 0));
        Check(plan.ObjectEdits.Count == 0 && plan.AddedShapes.Count == 2 && plan.Assets.Keys.Order().SequenceEqual(new[] { LeviathanCompound, LeviathanOwner }),
            $"The plan has {plan.ObjectEdits.Count} edits, {plan.AddedShapes.Count} shapes and assets {string.Join(",", plan.Assets.Keys.Select(k => k.ToString("X8")))}.");
        uint convexTag = TagHashes.Make(ShapePackage, 0), meshTag = convexTag + 1;
        Check(plan.AddedShapes[0].Tag == convexTag && plan.AddedShapes[1].Tag == meshTag
            && plan.AddedShapes.All(s => s.Native.PackageId == ShapePackage && s.Native.Reference == uint.MaxValue), "Added shape tags or metadata differ.");

        // The owner: two shapes appended with their run starts and slots, nothing else changed.
        var next = CollisionOwnerRows.Read(plan.Assets[LeviathanOwner]);
        int n = owner.Shapes.Count;
        Check(next.Shapes.Count == n + 2 && next.Shapes[n] == convexTag && next.Shapes[n + 1] == meshTag
            && next.Shapes.Take(n).SequenceEqual(owner.Shapes) && next.RunStarts.Count == n + 2
            && next.RunStarts.Take(n).SequenceEqual(owner.RunStarts) && next.Slots.Take(owner.Slots.Count).SequenceEqual(owner.Slots)
            && next.RunStarts[n] == owner.Slots.Count && next.Materials.Take(owner.Materials.Count).SequenceEqual(owner.Materials)
            && next.MaterialMaps.SequenceEqual(owner.MaterialMaps) && next.Compound == LeviathanCompound,
            "The owner rows are not the old rows plus the new ones.");

        // The compound: the installed rows are byte for byte the same, three rows appended.
        byte[] written = plan.Assets[LeviathanCompound];
        var after = HavokPackfile.Parse(written);
        var compound = StaticCompoundShape.Parse(after);
        int count = installed.Instances.Count;
        Check(compound.Instances.Count == count + 3 && compound.NodeCount == 2 * (count + 3) - 1, "The compound does not hold three more instances.");
        for (int i = 0; i < count; i++)
        {
            var (a, b) = (installed.Instances[i], compound.Instances[i]);
            Check(a.Offset == b.Offset && written.AsSpan(after.Absolute(b.Offset), 0x50).SequenceEqual(file.Bytes.AsSpan(file.Absolute(a.Offset), 0x50))
                && a.ChildImport == b.ChildImport && (a.ChildObject is null) == (b.ChildObject is null)
                && (a.ChildObject is not int child || after.ClassAt(b.ChildObject!.Value) == file.ClassAt(child)),
                $"Installed instance {i} changed.");
        }
        // Everything before the data section's six offsets, and the class names after the headers, is kept.
        Check(after.DataStart == file.DataStart && written.AsSpan(0, 0x40 + 2 * 0x30 + 0x18).SequenceEqual(file.Bytes.AsSpan(0, 0x40 + 2 * 0x30 + 0x18))
            && written.AsSpan(0x40 + 3 * 0x30, file.DataStart - 0x40 - 3 * 0x30).SequenceEqual(file.Bytes.AsSpan(0x40 + 3 * 0x30, file.DataStart - 0x40 - 3 * 0x30))
            && after.LocalFixups.Count == 2 && after.GlobalFixups.Count == file.GlobalFixups.Count
            && after.GlobalFixups.All(g => after.ClassAt(g.Target) is not null) && after.Objects.Count == file.Objects.Count
            && after.Imports.Count == file.Imports.Count + 3, "The compound's header, fixups or imports are off.");
        int root = after.RootOffset;
        int bits = (sbyte)after.U8(root + 0x30);
        Check(after.U8(root + 0x31) == 0 && after.U32(root + 0x34) == 0, "Compound +0x31 or +0x34 is not left at 0.");
        Check(CompressedMesh.BitLength((uint)compound.Instances.Count) + bits <= 32, "Instance bits plus child key bits pass 32.");
        foreach (var (field, name) in after.Imports)
        {
            int index = int.Parse(name[1..]);
            Check(name[0] == 'i' ? index < next.Shapes.Count : name[0] == 't' && index < next.MaterialMaps.Count, $"Import {name} does not resolve.");
        }

        // The tree: 2n - 1 nodes, each old leaf encloses its old leaf box, each new leaf its moved child.
        var oldLeaves = installed.LeafBoxes();
        var leaves = compound.LeafBoxes();
        for (int i = 0; i < count; i++) Check(leaves[i].Contains(oldLeaves[i]), $"New leaf {i} does not enclose the installed leaf.");
        var added = new[] { (Row: count, Tag: convexTag), (Row: count + 1, Tag: meshTag) };
        var shapes = plan.AddedShapes.ToDictionary(s => s.Tag, s => HavokPackfile.Parse(s.Data));
        foreach (var (row, tag) in added)
        {
            var shapeFile = shapes[tag];
            var (min, max) = CollisionShapes.Bounds(shapeFile, shapeFile.RootOffset);
            Check(leaves[row].Contains(StaticCollisionRule.MovedBox(min, max, compound.Instances[row].Transform)), $"Leaf {row} does not enclose its new shape.");
            Check(shapeFile.UserTag == 1, "A new shape packfile is not user tag 1.");
        }
        var copied = compound.Instances[count + 2];
        var anchorShape = baseline.Pieces[anchor.Piece].Shape;
        Check(leaves[count + 2].Contains(StaticCollisionRule.MovedBox(anchorShape.BoundsMin, anchorShape.BoundsMax, copied.Transform)),
            "The copied leaf does not enclose its shape.");
        Check(copied.ChildImport == installed.Instances[anchor.Instance].ChildImport
            && written.AsSpan(after.Absolute(copied.Offset) + 0x38, 0x18).SequenceEqual(file.Bytes.AsSpan(file.Absolute(installed.Instances[anchor.Instance].Offset) + 0x38, 0x18))
            && (copied.Flags & 1) == (installed.Instances[anchor.Instance].Flags & 1), "The copy does not keep its source's child, filter and user data.");
        var frame = baseline.Entities[anchor.Entity].Frame;
        Check(Near(copied.Transform.ToMatrix() * frame, copyWorld) && Near(compound.Instances[count].Transform.ToMatrix() * frame, convexWorld)
            && Near(compound.Instances[count + 1].Transform.ToMatrix() * frame, meshWorld), "An added instance is not at its world transform.");

        // Instance rows: user data 0x10000 | N, mesh filter 0 and mask 0xFFFFFFFF, convex its own filter and mask 0.
        var convexRow = Row(written, after, compound.Instances[count]);
        var meshRow = Row(written, after, compound.Instances[count + 1]);
        Check(convexRow.UserData == (0x10000u | (uint)n) && meshRow.UserData == (0x10000u | (uint)(n + 1)), "New user data is not 0x10000 | N.");
        Check(compound.Instances[count].ChildImport == $"i{n}" && compound.Instances[count + 1].ChildImport == $"i{n + 1}", "New imports are not iN.");
        Check(meshRow.FilterInfo == 0 && meshRow.ChildMask == 0xFFFFFFFF && convexRow.ChildMask == 0 && convexRow.FilterInfo != 0
            && (compound.Instances[count].Flags & 1) == 1 && (compound.Instances[count + 1].Flags & 1) == 0, "New filter, mask or convex flag is off.");
        Check(bits >= 1 && bits >= CompressedMesh.BitLength(CompressedMesh.Read(shapes[meshTag], shapes[meshTag].RootOffset).MaxKeyValue),
            "Child key bits do not cover the new mesh's largest key.");

        // The material path: +0x08[+0x28[+0x38[ud & 0xFFFF] + v]] for every new primitive.
        var convex = shapes[convexTag];
        Check(convex.I32(convex.RootOffset + 0x18) == 0 && next.Material(n, 0) == chosen, "The convex cube does not resolve to the chosen material.");
        var mesh = CompressedMesh.Read(shapes[meshTag], shapes[meshTag].RootOffset);
        Check(shapes[meshTag].I32(shapes[meshTag].RootOffset + 0x18) == -1 && mesh.HasPerPrimitiveUserData && mesh.Check().Count == 0,
            "The mesh cube is not a palette mesh that passes its invariants.");
        var decoded = CollisionShapes.Decode(shapes[meshTag], shapes[meshTag].RootOffset);
        var expected = ExpectedMaterials(catalog, baseline, owner, source!, decoded, compound.Instances[count + 1].Transform.ToMatrix() * frame);
        int resolved = 0;
        for (int t = 0; t < decoded.TriangleCount; t++)
        {
            uint key = decoded.TriangleKeys[t];
            var run = mesh.RunOf(mesh.Sections[(int)(key >> 8)], (int)(key >> 1) & 0x7F);
            long v = mesh.UserDataPalette[run.UserDataIndex];
            Check(next.Material(n + 1, v) == expected[t], $"Mesh triangle {t} resolves to {next.Material(n + 1, v):X8}, not {expected[t]:X8}.");
            resolved++;
        }
        Console.WriteLine($"PASS collision-add compound={LeviathanCompound:X8} instances={count}->{compound.Instances.Count} nodes={compound.NodeCount} "
            + $"owner-shapes={n}->{next.Shapes.Count} slots={owner.Slots.Count}->{next.Slots.Count} materials={owner.Materials.Count}->{next.Materials.Count} "
            + $"key-bits={bits} installed-rows=identical imports-resolve=yes tree-encloses=yes convex={convexTag:X8} material={chosen:X8} "
            + $"mesh={meshTag:X8} triangles={resolved} nearest-materials={string.Join(",", expected.Distinct().Select(m => m.ToString("X8")))} "
            + $"copy=#{anchor.Instance} size={file.Bytes.Length}->{written.Length}");
        return (plan, expected);
    }

    private static CompoundInstanceRow Row(byte[] bytes, HavokPackfile file, CompoundInstance instance) =>
        new(bytes.AsSpan(file.Absolute(instance.Offset), 0x50).ToArray(), instance.ChildImport, instance.ChildObject);

    /**
     * The nearest existing triangle's material for each new triangle, by brute force over every
     * placement, written independently of the importer.
     */
    private static uint[] ExpectedMaterials(PackageCatalog catalog, CollisionScene baseline, CollisionOwnerRows owner,
        List<CompoundInstanceRow> rows, CollisionShape shape, Matrix4x4 world)
    {
        var centers = Enumerable.Range(0, shape.TriangleCount).Select(t => Vector3.Transform(
            (shape.Vertices[shape.Indices[3 * t]] + shape.Vertices[shape.Indices[3 * t + 1]] + shape.Vertices[shape.Indices[3 * t + 2]]) / 3, world)).ToArray();
        var best = new float[centers.Length];
        Array.Fill(best, float.PositiveInfinity);
        var result = new uint[centers.Length];
        var meshes = new Dictionary<int, (HavokPackfile File, CompressedMesh? Mesh, int UserData)>();
        foreach (var placement in baseline.Placements.Where(p => p.CompoundTag == LeviathanCompound))
        {
            var row = rows[placement.Instance];
            if (row.UserData >> 16 != 1) continue;
            var piece = baseline.Pieces[placement.Piece];
            if (!meshes.TryGetValue(piece.Index, out var child))
            {
                var file = HavokPackfile.Parse(catalog.Read(piece.Tag));
                int userData = file.I32(piece.Offset + 0x18);
                meshes[piece.Index] = child = (file, file.ClassAt(piece.Offset) == CollisionShapes.CompressedMesh ? CompressedMesh.Read(file, piece.Offset) : null, userData);
            }
            var s = piece.Shape;
            for (int t = 0; t < s.TriangleCount; t++)
            {
                long v = child.UserData;
                if (v == -1 && child.Mesh is { } m)
                {
                    uint key = s.TriangleKeys[t];
                    int index = m.RunOf(m.Sections[(int)(key >> 8)], (int)(key >> 1) & 0x7F).UserDataIndex;
                    v = m.UserDataPalette.Length > 0 ? (int)m.UserDataPalette[index] : index == 0xFF ? -1 : index;
                }
                if (owner.Material((int)(row.UserData & 0xFFFF), v) is not uint material) continue;
                var c = Vector3.Transform((s.Vertices[s.Indices[3 * t]] + s.Vertices[s.Indices[3 * t + 1]] + s.Vertices[s.Indices[3 * t + 2]]) / 3, placement.World);
                for (int k = 0; k < centers.Length; k++)
                {
                    float d = Vector3.DistanceSquared(c, centers[k]);
                    if (d < best[k]) (best[k], result[k]) = (d, material);
                }
            }
        }
        return result;
    }

    private static bool Near(Matrix4x4 a, Matrix4x4 b)
    {
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                if (MathF.Abs(a[r, c] - b[r, c]) > 2e-3f * MathF.Max(1, MathF.Abs(b[r, c]))) return false;
        return true;
    }

    /** A static instance edit of an installed instance still compiles on top of the grown compound. */
    private static void RuleOnTop(PackageCatalog catalog, CollisionImportPlan plan)
    {
        var installed = StaticCompoundShape.Parse(HavokPackfile.Parse(catalog.ReadOriginal(LeviathanCompound)));
        const int instance = 10;
        byte[] original = catalog.ReadOriginal(LeviathanCompound);
        int offset = installed.File.Absolute(installed.Instances[instance].Offset);
        var target = new ObjectEditTarget(LeviathanCompound, ObjectEditLayout.CollisionInstance, offset, StaticCompoundShape.InstanceSize,
            instance, 0, BinaryDelta.Hash(original.AsSpan(offset, StaticCompoundShape.InstanceSize)));
        var moved = installed.Instances[instance].Transform.Translation + new Vector3(0, 0, 5);
        var edit = new ObjectEdit(ObjectEditKind.PlacementTransform, target, "Moved", [moved.X, moved.Y, moved.Z], null, null);
        byte[] ReadRaw(uint tag) => plan.Assets.TryGetValue(tag, out var bytes) ? bytes : catalog.ReadOriginal(tag);
        var compiled = ObjectEditCompiler.Default.Compile([edit], ReadRaw, catalog).Assets[LeviathanCompound];
        var raw = plan.Assets[LeviathanCompound];
        var shape = StaticCompoundShape.Parse(HavokPackfile.Parse(compiled));
        var rawShape = StaticCompoundShape.Parse(HavokPackfile.Parse(raw));
        Check(compiled.Length == raw.Length && Vector3.Distance(shape.Instances[instance].Transform.Translation, moved) < 1e-4f
            && shape.Instances.Skip(installed.Instances.Count).Zip(rawShape.Instances.Skip(installed.Instances.Count))
                .All(pair => pair.First.Transform == pair.Second.Transform), "The rule does not move an installed instance of the grown compound.");
        var leaves = shape.LeafBoxes();
        var rawLeaves = rawShape.LeafBoxes();
        for (int i = installed.Instances.Count; i < leaves.Length; i++) Check(leaves[i].Contains(rawLeaves[i]), $"The refit lost added leaf {i}.");
        Console.WriteLine($"PASS collision-add-rule instance={instance} moved=5m added-rows=kept size={compiled.Length}");
    }

    /** Runs the action with the plan's bytes in the catalog, as the editor overlay shows a project. */
    private static void Overlay(PackageCatalog catalog, CollisionImportPlan plan, Action action)
    {
        var rows = new List<PackageOverlayEntry>();
        foreach (var (tag, bytes) in plan.Assets)
        {
            catalog.TryGetEntry(tag, out var entry);
            rows.Add(new(entry! with { Size = bytes.Length }));
        }
        foreach (var shape in plan.AddedShapes)
            rows.Add(new(new PackageEntry(shape.Tag, shape.Native.Reference, (int)(shape.Native.TypeInfo >> 9 & 127), (int)(shape.Native.TypeInfo >> 6 & 7),
                shape.Data.Length, $"w64_sunrise_{shape.Native.PackageId:x4}_0", shape.Native.PackageId, shape.Native.EntryIndex!.Value)
                { TypeInfo = shape.Native.TypeInfo }));
        catalog.SetOverlayEntries(rows);
        catalog.ReadOverride = tag => plan.Assets.TryGetValue(tag, out var bytes) ? bytes.ToArray()
            : plan.AddedShapes.FirstOrDefault(shape => shape.Tag == tag)?.Data.ToArray();
        try { action(); }
        finally
        {
            catalog.ReadOverride = null;
            catalog.SetOverlayEntries([]);
        }
    }

    /**
     * The layer reads the new shapes back through the owner, and a second import moves an added
     * instance in the compound bytes and copies it again.
     */
    private static void SecondImport(PackageCatalog catalog, CollisionImportPlan plan)
    {
        var scene = new CollisionSceneLoader(catalog).Load(new SceneDocument { TagHash = LeviathanCompound });
        var installed = StaticCompoundShape.Parse(HavokPackfile.Parse(catalog.ReadOriginal(LeviathanCompound)));
        int count = installed.Instances.Count;
        var news = scene.Placements.Where(p => p.Instance >= count).OrderBy(p => p.Instance).ToArray();
        Check(news.Length == 3 && scene.Pieces[news[0].Piece].Tag == plan.AddedShapes[0].Tag && scene.Pieces[news[1].Piece].Tag == plan.AddedShapes[1].Tag
            && scene.Pieces[news[0].Piece].Shape.TriangleCount == 12 && scene.Pieces[news[1].Piece].Shape.TriangleCount == 12,
            "The layer does not read the added instances through the owner.");
        var input = Unchanged(scene);
        var moved = news[0].World * Matrix4x4.CreateTranslation(1, 0, 0);
        input = input with
        {
            Shapes = news.Take(2).Select(p => scene.Pieces[p.Piece]).Select(piece =>
                new CollisionShapeInput(piece.Tag, piece.Offset, piece.Shape.Vertices.Select(v => v * 3).ToArray(), piece.Shape.Indices)).ToArray(),
            Placements = [.. input.Placements.Select(p => p.CompoundTag == LeviathanCompound && p.Instance == news[0].Instance ? p with { World = moved } : p),
                new(LeviathanCompound, news[1].Instance, scene.Pieces[news[1].Piece].Tag, 0, news[1].World * Matrix4x4.CreateTranslation(0, 3, 0)) { Added = true }]
        };
        byte[] ReadRaw(uint tag) => plan.Assets.TryGetValue(tag, out var bytes) ? bytes
            : plan.AddedShapes.FirstOrDefault(shape => shape.Tag == tag)?.Data ?? catalog.ReadOriginal(tag);
        var second = CollisionImport.Plan(catalog, scene, input, ReadRaw, new CollisionShapePackage(ShapePackage, 2));
        var compound = StaticCompoundShape.Parse(HavokPackfile.Parse(second.Assets[LeviathanCompound]));
        var frame = scene.Entities[news[0].Entity].Frame;
        Check(second.ObjectEdits.Count == 0 && second.AddedShapes.Count == 0 && compound.Instances.Count == count + 4
            && Near(compound.Instances[news[0].Instance].Transform.ToMatrix() * frame, moved)
            && compound.Instances[count + 3].ChildImport == compound.Instances[news[1].Instance].ChildImport
            && !second.Assets.ContainsKey(LeviathanOwner), "A second import does not move and copy added instances in the compound bytes.");
        foreach (var (placement, row) in new[] { (news[0], news[0].Instance), (news[1], count + 3) })
        {
            var changed = HavokPackfile.Parse(second.Assets[scene.Pieces[placement.Piece].Tag]);
            var (min, max) = CollisionShapes.Bounds(changed, changed.RootOffset);
            Check(compound.LeafBoxes()[row].Contains(StaticCollisionRule.MovedBox(min, max, compound.Instances[row].Transform)),
                $"Added instance {row} excludes geometry enlarged while moving or copying it.");
        }
        Console.WriteLine($"PASS collision-add-reread placements={scene.Placements.Count} added-read=3 second-import=move+copy instances={compound.Instances.Count} owner-unchanged=yes");
    }

    /** A project holding the plan builds a patch of the Leviathan family and a new package, and both read back. */
    private static void Build(PackageCatalog catalog, CollisionImportPlan plan, string gameRoot)
    {
        string output = Path.Combine(Path.GetTempPath(), "sunrise-collision-add-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var project = StudioProject.Create(Path.Combine(output, "project"), "Added collision");
            project.ObjectEditCompiler = ObjectEditCompiler.Default;
            var created = project.RestoreTransaction(new(plan.Assets), new Dictionary<string, AddedAssetSnapshot?>(), catalog, null, plan.Additions);
            Check(created.Count == 2 && created.Select(a => TagHashes.Make(a.Native!.PackageId, a.Native.EntryIndex!.Value))
                .SequenceEqual(plan.AddedShapes.Select(s => s.Tag)), "The project did not keep the planned entry indices.");
            var result = ProjectBuilder.Build(project, catalog, Path.Combine(output, "build"));
            catalog.TryGetEntry(LeviathanCompound, out var entry);
            string family = entry!.PackageName[..entry.PackageName.LastIndexOf('_')];
            string install = Path.Combine(output, "install");
            Directory.CreateDirectory(Path.Combine(install, "packages"));
            Directory.CreateDirectory(Path.Combine(install, "bin", "x64"));
            File.Copy(Path.Combine(gameRoot, "bin", "x64", "oo2core_3_win64.dll"), Path.Combine(install, "bin", "x64", "oo2core_3_win64.dll"));
            foreach (string path in Directory.EnumerateFiles(catalog.PackageDirectory, family + "_*.pkg").Concat(result.PackageFiles))
                File.Copy(path, Path.Combine(install, "packages", Path.GetFileName(path)), true);
            using var rebuilt = PackageCatalog.Open(install, Path.Combine(gameRoot, "destiny2.exe"));
            foreach (var (tag, bytes) in plan.Assets) Check(rebuilt.Read(tag).AsSpan().SequenceEqual(bytes), $"The build does not hold {tag:X8}.");
            foreach (var shape in plan.AddedShapes) Check(rebuilt.Read(shape.Tag).AsSpan().SequenceEqual(shape.Data), $"The build does not hold new shape {shape.Tag:X8}.");
            var scene = new CollisionSceneLoader(rebuilt).Load(new SceneDocument { TagHash = LeviathanCompound });
            int count = StaticCompoundShape.Parse(HavokPackfile.Parse(catalog.ReadOriginal(LeviathanCompound))).Instances.Count;
            // Only the Leviathan family and the new package are copied, so installed children elsewhere are missing.
            var placed = scene.Placements.Where(p => p.Instance >= count).Select(p => scene.Pieces[p.Piece].Tag).Order().ToArray();
            Check(placed.SequenceEqual(plan.AddedShapes.Select(s => s.Tag).Order()), "The built packages do not place the new shapes.");
            // The game loads a new shape only when a record of a built patch lists it.
            var tables = result.PackageFiles.Select(path => PackageDependencyTable.Read(PackageWriter.ReadMisc(path)))
                .OfType<PackageDependencyTable>().ToArray();
            bool Listed(uint tag) => tables.Any(table => table.Records.Any(record => record.Lists(
                (ushort)TagHashes.Package(tag), TagHashes.Entry(tag))));
            Check(plan.AddedShapes.All(shape => Listed(shape.Tag)), "No built dependency record lists every new shape.");
            Console.WriteLine($"PASS collision-add-build packages={string.Join(",", result.PackageFiles.Select(Path.GetFileName))} read-back=equal "
                + $"new-shapes-placed={placed.Length} new-shapes-listed={plan.AddedShapes.Count} "
                + $"compound-size={plan.Assets[LeviathanCompound].Length} owner-size={plan.Assets[LeviathanOwner].Length}");
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, true);
        }
    }

    private static void Refusals(PackageCatalog catalog, CollisionScene baseline)
    {
        var (vertices, indices) = Cube(0.5f);
        var at = Matrix4x4.CreateTranslation((baseline.Placements[0].BoundsMin + baseline.Placements[0].BoundsMax) / 2);
        var input = Unchanged(baseline);
        CollisionImportInput With(CollisionNewShape shape) =>
            input with { Placements = [.. input.Placements, new(0, -1, 0, 0, at) { Added = true, NewShape = shape }] };
        var noPackage = Throws<NotSupportedException>(() =>
            CollisionImport.Plan(catalog, baseline, With(new("cube", vertices, indices, false, null)), catalog.ReadOriginal));
        Throws<InvalidDataException>(() => CollisionImport.Plan(catalog, baseline, With(new("cube", vertices, indices, false, LeviathanOwner)),
            catalog.ReadOriginal, new CollisionShapePackage(ShapePackage, 0)));
        Throws<InvalidDataException>(() => CollisionImport.Plan(catalog, baseline, With(new("flat", vertices.Take(3).ToArray(), [0, 1, 2], true, null)),
            catalog.ReadOriginal, new CollisionShapePackage(ShapePackage, 0)));
        Console.WriteLine($"PASS collision-add-refusals no-package=refused not-a-material=refused flat-convex=refused message=\"{noPackage.Message}\"");
    }
}
