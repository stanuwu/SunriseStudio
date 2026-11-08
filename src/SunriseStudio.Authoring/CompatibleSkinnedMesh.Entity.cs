using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

public sealed record PreparedCompatibleEntity(PreparedCompatibleSkin Skin, uint EntityTag, uint ModelTag,
    uint ConfigTag, string ProjectPath, IReadOnlyList<ProjectAssetAddition> Additions,
    IReadOnlyDictionary<uint, string> SourceHashes, IReadOnlyDictionary<uint, PackageEntry> SourceEntries);

public static partial class CompatibleSkinnedMesh
{
    private const uint EntityTemplate = 0x80B82FE3, EntityModel = 0x80C4424F, EntityConfig = 0x80C443F8;
    private const int EntityEntryCount = 9;

    public static bool SupportsEntityCreation(uint entityTag, uint modelTag) =>
        entityTag == EntityTemplate && modelTag == EntityModel;

    /** Creation keeps the checked component layout and shares the installed rig and clips. */
    // TODO: no caller yet; for a skinned import that builds its input without a glTF file.
    public static PreparedCompatibleEntity PrepareEntity(StudioProject project, PackageCatalog catalog,
        CompatibleSkinnedMeshInput input, CompatibleSkinReplacementOptions options, CancellationToken token = default)
    {
        long revision = catalog.OverlayRevision;
        ValidateEntityDestination(project, catalog, options);
        return CloneEntity(project, catalog, options, Prepare(catalog, input, options, token), revision, token);
    }

    /** File joints still have to match the installed template's package-order bind pose. */
    public static PreparedCompatibleEntity PrepareEntityGltf(StudioProject project, PackageCatalog catalog,
        string path, CompatibleSkinReplacementOptions options, CancellationToken token = default)
    {
        long revision = catalog.OverlayRevision;
        ValidateEntityDestination(project, catalog, options);
        return CloneEntity(project, catalog, options, PrepareGltf(catalog, path, options, token), revision, token);
    }

