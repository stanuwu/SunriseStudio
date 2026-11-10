using SunriseStudio.Packages;
using SunriseStudio.Tags.Views;
using RootView = SunriseStudio.Tags.Generated.InvestmentRoot;

namespace SunriseStudio.Formats;

public enum InvestmentSlotState { Loaded, Omitted, Missing, WrongClass }
/** One root slot's table. Bytes is empty unless the table loaded. */
public sealed record InvestmentSlotRead(int Slot, uint Tag, InvestmentSlotState State,
    ReadOnlyMemory<byte> Bytes, string Diagnostic)
{
    public bool Loaded => State == InvestmentSlotState.Loaded;
}

/** The root slots Studio reads, named by the root layout's field for each slot, as Sunrise's root_slot reads them. */
public static class InvestmentRootSlots
{
    /** Character classes. Each row names its class's default finisher. */
    public static readonly int Classes = Of(nameof(RootView.Slot12ClassTable));
    /** Inventory bucket descriptors. Sunrise reads the table without a class check. */
    public static readonly int Buckets = Of(nameof(RootView.Slot17BucketTable));
    /** The item index that pool, pass and preview item numbers refer to. */
    public static readonly int Items = Of(nameof(RootView.Slot48ItemIndex));
    public static readonly int Objectives = Of(nameof(RootView.Slot58Objectives));
    /** Progression definitions, including the season pass ladders. */
    public static readonly int Progressions = Of(nameof(RootView.Slot68Progressions));
    /** Shared unlock expressions that opcode 12 inlines. */
    public static readonly int SharedExpressions = Of(nameof(RootView.Slot109RewardExpressions));
    /** Unlock flag and value mapping tables, and the slot tables keyed by unlock slot. */
    public static readonly int FlagMaps = Of(nameof(RootView.Slot111UnlockFlagMaps)),
        FlagSlots = Of(nameof(RootView.Slot112UnlockFlagSlots)),
        ValueMaps = Of(nameof(RootView.Slot113UnlockValueMaps)), ValueSlots = Of(nameof(RootView.Slot114UnlockValueTable));

    /** The slot whose tag the root layout names field. */
    private static int Of(string field) => (TagView.FieldOffset<RootView>(field) - InvestmentRoot.SlotTable) / InvestmentRoot.SlotStride;
}

/**
 * The one installed investment root. Its table slots follow an 8-byte prefix and are 16 bytes each, tag first. Where the
 * root layout names a slot's tag, the name carries the slot number (Slot68Progressions is slot 68).
 */
public sealed class InvestmentRoot
{
    /** A slot holding all ones names a table the installed build omits. */
    public const uint OmittedSlot = 0xFFFFFFFF;
    /** The root's table slots: an 8-byte prefix, then one 16-byte slot per table, tag first. */
    internal const int SlotTable = 8, SlotStride = 16;
    private readonly ReadOnlyMemory<byte> bytes;
    public uint Tag { get; }
    public int SlotCount { get; }

    private InvestmentRoot(uint tag, ReadOnlyMemory<byte> bytes)
    { Tag = tag; this.bytes = bytes; SlotCount = Math.Max(0, (bytes.Length - SlotTable) / SlotStride); }

    /**
     * Finds the root as the one installed entry of the root class. It never picks between two.
     * @param explicitTag A root tag to use as-is, or 0 to search the installation.
     * @return The root, or null with a diagnostic when no single installed entry carries the root class.
     */
    public static InvestmentRoot? Locate(PackageCatalog catalog, uint explicitTag, out string diagnostic)
    {
        diagnostic = "";
        uint tag = explicitTag;
        if (tag == 0)
        {
            var roots = catalog.Entries.Where(entry => entry.Type is 8 or 16
                && entry.Reference == RootView.Class).Take(2).ToArray();
            if (roots.Length != 1)
            {
                diagnostic = roots.Length == 0 ? "This installation has no investment root." : "The investment root is ambiguous.";
                return null;
            }
            tag = roots[0].TagHash;
        }
        else if (!catalog.ContainsTag(tag))
        {
            diagnostic = "The investment root is not installed.";
            return null;
        }
        return new(tag, catalog.Read(tag));
    }

    public uint SlotTag(int slot)
    {
        if (slot < 0 || slot >= SlotCount) throw new InvalidDataException($"The investment root has no slot {slot}.");
        return TagRead.U32(bytes.Span, SlotTable + slot * SlotStride);
    }

    /** Reads a slot's table and checks its entry class. Omitted reports the all-ones sentinel. */
    public InvestmentSlotRead ReadSlot(PackageCatalog catalog, int slot, uint tableClass)
    {
        uint tag = SlotTag(slot);
        if (tag == OmittedSlot) return new(slot, tag, InvestmentSlotState.Omitted, default,
            $"Slot {slot} is omitted by this installation.");
        if (!catalog.TryGetEntry(tag, out var entry)) return new(slot, tag, InvestmentSlotState.Missing, default,
            $"Slot {slot} names a table that is not installed.");
        if (entry.Type is not (8 or 16) || entry.Reference != tableClass)
            return new(slot, tag, InvestmentSlotState.WrongClass, default, $"Slot {slot} has an unexpected class.");
        return new(slot, tag, InvestmentSlotState.Loaded, catalog.Read(tag), "");
    }

    /** Reads a slot's table whatever its class, as Sunrise reads the bucket table. Omitted and Missing work as in ReadSlot. */
    public InvestmentSlotRead ReadSlotAnyClass(PackageCatalog catalog, int slot)
    {
        uint tag = SlotTag(slot);
        if (tag == OmittedSlot) return new(slot, tag, InvestmentSlotState.Omitted, default,
            $"Slot {slot} is omitted by this installation.");
        if (!catalog.ContainsTag(tag)) return new(slot, tag, InvestmentSlotState.Missing, default,
            $"Slot {slot} names a table that is not installed.");
        return new(slot, tag, InvestmentSlotState.Loaded, catalog.Read(tag), "");
    }
}
