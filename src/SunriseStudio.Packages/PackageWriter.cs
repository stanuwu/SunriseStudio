using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using static SunriseStudio.Packages.PackageFile;

namespace SunriseStudio.Packages;

public sealed record AuthoredPackageEntry(uint Reference, uint TypeInfo, byte[] Data, string? Name = null)
{
    public WideHashAliases WideHashes { get; init; } = WideHashAliases.Empty;
}
public sealed record PackageBuildArtifact(string Path, ushort PackageId, ushort PatchId,
    int EntryCount, int BlockCount, string Sha256)
{
    /** The written header's metadata digest, which merge data binds to; the receipt omits it. */
    [JsonIgnore] public byte[] MetadataDigest { get; init; } = [];
    /** Each written entry's class; the receipt omits it. */
    [JsonIgnore] public uint[] EntryClasses { get; init; } = [];
    /** Each written entry's size in bytes; the receipt omits it. */
    [JsonIgnore] public long[] EntrySizes { get; init; } = [];
}

/** Authored blocks are uncompressed; inherited records still name their original patch files. */
public static partial class PackageWriter
{
    // The package format fixes these block, table, and disk-alignment sizes.
    private const int BlockSize = 0x40000, EntrySize = 16, BlockRecordSize = 48, DiskAlignment = 0x800;
    private const int HeaderSize = 0x180, MiscOffset = 0x1000, MetadataMinimumOffset = 0x1800;
    // The header holds the metadata digest; entry records start at metadata +0x60, size from bit 28.
    private const int MetadataDigestField = 0x118, MetadataDigestSize = 20, EntryTable = 0x60, EntrySizeShift = 28;
    private const uint ArrayMarker = TagArray.Marker, EntryClass = PackageEntryRecord.Class, BlockClass = PackageBlockRecord.Class;
    private const uint SupportedBuild = 86657;
    /** The client's package header check refuses ids above 0x0CFF, although the registrar takes more. */
    public const ushort FirstNewPackageId = 0x0100, LastNewPackageId = 0x0CFF;
    public static string NewPackageIdRange => $"{FirstNewPackageId:X4} to {LastNewPackageId:X4}";
    public static bool IsNewPackageId(int id) => id is >= FirstNewPackageId and <= LastNewPackageId;

    /**
     * Inherited blocks may refer to several older generations of the same package family.
     * @param otherDirectory Also searched, after the source's folder: a mod's patch sits apart from the installed generations.
     */
    public static IReadOnlyList<string> GetRequiredPredecessors(string sourcePackagePath, string? otherDirectory = null)
    {
        var source = new PackageFile(sourcePackagePath);
        source.LoadTables();
        int separator = source.Path.LastIndexOf('_');
        if (separator < 0) throw new InvalidDataException("Package family has no patch suffix.");
        string stem = source.Path[..separator];
        var paths = new List<string>();
        foreach (ushort patch in source.Blocks.Select(x => x.Patch).Distinct().Order())
        {
            string path = $"{stem}_{patch}.pkg";
            if (!File.Exists(path) && otherDirectory is not null) path = Path.Combine(otherDirectory, Path.GetFileName(path));
            if (!File.Exists(path)) throw new FileNotFoundException("An inherited package generation is missing.", path);
            var predecessor = new PackageFile(path);
            if (predecessor.Id != source.Id || predecessor.Patch != patch)
                throw new InvalidDataException("Inherited package identity mismatch.");
            paths.Add(path);
        }
        return paths;
    }

