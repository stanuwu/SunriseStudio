using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/** One weapon of a loadout row; Offset is the byte offset of the entity word in the config. */
public readonly record struct CombatantLoadoutWeapon(int Index, int Offset, uint Entity);

/** One weighted loadout row; Group is -1 for the fallback list. */
public sealed record CombatantLoadoutGroup(int Group, int Offset, float Weight, int EligibilityBit,
    IReadOnlyList<CombatantLoadoutWeapon> Weapons);

/** The combatant loadout payload: weighted weapon groups and a fallback list of weapon entities. */
public sealed record CombatantLoadout(uint ConfigTag, int Payload, IReadOnlyList<CombatantLoadoutGroup> Groups,
    CombatantLoadoutGroup Fallback)
{
    public const uint PayloadClass = UnknownLoadoutSecondaryComponent.Class;

    /** Every weapon entity the loadout names, groups first. */
    public IEnumerable<uint> Weapons => Groups.SelectMany(group => group.Weapons).Concat(Fallback.Weapons).Select(weapon => weapon.Entity);

    /**
     * Reads the loadout payload of one config.
     * @return Null when the config is not a loadout config or an array does not match its class.
     */
    public static CombatantLoadout? Read(uint configTag, ReadOnlyMemory<byte> config)
    {
        try
        {
            var tag = config.Span;
            if (!TagView.FitsThrough<SEntityConfig>(tag, 0, nameof(SEntityConfig.Definition))) return null;
            int payload = TagPointer.Checked(tag, TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition)));
            if (payload < TagView.ClassWordSize || !TagView.Fits<UnknownLoadoutSecondaryComponent>(tag, payload)
                || TagView.ClassAt(tag, payload) != PayloadClass || TagRead.U32(tag, payload) != configTag) return null;
            int groupStride = Unsafe.SizeOf<UnknownWeaponLoadoutGroup>();
            var groups = TagArray.Checked(tag, payload + TagView.FieldOffset<UnknownLoadoutSecondaryComponent>(
                nameof(UnknownLoadoutSecondaryComponent.LoadoutGroups)), UnknownWeaponLoadoutGroup.Class, groupStride);
            var rows = new CombatantLoadoutGroup[groups.Count];
            for (int index = 0; index < groups.Count; index++)
            {
                int row = groups.Data + index * groupStride;
                ref readonly var group = ref TagView.At<UnknownWeaponLoadoutGroup>(tag, row);
                rows[index] = new(index, row, group.Weight, group.EligibilityBitIndex,
                    WeaponRows(tag, row + TagView.FieldOffset<UnknownWeaponLoadoutGroup>(nameof(UnknownWeaponLoadoutGroup.Weapons))));
            }
            var fallback = new CombatantLoadoutGroup(-1, payload, 0, -1, WeaponRows(tag,
                payload + TagView.FieldOffset<UnknownLoadoutSecondaryComponent>(nameof(UnknownLoadoutSecondaryComponent.FallbackWeapons))));
            return new(configTag, payload, rows, fallback);
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException
            or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /** Points one weapon of a group at another weapon entity; group -1 is the fallback list. */
    public static byte[] SetWeapon(uint configTag, ReadOnlyMemory<byte> config, int group, int weapon, uint entity)
    {
        if (entity is 0 or uint.MaxValue) throw new ArgumentException("The weapon must be an entity tag.", nameof(entity));
        var loadout = Require(configTag, config);
        var row = (group == -1 ? loadout.Fallback : loadout.Groups.ElementAtOrDefault(group))?.Weapons.ElementAtOrDefault(weapon)
            ?? throw new ArgumentOutOfRangeException(nameof(weapon), "The loadout has no such weapon.");
        if (row.Offset == 0) throw new ArgumentOutOfRangeException(nameof(weapon), "The loadout has no such weapon.");
        byte[] bytes = config.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(row.Offset), entity);
        return bytes;
    }

    /** Sets a group's pick weight. */
    public static byte[] SetWeight(uint configTag, ReadOnlyMemory<byte> config, int group, float weight)
    {
        if (!float.IsFinite(weight) || weight < 0) throw new ArgumentException("The weight must be finite and not negative.", nameof(weight));
        var row = Require(configTag, config).Groups.ElementAtOrDefault(group)
            ?? throw new ArgumentOutOfRangeException(nameof(group), "The loadout has no such group.");
        byte[] bytes = config.ToArray();
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(row.Offset + TagView.FieldOffset<UnknownWeaponLoadoutGroup>(
            nameof(UnknownWeaponLoadoutGroup.Weight))), weight);
        return bytes;
    }

    /** The loadout with no group and no fallback weapon, the form a melee-only enemy such as the Thrall holds. */
    public static byte[] WithoutWeapons(uint configTag, ReadOnlyMemory<byte> config)
    {
        var loadout = Require(configTag, config);
        byte[] bytes = config.ToArray();
        // An empty array is a zero count and a zero link; the old rows stay as unread bytes.
        foreach (string field in new[] { nameof(UnknownLoadoutSecondaryComponent.LoadoutGroups), nameof(UnknownLoadoutSecondaryComponent.FallbackWeapons) })
            bytes.AsSpan(loadout.Payload + TagView.FieldOffset<UnknownLoadoutSecondaryComponent>(field), TagArray.FieldSize).Clear();
        return bytes;
    }

    /** The weapon rows of the array whose field is at `field`; throws on a bad header. */
    private static CombatantLoadoutWeapon[] WeaponRows(ReadOnlySpan<byte> tag, int field)
    {
        int stride = Unsafe.SizeOf<UnknownWeaponReference>();
        var weapons = TagArray.Checked(tag, field, UnknownWeaponReference.Class, stride);
        int entity = TagView.FieldOffset<UnknownWeaponReference>(nameof(UnknownWeaponReference.WeaponEntity));
        var rows = new CombatantLoadoutWeapon[weapons.Count];
        for (int index = 0; index < weapons.Count; index++)
        {
            int at = weapons.Data + index * stride + entity;
            rows[index] = new(index, at, TagRead.U32(tag, at));
        }
        return rows;
    }

    private static CombatantLoadout Require(uint configTag, ReadOnlyMemory<byte> config) =>
        Read(configTag, config) ?? throw new InvalidDataException("The config has no loadout payload.");
}
