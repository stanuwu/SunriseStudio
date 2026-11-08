using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

public sealed record PreparedWeaponArtEntity(uint EntityTag, uint ConfigTag,
    IReadOnlyList<ProjectAssetAddition> Assets);

/** Art roots retain their external frame bindings while their model component is cloned. */
public static class WeaponAssetEntityCompiler
{
    private sealed record Profile(uint Config, uint Model);
    private static readonly IReadOnlyDictionary<uint, Profile> Profiles = new Dictionary<uint, Profile>
    {
        [0x81A6B357] = new(0x81A6B355, 0x81A6B350),
        [0x81A6B3A5] = new(0x81A6B3A4, 0x81A6B39F),
        [0x81A6B3EC] = new(0x81A6B3EB, 0x81A6B3E7),
        [0x81A6B42E] = new(0x81528E8A, 0x81528E89)
    };
    // The members of each model component that Prepare reads or writes.
    private static readonly string[] PrimaryMembers = ["OwnerPairConfig", "H918e2747.OwnerPair0x80809bd8",
        "HCa95f655.OwnerPair0x808072cb", "HF5b4dbc0.OwnerPair0x808072c0", "HF75d2aff.OwnerPair0x80807314", "HCa95f655.Channels"];
    private static readonly string[] ResourceMembers = ["Model", "OwnerConfig", "H918e2747.OwnerPair0x80809bd9",
        "HCa95f655.OwnerPair0x808072c7", "HF5b4dbc0.OwnerPair0x808072c1", "HF75d2aff.OwnerPair0x80807315", "HCa95f655.Channels"];

    public static bool Supports(IReadOnlyList<WeaponArtVariant> variants)
    {
        var expected = new Dictionary<int, uint>
        { [0] = 0x81A6B357, [1] = 0x81A6B3A5, [21] = 0x81A6B3EC, [3] = 0x81A6B42E };
        return variants.Count == expected.Count && variants.Select(value => value.Selector).Distinct().Count() == expected.Count
            && variants.All(value => expected.GetValueOrDefault(value.Selector) == value.EntityTag);
    }

    /** Only typed reciprocal owners and typed root links receive new tags. */
    public static PreparedWeaponArtEntity Prepare(PackageCatalog catalog, uint sourceEntityTag,
        uint modelTag, ushort packageId, int firstEntry, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Profiles.TryGetValue(sourceEntityTag, out var profile))
            throw new NotSupportedException("Weapon art creation needs a checked Tommy art profile.");
        if (!PackageWriter.IsNewPackageId(packageId) || catalog.ContainsInstalledPackage(packageId)
            || firstEntry < 0 || firstEntry > 8190)
            throw new InvalidDataException("Weapon art creation needs two unused package entries.");
        uint Tag(int index) => TagHashes.Make(packageId, firstEntry + index);
        if (catalog.ContainsTag(Tag(0)) || catalog.ContainsTag(Tag(1)))
            throw new InvalidDataException("A weapon art destination tag is already used.");
        byte[] root = Read(sourceEntityTag, SEntity.Class);
        byte[] config = Read(profile.Config, SEntityConfig.Class);
        // Only the members read or written must fit, so the components are read member by member.
        Fit<SEntityConfig>(config, 0, [nameof(SEntityConfig.DefaultInstance), "Definition"]);
        int primary = TagView.TargetOrSelf(config, in Member<SEntityConfig, RelativePointer<Unknown>>(config, 0, nameof(SEntityConfig.DefaultInstance)));
        int resource = TagView.TargetOrSelf(config, in Member<SEntityConfig, RelativePointer<Unknown>>(config, 0, nameof(SEntityConfig.Definition)));
        if (TagView.ClassAt(config, primary) != SModelComponentPrimary.Class
            || TagView.ClassAt(config, resource) != SModelComponent.Class)
            throw new InvalidDataException("Weapon model component layout changed.");
        Fit<SModelComponentPrimary>(config, primary, PrimaryMembers);
        Fit<SModelComponent>(config, resource, ResourceMembers);
        if (Member<SModelComponent, uint>(config, resource, nameof(SModelComponent.Model)) != profile.Model)
            throw new InvalidDataException("The weapon model component names another model.");
        Pairs("OwnerPairConfig", "OwnerConfig", SModelComponentPrimary.Class, SModelComponent.Class);
        Pairs("H918e2747.OwnerPair0x80809bd8", "H918e2747.OwnerPair0x80809bd9", C80809bd8.Class, C80809bd9.Class);
        Pairs("HCa95f655.OwnerPair0x808072cb", "HCa95f655.OwnerPair0x808072c7", C808072cb.Class, C808072c7.Class);
        Pairs("HF5b4dbc0.OwnerPair0x808072c0", "HF5b4dbc0.OwnerPair0x808072c1", C808072c0.Class, C808072c1.Class);
        Pairs("HF75d2aff.OwnerPair0x80807314", "HF75d2aff.OwnerPair0x80807315", C80807314.Class, C80807315.Class);
        var values = TagArray.Checked(config, primary + TagView.FieldOffset<SModelComponentPrimary>("HCa95f655.Channels"),
            SModelValueBank.Class, Unsafe.SizeOf<SModelValueBank>());
        var resources = TagArray.Checked(config, resource + TagView.FieldOffset<SModelComponent>("HCa95f655.Channels"),
            SModelValueBankResource.Class, Unsafe.SizeOf<SModelValueBankResource>());
        if (values.Count != resources.Count)
            throw new InvalidDataException("Weapon model value-bank pairs disagree.");
        for (int index = 0; index < values.Count; index++)
            Pair(ref TagView.Edit<SModelValueBank>(config, values.Data + index * Unsafe.SizeOf<SModelValueBank>()).OwnerPair,
                ref TagView.Edit<SModelValueBankResource>(config, resources.Data + index * Unsafe.SizeOf<SModelValueBankResource>()).OwnerPair,
                SModelValueBank.Class, SModelValueBankResource.Class);
        Member<SModelComponent, uint>(config, resource, nameof(SModelComponent.Model)) = modelTag;
        root = WeaponConfigEdits.RetagRoot(root, profile.Config, Tag(0));
        if (ContainsAligned(config, profile.Config))
            throw new NotSupportedException("An unclassified old model-owner reference remains in the art component.");
        var configEntry = Require(profile.Config);
        var rootEntry = Require(sourceEntityTag);
        ProjectAssetAddition[] assets =
        [
            new(name + ".component", config,
                new(packageId, configEntry.Reference, configEntry.TypeInfo, null, firstEntry), profile.Config),
            new(name + ".entity", root,
                new(packageId, rootEntry.Reference, rootEntry.TypeInfo, name, firstEntry + 1), sourceEntityTag)
        ];
        return new(Tag(1), Tag(0), assets);

