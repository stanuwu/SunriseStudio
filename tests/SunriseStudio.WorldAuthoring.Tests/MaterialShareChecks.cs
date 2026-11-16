using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.World;

/** A static imported twice with one share adds its materials once; a discarded stage is forgotten. */
static class MaterialShareChecks
{
    public static void Run(string gameRoot)
    {
        using var catalog = PackageCatalog.Open(gameRoot);
        string folder = TestFixtures.TestbedBubble("bubble_a");
        var bubble = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "bubble.json")))!;
        string glb = Path.Combine(folder, (string)bubble["statics"]![0]!["glb"]!);
        var input = new StaticLodInput([glb], []);
        ushort package = Enumerable.Range(0, 0x10000).Select(x => (ushort)x)
            .First(x => PackageWriter.IsNewPackageId(x) && !catalog.ContainsInstalledPackage(x));
        static int Materials(PreparedBatch batch) => batch.Assets.Count(asset => asset.Name.EndsWith(".material", StringComparison.Ordinal));

        var share = new MaterialShare();
        var first = StaticBatchImporter.Prepare(catalog, input, "share_a", package, 0, share: share);
        share.Commit();
        var second = StaticBatchImporter.Prepare(catalog, input, "share_b", package, first.NextEntry, share: share);
        WorldBuild.Check(Materials(first) > 0 && Materials(second) == 0 && second.Assets.Count < first.Assets.Count && share.Reused == Materials(first),
            $"The second import of one static added {Materials(second)} materials; the first added {Materials(first)}.");

        var staged = new MaterialShare();
        var dropped = StaticBatchImporter.Prepare(catalog, input, "share_c", package, 0, share: staged);
        staged.Discard();
        var again = StaticBatchImporter.Prepare(catalog, input, "share_d", package, 0, share: staged);
        WorldBuild.Check(Materials(again) == Materials(dropped), "A discarded material was reused.");
        Console.WriteLine($"material_share materials={Materials(first)} first_assets={first.Assets.Count} second_assets={second.Assets.Count}");
    }
}
