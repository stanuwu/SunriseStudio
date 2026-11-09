/**
 * File: Registers the Solforge MCP with a Solforge AI the user starts directly, without the editor (UI-UE5 §4.4.7).
 * solforge-next's standalone overlay (ai/runtime/apps/desktop/src/solforge-standalone.ts, writeAppOverlay) does not read
 * solforge-ai-mcp.json; its only route for extra MCP servers is importClaudeServers: top-level stdio mcpServers entries of
 * ~/.claude.json named in <standalone profile>\electron\solforge-standalone.json. It drops rows named solforge or
 * solforge_next, so the entry is called "solforge-standalone" (row id solforge-solforge-standalone-mcp there).
 *
 * Register (a user action, Settings > Solforge AI > STANDALONE MCP) writes that ~/.claude.json entry (command = the run copy
 * of solforge-mcp.exe, args --sol --upstreams <per-user default>, no env) and appends the name to importClaudeServers,
 * keeping every other key; an invalid settings file is left alone. Refresh re-points an existing entry at the current run
 * copy (old copies are pruned); it never creates one. Neither file ever receives a credential.
 */
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Solforge.Mcp;

/** The ~/.claude.json + importClaudeServers registration of the standalone Solforge MCP. */
public static partial class StandaloneRegistration
{
    /** The top-level ~/.claude.json mcpServers name (solforge-standalone.ts refuses "solforge" and "solforge_next"). */
    public const string ServerName = "solforge-standalone";

    /** What a standalone Solforge AI will see. */
    public sealed record State(bool ClaudeEntry, bool ClaudeEntryCurrent, bool Imported, string? SettingsProblem, string Summary)
    {
        public bool Registered => ClaudeEntry && Imported;
    }

    private static readonly JsonSerializerOptions Write = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly object gate = new();

