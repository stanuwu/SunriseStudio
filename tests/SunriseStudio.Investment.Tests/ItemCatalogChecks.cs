using SunriseStudio.Editor;
using SunriseStudio.Formats;

/**
 * The item catalog view's rows over a synthetic catalog, without drawing: search by name, index, hash and tag, every order,
 * the candidates a picker passes, Changed only, a kind, and Reset. The workspace has no installation, so it lists no category
 * rows and no project changes.
 */
internal static class ItemCatalogChecks
{
    public static void Run(InvestmentCatalog catalog)
    {
        using var workspace = new StudioWorkspace();
        var view = new ItemCatalogView();
        InvestmentItem[] Listed(ItemCatalogOptions options = default) => view.Rows(workspace, catalog, options);
        Check(view.Kind == ItemCatalogView.AllItems && Listed().SequenceEqual(catalog.Items)
            && view.Count(workspace, catalog, default) == catalog.Items.Count,
            "All items did not list every item in installed order.");

        // Each word a row's fields contain finds it: its name, index, hash and tag, alone or together.
        var item = catalog.Items[^1];
        foreach (string search in new[] { item.Name, item.Index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"{item.Hash:X8}", $"{item.Tag:x8}", $"{item.Hash:X8} {item.Tag:X8}" })
        {
            view.Search = search;
            Check(Listed().Contains(item)
                && Listed().All(row => Fields(row).Contains(search.Split(' ')[0], StringComparison.OrdinalIgnoreCase)),
                $"Searching \"{search}\" did not list {item.Name}, or listed a row without the word.");
        }
        view.Search = $"{item.Hash:X8} no-such-word";
        Check(Listed().Length == 0, "A row was listed without every search word.");
        view.Search = "";

        // Four items whose name, tier and type each give another order than their index, so every order is its own sequence.
        static InvestmentItem Mixed(int index, string name, string type, byte tier) =>
            new(index, (uint)index, 0, 0, name, type, "", 0, tier, 1, false);
        InvestmentItem[] mixed = [Mixed(0, "Cedar", "Rifle", 5), Mixed(1, "birch", "Bow", 2), Mixed(2, "Aspen", "rifle", 2),
            Mixed(3, "Dogwood", "Bow", 4)];
        (ItemSort Sort, int[] Expected)[] orders =
        [
            (ItemSort.Catalog, [0, 1, 2, 3]), (ItemSort.Name, [2, 1, 0, 3]), (ItemSort.Rarity, [0, 3, 2, 1]),
            (ItemSort.Type, [1, 3, 2, 0])
        ];
        foreach (var (sort, expected) in orders)
        {
            int[] listed = [.. ItemCatalogView.Ordered(mixed, sort).Select(row => row.Index)];
            Check(listed.SequenceEqual(expected), $"Sorting by {sort} listed items {string.Join(", ", listed)}.");
            // The view lists every row of the catalog in each order.
            view.Sort = sort;
            Check(Listed().Length == catalog.Items.Count, $"Sorting by {sort} dropped or repeated a row.");
        }
        view.Sort = ItemSort.Catalog;

        // A picker's candidates narrow the rows to exactly those items; the project's changes and a kind narrow them too.
        int[] candidates = [0, catalog.Items.Count - 1];
        Check(Listed(new(Candidates: candidates)).Select(row => row.Index).SequenceEqual(candidates),
            "Candidates did not narrow the rows to them.");
        Check(Listed(new(ChangedOnly: true)).Length == 0, "Changed only listed an item the project does not change.");
        view.Kind = SunriseStudio.Packages.InvestmentItemKinds.Weapons;
        Check(Listed().Length == 0, "A kind listed items outside its category's rows.");

        // Reset clears the search and returns to every item; a catalog without a kind menu keeps the kind its owner set.
        view.Search = item.Name;
        view.Reset();
        Check(view.Search.Length == 0 && view.Kind == ItemCatalogView.AllItems && Listed().Length == catalog.Items.Count,
            "Reset kept the search or the kind.");
        var fixedKind = new ItemCatalogView(kindMenu: false) { Kind = SunriseStudio.Packages.InvestmentItemKinds.Weapons,
            Search = item.Name };
        fixedKind.Reset(ItemCatalogView.AllItems);
        Check(fixedKind.Search.Length == 0 && fixedKind.Kind == SunriseStudio.Packages.InvestmentItemKinds.Weapons,
            "Reset changed the kind of a catalog without a kind menu.");
        Console.WriteLine($"item_catalog items={catalog.Items.Count} search=name,index,hash,tag orders={orders.Length} candidates=exact reset=pass");
    }

    /** The fields a search word may match, as the view searches them. */
    private static string Fields(InvestmentItem item) =>
        $"{item.Name}\n{item.TypeName}\n{item.Rarity}\n{item.Index}\n{item.Hash:X8}\n{item.Tag:X8}";

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
