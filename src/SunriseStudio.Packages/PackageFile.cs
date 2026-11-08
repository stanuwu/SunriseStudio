using System.Buffers.Binary;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Packages;

/** Metadata comes from the header, including packages whose names carry no numeric id. */
internal sealed class PackageFile
{
    internal string Path { get; }
    internal string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    internal ushort Id { get; }
    internal ushort Patch { get; }
    internal uint Build { get; }
    internal ulong Group { get; }
    internal int Variant { get; }
    internal int EntryCount { get; }
    internal int BlockCount { get; }
    internal long EntryOffset { get; }
    internal long BlockOffset { get; private set; }
    internal long MiscOffset { get; }
    internal PackageEntry[] Entries { get; private set; } = [];
    internal BlockRecord[] Blocks { get; private set; } = [];

    /** Variant 1 sizes the entry region with its own count, not the logical entry count. */
    internal PackageFile(string path)
    {
        Path = path;
        byte[] header = ReadAt(path, 0, 0x180);
        if (U16(header, 0) != 38 || U16(header, 2) != 2)
            throw new InvalidDataException($"Unsupported package format: {path}");
        Id = U16(header, 4);
        Patch = U16(header, 0x20);
        Build = U32(header, 0x18);
        Group = U64(header, 8);
        Variant = header[0x1A];
        EntryCount = checked((int)U32(header, 0xB4));
        BlockCount = checked((int)U32(header, 0xD0));
        MiscOffset = U32(header, 0xF0);
        if (EntryCount is < 1 or > TagHashes.EntriesPerPackage || BlockCount is < 1 or > 16384)
            throw new InvalidDataException($"Invalid package table counts: {path}");
        if (Variant == 0)
        {
            EntryOffset = U32(header, 0xB8);
            BlockOffset = U32(header, 0xD4);
        }
        else if (Variant == 1)
        {
            EntryOffset = U32(header, 0x110) + 96L;
        }
        else
            throw new InvalidDataException($"Unsupported package variant: {Variant}");
    }

    /** Reads the entry table and the block table. */
    internal void LoadTables()
    {
        if (Variant == 1)
        {
            byte[] prefix = ReadAt(Path, EntryOffset - 20, 20);
            ulong physicalCount = U64(prefix, 4);
            if (U32(prefix, 0) != TagArray.Marker || U32(prefix, 12) != PackageEntryRecord.Class
                || physicalCount < (ulong)EntryCount || physicalCount > TagHashes.EntriesPerPackage)
                throw new InvalidDataException($"Invalid entry region: {Path}");
            BlockOffset = checked(EntryOffset + (long)physicalCount * 16 + 32);
            prefix = ReadAt(Path, BlockOffset - 20, 20);
            if (U32(prefix, 0) != TagArray.Marker || U32(prefix, 12) != PackageBlockRecord.Class
                || U64(prefix, 4) < (ulong)BlockCount || U64(prefix, 4) > 16384)
                throw new InvalidDataException($"Invalid block region: {Path}");
        }
        byte[] entries = ReadAt(Path, EntryOffset, checked(EntryCount * 16));
        Entries = new PackageEntry[EntryCount];
        string name = Name;
        for (int i = 0; i < EntryCount; i++)
        {
            int offset = i * 16;
            uint typeInfo = U32(entries, offset + 4);
            ulong blockInfo = U64(entries, offset + 8);
            Entries[i] = new PackageEntry(TagHashes.Make(Id, i),
                U32(entries, offset), (int)((typeInfo >> 9) & 127),
                (int)((typeInfo >> 6) & 7), checked((int)(blockInfo >> 28)), name, Id, i)
            {
                TypeInfo = typeInfo,
                StartBlock = (int)(blockInfo & 0x3FFF),
                StartOffset = (int)(((blockInfo >> 14) & 0x3FFF) << 4)
            };
        }
        byte[] blocks = ReadAt(Path, BlockOffset, checked(BlockCount * 48));
        Blocks = new BlockRecord[BlockCount];
        for (int i = 0; i < BlockCount; i++)
        {
            int offset = i * 48;
            Blocks[i] = new BlockRecord(U32(blocks, offset),
                checked((int)U32(blocks, offset + 4)), U16(blocks, offset + 8),
                U16(blocks, offset + 10), blocks.AsSpan(offset + 32, 16).ToArray());
        }
    }

    /** Every requested byte must exist; a short read cannot become a valid tag. */
    internal static byte[] ReadAt(string path, long offset, int count)
    {
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = RandomAccess.GetLength(file);
        if (offset < 0 || count < 0 || offset > length - count)
            throw new InvalidDataException($"Package range exceeds file: {path}");
        byte[] result = new byte[count];
        int read = 0;
        while (read < count)
        {
            int received = RandomAccess.Read(file, result.AsSpan(read), offset + read);
            if (received == 0) throw new EndOfStreamException(path);
            read += received;
        }
        return result;
    }

    internal static ushort U16(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    internal static uint U32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    internal static ulong U64(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
    internal static long I64(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
}

internal sealed record BlockRecord(uint Offset, int Size, ushort Patch, ushort Flags, byte[] Tag);
