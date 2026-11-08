using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;
using static Checks;

internal static class SharedShapeChecks
{
    /** Owner replacements, removals and package changes must not leave stale reverse links. */
    internal static void Synthetic()
    {
        const uint first = 0x80A00000, shapeA = first + 4, shapeB = first + 5;
        uint added = TagHashes.Make(0xAA0, 0);
        string root = Path.Combine(Path.GetTempPath(), "sunrise-collision-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "packages"));
        string path = Path.Combine(root, "packages", "w64_collision_index_0100_0.pkg");
        try
        {
            byte[] ownerA = Owner(first + 2, shapeA), ownerB = Owner(first + 3, shapeB);
            PackageWriter.WriteNew(path, 0x100,
                [new(StaticCollisionReader.OwnerClass, 8 << 9, ownerA), new(StaticCollisionReader.OwnerClass, 8 << 9, ownerB),
                 new(0, 8 << 9, [1]), new(0, 8 << 9, [2]), new(0, 8 << 9, [3]), new(0, 8 << 9, [4])]);
            using var catalog = PackageCatalog.Open(root, cacheOptions: new() { Enabled = false });
            uint[] Users(IReadOnlyCollection<uint>? changes) => CollisionReferenceIndex.Find(catalog,
                new HashSet<uint> { shapeA }, catalog.Read, changes).Select(owner => owner.Tag).ToArray();
            Check(Users([]).SequenceEqual([first]), "The installed reverse link is missing.");
            long reads = catalog.ReadStatistics.EntryReads;
            Check(Users([]).SequenceEqual([first]) && catalog.ReadStatistics.EntryReads == reads,
                "An unchanged reference query rescanned installed owner payloads.");
            var swapped = new Dictionary<uint, byte[]> { [first] = Owner(first + 2, shapeB), [first + 1] = Owner(first + 3, shapeA) };
            catalog.SetOverlayEntries(swapped.Select(pair =>
            {
                catalog.TryGetEntry(pair.Key, out var entry);
                return new PackageOverlayEntry(entry with { Size = pair.Value.Length });
            }));
            catalog.ReadOverride = tag => swapped.GetValueOrDefault(tag);
            Check(Users(swapped.Keys.ToArray()).SequenceEqual([first + 1]), "Retargeted owners retained their old shape links.");
            catalog.SetOverlayEntries([]);
            Check(Users(null).SequenceEqual([first + 1]), "An unannotated override was treated as an installed snapshot.");
            catalog.ReadOverride = null;
            Check(Users([]).SequenceEqual([first]), "Reverting the overlay retained stale owner links.");
            var addedBytes = Owner(first + 3, shapeA);
            catalog.SetOverlayEntries([new(new PackageEntry(added, StaticCollisionReader.OwnerClass, 8, 0, addedBytes.Length,
                "w64_collision_added_0aa0_0", 0xAA0, 0) { TypeInfo = 8 << 9 })]);
            catalog.ReadOverride = tag => tag == added ? addedBytes : null;
            Check(Users([added]).SequenceEqual(new[] { first, added }.Order()), "An added owner was absent from the index.");
            catalog.ReadOverride = null; catalog.SetOverlayEntries([]);
            Check(Users([]).SequenceEqual([first]), "A removed project owner remained indexed.");
            DateTime written = File.GetLastWriteTimeUtc(path);
            using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                file.Position = file.Length - 1; int value = file.ReadByte();
                file.Position--; file.WriteByte((byte)(value ^ 1));
            }
            File.SetLastWriteTimeUtc(path, written);
            Throws<InvalidDataException>(() => Users([]));
            Console.WriteLine("PASS collision-reference-index cached=yes retarget=pass revert=pass added=pass removed=pass unknown-override=checked changed-package=refused");
        }
        finally { Directory.Delete(root, true); }
        KeyWidth();
    }

