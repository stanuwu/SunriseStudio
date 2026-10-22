using System.Numerics;
using System.Runtime.InteropServices;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SunriseStudio.Viewport;

public sealed record NativeSceneFrameStats(int DrawnParts, int DrawCalls, long Triangles, int FailedParts,
    IReadOnlyDictionary<string, int> StageParts, int MaterialEvaluations);

/** Resource sharing follows original buffer, material, sampler, and texture identities. */
public sealed partial class NativeSceneRenderer : IDisposable
{
    private sealed class MaterialGpu
    {
        public required NativeSceneMaterial Source { get; init; }
        public NativeDrawBindings Bindings { get; } = new();
        public Dictionary<GameShaderStage, Vector4[]> Constants { get; } = [];
        public double LastTime { get; set; } = double.NaN;
        // True when neither TFX program reads an extern, so its output never changes.
        public bool Static { get; set; }
        // True when TFX reads the camera, so it runs every frame.
        public bool UsesCamera { get; set; }
        // True when TFX reads extern 1 float 7 or 8, so an exposure change runs it again.
        public bool UsesExposure { get; set; }
        public float LastExposureEv { get; set; } = float.NaN;
        // Water object position and scale for extern 67; one per material.
        public (Vector3 Position, float Scale)? WaterObject { get; set; }
        // Scope 14 CB8 reads the centre and vertical endpoints of one object's bounds.
        public NativeConstantBuffer? Fog { get; set; }
        public (Vector3 Minimum, Vector3 Maximum) FogBounds { get; set; }
        // A decorator material's payload, type and mesh, and its scope 8 buffer.
        public NativeDecoratorPart? Decorator { get; set; }
        public DecoratorScope? DecoratorScope { get; set; }
        // Vertex and pixel programs for evaluations shared within one frame.
        public TfxShare VertexShare { get; set; } = TfxShare.None;
        public TfxShare PixelShare { get; set; } = TfxShare.None;
        // Id of Source.ObjectChannels for shared evaluations; 0 is no table.
        public int ChannelTable { get; set; }
        // The extern view version and stage evaluations ApplyExternResources last bound.
        public int ExternVersion { get; set; } = -1;
        public TfxEvaluation? ExternVertex { get; set; }
        public TfxEvaluation? ExternPixel { get; set; }
    }
    /** Scope 8 rows depend only on the type, the mesh and the frame's wind, so one buffer serves them all. */
    private sealed class DecoratorScope(NativeConstantBuffer buffer, NativeDecoratorPart part)
    {
        public NativeConstantBuffer Buffer { get; } = buffer;
        public NativeDecoratorPart Part { get; } = part;
        // The decorator time whose wind the buffer holds.
        public double Time { get; set; } = double.NaN;
    }
    private sealed class DrawGroup(NativeSceneGroup source, NativeMaterialPipeline pipeline, NativeGpuMesh mesh,
        MaterialGpu material, NativeBufferResource? scalars, int pass, ID3D11ShaderResourceView?[]? dyes)
    {
        public NativeSceneGroup Source { get; } = source;
        public NativeMaterialPipeline Pipeline { get; } = pipeline;
        public NativeGpuMesh Mesh { get; } = mesh;
        public MaterialGpu Material { get; } = material;
        public NativeBufferResource? Scalars { get; } = scalars;
        public int Pass { get; } = pass;
        public int Order { get; set; }
        public int FirstHandle { get; set; }
        public bool Runtime { get; init; }
        public NativeDecoratorPart? Decorator { get; } = source.Parts[0].Decorator;
        private NativeDecoratorPayload[]? payloads;
        /** The distinct payloads of a decorator group's parts; empty for any other group. */
        public NativeDecoratorPayload[] Payloads => payloads ??= Source.Parts.Select(part => part.Decorator?.Payload)
            .OfType<NativeDecoratorPayload>().Distinct().ToArray();
        private int[]? cellStarts;
        private bool cellStartsRead;
        /**
         * Each part's cell start, for NativeDecoratorPayload.DrawsCell; null unless every part shares the payload
         * and the levels of Decorator.
         */
        public int[]? CellStarts => cellStartsRead || Decorator is null ? cellStarts : ReadCellStarts(Decorator);

        // Apart from the getter, so a read of the cached value allocates no lambda closure.
        private int[]? ReadCellStarts(NativeDecoratorPart first)
        {
            cellStartsRead = true;
            if (Source.Parts.All(part => part.Decorator is { } d && ReferenceEquals(d.Payload, first.Payload)
                && d.FirstLevel == first.FirstLevel && d.LastLevel == first.LastLevel))
                cellStarts = Source.Parts.Select(part => first.Payload.CellStarts[first.Payload.RecordCell[part.Decorator!.Record]]).ToArray();
            return cellStarts;
        }
        // Live slots and decorators record their draws again every frame.
        public bool PerFrame => Runtime || Decorator is not null;
        // Static parts share a draw 16 at a time; terrain and rigid-model parts one at a time; decorators many.
        public int PerDraw { get; } = source.Parts[0].Decorator is not null ? DecoratorsPerDraw
            : source.Kind == NativeMeshKind.Static ? InstancesPerDraw : 1;
        public int Stride { get; set; } = source.Parts[0].Decorator is not null ? NativeDecorators.InstanceStreams
            : source.Material.Palette != NativeEntityPalette.None ? NativeEntityPalettes.Rows
            : source.Kind == NativeMeshKind.Entity ? 8 : 4;
        // CB11 vectors per part, contiguous so recording reads memory in order; SetEntityPose replaces them.
        public Vector4[] Instances { get; set; } = source.Parts.SelectMany(p =>
            p.Constants.Concat(Enumerable.Repeat(Vector4.Zero, NativeEntityPalettes.Rows))
                .Take(source.Material.Palette != NativeEntityPalette.None ? NativeEntityPalettes.Rows : source.Kind == NativeMeshKind.Entity ? 8 : 4)).ToArray();
        public Vector3[] Centers { get; } = source.Parts.Select(part => part.Center).ToArray();
        // Terrain group dye textures per part; null entries unbind pixel t14.
        public ID3D11ShaderResourceView?[]? Dyes { get; } = dyes;
    }

