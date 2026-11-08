using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene.Collision;
using static Checks;

/** Decoder and rule checks on the owned install, compared with the reference decoder's numbers. */
internal static class OwnedChecks
{
    private const uint LeviathanMap = 0x80EDC19B, LeviathanOwner = 0x80EDC2D1, LeviathanCompound = 0x80EDC2D0;
    private const uint SabreTable = 0x80FAA28E;
    private const int SabreRow = 4544;

    internal static void Run(PackageCatalog catalog)
    {
        Decoder(catalog);
        Map(catalog);
        foreach (ushort package in new ushort[] { Package(LeviathanCompound), Package(SabreTable) }) Census(catalog, package);
        SabreTree(catalog);
        CullChecks.Owned(catalog, SabreTable, SabreRow);
        LeviathanMove(catalog);
    }

    private static ushort Package(uint tag) => (ushort)TagHashes.Package(tag);

    /** The reference decoder printed these numbers for owner 0x80EDC2D1. */
    private static void Decoder(PackageCatalog catalog)
    {
        var owner = StaticCollisionReader.ReadOwner(LeviathanOwner, catalog.ReadOriginal(LeviathanOwner));
        Check(owner.CompoundTag == LeviathanCompound && owner.ImportTags.Count == 360, "Owner tag list differs from the reference.");
        var file = HavokPackfile.Parse(catalog.ReadOriginal(LeviathanCompound));
        var compound = StaticCompoundShape.Parse(file);
        Check(file.UserTag == 0 && compound.Instances.Count == 5058 && compound.NodeCount == 10115, "Compound counts differ.");
        var flags = compound.Instances.GroupBy(x => x.Flags & 15).OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Count()}");
        Check(string.Join(",", flags) == "2:2632,3:558,6:1566,7:302", "Compound flag counts differ.");
        // The reference counts instances without an import; each has an in-file child.
        int inFile = compound.Instances.Count(x => x.ChildObject is not null);
        Check(inFile == 157 && compound.Instances.Count(x => x.ChildImport is null) == 157,
            $"In-file child count differs: in-file={inFile} no-import={compound.Instances.Count(x => x.ChildImport is null)}.");
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
        {
            var row = new byte[44];
            foreach (var x in compound.Instances)
            {
                var t = x.Transform;
                float[] values = [t.Translation.X, t.Translation.Y, t.Translation.Z, t.Rotation.X, t.Rotation.Y,
                    t.Rotation.Z, t.Rotation.W, t.Scale.X, t.Scale.Y, t.Scale.Z];
                for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(row.AsSpan(4 * i), values[i]);
                BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(40), x.Flags);
                sha.AppendData(row);
            }
            Check(Hex(sha.GetHashAndReset()) == "296b272c63d3d7a2517435990fee7283b5c9b6f3", "Instance hash differs.");
        }
        var leaves = compound.LeafBoxes();
        Near(leaves[0], [-263.6412, -1476.2232, -25.5013], [-255.8461, -1468.5595, -24.9764]);
        Near(leaves[1], [-273.9595, -1416.9745, -28.7189], [-267.2393, -1409.0535, -21.9888]);
        Near(leaves[5057], [-197.2418, -1355.9254, -20.5002], [-195.8108, -1345.8367, -14.6245]);
        int flagMismatch = compound.Instances.Count(x => StaticCompoundShape.Flags(x.Transform, (x.Flags & 1) != 0) != (x.Flags & 15));
        Check(flagMismatch == 0, $"{flagMismatch} stored flags differ from the transform.");

        var roots = new SortedDictionary<string, int>(StringComparer.Ordinal);
        long triangles = 0, custom = 0, convexVertices = 0;
        var meshHashes = new Dictionary<uint, string>();
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        foreach (uint tag in owner.ImportTags)
        {
            if (!catalog.TryGetEntry(tag, out _)) { Add(roots, $"unreadable {tag:X8}"); continue; }
            var child = HavokPackfile.Parse(catalog.ReadOriginal(tag));
            Add(roots, child.RootClass);
            var shape = CollisionShapes.Decode(child, child.RootOffset);
            if (child.RootClass == CollisionShapes.CompressedMesh)
            {
                triangles += shape.MeshTriangleCount;
                custom += shape.CustomPrimitives;
                string hash = TriangleHash(shape);
                meshHashes[tag] = hash;
                aggregate.AppendData(Encoding.ASCII.GetBytes(hash));
            }
            else if (child.RootClass == CollisionShapes.ConvexVertices) convexVertices += shape.Vertices.Length;
        }
        string rootText = string.Join(",", roots.Select(x => $"{x.Key}={x.Value}"));
        Check(rootText == "hkpBvCompressedMeshShape=303,hkpConvexTranslateShape=1,hkpConvexVerticesShape=54,unreadable FFFFFFFF=2",
            $"Child classes differ: {rootText}");
        Check(triangles == 54063 && custom == 5336 && convexVertices == 2585,
            $"Mesh totals differ: triangles={triangles} custom={custom} convex={convexVertices}");
        Check(meshHashes[0x80C6C7EE] == "80038eced53b362ae2043f3ad2abc034a2ba13c1"
            && meshHashes[0x80C6DD4C] == "f8b85aeef14f3434661e546b4c913ff5d8be6552"
            && meshHashes[0x80C6C7EC] == "6189bab4e9c1be08922699c756bc02c1c1fdab79"
            && meshHashes[0x80C4EC40] == "1fdc3a2e0959fb24dcb6de4cbc43bef6856fb8a7", "A mesh triangle hash differs.");
        Check(Hex(aggregate.GetHashAndReset()) == "3493b6b8f090dfe290a407d9999ebc921bf3e5a6", "The all-mesh hash differs.");
        Console.WriteLine($"PASS collision-decoder compound={LeviathanCompound:X8} instances=5058 nodes=10115 leaves=5058 "
            + $"flags=reference in-file=157 children={rootText} triangles={triangles} custom={custom} convex-vertices={convexVertices} "
            + "mesh-hashes=reference");
    }

    private static void Map(PackageCatalog catalog)
    {
        var reader = new StaticCollisionReader(catalog);
        var set = reader.ReadMap(LeviathanMap);
        int instances = set.Entities.Sum(x => x.Instances.Count);
        long triangles = set.Entities.SelectMany(x => x.Instances).Sum(x => (long)(x.Shape?.TriangleCount ?? 0));
        Check(set.Entities.Count > 0 && set.Entities.Any(x => x.Owner.Tag == LeviathanOwner), "The map chain did not reach the collision owner.");
        var levi = set.Entities.Single(x => x.Owner.Tag == LeviathanOwner);
        var first = levi.Instances[0];
        Check(Vector3.Distance(first.World.Translation, levi.Compound.Instances[0].Transform.Translation) < 1e-3f
            || levi.Placement.Placement.Translation != Vector3.Zero, "Instance world transform ignores the placement.");
        string undecoded = string.Join(",", set.Undecoded.Select(x => $"{x.Key}={x.Value}"));
        Console.WriteLine($"PASS collision-map map={LeviathanMap:X8} lists={reader.MapObjectLists(LeviathanMap).Count} "
            + $"entities={set.Entities.Count} compounds={set.Entities.Select(x => x.Owner.CompoundTag).Distinct().Count()} "
            + $"instances={instances} placed-triangles={triangles} custom={set.CustomPrimitives} custom-decoded={set.DecodedCustomPrimitives} "
            + $"undecoded=[{undecoded}] diagnostics={set.Diagnostics.Count}");
        foreach (var line in set.Diagnostics.Take(5)) Console.WriteLine("  " + line);
    }

    /** Joins every placed render row of a package to the compounds, as the rule does. */
    private static void Census(PackageCatalog catalog, ushort package)
    {
        var context = new ObjectEditContext(catalog, catalog.ReadOriginal);
        var index = new StaticCollisionRule.CollisionIndex(context, package);
        int rows = 0, none = 0, unique = 0, shared = 0, refused = 0, flagMismatch = 0;
        var reasons = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var entity in index.Entities)
            foreach (var x in entity.Compound.Instances.Where(x => StaticCompoundShape.Flags(x.Transform, (x.Flags & 1) != 0) != (x.Flags & 15)))
            {
                flagMismatch++;
                Console.WriteLine($"  flag mismatch {entity.Owner.CompoundTag:X8}/{x.Index}: stored {x.Flags & 15} "
                    + $"t={x.Transform.Translation} q={x.Transform.Rotation} s={x.Transform.Scale}");
            }
        var tables = catalog.Entries.Where(x => x.PackageId == package && x.Reference == 0x8080966D).Select(x => x.TagHash).Order();
        int multiple = 0;
        foreach (uint table in tables)
        {
            byte[] data = catalog.ReadOriginal(table);
            int[] offsets = TagRows.StaticInstances(data);
            foreach (var owner in index.TableOwners(table))
            {
                var placement = Placement(catalog, owner);
                for (int i = 0; i < offsets.Length; i++)
                {
                    rows++;
                    try
                    {
                        var matches = index.Find(StaticCollisionRule.Compose(placement, Row(data, offsets[i])));
                        if (matches.Count == 0) { none++; continue; }
                        if (index.RowsCopying(matches[0].Entity, matches[0].Instance).Count > 1) shared++;
                        else unique++;
                        if (matches.Count > 1) multiple++;
                    }
                    catch (Exception error) when (error is InvalidDataException or NotSupportedException)
                    {
                        refused++;
                        Add(reasons, error.Message);
                    }
                }
            }
        }
        Check(flagMismatch == 0, $"{flagMismatch} stored flags in package {package:X4} differ from the transform.");
        Console.WriteLine($"PASS collision-join package={package:X4} compounds={index.Entities.Count} "
            + $"compound-instances={index.Entities.Sum(x => x.Compound.Instances.Count)} flags=recomputed placed-rows={rows} "
            + $"own-collision={unique} shared-with-other-rows={shared} several-instances={multiple} no-collision={none} refused={refused}");
        foreach (var (reason, count) in reasons) Console.WriteLine($"  refused {count}: {reason}");
    }

    /** A sabre tree: table 0x80FAA28E row 4544, scaled to 3. */
    private static void SabreTree(PackageCatalog catalog)
    {
        byte[] table = catalog.ReadOriginal(SabreTable);
        int row = TagRows.StaticInstances(table)[SabreRow];
        Check(row + 0x1C == 218332, "The tree row is not at its expected offset.");
        var edit = Edit(table, SabreTable, SabreRow, row, scale: 3);
        var (result, moved) = CheckEdit(catalog, [edit]);
        var parts = new List<string>();
        foreach (var (entity, instance) in moved)
        {
            var placed = StaticCompoundShape.Parse(HavokPackfile.Parse(result[entity.Owner.CompoundTag])).Instances[instance];
            var installed = entity.Compound.Instances[instance];
            Check(placed.Transform.Translation == installed.Transform.Translation && placed.Transform.Rotation == installed.Transform.Rotation,
                "An unchanged translation or rotation was rewritten.");
            parts.Add($"{entity.Owner.CompoundTag:X8}/{instance} scale={installed.Transform.Scale.X:0.###}->{placed.Transform.Scale.X:0.###} "
                + $"flags={installed.Flags & 15}->{placed.Flags & 15}");
        }
        Console.WriteLine($"PASS collision-rule-sabre table={SabreTable:X8} row={SabreRow} moved=[{string.Join("; ", parts)}] "
            + "translation,rotation=stored-bytes tree=encloses");
    }

    /** Moves Leviathan rows with one and with several collision instances, then checks refusals. */
    private static void LeviathanMove(PackageCatalog catalog)
    {
        var context = new ObjectEditContext(catalog, catalog.ReadOriginal);
        ushort package = Package(LeviathanCompound);
        var index = new StaticCollisionRule.CollisionIndex(context, package);
        (uint Table, int Row)? single = null, several = null, sharedRow = null, rotated = null;
        foreach (uint table in catalog.Entries.Where(x => x.PackageId == package && x.Reference == 0x8080966D).Select(x => x.TagHash).Order())
        {
            byte[] data = catalog.ReadOriginal(table);
            var owners = index.TableOwners(table);
            if (owners.Count != 1) continue;
            var placement = Placement(catalog, owners[0]);
            int[] offsets = TagRows.StaticInstances(data);
            for (int i = 0; i < offsets.Length && (single is null || several is null || sharedRow is null || rotated is null); i++)
            {
                try
                {
                    var matches = index.Find(StaticCollisionRule.Compose(placement, Row(data, offsets[i])));
                    if (matches.Count == 0) continue;
                    if (index.RowsCopying(matches[0].Entity, matches[0].Instance).Count > 1) sharedRow ??= (table, i);
                    else if (matches.Count == 1) single ??= (table, i);
                    else several ??= (table, i);
                }
                catch (InvalidDataException) { rotated ??= (table, i); }
            }
        }
        Check(single is not null && several is not null, "No Leviathan row has its own collision instance.");
        foreach (var (table, row) in new[] { single!.Value, several!.Value })
        {
            byte[] bytes = catalog.ReadOriginal(table);
            int offset = TagRows.StaticInstances(bytes)[row];
            var stored = TagRows.StaticInstance(bytes, offset);
            var turn = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.7f) * ObjectRows.Rotation(stored.Rotation));
            var edit = Edit(bytes, table, row, offset, stored.Translation + new Vector3(3, -2, 1), turn, 2.5f);
            var (_, moved) = CheckEdit(catalog, [edit]);
            Console.WriteLine($"PASS collision-rule-move table={table:X8} row={row} instances={moved.Count} "
                + $"compound={moved[0].Entity.Owner.CompoundTag:X8} moved=position,rotation,scale tree=encloses "
                + "bytes=instances,nodes,domain deterministic=yes");
        }

        string sharedText = "none-found", rotatedText = "none-found";
        if (sharedRow is var (sharedTable, shared))
        {
            byte[] sharedBytes = catalog.ReadOriginal(sharedTable);
            int sharedOffset = TagRows.StaticInstances(sharedBytes)[shared];
            var alone = Edit(sharedBytes, sharedTable, shared, sharedOffset, scale: 2);
            Throws<InvalidDataException>(() => ObjectEditCompiler.Default.Compile([alone], catalog.ReadOriginal, catalog));
            sharedText = $"{sharedTable:X8}/{shared}=refused";
        }
        if (rotated is var (rotatedTable, rotatedIndex))
        {
            byte[] rotatedBytes = catalog.ReadOriginal(rotatedTable);
            int rotatedOffset = TagRows.StaticInstances(rotatedBytes)[rotatedIndex];
            var mismatch = Edit(rotatedBytes, rotatedTable, rotatedIndex, rotatedOffset, scale: 2);
            Throws<InvalidDataException>(() => ObjectEditCompiler.Default.Compile([mismatch], catalog.ReadOriginal, catalog));
            rotatedText = $"{rotatedTable:X8}/{rotatedIndex}=refused";
        }
        Console.WriteLine($"PASS collision-rule-refusals shared-alone={sharedText} rotation-or-scale-mismatch={rotatedText}");
    }

    /**
     * Compiles the edits twice and checks the moved instances, the refit tree and changed bytes.
     * @return The compiled assets and the moved instances.
     */
    private static (IReadOnlyDictionary<uint, byte[]> Assets, IReadOnlyList<(StaticCollisionRule.Entity Entity, int Instance)> Moved)
        CheckEdit(PackageCatalog catalog, ObjectEdit[] edits)
    {
        var compiled = ObjectEditCompiler.Default.Compile(edits, catalog.ReadOriginal, catalog);
        var again = ObjectEditCompiler.Default.Compile(edits.Reverse().ToArray(), catalog.ReadOriginal, catalog);
        Check(compiled.Assets.Keys.SequenceEqual(again.Assets.Keys)
            && compiled.Assets.All(x => x.Value.SequenceEqual(again.Assets[x.Key])), "The collision rule is not deterministic.");
        var edit = edits[0];
        uint table = edit.Target.SourceTag;
        var context = new ObjectEditContext(catalog, catalog.ReadOriginal);
        var index = new StaticCollisionRule.CollisionIndex(context, Package(table));
        var ownerPlacement = Placement(catalog, index.TableOwners(table).Single());
        var installed = StaticCollisionRule.Compose(ownerPlacement, Row(catalog.ReadOriginal(table), edit.Target.Offset));
        var edited = StaticCollisionRule.Compose(ownerPlacement, Row(compiled.Assets[table], edit.Target.Offset));
        var matches = index.Find(installed);
        Check(matches.Count > 0, "The edited row has no collision instance.");
        foreach (var group in matches.GroupBy(x => x.Entity.Owner.CompoundTag))
        {
            var entity = group.First().Entity;
            uint tag = entity.Owner.CompoundTag;
            Check(compiled.Edits.Single(x => x.Edit.Key == edit.Key).Tags.Contains(tag), "The edit does not list the compound it moved.");
            byte[] before = catalog.ReadOriginal(tag), after = compiled.Assets[tag];
            Check(before.Length == after.Length, "The compound packfile changed size.");
            var compound = StaticCompoundShape.Parse(HavokPackfile.Parse(after));
            Check(compound.NodeCount == 2 * compound.Instances.Count - 1, "The node count changed.");
            var movedSet = group.Select(x => x.Instance).ToHashSet();
            foreach (int instance in movedSet)
            {
                var moved = compound.Instances[instance];
                var world = StaticCollisionRule.Compose(entity.Placement.Placement with { Scale = Vector3.One }, moved.Transform);
                Check(StaticCollisionRule.Same(world, edited), "The compound instance does not hold the edited world transform.");
                Check(moved.Flags == (StaticCompoundShape.FlagBase
                    | StaticCompoundShape.Flags(moved.Transform, (entity.Compound.Instances[instance].Flags & 1) != 0)),
                    "The instance flags do not follow the new transform.");
            }

            // Only the moved instances, the node array and the domain may change.
            var file = entity.Compound.File;
            var instanceStarts = movedSet.Select(i => file.Absolute(entity.Compound.Instances[i].Offset)).ToArray();
            int nodeStart = file.Absolute(entity.Compound.NodeData), domainStart = file.Absolute(entity.Compound.DomainOffset);
            for (int i = 0; i < before.Length; i++)
            {
                bool allowed = instanceStarts.Any(start => i >= start && i < start + StaticCompoundShape.InstanceSize)
                    || (i >= nodeStart && i < nodeStart + compound.NodeCount * CompoundTree.NodeSize)
                    || (i >= domainStart && i < domainStart + 12) || (i >= domainStart + 16 && i < domainStart + 28);
                if (!allowed) Check(before[i] == after[i], $"The rule changed byte 0x{i:X} outside its fields.");
            }

            var oldLeaves = entity.Compound.LeafBoxes();
            var newLeaves = compound.LeafBoxes();
            for (int i = 0; i < newLeaves.Length; i++)
            {
                if (!movedSet.Contains(i))
                {
                    Check(newLeaves[i].Contains(oldLeaves[i]), $"Leaf {i} no longer encloses its installed box.");
                    continue;
                }
                var moved = compound.Instances[i];
                var childFile = moved.ChildObject is null
                    ? HavokPackfile.Parse(catalog.ReadOriginal(entity.Owner.ImportTag(moved.ImportIndex))) : compound.File;
                var (min, max) = CollisionShapes.Bounds(childFile, moved.ChildObject ?? childFile.RootOffset);
                Check(newLeaves[i].Contains(Moved(min, max, moved.Transform)), "The moved leaf does not enclose its shape.");
            }
        }
        return (compiled.Assets, matches);
    }

    private static CompoundBox Moved(Vector3 min, Vector3 max, QsTransform transform)
    {
        var box = new CompoundBox(double.MaxValue, double.MaxValue, double.MaxValue, double.MinValue, double.MinValue, double.MinValue);
        for (int corner = 0; corner < 8; corner++)
        {
            var point = new Vector3((corner & 1) == 0 ? min.X : max.X, (corner & 2) == 0 ? min.Y : max.Y, (corner & 4) == 0 ? min.Z : max.Z);
            var p = StaticCollisionRule.Rotate(transform.Rotation, point * transform.Scale) + transform.Translation;
            box = box.Union(new CompoundBox(p.X, p.Y, p.Z, p.X, p.Y, p.Z));
        }
        return box;
    }

    private static ObjectEdit Edit(byte[] table, uint tag, int index, int offset, Vector3? position = null,
        Quaternion? rotation = null, float? scale = null)
    {
        var target = new ObjectEditTarget(tag, ObjectEditLayout.StaticInstance, offset, 48, index, 0,
            BinaryDelta.Hash(table.AsSpan(offset, 48)));
        return new ObjectEdit(ObjectEditKind.PlacementTransform, target, "Collision check",
            position is { } p ? [p.X, p.Y, p.Z] : null, rotation is { } q ? [q.X, q.Y, q.Z, q.W] : null, scale);
    }

    private static QsTransform Row(byte[] data, int row) => TagRows.StaticInstance(data, row);

    private static QsTransform Placement(PackageCatalog catalog, PlacedPayload owner) =>
        TagRows.Placement(catalog.ReadOriginal(owner.ListTag), owner.Row);

    private static void Near(CompoundBox box, double[] min, double[] max)
    {
        double[] actual = [box.MinX, box.MinY, box.MinZ, box.MaxX, box.MaxY, box.MaxZ];
        double[] expected = [.. min, .. max];
        for (int i = 0; i < 6; i++) Check(Math.Abs(actual[i] - expected[i]) < 1e-4, "A leaf box differs from the reference.");
    }

    private static string TriangleHash(CollisionShape shape)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[12];
        // The reference hashes cover mesh triangles; custom primitive hulls follow them.
        foreach (int index in shape.Indices.Take(3 * shape.MeshTriangleCount))
        {
            var v = shape.Vertices[index];
            BinaryPrimitives.WriteSingleLittleEndian(buffer, v.X);
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(4), v.Y);
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(8), v.Z);
            sha.AppendData(buffer);
        }
        return Hex(sha.GetHashAndReset());
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static void Add(SortedDictionary<string, int> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
}
