using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Document;
using SunriseStudio.World;

/**
 * A bundle whose package is nearly full spills to a second new package, and the build resolves every
 * reference between the two packages and the world.
 */
static class SpillChecks
{
    private const string Destination = "spillbed";
    // Trostland, Widow's Walk and the Atrium in EDZ.
    private static readonly uint[] Kept = [0xB8459D59, 0xC0064AEF, 0xC0064AED];
    // An installed empty nav second tag: a small, non-root entry to fill the package with.
    private const uint Filler = 0x80C4E6E8;
    // Free entries left: enough for the sounds and music, too few for a bubble's reserve.
    private const int Left = 300;

    public static void Run(string gameRoot)
    {
        using var catalog = PackageCatalog.Open(gameRoot);
        var schema = StudioRegistry.Index;
        string scratch = Path.Combine(Path.GetTempPath(), "sunrise-spill-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = StudioProject.Create(Path.Combine(scratch, "project"), "Spill");
            WorldTemplate.Create(project.ContentDirectory, catalog, schema, WorldBuild.Named(catalog, "edz_freeroam:scenario_client"),
                Kept.ToHashSet(), Destination);
            ushort first = Enumerable.Range(0, 0x10000).Select(x => (ushort)x)
                .First(x => PackageWriter.IsNewPackageId(x) && !catalog.ContainsInstalledPackage(x));
            catalog.TryGetEntry(Filler, out var like);
            project.AddAssets([new("spill_filler", catalog.Read(Filler),
                new(first, like.Reference, like.TypeInfo, null, TagHashes.EntriesPerPackage - Left - 1), Filler)], catalog);
            Fill(project, catalog, first);

            string bundle = Path.GetDirectoryName(TestFixtures.TestbedBubble("bubble_a"))!;
            var placed = WorldBundleImport.PlaceBundle(project, catalog, schema, Destination, bundle, first);
            var byPackage = project.AddedAssets.Where(asset => asset.Native is not null).GroupBy(asset => asset.Native!.PackageId)
                .ToDictionary(group => group.Key, group => group.Count());
            WorldBuild.Check(placed.Packages.Count == 2 && placed.Packages[0] == first && byPackage.GetValueOrDefault(placed.Packages[1]) > 0
                && byPackage[first] > 1 && placed.Statics == 3,
                $"The bundle used packages {string.Join(',', placed.Packages.Select(id => id.ToString("X4")))}, not the full one and one more.");

            var build = project.Build(catalog, tagSchema: schema);
            string packages = Path.Combine(scratch, "built", "packages");
            Directory.CreateDirectory(packages);
            foreach (string file in build.PackageFiles) File.Copy(file, Path.Combine(packages, Path.GetFileName(file)));
            using var built = PackageCatalog.Open(Path.GetDirectoryName(packages)!);
            // New packages only; the build's patches of installed packages read through older patches it does not ship.
            var own = built.Entries.Where(entry => entry.PackageName.StartsWith("w64_sunrise_", StringComparison.Ordinal))
                .Select(entry => entry.PackageId).ToHashSet();
            int references = 0, crossing = 0;
            foreach (var entry in built.Entries.Where(entry => own.Contains(entry.PackageId)))
                foreach (var reference in TagDocument.Parse(built.Read(entry.TagHash), entry.Reference, schema).References.Where(x => !x.IsNull))
                {
                    uint? target = reference.Kind == TagReferenceKind.WideRef ? built.Resolve64(reference.Value) ?? catalog.Resolve64(reference.Value)
                        : (uint)reference.Value;
                    if (target is not uint tag || !own.Contains((ushort)TagHashes.Package(tag))) continue;
                    WorldBuild.Check(built.ContainsTag(tag), $"0x{entry.TagHash:X8} names 0x{tag:X8}, which no built package holds.");
                    references++;
                    if (TagHashes.Package(tag) != entry.PackageId) crossing++;
                }
            WorldBuild.Check(placed.Packages.All(own.Contains) && crossing > 0,
                $"The built packages do not hold both bundle packages, or nothing names across them ({crossing}).");
            Console.WriteLine($"world_spill packages={string.Join(',', placed.Packages.Select(id => id.ToString("X4")))} "
                + $"assets={string.Join(',', placed.Packages.Select(id => byPackage.GetValueOrDefault(id)))} references={references} crossing={crossing}");
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }

    /** A step that finds its package full runs again in the next one; any other failure is not retried. */
    private static void Fill(StudioProject project, PackageCatalog catalog, ushort first)
    {
        var packages = new BundlePackages(project, catalog, first);
        var tried = new List<ushort>();
        ushort got = packages.Fill(package =>
        {
            tried.Add(package);
            if (tried.Count == 1) throw new InvalidDataException("The package has no room for a mesh.");
            return package;
        });
        bool other = false;
        try { packages.Fill<int>(_ => throw new InvalidDataException("bad glb")); }
        catch (InvalidDataException) { other = true; }
        WorldBuild.Check(tried.Count == 2 && tried[0] == first && got != first && packages.Used.Count == 2 && other && packages.Used.Count == 2,
            "BundlePackages.Fill does not move on exactly when a package is full.");
    }
}
