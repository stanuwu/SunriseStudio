using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Views;
using SunriseStudio.Packages.Registry;

namespace SunriseStudio.Tags.Editing;

/** What a row of the field tree is. */
public enum TagFieldKind
{
    /** A struct: the root, a nested member, an inline array element, an object's body or an array row. */
    Struct,
    Array,
    /** A pointer to an object; its one child is the object's body. */
    Object,
    String,
    /** A value with no children. */
    Value,
    /** Bytes inside a struct that no field covers. */
    Unknown,
}

/** How a value row is edited; None is read-only. */
public enum TagValueEdit { None, Integer, Float, Vector, TagRef, WideRef, Hash, Raw }

/**
 * One row of a tag shown by schema. Offset and Size are the bytes the row covers in the tag; for
 * an array they are its 16-byte descriptor. Children follow field order.
 */
public sealed class TagFieldRow
{
    public required string Name { get; init; }
    /** Dotted path from the root, with `[i]` for rows and elements; the edit and search key. */
    public required string Path { get; init; }
    public required TagFieldKind Kind { get; init; }
    /** Field type name from the registry; empty for structs and unknown bytes. */
    public string Type { get; init; } = "";
    public required int Offset { get; init; }
    public required int Size { get; init; }
    /** Class of a struct or object, or the element class of an array. */
    public uint ClassId { get; init; }
    public string Value { get; init; } = "";
    /** A name for the value: the tag a reference names, or the strings a hash may be. */
    public string Note { get; init; } = "";
    public TagValueEdit Edit { get; init; }
    /** The tag hash a reference holds; null for anything else or a null reference. */
    public uint? Reference { get; init; }
    /** Classes a reference field is known to name; empty when the registry lists none. */
    public IReadOnlyList<uint> Targets { get; init; } = [];
    /** Rows of an array; -1 when the array has no header yet. */
    public int Count { get; init; } = -1;
    public bool CanResize { get; init; }
    public List<TagFieldRow> Children { get; } = [];

    public IEnumerable<TagFieldRow> Walk()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var row in child.Walk()) yield return row;
    }
}

/** Builds the field tree of a parsed tag. It never throws on odd bytes; it shows them raw. */
public static class TagFieldTree
{
    private static readonly HashSet<string> Hashes = ["strhash", "schema_handle", "handle32", "resource", "absptr32"];

    /**
     * @param nameOf The display name of a tag, or null.
     * @param hashNames Strings that hash to a value, for strhash fields; may be empty.
     */
    public static TagFieldRow Build(TagDocument document, RegistryIndex schema, Func<uint, string?>? nameOf = null,
        Func<uint, IReadOnlyList<string>>? hashNames = null) =>
        new Builder(document, schema, nameOf, hashNames).Struct(document.Root, schema.Class(document.ClassId)?.Name ?? "root", "", isRoot: true);

    private sealed class Builder(TagDocument document, RegistryIndex schema, Func<uint, string?>? nameOf, Func<uint, IReadOnlyList<string>>? hashNames)
    {
        private readonly byte[] data = document.Bytes.ToArray();

