/**
 * File: The editor command catalog for list_commands and run_command: every title bar menu command, toolbar toggle and panel
 * opener by a stable id (area.name), the menu path the user sees, what input it needs, and the dedicated tool to prefer when one
 * exists. Pure data; the editor (StudioForm.Ai.Commands.cs) runs the ids and reports which are enabled right now.
 */
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/** What a command needs besides its id. */
public enum CommandInput
{
    /** Nothing. */
    None,
    /** An existing file, used instead of the open-file picker. */
    OpenFile,
    /** A folder, used instead of the folder picker (created when its parent exists). */
    Folder,
    /** A file to write, used instead of the save picker; its extension picks the format. */
    SaveFile,
    /** on: true or false. */
    Toggle,
    /** value: one of Options (or free text when Options is empty). */
    Choice
}

/** One editor command: a stable id, the menu path, input, choices, the tool to prefer, and what it does. */
public sealed record EditorCommand(string Id, string Menu, CommandInput Input, string Description, string? Prefer = null,
    IReadOnlyList<string>? Options = null);

/** The catalog. Ids are public API for AI clients; add new commands, never rename. */
public static class EditorCommands
{
    public static readonly string[] Quality = ["Low", "Medium", "High", "Full"];
    public static readonly string[] Lighting = ["Studio", "Sky", "Ambient"];
    public static readonly string[] Layouts = ["Solforge", "Solforge (AI)", "Solforge (scene)", "Solforge (classic)", "Sunrise Studio"];
    public static readonly string[] Views = ["Top", "Bottom", "Front", "Back", "Right", "Left"];

    /** show_panel names: the dock panels, the floating editors and the dialogs a menu opens. */
    public static readonly string[] Panels =
    [
        "graph", "live", "local_server", "items", "data_explorer", "retail", "content_editors", "asset_browser", "inspector", "changes",
        "object_tree", "workspace", "ai_workspace", "settings", "keybinds", "shortcuts", "cache", "sunrise_location", "mod_details",
        "new_project", "add_asset", "notices"
    ];

