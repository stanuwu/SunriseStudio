using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

public sealed record MeshImportResult(AddedProjectAsset ModelAsset, uint ModelTag, uint TemplateTag,
    int VertexCount, int TriangleCount, IReadOnlyList<AddedProjectAsset> Assets);
public sealed record EncodedStaticGeometry(byte[] Positions, byte[] Attributes, byte[] Indices,
    Vector3 PositionOffset, float PositionScale, Vector2 UvOffset, float UvScale);
public sealed record PreparedStaticModel(uint ModelTag, uint TemplateTag, int VertexCount, int TriangleCount,
    IReadOnlyList<ProjectAssetAddition> Assets);

/** Part row byte +10 values: which LOD levels a part draws at. */
public static class StaticLodCategory
{
    /** Levels 0 to 2. */
    public const byte All = 2;
    /** Levels 0 and 1; the stock near half of a part pair. */
    public const byte Near = 1;
    /** Level 2 only; the stock far half of a part pair. */
    public const byte Far = 7;
    /** Level 0 only. */
    public const byte Level0 = 10;
    /** Level 1 only. */
    public const byte Level1 = 4;

    /** The categories of `count` LOD files, nearest first: one draws at every level, two split at level 2. */
    public static byte[] ForLevels(int count) => count switch
    {
        1 => [All],
        2 => [Near, Far],
        3 => [Level0, Level1, Far],
        _ => throw new ArgumentOutOfRangeException(nameof(count), "A static has one to three LOD levels.")
    };

    /**
     * Switch sizes that put the level switches at authored distances, at the default view scales.
     * @param level1 Metres where level 1 takes over from level 0; null keeps `radius`.
     * @param level2 Metres where level 2 takes over; null keeps `radius`.
     * @param cull Metres past which nothing draws; null keeps `radius`.
     */
    public static Vector3 Sizes(float radius, float? level1, float? level2, float? cull) => new(
        level1 / StaticLod.DefaultK0 ?? radius, level2 / StaticLod.DefaultK1 ?? radius, cull / StaticLod.DefaultK2 ?? radius);
}

/**
 * One authored part of a static model.
 * @param Material A technique made for the profile; 0 takes the template's opaque material.
 * @param ShadowMaterial The alpha-test shadow technique; other profiles use the shared caster.
 * @param LodCategory A StaticLodCategory value.
 */
public sealed record StaticPart(InterchangeMesh Mesh, uint Material = 0, StaticProfile Profile = StaticProfile.Opaque,
    uint ShadowMaterial = 0, byte LodCategory = StaticLodCategory.All);

/** Static model headers stay as deltas; all authored geometry payloads contain user mesh data. */
public static class MeshAssetImporter
{
    // This shader reads XYZ positions and multiplies the normal-tangent cross product by tangent W.
    private const string StaticShader = "D67606C0E9766D10759890F5A32A555662887C25533D81A2951E2D4DEACD0D66";
    private const uint PreferredTemplate = 0x80B9EEE1;
    // Depth prepass and shadow caster techniques every stock opaque part uses; position only, no pixel shader.
    private const uint DepthMaterial = 0x815B86EA;
    private const string DepthShader = "1D999BF3D64F29051458E7F5EE471C4EE5F671AD7DC6EA25A7AEA168C55F3C2F";
    private const uint CasterMaterial = 0x815B86E7;
    private const string CasterShader = "58D34302F942F570FC552CAB76AA1183B95052ED3EEF5BB54FD1F79205E28EEB";
    // Stage ids of the depth prepass, the shadow pass and the G-buffer pass.
    private const ushort DepthStage = 12, ShadowStage = 3, ColorStage = 0;
    // Position-only declaration of the depth and opaque shadow rows; the alpha-test shadow adds the UV.
    private const short PositionDeclaration = 120, AlphaShadowDeclaration = 121;
    // The model, its mesh data, then six buffer entries per group.
    private const int HeaderEntries = 2, GroupEntries = 6;
    // Triangle list.
    private const byte TriangleList = 3;
    // A group's first vertex in the model rounds up to this; all stock multi-group models follow it.
    private const int GroupVertexAlign = 4;

