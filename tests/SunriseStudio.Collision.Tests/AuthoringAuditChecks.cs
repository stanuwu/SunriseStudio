using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene.Collision;
using SunriseStudio.Tags.Generated;
using static Checks;

/** Partial typed edits preserve raw components; unsupported trigger frames refuse admission. */
internal static class AuthoringAuditChecks
{
    private const uint First = 0x80A00000, OwnerTag = First + 1, CompoundTag = First + 2,
        ShapeTag = First + 3, TableTag = First + 4, TriggerTag = First + 5, BoundsTag = First + 6;

    /** Each test estate is private and disposed before its directory is removed. */
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "sunrise-authoring-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "packages"));
        try
        {
            byte[] shape = CollisionPackfileWriter.WriteCompressedMesh(CompressedMeshEncoder.Encode(
                new CompressedMeshSource([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [0, 1, 2])).Mesh);
            byte[] compound = Compound();
            var (table, tableRow) = Table();
            var (trigger, triggerRow) = Trigger();
            uint havok = (26u << 9) | (7u << 6);
            PackageWriter.WriteNew(Path.Combine(root, "packages", "w64_authoring_audit_0100_0.pkg"), 0x100,
                [new(StaticCollisionReader.ObjectListClass, 8 << 9, List()),
                 new(StaticCollisionReader.OwnerClass, 8 << 9, Owner()),
                 new(uint.MaxValue, havok, compound), new(uint.MaxValue, havok, shape),
                 new(0x8080966D, 8 << 9, table), new(0x80809C36, 8 << 9, trigger),
                 new(0x80809671, 8 << 9, Bounds())]);
            using var catalog = PackageCatalog.Open(root, cacheOptions: new() { Enabled = false });
            var failures = new List<string>();
            foreach (var layout in new[] { ObjectEditLayout.CollisionInstance, ObjectEditLayout.StaticInstance })
                foreach (string component in new[] { "position", "rotation", "scale" })
                    RunCase($"{layout}-{component}", () => Partial(root, catalog, compound, table, shape, tableRow, layout, component), failures);
            RunCase("finite-trigger-placement", () => TriggerFrames(catalog, trigger, triggerRow), failures);
            RunCase("finite-large-domain", LargeDomain, failures);
            Check(failures.Count == 0, string.Join("; ", failures));
            Console.WriteLine("PASS authoring-audit raw-components=6 project-reopen=pass tree-refit=pass trigger-nonfinite=21 large-domain=finite quantization=bounded overflow=refused source=unchanged");
        }
        finally { Directory.Delete(root, true); }
    }

    /** Runs one case and records its failure, so every case runs and reports. */
    private static void RunCase(string name, Action action, List<string> failures)
    {
        try { action(); }
        catch (Exception error)
        {
            failures.Add($"{name}: {error.Message}");
            Console.WriteLine($"FAIL authoring-audit case={name} reason={error.Message}");
        }
    }

