using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

public sealed partial class AssetSceneLoader
{
    private readonly Dictionary<(uint Model, uint Config), List<(uint[] Materials, MeshData[] Meshes)>> variantMeshes = [];
    private sealed record InitialModelState(uint[] Materials, EntityInitialState Initial, bool MaterialsKnown);
    private readonly Dictionary<(uint Entity, uint Config, uint Source, int Offset, uint Variant, string Pairs), InitialModelState>
        initialMaterials = [];
    private readonly HashSet<EntityMaterialWarning> initialWarnings = [];

    /**
     * Entity geometry is a bind-pose preview; skinning is not evaluated.
     * @param external A part with group index v uses external[v] before its direct material.
     */
    private MeshData[] EntityMesh(uint tag, uint[]? external = null, uint configTag = 0)
    {
        if (external is { Length: > 0 })
        {
            if (!variantMeshes.TryGetValue((tag, configTag), out var variants))
                variantMeshes[(tag, configTag)] = variants = [];
            foreach (var variant in variants)
                if (variant.Materials.AsSpan().SequenceEqual(external)) return variant.Meshes;
            var decoded = DecodeEntityMesh(tag, external);
            variants.Add((external.ToArray(), decoded));
            return decoded;
        }
        if (meshes.TryGetValue(tag, out var cached)) return cached;
        return meshes[tag] = DecodeEntityMesh(tag, null);
    }

    /**
     * Clip label shared by the Animations tab and bundle exports.
     * @param catalog Supplies the package name.
     * @param clipTag Clip to name.
     * @param slot Clip index in its animation definition; used when no name is known.
     * @param names Caller's tag names; asked before the package name.
     * @param recoveredName Exact hash match from this definition's referenced clip.
     * @return The first name found, else "Clip N".
     */
    public static string ClipName(PackageCatalog catalog, uint clipTag, int slot,
        Func<uint, string?>? names = null, string? recoveredName = null) =>
        names?.Invoke(clipTag) ?? catalog.GetName(clipTag) ?? recoveredName ?? $"Clip {slot}";

    /** Model and cloth resources share this map; each row yields its first material only. */
    private static uint[] ExternalMaterials(byte[] config, int resource)
    {
        if (!TagView.FitsThrough<SModelComponent>(config, resource,
                $"{nameof(SModelComponent.HF5b4dbc0)}.{nameof(C808072c1.Materials)}"))
            return [];
        try
        {
            int block = resource + TagView.FieldOffset<SModelComponent>(nameof(SModelComponent.HF5b4dbc0));
            var materialRows = EntityMaterialSelector.MapRows<TagReferenceElement>(config, block, nameof(C808072c1.Materials),
                TagReferenceElement.Class);
            var materials = new uint[materialRows.Length];
            for (int i = 0; i < materials.Length; i++) materials[i] = materialRows[i].Tag;
            var groups = EntityMaterialSelector.MapRows<SExternalMaterialGroup>(config, block, nameof(C808072c1.MaterialGroups),
                SExternalMaterialGroup.Class);
            var result = new uint[groups.Length];
            for (int i = 0; i < groups.Length; i++)
            {
                int count = groups[i].Count, start = groups[i].First;
                result[i] = count > 0 && start >= 0 && start < materials.Length ? materials[start] : uint.MaxValue;
            }
            return result;
        }
        catch (InvalidDataException) { return []; }
    }

    /** Each spawn row may supply different initial filters for a shared model component. */
    private InitialModelState InitialMaterials(uint entity, uint configTag, byte[] config, int resource,
        SpawnInput? spawn, uint variant, IReadOnlyList<(uint Key, uint Value)> pairs, IReadOnlyDictionary<uint, uint> picks)
    {
        var key = (entity, configTag, spawn?.Tag ?? 0, spawn?.Offset ?? -1, variant,
            string.Join(",", pairs.Concat(picks.Select(pick => (pick.Key, pick.Value))).Select(pair => $"{pair.Key:X8}={pair.Value:X8}")));
        if (initialMaterials.TryGetValue(key, out var cached)) return cached;
        uint[] materials;
        string? unresolved;
        EntityInitialState initial = new(null, null);
        try
        {
            initial = new EntityInitialStateReader(catalog) { Reads = scene.ReadTagSet }
                .Read(entity, configTag, key.Item3, key.Item4, variant, pairs, picks);
            var selected = EntityMaterialSelector.Read(config, resource, initial.EnabledParts, initial.Unresolved);
            materials = selected.Materials;
            unresolved = selected.Unresolved ?? initial.Unresolved;
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException
            or OverflowException or IndexOutOfRangeException or KeyNotFoundException)
        {
            materials = ExternalMaterials(config, resource);
            unresolved = error.Message;
        }
        if (unresolved is not null) InitialWarning(entity, configTag, spawn, unresolved);
        return initialMaterials[key] = new(materials, initial, unresolved is null);
    }

