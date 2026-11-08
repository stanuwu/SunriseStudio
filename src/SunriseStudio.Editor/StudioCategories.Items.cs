using System.Collections.Concurrent;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;
using PassTableView = SunriseStudio.Tags.Generated.C80807cd9;
using PoolTableView = SunriseStudio.Tags.Generated.RewardPoolTable;
using RootView = SunriseStudio.Tags.Generated.InvestmentRoot;

namespace SunriseStudio.Editor;

/** Investment item, vendor and perk categories; item kinds come from the cached item facts. */
public static partial class StudioCategories
{
    internal const string VendorsName = "Vendors", PerksName = "Perks", SeasonPassName = "Season pass",
        RewardPoolsName = "Reward pools";
    internal const string InvestmentTablesName = "Investment tables";
    private static readonly string[] ItemSections = ["sunrise.investment", ItemRewardsPage.PageId, "sunrise.related"];
    // Categories follow the type line rather than the pursuit's shape, so Quests and Bounties both list both pursuit pages.
    // Either page counts as the category's view.
    private static readonly string[] QuestSections = [QuestPage.PageId, PursuitPage.PageId, "sunrise.investment",
        ItemRewardsPage.PageId, "sunrise.related"];
    private static readonly string[] BountySections = [PursuitPage.PageId, QuestPage.PageId, "sunrise.investment",
        ItemRewardsPage.PageId, "sunrise.related"];
    private const int PursuitViews = 2;
    // One shared kind string per category and type; item and content rows repeat a few hundred kinds.
    private static readonly ConcurrentDictionary<(string Category, string Type), string> ItemKinds = new();

    // Home-view icon and tooltip of each item category except Weapons.
    private static readonly Dictionary<string, (StudioIcon Icon, string Tooltip)> ItemLooks = new(StringComparer.Ordinal)
    {
        [InvestmentItemKinds.Armor] = (StudioIcon.Player, "Helmets, gauntlets, chest, leg and class armor"),
        [InvestmentItemKinds.Ghosts] = (StudioIcon.Player, "Ghost shells"),
        [InvestmentItemKinds.Vehicles] = (StudioIcon.Player, "Sparrows"),
        [InvestmentItemKinds.Ships] = (StudioIcon.Player, "Jumpships"),
        [InvestmentItemKinds.Subclasses] = (StudioIcon.Light, "Subclass items"),
        [InvestmentItemKinds.Shaders] = (StudioIcon.Shader, "Gear colour and material items"),
        [InvestmentItemKinds.Emblems] = (StudioIcon.Frame, "Player card emblems"),
        [InvestmentItemKinds.Emotes] = (StudioIcon.Play, "Emote items"),
        [InvestmentItemKinds.Finishers] = (StudioIcon.Cube, "Finisher items"),
        [InvestmentItemKinds.ClanBanners] = (StudioIcon.Pin, "Clan banner items"),
        [InvestmentItemKinds.Artifacts] = (StudioIcon.Cube, "Seasonal artifact items"),
        [InvestmentItemKinds.Mods] = (StudioIcon.Plus, "Mods, ornaments and other socket plugs"),
        [InvestmentItemKinds.Currencies] = (StudioIcon.Cube, "Tokens and other currencies"),
        [InvestmentItemKinds.Engrams] = (StudioIcon.Cube, "Engrams that decode into gear"),
        [InvestmentItemKinds.Consumables] = (StudioIcon.Cube, "Consumables, materials and packages"),
        [InvestmentItemKinds.Bounties] = (StudioIcon.Check, "Bounties and their objectives"),
        [InvestmentItemKinds.Quests] = (StudioIcon.Map, "Quests and quest steps"),
        [InvestmentItemKinds.Other] = (StudioIcon.Cube, "Items no other category matches")
    };

