/**
 * File: MCP tools for editor state, project files and changes, build status, settings, maps, missions, packages,
 * item/perk/vendor search and creation types.
 */
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/**
 * The project-wide tools: maps, missions, packages, assets, world objects, placements, items, vendors, creation, settings and
 * builds. Reads go through ISolforgeHost.QueryAsync (paged, heavy work off the UI thread); none needs a map open unless its
 * description says so. Actions go through the same approve click as every other action tool.
 */
public static partial class SolforgeTools
{
    /** Reads about the project and installed game rather than what the user looks at; they work while context is paused. */
    public static readonly string[] CatalogTools =
    [
        "get_editor_state", "get_project", "list_project_files", "list_project_changes", "get_build_status", "get_settings",
        "list_maps", "list_missions", "list_packages", "list_package_entries", "search_assets", "list_mission_scripts",
        "search_items", "get_item", "search_perks", "list_vendors", "get_vendor", "list_creation_types", "describe_creation"
    ];

    /** Reads about the open map; they stop while context publishing is paused. */
    public static readonly string[] WorldTools = ["find_world_objects", "get_placement"];

    /** Standard paging arguments (offset, limit). */
    private static readonly (string Name, string Type, string Description, bool Required)[] PageArgs =
        [("offset", "integer", "First row (default 0)", false), ("limit", "integer", "Rows per page (default 50, max 500)", false)];

    /** Schema with the paging arguments and vector array properties added. */
    private static JsonObject Paged(params (string, string, string, bool)[] properties) =>
        Vectors(McpJson.Schema([.. properties, .. PageArgs]));

    /** Array properties are lists of numbers (positions, corners). */
    private static JsonObject Vectors(JsonObject schema)
    {
        foreach (var (_, node) in (JsonObject)schema["properties"]!)
            if (node is JsonObject property && (string?)property["type"] == "array") property["items"] = new JsonObject { ["type"] = "number" };
        return schema;
    }

