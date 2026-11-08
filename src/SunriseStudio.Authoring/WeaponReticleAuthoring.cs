using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/** A holographic reticle part found on a template weapon's art. */
public sealed record WeaponReticleTemplate(int Selector, uint EntityTag, uint ModelTag, int PartOffset, uint MaterialTag);

public sealed record PreparedWeaponReticle(uint MaterialTag, IReadOnlyList<ProjectAssetAddition> Assets,
    IReadOnlyDictionary<string, byte[]> AddedChanges);

/**
 * A holo reticle is a second mesh row in the sight model: a quad at the origin drawn in render stage 16
 * with declaration 137. Its shader places it in front of the camera, so its weapon position is unused.
 */
public static class WeaponReticleAuthoring
{
    private const int ReticleDeclaration = 137, ReticleStage = 16;
    private static readonly int MeshStride = Unsafe.SizeOf<SEntityModelMesh>(), PartStride = Unsafe.SizeOf<SEntityModelPart>();
    private static readonly int MeshesAt = TagView.FieldOffset<SEntityModel>(nameof(SEntityModel.Meshes));
    private static readonly int PartsAt = TagView.FieldOffset<SEntityModelMesh>(nameof(SEntityModelMesh.Parts));
    // Primitive type 3 is a triangle list.
    private const ushort TriangleList = 3;

    /** The first art part of the template weapon whose LOD-0 geometry holds a stage-16 reticle. */
    public static WeaponReticleTemplate? FindTemplate(PackageCatalog catalog, IReadOnlyList<WeaponArtVariant> variants)
    {
        foreach (var variant in variants)
        {
            var reticle = new AssetSceneLoader(catalog) { IncludeLowerLods = false }.Load(variant.EntityTag).Instances
                .FirstOrDefault(instance => instance.Mesh.LodLevel == 0 && instance.Mesh.Native is
                    { NativeDeclarationId: ReticleDeclaration, RenderStage: ReticleStage });
            if (reticle is not null)
                return new(variant.Selector, variant.EntityTag, reticle.Mesh.TagHash, reticle.Mesh.PartIndex, reticle.Mesh.MaterialTag);
        }
        return null;
    }

