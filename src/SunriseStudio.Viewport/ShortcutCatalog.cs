namespace SunriseStudio.Viewport;

/**
 * One rebindable command.
 * @param Id Stable name used in saved preferences.
 * @param Accepts Input kinds a binding of this action may use.
 */
public sealed record ShortcutAction(string Id, string Name, ShortcutSection Section, IReadOnlyList<ShortcutKind> Accepts)
{
    /** True when the action accepts bindings of this kind. */
    public bool Allows(ShortcutKind kind) => Accepts.Contains(kind);
}

/** Every rebindable action. Id values are stored in preferences; never change one. */
public static class ShortcutActions
{
    public const string NewProject = "menu.new_project";
    public const string OpenProject = "menu.open_project";
    public const string SaveProject = "menu.save_project";
    public const string Undo = "menu.undo";
    public const string Redo = "menu.redo";
    public const string BuildPackages = "menu.build_packages";
    public const string BuildAndInstall = "menu.build_and_install";
    public const string UpdateRunningGame = "menu.update_running_game";
    public const string CreateTexture = "menu.create_texture";
    public const string CloneMaterial = "menu.create_material";
    public const string CreateStaticProp = "menu.create_static_prop";
    public const string CreateMissionScript = "menu.create_mission_script";
    public const string OpenInterfaceList = "menu.create_cui_widget";
    public const string CloneWeapon = "menu.create_weapon";
    public const string CloneArmor = "menu.create_armor";
    public const string ClonePlug = "menu.create_plug";
    public const string NavigateBack = "menu.navigate_back";
    public const string NavigateForward = "menu.navigate_forward";
    public const string ToggleBrowser = "menu.toggle_browser";
    public const string OpenAiWorkspace = "menu.open_ai_workspace";
    public const string ToggleViewportRendering = "menu.toggle_viewport_rendering";
    public const string OpenSettings = "menu.open_settings";
    public const string OpenHelp = "menu.open_help";

    public const string Pan2D = "2d.pan";
    public const string Zoom2D = "2d.zoom";
    public const string FrameSelection2D = "2d.frame_selection";
    public const string FrameAll2D = "2d.frame_all";
    public const string Select2D = "2d.select";
    public const string Delete2D = "2d.delete";
    public const string Duplicate2D = "2d.duplicate";

    public const string Orbit3D = "3d.orbit";
    public const string Pan3D = "3d.pan";
    public const string Zoom3D = "3d.zoom";
    public const string FrameSelection3D = "3d.frame_selection";
    public const string FrameAll3D = "3d.frame_all";
    public const string Select3D = "3d.select";
    public const string SetCursor3D = "3d.set_cursor";
    public const string ToggleCursor3D = "3d.toggle_cursor";
    public const string ToggleTree3D = "3d.toggle_tree";
    public const string HideSelection3D = "3d.hide_selection";
    public const string ShowAll3D = "3d.show_all";
    public const string TogglePlayer3D = "3d.toggle_player";
    public const string SelectTool3D = "3d.select_tool";
    public const string MoveTool3D = "3d.move_tool";
    public const string RotateTool3D = "3d.rotate_tool";
    public const string ScaleTool3D = "3d.scale_tool";

    private static readonly ShortcutKind[] KeyOnly = [ShortcutKind.Key];
    private static readonly ShortcutKind[] KeyOrClick = [ShortcutKind.Key, ShortcutKind.Click];
    private static readonly ShortcutKind[] ClickOnly = [ShortcutKind.Click];
    private static readonly ShortcutKind[] DragOnly = [ShortcutKind.Drag];
    private static readonly ShortcutKind[] WheelOrDrag = [ShortcutKind.Wheel, ShortcutKind.Drag];

