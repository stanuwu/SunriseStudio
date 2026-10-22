using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

/** The atmosphere a scene's sky starts with, the time-of-day placement beside it, and the time of day at start. */
public sealed record NativeSkySource(PlacedAtmosphere? Atmosphere, PlacedTimeOfDay? Time, float StartTime);

public sealed record NativeSceneMaterial(MaterialExecutionData Execution,
    NativeRenderStateDescriptions States, bool PixelViewKnown, IReadOnlySet<GameShaderStage> FrameStages, int TargetCount,
    NativeMeshKind Kind, NativeEntityPalette Palette = NativeEntityPalette.None)
{
    /** Transparent-scope slots this material reads: CB2 per stage, and pixel textures by slot and dimension. */
    public IReadOnlySet<GameShaderStage> ScopeConstantStages { get; init; } = new HashSet<GameShaderStage>();
    public IReadOnlyDictionary<uint, string> ScopeTextures { get; init; } = new Dictionary<uint, string>();
    /** True when the pixel shader reads scope 14 (transparent_advanced) at CB8. */
    public bool ReadsAdvancedScope { get; init; }
    /** True when TFX binds frame resources, such as the water inputs; the renderer supplies their views. */
    public bool ExternResources { get; init; }
    /** True when TFX reads extern 67, which holds one water object's position and scale. */
    public bool ReadsWaterObject { get; init; }
    /** True when TFX reads extern 86, the part's gear-dye bit; each bit value is its own material. */
    public bool ReadsGearDye { get; init; }
    public int GearDyeBit { get; init; }
    /** Dye scopes and plate textures this material reads; null when it reads none. */
    public NativeGearMaterial? Gear { get; init; }
    /** Declared slots whose material row has no texture; they bind a null view, which reads zero. */
    public IReadOnlyDictionary<NativeBindingKey, string> NullTextures { get; init; } = new Dictionary<NativeBindingKey, string>();
    /** Stencil reference of a draw whose state uses stencil; null when the state has no stencil. */
    public uint? StencilReference { get; init; }
    /** True when the vertex shader reads the SDSM bias and scale at t2, as shadow casters do. */
    public bool ReadsShadowScope { get; init; }
    /** True when TFX reads object channels (opcode 0x4D); each channel table is then its own material. */
    public bool ReadsObjectChannels { get; init; }
    /** The entity model's channel table; null reads (1,1,1,1) for every channel. */
    public IReadOnlyList<Vector4>? ObjectChannels { get; init; }
    /** ObjectChannels as text, so equal tables share a group; empty when it is null. */
    public string ObjectChannelKey { get; init; } = "";
}
public enum NativeBindingSource { Authored, IsolatedPreview }
/** Constants are the part's CB11 rows; Scalars is the VS t0 AO buffer, absent for entities. */
public sealed record NativeScenePart(MeshInstance Instance, Vector4[] Constants, NativeVertexStream? Scalars,
    Vector3 Center, float Radius)
{
    public NativeBindingSource BindingSource { get; init; }
    // A placement update rewrites these in place, so groups and renderer handles keep the part.
    public Vector4[] Constants { get; internal set; } = Constants;
    public Vector3 Center { get; internal set; } = Center;
    public float Radius { get; internal set; } = Radius;
    /** World bounds of the part; the plan bounds are their union. */
    internal Vector3 Minimum { get; set; }
    internal Vector3 Maximum { get; set; }
    /** Decorator record, mesh and LOD level; null for other parts. */
    public NativeDecoratorPart? Decorator { get; init; }
}
public sealed record NativeSceneGroup(string GeometryKey, string PipelineKey, NativeMeshData Mesh,
    NativeSceneMaterial Material, uint ScalarDataTag, int RenderStage, bool Sky, IReadOnlyList<NativeScenePart> Parts)
{
    public NativeMeshKind Kind => Mesh.Kind;
    /** Object-space bind stream 0 of a cloth display mesh; null uploads the authored stream. */
    public byte[]? ClothDisplayStream { get; init; }
}

/** Preparation rejects unknown bindings before decoding texture pixels or creating GPU resources. */
public sealed class NativeScenePlan
{
    // Preflight bounds the sum of shared texture payloads before any texture pixels are decoded.
    public const long DefaultTextureBudgetBytes = 512L * 1024 * 1024;
    // The installed render-globals launch record.
    public const uint RenderGlobalsTag = NativeInspectionMaterialInputs.RenderGlobalsTag;
    public const string PassPolicy = "Inspection forward-Z. Each stage takes its native pass states for blend, depth and culling; authored state overrides replace them. Stage 0 writes depth; decal, additive-decal and transparent stages test it without writing. The viewer keeps no stencil, so the stencil test the pass word adds is dropped. Frame flags 0x425B8/0x425B9 are inactive, matching their native constructor. TFX uses authored defaults after native normalization; runtime keyed overrides are not supplied. Decal stages read the normals from before those stages as extern 44 resource 1, and extern 44 vector 2 holds the image values (0.03, 0); resource 0 and vector 1 are not supplied. Other unavailable frame values stay unknown. Native lighting remains unverified. Object channels (TFX 0x4D) read each entity model's rest values from its value provider; the opened entity reads the values set in the entity view; a channel with no settled rest value reads (1,1,1,1) in maps and (0,0,0,0) in asset previews. Transparents read scope 13 CB2: height fog at the camera and hemisphere irradiance from the authored render globals. Scope 14 CB8 supplies centre and endpoint fog from each part's bounding sphere; caller values in row 3 W and row 4 X/Z/W stay unknown.";
    // Viewer value of an object channel with no table or no settled rest value.
    public static readonly Vector4 ObjectChannelDefault = NativeInspectionMaterialInputs.ObjectChannelDefault;
    /**
     * An asset preview reads object channels it has no table for as zero: they carry gameplay state such as an
     * exotic's heat or charge, which an idle item shows as off. Maps keep the inspection default of one.
     */
    public static readonly Vector4 AssetChannelDefault = Vector4.Zero;
    // Colour stages the viewer draws: generate_gbuffer, decals, investment_decals, decals_additive, transparents.
    public static readonly IReadOnlySet<int> DrawnStages = new HashSet<int> { 0, 1, 2, 6, 7 };
    // These stages write the G-buffer before lighting; the others draw into the lit color after it.
    public static readonly IReadOnlySet<int> GBufferStages = new HashSet<int> { 0, 1, 2 };
    // shadow_generate: caster parts draw depth into the sun cascades, never into a colour target.
    public const int ShadowStage = 3;
    // Scope 15, sdsm_bias_and_scale_textures, binds extern 60 resource 0 at vertex t2 in the shadow stage.
    public const uint ShadowScopeSlot = 2;
    // The transparents stage submits with scope 13 (transparent) and extern 39 set; 0x7FF742E91A80.
    // Its submitters bind one colour target, so pixel outputs past o0 are discarded.
    public const int TransparentsStage = 7;
    // Scope 13 binds extern 3 resource 7 at pixel t10 and extern 39 resources 0 to 8 at t11 to t19.
    public const uint SceneDepthSlot = 10;
    public const uint FirstAbsentScopeSlot = 11, LastAbsentScopeSlot = 19;
    // Gear object i's atlas tags are offset by i * this stride; one set holds at most a few atlases.
    private const uint AtlasTagStride = 0x1000;
    // Decorator parts whose payload, type, model or AO data does not resolve.
    public const string DecoratorSkip = "Decorator data is unavailable";
    // Extern 39 resource 0 at pixel t11 is the screen-space atmosphere lookup; the viewer binds its gradient there.
    public const uint AtmosphereLookupSlot = 11;
    // Extern 44 resource 1 in the decal stages: G-buffer normals from before those stages; 0x7FF742E88630.
    public static readonly TfxResource PreDecalNormals = new("pre-decal normals", Vortice.Direct3D.ShaderResourceViewDimension.Texture2D);
    // Extern 3 resource 7 is target 25, depth_stencil_read: the depth copy read after the G-buffer stages.
    public static readonly TfxResource SceneDepth = new("scene depth", Vortice.Direct3D.ShaderResourceViewDimension.Texture2D);
    // Extern 44 vector 2 is the floats at 0x7FF743C94B08 and 0x7FF744B1B978; no code writes them.
    public static readonly Vector4 DecalVector2 = new(0.03f, 0, 0, 0);

