using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SunriseStudio.Packages;

/** Deterministic JSON for checked-in files: sorted keys, two-space indent, LF, trailing newline. */
public static class StableJson
{
    /** Hex32 as the schema writes it: 0x and eight uppercase digits. */
    public static string HexText(uint value) => "0x" + value.ToString("X8", CultureInfo.InvariantCulture);

    /** Serializes with LF line ends so the file is the same on every platform. */
    public static byte[] Write(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
            node.WriteTo(writer);
        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    /** Drops null members and sorts keys ordinally, recursively, so output never depends on build order. */
    public static JsonObject Sorted(JsonNode node)
    {
        var source = (JsonObject)node;
        var result = new JsonObject();
        foreach (var (key, value) in source.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (value is null) continue;
            result[key] = value switch
            {
                JsonObject inner => Sorted(inner.DeepClone()),
                JsonArray array => new JsonArray(array.Select(item => item is JsonObject o ? (JsonNode)Sorted(o.DeepClone()) : item?.DeepClone()).ToArray()),
                _ => value.DeepClone(),
            };
        }
        return result;
    }
}
