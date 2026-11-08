using SunriseStudio.Tags.Generated;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using SunriseStudio.Animation;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

/** Animation is null when the weapon's rigs cannot be bound; the scene diagnostics say why. */
public sealed record PreparedWeaponAnimationPreview(SceneDocument Scene, AssetAnimationSource? Animation,
    IReadOnlySet<uint> SourceTags);

/** The weapon uses its own inverse binds with the copied first-person object frames. */
public sealed class WeaponAnimationBinding
{
    public SkeletonRig WeaponRig { get; }
    public SkeletonRig PresentationRig { get; }
    /** The weapon's bone count; its frames follow the player partition in the presentation rig. */
    public int BoneCount => WeaponRig.Bones.Count;
    /** First presentation bone of the weapon partition: the player partition's bone count. */
    public int FirstBone { get; }

    /**
     * 0x7FF742A190F0 binds the player skeleton first and the weapon skeleton after it, so weapon bone i
     * is presentation bone FirstBone + i with the same name, parent and local bind.
     */
    public WeaponAnimationBinding(SkeletonRig weapon, SkeletonRig presentation)
    {
        FirstBone = presentation.Bones.Count - weapon.Bones.Count;
        if (weapon.Bones.Count == 0 || FirstBone < 1)
            throw new NotSupportedException("The first-person rig has no partition for the weapon's bones.");
        for (int index = 0; index < weapon.Bones.Count; index++)
        {
            var local = weapon.Bones[index];
            var parent = presentation.Bones[FirstBone + index];
            if (parent.NameHash != local.NameHash
                || index > 0 && parent.ParentIndex != FirstBone + local.ParentIndex
                || index == 0 && (local.ParentIndex != -1 || parent.ParentIndex is < 0 || parent.ParentIndex >= FirstBone)
                || Difference(local.BindLocal.Matrix, parent.BindLocal.Matrix) > .00002f)
                throw new NotSupportedException("The weapon frame partition does not match its first-person rig.");
        }
        WeaponRig = weapon; PresentationRig = presentation;
    }

    /** Object frames cross the native partition before the weapon's inverse binds are applied. */
    public void Apply(SkeletonPose presentation, SkeletonPose weapon)
    {
        if (!ReferenceEquals(presentation.Rig, PresentationRig) || !ReferenceEquals(weapon.Rig, WeaponRig))
            throw new InvalidDataException("The weapon preview pose belongs to another rig.");
        var transforms = Enumerable.Range(0, BoneCount)
            .Select(index => BoneTransform.FromMatrix(presentation.ObjectTransforms[FirstBone + index])).ToArray();
        weapon.SetObjectTransforms(transforms);
    }

    /** A first-person skin-matrix suffix has the wrong inverse binds for weapon geometry. */
    public static IReadOnlyList<Matrix4x4> Compose(IReadOnlyList<Matrix4x4> weaponInverseBinds,
        IReadOnlyList<Matrix4x4> presentationObjectFrames, int firstBone)
    {
        if (firstBone < 0 || firstBone > presentationObjectFrames.Count - weaponInverseBinds.Count)
            throw new InvalidDataException("The weapon frame partition leaves the presentation pose.");
        return weaponInverseBinds.Select((inverse, index) => inverse * presentationObjectFrames[firstBone + index]).ToArray();
    }

    private static float Difference(Matrix4x4 left, Matrix4x4 right)
    {
        float[] a = [left.M11, left.M12, left.M13, left.M14, left.M21, left.M22, left.M23, left.M24,
            left.M31, left.M32, left.M33, left.M34, left.M41, left.M42, left.M43, left.M44];
        float[] b = [right.M11, right.M12, right.M13, right.M14, right.M21, right.M22, right.M23, right.M24,
            right.M31, right.M32, right.M33, right.M34, right.M41, right.M42, right.M43, right.M44];
        return a.Zip(b, (x, y) => MathF.Abs(x - y)).Max();
    }
}

