using System.Numerics;
using System.Text.Json;
using SunriseStudio.Animation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;

int game = Array.IndexOf(args, "--game");
int ikCases = Array.IndexOf(args, "--ik-cases");
int inspect = Array.IndexOf(args, "--inspect");
int dump = Array.IndexOf(args, "--dump");
int mode = Math.Max(ikCases, Math.Max(inspect, dump));
// Every mode and --game take a value; the modes also need --game.
if ((game >= 0 && game + 1 >= args.Length) || (mode >= 0 && (game < 0 || mode + 1 >= args.Length)))
{
    Console.Error.WriteLine("usage: [--game <dir>] | --game <dir> (--ik-cases <out> | --inspect <out> <tag>... | --dump <out>)");
    Environment.ExitCode = 2;
    return;
}
if (ikCases >= 0)
{
    NativeIkCases.Write(args[game + 1], args[ikCases + 1]);
    return;
}
if (inspect >= 0)
{
    AssetAudit.Run(args[game + 1], args[inspect + 1], args[(inspect + 2)..]);
    return;
}
if (dump >= 0)
{
    using var catalog = PackageCatalog.Open(args[game + 1]);
    DumpPose(catalog, args[dump + 1]);
    return;
}
foreach (bool usePoleAxes in new[] { true, false })
    foreach (bool rootZero in new[] { false, true })
        foreach (float scale in new[] { 1f, 1.7f }) CheckSyntheticLimb(rootZero, scale, usePoleAxes);
CheckClipTiming();
ClipNameChecks.Run();
PoseLayerChecks.Run();
ClothDisplayChecks.Run();
if (game >= 0)
{
    using var catalog = PackageCatalog.Open(args[game + 1]);
    ClipNameChecks.Owned(catalog);
    RetargetChecks.Owned(catalog);
    SkeletonWriterChecks.Owned(catalog);
    foreach (uint tag in new uint[] { 0x80BAB778, 0x80C0FA98, 0x80B82FE3 }) PoseLayerChecks.Owned(catalog, tag);
    foreach (uint tag in new uint[] { 0x80B82FE3, 0x80C0FA98 }) CheckOwnedRig(catalog, tag);
    foreach (uint tag in new uint[] { 0x80FB960D, 0x80C4A807 }) CheckPreviewClips(catalog, tag);
}

/** Authored layers preview over rig defaults without changing the strict absolute-clip API. */
static void CheckPreviewClips(PackageCatalog catalog, uint tag)
{
    var rig = new SkeletonReader(catalog).Read(tag);
    var binding = new AnimationRigReader(catalog).Read(rig);
    var pose = rig.CreateBindPose();
    int clips = 0, layers = 0;
    foreach (uint clipTag in new EntityAnimationCatalog(catalog).Read(tag).Select(value => value.ClipTag).Distinct())
    {
        var clip = new AnimationControlReader(catalog).Read(clipTag);
        foreach (float phase in new[] { 0f, .5f, 1f })
            binding.ApplyPreview(clip, clip.DurationSeconds * phase, pose);
        var sampled = AnimationInterchange.Sample(rig, binding, clip, previewLayers: true);
        Require(sampled.Frames.Count == clip.FrameCount, "Preview export changed native clip length.");
        if (clip.PoseMode != 0) layers++;
        clips++;
    }
    Require(clips > 0 && (tag != 0x80C4A807 || layers == 14), "Expected authored clip layers are missing.");
    Console.WriteLine($"animation_preview entity={tag:X8} clips={clips} layers={layers} unavailable=0");
}

/** Native-rate exports keep one sample per authored frame and one terminal key. */
static void CheckClipTiming()
{
    var bone = new SkeletonBone(0, 0, "root", -1, -1, -1,
        BoneTransform.Identity, BoneTransform.Identity, BoneTransform.Identity);
    var rig = new SkeletonRig(0, 0, [], [bone], [0], 0, []);
    var frame = new AnimationFrameReference(-1, -1, 0, null, 0, -1, 0);
    var control = new AnimationRigControl(0, 0, 0, -1, 0, 255, frame, frame, BoneTransform.Identity);
    var groups = new[] { new AnimationControlGroup(1, 1) };
    var binding = new AnimationRigBinding(rig, 0, [control], [1], groups, [], true, []);
    for (int count = 1; count <= 200; count++)
    {
        var clip = new AnimationControlClip(1, count, 1, 1, 0, [], [], groups);
        foreach (int rate in new[] { 24, 30, 60 })
        {
            var sampled = AnimationInterchange.Sample(rig, binding, clip, rate);
            Require(rate != 30 || sampled.Frames.Count == count, "Native frame count changed during export.");
            Require(sampled.Frames[^1].Time == clip.DurationSeconds, "Terminal sample differs from clip duration.");
            for (int i = 1; i < sampled.Frames.Count; i++)
                Require(sampled.Frames[i].Time > sampled.Frames[i - 1].Time, "Export contains a repeated time key.");
        }
    }
    Console.WriteLine("animation_timing lengths=200 rates=24,30,60 repeated_keys=0");
}

