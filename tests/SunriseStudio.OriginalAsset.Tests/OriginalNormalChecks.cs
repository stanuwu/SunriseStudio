using System.Numerics;
using System.Text.Json.Nodes;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

internal static class OriginalNormalChecks
{
    /** glTF scales XY before normalizing XYZ; the native map stores normalized XY with reversed Y. */
    public static void Run(PackageCatalog catalog, string root, ushort package)
    {
        string path = Path.Combine(root, "normal-scale.gltf");
        GltfInterchange.Write(path, [new InterchangeMesh("Normal triangle", [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ], [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [0, 1, 2])]);
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        var material = new JsonObject { ["normalTexture"] = new JsonObject { ["index"] = 0, ["scale"] = .5f } };
        document["materials"] = new JsonArray(material);
        document["textures"] = new JsonArray(new JsonObject { ["source"] = 0 });
        document["meshes"]![0]!["primitives"]![0]!["material"] = 0;
        // The last texel faces negative Z; the shader rebuilds Z as positive, so it stores as its mirror.
        byte[] pixels = [230, 128, 204, 255, 166, 204, 230, 255, 128, 128, 255, 255, 230, 128, 20, 255];
        var project = StudioProject.Create(Path.Combine(root, "normal-project"), "Normal scale fixture");
        foreach (float scale in new[] { .5f, 2f, -1f })
        {
            Write(pixels, scale);
            OriginalAssetImporter.Import(project, catalog, new(path, package, "normal" + scale, null, false));
            var payload = project.AddedAssets.Last(asset => asset.Name.EndsWith(".normal.pixels", StringComparison.Ordinal));
            byte[] actual = project.ReadAddedAsset(payload.Id, catalog);
            for (int index = 0; index < pixels.Length; index += 4)
            {
                double x = (pixels[index] / 255d * 2 - 1) * scale;
                double y = (pixels[index + 1] / 255d * 2 - 1) * scale;
                double z = pixels[index + 2] / 255d * 2 - 1;
                double length = Math.Sqrt(x * x + y * y + z * z);
                byte encodedX = (byte)Math.Round((x / length + 1) * 127.5);
                byte encodedY = (byte)Math.Round((1 - y / length) * 127.5);
                Check(Math.Abs(actual[index] - encodedX) <= 1 && Math.Abs(actual[index + 1] - encodedY) <= 1,
                    "Normal scale or nonunit RGB skipped mandatory XYZ normalization.");
                Check(actual[index + 2] == 255 && actual[index + 3] == 255, "A native normal texel does not store Z and alpha as 255.");
            }
        }
        Console.WriteLine("original_normals scale=0.5,2,-1 rgb=normalized negative_z=mirrored z=positive");

        void Write(byte[] data, float scale)
        {
            material["normalTexture"]!["scale"] = scale;
            string uri = "data:image/png;base64," + Convert.ToBase64String(PngInterchange.Write(new(data.Length / 4, 1, data)));
            document["images"] = new JsonArray(new JsonObject { ["uri"] = uri });
            File.WriteAllText(path, document.ToJsonString());
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
