/** File: ported from solforge-next (Solforge.Next.Harness/HarnessSettingsValidator.cs); only the namespace changed. */
using System.Text.RegularExpressions;

namespace Solforge.Harness;

public sealed class HarnessSettingsValidationException : ArgumentException
{
    public IReadOnlyList<string> Errors { get; }

    public HarnessSettingsValidationException(IEnumerable<string> errors)
        : this(errors.ToArray()) { }

    private HarnessSettingsValidationException(string[] errors)
        : base("Harness settings are invalid: " + string.Join("; ", errors))
    {
        Errors = Array.AsReadOnly(errors);
    }
}

public static class HarnessSettingsValidator
{
    public const int MaximumModels = 256;
    public const int MaximumParallelSubagents = 64;
    public const int MaximumSubagentDepth = 16;

    /// <summary>Validates structure only. Does not open credential/source files or resolve secrets.</summary>
    public static IReadOnlyList<string> Validate(HarnessSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = new List<string>();
        if (settings.MaxParallelSubagents is < 1 or > MaximumParallelSubagents)
            errors.Add($"MaxParallelSubagents must be 1..{MaximumParallelSubagents}.");
        if (settings.MaxSubagentDepth is < 0 or > MaximumSubagentDepth)
            errors.Add($"MaxSubagentDepth must be 0..{MaximumSubagentDepth} (0 disables subagents).");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (settings.Models is null)
            errors.Add("Models must not be null.");
        else
        {
            if (settings.Models.Count > MaximumModels)
                errors.Add($"At most {MaximumModels} models are supported.");
            for (var i = 0; i < settings.Models.Count; i++)
            {
                var model = settings.Models[i];
                var label = $"Models[{i}]";
                if (model is null) { errors.Add($"{label} must not be null."); continue; }
                if (!IsIdentifier(model.Id))
                    errors.Add($"{label}.Id must be nonempty without surrounding whitespace.");
                else if (!ids.Add(model.Id))
                    errors.Add($"{label}.Id duplicates another model (IDs are case-sensitive).");
                if (string.IsNullOrWhiteSpace(model.DisplayName))
                    errors.Add($"{label}.DisplayName is required.");
                if (!IsIdentifier(model.Provider)) errors.Add($"{label}.Provider is required.");
                if (!IsIdentifier(model.ModelId)) errors.Add($"{label}.ModelId is required.");
                if (!Enum.IsDefined(model.Transport)) errors.Add($"{label}.Transport is invalid.");
                if (!Enum.IsDefined(model.Tier)) errors.Add($"{label}.Tier is invalid.");
                if (model.ApiKeyVariable is not null &&
                    !Regex.IsMatch(model.ApiKeyVariable, @"\A[A-Za-z_][A-Za-z0-9_]*\z"))
                    errors.Add($"{label}.ApiKeyVariable must be an environment variable name.");
                if (model.BaseUrl is not null &&
                    (!Uri.TryCreate(model.BaseUrl, UriKind.Absolute, out var uri) ||
                     (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
                     !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
                     !string.IsNullOrEmpty(uri.Fragment)))
                    errors.Add($"{label}.BaseUrl must be HTTP(S) without user info, query, or fragment.");
            }
        }

        var roles = new HashSet<HarnessRole>();
        if (settings.Routes is null)
            errors.Add("Routes must not be null.");
        else
        {
            if (settings.Routes.Count > Enum.GetValues<HarnessRole>().Length)
                errors.Add("Too many role routes.");
            for (var i = 0; i < settings.Routes.Count; i++)
            {
                var route = settings.Routes[i];
                var label = $"Routes[{i}]";
                if (route is null) { errors.Add($"{label} must not be null."); continue; }
                if (!Enum.IsDefined(route.Role)) errors.Add($"{label}.Role is invalid.");
                if (!roles.Add(route.Role)) errors.Add($"{label}.Role duplicates another route.");
                if (route.ModelIds is null) { errors.Add($"{label}.ModelIds must not be null."); continue; }
                if (route.ModelIds.Count > MaximumModels)
                    errors.Add($"{label} exceeds {MaximumModels} models.");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in route.ModelIds)
                {
                    if (string.IsNullOrWhiteSpace(id) || !ids.Contains(id))
                        errors.Add($"{label} references a missing or empty model ID.");
                    if (!seen.Add(id)) errors.Add($"{label} contains a duplicate model ID.");
                }
            }
        }
        return errors.AsReadOnly();
    }

    public static void ValidateAndThrow(HarnessSettings settings)
    {
        var errors = Validate(settings);
        if (errors.Count != 0) throw new HarnessSettingsValidationException(errors);
    }

    /// <summary>Why <see cref="IsLaunchableCredentialName"/> refused a name (no values are ever included).</summary>
    public const string CredentialNameRule =
        "Credential variable names passed to Solforge AI must contain KEY, TOKEN, SECRET or PASSWORD (so the runtime scrubs them from tool and MCP child processes) and must not name an inherited system, proxy, certificate or NODE_/UV_/npm_ variable.";

    private static readonly HashSet<string> InheritedLaunchNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SystemRoot", "SystemDrive", "windir", "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "HOMEDRIVE", "HOMEPATH",
        "PATH", "PATHEXT", "COMSPEC", "OS", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS", "ProgramData", "ProgramFiles",
        "ProgramFiles(x86)", "ProgramW6432", "ComputerName", "USERNAME", "USERDOMAIN", "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY",
        "ALL_PROXY", "SSL_CERT_FILE", "SSL_CERT_DIR", "NODE_EXTRA_CA_CERTS", "NODE_TLS_REJECT_UNAUTHORIZED", "REQUESTS_CA_BUNDLE", "CURL_CA_BUNDLE"
    };

    /// <summary>
    /// Whether a credential variable may be copied into the separate Solforge AI process. The runtime's child scrub drops
    /// only names matching /KEY|PASSWORD|SECRET|TOKEN/i, so other names would reach every tool/MCP child; inherited
    /// system, proxy and TLS names would replace the editor's value for the whole AI process tree.
    /// Not a settings validation error: API workflows run in the editor and are unaffected. Enforced at Solforge AI launch.
    /// </summary>
    public static bool IsLaunchableCredentialName(string name) =>
        Regex.IsMatch(name, @"\A[A-Za-z_][A-Za-z0-9_]*\z")
        && Regex.IsMatch(name, "KEY|PASSWORD|SECRET|TOKEN", RegexOptions.IgnoreCase)
        && !InheritedLaunchNames.Contains(name)
        && !name.StartsWith("NODE_", StringComparison.OrdinalIgnoreCase)
        && !name.StartsWith("UV_", StringComparison.OrdinalIgnoreCase)
        && !name.StartsWith("npm_", StringComparison.OrdinalIgnoreCase);

    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value == value.Trim();
}