    /** Only typed owner tags change; reciprocal offsets and root ordinals keep their original positions. */
    private static PreparedCompatibleEntity CloneEntity(StudioProject project, PackageCatalog catalog,
        CompatibleSkinReplacementOptions options, PreparedCompatibleSkin skin, long revision, CancellationToken token)
    {
        var hashes = skin.SourceHashes.ToDictionary();
        var originalEntries = catalog.Entries.Where(entry => entry.TagHash is EntityTemplate or EntityConfig or EntityModel)
            .ToDictionary(entry => entry.TagHash);
        foreach (var (tag, original) in originalEntries)
        {
            var current = Require(catalog, tag);
            if (current.Reference != original.Reference || current.TypeInfo != original.TypeInfo
                || current.Type != original.Type || current.SubType != original.SubType)
                throw new NotSupportedException("Create asset needs the installed root, config and model metadata.");
        }
        if (originalEntries.Count != 3) throw new InvalidDataException("The entity creation profile is not installed.");
        byte[] Original(uint tag, uint type)
        {
            var entry = Require(catalog, tag);
            byte[] bytes = catalog.Read(tag), original = catalog.ReadOriginal(tag);
            if (entry.Reference != type || !bytes.AsSpan().SequenceEqual(original))
                throw new NotSupportedException("Create asset needs the unchanged installed Hob component profile.");
            string hash = Hash(bytes);
            if (hashes.TryGetValue(tag, out string? earlier) && earlier != hash)
                throw new InvalidDataException("An entity source changed during preparation.");
            hashes[tag] = hash;
            return bytes.ToArray();
        }
        byte[] root = Original(EntityTemplate, SEntity.Class);
        byte[] config = Original(EntityConfig, SEntityConfig.Class);
        // The model layout was checked when the skin was prepared from it.
        if (!TagView.Fits<SEntity>(root, 0)) throw new InvalidDataException("The source root is shorter than an entity.");
        if (!TagView.Fits<SEntityConfig>(config, 0)) throw new InvalidDataException("The model component is shorter than a config.");
        ref readonly var entity = ref TagView.At<SEntity>(root);
        int[] components = Rows(root, in entity.Components, SPatternComponent.Class);
        if (components.Length != 62) throw new InvalidDataException("The source entity component order changed.");
        foreach (int component in components)
        {
            token.ThrowIfCancellationRequested();
            Original(TagView.At<SPatternComponent>(root, component).Config, SEntityConfig.Class);
        }
        uint Tag(int index) => TagHashes.Make(options.PackageId, options.FirstEntryIndex + index);
        uint modelTag = Tag(6), configTag = Tag(7), entityTag = Tag(8);
        ref readonly var owner = ref TagView.At<SEntityConfig>(config);
        int primary = TagView.TargetOrSelf(config, in owner.DefaultInstance);
        int resource = TagView.TargetOrSelf(config, in owner.Definition);
        if (TagView.ClassAt(config, primary) != SModelComponentPrimary.Class
            || TagView.ClassAt(config, resource) != SModelComponent.Class)
            throw new InvalidDataException("The model component types changed.");
        if (!TagView.Fits<SModelComponentPrimary>(config, primary) || !TagView.Fits<SModelComponent>(config, resource))
            throw new InvalidDataException("A model component object runs past the config.");
        ref readonly var first = ref TagView.At<SModelComponentPrimary>(config, primary);
        ref readonly var second = ref TagView.At<SModelComponent>(config, resource);
        Pair(primary, resource, SModelComponentPrimary.Class, SModelComponent.Class);
        Pair(TagView.OffsetOf(config, in first.H918e2747), TagView.OffsetOf(config, in second.H918e2747), C80809bd8.Class, C80809bd9.Class);
        Pair(TagView.OffsetOf(config, in first.HCa95f655), TagView.OffsetOf(config, in second.HCa95f655), C808072cb.Class, C808072c7.Class);
        Pair(TagView.OffsetOf(config, in first.HF5b4dbc0), TagView.OffsetOf(config, in second.HF5b4dbc0), C808072c0.Class, C808072c1.Class);
        Pair(TagView.OffsetOf(config, in first.HF75d2aff), TagView.OffsetOf(config, in second.HF75d2aff), C80807314.Class, C80807315.Class);
        int[] values = Rows(config, in first.HCa95f655.Channels, SModelValueBank.Class);
        int[] valueResources = Rows(config, in second.HCa95f655.Channels, SModelValueBankResource.Class);
        if (values.Length != 8 || values.Length != valueResources.Length)
            throw new InvalidDataException("The model value-bank owner pairs changed.");
        for (int index = 0; index < values.Length; index++)
            Pair(values[index], valueResources[index], SModelValueBank.Class, SModelValueBankResource.Class);
        if (second.Model != EntityModel)
            throw new InvalidDataException("The model component lost its source model.");
        TagView.Edit<SModelComponent>(config, resource).Model = modelTag;
        int links = 0;
        foreach (int row in Rows(root, in entity.Components, SPatternComponent.Class))
            LinkWord(TagView.OffsetOf(root, in TagView.At<SPatternComponent>(root, row).Config));
        foreach (int row in Rows(root, in entity.HE3929a53.ConnectionList, SEntityBindingRow.Class))
        {
            LinkPointer(TagView.OffsetOf(root, in TagView.At<SEntityBindingRow>(root, row).ConfigA));
            LinkPointer(TagView.OffsetOf(root, in TagView.At<SEntityBindingRow>(root, row).ConfigB));
        }
        foreach (int row in Rows(root, in entity.HE3929a53.Unknown0x30MustBe0, SEntityBindingRow.Class))
        {
            LinkPointer(TagView.OffsetOf(root, in TagView.At<SEntityBindingRow>(root, row).ConfigA));
            LinkPointer(TagView.OffsetOf(root, in TagView.At<SEntityBindingRow>(root, row).ConfigB));
        }
        foreach (int row in Rows(root, in entity.H83bfb495.InterfaceDescriptors, ActorDescriptorRow.Class))
            LinkPointer(TagView.OffsetOf(root, in TagView.At<ActorDescriptorRow>(root, row).Config));
        foreach (int row in Rows(root, in entity.H83bfb495.LinksD24ByteRowsConfigAt0, ActorDescriptorMetadata.Class))
            LinkPointer(TagView.OffsetOf(root, in TagView.At<ActorDescriptorMetadata>(root, row).Config));
        if (links != 44) throw new InvalidDataException("The source root's typed model references changed.");
        var additions = skin.Additions.ToList();
        Add(EntityModel, skin.Replacements[EntityModel], 6, "model");
        Add(EntityConfig, config, 7, "component");
        Add(EntityTemplate, root, 8, "entity");
        var metadata = hashes.Keys.ToDictionary(tag => tag, tag => Require(catalog, tag));
        var prepared = new PreparedCompatibleEntity(skin, entityTag, modelTag, configTag,
            Path.GetFullPath(project.ManifestPath), additions.AsReadOnly(), hashes, metadata);
        token.ThrowIfCancellationRequested();
        if (catalog.OverlayRevision != revision)
            throw new InvalidDataException("Project sources changed while the entity was being prepared.");
        EntityEdits(project, catalog, prepared);
        return prepared;

        // An owner object starts with its partner's config tag, class and offset.
        void Pair(int p, int r, uint pType, uint rType)
        {
            if (!Owns(p, rType, r) || !Owns(r, pType, p))
                throw new InvalidDataException("A component owner pair is not reciprocal.");
            Retag(config, p); Retag(config, r);
        }
        bool Owns(int at, uint partnerClass, int partner) =>
            TagView.At<TagOffsetPointer>(config, at) is var link
            && link.Tag == EntityConfig && link.Word == partnerClass && link.Offset == partner;
        void Retag(byte[] bytes, int at)
        {
            ref var link = ref TagView.Edit<TagOffsetPointer>(bytes, at);
            link = link with { Tag = configTag };
        }
        void LinkWord(int at)
        {
            ref uint word = ref TagView.Edit<uint>(root, at);
            if (word == EntityConfig) { word = configTag; links++; }
        }
        void LinkPointer(int at)
        {
            if (TagView.At<TagOffsetPointer>(root, at).Tag == EntityConfig) { Retag(root, at); links++; }
        }
        void Add(uint source, byte[] bytes, int index, string suffix)
        {
            var entry = Require(catalog, source);
            additions.Add(new(options.Name + "." + suffix, bytes,
                new(options.PackageId, entry.Reference, entry.TypeInfo,
                    index == 8 ? options.Name : null, options.FirstEntryIndex + index), source));
        }
    }