    public static readonly EditorCommand[] All =
    [
        // File
        new("file.new_project", "File > New project", CommandInput.None, "Open the New project dialog.", "create_project"),
        new("file.open_project", "File > Open project...", CommandInput.OpenFile, "Open a sunrise.project.json.", "open_project"),
        new("file.open_recent", "File > Recent projects", CommandInput.Choice, "Open a recent project by folder name or manifest path."),
        new("file.new_mission_script", "File > New mission script...", CommandInput.None, "Start a new mission script.", "create_mission_script"),
        new("file.save", "File > Save project / Save script", CommandInput.None, "Save the active document (project, mission script or node graph)."),
        new("file.close_project", "File > Close project", CommandInput.None, "Close the project.", "close_project"),
        new("file.close_world", "File > Close world", CommandInput.None, "Close the open map.", "close_map"),
        new("file.sunrise_location", "File > Sunrise location...", CommandInput.None, "Open the installation dialog.", "set_settings (installation)"),
        new("file.cache", "File > Cache...", CommandInput.None, "Open the Cache dialog."),
        new("file.settings", "File > Settings...", CommandInput.None, "Open Settings (the AI tab stays the user's).", "set_settings"),
        new("file.exit", "File > Exit", CommandInput.None, "Quit Solforge (ends this AI session too)."),
        // Edit
        new("edit.undo", "Edit > Undo", CommandInput.None, "Undo in the active editor (project, mission graph or node graph)."),
        new("edit.redo", "Edit > Redo", CommandInput.None, "Redo in the active editor."),
        new("edit.convert_placement_deltas", "Edit > Convert placement deltas to object edits", CommandInput.None,
            "Turn raw placement package changes into object edits."),
        new("edit.shortcuts", "Edit > Shortcuts...", CommandInput.None, "Open the shortcut editor.", "get_help"),
        // Assets
        new("assets.export_full_blender", "Assets > Export full asset to Blender...", CommandInput.Folder,
            "Export the open asset as a Blender bundle into an empty folder outside the project.", "export_asset (blender_asset)"),
        new("assets.export_world_blender", "Assets > Export world to Blender...", CommandInput.Folder,
            "Export the open map as a Blender scene into an empty folder.", "export_asset (blender_world)"),
        new("assets.import_blender", "Assets > Import Blender asset...", CommandInput.OpenFile,
            "Import a *.sunrise-bundle.json back into the project.", "import_asset (blender_asset)"),
        new("assets.import_skinned_model", "Assets > Import skinned model...", CommandInput.OpenFile,
            "Replace the open entity's skinned model from a glTF.", "import_asset (skinned_model)"),
        new("assets.export", "Assets > Export...", CommandInput.SaveFile,
            "Export the open asset; the extension picks the format (.dds/.png textures, .glb/.gltf/.obj meshes, .bin raw, .json properties).",
            "export_asset (file)"),
        new("assets.export_animation", "Assets > Export animation...", CommandInput.SaveFile, "Export the previewed animation clip as .gltf.",
            "export_asset (animation)"),
        new("assets.export_editable_animation", "Assets > Export editable animation...", CommandInput.SaveFile,
            "Export the selected clip's controls as editable .gltf.", "export_asset (editable_animation)"),
        new("assets.import_animation_controls", "Assets > Import animation controls...", CommandInput.OpenFile,
            "Import edited animation controls (.gltf/.glb) into the selected clip.", "import_asset (animation_controls)"),
        new("assets.export_material_blender", "Assets > Export material to Blender...", CommandInput.Folder,
            "Export the open material as a Blender material into an empty folder.", "export_asset (blender_material)"),
        new("assets.export_collision", "Assets > Export collision...", CommandInput.SaveFile, "Export collision as .glb/.gltf/.obj.",
            "export_asset (collision) or export_collision_obj"),
        new("assets.import_collision", "Assets > Import collision...", CommandInput.OpenFile, "Import collision from .glb/.gltf.",
            "import_asset (collision)"),
        new("assets.import_delta", "Assets > Import as delta...", CommandInput.OpenFile,
            "Import a .bin/.dds/.png/.obj/.gltf/.glb/.json over the open asset as a project delta.", "import_asset (delta)"),
        new("assets.add_asset", "Assets > Add asset...", CommandInput.None, "Open the Add asset dialog (fill it with ui_set_value; " +
            "its Browse button takes arm_file_dialog)."),
        new("assets.edit_perk_behavior", "Assets > Edit perk behavior...", CommandInput.None, "Open the perk behavior picker."),
        new("assets.resolve_names", "Assets > Resolve asset names", CommandInput.None, "Index asset names across the installation."),
        new("assets.open_as_asset", "Asset browser > Open as asset", CommandInput.Choice,
            "Open a tag (value: hex hash) as a plain asset instead of its category editor.", "open_asset"),
        // Build
        new("build.packages", "Build > Build packages", CommandInput.None, "Build the project's packages.", "build_project"),
        new("build.install", "Build > Build and install", CommandInput.None, "Build and install the mod.", "build_project (install: true)"),
        new("build.update_running_game", "Build > Update running game", CommandInput.None, "Hot-update the running game.", "update_running_game"),
        new("build.to_folder", "Build > Build to folder...", CommandInput.Folder, "Build into a folder.", "build_to_folder"),
        new("build.mod_details", "Build > Mod details...", CommandInput.None, "Open the Mod details dialog.", "set_mod_details"),
        new("build.pack_solmod", "Build > Pack .solmod...", CommandInput.Folder, "Zip the installed mod with a manifest into a folder."),
        new("build.restore_snapshot", "Build > Restore install snapshot", CommandInput.Choice,
            "Restore an install snapshot by its stamp (list_commands shows them); Destiny 2 must be closed."),
        // View and toolbar
        new("view.graph", "View > Graph", CommandInput.None, "Show the Graph panel.", "show_panel"),
        new("view.live", "View > Live", CommandInput.None, "Show the Live tab.", "show_panel"),
        new("view.local_server", "View > Local Server", CommandInput.None, "Show the Local Server panel.", "show_panel"),
        new("view.items", "View > Items", CommandInput.None, "Show the item browser.", "show_panel"),
        new("view.data_explorer", "View > Data explorer", CommandInput.None, "Open the Data explorer.", "show_panel"),
        new("view.retail", "View > Retail workspace", CommandInput.None, "Open the Retail workspace (live-build data, maps, porting).", "show_panel"),
        new("view.frame_selection", "View > Frame selection", CommandInput.None, "Frame the selection in the 3D view.", "focus_camera"),
        new("view.set_cursor_to_selection", "View > Set cursor to selection", CommandInput.None, "Move the 3D cursor to the selection."),
        new("view.wireframe", "View > Wireframe", CommandInput.Toggle, "Wireframe on or off.", "set_viewport_options"),
        new("view.grid", "View > Grid", CommandInput.Toggle, "Grid on or off.", "set_viewport_options"),
        new("view.rendering", "View > 3D viewport rendering", CommandInput.Toggle, "3D rendering on or off.", "set_viewport_rendering"),
        new("toolbar.game_shaders", "Toolbar > Game shaders", CommandInput.Toggle, "Game shaders on or off.", "set_viewport_options"),
        new("toolbar.full_detail", "Toolbar > Full detail", CommandInput.Toggle, "Full detail on or off.", "set_viewport_options"),
        new("toolbar.quality", "Toolbar > Quality", CommandInput.Choice, "Viewport quality.", "set_viewport_options", Quality),
        new("toolbar.lighting", "Toolbar > Lighting", CommandInput.Choice, "Lighting mode.", "set_viewport_options", Lighting),
        // Window
        new("window.layout", "Window > Layouts", CommandInput.Choice, "Apply a layout preset.", null, Layouts),
        new("window.asset_browser", "Window > Asset browser drawer", CommandInput.None, "Toggle the asset browser drawer.", "show_panel"),
        new("window.ai_workspace", "Window > Solforge AI panel", CommandInput.None, "Show the Solforge AI panel (requests, edits, context); never launches the Solforge AI app.", "show_panel"),
        new("window.content_editors", "Window > Content editors", CommandInput.None, "Show the Content editors window.", "show_panel"),
        new("window.panel", "Window > Panels", CommandInput.Choice, "Show a dock panel by its title.", "show_panel",
            ["Workspace", "Asset browser", "Inspector", "Project changes", "Object tree", "Graph", "AI", "Local Server"]),
        new("window.restore_areas", "Window > Restore areas", CommandInput.None, "Undo a maximized area."),
        new("window.reset_layout", "Window > Reset layout", CommandInput.None, "Reset to the Solforge layout."),
        // Navigation and Help
        new("nav.back", "Toolbar > Back", CommandInput.None, "Go back in the asset history."),
        new("nav.forward", "Toolbar > Forward", CommandInput.None, "Go forward in the asset history."),
        new("help.keybinds", "Help > Keybinds & Tips...", CommandInput.None, "Open Keybinds & Tips.", "get_help"),
        new("help.notices", "Help > Third-party notices", CommandInput.None, "Open the third-party notices.")
    ];

