using ImGuiNET;
using SunriseStudio.Formats;
using ItemKinds = SunriseStudio.Packages.InvestmentItemKinds;

namespace SunriseStudio.Editor;

/** Counts, problems and stack ranges of one table snapshot. */
internal sealed record BucketIndex(InvestmentTables Tables, string Counts, IReadOnlyList<(int Bucket, string Text)> Problems,
    IReadOnlyDictionary<byte, string> Stacks);

/**
 * A column the owner of a bucket list adds, with its header and one cell per bucket id. A non-empty FirstRow adds a row above
 * the buckets that stands for all of them (BucketList.AllBuckets). Others are buckets only the owner knows, listed last.
 */
internal sealed record BucketColumn(string Header, Action<int> Cell, string FirstRow, string FirstTip,
    IReadOnlyList<(byte Id, int Slots)> Others);

/** The installed buckets as a searchable table sorted by id, with their problems. The Buckets page and Live's Inventory draw it. */
internal sealed class BucketList
{
    /** The id the optional first row selects. */
    public const int AllBuckets = -1;
    /** A chosen bucket's row scrolls to the middle of the list. */
    private const float CentreRow = .5f;
    private const string Dot = InvestmentTableViews.Dot;
    private const string Unchecked = "Studio does not check two more refusals: a slot range that does not fit its inventory "
        + "array, and an equipment slot that two buckets share. Sunrise also fails the whole table when its equipment pairing fails.";
    private const string OthersTip = "Not in the installed bucket table";
    // The first row's Id and Slots, since it stands for every bucket.
    private const string Many = "-";
    private BucketIndex? index;
    private (BucketIndex? Index, string Search) shownKey;
    /** The buckets the list shows under All held items, in its order. */
    private InvestmentBucket[] shown = [];
    private bool scrollToSelected;
    private string search = "";

    /** The chosen bucket id. AllBuckets (-1) means the first row, or no bucket when the list has no first row. */
    public int Selected { get; private set; } = AllBuckets;

    /** Chooses a bucket and scrolls its row into view on the next draw. */
    public void Select(int bucket) { Selected = bucket; scrollToSelected = true; }

    /** The counts, problems and stack ranges of a table snapshot, built once per snapshot. */
    public BucketIndex Index(InvestmentCatalog catalog, InvestmentTables tables)
    {
        if (index is { } cached && ReferenceEquals(cached.Tables, tables)) return cached;
        var scopes = tables.Buckets.GroupBy(bucket => bucket.ArraySelector).OrderBy(group => group.Key)
            .Select(group => $"{group.Count():N0} {InvestmentTableViews.Scope(group.Key).ToLowerInvariant()}");
        string counts = string.Join(Dot, scopes.Prepend(InvestmentTableViews.Count(tables.Buckets.Count, "bucket")));
        var problems = new List<(int, string)>();
        problems.AddRange(tables.Diagnostics.Where(line => line.StartsWith("Buckets: ",
            StringComparison.Ordinal)).Select(line => (-1, line)));
        problems.AddRange(tables.BucketTableProblems.Select(line => (-1, line)));
        problems.AddRange(tables.Buckets.Where(bucket => bucket.Problem.Length != 0).Select(bucket => ((int)bucket.Id,
            $"Bucket {bucket.Id}: {bucket.Problem}")));
        var stacks = new Dictionary<byte, string>();
        foreach (var bucket in tables.Buckets)
        {
            var items = tables.ItemsInBucket(bucket.Id);
            if (items.Count == 0) continue;
            int low = items.Min(item => catalog.Items[item].MaximumStack),
                high = items.Max(item => catalog.Items[item].MaximumStack);
            stacks[bucket.Id] = low == high ? $"{low:N0}" : $"{low:N0}-{high:N0}";
        }
        return index = new(tables, counts, problems, stacks);
    }

    /** Draws the Find bucket box, which filters by label or id. */
    public void DrawSearch()
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##find-bucket", "Find bucket", ref search, 256);
    }

    /** Draws table-level refusals first, then one line per row problem. @return True when a row problem's line was clicked. */
    public bool DrawProblems(BucketIndex buckets)
    {
        int chosen = InvestmentTableViews.Problems("bucket-problems", buckets.Problems, Selected, Unchecked);
        if (chosen >= 0) Select(chosen);
        return chosen >= 0;
    }

    /** Draws the buckets sorted by id and filtered by the search, with the owner's column. @return True when a row was chosen. */
    public bool Draw(BucketIndex buckets, BucketColumn? extra)
    {
        var tables = buckets.Tables;
        if (tables.Buckets.Count == 0) { ImGui.TextDisabled("Buckets not read."); return false; }
        if (!ReferenceEquals(shownKey.Index, buckets) || shownKey.Search != search)
        {
            shownKey = (buckets, search);
            shown = tables.Buckets.Where(bucket => Matches(bucket.Id)).OrderBy(bucket => bucket.Id).ThenBy(bucket => bucket.Index).ToArray();
        }
        var others = extra?.Others.Where(other => Matches(other.Id)).ToArray() ?? [];
        if (shown.Length == 0 && others.Length == 0 && extra?.FirstRow is not { Length: > 0 }) { ImGui.TextDisabled("No bucket matches"); return false; }
        if (!ImGui.BeginTable("buckets", extra is null ? 3 : 4,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp))
            return false;
        ImGui.TableSetupColumn("Bucket", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Id", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Slots", ImGuiTableColumnFlags.WidthFixed);
        if (extra is not null) ImGui.TableSetupColumn(extra.Header, ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
        bool chosen = false;
        if (extra is { FirstRow.Length: > 0 })
            chosen |= Row(AllBuckets, extra.FirstRow, extra.FirstTip, Many, Many, extra);
        foreach (var bucket in shown)
        {
            ImGui.PushID(bucket.Index);
            chosen |= Row(bucket.Id, ItemKinds.BucketLabel(bucket.Id), $"Bucket {bucket.Id}\nDescriptor {bucket.Index}\nSlots "
                + $"{bucket.FirstSlot}-{bucket.LastSlot} of the {InvestmentTableViews.Scope(bucket.ArraySelector).ToLowerInvariant()} array",
                $"{bucket.Id}", $"{bucket.SlotCount:N0}", extra);
            ImGui.PopID();
        }
        foreach (var (id, slots) in others)
        {
            ImGui.PushID($"other{id}");
            chosen |= Row(id, ItemKinds.BucketLabel(id), $"Bucket {id}\n{OthersTip}", $"{id}", $"{slots:N0}", extra);
            ImGui.PopID();
        }
        ImGui.EndTable();
        scrollToSelected = false;
        return chosen;
    }

    private bool Matches(byte id) => search.Length == 0
        || $"{ItemKinds.BucketLabel(id)}\n{id}".Contains(search, StringComparison.OrdinalIgnoreCase);

    /** One selectable row spanning every column. @return True when it was clicked. */
    private bool Row(int id, string label, string tip, string idText, string slots, BucketColumn? extra)
    {
        ImGui.TableNextRow(); ImGui.TableNextColumn();
        bool clicked = ImGui.Selectable(label, id == Selected, ImGuiSelectableFlags.SpanAllColumns);
        if (clicked) Select(id);
        if (scrollToSelected && id == Selected) { ImGui.SetScrollHereY(CentreRow); scrollToSelected = false; }
        StudioUi.Tip(tip);
        ImGui.TableNextColumn(); ImGui.TextUnformatted(idText);
        ImGui.TableNextColumn(); ImGui.TextUnformatted(slots);
        if (extra is not null) { ImGui.TableNextColumn(); extra.Cell(id); }
        return clicked;
    }
}
