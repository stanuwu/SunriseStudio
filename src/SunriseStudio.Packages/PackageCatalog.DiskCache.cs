using System.Security.Cryptography;
using System.Text;

namespace SunriseStudio.Packages;

/** Disk cache half of PackageCatalog: identity of the opened packages and the cache that follows it. */
public sealed partial class PackageCatalog
{
    private string? openedPackageIdentity;
    private long overlayRevision;
    public StudioDiskCache DiskCache { get; private set; } = null!;
    public bool HasOverlay { get { lock (readLock) return readOverride is not null || overlayState.Entries.Count != 0; } }
    public long OverlayRevision { get { lock (readLock) return overlayRevision; } }

    private void InitializeDiskCache(string[] files, StudioCacheOptions options)
    {
        DiskCache = new(options, GameRoot);
        try { openedPackageIdentity = CacheFileIdentity.Compute(files, default); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /** A metadata walk cannot prove ownership after its installed package snapshot changes. */
    public void RequireCurrentOriginalPackages(CancellationToken token = default)
    {
        if (openedPackageIdentity is null)
            throw new IOException("Installed package identity is unavailable. Reopen the Sunrise installation.");
        if (CacheFileIdentity.Compute(Directory.GetFiles(PackageDirectory, "*.pkg"), token) != openedPackageIdentity)
            throw new IOException("Installed packages changed. Reopen the Sunrise installation.");
    }

    /** Overlay bytes and changed open package tables never share an installed-data snapshot. */
    public string? CreateCacheIdentity(string schema, IEnumerable<string>? additionalFiles = null,
        CancellationToken token = default, bool codeVersioned = true)
        => HasOverlay ? null : CreateOriginalCacheIdentity(schema, additionalFiles, token, codeVersioned);

    /**
     * This identity is only for data read through ReadOriginal; overlays never enter its cache. With @param codeVersioned
     * false the reader assembly's build id is left out: the caller's @param schema must then carry an explicit version that
     * is bumped whenever the cached data's meaning changes (a rebuilt Solforge with unchanged rules keeps the snapshot).
     */
    public string? CreateOriginalCacheIdentity(string schema, IEnumerable<string>? additionalFiles = null,
        CancellationToken token = default, bool codeVersioned = true)
    {
        if (!DiskCache.Enabled || openedPackageIdentity is null) return null;
        try
        {
            string[] current = Directory.GetFiles(PackageDirectory, "*.pkg");
            if (CacheFileIdentity.Compute(current, token) != openedPackageIdentity) return null;
            string keys = PackageKeys.ResolveSource(GameRoot, keySourceExecutable);
            var sources = new[] { keys }.Append(Path.Combine(GameRoot, "bin", "x64", "oo2core_3_win64.dll"))
                .Concat(additionalFiles ?? []);
            string dependencies = CacheFileIdentity.Compute(sources, token);
            string code = codeVersioned ? typeof(PackageCatalog).Module.ModuleVersionId.ToString() : "explicit";
            string identity = $"{schema}\n{code}\n"
                + $"{GameRoot.ToUpperInvariant()}\n{openedPackageIdentity}\n{dependencies}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }
}
