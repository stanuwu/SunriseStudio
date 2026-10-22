using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SunriseStudio.Rendering;

/** G-buffer read surfaces, the SSAO target and the camera the ambient pass consumes. */
public sealed record NativeAmbientLightingInputs(GpuTexture Third, GpuTexture NormalRead,
    ID3D11ShaderResourceView DepthRead, GpuTexture Ssao, NativeCameraScopeConstants Camera,
    Matrix4x4 WorldToView, Matrix4x4 Projection, NativeFrameDepthInputs Depth,
    double SimulationSeconds, float ExposureEv, float TimeOfDay = NativeFrameInputs.ViewerTimeOfDay);

public sealed record NativeAmbientLightingCheck(int Checked, int SkyPixels, int LitPixels,
    float MaximumError, float MaximumDiffuse, IReadOnlyList<string> Samples, IReadOnlyList<string> PixelConstants)
{
    /** Lit pixels the sun reached and lit pixels the shadow mask darkened; zero for technique 39. */
    public int SunPixels { get; init; }
    public int ShadowedPixels { get; init; }
    public float MaximumSpecularError { get; init; }
}

/**
 * The per-frame sun inputs technique 37 reads besides the ambient ones.
 * @param Mask The cascade shadow mask, extern 42 resource 0; one is lit.
 * @param UpsampleDepth Extern 42 resource 2, the half-resolution min/max linear depth the mask upsample weighs by.
 * @param SkyDirection Unit direction toward the sun, frame +0x37010, extern 41 vector 3.
 */
public sealed record NativeSunLightingInputs(GpuTexture Mask, GpuTexture UpsampleDepth, Vector3 SkyDirection);

/**
 * Executes technique 39, global_lighting_ambient_only, as lighting_pass draws it.
 * With the sun it executes technique 37, global_lighting: the same ambient terms plus the shadowed sun.
 */
public sealed class NativeAmbientLighting : IDisposable
{
    public const uint MaterialTag = 0x80B357A8;
    public const uint TechniqueIndex = 39;
    public const uint SunMaterialTag = 0x80B357A2;
    public const uint SunTechniqueIndex = 37;
    // lighting_pass writes these pass bytes; the forced blend byte selects the additive state.
    public const uint PassStates = 0x81819081;
    public const uint ForcedStates = 0x82;
    // The native exposure state clamps EV to this range.
    public const float MinimumExposureEv = -3, MaximumExposureEv = 3;
    public const string Policy = "Ambient lighting executes technique 39 with pass bytes 0x81819081 and the forced additive"
        + " blend byte 0x82: blend 2, depth 16 without writes, rasterizer 1. Lighting buffers clear to zero."
        + " Extern 41 uses the authored parameter defaults and the native profile default 0.2;"
        + " runtime profile overrides are not supplied. Extern 1 float 7 is 2^EV with an explicit EV."
        + " SSAO mode 0 supplies a target cleared to one. The material declares no sampler; s0 is point-clamp"
        + " and both sampled textures are read at texel centers. The quad depth row uses forward depth.";
    public const string SunPolicy = "The sun executes technique 37 with the same pass bytes. Extern 41 vector 1 is"
        + " parameter 0x90745795 times 0x615E07A3 X from the authored defaults, vector 3 the time-of-day sky direction."
        + " Extern 42 holds the half-resolution cascade shadow mask, mask mode 1: the shader gathers four mask texels and"
        + " weighs each by its bilinear weight and by how close its min or max depth is to the pixel's depth.";
    // Both techniques share this vertex shader and program.
    private const string VertexDigest = "1A1A21BC9200E1CCBA992ADD8147BF63F6A0597935795A0C1E899AFC16B3339C";
    private const string PixelDigest = "C026D4E225EB238A54E2E728F437AF56C48BE6A19FD0DC0DA14FA3DC0A4806A9";
    private const string SunPixelDigest = "827A66CB1ED088FCCB8476D5553CBDD08BBA025E5DC7113A64AE76093B8BEFDF";
    private const string VertexProgram = "3E020E4428";
    private const string PixelProgram = "3F030B47203F030A47213F030747223F2A0147233D290543333C2924340034013C292403020C4335"
        + "3C2925340234033C292503020C43374E522200433A3C2928433D3D0300434034044E1E22003405090446004E214E2222000346"
        + "014E1F4E2022000346023D2907221A3D2907221A34060E34070B0E3C010721033408034E7E22000343343D2908221A3D290822"
        + "1A34090E340A0B0E3C01072103340B034E7E220003433645004E1D22004500031D0C433C4502221A4502340C0B0E3C01072103"
        + "340D0342340243384501221A4501340E0B0E3C01072103340F034236024339";
    private const string SunPixelProgram = "4C0049213F030B47203F030A47213F030747223F2A0047233F2A0147243F2A0247253D2A02432C"
        + "3C2A0D432E3D290543333C2924340034013C292403020C43353C2925340234033C292503020C43374E522200433A3D2903433F"
        + "3C292843444E53220043454E54220043463D0300434734044E1E22003405090446004E1C4E1B22000346014E214E2222000346"
        + "024E1F4E2022000346033D2907221A3D2907221A34060E34070B0E3C010721033408034E7E22000343343D2908221A3D290822"
        + "1A34090E340A0B0E3C01072103340B034E7E22000343363D2901221A3D2901221A340C0E340D0B0E3C01072103340E034E8322"
        + "0003433E45004E1D22004500031D0C43434503221A4503340F0B0E3C0107210334100342340243384502221A450234110B0E3C"
        + "0107210334120342360243394501221A4501221A34130E34140B0E3C01072103341503423E024341";
    // Targets 128 and 129 use native format 8, which maps to R11G11B10_FLOAT.
    private static readonly NativeColorTargetFormat[] Formats =
    [
        new(Format.R11G11B10_Float, Format.R11G11B10_Float, Format.R11G11B10_Float),
        new(Format.R11G11B10_Float, Format.R11G11B10_Float, Format.R11G11B10_Float)
    ];