    /**
     * Adds a reticle to the weapon's sight model, cloning the template's material.
     * @param image Optional reticle texture; null keeps the template's texture.
     */
    public static PreparedWeaponReticle Prepare(StudioProject project, PackageCatalog catalog, uint weaponTag,
        RgbaImage? image, ushort packageId, int firstEntry, IReadOnlyList<ProjectAssetAddition>? plannedAssets = null)
    {
        var profile = WeaponAssetReimporter.ReadProfile(project, catalog, weaponTag);
        var templateVariants = WeaponArchetypeAuthoring.Read(catalog, catalog.ReadOriginal(profile.BaseWeaponTag)).Variants;
        var template = FindTemplate(catalog, templateVariants)
            ?? throw new NotSupportedException("The base weapon's art has no holographic reticle to copy.");
        uint entity = profile.VariantEntities.TryGetValue(template.Selector, out uint owned) ? owned
            : throw new InvalidDataException("The weapon has no art part for the template's sight.");
        uint modelTag = new AssetSceneLoader(catalog).Load(entity).Instances.First().Mesh.TagHash;
        var model = Owned(project, modelTag) ?? throw new InvalidDataException("The sight model is not project-owned.");
        byte[] modelBytes = catalog.Read(modelTag);
        if (MeshRows(modelBytes).Length != 1)
            throw new InvalidDataException("The sight model already has a reticle or an unsupported mesh layout.");

        uint Tag(int index) => TagHashes.Make(packageId, firstEntry + index);
        int entries = image is null ? 7 : 9;
        if (firstEntry < 0 || firstEntry > TagHashes.EntriesPerPackage - entries || Enumerable.Range(0, entries).Any(index => catalog.ContainsTag(Tag(index))
            || plannedAssets?.Any(asset => asset.Native is { } native && NativeTag(native) == Tag(index)) == true))
            throw new InvalidDataException("The reticle needs free native entries.");

        // Template assets must be stock; FindTemplate read the project's copy.
        byte[] templateModel = catalog.ReadOriginal(template.ModelTag);
        if (!catalog.Read(template.ModelTag).AsSpan().SequenceEqual(templateModel))
            throw new NotSupportedException("The base weapon's sight model is edited in this project.");
        int templateRow = TemplateRowOffset(templateModel, template);
        var quad = ReadTemplateQuad(catalog, templateModel, templateRow, template);
        var assets = new List<ProjectAssetAddition>();
        byte[] material = catalog.ReadOriginal(template.MaterialTag);
        if (image is not null)
        {
            uint templateTexture = FindTexture(catalog, material)
                ?? throw new NotSupportedException("The template reticle material has no texture to replace.");
            var texture = TextureAssetImporter.PrepareRgba(catalog, image, "reticle", packageId, firstEntry + 7, templateTexture);
            material = Replace32(material, templateTexture, texture.HeaderTag);
            assets.AddRange(texture.Assets);
        }
        var materialEntry = Require(catalog, template.MaterialTag);
        assets.Insert(0, new("reticle.material", material,
            new(packageId, materialEntry.Reference, materialEntry.TypeInfo, null, firstEntry), template.MaterialTag));
        var buffers = new ReticleBuffers(Tag(1), Tag(3), Tag(5));
        var row = TemplateRow(templateModel, templateRow, template, buffers, Tag(0));

        ref readonly var mainRow = ref Mesh(modelBytes, MeshRows(modelBytes)[0]);
        uint positionsTag = Payload(catalog, PositionHeader(in mainRow)), attributesTag = Payload(catalog, AttributeHeader(in mainRow));
        var attached = Attach(modelBytes, catalog.Read(positionsTag), catalog.Read(attributesTag), quad, row);
        ref readonly var templateMesh = ref Mesh(templateModel, templateRow);
        AddBuffer(PositionHeader(in templateMesh), attached.ReticlePositions, 1, "reticle.positions", false);
        AddBuffer(AttributeHeader(in templateMesh), attached.ReticleAttributes, 3, "reticle.attributes", false);
        AddBuffer(templateMesh.IndexBuffer, attached.ReticleIndices, 5, "reticle.indices", true);

        var changes = new Dictionary<string, byte[]> { [model.Id] = attached.Model };
        if (attached.Positions is { } positions) changes[Owned(project, positionsTag)!.Id] = positions;
        if (attached.Attributes is { } attributes) changes[Owned(project, attributesTag)!.Id] = attributes;
        return new(Tag(0), assets.OrderBy(asset => asset.Native!.EntryIndex).ToArray(), changes);

        void AddBuffer(uint headerTag, byte[] data, int index, string name, bool indexBuffer)
        {
            var headerEntry = Require(catalog, headerTag);
            var payloadEntry = Require(catalog, headerEntry.Reference);
            byte[] header = catalog.ReadOriginal(headerTag);
            if (indexBuffer)
            {
                var indexView = Head<IndexBufferHeader>(header, nameof(IndexBufferHeader.ByteCount));
                indexView.Wide = 1;
                indexView.ByteCount = (ulong)data.Length;
                PutHead(header, in indexView);
            }
            else
            {
                var vertexView = Head<VertexBufferHeader>(header, nameof(VertexBufferHeader.ByteCount));
                vertexView.ByteCount = checked((uint)data.Length);
                PutHead(header, in vertexView);
            }
            assets.Insert(index, new(name, header, new(packageId, Tag(index + 1), headerEntry.TypeInfo, null, firstEntry + index), headerTag));
            assets.Insert(index + 1, new(name + ".bin", data, new(packageId, Tag(index), payloadEntry.TypeInfo, null, firstEntry + index + 1)));
        }
    }

