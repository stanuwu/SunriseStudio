using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SunriseStudio.Packages;

/** Key bytes come from the owned installation and never enter Studio project files. */
internal sealed class PackageKeys : IDisposable
{
    private readonly byte[] bytes;
    internal ReadOnlySpan<byte> Primary => bytes.AsSpan(0, 16);
    internal ReadOnlySpan<byte> Alternate => bytes.AsSpan(16, 16);
    internal ReadOnlySpan<byte> Nonce => bytes.AsSpan(32, 12);
    internal string Source { get; }

    private PackageKeys(byte[] bytes, string source) => (this.bytes, Source) = (bytes, source);

    /** An explicit source wins; the installed export is preferred to the executable. */
    internal static string ResolveSource(string gameRoot, string? explicitSource)
    {
        if (!string.IsNullOrWhiteSpace(explicitSource)) return Path.GetFullPath(explicitSource);
        string export = ExportPath(gameRoot);
        return Path.GetFullPath(File.Exists(export) ? export : Path.Combine(gameRoot, "destiny2.exe"));
    }

    private static string ExportPath(string gameRoot) =>
        Path.Combine(gameRoot, "bin", "x64", "Sunrise", "exports", "package_keys.bin");

    /** Exported keys and unpacked executables both belong to the installed build. */
    internal static PackageKeys Load(string gameRoot, string? explicitSource)
    {
        string source = ResolveSource(gameRoot, explicitSource);
        byte[] image = [];
        try
        {
            image = File.ReadAllBytes(source);
            if (image.AsSpan().StartsWith("SRPKKEYS"u8))
                return new PackageKeys(ReadExport(image), source);
            var sections = ReadSections(image);
            var table = FindTarget(image, sections,
                "0F 10 05 ? ? ? ? 48 8D 64 24 F8 48 89 2C 24 48 8D 2D ? ? ? ? E9",
                3, 7, 44);
            var token = FindTarget(image, sections,
                "0F 10 44 24 ? 41 B8 14 00 00 00 48 8D 15 ? ? ? ? 0F 10 4C 24 ? 48 8D 8C 24 ? ? ? ?",
                14, 18, 16);
            byte[] result = new byte[44];
            for (int i = 0; i < 16; i++)
                result[i] = unchecked((byte)(token[i] + table[16 + i]));
            table[..16].CopyTo(result.AsSpan(16));
            table[32..44].CopyTo(result.AsSpan(32));
            return new PackageKeys(result, source);
        }
        catch (Exception error) when (error is InvalidDataException or IOException
            or UnauthorizedAccessException or OverflowException)
        {
            // Without a key export, the packed executable never yields keys, so name the fix, not the signature miss.
            if (string.IsNullOrWhiteSpace(explicitSource) && !File.Exists(ExportPath(gameRoot)))
                throw new InvalidDataException("No decryption keys were found. Sunrise has not exported its package keys: "
                    + "set client.exports.package_keys to true, start the game with Sunrise once, then open the installation again.", error);
            throw new InvalidDataException($"Cannot read package keys from {Path.GetFileName(source)}: {error.Message} "
                + "Set client.exports.package_keys to true and run Sunrise once.", error);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(image);
        }
    }

    /** Version 1 holds two AES-128 keys and a 12-byte nonce for build 86657. */
    private static byte[] ReadExport(byte[] image)
    {
        const int headerSize = 16, payloadSize = 44;
        if (image.Length != headerSize + payloadSize)
            throw new InvalidDataException("Invalid package key export length.");
        if (U32(image, 8) != 1)
            throw new InvalidDataException("Unsupported package key export version.");
        if (U32(image, 12) != 86657)
            throw new InvalidDataException("Package key export is not for build 86657.");
        return image.AsSpan(headerSize, payloadSize).ToArray();
    }

    /** PE section ranges are checked before an instruction operand is resolved. */
    private static List<Section> ReadSections(byte[] image)
    {
        if (image.Length < 64 || image[0] != 'M' || image[1] != 'Z')
            throw new InvalidDataException("Invalid PE header.");
        int pe = checked((int)U32(image, 0x3C));
        if (pe < 0 || pe > image.Length - 26 || U32(image, pe) != 0x4550)
            throw new InvalidDataException("Invalid PE signature.");
        int count = U16(image, pe + 6);
        int table = checked(pe + 24 + U16(image, pe + 20));
        if (count is < 1 or > 96 || U16(image, pe + 24) != 0x20B
            || table > image.Length - count * 40)
            throw new InvalidDataException("Unsupported PE section table.");
        var sections = new List<Section>(count);
        for (int i = 0; i < count; i++)
        {
            int row = table + i * 40;
            int offset = checked((int)U32(image, row + 20));
            int size = checked((int)U32(image, row + 16));
            if (offset < 0 || size < 0 || offset > image.Length - size)
                throw new InvalidDataException("PE section exceeds the file.");
            sections.Add(new Section(U32(image, row + 12), offset, size,
                (U32(image, row + 36) & 0x20000000) != 0));
        }
        return sections;
    }

    /** A signature must match exactly once within executable file-backed sections. */
    private static ReadOnlySpan<byte> FindTarget(byte[] image, List<Section> sections,
        string signature, int displacementOffset, int instructionEnd, int length)
    {
        int?[] pattern = signature.Split(' ').Select(x => x == "?"
            ? (int?)null : Convert.ToInt32(x, 16)).ToArray();
        long target = -1;
        int matches = 0;
        foreach (var section in sections.Where(x => x.Executable))
        {
            var data = image.AsSpan(section.Offset, section.Size);
            for (int i = 0; i <= data.Length - pattern.Length; i++)
            {
                if (data[i] != pattern[0]) continue;
                bool match = true;
                for (int j = 1; j < pattern.Length; j++)
                    if (pattern[j] is int value && data[i + j] != value)
                    {
                        match = false;
                        break;
                    }
                if (!match) continue;
                matches++;
                target = (long)section.Rva + i + instructionEnd
                    + BinaryPrimitives.ReadInt32LittleEndian(data[(i + displacementOffset)..]);
            }
        }
        if (matches != 1)
            throw new InvalidDataException($"Key instruction has {matches} matches.");
        foreach (var section in sections)
            if (target >= section.Rva && target + length <= (long)section.Rva + section.Size)
                return image.AsSpan(checked(section.Offset + (int)(target - section.Rva)), length);
        throw new InvalidDataException("Key instruction target is outside file-backed sections.");
    }

    private static ushort U16(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private sealed record Section(uint Rva, int Offset, int Size, bool Executable);
    public void Dispose() => CryptographicOperations.ZeroMemory(bytes);
}
