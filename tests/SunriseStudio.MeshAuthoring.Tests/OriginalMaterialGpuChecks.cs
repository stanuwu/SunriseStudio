using System.Numerics;
using System.Text.Json;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

internal static class OriginalMaterialGpuChecks
{
    /** An owned Blender export reaches the original native shader through a complete project overlay. */
    public static void Run(string gameRoot, string input)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "sunrise-original-material-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var catalog = PackageCatalog.Open(gameRoot);
            var project = StudioProject.Create(temporary, "Original material fixture");
            ushort package = Enumerable.Range(0x100, 0xC00).Select(value => (ushort)value)
                .First(value => !catalog.ContainsInstalledPackage(value));
            Check(OriginalAssetImporter.Inspect(input).CanImportAll, "Owned Blender material fixture was refused.");
            OriginalAssetImporter.Import(project, catalog, new(input, package, "custom:panels", null, false));
            var metadata = project.AddedAssets.Select(asset =>
            {
                var native = asset.Native!; uint tag = TagHashes.Make(package, native.EntryIndex!.Value);
                return new PackageOverlayEntry(new PackageEntry(tag, native.Reference, (int)(native.TypeInfo >> 9 & 127),
                    (int)(native.TypeInfo >> 6 & 7), checked((int)asset.Size), $"w64_original_{package:X4}_0", package, native.EntryIndex.Value)
                    { TypeInfo = native.TypeInfo }, native.Name);
            }).ToArray();
            var data = project.AddedAssets.Zip(metadata).ToDictionary(pair => pair.Second.Entry.TagHash,
                pair => project.ReadAddedAsset(pair.First.Id, catalog));
            catalog.SetOverlayEntries(metadata); catalog.ReadOverride = tag => data.GetValueOrDefault(tag);
            uint model = metadata[0].Entry.TagHash;
            var scene = new AssetSceneLoader(catalog).LoadMesh(model);
            Check(scene.Instances.Count == 2 && scene.Instances.Select(part => part.Mesh.MaterialTag).Distinct().Count() == 2,
                "Two original material assignments collapsed into one part.");
            Check(scene.Instances.All(part => part.Mesh.Native?.NativeDeclarationId == 123), "Authored native declaration changed.");
            using var renderer = new Dx11Renderer(IntPtr.Zero, 512, 512, true);
            using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false, UseGameShaders = true };
            viewport.SetScene(scene);
            viewport.SetNativeScene(AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: long.MaxValue, assetPreview: true));
            viewport.Camera.SetExternalPose(new(0, 0, 5), -Vector3.UnitZ, Vector3.UnitY, MathF.PI / 3);
            var image = viewport.RenderImage(512, 512);
            Check(viewport.NativePartCount == 2, "Original materials were not admitted by the native renderer.");
            Vector3[] world = [new(-.75f, -.5f, 0), new(-.25f, -.5f, 0), new(-.75f, .5f, 0), new(-.25f, .5f, 0), new(.5f, 0, 0)];
            var matrix = viewport.Camera.ViewProjection(1);
            var points = world.Select(position =>
            {
                var clip = Vector4.Transform(new Vector4(position, 1), matrix);
                return (X: (int)((clip.X / clip.W * .5f + .5f) * 512), Y: (int)((.5f - clip.Y / clip.W * .5f) * 512));
            }).ToArray();
            var samples = viewport.NativeRenderer!.SamplePixels(points);
            var normals = renderer.ReadTexturePixels(viewport.NativeRenderer.Targets.Textures[1], points);
            var decoded = normals.Select(value => Vector3.Normalize(new Vector3(value.X, value.Y, value.Z) - new Vector3(.5f))).ToArray();
            var roughness = normals.Select(value => 1 - (new Vector3(value.X - .5f, value.Y - .5f, value.Z - .5f).Length() - .375f) / .125f).ToArray();
            string folder = Path.GetDirectoryName(Path.GetFullPath(input))!;
            string logs = Path.Combine(folder, "logs"); Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs, "native-materials.json"), JsonSerializer.Serialize(new
            { Samples = samples.Select((value, index) => new { World = world[index], Point = points[index], value.Albedo,
                value.Third, Normal = decoded[index], Roughness = roughness[index] }).ToArray() },
                new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            NativeShadingInspection.Capture(renderer, viewport, image, Path.Combine(folder, "native"));
            Vector3[] colors = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, new(1, 1, 0),
                new(Srgb(.1f), Srgb(.3f), Srgb(.7f))];
            for (int index = 0; index < colors.Length; index++)
            {
                var got = samples[index].Albedo;
                Vector3 Linear(Vector3 value) => new(ToLinear(value.X), ToLinear(value.Y), ToLinear(value.Z));
                Check(Vector3.Distance(Linear(new(got.X, got.Y, got.Z)), Linear(colors[index])) < .025f,
                    $"Material RGB or UV orientation differs at point {index}: {got}.");
                Check(MathF.Abs(samples[index].Third.X - (index < 4 ? 26 / 255f : .65f)) < .02f,
                    "Native metallic channel differs from glTF metallic.");
                Check(MathF.Abs(roughness[index] - (index < 4 ? .8f : .35f)) < .025f,
                    "Native smoothness does not invert glTF roughness.");
            }
            Check(decoded.Take(4).All(normal => Vector3.Distance(normal, new(0, .6f, .8f)) < .035f),
                "The native tangent normal differs from the Blender positive-Y normal.");
            Check(project.AddedAssets.Count == 26 && project.AddedAssets.Where(asset => asset.BaseTagHash is not null)
                .All(asset => asset.Path.StartsWith("deltas/", StringComparison.Ordinal)), "Original dependency storage lost its delta rule.");
            Console.WriteLine("original_materials native=warp parts=2 color=checked uv=checked normal=checked metallic=checked roughness=checked transaction=one");
        }
        finally
        {
            if (Directory.Exists(temporary) && Path.GetFullPath(temporary).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(temporary, true);
        }
    }
    private static float Srgb(float value) => value <= .0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1 / 2.4f) - .055f;
    private static float ToLinear(float value) => value <= .04045f ? value / 12.92f : MathF.Pow((value + .055f) / 1.055f, 2.4f);
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