    /**
     * Re-adds a sight model's reticle row to a freshly imported model.
     * @return The new bytes for the model, its main buffers if re-encoded, and the reticle buffers.
     */
    public static AttachedReticle Reattach(PackageCatalog catalog, byte[] currentModel, byte[] newModel,
        byte[] newPositions, byte[] newAttributes)
    {
        var rows = MeshRows(currentModel);
        if (rows.Length != 2 || ThirdHeader(in Mesh(currentModel, rows[1])) != uint.MaxValue)
            throw new InvalidDataException("The current sight model has no single reticle row.");
        var parts = PartRows(currentModel, rows[1]);
        if (parts.Length != 1) throw new InvalidDataException("The reticle row needs exactly one part.");
        var row = new ReticleRow(currentModel.AsSpan(rows[1], MeshStride).ToArray(), currentModel.AsSpan(parts[0], PartStride).ToArray());
        ref readonly var reticle = ref Mesh(currentModel, rows[1]);
        byte[] positions = catalog.Read(Payload(catalog, PositionHeader(in reticle)));
        byte[] attributes = catalog.Read(Payload(catalog, AttributeHeader(in reticle)));
        byte[] indices = catalog.Read(Payload(catalog, reticle.IndexBuffer));
        var encoding = Encoding.Read(currentModel);
        var quad = new ReticleQuad(Enumerable.Range(0, positions.Length / VertexStreamCodec.PositionStride)
                .Select(vertex => Snap(encoding.Position(positions, vertex * VertexStreamCodec.PositionStride))).ToArray(),
            Enumerable.Range(0, attributes.Length / VertexStreamCodec.AttributeStride)
                .Select(vertex => Snap(encoding.Uv(attributes, vertex * VertexStreamCodec.AttributeStride))).ToArray(),
            Enumerable.Range(0, attributes.Length / VertexStreamCodec.AttributeStride)
                .Select(vertex => VertexStreamCodec.ReadFrame(attributes, vertex * VertexStreamCodec.AttributeStride)).ToArray(),
            Enumerable.Range(0, indices.Length / 4).Select(index => BinaryPrimitives.ReadUInt32LittleEndian(indices.AsSpan(index * 4))).ToArray());
        return Attach(newModel, newPositions, newAttributes, quad, row);
    }

    /** A sight model with an attached reticle holds its imported row plus one reticle row. */
    public static bool HasReticle(byte[] model) =>
        MeshRows(model) is { Length: 2 } rows && ThirdHeader(in Mesh(model, rows[1])) == uint.MaxValue;

    /** The payload tags of the reticle row's position, attribute and index buffers. */
    public static IReadOnlyList<uint> ReticlePayloads(PackageCatalog catalog, byte[] model)
    {
        ref readonly var row = ref Mesh(model, MeshRows(model)[1]);
        return [Payload(catalog, PositionHeader(in row)), Payload(catalog, AttributeHeader(in row)), Payload(catalog, row.IndexBuffer)];
    }

    /** The header tags of the imported row's position and attribute buffers. */
    public static (uint Positions, uint Attributes) MainBuffers(byte[] model)
    {
        ref readonly var row = ref Mesh(model, MeshRows(model)[0]);
        return (PositionHeader(in row), AttributeHeader(in row));
    }

    public sealed record AttachedReticle(byte[] Model, byte[]? Positions, byte[]? Attributes,
        byte[] ReticlePositions, byte[] ReticleAttributes, byte[] ReticleIndices);
    private sealed record ReticleQuad(Vector3[] Positions, Vector2[] Uvs, byte[][] Frames, uint[] Indices);
    private sealed record ReticleRow(byte[] Mesh, byte[] Part);
    private sealed record ReticleBuffers(uint Positions, uint Attributes, uint Indices);

