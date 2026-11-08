namespace SunriseStudio.Packages;

/** Facts of one investment item: its index row, bucket, tier, display type line and which blocks are set. */
public readonly record struct InvestmentItemFacts(ushort Index, uint Hash, uint DisplayTag, byte Bucket, byte Tier,
    string TypeLine, bool HasObjectives, bool HasPlug, bool HasSack);

/** The one item-kind rule; the browser and the investment catalog both use it. */
public static class InvestmentItemKinds
{
    public const string Weapons = "Weapons", Armor = "Armor", Ghosts = "Ghosts", Vehicles = "Vehicles", Ships = "Ships",
        Shaders = "Shaders", Subclasses = "Subclasses", ClanBanners = "Clan banners", Emblems = "Emblems",
        Emotes = "Emotes", Finishers = "Finishers", Artifacts = "Artifacts", Currencies = "Currencies",
        Engrams = "Engrams", Bounties = "Bounties", Quests = "Quests", Mods = "Mods and plugs",
        Consumables = "Consumables", Other = "Other items";
    /** Held pursuits sit in inventory bucket 40 (State items kPursuitBucketId). */
    public const byte PursuitBucket = 40;
    /** Sunrise keeps at most this many objectives of a pursuit (State pursuits kObjectiveCapacity). Some installed items list 8. */
    public const int PursuitObjectiveCapacity = 7;
    /** Bucket id 255 names no bucket (State buckets kUnavailableBucketId). */
    public const byte NoBucket = byte.MaxValue;

    /** Every category Classify returns, in browser order. */
    public static IReadOnlyList<string> Categories { get; } =
    [
        Weapons, Armor, Ghosts, Vehicles, Ships, Subclasses, Shaders, Emblems, Emotes, Finishers, ClanBanners,
        Artifacts, Mods, Currencies, Engrams, Consumables, Bounties, Quests, Other
    ];

    // Buckets with one fixed category, by inventory bucket id.
    private static readonly Dictionary<byte, string> BucketCategories = new()
    {
        [8] = Ghosts, [9] = Vehicles, [10] = Ships, [12] = Emotes, [14] = Shaders, [16] = Subclasses,
        [17] = ClanBanners, [27] = Emblems, [41] = Emotes, [47] = Finishers, [49] = Artifacts
    };

    private static readonly HashSet<string> ConsumableLines = new(StringComparer.Ordinal)
        { "Consumable", "Material", "Package", "Container", "Treasure Map", "Seasonal Bonus" };

    /** Category and type of one item; the first matching rule wins. */
    public static (string Category, string Type) Classify(in InvestmentItemFacts facts)
    {
        string line = facts.TypeLine;
        byte bucket = facts.Bucket;
        if (bucket <= 2) return (Weapons, line.Length != 0 ? line : SlotName(bucket));
        if (bucket <= 7) return (Armor, SlotName(bucket));
        if (BucketCategories.TryGetValue(bucket, out string? fixedCategory))
            return line == "Quest Step" ? (Quests, line) : (fixedCategory, line);
        if (IsCurrency(bucket, line)) return (Currencies, line);
        if (bucket == 31 || line == "Engram") return (Engrams, line);
        // The type line wins over the bucket and the plug block, which also hold materials and containers.
        if (ConsumableLines.Contains(line)) return (Consumables, line);
        if (bucket == PursuitBucket && line != "Quest Step") return (Bounties, line);
        if (facts.HasObjectives || line is "Quest" or "Quest Step" || bucket == 33) return (Quests, line);
        if (facts.HasPlug) return (Mods, line);
        return (Other, line.Length != 0 ? line : $"Bucket {bucket}");
    }

    public static bool IsCurrency(byte bucket, string typeLine) => bucket is >= 21 and <= 25 || typeLine == "Currency";

    /** Equipment slot of a weapon or armor bucket; empty for other buckets. */
    public static string SlotName(byte bucket) => bucket switch
    {
        0 => "Kinetic", 1 => "Energy", 2 => "Power", 3 => "Helmet", 4 => "Gauntlets", 5 => "Chest armor",
        6 => "Leg armor", 7 => "Class armor", _ => ""
    };

    /** A bucket's display name: its equipment slot, Pursuits, the one category it holds, Currencies, or "Bucket N". */
    public static string BucketLabel(byte bucket) => SlotName(bucket) is { Length: > 0 } slot ? slot
        : bucket == PursuitBucket ? "Pursuits" : BucketCategories.GetValueOrDefault(bucket)
            ?? (IsCurrency(bucket, "") ? Currencies : $"Bucket {bucket}");

    /** Rarity name of a tier byte; 255 means unset. */
    public static string TierName(byte tier) => tier switch
    {
        0 => "None", 1 => "Common", 2 => "Uncommon", 3 => "Rare", 4 => "Legendary", 5 => "Exotic", 255 => "Unset",
        _ => $"Tier {tier}"
    };
}
