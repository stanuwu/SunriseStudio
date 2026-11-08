using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SunriseStudio.Animation;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Interchange;

public static partial class AssetBundle
{
    public const string Format = "sunrise-asset-bundle";
    public const string ManifestName = "asset.sunrise-bundle.json";
    public const string EditsName = "asset.edited.sunrise-bundle.json";

    /** Working assets contain package data and must remain outside projects and the installation. */
    public static AssetBundleExportResult Export(PackageCatalog catalog, uint sourceTag, string outputFolder,
        AssetBundleExportOptions? options = null, CancellationToken token = default, Action<string>? progress = null)
    {
        options ??= new();
        return Staged(catalog, outputFolder, options, token, staging => Write(catalog, sourceTag, staging, options, token, progress));
    }

    /**
     * Checks the folder rules, then lets write fill a sibling staging folder that is moved into place.
     * @param write Fills the staging folder and returns its manifest.
     * @return The manifest path in the final folder and the manifest.
     */
    private static AssetBundleExportResult Staged(PackageCatalog catalog, string outputFolder,
        AssetBundleExportOptions options, CancellationToken token, Func<string, AssetBundleManifest> write)
    {
        string root = Path.GetFullPath(outputFolder);
        BundlePaths.RejectLinks(root);
        for (string? parent = root; parent is not null; parent = Path.GetDirectoryName(parent))
            if (File.Exists(Path.Combine(parent, StudioProject.ProjectFileName)))
                throw new IOException("Export the working bundle outside Sunrise projects.");
        if (BundlePaths.Within(root, catalog.GameRoot) || options.Project is not null && BundlePaths.Within(root, options.Project.DirectoryPath))
            throw new IOException("Export the working bundle outside the installation and project.");
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException("Asset bundle export needs an empty folder.");
        if (options.SampleRate is < 1 or > 120 || options.MaximumAssets is < 1 or > 8192)
            throw new ArgumentOutOfRangeException(nameof(options));
        // Write into a sibling folder and move it into place, so a failed export leaves nothing.
        string container = Path.GetDirectoryName(root) ?? throw new IOException("Asset bundle export needs a named folder.");
        Directory.CreateDirectory(container);
        string staging = Path.Combine(container, ".sunrise-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var manifest = write(staging);
            token.ThrowIfCancellationRequested();
            BundlePaths.RejectLinks(root);
            if (Directory.Exists(root)) Directory.Delete(root);
            Directory.Move(staging, root);
            return new(Path.Combine(root, ManifestName), manifest);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                BundlePaths.RejectLinks(staging);
                Directory.Delete(staging, true);
            }
        }
    }

    /** Writes the whole bundle into root and returns its manifest. */
    private static AssetBundleManifest Write(PackageCatalog catalog, uint sourceTag, string root,
        AssetBundleExportOptions options, CancellationToken token, Action<string>? progress)
    {
        var notices = new List<BundleNotice>();
        var connections = new AssetConnectionReader(catalog).Read(sourceTag, options.Context, token: token);
        uint rootTag = connections.PreviewTag;
        string name = options.NameResolver?.Invoke(rootTag) ?? catalog.GetName(rootTag) ?? $"Asset {rootTag:X8}";
        var loader = new AssetSceneLoader(catalog) { IncludeLowerLods = true };
        SceneDocument? scene = null;
        try { scene = loader.Load(rootTag, options.Scene ?? new SceneLoadOptions(), token); }
        catch (Exception error) when (Recoverable(error)) { notices.Add(new("Geometry", "Preserved", error.Message, rootTag)); }
        if (scene is not null && PreviewInputs(scene) is { } inputs) notices.Add(new("Entity inputs", "Studio choice", inputs, rootTag));
        if (scene is not null)
            notices.AddRange(scene.MaterialWarnings.Select(warning => new BundleNotice("Materials", "Initial state fallback",
                $"Entity 0x{warning.EntityTag:X8}"
                + (warning.SourceTag == 0 ? "" : $", placement 0x{warning.SourceTag:X8}:0x{warning.SourceOffset:X}")
                + $": {warning.Message}", warning.ConfigTag)));
        // Placed scene copies come first so the first copy kept carries the placed transform.
        var effects = scene?.Markers.Select(marker => marker.Effect).OfType<EffectDescription>().ToList() ?? [];
        effects.AddRange(new EffectReader(catalog).ReadEntity(rootTag));
        effects = effects.DistinctBy(effect => (effect.SourceTag, effect.SourceOffset)).ToList();
        var emitters = effects.SelectMany(effect => effect.Systems).Select(system => system.EmitterTag).Distinct().ToArray();
        if (catalog.TryGetEntry(rootTag, out var selected) && selected.Reference == EffectReader.SystemClass)
        {
            var system = new EffectReader(catalog).ReadSystem(rootTag);
            if (system is not null) { effects.Add(new() { SourceTag = rootTag, Systems = [system] }); emitters = [system.EmitterTag]; }
        }
        if (selected?.Reference == EffectReader.EmitterClass) emitters = [rootTag];
        var particleValues = new List<BundleEmitter>();
        foreach (uint emitter in emitters)
            try { particleValues.Add(BundleParticlesCodec.Describe(catalog, emitter)); }
            catch (Exception error) when (Recoverable(error)) { notices.Add(new("Particles", "Preserved", error.Message, emitter)); }
        string? particlePath = null;
        if (effects.Count != 0 || particleValues.Count != 0)
        {
            particlePath = "particles/emitters.json";
            WriteParticles(BundlePaths.Resolve(root, particlePath), new BundleParticles(particleValues, effects));
            notices.Add(new("Particles", "Editable constants", "Blender preview is approximate; native programs kept."));
        }

        SkeletonRig? rig = null; AnimationRigBinding? binding = null;
        var skins = new List<ModelSkinBinding>();
        var animations = new List<BundleAnimation>();
        var sampled = new List<(uint Tag, string Name, BundleSceneWriter.SampledClip Clip)>();
        if (connections.AnimationEntityTag != 0)
        {
            try
            {
                rig = new SkeletonReader(catalog).Read(connections.AnimationEntityTag);
                foreach (uint model in rig.ModelTags)
                    try { skins.AddRange(new ModelSkinReader(catalog).Read(model, rig)); }
                    catch (Exception error) when (Recoverable(error)) { notices.Add(new("Skin", "Preserved", error.Message, model)); }
                foreach (var cloth in rig.ClothModels)
                    try { skins.AddRange(new ModelSkinReader(catalog).ReadClothInput(cloth, rig)); }
                    catch (Exception error) when (Recoverable(error)) { notices.Add(new("Skin", "Preserved", error.Message, cloth.ModelTag)); }
                if (skins.Any(skin => skin.PaletteKind == SkinPaletteKind.ClothInput))
                    notices.Add(new("Cloth", "Skeletal input", "Skinned to its skeletal input; not simulated."));
                binding = new AnimationRigReader(catalog).Read(rig);
            }
            catch (Exception error) when (Recoverable(error)) { notices.Add(new("Animation rig", "Preserved", error.Message, rootTag)); }
        }
        var linkedClips = connections.AnimationEntityTag == 0 ? [] : new EntityAnimationCatalog(catalog)
            .Read(connections.AnimationEntityTag, resolveNames: true, cancellationToken: token)
            .GroupBy(clip => clip.ClipTag).Select(group => group.First() with
            { Name = EntityAnimationCatalog.UniqueName(group) }).ToArray();
        if (selected?.Reference == SAnimationClip.Class && linkedClips.Length == 0)
            linkedClips = [new(0, 0, 0, rootTag)];
        foreach (var reference in linkedClips)
        {
            token.ThrowIfCancellationRequested();
            string? posePath = null, controlsPath = null, controlsHash = null, failure = null;
            string clipName = AssetSceneLoader.ClipName(catalog, reference.ClipTag, reference.Index,
                options.NameResolver, reference.Name);
            if (options.IncludeAnimations)
            {
                progress?.Invoke($"Exporting animation {reference.ClipTag:X8}");
                if (rig is not null && binding is not null)
                    try { posePath = SamplePose(catalog, rig, binding, reference.ClipTag, clipName, root, options, notices, sampled); }
                    catch (Exception error) when (Recoverable(error)) { failure = error.Message; }
                try
                {
                    controlsPath = $"animations/{reference.ClipTag:X8}.controls.gltf";
                    Directory.CreateDirectory(Path.Combine(root, "animations"));
                    AnimationAssetImporter.ExportControlsGltf(catalog, reference.ClipTag, BundlePaths.Resolve(root, controlsPath), clipName);
                    controlsHash = BundlePaths.FileHash(BundlePaths.Resolve(root, controlsPath));
                }
                catch (Exception error) when (Recoverable(error)) { controlsPath = null; failure = failure is null ? error.Message : failure + " " + error.Message; }
            }
            else failure = "Animation conversion was disabled; native clip bytes are preserved.";
            animations.Add(new(reference.ClipTag, clipName, posePath, controlsPath, controlsHash, failure));
            if (failure is not null) notices.Add(new("Animation", "Partial", failure, reference.ClipTag));
        }

        var meshData = new List<(BundleMesh Info, BundleGeometry Geometry)>();
        var meshMaterialReader = new GameMaterialReader(catalog);
        foreach (var instance in scene?.Instances ?? [])
        {
            token.ThrowIfCancellationRequested();
            var mesh = instance.Mesh;
            if (mesh.Vertices.Length == 0 || mesh.Indices.Length == 0) continue;
            string id = $"mesh_{mesh.TagHash:X8}_{mesh.PartIndex:X}_{meshData.Count}";
            var skin = skins.FirstOrDefault(value => value.ModelTag == mesh.TagHash && value.PartOffsets.Contains(mesh.PartIndex));
            var lanes = skin is null ? null : Lanes(skin, mesh);
            meshData.Add(WriteMesh(catalog, root, instance, id, lanes, meshMaterialReader, notices, scene!.ObjectChannelTable));
            if (rig is not null && skin is null && mesh.Native?.Kind == NativeMeshKind.Entity)
                notices.Add(new("Skin", "Preserved", "No skin mapping; bind pose and native bytes kept.", mesh.TagHash));
        }
        string? scenePath = null;
        if (meshData.Count != 0)
        {
            scenePath = "scene.gltf";
            BundleSceneWriter.Write(Path.Combine(root, scenePath), meshData, rig, sampled);
        }

        var seeds = new HashSet<uint> { rootTag, sourceTag };
        foreach (var pair in meshData) { seeds.Add(pair.Info.ModelTag); seeds.Add(pair.Info.MaterialTag); }
        foreach (var effect in effects)
        {
            seeds.Add(effect.SourceTag);
            foreach (var system in effect.Systems)
            { seeds.Add(system.Tag); seeds.Add(system.EmitterTag); seeds.Add(system.RasterMaterial); seeds.Add(system.MeshTag); foreach (uint material in system.ComputeMaterials) seeds.Add(material); }
            foreach (var flare in effect.FlareElements) { seeds.Add(flare.Material); seeds.Add(flare.Parameters); }
        }
        foreach (uint tag in emitters) seeds.Add(tag);
        var nativeAssets = ExportNative(catalog, root, seeds, options, notices, token, progress);
        foreach (var asset in nativeAssets.Where(value => value.Reference == EffectReader.EmitterClass
            && value.Type is 8 or 16 && particleValues.All(emitter => emitter.TagHash != value.TagHash)))
            try { particleValues.Add(BundleParticlesCodec.Describe(catalog, asset.TagHash)); }
            catch (Exception error) when (Recoverable(error)) { notices.Add(new("Particles", "Preserved", error.Message, asset.TagHash)); }
        if (particleValues.Count != 0)
        {
            particlePath ??= "particles/emitters.json";
            WriteParticles(BundlePaths.Resolve(root, particlePath), new BundleParticles(particleValues, effects));
        }
        if (scene is not null) WriteEffects(catalog, scene, root, notices, token, progress);
        var (materials, textures, shaders) = WriteSources(catalog, root, nativeAssets, meshData, notices, token, progress,
            scene?.ObjectChannelTable);
        notices.Add(new("Materials", "Shader preview", "Texture roles follow the shader; dye and lighting are approximate."));
        notices.Add(new("Shaders", "Preserved", "Kept as metadata; Blender does not run them."));
        notices.Add(new("Geometry", "Fixed topology", "Vertices, topology, weights and rig must stay the same."));
        notices.Add(new("Animation", "Control edits", "Reimport reads the control actions, not the sampled bones."));
        string? collisionPath = null;
        if (scene is not null)
            try { collisionPath = BundleCollisionCodec.Export(catalog, scene, root, notices, token); }
            catch (Exception error) when (Recoverable(error)) { notices.Add(new("Collision", "Not exported", error.Message, rootTag)); }
        var manifest = new AssetBundleManifest(Format, 1, rootTag, sourceTag, name, options.Context, scenePath,
            meshData.Select(value => value.Info).ToArray(), materials, textures, shaders, animations, nativeAssets, notices, particlePath)
        {
            CollisionPath = collisionPath,
            CollisionSha256 = collisionPath is null ? null : BundlePaths.FileHash(BundlePaths.Resolve(root, collisionPath))
        };
        BundlePaths.Write(Path.Combine(root, ManifestName), manifest);
        File.WriteAllText(Path.Combine(root, "README.txt"), BundleInstructions);
        return manifest;
    }

    /**
     * Joint and weight lanes of one scene mesh; Bake first moves the geometry onto a bone.
     * Rest holds one skin matrix per bone; each vertex is first skinned by it.
     */
    private sealed record MeshSkin(int[][] Joints, float[][] Weights, Matrix4x4? Bake = null,
        IReadOnlyList<Matrix4x4>? Rest = null)
    {
        /** Identity when the vertex's bones are all identity, so unposed vertices keep their exact bits. */
        public Matrix4x4 At(int vertex)
        {
            if (Rest is null) return Bake ?? Matrix4x4.Identity;
            var result = new Matrix4x4();
            bool moved = false;
            for (int lane = 0; lane < 4; lane++)
            {
                if (Weights[vertex][lane] == 0) continue;
                var matrix = Rest[Joints[vertex][lane]];
                moved |= !matrix.IsIdentity;
                result += matrix * Weights[vertex][lane];
            }
            return moved ? result : Matrix4x4.Identity;
        }
    }

    /** Normalized native lanes per scene vertex; null when the mesh lost its source vertex order. */
    private static MeshSkin? Lanes(ModelSkinBinding skin, MeshData mesh)
    {
        if (mesh.SourceVertexIndices.Length != mesh.Vertices.Length) return null;
        var joints = new int[mesh.Vertices.Length][]; var weights = new float[mesh.Vertices.Length][];
        for (int vertex = 0; vertex < joints.Length; vertex++)
        {
            var value = skin.Vertices[checked((int)mesh.SourceVertexIndices[vertex])];
            float sum = value.Weights.X + value.Weights.Y + value.Weights.Z + value.Weights.W;
            if (sum <= 0) throw new InvalidDataException("Skin weights sum to zero.");
            weights[vertex] = [value.Weights.X / sum, value.Weights.Y / sum, value.Weights.Z / sum, value.Weights.W / sum];
            int[] indices = [value.Bone0, value.Bone1, value.Bone2, value.Bone3];
            joints[vertex] = indices.Select((bone, index) => weights[vertex][index] == 0 ? 0 : bone).ToArray();
        }
        return new(joints, weights);
    }

    /**
     * Writes one instance's geometry file and returns its manifest entry.
     * @param skin Lanes of the mesh; null writes it unskinned.
     * @param channels Object channel table the vertex constants read.
     */
    private static (BundleMesh Info, BundleGeometry Geometry) WriteMesh(PackageCatalog catalog, string root,
        MeshInstance instance, string id, MeshSkin? skin, GameMaterialReader reader, List<BundleNotice> notices,
        IReadOnlyList<Vector4>? channels = null)
    {
        var mesh = instance.Mesh;
        int[][]? joints = skin?.Joints; float[][]? weights = skin?.Weights;
        NativeSurfaceBasis? basis = null; string? basisError = null;
        // A refused tangent frame costs this mesh its authored normals, not the whole export.
        if (mesh.Native is { } native && NativeMeshBasis.IsSupported(catalog, native))
            try { basis = NativeMeshBasis.Read(catalog, mesh); }
            catch (Exception error) when (Recoverable(error)) { basisError = error.Message; }
        if (basis is null && !notices.Any(value => value.Surface == "Normals" && value.TagHash == mesh.TagHash))
            notices.Add(new("Normals", "Diagnostic", basisError ?? "Tangent frame not decoded; normals generated.", mesh.TagHash));
        var placed = skin?.Bake is null ? instance.Transform : Matrix4x4.Identity;
        if (basis is not null && !UniformBasisTransform(placed)
            && !notices.Any(value => value.Surface == "Tangent transform" && value.TagHash == mesh.TagHash))
            notices.Add(new("Tangent transform", "Preview limit", "Nonuniform, sheared or mirrored transforms need a per-vertex basis in Blender.", mesh.TagHash));
        Matrix4x4 Bake(int vertex) => skin?.At(vertex) ?? Matrix4x4.Identity;
        var geometry = new BundleGeometry(id, mesh.Vertices.Select((value, vertex) => V(Point(value.Position, Bake(vertex)))).ToArray(),
            (basis?.Normals ?? mesh.Vertices.Select(value => value.Normal)).Select((value, vertex) => V(Direction(value, Bake(vertex)))).ToArray(),
            mesh.Vertices.Select(value => new[] { value.TexCoord.X, value.TexCoord.Y }).ToArray(),
            mesh.Indices, joints, weights, basis?.Tangents.Select((value, vertex) =>
            {
                var tangent = Direction(new(value.X, value.Y, value.Z), Bake(vertex));
                return new[] { tangent.X, tangent.Y, tangent.Z, value.W };
            }).ToArray(),
            basis is null ? "Diagnostic" : NativeMeshBasis.StaticProfile);
        // A baked skin moved the geometry, so a pre-skin offset would point the wrong way.
        try { geometry = geometry with { VertexMotion = NativeVertexMotion.Read(catalog, mesh, reader)
            ?? (skin?.Bake is null && skin?.Rest is null ? NativeVertexInputs.Motion(catalog, mesh, reader, channels) : null) }; }
        catch (Exception error) when (Recoverable(error))
        { notices.Add(new("Vertex motion", "Preserved", error.Message, mesh.MaterialTag)); }
        geometry = geometry with { TexCoords1 = DetailTexCoords(catalog, mesh, reader),
            Colors = NativeVertexInputs.Colors(mesh), TexCoordsZw = NativeVertexInputs.TexCoordZw(mesh),
            RawPositions = NativeVertexInputs.RawPositions(mesh) };
        string path = $"geometry/{id}.json";
        BundlePaths.Write(BundlePaths.Resolve(root, path), geometry);
        string? skinHash = joints is null ? null : BundlePaths.Hash(JsonSerializer.SerializeToUtf8Bytes(new { joints, weights }));
        string skinning = "Linear";
        if (joints is not null && mesh.Native?.Kind == NativeMeshKind.Entity
            && catalog.TryGetEntry(mesh.MaterialTag, out var materialEntry) && materialEntry.Reference == STechnique.Class)
        {
            var material = reader.Read(mesh.MaterialTag);
            if (material.VertexShader is { } vertexShader && NativeShaderContracts.ClassifyEntityPalette(
                reader.ReadShaderBytecode(vertexShader), vertexShader.Disassembly) == NativeEntityPalette.DualQuaternion)
                skinning = "DualQuaternion";
        }
        return (new(id, mesh.TagHash, mesh.PartIndex, instance.ModelSourceConfigTag, mesh.MaterialTag, mesh.LodLevel,
            path, BundlePaths.FileHash(BundlePaths.Resolve(root, path)), BundleSceneWriter.MatrixValues(placed), joints is not null, skinHash)
            { RenderStage = mesh.Native?.RenderStage ?? -1, Skinning = skinning,
                InitiallyVisible = instance.InitiallyVisible, InitialVisibilityReason = instance.InitialVisibilityReason }, geometry);
    }

    /** Identity skips the math, so an unbaked mesh keeps its exact bits, signed zeros included. */
    private static Vector3 Point(Vector3 value, Matrix4x4 bake) => bake.IsIdentity ? value : Vector3.Transform(value, bake);
    private static Vector3 Direction(Vector3 value, Matrix4x4 bake) =>
        bake.IsIdentity ? value : Vector3.Normalize(Vector3.TransformNormal(value, bake));

    /**
     * Writes the material, texture and shader sections of the preserved native assets.
     * @param channels Object channel table the material constants read; null keeps the viewer default.
     */
    private static (List<BundleMaterial> Materials, List<BundleTexture> Textures, List<BundleShader> Shaders) WriteSources(
        PackageCatalog catalog, string root, IReadOnlyList<BundleNativeAsset> nativeAssets,
        IReadOnlyList<(BundleMesh Info, BundleGeometry Geometry)> meshData, List<BundleNotice> notices,
        CancellationToken token, Action<string>? progress, IReadOnlyList<Vector4>? channels = null)
    {
        var materials = new List<BundleMaterial>(); var textures = new List<BundleTexture>(); var shaders = new List<BundleShader>();
        var materialReader = new GameMaterialReader(catalog);
        var materialInputs = MaterialPreviewAnalyzer.CreateInputs(catalog);
        materialInputs.ObjectChannels = channels;
        var volumeAtlases = new Dictionary<uint, (string Path, string Sha256)?>();
        foreach (var asset in nativeAssets)
        {
            token.ThrowIfCancellationRequested();
            progress?.Invoke($"Exporting {asset.Name}");
            if (asset.Type is 8 or 16 && asset.Reference == STechnique.Class)
                try
                {
                    string folder = $"materials/{asset.TagHash:X8}";
                    string materialPath = folder + "/" + MaterialInterchange.ManifestName;
                    var document = MaterialInterchange.Describe(catalog, asset.TagHash) with { Name = asset.Name };
                    BundlePaths.Write(BundlePaths.Resolve(root, materialPath), document);
                    BundleMaterialPreview? preview = null;
                    var stages = meshData.Where(value => value.Info.MaterialTag == asset.TagHash)
                        .Select(value => value.Info.RenderStage).ToArray();
                    try { preview = MaterialPreviewAnalyzer.Read(catalog, asset.TagHash, materialInputs, stages); }
                    catch (Exception error) when (Recoverable(error)) { notices.Add(new("Material preview", "Unavailable", error.Message, asset.TagHash)); }
                    if (preview is not null) preview = WithVolumeAtlases(catalog, root, preview, volumeAtlases, notices);
                    materials.Add(new(asset.TagHash, materialPath, preview?.Mode, preview));
                }
                catch (Exception error) when (Recoverable(error)) { notices.Add(new("Material", "Preserved", error.Message, asset.TagHash)); }
            if (asset.Type == 32 && asset.SubType is >= 1 and <= 3)
                textures.Add(ExportTexture(catalog, root, asset.TagHash, notices, token));
            if (asset.Type == 33)
                try
                {
                    var shader = materialReader.ReadShader(asset.TagHash, asset.SubType == 6 ? GameShaderStage.Compute : (GameShaderStage)asset.SubType);
                    byte[] bytecode = materialReader.ReadShaderBytecode(shader);
                    string path = $"shaders/{asset.TagHash:X8}.dxbc", disassembly = $"shaders/{asset.TagHash:X8}.txt";
                    Directory.CreateDirectory(Path.Combine(root, "shaders"));
                    File.WriteAllBytes(BundlePaths.Resolve(root, path), bytecode); File.WriteAllText(BundlePaths.Resolve(root, disassembly), shader.Disassembly);
                    shaders.Add(new(asset.TagHash, shader.BytecodeTag, shader.Stage.ToString(), shader.Profile, path, disassembly, BundlePaths.Hash(bytecode)));
                }
                catch (Exception error) when (Recoverable(error)) { notices.Add(new("Shader", "Preserved", error.Message, asset.TagHash)); }
        }
        return (materials, textures, shaders);
    }

    /**
     * Writes each volume texture a preview program samples once, as its depth slices side by side, and links the preview to it.
     * @param written Atlases already written under root by texture tag; null marks one that failed, which reads its mean.
     */
    internal static BundleMaterialPreview WithVolumeAtlases(PackageCatalog catalog, string root, BundleMaterialPreview preview,
        Dictionary<uint, (string Path, string Sha256)?> written, List<BundleNotice> notices)
    {
        var variants = preview.BlendVariants?.ToDictionary(pair => pair.Key, pair => WithVolumeAtlases(catalog, root, pair.Value, written, notices));
        var sampled = preview.Program?.Nodes.Where(node => node.Operation == "volume").Select(node => node.Slot).ToHashSet() ?? [];
        var volumes = preview.VolumeTextures?.Where(pair => sampled.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair =>
        {
            var volume = pair.Value;
            if (!written.TryGetValue(volume.TextureTag, out var file))
            {
                try
                {
                    string path = $"textures/{volume.TextureTag:X8}_volume.png";
                    byte[] png = PngInterchange.Write(MaterialPreviewAnalyzer.VolumeAtlas(catalog, volume));
                    Directory.CreateDirectory(Path.Combine(root, "textures"));
                    File.WriteAllBytes(BundlePaths.Resolve(root, path), png);
                    file = (path, BundlePaths.Hash(png));
                }
                catch (Exception error) when (Recoverable(error)) { notices.Add(new("Texture", "Volume preview unavailable", error.Message, volume.TextureTag)); }
                written[volume.TextureTag] = file;
            }
            return file is { } atlas ? volume with { Path = atlas.Path, Sha256 = atlas.Sha256 } : volume;
        });
        return preview with { VolumeTextures = volumes is { Count: > 0 } ? volumes : null, BlendVariants = variants };
    }

    // JSON has no infinity; an open effect range bound, such as a gate maximum, is written as the largest float.
    private static readonly JsonSerializerOptions ParticleJson = new(BundlePaths.Json) { Converters = { new FiniteSingleConverter() } };

    internal static void WriteParticles(string path, BundleParticles particles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(particles, ParticleJson));
    }

    /** Infinities become the float limit of the same sign; NaN, which no reader can use, becomes 0. */
    private sealed class FiniteSingleConverter : JsonConverter<float>
    {
        public override float Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetSingle();
        public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(float.IsNaN(value) ? 0 : Math.Clamp(value, float.MinValue, float.MaxValue));
    }

    /** The spawn, variant, parameter picks and channel values the root was read with; null when it used none. */
    private static string? PreviewInputs(SceneDocument scene)
    {
        var parts = new List<string>();
        if (scene.PreviewSpawn is { } spawn) parts.Add($"spawn 0x{spawn.Tag:X8}+0x{spawn.Offset:X}");
        if (scene.PreviewVariant != 0) parts.Add($"variant 0x{scene.PreviewVariant:X8}");
        parts.AddRange(scene.PreviewParameters.Select(pick => $"parameter 0x{pick.Key:X8} = 0x{pick.Value:X8}"));
        parts.AddRange(scene.ChannelValues.Select(channel => $"channel 0x{channel.Key:X8} = {channel.Value:0.###}"));
        return parts.Count == 0 ? null : string.Join("; ", parts) + ".";
    }

    private static float[] V(Vector3 value) => [value.X, value.Y, value.Z];
    /** Native per-vertex normalization commutes with positive uniform instance transforms only. */
    private static bool UniformBasisTransform(Matrix4x4 transform)
    {
        Vector3 x = new(transform.M11, transform.M12, transform.M13),
            y = new(transform.M21, transform.M22, transform.M23), z = new(transform.M31, transform.M32, transform.M33);
        float xx = x.LengthSquared(), yy = y.LengthSquared(), zz = z.LengthSquared();
        float tolerance = Math.Max(xx, Math.Max(yy, zz)) * 0.00001f;
        return float.IsFinite(tolerance) && xx > 1e-12f && transform.GetDeterminant() > 0
            && Math.Abs(xx - yy) <= tolerance && Math.Abs(xx - zz) <= tolerance
            && Math.Abs(Vector3.Dot(x, y)) <= tolerance && Math.Abs(Vector3.Dot(x, z)) <= tolerance
            && Math.Abs(Vector3.Dot(y, z)) <= tolerance;
    }
    private static bool Recoverable(Exception error) => error is InvalidDataException or NotSupportedException
        or ArgumentException or OverflowException or IndexOutOfRangeException or KeyNotFoundException;

    private const string BundleInstructions = """
        Sunrise Studio asset bundle

        This needs the Sunrise Blender add-on, installed in Blender separately; its README explains
        the steps and the editing rules: https://github.com/stanuwu/SunriseBlenderPlugin

        In Blender, choose File > Import > Sunrise bundle and open asset.sunrise-bundle.json.
        Export bundle edits from the Sunrise tab in the 3D View sidebar, then import
        asset.edited.sunrise-bundle.json in Sunrise Studio.

        Keep this working bundle outside Sunrise projects and the game installation.
        It contains original package data. Sunrise projects retain derived data as deltas.
        """;
}
