/**
 * File: MCP tools for sandbox perk behavior programs: get_perk_behavior, list_perk_node_types, edit_perk_behavior and
 * create_perk (the Perk Behavior editor's read, edit and New perk flows without the UI).
 */
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/**
 * Perk programs are the native graphs the client runs for a sandbox perk (NativePerkBehavior). Reads go through
 * ISolforgeHost.QueryAsync like the catalog tools; edit_perk_behavior and create_perk wait for the approve click and save
 * to the project with undo.
 */
public static partial class SolforgeTools
{
    /** Read tools of this file, in listing order. */
    public static readonly string[] PerkReadTools = ["get_perk_behavior", "list_perk_node_types"];
    /** Action tools of this file. */
    public static readonly string[] PerkActionTools = ["edit_perk_behavior", "create_perk", "clone_tags", "fork_subclass_grid", "set_display_icons", "set_item_screen_art",
        "add_icon_row", "add_list_presentation_rows", "add_talent_display_rows", "set_icon_layer_texture", "set_item_lore", "fork_list_text_bank", "set_talent_display_record", "set_icon_container_fingerprint",
        "set_subclass_element"];

    /** Phase names the perk tools take. */
    public static readonly string[] PerkPhases = ["activation", "effects", "deactivation"];

    private const string PerkNodeHelp = "Each node is {primitive, values: {key: value}, inner: {key: value}?}; primitive and " +
        "value keys come from list_perk_node_types (numbers, or 0x hex for hashes and tags). inner sets the inner condition of a " +
        "state-* node.";

