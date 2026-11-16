namespace SunriseStudio.Packages;

public sealed record PackageInstanceOrigin(uint SourceTag, int SourceIndex);
public sealed record PackagePlacementOrigin(uint SourceTag, int SourceOffset, int SourceIndex);
public sealed record PackageInstanceReplacement(uint SourceTag, int SourceIndex, uint PlacementSourceTag, int PlacementSourceOffset);
/**
 * The added object a generated row draws. Model 0 is a copy of the installed row at SourceOffset of
 * table SourceTag; any other Model is placed in container SourceTag. Identity numbers it.
 */
public sealed record PackageAddedInstance(uint SourceTag, int SourceOffset, int SourceIndex, ulong Identity, uint Model);

/** Overlay metadata describes project bytes; installed entries remain the baseline. */
public sealed record PackageOverlayEntry(PackageEntry Entry, string? Name = null)
{
    public WideHashAliases WideHashes { get; init; } = WideHashAliases.Empty;
    public bool Generated { get; init; }
    public uint? GeneratedSourceTag { get; init; }
    public IReadOnlyDictionary<int, PackageInstanceOrigin> InstanceOrigins { get; init; } = new Dictionary<int, PackageInstanceOrigin>();
    public IReadOnlyDictionary<int, PackagePlacementOrigin> PlacementOrigins { get; init; } = new Dictionary<int, PackagePlacementOrigin>();
    public IReadOnlyList<PackageInstanceReplacement> Replaces { get; init; } = [];
    public IReadOnlyDictionary<int, PackageAddedInstance> AddedInstances { get; init; } = new Dictionary<int, PackageAddedInstance>();
}
