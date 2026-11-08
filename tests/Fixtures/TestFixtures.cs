using System.Buffers.Binary;

/** Fixture files and stand-in inputs the check programs share. */
internal static class TestFixtures
{
    /** A bubble folder of the testbed bundle, found from the check's output folder. */
    internal static string TestbedBubble(string bubble)
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            string candidate = Path.Combine(folder.FullName, "tests", "Fixtures", "testbed", "bundle", bubble);
            if (File.Exists(Path.Combine(candidate, "bubble.json"))) return candidate;
        }
        throw new DirectoryNotFoundException($"The testbed bundle fixture {bubble} was not found.");
    }

    /** Writes a key export with zero keys; synthetic packages are not encrypted, but opening an install needs one. */
    internal static void WriteDummyKeyExport(string path)
    {
        // Version 1 export for build 86657: magic, version, build, two AES-128 keys and a 12-byte nonce.
        byte[] export = new byte[60];
        "SRPKKEYS"u8.CopyTo(export);
        BinaryPrimitives.WriteUInt32LittleEndian(export.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(export.AsSpan(12), 86657);
        File.WriteAllBytes(path, export);
    }
}
