/**
 * File: Models the user configured outside harness-settings.json, offered on Settings > Models & workflows for assignment
 * to the role ladders (the same discovery as Solforge AI's apps/desktop/src/solforge-model-sources.ts):
 * - the editor's own per-engine provider choices (StudioSettings.AiProviders, passed in by the editor);
 * - the llm-pi-ai providers saved in Solforge AI profiles (%LOCALAPPDATA%\SolforgeNext\AiApp\*\home\profiles\*\cordis.patch.yml);
 * - the official subscription CLIs on PATH (claude, codex, gemini, agy);
 * - the built-in ClinePass and OpenCode Zen catalogs (ProviderCatalogs). Cline's usage-billed /models catalog (imported into an
 *   old profile as "Clinepass") is listed apart as "Cline (usage-billed, not ClinePass)" and never mixed into ClinePass.
 * Only model ids, provider types, endpoints and credential variable NAMES are read. No source file is changed and no key
 * or token value is read (no profile .credentials.yaml either). Choices with no tier route (API-key / gateway, Cline's
 * saved provider, unknown presets) are still listed, marked Unavailable with the reason, so the user sees every entry.
 */
using System.Text.RegularExpressions;

namespace Solforge.Harness;

/** One discovered model; Key is stable across reloads (transport, provider, model, endpoint). */
public sealed record ConfiguredModelCandidate(
    string Key, string SourceLabel, string DisplayName, HarnessTransport Transport, string Provider, string ModelId,
    string? BaseUrl, string? ApiKeyVariable, string Note, bool Unavailable = false)
{
    /** A new HarnessModel for this candidate with a fresh id that is not in `taken`. */
    public HarnessModel ToModel(IReadOnlyCollection<string> taken, HarnessModelTier tier)
    {
        string name = ModelId == "default" ? Provider + "-subscription" : ModelId;
        string slug = Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length == 0) slug = "model";
        if (slug.Length > 40) slug = slug[..40];
        string id = slug;
        for (int n = 2; taken.Contains(id); n++) id = slug + "-" + n;
        return new HarnessModel
        {
            Id = id, DisplayName = DisplayName, Transport = Transport, Provider = Provider, ModelId = ModelId,
            BaseUrl = BaseUrl, ApiKeyVariable = ApiKeyVariable, Enabled = true, Tier = tier
        };
    }

    /** Whether an existing harness model is the same model (same transport and id; same endpoint for API models). */
    public bool Matches(HarnessModel model) => !Unavailable && model.Transport == Transport && model.ModelId == ModelId
        && (Transport != HarnessTransport.OpenAiCompatible
            || string.Equals((model.BaseUrl ?? "").TrimEnd('/'), (BaseUrl ?? "").TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
}

/** The editor's non-secret per-engine provider choice (EngineProviders.Choice), passed in by the editor. */
public sealed record EngineProviderChoice(string Engine, string Provider, string Model, string BaseUrl);

/** Discovery of configured models and subscription CLIs (file inspection only; nothing is started). */
public static class ConfiguredModelDiscovery
{
    private const long MaximumSourceBytes = 4 * 1024 * 1024;
    private const int MaximumCandidates = 2000;
    private static readonly Regex VariableName = new(@"\A[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);

    /** Every candidate, de-duplicated, in source order: editor choices, Solforge AI profiles, CLI defaults. */
    public static IReadOnlyList<ConfiguredModelCandidate> Discover(IEnumerable<EngineProviderChoice> editorChoices)
    {
        var output = new List<ConfiguredModelCandidate>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        void Add(string label, string display, HarnessTransport transport, string provider, string model, string? baseUrl, string? key, string note,
            bool unavailable = false)
        {
            if (output.Count >= MaximumCandidates) return;
            string id = (unavailable ? "unavailable|" + label + "|" : "") + transport + "|" + provider + "|" + model + "|" + (baseUrl ?? "");
            if (keys.Add(id)) output.Add(new(id, label, display, transport, provider, model, baseUrl, key, note, unavailable));
        }

        foreach (var choice in editorChoices)
        {
            string label = "Sunrise Studio settings (" + choice.Engine + ")";
            string model = choice.Model.Trim();
            switch (choice.Provider)
            {
                case "claude-subscription" or "cline-claude-code":
                    Add(label, "Claude subscription" + (model.Length == 0 ? "" : " (" + model + ")"), HarnessTransport.ClaudeSubscription, "anthropic",
                        model.Length == 0 ? "default" : model, null, null, "Runs through your signed-in claude CLI (claude -p), hidden and read-only.");
                    break;
                case "cline-pass" or "claude-cline-pass" when model.Length > 0:
                    // ClinePass ids are FULL ids with their prefix (cline-pass/..., cline-free/..., vendor/...-free).
                    string id = model.Contains('/') ? model : "cline-pass/" + model;
                    Add(label, "ClinePass " + id, HarnessTransport.OpenAiCompatible, "clinepass", id, ProviderCatalogs.ClinePassBaseUrl, ProviderCatalogs.ClinePassKey,
                        "ClinePass (streamed OpenAI-compatible endpoint); needs " + ProviderCatalogs.ClinePassKey + " in your credential file.");
                    break;
                case "claude-api-key" or "claude-gateway":
                    // Listed so the user sees it; an API key / gateway is a paid route, which Solforge tiers never fall back to.
                    Add(label, (choice.Provider == "claude-api-key" ? "Anthropic API key" : "Anthropic-format gateway") + (model.Length == 0 ? "" : " (" + model + ")"),
                        HarnessTransport.ClaudeSubscription, choice.Provider, model.Length == 0 ? "default" : model, null, null,
                        "API-key / gateway route: not offered in tiers (no paid fallback). Use a Claude subscription entry instead.", unavailable: true);
                    break;
                case "cline-saved":
                    Add(label, "Cline saved provider", HarnessTransport.OpenAiCompatible, "cline-saved", model.Length == 0 ? "unknown" : model, null, null,
                        "Cline saved provider: unknown model (whatever cline auth configured); not offered in tiers.", unavailable: true);
                    break;
                case "" when model.Length == 0:
                    break;
                default:
                    Add(label, (choice.Provider.Length == 0 ? "Engine default" : choice.Provider) + (model.Length == 0 ? "" : " (" + model + ")"),
                        HarnessTransport.OpenAiCompatible, choice.Provider.Length == 0 ? "unknown" : choice.Provider, model.Length == 0 ? "unknown" : model, null, null,
                        "No tier route is known for this provider choice; not offered in tiers.", unavailable: true);
                    break;
            }
        }

        // Cline's usage-billed catalog (hundreds of ids) is listed last, after ClinePass and OpenCode.
        var usageBilled = new List<(string Display, string Provider, string Model, string Url, string Env)>();
        foreach (string file in ProfilePatchFiles())
        {
            string? text = ReadSmall(file);
            if (text is null) continue;
            string profile = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(file)!)!)!)!) ?? "profile";
            string shortName = profile.Length > 12 ? profile[..8] + "…" : profile;
            foreach (var provider in ProfileProviders(text))
            {
                string label = "Solforge AI profile " + shortName + " (" + (provider.DisplayName ?? provider.Id) + ")";
                string lower = provider.Id.ToLowerInvariant();
                foreach (var (modelId, name) in provider.Models)
                {
                    string display = name ?? modelId;
                    if (lower == "openai-codex")
                        Add(label, display, HarnessTransport.CodexSubscription, "openai", modelId, null, null, "Runs through your signed-in codex CLI (hidden, read-only).");
                    else if (lower == "anthropic" && provider.ApiKeyEnv is null)
                        Add(label, display, HarnessTransport.ClaudeSubscription, "anthropic", modelId, null, null, "Runs through your signed-in claude CLI (hidden, read-only).");
                    else if (lower is "google-gemini-cli" or "google-antigravity")
                        Add(label, display, HarnessTransport.GoogleSubscription, "google", modelId, null, null, "Runs through your signed-in Gemini CLI or agy (hidden, read-only).");
                    else if (provider.BaseUrl is { } url && provider.ApiKeyEnv is { } env && provider.Api is null or "openai-completions" or "openai-responses")
                    {
                        if (ProviderCatalogs.IsClineEndpoint(url) && !ProviderCatalogs.IsClinePassId(modelId))
                            // Cline's usage-billed catalog: its own group, never shown as ClinePass.
                            usageBilled.Add(("Cline usage " + display, provider.Id, modelId, url.TrimEnd('/'), env));
                        else
                            Add(label, (provider.DisplayName ?? provider.Id) + " " + display, HarnessTransport.OpenAiCompatible, provider.Id, modelId,
                                url.TrimEnd('/'), env, "OpenAI-compatible (" + url + "); key name " + env + ".");
                    }
                }
            }
        }

        foreach (string id in ProviderCatalogs.ClinePassModels)
            Add("ClinePass (subscription)", "ClinePass " + id, HarnessTransport.OpenAiCompatible, "clinepass", id, ProviderCatalogs.ClinePassBaseUrl,
                ProviderCatalogs.ClinePassKey, "ClinePass model (streamed); needs " + ProviderCatalogs.ClinePassKey + " in your credential file."
                + (id.StartsWith("cline-free/", StringComparison.Ordinal) ? " cline-free models may answer 403 (not available for this key)." : ""));
        foreach (var (id, name, inputCost, outputCost) in ProviderCatalogs.OpenCodeModels)
            Add("OpenCode Zen", "OpenCode " + name, HarnessTransport.OpenAiCompatible, "opencode", id, ProviderCatalogs.OpenCodeBaseUrl, ProviderCatalogs.OpenCodeKey,
                "OpenCode Zen model, " + (inputCost == 0 && outputCost == 0 ? "free" : FormattableString.Invariant($"paid: ${inputCost} / ${outputCost} per million input / output tokens"))
                + "; needs " + ProviderCatalogs.OpenCodeKey + " in your credential file.");

        foreach (var usage in usageBilled)
            Add("Cline (usage-billed, not ClinePass)", usage.Display, HarnessTransport.OpenAiCompatible, usage.Provider, usage.Model, usage.Url, usage.Env,
                "Cline usage-billed model (pay per token, not ClinePass); key name " + usage.Env + ".");

        var clis = SubscriptionClis.Find();
        if (clis.Claude is not null)
            Add("claude CLI", "Claude subscription (CLI default model)", HarnessTransport.ClaudeSubscription, "anthropic", "default", null, null,
                "Your signed-in claude CLI (claude -p), hidden and read-only.");
        if (clis.Codex is not null)
            Add("codex CLI", "Codex subscription (CLI default model)", HarnessTransport.CodexSubscription, "openai", "default", null, null,
                "Your signed-in codex CLI (codex exec, ChatGPT sign-in), hidden and read-only.");
        if (clis.Google is { } google)
            Add(google.Cli + " CLI", "Google AI Pro subscription (CLI default model)", HarnessTransport.GoogleSubscription, "google", "default", null, null,
                google.Cli == "gemini" ? "Your Gemini CLI signed in with your Google account (no API key), hidden and read-only."
                    : "Your signed-in Antigravity CLI (agy -p --mode plan), hidden and read-only; no API key.");
        return output;
    }

    /** One llm-pi-ai provider of a profile patch (names only). */
    public sealed record ProfileProvider(string Id, string? DisplayName, string? Api, string? BaseUrl, string? ApiKeyEnv, IReadOnlyList<(string Id, string? Name)> Models);

    /** Solforge AI's profile homes (SolforgeNext\AiApp\*\home), the shared one first, then the newest. */
    public static IReadOnlyList<string> AppProfileHomes()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolforgeNext", "AiApp");
        if (!Directory.Exists(root)) return [];
        try
        {
            return Directory.EnumerateDirectories(root).Select(directory => Path.Combine(directory, "home")).Where(Directory.Exists)
                .OrderBy(home => string.Equals(Path.GetFileName(Path.GetDirectoryName(home)), "shared", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenByDescending(home => Directory.GetLastWriteTimeUtc(home)).ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> ProfilePatchFiles()
    {
        foreach (string home in AppProfileHomes())
        {
            string profiles = Path.Combine(home, "profiles");
            if (!Directory.Exists(profiles)) continue;
            string[] folders;
            try { folders = Directory.GetDirectories(profiles); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (string folder in folders)
            {
                string file = Path.Combine(folder, "cordis.patch.yml");
                if (File.Exists(file)) yield return file;
            }
        }
    }

    internal static string? ReadSmall(string path)
    {
        try { return new FileInfo(path).Length > MaximumSourceBytes ? null : File.ReadAllText(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static string Unquote(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length >= 2 && (trimmed[0] == '"' && trimmed[^1] == '"' || trimmed[0] == '\'' && trimmed[^1] == '\''))
            return trimmed[0] == '"' ? Regex.Unescape(trimmed[1..^1]) : trimmed[1..^1].Replace("''", "'");
        return trimmed;
    }

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    /** The llm-pi-ai row's providers, by indentation (DSH writes two-space YAML). Key values are never read. */
    public static IReadOnlyList<ProfileProvider> ProfileProviders(string text)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        var output = new List<ProfileProvider>();
        int row = Array.FindIndex(lines, line => Regex.IsMatch(line, @"^- id:\s*[""']?llm-pi-ai[""']?\s*$"));
        if (row < 0) return output;
        int end = Array.FindIndex(lines, row + 1, line => line.StartsWith("- ", StringComparison.Ordinal));
        if (end < 0) end = lines.Length;
        int providersAt = -1;
        for (int i = row + 1; i < end; i++) if (Regex.IsMatch(lines[i], @"^\s+providers:\s*$")) { providersAt = i; break; }
        if (providersAt < 0) return output;
        int baseIndent = Indent(lines[providersAt]), providerIndent = -1, modelsIndent = -1;
        string? id = null, display = null, api = null, url = null, env = null;
        List<(string Id, string? Name)>? models = null;
        bool inModels = false;
        void Flush() { if (id is not null) output.Add(new(id, display, api, url, env, models ?? [])); }
        for (int i = providersAt + 1; i < end; i++)
        {
            string line = lines[i];
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) continue;
            int indent = Indent(line);
            if (indent <= baseIndent) break;
            string body = line.Trim();
            if (providerIndent < 0) providerIndent = indent;
            if (indent == providerIndent)
            {
                var match = Regex.Match(body, @"^([^:\s][^:]*):\s*$");
                if (!match.Success) continue;
                Flush();
                id = Unquote(match.Groups[1].Value); display = api = url = env = null; models = []; inModels = false;
                continue;
            }
            if (id is null || models is null) continue;
            if (indent == providerIndent + 2)
            {
                inModels = false;
                var match = Regex.Match(body, @"^([A-Za-z][A-Za-z0-9_]*):\s*(.*)$");
                if (!match.Success) continue;
                string key = match.Groups[1].Value, value = Unquote(match.Groups[2].Value);
                switch (key)
                {
                    case "models": inModels = true; modelsIndent = -1; break;
                    case "displayName": display = value.Length == 0 ? null : value; break;
                    case "api": api = value.Length == 0 ? null : value; break;
                    case "baseURL": url = value.Length == 0 ? null : value; break;
                    case "apiKeyEnv": env = VariableName.IsMatch(value) ? value : null; break;
                }
                continue;
            }
            if (!inModels) continue;
            var item = Regex.Match(body, @"^-\s+id:\s*(.+)$");
            if (item.Success)
            {
                if (modelsIndent < 0) modelsIndent = indent;
                if (indent == modelsIndent) models.Add((Unquote(item.Groups[1].Value), null));
                continue;
            }
            var name = Regex.Match(body, @"^name:\s*(.+)$");
            if (name.Success && models.Count > 0 && models[^1].Name is null && indent == modelsIndent + 2)
                models[^1] = (models[^1].Id, Unquote(name.Groups[1].Value));
        }
        Flush();
        return output;
    }
}

/** The official subscription CLIs on this machine (file inspection only). */
public sealed record SubscriptionClis(string? Claude, string? Codex, (string Cli, string Path)? Google)
{
    /** Finds claude, codex and the Google CLI (gemini, else a native agy.exe) on PATH and at their default install paths. */
    public static SubscriptionClis Find()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string? claude = Resolve("claude", Path.Combine(home, ".local", "bin", "claude.exe"));
        string? codex = Resolve("codex");
        string? gemini = Resolve("gemini");
        string? agy = gemini is null ? Resolve("agy", Path.Combine(local, "agy", "bin", "agy.exe")) : null;
        (string, string)? google = gemini is not null ? ("gemini", gemini)
            : agy is not null && agy.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? ("agy", agy) : null;
        return new(claude, codex, google);
    }

    /** The first name.exe or name.cmd on PATH (user and machine PATH included), else the fallback when it exists. */
    public static string? Resolve(string name, string? fallback = null)
    {
        string path = string.Join(';', new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)
        }.Where(part => !string.IsNullOrWhiteSpace(part)));
        foreach (string raw in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string folder;
            try { folder = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"')); }
            catch (ArgumentException) { continue; }
            if (folder.Length == 0 || !Path.IsPathRooted(folder)) continue;
            foreach (string extension in new[] { ".exe", ".cmd" })
            {
                string candidate = Path.Combine(folder, name + extension);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return fallback is not null && File.Exists(fallback) ? fallback : null;
    }
}
