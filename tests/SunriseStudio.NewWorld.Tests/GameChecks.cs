using SunriseStudio.Authoring;
using SunriseStudio.Formats.NewWorld;
using SunriseStudio.Packages;
using static Expect;

/** With an installation: the tags a new map names have the checked classes and layouts, and a compile succeeds. */
internal static class GameChecks
{
    internal static void Run(string game, string? keys)
    {
        using var catalog = PackageCatalog.Open(game, keys);
        var installed = NewWorldCompiler.ReadInstalled(catalog);
        ushort package = Enumerable.Range(PackageWriter.FirstNewPackageId, PackageWriter.LastNewPackageId - PackageWriter.FirstNewPackageId + 1)
            .Select(id => (ushort)id).Reverse().First(id => !catalog.ContainsInstalledPackage(id));
        var build = NewWorldCompiler.Compile(catalog, CompileChecks.Two, package, 0);
        Console.WriteLine($"PASS new-world-game package={package:X4} entries={build.Allocation.Entries.Count} "
            + $"closures={installed.CollisionArchetypeClosure.Count},{installed.SurfaceClosure.Count},"
            + $"{installed.NavigationEntityClosure.Count},{installed.NavigationRootClosure.Count}");
    }

    /** The installed inputs read by tag equal the inputs World Studio pins; closures may only add the start tag. */
    internal static void Compare(string game, string? keys, NewWorldInstalled pinned)
    {
        using var catalog = PackageCatalog.Open(game, keys);
        var read = NewWorldCompiler.ReadInstalled(catalog);
        Check(read.Static.Archetype.AsSpan().SequenceEqual(pinned.Static.Archetype) && read.Static.Resource.AsSpan().SequenceEqual(pinned.Static.Resource)
            && read.Static.Header.AsSpan().SequenceEqual(pinned.Static.Header)
            && read.Static.Configs.Zip(pinned.Static.Configs).All(pair => pair.First.AsSpan().SequenceEqual(pair.Second)),
            "The installed static profile differs from the pinned one.");
        Check(read.Players.Timer.AsSpan().SequenceEqual(pinned.Players.Timer) && read.Players.Lifetime.AsSpan().SequenceEqual(pinned.Players.Lifetime)
            && read.Players.Players.AsSpan().SequenceEqual(pinned.Players.Players), "The installed player services differ from the pinned ones.");
        foreach (var (name, scanned, frozen, start) in new[]
        {
            ("collision archetype", read.CollisionArchetypeClosure, pinned.CollisionArchetypeClosure, NewWorldComposer.CollisionArchetype),
            ("surface", read.SurfaceClosure, pinned.SurfaceClosure, NewWorldComposer.Surface),
            ("navigation entity", read.NavigationEntityClosure, pinned.NavigationEntityClosure, MapComponentWriter.NavigationEntity),
            ("navigation root", read.NavigationRootClosure, pinned.NavigationRootClosure, MapComponentWriter.PlaceholderNavigationRoot)
        })
            Check(scanned.Where(tag => tag != start).SequenceEqual(frozen.Where(tag => tag != start).Order()),
                $"The scanned {name} closure differs from the pinned one.");
        // So a compile that reads the installation gives the oracle's bytes.
        var installed = NewWorldCompiler.Compile(catalog, CompileChecks.Two, CompileChecks.Package, 3);
        var oracle = NewWorldCompiler.Compile(CompileChecks.Two, pinned, CompileChecks.Package, 3);
        Check(installed.Allocation.Entries.Zip(oracle.Allocation.Entries).All(pair => pair.First.Key == pair.Second.Key
            && pair.First.Data.AsSpan().SequenceEqual(pair.Second.Data)) && installed.Allocation.Entries.Count == oracle.Allocation.Entries.Count,
            "A compile from the installation differs from the compile from the pinned inputs.");
        Console.WriteLine($"PASS new-world-installed static=pinned players=pinned closures=pinned installed-compile=oracle-bytes "
            + $"entries={installed.Allocation.Entries.Count}");
    }
}