    /** Appends the reticle row; widens the shared position and UV encoding only when the quad falls outside it. */
    private static AttachedReticle Attach(byte[] model, byte[] positions, byte[] attributes, ReticleQuad quad, ReticleRow row)
    {
        var old = Encoding.Read(model);
        int count = positions.Length / VertexStreamCodec.PositionStride;
        var points = Enumerable.Range(0, count).Select(vertex => old.Position(positions, vertex * VertexStreamCodec.PositionStride)).ToArray();
        var uvs = Enumerable.Range(0, count).Select(vertex => old.Uv(attributes, vertex * VertexStreamCodec.WeaponAttributeStride)).ToArray();
        bool fits = quad.Positions.All(point => old.Fits(point)) && quad.Uvs.All(uv => old.FitsUv(uv));
        var encoding = fits ? old : Encoding.Covering(points.Concat(quad.Positions), uvs.Concat(quad.Uvs));
        byte[]? newPositions = null, newAttributes = null;
        if (!fits)
        {
            newPositions = positions.ToArray(); newAttributes = attributes.ToArray();
            for (int vertex = 0; vertex < count; vertex++)
            {
                encoding.WritePosition(newPositions, vertex * VertexStreamCodec.PositionStride, points[vertex]);
                encoding.WriteUv(newAttributes, vertex * VertexStreamCodec.WeaponAttributeStride, uvs[vertex]);
            }
            model = encoding.Write(model);
        }
        byte[] reticlePositions = new byte[quad.Positions.Length * VertexStreamCodec.PositionStride];
        byte[] reticleAttributes = new byte[quad.Positions.Length * VertexStreamCodec.AttributeStride];
        for (int vertex = 0; vertex < quad.Positions.Length; vertex++)
        {
            encoding.WritePosition(reticlePositions, vertex * VertexStreamCodec.PositionStride, quad.Positions[vertex]);
            encoding.WriteUv(reticleAttributes, vertex * VertexStreamCodec.AttributeStride, quad.Uvs[vertex]);
            VertexStreamCodec.WriteFrame(reticleAttributes, vertex * VertexStreamCodec.AttributeStride, quad.Frames[vertex]);
        }
        byte[] indices = new byte[quad.Indices.Length * 4];
        for (int index = 0; index < quad.Indices.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(indices.AsSpan(index * 4), quad.Indices[index]);

        var rows = MeshRows(model);
        if (rows.Length != 1) throw new InvalidDataException("The sight model needs one imported mesh row.");
        byte[] part = row.Part.ToArray();
        ref var partView = ref TagView.Edit<SEntityModelPart>(part);
        partView.PrimitiveType3Written = TriangleList;
        partView.IndexStart = 0;
        partView.IndexCount = checked((uint)quad.Indices.Length);
        byte[] mesh = row.Mesh.ToArray();
        // Each stage entry is the first part of that stage; the one part belongs to the reticle stage.
        Span<ushort> stages = TagView.Edit<SEntityModelMesh>(mesh).StageIndexBoundaries;
        for (int stage = 0; stage < stages.Length; stage++) stages[stage] = (ushort)(stage <= ReticleStage ? 0 : 1);
        byte[] main = model.AsSpan(rows[0], MeshStride).ToArray();
        ref readonly var mainParts = ref Mesh(model, rows[0]).Parts;
        long mainPartCount = mainParts.Count;
        int mainPartHeader = new TagArray(model, TagView.OffsetOf(model, in mainParts)).HeaderOffset;
        model = Append(model, MeshesAt, SEntityModelMesh.Class, MeshStride, [.. main, .. mesh], out int first);
        // The copied imported row keeps naming its original parts.
        TagArray.WriteField(model, TagView.OffsetOf(model, in Mesh(model, first).Parts), mainPartCount, mainPartHeader);
        model = Append(model, TagView.OffsetOf(model, in Mesh(model, first + MeshStride).Parts), SEntityModelPart.Class, PartStride, part, out _);
        return new(model, newPositions, newAttributes, reticlePositions, reticleAttributes, indices);
    }

    /** The template row with its buffers and material retargeted to the clones. */
    private static ReticleRow TemplateRow(byte[] model, int at, WeaponReticleTemplate template, ReticleBuffers buffers, uint material)
    {
        byte[] mesh = model.AsSpan(at, MeshStride).ToArray();
        ref var meshView = ref TagView.Edit<SEntityModelMesh>(mesh);
        meshView.VertexBuffer0[0].Unk0 = buffers.Positions;
        meshView.VertexBuffer0[1].Unk0 = buffers.Attributes;
        meshView.IndexBuffer = buffers.Indices;
        byte[] part = model.AsSpan(template.PartOffset, PartStride).ToArray();
        TagView.Edit<SEntityModelPart>(part).Material = material;
        return new(mesh, part);
    }

    private static int TemplateRowOffset(byte[] model, WeaponReticleTemplate template) =>
        MeshRows(model).Single(row => PartRows(model, row).Contains(template.PartOffset));

    /** The template part's own vertices, as a triangle list in their stored winding. */
    private static ReticleQuad ReadTemplateQuad(PackageCatalog catalog, byte[] model, int row, WeaponReticleTemplate template)
    {
        var encoding = Encoding.Read(model);
        ref readonly var mesh = ref Mesh(model, row);
        byte[] positions = catalog.ReadOriginal(Payload(catalog, PositionHeader(in mesh)));
        byte[] attributes = catalog.ReadOriginal(Payload(catalog, AttributeHeader(in mesh)));
        uint indexHeader = mesh.IndexBuffer;
        byte[] indexData = catalog.ReadOriginal(Payload(catalog, indexHeader));
        bool wide = Head<IndexBufferHeader>(catalog.ReadOriginal(indexHeader), nameof(IndexBufferHeader.Wide)).Wide == 1;
        ref readonly var part = ref TagView.At<SEntityModelPart>(model, template.PartOffset);
        uint first = part.IndexStart, count = part.IndexCount;
        ushort topology = part.PrimitiveType3Written;
        var source = Enumerable.Range(0, checked((int)count)).Select(index => wide
            ? BinaryPrimitives.ReadUInt32LittleEndian(indexData.AsSpan(checked((int)(first + index) * 4)))
            : BinaryPrimitives.ReadUInt16LittleEndian(indexData.AsSpan(checked((int)(first + index) * 2)))).ToArray();
        var triangles = new List<uint>();
        if (topology == TriangleList) triangles.AddRange(source);
        else
            for (int index = 0; index + 2 < source.Length; index++)
                triangles.AddRange(index % 2 == 0 ? [source[index], source[index + 1], source[index + 2]]
                    : [source[index + 1], source[index], source[index + 2]]);
        uint[] vertices = triangles.Distinct().ToArray();
        int attributeStride = attributes.Length / (positions.Length / VertexStreamCodec.PositionStride);
        if (attributeStride != VertexStreamCodec.AttributeStride) throw new NotSupportedException("The template reticle has an unsupported vertex layout.");
        return new(vertices.Select(vertex => Snap(encoding.Position(positions, checked((int)vertex * VertexStreamCodec.PositionStride)))).ToArray(),
            vertices.Select(vertex => Snap(encoding.Uv(attributes, checked((int)vertex * VertexStreamCodec.AttributeStride)))).ToArray(),
            vertices.Select(vertex => VertexStreamCodec.ReadFrame(attributes, checked((int)vertex * VertexStreamCodec.AttributeStride))).ToArray(),
            triangles.Select(vertex => (uint)Array.IndexOf(vertices, vertex)).ToArray());
    }

    /** The model's shared snorm encoding: position = snorm * scale + offset, and the same for UVs. */
    private sealed record Encoding(Vector3 Offset, float Scale, Vector2 UvOffset, float UvScale)
    {
        public static Encoding Read(byte[] model)
        {
            var header = Head<SEntityModel>(model, nameof(SEntityModel.UvOffsetV));
            return new(header.PositionOffset, header.PositionScaleW, new(header.UvOffsetU, header.UvOffsetV), header.UvScaleU);
        }

        public static Encoding Covering(IEnumerable<Vector3> points, IEnumerable<Vector2> uvs)
        {
            var p = points.ToArray(); var t = uvs.ToArray();
            Vector3 min = p.Aggregate(Vector3.Min), max = p.Aggregate(Vector3.Max);
            Vector2 uvMin = t.Aggregate(Vector2.Min), uvMax = t.Aggregate(Vector2.Max);
            var half = (max - min) / 2; var uvHalf = (uvMax - uvMin) / 2;
            return new((min + max) / 2, Math.Max(1e-6f, Math.Max(half.X, Math.Max(half.Y, half.Z))),
                (uvMin + uvMax) / 2, Math.Max(1e-6f, Math.Max(uvHalf.X, uvHalf.Y)));
        }

        public bool Fits(Vector3 point) => new[] { point.X - Offset.X, point.Y - Offset.Y, point.Z - Offset.Z }
            .All(value => MathF.Abs(value / Scale) <= 1);
        public bool FitsUv(Vector2 uv) => MathF.Abs((uv.X - UvOffset.X) / UvScale) <= 1 && MathF.Abs((uv.Y - UvOffset.Y) / UvScale) <= 1;
        public Vector3 Position(byte[] data, int at) => VertexStreamCodec.ReadSnorm3(data, at) * Scale + Offset;
        public Vector2 Uv(byte[] data, int at) => VertexStreamCodec.ReadSnorm2(data, at) * UvScale + UvOffset;
        public void WritePosition(byte[] data, int at, Vector3 point) =>
            VertexStreamCodec.WriteSnorm3(data, at, (point - Offset) / Scale);
        public void WriteUv(byte[] data, int at, Vector2 uv) => VertexStreamCodec.WriteSnorm2(data, at, (uv - UvOffset) / UvScale);

        /** The model header holds the scale on each axis and once more beside the offset. */
        public byte[] Write(byte[] model)
        {
            byte[] result = model.ToArray();
            var header = Head<SEntityModel>(result, nameof(SEntityModel.UvOffsetV));
            header.PositionScale = new Vector3(Scale);
            header.PositionOffset = Offset;
            header.PositionScaleW = Scale;
            header.UvScaleU = UvScale;
            header.UvScaleV = UvScale;
            header.UvOffsetU = UvOffset.X;
            header.UvOffsetV = UvOffset.Y;
            PutHead(result, in header);
            return result;
        }
    }

    // Reticle quads are authored on a 0.1 mm and 1e-4 UV grid; snapping removes quantization drift between imports.
    private static Vector3 Snap(Vector3 value) => new(MathF.Round(value.X, 4), MathF.Round(value.Y, 4), MathF.Round(value.Z, 4));
    private static Vector2 Snap(Vector2 value) => new(MathF.Round(value.X, 4), MathF.Round(value.Y, 4));

    private static uint? FindTexture(PackageCatalog catalog, byte[] material)
    {
        for (int at = 0; at + 4 <= material.Length; at += 4)
        {
            uint tag = TagRead.U32(material, at);
            if (tag >= 0x80800000 && tag < 0x84000000 && catalog.TryGetEntry(tag, out var entry) && entry.Type == 32 && entry.SubType == 1)
                return tag;
        }
        return null;
    }

    private static byte[] Replace32(byte[] bytes, uint from, uint to)
    {
        byte[] result = bytes.ToArray();
        for (int at = 0; at + 4 <= result.Length; at += 4)
            if (TagRead.U32(result, at) == from) BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(at), to);
        return result;
    }

