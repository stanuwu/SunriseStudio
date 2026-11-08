using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SunriseStudio.Rendering;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using Vortice.DXGI;

internal static class CubemapFixture
{
    /** Owned cube colors and geometry test stencil coverage, box fades, and foreground rejection. */
    public static void Run(Dx11Renderer renderer, GameMaterialReader reader, TfxParameterLookup parameters)
    {
        using var lighting = new NativeCubemapLighting(renderer, reader, new(), parameters);
        byte[] resource = new byte[Unsafe.SizeOf<UnknownCubemapPayload>()];
        ref var payload = ref TagView.Edit<UnknownCubemapPayload>(resource);
        var model = Matrix4x4.CreateTranslation(0, 0, -3);
        payload.SourceMatrixB0 = model;
        payload.SourceMatrixF0 = model;
        payload.Extents = Vector3.One;
        payload.FadeA = payload.FadeB = new(.25f, .25f, .25f, 0);
        payload.TextureIntensity = 1;
        payload.ResolutionX = payload.ResolutionY = payload.ResolutionZ = 1;
        payload.Pixel13W = 1;
        payload.Pixel14X = 1;
        payload.CellSize = payload.AlphaPower = .5f;
        var binding = NativeCubemapBinding.Create(resource, 1, 255, 2);
        var uploads = new List<TextureSubresourceUpload>();
        for (int face = 0; face < 6; face++)
            for (int mip = 0; mip < 2; mip++)
            {
                int width = 2 >> mip;
                var pixels = Enumerable.Repeat(new Vector4(.5f, .25f, .125f, 1), width * width).ToArray();
                uploads.Add(new(mip, face, MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray(), width * 16, width * width * 16));
            }
        using var cube = renderer.CreateTextureResource(2, 2, 1, 6, 2, Format.R32G32B32A32_Float, true, false, uploads);
        using var gbuffer = new NativeGBufferTargets(renderer, 64, 64);
        using var baseLighting = renderer.CreateNativeTargets(64, 64, Format.R11G11B10_Float, Format.R11G11B10_Float);
        renderer.ClearNativeTargets(baseLighting, Vector4.Zero, 1);
        GpuTexture Image(Vector4 value) => renderer.CreateTexture(64, 64, Format.R32G32B32A32_Float,
            MemoryMarshal.AsBytes(Enumerable.Repeat(value, 4096).ToArray().AsSpan()), 64 * 16);
        using var albedo = Image(new(.5f, .5f, .5f, 0));
        using var normal = Image(new(.5f, .5f, .95f, 1));
        using var third = Image(new(1, .5f, 0, 1));
        using var ao = Image(Vector4.One);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100);
        var scope = reader.ReadScopeExecution(0x80B356A5);
        Vector4[] Render(Vector3 eye, float planeZ)
        {
            var view = Matrix4x4.CreateLookAt(eye, eye - Vector3.UnitZ, Vector3.UnitY);
            var camera = NativeViewInputs.EvaluateCameraScope(scope, 64, 64, view, projection);
            var clip = Vector4.Transform(new Vector4(0, 0, planeZ, 1), view * projection);
            gbuffer.Clear(Vector4.Zero, clip.Z / clip.W);
            var depth = gbuffer.Resolve().Depth;
            lighting.Render([new(binding, cube)], new(albedo, normal, third, depth), gbuffer.WriteTargets,
                baseLighting.Textures[0], baseLighting.Textures[1], ao, camera, view, projection, 0, 0, .1f);
            return renderer.ReadTexturePixels(lighting.Ibl!, [(32, 32), (21, 32), (1, 1)]);
        }
        var outside = Render(Vector3.Zero, -3);
        Console.WriteLine($"cubemap519 outside_samples={string.Join(';', outside)}");
        Check(outside[0].X > .01f && outside[0].X > outside[0].Y * 2, "Authored cube did not light the volume interior.");
        Check(outside[1].X < outside[0].X * .5f, "Authored box fade did not reduce the edge contribution.");
        Check(outside[2].X < 1e-6f, "Cubemap light leaked outside its volume.");
        var foreground = Render(Vector3.Zero, -1);
        Check(foreground[0].X < 1e-6f, "Cubemap stencil lit foreground geometry outside its volume.");
        var inside = Render(new(0, 0, -3), -3.5f);
        Check(inside[0].X > .01f, "Camera-inside cubemap stencil rejected its volume.");
        Console.WriteLine($"cubemap519 center={outside[0]} edge={outside[1]} outside={outside[2]} foreground={foreground[0]} inside={inside[0]}");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
