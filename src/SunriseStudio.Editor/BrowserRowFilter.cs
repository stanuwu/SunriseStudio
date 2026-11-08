using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Graph;
using SunriseStudio.Tags.Query;

namespace SunriseStudio.Editor;

/** One row filter for the browser and the creation pickers. */
public static class BrowserRowFilter
{
    /** Arrays this short with no search words filter on the calling thread; the rest run on a worker. */
    public const int InlineRows = 50_000;

    public static string[] Words(string search) =>
        search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /**
     * Rows that pass keep and contain every word; rows keep their order.
     * @param keep Runs on a worker for large arrays, so it must not read packages.
     */
    public static BrowserAsset[] Filter(IReadOnlyList<BrowserAsset> rows, string[] words, Func<BrowserAsset, bool>? keep,
        CancellationToken token)
    {
        var result = new List<BrowserAsset>();
        for (int index = 0; index < rows.Count; index++)
        {
            if ((index & 511) == 0) token.ThrowIfCancellationRequested();
            var row = rows[index];
            if (keep is not null && !keep(row)) continue;
            if (words.Length != 0)
            {
                string text = row.SearchText;
                if (!words.All(word => text.Contains(word, StringComparison.OrdinalIgnoreCase))) continue;
            }
            result.Add(row);
        }
        return [.. result];
    }

    /** True when the search is a path query, such as @<s_scenario>{dev_name_hash==0x1234}, rather than words. */
    public static bool IsQuery(string search) => search.StartsWith('@') || search.StartsWith("##0x", StringComparison.Ordinal);

    /**
     * Runs a path query on a worker and keeps the rows whose tag holds a match; rows keep their order.
     * A query that does not parse or names an unknown field ends the task with PathQueryException.
     * @param keep Runs on the worker, so it must not read packages.
     */
    public static Task<BrowserAsset[]> StartQuery(IReadOnlyList<BrowserAsset> rows, string query, PackageCatalog catalog,
        Func<BrowserAsset, bool>? keep, CancellationToken token) => Task.Run(() =>
    {
        var parsed = PathQuery.Parse(query);
        var tags = parsed.Evaluate(new QueryContext(new CatalogTagSource(catalog), StudioRegistry.Index)).Select(node => node.Tag).ToHashSet();
        return Filter(rows, [], row => tags.Contains(row.Entry.TagHash) && (keep is null || keep(row)), token);
    }, token);

    /** Filters inline when cheap and returns the rows; otherwise returns the started worker. */
    public static (BrowserAsset[]? Rows, Task<BrowserAsset[]>? Work) Start(IReadOnlyList<BrowserAsset> rows, string[] words,
        Func<BrowserAsset, bool>? keep, CancellationToken token) =>
        words.Length == 0 && rows.Count < InlineRows
            ? (Filter(rows, words, keep, token), null)
            : (null, Task.Run(() => Filter(rows, words, keep, token), token));
}
