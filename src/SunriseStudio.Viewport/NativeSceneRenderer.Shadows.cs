using System.Numerics;
using System.Runtime.InteropServices;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SunriseStudio.Viewport;

/** The sun: shadow_generate into four cascades, the cascade shadow mask, then technique 37. */
public sealed partial class NativeSceneRenderer
{
    // A caster group's pass marker; the shadow draw list uses the cascade index as its pass.
    private const int PassShadow = 4;
    private readonly List<DrawGroup> shadowGroups = [];
    private NativeConstantBuffer? shadowViewConstants;
    private GpuTexture? sdsmIdentity;
    private NativeRenderTargets? shadowValidation;
    // Fixed casters have one list per cascade, recorded again only when that cascade moves or caster state changes.
    private NativeDrawList[]? cascadeDrawLists;
    private NativeDrawList? shadowFrameList;
    private readonly Matrix4x4[] recordedCascades = new Matrix4x4[NativeShadowCascades.Count];
    private readonly bool[] cascadeRecorded = new bool[NativeShadowCascades.Count];
    private readonly bool[] recordCascade = new bool[NativeShadowCascades.Count];
    private readonly bool[] cullCascade = new bool[NativeShadowCascades.Count];
    private readonly int[] recordedCascadeParts = new int[NativeShadowCascades.Count];
    // Fixed parts per cascade and shadow group in the current recordings.
    private int[][] recordedGroupParts = [];
    private int shadowCasterEpoch, recordedCasterEpoch = -1, recordedShadowPose = -1;
    private long? recordedCasterRevision;
    private Func<MeshInstance, bool>? recordedCasterFilter;
    // ShadowCasterFilter per shadow group part, valid for shownRevision and shownFilter; null rows pass all.
    private bool[]?[] casterShown = [];
    private long? shownRevision;
    private Func<MeshInstance, bool>? shownFilter;
    // One sphere per shadow group holding all its part spheres, valid for sphereRevision.
    private Vector4[] groupSpheres = [];
    private int sphereRevision = -1;
    private NativeRenderTargets[]? cascadeTargets;
    private NativeShadowMask? shadowMask;
    private NativeDepthDownsample? depthDownsample;
    private NativeAmbientLighting? sunLighting;
    private NativeShadowCascade[] cascades = [];
    private readonly int[] cascadeParts = new int[NativeShadowCascades.Count];
    private readonly List<int>[] cascadeLists = Enumerable.Range(0, NativeShadowCascades.Count).Select(_ => new List<int>()).ToArray();
    private TfxInputs? frameInputs;
    private double frameSeconds;
    private float frameExposure;

    /** Atmosphere lighting adds the time-of-day sun, technique 37; off keeps technique 39. */
    public bool SunEnabled { get; set; } = true;
    /** Off leaves the sun unshadowed: the mask stays one. */
    public bool ShadowsEnabled { get; set; } = true;
    public bool SunActive { get; private set; }
    public bool ShadowsActive { get; private set; }
    public string? ShadowError { get; private set; }
    /** The cascades of the last shadowed frame; empty when no shadow ran. */
    public IReadOnlyList<NativeShadowCascade> Cascades => cascades;
    /** Caster parts per cascade in the last shadowed frame. */
    public IReadOnlyList<int> CascadePartCounts => cascadeParts;
    /** Draw calls per cascade in the last shadowed frame. */
    public IReadOnlyList<int> CascadeDrawCounts => cascadeDraws;
    private readonly int[] cascadeDraws = new int[NativeShadowCascades.Count];
    public int ShadowCasterPartCount => shadowGroups.Sum(group => group.Source.Parts.Count);
    public int LastShadowDraws { get; private set; }
    /** CPU milliseconds of the last frame's caster culling, recording and upload. */
    public double LastShadowRecordMilliseconds { get; private set; }
    /** Times the fixed casters were culled and recorded; a still camera reuses the last recording. */
    public int ShadowRecordings { get; private set; }

