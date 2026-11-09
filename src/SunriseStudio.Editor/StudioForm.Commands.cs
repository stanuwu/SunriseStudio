/**
 * File: Commands, dialogs and file pickers of StudioForm: menu actions, the project dialog, open/save helpers and
 * install-safety checks.
 */
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Projects;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    private bool addDialog;
    private string addPath = "", addName = "", addPackageId = "0AA0", addReference = "80800000";
    private int addKind, addType = 8, addSubType;
    private StudioDocument? cloneSource;

    // The menu bar's menus (StudioForm.MenuBar.cs), in the mockup's order: File, Edit, View, Assets, Map, Mission, Build,
    // AI, Help. Every item is an existing command (EditorCommands ids in StudioForm.Ai.Commands.cs run the same code) and
    // is disabled while it cannot run. The » menu reuses them as submenus.

    private void DrawFileMenu()
    {
        if (!BeginBarMenu("File")) return;
        if (UiAuto.MenuItem("New project", ShortcutInput.Text(ShortcutActions.NewProject), false, !workspace.IsBusy)) projectDialog = true;
        if (UiAuto.MenuItem("Open project...", ShortcutInput.Text(ShortcutActions.OpenProject), false, !workspace.IsBusy)) OpenProjectDialog();
        if (UiAuto.BeginMenu("Recent projects", settings.RecentProjects.Count != 0 && !workspace.IsBusy))
        {
            SolTheme.PopupShadow();
            foreach (string path in settings.RecentProjects.ToArray())
            {
                ImGui.PushID(path);
                // A moved or deleted project stays listed but cannot be opened.
                bool exists = IconChecks.Exists(path);
                if (UiAuto.MenuItem(Path.GetFileName(Path.GetDirectoryName(path)) ?? path, "", false, exists)) OpenProject(path);
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(exists ? path : path + " (missing)");
                ImGui.PopID();
            }
            UiAuto.EndMenu();
        }
        if (UiAuto.MenuItem(missionGraphActive ? "Save script" : "Save project",
            ShortcutInput.Text(ShortcutActions.SaveProject), false, CanSaveActive))
            SaveActiveDocument();
        if (UiAuto.MenuItem("Close project", "", false, workspace.CanEdit)) workspace.Run(() =>
        { if (ConfirmMissionDraft()) workspace.CloseProject(); });
        if (UiAuto.MenuItem("Close world", "", false, workspace.World is not null && !workspace.IsBusy)) workspace.Run(workspace.CloseWorld);
        ImGui.Separator();
        // The mockup keeps the mod's details with the project (File); build.mod_details is the same command.
        if (UiAuto.MenuItem("Mod details...", "", false, workspace.CanEdit)) workspace.Run(OpenModDialog);
        if (UiAuto.MenuItem("Sunrise location...", "", false, !workspace.IsBusy)) settingsDialog = true;
        if (UiAuto.MenuItem("Cache...", "", false, !workspace.IsBusy)) cacheDialog = true;
        if (UiAuto.MenuItem("Settings...", ShortcutInput.Text(ShortcutActions.OpenSettings))) preferencesOpen = true;
        ImGui.Separator();
        if (UiAuto.MenuItem("Exit")) BeginInvoke(Close);
        EndBarMenu();
    }

    private void DrawEditMenu()
    {
        if (!BeginBarMenu("Edit")) return;
        if (UiAuto.MenuItem("Undo", ShortcutInput.Text(ShortcutActions.Undo), false, CanUndoActive)) UndoActive();
        if (UiAuto.MenuItem("Redo", ShortcutInput.Text(ShortcutActions.Redo), false, CanRedoActive)) RedoActive();
        ImGui.Separator();
        if (UiAuto.MenuItem("Convert placement deltas to object edits", "", false,
            workspace.CanEdit && workspace.Project?.Changes.Count > 0))
            workspace.Run(() => workspace.ConvertPlacementDeltas());
        ImGui.Separator();
        if (UiAuto.MenuItem("Shortcuts...")) shortcutsDialog = true;
        if (UiAuto.MenuItem("Settings...", ShortcutInput.Text(ShortcutActions.OpenSettings))) preferencesOpen = true;
        EndBarMenu();
    }

    private void DrawAssetsMenu()
    {
        if (!BeginBarMenu("Assets")) return;
        if (UiAuto.MenuItem("Export full asset to Blender...", "", false, CanExportBundle)) ExportFullAsset();
        MapBundleTooltip();
        if (UiAuto.MenuItem("Export world to Blender...", "", false,
            !workspace.IsBusy && workspace.World is not null)) ExportWorldScene();
        if (UiAuto.MenuItem("Import Blender asset...", "", false, workspace.CanEdit)) BeginBundleImport();
        if (UiAuto.MenuItem("Import skinned model...", "", false, CanReplaceSkin)) BeginSkinImport();
        ImGui.Separator();
        if (UiAuto.MenuItem("Export...", "", false, workspace.Document is not null && !workspace.IsBusy)) ExportSelected();
        if (UiAuto.MenuItem("Export animation...", "", false,
            AssetPreviewViewport?.Animation is { Source.Binding: not null, Clip: not null })) ExportAnimation();
        if (UiAuto.MenuItem("Export editable animation...", "", false, SelectedClipTag() is not null
            && !workspace.IsBusy)) ExportAnimationControls();
        if (UiAuto.MenuItem("Import animation controls...", "", false, SelectedClipTag() is not null
            && workspace.CanEdit)) ImportAnimationControls();
        if (UiAuto.MenuItem("Export material to Blender...", "", false,
            workspace.Document?.Entry.Reference == 0x808071E8 && !workspace.IsBusy)) ExportMaterialBundle();
        if (UiAuto.MenuItem("Export collision...", "", false, CanExportCollision)) ExportCollision();
        if (UiAuto.MenuItem("Import collision...", "", false, workspace.CanEdit && CanExportCollision)) ImportCollision();
        if (UiAuto.MenuItem("Import as delta...", "", false, workspace.CanEdit && workspace.Document is not null)) ImportDelta();
        if (UiAuto.MenuItem("Add asset...", "", false, workspace.CanEdit)) addDialog = true;
        if (UiAuto.MenuItem("Edit perk behavior...", "", false,
            workspace.Catalog is not null && !workspace.IsBusy)) OpenPerkBehaviorPicker();
        ImGui.Separator();
        if (UiAuto.MenuItem("Resolve asset names", "", false, workspace.Catalog is not null && !workspace.IsBusy))
            workspace.Run(workspace.IndexNames);
        EndBarMenu();
    }

    /** Map: placing content in the open map, the live game link and the retail maps. */
    private void DrawMapMenu()
    {
        if (!BeginBarMenu("Map")) return;
        if (UiAuto.BeginMenu("Place"))
        {
            SolTheme.PopupShadow();
            DrawPlaceMenuItems();
            UiAuto.EndMenu();
        }
        ImGui.Separator();
        if (UiAuto.MenuItem("Live")) focusLive = true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("The link to the running game: player position, live objects and reloads");
        if (UiAuto.MenuItem("Retail workspace (live build, maps, porting)")) retailOpen = true;
        EndBarMenu();
    }

    /** Mission: new scripts and the mission graph. */
    private void DrawMissionMenu()
    {
        if (!BeginBarMenu("Mission")) return;
        if (UiAuto.MenuItem("New mission script...", ShortcutInput.Text(ShortcutActions.CreateMissionScript), false,
            workspace.Catalog is not null && !workspace.IsBusy))
            NewMissionScript();
        if (UiAuto.MenuItem("Graph")) focusMissionGraph = true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("The mission graph and node graph editor");
        EndBarMenu();
    }

    private void DrawBuildMenu()
    {
        if (!BeginBarMenu("Build")) return;
        if (UiAuto.MenuItem("Build packages", ShortcutInput.Text(ShortcutActions.BuildPackages), false, workspace.CanEdit))
            workspace.Run(() => workspace.Build());
        if (UiAuto.MenuItem("Build and install", ShortcutInput.Text(ShortcutActions.BuildAndInstall), false, workspace.CanEdit)) InstallMod();
        if (UiAuto.MenuItem("Update running game", ShortcutInput.Text(ShortcutActions.UpdateRunningGame), false, workspace.CanEdit))
            UpdateRunningGame();
        if (UiAuto.MenuItem("Build to folder...", "", false, workspace.CanEdit))
            PickFolder("Build output", folder => workspace.Build(folder));
        ImGui.Separator();
        DrawPackagingMenuItems();
        ImGui.Separator();
        if (UiAuto.MenuItem("Local Server")) focusLocalServer = true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("The local test server: start, stop, deploy and its log");
        EndBarMenu();
    }

    private void DrawViewMenu()
    {
        if (!BeginBarMenu("View")) return;
        bool wire = viewport?.Wireframe ?? false, grid = viewport?.ShowGrid ?? true;
        if (UiAuto.MenuItem("Wireframe", "", ref wire)) viewport!.Wireframe = wire;
        if (UiAuto.MenuItem("Grid", "", ref grid)) viewport!.ShowGrid = grid;
        if (UiAuto.BeginMenu("Lighting"))
        {
            SolTheme.PopupShadow();
            int lighting = Math.Clamp((int)settings.LightingMode, 0, LightingNames.Length - 1);
            for (int i = 0; i < LightingNames.Length; i++)
                if (UiAuto.MenuItem(LightingNames[i], "", i == lighting) && i != lighting) SetLighting(i);
            UiAuto.EndMenu();
        }
        DrawRenderingMenuItem();
        ImGui.Separator();
        if (UiAuto.MenuItem("Frame selection", ShortcutInput.Text(ShortcutActions.FrameSelection3D), false,
            viewport?.Camera.PivotFree == true)) viewport?.FocusSelection();
        if (UiAuto.MenuItem("Set cursor to selection", ShortcutInput.Text(ShortcutActions.SetCursor3D), false,
            (viewport?.SelectedObject is not null || viewport?.SelectedCollision >= 0) && viewport!.Camera.PivotFree))
            viewport?.SetCursorToSelection();
        ImGui.Separator();
        // The mockup's View > Panels group; the old Window menu's items live here now.
        MenuCaption("Panels");
        if (UiAuto.MenuItem("Content Browser drawer", ShortcutInput.Text(ShortcutActions.ToggleBrowser))) ToggleBrowserDrawer();
        if (UiAuto.MenuItem("Items", "", false, workspace.Catalog is not null)) ShowItems();
        if (UiAuto.MenuItem("Data explorer (decomp, tags, strings)")) dataExplorerOpen = true;
        DrawContentEditorsMenuItem();
        DrawLayoutMenu();
        if (UiAuto.BeginMenu("Layouts"))
        {
            SolTheme.PopupShadow();
            foreach (string name in StudioLayout.PresetNames)
                if (UiAuto.MenuItem(name)) ApplyLayoutPreset(name);
            UiAuto.EndMenu();
        }
        EndBarMenu();
    }

    /** AI: the separate Solforge AI app, its in-editor panel and the MCP call log. */
    private void DrawAiMenu()
    {
        if (!BeginBarMenu("AI")) return;
        // Launching or focusing a GUI app stays the user's: UI automation cannot click it (as the toolbar pill).
        UiAuto.PushBlocked();
        try
        {
            if (UiAuto.MenuItem("Solforge AI", ShortcutInput.Text(ShortcutActions.OpenAiWorkspace))) LaunchOrFocusSolforgeAi();
            StudioUi.Tip("Launch the Solforge AI app for this project, or bring it to the front.");
        }
        finally { UiAuto.PopBlocked(); }
        if (UiAuto.MenuItem("Solforge AI panel")) OpenAiWorkspace();
        if (UiAuto.MenuItem("Output Log", "", layout.Find(StudioPanelKind.Output) is not null || IsPoppedOut(StudioPanelKind.Output)))
            ShowPanelFromMenu(StudioPanelKind.Output);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Status, notices, errors and the MCP call log (AI LOG)");
        EndBarMenu();
    }

    /** Shows a dock panel as View > Panels does (a popped-out one comes to the front). */
    private void ShowPanelFromMenu(StudioPanelKind kind)
    {
        if (ShowPopout(kind, true)) return;
        var shown = layout.Show(kind);
        if (shown.Collapsed == true) shown.Collapsed = false;
        maximizedPanel = null;
        layoutDirty = true;
    }

    private void DrawHelpMenu()
    {
        if (!BeginBarMenu("Help")) return;
        DrawHelpMenuItems();
        if (UiAuto.MenuItem("Third-party notices")) noticesDialog = true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Licenses of the libraries and code Studio includes");
        // About: Solforge is a fork; the upstream author keeps the credit.
        ImGui.Separator();
        ImGui.TextDisabled($"Solforge {StudioVersion.Text}");
        ImGui.TextDisabled(AboutCredit);
        EndBarMenu();
    }

    private static readonly string[] LightingNames = ["Studio", "Sky", "Ambient"];

    /** Draws whichever modal dialogs are open. */
    private void DrawDialogs()
    {
        DrawWideHashes();
        DrawTagDiff();
        DrawItemSources();
        DrawConfigBindings();
        if (setup || settingsDialog) ImGui.OpenPopup("Sunrise location");
        if (projectDialog) { ImGui.OpenPopup("New project"); projectDialog = false; }
        if (addDialog) { ImGui.OpenPopup("Add asset"); addDialog = false; }
        if (cacheDialog) { ImGui.OpenPopup("Disk cache"); cacheDialog = false; cacheStats = null; }
        if (modDialog) { ImGui.OpenPopup("Mod details"); modDialog = false; }
        if (shortcutsDialog) { ImGui.OpenPopup("Shortcuts"); shortcutsDialog = false; }
        if (noticesDialog) { ImGui.OpenPopup("Third-party notices"); noticesDialog = false; }
        DrawLocationDialog(); DrawProjectDialog(); DrawAddDialog(); DrawCacheDialog(); DrawBundleDialogs(); DrawModDialog();
        DrawShortcutsDialog(); DrawNoticesDialog(); DrawHelpDialog(); DrawPreferences(); DrawDataExplorer(); DrawRetailWorkspace();
        DrawContentEditors();
        DrawCreation();
    }

    /** Centers the next modal window with the given width. */
    private void CenterModal(float width)
    {
        ImGui.SetNextWindowPos(new(ClientSize.Width / 2f, ClientSize.Height / 2f),
            ImGuiCond.Appearing, new(.5f));
        ImGui.SetNextWindowSize(new(width * scale, 0), ImGuiCond.Appearing);
    }

    /** The installation folder and key source; setup, which has nothing to go back to, has no Cancel. */
    private void DrawLocationDialog()
    {
        CenterModal(600);
        if (!SolTheme.BeginDialog("Sunrise location", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextUnformatted("Installation folder");
        ImGui.SetNextItemWidth(465 * scale);
        UiAuto.InputText("##install", ref location, 4096);
        ImGui.SameLine();
        if (UiAuto.Button("Browse")) PickFolder("Sunrise installation", path => location = path);
        if (UiAuto.CollapsingHeader("Package key source"))
        {
            ImGui.SetNextItemWidth(465 * scale);
            UiAuto.InputTextWithHint("##keySource", "Automatic", ref keySource, 4096);
            StudioUi.Tip("Empty: read the keys from the installation");
            ImGui.SameLine();
            if (UiAuto.Button("Browse##keys"))
                PickFile("Package key export|*.bin|Executable|*.exe|All files|*.*", path => keySource = path);
        }
        if (workspace.Error.Length != 0) ImGui.TextWrapped(workspace.Error);
        ImGui.Spacing();
        if (UiAuto.Button("Open Sunrise", new(150 * scale, 0))) workspace.Run(() =>
        {
            string root = StudioSettings.ResolveGameRoot(location);
            if (!ConfirmMissionDraft()) return;
            workspace.OpenInstallation(root, keySource);
            settings.SunrisePath = root; settings.KeySource = keySource;
            SaveSettings();
            setup = settingsDialog = false;
            ImGui.CloseCurrentPopup();
        });
        if (!setup)
        {
            ImGui.SameLine();
            if (UiAuto.Button("Cancel")) { settingsDialog = false; ImGui.CloseCurrentPopup(); }
        }
        ImGui.EndPopup();
    }

    /** Draws the new/open project dialog. */
    private void DrawProjectDialog()
    {
        CenterModal(600);
        if (!SolTheme.BeginDialog("New project", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextUnformatted("Name"); ImGui.SetNextItemWidth(-1);
        UiAuto.InputText("##projectName", ref projectName, 128);
        ImGui.TextUnformatted("Parent folder");
        ImGui.SetNextItemWidth(465 * scale); UiAuto.InputText("##projectFolder", ref projectFolder, 4096);
        ImGui.SameLine();
        if (UiAuto.Button("Browse")) PickFolder("Projects folder", path => projectFolder = path);
        if (workspace.Error.Length != 0) ImGui.TextWrapped(workspace.Error);
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(projectFolder)
            || workspace.IsBusy);
        if (UiAuto.Button("Create project", new(150 * scale, 0))) workspace.Run(() =>
        {
            string name = projectName.Trim();
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
                throw new IOException("Choose a valid project name.");
            string path = Path.Combine(Path.GetFullPath(projectFolder), name);
            EnsureOutsideInstall(path);
            if (!ConfirmMissionDraft()) return;
            workspace.CreateProject(path, name);
            RememberProject();
            ImGui.CloseCurrentPopup();
        });
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (UiAuto.Button("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    /** Adds a file as a source asset or a native entry; with a clone source it copies that asset instead. */
    private void DrawAddDialog()
    {
        CenterModal(600);
        if (!SolTheme.BeginDialog("Add asset", ImGuiWindowFlags.AlwaysAutoResize)) return;
        if (cloneSource is { } source) { ImGui.TextDisabled("Source"); ImGui.TextWrapped(source.Name); }
        else
        {
            ImGui.TextUnformatted("File");
            ImGui.SetNextItemWidth(465 * scale); UiAuto.InputText("##addPath", ref addPath, 4096);
            ImGui.SameLine();
            if (UiAuto.Button("Browse")) PickFile("All assets|*.*", path =>
            { addPath = path; addName = Path.GetFileName(path); });
        }
        ImGui.TextUnformatted("Name"); ImGui.SetNextItemWidth(-1);
        UiAuto.InputText("##addName", ref addName, 128);
        ImGui.SetNextItemWidth(-1);
        if (cloneSource is null) UiAuto.Combo("##assetKind", ref addKind, ["Source asset", "Native package entry"], 2);
        if (addKind == 1)
        {
            ImGui.SetNextItemWidth(140 * scale); UiAuto.InputText("Package ID", ref addPackageId, 4, ImGuiInputTextFlags.CharsHexadecimal);
            if (cloneSource is null)
            {
                ImGui.SetNextItemWidth(140 * scale); UiAuto.InputText("Class / reference", ref addReference, 8, ImGuiInputTextFlags.CharsHexadecimal);
                ImGui.SetNextItemWidth(140 * scale); UiAuto.InputInt("Type", ref addType);
                ImGui.SetNextItemWidth(140 * scale); UiAuto.InputInt("Subtype", ref addSubType);
            }
            else ImGui.TextDisabled("Stored as a delta from the original");
        }
        else ImGui.TextDisabled("Source file; excluded from package builds");
        if (workspace.Error.Length != 0) ImGui.TextWrapped(workspace.Error);
        ImGui.BeginDisabled(!workspace.CanEdit || cloneSource is null && !IconChecks.Exists(addPath) || string.IsNullOrWhiteSpace(addName));
        if (UiAuto.Button(cloneSource is null ? "Add asset" : "Create copy", new(150 * scale, 0))) workspace.Run(() =>
        {
            int kind = addKind;
            string path = addPath, name = addName;
            var original = cloneSource;
            ushort packageId = kind == 0 ? (ushort)0 : ushort.Parse(addPackageId, NumberStyles.HexNumber);
            NativeAssetMetadata? metadata = null;
            if (kind == 1)
            {
                if (addType is < 0 or > 127 || addSubType is < 0 or > 7)
                    throw new InvalidDataException("Type or subtype is out of range.");
                if (workspace.Catalog!.Entries.Any(entry => entry.PackageId == packageId))
                    throw new InvalidDataException("Choose an unused package ID.");
                metadata = new(packageId, original?.Entry.Reference ?? uint.Parse(addReference, NumberStyles.HexNumber),
                    original?.Entry.TypeInfo ?? (uint)((addType << 9) | (addSubType << 6)), name);
            }
            workspace.ImportAsset((project, catalog, token) => new([original is not null
                ? new(name, original.Bytes, metadata!, original.Entry.TagHash)
                : new ProjectAssetAddition(name, File.ReadAllBytes(path), metadata)]));
            cloneSource = null;
            ImGui.CloseCurrentPopup();
        });
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (UiAuto.Button("Cancel")) { cloneSource = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    /** Starts duplicating the selected object. */
    private void BeginDuplicate()
    {
        if (workspace.Document is not { AddedAssetId: null } source) return;
        // A texture's header names its pixel buffer, so a byte copy would share it.
        if (source.Entry.Type == 32) { workspace.Error = "A copy would share its pixels."; return; }
        cloneSource = source;
        addName = source.Name + " copy";
        addKind = 1;
        addDialog = true;
    }

    /** Asks for a project file and opens it. */
    private void OpenProjectDialog() => PickFile("Sunrise project|sunrise.project.json", OpenProject);

    /** Opens a project file and loads it into the workspace. */
    private void OpenProject(string path) => workspace.Run(() =>
    {
        if (!ConfirmMissionDraft()) return;
        workspace.OpenProject(path);
        RememberProject();
    });

    /** Context changes finish the draft decision before switching its owning project. */
    private bool ConfirmMissionDraft()
    {
        if (!missionGraph.IsDirty || options.Smoke) return true;
        bool previous = nativeDialog;
        nativeDialog = true;
        try
        {
            var answer = MessageBox.Show(this,
                missionGraph.CanSave ? "Save mission changes before continuing?" : "Discard the unsaved mission draft?",
                "Mission Graph", missionGraph.CanSave ? MessageBoxButtons.YesNoCancel : MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return false;
            if (answer == DialogResult.Yes)
            {
                missionGraph.SaveCurrent(workspace);
                if (!missionGraph.IsDirty) return true;
                focusMissionGraph = true; return false;
            }
            missionGraph.DiscardDraft();
            return true;
        }
        finally { nativeDialog = previous; }
    }

    /** Remembers the open project in the recent-project list. */
    private void RememberProject()
    {
        settings.RememberProject(workspace.Project!.ManifestPath);
        SaveSettings();
        Text = $"{workspace.Project.Name} - Solforge";
    }

    /** Shows a folder picker and calls back with the chosen folder. */
    private void PickFolder(string title, Action<string> selected)
    {
        if (AiFileDialog.Take(folder: true) is { } chosen) { workspace.Run(() => selected(chosen)); return; }
        nativeDialog = true;
        try
        {
            using var dialog = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) workspace.Run(() => selected(dialog.SelectedPath));
        }
        finally { nativeDialog = false; }
    }

    /** Shows an open-file picker and calls back with the chosen file. */
    private void PickFile(string filter, Action<string> selected)
    {
        if (AiFileDialog.Take() is { } chosen)
        {
            // A missing file does nothing (run_command checked it; an armed ui_invoke path may be stale).
            if (File.Exists(chosen)) workspace.Run(() => selected(chosen));
            return;
        }
        nativeDialog = true;
        try
        {
            using var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) workspace.Run(() => selected(dialog.FileName));
        }
        finally { nativeDialog = false; }
    }

    /** Shows a save-file picker and calls back with the chosen path. */
    private void SaveFile(string filter, string name, Action<string> save)
    {
        if (AiFileDialog.Take() is { } chosen)
        {
            workspace.Run(() => { EnsureExternalExport(chosen); save(chosen); });
            return;
        }
        nativeDialog = true;
        try
        {
            var invalid = Path.GetInvalidFileNameChars().ToHashSet();
            name = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
            using var dialog = new SaveFileDialog { Filter = filter, FileName = name, AddExtension = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) workspace.Run(() =>
            {
                EnsureExternalExport(dialog.FileName);
                save(dialog.FileName);
            });
        }
        finally { nativeDialog = false; }
    }

    /** Exported originals stay outside delta projects and the installed game. */
    private void EnsureExternalExport(string path)
    {
        EnsureOutsideInstall(path);
        if (workspace.Project is { } project && IsWithin(path, Path.GetDirectoryName(project.ManifestPath)!))
            throw new IOException("Export to a working folder outside the project.");
    }

    /** Throws when a path is inside the game install (Solforge never writes there). */
    private void EnsureOutsideInstall(string path)
    {
        RejectLinks(path);
        if (workspace.Catalog is { } catalog && IsWithin(path, catalog.GameRoot))
            throw new IOException("Choose a folder outside the Sunrise installation.");
    }

    /** Throws when the path is a symbolic link or junction. */
    private static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Choose a path without links or junctions.");
    }

    /** Whether a path lies inside a folder. */
    private static bool IsWithin(string path, string folder)
    {
        string full = Path.GetFullPath(path), root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /** Opens a folder in the file explorer. */
    private static void OpenFolder(string path) =>
        Process.Start(new ProcessStartInfo { FileName = Path.GetFullPath(path), UseShellExecute = true });
}
