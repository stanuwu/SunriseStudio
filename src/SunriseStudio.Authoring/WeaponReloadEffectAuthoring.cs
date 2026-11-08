using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/** Not tested in game: the Reload burst plays on the gun from firing row slot +0x90. */
public static class WeaponReloadEffectAuthoring
{
    // Stock gated particle entity; its sequence is one looping type-6 gate over one timed particle node.
    public const uint EntityTemplate = 0x80C0D769, SequenceTemplate = 0x80FC61D9;
    // The copy keeps sharing the common config and the variable store.
    private static readonly uint[] TemplateConfigs = [0x80C70B84, SequenceTemplate, 0x815B375D];
    // FNV-1 of "reloading", the channel the weapon publishes while it reloads.
    public const uint ReloadingChannel = 0xECDE609E;
    // Kind 3 with flags 0x22 places the point on the first-person pose.
    private const byte PointLocation = 3, PointFlags = 0x22;
    // Starts on spawn and retires when done, like the stock gated slot sequences.
    private const byte Lifecycle = 0x0B;
    private const byte GateNode = 6, ParticleNode = 4, PointSource = 2;

    /**
     * The reload burst, its sequence and its entity, in entry order; the entity is last.
     * @param pointHash Name hash of the authored reload point.
     */
    public static IReadOnlyList<ProjectAssetAddition> Prepare(PackageCatalog catalog, WeaponSourceEmitter emitter,
        uint pointHash, ushort packageId, int firstEntry, string name)
    {
        if (!float.IsFinite(emitter.StartSeconds) || emitter.StartSeconds is < 0 or > 60)
            throw new InvalidDataException("The Reload emitter must start within its clip.");
        if (firstEntry < 0 || firstEntry + 3 > 8191) throw new InvalidDataException("The reload effect needs 4 free package entries.");
        var particle = WeaponAssetParticleCompiler.Prepare(catalog, WeaponAssetParticleCompiler.BurstTemplate,
            WeaponAssetParticleCompiler.BurstChannels(emitter), packageId, firstEntry, name);
        int entry = firstEntry + particle.Assets.Count;
        uint sequenceTag = Tag(packageId, entry);
        if (catalog.ContainsTag(sequenceTag) || catalog.ContainsTag(Tag(packageId, entry + 1)))
            throw new InvalidDataException("The reload effect destination is already used.");
        if (!catalog.TryGetEntry(SequenceTemplate, out var sequenceEntry) || !catalog.TryGetEntry(EntityTemplate, out var entityEntry)
            || entityEntry.Reference != WeaponFiringSlotEntity.EntityClass)
            throw new InvalidDataException("The reload effect template is missing.");
        byte[] stockEntity = catalog.Read(EntityTemplate);
        if (!WeaponConfigEdits.ConstructionConfigs(stockEntity).SequenceEqual(TemplateConfigs))
            throw new InvalidDataException("The reload effect template entity changed.");
        byte[] sequence = Sequence(catalog.Read(SequenceTemplate), sequenceTag, particle.SystemTag, pointHash,
            emitter.StartSeconds, WeaponAssetParticleCompiler.BurstSeconds(emitter));
        byte[] entity = WeaponConfigEdits.RetagRoot(stockEntity, SequenceTemplate, sequenceTag);
        return [.. particle.Assets,
            new(name + ".sequence", sequence, new(packageId, sequenceEntry.Reference, sequenceEntry.TypeInfo,
                name + ".sequence", entry), SequenceTemplate),
            new(name + ".effect", entity, new(packageId, entityEntry.Reference, entityEntry.TypeInfo, name, entry + 1), EntityTemplate)];
    }

