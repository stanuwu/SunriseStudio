using System.Diagnostics;
using System.Text.Json;

namespace Solforge.Mcp;

/** Where a running Solforge says its MCP server is; the stdio shim reads it. One file per Solforge process. */
public sealed record McpDiscoveryEntry(int Pid, string Url, string Token, string? Project, DateTimeOffset Started);

/** Discovery files that let MCP clients and the shim find a running Solforge server. */
public static class McpDiscovery
{
    /** %LOCALAPPDATA%\Solforge\mcp; SOLFORGE_MCP_DISCOVERY overrides it for checks. */
    public static string Folder =>
        Environment.GetEnvironmentVariable("SOLFORGE_MCP_DISCOVERY") is { Length: > 0 } custom ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Solforge", "mcp");

    /** UI-UE5 §4.4: the editor's tool catalogue, rewritten on every editor start (ToolsCache). */
    public static string ToolsCacheFile => Path.Combine(Folder, "tools-cache.json");
    /** UI-UE5 §4.4.7: the per-user upstream list without project-specific entries (SolMcpServers.WriteDefaultUpstreams). */
    public static string DefaultUpstreamsFile => Path.Combine(Folder, "sol-upstreams-default.json");
    /**
     * UI-UE5 §4.4.7: the standalone descriptor (StandaloneDescriptor). solforge-next does not read it; a directly launched
     * Solforge AI gets the Solforge MCP through StandaloneRegistration instead.
     */
    public static string StandaloneDescriptorFile => Path.Combine(Folder, "solforge-ai-mcp.json");

    /**
     * The live editor for a project, cached for maxAge: the stdio shim re-runs discovery on every tools/list and tools/call
     * (UI-UE5 §4.4.3) without rereading the folder each time. A cached entry whose process is gone is dropped at once.
     */
    public static McpDiscoveryEntry? FindCached(string? project, TimeSpan maxAge)
    {
        string key = project ?? "";
        lock (cache)
        {
            if (cache.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.At < maxAge && (hit.Entry is null || Alive(hit.Entry.Pid)))
                return hit.Entry;
        }
        var found = Find(project);
        lock (cache) cache[key] = (found, DateTimeOffset.UtcNow);
        return found;
    }

    /** Forgets cached lookups (after a failed request to the cached editor). */
    public static void Invalidate()
    {
        lock (cache) cache.Clear();
    }

    private static readonly Dictionary<string, (McpDiscoveryEntry? Entry, DateTimeOffset At)> cache = new(StringComparer.OrdinalIgnoreCase);

    /** Writes server-{pid}.json for the entry and returns its path. */
    public static string Write(McpDiscoveryEntry entry, string? folder = null)
    {
        folder ??= Folder;
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"server-{entry.Pid}.json");
        string temporary = path + ".tmp";
        using (var stream = File.Create(temporary))
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteNumber("pid", entry.Pid);
            json.WriteString("url", entry.Url);
            json.WriteString("token", entry.Token);
            json.WriteString("project", entry.Project);
            json.WriteString("started", entry.Started);
            json.WriteEndObject();
        }
        File.Move(temporary, path, true);
        return path;
    }

    /** Deletes the discovery file of a process; failures are ignored. */
    public static void Remove(int pid, string? folder = null)
    {
        try { File.Delete(Path.Combine(folder ?? Folder, $"server-{pid}.json")); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /** Deletes server-<pid>.json files whose process is gone; returns how many were removed. */
    public static int CleanStale(string? folder = null, Func<int, bool>? alive = null)
    {
        folder ??= Folder;
        alive ??= Alive;
        if (!Directory.Exists(folder)) return 0;
        int removed = 0;
        foreach (string file in Directory.EnumerateFiles(folder, "server-*.json"))
        {
            if (Read(file) is { } entry && alive(entry.Pid)) continue;
            try { File.Delete(file); removed++; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    /** The newest live server for the project; with no match, the newest live server of any project. */
    public static McpDiscoveryEntry? Find(string? project, string? folder = null, bool checkProcess = true)
    {
        folder ??= Folder;
        if (!Directory.Exists(folder)) return null;
        var entries = new List<McpDiscoveryEntry>();
        foreach (string file in Directory.EnumerateFiles(folder, "server-*.json"))
            if (Read(file) is { } entry && (!checkProcess || Alive(entry.Pid))) entries.Add(entry);
        entries.Sort((a, b) => b.Started.CompareTo(a.Started));
        string? wanted = Normalize(project);
        return entries.FirstOrDefault(entry => wanted is not null && Normalize(entry.Project) == wanted) ?? entries.FirstOrDefault();
    }

    /** Reads one discovery file; null when it is missing or malformed. */
    public static McpDiscoveryEntry? Read(string file)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(file));
            var root = document.RootElement;
            return new McpDiscoveryEntry(root.GetProperty("pid").GetInt32(), root.GetProperty("url").GetString()!,
                root.GetProperty("token").GetString()!,
                root.TryGetProperty("project", out var project) && project.ValueKind == JsonValueKind.String ? project.GetString() : null,
                root.TryGetProperty("started", out var started) && started.TryGetDateTimeOffset(out var time) ? time : DateTimeOffset.MinValue);
        }
        catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException
            or InvalidOperationException or UnauthorizedAccessException) { return null; }
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path).TrimEnd('\\', '/').ToUpperInvariant(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static bool Alive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception) { return false; }
    }
}