    public static IReadOnlyList<ShortcutAction> All { get; } =
    [
        new(NewProject, "New project", ShortcutSection.Menus, KeyOnly),
        new(OpenProject, "Open project", ShortcutSection.Menus, KeyOnly),
        new(SaveProject, "Save project", ShortcutSection.Menus, KeyOnly),
        new(Undo, "Undo", ShortcutSection.Menus, KeyOnly),
        new(Redo, "Redo", ShortcutSection.Menus, KeyOnly),
        new(BuildPackages, "Build packages", ShortcutSection.Menus, KeyOnly),
        new(BuildAndInstall, "Build and install", ShortcutSection.Menus, KeyOnly),
        new(UpdateRunningGame, "Update running game", ShortcutSection.Menus, KeyOnly),
        new(CreateTexture, "New texture", ShortcutSection.Menus, KeyOnly),
        new(CloneMaterial, "Clone material", ShortcutSection.Menus, KeyOnly),
        new(CreateStaticProp, "New static prop", ShortcutSection.Menus, KeyOnly),
        new(CreateMissionScript, "New mission script", ShortcutSection.Menus, KeyOnly),
        new(OpenInterfaceList, "Open Interface list", ShortcutSection.Menus, KeyOnly),
        new(CloneWeapon, "Clone weapon", ShortcutSection.Menus, KeyOnly),
        new(CloneArmor, "Clone armor", ShortcutSection.Menus, KeyOnly),
        new(ClonePlug, "Clone plug", ShortcutSection.Menus, KeyOnly),
        new(NavigateBack, "Navigate back", ShortcutSection.Menus, KeyOrClick),
        new(NavigateForward, "Navigate forward", ShortcutSection.Menus, KeyOrClick),
        new(ToggleBrowser, "Show or hide the Asset browser", ShortcutSection.Menus, KeyOnly),
        new(OpenAiWorkspace, "Launch or focus Solforge AI", ShortcutSection.Menus, KeyOnly),
        new(ToggleViewportRendering, "Turn 3D viewport rendering on or off", ShortcutSection.Menus, KeyOnly),
        new(OpenSettings, "Open Settings", ShortcutSection.Menus, KeyOnly),
        new(OpenHelp, "Open Help: keybinds and tips", ShortcutSection.Menus, KeyOnly),

        new(Pan2D, "Pan", ShortcutSection.View2D, DragOnly),
        new(Zoom2D, "Zoom", ShortcutSection.View2D, WheelOrDrag),
        new(FrameSelection2D, "Frame selection", ShortcutSection.View2D, KeyOnly),
        new(FrameAll2D, "Frame all", ShortcutSection.View2D, KeyOnly),
        new(Select2D, "Select; drag to move", ShortcutSection.View2D, ClickOnly),
        new(Delete2D, "Delete selection", ShortcutSection.View2D, KeyOnly),
        new(Duplicate2D, "Duplicate selection", ShortcutSection.View2D, KeyOnly),

        new(Orbit3D, "Orbit", ShortcutSection.View3D, DragOnly),
        new(Pan3D, "Pan", ShortcutSection.View3D, DragOnly),
        new(Zoom3D, "Zoom", ShortcutSection.View3D, WheelOrDrag),
        new(FrameSelection3D, "Frame selection", ShortcutSection.View3D, KeyOnly),
        new(FrameAll3D, "Frame all", ShortcutSection.View3D, KeyOnly),
        new(Select3D, "Select", ShortcutSection.View3D, ClickOnly),
        new(SetCursor3D, "Set cursor to selection", ShortcutSection.View3D, KeyOnly),
        new(ToggleCursor3D, "Show or hide the 3D cursor", ShortcutSection.View3D, KeyOnly),
        new(ToggleTree3D, "Show or hide the object tree", ShortcutSection.View3D, KeyOnly),
        new(HideSelection3D, "Hide selected object", ShortcutSection.View3D, KeyOnly),
        new(ShowAll3D, "Show all objects", ShortcutSection.View3D, KeyOnly),
        new(TogglePlayer3D, "Toggle player camera", ShortcutSection.View3D, KeyOnly),
        new(SelectTool3D, "Select tool", ShortcutSection.View3D, KeyOnly),
        new(MoveTool3D, "Move tool", ShortcutSection.View3D, KeyOnly),
        new(RotateTool3D, "Rotate tool", ShortcutSection.View3D, KeyOnly),
        new(ScaleTool3D, "Scale tool", ShortcutSection.View3D, KeyOnly)
    ];

    private static readonly Dictionary<string, ShortcutAction> ById = All.ToDictionary(action => action.Id, StringComparer.Ordinal);

    /** The action with an id, or null. */
    public static ShortcutAction? Find(string? id) => id is null ? null : ById.GetValueOrDefault(id);
}

/** A named binding set. Every action has an entry; an empty list means unbound. */
public sealed class ShortcutPreset
{
    internal ShortcutPreset(string name, IReadOnlyDictionary<string, IReadOnlyList<ShortcutBinding>> bindings)
    {
        Name = name;
        Bindings = bindings;
    }

