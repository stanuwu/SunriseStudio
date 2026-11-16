using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using SunriseStudio.World;

/** A stock local-light row copied to a new place loads there as one light in the light and probe stages. */
static class LightChecks
{
    // Above the fixture block, in bundle space.
    private static readonly Vector3 BundlePosition = new(6, 4, 4);

    public static (string List, uint LightTable) Place(StudioProject project, PackageCatalog catalog, uint bubble)
    {
        ushort package = Enumerable.Range(0, 0x10000).Select(x => (ushort)x)
            .First(x => PackageWriter.IsNewPackageId(x) && !catalog.ContainsInstalledPackage(x)
                && project.AddedAssets.All(asset => asset.Native?.PackageId != x));
        var placed = WorldBundleImport.PlaceLight(project, catalog, "testbed", bubble, TestFixtures.TestbedBubble("bubble_a"), "block", BundlePosition, package);
        var assets = project.AddedAssets.Where(asset => asset.Native?.PackageId == package).ToList();
        byte[] table = project.ReadAddedAsset(assets.Single(asset => asset.Name.EndsWith(".lights", StringComparison.Ordinal)).Id, catalog);
        ref readonly var head = ref TagView.Checked<SLightCollection>(table, 0);
        WorldBuild.Check(assets.Count == LightAuthoring.EntryCount && TagView.Rows(table, in head.Lights, SLightRow.Class).Length == 1
            && head.ProbeRowCount == 1 && head.VolumetricRowCount == 0 && head.Bounds == placed.LightTable + 1,
            "The light table is not one probe row with its own bounds.");
        // The header box is the union of the bounds rows on the stock template too.
        uint template = assets.Single(asset => asset.Name.EndsWith(".lights", StringComparison.Ordinal)).BaseTagHash!.Value;
        byte[] stock = catalog.ReadOriginal(template);
        ref readonly var stockHead = ref TagView.Checked<SLightCollection>(stock, 0);
        byte[] stockBounds = catalog.ReadOriginal(stockHead.Bounds);
        var boxes = TagView.Rows(stockBounds, in TagView.Checked<SInstanceBoundsTable>(stockBounds, 0).Boxes, SInstanceBoundsRow.Class).ToArray();
        var low = boxes.Aggregate(new Vector4(float.PositiveInfinity), (a, b) => Vector4.Min(a, b.MinimumW1));
        var high = boxes.Aggregate(new Vector4(float.NegativeInfinity), (a, b) => Vector4.Max(a, b.MaximumW1));
        var headLow = TagView.At<Vector4>(stock, TagView.FieldOffset<SLightCollection>(nameof(SLightCollection.Unk10)));
        var headHigh = TagView.At<Vector4>(stock, TagView.FieldOffset<SLightCollection>(nameof(SLightCollection.Unk20)));
        WorldBuild.Check(Vector4.Distance(low, headLow) < 0.01f && Vector4.Distance(high, headHigh) < 0.01f,
            $"The stock table 0x{template:X8} header box {headLow}..{headHigh} is not the union {low}..{high} of its bounds rows.");
        Console.WriteLine($"world_light list={placed.List} table=0x{placed.LightTable:X8} template=0x{template:X8} header_box=union");
        return placed;
    }

    /** The loaded map holds the new table with its one row in the light and probe stages. */
    public static void Loaded(SunriseStudio.Scene.WorldDocument loaded, uint lightTable)
    {
        var tables = loaded.MapScene.LightTables.Where(x => x.Table.TagHash == lightTable).ToList();
        WorldBuild.Check(tables.Count > 0 && tables.All(x => x.Table.Rows is [{ AuthoredStages: [4, 5] }]),
            "The loaded map does not hold the new light as one row in stages 4 and 5.");
        Console.WriteLine($"world_light_loaded tables={tables.Count} at={tables[0].Table.Rows[0].Translation}");
    }
}
