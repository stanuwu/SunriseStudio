/**
 * File: Entry point of the MCP tool list. SolforgeTools.Create builds every tool from the per-area partial files:
 * Project (editor/project/items), Content (content families), Data (decomp + tag data), Graphs, Media
 * (havok/audio/video), Assets (shaders/animations/audio), Ui (UI control driving, viewport frames, OCR), Player
 * (position tracking); LocalServer/LocalServerTools.cs is registered separately. Tool names are public API for AI
 * clients.
 */
using System.Globalization;
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/** An object on screen with its screen rectangle in viewport pixels. */
public sealed record VisibleObject(string Id, string Label, string Kind, float X, float Y, float Width, float Height);

/**
 * What the Solforge editor gives the tools. Snapshot members are read on MCP threads and must be safe
 * there; the async members marshal to the UI thread themselves.
 */
public partial interface ISolforgeHost
{
    /** The privacy toggle: while paused, nothing about the scene is published. */
    bool Paused { get; }
    string? ProjectRoot { get; }
    IReadOnlyList<string> SdkRoots { get; }
    string? CurrentMap { get; }
    AttentionLog Attention { get; }
    /** The latest get_context snapshot, as JSON. */
    string ContextJson { get; }
    IReadOnlyList<VisibleObject> Visible { get; }
    Task<IReadOnlyList<ObjectEntry>> ObjectsAsync(string query, CancellationToken cancel);
    Task<JsonObject?> GetObjectAsync(string id, CancellationToken cancel);
    /** Everything Solforge has open (get_open_documents), read on the UI thread. */
    Task<JsonObject> GetOpenDocumentsAsync(CancellationToken cancel);
    Task<byte[]?> ScreenshotAsync(string? objectId, bool labels, CancellationToken cancel);
    Task<JsonObject?> GetTagAsync(uint hash, CancellationToken cancel);
    Task<JsonObject?> GetPackageEntryAsync(string id, CancellationToken cancel);
    JsonObject LiveState();
    /** The open mission's slots, built off the UI thread and cached per loaded world; null with no map or mission open. */
    Task<MissionSnapshot?> MissionAsync(CancellationToken cancel);
    /** The server mission scripts folder (Settings > Paths), read by read_project_file as "scripts:<path>"; null when unset. */
    string? ServerScriptsRoot => null;
    /** Lua folders that name mission slots: SDK mission files, server mission scripts, the project. */
    IReadOnlyList<CodeRoot> CodeRoots { get; }
    /** The reference library folder (08-docs\reference), found at call time; null until it exists. */
    string? ReferenceFolder { get; }
    /** One page of a panel the user sees (see Panels); heavy reads run off the UI thread. */
    Task<JsonObject> ReadPanelAsync(string panel, PanelQuery query, CancellationToken cancel);
    /** Shows the request in the AI workspace; true once the user clicks Approve. */
    Task<bool> ApproveAsync(string tool, string summary, CancellationToken cancel);
    /** Runs an approved action tool on the UI thread and says what happened. */
    Task<string> RunActionAsync(string tool, JsonObject args, CancellationToken cancel);
    /** A project-wide read (SolforgeTools.CatalogTools and WorldTools); an "error" member makes it a tool error. */
    Task<JsonObject> QueryAsync(string tool, JsonObject args, CancellationToken cancel);
    /** Every control Solforge drew in its last frame (list_ui_controls); the first call after a quiet minute waits a frame or two. */
    Task<IReadOnlyList<UiControl>> UiControlsAsync(CancellationToken cancel);
    /** Applies one approved ui_* command on the UI thread; Ok is false with the reason when it could not. */
    Task<(bool Ok, string Message)> UiCommandAsync(UiCommand command, CancellationToken cancel);
    /** The live viewport frames, captured continuously while anyone watches. */
    ViewportFeed Viewport { get; }
    /** The /automatic watchdog (AutomaticWatchdog.cs) the automatic_* and goal_* tools steer; null when this host has none. */
    AutomaticWatchdog? Automatic => null;
}

/** A page of a panel: path picks a node or section, filter narrows rows. */
public sealed record PanelQuery(string? Path, string? Filter, int Offset, int Limit);

