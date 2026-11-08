using System.Numerics;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

public sealed record AuthoredWeaponReference(uint ParentEntityTag, uint ConfigTag,
    int GroupIndex, int CandidateIndex, int SourceOffset, uint EntityTag,
    IReadOnlyList<uint> ModelTags);

public sealed record AuthoredWeaponLoadout(uint ConfigTag, int GroupIndex, int SourceOffset,
    int EligibilityBitIndex, float Weight, bool IsFallback,
    IReadOnlyList<AuthoredWeaponReference> Weapons);

public sealed record AuthoredAttachmentPoint(uint EntityTag, uint ConfigTag,
    int PrimaryOffset, int SecondaryOffset, int RowIndex, int SourceOffset,
    int NodeIndex, uint EligibilityBitIndex, uint NameHash, string Name,
    SceneNameEvidence NameEvidence, Quaternion Rotation, Vector3 Translation, float Scale)
{
    public Matrix4x4 LocalTransform => Matrix4x4.CreateScale(Scale)
        * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Translation);
}

public sealed record AuthoredAttachmentPointSet(uint EntityTag, uint ConfigTag,
    string Role, int PrimaryOffset, uint PrimaryClass, uint SecondaryClass,
    IReadOnlyList<AuthoredAttachmentPoint> Points);

public sealed record EntityAttachmentCatalog(uint EntityTag,
    IReadOnlyList<AuthoredWeaponLoadout> Loadouts,
    IReadOnlyList<AuthoredAttachmentPointSet> ParentPoints,
    IReadOnlyList<AuthoredAttachmentPointSet> WeaponPoints,
    IReadOnlyList<string> RuntimeInputs, IReadOnlyList<string> Diagnostics);

public sealed record AttachmentPreviewSource(AuthoredWeaponReference Weapon,
    AuthoredAttachmentPoint ParentPoint, AuthoredAttachmentPoint ChildPoint);