    public string Name { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<ShortcutBinding>> Bindings { get; }
    /** Bindings the preset gives an action; empty when unbound. */
    public IReadOnlyList<ShortcutBinding> For(string id) => Bindings.GetValueOrDefault(id) ?? [];
}

/** The built-in presets. An action with no common binding in an editor stays unbound. */
public static class ShortcutPresets
{
    public const string SunriseStudio = "Sunrise Studio";
    public const string Blender = "Blender";
    public const string Unity = "Unity";
    public const string Maya = "Maya";
    public const string Unreal = "Unreal";
    // The Shortcuts window shows two binding slots per action.
    public const int MaximumBindings = 2;

    private static readonly (string Name, (string Id, string Bindings)[] Rows)[] Tables =
    [
        (SunriseStudio, SunriseRows()),
        (Blender, BlenderRows()),
        (Unity, UnityRows()),
        (Maya, MayaRows()),
        (Unreal, UnrealRows())
    ];

    public static IReadOnlyList<ShortcutPreset> All { get; } = Tables.Select(table => Build(table.Name, table.Rows, null)).ToArray();
    public static ShortcutPreset Default => All[0];

    /** The preset with a name, or null. */
    public static ShortcutPreset? Find(string? name) => All.FirstOrDefault(preset => preset.Name == name);

    /** Lists table mistakes: a missing, repeated or unknown action, or a binding the action refuses. */
    public static IReadOnlyList<string> Problems()
    {
        List<string> problems = [];
        foreach (var (name, rows) in Tables) Build(name, rows, problems);
        return problems;
    }

    /** Studio's own bindings. */
    private static (string Id, string Bindings)[] SunriseRows() =>
    [
        (ShortcutActions.NewProject, "Ctrl+N"),
        (ShortcutActions.OpenProject, "Ctrl+O"),
        (ShortcutActions.SaveProject, "Ctrl+S"),
        (ShortcutActions.Undo, "Ctrl+Z"),
        (ShortcutActions.Redo, "Ctrl+Y"),
        (ShortcutActions.BuildPackages, "Ctrl+Shift+B"),
        (ShortcutActions.BuildAndInstall, ""),
        (ShortcutActions.UpdateRunningGame, ""),
        (ShortcutActions.CreateTexture, ""),
        (ShortcutActions.CloneMaterial, ""),
        (ShortcutActions.CreateStaticProp, ""),
        (ShortcutActions.CreateMissionScript, ""),
        (ShortcutActions.OpenInterfaceList, ""),
        (ShortcutActions.CloneWeapon, ""),
        (ShortcutActions.CloneArmor, ""),
        (ShortcutActions.ClonePlug, ""),
        (ShortcutActions.NavigateBack, "Back click or Alt+Left Arrow"),
        (ShortcutActions.NavigateForward, "Forward click or Alt+Right Arrow"),
        (ShortcutActions.ToggleBrowser, "Ctrl+Space"),
        (ShortcutActions.OpenAiWorkspace, "Ctrl+Shift+A"),
        (ShortcutActions.ToggleViewportRendering, "Ctrl+Shift+R"),
        (ShortcutActions.OpenSettings, "Ctrl+,"),
        (ShortcutActions.OpenHelp, "F1"),
        (ShortcutActions.Pan2D, "Middle drag or Right drag"),
        (ShortcutActions.Zoom2D, "Wheel"),
        (ShortcutActions.FrameSelection2D, "F or Numpad ."),
        (ShortcutActions.FrameAll2D, ""),
        (ShortcutActions.Select2D, "Left click"),
        (ShortcutActions.Delete2D, ""),
        (ShortcutActions.Duplicate2D, ""),
        (ShortcutActions.Orbit3D, "Right drag or Alt+Left drag"),
        (ShortcutActions.Pan3D, "Middle drag or Shift+Right drag"),
        (ShortcutActions.Zoom3D, "Wheel"),
        (ShortcutActions.FrameSelection3D, "F or Numpad ."),
        (ShortcutActions.FrameAll3D, ""),
        (ShortcutActions.Select3D, "Left click"),
        (ShortcutActions.SetCursor3D, ""),
        (ShortcutActions.ToggleCursor3D, ""),
        (ShortcutActions.ToggleTree3D, ""),
        (ShortcutActions.HideSelection3D, ""),
        (ShortcutActions.ShowAll3D, ""),
        (ShortcutActions.TogglePlayer3D, ""),
        (ShortcutActions.SelectTool3D, "Q"),
        (ShortcutActions.MoveTool3D, "W"),
        (ShortcutActions.RotateTool3D, "E"),
        (ShortcutActions.ScaleTool3D, "R")
    ];