/** The MCP tool catalogue for the editor (partial: one file per tool area, see the file header). */
public static partial class SolforgeTools
{
    public const string Instructions =
        "Solforge is the Project Sol game editor. The user's viewport labels (for example " +
        "'Botza District / Static mesh 7489 - Last city crater [182]') are exact names. When the user says " +
        "'this', 'that one', 'here' or 'the one I just clicked', call get_context or get_attention_log first, and " +
        "get_open_documents for the open map, asset, inspector values, mission script, tag diff, project changes and build " +
        "status, before searching files. Resolve spoken names with find_object, then get_object for details. Mission objects " +
        "('this event point', 'that generator') have slot ids (slot/...): get_mission_object gives their transform, prefab, " +
        "children and the SDK and server Lua lines naming them. read_panel reads any panel (hierarchy, inspector, assets, " +
        "mission graph, report, changes, live) a page at a time. No map needs to be open for get_editor_state, list_maps, " +
        "list_missions, list_packages, search_assets, search_items, get_item, list_vendors or list_creation_types; find map " +
        "content by name with list_maps (zones) and search_assets, open it with open_map, then find_world_objects (layer volumes " +
        "or collision) and get_placement before edit_placement or edit_bounds. Playbooks for common jobs are skills named " +
        "solforge-new-weapon, solforge-new-armor, solforge-map-bounds, solforge-patrol-content, solforge-new-enemy and " +
        "solforge-new-mission; read the matching one first. Solforge's own UI: list_ui_controls, then ui_invoke, ui_set_value, " +
        "ui_set_checked or ui_select. The live view: get_viewport_frame, get_viewport_frames, wait_for_viewport_change (each with " +
        "text for models without vision; ocr: true or read_screen_text reads the words on screen). Where the player is in game: get_player_position (map, zone, xyz, yaw, nearest " +
        "objects and slots) and wait_for_player_near. Shader, animation clip and audio metadata (no map needed): list_shaders, " +
        "get_shader, list_animations, get_animation, list_audio and get_audio. Action tools need the user's approve click. " +
        "For how the game client works (functions, xrefs, strings, structs, vtables, tag class handlers in destiny2.exe) and for raw " +
        "tag data, unknown tag classes, localized text and JSON/CSV export, start with decomp_status and read the solforge-decomp skill; " +
        "paged results return next_cursor, which you pass back as cursor. " + ParhelionHint + " " + RetailHint + " " + SelfServiceHint + " " + AutomaticHint + " " + CommandsHint;

    /** Parhelion's recipe path remains visible to MCP clients without an embedded AI interface. */
    public const string ParhelionHint =
        "For Parhelion-compatible weapon recipe work, call get_parhelion_context, list_parhelion_weapon_recipes and " +
        "validate_parhelion_weapon_recipe; read skills/solforge-new-weapon/SKILL.md before authoring. To load a validated " +
        "recipe into Solforge's normal clone dialog, call start_parhelion_weapon_recipe; the user reviews and presses Create. " +
        "For subclass requests, call get_parhelion_subclass_context and read skills/solforge-new-subclass/SKILL.md first. Parhelion's desktop workbench authors subclasses, " +
        "but this MCP has no Parhelion subclass recipe API. Solforge's separate server subclass controls are save_subclass_definition, " +
        "update_content, validate_content, check_subclass_build and export_content; don't describe those documents as Parhelion recipes.";

    /** Shared by the server instructions and the session file, so every engine knows it can do its own setup. */
    public const string SelfServiceHint =
        "Do Solforge setup yourself instead of asking the user to click: set_settings (Paths such as serverScripts and blender, " +
        "the game installation, rendering, memory, FPS caps; get_settings reads the same names), set_mod_details (Build and install " +
        "needs mod details; get_project shows them), open_project, create_project, close_project, close_map, close_asset, " +
        "build_project, build_to_folder, update_running_game and reload_mission_scripts. Server mission scripts read as " +
        "scripts:<path> with read_project_file. Only Settings > AI stays the user's.";


    /** read_panel names; each pages with offset/limit. */
    public static readonly string[] Panels =
        ["hierarchy", "inspector", "selection", "assets", "mission", "mission_graph", "report", "changes", "live", "attention"];

