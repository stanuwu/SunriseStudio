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
    public static readonly string[] PerkActionTools = ["edit_perk_behavior", "create_perk"];

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
    }
}
