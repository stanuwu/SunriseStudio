/**
 * File: Which MCP tools reach each part of Solforge's UI (08-docs\McpCoverage.md in short). get_help returns it as its
 * "tools" section so the AI can look up the tool for a menu, panel or dialog; every menu command is also in EditorCommands.
 */
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/** One row: the UI area, the feature the user sees, and the tools that do it. */
public sealed record ToolMapRow(string Area, string Feature, string Tools);

public static class ToolMap
{
    public static readonly ToolMapRow[] Rows =
    [
        new("Any", "Any control drawn on screen (buttons, menus, checkboxes, tabs, text, combos, sliders, headers, list rows)",
            "list_ui_controls, ui_invoke, ui_set_value, ui_set_checked, ui_select; show_panel first for closed panels"),
        new("Any", "Native file or folder pickers", "run_command/export_asset/import_asset path, or arm_file_dialog then ui_invoke"),
        new("Title bar", "File, Edit, Assets, Build, View, Window, Help menus, Back/Forward", "list_commands, run_command"),
        new("Title bar", "Toolbar: 3D rendering, Game shaders, Quality, Full detail, Lighting", "set_viewport_options, set_viewport_rendering"),
        new("Viewport", "Watch the live viewport in a local browser (this PC only)", "get_viewport_stream"),
        new("Project", "New / open / close project, recent projects, mod details", "create_project, open_project, close_project, set_mod_details, get_project, run_command file.open_recent"),
        new("Project", "Save, undo, redo", "run_command file.save, edit.undo, edit.redo"),
        new("Project", "Project changes panel: list, revert, remove, compare with base, export added", "list_project_changes, project_change, revert_object_edit, read_panel changes"),
        new("Project", "Project files and scripts", "list_project_files, read_project_file, search_project, apply_project_edit, open_file"),
        new("Build", "Build packages, Build and install, Update running game, Build to folder", "build_project, update_running_game, build_to_folder, get_build_status"),
        new("Build", "Pack .solmod, Restore install snapshot", "run_command build.pack_solmod, build.restore_snapshot"),
        new("Maps", "Open map/mission, close world, map and mission lists", "list_maps, list_missions, open_map, close_map"),
        new("Viewport", "Select, frame, highlight, screenshots, live frames", "select_object, focus_camera, highlight, get_viewport_screenshot, get_viewport_frame(s), wait_for_viewport_change"),
        new("Viewport", "Camera position, orbit, zoom, view presets, projection", "set_camera, set_viewport_options, focus_camera"),
        new("Viewport", "Wireframe, grid, axis gizmo, statistics overlay", "set_viewport_options"),
        new("Viewport", "Move objects, bounds, kill volumes, collision", "find_world_objects, get_placement, edit_placement, edit_bounds, export_collision_obj"),
        new("Object tree / Hierarchy", "Browse, find, select objects", "read_panel hierarchy, find_object, get_object, list_visible, select_object"),
        new("Inspector", "Read values; edit fields", "get_open_documents, read_panel inspector; ui_set_value for fields without a dedicated tool"),
        new("Asset browser", "Search, open, open as asset, clone, copy tag", "search_assets, open_asset, run_command assets.open_as_asset, start_creation, list_creation_types"),
        new("Assets menu", "Export asset / Blender asset / world / material / animation / collision", "export_asset"),
        new("Assets menu", "Import Blender asset, skinned model, animation controls, collision, delta", "import_asset"),
        new("Assets menu", "Add asset dialog, Edit perk behavior, Resolve asset names", "run_command assets.add_asset, assets.edit_perk_behavior, assets.resolve_names"),
        new("Textures", "Edit texture pixels", "replace_texture_pixels"),
        new("Items", "Item browser, weapons, armor, perks, vendors", "search_items, get_item, search_perks, apply_item_edit, list_vendors, get_vendor, apply_vendor_edit"),
        new("Creation", "New weapon, armor, texture, prop, material, exotic, perk, vendor workflows", "list_creation_types, describe_creation, start_creation"),
        new("Graph panel", "Mission scripts: open, edit, compile, apply, reload", "list_mission_scripts, create_mission_script, open_file, get_graph, apply_graph, reload_mission_scripts"),
        new("Graph panel", "Node graphs (material, weapon, mission): new, edit, compile, apply", "list_graphs, create_graph, get_graph, edit_graph, compile_graph, apply_graph, list_graph_node_types"),
        new("Missions", "Mission objects and slots", "list_mission_objects, get_mission_object, find_object"),
        new("Content editors", "Subclasses and content families: create, edit, validate, delete, export", "list_content_families, list_content, get_content, create_content, update_content, validate_content, delete_content, export_content, get_subclass_layout, check_subclass_build"),
        new("Data explorer", "Functions, strings, tag data, tool console", "decomp_status, search_functions, get_function, xrefs_to, xrefs_from, grep_decomp, rename_function, find_string, search_strings, get_string, dump_tag, read_tag_bytes, infer_tag_schema, list_tag_classes, list_tags_of_class, tag_type_handlers, get_class, get_vtables, get_struct, export_data"),
        new("Packages", "Packages and entries, raw tags", "list_packages, list_package_entries, get_package_entry, get_tag"),
        new("Media", "Shaders, animations, audio, video, Havok", "list_shaders, get_shader, list_animations, get_animation, list_audio, get_audio, audio_info, export_audio_wav, video_info, export_video, havok_packfile"),
        new("Live", "Live link state, player position", "get_live_state, get_player_position, wait_for_player_near, read_panel live"),
        new("Local Server", "Status, Log, Deploy, Config, Actions, Subsystems tabs", "local_server_status, local_server_log, local_server_deploy_plan, local_server_deploy, local_server_backups, local_server_rollback, local_server_config_read, local_server_config_write, local_server_command, local_server_quick_action, local_server_subsystems, local_server_start, local_server_stop, local_server_restart, local_server_selftest"),
        new("Settings", "Rendering, Paths, Live link, Interface tabs", "get_settings, set_settings; show_panel settings + ui_* for the rest"),
        new("Settings", "AI tab", "none: the user's only"),
        new("Window", "Layouts, panels, asset browser drawer, Solforge AI panel (window.ai_workspace shows it, never launches the app), content editors, restore/reset", "show_panel, run_command window.*"),
        new("Help", "Keybinds & Tips, shortcuts, notices", "get_help, run_command help.*, edit.shortcuts"),
        new("Reference", "Reference library (08-docs\\reference)", "search_reference, read_reference"),
        new("Retail", "Retail workspace: live-build (245048) tags, activities, strings, maps in the viewport, porting",
            "retail_status, retail_capabilities, retail_search, retail_tag, retail_read, retail_references, retail_strings, retail_class_map, retail_activity, " +
            "retail_map, retail_port_plan, retail_preview_mission, retail_engine, retail_open_map, retail_port_mission, retail_port_strings, retail_convert_tag, retail_port_audio, retail_export_geometry"),
        new("Solforge AI panel", "Context, attention, automatic mode (off: it needed the removed console)", "get_context, get_attention_log, get_open_documents, read_screen_text, automatic_start, automatic_status, automatic_stop, goal_progress, goal_complete, goal_blocked")
    ];

    /** Rows whose area, feature or tools contain every word of query. */
    public static JsonArray ToJson(string? query)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new JsonArray(Rows.Where(row => words.All(word => (row.Area + " " + row.Feature + " " + row.Tools).Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Select(row => (JsonNode)new JsonObject { ["area"] = row.Area, ["feature"] = row.Feature, ["tools"] = row.Tools }).ToArray());
    }
}