    private readonly Dx11Renderer renderer;
    private readonly List<IDisposable> owned = [];
    private readonly MaterialExecutionData execution;
    private readonly NativeMaterialPipeline pipeline;
    private readonly NativeDrawBindings bindings = new();
    private readonly NativeConstantBuffer vertexConstants, pixelConstants, vertexView, pixelView, frame;
    private readonly ID3D11SamplerState sampler;
    private readonly TfxResource thirdToken = new("gbuffer_third", ShaderResourceViewDimension.Texture2D);
    private readonly TfxResource normalToken = new("gbuffer_normal_read", ShaderResourceViewDimension.Texture2D);
    private readonly TfxResource depthToken = new("depth_stencil_read", ShaderResourceViewDimension.Texture2D);
    private readonly TfxResource ssaoToken = new("ssao", ShaderResourceViewDimension.Texture2D);
    private readonly TfxResource maskToken = new("shadow mask", ShaderResourceViewDimension.Texture2D);
    private readonly TfxResource upsampleToken = new("upsample depth", ShaderResourceViewDimension.Texture2D);
    private readonly int pixelRows;
    private NativeSunLightingInputs? lastSun;
    private NativeRenderTargets? targets, validationCopy;
    private bool validationCopied;
    private NativeAmbientLightingInputs? lastInputs;
    private GpuTexture? lastDiffuseBase;
    private double frameSeconds = double.NaN;
    private bool disposed;

    public NativeAmbientEnvironment Environment { get; }
    /** True for technique 37; Render then needs the sun inputs. */
    public bool Sun { get; }
    /** Extern 41 vector 1, the sun colour; zero for technique 39. */
    public Vector4 SunColor { get; }
    /** A viewer light color used in place of SunColor, such as an asset preview's studio key; null uses SunColor. */
    public Vector4? SunColorOverride { get; set; }
    public TfxParameterLookup Parameters { get; }
    public NativeStateSelection States { get; }
    public NativeRenderTargets? Targets => targets;
    public GpuTexture? Diffuse => targets?.Textures[0];
    public GpuTexture? Specular => targets?.Textures[1];
    public Vector4[]? LastPixelConstants { get; private set; }