    /** ~/.claude.json. */
    public static string ClaudeConfigFile(string? home = null) =>
        Path.Combine(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    /** %LOCALAPPDATA%\SolforgeNext\AiApp\_standalone\electron\solforge-standalone.json (the standalone profile's settings). */
    public static string StandaloneSettingsFile(string? localAppData = null) =>
        Path.Combine(localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SolforgeNext", "AiApp", "_standalone", "electron", "solforge-standalone.json");

    /** The defaults solforge-standalone.ts writes when the file is missing (defaultSettings()). */
    private static JsonObject DefaultSettings() => new()
    {
        ["sharedResourcesPath"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop", "ai", "ai-mcps-skills-consilidated"),
        ["workbenchRoot"] = "",
        ["importClaudeServers"] = new JsonArray("graphify", "rea")
    };

    /** solforge-standalone.ts CREDENTIAL_ARGUMENT: an entry whose command or argument matches is not imported there. */
    [GeneratedRegex(@"(?:^|[^a-z])(?:api[-_]?keys?|keys?|tokens?|secrets?|passw(?:or)?ds?|pass|pwd|auth(?:orization)?|bearer|cookies?|credentials?|pat)(?:$|[^a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialArgument();

    /** The entry as Claude Code and solforge-standalone.ts read it. */
    public static JsonObject Entry(string shimPath, string upstreamsFile) => new()
    {
        ["type"] = "stdio",
        ["command"] = Path.GetFullPath(shimPath),
        ["args"] = new JsonArray("--sol", "--upstreams", Path.GetFullPath(upstreamsFile)),
        ["env"] = new JsonObject()
    };

    /** Why solforge-standalone.ts would refuse the entry, or null. */
    public static string? Refusal(string shimPath, string upstreamsFile)
    {
        foreach (string part in new[] { Path.GetFullPath(shimPath), "--sol", "--upstreams", Path.GetFullPath(upstreamsFile) })
            if (CredentialArgument().IsMatch(part))
                return "Solforge AI would refuse the entry: a path looks credential-like to its import filter (" + Path.GetFileName(part) + " or a folder above it).";
        return null;
    }

    /** Current state, from file inspection only. */
    public static State Inspect(string? shimPath = null, string? upstreamsFile = null, string? claudeFile = null, string? settingsFile = null)
    {
        claudeFile ??= ClaudeConfigFile();
        settingsFile ??= StandaloneSettingsFile();
        bool entry = false, current = false, imported = false;
        string? problem = null;
        try
        {
            if (ReadObject(claudeFile)?["mcpServers"] is JsonObject servers && servers[ServerName] is JsonObject existing)
            {
                entry = true;
                current = shimPath is not null && upstreamsFile is not null && JsonNode.DeepEquals(existing, Entry(shimPath, upstreamsFile));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { problem = "~/.claude.json is unreadable."; }
        // A missing settings file means Solforge AI was never started standalone: it will use the defaults (no import).
        if (File.Exists(settingsFile))
            try
            {
                if (ReadObject(settingsFile) is not { } settings) problem = "solforge-standalone.json is not a JSON object.";
                else if (settings["importClaudeServers"] is JsonArray names) imported = names.Any(name => name?.GetValueKind() == JsonValueKind.String && name.GetValue<string>() == ServerName);
                else if (settings["importClaudeServers"] is not null) problem = "solforge-standalone.json: importClaudeServers is not a list.";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { problem = "solforge-standalone.json is invalid."; }
        string summary = entry && imported
            ? "Registered: a Solforge AI started without the editor loads the Solforge MCP as '" + ServerName + "'" + (current || shimPath is null ? "." : " (entry points at an older shim; Register again).")
            : "Not registered: a Solforge AI started without the editor has no Solforge MCP."
                + (entry ? " The ~/.claude.json entry exists but solforge-standalone.json does not import it." : imported ? " Imported by name, but ~/.claude.json has no entry." : "");
        if (problem is not null) summary += " " + problem;
        return new State(entry, current, imported, problem, summary);
    }

    /**
     * The user's Register action: writes the ~/.claude.json entry and adds the name to importClaudeServers (the settings
     * file is created with solforge-standalone.ts's defaults when missing; an invalid one is not touched).
     */
    public static State Register(string shimPath, string upstreamsFile, string? claudeFile = null, string? settingsFile = null)
    {
        claudeFile ??= ClaudeConfigFile();
        settingsFile ??= StandaloneSettingsFile();
        if (Refusal(shimPath, upstreamsFile) is { } refusal) throw new InvalidOperationException(refusal);
        lock (gate)
        {
            // Check the settings file first so a damaged one leaves ~/.claude.json unchanged too.
            JsonObject settings;
            if (File.Exists(settingsFile))
            {
                settings = ReadObject(settingsFile) ?? throw new InvalidOperationException("solforge-standalone.json is not a JSON object; fix or delete it, then Register again.");
                if (settings["importClaudeServers"] is not null and not JsonArray)
                    throw new InvalidOperationException("solforge-standalone.json: importClaudeServers is not a list; fix it, then Register again.");
            }
            else settings = DefaultSettings();
            UpdateObject(claudeFile, root =>
            {
                if (root["mcpServers"] is not JsonObject servers) root["mcpServers"] = servers = new JsonObject();
                servers[ServerName] = Entry(shimPath, upstreamsFile);
            }, backup: true);
            if (settings["importClaudeServers"] is not JsonArray names) settings["importClaudeServers"] = names = new JsonArray("graphify", "rea");
            if (!names.Any(name => name?.GetValueKind() == JsonValueKind.String && name.GetValue<string>() == ServerName)) names.Add(ServerName);
            WriteAtomic(settingsFile, settings.ToJsonString(Write) + "\n");
        }
        return Inspect(shimPath, upstreamsFile, claudeFile, settingsFile);
    }

    /** The user's Unregister action: removes the entry and the name; every other key and name stays. */
    public static State Unregister(string? claudeFile = null, string? settingsFile = null)
    {
        claudeFile ??= ClaudeConfigFile();
        settingsFile ??= StandaloneSettingsFile();
        lock (gate)
        {
            if (File.Exists(claudeFile) && ReadObject(claudeFile)?["mcpServers"] is JsonObject { } servers && servers.ContainsKey(ServerName))
                UpdateObject(claudeFile, root => (root["mcpServers"] as JsonObject)?.Remove(ServerName), backup: true);
            if (File.Exists(settingsFile) && ReadObject(settingsFile) is { } settings && settings["importClaudeServers"] is JsonArray names)
            {
                var keep = names.Where(name => !(name?.GetValueKind() == JsonValueKind.String && name.GetValue<string>() == ServerName)).Select(name => name?.DeepClone()).ToArray();
                if (keep.Length != names.Count) { settings["importClaudeServers"] = new JsonArray(keep); WriteAtomic(settingsFile, settings.ToJsonString(Write) + "\n"); }
            }
        }
        return Inspect(null, null, claudeFile, settingsFile);
    }

    /** Re-points an existing entry at the current shim run copy; does nothing when the user never registered. */
    public static void Refresh(string shimPath, string upstreamsFile, string? claudeFile = null)
    {
        claudeFile ??= ClaudeConfigFile();
        if (Refusal(shimPath, upstreamsFile) is not null) return;
        lock (gate)
        {
            try
            {
                if (ReadObject(claudeFile)?["mcpServers"] is not JsonObject servers || servers[ServerName] is not JsonObject existing) return;
                var wanted = Entry(shimPath, upstreamsFile);
                if (JsonNode.DeepEquals(existing, wanted)) return;
                UpdateObject(claudeFile, root => { if (root["mcpServers"] is JsonObject list) list[ServerName] = wanted; }, backup: false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        }
    }

    /**
     * Re-points every ~/.claude.json server (user level and per project, e.g. `sol`) that runs an older run copy of
     * solforge-mcp in the run-copy folder at @param shimPath. Run copies are renamed on every publish and the older ones
     * pruned, so an entry left on an old name stops starting; this runs on every editor start. Only the command changes.
     * @return how many entries were changed.
     */
    public static int RefreshRunCopies(string shimPath, string? claudeFile = null)
    {
        claudeFile ??= ClaudeConfigFile();
        string shim = Path.GetFullPath(shimPath);
        string? folder = Path.GetDirectoryName(shim);
        if (folder is null || !Path.GetFileName(shim).StartsWith("solforge-mcp-", StringComparison.OrdinalIgnoreCase)) return 0;
        bool Stale(JsonNode? entry)
        {
            if (entry?["command"] is not JsonValue value || !value.TryGetValue(out string? command) || string.IsNullOrWhiteSpace(command)) return false;
            try
            {
                string full = Path.GetFullPath(command.Trim('"'));
                return Path.GetFileName(full).StartsWith("solforge-mcp-", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetDirectoryName(full), folder, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(full, shim, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        }
        IEnumerable<JsonObject> Lists(JsonObject root)
        {
            if (root["mcpServers"] is JsonObject top) yield return top;
            if (root["projects"] is JsonObject projects)
                foreach (var (_, project) in projects)
                    if (project?["mcpServers"] is JsonObject servers) yield return servers;
        }
        lock (gate)
        {
            try
            {
                if (ReadObject(claudeFile) is not { } current
                    || !Lists(current).Any(list => list.Any(pair => Stale(pair.Value)))) return 0;
                int changed = 0;
                UpdateObject(claudeFile, root =>
                {
                    foreach (var list in Lists(root))
                        foreach (var (_, entry) in list.ToList())
                            if (Stale(entry)) { entry!["command"] = shim; changed++; }
                }, backup: false);
                return changed;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return 0; }
        }
    }

    private static JsonObject? ReadObject(string path) =>
        File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }) as JsonObject : null;

    /** Loads the whole object, applies the change and writes it atomically; an unparsable file is never overwritten. */
    private static void UpdateObject(string path, Action<JsonObject> change, bool backup)
    {
        JsonObject root;
        if (File.Exists(path))
        {
            string existing = File.ReadAllText(path);
            root = string.IsNullOrWhiteSpace(existing) ? new JsonObject()
                : JsonNode.Parse(existing) as JsonObject ?? throw new InvalidOperationException(Path.GetFileName(path) + " is not a JSON object; it was not changed.");
            if (backup && !File.Exists(path + ".solforge.bak")) File.Copy(path, path + ".solforge.bak");
        }
        else root = new JsonObject();
        change(root);
        WriteAtomic(path, root.ToJsonString(Write));
    }

    private static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, true);
    }
}
