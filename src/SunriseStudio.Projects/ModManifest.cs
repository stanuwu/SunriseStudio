using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SunriseStudio.Packages;

namespace SunriseStudio.Projects;

/** Mod details follow the limits the Sunrise mod loader enforces on `mod.json`. */
public static partial class ModManifest
{
    public const string FileName = "mod.json";
    /** Newest format: version 3 added config bindings, version 4 the own package list. */
    public const int ManifestVersion = 4, BindingsVersion = 3;
    /** Own package ids one manifest may list; the loader keeps them in fixed storage. */
    public const int PackageLimit = 256;
    // Loader limits. Ids and config keys count characters; text fields count UTF-8 bytes.
    private const int IdMinimum = 3, IdMaximum = 64, NameBytes = 64, DescriptionBytes = 512;
    private const int AuthorBytes = 64, AuthorLimit = 8, SourceBytes = 256, ListLimit = 32, KeyMaximum = 32;

    /** Returns one message per failed rule; an empty list means the details can be installed. */
    public static IReadOnlyList<string> Validate(ModMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var problems = new List<string>();
        CheckId(metadata.Id, "Mod id", problems);
        CheckVersion(metadata.Version, "Version", problems);
        if (string.IsNullOrEmpty(metadata.Name)) problems.Add("Name is required.");
        else if (Bytes(metadata.Name) > NameBytes) problems.Add($"Name must be at most {NameBytes} UTF-8 bytes.");
        if (metadata.Name?.Any(char.IsControl) == true) problems.Add("Name cannot contain control characters.");
        if (Bytes(metadata.Description) > DescriptionBytes)
            problems.Add($"Description must be at most {DescriptionBytes} UTF-8 bytes.");
        var authors = metadata.Authors ?? [];
        if (authors.Count > AuthorLimit) problems.Add($"List at most {AuthorLimit} authors.");
        for (int index = 0; index < authors.Count; index++)
            if (Bytes(authors[index]) is < 1 or > AuthorBytes)
                problems.Add($"Author {index + 1} must be 1 to {AuthorBytes} UTF-8 bytes.");
        if (Bytes(metadata.Src) > SourceBytes) problems.Add($"Source must be at most {SourceBytes} UTF-8 bytes.");
        var dependencies = metadata.Dependencies ?? [];
        if (dependencies.Count > ListLimit) problems.Add($"List at most {ListLimit} dependencies.");
        for (int index = 0; index < dependencies.Count; index++)
        {
            string label = $"Dependency {index + 1}";
            if (dependencies[index] is not { } dependency) { problems.Add(label + " is empty."); continue; }
            CheckId(dependency.Id, label + " id", problems);
            CheckVersion(dependency.MinVersion, label + " minimum version", problems);
            if (!string.IsNullOrEmpty(dependency.MaxVersion))
                CheckVersion(dependency.MaxVersion, label + " maximum version", problems);
            if (TryParseVersion(dependency.MinVersion, out var minimum) && TryParseVersion(dependency.MaxVersion, out var maximum)
                && maximum.CompareTo(minimum) < 0)
                problems.Add($"Dependency {dependency.Id} has maxVersion below minVersion.");
            if (!string.IsNullOrEmpty(dependency.Id) && dependency.Id == metadata.Id)
                problems.Add(label + " cannot be the mod itself.");
        }
        var incompatible = metadata.Incompatible ?? [];
        if (incompatible.Count > ListLimit) problems.Add($"List at most {ListLimit} incompatible ids.");
        for (int index = 0; index < incompatible.Count; index++)
        {
            string label = $"Incompatible id {index + 1}";
            CheckId(incompatible[index], label, problems);
            if (!string.IsNullOrEmpty(incompatible[index]) && incompatible[index] == metadata.Id)
                problems.Add(label + " cannot be the mod itself.");
        }
        string[] dependencyIds = dependencies.Select(dependency => dependency?.Id).OfType<string>()
            .Where(id => id.Length != 0).ToArray();
        string[] incompatibleIds = incompatible.OfType<string>().Where(id => id.Length != 0).ToArray();
        foreach (string id in Repeated(dependencyIds)) problems.Add($"Dependency {id} is listed twice.");
        foreach (string id in Repeated(incompatibleIds)) problems.Add($"Incompatible id {id} is listed twice.");
        foreach (string id in dependencyIds.Intersect(incompatibleIds, StringComparer.Ordinal))
            problems.Add($"{id} is both a dependency and incompatible.");
        CheckConfig(metadata.Config ?? [], problems);
        return problems;
    }