    /** The command with this id (case-insensitive), or null. */
    public static EditorCommand? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(command => string.Equals(command.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /** Error text for a run_command call, or null when the id is known and its input is given. */
    public static string? Problem(JsonObject args)
    {
        string? id = McpJson.String(args, "id");
        if (Find(id) is not { } command) return $"Unknown command '{id}'; list_commands shows the ids.";
        string? path = McpJson.String(args, "path"), value = McpJson.String(args, "value");
        switch (command.Input)
        {
            case CommandInput.OpenFile when string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path):
                return $"{command.Id} needs path: an existing file (absolute path).";
            case CommandInput.Folder or CommandInput.SaveFile when string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path):
                return $"{command.Id} needs path: an absolute {(command.Input == CommandInput.Folder ? "folder" : "file")} path.";
            case CommandInput.Folder when !Directory.Exists(path) && !Directory.Exists(Path.GetDirectoryName(path)):
            case CommandInput.SaveFile when !Directory.Exists(Path.GetDirectoryName(path)):
                return $"{command.Id}: the parent folder of {path} does not exist.";
            case CommandInput.Toggle when args["on"] is not JsonValue on || !on.TryGetValue(out bool _):
                return $"{command.Id} needs on: true or false.";
            case CommandInput.Choice when string.IsNullOrWhiteSpace(value):
                return $"{command.Id} needs value" + (command.Options is { Count: > 0 } needed ? ": " + string.Join(", ", needed) + "." : ".");
            case CommandInput.Choice when command.Options is { Count: > 0 } options && !options.Contains(value!.Trim(), StringComparer.OrdinalIgnoreCase):
                return $"{command.Id}: '{value}' is not one of {string.Join(", ", options)}.";
        }
        return null;
    }

    /** The catalog as JSON rows, filtered by words in the id, menu or description; enabled states from the editor when known. */
    public static JsonArray ToJson(string? query, JsonObject? enabled)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rows = new JsonArray();
        foreach (var command in All)
        {
            string text = command.Id + " " + command.Menu + " " + command.Description + " " + command.Prefer;
            if (!words.All(word => text.Contains(word, StringComparison.OrdinalIgnoreCase))) continue;
            var row = new JsonObject { ["id"] = command.Id, ["menu"] = command.Menu, ["description"] = command.Description };
            if (command.Input != CommandInput.None) row["input"] = InputName(command.Input);
            if (command.Options is { Count: > 0 } options) row["options"] = new JsonArray(options.Select(option => (JsonNode)option).ToArray());
            if (command.Prefer is { } prefer) row["prefer"] = prefer;
            if (enabled?[command.Id] is JsonValue state) row["enabled"] = state.DeepClone();
            if (enabled?[command.Id + "#options"] is JsonArray live) row["options"] = live.DeepClone();
            rows.Add(row);
        }
        return rows;
    }

    /** The argument a command's input takes, as list_commands says it. */
    public static string InputName(CommandInput input) => input switch
    {
        CommandInput.OpenFile => "path (existing file)",
        CommandInput.Folder => "path (folder)",
        CommandInput.SaveFile => "path (file to write)",
        CommandInput.Toggle => "on (true/false)",
        CommandInput.Choice => "value",
        _ => "none"
    };
}