/** This explicit path keeps external weapon frames separate from ordinary entity skin ownership. */
public static partial class WeaponAnimationPreviewSource
{
    /**
     * Parts in stages the viewer never draws are left out. A weapon whose rigs fail the frame partition
     * check opens at rest, with the reason as a scene diagnostic.
     * @param presentationEntity The animation owner; 0 when it could not be read.
     * @param presentationError Why the owner could not be read; shown when presentationEntity is 0.
     */
    public static PreparedWeaponAnimationPreview Prepare(PackageCatalog catalog, uint weaponEntity,
        uint presentationEntity, IReadOnlyList<uint> artEntities, CancellationToken token = default,
        Func<uint, string?>? boneNames = null, Func<uint, string?>? clipNames = null, string? presentationError = null)
    {
        if (artEntities.Count is 0 or > 32) throw new InvalidDataException("Choose the weapon's art parts.");
        var scene = new SceneDocument { TagHash = weaponEntity, Name = "Weapon animation preview" };
        // The preview draws the first-person presentation as the first-person player view, camera mode 0.
        NativeStencil.SetFirstPersonView(scene);
        var sources = new HashSet<uint> { weaponEntity };
        int undrawn = 0;
        foreach (uint entity in artEntities.Distinct())
        {
            token.ThrowIfCancellationRequested();
            if (!catalog.TryGetEntry(entity, out var entry) || entry.Reference != SEntity.Class)
                throw new InvalidDataException("A weapon art part is not an entity.");
            var part = new AssetSceneLoader(catalog).Load(entity, token);
            var visible = part.Instances.Where(instance => instance.InitiallyVisible != false
                && !instance.Mesh.NeverDrawn && instance.Mesh.LodLevel == 0).ToArray();
            var drawn = visible.Where(instance => instance.Mesh.Native is not { } native
                || NativeScenePlan.DrawnStages.Contains(native.RenderStage)).ToArray();
            undrawn += visible.Length - drawn.Length;
            scene.Instances.AddRange(drawn);
            foreach (string line in part.Diagnostics)
                if (!scene.Diagnostics.Contains(line)) scene.Diagnostics.Add(line);
            sources.UnionWith(part.ReadTags); sources.Add(entity);
        }
        if (scene.Instances.Count == 0) throw new InvalidDataException("The weapon's art parts have no drawn geometry.");
        if (undrawn != 0)
            scene.Diagnostics.Add($"Left out: {undrawn} part{(undrawn == 1 ? "" : "s")} in undrawn stages, such as the reticle");
        AssetAnimationSource? animation = null;
        try
        {
            if (presentationEntity == 0)
                throw new NotSupportedException(presentationError ?? "The weapon has no presentation owner.");
            animation = Animate(catalog, weaponEntity, presentationEntity, scene, sources, token, boneNames, clipNames);
        }
        catch (Exception error) when (error is NotSupportedException or InvalidDataException or KeyNotFoundException)
        { scene.Diagnostics.Add($"Weapon at rest: {error.Message}"); }
        return new(scene, animation, sources);
    }

    /** Binds the assembled parts to the weapon's frames inside the first-person rig. */
    private static AssetAnimationSource Animate(PackageCatalog catalog, uint weaponEntity, uint presentationEntity,
        SceneDocument scene, HashSet<uint> sources, CancellationToken token, Func<uint, string?>? boneNames,
        Func<uint, string?>? clipNames)
    {
        var weapon = new SkeletonReader(catalog) { NameResolver = boneNames }.Read(weaponEntity);
        var presentation = AssetAnimationSource.Prepare(catalog, presentationEntity, token, boneNames, clipNames);
        var binding = new WeaponAnimationBinding(weapon, presentation.Rig);
        // Every weapon frame needs a clip control on its presentation bone; the control index space differs.
        if (presentation.Binding is not { } controls || Enumerable.Range(0, binding.BoneCount).Any(index =>
                !controls.Controls.Any(control => control.BoneIndex == binding.FirstBone + index)))
            throw new NotSupportedException("The first-person weapon controls do not match the frame partition.");
        sources.UnionWith([presentationEntity, weapon.ResourceTag, presentation.Rig.ResourceTag, controls.ResourceTag]);
        var (skins, rigidSkins, rigid) = BindSkins(catalog, scene, binding.BoneCount);
        if (rigid.Count != 0)
            scene.Diagnostics.Add($"On bone 0: {rigid.Count} parts, skinning unverified ("
                + string.Join(", ", rigid.Order()) + ")");
        return AssetAnimationSource.ForWeapon(presentation, skins, binding, rigidSkins);
    }

