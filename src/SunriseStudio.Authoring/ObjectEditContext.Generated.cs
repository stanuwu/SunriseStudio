// The .Generated.cs name marks this file as generated code, which turns nullable checks off.
#nullable enable
using SunriseStudio.Packages;
using SunriseStudio.Tags;
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

    /** Stable recipe order and the mod id choose unused package ids; a project from before the mod probe keeps its old seed. */
    internal uint ReserveGenerated(string id, string name, uint sourceTag)
    {
        if (generatedIds.TryGetValue(id, out uint existing)) return existing;
        // A project entry can be the template too, such as a placement list a project added.
        (uint Reference, uint TypeInfo) source = Catalog.TryGetEntry(sourceTag, out var installed)
            ? (installed.Reference, installed.TypeInfo)
            : Options.AddedEntries.FirstOrDefault(entry => entry.Tag == sourceTag) is { } added
                ? (added.Class, added.TypeInfo)
                : throw new InvalidDataException("A generated asset template is missing.");
        if (generatedPackage is null || generatedIndex == 8192)
        {
            string seed = Options.ModProbe ? TagAllocator.ModSeed + Options.Identity : "Sunrise object edits\n" + Options.Identity;
            generatedPackage = null;
            foreach (ushort candidate in TagAllocator.Probe(seed))
            {
                if (Catalog.ContainsInstalledPackage(candidate) || Options.ReservedPackageIds.Contains(candidate)
                    || Options.TagPackages.Contains(candidate) || generatedPackages.Contains(candidate)) continue;
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
        IReadOnlyList<PackageInstanceReplacement>? replaces = null)
    {
        generated[tag] = generated[tag] with
        {
            Data = bytes,
            InstanceOrigins = instances ?? new Dictionary<int, PackageInstanceOrigin>(),
            PlacementOrigins = placements ?? new Dictionary<int, PackagePlacementOrigin>(),
            Replaces = replaces ?? []
        };
    }
}
