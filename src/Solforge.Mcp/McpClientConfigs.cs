using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/**
 * Writes the one "sol" MCP server entry for Claude Code (project and user config), Cline, the Antigravity desktop app,
 * the Antigravity CLI (agy) and the custom Pi harness, which reads .pi/mcp.json in the project. Every entry runs the
 * stdio shim with --sol and the local upstream list, so it exposes Solforge plus the workspace MCPs; the old separate
 * Sol/Solforge entries are removed. The session token never goes into a config file.
 */
public static class McpClientConfigs
{
    /** The one client-facing MCP server: the aggregator that fronts every Sol/Solforge upstream. */
    public const string ServerName = "sol";
    /** Old entries replaced by sol when Solforge writes a client config. */
    private static readonly string[] LegacyNames = ["solforge", "gemini", "clinepass", "sol-testkit", "tiger-mcp"];

    /** One client config file that was written: client name and path. */
    public sealed record Written(string Client, string Path);

    /**
     * @param home The user profile folder; checks pass a scratch folder. @param servers The aggregate's upstreams when
     * an engine starts; the local list is written and the same one `sol` entry goes to every client, including Pi.
     */
    public static IReadOnlyList<Written> WriteAll(string projectRoot, string shimPath, string? home = null, string? appData = null,
        IReadOnlyList<SolMcpServers.Server>? servers = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        appData ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var written = new List<Written>();
        string? upstreams = servers is null ? null : SolMcpServers.WriteUpstreams(projectRoot, servers);
        var sol = SolMcpServers.ClientEntry(projectRoot, shimPath, upstreams);
        // Claude Code: project-scoped .mcp.json at the project root.
        string claude = Path.Combine(projectRoot, ".mcp.json");
        Merge(claude, sol.DeepClone().AsObject());
        written.Add(new("Claude Code", claude));
        // Claude Code also reads user-level servers from ~/.claude.json; replace the old separate entries there too.
        string claudeGlobal = Path.Combine(home, ".claude.json");
        if (File.Exists(claudeGlobal))
        {
            UpdateClaudeGlobal(claudeGlobal, projectRoot, sol);
            written.Add(new("Claude Code (global)", claudeGlobal));
        }
        // Cline CLI keeps its MCP settings under ~/.cline; the VS Code extension in its global storage (only if installed).
        var cline = sol.DeepClone().AsObject();
        cline["disabled"] = false;
        cline["autoApprove"] = new JsonArray();
        string clineCli = Path.Combine(home, ".cline", "data", "settings", "cline_mcp_settings.json");
        Merge(clineCli, cline.DeepClone().AsObject());
        written.Add(new("Cline", clineCli));
        string clineCode = Path.Combine(appData, "Code", "User", "globalStorage", "saoudrizwan.claude-dev");
        if (Directory.Exists(clineCode))
        {
            string path = Path.Combine(clineCode, "settings", "cline_mcp_settings.json");
            Merge(path, cline.DeepClone().AsObject());
            written.Add(new("Cline (VS Code)", path));
        }
        // Antigravity desktop app: ~/.gemini/antigravity/mcp_config.json.
        string antigravity = Path.Combine(home, ".gemini", "antigravity", "mcp_config.json");
        Merge(antigravity, sol.DeepClone().AsObject());
        written.Add(new("Antigravity (desktop)", antigravity));
        // Antigravity CLI (agy): ~/.gemini/config/mcp_config.json, the shape "agy mcp add" writes.
        string agy = Path.Combine(home, ".gemini", "config", "mcp_config.json");
        var agyEntry = sol.DeepClone().AsObject();
        agyEntry["disabled"] = false;
        Merge(agy, agyEntry);
        written.Add(new("Antigravity CLI", agy));
        // The custom Pi harness: its project-level .pi/mcp.json is read after the project is trusted.
        if (servers is not null) written.Add(WritePi(projectRoot, servers, upstreams));
        return written;
    }

    /**
     * Writes the one `sol` entry into the Pi harness's project MCP file and keeps the user's other Pi settings and
     * unrelated servers. Upstream commands and their env stay in the aggregate's local list, not this file.
     */
    public static Written WritePi(string projectRoot, IReadOnlyList<SolMcpServers.Server> servers, string? upstreams = null)
    {
        string path = Path.Combine(projectRoot, ".pi", "mcp.json");
        upstreams ??= SolMcpServers.WriteUpstreams(projectRoot, servers);
        string shim = servers.FirstOrDefault(server => server.Name == SolMcpServers.Solforge)?.Command
            ?? SolforgeMcpService.FindShim() ?? "solforge-mcp.exe";
        var sol = SolMcpServers.ClientEntry(projectRoot, shim, upstreams);
        sol["exposure"] = "direct";
        sol["description"] = "Sol: Solforge editor, sol-testkit, tiger-mcp and delegate tools in one server.";
        Update(path, entries => SetSol(entries, sol));
        return new("Pi harness", path);
    }

    /**
     * Sets mcpServers.sol, removes the old separate Sol/Solforge entries, and keeps every unrelated server. An empty
     * file (the agy installer leaves one) counts as {}; a damaged one is kept beside as .bak and replaced.
     */
    public static void Merge(string path, JsonObject server) => Update(path, entries => SetSol(entries, server));

    /** Replaces the legacy entries with the aggregate. */
    private static void SetSol(JsonObject entries, JsonObject sol)
    {
        foreach (string name in LegacyNames) entries.Remove(name);
        entries[ServerName] = sol;
    }

    /**
     * Loads the file's mcpServers (created if missing), lets @param change edit it and writes the file only when the
     * text changed; every other key is kept.
     */
    public static void Update(string path, Action<JsonObject> change) =>
        UpdateRoot(path, root =>
        {
            if (root["mcpServers"] is not JsonObject servers) root["mcpServers"] = servers = new JsonObject();
            change(servers);
        });

    /** Loads a whole JSON object, lets @param change edit it and writes it only when the text changed. */
    private static void UpdateRoot(string path, Action<JsonObject> change)
    {
        var root = new JsonObject();
        if (File.Exists(path) && File.ReadAllText(path) is { } existing && !string.IsNullOrWhiteSpace(existing))
        {
            try { root = JsonNode.Parse(existing) as JsonObject ?? new JsonObject(); }
            catch (JsonException) { File.Copy(path, path + ".bak", true); root = new JsonObject(); }
        }
        change(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string text = root.ToJsonString(McpJson.Indented);
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        File.WriteAllText(path, text);
    }

    /** Replaces legacy Sol entries in Claude's user-level servers and in the matching project entry, if present. */
    private static void UpdateClaudeGlobal(string path, string projectRoot, JsonObject sol)
    {
        UpdateRoot(path, root =>
        {
            if (root["mcpServers"] is JsonObject global) SetSol(global, sol.DeepClone().AsObject());
            if (root["projects"] is not JsonObject projects) return;
            foreach (var (folder, entry) in projects)
                if (entry?["mcpServers"] is JsonObject servers && SameFolder(folder, projectRoot))
                    SetSol(servers, sol.DeepClone().AsObject());
        });
    }

    /** Whether two paths name the same folder (~/.claude.json keys use forward slashes). */
    private static bool SameFolder(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

}
