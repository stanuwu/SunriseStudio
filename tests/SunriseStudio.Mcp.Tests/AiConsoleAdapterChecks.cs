using System.Text.Json.Nodes;
using Solforge.Mcp;

internal static class AiConsoleAdapterChecks
{
    public static void Run(string scratch)
    {
        string[] expected = ["claude", "cline", "codex", "opencode"];
        if (!AiConsoleAdapters.Supported.Select(item => item.Command).SequenceEqual(expected))
            throw new InvalidOperationException("The four requested console adapters must remain available in order.");
        if (AiConsoleAdapters.QuotaFailure("request failed: rate limit reached") is null
            || AiConsoleAdapters.QuotaFailure("HTTP 429 Too Many Requests") is null
            || AiConsoleAdapters.QuotaFailure("The user asked how rate limits work") is not null)
            throw new InvalidOperationException("Quota detection should catch provider limits without matching ordinary discussion.");

        string project = Path.Combine(scratch, "AI project");
        Directory.CreateDirectory(project);
        var servers = new[]
        {
            new SolMcpServers.Server("solforge", "C:\\Sol Forge\\solforge-mcp.exe", ["--project", project], new JsonObject(), true, "Solforge")
        };
        string configDir = Path.Combine(scratch, "generated");
        string codex = EngineLaunch.WriteCodexConfig(project, servers, configDir);
        File.AppendAllText(codex, "\n[profiles.custom]\nmodel = \"keep-me\"\n");
        string second = EngineLaunch.WriteCodexConfig(project, servers, configDir);
        string codexText = File.ReadAllText(second);
        if (!codexText.Contains("# BEGIN SOLFORGE MANAGED MCP", StringComparison.Ordinal)
            || !codexText.Contains("keep-me", StringComparison.Ordinal) || Count(codexText, "[mcp_servers.solforge_studio]") != 1)
            throw new InvalidOperationException("Codex config writes an idempotent project-scoped MCP block and preserves user settings.");

        string projectConfig = Path.Combine(project, "opencode.json");
        const string projectConfigText = "{\n  // keep this user comment intact\n  \"model\": \"user/model\"\n}\n";
        File.WriteAllText(projectConfig, projectConfigText);
        string openCode = EngineLaunch.WriteOpenCodeConfig(project, servers, configDir);
        EngineLaunch.WriteOpenCodeConfig(project, servers, configDir);
        JsonObject result = (JsonObject)JsonNode.Parse(File.ReadAllText(openCode))!;
        if (result["model"]?.GetValue<string>() != "user/model"
            || result["mcp"]?["servers"]?["solforge-studio"]?["command"] is not JsonArray
            || result["instructions"] is not JsonArray || File.ReadAllText(projectConfig) != projectConfigText)
            throw new InvalidOperationException("OpenCode gets a merged config overlay and shared instructions while the user's JSONC stays unchanged.");

        EngineLaunch.WriteHandoff(project, "codex", "Codex", "Claude Code", terminalOutput: "HTTP 429 Too Many Requests\nTry again later");
        string handoff = File.ReadAllText(Path.Combine(project, ".solforge", "HANDOFF.md"));
        if (!handoff.Contains("Previous terminal output", StringComparison.Ordinal)
            || !handoff.Contains("HTTP 429 Too Many Requests", StringComparison.Ordinal))
            throw new InvalidOperationException("Quota fallback carries the prior terminal output in the shared project handoff file.");
    }

    private static int Count(string text, string value) =>
        (text.Length - text.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;
}