    private void InitialWarning(uint entity, uint config, SpawnInput? spawn, string reason)
    {
        string message = $"Model preview for entity 0x{entity:X8}, component 0x{config:X8} uses an authored fallback: {reason}";
        AddDiagnostic(message);
        var warning = new EntityMaterialWarning(entity, config, reason, spawn?.Tag ?? 0, spawn?.Offset ?? -1);
        if (initialWarnings.Add(warning)) scene.MaterialWarnings.Add(warning);
    }

    private MeshData[] DecodeEntityMesh(uint tag, uint[]? external)
    {
        var data = ReadBytes(tag);
        var result = new List<MeshData>();
        var scale = TagView.Member<SEntityModel, Vector3>(data, 0, nameof(SEntityModel.PositionScale));
        var translation = TagView.Member<SEntityModel, Vector3>(data, 0, nameof(SEntityModel.PositionOffset));
        foreach (ref readonly var mesh in EntityFields.Rows<SEntityModel, SEntityModelMesh>(data, 0,
            nameof(SEntityModel.Meshes), SEntityModelMesh.Class))
        {
            var raw = ReadBuffer(mesh.VertexBuffer0[0].Unk0, 4, out var header);
            var stride = TagView.Member<VertexBufferHeader, ushort>(header, 0, nameof(VertexBufferHeader.Stride));
            var byteCount = TagView.Member<VertexBufferHeader, uint>(header, 0, nameof(VertexBufferHeader.ByteCount));
            // The authored declaration names the position format; without one, stride 48 holds floats.
            var declared = DeclaredPositionFormat(in mesh);
            bool floats = declared is null ? stride == 48
                : declared is NativeVertexFormat.R32G32B32A32Float or NativeVertexFormat.R32G32B32Float;
            if (declared is { } format && !floats && format != NativeVertexFormat.R16G16B16A16SNorm)
                throw new InvalidDataException($"Entity position format {format} is not supported.");
            if (declared is null && stride is not (8 or 12 or 16 or 20 or 24 or 28 or 32 or 48)
                || stride < (floats ? 12 : 8) || byteCount > raw.Length)
                throw new InvalidDataException($"Entity position stride {stride} is not supported.");
            var all = new SceneVertex[byteCount / stride];
            for (var i = 0; i < all.Length; ++i)
            {
                var position = floats ? GpuBufferData.Float3(raw, i * stride) : GpuBufferData.Snorm3(raw, i * stride);
                all[i] = new SceneVertex(position * scale + translation, Vector3.UnitZ, Vector2.Zero, Vector4.One);
            }
            var extra = mesh.VertexBuffer0[1].Unk0;
            if (catalog.TryGetEntry(extra, out _)) VertexTexcoords(extra, all, data);
            var seen = new HashSet<(uint First, uint Count, ushort Topology, short LogicalPart, bool Hidden)>();
            var partRows = EntityFields.Rows(data, in mesh.Parts, SEntityModelPart.Class);
            for (var partOrdinal = 0; partOrdinal < partRows.Length; ++partOrdinal)
            {
                ref readonly var part = ref partRows[partOrdinal];
                var lod = LodLevel(part.LodCategory);
                if (lod < 0 || lod > 0 && !IncludeLowerLods) continue;
                var first = part.IndexStart;
                var count = part.IndexCount;
                var topology = part.PrimitiveType3Written;
                bool hidden = (part.PartFlags & MeshData.HiddenPartFlag) != 0;
                if (!seen.Add((first, count, topology, part.LogicalPartIndex, hidden))) continue;
                var indices = IndexData(mesh.IndexBuffer, first, count, checked((byte)topology), all.Length);
                var vertices = all;
                var sourceIndices = Compact(ref vertices, indices);
                GenerateNormals(vertices, indices);
                var stage = -1;
                ReadOnlySpan<ushort> boundaries = mesh.StageIndexBoundaries;
                for (var candidate = 0; candidate < boundaries.Length - 1; ++candidate)
                    if (partOrdinal >= boundaries[candidate] && partOrdinal < boundaries[candidate + 1])
                    { stage = candidate; break; }
                var declaration = stage < 0 ? -1 : mesh.StageVertexDeclaration[stage];
                var material = part.Material;
                var variantIndex = part.ExternalMaterialGroup1Direct;
                if (variantIndex != -1)
                    material = external is not null && variantIndex >= 0 && variantIndex < external.Length
                        ? external[variantIndex] : uint.MaxValue;
                var decoded = new MeshData
                {
                    TagHash = tag,
                    Vertices = vertices,
                    Indices = indices,
                    MaterialTag = material,
                    PartIndex = TagView.OffsetOf(data, in part),
                    LogicalPartIndex = part.LogicalPartIndex,
                    PartFlags = part.PartFlags,
                    LodLevel = lod,
                    SourceVertexIndices = sourceIndices,
                    Native = NativeMesh(NativeMeshKind.Entity, data, material, mesh.VertexBuffer0[0].Unk0, extra,
                        mesh.IndexBuffer, first, count, checked((byte)topology), weights: mesh.VertexBuffer0[2].Unk0,
                        declarationId: declaration, renderStage: stage)
                };
                ApplyNativeTexcoords(decoded);
                result.Add(decoded);
            }
        }
        return result.ToArray();
    }