    /** Bytecode, programs and resolved states are pinned; any change rejects the pass. */
    public NativeAmbientLighting(Dx11Renderer renderer, GameMaterialReader reader,
        TfxParameterDefaults defaults, NativeRenderStateCatalog states, int ssaoMode = 0,
        IReadOnlyDictionary<uint, Vector4>? parameterOverrides = null, bool sun = false)
    {
        this.renderer = renderer;
        Sun = sun;
        pixelRows = sun ? 72 : 65;
        try
        {
            execution = reader.ReadExecution(sun ? SunMaterialTag : MaterialTag);
            var material = execution.Material;
            if (material.VertexShader is null || material.PixelShader is null
                || Convert.ToHexString(SHA256.HashData(reader.ReadShaderBytecode(material.VertexShader))) != VertexDigest
                || Convert.ToHexString(SHA256.HashData(reader.ReadShaderBytecode(material.PixelShader))) != (sun ? SunPixelDigest : PixelDigest))
                throw new NotSupportedException("Ambient lighting bytecode differs from its verified contract.");
            if (Convert.ToHexString(execution.Vertex.TfxBytecode) != VertexProgram
                || Convert.ToHexString(execution.Pixel.TfxBytecode) != (sun ? SunPixelProgram : PixelProgram)
                || execution.Vertex.ConstantTemplate.Length != 44 || execution.Pixel.ConstantTemplate.Length != pixelRows)
                throw new NotSupportedException("Ambient lighting TFX programs differ from their verified contract.");
            // Technique 37 names one sampler, which TFX places at s1.
            if (material.StateOverrides != 0 || material.Textures.Count != 0 || execution.Pixel.Samplers.Count != (sun ? 1 : 0))
                throw new NotSupportedException("Ambient lighting material now carries overrides, textures or samplers.");
            Environment = NativeAmbientEnvironment.FromDefaults(defaults, ssaoMode, parameterOverrides);
            if (sun) SunColor = NativeAmbientEnvironment.ReadSunColor(defaults, parameterOverrides);
            Parameters = defaults.CreateSnapshot(parameterOverrides);
            var resolved = states.Resolve(PassStates, material.StateOverrides, ForcedStates, NativeDepthTable.Alternate);
            States = resolved.Selection;
            if (States != new NativeStateSelection(2, 16, 1, 1) || !resolved.Blend.RenderTarget[0].BlendEnable
                || resolved.DepthStencil.StencilEnable || resolved.DepthStencil.DepthWriteMask != DepthWriteMask.Zero)
                throw new NotSupportedException("Ambient lighting pass states differ from their verified selection.");
            pipeline = Own(renderer.CreateNativePipeline(reader, material, [], resolved.Rasterizer,
                resolved.DepthStencil, resolved.Blend));
            vertexConstants = Own(renderer.CreateNativeConstantBuffer(new byte[44 * 16]));
            pixelConstants = Own(renderer.CreateNativeConstantBuffer(new byte[pixelRows * 16]));
            vertexView = Own(renderer.CreateNativeConstantBuffer(new byte[10 * 16],
                new Dictionary<uint, uint> { [36] = uint.MaxValue, [37] = 0, [38] = 0, [39] = 0 }));
            pixelView = Own(renderer.CreateNativeConstantBuffer(new byte[13 * 16]));
            var initialFrame = CreateFrame(0);
            frame = Own(renderer.CreateNativeConstantBuffer(MemoryMarshal.AsBytes(initialFrame.Vectors.AsSpan()),
                initialFrame.KnownWordMasks));
            frameSeconds = 0;
            sampler = renderer.Device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp));
            owned.Add(sampler);
            bindings.ConstantBuffers[new(GameShaderStage.Vertex, 0)] = vertexConstants;
            bindings.ConstantBuffers[new(GameShaderStage.Pixel, 0)] = pixelConstants;
            bindings.ConstantBuffers[new(GameShaderStage.Vertex, 12)] = vertexView;
            bindings.ConstantBuffers[new(GameShaderStage.Pixel, 12)] = pixelView;
            bindings.ConstantBuffers[new(GameShaderStage.Pixel, 13)] = frame;
            bindings.Samplers[new(GameShaderStage.Pixel, 0)] = sampler;
        }
        catch { Dispose(); throw; }
    }

    private T Own<T>(T item) where T : IDisposable { owned.Add(item); return item; }

    private static NativeFrameScopeConstants CreateFrame(double seconds) => NativeFrameScope.Create(new()
    { SimulationSeconds = seconds, FrameFlag425B8 = false, FrameFlag425B9 = false });

    /** Lighting targets follow the G-buffer extent and test against its depth. */
    public void Resize(NativeRenderTargets depthOwner)
    {
        ArgumentNullException.ThrowIfNull(depthOwner);
        if (targets is not null && ReferenceEquals(targets.Depth, depthOwner.Depth)
            && targets.Width == depthOwner.Width && targets.Height == depthOwner.Height) return;
        var next = renderer.CreateNativeTargetsOnDepth(Formats, depthOwner);
        targets?.Dispose();
        targets = next;
        lastInputs = null;
    }

    /**
     * Every extern the programs read is supplied here; the evaluation refuses anything else.
     * @param sun Required by technique 37 and refused by technique 39.
     */
    public NativeRenderTargets Render(NativeAmbientLightingInputs inputs, NativeRenderTargets depthOwner,
        GpuTexture? diffuseBase = null, NativeSunLightingInputs? sun = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(inputs);
        if (Sun != sun is not null) throw new ArgumentException(Sun ? "Technique 37 needs the sun inputs." : "Technique 39 has no sun.");
        Resize(depthOwner);
        var output = targets!;
        if (inputs.Third.Width != output.Width || inputs.Third.Height != output.Height
            || inputs.NormalRead.Width != output.Width || inputs.NormalRead.Height != output.Height
            || inputs.Ssao.Width != output.Width || inputs.Ssao.Height != output.Height)
            throw new ArgumentException("Ambient lighting inputs must match the lighting target extent.");
        if (!float.IsFinite(inputs.ExposureEv) || inputs.ExposureEv is < MinimumExposureEv or > MaximumExposureEv)
            throw new ArgumentOutOfRangeException(nameof(inputs), "Exposure EV must stay within the native range.");
        if (inputs.Camera.Vertex.Length < 9 || inputs.Camera.Pixel.Length != 13)
            throw new ArgumentException("Ambient lighting needs nine vertex view rows and thirteen pixel view rows.");
        var tfx = NativeFrameInputs.Create(inputs.SimulationSeconds, timeOfDay: inputs.TimeOfDay);
        tfx.Parameters = Parameters;
        tfx.Floats[new(1, 7)] = MathF.Pow(2, inputs.ExposureEv);
        NativeDepthReconstruction.Apply(tfx, inputs.Depth);
        Environment.Apply(tfx);
        var camera = NativeViewInputs.CreateCameraInputs(checked((ushort)output.Width), checked((ushort)output.Height),
            inputs.WorldToView, inputs.Projection);
        foreach (var item in camera.Matrices) tfx.Matrices[item.Key] = item.Value;
        tfx.Resources[new(3, 7)] = depthToken;
        tfx.Resources[new(3, 10)] = normalToken;
        tfx.Resources[new(3, 11)] = thirdToken;
        tfx.Resources[new(42, 1)] = ssaoToken;
        if (sun is not null)
        {
            if (sun.Mask.Width != (output.Width + 1) / 2 || sun.Mask.Height != (output.Height + 1) / 2
                || sun.UpsampleDepth.Width != sun.Mask.Width || sun.UpsampleDepth.Height != sun.Mask.Height)
                throw new ArgumentException("Mask mode 1 reads a half-resolution mask and min/max depth.");
            tfx.Vectors[new(41, 1)] = SunColorOverride ?? SunColor;
            tfx.Vectors[new(41, 3)] = new(Vector3.Normalize(sun.SkyDirection), 0);
            tfx.Resources[new(42, 0)] = maskToken;
            tfx.Resources[new(42, 2)] = upsampleToken;
            // Vector 2 is the mask size; float 13 is the mask mode, profile row 2 +12.
            tfx.Vectors[new(42, 2)] = new(sun.Mask.Width, sun.Mask.Height, 0, 0);
            tfx.Floats[new(42, 13)] = NativeShadowMask.MaskMode;
        }
        var vertex = TfxEvaluator.Evaluate(execution.Vertex, tfx);
        var pixel = TfxEvaluator.Evaluate(execution.Pixel, tfx);
        vertex.RequireComplete(); pixel.RequireComplete();
        if (vertex.Constants.Length != 44 || pixel.Constants.Length != pixelRows)
            throw new InvalidDataException("Ambient lighting TFX output size changed.");
        renderer.UpdateNativeConstantBuffer(vertexConstants, MemoryMarshal.AsBytes(vertex.Constants.AsSpan()));
        renderer.UpdateNativeConstantBuffer(pixelConstants, MemoryMarshal.AsBytes(pixel.Constants.AsSpan()));
        bindings.MaterialEvaluations[GameShaderStage.Vertex] = vertex;
        bindings.MaterialEvaluations[GameShaderStage.Pixel] = pixel;
        var views = new Dictionary<TfxResource, ID3D11ShaderResourceView>
        {
            [thirdToken] = inputs.Third.View,
            [normalToken] = inputs.NormalRead.View,
            [depthToken] = inputs.DepthRead,
            [ssaoToken] = inputs.Ssao.View
        };
        if (sun is not null) { views[maskToken] = sun.Mask.View; views[upsampleToken] = sun.UpsampleDepth.View; }
        bindings.ApplyTfxResources(pixel, views);
        foreach (var item in pixel.Samplers)
            if (!bindings.Samplers.ContainsKey(item.Key))
                bindings.Samplers[item.Key] = Own(renderer.Device.CreateSamplerState(item.Value.Description));
        var vertexRows = new Vector4[10];
        Array.Copy(inputs.Camera.Vertex, vertexRows, 9);
        // Row 9 X is the quad clip depth: one places the quad on the forward-depth far plane.
        vertexRows[9] = new(1, 0, 0, 0);
        renderer.UpdateNativeConstantBuffer(vertexView, MemoryMarshal.AsBytes(vertexRows.AsSpan()));
        renderer.UpdateNativeConstantBuffer(pixelView, MemoryMarshal.AsBytes(inputs.Camera.Pixel.AsSpan()));
        if (frameSeconds != inputs.SimulationSeconds)
        {
            var constants = CreateFrame(inputs.SimulationSeconds);
            renderer.UpdateNativeConstantBuffer(frame, MemoryMarshal.AsBytes(constants.Vectors.AsSpan()));
            frameSeconds = inputs.SimulationSeconds;
        }
        try
        {
            renderer.ClearNativeColorTargets(output, Vector4.Zero);
            if (diffuseBase is not null) renderer.CopyTexture(output.Textures[0], diffuseBase);
            renderer.DrawNativeVertices(pipeline, bindings, output, PrimitiveTopology.TriangleStrip, 4);
        }
        finally
        {
            for (uint slot = 0; slot < 6; slot++) renderer.Context.PSSetShaderResource(slot, null!);
            renderer.RestoreMainTarget();
        }
        LastPixelConstants = pixel.Constants;
        lastInputs = inputs;
        lastDiffuseBase = diffuseBase;
        lastSun = sun;
        validationCopied = false;
        return output;
    }

    /** Later passes add into the targets; Validate then reads this copy of this pass's result. */
    public void KeepValidationCopy()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (targets is null) throw new InvalidOperationException("Ambient lighting has not rendered a frame.");
        if (validationCopy is null || validationCopy.Width != targets.Width || validationCopy.Height != targets.Height
            || !ReferenceEquals(validationCopy.Depth, targets.Depth))
        {
            validationCopy?.Dispose();
            validationCopy = null;
            validationCopy = renderer.CreateNativeTargetsOnDepth(Formats, targets);
        }
        renderer.CopyTexture(validationCopy.Textures[0], targets.Textures[0]);
        renderer.CopyTexture(validationCopy.Textures[1], targets.Textures[1]);
        validationCopied = true;
    }

    /** Sky pixels must stay zero; lit pixels must match the pixel program evaluated on the CPU. */
    public NativeAmbientLightingCheck Validate(NativeRenderTargets depthOwner, int columns = 96, int rows = 64,
        float relativeTolerance = 0.03f, float absoluteTolerance = 0.004f)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (lastInputs is null || targets is null || LastPixelConstants is null)
            throw new InvalidOperationException("Ambient lighting has not rendered a frame.");
        if (columns < 1 || rows < 1) throw new ArgumentOutOfRangeException(nameof(columns));
        var inputs = lastInputs;
        var invProjection = Invert(inputs.Projection);
        var invView = Invert(inputs.WorldToView);
        var frameRow4 = CreateFrame(inputs.SimulationSeconds).Vectors[4];
        var points = new List<(int X, int Y)>();
        for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
                points.Add(((int)((column + 0.5) * targets.Width / columns), (int)((row + 0.5) * targets.Height / rows)));
        // Later passes may have added into the targets; the copy holds this pass's result.
        var read = validationCopied ? validationCopy! : targets;
        var diffuse = renderer.ReadTexturePixels(read.Textures[0], points);
        var basePixels = lastDiffuseBase is null ? new Vector4[points.Count] : renderer.ReadTexturePixels(lastDiffuseBase, points);
        var specular = renderer.ReadTexturePixels(read.Textures[1], points);
        var depths = renderer.ReadNativeDepthPixels(depthOwner, points);
        var third = renderer.ReadTexturePixels(inputs.Third, points);
        var normals = renderer.ReadTexturePixels(inputs.NormalRead, points);
        var ssao = renderer.ReadTexturePixels(inputs.Ssao, points);
        var mask = lastSun is null ? null : UpsampledMask(lastSun, points, depths, targets.Width, targets.Height);
        int sky = 0, lit = 0, sunPixels = 0, shadowed = 0;
        float maximumError = 0, maximumDiffuse = 0, maximumSpecularError = 0;
        var samples = new List<string>();
        for (int i = 0; i < points.Count; i++)
        {
            (int x, int y) = points[i];
            if (lastSun is null && (specular[i].X != 0 || specular[i].Y != 0 || specular[i].Z != 0))
                throw new InvalidOperationException($"Ambient lighting wrote specular at {x},{y}: {specular[i]}.");
            if (!float.IsFinite(diffuse[i].X) || !float.IsFinite(diffuse[i].Y) || !float.IsFinite(diffuse[i].Z))
                throw new InvalidOperationException($"Ambient lighting produced a non-finite value at {x},{y}.");
            float depth = depths[i].X;
            if (depth >= 1)
            {
                sky++;
                if (diffuse[i].X != basePixels[i].X || diffuse[i].Y != basePixels[i].Y || diffuse[i].Z != basePixels[i].Z)
                    throw new InvalidOperationException($"Ambient lighting lit a sky pixel at {x},{y}: {diffuse[i]}.");
                continue;
            }
            lit++;
            var sample = new NativeAmbientLightingReference.Sample(x, y, third[i], normals[i], depth, ssao[i].X);
            Vector3 expected, expectedSpecular = Vector3.Zero;
            if (mask is null)
                expected = NativeAmbientLightingReference.Evaluate(LastPixelConstants, frameRow4,
                    targets.Width, targets.Height, invProjection, invView, sample);
            else
            {
                // Both outputs are linear in the mask, so the mask range gives a segment of allowed results.
                var low = NativeGlobalLightingReference.Evaluate(LastPixelConstants, frameRow4,
                    targets.Width, targets.Height, invProjection, invView, sample, mask[i].Low);
                var high = NativeGlobalLightingReference.Evaluate(LastPixelConstants, frameRow4,
                    targets.Width, targets.Height, invProjection, invView, sample, mask[i].High);
                var actualDiffuse = new Vector3(diffuse[i].X - basePixels[i].X, diffuse[i].Y - basePixels[i].Y, diffuse[i].Z - basePixels[i].Z);
                float t = Along(low.Diffuse, high.Diffuse, actualDiffuse);
                expected = Vector3.Lerp(low.Diffuse, high.Diffuse, t);
                expectedSpecular = Vector3.Lerp(low.Specular, high.Specular, t);
                if (expectedSpecular != Vector3.Zero || expected != NativeGlobalLightingReference.Evaluate(LastPixelConstants,
                    frameRow4, targets.Width, targets.Height, invProjection, invView, sample, 0).Diffuse) sunPixels++;
                if ((mask[i].Low + mask[i].High) * 0.5f < 0.5f) shadowed++;
            }
            expected += new Vector3(basePixels[i].X, basePixels[i].Y, basePixels[i].Z);
            var actual = new Vector3(diffuse[i].X, diffuse[i].Y, diffuse[i].Z);
            float error = Vector3.Distance(actual, expected);
            float allowed = absoluteTolerance + relativeTolerance * expected.Length();
            if (error > allowed)
                throw new InvalidOperationException($"Ambient lighting differs from the pixel program at {x},{y}: {actual},"
                    + $" expected {expected}" + (mask is null ? "." : $" for a mask from {mask[i].Low} to {mask[i].High}."));
            var actualSpecular = new Vector3(specular[i].X, specular[i].Y, specular[i].Z);
            float specularError = Vector3.Distance(actualSpecular, expectedSpecular);
            if (specularError > absoluteTolerance + relativeTolerance * expectedSpecular.Length())
                throw new InvalidOperationException(
                    $"Sun specular differs from the pixel program at {x},{y}: {actualSpecular}, expected {expectedSpecular}.");
            maximumError = MathF.Max(maximumError, error);
            maximumSpecularError = MathF.Max(maximumSpecularError, specularError);
            maximumDiffuse = MathF.Max(maximumDiffuse, actual.Length());
            if (samples.Count < 8)
                samples.Add($"{x},{y} third={sample.Third} normal={sample.Normal} depth={depth} ssao={sample.Ssao}"
                    + (mask is null ? "" : $" mask={mask[i].Low}..{mask[i].High}") + $" expected={expected} actual={actual}");
        }
        var constants = Enumerable.Range(51, LastPixelConstants.Length - 51).Select(row => $"cb0[{row}]={LastPixelConstants[row]}").ToArray();
        return new(points.Count, sky, lit, maximumError, maximumDiffuse, samples, constants)
        { SunPixels = sunPixels, ShadowedPixels = shadowed, MaximumSpecularError = maximumSpecularError };
    }

    /** Where on the segment from a to b the point p projects, clamped to the segment. */
    private static float Along(Vector3 a, Vector3 b, Vector3 p)
    {
        var d = b - a;
        float length = d.LengthSquared();
        return length <= 0 ? 0 : Math.Clamp(Vector3.Dot(p - a, d) / length, 0, 1);
    }

    /**
     * The mask range technique 37 can compute at each point: the mode 1 upsample of the half-resolution mask.
     * Its depth weights resolve differences below one step of a float depth near one, so the range spans the raw
     * depth and its two neighbours, each linearised with and without a fused multiply-add.
     */
    private (float Low, float High)[] UpsampledMask(NativeSunLightingInputs sun, List<(int X, int Y)> points, Vector4[] depths, int width, int height)
    {
        var cb0 = LastPixelConstants!;
        var texels = new List<(int X, int Y)>(points.Count * 4);
        var footprints = new (int I, int J, float Fx, float Fy)[points.Count];
        for (int k = 0; k < points.Count; k++)
        {
            // v2 is the pixel centre; cb12 row 12 ZW turns it into a texture coordinate.
            float u = (points[k].X + 0.5f) / width, v = (points[k].Y + 0.5f) / height;
            float tx = u * cb0[44].X - 0.5f, ty = v * cb0[44].Y - 0.5f;
            int i = (int)MathF.Floor(tx), j = (int)MathF.Floor(ty);
            footprints[k] = (i, j, tx - i, ty - j);
            // gather4 order: (i, j+1), (i+1, j+1), (i+1, j), (i, j); the sampler clamps.
            foreach (var (di, dj) in new[] { (0, 1), (1, 1), (1, 0), (0, 0) })
                texels.Add((Math.Clamp(i + di, 0, sun.Mask.Width - 1), Math.Clamp(j + dj, 0, sun.Mask.Height - 1)));
        }
        var maskTexels = renderer.ReadTexturePixels(sun.Mask, texels);
        var depthTexels = renderer.ReadTexturePixels(sun.UpsampleDepth, texels);
        var result = new (float, float)[points.Count];
        for (int k = 0; k < points.Count; k++)
        {
            var (_, _, fx, fy) = footprints[k];
            float low = float.MaxValue, high = float.MinValue;
            foreach (float raw in new[] { depths[k].X, MathF.BitDecrement(depths[k].X), MathF.BitIncrement(depths[k].X) })
                foreach (float linear in new[] { 1 / (raw * cb0[71].Y + cb0[71].X), 1 / MathF.FusedMultiplyAdd(raw, cb0[71].Y, cb0[71].X) })
                {
                    float value = NativeGlobalLightingReference.UpsampleMask(cb0, linear, fx, fy,
                        maskTexels.AsSpan(k * 4, 4), depthTexels.AsSpan(k * 4, 4));
                    low = MathF.Min(low, value); high = MathF.Max(high, value);
                }
            result[k] = (low, high);
        }
        return result;
    }

    private static Matrix4x4 Invert(Matrix4x4 matrix) =>
        Matrix4x4.Invert(matrix, out var inverse) ? inverse
            : throw new ArgumentException("Ambient lighting camera matrix is singular.");

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        targets?.Dispose(); validationCopy?.Dispose();
        for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
        owned.Clear();
    }
}

