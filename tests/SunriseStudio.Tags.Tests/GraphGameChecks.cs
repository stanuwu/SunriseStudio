using System.Diagnostics;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Graph;
using SunriseStudio.Tags.Query;

/** The content graph over the installation: build cost, reference resolution, agreement with PackageNativeDependencies, real queries. */
static class GraphGameChecks
{
    /** Classes PackageNativeDependencies reads, so its links can be compared with the graph's. */
    private static readonly uint[] NativeClasses = [0x808071E8, 0x808073A5, 0x80806E28, 0x80806E2E, 0x80809C36, 0x80809C0F, 0x80809802];

    public static void Run(string gameRoot, string registryPath, int sample)
    {
        var schema = new RegistryIndex(TagRegistry.Load(registryPath));
        using var catalog = PackageCatalog.Open(gameRoot);
        var source = new CatalogTagSource(catalog);
        var clock = Stopwatch.StartNew();
        var graph = ContentGraph.Build(source, schema);
        Console.WriteLine($"graph edges={graph.EdgeCount} tags={graph.Counts.Tags} null_references={graph.Counts.NullReferences} "
            + $"unresolved_references={graph.Counts.UnresolvedReferences} seconds={clock.Elapsed.TotalSeconds:F0} "
            + string.Join(" ", graph.Edges.GroupBy(x => x.Kind).OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Count()}")));

        // Every link PackageNativeDependencies reports should be a direct edge or reachable through the graph.
        foreach (uint classId in NativeClasses)
        {
            int tags = 0, links = 0, direct = 0, reachable = 0;
            foreach (var entry in catalog.Entries.Where(x => x.Reference == classId).Take(sample))
            {
                IReadOnlyList<uint> native;
                try { native = PackageNativeDependencies.Read(catalog, entry.TagHash, entry.Reference, entry.TypeInfo, catalog.Read(entry.TagHash)); }
                catch (InvalidDataException) { continue; }
                tags++;
                var targets = graph.ReferencesOf(entry.TagHash).ToArray().Select(x => x.To).ToHashSet();
                var closure = graph.DependencyClosure([entry.TagHash]);
                links += native.Count;
                direct += native.Count(targets.Contains);
                reachable += native.Count(closure.Contains);
            }
            Console.WriteLine($"  native class=0x{classId:X8} tags={tags} links={links} direct={direct} reachable={reachable}");
        }

        using (var stream = new MemoryStream())
        {
            graph.Save(stream);
            stream.Position = 0;
            var loaded = ContentGraph.Load(stream);
            if (!loaded.Edges.SequenceEqual(graph.Edges)) throw new InvalidOperationException("The installed graph does not survive save and load.");
            Console.WriteLine($"  graph saved_bytes={stream.Length}");
        }

        var context = new QueryContext(source, schema);
        var scenarios = PathQuery.Parse("@<s_scenario>").Evaluate(context);
        uint first = scenarios[0].Tag;
        var bubbles = PathQuery.Parse($"@0x{first:X8}.bubbles[*]").Evaluate(context);
        var names = PathQuery.Parse($"@0x{first:X8}.bubbles[*].name_hash").Evaluate(context);
        Console.WriteLine($"  query scenarios={scenarios.Count} first=0x{first:X8} bubbles={bubbles.Count} names={names.Count} "
            + $"used_by={graph.UsedBy(first).Length} closure={graph.DependencyClosure([first]).Count}");
    }
}