    private static (string Id, string Bindings)[] BlenderRows() =>
    [
        (ShortcutActions.NewProject, "Ctrl+N"),
        (ShortcutActions.OpenProject, "Ctrl+O"),
        (ShortcutActions.SaveProject, "Ctrl+S"),
        (ShortcutActions.Undo, "Ctrl+Z"),
        (ShortcutActions.Redo, "Ctrl+Shift+Z"),
        (ShortcutActions.BuildPackages, ""),
        (ShortcutActions.BuildAndInstall, ""),
        (ShortcutActions.UpdateRunningGame, ""),
        (ShortcutActions.CreateTexture, ""),
        (ShortcutActions.CloneMaterial, ""),
        (ShortcutActions.CreateStaticProp, ""),
        (ShortcutActions.CreateMissionScript, ""),
        (ShortcutActions.OpenInterfaceList, ""),
        (ShortcutActions.CloneWeapon, ""),
        (ShortcutActions.CloneArmor, ""),
        (ShortcutActions.ClonePlug, ""),
        (ShortcutActions.NavigateBack, "Back click"),
        (ShortcutActions.NavigateForward, "Forward click"),
        (ShortcutActions.ToggleBrowser, ""),
        (ShortcutActions.OpenAiWorkspace, ""),
        (ShortcutActions.ToggleViewportRendering, ""),
        (ShortcutActions.OpenSettings, ""),
        (ShortcutActions.OpenHelp, "F1"),
        (ShortcutActions.Pan2D, "Middle drag"),
        (ShortcutActions.Zoom2D, "Wheel or Ctrl+Middle drag"),
        (ShortcutActions.FrameSelection2D, "Numpad ."),
        (ShortcutActions.FrameAll2D, "Home"),
        (ShortcutActions.Select2D, "Left click"),
        (ShortcutActions.Delete2D, "X or Delete"),
        (ShortcutActions.Duplicate2D, "Shift+D"),
        (ShortcutActions.Orbit3D, "Middle drag"),
        (ShortcutActions.Pan3D, "Shift+Middle drag"),
        (ShortcutActions.Zoom3D, "Wheel or Ctrl+Middle drag"),
        (ShortcutActions.FrameSelection3D, "Numpad ."),
        (ShortcutActions.FrameAll3D, "Home"),
        (ShortcutActions.Select3D, "Left click"),
        (ShortcutActions.SetCursor3D, ""),
        (ShortcutActions.ToggleCursor3D, ""),
        (ShortcutActions.ToggleTree3D, ""),
        (ShortcutActions.HideSelection3D, "H"),
        (ShortcutActions.ShowAll3D, "Alt+H"),
        (ShortcutActions.TogglePlayer3D, ""),
        (ShortcutActions.SelectTool3D, ""),
        (ShortcutActions.MoveTool3D, ""),
        (ShortcutActions.RotateTool3D, ""),
        (ShortcutActions.ScaleTool3D, "")
    ];

