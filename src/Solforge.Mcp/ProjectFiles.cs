using System.Text;
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/**
 * Project-relative file access for the AI: reads and searches the project and the Lua SDK docs, and keeps
 * edits inside the project overlay. Paths that leave the root, game packages and build output are refused.
 */
public static class ProjectFiles
{
    public const string SdkPrefix = "sdk:";
    /** Read-only prefix for the server mission scripts folder (Settings > Paths), e.g. "scripts:freeroam/edz.lua". */
    public const string ScriptsPrefix = "scripts:";
    private const long MaximumRead = 2 << 20;
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
        { ".git", "bin", "obj", "node_modules", ".vs", "build", "out" };
    private static readonly HashSet<string> RefusedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".pkg", ".exe", ".dll", ".pdb", ".bak" };

    /** The full path for a project path ("scripts/a.lua") or SDK doc path ("sdk:api.md"); null when it leaves its root. */
    public static string? Resolve(string? projectRoot, IReadOnlyList<string> sdkRoots, string relative, string? scriptsRoot = null,
        IReadOnlyList<string>? readFolders = null)
    {
        relative = relative.Replace('\\', '/').Trim();
        if (relative.StartsWith(ScriptsPrefix, StringComparison.OrdinalIgnoreCase))
            return scriptsRoot is { Length: > 0 } ? Inside(scriptsRoot, relative[ScriptsPrefix.Length..].TrimStart('/')) : null;
        if (relative.StartsWith(SdkPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string rest = relative[SdkPrefix.Length..].TrimStart('/');
            foreach (string sdk in sdkRoots)
                if (Inside(sdk, rest) is { } path && File.Exists(path)) return path;
            return sdkRoots.Count > 0 ? Inside(sdkRoots[0], rest) : null;
        }
        if (projectRoot is not null && Inside(projectRoot, relative) is { } projectPath) return projectPath;
        // A folder grant includes every descendant. Absolute paths keep references to separate roots unambiguous;
        // a sibling whose name happens to start with the allowed folder's name does not inherit permission.
        if (Path.IsPathFullyQualified(relative) && readFolders is not null)
            foreach (string folder in readFolders)
                if (Inside(folder, relative) is { } allowed) return allowed;
        return null;
    }

    /** Refuses absolute paths, "..", other drives and anything the build writes or the game reads directly. */
    public static string? EditablePath(string? projectRoot, string relative, out string? reason)
    {
        reason = null;
        if (projectRoot is null) { reason = "No project is open."; return null; }
        if (Path.IsPathRooted(relative) || relative.StartsWith(SdkPrefix, StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith(ScriptsPrefix, StringComparison.OrdinalIgnoreCase))
        { reason = "Give a path relative to the project folder (server scripts are read-only here)."; return null; }
        if (Inside(projectRoot, relative) is not { } path) { reason = "The path leaves the project folder."; return null; }
        string[] parts = Path.GetRelativePath(projectRoot, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Any(part => SkippedFolders.Contains(part))) { reason = "Build output and tool folders are not editable."; return null; }
        if (RefusedExtensions.Contains(Path.GetExtension(path))) { reason = "Packages and binaries are never edited; edit the project's source files."; return null; }
        if (string.Equals(Path.GetFileName(path), "sunrise.project.json", StringComparison.OrdinalIgnoreCase))
        { reason = "The project manifest is edited through Solforge, not by the AI."; return null; }
        return path;
    }

    /** A page of lines of a project file, shown under the given display path. */
    public static JsonObject Read(string path, string shown, int offset, int limit)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException($"No such file: {shown}");
        if (info.Length > MaximumRead) throw new IOException($"{shown} is {info.Length} bytes; the limit is {MaximumRead}.");
        // One read gives both the lines and the version, so the sha256 always matches the text returned.
        byte[] bytes = File.ReadAllBytes(path);
        string[] lines = Lines(bytes);
        offset = Math.Clamp(offset, 1, Math.Max(1, lines.Length));
        limit = Math.Clamp(limit, 1, 4000);
        var text = new StringBuilder();
        for (int i = offset - 1; i < Math.Min(lines.Length, offset - 1 + limit); i++) text.Append(i + 1).Append('\t').AppendLine(lines[i]);
        return new JsonObject
        {
            ["path"] = shown, ["lines"] = lines.Length, ["from"] = offset,
            ["to"] = Math.Min(lines.Length, offset - 1 + limit), ["text"] = text.ToString(),
            // UI-UE5 §5.5: the whole file's version; pass it back as expected_sha256 to apply_project_edit.
            ["sha256"] = Sha256(bytes)
        };
    }

    /** Lowercase hex SHA-256 of a file's bytes (its version for expected_sha256). */
    public static string Sha256(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    /** The version of a file on disk, or null when it does not exist or cannot be read. */
    public static string? FileSha256(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length <= MaximumRead ? Sha256(File.ReadAllBytes(path)) : null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    /** True when expected (hex, any case) is the file's current version; a missing file matches only an empty expectation. */
    public static bool VersionMatches(string path, string? expected) =>
        string.IsNullOrWhiteSpace(expected) || string.Equals(FileSha256(path), expected.Trim(), StringComparison.OrdinalIgnoreCase);

    /** The text lines of file bytes, as File.ReadAllLines splits them (BOM-aware). */
    private static string[] Lines(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return [.. lines];
    }

    /** Text search over the project and SDK roots, limited to the extension if given. */
    public static JsonArray Search(string? projectRoot, IReadOnlyList<string> sdkRoots, string query, string? extension, int limit) =>
        Search(projectRoot, sdkRoots, query, extension, limit, Solforge.Harness.Credentials.CredentialGuard.IsCredentialPath);

    /** Text search that never opens a file skip returns true for (the credential file guard). */
    public static JsonArray Search(string? projectRoot, IReadOnlyList<string> sdkRoots, string query, string? extension, int limit, Func<string, bool> skip,
        IReadOnlyList<string>? readFolders = null, string? folder = null)
    {
        var results = new JsonArray();
        var roots = new List<(string Root, string Prefix)>();
        if (folder is { Length: > 0 })
        {
            string? probe = Resolve(projectRoot, sdkRoots, Path.Combine(folder, ".solforge-search-scope"), readFolders: readFolders);
            string? scoped = probe is null ? null : Path.GetDirectoryName(probe);
            if (scoped is null || !Directory.Exists(scoped)) throw new IOException("The search folder is missing or outside the project and enabled AI folders.");
            roots.Add((scoped, Path.GetFullPath(scoped).TrimEnd('\\', '/') + Path.DirectorySeparatorChar));
        }
        else
        {
            if (projectRoot is not null) roots.Add((projectRoot, ""));
            roots.AddRange(sdkRoots.Where(Directory.Exists).Select(root => (root, SdkPrefix)));
            if (readFolders is not null) roots.AddRange(readFolders.Where(Directory.Exists).Select(root => (root, Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar)));
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, prefix) in roots)
            foreach (string file in Files(root))
            {
                if (!seen.Add(Path.GetFullPath(file))) continue;
                if (extension is { Length: > 0 } && !file.EndsWith(extension.StartsWith('.') ? extension : "." + extension, StringComparison.OrdinalIgnoreCase)) continue;
                if (skip(file)) continue;
                string relative = prefix + Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains(query, StringComparison.OrdinalIgnoreCase))
                    results.Add(new JsonObject { ["path"] = relative, ["line"] = 0, ["text"] = "(file name)" });
                if (results.Count >= limit) return results;
                if (new FileInfo(file).Length > MaximumRead || !LooksText(file)) continue;
                int number = 0;
                foreach (string line in File.ReadLines(file))
                {
                    number++;
                    if (!line.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                    results.Add(new JsonObject { ["path"] = relative, ["line"] = number, ["text"] = line.Trim() is { Length: > 200 } long_ ? long_[..200] : line.Trim() });
                    if (results.Count >= limit) return results;
                }
            }
        return results;
    }

    private static IEnumerable<string> Files(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string folder = pending.Pop();
            string[] files, folders;
            try { files = Directory.GetFiles(folder); folders = Directory.GetDirectories(folder); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
            foreach (string file in files.Order(StringComparer.OrdinalIgnoreCase))
                if (!IsLink(file)) yield return file;
            foreach (string child in folders.OrderDescending(StringComparer.OrdinalIgnoreCase))
                if (!SkippedFolders.Contains(Path.GetFileName(child)) && !IsLink(child)) pending.Push(child);
        }
    }

    private static bool LooksText(string file)
    {
        Span<byte> head = stackalloc byte[512];
        using var stream = File.OpenRead(file);
        int read = stream.Read(head);
        return !head[..read].Contains((byte)0);
    }

    private static string? Inside(string root, string relative)
    {
        try
        {
            string full = Path.GetFullPath(Path.Combine(root, relative));
            string baseline = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(baseline, StringComparison.OrdinalIgnoreCase)) return null;
            string actualRoot = RealPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return RealPath(full).StartsWith(actualRoot, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }

    /** Resolve existing junctions/symlinks without requiring the final file to exist (new edits still work). */
    private static string RealPath(string path)
    {
        string full = Path.GetFullPath(path), current = Path.GetPathRoot(full)!;
        foreach (string part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo item = File.Exists(current) ? new FileInfo(current) : new DirectoryInfo(current);
            if (item.LinkTarget is not null)
                current = item.ResolveLinkTarget(true)?.FullName ?? throw new IOException("The path contains an unresolved link.");
        }
        return current;
    }
}