        PackageEntry Require(uint tag) => catalog.TryGetEntry(tag, out var entry) ? entry
            : throw new InvalidDataException("Weapon art source is missing.");
        byte[] Read(uint tag, uint type)
        {
            if (Require(tag).Reference != type)
                throw new NotSupportedException($"Weapon art source {tag:X8} is not of class {type:X8}.");
            return catalog.Read(tag).ToArray();
        }
        void Pairs(string owner, string partner, uint ownerType, uint partnerType) =>
            Pair(ref Member<SModelComponentPrimary, TagOffsetPointer>(config, primary, owner),
                ref Member<SModelComponent, TagOffsetPointer>(config, resource, partner), ownerType, partnerType);
        // Each owner of a pair names the config tag, the other owner's class and its offset.
        void Pair(ref TagOffsetPointer owner, ref TagOffsetPointer partner, uint ownerType, uint partnerType)
        {
            int p = TagView.OffsetOf(config, in owner), r = TagView.OffsetOf(config, in partner);
            if (owner.Tag != profile.Config || owner.Word != partnerType || owner.Offset != r
                || partner.Tag != profile.Config || partner.Word != ownerType || partner.Offset != p)
                throw new InvalidDataException("Weapon model owner pair is not reciprocal.");
            owner = owner with { Tag = Tag(0) };
            partner = partner with { Tag = Tag(0) };
        }
    }

    /** Refuses a T at `at` when one of `members` runs past the tag. */
    private static void Fit<T>(byte[] data, int at, string[] members) where T : unmanaged
    {
        foreach (string member in members)
            if (!TagView.FitsThrough<T>(data, at, member))
                throw new InvalidDataException($"{typeof(T).Name}.{member} runs past the weapon art component.");
    }

    /** @return The `member` of a T at `root`; a member past the end throws ArgumentOutOfRangeException. */
    private static ref TField Member<T, TField>(byte[] data, int root, string member) where T : unmanaged where TField : unmanaged =>
        ref MemoryMarshal.AsRef<TField>(data.AsSpan(root + TagView.FieldOffset<T>(member), Unsafe.SizeOf<TField>()));

    private static bool ContainsAligned(byte[] data, uint tag) =>
        Enumerable.Range(0, data.Length / 4).Any(index => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(index * 4)) == tag);
}