    private static int[] MeshRows(byte[] model) => RowOffsets(model, MeshesAt, SEntityModelMesh.Class, MeshStride);
    private static int[] PartRows(byte[] model, int row) => RowOffsets(model, row + PartsAt, SEntityModelPart.Class, PartStride);
    private static ref readonly SEntityModelMesh Mesh(byte[] model, int row) => ref TagView.At<SEntityModelMesh>(model, row);

    // Vertex buffer headers of a mesh row; a reticle row has no third buffer.
    private static uint PositionHeader(in SEntityModelMesh mesh) => mesh.VertexBuffer0[0].Unk0;
    private static uint AttributeHeader(in SEntityModelMesh mesh) => mesh.VertexBuffer0[1].Unk0;
    private static uint ThirdHeader(in SEntityModelMesh mesh) => mesh.VertexBuffer0[2].Unk0;

    /** Row offsets of a typed array; a zero count needs only its count word and reads as empty. */
    private static int[] RowOffsets(byte[] tag, int field, uint elementClass, int stride)
    {
        if (!TagView.FitsThrough<TagArray<Unknown>>(tag, field, nameof(TagArray<Unknown>.Count)))
            throw new InvalidDataException("A typed native array is truncated.");
        if (new TagArray(tag, field).Count == 0) return [];
        if (!TagView.Fits<TagArray<Unknown>>(tag, field)) throw new InvalidDataException("A typed native array is truncated.");
        if (!TagArray.TryChecked(tag, field, elementClass, stride, out var array, TagArray.MaximumReaderRows))
            throw new InvalidDataException("A typed native array is invalid.");
        return Enumerable.Range(0, array.Count).Select(index => array.Data + index * stride).ToArray();
    }

