using SunriseStudio.Formats;

namespace SunriseStudio.Editor;

/**
 * Why Sunrise refuses a reward that grants an item at a quantity, shared by season pass rows and reward pool entries. The
 * resolver grants an item that opens when acquired one at a time (state/rewards/reward_resolver.cpp resolve_item and
 * acquire_item). Any other item is placed as itself (state_account_reward_grant_runtime.cpp prepare_reward_grant): a perk
 * needs an account acquisition flag, quantity 1 and no socket overrides, an instance goes to a character bucket one at a
 * time, and only an instance takes socket overrides. Sunrise also refuses a stack whose item or bucket has an equipment slot.
 * Studio does not copy that pairing, so the game decides it.
 */
internal static class RewardRefusals
{
    /** The perk bucket (state/build_data/inventory/buckets/definition.h kPerkBucketId). */
    internal const byte PerkBucket = 37;
    // The bucket arrays Sunrise places rewards in (buckets/definition.h ArraySelector character and profile).
    private const byte CharacterArray = 0, ProfileArray = 1;
    internal const string WrapperQuantity = "Sunrise refuses this reward: an item that opens when acquired is granted one at a time.";
    internal const string PerkAcquisition =
        "Sunrise refuses this reward: a perk needs an account acquisition flag, quantity 1 and no socket overrides.";
    internal const string OtherInventory = "Sunrise refuses this reward: the item's bucket is not a character or account inventory.";
    internal const string InstanceQuantity = "Sunrise refuses this reward: an instanced item goes to a character bucket, one at a time.";
    internal const string StackSockets = "Sunrise refuses this reward: only an instanced item takes socket overrides.";

    /**
     * Why Sunrise refuses granting this item at this quantity, or empty when it would grant it.
     * @param sockets True when the reward carries socket overrides.
     */
    public static string Grant(InvestmentCatalog catalog, InvestmentTables tables, int item, long quantity, bool sockets)
    {
        if (item < 0 || item >= catalog.Items.Count) return "";
        if (tables.OpensOnAcquisition(item)) return quantity != 1 ? WrapperQuantity : "";
        return Placement(tables, catalog.Items[item], quantity, sockets);
    }

    /**
     * The placement refusals of an item granted as itself, in prepare_reward_grant's order. A bucket the installed table does
     * not list has no descriptor, which the game reports itself.
     */
    internal static string Placement(InvestmentTables tables, InvestmentItem item, long quantity, bool sockets)
    {
        if (item.Bucket == PerkBucket)
            return !AccountAcquisition(tables, item.Index) || quantity != 1 || sockets ? PerkAcquisition : "";
        if (tables.Bucket(item.Bucket) is not { } bucket) return "";
        if (bucket.ArraySelector is not (CharacterArray or ProfileArray)) return OtherInventory;
        if (item.Instanced) return bucket.ArraySelector != CharacterArray || quantity != 1 ? InstanceQuantity : "";
        return sockets ? StackSockets : "";
    }

    /**
     * True when the item's acquisition flag binds to the account bank. Sunrise keeps only such a flag
     * (package_reward_build.cpp RewardBuild::read_item).
     */
    internal static bool AccountAcquisition(InvestmentTables tables, int item) =>
        tables.AcquisitionFlag(item) is var flag && flag != ushort.MaxValue && tables.Unlocks.AccountFlag(flag);
}
