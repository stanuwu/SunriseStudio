using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Packages.Registry;

/** One field type (modelled on Bungie's tgft table); Id is its index in the registry's type list. */
public sealed record RegistryFieldType(int Id, string Name, int Size, int Align, string Kind, uint? GameType = null);

/**
 * One field of a class. Optional members are null when absent; Extra keeps seed members this
 * reader does not model (meta, variant) so a round trip loses nothing.
 */
public sealed class RegistryField
{
    public required string Name { get; set; }
    public uint? NameHash { get; set; }
    public required int Offset { get; set; }
    public required int Type { get; set; }
    public uint? Element { get; set; }
    public string? ElementScalar { get; set; }
    public uint? Struct { get; set; }
    public List<uint>? Targets { get; set; }
    public int? Enum { get; set; }
    public string? ArrayPath { get; set; }
    public int? Count { get; set; }
    public int? Length { get; set; }
    public bool? Key { get; set; }
    public required string Confidence { get; set; }
    public List<JsonNode>? Alternates { get; set; }
    public List<string>? Refs { get; set; }
    public JsonObject? Extra { get; set; }
}

/** One class or element struct. */
public sealed class RegistryClass
{
    public required uint Class { get; init; }
    public required string Name { get; set; }
    public string? NameSource { get; set; }
    public List<string>? OtherNames { get; set; }
    public string? TftType { get; set; }
    public required string Kind { get; set; }
    public int? Size { get; set; }
    public int? Stride { get; set; }
    public int? Align { get; set; }
    public uint? Parent { get; set; }
    public required string Source { get; set; }
    public string? KeyField { get; set; }
    public bool? PokeSafe { get; set; }
    /** When a running game shows a change: 1 Sunrise rebuilds its data, 2 a patch at once, 3 on the next spawn; null needs a restart. */
    public int? LiveTier { get; set; }
    /** Browser group, such as "Map" or "Investment / Table"; null means Other. */
    public string? Category { get; set; }
    public List<string>? Pins { get; set; }
    public string? Notes { get; set; }
    public List<RegistryField> Fields { get; set; } = [];
}

/** The tag schema registry (sunrise-tag-registry v1): one file per game build. */
public sealed class TagRegistry
{
    public const string FormatName = "sunrise-tag-registry";
    public const int FormatVersion = 1;

    public string? Layer { get; set; }
    public required string BuildId { get; set; }
    public string? ExecutableSha256 { get; set; }
    public uint ArrayMarker { get; set; } = TagArray.Marker;
    public List<RegistryFieldType> FieldTypes { get; set; } = [];
    public List<JsonNode> Enums { get; set; } = [];
    public List<RegistryClass> Classes { get; set; } = [];
    public SortedDictionary<uint, string> Names { get; set; } = [];
    public List<uint>? ExternalClasses { get; set; }

    /** @return The id of the named field type; throws when the table has none. */
    public int TypeId(string name) =>
        FieldTypes.FirstOrDefault(x => x.Name == name)?.Id ?? throw new InvalidDataException($"The registry has no field type \"{name}\".");

    /** @return The field type with this id; registry-gen reads it. Throws when the table has none. */
    public RegistryFieldType Type(int id) =>
        FieldTypes.FirstOrDefault(x => x.Id == id) ?? throw new InvalidDataException($"The registry has no field type {id}.");

    public static TagRegistry Load(string path) => Parse(File.ReadAllBytes(path));

    /** Loads a registry for one game build; class ids differ between builds, so another build's registry is refused. */
    public static TagRegistry Load(string path, string expectedBuildId)
    {
        var registry = Load(path);
        if (registry.BuildId != expectedBuildId)
            throw new InvalidDataException($"{Path.GetFileName(path)} is the tag registry for build {registry.BuildId}; this game is build {expectedBuildId}.");
        return registry;
    }

