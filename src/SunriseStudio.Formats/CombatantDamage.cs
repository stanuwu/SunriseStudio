using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Scene.Collision;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/** One per-damage-type scale row of a part; Offset is the float's byte offset in the config. */
public readonly record struct CombatantDamageTypeScale(int Index, int Offset, float Scale);

/** One damage part row. The meaning of Health is assumed; Pool indexes the payload's pool rows. */
public sealed record CombatantDamagePart(int Index, int Offset, uint NameHash, float Health, sbyte Kind, int Pool,
    float Scale, IReadOnlyList<CombatantDamageTypeScale> TypeScales)
{
    /** The part's name, its hash when the name is not known, or "(none)" for an empty hash. */
    public string Name => CollisionRegions.Name(NameHash) is { Length: > 0 } name ? name : "(none)";
}

/** One damage pool row; Fraction is its initial fraction. */
public readonly record struct CombatantDamagePool(int Index, int Offset, float Fraction);

/** The damage component payload behind a damage config's Definition link. */
public sealed record CombatantDamage(uint ConfigTag, int Payload, IReadOnlyList<CombatantDamagePart> Parts,
    IReadOnlyList<CombatantDamagePool> Pools)
{
    public const uint PayloadClass = SCombatantDamagePayload.Class;

    /**
     * Reads the damage payload of one config.
     * @return Null when the config is not a damage config or any array does not match its class.
     */
    public static CombatantDamage? Read(uint configTag, ReadOnlyMemory<byte> config)
    {
        try
        {
            var tag = config.Span;
            if (!TagView.FitsThrough<SEntityConfig>(tag, 0, "Definition")) return null;
            int payload = TagPointer.Checked(tag, TagView.FieldOffset<SEntityConfig>("Definition"));
            // The payload is typed by the marker before it and names its own config first.
            // It needs to hold only the bytes through Pools.
            if (payload < TagView.ClassWordSize || !TagView.FitsThrough<SCombatantDamagePayload>(tag, payload, "Pools")
                || TagView.ClassAt(tag, payload) != PayloadClass
                || TagRead.U32(tag, payload + TagView.FieldOffset<SCombatantDamagePayload>("OwnerConfig.Tag")) != configTag) return null;
            int poolStride = Unsafe.SizeOf<SCombatantDamagePool>(), partStride = Unsafe.SizeOf<SCombatantDamagePart>();
            int typeStride = Unsafe.SizeOf<SCombatantDamageTypeScale>();
            var pools = TagArray.Checked(tag, payload + TagView.FieldOffset<SCombatantDamagePayload>("Pools"),
                SCombatantDamagePool.Class, poolStride);
            var poolRows = new CombatantDamagePool[pools.Count];
            for (int index = 0; index < pools.Count; index++)
            {
                ref readonly float fraction = ref TagView.At<SCombatantDamagePool>(tag, pools.Data + index * poolStride).InitialFraction;
                poolRows[index] = new(index, TagView.OffsetOf(tag, in fraction), fraction);
            }
            var parts = TagArray.Checked(tag, payload + TagView.FieldOffset<SCombatantDamagePayload>("Parts"),
                SCombatantDamagePart.Class, partStride);
            var partRows = new CombatantDamagePart[parts.Count];
            for (int index = 0; index < parts.Count; index++)
            {
                int row = parts.Data + index * partStride;
                ref readonly var part = ref TagView.At<SCombatantDamagePart>(tag, row);
                int pool = part.PoolIndex;
                if (pool < 0 || pool >= pools.Count) return null;
                var types = TagArray.Checked(tag, TagView.OffsetOf(tag, in part.ScaleOptions), SCombatantDamageTypeScale.Class, typeStride);
                var typeRows = new CombatantDamageTypeScale[types.Count];
                for (int type = 0; type < types.Count; type++)
                {
                    ref readonly float scale = ref TagView.At<SCombatantDamageTypeScale>(tag, types.Data + type * typeStride).Scale;
                    typeRows[type] = new(type, TagView.OffsetOf(tag, in scale), scale);
                }
                partRows[index] = new(index, row, part.RegionNameHash, part.Health,
                    (sbyte)part.Kind, pool, part.DamageScale, typeRows);
            }
            return new(configTag, payload, partRows, poolRows);
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException
            or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /** True when the config's definition link is typed as a damage payload, readable or not. */
    public static bool IsDamageConfig(ReadOnlyMemory<byte> config)
    {
        var tag = config.Span;
        if (!TagView.FitsThrough<SEntityConfig>(tag, 0, "Definition")) return false;
        int definition = TagView.FieldOffset<SEntityConfig>("Definition");
        long delta = TagView.At<RelativePointer<Unknown>>(tag, definition).Relative;
        if (delta == 0 || delta < int.MinValue || delta > int.MaxValue) return false;
        long payload = definition + delta;
        return payload >= TagView.ClassWordSize && payload <= tag.Length - TagView.ClassWordSize && TagView.ClassAt(tag, (int)payload) == PayloadClass;
    }

    /** Sets a part's damage scale. */
    public static byte[] SetPartScale(uint configTag, ReadOnlyMemory<byte> config, int part, float value) =>
        Write(config, value, Require(configTag, config).Parts.ElementAtOrDefault(part) is { } row
            ? TagView.OffsetOf(config.Span, in TagView.At<SCombatantDamagePart>(config.Span, row.Offset).DamageScale) : null);

    /** Sets one per-damage-type scale of a part's override row. */
    public static byte[] SetTypeScale(uint configTag, ReadOnlyMemory<byte> config, int part, int row, float value) =>
        Write(config, value, Require(configTag, config).Parts.ElementAtOrDefault(part)?.TypeScales
            .Where(scale => scale.Index == row).Select(scale => (int?)scale.Offset).FirstOrDefault());

    /** Sets a pool's initial fraction. */
    public static byte[] SetPoolFraction(uint configTag, ReadOnlyMemory<byte> config, int pool, float value) =>
        Write(config, value, Require(configTag, config).Pools
            .Where(row => row.Index == pool).Select(row => (int?)row.Offset).FirstOrDefault());

    private static CombatantDamage Require(uint configTag, ReadOnlyMemory<byte> config) =>
        Read(configTag, config) ?? throw new InvalidDataException("The config has no damage payload.");

    private static byte[] Write(ReadOnlyMemory<byte> config, float value, int? offset)
    {
        if (offset is not { } at) throw new ArgumentOutOfRangeException(nameof(offset), "The damage row does not exist.");
        if (!float.IsFinite(value)) throw new ArgumentException("The value must be a finite number.", nameof(value));
        byte[] bytes = config.ToArray();
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value);
        return bytes;
    }
}
