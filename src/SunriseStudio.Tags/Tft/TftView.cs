using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Packages;

namespace SunriseStudio.Tags.Tft;

/**
 * Tag bytes as named fields, and back: a struct is an object keyed by field name, an array a list
 * of rows, a pointer what it names. `@class`, `@rows` and `@unknown` are the keys that are not fields.
 */
internal static class TftView
{
    public const string ClassKey = "@class", RowsKey = "@rows", UnknownKey = "@unknown";

    /** The bytes hold something a view cannot state: an alias, an untyped block, a parse problem. */
    private sealed class NotViewable(string reason) : Exception(reason);

    /** @return The key of each field: its name, or `name@offset` when two fields of the class share a name. */
    public static Dictionary<RegistryField, string> Keys(RegistryClass structure)
    {
        var repeated = structure.Fields.GroupBy(x => x.Name).Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet();
        return structure.Fields.ToDictionary(x => x, x => repeated.Contains(x.Name) ? $"{x.Name}@{x.Offset}" : x.Name);
    }

    /**
     * @param references Symbolic references by data offset; a field that holds one shows its target.
     * @param used Receives the offsets of the references the view shows.
     * @return The root struct's view, or null with the reason when the bytes cannot be stated as fields.
     */
    public static JsonObject? Build(byte[] data, uint classId, RegistryIndex schema, IReadOnlyDictionary<int, TftReference> references,
        HashSet<int> used, out string? reason)
    {
        reason = null;
        var document = TagDocument.Parse(data, classId, schema);
        if (document.Problems.Count > 0) { reason = "parse problem: " + document.Problems[0]; return null; }
        try { return new Builder(data, schema, references, used).Struct(document.Root, isRoot: true); }
        catch (NotViewable error) { reason = error.Message; used.Clear(); return null; }
    }

    private sealed class Builder(byte[] data, RegistryIndex schema, IReadOnlyDictionary<int, TftReference> references, HashSet<int> used)
    {
        public JsonObject Struct(TagNode node, bool isRoot = false)
        {
            var structure = schema.Class(node.ClassId) ?? throw new NotViewable($"class 0x{node.ClassId:X8} is not in the layout");
            if (structure.Size is not { } size || node.Length != size) throw new NotViewable($"struct 0x{node.ClassId:X8} at {node.Offset} is cut short");
            var keys = Keys(structure);
            var covered = new bool[size];
            var result = new JsonObject();
            foreach (var field in structure.Fields.OrderBy(x => x.Offset))
            {
                int width = schema.FieldSize(field);
                if (width <= 0) continue;
                if (field.Offset < 0 || field.Offset + width > size) throw new NotViewable($"field {field.Name} runs past its class");
                System.Array.Fill(covered, true, field.Offset, width);
                var children = node.Children.Where(x => ReferenceEquals(x.Field, field)).ToList();
                result[keys[field]] = Field(field, node.Offset + field.Offset, width, children);
            }
            // The writer owns the root's size word.
            if (isRoot && size >= TagView.SizeWordSize) System.Array.Fill(covered, true, 0, TagView.SizeWordSize);
            var unknown = new JsonObject();
            for (int at = 0; at < size; at++)
            {
                if (covered[at] || data[node.Offset + at] == 0) continue;
                int end = at;
                while (end < size && !covered[end]) end++;
                while (data[node.Offset + end - 1] == 0) end--;
                unknown[at.ToString(CultureInfo.InvariantCulture)] = Convert.ToHexStringLower(data, node.Offset + at, end - at);
                at = end;
            }
            if (unknown.Count > 0) result[UnknownKey] = unknown;
            return result;
        }

