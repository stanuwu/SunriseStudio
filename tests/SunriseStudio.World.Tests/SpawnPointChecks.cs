using System.Numerics;
using System.Text.Json.Nodes;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

internal static class SpawnPointChecks
{
    internal const uint Tag = 0x80A00000;
    private const int Stride = 48;
    private static int checks;

    private static int Field => TagView.FieldOffset<SSpawnPoints>(nameof(SSpawnPoints.SpawnPoints));

    /** A bank with two installed rows that end the tag; each row's unknown bytes are distinct. */
    internal static byte[] Bank()
    {
        int header = TagView.BodyAfter(24, TagArray.HeaderAlignment), data = header + TagArray.HeaderSize;
        byte[] bytes = new byte[data + 2 * Stride];
        TagArray.WriteHeader(bytes, header, 2, SSpawnPointRow.Class);
        TagArray.WriteField(bytes, Field, 2, header);
        for (int i = 0; i < 2; i++)
        {
            int at = data + i * Stride;
            ref var row = ref TagView.Edit<SSpawnPointRow>(bytes, at);
            row.Rotation = Quaternion.Identity;
            row.Position = new(i + 1, 0, 0);
            row.Unk1c = 7 + i;
            row.SpawnNameIdentity = 0x2EA8FB98u + (uint)i;
            for (int b = 36; b < Stride; b++) bytes[at + b] = (byte)(0xA0 + i * 16 + b);
        }
        // Installed banks carry their length in the first u64.
        TagView.WriteSizeWord(bytes);
        return bytes;
    }

    internal static void Installation(string folder, byte[] bytes)
    {
        Directory.CreateDirectory(Path.Combine(folder, "packages"));
        PackageWriter.WriteNew(Path.Combine(folder, "packages", "w64_spawns_0100_0.pkg"), 0x100,
            [new AuthoredPackageEntry(SSpawnPoints.Class, 0x100A, bytes)]);
    }

    private static int[] Rows(byte[] bank) => TagView.RowOffsets(bank, TagView.Rows(bank,
        in TagView.At<TagArray<SSpawnPointRow>>(bank, Field), SSpawnPointRow.Class));

