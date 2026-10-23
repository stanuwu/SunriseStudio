using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

internal static class SpawnPointChecks
{
    internal const uint Tag = 0x80A00000;
    private static int checks;

    /** A synthetic bank with two installed arrivals and Python's fresh-bank framing. */
    internal static byte[] Bank()
    {
        byte[] bytes = new byte[160];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, 160);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), 2);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44), 0x80809FBD);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(48), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56), 0x80809164);
        for (int i = 0; i < 2; i++)
        {
            int at = 64 + i * 48;
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + 12), 1);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + 16), i + 1);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + 28), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 32), 0x2EA8FB98);
        }
        return bytes;
    }

    internal static void Installation(string folder, byte[] bytes)
    {
        Directory.CreateDirectory(Path.Combine(folder, "packages"));
        PackageWriter.WriteNew(Path.Combine(folder, "packages", "w64_spawns_0100_0.pkg"), 0x100,
            [new AuthoredPackageEntry(0x80809162, 0x100A, bytes)]);
    }

    internal static void Run(string temporary)
    {
        string root = Path.Combine(temporary, "spawn-points"), install = Path.Combine(root, "installation");
        byte[] original = Bank();
        Installation(install, original);
        using var catalog = PackageCatalog.Open(install);
        var project = StudioProject.Create(Path.Combine(root, "project"), "Arrival points");
        project.ObjectEditCompiler = ObjectEditCompiler.Default;
        var first = SpawnPointAuthoring.Create(catalog, [], Tag, new(3, 4, 5), Quaternion.Identity, 0x2EA8FB98);
        var second = SpawnPointAuthoring.Create(catalog, [first], Tag, new(-6, 7, 8), new(0, 0, 1, 0), 0x12345678);
        project.RecordObjectEdits([second, first], catalog);
        byte[] output = project.ReadCompiled(Tag, catalog);
        Check(output.Length == 256 && BinaryPrimitives.ReadUInt64LittleEndian(output) == 256, "Bank size was not updated.");
        Check(BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(8)) == 4
            && BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(48)) == 4, "Both counts must grow.");
        Check(output.AsSpan(64, 96).SequenceEqual(original.AsSpan(64)), "Installed rows changed.");
        Check(output.AsSpan(196, 12).SequenceEqual(new byte[12]) && output.AsSpan(244, 12).SequenceEqual(new byte[12]), "New opaque fields are not zero.");
        Check(BinaryPrimitives.ReadSingleLittleEndian(output.AsSpan(188)) == 1
            && BinaryPrimitives.ReadSingleLittleEndian(output.AsSpan(236)) == 1, "Position W is not one.");
        Check(ObjectEditCompiler.Default.Compile([first, second], catalog.ReadOriginal, catalog).Assets[Tag].SequenceEqual(output),
            "Compile order changed the bank.");
        Check(ObjectEditCompiler.Default.Compile([second, first], catalog.ReadOriginal, catalog).Assets[Tag].SequenceEqual(output),
            "Repeat compile changed the bank.");
        catalog.ReadOverride = tag => project.CompileObjectEdits(catalog).Assets.GetValueOrDefault(tag);
        var markers = new AssetSceneLoader(catalog).Load(Tag).Markers.ToArray();
        Check(markers.Length == 4 && markers.All(marker => marker.Layer == SceneLayer.Spawns), "Studio did not decode all four markers.");
        Check(markers[2].Position == new Vector3(3, 4, 5) && markers[2].Rotation == Quaternion.Identity
            && markers[2].NameHash == 0x2EA8FB98 && markers[3].Position == new Vector3(-6, 7, 8)
            && markers[3].Rotation == new Quaternion(0, 0, 1, 0) && markers[3].NameHash == 0x12345678, "Studio spawn fields differ.");
        var frame = TransformHandles.Read(catalog, markers[3], null, out _, project.ObjectEdits);
        Check(frame is { Info.CanTransform: true, Info.CanScale: false }, "Added point has no move/rotate handles.");
        var move = SceneEdits.CreateTransformEdit(catalog, markers[3], new(9, 10, 11), null, 1,
            edits: project.ObjectEdits)!;
        project.RecordObjectEdit(move, catalog);
        Check(project.ObjectEdits.Count == 2 && project.FindObjectEdit(second.Key)!.Position!.SequenceEqual(new float[] { 9, 10, 11 }),
            "Moving an added row created another row.");
        project.RestoreObjectEdits(new Dictionary<string, ObjectEdit?> { [second.Key] = second }, catalog);
        project.RevertObjectEdit(first.Key);
        markers = new AssetSceneLoader(catalog).Load(Tag).Markers.ToArray();
        Check(markers.Length == 3 && SpawnPointAuthoring.Find(catalog, markers[2], project.ObjectEdits)?.Key == second.Key,
            "Removing an earlier addition changed the later point's identity.");
        project.RestoreObjectEdits(new Dictionary<string, ObjectEdit?> { [first.Key] = first }, catalog);
        Check(project.ReadCompiled(Tag, catalog).SequenceEqual(output), "Restoring an addition changed its bytes.");
        var reopened = StudioProject.Open(project.DirectoryPath);
        reopened.ObjectEditCompiler = ObjectEditCompiler.Default;
        Check(reopened.ReadCompiled(Tag, catalog).SequenceEqual(output), "Saved additions did not round trip.");
        var manifest = JsonNode.Parse(File.ReadAllText(project.ManifestPath))!;
        Check(manifest["version"]!.GetValue<int>() == 8 && project.Changes.Count == 0 && project.AddedAssets.Count == 0
            && manifest["features"]!.AsArray().Select(name => name!.GetValue<string>()).SequenceEqual([ProjectFeatures.AddedSpawnPoints]),
            "Additions must store recipes in version 8 under their feature, without payloads.");
        manifest["features"] = new JsonArray(ProjectFeatures.AddedSpawnPoints, "later-feature");
        File.WriteAllText(project.ManifestPath, manifest.ToJsonString());
        Reject(() => StudioProject.Open(project.DirectoryPath), "newer Sunrise Studio");
        manifest["features"] = new JsonArray(ProjectFeatures.AddedSpawnPoints);
        manifest["version"] = 7;
        File.WriteAllText(project.ManifestPath, manifest.ToJsonString());
        Reject(() => StudioProject.Open(project.DirectoryPath), "version 8");
        project.Save();
        foreach (string role in new[] { "respawn", "checkpoint" })
            Reject(() => SpawnPointAuthoring.Create(catalog, [], Tag, Vector3.Zero, Quaternion.Identity, 0, role), role);
        Reject(() => project.RecordObjectEdit(first with { Target = first.Target with { OriginalSha256 = new string('0', 64) } }, catalog), "changed");
        Reject(() => SpawnPointAuthoring.Create(catalog, [], Tag, new(float.NaN), Quaternion.Identity, 0), "finite");
        Reject(() => SpawnPointAuthoring.Create(catalog, [], Tag, Vector3.Zero, new(0, 0, 0, 0), 0), "unit quaternion");
        byte[] malformed = original.ToArray(); malformed[56] ^= 1;
        Reject(() => ObjectEditCompiler.Default.Compile([first], _ => malformed, catalog), "array");
        byte[] trailer = new byte[original.Length + 16]; original.CopyTo(trailer, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(trailer, (ulong)trailer.Length);
        Reject(() => ObjectEditCompiler.Default.Compile([first], _ => trailer, catalog), "terminal");
        Reject(() => ObjectEditCompiler.Default.Compile([first], _ => output, catalog), "resized");
        byte[] opaque = original.ToArray(); opaque[100] = 0xA5;
        var layered = ObjectEditCompiler.Default.Compile([first], _ => opaque, catalog).Assets[Tag];
        Check(layered.AsSpan(64, 96).SequenceEqual(opaque.AsSpan(64)), "Raw opaque row fields were lost.");
        var nativeMove = SceneEdits.CreateTransformEdit(catalog, markers[0], new(12, 13, 14), null, 1)!;
        var composed = ObjectEditCompiler.Default.Compile([second, nativeMove, first], catalog.ReadOriginal, catalog).Assets[Tag];
        Check(BinaryPrimitives.ReadSingleLittleEndian(composed.AsSpan(80)) == 12 && composed.AsSpan(112, 48).SequenceEqual(original.AsSpan(112)),
            "Native moves and additions did not compose.");
        var build = reopened.Build(catalog);
        string readback = Path.Combine(root, "readback");
        Installation(readback, original);
        foreach (string file in build.PackageFiles) File.Copy(file, Path.Combine(readback, "packages", Path.GetFileName(file)));
        using (var built = PackageCatalog.Open(readback))
        {
            Check(built.Read(Tag).SequenceEqual(output), "PackageFile did not read the grown patch byte exactly.");
            Check(new AssetSceneLoader(built).Load(Tag).Markers.Count == 4, "Patched bank did not decode four points.");
        }
        Parity(root, output);
        project.RevertObjectEdit(first.Key); project.RevertObjectEdit(second.Key);
        Check(JsonNode.Parse(File.ReadAllText(project.ManifestPath))!["version"]!.GetValue<int>() == 1
            && project.ReadCompiled(Tag, catalog).SequenceEqual(original), "Removing all additions did not restore the original/version.");
        catalog.ReadOverride = null;
        Console.WriteLine($"PASS spawn-points checks={checks} original-rows=2 added-rows=2 preserved-bytes=96 bank-bytes=256 package-readback=exact");
    }

    // SHA-256 of World Studio's spawn_authoring.create on the same four points, frozen from the parity run.
    private const string PythonBankSha256 = "B19D78399C6AF09D3E66C665C84EF3D7061716C7F076F1E7D1AB79760BAF0B68";

    /** The driver and World Studio live outside this repository; WORLD_STUDIO_SOURCE and SUNRISE_PARITY_DRIVERS name them. */
    private static void Parity(string root, byte[] output)
    {
        Check(BinaryDelta.Hash(output).Equals(PythonBankSha256, StringComparison.OrdinalIgnoreCase), "The grown bank differs from World Studio's frozen bytes.");
        string? source = Environment.GetEnvironmentVariable("WORLD_STUDIO_SOURCE"), drivers = Environment.GetEnvironmentVariable("SUNRISE_PARITY_DRIVERS");
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(drivers))
        {
            Console.WriteLine("SKIP spawn-python-parity: set WORLD_STUDIO_SOURCE and SUNRISE_PARITY_DRIVERS.");
            return;
        }
        string script = Path.Combine(drivers, "spawn_parity.py"), result = Path.Combine(root, "python-bank.bin");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("PYTHON") ?? "python")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "-I", "-B", script, source, result }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000)) { process.Kill(true); throw new InvalidDataException("Python spawn parity timed out."); }
        Check(process.ExitCode == 0, "Python spawn parity failed: " + stderr.GetAwaiter().GetResult() + stdout.GetAwaiter().GetResult());
        Check(File.ReadAllBytes(result).SequenceEqual(output), "Python and C# banks differ.");
        string? artifacts = Environment.GetEnvironmentVariable("SPAWN_PARITY_OUTPUT");
        if (!string.IsNullOrWhiteSpace(artifacts))
        {
            Directory.CreateDirectory(artifacts);
            File.Copy(result, Path.Combine(artifacts, "python-bank.bin"), true);
            File.WriteAllBytes(Path.Combine(artifacts, "csharp-bank.bin"), output);
        }
        Console.WriteLine($"PASS spawn-python-parity bytes={output.Length} sha256={BinaryDelta.Hash(output)}");
    }

    private static void Check(bool value, string message)
    { checks++; if (!value) throw new InvalidDataException(message); }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException error) when (error.Message.Contains(message, StringComparison.OrdinalIgnoreCase)) { checks++; return; }
        throw new InvalidDataException("Expected refusal: " + message);
    }
}
