using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/** A firing row slot entity is the stock slot entity with only its particle sequence replaced. */
public static class WeaponFiringSlotEntity
{
    public const uint EntityClass = SEntity.Class, VariableStoreClass = SVariableStore.Class;

    /**
     * Copies the stock entity onto the authored sequence. Its variable joins into the stock sequence
     * are dropped; the authored sequence reads no named variables.
     * @param sequence Bytes of the authored sequence, a clone of the checked particle profile.
     * @param bursts The sequence's burst count, which picks its profile.
     * @return The new entity bytes.
     */
    public static byte[] Compose(PackageCatalog catalog, uint stockEntity, uint sequenceTag, byte[] sequence, int bursts = 1)
    {
        if (!catalog.TryGetEntry(stockEntity, out var entry) || entry.Reference != EntityClass)
            throw new InvalidDataException($"Firing slot entity {stockEntity:X8} is not an entity.");
        byte[] stock = catalog.Read(stockEntity);
        uint stockSequence = RequirePerShot(catalog.Read, stock, $"The stock muzzle {stockEntity:X8}");
        int stockResource = WeaponParticleBinding.SequenceResource(catalog.Read(stockSequence));
        int resource = WeaponParticleBinding.SequenceResource(sequence);
        if (resource < 0) throw new InvalidDataException("The authored firing sequence is not a sequence config.");

        // The checked profile entity names every interface of the authored sequence layout.
        uint profileConfig = WeaponParticleBinding.ConfigTemplateFor(bursts);
        byte[] profile = catalog.Read(WeaponParticleBinding.EntityTemplateFor(bursts));
        int profileResource = WeaponParticleBinding.SequenceResource(catalog.Read(profileConfig));
        int[] profileRows = InterfaceRows(profile);
        int[] profileConfigs = InterfaceConfigs(profile);
        var layout = new Dictionary<(uint Component, uint Map), (int Row, int Config)>();
        for (int index = 0; index < profileRows.Length; index++)
        {
            ref readonly var profileRow = ref Interface(profile, profileRows[index]);
            if (profileRow.Config.Tag == profileConfig
                && !layout.TryAdd((profileRow.ComponentKey, profileRow.InterfaceMapHash), (profileRows[index], profileConfigs[index])))
                throw new InvalidDataException("The particle profile names one sequence interface twice.");
        }

        byte[] result = stock.ToArray();
        foreach (int row in ConstructionRows(result))
        {
            ref var component = ref TagView.Edit<SPatternComponent>(result, row);
            if (component.Config == stockSequence) component.Config = sequenceTag;
        }
        int[] rows = InterfaceRows(result);
        int[] configs = InterfaceConfigs(result);
        if (rows.Length != configs.Length) throw new InvalidDataException("Entity interface tables differ in length.");
        var matched = new HashSet<(uint, uint)>();
        for (int index = 0; index < rows.Length; index++)
        {
            ref var row = ref TagView.Edit<ActorDescriptorRow>(result, rows[index]);
            ref var config = ref TagView.Edit<ActorDescriptorMetadata>(result, configs[index]);
            if (row.Config.Tag != stockSequence) continue;
            var key = (row.ComponentKey, row.InterfaceMapHash);
            if (!layout.TryGetValue(key, out var source) || !matched.Add(key) || config.Config.Tag != stockSequence
                || !SameFields(in row, in Interface(profile, source.Row))
                || config.Config.Word != TagView.At<ActorDescriptorMetadata>(profile, source.Config).Config.Word
                || row.Config.Offset - stockResource != Interface(profile, source.Row).Config.Offset - profileResource)
                throw new InvalidDataException($"Stock sequence interface {key.Item2:X8} has no matching authored row.");
            // The stock row keeps its ordering fields; only the config and its interface offset change.
            row.Config = row.Config with { Tag = sequenceTag };
            row.Config = row.Config with { Offset = Interface(profile, source.Row).Config.Offset - profileResource + resource };
            config.Config = config.Config with { Tag = sequenceTag };
            config.Config = config.Config with { Offset = TagView.At<ActorDescriptorMetadata>(profile, source.Config).Config.Offset };
        }
        if (matched.Count != layout.Count) throw new InvalidDataException("The stock sequence lacks an authored interface.");
        foreach (int field in JoinFields)
            DropRows(result, field, row => FeedsSequence(result, row, stockSequence));
        for (int at = 0; at + 4 <= result.Length; at += 4)
            if (TagRead.U32(result, at) == stockSequence)
                throw new InvalidDataException("The stock firing entity names its sequence outside its typed tables.");
        return result;
    }

    /**
     * True when the stock entity's variable stores feed only its particle sequence.
     * Such a slot uses the profile entity; a copy would keep stores that nothing reads.
     */
    public static bool FeedsOnlyItsSequence(PackageCatalog catalog, uint stockEntity)
    {
        byte[] stock = catalog.Read(stockEntity), profile = catalog.Read(WeaponParticleBinding.EntityTemplate);
        uint sequence = RequirePerShot(catalog.Read, stock, $"The stock entity {stockEntity:X8}");
        var common = ConstructionRows(profile).Select(row => TagView.At<SPatternComponent>(profile, row).Config)
            .Where(tag => tag != WeaponParticleBinding.ConfigTemplate).ToHashSet();
        bool joinsFeedSequence = JoinFields.SelectMany(field => JoinRows(stock, field))
            .All(row => FeedsSequence(stock, row, sequence));
        bool otherConfigs = ConstructionRows(stock).Select(row => TagView.At<SPatternComponent>(stock, row).Config)
            .Where(tag => tag != sequence).All(tag => common.Contains(tag) || ResourceClass(catalog.Read(tag)) == VariableStoreClass);
        return joinsFeedSequence && otherConfigs;
    }

