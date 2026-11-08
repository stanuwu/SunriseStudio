using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using SunriseStudio.Animation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

public sealed record WeaponRigidPrimitive(InterchangeMesh Mesh, uint MaterialTag, short PaletteIndex)
{
    public IReadOnlyList<short>? PaletteIndices { get; init; }
}
public sealed record WeaponRigidModelOptions(uint EntityTag, uint ModelTag, int SourcePartOffset,
    ushort PackageId, int FirstEntryIndex, string Name, uint SkeletonEntityTag = 0);
public sealed record PreparedWeaponRigidModel(uint ModelTag, uint VertexShaderTag,
    int VertexCount, int TriangleCount, IReadOnlyList<ProjectAssetAddition> Assets);

/** The weapon profile stores one matrix-palette index in each position's fourth short. */
public static class WeaponAssetModelCompiler
{
    private const string ShaderHash = "4D3706BDD7372EEDF5D57D289C1956F2C833DBBC2A2A7E2A0D2E9E4852B534B6";
    private const int EntryCount = 7, Declaration = 139;

    /** Primitive coordinates are package Z-up space and palette indices belong to the donor. */
    public static PreparedWeaponRigidModel Prepare(PackageCatalog catalog,
        IReadOnlyList<WeaponRigidPrimitive> primitives, WeaponRigidModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(primitives);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Name);
        if (primitives.Count is < 1 or > short.MaxValue)
            throw new InvalidDataException("A weapon model needs one or more triangle primitives.");
        if (!PackageWriter.IsNewPackageId(options.PackageId) || catalog.ContainsInstalledPackage(options.PackageId)
            || options.FirstEntryIndex < 0 || options.FirstEntryIndex > TagHashes.EntriesPerPackage - EntryCount)
            throw new InvalidDataException("Weapon geometry needs seven entries in an unused package.");
        var modelEntry = Require(catalog, options.ModelTag);
        if (modelEntry.Reference != SEntityModel.Class)
            throw new InvalidDataException("Weapon template is not a native entity model.");
        var scene = new AssetSceneLoader(catalog) { IncludeLowerLods = true }.Load(options.EntityTag);
        var chosen = scene.Instances.FirstOrDefault(instance => instance.Mesh.TagHash == options.ModelTag
            && instance.Mesh.PartIndex == options.SourcePartOffset)
            ?? throw new InvalidDataException("Weapon template part is not linked to its entity.");
        if (chosen.InitiallyVisible == false || chosen.Mesh.LodLevel != 0
            || chosen.Mesh.Native is not { } native || !Profile(native)
            || Convert.ToHexString(SHA256.HashData(catalog.Read(native.VertexShaderDataTag))) != ShaderHash)
            throw new NotSupportedException("Weapon geometry needs the checked declaration-139 matrix profile.");
        byte[] model = catalog.Read(options.ModelTag).ToArray();
        int meshSize = Unsafe.SizeOf<SEntityModelMesh>(), partSize = Unsafe.SizeOf<SEntityModelPart>();
        int meshField = TagView.FieldOffset<SEntityModel>(nameof(SEntityModel.Meshes));
        int[] meshRows = WeaponTagRows.Starts<SEntityModelMesh>(model, meshField, SEntityModelMesh.Class);
        int sourceMesh = meshRows.Single(row => WeaponTagRows.Starts<SEntityModelPart>(model, PartsField(row), SEntityModelPart.Class)
            .Contains(options.SourcePartOffset));
        var paletteIndices = new HashSet<short>();
        foreach (var instance in scene.Instances.Where(instance => instance.Mesh.TagHash == options.ModelTag))
        {
            var source = instance.Mesh.Native?.VertexStreams[0];
            if (source is null || source.Stride != VertexStreamCodec.PositionStride) continue;
            foreach (uint vertex in instance.Mesh.SourceVertexIndices)
                paletteIndices.Add(VertexStreamCodec.ReadPaletteIndex(source.Data, VertexStreamCodec.PositionStride, vertex));
        }
        int paletteCount = options.SkeletonEntityTag == 0 ? 0
            : new SkeletonReader(catalog).Read(options.SkeletonEntityTag).Bones.Count;
        foreach (var primitive in primitives)
        {
            if (primitive.PaletteIndices is not null && primitive.PaletteIndices.Count != primitive.Mesh.Positions.Length)
                throw new InvalidDataException("Every weapon vertex needs one palette index.");
            foreach (short index in primitive.PaletteIndices ?? [primitive.PaletteIndex])
                if (index < 0 || (paletteCount == 0 ? !paletteIndices.Contains(index) : index >= paletteCount))
                    throw new NotSupportedException("A weapon vertex names a bone outside its native palette.");
        }
        foreach (var primitive in primitives) _ = ObjInterchange.ToMesh(primitive.Mesh);
        var positions = primitives.SelectMany(part => part.Mesh.Positions).ToArray();
        var normals = primitives.SelectMany(part => part.Mesh.Normals).ToArray();
        var uvs = primitives.SelectMany(part => part.Mesh.TexCoords).ToArray();
        var indices = new List<uint>();
        uint baseVertex = 0;
        foreach (var primitive in primitives)
        {
            indices.AddRange(primitive.Mesh.Indices.Select(index => checked(index + baseVertex)));
            baseVertex = checked(baseVertex + (uint)primitive.Mesh.Positions.Length);
        }
        var encoded = MeshAssetImporter.EncodeGeometry(new(options.Name, positions, normals, uvs, indices.ToArray()));
        byte[] attributes = new byte[checked(positions.Length * VertexStreamCodec.WeaponAttributeStride)];
        int firstVertex = 0;
        foreach (var primitive in primitives)
            for (int local = 0; local < primitive.Mesh.Positions.Length; local++)
            {
                int vertex = firstVertex++;
                VertexStreamCodec.WritePaletteIndex(encoded.Positions, vertex, primitive.PaletteIndices?[local] ?? primitive.PaletteIndex);
                VertexStreamCodec.WriteWeaponAttribute(encoded.Attributes, attributes, vertex);
            }
        uint Tag(int index) => TagHashes.Make(options.PackageId, options.FirstEntryIndex + index);
        byte[] parts = new byte[checked(primitives.Count * partSize)];
        uint firstIndex = 0;
        for (int index = 0; index < primitives.Count; index++)
        {
            int at = index * partSize;
            model.AsSpan(options.SourcePartOffset, partSize).CopyTo(parts.AsSpan(at));
            ref var part = ref TagView.Edit<SEntityModelPart>(parts, at);
            part.Material = primitives[index].MaterialTag;
            part.ExternalMaterialGroup1Direct = -1;
            part.PrimitiveType3Written = 3;
            part.IndexStart = firstIndex;
            part.IndexCount = checked((uint)primitives[index].Mesh.Indices.Length);
            firstIndex = checked(firstIndex + (uint)primitives[index].Mesh.Indices.Length);
        }
        byte[] mesh = model.AsSpan(sourceMesh, meshSize).ToArray();
        ref var newMesh = ref TagView.Edit<SEntityModelMesh>(mesh);
        newMesh.VertexBuffer0[0].Unk0 = Tag(1); newMesh.VertexBuffer0[1].Unk0 = Tag(3); newMesh.VertexBuffer0[2].Unk0 = uint.MaxValue;
        newMesh.IndexBuffer = Tag(5);
        Span<ushort> stages = newMesh.StageIndexBoundaries;
        for (int stage = 0; stage < stages.Length; stage++)
            stages[stage] = stage == 0 ? (ushort)0 : checked((ushort)primitives.Count);
        int meshAt = Append(ref model, meshField, SEntityModelMesh.Class, meshSize, mesh);
        Append(ref model, PartsField(meshAt), SEntityModelPart.Class, partSize, parts);
        ref var root = ref TagView.Edit<SEntityModel>(model);
        if (paletteCount > 0) root.AuthoredBoneCount = paletteCount;
        root.PositionScale = new(encoded.PositionScale);
        root.PositionOffset = new(encoded.PositionOffset.X, encoded.PositionOffset.Y, encoded.PositionOffset.Z);
        root.PositionScaleW = encoded.PositionScale;
        root.UvScaleU = encoded.UvScale; root.UvScaleV = encoded.UvScale;
        root.UvOffsetU = encoded.UvOffset.X; root.UvOffsetV = encoded.UvOffset.Y;
        var assets = new List<ProjectAssetAddition>
        {
            new(options.Name + ".model", model,
                new(options.PackageId, modelEntry.Reference, modelEntry.TypeInfo, options.Name, options.FirstEntryIndex), options.ModelTag)
        };
        Pair(native.VertexStreams[0].HeaderTag, encoded.Positions, 1, "positions", false);
        Pair(native.VertexStreams[1].HeaderTag, attributes, 3, "attributes", false);
        Pair(native.IndexHeaderTag, encoded.Indices, 5, "indices", true);
        return new(Tag(0), native.VertexShaderTag, positions.Length, indices.Count / 3, assets);