        private JsonNode? Field(RegistryField field, int at, int width, List<TagNode> children)
        {
            string type = schema.TypeName(field);
            switch (type)
            {
                case "array":
                    if (children.SingleOrDefault(x => x.Kind == TagNodeKind.Array) is { } array) return Rows(array, field);
                    return Zero(at, TagArray.FieldSize) ? null : throw new NotViewable($"array {field.Name} at {at} was not followed");
                case "relptr" or "blockptr":
                    if (children.SingleOrDefault() is { } target)
                        return target.Kind switch
                        {
                            TagNodeKind.Array => Rows(target, field),
                            TagNodeKind.Object when !target.Alias && target.ClassId != SInlineString.Class && target.Children.Count == 1 =>
                                Object(target),
                            _ => throw new NotViewable($"pointer {field.Name} at {at} names a {target.Kind} a view cannot hold"),
                        };
                    return Zero(at, 8) ? null : throw new NotViewable($"pointer {field.Name} at {at} was not followed");
                case "string_ptr" or "inline_string":
                    if (children.SingleOrDefault(x => x.Kind == TagNodeKind.String) is { } text) return Text(text);
                    return Zero(at, 8) ? null : throw new NotViewable($"string {field.Name} at {at} was not followed");
                case "tagref":
                    return Reference(at, TftReferenceKind.TagRef) ?? Hex(at, 4);
                case "wideref":
                    return Reference(at, TftReferenceKind.WideRef) ?? Hex(at, 8);
                case "tagptr" when width == Unsafe.SizeOf<TagOffsetPointer>():
                    var pointer = TagOffsetPointer.Read(data, at);
                    return new JsonObject
                    {
                        ["tag"] = Reference(at, TftReferenceKind.TagRef) ?? Hex(at, 4),
                        ["word"] = pointer.Word,
                        ["offset"] = pointer.Offset,
                    };
                case "struct" when children.SingleOrDefault(x => x.Kind == TagNodeKind.Struct) is { } nested:
                    return Struct(nested);
                case "inline_array" when children.Count > 0 && children.All(x => x.Kind == TagNodeKind.Struct):
                    return new JsonArray(children.Select(x => (JsonNode)Struct(x)).ToArray());
            }
            if (ScalarTypes.Integers.Contains(type) && width == schema.TypeSize(field)) return Integer(type, at);
            if (ScalarTypes.Floats.Contains(type) && width == schema.TypeSize(field)) return Float(at, width);
            if (ScalarTypes.Vectors.Contains(type) && width % 4 == 0)
                return new JsonArray(Enumerable.Range(0, width / 4).Select(i => Float(at + i * 4, 4)).ToArray());
            return Raw(at, width);
        }

        private JsonNode Rows(TagNode array, RegistryField field)
        {
            if (array.Alias || array.Stride <= 0) throw new NotViewable($"array {field.Name} at {array.Offset} is shared or has no element class");
            var rows = new JsonArray(array.Children.Select(x => (JsonNode)Struct(x)).ToArray());
            if (field.Element == array.ClassId && schema.TypeName(field) == "array") return rows;
            return new JsonObject { [ClassKey] = StableJson.HexText(array.ClassId), [RowsKey] = rows };
        }

        private JsonObject Object(TagNode target)
        {
            var body = Struct(target.Children[0]);
            body[ClassKey] = StableJson.HexText(target.ClassId);
            return body;
        }

        private JsonNode Text(TagNode text)
        {
            var bytes = data.AsSpan(text.Offset, text.Length - 1);
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { throw new NotViewable($"string at {text.Offset} is not UTF-8"); }
        }

        private JsonNode? Reference(int at, TftReferenceKind kind)
        {
            if (!references.TryGetValue(at, out var reference) || reference.Kind != kind || reference.Flags != 0) return null;
            used.Add(at);
            return reference.Target;
        }

        private bool Zero(int at, int width) => !data.AsSpan(at, width).ContainsAnyExcept((byte)0);

