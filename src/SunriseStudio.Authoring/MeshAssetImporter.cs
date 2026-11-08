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

/** Static model headers stay as deltas; all authored geometry payloads contain user mesh data. */
public static class MeshAssetImporter
{
    // This shader reads XYZ positions and multiplies the normal-tangent cross product by tangent W.
    private const string StaticShader = "D67606C0E9766D10759890F5A32A555662887C25533D81A2951E2D4DEACD0D66";
    private const uint PreferredTemplate = 0x80B9EEE1;
    private const int EntryCount = 8;

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

    /** Each primitive retains a material assignment while sharing one authored geometry buffer group. */
    public static PreparedStaticModel PrepareParts(PackageCatalog catalog, IReadOnlyList<InterchangeMesh> meshes,
        IReadOnlyList<uint>? materialTags, string name, ushort packageId, int first,
        uint? templateTag = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (meshes.Count is < 1 or > short.MaxValue || materialTags is not null && materialTags.Count != meshes.Count)
            throw new InvalidDataException("Static primitives need one material assignment each.");
        foreach (var primitive in meshes) ValidateMesh(primitive);
        var mesh = Combine(meshes, name);
        ValidateMesh(mesh);
        if (!PackageWriter.IsNewPackageId(packageId) || catalog.ContainsInstalledPackage(packageId))
            throw new InvalidDataException($"Choose an unused package id from {PackageWriter.NewPackageIdRange}.");
        if (first < 0 || first > TagHashes.EntriesPerPackage - EntryCount) throw new InvalidDataException("The package has no room for a mesh.");
        cancellationToken.ThrowIfCancellationRequested();
        using var originals = PackageCatalog.Open(catalog.GameRoot);
        originals.ReadOverride = catalog.ReadOriginal;
        var template = FindTemplate(originals, templateTag, cancellationToken);
        uint Tag(int index) => TagHashes.Make(packageId, first + index);
        var encoded = EncodeGeometry(mesh, cancellationToken);
        byte[] model = (byte[])template.Model.Clone(), resource = (byte[])template.Resource.Clone();
        Member<SStaticMesh, uint>(model, nameof(SStaticMesh.MeshData)) = Tag(1);
        Member<SStaticMesh, Vector3>(model, nameof(SStaticMesh.PositionOffset)) = encoded.PositionOffset;
        Member<SStaticMesh, float>(model, nameof(SStaticMesh.PositionScale)) = encoded.PositionScale;
        Member<SStaticMesh, float>(model, nameof(SStaticMesh.UvScaleU)) = encoded.UvScale;
        Member<SStaticMesh, float>(model, nameof(SStaticMesh.UvScaleV)) = encoded.UvScale;
        Member<SStaticMesh, float>(model, nameof(SStaticMesh.UvOffsetU)) = encoded.UvOffset.X;
        Member<SStaticMesh, float>(model, nameof(SStaticMesh.UvOffsetV)) = encoded.UvOffset.Y;
        var material = new byte[checked(Unsafe.SizeOf<TagReferenceElement>() * meshes.Count)];
        var assignments = new byte[checked(Unsafe.SizeOf<SStaticMeshGroup>() * meshes.Count)];
        var parts = new byte[checked(Unsafe.SizeOf<SStaticMeshPart>() * meshes.Count)];
        var materialRows = MemoryMarshal.Cast<byte, TagReferenceElement>(material);
        var assignmentRows = MemoryMarshal.Cast<byte, SStaticMeshGroup>(assignments);
        var partRows = MemoryMarshal.Cast<byte, SStaticMeshPart>(parts);
        uint firstIndex = 0;
        for (int index = 0; index < meshes.Count; index++)
        {
            materialRows[index].Tag = materialTags?[index] ?? template.Mesh.MaterialTag;
            template.Assignment.CopyTo(MemoryMarshal.AsBytes(assignmentRows.Slice(index, 1)));
            assignmentRows[index].PartIndex = checked((ushort)index);
            template.Part.CopyTo(MemoryMarshal.AsBytes(partRows.Slice(index, 1)));
            ref var part = ref partRows[index];
            part.IndexStart = firstIndex; part.IndexCount = checked((uint)meshes[index].Indices.Length);
            part.BufferGroupIndex = 0; part.LodCategory = 0; part.PrimitiveType = 3;
            firstIndex = checked(firstIndex + (uint)meshes[index].Indices.Length);
        }
        model = AppendArray(model, TagView.FieldOffset<SStaticMesh>(nameof(SStaticMesh.Materials)),
            TagReferenceElement.Class, Unsafe.SizeOf<TagReferenceElement>(), material);
        resource = AppendArray(resource, TagView.FieldOffset<SStaticMeshData>(nameof(SStaticMeshData.StageAssignments)),
            SStaticMeshGroup.Class, Unsafe.SizeOf<SStaticMeshGroup>(), assignments);
        resource = AppendArray(resource, TagView.FieldOffset<SStaticMeshData>(nameof(SStaticMeshData.Parts)),
            SStaticMeshPart.Class, Unsafe.SizeOf<SStaticMeshPart>(), parts);
        var group = (byte[])template.Group.Clone();
        ref var buffers = ref TagView.Edit<SStaticBufferGroup>(group);
        buffers.IndexBuffer = Tag(6); buffers.PositionBuffer[0].Unk0 = Tag(2); buffers.PositionBuffer[1].Unk0 = Tag(4);
        buffers.Unknown0x0c = 0;
        resource = AppendArray(resource, TagView.FieldOffset<SStaticMeshData>(nameof(SStaticMeshData.Buffers)),
            SStaticBufferGroup.Class, Unsafe.SizeOf<SStaticBufferGroup>(), group);
        var positionsHeader = (byte[])template.PositionHeader.Clone();
        var attributesHeader = (byte[])template.AttributeHeader.Clone();
        var indexHeader = (byte[])template.IndexHeader.Clone();
        TagView.Edit<VertexBufferHeader>(positionsHeader).ByteCount = checked((uint)encoded.Positions.Length);
        TagView.Edit<VertexBufferHeader>(attributesHeader).ByteCount = checked((uint)encoded.Attributes.Length);
        Member<IndexBufferHeader, byte>(indexHeader, nameof(IndexBufferHeader.Wide)) = 1;
        Member<IndexBufferHeader, long>(indexHeader, nameof(IndexBufferHeader.ByteCount)) = encoded.Indices.LongLength;
        ProjectAssetAddition Derived(int index, string suffix, byte[] bytes, PackageEntry source,
            uint reference, bool named = false) => new(name + suffix, bytes,
                new(packageId, reference, source.TypeInfo, named ? name : null, first + index), source.TagHash);
        ProjectAssetAddition Owned(int index, int headerIndex, string suffix, byte[] bytes, PackageEntry source) =>
            new(name + suffix, bytes, new(packageId, Tag(headerIndex), source.TypeInfo, null, first + index));
        cancellationToken.ThrowIfCancellationRequested();
        ProjectAssetAddition[] additions = [
            Derived(0, ".model", model, template.ModelEntry, template.ModelEntry.Reference, true),
            Derived(1, ".mesh", resource, template.ResourceEntry, template.ResourceEntry.Reference),
            Derived(2, ".positions", positionsHeader, template.PositionEntry, Tag(3)),
            Owned(3, 2, ".positions.bin", encoded.Positions, template.PositionPayload),
            Derived(4, ".attributes", attributesHeader, template.AttributeEntry, Tag(5)),
            Owned(5, 4, ".attributes.bin", encoded.Attributes, template.AttributePayload),
            Derived(6, ".indices", indexHeader, template.IndexEntry, Tag(7)),
            Owned(7, 6, ".indices.bin", encoded.Indices, template.IndexPayload)
        ];
        return new(Tag(0), template.ModelEntry.TagHash, mesh.Positions.Length, mesh.Indices.Length / 3, additions);
    }

