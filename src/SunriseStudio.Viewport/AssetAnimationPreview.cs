using System.Numerics;
using ImGuiNET;
using SunriseStudio.Animation;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

/** Package readers are borrowed; the catalog must outlive pending clip loads. */
public sealed partial class AssetAnimationSource
{
    private readonly PackageCatalog catalog;
    public SkeletonRig Rig { get; }
    public IReadOnlyList<ModelSkinBinding> SkinBindings { get; }
    public AnimationRigBinding? Binding { get; }
    public IReadOnlyList<EntityAnimationReference> Clips { get; }
    public IReadOnlyDictionary<uint, string> ClipNames { get; }
    public IReadOnlyList<uint> BaseClipTags { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public string? BindingError { get; }

    private AssetAnimationSource(PackageCatalog catalog, SkeletonRig rig,
        IReadOnlyList<ModelSkinBinding> skins, AnimationRigBinding? binding,
        IReadOnlyList<EntityAnimationReference> clips, IReadOnlyList<string> diagnostics, string? error,
        Func<uint, string?>? clipNames)
    {
        this.catalog = catalog;
        Rig = rig;
        SkinBindings = skins;
        Binding = binding;
        Clips = clips;
        Diagnostics = diagnostics;
        BindingError = error;
        // Labels carry no tag, so a repeated name gets " 2", " 3" in clip order to stay unique.
        var labels = new Dictionary<uint, string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in clips.GroupBy(x => x.ClipTag))
        {
            string name = AssetSceneLoader.ClipName(catalog, g.Key, g.First().Index, clipNames, EntityAnimationCatalog.UniqueName(g));
            string label = name;
            for (int copy = 2; !used.Add(label); copy++) label = $"{name} {copy}";
            labels.Add(g.Key, label);
        }
        ClipNames = labels;
        BaseClipTags = clips.Select(value => value.ClipTag).Distinct().Where(tag =>
        {
            byte[] data = catalog.Read(tag);
            return data.Length > 0x181 && data[0x181] == 0;
        }).ToArray();
    }

    /**
     * Only typed entity links supply the skeleton, skin weights, and animation controls.
     * @param boneNameResolver Resolves bone name hashes.
     * @param clipNameResolver Resolves clip tags to names; asked before the package name.
     */
    public static AssetAnimationSource Prepare(PackageCatalog catalog, uint entityTag,
        CancellationToken token = default, Func<uint, string?>? boneNameResolver = null,
        Func<uint, string?>? clipNameResolver = null)
    {
        token.ThrowIfCancellationRequested();
        var rig = new SkeletonReader(catalog) { NameResolver = boneNameResolver }.Read(entityTag);
        var skins = new List<ModelSkinBinding>();
        var diagnostics = new List<string>();
        foreach (uint model in rig.ModelTags)
        {
            token.ThrowIfCancellationRequested();
            try { skins.AddRange(new ModelSkinReader(catalog).Read(model, rig)); }
            catch (NotSupportedException error) { diagnostics.Add(error.Message); }
        }
        foreach (var cloth in rig.ClothModels)
        {
            token.ThrowIfCancellationRequested();
            try { skins.AddRange(new ModelSkinReader(catalog).ReadClothInput(cloth, rig)); }
            catch (NotSupportedException error) { diagnostics.Add(error.Message); }
        }
        if (skins.Any(x => x.PaletteKind == SkinPaletteKind.ClothInput))
            diagnostics.Add("Cloth shows its skeletal input; cloth simulation is unavailable.");
        AnimationRigBinding? binding = null;
        IReadOnlyList<EntityAnimationReference> clips = [];
        string? bindingError = null;
        try
        {
            token.ThrowIfCancellationRequested();
            clips = new EntityAnimationCatalog(catalog).Read(rig.EntityTag, resolveNames: true,
                cancellationToken: token);
            binding = new AnimationRigReader(catalog).Read(rig);
        }
        catch (Exception error) when (error is NotSupportedException or InvalidDataException)
        {
            bindingError = error.Message;
            diagnostics.Add(error.Message);
        }
        return new(catalog, rig, skins.AsReadOnly(), binding, clips, diagnostics.AsReadOnly(), bindingError,
            clipNameResolver);
    }

