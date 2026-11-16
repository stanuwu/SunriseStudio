using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene.Collision;

/** The bundle's closed meshes join the bubble's tomes as occluders; open meshes do not. */
static class OcclusionChecks
{
    // A mesh the tome treats as an occluder blocks most axis lines through it.
    private const double Occluding = 0.5;

    public static void Placed(StudioProject project, PackageCatalog catalog, PlacedBundleStatic plate)
    {
        var glb = Path.Combine(TestFixtures.TestbedBubble("bubble_a"), "static.glb");
        var world = new QsTransform(plate.Position, plate.Rotation, Vector3.One).ToMatrix();
        var closed = OcclusionBake.ClosedMeshes(glb, world);
        WorldBuild.Check(closed.Count == 1 && closed[0].Count == 36, $"static.glb gives {closed.Count} closed meshes; the block is its only one.");
        var tomes = project.Changes.Select(change => change.TagHash).Where(tag => TomeReader.IsTome(catalog.Read(tag))).ToList();
        WorldBuild.Check(tomes.Count > 0, "The placement edited no tome of the bubble.");
        var lines = new List<string>();
        foreach (uint tag in tomes)
        {
            double before = TomeEditor.BlockedShare(TomeReader.Read(catalog.Read(tag)), closed[0], []);
            double after = TomeEditor.BlockedShare(TomeReader.Read(project.ReadAsset(tag, catalog)), closed[0], []);
            WorldBuild.Check(after >= Occluding, $"Tome {tag:X8} blocks {after:P0} of the lines through the block.");
            lines.Add($"{tag:X8}:{before:F2}->{after:F2}");
        }
        Console.WriteLine($"world_occlusion tomes={tomes.Count} blocked_share={string.Join(',', lines)}");
    }
}