/** A CPU port of pixel shader 0x80B357A6, instruction for instruction. */
public static class NativeAmbientLightingReference
{
    public sealed record Sample(int X, int Y, Vector4 Third, Vector4 Normal, float Depth, float Ssao);

    public static Vector3 Evaluate(Vector4[] cb0, Vector4 cb13Row4, int width, int height,
        Matrix4x4 inverseProjection, Matrix4x4 inverseView, Sample sample)
    {
        ArgumentNullException.ThrowIfNull(cb0);
        if (cb0.Length < 65) throw new ArgumentException("The pixel program needs 65 constant rows.");
        float r0x = cb0[61].X - 1;
        float r0y = MathF.Max(Saturate(0.8f * cb13Row4.W), 0.0001f);
        float r1x = sample.Third.Y, r1y = Saturate(sample.Third.Z * 0.8f), r1z = sample.Third.W;
        r0y = MathF.Min(r1y / r0y, 1);
        bool occluded = 0.0001f < r1y;
        r0x = r0y * r0x + 1;
        float ssao = sample.Ssao;
        r0x = 1 - Saturate(r0x * (1 - ssao));
        r0x = occluded ? r0x : ssao;
        float gloss = Saturate(r1x + r1x) * r1z;
        gloss = MathF.Pow(MathF.Max(gloss * gloss, 0.0001f), cb0[58].X);
        r0x *= gloss;
        var normal = Vector3.Normalize(new Vector3(sample.Normal.X, sample.Normal.Y, sample.Normal.Z) * 2 - Vector3.One);
        float linearDepth = 1 / (sample.Depth * cb0[64].Y + cb0[64].X);
        var clip = new Vector4((sample.X + 0.5f) / width * 2 - 1, 1 - (sample.Y + 0.5f) / height * 2, 1, 1);
        var world = Vector4.Transform(Vector4.Transform(clip, inverseProjection), inverseView);
        float distance = linearDepth * MathF.Sqrt(world.X * world.X + world.Y * world.Y);
        float fade = Saturate(distance * cb0[60].X + cb0[60].Y);
        float cosine = Vector3.Dot(normal, Xyz(cb0[51]));
        float down = Saturate(-cosine * cb0[55].X + cb0[55].Y);
        float up = Saturate(cosine * cb0[53].X + cb0[53].Y);
        up *= up; down *= down;
        var lower = fade * Xyz(cb0[57]) + Xyz(cb0[54]);
        var upper = fade * Xyz(cb0[56]) + Xyz(cb0[52]);
        return r0x * (up * upper + down * lower);
    }

