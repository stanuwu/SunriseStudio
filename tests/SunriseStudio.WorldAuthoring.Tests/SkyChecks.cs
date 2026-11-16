using System.Numerics;
using System.Runtime.InteropServices;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Tft;
using SunriseStudio.Tags.Views;
using SunriseStudio.World;

/** A sky ring cloned from the stock one-row sky, retextured, placed in a bubble, loads as a sky part. */
static class SkyChecks
{
    public static (string List, uint SkyObjects) Place(StudioProject project, PackageCatalog catalog, uint bubble)
    {
        ushort package = Enumerable.Range(0, 0x10000).Select(x => (ushort)x)
            .First(x => PackageWriter.IsNewPackageId(x) && !catalog.ContainsInstalledPackage(x)
                && project.AddedAssets.All(asset => asset.Native?.PackageId != x));
        var placed = WorldBundleImport.PlaceSky(project, catalog, "testbed", bubble, Gradient(), package);
        var world = WorldFiles.Load(project.ContentDirectory, "testbed");
        var assets = project.AddedAssets.Where(asset => asset.Native?.PackageId == package).ToList();
        byte[] Read(string suffix) => project.ReadAddedAsset(assets.Single(asset => asset.Name == "testbed_sky" + suffix).Id, catalog);
        byte[] sky = Read(".sky"), wrapper = Read(".wrapper"), model = Read(".model"), technique = Read(".material");
        uint Tag(int index) => TagHashes.Make(package, index);
        var row = TagView.Rows(sky, in TagView.Checked<SSkyObjects>(sky, 0).Placements, UnknownSkyEntry.Class)[0];
        var parts = TagView.Rows(model, in TagView.Checked<SEntityModel>(model, 0).Meshes, SEntityModelMesh.Class).ToArray()
            .SelectMany((_, i) => TagView.Rows(model, in TagView.Checked<SEntityModel>(model, 0).Meshes, SEntityModelMesh.Class)[i].Parts.Count > 0
                ? TagView.Rows(model, in TagView.Rows(model, in TagView.Checked<SEntityModel>(model, 0).Meshes, SEntityModelMesh.Class)[i].Parts,
                    SEntityModelPart.Class).ToArray() : []).ToArray();
        var textures = TagView.Rows(technique, in TagView.Checked<STechnique>(technique, 0).PixelShader.State.StaticTextures.Textures,
            SMaterialTextureAssignment.Class);
        var constants = MemoryMarshal.Cast<byte, Vector4>(Read(".constants.bin"));
        WorldBuild.Check(assets.Count == SkyAuthoring.EntryCount && placed.SkyObjects == Tag(0) && row.ModelWrapper == Tag(1)
            && TagView.Member<SSkyModelWrapper, uint>(wrapper, 0, nameof(SSkyModelWrapper.Model)) == Tag(2)
            && row.LightShaftPass == 0 && row.LightShaftPassAlt == 0 && row.HiddenInTransparents == 0
            && parts.Any(part => part.Material == Tag(3)) && parts.All(part => part.Material != 0x80C44C62) && textures[0].Texture == Tag(6) && constants[2].X == 1 && constants[1].X < 122
            && constants[0] == new Vector4(0.25f, 1, 0, 0),
            "The sky ring does not chain sky objects, wrapper, model, technique and texture, or it still draws only as a light-shaft blocker.");
        var list = world.Fields(placed.List);
        var payload = (System.Text.Json.Nodes.JsonObject)((System.Text.Json.Nodes.JsonArray)list["placements"]!)[0]!["payload"]!;
        WorldBuild.Check(WorldFiles.Text(payload["sky_objects"]) == "#" + WorldFiles.HashText(placed.SkyObjects)
            && WorldContent.Containers(world, bubble).Any(container =>
                ((System.Text.Json.Nodes.JsonArray)world.Fields(container)["contents"]!).Any(member => WorldFiles.Text(member!["member_tag"]) == placed.List)),
            "The sky list does not name the new sky or join the bubble.");
        Console.WriteLine($"world_sky list={placed.List} sky=0x{placed.SkyObjects:X8} parts={parts.Length} texture=0x{textures[0].Texture:X8}");
        return placed;
    }

    /** The loaded map draws the dome as a sky part with the new technique. */
    public static void Loaded(SunriseStudio.Scene.WorldDocument loaded, uint skyObjects)
    {
        uint technique = skyObjects + 3;
        var drawn = loaded.MapScene.Instances.Where(x => x.Layer == SunriseStudio.Scene.SceneLayer.Skybox && x.Mesh.MaterialTag == technique).ToList();
        WorldBuild.Check(drawn.Count > 0, "The loaded map does not draw the sky ring.");
        Console.WriteLine($"world_sky_loaded parts={drawn.Count} technique=0x{technique:X8}");
    }

    /** 256 by 96: deep blue at the top to pale orange at the horizon. */
    private static RgbaImage Gradient()
    {
        const int width = 256, height = 96;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            float t = y / (height - 1f);
            byte r = (byte)(20 + t * 220), g = (byte)(40 + t * 140), b = (byte)(140 - t * 60);
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                pixels[i] = r; pixels[i + 1] = g; pixels[i + 2] = b; pixels[i + 3] = 255;
            }
        }
        return new(width, height, pixels);
    }
}
