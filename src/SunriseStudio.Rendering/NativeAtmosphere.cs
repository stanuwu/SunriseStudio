using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SunriseStudio.Rendering;

/** The atmosphere settings 0x7FF742EF7B60 copies from a payload, Unk10 to the end. */
public sealed class NativeAtmosphereSettings
{
    /** Parse takes one whole class-0x80807086 payload. */
    public static readonly int Size = Unsafe.SizeOf<UnknownAtmospherePayload>();
    private const uint NoHandle = uint.MaxValue;
    /** The mode-0 3D lookup, an optional second 3D lookup, and a 2D lookup. */
    public uint[] Lookups { get; }
    /** Time-of-day keys, one per lookup slice. */
    public float[] Keys { get; }

    /** The 16 x 256 RGBA8 object fog colour table 0x7FF742EFB590 reads. */
    public uint FogTable { get; }

    private NativeAtmosphereSettings(uint[] lookups, float[] keys, uint fogTable)
    { Lookups = lookups; Keys = keys; FogTable = fogTable; }

    public static NativeAtmosphereSettings Parse(ReadOnlySpan<byte> resource)
    {
        if (resource.Length != Size) throw new ArgumentException($"Atmosphere payloads are {Size} bytes.");
        ref readonly var payload = ref TagView.At<UnknownAtmospherePayload>(resource);
        uint[] lookups = [payload.Lookup3dMode0, payload.Lookup3dSecond, payload.Lookup2d];
        float[] keys = ((ReadOnlySpan<float>)payload.TimeOfDayKeys).ToArray();
        return new(lookups, keys, payload.ObjectFogTable);
    }

    /** The fill 0x7FF742EF1820 refuses mode 0 without the first and third handles. */
    public bool HasMode0Lookups => Lookups[0] != NoHandle && Lookups[2] != NoHandle;
    /** Extern 7 resource 5 falls back to the first lookup when the second handle is absent. */
    public uint SecondLookup => Lookups[1] != NoHandle ? Lookups[1] : Lookups[0];

    /**
     * Port of 0x7FF742EF3030: finds the key pair around the time and returns index plus blend.
     * @param time Time of day in [0, 1).
     * @param keyframe Receives the pair's first index plus the blend weight.
     * @return False when no pair spans the time; the native pass then draws the black sky.
     */
    public bool TryPickKeyframe(float time, out float keyframe)
    {
        for (int i = 0; i < Keys.Length; i++)
        {
            int next = (i + 1) % Keys.Length;
            float low = Keys[i], high = Keys[next];
            // The pair from the last key to the first wraps across midnight.
            if (next < i)
            {
                if (high < time) high += 1;
                else low -= 1;
            }
            if (time < low || time > high) continue;
            keyframe = i + (high == low ? 0 : (time - low) / (high - low));
            return true;
        }
        keyframe = 0;
        return false;
    }

    /** Mode 0 stores (keyframe + 0.5) / 16 in extern 7 float 12, the lookup's W coordinate. */
    public static float SliceCoordinate(float keyframe) => (keyframe + 0.5f) * 0.0625f;
}

/** Inputs for one atmosphere frame; Light is the time-of-day light direction that frame +0x37020 holds. */
public sealed record NativeAtmosphereFrame(NativeAtmosphereSettings Settings, GpuTexture FirstLookup,
    GpuTexture SecondLookup, NativeCameraScopeConstants Camera, Matrix4x4 View, Matrix4x4 Projection,
    float TimeOfDay, Vector3 Light, ID3D11ShaderResourceView Depth, int DepthWidth, int DepthHeight);

