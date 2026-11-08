using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;
using static Checks;

internal static class ImportAdmissionChecks
{
    private const uint OwnerTag = 0x80A00000, CompoundTag = OwnerTag + 1, ShapeTag = OwnerTag + 2;
    private const uint MaterialTag = OwnerTag + 3, NewMaterialTag = OwnerTag + 4;

    /** Valid imports survive while malformed transforms and overflowing indices are refused. */
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "sunrise-collision-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "packages"));
        try
        {
            var box = CollisionShapes.BoxShape(Vector3.One);
            byte[] shape = CollisionPackfileWriter.WriteConvexVertices(box.Vertices, ConvexHull.Planes(box.Vertices), 0);
            var owner = new CollisionOwnerRows { Compound = CompoundTag };
            owner.Materials.Add(MaterialTag); owner.Shapes.Add(ShapeTag); owner.RunStarts.Add(0); owner.Slots.Add(0);
            PackageWriter.WriteNew(Path.Combine(root, "packages", "w64_collision_admission_0100_0.pkg"), 0x100,
                [new(StaticCollisionReader.OwnerClass, 8 << 9, owner.Write()),
                 new(uint.MaxValue, (26 << 9) | (7 << 6), Compound()),
                 new(uint.MaxValue, (26 << 9) | (7 << 6), shape),
                 new(CollisionOwnerRows.MaterialClass, 8 << 9, [0]),
                 new(CollisionOwnerRows.MaterialClass, 8 << 9, [1])]);
            using var catalog = PackageCatalog.Open(root, cacheOptions: new() { Enabled = false });
            var scene = new CollisionSceneLoader(catalog).Load(new SceneDocument { TagHash = CompoundTag });
            Check(scene.Placements.Count == 1, "The collision admission fixture must have one placement.");
            var old = scene.Placements[0];
            CollisionImportPlan Plan(Matrix4x4 world, bool added = false) => CollisionImport.Plan(catalog, scene,
                new([new(CompoundTag, 0, ShapeTag, scene.Pieces[0].Offset, world) { Added = added }], [], new Dictionary<uint, string>()),
                catalog.ReadOriginal);
            var failures = new List<string>();
            void Refused(string name, Action action)
            {
                try { action(); failures.Add(name); }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException) { }
            }
            var nan = old.World; nan.M41 = float.NaN;
            var infinite = old.World; infinite.M11 = float.PositiveInfinity;
            var perspective = old.World; perspective.M14 = .1f;
            var shear = old.World; shear.M12 = .01f;
            Refused("nan", () => Plan(nan));
            Refused("infinity", () => Plan(infinite));
            Refused("perspective", () => Plan(perspective));
            Refused("shear", () => Plan(shear));
            Refused("copied-shear", () => Plan(shear, true));
            Check(Plan(old.World).ObjectEdits.Count == 0, "An unchanged collision instance produced an edit.");
            var moved = Matrix4x4.CreateScale(1.5f) * Matrix4x4.CreateRotationZ(.4f) * Matrix4x4.CreateTranslation(3, -2, 1);
            var plan = Plan(moved);
            Check(plan.ObjectEdits.Count == 1 && plan.ObjectEdits[0].Position!.SequenceEqual([3f, -2f, 1f]),
                "A valid collision transform was lost.");
            var originalScene = scene;
            var frame = Matrix4x4.CreateScale(1.002f) * Matrix4x4.CreateFromYawPitchRoll(.026f, .034f, .038f);
            scene = new CollisionScene();
            scene.Pieces.AddRange(originalScene.Pieces);
            scene.Entities.Add(new CollisionEntity { Index = 0, Source = CollisionSource.Asset, Tag = OwnerTag, Frame = frame });
            scene.Placements.Add(old with { World = old.Local.ToMatrix() * frame });
            var localMove = Matrix4x4.CreateTranslation(3, -2, 1);
            var framed = Plan(localMove * frame);
            Check(framed.ObjectEdits.Count == 1
                && Vector3.Distance(ObjectEditCompiler.Vector(framed.ObjectEdits[0].Position!), localMove.Translation) < 1e-4f,
                "A valid rotated/scaled frame lost its local movement.");
            var newFramed = new CollisionImportInput([new(CompoundTag, -1, 0, 0, localMove * frame)
                { Added = true, NewShape = new("framed cube", box.Vertices, box.Indices, true, MaterialTag) }], [],
                new Dictionary<uint, string>());
            var framedPlan = CollisionImport.Plan(catalog, scene, newFramed, catalog.Read, new CollisionShapePackage(0xAA0, 0));
            var framedCompound = StaticCompoundShape.Parse(HavokPackfile.Parse(framedPlan.Assets[CompoundTag]));
            Check(Vector3.Distance(framedCompound.Instances[^1].Transform.Translation, localMove.Translation) < 1e-4f,
                "A valid rotated/scaled frame lost its new shape placement.");
            scene = originalScene;
            owner.Materials.Clear();
            owner.Materials.AddRange(Enumerable.Repeat(MaterialTag, short.MaxValue + 1));
            byte[] fullOwner = owner.Write();
            catalog.ReadOverride = tag => tag == OwnerTag ? fullOwner : null;
            var newShape = new CollisionNewShape("cube", box.Vertices, box.Indices, true, NewMaterialTag);
            var add = new CollisionImportInput([new(CompoundTag, -1, 0, 0, Matrix4x4.CreateTranslation(0, 0, 3))
                { Added = true, NewShape = newShape }], [], new Dictionary<uint, string>());
            Refused("material-index-overflow", () => CollisionImport.Plan(catalog, scene, add, catalog.Read,
                new CollisionShapePackage(0xAA0, 0)));
            owner.Materials[^1] = NewMaterialTag;
            fullOwner = owner.Write();
            var boundary = CollisionImport.Plan(catalog, scene, add, catalog.Read, new CollisionShapePackage(0xAA0, 0));
            var boundaryOwner = CollisionOwnerRows.Read(boundary.Assets[OwnerTag]);
            Check(boundaryOwner.Slots[^1] == short.MaxValue && boundaryOwner.Material(1, 0) == NewMaterialTag,
                "The last representable material index did not resolve its material.");
            var baked = add with { Placements = [add.Placements[0] with { World = shear }] };
            var bakedPlan = CollisionImport.Plan(catalog, scene, baked, catalog.Read, new CollisionShapePackage(0xAA0, 0));
            var bakedFile = HavokPackfile.Parse(bakedPlan.AddedShapes.Single().Data);
            var bakedShape = CollisionShapes.Decode(bakedFile, bakedFile.RootOffset);
            var expectedVertices = box.Vertices.Select(vertex => Vector3.Transform(vertex, shear)).ToArray();
            Check(bakedShape.Vertices.Length == expectedVertices.Length
                && expectedVertices.All(vertex => bakedShape.Vertices.Any(actual => Vector3.Distance(vertex, actual) < 1e-4f)),
                "A new shape did not preserve its sheared vertices.");
            var invalidNew = add with { Placements = [add.Placements[0] with { World = nan }] };
            Refused("new-shape-nan", () => CollisionImport.Plan(catalog, scene, invalidNew, catalog.Read,
                new CollisionShapePackage(0xAA0, 0)));
            owner.Shapes.Clear(); owner.RunStarts.Clear();
            owner.Shapes.AddRange(Enumerable.Repeat(ShapeTag, ushort.MaxValue + 1));
            owner.RunStarts.AddRange(Enumerable.Repeat((short)0, ushort.MaxValue + 1));
            fullOwner = owner.Write();
            Refused("shape-index-overflow", () => CollisionImport.Plan(catalog, scene, add, catalog.Read,
                new CollisionShapePackage(0xAA0, 0)));
            Check(failures.Count == 0, "Collision import accepted: " + string.Join(", ", failures));
            Console.WriteLine("PASS collision-import-admission invalid-transform=refused instance-shear=refused new-shape-shear=baked material-limit=pass shape-limit=pass rotated-frame=pass valid-transform=pass");
        }
        finally { Directory.Delete(root, true); }
    }

    /** The child and material are supplied by the owned test package. */
    private static byte[] Compound()
    {
        var builder = new HavokPackfileBuilder();
        byte[] root = new byte[0xA0]; root[0x30] = 1;
        CollisionCompoundWriter.WriteFloats(root, 0x80, -10, -10, -10); CollisionCompoundWriter.WriteFloats(root, 0x90, 10, 10, 10);
        builder.PlaceObject(root, StaticCompoundShape.ClassName);
        var instance = new CompoundInstanceRow(new byte[StaticCompoundShape.InstanceSize], "i0", null);
        instance.SetTransform(QsTransform.Identity, true);
        BinaryPrimitives.WriteUInt64LittleEndian(instance.Bytes.AsSpan(0x40), 0x10000);
        builder.Array(0x38, instance.Bytes, 1); builder.Import(0xA0 + 0x30, "i0");
        builder.Array(0x70, new byte[CompoundTree.NodeSize], 1);
        return builder.Build([(0, StaticCompoundShape.ClassName)], 0, 0);
    }
}
