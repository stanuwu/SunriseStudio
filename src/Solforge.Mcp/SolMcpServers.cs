/**
 * File: The upstreams of the one `sol` MCP server every engine started from the AI workspace gets (solforge, gemini,
 * clinepass and the workspace's other MCPs such as sol-testkit and tiger-mcp), their local list for the aggregate, a
 * run copy of solforge-mcp.exe that a publish cannot replace or kill, and the checks behind the AI panel's MCP warnings.
 */
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/**
 * Collects the MCP servers for an engine start. Solforge's own servers (solforge, gemini, clinepass) always run the
 * current shim; the others are carried as the user configured them: every stdio server in a .mcp.json at or above the
 * project, then sol-testkit and tiger-mcp from ~/.claude.json, agy's or Cline's MCP settings.
 */
public static class SolMcpServers
{
    public const string Testkit = "sol-testkit", Tiger = "tiger-mcp";
    /** The editor's own upstream entry; the client-facing server is `McpClientConfigs.ServerName` (sol). */
    public const string Solforge = "solforge";

    /** Workspace MCPs looked up in the user's engine configs when no .mcp.json above the project has them. */
    public static readonly string[] Carried = [Testkit, Tiger];

    /** The engines Solforge starts with MCP servers. */
    public enum Engine { Claude, Agy, Cline, Pi }

    /**
     * One stdio server. @param Owned True for Solforge's own (shim) servers, which are rewritten on every start;
     * carried ones are only added where the engine's config does not name them yet.
     */
    public sealed record Server(string Name, string Command, IReadOnlyList<string> Args, JsonObject Env, bool Owned, string Source);

