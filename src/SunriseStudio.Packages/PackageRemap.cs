using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SunriseStudio.Packages;

/**
 * One location holding an own tag (kind 1) or an own package id (kind 5). Entry names an entry of
 * the package, MiscEntry its misc region, SidecarEntry its merge sidecar (kind 3 holds a tag).
 */
public sealed record RemapLocation(ushort Package, uint Entry, uint Offset, byte Kind);

/**
 * Moves a mod's package ids: copies its package files with every listed location rewritten, the
 * block and region digests recomputed, and moved files renamed. Sunrise's loader does the same.
 */
public static class PackageRemap
{
    public const uint MiscEntry = 0xFFFFFFFF, SidecarEntry = 0xFFFFFFFE;
    public const byte TagKind = 1, SidecarKind = 3, PackageKind = 5;
    // Header fields: package id, misc and metadata regions with their SHA-1 digests.
    private const int PackageIdField = 4, MiscOffsetField = 0xF0, MiscSizeField = 0xF4, MiscDigestField = 0xF8;
    private const int MetadataOffsetField = 0x110, MetadataSizeField = 0x114, MetadataDigestField = 0x118;
    // A block record keeps the SHA-1 of its stored bytes here; a sidecar keeps its package id and digest.
    private const int BlockRecordSize = 48, BlockDigestField = 12, SidecarPackageField = 8, SidecarDigestField = 12;

    private sealed class Loaded(string path, PackageFile file, byte[] data)
    {
        public string Path { get; } = path;
        public PackageFile File { get; } = file;
        public byte[] Data { get; } = data;
        public byte[]? Sidecar { get; set; }
        public bool MiscChanged { get; set; }
        public bool MetadataChanged { get; set; }
    }

