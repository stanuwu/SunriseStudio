/** File: Preferences window of StudioForm; values are stored in StudioSettings.cs. */
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/**
 * The Settings window (File or Edit > Settings, the toolbar gear, Ctrl+,). Every value lives in StudioSettings and is saved
 * as it changes; each applies live unless its row says when it takes effect.
 */
public sealed partial class StudioForm
{
    private bool preferencesOpen;
    private string preferencesScripts = "", preferencesBlender = "";
    private bool preferencesSeeded;
    /** A tab to select the next time the window draws (the Solforge AI pill's NotFound click, its "Settings..."). */
    private string? preferencesSelectTab;

    /** Declared in StudioForm.SolforgeAi.cs: shows Settings on the "Solforge AI" tab. */
    partial void OpenSolforgeAiSettings()
    {
        preferencesOpen = true;
        preferencesSelectTab = "Solforge AI";
    }

    /** A Settings tab, selected once when preferencesSelectTab names it, with the sliding Accent underline. */
    private unsafe bool BeginSettingsTab(string label)
    {
        bool select = string.Equals(preferencesSelectTab, label, StringComparison.Ordinal);
        if (select) preferencesSelectTab = null;
        // The bar's id, taken before the tab pushes its own, so the underline slides between this bar's tabs.
        uint bar = ImGui.GetID("##tab-underline");
        bool open;
        if (!select) open = UiAuto.BeginTabItem(label);
        else
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(label + '\0');
            fixed (byte* text = bytes) open = ImGuiNative.igBeginTabItem(text, null, ImGuiTabItemFlags.SetSelected) != 0;
        }
        if (open) UiMotion.TabUnderline(bar, ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        return open;
    }

    /** Draws the preferences window and its tabs. */
    private void DrawPreferences()
    {
        if (!preferencesOpen) { preferencesSeeded = false; return; }
        if (!preferencesSeeded)
        {
            preferencesSeeded = true;
            preferencesScripts = settings.ScriptsPath;
            preferencesBlender = settings.BlenderPath;
        }
        ImGui.SetNextWindowPos(new(ClientSize.Width / 2f, ClientSize.Height / 2f), ImGuiCond.Appearing, new(.5f));
        ImGui.SetNextWindowSize(new Vector2(620, 520) * scale, ImGuiCond.Appearing);
        if (!ImGui.Begin("Settings###solforge-settings", ref preferencesOpen))
        { ImGui.End(); return; }
        if (ImGui.BeginTabBar("settings-tabs", ImGuiTabBarFlags.FittingPolicyScroll))
        {
            if (BeginSettingsTab("Rendering")) { DrawRenderingSettings(); UiAuto.EndTabItem(); }
            if (BeginSettingsTab("AI")) { DrawAiSettings(); UiAuto.EndTabItem(); }
            // Hook H4: agent C's pages (each draws its own UiAuto.PushBlocked/PopBlocked).
            if (BeginSettingsTab("Solforge AI")) { DrawSolforgeAiSettings(); UiAuto.EndTabItem(); }
            if (BeginSettingsTab("Models & workflows")) { DrawModelsWorkflowsSettings(); UiAuto.EndTabItem(); }
            if (BeginSettingsTab("Paths")) { DrawPathSettings(); UiAuto.EndTabItem(); }
            if (BeginSettingsTab("Live link")) { DrawLiveSettings(); UiAuto.EndTabItem(); }
            if (BeginSettingsTab("Interface")) { DrawInterfaceSettings(); UiAuto.EndTabItem(); }
            ImGui.EndTabBar();
        }
        ImGui.End();
    }

    /** Draws a wrapped disabled-colour note. */
    private static void SettingNote(string text) { ImGui.PushTextWrapPos(); ImGui.TextDisabled(text); ImGui.PopTextWrapPos(); }