    /** Yields the perk behavior tools. */
    private static IEnumerable<McpTool> PerkTools(ISolforgeHost host,
        Func<string, string, JsonObject, Func<JsonObject, CancellationToken, Task<McpToolResult>>, McpTool> read,
        Func<string, string, JsonObject, Func<JsonObject, string?>, Func<JsonObject, string>, McpTool> act)
    {
        McpTool Query(string name, string description, JsonObject schema) => read(name, description, schema, async (args, cancel) =>
        {
            var result = await host.QueryAsync(name, args, cancel).ConfigureAwait(false);
            return result["error"] is JsonValue error ? McpToolResult.Error(error.ToString()) : McpToolResult.Json(result);
        });
        static string? NeedPerk(JsonObject args) =>
            string.IsNullOrWhiteSpace(McpJson.String(args, "perk")) ? "Give a perk index, exact name or program tag (0x...)." : null;
        static string? NeedOps(JsonObject args) => args["ops"] is JsonArray { Count: > 0 } ops
            && ops.All(op => op is JsonObject row && (string?)row["op"] is "add" or "set" or "remove" or "labels" or "hud" or "display")
            ? null : "Give ops: [{op: add|set|remove|labels|hud|display, ...}].";

        yield return Query("get_perk_behavior", "A sandbox perk's native behavior program, decompiled: its program tag, whether " +
            "the project changes it, the HUD status, and the activation, effects and deactivation phases with every node's " +
            "primitive, inputs (key, value, range, choices), inner conditions and label filters. Nodes marked editable=false " +
            "are kept byte for byte. tested says the node kind has worked in game. perk is a sandbox perk index (search_perks), " +
            "an exact perk name or a program tag (0x...).",
            McpJson.Schema(("perk", "string", "Perk index, exact name or program tag (0x...)", true)));
        yield return Query("list_perk_node_types", "Node kinds a perk program can hold (conditions and effects) with their " +
            "inputs and defaults, for edit_perk_behavior and create_perk. tested marks kinds proven in game; the others compile " +
            "but may do nothing.",
            McpJson.Schema(("query", "string", "Optional words, e.g. 'spawn' or 'kill'", false)));
        yield return act("edit_perk_behavior", "Edit an existing perk program and save it to the project with undo (it changes " +
            "every item that uses the perk). ops run in order: {op: add, phase, node, at?} adds a node (at = index, default " +
            "end); {op: set, phase, index, values, inner?} changes inputs; {op: remove, phase, index}; {op: labels, phase, index, filter, anyOf?, allOf?, noneOf?, notAllOf?} " +
            "replaces a node's label filter lists (filter keys and hashes from get_perk_behavior); {op: hud, label?, " +
            "show_timer?} sets or clears (label 0) the HUD status; {op: display, name?, description?, icon?} (alone) changes " +
            "a project perk's name and description as shown on items (an empty name hides its row) and copies the icon of " +
            "the perk icon names (index or exact name). phase is activation, effects or deactivation. " + PerkNodeHelp,
            new JsonObject
            {
                ["type"] = "object", ["required"] = new JsonArray("perk", "ops"),
                ["properties"] = new JsonObject
                {
                    ["perk"] = new JsonObject { ["type"] = "string", ["description"] = "Perk index, exact name or program tag (0x...)" },
                    ["ops"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object" }, ["description"] = "Edits in order" }
                }
            },
            args => NeedPerk(args) ?? NeedOps(args),
            args => $"Edit perk program {McpJson.String(args, "perk")}: {(args["ops"] as JsonArray)?.Count ?? 0} op(s) " + args["ops"]?.ToJsonString());
        yield return act("create_perk", "Register a new sandbox perk (definition, display and runtime-name rows, a compiled " +
            "program and its name and description strings) in the project. No item carries it: put it on a plug with " +
            "apply_item_edit add_perk, or have the server add it (sol_custom_data.lua ability sandbox_perks). The reply gives " +
            "the new perk index once the work finishes (call again with the same name to read it). from copies an existing perk's " +
            "program (every node byte for byte, label filters included) and its icon; activation/effects/deactivation are then " +
            "added after its nodes. icon takes another perk's icon instead. " + PerkNodeHelp,
            new JsonObject
            {
                ["type"] = "object", ["required"] = new JsonArray("name"),
                ["properties"] = new JsonObject
                {
                    ["name"] = new JsonObject { ["type"] = "string", ["description"] = "Perk name (1-200 characters)" },
                    ["description"] = new JsonObject { ["type"] = "string", ["description"] = "Perk description (at most 4000)" },
                    ["activation"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object" } },
                    ["effects"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object" } },
                    ["deactivation"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object" },
                        ["description"] = "Default: one 'timer' of 0.1 s so the perk re-arms" },
                    ["package"] = new JsonObject { ["type"] = "string", ["description"] = "Optional new package ID (hex); default as custom perks choose it" },
                    ["from"] = new JsonObject { ["type"] = "string", ["description"] = "Optional perk (index, exact name or program tag) whose program to copy first" },
                    ["icon"] = new JsonObject { ["type"] = "string", ["description"] = "Optional perk (index or exact name) whose icon to show" }
                }
            },
            args => string.IsNullOrWhiteSpace(McpJson.String(args, "name")) ? "Give the perk a name."
                : !string.IsNullOrWhiteSpace(McpJson.String(args, "from"))
                  || args["activation"] is JsonArray { Count: > 0 } && args["effects"] is JsonArray { Count: > 0 } ? null
                : "Give from, or at least one activation node and one effect node.",
            args => $"Create perk '{McpJson.String(args, "name")}'" + (McpJson.String(args, "from") is { Length: > 0 } from ? $" from perk {from}" : "") +
                $": activation {args["activation"]?.ToJsonString()}, effects " +
                $"{args["effects"]?.ToJsonString()}, deactivation {args["deactivation"]?.ToJsonString() ?? "[timer 0.1]"}");
        yield return act("clone_tags", "Copy installed tags (any class, e.g. a buff object and its config) into the project as new " +
            "entries. Inside the copies, every 4-byte value naming a copied tag is swapped for its copy, replace swaps other " +
            "values ({\"0xOLD\": \"0xNEW\"}) and strings swaps a string hash for a new project string ({\"0xHASH\": \"text\"}). " +
            "Nothing uses the copies until another edit names them (e.g. a perk spawn node's definition). The reply lists the new " +
            "tags once the work finishes (call again with the same name to read them).",
            new JsonObject
            {
                ["type"] = "object", ["required"] = new JsonArray("tags", "name"),
                ["properties"] = new JsonObject
                {
                    ["tags"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Tags to copy (0x...)" },
                    ["name"] = new JsonObject { ["type"] = "string", ["description"] = "Name for the copies in Project changes" },
                    ["replace"] = new JsonObject { ["type"] = "object", ["description"] = "Optional {\"0xOLD\": \"0xNEW\"} value swaps" },
                    ["strings"] = new JsonObject { ["type"] = "object", ["description"] = "Optional {\"0xHASH\": \"new text\"} string swaps" }
                }
            },
            args => args["tags"] is JsonArray { Count: > 0 } && !string.IsNullOrWhiteSpace(McpJson.String(args, "name")) ? null
                : "Give tags [0x...] and a name.",
            args => $"Copy tags {args["tags"]?.ToJsonString()} as '{McpJson.String(args, "name")}'; replace {args["replace"]?.ToJsonString() ?? "{}"}, " +
                $"strings {args["strings"]?.ToJsonString() ?? "{}"}");
        yield return act("fork_subclass_grid", "The talent-grid writer: give a subclass item (e.g. a Stasis clone that still shares its " +
            "base's grid) its own socket-entry list. Copies the list definition and every pool tag its entries name into the project, " +
            "points the copy at the copied pools, appends a socket-entry-list table row (investment root slot 97) for it and points " +
            "the item at that row. The base subclass is untouched; edit the copied pools (clone_tags replace, or their bytes) to change " +
            "the fork's nodes. The reply gives the new list once the work finishes (call again with the same name).",
            new JsonObject
            {
                ["type"] = "object", ["required"] = new JsonArray("item", "name"),
                ["properties"] = new JsonObject
                {
                    ["item"] = new JsonObject { ["type"] = "string", ["description"] = "Subclass item definition tag (0x...)" },
                    ["name"] = new JsonObject { ["type"] = "string", ["description"] = "Name for the copies in Project changes" }
                }
            },
            args => !string.IsNullOrWhiteSpace(McpJson.String(args, "item")) && !string.IsNullOrWhiteSpace(McpJson.String(args, "name")) ? null
                : "Give the subclass item tag and a name.",
            args => $"Fork the socket-entry list of item {McpJson.String(args, "item")} as '{McpJson.String(args, "name")}'");
        yield return act("set_display_icons", "Point a project item display's icon rows at existing icon-table rows: primary is " +
            "display +0x80 (the item icon), secondary is +0x82 (the tooltip's element icon). Rows are u16 icon-table indexes " +
            "(decimal or 0x hex) and must exist; only displays the project owns change. One undo step in Project changes. If no " +
            "document is open the first call opens the display; call again.",
            new JsonObject
            {
                ["type"] = "object", ["required"] = new JsonArray("display"),
                ["properties"] = new JsonObject
                {
                    ["display"] = new JsonObject { ["type"] = "string", ["description"] = "Item display tag (0x...)" },
                    ["primary"] = new JsonObject { ["type"] = "string", ["description"] = "Optional icon row for +0x80" },
                    ["secondary"] = new JsonObject { ["type"] = "string", ["description"] = "Optional icon row for +0x82" }
                }
            },
            args => string.IsNullOrWhiteSpace(McpJson.String(args, "display")) ? "Give the display tag."
                : string.IsNullOrWhiteSpace(McpJson.String(args, "primary")) && string.IsNullOrWhiteSpace(McpJson.String(args, "secondary"))
                    ? "Give primary and/or secondary." : null,
            args => $"Set display {McpJson.String(args, "display")} icon rows: primary {McpJson.String(args, "primary") ?? "kept"}, " +
                $"secondary {McpJson.String(args, "secondary") ?? "kept"}");
        yield return act("set_item_screen_art", "Set a project subclass item definition's u32 at +0x1FC to 97, 98 or 99. NOTE " +
            "(2026-11-09): +0x1FC is the subclass item's LORE index (its +0x28 lore block, class 0x808077ED; 97 = Arcstrider's " +
            "lore row), not screen art; prefer set_item_lore. Only " +
            "items the project owns, only those three values. One undo step in Project changes. If no document is open the " +
            "first call opens the item; call again.",
            new JsonObject
            {
                ["type"] = "object", ["required"] = new JsonArray("item", "value"),
                ["properties"] = new JsonObject
                {
                    ["item"] = new JsonObject { ["type"] = "string", ["description"] = "Item definition tag (0x...)" },
                    ["value"] = new JsonObject { ["type"] = "string", ["description"] = "97, 98 or 99" }
                }
            },
            args => string.IsNullOrWhiteSpace(McpJson.String(args, "item")) || string.IsNullOrWhiteSpace(McpJson.String(args, "value"))
                ? "Give the item tag and the value." : null,
            args => $"Set item {McpJson.String(args, "item")} screen art index (+0x1FC) to {McpJson.String(args, "value")}");
        yield return act("add_icon_row", "Append an icon-table row (investment icon table 0x81A291C2) naming an existing project icon " +
            "container (class 0x80804A53), through the custom-icon row writer, under a row hash derived from the container tag. " +
            "Returns the new row index, for set_display_icons (e.g. secondary = a subclass's screen art). A row already naming " +
            "the container is returned unchanged. Only containers the project owns. One undo step. If no document is open the " +
            "first call opens the container; call again.",
            new JsonObject
            {
                ["type"] = "object", ["required"] = new JsonArray("container"),
                ["properties"] = new JsonObject
                {
                    ["container"] = new JsonObject { ["type"] = "string", ["description"] = "Icon container tag (0x...)" }
                }
            },
            args => string.IsNullOrWhiteSpace(McpJson.String(args, "container")) ? "Give the icon container tag." : null,
            args => $"Append an icon-table row for container {McpJson.String(args, "container")}");
        yield return act("add_list_presentation_rows", "Append per-list presentation rows to the dense item-presentation " +
            "table (0x81A28B23, descriptor +0x48, one {first field, field count} row per socket-entry list, read by list index; " +
            "a list without a row shows no subclass nodes). Each new row copies its base list's row, as Parhelion's " +
            "append_dense_list_presentation does. rows: list=base pairs in order, e.g. 14=3,15=5,16=11; new lists must follow " +
            "the existing rows and exist in the socket-entry-list table. One undo step. If no document is open the first call " +
            "opens the table; call again.",
            new JsonObject
            {
                ["type"] = "object", ["required"] = new JsonArray("rows"),
                ["properties"] = new JsonObject
                {
                    ["rows"] = new JsonObject { ["type"] = "string", ["description"] = "list=base pairs, e.g. 14=3,15=5,16=11" }
                }
            },
            args => string.IsNullOrWhiteSpace(McpJson.String(args, "rows")) ? "Give rows as list=base pairs." : null,
            args => $"Add list presentation rows {McpJson.String(args, "rows")}");
        yield return act("add_talent_display_rows", "Append rows to the client talent display table (0x81613D09, class " +
            "0x80805C3C: one {list row hash, display record 0x80805C42} row per socket-entry list, same order and hashes as the " +
            "socket-entry-list table; a list without a row draws an empty subclass grid). Each new row takes the list's hash " +
            "from the socket-entry-list table and shares its base list's display record. rows: list=base pairs in order, e.g. " +
            "14=3,15=5,16=11. One undo step. If no document is open the first call opens the table; call again.",
            new JsonObject
            {
                ["type"] = "object", ["required"] = new JsonArray("rows"),
                ["properties"] = new JsonObject
                {
                    ["rows"] = new JsonObject { ["type"] = "string", ["description"] = "list=base pairs, e.g. 14=3,15=5,16=11" }
                }
            },
            args => string.IsNullOrWhiteSpace(McpJson.String(args, "rows")) ? "Give rows as list=base pairs." : null,
            args => $"Add talent display rows {McpJson.String(args, "rows")}");
        yield return act("set_icon_layer_texture", "Import a PNG as a new 2D RGBA8 texture in the project package and point " +
            "every texture reference of a project icon layer (class 0x80804A69, e.g. a subclass big-art layer from clone_tags) at " +
            "it. Installed textures are never changed. The layer's first installed texture is the header template. Reply lists " +
            "the new texture tags; call again with the same name to read it once the work finishes.",
            new JsonObject
            {
                ["type"] = "object", ["required"] = new JsonArray("layer", "path", "name"),
                ["properties"] = new JsonObject
                {
                    ["layer"] = new JsonObject { ["type"] = "string", ["description"] = "Project icon layer tag (0x...)" },
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Absolute .png path" },
                    ["name"] = new JsonObject { ["type"] = "string", ["description"] = "Texture name in Project changes" }
                }
            },
            args => string.IsNullOrWhiteSpace(McpJson.String(args, "layer")) || string.IsNullOrWhiteSpace(McpJson.String(args, "name"))
                ? "Give layer, path and name." : null,
            args => $"New texture {McpJson.String(args, "name")} for layer {McpJson.String(args, "layer")}");
        yield return act("set_item_lore", "Give a project item its own lore (the Show Lore page): title, bold subtitle and " +
            "body become new project strings; a row is appended to both lore tables (0x81319348 definitions, 0x81613CF2 display), " +
            "copied from the row the item names now, and the item's lore block (+0x28 -> class 0x808077ED u16) is pointed at it. " +
            "If the item already names a project row, that row's strings are replaced. Strings shared with other items are never " +
            "changed. Call again with only item and title to read the result.",
            new JsonObject
            {
                ["type"] = "object", ["required"] = new JsonArray("item", "title"),
                ["properties"] = new JsonObject
                {
                    ["item"] = new JsonObject { ["type"] = "string", ["description"] = "Project item definition tag (0x...)" },
                    ["title"] = new JsonObject { ["type"] = "string", ["description"] = "Lore title" },
                    ["subtitle"] = new JsonObject { ["type"] = "string", ["description"] = "Bold tagline under the title" },
                    ["body"] = new JsonObject { ["type"] = "string", ["description"] = "Lore text; \n for line breaks" }
                }
            },
            args => string.IsNullOrWhiteSpace(McpJson.String(args, "item")) || string.IsNullOrWhiteSpace(McpJson.String(args, "title"))
                ? "Give item and title (and subtitle + body to write)." : null,
            args => $"Set lore of {McpJson.String(args, "item")}: {McpJson.String(args, "title")}");
        yield return act("fork_list_text_bank", "Give an ADDED socket-entry list (one fork_subclass_grid made, e.g. 14) its own " +
            "copy of one of its text banks (class 0x80809A88, one of the 5 banks its presentation type-9 field names), with " +
            "strings replaced or added by role hash (e.g. 0xDF417340 = the path name under the big art, node name/description " +
            "role hashes from a node record's +0xA0 block), plus a string-bank index row so node records can name the copy. The " +
            "list's presentation fields are forked so only this list reads it; shipped and base lists keep theirs. icons swaps " +
            "handles of the list's type-1 icon field for project icon containers. Re-running for the same list and bank rewrites " +
            "the copy. Call again with only list and bank to read the result (new bank tag and bank index).",
            new JsonObject
            {
                ["type"] = "object", ["required"] = new JsonArray("list", "bank"),
                ["properties"] = new JsonObject
                {
                    ["list"] = new JsonObject { ["type"] = "integer", ["description"] = "Added socket-entry list, e.g. 14" },
                    ["bank"] = new JsonObject { ["type"] = "string", ["description"] = "Installed bank the list names (0x...)" },
                    ["texts"] = new JsonObject { ["type"] = "object", ["description"] = "{\"0xROLEHASH\": \"text\"}" },
                    ["icons"] = new JsonObject { ["type"] = "object", ["description"] = "{\"0xINSTALLED_HANDLE\": \"0xPROJECT_CONTAINER\"}" }
                }
            },
            args => string.IsNullOrWhiteSpace(McpJson.String(args, "list")) || string.IsNullOrWhiteSpace(McpJson.String(args, "bank"))
                ? "Give list and bank." : null,
            args => $"Fork list {McpJson.String(args, "list")} text bank {McpJson.String(args, "bank")}");
        yield return act("set_talent_display_record", "Point an ADDED socket-entry list's row of the talent display table " +
            "(0x81613D09) at a project display record (class 0x80805C42, e.g. a clone_tags copy of the base record and its " +
            "node records), so that list stops sharing its base subclass's record. Shipped rows are refused. One undo step. If no " +
            "document is open the first call opens the table; call again.",
            new JsonObject
            {
                ["type"] = "object", ["required"] = new JsonArray("list", "record"),
                ["properties"] = new JsonObject
                {
                    ["list"] = new JsonObject { ["type"] = "integer", ["description"] = "Added socket-entry list, e.g. 14" },
                    ["record"] = new JsonObject { ["type"] = "string", ["description"] = "Project display record tag (0x...)" }
                }
            },
            args => string.IsNullOrWhiteSpace(McpJson.String(args, "list")) || string.IsNullOrWhiteSpace(McpJson.String(args, "record"))
                ? "Give list and record." : null,
            args => $"Set list {McpJson.String(args, "list")} display record to {McpJson.String(args, "record")}");
        yield return act("set_icon_container_fingerprint", "Set a project icon container's fingerprint (+0x10, u32). An authored " +
            "container sets its own so it stops sharing its donor's (Parhelion art.rs). Default value: Parhelion's SHA-1 form " +
            "over the container from +0x14, its tag and its layer's bytes (stable per container). Only containers the project " +
            "owns; 0 and 0xFFFFFFFF are refused. One undo step. If no document is open the first call opens the container; call again.",
            new JsonObject
            {
                ["type"] = "object", ["required"] = new JsonArray("container"),
                ["properties"] = new JsonObject
                {
                    ["container"] = new JsonObject { ["type"] = "string", ["description"] = "Icon container tag (0x...)" },
                    ["value"] = new JsonObject { ["type"] = "string", ["description"] = "Optional u32 (decimal or 0x hex); default derived" }
                }
            },
            args => string.IsNullOrWhiteSpace(McpJson.String(args, "container")) ? "Give the icon container tag." : null,
            args => $"Set icon container {McpJson.String(args, "container")} fingerprint (+0x10) to " +
                (McpJson.String(args, "value") is { Length: > 0 } value ? value : "a value derived from the container"));
        yield return act("set_subclass_element", "Set a project subclass item's element as Parhelion's set_element and " +
            "set_damage_type do: the definition's element unlock flag (Arc 0x7B3, Solar 0x7B4, Void 0x7B5) and element value " +
            "(index 0x1E9: Arc 1, Solar 3, Void 2) in its +0x90 unlock block, and its display's stat block (+0x70) damage type " +
            "(+8: Kinetic 0, Solar 1, Arc 2, Void 3). Kinetic removes the element flag and sets the value to 0. Item and display " +
            "must be project-owned. One undo step. If no document is open the first call opens the item; call again.",
            new JsonObject
            {
                ["type"] = "object", ["required"] = new JsonArray("item", "element"),
                ["properties"] = new JsonObject
                {
                    ["item"] = new JsonObject { ["type"] = "string", ["description"] = "Subclass item definition tag (0x...)" },
                    ["element"] = new JsonObject { ["type"] = "string", ["description"] = "arc, solar, void or kinetic" }
                }
            },
            args => string.IsNullOrWhiteSpace(McpJson.String(args, "item")) ? "Give the subclass item tag."
                : (McpJson.String(args, "element") ?? "").Trim().ToLowerInvariant() is "arc" or "solar" or "void" or "kinetic" ? null
                : "element must be arc, solar, void or kinetic.",
            args => $"Set subclass item {McpJson.String(args, "item")} element to {McpJson.String(args, "element")}");
    }
}
