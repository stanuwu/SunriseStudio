/**
 * File: The editor side of the command tools (Solforge.Mcp SolforgeTools.Commands.cs): run_command over the EditorCommands
 * catalog, show_panel, set_viewport_options, set_camera, project_change, arm_file_dialog and list_commands' enabled states.
 * Everything runs on the UI thread after the approve click; picker commands get their path through AiFileDialog, so the same
 * install and project checks as a real pick apply. Results that start with "Refused:" become tool errors.
 */
using System.Numerics;
using System.Text.Json.Nodes;
using Solforge.Mcp;
using SunriseStudio.Projects;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/**
 * A path the AI gave for the next file or folder picker (arm_file_dialog, run_command path). PickFile, PickFolder, SaveFile and
 * the pages' own pickers take it instead of showing the dialog; it is used once and expires. UI thread only.
 */
internal static class AiFileDialog
{
    private static string? armed;
    private static long until;

    /** Arms the next picker with path for the given seconds. */
    public static void Arm(string path, int seconds)
    {
        armed = path;
        until = Environment.TickCount64 + Math.Clamp(seconds, 1, 600) * 1000L;
    }

    /** The armed path, or else what the real picker returns (null when cancelled). */
    public static string? Pick(Func<string?> show, bool folder = false) => Take(folder) ?? show();

    /** Drops an unused path. */
    public static void Clear() => armed = null;

    /** Whether a path is waiting. */
    public static bool Armed => armed is not null && Environment.TickCount64 < until;