    /** An empty name is left out of `mod.json`, so only a written name is checked. */
    private static void CheckConfig(IReadOnlyList<ModConfigEntry> config, List<string> problems)
    {
        if (config.Count > ListLimit) problems.Add($"List at most {ListLimit} config entries.");
        for (int index = 0; index < config.Count; index++)
        {
            string label = $"Config entry {index + 1}";
            if (config[index] is not { } entry) { problems.Add(label + " is empty."); continue; }
            if (string.IsNullOrEmpty(entry.Key)) problems.Add(label + " key is required.");
            else if (entry.Key.Length > KeyMaximum || !char.IsAsciiLetterLower(entry.Key[0])
                || !entry.Key.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '_'))
                problems.Add($"{label} key must be 1 to {KeyMaximum} characters from a-z, 0-9 and '_', starting with a letter.");
            if (!Enum.IsDefined(entry.Type)) problems.Add(label + " type must be int, float or bool.");
            else if (string.IsNullOrEmpty(entry.Default)) problems.Add(label + " default is required.");
            else if (entry.Type == ModConfigType.Int && !TryParseInt(entry.Default, out _))
                problems.Add(label + " default must be a whole number that fits 32 bits.");
            else if (entry.Type == ModConfigType.Float && !TryParseFloat(entry.Default, out _))
                problems.Add(label + " default must be a finite number that fits a 32-bit float.");
            else if (entry.Type == ModConfigType.Bool && !TryParseBool(entry.Default, out _))
                problems.Add(label + " default must be true or false.");
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (Bytes(entry.Name) > NameBytes) problems.Add($"{label} name must be at most {NameBytes} UTF-8 bytes.");
            if (entry.Name.Any(char.IsControl)) problems.Add(label + " name cannot contain control characters.");
        }
        foreach (string key in Repeated(config.Select(entry => entry?.Key).OfType<string>().Where(key => key.Length != 0)))
            problems.Add($"Config key {key} is listed twice.");
    }

    private static IEnumerable<string> Repeated(IEnumerable<string> ids) =>
        ids.GroupBy(id => id, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key);

    public static void Require(ModMetadata metadata)
    {
        var problems = Validate(metadata);
        if (problems.Count > 0) throw new InvalidDataException(string.Join("\n", problems));
    }

    /** UTF-8 without a BOM, camelCase keys, LF line ends; empty optional fields are omitted. */
    public static byte[] Serialize(ModMetadata metadata) => Serialize(metadata, null);

    /**
     * Serializes the details, compiled config sites and the new package ids the mod ships. Any site
     * raises manifestVersion to 3; any package id raises it to 4.
     */
    public static byte[] Serialize(ModMetadata metadata, IReadOnlyList<ModConfigSite>? bindings, IReadOnlyCollection<ushort>? packages = null)
    {
        Require(metadata);
        RequireBindings(metadata, bindings ?? []);
        var own = (packages ?? []).Order().ToArray();
        if (own.Length > PackageLimit || own.Distinct().Count() != own.Length || own.Any(id => !PackageWriter.IsNewPackageId(id)))
            throw new InvalidDataException($"A mod lists at most {PackageLimit} distinct package ids from {PackageWriter.NewPackageIdRange}.");
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions
            { Indented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteNumber("manifestVersion", own.Length > 0 ? ManifestVersion : bindings is { Count: > 0 } ? BindingsVersion : 2);
            json.WriteString("id", metadata.Id);
            json.WriteString("version", metadata.Version);
            json.WriteString("name", metadata.Name);
            if (!string.IsNullOrEmpty(metadata.Description)) json.WriteString("description", metadata.Description);
            WriteList(json, "authors", metadata.Authors);
            if (!string.IsNullOrEmpty(metadata.Src)) json.WriteString("src", metadata.Src);
            if (metadata.Dependencies is { Count: > 0 } dependencies)
            {
                json.WriteStartArray("dependencies");
                foreach (var dependency in dependencies)
                {
                    json.WriteStartObject();
                    json.WriteString("id", dependency.Id);
                    json.WriteString("minVersion", dependency.MinVersion);
                    if (!string.IsNullOrEmpty(dependency.MaxVersion)) json.WriteString("maxVersion", dependency.MaxVersion);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
            }
            WriteList(json, "incompatible", metadata.Incompatible);
            if (metadata.Cheat) json.WriteBoolean("cheat", true);
            if (metadata.Config is { Count: > 0 } config)
            {
                json.WriteStartArray("config");
                foreach (var entry in config) WriteConfigEntry(json, entry);
                json.WriteEndArray();
            }
            WriteBindings(json, bindings ?? []);
            if (own.Length > 0)
            {
                json.WriteStartArray("packages");
                foreach (ushort id in own) json.WriteStringValue($"0x{id:X4}");
                json.WriteEndArray();
            }
            json.WriteEndObject();
        }
        stream.WriteByte((byte)'\n');
        if (stream.Length > 256 * 1024) throw new InvalidDataException("Mod manifest exceeds 256 KiB.");
        return stream.ToArray();
    }

    /** The default is written as a JSON integer, number or boolean to match its type. */
    private static void WriteConfigEntry(Utf8JsonWriter json, ModConfigEntry entry)
    {
        json.WriteStartObject();
        json.WriteString("key", entry.Key);
        switch (entry.Type)
        {
            case ModConfigType.Int:
                json.WriteString("type", "int");
                TryParseInt(entry.Default, out int integer);
                json.WriteNumber("default", integer);
                break;
            case ModConfigType.Float:
                json.WriteString("type", "float");
                TryParseFloat(entry.Default, out float real);
                json.WriteNumber("default", real);
                break;
            default:
                json.WriteString("type", "bool");
                TryParseBool(entry.Default, out bool flag);
                json.WriteBoolean("default", flag);
                break;
        }
        if (!string.IsNullOrEmpty(entry.Name)) json.WriteString("name", entry.Name);
        json.WriteEndObject();
    }

    public static bool TryParseInt(string? text, out int value) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    /** A float fits when the parsed value rounds to a finite 32-bit float. */
    public static bool TryParseFloat(string? text, out float value)
    {
        value = 0;
        if (!double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture, out double parsed) || !double.IsFinite(parsed) || !float.IsFinite((float)parsed))
            return false;
        value = (float)parsed;
        return true;
    }

    public static bool TryParseBool(string? text, out bool value)
    {
        value = text == "true";
        return text is "true" or "false";
    }

    private static void WriteList(Utf8JsonWriter json, string name, IReadOnlyList<string>? values)
    {
        if (values is not { Count: > 0 }) return;
        json.WriteStartArray(name);
        foreach (string value in values) json.WriteStringValue(value);
        json.WriteEndArray();
    }

    /** Lowercase ASCII parts joined by dots; each part starts with a letter or digit. */
    private static void CheckId(string? id, string label, List<string> problems)
    {
        if (string.IsNullOrEmpty(id)) { problems.Add(label + " is required."); return; }
        if (id.Length is < IdMinimum or > IdMaximum)
            problems.Add($"{label} must be {IdMinimum} to {IdMaximum} characters.");
        if (!id.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.'))
            problems.Add(label + " may use only lowercase letters, digits, '-', '_' and '.'.");
        string[] parts = id.Split('.');
        if (parts.Length < 2) problems.Add(label + " needs at least two parts separated by '.'.");
        else if (parts.Any(part => part.Length == 0 || !char.IsAsciiLetterOrDigit(part[0])))
            problems.Add(label + " parts cannot be empty and must start with a letter or digit.");
    }

    private static void CheckVersion(string? version, string label, List<string> problems)
    {
        if (string.IsNullOrEmpty(version)) problems.Add(label + " is required.");
        else if (!TryParseVersion(version, out _))
            problems.Add(label + " must be MAJOR.MINOR.PATCH with numbers from 0 to 65535 and no leading zeros.");
    }

    /** MAJOR.MINOR.PATCH, each part 0 to 65535, with no leading zero except a lone 0. */
    private static bool TryParseVersion(string? text, out (int Major, int Minor, int Patch) version)
    {
        version = default;
        string[] parts = (text ?? "").Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length is < 1 or > 5 || !part.All(char.IsAsciiDigit)
            || part.Length > 1 && part[0] == '0' || int.Parse(part, CultureInfo.InvariantCulture) > ushort.MaxValue))
            return false;
        version = (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture),
            int.Parse(parts[2], CultureInfo.InvariantCulture));
        return true;
    }

    private static int Bytes(string? text) => Encoding.UTF8.GetByteCount(text ?? "");
}