    /**
     * @param files The mod's package files; a `.merge` file beside one is its sidecar.
     * @param moves Old package id to new.
     * @param outputDirectory Receives each file under its folder name, renamed when its id moved.
     * @return Each input path and the path of its copy; sidecars are written beside their copies.
     */
    public static IReadOnlyDictionary<string, string> Rewrite(IReadOnlyList<string> files, IReadOnlyList<RemapLocation> locations,
        IReadOnlyDictionary<ushort, ushort> moves, string outputDirectory)
    {
        var loaded = files.Select(path =>
        {
            var file = new PackageFile(path);
            file.LoadTables();
            var item = new Loaded(path, file, File.ReadAllBytes(path));
            string sidecar = System.IO.Path.ChangeExtension(path, ".merge");
            if (File.Exists(sidecar)) item.Sidecar = File.ReadAllBytes(sidecar);
            return item;
        }).ToArray();
        var families = loaded.GroupBy(item => item.File.Id).ToDictionary(group => group.Key, group => group.OrderBy(item => item.File.Patch).ToArray());
        var changedBlocks = new HashSet<(ushort Package, ushort Patch, int Block)>();
        foreach (var location in locations)
        {
            if (!families.TryGetValue(location.Package, out var family)) continue;
            if (location.Entry == MiscEntry)
                foreach (var item in family)
                {
                    int misc = (int)U32(item.Data, MiscOffsetField), size = (int)U32(item.Data, MiscSizeField);
                    item.MiscChanged |= Move(item.Data.AsSpan(misc, size), (int)location.Offset, location.Kind, moves);
                }
            else if (location.Entry == SidecarEntry)
            {
                foreach (var item in family.Where(item => item.Sidecar is not null))
                    Move(item.Sidecar, (int)location.Offset, TagKind, moves);
            }
            else
            {
                var (holder, block, at) = Locate(family, location);
                if (Move(holder.Data.AsSpan(at, Width(location.Kind)), 0, location.Kind, moves))
                    changedBlocks.Add((location.Package, holder.File.Patch, block));
            }
        }
        foreach (var (package, patch, block) in changedBlocks)
        {
            var family = families[package];
            var record = family.Single(item => item.File.Patch == patch).File.Blocks[block];
            byte[] digest = SHA1.HashData(family.Single(item => item.File.Patch == patch).Data.AsSpan((int)record.Offset, record.Size));
            foreach (var item in family.Where(item => item.File.Blocks.Length > block && item.File.Blocks[block].Patch == patch))
            {
                digest.CopyTo(item.Data, (int)(item.File.BlockOffset + block * BlockRecordSize + BlockDigestField));
                item.MetadataChanged = true;
            }
        }
        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in loaded)
        {
            bool moved = moves.TryGetValue(item.File.Id, out ushort target);
            if (moved) BinaryPrimitives.WriteUInt16LittleEndian(item.Data.AsSpan(PackageIdField), target);
            if (item.MiscChanged) Digest(item.Data, MiscOffsetField, MiscSizeField, MiscDigestField);
            if (item.MetadataChanged) Digest(item.Data, MetadataOffsetField, MetadataSizeField, MetadataDigestField);
            string name = System.IO.Path.GetFileName(item.Path);
            if (moved) name = Renamed(name, target);
            string folder = System.IO.Path.Combine(outputDirectory, System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(item.Path)!));
            Directory.CreateDirectory(folder);
            string copy = System.IO.Path.Combine(folder, name);
            File.WriteAllBytes(copy, item.Data);
            if (item.Sidecar is { } sidecar)
            {
                if (moved) BinaryPrimitives.WriteUInt16LittleEndian(sidecar.AsSpan(SidecarPackageField), target);
                item.Data.AsSpan(MetadataDigestField, 20).CopyTo(sidecar.AsSpan(SidecarDigestField));
                File.WriteAllBytes(System.IO.Path.ChangeExtension(copy, ".merge"), sidecar);
            }
            output[item.Path] = copy;
        }
        return output;
    }

    /** Finds the file and byte that hold one entry offset, through the newest generation's tables. */
    private static (Loaded Holder, int Block, int At) Locate(Loaded[] family, RemapLocation location)
    {
        var newest = family[^1].File;
        if (location.Entry >= newest.EntryCount) throw new InvalidDataException($"Package {location.Package:X4} has no entry {location.Entry}.");
        var entry = newest.Entries[location.Entry];
        long remaining = location.Offset;
        int block = entry.StartBlock, skip = entry.StartOffset;
        while (true)
        {
            if (block >= newest.Blocks.Length) throw new InvalidDataException("A reference lies past its entry's blocks.");
            var record = newest.Blocks[block];
            if (remaining < record.Size - skip)
            {
                int inBlock = (int)(skip + remaining);
                if (inBlock + Width(location.Kind) > record.Size) throw new InvalidDataException("A reference straddles two blocks.");
                if ((record.Flags & 3) != 0) throw new InvalidDataException("A reference lies in a compressed or encrypted block.");
                var holder = family.FirstOrDefault(item => item.File.Patch == record.Patch)
                    ?? throw new InvalidDataException($"Package {location.Package:X4} generation {record.Patch} is not part of the mod.");
                return (holder, block, (int)record.Offset + inBlock);
            }
            remaining -= record.Size - skip;
            skip = 0;
            block++;
        }
    }

    /** Rewrites one value in place. @return True when it moved. */
    private static bool Move(Span<byte> bytes, int offset, byte kind, IReadOnlyDictionary<ushort, ushort> moves)
    {
        if (kind == PackageKind)
        {
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
            if (!moves.TryGetValue(id, out ushort target)) return false;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[offset..], target);
            return true;
        }
        uint tag = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
        if (!TagHashes.IsTag(tag) || TagHashes.Package(tag) > ushort.MaxValue) throw new InvalidDataException($"0x{tag:X8} is not a tag.");
        ushort package = (ushort)TagHashes.Package(tag);
        if (!moves.TryGetValue(package, out ushort moved)) return false;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], tag + (uint)((moved - package) << TagHashes.EntryBits));
        return true;
    }

    private static int Width(byte kind) => kind == PackageKind ? 2 : 4;

    private static void Digest(byte[] data, int offsetField, int sizeField, int digestField) =>
        SHA1.HashData(data.AsSpan((int)U32(data, offsetField), (int)U32(data, sizeField))).CopyTo(data, digestField);

    /** `<family>_<id>_<generation>.pkg` with the id replaced. */
    private static string Renamed(string name, ushort id)
    {
        string stem = System.IO.Path.GetFileNameWithoutExtension(name);
        int last = stem.LastIndexOf('_'), before = stem.LastIndexOf('_', last - 1);
        return $"{stem[..(before + 1)]}{id:x4}{stem[last..]}.pkg";
    }

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
}
