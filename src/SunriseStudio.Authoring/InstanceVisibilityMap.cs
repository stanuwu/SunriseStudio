using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/** Tome lookups follow the stored probe chain; unused keys and negative values are not visibility. */
internal sealed class InstanceVisibilityMap
{
    private readonly byte[] data;
    private readonly int[] buckets, values;
    internal IReadOnlyCollection<ulong> Keys { get; }

    internal InstanceVisibilityMap(byte[] bytes)
    {
        data = bytes;
        buckets = InstanceTagReads.Rows<SVisibilityTomeMap, SVisibilityBucket>(bytes, 0, "HAda0152e.Buckets", SVisibilityBucket.Class);
        values = InstanceTagReads.Rows<SVisibilityTomeMap, SI32Element>(bytes, 0, "HAda0152e.Values", SI32Element.Class);
        if (buckets.Length == 0 || (buckets.Length & (buckets.Length - 1)) != 0)
            throw new InvalidDataException("The visibility hash table has an invalid bucket count.");
        Keys = buckets.Select(row => TagView.At<SVisibilityBucket>(bytes, row).InstanceId).ToHashSet();
    }

    /** Bit 30 admits a value slot; bit 31 continues an unequal-key probe. */
    internal bool Contains(ulong id)
    {
        uint mask = checked((uint)buckets.Length - 1);
        uint index = Hash(id, InstanceTagReads.Field<SVisibilityTomeMap, uint>(data, 0, "HAda0152e.HashSeed")) & mask;
        for (int count = 0; count < buckets.Length; count++)
        {
            ref readonly var bucket = ref TagView.At<SVisibilityBucket>(data, buckets[index]);
            uint control = bucket.ControlBit30HasSlotBit31ContinueLow30Slot;
            if (bucket.InstanceId == id)
            {
                if ((control & 0x40000000) == 0) return false;
                uint slot = control & 0x3FFFFFFF;
                if (slot >= values.Length) throw new InvalidDataException("The visibility value slot leaves its array.");
                int value = TagView.At<SI32Element>(data, values[slot]).Value;
                if (value < 0) return false;
                if ((uint)value >> 3 >= ushort.MaxValue)
                    throw new InvalidDataException("The visibility bit index exceeds its native storage.");
                return true;
            }
            if ((control & 0x80000000) == 0) return false;
            index = (index + 1) & mask;
        }
        throw new InvalidDataException("The visibility hash probe does not terminate.");
    }

    private static uint Hash(ulong id, uint seed)
    {
        unchecked
        {
            ulong wideSeed = seed | ((ulong)seed << 32);
            ulong x = (id << 18) + (~id ^ wideSeed);
            ulong y = 21 * (x ^ (x >> 31));
            ulong z = 65 * (y ^ (y >> 11));
            return (uint)z ^ (uint)(z >> 22);
        }
    }
}