public sealed partial class AssetSceneLoader
{
    /** Loadout alternatives remain explicit because native selection uses runtime bits and RNG. */
    public EntityAttachmentCatalog DescribeEntityAttachments(uint entityTag)
    {
        if (Class(entityTag) != SEntity.Class)
            throw new ArgumentException("Attachment metadata requires an entity class.");
        var entity = ReadBytes(entityTag);
        var loadouts = new List<AuthoredWeaponLoadout>();
        var parentPoints = new List<AuthoredAttachmentPointSet>();
        var diagnostics = new List<string>();
        foreach (ref readonly var build in AttachmentRows(entity,
                     in TagView.Member<SEntity, TagArray<SPatternComponent>>(entity, 0, nameof(SEntity.Components)),
                     SPatternComponent.Class))
        {
            var configTag = build.Config;
            if (Class(configTag) != SEntityConfig.Class) continue;
            var bytes = ReadBytes(configTag);
            if (!ConfigLinks(bytes, out var primary, out var secondary)
                || !HasOwnerLink(bytes, primary) || !HasOwnerLink(bytes, secondary)) continue;
            var primaryClass = TagView.ClassAt(bytes, primary);
            if (primaryClass == UnknownPlayerEquipmentPrimary.Class)
                diagnostics.Add($"Config 0x{configTag:X8} uses an unsupported player equipment family.");
            if (primaryClass != UnknownLoadoutPrimaryComponent.Class
                || TagView.ClassAt(bytes, secondary) != UnknownLoadoutSecondaryComponent.Class) continue;
            CheckComponentPair(configTag, bytes, primary, secondary,
                UnknownLoadoutPrimaryComponent.Class, UnknownLoadoutSecondaryComponent.Class);
            var groups = AttachmentRows(bytes, in TagView.Member<UnknownLoadoutSecondaryComponent,
                TagArray<UnknownWeaponLoadoutGroup>>(bytes, secondary, nameof(UnknownLoadoutSecondaryComponent.LoadoutGroups)),
                UnknownWeaponLoadoutGroup.Class);
            for (var index = 0; index < groups.Length; ++index)
            {
                ref readonly var row = ref groups[index];
                var weight = row.Weight;
                if (!float.IsFinite(weight) || weight < 0)
                    throw new InvalidDataException("Authored weapon loadout weight is invalid.");
                loadouts.Add(new AuthoredWeaponLoadout(configTag, index, TagView.OffsetOf(bytes, in row),
                    row.EligibilityBitIndex, weight, false,
                    ReadWeaponReferences(entityTag, configTag, bytes, in row.Weapons, index)));
            }
            ref readonly var fallbackList = ref TagView.Member<UnknownLoadoutSecondaryComponent, TagArray<UnknownWeaponReference>>(
                bytes, secondary, nameof(UnknownLoadoutSecondaryComponent.FallbackWeapons));
            var fallback = ReadWeaponReferences(entityTag, configTag, bytes, in fallbackList, -1);
            if (fallback.Count != 0)
                loadouts.Add(new AuthoredWeaponLoadout(configTag, -1, TagView.OffsetOf(bytes, in fallbackList),
                    -1, 0, true, fallback));
            parentPoints.Add(ReadAttachmentPoints(entityTag, configTag, bytes,
                primary + TagView.FieldOffset<UnknownLoadoutPrimaryComponent>(nameof(UnknownLoadoutPrimaryComponent.H930b1fef)),
                UnknownAttachmentPointSourcePrimary.Class, UnknownAttachmentPointSourceSecondary.Class,
                "Native mode 1 parent default"));
            parentPoints.Add(ReadAttachmentPoints(entityTag, configTag, bytes,
                primary + TagView.FieldOffset<UnknownLoadoutPrimaryComponent>("HE11ef78c.HF592fd76"),
                UnknownDeclaredPointSourcePrimary.Class, UnknownDeclaredPointSourceSecondary.Class,
                "Native mode 3 declared parent points"));
            if (TagView.Member<UnknownLoadoutPrimaryComponent, long>(bytes, primary, "ParentPointOverridesA.Count") != 0
                || TagView.Member<UnknownLoadoutPrimaryComponent, long>(bytes, primary, "ParentPointOverridesB.Count") != 0)
                diagnostics.Add($"Config 0x{configTag:X8} has parent point overrides not selected by this reader.");
        }
        var weaponTags = loadouts.SelectMany(g => g.Weapons).Select(w => w.EntityTag);
        if (ObjectType(entity) == 14) weaponTags = weaponTags.Append(entityTag);
        var weaponPoints = weaponTags.Distinct()
            .SelectMany(DescribeWeaponAttachmentPoints).ToArray();
        string[] inputs = loadouts.Count != 0
            ? ["Runtime eligibility bitset", "Weighted loadout random state",
                "Equipped/stored slot disposition", "Point override and linked-point state",
                "Pose-provider binding and current node poses", "Child scale"]
            : weaponPoints.Length != 0 ? ["Point selection and overrides",
                "Pose-provider binding and current node poses", "Child scale"] : [];
        return new EntityAttachmentCatalog(entityTag, loadouts.AsReadOnly(), parentPoints.AsReadOnly(),
            weaponPoints, inputs, diagnostics.AsReadOnly());
    }