/** Writes one sampled pose as JSON through public APIs only, so two builds can be compared. */
static void DumpPose(PackageCatalog catalog, string path)
{
    var rig = new SkeletonReader(catalog).Read(0x80B82FE3);
    var binding = new AnimationRigReader(catalog).Read(rig);
    var clip = new AnimationControlReader(catalog).Read(0x80BFBEB8);
    var pose = rig.CreateBindPose();
    binding.Apply(clip, clip.DurationSeconds * .5f, pose);
    var skins = rig.ModelTags.SelectMany(model => new ModelSkinReader(catalog).Read(model, rig));
    float[] V(Vector3 value) => [value.X, value.Y, value.Z];
    float[] Q(Quaternion value) => [value.X, value.Y, value.Z, value.W];
    var report = new
    {
        entity = rig.EntityTag, clip = clip.TagHash, seconds = clip.DurationSeconds * .5f,
        bones = rig.Bones.Select(bone => new
        {
            index = bone.Index, name = bone.Name, parent = bone.ParentIndex,
            position = V(pose.ObjectTransforms[bone.Index].Translation),
            rotation = Q(BoneTransform.FromMatrix(pose.ObjectTransforms[bone.Index]).Rotation),
            scale = BoneTransform.FromMatrix(pose.ObjectTransforms[bone.Index]).Scale,
            bindScale = bone.BindObject.Scale
        }),
        controls = binding.Controls.Select(control => new { control.Index, control.BoneIndex,
            control.ChainMode, control.CompositionMode, control.PoleAxis, defaultScale = control.DefaultValue.Scale }),
        weightCounts = skins.Select(skin => new { skin.ModelTag, skin.PositionStride,
            vertices = skin.Vertices.Count, bones = skin.Vertices.SelectMany(vertex => new[]
            {
                (vertex.Bone0, vertex.Weights.X), (vertex.Bone1, vertex.Weights.Y),
                (vertex.Bone2, vertex.Weights.Z), (vertex.Bone3, vertex.Weights.W)
            }).Where(value => value.Item2 > 0).GroupBy(value => value.Item1)
                .ToDictionary(group => group.Key, group => group.Count()) })
    };
    File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"animation_pose entity={rig.EntityTag:X8} clip={clip.TagHash:X8} seconds={clip.DurationSeconds * .5f:G6}");
}

/** Child offsets must survive a limb solve under a rotated and translated root. */
static void CheckSyntheticLimb(bool rootZero, float scale, bool usePoleAxes)
{
    int[] parents = [-1, 0, 1, 2, 1, 2, 3, 6];
    Vector3[] positions = [Vector3.Zero, new(2, 1, 3), new(3, 1, 3), new(4, 1, 3),
        new(2, 1.3f, 3), new(3, 1.4f, 3), new(4.2f, 1.1f, 3), new(4.4f, 1.2f, 3)];
    if (rootZero)
    {
        parents = parents.Skip(1).Select(parent => parent - 1).ToArray();
        positions = positions.Skip(1).ToArray();
    }
    int root = rootZero ? 0 : 1, end = root + 2;
    var binds = positions.Select(p => new BoneTransform(Quaternion.Identity, p, 1)).ToArray();
    var bones = binds.Select((bind, i) => new SkeletonBone(i, (uint)i, $"bone_{i}", parents[i],
        Array.FindIndex(parents, p => p == i),
        parents[i] < 0 ? -1 : Array.FindIndex(parents, i + 1, p => p == parents[i]),
        bind, AnimationIkSolver.Inverse(bind), parents[i] < 0 ? bind
            : AnimationIkSolver.Compose(AnimationIkSolver.Inverse(binds[parents[i]]), bind))).ToArray();
    var rig = new SkeletonRig(0, 0, [], bones, Enumerable.Range(0, bones.Length).ToArray(), 0, []);
    var move = new BoneTransform(Quaternion.CreateFromYawPitchRoll(.45f, -.3f, .7f), new(1, -2, .5f), scale);
    var before = binds.Select(bind => AnimationIkSolver.Compose(move, bind)).ToArray();
    before[end] = before[end] with { Translation = before[root].Translation + new Vector3(.7f, .8f, -.3f) };
    var frame = new AnimationFrameReference(-1, -1, 0, null, 0, -1, 0);
    var control = new AnimationRigControl(0, 0, (short)end, -1, 0, 0, frame, frame, BoneTransform.Identity)
        { PoleAxis = 3 };
    var after = before.ToArray();
    AnimationIkSolver.Apply(rig, [control], [0], usePoleAxes, after);
    float maximum = AttachmentError(rig, before, after, new HashSet<int> { root, root + 1, end });
    Require(maximum < .00001f, $"IK changed child offsets by {maximum:G6}.");
    Require(Vector3.Distance(after[end].Translation, before[end].Translation) < .00001f,
        "Reachable IK target moved.");
    if (rootZero) Require(MatrixError(before[root].Matrix, after[root].Matrix) == 0, "IK changed root motion.");
    float upper = Vector3.Distance(after[root].Translation, after[root + 1].Translation);
    float lower = Vector3.Distance(after[root + 1].Translation, after[end].Translation);
    Require(MathF.Abs(upper - scale) < .0001f && MathF.Abs(lower - scale) < .0001f, "IK changed segment lengths.");
    Console.WriteLine($"animation_ik synthetic=pass solver={(usePoleAxes ? "pole" : "plane")} root={root} scale={scale} attachment_error={maximum:G6}");
}

