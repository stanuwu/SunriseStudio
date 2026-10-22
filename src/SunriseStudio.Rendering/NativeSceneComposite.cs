using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace SunriseStudio.Rendering;

/** Viewer full-screen passes: composite, sky gradient, exposure meter, gain, antialiasing and present. */
public sealed class NativeSceneComposite : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Settings
    {
        public Vector4 Background;
        public Vector4 Flags;
        public Matrix4x4 InverseViewProjection;
        public Vector4 SkyZenith, SkyHorizon, SkyGround;
        public Vector4 SkyScale;
        public Vector4 SkyUp;
    }

    // Viewer sky colours in sky-layer units; the game's atmosphere lookups are not rendered.
    private static readonly Vector3 ViewerZenith = new(0.014f, 0.028f, 0.060f), ViewerHorizon = new(0.052f, 0.062f, 0.076f),
        ViewerGround = new(0.028f, 0.030f, 0.032f);

    // Pending GPU copies own their slots until read or discarded by an immediate measurement.
    private const int RingSize = 3;
    /** Viewer policy: the meter treats a pixel as reflecting at least this share of its light. */
    public const float MinimumReflectance = 0.02f;
    private readonly Dx11Renderer renderer;
    private readonly ID3D11VertexShader vertexShader;
    private readonly ID3D11PixelShader compositeShader, presentShader, luminanceShader, scaleShader, skyShader, antialiasShader;
    private readonly ID3D11Texture2D[] staging = new ID3D11Texture2D[RingSize];
    private int readIndex, writeIndex;
    private ID3D11Texture2D? luminance;
    private ID3D11RenderTargetView? luminanceTarget;
    private ID3D11ShaderResourceView? luminanceView;
    private int luminanceWidth = -1, luminanceHeight = -1;
    private uint luminanceMips;

    /** Mean log2 luminance of geometry pixels from a recent frame; null until the first readback. */
    public float? MeanLogLuminance { get; private set; }
    /** Share of measured pixels at or below the black floor; the mean leaves them out. */
    public float BlackFraction { get; private set; }
    public double ReadbackMilliseconds { get; private set; }
    public int ReadbackCount { get; private set; }
    public int PendingReadbacks { get; private set; }
    public int ReadbackBusyCount { get; private set; }
    public int SkippedMeasurements { get; private set; }
    private readonly ID3D11Buffer constants;
    private readonly ID3D11SamplerState sampler, linear;
    private readonly ID3D11RasterizerState rasterizer;
    private readonly ID3D11DepthStencilState noDepth, writeDepth;
    private readonly ID3D11BlendState opaque;
    private bool disposed;

    public NativeSceneComposite(Dx11Renderer renderer)
    {
        this.renderer = renderer;
        var device = renderer.Device;
        vertexShader = device.CreateVertexShader(Dx11Renderer.Compile(Shader, "VSMain", "vs_5_0").Span);
        compositeShader = device.CreatePixelShader(Dx11Renderer.Compile(Shader, "PSComposite", "ps_5_0").Span);
        skyShader = device.CreatePixelShader(Dx11Renderer.Compile(Shader, "PSSky", "ps_5_0").Span);
        presentShader = device.CreatePixelShader(Dx11Renderer.Compile(Shader, "PSPresent", "ps_5_0").Span);
        luminanceShader = device.CreatePixelShader(Dx11Renderer.Compile(Shader, "PSLuminance", "ps_5_0").Span);
        scaleShader = device.CreatePixelShader(Dx11Renderer.Compile(Shader, "PSScale", "ps_5_0").Span);
        antialiasShader = device.CreatePixelShader(Dx11Renderer.Compile(Shader, "PSAntialias", "ps_5_0").Span);
        for (int i = 0; i < RingSize; i++)
            staging[i] = device.CreateTexture2D(new Texture2DDescription
            {
                Width = 1, Height = 1, MipLevels = 1, ArraySize = 1, Format = Vortice.DXGI.Format.R16G16B16A16_Float,
                SampleDescription = Vortice.DXGI.SampleDescription.Default, Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read
            });
        constants = device.CreateBuffer((uint)Marshal.SizeOf<Settings>(), BindFlags.ConstantBuffer);
        sampler = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp));
        linear = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp));
        rasterizer = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
        noDepth = device.CreateDepthStencilState(new DepthStencilDescription(false, DepthWriteMask.Zero, ComparisonFunction.Always));
        writeDepth = device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.Always));
        opaque = device.CreateBlendState(new BlendDescription(Blend.One, Blend.Zero));
    }

    /**
     * Writes linear scene color; empty pixels get the viewer sky gradient, or the flat background without a camera.
     * @param diffuse Null draws albedo unlit.
     * @param sky Sky layer from DrawSkyGradient and the sky parts; null gives the flat background.
     * @param skyScale Multiplies the sky layer when it is copied into the scene.
     * @param skyOnly Leaves geometry pixels untouched; deferred shading already wrote them.
     */
    public void Composite(NativeRenderTargets target, GpuTexture albedo, GpuTexture? diffuse, GpuTexture? specular,
        ID3D11ShaderResourceView depth, Vector4 background, GpuTexture? sky = null, float skyScale = 1, bool skyOnly = false)
    {
        var context = renderer.Context;
        context.OMSetRenderTargets(target.Views, null);
        var settings = new Settings
        {
            Background = background, Flags = new(diffuse is null ? 0 : 1, skyOnly ? 2 : 0, sky is null ? 0 : 1, 0),
            SkyScale = new(skyScale)
        };
        context.PSSetShaderResource(4, sky?.View!);
        Draw(compositeShader, target.Width, target.Height, settings,
            albedo.View, diffuse?.View ?? albedo.View, specular?.View ?? albedo.View, depth, noDepth);
        context.PSSetShaderResource(4, null!);
    }

    /**
     * Fills a target with the viewer gradient in sky-layer units, times scale.
     * @param scale One for the sky layer; the atmosphere lookup stand-in uses a brighter copy.
     * @param flat One color in place of the gradient, such as an asset preview's backdrop; null draws the gradient.
     * @param up The gradient's zenith direction; null is world up. An item orbited in its own frame passes that frame's up.
     */
    public void DrawSkyGradient(NativeRenderTargets sky, ID3D11ShaderResourceView depth, Matrix4x4 viewProjection,
        float scale = 1, Vector3? flat = null, Vector3? up = null)
    {
        if (!Matrix4x4.Invert(viewProjection, out var inverse)) throw new InvalidOperationException("The view cannot be inverted.");
        renderer.Context.OMSetRenderTargets(sky.Views, null);
        var settings = new Settings
        {
            Flags = new(0, 0, 0, 1), InverseViewProjection = Matrix4x4.Transpose(inverse),
            SkyZenith = new((flat ?? ViewerZenith) * scale, 0), SkyHorizon = new((flat ?? ViewerHorizon) * scale, 0),
            SkyGround = new((flat ?? ViewerGround) * scale, 0), SkyUp = new(up ?? Vector3.UnitZ, 0)
        };
        Draw(skyShader, sky.Width, sky.Height, settings, depth, depth, depth, depth, noDepth);
    }

    /**
     * Averages log2 luminance over a 256 by 256 point grid.
     * @param sky True measures pixels without geometry instead of geometry pixels.
     * @param immediate Waits for this frame when an asset must be ready before its first display.
     * @param illumination Diffuse light at color's size; each pixel then counts as reflecting at least
     * MinimumReflectance of it.
     * @param uniformLight Light every pixel gets when color is unlit albedo; zero means no floor.
     */
    public void MeasureLuminance(GpuTexture color, ID3D11ShaderResourceView depth, bool sky = false,
        bool immediate = false, GpuTexture? illumination = null, float uniformLight = 0)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (illumination is not null && (illumination.Width != color.Width || illumination.Height != color.Height))
            throw new ArgumentException("The illumination does not match the measured color's size.", nameof(illumination));
        var context = renderer.Context;
        if (immediate) { readIndex = writeIndex = PendingReadbacks = 0; }
        else
        {
            // Keep one recent sample queued so ordinary reads are at least two frames old.
            while (PendingReadbacks >= 2 && ReadLuminance(false)) { }
            if (PendingReadbacks == RingSize) { SkippedMeasurements++; return; }
        }
        // A power-of-two grid makes every mip level an exact average of the level above.
        const int width = 256, height = 256;
        if (luminance is null || luminanceWidth != width || luminanceHeight != height)
        {
            luminanceView?.Dispose(); luminanceTarget?.Dispose(); luminance?.Dispose();
            luminance = renderer.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = width, Height = height, MipLevels = 0, ArraySize = 1,
                Format = Vortice.DXGI.Format.R16G16B16A16_Float, SampleDescription = Vortice.DXGI.SampleDescription.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource, MiscFlags = ResourceOptionFlags.GenerateMips
            });
            luminanceMips = luminance.Description.MipLevels;
            luminanceTarget = renderer.Device.CreateRenderTargetView(luminance,
                new RenderTargetViewDescription(RenderTargetViewDimension.Texture2D, Vortice.DXGI.Format.R16G16B16A16_Float));
            luminanceView = renderer.Device.CreateShaderResourceView(luminance);
            luminanceWidth = width; luminanceHeight = height;
        }
        context.OMSetRenderTargets(luminanceTarget!, null);
        Draw(luminanceShader, width, height,
            new()
            {
                Flags = new(illumination is null && uniformLight == 0 ? 0 : MinimumReflectance, sky ? 1 : 0, color.Width, color.Height),
                SkyScale = new(uniformLight, 0, 0, 0)
            },
            color.View, illumination?.View ?? color.View, color.View, depth, noDepth);
        context.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);
        context.GenerateMips(luminanceView!);
        context.CopySubresourceRegion(staging[writeIndex], 0, 0, 0, 0, luminance, luminanceMips - 1);
        writeIndex = (writeIndex + 1) % RingSize;
        PendingReadbacks++;
        if (immediate) ReadLuminance(true);
    }

    /** A busy sample stays queued; steady rendering must never wait for the GPU. */
    private unsafe bool ReadLuminance(bool wait)
    {
        var context = renderer.Context;
        long readbackBegan = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = context.Map(staging[readIndex], 0, MapMode.Read,
            wait ? Vortice.Direct3D11.MapFlags.None : Vortice.Direct3D11.MapFlags.DoNotWait, out var mapped);
        ReadbackMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(readbackBegan).TotalMilliseconds;
        if (result == Vortice.DXGI.ResultCode.WasStillDrawing) { ReadbackBusyCount++; return false; }
        result.CheckError();
        ReadbackCount++;
        try
        {
            ushort* value = (ushort*)mapped.DataPointer;
            float sum = (float)BitConverter.UInt16BitsToHalf(value[0]), coverage = (float)BitConverter.UInt16BitsToHalf(value[1]);
            float black = (float)BitConverter.UInt16BitsToHalf(value[2]);
            BlackFraction = coverage + black > 1e-4f ? black / (coverage + black) : 0;
            if (coverage > 1e-4f && float.IsFinite(sum)) MeanLogLuminance = sum / coverage;
        }
        finally { context.Unmap(staging[readIndex], 0); }
        readIndex = (readIndex + 1) % RingSize;
        PendingReadbacks--;
        return true;
    }

    /** Multiplies linear color by a viewer gain; the game's own exposure is applied in lighting. */
    public void Scale(GpuTexture source, NativeRenderTargets target, float gain)
    {
        renderer.Context.OMSetRenderTargets(target.Views, null);
        Draw(scaleShader, target.Width, target.Height, new() { Flags = new(gain, 0, 0, 0) },
            source.View, source.View, source.View, source.View, noDepth);
    }

    /** FXAA over a display-encoded image, for asset previews; flat areas keep their texels. */
    public void Antialias(GpuTexture source, NativeRenderTargets target)
    {
        renderer.Context.OMSetRenderTargets(target.Views, null);
        renderer.Context.PSSetSampler(1, linear);
        Draw(antialiasShader, target.Width, target.Height, new() { Flags = new(0, 0, 1f / target.Width, 1f / target.Height) },
            source.View, source.View, source.View, source.View, noDepth);
    }

    /** Copies an image to the bound swap chain and writes the native depth so later plain draws occlude correctly. */
    public void Present(GpuTexture image, ID3D11ShaderResourceView depth, bool keepSky)
    {
        renderer.RestoreMainTarget();
        Draw(presentShader, renderer.Width, renderer.Height, new() { Flags = new(0, keepSky ? 1 : 0, 0, 0) },
            image.View, image.View, image.View, depth, writeDepth);
        renderer.RestoreMainTarget();
    }

    private void Draw(ID3D11PixelShader shader, int width, int height, Settings settings,
        ID3D11ShaderResourceView t0, ID3D11ShaderResourceView t1, ID3D11ShaderResourceView t2,
        ID3D11ShaderResourceView t3, ID3D11DepthStencilState depthState)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var context = renderer.Context;
        context.RSSetViewport(new Viewport(width, height));
        context.RSSetScissorRect(width, height);
        context.RSSetState(rasterizer);
        context.OMSetDepthStencilState(depthState, 0);
        context.OMSetBlendState(opaque);
        context.IASetInputLayout(null!);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(vertexShader);
        context.PSSetShader(shader);
        context.UpdateSubresource(in settings, constants);
        context.PSSetConstantBuffer(0, constants);
        context.PSSetShaderResources(0, [t0, t1, t2, t3]);
        context.PSSetSampler(0, sampler);
        context.Draw(3, 0);
        renderer.CountDraws(1);
        context.PSSetShaderResources(0, new ID3D11ShaderResourceView[4]);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        opaque.Dispose(); writeDepth.Dispose(); noDepth.Dispose(); rasterizer.Dispose(); sampler.Dispose(); linear.Dispose();
        constants.Dispose(); presentShader.Dispose(); compositeShader.Dispose(); skyShader.Dispose(); vertexShader.Dispose();
        luminanceShader.Dispose(); scaleShader.Dispose(); antialiasShader.Dispose();
        luminanceView?.Dispose(); luminanceTarget?.Dispose(); luminance?.Dispose();
        foreach (var texture in staging) texture.Dispose();
    }

    private const string Shader = """
        cbuffer CompositeSettings : register(b0) {
            float4 Background; float4 Flags; float4x4 InverseViewProjection;
            float4 SkyZenith; float4 SkyHorizon; float4 SkyGround; float4 SkyScale; float4 SkyUp;
        };
        Texture2D SkyLayer : register(t4);
        Texture2D Source0 : register(t0);
        Texture2D Source1 : register(t1);
        Texture2D Source2 : register(t2);
        Texture2D<float> Depth : register(t3);
        struct PixelInput { float4 Position : SV_POSITION; };
        PixelInput VSMain(uint id : SV_VertexID) {
            PixelInput result;
            float2 uv = float2((id << 1) & 2, id & 2);
            result.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
            return result;
        }
        float3 SkyColor(float2 position) {
            if (Flags.w == 0) return Background.rgb;
            uint width, height;
            Depth.GetDimensions(width, height);
            float2 ndc = float2(position.x / width * 2 - 1, 1 - position.y / height * 2);
            float4 nearPoint = mul(float4(ndc, 0, 1), InverseViewProjection);
            float4 farPoint = mul(float4(ndc, 0.5, 1), InverseViewProjection);
            float3 direction = normalize(farPoint.xyz / farPoint.w - nearPoint.xyz / nearPoint.w);
            float up = dot(direction, SkyUp.xyz);
            return up >= 0 ? lerp(SkyHorizon.rgb, SkyZenith.rgb, pow(saturate(up), 0.5))
                : lerp(SkyHorizon.rgb, SkyGround.rgb, saturate(-up * 4));
        }
        float4 PSSky(PixelInput input) : SV_TARGET { return float4(SkyColor(input.Position.xy), 0); }
        float4 PSComposite(PixelInput input) : SV_TARGET {
            int2 pixel = int2(input.Position.xy);
            if (Depth.Load(int3(pixel, 0)) >= 1)
                return float4(Flags.z != 0 ? SkyLayer.Load(int3(pixel, 0)).rgb * SkyScale.x : Background.rgb, 1);
            if (Flags.y == 2) discard;
            float3 albedo = Source0.Load(int3(pixel, 0)).rgb;
            if (Flags.x == 0) return float4(albedo, 1);
            float3 diffuse = Source1.Load(int3(pixel, 0)).rgb;
            float3 specular = Source2.Load(int3(pixel, 0)).rgb;
            return float4(albedo * diffuse + specular, 1);
        }
        float4 PSLuminance(PixelInput input) : SV_TARGET {
            int2 pixel = int2(input.Position.xy / 256 * Flags.zw);
            if ((Depth.Load(int3(pixel, 0)) >= 1) != (Flags.y != 0)) return float4(0, 0, 0, 0);
            float3 color = Source0.Load(int3(pixel, 0)).rgb;
            float luminance = dot(color, float3(0.2126, 0.7152, 0.0722));
            if (isnan(luminance) || isinf(luminance)) return float4(0, 0, 0, 0);
            if (luminance <= 1e-7) return float4(0, 0, 1, 0);
            float light = SkyScale.x != 0 ? SkyScale.x : dot(Source1.Load(int3(pixel, 0)).rgb, float3(0.2126, 0.7152, 0.0722));
            if (!isnan(light) && !isinf(light)) luminance = max(luminance, Flags.x * light);
            return float4(log2(luminance), 1, 0, 0);
        }
        SamplerState Linear : register(s1);
        float3 Tap(float2 uv) { return Source0.SampleLevel(Linear, uv, 0).rgb; }
        float Luma(float3 color) { return dot(color, float3(0.299, 0.587, 0.114)); }
        float4 PSAntialias(PixelInput input) : SV_TARGET {
            float2 texel = Flags.zw, uv = input.Position.xy * texel;
            float3 middle = Tap(uv);
            float nw = Luma(Tap(uv - texel)), ne = Luma(Tap(uv + float2(1, -1) * texel));
            float sw = Luma(Tap(uv + float2(-1, 1) * texel)), se = Luma(Tap(uv + texel));
            float m = Luma(middle);
            float low = min(m, min(min(nw, ne), min(sw, se))), high = max(m, max(max(nw, ne), max(sw, se)));
            // Skip contrast below 0.0312 luma or one eighth of the brightest sample.
            if (high - low < max(0.0312, high * 0.125)) return float4(middle, 1);
            float2 direction = float2(-((nw + ne) - (sw + se)), (nw + sw) - (ne + se));
            // One eighth of the mean corner luma, at least 1/128, keeps the direction stable in dark regions.
            float reduce = max((nw + ne + sw + se) * (0.25 / 8.0), 1.0 / 128.0);
            // Limit the filter span to eight pixels on either axis.
            direction = clamp(direction / (min(abs(direction.x), abs(direction.y)) + reduce), -8.0, 8.0) * texel;
            float3 two = 0.5 * (Tap(uv + direction * (1.0 / 3.0 - 0.5)) + Tap(uv + direction * (2.0 / 3.0 - 0.5)));
            float3 four = two * 0.5 + 0.25 * (Tap(uv - direction * 0.5) + Tap(uv + direction * 0.5));
            float luma = Luma(four);
            return float4(luma < low || luma > high ? two : four, 1);
        }
        float4 PSScale(PixelInput input) : SV_TARGET {
            return float4(Source0.Load(int3(input.Position.xy, 0)).rgb * Flags.x, 1);
        }
        struct PresentOutput { float4 Color : SV_TARGET; float Depth : SV_DEPTH; };
        PresentOutput PSPresent(PixelInput input) {
            int2 pixel = int2(input.Position.xy);
            float depth = Depth.Load(int3(pixel, 0));
            if (depth >= 1 && Flags.y == 0) discard;
            PresentOutput output;
            output.Color = float4(Source0.Load(int3(pixel, 0)).rgb, 1);
            output.Depth = depth;
            return output;
        }
        """;
}
