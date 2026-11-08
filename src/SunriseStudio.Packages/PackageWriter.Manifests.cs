using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using static SunriseStudio.Packages.PackageFile;
using ManifestView = SunriseStudio.Tags.Generated.PackageManifest;

namespace SunriseStudio.Packages;

public sealed record PackageRootManifest(uint RootTag, uint ManifestTag);

public static partial class PackageWriter
{
    private static readonly int RootPairStride = Unsafe.SizeOf<PackageRootManifestPair>();

    /** Manifest-root entries resolve through the package metadata's sorted tag pairs. */
    public static IReadOnlyList<PackageRootManifest> ReadRootManifests(string packagePath)
    {
        byte[] header = ReadAt(packagePath, 0, HeaderSize);
        if (header[0x1A] != 1) throw new NotSupportedException("Root manifests need pre-BL package metadata.");
        byte[] metadata = ReadAt(packagePath, U32(header, 0x110), checked((int)U32(header, 0x114)));
        if (metadata.Length < 0x60) throw new InvalidDataException("Package metadata is truncated.");
        ulong count = U64(metadata, 0x30);
        if (count == 0) return [];
        long array = checked(0x38 + BinaryPrimitives.ReadInt64LittleEndian(metadata.AsSpan(0x38)));
        if (count > TagArray.MaximumCheckedCount || array < TagView.ClassWordSize || array > metadata.Length - TagArray.HeaderSize
            || U32(metadata, (int)array - TagView.ClassWordSize) != ArrayMarker || U64(metadata, (int)array) < count
            || U32(metadata, (int)array + TagArray.ElementClassOffset) != PackageRootManifestPair.Class
            || count * (ulong)RootPairStride > (ulong)(metadata.Length - array - TagArray.HeaderSize))
            throw new InvalidDataException("Package root manifest pairs are invalid.");
        var rows = new List<PackageRootManifest>();
        for (int i = 0; i < (int)count; i++)
        {
            ref readonly var pair = ref TagView.At<PackageRootManifestPair>(metadata, checked((int)array + TagArray.HeaderSize + i * RootPairStride));
            var row = new PackageRootManifest(pair.RootTag, pair.ManifestTag);
            if (rows.Count > 0 && rows[^1].RootTag > row.RootTag)
                throw new InvalidDataException("Package root manifest pairs must be sorted.");
            if (rows.Count > 0 && rows[^1].RootTag == row.RootTag)
            {
                if (rows[^1].ManifestTag != row.ManifestTag)
                    throw new InvalidDataException("A package root has conflicting manifest targets.");
                continue;
            }
            rows.Add(row);
        }
        return rows;
    }

    /** Every new root keeps its root flag and must have exactly one authored manifest. */
    private static byte[] AppendRootManifestMap(byte[] metadata, ushort package,
        IReadOnlyList<AuthoredPackageEntry> entries)
    {
        uint Tag(int index) => TagHashes.Make(package, index);
        var roots = Enumerable.Range(0, entries.Count)
            .Where(index => (entries[index].TypeInfo & 0xF000) == 0x2000).Select(Tag).ToHashSet();
        var manifests = new SortedDictionary<uint, uint>();
        for (int index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry.Reference != PackageManifest.ClassId) continue;
            byte[] data = entry.Data;
            var manifest = PackageManifest.Read(data);
            if (!TagView.Fits<ManifestView>(data, 0) || TagView.At<ManifestView>(data).TagSize != (ulong)data.Length
                || TagView.At<ManifestView>(data).SelfTag != Tag(index))
                throw new InvalidDataException("An authored manifest has an invalid size or identity.");
            uint root = TagView.At<ManifestView>(data).RootTag;
            if (!roots.Contains(root) || !manifests.TryAdd(root, Tag(index)))
                throw new InvalidDataException("Each authored manifest needs one unique root in its package.");
            if (!manifest.Lists(root) || !manifest.Lists(Tag(index)))
                throw new InvalidDataException("A native manifest must include its root and its own entry.");
        }
        if (!roots.SetEquals(manifests.Keys))
            throw new InvalidDataException("A new manifest-root entry has no authored dependency manifest.");
        if (roots.Count == 0) return metadata;
        int dataStart = checked(((metadata.Length + 15) & ~15) + 32);
        byte[] output = new byte[checked(dataStart + manifests.Count * RootPairStride)];
        metadata.CopyTo(output, 0);
        ArrayPrefix(output, dataStart, manifests.Count, PackageRootManifestPair.Class);
        Put64(output, 0x30, (ulong)manifests.Count);
        Put64(output, 0x38, (ulong)(dataStart - 0x48));
        int rowAt = dataStart;
        foreach (var (root, manifest) in manifests)
        {
            ref var pair = ref TagView.Edit<PackageRootManifestPair>(output, rowAt);
            pair.RootTag = root;
            pair.ManifestTag = manifest;
            rowAt += RootPairStride;
        }
        Put64(output, 0, (ulong)output.Length);
        return output;
    }
}