/** Runs the atmosphere chain of 0x7FF742EFDB20 on placed atmosphere data: sky mask, light shafts, lookup, sky. */
public sealed class NativeAtmosphere : IDisposable
{
    public const uint GenerateMaterial = 0x80B35858, SkyMaterial = 0x80B35846, SkyMaskMaterial = 0x80B35867,
        DownsampleMaterial = 0x80B35A5A;
    // radial_blur_8, _12 and _16 (techniques 303 to 305); 0x7FF742F0B6B0 picks one by source width.
    private static readonly uint[] RadialMaterials = [0x80B35DCC, 0x80B35DDD, 0x80B35DE1];
    // The sky_mask fixed target is 64 x 64 R8_UNORM.
    private const int SkyMaskSize = 64;
    // Float16 maximum: what an empty pixel holds in a half-float linear depth target.
    private const float FarDepth = 65504;
    private const string QuadProgram = "3E020E4400";
    private const string GenerateProgram = "4C0049214C0149223F070447203F070547213F071147223F070C47233F071047243C073843053C0739"
        + "43063C075143163C075243173C075343183C070C431A3D070A431B3D0705431E3D071643203C075C43223C075D43233C075E4324"
        + "4E2B432B4E28432C3D250B221A432E3D250C221A432F3D250D221A4330";
    private const string SkyProgram = "4C0049213F071147203D070D4304";
    // Keys at 0x7FF743C9742C; 0x7FF742EF8100 stores their parameter indices at settings +0xF0.
    private static readonly uint[] SlotKeys =
    [
        0x056007C7, 0x4AA1BEF5, 0x9859DAF1, 0xC876108A, 0xE111D856, 0xF853533C, 0xED4BB08A, 0x9E769ED2,
        0x49FBBCE1, 0x94D8ECDC, 0xBD2C7FE8, 0x9EC7A5E8, 0xB630810B, 0x3EEACB23, 0x7E92EB31, 0x9F4CB78F,
        0x3E9CB6ED, 0x05FC9836, 0xE283FBE0, 0x5F3B8491, 0x79F2E305, 0x62E4542E, 0x949768CF, 0xD9A2D8A3,
        0xD8281393, 0x4DA73CA7, 0xE685C537, 0xE4A1BF60, 0x063D92F7, 0x49864A42
    ];
    public const string Policy = "Atmosphere: the placed class-0x80807086 atmosphere and its class-0x80806F61 time-of-day"
        + " curves drive sky_generate_sky_mask (110), downsample_block_2x2 (237), two radial_blur passes (303 to 305),"
        + " sky_lookup_generate (105) and sky (95), with the keyframe and light direction the time of day picks."
        + " The min/max linear depth input of the sky mask is a viewer pass at eighth resolution; the game's producer"
        + " (view +0x238) is not identified. light_shaft_occlusion parts (stage 9) are not drawn into the sky mask."
        + " The hemisphere passes (584, 583) are not run: with the authored parameters extern 7 float 93 is zero, so"
        + " the lookup never reads resource 16 or the sky mask directly; resource 16 reads one.";

