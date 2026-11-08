using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using SunriseStudio.Animation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

namespace SunriseStudio.Authoring;

public sealed record PreparedWeaponOriginalAsset(IReadOnlyList<ProjectAssetAddition> Assets,
    IReadOnlyDictionary<int, uint> VariantEntities, OriginalAssetInspection SurfaceReport)
{
    public IReadOnlyDictionary<string, string> SourceHashes { get; init; } = new Dictionary<string, string>();
    public uint? GameplayEntityTag { get; init; }
    public uint? AnimationEntityTag { get; init; }
    public IReadOnlyDictionary<string, uint> AnimationClips { get; init; } = new Dictionary<string, uint>();
}

/** Every source mesh must belong to one native art selector before any asset is prepared. */
public static class WeaponOriginalAssetImporter
{
    public const string SidecarSuffix = ".sunrise-weapon.json";
    public const string ReloadSurface = "Reload effect", ClipBurstSurface = "Clip bursts", ArmsSurface = "First-person arms";
    private const uint VertexShaderTag = 0x81532FC4;
    private sealed record Part(int Selector, string[] Meshes);
    private sealed record Source(Part[] Parts, float Scale,
        IReadOnlyDictionary<string, int> Controls, WeaponSourceClip[] Clips, WeaponArmsSource? Arms);

    /** Weapon surfaces pass only after their source assignments and native profiles validate. */
    public static OriginalAssetInspection Inspect(PackageCatalog catalog, string path,
        IReadOnlyList<WeaponArtVariant> variants)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(variants);
        var source = OriginalAssetImporter.Inspect(path);
        var surfaces = source.Surfaces.Select(surface => surface.Name switch
        {
            "Skin weights" when surface.Present => surface with { Detail = "Needs a weapon rig map." },
            "Skeleton" when surface.Present => surface with { Detail = "Needs a weapon rig map." },
            "Animations" when surface.Present => surface with { Detail = "Needs weapon clip bindings." },
            "Particles" when surface.Present => surface with { Detail = "Needs a weapon rig map." },
            _ => surface
        }).ToList();
        using (var json = GltfInterchange.ReadJson(path))
        {
            bool external = new[] { "buffers", "images" }.Any(field =>
                json.RootElement.TryGetProperty(field, out var rows) && rows.EnumerateArray().Any(row =>
                    row.TryGetProperty("uri", out var uri) && !uri.GetString()!.StartsWith("data:", StringComparison.Ordinal)));
            surfaces.Add(new("Source dependencies", external, !external,
                external ? "Weapon exports must embed buffers and images in the scene file."
                    : "Buffers and images are embedded in the source scene."));
        }
        try
        {
            var meshes = GltfInterchange.Read(path, skipUnsupported: true);
            var parts = ReadParts(path, meshes.Select(mesh => mesh.Name).ToArray(), variants);
            int visual = meshes.Count(mesh => parts.Parts.Any(part => part.Meshes.Contains(mesh.Name)));
            int geometry = surfaces.FindIndex(surface => surface.Name == "Mesh geometry");
            surfaces[geometry] = surfaces[geometry] with { Detail = $"{visual} meshes in {parts.Parts.Length} art parts" };
            int artRow = surfaces.Count;
            if (parts.Controls.Count != 0)
            {
                if (!WeaponAssetEntityCompiler.Supports(variants))
                    throw new NotSupportedException("The weapon needs the checked native frame and art profiles.");
                var names = parts.Parts.SelectMany(part => part.Meshes).ToHashSet();
                _ = WeaponGltfGeometryReader.Read(path, parts.Controls, names);
                Set("Skin weights", "One joint per vertex.");
                Set("Skeleton", "Maps to the weapon's five bones.");
                using var scene = GltfInterchange.ReadJson(path);
                string[] exported = scene.RootElement.TryGetProperty("animations", out var animations)
                    ? animations.EnumerateArray().Select(animation => animation.GetProperty("name").GetString()!).ToArray() : [];
                if (!exported.Order().SequenceEqual(parts.Clips.Select(clip => clip.Action).Order()))
                    throw new InvalidDataException("Every exported action needs one weapon clip binding.");
                if (parts.Clips.Length != 0)
                {
                    var rig = new SkeletonReader(catalog).Read(WeaponPresentationAuthoring.GameplayTemplate);
                    foreach (var clip in parts.Clips)
                        _ = WeaponGltfAnimationReader.Read(path, clip.Action, parts.Controls, rig, 2, parts.Scale);
                    if (parts.Arms is not null) WeaponArmsRig.Check(catalog, parts.Arms, parts.Scale);
                    Set("Animations", "Fire, reload and named clips");
                    if (parts.Arms is not null) surfaces.Add(new(ArmsSurface, true, true, $"{parts.Arms.Clips.Count} arm actions. May not work in game."));
                    if (surfaces.Any(surface => surface.Name == "Particles" && surface.Present))
                    {
                        var emitters = WeaponGltfEmitterReader.Read(path, parts.Controls, rig, "review");
                        WeaponParticleBinding.ValidateSystem(catalog, WeaponAssetParticleCompiler.BurstTemplate);
                        string[] perShot = emitters.Where(emitter => WeaponGltfEmitterReader.FiresEveryShot(emitter.Event))
                            .Select(emitter => emitter.Event == WeaponGltfEmitterReader.EjectEvent ? "Eject" : "Muzzle")
                            .OrderDescending().ToArray();
                        Set("Particles", perShot.Length == 0 ? "None per shot" : string.Join(", ", perShot));
                        if (emitters.Any(emitter => emitter.Event == WeaponGltfEmitterReader.ReloadEvent))
                            surfaces.Add(new(ReloadSurface, true, true, "On the gun while reloading. May not work in game."));
                        if (EffectEvents(path).Any(name => !WeaponGltfEmitterReader.FiresEveryShot(name)
                            && name != WeaponGltfEmitterReader.ReloadEvent))
                            surfaces.Add(new(ClipBurstSurface, true, true, "Skipped: does not show in game"));
                    }
                }
            }
            // The row reads ready only once every check above passed.
            surfaces.Insert(artRow, new("Weapon art parts", true, true, "Each mesh has one art part."));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException
            or NotSupportedException or ArgumentException or KeyNotFoundException)
        { surfaces.Add(new("Weapon setup", true, false, error.Message)); }
        return new(source.Path, surfaces);

