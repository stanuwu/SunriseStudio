namespace SunriseStudio.Packages;

/** A tag hash is Base + (package << EntryBits) + entry; it adds, since the package bits overlap Base. */
public static class TagHashes
{
    public const uint Base = 0x80800000;
    public const int EntryBits = 13;
    public const int EntriesPerPackage = 1 << EntryBits;

    /** @return The tag hash of one entry; throws when the entry is outside its package. */
    public static uint Make(ushort packageId, int entryIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entryIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(entryIndex, EntriesPerPackage);
        return checked(Base + ((uint)packageId << EntryBits) + (uint)entryIndex);
    }

    /** True when `value` is at or above Base. */
    public static bool IsTag(uint value) => value >= Base;

    /** @return The package bits, not narrowed to 16 bits; `tag` must be at or above Base. */
    public static uint Package(uint tag) => (tag - Base) >> EntryBits;

    /** @return The entry bits; `tag` must be at or above Base. */
    public static int Entry(uint tag) => (int)((tag - Base) & (EntriesPerPackage - 1));

    /** @return The package id, narrowed to 16 bits, and the entry; throws below Base. */
    public static (ushort PackageId, int EntryIndex) Split(uint tag)
    {
        if (!IsTag(tag)) throw new ArgumentOutOfRangeException(nameof(tag));
        return ((ushort)Package(tag), Entry(tag));
    }
}
