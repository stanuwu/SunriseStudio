/**
 * File: Where Solforge AI is (UI-UE5 §4.1). Order: the SolforgeAiPath setting (a folder holding solforge-ai\Solforge AI.exe,
 * or the exe itself), then solforge-ai\ beside the editor exe, then the newest <SolforgeBuildsRoot>\*\solforge-ai by folder
 * time, skipping names that start with '.' or contain "-staging-". A layout also needs <base>\ai-runtime\node\node.exe.
 * File inspection only: nothing is started.
 */
namespace Solforge.AiLauncher;

/** A published Solforge AI: <base>\solforge-ai\Solforge AI.exe next to <base>\ai-runtime\node\node.exe. */
public sealed record SolforgeAiLayout(string AppExecutable, string BaseDirectory, string RuntimeDirectory, string Node, string Source);

/** The discovery result: a layout, or why none was found. */
public sealed record SolforgeAiDiscovery(SolforgeAiLayout? Layout, string Detail);

/** Finds the Solforge AI build. */
public static class AppDiscovery
{
    public const string AppFolder = "solforge-ai";
    public const string AppExe = "Solforge AI.exe";
    /** Used when no solforge-builds folder is found above the editor (editable in Settings > Solforge AI). */
    public const string FallbackBuildsRoot = @"C:\Users\Seth\Desktop\d2moddingsuite\solforge-builds";

    /** The first solforge-builds folder walking up from the editor folder, else FallbackBuildsRoot. */
    public static string DefaultBuildsRoot(string? editorDirectory = null)
    {
        try
        {
            for (var folder = new DirectoryInfo(editorDirectory ?? AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            {
                if (folder.Name.Equals("solforge-builds", StringComparison.OrdinalIgnoreCase)) return folder.FullName;
                string child = Path.Combine(folder.FullName, "solforge-builds");
                if (Directory.Exists(child)) return child;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }
        return FallbackBuildsRoot;
    }

    /** The layout under base, or null when a part is missing. */
    public static SolforgeAiLayout? FindLayout(string baseDirectory, string source)
    {
        try
        {
            string app = Path.Combine(baseDirectory, AppFolder, AppExe);
            string runtime = Path.Combine(baseDirectory, "ai-runtime");
            string node = Path.Combine(runtime, "node", "node.exe");
            return File.Exists(app) && Directory.Exists(runtime) && File.Exists(node)
                ? new SolforgeAiLayout(Path.GetFullPath(app), Path.GetFullPath(baseDirectory), Path.GetFullPath(runtime), Path.GetFullPath(node), source)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /**
     * SOLFORGE-AI-APP-SPEC §6.2 pairing check: true when imagePath is a "Solforge AI.exe" that is the exe the path setting names,
     * the one discovery finds now, or the one this editor launched (knownExecutable). File inspection only.
     */
    public static bool IsAppImage(string imagePath, string? setting, string? editorDirectory, string? buildsRoot, string? knownExecutable)
    {
        if (!Path.GetFileName(imagePath).Equals(AppExe, StringComparison.OrdinalIgnoreCase)) return false;
        string? Full(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetFullPath(path.Trim().Trim('"')); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        }
        string? image = Full(imagePath);
        if (image is null) return false;
        bool Same(string? other) => Full(other) is { } full && string.Equals(full, image, StringComparison.OrdinalIgnoreCase);
        return Same(knownExecutable) || Same(Find(setting, editorDirectory, buildsRoot).Layout?.AppExecutable);
    }

    /** Runs the discovery order. @param setting StudioSettings.SolforgeAiPath; @param buildsRoot StudioSettings.SolforgeBuildsRoot. */
    public static SolforgeAiDiscovery Find(string? setting, string? editorDirectory, string? buildsRoot)
    {
        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(setting))
        {
            string path = setting.Trim().Trim('"');
            string? baseDirectory = null;
            try
            {
                if (File.Exists(path) && Path.GetFileName(path).Equals(AppExe, StringComparison.OrdinalIgnoreCase))
                    baseDirectory = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(path)));
                else if (File.Exists(Path.Combine(path, AppFolder, AppExe))) baseDirectory = path;
                else if (File.Exists(Path.Combine(path, AppExe))) baseDirectory = Path.GetDirectoryName(Path.GetFullPath(path).TrimEnd('\\', '/'));
            }
            catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException) { }
            if (baseDirectory is not null && FindLayout(baseDirectory, "Settings > Solforge AI path") is { } chosen)
                return new(chosen, "Solforge AI path setting");
            notes.Add("The Solforge AI path setting does not point at solforge-ai\\Solforge AI.exe with ai-runtime\\node\\node.exe beside it.");
        }
        string editor = editorDirectory ?? AppContext.BaseDirectory;
        if (FindLayout(editor, "beside the editor") is { } beside) return new(beside, "beside the editor");
        string root = string.IsNullOrWhiteSpace(buildsRoot) ? DefaultBuildsRoot(editor) : buildsRoot;
        try
        {
            if (Directory.Exists(root))
            {
                foreach (var build in new DirectoryInfo(root).EnumerateDirectories().OrderByDescending(folder => folder.LastWriteTimeUtc))
                {
                    if (build.Name.StartsWith('.') || build.Name.Contains("-staging-", StringComparison.OrdinalIgnoreCase)) continue;
                    if (FindLayout(build.FullName, "newest build in " + root) is { } found) return new(found, "newest complete build: " + build.Name);
                }
                notes.Add("No complete build in " + root + " (each needs solforge-ai\\Solforge AI.exe and ai-runtime\\node\\node.exe; staging folders are skipped).");
            }
            else notes.Add("The builds folder " + root + " does not exist.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            notes.Add("The builds folder " + root + " could not be listed: " + exception.Message);
        }
        notes.Add("Nothing beside the editor in " + editor + ".");
        return new(null, "Solforge AI not found. " + string.Join(" ", notes));
    }
}
