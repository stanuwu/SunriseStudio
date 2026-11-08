namespace SunriseStudio.Packages;

public sealed partial class PackageCatalog
{
    /** Returns one metadata snapshot; arbitrary read overrides may change tags absent from it. */
    public IReadOnlyList<PackageOverlayEntry> GetOverlayEntries() =>
        Volatile.Read(ref overlayState).Entries.Values.ToArray();

    /** Installed entries, then the entries a project adds; an overlay edit of an installed entry is not repeated. */
    public IEnumerable<PackageEntry> EntriesWithAdded() =>
        Entries.Concat(Volatile.Read(ref overlayState).Entries.Values.Select(x => x.Entry).Where(x => !byTag.ContainsKey(x.TagHash)).OrderBy(x => x.TagHash));

    /** File identity includes change time, so restoring the write timestamp does not retain an index. */
    public string GetPackageFileIdentity(CancellationToken token = default) =>
        CacheFileIdentity.Compute(Directory.EnumerateFiles(PackageDirectory, "*.pkg"), token);
}
