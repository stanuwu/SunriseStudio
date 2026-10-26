/**
 * File: Solforge AI profile location and project keys (UI-UE5 §4.1, SOLFORGE-AI-APP-SPEC §7.3). Every launch, from the editor
 * or standalone, uses the one shared profile %LOCALAPPDATA%\SolforgeNext\AiApp\shared\{home,electron}: models, plugins,
 * automatic-mode settings and UI preferences are shared, and only the workspace (the open project) and its sessions differ.
 * The single-instance lock is on that root, so an editor launch while Solforge AI already runs focuses it instead of starting
 * a second app. Key keeps solforge-next's EngineSessionStore SHA-256 helper for other per-project folders (workflow history).
 * The older per-project and "_standalone" profile folders are left in place (the app migrates from them without deleting).
 */
using System.Security.Cryptography;
using System.Text;

namespace Solforge.AiLauncher;

/** Profile folder and project keys. */
public static class ProjectKey
{
    /** The shared profile folder name under AiApp. */
    public const string Shared = "shared";

    /** The path solforge-next hashes: full path, no trailing separator, upper case. */
    public static string Normalize(string project) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(project)).ToUpperInvariant();

    /** EngineSessionStore.Key: upper-case hex SHA-256 of "<normalized project>\n<engine>". */
    public static string Key(string project, string engine) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(project) + "\n" + engine)));

    /**
     * %LOCALAPPDATA%\SolforgeNext\AiApp\shared: holds home\ (DSH_HOME, overlays) and electron\ (--user-data-dir). The same for
     * every project; the parameter is kept so callers state which project they launch for.
     */
    public static string AppRoot(string? project = null) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolforgeNext", "AiApp", Shared);
}
