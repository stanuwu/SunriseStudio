/**
 * File: Runs an `npx -y package@x.y.z ...` MCP upstream straight from a copy npm already has on disk (the npx cache or the
 * global node_modules), as `node <bin script> ...`. npx itself costs about 12 s per start on this machine (registry check,
 * cmd.exe, npm's own start-up); node on the cached copy answers initialize in about 2 s. Only an exact version pin is
 * resolved, and only to an installed copy of that exact version; anything else (a range, latest, nothing cached) keeps the
 * original command, so nothing is ever downloaded here and a missing copy still starts through npx as before.
 */
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Solforge.Mcp;

/** Resolves pinned npx commands to an installed copy. */
public static partial class NpxLocal
{
    [GeneratedRegex(@"^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.\-+]+)?$")]
    private static partial Regex ExactVersion();

    /**
     * The command to run instead of @param command/@param args, or null to keep them. @param roots folders that hold
     * node_modules copies (default: every %LOCALAPPDATA%\npm-cache\_npx\* folder, then %APPDATA%\npm).
     */
    public static (string Command, IReadOnlyList<string> Args)? Resolve(string command, IReadOnlyList<string> args, IEnumerable<string>? roots = null)
    {
        int at = NpxIndex(command, args);
        if (at < -1) return null;
        // After npx: its own flags, then the package spec, then the server's arguments.
        int i = at + 1;
        while (i < args.Count && args[i] is "-y" or "--yes" or "-q" or "--quiet") i++;
        if (i >= args.Count) return null;
        string spec = args[i];
        int split = spec.LastIndexOf('@');
        if (split <= 0) return null;
        string name = spec[..split], version = spec[(split + 1)..];
        if (!ExactVersion().IsMatch(version) || name.Contains("..") || name.IndexOfAny(['\\', ':']) >= 0) return null;
        foreach (string root in roots ?? DefaultRoots())
        {
            try
            {
                string folder = Path.Combine(root, "node_modules", name.Replace('/', Path.DirectorySeparatorChar));
                string manifest = Path.Combine(folder, "package.json");
                if (!File.Exists(manifest) || JsonNode.Parse(File.ReadAllText(manifest)) is not JsonObject package) continue;
                if (package["version"]?.ToString() != version) continue;
                string? bin = Bin(package, name);
                if (bin is null) continue;
                string script = Path.GetFullPath(Path.Combine(folder, bin));
                if (!script.StartsWith(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase) || !File.Exists(script)) continue;
                var resolved = new List<string> { script };
                resolved.AddRange(args.Skip(i + 1));
                return ("node", resolved);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or NotSupportedException) { }
        }
        return null;
    }

    /** Index in args of the npx word (-1 when the command itself is npx), or -2 when this is not an npx command. */
    private static int NpxIndex(string command, IReadOnlyList<string> args)
    {
        string exe = Path.GetFileNameWithoutExtension(command.Trim('"')).ToLowerInvariant();
        if (exe == "npx") return -1;
        if (exe != "cmd") return -2;
        // cmd [/d] [/s] /c npx ...
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i].ToLowerInvariant();
            if (arg is "/d" or "/s" or "/q") continue;
            if (arg != "/c" || i + 1 >= args.Count) return -2;
            string next = Path.GetFileNameWithoutExtension(args[i + 1].Trim('"')).ToLowerInvariant();
            return next == "npx" ? i + 1 : -2;
        }
        return -2;
    }

    /** The package's bin script for its own name (or its only bin). */
    private static string? Bin(JsonObject package, string name)
    {
        if (package["bin"] is JsonValue single && single.TryGetValue(out string? only)) return only;
        if (package["bin"] is not JsonObject bins) return null;
        string bare = name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..] : name;
        if (bins[bare]?.ToString() is { Length: > 0 } own) return own;
        return bins.Count == 1 ? bins.First().Value?.ToString() : null;
    }

    private static IEnumerable<string> DefaultRoots()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string cache = Path.Combine(local, "npm-cache", "_npx");
        if (Directory.Exists(cache))
            foreach (var folder in new DirectoryInfo(cache).EnumerateDirectories().OrderByDescending(folder => folder.LastWriteTimeUtc))
                yield return folder.FullName;
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
    }
}