    // Draw-list passes: G-buffer, G-buffer decals, lit-color stages, and sky parts drawn before them.
    private const int PassGBuffer = 0, PassDecals = 1, PassForward = 2, PassSky = 3;
    private const int InstancesPerDraw = 16;
    // Three instance vectors each keep 1024 decorators inside the 4096-vector draw window.
    private const int DecoratorsPerDraw = 1024;
    private readonly List<MaterialGpu> decoratorMaterials = [];
    // Decorator types by tag in first-use order; the wind state is per tag too.
    private readonly List<NativeDecoratorType> decoratorTypes = [];
    private readonly Dictionary<uint, Vector4[]> decoratorRows = [];
    private readonly Dictionary<(uint Type, int Mesh), DecoratorScope> decoratorScopes = [];
    private readonly Dictionary<uint, SpeedTreeWind> decoratorWinds = [];
    private double decoratorTime = double.NaN;
    private Vector3 decoratorCamera;
    private NativeConstantBuffer? decoratorLodScope;
    /** foliage_detail 0 to 2 for the decorator distances; the game's default is 1. */
    public int FoliageDetail { get; set; } = SpeedTreeLod.DefaultFoliageDetail;
    /** Decorator instances the last frame drew, by LOD level. */
    public IReadOnlyList<int> DecoratorLodCounts => decoratorLodCounts;
    private readonly int[] decoratorLodCounts = new int[5];
    /** Why the decorator wind uses the zero block the game holds before its settings load; null if it does not. */
    public string? DecoratorWindFallback { get; private set; }
    private readonly Dx11Renderer renderer;
    private readonly Dictionary<NativeMeshKind, NativeConstantBuffer> instanceConstants;
    private readonly GpuTexture dyePlaceholder;
    private readonly HashSet<uint> badTextures = [];
    private readonly List<IDisposable> owned = [];
    private readonly List<DrawGroup> groups = [];
    private readonly List<(DrawGroup Group, int Part)> handles = [];
    private readonly Dictionary<MeshInstance, HashSet<DrawGroup>> instanceGroups = [];
    private Func<MeshInstance, IReadOnlyList<Matrix4x4>?>? entitySkin;
    // One entry per uploaded cloth display geometry; SetEntityPose rewrites its stream 0.
    private readonly List<(NativeSceneGroup Source, NativeGpuGeometry Geometry)> clothGeometry = [];
    private readonly Dictionary<uint, GpuTexture> textures = [];
    private readonly Dictionary<uint, ID3D11SamplerState> samplers = [];
    private readonly Dictionary<uint, NativeBufferResource> scalarBuffers = [];
    // Keyed by every input that changes a material's bindings: water object (extern 67), fog
    // bounds, forward frame scope, gear-dye bit, gear inputs, stencil reference, shadow flag with
    // decorator payload, type and mesh, and the object channel table.
    private readonly Dictionary<(uint Tag, (Vector3, float)? Water, (Vector3, Vector3)? Fog, bool Forward, int Dye, string Gear,
        uint? Stencil, string Decorator, string Channels), MaterialGpu> materials = [];
    // Object channel tables by content key; ids start at 1.
    private readonly Dictionary<string, int> channelIds = [];
    private readonly Dictionary<string, NativeGpuGeometry> geometry = [];
    private readonly Dictionary<string, NativeMaterialPipeline> pipelines = [];
    private readonly Dictionary<string, int> gpuSkips = [];
    private readonly List<string> diagnostics = [];
    private readonly NativeConstantBuffer viewConstants;
    private readonly NativeConstantBuffer pixelViewConstants;
    private readonly NativeConstantBuffer frameConstants;
    // Frame scope of the lit-colour forward stages; row 1 X is their exposure, see Render.
    private readonly NativeConstantBuffer forwardFrameConstants;
    // Transparent scope CB2: vector 0 gives 1 / view depth = depth * y + x; vectors 1 to 5 are extern 39.
    private readonly NativeConstantBuffer transparentConstants;
    // Sky parts draw only over pixels without geometry, so their scene depth reads as 1e30 away.
    private readonly NativeConstantBuffer skyScopeConstants;
    // Absent scope targets read as zero, the same as the native null binding.
    private readonly GpuTexture absentTexture, absentVolume;
    private readonly List<MaterialGpu> depthReaders = [], lookupReaders = [], externReaders = [], fogReaders = [];
    // Set when a decal material reads the normals copy; the frame then takes the copy before the decal pass.
    private bool readsPreDecalNormals;
    // Resolved depth of this frame, bound where TFX picks extern 3 resource 7; null before the first resolve.
    private ID3D11ShaderResourceView? sceneDepth;
    // Water stand-ins: no fog at scope t16, unshadowed, flat ripples. See NativeWater.Policy.
    private readonly GpuTexture noFog, unshadowed, flatRipples;
    private NativeRenderTargets refraction = null!, forwardColor = null!;
    private GpuTexture screenUv = null!;
    // Screen-space atmosphere lookup, bound at pixel t11 for sky parts as extern 39 resource 0.
    private NativeRenderTargets skyLookup = null!;
    // Fallback only: sky domes take about 90% of their colour from the lookup, so a brighter stand-in hides dome rims.
    public const float DefaultAtmosphereFill = 16;
    /** Fallback brightness of the viewer gradient that stands in for the lookup on maps with no atmosphere. */
    public float AtmosphereFill { get; set; } = DefaultAtmosphereFill;
    private NativeAtmosphere? atmosphere;
    private readonly Dictionary<uint, GpuTexture> atmosphereLookups = [];
    /** Index into Plan.Atmospheres that drives the sky; -1 selects the viewer gradient fallback. */
    public int AtmosphereIndex { get; set; } = -1;
    /** Command-line choice applied to every new scene; null keeps the default selection. */
    public static int? AtmosphereOverride { get; set; }
    public string? AtmosphereError { get; private set; }
    /** True when the last frame drew the sky from placed atmosphere data. */
    public bool AtmosphereActive { get; private set; }
    public float AtmosphereKeyframe => atmosphere?.LastKeyframe ?? float.NaN;
    /** Time-of-day directions of the last frame: key 0x5C579DFA (sky) and 0xC09AC1A6 (light, frame +0x37020). */
    public Vector3 SkyDirection { get; private set; } = Vector3.UnitZ;
    public Vector3 LightDirection { get; private set; } = Vector3.UnitZ;
    /** True when the atmosphere's list has no time-of-day curves and the light is the +Z default. */
    public bool TimeOfDayFallback { get; private set; }
    public Vector2 LightScreen => atmosphere?.LastLightScreen ?? default;
    /** Reads the light-shaft target at UVs; call after Render. */
    public Vector4[] SampleLightShafts(IReadOnlyList<Vector2> uvs)
    {
        if (atmosphere?.LightShafts is not { } shafts) return uvs.Select(_ => new Vector4(float.NaN)).ToArray();
        return renderer.ReadTexturePixels(shafts, uvs.Select(uv => ((int)Math.Clamp(uv.X * shafts.Width, 0, shafts.Width - 1),
            (int)Math.Clamp(uv.Y * shafts.Height, 0, shafts.Height - 1))).ToArray());
    }
    private readonly NativeDrawList drawList;
    private readonly NativeDrawList runtimeDrawList;
    private readonly NativeDrawList forwardDrawList;
    private readonly List<(float Distance, DrawGroup Group, int Part)> sorted = [];
    private static readonly Comparison<(float Distance, DrawGroup Group, int Part)> FarToNear =
        (a, b) => b.Distance.CompareTo(a.Distance);
    private Vector3 sortedEye;
    private int sortedCentersRevision, centersRevision;
    private bool sortedValid;
    private readonly List<DrawGroup> activeGroups = [];
    private readonly List<int> activeOrders = [];
    private int[] handleOrders = [], groupStages = [], groupStarts = [];
    // DrawGroup.PerFrame of each handle's group.
    private bool[] handlePerFrame = [];
    private int[] visibleGroupCounts = [], visibleGroupOffsets = [], visibleGroupParts = [];
    private bool[] failedGroups = [];
    /** A group's fixed draw inputs, by order, so a frame lays its draws out without visiting each group. */
    private readonly record struct GroupDraw(NativeMaterialPipeline Pipeline, NativeGpuMesh Mesh, NativeDrawBindings Bindings,
        ID3D11ShaderResourceView? Scalars, ID3D11ShaderResourceView?[]? Dyes, int Pass, int PerDraw, bool PerFrame, bool Water);
    private GroupDraw[] groupDraws = [];
    private readonly List<int> recordedVisible = [];
    private readonly HashSet<int> recordedStages = [];
    private bool hasRecordedVisibility, previousOpaqueFailure, opaqueUploadValid;
    private int poseRevision, recordedPoseRevision = -1;
    public bool CacheOpaqueDraws { get; set; } = true;
    public int OpaqueRecordings { get; private set; }
    private readonly int[] forwardParts = new int[InstancesPerDraw];
    private readonly Dictionary<uint, Vector4> ambientOverrides;
    private double frameTime = double.NaN;
    private float frameEv = float.NaN;
    private NativeOutputDisplay display = null!;
    private NativeGBufferTargets gbuffer = null!;
    private NativeRenderTargets sceneColor = null!, exposedColor = null!, skyColor = null!;
    // The sky layer skips the viewer gain, which is fitted to dim ambient-lit geometry; its own gain aims its
    // mean at SkyTarget, at most SkyRangeStops from SkyLayerScale.
    private const float SkyLayerScale = 5, SkyTarget = 0.25f, SkyRangeStops = 3;
    private NativeSceneComposite skyMeter = null!;
    private float skyLog = float.NaN;
    private bool skyMeasured;
    private float viewerLog = float.NaN;
    private bool exposureWarmupRequested;
    private readonly NativeRenderStateCatalog states = new();
    private NativeAmbientLighting? ambient;
    private NativeSkyLighting? skyLighting;
    private NativeCubemapLighting? localCubemaps;
    private readonly List<NativeCubemapDraw> localCubemapDraws = [];
    private NativeAtmosphereFrame? atmosphereFrame;
    private GpuTexture? studioReflection;
    /** Studio radiance is an editor reference level, independent of map atmosphere. */
    private const float StudioEnvironmentRadiance = .18f;
    // Asset previews reflect a studio and take a key light that follow the camera; maps keep the flat level.
    private NativeStudioEnvironment? studioEnvironment;
    private NativeRenderTargets? antialiased;
    // The key in camera terms: right, up and toward the eye.
    private static readonly Vector3 StudioKeyView = Vector3.Normalize(new(-.35f, .55f, .76f));
    // Key light color in the sun's units, and the native ambient strength under it: the key models the form and the
    // ambient keeps the shaded side readable.
    private static readonly Vector4 StudioKeyColor = new(StudioKeyIntensity, StudioKeyIntensity, StudioKeyIntensity, 1);
    private const float StudioKeyIntensity = 16, AssetAmbientIntensity = .5f;
    // Asset previews hold one exposure, so a black weapon stays black instead of metering up to middle gray. The value
    // is where the meter settled for light weapons under the studio key.
    private const float AssetViewerStops = 1.5f;
    // An asset's backdrop is one dark blue-gray, about as bright as the viewer gradient, so the sky meter and the
    // transparents it exposes read as they do over the gradient. The gain alone darkens it; through the film curve it
    // shows as sRGB 24, 28, 35.
    private static readonly Vector3 AssetBackdrop = new Vector3(.85f, 1, 1.35f) * .045f;
    private const float AssetBackdropGain = .13f;
    /** An asset preview draws its flat backdrop; off draws the viewer gradient behind it, as maps have. */
    public bool FlatBackdrop { get; set; }
    private bool AssetBackdropActive => Plan.AssetPreview && FlatBackdrop;
    private Vector3 studioKey = Vector3.UnitZ;
    private bool StudioKeyActive => Plan.AssetPreview && LightingMode == NativeLightingMode.Studio;
    private NativeLightingMode lightingMode = NativeLightingMode.Studio;
    /** A lighting change meters the new mode once before displaying it. */
    public NativeLightingMode LightingMode
    {
        get => lightingMode;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (lightingMode == value) return;
            lightingMode = value;
            // An asset preview holds its exposure only under its studio key; other lighting meters as maps do.
            if (Plan.AssetPreview) AutoExposure = value != NativeLightingMode.Studio;
            RequestExposureWarmup();
        }
    }
    public bool SkyLightingEnabled
    {
        get => lightingMode == NativeLightingMode.Atmosphere;
        set => LightingMode = value ? NativeLightingMode.Atmosphere : NativeLightingMode.Ambient;
    }
    public bool SkyLightingActive { get; private set; }
    public bool StudioLightingActive { get; private set; }
    public bool LocalCubemapsEnabled { get; set; } = true;
    public bool LocalCubemapsActive { get; private set; }
    public string? LocalCubemapError { get; private set; }
    public int LocalCubemapCount => localCubemapDraws.Count;
    public int LocalCubemapDrawCount => LocalCubemapsActive ? localCubemaps?.LastDrawCount ?? 0 : 0;
    private bool EnvironmentLightingActive => SkyLightingActive || StudioLightingActive;
    public string? SkyLightingError { get; private set; }
    public GpuTexture? IblLighting => LocalCubemapsActive ? localCubemaps?.Ibl : EnvironmentLightingActive ? skyLighting?.Ibl : null;
    public GpuTexture? SkyReflection => SkyLightingActive ? skyLighting?.Reflection : null;
    private NativeDeferredShading? deferred;
    /** Technique 0 uses placed fog in Atmosphere mode and neutral fog in Studio mode. */
    public bool DeferredShadingEnabled { get; set; } = true;
    public bool DeferredActive { get; private set; }
    public string? DeferredError { get; private set; }
    private NativeRenderTargets? ssao;
    private NativeFinalCombine? finalCombine;
    private readonly NativeSceneComposite composite;
    private NativeCameraScopeConstants? cameraScope;
    private Matrix4x4 cameraView, cameraProjection;
    private bool disposed;

    public const uint AmbientIntensityKey = 0xE638AAEB;
    public static readonly IReadOnlyList<string> OutputNames =
        ["gbuffer_albedo", "gbuffer_normal", "gbuffer_third", "lighting_diffuse", "lighting_specular", "final_combine", "scene_color"];
    public const int FinalOutput = 5;
    public NativeScenePlan Plan { get; }
    public NativeRenderTargets Targets => gbuffer.WriteTargets;
    public NativeSceneFrameStats LastFrame { get; private set; } = new(0, 0, 0, 0, new Dictionary<string, int>(), 0);
    private int[] lastStageCounts = [];
    private IReadOnlyDictionary<string, int> lastStageParts = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> GpuSkippedReasons => gpuSkips;
    public IReadOnlyList<string> Diagnostics => diagnostics;
    public IReadOnlyList<int> RenderStages => groups.Select(g => g.Source.RenderStage).Distinct().Order().ToArray();
    public ISet<int> EnabledStages { get; } = new HashSet<int>(NativeScenePlan.DrawnStages);
    // One handle per part of every group.
    public int ReadyPartCount => handles.Count;
    public int ReadyMaterialCount => materials.Count;
    /** Water objects ready to draw: material, position, scale and part count. */
    public IReadOnlyList<(uint Material, Vector3 Position, float Scale, int Parts)> WaterObjects =>
        groups.Where(g => g.Material.WaterObject is not null)
            .GroupBy(g => (g.Source.Mesh.MaterialTag, g.Material.WaterObject!.Value))
            .Select(g => (g.Key.MaterialTag, g.Key.Value.Position, g.Key.Value.Scale, g.Sum(x => x.Source.Parts.Count))).ToArray();
    /** Water parts the last frame drew. */
    public int LastWaterParts { get; private set; }
    public int TextureCount => textures.Count;
    public int GeometryCount => geometry.Count;
    public int ScalarBufferCount => scalarBuffers.Count;
    public bool UsesConstantOffsets => drawList.UsesOffsets;
    public string PassPolicy => NativeScenePlan.PassPolicy
        + " Native G-buffer formats and separate normal/depth read copies are used; conditional G-buffer modification is not evaluated."
        + (LightingEnabled ? " " + NativeAmbientLighting.Policy : "")
        + (SunActive ? " " + NativeAmbientLighting.SunPolicy : "")
        + (ShadowsActive ? " " + NativeShadowMask.Policy : "")
        + (AtmosphereActive ? " " + NativeAtmosphere.Policy
            : " Sky fallback: a labelled viewer gradient stands in for the atmosphere lookup; the map has no usable placed atmosphere.")
        + (SkyLightingActive ? " " + NativeSkyLighting.Policy : "")
        + (StudioLightingActive ? Plan.AssetPreview ? " " + NativeStudioEnvironment.Policy
            : " Studio lighting uses a neutral inspection environment with no atmospheric fog." : "")
        + (DeferredActive ? " " + NativeDeferredShading.Policy : " The viewer composite uses diffuse lighting.")
        + $" Ambient intensity 0x{AmbientIntensityKey:X8} = {AmbientIntensity:F2} unless overridden; the viewer default is 1, the authored default 0."
        + (Plan.ParameterOverrides is { Count: > 0 } overrides ? " Explicit TFX parameter overrides: "
            + string.Join(", ", overrides.Select(o => $"0x{o.Key:X8}={o.Value}")) + "." : "");
    public NativeGBufferInputs? GBufferInputs => gbuffer.Inputs;
    /** Depth of the last native frame, D32 as R32_FLOAT, for passes drawn after it. */
    public ID3D11ShaderResourceView? SceneDepth => gbuffer?.Inputs?.Depth;
    /** Linear lit colour of the last native frame, before the viewer gain and final combine. */
    public GpuTexture? SceneColor => sceneColor?.Textures[0];
    /** Runs after the forward stages and exposure measurement, with the lit target on native depth. */
    public Action<NativeRenderTargets>? DrawForwardEffects { get; set; }
    /** Summed CPU milliseconds: record, G-buffer submission, material TFX. */
    public double[] CpuMilliseconds { get; } = new double[3];
    public double ExposureReadbackMilliseconds => composite.ReadbackMilliseconds + skyMeter.ReadbackMilliseconds;
    public int ExposureReadbackCount => composite.ReadbackCount + skyMeter.ReadbackCount;
    public int ExposureReadbackBusyCount => composite.ReadbackBusyCount + skyMeter.ReadbackBusyCount;
    public int ExposureSkippedMeasurements => composite.SkippedMeasurements + skyMeter.SkippedMeasurements;
    public int SkyReflectionGenerations => skyLighting?.GenerationCount ?? 0;
    public bool LightingEnabled { get; set; } = true;
    public float ExposureEv { get; set; }
    /**
     * Viewer-only gain that brings mean geometry luminance to 0.18; the game has no such pass.
     * The meter counts each geometry pixel as reflecting at least 2% of its diffuse light, or of a
     * light of one when lighting is off.
     */
    public bool AutoExposure { get; set; } = true;
    /** Viewer gain in stops when AutoExposure is off. */
    public float ViewerStops { get; set; }
    /** Gain in stops applied to the last frame before final combine. */
    public float AppliedViewerStops => float.IsNaN(viewerLog) ? ViewerStops : viewerLog;
    public float? MeanLogLuminance => composite.MeanLogLuminance;
    /** Share of measured geometry pixels that lit to black; the exposure meter leaves them out. */
    public float BlackFraction => composite.BlackFraction;
    /** The next render measures exposure immediately and resolves one frame at the measured gain. */
    public void RequestExposureWarmup() => exposureWarmupRequested = true;
    /** Replaces parameter 0xE638AAEB for the ambient pass; the authored default is zero. */
    public float AmbientIntensity
    {
        get => ambientOverrides.TryGetValue(AmbientIntensityKey, out var value) ? value.X : 0;
        set
        {
            if (value == AmbientIntensity) return;
            ambientOverrides[AmbientIntensityKey] = new(value, 0, 0, 0);
            ambient?.Dispose(); ambient = null;
        }
    }
    /** Fraction of a day this renderer draws; each renderer keeps its own. */
    public float TimeOfDay { get; set; } = NativeFrameInputs.ViewerTimeOfDay;
    public string? LightingError { get; private set; }
    public NativeAmbientLightingCheck? LightingCheck { get; private set; }
    public NativeAmbientEnvironment? AmbientEnvironment => ambient?.Environment;

    /** Construction runs on the render thread after the worker has decoded shared texture payloads. */
    public NativeSceneRenderer(Dx11Renderer renderer, NativeScenePlan plan, int width, int height,
        Action<string>? progress = null, Action<string, int, int>? preparationProgress = null)
    {
        this.renderer = renderer; Plan = plan;
        ambientOverrides = new(plan.ParameterOverrides ?? new Dictionary<uint, Vector4>());
        if (plan.AssetPreview)
        {
            ambientOverrides[AmbientIntensityKey] = new(AssetAmbientIntensity, 0, 0, 0);
            AutoExposure = false; ViewerStops = AssetViewerStops;
        }
        // Resource creation overlaps target and material setup below; binding stays on this thread.
        var preload = new GpuPreload(renderer, plan);
        try
        {
            preparationProgress?.Invoke("Preparing render targets", 0, 1);
            drawList = Own(new NativeDrawList(renderer, 4));
            runtimeDrawList = Own(new NativeDrawList(renderer, 4));
            forwardDrawList = Own(new NativeDrawList(renderer, 4));
            composite = Own(new NativeSceneComposite(renderer));
            skyMeter = Own(new NativeSceneComposite(renderer));
            // CB11 is bound per draw by the draw list; this placeholder only satisfies declaration checks.
            var masks = Enumerable.Range(0, 16).ToDictionary(i => (uint)(15 + i * 16), _ => 0x03FFFFFFu);
            instanceConstants = new Dictionary<NativeMeshKind, NativeConstantBuffer>
            {
                [NativeMeshKind.Static] = Own(renderer.CreateNativeConstantBuffer(new byte[64 * 16], masks)),
                [NativeMeshKind.Terrain] = Own(renderer.CreateNativeConstantBuffer(new byte[4 * 16], NativeScenePlan.TerrainUnknownWords)),
                [NativeMeshKind.Entity] = Own(renderer.CreateNativeConstantBuffer(new byte[NativeEntityPalettes.Rows * 16]))
            };
            // Validation placeholder for terrain pixel t14; each draw binds its group dye texture or nothing.
            dyePlaceholder = Own(renderer.CreateTexture(1, 1, new byte[4]));
            viewConstants = Own(renderer.CreateNativeConstantBuffer(new byte[14 * 16], NativeViewInputs.UnknownVertexWords));
            pixelViewConstants = Own(renderer.CreateNativeConstantBuffer(new byte[14 * 16], NativeViewInputs.UnknownPixelWords));
            var frame = NativeScenePlan.InspectionFrame(0);
            frameConstants = Own(renderer.CreateNativeConstantBuffer(MemoryMarshal.AsBytes(frame.Vectors.AsSpan()), frame.KnownWordMasks));
            forwardFrameConstants = Own(renderer.CreateNativeConstantBuffer(MemoryMarshal.AsBytes(frame.Vectors.AsSpan()), frame.KnownWordMasks));
            transparentConstants = Own(renderer.CreateNativeConstantBuffer(new byte[NativeTransparentScope.Rows * 16],
                new Dictionary<uint, uint> { [2] = 0, [3] = 0 }));
            skyScopeConstants = Own(renderer.CreateNativeConstantBuffer(new byte[NativeTransparentScope.Rows * 16],
                new Dictionary<uint, uint> { [2] = 0, [3] = 0 }));
            absentTexture = Own(renderer.CreateTexture(1, 1, new byte[4]));
            noFog = Own(renderer.CreateTexture(1, 1, [0, 0, 0, 255]));
            unshadowed = Own(renderer.CreateTexture(1, 1, [255, 255, 255, 255]));
            flatRipples = Own(renderer.CreateTexture(1, 1, [128, 128, 128, 255]));
            absentVolume = Own(renderer.CreateTextureResource(1, 1, 1, 1, 1, Format.R8G8B8A8_UNorm, false, true,
                [new TextureSubresourceUpload(0, 0, new byte[4], 4, 4)]));
            Resize(width, height);
            preparationProgress?.Invoke("Preparing render targets", 1, 1);
            UploadTextures(plan, progress, preparationProgress, preload);
            foreach (var binding in plan.Cubemaps)
                if (textures.TryGetValue(binding.TextureTag, out var texture))
                    localCubemapDraws.Add(new(binding, texture));
            foreach (var (tag, t) in plan.AtmosphereLookups)
                atmosphereLookups.Add(tag, Own(renderer.CreateTextureResource(t.Width, t.Height, t.Depth, t.ArraySize,
                    t.MipCount, (Format)t.Format, t.IsCube, t.IsVolume, t.Subresources.Select(x => new TextureSubresourceUpload(
                        x.MipLevel, x.ArraySlice, x.Data, x.RowPitch, x.SlicePitch)).ToArray())));
            AtmosphereIndex = AtmosphereOverride ?? DefaultAtmosphere(plan);
            int number = 0;
            preparationProgress?.Invoke("Preparing materials", 0, plan.Groups.Count);
            foreach (var source in plan.Groups)
            {
                if (UploadGroup(source, plan.Reader, preload) is { } group)
                    (source.RenderStage == NativeScenePlan.ShadowStage ? shadowGroups : groups).Add(group);
                if (++number % 8 == 0 || number == plan.Groups.Count)
                    preparationProgress?.Invoke("Preparing materials", number, plan.Groups.Count);
            }
            preload.Finish();
            // Stage, then pipeline, material and geometry order keeps state changes rare.
            Comparison<DrawGroup> drawOrder = (a, b) =>
            {
                int order = a.Source.RenderStage.CompareTo(b.Source.RenderStage);
                if (order == 0) order = string.CompareOrdinal(a.Source.PipelineKey, b.Source.PipelineKey);
                if (order == 0) order = a.Source.Mesh.MaterialTag.CompareTo(b.Source.Mesh.MaterialTag);
                return order != 0 ? order : string.CompareOrdinal(a.Source.GeometryKey, b.Source.GeometryKey);
            };
            groups.Sort(drawOrder);
            shadowGroups.Sort(drawOrder);
            AppendGroupHandles(0);
        }
        catch { preload.Finish(); Dispose(); throw; }
    }

    /** New prototypes share uploaded textures by package identity; a preload supplies created textures. */
    private void UploadTextures(NativeScenePlan plan, Action<string>? progress = null,
        Action<string, int, int>? preparationProgress = null, GpuPreload? preload = null)
    {
        int number = 0;
        preparationProgress?.Invoke("Uploading textures", 0, plan.Textures.Count);
        preload?.WaitTextures(preparationProgress);
        foreach (var item in plan.Textures)
        {
            number++;
            if (textures.ContainsKey(item.Key)) continue;
            progress?.Invoke($"Uploading shared texture {number}/{plan.Textures.Count}");
            try
            {
                var t = item.Value;
                textures.Add(item.Key, Own(preload?.TakeTexture(item.Key) ?? renderer.CreateTextureResource(t.Width, t.Height, t.Depth,
                    t.ArraySize, t.MipCount, (Format)t.Format, t.IsCube, t.IsVolume,
                    t.Subresources.Select(s => new TextureSubresourceUpload(s.MipLevel, s.ArraySlice,
                        s.Data, s.RowPitch, s.SlicePitch)).ToArray())));
                badTextures.Remove(item.Key);
            }
            catch (Exception error) when (Recoverable(error))
            {
                badTextures.Add(item.Key);
                AddDiagnostic($"Texture 0x{item.Key:X8}: {error.Message}");
            }
            preparationProgress?.Invoke("Uploading textures", number, plan.Textures.Count);
        }
        plan.Textures.Clear();
    }

    /** A prototype uploads each material and vertex buffer once; instances share those resources. */
    private DrawGroup? UploadGroup(NativeSceneGroup source, GameMaterialReader reader, GpuPreload? preload = null)
    {
        try
        {
            if (source.Material.Execution.Material.Textures.Where(t => t.DeclaredByShader)
                .Any(t => badTextures.Contains(t.TextureTag))
                || source.Material.Gear?.TextureTags.Any(badTextures.Contains) == true
                || source.Parts.Any(p => badTextures.Contains(p.Instance.Mesh.Native!.TerrainDyeTexture)))
                throw new NotSupportedException("A required texture failed GPU upload.");
            (Vector3, float)? water = source.Material.ReadsWaterObject
                ? NativeWater.SingleObject(source.Parts.Select(p => p.Instance.Transform)) : null;
            bool shadow = source.RenderStage == NativeScenePlan.ShadowStage;
            bool forward = !NativeScenePlan.GBufferStages.Contains(source.RenderStage) && !source.Sky && !shadow;
            // Scope 14 fog is per object; the plan gives each fog reader its own group.
            (Vector3, Vector3)? fog = source.Material.ReadsAdvancedScope
                ? (source.Parts[0].Center - new Vector3(source.Parts[0].Radius),
                    source.Parts[0].Center + new Vector3(source.Parts[0].Radius)) : null;
            var decorator = source.Parts[0].Decorator;
            var materialKey = (source.Mesh.MaterialTag, water, fog, forward, source.Material.GearDyeBit,
                source.Material.Gear?.Key ?? "", source.Material.StencilReference,
                (shadow ? "shadow|" : "") + (decorator is null ? "" : $"{decorator.Payload.Tag:X8}@{decorator.Payload.Placement.Translation}:{decorator.Type.Tag:X8}:{decorator.MeshIndex}"),
                source.Material.ObjectChannelKey);
            if (!materials.TryGetValue(materialKey, out var material))
            {
                material = CreateMaterial(source.Material, instanceConstants[source.Kind], forward, decorator, shadow);
                material.WaterObject = water;
                if (fog is { } bounds) material.FogBounds = bounds;
                // Scope 13 binds the screen atmosphere lookup at t11 for every transparent part.
                if (source.Material.ScopeTextures.TryGetValue(NativeScenePlan.AtmosphereLookupSlot, out var lookup)
                    && lookup == "texture2d")
                    lookupReaders.Add(material);
                if (source.Sky)
                    foreach (var stage in source.Material.ScopeConstantStages)
                        material.Bindings.ConstantBuffers[new(stage, 2)] = skyScopeConstants;
                if (source.Kind == NativeMeshKind.Terrain)
                    material.Bindings.Resources[new(GameShaderStage.Pixel, 14)] = dyePlaceholder.View;
                materials.Add(materialKey, material);
            }
            if (!pipelines.TryGetValue(source.PipelineKey, out var pipeline))
            {
                var elements = source.Mesh.InputElements.Select(e => new InputElementDescription(e.Semantic,
                    e.SemanticIndex, (Format)e.Format, (uint)e.Offset, (uint)e.Slot,
                    e.PerInstance ? InputClassification.PerInstanceData : InputClassification.PerVertexData,
                    (uint)e.StepRate)).ToArray();
                var state = source.Material.States;
                var rasterizer = state.Rasterizer;
                var depthState = state.DepthStencil;
                if (source.Sky)
                {
                    // Sky parts draw at depth one and pass only where no geometry wrote depth.
                    rasterizer.DepthClipEnable = false;
                    depthState.DepthEnable = true;
                    depthState.DepthWriteMask = DepthWriteMask.Zero;
                    depthState.DepthFunc = ComparisonFunction.LessEqual;
                    depthState.StencilEnable = false;
                }
                pipeline = Own(renderer.CreateNativePipeline(reader, source.Material.Execution.Material,
                    elements, rasterizer, depthState, state.Blend,
                    source.RenderStage == NativeScenePlan.TransparentsStage ? 1u : shadow ? Dx11Renderer.NoColorTargets : 0u));
                pipelines.Add(source.PipelineKey, pipeline);
            }
            if (!geometry.TryGetValue(source.GeometryKey, out var shared))
            {
                shared = Own(preload?.TakeGeometry(source.GeometryKey) ?? CreateGeometry(renderer, source));
                geometry.Add(source.GeometryKey, shared);
                if (source.ClothDisplayStream is not null) clothGeometry.Add((source, shared));
            }
            var mesh = Own(renderer.CreateNativeMesh(shared, source.Mesh.FirstIndex, source.Mesh.IndexCount,
                source.Mesh.GamePrimitiveType == 3 ? PrimitiveTopology.TriangleList : PrimitiveTopology.TriangleStrip));
            NativeBufferResource? scalars = null;
            if (source.Parts[0].Scalars is { } stream && !scalarBuffers.TryGetValue(source.ScalarDataTag, out scalars))
            {
                // The AO scalar buffer is R8_UNORM.
                scalars = Own(renderer.CreateNativeBufferResource(stream.Data, Format.R8_UNorm));
                scalarBuffers.Add(source.ScalarDataTag, scalars);
            }
            if (scalars is not null) material.Bindings.Resources[new(GameShaderStage.Vertex, 0)] = scalars.View;
            int pass = source.RenderStage == 0 ? PassGBuffer
                : NativeScenePlan.GBufferStages.Contains(source.RenderStage) ? PassDecals
                : shadow ? PassShadow : source.Sky ? PassSky : PassForward;
            // Validation runs once here; the per-frame draw list binds without repeating it.
            var missing = pipeline.Validate(material.Bindings, shadow ? ShadowValidationTarget()
                : pass is PassForward or PassSky ? sceneColor : gbuffer.WriteTargets);
            if (missing.Count != 0) throw new NotSupportedException(string.Join(" ", missing));
            var dyes = source.Kind == NativeMeshKind.Terrain ? source.Parts.Select(p =>
                textures.GetValueOrDefault(p.Instance.Mesh.Native!.TerrainDyeTexture)?.View).ToArray() : null;
            var group = new DrawGroup(source, pipeline, mesh, material, scalars, pass, dyes);
            return group;
        }
        catch (Exception error) when (Recoverable(error))
        {
            gpuSkips[error.Message] = gpuSkips.GetValueOrDefault(error.Message) + source.Parts.Count;
            AddDiagnostic($"Material 0x{source.Mesh.MaterialTag:X8}: {error.Message}");
            return null;
        }
    }

    /** Shared vertex and index buffers; a cloth display mesh takes its object-space bind stream. */
    private static NativeGpuGeometry CreateGeometry(Dx11Renderer renderer, NativeSceneGroup source) =>
        renderer.CreateNativeGeometry(source.Mesh.VertexStreams.Select((s, slot) => new NativeVertexStreamUpload(
                slot == 0 && source.ClothDisplayStream is { } cloth ? cloth : s.Data, (uint)s.Stride)).ToArray(),
            source.Mesh.IndexData, source.Mesh.Is32Bit ? Format.R32_UInt : Format.R16_UInt);

    /** Appending groups preserves all existing handles and GPU resources. */
    private void AppendGroupHandles(int firstGroup)
    {
        for (int order = firstGroup; order < groups.Count; order++)
        {
            groups[order].Order = order;
            groups[order].FirstHandle = handles.Count;
            for (int part = 0; part < groups[order].Source.Parts.Count; part++)
            {
                var group = groups[order]; handles.Add((group, part));
                var instance = group.Source.Parts[part].Instance;
                if (group.Source.Kind != NativeMeshKind.Entity && !group.Runtime) continue;
                if (!instanceGroups.TryGetValue(instance, out var matching)) instanceGroups.Add(instance, matching = []);
                matching.Add(group);
            }
        }
        handleOrders = new int[handles.Count];
        handlePerFrame = new bool[handles.Count];
        visibleGroupParts = new int[handles.Count];
        groupStages = new int[groups.Count]; groupStarts = new int[groups.Count];
        visibleGroupCounts = new int[groups.Count]; visibleGroupOffsets = new int[groups.Count];
        failedGroups = new bool[groups.Count];
        groupDraws = new GroupDraw[groups.Count];
        foreach (var group in groups)
        {
            groupStages[group.Order] = group.Source.RenderStage;
            groupStarts[group.Order] = group.FirstHandle;
            groupDraws[group.Order] = new(group.Pipeline, group.Mesh, group.Material.Bindings, group.Scalars?.View, group.Dyes,
                group.Pass, group.PerDraw, group.PerFrame, group.Material.WaterObject is not null);
            Array.Fill(handleOrders, group.Order, group.FirstHandle, group.Source.Parts.Count);
            Array.Fill(handlePerFrame, group.PerFrame, group.FirstHandle, group.Source.Parts.Count);
        }
        hasRecordedVisibility = false;
        if (firstGroup == 0) opaqueUploadValid = false;
    }

    private T Own<T>(T resource) where T : IDisposable { owned.Add(resource); return resource; }
    private void AddDiagnostic(string text) { if (diagnostics.Count < 64 && !diagnostics.Contains(text)) diagnostics.Add(text); }

    /**
     * Poses skinned entity parts: their palette entries become the bone skin matrices times the placement.
     * Cloth display meshes take their posed stream 0 instead.
     * @param skinFor Skin matrices by skeleton bone for a part, or null to keep the placement palette.
     * @param clothFor Posed stream 0 by position header tag, or null to restore the bind stream.
     * @return Parts posed.
     */
    public int SetEntityPose(Func<MeshInstance, IReadOnlyList<Matrix4x4>?> skinFor,
        Func<uint, ReadOnlyMemory<byte>?>? clothFor = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        entitySkin = skinFor;
        poseRevision++;
        int posed = 0;
        foreach (var group in groups.Concat(shadowGroups))
        {
            var form = group.Source.Material.Palette;
            if (form == NativeEntityPalette.None) continue;
            posed += RefreshEntityGroup(group);
        }
        PosedClothStreams = 0;
        foreach (var (source, shared) in clothGeometry)
        {
            var stream = clothFor?.Invoke(source.Mesh.VertexStreams[0].HeaderTag);
            if (stream is not null) PosedClothStreams++;
            renderer.UpdateNativeVertexStream(shared, 0, (stream ?? source.ClothDisplayStream!).Span);
        }
        return posed;
    }

    /** Cloth display streams the last SetEntityPose wrote from a pose, not the bind stream. */
    public int PosedClothStreams { get; private set; }

    public bool SupportsLiveTransform(MeshInstance instance) => livePalettes.ContainsKey(instance) || (instanceGroups.TryGetValue(instance, out var matching)
        ? matching.All(group => group.Decorator is null) && matching.All(group => group.Runtime && group.Source.Kind == NativeMeshKind.Static
            || group.Source.Kind == NativeMeshKind.Entity && !group.Source.Material.ReadsWaterObject
                && !group.Source.Material.ReadsAdvancedScope && !NativeEntityPalettes.RequiresDiagnosticPlacement(instance.RenderTransform, group.Source.Material.Palette))
        : instance.Mesh.Native?.Kind == NativeMeshKind.Entity);

    public void ValidateLiveTransform(MeshInstance instance, Matrix4x4 transform)
    {
        if (livePalettes.ContainsKey(instance)) return;
        if (!instanceGroups.TryGetValue(instance, out var matching)) return;
        foreach (var group in matching)
            if (group.Source.Kind == NativeMeshKind.Entity)
                NativeEntityPalettes.ValidatePlacement(transform, group.Source.Material.Palette);
    }

    /** Only entity placement constants are refreshed; static AO rows remain authored. */
    public void RefreshLiveTransforms(IEnumerable<MeshInstance> changed)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var affected = new HashSet<DrawGroup>();
        foreach (var instance in changed)
            if (instanceGroups.TryGetValue(instance, out var matching)) affected.UnionWith(matching);
        foreach (var group in affected)
        {
            if (group.Source.Kind != NativeMeshKind.Entity && !group.Runtime) continue;
            if (group.Runtime && group.Source.Kind == NativeMeshKind.Static)
            {
                group.Instances = group.Source.Parts.SelectMany(part => RuntimeConstants(part.Instance, NativeEntityPalette.None)).ToArray();
            }
            else RefreshEntityGroup(group);
            for (int index = 0; index < group.Source.Parts.Count; index++) group.Centers[index] = PartCenter(group.Source.Parts[index]);
            centersRevision++;
        }
        if (affected.Any(group => !group.Runtime)) poseRevision++;
    }

    /** A live pose or preview placement moves the part's sort center with it. */
    private Vector3 PartCenter(NativeScenePart part) =>
        livePalettes.TryGetValue(part.Instance, out var livePalette) ? livePalette.Center
        : part.Instance.PreviewTransform is { } current && Matrix4x4.Invert(part.Instance.Transform, out var inverse)
            ? Vector3.Transform(part.Center, inverse * current) : part.Center;

    /** Copies the plan's recomputed constants of moved parts; call after NativeScenePlan.UpdatePlacements. */
    public void RefreshPlacements(IEnumerable<MeshInstance> moved)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var set = moved.ToHashSet();
        if (set.Count == 0) return;
        foreach (var group in groups.Concat(shadowGroups))
        {
            if (group.Runtime) continue;
            bool touched = false;
            for (int index = 0; index < group.Source.Parts.Count; index++)
            {
                var part = group.Source.Parts[index];
                if (!set.Contains(part.Instance)) continue;
                touched = true;
                if (group.Source.Kind != NativeMeshKind.Entity)
                    part.Constants.AsSpan(0, group.Stride).CopyTo(group.Instances.AsSpan(index * group.Stride));
                group.Centers[index] = PartCenter(part);
            }
            if (touched && group.Source.Kind == NativeMeshKind.Entity) RefreshEntityGroup(group);
        }
        poseRevision++;
        centersRevision++;
    }

    /** Pose and live placement share one palette so either update preserves the other. */
    private int RefreshEntityGroup(DrawGroup group)
    {
        var form = group.Source.Material.Palette; int posed = 0;
        var rows = group.Source.Parts.Select(part =>
        {
            if (livePalettes.TryGetValue(part.Instance, out var captured)) return captured.Rows.ToArray();
            if (NativeEntityPalettes.RequiresDiagnosticPlacement(part.Instance.RenderTransform, form)) return part.Constants;
            if (form == NativeEntityPalette.None) return AssetSceneLoader.ReadNativeEntityConstants(part.Instance);
            if (entitySkin?.Invoke(part.Instance) is not { } skin) return NativeEntityPalettes.Build(part.Instance, form);
            posed++; return NativeEntityPalettes.BuildSkinned(part.Instance, form, skin);
        }).ToArray();
        int stride = rows.Max(row => row.Length);
        var instances = new Vector4[stride * rows.Length];
        for (int part = 0; part < rows.Length; part++) rows[part].CopyTo(instances, part * stride);
        group.Stride = stride; group.Instances = instances;
        return posed;
    }

    /** Every drawable part as an integer handle; build the viewer lookup from this once. */
    public IEnumerable<(MeshInstance Instance, int Handle)> PartHandles() =>
        handles.Select((item, index) => (item.Group.Source.Parts[item.Part].Instance, index));

    public int HandleStage(int handle) => handles[handle].Group.Source.RenderStage;

    /** The CB11 vectors the draw list uploads for a part handle. */
    public ReadOnlySpan<Vector4> HandleConstants(int handle)
    {
        var (group, part) = handles[handle];
        return group.Instances.AsSpan(part * group.Stride, group.Stride);
    }

    /** The stencil plane copied after stage 0 in the last frame; a diagnostic for tests. */
    internal byte[] ReadStencilCopy() => gbuffer.ReadStencilCopy();
    /** G-buffer stencil clear; tests change it to set bits no camera mode 0 draw writes. */
    internal byte StencilClear { get; set; } = NativeStencil.ClearValue;

    /** One material owns its constants; shared samplers and textures are resolved by package tag. */
    private MaterialGpu CreateMaterial(NativeSceneMaterial source, NativeConstantBuffer instanceConstants, bool forward,
        NativeDecoratorPart? decorator = null, bool shadow = false)
    {
        var material = new MaterialGpu { Source = source, Decorator = decorator };
        if (source.ObjectChannelKey.Length != 0)
        {
            if (!channelIds.TryGetValue(source.ObjectChannelKey, out int id)) channelIds.Add(source.ObjectChannelKey, id = channelIds.Count + 1);
            material.ChannelTable = id;
        }
        material.Bindings.StencilReference = source.StencilReference;
        if (decorator is not null && !decorator.Type.Ground)
        {
            // Scope 8 is written before each frame's first draw; scope 12 only for a header with its form byte set.
            if (!decoratorScopes.TryGetValue((decorator.Type.Tag, decorator.MeshIndex), out var scope))
                decoratorScopes.Add((decorator.Type.Tag, decorator.MeshIndex), scope = new(Own(renderer.CreateNativeConstantBuffer(
                    new byte[NativeDecorators.ScopeRows * 16])), decorator));
            material.DecoratorScope = scope;
            material.Bindings.ConstantBuffers[new(GameShaderStage.Vertex, NativeDecorators.ScopeSlot)] = scope.Buffer;
            if (decorator.Payload.Form != 0)
            {
                decoratorLodScope ??= Own(renderer.CreateNativeConstantBuffer(MemoryMarshal.AsBytes(
                    new[] { NativeDecorators.LodScopeRow }.AsSpan())));
                material.Bindings.ConstantBuffers[new(GameShaderStage.Vertex, NativeDecorators.LodScopeSlot)] = decoratorLodScope;
            }
        }
        if (decorator is not null)
        {
            decoratorMaterials.Add(material);
            if (!decoratorTypes.Exists(type => type.Tag == decorator.Type.Tag)) decoratorTypes.Add(decorator.Type);
        }
        material.Bindings.ConstantBuffers[new(GameShaderStage.Vertex, 11)] = instanceConstants;
        // A caster reads the view of the cascade it draws into; the shadow pass rewrites it per cascade.
        material.Bindings.ConstantBuffers[new(GameShaderStage.Vertex, 12)] = shadow ? ShadowViewConstants() : viewConstants;
        if (source.ReadsShadowScope)
            material.Bindings.Resources[new(GameShaderStage.Vertex, NativeScenePlan.ShadowScopeSlot)] = SdsmIdentity().View;
        if (source.PixelViewKnown) material.Bindings.ConstantBuffers[new(GameShaderStage.Pixel, 12)] = pixelViewConstants;
        foreach (var stage in source.FrameStages)
            material.Bindings.ConstantBuffers[new(stage, 13)] = forward ? forwardFrameConstants : frameConstants;
        foreach (var stage in source.ScopeConstantStages) material.Bindings.ConstantBuffers[new(stage, 2)] = transparentConstants;
        if (source.ReadsAdvancedScope)
        {
            material.Fog = Own(renderer.CreateNativeConstantBuffer(new byte[NativeObjectFog.Rows * 16], NativeObjectFog.UnknownWords));
            material.Bindings.ConstantBuffers[new(GameShaderStage.Pixel, 8)] = material.Fog;
            fogReaders.Add(material);
        }
        // The scene depth replaces the zero placeholder once each frame resolves.
        foreach (var (slot, dimension) in source.ScopeTextures)
            material.Bindings.Resources[new(GameShaderStage.Pixel, slot)] = dimension == "texture3d" ? absentVolume.View
                // Scope t16 is fog: colour times alpha plus rgb. (0, 0, 0, 1) leaves the colour unchanged.
                : slot == FogSlot ? noFog.View : absentTexture.View;
        if (source.ScopeTextures.ContainsKey(NativeScenePlan.SceneDepthSlot)) depthReaders.Add(material);
        bool isStatic = true;
        foreach (var stage in new[] { source.Execution.Vertex, source.Execution.Pixel })
        {
            var program = TfxProgramInspection.Read(stage.TfxBytecode);
            if (!program.FullyDecoded || program.Dependencies.Count != 0) isStatic = false;
            // Extern 7 floats 61 and 73 follow the camera height.
            if (program.Dependencies.Any(d => d.Extern is 2 or 7)) material.UsesCamera = true;
            if (program.Dependencies.Any(d => d is { Extern: 1, Element: 7 or 8 })) material.UsesExposure = true;
            if (ReferenceEquals(stage, source.Execution.Vertex)) material.VertexShare = ShareOf(stage, program);
            else material.PixelShare = ShareOf(stage, program);
            var inputs = NativeScenePlan.MaterialInputs(0, Plan.Parameters, null, Plan.AtmosphereSlots, timeOfDay: TimeOfDay,
                assetPreview: Plan.AssetPreview);
            NativeGearDye.Apply(inputs, source.GearDyeBit);
            inputs.ObjectChannels = source.ObjectChannels;
            if (decorator is not null)
            {
                NativeDecorators.ApplyExtern72(inputs, decorator.Payload.Extern72);
                if (decorator.Type.Ground) NativeDecorators.ApplyExtern47(inputs, Vector4.Zero);
            }
            var evaluation = TfxEvaluator.Evaluate(stage, inputs);
            evaluation.RequireComplete();
            material.Bindings.MaterialEvaluations[stage.Stage] = evaluation;
            if (evaluation.Constants.Length != 0)
            {
                material.Bindings.ConstantBuffers[new(stage.Stage, 0)] = Own(renderer.CreateNativeConstantBuffer(
                    MemoryMarshal.AsBytes(evaluation.Constants.AsSpan())));
                material.Constants[stage.Stage] = evaluation.Constants;
            }
            ApplySamplers(material, evaluation);
        }
        material.Static = isStatic;
        if (source.Gear is { } gear)
        {
            // Gear scopes bind first; a texture the material names itself replaces them.
            foreach (var scope in gear.Scopes)
            {
                material.Bindings.ConstantBuffers[new(GameShaderStage.Pixel, scope.ConstantBufferSlot)] = Own(
                    renderer.CreateNativeConstantBuffer(MemoryMarshal.AsBytes(scope.Constants.AsSpan())));
                foreach (var (slot, tag) in scope.Textures)
                    material.Bindings.Resources[new(GameShaderStage.Pixel, slot)] =
                        tag == NativeGearPlating.FrameDyeTexture ? absentTexture.View : textures[tag].View;
            }
            foreach (var (slot, tag) in gear.PlateTextures)
                material.Bindings.Resources[new(GameShaderStage.Pixel, slot)] = textures[tag].View;
        }
        foreach (var texture in source.Execution.Material.Textures.Where(t => t.DeclaredByShader))
            material.Bindings.Resources[new(texture.Stage, texture.Slot)] = textures[texture.TextureTag].View;
        foreach (var (key, dimension) in source.NullTextures)
            material.Bindings.Resources[key] = dimension == "texture3d" ? absentVolume.View : absentTexture.View;
        if (source.ExternResources)
        {
            externReaders.Add(material);
            ApplyExternResources(material);
            readsPreDecalNormals |= material.Bindings.MaterialEvaluations.Values
                .Any(evaluation => evaluation.ResourceBindings.Values.Contains(NativeScenePlan.PreDecalNormals));
        }
        return material;
    }

    // Scope 13 binds extern 39 resource 5, the fog, at pixel t16.
    private const uint FogSlot = 16;

    /** Binds the views of the frame resources TFX chose; they change on resize and atmosphere switch. */
    private void ApplyExternResources(MaterialGpu material) => ApplyExternResources(material, ExternViews());

    /** ApplyExternResources for every extern reader. */
    private void ApplyExternResources()
    {
        var views = ExternViews();
        foreach (var reader in externReaders) ApplyExternResources(reader, views);
    }

    /** Skips a material that already bound these views and these evaluations. */
    private void ApplyExternResources(MaterialGpu material, Dictionary<TfxResource, ID3D11ShaderResourceView> views)
    {
        var evaluations = material.Bindings.MaterialEvaluations;
        var vertex = evaluations.GetValueOrDefault(GameShaderStage.Vertex);
        var pixel = evaluations.GetValueOrDefault(GameShaderStage.Pixel);
        bool unchanged = material.ExternVersion == externViewsVersion && evaluations.Count <= 2
            && ReferenceEquals(vertex, material.ExternVertex) && ReferenceEquals(pixel, material.ExternPixel);
        if (unchanged) return;
        foreach (var evaluation in evaluations.Values)
            if (evaluation.ResourceBindings.Count != 0) material.Bindings.ApplyTfxResources(evaluation, views);
        material.ExternVersion = externViewsVersion; material.ExternVertex = vertex; material.ExternPixel = pixel;
    }

    private readonly Dictionary<TfxResource, ID3D11ShaderResourceView> externViews = [];
    // Changes each time ExternViews rebuilds the views.
    private int externViewsVersion;
    private readonly ID3D11ShaderResourceView?[] externViewSources = new ID3D11ShaderResourceView?[8];

    /** The frame resource views by TFX resource; rebuilt when one of the views changes. */
    private Dictionary<TfxResource, ID3D11ShaderResourceView> ExternViews()
    {
        var lookup2D = (uint)AtmosphereIndex < (uint)Plan.Atmospheres.Count
            && atmosphereLookups.TryGetValue(AtmosphereSettings().Lookups[2], out var lookup)
            ? lookup.View : absentTexture.View;
        var noise = textures.GetValueOrDefault(Plan.WindNoiseTexture)?.View ?? absentTexture.View;
        // The transparent pass binds sky_hemisphere_mips as extern 3 resource 19; 0x7FF742E96782.
        var environment = (SkyLightingActive ? skyLighting?.Reflection : StudioLightingActive ? studioReflection : null)?.View
            ?? absentTexture.View;
        Span<ID3D11ShaderResourceView?> current = [refraction.Textures[0].View, screenUv.View, lookup2D,
            skyLookup.Textures[0].View, gbuffer.NormalRead.View, sceneDepth ?? absentTexture.View, gbuffer.StencilRead, environment];
        bool changed = externViews.Count == 0 || !ReferenceEquals(externViews[NativeWindFrame.Noise], noise);
        for (int i = 0; i < current.Length; i++)
            if (!ReferenceEquals(current[i], externViewSources[i])) { changed = true; externViewSources[i] = current[i]; }
        if (!changed) return externViews;
        externViewsVersion++;
        externViews.Clear();
        externViews[NativeWater.Refraction] = refraction.Textures[0].View; externViews[NativeWater.ScreenOffsets] = screenUv.View;
        externViews[NativeWater.Shadow] = unshadowed.View; externViews[NativeWater.Ripples] = flatRipples.View;
        externViews[NativeWater.Reflection] = absentTexture.View; externViews[NativeWater.Environment] = environment;
        externViews[NativeWater.AtmosphereLookup] = lookup2D; externViews[NativeWater.ScreenLookup] = skyLookup.Textures[0].View;
        externViews[NativeScenePlan.PreDecalNormals] = gbuffer.NormalRead.View;
        externViews[NativeGearPlating.FrameDyeResource] = absentTexture.View;
        externViews[NativeScenePlan.SceneDepth] = sceneDepth ?? absentTexture.View;
        externViews[NativeStencil.SceneStencil] = gbuffer.StencilRead;
        externViews[NativeWindFrame.Noise] = noise;
        externViews[NativeWindFrame.Unset] = absentTexture.View;
        foreach (var (resource, view) in NativeOcean.Views(Plan.Ocean, tag => textures.GetValueOrDefault(tag)?.View))
            externViews[resource] = view;
        return externViews;
    }

    private NativeAtmosphereSettings? atmosphereSettings;
    private int atmosphereSettingsIndex = -1;

    /** The parsed settings of the atmosphere AtmosphereIndex selects; the index must be valid. */
    private NativeAtmosphereSettings AtmosphereSettings()
    {
        if (atmosphereSettings is null || atmosphereSettingsIndex != AtmosphereIndex)
        {
            atmosphereSettings = NativeAtmosphereSettings.Parse(Plan.Atmospheres[AtmosphereIndex].Settings);
            atmosphereSettingsIndex = AtmosphereIndex;
        }
        return atmosphereSettings;
    }

    /** TFX may choose only verified sampler records; every result is applied before drawing. */
    private void ApplySamplers(MaterialGpu material, TfxEvaluation evaluation)
    {
        // A non-empty result is a Dictionary; enumerating it directly avoids a boxed enumerator.
        if (evaluation.Samplers is not Dictionary<NativeBindingKey, MaterialSampler> bindings)
        {
            foreach (var binding in evaluation.Samplers) ApplySampler(material, binding.Key, binding.Value);
            return;
        }
        foreach (var binding in bindings) ApplySampler(material, binding.Key, binding.Value);
    }

    private void ApplySampler(MaterialGpu material, NativeBindingKey key, MaterialSampler value)
    {
        if (!samplers.TryGetValue(value.HeaderTag, out var sampler))
        {
            sampler = Own(renderer.Device.CreateSamplerState(value.Description));
            samplers.Add(value.HeaderTag, sampler);
        }
        material.Bindings.Samplers[key] = sampler;
    }

    /**
     * A material is evaluated once per time value even when it appears in several draw groups.
     * @return True when TFX ran.
     */
    private bool UpdateMaterial(MaterialGpu material, TfxInputs inputs, double seconds, float exposureEv)
    {
        if (material.Static || material.LastTime == seconds && !material.UsesCamera
            && (!material.UsesExposure || material.LastExposureEv == exposureEv)) return false;
        // The inputs are shared by every material, so each sets its own bit and channel table.
        NativeGearDye.Apply(inputs, material.Source.GearDyeBit);
        inputs.ObjectChannels = material.Source.ObjectChannels;
        UpdateStage(material, material.Source.Execution.Vertex, material.VertexShare, inputs);
        UpdateStage(material, material.Source.Execution.Pixel, material.PixelShare, inputs);
        material.LastTime = seconds;
        material.LastExposureEv = exposureEv;
        return true;
    }

    private void UpdateStage(MaterialGpu material, MaterialStageData stage, TfxShare share, TfxInputs inputs)
    {
        if (stage.TfxBytecode.Length == 0) return;
        ApplyEvaluation(material, stage, SharedEvaluation(stage, share, inputs, material.UsesCamera, material.ChannelTable));
    }

    /** Binds one stage's evaluation: its constants, when they changed, and its samplers. */
    private void ApplyEvaluation(MaterialGpu material, MaterialStageData stage, TfxEvaluation evaluation)
    {
        evaluation.RequireComplete();
        material.Bindings.MaterialEvaluations[stage.Stage] = evaluation;
        if (material.Constants.TryGetValue(stage.Stage, out var previous)
            && !MemoryMarshal.AsBytes(previous.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(evaluation.Constants.AsSpan())))
        {
            renderer.UpdateNativeConstantBuffer(material.Bindings.ConstantBuffers[new(stage.Stage, 0)],
                MemoryMarshal.AsBytes(evaluation.Constants.AsSpan()));
            // A copy: the evaluation's array is reused by a later frame.
            evaluation.Constants.CopyTo(previous, 0);
        }
        ApplySamplers(material, evaluation);
    }

    /**
     * A stage program for shared evaluation: its content id and the GroupKeys it reads.
     * Program -1 never shares, since its reads are not known.
     */
    private readonly record struct TfxShare(int Program, int Reads)
    {
        public static readonly TfxShare None = new(-1, 0);
    }

    // Inputs that change between groups of one frame; Render sets the rest once per frame.
    // Index 0 is a float; the rest are vectors.
    private static readonly TfxExternKey[] GroupKeys = [new(67, 28), new(67, 4), NativeGearDye.Key, new(47, 0),
        new(72, 0), new(72, 1), new(72, 2), new(72, 3), new(72, 4), new(72, 5), new(72, 6), new(72, 7)];

    [System.Runtime.CompilerServices.InlineArray(12)]
    private struct GroupValues { private Vector4 first; }

    // TfxShare.Reads bit of a program that reads object channels (TFX 0x4D); clear of the GroupKeys bits.
    private const int ChannelsRead = 1 << 30;

    /** Program, channel table and the bits of the group inputs it reads; compared bit for bit. */
    private struct SharedKey : IEquatable<SharedKey>
    {
        public int Program, Present, Channels;
        public GroupValues Values;
        public readonly bool Equals(SharedKey other) => Bytes(in this).SequenceEqual(Bytes(in other));
        public override readonly bool Equals(object? other) => other is SharedKey key && Equals(key);
        public override readonly int GetHashCode()
        {
            var hash = new HashCode();
            hash.AddBytes(Bytes(in this));
            return hash.ToHashCode();
        }
        private static ReadOnlySpan<byte> Bytes(in SharedKey key) => MemoryMarshal.AsBytes(new ReadOnlySpan<SharedKey>(in key));
    }

    private readonly Dictionary<MaterialStageData, int> programIds = new(new StageContentComparer());
    private readonly Dictionary<SharedKey, TfxEvaluation> sharedEvaluations = [];
    // Materials created per program id.
    private readonly List<int> programUsers = [];
    private TfxInputs? sharedInputs;

    /** Which GroupKeys a decoded program reads; an undecoded program does not share. */
    private TfxShare ShareOf(MaterialStageData stage, TfxProgramSummary program)
    {
        if (stage.TfxBytecode.Length == 0 || !program.FullyDecoded) return TfxShare.None;
        int reads = 0;
        foreach (var dependency in program.Dependencies)
            for (int slot = 0; slot < GroupKeys.Length; slot++)
                if (dependency.Extern == GroupKeys[slot].Extern && dependency.Element == GroupKeys[slot].Element
                    && dependency.Kind == (slot == 0 ? "Float" : "Vector")) reads |= 1 << slot;
        if (program.Instructions.Any(instruction => instruction.Opcode == 0x4D)) reads |= ChannelsRead;
        if (!programIds.TryGetValue(stage, out int id)) { programIds.Add(stage, id = programIds.Count); programUsers.Add(0); }
        programUsers[id]++;
        return new(id, reads);
    }

    /**
     * One evaluation per program and group inputs for the inputs of one frame.
     * The evaluator is a pure function of the stage content and the inputs, so a shared result is exact.
     */
    private TfxEvaluation SharedEvaluation(MaterialStageData stage, TfxShare share, TfxInputs inputs, bool usesCamera,
        int channelTable)
    {
        // One material without the camera runs once per frame, so no lookup could hit.
        if (share.Program < 0 || programUsers[share.Program] == 1 && !usesCamera) return TfxEvaluator.Evaluate(stage, inputs);
        if (!ReferenceEquals(inputs, sharedInputs)) { sharedEvaluations.Clear(); sharedInputs = inputs; }
        var key = new SharedKey { Program = share.Program, Channels = (share.Reads & ChannelsRead) != 0 ? channelTable : 0 };
        for (int slot = 0; slot < GroupKeys.Length; slot++)
        {
            if ((share.Reads & 1 << slot) == 0) continue;
            bool present;
            Vector4 value;
            if (slot == 0) { present = inputs.Floats.TryGetValue(GroupKeys[0], out float scalar); value = new(scalar, 0, 0, 0); }
            else present = inputs.Vectors.TryGetValue(GroupKeys[slot], out value);
            key.Values[slot] = value;
            if (present) key.Present |= 1 << slot;
        }
        if (!sharedEvaluations.TryGetValue(key, out var evaluation))
            sharedEvaluations.Add(key, evaluation = TfxEvaluator.Evaluate(stage, inputs));
        return evaluation;
    }

    /** Stages match when everything the evaluator reads matches: bytecode, constants, template and samplers. */
    private sealed class StageContentComparer : IEqualityComparer<MaterialStageData>
    {
        public bool Equals(MaterialStageData? a, MaterialStageData? b) => ReferenceEquals(a, b) || a is not null && b is not null
            && a.Stage == b.Stage && a.TfxBytecode.AsSpan().SequenceEqual(b.TfxBytecode)
            && MemoryMarshal.AsBytes(a.TfxConstants.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.TfxConstants.AsSpan()))
            && MemoryMarshal.AsBytes(a.ConstantTemplate.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.ConstantTemplate.AsSpan()))
            && a.Samplers.SequenceEqual(b.Samplers);

        public int GetHashCode(MaterialStageData stage)
        {
            var hash = new HashCode();
            hash.Add(stage.Stage);
            hash.AddBytes(stage.TfxBytecode);
            hash.AddBytes(MemoryMarshal.AsBytes(stage.TfxConstants.AsSpan()));
            hash.AddBytes(MemoryMarshal.AsBytes(stage.ConstantTemplate.AsSpan()));
            hash.Add(stage.Samplers.Count);
            return hash.ToHashCode();
        }
    }

    /**
     * Draws the given part handles through every enabled stage, then lighting, composite and final combine.
     * @param visible Handles from PartHandles for the parts the viewer's cull kept.
     */
    public void Render(ViewerCamera camera, double simulationSeconds, List<int> visible, Vector4 background)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        bool warmExposure = exposureWarmupRequested || Plan.AssetPreview && float.IsNaN(viewerLog);
        exposureWarmupRequested = false;
        float aspectRatio = (float)Targets.Width / Targets.Height;
        float ev = Math.Clamp(ExposureEv, NativeAmbientLighting.MinimumExposureEv, NativeAmbientLighting.MaximumExposureEv);
        var inputs = NativeScenePlan.MaterialInputs(simulationSeconds, Plan.Parameters, NativeViewInputs.CreateCameraInputs(
            checked((ushort)Targets.Width), checked((ushort)Targets.Height), camera.View, camera.Projection(aspectRatio)),
            Plan.AtmosphereSlots, camera.Position.Z, NativeAtmosphere.SizeOf(skyLookup.Width, skyLookup.Height), ev, TimeOfDay,
            Plan.AssetPreview);
        windFrame ??= new NativeWindFrame(Plan.WindSettings);
        if (windTime != simulationSeconds) { windFrame.Tick(simulationSeconds); windTime = simulationSeconds; }
        windFrame.Apply(inputs);
        KeepFrameInputs(inputs, simulationSeconds, ev);
        if (frameTime != simulationSeconds || frameEv != ev)
        {
            var frame = NativeScenePlan.InspectionFrame(simulationSeconds, ev);
            renderer.UpdateNativeConstantBuffer(frameConstants, MemoryMarshal.AsBytes(frame.Vectors.AsSpan()));
            frameTime = simulationSeconds; frameEv = ev;
        }
        UpdateView(camera, aspectRatio);
        if (decoratorMaterials.Count != 0) UpdateDecorators(camera.Position, simulationSeconds);
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        bool stagesChanged = !recordedStages.SetEquals(EnabledStages);
        var eye = camera.Position;
        // Gathered decorator parts passed their LOD test for one eye, foliage detail and placement.
        bool decoratorsMoved = gatheredDecorators && (eye != gatheredEye || FoliageDetail != gatheredDetail
            || centersRevision != gatheredCentersRevision);
        bool visibilityChanged = !hasRecordedVisibility || stagesChanged || decoratorsMoved ||
            !CollectionsMarshal.AsSpan(visible).SequenceEqual(CollectionsMarshal.AsSpan(recordedVisible));
        bool staticVisibilityChanged = visibilityChanged && StaticVisibilityChanged(visible, stagesChanged);
        if (visibilityChanged) { GatherVisibleParts(visible, eye); InvalidateShadowCasters(); }
        Span<int> stageCounts = stackalloc int[RenderNames.StageNames.Count];
        int evaluations = 0;
        int drawn = 0, failed = 0, waterParts = 0;
        bool hasForward = false;
        forwardDrawList.Clear();
        runtimeDrawList.ResetCounters();
        drawList.ResetCounters();
        forwardDrawList.ResetCounters();
        // Last frame's far-to-near order stands while its inputs do; a sort of equal input gives equal order.
        bool reuseSorted = sortedValid && !visibilityChanged && eye == sortedEye && centersRevision == sortedCentersRevision;
        // The list is valid again only after a complete sort below.
        if (!reuseSorted) { sorted.Clear(); sortedValid = false; }
        long evaluationStart = System.Diagnostics.Stopwatch.GetTimestamp();
        PlanMaterialUpdates(inputs, simulationSeconds, ev);
        evaluations = RunMaterialUpdates(inputs);
        CpuMilliseconds[2] += System.Diagnostics.Stopwatch.GetElapsedTime(evaluationStart).TotalMilliseconds;
        foreach (int order in activeOrders)
        {
            int count = visibleGroupCounts[order];
            if (failedGroups[order]) { failed += count; continue; }
            ref readonly var draw = ref groupDraws[order];
            stageCounts[groupStages[order]] += count;
            if (draw.Water) waterParts += count;
            drawn += count;
            hasForward |= draw.Pass == PassForward;
            if (!reuseSorted && draw.Pass is PassForward or PassSky) AddSorted(groups[order], eye);
        }
        if (reuseSorted && failed != 0)
        {
            reuseSorted = false;
            sorted.Clear();
            sortedValid = false;
            foreach (var group in activeGroups)
                if (!failedGroups[group.Order] && group.Pass is PassForward or PassSky) AddSorted(group, eye);
        }
        bool rebuildOpaque = !CacheOpaqueDraws || !opaqueUploadValid || staticVisibilityChanged || recordedPoseRevision != poseRevision ||
            previousOpaqueFailure || failed != 0;
        if (rebuildOpaque)
        {
            opaqueUploadValid = false;
            drawList.Clear();
            opaqueFills.Clear();
            foreach (int order in activeOrders)
            {
                ref readonly var draw = ref groupDraws[order];
                if (draw.PerFrame || draw.Pass is PassForward or PassSky || failedGroups[order]) continue;
                var group = groups[order];
                int offset = visibleGroupOffsets[order], total = visibleGroupCounts[order], stride = group.Stride;
                for (int start = 0; start < total; start += draw.PerDraw)
                {
                    int count = Math.Min(draw.PerDraw, total - start);
                    int vector = drawList.Reserve(draw.Pass, draw.Pipeline, draw.Mesh, draw.Bindings, draw.Scalars,
                        (uint)count, stride, draw.Dyes is not null, draw.Dyes?[visibleGroupParts[offset + start]]);
                    opaqueFills.Add(new(group, offset + start, count, vector));
                }
            }
            FillOpaque();
            recordedPoseRevision = poseRevision;
            OpaqueRecordings++;
        }
        previousOpaqueFailure = failed != 0;
        // Decorator draws read only the eye, the foliage detail, the visible parts and the placements.
        bool reusePerFrame = perFrameReusable && !visibilityChanged && failed == 0 && eye == perFrameEye
            && FoliageDetail == perFrameDetail && poseRevision == perFramePose;
        if (reusePerFrame)
        {
            foreach (var material in perFrameMaterials) WriteDecoratorScope(material);
            perFrameLodCounts.CopyTo(decoratorLodCounts, 0);
        }
        else
        {
            runtimeDrawList.Clear();
            perFrameMaterials.Clear();
            // Live slots move without a revision, so a list that holds one is recorded again.
            bool reusable = failed == 0;
            foreach (var group in activeGroups)
            {
                if (!group.PerFrame || group.Pass is PassForward or PassSky || failedGroups[group.Order]) continue;
                reusable &= !group.Runtime;
                if (group.Material.DecoratorScope is not null) perFrameMaterials.Add(group.Material);
                var parts = visibleGroupParts.AsSpan(visibleGroupOffsets[group.Order], visibleGroupCounts[group.Order]);
                for (int start = 0; start < parts.Length; start += group.PerDraw)
                    Record(group.Pass, group, parts.Slice(start, Math.Min(group.PerDraw, parts.Length - start)));
            }
            perFrameReusable = reusable; perFrameEye = eye; perFrameDetail = FoliageDetail; perFramePose = poseRevision;
            decoratorLodCounts.CopyTo(perFrameLodCounts, 0);
            PerFrameRecordings++;
        }
        // Lit-color stages draw far to near; consecutive parts of one group still share a draw.
        if (!reuseSorted)
        {
            sorted.Sort(FarToNear);
            sortedEye = eye; sortedCentersRevision = centersRevision;
        }
        // A list built without failed groups is missing their parts next frame.
        sortedValid = failed == 0;
        for (int start = 0; start < sorted.Count;)
        {
            var group = sorted[start].Group;
            int count = 1;
            while (count < group.PerDraw && start + count < sorted.Count && sorted[start + count].Group == group) count++;
            for (int i = 0; i < count; i++) forwardParts[i] = sorted[start + i].Part;
            Record(group.Pass, group, forwardParts.AsSpan(0, count));
            start += count;
        }
        long recorded = System.Diagnostics.Stopwatch.GetTimestamp();
        CpuMilliseconds[0] += System.Diagnostics.Stopwatch.GetElapsedTime(began, recorded).TotalMilliseconds;
        try
        {
            if (rebuildOpaque) { drawList.Upload(); opaqueUploadValid = true; }
            forwardDrawList.Upload();
            // A dynamic buffer keeps its contents until the next discard map.
            if (!reusePerFrame) runtimeDrawList.Upload();
            gbuffer.Clear(Vector4.Zero, 1, StencilClear);
            drawList.Execute(PassGBuffer, gbuffer.WriteTargets);
            runtimeDrawList.Execute(PassGBuffer, gbuffer.WriteTargets);
            gbuffer.CaptureDepthStencil();
            if (readsPreDecalNormals)
            {
                gbuffer.CaptureNormals();
                // A resize replaces the copy, so its view is bound again each frame.
                ApplyExternResources();
            }
            drawList.Execute(PassDecals, gbuffer.WriteTargets);
            runtimeDrawList.Execute(PassDecals, gbuffer.WriteTargets);
            CpuMilliseconds[1] += System.Diagnostics.Stopwatch.GetElapsedTime(recorded).TotalMilliseconds;
            var resolved = gbuffer.Resolve();
            foreach (var reader in depthReaders)
                reader.Bindings.Resources[new(GameShaderStage.Pixel, NativeScenePlan.SceneDepthSlot)] = resolved.Depth;
            if (sceneDepth != resolved.Depth)
            {
                sceneDepth = resolved.Depth;
                ApplyExternResources();
            }
            AtmosphereActive = RenderAtmosphere(resolved.Depth);
            RenderLighting(resolved, camera, simulationSeconds);
            if (!AtmosphereActive)
            {
                // An item orbited in its own frame keeps the gradient's zenith above it on screen.
                var skyUp = Vector3.Transform(Vector3.UnitZ, camera.Basis);
                composite.DrawSkyGradient(skyColor, resolved.Depth, cameraView * cameraProjection, AtmosphereFill,
                    AssetBackdropActive ? AssetBackdrop : null, skyUp);
                composite.DrawSkyGradient(skyLookup, resolved.Depth, cameraView * cameraProjection, AtmosphereFill, up: skyUp);
            }
            foreach (var reader in lookupReaders)
                reader.Bindings.Resources[new(GameShaderStage.Pixel, NativeScenePlan.AtmosphereLookupSlot)] = skyLookup.Textures[0].View;
            forwardDrawList.Execute(PassSky, skyColor, 1);
            // The first sky measurement waits for the GPU, so the first frames do not depend on readback timing.
            skyMeter.MeasureLuminance(skyColor.Textures[0], resolved.Depth, true, warmExposure || !skyMeasured);
            skyMeasured = true;
            if (skyMeter.MeanLogLuminance is { } skyMean)
            {
                float fixedLog = SkyFixedLog;
                float wanted = Math.Clamp(MathF.Log2(SkyTarget) - skyMean, fixedLog - SkyRangeStops, fixedLog + SkyRangeStops);
                skyLog = warmExposure || float.IsNaN(skyLog) ? wanted : skyLog + (wanted - skyLog) * 0.15f;
            }
            float skyDisplayLog = float.IsNaN(skyLog) ? SkyFixedLog : skyLog;
            float viewerGain = MathF.Pow(2, float.IsNaN(viewerLog) ? ViewerStops : viewerLog);
            DeferredActive = RenderDeferred(resolved, simulationSeconds, ev, MathF.Pow(2, skyDisplayLog) / viewerGain);
            float backdrop = AssetBackdropActive && !AtmosphereActive ? AssetBackdropGain : 1;
            composite.Composite(sceneColor, resolved.Albedo, LightingEnabled && LightingError is null ? Lighting?.Diffuse : null,
                LightingEnabled && LightingError is null ? Lighting?.Specular : null, resolved.Depth, background,
                skyColor.Textures[0], MathF.Pow(2, skyDisplayLog) / viewerGain * backdrop, DeferredActive);
            if (hasForward)
            {
                // The game's forward pass is pre-exposed: frame row 1 X is the exposure and authored colours are
                // display values. It runs here in display units, exposed like the sky; an asset preview has no sky
                // of its own, so its transparents, such as hologram shells, take the item's exposure.
                float forwardExposure = Plan.AssetPreview ? viewerGain : MathF.Pow(2, skyDisplayLog);
                var forwardFrame = NativeScenePlan.InspectionFrame(simulationSeconds, ev, forwardExposure / MathF.Pow(2, ev));
                renderer.UpdateNativeConstantBuffer(forwardFrameConstants, MemoryMarshal.AsBytes(forwardFrame.Vectors.AsSpan()));
                composite.Scale(sceneColor.Textures[0], forwardColor, viewerGain);
                // Water refracts the lit colour as it stands before transparents.
                if (externReaders.Count != 0)
                {
                    renderer.CopyTexture(refraction.Textures[0], forwardColor.Textures[0]);
                    ApplyExternResources();
                }
                forwardDrawList.Execute(PassForward, forwardColor);
                composite.Scale(forwardColor.Textures[0], sceneColor, 1 / viewerGain);
            }
            // Without lighting the composite draws albedo, which is lit by a light of one.
            var illumination = LightingEnabled && LightingError is null ? Lighting?.Diffuse : null;
            composite.MeasureLuminance(sceneColor.Textures[0], resolved.Depth, immediate: warmExposure,
                illumination: illumination, uniformLight: illumination is null ? 1 : 0);
            if (AutoExposure && composite.MeanLogLuminance is { } mean)
            {
                float target = Math.Clamp(MathF.Log2(0.18f) - mean, -4, 16);
                // The first measurement applies at once; later ones ease in to avoid flicker.
                viewerLog = warmExposure || float.IsNaN(viewerLog) ? target : viewerLog + (target - viewerLog) * 0.15f;
            }
            else if (!AutoExposure) viewerLog = ViewerStops;
            // Viewer effects stay out of the exposure measurement.
            DrawForwardEffects?.Invoke(sceneColor);
            composite.Scale(sceneColor.Textures[0], exposedColor, MathF.Pow(2, float.IsNaN(viewerLog) ? ViewerStops : viewerLog));
        }
        catch (Exception error) when (Recoverable(error) || error is InvalidOperationException)
        {
            opaqueUploadValid = false;
            perFrameReusable = false;
            failed += drawn; drawn = 0;
            AddDiagnostic($"Native frame: {error.Message}");
        }
        finally { renderer.RestoreMainTarget(); }
        int draws = drawList.DrawCount + runtimeDrawList.DrawCount + forwardDrawList.DrawCount + LastShadowDraws;
        renderer.CountDraws(draws);
        // Callers keep LastFrame, so a changed count gets a new dictionary; an unchanged one is shared.
        if (!stageCounts.SequenceEqual(lastStageCounts))
        {
            var stageParts = new Dictionary<string, int>();
            for (int stage = 0; stage < stageCounts.Length; stage++)
                if (stageCounts[stage] != 0) stageParts[RenderNames.StageNames[stage]] = stageCounts[stage];
            lastStageParts = stageParts;
            lastStageCounts = stageCounts.ToArray();
        }
        LastFrame = new(drawn, draws, (drawList.IndexCount + runtimeDrawList.IndexCount + forwardDrawList.IndexCount) / 3, failed,
            lastStageParts, evaluations);
        LastWaterParts = waterParts;
        if (warmExposure && AutoExposure && !float.IsNaN(viewerLog))
            Render(camera, simulationSeconds, visible, background);
    }

    /** Read G-buffer and lighting samples only after Render. */
    public (Vector4 Albedo, Vector4 Third, Vector4 Diffuse, Vector4 Scene)[] SamplePixels(IReadOnlyList<(int X, int Y)> points)
    {
        var inputs = gbuffer.Inputs ?? throw new InvalidOperationException("The native scene has not rendered.");
        var albedo = renderer.ReadTexturePixels(inputs.Albedo, points);
        var third = renderer.ReadTexturePixels(inputs.Third, points);
        var diffuse = Lighting?.Diffuse is { } lit ? renderer.ReadTexturePixels(lit, points) : new Vector4[points.Count];
        var scene = renderer.ReadTexturePixels(sceneColor.Textures[0], points);
        return Enumerable.Range(0, points.Count).Select(i => (albedo[i], third[i], diffuse[i], scene[i])).ToArray();
    }

    /** Reads the lookup and the sky layer at screen points; call after Render. */
    public (Vector4 Lookup, Vector4 Sky)[] SampleSky(IReadOnlyList<(int X, int Y)> points)
    {
        var lookup = renderer.ReadTexturePixels(skyLookup.Textures[0], points);
        var sky = renderer.ReadTexturePixels(skyColor.Textures[0], points);
        return lookup.Zip(sky).ToArray();
    }

    // Viewer gain centre: the fallback gradient is scaled by the fill; atmosphere data centres two stops down.
    private float SkyFixedLog => AtmosphereActive ? AtmosphereSkyLog : MathF.Log2(SkyLayerScale / Math.Max(AtmosphereFill, 1));
    private const float AtmosphereSkyLog = -2;

    /** Viewer choice: the atmosphere in the bubble with the most drawable parts. */
    private static int DefaultAtmosphere(NativeScenePlan plan)
    {
        var parts = plan.Groups.SelectMany(g => g.Parts).GroupBy(p => p.Instance.BubbleHash).ToDictionary(g => g.Key, g => g.Count());
        int best = -1, bestParts = -1;
        for (int i = 0; i < plan.Atmospheres.Count; i++)
        {
            if (!NativeAtmosphereSettings.Parse(plan.Atmospheres[i].Settings).HasMode0Lookups) continue;
            int count = parts.GetValueOrDefault(plan.Atmospheres[i].BubbleHash);
            if (count > bestParts) { best = i; bestParts = count; }
        }
        return best;
    }

    /**
     * Shades geometry with technique 0 into sceneColor; needs lighting and the placed atmosphere's lookups.
     * @param skyScale Scene units per sky unit; the in-scatter uses it so fog matches the drawn sky.
     * @return False when a required input is missing; the viewer composite then shades geometry.
     */
    private bool RenderDeferred(NativeGBufferInputs gbufferInputs, double seconds, float ev, float skyScale)
    {
        bool studio = LightingMode == NativeLightingMode.Studio;
        if (!DeferredShadingEnabled || !studio && !AtmosphereActive || !LightingEnabled || LightingError is not null
            || Lighting?.Diffuse is null || ssao is null || cameraScope is null || Plan.AtmosphereSlots is null) return false;
        GpuTexture? lookup2D = studio ? absentTexture : null;
        if (!studio)
        {
            var settings = AtmosphereSettings();
            if (!atmosphereLookups.TryGetValue(settings.Lookups[2], out lookup2D))
            {
                DeferredError = "The atmosphere's 2D lookup did not decode.";
                return false;
            }
        }
        try
        {
            deferred ??= new NativeDeferredShading(renderer, Plan.Reader, states, Plan.Parameters);
            deferred.Render(new(gbufferInputs, Lighting.Diffuse, Lighting.Specular!, ssao.Textures[0], lookup2D!,
                studio ? absentTexture : skyLookup.Textures[0], cameraScope, cameraView, cameraProjection, seconds, ev, Plan.AtmosphereSlots,
                studio ? 1 : skyScale / MathF.Pow(2, ev), IblLighting, textures.GetValueOrDefault(Plan.FrameTexture24Tag), TimeOfDay),
                sceneColor);
            DeferredError = null;
            return true;
        }
        catch (Exception error) when (Recoverable(error) || error is InvalidOperationException or ArgumentException)
        {
            DeferredError = error.Message;
            AddDiagnostic($"Deferred shading: {error.Message}");
            return false;
        }
        finally { renderer.RestoreMainTarget(); }
    }

    /** Generates the lookup and draws the sky from the selected atmosphere; false selects the fallback. */
    private bool RenderAtmosphere(ID3D11ShaderResourceView depth)
    {
        atmosphereFrame = null;
        if ((uint)AtmosphereIndex >= (uint)Plan.Atmospheres.Count || cameraScope is null) return false;
        try
        {
            var settings = AtmosphereSettings();
            if (!atmosphereLookups.TryGetValue(settings.Lookups[0], out var firstLookup)
                || !atmosphereLookups.TryGetValue(settings.SecondLookup, out var secondLookup))
                throw new InvalidDataException("The atmosphere's 3D lookups did not decode.");
            atmosphere ??= new NativeAtmosphere(renderer, Plan.Reader,
                Plan.Reader.ReadTfxParameterDefaults(NativeScenePlan.RenderGlobalsTag), Plan.Parameters);
            var placed = Plan.Atmospheres[AtmosphereIndex];
            // The light direction comes from the time-of-day curves placed in the same list as the atmosphere.
            var light = Plan.TimesOfDay.TryGetValue(placed.PlacementListTag, out var timeOfDay)
                ? timeOfDay.Evaluate(TimeOfDay) : (Sky: Vector3.UnitZ, Light: Vector3.UnitZ);
            SkyDirection = light.Sky; LightDirection = light.Light;
            TimeOfDayFallback = timeOfDay is null;
            var frame = new NativeAtmosphereFrame(settings, firstLookup, secondLookup, cameraScope, cameraView,
                cameraProjection, TimeOfDay, light.Light, depth, Targets.Width, Targets.Height);
            atmosphereFrame = frame;
            renderer.ClearNativeColorTargets(skyColor, Vector4.Zero);
            // With no keyframe the native pass draws sky_no_atm, which is black.
            if (atmosphere.Generate(frame, skyLookup))
                atmosphere.DrawSky(frame, skyLookup.Textures[0], skyColor, frameConstants);
            AtmosphereError = null;
            return true;
        }
        catch (Exception error) when (Recoverable(error) || error is InvalidOperationException or ArgumentException)
        {
            AtmosphereError = error.Message;
            AddDiagnostic($"Atmosphere: {error.Message}");
            return false;
        }
        finally { renderer.RestoreMainTarget(); }
    }

    /**
     * One frame of 0x7FF742F15020 per decorator type; WriteDecoratorScope then writes the scope 8 rows.
     * The wind block comes from the payload package's wind settings; without them it stays zero,
     * as it is before the game loads them.
     */
    private void UpdateDecorators(Vector3 camera, double seconds)
    {
        decoratorCamera = camera;
        Array.Clear(decoratorLodCounts);
        if (decoratorTime == seconds) return;
        decoratorTime = seconds;
        DecoratorWindFallback = windFrame?.Settings is null ? decoratorMaterials[0].Decorator!.Payload.WindSource : null;
        var direction = windFrame?.Direction ?? Vector3.Zero;
        float strength = windFrame?.Strength ?? 0;
        // Each type's wind advances once per frame; materials of one type share its rows.
        foreach (var type in decoratorTypes)
        {
            // A ground type's wind is extern 47, which TFX reads; 0x7FF742F14EC0.
            if (type.Ground)
            {
                groundWinds[type.Tag] = NativeDecorators.GroundWind(type, direction, strength);
                continue;
            }
            if (!decoratorWinds.TryGetValue(type.Tag, out var state))
                decoratorWinds.Add(type.Tag, state = new SpeedTreeWind(type.WindParameters, type.WindOptions));
            decoratorRows[type.Tag] = state.Update((float)seconds, direction, strength);
        }
    }

    /**
     * Runs NativeDecoratorPayload.Evaluate for every payload the groups draw, in parallel.
     * Recording then finds each payload evaluated for this frame; a payload holds no shared state.
     */
    private void EvaluateDecoratorPayloads(IEnumerable<DrawGroup> drawn, Vector3 camera)
    {
        payloadSeen.Clear(); payloadQueue.Clear();
        foreach (var group in drawn)
            if (group.Decorator is not null)
                foreach (var payload in group.Payloads)
                    if (payloadSeen.Add(payload)) payloadQueue.Add(payload);
        int detail = FoliageDetail;
        if (payloadQueue.Count < MinimumParallelPayloads)
            foreach (var payload in payloadQueue) payload.Evaluate(camera, detail);
        else Parallel.ForEach(payloadQueue, payload => payload.Evaluate(camera, detail));
    }

    /**
     * Evaluates every decorator payload for the camera and fills DecoratorCellLevels, so a viewer can test
     * decorator parts before Render. Render then reuses the evaluation.
     */
    public void PrepareDecoratorLod(Vector3 camera)
    {
        DecoratorPayloadCells();
        int detail = FoliageDetail;
        void Prepare(NativeDecoratorPayload payload)
        {
            payload.Evaluate(camera, detail);
            payload.LevelMasks(decoratorCellLevels.AsSpan(decoratorCellBase[payload], payload.CellStarts.Length));
        }
        if (decoratorPayloads.Length < MinimumParallelPayloads) foreach (var payload in decoratorPayloads) Prepare(payload);
        else Parallel.ForEach(decoratorPayloads, Prepare);
    }

    /**
     * Lists the payloads the colour groups draw and gives each its first global cell.
     * Runtime groups only append or drop at the end, so a kept payload keeps its cells.
     */
    private void DecoratorPayloadCells()
    {
        if (decoratorPayloadGroups == groups.Count) return;
        decoratorPayloads = groups.Where(group => group.Decorator is not null).SelectMany(group => group.Payloads).Distinct().ToArray();
        decoratorCellBase.Clear();
        int cells = 0;
        foreach (var payload in decoratorPayloads) { decoratorCellBase.Add(payload, cells); cells += payload.CellStarts.Length; }
        decoratorCellLevels = new byte[cells];
        decoratorPayloadGroups = groups.Count;
    }

    // Every payload a colour group draws, for groups.Count groups, and each one's first global cell.
    private NativeDecoratorPayload[] decoratorPayloads = [];
    private readonly Dictionary<NativeDecoratorPayload, int> decoratorCellBase = new(ReferenceEqualityComparer.Instance);
    private int decoratorPayloadGroups = -1;
    private byte[] decoratorCellLevels = [];

    /** Per global cell, bit k set when LOD level k draws there; valid after PrepareDecoratorLod. */
    internal byte[] DecoratorCellLevels => decoratorCellLevels;

    /**
     * A colour part's global decorator cell and the levels it draws at, as a bit mask.
     * @return Cell -1 for a part that is no decorator.
     */
    public (int Cell, int Levels) HandleDecorator(int handle)
    {
        if (handles[handle].Group.Source.Parts[handles[handle].Part].Decorator is not { } part) return (-1, 0);
        DecoratorPayloadCells();
        int levels = 0;
        for (int level = Math.Max(0, part.FirstLevel); level <= Math.Min(NativeDecoratorPayload.MaxDrawnLevel, part.LastLevel); level++)
            levels |= 1 << level;
        return (decoratorCellBase[part.Payload] + part.Payload.RecordCell[part.Record], levels);
    }

    /** The inputs besides the camera that decorator LOD reads: foliage detail and placements. */
    public (int Detail, int Centers) DecoratorLodInputs => (FoliageDetail, centersRevision);

    /** A decorator part's LOD test for this frame; evaluates its payload first. */
    private bool Draws(DrawGroup group, int index, bool shadow)
    {
        var part = group.Source.Parts[index].Decorator!;
        part.Payload.Evaluate(decoratorCamera, FoliageDetail);
        return shadow ? part.Payload.DrawsShadow(part.Record, part.FirstLevel, part.LastLevel)
            : part.Payload.Draws(part.Record, part.FirstLevel, part.LastLevel);
    }

    /**
     * Drops gathered decorator parts that fail their LOD test, in parallel, and the orders left empty.
     * Kept parts stay in order; a group's range keeps its offset and shrinks its count.
     */
    private void KeepDrawnDecorators(Vector3 eye)
    {
        keepGroups.Clear();
        foreach (int order in activeOrders) if (groups[order].Decorator is not null) keepGroups.Add(groups[order]);
        gatheredDecorators = keepGroups.Count != 0;
        gatheredEye = eye; gatheredDetail = FoliageDetail; gatheredCentersRevision = centersRevision;
        if (!gatheredDecorators) return;
        // Draws only reads payloads evaluated here.
        EvaluateDecoratorPayloads(keepGroups, eye);
        void Keep(DrawGroup group)
        {
            var parts = visibleGroupParts.AsSpan(visibleGroupOffsets[group.Order], visibleGroupCounts[group.Order]);
            int kept = 0;
            if (group.CellStarts is { } cells)
            {
                var decorator = group.Decorator!;
                foreach (int part in parts)
                    if (decorator.Payload.DrawsCell(cells[part], decorator.FirstLevel, decorator.LastLevel)) parts[kept++] = part;
            }
            else foreach (int part in parts) if (Draws(group, part, false)) parts[kept++] = part;
            visibleGroupCounts[group.Order] = kept;
        }
        if (keepGroups.Count < MinimumParallelPayloads) foreach (var group in keepGroups) Keep(group);
        else Parallel.ForEach(keepGroups, Keep);
        activeOrders.RemoveAll(order => visibleGroupCounts[order] == 0);
    }

    private readonly List<DrawGroup> keepGroups = [];
    // Below this many payloads the evaluation runs on the calling thread.
    private const int MinimumParallelPayloads = 8;
    private readonly HashSet<NativeDecoratorPayload> payloadSeen = new(ReferenceEqualityComparer.Instance);
    private readonly List<NativeDecoratorPayload> payloadQueue = [];
    // The eye, foliage detail and placements the gathered decorator parts were tested for; see Render.
    private bool gatheredDecorators;
    private Vector3 gatheredEye;
    private int gatheredDetail = -1, gatheredCentersRevision = -1;
    // The last recording of the per-frame list and its inputs; see Render.
    private bool perFrameReusable;
    private Vector3 perFrameEye;
    private int perFrameDetail = -1, perFramePose = -1;
    private readonly List<MaterialGpu> perFrameMaterials = [];
    private readonly int[] perFrameLodCounts = new int[5];
    /** Times the decorator and live-slot draws were recorded; a still camera reuses the last recording. */
    public int PerFrameRecordings { get; private set; }

    /** Writes a scope 8 buffer once per decorator time, before the first draw that reads it. */
    private void WriteDecoratorScope(MaterialGpu material)
    {
        if (material.DecoratorScope is not { } scope || scope.Time == decoratorTime) return;
        // A type added since the last wind step keeps its zero rows until the time moves.
        if (!decoratorRows.TryGetValue(scope.Part.Type.Tag, out var wind)) return;
        Span<Vector4> rows = stackalloc Vector4[NativeDecorators.ScopeRows];
        NativeDecorators.ScopeConstants(scope.Part, wind, rows);
        renderer.UpdateNativeConstantBuffer(scope.Buffer, MemoryMarshal.AsBytes(rows));
        scope.Time = decoratorTime;
    }

    private void AddSorted(DrawGroup group, Vector3 eye)
    {
        foreach (int part in visibleGroupParts.AsSpan(visibleGroupOffsets[group.Order], visibleGroupCounts[group.Order]))
            sorted.Add((Vector3.DistanceSquared(eye, group.Centers[part]), group, part));
    }

    /**
     * Stable scatter retains the original placement order within every visible group; then decorator LOD.
     * A long list is cut into chunks: each chunk counts its parts and writes them from its own cursors.
     */
    private void GatherVisibleParts(List<int> visible, Vector3 eye)
    {
        activeGroups.Clear(); activeOrders.Clear();
        Array.Clear(visibleGroupCounts);
        if (visibleGroupParts.Length < visible.Count) Array.Resize(ref visibleGroupParts, visible.Count);
        var enabled = enabledStages;
        Array.Clear(enabled);
        foreach (int stage in EnabledStages) if ((uint)stage < enabled.Length) enabled[stage] = true;
        int groupCount = visibleGroupCounts.Length;
        int chunks = visible.Count < GatherHandlesPerTask * 2 ? 1 : (visible.Count + GatherHandlesPerTask - 1) / GatherHandlesPerTask;
        // Rows are zero between gathers; each chunk zeroes the entries it touched.
        if (chunkCursors.Length < chunks * groupCount) chunkCursors = new int[chunks * groupCount];
        while (chunkGroups.Count < chunks) chunkGroups.Add([]);
        if (groupCursors.Length < groupCount) groupCursors = new int[groupCount];
        var orders = handleOrders; var stages = groupStages; var counts = visibleGroupCounts; var cursors = chunkCursors;
        var starts = groupStarts; var parts = visibleGroupParts;
        void Count(int chunk)
        {
            var row = cursors.AsSpan(chunk * groupCount, groupCount);
            var touched = chunkGroups[chunk];
            touched.Clear();
            foreach (int handle in Chunk(visible, chunk, chunks))
            {
                int order = orders[handle];
                if (enabled[stages[order]] && row[order]++ == 0) touched.Add(order);
            }
        }
        void Scatter(int chunk)
        {
            var row = cursors.AsSpan(chunk * groupCount, groupCount);
            foreach (int handle in Chunk(visible, chunk, chunks))
            {
                int order = orders[handle];
                if (counts[order] != 0) parts[row[order]++] = handle - starts[order];
            }
            foreach (int order in chunkGroups[chunk]) row[order] = 0;
        }
        if (chunks == 1) Count(0); else Parallel.For(0, chunks, Count);
        for (int chunk = 0; chunk < chunks; chunk++)
            foreach (int order in chunkGroups[chunk]) counts[order] += cursors[chunk * groupCount + order];
        int nextPart = 0;
        for (int order = 0; order < groupCount; order++)
        {
            if (counts[order] == 0) continue;
            activeOrders.Add(order);
            visibleGroupOffsets[order] = groupCursors[order] = nextPart;
            nextPart += counts[order];
        }
        // Each chunk starts a group after the parts the chunks before it hold.
        for (int chunk = 0; chunk < chunks; chunk++)
            foreach (int order in chunkGroups[chunk])
            {
                ref int cursor = ref cursors[chunk * groupCount + order];
                int count = cursor;
                cursor = groupCursors[order];
                groupCursors[order] += count;
            }
        if (chunks == 1) Scatter(0); else Parallel.For(0, chunks, Scatter);
        var source = CollectionsMarshal.AsSpan(visible);
        recordedVisible.Clear(); recordedVisible.AddRange(source);
        recordedStages.Clear(); recordedStages.UnionWith(EnabledStages);
        hasRecordedVisibility = true;
        KeepDrawnDecorators(eye);
        foreach (int order in activeOrders) activeGroups.Add(groups[order]);
    }

    /** One gather chunk of the visible handles; a single chunk is the whole list. */
    private static ReadOnlySpan<int> Chunk(List<int> list, int chunk, int chunks)
    {
        var all = CollectionsMarshal.AsSpan(list);
        int start = chunk * GatherHandlesPerTask;
        return chunks == 1 ? all : all[start..Math.Min(all.Length, start + GatherHandlesPerTask)];
    }

    // Handles per parallel task of GatherVisibleParts.
    private const int GatherHandlesPerTask = 65536;
    // Per gather chunk and group: the chunk's part count, then its first part in visibleGroupParts.
    private int[] chunkCursors = [];
    // The groups each gather chunk touched, and each group's next free part while chunks are placed.
    private readonly List<List<int>> chunkGroups = [];
    private int[] groupCursors = [];
    private readonly bool[] enabledStages = new bool[RenderNames.StageNames.Count];

    /** One reserved opaque draw: its parts start at Part in visibleGroupParts. */
    private readonly record struct OpaqueFill(DrawGroup Group, int Part, int Count, int Vector);
    private readonly List<OpaqueFill> opaqueFills = [];
    // Draws per parallel task; the copy per draw is small.
    private const int OpaqueFillsPerTask = 256;

    /** Copies the CB11 vectors of every reserved opaque draw; draws own disjoint ranges. */
    private void FillOpaque()
    {
        int total = opaqueFills.Count;
        if (total <= OpaqueFillsPerTask) { FillOpaqueRange(0, total); return; }
        Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, total, OpaqueFillsPerTask),
            range => FillOpaqueRange(range.Item1, range.Item2));
    }

    private void FillOpaqueRange(int from, int to)
    {
        var fills = CollectionsMarshal.AsSpan(opaqueFills);
        for (int i = from; i < to; i++)
        {
            var fill = fills[i];
            int stride = fill.Group.Stride;
            var source = fill.Group.Instances.AsSpan();
            var target = drawList.Vectors(fill.Vector, fill.Count * stride);
            var parts = visibleGroupParts.AsSpan(fill.Part, fill.Count);
            for (int k = 0; k < parts.Length; k++)
                source.Slice(parts[k] * stride, stride).CopyTo(target.Slice(k * stride, stride));
        }
    }

    /** Camera and material buffers stay live; only immutable instance vectors are cached. */
    private void Record(int pass, DrawGroup group, ReadOnlySpan<int> parts) =>
        Record(pass is PassForward or PassSky ? forwardDrawList : group.PerFrame ? runtimeDrawList : drawList, pass, group, parts, false);

    /**
     * @param shadow True for a caster part, which draws in the shadow LOD window. Other decorator parts come
     * from visibleGroupParts, which holds only parts that pass their LOD test.
     */
    private void Record(NativeDrawList list, int pass, DrawGroup group, ReadOnlySpan<int> parts, bool shadow)
    {
        if (group.Decorator is not null) { RecordDecorators(list, pass, group, parts, shadow); return; }
        var dye = group.Dyes?[parts[0]];
        int stride = group.Stride;
        var span = list.Add(pass, group.Pipeline, group.Mesh, group.Material.Bindings, group.Scalars?.View, (uint)parts.Length,
            stride, group.Dyes is not null, dye);
        var source = group.Instances.AsSpan();
        for (int i = 0; i < parts.Length; i++)
            source.Slice(parts[i] * stride, stride).CopyTo(span.Slice(i * stride, stride));
    }

    private readonly Dictionary<uint, Vector4> groundWinds = [];
    private NativeWindFrame? windFrame;
    private double windTime = double.NaN;
    // Ground draws put the eight CB11 rows before their records: (4096 - 8) / 3 records fit one window.
    private const int GroundRecordsPerDraw = 1362;
    // Drawn ground cells of one group: payload, first record and record count.
    private readonly List<(NativeDecoratorPayload Payload, int Record, int Count)> groundRanges = [];
    private int[] keptParts = [];

    /**
     * Decorator parts draw only when their cell's LOD index is in their level's window; 0x7FF742DFE1D0.
     * Shadow casters read the same index through the shadow window.
     */
    private void RecordDecorators(NativeDrawList list, int pass, DrawGroup group, ReadOnlySpan<int> parts, bool shadow)
    {
        if (group.Decorator!.Payload.Ground) { RecordGroundDecorators(list, pass, group, parts, shadow); return; }
        if (keptParts.Length < parts.Length) keptParts = new int[Math.Max(parts.Length, DecoratorsPerDraw)];
        var kept = keptParts.AsSpan(0, parts.Length);
        int count = 0;
        for (int i = 0; i < parts.Length; i++)
            if (!shadow || Draws(group, parts[i], true)) kept[count++] = parts[i];
        if (count == 0) return;
        var level = group.Source.Parts[parts[0]].Decorator!.FirstLevel;
        if (!shadow) decoratorLodCounts[Math.Min(level, decoratorLodCounts.Length - 1)] += count;
        WriteDecoratorScope(group.Material);
        const int stride = NativeDecorators.InstanceStreams;
        var span = list.Add(pass, group.Pipeline, group.Mesh, group.Material.Bindings, null, (uint)count,
            stride, instanceStreams: stride);
        for (int i = 0; i < count; i++)
        {
            var part = group.Source.Parts[kept[i]].Decorator!;
            var payload = part.Payload;
            var target = span.Slice(i * stride, stride);
            payload.StaticVectors().Slice(part.Record * stride, stride).CopyTo(target);
            var (fade, blend) = payload.FadeAndBlend(part.Record);
            target[1] = new(fade, blend, 0, 0);
        }
    }

    /** Ground cells: every record of a drawn cell, after the scope 2 rows CB11 reads; 0x7FF742DFDEC0. */
    private void RecordGroundDecorators(NativeDrawList list, int pass, DrawGroup group, ReadOnlySpan<int> parts, bool shadow)
    {
        var ranges = groundRanges;
        ranges.Clear();
        int total = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            if (shadow && !Draws(group, parts[i], true)) continue;
            var part = group.Source.Parts[parts[i]].Decorator!;
            ranges.Add((part.Payload, part.Record, part.Count));
            total += part.Count;
        }
        if (total == 0) return;
        WriteDecoratorScope(group.Material);
        var first = group.Source.Parts[parts[0]].Decorator!;
        if (!shadow) decoratorLodCounts[Math.Min(first.FirstLevel, decoratorLodCounts.Length - 1)] += total;
        var rigid = first.Type.RigidRows;
        const int stride = NativeDecorators.InstanceStreams;
        int range = 0, within = 0;
        for (int start = 0; start < total; start += GroundRecordsPerDraw)
        {
            int count = Math.Min(GroundRecordsPerDraw, total - start);
            var span = list.Add(pass, group.Pipeline, group.Mesh, group.Material.Bindings, null, (uint)count, stride,
                instanceStreams: stride, streamBase: rigid.Length);
            rigid.CopyTo(span);
            // A ground record's vectors never change, so whole cells copy as one block.
            for (int written = 0; written < count;)
            {
                var (payload, record, length) = ranges[range];
                int take = Math.Min(length - within, count - written);
                payload.StaticVectors().Slice((record + within) * stride, take * stride)
                    .CopyTo(span.Slice(rigid.Length + written * stride));
                written += take; within += take;
                if (within == length) { range++; within = 0; }
            }
        }
    }

    /** Lighting runs after the G-buffer resolve; a refused frame composites albedo only. */
    private void RenderLighting(NativeGBufferInputs inputs, ViewerCamera camera, double simulationSeconds)
    {
        LightingCheck = null;
        SkyLightingActive = false;
        StudioLightingActive = false;
        LocalCubemapsActive = false;
        LocalLightsActive = false;
        if (!LightingEnabled || cameraScope is null) return;
        try
        {
            ambient ??= new NativeAmbientLighting(renderer, Plan.Reader,
                Plan.Reader.ReadTfxParameterDefaults(NativeScenePlan.RenderGlobalsTag), states,
                parameterOverrides: ambientOverrides);
            var targets = gbuffer.WriteTargets;
            if (ssao is null || ssao.Width != targets.Width || ssao.Height != targets.Height)
            {
                ssao?.Dispose();
                ssao = renderer.CreateNativeTargets(targets.Width, targets.Height, [Format.R8G8_UNorm], Format.D16_UNorm);
                // SSAO mode 0 clears target 48 to one before the lighting pass reads it.
                renderer.ClearNativeTargets(ssao, Vector4.One, 1);
                renderer.RestoreMainTarget();
            }
            float ev = Math.Clamp(ExposureEv, NativeAmbientLighting.MinimumExposureEv, NativeAmbientLighting.MaximumExposureEv);
            bool studio = LightingMode == NativeLightingMode.Studio;
            if (studio || SkyLightingEnabled && AtmosphereActive && atmosphereFrame is not null)
            {
                try
                {
                    skyLighting ??= new NativeSkyLighting(renderer, Plan.Reader,
                        Plan.Reader.ReadTfxParameterDefaults(NativeScenePlan.RenderGlobalsTag), Plan.Parameters, states);
                    if (studio)
                    {
                        if (Plan.AssetPreview)
                        {
                            studioKey = Vector3.Normalize(camera.Right * StudioKeyView.X + camera.Up * StudioKeyView.Y
                                - camera.Forward * StudioKeyView.Z);
                            studioEnvironment ??= Own(new NativeStudioEnvironment(renderer));
                            studioReflection = studioEnvironment.Update(studioKey);
                        }
                        else studioReflection ??= Own(renderer.CreateTexture(1, 1, Format.R32G32B32A32_Float,
                            MemoryMarshal.AsBytes(new Vector4[] { new(StudioEnvironmentRadiance, StudioEnvironmentRadiance,
                                StudioEnvironmentRadiance, 1) }.AsSpan()), 16));
                        skyLighting.Render(inputs, cameraScope, cameraView, cameraProjection, simulationSeconds, ev, studioReflection);
                        StudioLightingActive = true;
                    }
                    else if (skyLighting.Generate(atmosphereFrame!))
                    {
                        skyLighting.Render(inputs, cameraScope, cameraView, cameraProjection, simulationSeconds, ev);
                        SkyLightingActive = true;
                        if (LocalCubemapsEnabled && localCubemapDraws.Count > 0)
                        {
                            try
                            {
                                localCubemaps ??= new NativeCubemapLighting(renderer, Plan.Reader, states, Plan.Parameters);
                                localCubemaps.Render(localCubemapDraws, inputs, targets, skyLighting.Diffuse!, skyLighting.Ibl!,
                                    skyLighting.TemporaryAo!, cameraScope, cameraView, cameraProjection, simulationSeconds, ev, camera.NearPlane);
                                LocalCubemapsActive = true;
                                LocalCubemapError = null;
                            }
                            catch (Exception error) when (Recoverable(error) || error is InvalidOperationException or ArgumentException)
                            {
                                LocalCubemapError = error.Message;
                                AddDiagnostic($"Local cubemaps: {error.Message}");
                            }
                        }
                    }
                    SkyLightingError = null;
                }
                catch (Exception error) when (Recoverable(error) || error is InvalidOperationException or ArgumentException)
                {
                    SkyLightingError = error.Message;
                    AddDiagnostic($"Sky lighting: {error.Message}");
                }
            }
            var lightingInputs = new NativeAmbientLightingInputs(inputs.Third, inputs.Normal, inputs.Depth, ssao.Textures[0], cameraScope,
                cameraView, cameraProjection, new(camera.NearPlane, camera.FarPlane, false), simulationSeconds, ev, TimeOfDay);
            var diffuseBase = LocalCubemapsActive ? localCubemaps!.Diffuse : EnvironmentLightingActive ? skyLighting!.Diffuse : null;
            if (RenderSun(camera, inputs, lightingInputs, targets) is not { } sunInputs)
                ambient.Render(lightingInputs, targets, diffuseBase);
            else
                sunLighting!.Render(lightingInputs, targets, diffuseBase, sunInputs);
            LightingError = null;
            RenderLocalLights(lightingInputs, Lighting!, camera);
        }
        catch (Exception error) when (Recoverable(error) || error is InvalidOperationException)
        {
            LightingError = error.Message;
            AddDiagnostic($"Ambient lighting: {error.Message}");
        }
    }

    /** Readback compares the lit frame with the CPU pixel program; call only after Render. */
    public NativeAmbientLightingCheck ValidateLighting()
    {
        if (Lighting is not { } lighting || LightingError is not null)
            throw new InvalidOperationException(LightingError ?? "Ambient lighting has not rendered.");
        LightingCheck = lighting.Validate(gbuffer.WriteTargets);
        return LightingCheck;
    }

    private Vector4 fogWrittenKey;
    private byte[]? fogWrittenTable;
    private int fogWrittenReaders;

    /** The authored stage layouts keep unresolved pass values out of readable shader words. */
    private void UpdateView(ViewerCamera camera, float aspect)
    {
        cameraView = camera.View;
        cameraProjection = camera.Projection(aspect);
        var view = NativeViewInputs.EvaluateCameraScope(Plan.ViewScope,
            checked((ushort)Targets.Width), checked((ushort)Targets.Height), cameraView, cameraProjection);
        cameraScope = view;
        // Forward depth d = M33 / M34 + M43 / w, so 1 / w = d / M43 - M33 / (M34 * M43).
        var p = cameraProjection;
        var linear = new Vector4(-p.M33 / (p.M34 * p.M43), 1 / p.M43, 0, 0);
        var fog = Plan.AtmosphereSlots is { } slots
            ? NativeTransparentScope.HeightFog(slots, camera.Position.Z) : new Vector4(0, 0, 1e-4f, 1e-4f);
        var transparent = NativeTransparentScope.Evaluate(Plan.TransparentScope, linear, fog, Plan.TransparentLighting);
        var sky = NativeTransparentScope.Evaluate(Plan.TransparentScope, new(1e-30f, 0, 0, 0), fog, Plan.TransparentLighting);
        renderer.UpdateNativeConstantBuffer(transparentConstants, MemoryMarshal.AsBytes(transparent.AsSpan()));
        renderer.UpdateNativeConstantBuffer(skyScopeConstants, MemoryMarshal.AsBytes(sky.AsSpan()));
        // Object fog depends only on the camera position, the time of day and the fog table.
        var fogTable = fogReaders.Count != 0 ? ActiveFogTable() : null;
        var fogKey = new Vector4(camera.Position, TimeOfDay);
        bool fogChanged = !MemoryMarshal.AsBytes(new ReadOnlySpan<Vector4>(in fogKey))
                .SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<Vector4>(in fogWrittenKey)))
            || !ReferenceEquals(fogTable, fogWrittenTable) || fogReaders.Count != fogWrittenReaders;
        if (fogReaders.Count != 0 && Plan.AtmosphereSlots is { } fogSlots && fogChanged)
        {
            var viewFog = NativeObjectFog.ReadViewFog(fogSlots, camera.Position, TimeOfDay, fogTable);
            foreach (var reader in fogReaders)
                renderer.UpdateNativeConstantBuffer(reader.Fog!, MemoryMarshal.AsBytes(NativeObjectFog.Evaluate(viewFog,
                    reader.FogBounds.Minimum, reader.FogBounds.Maximum).AsSpan()));
            (fogWrittenKey, fogWrittenTable, fogWrittenReaders) = (fogKey, fogTable, fogReaders.Count);
        }
        renderer.UpdateNativeConstantBuffer(viewConstants, MemoryMarshal.AsBytes(view.Vertex.AsSpan()));
        renderer.UpdateNativeConstantBuffer(pixelViewConstants, MemoryMarshal.AsBytes(view.PixelRows.AsSpan()));
    }

    /** The fog colour table of the atmosphere that drives the sky; null when none is placed. */
    private byte[]? ActiveFogTable()
    {
        if ((uint)AtmosphereIndex >= (uint)Plan.Atmospheres.Count) return null;
        uint tag = AtmosphereSettings().FogTable;
        return Plan.FogTables.GetValueOrDefault(tag);
    }

    /**
     * Copies one output to the swap chain with the native depth.
     * @param output Index into OutputNames; FinalOutput is the shaded image.
     */
    public void Present(int output, int channel, bool srgb, float exposure)
    {
        var image = DisplayTexture(output, channel, srgb, exposure);
        composite.Present(image, gbuffer.Inputs!.Depth, true);
    }

    /** The returned texture is borrowed until the next render, resize, or disposal. */
    public GpuTexture DisplayTexture(int output = FinalOutput, int channel = 0,
        bool srgb = true, float exposure = 0)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var inputs = gbuffer.Inputs ?? throw new InvalidOperationException("The native scene has not rendered.");
        GpuTexture image;
        if (output == FinalOutput)
        {
            finalCombine ??= Own(new NativeFinalCombine(renderer, Plan.Reader));
            image = finalCombine.Render(exposedColor.Textures[0]);
            if (Plan.AssetPreview)
            {
                if (antialiased is null || antialiased.Width != image.Width || antialiased.Height != image.Height)
                {
                    antialiased?.Dispose();
                    antialiased = renderer.CreateNativeTargets(image.Width, image.Height, Format.R16G16B16A16_Float);
                }
                composite.Antialias(image, antialiased);
                renderer.RestoreMainTarget();
                image = antialiased.Textures[0];
            }
        }
        else
        {
            var source = output switch
            {
                0 => inputs.Albedo,
                1 => inputs.Normal,
                2 => inputs.Third,
                3 => Lighting?.Diffuse,
                4 => Lighting?.Specular,
                6 => sceneColor.Textures[0],
                _ => throw new ArgumentOutOfRangeException(nameof(output))
            } ?? throw new InvalidOperationException(LightingError ?? "Lighting has not rendered; this output is empty.");
            image = display.Render(source, channel, srgb, exposure);
        }
        return image;
    }

    /** Size-dependent targets follow the swap chain; geometry and material allocations remain shared. */
    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0 || gbuffer is not null && Targets.Width == width && Targets.Height == height) return;
        var nextGbuffer = new NativeGBufferTargets(renderer, width, height);
        NativeRenderTargets? nextColor = null, nextExposed = null, nextSky = null, nextLookup = null, nextRefraction = null,
            nextForward = null;
        GpuTexture? nextUv = null;
        NativeOutputDisplay nextDisplay;
        try
        {
            nextColor = renderer.CreateNativeTargetsOnDepth(
                [new NativeColorTargetFormat(Format.R16G16B16A16_Float, Format.R16G16B16A16_Float, Format.R16G16B16A16_Float)],
                nextGbuffer.WriteTargets);
            nextSky = renderer.CreateNativeTargetsOnDepth(
                [new NativeColorTargetFormat(Format.R16G16B16A16_Float, Format.R16G16B16A16_Float, Format.R16G16B16A16_Float)],
                nextGbuffer.WriteTargets);
            nextExposed = renderer.CreateNativeTargets(width, height, Format.R16G16B16A16_Float);
            nextLookup = renderer.CreateNativeTargets(width, height, Format.R16G16B16A16_Float);
            nextRefraction = renderer.CreateNativeTargets(width, height, Format.R16G16B16A16_Float);
            nextForward = renderer.CreateNativeTargetsOnDepth(
                [new NativeColorTargetFormat(Format.R16G16B16A16_Float, Format.R16G16B16A16_Float, Format.R16G16B16A16_Float)],
                nextGbuffer.WriteTargets);
            nextUv = renderer.CreateTexture(width, height, Format.R16G16_UNorm, NativeWater.ScreenUv(width, height), width * 4);
            nextDisplay = new(renderer, width, height);
        }
        catch
        {
            nextUv?.Dispose(); nextRefraction?.Dispose(); nextForward?.Dispose();
            nextLookup?.Dispose(); nextExposed?.Dispose(); nextSky?.Dispose(); nextColor?.Dispose(); nextGbuffer.Dispose(); throw;
        }
        screenUv?.Dispose(); refraction?.Dispose(); forwardColor?.Dispose();
        screenUv = nextUv; refraction = nextRefraction; forwardColor = nextForward;
        display?.Dispose();
        // Ambient and sun lighting resize their own targets when the depth owner changes.
        ssao?.Dispose(); ssao = null;
        exposedColor?.Dispose(); skyColor?.Dispose(); skyLookup?.Dispose(); sceneColor?.Dispose(); gbuffer?.Dispose();
        gbuffer = nextGbuffer; sceneColor = nextColor; skyColor = nextSky; skyLookup = nextLookup;
        exposedColor = nextExposed; display = nextDisplay;
    }

    private static bool Recoverable(Exception error) => NativeScenePlan.Recoverable(error)
        || error is SharpGen.Runtime.SharpGenException;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        display?.Dispose();
        ambient?.Dispose(); localCubemaps?.Dispose(); skyLighting?.Dispose(); ssao?.Dispose(); atmosphere?.Dispose(); deferred?.Dispose();
        sunLighting?.Dispose(); DisposeShadows(); DisposeLocalLights(); antialiased?.Dispose();
        exposedColor?.Dispose(); skyColor?.Dispose(); skyLookup?.Dispose(); sceneColor?.Dispose(); gbuffer?.Dispose();
        screenUv?.Dispose(); refraction?.Dispose(); forwardColor?.Dispose();
        for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
        owned.Clear();
    }
}