    /**
     * Render calls this when the viewer's visible set changes. It records the casters again only
     * while ShadowCasterRevision is unset or null, since the revision tracks the filter otherwise.
     */
    private void InvalidateShadowCasters() => shadowCasterEpoch++;
    /** The viewer's hide state for casters; caster parts are never in the visible handle list. */
    public Func<MeshInstance, bool>? ShadowCasterFilter { get; set; }
    /** The main camera's static level view; static casters switch level by it. Null draws them as the filter passes them. */
    public StaticLodView? CasterLod { get; set; }
    private StaticLodView? recordedCasterLod;
    // Placed static level inputs per shadow group part, valid for sphereRevision; null for a group with none.
    private StaticLodInstance[]?[] casterLods = [];
    /** Changes whenever ShadowCasterFilter's answer may change; null calls the filter on every recording. */
    public Func<long?>? ShadowCasterRevision { get; set; }
    /** The cascade shadow mask of the last frame: one is lit. Null when the sun did not run. */
    public GpuTexture? ShadowMaskTexture => SunActive ? shadowMask?.Mask : null;
    /** One cascade's depth map of the last shadowed frame; null when no shadow ran. */
    public NativeRenderTargets? CascadeMap(int index) =>
        ShadowsActive && cascadeTargets is { } maps && (uint)index < (uint)maps.Length ? maps[index] : null;
    private NativeAmbientLighting? Lighting => SunActive ? sunLighting : ambient;

    /** Caster bindings are created on first use so scenes without casters allocate nothing. */
    private NativeConstantBuffer ShadowViewConstants() => shadowViewConstants ??=
        Own(renderer.CreateNativeConstantBuffer(new byte[14 * 16], NativeViewInputs.UnknownVertexWords));

    // Fixed slot 67, the SDSM-off bias and scale (0, 0, 1, 1), which leaves the caster position unchanged.
    private GpuTexture SdsmIdentity() => sdsmIdentity ??= Own(renderer.CreateTexture(1, 1, Format.R32G32B32A32_Float,
        MemoryMarshal.AsBytes(new[] { new Vector4(0, 0, 1, 1) }.AsSpan()), 16));

    private NativeRenderTargets ShadowValidationTarget() => shadowValidation ??=
        Own(renderer.CreateNativeTargets(1, 1, Array.Empty<NativeColorTargetFormat>(), Format.D32_Float, true));

    /** Render keeps its material inputs for the casters' TFX. */
    private void KeepFrameInputs(TfxInputs inputs, double seconds, float ev)
    {
        frameInputs = inputs; frameSeconds = seconds; frameExposure = ev;
    }

    /**
     * Runs the shadow maps and the mask when atmosphere lighting has a time of day.
     * @return The sun inputs of technique 37, or null for technique 39.
     */
    private NativeSunLightingInputs? RenderSun(ViewerCamera camera, NativeGBufferInputs gbufferInputs,
        NativeAmbientLightingInputs lighting, NativeRenderTargets targets)
    {
        SunActive = false; ShadowsActive = false; LastShadowDraws = 0;
        Array.Clear(cascadeParts);
        // The viewer has no sun outside atmosphere lighting, except an asset's studio key. An orthographic view has no
        // linear depth for the cascades, so it draws only the studio key, unshadowed.
        bool keyed = StudioKeyActive;
        if (!keyed && (!SunEnabled || LightingMode != NativeLightingMode.Atmosphere || !AtmosphereActive || TimeOfDayFallback
            || camera.Orthographic) || cameraScope is null) return null;
        var toLight = keyed ? studioKey : LightDirection;
        try
        {
            sunLighting ??= new NativeAmbientLighting(renderer, Plan.Reader,
                Plan.Reader.ReadTfxParameterDefaults(NativeScenePlan.RenderGlobalsTag), states,
                parameterOverrides: ambientOverrides, sun: true);
            sunLighting.SunColorOverride = keyed ? StudioKeyColor : null;
            shadowMask ??= new NativeShadowMask(renderer, Plan.Reader, states);
            depthDownsample ??= new NativeDepthDownsample(renderer, Plan.Reader, states);
            depthDownsample.Render(gbufferInputs.Depth, targets.Width, targets.Height,
                NativeDepthReconstruction.Create(lighting.Depth), cameraProjection.M22);
            cascades = [];
            var depthMaps = new List<ID3D11ShaderResourceView>();
            if (ShadowsEnabled && shadowGroups.Count != 0 && !camera.Orthographic)
            {
                cascades = NativeShadowCascades.Fit(cameraView, cameraProjection, camera.NearPlane, camera.Position, toLight);
                RenderCascades();
                foreach (var map in cascadeTargets!) depthMaps.Add(map.DepthShaderView!);
                ShadowsActive = true;
            }
            var mask = shadowMask.Render(cascades, depthMaps, depthDownsample, gbufferInputs.Depth, targets.Width, targets.Height,
                cameraScope, cameraView, cameraProjection, lighting.Depth);
            SunActive = true;
            ShadowError = null;
            return new(mask, depthDownsample.MinMax!.Texture, keyed ? studioKey : SkyDirection);
        }
        catch (Exception error) when (Recoverable(error) || error is InvalidOperationException)
        {
            SunActive = false; ShadowsActive = false; cascades = [];
            ShadowError = error.Message;
            AddDiagnostic($"Sun shadows: {error.Message}");
            return null;
        }
        finally { renderer.RestoreMainTarget(); }
    }