    /** Copies `tag` with `rows` appended as a new array that `field` points to; the size word follows. */
    private static byte[] Append(byte[] tag, int field, uint elementClass, int stride, byte[] rows, out int first)
    {
        byte[] output = tag;
        first = TagRelocator.AppendArray(ref output, field, elementClass, rows.Length / stride, rows.Length);
        rows.CopyTo(output, first);
        var header = Head<SEntityModel>(output, nameof(SEntityModel.TagByteLength));
        header.TagByteLength = (ulong)output.Length;
        PutHead(output, in header);
        return output;
    }

    /**
     * A copy of the T at the start of `bytes`, which may end inside it; the missing tail reads as zero.
     * @param last The last member used; bytes ending before it throw ArgumentOutOfRangeException.
     */
    private static T Head<T>(ReadOnlySpan<byte> bytes, string last) where T : unmanaged
    {
        if (!TagView.FitsThrough<T>(bytes, 0, last))
            throw new ArgumentOutOfRangeException(nameof(bytes), $"The bytes end before {typeof(T).Name}.{last}.");
        T value = default;
        var target = MemoryMarshal.AsBytes(new Span<T>(ref value));
        bytes[..Math.Min(bytes.Length, target.Length)].CopyTo(target);
        return value;
    }

    /** Writes `value` over the start of `bytes`, up to their end. */
    private static void PutHead<T>(Span<byte> bytes, in T value) where T : unmanaged
    {
        var source = MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value));
        source[..Math.Min(bytes.Length, source.Length)].CopyTo(bytes);
    }

    private static uint Payload(PackageCatalog catalog, uint headerTag) => Require(catalog, headerTag).Reference;
    private static AddedProjectAsset? Owned(StudioProject project, uint tag) =>
        project.AddedAssets.FirstOrDefault(asset => asset.Native is { } native && NativeTag(native) == tag);
    private static PackageEntry Require(PackageCatalog catalog, uint tag) => catalog.TryGetEntry(tag, out var entry)
        ? entry : throw new InvalidDataException($"Reticle source {tag:X8} is missing.");
    private static uint NativeTag(NativeAssetMetadata native) =>
        TagHashes.Make(native.PackageId, native.EntryIndex!.Value);
}
