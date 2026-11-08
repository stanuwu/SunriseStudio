using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Packages;

namespace SunriseStudio.Tags.Tft;

/**
 * The `.tft.json` text twin: a review and diff view of a `.tft` file, keys sorted. The tag is shown
 * as named fields when its layout describes every byte the rebuild cannot recreate; otherwise it is
 * stored as `bytes`. Import always gives back the exact `.tft` bytes.
 */
public static class TftText
{
    public const string FormatName = "sunrise-tft-text";
    public const int FormatVersion = 1;
    public const string FileExtension = ".tft.json";

    private static readonly HashSet<string> TopKeys =
        ["build", "bytes", "class", "fields", "format", "game_name", "info", "layout", "meta", "name", "refs", "tag_type", "type_info", "unknown", "version"];

    public static byte[] Export(TftFile file) => StableJson.Write(ToJson(file));

    public static TftFile Import(ReadOnlySpan<byte> json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json.ToArray()); }
        catch (JsonException error) { throw new InvalidDataException("The .tft.json file is not valid JSON: " + error.Message, error); }
        return FromJson(root as JsonObject ?? throw new InvalidDataException("The .tft.json file is not an object."));
    }

    /** @return The text form; it falls back to `bytes` whenever the field view would not give the file back exactly. */
    public static JsonObject ToJson(TftFile file) => ToJson(file, out _);

    /** @param fallback Why the file is shown as bytes; null when it is shown as fields. */
    public static JsonObject ToJson(TftFile file, out string? fallback)
    {
        fallback = file.Layout is null ? "no layout" : null;
        var header = new JsonObject
        {
            ["format"] = FormatName, ["version"] = FormatVersion, ["name"] = file.Name, ["tag_type"] = file.TagType, ["game_name"] = file.GameName,
            ["class"] = StableJson.HexText(file.ClassId), ["type_info"] = StableJson.HexText(file.TypeInfo),
            ["build"] = Convert.ToHexStringLower(file.BuildHash), ["layout"] = file.Layout?.ToJson(), ["info"] = file.Info,
            ["meta"] = file.Meta is null ? null : Convert.ToHexStringLower(file.Meta),
        };
        if (file.Layout is not null && View(file, file.Layout, out fallback) is { } view)
        {
            var candidate = (JsonObject)header.DeepClone();
            candidate["fields"] = view.Fields;
            candidate["refs"] = References(view.Others);
            candidate["unknown"] = view.Unknown;
            var sorted = StableJson.Sorted(candidate);
            if (Reproduces(sorted, file)) return sorted;
            fallback = "the fields do not give the file back";
        }
        header["bytes"] = Convert.ToHexStringLower(file.Data);
        header["refs"] = References(file.References);
        header["unknown"] = new JsonObject();
        return StableJson.Sorted(header);
    }

    private sealed record FieldView(JsonObject Fields, IReadOnlyList<TftReference> Others, JsonObject Unknown);

    private static FieldView? View(TftFile file, TftLayout layout, out string? reason)
    {
        var used = new HashSet<int>();
        var fields = TftView.Build(file.Data, file.ClassId, layout.Index, file.References.ToDictionary(x => x.Offset), used, out reason);
        if (fields is null) return null;
        byte[] rebuilt;
        try { rebuilt = new TftView.Rebuild(layout.Index, strict: true).Build(fields, file.ClassId, []); }
        catch (InvalidDataException error) { reason = "rebuild refused: " + error.Message; return null; }
        if (rebuilt.Length != file.Data.Length) { reason = $"the rebuild is {rebuilt.Length} bytes, the tag {file.Data.Length}"; return null; }
        var unknown = new JsonObject();
        for (int at = 0; at < rebuilt.Length; at++)
        {
            if (rebuilt[at] == file.Data[at]) continue;
            int end = at;
            while (end < rebuilt.Length && rebuilt[end] != file.Data[end]) end++;
            unknown[at.ToString(CultureInfo.InvariantCulture)] = Convert.ToHexStringLower(file.Data, at, end - at);
            at = end;
        }
        return new(fields, file.References.Where(x => !used.Contains(x.Offset)).ToList(), unknown);
    }

    private static bool Reproduces(JsonObject json, TftFile file)
    {
        try { return FromJson(json).Write().AsSpan().SequenceEqual(file.Write()); }
        catch (InvalidDataException) { return false; }
    }

    private static JsonArray References(IEnumerable<TftReference> references) => new(references.OrderBy(x => x.Offset)
        .Select(x => (JsonNode)StableJson.Sorted(new JsonObject
        {
            ["offset"] = x.Offset, ["kind"] = x.Kind == TftReferenceKind.WideRef ? "wideref" : "tagref", ["target"] = x.Target,
            ["flags"] = x.Flags == 0 ? null : x.Flags,
        })).ToArray());

    /** Reads the text form; a field the layout lacks, or a value its type cannot hold, throws. */
    public static TftFile FromJson(JsonObject json)
    {
        if ((string?)json["format"] != FormatName || (int?)json["version"] != FormatVersion)
            throw new InvalidDataException($"The file is not {FormatName} version {FormatVersion}.");
        if (json.Select(x => x.Key).FirstOrDefault(x => !TopKeys.Contains(x)) is { } extra)
            throw new InvalidDataException($"The .tft.json file has an unknown member \"{extra}\".");
        try
        {
            var layout = json["layout"] is JsonObject layoutJson ? TftLayout.FromJson(layoutJson, "") : null;
            uint classId = Word(json["class"]);
            var listed = ((JsonArray)json["refs"]!).Select(x => new TftReference((int)x!["offset"]!,
                (string)x["kind"]! switch { "tagref" => TftReferenceKind.TagRef, "wideref" => TftReferenceKind.WideRef, var k => throw new FormatException($"Unknown reference kind {k}.") },
                (byte?)x["flags"] ?? 0, (string)x["target"]!)).ToList();
            byte[] data;
            var references = new List<TftReference>();
            if (json["bytes"] is { } bytes)
            {
                if (json["fields"] is not null) throw new InvalidDataException("A .tft.json file holds bytes or fields, not both.");
                data = Convert.FromHexString((string)bytes!);
            }
            else
            {
                var fields = json["fields"] as JsonObject ?? throw new InvalidDataException("The .tft.json file has neither fields nor bytes.");
                var schema = (layout ?? throw new InvalidDataException("Fields need the layout that describes them.")).Index;
                data = new TftView.Rebuild(schema, strict: true).Build(fields, classId, references);
            }
            foreach (var (key, value) in (JsonObject)json["unknown"]!)
            {
                byte[] patch = Convert.FromHexString((string)value!);
                if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int offset) || offset + patch.Length > data.Length)
                    throw new InvalidDataException($"unknown.{key} lies outside the tag.");
                patch.CopyTo(data, offset);
            }
            references.AddRange(listed);
            if (references.Select(x => x.Offset).Distinct().Count() != references.Count)
                throw new InvalidDataException("Two references share one offset.");
            byte[] build = Convert.FromHexString((string)json["build"]!);
            return new TftFile
            {
                Name = (string)json["name"]!, TagType = (string)json["tag_type"]!, GameName = (string?)json["game_name"], ClassId = classId, TypeInfo = Word(json["type_info"]),
                BuildHash = build, Layout = layout, Data = data, References = references.OrderBy(x => x.Offset).ToList(),
                Info = (string?)json["info"], Meta = json["meta"] is { } meta ? Convert.FromHexString((string)meta!) : null,
            };
        }
        catch (Exception error) when (error is InvalidCastException or NullReferenceException or FormatException or InvalidOperationException)
        {
            throw new InvalidDataException("The .tft.json file has the wrong shape: " + error.Message, error);
        }
    }

    private static uint Word(JsonNode? node)
    {
        string text = (string?)node ?? throw new FormatException("Missing hex value.");
        return text.StartsWith("0x", StringComparison.Ordinal)
            ? uint.Parse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
            : throw new FormatException($"Expected 0x hex, got \"{text}\".");
    }
}