    /** The weapon component retains two point sources; reflected role selection stays explicit. */
    public IReadOnlyList<AuthoredAttachmentPointSet> DescribeWeaponAttachmentPoints(uint entityTag)
    {
        if (Class(entityTag) != SEntity.Class)
            throw new ArgumentException("Weapon point metadata requires an entity class.");
        var entity = ReadBytes(entityTag);
        if (ObjectType(entity) != 14)
            throw new ArgumentException("Entity is not the native weapon object type.");
        var result = new List<AuthoredAttachmentPointSet>();
        foreach (ref readonly var build in AttachmentRows(entity,
                     in TagView.Member<SEntity, TagArray<SPatternComponent>>(entity, 0, nameof(SEntity.Components)),
                     SPatternComponent.Class))
        {
            var configTag = build.Config;
            if (Class(configTag) != SEntityConfig.Class) continue;
            var bytes = ReadBytes(configTag);
            if (!ConfigLinks(bytes, out var primary, out var secondary)
                || !HasOwnerLink(bytes, primary) || !HasOwnerLink(bytes, secondary)
                || TagView.ClassAt(bytes, primary) != UnknownWeaponPrimaryComponent.Class
                || TagView.ClassAt(bytes, secondary) != SWeaponResource.Class) continue;
            CheckComponentPair(configTag, bytes, primary, secondary,
                UnknownWeaponPrimaryComponent.Class, SWeaponResource.Class);
            result.Add(ReadAttachmentPoints(entityTag, configTag, bytes,
                primary + TagView.FieldOffset<UnknownWeaponPrimaryComponent>("H7cafea08.H575b6d29"),
                UnknownAttachmentPointSourcePrimary.Class, UnknownAttachmentPointSourceSecondary.Class,
                "Weapon point source +0x350"));
            result.Add(ReadAttachmentPoints(entityTag, configTag, bytes,
                primary + TagView.FieldOffset<UnknownWeaponPrimaryComponent>("H486248af.H575b6d29"),
                UnknownAttachmentPointSourcePrimary.Class, UnknownAttachmentPointSourceSecondary.Class,
                "Weapon point source +0x3F0"));
        }
        return result.AsReadOnly();
    }

    /** Child tags follow the exact 16-byte list consumed by native weapon instantiation. */
    private IReadOnlyList<AuthoredWeaponReference> ReadWeaponReferences(uint parentTag,
        uint configTag, ReadOnlySpan<byte> data, ref readonly TagArray<UnknownWeaponReference> descriptor, int group)
    {
        var result = new List<AuthoredWeaponReference>();
        var rows = AttachmentRows(data, in descriptor, UnknownWeaponReference.Class);
        for (var index = 0; index < rows.Length; ++index)
        {
            ref readonly var reference = ref rows[index];
            var child = reference.WeaponEntity;
            if (child == uint.MaxValue) continue;
            if (Class(child) != SEntity.Class || ObjectType(ReadBytes(child)) != 14)
                throw new InvalidDataException("Authored weapon reference has the wrong entity type.");
            var models = new HashSet<uint>();
            var entity = ReadBytes(child);
            foreach (ref readonly var build in AttachmentRows(entity,
                         in TagView.Member<SEntity, TagArray<SPatternComponent>>(entity, 0, nameof(SEntity.Components)),
                         SPatternComponent.Class))
            {
                var config = build.Config;
                if (Class(config) != SEntityConfig.Class) continue;
                var resourceData = ReadBytes(config);
                // A null link has no component; only the members through Model are read.
                if (TagView.TargetOffset(resourceData, in TagView.Member<SEntityConfig, RelativePointer<Unknown>>(
                        resourceData, 0, nameof(SEntityConfig.Definition))) is not { } resource
                    || resource < TagView.ClassWordSize
                    || !TagView.FitsThrough<SModelComponent>(resourceData, resource, nameof(SModelComponent.Model))
                    || TagView.ClassAt(resourceData, resource) != SModelComponent.Class) continue;
                var model = TagView.Member<SModelComponent, uint>(resourceData, resource, nameof(SModelComponent.Model));
                if (Class(model) == SEntityModel.Class) models.Add(model);
            }
            result.Add(new AuthoredWeaponReference(parentTag, configTag, group, index,
                TagView.OffsetOf(data, in reference), child, models.ToArray()));
        }
        return result.AsReadOnly();
    }

