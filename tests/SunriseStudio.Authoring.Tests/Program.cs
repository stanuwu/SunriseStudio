using System.Buffers.Binary;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

string temporary = Path.Combine(Path.GetTempPath(), "sunrise-authoring-tests-" + Guid.NewGuid().ToString("N"));
try
{
    string installation = Path.Combine(temporary, "owned-fixture");
    string packages = Path.Combine(installation, "packages");
    Directory.CreateDirectory(packages);
    const uint templateTag = 0x80A00000;
    const uint headerType = (32 << 9) | (1 << 6) | 10;
    const uint payloadType = (40 << 9) | (1 << 6) | 10;
    byte[] nativeHeader = new byte[40];
    BinaryPrimitives.WriteUInt32LittleEndian(nativeHeader, 4);
    BinaryPrimitives.WriteUInt32LittleEndian(nativeHeader.AsSpan(4), 28);
    nativeHeader[8] = 0x6B;
    BinaryPrimitives.WriteUInt16LittleEndian(nativeHeader.AsSpan(0x0C), 0xCAFE);
    foreach (int field in new[] { 0x0E, 0x10, 0x12, 0x14 })
        BinaryPrimitives.WriteUInt16LittleEndian(nativeHeader.AsSpan(field), 1);
    nativeHeader[0x17] = 1;
    BinaryPrimitives.WriteUInt32LittleEndian(nativeHeader.AsSpan(0x24), uint.MaxValue);
    PackageWriter.WriteNew(Path.Combine(packages, "w64_fixture_0100_0.pkg"), 0x100,
        [new(templateTag + 1, headerType, nativeHeader), new(uint.MaxValue, payloadType, [1, 2, 3, 255]),
        new(templateTag + 3, headerType, nativeHeader), new(uint.MaxValue, 8 << 9, [1, 2, 3, 255])]);
    using var catalog = PackageCatalog.Open(installation);
    var project = StudioProject.Create(Path.Combine(temporary, "project"), "Texture authoring");
    var image = new RgbaImage(3, 2, [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255,
        12, 34, 56, 78, 90, 123, 234, 255, 255, 255, 255, 0]);
    string png = Path.Combine(temporary, "user-image.png");
    PngInterchange.Write(png, image);
    var imported = TextureAssetImporter.ImportPng(project, catalog, png, "custom:texture", 0x0C00);
    Check(imported.HeaderAsset.BaseTagHash == templateTag && imported.PayloadAsset.BaseTagHash is null,
        "Header must use a delta while new pixels stay whole.");
    Check(imported.HeaderAsset.Native!.Reference == imported.PayloadTag
        && imported.PayloadAsset.Native!.Reference == imported.HeaderTag
        && imported.HeaderAsset.Native.TypeInfo == headerType && imported.PayloadAsset.Native.TypeInfo == payloadType,
        "Native entry metadata changed.");
    Check(imported.HeaderAsset.Path.StartsWith("deltas/", StringComparison.Ordinal)
        && imported.PayloadAsset.Path.StartsWith("assets/", StringComparison.Ordinal), "Texture storage was mixed.");
    Check(!File.ReadAllText(Path.Combine(project.DirectoryPath, imported.HeaderAsset.Path))
        .Contains(Convert.ToBase64String(nativeHeader), StringComparison.Ordinal), "Project stored the source header.");
    Check(project.ReadAddedAsset(imported.HeaderAsset.Id, catalog)[8] == nativeHeader[8], "Unknown header field changed.");
    project = StudioProject.Open(project.DirectoryPath);
    var build = project.Build(catalog);
    using (var built = PackageCatalog.Open(build.OutputDirectory))
    {
        Check(built.TryGetEntry(imported.HeaderTag, out var header) && header.Reference == imported.PayloadTag
            && built.TryGetEntry(imported.PayloadTag, out var payload) && payload.Reference == imported.HeaderTag,
            "Built texture header and payload references are not reciprocal.");
        var texture = new TextureDataReader(built).Read(imported.HeaderTag);
        Check(texture.Width == image.Width && texture.Height == image.Height && texture.MipCount == 1
            && texture.Format == 28 && texture.Subresources.Single().Data.SequenceEqual(image.Pixels),
            "New native texture pixels differ from PNG pixels.");
    }
    Console.WriteLine("PASS png-native-package header=delta payload=user-pixels reopen=verified pixels=equal");

    int previous = project.AddedAssets.Count;
    Throws<KeyNotFoundException>(() => project.AddAssets([
        new("valid", [1, 2, 3], new(0x0C01, uint.MaxValue, payloadType)),
        new("invalid", [4, 5, 6], new(0x0C01, uint.MaxValue, headerType), uint.MaxValue)
    ], catalog));
    Check(project.AddedAssets.Count == previous && StudioProject.Open(project.DirectoryPath).AddedAssets.Count == previous,
        "Failed pair left a half-published asset.");
    Throws<NotSupportedException>(() => TextureAssetImporter.ImportPng(project, catalog, png, "wrong-template", 0x0C01, templateTag + 1));
    Throws<NotSupportedException>(() => TextureAssetImporter.ImportPng(project, catalog, png, "wrong-payload", 0x0C01, templateTag + 2));
    Throws<InvalidDataException>(() => TextureAssetImporter.ImportPng(project, catalog, png, "collision", 0x100));
    Console.WriteLine("PASS batch-additions rollback=verified invalid-template=rejected installed-id=rejected");

    if (args.Length == 4 && args[0] == "--game-root")
    {
        // A private creature glb and its idle clip name: only the glb build and its checks run.
        var idle = new Dictionary<SunriseStudio.Animation.CombatantBehaviour, string>
        {
            [SunriseStudio.Animation.CombatantBehaviour.Idle] = args[3], [SunriseStudio.Animation.CombatantBehaviour.Locomotion] = args[3],
        };
        CombatantCreatureChecks.RunGlb(args[1], args[2], idle, 0.03f, 90);
    }
    if (args.Length == 2 && args[0] == "--game-root")
    {
        using var owned = PackageCatalog.Open(args[1]);
        var realProject = StudioProject.Create(Path.Combine(temporary, "owned-project"), "Owned texture template");
        ushort free = Enumerable.Range(0x100, 0x0C00).Select(x => (ushort)x)
            .First(id => owned.Entries.All(x => x.PackageId != id));
        var textureImport = TextureAssetImporter.ImportPng(realProject, owned, png, "custom:owned-template", free);
        var realBuild = realProject.Build(owned);
        using var built = PackageCatalog.Open(realBuild.OutputDirectory);
        Check(built.TryGetEntry(textureImport.HeaderTag, out var header) && header.Reference == textureImport.PayloadTag
            && built.TryGetEntry(textureImport.PayloadTag, out var payload) && payload.Reference == textureImport.HeaderTag,
            "Owned-template texture retained a template metadata reference.");
        var decoded = new TextureDataReader(built).Read(textureImport.HeaderTag);
        Check(decoded.Subresources.Single().Data.SequenceEqual(image.Pixels), "Owned-template texture pixels changed.");
        Console.WriteLine($"PASS owned-template tag={textureImport.TemplateTag:X8} png-native-package pixels=equal");
        CombatantAnimationChecks.Run(args[1]);
        CombatantCreatureChecks.Run(args[1]);
    }
}
finally
{
    if (Directory.Exists(temporary) && Path.GetFullPath(temporary)
        .StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(temporary, true);
}

static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
