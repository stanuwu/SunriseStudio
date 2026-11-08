using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/** One burst of a sequence: its system, its node's duration and its attachment point. */
public sealed record WeaponParticleBurst(uint SystemTag, float DurationSeconds, uint AttachmentHash);

/** Builds particle sequences and their entities for the firing row. */
public static class WeaponParticleBinding
{
    public const uint EntityTemplate = 0x80C5CE35, ConfigTemplate = 0x80C5CE34, SequenceClass = SParticleSequenceResource.Class;
    // Two timed particle nodes under one parallel root, each on its own location.
    public const uint PairEntityTemplate = 0x80B3B903, PairConfigTemplate = 0x80B3B902;
    // Sequence lifecycle byte: bit 0 starts on spawn, bit 1 retires the node when it ends.
    public static readonly int LifecycleOffset =
        TagView.FieldOffset<SParticleSequenceResource>(nameof(SParticleSequenceResource.LifecycleBit0StartOnSpawnBit1Retire));
    public const byte StartOnSpawn = 1, RetireOnEnd = 2;
    private const byte LifecycleStartAndRetire = StartOnSpawn | RetireOnEnd;
    // The template locations use the object frame (kind 0, flags 0x3F). Kind 3 with flags 0x22 is the
    // first-person pose; kind 2 would place the burst on the world weapon.
    private const byte PointLocation = 3, ObjectFrameMask = 0x3F, PointMask = 0x22;
    private static readonly int InstanceFlagsAt =
        TagView.FieldOffset<C808084d7>($"{nameof(C808084d7.H846fde3f)}.{nameof(C8080860c.InstanceFlags)}");

    /** A checked profile: entity, sequence and the sequence's stock systems. */
    private sealed record Profile(uint Entity, uint Config, uint[] Systems);

    private static readonly Profile Single = new(EntityTemplate, ConfigTemplate, [0x80C5CE33]);

    private static readonly Profile Pair = new(PairEntityTemplate, PairConfigTemplate, [0x80B3B901, 0x80C67FD4]);

    private static Profile For(int bursts) => bursts switch
    {
        1 => Single,
        2 => Pair,
        _ => throw new NotSupportedException("A weapon particle sequence holds one or two bursts.")
    };

    /** The checked entity and sequence templates for a sequence of one or two bursts. */
    public static uint EntityTemplateFor(int bursts) => For(bursts).Entity;
    public static uint ConfigTemplateFor(int bursts) => For(bursts).Config;

