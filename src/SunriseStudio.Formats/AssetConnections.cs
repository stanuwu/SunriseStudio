using System.Runtime.CompilerServices;
using SunriseStudio.Animation;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

public enum AssetRelationshipKind
{
    Component, Model, Material, Texture, Shader, Buffer, Data, Skeleton,
    AnimationDefinition, AnimationClip, Owner, Attachment, Source, Reference
}

public sealed record RelatedAsset(uint TagHash, string Name, AssetRelationshipKind Kind, string Role,
    uint SourceTag, int SourceOffset, AssetOpenContext? Context = null, bool IsCandidate = false);

public sealed record AssetConnections(uint AssetTag, uint PreviewTag, uint AnimationEntityTag,
    uint SelectedClipTag, IReadOnlyList<RelatedAsset> Related, IReadOnlyList<uint> EntityCandidates,
    IReadOnlyList<string> Diagnostics);

/** The package links of one asset, read once. Complete adds the preview scene and the inspection. */
public sealed class AssetConnectionDraft
{
    internal AssetConnectionDraft(uint tag, AssetOpenContext? context, Func<uint, string?>? resolveName, bool hasOwner,
        uint previewTag, uint animationEntityTag, uint selectedClipTag, List<RelatedAsset> links,
        HashSet<uint> entityCandidates, List<string> diagnostics)
    {
        Tag = tag; Context = context; ResolveName = resolveName; HasOwner = hasOwner;
        PreviewTag = previewTag; AnimationEntityTag = animationEntityTag; SelectedClipTag = selectedClipTag;
        Links = links; EntityCandidates = entityCandidates; Diagnostics = diagnostics;
    }

    public uint Tag { get; }
    public uint PreviewTag { get; }
    public uint AnimationEntityTag { get; }
    public uint SelectedClipTag { get; }
    internal AssetOpenContext? Context { get; }
    internal Func<uint, string?>? ResolveName { get; }
    internal bool HasOwner { get; }
    internal IReadOnlyList<RelatedAsset> Links { get; }
    internal IReadOnlySet<uint> EntityCandidates { get; }
    internal IReadOnlyList<string> Diagnostics { get; }
}

/** Typed package edges define navigation; shared owners never choose an animation rig implicitly. */
public sealed class AssetConnectionReader(PackageCatalog catalog)
{
    /** Physical reference rows preserve repeated targets at distinct native offsets. */
    public IReadOnlyList<RelatedAsset> ReadNativeReferences(uint tag, CancellationToken token = default) =>
        ReadDirect(tag, null, token);

    private const uint EntityClass = SEntity.Class, ConfigClass = SEntityConfig.Class, ModelClass = SEntityModel.Class;
    private sealed record ModelLink(uint Config, uint Model, int Offset);
    private sealed record EntityLinks(uint Tag, uint[] Configs, ModelLink[] Models,
        EntityAnimationReference[] Clips, uint[] Skeletons);

    /** Owner tags are hints from an existing index, not permission to choose the first matching entity. */
    public AssetConnections Read(uint tag, AssetOpenContext? context = null, SceneDocument? previewScene = null,
        Func<uint, string?>? resolveName = null, IEnumerable<uint>? ownerTags = null, CancellationToken token = default,
        AssetInspection? inspection = null) =>
        Complete(ReadDraft(tag, context, resolveName, ownerTags, token), previewScene, inspection, token);

    /** Reads every link that does not need the preview scene; PreviewTag names the scene to load. */
    public AssetConnectionDraft ReadDraft(uint tag, AssetOpenContext? context = null,
        Func<uint, string?>? resolveName = null, IEnumerable<uint>? ownerTags = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!catalog.TryGetEntry(tag, out var entry)) throw new KeyNotFoundException($"Asset {tag:X8} is not installed.");
        var links = new List<RelatedAsset>();
        var diagnostics = new List<string>();
        var entities = new Dictionary<uint, EntityLinks>();
        var entityCandidates = new HashSet<uint>();
        uint preview = tag, animationEntity = 0, selectedClip = 0;
        EntityLinks Entity(uint candidate)
        {
            token.ThrowIfCancellationRequested();
            if (!entities.TryGetValue(candidate, out var found)) entities[candidate] = found = ReadEntity(candidate, token);
            return found;
        }
        string Name(uint target) => NameOf(target, resolveName);
        void Add(uint target, AssetRelationshipKind kind, string role, uint sourceTag, int offset,
            AssetOpenContext? navigation = null, bool candidate = false) =>
            AddLink(links, tag, resolveName, target, kind, role, sourceTag, offset, navigation ?? context, candidate);
        void Safe(Action action)
        {
            try { action(); }
            catch (Exception error) when (Recoverable(error)) { diagnostics.Add(error.Message); }
        }

