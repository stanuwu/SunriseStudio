using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using SunriseStudio.Viewport;
using Vortice.DXGI;

string? Option(string name) => args.Contains(name) ? args[Array.IndexOf(args, name) + 1] : null;
var states = new NativeRenderStateCatalog();
Check(states.Resolve(0, 0, 0x81818181, NativeDepthTable.Alternate).Selection == new NativeStateSelection(1, 1, 1, 1),
    "Sky lighting forced states changed.");
if (Option("--game-root") is not { } game)
{
    Console.WriteLine("shading states=pass installed=not_requested");
    return;
}
using var catalog = PackageCatalog.Open(game);
var hardware = PassFixture(catalog, false);
var warp = PassFixture(catalog, true);
Check(Vector3.Distance(hardware, warp) < .01f, "Hardware and WARP native reflection differ.");
Console.WriteLine($"shading hardware={hardware} warp={warp} native_passes=pass");
if (args.Contains("--world") || args.Contains("--local-cubemap"))
    WorldFixture(catalog, Option("--output") ?? "captures/shading", args.Contains("--local-cubemap"));

/** Owned texture inputs verify that native sky lighting follows input color and exposure. */
static Vector3 PassFixture(PackageCatalog catalog, bool warp)
{
    using var renderer = new Dx11Renderer(IntPtr.Zero, 64, 64, warp);
    var reader = new GameMaterialReader(catalog);
    var defaults = reader.ReadTfxParameterDefaults(NativeScenePlan.RenderGlobalsTag);
    var parameters = defaults.CreateSnapshot();
    CubemapFixture.Run(renderer, reader, parameters);
    using var sky = new NativeSkyLighting(renderer, reader, defaults, parameters, new());
    byte[] Bytes(Vector4 value) => MemoryMarshal.AsBytes(new[] { value }.AsSpan()).ToArray();
    using var lookup = renderer.CreateTextureResource(1, 1, 1, 1, 1, Format.R32G32B32A32_Float, false, true,
        [new(0, 0, Bytes(new(.2f, .4f, .6f, 0)), 16, 16)]);
    byte[] settingsBytes = new byte[NativeAtmosphereSettings.Size];
    ref var atmosphere = ref TagView.Edit<UnknownAtmospherePayload>(settingsBytes);
    (atmosphere.Lookup3dMode0, atmosphere.Lookup3dSecond, atmosphere.Lookup2d) = (1, 2, 3);
    for (int i = 0; i < 16; i++) atmosphere.TimeOfDayKeys[i] = i / 16f;
    var view = Matrix4x4.CreateLookAt(new(2, -5, 3), Vector3.Zero, Vector3.UnitZ);
    var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, 1, .1f, 1000);
    var camera = NativeViewInputs.EvaluateCameraScope(reader.ReadScopeExecution(0x80B356A5), 64, 64, view, projection);
    using var depth = renderer.CreateNativeTargets(64, 64, [Format.R16G16B16A16_Float], Format.D32_Float, true);
    renderer.ClearNativeTargets(depth, Vector4.Zero, .5f);
    var frame = new NativeAtmosphereFrame(NativeAtmosphereSettings.Parse(settingsBytes), lookup, lookup,
        camera, view, projection, .5f, Vector3.UnitZ, depth.DepthShaderView!, 64, 64);
    Check(sky.Generate(frame), "Native atmosphere fixture failed to generate.");
    Check(sky.Reflection.MipLevels == 10, "Reflection filter omitted roughness mip levels.");
    Check(!reader.ReadExecution(NativeSkyLighting.GenerateTag).Pixel.Shader!.Declarations
        .Where(x => x.StartsWith("dcl_input_ps", StringComparison.Ordinal)).Any(x => !x.Contains("v2.xy, position", StringComparison.Ordinal)),
        "Hemisphere PS now consumes projection-dependent interpolants.");
    GpuTexture Image(Vector4 color) => renderer.CreateTexture(64, 64, Format.R32G32B32A32_Float,
        MemoryMarshal.AsBytes(Enumerable.Repeat(color, 64 * 64).ToArray().AsSpan()), 64 * 16);
    using var albedo = Image(new(.25f, .25f, .25f, 0));
    using var normal = Image(new(.5f, .5f, .95f, 1));
    using var third = Image(new(1, .5f, 0, 1));
    var gbuffer = new NativeGBufferInputs(albedo, normal, third, depth.DepthShaderView!);
    var points = new[] { (16, 16), (32, 32), (48, 48) };
    sky.Render(gbuffer, camera, view, projection, 0, 0);
    var original = renderer.ReadTexturePixels(sky.Ibl!, points);
    var diffuse = renderer.ReadTexturePixels(sky.Diffuse!, points);
    Check(original.All(x => float.IsFinite(x.X + x.Y + x.Z) && x.X > .001f && x.Z > x.Y && x.Y > x.X),
        "Native reflection does not preserve the owned lookup color.");
    Check(diffuse.All(x => x.X > .001f), "Native sky pass did not write diffuse lighting.");
    uint frameLookupTag = reader.ReadFrameTexture24(NativeScenePlan.RenderGlobalsTag);
    var frameLookup = new TextureDataReader(catalog).Read(frameLookupTag);
    Check(frameLookup.Width == 64 && frameLookup.Height == 128 && !frameLookup.IsCube && !frameLookup.IsVolume,
        "Installed frame lookup24 shape changed.");
    using var materialLookup = renderer.CreateTextureResource(frameLookup.Width, frameLookup.Height, frameLookup.Depth,
        frameLookup.ArraySize, frameLookup.MipCount, (Format)frameLookup.Format, frameLookup.IsCube, frameLookup.IsVolume,
        frameLookup.Subresources.Select(x => new TextureSubresourceUpload(x.MipLevel, x.ArraySlice, x.Data, x.RowPitch, x.SlicePitch)).ToArray());
    using var conditionalAlbedo = Image(new(.25f, .25f, .25f, .75f));
    using var noFog = Image(Vector4.Zero);
    using var ssao = Image(Vector4.One);
    using var output = renderer.CreateNativeTargets(64, 64, Format.R16G16B16A16_Float);
    using var deferred = new NativeDeferredShading(renderer, reader, new(), parameters);
    var deferredInputs = new NativeDeferredShadingInputs(gbuffer with { Albedo = conditionalAlbedo }, sky.Diffuse, null,
        ssao, noFog, noFog, camera, view, projection, 0, 0, NativeAtmosphere.ReadSlots(defaults, parameters, out _), 1, sky.Ibl);
    deferred.Render(deferredInputs, output);
    var missingLookup = renderer.ReadTexturePixels(output.Textures[0], points);
    deferred.Render(deferredInputs with { FrameTexture24 = materialLookup }, output);
    var suppliedLookup = renderer.ReadTexturePixels(output.Textures[0], points);
    Check(missingLookup.All(x => Xyz(x).LengthSquared() < 1e-8f)
        && suppliedLookup.All(x => Xyz(x).LengthSquared() > .00001f),
        "The conditional material response did not recover from its missing frame lookup.");
    var fogSlots = NativeAtmosphere.ReadSlots(defaults, parameters, out _);
    fogSlots[6] = new(.5f); fogSlots[13] = new(.5f);
    var neutralFog = deferredInputs with { FrameTexture24 = materialLookup, AtmosphereSlots = fogSlots };
    renderer.ClearNativeTargets(depth, Vector4.Zero, .01f);
    deferred.Render(neutralFog, output);
    var near = renderer.ReadTexturePixels(output.Textures[0], points);
    renderer.ClearNativeTargets(depth, Vector4.Zero, .9999f);
    deferred.Render(neutralFog, output);
    var far = renderer.ReadTexturePixels(output.Textures[0], points);
    Check(near.Zip(far).All(pair => Vector3.Distance(Xyz(pair.First), Xyz(pair.Second)) < 1e-5f),
        "Neutral fog changed native material response with distance.");
    deferred.Render(neutralFog with { AtmosphereLookup = ssao }, output);
    var attenuated = renderer.ReadTexturePixels(output.Textures[0], points);
    Check(attenuated.Zip(far).Any(pair => Xyz(pair.First).Length() < Xyz(pair.Second).Length() * .8f),
        "Fog control fixture did not exercise the transmittance path.");
    renderer.ClearNativeTargets(depth, Vector4.Zero, .5f);
    Console.WriteLine($"shading_lookup device={(warp ? "warp" : "hardware")} tag={frameLookupTag:X8} missing={missingLookup[1]} supplied={suppliedLookup[1]}");
    sky.Render(gbuffer, camera, view, projection, 0, 1);
    var exposed = renderer.ReadTexturePixels(sky.Ibl!, points);
    for (int i = 0; i < original.Length; i++)
        Check(Vector3.Distance(Xyz(exposed[i]), Xyz(original[i]) * 2) < .03f, "Native exposure did not scale reflection by two.");
    using var blackLookup = renderer.CreateTextureResource(1, 1, 1, 1, 1, Format.R32G32B32A32_Float, false, true,
        [new(0, 0, Bytes(Vector4.Zero), 16, 16)]);
    int generation = sky.GenerationCount;
    Check(sky.Generate(frame with { SecondLookup = blackLookup }) && sky.GenerationCount == generation + 1,
        "Changing the second atmosphere lookup reused a stale reflection.");
    Check(sky.Generate(frame with { FirstLookup = blackLookup, SecondLookup = blackLookup }), "Black lookup was rejected.");
    sky.Render(gbuffer, camera, view, projection, 0, 0);
    Check(renderer.ReadTexturePixels(sky.Ibl!, points).All(x => Xyz(x).LengthSquared() < 1e-8f),
        "Sky reflection retained stale color or added an unauthored light.");
    return Xyz(original[1]);
}