    /** Relative and override clips need a base layer and cannot play as absolute poses. */
    public AnimationControlClip ReadClip(uint tag, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (Binding is null) throw new NotSupportedException(BindingError ?? "No supported animation rig.");
        if (!Clips.Any(x => x.ClipTag == tag))
            throw new InvalidDataException("Clip is not linked to this entity.");
        var clip = new AnimationControlReader(catalog).Read(tag);
        token.ThrowIfCancellationRequested();
        Binding.ApplyPreview(clip, 0, Rig.CreateBindPose());
        return clip;
    }
}

/** Playback changes GPU vertices and palettes while the source scene keeps its bind geometry. */
public sealed class AssetAnimationPreview : IDisposable
{
    // RigidEntries above zero repeats skin matrix 0 that many times for the native palette.
    // Attached parts follow one rig bone's object transform and ignore their weights.
    private sealed record SkinnedPart(MeshData Mesh, SceneVertex[] BindVertices,
        VertexSkinWeights[] Weights, SkinPaletteKind Palette, RenderVertex[] Upload, int RigidEntries,
        BoneAttachment? Attached);
    private sealed record ClothDisplay(byte[] Bind, IReadOnlyList<VertexSkinWeights> Weights, byte[] Posed);
    private readonly Dictionary<MeshData, SkinnedPart> parts = [];
    // Cloth display streams for the game shaders, by position header tag.
    private readonly Dictionary<uint, ClothDisplay> cloth = [];
    private readonly Action<MeshData, RenderVertex[]> upload;
    private readonly Action poseChanged;
    private readonly CancellationTokenSource lifetime = new();
    private Task<AnimationControlClip>? loading;
    private uint pendingTag;
    private bool loadingBase, playLoaded;
    private bool disposed, playing, loop = true, showBones;
    private float seconds, rate = 1;
    private string clipSearch = "";
    private Vector2 poseInput, aimAngles, lookAngles;
    private readonly SkeletonPose? weaponPose;

    public AssetAnimationSource Source { get; }
    public SkeletonPose Pose { get; }
    public SkeletonPose DisplayPose => weaponPose ?? Pose;
    public AnimationControlClip? Clip { get; private set; }
    public AnimationControlClip? BaseClip { get; private set; }
    public string? Error { get; private set; }
    public int SkinnedPartCount => parts.Count;
    public bool IsLoading => loading is not null;
    public bool Playing { get => playing; set => playing = value && Clip is not null; }
    public bool Loop { get => loop; set => loop = value; }
    public bool ShowBones { get => showBones; set => showBones = value; }
    public float TimeSeconds => seconds;