    private sealed record Template(PackageEntry ModelEntry, byte[] Model,
        PackageEntry ResourceEntry, byte[] Resource, MeshData Mesh,
        byte[] Assignment, byte[] Part, byte[] Group,
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
                    return new(candidate, model, resourceEntry, resource, mesh,
                        MemoryMarshal.AsBytes(assignments.Slice(assignment, 1)).ToArray(), MemoryMarshal.AsBytes(parts.Slice(mesh.PartIndex, 1)).ToArray(),
                        MemoryMarshal.AsBytes(groups.Slice(groupIndex, 1)).ToArray(), position.Entry, position.Header, position.Payload,
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
        ValidateMesh(mesh);
        var minimum = mesh.Positions.Aggregate(Vector3.Min);
        var maximum = mesh.Positions.Aggregate(Vector3.Max);
        var minUv = mesh.TexCoords.Aggregate(Vector2.Min);
        var maxUv = mesh.TexCoords.Aggregate(Vector2.Max);
        Vector3 offset = new(Mid(minimum.X, maximum.X), Mid(minimum.Y, maximum.Y), Mid(minimum.Z, maximum.Z));
        float scale = Math.Max(1e-6f, Math.Max(HalfRange(minimum.X, maximum.X),
            Math.Max(HalfRange(minimum.Y, maximum.Y), HalfRange(minimum.Z, maximum.Z))));
        Vector2 uvOffset = new(Mid(minUv.X, maxUv.X), Mid(minUv.Y, maxUv.Y));
        float uvScale = Math.Max(1e-6f, Math.Max(HalfRange(minUv.X, maxUv.X), HalfRange(minUv.Y, maxUv.Y)));
        var tangents = new Vector3[mesh.Positions.Length];
        var bitangents = new Vector3[mesh.Positions.Length];
        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            if ((i & 0x3FFF) == 0) token.ThrowIfCancellationRequested();
            uint a = mesh.Indices[i], b = mesh.Indices[i + 1], c = mesh.Indices[i + 2];
            Vector3 edge1 = mesh.Positions[b] - mesh.Positions[a], edge2 = mesh.Positions[c] - mesh.Positions[a];
            Vector2 uv1 = mesh.TexCoords[b] - mesh.TexCoords[a], uv2 = mesh.TexCoords[c] - mesh.TexCoords[a];
            float determinant = uv1.X * uv2.Y - uv1.Y * uv2.X;
            if (MathF.Abs(determinant) < 1e-12f) continue;
            Vector3 tangent = (edge1 * uv2.Y - edge2 * uv1.Y) / determinant;
            Vector3 bitangent = (edge2 * uv1.X - edge1 * uv2.X) / determinant;
            if (!Finite(tangent) || !Finite(bitangent)) throw new InvalidDataException("UV derivatives exceed finite range.");
            tangents[a] += tangent; tangents[b] += tangent; tangents[c] += tangent;
            bitangents[a] += bitangent; bitangents[b] += bitangent; bitangents[c] += bitangent;
        }
        var positions = new byte[checked(mesh.Positions.Length * Unsafe.SizeOf<PositionVertex>())];
        var attributes = new byte[checked(mesh.Positions.Length * Unsafe.SizeOf<AttributeVertex>())];
        var indices = new byte[checked(mesh.Indices.Length * sizeof(uint))];
        var positionRows = MemoryMarshal.Cast<byte, PositionVertex>(positions);
        var attributeRows = MemoryMarshal.Cast<byte, AttributeVertex>(attributes);
        for (int i = 0; i < mesh.Positions.Length; i++)
        {
            if ((i & 0xFFF) == 0) token.ThrowIfCancellationRequested();
            Vector3 position = (mesh.Positions[i] - offset) / scale;
            Vector2 uv = (mesh.TexCoords[i] - uvOffset) / uvScale;
            Vector3 normal = Normalize(mesh.Normals[i], Vector3.UnitZ);
            Vector3 tangent = tangents[i] - normal * Vector3.Dot(normal, tangents[i]);
            tangent = Normalize(tangent, Normalize(Vector3.Cross(MathF.Abs(normal.Z) < .9f ? Vector3.UnitZ : Vector3.UnitY, normal), Vector3.UnitX));
            float handedness = Vector3.Dot(Vector3.Cross(normal, tangent), bitangents[i]) < 0 ? -1 : 1;
            positionRows[i] = new() { X = Snorm(position.X), Y = Snorm(position.Y), Z = Snorm(position.Z) };
            attributeRows[i] = new()
            {
                U = Snorm(uv.X), V = Snorm(uv.Y),
                NormalX = Snorm(normal.X), NormalY = Snorm(normal.Y), NormalZ = Snorm(normal.Z),
                TangentX = Snorm(tangent.X), TangentY = Snorm(tangent.Y), TangentZ = Snorm(tangent.Z), Handedness = Snorm(handedness)
            };
        }
        mesh.Indices.CopyTo(MemoryMarshal.Cast<byte, uint>(indices));
        return new(positions, attributes, indices, offset, scale, uvOffset, uvScale);
    }

