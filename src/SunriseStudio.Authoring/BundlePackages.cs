using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Authoring;

/**
 * The new packages a bundle import fills, in order. When the current one has no room the import moves
 * to the next unused new package id, so one import is not bound to one package's 8192 entries.
 */
public sealed class BundlePackages
{
    /** Entries a bundle bubble needs free before it starts in a package, so its assets stay together. */
    public const int BubbleReserve = 1024;
    /** Entries one step past the statics needs free: a light, a sky, a nav mesh, shapes, entities. */
    public const int StepReserve = 256;

    private readonly StudioProject project;
    private readonly PackageCatalog catalog;
    private readonly List<ushort> used;

    /** @param first The package the import starts in; usually the activity package. */
    public BundlePackages(StudioProject project, PackageCatalog catalog, ushort first)
    {
        this.project = project;
        this.catalog = catalog;
        used = [first];
    }

    /** The package new entries go in now. */
    public ushort Current => used[^1];

    /** Every package the import has used, in order. */
    public IReadOnlyList<ushort> Used => used;

    // Entries prepared for a package but not yet added to the project.
    private readonly Dictionary<ushort, int> claimed = [];

    /** The first free entry of a package, after every project asset in it and every claimed entry. */
    public int NextEntry(ushort package) => Math.Max(claimed.GetValueOrDefault(package), project.NextEntry(package));

    /** Marks a package's entries up to `next` as taken by assets the caller adds to the project later. */
    public void Claim(ushort package, int next) => claimed[package] = Math.Max(claimed.GetValueOrDefault(package), next);

    /** @return A package with at least `entries` free: the current one, else the next unused one. */
    public ushort Room(int entries)
    {
        if (TagHashes.EntriesPerPackage - NextEntry(Current) < entries) Advance();
        return Current;
    }

    /** Materials the import's statics share, across bubbles and packages. */
    public MaterialShare Materials { get; } = new();

    /**
     * Runs a step that prepares assets for one package without changing the project. A step that finds
     * the current package full runs once more in the next package. Materials the step staged are kept
     * only when it succeeds.
     */
    public T Fill<T>(Func<ushort, T> step)
    {
        try
        {
            T result;
            try { result = step(Current); }
            catch (InvalidDataException error) when (error.Message.Contains("no room", StringComparison.Ordinal) && NextEntry(Current) > 0)
            {
                Materials.Discard();
                Advance();
                result = step(Current);
            }
            Materials.Commit();
            return result;
        }
        catch
        {
            Materials.Discard();
            throw;
        }
    }

    /**
     * Moves to the next new package id after the current one, wrapping round, that is not installed, not
     * used by the project and not its tag allocator's.
     */
    public void Advance()
    {
        var taken = project.AddedAssets.Select(asset => asset.Native?.PackageId).OfType<ushort>()
            .Concat(project.ReadTagAllocator()?.Packages ?? []).Concat(used).ToHashSet();
        int first = PackageWriter.FirstNewPackageId, span = PackageWriter.LastNewPackageId - first + 1;
        for (int step = 1; step <= span; step++)
        {
            ushort id = (ushort)(first + (Current - first + step) % span);
            if (taken.Contains(id) || catalog.ContainsInstalledPackage(id)) continue;
            used.Add(id);
            return;
        }
        throw new InvalidDataException($"No new package id from {PackageWriter.NewPackageIdRange} is free.");
    }
}
