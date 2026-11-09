/**
 * File: MCP tools compare_with_base, revert_to_base (T1.5) and get_item_sources (T1.4).
 */
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/**
 * Base-versus-project tag comparison and the item "where is this used" report. Reads go through ISolforgeHost.QueryAsync
 * like the catalog tools (they work while context publishing is paused); revert_to_base waits for the approve click.
 */
public static partial class SolforgeTools
{
    /** Read tools of this file, in listing order. */
    public static readonly string[] ItemSourceReadTools = ["compare_with_base", "get_item_sources"];
    /** Action tools of this file. */
    public static readonly string[] ItemSourceActionTools = ["repair_item_perk_displays", "revert_to_base"];
    /** Every tool of this file, in listing order. */
    public static readonly string[] ItemSourceToolNames = ["compare_with_base", "get_item_sources", "check_item_perk_displays", "repair_item_perk_displays", "revert_to_base"];

    /** Source kinds get_item_sources reports (its kind filter). */
    public static readonly string[] ItemSourceKinds =
        ["loot_pool", "loot_table", "vendor", "vendor_cost", "collection", "plug", "record", "quest", "activity", "reference", "project_edit"];

    /** Yields compare_with_base, get_item_sources and revert_to_base. */
    private static IEnumerable<McpTool> ItemSourceTools(ISolforgeHost host,
        Func<string, string, JsonObject, Func<JsonObject, CancellationToken, Task<McpToolResult>>, McpTool> read,
        Func<string, string, JsonObject, Func<JsonObject, string?>, Func<JsonObject, string>, McpTool> act)
    {
        McpTool Query(string name, string description, JsonObject schema) => read(name, description, schema, async (args, cancel) =>
        {
            var result = await host.QueryAsync(name, args, cancel).ConfigureAwait(false);
            return result["error"] is JsonValue error ? McpToolResult.Error(error.ToString()) : McpToolResult.Json(result);
        });
        static string? NeedTag(JsonObject args) => ParseHash(McpJson.String(args, "hash")) is null ? "Give a tag hash such as 0x80A1B2C3." : null;

        yield return Query("compare_with_base", "How the project's version of a tag differs from the base install (the Changes " +
            "panel's 'Compare with base...'). Changed fields are named, with base and project values, where a decoder (item, " +
            "vendor, material, CUI...) or a tag schema knows the layout; every other changed byte is listed as a byte range " +
            "(offset, length, base and project hex). field narrows the reply to one field name, an offset (0x40) or a range " +
            "(0x40+8). revert_to_base undoes it.",
            McpJson.Schema(("hash", "string", "Tag hash (0x...), e.g. from list_project_changes", true),
                ("field", "string", "Optional field name, offset (0x40) or byte range (0x40+8)", false)));
        yield return Query("get_item_sources", "Where an item comes from and what uses it: loot pools in the server's " +
            "loot_pools.lua and the project's loot_pool content (with the enemy_kill rules that roll them), loot tables, vendors " +
            "that sell it or charge it as a cost, Collections pages, other items' sockets and plug sets that offer it, and tags " +
            "that reference it (activities, records, quests, the project's own edits). Rows marked edited come from project " +
            "changes. The first call may say the item catalog is loading; call again. Scans are bounded, so truncated says a " +
            "list may be incomplete.",
            Paged(("item", "string", "Item tag (0x...), definition hash or exact name", true),
                ("kind", "string", "Optional kind: " + string.Join(", ", ItemSourceKinds), false),
                ("deep", "boolean", "Also walk activity and reference graphs (slower; default true)", false)));
        yield return Query("check_item_perk_displays", "For every project item (edited or added): its perk count against its display tag's " +
            "requirement row count, and any display tag claimed by more than one item. The game client walks both arrays with one index, " +
            "so a mismatch crashes it. repair_item_perk_displays fixes mismatches. The first call may say the item catalog is loading.",
            McpJson.Schema());
        yield return act("repair_item_perk_displays", "Resize the display requirement rows of every mismatched project item to its perk " +
            "count, keeping authored rows (extra rows at the end are dropped, missing ones are empty), as one undoable project change. " +
            "dry_run (default true) only lists what would change. Open any asset first before applying.",
            McpJson.Schema(("dry_run", "boolean", "List the fixes without applying them (default true)", false)),
            _ => null,
            args => McpJson.Bool(args, "dry_run", true) ? "Check which project items need their perk display rows resized (dry run)"
                : "Resize perk display rows of every mismatched project item");
        yield return act("revert_to_base", "Revert a tag to the base install: the whole tag (drops the project's change, as " +
            "the Changes panel's Revert does) or, with field, only that field or byte range (other edits stay). Undo works. " +
            "Use compare_with_base first to see what changes.",
            McpJson.Schema(("hash", "string", "Tag hash (0x...)", true),
                ("field", "string", "Optional field name, offset (0x40) or byte range (0x40+8) to revert alone", false)),
            NeedTag,
            args => McpJson.String(args, "field") is { Length: > 0 } field
                ? $"Revert {field} of {McpJson.String(args, "hash")} to base" : $"Revert {McpJson.String(args, "hash")} to base (drop the project's change)");
    }
}