    private static float Saturate(float value) => Math.Clamp(value, 0, 1);
    private static Vector3 Xyz(Vector4 value) => new(value.X, value.Y, value.Z);
}

/** A CPU port of pixel shader 0x80B357A1, technique 37. */
public static class NativeGlobalLightingReference
{
    /**
     * The mask mode 1 branch (cb0[46] X above 0.5): four gathered texels, each weighed by its bilinear weight and
     * by 0.001 / (|depth - its min or max depth| + 0.001), cb0[49] X.
     * @param mask Gathered mask texels in gather4 order: (i, j+1), (i+1, j+1), (i+1, j), (i, j).
     * @param minMax The min/max linear depth texels in the same order.
     * @param fx Fraction of the texel coordinate across; fy down.
     */
    public static float UpsampleMask(Vector4[] cb0, float linearDepth, float fx, float fy,
        ReadOnlySpan<Vector4> mask, ReadOnlySpan<Vector4> minMax)
    {
        if (cb0[46].X <= 0.5f) throw new NotSupportedException("The upsample runs only in mask mode 1.");
        Span<float> bilinear = [fy * (1 - fx), fy * fx, fx * (1 - fy), (1 - fy) * (1 - fx)];
        float sum = 0, weights = 0, epsilon = cb0[49].X;
        for (int k = 0; k < 4; k++)
        {
            float low = bilinear[k] * (epsilon / (MathF.Abs(linearDepth - minMax[k].X) + epsilon));
            float high = bilinear[k] * (epsilon / (MathF.Abs(linearDepth - minMax[k].Y) + epsilon));
            sum += mask[k].X * low + mask[k].Y * high;
            weights += low + high;
        }
        return sum / weights;
    }

