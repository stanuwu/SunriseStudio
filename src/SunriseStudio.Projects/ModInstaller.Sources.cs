/**
 * File: Partial ModInstaller: collects a project's Lua controllers and source folders into mod script files.
 */
using System.Text.Json;

namespace SunriseStudio.Projects;

/** Partial: script source collection. */
public static partial class ModInstaller
{
    // The native controller reader owns a fixed 128 KiB source buffer.
    private const int MaximumControllerBytes = 128 * 1024;
    // Generated SDK modules can reach 12 MiB; larger sources are refused before allocation.
    private const int MaximumModuleBytes = 16 * 1024 * 1024;
    private const string SourceReceiptName = ".studio-sources.json";

    /** Disk helpers join saved controllers; a concurrent controller edit cannot mix two versions. */
    private static IReadOnlyList<ModScriptFile> ProjectScripts(StudioProject project, IReadOnlyList<ModScriptFile> controllers)
    {
        CheckScripts(controllers);
        var scripts = controllers.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var file in ProjectSources(project, "scripts"))
        {
            if (scripts.TryGetValue(file.RelativePath, out var previous) && !previous.Data.AsSpan().SequenceEqual(file.Data))
                throw new IOException($"Script {file.RelativePath} changed while the install was being prepared.");
            scripts[file.RelativePath] = file;
        }
        return scripts.Values.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
    }

    /** Source folders contain owned Lua; they never copy modules from the installed SDK. */
    /** Top folders a project's `modfiles` may fill; the client reads them from loaded mods (e.g. ui/subclass_screens.json). */
    public static readonly string[] ModFileFolders = ["ui", "textures", "data", "resources"];
    private const long MaximumModFileBytes = 16L * 1024 * 1024;
    private const int MaximumModFiles = 512;

    /**
     * Mod data files: everything below `<project>/modfiles/` is copied into the mod folder at the same relative path.
     * Only the ModFileFolders top folders are allowed, so a data file can never replace mod.json, packages, patches,
     * scripts, sdk or a native bin/ DLL.
     */
    private static IReadOnlyList<ModScriptFile> ProjectModFiles(StudioProject project)
    {
        string root = ProjectPaths.Resolve(project.DirectoryPath, "modfiles");
        if (!Directory.Exists(root)) return [];
        var result = new List<ModScriptFile>();
        foreach (string path in UnlinkedFiles(root))
        {
            string relative = Relative(root, path);
            RequireModFilePath(relative);
            if (new FileInfo(path).Length > MaximumModFileBytes)
                throw new InvalidDataException($"Mod file {relative} exceeds {MaximumModFileBytes} bytes.");
            if (result.Count >= MaximumModFiles) throw new InvalidDataException($"modfiles holds more than {MaximumModFiles} files.");
            result.Add(new(relative, File.ReadAllBytes(path)));
        }
        return result.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
    }

    /** A mod file path: '/'-separated, no empty, '.' or '..' part, under one of ModFileFolders. */
    internal static void RequireModFilePath(string relative)
    {
        string[] parts = relative.Split('/');
        if (parts.Length < 2 || parts.Any(part => part.Length == 0 || part is "." or ".." || part.IndexOfAny([':', '\\']) >= 0)
            || !ModFileFolders.Contains(parts[0], StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"Mod file {relative} must sit below one of: {string.Join(", ", ModFileFolders)}.");
    }

    private static bool SameHashes(IReadOnlyDictionary<string, string>? before, IReadOnlyDictionary<string, string>? after)
    {
        if ((before?.Count ?? 0) != (after?.Count ?? 0)) return false;
        if (before is null || after is null) return true;
        return before.All(file => after.TryGetValue(file.Key, out string? hash) && hash.Equals(file.Value, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<ModScriptFile> ProjectSources(StudioProject project, string folder)
    {
        string root = ProjectPaths.Resolve(project.DirectoryPath, folder);
        if (!Directory.Exists(root)) return [];
        var result = new List<ModScriptFile>();
        foreach (string path in UnlinkedFiles(root).Where(path => path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)))
        {
            string relative = Relative(root, path);
            RequireModulePath(relative);
            if (new FileInfo(path).Length > MaximumModuleBytes)
                throw new InvalidDataException($"Lua module {relative} exceeds {MaximumModuleBytes} bytes.");
            result.Add(new(relative, File.ReadAllBytes(path)));
        }
        return result.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
    }

    /** Native require accepts only letters, digits, underscores and dots between nonempty names. */
    private static void RequireModulePath(string? relative)
    {
        if (string.IsNullOrEmpty(relative) || !relative.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)
            || relative.Contains('\\') || relative[..^4].Split('/').Any(part => part.Length == 0
                || part.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_')))
            throw new InvalidDataException($"Lua source path {relative} cannot be loaded by require.");
    }

    /** The receipt distinguishes project helpers from files added by hand to an installed mod. */
    private static byte[] SourceReceipt(IEnumerable<string> scripts) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        format = "sunrise-mod-sources", version = 1,
        scripts = scripts.Order(StringComparer.Ordinal).ToArray()
    });

    /** Reads an installed mod's source receipt; both null when it has none. */
    private static (IReadOnlySet<string>? Paths, byte[]? Bytes) ReadSourceReceipt(string target)
    {
        string path = ProjectPaths.Resolve(target, SourceReceiptName);
        if (!File.Exists(path)) return (null, null);
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("The mod source receipt is too large.");
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            if (root.GetProperty("format").GetString() != "sunrise-mod-sources" || root.GetProperty("version").GetInt32() != 1)
                throw new InvalidDataException("The mod source receipt has an unsupported format.");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in root.GetProperty("scripts").EnumerateArray())
            {
                string relative = row.GetString() ?? "";
                RequireModulePath(relative);
                if (!paths.Add(relative)) throw new InvalidDataException("The mod source receipt contains a duplicate path.");
            }
            return (paths, bytes);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new InvalidDataException("The mod source receipt is invalid. Build and install again.", error); }
    }
}
