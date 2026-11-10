using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Viewport;
using ItemKinds = SunriseStudio.Packages.InvestmentItemKinds;

namespace SunriseStudio.Editor;

/** How the item catalog orders its rows. Catalog keeps the installed order. */
internal enum ItemSort { Catalog, Name, Rarity, Type }

/**
 * What one catalog draw lists. Candidates limits the rows to these item indices, such as the plugs a socket allows, and
 * ChangedOnly limits them to items the project changes. Held marks each row with what the game's character holds. Menu draws
 * a row's context menu.
 */
internal readonly record struct ItemCatalogOptions(IReadOnlyCollection<int>? Candidates = null, LiveHeldIndex? Held = null,
    Action<InvestmentItem>? Menu = null, bool ChangedOnly = false);

/**
 * The installed items as one searchable list or grid, under a bar with the search box, the kind and sort menus and the count.
 * The asset browser, Live's Items and the item pickers all use it, so items are browsed the same way everywhere. Without
 * kindMenu the owner sets Kind, as a browser category does.
 */
internal sealed class ItemCatalogView(bool kindMenu = true)
{
    public const string AllItems = "All items";
    /** The kind and sort menus' width in font heights, enough for "Mods and plugs". */
    internal const float MenuFonts = 10;
    // A picker's size in font heights. The width keeps the bar on one line.
    private const float PickerWidthFonts = 44, PickerHeightFonts = 24;
    private static readonly string[] SortNames = ["Installed order", "Name", "Rarity", "Type"];
    private readonly IconGrid grid = new();
    private string search = "";
    private ItemSort sort;
    private int chosen = -1;
    // What the next draw does to the list's scroll: back to the top for new rows, then to the chosen item when the rows list it.
    private bool scrollToTop, scrollToChosen;
    // Whether the list last drew as a grid. Switching between rows and grid scrolls to the chosen item.
    private bool? drawnAsGrid;
    // The item of the grid cell whose context menu is open.
    private InvestmentItem? contextItem;
    // The kind, search, order and narrowing the rows were built for. A change scrolls the list to the chosen item, or to the
    // top when the new rows do not list it.
    private (string Kind, string Search, ItemSort Sort, IReadOnlyCollection<int>? Candidates, bool ChangedOnly) filter;
    // The data the rows were built from. A new catalog, category rows or, under ChangedOnly, a project edit rebuilds the rows
    // without moving the list.
    private (InvestmentCatalog? Catalog, BrowserAsset[]? KindRows, int ProjectRevision) source;
    private InvestmentItem[] rows = [];

    /** A category name from the kind menu, or AllItems. */
    public string Kind { get; set; } = AllItems;

    /** The search box's text: words that a listed item's name, type, rarity, index, hash or tag must all contain. */
    internal string Search { get => search; set => search = value; }

    /** The order the sort menu chose. */
    internal ItemSort Sort { get => sort; set => sort = value; }

    /** The chosen item's index, or -1 for none. Setting a different item scrolls the list to it on the next draw. */
    public int Chosen
    {
        get => chosen;
        set { if (value != chosen) { chosen = value; scrollToChosen = true; } }
    }

    /** How many items the current kind, search and options list. */
    public int Count(StudioWorkspace workspace, InvestmentCatalog catalog, ItemCatalogOptions options) =>
        Rows(workspace, catalog, options).Length;

    /**
     * Clears the search, shows kind when the view has a kind menu, and scrolls to the chosen item. An opening picker or a link
     * that chooses an item then starts where that item is listed.
     */
    public void Reset(string kind = AllItems)
    {
        search = "";
        if (kindMenu) Kind = kind;
        scrollToChosen = true;
    }