    /** Reads a registry; a wrong format or version throws InvalidDataException. */
    public static TagRegistry Parse(ReadOnlySpan<byte> json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json.ToArray()); }
        catch (JsonException error) { throw new InvalidDataException("The registry is not valid JSON: " + error.Message, error); }
        if (root is not JsonObject top || (string?)top["format"] != FormatName || (int?)top["version"] != FormatVersion)
            throw new InvalidDataException($"The registry is not {FormatName} version {FormatVersion}.");
        try
        {
            var build = (JsonObject)top["build"]!;
            return new()
            {
                Layer = (string?)top["layer"],
                BuildId = (string)build["id"]!,
                ExecutableSha256 = (string?)build["executable_sha256"],
                ArrayMarker = Hex(build["array_marker"]),
                FieldTypes = ((JsonArray)top["field_types"]!).Select(x => new RegistryFieldType((int)x!["id"]!, (string)x["name"]!,
                    (int)x["size"]!, (int)x["align"]!, (string)x["kind"]!, x["game_type"] is { } g ? Hex(g) : null)).ToList(),
                Enums = ((JsonArray)top["enums"]!).Select(x => x!.DeepClone()).ToList(),
                Classes = ((JsonArray)top["classes"]!).Select(x => ReadClass((JsonObject)x!)).ToList(),
                Names = new(((JsonObject)top["names"]!).ToDictionary(x => Hex(JsonValue.Create(x.Key)), x => (string)x.Value!)),
                ExternalClasses = top["external_classes"] is JsonArray external ? external.Select(Hex).ToList() : null,
            };
        }
        catch (Exception error) when (error is InvalidCastException or NullReferenceException or FormatException or InvalidOperationException)
        {
            throw new InvalidDataException("The registry has the wrong shape: " + error.Message, error);
        }
    }

    private static RegistryClass ReadClass(JsonObject x) => new()
    {
        Class = Hex(x["class"]),
        Name = (string)x["name"]!,
        NameSource = (string?)x["name_source"],
        OtherNames = Strings(x["other_names"]),
        TftType = (string?)x["tft_type"],
        Kind = (string)x["kind"]!,
        Size = (int?)x["size"],
        Stride = (int?)x["stride"],
        Align = (int?)x["align"],
        Parent = x["parent"] is { } parent ? Hex(parent) : null,
        Source = (string)x["source"]!,
        KeyField = (string?)x["key_field"],
        PokeSafe = (bool?)x["poke_safe"],
        LiveTier = (int?)x["live_tier"],
        Category = (string?)x["category"],
        Pins = Strings(x["pins"]),
        Notes = (string?)x["notes"],
        Fields = ((JsonArray)x["fields"]!).Select(f => ReadField((JsonObject)f!)).ToList(),
    };

    /** Members this model names; anything else on a field is kept in Extra. */
    private static readonly HashSet<string> FieldKeys = ["name", "name_hash", "offset", "type", "element", "element_scalar", "struct",
        "targets", "enum", "array_path", "count", "length", "key", "confidence", "alternates", "refs"];

    private static RegistryField ReadField(JsonObject x)
    {
        var extra = new JsonObject();
        foreach (var (key, value) in x)
            if (!FieldKeys.Contains(key)) extra[key] = value?.DeepClone();
        return new()
        {
            Name = (string)x["name"]!,
            NameHash = x["name_hash"] is { } hash ? Hex(hash) : null,
            Offset = (int)x["offset"]!,
            Type = (int)x["type"]!,
            Element = x["element"] is { } element ? Hex(element) : null,
            ElementScalar = (string?)x["element_scalar"],
            Struct = x["struct"] is { } structure ? Hex(structure) : null,
            Targets = x["targets"] is JsonArray targets ? targets.Select(Hex).ToList() : null,
            Enum = (int?)x["enum"],
            ArrayPath = (string?)x["array_path"],
            Count = (int?)x["count"],
            Length = (int?)x["length"],
            Key = (bool?)x["key"],
            Confidence = (string)x["confidence"]!,
            Alternates = x["alternates"] is JsonArray alternates ? alternates.Select(a => a!.DeepClone()).ToList() : null,
            Refs = Strings(x["refs"]),
            Extra = extra.Count == 0 ? null : extra,
        };
    }

    private static List<string>? Strings(JsonNode? node) => node is JsonArray array ? array.Select(x => (string)x!).ToList() : null;

    private static uint Hex(JsonNode? node)
    {
        string text = (string?)node ?? throw new FormatException("Missing hex value.");
        if (!text.StartsWith("0x", StringComparison.Ordinal)) throw new FormatException($"Expected 0x hex, got \"{text}\".");
        return uint.Parse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }

    /** Writes the deterministic file form: keys sorted, two-space indent, LF, trailing newline. */
    public byte[] ToJson()
    {
        var top = new JsonObject
        {
            ["build"] = StableJson.Sorted(new JsonObject
            {
                ["array_marker"] = StableJson.HexText(ArrayMarker),
                ["executable_sha256"] = ExecutableSha256,
                ["id"] = BuildId,
            }),
            ["classes"] = new JsonArray(Classes.OrderBy(x => x.Class).Select(x => (JsonNode)WriteClass(x)).ToArray()),
            ["enums"] = new JsonArray(Enums.Select(x => StableJson.Sorted(x.DeepClone())).ToArray()),
            ["field_types"] = new JsonArray(FieldTypes.OrderBy(x => x.Id).Select(x => (JsonNode)StableJson.Sorted(new JsonObject
            {
                ["align"] = x.Align, ["id"] = x.Id, ["kind"] = x.Kind, ["name"] = x.Name, ["size"] = x.Size,
                ["game_type"] = x.GameType is { } game ? StableJson.HexText(game) : null,
            })).ToArray()),
            ["format"] = FormatName,
            ["names"] = new JsonObject(Names.Select(x => KeyValuePair.Create(StableJson.HexText(x.Key), (JsonNode?)x.Value))),
            ["version"] = FormatVersion,
        };
        if (Layer is not null) top["layer"] = Layer;
        if (ExternalClasses is not null) top["external_classes"] = new JsonArray(ExternalClasses.Order().Select(x => (JsonNode)StableJson.HexText(x)).ToArray());
        return StableJson.Write(StableJson.Sorted(top));
    }

    private static JsonObject WriteClass(RegistryClass x) => StableJson.Sorted(new JsonObject
    {
        ["class"] = StableJson.HexText(x.Class),
        ["name"] = x.Name,
        ["name_source"] = x.NameSource,
        ["other_names"] = x.OtherNames is null ? null : new JsonArray(x.OtherNames.Select(n => (JsonNode)n).ToArray()),
        ["tft_type"] = x.TftType,
        ["kind"] = x.Kind,
        ["size"] = x.Size,
        ["stride"] = x.Stride,
        ["align"] = x.Align,
        ["parent"] = x.Parent is { } parent ? StableJson.HexText(parent) : null,
        ["source"] = x.Source,
        ["key_field"] = x.KeyField,
        ["poke_safe"] = x.PokeSafe,
        ["live_tier"] = x.LiveTier,
        ["category"] = x.Category,
        ["pins"] = x.Pins is null ? null : new JsonArray(x.Pins.Select(n => (JsonNode)n).ToArray()),
        ["notes"] = x.Notes,
        ["fields"] = new JsonArray(x.Fields.OrderBy(f => f.Offset).ThenBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => (JsonNode)WriteField(f)).ToArray()),
    });

    private static JsonObject WriteField(RegistryField x)
    {
        var node = new JsonObject
        {
            ["name"] = x.Name,
            ["name_hash"] = x.NameHash is { } hash ? StableJson.HexText(hash) : null,
            ["offset"] = x.Offset,
            ["type"] = x.Type,
            ["element"] = x.Element is { } element ? StableJson.HexText(element) : null,
            ["element_scalar"] = x.ElementScalar,
            ["struct"] = x.Struct is { } structure ? StableJson.HexText(structure) : null,
            ["targets"] = x.Targets is null ? null : new JsonArray(x.Targets.Select(t => (JsonNode)StableJson.HexText(t)).ToArray()),
            ["enum"] = x.Enum,
            ["array_path"] = x.ArrayPath,
            ["count"] = x.Count,
            ["length"] = x.Length,
            ["key"] = x.Key,
            ["confidence"] = x.Confidence,
            ["alternates"] = x.Alternates is null ? null : new JsonArray(x.Alternates.Select(a => a.DeepClone()).ToArray()),
            ["refs"] = x.Refs is null ? null : new JsonArray(x.Refs.Select(r => (JsonNode)r).ToArray()),
        };
        if (x.Extra is not null)
            foreach (var (key, value) in x.Extra) node[key] = value?.DeepClone();
        return StableJson.Sorted(node);
    }
}