    /** Builds the full tool list for a host; each area method yields its tools and Read() wraps a read-only tool. */
    public static IReadOnlyList<McpTool> Create(ISolforgeHost host)
    {
        McpTool Read(string name, string description, JsonObject schema, Func<JsonObject, CancellationToken, Task<McpToolResult>> run) =>
            new(name, description, schema, false, async (args, cancel) =>
                host.Paused && name is not ("read_project_file" or "search_project" or "search_reference" or "read_reference") && !CatalogTools.Contains(name) && !ContentReadTools.Contains(name) && !AssetReadTools.Contains(name) && !ItemSourceReadTools.Contains(name) && !RetailReadTools.Contains(name)
                    ? McpToolResult.Error("The user paused context publishing in Solforge's AI workspace.")
                    : await run(args, cancel).ConfigureAwait(false));
        McpTool Act(string name, string description, JsonObject schema, Func<JsonObject, string?> validate, Func<JsonObject, string> summary) =>
            new(name, description, schema, true, async (args, cancel) =>
            {
                if (validate(args) is { } problem) return McpToolResult.Error(problem);
                if (!await host.ApproveAsync(name, summary(args), cancel).ConfigureAwait(false))
                    return McpToolResult.Error("The user declined this in Solforge.");
                return McpToolResult.Text(await host.RunActionAsync(name, args, cancel).ConfigureAwait(false));
            });
        /** Shorthand for a JSON schema from (name, type, description, required) tuples. */
        static JsonObject Schema(params (string, string, string, bool)[] properties) => McpJson.Schema(properties);
        string? NeedId(JsonObject args) => string.IsNullOrWhiteSpace(McpJson.String(args, "id")) ? "Give an object id from find_object." : null;

        return
        [
            Read("get_context", "What the user is looking at now: hovered and selected objects (with slot id, slot name, type, " +
                "position and owning prefab for mission objects), camera position and zone, open editor and file, active map and " +
                "mission, pinned objects, a short list of open documents, and graph_kind: the model the Graph panel shows " +
                "(mission_script, node_graph:material, node_graph:weapon, node_graph:mission, or null).", Schema(),
                (_, _) => Task.FromResult(McpToolResult.Text(host.ContextJson))),
            Read("get_open_documents", "Everything Solforge has open right now: map and mission, the open asset (tag, type, package, " +
                "name) with its inspector values, the selected objects' property values, the open mission script, the open tag " +
                "diff, the project change list and the build and notice status; graph_kind and graph (path, unsaved, problems, " +
                "which tools edit it) for the Graph panel's active model.", Schema(),
                async (_, cancel) => McpToolResult.Json(await host.GetOpenDocumentsAsync(cancel).ConfigureAwait(false))),
            Read("find_object", "Fuzzy-resolve a spoken name ('static mesh 7489', 'last city crater', '[182]', 'o_generator', " +
                "'pf_event_multipoint', a tag, prefab or registry hash, a slot id) against viewport labels, the Object tree, the open " +
                "mission's slots and the asset index of the open map. Returns ranked matches; slot rows say next: get_mission_object.",
                Schema(("query", "string", "The name as the user said it", true), ("limit", "integer", "Most matches to return (default 10)", false)),
                async (args, cancel) =>
                {
                    string query = McpJson.String(args, "query") ?? "";
                    var entries = await host.ObjectsAsync(query, cancel).ConfigureAwait(false);
                    var mission = await host.MissionAsync(cancel).ConfigureAwait(false);
                    var visible = host.Visible.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
                    var all = entries.Select(entry => entry.Scope == ObjectScope.Tree && visible.Contains(entry.Id) ? entry with { Scope = ObjectScope.Viewport } : entry)
                        .Concat(mission?.Entries(visible) ?? Enumerable.Empty<ObjectEntry>());
                    var matches = ObjectIndex.Find(all, query, host.CurrentMap, host.Attention.Recency(), Math.Clamp(McpJson.Int(args, "limit", 10), 1, 50));
                    return McpToolResult.Json(new JsonObject { ["query"] = query, ["map"] = host.CurrentMap, ["matches"] = ObjectIndex.ToJson(matches) });
                }),
            Read("get_object", "Full details for an object id: label, type, tag hash, package, transform, bounds, mesh/material/texture " +
                "references, parent and children, slot facts, script references. Slot ids (slot/...) return the mission slot.",
                Schema(("id", "string", "Object id from find_object or get_context", true)),
                async (args, cancel) =>
                {
                    string id = McpJson.String(args, "id") ?? "";
                    if (id.StartsWith("slot/", StringComparison.OrdinalIgnoreCase)
                        && await host.MissionAsync(cancel).ConfigureAwait(false) is { } mission && mission.Find(id) is { } slot)
                        return McpToolResult.Json(MissionSlots.Describe(mission, slot, 0, 50));
                    return await host.GetObjectAsync(id, cancel).ConfigureAwait(false) is { } found
                        ? McpToolResult.Json(found) : McpToolResult.Error("No object with that id in the open map.");
                }),
            Read("get_viewport_screenshot", "The current viewport as a PNG; with id, cropped to that object's screen rectangle.",
                Schema(("id", "string", "Optional object id to crop to", false), ("labels", "boolean", "Draw object labels (default true)", false)),
                async (args, cancel) =>
                {
                    var png = await host.ScreenshotAsync(McpJson.String(args, "id"), McpJson.Bool(args, "labels", true), cancel).ConfigureAwait(false);
                    if (png is null) return McpToolResult.Error("No viewport is showing (3D rendering may be off: get_editor_state, " +
                        "set_viewport_rendering), or the object is off screen.");
                    var result = McpToolResult.Text($"Viewport screenshot, {png.Length} bytes.");
                    result.Images.Add(png);
                    return result;
                }),
            Read("list_visible", "Everything on screen with labels and screen rectangles (viewport pixels).",
                Schema(("filter", "string", "Optional text the label or kind must contain", false), ("limit", "integer", "Most rows (default 200)", false)),
                (args, _) =>
                {
                    string? filter = McpJson.String(args, "filter");
                    var rows = new JsonArray();
                    foreach (var item in host.Visible.Where(item => string.IsNullOrEmpty(filter)
                        || item.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) || item.Kind.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        .Take(Math.Clamp(McpJson.Int(args, "limit", 200), 1, 2000)))
                        rows.Add(new JsonObject { ["id"] = item.Id, ["label"] = item.Label, ["kind"] = item.Kind,
                            ["rect"] = new JsonArray(Math.Round(item.X), Math.Round(item.Y), Math.Round(item.Width), Math.Round(item.Height)) });
                    return Task.FromResult(McpToolResult.Json(new JsonObject { ["count"] = rows.Count, ["objects"] = rows }));
                }),
            Read("get_attention_log", "The last n hovered, selected, focused, pinned or opened items with timestamps, newest first.",
                Schema(("n", "integer", "How many (default 20)", false)),
                (args, _) => Task.FromResult(McpToolResult.Json(host.Attention.ToJson(Math.Clamp(McpJson.Int(args, "n", 20), 1, 256))))),
            Read("read_project_file", "Read a project file (path relative to the project folder), a Lua SDK doc ('sdk:<path>') or a " +
                "server mission script ('scripts:<path>', as list_mission_scripts gives it).",
                Schema(("path", "string", "Project-relative path, sdk:<path> or scripts:<path>", true), ("offset", "integer", "First line (default 1)", false),
                    ("limit", "integer", "Line count (default 400)", false)),
                (args, _) =>
                {
                    string shown = McpJson.String(args, "path") ?? "";
                    if (ProjectFiles.Resolve(host.ProjectRoot, host.SdkRoots, shown, host.ServerScriptsRoot) is not { } path)
                        return Task.FromResult(McpToolResult.Error("That path is outside the project, the SDK docs and the server scripts folder."));
                    return Task.FromResult(McpToolResult.Json(ProjectFiles.Read(path, shown, McpJson.Int(args, "offset", 1), McpJson.Int(args, "limit", 400))));
                }),
            Read("search_project", "Case-insensitive text search over project files and the Lua SDK docs.",
                Schema(("query", "string", "Text to find", true), ("extension", "string", "Optional extension filter, e.g. .lua", false),
                    ("limit", "integer", "Most hits (default 100)", false)),
                (args, _) => Task.FromResult(McpToolResult.Json(ProjectFiles.Search(host.ProjectRoot, host.SdkRoots,
                    McpJson.String(args, "query") ?? "", McpJson.String(args, "extension"), Math.Clamp(McpJson.Int(args, "limit", 100), 1, 1000))))),
            Read("search_reference", "Search the reference library (saved docs and repo pages, 08-docs\\reference): ranked hits " +
                "with title, url, library path, tags and a snippet. Read a hit with read_reference.",
                Schema(("query", "string", "Words to find", true), ("tag", "string", "Optional tag the page must have", false),
                    ("limit", "integer", "Most hits (default 10, max 100)", false)),
                (args, cancel) => Task.Run(() => host.ReferenceFolder is not { } folder
                    ? McpToolResult.Error(NoReference)
                    : McpToolResult.Json(ReferenceLibrary.Search(folder, McpJson.String(args, "query") ?? "", McpJson.String(args, "tag"),
                        McpJson.Int(args, "limit", 10))), cancel)),
            Read("read_reference", "Read a reference library page by its library path (from search_reference) or its url, a slice of " +
                "lines at a time (next gives the following offset).",
                Schema(("path", "string", "Library path, e.g. 'unreal/networking.md'", false), ("url", "string", "Or the page's url", false),
                    ("offset", "integer", "First line (default 1)", false), ("limit", "integer", "Line count (default 300, max 2000)", false)),
                (args, cancel) => Task.Run(() =>
                {
                    if (host.ReferenceFolder is not { } folder) return McpToolResult.Error(NoReference);
                    string? path = McpJson.String(args, "path"), url = McpJson.String(args, "url");
                    if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(url)) return McpToolResult.Error("Give a path or a url.");
                    return ReferenceLibrary.Read(folder, path, url, McpJson.Int(args, "offset", 1), McpJson.Int(args, "limit", 300)) is { } page
                        ? McpToolResult.Json(page) : McpToolResult.Error("No such page in the reference library (it may not be saved yet).");
                }, cancel)),
            Read("get_tag", "Raw read-only inspection of a tag by hash (hex like 0x80A1B2C3 or decimal).",
                Schema(("hash", "string", "Tag hash", true)),
                async (args, cancel) => ParseHash(McpJson.String(args, "hash")) is not { } hash ? McpToolResult.Error("Give a tag hash such as 0x80A1B2C3.")
                    : await host.GetTagAsync(hash, cancel).ConfigureAwait(false) is { } tag ? McpToolResult.Json(WithSchemaFields(host, hash, tag)) : McpToolResult.Error("No such tag in the loaded packages.")),
            Read("get_package_entry", "Read-only package entry by tag hash or 'package/index'.",
                Schema(("id", "string", "Tag hash, or package name and entry index as 'name/index'", true)),
                async (args, cancel) => await host.GetPackageEntryAsync(McpJson.String(args, "id") ?? "", cancel).ConfigureAwait(false) is { } entry
                    ? McpToolResult.Json(entry) : McpToolResult.Error("No such package entry.")),
            Read("get_live_state", "When the in-game Live link is attached: player position and nearby in-game objects.", Schema(),
                (_, _) => Task.FromResult(McpToolResult.Json(host.LiveState()))),
            Read("list_mission_objects", "The open mission's placed objects (prefab slots), a page at a time: slot id, name, type, " +
                "registry, owning prefab, placing object id, position and zone; squad slots with no squad data (squadData false: cannot be " +
                "placed), each cutscene's state, dialogue cue counts and first line, and trigger volume overlaps. Every filter word must appear in the name, slot id, " +
                "SDK constant, kind, label, prefab name or hash.",
                Schema(("filter", "string", "Optional words, e.g. 'pf_event_multipoint generator'", false),
                    ("placed", "boolean", "Only slots the map places (with a position); default false", false),
                    ("triggerMap", "boolean", "Also list trigger volumes that overlap a spawn point or each other (default false)", false),
                    ("offset", "integer", "First row (default 0)", false), ("limit", "integer", "Rows per page (default 100, max 500)", false)),
                async (args, cancel) => await host.MissionAsync(cancel).ConfigureAwait(false) is not { } mission
                    ? McpToolResult.Error(NoMission)
                    : McpToolResult.Json(await Task.Run(() => ListWithInsight(host, mission, args), cancel).ConfigureAwait(false))),
            Read("get_mission_object", "One mission object by slot id, placing object id, slot name or SDK constant: type, transform, " +
                "registry, parent prefab, parent slot, a page of children, squad data, cutscene state (tag bytes 0x0C-0x0F), every dialogue " +
                "line's text, trigger volume bounds and overlaps (spawn points, other volumes), and code refs, the lines naming it in the SDK mission Lua " +
                "(this mission's file) and the server mission scripts, with full paths and line numbers.",
                Schema(("id", "string", "slot/... id, object id, slot name or constant (PF_..._O_GENERATOR[_80BE5D9B])", true),
                    ("code", "boolean", "Search the Lua for code refs (default true)", false),
                    ("everywhere", "boolean", "Read every SDK mission file, not just this mission's (slower; default false)", false),
                    ("codeLimit", "integer", "Most code lines (default 60, max 500)", false),
                    ("childOffset", "integer", "First child row (default 0)", false), ("childLimit", "integer", "Children per page (default 50)", false)),
                async (args, cancel) =>
                {
                    if (await host.MissionAsync(cancel).ConfigureAwait(false) is not { } mission) return McpToolResult.Error(NoMission);
                    if (mission.Find(McpJson.String(args, "id") ?? "") is not { } slot)
                        return McpToolResult.Error("No slot with that id, name or constant in the open mission; try find_object or list_mission_objects.");
                    var details = MissionSlots.Describe(mission, slot, McpJson.Int(args, "childOffset", 0), McpJson.Int(args, "childLimit", 50));
                    await Task.Run(() => DescribeWithInsight(host, mission, slot, details), cancel).ConfigureAwait(false);
                    if (McpJson.Bool(args, "code", true))
                    {
                        bool everywhere = McpJson.Bool(args, "everywhere", false);
                        var roots = host.CodeRoots;
                        details["code"] = await Task.Run(() => CodeRefs.Search(roots, CodeRefs.Needles(slot), mission.MissionTag, everywhere,
                            McpJson.Int(args, "codeLimit", 60), TimeSpan.FromSeconds(everywhere ? 60 : 15), cancel), cancel).ConfigureAwait(false);
                    }
                    return McpToolResult.Json(details);
                }),
            Read("read_panel", "Read any Solforge panel a page at a time, off the UI thread: " + string.Join(", ", Panels) + ". " +
                "hierarchy: Object tree nodes (path = node id lists its children; filter searches names). inspector: the open asset's " +
                "inspector rows and references. selection: every property of the selected objects. assets: the asset index. mission: " +
                "mission slots. mission_graph: nodes, edges and diagnostics of the open mission script. report: the world report " +
                "(counts, load, names and shader diagnostics). changes: project changes. live: Live link state. attention: the attention log.",
                Schema(("panel", "string", "One of: " + string.Join(", ", Panels), true), ("path", "string", "Optional node id or section", false),
                    ("filter", "string", "Optional text rows must contain", false), ("offset", "integer", "First row (default 0)", false),
                    ("limit", "integer", "Rows per page (default 100, max 500)", false)),
                async (args, cancel) =>
                {
                    string panel = (McpJson.String(args, "panel") ?? "").Trim().ToLowerInvariant().Replace(' ', '_');
                    if (!Panels.Contains(panel)) return McpToolResult.Error("Unknown panel; use one of: " + string.Join(", ", Panels));
                    var query = new PanelQuery(McpJson.String(args, "path"), McpJson.String(args, "filter"),
                        Math.Max(0, McpJson.Int(args, "offset", 0)), Math.Clamp(McpJson.Int(args, "limit", 100), 1, Pages.MaximumLimit));
                    if (panel == "attention")
                        return McpToolResult.Json(Pages.Page(host.Attention.ToJson(256).OfType<JsonNode>().ToArray()
                            .Where(row => query.Filter is not { Length: > 0 } filter || row.ToJsonString().Contains(filter, StringComparison.OrdinalIgnoreCase)),
                            query.Offset, query.Limit, row => row.DeepClone()));
                    if (panel == "mission")
                        return await host.MissionAsync(cancel).ConfigureAwait(false) is { } mission
                            ? McpToolResult.Json(MissionSlots.List(mission, query.Filter, false, query.Offset, query.Limit)) : McpToolResult.Error(NoMission);
                    var result = await host.ReadPanelAsync(panel, query, cancel).ConfigureAwait(false);
                    result["panel"] = panel;
                    return McpToolResult.Json(result);
                }),

            Act("select_object", "Select an object in Solforge.", Schema(("id", "string", "Object id", true)), NeedId,
                args => $"Select {McpJson.String(args, "id")}"),
            Act("focus_camera", "Select an object and frame the viewport camera on it.", Schema(("id", "string", "Object id", true)), NeedId,
                args => $"Focus the camera on {McpJson.String(args, "id")}"),
            Act("highlight", "Outline objects in the viewport for a few seconds so you can point things out.",
                new JsonObject
                {
                    ["type"] = "object", ["required"] = new JsonArray("ids"),
                    ["properties"] = new JsonObject
                    {
                        ["ids"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Object ids" },
                        ["seconds"] = new JsonObject { ["type"] = "number", ["description"] = "How long (default 8)" }
                    }
                },
                args => args["ids"] is JsonArray { Count: > 0 } ? null : "Give ids to highlight.",
                args => $"Highlight {(args["ids"] as JsonArray)?.Count ?? 0} object(s)"),
            Act("open_file", "Open a project file in Solforge (Lua scripts and *.solgraph.json graphs open in the Graph panel).",
                Schema(("path", "string", "Project-relative path", true), ("line", "integer", "Optional line", false)),
                args => ProjectFiles.Resolve(host.ProjectRoot, host.SdkRoots, McpJson.String(args, "path") ?? "") is { } path && File.Exists(path)
                    ? null : "No such project file.",
                args => $"Open {McpJson.String(args, "path")}"),
            Act("apply_project_edit", "Write a project file (project overlay only, never game packages). Either 'content' replaces the " +
                "file, or 'find'/'replace' edits one exact occurrence. Shows in Project changes and can be undone.",
                Schema(("path", "string", "Project-relative path", true), ("content", "string", "New full content", false),
                    ("find", "string", "Exact text to replace (must occur once)", false), ("replace", "string", "Replacement text", false)),
                args =>
                {
                    if (ProjectFiles.EditablePath(host.ProjectRoot, McpJson.String(args, "path") ?? "", out string? reason) is null) return reason;
                    bool whole = args["content"] is not null, patch = args["find"] is not null;
                    return whole == patch ? "Give either content, or find and replace." : null;
                },
                args => args["content"] is not null ? $"Write {McpJson.String(args, "path")} ({McpJson.String(args, "content")?.Length ?? 0} chars)"
                    : $"Edit {McpJson.String(args, "path")}: replace {Quote(McpJson.String(args, "find"))} with {Quote(McpJson.String(args, "replace"))}"),
            .. ProjectTools(host, Read, Act),
            .. DataTools(host),
            .. SchemaTools(host),
            .. MediaTools(host),
            .. ContentTools(host, Read),
            .. UiTools(host, Read),
            .. PlayerTools(host, Read),
            .. GraphTools(host),
            .. AssetTools(host, Read),
            .. AutomaticModeTools(host),
            .. CommandTools(host),
            .. InsightTools(host, Read),
            .. MissionHostTools(host, Read),
            .. ItemSourceTools(host, Read, Act),
            .. ParhelionTools(host),
            .. RetailTools(host, Read, Act)
        ];
    }

    private const string NoReference = "No reference library found (08-docs\\reference above the project or game folder, or SOLFORGE_REFERENCE).";
    private const string NoMission = "No mission is open in Solforge. list_missions lists the open map's missions; open_map with " +
        "mission=<tag> opens one (its map is found for you).";

    /** Parses a tag hash given as 0x hex or decimal; null when it is neither. */
    public static uint? ParseHash(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(text[2..], NumberStyles.HexNumber, null, out uint hex) ? hex : null;
        if (uint.TryParse(text, NumberStyles.None, null, out uint number) && text.Length < 8) return number;
        return uint.TryParse(text, NumberStyles.HexNumber, null, out uint bare) ? bare : null;
    }

    /** Quotes text for error messages, truncating long values to 60 characters. */
    private static string Quote(string? text) => text is null ? "\"\"" : "\"" + (text.Length > 60 ? text[..60] + "..." : text) + "\"";
}
