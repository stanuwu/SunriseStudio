/**
 * File: MCP tools for the game's interface data (CUI): get_cui_tree, find_cui_by_widget, render_cui. Decoders live in the
 * editor behind ISolforgeCuiHost (Data/CuiHost.cs).
 */
using System.Globalization;
using System.Text.Json.Nodes;
using Solforge.Mcp.Data;

namespace Solforge.Mcp;

public static partial class SolforgeTools
{
    public static readonly string[] CuiToolNames = ["get_cui_tree", "find_cui_by_widget", "render_cui"];

    private const string NoCui = "This Solforge host has no CUI decoders.";

    /** Parses 8 hex digits (type low half), 16 digits or AUX/LOW (full 64-bit type key); null when malformed. */
    private static (ulong? Key, uint? Low)? ParseWidgetType(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase).Replace("/", "", StringComparison.Ordinal);
        if (!ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong value)) return null;
        return text.Length > 8 ? (value, null) : (null, (uint)value);
    }

    /** Yields the CUI tools. */
    private static IEnumerable<McpTool> CuiTools(ISolforgeHost host)
    {
        McpTool Cui(string name, string description, JsonObject schema, Func<ISolforgeCuiHost, JsonObject, CancellationToken, McpToolResult> run) =>
            new(name, description, schema, false, (args, cancel) =>
            {
                if (host is not ISolforgeCuiHost cui) return Task.FromResult(McpToolResult.Error(NoCui));
                if (host is ISolforgeDataHost { DataUnavailable: { } reason }) return Task.FromResult(McpToolResult.Error(reason));
                return Task.Run(() =>
                {
                    try { return run(cui, args, cancel); }
                    catch (InvalidDataException error) { return McpToolResult.Error(error.Message); }
                    catch (NotSupportedException error) { return McpToolResult.Error(error.Message); }
                }, cancel);
            });

        yield return Cui("get_cui_tree", "Node tree of a CUI screen (class 0x8080496A, composed with every widget resource it imports) or a " +
            "widget resource (0x80804825), one row per node, paged: id, parent, label, widget type name + 64-bit key, name hash (and the name when a " +
            "string table has it), layout x/y/w/h in 1920x1080 space, static visibility, owning resource, bound bitmap and texture (art_status says " +
            "why art is missing), tint, text and referenced resource. Static default-profile data only: bindings and animations are not " +
            "evaluated. The talent-grid panel row (type 0x91E8C7E39BFF4CBA) is the native subclass grid; its slots are built at runtime.",
            McpJson.Schema([("hash", "string", "Screen or widget resource tag, e.g. 0x80BC7483", true),
                ("node", "integer", "Only this node and its descendants", false),
                ("include_hidden", "boolean", "Also list nodes hidden by a static visibility flag (default true; they carry visible=false)", false),
                .. DataPages.Args]),
            (cui, args, cancel) =>
            {
                if (ParseHash(McpJson.String(args, "hash")) is not { } tag) return McpToolResult.Error("Give a CUI tag hash.");
                int? node = args.ContainsKey("node") ? McpJson.Int(args, "node", 0) : null;
                if (cui.CuiTree(tag, node, McpJson.Bool(args, "include_hidden", true), cancel) is not { } tree) return McpToolResult.Error($"Unknown tag 0x{tag:X8}.");
                var page = DataPages.Page(tree.Rows, args, row => row.DeepClone());
                foreach (var (key, value) in tree.Header) page[key] = value?.DeepClone();
                return McpToolResult.Json(page);
            });

        yield return Cui("find_cui_by_widget", "Which CUI screens (0x8080496A) and widget resources (0x80804825) contain a widget type or a node " +
            "name hash. type: the low 32 bits ('9BFF4CBA'), or the full 64-bit key ('91E8C7E39BFF4CBA' or '91E8C7E3/9BFF4CBA'; auxiliary hash first). " +
            "Screens match through the resources they import. Rows give the tag, name, class, package, matching node count and, for screens, " +
            "the resources that supply the widget. Scans all ~1,600 CUI tags (a few seconds).",
            McpJson.Schema([("type", "string", "Widget type hash (8 hex digits) or 64-bit key", false),
                ("name_hash", "string", "Node name hash, e.g. 0x14223E4D", false), .. DataPages.Args]),
            (cui, args, cancel) =>
            {
                var type = ParseWidgetType(McpJson.String(args, "type"));
                uint? name = McpJson.String(args, "name_hash") is { Length: > 0 } text ? ParseHash(text) : null;
                if (type is null && name is null) return McpToolResult.Error("Give a widget type hash (type) or a node name hash (name_hash).");
                var found = cui.FindCuiByWidget(type?.Key, type?.Low, name, cancel);
                var page = DataPages.Page(found.Rows, args, row => row.DeepClone());
                foreach (var (key, value) in found.Header) page[key] = value?.DeepClone();
                return McpToolResult.Json(page);
            });

        yield return Cui("render_cui", "Render a CUI screen (or one node's subtree with node) to a PNG under the project's exports\\cui folder " +
            "(or %LOCALAPPDATA%\\Solforge\\exports\\cui) and return the path. Draws bound bitmaps (icons resolved bitmap -> texture, multiplied by the " +
            "stored tint), text, and labelled dashed placeholders for widget types with no art decoder; statically hidden nodes are skipped. " +
            "subclass_item (e.g. 0x078CFF0D) adds the subclass grid overlay: the 23 slots labelled like '13 · Path 1 · Shatterdive' from " +
            "subclass_nodes.tsv, at approximate runtime positions over the talent-grid panel.",
            McpJson.Schema(("hash", "string", "Screen tag, e.g. 0x80BC7483", true), ("node", "integer", "Render only this node and its descendants", false),
                ("subclass_item", "string", "Subclass item hash for the grid overlay", false), ("scale", "number", "Pixel scale (default 1, max 4)", false),
                ("labels", "boolean", "Print node IDs on large widgets", false), ("show_hidden", "boolean", "Also draw statically hidden nodes", false)),
            (cui, args, cancel) =>
            {
                if (ParseHash(McpJson.String(args, "hash")) is not { } tag) return McpToolResult.Error("Give a CUI screen tag hash.");
                uint? item = McpJson.String(args, "subclass_item") is { Length: > 0 } text ? ParseHash(text) : null;
                string folder = Path.Combine(ExportFolder(host.ProjectRoot), "cui");
                Directory.CreateDirectory(folder);
                int? node = args.ContainsKey("node") ? McpJson.Int(args, "node", 0) : null;
                float scale = (float)(args["scale"] is JsonValue v && v.TryGetValue<double>(out double d) ? d : 1);
                var result = cui.RenderCui(tag, node, item, scale, McpJson.Bool(args, "labels", false), McpJson.Bool(args, "show_hidden", false), folder, cancel);
                return result is null ? McpToolResult.Error($"Unknown tag 0x{tag:X8}.") : McpToolResult.Json(result);
            });
    }
}
