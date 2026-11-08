using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Tags.Document;

/** One field whose bytes differ between two tags of a class; Path uses the path query syntax. */
public sealed record FieldDifference(string Path, string Left, string Right);

/**
 * Field-by-field diff of two tags of one class: scalars and references by value, nested structs and
 * inline elements by their fields, arrays by count and then row by row. Pointers compare by target
 * presence, since their relative values differ whenever the layout does.
 */
public static class TagDiff
{
    /** Rows compared per array; the rest only count. */
    public const int MaximumRows = 64;

    public static List<FieldDifference> Compare(byte[] left, byte[] right, uint classId, RegistryIndex schema)
    {
        var result = new List<FieldDifference>();
        Struct(left, 0, right, 0, classId, "", schema, result, 0);
        return result;
    }

    private static void Struct(byte[] left, int leftAt, byte[] right, int rightAt, uint classId, string path, RegistryIndex schema,
        List<FieldDifference> result, int depth)
    {
        if (depth > TagDocument.MaximumDepth || schema.Class(classId) is not { } structure) return;
        foreach (var field in structure.Fields.OrderBy(x => x.Offset))
        {
            int a = leftAt + field.Offset, b = rightAt + field.Offset, size = schema.FieldSize(field);
            string name = $"{path}.{field.Name}";
            if (a + Math.Max(size, 1) > left.Length || b + Math.Max(size, 1) > right.Length) continue;
            switch (schema.TypeName(field))
            {
                case "array":
                    var leftArray = new TagArray(left, a);
                    var rightArray = new TagArray(right, b);
                    if (leftArray.Count != rightArray.Count) result.Add(new(name + ".count", $"{leftArray.Count}", $"{rightArray.Count}"));
                    if (!leftArray.HasHeader || !rightArray.HasHeader || leftArray.ElementClass != rightArray.ElementClass) break;
                    int stride = schema.Class(leftArray.ElementClass)?.Size ?? 0;
                    for (int row = 0; stride > 0 && row < Math.Min(Math.Min(leftArray.Count, rightArray.Count), MaximumRows); row++)
                        Struct(left, leftArray.RowOffset(row, stride), right, rightArray.RowOffset(row, stride), leftArray.ElementClass,
                            $"{name}[{row}]", schema, result, depth + 1);
                    break;
                case "struct" when field.Struct is { } nested:
                    Struct(left, a, right, b, nested, name, schema, result, depth + 1);
                    break;
                case "inline_array" when field.Struct is { } element && field.Count is > 0 and int count && size > 0:
                    for (int index = 0; index < count; index++)
                        Struct(left, a + index * (size / count), right, b + index * (size / count), element, $"{name}[{index}]", schema, result, depth + 1);
                    break;
                case "relptr" or "blockptr" or "string_ptr" or "inline_string":
                    bool leftNull = new TagPointer(left, a).IsNull, rightNull = new TagPointer(right, b).IsNull;
                    if (leftNull != rightNull) result.Add(new(name, leftNull ? "null" : "set", rightNull ? "null" : "set"));
                    break;
                default:
                    if (size <= 0) break;
                    var leftBytes = left.AsSpan(a, size);
                    var rightBytes = right.AsSpan(b, size);
                    if (!leftBytes.SequenceEqual(rightBytes)) result.Add(new(name, Convert.ToHexString(leftBytes), Convert.ToHexString(rightBytes)));
                    break;
            }
        }
    }
}
