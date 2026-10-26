/**
 * File: Builds the launch command, MCP config and prompt hook files for starting supported coding-agent CLIs
 * from the AI workspace (editor side: Editor/Ai/ and StudioForm.Ai*.cs).
 */
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/**
 * How Solforge starts Claude Code: its own MCP config (no project .mcp.json approval needed), a prompt hook
 * that injects the live context, a system prompt naming the Solforge tools, and every folder Solforge has open.
 */
public static class EngineLaunch
{
    // No double quotes or percent signs: the text passes through cmd.exe and npm's .cmd shim.
    public const string SystemPrompt =
        "You are running inside Solforge's AI workspace (the Project Sol game editor). When the user says 'this', 'that', " +
        "'here' or 'what I'm looking at', they mean Solforge's live context: call the sol MCP tools get_context and " +
        "get_open_documents before searching files. .solforge/SESSION.md in the project holds the session context, and the " +
        "solforge-* skills are playbooks for common jobs (new weapon or armor, map bounds, patrol content, enemies, missions): " +
        "read the matching one first.";

    /** Added when the gemini delegate is an upstream of sol (agy installed). Safe through cmd. */
    public const string GeminiPrompt =
        "The sol MCP tool ask_gemini hands a self-contained task to Gemini on the user's own Antigravity CLI login; " +
        "use it when the user asks for Gemini, a second opinion or a very large read.";

    /** Added when the clinepass delegate is an upstream of sol (cline installed). Safe through cmd. */
    public const string ClinePassPrompt =
        "The sol MCP tool ask_clinepass hands a self-contained task to an open-weight ClinePass model on the user's own Cline login; " +
        "use it when the user asks for ClinePass, Cline or one of its models, or for a cheap second opinion.";

    /** The custom Pi harness prompt: the same live Solforge context, with Pi's MCP tool names. Safe through cmd. */
    public const string PiSystemPrompt =
        "You are running inside Solforge's AI workspace (the Project Sol game editor). The sol MCP server exposes " +
        "the live editor. When the user says 'this', 'that', 'here' or 'what I'm looking at', or asks about the selection, " +
        "the open map, asset or script, call mcp__sol__get_context and mcp__sol__get_open_documents before " +
        "searching files. Use the other mcp__sol__ tools for objects, missions, files and panels. " +
        ".solforge/SESSION.md holds the session context, and the solforge-* skills are playbooks for common jobs " +
        "(new weapon or armor, map bounds, patrol content, enemies, missions): read the matching one first.";

    /** First prompt of an engine started by a handoff (no double quotes or percent signs). */
    public const string HandoffPrompt =
        "Solforge handed this project over from another AI engine. Read .solforge/HANDOFF.md and .solforge/SESSION.md, " +
        "then say in a few lines what you are picking up and wait for the user to go ahead.";

    /**
     * @param Gemini True when the config also holds the gemini (ask_gemini) server.
     * @param ClinePass True when it also holds the clinepass (ask_clinepass) server.
     */
    public sealed record ClaudeFiles(string McpConfig, string Settings, bool Gemini = false, bool ClinePass = false);

