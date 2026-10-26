/**
 * File: keys the user saved inside Solforge AI (the `refs` block of its shared profile's .credentials.yaml), read as a
 * fallback for editor workflows when the editor's credential file does not name the key. The user approved reading
 * that store on 2026-10-25 ("I set the api keys inside the app, read it from there"). Only `refs` entries are read
 * (never `records`), values stay in memory for the one lookup and are never logged; the file is capped at 64 KB.
 */
namespace Solforge.Harness.Credentials;

/** Solforge AI's saved provider keys. */
public static class AppCredentialStore
{
    /** The shared Solforge AI profile's key store (DSH_HOME when set, else %LOCALAPPDATA%\SolforgeNext\AiApp\shared\home). */
    public static string StorePath() =>
        Path.Combine(Environment.GetEnvironmentVariable("DSH_HOME") is { Length: > 0 } home ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolforgeNext", "AiApp", "shared", "home"),
            ".credentials.yaml");

    /** The value saved for name in Solforge AI, or null. */
    public static string? Value(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string path = StorePath();
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > CredentialEnvironment.MaximumBytes) return null;
            return Refs(File.ReadAllLines(path)).GetValueOrDefault(name);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /** True when Solforge AI has a value saved for name. */
    public static bool Has(string name) => !string.IsNullOrEmpty(Value(name));

    /** The `refs:` block: one indented `NAME: value` line per key (plain, 'single' or "double" quoted scalars). */
    private static Dictionary<string, string> Refs(string[] lines)
    {
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        bool inRefs = false;
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd();
            if (line.Length == 0 || line.TrimStart().StartsWith('#')) continue;
            if (!char.IsWhiteSpace(line[0])) { inRefs = line == "refs:"; continue; }
            if (!inRefs) continue;
            string entry = line.Trim();
            int colon = entry.IndexOf(": ", StringComparison.Ordinal);
            if (colon <= 0) continue;
            string key = entry[..colon].Trim(), value = entry[(colon + 2)..].Trim();
            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') value = value[1..^1].Replace("''", "'");
            else if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = System.Text.RegularExpressions.Regex.Unescape(value[1..^1]);
            if (value.Length > 0) refs[key] = value;
        }
        return refs;
    }
}