    private static (string Id, string Bindings)[] UnityRows() =>
    [
        (ShortcutActions.NewProject, "Ctrl+N"),
        (ShortcutActions.OpenProject, "Ctrl+O"),
        (ShortcutActions.SaveProject, "Ctrl+S"),
        (ShortcutActions.Undo, "Ctrl+Z"),
        (ShortcutActions.Redo, "Ctrl+Y"),
        (ShortcutActions.BuildPackages, ""),
        (ShortcutActions.BuildAndInstall, ""),
        (ShortcutActions.UpdateRunningGame, ""),
        (ShortcutActions.CreateTexture, ""),
        (ShortcutActions.CloneMaterial, ""),
        (ShortcutActions.CreateStaticProp, ""),
        (ShortcutActions.CreateMissionScript, ""),
        (ShortcutActions.OpenInterfaceList, ""),
        (ShortcutActions.CloneWeapon, ""),
        (ShortcutActions.CloneArmor, ""),
        (ShortcutActions.ClonePlug, ""),
        (ShortcutActions.NavigateBack, "Back click"),
        (ShortcutActions.NavigateForward, "Forward click"),
        (ShortcutActions.ToggleBrowser, ""),
        (ShortcutActions.OpenAiWorkspace, ""),
        (ShortcutActions.ToggleViewportRendering, ""),
        (ShortcutActions.OpenSettings, ""),
        (ShortcutActions.OpenHelp, "F1"),
        (ShortcutActions.Pan2D, "Middle drag"),
        (ShortcutActions.Zoom2D, "Wheel"),
        (ShortcutActions.FrameSelection2D, "F"),
        (ShortcutActions.FrameAll2D, ""),
        (ShortcutActions.Select2D, "Left click"),
        (ShortcutActions.Delete2D, "Delete"),
        (ShortcutActions.Duplicate2D, "Ctrl+D"),
        (ShortcutActions.Orbit3D, "Alt+Left drag"),
        (ShortcutActions.Pan3D, "Middle drag"),
        (ShortcutActions.Zoom3D, "Wheel or Alt+Right drag"),
        (ShortcutActions.FrameSelection3D, "F"),
        (ShortcutActions.FrameAll3D, ""),
        (ShortcutActions.Select3D, "Left click"),
        (ShortcutActions.SetCursor3D, ""),
        (ShortcutActions.ToggleCursor3D, ""),
        (ShortcutActions.ToggleTree3D, ""),
        (ShortcutActions.HideSelection3D, ""),
        (ShortcutActions.ShowAll3D, ""),
        (ShortcutActions.TogglePlayer3D, ""),
        (ShortcutActions.SelectTool3D, ""),
        (ShortcutActions.MoveTool3D, "W"),
        (ShortcutActions.RotateTool3D, "E"),
        (ShortcutActions.ScaleTool3D, "R")
    ];

    private static (string Id, string Bindings)[] MayaRows() =>
    [
        (ShortcutActions.NewProject, "Ctrl+N"),
        (ShortcutActions.OpenProject, "Ctrl+O"),
        (ShortcutActions.SaveProject, "Ctrl+S"),
        (ShortcutActions.Undo, "Ctrl+Z"),
        (ShortcutActions.Redo, "Shift+Z"),
        (ShortcutActions.BuildPackages, ""),
        (ShortcutActions.BuildAndInstall, ""),
        (ShortcutActions.UpdateRunningGame, ""),
        (ShortcutActions.CreateTexture, ""),
        (ShortcutActions.CloneMaterial, ""),
        (ShortcutActions.CreateStaticProp, ""),
        (ShortcutActions.CreateMissionScript, ""),
        (ShortcutActions.OpenInterfaceList, ""),
        (ShortcutActions.CloneWeapon, ""),
        (ShortcutActions.CloneArmor, ""),
        (ShortcutActions.ClonePlug, ""),
        (ShortcutActions.NavigateBack, "Back click"),
        (ShortcutActions.NavigateForward, "Forward click"),
        (ShortcutActions.ToggleBrowser, ""),
        (ShortcutActions.OpenAiWorkspace, ""),
        (ShortcutActions.ToggleViewportRendering, ""),
        (ShortcutActions.OpenSettings, ""),
        (ShortcutActions.OpenHelp, "F1"),
        (ShortcutActions.Pan2D, "Alt+Middle drag"),
        (ShortcutActions.Zoom2D, "Alt+Right drag or Wheel"),
        (ShortcutActions.FrameSelection2D, "F"),
        (ShortcutActions.FrameAll2D, "A"),
        (ShortcutActions.Select2D, "Left click"),
        (ShortcutActions.Delete2D, "Delete"),
        (ShortcutActions.Duplicate2D, "Ctrl+D"),
        (ShortcutActions.Orbit3D, "Alt+Left drag"),
        (ShortcutActions.Pan3D, "Alt+Middle drag"),
        (ShortcutActions.Zoom3D, "Alt+Right drag or Wheel"),
        (ShortcutActions.FrameSelection3D, "F"),
        (ShortcutActions.FrameAll3D, "A"),
        (ShortcutActions.Select3D, "Left click"),
        (ShortcutActions.SetCursor3D, ""),
        (ShortcutActions.ToggleCursor3D, ""),
        (ShortcutActions.ToggleTree3D, ""),
        (ShortcutActions.HideSelection3D, "Ctrl+H"),
        (ShortcutActions.ShowAll3D, ""),
        (ShortcutActions.TogglePlayer3D, ""),
        (ShortcutActions.SelectTool3D, "Q"),
        (ShortcutActions.MoveTool3D, "W"),
        (ShortcutActions.RotateTool3D, "E"),
        (ShortcutActions.ScaleTool3D, "R")
    ];