    /** Draws the rendering settings. */
    private void DrawRenderingSettings()
    {
        bool render = settings.ViewportRendering;
        if (UiAuto.Checkbox("3D viewport rendering", ref render)) SetViewportRendering(render);
        SettingNote($"Off stops all 3D drawing and shrinks the viewport's GPU targets; every panel and the AI tools keep working. "
            + $"Toolbar eye or {ShortcutInput.Text(ShortcutActions.ToggleViewportRendering)}. Applies now.");
        bool pause = settings.PauseRenderWhenGameRunning;
        if (UiAuto.Checkbox("Pause rendering while Destiny 2 runs", ref pause)) { settings.PauseRenderWhenGameRunning = pause; SaveSettings(); }
        SettingNote("Unless the Live link is attached. The viewport's Resume button overrides it until the game closes. Applies now.");
        bool low = settings.LowMemoryMode;
        if (UiAuto.Checkbox("Low memory mode (16 GB PCs)", ref low)) { settings.LowMemoryMode = low; SaveSettings(); }
        SettingNote("Bounds the world texture cache; textures past the budget keep only their low mips, so surfaces look softer and flatter. Off matches upstream Sunrise Studio. Applies to the next map or shader load.");
        bool withGame = settings.LowMemoryWithGame;
        if (UiAuto.Checkbox("Low memory mode while Destiny 2 runs or Live is attached", ref withGame)) { settings.LowMemoryWithGame = withGame; SaveSettings(); }
        int budget = settings.TextureBudgetMiB;
        ImGui.SetNextItemWidth(220 * scale);
        if (UiAuto.SliderInt("Texture budget (MiB)", ref budget, 64, 2048)) { settings.TextureBudgetMiB = budget; SaveSettings(); }
        SettingNote("Used while Low memory mode is on. Applies to the next map or shader load.");
        int cache = settings.TextureCacheMiB;
        ImGui.SetNextItemWidth(220 * scale);
        if (UiAuto.SliderInt("Reload texture cache (MiB)", ref cache, 0, 2048)) { settings.TextureCacheMiB = cache; SaveSettings(); }
        SettingNote("Decoded textures kept in RAM so a reload after an edit is faster; the image is the same at any size. Applies after the next load.");
        int focused = settings.FpsFocused, background = settings.FpsBackground;
        ImGui.SetNextItemWidth(220 * scale);
        if (UiAuto.SliderInt("FPS cap, focused", ref focused, 0, 240, focused == 0 ? "no cap" : "%d")) { settings.FpsFocused = focused; SaveSettings(); }
        ImGui.SetNextItemWidth(220 * scale);
        if (UiAuto.SliderInt("FPS cap, background", ref background, 0, 240, background == 0 ? "no cap" : "%d")) { settings.FpsBackground = background; SaveSettings(); }
        SettingNote("0 means no cap. Lower caps from the Live link (30/10), Destiny 2 (15/5) and viewport off (30/10) still apply. Applies now.");
    }

    /** Draws the AI settings section. */
    private void DrawAiSettings()
    {
        // Settings > AI (MCP switch, auto-allow) is never driven by the MCP itself.
        UiAuto.PushBlocked();
        try { DrawAiSettingsRows(); }
        finally { UiAuto.PopBlocked(); }
    }

    /**
     * Settings > AI: the MCP server switch, auto-approve and the automatic-mode note. The console rows (the consoles,
     * the Pi harness path, Resume) went with the in-editor console; their settings fields stay so old files load.
     */
    private void DrawAiSettingsRows()
    {
        SettingNote("The AI runs in the separate Solforge AI app (the toolbar button, or Ctrl+Shift+A). Solforge shares project "
            + "context and live editing tools with it, and with any other MCP client, through its MCP server. Settings > Solforge AI sets where the app is.");
        bool approveRequests = settings.AutoApproveRequests;
        if (UiAuto.Checkbox("Auto-approve AI requests", ref approveRequests)) { settings.AiAutoApproveRequests = approveRequests; SaveSettings(); }
        SettingNote("Solforge MCP action requests (open file, edits, builds) run with no Approve click and are listed in the AI LOG (Output Log, MCP). "
            + "Model-workflow workers are never auto-approved.");
        bool mcp = settings.AiMcpEnabled;
        if (UiAuto.Checkbox("MCP server (Solforge live editing tools)", ref mcp)) SetAiMcp(mcp);
        string status = "MCP off";
        AiServiceStatus(ref status);
        SettingNote(status + ". Off stops the server at once; connected clients lose Solforge's live editing tools until it is back on. "
            + "On, it starts with Solforge, with or without a project.");
        ImGui.Separator();
        DrawAiAutomaticSettings();
    }


