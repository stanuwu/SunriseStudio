using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

string root = Path.Combine(Path.GetTempPath(), "sunrise-studio-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = new List<string>();
try
{
    var random = new Random(73691);
    for (int iteration = 0; iteration < 200; iteration++)
    {
        byte[] original = new byte[random.Next(0, 8192)];
        random.NextBytes(original);
        var edited = original.ToList();
        for (int change = 0; change < 15; change++)
        {
            int index = random.Next(edited.Count + 1);
            if (index < edited.Count && random.Next(2) == 0) edited.RemoveAt(index);
            else edited.Insert(index, (byte)random.Next(256));
        }
        var delta = BinaryDelta.Create(original, edited.ToArray());
        Require(delta.Apply(original).SequenceEqual(edited), "Delta roundtrip failed.");
        if (original.Length != 0)
        {
            Require(delta.Operations.All(x => x.Kind != DeltaKind.Add), "Delta stored original-backed literals.");
            original[0] ^= 1;
            Throws<InvalidDataException>(() => delta.Apply(original));
        }
    }
    checks.Add("200 randomized insert, delete, replace, and changed-base checks");
    byte[] repetitive = Enumerable.Repeat((byte)23, 500_000).ToArray();
    byte[] shifted = new byte[] { 1, 2, 3 }.Concat(repetitive).Concat(new byte[] { 9 }).ToArray();
    var shiftDelta = BinaryDelta.Create(repetitive, shifted);
    Require(shiftDelta.Apply(repetitive).SequenceEqual(shifted), "Large shifted delta failed.");
    Require(shiftDelta.Operations.Where(x => x.Data is not null).Sum(x => x.Data!.Length) < 32,
        "Insertion copied a base range into the delta.");
    Require(BinaryDelta.Create(repetitive, []).Apply(repetitive).Length == 0, "Empty edit failed.");
    checks.Add("large shifted ranges stay copy references and whole-entry deletion reconstructs");

    string install = Path.Combine(root, "owned-install");
    string packageDirectory = Path.Combine(install, "packages");
    Directory.CreateDirectory(packageDirectory);
    byte[] originalAsset = Encoding.UTF8.GetBytes("This synthetic asset has a stable original body for delta storage checks.");
    byte[] untouched = new byte[600_000];
    random.NextBytes(untouched);
    var sourceEntries = new[]
    {
        new AuthoredPackageEntry(0x80800001, 0x100A, originalAsset, "test:edited"),
        new AuthoredPackageEntry(0x80800002, 0x100A, untouched, "test:untouched")
    };
    string baseFile = Path.Combine(packageDirectory, "w64_test_0100_0.pkg");
    var baseArtifact = PackageWriter.WriteNew(baseFile, 0x100, sourceEntries);
    string baseHash = FileHash(baseFile);
    const uint firstTag = 0x80A00000;
    using var catalog = PackageCatalog.Open(install);
    Require(catalog.ReadOriginal(firstTag).SequenceEqual(originalAsset), "New package asset read failed.");
    if (args.Contains("--publication-audit"))
    {
        PublicationAuditChecks.Run(root, catalog, firstTag);
        return;
    }
    Require(catalog.ReadOriginal(firstTag + 1).SequenceEqual(untouched), "Spanning asset read failed.");
    Require(catalog.GetName(firstTag) == "test:edited", "Named tag metadata failed.");
    ValidateHashes(baseFile);
    checks.Add("native package blocks, names, spanning assets, and metadata SHA1 roundtrip");

    string projectDirectory = Path.Combine(root, "project");
    var project = StudioProject.Create(projectDirectory, "Synthetic project");
    var editedAsset = Encoding.UTF8.GetBytes("This synthetic asset has an edited original body for delta storage checks. Extended.");
    project.ApplyEdit(firstTag, editedAsset, catalog);
    Require(project.ReadAsset(firstTag, catalog).SequenceEqual(editedAsset), "Project edit read failed.");
    Throws<KeyNotFoundException>(() => project.ApplyEdits(new Dictionary<uint, byte[]>
    { [firstTag] = [1, 2, 3], [0xFFFFFFFF] = [4, 5, 6] }, catalog));
    Require(project.ReadAsset(firstTag, catalog).SequenceEqual(editedAsset), "Failed batch saved a partial edit.");
    Require(!File.ReadAllText(project.ManifestPath).Contains(install, StringComparison.OrdinalIgnoreCase),
        "Manifest stored the machine install path.");
    string deltaFile = Path.Combine(projectDirectory, project.Changes[0].DeltaPath);
    Require(!File.ReadAllText(deltaFile).Contains(Convert.ToBase64String(originalAsset), StringComparison.Ordinal),
        "Delta stored the original payload.");
    project = StudioProject.Open(project.ManifestPath);
    Require(project.ReadAsset(firstTag, catalog).SequenceEqual(editedAsset), "Project reopen failed.");
    catalog.ReadOverride = tag => project.ContainsEdit(tag) ? project.ReadAsset(tag, catalog) : null;
    Require(catalog.Read(firstTag).SequenceEqual(editedAsset), "Catalog edit overlay failed.");
    Require(catalog.ReadOriginal(firstTag).SequenceEqual(originalAsset), "Overlay changed original reads.");
    var native = project.AddAsset("new-native.bin", new byte[] { 7, 8, 9 }, new(0x0C00, 0x80800003, 0x100A, "test:new"));
    project.UpdateAddedAsset(native.Id, new byte[] { 6, 7, 8, 9 });
    Require(project.ReadAddedAsset(native.Id).SequenceEqual(new byte[] { 6, 7, 8, 9 }), "Added asset update failed.");
    var clone = project.AddDerivedAsset("derived-native.bin", firstTag, originalAsset,
        new(0x0C00, sourceEntries[0].Reference, sourceEntries[0].TypeInfo, "test:derived"), catalog);
    string cloneDelta = File.ReadAllText(Path.Combine(projectDirectory, clone.Path));
    Require(cloneDelta.Contains("Copy", StringComparison.Ordinal)
        && !cloneDelta.Contains(Convert.ToBase64String(originalAsset), StringComparison.Ordinal),
        "Unchanged clone stored original payload bytes.");
    Throws<InvalidOperationException>(() => project.ReadAddedAsset(clone.Id));
    Require(project.ReadAddedAsset(clone.Id, catalog).SequenceEqual(originalAsset), "Unchanged clone reconstruction failed.");
    project.UpdateAddedAsset(clone.Id, editedAsset, catalog);
    project = StudioProject.Open(projectDirectory);
    Require(project.ReadAddedAsset(clone.Id, catalog).SequenceEqual(editedAsset), "Modified clone reopen failed.");
    Require(project.AddedAssets.First(x => x.Id == clone.Id).Native!.EntryIndex == clone.Native!.EntryIndex,
        "Derived asset update changed its tag index.");
    byte[] mixedOriginal = editedAsset.Concat(new byte[] { 11 }).ToArray();
    project.ApplyEdits(new Dictionary<uint, byte[]> { [firstTag] = mixedOriginal }, catalog,
        new Dictionary<string, byte[]> { [native.Id] = [10, 20, 30], [clone.Id] = [40, 50, 60] });
    project = StudioProject.Open(projectDirectory);
    Require(project.ReadAsset(firstTag, catalog).SequenceEqual(mixedOriginal)
        && project.ReadAddedAsset(native.Id, catalog).SequenceEqual(new byte[] { 10, 20, 30 })
        && project.ReadAddedAsset(clone.Id, catalog).SequenceEqual(new byte[] { 40, 50, 60 }),
        "Mixed original and added asset transaction failed.");
    Throws<KeyNotFoundException>(() => project.ApplyEdits(new Dictionary<uint, byte[]> { [firstTag] = [1] }, catalog,
        new Dictionary<string, byte[]> { ["missing-added-id"] = [2] }));
    Require(project.ReadAsset(firstTag, catalog).SequenceEqual(mixedOriginal), "Bad added id published a partial original edit.");
    project.ApplyEdits(new Dictionary<uint, byte[]> { [firstTag] = editedAsset }, catalog,
        new Dictionary<string, byte[]> { [native.Id] = [6, 7, 8, 9], [clone.Id] = editedAsset });
    var build = project.Build(catalog);
    Require(build.PackageFiles.Count == 2 && File.Exists(build.ReceiptPath), "Build output is incomplete.");
    Require(FileHash(baseFile) == baseHash, "Build modified an original package.");
    foreach (string output in build.PackageFiles) ValidateHashes(output);
    string verificationInstall = Path.Combine(root, "verification");
    Directory.CreateDirectory(Path.Combine(verificationInstall, "packages"));
    File.Copy(baseFile, Path.Combine(verificationInstall, "packages", Path.GetFileName(baseFile)));
    foreach (string output in build.PackageFiles)
        File.Copy(output, Path.Combine(verificationInstall, "packages", Path.GetFileName(output)));
    using (var builtCatalog = PackageCatalog.Open(verificationInstall))
    {
        Require(builtCatalog.Read(firstTag).SequenceEqual(editedAsset), "Patch changed asset read failed.");
        Require(builtCatalog.Read(firstTag + 1).SequenceEqual(untouched), "Inherited asset read failed.");
        Require(builtCatalog.Read(build.AddedAssets[0].TagHash).SequenceEqual(new byte[] { 6, 7, 8, 9 }), "Added native read failed.");
        Require(builtCatalog.Read(build.AddedAssets.Single(x => x.Id == clone.Id).TagHash).SequenceEqual(editedAsset),
            "Derived native package roundtrip failed.");
    }
    checks.Add("project reopen, catalog overlay, asset updates, native additions, patch builds, and inherited blocks");

    string sourcePatch = Path.Combine(verificationInstall, "packages", "w64_test_0100_1.pkg");
    string secondPatch = Path.Combine(root, "second-build", "w64_test_0100_2.pkg");
    byte[] largeReplacement = new byte[0x210000];
    random.NextBytes(largeReplacement);
    PackageWriter.WritePatch(sourcePatch, secondPatch, new Dictionary<int, byte[]> { [0] = largeReplacement }, verificationInstall);
    ValidateHashes(secondPatch);
    File.Copy(secondPatch, Path.Combine(verificationInstall, "packages", Path.GetFileName(secondPatch)));
    Require(PackageWriter.GetRequiredPredecessors(Path.Combine(verificationInstall, "packages", Path.GetFileName(secondPatch))).Count == 3,
        "Expected inherited generations are missing.");
    using (var grownCatalog = PackageCatalog.Open(verificationInstall))
    {
        Require(grownCatalog.Read(firstTag).SequenceEqual(largeReplacement), "Growing block table changed replacement bytes.");
        Require(grownCatalog.Read(firstTag + 1).SequenceEqual(untouched), "Second patch broke inherited bytes.");
        Require(grownCatalog.GetName(firstTag) == "test:edited", "Second patch changed inherited names.");
    }
    checks.Add("multiple patch generations and block-table growth preserve inherited assets and names");

    Throws<IOException>(() => project.Build(catalog, Path.Combine(install, "forbidden")));
    Throws<IOException>(() => PackageWriter.WritePatch(baseFile,
        Path.Combine(packageDirectory, "w64_test_0100_1.pkg"), new Dictionary<int, byte[]> { [0] = editedAsset }, install));
    var wrongBase = originalAsset.ToArray();
    wrongBase[0] ^= 1;
    string wrongInstall = Path.Combine(root, "changed-base");
    Directory.CreateDirectory(Path.Combine(wrongInstall, "packages"));
    PackageWriter.WriteNew(Path.Combine(wrongInstall, "packages", "w64_test_0100_0.pkg"), 0x100,
        [sourceEntries[0] with { Data = wrongBase }, sourceEntries[1]]);
    using (var wrongCatalog = PackageCatalog.Open(wrongInstall))
    {
        Throws<InvalidDataException>(() => project.ReadAsset(firstTag, wrongCatalog));
        Throws<InvalidDataException>(() => project.ReadAddedAsset(clone.Id, wrongCatalog));
        Throws<InvalidDataException>(() => project.UpdateAddedAsset(clone.Id, [1, 2, 3], wrongCatalog));
        Throws<InvalidDataException>(() => project.ApplyEdits(new Dictionary<uint, byte[]>(), wrongCatalog,
            new Dictionary<string, byte[]> { [native.Id] = [1, 2, 3], [clone.Id] = [4, 5, 6] }));
        Require(project.ReadAddedAsset(native.Id).SequenceEqual(new byte[] { 6, 7, 8, 9 }),
            "Changed derived base published a partial added edit.");
        Throws<InvalidDataException>(() => project.Build(wrongCatalog));
    }
    string savedManifest = File.ReadAllText(project.ManifestPath);
    var malicious = JsonNode.Parse(savedManifest)!;
    malicious["changes"]![0]!["deltaPath"] = "deltas/../../outside.json";
    File.WriteAllText(project.ManifestPath, malicious.ToJsonString());
    Throws<InvalidDataException>(() => StudioProject.Open(projectDirectory));
    File.WriteAllText(project.ManifestPath, savedManifest);
    var sourceOnly = project.AddAsset("mesh.obj", Encoding.UTF8.GetBytes("v 0 0 0\n"));
    var sourceBuild = project.Build(catalog);
    Require(File.ReadAllText(sourceBuild.ReceiptPath).Contains("mesh.obj", StringComparison.Ordinal),
        "Build receipt omitted retained source assets.");
    project.RemoveAddedAsset(sourceOnly.Id);
    project.RemoveAddedAsset(native.Id);
    project.RemoveAddedAsset(clone.Id);
    project.Revert(firstTag);
    Require(StudioProject.Open(projectDirectory).Changes.Count == 0 && !File.Exists(deltaFile), "Revert left a referenced delta.");
    Require(!Directory.Exists(Path.Combine(install, "forbidden")), "Rejected output created install files.");
    checks.Add("changed bases, traversal, install writes, partial edit batches, and stale delta files are rejected");
    WideHashChecks.Run(root);
    TransactionChecks.Run(root, catalog, firstTag);
    ObjectEditChecks.Run(root, catalog, firstTag);
    ObjectEditChecks.Structural(root, catalog, firstTag);
    ObjectNameChecks.Run(root);
    ObjectGroupChecks.Run(root);
    PublicationAuditChecks.Run(root, catalog, firstTag);
    ModInstallChecks.Run(root);
    MergeSidecarChecks.Run(root);
    DependencyTableChecks.Run();
    TftBuildChecks.Run(root);
    LiveBuildChecks.Run(root);
    TagAllocationChecks.Run(root);
    ReferenceMapChecks.Run(root);
    foreach (string check in checks) Console.WriteLine("PASS " + check);
    Console.WriteLine($"PASS checks={checks.Count}");
}
catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
finally
{
    if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(root, true);
}

if (Environment.ExitCode == 0 && args.Length == 2 && args[0] == "--game-root") LivePackageValidation.Run(args[1]);

static void Require(bool value, string message) { if (!value) throw new Exception(message); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
static string FileHash(string path)
{
    using var file = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(file));
}
static void ValidateHashes(string path)
{
    PackageFormatValidation.Check(path);
    byte[] file = File.ReadAllBytes(path);
    foreach (int field in new[] { 0xF0, 0x110 })
    {
        int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(field));
        int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(field + 4));
        Require(SHA1.HashData(file.AsSpan(offset, size)).SequenceEqual(file.AsSpan(field + 8, 20).ToArray()),
            "Metadata SHA1 mismatch.");
    }
}
