using System.Text.RegularExpressions;

namespace Solforge.Mcp;

/** Common CLI metadata and quota detection shared by the Solforge AI workspace. */
public static partial class AiConsoleAdapters
{
    public sealed record Adapter(string Name, string Command, string Install, string Note);

    public static IReadOnlyList<Adapter> Supported { get; } =
    [
        new("Claude Code", "claude", "npm install -g @anthropic-ai/claude-code", "Uses your existing Claude Code login."),
        new("Cline", "cline", "npm install -g cline", "Uses your existing Cline CLI login."),
        new("Codex", "codex", "npm install -g @openai/codex", "Uses your existing Codex CLI login. Trust this project in Codex to load its project MCP config."),
        new("OpenCode", "opencode", "npm install -g opencode-ai", "Uses your existing OpenCode login.")
    ];

    [GeneratedRegex(@"\b(?:rate[- ]limit(?:ed|ing)?|too many requests|quota (?:is )?(?:exceeded|exhausted)|usage limit (?:reached|exceeded)|out of (?:credits|usage)|credits exhausted|429\s*(?:too many requests|rate limit)?|(?:you(?:'ve| have)|we) (?:hit|reached|exceeded) (?:your )?(?:usage )?(?:limit|quota))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuotaPattern();

    /** Returns the matched limit message, or null when recent terminal output is not a quota failure. */
    public static string? QuotaFailure(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var match = QuotaPattern().Match(output);
        if (!match.Success) return null;
        int start = output.LastIndexOfAny(['\r', '\n'], match.Index);
        int end = output.IndexOfAny(['\r', '\n'], match.Index + match.Length);
        string line = output[(start < 0 ? 0 : start + 1)..(end < 0 ? output.Length : end)].Trim();
        return line.Length > 300 ? line[..300] : line;
    }
}