    /** A real project layers one typed component over a raw collider, then reopens and compiles it. */
    private static void Partial(string root, PackageCatalog catalog, byte[] original, byte[] table, byte[] shape,
        int tableRow, ObjectEditLayout layout, string component)
    {
        var installed = StaticCompoundShape.Parse(HavokPackfile.Parse(original));
        int at = installed.File.Absolute(installed.Instances[0].Offset);
        var stored = new QsTransform(new(3, -4, 5), Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(.4f, .2f, .6f)), new(-2, 3, 4));
        byte[] raw = original.ToArray();
        CollisionCompoundWriter.WriteFloats(raw, at, stored.Translation.X, stored.Translation.Y, stored.Translation.Z);
        CollisionCompoundWriter.WriteFloats(raw, at + 0x10, stored.Rotation.X, stored.Rotation.Y, stored.Rotation.Z, stored.Rotation.W);
        CollisionCompoundWriter.WriteFloats(raw, at + 0x20, stored.Scale.X, stored.Scale.Y, stored.Scale.Z);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(at + 12), StaticCompoundShape.FlagBase | StaticCompoundShape.Flags(stored, false));
        var position = new Vector3(30, 40, 50);
        var rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(-.3f, .7f, .4f));
        const float scale = 1.75f;
        var expected = component switch
        {
            "position" => stored with { Translation = position },
            "rotation" => stored with { Rotation = rotation },
            _ => stored with { Scale = new(scale) }
        };
        uint tag = layout == ObjectEditLayout.CollisionInstance ? CompoundTag : TableTag;
        int offset = layout == ObjectEditLayout.CollisionInstance ? at : tableRow;
        int length = layout == ObjectEditLayout.CollisionInstance ? StaticCompoundShape.InstanceSize : 48;
        var target = new ObjectEditTarget(tag, layout, offset, length, 0, 0,
            BinaryDelta.Hash((layout == ObjectEditLayout.CollisionInstance ? original : table).AsSpan(offset, length)));
        var edit = new ObjectEdit(ObjectEditKind.PlacementTransform, target, component,
            Position: component == "position" ? [position.X, position.Y, position.Z] : null,
            Rotation: component == "rotation" ? [rotation.X, rotation.Y, rotation.Z, rotation.W] : null,
            Scale: component == "scale" ? scale : null);
        var project = StudioProject.Create(Path.Combine(root, $"{layout}-{component}"), "Partial collision edit");
        project.ObjectEditCompiler = ObjectEditCompiler.Default;
        project.ApplyEdit(CompoundTag, raw, catalog);
        Check(project.RecordObjectEdit(edit, catalog), "The typed edit was not recorded.");
        project = StudioProject.Open(project.DirectoryPath);
        project.ObjectEditCompiler = ObjectEditCompiler.Default;
        byte[] result = project.ReadCompiled(CompoundTag, catalog);
        var changed = StaticCompoundShape.Parse(HavokPackfile.Parse(result));
        var actual = changed.Instances[0].Transform;
        bool sameRotation = component == "rotation"
            ? MathF.Abs(Quaternion.Dot(actual.Rotation, expected.Rotation)) > 1 - 1e-6f
            : actual.Rotation == expected.Rotation;
        Check(actual.Translation == expected.Translation && sameRotation && actual.Scale == expected.Scale,
            $"Omitted raw components changed: expected {expected}, actual {actual}.");
        Check(changed.Instances[0].Flags == (StaticCompoundShape.FlagBase | StaticCompoundShape.Flags(expected, false)),
            "Transform flags do not describe the retained scale/rotation.");
        var child = HavokPackfile.Parse(shape);
        var (min, max) = CollisionShapes.Bounds(child, child.RootOffset);
        var matrix = expected.ToMatrix();
        var points = Enumerable.Range(0, 8).Select(i => Vector3.Transform(new Vector3(
            (i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z), matrix)).ToArray();
        var bounds = CompoundBox.From(points.Aggregate(Vector3.Min), points.Aggregate(Vector3.Max));
        Check(changed.LeafBoxes()[0].Contains(bounds, 1e-4) && changed.ReadNodes().Length == installed.ReadNodes().Length,
            "The refitted tree misses the retained raw transform or changed topology.");
        Check(catalog.ReadOriginal(CompoundTag).AsSpan().SequenceEqual(original)
            && catalog.ReadOriginal(TableTag).AsSpan().SequenceEqual(table), "Compilation changed an installed source.");
        Check(project.ReadAsset(CompoundTag, catalog).AsSpan().SequenceEqual(raw), "Compilation changed the raw delta layer.");
    }

    /** Each source placement lane must be finite before its identity tolerance is considered. */
    private static void TriggerFrames(PackageCatalog catalog, byte[] original, int row)
    {
        var target = new ObjectEditTarget(TriggerTag, ObjectEditLayout.TriggerVolume, row, 0x120, 0, 0,
            BinaryDelta.Hash(original.AsSpan(row, 0x120)));
        ObjectRows.Locate(catalog, target, original);
        (string Member, int Lane)[] lanes = [("SpawnEntry.Rotation", 0), ("SpawnEntry.Rotation", 4), ("SpawnEntry.Rotation", 8),
            ("SpawnEntry.Rotation", 12), ("SpawnEntry.Position", 0), ("SpawnEntry.Position", 4), ("SpawnEntry.Position", 8)];
        foreach (var (member, lane) in lanes)
            foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                byte[] raw = original.ToArray(); ObjectRows.Member<STriggerVolumeRow, float>(raw, row + lane, member) = invalid;
                Throws<NotSupportedException>(() => ObjectRows.Locate(catalog, target, raw));
            }
    }

    /** Finite large coordinates keep bounded decoded error; unrepresentable domains are refused. */
    private static void LargeDomain()
    {
        Vector3[] vertices = [new(1e38f, 0, 0), new(1e38f, 1, 0), new(1e38f, 0, 1)];
        var encoded = CompressedMeshEncoder.Encode(new(vertices, [0, 1, 2]));
        Verify(encoded.Mesh);
        var ordinary = CompressedMeshEncoder.Encode(new([Vector3.Zero, Vector3.UnitY, Vector3.UnitZ], [0, 1, 2])).Mesh;
        Verify(CollisionShapeEdits.MoveVertices(ordinary, vertices));
        Vector3[] unrepresentable = vertices.Select(v => v with { X = float.MaxValue }).ToArray();
        Throws<InvalidDataException>(() => CompressedMeshEncoder.Encode(new(unrepresentable, [0, 1, 2])));
        Throws<InvalidDataException>(() => CollisionShapeEdits.MoveVertices(ordinary, unrepresentable));

        void Verify(CompressedMesh mesh)
        {
            static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
            Check(Finite(mesh.DomainMin) && Finite(mesh.DomainMax), "Finite input emitted an infinite mesh domain.");
            Check(mesh.SharedVertices.Length == 0 && mesh.Sections.Count == 1, "The fixture must exercise packed vertices.");
            var file = HavokPackfile.Parse(CollisionPackfileWriter.WriteCompressedMesh(mesh));
            var decoded = CollisionShapes.Decode(file, file.RootOffset);
            Check(decoded.Indices.Length == 3, "The large triangle did not survive serialization.");
            for (int index = 0; index < 3; index++)
            {
                var expected = vertices[index];
                var actual = decoded.Vertices[decoded.Indices[index]];
                Check(Finite(actual), "A decoded large-coordinate vertex is not finite.");
                double[] values = [expected.X, expected.Y, expected.Z];
                double[] errors = [Math.Abs((double)expected.X - actual.X), Math.Abs((double)expected.Y - actual.Y), Math.Abs((double)expected.Z - actual.Z)];
                for (int axis = 0; axis < 3; axis++)
                {
                    double tolerance = mesh.Sections[0].Codec[axis + 3] / 2.0
                                       + 4.0 * Math.Max(1, Math.Abs(values[axis])) * Math.Pow(2, -23);
                    Check(errors[axis] <= tolerance, "The large-coordinate vertex exceeds its codec error bound.");
                }
            }
        }
    }

    /** One imported triangle instance has a broad initial tree for later refitting. */
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

    /** The compound import resolves through the owner's one-entry tag array. */
    private static byte[] Owner()
    {
        var bytes = new Bytes(0x60);
        int imports = bytes.Array(0x48, 0x80800014, 4, 1);
        bytes.U32(0x58, CompoundTag); bytes.U32(imports, ShapeTag);
        return bytes.ToArray();
    }

    /** One object list places the collider and its matching render-instance table. */
    private static byte[] List()
    {
        var bytes = new Bytes(0x30);
        int rows = bytes.Array(8, StaticCollisionReader.PlacementRowClass, 144, 2);
        for (int index = 0; index < 2; index++)
        {
            int row = rows + index * 144;
            bytes.Qts(row + 16);
            int payload = bytes.Allocate(32) + 4;
            bytes.U32(payload - 4, index == 0 ? SStaticCollisionPayload.Class : SStaticInstancesPayload.Class);
            bytes.U32(payload + 16, index == 0 ? OwnerTag : TableTag);
            bytes.I64(row + 0x78, payload - row - 0x78);
        }
        return bytes.ToArray();
    }

    /** The render row starts at the installed collider transform. */
    private static (byte[], int) Table()
    {
        var bytes = new Bytes(0xA0);
        int row = bytes.Array(0x40, 0x808071A3, 48, 1);
        bytes.Qts(row); bytes.U32(0x18, BoundsTag);
        bytes.V3(0x80, new(-1)); bytes.V3(0x90, new(1));
        return (bytes.ToArray(), row);
    }

    /** Static instance compilation retains its required paired bounds table. */
    private static byte[] Bounds()
    {
        var bytes = new Bytes(0x30);
        int row = bytes.Array(8, 0x80809673, 48, 1);
        bytes.V3(row, new(-1)); bytes.V3(row + 16, new(1));
        return bytes.ToArray();
    }

    /** The volume is owned by one typed config component and starts at identity. */
    private static (byte[], int) Trigger()
    {
        var bytes = new Bytes(0x80);
        int component = bytes.Array(0x30, 0x808091A4, 16, 1);
        int header = bytes.Allocate(16), root = bytes.Allocate(0x50);
        bytes.U32(component, (uint)(header - component)); bytes.U32(component + 8, 0x808099C8);
        bytes.U32(header, TriggerTag); bytes.U32(header + 4, 0x808099C9); bytes.I64(header + 8, root);
        int row = bytes.Array(root + 0x38, 0x808099D0, 0x120, 1);
        bytes.F32(row + 0x3C, 1); bytes.V3(row + 0xB0, new(-1)); bytes.V3(row + 0xC0, new(1));
        return (bytes.ToArray(), row);
    }

    /** Typed test arrays use independent byte writes and the public relative-pointer grammar. */
    private sealed class Bytes(int initial)
    {
        private readonly List<byte> bytes = new(new byte[initial]);
        internal byte[] ToArray() { I64(0, bytes.Count); return bytes.ToArray(); }
        internal int Allocate(int count)
        {
            while (bytes.Count % 16 != 0) bytes.Add(0);
            int at = bytes.Count; bytes.AddRange(new byte[count]); return at;
        }
        internal int Array(int field, uint type, int stride, int count)
        {
            int header = Allocate(32 + stride * count);
            U32(header + 12, 0x80809FBD); I64(header + 16, count); U32(header + 24, type);
            I64(field, count); I64(field + 8, header + 16 - field - 8); return header + 32;
        }
        internal void Qts(int at) { F32(at + 12, 1); F32(at + 28, 1); }
        internal void V3(int at, Vector3 value) { F32(at, value.X); F32(at + 4, value.Y); F32(at + 8, value.Z); }
        internal void F32(int at, float value) => Put(at, BitConverter.GetBytes(value));
        internal void U32(int at, uint value) => Put(at, BitConverter.GetBytes(value));
        internal void I64(int at, long value) => Put(at, BitConverter.GetBytes(value));
        private void Put(int at, byte[] data) { for (int i = 0; i < data.Length; i++) bytes[at + i] = data[i]; }
    }
}