    /**
     * The timed sequence alone, for a firing row slot entity the weapon spawns once and restarts per shot.
     * @param bursts One or two bursts; each plays on its own location.
     * @param entry Package entry of the sequence record.
     * @return The sequence record; its lifecycle neither starts on spawn nor retires the node.
     */
    public static ProjectAssetAddition PrepareSequence(PackageCatalog catalog, IReadOnlyList<WeaponParticleBurst> bursts,
        ushort packageId, int entry, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!PackageWriter.IsNewPackageId(packageId) || catalog.ContainsInstalledPackage(packageId) || entry is < 0 or > 8191)
            throw new InvalidDataException("A per-shot particle sequence needs an unused package entry.");
        uint tag = TagHashes.Make(packageId, entry);
        if (catalog.ContainsTag(tag) || bursts.Any(burst => burst.SystemTag is 0 or uint.MaxValue
                || !TagHashes.IsTag(burst.SystemTag) || burst.SystemTag == tag))
            throw new InvalidDataException("The per-shot particle sequence overlaps another asset.");
        var profile = For(bursts.Count);
        byte[] config = BuildConfig(catalog, bursts, tag);
        catalog.TryGetEntry(profile.Config, out var configEntry);
        return new(name + ".sequence", config, new(packageId, configEntry.Reference, configEntry.TypeInfo,
            name + ".sequence", entry), profile.Config);
    }

    /** The checked profile entity for one or two bursts, with its sequence link moved to the given sequence. */
    public static byte[] ProfileEntity(PackageCatalog catalog, uint sequenceTag, int bursts = 1)
    {
        var profile = For(bursts);
        return WeaponConfigEdits.RetagRoot(Read(catalog, profile.Entity, SEntity.Class), profile.Config, sequenceTag);
    }

    /** The resource offset of a sequence config, or -1 when the config is not a sequence. */
    public static int SequenceResource(byte[] config)
    {
        if (!TagView.FitsThrough<SEntityConfig>(config, 0, nameof(SEntityConfig.Definition))) return -1;
        ref readonly var definition = ref TagView.Member<SEntityConfig, RelativePointer<Unknown>>(config, 0, nameof(SEntityConfig.Definition));
        long resource = TagView.OffsetOf(config, in definition) + definition.Relative;
        // The resource must hold a 4-byte word at the lifecycle offset.
        return resource >= 4 && resource <= config.Length - LifecycleOffset - 4
            && TagView.ClassAt(config, (int)resource) == SParticleSequenceResource.Class ? (int)resource : -1;
    }

    /** The per-location flag array field of a sequence config's default instance, or -1 when there is none. */
    internal static int InstanceFlagsField(byte[] config)
    {
        if (!TagView.FitsThrough<SEntityConfig>(config, 0, nameof(SEntityConfig.DefaultInstance))) return -1;
        ref readonly var instance = ref TagView.Member<SEntityConfig, RelativePointer<Unknown>>(config, 0, nameof(SEntityConfig.DefaultInstance));
        long target = TagView.OffsetOf(config, in instance) + instance.Relative;
        if (instance.IsNull || target < TagView.ClassWordSize || target > config.Length
            || TagView.ClassAt(config, (int)target) != C808084d7.Class) return -1;
        int field = (int)target + InstanceFlagsAt;
        return TagView.Fits<TagArray<SSequenceLocationFlags>>(config, field) ? field : -1;
    }

    /** Refuses a system that is not a CPU emitter with one shared frame slot. */
    public static void ValidateSystem(PackageCatalog catalog, uint systemTag)
    {
        var system = new EffectReader(catalog).ReadSystem(systemTag);
        if (system is null || system.GpuSimulated || system.Emitter is not { SpawnSlot: 0, EmitterSlot: 0, SlotCount: 1 })
            throw new NotSupportedException("Weapon particle binding supports a CPU emitter with one shared frame slot.");
    }

    /**
     * The checked profile with each burst's system, duration and point written in, retagged to its destination.
     * Burst i plays on work node i, which spawns and simulates in location i.
     */
    private static byte[] BuildConfig(PackageCatalog catalog, IReadOnlyList<WeaponParticleBurst> bursts, uint destination)
    {
        var profile = For(bursts.Count);
        foreach (var burst in bursts)
        {
            if (!float.IsFinite(burst.DurationSeconds) || burst.DurationSeconds <= 0 || burst.DurationSeconds > 60)
                throw new InvalidDataException("Particle duration must be greater than zero and at most 60 seconds.");
            if (burst.AttachmentHash is 0 or uint.MaxValue)
                throw new InvalidDataException("The particle attachment name cannot be invalid.");
            if (catalog.ContainsTag(burst.SystemTag)) ValidateSystem(catalog, burst.SystemTag);
        }
        byte[] config = Read(catalog, profile.Config, SEntityConfig.Class);
        int resource = TagView.TargetOrSelf(config, in TagView.Checked<SEntityConfig>(config, 0).Definition);
        if (TagView.ClassAt(config, resource) != SParticleSequenceResource.Class
            || !TagView.Fits<SParticleSequenceResource>(config, resource))
            throw new InvalidDataException("The particle sequence resource changed class or runs past the config.");
        if ((TagView.At<SParticleSequenceResource>(config, resource).LifecycleBit0StartOnSpawnBit1Retire
                & LifecycleStartAndRetire) != LifecycleStartAndRetire)
            throw new InvalidDataException("The particle sequence lost its finite spawn lifecycle.");
        ref var sequence = ref TagView.Edit<SParticleSequenceResource>(config, resource);
        // A firing slot node is spawned once at equip; a sequence that retires deletes it after the first shot.
        sequence.LifecycleBit0StartOnSpawnBit1Retire &= unchecked((byte)~LifecycleStartAndRetire);
        var works = TagView.CheckedRows(config, in sequence.WorkNodes, SSequenceFlowRow.Class, TagArray.MaximumReaderRows);
        var sources = EditRows(config, in sequence.H846fde3f.Update, SSequenceLocationSource.Class);
        var masks = EditRows(config, in sequence.H846fde3f.Locations, SSequenceLocationMask.Class);
        int flagsField = InstanceFlagsField(config);
        if (flagsField < 0) throw new InvalidDataException("The particle sequence layout changed.");
        var flags = EditRows(config, in TagView.At<TagArray<SSequenceLocationFlags>>(config, flagsField), SSequenceLocationFlags.Class);
        var names = EditRows(config, in sequence.H846fde3f.Debug, SSequenceLocationName.Class);
        if (works.Length != bursts.Count || sources.Length != 2 || masks.Length != 2 || flags.Length != 2 || names.Length != 2)
            throw new InvalidDataException("The particle sequence layout changed.");
        for (int index = 0; index < bursts.Count; index++)
        {
            var burst = bursts[index];
            int wrapper = TagView.TargetOrSelf(config, in works[index].OwnerPointer);
            if (TagView.ClassAt(config, wrapper) != SEffectNode.Class || !TagView.Fits<SEffectNode>(config, wrapper)
                || TagView.At<SEffectNode>(config, wrapper).NodeKind != 4 || (TagView.At<SEffectNode>(config, wrapper).NodeFlags & 0x18) != 0)
                throw new InvalidDataException("A particle sequence node is no longer a timed particle node.");
            ref var node = ref TagView.Edit<SEffectNode>(config, wrapper);
            var systems = EditRows(config, in node.SystemRows, SEffectNodeRow.Class);
            if (systems.Length != 1 || systems[0].ParticleSystem != profile.Systems[index])
                throw new InvalidDataException("The particle system binding changed.");
            var slots = TagView.CheckedRows(config, in systems[0].LocationIndices, ExpressionCode.Class, TagArray.MaximumReaderRows);
            if (slots.Length == 0 || slots[0].Value != index)
                throw new InvalidDataException("The particle spawn frame mapping changed.");
            ref var source = ref sources[index];
            ref var flag = ref flags[index];
            if (source.LocationKind0Object2WorldWeapon3FirstPersonPoint != 0 || source.SubObject != 1
                || source.FrameFlags != byte.MaxValue || masks[index].LocationMask != ObjectFrameMask
                || source.LocationMask0x3fObject0x22Point != ObjectFrameMask || flag.LocationFlags != ObjectFrameMask)
                throw new InvalidDataException("The particle sequence needs its own object frame.");
            systems[0].ParticleSystem = burst.SystemTag;
            // The location's mask is written in its source row and in both side arrays.
            source.LocationKind0Object2WorldWeapon3FirstPersonPoint = PointLocation;
            source.LocationMask0x3fObject0x22Point = PointMask; masks[index].LocationMask = PointMask; flag.LocationFlags = PointMask;
            source.AttachmentHash = burst.AttachmentHash; names[index].AttachmentHash = burst.AttachmentHash;
            // The one-slot emitter spawns and simulates in its own location.
            int slotHeader = new TagArray(config, TagView.OffsetOf(config, in systems[0].LocationIndices)).HeaderOffset;
            TagView.SetArray(config, in systems[0].LocationIndices, 1, slotHeader);
            // The header repeats the count; its other words stay as they are.
            TagView.Edit<STypedArrayMarker>(config, slotHeader).Count = 1;
            node.DurationMin = burst.DurationSeconds; node.DurationMax = burst.DurationSeconds;
        }
        RetagConfig(config, profile, destination);
        return config;
    }

    private static byte[] Read(PackageCatalog catalog, uint tag, uint type)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != type)
            throw new NotSupportedException($"Weapon particle template {tag:X8} is missing or not of class {type:X8}.");
        return (byte[])catalog.Read(tag).Clone();
    }

    /** Every owner pair of the profile retains its types and reciprocal offsets. */
    private static void RetagConfig(byte[] bytes, Profile profile, uint destination)
    {
        var owners = new HashSet<int>();
        foreach (var (primary, resource) in OwnerPairs(bytes))
        {
            var forward = TagOffsetPointer.Read(bytes, primary);
            var back = TagOffsetPointer.Read(bytes, resource);
            if (forward.Tag != profile.Config || back.Tag != profile.Config || forward.Offset != resource || back.Offset != primary)
                throw new InvalidDataException("A particle sequence owner pair is not reciprocal.");
            owners.Add(primary); owners.Add(resource);
        }
        for (int at = 0; at + 4 <= bytes.Length; at += 4)
            if (TagRead.U32(bytes, at) == profile.Config && !owners.Contains(at))
                throw new NotSupportedException("An unclassified particle owner cannot be cloned.");
        foreach (int at in owners) Put32(bytes, at, destination);
    }

    /**
     * The owner pointers the default instance and the sequence resource hold for each other.
     * @return Each pair as the instance field offset and the resource field offset.
     */
    private static List<(int Primary, int Resource)> OwnerPairs(byte[] config)
    {
        ref readonly var root = ref TagView.Checked<SEntityConfig>(config, 0);
        ref readonly var instance = ref TagView.Checked<C808084d7>(config, TagView.TargetOrSelf(config, in root.DefaultInstance));
        ref readonly var resource = ref TagView.Checked<SParticleSequenceResource>(config, TagView.TargetOrSelf(config, in root.Definition));
        var pairs = new List<(int Primary, int Resource)>
        {
            (TagView.OffsetOf(config, in instance.Unk0), TagView.OffsetOf(config, in resource.Unk0)),
            (TagView.OffsetOf(config, in instance.H846fde3f.Unk0), TagView.OffsetOf(config, in resource.H846fde3f.LocationTableOwner)),
            (TagView.OffsetOf(config, in instance.H880ca149.OwnerPair0x808072c0),
                TagView.OffsetOf(config, in resource.H880ca149.OwnerPair0x808072c1)),
            (TagView.OffsetOf(config, in instance.HCa95f655.OwnerPair0x808072cb),
                TagView.OffsetOf(config, in resource.HCa95f655.OwnerPair0x808072c7))
        };
        AddRows(in instance.HC78e66c7, in resource.FlowNodes);
        AddRows(in instance.HE3cc5c98, in resource.WorkNodes);
        return pairs;

        // Row k of the instance array pairs with row k of the resource array, and so do their node objects.
        void AddRows(ref readonly TagArray<C808093e5> instanceArray, ref readonly TagArray<SSequenceFlowRow> resourceArray)
        {
            var owners = TagView.CheckedRows(config, in instanceArray, C808093e5.Class, TagArray.MaximumReaderRows);
            var rows = TagView.CheckedRows(config, in resourceArray, SSequenceFlowRow.Class, TagArray.MaximumReaderRows);
            if (owners.Length != rows.Length) throw new InvalidDataException("A particle sequence owner pair is not reciprocal.");
            for (int index = 0; index < rows.Length; index++)
                pairs.Add((TagView.OffsetOf(config, in owners[index].Unk0), TagView.OffsetOf(config, in rows[index].Unk0)));
            for (int index = 0; index < rows.Length; index++)
                pairs.Add((NodeOwner(config, TagView.TargetOrSelf(config, in owners[index].OwnerPointer)),
                    NodeOwner(config, TagView.TargetOrSelf(config, in rows[index].OwnerPointer))));
        }
    }

    /** The owner pointer field of a flow or work node object at `node`. */
    private static int NodeOwner(byte[] config, int node) => node + (TagView.ClassAt(config, node) switch
    {
        C808093d6.Class => TagView.FieldOffset<C808093d6>(nameof(C808093d6.Unk0)),
        C808093d7.Class => TagView.FieldOffset<C808093d7>(nameof(C808093d7.Unk0)),
        SEffectNodeLink.Class => TagView.FieldOffset<SEffectNodeLink>(nameof(SEffectNodeLink.Unk0)),
        SEffectNode.Class => TagView.FieldOffset<SEffectNode>(nameof(SEffectNode.OwnerLink)),
        _ => throw new InvalidDataException("A particle sequence node has an unexpected class.")
    });

    /** Checked rows of an array field read from `tag`, writable. */
    private static Span<T> EditRows<T>(byte[] tag, ref readonly TagArray<T> array, uint elementClass) where T : unmanaged
    {
        var rows = TagView.CheckedRows(tag, in array, elementClass, TagArray.MaximumReaderRows);
        return rows.IsEmpty ? [] : MemoryMarshal.CreateSpan(ref Unsafe.AsRef(in rows[0]), rows.Length);
    }

    private static void Put32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
}