        public TagFieldRow Struct(TagNode node, string name, string path, bool isRoot = false)
        {
            var structure = schema.Class(node.ClassId);
            var row = new TagFieldRow
            {
                Name = name, Path = path, Kind = TagFieldKind.Struct, Offset = node.Offset, Size = node.Length, ClassId = node.ClassId,
                Value = structure?.Name ?? $"0x{node.ClassId:X8}",
            };
            if (structure is null) return row;
            var covered = new bool[node.Length];
            if (isRoot && node.Length >= TagView.SizeWordSize) System.Array.Fill(covered, true, 0, TagView.SizeWordSize);
            foreach (var field in structure.Fields.OrderBy(x => x.Offset).ThenBy(x => x.Name, StringComparer.Ordinal))
            {
                int width = schema.FieldSize(field);
                if (width <= 0 || field.Offset < 0 || field.Offset + width > node.Length) continue;
                System.Array.Fill(covered, true, field.Offset, width);
                string fieldPath = Join(path, field.Name);
                var children = node.Children.Where(x => ReferenceEquals(x.Field, field)).ToList();
                row.Children.Add(Field(field, node.Offset + field.Offset, width, children, fieldPath));
            }
            for (int at = 0; at < node.Length; at++)
            {
                if (covered[at]) continue;
                int end = at;
                while (end < node.Length && !covered[end]) end++;
                if (data.AsSpan(node.Offset + at, end - at).ContainsAnyExcept((byte)0))
                    row.Children.Add(new()
                    {
                        Name = $"+{at}", Path = Join(path, $"+{at}"), Kind = TagFieldKind.Unknown, Offset = node.Offset + at, Size = end - at,
                        Value = Convert.ToHexString(data, node.Offset + at, end - at), Edit = TagValueEdit.Raw,
                    });
                at = end;
            }
            return row;
        }

        private TagFieldRow Field(RegistryField field, int at, int width, List<TagNode> children, string path)
        {
            string type = schema.TypeName(field);
            switch (type)
            {
                case "array":
                    return ArrayRow(field, at, children.FirstOrDefault(x => x.Kind == TagNodeKind.Array), path, type);
                case "relptr" or "blockptr" when children.FirstOrDefault() is { Kind: TagNodeKind.Array } pointed:
                    return ArrayRow(field, at - TagArray.RelativeOffset, pointed, path, type);
                case "relptr" or "blockptr" when children.FirstOrDefault() is { Kind: TagNodeKind.Object, Alias: false } target && target.Children.Count == 1:
                {
                    var row = new TagFieldRow
                    {
                        Name = field.Name, Path = path, Kind = TagFieldKind.Object, Type = type, Offset = at, Size = width, ClassId = target.ClassId,
                        Value = "-> " + (schema.Class(target.ClassId)?.Name ?? $"0x{target.ClassId:X8}"),
                    };
                    row.Children.Add(Struct(target.Children[0], schema.Class(target.ClassId)?.Name ?? "object", path));
                    return row;
                }
                case "string_ptr" or "inline_string" when children.FirstOrDefault(x => x.Kind == TagNodeKind.String) is { } text:
                    return new()
                    {
                        Name = field.Name, Path = path, Kind = TagFieldKind.String, Type = type, Offset = at, Size = width,
                        Value = Encoding.UTF8.GetString(data, text.Offset, text.Length - 1),
                    };
                case "struct" when children.FirstOrDefault(x => x.Kind == TagNodeKind.Struct) is { } nested:
                    return Struct(nested, field.Name, path);
                case "inline_array" when children.Count > 0:
                {
                    var row = new TagFieldRow { Name = field.Name, Path = path, Kind = TagFieldKind.Struct, Type = type, Offset = at, Size = width, ClassId = field.Struct ?? 0,
                        Value = $"{children.Count} x {schema.Class(field.Struct ?? 0)?.Name}" };
                    for (int index = 0; index < children.Count; index++) row.Children.Add(Struct(children[index], $"[{index}]", $"{path}[{index}]"));
                    return row;
                }
                case "tagref" or "tagptr":
                {
                    uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
                    bool none = value is 0 or uint.MaxValue;
                    return new()
                    {
                        Name = field.Name, Path = path, Kind = TagFieldKind.Value, Type = type, Offset = at, Size = 4, Value = $"0x{value:X8}",
                        Note = none ? "none" : nameOf?.Invoke(value) ?? "", Edit = TagValueEdit.TagRef, Reference = none ? null : value,
                        Targets = field.Targets ?? [],
                    };
                }
                case "wideref":
                    return Value(field, type, at, 8, $"0x{BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(at)):X16}", TagValueEdit.WideRef, path);
            }
            if (ScalarTypes.Integers.Contains(type) && width == schema.TypeSize(field))
                return Value(field, type, at, width, IntegerText(type, at), TagValueEdit.Integer, path);
            if (ScalarTypes.Floats.Contains(type) && width == schema.TypeSize(field))
                return Value(field, type, at, width, FloatText(data.AsSpan(at, width)), TagValueEdit.Float, path);
            if (ScalarTypes.Vectors.Contains(type) && width % 4 == 0)
                return Value(field, type, at, width, string.Join(", ", Enumerable.Range(0, width / 4).Select(i => FloatText(data.AsSpan(at + i * 4, 4)))),
                    TagValueEdit.Vector, path);
            if (Hashes.Contains(type) && width == 4)
            {
                uint hash = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
                var row = Value(field, type, at, 4, $"0x{hash:X8}", TagValueEdit.Hash, path);
                return hashNames?.Invoke(hash) is { Count: > 0 } names && type == "strhash"
                    ? new TagFieldRow { Name = row.Name, Path = row.Path, Kind = row.Kind, Type = row.Type, Offset = row.Offset, Size = row.Size,
                        Value = row.Value, Edit = row.Edit, Note = string.Join(" | ", names.Take(4)) }
                    : row;
            }
            bool pointer = type is "relptr" or "blockptr" or "string_ptr" or "inline_string" or "array" or "hkarray";
            return Value(field, type, at, width, Convert.ToHexString(data, at, width), pointer ? TagValueEdit.None : TagValueEdit.Raw, path);
        }

