using System.Buffers.Binary;
using System.Security.Cryptography;
using SunriseStudio.Packages;

internal static class PackageKeySourceChecks
{
    // Separate package ids exercise both key choices and the package-specific nonce.
    private const ushort PrimaryPackage = 0x0123, AlternatePackage = 0x0124;
    private static readonly byte[] PrimaryPayload = [1, 3, 5, 7, 9];
    private static readonly byte[] AlternatePayload = [2, 4, 6, 8, 10, 12];

    /** Synthetic packages use the same key selection as installed data. */
    internal static void Synthetic()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "sunrise-package-keys-" + Guid.NewGuid().ToString("N"));
        string install = Path.Combine(root, "install");
        string export = Path.Combine(install, "bin", "x64", "Sunrise", "exports",
            "package_keys.bin");
        byte[] keys = Enumerable.Range(1, 44).Select(value => (byte)value).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(export)!);
        try
        {
            EncryptedPackage(install, PrimaryPackage, PrimaryPayload, keys, alternate: false);
            EncryptedPackage(install, AlternatePackage, AlternatePayload, keys, alternate: true);
            SourceSelection(root, install, export, keys);
            InvalidExports(install, export, keys);
            CacheIdentity(root, install, export, keys);
            Console.WriteLine("package_keys export=discovered primary=decrypted "
                + "alternate=decrypted "
                + "override=preferred invalid=rejected pe=compatible cache=invalidated");
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()),
                StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }

    /** An explicit source must win; a broken explicit source must never choose installed keys. */
    private static void SourceSelection(string root, string install, string export, byte[] keys)
    {
        string executable = Path.Combine(install, "destiny2.exe");
        byte[] wrong = keys.Select(value => (byte)(value ^ 0x80)).ToArray();
        File.WriteAllBytes(executable, UnpackedImage(wrong));
        File.WriteAllBytes(export, Export(keys));
        ReadBoth(install, null, export);
        ReadBoth(install, "", export);
        ReadBoth(install, " \t ", export);

        string explicitExport = Path.Combine(root, "explicit-keys.bin");
        string explicitImage = Path.Combine(root, "explicit-unpacked.exe");
        File.WriteAllBytes(explicitExport, Export(keys));
        File.WriteAllBytes(explicitImage, UnpackedImage(keys));
        File.WriteAllBytes(export, Export(wrong));
        ReadBoth(install, explicitExport, explicitExport);
        ReadBoth(install, explicitImage, explicitImage);

        File.WriteAllBytes(export, Export(keys));
        RejectRead(install, Path.Combine(root, "missing-keys.bin"));
        File.WriteAllBytes(explicitExport, [1, 2, 3]);
        RejectRead(install, explicitExport);
        File.Delete(export);
        File.WriteAllBytes(executable, UnpackedImage(keys));
        ReadBoth(install, null, executable);
        byte[] packed = UnpackedImage(keys);
        packed.AsSpan(0x220, 0x80).Clear();
        File.WriteAllBytes(executable, packed);
        string failure = RejectRead(install, null);
        Check(failure.Contains("client.exports.package_keys", StringComparison.Ordinal),
            "Missing keys did not explain the Sunrise export option.");
        File.WriteAllBytes(executable, UnpackedImage(keys));
        File.WriteAllBytes(export, Export(keys));
    }

    /** Invalid exports must fail even when the installed executable contains usable keys. */
    private static void InvalidExports(string install, string export, byte[] keys)
    {
        byte[] valid = Export(keys);
        byte[] badMagic = valid.ToArray(); badMagic[0] ^= 1;
        byte[] badVersion = valid.ToArray(); U32(badVersion, 8, 2);
        byte[] badBuild = valid.ToArray(); U32(badBuild, 12, 86658);
        foreach (byte[] invalid in new byte[][]
            { [], valid[..8], valid[..59], [.. valid, 0], badMagic, badVersion, badBuild })
        {
            File.WriteAllBytes(export, invalid);
            RejectRead(install, null);
        }
        File.WriteAllBytes(export, valid);
        ReadBoth(install, null, export);
    }

    /** Key edits must invalidate cached data even when size and write time stay the same. */
    private static void CacheIdentity(string root, string install, string export, byte[] keys)
    {
        var options = new StudioCacheOptions { DirectoryPath = Path.Combine(root, "cache") };
        string executable = Path.Combine(install, "destiny2.exe");
        using var initial = PackageCatalog.Open(install, cacheOptions: options);
        string identity = Identity(initial);
        Check(initial.DiskCache.Write(identity, stream => stream.WriteByte(42)),
            "Key source cache fixture was not saved.");
        using (var warm = PackageCatalog.Open(install, cacheOptions: options))
            Check(Identity(warm) == identity && warm.DiskCache.TryRead(identity,
                stream => Check(stream.ReadByte() == 42, "Key source cache payload changed.")),
                "Unchanged key export missed the cache.");
        using var explicitImage = PackageCatalog.Open(install, executable, options);
        string explicitIdentity = Identity(explicitImage);
        Check(explicitIdentity != identity,
            "Explicit executable shared the export cache identity.");
        DateTime stamp = File.GetLastWriteTimeUtc(export);
        byte[] changed = Export(keys); changed[^1] ^= 1;
        File.WriteAllBytes(export, changed); File.SetLastWriteTimeUtc(export, stamp);
        using (var modified = PackageCatalog.Open(install, cacheOptions: options))
        {
            string changedIdentity = Identity(modified);
            Check(changedIdentity != identity
                && !modified.DiskCache.TryRead(changedIdentity, _ => { }),
                "Changed key export reused its old cache snapshot.");
        }
        Check(Identity(explicitImage) == explicitIdentity,
            "An unused automatic export changed an explicit source's cache identity.");
        File.Delete(export);
        using (var removed = PackageCatalog.Open(install, cacheOptions: options))
            Check(Identity(removed) == explicitIdentity,
                "Removed export did not select the executable cache identity.");
        File.WriteAllBytes(export, Export(keys));
        using var restored = PackageCatalog.Open(install, cacheOptions: options);
        Check(Identity(restored) != explicitIdentity,
            "Added export retained the executable cache identity.");
    }

    private static string Identity(PackageCatalog catalog) =>
        catalog.CreateOriginalCacheIdentity("package-key-source-checks-v1")
            ?? throw new InvalidDataException("Key source cache identity is unavailable.");

    /** Both flag choices must authenticate and return their original payloads. */
    private static void ReadBoth(string install, string? source, string expectedSource)
    {
        using var catalog = PackageCatalog.Open(install, source, new() { Enabled = false });
        Check(catalog.ReadOriginal(Tag(PrimaryPackage)).SequenceEqual(PrimaryPayload),
            "Primary package key did not decrypt the payload.");
        Check(catalog.ReadOriginal(Tag(AlternatePackage)).SequenceEqual(AlternatePayload),
            "Alternate package key did not decrypt the payload.");
        Check(string.Equals(catalog.KeySource, Path.GetFullPath(expectedSource),
            StringComparison.OrdinalIgnoreCase), "Package keys came from an unexpected source.");
    }

    private static string RejectRead(string install, string? source)
    {
        using var catalog = PackageCatalog.Open(install, source, new() { Enabled = false });
        try { catalog.ReadOriginal(Tag(PrimaryPackage)); }
        catch (InvalidDataException error) { return error.Message; }
        throw new InvalidDataException("Invalid key source silently used fallback keys.");
    }

    /** Export fixtures contain arbitrary bytes and never copy installed key material. */
    private static byte[] Export(byte[] keys)
    {
        byte[] bytes = new byte[60];
        "SRPKKEYS"u8.CopyTo(bytes);
        U32(bytes, 8, 1); U32(bytes, 12, 86657);
        keys.CopyTo(bytes, 16);
        return bytes;
    }

    /** Ciphertext must authenticate without a codec or a running game. */
    private static void EncryptedPackage(string install, ushort id,
        byte[] payload, byte[] keys, bool alternate)
    {
        string path = Path.Combine(install, "packages", $"w64_key_fixture_{id:X4}_0.pkg");
        PackageWriter.WriteNew(path, id, [new(uint.MaxValue, 40 << 9, payload)]);
        byte[] file = File.ReadAllBytes(path);
        int metadata = checked((int)R32(file, 0x110));
        int metadataSize = checked((int)R32(file, 0x114));
        int entries = metadata + 96;
        int capacity = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(
            file.AsSpan(entries - 16)));
        int block = checked(entries + capacity * 16 + 32);
        int offset = checked((int)R32(file, block)), length = checked((int)R32(file, block + 4));
        byte[] nonce = keys.AsSpan(32, 12).ToArray();
        nonce[0] ^= (byte)(id >> 8); nonce[1] = 0xF9; nonce[11] ^= (byte)id;
        byte[] ciphertext = new byte[length], tag = new byte[16];
        using var aes = new AesGcm(keys.AsSpan(alternate ? 16 : 0, 16), tag.Length);
        aes.Encrypt(nonce, file.AsSpan(offset, length), ciphertext, tag);
        ciphertext.CopyTo(file, offset); tag.CopyTo(file, block + 32);
        U16(file, block + 10, alternate ? (ushort)6 : (ushort)2);
        SHA1.HashData(ciphertext).CopyTo(file, block + 12);
        SHA1.HashData(file.AsSpan(metadata, metadataSize)).CopyTo(file, 0x118);
        File.WriteAllBytes(path, file);
    }

    /** A small file-backed PE keeps the existing explicit executable source testable. */
    private static byte[] UnpackedImage(byte[] keys)
    {
        byte[] image = new byte[0xA00];
        image[0] = (byte)'M'; image[1] = (byte)'Z'; U32(image, 0x3C, 0x80);
        U32(image, 0x80, 0x4550); U16(image, 0x86, 1); U16(image, 0x94, 0xF0);
        U16(image, 0x98, 0x20B);
        U32(image, 0x188 + 12, 0x1000); U32(image, 0x188 + 16, 0x800);
        U32(image, 0x188 + 20, 0x200); U32(image, 0x188 + 36, 0x60000020);
        byte[] tableInstruction = Convert.FromHexString(
            "0F100500000000488D6424F848892C24488D2D00000000E9");
        byte[] tokenInstruction = Convert.FromHexString(
            "0F1044240041B814000000488D15000000000F104C2400488D8C2400000000");
        tableInstruction.CopyTo(image, 0x220); U32(image, 0x223, 0x600 - (0x220 + 7));
        tokenInstruction.CopyTo(image, 0x260); U32(image, 0x260 + 14, 0x650 - (0x260 + 18));
        keys.AsSpan(16, 16).CopyTo(image.AsSpan(0x600));
        keys.AsSpan(32, 12).CopyTo(image.AsSpan(0x620));
        for (int i = 0; i < 16; i++)
        {
            image[0x650 + i] = (byte)(0xA0 + i);
            image[0x610 + i] = unchecked((byte)(keys[i] - image[0x650 + i]));
        }
        return image;
    }

    private static uint Tag(ushort id) => TagHashes.Make(id, 0);
    private static uint R32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static void U32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void U16(byte[] bytes, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidDataException(message); }
}
