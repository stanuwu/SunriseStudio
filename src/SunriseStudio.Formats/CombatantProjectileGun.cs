using System.Buffers.Binary;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/** A stat of the gun; Offset is the byte offset of the stat's base float in the config. */
public readonly record struct CombatantGunStat(string Name, int Offset, float Value);

/** A projectile slot of the gun barrel; Entity is 0 or 0xFFFFFFFF when the slot is empty. */
public readonly record struct CombatantGunProjectile(int Slot, int Offset, uint Entity);

/**
 * The gun payload of a weapon entity: projectile slots and stats of the weapon definition family.
 * Stat names come from the game's own field-name hashes; see the ability doc for the grade.
 */
public sealed record CombatantGun(uint ConfigTag, int Payload, uint PayloadClass,
    IReadOnlyList<CombatantGunProjectile> Projectiles, IReadOnlyList<CombatantGunStat> Stats)
{
    /** Every installed weapon definition class; all share this layout through the barrel. */
    public static readonly IReadOnlySet<uint> Classes = new HashSet<uint>
    {
        C80c1c3e9.Class, C80c1c3cc.Class, C80c22c04.Class, C80bfdfeb.Class,
        C80c1c533.Class, C80bfdd70.Class, C80c1c429.Class, C80c1c4c2.Class,
    };

    private static readonly int Barrel = TagView.FieldOffset<C80c1c3e9>(nameof(C80c1c3e9.Barrel));
    private static readonly int StatValue = TagView.FieldOffset<C808045c2>(nameof(C808045c2.Unk4));

    private static readonly int[] ProjectileSlots =
    [
        Barrel + TagView.FieldOffset<C80803865>(nameof(C80803865.UnkB80)),
        Barrel + TagView.FieldOffset<C80803865>(nameof(C80803865.UnkB90)),
        Barrel + TagView.FieldOffset<C80803865>(nameof(C80803865.UnkBa0)),
    ];

    private static readonly int FiringStats = Barrel + TagView.FieldOffset<C80803865>(nameof(C80803865.H3411f31c));
    private static readonly int SpreadStats = Barrel + TagView.FieldOffset<C80803865>(nameof(C80803865.H29a756a8));
    private static readonly int ProjectileStats = Barrel + TagView.FieldOffset<C80803865>(nameof(C80803865.H660eabd4));

    // Stat structs by payload offset; each holds its base value at +4.
    private static readonly (string Name, int Stat)[] StatFields =
    [
        ("damage_falloff_distance_start", TagView.FieldOffset<C80c1c3e9>(nameof(C80c1c3e9.HA9dad93c))),
        ("damage_falloff_distance_end", TagView.FieldOffset<C80c1c3e9>(nameof(C80c1c3e9.HF2dda61f))),
        ("ready_time", TagView.FieldOffset<C80c1c3e9>(nameof(C80c1c3e9.HFf216ea2))),
        ("damage_multiplier_base", Barrel + TagView.FieldOffset<C80803865>(nameof(C80803865.H7b8da954))),
        ("precision_damage_fraction", Barrel + TagView.FieldOffset<C80803865>(nameof(C80803865.H1af17b4a))),
        ("error_deceleration_time", Barrel + TagView.FieldOffset<C80803865>(nameof(C80803865.HEef601e3))),
        ("recoil_deceleration_time", Barrel + TagView.FieldOffset<C80803865>(nameof(C80803865.H400b7a5d))),
        ("firing_accuracy", FiringStats + TagView.FieldOffset<C80803871>(nameof(C80803871.H6f2c1224))),
        ("firing_zoomed_accuracy", FiringStats + TagView.FieldOffset<C80803871>(nameof(C80803871.H200faf63))),
        ("firing_recoil", FiringStats + TagView.FieldOffset<C80803871>(nameof(C80803871.HAd448985))),
        ("spread_angle_scale", SpreadStats + TagView.FieldOffset<C80803870>(nameof(C80803870.H81266e27))),
        ("initial_speed_scale", ProjectileStats + TagView.FieldOffset<C8080386f>(nameof(C8080386f.H6a84ba24))),
    ];

    public static IEnumerable<string> StatNames => StatFields.Select(field => field.Name);

    public float Stat(string name) => Stats.Single(stat => stat.Name == name).Value;

    /**
     * Reads the gun payload of one config.
     * @return Null when the definition is not a weapon definition class that names this config.
     */
    public static CombatantGun? Read(uint configTag, ReadOnlyMemory<byte> config)
    {
        try
        {
            var tag = config.Span;
            if (!TagView.FitsThrough<SEntityConfig>(tag, 0, nameof(SEntityConfig.Definition))) return null;
            int payload = TagPointer.Checked(tag, TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition)));
            if (payload < TagView.ClassWordSize || TagView.ClassAt(tag, payload) is not { } cls || !Classes.Contains(cls)
                || !TagView.FitsThrough<C80c1c3e9>(tag, payload, nameof(C80c1c3e9.Barrel))
                || TagRead.U32(tag, payload) != configTag) return null;
            var slots = new CombatantGunProjectile[ProjectileSlots.Length];
            for (int slot = 0; slot < slots.Length; slot++)
                slots[slot] = new(slot, payload + ProjectileSlots[slot], TagRead.U32(tag, payload + ProjectileSlots[slot]));
            var stats = new CombatantGunStat[StatFields.Length];
            for (int index = 0; index < stats.Length; index++)
            {
                int at = payload + StatFields[index].Stat + StatValue;
                stats[index] = new(StatFields[index].Name, at, TagRead.F32(tag, at));
            }
            return new(configTag, payload, cls, slots, stats);
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException
            or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /** Points one barrel projectile slot at another projectile entity. */
    public static byte[] SetProjectile(uint configTag, ReadOnlyMemory<byte> config, int slot, uint entity)
    {
        var row = Require(configTag, config).Projectiles.Where(item => item.Slot == slot).Select(item => (int?)item.Offset).FirstOrDefault()
            ?? throw new ArgumentOutOfRangeException(nameof(slot), "The barrel has no such projectile slot.");
        if (entity is 0 or uint.MaxValue) throw new ArgumentException("The projectile entity must be a tag.", nameof(entity));
        byte[] bytes = config.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(row), entity);
        return bytes;
    }

    /** Points every barrel slot that names oldEntity at newEntity. */
    public static byte[] ReplaceProjectile(uint configTag, ReadOnlyMemory<byte> config, uint oldEntity, uint newEntity)
    {
        var rows = Require(configTag, config).Projectiles.Where(item => item.Entity == oldEntity).ToArray();
        if (rows.Length == 0) throw new ArgumentOutOfRangeException(nameof(oldEntity), "No barrel slot names that entity.");
        byte[] bytes = config.ToArray();
        foreach (var row in rows) bytes = SetProjectile(configTag, bytes, row.Slot, newEntity);
        return bytes;
    }

    /** Sets a stat's base value by its name. */
    public static byte[] SetStat(uint configTag, ReadOnlyMemory<byte> config, string name, float value)
    {
        if (!float.IsFinite(value)) throw new ArgumentException("The value must be a finite number.", nameof(value));
        var at = Require(configTag, config).Stats.Where(stat => stat.Name == name).Select(stat => (int?)stat.Offset).FirstOrDefault()
            ?? throw new ArgumentOutOfRangeException(nameof(name), "The gun has no such stat.");
        byte[] bytes = config.ToArray();
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value);
        return bytes;
    }

    private static CombatantGun Require(uint configTag, ReadOnlyMemory<byte> config) =>
        Read(configTag, config) ?? throw new InvalidDataException("The config has no weapon definition payload.");
}