    public static MeshImportResult ImportFile(StudioProject project, PackageCatalog catalog,
        string path, string name, ushort packageId, uint? templateTag = null,
        CancellationToken cancellationToken = default)
    {
        int first = project.AddedAssets.Where(x => x.Native?.PackageId == packageId)
            .Select(x => x.Native!.EntryIndex ?? -1).DefaultIfEmpty(-1).Max() + 1;
        var prepared = PrepareFile(catalog, path, name, packageId, first, templateTag, cancellationToken);
        var additions = project.AddAssets(prepared.Assets, catalog);
        return new(additions[0], prepared.ModelTag, prepared.TemplateTag, prepared.VertexCount, prepared.TriangleCount, additions);
    }

    /** Preparation allocates native indices without publishing project files. */
    public static PreparedStaticModel PrepareFile(PackageCatalog catalog, string path, string name,
        ushort packageId, int firstIndex, uint? templateTag = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var meshes = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".obj" => ObjInterchange.Read(path),
            ".gltf" or ".glb" => GltfInterchange.Read(path),
            _ => throw new NotSupportedException("Native mesh import accepts OBJ, glTF and GLB.")
        };
        return PrepareParts(catalog, [Combine(meshes, name)], null, name, packageId,
            firstIndex, templateTag, cancellationToken);
    }

    /** One authored triangle-list part replaces the template's referenced arrays. */
    public static MeshImportResult ImportMesh(StudioProject project, PackageCatalog catalog,
        InterchangeMesh mesh, string name, ushort packageId, uint? templateTag = null,
        CancellationToken cancellationToken = default)
    {
        int first = project.AddedAssets.Where(x => x.Native?.PackageId == packageId)
            .Select(x => x.Native!.EntryIndex ?? -1).DefaultIfEmpty(-1).Max() + 1;
        var prepared = PrepareParts(catalog, [mesh], null, name, packageId, first, templateTag, cancellationToken);
        var additions = project.AddAssets(prepared.Assets, catalog);
        return new(additions[0], prepared.ModelTag, prepared.TemplateTag, prepared.VertexCount, prepared.TriangleCount, additions);
    }

    /** Each primitive becomes one opaque part with its own material, drawn at every LOD level. */
    public static PreparedStaticModel PrepareParts(PackageCatalog catalog, IReadOnlyList<InterchangeMesh> meshes,
        IReadOnlyList<uint>? materialTags, string name, ushort packageId, int first,
        uint? templateTag = null, CancellationToken cancellationToken = default)
    {
        if (materialTags is not null && materialTags.Count != meshes.Count)
            throw new InvalidDataException("Static primitives need one material assignment each.");
        return PrepareStatic(catalog, meshes.Select((mesh, i) => new StaticPart(mesh, materialTags?[i] ?? 0)).ToArray(),
            name, packageId, first, null, templateTag, cancellationToken);
    }

    /**
     * One static model from authored parts. Parts of one profile share a buffer group.
     * @param lodSizes The three switch sizes from the bounding sphere radius; null uses the radius for each, as stock models do.
     * @return The model and its entries; no project file is written.
     */
    public static PreparedStaticModel PrepareStatic(PackageCatalog catalog, IReadOnlyList<StaticPart> parts,
        string name, ushort packageId, int first, Func<float, Vector3>? lodSizes = null, uint? templateTag = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (parts.Count is < 1 or > short.MaxValue) throw new InvalidDataException("A static model needs 1 to 32767 parts.");
        foreach (var part in parts)
        {
            StaticVertexEncoder.Validate(part.Mesh);
            if (part.Profile == StaticProfile.AlphaTest && part.ShadowMaterial == 0)
                throw new InvalidDataException("An alpha-test part needs its shadow technique.");
            if (part.Material == 0 && part.Profile != StaticProfile.Opaque)
                throw new InvalidDataException("Only an opaque part can take the template material.");
        }
        var groups = parts.Select(part => part.Profile).Distinct().ToArray();
        int entries = HeaderEntries + GroupEntries * groups.Length;
        if (!PackageWriter.IsNewPackageId(packageId) || catalog.ContainsInstalledPackage(packageId))
            throw new InvalidDataException($"Choose an unused package id from {PackageWriter.NewPackageIdRange}.");
        if (first < 0 || first > TagHashes.EntriesPerPackage - entries) throw new InvalidDataException("The package has no room for a mesh.");
        cancellationToken.ThrowIfCancellationRequested();
        var template = CheckedTemplate(catalog, templateTag, cancellationToken);
        uint Tag(int index) => TagHashes.Make(packageId, first + index);
        var quantization = StaticQuantization.Of(parts.Select(part => part.Mesh));
        var (center, radius) = Sphere(parts);
        Vector3 sizes = lodSizes?.Invoke(radius) ?? new(radius);
        if (!float.IsFinite(sizes.X) || !float.IsFinite(sizes.Y) || !float.IsFinite(sizes.Z) || sizes.X <= 0 || sizes.Y <= 0 || sizes.Z <= 0)
            throw new InvalidDataException("LOD switch sizes must be finite and positive.");

        byte[] model = (byte[])template.Model.Clone(), resource = (byte[])template.Resource.Clone();
        Member<SStaticMesh, uint>(model, nameof(SStaticMesh.MeshData)) = Tag(1);
        Member<SStaticMesh, Vector3>(model, nameof(SStaticMesh.PositionOffset)) = quantization.PositionOffset;
        Member<SStaticMesh, float>(model, nameof(SStaticMesh.PositionScale)) = quantization.PositionScale;
        Member<SStaticMesh, float>(model, nameof(SStaticMesh.UvScaleU)) = quantization.UvScale;
        Member<SStaticMesh, float>(model, nameof(SStaticMesh.UvScaleV)) = quantization.UvScale;
        Member<SStaticMesh, float>(model, nameof(SStaticMesh.UvOffsetU)) = quantization.UvOffset.X;
        Member<SStaticMesh, float>(model, nameof(SStaticMesh.UvOffsetV)) = quantization.UvOffset.Y;
        Member<SStaticMesh, float>(model, nameof(SStaticMesh.LodSwitchSize0)) = sizes.X;
        Member<SStaticMesh, float>(model, nameof(SStaticMesh.LodSwitchSize1)) = sizes.Y;
        Member<SStaticMesh, float>(model, nameof(SStaticMesh.LodSwitchSize2)) = sizes.Z;
        Member<SStaticMesh, Vector3>(model, nameof(SStaticMesh.BoundingSphereCenter)) = center;
        Member<SStaticMesh, float>(model, nameof(SStaticMesh.BoundingSphereRadius)) = radius;

        var partRows = new SStaticMeshPart[parts.Count];
        var rows = new List<(SStaticMeshGroup Row, uint Material)>();
        var groupRows = new SStaticBufferGroup[groups.Length];
        var buffers = new List<ProjectAssetAddition>();
        int vertexStart = 0, totalVertices = 0, totalTriangles = 0;
        for (int g = 0; g < groups.Length; g++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profile = groups[g];
            var (positionStride, attributeStride) = StaticVertexEncoder.Strides(profile);
            using MemoryStream positions = new(), attributes = new();
            List<uint> indices = [];
            int groupVertices = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i].Profile != profile) continue;
                var mesh = parts[i].Mesh;
                var (p, a) = StaticVertexEncoder.Encode(mesh, profile, quantization, cancellationToken);
                partRows[i] = template.Part;
                partRows[i].IndexStart = checked((uint)indices.Count);
                partRows[i].IndexCount = checked((uint)mesh.Indices.Length);
                partRows[i].BufferGroupIndex = checked((ushort)g);
                partRows[i].LodCategory = parts[i].LodCategory;
                partRows[i].PrimitiveType = TriangleList;
                positions.Write(p); attributes.Write(a);
                foreach (uint index in mesh.Indices) indices.Add(checked((uint)groupVertices + index));
                groupVertices = checked(groupVertices + mesh.Positions.Length);
                totalTriangles += mesh.Indices.Length / 3;
            }
            int @base = 2 + GroupEntries * g;
            groupRows[g] = template.Group;
            groupRows[g].IndexBuffer = Tag(@base + 4);
            groupRows[g].PositionBuffer[0].Unk0 = Tag(@base);
            groupRows[g].PositionBuffer[1].Unk0 = Tag(@base + 2);
            groupRows[g].Unknown0x0c = checked((uint)vertexStart);
            vertexStart = checked((vertexStart + groupVertices + GroupVertexAlign - 1) / GroupVertexAlign * GroupVertexAlign);
            totalVertices += groupVertices;
            buffers.AddRange(Buffers(template, name, packageId, first, @base, g,
                positions.ToArray(), positionStride, attributes.ToArray(), attributeStride, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(indices)).ToArray()));
        }
        for (int i = 0; i < parts.Count; i++)
        {
            var color = template.Assignment;
            color.PartIndex = checked((ushort)i); color.RenderStage = ColorStage;
            color.InputLayout = StaticVertexEncoder.Declaration(parts[i].Profile);
            rows.Add((color, parts[i].Material != 0 ? parts[i].Material : template.Mesh.MaterialTag));
            var shadow = color with { RenderStage = ShadowStage };
            if (parts[i].Profile == StaticProfile.AlphaTest)
            {
                shadow.InputLayout = AlphaShadowDeclaration;
                rows.Add((shadow, parts[i].ShadowMaterial));
                continue;
            }
            shadow.InputLayout = PositionDeclaration;
            rows.Add((shadow, CasterMaterial));
            rows.Add((shadow with { RenderStage = DepthStage }, DepthMaterial));
        }
        // Stock rows run from the highest stage down, and within a stage from the last part down.
        var ordered = rows.OrderByDescending(x => x.Row.RenderStage).ThenByDescending(x => x.Row.PartIndex).ToArray();
        model = TagRows.Replace(model, TagView.FieldOffset<SStaticMesh>(nameof(SStaticMesh.Materials)), TagReferenceElement.Class,
            ordered.Select(x => { var row = default(TagReferenceElement); row.Tag = x.Material; return row; }).ToArray());
        resource = TagRows.Replace(resource, TagView.FieldOffset<SStaticMeshData>(nameof(SStaticMeshData.StageAssignments)),
            SStaticMeshGroup.Class, ordered.Select(x => x.Row).ToArray());
        resource = TagRows.Replace(resource, TagView.FieldOffset<SStaticMeshData>(nameof(SStaticMeshData.Parts)), SStaticMeshPart.Class, partRows);
        resource = TagRows.Replace(resource, TagView.FieldOffset<SStaticMeshData>(nameof(SStaticMeshData.Buffers)), SStaticBufferGroup.Class, groupRows);
        cancellationToken.ThrowIfCancellationRequested();
        ProjectAssetAddition[] additions = [
            new(name + ".model", model, new(packageId, template.ModelEntry.Reference, template.ModelEntry.TypeInfo, name, first), template.ModelEntry.TagHash),
            new(name + ".mesh", resource, new(packageId, template.ResourceEntry.Reference, template.ResourceEntry.TypeInfo, null, first + 1),
                template.ResourceEntry.TagHash),
            .. buffers
        ];
        return new(Tag(0), template.ModelEntry.TagHash, totalVertices, totalTriangles, additions);
    }

    /** The six buffer entries of group `g`: position, attribute and index headers, each followed by its payload. */
    private static ProjectAssetAddition[] Buffers(Template template, string name, ushort packageId, int first, int @base, int g,
        byte[] positions, int positionStride, byte[] attributes, int attributeStride, byte[] indices)
    {
        uint Tag(int index) => TagHashes.Make(packageId, first + index);
        string suffix = g == 0 ? "" : $".{g}";
        var positionsHeader = (byte[])template.PositionHeader.Clone();
        var attributesHeader = (byte[])template.AttributeHeader.Clone();
        var indexHeader = (byte[])template.IndexHeader.Clone();
        ref var positionRow = ref TagView.Edit<VertexBufferHeader>(positionsHeader);
        positionRow.ByteCount = checked((uint)positions.Length); positionRow.Stride = checked((ushort)positionStride);
        ref var attributeRow = ref TagView.Edit<VertexBufferHeader>(attributesHeader);
        attributeRow.ByteCount = checked((uint)attributes.Length); attributeRow.Stride = checked((ushort)attributeStride);
        Member<IndexBufferHeader, byte>(indexHeader, nameof(IndexBufferHeader.Wide)) = 1;
        Member<IndexBufferHeader, long>(indexHeader, nameof(IndexBufferHeader.ByteCount)) = indices.LongLength;
        ProjectAssetAddition Header(int index, string kind, byte[] bytes, PackageEntry source) => new(name + suffix + kind, bytes,
            new(packageId, Tag(index + 1), source.TypeInfo, null, first + index), source.TagHash);
        ProjectAssetAddition Payload(int index, string kind, byte[] bytes, PackageEntry source) => new(name + suffix + kind + ".bin", bytes,
            new(packageId, Tag(index - 1), source.TypeInfo, null, first + index));
        return [
            Header(@base, ".positions", positionsHeader, template.PositionEntry),
            Payload(@base + 1, ".positions", positions, template.PositionPayload),
            Header(@base + 2, ".attributes", attributesHeader, template.AttributeEntry),
            Payload(@base + 3, ".attributes", attributes, template.AttributePayload),
            Header(@base + 4, ".indices", indexHeader, template.IndexEntry),
            Payload(@base + 5, ".indices", indices, template.IndexPayload)
        ];
    }

    /** The centre of the parts' bounds and the largest vertex distance from it. */
    private static (Vector3 Center, float Radius) Sphere(IReadOnlyList<StaticPart> parts)
    {
        Vector3 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
        foreach (var p in parts.SelectMany(part => part.Mesh.Positions)) { minimum = Vector3.Min(minimum, p); maximum = Vector3.Max(maximum, p); }
        var center = (minimum + maximum) / 2;
        float radius = parts.SelectMany(part => part.Mesh.Positions).Max(p => Vector3.Distance(p, center));
        return (center, Math.Max(radius, 1e-3f));
    }

    // Checked templates by catalog; installed data does not change while a catalog is open.
    private static readonly ConditionalWeakTable<PackageCatalog, Dictionary<uint, Template>> Templates = new();

    /** The static template and the shared depth and caster techniques, checked once per catalog and template choice. */
    private static Template CheckedTemplate(PackageCatalog catalog, uint? templateTag, CancellationToken token)
    {
        var cache = Templates.GetOrCreateValue(catalog);
        uint key = templateTag ?? 0;
        lock (cache)
            if (cache.TryGetValue(key, out var cached)) return cached;
        using var originals = PackageCatalog.Open(catalog.GameRoot);
        originals.ReadOverride = catalog.ReadOriginal;
        var template = FindTemplate(originals, templateTag, token);
        RequireShader(originals, DepthMaterial, DepthShader);
        RequireShader(originals, CasterMaterial, CasterShader);
        lock (cache) cache[key] = template;
        return template;
    }

    private static void RequireShader(PackageCatalog catalog, uint material, string vertexShader)
    {
        if (!catalog.TryGetEntry(material, out var entry) || entry.Reference != STechnique.Class)
            throw new NotSupportedException($"The shared static technique {material:X8} is not installed.");
        uint shader = TagView.Checked<STechnique>(catalog.Read(material), 0).VertexShader.Shader;
        if (!catalog.TryGetEntry(shader, out var header)
            || Convert.ToHexString(SHA256.HashData(catalog.Read(header.Reference))) != vertexShader)
            throw new NotSupportedException($"The shared static technique {material:X8} differs from the checked one.");
    }

    private sealed record Template(PackageEntry ModelEntry, byte[] Model,
        PackageEntry ResourceEntry, byte[] Resource, MeshData Mesh,
        SStaticMeshGroup Assignment, SStaticMeshPart Part, SStaticBufferGroup Group,
        PackageEntry PositionEntry, byte[] PositionHeader, PackageEntry PositionPayload,
        PackageEntry AttributeEntry, byte[] AttributeHeader, PackageEntry AttributePayload,
        PackageEntry IndexEntry, byte[] IndexHeader, PackageEntry IndexPayload);

    /** A verified declaration and exact shader profile gate the supported attribute encoding. */
    private static Template FindTemplate(PackageCatalog catalog, uint? explicitTag, CancellationToken token)
    {
        IEnumerable<PackageEntry> candidates = explicitTag is uint tag
            ? catalog.TryGetEntry(tag, out var found) ? [found] : []
            : catalog.Entries.Where(x => x.Reference == SStaticMesh.Class)
                .OrderBy(x => x.TagHash == PreferredTemplate ? 0 : 1).ThenBy(x => x.TagHash);
        var loader = new AssetSceneLoader(catalog);
        Dictionary<uint, bool> shaders = [];
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (candidate.Reference != SStaticMesh.Class || candidate.Type is not (8 or 16)) continue;
            SceneDocument scene;
            try { scene = loader.LoadMesh(candidate.TagHash, token); }
            catch (Exception error) when (explicitTag is null && error is InvalidDataException
                or NotSupportedException or ArgumentException or OverflowException or IndexOutOfRangeException)
            { continue; }
            foreach (var mesh in scene.Instances.Select(x => x.Mesh))
            {
                var native = mesh.Native;
                if (native is null || !SupportedLayout(native) || mesh.LodLevel != 0) continue;
                if (!shaders.TryGetValue(native.VertexShaderDataTag, out bool supportedShader))
                {
                    supportedShader = native.VertexShaderDataTag != 0
                        && Convert.ToHexString(SHA256.HashData(catalog.Read(native.VertexShaderDataTag))) == StaticShader;
                    shaders[native.VertexShaderDataTag] = supportedShader;
                }
                if (!supportedShader) continue;
                try
                {
                    byte[] model = catalog.Read(candidate.TagHash);
                    // The model must reach UvOffsetV; the members after it are not read.
                    if (!TagView.FitsThrough<SStaticMesh>(model, 0, nameof(SStaticMesh.UvOffsetV))
                        || Member<SStaticMesh, long>(model, nameof(SStaticMesh.TagByteLength)) != model.LongLength)
                        throw new InvalidDataException("Invalid static model header.");
                    var resourceEntry = Require(catalog, Member<SStaticMesh, uint>(model, nameof(SStaticMesh.MeshData)), SStaticMeshData.Class);
                    byte[] resource = catalog.Read(resourceEntry.TagHash);
                    if (!TagView.Fits<SStaticMeshData>(resource, 0)
                        || TagView.At<SStaticMeshData>(resource).TagByteLength != (ulong)resource.LongLength)
                        throw new InvalidDataException("Invalid static resource header.");
                    ref readonly var data = ref TagView.At<SStaticMeshData>(resource);
                    var materials = Rows(model, in Member<SStaticMesh, TagArray<TagReferenceElement>>(model, nameof(SStaticMesh.Materials)),
                        TagReferenceElement.Class);
                    var assignments = Rows(resource, in data.StageAssignments, SStaticMeshGroup.Class);
                    var parts = Rows(resource, in data.Parts, SStaticMeshPart.Class);
                    var groups = Rows(resource, in data.Buffers, SStaticBufferGroup.Class);
                    if (mesh.PartIndex < 0 || mesh.PartIndex >= parts.Length) continue;
                    ref readonly var part = ref parts[mesh.PartIndex];
                    int groupIndex = part.BufferGroupIndex;
                    if (groupIndex >= groups.Length) throw new InvalidDataException("Invalid buffer group index.");
                    ref readonly var group = ref groups[groupIndex];
                    int assignment = -1;
                    for (int i = 0; i < Math.Min(materials.Length, assignments.Length); i++)
                        if (assignments[i].PartIndex == mesh.PartIndex && assignments[i].RenderStage == native.RenderStage
                            && materials[i].Tag == mesh.MaterialTag) { assignment = i; break; }
                    if (assignment < 0) throw new InvalidOperationException("No assignment matches the part, stage and material.");
                    var position = Buffer(catalog, group.PositionBuffer[0].Unk0, 4, 8);
                    var attribute = Buffer(catalog, group.PositionBuffer[1].Unk0, 4, 20);
                    var index = Buffer(catalog, group.IndexBuffer, 6, 0);
                    return new(candidate, model, resourceEntry, resource, mesh, assignments[assignment], part, group,
                        position.Entry, position.Header, position.Payload,
                        attribute.Entry, attribute.Header, attribute.Payload, index.Entry, index.Header, index.Payload);
                }
                catch (Exception error) when (explicitTag is null && error is InvalidDataException or InvalidOperationException)
                { }
            }
        }
        throw new NotSupportedException("No supported static template with declaration 123 and the checked shader was found.");
    }

    private static bool SupportedLayout(NativeMeshData native) =>
        native.Kind == NativeMeshKind.Static && native.InputLayoutVerified
        && native.NativeDeclarationId == 123 && native.RenderStage == 0
        && native.VertexStreams.Length == 2 && native.VertexStreams[0].Stride == 8
        && native.VertexStreams[1].Stride == 20 && native.VertexStreams.All(x => x.Type == 0)
        && native.InputElements.SequenceEqual(new NativeInputElement[]
        {
            new("POSITION", 0, 0, 0, NativeVertexFormat.R16G16B16A16SNorm),
            new("TEXCOORD", 0, 1, 0, NativeVertexFormat.R16G16SNorm),
            new("NORMAL", 0, 1, 4, NativeVertexFormat.R16G16B16A16SNorm),
            new("TANGENT", 0, 1, 12, NativeVertexFormat.R16G16B16A16SNorm)
        });

    private static PackageEntry Require(PackageCatalog catalog, uint tag, uint reference)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != reference)
            throw new InvalidDataException("Static dependency has an unexpected class.");
        return entry;
    }

    /** Buffer headers retain native type words while their paired payload lengths change. */
    private static (PackageEntry Entry, byte[] Header, PackageEntry Payload) Buffer(
        PackageCatalog catalog, uint tag, int subtype, int stride)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Type != 32 || entry.SubType != subtype
            || !catalog.TryGetEntry(entry.Reference, out var payload) || payload.Type != 40 || payload.SubType != subtype)
            throw new InvalidDataException("Static buffer header and payload types disagree.");
        byte[] header = catalog.Read(tag);
        if (subtype == 4 && (!TagView.Fits<VertexBufferHeader>(header, 0) || TagView.At<VertexBufferHeader>(header).ByteCount != payload.Size
            || TagView.At<VertexBufferHeader>(header).Stride != stride || TagView.At<VertexBufferHeader>(header).End != VertexBufferHeader.EndValue)
            || subtype == 6 && (!TagView.FitsThrough<IndexBufferHeader>(header, 0, nameof(IndexBufferHeader.ByteCount))
                || Member<IndexBufferHeader, long>(header, nameof(IndexBufferHeader.ByteCount)) != payload.Size))
            throw new InvalidDataException("Static buffer header has an unsupported layout.");
        return (entry, header, payload);
    }

    /** Uniform position and UV transforms keep every encoded lane inside signed normalized range. */
    public static EncodedStaticGeometry EncodeGeometry(InterchangeMesh mesh,
        CancellationToken token = default)
    {
        StaticVertexEncoder.Validate(mesh);
        var quantization = StaticQuantization.Of([mesh]);
        var (positions, attributes) = StaticVertexEncoder.Encode(mesh, StaticProfile.Opaque, quantization, token);
        var indices = new byte[checked(mesh.Indices.Length * sizeof(uint))];
        mesh.Indices.CopyTo(MemoryMarshal.Cast<byte, uint>(indices));
        return new(positions, attributes, indices, quantization.PositionOffset, quantization.PositionScale,
            quantization.UvOffset, quantization.UvScale);
    }

    /** Primitives keep their vertex and triangle order when joined into shared buffers. */
    private static InterchangeMesh Combine(IReadOnlyList<InterchangeMesh> meshes, string name)
    {
        List<Vector3> positions = [], normals = [];
        List<Vector2> uv = [];
        List<uint> indices = [];
        foreach (var mesh in meshes)
        {
            StaticVertexEncoder.Validate(mesh);
            uint start = checked((uint)positions.Count);
            positions.AddRange(mesh.Positions); normals.AddRange(mesh.Normals); uv.AddRange(mesh.TexCoords);
            indices.AddRange(mesh.Indices.Select(x => checked(start + x)));
        }
        return new(name, positions.ToArray(), normals.ToArray(), uv.ToArray(), indices.ToArray());
    }

    /** The member of a T at offset 0; only that member must fit, not the whole T. */
    private static ref TField Member<T, TField>(byte[] bytes, string member) where T : unmanaged where TField : unmanaged =>
        ref TagView.Edit<TField>(bytes, TagView.FieldOffset<T>(member));

    /**
     * Rows of an array field. No rows and no header reads as empty; otherwise it needs at most
     * MaximumReaderRows rows and a well-formed header of `elementClass`, with its rows inside the tag.
     */
    private static ReadOnlySpan<TRow> Rows<TRow>(byte[] bytes, ref readonly TagArray<TRow> field, uint elementClass)
        where TRow : unmanaged
    {
        if (field.Count == 0 && field.Relative == 0) return [];
        // Only a long overflow throws; a header past the int range fails the layout checks.
        long header = checked(TagView.OffsetOf(bytes, in field.Relative) + field.Relative);
        var array = new TagArray(bytes, TagView.OffsetOf(bytes, in field));
        if (field.Count is < 0 or > TagArray.MaximumReaderRows || header < TagView.ClassWordSize || header > bytes.Length - TagArray.HeaderSize
            || !array.IsWellFormed || array.ElementClass != elementClass
            || field.Count * Unsafe.SizeOf<TRow>() > bytes.Length - header - TagArray.HeaderSize)
            throw new InvalidDataException("Invalid typed native array.");
        return MemoryMarshal.Cast<byte, TRow>(bytes.AsSpan(array.DataOffset, (int)field.Count * Unsafe.SizeOf<TRow>()));
    }
}