        void Pair(uint headerTag, byte[] data, int index, string name, bool indexBuffer)
        {
            var headerEntry = Require(catalog, headerTag);
            var payloadEntry = Require(catalog, headerEntry.Reference);
            if (headerEntry.Type != 32 || headerEntry.SubType != (indexBuffer ? 6 : 4)
                || payloadEntry.Type != 40 || payloadEntry.SubType != headerEntry.SubType)
                throw new InvalidDataException("Weapon buffer source types changed.");
            byte[] header = catalog.Read(headerTag).ToArray();
            if (indexBuffer)
            {
                TagView.Edit<byte>(header, TagView.FieldOffset<IndexBufferHeader>(nameof(IndexBufferHeader.Wide))) = 1;
                TagView.Edit<ulong>(header, TagView.FieldOffset<IndexBufferHeader>(nameof(IndexBufferHeader.ByteCount))) = (ulong)data.Length;
            }
            else TagView.Edit<uint>(header, TagView.FieldOffset<VertexBufferHeader>(nameof(VertexBufferHeader.ByteCount))) = checked((uint)data.Length);
            assets.Add(new(options.Name + "." + name, header,
                new(options.PackageId, Tag(index + 1), headerEntry.TypeInfo, null, options.FirstEntryIndex + index), headerTag));
            assets.Add(new(options.Name + "." + name + ".bin", data,
                new(options.PackageId, Tag(index), payloadEntry.TypeInfo, null, options.FirstEntryIndex + index + 1)));
        }
    }

    private static bool Profile(NativeMeshData native) => native.Kind == NativeMeshKind.Entity
        && native.InputLayoutVerified && native.NativeDeclarationId == Declaration && native.RenderStage == 0
        && native.VertexStreams.Length == 2 && native.VertexStreams[0].Stride == VertexStreamCodec.PositionStride
        && native.VertexStreams[1].Stride == VertexStreamCodec.WeaponAttributeStride && native.VertexStreams.All(stream => stream.Type == 0)
        && native.SkinWeightStream is null && native.InputElements.SequenceEqual(new NativeInputElement[]
        {
            new("POSITION", 0, 0, 0, NativeVertexFormat.R16G16B16A16SNorm),
            new("TEXCOORD", 0, 1, 0, NativeVertexFormat.R16G16SNorm),
            new("NORMAL", 0, 1, 4, NativeVertexFormat.R16G16B16A16SNorm),
            new("TANGENT", 0, 1, 12, NativeVertexFormat.R16G16B16A16SNorm),
            new("TEXCOORD", 2, 1, 20, NativeVertexFormat.R16G16Float)
        });

    private static PackageEntry Require(PackageCatalog catalog, uint tag) => catalog.TryGetEntry(tag, out var entry)
        ? entry : throw new InvalidDataException("Weapon geometry source is missing.");

    /** The parts array field of the mesh row at `mesh`. */
    private static int PartsField(int mesh) => mesh + TagView.FieldOffset<SEntityModelMesh>(nameof(SEntityModelMesh.Parts));

    /**
     * Points `field` at a new array of `rows` at the end of the model; the old rows stay unreferenced.
     * @return The offset of the first new row.
     */
    private static int Append(ref byte[] model, int field, uint elementClass, int stride, byte[] rows)
    {
        int first = TagRelocator.AppendArray(ref model, field, elementClass, rows.Length / stride, rows.Length);
        rows.CopyTo(model, first);
        TagView.Edit<SEntityModel>(model).TagByteLength = (ulong)model.Length;
        return first;
    }

}
