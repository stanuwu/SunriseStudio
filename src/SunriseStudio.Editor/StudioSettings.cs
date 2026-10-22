using System.Text.Json;
using SunriseStudio.Rendering;

namespace SunriseStudio.Editor;

/** One editor in its own window. Bounds are screen pixels. */
public sealed record StudioPopoutState(StudioPanelKind Panel, int X, int Y, int Width, int Height);

public sealed class StudioSettings
{
    public string SunrisePath { get; set; } = "";
    public string KeySource { get; set; } = "";
    public List<string> RecentProjects { get; set; } = [];
    public float BrowserWidth { get; set; } = 300;
    public float InspectorWidth { get; set; } = 300;
    public float ChangesHeight { get; set; } = 190;
    public bool GameShaders { get; set; } = true;
    /** Static objects keep their most detailed level at every distance instead of the game's distance levels. */
    public bool FullDetail { get; set; }
    public bool ShowSceneTree { get; set; } = true;
    public float SceneTreeWidth { get; set; } = 280;
    /** Where the model pane's divider sits, as the share of the page the pane takes; null keeps the default share. */
    public float? ModelPaneDivider { get; set; }
    public bool ShowSceneCursor { get; set; } = true;
    public bool ShowAssetCursor { get; set; } = true;
    public bool DiskCacheEnabled { get; set; } = true;
    public int DiskCacheLimitMiB { get; set; } = 512;
    public NativeLightingMode LightingMode { get; set; } = NativeLightingMode.Studio;
    /** Asset previews draw a flat backdrop; off draws the viewer sky gradient behind them. */
    public bool FlatBackdrop { get; set; }
    /** The model pane keeps its controls shown; off shows them on hover. */
    public bool AssetControlsPinned { get; set; } = true;
    /** Asset previews keep the last projection choice. */
    public bool Orthographic { get; set; }
    /** Armor previews draw the guardian wearing the piece, and weapons show in a guardian's hands. */
    public bool ShowGuardian { get; set; }
    /** Kept as raw JSON so a damaged section falls back per action instead of resetting every preference. */
    public JsonElement? Shortcuts { get; set; }
    public JsonElement? Layout { get; set; }
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
            settings.Popouts = settings.Popouts?.Where(popout => popout is not null && Enum.IsDefined(popout.Panel)
                && popout.Panel != StudioPanelKind.Empty).DistinctBy(popout => popout.Panel).ToList() ?? [];
            if (!Enum.IsDefined(settings.LightingMode)) settings.LightingMode = NativeLightingMode.Studio;
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

    public void RememberProject(string path)
    {
        path = Path.GetFullPath(path);
        RecentProjects.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, path);
        if (RecentProjects.Count > 8) RecentProjects.RemoveRange(8, RecentProjects.Count - 8);
    }
}
