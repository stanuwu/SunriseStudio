namespace Solforge.Mcp;

/** Native Claude subscription and user-owned Anthropic endpoints. */
public static class EngineProviders
{
    /** @param Login The engine's own sign-in command, run in the terminal; empty when none is needed. */
    public sealed record Preset(string Id, string Engine, string Name, string Note, string Login = "",
        string DefaultModel = "", bool UsesModel = false, bool UsesBaseUrl = false);

    /** The non-secret choice saved in Studio settings for one engine. */
    public sealed class Choice
    {
        public string Provider { get; set; } = "";
        public string Model { get; set; } = "";
        public string BaseUrl { get; set; } = "";
    }

    public const string ClaudeSubscription = "claude-subscription", ClaudeApiKey = "claude-api-key", ClaudeGateway = "claude-gateway";

    /** Credentials that would make claude bill an API key instead of the subscription login. */
    private static readonly string[] AnthropicCredentials = ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL"];

    public static readonly IReadOnlyList<Preset> Presets =
    [
        new(ClaudeSubscription, "claude", "Claude subscription (claude auth login)",
            "Uses your Pro/Max login. Solforge hides ANTHROPIC_API_KEY/AUTH_TOKEN/BASE_URL from this run so the subscription is used.",
            "claude auth login"),
        new(ClaudeApiKey, "claude", "Anthropic API key (your environment)",
            "Uses ANTHROPIC_API_KEY from your own environment, billed per token to your Console account. Solforge stores no key."),
        new(ClaudeGateway, "claude", "Anthropic-format LLM gateway",
            "Sets ANTHROPIC_BASE_URL; put the gateway credential in ANTHROPIC_AUTH_TOKEN in your own environment. Claude models only.",
            UsesBaseUrl: true),

    ];

    /** Presets available to one engine. */
    public static IEnumerable<Preset> For(string engine) => Presets.Where(preset => preset.Engine == engine);

    /** The saved preset for the engine, else its first; null for engines without presets (agy, custom). */
    public static Preset? Resolve(string engine, string? id) =>
        For(engine).FirstOrDefault(preset => preset.Id == id) ?? For(engine).FirstOrDefault();

    /** Launch flags for the preset, joined like EngineLaunch arguments. */
    public static string Arguments(Preset? preset, Choice? choice) => "";

    /** Environment overrides for the run; an empty value removes the variable from the engine's environment. */
    public static IReadOnlyDictionary<string, string> Environment(Preset? preset, Choice? choice)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        switch (preset?.Id)
        {
            // Cline's claude-code provider starts claude with Cline's environment, so the same rule keeps it on the subscription.
            case ClaudeSubscription:
                foreach (string name in AnthropicCredentials) env[name] = "";
                break;
            case ClaudeGateway when (choice?.BaseUrl.Trim() ?? "") is { Length: > 0 } url:
                env["ANTHROPIC_BASE_URL"] = url;
                break;
        }
        return env;
    }

    /** Model name to pass for a preset (empty when the preset does not use one). */
    public static string Model(Preset preset, Choice? choice) =>
        !preset.UsesModel ? "" : choice?.Model.Trim() is { Length: > 0 } model ? model : preset.DefaultModel;
}
