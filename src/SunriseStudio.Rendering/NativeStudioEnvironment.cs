using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace SunriseStudio.Rendering;

/**
 * The studio a previewed asset reflects, in the layout the sky-apply pass 0x80B9E24B reads: a world direction d
 * (Z up) sits at uv = 0.5 + 0.5 * d.xy / |d + (0, 0, 1)|, up at the centre and down at the rim. Diffuse reads mip 8
 * at the normal and reflection reads mip 8 * sqrt(1 - smoothness) at the reflected view.
 */
public sealed class NativeStudioEnvironment : IDisposable
{
    public const string Policy = "Asset previews reflect a viewer studio: brighter above than below, a softbox toward"
        + " the key light and soft clouds, blurred to a flat level by roughness. It is viewer lighting, not a game environment.";
    // The flat level the roughest mips hold and the apply pass reads as diffuse. It sits under the map viewer's 0.18, as
    // a dark inspect room reflects little.
    private const float FlatRadiance = .054f;
    // The shader's studio is authored around a flat level of 0.32, scaled to FlatRadiance.
    private const float AuthoredFlat = .32f;

    [StructLayout(LayoutKind.Sequential)]
    private struct Settings
    {
        public Vector4 Key, Right, Mip;
    }

    private readonly Dx11Renderer renderer;
    private readonly ID3D11RenderTargetView[] targets;
    private readonly ID3D11VertexShader vertexShader;
    private readonly ID3D11PixelShader pixelShader;
    private readonly ID3D11Buffer constants;
    private readonly ID3D11RasterizerState rasterizer;
    private readonly ID3D11DepthStencilState noDepth;
    private readonly ID3D11BlendState opaque;
    private Vector3 lastKey, lastRight;
    private bool disposed;

    /** The reflection source to pass to the sky-apply pass. */
    public GpuTexture Reflection { get; }

    public NativeStudioEnvironment(Dx11Renderer renderer)
    {
        this.renderer = renderer;
        var device = renderer.Device;
        int size = NativeSkyLighting.ReflectionSize, mips = NativeSkyLighting.ReflectionMips;
        var texture = device.CreateTexture2D(Format.R16G16B16A16_Float, (uint)size, (uint)size, mipLevels: (uint)mips,
            bindFlags: BindFlags.ShaderResource | BindFlags.RenderTarget);
        Reflection = new GpuTexture(texture, device.CreateShaderResourceView(texture), size, size, mipLevels: mips);
        targets = Enumerable.Range(0, mips).Select(mip => device.CreateRenderTargetView(texture, new RenderTargetViewDescription(
            texture, RenderTargetViewDimension.Texture2D, Format.R16G16B16A16_Float, (uint)mip))).ToArray();
        vertexShader = device.CreateVertexShader(Dx11Renderer.Compile(Shader, "VSMain", "vs_5_0").Span);
        pixelShader = device.CreatePixelShader(Dx11Renderer.Compile(Shader, "PSStudio", "ps_5_0").Span);
        constants = device.CreateBuffer((uint)Marshal.SizeOf<Settings>(), BindFlags.ConstantBuffer);
        rasterizer = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
        noDepth = device.CreateDepthStencilState(
            new DepthStencilDescription(false, DepthWriteMask.Zero, ComparisonFunction.Always));
        opaque = device.CreateBlendState(new BlendDescription(Blend.One, Blend.Zero));
    }

    /**
     * Draws every mip for this key; an unchanged key keeps the last texture.
     * @param key Unit direction toward the key light.
     * @param right Unit direction the clouds run along, the camera's right.
     */
    public GpuTexture Update(Vector3 key, Vector3 right)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (key == lastKey && right == lastRight) return Reflection;
        lastKey = key; lastRight = right;
        var context = renderer.Context;
        context.RSSetState(rasterizer);
        context.OMSetDepthStencilState(noDepth, 0);
        context.OMSetBlendState(opaque);
        context.IASetInputLayout(null!);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(vertexShader);
        context.PSSetShader(pixelShader);
        context.PSSetConstantBuffer(0, constants);
        for (int mip = 0; mip < targets.Length; mip++)
        {
            int size = Math.Max(1, NativeSkyLighting.ReflectionSize >> mip);
            // The apply pass picks mip 8 * sqrt(1 - smoothness), so a mip stands for roughness (mip / 8)^2.
            float roughness = MathF.Min(1, mip / 8f * (mip / 8f));
            var settings = new Settings { Key = new(key, 0), Right = new(right, 0),
                Mip = new(size, roughness, FlatRadiance / AuthoredFlat, AuthoredFlat) };
            context.UpdateSubresource(in settings, constants);
            context.OMSetRenderTargets(targets[mip]);
            context.RSSetViewport(new Viewport(size, size));
            context.RSSetScissorRect(size, size);
            context.Draw(3, 0);
        }
        renderer.CountDraws(targets.Length);
        renderer.RestoreMainTarget();
        return Reflection;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        opaque.Dispose(); noDepth.Dispose(); rasterizer.Dispose(); constants.Dispose();
        pixelShader.Dispose(); vertexShader.Dispose();
        foreach (var target in targets) target.Dispose();
        Reflection.Dispose();
    }

    // The studio: a gradient of 0.08 to 0.6 by height, a softbox of 1.4 toward the key and clouds of +-0.102.
    private const string Shader = """
        cbuffer StudioSettings : register(b0) { float4 Key; float4 Right; float4 Mip; };
        struct PixelInput { float4 Position : SV_POSITION; };
        PixelInput VSMain(uint id : SV_VertexID) {
            PixelInput result;
            float2 uv = float2((id << 1) & 2, id & 2);
            result.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
            return result;
        }
        float HashCell(int x, int y) {
            uint v = uint(x) * 0x8DA6B343u ^ uint(y) * 0xD8163841u ^ 0x2C1B3C6Du;
            v ^= v >> 13u;
            v *= 0x5BD1E995u;
            v ^= v >> 15u;
            return float(v & 0xFFFFu) / 65535.0;
        }
        float ValueNoise(float2 p) {
            float2 cell = floor(p);
            float2 f = p - cell;
            f = f * f * (3.0 - 2.0 * f);
            int cx = int(cell.x), cy = int(cell.y);
            return lerp(lerp(HashCell(cx, cy), HashCell(cx + 1, cy), f.x),
                lerp(HashCell(cx, cy + 1), HashCell(cx + 1, cy + 1), f.x), f.y);
        }
        float4 PSStudio(PixelInput input) : SV_TARGET {
            // Inverse of the apply pass mapping: radius r gives z = 1 - 2r^2 and xy = 2p * sqrt(1 - r^2).
            float2 p = input.Position.xy / Mip.x * 2 - 1;
            float r2 = min(dot(p, p), 1);
            float3 d = float3(2 * p * sqrt(1 - r2), 1 - 2 * r2);
            float gradient = lerp(0.08, 0.6, saturate(0.5 + 0.6 * d.z));
            float softbox = 1.4 * exp(-(1 - dot(d, Key.xyz)) / 0.06);
            float clouds = (ValueNoise(float2(dot(d, Right.xyz) * 3 + 7, -d.z * 3 + 7)) - 0.5) * 0.204;
            float sharp = max(gradient + softbox + clouds, 0);
            float value = lerp(sharp, Mip.w, saturate(Mip.y * 2.5)) * Mip.z;
            return float4(value, value, value, 1);
        }
        """;
}
