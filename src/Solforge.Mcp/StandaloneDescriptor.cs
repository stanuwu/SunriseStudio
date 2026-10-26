/**
 * File: %LOCALAPPDATA%\Solforge\mcp\solforge-ai-mcp.json (UI-UE5 §4.4.7), the interface contract for a Solforge AI that
 * the user starts directly, without the editor: which shim to run (solforge-mcp --sol --upstreams <per-user default>),
 * the destiny-modder launch definition and the skill folders. solforge-next's standalone overlay does NOT read this file
 * (solforge-standalone.ts writeAppOverlay); it is a description for clients and tools that choose to read it. What actually
 * gives a directly launched Solforge AI the Solforge MCP is StandaloneRegistration (~/.claude.json + importClaudeServers).
 * The file holds paths only, never a credential.
 */
using System.Text.Json.Nodes;
using Solforge.Harness.Shared;

namespace Solforge.Mcp;

/** Writes and reads the standalone descriptor. */
public static class StandaloneDescriptor
{
    public const int Version = 1;

    /** The descriptor as JSON. @param destinyModder the shared-resources preflight's server, or null when it did not pass. */
    public static JsonObject Build(string shimPath, string upstreamsFile, SharedMcpServerDefinition? destinyModder, IEnumerable<string> skillDirs)
    {
        var descriptor = new JsonObject
        {
            ["version"] = Version,
            ["shim"] = Path.GetFullPath(shimPath),
            ["args"] = new JsonArray("--sol", "--upstreams", Path.GetFullPath(upstreamsFile)),
            ["failOnStartupError"] = false,
            ["toolCallTimeoutMs"] = 180000,
            ["written"] = DateTimeOffset.UtcNow.ToString("O"),
            ["skillDirs"] = new JsonArray([.. skillDirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Select(dir => (JsonNode)dir)])
        };
        if (destinyModder is { } server)
        {
            var env = new JsonObject();
            foreach (var (name, value) in server.Environment) env[name] = value;
            descriptor["destinyModder"] = new JsonObject
            {
                ["name"] = server.Name, ["command"] = server.Command,
                ["args"] = new JsonArray([.. server.Arguments.Select(arg => (JsonNode)arg)]),
                ["cwd"] = server.WorkingDirectory, ["env"] = env, ["failOnStartupError"] = false
            };
        }
        else descriptor["destinyModder"] = null;
        return descriptor;
    }

    /** Writes the descriptor atomically; returns its path, or null when the folder is not writable. */
    public static string? Write(string shimPath, string upstreamsFile, SharedMcpServerDefinition? destinyModder, IEnumerable<string> skillDirs,
        string? path = null)
    {
        path ??= McpDiscovery.StandaloneDescriptorFile;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, Build(shimPath, upstreamsFile, destinyModder, skillDirs).ToJsonString(McpJson.Indented));
            File.Move(temporary, path, true);
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /**
     * Editor start without the shared-resources snapshot (SolforgeMcpService.PublishStandaloneFiles with shared null): a
     * missing or damaged descriptor is written fresh; an existing one is rewritten only when it is stale, i.e. an older
     * version, or its shim or upstreams file is not this editor's current run copy (run copies are pruned, so an old path
     * stops existing), or the shim it names is gone. A stale rewrite keeps the existing destinyModder entry and the skill
     * folders that still exist, so a richer descriptor written from the Solforge AI settings keeps its content.
     * The file holds paths only; nothing read here is a credential, and nothing is logged.
     * @returns "written" (fresh), "rewritten" (was stale), "current" (left alone), or null when the folder is not writable.
     */
    public static string? RefreshIfStale(string shimPath, string upstreamsFile, string? path = null)
    {
        path ??= McpDiscovery.StandaloneDescriptorFile;
        var existing = Read(path);
        if (existing is null) return Write(shimPath, upstreamsFile, null, [], path) is null ? null : "written";
        string shim = Path.GetFullPath(shimPath), upstreams = Path.GetFullPath(upstreamsFile);
        bool current = existing["version"] is JsonValue version && version.TryGetValue<int>(out int number) && number == Version
            && existing["shim"] is JsonValue shimValue && shimValue.TryGetValue<string>(out string? oldShim)
            && string.Equals(oldShim, shim, StringComparison.OrdinalIgnoreCase) && File.Exists(oldShim)
            && existing["args"] is JsonArray args && args.Count == 3 && args[2] is JsonValue upstreamsValue
            && upstreamsValue.TryGetValue<string>(out string? oldUpstreams)
            && string.Equals(oldUpstreams, upstreams, StringComparison.OrdinalIgnoreCase);
        if (current) return "current";
        var descriptor = Build(shim, upstreams, null, []);
        // Keep what the settings path contributed: the destiny-modder definition and the skill folders still on disk.
        if (existing["destinyModder"] is JsonObject destinyModder) descriptor["destinyModder"] = destinyModder.DeepClone();
        if (existing["skillDirs"] is JsonArray oldDirs)
            descriptor["skillDirs"] = new JsonArray([.. oldDirs
                .Select(node => node is JsonValue value && value.TryGetValue<string>(out string? dir) ? dir : null)
                .Where(dir => !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(dir => (JsonNode)dir!)]);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, descriptor.ToJsonString(McpJson.Indented));
            File.Move(temporary, path, true);
            return "rewritten";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /** The current descriptor, or null when missing or damaged. */
    public static JsonObject? Read(string? path = null)
    {
        path ??= McpDiscovery.StandaloneDescriptorFile;
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }
}
