/**
 * File: Built-in provider catalogs offered on Settings > Models & workflows (mirror of Solforge AI's
 * apps/desktop/src/solforge-provider-catalogs.ts; keep both lists equal). Names, ids and endpoints only.
 * - ClinePass: the user's model list from the Cline app. Full ids with their prefix, endpoint https://api.cline.bot/api/v1,
 *   key variable CLINEPASS_API_KEY. ClinePass needs stream: true (OpenAiCompatibleWorker streams for api.cline.bot).
 *   Cline's GET /models catalog is Cline's USAGE-BILLED list, not ClinePass, and is never mixed in.
 * - OpenCode Zen: pi-ai 1.3.0's opencode provider, its openai-completions models, endpoint https://opencode.ai/inference/v1,
 *   key variable OPENCODE_API_KEY. Costs are USD per million input / output tokens.
 */
namespace Solforge.Harness;

/** ClinePass and OpenCode Zen model catalogs. */
public static class ProviderCatalogs
{
    public const string ClinePassBaseUrl = "https://api.cline.bot/api/v1";
    public const string ClinePassKey = "CLINEPASS_API_KEY";
    public const string OpenCodeBaseUrl = "https://opencode.ai/inference/v1";
    public const string OpenCodeKey = "OPENCODE_API_KEY";

    /** The ClinePass list (Beam is free too, but its id is unknown, so it is not listed). */
    public static readonly IReadOnlyList<string> ClinePassModels =
    [
        "cline-pass/deepseek-v4-pro", "cline-pass/deepseek-v4.1-flash", "cline-pass/deepseek-v4.1-flash-1017", "cline-pass/kimi-k3",
        "cline-pass/kimi-k3.1-pro", "cline-pass/glm-5.3", "cline-pass/glm-5.3-flash", "cline-pass/mimo-v2.6-flash", "cline-pass/mimo-v2.6-pro",
        "cline-pass/minimax-m3", "cline-pass/minimax-m3.1-flash", "cline-pass/muse-spark-1.3-contributor", "cline-pass/qwen3.7-max",
        "cline-pass/qwen3.7-plus", "cline-pass/qwen3.8-max", "cline-free/nemotron-3.5-super-vl", "cline-free/solar-mini4",
        "nvidia/nemotron-3.5-lightning-free", "poolside/laguna-s-2.1-free",
    ];

    /** OpenCode Zen openai-completions models: id, display name, USD per million input / output tokens. */
    public static readonly IReadOnlyList<(string Id, string Name, double Input, double Output)> OpenCodeModels =
    [
        ("big-pickle", "Big Pickle", 0, 0), ("cat-jam-free", "Cat Jam (free)", 0, 0),
        ("deepseek-v4.1-flash-free", "DeepSeek V4.1 Flash (free)", 0, 0), ("longcat-2.5-preview-free", "LongCat 2.5 Preview (free)", 0, 0),
        ("mimo-v2.6-flash-free", "MiMo V2.6 Flash (free)", 0, 0), ("nemotron-3-ultra-free", "Nemotron 3 Ultra (free)", 0, 0),
        ("nemotron-3.5-lightning-free", "Nemotron 3.5 Lightning (free)", 0, 0), ("nemotron-3.5-super-vl-free", "Nemotron 3.5 Super VL (free)", 0, 0),
        ("space-bunny-free", "Space Bunny (free)", 0, 0), ("step-5-preview-free", "Step 5 Preview (free)", 0, 0),
        ("deepseek-v4-flash", "DeepSeek V4 Flash", 0.14, 0.28), ("deepseek-v4-flash-vision-exp", "DeepSeek V4 Flash Vision (exp)", 0.14, 0.28),
        ("deepseek-v4-pro", "DeepSeek V4 Pro", 1.74, 3.84), ("deepseek-v4.1-flash", "DeepSeek V4.1 Flash", 0.3, 1.2),
        ("glm-5", "GLM 5", 1, 3.2), ("glm-5.1", "GLM 5.1", 1.4, 4.4), ("glm-5.2", "GLM 5.2", 1.4, 4.4), ("glm-5.3", "GLM 5.3", 1.4, 4.4),
        ("glm-5.3-flash", "GLM 5.3 Flash", 0.15, 0.5), ("kimi-k2.5", "Kimi K2.5", 0.6, 3), ("kimi-k2.6", "Kimi K2.6", 0.95, 4),
        ("kimi-k2.7-code", "Kimi K2.7 Code", 0.95, 4), ("kimi-k3", "Kimi K3", 3, 15), ("minimax-m2.5", "MiniMax M2.5", 0.3, 1.2),
        ("minimax-m2.7", "MiniMax M2.7", 0.3, 1.2), ("minimax-m3", "MiniMax M3", 0.3, 1.2), ("mistral-large-4", "Mistral Large 4", 0.68, 2.09),
        ("qwen3.8-max", "Qwen3.8 Max", 2, 6),
    ];

    /** Whether an endpoint is Cline's API (ClinePass and Cline's usage-billed catalog share it). */
    public static bool IsClineEndpoint(string? baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && string.Equals(uri.Host, "api.cline.bot", StringComparison.OrdinalIgnoreCase);

    /** Whether a model id on Cline's endpoint is a ClinePass model. */
    public static bool IsClinePassId(string modelId) => ClinePassModels.Contains(modelId, StringComparer.Ordinal);

    /** An OpenAI-compatible model on Cline's endpoint whose id is not ClinePass: Cline usage-billed (not ClinePass). */
    public static bool IsClineUsageBilled(HarnessModel model) =>
        model.Transport == HarnessTransport.OpenAiCompatible && IsClineEndpoint(model.BaseUrl) && !IsClinePassId(model.ModelId);
}
