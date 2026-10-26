/**
 * File: %LOCALAPPDATA%\Solforge\mcp\tools-cache.json (UI-UE5 §4.4.3), the editor's tool catalogue as of its last start.
 * The stdio shim lists the union of this cache, its own headless tools and its other upstreams, so tool names stay stable
 * when the editor is closed and when a client ignores notifications/tools/list_changed. Each entry carries the
 * requiresEditor metadata (SolforgeTools.RequiresEditor).
 */
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/** One cached tool as tools/list shows it, plus whether it needs the editor. */
public sealed record CachedTool(string Name, JsonObject Entry, bool RequiresEditor);

/** Reads and writes the editor tool catalogue cache. */
public static class ToolsCache
{
    public const int Version = 1;
    /** The _meta key the shim puts on every Solforge tool it lists. */
    public const string MetaKey = "solforge/requiresEditor";

    /** The tools/list entry of a tool, exactly as McpDispatcher lists it, with the requiresEditor _meta. */
    public static JsonObject Entry(McpTool tool) => new()
    {
        ["name"] = tool.Name,
        ["description"] = tool.RequiresApproval ? tool.Description + " Needs an approve click in Solforge." : tool.Description,
        ["inputSchema"] = tool.InputSchema.DeepClone(),
        ["annotations"] = new JsonObject { ["readOnlyHint"] = !tool.RequiresApproval },
        ["_meta"] = new JsonObject { [MetaKey] = SolforgeTools.RequiresEditor(tool) }
    };

    /** Writes the catalogue atomically; failures are ignored (the cache is an optimisation for the shim). */
    public static string? Write(IEnumerable<McpTool> tools, string version, string? path = null)
    {
        path ??= McpDiscovery.ToolsCacheFile;
        try
        {
            var list = new JsonArray();
            foreach (var tool in tools) list.Add(Entry(tool));
            var root = new JsonObject
            {
                ["version"] = Version, ["solforge"] = version, ["written"] = DateTimeOffset.UtcNow.ToString("O"),
                ["tools"] = list
            };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, root.ToJsonString(McpJson.Indented));
            File.Move(temporary, path, true);
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    /** The cached tools, or none when the cache is missing, damaged or from another format version. */
    public static IReadOnlyList<CachedTool> Read(string? path = null)
    {
        path ??= McpDiscovery.ToolsCacheFile;
        try
        {
            if (!File.Exists(path)) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (JsonNode.Parse(stream) is not JsonObject root || root["version"]?.GetValue<int>() != Version
                || root["tools"] is not JsonArray tools) return [];
            var result = new List<CachedTool>();
            foreach (var node in tools.OfType<JsonObject>())
            {
                if (node["name"]?.ToString() is not { Length: > 0 } name) continue;
                bool editor = node["_meta"]?[MetaKey]?.GetValueKind() != JsonValueKind.False;
                result.Add(new CachedTool(name, node.DeepClone().AsObject(), editor));
            }
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or FormatException) { return []; }
    }
}
