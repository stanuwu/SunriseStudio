using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.World;

/** The bake record of a placed bubble: unchanged inputs skip, a forced bake replaces the collision. */
static class BakeChecks
{
    public static void Run(StudioProject project, PackageCatalog catalog, uint owner)
    {
        var records = WorldBundleImport.BakeRecords(project);
        WorldBuild.Check(records.Count == 1 && records[0].BubbleName == "bubble_a" && records[0].CollisionList is not null && records[0].Tomes.Count > 0,
            $"The placement left {records.Count} bake records, not one for bubble_a with its list and tomes.");
        var skipped = WorldBundleImport.Rebake(project, catalog);
        WorldBuild.Check(skipped.Count == 1 && !skipped[0].Baked, "A bake with unchanged inputs ran again.");
        var forced = WorldBundleImport.Rebake(project, catalog, force: true);
        var world = WorldFiles.Load(project.ContentDirectory, "testbed");
        var payload = ((System.Text.Json.Nodes.JsonArray)world.Fields(records[0].CollisionList!)["placements"]!)[0]!["payload"]!;
        uint newOwner = Convert.ToUInt32(((string)payload["collision_owner"]!).TrimStart('#'), 16);
        int owners = project.AddedAssets.Count(asset => asset.Name == "testbed_bubble_a_collision.owner");
        var tomes = WorldBundleImport.BakeRecords(project)[0].Tomes;
        // The old entity's entries are freed first, so the new one takes the same tags.
        WorldBuild.Check(forced.Count == 1 && forced[0].Baked && forced[0].CollisionShapes == 4 && newOwner == owner && owners == 1
            && tomes.Count == records[0].Tomes.Count && forced[0].Notes.Count == 0 && tomes.All(project.ContainsEdit) && tomes.All(tag => TomeReader.IsTome(project.ReadAsset(tag, catalog))),
            $"The forced bake did not replace the collision (owner 0x{newOwner:X8}, {owners} owners) or lost its tome edits.");
        // The bubble asks for a nav mesh: its three tags follow the collision, once, also after the rebake.
        var navAssets = project.AddedAssets.Where(asset => asset.Name.StartsWith("testbed_bubble_a_nav.", StringComparison.Ordinal)).ToArray();
        var navMesh = SunriseStudio.Scene.Navigation.NavMeshReader.Read(project.ReadAddedAsset(navAssets[0].Id, catalog));
        WorldBuild.Check(navAssets.Length == 3 && navMesh.Layers[0].Polygons.Length > 0
            && world.Files.Count(file => file.Name.EndsWith("/testbed_bubble_a_nav.map_placements", StringComparison.Ordinal)) == 1,
            $"The bake left {navAssets.Length} nav assets or not one nav list.");
        Console.WriteLine($"world_bake_nav assets={navAssets.Length} polygons={string.Join(",", navMesh.Layers.Select(layer => layer.Polygons.Length))}");
        Console.WriteLine($"world_bake records=1 unchanged=skipped forced_owner=0x{newOwner:X8} shapes={forced[0].CollisionShapes} tomes={tomes.Count} "
            + $"first_tomes={records[0].Tomes.Count} notes={string.Join(" | ", forced[0].Notes)}");
    }
}
