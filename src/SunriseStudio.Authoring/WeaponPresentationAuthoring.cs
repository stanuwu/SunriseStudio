using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SunriseStudio.Animation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

public sealed record PreparedWeaponPresentation(IReadOnlyList<ProjectAssetAddition> Assets,
    uint GameplayEntityTag, uint PresentationEntityTag, uint AnimationDefinitionTag,
    IReadOnlyDictionary<uint, string> SourceHashes)
{
    public IReadOnlyDictionary<string, int> AddedClipIndices { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, uint> AddedNameHashes { get; init; } = new Dictionary<string, uint>();
}

public sealed record WeaponPresentationClip(string Name, uint ClipTag, int FrameCount);

/** Weapon animation edits own their presentation branch and leave installed weapon graphs unchanged. */
public static class WeaponPresentationAuthoring
{
    public const uint GameplayTemplate = 0x81A6B325, PresentationTemplate = 0x80BC1002;
    public const uint DefinitionTemplate = 0x80BC11AF;
    public const uint QueryTemplate = 0x80BC11B0;
    public const int ReloadSlot = 113, FireSlot = 114;
    private const uint WeaponConfig = 0x8152C7C4, SelectorConfig = 0x8152C7C9;
    private const uint AnimationConfig = 0x8152C7CD, StateConfig = 0x8161FA61;
    /** @param Pairs The reciprocal owner pairs a config holds for itself. */
    private sealed record Profile(uint Tag, uint Class, int Pairs = 0);
    private static readonly Profile[] Profiles =
    [
        new(GameplayTemplate, SEntity.Class),
        new(WeaponConfig, SEntityConfig.Class, 13),
        new(PresentationTemplate, SEntity.Class),
        new(SelectorConfig, SEntityConfig.Class, 1),
        new(AnimationConfig, SEntityConfig.Class, 21),
        new(StateConfig, SEntityConfig.Class, 58),
        new(DefinitionTemplate, SAnimationDefinition.Class)
    ];
    // The weapon component's two bound methods: class handle, owner pointer, slot.
    private static readonly (string Handle, string SlotWord, string Method, uint Slot)[] BoundMethods =
    [
        (nameof(UnknownWeaponPrimaryComponent.Unk30), nameof(UnknownWeaponPrimaryComponent.BoundMethodSlot0),
            nameof(UnknownWeaponPrimaryComponent.Unk38), 8),
        (nameof(UnknownWeaponPrimaryComponent.Unk60), nameof(UnknownWeaponPrimaryComponent.BoundMethodSlot2),
            nameof(UnknownWeaponPrimaryComponent.Unk68), 9)
    ];

    /** New clip tags must use the source branch's 75-control group layout. */
    public static PreparedWeaponPresentation Prepare(PackageCatalog catalog, ushort packageId,
        int firstEntry, string name, uint newReloadClipTag, uint newFireClipTag,
        IReadOnlyList<WeaponPresentationClip>? additionalClips = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        additionalClips ??= [];
        if (additionalClips.Count > 64 || additionalClips.Select(clip => clip.Name).Distinct(StringComparer.Ordinal).Count() != additionalClips.Count)
            throw new InvalidDataException("Additional weapon clips need unique names and at most 64 entries.");
        var profiles = Profiles.ToList();
        if (additionalClips.Count != 0) profiles.Add(new(QueryTemplate, SAnimationQueryTable.Class));
        if (!PackageWriter.IsNewPackageId(packageId) || catalog.ContainsInstalledPackage(packageId)
            || firstEntry < 0 || firstEntry > TagHashes.EntriesPerPackage - profiles.Count)
            throw new InvalidDataException("Weapon presentation needs an unused package entry for each cloned asset.");
        uint Tag(int index) => TagHashes.Make(packageId, firstEntry + index);
        var tags = profiles.Select((profile, index) => (profile.Tag, NewTag: Tag(index)))
            .ToDictionary(pair => pair.Tag, pair => pair.NewTag);
        if (tags.Values.Any(catalog.ContainsTag) || tags.Values.Contains(newReloadClipTag)
            || tags.Values.Contains(newFireClipTag) || newReloadClipTag == newFireClipTag)
            throw new InvalidDataException("Weapon presentation destinations overlap another asset.");
        foreach (uint clip in new[] { newReloadClipTag, newFireClipTag }.Concat(additionalClips.Select(clip => clip.ClipTag)))
        {
            if (!TagHashes.IsTag(clip) || clip == uint.MaxValue || tags.Values.Contains(clip))
                throw new InvalidDataException("Weapon animation needs a native clip tag.");
            if (catalog.ContainsTag(clip)) ValidateClip(catalog, clip);
        }
        long revision = catalog.OverlayRevision;
        var bytes = new Dictionary<uint, byte[]>();
        var hashes = new Dictionary<uint, string>();
        foreach (var profile in profiles)
        {
            if (!catalog.TryGetEntry(profile.Tag, out var entry) || entry.Reference != profile.Class)
                throw new NotSupportedException("The checked weapon presentation profile is missing.");
            byte[] source = catalog.Read(profile.Tag);
            if (profile.Class == SEntityConfig.Class && !TagView.Fits<SEntityConfig>(source, 0))
                throw new InvalidDataException($"Presentation config {profile.Tag:X8} is shorter than a config.");
            bytes[profile.Tag] = (byte[])source.Clone(); hashes[profile.Tag] = Convert.ToHexString(SHA256.HashData(source));
        }
        ValidateClip(catalog, 0x81A6B4F0); ValidateClip(catalog, 0x81A6B4F1);
        foreach (var profile in Profiles.Where(profile => profile.Class == SEntityConfig.Class))
            RetagConfig(bytes[profile.Tag], profile.Tag, tags[profile.Tag], profile.Pairs);
        bytes[GameplayTemplate] = WeaponConfigEdits.RetagRoot(bytes[GameplayTemplate], WeaponConfig, tags[WeaponConfig]);
        foreach (uint config in new[] { SelectorConfig, AnimationConfig, StateConfig })
            bytes[PresentationTemplate] = WeaponConfigEdits.RetagRoot(bytes[PresentationTemplate], config, tags[config]);
        byte[] weapon = bytes[WeaponConfig];
        int resource = TagView.TargetOrSelf(weapon, in TagView.At<SEntityConfig>(weapon).Definition);
        if (TagView.ClassAt(weapon, resource) != SWeaponResource.Class)
            throw new InvalidDataException("The weapon presentation resource changed class.");
        ref var weaponResource = ref TagView.Edit<SWeaponResource>(weapon, resource);
        var variants = EditRows(weapon, in weaponResource.PresentationRows, SWeaponVariantRow.Class);
        int selected = -1, matches = 0;
        for (int row = 0; row < variants.Length; row++)
            if (variants[row].GearSelectorKey0x44a0b334Base == 0x44A0B334 && matches++ == 0) selected = row;
        if (matches != 1)
            throw new InvalidDataException("The weapon presentation selector is missing or ambiguous.");
        Replace(ref weaponResource.HDe30eaea.FirstPersonPresentationEntity, PresentationTemplate, tags[PresentationTemplate]);
        Replace(ref variants[selected].FirstPersonPresentationEntity, PresentationTemplate, tags[PresentationTemplate]);
        Replace(ref ConfigWord<SAnimationStandSelector>(bytes[SelectorConfig], true, SAnimationStandSelector.Class,
            nameof(SAnimationStandSelector.AnimationDefinition)), DefinitionTemplate, tags[DefinitionTemplate]);
        Replace(ref ConfigWord<SAnimationComponent>(bytes[AnimationConfig], false, SAnimationComponent.Class,
            $"{nameof(SAnimationComponent.H624f85f9)}.{nameof(C808036f7.AnimationDefinition)}"), DefinitionTemplate, tags[DefinitionTemplate]);
        Replace(ref ConfigWord<C80803640>(bytes[StateConfig], true, C80803640.Class, nameof(C80803640.Unk108)),
            DefinitionTemplate, tags[DefinitionTemplate]);
        byte[] definition = bytes[DefinitionTemplate];
        var clips = EditRows(definition, in TagView.Edit<SAnimationDefinition>(definition).Animations, SAnimationClipSlot.Class);
        if (clips.Length != 116) throw new InvalidDataException("The weapon clip list changed.");
        Replace(ref clips[ReloadSlot].Clip, 0x81A6B4F0, newReloadClipTag);
        Replace(ref clips[FireSlot].Clip, 0x81A6B4F1, newFireClipTag);
        var addedIndices = new Dictionary<string, int>();
        var addedHashes = new Dictionary<string, uint>();
        foreach (var clip in additionalClips)
        {
            if (string.IsNullOrWhiteSpace(clip.Name) || clip.FrameCount is < 1 or > short.MaxValue)
                throw new InvalidDataException("A named weapon clip needs a name and a valid native frame count.");
            uint hash = StringBankWriter.Hash(clip.Name);
            byte[] current = bytes[DefinitionTemplate];
            ref readonly var currentDefinition = ref TagView.Checked<SAnimationDefinition>(current, 0);
            int index = Rows(current, in currentDefinition.Animations, SAnimationClipSlot.Class).Length;
            int nameIndex = Rows(current, in currentDefinition.MotionDefinitions, SAnimationNameRow.Class).Length;
            bytes[DefinitionTemplate] = AppendClip(bytes[DefinitionTemplate], clip, hash);
            bytes[QueryTemplate] = AppendQuery(bytes[QueryTemplate], hash, nameIndex);
            addedIndices.Add(clip.Name, index); addedHashes.Add(clip.Name, hash);
        }
        if (additionalClips.Count != 0)
            Replace(ref ConfigWord<SAnimationStandSelector>(bytes[SelectorConfig], true, SAnimationStandSelector.Class,
                nameof(SAnimationStandSelector.QueryTable)), QueryTemplate, tags[QueryTemplate]);
        var assets = profiles.Select((profile, index) =>
        {
            catalog.TryGetEntry(profile.Tag, out var entry);
            string suffix = profile.Tag == GameplayTemplate ? "gameplay" : profile.Tag == PresentationTemplate
                ? "presentation" : profile.Tag == DefinitionTemplate ? "animations" : $"component-{profile.Tag:X8}";
            return new ProjectAssetAddition(name + "." + suffix, bytes[profile.Tag],
                new(packageId, entry.Reference, entry.TypeInfo,
                    profile.Tag == GameplayTemplate ? name : null, firstEntry + index), profile.Tag);
        }).ToArray();
        if (catalog.OverlayRevision != revision)
            throw new InvalidDataException("Weapon presentation sources changed during preparation.");
        return new(assets, tags[GameplayTemplate], tags[PresentationTemplate], tags[DefinitionTemplate], hashes)
        { AddedClipIndices = addedIndices, AddedNameHashes = addedHashes };
    }

    /** A clip, its parallel entry and its named-state row are appended as one native definition. */
    private static byte[] AppendClip(byte[] definition, WeaponPresentationClip clip, uint nameHash)
    {
        ref readonly var root = ref TagView.Checked<SAnimationDefinition>(definition, 0);
        var clips = Rows(definition, in root.Animations, SAnimationClipSlot.Class);
        var mirrors = Rows(definition, in root.ClipMirrorParallel0xffffffff, SI32Element.Class);
        var names = Rows(definition, in root.MotionDefinitions, SAnimationNameRow.Class);
        bool duplicate = false;
        foreach (ref readonly var row in names) duplicate |= row.NameHash == nameHash;
        if (clips.Length != mirrors.Length || clips.Length >= short.MaxValue || duplicate)
            throw new InvalidDataException("The named animation is duplicate or its clip arrays disagree.");
        foreach (ref readonly var row in names)
            if (row.UndecodedLinkAMustBe0.Count != 0 || row.UndecodedLinkAMustBe0.Relative != 0)
                throw new NotSupportedException("Animation name rows with undecoded links cannot grow.");
        int clipField = TagView.OffsetOf(definition, in root.Animations);
        int mirrorField = TagView.OffsetOf(definition, in root.ClipMirrorParallel0xffffffff);
        int nameField = TagView.OffsetOf(definition, in root.MotionDefinitions);
        int clipCount = clips.Length;
        byte[] slot = new byte[Unsafe.SizeOf<SAnimationClipSlot>()];
        TagView.Edit<SAnimationClipSlot>(slot).Clip = clip.ClipTag;
        byte[] output = InvestmentItemClone.AppendRow(definition, clipField, Unsafe.SizeOf<SAnimationClipSlot>(), slot);
        byte[] mirror = new byte[Unsafe.SizeOf<SI32Element>()];
        TagView.Edit<SI32Element>(mirror).Value = -1;
        output = InvestmentItemClone.AppendRow(output, mirrorField, Unsafe.SizeOf<SI32Element>(), mirror);
        byte[] label = new byte[Unsafe.SizeOf<SAnimationNameRow>()];
        ref var name = ref TagView.Edit<SAnimationNameRow>(label);
        name.NameHash = nameHash;
        name.DurationSeconds = (clip.FrameCount - 1) / 30f;
        name.ClipSlot = checked((short)clipCount);
        name.Kind = 1;
        return InvestmentItemClone.AppendRow(output, nameField, Unsafe.SizeOf<SAnimationNameRow>(), label);
    }

    /** New names resolve directly to their definition row; existing query branches keep their targets. */
    private static byte[] AppendQuery(byte[] query, uint nameHash, int definitionRow)
    {
        if (nameHash is 0 or uint.MaxValue or StringHash.Empty)
            throw new InvalidDataException("Animation name hashes cannot be empty.");
        ref readonly var table = ref TagView.Checked<SAnimationQueryTable>(query, 0);
        var map = Rows(query, in table.Keys, SAnimationQueryKey.Class);
        int selectors = Rows(query, in table.Queries, SAnimationQuery.Class).Length;
        for (int i = 0; i < map.Length; i++)
            if (map[i].QueryNameHash == nameHash || i > 0 && map[i - 1].QueryNameHash >= map[i].QueryNameHash)
                throw new InvalidDataException("Animation query names must be unique and sorted.");
        int keyField = TagView.OffsetOf(query, in table.Keys), queryField = TagView.OffsetOf(query, in table.Queries);
        byte[] output = query;
        int payload = TagRelocator.AppendObject(ref output, SAnimationSingleQuery.Class, Unsafe.SizeOf<SAnimationSingleQuery>());
        // The one-row array follows the object directly, its header on the next 8-byte boundary.
        int first = TagRelocator.AppendArray(ref output,
            TagView.OffsetOf(output, in TagView.At<SAnimationSingleQuery>(output, payload).H506e8443.Rows), SAnimationQueryRow.Class, 1,
            Unsafe.SizeOf<SAnimationQueryRow>(), sizeof(long));
        ref var queryRow = ref TagView.Edit<SAnimationQueryRow>(output, first);
        queryRow.NameRow = checked((uint)definitionRow);
        queryRow.Weight = 1;
        TagView.Edit<SAnimationQueryTable>(output).TagByteLength = (ulong)output.Length;
        byte[] selector = new byte[Unsafe.SizeOf<SAnimationQuery>()];
        TagView.Edit<SAnimationQuery>(selector).Kind = 1;
        // The row is built as if at offset 0; AppendRow rebases its pointer.
        TagView.SetTarget(selector, in TagView.At<SAnimationQuery>(selector).Target, payload);
        output = InvestmentItemClone.AppendRow(output, queryField, Unsafe.SizeOf<SAnimationQuery>(), selector, [0], 0);
        byte[] key = new byte[Unsafe.SizeOf<SAnimationQueryKey>()];
        ref var keyRow = ref TagView.Edit<SAnimationQueryKey>(key);
        keyRow.QueryNameHash = nameHash;
        keyRow.QueryIndex = checked((uint)selectors);
        output = InvestmentItemClone.AppendRow(output, keyField, Unsafe.SizeOf<SAnimationQueryKey>(), key);
        var moved = EditRows(output, in TagView.Edit<SAnimationQueryTable>(output).Keys, SAnimationQueryKey.Class);
        var sorted = moved.ToArray().OrderBy(row => row.QueryNameHash).ToArray();
        sorted.CopyTo(moved);
        return output;
    }

    /** Clip controls retain the two player groups before the five weapon controls. */
    public static void ValidateClip(PackageCatalog catalog, uint tag)
    {
        var clip = new AnimationControlReader(catalog).Read(tag);
        var expected = new[] { new AnimationControlGroup(0xD59A5FE6, 8),
            new AnimationControlGroup(0x7CB60FEC, 62), new AnimationControlGroup(0xC42D6707, 5) };
        if (clip.ControlCount != 75 || !clip.ControlGroups.SequenceEqual(expected))
            throw new InvalidDataException("Weapon presentation clips need the checked 70 player and five weapon controls.");
    }

    /** Rebinding preserves each query name and updates the durations of rows that use this clip slot. */
    public static byte[] BindClip(PackageCatalog catalog, ReadOnlyMemory<byte> current,
        int slot, uint clipTag)
    {
        ValidateClip(catalog, clipTag);
        var clip = new AnimationControlReader(catalog).Read(clipTag);
        byte[] output = current.ToArray();
        if (!TagView.FitsThrough<SAnimationDefinition>(output, 0, nameof(SAnimationDefinition.MotionDefinitions)))
            throw new InvalidDataException("The animation definition is truncated.");
        var clips = EditRows(output, in TagView.At<TagArray<SAnimationClipSlot>>(output,
            TagView.FieldOffset<SAnimationDefinition>(nameof(SAnimationDefinition.Animations))), SAnimationClipSlot.Class);
        var names = EditRows(output, in TagView.At<TagArray<SAnimationNameRow>>(output,
            TagView.FieldOffset<SAnimationDefinition>(nameof(SAnimationDefinition.MotionDefinitions))), SAnimationNameRow.Class);
        if (slot < 0 || slot >= clips.Length)
            throw new InvalidDataException("The animation slot is outside this definition.");
        clips[slot].Clip = clipTag;
        foreach (ref var row in names)
            if (row.ClipSlot == slot) row.DurationSeconds = clip.DurationSeconds;
        return output;
    }

    /** Every changed owner is a reciprocal typed pair or a checked bound-method endpoint. */
    private static void RetagConfig(byte[] bytes, uint source, uint destination, int pairCount)
    {
        var owners = new HashSet<int>();
        int pairs = 0;
        for (int at = 0; at + Unsafe.SizeOf<TagOffsetPointer>() <= bytes.Length; at += 4)
        {
            if (TagRead.U32(bytes, at) != source) continue;
            var owner = TagOffsetPointer.Read(bytes, at);
            long peerLong = owner.Offset;
            uint peerClass = owner.Word;
            if (peerLong < 0 || peerLong > bytes.Length - Unsafe.SizeOf<TagOffsetPointer>() || peerClass < 0x80800000 || peerClass >= 0x84000000) continue;
            int peer = checked((int)peerLong);
            var back = TagOffsetPointer.Read(bytes, peer);
            if (back.Tag != source || back.Offset != at) continue;
            owners.Add(at); owners.Add(peer);
            if (at < peer) pairs++;
        }
        if (pairs != pairCount) throw new InvalidDataException("A presentation owner pair changed.");
        if (source == WeaponConfig)
        {
            int component = TagView.TargetOrSelf(bytes, in TagView.At<SEntityConfig>(bytes).DefaultInstance);
            foreach (var (handle, slotWord, method, slot) in BoundMethods)
            {
                if (new[] { handle, slotWord, method }.Any(member => !TagView.FitsThrough<UnknownWeaponPrimaryComponent>(bytes, component, member)))
                    throw new InvalidDataException("A weapon bound-method field runs past the weapon config.");
                int at = component + TagView.FieldOffset<UnknownWeaponPrimaryComponent>(handle);
                ref readonly var owner = ref TagView.At<TagOffsetPointer>(bytes,
                    component + TagView.FieldOffset<UnknownWeaponPrimaryComponent>(method));
                if (TagRead.U32(bytes, at) != UnknownWeaponPrimaryComponent.Class
                    || TagRead.U32(bytes, component + TagView.FieldOffset<UnknownWeaponPrimaryComponent>(slotWord)) != slot
                    || owner.Tag != source || owner.Word != UnknownWeaponPrimaryComponent.Class || owner.Offset != component)
                    throw new InvalidDataException("A weapon bound-method owner changed.");
                owners.Add(TagView.OffsetOf(bytes, in owner));
            }
        }
        for (int at = 0; at + 4 <= bytes.Length; at += 4)
            if (TagRead.U32(bytes, at) == source && !owners.Contains(at))
                throw new NotSupportedException("An unclassified presentation owner cannot be cloned.");
        foreach (int at in owners) Put32(bytes, at, destination);
    }

    /**
     * A u32 member of the object a config's root pointer names, viewed alone and writable.
     * @param definition True follows SEntityConfig.Definition, false SEntityConfig.DefaultInstance.
     * @param classId The class the object must have; another class throws.
     */
    private static ref uint ConfigWord<T>(byte[] config, bool definition, uint classId, string member) where T : unmanaged
    {
        ref readonly var root = ref TagView.At<SEntityConfig>(config);
        int target = definition ? TagView.TargetOrSelf(config, in root.Definition) : TagView.TargetOrSelf(config, in root.DefaultInstance);
        if (TagView.ClassAt(config, target) != classId) throw new InvalidDataException("A presentation reference changed.");
        return ref TagView.Edit<uint>(config, target + TagView.FieldOffset<T>(member));
    }

    private static void Replace(ref uint field, uint expected, uint replacement)
    {
        if (field != expected) throw new InvalidDataException("A presentation reference changed.");
        field = replacement;
    }

    private static ReadOnlySpan<T> Rows<T>(byte[] bytes, ref readonly TagArray<T> array, uint elementClass) where T : unmanaged =>
        TagView.CheckedRows(bytes, in array, elementClass, TagArray.MaximumReaderRows);

    /** Checked rows of an array field read from `bytes`, writable. */
    private static Span<T> EditRows<T>(byte[] bytes, ref readonly TagArray<T> array, uint elementClass) where T : unmanaged
    {
        var rows = Rows(bytes, in array, elementClass);
        return rows.IsEmpty ? [] : MemoryMarshal.CreateSpan(ref Unsafe.AsRef(in rows[0]), rows.Length);
    }

    private static void Put32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
}
