using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SunriseStudio.World.Import;

/** One bundle failure: the file, the JSON path in it, and what is wrong. */
public sealed record WorldBundleIssue(string File, string Path, string Message)
{
    /** The issue as `file: path: message`; the root path prints as `$`. */
    public override string ToString() => $"{File}: {(Path.Length == 0 ? "$" : Path)}: {Message}";
}

/**
 * The World Import Bundle schema and a validator for the JSON Schema keywords it uses. A keyword the
 * validator does not know makes the schema refuse to load, so no rule is skipped silently.
 */
public sealed partial class WorldBundleSchema
{
    /** The schema file name; the file sits next to this source and is embedded. */
    public const string FileName = "world-import-bundle.v1-draft.schema.json";

    private static readonly HashSet<string> Annotations =
        ["$schema", "$id", "$comment", "title", "description", "$defs", "x-provisional"];
    private static readonly HashSet<string> Keywords =
    [
        "$ref", "type", "const", "enum", "minLength", "maxLength", "pattern", "x-file", "minimum", "maximum",
        "exclusiveMinimum", "exclusiveMaximum", "minItems", "maxItems", "uniqueItems", "prefixItems", "items",
        "required", "properties", "additionalProperties", "propertyNames", "dependentRequired", "allOf", "anyOf", "oneOf", "not",
        "if", "then", "else",
    ];

    private readonly JsonElement root;
    private readonly Dictionary<string, Regex> patterns = new(StringComparer.Ordinal);

    private WorldBundleSchema(JsonElement root)
    {
        this.root = root;
        CheckKeywords(root, "#");
    }

    /** A schema from its JSON text. @throws InvalidDataException on a keyword the validator does not know. */
    public static WorldBundleSchema Parse(string text)
    {
        using var document = JsonDocument.Parse(text);
        return new(document.RootElement.Clone());
    }

