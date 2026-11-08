using System.Text.Json;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Authoring;

public sealed record OriginalAssetSurface(string Name, bool Present, bool Supported, string Detail);
public sealed record OriginalAssetInspection(string Path, IReadOnlyList<OriginalAssetSurface> Surfaces)
{
    public bool CanImportAll => Surfaces.All(surface => !surface.Present || surface.Supported);
}
public sealed record OriginalAssetImportOptions(string Path, ushort PackageId, string Name,
    uint? TemplateTag, bool SkipUnsupported);
public sealed record PreparedOriginalAsset(uint ModelTag, IReadOnlyList<ProjectAssetAddition> Assets, string Summary);

/** Static primitive materials compile through one checked opaque native shader profile. */
public static class OriginalAssetImporter
{
    public const string SidecarFormat = "sunrise-original-asset";
    public const string SidecarSuffix = ".sunrise-original.json";
    private const string MeshGeometry = "Mesh geometry";
    private const string Png = "image/png";
    private const int MaxSidecarBytes = 16 * 1024 * 1024;

    /** Reads the glTF JSON and the sidecar with the same stem; buffers and images stay unread. */
    public static OriginalAssetInspection Inspect(string path)
    {
        string scene = Path.GetFullPath(path);
        if (Path.GetExtension(scene).ToLowerInvariant() is not (".glb" or ".gltf"))
            throw new NotSupportedException("Choose the .glb or .gltf file of an original asset.");
        int? particleSystems = ReadParticleSystemCount(scene);
        using var json = GltfInterchange.ReadJson(scene);
        return new(scene, Describe(json.RootElement, particleSystems));
    }

    /**
     * Returns a one-line summary.
     * @param options TemplateTag picks the installed static model template; textures pick their own.
     */
    public static string Import(StudioProject project, PackageCatalog catalog, OriginalAssetImportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(catalog);
        catalog.InvalidateReadCache();
        int first = project.AddedAssets.Where(asset => asset.Native?.PackageId == options.PackageId)
            .Select(asset => asset.Native!.EntryIndex ?? -1).DefaultIfEmpty(-1).Max() + 1;
        var prepared = Prepare(catalog, options, first, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        project.ApplyTransaction(prepared.Assets, catalog: catalog);
        return prepared.Summary;
    }

    /** The complete model and material group is prepared before any project publication. */
    public static PreparedOriginalAsset Prepare(PackageCatalog catalog, OriginalAssetImportOptions options,
        int first, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Name);
        var inspection = Inspect(options.Path);
        var refused = inspection.Surfaces.Where(surface => surface.Present && !surface.Supported).ToList();
        if (refused.Count > 0 && !options.SkipUnsupported)
            throw new NotSupportedException($"{refused[0].Name}: {refused[0].Detail}");
        if (!inspection.Surfaces.First(surface => surface.Name == MeshGeometry).Present)
            throw new InvalidDataException("The scene has no mesh.");
        cancellationToken.ThrowIfCancellationRequested();
        // glTF image UVs go straight to D3D; geometry-only interchange flips V for its own round trip.
        var meshes = GltfInterchange.Read(inspection.Path, options.SkipUnsupported)
            .Select(mesh => mesh with { TexCoords = mesh.TexCoords.Select(uv => new System.Numerics.Vector2(uv.X, 1 - uv.Y)).ToArray() }).ToArray();
        using var document = GltfInterchange.ReadJson(inspection.Path);
        var images = new Dictionary<int, RgbaImage>();
        foreach (var image in GltfInterchange.ReadImages(inspection.Path).Where(entry => entry.MimeType == Png))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decoded = PngInterchange.Read(image.Bytes); decoded.Validate(); images.Add(image.Index, decoded);
        }
        int cursor = checked(first + 8);
        var materialTags = new Dictionary<int, uint>();
        var materials = new List<ProjectAssetAddition>();
        foreach (int index in meshes.Select(mesh => mesh.MaterialIndex ?? -1).Distinct())
        {
            var material = OriginalAssetMaterials.Prepare(catalog, document.RootElement, index < 0 ? null : index,
                images, options.Name + ".material" + materialTags.Count, options.PackageId, cursor,
                options.SkipUnsupported, cancellationToken);
            materialTags.Add(index, material.MaterialTag); materials.AddRange(material.Assets);
            cursor = checked(cursor + material.Assets.Count);
        }
        var model = MeshAssetImporter.PrepareParts(catalog, meshes,
            meshes.Select(mesh => materialTags[mesh.MaterialIndex ?? -1]).ToArray(), options.Name,
            options.PackageId, first, options.TemplateTag, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var skippedNames = refused.Select(surface => surface.Name).Distinct().ToArray();
        string skipped = skippedNames.Length == 0 ? "" : $" Skipped: {string.Join(", ", skippedNames)}.";
        string summary = $"Added {options.Name} as model {model.ModelTag:X8} ({Plural(model.VertexCount, "vertex", "vertices")}, "
            + $"{Plural(model.TriangleCount, "triangle", "triangles")}, {Plural(meshes.Length, "part", "parts")}) with "
            + $"{Plural(materialTags.Count, "opaque material", "opaque materials")} and bound textures.{skipped}";
        return new(model.ModelTag, model.Assets.Concat(materials).ToArray(), summary);
    }