        EntityLinks? owner = null;
        if (Class(tag) == EntityClass) Safe(() => owner = Entity(tag));
        else if (Class(tag) is ModelClass or ConfigClass or SAnimationClip.Class or SAnimationDefinition.Class
            && context?.EntityTag is > 0 and var contextEntity && Class(contextEntity) == EntityClass)
        {
            Safe(() =>
            {
                var found = Entity(contextEntity);
                bool linked = found.Models.Any(model => model.Model == tag && (context.ConfigTag == 0 || model.Config == context.ConfigTag))
                    || entry.Reference == ConfigClass && found.Configs.Contains(tag)
                    || entry.Reference == SAnimationClip.Class && found.Clips.Any(clip => clip.ClipTag == tag)
                    || entry.Reference == SAnimationDefinition.Class && found.Clips.Any(clip => clip.DefinitionTag == tag);
                if (linked) owner = found;
                else diagnostics.Add("Owner: no typed link to this asset");
            });
        }
        foreach (uint candidate in (ownerTags ?? []).Distinct())
        {
            token.ThrowIfCancellationRequested();
            if (Class(candidate) == EntityClass)
                Safe(() =>
                {
                    var found = Entity(candidate);
                    if (found.Models.Any(model => model.Model == tag) || found.Configs.Contains(tag)
                        || found.Clips.Any(clip => clip.ClipTag == tag || clip.DefinitionTag == tag))
                    {
                        entityCandidates.Add(candidate);
                        Add(candidate, AssetRelationshipKind.Owner, "Entity owner", candidate, -1, new(candidate));
                    }
                });
            else Safe(() =>
            {
                var reverse = ReadDirect(candidate, null, token);
                if (reverse.Any(edge => edge.TagHash == tag))
                {
                    Add(candidate, AssetRelationshipKind.Owner, "Used by", candidate, -1);
                }
            });
        }
        if (owner is not null)
        {
            context = (context ?? new()) with { EntityTag = owner.Tag };
            entityCandidates.Add(owner.Tag);
            if (entry.Reference is EntityClass or ModelClass or ConfigClass or SAnimationClip.Class or SAnimationDefinition.Class)
                preview = owner.Tag;
            if (owner.Skeletons.Length != 0) animationEntity = owner.Tag;
            if (entry.Reference == SAnimationClip.Class && owner.Clips.Any(clip => clip.ClipTag == tag)) selectedClip = tag;
            Add(owner.Tag, AssetRelationshipKind.Owner, "Entity owner", owner.Tag, -1, new(owner.Tag));
            foreach (var model in owner.Models)
                Add(model.Model, AssetRelationshipKind.Model, "Model", model.Config, model.Offset,
                    context with { ModelTag = model.Model, ConfigTag = model.Config });
            foreach (uint skeleton in owner.Skeletons)
                Add(skeleton, AssetRelationshipKind.Skeleton, "Skeleton", owner.Tag, -1, context);
            foreach (var clip in owner.Clips)
            {
                Add(clip.DefinitionTag, AssetRelationshipKind.AnimationDefinition, "Animation definition", clip.ConfigTag, -1, context);
                Add(clip.ClipTag, AssetRelationshipKind.AnimationClip, $"Clip {clip.Index}", clip.DefinitionTag, -1, context);
            }
            if (entry.Reference == EntityClass)
                Safe(() =>
                {
                    var attachments = new AssetSceneLoader(catalog).DescribeEntityAttachments(owner.Tag);
                    // SourceOffset is a weapon reference row; the link names its entity field.
                    int entityField = TagView.FieldOffset<UnknownWeaponReference>("WeaponEntity");
                    foreach (var weapon in attachments.Loadouts.SelectMany(loadout => loadout.Weapons))
                        Add(weapon.EntityTag, AssetRelationshipKind.Attachment, "Weapon option", weapon.ConfigTag,
                            weapon.SourceOffset + entityField, new(weapon.EntityTag));
                });
        }
        Safe(() => links.AddRange(ReadDirect(tag, context, token).Select(edge => edge with { Name = Name(edge.TagHash) })));
        if (entry.Reference == ConfigClass && owner is null)
            Safe(() => { if (ReadModelConfig(tag) is { } model) preview = model.Model; });
        return new(tag, context, resolveName, owner is not null, preview, animationEntity, selectedClip,
            links, entityCandidates, diagnostics);
    }

    /** Adds the preview scene's links and the inspection's references. The draft is not changed. */
    public AssetConnections Complete(AssetConnectionDraft draft, SceneDocument? previewScene = null,
        AssetInspection? inspection = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        uint tag = draft.Tag;
        var links = draft.Links.ToList();
        var diagnostics = draft.Diagnostics.ToList();
        void Add(uint target, AssetRelationshipKind kind, string role, uint sourceTag, int offset, bool candidate = false) =>
            AddLink(links, tag, draft.ResolveName, target, kind, role, sourceTag, offset, draft.Context, candidate);
        if (previewScene is not null)
            foreach (var mesh in previewScene.Instances.Select(instance => instance.Mesh).Distinct())
            {
                token.ThrowIfCancellationRequested();
                Add(mesh.TagHash, AssetRelationshipKind.Model, "Preview model", tag, -1);
                Add(mesh.MaterialTag, AssetRelationshipKind.Material, "Material", mesh.TagHash, mesh.PartIndex);
                if (mesh.Native is not { } native) continue;
                foreach (var stream in native.VertexStreams)
                    Add(stream.HeaderTag, AssetRelationshipKind.Buffer, "Vertex buffer", mesh.TagHash, -1);
                Add(native.IndexHeaderTag, AssetRelationshipKind.Buffer, "Index buffer", mesh.TagHash, -1);
            }
        if (!draft.HasOwner && draft.EntityCandidates.Count != 0)
            diagnostics.Add("Animations: open an entity owner");
        if (inspection is not null)
            foreach (var reference in inspection.References)
                if (!links.Any(link => link.TagHash == reference.TagHash && !link.IsCandidate))
                    Add(reference.TagHash, AssetRelationshipKind.Reference,
                        reference.IsCandidate ? "Possible reference" : "Reference", tag, reference.Offset,
                        candidate: reference.IsCandidate);
        return new(tag, draft.PreviewTag, draft.AnimationEntityTag, draft.SelectedClipTag,
            links.DistinctBy(link => (link.TagHash, link.Kind, link.Role, link.Context))
                .OrderBy(link => link.Kind).ThenBy(link => link.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            draft.EntityCandidates.Order().ToArray(), diagnostics.Distinct().ToArray());
    }

    private string NameOf(uint target, Func<uint, string?>? resolveName) =>
        resolveName?.Invoke(target) ?? catalog.GetName(target)
            ?? (catalog.TryGetEntry(target, out var named) ? AssetCategories.CategoryOf(named) : "Asset");

    private void AddLink(List<RelatedAsset> links, uint tag, Func<uint, string?>? resolveName, uint target,
        AssetRelationshipKind kind, string role, uint sourceTag, int offset, AssetOpenContext? context, bool candidate)
    {
        if (target == tag || target is 0 or uint.MaxValue || !catalog.ContainsTag(target)) return;
        links.Add(new(target, NameOf(target, resolveName), kind, role, sourceTag, offset, context, candidate));
    }

    /** Only a repeated config owner and known component resource can establish a model link. */
    private ModelLink? ReadModelConfig(uint tag)
    {
        if (Class(tag) != ConfigClass) return null;
        byte[] data = catalog.Read(tag);
        int resource = TagPointer.Checked(data, TagView.FieldOffset<SEntityConfig>("Definition"));
        uint? resourceClass = resource < 4 ? null : TagView.ClassAt(data, resource);
        bool cloth = resourceClass == SClothModelComponent.Class;
        // The component needs to hold only the bytes through Model.
        if (resourceClass is not (SModelComponent.Class or SClothModelComponent.Class)
            || !(cloth ? TagView.FitsThrough<SClothModelComponent>(data, resource, "Model")
                : TagView.FitsThrough<SModelComponent>(data, resource, "Model"))) return null;
        int Member(string name) => resource
            + (cloth ? TagView.FieldOffset<SClothModelComponent>(name) : TagView.FieldOffset<SModelComponent>(name));
        if (TagRead.U32(data, Member("OwnerConfig.Tag")) != tag)
            throw new InvalidDataException("Model component has the wrong config owner.");
        if (cloth)
        {
            int primary = TagPointer.Checked(data, TagView.FieldOffset<SEntityConfig>("DefaultInstance"));
            if (primary < 4 || TagView.ClassAt(data, primary) != SClothModelPrimary.Class)
                throw new InvalidDataException("Cloth model component has the wrong primary class.");
        }
        int modelAt = Member("Model");
        uint model = TagRead.U32(data, modelAt);
        return Class(model) == ModelClass ? new(tag, model, modelAt) : null;
    }

    /** Entity config membership keeps shared models and clip lists tied to their original owner. */
    private EntityLinks ReadEntity(uint tag, CancellationToken token)
    {
        if (Class(tag) != EntityClass) throw new InvalidDataException("Animation owner is not an entity definition.");
        byte[] data = catalog.Read(tag);
        int stride = Unsafe.SizeOf<SPatternComponent>();
        var rows = TagArray.Checked(data, TagView.FieldOffset<SEntity>("Components"), SPatternComponent.Class, stride);
        var configs = new List<uint>(); var models = new List<ModelLink>(); var skeletons = new List<uint>();
        for (int index = 0; index < rows.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            uint config = TagView.At<SPatternComponent>(data, rows.Data + index * stride).Config;
            if (Class(config) != ConfigClass) continue;
            configs.Add(config);
            if (ReadModelConfig(config) is { } model) models.Add(model);
            byte[] configData = catalog.Read(config);
            int primary = TagPointer.Checked(configData, TagView.FieldOffset<SEntityConfig>("DefaultInstance"));
            int secondary = TagPointer.Checked(configData, TagView.FieldOffset<SEntityConfig>("Definition"));
            if (primary >= 4 && secondary >= 4
                && TagView.ClassAt(configData, primary) == SSkeletonPrimary.Class
                && TagView.ClassAt(configData, secondary) == SSkeletonResource.Class)
                skeletons.Add(config);
        }
        return new(tag, configs.ToArray(), models.ToArray(), new EntityAnimationCatalog(catalog).Read(tag).ToArray(), skeletons.ToArray());
    }

    /** Metadata readers expose direct typed links without loading geometry or scanning arbitrary words. */
    private IReadOnlyList<RelatedAsset> ReadDirect(uint tag, AssetOpenContext? context, CancellationToken token)
    {
        if (!catalog.TryGetEntry(tag, out var entry)) return [];
        var result = new List<RelatedAsset>();
        void Add(uint target, AssetRelationshipKind kind, string role, int offset, uint expected = 0,
            int? expectedType = null, int? expectedSubtype = null)
        {
            if (target is 0 or uint.MaxValue || !catalog.TryGetEntry(target, out var linked)
                || expected != 0 && (!AssetCategories.IsStructured(linked) || linked.Reference != expected)
                || expectedType.HasValue && linked.Type != expectedType.Value
                || expectedSubtype.HasValue && linked.SubType != expectedSubtype.Value) return;
            result.Add(new(target, "", kind, role, tag, offset, context));
        }
        if (entry.Type is 32 or 33 or 34 && catalog.TryGetEntry(entry.Reference, out var payload)
            && payload.Type == entry.Type + 8 && payload.SubType == entry.SubType)
            Add(entry.Reference, AssetRelationshipKind.Data, "Data", -1);
        if (!AssetCategories.IsStructured(entry)) return result;
        byte[] data = catalog.Read(tag);
        void Array<T>(int field, uint type, Action<int, int> action) where T : unmanaged
        {
            int stride = Unsafe.SizeOf<T>();
            var rows = TagArray.Checked(data, field, type, stride);
            for (int index = 0; index < rows.Count; index++) { token.ThrowIfCancellationRequested(); action(rows.Data + index * stride, index); }
        }
        void Reference(ref readonly uint target, AssetRelationshipKind kind, string role, uint expected = 0,
            int? expectedType = null, int? expectedSubtype = null) =>
            Add(target, kind, role, TagView.OffsetOf(data, in target), expected, expectedType, expectedSubtype);
        // Reads one word; the tag need not hold the whole struct around it.
        void Word(int at, AssetRelationshipKind kind, string role, uint expected = 0, int? expectedType = null,
            int? expectedSubtype = null) => Add(TagRead.U32(data, at), kind, role, at, expected, expectedType, expectedSubtype);
        switch (entry.Reference)
        {
            case EntityClass:
                Array<SPatternComponent>(TagView.FieldOffset<SEntity>("Components"), SPatternComponent.Class, (p, i) =>
                    Reference(in TagView.At<SPatternComponent>(data, p).Config, AssetRelationshipKind.Component, $"Component {i}", ConfigClass));
                break;
            case ConfigClass:
            {
                if (ReadModelConfig(tag) is { } model) Add(model.Model, AssetRelationshipKind.Model, "Model", model.Offset, ModelClass);
                const string definition = "H624f85f9.AnimationDefinition";
                int primary = TagPointer.Checked(data, TagView.FieldOffset<SEntityConfig>("DefaultInstance"));
                if (primary >= 4 && TagView.FitsThrough<SAnimationComponent>(data, primary, definition)
                    && TagView.ClassAt(data, primary) == SAnimationComponent.Class)
                    Word(primary + TagView.FieldOffset<SAnimationComponent>(definition),
                        AssetRelationshipKind.AnimationDefinition, "Animation definition", SAnimationDefinition.Class);
                break;
            }
            case SStaticMesh.Class:
                Word(TagView.FieldOffset<SStaticMesh>("MeshData"), AssetRelationshipKind.Model, "Mesh data", SStaticMeshData.Class);
                Array<TagReferenceElement>(TagView.FieldOffset<SStaticMesh>("Materials"), TagReferenceElement.Class, (p, _) =>
                    Reference(in TagView.At<TagReferenceElement>(data, p).Tag, AssetRelationshipKind.Material, "Material", STechnique.Class));
                break;
            case SStaticMeshData.Class:
            {
                Array<SStaticBufferGroup>(TagView.FieldOffset<SStaticMeshData>("Buffers"), SStaticBufferGroup.Class, (p, i) =>
                {
                    ref readonly var buffers = ref TagView.At<SStaticBufferGroup>(data, p);
                    Reference(in buffers.IndexBuffer, AssetRelationshipKind.Buffer, $"Indices {i}", expectedType: 32, expectedSubtype: 6);
                    Reference(in buffers.PositionBuffer[0].Unk0, AssetRelationshipKind.Buffer, $"Positions {i}", expectedType: 32, expectedSubtype: 4);
                    Reference(in buffers.PositionBuffer[1].Unk0, AssetRelationshipKind.Buffer, $"Attributes {i}", expectedType: 32, expectedSubtype: 4);
                });
                break;
            }
            case ModelClass:
            {
                Array<SEntityModelMesh>(TagView.FieldOffset<SEntityModel>("Meshes"), SEntityModelMesh.Class, (p, i) =>
                {
                    ref readonly var mesh = ref TagView.At<SEntityModelMesh>(data, p);
                    Reference(in mesh.VertexBuffer0[0].Unk0, AssetRelationshipKind.Buffer, $"Positions {i}", expectedType: 32, expectedSubtype: 4);
                    Reference(in mesh.VertexBuffer0[1].Unk0, AssetRelationshipKind.Buffer, $"Attributes {i}", expectedType: 32, expectedSubtype: 4);
                    Reference(in mesh.VertexBuffer0[2].Unk0, AssetRelationshipKind.Buffer, $"Skin weights {i}", expectedType: 32, expectedSubtype: 4);
                    Reference(in mesh.IndexBuffer, AssetRelationshipKind.Buffer, $"Indices {i}", expectedType: 32, expectedSubtype: 6);
                    Array<SEntityModelPart>(TagView.OffsetOf(data, in mesh.Parts), SEntityModelPart.Class, (part, _) =>
                        Reference(in TagView.At<SEntityModelPart>(data, part).Material, AssetRelationshipKind.Material, "Material", STechnique.Class));
                });
                break;
            }
            case STechnique.Class:
            {
                foreach (var (field, stage, subtype) in new[] { ("VertexShader", "Vertex", 1), ("PixelShader", "Pixel", 0), ("ComputeShader", "Compute", 6) })
                {
                    int start = TagView.FieldOffset<STechnique>(field);
                    Word(start + TagView.FieldOffset<ShaderStage>("Shader"), AssetRelationshipKind.Shader, stage + " shader",
                        expectedType: 33, expectedSubtype: subtype);
                    Array<SMaterialTextureAssignment>(start + TagView.FieldOffset<ShaderStage>("State.StaticTextures.Textures"),
                        SMaterialTextureAssignment.Class, (p, _) =>
                    {
                        ref readonly var texture = ref TagView.At<SMaterialTextureAssignment>(data, p);
                        Reference(in texture.Texture, AssetRelationshipKind.Texture, $"{stage} texture {texture.TextureSlot}", expectedType: 32);
                    });
                }
                break;
            }
            case SAnimationDefinition.Class:
            {
                Array<SAnimationClipSlot>(TagView.FieldOffset<SAnimationDefinition>("Animations"), SAnimationClipSlot.Class, (p, i) =>
                    Reference(in TagView.At<SAnimationClipSlot>(data, p).Clip, AssetRelationshipKind.AnimationClip, $"Clip {i}", SAnimationClip.Class));
                break;
            }
        }
        return result;
    }

    private uint Class(uint tag) => catalog.TryGetEntry(tag, out var entry) && AssetCategories.IsStructured(entry) ? entry.Reference : 0;
    private static bool Recoverable(Exception error) => error is InvalidDataException or NotSupportedException
        or ArgumentException or OverflowException or IndexOutOfRangeException or IOException or KeyNotFoundException;
}
