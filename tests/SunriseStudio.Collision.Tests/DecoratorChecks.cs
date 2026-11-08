using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;
using static Checks;

/** Decorator instance edits on a sabre canopy: payload 0x80FAA33A, instance 42. */
internal static class DecoratorChecks
{
    private const uint SabrePayload = 0x80FAA33A;
    private const int Canopy = 42;

    internal static void Run(PackageCatalog catalog, string gameRoot)
    {
        var tags = DecoratorTags.Read(catalog, SabrePayload);
        var target = Selection(catalog, tags, SabrePayload, Canopy);
        var info = SceneEdits.Describe(catalog, target);
        Check(info.Target?.Layout == ObjectEditLayout.DecoratorInstance && info.CanScale && info.CanTransform,
            "The canopy is not editable: " + info.Reason);
        Check(Vector3.Distance(info.Position, new(-38.860f, 35.411f, 2.462f)) < 0.01f && MathF.Abs(info.Scale - 0.4133f) < 0.001f,
            "The canopy does not decode where the trunk stands.");
        Check(SceneEdits.CreateTransformEdit(catalog, target, info.Position, null, info.Scale) is null,
            "An unchanged canopy produced an edit.");

        var installedQuant = DecoratorQuantization.Read(catalog.ReadOriginal(tags.Quantization));
        var grown = SceneEdits.CreateTransformEdit(catalog, target, info.Position, null, 0.5f)!;
        var inRange = ObjectEditCompiler.Default.Compile([grown], catalog.ReadOriginal, catalog);
        Check(!inRange.Assets.ContainsKey(tags.Quantization), "An in-range scale re-quantized the block.");
        var inRangeBlock = inRange.Assets[tags.Block];
        Check(Changed(catalog.ReadOriginal(tags.Block), inRangeBlock).SequenceEqual([Canopy]), "An in-range edit changed other records.");
        Check(MathF.Abs(Value(inRangeBlock, installedQuant, Canopy).Scale - 0.5f) < 1e-3f, "The in-range scale did not land.");
        Pairs(inRangeBlock, inRange.Assets[tags.Buffer]);
        Check(inRange.Assets.ContainsKey(tags.Cells), "A grown canopy kept its installed cell box.");

        // Trunk and canopy are both installed at 0.4133; 0.05 is below the quantized range.
        const float wanted = 0.05f;
        var shrunk = SceneEdits.CreateTransformEdit(catalog, target, info.Position, null, wanted)!;
        var outside = ObjectEditCompiler.Default.Compile([shrunk], catalog.ReadOriginal, catalog);
        var quant = DecoratorQuantization.Read(outside.Assets[tags.Quantization]);
        byte[] block = outside.Assets[tags.Block], installed = catalog.ReadOriginal(tags.Block);
        var value = Value(block, quant, Canopy);
        Check(MathF.Abs(value.Scale - wanted) < 1e-4f && Vector3.Distance(value.Position, info.Position) < 1e-3f,
            "The re-quantized canopy moved or kept its scale.");
        int count = Rows(block).Length;
        float worst = 0;
        for (int index = 0; index < count; index++)
        {
            if (index == Canopy) continue;
            var before = Value(installed, installedQuant, index);
            var after = Value(block, quant, index);
            worst = MathF.Max(worst, Vector3.Distance(before.Position, after.Position));
            Check(MathF.Abs(before.Scale - after.Scale) < 1e-3f && Vector3.Distance(before.XAxis, after.XAxis) < 0.02f
                && Vector3.Distance(before.ZAxis, after.ZAxis) < 0.02f, $"Re-quantizing moved instance {index}.");
        }
        Check(worst < 0.01f, "Re-quantizing moved another instance by more than a centimetre.");
        Pairs(block, outside.Assets[tags.Buffer]);
        // The canopy shrinks toward its origin at the ground; its own cell box follows it down.
        byte[] cells = outside.Assets[tags.Cells], installedCells = catalog.ReadOriginal(tags.Cells);
        int box = TagRows.Boxes(cells)[Canopy], installedBox = TagRows.Boxes(installedCells)[Canopy];
        var (low, high) = TagRows.Box(cells, box);
        Matrix4x4.Invert(Value(installed, installedQuant, Canopy).Transform, out var inverse);
        var (installedLow, installedHigh) = TagRows.Box(installedCells, installedBox);
        var moved = BoxBounds.Transform(installedLow, installedHigh, inverse * value.Transform);
        Check(low == moved.Min && high == moved.Max && high.Z < installedHigh.Z,
            "The canopy's own cell box did not shrink with it.");
        Check(outside.Edits.Single().Tags.Order().SequenceEqual(new[] { tags.Quantization, tags.Buffer, tags.Block, tags.Cells, tags.Payload }
            .Where(tag => outside.Assets.ContainsKey(tag)).Order()), "The edit lists the wrong tags.");
        Check(ObjectEditCompiler.Default.Normalize(shrunk with { Scale = info.Scale }, catalog.ReadOriginal, catalog) is null,
            "Normalize kept an unchanged scale.");

        Build(catalog, shrunk, outside, gameRoot);

        var sharedPayload = SharedPayload(catalog);
        var sharedTags = DecoratorTags.Read(catalog, sharedPayload);
        var sharedTarget = Selection(catalog, sharedTags, sharedPayload, 0);
        var sharedInfo = SceneEdits.Describe(catalog, sharedTarget);
        var sharedEdit = SceneEdits.CreateTransformEdit(catalog, sharedTarget, sharedInfo.Position + Vector3.UnitZ, null, sharedInfo.Scale)!;
        Throws<NotSupportedException>(() => ObjectEditCompiler.Default.Compile([sharedEdit], catalog.ReadOriginal, catalog));
        Console.WriteLine($"PASS decorator-edit payload={SabrePayload:X8} instance={Canopy} in-range=one-record "
            + $"scale={info.Scale:0.####}->{value.Scale:0.####} requantized others<{worst * 1000:0.##}mm shared={sharedPayload:X8} refused");
    }