    /** Surface order decides which refusal Import names first. */
    private static List<OriginalAssetSurface> Describe(JsonElement document, int? particleSystems)
    {
        var counts = Scan(document);
        var required = document.TryGetProperty("extensionsRequired", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(value => value.GetString() ?? "").ToList() : new List<string>();
        var geometryExtensions = required.Where(name => !GltfInterchange.IgnoredByStaticRead(name)).ToList();
        var textureExtensions = required.Where(name => GltfInterchange.IgnoredByStaticRead(name)
            && !name.StartsWith("KHR_materials_", StringComparison.Ordinal)).ToList();
        var imageTypes = document.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array
            ? images.EnumerateArray().Select(GltfInterchange.ImageType).ToList() : new List<string>();
        int png = imageTypes.Count(type => type == Png);
        var otherImages = imageTypes.Where(type => type != Png).GroupBy(ImageLabel)
            .Select(group => $"{group.Count()} {group.Key}").ToList();
        int materials = Length(document, "materials"), skins = Length(document, "skins");
        int animations = Length(document, "animations");
        int joints = document.TryGetProperty("skins", out var skinList) && skinList.ValueKind == JsonValueKind.Array
            ? skinList.EnumerateArray().Sum(skin => skin.TryGetProperty("joints", out var names) ? names.GetArrayLength() : 0)
            : 0;

        var surfaces = new List<OriginalAssetSurface>();
        void Add(string name, bool present, bool supported, string detail) =>
            surfaces.Add(new(name, present, supported, detail));

        bool geometry = counts.Triangles + counts.Others > 0;
        Add(MeshGeometry, geometry, counts.Triangles > 0 && counts.Others == 0 && geometryExtensions.Count == 0,
            !geometry ? "The default scene has no mesh. Import needs one." : Join(
                counts.Triangles > 0
                    ? $"Preserves {Plural(counts.Triangles, "triangle primitive", "triangle primitives")} from "
                        + $"{Plural(counts.Meshes, "mesh", "meshes")} as separate static parts with material assignments."
                    : "No triangle primitive.",
                counts.Others > 0
                    ? $"No encoder for {Plural(counts.Others, "point or line primitive", "point or line primitives")}."
                    : null,
                geometryExtensions.Count > 0
                    ? $"No support for required extension {string.Join(", ", geometryExtensions)}." : null));

        bool uvs = counts.Uvs + counts.ExtraUvs > 0;
        Add("UVs", uvs, counts.ExtraUvs == 0, !uvs ? "None. The mesh gets zero UVs." : Join(
            "The first UV set imports.",
            counts.ExtraUvs > 0
                ? $"No encoder for extra UV sets on {Plural(counts.ExtraUvs, "primitive", "primitives")}." : null,
            counts.Uvs < counts.Triangles ? "Primitives without UVs get zero UVs." : null));

        Add("Normals", counts.Normals + counts.Tangents > 0, counts.Tangents == 0,
            Join(counts.Normals == 0 ? "Normals are generated from the faces." : "Normals are imported; tangents are generated from UVs.",
                counts.Normals > 0 && counts.Normals < counts.Triangles ? "Missing normals are generated." : null,
                counts.Tangents > 0 ? "Authored tangent streams have no import path; skipping regenerates them." : null));

        Add("Vertex colors", counts.Colors > 0, false, counts.Colors == 0 ? "None."
            : $"Found on {Plural(counts.Colors, "primitive", "primitives")}. The static profile has no color stream.");

        Add("Textures", imageTypes.Count > 0, png == imageTypes.Count && textureExtensions.Count == 0,
            imageTypes.Count == 0 ? "No images." : Join(
                png > 0 ? $"Imports {Plural(png, "PNG image", "PNG images")} through referenced material slots as RGBA8 2D textures with one mip."
                    : null,
                otherImages.Count > 0 ? $"No reader for these images: {string.Join(", ", otherImages)}." : null,
                textureExtensions.Count > 0
                    ? $"No support for required extension {string.Join(", ", textureExtensions)}." : null));

        var materialIssues = OriginalAssetMaterials.Unsupported(document);
        Add("Materials", materials > 0, materialIssues.Count == 0, materials == 0
            ? "Uses the glTF default opaque material."
            : Join("Opaque base color, metallic, roughness and normal maps use a checked native shader.",
                materialIssues.Count == 0 ? null : "No encoder for " + string.Join(", ", materialIssues) + "."));

        bool skinned = counts.Weights > 0 || counts.SkinnedNodes > 0;
        Add("Skin weights", skinned, false, !skinned ? "None."
            : "Use Import skinned model for a compatible rig. Skipping imports static geometry.");

        Add("Skeleton", skins > 0, false, skins == 0 ? "None."
            : $"{Plural(skins, "skin", "skins")} with {Plural(joints, "joint", "joints")}. No native skeleton encoder.");

        Add("Animations", animations > 0, false, animations == 0 ? "None."
            : $"{Plural(animations, "animation", "animations")}. No native clip encoder.");

        Add("Morph targets", counts.Morphs > 0, false, counts.Morphs == 0 ? "None."
            : $"Found on {Plural(counts.Morphs, "primitive", "primitives")}. No encoder. When skipped, the base shape imports.");

        Add("Particles", particleSystems > 0, false, particleSystems switch
        {
            null => "No sidecar beside the scene.",
            0 => "The sidecar lists no particle systems.",
            int count => $"The sidecar lists {Plural(count, "particle system", "particle systems")}. "
                + "No native particle encoder."
        });
        return surfaces;
    }

    private sealed class Counts
    {
        public int Meshes, Triangles, Others, Uvs, ExtraUvs, Normals, Tangents, Colors, Weights, Morphs, SkinnedNodes;
    }

    /** Walks the default scene, as the geometry reader does, and counts what its meshes carry. */
    private static Counts Scan(JsonElement document)
    {
        var counts = new Counts();
        if (!document.TryGetProperty("scenes", out var scenes) || !document.TryGetProperty("nodes", out var nodes))
            return counts;
        int sceneIndex = document.TryGetProperty("scene", out var chosen) ? chosen.GetInt32() : 0;
        if (sceneIndex < 0 || sceneIndex >= scenes.GetArrayLength()
            || !scenes[sceneIndex].TryGetProperty("nodes", out var roots)) return counts;
        HashSet<int> visited = [], meshIndices = [];
        var pending = new Stack<int>(roots.EnumerateArray().Select(value => value.GetInt32()));
        while (pending.Count > 0)
        {
            int index = pending.Pop();
            if (index < 0 || index >= nodes.GetArrayLength() || !visited.Add(index)) continue;
            var node = nodes[index];
            if (node.TryGetProperty("skin", out _)) counts.SkinnedNodes++;
            if (node.TryGetProperty("mesh", out var mesh)) meshIndices.Add(mesh.GetInt32());
            if (node.TryGetProperty("children", out var children))
                foreach (var child in children.EnumerateArray()) pending.Push(child.GetInt32());
        }
        if (!document.TryGetProperty("meshes", out var meshes)) return counts;
        foreach (int index in meshIndices.Where(value => value >= 0 && value < meshes.GetArrayLength()))
        {
            counts.Meshes++;
            if (!meshes[index].TryGetProperty("primitives", out var primitives)) continue;
            foreach (var primitive in primitives.EnumerateArray())
            {
                bool triangles = !primitive.TryGetProperty("mode", out var mode) || mode.GetInt32() == 4;
                if (triangles) counts.Triangles++; else counts.Others++;
                if (primitive.TryGetProperty("targets", out _)) counts.Morphs++;
                if (!primitive.TryGetProperty("attributes", out var attributes)) continue;
                var names = attributes.EnumerateObject().Select(attribute => attribute.Name).ToList();
                if (names.Contains("TEXCOORD_0")) counts.Uvs++;
                if (names.Any(name => name.StartsWith("TEXCOORD_", StringComparison.Ordinal) && name != "TEXCOORD_0"))
                    counts.ExtraUvs++;
                if (names.Contains("NORMAL")) counts.Normals++;
                if (names.Contains("TANGENT")) counts.Tangents++;
                if (names.Any(name => name.StartsWith("COLOR_", StringComparison.Ordinal))) counts.Colors++;
                if (names.Any(name => name.StartsWith("JOINTS_", StringComparison.Ordinal)
                    || name.StartsWith("WEIGHTS_", StringComparison.Ordinal))) counts.Weights++;
            }
        }
        return counts;
    }

    /** The sidecar is optional; when present, its format, version and scene name must match. */
    private static int? ReadParticleSystemCount(string scene)
    {
        string path = Path.ChangeExtension(scene, null) + SidecarSuffix;
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > MaxSidecarBytes)
            throw new InvalidDataException("The original asset sidecar is too large.");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Text(root, "format") != SidecarFormat
            || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out int number) || number != 1)
            throw new InvalidDataException("Unsupported original asset sidecar.");
        if (Text(root, "scene") is { } named && !named.Equals(Path.GetFileName(scene), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The sidecar describes a different scene file.");
        return root.TryGetProperty("emitters", out var emitters) && emitters.ValueKind == JsonValueKind.Array
            ? emitters.GetArrayLength() : 0;
    }

    private static string ImageLabel(string type) => type switch
    {
        "image/jpeg" => "JPEG",
        "image/webp" => "WebP",
        "image/ktx" or "image/ktx2" => "KTX",
        "" => "unknown format",
        _ => type
    };

    private static int Length(JsonElement document, string name) =>
        document.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : 0;

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Plural(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";

    private static string Join(params string?[] parts) => string.Join(" ", parts.Where(part => !string.IsNullOrEmpty(part)));
}
