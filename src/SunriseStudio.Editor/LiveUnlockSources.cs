using System.Runtime.CompilerServices;
using SunriseStudio.Formats;
using SunriseStudio.Live;

namespace SunriseStudio.Editor;

/**
 * Binds an installed flag or value read to the saved row the game reads. The game reads saved rows by bank and row, so
 * Studio sends a row only when its proven label names the row Sunrise binds the slot to. Sunrise binds a slot to the
 * first mapping row naming it and tries the account, profile and character tables in that order
 * (package_node_build.cpp read_slot_map, package_reward_build.cpp RewardConditions::bind).
 */
internal static class LiveUnlockSources
{
    /** Sunrise's unmapped-slot marker (items/packages/internal.h kUnmappedSlot). Mapping rows from it on are never bound. */
    private const int UnmappedRow = ushort.MaxValue;
    /** The saved banks and the slot kind that names each, in the order Sunrise tries their mapping tables. */
    private static readonly (LiveUnlockBank Bank, int Kind)[] FlagBanks =
    [
        (LiveUnlockBank.Account, InvestmentUnlocks.AccountFlagKind), (LiveUnlockBank.Profile, InvestmentUnlocks.ProfileFlagKind),
        (LiveUnlockBank.Character, InvestmentUnlocks.CharacterFlagKind)
    ];
    private static readonly (LiveUnlockBank Bank, int Kind)[] ValueBanks =
    [
        (LiveUnlockBank.Account, InvestmentUnlocks.AccountValueKind), (LiveUnlockBank.Character, InvestmentUnlocks.CharacterValueKind)
    ];
    private static readonly ConditionalWeakTable<InvestmentUnlocks, Dictionary<(bool Value, int Slot),
        LiveUnlockSource>> bound = new();

    /**
     * @return The saved row, or null for a class flag (Studio reads the selected character's class from Hello), an
     * unproven label, a computed or per-item source, a slot Sunrise binds elsewhere, or any instruction other than a
     * flag or value read.
     */
    public static LiveUnlockSource? Of(InvestmentUnlocks unlocks, InvestmentUnlockInstruction instruction)
    {
        bool value = instruction.Opcode == InvestmentUnlocks.ValueOpcode;
        if ((!value && instruction.Opcode != InvestmentUnlocks.FlagOpcode) || unlocks.ClassOf(instruction) >= 0
            || !unlocks.Label(instruction).Proven) return null;
        int slot = (int)instruction.Operand;
        var studio = value
            ? Row(LiveUnlockKind.Value, ValueBanks, unlocks.ValueSlots[slot].Kind, unlocks.ValueSlots[slot].BankRow)
            : Row(LiveUnlockKind.Flag, FlagBanks, unlocks.FlagSlots[slot].Kind, unlocks.FlagSlots[slot].BankRow);
        return studio is { } source && bound.GetValue(unlocks, Bind).TryGetValue((value, slot), out var sunrise)
            && sunrise == source ? source : null;
    }

    /** The saved row a slot's own kind and bank row name, or null for a kind that names no saved bank. */
    private static LiveUnlockSource? Row(LiveUnlockKind kind, (LiveUnlockBank Bank, int Kind)[] banks, int slotKind, ushort row)
    {
        foreach (var bank in banks)
            if (bank.Kind == slotKind) return new(kind, bank.Bank, row);
        return null;
    }

    /** Every slot the installed mapping tables name, bound to its row as Sunrise binds it. */
    private static Dictionary<(bool Value, int Slot), LiveUnlockSource> Bind(InvestmentUnlocks unlocks)
    {
        var rows = new Dictionary<(bool, int), LiveUnlockSource>();
        void Add(bool value, IReadOnlyList<InvestmentSlotMap> maps, (LiveUnlockBank Bank, int Kind)[] banks)
        {
            foreach (var (bank, kind) in banks)
            {
                int map = value ? InvestmentUnlocks.ValueMap(kind) : InvestmentUnlocks.FlagMap(kind);
                if (map >= maps.Count) continue;
                var destinations = maps[map].Destinations;
                // A negative destination addresses no slot.
                for (int row = 0; row < destinations.Count && row < UnmappedRow; row++)
                    if (destinations[row] >= 0)
                        rows.TryAdd((value, destinations[row]),
                            new(value ? LiveUnlockKind.Value : LiveUnlockKind.Flag, bank, (ushort)row));
            }
        }
        Add(false, unlocks.FlagMaps, FlagBanks);
        Add(true, unlocks.ValueMaps, ValueBanks);
        return rows;
    }
}