    /**
     * Reads one entry whose blocks are stored plain, as the writer stores every new block.
     * @param otherDirectory Also searched for the generation files that hold the entry's blocks.
     * @return The bytes, or null when a block is compressed or encrypted; read those through a catalog.
     */
    public static byte[]? ReadPlainEntry(string packagePath, int entryIndex, string? otherDirectory = null)
    {
        var file = new PackageFile(packagePath);
        file.LoadTables();
        if ((uint)entryIndex >= (uint)file.EntryCount) throw new InvalidDataException("The entry is outside the package.");
        var entry = file.Entries[entryIndex];
        string stem = file.Path[..file.Path.LastIndexOf('_')];
        var result = new byte[entry.Size];
        int copied = 0, block = entry.StartBlock, skip = entry.StartOffset;
        while (copied < result.Length)
        {
            if ((uint)block >= (uint)file.Blocks.Length) throw new InvalidDataException("The entry runs past its block table.");
            var record = file.Blocks[block];
            // Bit 0 marks a compressed block, bit 1 an encrypted one.
            if ((record.Flags & 3) != 0) return null;
            string path = $"{stem}_{record.Patch}.pkg";
            if (!File.Exists(path) && otherDirectory is not null) path = Path.Combine(otherDirectory, Path.GetFileName(path));
            int count = Math.Min(record.Size - skip, result.Length - copied);
            if (count <= 0) throw new InvalidDataException("The entry runs past its block.");
            PackageFile.ReadAt(path, record.Offset + skip, count).CopyTo(result, copied);
            copied += count;
            skip = 0;
            block++;
        }
        return result;
    }

    /** @return The misc block of one package file, as the patch writer copies it. */
    public static byte[] ReadMisc(string packagePath)
    {
        byte[] header = ReadAt(packagePath, 0, HeaderSize);
        return ReadAt(packagePath, U32(header, 0xF0), checked((int)U32(header, 0xF4)));
    }