    /** shadow_generate: every caster inside a cascade's box draws into that cascade's depth map. */
    private void RenderCascades()
    {
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        cascadeTargets ??= cascades.Select(cascade => Own(renderer.CreateNativeTargets(cascade.Resolution, cascade.Resolution,
            Array.Empty<NativeColorTargetFormat>(), Format.D32_Float, true))).ToArray();
        if (cascadeDrawLists is null)
        {
            cascadeDrawLists = new NativeDrawList[cascades.Length];
            for (int c = 0; c < cascades.Length; c++) cascadeDrawLists[c] = Own(new NativeDrawList(renderer, 1));
        }
        shadowFrameList ??= Own(new NativeDrawList(renderer, cascades.Length));
        foreach (var list in cascadeDrawLists) list.ResetCounters();
        shadowFrameList.Clear(); shadowFrameList.ResetCounters();
        var filter = ShadowCasterFilter;
        long? revision = ShadowCasterRevision?.Invoke();
        if (recordedGroupParts.Length != cascades.Length || recordedGroupParts[0].Length != shadowGroups.Count)
        {
            recordedGroupParts = new int[cascades.Length][];
            for (int c = 0; c < cascades.Length; c++) recordedGroupParts[c] = new int[shadowGroups.Count];
            Array.Clear(cascadeRecorded);
        }
        // Without a revision the visible-set epoch stands in for the filter's state.
        bool casterChanged = recordedCasterRevision != revision || revision is null && recordedCasterEpoch != shadowCasterEpoch
            || recordedShadowPose != poseRevision || !ReferenceEquals(recordedCasterFilter, filter) || recordedCasterLod != CasterLod;
        bool record = false;
        for (int c = 0; c < cascades.Length; c++)
        {
            recordCascade[c] = casterChanged || !cascadeRecorded[c] || cascades[c].ViewProjection != recordedCascades[c];
            if (!recordCascade[c]) continue;
            record = true;
            cascadeRecorded[c] = false;
            cascadeDrawLists[c].Clear();
            recordedCascadeParts[c] = 0;
            Array.Clear(recordedGroupParts[c]);
        }
        if (record) ShadowRecordings++;
        RefreshCasterShown(filter, revision);
        if (sphereRevision != poseRevision) RefreshGroupSpheres();
        // A live caster filter may not run off this thread, so only cached hide state culls in parallel.
        castersCulled = !(shownRevision is null && filter is not null);
        if (castersCulled) CullCasters(record);
        // A cascade kept from an earlier recording may still draw a group no recorded cascade holds.
        bool keptCascade = recordCascade.AsSpan(0, cascades.Length).Contains(false);
        for (int g = 0; g < shadowGroups.Count; g++)
        {
            var group = shadowGroups[g];
            // Decorators follow the camera's LOD index each frame; live slots move.
            bool perFrame = group.PerFrame;
            if (!perFrame && !record) continue;
            if (castersCulled && casterGroupMasks[g] == 0 && (perFrame || !keptCascade)) continue;
            CullShadowGroup(g, group, perFrame, filter);
            // Only a group that draws in some cascade needs its TFX.
            bool draws = false;
            for (int c = 0; c < cascades.Length; c++)
                draws |= cascadeLists[c].Count != 0 || !perFrame && !recordCascade[c] && recordedGroupParts[c][g] != 0;
            if (draws && !group.Material.Static && frameInputs is { } frame)
            {
                // Decorator externs are shared inputs, so each evaluated decorator sets its own first.
                if (group.Material.Decorator is { } decorator)
                {
                    NativeDecorators.ApplyExtern72(frame, decorator.Payload.Extern72);
                    if (decorator.Type.Ground) NativeDecorators.ApplyExtern47(frame, groundWinds.GetValueOrDefault(decorator.Type.Tag));
                }
                try { UpdateMaterial(group.Material, frame, frameSeconds, frameExposure); }
                catch (Exception error) when (Recoverable(error) || error is InvalidOperationException)
                {
                    AddDiagnostic($"Shadow material 0x{group.Source.Mesh.MaterialTag:X8}: {error.Message}");
                    continue;
                }
            }
            for (int c = 0; c < cascades.Length; c++)
            {
                var parts = CollectionsMarshal.AsSpan(cascadeLists[c]);
                if (parts.Length == 0) continue;
                if (perFrame) cascadeParts[c] += parts.Length;
                else { recordedCascadeParts[c] += parts.Length; recordedGroupParts[c][g] = parts.Length; }
                for (int start = 0; start < parts.Length; start += group.PerDraw)
                    Record(perFrame ? shadowFrameList : cascadeDrawLists[c], perFrame ? c : 0, group,
                        parts.Slice(start, Math.Min(group.PerDraw, parts.Length - start)), true);
            }
        }
        for (int c = 0; c < cascades.Length; c++)
        {
            if (!recordCascade[c]) continue;
            cascadeDrawLists[c].Upload();
            recordedCascades[c] = cascades[c].ViewProjection;
            cascadeRecorded[c] = true;
        }
        if (record)
        {
            recordedCasterEpoch = shadowCasterEpoch; recordedCasterRevision = revision;
            recordedShadowPose = poseRevision; recordedCasterFilter = filter; recordedCasterLod = CasterLod;
        }
        shadowFrameList.Upload();
        for (int c = 0; c < cascades.Length; c++) cascadeParts[c] += recordedCascadeParts[c];
        LastShadowRecordMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        var view = ShadowViewConstants();
        int shadowDraws = 0;
        for (int c = 0; c < cascades.Length; c++)
        {
            var cascade = cascades[c];
            var scope = NativeViewInputs.EvaluateCameraScope(Plan.ViewScope, checked((ushort)cascade.Resolution),
                checked((ushort)cascade.Resolution), cascade.LightView, cascade.Projection);
            renderer.UpdateNativeConstantBuffer(view, MemoryMarshal.AsBytes(scope.Vertex.AsSpan()));
            renderer.ClearNativeTargets(cascadeTargets[c], Vector4.Zero, 1);
            int before = shadowFrameList.DrawCount;
            cascadeDrawLists[c].Execute(0, cascadeTargets[c]);
            shadowFrameList.Execute(c, cascadeTargets[c]);
            cascadeDraws[c] = cascadeDrawLists[c].DrawCount + shadowFrameList.DrawCount - before;
            shadowDraws += cascadeDraws[c];
        }
        renderer.Context.VSSetShaderResource(NativeScenePlan.ShadowScopeSlot, null!);
        LastShadowDraws = shadowDraws;
    }

