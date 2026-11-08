using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using SunriseStudio.Tags.Document;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Packages;

namespace SunriseStudio.Tags.Tft;

/**
 * The registry subset one tag needs: the classes its bytes use, the classes they hold by value or
 * as array elements, and their field types and enums renumbered from zero. A `.tft` file embeds it
 * so the tag still decodes after the registry changes.
 */
public sealed class TftLayout
{
    /** Classes and fields carry no source or confidence in a layout; these fill the registry's required members. */
    private const string LayoutSource = "layout";
    private const int RowHeader = 16;

    private RegistryIndex? index;

    public required TagRegistry Registry { get; init; }
    public RegistryIndex Index => index ??= new(Registry);

    /** @return The subset of `schema` that decoding `data` as `classId` reaches. */
    public static TftLayout Extract(RegistryIndex schema, byte[] data, uint classId)
    {
        var document = TagDocument.Parse(data, classId, schema);
        var wanted = new SortedSet<uint>();
        var pending = new Stack<uint>();
        void Add(uint id)
        {
            if (schema.Class(id) is not null && wanted.Add(id)) pending.Push(id);
        }
        Add(classId);
        foreach (var node in document.OutOfLine) Add(node.ClassId);
        while (pending.TryPop(out uint id))
            foreach (var field in schema.Class(id)!.Fields)
            {
                if (field.Struct is { } nested) Add(nested);
                if (field.Element is { } element) Add(element);
            }
        var classes = wanted.Select(id => schema.Class(id)!).ToList();
        var fields = classes.SelectMany(x => x.Fields).ToList();
        var types = fields.Select(x => x.Type).Distinct().Order().ToList();
        var enums = fields.Where(x => x.Enum is not null).Select(x => x.Enum!.Value).Distinct().Order().ToList();
        var registry = new TagRegistry
        {
            BuildId = schema.Registry.BuildId,
            FieldTypes = types.Select((id, local) => schema.Registry.FieldTypes[id] with { Id = local, GameType = null }).ToList(),
            Enums = enums.Select(id => schema.Registry.Enums[id].DeepClone()).ToList(),
            Classes = classes.Select(x => new RegistryClass
            {
                Class = x.Class, Name = x.Name, Kind = LayoutSource, Source = LayoutSource, Size = x.Size, Align = x.Align,
                Fields = x.Fields.OrderBy(f => f.Offset).ThenBy(f => f.Name, StringComparer.Ordinal).Select(f => new RegistryField
                {
                    Name = f.Name, Offset = f.Offset, Type = types.IndexOf(f.Type), Element = f.Element, Struct = f.Struct, Count = f.Count,
                    Length = f.Length, Enum = f.Enum is { } e ? enums.IndexOf(e) : null, Key = f.Key, Confidence = LayoutSource,
                }).ToList(),
            }).ToList(),
        };
        return new() { Registry = registry };
    }

    /** The layout alone, with no other strings; two layouts are equal when these bytes are. */
    public byte[] LayoutBytes()
    {
        using var stream = new MemoryStream();
        Write(stream, new TftStrings(), []);
        return stream.ToArray();
    }

    /**
     * Writes the `layt` payload: `tgft`, `clss`, `flds`, `enms`, then `strs`. Every sub-table is
     * {id, u32 rows, u32 bytes, u32 0} and its rows. `extra` strings join the table after the layout's own.
     */
    internal void Write(Stream output, TftStrings strings, IEnumerable<string> extra)
    {
        foreach (var type in Registry.FieldTypes) { strings.Add(type.Name); strings.Add(type.Kind); }
        foreach (var structure in Registry.Classes) strings.Add(structure.Name);
        foreach (var field in Registry.Classes.SelectMany(x => x.Fields)) strings.Add(field.Name);
        var enumTexts = Registry.Enums.Select(x => StableJson.Sorted(x).ToJsonString()).ToList();
        foreach (string text in enumTexts) strings.Add(text);
        foreach (string text in extra) strings.Add(text);

        using var rows = new MemoryStream();
        foreach (var type in Registry.FieldTypes)
        {
            TftBytes.U32(rows, (uint)strings[type.Name]);
            TftBytes.U32(rows, (uint)type.Size);
            TftBytes.U32(rows, (uint)type.Align);
            TftBytes.U32(rows, (uint)strings[type.Kind]);
        }
        Table(output, "tgft", Registry.FieldTypes.Count, rows);
        int first = 0;
        foreach (var structure in Registry.Classes)
        {
            TftBytes.U32(rows, structure.Class);
            TftBytes.U32(rows, (uint)strings[structure.Name]);
            TftBytes.I32(rows, structure.Size ?? -1);
            TftBytes.I32(rows, structure.Align ?? -1);
            TftBytes.U32(rows, (uint)first);
            TftBytes.U32(rows, (uint)structure.Fields.Count);
            first += structure.Fields.Count;
        }
        Table(output, "clss", Registry.Classes.Count, rows);
        foreach (var field in Registry.Classes.SelectMany(x => x.Fields))
        {
            TftBytes.U32(rows, (uint)strings[field.Name]);
            TftBytes.I32(rows, field.Offset);
            TftBytes.U32(rows, (uint)field.Type);
            TftBytes.U32(rows, field.Element ?? 0);
            TftBytes.U32(rows, field.Struct ?? 0);
            TftBytes.I32(rows, field.Count ?? -1);
            TftBytes.I32(rows, field.Length ?? -1);
            TftBytes.I32(rows, field.Enum ?? -1);
            // Bit 0: the field states key; bit 1: its value.
            TftBytes.U32(rows, field.Key switch { null => 0u, false => 1u, true => 3u });
        }
        Table(output, "flds", first, rows);
        foreach (string text in enumTexts) TftBytes.U32(rows, (uint)strings[text]);
        Table(output, "enms", enumTexts.Count, rows);
        rows.Write(strings.ToArray());
        Table(output, "strs", strings.Count, rows);
    }