    /**
     * Writes the MCP config and the UserPromptSubmit hook settings for the project; @param folder defaults to the
     * discovery folder; @param gemini also adds the shim's --gemini server (ask_gemini runs the user's own agy);
     * @param clinePass also adds its --clinepass server (ask_clinepass runs the user's own cline on ClinePass);
     * @param servers when given, the config holds exactly this Sol MCP set (SolMcpServers.Collect) instead.
     */
    public static ClaudeFiles WriteClaudeFiles(string projectRoot, string shimPath, string? folder = null, bool gemini = false, bool clinePass = false,
        IReadOnlyList<SolMcpServers.Server>? servers = null)
    {
        if (servers is not null)
        {
            gemini = servers.Any(server => server.Name == AgyDelegate.ServerName);
            clinePass = servers.Any(server => server.Name == ClineDelegate.ServerName);
        }
        folder ??= McpDiscovery.Folder;
        Directory.CreateDirectory(folder);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(projectRoot).ToUpperInvariant())))[..12];
        var files = new ClaudeFiles(Path.Combine(folder, $"claude-mcp-{key}.json"), Path.Combine(folder, $"claude-settings-{key}.json"), gemini, clinePass);
        string? upstreams = servers is null ? null : SolMcpServers.WriteUpstreams(projectRoot, servers);
        var sol = SolMcpServers.ClientEntry(projectRoot, shimPath, upstreams);
        Write(files.McpConfig, new JsonObject
        {
            ["mcpServers"] = new JsonObject { [McpClientConfigs.ServerName] = sol }
        });
        // Forward slashes and plain quotes read the same in cmd.exe and in bash, whichever runs the hook.
        string hook = $"\"{shimPath.Replace('\\', '/')}\" --context --project \"{projectRoot.Replace('\\', '/').TrimEnd('/')}\"";
        var command = new JsonObject { ["type"] = "command", ["command"] = hook, ["timeout"] = 5 };
        Write(files.Settings, new JsonObject
        {
            ["hooks"] = new JsonObject { ["UserPromptSubmit"] = new JsonArray(new JsonObject { ["hooks"] = new JsonArray(command) }) }
        });
        return files;
    }

    /** Writes Codex's project-scoped MCP config without replacing user config outside the managed Solforge block. */
    public static string WriteCodexConfig(string projectRoot, IReadOnlyList<SolMcpServers.Server> servers, string? configFolder = null)
    {
        string path = Path.Combine(projectRoot, ".codex", "config.toml");
        string begin = "# BEGIN SOLFORGE MANAGED MCP";
        string end = "# END SOLFORGE MANAGED MCP";
        string upstreams = SolMcpServers.WriteUpstreams(projectRoot, servers, configFolder);
        JsonObject entry = SolMcpServers.ClientEntry(projectRoot, servers[0].Command, upstreams);
        var text = new StringBuilder(begin).AppendLine();
        text.Append("[mcp_servers.solforge_studio]").AppendLine();
        text.Append("command = ").Append(TomlString(entry["command"]!.GetValue<string>())).AppendLine();
        text.Append("args = [").Append(string.Join(", ", ((JsonArray)entry["args"]!).Select(value => TomlString(value!.GetValue<string>())))).AppendLine("]");
        text.AppendLine("enabled = true").Append(end);
        return WriteManagedBlock(path, begin, end, text.ToString(), "[mcp_servers.solforge_studio]");
    }

    /** Adds the Solforge MCP entry to an existing OpenCode project config, preserving every other setting. */
    public static string WriteOpenCodeConfig(string projectRoot, IReadOnlyList<SolMcpServers.Server> servers, string? configFolder = null)
    {
        // Keep the user's opencode.json/jsonc byte-for-byte; OpenCode gets a generated overlay through OPENCODE_CONFIG.
        string path = Path.Combine(projectRoot, ".solforge", "opencode.json");
        string source = new[] { Path.Combine(projectRoot, "opencode.json"), Path.Combine(projectRoot, "opencode.jsonc"), path }
            .FirstOrDefault(File.Exists) ?? "";
        JsonObject config;
        if (source.Length > 0)
            config = JsonNode.Parse(File.ReadAllText(source), documentOptions: new System.Text.Json.JsonDocumentOptions
                { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                ?? throw new System.Text.Json.JsonException(Path.GetFileName(source) + " must contain a JSON object.");
        else config = new JsonObject();

        string upstreams = SolMcpServers.WriteUpstreams(projectRoot, servers, configFolder);
        JsonObject entry = SolMcpServers.ClientEntry(projectRoot, servers[0].Command, upstreams);
        string command = entry["command"]!.GetValue<string>();
        var args = (JsonArray)entry["args"]!;
        var launch = new JsonArray(command);
        foreach (var arg in args) launch.Add(arg!.GetValue<string>());
        if (config["mcp"] is not null and not JsonObject)
            throw new System.Text.Json.JsonException("opencode.json mcp setting must be an object.");
        var mcp = config["mcp"] as JsonObject ?? new JsonObject();
        if (mcp["servers"] is JsonObject nested)
        {
            if (nested.ContainsKey("solforge-studio") && !IsOurOpenCodeEntry(nested["solforge-studio"], command))
                throw new IOException("opencode.json already defines mcp.servers.solforge-studio; keeping the user's entry unchanged.");
            nested["solforge-studio"] = new JsonObject { ["type"] = "local", ["command"] = launch, ["enabled"] = true };
        }
        else if (mcp.Count == 0)
        {
            nested = new JsonObject
            {
                ["solforge-studio"] = new JsonObject { ["type"] = "local", ["command"] = launch, ["enabled"] = true }
            };
            mcp["servers"] = nested;
        }
        else
        {
            if (mcp.ContainsKey("solforge-studio") && !IsOurOpenCodeEntry(mcp["solforge-studio"], command))
                throw new IOException("opencode.json already defines mcp.solforge-studio; keeping the user's entry unchanged.");
            mcp["solforge-studio"] = new JsonObject { ["type"] = "local", ["command"] = launch, ["enabled"] = true };
        }
        config["mcp"] = mcp;
        if (config["instructions"] is not null and not JsonArray and not JsonValue)
            throw new System.Text.Json.JsonException("opencode.json instructions must be a string or an array of strings.");
        var instructions = config["instructions"] is JsonArray existingInstructions
            ? (JsonArray)existingInstructions.DeepClone() : new JsonArray();
        if (config["instructions"] is JsonValue singleInstruction && singleInstruction.TryGetValue<string>(out string? value))
            instructions.Add(value);
        foreach (string instruction in new[] { "AGENTS.md", ".solforge/SESSION.md", ".solforge/HANDOFF.md" })
            if (!instructions.Any(item => item?.GetValueKind() == System.Text.Json.JsonValueKind.String
                && item.GetValue<string>().Equals(instruction, StringComparison.OrdinalIgnoreCase))) instructions.Add(instruction);
        config["instructions"] = instructions;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, config.ToJsonString(McpJson.Indented));
        return path;
    }

    private static bool IsOurOpenCodeEntry(JsonNode? entry, string command) => entry?["command"] is JsonArray args
        && args.Count > 0 && args[0]?.GetValue<string>() == command;

    private static string WriteManagedBlock(string path, string begin, string end, string block, string conflictHeader)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string previous = File.Exists(path) ? File.ReadAllText(path) : "";
        int start = previous.IndexOf(begin, StringComparison.Ordinal);
        int stop = start < 0 ? -1 : previous.IndexOf(end, start + begin.Length, StringComparison.Ordinal);
        string next;
        if (start >= 0 && stop >= 0) next = previous[..start] + block + previous[(stop + end.Length)..];
        else
        {
            if (previous.Contains(conflictHeader, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"{path} already defines {conflictHeader}; keeping the user's entry unchanged.");
            next = previous.Length == 0 ? block + Environment.NewLine : previous.TrimEnd() + Environment.NewLine + Environment.NewLine + block + Environment.NewLine;
        }
        if (next != previous) File.WriteAllText(path, next);
        return path;
    }

    private static string TomlString(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    /** @param files Null when they could not be written: the engine still gets the prompt and the folders. */
    public static string ClaudeArguments(ClaudeFiles? files, IEnumerable<string> addDirs)
    {
        var args = new List<string>();
        if (files is not null) args.AddRange(["--mcp-config", files.McpConfig, "--settings", files.Settings]);
        string prompt = SystemPrompt;
        if (files is { Gemini: true }) prompt += " " + GeminiPrompt;
        if (files is { ClinePass: true }) prompt += " " + ClinePassPrompt;
        args.AddRange(["--append-system-prompt", prompt]);
        foreach (string folder in addDirs) { args.Add("--add-dir"); args.Add(folder); }
        return string.Join(' ', args.Select(Quote));
    }

    /** The Antigravity CLI (agy) gets the same folders; its MCP entry comes from ~/.gemini/config/mcp_config.json. */
    public static string AgyArguments(IEnumerable<string> addDirs) =>
        string.Join(' ', addDirs.SelectMany(folder => new[] { "--add-dir", folder }).Select(Quote));

    /** cmd /s strips only the outer quotes, so the engine path and each argument keep theirs; /k keeps a prompt afterwards. */
    public static string CmdLine(string executable, string arguments, bool keep = true) =>
        $"cmd.exe /d /s /{(keep ? 'k' : 'c')} \"{Quote(executable)}{(arguments.Length > 0 ? " " + arguments : "")}\"";

    /**
     * The engine's no-prompt flag (checked against each CLI's --help): claude and agy --dangerously-skip-permissions,
     * cline --yolo (-y; auto-approves a limited tool set). "" for any other engine.
     */
    public static string SkipPermissionsArguments(string command) => command switch
    {
        "claude" or "agy" => "--dangerously-skip-permissions",
        "cline" => "--yolo",
        _ => ""
    };

    /** One argument as the Windows argv parser reads it back. */
    public static string Quote(string value)
    {
        if (value.Length > 0 && value.IndexOfAny([' ', '\t', '"']) < 0) return value;
        var text = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            text.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }

    /**
     * Folders the engine may read besides the project: the given ones that exist outside it, then each ancestor of the
     * project below the user profile holding a .claude folder or CLAUDE.md (~/.claude loads anyway). Without
     * ancestors only the given folders are kept, so a caller that already scanned them can leave some out.
     */
    public static IReadOnlyList<string> AddDirs(string projectRoot, IEnumerable<string?> folders, string? home = null,
        bool ancestors = true)
    {
        string root = Full(projectRoot), profile = Full(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var result = new List<string>();
        void Add(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;
            string full;
            try { full = Full(folder); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return; }
            if (!Directory.Exists(full) || Inside(full, root) || result.Any(other => Inside(full, other))) return;
            result.RemoveAll(other => Inside(other, full));
            result.Add(full);
        }
        foreach (string? folder in folders) Add(folder);
        if (!ancestors) return result;
        for (var parent = Directory.GetParent(root); parent is not null && !string.Equals(Full(parent.FullName), profile,
                 StringComparison.OrdinalIgnoreCase); parent = parent.Parent)
            if (Directory.Exists(Path.Combine(parent.FullName, ".claude")) || File.Exists(Path.Combine(parent.FullName, "CLAUDE.md")))
                Add(parent.FullName);
        return result;
    }

    /**
     * Arguments that resume an engine's previous session, or "" when there is none (a first run starts fresh):
     * claude --continue (its latest session in the cwd), agy --conversation &lt;id&gt;, cline --id &lt;id&gt;.
     */
    public static string ResumeArguments(string engine, string? session) => session is null or "" ? "" : engine switch
    {
        "claude" => "--continue",
        "agy" => "--conversation " + Quote(session),
        "cline" => "--id " + Quote(session),
        _ => ""
    };

    /** Pi harness arguments: Solforge's context prompt, then the previous session when resume is on. */
    public static string PiArguments(bool resume) =>
        JoinArguments("--append-system-prompt", Quote(PiSystemPrompt), resume ? "--continue" : "");

    /**
     * Adds a first prompt that starts the engine interactively: claude takes it as the first argument (its --add-dir
     * and --mcp-config take several values and would swallow a trailing one), agy as -i; other engines get none.
     */
    public static string WithPrompt(string engine, string arguments, string? prompt) => prompt is null or "" ? arguments : engine switch
    {
        "claude" => JoinArguments(Quote(prompt), arguments),
        "agy" => JoinArguments(arguments, "-i", Quote(prompt)),
        _ => arguments
    };

    /**
     * Engine handoff: writes .solforge/HANDOFF.md from the previous engine's last session for this folder (its recent
     * requests and, for Claude Code, its last reply) so the next engine picks up with the same project, MCP and skills.
     * Only local session files are read; no sign-in is touched. Returns the file's path.
     */
    public static string WriteHandoff(string projectRoot, string fromCommand, string fromName, string toName, string? home = null,
        string? terminalOutput = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? session = null, transcript = null, reply = null;
        IReadOnlyList<string> requests = [];
        try
        {
            if (fromCommand == "claude" && ClaudeSession(projectRoot, home) is { } id)
            {
                session = id;
                transcript = Path.Combine(ClaudeSessionsDir(projectRoot, home), id + ".jsonl");
                (requests, reply) = ClaudeTranscript(TailLines(transcript));
            }
            else if (fromCommand == "agy" && AgySession(projectRoot, home) is { } conversation)
            {
                session = conversation;
                requests = AgyRequests(projectRoot, conversation, TailLines(Path.Combine(home, ".gemini", "antigravity-cli", "history.jsonl")));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        string folder = Path.Combine(projectRoot, ".solforge");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "HANDOFF.md");
        var text = new StringBuilder(HandoffText(fromName, toName, session, transcript, requests, reply, DateTime.Now));
        if (!string.IsNullOrWhiteSpace(terminalOutput))
        {
            string[] lines = terminalOutput.Replace("\r", "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            string tail = string.Join(Environment.NewLine, lines.TakeLast(20));
            if (tail.Length > 5000) tail = tail[^5000..];
            text.AppendLine().AppendLine("## Previous terminal output");
            foreach (string line in tail.Split('\n')) text.Append("- ").AppendLine(line.Replace("`", "'").Trim());
        }
        File.WriteAllText(path, text.ToString());
        return path;
    }

    /**
     * Markdown text carrying a conversation over from one engine to another (requests, last reply and transcript
     * path).
     */
    public static string HandoffText(string fromName, string toName, string? session, string? transcript,
        IReadOnlyList<string> requests, string? reply, DateTime at)
    {
        var text = new StringBuilder("# Engine handoff\n\n");
        text.Append($"Solforge switched this project from {fromName} to {toName} on {at:yyyy-MM-dd HH:mm}. Pick up where {fromName} left off.\n");
        text.Append("Same project, sol MCP tools (get_context, get_open_documents), skills and AGENTS.md rules; ");
        text.Append(".solforge/SESSION.md holds the live session context.\n");
        if (session is not null) text.Append($"{fromName} session: {session}{(transcript is null ? "" : $" (full transcript: {transcript})")}\n");
        text.Append("\n## Recent requests (oldest first)\n");
        if (requests.Count == 0) text.Append("(none recorded)\n");
        foreach (string request in requests) text.Append("- ").Append(request.Replace("\r", "").Replace("\n", "\n  ")).Append('\n');
        if (reply is not null) text.Append($"\n## {fromName}'s last reply\n").Append(reply).Append('\n');
        return text.ToString();
    }

    /** The user's typed requests (oldest first, at most keep) and the last assistant text in Claude Code transcript lines. */
    public static (IReadOnlyList<string> Requests, string? Reply) ClaudeTranscript(IEnumerable<string> lines, int keep = 8)
    {
        var requests = new List<string>();
        string? reply = null;
        foreach (string line in lines)
        {
            if (Parse(line) is not JsonObject entry || IsTrue(entry["isSidechain"]) || IsTrue(entry["isMeta"])) continue;
            var content = entry["message"]?["content"];
            string text = (content is JsonArray parts
                ? string.Join("\n", parts.OfType<JsonObject>().Where(part => Text(part["type"]) == "text").Select(part => Text(part["text"])))
                : Text(content) ?? "").Trim();
            if (text.Length == 0) continue;
            string? type = Text(entry["type"]);
            // Tool results carry no text part; command and hook echoes start with a tag.
            if (type == "user" && !text.StartsWith('<')) { requests.Add(Clip(text, 600)); if (requests.Count > keep) requests.RemoveAt(0); }
            else if (type == "assistant") reply = text;
        }
        return (requests, reply is null ? null : Clip(reply, 4000));
    }

    /** The prompts typed in one agy conversation for this workspace (oldest first, at most keep), slash commands left out. */
    public static IReadOnlyList<string> AgyRequests(string folder, string conversation, IEnumerable<string> historyLines, int keep = 8)
    {
        var requests = new List<string>();
        foreach (string line in historyLines)
        {
            if (Parse(line) is not JsonObject entry || !Same(entry["workspace"], folder) || Text(entry["conversationId"]) != conversation
                || Text(entry["type"]) == "slash_command" || Text(entry["display"]) is not { Length: > 0 } display) continue;
            requests.Add(Clip(display.Trim(), 600));
            if (requests.Count > keep) requests.RemoveAt(0);
        }
        return requests;
    }

    /** The last lines of a file still being written (at most the final 4 MB; a cut first line is dropped). */
    private static string[] TailLines(string file, long bytes = 4 << 20)
    {
        if (!File.Exists(file)) return [];
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        bool cut = stream.Length > bytes;
        if (cut) stream.Seek(-bytes, SeekOrigin.End);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string[] lines = reader.ReadToEnd().Split('\n');
        return cut ? lines[1..] : lines;
    }

    /** Truncates text to max characters with an ellipsis. */
    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + " ...";

    /** Whether a JSON node is the boolean true. */
    private static bool IsTrue(JsonNode? node) => node?.GetValueKind() == System.Text.Json.JsonValueKind.True;

    /** Joins argument strings, skipping empty ones. */
    public static string JoinArguments(params string[] parts) => string.Join(' ', parts.Where(part => part.Length > 0));

    /** Claude Code keeps a folder's sessions in ~/.claude/projects/&lt;full path, non-alphanumerics as '-'&gt;/*.jsonl. */
    public static string ClaudeSessionsDir(string folder, string? home = null) => Path.Combine(
        home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects",
        new string(Full(folder).Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()));

    /** The newest Claude Code session id for the folder, or null. */
    public static string? ClaudeSession(string folder, string? home = null)
    {
        try
        {
            var dir = new DirectoryInfo(ClaudeSessionsDir(folder, home));
            return dir.Exists ? dir.EnumerateFiles("*.jsonl").MaxBy(file => file.LastWriteTimeUtc) is { } newest
                ? Path.GetFileNameWithoutExtension(newest.Name) : null : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    /** The agy CLI logs each prompt to ~/.gemini/antigravity-cli/history.jsonl with its workspace and conversationId. */
    public static string? AgySession(string folder, string? home = null)
    {
        string root = Path.Combine(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli");
        try
        {
            string history = Path.Combine(root, "history.jsonl");
            if (!File.Exists(history)) return null;
            string? id = AgySession(folder, File.ReadLines(history));
            return id is not null && File.Exists(Path.Combine(root, "conversations", id + ".db")) ? id : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    /** The last conversationId logged for this workspace in agy history lines (oldest first), or null. */
    public static string? AgySession(string folder, IEnumerable<string> historyLines)
    {
        string? found = null;
        foreach (string line in historyLines)
        {
            if (Parse(line) is not JsonObject entry) continue;
            if (Same(entry["workspace"], folder) && Text(entry["conversationId"]) is { Length: > 0 } id) found = id;
        }
        return found;
    }

    /** The newest top-level session for this folder in `cline history --json` output, or null. */
    public static string? ClineSession(string folder, string historyJson)
    {
        int start = historyJson.IndexOf('[');
        if (start < 0 || Parse(historyJson[start..]) is not JsonArray sessions) return null;
        return sessions.OfType<JsonObject>()
            .Where(session => (Same(session["cwd"], folder) || Same(session["workspaceRoot"], folder))
                && !(session["isSubagent"] is JsonValue sub && sub.TryGetValue(out bool isSub) && isSub))
            .Select(session => (Id: Text(session["sessionId"]), Started: Text(session["startedAt"]) ?? ""))
            .Where(session => session.Id is { Length: > 0 })
            .OrderByDescending(session => DateTimeOffset.TryParse(session.Started, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : DateTimeOffset.MinValue)
            .Select(session => session.Id).FirstOrDefault();
    }

    /** Parses JSON, returning null when invalid. */
    private static JsonNode? Parse(string json)
    {
        try { return JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /** A JSON string value, or null. */
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    /** Whether a JSON path value names the same folder. */
    private static bool Same(JsonNode? node, string folder)
    {
        if (Text(node) is not { Length: > 0 } path) return false;
        try { return string.Equals(Full(path), Full(folder), StringComparison.OrdinalIgnoreCase); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /** The full path without a trailing separator, except on a drive root. */
    private static string Full(string path)
    {
        string full = Path.GetFullPath(path);
        return full.Length > 3 ? full.TrimEnd('\\', '/') : full;
    }

    /** True when the path is the folder or below it. */
    private static bool Inside(string path, string folder) =>
        string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /** Writes the file only if its content changed, to avoid needless rewrites. */
    private static void Write(string path, JsonObject root)
    {
        string text = root.ToJsonString(McpJson.Indented);
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        File.WriteAllText(path, text);
    }
}