    /** Primitives keep their vertex and triangle order when joined into shared buffers. */
    private static InterchangeMesh Combine(IReadOnlyList<InterchangeMesh> meshes, string name)
    {
        List<Vector3> positions = [], normals = [];
        List<Vector2> uv = [];
        List<uint> indices = [];
        foreach (var mesh in meshes)
        {
            ValidateMesh(mesh);
            uint start = checked((uint)positions.Count);
            positions.AddRange(mesh.Positions); normals.AddRange(mesh.Normals); uv.AddRange(mesh.TexCoords);
            indices.AddRange(mesh.Indices.Select(x => checked(start + x)));
        }
        return new(name, positions.ToArray(), normals.ToArray(), uv.ToArray(), indices.ToArray());
    }

    private static void ValidateMesh(InterchangeMesh mesh)
    {
        if (mesh.Positions.Length is < 3 or > 4_000_000 || mesh.Indices.Length is < 3 or > 24_000_000
            || mesh.Indices.Length % 3 != 0 || mesh.Positions.Length != mesh.Normals.Length
            || mesh.Positions.Length != mesh.TexCoords.Length || mesh.Indices.Any(x => x >= mesh.Positions.Length)
            || mesh.Positions.Any(x => !Finite(x)) || mesh.Normals.Any(x => !Finite(x))
            || mesh.TexCoords.Any(x => !float.IsFinite(x.X) || !float.IsFinite(x.Y)))
            throw new InvalidDataException("Mesh attributes, topology or dimensions are invalid.");
    }

