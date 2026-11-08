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
    /** The mapping tables in the order Sunrise tries them. Flag maps sit at +8, +24 and +40, value maps at +8 and +24. */
    private static readonly (LiveUnlockBank Bank, int Map)[] FlagBanks =
        [(LiveUnlockBank.Account, 0), (LiveUnlockBank.Profile, 1), (LiveUnlockBank.Character, 2)];
    private static readonly (LiveUnlockBank Bank, int Map)[] ValueBanks = [(LiveUnlockBank.Account, 0),
        (LiveUnlockBank.Character, 1)];
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
        // Slot kinds number the banks the way InvestmentUnlocks.FlagLabel and ValueLabel read them.
        LiveUnlockSource? studio;
        if (value)
        {
            var slot = unlocks.ValueSlots[(int)instruction.Operand];
            studio = slot.Kind switch
            {
                1 => new(LiveUnlockKind.Value, LiveUnlockBank.Account, slot.BankRow),
                2 => new(LiveUnlockKind.Value, LiveUnlockBank.Character, slot.BankRow),
                _ => null
            };
        }
        else
        {
            var slot = unlocks.FlagSlots[(int)instruction.Operand];
            studio = slot.Kind switch
            {
                1 => new(LiveUnlockKind.Flag, LiveUnlockBank.Account, slot.BankRow),
                2 => new(LiveUnlockKind.Flag, LiveUnlockBank.Profile, slot.BankRow),
                3 => new(LiveUnlockKind.Flag, LiveUnlockBank.Character, slot.BankRow),
                _ => null
            };
        }
        return studio is { } source
            && bound.GetValue(unlocks, Bind).TryGetValue((value, (int)instruction.Operand), out var sunrise)
            && sunrise == source ? source : null;
    }

    /** Every slot the installed mapping tables name, bound to its row as Sunrise binds it. */
    private static Dictionary<(bool Value, int Slot), LiveUnlockSource> Bind(InvestmentUnlocks unlocks)
    {
        var rows = new Dictionary<(bool, int), LiveUnlockSource>();
        void Add(bool value, IReadOnlyList<InvestmentSlotMap> maps, (LiveUnlockBank Bank, int Map)[] banks)
        {
            foreach (var (bank, map) in banks.Where(bank => bank.Map < maps.Count))
            {
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