    /** The armed path (once), or null when none is waiting; a folder picker creates the folder when its parent exists. */
    public static string? Take(bool folder = false)
    {
        if (!Armed) { armed = null; return null; }
        string path = armed!;
        armed = null;
        if (folder && !Directory.Exists(path) && Directory.Exists(Path.GetDirectoryName(path)))
            try { Directory.CreateDirectory(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return path;
    }
}

public sealed partial class StudioForm
{
    private const string Refused = "Refused: ";

    /** The viewport a tool means: "scene", "asset", or the one showing. */
    private AssetViewport? AiViewport(string? which) => which switch
    {
        "scene" => viewport,
        "asset" => AssetPreviewViewport,
        _ => workspace.World is not null && !assetPageVisible ? viewport : AssetPreviewViewport
    };

    /** The command tools' actions; null for other tools. */
    private string? RunAiCommandAction(string tool, JsonObject args)
    {
        try
        {
            return tool switch
            {
                "run_command" => AiRunCommand(args),
                "show_panel" => AiShowPanel(McpJson.String(args, "panel")?.Trim().ToLowerInvariant() ?? ""),
                "set_viewport_options" => AiViewportOptions(args),
                "set_camera" => AiSetCamera(args),
                "project_change" => AiProjectChange(args),
                "arm_file_dialog" => AiArmDialog(args),
                _ => null
            };
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            AiFileDialog.Clear();
            return Refused + exception.Message;
        }
    }

    private static string AiArmDialog(JsonObject args)
    {
        string path = McpJson.String(args, "path")!;
        AiFileDialog.Arm(path, McpJson.Int(args, "seconds", 120));
        return $"The next file or folder picker uses {path}. Now ui_invoke the control that opens it.";
    }

    /** Enabled flags (and live choices) for list_commands. */
    private JsonObject AiCommandStates()
    {
        var enabled = new JsonObject();
        foreach (var command in EditorCommands.All) enabled[command.Id] = AiCommandEnabled(command.Id);
        enabled["file.open_recent#options"] = new JsonArray(settings.RecentProjects
            .Select(path => (JsonNode)(Path.GetFileName(Path.GetDirectoryName(path)) ?? path)).ToArray());
        if (workspace.CanEdit && workspace.Project?.Mod is not null)
            enabled["build.restore_snapshot#options"] = new JsonArray(workspace.ListInstallSnapshots().Select(snapshot => (JsonNode)snapshot.Stamp).ToArray());
        return new JsonObject { ["enabled"] = enabled };
    }

    /** Whether a command can run now, mirroring its menu item's enabled state. */
    private bool AiCommandEnabled(string id)
    {
        bool idle = !workspace.IsBusy, edit = workspace.CanEdit, modded = edit && workspace.Project?.Mod is not null;
        return id switch
        {
            "file.new_project" or "file.open_project" or "file.sunrise_location" or "file.cache" => idle,
            "file.open_recent" => idle && settings.RecentProjects.Count > 0,
            "file.new_mission_script" => workspace.Catalog is not null && idle,
            "file.save" => CanSaveActive,
            "file.close_project" => edit,
            "file.close_world" => workspace.World is not null && idle,
            "edit.undo" => CanUndoActive,
            "edit.redo" => CanRedoActive,
            "edit.convert_placement_deltas" => edit && workspace.Project?.Changes.Count > 0,
            "assets.export_full_blender" => CanExportBundle,
            "assets.export_world_blender" => idle && workspace.World is not null,
            "assets.import_blender" or "assets.add_asset" => edit,
            "assets.import_skinned_model" => CanReplaceSkin,
            "assets.export" => workspace.Document is not null && idle,
            "assets.export_animation" => AssetPreviewViewport?.Animation is { Source.Binding: not null, Clip: not null },
            "assets.export_editable_animation" => SelectedClipTag() is not null && idle,
            "assets.import_animation_controls" => SelectedClipTag() is not null && edit,
            "assets.export_material_blender" => workspace.Document?.Entry.Reference == 0x808071E8 && idle,
            "assets.export_collision" => CanExportCollision,
            "assets.import_collision" => edit && CanExportCollision,
            "assets.import_delta" => edit && workspace.Document is not null,
            "assets.edit_perk_behavior" or "assets.resolve_names" or "assets.open_as_asset" => workspace.Catalog is not null && idle,
            "build.packages" or "build.install" or "build.update_running_game" or "build.to_folder" or "build.mod_details" => edit,
            "build.pack_solmod" => modded,
            "build.restore_snapshot" => modded && !destiny2Detected,
            "view.items" => workspace.Catalog is not null,
            "view.frame_selection" => viewport?.Camera.PivotFree == true,
            "view.set_cursor_to_selection" => (viewport?.SelectedObject is not null || viewport?.SelectedCollision >= 0) && viewport!.Camera.PivotFree,
            "view.wireframe" or "view.grid" => viewport is not null,
            "window.restore_areas" => maximizedPanel is not null,
            "nav.back" => workspace.CanNavigateBack,
            "nav.forward" => workspace.CanNavigateForward,
            _ => true
        };
    }

    /** run_command: one catalog command. */
    private string AiRunCommand(JsonObject args)
    {
        if (EditorCommands.Problem(args) is { } problem) return Refused + problem;
        var command = EditorCommands.Find(McpJson.String(args, "id"))!;
        if (!AiCommandEnabled(command.Id)) return Refused + $"{command.Menu} is disabled right now (list_commands shows what is enabled).";
        string? path = McpJson.String(args, "path"), value = McpJson.String(args, "value")?.Trim();
        bool on = McpJson.Bool(args, "on", false);
        if (path is not null) AiFileDialog.Arm(path, 5);
        try
        {
            string done = AiCommand(command.Id, path, value, on);
            // A picker command that never reached its picker did nothing.
            if (path is not null && AiFileDialog.Armed) return Refused + $"{command.Menu} did not ask for a file (nothing open for it?).";
            return done;
        }
        finally { AiFileDialog.Clear(); }
    }

    private string AiCommand(string id, string? path, string? value, bool on)
    {
        switch (id)
        {
            case "file.new_project": projectDialog = true; return "Opened New project; fill it with ui_set_value or use create_project.";
            case "file.open_project": AiFileDialog.Clear(); OpenProject(path!); return "Opening " + path + "; get_project shows it.";
            case "file.open_recent":
            {
                string? match = settings.RecentProjects.FirstOrDefault(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileName(Path.GetDirectoryName(item)), value, StringComparison.OrdinalIgnoreCase));
                if (match is null || !File.Exists(match)) return Refused + $"No recent project '{value}' (list_commands lists them).";
                OpenProject(match);
                return "Opening " + match + ".";
            }
            case "file.new_mission_script": NewMissionScript(); return "Started a new mission script.";
            case "file.save": SaveActiveDocument(); return "Saved the active document.";
            case "file.close_project": workspace.Run(() => { if (ConfirmMissionDraft()) workspace.CloseProject(); }); return "Closing the project.";
            case "file.close_world": workspace.Run(workspace.CloseWorld); return "Closing the map.";
            case "file.sunrise_location": settingsDialog = true; return "Opened Sunrise location.";
            case "file.cache": cacheDialog = true; return "Opened Cache.";
            case "file.settings": preferencesOpen = true; return "Opened Settings.";
            case "file.exit": BeginInvoke(Close); return "Solforge is closing.";
            case "edit.undo": UndoActive(); return "Undone.";
            case "edit.redo": RedoActive(); return "Redone.";
            case "edit.convert_placement_deltas": workspace.Run(() => workspace.ConvertPlacementDeltas()); return "Converting placement deltas.";
            case "edit.shortcuts": shortcutsDialog = true; return "Opened Shortcuts.";
            case "assets.export_full_blender": ExportFullAsset(); return "Exporting the Blender asset to " + path + ".";
            case "assets.export_world_blender": ExportWorldScene(); return "Exporting the world to " + path + ".";
            case "assets.import_blender": BeginBundleImport(); return "Importing " + path + "; list_project_changes shows the result.";
            case "assets.import_skinned_model": BeginSkinImport(); return "Importing the skinned model " + path + ".";
            case "assets.export": ExportSelected(); return "Exporting the open asset to " + path + ".";
            case "assets.export_animation": ExportAnimation(); return "Exporting the animation to " + path + ".";
            case "assets.export_editable_animation": ExportAnimationControls(); return "Exporting animation controls to " + path + ".";
            case "assets.import_animation_controls": ImportAnimationControls(); return "Importing animation controls from " + path + ".";
            case "assets.export_material_blender": ExportMaterialBundle(); return "Exporting the material to " + path + ".";
            case "assets.export_collision": ExportCollision(); return "Exporting collision to " + path + ".";
            case "assets.import_collision": ImportCollision(); return "Importing collision from " + path + ".";
            case "assets.import_delta": ImportDelta(); return "Importing " + path + " as a delta.";
            case "assets.add_asset": addDialog = true; return "Opened Add asset; fill it with ui_set_value (Browse takes arm_file_dialog).";
            case "assets.edit_perk_behavior": OpenPerkBehaviorPicker(); return "Opened the perk behavior picker.";
            case "assets.resolve_names": workspace.Run(workspace.IndexNames); return "Resolving asset names.";
            case "assets.open_as_asset":
                if (SolforgeTools.ParseHash(value) is not { } tag) return Refused + "value must be a tag hash such as 0x80A1B2C3.";
                workspace.Open(tag, StudioWorkspace.PlainOpen);
                return $"Opened 0x{tag:X8} as a plain asset.";
            case "build.packages": workspace.Run(() => workspace.Build()); return "Building; poll get_build_status.";
            case "build.install": return RunAiProjectAction("build_project", new JsonObject { ["install"] = true }) ?? "Started.";
            case "build.update_running_game": UpdateRunningGame(); return "Updating the running game; poll get_build_status.";
            case "build.to_folder": AiFileDialog.Clear(); workspace.Run(() => workspace.Build(path!)); return "Building into " + path + ".";
            case "build.mod_details": workspace.Run(OpenModDialog); return "Opened Mod details.";
            case "build.pack_solmod": AiFileDialog.Clear(); EnsureOutsideInstall(path!); workspace.Run(() => workspace.PackSolmod(path!)); return "Packing the .solmod into " + path + ".";
            case "build.restore_snapshot":
            {
                var snapshot = workspace.ListInstallSnapshots().FirstOrDefault(row => string.Equals(row.Stamp, value, StringComparison.OrdinalIgnoreCase));
                if (snapshot is null) return Refused + $"No snapshot '{value}' (list_commands lists them).";
                workspace.Run(() => workspace.RestoreInstallSnapshotWithNotice(snapshot.Stamp));
                return "Restoring snapshot " + snapshot.Stamp + ".";
            }
            case "view.graph": return AiShowPanel("graph");
            case "view.live": return AiShowPanel("live");
            case "view.local_server": return AiShowPanel("local_server");
            case "view.items": return AiShowPanel("items");
            case "view.data_explorer": return AiShowPanel("data_explorer");
            case "view.retail": return AiShowPanel("retail");
            case "view.frame_selection": viewport?.FocusSelection(); return "Framed the selection.";
            case "view.set_cursor_to_selection": viewport?.SetCursorToSelection(); return "Moved the 3D cursor to the selection.";
            case "view.wireframe": viewport!.Wireframe = on; return "Wireframe " + (on ? "on." : "off.");
            case "view.grid": viewport!.ShowGrid = on; return "Grid " + (on ? "on." : "off.");
            case "view.rendering": SetViewportRendering(on); return "3D viewport rendering " + (on ? "on." : "off.");
            case "toolbar.game_shaders": SetGameShaders(on); return "Game shaders " + (on ? "on." : "off.");
            case "toolbar.full_detail": SetFullDetail(on); return "Full detail " + (on ? "on." : "off.");
            case "toolbar.quality": return AiViewportOptions(new JsonObject { ["quality"] = value });
            case "toolbar.lighting": return AiViewportOptions(new JsonObject { ["lighting"] = value });
            case "window.layout":
            {
                string name = EditorCommands.Layouts.First(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));
                ApplyLayoutPreset(name);
                return "Applied the " + name + " layout.";
            }
            case "window.asset_browser": ToggleBrowserDrawer(); return "Toggled the asset browser drawer.";
            case "window.ai_workspace": return AiShowPanel("ai_workspace");
            case "window.content_editors": return AiShowPanel("content_editors");
            case "window.panel":
            {
                var kind = VisiblePanelChoices
                    .FirstOrDefault(item => string.Equals(PanelTitle(item), value, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(DisplayTitle(item), value, StringComparison.OrdinalIgnoreCase), StudioPanelKind.Empty);
                return kind == StudioPanelKind.Empty ? Refused + $"No panel '{value}'." : AiShowLayoutPanel(kind);
            }
            case "window.restore_areas": maximizedPanel = null; return "Restored the areas.";
            case "window.reset_layout": ResetLayout(); return "Reset the layout.";
            case "nav.back": NavigateBack(); return "Went back.";
            case "nav.forward": NavigateForward(); return "Went forward.";
            case "help.keybinds": helpDialog = true; return "Opened Keybinds & Tips (get_help reads the same).";
            case "help.notices": noticesDialog = true; return "Opened Third-party notices.";
            default: return Refused + "Unknown command.";
        }
    }

    /** Shows a dock panel the way Window > Panels does. */
    private string AiShowLayoutPanel(StudioPanelKind kind)
    {
        if (!ShowPopout(kind, true))
        {
            var shown = layout.Show(kind);
            if (shown.Collapsed == true) shown.Collapsed = false;
            maximizedPanel = null;
            layoutDirty = true;
        }
        return "Showing " + PanelTitle(kind) + ".";
    }

    /** show_panel. */
    private string AiShowPanel(string panel)
    {
        switch (panel)
        {
            case "graph": focusMissionGraph = true; return "Showing the Graph panel.";
            case "live": focusLive = true; return "Showing the Live tab.";
            case "local_server": focusLocalServer = true; return "Showing the Local Server panel.";
            case "items":
                if (workspace.Catalog is null) return Refused + "Open the game installation first.";
                ShowItems(); return "Showing the item browser.";
            case "data_explorer": dataExplorerOpen = true; return "Opened the Data explorer.";
            case "retail": retailOpen = true; return "Opened the Retail workspace.";
            case "content_editors": contentOpen = true; return "Opened the Content editors.";
            case "asset_browser": return AiShowLayoutPanel(StudioPanelKind.Browser);
            case "inspector": return AiShowLayoutPanel(StudioPanelKind.Inspector);
            case "changes": return AiShowLayoutPanel(StudioPanelKind.Changes);
            case "object_tree": return AiShowLayoutPanel(StudioPanelKind.ObjectTree);
            case "workspace": return AiShowLayoutPanel(StudioPanelKind.Workspace);
            // Shows the Solforge AI panel; it never launches the Solforge AI app (launching a GUI app is a user action).
            case "ai_workspace":
                OpenAiWorkspace();
                return McpOnlyUi ? "The assistant interface is not part of this build; Solforge MCP remains available." : "Showing the Solforge AI panel.";
            case "settings": preferencesOpen = true; return "Opened Settings.";
            case "keybinds": helpDialog = true; return "Opened Keybinds & Tips.";
            case "shortcuts": shortcutsDialog = true; return "Opened Shortcuts.";
            case "cache": cacheDialog = true; return "Opened Cache.";
            case "sunrise_location": settingsDialog = true; return "Opened Sunrise location.";
            case "mod_details":
                if (!workspace.CanEdit) return Refused + "No project is open, or Solforge is busy.";
                workspace.Run(OpenModDialog); return "Opened Mod details.";
            case "new_project": projectDialog = true; return "Opened New project.";
            case "add_asset":
                if (!workspace.CanEdit) return Refused + "No project is open, or Solforge is busy.";
                addDialog = true; return "Opened Add asset.";
            case "notices": noticesDialog = true; return "Opened Third-party notices.";
            default: return Refused + "Unknown panel.";
        }
    }

    /** set_viewport_options. */
    private string AiViewportOptions(JsonObject args)
    {
        if (SolforgeTools.ViewportOptionsProblem(args) is { } problem) return Refused + problem;
        var view = AiViewport(McpJson.String(args, "viewport"));
        var done = new List<string>();
        foreach (var (name, _) in args.ToArray())
        {
            string text = McpJson.String(args, name)?.Trim() ?? "";
            bool flag = McpJson.Bool(args, name, false);
            switch (name)
            {
                case "game_shaders": SetGameShaders(flag); break;
                case "full_detail": SetFullDetail(flag); break;
                case "quality": SetViewportQuality((ViewportQuality)Array.FindIndex(EditorCommands.Quality, item => item.Equals(text, StringComparison.OrdinalIgnoreCase))); break;
                case "lighting": SetLighting(Array.FindIndex(EditorCommands.Lighting, item => item.Equals(text, StringComparison.OrdinalIgnoreCase))); break;
                case "viewport": continue;
                default:
                    if (view is null) return Refused + "No 3D view is showing (open a map or asset; set_viewport_rendering turns rendering on).";
                    switch (name)
                    {
                        case "wireframe": view.Wireframe = flag; break;
                        case "grid": view.ShowGrid = flag; break;
                        case "axis_gizmo": view.ShowAxisGizmo = flag; break;
                        case "statistics": view.ShowStatistics = flag; break;
                        case "projection": view.Camera.Orthographic = text.Equals("orthographic", StringComparison.OrdinalIgnoreCase); break;
                        case "view": view.SetViewPreset(Enum.Parse<CameraViewPreset>(text, true)); break;
                    }
                    break;
            }
            done.Add(name + " = " + (args[name]?.ToString() ?? ""));
        }
        return "Set " + string.Join(", ", done) + ".";
    }

    /** set_camera. */
    private string AiSetCamera(JsonObject args)
    {
        if (SolforgeTools.CameraProblem(args) is { } problem) return Refused + problem;
        if (AiViewport(McpJson.String(args, "viewport")) is not { } view) return Refused + "No 3D view is showing.";
        var camera = view.Camera;
        if (!camera.PivotFree) return Refused + "The camera follows the player or the Live game right now; detach it first.";
        var eye = SolforgeTools.Vector(args["eye"]);
        var target = SolforgeTools.Vector(args["look_at"]);
        if (eye is { } from && target is { } to) camera.LookFrom(from, to);
        else if (target is { } pivot) camera.MovePivot(pivot);
        if (args["yaw"] is not null || args["pitch"] is not null)
        {
            float yaw = args["yaw"] is JsonValue yawValue ? (float)(SolforgeTools.SettingNumber(yawValue)!.Value * Math.PI / 180) : camera.Yaw;
            float pitch = args["pitch"] is JsonValue pitchValue ? (float)(Math.Clamp(SolforgeTools.SettingNumber(pitchValue)!.Value, -89, 89) * Math.PI / 180) : camera.Pitch;
            camera.SetView(yaw, pitch);
        }
        if (args["distance"] is JsonValue distanceValue && SolforgeTools.SettingNumber(distanceValue) is { } distance && camera.Distance > 0)
            camera.Zoom((float)(Math.Log(distance / camera.Distance) / Math.Log(0.85)));
        Vector3 position = camera.Position;
        return $"Camera at [{position.X:0.##}, {position.Y:0.##}, {position.Z:0.##}], yaw {camera.Yaw * 180 / MathF.PI:0.#}, " +
            $"pitch {camera.Pitch * 180 / MathF.PI:0.#}, distance {camera.Distance:0.##}.";
    }

    /** project_change. */
    private string AiProjectChange(JsonObject args)
    {
        if (SolforgeTools.ChangeProblem(args) is { } problem) return Refused + problem;
        if (workspace.Project is not { } project) return Refused + "No project is open.";
        string action = McpJson.String(args, "action")!.Trim().ToLowerInvariant();
        switch (action)
        {
            case "revert" or "compare":
            {
                uint tag = SolforgeTools.ParseHash(McpJson.String(args, "tag"))!.Value;
                if (project.Changes.All(change => change.TagHash != tag)) return Refused + $"0x{tag:X8} is not a changed asset (list_project_changes).";
                if (action == "compare")
                {
                    if (workspace.Catalog is null) return Refused + "Open the game installation first.";
                    BeginTagDiff(tag);
                    return $"Opened the tag diff for 0x{tag:X8}; get_open_documents reads it.";
                }
                if (!workspace.CanEdit) return Refused + "Solforge is busy.";
                workspace.Run(() => workspace.Revert(tag));
                return $"Reverting 0x{tag:X8}.";
            }
            default:
            {
                string id = McpJson.String(args, "id")!.Trim();
                if (project.AddedAssets.FirstOrDefault(asset => asset.Id == id) is not { } added) return Refused + $"No added asset '{id}'.";
                if (action == "remove")
                {
                    if (!workspace.CanEdit) return Refused + "Solforge is busy.";
                    workspace.Run(() => workspace.RemoveAdded(added.Id));
                    return "Removing the added asset " + id + ".";
                }
                string path = McpJson.String(args, "path")!;
                AiFileDialog.Arm(path, 5);
                try { ExportAdded(added); }
                finally { AiFileDialog.Clear(); }
                return $"Exporting {id} to {path}.";
            }
        }
    }
}