    /** Appended arrays leave all unknown original bytes available through the base delta. */
    private static byte[] AppendArray(byte[] source, int field, uint type, int stride, byte[] rows)
    {
        if (rows.Length % stride != 0 || field < 8 || field > source.Length - Unsafe.SizeOf<TagArray<Unknown>>())
            throw new InvalidDataException("Invalid native array replacement.");
        // The template's size word holds its length, so the relocator keeps it current.
        return TagRelocator.ReplaceArray(source, field, type, stride, rows, out _);
    }

    private static float Mid(float a, float b) => checked((float)(((double)a + b) * .5));
    private static float HalfRange(float a, float b) => checked((float)(((double)b - a) * .5));
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static Vector3 Normalize(Vector3 value, Vector3 fallback) => value.LengthSquared() > 1e-12f
        && float.IsFinite(value.LengthSquared()) ? Vector3.Normalize(value) : fallback;
    private static short Snorm(float value)
    {
        if (!float.IsFinite(value) || value is < -1.00001f or > 1.00001f)
            throw new InvalidDataException("Encoded mesh attribute is outside signed normalized range.");
        return (short)MathF.Round(Math.Clamp(value, -1, 1) * 32767);
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

    /** One position of declaration 123: snorm16 XYZ; W stays zero. */
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct PositionVertex
    {
        public short X, Y, Z, W;
    }

    /**
     * One attribute vertex of declaration 123: snorm16 UV, normal and tangent.
     * The tangent's W lane holds the handedness; the normal's W stays zero.
     */
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct AttributeVertex
    {
        public short U, V, NormalX, NormalY, NormalZ, NormalW, TangentX, TangentY, TangentZ, Handedness;
    }
}
