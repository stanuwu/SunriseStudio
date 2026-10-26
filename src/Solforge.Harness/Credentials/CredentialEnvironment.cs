/**
 * File: The credential file (Settings > Solforge AI > Credential file), ported from solforge-next (CredentialEnvironment
 * in SolforgeAiHost.cs). At most 64 KB, a flat JSON object of strings or KEY=value lines. Values are only ever handed to
 * a child process environment or an outgoing Authorization header; they are never logged, shown or returned by Inspect,
 * and no error message carries file contents.
 */
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Solforge.Harness.Credentials;

/** A name the credential file holds and why it may not be passed to Solforge AI (names only, never values). */
public sealed record CredentialRefusal(string Name, string Reason);

/** What the settings page shows for a credential file: counts and names, never values. */
public sealed record CredentialFileReport(bool Readable, IReadOnlyList<string> Accepted, IReadOnlyList<CredentialRefusal> Refused, string? Error)
{
    /** "3 names accepted, 1 refused: NAME (reason)" or the read error. */
    public string Summary => !Readable ? Error ?? "The credential file could not be read."
        : $"{Accepted.Count} name{(Accepted.Count == 1 ? "" : "s")} accepted, {Refused.Count} refused"
          + (Refused.Count == 0 ? "" : ": " + string.Join("; ", Refused.Select(refused => refused.Name + " (" + refused.Reason + ")")));
}

/** Reads and validates the credential file. */
public static class CredentialEnvironment
{
    public const int MaximumBytes = 65536;

    /** Every name/value pair. Throws InvalidDataException with a curated message (no contents) when the file is malformed. */
    public static IReadOnlyDictionary<string, string> Read(string path)
    {
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("The credential file must be at most 64 KB.");
        string text = File.ReadAllText(path);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (text.TrimStart().StartsWith('{'))
        {
            try
            {
                using var json = JsonDocument.Parse(text);
                if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Credential JSON must be a flat object.");
                foreach (var property in json.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Credential JSON values must be strings.");
                    Add(result, property.Name, property.Value.GetString()!);
                }
            }
            catch (JsonException) { throw new InvalidDataException("The credential file must be a flat JSON object or KEY=value lines. Contents were not logged."); }
        }
        else
        {
            int lineNumber = 0;
            foreach (string raw in text.Split('\n'))
            {
                lineNumber++; string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
                int equals = line.IndexOf('=');
                if (equals <= 0) throw new InvalidDataException($"Credential line {lineNumber} must use KEY=value. Its contents were not logged.");
                string value = line[(equals + 1)..].Trim();
                if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'')) value = value[1..^1];
                Add(result, line[..equals].Trim(), value);
            }
        }
        return result;
    }

    /** One value by name, or null (missing file, malformed file or no such name). */
    public static string? Value(string? path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try { return Read(path).GetValueOrDefault(name); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    /** Names the file holds, split into those a Solforge AI launch accepts and those it refuses. Values are dropped at once. */
    public static CredentialFileReport Inspect(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new(false, [], [], "No credential file chosen.");
        if (!File.Exists(path)) return new(false, [], [], "The credential file does not exist.");
        try
        {
            var names = Read(path).Keys.Order(StringComparer.Ordinal).ToList();
            var accepted = new List<string>();
            var refused = new List<CredentialRefusal>();
            foreach (string name in names)
                if (RefusalReason(name) is { } reason) refused.Add(new(name, reason));
                else accepted.Add(name);
            return new(true, accepted, refused, null);
        }
        catch (InvalidDataException error) { return new(false, [], [], error.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new(false, [], [], "The credential file could not be read."); }
    }

    /** Why a credential name cannot be passed to Solforge AI, or null when it can. */
    public static string? RefusalReason(string name)
    {
        if (!Regex.IsMatch(name, @"\A[A-Za-z_][A-Za-z0-9_]*\z")) return "not a variable name";
        if (IsReservedLaunchName(name)) return "reserved for the Solforge AI launch";
        if (!HarnessSettingsValidator.IsLaunchableCredentialName(name))
            return Regex.IsMatch(name, "KEY|PASSWORD|SECRET|TOKEN", RegexOptions.IgnoreCase)
                ? "inherited, proxy, TLS or NODE_/UV_/npm_ name" : "name must contain KEY, TOKEN, SECRET or PASSWORD";
        return null;
    }

    /** SOLFORGE_*, ELECTRON_* and DSH_* belong to the launch itself. */
    public static bool IsReservedLaunchName(string name) =>
        name.StartsWith("SOLFORGE_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ELECTRON_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("DSH_", StringComparison.OrdinalIgnoreCase);

    private static void Add(Dictionary<string, string> target, string name, string value)
    {
        if (!Regex.IsMatch(name, @"\A[A-Za-z_][A-Za-z0-9_]*\z") || value.Length is < 1 or > 8192 || value.Any(char.IsControl)) throw new InvalidDataException("A credential entry has an invalid name or value. Contents were not logged.");
        if (name is "NODE_OPTIONS" or "NODE_PATH" or "PATH" or "COMSPEC" or "DSH_HOME" || name.StartsWith("SOLFORGE_NEXT_", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Credential files cannot override runtime or editor connection settings.");
        if (!target.TryAdd(name, value)) throw new InvalidDataException("The credential file contains a duplicate variable name.");
    }
}