    /** Parameter of a pose-space clip; ignored for clips played by time. */
    public Vector2 PoseInput
    {
        get => poseInput;
        set
        {
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y)) throw new ArgumentOutOfRangeException(nameof(value));
            poseInput = value;
            ApplySample();
        }
    }
    public float DurationSeconds => Clip is null ? 0 : Math.Max(Clip.DurationSeconds,
        Clip.PoseMode == 0 ? 0 : BaseClip?.DurationSeconds ?? 0);
    public float PlaybackRate
    {
        get => rate;
        set
        {
            if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            rate = Math.Clamp(value, .05f, 4);
        }
    }

    /** Compact mesh indices are mapped back to their authored skin stream before playback starts. */
    public AssetAnimationPreview(AssetAnimationSource source, SceneDocument scene,
        Action<MeshData, RenderVertex[]> upload, Action poseChanged)
    {
        Source = source;
        Pose = source.Rig.CreateBindPose();
        source.Binding?.ApplyBindPose(Pose);
        weaponPose = source.WeaponBinding?.WeaponRig.CreateBindPose();
        this.upload = upload;
        this.poseChanged = poseChanged;
        foreach (var mesh in scene.Instances.Select(x => x.Mesh).Distinct())
        {
            var binding = source.SkinBindings.FirstOrDefault(x =>
                x.ModelTag == mesh.TagHash && x.PartOffsets.Contains(mesh.PartIndex));
            if (binding is null || mesh.SourceVertexIndices.Length != mesh.Vertices.Length) continue;
            if (mesh.SourceVertexIndices.Any(i => i >= binding.Vertices.Count))
                throw new InvalidDataException("Scene vertex exceeds its authored skin stream.");
            parts.Add(mesh, new(mesh, mesh.Vertices.ToArray(),
                mesh.SourceVertexIndices.Select(i => binding.Vertices[(int)i]).ToArray(),
                binding.PaletteKind, new RenderVertex[mesh.Vertices.Length], source.RigidSkins.GetValueOrDefault(binding),
                source.AttachedSkins.GetValueOrDefault(binding)));
        }
        foreach (var binding in source.SkinBindings.Where(x => x.PaletteKind == SkinPaletteKind.ClothInput))
        {
            var instance = scene.Instances.FirstOrDefault(x => x.Mesh.TagHash == binding.ModelTag
                && NativeClothDisplay.IsDisplayMesh(x) && x.Mesh.Native!.VertexStreams[0].HeaderTag == binding.PositionHeaderTag);
            if (instance is null || cloth.ContainsKey(binding.PositionHeaderTag)) continue;
            var bind = NativeClothDisplay.BindStream(instance.Mesh.Native!);
            if (bind.Length != binding.Vertices.Count * ClothDisplaySkin.Stride)
                throw new InvalidDataException("Cloth display stream differs from its authored skin stream.");
            cloth.Add(binding.PositionHeaderTag, new(bind, binding.Vertices, new byte[bind.Length]));
        }
    }

    /** Posed stream 0 of a cloth display mesh by position header tag; null when none is bound. */
    public ReadOnlyMemory<byte>? ClothStreamFor(uint positionHeaderTag) =>
        cloth.TryGetValue(positionHeaderTag, out var display) ? new ReadOnlyMemory<byte>(display.Posed) : null;

    public int ClothDisplayCount => cloth.Count;

    /**
     * Clip decoding stays off the graphics thread; one pending selection is allowed at a time.
     * @param play False holds the loaded clip on frame 0.
     */
    public void SelectClip(uint tag, bool play = true)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (loading is not null) return;
        pendingTag = tag;
        loadingBase = false;
        playLoaded = play;
        playing = false;
        Error = null;
        var token = lifetime.Token;
        loading = Task.Run(() => Source.ReadClip(tag, token), token);
    }

    /** A layer base is a linked absolute clip or the rig's authored control pose. */
    public void SelectBaseClip(uint? tag)
    {
        if (loading is not null) return;
        if (tag is null) { BaseClip = null; ApplySample(); return; }
        if (!Source.BaseClipTags.Contains(tag.Value)) throw new ArgumentException("Choose an absolute base clip.", nameof(tag));
        pendingTag = tag.Value;
        loadingBase = true;
        playing = false;
        Error = null;
        var token = lifetime.Token;
        loading = Task.Run(() => Source.ReadClip(tag.Value, token), token);
    }

    /** Validation uses a separate pose so a rejected clip cannot change the visible skeleton. */
    public void SetClip(AnimationControlClip clip, bool play = true)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Source.Binding is null) throw new NotSupportedException(Source.BindingError);
        Source.Binding.ApplyPreview(clip, 0, Source.Rig.CreateBindPose(), BaseClip);
        Clip = clip;
        seconds = 0;
        poseInput = clip.PoseSpace is { } space ? (space.Bounds.Minimum + space.Bounds.Maximum) / 2 : Vector2.Zero;
        playing = play;
        Error = null;
        ApplySample();
    }

    /** Seeking pauses playback and keeps time inside the selected clip. */
    public void Seek(float timeSeconds)
    {
        if (Clip is null) return;
        ArgumentOutOfRangeException.ThrowIfNegative(timeSeconds);
        if (!float.IsFinite(timeSeconds)) throw new ArgumentOutOfRangeException(nameof(timeSeconds));
        playing = false;
        seconds = Math.Clamp(timeSeconds, 0, DurationSeconds);
        ApplySample();
    }

    /** The binding's rest layer, such as a guardian's neutral face, still applies. */
    public void ResetBindPose()
    {
        playing = false;
        Clip = null;
        seconds = 0;
        Error = null;
        if (Source.Binding is { } binding) binding.ApplyBindPose(Pose);
        else Pose.ResetToBindPose();
        UploadPose();
    }

    /** Completed clips are adopted and sampled only on the graphics thread. */
    public void Tick(float deltaSeconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!float.IsFinite(deltaSeconds)) throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        if (loading is { IsCompleted: true } completed)
        {
            loading = null;
            try
            {
                var next = completed.GetAwaiter().GetResult();
                if (loadingBase) { BaseClip = next; ApplySample(); }
                else SetClip(next, playLoaded);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { playing = false; Error = error.Message; }
        }
        if (!playing || Clip is null) return;
        seconds += Math.Clamp(deltaSeconds, 0, .25f) * rate;
        if (loop && DurationSeconds > 0) seconds %= DurationSeconds;
        else if (seconds >= DurationSeconds) { seconds = DurationSeconds; playing = false; }
        ApplySample();
    }

    private void ApplySample()
    {
        if (Clip is null || Source.Binding is null) return;
        try
        {
            Source.Binding.AimDirection = AnimationPoseSpace.Direction(aimAngles.X, aimAngles.Y);
            Source.Binding.LookDirection = AnimationPoseSpace.Direction(lookAngles.X, lookAngles.Y);
            if (Clip.PoseSpace is not null)
            {
                // A pose clip is a parameter-driven layer over the base clip or the rig pose.
                var entries = new List<AnimationLayerEntry>();
                if (BaseClip is not null) entries.Add(new(0, 1, [new(BaseClip, seconds * 30, 1)]));
                var samples = AnimationLayerStack.PoseSamples(Clip, poseInput.X, poseInput.Y);
                if (samples.Count != 0) entries.Add(new(1, 1, samples));
                Source.Binding.ApplyStack(entries, Pose);
            }
            else Source.Binding.ApplyPreview(Clip, seconds, Pose, BaseClip, loop: playing && loop);
            UploadPose();
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException
            or ArgumentException or InvalidOperationException)
        { playing = false; Error = error.Message; }
    }

    /** Cloth uses its pre-correction palette; zero-weight sentinel bones are never read. */
    private void UploadPose()
    {
        if (weaponPose is not null) Source.WeaponBinding!.Apply(Pose, weaponPose);
        foreach (var part in parts.Values)
        {
            var palette = part.Palette == SkinPaletteKind.ClothInput
                ? Pose.ClothInputSkinMatrices : DisplayPose.SkinMatrices;
            for (int i = 0; i < part.Upload.Length; i++)
            {
                var original = part.BindVertices[i];
                if (part.Attached is { } attached)
                {
                    part.Upload[i] = new(Vector3.Transform(original.Position, Pose.ObjectTransforms[attached.Bone]),
                        Vector3.Zero, original.TexCoord);
                    continue;
                }
                var weights = part.Weights[i];
                Vector3 position = Vector3.Zero;
                void Add(int index, float weight)
                {
                    if (weight > 0) position += Vector3.Transform(original.Position, palette[index]) * weight;
                }
                Add(weights.Bone0, weights.Weights.X); Add(weights.Bone1, weights.Weights.Y);
                Add(weights.Bone2, weights.Weights.Z); Add(weights.Bone3, weights.Weights.W);
                part.Upload[i] = new(position, Vector3.Zero, original.TexCoord);
            }
            RebuildNormals(part);
            upload(part.Mesh, part.Upload);
        }
        foreach (var display in cloth.Values)
            ClothDisplaySkin.Skin(display.Bind, display.Weights, Pose.ClothInputSkinMatrices, display.Posed);
        poseChanged();
    }

    /** Diagnostic normals follow deformed triangles without changing the source mesh. */
    private static void RebuildNormals(SkinnedPart part)
    {
        var vertices = part.Upload;
        var indices = part.Mesh.Indices;
        for (int i = 0; i < indices.Length; i += 3)
        {
            uint a = indices[i], b = indices[i + 1], c = indices[i + 2];
            var normal = Vector3.Cross(vertices[b].Position - vertices[a].Position,
                vertices[c].Position - vertices[a].Position);
            vertices[a] = vertices[a] with { Normal = vertices[a].Normal + normal };
            vertices[b] = vertices[b] with { Normal = vertices[b].Normal + normal };
            vertices[c] = vertices[c] with { Normal = vertices[c].Normal + normal };
        }
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = vertices[i] with { Normal = vertices[i].Normal.LengthSquared() > 1e-12f
                ? Vector3.Normalize(vertices[i].Normal) : Vector3.UnitZ };
    }

    public IReadOnlyList<Matrix4x4>? SkinFor(MeshInstance instance) =>
        !parts.TryGetValue(instance.Mesh, out var part) ? null
            : part.Attached is { } attached ? Enumerable.Repeat(Pose.ObjectTransforms[attached.Bone], attached.Entries).ToArray()
            : part.RigidEntries > 0 ? Enumerable.Repeat(DisplayPose.SkinMatrices[0], part.RigidEntries).ToArray()
            : part.Palette == SkinPaletteKind.ClothInput ? Pose.ClothInputSkinMatrices : DisplayPose.SkinMatrices;

    /**
     * How the shown pose moves a mesh that follows one bone, in mesh space: an attached part's bone, else the bone
     * carrying more than half its skin weight. Null for a mesh spread over many bones, such as a guardian's body, and
     * for cloth or an unskinned mesh.
     */
    public Matrix4x4? BonePose(MeshData mesh)
    {
        if (!parts.TryGetValue(mesh, out var part) || part.Palette == SkinPaletteKind.ClothInput) return null;
        if (part.Attached is { } attached) return Pose.ObjectTransforms[attached.Bone];
        var weightByBone = new Dictionary<int, float>();
        void Add(int bone, float weight) { if (weight > 0) weightByBone[bone] = weightByBone.GetValueOrDefault(bone) + weight; }
        foreach (var weights in part.Weights)
        {
            Add(weights.Bone0, weights.Weights.X); Add(weights.Bone1, weights.Weights.Y);
            Add(weights.Bone2, weights.Weights.Z); Add(weights.Bone3, weights.Weights.W);
        }
        if (weightByBone.Count == 0) return null;
        var (bone, weight) = weightByBone.MaxBy(pair => pair.Value);
        return weight * 2 > weightByBone.Values.Sum() ? DisplayPose.SkinMatrices[bone] : null;
    }

    /** Bone endpoints use the evaluated skeleton pose and are shown without depth testing. */
    public void DrawBones(Dx11Renderer renderer, Matrix4x4 viewProjection)
    {
        if (!showBones) return;
        var lines = new List<LineVertex>();
        var color = new Vector4(.2f, .95f, .65f, 1);
        foreach (var bone in DisplayPose.Rig.Bones)
        {
            if (bone.ParentIndex < 0) continue;
            lines.Add(new(DisplayPose.ObjectTransforms[bone.ParentIndex].Translation, color));
            lines.Add(new(DisplayPose.ObjectTransforms[bone.Index].Translation, color));
        }
        renderer.DrawLines(lines.ToArray(), viewProjection, false);
    }

    /** Image-space bones remain visible over both diagnostic and native shader previews. */
    public void DrawImGuiBones(ImDrawListPtr draw, Vector2 origin, Vector2 size, Matrix4x4 viewProjection)
    {
        if (!showBones) return;
        uint color = ImGui.GetColorU32(new Vector4(.2f, .95f, .65f, 1));
        foreach (var bone in DisplayPose.Rig.Bones)
        {
            if (bone.ParentIndex < 0) continue;
            var a = Project(DisplayPose.ObjectTransforms[bone.ParentIndex].Translation);
            var b = Project(DisplayPose.ObjectTransforms[bone.Index].Translation);
            if (a is { } first && b is { } second) draw.AddLine(first, second, color, 1.5f);
        }
        Vector2? Project(Vector3 point)
        {
            var p = Vector4.Transform(new Vector4(point, 1), viewProjection);
            if (p.W <= 0 || p.Z < 0 || p.Z > p.W) return null;
            return origin + new Vector2(p.X / p.W * .5f + .5f, .5f - p.Y / p.W * .5f) * size;
        }
    }

    /** The clip's tag on hover; clip labels leave it out. */
    private static void TagTip(uint tag)
    {
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tag.ToString("X8"));
    }

    /** Controls expose linked clips and playback without reproducing the game's animation graph. */
    public void DrawImGui()
    {
        var style = ImGui.GetStyle();
        float available = ImGui.GetContentRegionAvail().X;
        float right = ImGui.GetCursorScreenPos().X + available;
        ImGui.BeginDisabled(IsLoading || Source.Binding is null);
        ImGui.SetNextItemWidth(MathF.Min(160, MathF.Max(1, available)));
        string label = IsLoading ? $"Loading {pendingTag:X8}" : Clip is null ? "Bind pose"
            : Source.ClipNames.GetValueOrDefault(Clip.TagHash, $"{Clip.TagHash:X8}");
        bool clipsOpen = ImGui.BeginCombo("##clip", label, ImGuiComboFlags.HeightLarge);
        if (!clipsOpen && !IsLoading && Clip is not null) TagTip(Clip.TagHash);
        if (clipsOpen)
        {
            if (ImGui.IsWindowAppearing()) { clipSearch = ""; ImGui.SetKeyboardFocusHere(); }
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##clip-search", "Find animation", ref clipSearch, 256);
            if (ImGui.Selectable("Bind pose", Clip is null)) ResetBindPose();
            foreach (uint tag in Source.Clips.Select(x => x.ClipTag).Distinct())
            {
                if (clipSearch.Length != 0 && !Source.ClipNames[tag].Contains(clipSearch, StringComparison.OrdinalIgnoreCase)
                    && !tag.ToString("X8").Contains(clipSearch, StringComparison.OrdinalIgnoreCase)) continue;
                if (ImGui.Selectable(Source.ClipNames[tag] + "##" + tag, Clip?.TagHash == tag)) SelectClip(tag);
                TagTip(tag);
            }
            ImGui.EndCombo();
        }
        if (Clip is { PoseMode: not 0 })
        {
            Next(230);
            ImGui.SetNextItemWidth(200);
            if (ImGui.BeginCombo("Base", BaseClip is null ? "Rig pose" : Source.ClipNames[BaseClip.TagHash]))
            {
                if (ImGui.Selectable("Rig pose", BaseClip is null)) SelectBaseClip(null);
                foreach (uint tag in Source.BaseClipTags)
                {
                    if (ImGui.Selectable(Source.ClipNames[tag] + "##" + tag, BaseClip?.TagHash == tag)) SelectBaseClip(tag);
                    TagTip(tag);
                }
                ImGui.EndCombo();
            }
        }
        Next(ImGui.GetFrameHeight());
        ImGui.BeginDisabled(Clip is null);
        if (ImGuiIcons.Button("playback", playing ? StudioIcon.Pause : StudioIcon.Play, playing ? "Pause" : "Play")) playing = !playing;
        Next(ImGui.GetFrameHeight());
        ImGuiIcons.Toggle("loop", StudioIcon.Loop, "Loop animation", ref loop);
        float speedLabel = ImGui.CalcTextSize("Speed").X + style.ItemInnerSpacing.X;
        float speedWidth = MathF.Min(90, MathF.Max(1, available - speedLabel));
        Next(speedWidth + speedLabel);
        ImGui.SetNextItemWidth(speedWidth);
        ImGui.SliderFloat("Speed", ref rate, .05f, 4, "%.2fx");
        ImGui.EndDisabled();
        ImGui.EndDisabled();
        Next(ImGui.GetFrameHeight());
        ImGuiIcons.Toggle("bones", StudioIcon.Group, "Show bones", ref showBones);
        if (Clip is { } clip)
        {
            float selected = seconds;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderFloat("##clip-time", ref selected, 0, DurationSeconds, "%.3f s")) Seek(selected);
            if (clip.PoseSpace is { } space) DrawPoseInput(space);
        }
        if (Source.Binding?.Controls.Any(control => control.RotationFrame.Kind == 6) == true
            && ImGui.TreeNode("Aim and look"))
        {
            bool changed = ImGui.SliderFloat2("Aim yaw, pitch", ref aimAngles, -MathF.PI / 2, MathF.PI / 2, "%.2f rad");
            changed |= ImGui.SliderFloat2("Look yaw, pitch", ref lookAngles, -MathF.PI / 2, MathF.PI / 2, "%.2f rad");
            if (changed) ApplySample();
            ImGui.TreePop();
        }
        if (Error is not null || Source.BindingError is not null)
        {
            ImGui.TextDisabled("Clip unavailable");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Error ?? Source.BindingError!);
        }
        void Next(float width)
        {
            if (ImGui.GetItemRectMax().X + style.ItemSpacing.X + width <= right) ImGui.SameLine();
        }
    }

    /** Sliders span the authored points; a disk or sphere spans the full turn. */
    private void DrawPoseInput(AnimationPoseSpace space)
    {
        var (minimum, maximum) = space.Bounds;
        if (space.Kind is AnimationPoseSpaceKind.Disk or AnimationPoseSpaceKind.Sphere)
            (minimum.X, maximum.X) = (-MathF.PI, MathF.PI);
        if (space.Kind == AnimationPoseSpaceKind.Sphere) (minimum.Y, maximum.Y) = (-MathF.PI / 2, MathF.PI / 2);
        string[] labels = space.Kind == AnimationPoseSpaceKind.Sphere ? ["Pose yaw", "Pose pitch"] : ["Pose x", "Pose y"];
        bool changed = false;
        ImGui.SetNextItemWidth(-ImGui.CalcTextSize(labels[0]).X - ImGui.GetStyle().ItemInnerSpacing.X);
        if (maximum.X > minimum.X) changed |= ImGui.SliderFloat(labels[0], ref poseInput.X, minimum.X, maximum.X, "%.3f");
        ImGui.SetNextItemWidth(-ImGui.CalcTextSize(labels[1]).X - ImGui.GetStyle().ItemInnerSpacing.X);
        if (space.Kind != AnimationPoseSpaceKind.Disk && maximum.Y > minimum.Y)
            changed |= ImGui.SliderFloat(labels[1], ref poseInput.Y, minimum.Y, maximum.Y, "%.3f");
        if (changed) ApplySample();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        if (loading is { } pending)
            _ = pending.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        lifetime.Dispose();
        parts.Clear();
        cloth.Clear();
    }
}
