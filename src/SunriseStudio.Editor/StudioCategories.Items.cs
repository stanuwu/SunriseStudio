using System.Collections.Concurrent;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** Investment item, vendor and perk categories; item kinds come from the cached item facts. */
public static partial class StudioCategories
{
    internal const string VendorsName = "Vendors", PerksName = "Perks";
    private static readonly string[] ItemSections = ["sunrise.investment", "sunrise.related"];
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
            ["sunrise.investment", "sunrise.weapon", "sunrise.scene", "sunrise.materials", "sunrise.shaders", "sunrise.related"],
            ModelPane: true, OpenContext: tag => new AssetOpenContext { WeaponItemTag = tag }));
        foreach (string name in InvestmentItemKinds.Categories)
        {
            if (name == InvestmentItemKinds.Weapons) continue;
            var (icon, tooltip) = ItemLooks.TryGetValue(name, out var look) ? look : (StudioIcon.Cube, name);
            // Any item with art opens with its model; one without shows its pages alone.
            categories.Add(new(name, name, ItemsGroup, icon, tooltip, ItemSections, ModelPane: true,
                OpenContext: tag => new AssetOpenContext { ItemModelTag = tag }));
        }
        categories.Add(new(VendorsName, VendorsName, ItemsGroup, StudioIcon.Group, "Vendors and what they sell", ItemSections));
        categories.Add(new(PerksName, PerksName, ItemsGroup, StudioIcon.Build, "Perk gameplay programs",
            ["sunrise.perk-behavior", "sunrise.graph", "sunrise.related"]));
    }

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
        }
    }
}