    static partial void AddItemCategories(List<StudioCategory> categories)
    {
        categories.Add(new(InvestmentItemKinds.Weapons, "Weapons", ItemsGroup, StudioIcon.Spark,
            "Weapons with their stats, perks, model and parts",
            ["sunrise.investment", ItemRewardsPage.PageId, "sunrise.weapon", "sunrise.scene", "sunrise.materials",
                "sunrise.shaders", "sunrise.related"],
            ModelPane: true, OpenContext: tag => new AssetOpenContext { WeaponItemTag = tag }));
        foreach (string name in InvestmentItemKinds.Categories)
        {
            if (name == InvestmentItemKinds.Weapons) continue;
            var (icon, tooltip) = ItemLooks.TryGetValue(name, out var look) ? look : (StudioIcon.Cube, name);
            bool model = name is InvestmentItemKinds.Armor or InvestmentItemKinds.Ghosts
                or InvestmentItemKinds.Vehicles or InvestmentItemKinds.Ships;
            bool pursuits = name is InvestmentItemKinds.Quests or InvestmentItemKinds.Bounties;
            var sections = name == InvestmentItemKinds.Quests ? QuestSections : name == InvestmentItemKinds.Bounties ? BountySections : ItemSections;
            categories.Add(new(name, name, ItemsGroup, icon, tooltip, sections, ModelPane: model,
                Views: pursuits ? PursuitViews : 1));
        }
        categories.Add(new(VendorsName, VendorsName, ItemsGroup, StudioIcon.Group, "Vendors and what they sell", ItemSections));
        categories.Add(new(PerksName, PerksName, ItemsGroup, StudioIcon.Build, "Perk gameplay programs",
            ["sunrise.perk-behavior", "sunrise.graph", "sunrise.related"]));
        categories.Add(new(SeasonPassName, SeasonPassName, TablesGroup, StudioIcon.Grid, "Progression ranks and their rewards",
            [SeasonPassPage.PageId, "sunrise.related"], Home: false));
        categories.Add(new(RewardPoolsName, RewardPoolsName, TablesGroup, StudioIcon.Tree, "Reward pools of engrams and packages",
            [RewardPoolsPage.PageId, "sunrise.related"], Home: false));
        categories.Add(new(InvestmentTablesName, InvestmentTablesName, TablesGroup, StudioIcon.Folder,
            "Inventory buckets of the installed investment data", [BucketsPage.PageId, "sunrise.related"], Home: false));
    }

    /** The rows of the home view's Tables group. Each opens one table of the investment root on its page. */
    internal static readonly StudioTableRow[] Tables =
    [
        new("Season pass", StudioIcon.Grid, "Progression ranks and their rewards", SeasonPassName, SeasonPassPage.PageId,
            catalog => Root(catalog).SlotTag(InvestmentRootSlots.Progressions)),
        new("Reward pools", StudioIcon.Tree, "Reward pools of engrams and packages", RewardPoolsName, RewardPoolsPage.PageId,
            catalog => Root(catalog).SlotTag(InvestmentRootSlots.Pools)),
        new("Buckets", StudioIcon.Folder, "Inventory buckets and their installed items", InvestmentTablesName,
            BucketsPage.PageId, catalog => Root(catalog).Tag)
    ];

    /** The installation's investment root, whose slots name the tables. */
    private static InvestmentRoot Root(PackageCatalog catalog) =>
        InvestmentRoot.Locate(catalog, new InvestmentSources().Root, out string why) ?? throw new InvalidDataException(why);

    static partial void ClassifyItem(PackageEntry entry, StudioNameIndex names, ref StudioCategoryMatch? match)
    {
        if (!AssetCategories.IsStructured(entry)) return;
        switch (entry.Reference)
        {
            case InvestmentCatalog.ItemClass when names.TryGetItem(entry.TagHash, out var facts):
                var (category, type) = InvestmentItemKinds.Classify(facts);
                string kind = ItemKinds.GetOrAdd((category, type), key =>
                    key.Type.Length == 0 ? key.Category : $"{key.Category} / {key.Type}");
                string rarity = InvestmentItemKinds.TierName(facts.Tier);
                // Tier 255 is unset, so the row keeps the plain text colour.
                match = new(kind, $"{facts.Hash:X8} {facts.DisplayTag:X8} {rarity} {facts.TypeLine}",
                    facts.Tier == 255 ? -1 : facts.Tier);
                break;
            case InvestmentCatalog.VendorClass: match = new(VendorsName); break;
            case InvestmentSandboxPerkAuthoring.GameplayClass: match = new(PerksName); break;
            // The installed build has one asset of each table class. The pages check that it is the root's table.
            case PassTableView.Class: match = new(SeasonPassName, "season pass progression ranks rewards"); break;
            case PoolTableView.Class: match = new(RewardPoolsName, "reward pools engram packages"); break;
            case RootView.Class: match = new(InvestmentTablesName, "investment tables buckets"); break;
        }
    }
}
