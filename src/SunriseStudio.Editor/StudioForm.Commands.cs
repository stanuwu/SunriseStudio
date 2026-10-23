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

    /** The main menu bar; each item is disabled while its command cannot run. */
    private void DrawMenu()
    {
        if (!ImGui.BeginMainMenuBar()) return;
        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("New project", ShortcutInput.Text(ShortcutActions.NewProject), false, !workspace.IsBusy)) projectDialog = true;
            if (ImGui.MenuItem("Open project...", ShortcutInput.Text(ShortcutActions.OpenProject), false, !workspace.IsBusy)) OpenProjectDialog();
            if (ImGui.BeginMenu("Recent projects", settings.RecentProjects.Count != 0 && !workspace.IsBusy))
            {
                foreach (string path in settings.RecentProjects.ToArray())
                {
                    ImGui.PushID(path);
                    // A moved or deleted project stays listed but cannot be opened.
                    bool exists = IconChecks.Exists(path);
                    if (ImGui.MenuItem(Path.GetFileName(Path.GetDirectoryName(path)), "", false, exists)) OpenProject(path);
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip(exists ? path : path + " (missing)");
                    ImGui.PopID();
                }
                ImGui.EndMenu();
            }
            if (ImGui.MenuItem("New mission script...", ShortcutInput.Text(ShortcutActions.CreateMissionScript), false,
                workspace.Catalog is not null && !workspace.IsBusy))
                NewMissionScript();
            if (ImGui.MenuItem(missionGraphActive ? "Save script" : "Save project",
                ShortcutInput.Text(ShortcutActions.SaveProject), false, CanSaveActive))
                SaveActiveDocument();
            if (ImGui.MenuItem("Close project", "", false, workspace.CanEdit)) workspace.Run(() =>
            { if (ConfirmMissionDraft()) workspace.CloseProject(); });
            if (ImGui.MenuItem("Close world", "", false, workspace.World is not null && !workspace.IsBusy)) workspace.Run(workspace.CloseWorld);
            ImGui.Separator();
            if (ImGui.MenuItem("Sunrise location...", "", false, !workspace.IsBusy)) settingsDialog = true;
            if (ImGui.MenuItem("Cache...", "", false, !workspace.IsBusy)) cacheDialog = true;
            ImGui.Separator();
            if (ImGui.MenuItem("Exit")) BeginInvoke(Close);
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Edit"))
        {
            if (ImGui.MenuItem("Undo", ShortcutInput.Text(ShortcutActions.Undo), false, CanUndoActive)) UndoActive();
            if (ImGui.MenuItem("Redo", ShortcutInput.Text(ShortcutActions.Redo), false, CanRedoActive)) RedoActive();
            ImGui.Separator();
            if (ImGui.MenuItem("Convert placement deltas to object edits", "", false,
                workspace.CanEdit && workspace.Project?.Changes.Count > 0))
                workspace.Run(() => workspace.ConvertPlacementDeltas());
            ImGui.Separator();
            if (ImGui.MenuItem("Shortcuts...")) shortcutsDialog = true;
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Assets"))
        {
            if (ImGui.MenuItem("Export full asset to Blender...", "", false, CanExportBundle)) ExportFullAsset();
            MapBundleTooltip();
            if (ImGui.MenuItem("Export world to Blender...", "", false,
                !workspace.IsBusy && workspace.World is not null)) ExportWorldScene();
            if (ImGui.MenuItem("Import Blender asset...", "", false, workspace.CanEdit)) BeginBundleImport();
            if (ImGui.MenuItem("Import skinned model...", "", false, CanReplaceSkin)) BeginSkinImport();
            ImGui.Separator();
            if (ImGui.MenuItem("Export...", "", false, workspace.Document is not null && !workspace.IsBusy)) ExportSelected();
            if (ImGui.MenuItem("Export animation...", "", false,
                AssetPreviewViewport?.Animation is { Source.Binding: not null, Clip: not null })) ExportAnimation();
            if (ImGui.MenuItem("Export editable animation...", "", false, SelectedClipTag() is not null
                && !workspace.IsBusy)) ExportAnimationControls();
            if (ImGui.MenuItem("Import animation controls...", "", false, SelectedClipTag() is not null
                && workspace.CanEdit)) ImportAnimationControls();
            if (ImGui.MenuItem("Export material to Blender...", "", false,
                workspace.Document?.Entry.Reference == 0x808071E8 && !workspace.IsBusy)) ExportMaterialBundle();
            if (ImGui.MenuItem("Export collision...", "", false, CanExportCollision)) ExportCollision();
            if (ImGui.MenuItem("Import collision...", "", false, workspace.CanEdit && CanExportCollision)) ImportCollision();
            if (ImGui.MenuItem("Import as delta...", "", false, workspace.CanEdit && workspace.Document is not null)) ImportDelta();
            if (ImGui.MenuItem("Add asset...", "", false, workspace.CanEdit)) addDialog = true;
            if (ImGui.MenuItem("Edit perk behavior...", "", false,
                workspace.Catalog is not null && !workspace.IsBusy)) OpenPerkBehaviorPicker();
            ImGui.Separator();
            if (ImGui.MenuItem("Resolve asset names", "", false, workspace.Catalog is not null && !workspace.IsBusy))
                workspace.Run(workspace.IndexNames);
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Build"))
        {
            if (ImGui.MenuItem("Build packages", ShortcutInput.Text(ShortcutActions.BuildPackages), false, workspace.CanEdit))
                workspace.Run(() => workspace.Build());
            if (ImGui.MenuItem("Build and install", ShortcutInput.Text(ShortcutActions.BuildAndInstall), false, workspace.CanEdit)) InstallMod();
            if (ImGui.MenuItem("Update running game", ShortcutInput.Text(ShortcutActions.UpdateRunningGame), false, workspace.CanEdit))
                UpdateRunningGame();
            if (ImGui.MenuItem("Build to folder...", "", false, workspace.CanEdit))
                PickFolder("Build output", folder => workspace.Build(folder));
            ImGui.Separator();
            if (ImGui.MenuItem("Mod details...", "", false, workspace.CanEdit)) workspace.Run(OpenModDialog);
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("View"))
        {
            if (ImGui.MenuItem("Mission Graph")) focusMissionGraph = true;
            if (ImGui.MenuItem("Live")) focusLive = true;
            if (ImGui.MenuItem("Items", "", false, workspace.Catalog is not null)) ShowItems();
            ImGui.Separator();
            if (ImGui.MenuItem("Frame selection", ShortcutInput.Text(ShortcutActions.FrameSelection3D), false,
                viewport?.Camera.PivotFree == true)) viewport?.FocusSelection();
            if (ImGui.MenuItem("Set cursor to selection", ShortcutInput.Text(ShortcutActions.SetCursor3D), false,
                (viewport?.SelectedObject is not null || viewport?.SelectedCollision >= 0) && viewport!.Camera.PivotFree))
                viewport?.SetCursorToSelection();
            bool wire = viewport?.Wireframe ?? false, grid = viewport?.ShowGrid ?? true;
            if (ImGui.MenuItem("Wireframe", "", ref wire)) viewport!.Wireframe = wire;
            if (ImGui.MenuItem("Grid", "", ref grid)) viewport!.ShowGrid = grid;
            DrawLayoutMenu();
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Help"))
        {
            if (ImGui.MenuItem("Licenses")) noticesDialog = true;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Studio's license (GPL 3.0) and the licenses of what it includes");
            ImGui.EndMenu();
        }
        chrome.DrawButtons(ImGui.GetFrameHeight());
        ImGui.EndMainMenuBar();
    }

    /** Pixels from a text line's top to its baseline in the current font. */
    private static float FontAscent() => ImGui.GetFont().Ascent * ImGui.GetFontSize() / ImGui.GetFont().FontSize;

    /** Logo, title, navigation, project actions and shading controls, centred on one line. */
    private void DrawToolbar(float top, float height)
    {
        BeginPanel("Toolbar", new(0, top), new(ClientSize.Width, height), ImGuiWindowFlags.NoScrollbar);
        // Every item on the row is centred on the toolbar's middle line.
        ImGui.PushFont(gui!.TitleFont);
        int mark = (int)MathF.Round(ImGui.GetTextLineHeight() * 1.2f);
        if (toolbarLogo?.Width != mark && renderer is not null)
        {
            toolbarLogo?.Dispose();
            toolbarLogo = StudioBrand.CreateTexture(renderer, mark);
        }
        if (toolbarLogo is { } logo)
        {
            // Whole pixels keep the 1:1 logo from being resampled again.
            ImGui.SetCursorPosY((height - mark) / 2);
            ImGui.SetCursorScreenPos(Vector2.Round(ImGui.GetCursorScreenPos()));
            ImGui.Image(logo.ImGuiId, new(mark));
            ImGui.SameLine(0, 8 * scale);
        }
        // SameLine re-applies the line's text offset, so each word sets its own top to share one baseline.
        float titleTop = (height - ImGui.GetTextLineHeight()) / 2;
        float baseline = titleTop + FontAscent();
        ImGui.SetCursorPosY(titleTop);
        ImGui.TextColored(StudioTheme.Accent, "SUNRISE");
        ImGui.SameLine(0, ImGui.CalcTextSize(" ").X);
        ImGui.SetCursorPosY(titleTop);
        ImGui.TextUnformatted("STUDIO");
        ImGui.PopFont();
        ImGui.SameLine(0, 6 * scale);
        ImGui.SetCursorPosY(baseline - FontAscent());
        ImGui.TextDisabled(StudioVersion.Text);
        float brandEnd = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X;
        ImGui.SameLine(brandEnd + 16 * scale);
        ImGui.SetCursorPosY((height - ImGui.GetFrameHeight()) / 2);
        DrawNavigationButtons();
        float navigationEnd = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X;
        ImGui.SameLine(MathF.Max(230 * scale, navigationEnd + 16 * scale));
        ImGui.SetCursorPosY((height - ImGui.GetFrameHeight()) / 2);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(workspace.Project?.Name ?? "View only");
        ImGui.SameLine();
        if (workspace.Project is null)
        {
            if (ImGuiIcons.Button("tb-new", StudioIcon.Plus, "New project")) projectDialog = true;
        }
        else
        {
            ImGui.BeginDisabled(!workspace.CanEdit);
            if (ImGuiIcons.Button("tb-add", StudioIcon.Plus, "Add asset")) addDialog = true;
            ImGui.SameLine();
            string build = ShortcutInput.Text(ShortcutActions.BuildPackages);
            if (ImGuiIcons.Button("tb-build", StudioIcon.Build, build.Length == 0 ? "Build packages" : $"Build packages ({build})"))
                workspace.Run(() => workspace.Build());
            ImGui.EndDisabled();
        }
        ImGui.SameLine();
        bool shading = settings.GameShaders;
        if (ImGuiIcons.Toggle("tb-shaders", StudioIcon.Shader, "Game shaders", ref shading))
        { settings.GameShaders = shading; PrepareGameShaders(); }
        ImGui.SameLine();
        bool fullDetail = settings.FullDetail;
        if (ImGui.Checkbox("Full detail", ref fullDetail))
        {
            settings.FullDetail = fullDetail;
            if (viewport is not null) viewport.FullDetail = fullDetail;
            if (assetViewport is not null) assetViewport.FullDetail = fullDetail;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(
            "Draw static objects at full detail at every distance, with no far cull.\nOff switches detail and culls with distance, as the game does.");
        ImGui.SameLine();
        ImGui.BeginDisabled(!settings.GameShaders);
        ImGui.SetNextItemWidth(90 * scale);
        int lighting = (int)settings.LightingMode;
        if (ImGui.Combo("##lighting", ref lighting, LightingNames, LightingNames.Length))
        {
            settings.LightingMode = (SunriseStudio.Rendering.NativeLightingMode)lighting;
            if (viewport?.NativeRenderer is { } scene) scene.LightingMode = settings.LightingMode;
            if (assetViewport?.NativeRenderer is { } asset) asset.LightingMode = settings.LightingMode;
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(lighting == 1
            ? "Lighting: atmosphere preview\nLocal probes and shadows are not drawn"
            : "Lighting: editor lights with game materials");
        ImGui.EndDisabled();
        if (workspace.IsBusy) { ImGui.SameLine(); ImGui.AlignTextToFramePadding(); ImGui.TextDisabled("Working"); }
        ImGui.End();
    }

    private static readonly string[] LightingNames = ["Studio", "Sky", "Ambient"];

    private void DrawDialogs()
    {
        DrawWideHashes();
        DrawCrossings();
        DrawConfigBindings();
        if (setup || settingsDialog) ImGui.OpenPopup("Sunrise location");
        if (projectDialog) { ImGui.OpenPopup("New project"); projectDialog = false; }
        if (addDialog) { ImGui.OpenPopup("Add asset"); addDialog = false; }
        if (cacheDialog) { ImGui.OpenPopup("Disk cache"); cacheDialog = false; cacheStats = null; }
        if (modDialog) { ImGui.OpenPopup("Mod details"); modDialog = false; }
        if (shortcutsDialog) { ImGui.OpenPopup("Shortcuts"); shortcutsDialog = false; }
        if (noticesDialog) { ImGui.OpenPopup("Licenses"); noticesDialog = false; }
        DrawLocationDialog(); DrawProjectDialog(); DrawAddDialog(); DrawCacheDialog(); DrawBundleDialogs(); DrawModDialog();
        DrawShortcutsDialog(); DrawNoticesDialog();
        DrawCreation();
    }

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
        if (!ImGui.BeginPopupModal("Sunrise location", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextUnformatted("Installation folder");
        ImGui.SetNextItemWidth(465 * scale);
        ImGui.InputText("##install", ref location, 4096);
        ImGui.SameLine();
        if (ImGui.Button("Browse")) PickFolder("Sunrise installation", path => location = path);
        if (ImGui.CollapsingHeader("Package key source"))
        {
            ImGui.SetNextItemWidth(465 * scale);
            ImGui.InputTextWithHint("##keySource", "Automatic", ref keySource, 4096);
            StudioUi.Tip("Empty: read the keys from the installation");
            ImGui.SameLine();
            if (ImGui.Button("Browse##keys"))
                PickFile("Package key export|*.bin|Executable|*.exe|All files|*.*", path => keySource = path);
        }
        if (workspace.Error.Length != 0) ImGui.TextWrapped(workspace.Error);
        ImGui.Spacing();
        if (ImGui.Button("Open Sunrise", new(150 * scale, 0))) workspace.Run(() =>
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
            if (ImGui.Button("Cancel")) { settingsDialog = false; ImGui.CloseCurrentPopup(); }
        }
        ImGui.EndPopup();
    }

    private void DrawProjectDialog()
    {
        CenterModal(600);
        if (!ImGui.BeginPopupModal("New project", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextUnformatted("Name"); ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##projectName", ref projectName, 128);
        ImGui.TextUnformatted("Parent folder");
        ImGui.SetNextItemWidth(465 * scale); ImGui.InputText("##projectFolder", ref projectFolder, 4096);
        ImGui.SameLine();
        if (ImGui.Button("Browse")) PickFolder("Projects folder", path => projectFolder = path);
        if (workspace.Error.Length != 0) ImGui.TextWrapped(workspace.Error);
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(projectFolder)
            || workspace.IsBusy);
        if (ImGui.Button("Create project", new(150 * scale, 0))) workspace.Run(() =>
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
        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    /** Adds a file as a source asset or a native entry; with a clone source it copies that asset instead. */
    private void DrawAddDialog()
    {
        CenterModal(600);
        if (!ImGui.BeginPopupModal("Add asset", ImGuiWindowFlags.AlwaysAutoResize)) return;
        if (cloneSource is { } source) { ImGui.TextDisabled("Source"); ImGui.TextWrapped(source.Name); }
        else
        {
            ImGui.TextUnformatted("File");
            ImGui.SetNextItemWidth(465 * scale); ImGui.InputText("##addPath", ref addPath, 4096);
            ImGui.SameLine();
            if (ImGui.Button("Browse")) PickFile("All assets|*.*", path =>
            { addPath = path; addName = Path.GetFileName(path); });
        }
        ImGui.TextUnformatted("Name"); ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##addName", ref addName, 128);
        ImGui.SetNextItemWidth(-1);
        if (cloneSource is null) ImGui.Combo("##assetKind", ref addKind, ["Source asset", "Native package entry"], 2);
        if (addKind == 1)
        {
            ImGui.SetNextItemWidth(140 * scale); ImGui.InputText("Package ID", ref addPackageId, 4, ImGuiInputTextFlags.CharsHexadecimal);
            if (cloneSource is null)
            {
                ImGui.SetNextItemWidth(140 * scale); ImGui.InputText("Class / reference", ref addReference, 8, ImGuiInputTextFlags.CharsHexadecimal);
                ImGui.SetNextItemWidth(140 * scale); ImGui.InputInt("Type", ref addType);
                ImGui.SetNextItemWidth(140 * scale); ImGui.InputInt("Subtype", ref addSubType);
            }
            else ImGui.TextDisabled("Stored as a delta from the original");
        }
        else ImGui.TextDisabled("Source file; excluded from package builds");
        if (workspace.Error.Length != 0) ImGui.TextWrapped(workspace.Error);
        ImGui.BeginDisabled(!workspace.CanEdit || cloneSource is null && !IconChecks.Exists(addPath) || string.IsNullOrWhiteSpace(addName));
        if (ImGui.Button(cloneSource is null ? "Add asset" : "Create copy", new(150 * scale, 0))) workspace.Run(() =>
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
        if (ImGui.Button("Cancel")) { cloneSource = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

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

    private void OpenProjectDialog() => PickFile("Sunrise project|sunrise.project.json", OpenProject);

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

    private void RememberProject()
    {
        settings.RememberProject(workspace.Project!.ManifestPath);
        SaveSettings();
        Text = $"{workspace.Project.Name} - Sunrise Studio";
    }

    private void PickFolder(string title, Action<string> selected)
    {
        nativeDialog = true;
        try
        {
            using var dialog = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) workspace.Run(() => selected(dialog.SelectedPath));
        }
        finally { nativeDialog = false; }
    }

    private void PickFile(string filter, Action<string> selected)
    {
        nativeDialog = true;
        try
        {
            using var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) workspace.Run(() => selected(dialog.FileName));
        }
        finally { nativeDialog = false; }
    }

    private void SaveFile(string filter, string name, Action<string> save)
    {
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

    private void EnsureOutsideInstall(string path)
    {
        RejectLinks(path);
        if (workspace.Catalog is { } catalog && IsWithin(path, catalog.GameRoot))
            throw new IOException("Choose a folder outside the Sunrise installation.");
    }

    private static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Choose a path without links or junctions.");
    }

    private static bool IsWithin(string path, string folder)
    {
        string full = Path.GetFullPath(path), root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void OpenFolder(string path) =>
        Process.Start(new ProcessStartInfo { FileName = Path.GetFullPath(path), UseShellExecute = true });
}
