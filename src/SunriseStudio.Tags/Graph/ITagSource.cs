using SunriseStudio.Packages;

namespace SunriseStudio.Tags.Graph;

/** What the content graph reads: package entries, their bytes, and the tag and wide hash tables. */
public interface ITagSource
{
    IEnumerable<PackageEntry> Entries { get; }
    byte[] Read(uint tag);
    bool Contains(uint tag);
    /** @return The tag a wide hash names, or null. */
    uint? ResolveWide(ulong hash);
}

/** The installed packages, plus any overlay the catalog carries. */
public sealed class CatalogTagSource(PackageCatalog catalog) : ITagSource
{
    public IEnumerable<PackageEntry> Entries => catalog.Entries;
    public byte[] Read(uint tag) => catalog.Read(tag);
    public bool Contains(uint tag) => catalog.ContainsTag(tag);
    public uint? ResolveWide(ulong hash) => catalog.Resolve64(hash);
}