    /**
     * Binds each part to the weapon bone its position word 3 names, when its vertex shader is checked.
     * @return The bindings; the rigid ones with the palette entries they need; the rigid parts by name.
     */
    internal static (List<ModelSkinBinding> Skins, Dictionary<ModelSkinBinding, int> RigidSkins, List<string> Rigid)
        BindSkins(PackageCatalog catalog, SceneDocument scene, int boneCount)
    {
        var reader = new GameMaterialReader(catalog);
        var checkedShaders = new Dictionary<uint, bool>();
        var skins = new List<ModelSkinBinding>();
        var rigidSkins = new Dictionary<ModelSkinBinding, int>(ReferenceEqualityComparer.Instance);
        var rigidNames = new List<string>();
        var meshes = scene.Instances.Select(instance => instance.Mesh).Distinct()
            .Where(mesh => mesh.Native is { VertexStreams.Length: > 0 } && mesh.SourceVertexIndices.Length == mesh.Vertices.Length)
            .Select(mesh => (Mesh: mesh, Indices: PaletteIndices(reader, checkedShaders, mesh, boneCount)));
        foreach (var group in meshes.GroupBy(item => (item.Mesh.TagHash, item.Mesh.Native!.VertexStreams[0].HeaderTag,
            Rigid: item.Indices is null)))
        {
            var stream = group.First().Mesh.Native!.VertexStreams[0];
            var weights = new VertexSkinWeights[stream.Data.Length / stream.Stride];
            int entries = boneCount;
            foreach (var (mesh, indices) in group)
                for (int vertex = 0; vertex < mesh.SourceVertexIndices.Length; vertex++)
                {
                    uint source = mesh.SourceVertexIndices[vertex];
                    if (source >= weights.Length) throw new InvalidDataException("A weapon vertex leaves its position stream.");
                    weights[source] = new(indices?[vertex] ?? 0, 0, 0, 0, new Vector4(1, 0, 0, 0));
                    if (indices is null && stream.Stride == SkinPositionStream.PaletteStride)
                        entries = Math.Max(entries, ReadIndex(stream, source) + 1);
                }
            var skin = new ModelSkinBinding(group.Key.TagHash, skins.Count, stream.HeaderTag, stream.Stride,
                group.Select(item => item.Mesh.PartIndex).Distinct().ToArray(), weights);
            skins.Add(skin);
            if (!group.Key.Rigid) continue;
            rigidSkins.Add(skin, entries);
            rigidNames.AddRange(group.Select(item => $"0x{item.Mesh.TagHash:X8} part {item.Mesh.PartIndex}"));
        }
        var still = scene.Instances.Select(instance => instance.Mesh).Distinct()
            .Where(mesh => !skins.Any(skin => skin.ModelTag == mesh.TagHash && skin.PartOffsets.Contains(mesh.PartIndex)))
            .Select(mesh => $"0x{mesh.TagHash:X8} part {mesh.PartIndex}").ToArray();
        if (still.Length != 0)
            scene.Diagnostics.Add($"Not skinned: {still.Length} parts (" + string.Join(", ", still) + ")");
        return (skins, rigidSkins, rigidNames.Distinct().ToList());
    }

    /** Palette index per compact vertex, or null when the profile or an index is unverified. */
    private static int[]? PaletteIndices(GameMaterialReader reader, Dictionary<uint, bool> checkedShaders, MeshData mesh,
        int boneCount)
    {
        var native = mesh.Native!;
        if (!native.InputLayoutVerified || native.VertexStreams[0] is not { Stride: SkinPositionStream.PaletteStride } stream
            || stream.Data.Length % SkinPositionStream.PaletteStride != 0
            || !native.InputElements.Any(e => e is { Semantic: "POSITION", SemanticIndex: 0, Slot: 0, Offset: 0,
                Format: NativeVertexFormat.R16G16B16A16SNorm, PerInstance: false }))
            return null;
        if (!checkedShaders.TryGetValue(native.VertexShaderTag, out bool verified))
            checkedShaders.Add(native.VertexShaderTag, verified = IndexesPaletteByPositionW(reader, native));
        if (!verified) return null;
        var indices = new int[mesh.SourceVertexIndices.Length];
        for (int vertex = 0; vertex < indices.Length; vertex++)
        {
            uint source = mesh.SourceVertexIndices[vertex];
            if (source >= stream.Data.Length / SkinPositionStream.PaletteStride) return null;
            indices[vertex] = ReadIndex(stream, source);
            if (indices[vertex] < 0 || indices[vertex] >= boneCount) return null;
        }
        return indices;
    }

