namespace Solforge.Mcp;

/**
 * Finds an AI engine's command the way cmd.exe does (PATH order, then PATHEXT order), on a PATH re-read from
 * the registry, so an engine installed after Solforge started is found without restarting Solforge.
 */
public static class EngineCommands
{
    public const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD";

    /** Where the Antigravity and npm installers put their commands, for when PATH has not caught up yet. */
    public static IReadOnlyList<string> KnownFolders()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return [Path.Combine(local, "agy", "bin"), Path.Combine(roaming, "npm")];
    }

    /** Machine PATH then user PATH (what a new console gets), then this process's other entries and the known folders. */
    public static string FreshPath()
    {
        string[] sources =
        [
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "",
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "",
            Environment.GetEnvironmentVariable("PATH") ?? "",
            string.Join(';', KnownFolders().Where(Directory.Exists))
        ];
        return Join(sources);
    }

    /** Joins PATH lists with %VARIABLES% expanded, keeping the first of each folder. */
    public static string Join(IEnumerable<string> lists)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new List<string>();
        foreach (string list in lists)
            foreach (string raw in Environment.ExpandEnvironmentVariables(list).Split(';'))
            {
                string folder = raw.Trim().Trim('"').TrimEnd('\\', '/');
                if (folder.Length > 0 && seen.Add(folder)) folders.Add(folder);
            }
        return string.Join(';', folders);
    }

    /**
     * The custom Pi harness launcher (pi-cust.cmd): the configured path, PI_CUST_HARNESS or PI_CUST_HARNESS_PATH,
     * pi-cust on PATH, the default Desktop build, or a copy beside Solforge.exe. Null when none exists.
     */
    public static string? FindPiHarness(string? configured = null, string? path = null, string? desktop = null)
    {
        string? Existing(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return null;
            try
            {
                string full = Path.GetFullPath(candidate.Trim().Trim('"'));
                if (Directory.Exists(full)) full = Path.Combine(full, "pi-cust.cmd");
                return File.Exists(full) ? full : null;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        }
        if (Existing(configured) is { } chosen) return chosen;
        foreach (string name in new[] { "PI_CUST_HARNESS", "PI_CUST_HARNESS_PATH" })
            if (Existing(Environment.GetEnvironmentVariable(name)) is { } fromEnvironment) return fromEnvironment;
        if (Resolve("pi-cust", path ?? FreshPath()) is { } onPath) return onPath;
        string profile = desktop ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        foreach (string candidate in new[]
        {
            Path.Combine(profile, "pi-cust", "pi-cust-harness-build", "build-1", "pi-cust.cmd"),
            Path.Combine(AppContext.BaseDirectory, "pi-cust.cmd")
        })
            if (Existing(candidate) is { } found) return found;
        return null;
    }

    /** The full path cmd.exe would run for the command, or null. A command with a folder in it is checked as given. */
    public static string? Resolve(string command, string path, string? pathExt = null)
    {
        command = command.Trim().Trim('"');
        if (command.Length == 0) return null;
        string[] extensions = (string.IsNullOrWhiteSpace(pathExt) ? DefaultPathExt : pathExt)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // npm also writes an extensionless shell script beside each .cmd; cmd only runs a bare name that has an extension.
        bool named = extensions.Any(extension => command.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
        bool placed = command.IndexOfAny(['\\', '/', ':']) >= 0;
        foreach (string raw in placed ? new[] { "" } : path.Split(';'))
        {
            string folder = raw.Trim().Trim('"');
            if (!placed && folder.Length == 0) continue;
            string stem;
            try { stem = Path.GetFullPath(placed ? command : Path.Combine(folder, command)); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (named && File.Exists(stem)) return OnDisk(stem);
            foreach (string extension in extensions)
                if (File.Exists(stem + extension)) return OnDisk(stem + extension);
        }
        return null;
    }

    /** The file as the folder spells it (claude.cmd, not PATHEXT's claude.CMD). */
    private static string OnDisk(string file)
    {
        try { return Directory.EnumerateFiles(Path.GetDirectoryName(file)!, Path.GetFileName(file)).FirstOrDefault() ?? file; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return file; }
    }
}