    /** Draws the path settings (game folder, reference folder and others). */
    private void DrawPathSettings()
    {
        ImGui.TextDisabled("Game install");
        ImGui.TextWrapped(workspace.Catalog?.GameRoot ?? (settings.SunrisePath.Length > 0 ? settings.SunrisePath : "-"));
        if (UiAuto.Button("Change installation...")) settingsDialog = true;
        SettingNote("Reopens the packages; the open project stays.");
        ImGui.Spacing();
        ImGui.TextDisabled("Project");
        ImGui.TextWrapped(workspace.Project?.DirectoryPath ?? "No project open");
        ImGui.BeginDisabled(workspace.IsBusy);
        if (UiAuto.Button("Open project...")) OpenProjectDialog();
        ImGui.EndDisabled();
        ImGui.Spacing();
        PathRow("Server mission scripts", ref preferencesScripts, value => settings.ScriptsPath = value,
            "Destiny-Sol-main\\mission-scripts or the server's scripts folder. The AI tools search it for mission code. Applies now.");
        PathRow("Blender", ref preferencesBlender, value => settings.BlenderPath = value,
            "blender.exe, read by the AI playbooks (get_settings). Blank: they look for it themselves.");
    }

    /** Draws one path field with a browse button and a note. */
    private void PathRow(string label, ref string text, Action<string> store, string note)
    {
        ImGui.TextDisabled(label);
        ImGui.PushID(label);
        ImGui.SetNextItemWidth(-ButtonWidth("Save") - ImGui.GetStyle().ItemSpacing.X);
        UiAuto.InputText("##path", ref text, 1024);
        ImGui.SameLine();
        string value = text.Trim().Trim('"');
        bool valid = value.Length == 0 || Directory.Exists(value) || File.Exists(value);
        ImGui.BeginDisabled(!valid);
        if (UiAuto.Button("Save")) { store(value); SaveSettings(); }
        ImGui.EndDisabled();
        if (!valid) ImGui.TextColored(SolTheme.Sun, "Not found.");
        SettingNote(note);
        ImGui.PopID();
    }

    /** Draws the Live link settings. */
    private void DrawLiveSettings()
    {
        ImGui.TextUnformatted(livePage.IsAttached ? "Attached to a running game." : "Not attached.");
        if (UiAuto.Button("Open the Live tab")) { focusLive = true; preferencesOpen = false; }
        SettingNote("Attaching needs core.studio_attach = true in the game's bin\\x64\\Sunrise\\settings.json. While attached, "
            + "Solforge draws at most 30 frames a second (10 in the background) and keeps Low memory mode on.");
        bool pause = settings.PauseRenderWhenGameRunning;
        if (UiAuto.Checkbox("Pause rendering while Destiny 2 runs detached", ref pause)) { settings.PauseRenderWhenGameRunning = pause; SaveSettings(); }
    }

    /** Draws the interface settings. */
    private void DrawInterfaceSettings()
    {
        ImGui.TextDisabled("Layout");
        foreach (string name in StudioLayout.PresetNames)
        {
            if (UiAuto.Button(name)) ApplyLayoutPreset(name);
            ImGui.SameLine();
        }
        ImGui.NewLine();
        if (UiAuto.Button("Reset layout")) ResetLayout();
        SettingNote("Each project remembers its own layout, including collapsed areas. Applies now.");
        ImGui.Spacing();
        ImGui.TextDisabled("Theme");
        ImGui.TextUnformatted("Solforge UE5 (dark)");
        SettingNote("Solforge has one theme for now.");
        ImGui.Spacing();
        ImGui.TextDisabled("Motion");
        // Hook H3: Reduce motion and the animation speed (agent A).
        DrawMotionSettings();
        ImGui.Spacing();
        float size = settings.UiScale;
        ImGui.SetNextItemWidth(220 * scale);
        if (UiAuto.SliderFloat("Interface size", ref size, .8f, 1.5f, "%.2fx")) settings.UiScale = size;
        // Fonts rebuild between frames once the slider is let go.
        if (ImGui.IsItemDeactivatedAfterEdit()) { SaveSettings(); pendingDpi = DeviceDpi; }
        SettingNote("Text and control size on top of the monitor's DPI. Applies when you let go of the slider.");
    }
}