    /**
     * A patch keeps stable entry indices and every inherited block record byte for byte.
     * @param misc Replaces the source's misc block, such as after a dependency record edit.
     */
    public static PackageBuildArtifact WritePatch(string sourcePackagePath, string outputPath,
        IReadOnlyDictionary<int, byte[]> replacements, string? protectedGameRoot = null, byte[]? misc = null,
        string? predecessorDirectory = null)
    {
        var source = new PackageFile(sourcePackagePath);
        source.LoadTables();
        GetRequiredPredecessors(sourcePackagePath, predecessorDirectory);
        if (source.Variant != 1)
            throw new NotSupportedException("Package authoring requires the pre-BL version-38 table layout.");
        if (source.Patch >= 255 || replacements.Count == 0 && misc is null)
            throw new InvalidDataException("A patch needs edits and a patch number below 255.");
        ushort patch = (ushort)(source.Patch + 1);
        string stem = source.Name[..source.Name.LastIndexOf('_')];
        if (!Path.GetFileName(outputPath).Equals($"{stem}_{patch}.pkg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Patch filename must match its source family and next patch number.");
        if (Path.GetFullPath(outputPath).Equals(Path.GetFullPath(sourcePackagePath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Package output cannot replace its source.");
        ValidateOutput(outputPath, protectedGameRoot ?? Path.GetDirectoryName(sourcePackagePath)!);
        byte[] header = ReadAt(source.Path, 0, HeaderSize);
        misc ??= ReadAt(source.Path, U32(header, 0xF0), checked((int)U32(header, 0xF4)));
        int metadataOffset = checked((int)U32(header, 0x110));
        byte[] originalMetadata = ReadAt(source.Path, metadataOffset, checked((int)U32(header, 0x114)));
        if (originalMetadata.Length < 0x60 || U64(originalMetadata, 8) != 1)
            throw new NotSupportedException("Unknown package metadata layout.");
        int oldBlockRelative = checked((int)(source.BlockOffset - metadataOffset));
        int oldCapacity = checked((int)U64(originalMetadata, oldBlockRelative - 16));
        int oldTail = checked(oldBlockRelative + oldCapacity * BlockRecordSize);
        if (oldTail > originalMetadata.Length) throw new InvalidDataException("Block table exceeds metadata.");
        var entries = replacements.OrderBy(x => x.Key).ToArray();
        foreach (var entry in entries)
            if (entry.Key < 0 || entry.Key >= source.EntryCount || entry.Value is null)
                throw new InvalidDataException("Replacement entry is outside the package.");
        var (placements, payloads) = Place(entries.Select(x => x.Value), source.BlockCount);
        int count = source.BlockCount + payloads.Count;
        if (count > 16384) throw new InvalidDataException("Package exceeds its block limit.");
        byte[] metadata = GrowBlockTable(originalMetadata, oldBlockRelative, oldCapacity, oldTail, count);
        for (int i = 0; i < entries.Length; i++)
            Put64(metadata, 0x60 + entries[i].Key * EntrySize + 8, placements[i]);
        Put16(header, 0x20, patch);
        return Write(outputPath, header, misc, metadata, oldBlockRelative, source.BlockCount, payloads);
    }

    /** Game-facing output needs an installed header profile with a patchable locale. */
    public static PackageBuildArtifact WriteNew(string outputPath, ushort packageId,
        IReadOnlyList<AuthoredPackageEntry> entries, string? profilePackagePath = null,
        string? protectedGameRoot = null)
    {
        ValidateOutput(outputPath, protectedGameRoot);
        if (!IsNewPackageId(packageId) || entries.Count is < 1 or > TagHashes.EntriesPerPackage)
            throw new InvalidDataException($"New packages need an id from {NewPackageIdRange} and 1 to 8192 entries.");
        if (entries.Any(x => x.Data is null || x.Name?.Contains('\0') == true || x.WideHashes is null))
            throw new InvalidDataException("Invalid authored entry.");
        byte[] misc = CreateMisc(packageId, entries);
        byte[] header = profilePackagePath is null ? new byte[HeaderSize] : ReadAt(profilePackagePath, 0, HeaderSize);
        if (profilePackagePath is not null && (U16(header, 0) != 38 || U16(header, 2) != 2
            || header[0x1A] != 1 || header[0x16C] != 0))
            throw new NotSupportedException("New packages need a patchable pre-BL version-38 header profile.");
        if (profilePackagePath is null)
        {
            Put16(header, 0, 38);
            Put16(header, 2, 2);
            Put32(header, 0x18, SupportedBuild);
            Put32(header, 0x1C, 2);
        }
        Put16(header, 4, packageId);
        Put16(header, 0x20, 0);
        Put16(header, 0x22, 0);
        Put32(header, 0xB4, (uint)entries.Count);
        var (placements, payloads) = Place(entries.Select(x => x.Data), 0);
        int entryCapacity = Math.Max(8, entries.Count);
        int blockRelative = 0x60 + entryCapacity * EntrySize + 0x20;
        int blockCapacity = Math.Max(8, payloads.Count);
        byte[] metadata = new byte[blockRelative + blockCapacity * BlockRecordSize];
        Put64(metadata, 0, (ulong)metadata.Length);
        Put64(metadata, 8, 1);
        Put64(metadata, 0x10, (ulong)entries.Count);
        Put64(metadata, 0x18, 0x38);
        Put64(metadata, 0x20, (ulong)payloads.Count);
        Put64(metadata, 0x28, (ulong)(blockRelative - 0x38));
        ArrayPrefix(metadata, 0x60, entryCapacity, EntryClass);
        ArrayPrefix(metadata, blockRelative, blockCapacity, BlockClass);
        for (int i = 0; i < entries.Count; i++)
        {
            Put32(metadata, 0x60 + i * EntrySize, entries[i].Reference);
            Put32(metadata, 0x64 + i * EntrySize, entries[i].TypeInfo);
            Put64(metadata, 0x68 + i * EntrySize, placements[i]);
        }
        metadata = AppendRootManifestMap(metadata, packageId, entries);
        return Write(outputPath, header, misc, metadata, blockRelative, 0, payloads);
    }

    /** Full replacements keep their installed filename and rebuild every physical block. */
    public static PackageBuildArtifact WriteReplacement(string sourcePackagePath, string outputPath,
        IReadOnlyDictionary<int, byte[]> replacements, Func<uint, byte[]> readOriginal,
        string? protectedGameRoot = null)
    {
        ArgumentNullException.ThrowIfNull(readOriginal);
        var source = new PackageFile(sourcePackagePath);
        source.LoadTables();
        if (source.Variant != 1)
            throw new NotSupportedException("Package authoring requires the pre-BL version-38 table layout.");
        if (!Path.GetFileName(outputPath).Equals(source.Name + ".pkg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Replacement filename must match its installed package.");
        ValidateOutput(outputPath, protectedGameRoot ?? Path.GetDirectoryName(sourcePackagePath)!);
        foreach (var (index, bytes) in replacements)
            if ((uint)index >= source.EntryCount || bytes is null)
                throw new InvalidDataException("Replacement entry is outside the package.");
        byte[] header = ReadAt(source.Path, 0, HeaderSize);
        byte[] misc = ReadAt(source.Path, U32(header, 0xF0), checked((int)U32(header, 0xF4)));
        int metadataOffset = checked((int)U32(header, 0x110));
        byte[] originalMetadata = ReadAt(source.Path, metadataOffset, checked((int)U32(header, 0x114)));
        if (originalMetadata.Length < 0x60 || U64(originalMetadata, 8) != 1)
            throw new NotSupportedException("Unknown package metadata layout.");
        int blockRelative = checked((int)(source.BlockOffset - metadataOffset));
        int oldCapacity = checked((int)U64(originalMetadata, blockRelative - 16));
        int oldTail = checked(blockRelative + oldCapacity * BlockRecordSize);
        if (oldTail > originalMetadata.Length) throw new InvalidDataException("Block table exceeds metadata.");
        IEnumerable<byte[]> ReadEntries()
        {
            foreach (var entry in source.Entries)
            {
                if (replacements.TryGetValue(entry.EntryIndex, out var edited)) { yield return edited; continue; }
                byte[] bytes = readOriginal(entry.TagHash);
                if (bytes.Length != entry.Size) throw new InvalidDataException("Source asset size changed.");
                yield return bytes;
            }
        }
        var (placements, payloads) = Place(ReadEntries(), 0);
        byte[] metadata = GrowBlockTable(originalMetadata, blockRelative, oldCapacity, oldTail, payloads.Count);
        for (int index = 0; index < placements.Length; index++)
            Put64(metadata, 0x68 + index * EntrySize, placements[index]);
        return Write(outputPath, header, misc, metadata, blockRelative, 0, payloads);
    }

    /**
     * Copies the metadata with room for count block records, then sets the block count and capacity.
     * @param oldTail End of the old block table; the bytes after it move back by the added records.
     * @return The new metadata; the tag-pair link at +0x38 follows the moved bytes.
     */
    private static byte[] GrowBlockTable(byte[] original, int blockRelative, int oldCapacity, int oldTail, int count)
    {
        int capacity = Math.Max(oldCapacity, count);
        int shift = checked((capacity - oldCapacity) * BlockRecordSize);
        byte[] metadata = new byte[checked(original.Length + shift)];
        original.AsSpan(0, oldTail).CopyTo(metadata);
        original.AsSpan(oldTail).CopyTo(metadata.AsSpan(oldTail + shift));
        Put64(metadata, 0, (ulong)metadata.Length);
        Put64(metadata, 0x20, (ulong)count);
        Put64(metadata, blockRelative - 16, (ulong)capacity);
        if (U64(metadata, 0x30) != 0)
        {
            ulong relative = U64(metadata, 0x38);
            if (relative + 0x48 < (ulong)oldTail || relative + 0x48 > (ulong)original.Length)
                throw new NotSupportedException("Unknown package tag-pair placement.");
            Put64(metadata, 0x38, checked(relative + (ulong)shift));
        }
        return metadata;
    }

    /** Entry starts are sixteen-byte aligned within full-size logical blocks. */
    private static (ulong[] Placements, List<byte[]> Blocks) Place(IEnumerable<byte[]> entries, int firstBlock)
    {
        var placements = new List<ulong>();
        var blocks = new List<byte[]>();
        long position = 0;
        foreach (byte[] entry in entries)
        {
            position = (position + 15) & ~15L;
            long blockIndex = firstBlock + position / BlockSize;
            if (blockIndex >= 16384) throw new InvalidDataException("Package exceeds its block limit.");
            placements.Add(((ulong)entry.Length << 28) | ((ulong)(position % BlockSize / 16) << 14) | (ulong)blockIndex);
            int copied = 0;
            while (copied < entry.Length)
            {
                int block = checked((int)(position / BlockSize));
                while (blocks.Count <= block) blocks.Add(new byte[BlockSize]);
                int offset = (int)(position % BlockSize);
                int count = Math.Min(BlockSize - offset, entry.Length - copied);
                entry.AsSpan(copied, count).CopyTo(blocks[block].AsSpan(offset));
                copied += count;
                position += count;
            }
        }
        if (firstBlock + blocks.Count > 16384) throw new InvalidDataException("Package exceeds its block limit.");
        if (blocks.Count == 0 && firstBlock == 0) blocks.Add(new byte[BlockSize]);
        return (placements.ToArray(), blocks);
    }

    /** Metadata digests cover only their declared regions; block digests cover stored bytes. */
    private static PackageBuildArtifact Write(string outputPath, byte[] header, byte[] misc,
        byte[] metadata, int blockRelative, int inherited, IReadOnlyList<byte[]> payloads)
    {
        ushort id = U16(header, 4), patch = U16(header, 0x20);
        int metadataOffset = Math.Max(MetadataMinimumOffset, Align(MiscOffset + misc.Length));
        int cursor = Align(checked(metadataOffset + metadata.Length));
        for (int i = 0; i < payloads.Count; i++)
        {
            int record = blockRelative + (inherited + i) * BlockRecordSize;
            metadata.AsSpan(record, BlockRecordSize).Clear();
            Put32(metadata, record, (uint)cursor);
            Put32(metadata, record + 4, (uint)payloads[i].Length);
            Put16(metadata, record + 8, patch);
            SHA1.HashData(payloads[i]).CopyTo(metadata, record + 12);
            cursor = Align(checked(cursor + payloads[i].Length));
        }
        int footer = cursor;
        int fileSize = checked(footer + DiskAlignment);
        Put32(header, 0xB0, 0x800);
        Put32(header, 0xD0, (uint)(inherited + payloads.Count));
        Put32(header, 0xF0, MiscOffset);
        Put32(header, 0xF4, (uint)misc.Length);
        SHA1.HashData(misc).CopyTo(header, 0xF8);
        Put32(header, 0x110, (uint)metadataOffset);
        Put32(header, 0x114, (uint)metadata.Length);
        SHA1.HashData(metadata).CopyTo(header, MetadataDigestField);
        Put32(header, 0x160, (uint)footer);
        Put32(header, 0x164, (uint)fileSize);
        string fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(fileSize);
                stream.Write(header);
                stream.Position = MiscOffset;
                stream.Write(misc);
                stream.Position = metadataOffset;
                stream.Write(metadata);
                for (int i = 0; i < payloads.Count; i++)
                {
                    stream.Position = U32(metadata, blockRelative + (inherited + i) * BlockRecordSize);
                    stream.Write(payloads[i]);
                }
                stream.Position = footer;
                stream.Write(BitConverter.GetBytes(0xDEADBEEFu));
                stream.Flush(true);
            }
            VerifyWritten(temporary, metadata, blockRelative, inherited, payloads);
            File.Move(temporary, fullPath, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        using var written = File.OpenRead(fullPath);
        int entryCount = checked((int)U32(header, 0xB4));
        return new(fullPath, id, patch, entryCount, inherited + payloads.Count, Convert.ToHexString(SHA256.HashData(written)))
        {
            MetadataDigest = header.AsSpan(MetadataDigestField, MetadataDigestSize).ToArray(),
            EntryClasses = Enumerable.Range(0, entryCount).Select(index => U32(metadata, EntryTable + index * EntrySize)).ToArray(),
            EntrySizes = Enumerable.Range(0, entryCount)
                .Select(index => (long)(U64(metadata, EntryTable + index * EntrySize + 8) >> EntrySizeShift)).ToArray()
        };
    }

    /** The temporary output must decode its tables and match every newly stored block. */
    private static void VerifyWritten(string path, byte[] metadata, int blockRelative,
        int inherited, IReadOnlyList<byte[]> payloads)
    {
        var package = new PackageFile(path);
        package.LoadTables();
        for (int i = 0; i < payloads.Count; i++)
        {
            int row = blockRelative + (inherited + i) * BlockRecordSize;
            byte[] stored = ReadAt(path, U32(metadata, row), payloads[i].Length);
            if (!stored.AsSpan().SequenceEqual(payloads[i])) throw new IOException("Package output verification failed.");
        }
    }

    /** Named tags use relative string pointers inside the package misc region. */
    private static byte[] CreateNames(ushort id, IReadOnlyList<AuthoredPackageEntry> entries)
    {
        var named = entries.Select((entry, index) => (entry, index))
            .Where(x => !string.IsNullOrWhiteSpace(x.entry.Name)).ToArray();
        if (named.Length == 0)
        {
            byte[] empty = new byte[0x60];
            Put64(empty, 0, 0x60);
            Put64(empty, 8, 3);
            return empty;
        }
        int capacity = Math.Max(8, named.Length);
        int strings = 0x80 + capacity * EntrySize;
        int total = checked(strings + 16 + named.Sum(x => Encoding.UTF8.GetByteCount(x.entry.Name!) + 1));
        byte[] misc = new byte[total];
        Put64(misc, 0, (ulong)total);
        Put64(misc, 8, 3);
        Put64(misc, 0x10, (ulong)named.Length);
        Put64(misc, 0x18, 0x58);
        ArrayPrefix(misc, 0x80, capacity, PackageNamedTagRow.Class);
        Put32(misc, strings + 4, SInlineString.Class);
        Put64(misc, strings + 8, (ulong)(total - strings - 16));
        int cursor = strings + 16;
        for (int i = 0; i < named.Length; i++)
        {
            int row = 0x80 + i * EntrySize;
            Put32(misc, row, TagHashes.Make(id, named[i].index));
            Put32(misc, row + 4, named[i].entry.Reference);
            Put64(misc, row + 8, (ulong)(cursor - row - 8));
            cursor += Encoding.UTF8.GetBytes(named[i].entry.Name!, misc.AsSpan(cursor)) + 1;
        }
        return misc;
    }

    /** Wide hashes append a separate typed array without moving the existing named-tag data. */
    private static byte[] CreateMisc(ushort id, IReadOnlyList<AuthoredPackageEntry> entries)
    {
        long count = 0;
        foreach (var entry in entries)
        {
            count = checked(count + entry.WideHashes.Count);
            if (count > WideHashAliases.MaximumCount)
                throw new InvalidDataException("A package has too many wide-hash aliases.");
        }
        byte[] names = CreateNames(id, entries);
        var aliases = entries.SelectMany((entry, index) => entry.WideHashes.Select(hash =>
            (Hash: hash, Tag: TagHashes.Make(id, index), Class: entry.Reference)))
            .OrderBy(row => row.Hash).ToArray();
        if (aliases.Length == 0) return names;
        if (aliases.Select(row => row.Hash).Distinct().Count() != aliases.Length)
            throw new InvalidDataException("Package wide-hash aliases are duplicated.");
        int rows = checked((names.Length + 15) / 16 * 16 + 0x20);
        byte[] misc = new byte[checked(rows + aliases.Length * EntrySize)];
        names.CopyTo(misc, 0);
        Put64(misc, 0, (ulong)misc.Length);
        Put64(misc, 0x30, (ulong)aliases.Length);
        Put64(misc, 0x38, (ulong)(rows - 0x48));
        ArrayPrefix(misc, rows, aliases.Length, Hash64ReferenceRow.Class);
        for (int i = 0; i < aliases.Length; i++)
        {
            int row = rows + i * EntrySize;
            Put64(misc, row, aliases[i].Hash);
            Put32(misc, row + 8, aliases[i].Tag);
            Put32(misc, row + 12, aliases[i].Class);
        }
        return misc;
    }

    /** Output paths cannot pass through a link or overwrite an existing package. */
    private static void ValidateOutput(string path, string? protectedRoot)
    {
        string full = Path.GetFullPath(path);
        if (!Path.GetExtension(full).Equals(".pkg", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Package output needs a .pkg extension.");
        if (File.Exists(full)) throw new IOException("Package output already exists.");
        if (protectedRoot is not null)
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedRoot));
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Packages cannot be written into the original installation.");
        }
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Package output paths cannot contain links or junctions.");
    }

    private static void ArrayPrefix(byte[] data, int target, int capacity, uint elementClass)
    {
        Put32(data, target - 20, ArrayMarker);
        Put64(data, target - 16, (ulong)capacity);
        Put64(data, target - 8, elementClass);
    }

    private static int Align(int value) => checked(value + DiskAlignment - 1) & -DiskAlignment;
    private static void Put16(byte[] data, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);
    private static void Put32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    private static void Put64(byte[] data, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset), value);
}
