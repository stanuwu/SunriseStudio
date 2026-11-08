using System.Buffers.Binary;
using System.Text;
using SunriseStudio.Packages;

namespace SunriseStudio.Projects;

/** What a reference map location holds. WideRef is reserved: a wide key does not name its package. */
public enum ReferenceKind : byte { TagRef = 1, WideRef = 2, Sidecar = 3, LuaConstant = 4, PackageId = 5 }

/**
 * One place that holds an own tag or package id: a package, an entry (or MiscEntry or
 * SidecarEntry) and a byte. A Lua constant names a Files index, a 1-based line and a 0-based column.
 */
public sealed record ReferenceRow(uint Package, uint Entry, uint Offset, ReferenceKind Kind);

/**
 * `refmap.bin`, every place a loader rewrites to move the mod. Little-endian: "SRMP", u16 version,
 * u16 packages, u32 rows, u16 files, u16 id length, the id, u16 package ids, u16-prefixed file
 * names, then 16-byte rows; each list pads to 4.
 */
public sealed record ReferenceMap(string ModId, IReadOnlyList<ushort> Packages, IReadOnlyList<string> Files, IReadOnlyList<ReferenceRow> Rows)
{
    public const string FileName = "refmap.bin";
    public const uint Magic = 0x504D5253;
    public const ushort Version = 1;
    public const uint MiscEntry = 0xFFFFFFFF, SidecarEntry = 0xFFFFFFFE;
    public const int RowSize = 16;
    // Header fields before the mod id.
    private const int HeaderSize = 16;

    /** The package locations a loader rewrites; Lua rows name script files, not packages. */
    public IReadOnlyList<RemapLocation> Locations => Rows.Where(row => row.Kind != ReferenceKind.LuaConstant)
        .Select(row => new RemapLocation((ushort)row.Package, row.Entry, row.Offset, (byte)row.Kind)).ToArray();

    public byte[] Write()
    {
        byte[] id = Encoding.ASCII.GetBytes(ModId);
        if (Packages.Count > ushort.MaxValue || Files.Count > ushort.MaxValue || id.Length > ushort.MaxValue)
            throw new InvalidDataException("The reference map has too many packages or files.");
        byte[][] files = Files.Select(Encoding.UTF8.GetBytes).ToArray();
        int packagesAt = Align(HeaderSize + id.Length), filesAt = Align(packagesAt + Packages.Count * 2);
        int rowsAt = Align(filesAt + files.Sum(file => 2 + file.Length));
        byte[] output = new byte[rowsAt + Rows.Count * RowSize];
        var span = output.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], (ushort)Packages.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)Rows.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], (ushort)files.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], (ushort)id.Length);
        id.CopyTo(span[HeaderSize..]);
        for (int i = 0; i < Packages.Count; i++) BinaryPrimitives.WriteUInt16LittleEndian(span[(packagesAt + i * 2)..], Packages[i]);
        int at = filesAt;
        foreach (byte[] file in files)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span[at..], (ushort)file.Length);
            file.CopyTo(span[(at + 2)..]);
            at += 2 + file.Length;
        }
        for (int i = 0; i < Rows.Count; i++)
        {
            var row = span.Slice(rowsAt + i * RowSize, RowSize);
            BinaryPrimitives.WriteUInt32LittleEndian(row, Rows[i].Package);
            BinaryPrimitives.WriteUInt32LittleEndian(row[4..], Rows[i].Entry);
            BinaryPrimitives.WriteUInt32LittleEndian(row[8..], Rows[i].Offset);
            row[12] = (byte)Rows[i].Kind;
        }
        return output;
    }

    /** Reads a map; anything past the rows or outside the format throws. */
    public static ReferenceMap Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != Version)
            throw new InvalidDataException("refmap.bin has the wrong magic or version.");
        int packageCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        uint rowCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        int fileCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]), idLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]);
        int at = HeaderSize;
        string id = Encoding.ASCII.GetString(Take(bytes, ref at, idLength));
        at = Align(at);
        var packages = new ushort[packageCount];
        for (int i = 0; i < packageCount; i++) packages[i] = BinaryPrimitives.ReadUInt16LittleEndian(Take(bytes, ref at, 2));
        at = Align(at);
        var files = new string[fileCount];
        for (int i = 0; i < fileCount; i++)
            files[i] = Encoding.UTF8.GetString(Take(bytes, ref at, BinaryPrimitives.ReadUInt16LittleEndian(Take(bytes, ref at, 2))));
        at = Align(at);
        if ((long)rowCount * RowSize != bytes.Length - at) throw new InvalidDataException("refmap.bin rows do not fill the file.");
        var rows = new ReferenceRow[rowCount];
        for (int i = 0; i < rows.Length; i++)
        {
            var row = Take(bytes, ref at, RowSize);
            byte kind = row[12];
            if (kind is < 1 or > 5 || row[13] != 0 || row[14] != 0 || row[15] != 0) throw new InvalidDataException("refmap.bin has an unknown row kind.");
            rows[i] = new(BinaryPrimitives.ReadUInt32LittleEndian(row), BinaryPrimitives.ReadUInt32LittleEndian(row[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(row[8..]), (ReferenceKind)kind);
        }
        return new(id, packages, files, rows);
    }

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> bytes, ref int at, int count)
    {
        if (count < 0 || at > bytes.Length - count) throw new InvalidDataException("refmap.bin is cut short.");
        var slice = bytes.Slice(at, count);
        at += count;
        return slice;
    }

    private static int Align(int value) => (value + 3) & ~3;
}
