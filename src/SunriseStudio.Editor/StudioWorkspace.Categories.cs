using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Editor;

/** Asset classes that creation pickers list; each has one cached row array. */
public enum PickClass { Material, Texture, StaticMesh, InteractionConfig, CuiBitmap }

public sealed partial class StudioWorkspace
{
    /** Row index of every row whose kind can change; positions in Assets. */
    private Dictionary<uint, int> componentRows = [];
    private Dictionary<string, int> kindCounts = new(StringComparer.Ordinal);
    // Each map is replaced whole, never changed in place, so a worker may hold the old one.
    private Dictionary<uint, string> categoryOf = [];
    /** Item tag of each category item's display record. */
    private Dictionary<uint, uint> displayOwner = [];
    private Dictionary<PickClass, BrowserAsset[]> classRows = [];
    /** Installed rows lead each class array; project rows follow them. */
    private Dictionary<PickClass, int> installedClassCounts = [];

    /** Rows of each category with a viewer, keyed by category name. Built with Assets. */
    public IReadOnlyDictionary<string, BrowserAsset[]> CategoryRows { get; private set; } =
        new Dictionary<string, BrowserAsset[]>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, int> CategoryCounts { get; private set; } = new Dictionary<string, int>(StringComparer.Ordinal);

    public IReadOnlyList<BrowserAsset> CategoryRowsOf(string name) => CategoryRows.GetValueOrDefault(name) ?? [];

    public IReadOnlyList<BrowserAsset> ClassRows(PickClass kind) => classRows.GetValueOrDefault(kind) ?? [];

    /** The category of a row, or of an item's display record; null for a normal asset. */
    public string? CategoryNameOf(uint tag) => categoryOf.GetValueOrDefault(tag);

    /** The item a display record belongs to; else the tag itself. VendorWorkflow maps a vendor's display record. */
    public uint CloneBaseOf(uint tag) => displayOwner.GetValueOrDefault(tag, tag);

    /** True for an entry the open project adds; the set follows every overlay refresh. */
    public bool IsProjectTag(uint tag) => nativeAdditions.ContainsKey(tag);

    internal static PickClass? ClassOf(PackageEntry entry) => entry switch
    {
        { Reference: STechnique.Class, Type: 8 or 16 } => PickClass.Material,
        { Type: 32, SubType: >= 1 and <= 3 } => PickClass.Texture,
        { Reference: SStaticMesh.Class, Type: 8 or 16 } => PickClass.StaticMesh,
        { Reference: SEntityConfig.Class } => PickClass.InteractionConfig,
        { Reference: SIconBitmap.Class } => PickClass.CuiBitmap,
        _ => null
    };

    /** Adds or removes a row's category, and its item's display record, in the category and owner maps. */
    private static void IndexCategory(Dictionary<uint, string> map, Dictionary<uint, uint> owners, BrowserAsset row, bool add)
    {
        if (!row.InCategory) return;
        uint item = row.Entry.TagHash;
        uint display = row.Names.TryGetItem(item, out var facts) ? facts.DisplayTag : 0;
        foreach (uint tag in display == 0 ? [item] : new[] { item, display })
            if (add) map[tag] = row.Category;
            else if (map.GetValueOrDefault(tag) == row.Category) map.Remove(tag);
        if (display == 0) return;
        if (add) owners[display] = item;
        else if (owners.GetValueOrDefault(display) == item) owners.Remove(display);
    }

    /** Puts replaced rows into the class arrays that hold the old ones. */
    private void SwapClassRows(IReadOnlyList<(BrowserAsset? Old, BrowserAsset? New)> changes)
    {
        var swaps = new Dictionary<BrowserAsset, BrowserAsset>(ReferenceEqualityComparer.Instance);
        var kinds = new HashSet<PickClass>();
        foreach (var (old, row) in changes)
            if (old is not null && row is not null && ClassOf(old.Entry) is { } kind) { swaps[old] = row; kinds.Add(kind); }
        if (swaps.Count == 0) return;
        var updated = new Dictionary<PickClass, BrowserAsset[]>(classRows);
        foreach (var kind in kinds)
            updated[kind] = [.. (updated.GetValueOrDefault(kind) ?? []).Select(row => swaps.TryGetValue(row, out var next) ? next : row)];
        classRows = updated;
    }