        private JsonNode Integer(string type, int at) => type switch
        {
            "u8" or "bool8" => data[at],
            "i8" => (sbyte)data[at],
            "u16" => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)),
            "i16" => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(at)),
            "u32" or "bool32" => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at)),
            "i32" => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at)),
            "u64" => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(at)),
            _ => BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(at)),
        };

        private JsonNode Float(int at, int width) => FloatValue(data.AsSpan(at, width));

        private JsonNode Hex(int at, int width) => HexValue(data.AsSpan(at, width));

        private JsonNode Raw(int at, int width) => width is 1 or 2 or 4 or 8 ? HexValue(data.AsSpan(at, width)) : Convert.ToHexStringLower(data, at, width);
    }

    /** A finite float that is not -0 is a JSON number in its shortest exact form; anything else is its bits as hex. */
    public static JsonNode FloatValue(ReadOnlySpan<byte> bytes)
    {
        string? text = bytes.Length switch
        {
            2 when BinaryPrimitives.ReadHalfLittleEndian(bytes) is var h && Half.IsFinite(h) && !(h == Half.Zero && Half.IsNegative(h)) =>
                h.ToString("R", CultureInfo.InvariantCulture),
            4 when BinaryPrimitives.ReadSingleLittleEndian(bytes) is var f && float.IsFinite(f) && !(f == 0 && float.IsNegative(f)) =>
                f.ToString("R", CultureInfo.InvariantCulture),
            8 when BinaryPrimitives.ReadDoubleLittleEndian(bytes) is var d && double.IsFinite(d) && !(d == 0 && double.IsNegative(d)) =>
                d.ToString("R", CultureInfo.InvariantCulture),
            _ => null,
        };
        return text is null ? HexValue(bytes) : JsonNode.Parse(text)!;
    }

    /** An integer of 1, 2, 4 or 8 bytes as `0x` and its uppercase hex digits. */
    public static JsonNode HexValue(ReadOnlySpan<byte> bytes)
    {
        ulong value = bytes.Length switch
        {
            1 => bytes[0], 2 => BinaryPrimitives.ReadUInt16LittleEndian(bytes), 4 => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            _ => BinaryPrimitives.ReadUInt64LittleEndian(bytes),
        };
        return "0x" + value.ToString("X" + (bytes.Length * 2), CultureInfo.InvariantCulture);
    }

    /**
     * Turns a view back into tag bytes. Strict mode refuses anything the layout does not describe;
     * upgrade mode drops it and lists the path in Dropped.
     * @param previousType The type a field had in the layout the view was made with; upgrade mode only.
     * @param previousOffset The offset a field had in that layout; upgrade mode carries a renamed field by it.
     */
    public sealed class Rebuild(RegistryIndex schema, bool strict, Func<uint, string, string?>? previousType = null,
        Func<uint, int?>? previousSize = null, Func<uint, string, int?>? previousOffset = null)
    {
        private readonly List<(TagBlueprint Owner, int At, TftReferenceKind Kind, string Target)> pending = [];

        public List<string> Dropped { get; } = [];

        /** @return The tag bytes; `references` receives every symbolic reference the view holds. */
        public byte[] Build(JsonObject view, uint classId, List<TftReference> references)
        {
            var root = New(classId, view, "");
            var placed = new Dictionary<TagBlueprint, int>();
            byte[] data = CanonicalTagWriter.Write(root, schema, placed);
            foreach (var (owner, at, kind, target) in pending)
                references.Add(new(placed[owner] + at, kind, 0, target));
            return data;
        }

        private TagBlueprint New(uint classId, JsonObject view, string path)
        {
            var structure = schema.Class(classId) ?? throw new InvalidDataException($"{Where(path)}: class 0x{classId:X8} is not in the layout.");
            var blueprint = new TagBlueprint
            {
                ClassId = classId, Bytes = new byte[structure.Size ?? throw new InvalidDataException($"{Where(path)}: class 0x{classId:X8} has no size.")],
            };
            Fill(classId, view, blueprint, 0, path);
            return blueprint;
        }

        private void Fill(uint classId, JsonObject view, TagBlueprint owner, int start, string path)
        {
            var structure = schema.Class(classId)!;
            int size = structure.Size!.Value;
            var keys = Keys(structure);
            var byKey = keys.ToDictionary(x => x.Value, x => x.Key);
            var renamed = Renamed(classId, view, keys, byKey);
            foreach (var (key, _) in view)
                if (key.StartsWith('@') ? key is not (ClassKey or UnknownKey) : !byKey.ContainsKey(key) && !renamed.ContainsValue(key))
                    Refuse($"{Join(path, key)}: class 0x{classId:X8} has no such field");
            string Source(RegistryField field) => renamed.GetValueOrDefault(keys[field], keys[field]);
            bool Written(RegistryField field) => view[Source(field)] is not null;
            // A field after this one that kept its offset is written later, so its view value lands on top.
            bool Later(RegistryField field, RegistryField after) => field.Offset > after.Offset && previousOffset?.Invoke(classId, Source(field)) == field.Offset;
            if (view[UnknownKey] is JsonObject unknown) Unknown(classId, unknown, owner, start, size, structure, path, Written);
            foreach (var field in structure.Fields.OrderBy(x => x.Offset))
            {
                string key = keys[field], source = renamed.GetValueOrDefault(key, key);
                if (view[source] is not { } value) continue;
                int width = schema.FieldSize(field);
                if (width <= 0) continue;
                string fieldPath = Join(path, key);
                string type = schema.TypeName(field);
                if (!strict && previousType?.Invoke(classId, source) is { } before && before != type && !Convertible(before, type, value))
                {
                    if (!OldBytes(classId, structure, size, field, source, before, value, owner, start, fieldPath,
                            f => Written(f) && !Later(f, field)))
                        Dropped.Add($"{Where(fieldPath)}: type changed from {before} to {type}");
                    continue;
                }
                try { Encode(field, type, width, value, owner, start + field.Offset, fieldPath); }
                catch (InvalidDataException error) when (!strict) { Dropped.Add(error.Message); }
            }
        }

        /**
         * Writes a plain value in its old type at its old offset, when the class kept its size and no
         * conflicting field shares those bytes. This keeps a vector the registry split, or bytes it now types.
         * @param conflicts True for a field whose view value this write would overwrite.
         * @return False when the value cannot be kept that way.
         */
        private bool OldBytes(uint classId, RegistryClass structure, int size, RegistryField field, string source, string before,
            JsonNode value, TagBlueprint owner, int start, string path, Func<RegistryField, bool> conflicts)
        {
            if (previousSize?.Invoke(classId) != size || previousOffset?.Invoke(classId, source) != field.Offset
                || schema.Registry.FieldTypes.FirstOrDefault(x => x.Name == before) is not { } old)
                return false;
            // Opaque bytes are hex text, so their width is the text's.
            int width = ScalarTypes.Integers.Contains(before) || ScalarTypes.Floats.Contains(before) || ScalarTypes.Vectors.Contains(before) ? old.Size
                : old.Kind == "opaque" && value is JsonValue text && text.TryGetValue(out string? hex)
                    ? (hex.StartsWith("0x", StringComparison.Ordinal) ? hex.Length - 2 : hex.Length) / 2
                : 0;
            if (width <= 0 || field.Offset + width > size) return false;
            int end = field.Offset + width;
            if (structure.Fields.Any(f => f != field && conflicts(f) && schema.FieldSize(f) is > 0 and var w && f.Offset < end && field.Offset < f.Offset + w))
                return false;
            var stand = new RegistryField { Name = field.Name, Offset = field.Offset, Type = old.Id, Confidence = field.Confidence };
            try { Encode(stand, before, width, value, owner, start + field.Offset, path); }
            catch (InvalidDataException) { return false; }
            return true;
        }

        /** @return New key to old key, for each view key the layout renamed: one new field at its old offset that the view does not name. */
        private Dictionary<string, string> Renamed(uint classId, JsonObject view, Dictionary<RegistryField, string> keys,
            Dictionary<string, RegistryField> byKey)
        {
            var renamed = new Dictionary<string, string>();
            if (strict || previousOffset is null) return renamed;
            foreach (var (key, _) in view)
            {
                if (key.StartsWith('@') || byKey.ContainsKey(key) || previousOffset(classId, key) is not { } offset) continue;
                if (keys.Where(x => x.Key.Offset == offset && view[x.Value] is null).ToList() is [var only]) renamed.TryAdd(only.Value, key);
            }
            return renamed;
        }

        /** @param written True for a field the view gives a value; a new field the view leaves empty keeps the old bytes. */
        private void Unknown(uint classId, JsonObject unknown, TagBlueprint owner, int start, int size, RegistryClass structure, string path,
            Func<RegistryField, bool> written)
        {
            bool sameSize = strict || previousSize?.Invoke(classId) == size;
            foreach (var (key, value) in unknown)
            {
                byte[] bytes = Bytes(value, Join(path, UnknownKey));
                if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int offset) || offset + bytes.Length > size)
                {
                    Refuse($"{Join(path, UnknownKey)}.{key}: bytes lie outside the class");
                    continue;
                }
                bool overlaps = structure.Fields.Any(f => schema.FieldSize(f) is > 0 and var w && f.Offset < offset + bytes.Length && offset < f.Offset + w
                    && written(f));
                if (!strict && (!sameSize || overlaps))
                {
                    Dropped.Add($"{Join(path, UnknownKey)}.{key}: the class changed, so bytes no field covers cannot be placed");
                    continue;
                }
                bytes.CopyTo(owner.Bytes, start + offset);
            }
        }

        private void Encode(RegistryField field, string type, int width, JsonNode value, TagBlueprint owner, int at, string path)
        {
            var bytes = owner.Bytes.AsSpan(at, width);
            switch (type)
            {
                case "array":
                    owner.Links.Add(ArrayLink(field, value, at, path));
                    return;
                case "relptr" or "blockptr":
                    if (value is JsonArray || value is JsonObject o && o.ContainsKey(RowsKey))
                    {
                        if (at < TagArray.RelativeOffset) throw new InvalidDataException($"{Where(path)}: a pointer onto an array needs a count 8 bytes before it.");
                        owner.Links.Add(ArrayLink(field, value, at - TagArray.RelativeOffset, path));
                        return;
                    }
                    var target = value as JsonObject ?? throw new InvalidDataException($"{Where(path)}: expected an object with {ClassKey}.");
                    uint classId = HexWord(target[ClassKey], Join(path, ClassKey));
                    var body = New(classId, target, path);
                    owner.Links.Add(new(at, TagLinkKind.Object, classId, [body]));
                    return;
                case "string_ptr" or "inline_string":
                    string text = (string?)(value as JsonValue) ?? throw new InvalidDataException($"{Where(path)}: expected a string.");
                    owner.Links.Add(new(at, TagLinkKind.String, 0, [], [.. Encoding.UTF8.GetBytes(text), 0]));
                    return;
                case "tagref":
                    Reference(value, TftReferenceKind.TagRef, owner, at, bytes, path);
                    return;
                case "wideref":
                    Reference(value, TftReferenceKind.WideRef, owner, at, bytes, path);
                    return;
                case "tagptr" when width == Unsafe.SizeOf<TagOffsetPointer>() && value is JsonObject pointer:
                    Reference(pointer["tag"] ?? "0x00000000", TftReferenceKind.TagRef, owner, at, bytes[..4], Join(path, "tag"));
                    // Word then offset, each written as soon as it parses.
                    ref var handle = ref TagView.Edit<TagOffsetPointer>(bytes);
                    handle = handle with { Word = (uint)Integer(pointer["word"], "u32", path) };
                    handle = handle with { Offset = (long)Integer(pointer["offset"], "i64", path) };
                    return;
                case "struct" when field.Struct is { } nested && value is JsonObject inner:
                    if (schema.Class(nested)?.Size != width) throw new InvalidDataException($"{Where(path)}: struct 0x{nested:X8} does not fit the field.");
                    Fill(nested, inner, owner, at, path);
                    return;
                case "inline_array" when field.Struct is { } element && field.Count is > 0 and int count && value is JsonArray items:
                    int stride = width / count;
                    if (schema.Class(element)?.Size != stride) throw new InvalidDataException($"{Where(path)}: element 0x{element:X8} does not fit the field.");
                    if (items.Count != count) Refuse($"{Where(path)}: {items.Count} elements where the field holds {count}");
                    for (int index = 0; index < Math.Min(count, items.Count); index++)
                        Fill(element, items[index] as JsonObject ?? throw new InvalidDataException($"{Where(path)}[{index}]: expected an object."),
                            owner, at + index * stride, $"{path}[{index}]");
                    return;
            }
            if (ScalarTypes.Integers.Contains(type) && width == schema.TypeSize(field))
            {
                ulong number = Integer(value, type, path);
                for (int index = 0; index < width; index++) bytes[index] = (byte)(number >> (index * 8));
                return;
            }
            if (ScalarTypes.Floats.Contains(type) && width == schema.TypeSize(field)) { FloatBytes(value, bytes, path); return; }
            if (ScalarTypes.Vectors.Contains(type) && width % 4 == 0 && value is JsonArray vector)
            {
                if (vector.Count != width / 4) throw new InvalidDataException($"{Where(path)}: expected {width / 4} numbers.");
                for (int index = 0; index < vector.Count; index++) FloatBytes(vector[index], bytes.Slice(index * 4, 4), $"{path}[{index}]");
                return;
            }
            RawBytes(value, bytes, path);
        }

        private TagLink ArrayLink(RegistryField field, JsonNode value, int at, string path)
        {
            uint? element = field.Element;
            JsonArray rows;
            if (value is JsonObject form)
            {
                element = HexWord(form[ClassKey], Join(path, ClassKey));
                rows = form[RowsKey] as JsonArray ?? throw new InvalidDataException($"{Where(path)}: {RowsKey} is not a list.");
            }
            else rows = value as JsonArray ?? throw new InvalidDataException($"{Where(path)}: expected a list of rows.");
            if (element is not { } elementClass) throw new InvalidDataException($"{Where(path)}: the element class is unknown; state it with {ClassKey}.");
            var children = rows.Select((row, index) => New(elementClass,
                row as JsonObject ?? throw new InvalidDataException($"{Where(path)}[{index}]: expected an object."), $"{path}[{index}]")).ToList();
            return new(at, TagLinkKind.Array, elementClass, children);
        }

        private void Reference(JsonNode value, TftReferenceKind kind, TagBlueprint owner, int at, Span<byte> bytes, string path)
        {
            string text = (string?)(value as JsonValue) ?? throw new InvalidDataException($"{Where(path)}: expected a hex value or a tag name.");
            if (text.StartsWith("0x", StringComparison.Ordinal)) { RawBytes(value, bytes, path); return; }
            try { TftNames.CheckTarget(text, kind); }
            catch (InvalidDataException error) { throw new InvalidDataException($"{Where(path)}: {error.Message}"); }
            if (kind == TftReferenceKind.WideRef) bytes.Clear();
            else BinaryPrimitives.WriteUInt32LittleEndian(bytes, TftFile.TagRefPlaceholder);
            pending.Add((owner, at, kind, text));
        }

        private void Refuse(string message)
        {
            if (strict) throw new InvalidDataException(message);
            Dropped.Add(message);
        }
    }

    /** A changed type keeps its value when both are integers (range checked on write), or both floats and the value is a number. */
    private static bool Convertible(string before, string after, JsonNode value) =>
        (ScalarTypes.Integers.Contains(before) && ScalarTypes.Integers.Contains(after))
        || (ScalarTypes.Floats.Contains(before) && ScalarTypes.Floats.Contains(after) && value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number);

    private static string Join(string path, string key) => path.Length == 0 ? key : path + "." + key;
    private static string Where(string path) => path.Length == 0 ? "(root)" : path;

    private static ulong Integer(JsonNode? value, string type, string path)
    {
        string text = value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number ? v.ToJsonString() : "";
        bool signed = type is "i8" or "i16" or "i32" or "i64";
        int bits = type switch { "u8" or "i8" or "bool8" => 8, "u16" or "i16" => 16, "u32" or "i32" or "bool32" => 32, _ => 64 };
        if (signed && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long s)
            && (bits == 64 || (s >= -(1L << (bits - 1)) && s < 1L << (bits - 1))))
            return unchecked((ulong)s);
        if (!signed && ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong u) && (bits == 64 || u < 1UL << bits))
            return u;
        throw new InvalidDataException($"{Where(path)}: expected a whole number that fits {type}.");
    }

    private static void FloatBytes(JsonNode? value, Span<byte> bytes, string path)
    {
        if (value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number)
        {
            string text = v.ToJsonString();
            bool finite = bytes.Length switch
            {
                2 => Half.Parse(text, CultureInfo.InvariantCulture) is var h && Write(h, bytes) && Half.IsFinite(h),
                4 => float.Parse(text, CultureInfo.InvariantCulture) is var f && Write(f, bytes) && float.IsFinite(f),
                _ => double.Parse(text, CultureInfo.InvariantCulture) is var d && Write(d, bytes) && double.IsFinite(d),
            };
            if (!finite) throw new InvalidDataException($"{Where(path)}: {text} does not fit the field.");
            return;
        }
        RawBytes(value, bytes, path);
    }

    private static bool Write(Half value, Span<byte> bytes) { BinaryPrimitives.WriteHalfLittleEndian(bytes, value); return true; }
    private static bool Write(float value, Span<byte> bytes) { BinaryPrimitives.WriteSingleLittleEndian(bytes, value); return true; }
    private static bool Write(double value, Span<byte> bytes) { BinaryPrimitives.WriteDoubleLittleEndian(bytes, value); return true; }

    /** Writes a `0x` integer for widths 1, 2, 4 and 8, or plain hex bytes for any other width. */
    private static void RawBytes(JsonNode? value, Span<byte> bytes, string path)
    {
        string text = (string?)(value as JsonValue) ?? throw new InvalidDataException($"{Where(path)}: expected a hex string.");
        if (bytes.Length is 1 or 2 or 4 or 8)
        {
            if (text.Length != 2 + bytes.Length * 2 || !text.StartsWith("0x", StringComparison.Ordinal)
                || !ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong number))
                throw new InvalidDataException($"{Where(path)}: expected 0x and {bytes.Length * 2} hex digits.");
            for (int index = 0; index < bytes.Length; index++) bytes[index] = (byte)(number >> (index * 8));
            return;
        }
        byte[] raw = Bytes(value, path);
        if (raw.Length != bytes.Length) throw new InvalidDataException($"{Where(path)}: expected {bytes.Length} bytes of hex.");
        raw.CopyTo(bytes);
    }

    private static byte[] Bytes(JsonNode? value, string path)
    {
        string text = (string?)(value as JsonValue) ?? throw new InvalidDataException($"{Where(path)}: expected a hex string.");
        try { return Convert.FromHexString(text); }
        catch (FormatException) { throw new InvalidDataException($"{Where(path)}: \"{text}\" is not hex bytes."); }
    }

    private static uint HexWord(JsonNode? value, string path)
    {
        string text = (string?)(value as JsonValue) ?? throw new InvalidDataException($"{Where(path)}: expected a 0x class id.");
        return text.Length == 10 && text.StartsWith("0x", StringComparison.Ordinal)
            && uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint id)
            ? id : throw new InvalidDataException($"{Where(path)}: expected a 0x class id.");
    }
}