    /**
     * Every server for a start. @param gemini adds ask_gemini (agy installed); @param clinePass adds ask_clinepass
     * (setting on, cline installed); @param home the user profile (checks pass a scratch folder).
     */
    public static IReadOnlyList<Server> Collect(string projectRoot, string shimPath, bool gemini, bool clinePass, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var servers = new List<Server>
        {
            new(Solforge, shimPath, ["--project", projectRoot], new JsonObject(), true, "Solforge")
        };
        if (gemini) servers.Add(new(AgyDelegate.ServerName, shimPath, ["--gemini"], new JsonObject(), true, "Solforge"));
        if (clinePass) servers.Add(new(ClineDelegate.ServerName, shimPath, ["--clinepass"], new JsonObject(), true, "Solforge"));
        var owned = new HashSet<string>([Solforge, McpClientConfigs.ServerName, AgyDelegate.ServerName, ClineDelegate.ServerName], StringComparer.OrdinalIgnoreCase);
        void Add(string name, JsonNode? node, string source)
        {
            if (owned.Contains(name) || servers.Any(server => server.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
            if (Parse(name, node, source) is { } server) servers.Add(server);
        }
        // Every stdio server in a .mcp.json at or above the project (nearest wins).
        for (var folder = new DirectoryInfo(Path.GetFullPath(projectRoot)); folder is not null; folder = folder.Parent)
            if (Read(Path.Combine(folder.FullName, ".mcp.json"))?["mcpServers"] is JsonObject found)
                foreach (var (name, node) in found) Add(name, node, Path.Combine(folder.FullName, ".mcp.json"));
        // The named workspace MCPs from the user's own engine configs.
        string claudeJson = Path.Combine(home, ".claude.json");
        var sources = new List<(JsonObject? Servers, string Source)>();
        if (Read(claudeJson) is JsonObject claude)
        {
            if (claude["projects"] is JsonObject projects)
                foreach (var (key, value) in projects)
                    if (SamePath(key, projectRoot)) sources.Add((value?["mcpServers"] as JsonObject, claudeJson));
            sources.Add((claude["mcpServers"] as JsonObject, claudeJson));
        }
        foreach (string file in new[] { AgyConfig(home), ClineConfig(home) })
            sources.Add((Read(file)?["mcpServers"] as JsonObject, file));
        foreach (string name in Carried)
            foreach (var (found, source) in sources)
                if (found?[name] is { } node) { Add(name, node, source); break; }
        // The previous aggregate keeps a carried server available after its client entry was replaced by sol.
        foreach (var previous in ReadUpstreams(projectRoot))
            if (!owned.Contains(previous.Name) && !servers.Any(server => server.Name.Equals(previous.Name, StringComparison.OrdinalIgnoreCase)))
                servers.Add(previous with { Owned = false });
        return servers;
    }

    /**
     * UI-UE5 §4.4.7: the per-user upstreams for a Solforge AI started without the editor or a project: solforge (no
     * --project: the shim attaches to whichever editor runs, else serves headless), the delegates, and the carried workspace
     * MCPs from the user's own engine configs. Nothing from a project's .mcp.json.
     */
    public static IReadOnlyList<Server> CollectDefault(string shimPath, bool gemini, bool clinePass, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var servers = new List<Server> { new(Solforge, shimPath, [], new JsonObject(), true, "Solforge") };
        if (gemini) servers.Add(new(AgyDelegate.ServerName, shimPath, ["--gemini"], new JsonObject(), true, "Solforge"));
        if (clinePass) servers.Add(new(ClineDelegate.ServerName, shimPath, ["--clinepass"], new JsonObject(), true, "Solforge"));
        var sources = new List<(JsonObject? Servers, string Source)>();
        string claudeJson = Path.Combine(home, ".claude.json");
        if (Read(claudeJson) is JsonObject claude) sources.Add((claude["mcpServers"] as JsonObject, claudeJson));
        foreach (string file in new[] { AgyConfig(home), ClineConfig(home) })
            sources.Add((Read(file)?["mcpServers"] as JsonObject, file));
        foreach (string name in Carried)
            foreach (var (found, source) in sources)
                if (found?[name] is { } node && Parse(name, node, source) is { } server) { servers.Add(server); break; }
        return servers;
    }

    /** %LOCALAPPDATA%\Solforge\mcp\sol-upstreams-default.json (McpDiscovery.DefaultUpstreamsFile). */
    public static string DefaultUpstreamFile(string? folder = null) =>
        folder is null ? McpDiscovery.DefaultUpstreamsFile : Path.Combine(folder, "sol-upstreams-default.json");

    /** Writes the per-user default upstream list and returns its path (its env may hold credentials: kept outside projects). */
    public static string WriteDefaultUpstreams(IEnumerable<Server> servers, string? folder = null)
    {
        var list = new JsonArray();
        foreach (var server in servers.Where(server => !server.Name.Equals(McpClientConfigs.ServerName, StringComparison.OrdinalIgnoreCase)))
        {
            var args = new JsonArray();
            foreach (string arg in server.Args) args.Add(arg);
            list.Add(new JsonObject { ["name"] = server.Name, ["command"] = server.Command, ["args"] = args, ["env"] = server.Env.DeepClone() });
        }
        string path = DefaultUpstreamFile(folder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, new JsonObject { ["project"] = null, ["servers"] = list }.ToJsonString(McpJson.Indented));
        File.Move(temporary, path, true);
        return path;
    }

    /** The aggregate's local upstream list; its path is stable per project. */
    public static string UpstreamFile(string projectRoot, string? folder = null)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(projectRoot).ToUpperInvariant())))[..12].ToLowerInvariant();
        return Path.Combine(folder ?? McpDiscovery.Folder, $"sol-upstreams-{key}.json");
    }

    /** Writes the aggregate's upstream commands outside the project (their env may hold credentials) and returns its path. */
    public static string WriteUpstreams(string projectRoot, IEnumerable<Server> servers, string? folder = null)
    {
        var list = new JsonArray();
        foreach (var server in servers.Where(server => !server.Name.Equals(McpClientConfigs.ServerName, StringComparison.OrdinalIgnoreCase)))
        {
            var args = new JsonArray();
            foreach (string arg in server.Args) args.Add(arg);
            list.Add(new JsonObject
            {
                ["name"] = server.Name, ["command"] = server.Command, ["args"] = args,
                ["env"] = server.Env.DeepClone()
            });
        }
        string path = UpstreamFile(projectRoot, folder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Atomic and uniquely named: a launch and a background publish may write the same project's list at once.
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, new JsonObject { ["project"] = Path.GetFullPath(projectRoot), ["servers"] = list }.ToJsonString(McpJson.Indented));
        File.Move(temporary, path, true);
        return path;
    }

    /** One client entry: the `sol` aggregator, with the upstream list when one is available. */
    public static JsonObject ClientEntry(string projectRoot, string shimPath, string? upstreamFile)
    {
        var args = new JsonArray("--sol", "--project", projectRoot);
        if (!string.IsNullOrWhiteSpace(upstreamFile))
        {
            args.Add("--upstreams");
            args.Add(upstreamFile);
        }
        return new JsonObject
        {
            ["type"] = "stdio", ["command"] = shimPath, ["args"] = args, ["env"] = new JsonObject()
        };
    }

    /** Previous aggregate upstream servers, or none. */
    public static IReadOnlyList<Server> ReadUpstreams(string projectRoot, string? folder = null)
    {
        string path = UpstreamFile(projectRoot, folder);
        try
        {
            if (Read(path)?["servers"] is not JsonArray list) return [];
            var servers = new List<Server>();
            foreach (var node in list.OfType<JsonObject>())
            {
                string? name = node["name"]?.ToString();
                string? command = node["command"]?.ToString();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(command)) continue;
                var args = node["args"] is JsonArray array ? array.Select(arg => arg?.ToString() ?? "").ToList() : [];
                var env = node["env"] as JsonObject ?? new JsonObject();
                servers.Add(new(name!, command!, args, env.DeepClone().AsObject(), false, path));
            }
            return servers;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    /** The servers one engine gets: an engine never gets the server that delegates to itself. */
    public static IEnumerable<Server> For(Engine engine, IEnumerable<Server> servers) => servers.Where(server =>
        !(engine == Engine.Agy && server.Name == AgyDelegate.ServerName) && !(engine == Engine.Cline && server.Name == ClineDelegate.ServerName));

    /** The server as Claude Code, agy or Cline writes it. */
    public static JsonObject Entry(Engine engine, Server server)
    {
        var args = new JsonArray();
        foreach (string arg in server.Args) args.Add(arg);
        var entry = new JsonObject();
        if (engine is Engine.Claude or Engine.Pi) entry["type"] = "stdio";
        entry["command"] = server.Command;
        entry["args"] = args;
        if (engine is Engine.Claude or Engine.Pi || server.Env.Count > 0) entry["env"] = server.Env.DeepClone();
        if (engine is not (Engine.Claude or Engine.Pi)) entry["disabled"] = false;
        if (engine == Engine.Cline) entry["autoApprove"] = new JsonArray();
        if (engine == Engine.Pi)
        {
            entry["exposure"] = "direct";
            if (server.Name == Solforge)
                entry["description"] = "Solforge live editor context and tools.";
        }
        return entry;
    }

    /** Claude Code's --mcp-config body: one aggregate entry, exactly as Solforge writes it. */
    public static JsonObject ClaudeConfig(IEnumerable<Server> servers)
    {
        var list = servers.ToList();
        var sol = list.FirstOrDefault(server => server.Name == Solforge);
        if (sol is null) return new JsonObject { ["mcpServers"] = new JsonObject() };
        string project = ProjectOf(sol) ?? Environment.CurrentDirectory;
        string upstreams = WriteUpstreams(project, list);
        return new JsonObject
        {
            ["mcpServers"] = new JsonObject { [McpClientConfigs.ServerName] = ClientEntry(project, sol.Command, upstreams) }
        };
    }

    /**
     * Replaces an engine's separate Sol/Solforge entries with the one aggregate. The aggregate's upstream list is
     * written outside the project, so this file holds no carried server env.
     */
    public static IReadOnlyList<string> MergeInto(string path, Engine engine, IEnumerable<Server> servers)
    {
        var list = servers.ToList();
        var sol = list.FirstOrDefault(server => server.Name == Solforge);
        if (sol is null) return [];
        string project = ProjectOf(sol) ?? Environment.CurrentDirectory;
        string upstreams = WriteUpstreams(project, list);
        var entry = ClientEntry(project, sol.Command, upstreams);
        if (engine == Engine.Cline) { entry["disabled"] = false; entry["autoApprove"] = new JsonArray(); }
        if (engine == Engine.Agy) entry["disabled"] = false;
        McpClientConfigs.Merge(path, entry);
        return [];
    }

    /** The --project value of a Solforge upstream, or null. */
    private static string? ProjectOf(Server server)
    {
        for (int i = 0; i + 1 < server.Args.Count; i++)
            if (server.Args[i] == "--project") return server.Args[i + 1];
        return null;
    }

    /** True when the entry runs solforge-mcp (any copy or path), so Solforge may refresh it. */
    public static bool IsShimEntry(JsonObject entry) =>
        entry["command"] is JsonValue value && value.TryGetValue(out string? command)
        && Path.GetFileName(command.Trim('"')).StartsWith("solforge-mcp", StringComparison.OrdinalIgnoreCase);

    /** ~/.gemini/config/mcp_config.json, the Antigravity CLI's MCP file. */
    public static string AgyConfig(string home) => Path.Combine(home, ".gemini", "config", "mcp_config.json");

    /** ~/.cline/data/settings/cline_mcp_settings.json, the Cline CLI's MCP file. */
    public static string ClineConfig(string home) => Path.Combine(home, ".cline", "data", "settings", "cline_mcp_settings.json");

    /**
     * The shim the engines run: a copy of the published solforge-mcp.exe under @param folder named after its size and
     * time (solforge-mcp-&lt;stamp&gt;.exe). publish.ps1 overwrites, and killing solforge-mcp before a publish ends,
     * only the published file, so running sessions keep their servers and a new start never reads a half-written exe.
     * Falls back to the newest earlier copy while the published one is being written, then to the published one.
     */
    public static string RunCopy(string published, string folder)
    {
        try
        {
            var source = new FileInfo(published);
            if (!source.Exists || !published.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return published;
            Directory.CreateDirectory(folder);
            string stamp = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes($"{source.Length}:{source.LastWriteTimeUtc.Ticks}")))[..10].ToLowerInvariant();
            string target = Path.Combine(folder, $"solforge-mcp-{stamp}.exe");
            if (!(File.Exists(target) && new FileInfo(target).Length == source.Length))
            {
                // A publish holds the file open for writing: the copy fails and the last good copy runs instead.
                string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var input = new FileStream(published, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = File.Create(temporary))
                    input.CopyTo(output);
                File.Move(temporary, target, true);
            }
            foreach (var old in new DirectoryInfo(folder).EnumerateFiles("solforge-mcp-*.exe")
                         .Where(file => !file.FullName.Equals(target, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(file => file.LastWriteTimeUtc).Skip(2))
                try { old.Delete(); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            return target;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try
            {
                return new DirectoryInfo(folder).Exists
                    ? new DirectoryInfo(folder).EnumerateFiles("solforge-mcp-*.exe").MaxBy(file => file.LastWriteTimeUtc)?.FullName ?? published
                    : published;
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { return published; }
        }
    }

    /**
     * File checks for each server: the command (a full path, or found on @param path with PATHEXT) and every
     * fully qualified argument (a script, package folder or project) must exist. One line per problem.
     */
    public static IReadOnlyList<string> CheckFiles(IEnumerable<Server> servers, string path, string? pathExt = null)
    {
        var problems = new List<string>();
        foreach (var server in servers)
        {
            if (Path.IsPathFullyQualified(server.Command) ? !File.Exists(server.Command) : EngineCommands.Resolve(server.Command, path, pathExt) is null)
                problems.Add($"{server.Name}: {server.Command} not found ({server.Source})");
            foreach (string arg in server.Args)
                if (Path.IsPathFullyQualified(arg) && !File.Exists(arg) && !Directory.Exists(arg))
                    problems.Add($"{server.Name}: {arg} not found ({server.Source})");
        }
        return problems;
    }

    /**
     * Starts the server, sends initialize and waits for its reply: null when it answers with serverInfo, else what
     * went wrong. The process is killed afterwards.
     */
    public static async Task<string?> Probe(string command, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(command)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false)
        };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        Process process;
        try { process = Process.Start(info) ?? throw new InvalidOperationException("no process"); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        { return "could not start: " + exception.Message; }
        using (process)
        {
            using var cancel = new CancellationTokenSource(timeout);
            try
            {
                _ = process.StandardError.ReadToEndAsync(cancel.Token);
                await process.StandardInput.WriteLineAsync(
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\","
                    + "\"capabilities\":{},\"clientInfo\":{\"name\":\"solforge-check\",\"version\":\"1\"}}}");
                await process.StandardInput.FlushAsync();
                while (await process.StandardOutput.ReadLineAsync(cancel.Token) is { } line)
                {
                    JsonNode? reply;
                    try { reply = JsonNode.Parse(line); }
                    catch (JsonException) { continue; }
                    if (reply?["id"]?.ToJsonString() != "1") continue;
                    if (reply["result"]?["serverInfo"] is not null) return null;
                    return reply["error"]?["message"]?.ToString() ?? "no serverInfo in its reply";
                }
                return "exited without answering initialize";
            }
            catch (OperationCanceledException) { return $"no answer to initialize within {timeout.TotalSeconds:0} s"; }
            catch (IOException exception) { return process.HasExited ? "exited without answering initialize" : exception.Message; }
            finally
            {
                try { if (!process.HasExited) process.Kill(true); }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
    }

    /** A stdio entry in a config file as a server, or null (disabled, remote or no command). */
    private static Server? Parse(string name, JsonNode? node, string source)
    {
        if (node is not JsonObject entry || entry["command"] is not JsonValue command || !command.TryGetValue(out string? text)
            || text.Length == 0 || entry["disabled"]?.GetValueKind() == JsonValueKind.True) return null;
        if (entry["type"] is JsonValue type && type.TryGetValue(out string? kind) && kind != "stdio") return null;
        var args = entry["args"] is JsonArray array
            ? array.Select(arg => arg is JsonValue value && value.TryGetValue(out string? s) ? s : arg?.ToJsonString() ?? "").ToList()
            : new List<string>();
        return new(name, text, args, entry["env"] is JsonObject env ? env.DeepClone().AsObject() : new JsonObject(), false, source);
    }

    /** A JSON file read while its owner may be writing it, or null (missing, empty or damaged). */
    private static JsonNode? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string text = reader.ReadToEnd();
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /** Whether two paths name the same folder (~/.claude.json keys use forward slashes). */
    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