    /** More triangles within the same box still need wider child keys in every compound. */
    private static void KeyWidth()
    {
        CollisionCompoundWriter.RequireKeyCapacity(8192, 18);
        Throws<NotSupportedException>(() => CollisionCompoundWriter.RequireKeyCapacity(8192, 19));
        const uint first = 0x80A00000, meshTag = first + 4;
        string root = Path.Combine(Path.GetTempPath(), "sunrise-collision-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "packages"));
        try
        {
            var encoded = CompressedMeshEncoder.Encode(new CompressedMeshSource([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [0, 1, 2]));
            byte[] mesh = CollisionPackfileWriter.WriteCompressedMesh(encoded.Mesh), compound = Compound();
            uint type = (26u << 9) | (7u << 6);
            PackageWriter.WriteNew(Path.Combine(root, "packages", "w64_collision_keys_0100_0.pkg"), 0x100,
                [new(StaticCollisionReader.OwnerClass, 8 << 9, Owner(first + 2, meshTag)),
                 new(StaticCollisionReader.OwnerClass, 8 << 9, Owner(first + 3, meshTag)),
                 new(uint.MaxValue, type, compound), new(uint.MaxValue, type, compound), new(uint.MaxValue, type, mesh)]);
            using var catalog = PackageCatalog.Open(root, cacheOptions: new() { Enabled = false });
            var file = HavokPackfile.Parse(mesh);
            var scene = new CollisionScene();
            scene.Pieces.Add(new(0, meshTag, file.RootOffset, CollisionShapes.Decode(file, file.RootOffset)));
            var vertices = new List<Vector3>(); var indices = new List<int>();
            for (int y = 0; y <= 16; y++)
                for (int x = 0; x <= 16; x++) vertices.Add(new(x / 16f, y / 16f, 0));
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                {
                    int start = y * 17 + x;
                    indices.AddRange([start, start + 1, start + 18, start, start + 18, start + 17]);
                }
            var plan = CollisionImport.Plan(catalog, scene,
                new([], [new(meshTag, file.RootOffset, vertices, indices)], new Dictionary<uint, string>()),
                catalog.ReadOriginal, changedTags: []);
            var changedMesh = HavokPackfile.Parse(plan.Assets[meshTag]);
            int bits = CompressedMesh.BitLength(CompressedMesh.Read(changedMesh, changedMesh.RootOffset).MaxKeyValue);
            Check(bits > 1 && plan.Assets.Count == 3, "Topology growth did not update both compound key widths.");
            var original = StaticCompoundShape.Parse(HavokPackfile.Parse(compound));
            foreach (uint tag in new[] { first + 2, first + 3 })
            {
                var changed = StaticCompoundShape.Parse(HavokPackfile.Parse(plan.Assets[tag]));
                Check(changed.File.U8(changed.File.RootOffset + 0x30) == bits
                    && changed.ReadNodes().AsSpan().SequenceEqual(original.ReadNodes())
                    && changed.DomainMin == original.DomainMin && changed.DomainMax == original.DomainMax,
                    "A key-only change lost tree bytes or kept the old child width.");
            }
            Console.WriteLine($"PASS collision-child-keys previous=1 current={bits} compounds=2 bounds=unchanged capacity32=accepted capacity33=refused");
        }
        finally { Directory.Delete(root, true); }
    }

    private static byte[] Compound()
    {
        var builder = new HavokPackfileBuilder();
        byte[] root = new byte[0xA0]; root[0x30] = 1;
        CollisionCompoundWriter.WriteFloats(root, 0x80, -10, -10, -10); CollisionCompoundWriter.WriteFloats(root, 0x90, 10, 10, 10);
        builder.PlaceObject(root, StaticCompoundShape.ClassName);
        var instance = new CompoundInstanceRow(new byte[StaticCompoundShape.InstanceSize], "i0", null);
        instance.SetTransform(QsTransform.Identity, false);
        builder.Array(0x38, instance.Bytes, 1); builder.Import(0xA0 + 0x30, "i0");
        builder.Array(0x70, new byte[CompoundTree.NodeSize], 1);
        return builder.Build([(0, StaticCompoundShape.ClassName)], 0, 0);
    }

    /** One shape edit refits users outside the exported compound and saves all bytes as deltas. */
    internal static void Owned(PackageCatalog catalog)
    {
        var owners = catalog.Entries.Where(entry => entry.Reference == StaticCollisionReader.OwnerClass)
            .Select(entry => StaticCollisionReader.ReadOwner(entry.TagHash, catalog.ReadOriginal(entry.TagHash))).ToArray();
        var shared = owners.SelectMany(owner => owner.ImportTags.Distinct().Select(tag => (Tag: tag, Owner: owner)))
            .GroupBy(pair => pair.Tag).Where(group => group.Select(pair => pair.Owner.CompoundTag).Distinct().Count() > 1);
        (uint Tag, StaticCollisionOwner[] Owners, HavokPackfile File)? candidate = null;
        foreach (var group in shared)
        {
            if (!catalog.TryGetEntry(group.Key, out var entry) || entry is not { Type: 26, SubType: 7 }) continue;
            var file = HavokPackfile.Parse(catalog.ReadOriginal(group.Key));
            if (file.UserTag == 1 && file.RootClass == CollisionShapes.ConvexVertices)
            { candidate = (group.Key, group.Select(pair => pair.Owner).ToArray(), file); break; }
        }
        Check(candidate is not null, "The installed fixture has no shared convex collision shape.");
        var found = candidate!.Value;
        var scene = new CollisionSceneLoader(catalog).Load(new SceneDocument { TagHash = found.Owners[0].CompoundTag });
        var piece = scene.Pieces.Single(piece => piece.Tag == found.Tag);
        var positions = piece.Shape.Vertices.Select(vertex => vertex + new Vector3(10000, 10000, 10000)).ToArray();
        var plan = CollisionImport.Plan(catalog, scene,
            new([], [new(found.Tag, piece.Offset, positions, piece.Shape.Indices)], new Dictionary<uint, string>()),
            catalog.ReadOriginal, changedTags: []);
        var shape = HavokPackfile.Parse(plan.Assets[found.Tag]);
        var (min, max) = CollisionShapes.Bounds(shape, shape.RootOffset);
        int outside = 0, instances = 0;
        foreach (var group in found.Owners.GroupBy(owner => owner.CompoundTag))
        {
            var compound = StaticCompoundShape.Parse(HavokPackfile.Parse(plan.Assets.GetValueOrDefault(group.Key)
                ?? catalog.ReadOriginal(group.Key)));
            var leaves = compound.LeafBoxes();
            foreach (var instance in compound.Instances.Where(instance => group.Any(owner => instance.ImportIndex >= 0
                && instance.ImportIndex < owner.ImportTags.Count && owner.ImportTags[instance.ImportIndex] == found.Tag)))
            {
                Check(leaves[instance.Index].Contains(StaticCollisionRule.MovedBox(min, max, instance.Transform)),
                    $"Shared compound {group.Key:X8} instance {instance.Index} excludes the edited shape.");
                instances++;
            }
            if (group.Key != found.Owners[0].CompoundTag && plan.Assets.ContainsKey(group.Key)) outside++;
        }
        Check(outside > 0 && instances > 1, "The edit did not refit a compound outside its export.");
        string root = Path.Combine(Path.GetTempPath(), "sunrise-shared-collision-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = StudioProject.Create(root, "Shared collision");
            project.ApplyEdits(plan.Assets, catalog);
            var reopened = StudioProject.Open(project.ManifestPath);
            Check(reopened.AddedAssets.Count == 0 && reopened.Changes.Count == plan.Assets.Count
                && plan.Assets.All(pair => reopened.ReadAsset(pair.Key, catalog).AsSpan().SequenceEqual(pair.Value)),
                "The shared refit did not reopen as one set of project deltas.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"PASS collision-shared-grow shape={found.Tag:X8} outside-compounds={outside} instances={instances} delta-reopen=pass");
    }

    private static byte[] Owner(uint compound, uint shape)
    {
        byte[] bytes = new byte[132];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, bytes.Length);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(0x48), 1);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(0x50), 0x20);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x58), compound);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x6C), 0x80809FBD);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(0x70), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x78), 0x80800014);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x80), shape);
        return bytes;
    }
}
