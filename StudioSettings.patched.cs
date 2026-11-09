/**
 * File: Persisted editor settings (JSON): paths, renderer, layout and AI options. Edited in
 * StudioForm.Preferences.cs.
 */
using System.Text.Json;
using SunriseStudio.Rendering;

namespace SunriseStudio.Editor;

/** One editor in its own window. Bounds are screen pixels. */
public sealed record StudioPopoutState(StudioPanelKind Panel, int X, int Y, int Width, int Height);

/** The persisted editor settings; each property is one saved option. */
public sealed partial class StudioSettings
{
    public string SunrisePath { get; set; } = "";
    public string KeySource { get; set; } = "";
    public List<string> RecentProjects { get; set; } = [];
    /** The project opened last; reopened at startup when AutoOpenLastProject is on. */
    public string LastProjectPath { get; set; } = "";
    public bool AutoOpenLastProject { get; set; } = true;
    /** Fallback when no project was saved yet; empty means no automatic project. */
    public const string DefaultProjectPath = "";
    public float BrowserWidth { get; set; } = 300;
    public float InspectorWidth { get; set; } = 300;
    public float ChangesHeight { get; set; } = 190;
    public bool GameShaders { get; set; } = true;
    /** Static objects keep their most detailed level at every distance instead of the game's distance levels. */
    public bool FullDetail { get; set; }
    /** Map viewport quality: Low (flat, coarse, short cull) to Full (all levels, decals, effects). */
    public SunriseStudio.Viewport.ViewportQuality ViewportQuality { get; set; } = SunriseStudio.Viewport.ViewportQuality.High;
    /**
     * Memory optimization profile for 16GB RAM systems: bounds texture caches and LOH buffers. Off by default because the
     * budget leaves textures on their low resident mips, which changes the base look; the stored name changed so files
     * saved while it defaulted on load as off.
     */
    [System.Text.Json.Serialization.JsonPropertyName("LowMemoryTextures")]
    public bool LowMemoryMode { get; set; }
    /** Also force Low memory mode while Destiny 2 runs or the Live link is attached (changes the look the same way). */
    public bool LowMemoryWithGame { get; set; }
    /** Suspends viewport rendering while Destiny 2 is running unless attached via Live link. */
    public bool PauseRenderWhenGameRunning { get; set; } = true;
    /** The user's 3D viewport switch (View > 3D viewport rendering, Ctrl+Shift+R); off draws no 3D at all. */
    public bool ViewportRendering { get; set; } = true;
    /** Frame caps while Solforge has focus and while it is in the background; 0 means no cap (vsync). */
    public int FpsFocused { get; set; }
    public int FpsBackground { get; set; }
    /** World texture budget in MiB while Low memory mode is on. */
    public int TextureBudgetMiB { get; set; } = 256;
    /** Decoded world textures kept in RAM for faster reloads after edits (no effect on the image); 0 keeps none. */
    public int TextureCacheMiB { get; set; } = 256;
    /** Interface size multiplier on top of the monitor DPI (0.8 to 1.5). */
    public float UiScale { get; set; } = 1;
    /** AI workspace: legacy engine and custom command fields, kept so old settings files load; the Pi harness is the only engine. */
    public string AiEngine { get; set; } = "";
    public string AiCustomCommand { get; set; } = "";
    /** AI workspace: pi-cust.cmd for the custom Pi harness; blank discovers PATH, the Desktop build, or a copy beside Solforge. */
    public string AiPiHarnessPath { get; set; } = "";
    /** AI workspace: run the MCP server for AI engines (off stops it; engines then see no Solforge tools). */
    public bool AiMcpEnabled { get; set; } = true;
    /** Folders the tools and playbooks use: the server's mission scripts, and Blender (blank = found automatically). */
    public string ScriptsPath { get; set; } = "";
    public string BlenderPath { get; set; } = "";
    /** Layouts remembered per project folder (full path, upper case), so each workspace opens the way it was left. */
    public Dictionary<string, JsonElement> ProjectLayouts { get; set; } = [];
    public bool ShowSceneTree { get; set; } = true;
    public float SceneTreeWidth { get; set; } = 280;
    public bool ShowSceneCursor { get; set; } = true;
    public bool ShowAssetCursor { get; set; } = true;
    public bool DiskCacheEnabled { get; set; } = true;
    public int DiskCacheLimitMiB { get; set; } = 512;
    public NativeLightingMode LightingMode { get; set; } = NativeLightingMode.Studio;
    /** Kept as raw JSON so a damaged section falls back per action instead of resetting every preference. */
    public JsonElement? Shortcuts { get; set; }
    public JsonElement? Layout { get; set; }
    /** AI workspace: folders added for the engines by hand, and found folders the user switched off. */
    public List<string> AiFolders { get; set; } = [];
    public List<string> AiHiddenFolders { get; set; } = [];
    /** AI workspace: press Enter on an engine's allow prompt when its highlighted choice is a one-time Yes/Allow. */
    public bool AiAutoAllow { get; set; }
    /** AI workspace: approve MCP action requests (the AI LOG panel) with no click; null follows AiAutoAllow. */
    public bool? AiAutoApproveRequests { get; set; }
    /** Whether MCP action requests are approved automatically. */
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AutoApproveRequests => AiAutoApproveRequests ?? AiAutoAllow;
    /** AI workspace: start each engine in its no-prompt mode (claude/agy --dangerously-skip-permissions, cline --yolo); auto-allow stays as the fallback. */
    public bool AiSkipPermissions { get; set; } = true;
    /** AI workspace: Start/Restart resume the engine's last session for the folder (New session always starts fresh). */
    public bool AiResumeSessions { get; set; } = true;
    /** AI workspace, /automatic: most continue nudges per goal, longest run in minutes, quiet seconds before a nudge (AutomaticOptions clamps them). */
    public int AiAutomaticMaxNudges { get; set; } = 50;
    public int AiAutomaticMaxMinutes { get; set; } = 240;
    public int AiAutomaticIdleSeconds { get; set; } = 45;
    /** AI workspace: Claude Code also gets the ask_clinepass tool (solforge-mcp --clinepass) when cline is installed. */
    public bool AiAskClinePass { get; set; }
    /** AI workspace: the provider preset per engine command (claude, cline); never holds keys or tokens. */
    public Dictionary<string, Solforge.Mcp.EngineProviders.Choice> AiProviders { get; set; } = [];
    /** The AI panel was docked once by default; never added again, so closing it stays closed. */
    public bool AiPanelOffered { get; set; }
    /** Editors shown in their own windows, with each window's screen bounds. */
    public List<StudioPopoutState> Popouts { get; set; } = [];

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sunrise Studio", "settings.json");