    /** Dependency facts refresh before documents or browser filters reopen. */
    private void RefreshComponentRows(IEnumerable<uint> changes)
    {
        if (Names is not { } names || Catalog is not { } catalog) return;
        long oldRevision = names.Revision;
        var affected = names.RefreshComponents(changes);
        if (oldRevision == names.Revision) return;
        BrowserAsset[]? updated = null;
        var replaced = new List<(BrowserAsset? Old, BrowserAsset? New)>();
        foreach (uint tag in affected)
        {
            if (!componentRows.TryGetValue(tag, out int index) || !catalog.TryGetEntry(tag, out var entry)) continue;
            var old = Assets[index];
            var row = CreateRow(entry, names);
            // Names are read live, so a row whose kind, tags and tier hold needs no copy.
            if (row.Kind == old.Kind && row.Tags == old.Tags && row.Tier == old.Tier) continue;
            updated ??= Assets.ToArray();
            updated[index] = row;
            kindCounts[old.Kind]--;
            kindCounts[row.Kind] = kindCounts.GetValueOrDefault(row.Kind) + 1;
            replaced.Add((old, row));
        }
        if (updated is not null)
        {
            Assets = updated;
            UpdateCategoryRows(replaced);
            SwapClassRows(replaced);
            PublishBrowserKinds();
            AssetRevision++;
        }
        publishedNameRevision = names.Revision;
        NameRevision++;
    }

    private static BrowserAsset CreateRow(PackageEntry entry, StudioNameIndex names)
    {
        var (kind, tags, tier) = BrowserKind(entry, names);
        return new(entry, kind, names) { Tags = tags, Tier = tier };
    }

    /** A category match wins; otherwise structured entity and config rows take their kind from the name index. */
    private static (string Kind, string Tags, int Tier) BrowserKind(PackageEntry entry, StudioNameIndex names)
    {
        if (StudioCategories.Classify(entry, names) is { } match) return (match.Kind, match.Tags, match.Tier);
        if (!AssetCategories.IsStructured(entry) || entry.Reference is not (SEntity.Class or SEntityConfig.Class))
            return (AssetKind(entry), "", -1);
        var (kind, tags) = AssetCategories.Classify(entry, names.GameNames);
        return (kind, StudioCategories.SearchTags(tags), -1);
    }

    /** Rows whose kind can change when component or item facts refresh. */
    private static bool IsComponentRow(PackageEntry entry) =>
        entry.Reference is SEntity.Class or SEntityConfig.Class or SWwiseEvent.Class or InvestmentTags.ItemClass;

    private void PublishBrowserKinds() => BrowserKinds =
        [.. kindCounts.Where(pair => pair.Value > 0).Select(pair => pair.Key).Order(StringComparer.Ordinal)];

    /**
     * Applies row swaps to the category lists they touch; a null Old adds a row, a null New removes one.
     * Untouched lists are kept, so the full row array is never copied here.
     */
    private void UpdateCategoryRows(IReadOnlyList<(BrowserAsset? Old, BrowserAsset? New)> changes)
    {
        var touched = changes.SelectMany(change => new[] { change.Old, change.New })
            .Where(row => row is { InCategory: true }).Select(row => row!.Category).ToHashSet(StringComparer.Ordinal);
        if (touched.Count == 0) return;
        var swaps = new Dictionary<BrowserAsset, BrowserAsset?>(ReferenceEqualityComparer.Instance);
        foreach (var (old, row) in changes) if (old is not null) swaps[old] = row;
        var lists = new Dictionary<string, BrowserAsset[]>(CategoryRows, StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(CategoryCounts, StringComparer.Ordinal);
        foreach (string name in touched)
        {
            bool Holds(BrowserAsset? row) => row is { InCategory: true } && row.Category == name;
            var list = new List<BrowserAsset>();
            foreach (var row in lists.GetValueOrDefault(name) ?? [])
                if (!swaps.TryGetValue(row, out var next)) list.Add(row);
                else if (Holds(next)) list.Add(next!);
            foreach (var (old, row) in changes)
                if (Holds(row) && !Holds(old)) list.Add(row!);
            lists[name] = [.. list];
            counts[name] = list.Count;
        }
        CategoryRows = lists;
        CategoryCounts = counts;
        var map = new Dictionary<uint, string>(categoryOf);
        var owners = new Dictionary<uint, uint>(displayOwner);
        foreach (var (old, _) in changes) if (old is not null) IndexCategory(map, owners, old, add: false);
        foreach (var (_, row) in changes) if (row is not null) IndexCategory(map, owners, row, add: true);
        categoryOf = map;
        displayOwner = owners;
    }
}