    private static (string Id, string Bindings)[] UnrealRows() =>
    [
        (ShortcutActions.NewProject, "Ctrl+N"),
        (ShortcutActions.OpenProject, "Ctrl+O"),
        (ShortcutActions.SaveProject, "Ctrl+S"),
        (ShortcutActions.Undo, "Ctrl+Z"),
        (ShortcutActions.Redo, "Ctrl+Y"),
        (ShortcutActions.BuildPackages, ""),
        (ShortcutActions.BuildAndInstall, ""),
        (ShortcutActions.UpdateRunningGame, ""),
        (ShortcutActions.CreateTexture, ""),
        (ShortcutActions.CloneMaterial, ""),
        (ShortcutActions.CreateStaticProp, ""),
        (ShortcutActions.CreateMissionScript, ""),
        (ShortcutActions.OpenInterfaceList, ""),
        (ShortcutActions.CloneWeapon, ""),
        (ShortcutActions.CloneArmor, ""),
        (ShortcutActions.ClonePlug, ""),
        (ShortcutActions.NavigateBack, "Back click"),
        (ShortcutActions.NavigateForward, "Forward click"),
        (ShortcutActions.ToggleBrowser, "Ctrl+Space"),
        (ShortcutActions.OpenAiWorkspace, "Ctrl+Shift+A"),
        (ShortcutActions.ToggleViewportRendering, "Ctrl+Shift+R"),
        (ShortcutActions.OpenSettings, "Ctrl+,"),
        (ShortcutActions.OpenHelp, "F1"),
        (ShortcutActions.Pan2D, "Right drag"),
        (ShortcutActions.Zoom2D, "Wheel"),
        (ShortcutActions.FrameSelection2D, ""),
        (ShortcutActions.FrameAll2D, "Home"),
        (ShortcutActions.Select2D, "Left click"),
        (ShortcutActions.Delete2D, "Delete"),
        (ShortcutActions.Duplicate2D, "Ctrl+D"),
        (ShortcutActions.Orbit3D, "Alt+Left drag"),
        (ShortcutActions.Pan3D, "Middle drag"),
        (ShortcutActions.Zoom3D, "Alt+Right drag or Wheel"),
        (ShortcutActions.FrameSelection3D, "F"),
        (ShortcutActions.FrameAll3D, ""),
        (ShortcutActions.Select3D, "Left click"),
        (ShortcutActions.SetCursor3D, ""),
        (ShortcutActions.ToggleCursor3D, ""),
        (ShortcutActions.ToggleTree3D, ""),
        (ShortcutActions.HideSelection3D, "H"),
        (ShortcutActions.ShowAll3D, "Ctrl+H"),
        (ShortcutActions.TogglePlayer3D, ""),
        (ShortcutActions.SelectTool3D, "Q"),
        (ShortcutActions.MoveTool3D, "W"),
        (ShortcutActions.RotateTool3D, "E"),
        (ShortcutActions.ScaleTool3D, "R")
    ];

    /** Parses one table; a bad row is skipped at run time and reported when problems is set. */
    private static ShortcutPreset Build(string name, (string Id, string Bindings)[] rows, List<string>? problems)
    {
        var bindings = new Dictionary<string, IReadOnlyList<ShortcutBinding>>(StringComparer.Ordinal);
        foreach (var (id, text) in rows)
        {
            if (ShortcutActions.Find(id) is not { } action) { problems?.Add($"{name}: unknown action {id}"); continue; }
            if (bindings.ContainsKey(id)) problems?.Add($"{name}: {id} is listed twice");
            if (!ShortcutMap.TryParseList(text, action, out var parsed))
            {
                problems?.Add($"{name}: {id} has invalid bindings '{text}'");
                parsed = [];
            }
            bindings[id] = parsed;
        }
        foreach (var action in ShortcutActions.All.Where(action => !bindings.ContainsKey(action.Id)))
        {
            problems?.Add($"{name}: {action.Id} is not listed");
            bindings[action.Id] = [];
        }
        return new(name, bindings);
    }
}