    /** The template sequence gated on reloading, playing the system once on the point after the delay. */
    private static byte[] Sequence(byte[] template, uint destination, uint system, uint pointHash, float delay, float duration)
    {
        byte[] bytes = WeaponConfigEdits.RetagConfig(template, SequenceTemplate, destination).Bytes;
        int resource = WeaponParticleBinding.SequenceResource(bytes);
        if (resource < 0) throw new InvalidDataException("The reload effect template is not a sequence.");
        // The resource is checked only through its lifecycle word, so each member is viewed alone.
        int gates = 0;
        foreach (ref readonly var row in Rows(bytes, in Member<TagArray<SSequenceFlowRow>>(bytes, resource,
                     nameof(SParticleSequenceResource.FlowNodes)), SSequenceFlowRow.Class))
            if (TagView.At<SEffectNode>(bytes, TagView.TargetOrSelf(bytes, in row.OwnerPointer)).NodeKind == GateNode) gates++;
        var works = Rows(bytes, in Member<TagArray<SSequenceFlowRow>>(bytes, resource, nameof(SParticleSequenceResource.WorkNodes)),
            SSequenceFlowRow.Class);
        var channels = EditRows(bytes, in Member<TagArray<SModelValueBankResource>>(bytes, resource,
            nameof(SParticleSequenceResource.InputChannels)), SModelValueBankResource.Class);
        ref readonly var table = ref Member<C8080860d>(bytes, resource, nameof(SParticleSequenceResource.H846fde3f));
        var locations = EditRows(bytes, in table.Locations, SSequenceLocationMask.Class);
        var sources = EditRows(bytes, in table.Update, SSequenceLocationSource.Class);
        var names = EditRows(bytes, in table.Debug, SSequenceLocationName.Class);
        int flagsField = WeaponParticleBinding.InstanceFlagsField(bytes);
        Span<SSequenceLocationFlags> flags = flagsField < 0 ? []
            : EditRows(bytes, in TagView.At<TagArray<SSequenceLocationFlags>>(bytes, flagsField), SSequenceLocationFlags.Class);
        if (flagsField < 0 || gates != 1 || works.Length != 1 || channels.Length != 1 || locations.Length != 1
            || sources.Length != 1 || names.Length != 1 || flags.Length != 1
            || sources[0].LocationKind0Object2WorldWeapon3FirstPersonPoint != PointSource)
            throw new InvalidDataException("The reload effect template is no longer one gate over one point burst.");
        int wrapper = TagView.TargetOrSelf(bytes, in works[0].OwnerPointer);
        ref var node = ref TagView.Edit<SEffectNode>(bytes, wrapper);
        var systems = EditRows(bytes, in node.SystemRows, SEffectNodeRow.Class);
        if (TagView.ClassAt(bytes, wrapper) != SEffectNode.Class || node.NodeKind != ParticleNode
            || systems.Length != 1
            || Rows(bytes, in systems[0].LocationIndices, ExpressionCode.Class) is not [var slot] || slot.Value != 0)
            throw new InvalidDataException("The reload effect template node is no longer one timed particle system.");

        channels[0].ChannelHashReloading0xecde609e = ReloadingChannel;
        systems[0].ParticleSystem = system;
        // The location kind and flags are written in its source row and in both side arrays.
        ref var source = ref sources[0];
        source.LocationKind0Object2WorldWeapon3FirstPersonPoint = PointLocation;
        source.LocationMask0x3fObject0x22Point = PointFlags;
        locations[0].LocationMask = PointFlags;
        flags[0].LocationFlags = PointFlags;
        source.AttachmentHash = pointHash;
        names[0].AttachmentHash = pointHash;
        node.StartMin = delay; node.StartMax = delay;
        node.DurationMin = duration; node.DurationMax = duration;
        Member<byte>(bytes, resource, nameof(SParticleSequenceResource.LifecycleBit0StartOnSpawnBit1Retire)) = Lifecycle;
        return bytes;
    }

    /** A member of the sequence resource at `resource`, viewed alone and writable. */
    private static ref M Member<M>(byte[] bytes, int resource, string member) where M : unmanaged =>
        ref Unsafe.AsRef(in TagView.Member<SParticleSequenceResource, M>(bytes, resource, member));

    private static ReadOnlySpan<T> Rows<T>(byte[] bytes, ref readonly TagArray<T> array, uint elementClass) where T : unmanaged =>
        TagView.CheckedRows(bytes, in array, elementClass, TagArray.MaximumReaderRows);

    /** Checked rows of an array field read from `bytes`, writable. */
    private static Span<T> EditRows<T>(byte[] bytes, ref readonly TagArray<T> array, uint elementClass) where T : unmanaged
    {
        var rows = Rows(bytes, in array, elementClass);
        return rows.IsEmpty ? [] : MemoryMarshal.CreateSpan(ref Unsafe.AsRef(in rows[0]), rows.Length);
    }

    private static uint Tag(ushort package, int entry) => TagHashes.Make(package, entry);
}
