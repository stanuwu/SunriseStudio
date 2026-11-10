using SunriseStudio.Packages;

namespace SunriseStudio.Editor.Creation;

/** Clone for every investment item category; New is blocked here, and Weapons New is registered elsewhere. */
public static partial class CreationTypes
{
    // Lower-case singular names for dialog titles; a category not listed reads "item".
    private static readonly Dictionary<string, string> ItemNouns = new(StringComparer.Ordinal)
    {
        [InvestmentItemKinds.Weapons] = "weapon", [InvestmentItemKinds.Armor] = "armor",
        [InvestmentItemKinds.Ghosts] = "ghost", [InvestmentItemKinds.Vehicles] = "sparrow",
        [InvestmentItemKinds.Ships] = "ship", [InvestmentItemKinds.Subclasses] = "subclass",
        [InvestmentItemKinds.Shaders] = "shader", [InvestmentItemKinds.Emblems] = "emblem",
        [InvestmentItemKinds.Emotes] = "emote", [InvestmentItemKinds.Finishers] = "finisher",
        [InvestmentItemKinds.ClanBanners] = "clan banner", [InvestmentItemKinds.Artifacts] = "artifact",
        [InvestmentItemKinds.Mods] = "plug", [InvestmentItemKinds.Currencies] = "currency",
        [InvestmentItemKinds.Engrams] = "engram", [InvestmentItemKinds.Consumables] = "consumable",
        [InvestmentItemKinds.Bounties] = "bounty", [InvestmentItemKinds.Quests] = "quest"
    };

    public static string ItemNoun(string category) => ItemNouns.GetValueOrDefault(category) ?? "item";

    static partial void AddItemTypes()
    {
        foreach (string category in InvestmentItemKinds.Categories)
        {
            string noun = ItemNoun(category);
            Add(category, CreationMode.Clone, new($"Clone {noun}",
                $"A copy of an installed {noun} with its own name. May not work in game.", "",
                workspace => new ItemWorkflow(workspace, category)));
            if (category == InvestmentItemKinds.Weapons) continue;
            Add(category, CreationMode.New, new($"New {noun}", "", category switch
            {
                InvestmentItemKinds.Armor => "No armor model writer. Use Clone.",
                InvestmentItemKinds.Mods => "No plug writer from scratch. Use Clone.",
                _ => NoScratchItem
            }));
        }
    }
}