    /** A corrupt local preference file must not prevent the location picker from opening. */
    public static StudioSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            var settings = JsonSerializer.Deserialize<StudioSettings>(File.ReadAllText(path)) ?? new();
            settings.RecentProjects ??= [];
            settings.SunrisePath ??= "";
            settings.KeySource ??= "";
            settings.AiFolders ??= [];
            settings.AiHiddenFolders ??= [];
            settings.AiProviders ??= [];
            settings.AiEngine ??= "";
            settings.AiCustomCommand ??= "";
            settings.AiPiHarnessPath ??= "";
            settings.ScriptsPath ??= "";
            settings.BlenderPath ??= "";
            settings.ProjectLayouts ??= [];
            settings.FpsFocused = Math.Clamp(settings.FpsFocused, 0, 240);
            settings.FpsBackground = Math.Clamp(settings.FpsBackground, 0, 240);
            settings.TextureBudgetMiB = Math.Clamp(settings.TextureBudgetMiB, 64, 4096);
            settings.TextureCacheMiB = Math.Clamp(settings.TextureCacheMiB, 0, 4096);
            settings.UiScale = float.IsFinite(settings.UiScale) ? Math.Clamp(settings.UiScale, .8f, 1.5f) : 1;
            settings.Popouts = settings.Popouts?.Where(popout => popout is not null && Enum.IsDefined(popout.Panel)
                && popout.Panel != StudioPanelKind.Empty).DistinctBy(popout => popout.Panel).ToList() ?? [];
            if (!Enum.IsDefined(settings.LightingMode)) settings.LightingMode = NativeLightingMode.Studio;
            if (!Enum.IsDefined(settings.ViewportQuality)) settings.ViewportQuality = SunriseStudio.Viewport.ViewportQuality.High;
            settings.DiskCacheLimitMiB = Math.Clamp(settings.DiskCacheLimitMiB, 128, 4096);
            return settings;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    /** Preferences are local; project manifests never carry machine-specific installation paths. */
    public void Save(string path)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }

    /** Accept either the installation root, its packages folder, or the Sunrise runtime folder. */
    public static string ResolveGameRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new IOException("Choose your Sunrise folder.");
        string full = Path.GetFullPath(path.Trim().Trim('"'));
        if (File.Exists(full)) full = Path.GetDirectoryName(full)!;
        var current = new DirectoryInfo(full);
        for (int level = 0; level < 7 && current is not null; level++, current = current.Parent)
        {
            string packages = Path.Combine(current.FullName, "packages");
            if (Directory.Exists(packages) && Directory.EnumerateFiles(packages, "*.pkg").Any())
                return current.FullName;
        }
        throw new IOException("No packages found in this Sunrise installation.");
    }

    /** Moves a project to the top of the recent project list. */
    public void RememberProject(string path)
    {
        path = Path.GetFullPath(path);
        LastProjectPath = path;
        RecentProjects.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, path);
        if (RecentProjects.Count > 8) RecentProjects.RemoveRange(8, RecentProjects.Count - 8);
    }
}