    /** The POSITION format of the mesh's first authored declaration; null when none resolves. */
    private NativeVertexFormat? DeclaredPositionFormat(ref readonly SEntityModelMesh mesh)
    {
        ReadOnlySpan<short> declarations = mesh.StageVertexDeclaration;
        for (var stage = 0; stage < declarations.Length; ++stage)
        {
            int id = declarations[stage];
            if (id < 0) continue;
            try
            {
                nativeLayouts ??= new NativeLayoutCatalog(catalog);
                if (!nativeLayouts.TryGet(id, out var elements, out _)) return null;
                return elements.FirstOrDefault(e => e is { Semantic: "POSITION", SemanticIndex: 0, Slot: 0, Offset: 0 })?.Format;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or OverflowException)
            {
                return null;
            }
        }
        return null;
    }

    /** Sky records store a full row-vector matrix before their model wrapper. */
    private void SkyEntities(uint tag, byte[] data, uint bubble, Matrix4x4 parent)
    {
        foreach (ref readonly var row in EntityFields.Rows<SSkyObjects, UnknownSkyEntry>(data, 0,
            nameof(SSkyObjects.Placements), UnknownSkyEntry.Class))
        {
            var wrapper = row.ModelWrapper;
            if (Class(wrapper) != SSkyModelWrapper.Class) throw new InvalidDataException("Sky model wrapper class differs from build 86657.");
            var model = TagView.Member<SSkyModelWrapper, uint>(ReadBytes(wrapper), 0, nameof(SSkyModelWrapper.Model));
            if (Class(model) != SEntityModel.Class) throw new InvalidDataException("Sky resource does not reference an entity model.");
            var matrix = row.Transform;
            var objectPlacement = new SceneObjectPlacement(tag, TagView.OffsetOf(data, in row), matrix * parent);
            foreach (var mesh in EntityMesh(model))
                scene.Instances.Add(new MeshInstance
                {
                    Mesh = mesh,
                    ObjectPlacement = objectPlacement,
                    Transform = matrix * parent,
                    Name = Name(model),
                    BubbleHash = bubble,
                    Layer = SceneLayer.Skybox,
                    InitiallyVisible = mesh.NeverDrawn ? false : null,
                    InitialVisibilityReason = mesh.NeverDrawn ? MeshData.HiddenPartReason : null
                });
        }
    }

