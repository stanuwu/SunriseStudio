namespace SunriseStudio.Packages;

/** One row of a package's entry table; TagHash is TagHashes.Make(PackageId, EntryIndex). */
public sealed record PackageEntry(
    uint TagHash,
    uint Reference,
    int Type,
    int SubType,
    int Size,
    string PackageName,
    ushort PackageId,
    int EntryIndex)
{
    public uint TypeInfo { get; init; }
    internal int StartBlock { get; init; }
    internal int StartOffset { get; init; }
    public override string ToString() => $"0x{TagHash:X8} {PackageName} [{Type}/{SubType}]";
}

/** A historical row is class-compatible; its source remains part of the naming evidence. */
public sealed record NamedTag(uint TagHash, uint ClassHash, string Name)
{
    public string SourcePackage { get; init; } = "";
    public ushort SourcePackageId { get; init; }
    public ushort SourcePatchId { get; init; }
    public uint SourceBuild { get; init; }
    public ulong SourceGroup { get; init; }
    public bool IsCurrentSource { get; init; }
}