    /**
     * Refuses an entity the firing row cannot restart on every shot.
     * @param read Reads a config by tag, including authored ones.
     * @param subject Names the entity in the refusal.
     * @return The tag of the entity's one particle sequence.
     */
    public static uint RequirePerShot(Func<uint, byte[]> read, byte[] entity, string subject)
    {
        var configs = ConstructionRows(entity).Select(row => TagView.At<SPatternComponent>(entity, row).Config).ToArray();
        var sequences = configs.Where(tag => WeaponParticleBinding.SequenceResource(read(tag)) >= 0).ToArray();
        if (sequences.Length != 1)
            throw new InvalidDataException($"{subject} needs exactly one particle sequence; it has {sequences.Length}.");
        byte[] sequence = read(sequences[0]);
        byte lifecycle = sequence[WeaponParticleBinding.SequenceResource(sequence) + WeaponParticleBinding.LifecycleOffset];
        if ((lifecycle & (WeaponParticleBinding.StartOnSpawn | WeaponParticleBinding.RetireOnEnd)) != 0)
            throw new InvalidDataException($"{subject} has a sequence that starts on spawn or retires its node "
                + $"(lifecycle {lifecycle:X2}); a firing slot is restarted on every shot.");
        return sequences[0];
    }

    private static uint ResourceClass(byte[] config)
    {
        if (!TagView.FitsThrough<SEntityConfig>(config, 0, nameof(SEntityConfig.Definition))) return 0;
        ref readonly var definition = ref TagView.At<RelativePointer<Unknown>>(config,
            TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition)));
        if (definition.IsNull) return 0;
        long resource = TagView.OffsetOf(config, in definition) + definition.Relative;
        return resource >= TagView.ClassWordSize && resource <= config.Length ? TagView.ClassAt(config, (int)resource) ?? 0 : 0;
    }

    /** True when a join row reads from or writes to the sequence. */
    private static bool FeedsSequence(byte[] entity, int row, uint sequence)
    {
        ref readonly var join = ref TagView.At<SEntityBindingRow>(entity, row);
        return join.ConfigA.Tag == sequence || join.ConfigB.Tag == sequence;
    }

    /** Compares the ordering fields an authored interface row must share with its stock row. */
    private static bool SameFields(ref readonly ActorDescriptorRow left, ref readonly ActorDescriptorRow right) =>
        left.ComponentKey == right.ComponentKey && left.Config.Word == right.Config.Word
        && left.InterfaceMapHash == right.InterfaceMapHash;

    private static ref readonly ActorDescriptorRow Interface(byte[] entity, int row) =>
        ref TagView.At<ActorDescriptorRow>(entity, row);

    private static int[] ConstructionRows(byte[] entity) =>
        WeaponTagRows.Starts<SPatternComponent>(entity, TagView.FieldOffset<SEntity>(nameof(SEntity.Components)),
            SPatternComponent.Class);

    private static int[] InterfaceRows(byte[] entity) =>
        WeaponTagRows.Starts<ActorDescriptorRow>(entity, TagView.FieldOffset<SEntity>("H83bfb495.InterfaceDescriptors"),
            ActorDescriptorRow.Class);

    private static int[] InterfaceConfigs(byte[] entity) =>
        WeaponTagRows.Starts<ActorDescriptorMetadata>(entity, TagView.FieldOffset<SEntity>("H83bfb495.LinksD24ByteRowsConfigAt0"),
            ActorDescriptorMetadata.Class);

    private static int[] JoinRows(byte[] entity, int field) =>
        WeaponTagRows.Starts<SEntityBindingRow>(entity, field, SEntityBindingRow.Class);

    /** The two variable join tables, in table order. */
    private static readonly int[] JoinFields =
        [TagView.FieldOffset<SEntity>("HE3929a53.ConnectionList"), TagView.FieldOffset<SEntity>("HE3929a53.Unknown0x30MustBe0")];

    /** Keeps the join rows the predicate does not select, in order; an emptied table has no array. */
    private static void DropRows(byte[] bytes, int field, Func<int, bool> drop)
    {
        int stride = Unsafe.SizeOf<SEntityBindingRow>();
        int[] rows = JoinRows(bytes, field);
        int[] kept = rows.Where(row => !drop(row)).ToArray();
        if (kept.Length == rows.Length) return;
        byte[][] copies = kept.Select(row => bytes.AsSpan(row, stride).ToArray()).ToArray();
        for (int index = 0; index < rows.Length; index++)
            if (index < copies.Length) copies[index].CopyTo(bytes, rows[index]);
            else bytes.AsSpan(rows[index], stride).Clear();
        int header = new TagArray(bytes, field).HeaderOffset;
        TagView.Edit<STypedArrayMarker>(bytes, header).Count = (ulong)kept.Length;
        TagView.SetArray(bytes, in TagView.At<TagArray<SEntityBindingRow>>(bytes, field), kept.Length, kept.Length == 0 ? null : header);
    }
}