    /** A project holding the edit builds patches that read back as the compiled bytes. */
    private static void Build(PackageCatalog catalog, ObjectEdit edit, ObjectEditCompilation compiled, string gameRoot)
    {
        string output = Path.Combine(Path.GetTempPath(), "sunrise-decorator-edit-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var project = StudioProject.Create(Path.Combine(output, "project"), "Canopy scale");
            project.ObjectEditCompiler = ObjectEditCompiler.Default;
            Check(project.RecordObjectEdit(edit, catalog), "The project did not record the canopy edit.");
            var result = ProjectBuilder.Build(project, catalog, Path.Combine(output, "build"));
            string install = Path.Combine(output, "install");
            Directory.CreateDirectory(Path.Combine(install, "packages"));
            Directory.CreateDirectory(Path.Combine(install, "bin", "x64"));
            File.Copy(Path.Combine(gameRoot, "bin", "x64", "oo2core_3_win64.dll"), Path.Combine(install, "bin", "x64", "oo2core_3_win64.dll"));
            var families = compiled.Assets.Keys.Select(tag => { catalog.TryGetEntry(tag, out var entry); return entry!.PackageName; })
                .Select(name => name[..name.LastIndexOf('_')]).Distinct().ToArray();
            foreach (string path in families.SelectMany(family => Directory.EnumerateFiles(catalog.PackageDirectory, family + "_*.pkg"))
                .Concat(result.PackageFiles))
                File.Copy(path, Path.Combine(install, "packages", Path.GetFileName(path)), true);
            using var rebuilt = PackageCatalog.Open(install, Path.Combine(gameRoot, "destiny2.exe"));
            foreach (var (tag, bytes) in compiled.Assets)
                Check(rebuilt.Read(tag).AsSpan().SequenceEqual(bytes), $"The build does not hold {tag:X8}.");
            Console.WriteLine($"PASS decorator-build families=[{string.Join(",", families)}] tags={compiled.Assets.Count} read-back=equal");
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, true);
        }
    }

    /** The mesh instance the scene loader makes for one record. */
    private static MeshInstance Selection(PackageCatalog catalog, DecoratorTags tags, uint payload, int index)
    {
        byte[] block = catalog.Read(tags.Block);
        return new MeshInstance
        {
            Mesh = new MeshData(),
            ObjectPlacement = new SceneObjectPlacement(payload, index, Matrix4x4.Identity) { EntryIndex = index },
            NativeInstanceData = block.AsMemory(Rows(block)[index], DecoratorRecords.Size)
        };
    }

    private static int[] Rows(byte[] block) =>
        TagRows.Offsets<SDecoratorBlock, SDecoratorRecord>(block, nameof(SDecoratorBlock.Records), DecoratorTags.RecordClass);

    private static DecoratorInstance Value(byte[] block, DecoratorQuantization quant, int index) =>
        DecoratorRecords.Decode(block.AsSpan(Rows(block)[index], DecoratorRecords.Size), quant);

    private static IEnumerable<int> Changed(byte[] before, byte[] after)
    {
        int[] rows = Rows(before);
        return Enumerable.Range(0, rows.Length).Where(index =>
            !before.AsSpan(rows[index], DecoratorRecords.Size).SequenceEqual(after.AsSpan(rows[index], DecoratorRecords.Size)));
    }

    /** The buffer rounds where the block truncates: equal, or one step away from zero. */
    private static void Pairs(byte[] block, byte[] buffer)
    {
        int[] rows = Rows(block);
        for (int index = 0; index < rows.Length; index++)
        {
            for (int lane = 0; lane < 4; lane++)
            {
                short cpu = BinaryPrimitives.ReadInt16LittleEndian(block.AsSpan(rows[index] + lane * 2));
                short gpu = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(index * 16 + lane * 2));
                Check(gpu == cpu || gpu - cpu == Math.Sign(cpu) || cpu == 0 && Math.Abs(gpu) == 1, $"Record {index} lane {lane} breaks the buffer pairing.");
            }
            foreach (int at in new[] { 8, 9, 10, 11, 12, 13, 14, 15 })
                Check(buffer[index * 16 + at] - block[rows[index] + at] is 0 or 1, $"Record {index} byte {at} breaks the buffer pairing.");
        }
    }

    /** A payload whose quantization record another payload also names. */
    private static uint SharedPayload(PackageCatalog catalog)
    {
        var byQuant = catalog.Entries.Where(entry => entry.Reference == DecoratorTags.PayloadClass).Select(entry => entry.TagHash).Distinct()
            .Select(payload => (Payload: payload, Tags: DecoratorTags.Read(catalog, payload)))
            .GroupBy(item => item.Tags.Quantization).First(group => group.Count() > 1);
        return byQuant.First().Payload;
    }
}
