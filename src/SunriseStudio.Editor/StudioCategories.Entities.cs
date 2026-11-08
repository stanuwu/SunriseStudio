using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public static partial class StudioCategories
{
    public const string EnemiesCategory = "Enemies and NPCs", EntityCategory = "Entity";
    private static readonly string[] EntityViewSections =
        ["sunrise.components", "sunrise.scene", "sunrise.materials", "sunrise.shaders", "sunrise.related", "sunrise.graph"];

    static partial void AddEntityCategories(List<StudioCategory> categories)
    {
        // Studio cannot tell friend from foe, so one category holds both.
        categories.Add(new(EnemiesCategory, EnemiesCategory, WorldGroup, StudioIcon.Player,
            "Enemies, bosses and friendly NPCs. The game sets which side they are on.", EntityViewSections, ModelPane: true));
        categories.Add(new(EntityCategory, "Entities", WorldGroup, StudioIcon.Cube,
            "Props, lights, triggers and other placed objects.", EntityViewSections, ModelPane: true));
    }

    /** Entities with an ActorGroup config are enemies and NPCs; every other entity keeps its entity kind. */
    static partial void ClassifyEntity(PackageEntry entry, StudioNameIndex names, ref StudioCategoryMatch? match)
    {
        if (entry.Reference != SEntity.Class || !AssetCategories.IsStructured(entry)) return;
        var game = names.GameNames;
        var (kind, tags) = AssetCategories.Classify(entry, game);
        string search = SearchTags(tags);
        if (game?.Components is { } index && index.HasActorGroup(entry.TagHash))
        {
            string type = index.CombatantName(entry.TagHash) is { Length: > 0 } name ? name
                : index.ObjectType(entry.TagHash) ?? "Unnamed";
            match = new($"{EnemiesCategory} / {type}", search);
            return;
        }
        match = new(kind, search);
    }

    /** Only names the kind and package do not already carry; one string per row adds up over the catalog. */
    internal static string SearchTags(IEnumerable<string> tags) => string.Join(' ', tags.Where(tag =>
        tag.StartsWith("combatant ", StringComparison.Ordinal) || tag.StartsWith("prompt ", StringComparison.Ordinal)
        || tag.StartsWith("object type ", StringComparison.Ordinal)));
}