    public required GameMaterialReader Reader { get; init; }
    public required GameScopeExecution ViewScope { get; init; }
    /** Scope 13 (transparent) and its extern 39 lighting vectors 6 to 8, which do not follow the camera. */
    public required GameScopeExecution TransparentScope { get; init; }
    public required IReadOnlyList<Vector4> TransparentLighting { get; init; }
    public required TfxParameterLookup Parameters { get; init; }
    /** Explicit replacements for authored parameter defaults; the runtime producers are not reproduced. */
    public IReadOnlyDictionary<uint, Vector4>? ParameterOverrides { get; init; }
    public required IReadOnlyList<NativeSceneGroup> Groups { get; init; }
    /** Decoded payloads; the renderer empties this after upload so the pixels are not kept twice. */
    public required Dictionary<uint, TextureData> Textures { get; init; }
    public uint FrameTexture24Tag { get; init; }
    /** The ocean wave settings extern 91 reads; null when the map and the render globals name none. */
    public NativeOceanSettings? Ocean { get; init; }
    public IReadOnlyList<NativeCubemapBinding> Cubemaps { get; init; } = [];
    public IReadOnlyDictionary<string, int> CubemapSkippedReasons { get; init; } = new Dictionary<string, int>();
    /** Placed atmosphere resources in load order, and their decoded 3D lookups by handle. */
    public IReadOnlyList<PlacedAtmosphere> Atmospheres { get; init; } = [];
    /** Light rows that draw in stage 4, with their table's bubble; a placement update moves a row in place. */
    public IReadOnlyList<(AuthoredLightRow Row, uint BubbleHash)> LocalLights { get; init; } = [];
    public Dictionary<uint, TextureData> AtmosphereLookups { get; init; } = [];
    /** Object fog colour tables by the handle at atmosphere settings +0x8C. */
    public IReadOnlyDictionary<uint, byte[]> FogTables { get; init; } = new Dictionary<uint, byte[]>();
    /** Decoded time-of-day curve sets by placement list tag; an atmosphere uses the one in its own list. */
    public IReadOnlyDictionary<uint, NativeTimeOfDay> TimesOfDay { get; init; } = new Dictionary<uint, NativeTimeOfDay>();
    /** The 30 atmosphere parameter slots from the render-global table; they fill extern 7. */
    public IReadOnlyList<Vector4>? AtmosphereSlots { get; init; }
    /** The decorators' wind settings and their noise texture tag; null and 0 when no payload names them. */
    public NativeWindSettings? WindSettings { get; init; }
    public uint WindNoiseTexture { get; init; }
    public required IReadOnlyDictionary<string, int> SkippedReasons { get; init; }
    /** Skips of decal, additive-decal and transparent parts, keyed by stage and reason. */
    public required IReadOnlyDictionary<string, int> StageSkippedReasons { get; init; }
    /** Skips of Skybox-layer parts, keyed by reason. */
    public IReadOnlyDictionary<string, int> SkySkippedReasons { get; init; } = new Dictionary<string, int>();
    public required IReadOnlyList<string> Diagnostics { get; init; }
    public required int InputPartCount { get; init; }
    public int PreparedPartCount => Groups.Sum(group => group.Parts.Count);
    public int IsolatedPartCount => Groups.Sum(group => group.Parts.Count(part => part.BindingSource == NativeBindingSource.IsolatedPreview));
    public bool AssetPreview { get; init; }
    public int SkippedPartCount => InputPartCount - PreparedPartCount;
    public IReadOnlyList<int> RenderStages => Groups.Select(group => group.RenderStage).Distinct().Order().ToArray();
    public IReadOnlyDictionary<string, int> StagePartCounts => Groups.GroupBy(group => group.RenderStage).OrderBy(g => g.Key)
        .ToDictionary(g => RenderNames.GetStageName(g.Key) ?? g.Key.ToString(CultureInfo.InvariantCulture), g => g.Sum(x => x.Parts.Count));
    public IReadOnlyDictionary<string, int> KindPartCounts => Groups.GroupBy(group => group.Sky ? "Sky" : group.Kind.ToString())
        .ToDictionary(g => g.Key, g => g.Sum(x => x.Parts.Count));
    /** Input parts by native kind; Sky counts entity parts on the skybox layer. */
    public required IReadOnlyDictionary<string, int> InputKindCounts { get; init; }
    public required long RequiredTextureBytes { get; init; }
    public required long TextureBytes { get; init; }
    public required long GeometryBytes { get; init; }
    public required long ScalarBytes { get; init; }
    public required long TextureBudgetBytes { get; init; }
    /** Textures loaded without their streamed mips because those did not fit the budget. */
    public int ResidentOnlyTextureCount { get; init; }
    private Vector3 minimum, maximum;
    public required Vector3 Minimum { get => minimum; init => minimum = value; }
    public required Vector3 Maximum { get => maximum; init => maximum = value; }
    /** Instances whose skip may depend on their placement; moving one needs a new plan. */
    private IReadOnlySet<MeshInstance> placementSkips = new HashSet<MeshInstance>();
    /** Each call starts a decorator read of the current package bytes with an empty payload cache. */
    private Func<Func<MeshInstance, NativeDecoratorPart>>? decoratorReads;

    /**
     * The inspection profile supplies viewer time, one verified native constructor choice and 2^EV.
     * @param preExposure Extra factor on frame row 1 X; water uses it to match the viewer's sky gain.
     */
    internal static NativeFrameScopeConstants InspectionFrame(double simulationSeconds, float exposureEv = 0,
        float preExposure = 1) => NativeFrameScope.Create(new()
    {
        SimulationSeconds = simulationSeconds, FrameFlag425B8 = false, FrameFlag425B9 = false,
        // Extern 1 float 7 holds 2^EV and float 10 holds 2^(0.33 EV) / 2^EV; they fill frame row 1.
        Extern1C = MathF.Pow(2, exposureEv) * preExposure,
        Extern28 = MathF.Pow(2, BitConverter.UInt32BitsToSingle(0x3EA8F5C3) * exposureEv) / MathF.Pow(2, exposureEv)
    });

    /**
     * Without a camera, preparation supplies an identity camera so camera-reading TFX validates.
     * @param atmosphereSlots Atmosphere slots for extern 7; null leaves extern 7 unset.
     * @param lookupSize Extern 7 vector 5, the screen lookup size; null uses one pixel.
     * @param exposureEv Extern 1 float 8; float 7 is 2^EV. 0x7FF742EEC420 writes both.
     * @param timeOfDay Extern 1 float 4; preparation keeps the viewer default.
     * @param assetPreview Object channels with no table read AssetChannelDefault.
     */
    internal static TfxInputs MaterialInputs(double simulationSeconds, TfxParameterLookup parameters,
        TfxInputs? camera = null, IReadOnlyList<Vector4>? atmosphereSlots = null, float cameraHeight = 0,
        Vector4? lookupSize = null, float exposureEv = 0, float timeOfDay = NativeFrameInputs.ViewerTimeOfDay,
        bool assetPreview = false)
    {
        var inputs = NativeInspectionMaterialInputs.Create(simulationSeconds, parameters, camera, timeOfDay);
        if (assetPreview) inputs.ObjectChannelDefault = AssetChannelDefault;
        NativeWater.Apply(inputs, atmosphereSlots, cameraHeight, lookupSize ?? Vector4.One);
        NativeOcean.ApplyRegistered(inputs, parameters);
        inputs.Resources[new(44, 1)] = PreDecalNormals;
        inputs.Resources[new(3, 7)] = SceneDepth;
        inputs.Resources[new(3, 15)] = NativeStencil.SceneStencil;
        inputs.Vectors[new(44, 2)] = DecalVector2;
        inputs.Floats[new(1, 7)] = MathF.Pow(2, exposureEv);
        inputs.Floats[new(1, 8)] = exposureEv;
        inputs.Resources[NativeGearPlating.FrameDyeKey] = NativeGearPlating.FrameDyeResource;
        NativeGearDye.Apply(inputs, 0);
        // Extern 1 wind values as the frame writer leaves them before wind settings load.
        new NativeWindFrame(null).Apply(inputs);
        return inputs;
    }

    /**
     * The atmosphere with lookups in the bubble with the most parts, and the time of day in its list.
     * The start time is the placement's +0x18 value; the viewer default when the scene places no time of day.
     */
    public static NativeSkySource SkySource(SceneDocument scene)
    {
        var parts = scene.Instances.GroupBy(instance => instance.BubbleHash).ToDictionary(group => group.Key, group => group.Count());
        var atmosphere = scene.Atmospheres.Where(item => NativeAtmosphereSettings.Parse(item.Settings).HasMode0Lookups)
            .OrderByDescending(item => parts.GetValueOrDefault(item.BubbleHash)).FirstOrDefault();
        // A scene with no atmosphere, or none in its list, keeps its first time of day.
        var time = scene.TimesOfDay.FirstOrDefault(item => item.PlacementListTag == atmosphere?.PlacementListTag)
            ?? scene.TimesOfDay.FirstOrDefault();
        if (time is null) return new(atmosphere, null, NativeFrameInputs.ViewerTimeOfDay);
        // 0x7FF742E05F50 adds the activity clock's phase, which Studio does not have; 0x7FF742E08C80 keeps the fraction.
        float start = time.StartTime - MathF.Floor(time.StartTime);
        return new(atmosphere, time, start is >= 0 and < 1 ? start : 0);
    }