    /** One full-screen technique with its own constant buffers and TFX bindings. */
    private sealed class Pass(MaterialExecutionData execution, NativeMaterialPipeline pipeline,
        NativeConstantBuffer? vertex, NativeConstantBuffer? pixel)
    {
        public MaterialExecutionData Execution { get; } = execution;
        public NativeMaterialPipeline Pipeline { get; } = pipeline;
        public NativeDrawBindings Bindings { get; } = new();
        public NativeConstantBuffer? Vertex { get; } = vertex;
        public NativeConstantBuffer? Pixel { get; } = pixel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DepthSettings
    {
        public Vector4 Linear;
        public Vector4 Size;
    }

    // Viewer stand-in for the min/max linear depth target the sky mask reads (x = min, y = max).
    private const string DepthShader = """
        cbuffer Settings : register(b0) { float4 Linear; float4 Size; };
        Texture2D<float> Depth : register(t0);
        float4 VSMain(uint id : SV_VertexID) : SV_Position
        {
            float2 uv = float2((id << 1) & 2, id & 2);
            return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
        }
        float2 PSMain(float4 position : SV_Position) : SV_Target
        {
            uint2 start = uint2(position.xy) * uint2(Size.zw);
            float low = 1e30, high = 0;
            for (uint y = 0; y < (uint)Size.w; y++)
            for (uint x = 0; x < (uint)Size.z; x++)
            {
                uint2 p = min(start + uint2(x, y), uint2(Size.xy) - 1);
                float d = Depth.Load(int3(p, 0));
                float w = d >= 1 ? Linear.z : 1 / max(d * Linear.x + Linear.y, 1e-30);
                low = min(low, w); high = max(high, w);
            }
            return float2(min(low, Linear.z), min(high, Linear.z));
        }
        """;

    private readonly Dx11Renderer renderer;
    private readonly List<IDisposable> owned = [];
    private readonly Pass generate, sky, skyMask, downsample;
    private readonly Pass[] radial;
    private readonly NativeConstantBuffer vertexView, pixelView;
    private readonly GpuTexture one;
    private readonly TfxParameterLookup parameters;
    private readonly Vector4[] slots;
    private readonly ID3D11VertexShader depthVertex;
    private readonly ID3D11PixelShader depthPixel;
    private readonly ID3D11Buffer depthConstants;
    private readonly ID3D11RasterizerState depthRasterizer;
    private readonly ID3D11DepthStencilState depthNoTest;
    private readonly ID3D11BlendState depthOpaque;
    private NativeRenderTargets? minMax, mask, maskDownsampled, radialTemp, lightShafts;
    private readonly TfxResource first = new("atmosphere lookup", ShaderResourceViewDimension.Texture3D);
    private readonly TfxResource second = new("atmosphere second lookup", ShaderResourceViewDimension.Texture3D);
    private readonly TfxResource maskToken = new("sky mask", ShaderResourceViewDimension.Texture2D);
    private readonly TfxResource shafts = new("light shafts", ShaderResourceViewDimension.Texture2D);
    private readonly TfxResource hemisphere = new("hemisphere", ShaderResourceViewDimension.Texture2D);
    private readonly TfxResource lookupToken = new("screen atmosphere lookup", ShaderResourceViewDimension.Texture2D);
    private readonly TfxResource source = new("extern 37 source", ShaderResourceViewDimension.Texture2D);
    private bool disposed;

    /** Parameter slots whose key is absent from the render-global table; the fill leaves them zero. */
    public IReadOnlyList<uint> MissingKeys { get; }
    public float LastKeyframe { get; private set; } = float.NaN;
    /** Screen UV of the light direction from the last Generate. */
    public Vector2 LastLightScreen { get; private set; }
    /** True when the light direction lay behind the camera in the last Generate. */
    public bool LastLightBehind { get; private set; }
    public GpuTexture? LightShafts => lightShafts?.Textures[0];
    public GpuTexture? SkyMask => mask?.Textures[0];

    public NativeAtmosphere(Dx11Renderer renderer, GameMaterialReader reader, TfxParameterDefaults defaults,
        TfxParameterLookup parameters)
    {
        this.renderer = renderer;
        this.parameters = parameters;
        try
        {
            generate = CreatePass(reader, GenerateMaterial, false);
            sky = CreatePass(reader, SkyMaterial, true);
            skyMask = CreatePass(reader, SkyMaskMaterial, false);
            downsample = CreatePass(reader, DownsampleMaterial, false);
            radial = RadialMaterials.Select(tag => CreatePass(reader, tag, false)).ToArray();
            if (Convert.ToHexString(generate.Execution.Vertex.TfxBytecode) != QuadProgram
                || Convert.ToHexString(sky.Execution.Vertex.TfxBytecode) != QuadProgram
                || Convert.ToHexString(generate.Execution.Pixel.TfxBytecode) != GenerateProgram
                || Convert.ToHexString(sky.Execution.Pixel.TfxBytecode) != SkyProgram)
                throw new NotSupportedException("Atmosphere TFX programs differ from their verified contract.");
            slots = ReadSlots(defaults, parameters, out var missing);
            MissingKeys = missing;
            vertexView = Own(renderer.CreateNativeConstantBuffer(new byte[10 * 16]));
            pixelView = Own(renderer.CreateNativeConstantBuffer(new byte[13 * 16]));
            one = Own(renderer.CreateTexture(1, 1, [255, 255, 255, 255]));
            foreach (var pass in radial.Append(generate).Append(sky).Append(skyMask).Append(downsample))
            {
                if (pass.Vertex is not null) pass.Bindings.ConstantBuffers[new(GameShaderStage.Vertex, 0)] = pass.Vertex;
                if (pass.Pixel is not null) pass.Bindings.ConstantBuffers[new(GameShaderStage.Pixel, 0)] = pass.Pixel;
                pass.Bindings.ConstantBuffers[new(GameShaderStage.Vertex, 12)] = vertexView;
                pass.Bindings.ConstantBuffers[new(GameShaderStage.Pixel, 12)] = pixelView;
            }
            var device = renderer.Device;
            depthVertex = Own(device.CreateVertexShader(Dx11Renderer.Compile(DepthShader, "VSMain", "vs_5_0").Span));
            depthPixel = Own(device.CreatePixelShader(Dx11Renderer.Compile(DepthShader, "PSMain", "ps_5_0").Span));
            depthConstants = Own(device.CreateBuffer((uint)Marshal.SizeOf<DepthSettings>(), BindFlags.ConstantBuffer));
            depthRasterizer = Own(device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid)));
            depthNoTest = Own(device.CreateDepthStencilState(new DepthStencilDescription(false, DepthWriteMask.Zero,
                ComparisonFunction.Always)));
            depthOpaque = Own(device.CreateBlendState(new BlendDescription(Blend.One, Blend.Zero)));
        }
        catch { Dispose(); throw; }
    }

    private T Own<T>(T item) where T : IDisposable { owned.Add(item); return item; }

    /** The sky draw tests depth at one so it lands only where no geometry wrote depth. */
    private Pass CreatePass(GameMaterialReader reader, uint tag, bool skyDepth)
    {
        var execution = reader.ReadExecution(tag);
        // The lookup quad may sit just past depth one; see DirectionDepth.
        var rasterizer = new RasterizerDescription(CullMode.None, FillMode.Solid) { DepthClipEnable = false };
        var depth = skyDepth ? new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.LessEqual)
            : new DepthStencilDescription(false, DepthWriteMask.Zero, ComparisonFunction.Always);
        var pipeline = Own(renderer.CreateNativePipeline(reader, execution.Material, [], rasterizer, depth,
            new BlendDescription(Blend.One, Blend.Zero)));
        NativeConstantBuffer? Buffer(int rows) => rows == 0 ? null : Own(renderer.CreateNativeConstantBuffer(new byte[rows * 16]));
        return new(execution, pipeline, Buffer(execution.Vertex.ConstantTemplate.Length), Buffer(execution.Pixel.ConstantTemplate.Length));
    }

    /** Phase terms get g / 2000 - 1 and a scale times 1.5 (1 - g^2) / (2 + g^2), as the fill does. */
    private static (float G, float Scale) Phase(float authoredG, float authoredScale)
    {
        float g = authoredG / 2000 - 1;
        return (g, 1.5f * (1 - g * g) / (g * g + 2) * authoredScale);
    }

    /** Extern 7 vector 5 of a target: width, height and their reciprocals. */
    public static Vector4 SizeOf(int width, int height) => new(width, height, 1f / width, 1f / height);

    /**
     * Reads the 30 parameter slots from the render-global table.
     * @param missing Receives the keys the table lacks; their slots stay zero.
     */
    public static Vector4[] ReadSlots(TfxParameterDefaults defaults, TfxParameterLookup parameters, out List<uint> missing)
    {
        var values = new Vector4[SlotKeys.Length];
        missing = [];
        for (int i = 0; i < SlotKeys.Length; i++)
        {
            var entry = defaults.Entries.FirstOrDefault(e => e.LookupKey == SlotKeys[i]);
            if (entry is null) missing.Add(SlotKeys[i]);
            else values[i] = parameters.Read(checked((byte)entry.Index));
        }
        return values;
    }

    /** World Z of the camera, which the height-fog terms read. */
    public static float CameraHeight(Matrix4x4 view) => Matrix4x4.Invert(view, out var world) ? world.M43 : 0;

    /**
     * Writes the slot part of extern 7 as 0x7FF742EF1820 does: vectors 13, 17, 19, 22 and floats 56 to 97.
     * @param cameraHeight World Z of the camera; floats 61 and 73 fall off with it.
     */
    public static void ApplyParameters(TfxInputs inputs, IReadOnlyList<Vector4> values, float cameraHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(values.Count, SlotKeys.Length);
        var f = new float[98];
        for (int slot = 0; slot < SlotKeys.Length; slot++)
        {
            var (index, vector) = SlotTargets[slot];
            if (vector) inputs.Vectors[new(7, (byte)index)] = values[slot];
            else f[index] = values[slot].X;
        }
        (f[56], f[57]) = Phase(f[56], f[57]);
        (f[58], f[59]) = Phase(f[58], f[59]);
        (f[92], f[93]) = Phase(f[92], f[93]);
        f[81] /= 360;
        // Height fog: density times exp(-(camera z - base) * falloff / 1000), the exponent capped at 64.
        f[73] *= MathF.Exp(MathF.Min((cameraHeight - f[75]) * f[74] * -0.001f, 64));
        f[61] *= MathF.Exp(MathF.Min((cameraHeight - f[65]) * f[64] * -0.001f, 64));
        if (f[73] == 0) f[74] = MinimumFalloff;
        if (f[61] == 0) f[64] = MinimumFalloff;
        f[64] = MathF.Max(f[64], MinimumFalloff);
        f[74] = MathF.Max(f[74], MinimumFalloff);
        if (f[62] == f[63]) f[63] = f[62] + MinimumFalloff;
        foreach (var (index, vector) in SlotTargets)
            if (!vector) inputs.Floats[new(7, (byte)index)] = f[index];
        // The fill writes 0.0625 at vector 25 X.
        inputs.Floats[new(7, 100)] = 0.0625f;
    }

    // The fill's floor for the two fog falloffs and the gap between floats 62 and 63.
    private const float MinimumFalloff = 1e-4f;
    // Destination of each slot in extern 7, as 0x7FF742EF1820 writes it: a whole vector or one float.
    private static readonly (int Index, bool Vector)[] SlotTargets =
    [
        (13, true), (56, false), (57, false), (58, false), (59, false), (60, false), (61, false), (62, false),
        (63, false), (64, false), (65, false), (17, true), (72, false), (73, false), (74, false), (75, false),
        (19, true), (80, false), (81, false), (82, false), (83, false), (84, false), (85, false), (22, true),
        (92, false), (93, false), (94, false), (95, false), (96, false), (97, false)
    ];

    private NativeRenderTargets Target(ref NativeRenderTargets? target, int width, int height, Format format)
    {
        if (target is null || target.Width != width || target.Height != height)
        {
            target?.Dispose();
            target = renderer.CreateNativeTargets(width, height, format);
        }
        return target;
    }

    /**
     * Runs the chain into target 0 of lookup: sky mask, light shafts, then sky_lookup_generate.
     * @return False when no keyframe spans the time; the lookup is then cleared to black.
     */
    public bool Generate(NativeAtmosphereFrame frame, NativeRenderTargets lookup)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        renderer.ClearNativeColorTargets(lookup, Vector4.Zero);
        if (!frame.Settings.HasMode0Lookups || !frame.Settings.TryPickKeyframe(frame.TimeOfDay, out float keyframe))
        {
            LastKeyframe = float.NaN;
            renderer.RestoreMainTarget();
            return false;
        }
        LastKeyframe = keyframe;
        var shaftTarget = RenderLightShafts(frame, lookup.Width, lookup.Height);
        int width = lookup.Width, height = lookup.Height;
        var inputs = CameraInputs(frame, width, height);
        ApplyParameters(inputs, slots, CameraHeight(frame.View));
        inputs.Floats[new(7, 12)] = NativeAtmosphereSettings.SliceCoordinate(keyframe);
        inputs.Vectors[new(7, 10)] = new(frame.Light, 0);
        inputs.Vectors[new(7, 5)] = SizeOf(width, height);
        // Only the hemisphere lookup reads extern 37 vectors 11 to 13; that path is inert with authored parameters.
        inputs.Vectors[new(37, 11)] = Vector4.UnitX;
        inputs.Vectors[new(37, 12)] = Vector4.UnitY;
        inputs.Vectors[new(37, 13)] = Vector4.UnitZ;
        inputs.Resources[new(7, 4)] = first;
        inputs.Resources[new(7, 5)] = second;
        inputs.Resources[new(7, 17)] = maskToken;
        inputs.Resources[new(7, 12)] = shafts;
        inputs.Resources[new(7, 16)] = hemisphere;
        Run(frame, generate, inputs, lookup, new Dictionary<TfxResource, ID3D11ShaderResourceView>
        {
            [first] = frame.FirstLookup.View, [second] = frame.SecondLookup.View,
            [maskToken] = mask!.Textures[0].View, [shafts] = shaftTarget.View, [hemisphere] = one.View
        });
        return true;
    }

    /** Sky mask (110), 2x2 downsample (237) and two radial blurs toward the light, in 0x7FF742EFDB20 order. */
    private GpuTexture RenderLightShafts(NativeAtmosphereFrame frame, int width, int height)
    {
        // Transient targets 32, 34 and 57 use the eighth-resolution size class.
        int eighthWidth = Math.Max(1, (width + 7) / 8), eighthHeight = Math.Max(1, (height + 7) / 8);
        var depthTarget = Target(ref minMax, eighthWidth, eighthHeight, Format.R16G16_Float);
        DrawMinMaxDepth(frame, depthTarget);
        var maskTarget = Target(ref mask, SkyMaskSize, SkyMaskSize, Format.R8_UNorm);
        var inputs = SourceInputs(frame, depthTarget, maskTarget);
        // Extern 7 floats 95 and 85 are parameter slots 27 and 22.
        inputs.Floats[new(7, 95)] = slots[27].X;
        inputs.Floats[new(7, 85)] = slots[22].X;
        Run(frame, skyMask, inputs, maskTarget, Views(depthTarget));
        var down = Target(ref maskDownsampled, eighthWidth, eighthHeight, Format.R16G16B16A16_UNorm);
        Run(frame, downsample, SourceInputs(frame, maskTarget, down), down, Views(maskTarget));
        var (screen, behind) = LightScreen(frame);
        LastLightScreen = screen; LastLightBehind = behind;
        var pass = radial[down.Width >= 480 ? 2 : down.Width >= 320 ? 1 : 0];
        float side = behind ? -1 : 1;
        var temp = Target(ref radialTemp, eighthWidth, eighthHeight, Format.R16G16B16A16_Float);
        var radialInputs = SourceInputs(frame, down, temp);
        radialInputs.Vectors[new(37, 11)] = new(screen.X, screen.Y, 0.03f, 0.022f);
        radialInputs.Vectors[new(37, 12)] = new(side, 1, 0, 0);
        Run(frame, pass, radialInputs, temp, Views(down));
        var result = Target(ref lightShafts, eighthWidth, eighthHeight, Format.R16G16B16A16_UNorm);
        radialInputs = SourceInputs(frame, temp, result);
        radialInputs.Vectors[new(37, 11)] = new(screen.X, screen.Y, 0.08f, 0.0587f);
        radialInputs.Vectors[new(37, 12)] = new(side, 0.2725f, 0, 0);
        Run(frame, pass, radialInputs, result, Views(temp));
        return result.Textures[0];
    }

    private Dictionary<TfxResource, ID3D11ShaderResourceView> Views(NativeRenderTargets input) =>
        new() { [source] = input.Textures[0].View };

    /** Extern 37 as 0x7FF742F0E150 and 0x7FF742F0CF40 fill it: resource 0 and vector 5 are the input, vector 4 the output. */
    private TfxInputs SourceInputs(NativeAtmosphereFrame frame, NativeRenderTargets input, NativeRenderTargets output)
    {
        var inputs = CameraInputs(frame, output.Width, output.Height);
        inputs.Resources[new(37, 0)] = source;
        inputs.Vectors[new(37, 4)] = SizeOf(output.Width, output.Height);
        inputs.Vectors[new(37, 5)] = SizeOf(input.Width, input.Height);
        return inputs;
    }

    /** 0x7FF742EFDB20 places the light direction one unit ahead in view space, projects it and maps NDC to UV. */
    private static (Vector2 Screen, bool Behind) LightScreen(NativeAtmosphereFrame frame)
    {
        var view = Vector4.Transform(new Vector4(frame.Light, 0), frame.View);
        var clip = Vector4.Transform(new Vector4(view.X, view.Y, view.Z, 1), frame.Projection);
        if (MathF.Abs(clip.W) <= 1e-6f) return (new(0.5f, 0.5f), false);
        return (new(clip.X / clip.W * 0.5f + 0.5f, -0.5f * clip.Y / clip.W + 0.5f), clip.W < 0);
    }

    private void DrawMinMaxDepth(NativeAtmosphereFrame frame, NativeRenderTargets target)
    {
        var p = frame.Projection;
        var settings = new DepthSettings
        {
            // Forward depth d gives 1 / w = d / M43 - M33 / (M34 * M43).
            Linear = new(1 / p.M43, -p.M33 / (p.M34 * p.M43), FarDepth, 0),
            Size = new(frame.DepthWidth, frame.DepthHeight, (frame.DepthWidth + target.Width - 1) / target.Width,
                (frame.DepthHeight + target.Height - 1) / target.Height)
        };
        var context = renderer.Context;
        context.OMSetRenderTargets(target.Views, null);
        context.RSSetViewport(new Vortice.Mathematics.Viewport(target.Width, target.Height));
        context.RSSetScissorRect(target.Width, target.Height);
        context.RSSetState(depthRasterizer);
        context.OMSetDepthStencilState(depthNoTest, 0);
        context.OMSetBlendState(depthOpaque);
        context.IASetInputLayout(null!);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(depthVertex);
        context.PSSetShader(depthPixel);
        context.UpdateSubresource(in settings, depthConstants);
        context.PSSetConstantBuffer(0, depthConstants);
        context.PSSetShaderResource(0, frame.Depth);
        context.Draw(3, 0);
        renderer.CountDraws(1);
        context.PSSetShaderResource(0, null!);
        renderer.RestoreMainTarget();
    }

    /** Draws technique 95 over the sky pixels of target, reading the lookup Generate wrote. */
    public void DrawSky(NativeAtmosphereFrame frame, GpuTexture lookup, NativeRenderTargets target, NativeConstantBuffer frameScope)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var inputs = CameraInputs(frame, target.Width, target.Height);
        inputs.Resources[new(7, 17)] = lookupToken;
        inputs.Vectors[new(7, 13)] = slots[0];
        sky.Bindings.ConstantBuffers[new(GameShaderStage.Pixel, 13)] = frameScope;
        Run(frame, sky, inputs, target, new Dictionary<TfxResource, ID3D11ShaderResourceView> { [lookupToken] = lookup.View });
    }

    private static TfxInputs CameraInputs(NativeAtmosphereFrame frame, int width, int height)
    {
        var inputs = new TfxInputs();
        var camera = NativeViewInputs.CreateCameraInputs(checked((ushort)width), checked((ushort)height),
            frame.View, frame.Projection);
        foreach (var item in camera.Matrices) inputs.Matrices[item.Key] = item.Value;
        inputs.Matrices[new(2, 14)] = InvertPrecise(frame.Projection);
        return inputs;
    }

    /** Gauss-Jordan inverse in double precision, rounded to float at the end. */
    internal static Matrix4x4 InvertPrecise(Matrix4x4 m)
    {
        double[,] a =
        {
            { m.M11, m.M12, m.M13, m.M14, 1, 0, 0, 0 }, { m.M21, m.M22, m.M23, m.M24, 0, 1, 0, 0 },
            { m.M31, m.M32, m.M33, m.M34, 0, 0, 1, 0 }, { m.M41, m.M42, m.M43, m.M44, 0, 0, 0, 1 }
        };
        for (int column = 0; column < 4; column++)
        {
            int pivot = column;
            for (int row = column + 1; row < 4; row++)
                if (Math.Abs(a[row, column]) > Math.Abs(a[pivot, column])) pivot = row;
            if (Math.Abs(a[pivot, column]) < 1e-30) throw new ArgumentException("Projection is singular.");
            for (int k = 0; k < 8; k++) (a[column, k], a[pivot, k]) = (a[pivot, k], a[column, k]);
            double scale = a[column, column];
            for (int k = 0; k < 8; k++) a[column, k] /= scale;
            for (int row = 0; row < 4; row++)
            {
                if (row == column || a[row, column] == 0) continue;
                double factor = a[row, column];
                for (int k = 0; k < 8; k++) a[row, k] -= factor * a[column, k];
            }
        }
        return new((float)a[0, 4], (float)a[0, 5], (float)a[0, 6], (float)a[0, 7], (float)a[1, 4], (float)a[1, 5],
            (float)a[1, 6], (float)a[1, 7], (float)a[2, 4], (float)a[2, 5], (float)a[2, 6], (float)a[2, 7],
            (float)a[3, 4], (float)a[3, 5], (float)a[3, 6], (float)a[3, 7]);
    }

    /**
     * Quad depth at which the inverse projection gives W = 0, so the VS outputs a pure world direction.
     * The game's projection reaches infinity at depth one; the viewer's finite far plane needs a little more,
     * and without it CB12 row 7 adds the camera position to the direction.
     */
    internal static float DirectionDepth(Matrix4x4 projection)
    {
        var inverse = InvertPrecise(projection);
        return inverse.M34 != 0 ? -inverse.M44 / inverse.M34 : 1;
    }

    private void UpdateView(NativeAtmosphereFrame frame, int width, int height, float quadDepth = 1)
    {
        if (frame.Camera.Vertex.Length < 9 || frame.Camera.Pixel.Length != 13)
            throw new ArgumentException("Atmosphere passes need nine vertex view rows and thirteen pixel view rows.");
        var vertexRows = new Vector4[10];
        Array.Copy(frame.Camera.Vertex, vertexRows, 9);
        // Row 9 X is the quad clip depth; one is the forward-depth far plane.
        vertexRows[9] = new(quadDepth, 0, 0, 0);
        var pixelRows = (Vector4[])frame.Camera.Pixel.Clone();
        pixelRows[12] = SizeOf(width, height);
        renderer.UpdateNativeConstantBuffer(vertexView, MemoryMarshal.AsBytes(vertexRows.AsSpan()));
        renderer.UpdateNativeConstantBuffer(pixelView, MemoryMarshal.AsBytes(pixelRows.AsSpan()));
    }

    private void Run(NativeAtmosphereFrame frame, Pass pass, TfxInputs inputs, NativeRenderTargets target,
        Dictionary<TfxResource, ID3D11ShaderResourceView> views)
    {
        // Only the lookup reads the interpolated direction; the sky draw must stay at depth one for its test.
        UpdateView(frame, target.Width, target.Height, pass == generate ? DirectionDepth(frame.Projection) : 1);
        inputs.Parameters = parameters;
        var vertex = TfxEvaluator.Evaluate(pass.Execution.Vertex, inputs);
        var pixel = TfxEvaluator.Evaluate(pass.Execution.Pixel, inputs);
        vertex.RequireComplete(); pixel.RequireComplete();
        if (pass.Vertex is not null)
            renderer.UpdateNativeConstantBuffer(pass.Vertex, MemoryMarshal.AsBytes(vertex.Constants.AsSpan()));
        if (pass.Pixel is not null)
            renderer.UpdateNativeConstantBuffer(pass.Pixel, MemoryMarshal.AsBytes(pixel.Constants.AsSpan()));
        pass.Bindings.MaterialEvaluations[GameShaderStage.Vertex] = vertex;
        pass.Bindings.MaterialEvaluations[GameShaderStage.Pixel] = pixel;
        foreach (var sampler in pixel.Samplers)
            if (!pass.Bindings.Samplers.ContainsKey(sampler.Key))
                pass.Bindings.Samplers[sampler.Key] = Own(renderer.Device.CreateSamplerState(sampler.Value.Description));
        pass.Bindings.ApplyTfxResources(pixel, views);
        try { renderer.DrawNativeVertices(pass.Pipeline, pass.Bindings, target, PrimitiveTopology.TriangleStrip, 4); }
        finally
        {
            for (uint slot = 0; slot < 8; slot++) renderer.Context.PSSetShaderResource(slot, null!);
            renderer.RestoreMainTarget();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        minMax?.Dispose(); mask?.Dispose(); maskDownsampled?.Dispose(); radialTemp?.Dispose(); lightShafts?.Dispose();
        for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
        owned.Clear();
    }
}