    internal static void Run(string temporary)
    {
        string root = Path.Combine(temporary, "spawn-points"), install = Path.Combine(root, "installation");
        byte[] original = Bank();
        int[] installed = Rows(original);
        Installation(install, original);
        using var catalog = PackageCatalog.Open(install);
        var project = StudioProject.Create(Path.Combine(root, "project"), "Spawn points");
        project.ObjectEditCompiler = ObjectEditCompiler.Default;
        SceneMarker[] Markers() => new AssetSceneLoader(catalog).Load(Tag).Markers.ToArray();
        var stock = Markers();
        Check(stock.Length == 2, "The bank did not decode two markers.");

        var first = SceneEdits.CreateSpawnPointEdit(catalog, stock[0], [], new(3, 4, 5), Quaternion.Identity);
        var second = SceneEdits.CreateSpawnPointEdit(catalog, stock[1], [first], new(-6, 7, 8), new(0, 0, 1, 0));
        project.RecordObjectEdits([second, first], catalog);
        byte[] output = project.ReadCompiled(Tag, catalog);
        int[] rows = Rows(output);
        Check(rows.Length == 4 && output.Length == original.Length + 2 * Stride && TagView.HasSizeWord(output) && rows[..2].SequenceEqual(installed),
            "The bank did not grow in place by two rows.");
        Check(output.AsSpan(installed[0], 2 * Stride).SequenceEqual(original.AsSpan(installed[0], 2 * Stride)),
            "Installed rows changed.");
        // A copy keeps every byte of its source past the pose.
        int pose = TagView.FieldOffset<SSpawnPointRow>(nameof(SSpawnPointRow.Unk1c));
        for (int i = 0; i < 2; i++)
            Check(output.AsSpan(rows[2 + i] + pose, Stride - pose).SequenceEqual(original.AsSpan(installed[i] + pose, Stride - pose)),
                "An added row did not copy its source row.");
        Check(TagView.At<SSpawnPointRow>(output, rows[2]).Position == new Vector3(3, 4, 5)
            && TagView.At<SSpawnPointRow>(output, rows[3]).Rotation == new Quaternion(0, 0, 1, 0), "An added row lost its pose.");
        Check(ObjectEditCompiler.Default.Compile([first, second], catalog.ReadOriginal, catalog).Assets[Tag].SequenceEqual(output)
            && ObjectEditCompiler.Default.Compile([second, first], catalog.ReadOriginal, catalog).Assets[Tag].SequenceEqual(output),
            "Edit order changed the bank.");

        catalog.ReadOverride = tag => project.CompileObjectEdits(catalog).Assets.GetValueOrDefault(tag);
        var markers = Markers();
        Check(markers.Length == 4 && markers[2].Position == new Vector3(3, 4, 5) && markers[3].NameHash == stock[1].NameHash,
            "Studio did not decode the added markers.");
        var info = SceneEdits.Describe(catalog, markers[3], edits: project.ObjectEdits);
        Check(info is { CanTransform: true, CanScale: false, Target: null } && info.Addition?.Key == second.Key,
            "An added marker does not resolve to its edit.");
        Check(!SceneEdits.Describe(catalog, markers[3]).CanTransform, "An added marker resolved without the project's edits.");
        var frame = TransformHandles.Read(catalog, markers[3], null, out _, project.ObjectEdits);
        Check(frame is { Info.CanTransform: true }, "An added point has no move handles.");
        var move = SceneEdits.CreateTransformEdit(catalog, markers[3], new(9, 10, 11), null, 1, edits: project.ObjectEdits)!;
        project.RecordObjectEdit(move, catalog);
        Check(project.ObjectEdits.Count == 2 && project.FindObjectEdit(second.Key)!.Position!.SequenceEqual(new float[] { 9, 10, 11 }),
            "Moving an added point added another row.");
        var copy = SceneEdits.CreateSpawnPointEdit(catalog, markers[3], project.ObjectEdits, Vector3.Zero, Quaternion.Identity);
        Check(copy.Target.Key == second.Target.Key && copy.Target.Index == second.Target.Index + 1,
            "Duplicating an added point did not copy its source row.");

        project.RestoreObjectEdits(new Dictionary<string, ObjectEdit?> { [second.Key] = second }, catalog);
        project.RevertObjectEdit(first.Key);
        markers = Markers();
        Check(markers.Length == 3 && SpawnPointAdditions.At(catalog, catalog.Read(Tag), Tag, markers[2].SourceOffset, project.ObjectEdits)?.Key
            == second.Key, "Removing one addition changed another's identity.");
        project.RestoreObjectEdits(new Dictionary<string, ObjectEdit?> { [first.Key] = first }, catalog);
        Check(project.ReadCompiled(Tag, catalog).SequenceEqual(output), "Restoring an addition changed its bytes.");
        var reopened = StudioProject.Open(project.DirectoryPath);
        reopened.ObjectEditCompiler = ObjectEditCompiler.Default;
        Check(reopened.ReadCompiled(Tag, catalog).SequenceEqual(output), "Saved additions did not round trip.");
        var manifest = JsonNode.Parse(File.ReadAllText(project.ManifestPath))!;
        Check(manifest["version"]!.GetValue<int>() <= StudioProject.ManifestVersion && project.Changes.Count == 0
            && File.ReadAllText(project.ManifestPath).Contains("AddSpawnPoint", StringComparison.Ordinal),
            "Additions must save as edit recipes without payloads.");

        Reject(() => project.RecordObjectEdit(first with { Target = first.Target with { OriginalSha256 = new string('0', 64) } }, catalog),
            "changed");
        Reject(() => project.RecordObjectEdit(first with { Scale = 2 }, catalog), "no scale");
        Reject(() => project.RecordObjectEdit(first with { Position = [float.NaN, 0, 0] }, catalog), "finite");
        byte[] padded = new byte[original.Length + 16]; original.CopyTo(padded, 0);
        Reject(() => ObjectEditCompiler.Default.Compile([first], _ => padded, catalog), "ends with its rows");
        byte[] raw = original.ToArray(); raw[installed[0] + Stride - 1] ^= 0xFF;
        Check(ObjectEditCompiler.Default.Compile([first], _ => raw, catalog).Assets[Tag].AsSpan(installed[0], 3 * Stride).ToArray()
            is var layered && layered[Stride - 1] == raw[installed[0] + Stride - 1] && layered[3 * Stride - 1] == raw[installed[0] + Stride - 1],
            "A raw change to the source row was lost or not copied.");
        var nativeMove = SceneEdits.CreateTransformEdit(catalog, markers[0], new(12, 13, 14), null, 1)!;
        var composed = ObjectEditCompiler.Default.Compile([second, nativeMove, first], catalog.ReadOriginal, catalog).Assets[Tag];
        Check(TagView.At<SSpawnPointRow>(composed, installed[0]).Position == new Vector3(12, 13, 14)
            && TagView.At<SSpawnPointRow>(composed, Rows(composed)[2]).Position == new Vector3(3, 4, 5),
            "Native moves and additions did not compose.");

        var build = reopened.Build(catalog);
        string readback = Path.Combine(root, "readback");
        Installation(readback, original);
        foreach (string file in build.PackageFiles) File.Copy(file, Path.Combine(readback, "packages", Path.GetFileName(file)));
        using (var built = PackageCatalog.Open(readback))
        {
            Check(built.Read(Tag).SequenceEqual(output), "The built package did not read back the grown bank.");
            Check(new AssetSceneLoader(built).Load(Tag).Markers.Count == 4, "The built bank did not decode four points.");
        }
        project.RevertObjectEdit(first.Key); project.RevertObjectEdit(second.Key);
        Check(JsonNode.Parse(File.ReadAllText(project.ManifestPath))!["version"]!.GetValue<int>() == 1
            && project.ReadCompiled(Tag, catalog).SequenceEqual(original), "Removing all additions did not restore the bank.");
        catalog.ReadOverride = null;
        Console.WriteLine($"PASS spawn-points checks={checks}");
    }

