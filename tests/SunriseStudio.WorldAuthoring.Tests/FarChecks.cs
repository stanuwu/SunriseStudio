using System.Numerics;
using System.Text.Json.Nodes;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Tft;
using SunriseStudio.Tags.Views;
using SunriseStudio.World;

/** A far tile stands in for one bubble: every other bubble that does not stream its cells loads it, and it alone. */
static class FarChecks
{
    // Bubble B of the fixture bundle, and the far grid's reach past it on +X, so the clip has work to do.
    private static readonly Vector3 CoveredMin = new(16, -16, -8), CoveredMax = new(48, 16, 24);
    private const float Margin = 2, Sink = 0.5f;

    /** Places a far grid for `covered` in a new far container. */
    public static IReadOnlyList<PlacedBundleStatic> Place(StudioProject project, PackageCatalog catalog, RegistryIndex schema,
        uint arrival, uint covered, IReadOnlySet<uint> candidates, string scratch)
    {
        string glb = Path.Combine(scratch, "far_bubble_b.glb");
        GltfInterchange.Write(glb, [Grid()]);
        ushort package = Enumerable.Range(0, 0x10000).Select(x => (ushort)x)
            .First(x => PackageWriter.IsNewPackageId(x) && !catalog.ContainsInstalledPackage(x)
                && project.AddedAssets.All(asset => asset.Native?.PackageId != x));
        var placed = WorldBundleImport.PlaceFar(project, catalog, schema, "testbed", arrival, TestFixtures.TestbedBubble("bubble_a"),
            "bubble_b", glb, new(CoveredMin, CoveredMax, Margin, Sink), [covered], candidates, package);
        var world = WorldFiles.Load(project.ContentDirectory, "testbed");
        string container = placed[0].Container;
        var loaders = WorldFarLayer.Loaders(world, [covered], candidates);
        var mask = Convert.FromHexString(WorldFiles.Text(world.Fields(container)["bubble_mask"])!);
        WorldBuild.Check(placed.Count >= 1 && placed.All(x => x.Container == container) && loaders.Contains(arrival) && !loaders.Contains(covered),
            "The far layer has no models, more than one container, or the wrong loaders.");
        foreach (var bubble in WorldFarLayer.Bubbles(world).Where(b => candidates.Contains(b.NameHash)))
        {
            bool meets = Enumerable.Range(0, 256).Any(cell => WorldFarLayer.Has(mask, cell) && WorldFarLayer.Has(bubble.CellMask, cell));
            bool listed = WorldContent.Containers(world, bubble.NameHash).Contains(container);
            WorldBuild.Check(meets == loaders.Contains(bubble.NameHash) && listed == loaders.Contains(bubble.NameHash),
                $"Bubble 0x{bubble.NameHash:X8} {(meets ? "meets" : "misses")} the far mask and {(listed ? "lists" : "omits")} the far container.");
        }
        var members = ((JsonArray)world.Fields(container)["contents"]!).Select(row => WorldFiles.Text(row!["member_tag"])).ToList();
        WorldBuild.Check(members.Count == placed.Count && placed.All(x => members.Contains(x.Files[^1])), "The far container does not hold every far list.");
        // The clip keeps the covered area plus the margin, lowered by the sink.
        foreach (var model in placed)
        {
            byte[] data = project.ReadAddedAsset(project.AddedAssets.Single(asset => asset.Name == model.Name + ".model").Id, catalog);
            ref readonly var header = ref TagView.Checked<SStaticMesh>(data, 0);
            Vector3 low = header.PositionOffset - new Vector3(header.PositionScale), high = header.PositionOffset + new Vector3(header.PositionScale);
            // X is the grid's longest half extent, so the scale bounds it exactly; a kept cell reaches 4 m past the margin.
            WorldBuild.Check(low.X >= CoveredMin.X - Margin - 4 && high.X <= CoveredMax.X + Margin + 4
                && MathF.Abs(header.PositionOffset.Z + Sink) < 0.01f, $"The far model {model.Name} was not clipped and sunk.");
        }
        Console.WriteLine($"world_far container={container} models={placed.Count} loaders={loaders.Count} covered=0x{covered:X8} mask_cells="
            + Enumerable.Range(0, 256).Count(cell => WorldFarLayer.Has(mask, cell)));
        return placed;
    }

    /** The loaded map draws the far models from the far container, whose cells are none of the covered bubble's. */
    public static void Loaded(SunriseStudio.Scene.WorldDocument loaded, IReadOnlyList<TftCompiledTag> tags, IReadOnlyList<PlacedBundleStatic> far,
        WorldFiles world, uint covered)
    {
        uint containerTag = tags.Single(x => x.Name == far[0].Container).TagHash;
        var container = loaded.MapScene.MapContainers.SingleOrDefault(x => x.TagHash == containerTag)
            ?? throw new InvalidDataException("The loaded map does not reach the far container.");
        var coveredCells = WorldFarLayer.Bubbles(world).Single(b => b.NameHash == covered).CellMask;
        var tables = far.Select(x => tags.Single(t => t.Name == x.Files[1]).TagHash).ToHashSet();
        // A shared container draws once under each bubble that lists it.
        var loaders = WorldFarLayer.Loaders(world, [covered], loaded.MapScene.Bubbles.Select(b => b.NameHash).ToHashSet())
            .Select(hash => loaded.MapScene.Bubbles.Single(b => b.NameHash == hash).TagHash).ToHashSet();
        var drawnBy = loaded.MapScene.Instances.Where(x => tables.Contains(x.InstanceSourceTag) && x.Mesh.Native?.RenderStage == 0)
            .GroupBy(x => x.BubbleHash).ToDictionary(g => g.Key, g => g.Count());
        int drawn = drawnBy.Values.Sum();
        WorldBuild.Check(drawnBy.Keys.ToHashSet().SetEquals(loaders) && drawnBy.Values.All(count => count == far.Count)
            && container.MapGlobalIndices.Count > 0 && container.MapGlobalIndices.All(cell => !WorldFarLayer.Has(coveredCells, cell)),
            $"The far layer draws in {drawnBy.Count} bubbles, not its {loaders.Count} loaders, or its cells reach the covered bubble.");
        Console.WriteLine($"world_far_loaded models={drawn} cells={string.Join(',', container.MapGlobalIndices)}");
    }

    /** A 64 by 32 m grid of 4 m cells over bubble B and past it on +X. */
    private static InterchangeMesh Grid()
    {
        const int columns = 17, rows = 9;
        var positions = new List<Vector3>();
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < columns; x++) positions.Add(new(x * 4, -16 + y * 4, 0));
        var indices = new List<uint>();
        for (int y = 0; y < rows - 1; y++)
            for (int x = 0; x < columns - 1; x++)
            {
                uint a = (uint)(y * columns + x), b = a + 1, c = a + columns, d = c + 1;
                indices.AddRange([a, b, d, a, d, c]);
            }
        return new("far", positions.ToArray(), positions.Select(_ => Vector3.UnitZ).ToArray(),
            positions.Select(p => new Vector2(p.X / 8, p.Y / 8)).ToArray(), indices.ToArray());
    }
}
