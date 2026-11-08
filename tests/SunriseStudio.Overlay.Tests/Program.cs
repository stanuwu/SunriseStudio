using System.Buffers.Binary;
using System.Security.Cryptography;
using SunriseStudio.Packages;

string root = Path.Combine(Path.GetTempPath(), "sunrise-overlay-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(root, "packages"));
try
{
    string package = Path.Combine(root, "packages", "w64_synthetic_0100_0.pkg");
    byte[] original = [1, 2, 3, 4];
    PackageWriter.WriteNew(package, 0x100, [new(0x80800001, 8u << 9, original, "synthetic:base")]);
    string originalFileHash = HashFile(package);
    using var catalog = PackageCatalog.Open(root);
    var installed = catalog.Entries.Single();
    const ushort overlayPackage = 0x1000;
    uint headerTag = TagHashes.Make(overlayPackage, 0);
    uint payloadTag = headerTag + 1;
    byte[] header = new byte[40];
    byte[] pixels = [255, 0, 0, 255, 0, 255, 0, 255];
    BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)pixels.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 28);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x0C), 0xCAFE);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x0E), 2);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x10), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x12), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x14), 1);
    header[0x17] = 1;
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x24), uint.MaxValue);
    var textureEntry = new PackageEntry(headerTag, payloadTag, 32, 1, header.Length,
        "project-texture", overlayPackage, 0) { TypeInfo = (32u << 9) | (1u << 6) };
    var pixelsEntry = new PackageEntry(payloadTag, 0, 40, 0, pixels.Length,
        "project-texture", overlayPackage, 1) { TypeInfo = 40u << 9 };
    PackageOverlayEntry[] snapshot =
    [
        new(textureEntry, "New texture"), new(pixelsEntry, "New pixels"),
        new(installed with { Size = 6 }, "Edited source")
    ];
    catalog.SetOverlayEntries(snapshot);
    catalog.ReadOverride = tag => tag == headerTag ? header : tag == payloadTag ? pixels
        : tag == installed.TagHash ? [1, 2, 3, 4, 5, 6] : null;
    var texture = new TextureDataReader(catalog).Read(headerTag);
    Require(texture.Width == 2 && texture.Height == 1 && texture.Format == 28,
        "New overlay texture did not decode before package build.");
    Require(texture.Subresources.Single().Data.SequenceEqual(pixels), "Overlay payload pixels changed.");
    Require(catalog.ContainsTag(headerTag) && catalog.GetName(headerTag) == "New texture",
        "Overlay lookup did not expose the new asset.");
    Require(catalog.TryGetEntry(installed.TagHash, out var edited) && edited.Size == 6,
        "Original size overlay was not applied.");
    Require(catalog.Entries.Single() == installed && catalog.Names[installed.TagHash] == "synthetic:base",
        "Installed metadata changed.");
    Require(catalog.ReadOriginal(installed.TagHash).SequenceEqual(original), "Overlay replaced original bytes.");
    Throws<KeyNotFoundException>(() => catalog.ReadOriginal(headerTag));
    Throws<InvalidDataException>(() => catalog.SetOverlayEntries([snapshot[0], snapshot[0]]));
    Throws<InvalidDataException>(() => catalog.SetOverlayEntries([new(textureEntry with { TagHash = headerTag + 5 })]));
    Throws<InvalidDataException>(() => catalog.SetOverlayEntries([new(textureEntry with { Type = 127 })]));
    Throws<InvalidDataException>(() => catalog.SetOverlayEntries([new(textureEntry with { Size = -1 })]));
    Throws<InvalidDataException>(() => catalog.SetOverlayEntries([new(installed with { Reference = 123 })]));
    Throws<InvalidDataException>(() => catalog.SetOverlayEntries([new(installed with { TypeInfo = installed.TypeInfo | 1 })]));
    Require(catalog.ContainsTag(headerTag), "Rejected metadata erased the previous snapshot.");
    snapshot[0] = new(textureEntry, "Caller replaced its array");
    Require(catalog.GetName(headerTag) == "New texture", "Catalog retained the caller's mutable snapshot array.");
    Parallel.For(0, 100, iteration =>
    {
        if (iteration % 5 == 0) catalog.SetOverlayEntries([new(textureEntry), new(pixelsEntry)]);
        Require(catalog.TryGetEntry(headerTag, out var current) && current.Reference == payloadTag,
            "Concurrent overlay lookup lost a complete entry.");
        Require(new TextureDataReader(catalog).Read(headerTag).Subresources[0].Data.SequenceEqual(pixels),
            "Concurrent overlay read changed its payload.");
    });
    catalog.SetOverlayEntries([]);
    catalog.ReadOverride = null;
    Require(!catalog.ContainsTag(headerTag) && catalog.TryGetEntry(installed.TagHash, out var restored)
        && restored == installed && catalog.GetName(installed.TagHash) == "synthetic:base",
        "Clearing the overlay did not restore installed metadata.");
    Require(HashFile(package) == originalFileHash && Directory.GetFiles(Path.Combine(root, "packages")).Length == 1,
        "Overlay viewing wrote a package file.");
    Console.WriteLine("overlay_test texture=pass originals=pass validation=pass concurrency=pass files=unchanged");
    return 0;
}
finally
{
    string temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
    string verified = Path.GetFullPath(root);
    if (verified.StartsWith(temporaryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(verified).StartsWith("sunrise-overlay-tests-", StringComparison.Ordinal))
        Directory.Delete(verified, true);
}

static string HashFile(string path)
{
    using var file = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(file));
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
