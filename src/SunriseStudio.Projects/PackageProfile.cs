using System.Buffers.Binary;

namespace SunriseStudio.Projects;

/** Picks the installed package file header that new packages copy. */
internal static class PackageProfile
{
    // Package file header: its size, the u32 build at 0x18 and the variant byte at 0x1A.
    private const int HeaderSize = 0x180, BuildOffset = 0x18, VariantOffset = 0x1A;
    // PackageWriter.WriteNew accepts only variant 1 with a zero byte here (pre-BL).
    private const int PreBlByte = 0x16C;

    /**
     * Reads every installed package header and keeps the pre-BL one with the highest build.
     * @return The package path, or null when no pre-BL header is installed.
     */
    internal static string? Newest(string packageDirectory) => Directory.EnumerateFiles(packageDirectory, "*.pkg")
        .Select(path => (Path: path, Header: ReadHeader(path)))
        .Where(x => x.Header[VariantOffset] == 1 && x.Header[PreBlByte] == 0)
        .OrderByDescending(x => BinaryPrimitives.ReadUInt32LittleEndian(x.Header.AsSpan(BuildOffset)))
        .Select(x => x.Path).FirstOrDefault();

    private static byte[] ReadHeader(string path)
    {
        using var stream = File.OpenRead(path);
        byte[] header = new byte[HeaderSize];
        stream.ReadExactly(header);
        return header;
    }
}
