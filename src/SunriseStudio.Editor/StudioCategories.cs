using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/**
 * One browser category with its own viewer; Name is the category part of a browser kind. Views counts the leading
 * Sections that are alternative primary views, and an asset has no view of the category only when none of them opens.
 * Home false keeps the category off the home view, for categories a Tables row opens.
 */
public sealed record StudioCategory(
    string Name,
    string Label,
    string Group,
    StudioIcon Icon,
    string Tooltip,
    string[] Sections,
    bool ModelPane = false,
    Func<uint, AssetOpenContext?>? OpenContext = null,
    int Views = 1,
    bool Home = true);

/**
 * A row of the home view's Tables group. It opens its table asset on Page and is listed while Category has rows. Tag throws
 * with the reason when the table cannot be found.
 */
public sealed record StudioTableRow(string Label, StudioIcon Icon, string Tooltip, string Category, string Page,
    Func<PackageCatalog, uint> Tag);

/** Kind is "Name / Type" or "Name"; Tags join the search text; Tier >= 0 colours the row. */
public readonly record struct StudioCategoryMatch(string Kind, string Tags = "", int Tier = -1);

/** The browser categories with a viewer. Each hook below is implemented once, in its own category file. */
public static partial class StudioCategories
{
    public const string ItemsGroup = "Items", TablesGroup = "Tables", WorldGroup = "World", ContentGroup = "Content";
    public const string PreviewPageId = "sunrise.preview";
    // Pages that draw the model view themselves; the model pane hides while one is shown.
    public static readonly IReadOnlySet<string> OwnViewPages = new HashSet<string>(StringComparer.Ordinal) { "sunrise.scene" };
    private static readonly string[] Groups = [ItemsGroup, TablesGroup, WorldGroup, ContentGroup];
    // Both are replaced whole, never changed in place, so worker threads read them without a lock.
    private static StudioCategory[] all = [];
    private static Dictionary<string, StudioCategory> byName = new(StringComparer.Ordinal);
    private static Func<PackageEntry, StudioCategoryMatch?>[] testRules = [];

    /** Items, then Tables, then World, then Content; registration order within a group. */
    public static IReadOnlyList<StudioCategory> All => all;

    static StudioCategories()
    {
        var categories = new List<StudioCategory>();
        AddItemCategories(categories);
        AddGuardianCategories(categories);
        AddEntityCategories(categories);
        AddContentCategories(categories);
        Publish(categories);
    }

    private static void Publish(IEnumerable<StudioCategory> categories)
    {
        var sorted = categories.OrderBy(category => Array.IndexOf(Groups, category.Group) is >= 0 and var at ? at : Groups.Length)
            .ToArray();
        var names = new Dictionary<string, StudioCategory>(StringComparer.Ordinal);
        foreach (var category in sorted)
            if (!names.TryAdd(category.Name, category))
                throw new InvalidOperationException($"Two browser categories are named {category.Name}.");
        byName = names;
        all = sorted;
    }

    public static StudioCategory? Find(string name) => byName.GetValueOrDefault(name);

    public static StudioCategory? ForKind(string kind) =>
        byName.Count == 0 ? null : Find(AssetCategories.SplitKind(kind).Category);

    /** Null keeps the row a normal asset. Worker-safe: cached lookups only, no package reads. */
    public static StudioCategoryMatch? Classify(PackageEntry entry, StudioNameIndex names)
    {
        if (byName.Count == 0) return null;
        StudioCategoryMatch? match = null;
        ClassifyItem(entry, names, ref match);
        if (match is null) ClassifyGuardian(entry, names, ref match);
        if (match is null) ClassifyEntity(entry, names, ref match);
        if (match is null) ClassifyContent(entry, names, ref match);
        foreach (var rule in testRules) match ??= rule(entry);
        return match is { } found && ForKind(found.Kind) is not null ? found : null;
    }

    /** Adds a category with its rule until disposed. Tests only; call it while no worker runs. */
    internal static IDisposable AddForTest(StudioCategory category, Func<PackageEntry, StudioCategoryMatch?> classify)
    {
        Publish([.. all, category]);
        testRules = [.. testRules, classify];
        return new Removal(() =>
        {
            Publish(all.Where(item => !ReferenceEquals(item, category)));
            testRules = [.. testRules.Where(rule => rule != classify)];
        });
    }

    private sealed class Removal(Action remove) : IDisposable
    {
        private Action? pending = remove;
        public void Dispose() { pending?.Invoke(); pending = null; }
    }

    public static AssetOpenContext? DefaultContext(string kind, uint tag) => ForKind(kind)?.OpenContext?.Invoke(tag);

    /** Sort key in a category list: rows its viewer shows first, then rows with no real name. Worker only. */
    internal static int RowRank(BrowserAsset row, StudioNameIndex names) =>
        (ListOnlyKinds.Contains(row.Kind) ? 2 : 0)
        + (names.Lookup(row.Entry) is { Confidence: StudioNameConfidence.Fallback } ? 1 : 0);

    /**
     * Tabs of a category document: its Sections in order, then every other page that opens it.
     * @param paneShown True when the model pane draws the preview, so it is not a tab.
     */
    public static IReadOnlyList<IStudioPage> Tabs(StudioCategory category, IEnumerable<IStudioPage> pages,
        StudioDocument document, bool paneShown) =>
        [.. pages.Where(page => page.CanOpen(document) && !(paneShown && page.Id == PreviewPageId))
            .OrderBy(page => Array.IndexOf(category.Sections, page.Id) is >= 0 and var at ? at : int.MaxValue)];

    static partial void AddItemCategories(List<StudioCategory> categories);
    static partial void AddGuardianCategories(List<StudioCategory> categories);
    static partial void AddEntityCategories(List<StudioCategory> categories);
    static partial void AddContentCategories(List<StudioCategory> categories);
    static partial void ClassifyItem(PackageEntry entry, StudioNameIndex names, ref StudioCategoryMatch? match);
    static partial void ClassifyGuardian(PackageEntry entry, StudioNameIndex names, ref StudioCategoryMatch? match);
    static partial void ClassifyEntity(PackageEntry entry, StudioNameIndex names, ref StudioCategoryMatch? match);
    static partial void ClassifyContent(PackageEntry entry, StudioNameIndex names, ref StudioCategoryMatch? match);
}
