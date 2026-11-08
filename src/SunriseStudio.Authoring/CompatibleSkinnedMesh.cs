using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

public sealed record CompatibleSkinBone(uint NameHash, int ParentIndex, Matrix4x4 BindObject,
    Matrix4x4 InverseBindObject);
public sealed record CompatibleSkinnedMeshInput(InterchangeMesh Geometry,
    IReadOnlyList<CompatibleSkinBone> Bones, IReadOnlyList<VertexSkinWeights> Influences);
public sealed record CompatibleSkinReplacementOptions(uint EntityTag, uint ModelTag, int SourcePartOffset,
    string Name, ushort PackageId, int FirstEntryIndex);
public sealed record CompatibleSkinTemplate(uint ModelTag, int PartOffset, uint MaterialTag);
public sealed record PreparedCompatibleSkin(uint EntityTag, uint ModelTag, uint SkeletonTag, uint MaterialTag,
    int VertexCount, int TriangleCount, IReadOnlyList<int> RetainedSourceParts, int RemovedSourceParts,
    IReadOnlyList<ProjectAssetAddition> Additions, IReadOnlyDictionary<uint, byte[]> Replacements,
    IReadOnlyDictionary<uint, string> SourceHashes)
{
    public int ConsumedBones { get; init; }
}

/** A replacement keeps the installed rig and compiles only the checked four-weight matrix profile. */
public static partial class CompatibleSkinnedMesh
{
    private const string MatrixShaderHash = "880AC672C57AC1947343E344CBAD49EEB3B6A7759C68FE28EC1504D1A52997EF";
    private const string ShadowShaderHash = "A8558442B245BD8CDF3E40E4115D7248A903B44D0E864BBD9C1FF1B3986E15F8";
    private const int EntryCount = 6;
    private const float BindTolerance = .0002f;

    /** Template choices come from installed geometry even after a project replaces the model. */
    public static IReadOnlyList<CompatibleSkinTemplate> Templates(PackageCatalog catalog, uint entityTag,
        CancellationToken token = default)
    {
        using var originals = PackageCatalog.Open(catalog.GameRoot);
        originals.ReadOverride = catalog.ReadOriginal;
        var rig = new SkeletonReader(originals).Read(entityTag);
        var scene = new AssetSceneLoader(originals).Load(entityTag, token);
        var shaders = new Dictionary<uint, bool>();
        bool Supported(MeshData mesh)
        {
            if (mesh.LodLevel != 0 || !rig.ModelTags.Contains(mesh.TagHash)
                || mesh.Native is not { Kind: NativeMeshKind.Entity, RenderStage: 0 } native || !Profile(native)) return false;
            if (!shaders.TryGetValue(native.VertexShaderDataTag, out bool accepted))
                shaders[native.VertexShaderDataTag] = accepted = Hash(originals.Read(native.VertexShaderDataTag)) == MatrixShaderHash;
            return accepted;
        }
        return scene.Instances.Where(instance => instance.InitiallyVisible != false)
            .Select(instance => instance.Mesh).Where(Supported)
            .Select(mesh => new CompatibleSkinTemplate(mesh.TagHash, mesh.PartIndex, mesh.MaterialTag))
            .Distinct().OrderBy(value => value.ModelTag).ThenBy(value => value.PartOffset).ToArray();
    }