/** Authored helpers retain their joint-relative pose before correction rows run. */
static void CheckOwnedRig(PackageCatalog catalog, uint tag)
{
    var rig = new SkeletonReader(catalog).Read(tag);
    var binding = new AnimationRigReader(catalog).Read(rig);
    uint[] clips = new EntityAnimationCatalog(catalog).Read(tag).Select(row => row.ClipTag).Distinct().ToArray();
    var joints = new HashSet<int>();
    foreach (int index in binding.IkControlIndices)
    {
        int end = binding.Controls[index].BoneIndex;
        int middle = rig.Bones[end].ParentIndex;
        joints.Add(end); joints.Add(middle); joints.Add(rig.Bones[middle].ParentIndex);
    }
    float maximum = 0, controlScaleError = 0;
    int samples = 0, skipped = 0;
    foreach (uint clipTag in clips)
    {
        AnimationControlClip clip;
        try { clip = new AnimationControlReader(catalog).Read(clipTag); }
        catch (NotSupportedException) { skipped++; continue; }
        if (clip.PoseMode != 0) { skipped++; continue; }
        int count = binding.CompatibleControlCount(clip);
        if (count == 0) { skipped++; continue; }
        foreach (float phase in new float[] { 0, .25f, .5f, .75f, 1 })
        {
            var values = binding.Controls.Select(control => control.DefaultValue).ToArray();
            foreach (var sample in clip.Sample(clip.DurationSeconds * phase))
            {
                if (sample.ControlIndex >= count) continue;
                var value = values[sample.ControlIndex];
                values[sample.ControlIndex] = new(sample.HasRotation ? sample.Rotation : value.Rotation,
                    sample.HasTranslation ? sample.Translation : value.Translation,
                    sample.HasScale ? sample.Scale : value.Scale);
            }
            var before = binding.Evaluate(values).ToArray();
            controlScaleError = Math.Max(controlScaleError, values.Max(value => MathF.Abs(value.Scale - 1)));
            foreach (bool usePoleAxes in new[] { true, false })
            {
                var after = before.ToArray();
                AnimationIkSolver.Apply(rig, binding.Controls, binding.IkControlIndices, usePoleAxes, after);
                maximum = Math.Max(maximum, AttachmentError(rig, before, after, joints));
            }
            samples++;
        }
    }
    Require(samples > 0, "Owned rig has no sampled clips.");
    Require(maximum < .0001f, $"Rig {tag:X8} IK changed child offsets by {maximum:G6}.");
    Console.WriteLine($"animation_ik entity={tag:X8} bones={rig.Bones.Count} limbs={binding.IkControlIndices.Count} corrections={binding.BoneCorrectionCount} samples={samples} skipped={skipped} attachment_error={maximum:G6} control_scale_error={controlScaleError:G6}");
}

/** Only side branches belong to a joint; solved joints have their own target. */
static float AttachmentError(SkeletonRig rig, BoneTransform[] before, BoneTransform[] after,
    IReadOnlySet<int> joints)
{
    float maximum = 0;
    foreach (var bone in rig.Bones)
    {
        if (bone.ParentIndex < 0 || joints.Contains(bone.Index)) continue;
        int ancestor = bone.ParentIndex;
        while (ancestor >= 0 && !joints.Contains(ancestor)) ancestor = rig.Bones[ancestor].ParentIndex;
        if (ancestor < 0) continue;
        var expected = Relative(before[bone.Index], before[bone.ParentIndex]);
        var actual = Relative(after[bone.Index], after[bone.ParentIndex]);
        maximum = Math.Max(maximum, MatrixError(expected, actual));
    }
    return maximum;
}

static Matrix4x4 Relative(BoneTransform child, BoneTransform parent)
{
    Require(Matrix4x4.Invert(parent.Matrix, out var inverse), "Parent transform is singular.");
    return child.Matrix * inverse;
}

static float MatrixError(Matrix4x4 first, Matrix4x4 second)
{
    var delta = first - second;
    float[] values = [delta.M11, delta.M12, delta.M13, delta.M14, delta.M21, delta.M22, delta.M23,
        delta.M24, delta.M31, delta.M32, delta.M33, delta.M34, delta.M41, delta.M42, delta.M43, delta.M44];
    return values.Max(MathF.Abs);
}

static void Require(bool value, string message)
{
    if (!value) throw new InvalidDataException(message);
}