    /** Installed banks must end with their rows to take additions; one addition must compile and decode. */
    internal static void Owned(PackageCatalog catalog)
    {
        int banks = 0, terminal = 0, sized = 0;
        uint? sample = null;
        foreach (var entry in catalog.Entries.Where(entry => entry.Reference == SSpawnPoints.Class))
        {
            byte[] bytes = catalog.Read(entry.TagHash);
            int[] rows = Rows(bytes);
            banks++;
            if (rows.Length > 0 && rows[^1] + Stride == bytes.Length) { terminal++; sample ??= entry.TagHash; }
            if (TagView.HasSizeWord(bytes)) sized++;
        }
        Check(sample is not null, "No installed spawn bank ends with its rows.");
        var marker = new AssetSceneLoader(catalog).Load(sample!.Value).Markers.First(item => item.SourceTag == sample);
        var edit = SceneEdits.CreateSpawnPointEdit(catalog, marker, [], marker.Position + Vector3.UnitZ, marker.Rotation);
        byte[] grown = ObjectEditCompiler.Default.Compile([edit], catalog.ReadOriginal, catalog).Assets[sample.Value];
        Check(Rows(grown).Length == Rows(catalog.ReadOriginal(sample.Value)).Length + 1 && TagView.HasSizeWord(grown),
            "An installed bank did not take an addition with its size word.");
        Console.WriteLine($"PASS spawn-banks installed={banks} end-with-rows={terminal} size-word={sized} sample={sample:X8}");
    }

    private static void Check(bool value, string message)
    { checks++; if (!value) throw new InvalidDataException(message); }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException
            && error.Message.Contains(message, StringComparison.OrdinalIgnoreCase)) { checks++; return; }
        throw new InvalidDataException("Expected refusal: " + message);
    }
}