    /**
     * Draws the search box, the kind and sort menus and the count on one line. Trailing is the width the caller keeps for its
     * own controls after them.
     */
    public void DrawBar(StudioWorkspace workspace, InvestmentCatalog catalog, ItemCatalogOptions options, float trailing)
    {
        Rows(workspace, catalog, options);
        var style = ImGui.GetStyle();
        float menu = MenuFonts * ImGui.GetFontSize();
        int menus = kindMenu ? 2 : 1;
        string count = InvestmentTableViews.Count(rows.Length, "item");
        float counted = ImGui.CalcTextSize(count).X;
        ImGui.SetNextItemWidth(Math.Max(menu,
            ImGui.GetContentRegionAvail().X - menus * menu - counted - trailing - (menus + 1) * style.ItemSpacing.X));
        ImGui.InputTextWithHint("##item-search", "Search items", ref search, 256);
        ImGui.SameLine();
        if (kindMenu)
        {
            ImGui.SetNextItemWidth(menu);
            if (ImGui.BeginCombo("##item-kind", KindLabel(Kind)))
            {
                foreach (string name in ItemKinds.Categories.Prepend(AllItems))
                    if (ImGui.Selectable(KindLabel(name), name == Kind)) Kind = name;
                ImGui.EndCombo();
            }
            StudioUi.Tip("Kind");
            ImGui.SameLine();
        }
        ImGui.SetNextItemWidth(menu);
        if (ImGui.BeginCombo("##item-sort", SortNames[(int)sort]))
        {
            for (int i = 0; i < SortNames.Length; i++)
                if (ImGui.Selectable(SortNames[i], (int)sort == i)) sort = (ItemSort)i;
            ImGui.EndCombo();
        }
        StudioUi.Tip("Order");
        ImGui.SameLine();
        ImGui.TextDisabled(count);
    }

    /**
     * The rows or the grid of the listed items in a child of the given size (zero fills the rest), with their icons, rarity
     * colours, modified dots and the held badges.
     * @return The item clicked this frame, else null.
     */
    public InvestmentItem? DrawList(string id, StudioWorkspace workspace, InvestmentCatalog catalog,
        InvestmentIconCache icons, float scale,
        bool asGrid, Vector2 size, ItemCatalogOptions options)
    {
        var shown = Rows(workspace, catalog, options);
        if (drawnAsGrid != asGrid) { drawnAsGrid = asGrid; scrollToChosen = true; }
        ImGui.BeginChild(id, size, ImGuiChildFlags.None);
        InvestmentItem? clicked = shown.Length == 0 ? null
            : asGrid ? DrawGrid(workspace, catalog, icons, scale, shown, options) : DrawRows(workspace, catalog,
                icons, scale, shown, options);
        if (shown.Length == 0) ImGui.TextDisabled(search.Length != 0 ? "No matching items." : "No items.");
        ImGui.EndChild();
        // A click chooses a row already in view, so the list stays where it is.
        if (clicked is not null) chosen = clicked.Index;
        return clicked;
    }

    /** The bar over the rows in a bordered child of a picker's size, for a modal that chooses one item. @return The item clicked. */
    public InvestmentItem? DrawPicker(string id, StudioWorkspace workspace, InvestmentCatalog catalog,
        InvestmentIconCache icons, ItemCatalogOptions options)
    {
        float font = ImGui.GetFontSize();
        ImGui.BeginChild(id, new(PickerWidthFonts * font, PickerHeightFonts * font), ImGuiChildFlags.Borders);
        DrawBar(workspace, catalog, options, 0);
        var clicked = DrawList(id + "-rows", workspace, catalog, icons, InvestmentTableViews.PageScale,
            false, Vector2.Zero, options);
        ImGui.EndChild();
        return clicked;
    }