    /** Records the opened entity's inputs; each spawn entry names the variant it selects. */
    private void DescribePreviewInputs(uint entity, uint configTag, EntityInitialState initial, SpawnInput? spawn,
        uint variant)
    {
        scene.PreviewSpawn = spawn;
        scene.EntityParameters = initial.Parameters;
        scene.PreviewParameters = options.PreviewParameters;
        if (initial.Variants.Count != 0)
        {
            scene.EntityVariants = initial.Variants;
            scene.PreviewVariant = variant;
        }
        if (scene.SpawnChoices.Count != 0 || options.AuthoredSpawns.Count == 0) return;
        var reader = new EntityInitialStateReader(catalog) { Reads = scene.ReadTagSet };
        scene.SpawnChoices = options.AuthoredSpawns.Select(authored =>
        {
            var state = reader.Read(entity, configTag, authored.First.Tag, authored.First.Offset);
            return new SpawnChoice(authored.First, authored.RowCount, state.SelectedVariant, state.Unresolved);
        }).ToArray();
    }

    /** Entity model links are accepted only behind their typed resource marker. */
    private void ClassMeshes(uint classTag, uint bubble, Matrix4x4 transform,
        SceneMarker? placement = null)
    {
        var initialCount = scene.Instances.Count;
        var objectPlacement = placement?.ObjectPlacement ?? new SceneObjectPlacement(classTag, -1, transform);
        var definition = ReadBytes(classTag);
        foreach (ref readonly var build in EntityFields.Rows<SEntity, SPatternComponent>(definition, 0,
            nameof(SEntity.Components), SPatternComponent.Class))
        {
            var configTag = build.Config;
            if (Class(configTag) != SEntityConfig.Class) continue;
            var config = ReadBytes(configTag);
            // A null link has no model resource.
            if (TagView.TargetOffset(config, in TagView.Member<SEntityConfig, RelativePointer<Unknown>>(config, 0,
                    nameof(SEntityConfig.Definition))) is not { } resource) continue;
            var resourceClass = TagView.ClassAt(config, resource) ?? 0;
            bool cloth = resourceClass == SClothModelComponent.Class;
            if (!cloth && resourceClass != SModelComponent.Class
                || !ModelComponent.FitsThrough(config, resource, cloth, nameof(SModelComponent.Model))) continue;
            if (cloth)
            {
                // A null link has no primary component.
                if (TagView.TargetOffset(config, in TagView.Member<SEntityConfig, RelativePointer<Unknown>>(config, 0,
                        nameof(SEntityConfig.DefaultInstance))) is not { } primary
                    || primary < TagView.ClassWordSize
                    || !TagView.FitsThrough<SClothModelPrimary>(config, primary, nameof(SClothModelPrimary.OwnerPairConfig))
                    || TagView.ClassAt(config, primary) != SClothModelPrimary.Class)
                    throw new InvalidDataException("Cloth model component has the wrong primary class.");
                CheckComponentPair(configTag, config, primary, resource, SClothModelPrimary.Class, SClothModelComponent.Class);
            }
            if (ModelComponent.At<uint>(config, resource, cloth, ModelComponent.OwnerTag) != configTag)
                throw new InvalidDataException("Entity model component has the wrong owner tag.");
            var model = ModelComponent.At<uint>(config, resource, cloth, nameof(SModelComponent.Model));
            if (Class(model) != SEntityModel.Class) continue;
            // Only the opened entity takes the preview inputs; a placed entity reads its own row.
            bool root = placement is null && classTag == scene.TagHash;
            SpawnInput? spawn = root ? options.PreviewSpawn
                : placement is null ? null : new SpawnInput(placement.SourceTag, placement.SourceOffset);
            uint variant = root ? options.PreviewVariant : 0;
            var state = InitialMaterials(classTag, configTag, config, resource, spawn, variant, root ? options.SpawnPairs : [],
                root ? options.PreviewParameters : new Dictionary<uint, uint>());
            if (root) DescribePreviewInputs(classTag, configTag, state.Initial, spawn, variant);
            if (root && resourceClass == SModelComponent.Class) DescribeObjectChannels(classTag, configTag, config, resource);
            if (state.Initial.DisabledByEveryVariant is not null)
            {
                string message = $"Entity 0x{classTag:X8}: no variant with positive weight; variant filter skipped"
                    + (root ? ". Pick another input in Parts." : "");
                AddDiagnostic(message);
            }
            var clothParts = cloth ? ClothDrawnParts(config, resource, model) : null;
            foreach (var mesh in EntityMesh(model, state.Materials, configTag))
            {
                bool? visible = state.Initial.Enabled(mesh.LogicalPartIndex);
                string? reason = visible == false ? MeshData.InactiveInitialPartReason : visible is null ? state.Initial.Unresolved
                    : state.Initial.EveryVariantDisables(mesh.LogicalPartIndex)
                        ? "Every variant disables this part"
                        : null;
                if (visible is null && reason is null)
                {
                    reason = "Part index outside its bitset";
                    InitialWarning(classTag, configTag, spawn, reason);
                }
                int group = TagView.Member<SEntityModelPart, short>(ReadBytes(model), mesh.PartIndex,
                    nameof(SEntityModelPart.ExternalMaterialGroup1Direct));
                if (mesh.NeverDrawn) { visible = false; reason = MeshData.HiddenPartReason; }
                else if (clothParts is not null && mesh.Native is { } native
                    && !clothParts.Contains((native.RenderStage, native.FirstIndex, native.IndexCount)))
                { visible = false; reason = MeshData.ClothSetPartReason; }
                else if (mesh.MaterialTag == uint.MaxValue && (group == -1 || state.MaterialsKnown
                    && group >= 0 && group < state.Materials.Length))
                { visible = false; reason = "No active material"; }
                scene.Instances.Add(new MeshInstance
                {
                    Mesh = mesh,
                    ObjectPlacement = objectPlacement,
                    Transform = transform,
                    Name = placement?.Name ?? Name(classTag),
                    BubbleHash = bubble,
                    Layer = SceneLayer.Objects,
                    ObjectClassTag = classTag,
                    ModelSourceConfigTag = configTag,
                    ModelSourceResourceClass = resourceClass,
                    InitiallyVisible = visible,
                    InitialVisibilityReason = reason,
                    PlacementSourceTag = placement?.SourceTag ?? 0,
                    PlacementSourceOffset = placement?.SourceOffset ?? -1,
                    PlacementIdentity = placement?.PlacementIdentity ?? 0,
                    MapContainers = placement?.MapContainers ?? []
                });
            }
            if (cloth) AddDiagnostic($"Cloth model 0x{model:X8} is drawn without cloth simulation.");
        }
        if (placement == null) ReadClassVolumes(classTag, bubble, Name(classTag));
        if (placement == null && scene.Instances.Count == initialCount)
        {
            scene.UnsupportedCount++;
            scene.Diagnostics.Add($"No supported mesh or trigger resource in class {Name(classTag)}.");
        }
    }