    private static int ReadIndex(NativeVertexStream stream, uint vertex) => SkinPositionStream.PaletteIndex(stream.Data, vertex);

    /**
     * True when the VS reads a CB11 3x4 matrix palette by ftou(POSITION.w * 32767 + 0.1), its only
     * float-to-integer conversion, with float inputs only and no resources.
     */
    private static bool IndexesPaletteByPositionW(GameMaterialReader reader, NativeMeshData native)
    {
        if (native.VertexShaderTag == 0) return false;
        var position = native.ShaderInputs.FirstOrDefault(input => input is { Semantic: "POSITION", SemanticIndex: 0 });
        if (position is null || native.ShaderInputs.Any(input => input.SystemValue != 0 || input.ComponentType != 3))
            return false;
        GameShaderInfo shader;
        try { shader = reader.ReadShader(native.VertexShaderTag, GameShaderStage.Vertex); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException)
        { return false; }
        if (NativeShaderContracts.ClassifyEntityPalette(reader.ReadShaderBytecode(shader), shader.Disassembly)
            != NativeEntityPalette.Matrix || shader.Declarations.Any(line => line.StartsWith("dcl_resource", StringComparison.Ordinal)))
            return false;
        var lines = shader.Disassembly.Split('\n').Select(line => line.Trim()).ToArray();
        var conversions = lines.Where(line => line.StartsWith("ftou ", StringComparison.Ordinal)
            || line.StartsWith("ftoi ", StringComparison.Ordinal)).ToArray();
        if (conversions.Length != 1) return false;
        var scale = lines.Select(line => PositionIndexScale().Match(line))
            .FirstOrDefault(match => match.Success && match.Groups["input"].Value == position.Register.ToString(CultureInfo.InvariantCulture));
        return scale is not null
            && conversions[0] == $"ftou {Destination().Match(conversions[0]).Groups[1].Value}, {scale.Groups["register"].Value}";
    }

    [GeneratedRegex(@"^mad (?<register>r\d+\.[xyzw]), v(?<input>\d+)\.w, l\(32767\.000000\), l\(0\.100000\)$")]
    private static partial Regex PositionIndexScale();

    [GeneratedRegex(@"^ftou (r\d+\.[xyzw]),")]
    private static partial Regex Destination();
}

public sealed partial class AssetAnimationSource
{
    public WeaponAnimationBinding? WeaponBinding { get; }
    /** Bindings drawn with every palette entry set to weapon bone 0, with the entry count they need. */
    public IReadOnlyDictionary<ModelSkinBinding, int> RigidSkins { get; } =
        new Dictionary<ModelSkinBinding, int>(ReferenceEqualityComparer.Instance);

    private AssetAnimationSource(AssetAnimationSource source, IReadOnlyList<ModelSkinBinding> skins,
        WeaponAnimationBinding binding, IReadOnlyDictionary<ModelSkinBinding, int> rigidSkins)
    {
        catalog = source.catalog; Rig = source.Rig; SkinBindings = skins; Binding = source.Binding;
        Clips = source.Clips; ClipNames = source.ClipNames; BaseClipTags = source.BaseClipTags;
        Diagnostics = source.Diagnostics; BindingError = source.BindingError; WeaponBinding = binding;
        RigidSkins = rigidSkins;
    }

    internal static AssetAnimationSource ForWeapon(AssetAnimationSource source, IReadOnlyList<ModelSkinBinding> skins,
        WeaponAnimationBinding binding, IReadOnlyDictionary<ModelSkinBinding, int> rigidSkins) =>
        new(source, skins, binding, rigidSkins);
}