    /** Yields the project and catalogue tools. */
    private static IEnumerable<McpTool> ProjectTools(ISolforgeHost host,
        Func<string, string, JsonObject, Func<JsonObject, CancellationToken, Task<McpToolResult>>, McpTool> read,
        Func<string, string, JsonObject, Func<JsonObject, string?>, Func<JsonObject, string>, McpTool> act)
    {
        McpTool Query(string name, string description, JsonObject schema) => read(name, description, schema, async (args, cancel) =>
        {
            var result = await host.QueryAsync(name, args, cancel).ConfigureAwait(false);
            return result["error"] is JsonValue error ? McpToolResult.Error(error.ToString()) : McpToolResult.Json(result);
        });
        /** Error text when a required argument is blank; null when present. */
        static string? Need(JsonObject args, string name, string message) => string.IsNullOrWhiteSpace(McpJson.String(args, name)) ? message : null;
        /** String argument or empty text. */
        static string Arg(JsonObject args, string name) => McpJson.String(args, name) ?? "";

        // Editor and project.
        yield return Query("get_editor_state", "Solforge's state in one call: installation and game build, project, open map and " +
            "mission, open asset, busy work and progress, last error, whether the 3D viewport renders (and why not), Live link, " +
            "Destiny 2 running, MCP and the skills folder, and a memory breakdown in MiB (process and managed heaps, the open " +
            "scene's decoded and native mesh bytes per detail level, caches, native plan and viewport GPU estimates). " +
            "Works with nothing open.", McpJson.Schema());
        yield return Query("get_project", "The open project: name, folder, mod details (set_mod_details sets them), counts of " +
            "asset, object and script changes, and the game install it targets.", McpJson.Schema());
        yield return Query("list_project_files", "Files in the project folder (relative paths, sizes), filtered and paged. " +
            "Read one with read_project_file.", Paged(("filter", "string", "Optional text the path must contain", false),
                ("extension", "string", "Optional extension, e.g. .lua", false)));
        yield return Query("list_project_changes", "Every project change, paged: asset edits (tag, name, package), object edits " +
            "(key for revert_object_edit, object name, what changed) and added assets.",
            Paged(("filter", "string", "Optional text a row must contain", false), ("kind", "string", "assets, objects or added; default all", false)));
        yield return Query("get_build_status", "The last build and install: when, output folder, notes and errors, and whether " +
            "a build is running now.", McpJson.Schema());
        yield return Query("get_settings", "Solforge settings the playbooks need: game install, project, server mission scripts " +
            "folder, Blender path, viewport rendering, low memory mode, texture budget, FPS caps, interface size and AI options. " +
            "set_settings changes them (same names).", McpJson.Schema());

        // Maps, missions, packages and assets: no map needs to be open.
        yield return Query("list_maps", "Every map in the installed game with its tag and its zones (bubbles) by name. No map needs " +
            "to be open. Filter matches the map name, tag or a zone name, e.g. 'edz' or 'dark forest'.",
            Paged(("filter", "string", "Optional words a map or one of its zones must contain", false)));
        yield return Query("list_missions", "Missions and activities with their tags, zones and the maps they load on. With a map " +
            "open, only that map's missions are listed (the reply's map says which); name another map, or all=true for every " +
            "mission. No map needs to be open.",
            Paged(("filter", "string", "Optional words the name, tag or a zone must contain", false),
                ("map", "string", "Optional map tag to list only its missions (default: the open map)", false),
                ("all", "boolean", "List every mission, not just the open map's (default false)", false)));
        yield return Query("list_packages", "Installed packages with entry counts, filtered and paged. Works without a map.",
            Paged(("filter", "string", "Optional text the package name must contain", false)));
        yield return Query("list_package_entries", "Entries of one package (tag, index, type, subtype, name), paged.",
            Paged(("package", "string", "Package name (from list_packages)", true), ("filter", "string", "Optional text the name or tag must contain", false)));
        yield return Query("search_assets", "Search the asset index of the whole install (name, kind, tag, package), paged. Works " +
            "without a map. Every word must match, e.g. 'shard traveler' or 'kill volume edz'.",
            Paged(("query", "string", "Words to find", true), ("kind", "string", "Optional asset kind, e.g. Map, Static prop, Material", false)));
        yield return Query("list_mission_scripts", "Lua mission scripts in the project and the server scripts folder (Settings > " +
            "Paths; set_settings serverScripts sets it), filtered and paged; read them with read_project_file (server ones as " +
            "scripts:<path>), open project ones with open_file.",
            Paged(("filter", "string", "Optional text the path must contain", false)));

        // The open map's objects.
        yield return Query("find_world_objects", "Search the open map's objects, paged, off the UI thread: name, kind, zone, " +
            "layer (volumes, collision, meshes, lights...), or near a point. Use layer=volumes for kill, blocker and trigger " +
            "volumes, layer=collision for invisible walls. Rows give id, name, kind, layer, zone, position and extents; " +
            "then get_placement for what can be edited. Needs a map open (open_map).",
            Paged(("filter", "string", "Optional words the name, kind or zone must contain", false),
                ("layer", "string", "Optional layer: volumes, collision, meshes, markers, lights, authored, squads", false),
                ("zone", "string", "Optional zone (bubble) name", false),
                ("near", "array", "Optional [x, y, z] point", false), ("radius", "number", "Distance from near (default 100)", false)));
        yield return Query("get_placement", "What an object's placement allows: position, rotation (degrees), scale, bounds " +
            "(trigger volumes), the coordinate space and why an edit is refused. Use before edit_placement or edit_bounds.",
            McpJson.Schema(("id", "string", "Object id from find_world_objects, find_object or get_context", true)));

        // Items, perks and vendors (the investment catalog; first use reads it in the background).
        yield return Query("search_items", "Search weapons, armor and items by name, type, rarity or hash, paged. The first call " +
            "may say the catalog is loading; call again. Pairs with skills solforge-new-weapon and solforge-new-armor.",
            Paged(("query", "string", "Optional words, e.g. 'hand cannon legendary'", false),
                ("category", "string", "Optional: weapons, armor, items or currencies", false)));
        yield return Query("get_item", "One item's definition: name, type, rarity, bucket, stats, sockets and plugs, perks, the " +
            "editable fields (for apply_item_edit set_field) and diagnostics.",
            McpJson.Schema(("item", "string", "Item tag (0x...), hash or exact name", true)));
        yield return Query("search_perks", "Perk definitions by name, description or index, paged.",
            Paged(("query", "string", "Optional words", false)));
        yield return Query("list_vendors", "Vendors with tag and name, paged.", Paged(("filter", "string", "Optional words", false)));
        yield return Query("get_vendor", "One vendor's sales: sale index, item, category and costs (item, quantity), paged.",
            Paged(("vendor", "string", "Vendor tag (0x...), hash or exact name", true)));

        // Creation (New and Clone).
        yield return Query("list_creation_types", "Everything Solforge can create or clone (keys and modes for start_creation), " +
            "with a blocker when it can't yet.", McpJson.Schema(("filter", "string", "Optional words", false)));
        yield return Query("describe_creation", "The fields and limits of one creation workflow, so start_creation can prefill them.",
            McpJson.Schema(("key", "string", "Type key from list_creation_types", true), ("mode", "string", "new or clone", true)));

        // Actions: each waits for the user's approve click.
        yield return act("open_map", "Open a map, a mission on its map, or both, in Solforge. A mission alone opens on the map it " +
            "belongs to (resolved for you); a map alone lists its missions in the reply. Large maps take a while; poll " +
            "get_editor_state until it is no longer busy.",
            McpJson.Schema(("map", "string", "Map tag from list_maps (optional when mission is given)", false),
                ("mission", "string", "Mission tag from list_missions", false)),
            args => ParseHash(McpJson.String(args, "map")) is null && ParseHash(McpJson.String(args, "mission")) is null
                ? "Give a map tag from list_maps or a mission tag from list_missions." : null,
            args => (McpJson.String(args, "mission") is { Length: > 0 } mission ? $"Open mission {mission}" : $"Open map {Arg(args, "map")}")
                + (McpJson.String(args, "mission") is { Length: > 0 } && McpJson.String(args, "map") is { Length: > 0 } map ? $" on map {map}" : ""));
        yield return act("open_asset", "Open an asset (item, vendor, material, any tag) in Solforge's inspector and editor pages.",
            McpJson.Schema(("tag", "string", "Tag hash (0x...)", true)),
            args => ParseHash(McpJson.String(args, "tag")) is null ? "Give a tag such as 0x80A1B2C3." : null, args => $"Open asset {Arg(args, "tag")}");
        yield return act("set_viewport_rendering", "Turn the 3D viewport on or off, and/or set its quality. Off saves GPU and " +
            "memory; every tool except get_viewport_screenshot keeps working. Quality: low (flat grey, coarse levels, short " +
            "cull), medium (textured, coarse levels), high (game shaders and lighting, game levels), full (all detail, decals, " +
            "effects). Saved in settings.",
            McpJson.Schema(("on", "boolean", "true to render, false to stop", false),
                ("quality", "string", "low, medium, high or full", false)),
            args => args["on"] is not JsonValue && McpJson.String(args, "quality") is not { Length: > 0 }
                ? "Give on: true or false, and/or quality: low, medium, high or full."
                : McpJson.String(args, "quality") is { Length: > 0 } quality
                    && quality.ToLowerInvariant() is not ("low" or "medium" or "high" or "full")
                    ? "quality must be low, medium, high or full." : null,
            args => string.Join("; ", new[]
            {
                args["on"] is JsonValue ? (McpJson.Bool(args, "on", true) ? "Turn the 3D viewport on" : "Turn the 3D viewport off") : null,
                McpJson.String(args, "quality") is { Length: > 0 } quality ? $"Set viewport quality to {quality}" : null
            }.Where(part => part is not null)));
        yield return act("edit_placement", "Move, rotate or scale a placed object (static meshes, markers, lights, volumes whose " +
            "placement decodes). Saved as a project object edit (revert_object_edit undoes it). Omitted values keep the current ones. " +
            "See skill solforge-map-bounds.",
            Vectors(McpJson.Schema(("id", "string", "Object id", true), ("position", "array", "[x, y, z]", false),
                ("rotation", "array", "[x, y, z] degrees, as get_placement gives", false), ("scale", "number", "Uniform scale", false))),
            args => Need(args, "id", "Give an object id from find_world_objects."),
            args => $"Edit placement of {Arg(args, "id")}: " + string.Join(", ", new[] { "position", "rotation", "scale" }
                .Where(key => args[key] is not null).Select(key => $"{key} {args[key]!.ToJsonString()}")));
        yield return act("edit_bounds", "Change a trigger volume's bounds (minimum and maximum corners). Bounds only grow to " +
            "contain the authored hull, so this extends areas; see skill solforge-map-bounds.",
            Vectors(McpJson.Schema(("id", "string", "Object id", true), ("minimum", "array", "[x, y, z]", true), ("maximum", "array", "[x, y, z]", true))),
            args => Need(args, "id", "Give an object id.") ?? (args["minimum"] is JsonArray { Count: 3 } && args["maximum"] is JsonArray { Count: 3 }
                ? null : "Give minimum and maximum as [x, y, z]."),
            args => $"Set bounds of {Arg(args, "id")} to {args["minimum"]?.ToJsonString()} .. {args["maximum"]?.ToJsonString()}");
        yield return act("revert_object_edit", "Undo a saved object edit by its key (from list_project_changes).",
            McpJson.Schema(("key", "string", "Object edit key", true)), args => Need(args, "key", "Give an object edit key."),
            args => $"Revert object edit {Arg(args, "key")}");
        yield return act("apply_item_edit", "Edit the item open in Solforge (open it with open_asset first): op set_field (field, " +
            "value), add_stat (stat, value), remove_stat (stat), add_perk (perk), remove_perk (perk), set_socket_plug (socket, plug: makes " +
            "the plug the socket's only and initial choice, e.g. a cloned plug from Mods and plugs). Saved to the project with undo. " +
            "See skill solforge-new-weapon.",
            McpJson.Schema(("item", "string", "Item tag (must be the open document)", true), ("op", "string", "set_field, add_stat, remove_stat, add_perk, remove_perk or set_socket_plug", true),
                ("field", "string", "Field name from get_item (set_field)", false), ("value", "string", "New value (set_field, add_stat)", false),
                ("stat", "string", "Stat index or name", false), ("perk", "string", "Perk index or name", false),
                ("socket", "integer", "Socket index from get_item (set_socket_plug)", false), ("plug", "string", "Plug item index, tag or exact name (set_socket_plug)", false)),
            args => Need(args, "item", "Give the item tag.") ?? (Arg(args, "op") is "set_field" or "add_stat" or "remove_stat" or "add_perk" or "remove_perk" or "set_socket_plug"
                ? null : "op must be set_field, add_stat, remove_stat, add_perk, remove_perk or set_socket_plug."),
            args => $"Item {Arg(args, "item")}: {Arg(args, "op")} " + string.Join(" ", new[] { "field", "stat", "perk", "value", "socket", "plug" }
                .Where(key => args[key] is not null).Select(key => $"{key}={Arg(args, key)}")));
        yield return act("apply_vendor_edit", "Edit the vendor open in Solforge (open_asset first): op duplicate_sale (sale), " +
            "remove_sale (sale), set_sale_item (sale, item), edit_cost (sale, cost, item, quantity). Saved with undo.",
            McpJson.Schema(("vendor", "string", "Vendor tag (must be the open document)", true), ("op", "string", "duplicate_sale, remove_sale, set_sale_item or edit_cost", true),
                ("sale", "integer", "Sale index from get_vendor", true), ("cost", "integer", "Cost row index (edit_cost)", false),
                ("item", "string", "Item index, tag or exact name (set_sale_item, edit_cost)", false), ("quantity", "integer", "Cost quantity (edit_cost)", false)),
            args => Need(args, "vendor", "Give the vendor tag.") ?? (Arg(args, "op") is "duplicate_sale" or "remove_sale" or "set_sale_item" or "edit_cost"
                ? null : "op must be duplicate_sale, remove_sale, set_sale_item or edit_cost."),
            args => $"Vendor {Arg(args, "vendor")}: {Arg(args, "op")} sale {Arg(args, "sale")}" + string.Join("", new[] { "cost", "item", "quantity" }
                .Where(key => args[key] is not null).Select(key => $" {key}={Arg(args, key)}")));
        yield return act("start_creation", "Open Solforge's New or Clone dialog for a type (list_creation_types) with fields " +
            "prefilled by label (describe_creation). The user reviews and presses Create; Solforge assigns new hashes. " +
            "See skills solforge-new-weapon, solforge-new-armor.",
            McpJson.Schema(("key", "string", "Type key", true), ("mode", "string", "new or clone", true),
                ("source", "string", "Clone source tag (0x...)", false), ("fields", "object", "Field label to value, e.g. {\"Name\": \"Sol Fang\"}", false)),
            args => Need(args, "key", "Give a type key from list_creation_types.") ?? (Arg(args, "mode") is "new" or "clone" ? null : "mode must be new or clone."),
            args => $"Open {Arg(args, "mode")} {Arg(args, "key")}" + (McpJson.String(args, "source") is { Length: > 0 } source ? $" from {source}" : "")
                + (args["fields"] is JsonObject { Count: > 0 } fields ? $" with {fields.Count} field(s) prefilled" : ""));
        yield return act("replace_texture_pixels", "Replace a texture's pixels from a PNG (same size; converted to the texture's own " +
            "format) or a DDS file on disk, as Import as delta does. The texture must be open (open_asset); saved to the project with undo. " +
            "Startup (_unp1_) packages such as w64_ui_bootflow_unp1_0 cannot be installed as package replacements.",
            McpJson.Schema(("tag", "string", "Texture tag (must be the open document)", true), ("path", "string", "Absolute path of a .png or .dds file", true)),
            args => ParseHash(McpJson.String(args, "tag")) is null ? "Give the texture tag."
                : McpJson.String(args, "path") is { } file && Path.IsPathFullyQualified(file) && File.Exists(file)
                    && Path.GetExtension(file).ToLowerInvariant() is ".png" or ".dds" ? null : "Give an absolute path to an existing .png or .dds file.",
            args => $"Replace pixels of texture {Arg(args, "tag")} with {Path.GetFileName(Arg(args, "path"))}");
        yield return act("build_project", "Build the project's packages; install=true also installs the mod (close the game " +
            "first; needs mod details, see set_mod_details). Poll get_build_status for the result.",
            McpJson.Schema(("install", "boolean", "Also install the mod (default false)", false)), _ => null,
            args => McpJson.Bool(args, "install", false) ? "Build and install the mod" : "Build the project packages");
        foreach (var tool in SelfServiceTools(act)) yield return tool;
    }
}