    /**
     * The opened entity's object channels: each reads the user's value, else its rest value.
     * The first model config with channels takes the user's values; another model keeps its rest values.
     */
    private void DescribeObjectChannels(uint entity, uint configTag, byte[] config, int resource)
    {
        uint[] names;
        try { names = ObjectChannelRest.RequestNames(config, resource); }
        catch (InvalidDataException error) { AddDiagnostic($"Object channels of 0x{configTag:X8}: {error.Message}"); return; }
        if (names.Length == 0) return;
        if (scene.ObjectChannels.Count != 0)
        {
            if (!scene.ObjectChannels.Select(channel => channel.NameHash).SequenceEqual(names))
                AddDiagnostic($"Entity 0x{entity:X8}: model 0x{configTag:X8} names other object channels; it draws with its rest values.");
            return;
        }
        var rest = ChannelRestValues(entity, names);
        scene.ChannelValues = options.ChannelValues;
        scene.ObjectChannels = names.Select((name, index) => new SceneObjectChannel(index, name,
            options.ChannelValues.TryGetValue(name, out float value) ? new Vector4(value) : rest[index].Value, rest[index])).ToArray();
    }

    /** Each named channel's value at rest, read from the value provider and publishers among the entity's components. */
    private ObjectChannelRestValue[] ChannelRestValues(uint entity, uint[] names)
    {
        try
        {
            var definition = ReadBytes(entity);
            var components = EntityFields.Rows<SEntity, SPatternComponent>(definition, 0, nameof(SEntity.Components),
                SPatternComponent.Class);
            var tags = new uint[components.Length];
            for (int i = 0; i < tags.Length; i++) tags[i] = components[i].Config;
            var configs = tags.Where(tag => Class(tag) == SEntityConfig.Class).Select(tag => (tag, ReadBytes(tag)));
            return ObjectChannelRest.RestValues(configs, names);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        {
            AddDiagnostic($"Entity 0x{entity:X8}: object channel rest values unread: {error.Message}");
            return names.Select(_ => ObjectChannelRestValue.Open("value provider unreadable")).ToArray();
        }
    }

    private const string ClothSetZero = $"{nameof(SSkeletonAsset.HC250af49)}.{nameof(C80807278.ClothDrawSets)}[0]";

    /**
     * The parts the cloth draw 0x7FF742E78D60 takes: set 0's list for each stage, as indices from the stage's first part.
     * The set is the draw data's first int, which 0x7FF742E5B480 leaves zero.
     * @return Stage and index range of each listed part; null when the asset or model has another layout.
     */
    private HashSet<(int Stage, uint First, uint Count)>? ClothDrawnParts(byte[] config, int resource, uint modelTag)
    {
        if (!TagView.FitsThrough<SClothModelComponent>(config, resource, nameof(SClothModelComponent.HavokClothAsset))) return null;
        uint assetTag = TagView.Member<SClothModelComponent, uint>(config, resource, nameof(SClothModelComponent.HavokClothAsset));
        if (Class(assetTag) != SSkeletonAsset.Class) return null;
        var asset = ReadBytes(assetTag);
        var model = ReadBytes(modelTag);
        var meshes = EntityFields.Rows<SEntityModel, SEntityModelMesh>(model, 0, nameof(SEntityModel.Meshes), SEntityModelMesh.Class);
        if (meshes.Length != 1 || !TagView.FitsThrough<SSkeletonAsset>(asset, 0, ClothSetZero)) return null;
        var parts = EntityFields.Rows(model, in meshes[0].Parts, SEntityModelPart.Class);
        ref readonly var set = ref TagView.Member<SSkeletonAsset, G808072780>(asset, 0, ClothSetZero);
        ReadOnlySpan<G7f00061410> lists = set.StagePartLists;
        var drawn = new HashSet<(int, uint, uint)>();
        for (int stage = 0; stage < lists.Length; stage++)
        {
            int start = meshes[0].StageIndexBoundaries[stage], end = meshes[0].StageIndexBoundaries[stage + 1];
            foreach (ref readonly var row in EntityFields.Rows(asset, in lists[stage].PartIndices, SI32Element.Class))
            {
                int part = start + row.Value;
                if (part < start || part >= end || part >= parts.Length)
                    throw new InvalidDataException($"Cloth asset 0x{assetTag:X8} lists a part outside stage {stage}.");
                drawn.Add((stage, parts[part].IndexStart, parts[part].IndexCount));
            }
        }
        return drawn.Count == 0 ? null : drawn;
    }
}

/** Model and cloth model components share these member names; the class picks the layout. */
file static class ModelComponent
{
    public const string OwnerTag = $"{nameof(SModelComponent.OwnerConfig)}.{nameof(TagOffsetPointer.Tag)}";

    public static bool FitsThrough(ReadOnlySpan<byte> tag, int resource, bool cloth, string member) =>
        cloth ? TagView.FitsThrough<SClothModelComponent>(tag, resource, member)
            : TagView.FitsThrough<SModelComponent>(tag, resource, member);

    public static ref readonly M At<M>(ReadOnlySpan<byte> tag, int resource, bool cloth, string member) where M : unmanaged =>
        ref cloth ? ref TagView.Member<SClothModelComponent, M>(tag, resource, member)
            : ref TagView.Member<SModelComponent, M>(tag, resource, member);
}