    /**
     * Call on a worker; cancellation is checked between package reads and placement groups.
     * @param textureCache Decoded textures kept between plans; the caller removes a tag when its bytes change.
     */
    public static NativeScenePlan Prepare(PackageCatalog catalog, SceneDocument scene,
        CancellationToken cancellationToken = default, Action<string>? progress = null,
        long textureBudgetBytes = DefaultTextureBudgetBytes, IReadOnlyDictionary<uint, Vector4>? parameterOverrides = null,
        bool assetPreview = false, IDictionary<uint, TextureData>? textureCache = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(textureBudgetBytes);
        var reader = new GameMaterialReader(catalog);
        var viewScope = reader.ReadScopeExecution(0x80B356A5);
        var transparentScope = reader.ReadScopeExecution(NativeTransparentScope.ScopeTag);
        var parameterDefaults = reader.ReadTfxParameterDefaults(RenderGlobalsTag);
        var parameters = parameterDefaults.CreateSnapshot(parameterOverrides);
        var atmosphereSlots = NativeAtmosphere.ReadSlots(parameterDefaults, parameters, out _);
        var transparentLighting = NativeTransparentScope.ReadLighting(parameterDefaults, parameters);
        var states = new NativeRenderStateCatalog();
        var loader = new AssetSceneLoader(catalog);
        var decoratorReader = new NativeDecorators(catalog)
        {
            SceneMap = catalog.TryGetEntry(scene.TagHash, out var sceneEntry) && sceneEntry.Reference == 0x808091DE ? scene.TagHash : 0
        };
        var decoratorMaps = scene.OcclusionOwners.GroupBy(owner => owner.BubbleHash)
            .ToDictionary(g => g.Key, g => (IReadOnlySet<uint>)g.Select(owner => owner.MapTag).ToHashSet());
        var decoratorSlices = NativeDecorators.SliceIndices(scene);
        var materials = new Dictionary<(uint Tag, int Stage, NativeMeshKind Kind, string Gear), NativeSceneMaterial>();
        var dyeMaterials = new Dictionary<(uint Tag, int Stage, NativeMeshKind Kind, string Gear), NativeSceneMaterial>();
        var channelTables = new ChannelPicker(catalog, scene);
        var channelMaterials = new Dictionary<NativeSceneMaterial, Dictionary<IReadOnlyList<Vector4>, NativeSceneMaterial>>(
            ReferenceEqualityComparer.Instance);
        var rejected = new Dictionary<(uint Tag, int Stage, NativeMeshKind Kind, string Gear), (string Reason, string Detail)>();
        bool firstPerson = NativeStencil.IsFirstPersonView(scene);
        var gearBits = new Dictionary<uint, uint>();
        var gearRecords = new Dictionary<uint, (byte[]? Record, string? Error)>();
        var gearScopes = new Dictionary<uint, NativeGearScope>();
        uint GearBits(uint material)
        {
            if (!gearBits.TryGetValue(material, out uint bits))
                gearBits.Add(material, bits = NativeGearPlating.GearBits(catalog, material));
            return bits;
        }
        // Each gear object's models share one set of plate atlases, sized by its gear kind; an unmarked scene is one gear object.
        var gearObjects = new Dictionary<object, int>();
        var atlasSets = new List<NativePlateAtlases>();
        bool unknownGearKind = false;
        foreach (var gearObject in scene.Instances.GroupBy(instance => NativeGearPlating.GearObjectFor(scene, instance)))
        {
            gearObjects.Add(gearObject.Key, atlasSets.Count);
            byte[][] plated = [.. NativeGearPlating.SceneRecords(catalog, gearObject, gearRecords)];
            int kind = NativeGearPlating.KindFor(scene, gearObject.First(), out bool known);
            unknownGearKind |= !known && plated.Length != 0;
            atlasSets.Add(NativePlateAtlases.Build(catalog, plated, kind));
        }
        // The atlas set and its own tag behind a plan tag; null for a package texture.
        (NativePlateAtlases Set, uint Tag)? AtlasOf(uint tag)
        {
            for (int index = 0; index < atlasSets.Count; index++)
            {
                uint offset = (uint)index * AtlasTagStride;
                if (tag >= offset && atlasSets[index].IsAtlas(tag - offset)) return (atlasSets[index], tag - offset);
            }
            return null;
        }
        bool IsAtlas(uint tag) => AtlasOf(tag) is not null;
        NativeGearScope GearScope(uint record)
        {
            if (!gearScopes.TryGetValue(record, out var scope))
                gearScopes.Add(record, scope = NativeGearPlating.Evaluate(reader, record,
                    MaterialInputs(0, parameters, null, atmosphereSlots, assetPreview: assetPreview)));
            return scope;
        }
        var skipped = new Dictionary<string, int>();
        var details = new List<string>();
        var detailCounts = new Dictionary<string, int>();
        var candidates = new List<(NativeScenePart Part, NativeSceneMaterial Material)>();
        var bounds = new Dictionary<MeshData, (Vector3 Min, Vector3 Max)>();
        var maximumVertices = new Dictionary<MeshData, uint>();
        var bindingCache = new Dictionary<(uint Table, int Row, uint Mesh, uint Group, uint Bubble, string Owners, Matrix4x4 Transform, int Part), NativeStaticBinding>();
        var ownerKeys = new Dictionary<IReadOnlyList<NativeOcclusionOwner>, string>(ReferenceEqualityComparer.Instance);
        var placementSkips = new HashSet<MeshInstance>();
        void Detail(string reason, string detail)
        {
            string text = $"{reason}: {detail}";
            if (detailCounts.GetValueOrDefault(reason) >= 40 || details.Contains(text)) return;
            details.Add(text);
            detailCounts[reason] = detailCounts.GetValueOrDefault(reason) + 1;
        }
        // Extern 91 must be keyed before any material validates.
        NativeOceanSettings? ocean = null;
        try { ocean = NativeOcean.Register(catalog, scene.TagHash, parameters); }
        catch (Exception error) when (Recoverable(error)) { Detail("Ocean settings failed", error.Message); }
        int currentStage = -1;
        var stageSkips = new Dictionary<string, int>();
        var skySkips = new Dictionary<string, int>();
        bool currentSky = false;
        void Skip(string reason, string? detail = null, int count = 1)
        {
            skipped[reason] = skipped.GetValueOrDefault(reason) + count;
            if (currentSky) skySkips[reason] = skySkips.GetValueOrDefault(reason) + count;
            if (currentStage > 0 && (DrawnStages.Contains(currentStage) || currentStage == ShadowStage))
            {
                string key = $"{RenderNames.GetStageName(currentStage)}: {reason}";
                stageSkips[key] = stageSkips.GetValueOrDefault(key) + count;
            }
            if (detail is not null) Detail(reason, detail);
        }
        for (int i = 0; i < scene.Instances.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (i % 128 == 0) progress?.Invoke($"Checking native part {i + 1}/{scene.Instances.Count}");
            var instance = scene.Instances[i];
            var native = instance.Mesh.Native;
            currentStage = native?.RenderStage ?? -1;
            currentSky = instance.Layer == SceneLayer.Skybox;
            if (native is null)
            { Skip("Native geometry required"); continue; }
            bool decorator = native.InputLayoutVerified && !native.InputLayoutFitsSignature && MissesOnlyInstanceStreams(native)
                && NativeStencil.PathOf(instance) == NativeStencilPath.Decorator;
            if (!decorator && (!native.InputLayoutVerified || !native.InputLayoutFitsSignature))
            {
                Skip("Input declaration is unverified", $"{native.Kind} mesh 0x{instance.Mesh.TagHash:X8} part {instance.Mesh.PartIndex} stage {native.RenderStage} "
                    + $"LOD {instance.Mesh.LodLevel} material 0x{native.MaterialTag:X8}: {native.LayoutStatus}");
                continue;
            }
            if (native.RenderStage < 0 || native.BaseVertex != 0 || native.GamePrimitiveType is not (3 or 5))
            { Skip("Draw stage or range is unsupported"); continue; }
            if (!DrawnStages.Contains(native.RenderStage) && native.RenderStage != ShadowStage)
            { Skip($"Stage {RenderNames.GetStageName(native.RenderStage) ?? native.RenderStage.ToString(CultureInfo.InvariantCulture)} is not drawn"); continue; }
            bool sky = instance.Layer == SceneLayer.Skybox;
            if (sky && GBufferStages.Contains(native.RenderStage))
            { Skip("Sky G-buffer parts are not drawn"); continue; }
            // Sky entities draw in the sky pass only; no shadow view sees them.
            if (sky && native.RenderStage == ShadowStage)
            { Skip("Sky shadow parts are not drawn"); continue; }
            NativeGearMaterial? gear;
            try
            {
                int gearObject = gearObjects[NativeGearPlating.GearObjectFor(scene, instance)];
                gear = NativeGearPlating.ForPart(catalog, instance, GearBits(native.MaterialTag), NativeGearPlating.DyesFor(scene, instance),
                    GearScope, gearRecords, atlasSets[gearObject], out string? gearError, (uint)gearObject * AtlasTagStride);
                if (gearError is not null)
                { Skip("Gear scope unavailable", $"{native.Kind} mesh 0x{instance.Mesh.TagHash:X8} part {instance.Mesh.PartIndex}: {gearError}"); continue; }
            }
            catch (Exception error) when (Recoverable(error))
            { Skip("Gear scope unavailable", $"{native.Kind} material 0x{native.MaterialTag:X8}: {error.Message}"); continue; }
            NativeDecoratorPart? decoratorPart = null;
            if (decorator)
            {
                try
                {
                    CheckDecoratorLayout(native);
                    decoratorPart = decoratorReader.Read(instance, decoratorMaps.GetValueOrDefault(instance.BubbleHash) ?? new HashSet<uint>(),
                        decoratorSlices.GetValueOrDefault(instance.BubbleHash) ?? new HashSet<int>());
                }
                catch (Exception error) when (Recoverable(error) || error is NotSupportedException)
                {
                    Skip(FailureReason(error, DecoratorSkip), $"mesh 0x{instance.Mesh.TagHash:X8} part {instance.Mesh.PartIndex}: {error.Message}");
                    continue;
                }
                if (decoratorPart.Payload.Wind is null) Detail("Decorator wind settings missing", decoratorPart.Payload.WindSource);
            }
            // Entities and decorators share a kind but not a stencil reference.
            var stencilPath = NativeStencil.PathOf(instance);
            var materialKey = (native.MaterialTag, native.RenderStage, native.Kind, (gear?.Key ?? "") + "|" + stencilPath
                + (decorator ? "|decorator" : ""));
            if (rejected.TryGetValue(materialKey, out var reject))
            { Skip(reject.Reason); continue; }
            if (!materials.TryGetValue(materialKey, out var material))
            {
                try
                {
                    material = ValidateMaterial(reader, states, native.MaterialTag, parameters, native.RenderStage, native.Kind,
                        atmosphereSlots, gear, firstPerson, stencilPath, decorator);
                    materials.Add(materialKey, material);
                }
                catch (Exception error) when (Recoverable(error))
                {
                    string reason = Reason(error);
                    string detail = $"{native.Kind} material 0x{native.MaterialTag:X8}: {error.Message}";
                    rejected[materialKey] = (reason, detail);
                    Skip(reason, detail);
                    continue;
                }
            }
            if (material.ReadsGearDye && NativeGearDye.PartBit(instance) is not 0 and var dyeBit)
            {
                if (!dyeMaterials.TryGetValue(materialKey, out var dyed))
                    dyeMaterials.Add(materialKey, dyed = material with { GearDyeBit = dyeBit });
                material = dyed;
            }
            if (material.ReadsObjectChannels && native.Kind == NativeMeshKind.Entity && channelTables.For(instance) is { } channelTable)
            {
                if (!channelMaterials.TryGetValue(material, out var byTable))
                    channelMaterials.Add(material, byTable = new(ReferenceEqualityComparer.Instance));
                if (!byTable.TryGetValue(channelTable, out var channelled))
                    byTable.Add(channelTable, channelled = material with { ObjectChannels = channelTable, ObjectChannelKey = ChannelKey(channelTable) });
                material = channelled;
            }
            // Static AO bindings do not read the placement; entity and terrain constants and the bounds do.
            bool placementDependent = native.Kind != NativeMeshKind.Static;
            try
            {
                Vector4[] constants;
                NativeVertexStream? scalars = null;
                bool isolated = assetPreview && native.Kind == NativeMeshKind.Static
                    && instance.InstanceSourceTag == 0 && instance.NativeInstanceData.IsEmpty;
                // Decorator instance streams are built each frame from the part's record.
                if (decoratorPart is not null) constants = new Vector4[NativeDecorators.InstanceStreams];
                else if (native.Kind == NativeMeshKind.Entity)
                    constants = material.Palette == NativeEntityPalette.None ? AssetSceneLoader.ReadNativeEntityConstants(instance)
                        : NativeEntityPalettes.Build(instance, material.Palette);
                else
                {
                    if (!ownerKeys.TryGetValue(instance.OcclusionOwners, out var owners))
                    {
                        owners = string.Join('/', instance.OcclusionOwners.Select(owner => owner.MapTag).Distinct().Order());
                        ownerKeys.Add(instance.OcclusionOwners, owners);
                    }
                    var key = (instance.InstanceSourceTag, instance.InstanceSourceIndex,
                        instance.Mesh.TagHash, native.BufferGroupWord0C, instance.BubbleHash, owners, instance.Transform,
                        native.Kind == NativeMeshKind.Terrain ? instance.Mesh.PartIndex : -1);
                    if (!bindingCache.TryGetValue(key, out var binding))
                    {
                        binding = isolated ? NativeAssetBindings.CreateStatic(instance) : native.Kind == NativeMeshKind.Terrain
                            ? loader.ReadNativeTerrainBinding(instance, cancellationToken)
                            : loader.ReadNativeStaticBinding(instance, token: cancellationToken);
                        bindingCache.Add(key, binding);
                    }
                    uint maximumVertex = 0;
                    if (!binding.UseSingleScalar && !maximumVertices.TryGetValue(instance.Mesh, out maximumVertex))
                        maximumVertices.Add(instance.Mesh, maximumVertex = instance.Mesh.SourceVertexIndices.Max());
                    if ((ulong)binding.ScalarByteAddress + maximumVertex >= (ulong)binding.ScalarBuffer.Data.Length)
                        throw new InvalidDataException("AO fetch leaves the authored scalar buffer.");
                    constants = binding.InstanceConstants;
                    scalars = binding.ScalarBuffer;
                }
                placementDependent = true;
                // A ground cell draws many records; its bounds are the cell's, not the first record's.
                var world = instance.CellBounds ?? PartBounds(instance, bounds);
                candidates.Add((new NativeScenePart(instance, constants, scalars, (world.Min + world.Max) * 0.5f,
                    Vector3.Distance(world.Min, world.Max) * 0.5f + 0.001f)
                {
                    BindingSource = isolated ? NativeBindingSource.IsolatedPreview : NativeBindingSource.Authored,
                    Minimum = world.Min, Maximum = world.Max, Decorator = decoratorPart
                }, material));
            }
            catch (Exception error) when (Recoverable(error))
            {
                if (placementDependent) placementSkips.Add(instance);
                Skip(FailureReason(error, $"{native.Kind} instance or AO binding unavailable"), $"{native.Kind} mesh 0x{instance.Mesh.TagHash:X8}: {error.Message}");
            }
        }
        currentStage = -1;
        // Full size and streamed-mip size per texture; the game keeps the other mips resident.
        var sizes = new Dictionary<uint, (long Full, long Streamed)>();
        var selectedTextures = new HashSet<uint>();
        var textureQueue = new List<uint>();
        var admitted = new HashSet<uint>();
        var textureReader = new TextureDataReader(catalog);
        long requiredBytes = 0, selectedBytes = 0;
        foreach (var group in candidates.GroupBy(p => p.Material.Execution.Material.Tag)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var tags = group.SelectMany(p => PartTextures(p.Part, p.Material)).Distinct().ToArray();
                foreach (uint tag in tags)
                    if (!sizes.ContainsKey(tag) && AtlasOf(tag) is { } atlas)
                    {
                        long size = atlas.Set.ByteCount(atlas.Tag);
                        sizes.Add(tag, (size, 0)); requiredBytes += size;
                    }
                    else if (!sizes.ContainsKey(tag))
                    {
                        byte[] header = catalog.Read(tag);
                        if (header.Length != 40) throw new InvalidDataException("Texture header has an unsupported size.");
                        long size = BinaryPrimitives.ReadUInt32LittleEndian(header);
                        sizes.Add(tag, (size, textureReader.StreamedByteCount(tag))); requiredBytes += size;
                    }
                // The budget never drops a part; it only decides which textures get their streamed mips.
                admitted.Add(group.Key);
                foreach (uint tag in tags)
                    if (selectedTextures.Add(tag)) { textureQueue.Add(tag); selectedBytes += sizes[tag].Full - sizes[tag].Streamed; }
            }
            catch (Exception error) when (Recoverable(error))
            { Skip(FailureReason(error, "Texture preflight failed"), $"Material 0x{group.Key:X8}: {error.Message}", group.Count()); }
        }
        // Streamed mips go to the textures of the most-used materials first, while the budget holds.
        var residentOnly = new HashSet<uint>();
        foreach (uint tag in textureQueue)
        {
            long streamed = sizes[tag].Streamed;
            if (streamed == 0) continue;
            if (streamed <= textureBudgetBytes - selectedBytes) selectedBytes += streamed;
            else residentOnly.Add(tag);
        }
        if (residentOnly.Count != 0)
            details.Add($"{residentOnly.Count} of {textureQueue.Count} textures load their resident mips only: the streamed mips "
                + $"({residentOnly.Sum(tag => sizes[tag].Streamed) >> 20} MiB) do not fit the {textureBudgetBytes >> 20} MiB budget.");
        // The wind noise texture is extern 1 resource 15; it decodes with the material textures.
        var windPayload = candidates.Select(c => c.Part.Decorator?.Payload).FirstOrDefault(p => p?.Wind is not null);
        if (windPayload is { NoiseTexture: not 0 } && !IsAtlas(windPayload.NoiseTexture))
            selectedTextures.Add(windPayload.NoiseTexture);
        // Stage-4 light materials draw in the lighting pass; their textures decode with the part textures.
        var lightTextures = new HashSet<uint>();
        foreach (uint lightMaterial in scene.LightTables.SelectMany(table => table.Table.Rows)
            .Where(row => row.AuthoredStages.Contains(4)).Select(row => row.MaterialTag).Distinct())
            try
            {
                foreach (var texture in reader.ReadExecution(lightMaterial).Material.Textures)
                    if (texture.DeclaredByShader && texture.TextureTag is not (0 or uint.MaxValue)) lightTextures.Add(texture.TextureTag);
            }
            catch (Exception error) when (Recoverable(error))
            { Detail("Light material unreadable", $"Material 0x{lightMaterial:X8}: {error.Message}"); }
        selectedTextures.UnionWith(lightTextures);
        var textures = new Dictionary<uint, TextureData>();
        var badTextures = new Dictionary<uint, string>();
        uint[] textureOrder = selectedTextures.Order().ToArray();
        var decodedTextures = new TextureData?[textureOrder.Length];
        var textureErrors = new Exception?[textureOrder.Length];
        int textureNumber = 0;
        var progressLock = new object();
        // Catalog reads decode blocks in parallel; results are published below in tag order.
        try
        {
            Parallel.For(0, textureOrder.Length, new ParallelOptions { CancellationToken = cancellationToken }, index =>
            {
                uint tag = textureOrder[index];
                lock (progressLock) progress?.Invoke($"Decoding shared texture {++textureNumber}/{textureOrder.Length}");
                var atlas = AtlasOf(tag);
                if (textureCache is not null && atlas is null && textureCache.TryGetValue(tag, out var kept))
                { decodedTextures[index] = kept; return; }
                try
                {
                    decodedTextures[index] = atlas is { } found ? found.Set.Compose(found.Tag, source =>
                        textureCache is not null && textureCache.TryGetValue(source, out var cached) ? cached : textureReader.Read(source))
                        : textureReader.Read(tag, residentOnly.Contains(tag));
                }
                catch (Exception error) when (Recoverable(error)) { textureErrors[index] = error; }
            });
        }
        catch (AggregateException error) { ExceptionDispatchInfo.Capture(error.InnerExceptions[0]).Throw(); }
        for (int index = 0; index < textureOrder.Length; index++)
        {
            uint tag = textureOrder[index];
            if (decodedTextures[index] is { } decoded)
            {
                textures.Add(tag, decoded);
                // The cache keeps full textures only, so a later plan with room never reuses a reduced one.
                if (textureCache is not null && !IsAtlas(tag) && !residentOnly.Contains(tag)) textureCache[tag] = decoded;
                continue;
            }
            var error = textureErrors[index]!;
            string reason = FailureReason(error, "Texture payload decoding failed");
            badTextures.Add(tag, reason);
            Detail(reason, $"Texture 0x{tag:X8}: {error.Message}");
        }
        var prepared = new List<(NativeScenePart Part, NativeSceneMaterial Material)>();
        foreach (var candidate in candidates)
        {
            if (!admitted.Contains(candidate.Material.Execution.Material.Tag)) continue;
            var failures = PartTextures(candidate.Part, candidate.Material).Where(badTextures.ContainsKey).Select(tag => badTextures[tag]).ToArray();
            if (failures.Length != 0)
            { Skip(failures.Contains("Package block authentication failed") ? "Package block authentication failed" : failures[0]); continue; }
            prepared.Add(candidate);
        }
        var clothStreams = new Dictionary<string, byte[]>();
        var groups = prepared.GroupBy(p => GroupKey(p.Part, p.Material)).Select(group =>
        {
            var first = group.First(); var mesh = first.Part.Instance.Mesh.Native!;
            byte[]? cloth = null;
            string geometry = GeometryKey(mesh);
            if (ReadsClothDisplay(first.Part.Instance, first.Material, reader))
            {
                // The bind stream is not the authored bytes, so no other reader shares it.
                geometry += "|cloth";
                if (!clothStreams.TryGetValue(geometry, out cloth)) clothStreams.Add(geometry, cloth = NativeClothDisplay.BindStream(mesh));
            }
            return new NativeSceneGroup(geometry, PipelineKey(mesh, first.Part.Instance.Layer == SceneLayer.Skybox) + StencilKey(first.Material), mesh, first.Material,
                first.Part.Scalars?.DataTag ?? 0, mesh.RenderStage, first.Part.Instance.Layer == SceneLayer.Skybox,
                group.Select(x => x.Part).ToArray()) { ClothDisplayStream = cloth };
        }).ToArray();
        var dyeSources = groups.Where(group => group.Material.Gear is { Scopes.Count: > 0 })
            .SelectMany(group => group.Parts).Select(part => NativeGearPlating.DyesFor(scene, part.Instance).Source).Distinct().ToArray();
        if (dyeSources.Length != 0)
            details.Add($"Gear dye scopes use {string.Join(", ", dyeSources)}; the slot of each dye follows its record's scope index.");
        if (groups.Any(group => group.Material.Gear is { PlateTextures.Count: > 0 }))
            details.Add("Gear plate atlases are composed from their descriptor rows at the game's size for the gear kind and set; "
                + "the kind comes from the item's bucket, fitted to the installed rectangles, and the formats and smaller mips are unverified.");
        if (unknownGearKind) details.Add("Gear kind unknown; weapon atlas sizes used.");
        int channelParts = groups.Where(group => group.Material.ObjectChannels is not null).Sum(group => group.Parts.Count);
        if (channelParts != 0 || channelTables.Tables.Unread != 0)
            details.Add($"Object channels: {channelParts} entity parts read rest tables ({channelTables.Tables.DistinctTables} distinct); "
                + $"{channelTables.Tables.Unread} models did not read and use (1,1,1,1).");
        var usedTextures = groups.SelectMany(g => g.Parts.SelectMany(p => PartTextures(p, g.Material))).ToHashSet();
        usedTextures.UnionWith(lightTextures);
        foreach (uint tag in textures.Keys.Where(tag => !usedTextures.Contains(tag)).ToArray()) textures.Remove(tag);
        var (minimum, maximum) = Union(prepared.Select(item => item.Part));
        var atmosphereLookups = new Dictionary<uint, TextureData>();
        foreach (var placed in scene.Atmospheres)
        {
            var settings = NativeAtmosphereSettings.Parse(placed.Settings);
            if (!settings.HasMode0Lookups) continue;
            // Handles 0 and 1 are 3D lookups; handle 2 is the 2D lookup extern 7 resource 7 binds.
            foreach (var (tag, volume) in new[] { (settings.Lookups[0], true), (settings.SecondLookup, true), (settings.Lookups[2], false) })
            {
                if (atmosphereLookups.ContainsKey(tag)) continue;
                try
                {
                    var data = textureReader.Read(tag);
                    if (data.IsVolume != volume || data.IsCube)
                        throw new InvalidDataException($"Atmosphere lookup is not a {(volume ? "3D" : "2D")} texture.");
                    atmosphereLookups.Add(tag, data);
                }
                catch (Exception error) when (Recoverable(error)) { Detail("Atmosphere lookup failed", $"0x{tag:X8}: {error.Message}"); }
            }
        }
        var fogTables = new Dictionary<uint, byte[]>();
        foreach (var placed in scene.Atmospheres)
        {
            uint tag = NativeAtmosphereSettings.Parse(placed.Settings).FogTable;
            if (tag == uint.MaxValue || fogTables.ContainsKey(tag)) continue;
            try
            {
                var bytes = catalog.Read(tag);
                if (bytes.Length != NativeObjectFog.TableBytes) throw new InvalidDataException("Fog colour table is not 16 x 256 RGBA8.");
                fogTables.Add(tag, bytes);
            }
            catch (Exception error) when (Recoverable(error)) { Detail("Fog colour table failed", $"0x{tag:X8}: {error.Message}"); }
        }
        var timesOfDay = new Dictionary<uint, NativeTimeOfDay>();
        foreach (var placed in scene.TimesOfDay)
        {
            if (timesOfDay.ContainsKey(placed.PlacementListTag)) continue;
            try
            {
                var (windows, handles) = NativeTimeOfDay.ReadHeader(catalog.Read(placed.CurveSetTag));
                var curves = handles.Select(h => h == uint.MaxValue ? null : NativeDirectionCurve.Parse(catalog.Read(h))).ToArray();
                timesOfDay.Add(placed.PlacementListTag, new NativeTimeOfDay(windows, curves));
            }
            catch (Exception error) when (Recoverable(error))
            { Detail("Time-of-day curves failed", $"0x{placed.CurveSetTag:X8}: {error.Message}"); }
        }
        TextureData ReadEnvironmentTexture(uint tag, byte[] header)
        {
            if (textures.TryGetValue(tag, out var existing)) return existing;
            if (header.Length != 40) throw new InvalidDataException("Environment texture header has an unsupported size.");
            long bytes = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (sizes.TryAdd(tag, (bytes, 0))) requiredBytes += bytes;
            // Cubemap bindings read the header's mip count, so environment textures always load whole.
            var decoded = textureReader.Read(tag);
            textures.Add(tag, decoded);
            selectedBytes += bytes;
            return decoded;
        }
        uint frameTexture24 = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            frameTexture24 = reader.ReadFrameTexture24(RenderGlobalsTag);
            ReadEnvironmentTexture(frameTexture24, catalog.Read(frameTexture24));
        }
        catch (Exception error) when (Recoverable(error))
        { Detail("Frame lookup failed", error.Message); }
        // The noise textures load only when a drawn material reads extern 91.
        if (ocean is not null && groups.Any(group => NativeOcean.Reads(group.Material.Execution)))
            try
            {
                foreach (var (tag, data) in NativeOcean.ReadTextures(textureReader, ocean)) textures.TryAdd(tag, data);
            }
            catch (Exception error) when (Recoverable(error)) { Detail("Ocean noise failed", error.Message); }
        var cubemaps = new List<NativeCubemapBinding>();
        var cubeSkips = new Dictionary<string, int>();
        foreach (var placement in scene.Cubemaps.DistinctBy(item => (item.PlacementSourceTag, item.AuxiliaryOffset)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                uint tag = placement.Cubemap.Texture0Tag;
                var header = catalog.Read(tag);
                if (header.Length != 40 || !catalog.TryGetEntry(tag, out var entry) || entry.Type != 32 || entry.SubType != 2)
                    throw new NotSupportedException("Cubemap volume texture is not one cube.");
                var binding = NativeCubemapBinding.Create(placement.Cubemap.SourceData.Span, tag, header[27], header[23]);
                var texture = ReadEnvironmentTexture(tag, header);
                if (!texture.IsCube || texture.ArraySize != 6)
                    throw new NotSupportedException("Cubemap volume texture is not one cube.");
                cubemaps.Add(binding);
            }
            catch (Exception error) when (Recoverable(error))
            {
                cubeSkips[error.Message] = cubeSkips.GetValueOrDefault(error.Message) + 1;
                Detail("Cubemap volume skipped", error.Message);
            }
        }
        return new NativeScenePlan
        {
            AssetPreview = assetPreview,
            TimesOfDay = timesOfDay,
            Atmospheres = scene.Atmospheres.ToArray(),
            LocalLights = scene.LightTables.SelectMany(table => table.Table.Rows
                .Where(row => row.AuthoredStages.Contains(4)).Select(row => (row, table.BubbleHash))).ToArray(),
            AtmosphereLookups = atmosphereLookups,
            FogTables = fogTables,
            AtmosphereSlots = atmosphereSlots,
            WindSettings = windPayload?.Wind,
            WindNoiseTexture = windPayload?.NoiseTexture ?? 0,
            Reader = reader,
            ViewScope = viewScope,
            TransparentScope = transparentScope,
            TransparentLighting = transparentLighting,
            Parameters = parameters,
            ParameterOverrides = parameterOverrides,
            Groups = groups,
            Textures = textures,
            FrameTexture24Tag = frameTexture24,
            Ocean = ocean,
            Cubemaps = cubemaps.AsReadOnly(),
            CubemapSkippedReasons = cubeSkips,
            SkippedReasons = skipped,
            StageSkippedReasons = stageSkips,
            SkySkippedReasons = skySkips,
            Diagnostics = details,
            InputPartCount = scene.Instances.Count,
            InputKindCounts = scene.Instances.GroupBy(i => i.Mesh.Native is null ? "None" : i.Layer == SceneLayer.Skybox ? "Sky" : i.Mesh.Native.Kind.ToString())
                .ToDictionary(g => g.Key, g => g.Count()),
            RequiredTextureBytes = requiredBytes,
            TextureBytes = textures.Values.Sum(t => t.ByteCount),
            TextureBudgetBytes = textureBudgetBytes,
            ResidentOnlyTextureCount = residentOnly.Count,
            GeometryBytes = groups.DistinctBy(g => g.GeometryKey).Sum(g =>
                g.Mesh.VertexStreams.Sum(s => (long)s.Data.Length) + g.Mesh.IndexData.Length),
            ScalarBytes = groups.Where(g => g.Parts[0].Scalars is not null).DistinctBy(g => g.ScalarDataTag).Sum(g => (long)g.Parts[0].Scalars!.Data.Length),
            Minimum = minimum,
            Maximum = maximum,
            placementSkips = placementSkips,
            decoratorReads = () =>
            {
                var fresh = new NativeDecorators(catalog) { SceneMap = decoratorReader.SceneMap };
                fresh.ShareOcclusion(decoratorReader);
                return instance => fresh.Read(instance, decoratorMaps.GetValueOrDefault(instance.BubbleHash) ?? new HashSet<uint>(),
                    decoratorSlices.GetValueOrDefault(instance.BubbleHash) ?? new HashSet<int>());
            }
        };
    }

    /**
     * Recomputes the parts of moved instances exactly as Prepare would. Nothing changes on refusal.
     * @param reason Why a new plan is needed; empty on success.
     * @return False when a moved instance could join, leave or change a group.
     */
    public bool UpdatePlacements(IEnumerable<MeshInstance> moved, out string reason)
    {
        reason = "";
        var set = moved.ToHashSet();
        if (set.Count == 0) return true;
        if (set.Any(placementSkips.Contains)) { reason = "A moved part was skipped for its placement."; return false; }
        var bounds = new Dictionary<MeshData, (Vector3 Min, Vector3 Max)>();
        var updates = new List<(NativeScenePart Part, Vector4[] Constants, (Vector3 Min, Vector3 Max) World)>();
        // A requantized payload moves every record, so its shared payload object takes the fresh read.
        var payloads = new Dictionary<NativeDecoratorPayload, NativeDecoratorPayload>(ReferenceEqualityComparer.Instance);
        Func<MeshInstance, NativeDecoratorPart>? readDecorator = null;
        try
        {
            foreach (var group in Groups)
                foreach (var part in group.Parts)
                {
                    if (!set.Contains(part.Instance)) continue;
                    if (part.BindingSource != NativeBindingSource.Authored) { reason = "A moved part uses a preview binding."; return false; }
                    if (part.Decorator is { } decorator)
                    {
                        // A ground cell's bounds are its cell's, which a placement update does not recompute.
                        if (decorator.Payload.Ground || decoratorReads is null) { reason = "A moved part is a ground decorator cell."; return false; }
                        readDecorator ??= decoratorReads();
                        var fresh = readDecorator(part.Instance);
                        if (fresh.Record != decorator.Record || fresh.MeshIndex != decorator.MeshIndex || fresh.Type.Tag != decorator.Type.Tag
                            || fresh.FirstLevel != decorator.FirstLevel || fresh.LastLevel != decorator.LastLevel
                            || !decorator.Payload.CanAdopt(fresh.Payload)
                            || payloads.TryGetValue(decorator.Payload, out var other) && !ReferenceEquals(other, fresh.Payload))
                        { reason = "A moved decorator part changed its record, mesh, levels or payload layout."; return false; }
                        payloads[decorator.Payload] = fresh.Payload;
                        updates.Add((part, part.Constants, part.Instance.CellBounds ?? PartBounds(part.Instance, bounds)));
                        continue;
                    }
                    // Extern 67 holds the water object's position, so each water object is its own group.
                    if (group.Material.ReadsWaterObject) { reason = "A moved part is water."; return false; }
                    // Scope 14 fog is per object, so each fog reader is its own group.
                    if (group.Material.ReadsAdvancedScope) { reason = "A moved part reads per-object fog."; return false; }
                    var constants = group.Kind switch
                    {
                        NativeMeshKind.Entity => group.Material.Palette == NativeEntityPalette.None
                            ? AssetSceneLoader.ReadNativeEntityConstants(part.Instance)
                            : NativeEntityPalettes.Build(part.Instance, group.Material.Palette),
                        NativeMeshKind.Static => AssetSceneLoader.NativeStaticConstants(part.Instance, part.Constants[3]),
                        _ => null
                    };
                    if (constants is null) { reason = "A moved part is terrain."; return false; }
                    updates.Add((part, constants, PartBounds(part.Instance, bounds)));
                }
        }
        catch (Exception error) when (Recoverable(error)) { reason = error.Message; return false; }
        foreach (var (payload, fresh) in payloads) payload.Adopt(fresh);
        foreach (var (part, constants, world) in updates)
        {
            part.Constants = constants;
            part.Center = (world.Min + world.Max) * 0.5f;
            part.Radius = Vector3.Distance(world.Min, world.Max) * 0.5f + 0.001f;
            part.Minimum = world.Min; part.Maximum = world.Max;
        }
        (minimum, maximum) = Union(Groups.SelectMany(group => group.Parts));
        return true;
    }

    private static (Vector3 Minimum, Vector3 Maximum) Union(IEnumerable<NativeScenePart> parts)
    {
        var minimum = new Vector3(float.PositiveInfinity); var maximum = new Vector3(float.NegativeInfinity);
        foreach (var part in parts) { minimum = Vector3.Min(minimum, part.Minimum); maximum = Vector3.Max(maximum, part.Maximum); }
        return float.IsFinite(minimum.X) ? (minimum, maximum) : (-Vector3.One, Vector3.One);
    }

    /** Only the proved static VS contract and explicitly supplied pixel resources are admitted. */
    internal static NativeSceneMaterial ValidateMaterial(GameMaterialReader reader,
        NativeRenderStateCatalog states, uint tag, TfxParameterLookup parameters, int renderStage = 0,
        NativeMeshKind kind = NativeMeshKind.Static, IReadOnlyList<Vector4>? atmosphereSlots = null,
        NativeGearMaterial? gear = null, bool firstPerson = false, NativeStencilPath? stencilPath = null, bool decorator = false)
    {
        var execution = reader.ReadExecution(tag); var material = execution.Material;
        bool shadow = renderStage == ShadowStage;
        var vs = material.VertexShader ?? throw new NotSupportedException("Vertex shader is missing.");
        // A shadow caster may draw depth only.
        var ps = material.PixelShader;
        if (ps is null && !shadow) throw new NotSupportedException("Pixel shader is missing.");
        byte[] vertexCode = reader.ReadShaderBytecode(vs);
        if (kind == NativeMeshKind.Static && !NativeShaderContracts.CanUsePartialStaticInstances(vertexCode))
            throw new NotSupportedException("Vertex shader contract is unverified.");
        var palette = kind == NativeMeshKind.Entity ? NativeShaderContracts.ClassifyEntityPalette(vertexCode, vs.Disassembly) : NativeEntityPalette.None;
        if (kind != NativeMeshKind.Static && palette == NativeEntityPalette.None && NativeShaderUsage.Get(vertexCode).DynamicSlots.Contains(11))
            throw new NotSupportedException("CB11 palette form is not recognized.");
        if (kind == NativeMeshKind.Terrain && NativeShaderContracts.MissingWords(vertexCode, new(GameShaderStage.Vertex, 11), TerrainUnknownWords) is { } terrainWords)
            throw new NotSupportedException($"Unknown terrain words: {terrainWords}");
        if (NativeShaderContracts.MissingWords(vertexCode, new(GameShaderStage.Vertex, 12), NativeViewInputs.UnknownVertexWords) is { } view)
            throw new NotSupportedException($"Unknown view words: {view}");
        if (material.AdditionalShaderHeaders.Count != 0 || material.ComputeShader is not null)
            throw new NotSupportedException("Additional shader stages are unsupported.");
        var renderStates = ResolveStates(states, material.StateOverrides, renderStage);
        if (renderStates.DepthStencil.StencilEnable)
        {
            if (!NativeWater.IsWaterStencil(renderStates.DepthStencil, execution))
                throw new NotSupportedException($"Stencil reference is unresolved: {renderStates.DepthStencil.FrontFace.StencilFunc}, read 0x{renderStates.DepthStencil.StencilReadMask:X2}, write 0x{renderStates.DepthStencil.StencilWriteMask:X2}, overrides 0x{material.StateOverrides:X8}.");
            var stencil = renderStates.DepthStencil;
            stencil.StencilEnable = false;
            renderStates = renderStates with { DepthStencil = stencil };
        }
        var stencilDraw = NativeStencil.Resolve(states, material.StateOverrides, renderStage,
            stencilPath ?? (kind == NativeMeshKind.Static ? NativeStencilPath.Static : kind == NativeMeshKind.Terrain ? NativeStencilPath.Terrain : NativeStencilPath.Entity),
            firstPerson);
        if (stencilDraw.States is { } stencilStates) renderStates = stencilStates;
        // A decal dye forces only the blend byte, over the pass and the material.
        if (gear?.ForcedBlend is { } forcedBlend)
        {
            int index = forcedBlend & 0x7F;
            renderStates = renderStates with
            {
                Blend = states.GetBlend(index), Selection = renderStates.Selection with { Blend = (byte)index }
            };
        }
        if (renderStage == 0)
            for (int target = 0; target < 4; target++)
                if (renderStates.Blend.RenderTarget[target].BlendEnable)
                    throw new NotSupportedException("Blended material ordering is unresolved.");
        bool pixelViewKnown = ps is not null && NativeShaderContracts.CanUseKnownPixelView(ps) && NativeShaderContracts.MissingWords(
            reader.ReadShaderBytecode(ps), new(GameShaderStage.Pixel, 12), NativeViewInputs.UnknownPixelWords) is null;
        var frame = InspectionFrame(0);
        var frameStages = new HashSet<GameShaderStage>();
        var scopeConstants = new HashSet<GameShaderStage>();
        var scopeTextures = new Dictionary<uint, string>();
        var nullTextures = new Dictionary<NativeBindingKey, string>();
        bool transparentScope = renderStage == TransparentsStage;
        bool advancedScope = false;
        bool externResources = false;
        bool shadowScope = false;
        int targets = 0;
        foreach (var stage in ps is null ? new[] { execution.Vertex } : new[] { execution.Vertex, execution.Pixel })
        {
            var shader = stage.Shader!;
            var stageInputs = MaterialInputs(0, parameters, null, atmosphereSlots);
            // Extern 72 is per payload; validation reads it with an unplaced payload.
            if (decorator)
            {
                NativeDecorators.ApplyExtern72(stageInputs, NativeDecorators.UnplacedExtern72);
                // Ground materials read extern 47; SpeedTree materials do not.
                NativeDecorators.ApplyExtern47(stageInputs, Vector4.Zero);
            }
            var evaluation = TfxEvaluator.Evaluate(stage, stageInputs);
            evaluation.RequireComplete();
            string? frameMissing = null;
            if (shader.Declarations.Any(line => line.StartsWith("dcl_constantbuffer CB13[", StringComparison.Ordinal)))
            {
                frameMissing = NativeShaderContracts.MissingWords(reader.ReadShaderBytecode(shader),
                    new(stage.Stage, 13), frame.KnownWordMasks);
                if (frameMissing is null) frameStages.Add(stage.Stage);
            }
            string? advancedMissing = null;
            if (transparentScope && stage.Stage == GameShaderStage.Pixel
                && shader.Declarations.Any(line => line.StartsWith("dcl_constantbuffer CB8[", StringComparison.Ordinal)))
            {
                advancedMissing = NativeShaderContracts.MissingWords(reader.ReadShaderBytecode(shader),
                    new(stage.Stage, 8), NativeObjectFog.UnknownWords);
                advancedScope = advancedMissing is null;
            }
            foreach (string declaration in shader.Declarations)
            {
                var constant = Regex.Match(declaration, @"^dcl_constantbuffer CB(\d+)\[(\d+)\]");
                if (constant.Success)
                {
                    int slot = int.Parse(constant.Groups[1].Value, CultureInfo.InvariantCulture);
                    int count = int.Parse(constant.Groups[2].Value, CultureInfo.InvariantCulture);
                    int supplied = slot == 0 ? evaluation.Constants.Length
                        : stage.Stage == GameShaderStage.Vertex && slot == 11 ? palette != NativeEntityPalette.None ? NativeEntityPalettes.Rows : InstanceVectors(kind)
                        : slot == 12 && stage.Stage == GameShaderStage.Vertex ? 14
                        : slot == 12 && pixelViewKnown ? 14
                        : slot == 13 && frameStages.Contains(stage.Stage) ? 13
                        // Scope 13 writes six pixel vectors and one vertex vector.
                        : slot == 2 && transparentScope ? stage.Stage == GameShaderStage.Pixel ? NativeTransparentScope.Rows : 1
                        : slot == 8 && advancedScope && stage.Stage == GameShaderStage.Pixel ? NativeObjectFog.Rows
                        : decorator && stage.Stage == GameShaderStage.Vertex && slot == NativeDecorators.ScopeSlot ? NativeDecorators.ScopeRows
                        : decorator && stage.Stage == GameShaderStage.Vertex && slot == NativeDecorators.LodScopeSlot ? 1
                        : stage.Stage == GameShaderStage.Pixel && gear?.Scopes.FirstOrDefault(scope => scope.ConstantBufferSlot == slot) is { } dye
                            ? dye.Constants.Length : 0;
                    if (slot == 2 && transparentScope && count <= supplied) scopeConstants.Add(stage.Stage);
                    if (count > supplied) throw new NotSupportedException(slot == 13 && frameMissing is not null
                        ? $"Unknown frame words: {frameMissing}"
                        : slot == 8 && advancedMissing is not null ? $"Unknown transparent_advanced words: {advancedMissing}"
                        : $"Unknown constant buffer: {stage.Stage} CB{slot}.");
                }
                var sampler = Regex.Match(declaration, @"^dcl_sampler s(\d+),");
                if (sampler.Success && !evaluation.Samplers.ContainsKey(new(stage.Stage, uint.Parse(sampler.Groups[1].Value))))
                    throw new NotSupportedException($"Unknown sampler: {stage.Stage} s{sampler.Groups[1].Value}.");
                if (declaration.StartsWith("dcl_uav", StringComparison.Ordinal)
                    || declaration.StartsWith("dcl_interface", StringComparison.Ordinal))
                    throw new NotSupportedException("Unsupported shader resource declaration.");
                if (stage.Stage == GameShaderStage.Pixel)
                {
                    var output = Regex.Match(declaration, @"^dcl_output o(\d+)\.");
                    if (output.Success) targets = Math.Max(targets, int.Parse(output.Groups[1].Value) + 1);
                }
            }
            foreach (var resource in shader.TextureSlots)
            {
                if (kind != NativeMeshKind.Entity && stage.Stage == GameShaderStage.Vertex && resource.Slot == 0 && resource.Dimension == "buffer") continue;
                // The terrain draw binds its group dye texture, or nothing, at pixel t14.
                if (kind == NativeMeshKind.Terrain && stage.Stage == GameShaderStage.Pixel && resource.Slot == 14 && resource.Dimension == "texture2d") continue;
                var texture = material.Textures.FirstOrDefault(t => t.Stage == stage.Stage && t.Slot == resource.Slot);
                // An empty row binds null; a slot another source also binds has no verified order.
                if (texture is null && material.EmptyTextureSlots.Contains((stage.Stage, resource.Slot)))
                {
                    if (resource.Dimension is not ("texture2d" or "texture3d")
                        || evaluation.ResourceBindings.ContainsKey(new(stage.Stage, resource.Slot))
                        || gear is not null && (gear.PlateTextures.ContainsKey(resource.Slot)
                            || gear.Scopes.Any(scope => scope.Textures.ContainsKey(resource.Slot))))
                        throw new NotSupportedException($"Empty texture row {stage.Stage} t{resource.Slot} is unsupported.");
                    nullTextures[new(stage.Stage, resource.Slot)] = resource.Dimension;
                    continue;
                }
                if (texture is null && evaluation.ResourceBindings.TryGetValue(new(stage.Stage, resource.Slot), out var bound))
                {
                    if (!DimensionMatches(bound.Dimension, resource.Dimension))
                        throw new NotSupportedException($"TFX resource {bound.Name} does not match {stage.Stage} t{resource.Slot}.");
                    // Stage 6 fills extern 44 from other targets.
                    if (bound == PreDecalNormals && renderStage is not (1 or 2))
                        throw new NotSupportedException($"TFX resource {bound.Name} is set only in the decal stages.");
                    if ((bound == SceneDepth || bound == NativeStencil.SceneStencil) && GBufferStages.Contains(renderStage))
                        throw new NotSupportedException($"TFX resource {bound.Name} is set only after the G-buffer stages.");
                    externResources = true;
                    continue;
                }
                if (texture is null && stage.Stage == GameShaderStage.Pixel && resource.Dimension == "texture2d" && gear is not null
                    && (gear.PlateTextures.ContainsKey(resource.Slot) || gear.Scopes.Any(scope => scope.Textures.ContainsKey(resource.Slot))))
                    continue;
                if (texture is null && shadow && stage.Stage == GameShaderStage.Vertex && resource.Slot == ShadowScopeSlot
                    && resource.Dimension == "texture2d")
                {
                    shadowScope = true;
                    continue;
                }
                if (texture is null && transparentScope && stage.Stage == GameShaderStage.Pixel
                    && (resource.Slot == SceneDepthSlot && resource.Dimension == "texture2d"
                        || resource.Slot is >= FirstAbsentScopeSlot and <= LastAbsentScopeSlot
                            && resource.Dimension is "texture2d" or "texture3d"))
                {
                    scopeTextures[resource.Slot] = resource.Dimension;
                    continue;
                }
                if (texture is null) throw new NotSupportedException($"Unknown texture: {stage.Stage} t{resource.Slot}.");
                if (resource.Dimension is not ("texture2d" or "texturecube" or "texture3d"))
                    throw new NotSupportedException("Texture resource dimension is unsupported.");
            }
        }
        // shadow_generate binds no colour target, so a caster's pixel outputs are discarded.
        if (shadow ? targets > 8 : targets is < 1 or > 8) throw new NotSupportedException("Pixel output count is unsupported.");
        if (GBufferStages.Contains(renderStage) ? targets > 3 : targets > 1 && renderStage != TransparentsStage && !shadow)
            throw new NotSupportedException($"Pixel output count {targets} does not fit the {(GBufferStages.Contains(renderStage) ? "G-buffer" : "lit color")} target.");
        return new(execution, renderStates, pixelViewKnown, frameStages, targets, kind, palette)
        { ScopeConstantStages = scopeConstants, ScopeTextures = scopeTextures, ExternResources = externResources,
            ReadsWaterObject = NativeWater.ReadsObject(execution), ReadsAdvancedScope = advancedScope,
            ReadsGearDye = NativeGearDye.Reads(execution), Gear = gear, NullTextures = nullTextures,
            StencilReference = stencilDraw.Reference, ReadsShadowScope = shadowScope,
            ReadsObjectChannels = ReadsChannels(execution.Vertex) || ReadsChannels(execution.Pixel) };
    }

    // TFX opcode 0x4D pushes an object channel.
    private const byte ObjectChannelOpcode = 0x4D;

    /** A program not fully decoded counts as a reader, so it never misses its table. */
    private static bool ReadsChannels(MaterialStageData? stage)
    {
        if (stage is null || stage.TfxBytecode.Length == 0) return false;
        var program = TfxProgramInspection.Read(stage.TfxBytecode);
        return !program.FullyDecoded || program.Instructions.Any(instruction => instruction.Opcode == ObjectChannelOpcode);
    }

    /** The channel table of each part, as the viewport draws it; the opened entity's model takes the entity view's values. */
    private sealed class ChannelPicker(PackageCatalog catalog, SceneDocument scene)
    {
        public ObjectChannelTables Tables { get; } = new(catalog);
        private readonly IReadOnlyList<Vector4>? opened = scene.ObjectChannelTable;
        private readonly uint[] openedNames = scene.ObjectChannels.OrderBy(channel => channel.Index)
            .Select(channel => channel.NameHash).ToArray();

        /** Null reads (1,1,1,1) for every channel. */
        public IReadOnlyList<Vector4>? For(MeshInstance instance)
        {
            if (instance.ObjectClassTag == 0 || instance.ModelSourceConfigTag == 0) return null;
            if (opened is not null && instance.ObjectClassTag == scene.TagHash && instance.PlacementSourceTag == 0
                && Tables.Names(instance.ObjectClassTag, instance.ModelSourceConfigTag).SequenceEqual(openedNames))
                return opened;
            return Tables.Table(instance.ObjectClassTag, instance.ModelSourceConfigTag);
        }
    }

    /**
     * The object channel table of each entity part whose material reads channels, as the viewport draws it.
     * @return Parts that read no channels, or whose table is all ones, are left out.
     */
    public static Dictionary<MeshInstance, IReadOnlyList<Vector4>> ChannelTablesByPart(PackageCatalog catalog, SceneDocument scene)
    {
        var reader = new GameMaterialReader(catalog);
        var picker = new ChannelPicker(catalog, scene);
        var reads = new Dictionary<uint, bool>();
        var result = new Dictionary<MeshInstance, IReadOnlyList<Vector4>>(ReferenceEqualityComparer.Instance);
        foreach (var instance in scene.Instances)
        {
            if (instance.Mesh.Native is not { Kind: NativeMeshKind.Entity } native || picker.For(instance) is not { } table) continue;
            if (!reads.TryGetValue(native.MaterialTag, out bool read))
            {
                try
                {
                    var execution = reader.ReadExecution(native.MaterialTag);
                    read = ReadsChannels(execution.Vertex) || ReadsChannels(execution.Pixel);
                }
                catch (Exception error) when (Recoverable(error)) { read = false; }
                reads.Add(native.MaterialTag, read);
            }
            if (read) result[instance] = table;
        }
        return result;
    }

    /** Float bits of a channel table, so two equal tables give one key. */
    public static string ChannelKey(IReadOnlyList<Vector4> table) => string.Join(',', table.Select(value =>
        $"{BitConverter.SingleToUInt32Bits(value.X):X8}{BitConverter.SingleToUInt32Bits(value.Y):X8}"
        + $"{BitConverter.SingleToUInt32Bits(value.Z):X8}{BitConverter.SingleToUInt32Bits(value.W):X8}"));

    /** The draw binds instance streams at slots 1 to 3 after one mesh stream. */
    private static void CheckDecoratorLayout(NativeMeshData mesh)
    {
        if (mesh.VertexStreams.Length != NativeDecorators.FirstInstanceSlot)
            throw new NotSupportedException("Decorator mesh does not have exactly one vertex stream.");
        if (mesh.InputElements.Any(e => e.PerInstance != (e.Slot >= NativeDecorators.FirstInstanceSlot)
            || e.Slot >= NativeDecorators.FirstInstanceSlot + NativeDecorators.InstanceStreams || e.PerInstance && e.StepRate != 1))
            throw new NotSupportedException("Decorator declaration does not place instance data at slots 1 to 3.");
    }

    /** True when every shader input the mesh streams lack comes from a per-instance slot. */
    private static bool MissesOnlyInstanceStreams(NativeMeshData mesh)
    {
        var missing = mesh.ShaderInputs.Where(i => i.SystemValue == 0).Select(i => mesh.InputElements.FirstOrDefault(e =>
            e.Semantic == i.Semantic && e.SemanticIndex == i.SemanticIndex)).Where(e => e is null || e.Slot >= mesh.VertexStreams.Length).ToArray();
        return missing.Length > 0 && missing.All(e => e is { PerInstance: true });
    }

    private static bool DimensionMatches(Vortice.Direct3D.ShaderResourceViewDimension dimension, string declared) => declared switch
    {
        "texture2d" => dimension == Vortice.Direct3D.ShaderResourceViewDimension.Texture2D,
        "texture3d" => dimension == Vortice.Direct3D.ShaderResourceViewDimension.Texture3D,
        "texturecube" => dimension == Vortice.Direct3D.ShaderResourceViewDimension.TextureCube,
        _ => false
    };

    /** CB11 vectors each kind supplies: static windows of 16 instances, terrain 3 rows, rigid_model 8. */
    internal static int InstanceVectors(NativeMeshKind kind) => kind switch
    { NativeMeshKind.Static => 64, NativeMeshKind.Terrain => 3, _ => 8 };

    // Terrain CB11 row 2 XYZ hold a frame vector that is not reproduced.
    internal static readonly IReadOnlyDictionary<uint, uint> TerrainUnknownWords = new Dictionary<uint, uint> { [8] = 0, [9] = 0, [10] = 0 };

    /**
     * Resolves the stage's native pass word with the material override, as the client does.
     * The viewer keeps no stencil bits, so it drops a stencil the pass supplies and keeps its depth test.
     */
    internal static NativeRenderStateDescriptions ResolveStates(NativeRenderStateCatalog states, uint overrides, int renderStage)
    {
        var (pass, forced) = NativeStagePassStates.Get(renderStage);
        // Shadow maps keep forward depth: depth 13 tests LESS_EQUAL in the primary table.
        var table = renderStage == ShadowStage ? NativeDepthTable.Primary : NativeDepthTable.Alternate;
        var resolved = states.Resolve(pass, overrides, forced, table);
        if (((overrides | forced) & 0x8000) != 0 || !resolved.DepthStencil.StencilEnable) return resolved;
        int index = states.WithoutStencil(resolved.Selection.DepthStencil);
        return resolved with
        {
            DepthStencil = states.GetDepthStencil(index, table),
            Selection = resolved.Selection with { DepthStencil = (byte)index }
        };
    }

    /** Material textures plus a terrain part's group dye texture. */
    private static IEnumerable<uint> PartTextures(NativeScenePart part, NativeSceneMaterial material) =>
        part.Instance.Mesh.Native is { Kind: NativeMeshKind.Terrain, TerrainDyeTexture: not uint.MaxValue } native
            ? TextureTags(material).Append(native.TerrainDyeTexture) : TextureTags(material);
    private static IEnumerable<uint> TextureTags(NativeSceneMaterial material) =>
        material.Execution.Material.Textures.Where(t => t.DeclaredByShader).Select(t => t.TextureTag)
            .Concat(material.Gear?.TextureTags ?? []).Distinct();
    private static string PipelineKey(NativeMeshData mesh, bool sky) => $"{mesh.Kind}:{mesh.MaterialTag:X8}:{mesh.NativeDeclarationTag:X8}:{mesh.NativeDeclarationId}:{mesh.RenderStage}:{sky}";
    private static string GeometryKey(NativeMeshData mesh) =>
        string.Join('/', mesh.VertexStreams.Select(s => $"{s.DataTag:X8}:{s.Stride}")) + $"/{mesh.IndexDataTag:X8}:{mesh.Is32Bit}";

    /** A cloth display shader draws stream 0 as is: no palette and no read of CB11 rows 4 or 5. */
    private static bool ReadsClothDisplay(MeshInstance instance, NativeSceneMaterial material, GameMaterialReader reader)
    {
        if (!NativeClothDisplay.IsDisplayMesh(instance) || material.Palette != NativeEntityPalette.None
            || material.Execution.Material.VertexShader is not { } vertex) return false;
        // Rows 4 and 5 hold the model scale and offset, words 16 to 23.
        return !NativeShaderUsage.Get(reader.ReadShaderBytecode(vertex)).Words.TryGetValue(11, out var words)
            || !words.Any(word => word is >= 16 and < 24);
    }
    private static string GroupKey(NativeScenePart part, NativeSceneMaterial material)
    {
        var mesh = part.Instance.Mesh.Native!;
        return $"{PipelineKey(mesh, part.Instance.Layer == SceneLayer.Skybox)}|{GeometryKey(mesh)}|{mesh.FirstIndex}:{mesh.IndexCount}:{mesh.GamePrimitiveType}|{part.Scalars?.DataTag ?? 0:X8}"
            // Extern 67 is per water object, so each object gets its own group.
            + (material.ReadsWaterObject ? $"|{part.Instance.Transform.Translation}" : "")
            + (material.ReadsAdvancedScope ? $"|fog {part.Center}:{part.Radius}" : "")
            + (material.GearDyeBit != 0 ? "|dye 1" : "")
            + (material.ObjectChannelKey.Length != 0 ? $"|channels {material.ObjectChannelKey}" : "")
            + (material.Gear is { } gear ? $"|gear {gear.Key}" : "") + StencilKey(material)
            // Extern 72 is per payload and scope 8 rows 0 to 4 are per type mesh.
            + (part.Decorator is { } d ? $"|decorator {d.Payload.Tag:X8}@{d.Payload.Placement.Translation}:{d.Type.Tag:X8}:{d.MeshIndex}" : "");
    }

    /** Draws of one material with different stencil states need their own pipeline and group. */
    private static string StencilKey(NativeSceneMaterial material) =>
        material.StencilReference is { } reference ? $"|stencil {material.States.DepthStencil.StencilWriteMask:X2}:{reference:X2}" : "";

    /** A part needs finite world bounds to be drawn. */
    private static (Vector3 Min, Vector3 Max) PartBounds(MeshInstance instance,
        Dictionary<MeshData, (Vector3 Min, Vector3 Max)> cache)
    {
        var world = WorldBounds(instance, cache);
        if (!Finite(world.Min) || !Finite(world.Max)) throw new InvalidDataException("Geometry bounds are not finite.");
        return world;
    }

    /** Transform all eight corners so nonuniform scales cannot shrink the culling sphere. */
    private static (Vector3 Min, Vector3 Max) WorldBounds(MeshInstance instance,
        Dictionary<MeshData, (Vector3 Min, Vector3 Max)> cache)
    {
        if (!cache.TryGetValue(instance.Mesh, out var local))
        {
            if (instance.Mesh.Vertices.Length == 0) throw new InvalidDataException("Mesh has no vertices.");
            local = (new(float.PositiveInfinity), new(float.NegativeInfinity));
            foreach (var vertex in instance.Mesh.Vertices)
            { local.Min = Vector3.Min(local.Min, vertex.Position); local.Max = Vector3.Max(local.Max, vertex.Position); }
            cache.Add(instance.Mesh, local);
        }
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        for (int corner = 0; corner < 8; corner++)
        {
            var point = Vector3.Transform(new Vector3((corner & 1) == 0 ? local.Min.X : local.Max.X,
                (corner & 2) == 0 ? local.Min.Y : local.Max.Y, (corner & 4) == 0 ? local.Min.Z : local.Max.Z), instance.Transform);
            min = Vector3.Min(min, point); max = Vector3.Max(max, point);
        }
        return (min, max);
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    internal static bool Recoverable(Exception error) => error is InvalidDataException or NotSupportedException
        or ArgumentException or IOException or KeyNotFoundException or OverflowException;
    private static string Reason(Exception error)
    {
        string message = error.Message;
        if (message.StartsWith("Package block authentication failed", StringComparison.Ordinal))
            return "Package block authentication failed";
        if (message.StartsWith("Vertex shader contract", StringComparison.Ordinal)) return "Vertex shader contract unverified";
        // TFX reasons keep their input or opcode so each blocker is counted on its own.
        if (message.StartsWith("TFX", StringComparison.Ordinal)) return Regex.Replace(message, @" at \+0x[0-9A-F]+", "").TrimEnd('.');
        if (message.StartsWith("Unknown", StringComparison.Ordinal)) return "Shader runtime binding unresolved";
        if (message.StartsWith("Stencil", StringComparison.Ordinal)) return "Stencil reference unresolved";
        if (message.StartsWith("Blended", StringComparison.Ordinal)) return "Blend in the G-buffer stage unresolved";
        if (message.StartsWith("Pixel output count", StringComparison.Ordinal)) return "Pixel output count unsupported";
        if (message.StartsWith("Additional shader", StringComparison.Ordinal)) return "Additional shader stages unsupported";
        return message.Length <= 72 ? message.TrimEnd('.') : "Material contract unsupported";
    }
    private static string FailureReason(Exception error, string fallback) =>
        error.Message.StartsWith("Package block authentication failed", StringComparison.Ordinal)
            ? "Package block authentication failed" : fallback;
}