    /** The schema from `path`, else the copy embedded in this assembly. */
    public static WorldBundleSchema Load(string? path = null)
    {
        if (path is not null) return Parse(File.ReadAllText(path));
        using var stream = typeof(WorldBundleSchema).Assembly.GetManifestResourceStream(FileName)!;
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /** Every field name the schema defines in any `properties` block. */
    public IReadOnlySet<string> FieldNames()
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        void Walk(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) Walk(item);
            if (node.ValueKind != JsonValueKind.Object) return;
            foreach (var property in node.EnumerateObject())
            {
                if (property.Name == "properties" && property.Value.ValueKind == JsonValueKind.Object)
                    foreach (var field in property.Value.EnumerateObject()) names.Add(field.Name);
                Walk(property.Value);
            }
        }
        Walk(root);
        return names;
    }

    /**
     * Checks one parsed JSON file against a definition under $defs.
     * @param definition The $defs entry, such as bundle or bubble.
     * @param file The file's label in each issue.
     * @param folder The folder x-file paths are relative to.
     * @return One issue per failure, in document order.
     */
    public IReadOnlyList<WorldBundleIssue> Validate(JsonElement instance, string definition, string file, string folder)
    {
        var errors = new List<(string Path, string Message)>();
        Check(root.GetProperty("$defs").GetProperty(definition), instance, "", folder, errors);
        return errors.Select(error => new WorldBundleIssue(file, error.Path, error.Message)).ToArray();
    }

    /** Refuses any keyword outside Keywords and Annotations, in every subschema. */
    private static void CheckKeywords(JsonElement schema, string at)
    {
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False) return;
        if (schema.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"Schema {at} is not an object or a boolean.");
        foreach (var property in schema.EnumerateObject())
        {
            string name = property.Name, here = $"{at}/{name}";
            if (!Keywords.Contains(name) && !Annotations.Contains(name))
                throw new InvalidDataException($"Schema {here} uses a keyword the bundle validator does not check.");
            switch (name)
            {
                case "properties" or "$defs":
                    foreach (var child in property.Value.EnumerateObject()) CheckKeywords(child.Value, $"{here}/{child.Name}");
                    break;
                case "prefixItems" or "allOf" or "anyOf" or "oneOf":
                    int index = 0;
                    foreach (var child in property.Value.EnumerateArray()) CheckKeywords(child, $"{here}/{index++}");
                    break;
                case "items" or "additionalProperties" or "propertyNames" or "not" or "if" or "then" or "else":
                    CheckKeywords(property.Value, here);
                    break;
            }
        }
    }

    private static string Child(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

    /**
     * Applies one subschema to one value and appends each failure.
     * @param folder The folder x-file paths are relative to.
     */
    private void Check(JsonElement schema, JsonElement value, string path, string folder, List<(string Path, string Message)> errors)
    {
        if (schema.ValueKind == JsonValueKind.True) return;
        if (schema.ValueKind == JsonValueKind.False) { errors.Add((path, "not allowed")); return; }
        if (schema.TryGetProperty("$ref", out var reference)) Check(Resolve(reference.GetString()!), value, path, folder, errors);
        if (schema.TryGetProperty("type", out var type) && !HasType(type, value))
        {
            errors.Add((path, $"must be {TypeText(type)}"));
            return;
        }
        if (schema.TryGetProperty("const", out var constant) && !JsonEquals(constant, value))
            errors.Add((path, $"must be {constant.GetRawText()}"));
        if (schema.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(choice => JsonEquals(choice, value)))
            errors.Add((path, $"must be one of {choices.GetRawText()}"));
        switch (value.ValueKind)
        {
            case JsonValueKind.String: CheckString(schema, value.GetString()!, path, folder, errors); break;
            case JsonValueKind.Number: CheckNumber(schema, value.GetDouble(), path, errors); break;
            case JsonValueKind.Array: CheckArray(schema, value, path, folder, errors); break;
            case JsonValueKind.Object: CheckObject(schema, value, path, folder, errors); break;
        }
        CheckCombinators(schema, value, path, folder, errors);
    }

    private JsonElement Resolve(string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal)) throw new InvalidDataException($"Schema reference {reference} is not local.");
        var node = root;
        foreach (string part in reference[2..].Split('/')) node = node.GetProperty(part.Replace("~1", "/").Replace("~0", "~"));
        return node;
    }

    private static bool HasType(JsonElement type, JsonElement value) => type.ValueKind == JsonValueKind.Array
        ? type.EnumerateArray().Any(item => HasType(item.GetString()!, value))
        : HasType(type.GetString()!, value);

    // An integer is a number Int64 can hold exactly, the same test the reader's (int) casts make.
    private static bool HasType(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => throw new InvalidDataException($"Schema type {type} is not a JSON Schema type."),
    };

    private static string TypeText(JsonElement type) => type.ValueKind == JsonValueKind.Array
        ? string.Join(" or ", type.EnumerateArray().Select(item => Article(item.GetString()!)))
        : Article(type.GetString()!);

    private static string Article(string type) => type is "object" or "array" or "integer" ? $"an {type}" : $"a {type}";

    private void CheckString(JsonElement schema, string text, string path, string folder, List<(string Path, string Message)> errors)
    {
        if (schema.TryGetProperty("minLength", out var least) && text.Length < least.GetInt32())
            errors.Add((path, $"needs at least {least.GetInt32()} characters"));
        if (schema.TryGetProperty("maxLength", out var most) && text.Length > most.GetInt32())
            errors.Add((path, $"holds at most {most.GetInt32()} characters"));
        if (schema.TryGetProperty("pattern", out var pattern))
        {
            string source = pattern.GetString()!;
            if (!patterns.TryGetValue(source, out var regex)) patterns[source] = regex = new Regex(source, RegexOptions.CultureInvariant);
            if (!regex.IsMatch(text)) errors.Add((path, $"\"{text}\" does not match {source}"));
        }
        if (schema.TryGetProperty("x-file", out var file) && file.ValueKind == JsonValueKind.True
            && !File.Exists(Path.Combine(folder, text)))
            errors.Add((path, $"file not found: {text}"));
    }

    private static void CheckNumber(JsonElement schema, double number, string path, List<(string Path, string Message)> errors)
    {
        string Text(JsonElement bound) => bound.GetDouble().ToString(CultureInfo.InvariantCulture);
        if (schema.TryGetProperty("minimum", out var minimum) && number < minimum.GetDouble())
            errors.Add((path, $"must be at least {Text(minimum)}"));
        if (schema.TryGetProperty("maximum", out var maximum) && number > maximum.GetDouble())
            errors.Add((path, $"must be at most {Text(maximum)}"));
        if (schema.TryGetProperty("exclusiveMinimum", out var above) && number <= above.GetDouble())
            errors.Add((path, $"must be above {Text(above)}"));
        if (schema.TryGetProperty("exclusiveMaximum", out var below) && number >= below.GetDouble())
            errors.Add((path, $"must be below {Text(below)}"));
    }

    /** minItems, maxItems, uniqueItems, then prefixItems on the first items and items on the rest. */
    private void CheckArray(JsonElement schema, JsonElement array, string path, string folder, List<(string Path, string Message)> errors)
    {
        int count = array.GetArrayLength();
        if (schema.TryGetProperty("minItems", out var least) && count < least.GetInt32())
            errors.Add((path, $"needs at least {least.GetInt32()} item{(least.GetInt32() == 1 ? "" : "s")}"));
        if (schema.TryGetProperty("maxItems", out var most) && count > most.GetInt32())
            errors.Add((path, $"holds at most {most.GetInt32()} item{(most.GetInt32() == 1 ? "" : "s")}"));
        var items = array.EnumerateArray().ToArray();
        if (schema.TryGetProperty("uniqueItems", out var unique) && unique.ValueKind == JsonValueKind.True)
            for (int i = 0; i < items.Length; i++)
                for (int j = 0; j < i; j++)
                    if (JsonEquals(items[i], items[j])) { errors.Add(($"{path}[{i}]", $"repeats item {j}")); break; }
        int prefix = 0;
        if (schema.TryGetProperty("prefixItems", out var prefixItems))
            foreach (var itemSchema in prefixItems.EnumerateArray())
            {
                if (prefix >= items.Length) break;
                Check(itemSchema, items[prefix], $"{path}[{prefix}]", folder, errors);
                prefix++;
            }
        if (schema.TryGetProperty("items", out var rest))
            for (int i = prefix; i < items.Length; i++) Check(rest, items[i], $"{path}[{i}]", folder, errors);
    }

    /** required, dependentRequired, properties, then additionalProperties on the fields properties does not name. */
    private void CheckObject(JsonElement schema, JsonElement value, string path, string folder, List<(string Path, string Message)> errors)
    {
        if (schema.TryGetProperty("required", out var required))
            foreach (var name in required.EnumerateArray().Select(item => item.GetString()!))
                if (!value.TryGetProperty(name, out _)) errors.Add((Child(path, name), "missing"));
        if (schema.TryGetProperty("dependentRequired", out var dependent))
            foreach (var rule in dependent.EnumerateObject().Where(rule => value.TryGetProperty(rule.Name, out _)))
                foreach (var name in rule.Value.EnumerateArray().Select(item => item.GetString()!))
                    if (!value.TryGetProperty(name, out _)) errors.Add((Child(path, name), $"missing (needed with {rule.Name})"));
        if (schema.TryGetProperty("propertyNames", out var names))
            foreach (var field in value.EnumerateObject())
                using (var document = JsonDocument.Parse(JsonSerializer.Serialize(field.Name)))
                    foreach (var error in Try(names, document.RootElement, Child(path, field.Name), folder))
                        errors.Add((error.Path, "field name: " + error.Message));
        bool hasProperties = schema.TryGetProperty("properties", out var properties);
        bool hasAdditional = schema.TryGetProperty("additionalProperties", out var additional);
        foreach (var field in value.EnumerateObject())
        {
            if (hasProperties && properties.TryGetProperty(field.Name, out var fieldSchema))
                Check(fieldSchema, field.Value, Child(path, field.Name), folder, errors);
            else if (hasAdditional && additional.ValueKind == JsonValueKind.False)
                errors.Add((Child(path, field.Name), "unknown field"));
            else if (hasAdditional)
                Check(additional, field.Value, Child(path, field.Name), folder, errors);
        }
    }

    /** allOf, anyOf, oneOf, not and if/then/else. */
    private void CheckCombinators(JsonElement schema, JsonElement value, string path, string folder, List<(string Path, string Message)> errors)
    {
        if (schema.TryGetProperty("allOf", out var all))
            foreach (var branch in all.EnumerateArray()) Check(branch, value, path, folder, errors);
        if (schema.TryGetProperty("anyOf", out var any))
        {
            var results = any.EnumerateArray().Select(branch => Try(branch, value, path, folder)).ToArray();
            if (results.All(result => result.Count > 0)) errors.AddRange(results.MinBy(result => result.Count)!);
        }
        if (schema.TryGetProperty("oneOf", out var one))
        {
            var branches = one.EnumerateArray().ToArray();
            var results = branches.Select(branch => Try(branch, value, path, folder)).ToArray();
            int matched = results.Count(result => result.Count == 0);
            string? names = RequiredOnly(branches);
            if (matched == 0) errors.AddRange(names is null ? results.MinBy(result => result.Count)! : [(path, $"needs one of: {names}")]);
            else if (matched > 1) errors.Add((path, names is null ? "matches more than one choice" : $"give only one of: {names}"));
        }
        if (schema.TryGetProperty("not", out var not) && Try(not, value, path, folder).Count == 0)
            errors.Add((path, RequiredOnly([not]) is { } names ? $"may not give all of: {names}"
                : not.ValueKind == JsonValueKind.Object && not.TryGetProperty("const", out var banned) && not.EnumerateObject().Count() == 1
                    ? $"may not be {banned.GetRawText()}" : "matches a shape that is not allowed"));
        if (schema.TryGetProperty("if", out var condition))
        {
            bool holds = Try(condition, value, path, folder).Count == 0;
            if (schema.TryGetProperty(holds ? "then" : "else", out var branch))
            {
                var found = Try(branch, value, path, folder);
                string reason = holds && condition.TryGetProperty("required", out var given)
                    ? $" (needed with {string.Join(", ", given.EnumerateArray().Select(item => item.GetString()))})" : "";
                errors.AddRange(found.Select(error => (error.Path, error.Message + reason)));
            }
        }
    }

    private List<(string Path, string Message)> Try(JsonElement schema, JsonElement value, string path, string folder)
    {
        var found = new List<(string Path, string Message)>();
        Check(schema, value, path, folder, found);
        return found;
    }

    /** The required names of branches that hold only `required`, joined; null when any branch holds more. */
    private static string? RequiredOnly(IEnumerable<JsonElement> branches)
    {
        var names = new List<string>();
        foreach (var branch in branches)
        {
            if (branch.ValueKind != JsonValueKind.Object || branch.EnumerateObject().Any(p => p.Name != "required")
                || !branch.TryGetProperty("required", out var required)) return null;
            names.AddRange(required.EnumerateArray().Select(item => item.GetString()!));
        }
        return string.Join(", ", names);
    }

    /** JSON equality with numbers compared by value, so 1 equals 1.0. */
    private static bool JsonEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number) return a.GetDouble() == b.GetDouble();
        if (a.ValueKind != b.ValueKind) return false;
        return a.ValueKind switch
        {
            JsonValueKind.String => a.GetString() == b.GetString(),
            JsonValueKind.Array => a.GetArrayLength() == b.GetArrayLength()
                && a.EnumerateArray().Zip(b.EnumerateArray()).All(pair => JsonEquals(pair.First, pair.Second)),
            JsonValueKind.Object => a.EnumerateObject().Count() == b.EnumerateObject().Count()
                && a.EnumerateObject().All(p => b.TryGetProperty(p.Name, out var other) && JsonEquals(p.Value, other)),
            _ => true,
        };
    }
}