    /**
     * @param mask The point-sampled mask in mode 0, or UpsampleMask's result in mode 1.
     * @return The diffuse target o0 and the specular target o1.
     */
    public static (Vector3 Diffuse, Vector3 Specular) Evaluate(Vector4[] cb0, Vector4 cb13Row4, int width, int height,
        Matrix4x4 inverseProjection, Matrix4x4 inverseView, NativeAmbientLightingReference.Sample sample, float mask)
    {
        ArgumentNullException.ThrowIfNull(cb0);
        if (cb0.Length < 72) throw new ArgumentException("The pixel program needs 72 constant rows.");
        var raw = new Vector3(sample.Normal.X, sample.Normal.Y, sample.Normal.Z) * 2 - Vector3.One;
        float normalLength = raw.Length();
        var n = raw / normalLength;
        float linearDepth = 1 / (sample.Depth * cb0[71].Y + cb0[71].X);
        var clip = new Vector4((sample.X + 0.5f) / width * 2 - 1, 1 - (sample.Y + 0.5f) / height * 2, 1, 1);
        var ray = Vector4.Transform(Vector4.Transform(clip, inverseProjection), inverseView);
        var toPoint = linearDepth * new Vector3(ray.X, ray.Y, ray.Z);
        var third = sample.Third;
        float doubled = Saturate(third.Y + third.Y);
        float subsurface = Saturate(third.Z * 0.8f);
        float occlusion = doubled * third.W; occlusion *= occlusion;
        var light = Xyz(cb0[63]);
        float nDotL = Vector3.Dot(n, light);
        // A subsurface pixel wraps the terminator; the shadow term fades in over two percent.
        float terminator = MathF.Min(subsurface * 20, 1);
        terminator = MathF.Min(Saturate(terminator * (1 - nDotL) + nDotL - 0.02f) * 10, 1);
        float shadow = mask * terminator;
        float ssao = sample.Ssao;
        float lower = MathF.Max(Saturate(0.8f * cb13Row4.W), 0.0001f);
        float ao = MathF.Min(subsurface / lower, 1);
        ao = 1 - Saturate((ao * (cb0[68].X - 1) + 1) * (1 - ssao));
        ao = 0.0001f < subsurface ? ao : ssao;
        float fade = Saturate(MathF.Sqrt(toPoint.X * toPoint.X + toPoint.Y * toPoint.Y) * cb0[67].X + cb0[67].Y);
        float cosine = Vector3.Dot(n, Xyz(cb0[51]));
        float up = Saturate(cosine * cb0[53].X + cb0[53].Y); up *= up;
        float down = Saturate(-cosine * cb0[55].X + cb0[55].Y); down *= down;
        var upper = fade * Xyz(cb0[56]) + Xyz(cb0[52]);
        var lowerColor = fade * Xyz(cb0[57]) + Xyz(cb0[54]);
        ao *= MathF.Pow(MathF.Max(occlusion, 0.0001f), cb0[58].X);
        var ambient = up * upper + down * lowerColor;
        if (!(1e-5f < shadow || 0.001f < subsurface)) return (ambient * ao, Vector3.Zero);
        float smoothness = normalLength * 4 - 3;
        float specularOcclusion = MathF.Pow(MathF.Max(third.W, 0.0001f), cb0[69].X)
            * MathF.Pow(MathF.Max(doubled, 0.0001f), cb0[70].X);
        specularOcclusion *= specularOcclusion;
        var view = toPoint / toPoint.Length();
        float clampedNDotL = Saturate(nDotL);
        float wrapped = MathF.Min(MathF.Min(subsurface, 0.2f) + clampedNDotL, 1);
        var half = Vector3.Normalize(light - view);
        float nDotH = Saturate(Vector3.Dot(n, half));
        float nDotV = MathF.Abs(Vector3.Dot(n, -view)) + 1e-5f;
        float lDotH = Saturate(Vector3.Dot(light, half));
        float rough = -smoothness * 0.9f + 1;
        float alpha2 = rough * rough; alpha2 *= alpha2;
        float oneMinus = 1 - lDotH;
        float fifth = oneMinus * oneMinus; fifth *= fifth; fifth *= oneMinus;
        float fresnel = 1 / (third.X * 0.84f + 0.04f) * fifth + (1 - fifth);
        float visibility = MathF.Sqrt((-nDotV * alpha2 + nDotV) * nDotV + alpha2) + clampedNDotL;
        visibility += MathF.Sqrt((-clampedNDotL * alpha2 + clampedNDotL) * clampedNDotL + alpha2) + nDotV;
        visibility = MathF.Min(0.5f / visibility + Saturate(smoothness * -0.5f), 1);
        float distribution = (nDotH * alpha2 - nDotH) * nDotH + 1;
        distribution = alpha2 / (distribution * distribution);
        float specular = visibility * distribution * fresnel;
        float retro = 2 * lDotH * lDotH * rough - 0.5f;
        float lightFifth = Fifth(1 - clampedNDotL), viewFifth = Fifth(1 - (nDotV));
        float diffuse = (retro * viewFifth + 1) * (retro * lightFifth + 1);
        float specularLit = specularOcclusion * (clampedNDotL * specular);
        float back = Saturate(Vector3.Dot(light, -n));
        float forward = MathF.Pow(Saturate(Vector3.Dot(light, view)), cb13Row4.Y);
        float transmission = 0.0001f < subsurface ? subsurface * Saturate(back * cb13Row4.X + forward) : 0;
        float diffuseLit = wrapped * diffuse * 0.31831f + transmission;
        var sun = (fade * Xyz(cb0[65]) + Xyz(cb0[62])) * shadow;
        return (ambient * ao + sun * (specularOcclusion * diffuseLit), sun * (specularLit * 0.31831f) * cb0[61].X);
    }

    private static float Fifth(float value) { float square = value * value; return square * square * value; }
    private static float Saturate(float value) => Math.Clamp(value, 0, 1);
    private static Vector3 Xyz(Vector4 value) => new(value.X, value.Y, value.Z);
}