    /** Embedded point pairs repeat their owner, peer classes and absolute peer offsets. */
    private AuthoredAttachmentPointSet ReadAttachmentPoints(uint entityTag, uint configTag,
        ReadOnlySpan<byte> bytes, int primary, uint primaryClass, uint secondaryClass, string role)
    {
        var secondary = checked((int)(ulong)TagOffsetPointer.Read(bytes, primary).Offset);
        CheckComponentPair(configTag, bytes, primary, secondary, primaryClass, secondaryClass);
        const string pointList = "H7bf0a1ea.Points";
        int field = secondary + (secondaryClass == UnknownDeclaredPointSourceSecondary.Class
            ? TagView.FieldOffset<UnknownDeclaredPointSourceSecondary>(pointList)
            : TagView.FieldOffset<UnknownAttachmentPointSourceSecondary>(pointList));
        // The registry types this list Unknown; both row classes share the 64-byte point layout.
        ref readonly var list = ref TagView.At<TagArray<SWeaponBarrelPoint>>(bytes, field);
        var rowClass = list.Count != 0 && new TagArray(bytes, field).ElementClass == SWeaponAttachmentPoint.Class
            ? SWeaponAttachmentPoint.Class : SWeaponBarrelPoint.Class;
        var rows = AttachmentRows(bytes, in list, rowClass);
        var points = new AuthoredAttachmentPoint[rows.Length];
        for (var index = 0; index < rows.Length; ++index)
        {
            ref readonly var row = ref rows[index];
            var rotation = row.Rotation;
            var translation = row.Translation;
            var scale = row.Scale;
            if (!Finite(translation) || !float.IsFinite(rotation.LengthSquared())
                || rotation.LengthSquared() < 1e-8f || !float.IsFinite(scale) || scale <= 0)
                throw new InvalidDataException("Attachment point transform is invalid.");
            var hash = row.NameHash;
            var name = ResolveHashName(hash);
            points[index] = new AuthoredAttachmentPoint(entityTag, configTag, primary, secondary,
                index, TagView.OffsetOf(bytes, in row), row.BoneIndex, row.EligibilityBitIndex, hash, name.Name,
                name.Evidence, rotation, translation, scale);
        }
        return new AuthoredAttachmentPointSet(entityTag, configTag, role, primary,
            primaryClass, secondaryClass, points);
    }

    /** @return False when either link is null; a null link names no component. */
    private static bool ConfigLinks(ReadOnlySpan<byte> config, out int primary, out int secondary)
    {
        int? first = TagView.TargetOffset(config, in TagView.Member<SEntityConfig, RelativePointer<Unknown>>(config, 0,
                nameof(SEntityConfig.DefaultInstance))),
            second = TagView.TargetOffset(config, in TagView.Member<SEntityConfig, RelativePointer<Unknown>>(config, 0,
                nameof(SEntityConfig.Definition)));
        primary = first ?? 0;
        secondary = second ?? 0;
        return first is not null && second is not null;
    }

    /** True when the class word and the 16-byte owner link at `component` lie inside the tag. */
    private static bool HasOwnerLink(ReadOnlySpan<byte> tag, int component) =>
        component >= TagView.ClassWordSize && TagView.Fits<TagOffsetPointer>(tag, component);

    private static byte ObjectType(ReadOnlySpan<byte> entity) =>
        TagView.Member<SEntity, byte>(entity, 0, nameof(SEntity.ObjectType));

    /** Rows of an array field; a count above MaximumReaderRows is refused as corrupt. */
    private static ReadOnlySpan<T> AttachmentRows<T>(ReadOnlySpan<byte> tag, ref readonly TagArray<T> array, uint elementClass)
        where T : unmanaged
    {
        if (array.Count > TagArray.MaximumReaderRows) throw new InvalidDataException("Array count is out of range.");
        return TagView.Rows(tag, in array, elementClass);
    }

    private static void CheckComponentPair(uint tag, ReadOnlySpan<byte> bytes, int primary, int secondary,
        uint primaryClass, uint secondaryClass)
    {
        if (!TagView.Fits<TagOffsetPointer>(bytes, primary) || !TagView.Fits<TagOffsetPointer>(bytes, secondary))
            throw new InvalidDataException("Attachment component pair does not agree with its owner.");
        var owner = TagOffsetPointer.Read(bytes, primary);
        var peer = TagOffsetPointer.Read(bytes, secondary);
        if (owner.Tag != tag || owner.Word != secondaryClass || (ulong)owner.Offset != (ulong)secondary
            || peer.Tag != tag || peer.Word != primaryClass || (ulong)peer.Offset != (ulong)primary)
            throw new InvalidDataException("Attachment component pair does not agree with its owner.");
    }
}