    /** Input bones and vertices use package order and Z-up object space; no bone retargeting occurs. */
    public static PreparedCompatibleSkin Prepare(PackageCatalog catalog, CompatibleSkinnedMeshInput input,
        CompatibleSkinReplacementOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(input.Geometry); ArgumentNullException.ThrowIfNull(input.Bones);
        ArgumentNullException.ThrowIfNull(input.Influences);
        long sourceRevision = catalog.OverlayRevision;
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Name);
        if (!PackageWriter.IsNewPackageId(options.PackageId) || catalog.ContainsInstalledPackage(options.PackageId)
            || options.FirstEntryIndex < 0 || options.FirstEntryIndex > TagHashes.EntriesPerPackage - EntryCount)
            throw new InvalidDataException("Skin buffers need six unused entries in a new package.");
        using var originals = PackageCatalog.Open(catalog.GameRoot);
        originals.ReadOverride = catalog.ReadOriginal;
        var modelEntry = Require(originals, options.ModelTag);
        if (modelEntry.Reference != SEntityModel.Class || !catalog.ContainsInstalledPackage(modelEntry.PackageId))
            throw new NotSupportedException("Choose an installed entity model to replace.");
        var rig = new SkeletonReader(originals).Read(options.EntityTag);
        if (!rig.ModelTags.Contains(options.ModelTag))
            throw new NotSupportedException("The model must use this entity's non-cloth skeleton.");
        ValidateBones(input.Bones, rig);
        var activeRig = new SkeletonReader(catalog).Read(options.EntityTag);
        if (!activeRig.ModelTags.Contains(options.ModelTag) || activeRig.ResourceTag != rig.ResourceTag)
            throw new NotSupportedException("The entity no longer links this installed model and rig.");
        ValidateBones(input.Bones, activeRig);
        if (input.Influences.Count != input.Geometry.Positions.Length)
            throw new InvalidDataException("Each imported vertex needs four package-order skin influences.");
        token.ThrowIfCancellationRequested();
        var scene = new AssetSceneLoader(originals) { IncludeLowerLods = true }.Load(options.EntityTag, token);
        var selectedInstance = scene.Instances
            .FirstOrDefault(instance => instance.Mesh.TagHash == options.ModelTag && instance.Mesh.PartIndex == options.SourcePartOffset)
            ?? throw new InvalidDataException("The selected part does not belong to the template entity.");
        var selected = selectedInstance.Mesh;
        if (selectedInstance.InitiallyVisible == false)
            throw new NotSupportedException("Choose a template part that is visible in the entity's initial state.");
        if (selected.LodLevel != 0 || selected.Native is not { Kind: NativeMeshKind.Entity, RenderStage: 0 } native
            || !Profile(native) || Hash(originals.Read(native.VertexShaderDataTag)) != MatrixShaderHash)
            throw new NotSupportedException("Choose a LOD0 part with the checked declaration-16 matrix skinning shader.");
        var hashes = new Dictionary<uint, string>();
        var activeSources = new Dictionary<uint, byte[]>();
        byte[] Active(uint tag)
        {
            if (activeSources.TryGetValue(tag, out var bytes)) return bytes;
            bytes = catalog.Read(tag); activeSources.Add(tag, bytes); hashes[tag] = Hash(bytes); return bytes;
        }
        byte[] Read(uint tag)
        { Active(tag); return originals.Read(tag); }
        Read(options.EntityTag); Read(rig.ResourceTag); Read(selected.MaterialTag); Read(native.VertexShaderDataTag);
        byte[] model = Read(options.ModelTag);
        if (!TagView.Fits<SEntityModel>(model, 0) || (long)TagView.At<SEntityModel>(model).TagByteLength != model.Length)
            throw new InvalidDataException("Entity model header is invalid.");
        byte[] activeEntity = Active(options.EntityTag);
        if (!Rows<SPatternComponent>(activeEntity, TagView.FieldOffset<SEntity>(nameof(SEntity.Components)), SPatternComponent.Class)
            .Any(row => TagView.At<SPatternComponent>(activeEntity, row).Config == selectedInstance.ModelSourceConfigTag))
            throw new NotSupportedException("The entity no longer uses the installed template's model component.");
        byte[] activeConfig = Active(selectedInstance.ModelSourceConfigTag);
        int consumedBones = ConsumedBoneCount(activeConfig, model, options.ModelTag);
        if (consumedBones > rig.Bones.Count)
            throw new InvalidDataException("The model consumes more bones than its entity skeleton supplies.");
        var weights = EncodeInfluences(input.Influences, consumedBones);
        var geometry = MeshAssetImporter.EncodeGeometry(input.Geometry, token);
        var positions = VertexStreamCodec.SkinPositions(geometry.Positions, weights, input.Geometry.Positions.Length);
        var sourcePositions = scene.Instances.Where(instance => instance.Mesh.TagHash == options.ModelTag)
            .SelectMany(instance => instance.Mesh.Vertices.Select(vertex => vertex.Position)).ToArray();
        var minimum = sourcePositions.Aggregate(Vector3.Min); var maximum = sourcePositions.Aggregate(Vector3.Max);
        if (input.Geometry.Positions.Any(position => position.X < minimum.X || position.Y < minimum.Y || position.Z < minimum.Z
            || position.X > maximum.X || position.Y > maximum.Y || position.Z > maximum.Z))
            throw new NotSupportedException("Replacement vertices must stay within the source model's bind bounds.");
        int[] meshRows = Rows(model, in TagView.At<SEntityModel>(model).Meshes, SEntityModelMesh.Class);
        int[] PartRows(int mesh) => Rows(model, in TagView.At<SEntityModelMesh>(model, mesh).Parts, SEntityModelPart.Class);
        SEntityModelPart PartAt(int at) => TagView.At<SEntityModelPart>(model, at);
        int originalPartCount = meshRows.Sum(row => PartRows(row).Length);
        int sourceMesh = meshRows.Single(row => PartRows(row).Contains(options.SourcePartOffset));
        int[] sourceParts = PartRows(sourceMesh);
        int sourcePart = options.SourcePartOffset;
        int Stage(int ordinal)
        {
            ReadOnlySpan<ushort> bounds = TagView.At<SEntityModelMesh>(model, sourceMesh).StageIndexBoundaries;
            for (int stage = 0; stage < bounds.Length - 1; stage++)
                if (ordinal >= bounds[stage] && ordinal < bounds[stage + 1]) return stage;
            throw new InvalidDataException("A model part has no render-stage range.");
        }
        var retained = sourceParts.Select((part, ordinal) => (Part: part, Stage: Stage(ordinal)))
            .Where(value => PartAt(value.Part).IndexStart == PartAt(sourcePart).IndexStart
                && PartAt(value.Part).IndexCount == PartAt(sourcePart).IndexCount
                && PartAt(value.Part).PrimitiveType3Written == PartAt(sourcePart).PrimitiveType3Written
                && PartAt(value.Part).LodCategory == PartAt(sourcePart).LodCategory).OrderBy(value => value.Stage).ToArray();
        uint visibilityCount = TagView.At<SEntityModel>(model).PartVisibilityMaskBitsMultipleOf32;
        if (retained.Length == 0 || visibilityCount == 0 || (visibilityCount & 31) != 0
            || retained.Any(value => PartAt(value.Part).VisibilityIndex >= visibilityCount))
            throw new InvalidDataException("The template part visibility indices leave their original mask.");
        uint[] selectedMaterials = [];
        if (retained.Any(value => PartAt(value.Part).ExternalMaterialGroup1Direct != -1))
        {
            var stateReads = new HashSet<uint>();
            var initial = new EntityInitialStateReader(catalog) { Reads = stateReads }
                .Read(options.EntityTag, selectedInstance.ModelSourceConfigTag);
            foreach (uint tag in stateReads) Active(tag);
            int resource = PointerTarget(activeConfig, TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition)));
            var selection = EntityMaterialSelector.Read(activeConfig, resource, initial.EnabledParts, initial.Unresolved);
            if (selection.Unresolved is not null)
                throw new NotSupportedException("The template's initial material state is unresolved: " + selection.Unresolved);
            selectedMaterials = selection.Materials;
        }
        var checkedMaterials = new HashSet<(uint Tag, int Stage)>();
        uint materialTag = 0;
        foreach (var part in retained)
        {
            if (TagView.At<SEntityModelMesh>(model, sourceMesh).StageVertexDeclaration[part.Stage] != native.NativeDeclarationId)
                throw new NotSupportedException("A retained render pass uses a different vertex declaration.");
            uint[] materials = PartMaterials(model, part.Part, activeConfig);
            if (part.Part == sourcePart)
            {
                var row = PartAt(part.Part);
                int group = row.ExternalMaterialGroup1Direct;
                materialTag = group == -1 ? row.Material : selectedMaterials[group];
                if (materialTag == uint.MaxValue)
                    throw new NotSupportedException("The selected part has no material in its initial state.");
            }
            foreach (uint tag in materials)
            {
                if (!checkedMaterials.Add((tag, part.Stage))) continue;
                string expected = part.Stage switch { 0 => MatrixShaderHash, 3 => ShadowShaderHash,
                    _ => throw new NotSupportedException("A retained render pass has no checked skinning profile.") };
                ValidateMaterial(catalog, tag, expected, Active);
            }
        }
        uint Tag(int offset) => TagHashes.Make(options.PackageId, options.FirstEntryIndex + offset);
        int partSize = Unsafe.SizeOf<SEntityModelPart>(), meshSize = Unsafe.SizeOf<SEntityModelMesh>();
        var partBytes = new byte[checked(retained.Length * partSize)];
        for (int index = 0; index < retained.Length; index++)
        {
            model.AsSpan(retained[index].Part, partSize).CopyTo(partBytes.AsSpan(index * partSize));
            ref var copy = ref TagView.Edit<SEntityModelPart>(partBytes, index * partSize);
            copy.PrimitiveType3Written = 3; copy.IndexStart = 0;
            copy.IndexCount = checked((uint)input.Geometry.Indices.Length);
        }
        byte[] mesh = model.AsSpan(sourceMesh, meshSize).ToArray();
        ref var meshRow = ref TagView.Edit<SEntityModelMesh>(mesh);
        meshRow.VertexBuffer0[0].Unk0 = Tag(0); meshRow.VertexBuffer0[1].Unk0 = Tag(2);
        meshRow.VertexBuffer0[2].Unk0 = uint.MaxValue; meshRow.IndexBuffer = Tag(4);
        Span<ushort> boundaries = meshRow.StageIndexBoundaries;
        for (int stage = 0; stage < boundaries.Length; stage++)
            boundaries[stage] = checked((ushort)retained.Count(value => value.Stage < stage));
        model = TagRelocator.ReplaceArray(model, TagView.OffsetOf(model, in TagView.At<SEntityModel>(model).Meshes),
            SEntityModelMesh.Class, meshSize, mesh, out int newMesh);
        model = TagRelocator.ReplaceArray(model, TagView.OffsetOf(model, in TagView.At<SEntityModelMesh>(model, newMesh).Parts),
            SEntityModelPart.Class, partSize, partBytes, out _);
        ref var modelHeader = ref TagView.Edit<SEntityModel>(model);
        modelHeader.PositionScale = new Vector3(geometry.PositionScale);
        modelHeader.PositionOffset = new Vector3(geometry.PositionOffset.X, geometry.PositionOffset.Y, geometry.PositionOffset.Z);
        modelHeader.PositionScaleW = geometry.PositionScale;
        modelHeader.UvScaleU = geometry.UvScale; modelHeader.UvScaleV = geometry.UvScale;
        modelHeader.UvOffsetU = geometry.UvOffset.X; modelHeader.UvOffsetV = geometry.UvOffset.Y;
        var additions = new List<ProjectAssetAddition>();
        BufferPair(native.VertexStreams[0].HeaderTag, positions, 0, "positions", false);
        BufferPair(native.VertexStreams[1].HeaderTag, geometry.Attributes, 2, "attributes", false);
        BufferPair(native.IndexHeaderTag, geometry.Indices, 4, "indices", true);
        token.ThrowIfCancellationRequested();
        if (catalog.OverlayRevision != sourceRevision)
            throw new InvalidDataException("Project sources changed while the skin replacement was being prepared.");
        return new(options.EntityTag, options.ModelTag, rig.ResourceTag, materialTag,
            input.Geometry.Positions.Length, input.Geometry.Indices.Length / 3, retained.Select(value => value.Part).ToArray(),
            originalPartCount - retained.Length, additions.AsReadOnly(), new Dictionary<uint, byte[]> { [options.ModelTag] = model }, hashes)
        { ConsumedBones = consumedBones };

        void BufferPair(uint headerTag, byte[] data, int index, string suffix, bool indices)
        {
            var headerEntry = Require(originals, headerTag); var payloadEntry = Require(originals, headerEntry.Reference);
            if (!catalog.ContainsInstalledPackage(headerEntry.PackageId) || !catalog.ContainsInstalledPackage(payloadEntry.PackageId)
                || headerEntry.Type != 32 || headerEntry.SubType != (indices ? 6 : 4)
                || payloadEntry.Type != 40 || payloadEntry.SubType != headerEntry.SubType)
                throw new NotSupportedException("The template must still reference installed vertex/index buffer pairs.");
            byte[] header = Read(headerTag).ToArray(); Read(payloadEntry.TagHash);
            if (indices)
            {
                if (!TagView.FitsThrough<IndexBufferHeader>(header, 0, nameof(IndexBufferHeader.ByteCount))) throw new InvalidDataException("Truncated index header.");
                Put<IndexBufferHeader, byte>(header, 0, nameof(IndexBufferHeader.Wide), 1);
                Put<IndexBufferHeader, long>(header, 0, nameof(IndexBufferHeader.ByteCount), data.Length);
            }
            else Put<VertexBufferHeader, uint>(header, 0, nameof(VertexBufferHeader.ByteCount), checked((uint)data.Length));
            additions.Add(new(options.Name + "." + suffix, header,
                new(options.PackageId, Tag(index + 1), headerEntry.TypeInfo, null, options.FirstEntryIndex + index), headerEntry.TagHash));
            additions.Add(new(options.Name + "." + suffix + ".bin", data,
                new(options.PackageId, Tag(index), payloadEntry.TypeInfo, null, options.FirstEntryIndex + index + 1), payloadEntry.TagHash));
        }
    }

    /** Each material variant and retained shadow pass must consume the same encoded skin layout. */
    private static void ValidateMaterial(PackageCatalog catalog, uint tag, string shaderHash, Func<uint, byte[]> read)
    {
        if (Require(catalog, tag).Reference != STechnique.Class)
            throw new InvalidDataException("A retained assignment is not a material.");
        byte[] material = read(tag);
        if (!TagView.Fits<STechnique>(material, 0) || TagView.Member<STechnique, long>(material, 0, nameof(STechnique.TagSize)) != material.Length)
            throw new InvalidDataException("A retained material has an invalid header.");
        var headerEntry = Require(catalog, TagView.Member<STechnique, uint>(material, 0, "VertexShader.Shader"));
        var payloadEntry = Require(catalog, headerEntry.Reference);
        byte[] header = read(headerEntry.TagHash), payload = read(payloadEntry.TagHash);
        if (headerEntry.Type != 33 || headerEntry.SubType != 1 || payloadEntry.Type != 41 || payloadEntry.SubType != 1
            || !TagView.FitsThrough<ShaderHeader>(header, 0, nameof(ShaderHeader.BytecodeSize))
            || TagView.Member<ShaderHeader, long>(header, 0, nameof(ShaderHeader.HeaderSize)) != header.Length
            || TagView.Member<ShaderHeader, uint>(header, 0, nameof(ShaderHeader.BytecodeSize)) != payload.Length
            || Hash(payload) != shaderHash)
            throw new NotSupportedException("A retained material variant no longer uses the checked skinning shader.");
    }

    /** Validate every listed alternative even when initial state selects only one. */
    private static uint[] PartMaterials(byte[] model, int part, byte[] config)
    {
        var row = TagView.At<SEntityModelPart>(model, part);
        uint direct = row.Material;
        int variant = row.ExternalMaterialGroup1Direct;
        if (variant == -1) return [direct];
        int resource = PointerTarget(config, TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition)));
        int[] groups = Rows<SExternalMaterialGroup>(config,
            resource + TagView.FieldOffset<SModelComponent>("HF5b4dbc0.MaterialGroups"), SExternalMaterialGroup.Class);
        int[] materials = Rows<TagReferenceElement>(config,
            resource + TagView.FieldOffset<SModelComponent>("HF5b4dbc0.Materials"), TagReferenceElement.Class);
        if ((uint)variant >= groups.Length) throw new InvalidDataException("The part's external material group is missing.");
        var group = TagView.At<SExternalMaterialGroup>(config, groups[variant]);
        int count = group.Count;
        int first = group.First;
        if (count <= 0 || first < 0 || first > materials.Length - count)
            throw new InvalidDataException("The part's external material variants leave their table.");
        return materials.AsSpan(first, count).ToArray().Select(offset => TagView.At<TagReferenceElement>(config, offset).Tag).ToArray();
    }

    /** The model constructor uploads a prefix of the skeleton, independently of its physics partitions. */
    private static int ConsumedBoneCount(byte[] config, byte[] model, uint modelTag)
    {
        if (!TagView.FitsThrough<SEntityConfig>(config, 0, nameof(SEntityConfig.Definition))) throw new InvalidDataException("The model component is truncated.");
        int primary = PointerTarget(config, TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.DefaultInstance)));
        int resource = PointerTarget(config, TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition)));
        // Only the fields read must fit; the class words sit just before each object.
        if (primary < TagView.ClassWordSize || !TagView.FitsThrough<SModelComponentPrimary>(config, primary, nameof(SModelComponentPrimary.ConsumedBoneCount))
            || resource < TagView.ClassWordSize || !TagView.FitsThrough<SModelComponent>(config, resource, nameof(SModelComponent.ConstructorKind2MatrixPalette))
            || TagView.ClassAt(config, primary) != SModelComponentPrimary.Class
            || TagView.ClassAt(config, resource) != SModelComponent.Class
            || TagView.Member<SModelComponent, uint>(config, resource, nameof(SModelComponent.Model)) != modelTag
            || TagView.Member<SModelComponent, uint>(config, resource, nameof(SModelComponent.ConstructorKind2MatrixPalette)) != 2)
            throw new NotSupportedException("The model component does not use the checked matrix-palette constructor.");
        uint count = TagView.Member<SModelComponentPrimary, uint>(config, primary, nameof(SModelComponentPrimary.ConsumedBoneCount));
        int authored = TagView.At<SEntityModel>(model).AuthoredBoneCount;
        if (authored >= 0) count = Math.Max(count, (uint)authored);
        if (count is 0 or > 256) throw new NotSupportedException("The consumed matrix palette does not fit byte joint indices.");
        return (int)count;
    }

    /** Coupled model and buffer publication is one project transaction after source revalidation. */
    public static IReadOnlyList<AddedProjectAsset> Apply(StudioProject project, PackageCatalog catalog, PreparedCompatibleSkin prepared)
        => project.ApplyTransaction(prepared.Additions, _ => Edits(catalog, prepared), catalog);

    /** The editor checks the same source snapshot before publishing its undoable transaction. */
    public static ProjectAssetEdits Edits(PackageCatalog catalog, PreparedCompatibleSkin prepared)
    {
        foreach (var (tag, expected) in prepared.SourceHashes)
            if (Hash(catalog.Read(tag)) != expected) throw new InvalidDataException("The compatible skin template changed during preparation.");
        return new(prepared.Replacements);
    }

    /** Bone names, parent order and both bind matrices must match the selected installed rig. */
    private static void ValidateBones(IReadOnlyList<CompatibleSkinBone> supplied, SkeletonRig rig)
    {
        if (supplied.Count != rig.Bones.Count) throw new InvalidDataException("Imported and template skeleton counts differ.");
        for (int index = 0; index < supplied.Count; index++)
        {
            var input = supplied[index]; var native = rig.Bones[index];
            if (input.NameHash != native.NameHash || input.ParentIndex != native.ParentIndex
                || !Close(input.BindObject, native.BindObject.Matrix) || !Close(input.InverseBindObject, native.InverseBindObject.Matrix))
                throw new InvalidDataException($"Imported bone {index} differs from the template's package-order hierarchy or bind pose.");
        }
    }

    /** Largest remainders keep normalized byte weights at exactly 255. */
    public static byte[] EncodeInfluences(IReadOnlyList<VertexSkinWeights> vertices, int boneCount)
    {
        if (boneCount is < 1 or > 256) throw new InvalidDataException("This skin profile addresses at most 256 bones.");
        byte[] result = new byte[checked(vertices.Count * 8)];
        for (int vertex = 0; vertex < vertices.Count; vertex++)
        {
            var source = vertices[vertex]; int[] indices = [source.Bone0, source.Bone1, source.Bone2, source.Bone3];
            float[] weights = [source.Weights.X, source.Weights.Y, source.Weights.Z, source.Weights.W];
            double sum = weights.Sum(value => (double)value);
            if (weights.Any(value => !float.IsFinite(value) || value < 0 || value > 1) || Math.Abs(sum - 1) > .00001)
                throw new InvalidDataException("Skin weights must be finite, nonnegative and sum to one.");
            double[] scaled = weights.Select(value => value / sum * 255).ToArray();
            int[] encoded = scaled.Select(value => (int)Math.Floor(value)).ToArray();
            foreach (int lane in Enumerable.Range(0, 4).OrderByDescending(lane => scaled[lane] - encoded[lane]).ThenBy(lane => lane).Take(255 - encoded.Sum())) encoded[lane]++;
            for (int lane = 0; lane < 4; lane++)
            {
                if (weights[lane] > 0 && (uint)indices[lane] >= boneCount) throw new InvalidDataException("A weighted joint is outside the package-order skeleton.");
                result[vertex * 8 + lane] = checked((byte)encoded[lane]);
                result[vertex * 8 + 4 + lane] = weights[lane] > 0 ? checked((byte)indices[lane]) : (byte)0;
            }
        }
        return result;
    }

    private static bool Profile(NativeMeshData native) => native.InputLayoutVerified && native.NativeDeclarationId == 16
        && native.SkinWeightStream is null && native.VertexStreams.Length == 2
        && native.VertexStreams[0].Stride == VertexStreamCodec.SkinPositionStride
        && native.VertexStreams[1].Stride == VertexStreamCodec.AttributeStride
        && native.VertexStreams.All(stream => stream.Type == 0)
        && native.InputElements.SequenceEqual(new NativeInputElement[]
        {
            new("POSITION", 0, 0, 0, NativeVertexFormat.R16G16B16A16SNorm),
            new("BLENDWEIGHT", 0, 0, 8, NativeVertexFormat.R8G8B8A8UNorm),
            new("BLENDINDICES", 0, 0, 12, NativeVertexFormat.R8G8B8A8UInt),
            new("TEXCOORD", 0, 1, 0, NativeVertexFormat.R16G16SNorm),
            new("NORMAL", 0, 1, 4, NativeVertexFormat.R16G16B16A16SNorm),
            new("TANGENT", 0, 1, 12, NativeVertexFormat.R16G16B16A16SNorm)
        });
    private static bool Close(Matrix4x4 left, Matrix4x4 right)
    {
        float[] a = [left.M11,left.M12,left.M13,left.M14,left.M21,left.M22,left.M23,left.M24,left.M31,left.M32,left.M33,left.M34,left.M41,left.M42,left.M43,left.M44];
        float[] b = [right.M11,right.M12,right.M13,right.M14,right.M21,right.M22,right.M23,right.M24,right.M31,right.M32,right.M33,right.M34,right.M41,right.M42,right.M43,right.M44];
        return a.Zip(b).All(pair => float.IsFinite(pair.First) && MathF.Abs(pair.First - pair.Second) <= BindTolerance);
    }
    private static PackageEntry Require(PackageCatalog catalog, uint tag) => catalog.TryGetEntry(tag, out var entry) ? entry : throw new InvalidDataException("Skin template entry is missing.");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /** Row offsets of an array field inside a struct read through a view. */
    private static int[] Rows<T>(ReadOnlySpan<byte> tag, ref readonly TagArray<T> field, uint elementClass) where T : unmanaged =>
        Rows<T>(tag, TagView.OffsetOf(tag, in field), elementClass);

    /**
     * Row offsets of the array field at `field`, whose owner need not fit the tag. A zero count is
     * empty and needs only the count word; anything else is refused as TagArray.Checked refuses.
     */
    private static int[] Rows<T>(ReadOnlySpan<byte> tag, int field, uint elementClass) where T : unmanaged
    {
        if (field < 0 || field > tag.Length - sizeof(long)) throw new InvalidDataException("A typed native array is truncated.");
        if (TagRead.I64(tag, field) == 0) return [];
        if (!TagView.Fits<TagArray<T>>(tag, field)) throw new InvalidDataException("A typed native array is truncated.");
        int stride = Unsafe.SizeOf<T>();
        if (!TagArray.TryChecked(tag, field, elementClass, stride, out var array, TagArray.MaximumReaderRows))
            throw new InvalidDataException("A typed native array is invalid.");
        return Enumerable.Range(0, array.Count).Select(index => array.Data + index * stride).ToArray();
    }

    /** @return The target of the pointer field at `field`; a null pointer names the field itself. */
    private static int PointerTarget(ReadOnlySpan<byte> tag, int field) =>
        TagView.TargetOrSelf(tag, in TagView.At<RelativePointer<Unknown>>(tag, field));

    /** Writes one member of the T at `owner`, where the whole T need not fit. */
    private static void Put<T, TField>(byte[] bytes, int owner, string member, TField value)
        where T : unmanaged where TField : unmanaged => MemoryMarshal.Write(bytes.AsSpan(owner + TagView.FieldOffset<T>(member)), in value);
}