    private unsafe InvestmentItem? DrawRows(StudioWorkspace workspace, InvestmentCatalog catalog,
        InvestmentIconCache icons, float scale,
        InvestmentItem[] shown, ItemCatalogOptions options)
    {
        InvestmentItem? clicked = null;
        float height = IconList.RowHeight(scale);
        if (scrollToTop) ImGui.SetScrollY(0);
        if (ChosenRow(shown) is >= 0 and var row) ImGui.SetScrollY(row * height);
        scrollToTop = false;
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(shown.Length, height);
        while (clipper.Step())
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var item = shown[i];
                ImGui.PushID(item.Index);
                if (IconList.Row(icons, scale, item.Index == chosen, catalog, item, true, item.Name, Colour(item), item.TypeName,
                    options.Held?.Badge(item.Hash) ?? "", workspace.IsModified(item.Tag)))
                    clicked = item;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(Tip(item, options.Held));
                if (options.Menu is { } menu && ImGui.BeginPopupContextItem("item-context"))
                {
                    menu(item);
                    ImGui.EndPopup();
                }
                ImGui.PopID();
            }
        clipper.End(); clipper.Destroy();
        return clicked;
    }

    /** The grid form. A cell shows the icon and name, "x3" when more than one is held, and a corner mark when one is equipped. */
    private InvestmentItem? DrawGrid(StudioWorkspace workspace, InvestmentCatalog catalog, InvestmentIconCache icons, float scale,
        InvestmentItem[] shown, ItemCatalogOptions options)
    {
        if (scrollToTop) grid.ResetScroll();
        if (ChosenRow(shown) is >= 0 and var cell) grid.ScrollTo(cell);
        scrollToTop = false;
        uint badge = ImGui.GetColorU32(StudioTheme.Accent);
        IconCell Cell(int index)
        {
            var item = shown[index];
            var held = options.Held?.Of(item.Hash) ?? [];
            long quantity = held.Sum(row => (long)row.Quantity);
            return new(item.Name, Colour(item), "", icons.Get(catalog, item.Index), true, StudioIcon.Cube,
                quantity > 1 ? $"x{quantity:N0}" : "", badge, IconGrid.NoProgress, false, held.Any(row => row.Equipped), false,
                item.Index == chosen, workspace.IsModified(item.Tag));
        }
        int clicked = grid.Draw("item-grid", shown.Length, Cell, scale, 0, out int hovered, out int context);
        if (hovered >= 0) ImGui.SetTooltip(Tip(shown[hovered], options.Held));
        if (context >= 0 && options.Menu is not null) { contextItem = shown[context]; ImGui.OpenPopup("item-grid-context"); }
        if (options.Menu is { } menu && contextItem is { } open && ImGui.BeginPopup("item-grid-context"))
        {
            menu(open);
            ImGui.EndPopup();
        }
        return clicked >= 0 ? shown[clicked] : null;
    }

    /**
     * Takes the pending scroll to the chosen item. A chosen item the rows do not list leaves the list where it is.
     * @return The chosen item's place in the shown rows when the list should scroll to it, else -1.
     */
    private int ChosenRow(InvestmentItem[] shown)
    {
        if (!scrollToChosen) return -1;
        scrollToChosen = false;
        return Array.FindIndex(shown, item => item.Index == chosen);
    }

    /**
     * The kind's items, or every item for AllItems, narrowed to the candidates, to the project's changes when asked and to every
     * search word, in the chosen order. The rows are rebuilt only when the filter or their source changes.
     */
    internal InvestmentItem[] Rows(StudioWorkspace workspace, InvestmentCatalog catalog, ItemCatalogOptions options)
    {
        var kindRows = Kind == AllItems ? null : workspace.CategoryRows.GetValueOrDefault(Kind) ?? [];
        var narrowed = (Kind, search, sort, options.Candidates, options.ChangedOnly);
        var built = (catalog, kindRows, options.ChangedOnly ? workspace.ProjectRevision : 0);
        if (filter == narrowed && source == built) return rows;
        if (filter != narrowed) scrollToTop = scrollToChosen = true;
        filter = narrowed; source = built;
        IEnumerable<InvestmentItem> items = kindRows is null ? catalog.Items
            : kindRows.Select(row => catalog.FindItem(row.Entry.TagHash)).OfType<InvestmentItem>();
        if (options.Candidates is { } only) items = items.Where(item => only.Contains(item.Index));
        if (options.ChangedOnly) items = items.Where(item => workspace.IsModified(item.Tag));
        string[] words = BrowserRowFilter.Words(search);
        if (words.Length != 0)
            items = items.Where(item => SearchText(item) is var text
                && words.All(word => text.Contains(word, StringComparison.OrdinalIgnoreCase)));
        rows = Ordered(items, sort).ToArray();
        return rows;
    }

    /** The items in one of the sort menu's orders: by name, rarest tier first, or by type line. Names order equal ones. */
    internal static IEnumerable<InvestmentItem> Ordered(IEnumerable<InvestmentItem> items, ItemSort sort) => sort switch
    {
        ItemSort.Name => items.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
        ItemSort.Rarity => items.OrderByDescending(item => item.Tier).ThenBy(item => item.Name,
            StringComparer.CurrentCultureIgnoreCase),
        ItemSort.Type => items.OrderBy(item => item.TypeName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
        _ => items
    };

    /** Name, type line, rarity, item index, definition hash and package tag, which a search word may match. */
    private static string SearchText(InvestmentItem item) =>
        $"{item.Name}\n{item.TypeName}\n{item.Rarity}\n{item.Index}\n{item.Hash:X8}\n{item.Tag:X8}";

    private static string KindLabel(string name) => StudioCategories.Find(name)?.Label ?? name;

    private static uint Colour(InvestmentItem item) => ImGui.GetColorU32(StudioTheme.Rarity(item.Tier));

    private static string Tip(InvestmentItem item, LiveHeldIndex? held)
    {
        var rows = held?.Of(item.Hash) ?? [];
        return $"{item.Name}\n{item.TypeName}\n{item.Hash:X8}"
            + (rows.Length == 0 ? "" : $"\nHeld {rows.Sum(row => (long)row.Quantity):N0}" + (rows.Any(row => row.Equipped) ? ", equipped" : ""));
    }
}