        void Set(string surface, string detail)
        {
            int index = surfaces.FindIndex(value => value.Name == surface);
            surfaces[index] = surfaces[index] with { Supported = true, Detail = detail };
        }
    }

    /** Preparation refuses unsupported surfaces before allocating model, material or entity records. */
    public static PreparedWeaponOriginalAsset Prepare(PackageCatalog catalog, string path,
        ushort packageId, int firstEntry, string name, IReadOnlyList<WeaponArtVariant> variants)
    {
        using var snapshot = new SourceSnapshot(path);
        var result = PrepareFrozen(catalog, snapshot.Path, packageId, firstEntry, name, variants);
        var prepared = result with
        {
            SurfaceReport = result.SurfaceReport with { Path = System.IO.Path.GetFullPath(path) },
            SourceHashes = snapshot.Hashes
        };
        ValidateSources(prepared);
        return prepared;
    }

    /** Frozen source files keep geometry, textures, and part assignments on one revision. */
    private static PreparedWeaponOriginalAsset PrepareFrozen(PackageCatalog catalog, string path,
        ushort packageId, int firstEntry, string name, IReadOnlyList<WeaponArtVariant> variants)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var inspection = Inspect(catalog, path, variants);
        var refused = inspection.Surfaces.FirstOrDefault(surface => surface.Present && !surface.Supported);
        if (refused is not null) throw new NotSupportedException(refused.Name + ": " + refused.Detail);
        var allMeshes = GltfInterchange.Read(path, true);
        var source = ReadParts(path, allMeshes.Select(mesh => mesh.Name).ToArray(), variants);
        var visualNames = source.Parts.SelectMany(part => part.Meshes).ToHashSet();
        var weighted = source.Controls.Count == 0 ? null : WeaponGltfGeometryReader.Read(path, source.Controls, visualNames);
        var meshes = weighted?.Select(part => part.Mesh).ToArray()
            ?? allMeshes.Where(mesh => visualNames.Contains(mesh.Name)).ToArray();
        var material = WeaponAssetMaterialCompiler.PrepareUsed(catalog, path, name,
            packageId, checked(firstEntry + 9), VertexShaderTag,
            meshes.Select(mesh => mesh.MaterialIndex ?? -1).ToArray());
        var additions = material.Assets.ToList();
        var entities = new Dictionary<int, uint>();
        int cursor = material.NextEntryIndex;
        var templates = variants.Select(variant => new
        {
            variant.EntityTag,
            Part = new AssetSceneLoader(catalog).Load(variant.EntityTag).Instances.FirstOrDefault(instance =>
                instance.InitiallyVisible != false && instance.Mesh.LodLevel == 0
                && instance.Mesh.Native is { NativeDeclarationId: 139, RenderStage: 0, VertexShaderTag: VertexShaderTag })
        }).Where(value => value.Part is not null).ToArray();
        if (templates.Length == 0)
            throw new NotSupportedException("The weapon art has no checked opaque matrix geometry profile.");
        Matrix4x4 transform = Matrix4x4.CreateRotationZ(-MathF.PI / 2) * Matrix4x4.CreateScale(source.Scale);
        foreach (var part in source.Parts)
        {
            int modelEntry = entities.Count == 0 ? firstEntry : cursor;
            var variant = variants.Single(value => value.Selector == part.Selector);
            var template = templates.FirstOrDefault(value => value.EntityTag == variant.EntityTag) ?? templates[0];
            var donor = template.Part!;
            var stream = donor.Mesh.Native!.VertexStreams[0];
            short palette = donor.Mesh.SourceVertexIndices.Select(vertex =>
                VertexStreamCodec.ReadPaletteIndex(stream.Data, stream.Stride, vertex))
                .GroupBy(value => value).OrderByDescending(group => group.Count()).First().Key;
            var primitives = meshes.Select((mesh, index) => (Mesh: mesh, Index: index))
                .Where(value => part.Meshes.Contains(value.Mesh.Name)).Select(value =>
                new WeaponRigidPrimitive(value.Mesh with
                {
                    Positions = value.Mesh.Positions.Select(point => Vector3.Transform(point, transform)).ToArray(),
                    Normals = value.Mesh.Normals.Select(normal => Vector3.Normalize(Vector3.TransformNormal(normal, transform))).ToArray(),
                    TexCoords = value.Mesh.TexCoords.Select(uv => new Vector2(uv.X, 1 - uv.Y)).ToArray()
                }, material.MaterialTags[value.Mesh.MaterialIndex ?? -1], palette)
                { PaletteIndices = weighted?[value.Index].PaletteIndices }).ToArray();
            var model = WeaponAssetModelCompiler.Prepare(catalog, primitives,
                new(template.EntityTag, donor.Mesh.TagHash, donor.Mesh.PartIndex,
                    packageId, modelEntry, name + ".part" + part.Selector,
                    weighted is null ? 0 : WeaponPresentationAuthoring.GameplayTemplate));
            additions.AddRange(model.Assets);
            var entity = WeaponAssetEntityCompiler.Prepare(catalog, variant.EntityTag,
                model.ModelTag, packageId, checked(modelEntry + model.Assets.Count), name + ".part" + part.Selector);
            additions.AddRange(entity.Assets);
            if (entities.Count != 0) cursor = checked(modelEntry + model.Assets.Count + entity.Assets.Count);
            entities.Add(part.Selector, entity.EntityTag);
        }
        if (source.Clips.Length == 0) return new(additions, entities, inspection);
        var clips = WeaponAssetClipCompiler.Prepare(catalog, path, source.Controls, source.Clips,
            source.Scale, packageId, cursor, name, source.Arms);
        additions.AddRange(clips.Assets); cursor = checked(cursor + clips.Assets.Count);
        var presentation = WeaponPresentationAuthoring.Prepare(catalog, packageId, cursor, name,
            clips.Clips.Single(clip => clip.Name == "reload").Tag,
            clips.Clips.Single(clip => clip.Name == "fire").Tag, clips.AdditionalClips);
        additions.AddRange(presentation.Assets);
        // The firing step builds Muzzle, Eject and Reload; other clip bursts are skipped.
        return new(additions, entities, inspection)
        {
            GameplayEntityTag = presentation.GameplayEntityTag,
            AnimationEntityTag = presentation.PresentationEntityTag,
            AnimationClips = clips.Clips.ToDictionary(clip => clip.Name, clip => clip.Tag)
        };
    }

    /** The event of each emitter settings row in the weapon sidecar. */
    internal static string[] EffectEvents(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(Path.GetFullPath(path), null) + SidecarSuffix));
        return json.RootElement.TryGetProperty("effects", out var effects) && effects.ValueKind == JsonValueKind.Array
            ? effects.EnumerateArray().Select(effect => effect.GetProperty("event").GetString() ?? "").ToArray() : [];
    }

    /** Publication must reject sources that changed after the frozen preparation completed. */
    public static void ValidateSources(PreparedWeaponOriginalAsset prepared) => ValidateSources(prepared.SourceHashes);

    /** An absent optional sidecar is part of the source snapshot too. */
    public static void ValidateSources(IReadOnlyDictionary<string, string> sourceHashes)
    {
        foreach (var (path, expected) in sourceHashes)
        {
            string current = File.Exists(path) ? Hash(File.ReadAllBytes(path)) : "missing";
            if (current != expected)
                throw new InvalidDataException("Weapon source files changed after preparation. Prepare them again.");
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /** Only files owned by this snapshot are removed when preparation ends. */
    private sealed class SourceSnapshot : IDisposable
    {
        private readonly string directory;
        private readonly List<string> files = [];
        public string Path { get; }
        public IReadOnlyDictionary<string, string> Hashes { get; }

        public SourceSnapshot(string source)
        {
            source = System.IO.Path.GetFullPath(source);
            directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sunrise-weapon-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, System.IO.Path.GetFileName(source));
            var hashes = new Dictionary<string, string>();
            Hashes = hashes;
            try
            {
                foreach (string input in new[] { source,
                    System.IO.Path.ChangeExtension(source, null) + OriginalAssetImporter.SidecarSuffix,
                    System.IO.Path.ChangeExtension(source, null) + SidecarSuffix,
                    System.IO.Path.ChangeExtension(source, null) + WeaponArmsRig.SourceSuffix })
                {
                    string target = System.IO.Path.Combine(directory, System.IO.Path.GetFileName(input));
                    if (!File.Exists(input)) { hashes.Add(input, "missing"); continue; }
                    byte[] bytes = File.ReadAllBytes(input);
                    hashes.Add(input, Hash(bytes));
                    File.WriteAllBytes(target, bytes);
                    files.Add(target);
                }
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            foreach (string file in files) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    /** The sidecar cannot omit, repeat or move a mesh to an absent art selector. */
    private static Source ReadParts(string path, string[] meshNames, IReadOnlyList<WeaponArtVariant> variants)
    {
        string file = Path.ChangeExtension(Path.GetFullPath(path), null) + SidecarSuffix;
        if (!File.Exists(file)) throw new InvalidDataException("Keep a .sunrise-weapon.json part map beside the source scene.");
        if (new FileInfo(file).Length > 1024 * 1024)
            throw new InvalidDataException("Weapon part map exceeds one megabyte.");
        using var json = JsonDocument.Parse(File.ReadAllText(file));
        var root = json.RootElement;
        if (root.GetProperty("format").GetString() != "sunrise-weapon-source"
            || root.GetProperty("version").GetInt32() != 1
            || root.GetProperty("scene").GetString() != Path.GetFileName(path))
            throw new InvalidDataException("Weapon part map does not describe this source scene.");
        if (root.GetProperty("forward").GetString() != "+Y" || root.GetProperty("up").GetString() != "+Z")
            throw new NotSupportedException("Weapon source axes must be +Y forward and +Z up.");
        float scale = root.GetProperty("metresPerUnit").GetSingle();
        if (!float.IsFinite(scale) || scale <= 0)
            throw new InvalidDataException("Weapon source metresPerUnit must be finite and positive.");
        var parts = root.GetProperty("parts").EnumerateArray().Select(part =>
            new Part(part.GetProperty("selector").GetInt32(), part.GetProperty("meshes").EnumerateArray()
                .Select(mesh => mesh.GetString() ?? "").ToArray())).ToArray();
        string originalSidecar = Path.ChangeExtension(Path.GetFullPath(path), null) + OriginalAssetImporter.SidecarSuffix;
        if (File.Exists(originalSidecar))
        {
            using var original = JsonDocument.Parse(File.ReadAllText(originalSidecar));
            var emitters = original.RootElement.GetProperty("emitters").EnumerateArray()
                .Select(emitter => emitter.GetProperty("object").GetString()).ToHashSet();
            if (root.TryGetProperty("effects", out var effects))
                foreach (var effect in effects.EnumerateArray())
                    if (effect.TryGetProperty("instanceObject", out var instance)) emitters.Add(instance.GetString());
            meshNames = meshNames.Where(mesh => !emitters.Contains(mesh)).ToArray();
        }
        if (!parts.Select(part => part.Selector).Order().SequenceEqual(variants.Select(variant => variant.Selector).Order())
            || parts.Any(part => part.Meshes.Length == 0 || part.Meshes.Any(string.IsNullOrWhiteSpace))
            || !parts.SelectMany(part => part.Meshes).Order().SequenceEqual(meshNames.Distinct().Order()))
            throw new InvalidDataException("Every source mesh needs exactly one native art selector.");
        var controls = root.TryGetProperty("rig", out var rig)
            ? rig.GetProperty("bones").EnumerateArray().ToDictionary(bone => bone.GetProperty("bone").GetString()!,
                bone => bone.GetProperty("control").GetInt32()) : new Dictionary<string, int>();
        var clips = root.TryGetProperty("clips", out var namedClips) ? namedClips.EnumerateArray()
            .Select(clip => new WeaponSourceClip(clip.GetProperty("action").GetString()!, clip.GetProperty("event").GetString()!)).ToArray() : [];
        WeaponArmsSource? arms = null;
        if (root.TryGetProperty("arms", out var armsRoot))
        {
            string scene = Path.GetFileNameWithoutExtension(path) + WeaponArmsRig.SourceSuffix;
            if (armsRoot.GetProperty("scene").GetString() != scene)
                throw new InvalidDataException("The arms scene must sit beside the weapon scene as " + scene + ".");
            string armsPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, scene);
            if (!File.Exists(armsPath)) throw new InvalidDataException("Keep " + scene + " beside the weapon scene.");
            arms = new(armsPath, armsRoot.GetProperty("clips").EnumerateArray().Select(clip => new WeaponArmsClip(
                clip.GetProperty("action").GetString()!, clip.GetProperty("event").GetString()!)).ToArray());
            if (arms.Clips.Count == 0 || arms.Clips.Any(clip => !clips.Any(weapon => weapon.Event == clip.Event)))
                throw new InvalidDataException("Each arms action needs a weapon action with the same event.");
        }
        return new(parts, scale, controls, clips, arms);
    }
}
