// The .Generated.cs name marks this file as generated code, which turns nullable checks off.
#nullable enable
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Authoring;

public sealed partial class ObjectEditContext
{
    private readonly Dictionary<uint, GeneratedObjectAsset> generated = [];
    private readonly HashSet<ushort> generatedPackages = [];
    private readonly Dictionary<string, uint> generatedIds = new(StringComparer.Ordinal);
    private ushort? generatedPackage;
    private int generatedIndex;
    private bool isolationSnapshotRead;
    private OriginalConfigReferences.Snapshot? isolationSnapshot;

    internal ObjectEditCompileOptions Options { get; }

    internal byte[] ReadLayer(uint tag) => working.TryGetValue(tag, out var bytes) ? bytes : readRaw(tag);

    /** One compile admits one original snapshot; its raw changes never enter that shared snapshot. */
    internal OriginalConfigReferences.Snapshot? IsolationSnapshot
    {
        get
        {
            if (!isolationSnapshotRead)
            {
                isolationSnapshot = Options.RawChangedTags is { } changed
                    ? OriginalConfigReferences.Open(Catalog, changed) : null;
                isolationSnapshotRead = true;
            }
            return isolationSnapshot;
        }
    }

    /** The raw layer stays immutable even when a derived rule changes an array's size. */
    internal ReadOnlyMemory<byte> Baseline(uint tag)
    {
        _ = Current(tag);
        return raw[tag];
    }

    internal void ReplaceCurrent(uint tag, byte[] bytes)
    {
        _ = Current(tag);
        working[tag] = bytes;
    }

    /** Stable recipe order and project identity choose unused native package namespaces. */
    internal uint ReserveGenerated(string id, string name, uint sourceTag)
    {
        if (generatedIds.TryGetValue(id, out uint existing)) return existing;
        if (!Catalog.TryGetEntry(sourceTag, out var source)) throw new InvalidDataException("A generated asset template is missing.");
        if (generatedPackage is null || generatedIndex == 8192)
        {
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes("Sunrise object edits\n" + Options.Identity));
            int count = PackageWriter.LastNewPackageId - PackageWriter.FirstNewPackageId + 1;
            int first = (int)(BinaryPrimitives.ReadUInt32LittleEndian(digest) % count);
            generatedPackage = null;
            for (int offset = 0; offset < count; offset++)
            {
                ushort candidate = (ushort)(PackageWriter.FirstNewPackageId + (first + offset) % count);
                if (Catalog.ContainsInstalledPackage(candidate) || Options.ReservedPackageIds.Contains(candidate)
                    || generatedPackages.Contains(candidate)) continue;
                generatedPackage = candidate; generatedPackages.Add(candidate); generatedIndex = 0; break;
            }
            if (generatedPackage is null) throw new InvalidDataException("No unused package namespace is available for generated object data.");
        }
        var native = new NativeAssetMetadata(generatedPackage.Value, source.Reference, source.TypeInfo,
            name, generatedIndex++);
        var asset = new GeneratedObjectAsset(id, name, native, [], sourceTag);
        generated.Add(asset.TagHash, asset); generatedIds.Add(id, asset.TagHash);
        return asset.TagHash;
    }

    internal void CompleteGenerated(uint tag, byte[] bytes,
        IReadOnlyDictionary<int, PackageInstanceOrigin>? instances = null,
        IReadOnlyDictionary<int, PackagePlacementOrigin>? placements = null,
        IReadOnlyList<PackageInstanceReplacement>? replaces = null,
        IReadOnlyDictionary<int, PackageAddedInstance>? added = null)
    {
        generated[tag] = generated[tag] with
        {
            Data = bytes,
            InstanceOrigins = instances ?? new Dictionary<int, PackageInstanceOrigin>(),
            PlacementOrigins = placements ?? new Dictionary<int, PackagePlacementOrigin>(),
            Replaces = replaces ?? [],
            AddedInstances = added ?? new Dictionary<int, PackageAddedInstance>()
        };
    }
}
