using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

var mesh = new InterchangeMesh("Quad",
    [new(-.8f, -.6f, .2f), new(.8f, -.6f, .2f), new(.8f, .6f, .2f), new(-.8f, .6f, .2f)],
    [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [0, 1, 2, 0, 2, 3]);
var encoded = MeshAssetImporter.EncodeGeometry(mesh);
Check(encoded.Positions.Length == 32 && encoded.Attributes.Length == 80 && encoded.Indices.Length == 24,
    "Encoded buffer lengths differ from native strides.");
for (int i = 0; i < mesh.Positions.Length; i++)
{
    Vector3 decoded = new(Snorm(encoded.Positions, i * 8), Snorm(encoded.Positions, i * 8 + 2),
        Snorm(encoded.Positions, i * 8 + 4));
    decoded = decoded * encoded.PositionScale + encoded.PositionOffset;
    Check(Vector3.Distance(decoded, mesh.Positions[i]) < 0.0001f, "Native position quantization lost precision.");
    Check(MathF.Abs(Snorm(encoded.Attributes, i * 20 + 8) - 1) < 0.0001f, "Normal Z encoding failed.");
    Check(MathF.Abs(Snorm(encoded.Attributes, i * 20 + 12) - 1) < 0.0001f, "Tangent X encoding failed.");
    Check(MathF.Abs(Snorm(encoded.Attributes, i * 20 + 18) - 1) < 0.0001f, "Tangent handedness encoding failed.");
}
Console.WriteLine("pass encoding=static_decl123 positions=4 indices=6 tangents=verified");
var mirrored = mesh with { TexCoords = mesh.TexCoords.Select(x => new Vector2(-x.X, x.Y)).ToArray() };
Check(Snorm(MeshAssetImporter.EncodeGeometry(mirrored).Attributes, 18) == -1, "Mirrored UV tangent sign is wrong.");
Reject<InvalidDataException>(() => MeshAssetImporter.EncodeGeometry(mesh with { Indices = [0, 1, 99] }));
Reject<OperationCanceledException>(() => MeshAssetImporter.EncodeGeometry(mesh, new CancellationToken(true)));
Console.WriteLine("pass mirrored_uv=verified invalid_indices=rejected cancellation=verified");
CompatibleSkinChecks.Weights();
EntityMaterialChecks.Synthetic();

if (args is ["--game-root", var materialGame, "--entity-materials"])
{
    try { EntityMaterialChecks.Owned(materialGame); }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
}
if (args is ["--game-root", var materialProfileGame, "--profile-materials"])
{
    try { EntityMaterialChecks.Profile(materialProfileGame); }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
}

if (args is ["--game-root", var spawnGame, "--authored-spawns"])
{
    try { EntityMaterialChecks.NativeRules(spawnGame); EntityMaterialChecks.Spawns(spawnGame); }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
}
if (args.Length == 2 && args[0] == "--game-root") RunInstalled(args[1], mesh);
if (args.Length == 2 && args[0] == "--game-root") CompatibleSkinChecks.Run(args[1]);
if (args.Length == 2 && args[0] == "--game-root") EntityMaterialChecks.Owned(args[1]);
if (args.Length == 2 && args[0] == "--game-root") EntityMaterialChecks.Variants(args[1]);
if (args.Length == 2 && args[0] == "--game-root") EntityMaterialChecks.NativeRules(args[1]);
if (args.Length == 2 && args[0] == "--game-root") EntityMaterialChecks.Spawns(args[1]);
if (args is ["--game-root", var skinGame, "--compatible-skin"])
{
    try { CompatibleSkinChecks.Run(skinGame); }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
}
if (args is ["--game-root", var gltfGame, "--compatible-gltf", var gltfPath])
{
    try { CompatibleGltfChecks.Run(gltfGame, gltfPath); }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
}
if (args is ["--game-root", var entityGame, "--compatible-entity", var entityPath])
{
    try { CompatibleEntityChecks.Run(entityGame, entityPath); }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
}
if (args is ["--game-root", var originalGame, "--original", var originalPath])
{
    try { OriginalMaterialGpuChecks.Run(originalGame, originalPath); }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
}
return 0;

static void RunInstalled(string gameRoot, InterchangeMesh mesh)
{
    string temporary = Path.Combine(Path.GetTempPath(), "sunrise-mesh-authoring-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporary);
    try
    {
        using var catalog = PackageCatalog.Open(gameRoot);
        var project = StudioProject.Create(Path.Combine(temporary, "project"), "Native mesh fixture");
        ushort packageId = Enumerable.Range(0x100, 0x0C00).Select(x => (ushort)x)
            .First(id => catalog.Entries.All(x => x.PackageId != id));
        string input = Path.Combine(temporary, "user-quad.glb");
        GltfInterchange.Write(input, [mesh]);
        var imported = MeshAssetImporter.ImportFile(project, catalog, input, "custom:quad", packageId);
        Check(imported.Assets.Count == 8 && imported.Assets.Count(x => x.BaseTagHash.HasValue) == 5,
            "Mesh metadata did not use five derived assets.");
        Check(imported.Assets.Count(x => !x.BaseTagHash.HasValue) == 3, "Geometry payloads were not stored as user assets.");
        foreach (int headerIndex in new[] { 2, 4, 6 })
        {
            uint headerTag = checked(imported.ModelTag + (uint)headerIndex);
            Check(imported.Assets[headerIndex].Native!.Reference == headerTag + 1
                && imported.Assets[headerIndex + 1].Native!.Reference == headerTag,
                "Authored mesh header and payload metadata are not reciprocal.");
        }
        Check(imported.Assets.Where(x => x.BaseTagHash.HasValue).All(x => x.Path.StartsWith("deltas/", StringComparison.Ordinal)),
            "A template header was stored whole.");
        Check(imported.Assets.Where(x => !x.BaseTagHash.HasValue).All(x => x.Path.StartsWith("assets/", StringComparison.Ordinal)),
            "User geometry was not stored whole.");
        var overlay = imported.Assets.Select(asset =>
        {
            var native = asset.Native!;
            int index = native.EntryIndex!.Value;
            uint tag = TagHashes.Make(native.PackageId, index);
            return new PackageOverlayEntry(new PackageEntry(tag, native.Reference, (int)(native.TypeInfo >> 9 & 127),
                (int)(native.TypeInfo >> 6 & 7), (int)asset.Size, $"w64_sunrise_{native.PackageId:x4}_0", native.PackageId, index)
            { TypeInfo = native.TypeInfo }, native.Name);
        }).ToArray();
        catalog.SetOverlayEntries(overlay);
        var bytesByTag = imported.Assets.Zip(overlay).ToDictionary(x => x.Second.Entry.TagHash,
            x => project.ReadAddedAsset(x.First.Id, catalog));
        catalog.ReadOverride = tag => bytesByTag.GetValueOrDefault(tag);
        var preview = new AssetSceneLoader(catalog).LoadMesh(imported.ModelTag);
        CheckMesh(preview, mesh);
        Check(preview.Instances.Single().Mesh.Native is { InputLayoutVerified: true, NativeDeclarationId: 123 },
            "Authored overlay lost its verified native declaration.");
        Check(preview.Instances.Single().Mesh.MaterialTag != 0, "Authored material assignment is missing.");
        Console.WriteLine($"pass overlay template={imported.TemplateTag:X8} model={imported.ModelTag:X8} vertices=4 triangles=2 native_layout=123");
        Draw(preview.Instances.Single().Mesh, Path.Combine(temporary, "mesh.png"));
        DrawNative(catalog, preview, Path.Combine(temporary, "native-mesh.png"));
        catalog.ReadOverride = null;
        catalog.SetOverlayEntries([]);
        project = StudioProject.Open(project.DirectoryPath);
        var build = project.Build(catalog);
        using var built = PackageCatalog.Open(build.OutputDirectory);
        var decoded = new AssetSceneLoader(built).LoadMesh(imported.ModelTag);
        CheckMesh(decoded, mesh);
        Check(built.Entries.Count == 8, "Authored package has an unexpected entry count.");
        foreach (int headerIndex in new[] { 2, 4, 6 })
        {
            uint headerTag = checked(imported.ModelTag + (uint)headerIndex);
            Check(built.TryGetEntry(headerTag, out var header) && header.Reference == headerTag + 1
                && built.TryGetEntry(headerTag + 1, out var payload) && payload.Reference == headerTag,
                "Built mesh retained a template metadata reference.");
        }
        foreach (var asset in imported.Assets)
        {
            uint tag = TagHashes.Make(packageId, asset.Native!.EntryIndex!.Value);
            Check(built.Read(tag).SequenceEqual(project.ReadAddedAsset(asset.Id, catalog)), "Package payload differs from project reconstruction.");
        }
        Console.WriteLine("pass native_package entries=8 reopen=verified geometry=equal delta_storage=verified");
        int previous = project.AddedAssets.Count;
        Reject<NotSupportedException>(() => MeshAssetImporter.ImportMesh(project, catalog, mesh, "bad", packageId, 0x80C0FA98));
        Check(project.AddedAssets.Count == previous, "Rejected template published an asset.");
        Reject<InvalidDataException>(() => MeshAssetImporter.ImportMesh(project, catalog, mesh, "bad", catalog.Entries[0].PackageId));
        Console.WriteLine("pass unsupported_template=rejected installed_package_id=rejected");
    }
    finally
    {
        if (Path.GetFullPath(temporary).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            Directory.Delete(temporary, true);
    }
}

static void CheckMesh(SceneDocument scene, InterchangeMesh expected)
{
    Check(scene.DecodeErrorCount == 0 && scene.Instances.Count == 1, string.Join(';', scene.Diagnostics));
    var mesh = scene.Instances.Single().Mesh;
    Check(mesh.Vertices.Length == expected.Positions.Length && mesh.Indices.SequenceEqual(expected.Indices),
        "Native mesh topology differs from user geometry.");
    for (int i = 0; i < mesh.Vertices.Length; i++)
    {
        Check(Vector3.Distance(mesh.Vertices[i].Position, expected.Positions[i]) < 0.0001f, "Native positions changed.");
        Check(Vector2.Distance(mesh.Vertices[i].TexCoord, expected.TexCoords[i]) < 0.0001f, "Native UVs changed.");
    }
}

static void Draw(MeshData mesh, string path)
{
    using var renderer = new Dx11Renderer(IntPtr.Zero, 256, 256, true);
    var vertices = mesh.Vertices.Select(x => new RenderVertex(x.Position, x.Normal, x.TexCoord)).ToArray();
    using var gpu = renderer.CreateMesh(vertices, mesh.Indices);
    renderer.BeginFrame(new Vector4(0, 0, 0, 1));
    renderer.DrawMesh(gpu, Matrix4x4.Identity, Matrix4x4.Identity, Vector4.One);
    renderer.CapturePng(path);
    var image = PngInterchange.Read(path);
    int bright = 0;
    for (int p = 0; p < image.Pixels.Length; p += 4)
        if (image.Pixels[p] > 30 || image.Pixels[p + 1] > 30 || image.Pixels[p + 2] > 30) bright++;
    Check(bright > 1000, "Authored mesh produced no visible DX11 pixels.");
    Console.WriteLine($"pass dx11=warp pipeline=diagnostic visible_pixels={bright}");
}

/** The original vertex shader reads authored streams with explicit instance and AO inputs. */
static void DrawNative(PackageCatalog catalog, SceneDocument scene, string path)
{
    using var renderer = new Dx11Renderer(IntPtr.Zero, 256, 256, true);
    using (var viewport = new AssetViewport(renderer))
    {
        viewport.SetScene(scene);
        var plan = AssetViewport.PrepareNativeScene(catalog, scene);
        viewport.SetNativeScene(plan);
        viewport.RenderImage(256, 256);
        Check(viewport.NativePartCount == 0 && plan.Diagnostics.Any(x => x.Contains("48-byte instance row", StringComparison.Ordinal)),
            "Standalone static native-plan limitation changed; update the fixture contract.");
        Console.WriteLine("pass native_scene limitation=authored_48_byte_instance_and_map_ao_required");
    }
    var native = scene.Instances.Single().Mesh.Native!;
    byte[] vertexCode = catalog.Read(native.VertexShaderDataTag);
    using var vertexShader = renderer.Device.CreateVertexShader(vertexCode);
    var pixelCode = Compiler.Compile("""
        float4 main(float4 n:TEXCOORD0, float4 t:TEXCOORD1, float3 b:TEXCOORD2,
                    float2 uv:TEXCOORD3):SV_TARGET
        { return float4(uv, saturate((n.z+t.x+b.y)/3), 1); }
        """, "main", "MeshAuthoringFixture", "ps_5_0", ShaderFlags.OptimizationLevel3);
    using var pixelShader = renderer.Device.CreatePixelShader(pixelCode.Span);
    using var layout = renderer.Device.CreateInputLayout(native.InputElements.Select(x =>
        new InputElementDescription(x.Semantic, x.SemanticIndex, (Format)x.Format, (uint)x.Offset, (uint)x.Slot)).ToArray(), vertexCode);
    using var positions = renderer.Device.CreateBuffer<byte>(native.VertexStreams[0].Data, BindFlags.VertexBuffer);
    using var attributes = renderer.Device.CreateBuffer<byte>(native.VertexStreams[1].Data, BindFlags.VertexBuffer);
    using var indices = renderer.Device.CreateBuffer<byte>(native.IndexData, BindFlags.IndexBuffer);
    var instance = new Vector4[64];
    float scale = native.PositionScale.X;
    instance[0] = new(scale, 0, 0, native.PositionOffset.X);
    instance[1] = new(0, scale, 0, native.PositionOffset.Y);
    instance[2] = new(0, 0, scale, native.PositionOffset.Z);
    instance[3] = new(native.UvTransform.X, native.UvTransform.Z, native.UvTransform.W,
        BitConverter.UInt32BitsToSingle(0x02000000));
    var view = new Vector4[14];
    view[0] = Vector4.UnitX; view[1] = Vector4.UnitY; view[2] = Vector4.UnitZ; view[13] = Vector4.UnitW;
    using var cb11 = renderer.Device.CreateBuffer<Vector4>(instance, BindFlags.ConstantBuffer);
    using var cb12 = renderer.Device.CreateBuffer<Vector4>(view, BindFlags.ConstantBuffer);
    using var ao = renderer.CreateNativeBufferResource([255, 255, 255, 255], Format.R8_UNorm);
    using var rasterizer = renderer.Device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
    byte[] Render()
    {
        renderer.BeginFrame(new Vector4(0, 0, 0, 1));
        renderer.Context.IASetInputLayout(layout);
        renderer.Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        renderer.Context.IASetVertexBuffer(0, positions, 8);
        renderer.Context.IASetVertexBuffer(1, attributes, 20);
        renderer.Context.IASetIndexBuffer(indices, Format.R32_UInt, 0);
        renderer.Context.VSSetShader(vertexShader);
        renderer.Context.VSSetConstantBuffer(11, cb11);
        renderer.Context.VSSetConstantBuffer(12, cb12);
        renderer.Context.VSSetShaderResource(0, ao.View);
        renderer.Context.PSSetShader(pixelShader);
        renderer.Context.RSSetState(rasterizer);
        renderer.Context.DrawIndexed(native.IndexCount, 0, 0);
        renderer.CapturePng(path);
        return PngInterchange.Read(path).Pixels;
    }
    int center = (128 * 256 + 128) * 4;
    var pixels = Render();
    Check(pixels[center] is > 110 and < 145 && pixels[center + 1] is > 110 and < 145
        && pixels[center + 2] > 245, "Original VS did not reproduce authored UVs and tangent basis.");
    var flipped = (byte[])native.VertexStreams[1].Data.Clone();
    for (int p = 18; p < flipped.Length; p += 20)
        BinaryPrimitives.WriteInt16LittleEndian(flipped.AsSpan(p), -32767);
    renderer.Context.UpdateSubresource<byte>(flipped, attributes);
    pixels = Render();
    Check(pixels[center + 2] is > 70 and < 100, "Original VS did not consume authored tangent handedness.");
    Console.WriteLine("pass dx11=warp shader=installed_vertex_shader positions=verified uv=verified tangent_basis=verified handedness=verified");
}

static float Snorm(byte[] bytes, int offset) => Math.Max(BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset)) / 32767f, -1);
static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
static void Reject<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