/** The installed world comparison keeps geometry and material inputs identical across lighting modes. */
static void WorldFixture(PackageCatalog catalog, string output, bool localOnly)
{
    Directory.CreateDirectory(output);
    var world = new WorldLoader(catalog, StudioNameIndex.Create(catalog)).LoadMission(0x80EDC19B, 0x80B48062,
        new() { IncludeLowerLods = false });
    if (localOnly) CubemapPreflight(catalog, world.Scene.Cubemaps.First(item => item.Cubemap.Texture0Tag == 0x80C43111));
    using var renderer = new Dx11Renderer(IntPtr.Zero, 800, 500, false);
    using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
    viewport.SetScene(world.Scene);
    viewport.SetNativeScene(AssetViewport.PrepareNativeScene(catalog, world.Scene, textureBudgetBytes: long.MaxValue));
    viewport.Visibility.Filter = item => item is MeshInstance;
    var native = viewport.NativeRenderer ?? throw new InvalidOperationException(viewport.NativeError);
    var spawn = world.Scene.Markers.Where(x => x.Layer == SceneLayer.Spawns)
        .OrderByDescending(x => world.AllStates.Any(state => state.PackageEnabled && state.Ordinal == x.StateOrdinal
            && state.BubbleNameHash == world.CanonicalBubble(x.BubbleHash))).First();
    var other = world.Scene.Markers.First(x => x.Layer == SceneLayer.Spawns && world.CanonicalBubble(x.BubbleHash)
        != world.CanonicalBubble(spawn.BubbleHash));
    var comparisons = new List<object>();
    for (int index = 0; index < (localOnly ? 1 : 3); index++)
    {
        var marker = index == 2 ? other : spawn;
        var facing = Vector3.Transform(Vector3.UnitX, marker.Rotation); facing.Z = 0;
        facing = facing.LengthSquared() > 1e-6f ? Vector3.Normalize(facing) : Vector3.UnitX;
        if (index == 1) facing = -facing;
        var eye = marker.Position + Vector3.UnitZ * 1.7f;
        viewport.Camera.LookFrom(eye, eye + facing * 10);
        NativeShadingReport Capture(bool sky, string? label = null)
        {
            native.SkyLightingEnabled = sky;
            native.RequestExposureWarmup();
            var image = viewport.RenderImage(800, 500, 2);
            return NativeShadingInspection.Capture(renderer, viewport, image,
                Path.Combine(output, $"view-{index}-{label ?? (sky ? "native-sky" : "ambient")}"));
        }
        var before = Capture(false);
        var after = Capture(true);
        Check(after.SkyLighting && after.SkyLightingError is null && after.Deferred && after.LightingError is null,
            after.SkyLightingError ?? after.LightingError ?? "Native world sky lighting is inactive.");
        Check(before.AlbedoRange == after.AlbedoRange, "Lighting changed the underlying material color.");
        Check(after.IblRange is { Maximum: > 0 } && after.SceneRange.Maximum > 0, "Sky lighting was absent from the world output.");
        if (index == 0)
        {
            Check(before.SceneBlack > 50 && after.SceneBlack < before.SceneBlack / 4,
                "Native reflection did not remove the spawn's missing-specular black pixels.");
            Check(after.ExposureStops < before.ExposureStops - 2, "Missing sky lighting still requires the old exposure gain.");
        }
        native.ValidateLighting();
        if (localOnly)
        {
            Check(native.LocalCubemapsActive && native.LocalCubemapDrawCount > 0 && native.LocalCubemapError is null,
                native.LocalCubemapError ?? "Installed local cubemap pass did not draw.");
            native.LocalCubemapsEnabled = false;
            var skyOnly = Capture(true, "atmosphere-only");
            native.LocalCubemapsEnabled = true;
            var local = Capture(true, "local-cubemaps");
            Check(local.AlbedoRange == skyOnly.AlbedoRange && local.IblRange != skyOnly.IblRange,
                "Installed local cubemaps did not change lighting independently of material output.");
            native.AtmosphereIndex = -1; viewport.AutoAtmosphere = false;
            native.RequestExposureWarmup(); viewport.RenderImage(800, 500, 2);
            Check(!native.LocalCubemapsActive && native.IblLighting is null,
                "Deactivating the atmosphere retained stale local cubemap lighting.");
            Console.WriteLine($"cubemap_installed ready={native.LocalCubemapCount} local_ibl={local.IblRange} sky_ibl={skyOnly.IblRange}");
        }
        comparisons.Add(new { View = index, marker.BubbleHash, Eye = eye, Before = before, After = after });
        Console.WriteLine($"shading_world view={index} black_before={before.SceneBlack} black_after={after.SceneBlack} "
            + $"stops_before={before.ExposureStops:F2} stops_after={after.ExposureStops:F2} "
            + $"luminance_max_before={before.FinalRange.Maximum:F3} luminance_max_after={after.FinalRange.Maximum:F3}");
    }
    File.WriteAllText(Path.Combine(output, "comparison.json"), JsonSerializer.Serialize(comparisons,
        new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
}

static Vector3 Xyz(Vector4 value) => new(value.X, value.Y, value.Z);
static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }

/** Budget and unsupported-variant checks must finish before any cube payload is read. */
static void CubemapPreflight(PackageCatalog catalog, PlacedCubemap source)
{
    uint tag = source.Cubemap.Texture0Tag;
    Check(catalog.TryGetEntry(tag, out var entry), "Cubemap fixture texture is missing.");
    byte[] header = catalog.Read(tag);
    uint large = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x24));
    var prior = catalog.ReadOverride;
    catalog.ReadOverride = candidate => candidate == entry!.Reference || candidate == large
        ? throw new InvalidOperationException("Cubemap payload was decoded before preflight completed.") : prior?.Invoke(candidate);
    try
    {
        var scene = new SceneDocument(); scene.Cubemaps.Add(source);
        var limited = NativeScenePlan.Prepare(catalog, scene, textureBudgetBytes: 0);
        Check(limited.TextureBytes == 0 && limited.Cubemaps.Count == 0
            && limited.CubemapSkippedReasons.Keys.Any(reason => reason.Contains("budget", StringComparison.OrdinalIgnoreCase)),
            "Environment textures bypassed the caller's budget.");
        byte[] unsupported = source.Cubemap.ResourceData.ToArray();
        TagView.Edit<UnknownCubemapPayload>(unsupported).Flag16Value = 1;
        scene.Cubemaps.Clear(); scene.Cubemaps.Add(source with { Cubemap = new(unsupported) });
        var rejected = NativeScenePlan.Prepare(catalog, scene, textureBudgetBytes: long.MaxValue);
        Check(rejected.Cubemaps.Count == 0 && rejected.CubemapSkippedReasons.Keys.Any(reason => reason.Contains("technique 520", StringComparison.Ordinal)),
            "Unsupported cubemap variant was not rejected during preflight.");
    }
    finally { catalog.ReadOverride = prior; }
    Console.WriteLine("cubemap_preflight budget=pass unsupported=pass payload_reads=none");
}