    /**
     * Runs CullShadowGroup's tests for every group the frame records, in parallel, into one cascade bit per
     * part and cascade. CullShadowGroup then reads the bits in part order. Needs cached caster hide state.
     */
    private void CullCasters(bool record)
    {
        if (casterBase.Length != shadowGroups.Count + 1)
        {
            casterBase = new int[shadowGroups.Count + 1];
            for (int g = 0; g < shadowGroups.Count; g++) casterBase[g + 1] = casterBase[g] + shadowGroups[g].Source.Parts.Count;
            casterMasks = new byte[casterBase[^1]];
            casterGroupMasks = new byte[shadowGroups.Count];
        }
        var view = CasterLod.GetValueOrDefault();
        bool lod = CasterLod.HasValue;
        void Cull(int from, int to)
        {
            Span<bool> inside = stackalloc bool[cascades.Length];
            for (int g = from; g < to; g++)
            {
                var group = shadowGroups[g];
                var masks = casterMasks.AsSpan(casterBase[g], group.Source.Parts.Count);
                masks.Clear();
                casterGroupMasks[g] = 0;
                if (!group.PerFrame && !record) continue;
                var sphere = groupSpheres[g];
                var center = new Vector3(sphere.X, sphere.Y, sphere.Z);
                bool any = false;
                for (int c = 0; c < cascades.Length; c++)
                    any |= inside[c] = (group.PerFrame || recordCascade[c]) && Inside(cascades[c], center, sphere.W);
                if (!any) continue;
                var shown = shownRevision is null ? null : casterShown[g];
                var parts = group.Source.Parts;
                var centers = group.Centers;
                var lods = lod ? casterLods[g] : null;
                byte hits = 0;
                for (int part = 0; part < parts.Count; part++)
                {
                    if (shown is not null && !shown[part]) continue;
                    if (lods is not null && lods[part].Levels != 0
                        && !StaticLod.Draws(lods[part].Levels, StaticLod.ShadowLevel(StaticLod.Value(lods[part], view)))) continue;
                    byte mask = 0;
                    for (int c = 0; c < cascades.Length; c++)
                        if (inside[c] && Inside(cascades[c], centers[part], parts[part].Radius)) mask |= (byte)(1 << c);
                    masks[part] = mask;
                    hits |= mask;
                }
                casterGroupMasks[g] = hits;
            }
        }
        Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, shadowGroups.Count, CasterGroupsPerTask),
            range => Cull(range.Item1, range.Item2));
    }

    // Shadow groups per parallel task of CullCasters.
    private const int CasterGroupsPerTask = 64;
    // Per caster part, bit c set when the part draws into cascade c this frame; valid while castersCulled.
    private byte[] casterMasks = [];
    // Per shadow group, the OR of its parts' casterMasks; zero means no part draws this frame.
    private byte[] casterGroupMasks = [];
    // Each shadow group's first part in casterMasks, then the total.
    private int[] casterBase = [];
    private bool castersCulled;

    /** Fills cascadeLists with the group's parts, in part order, for every cascade recorded this frame. */
    private void CullShadowGroup(int g, DrawGroup group, bool perFrame, Func<MeshInstance, bool>? filter)
    {
        foreach (var list in cascadeLists) list.Clear();
        if (castersCulled)
        {
            if (casterGroupMasks[g] == 0) return;
            var masks = casterMasks.AsSpan(casterBase[g], group.Source.Parts.Count);
            for (int part = 0; part < masks.Length; part++)
                for (int bits = masks[part], c = 0; bits != 0; bits >>= 1, c++)
                    if ((bits & 1) != 0) cascadeLists[c].Add(part);
            return;
        }
        var sphere = groupSpheres[g];
        var center = new Vector3(sphere.X, sphere.Y, sphere.Z);
        int first = -1, last = -1;
        // A part inside a cascade puts the group's sphere inside it too, so the sphere test is exact.
        for (int c = 0; c < cascades.Length; c++)
        {
            cullCascade[c] = (perFrame || recordCascade[c]) && Inside(cascades[c], center, sphere.W);
            if (!cullCascade[c]) continue;
            if (first < 0) first = c;
            last = c;
        }
        if (first < 0) return;
        var shown = shownRevision is null ? null : casterShown[g];
        bool live = shownRevision is null && filter is not null;
        var parts = group.Source.Parts;
        var centers = group.Centers;
        var lods = CasterLod.HasValue ? casterLods[g] : null;
        var view = CasterLod.GetValueOrDefault();
        for (int part = 0; part < parts.Count; part++)
        {
            if (shown is not null && !shown[part]) continue;
            var source = parts[part];
            if (live && !filter!(source.Instance)) continue;
            if (lods is not null && lods[part].Levels != 0
                && !StaticLod.Draws(lods[part].Levels, StaticLod.ShadowLevel(StaticLod.Value(lods[part], view)))) continue;
            for (int c = first; c <= last; c++)
                if (cullCascade[c] && Inside(cascades[c], centers[part], source.Radius)) cascadeLists[c].Add(part);
        }
    }

    /** Caches ShadowCasterFilter per part while the revision and the filter stay the same. */
    private void RefreshCasterShown(Func<MeshInstance, bool>? filter, long? revision)
    {
        if (revision is null) { shownRevision = null; return; }
        if (shownRevision == revision && ReferenceEquals(shownFilter, filter) && casterShown.Length == shadowGroups.Count) return;
        if (casterShown.Length != shadowGroups.Count) casterShown = new bool[]?[shadowGroups.Count];
        for (int g = 0; g < shadowGroups.Count; g++)
        {
            if (filter is null) { casterShown[g] = null; continue; }
            var parts = shadowGroups[g].Source.Parts;
            var row = casterShown[g] is { } kept && kept.Length == parts.Count ? kept : new bool[parts.Count];
            for (int part = 0; part < parts.Count; part++) row[part] = filter(parts[part].Instance);
            casterShown[g] = row;
        }
        shownRevision = revision; shownFilter = filter;
    }

    /** Bounds each shadow group's part spheres; a group with a non-finite part always passes. */
    private void RefreshGroupSpheres()
    {
        if (groupSpheres.Length != shadowGroups.Count) groupSpheres = new Vector4[shadowGroups.Count];
        if (casterLods.Length != shadowGroups.Count) casterLods = new StaticLodInstance[]?[shadowGroups.Count];
        for (int g = 0; g < shadowGroups.Count; g++)
        {
            var group = shadowGroups[g];
            var parts = group.Source.Parts;
            casterLods[g] = parts.Count != 0 && parts[0].Instance.Mesh.StaticLod is not null
                ? parts.Select(part => StaticLod.Place(part.Instance)).ToArray() : null;
            var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
            bool finite = true;
            for (int part = 0; part < parts.Count && finite; part++)
            {
                var c = group.Centers[part]; float r = parts[part].Radius;
                finite = float.IsFinite(c.X) && float.IsFinite(c.Y) && float.IsFinite(c.Z) && float.IsFinite(r);
                min = Vector3.Min(min, c - new Vector3(r)); max = Vector3.Max(max, c + new Vector3(r));
            }
            if (!finite || parts.Count == 0) { groupSpheres[g] = new(0, 0, 0, float.PositiveInfinity); continue; }
            var center = (min + max) * .5f;
            float radius = 0;
            for (int part = 0; part < parts.Count; part++)
                radius = MathF.Max(radius, Vector3.Distance(center, group.Centers[part]) + parts[part].Radius);
            // Margin for float rounding in Inside, so the sphere never fails where a part passes.
            float magnitude = MathF.Max(MathF.Abs(center.X), MathF.Max(MathF.Abs(center.Y), MathF.Abs(center.Z)));
            groupSpheres[g] = new(center, radius * 1.001f + 0.5f + magnitude * 1e-4f);
        }
        sphereRevision = poseRevision;
    }

    /** A bounding sphere against the cascade's light-space box, which reaches CasterHeight toward the sun. */
    private static bool Inside(NativeShadowCascade cascade, Vector3 center, float radius)
    {
        var p = Vector3.Transform(center, cascade.LightView);
        return MathF.Abs(p.X) <= cascade.Extents.X * 0.5f + radius && MathF.Abs(p.Y) <= cascade.Extents.Y * 0.5f + radius
            && p.Z <= radius && p.Z >= -cascade.Extents.Z - radius;
    }

    private void DisposeShadows()
    {
        sunLighting = null;
        shadowMask?.Dispose(); shadowMask = null;
        depthDownsample?.Dispose(); depthDownsample = null;
    }
}
