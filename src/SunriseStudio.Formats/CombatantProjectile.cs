using System.Buffers.Binary;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/** A float field of the projectile payload; Offset is its byte offset in the config. */
public readonly record struct CombatantProjectileValue(string Name, int Offset, float Value);

/** A tag field of the projectile payload; Tag is 0xFFFFFFFF when empty. */
public readonly record struct CombatantProjectileLink(string Name, int Offset, uint Tag);

/**
 * The projectile payload behind a projectile config's Definition link.
 * Speed is measured across enemies; lifetime and the 0x74 value are assumed.
 */
public sealed record CombatantProjectile(uint ConfigTag, int Payload, IReadOnlyList<CombatantProjectileValue> Values,
    IReadOnlyList<CombatantProjectileLink> Links)
{
    public const uint PayloadClass = C8080388f.Class;
    public const uint ImpactTableClass = 0x80808BCD, SoundClass = 0x80809802, AreaDamageClass = 0x80803EB0;
    public const uint None = 0xFFFFFFFF;

    // Float fields by payload member; the names are ours.
    private static readonly (string Name, string Member)[] FloatFields =
    [
        ("speed", nameof(C8080388f.Unk88)),
        ("lifetime", nameof(C8080388f.Unk68)),
        ("value_74", nameof(C8080388f.Unk74)),
        ("value_7c", nameof(C8080388f.Unk7c)),
    ];

    // Tag fields: the per-surface impact table, a sound event, and an area damage record.
    private static readonly (string Name, string Member)[] LinkFields =
    [
        ("impact_table", nameof(C8080388f.Unk1e0)),
        ("sound", nameof(C8080388f.Unk1f0)),
        ("area_damage", nameof(C8080388f.Unk200)),
    ];

    public float Speed => Value("speed");
    public uint ImpactTable => Link("impact_table");
    public uint Sound => Link("sound");
    public uint AreaDamage => Link("area_damage");

    public float Value(string name) => Values.Single(value => value.Name == name).Value;
    public uint Link(string name) => Links.Single(link => link.Name == name).Tag;

    /**
     * Reads the projectile payload of one config.
     * @return Null when the config's definition is not a projectile payload that names this config.
     */
    public static CombatantProjectile? Read(uint configTag, ReadOnlyMemory<byte> config)
    {
        try
        {
            var tag = config.Span;
            if (!TagView.FitsThrough<SEntityConfig>(tag, 0, nameof(SEntityConfig.Definition))) return null;
            int payload = TagPointer.Checked(tag, TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition)));
            if (payload < TagView.ClassWordSize || !TagView.Fits<C8080388f>(tag, payload)
                || TagView.ClassAt(tag, payload) != PayloadClass || TagRead.U32(tag, payload) != configTag) return null;
            var values = new CombatantProjectileValue[FloatFields.Length];
            for (int index = 0; index < values.Length; index++)
            {
                int at = payload + TagView.FieldOffset<C8080388f>(FloatFields[index].Member);
                values[index] = new(FloatFields[index].Name, at, TagRead.F32(tag, at));
            }
            var links = new CombatantProjectileLink[LinkFields.Length];
            for (int index = 0; index < links.Length; index++)
            {
                int at = payload + TagView.FieldOffset<C8080388f>(LinkFields[index].Member);
                links[index] = new(LinkFields[index].Name, at, TagRead.U32(tag, at));
            }
            return new(configTag, payload, values, links);
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException
            or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /** Sets one float field by its name. */
    public static byte[] SetValue(uint configTag, ReadOnlyMemory<byte> config, string name, float value)
    {
        if (!float.IsFinite(value)) throw new ArgumentException("The value must be a finite number.", nameof(value));
        var field = Require(configTag, config).Values.Where(row => row.Name == name).Select(row => (int?)row.Offset).FirstOrDefault()
            ?? throw new ArgumentOutOfRangeException(nameof(name), "The projectile has no such value.");
        byte[] bytes = config.ToArray();
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(field), value);
        return bytes;
    }

    /** Sets one tag field by its name; None clears it. */
    public static byte[] SetLink(uint configTag, ReadOnlyMemory<byte> config, string name, uint tag)
    {
        var field = Require(configTag, config).Links.Where(row => row.Name == name).Select(row => (int?)row.Offset).FirstOrDefault()
            ?? throw new ArgumentOutOfRangeException(nameof(name), "The projectile has no such link.");
        if (tag == 0) throw new ArgumentException("An empty link is 0xFFFFFFFF, not 0.", nameof(tag));
        byte[] bytes = config.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(field), tag);
        return bytes;
    }

    /** Points the projectile at another per-surface impact table, which carries the impact effects and sounds. */
    public static byte[] SetImpactTable(uint configTag, ReadOnlyMemory<byte> config, uint table) =>
        SetLink(configTag, config, "impact_table", table);

    private static CombatantProjectile Require(uint configTag, ReadOnlyMemory<byte> config) =>
        Read(configTag, config) ?? throw new InvalidDataException("The config has no projectile payload.");
}

/** A projectile entity: the entity a gun barrel spawns per shot, with its projectile and sequence configs. */
public static class CombatantProjectileEntity
{
    public const uint SequenceClass = 0x808084E9;

    /**
     * The component config tags of an entity, in row order, with the byte offset of each row's tag word.
     * @return Empty when the entity has no component rows.
     */
    public static IReadOnlyList<(int Offset, uint Config)> Components(ReadOnlySpan<byte> entity)
    {
        ref readonly var root = ref TagView.Checked<SEntity>(entity, 0);
        var rows = TagView.Rows(entity, in root.Components, SPatternComponent.Class);
        var output = new (int, uint)[rows.Length];
        for (int index = 0; index < rows.Length; index++)
            output[index] = (TagView.OffsetOf(entity, in rows[index].Config), rows[index].Config);
        return output;
    }

    /**
     * Replaces one component config of the entity, the way to swap its travel sequence.
     * The entity keeps offsets into the old config, so the new config needs the same layout.
     * @param classOf Gives a tag's definition class, to check the new config is of the same class.
     */
    public static byte[] ReplaceComponent(ReadOnlyMemory<byte> entity, uint oldConfig, uint newConfig, Func<uint, uint> classOf)
    {
        if (classOf(oldConfig) != classOf(newConfig) || classOf(newConfig) == 0)
            throw new ArgumentException("The new config must have the old config's definition class.", nameof(newConfig));
        var rows = Components(entity.Span).Where(row => row.Config == oldConfig).ToArray();
        if (rows.Length == 0) throw new ArgumentOutOfRangeException(nameof(oldConfig), "The entity does not list the config.");
        byte[] bytes = entity.ToArray();
        foreach (var row in rows) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(row.Offset), newConfig);
        return bytes;
    }
}