    /** Destination tags are reserved as a complete nine-entry group before geometry preparation. */
    private static void ValidateEntityDestination(StudioProject project, PackageCatalog catalog,
        CompatibleSkinReplacementOptions options)
    {
        if (!SupportsEntityCreation(options.EntityTag, options.ModelTag))
            throw new NotSupportedException("Create asset supports only the checked installed Hob rig and model.");
        if (!PackageWriter.IsNewPackageId(options.PackageId) || catalog.ContainsInstalledPackage(options.PackageId)
            || options.FirstEntryIndex < 0 || options.FirstEntryIndex > TagHashes.EntriesPerPackage - EntityEntryCount)
            throw new InvalidDataException("Entity creation needs nine unused entries in a new package.");
        for (int index = options.FirstEntryIndex; index < options.FirstEntryIndex + EntityEntryCount; index++)
        {
            uint tag = TagHashes.Make(options.PackageId, index);
            if (catalog.TryGetEntry(tag, out _) || project.AddedAssets.Any(asset =>
                asset.Native?.PackageId == options.PackageId && asset.Native.EntryIndex == index))
                throw new InvalidDataException("A destination entry is already used by this project.");
        }
    }

    /** Source bytes, metadata and destination identities must still match the review at publication. */
    public static ProjectAssetEdits EntityEdits(StudioProject project, PackageCatalog catalog,
        PreparedCompatibleEntity prepared)
    {
        long revision = catalog.OverlayRevision;
        if (!string.Equals(Path.GetFullPath(project.ManifestPath), prepared.ProjectPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The entity was prepared for a different project.");
        var first = prepared.Additions[0].Native!;
        ValidateEntityDestination(project, catalog, new(EntityTemplate, EntityModel, 0,
            prepared.Additions[0].Name, first.PackageId, first.EntryIndex!.Value));
        foreach (var (tag, hash) in prepared.SourceHashes)
            if (Require(catalog, tag) != prepared.SourceEntries[tag] || Hash(catalog.Read(tag)) != hash)
                throw new InvalidDataException("An entity source changed after review.");
        if (catalog.OverlayRevision != revision)
            throw new InvalidDataException("Project sources changed during entity validation.");
        return new(new Dictionary<uint, byte[]>());
    }

    public static IReadOnlyList<AddedProjectAsset> ApplyEntity(StudioProject project, PackageCatalog catalog,
        PreparedCompatibleEntity prepared) => project.ApplyTransaction(prepared.Additions,
            _ => EntityEdits(project, catalog, prepared), catalog);
}