    private static void Table(Stream output, string id, int count, MemoryStream rows)
    {
        output.Write(Encoding.ASCII.GetBytes(id));
        TftBytes.U32(output, (uint)count);
        TftBytes.U32(output, (uint)rows.Length);
        TftBytes.U32(output, 0);
        rows.WriteTo(output);
        rows.SetLength(0);
    }

    /** Reads a `layt` payload. @param strings Receives the string table, which the `refs` and `want` chunks index too. */
    internal static TftLayout Read(ReadOnlySpan<byte> payload, string buildId, out byte[] strings)
    {
        var tables = new Dictionary<string, (int Count, byte[] Rows)>();
        int at = 0;
        foreach (string id in new[] { "tgft", "clss", "flds", "enms", "strs" })
        {
            if (at + RowHeader > payload.Length || Encoding.ASCII.GetString(payload.Slice(at, 4)) != id)
                throw new InvalidDataException($"The layout has no {id} table where one belongs.");
            int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload[(at + 4)..]));
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload[(at + 8)..]));
            if (BinaryPrimitives.ReadUInt32LittleEndian(payload[(at + 12)..]) != 0 || at + RowHeader + length > payload.Length)
                throw new InvalidDataException($"The layout's {id} table header is malformed.");
            tables[id] = (count, payload.Slice(at + RowHeader, length).ToArray());
            at += RowHeader + length;
        }
        if (at != payload.Length) throw new InvalidDataException("The layout has bytes after its string table.");
        strings = tables["strs"].Rows;
        var table = strings;
        string Text(uint offset) => TftStrings.At(table, offset);
        uint U(byte[] rows, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(rows.AsSpan(offset));
        int? Optional(byte[] rows, int offset) => (int)U(rows, offset) is var value && value == -1 ? null : value;
        uint? Id(byte[] rows, int offset) => U(rows, offset) is var value && value == 0 ? null : value;

        var (typeCount, typeRows) = Rows(tables, "tgft", 16);
        var types = Enumerable.Range(0, typeCount).Select(i => new RegistryFieldType(i, Text(U(typeRows, i * 16)),
            (int)U(typeRows, i * 16 + 4), (int)U(typeRows, i * 16 + 8), Text(U(typeRows, i * 16 + 12)))).ToList();
        var (fieldCount, fieldRows) = Rows(tables, "flds", 36);
        var fields = Enumerable.Range(0, fieldCount).Select(i =>
        {
            int row = i * 36;
            int type = (int)U(fieldRows, row + 8);
            if (type >= types.Count) throw new InvalidDataException($"Layout field {i} names type {type}, which the layout lacks.");
            return new RegistryField
            {
                Name = Text(U(fieldRows, row)), Offset = (int)U(fieldRows, row + 4), Type = type, Element = Id(fieldRows, row + 12),
                Struct = Id(fieldRows, row + 16), Count = Optional(fieldRows, row + 20), Length = Optional(fieldRows, row + 24),
                Enum = Optional(fieldRows, row + 28), Confidence = LayoutSource,
                Key = U(fieldRows, row + 32) switch
                {
                    0 => null, 1 => false, 3 => true, var flags => throw new InvalidDataException($"Layout field flags {flags} are unknown."),
                },
            };
        }).ToList();
        var (classCount, classRows) = Rows(tables, "clss", 24);
        int next = 0;
        var classes = new List<RegistryClass>();
        for (int i = 0; i < classCount; i++)
        {
            int row = i * 24;
            int first = (int)U(classRows, row + 16), count = (int)U(classRows, row + 20);
            if (first != next || first + count > fields.Count) throw new InvalidDataException("Layout classes do not own consecutive fields.");
            next += count;
            classes.Add(new()
            {
                Class = U(classRows, row), Name = Text(U(classRows, row + 4)), Kind = LayoutSource, Source = LayoutSource,
                Size = Optional(classRows, row + 8), Align = Optional(classRows, row + 12), Fields = fields.GetRange(first, count),
            });
        }
        if (next != fields.Count) throw new InvalidDataException("Layout fields belong to no class.");
        var (enumCount, enumRows) = Rows(tables, "enms", 4);
        var enums = Enumerable.Range(0, enumCount).Select(i => JsonNode.Parse(Text(U(enumRows, i * 4)))!).ToList();
        if (fields.Any(x => x.Enum is { } e && (e < 0 || e >= enums.Count))) throw new InvalidDataException("A layout field names a missing enum.");
        return new() { Registry = new TagRegistry { BuildId = buildId, FieldTypes = types, Classes = classes, Enums = enums } };
    }

    private static (int Count, byte[] Rows) Rows(Dictionary<string, (int Count, byte[] Rows)> tables, string id, int stride)
    {
        var (count, rows) = tables[id];
        if ((long)count * stride != rows.Length) throw new InvalidDataException($"The layout's {id} table is not {count} rows of {stride} bytes.");
        return (count, rows);
    }

    /** The text-twin form: field types, classes with their fields, and enums, in stored order. */
    public JsonObject ToJson() => new()
    {
        ["classes"] = new JsonArray(Registry.Classes.Select(x => (JsonNode)StableJson.Sorted(new JsonObject
        {
            ["align"] = x.Align, ["class"] = StableJson.HexText(x.Class), ["name"] = x.Name, ["size"] = x.Size,
            ["fields"] = new JsonArray(x.Fields.Select(f => (JsonNode)StableJson.Sorted(new JsonObject
            {
                ["name"] = f.Name, ["offset"] = f.Offset, ["type"] = f.Type,
                ["element"] = f.Element is { } element ? StableJson.HexText(element) : null,
                ["struct"] = f.Struct is { } structure ? StableJson.HexText(structure) : null,
                ["count"] = f.Count, ["length"] = f.Length, ["enum"] = f.Enum, ["key"] = f.Key,
            })).ToArray()),
        })).ToArray()),
        ["enums"] = new JsonArray(Registry.Enums.Select(x => (JsonNode)StableJson.Sorted(x.DeepClone())).ToArray()),
        ["field_types"] = new JsonArray(Registry.FieldTypes.Select(x => (JsonNode)StableJson.Sorted(new JsonObject
        {
            ["align"] = x.Align, ["id"] = x.Id, ["kind"] = x.Kind, ["name"] = x.Name, ["size"] = x.Size,
        })).ToArray()),
    };

    /** Reads the text-twin form back. */
    public static TftLayout FromJson(JsonObject json, string buildId)
    {
        try
        {
            var types = ((JsonArray)json["field_types"]!).Select((x, i) => (int)x!["id"]! == i
                ? new RegistryFieldType(i, (string)x["name"]!, (int)x["size"]!, (int)x["align"]!, (string)x["kind"]!)
                : throw new InvalidDataException("Layout field type ids are not their positions.")).ToList();
            var classes = ((JsonArray)json["classes"]!).Select(x => new RegistryClass
            {
                Class = Hex(x!["class"]), Name = (string)x["name"]!, Kind = LayoutSource, Source = LayoutSource,
                Size = (int?)x["size"], Align = (int?)x["align"],
                Fields = ((JsonArray)x["fields"]!).Select(f => new RegistryField
                {
                    Name = (string)f!["name"]!, Offset = (int)f["offset"]!, Type = (int)f["type"]!,
                    Element = f["element"] is { } element ? Hex(element) : null, Struct = f["struct"] is { } structure ? Hex(structure) : null,
                    Count = (int?)f["count"], Length = (int?)f["length"], Enum = (int?)f["enum"], Key = (bool?)f["key"], Confidence = LayoutSource,
                }).ToList(),
            }).ToList();
            if (classes.SelectMany(x => x.Fields).Any(f => f.Type < 0 || f.Type >= types.Count))
                throw new InvalidDataException("A layout field names a missing type.");
            var enums = ((JsonArray)json["enums"]!).Select(x => x!.DeepClone()).ToList();
            return new() { Registry = new TagRegistry { BuildId = buildId, FieldTypes = types, Classes = classes, Enums = enums } };
        }
        catch (Exception error) when (error is InvalidCastException or NullReferenceException or FormatException or InvalidOperationException)
        {
            throw new InvalidDataException("The layout has the wrong shape: " + error.Message, error);
        }
    }

    private static uint Hex(JsonNode? node)
    {
        string text = (string?)node ?? throw new FormatException("Missing hex value.");
        return text.StartsWith("0x", StringComparison.Ordinal)
            ? uint.Parse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
            : throw new FormatException($"Expected 0x hex, got \"{text}\".");
    }
}