        private TagFieldRow ArrayRow(RegistryField field, int descriptor, TagNode? node, string path, string type)
        {
            uint element = node?.ClassId ?? field.Element ?? 0;
            var row = new TagFieldRow
            {
                Name = field.Name, Path = path, Kind = TagFieldKind.Array, Type = type, Offset = descriptor, Size = TagArray.FieldSize, ClassId = element,
                Count = node?.Count ?? (BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(descriptor)) == 0 ? 0 : -1),
                Value = $"{node?.Count ?? 0} x {schema.Class(element)?.Name ?? $"0x{element:X8}"}",
                CanResize = node is null ? element != 0 && schema.Class(element)?.Size > 0 && BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(descriptor + TagArray.RelativeOffset)) == 0
                    : !node.Alias && node.Stride > 0,
            };
            if (node is { Alias: false })
                for (int index = 0; index < node.Children.Count; index++) row.Children.Add(Struct(node.Children[index], $"[{index}]", $"{path}[{index}]"));
            return row;
        }

        private TagFieldRow Value(RegistryField field, string type, int at, int width, string value, TagValueEdit edit, string path) => new()
        {
            Name = field.Name, Path = path, Kind = TagFieldKind.Value, Type = type, Offset = at, Size = width, Value = value, Edit = edit,
        };

        private string IntegerText(string type, int at) => type switch
        {
            "u8" or "bool8" => data[at].ToString(CultureInfo.InvariantCulture),
            "i8" => ((sbyte)data[at]).ToString(CultureInfo.InvariantCulture),
            "u16" => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)).ToString(CultureInfo.InvariantCulture),
            "i16" => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(at)).ToString(CultureInfo.InvariantCulture),
            "u32" or "bool32" => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at)).ToString(CultureInfo.InvariantCulture),
            "i32" => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at)).ToString(CultureInfo.InvariantCulture),
            "u64" => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(at)).ToString(CultureInfo.InvariantCulture),
            _ => BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(at)).ToString(CultureInfo.InvariantCulture),
        };
    }

    public static string FloatText(ReadOnlySpan<byte> bytes) => bytes.Length switch
    {
        2 => BinaryPrimitives.ReadHalfLittleEndian(bytes).ToString("R", CultureInfo.InvariantCulture),
        4 => BinaryPrimitives.ReadSingleLittleEndian(bytes).ToString("R", CultureInfo.InvariantCulture),
        _ => BinaryPrimitives.ReadDoubleLittleEndian(bytes).ToString("R", CultureInfo.InvariantCulture),
    };

    private static string Join(string path, string name) => path.Length == 0 ? name : path + "." + name;
}
