using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Graph;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Tags.Query;

/** A query that does not parse, names an unknown field, or applies a step to the wrong kind of node. */
public sealed class PathQueryException(string message) : Exception(message);

/**
 * One resolved node: a struct (Type "struct", ClassId its class), a field (Type its field type), or a
 * string. Every node carries the tag hash and the absolute byte offset in that tag.
 */
public sealed record QueryNode(uint Tag, int Offset, uint ClassId, RegistryField? Field, string Type);

/** Tag bytes, classes and names a query reads. */
public sealed class QueryContext(ITagSource source, RegistryIndex schema, Func<string, uint?>? projectNames = null)
{
    private readonly Dictionary<uint, PackageEntry> entries = source.Entries.GroupBy(x => x.TagHash).ToDictionary(x => x.Key, x => x.First());
    private readonly Dictionary<uint, byte[]> bytes = [];

    public RegistryIndex Schema => schema;
    public uint ClassOf(uint tag) => entries.TryGetValue(tag, out var entry) ? entry.Reference
        : throw new PathQueryException($"Tag 0x{tag:X8} is not in the package set.");
    public byte[] Bytes(uint tag) => bytes.TryGetValue(tag, out var cached) ? cached : bytes[tag] = source.Read(tag);
    public IEnumerable<uint> TagsOfClass(uint classId) => entries.Values.Where(x => x.Reference == classId).Select(x => x.TagHash).Order();
    public uint? ResolveWide(ulong hash) => source.ResolveWide(hash);
    public uint? ResolveName(string name) => projectNames?.Invoke(name);
}

/** Tag path queries (specs/path-query-syntax.md): `@0x80A1B2C3.bubbles[*]->statics[0].model`. */
public sealed class PathQuery
{
    private abstract record Step;
    private sealed record FieldStep(string Name) : Step;
    private sealed record IndexStep(string Spec) : Step;
    private sealed record FollowStep : Step;
    private sealed record ClassStep(string Class) : Step;
    private sealed record PredicateStep(string Field, string Operator, string Literal) : Step;

    private readonly string text;
    private readonly string root;
    private readonly List<Step> steps;

    private PathQuery(string text, string root, List<Step> steps) { this.text = text; this.root = root; this.steps = steps; }

    public override string ToString() => text;

    public static PathQuery Parse(string text)
    {
        int at = 0;
        string root;
        if (text.StartsWith('.')) root = ".";
        else if (text.StartsWith("##0x", StringComparison.Ordinal)) root = Take(text, ref at, 4, 16, char.IsAsciiHexDigit, "a 16-digit wide hash");
        else if (text.StartsWith("@0x", StringComparison.Ordinal)) root = Take(text, ref at, 3, 8, char.IsAsciiHexDigit, "an 8-digit tag hash");
        else if (text.StartsWith("@<", StringComparison.Ordinal)) { at = 1; root = "<" + Bracketed(text, ref at, '<', '>') + ">"; }
        else if (text.StartsWith("@\"", StringComparison.Ordinal)) { at = 1; root = "\"" + Quoted(text, ref at) + "\""; }
        else throw new PathQueryException($"A query starts with @0x, ##0x, @<class>, @\"name\" or '.': {text}");
        if (root == ".") at = 0;
        var steps = new List<Step>();
        while (at < text.Length)
        {
            char c = text[at];
            if (c == '.') { at++; steps.Add(new FieldStep(Identifier(text, ref at))); }
            else if (c == '[') steps.Add(new IndexStep(Bracketed(text, ref at, '[', ']')));
            else if (c == '-' && at + 1 < text.Length && text[at + 1] == '>')
            {
                at += 2;
                steps.Add(new FollowStep());
                // "->statics" names a field of the target with no dot in between.
                if (at < text.Length && (char.IsAsciiLetter(text[at]) || text[at] == '_')) steps.Add(new FieldStep(Identifier(text, ref at)));
            }
            else if (c == '<') steps.Add(new ClassStep(Bracketed(text, ref at, '<', '>')));
            else if (c == '{') steps.Add(Predicate(Bracketed(text, ref at, '{', '}')));
            else throw new PathQueryException($"Unexpected '{c}' at {at} in {text}");
        }
        return new(text, root, steps);
    }

    /** @param start The node a relative ('.') query starts from. */
    public List<QueryNode> Evaluate(QueryContext context, QueryNode? start = null)
    {
        var nodes = Roots(context, start);
        foreach (var step in steps)
            nodes = step switch
            {
                FieldStep field => nodes.Select(x => Field(context, x, field.Name)).ToList(),
                // After a class filter the nodes are rows already, so the index picks among them.
                IndexStep index => nodes.Count > 0 && nodes.All(x => x.Type == "struct")
                    ? Select(context, nodes, index.Spec) : nodes.SelectMany(x => Index(context, x, index.Spec)).ToList(),
                FollowStep => nodes.Select(x => Follow(context, x)).OfType<QueryNode>().ToList(),
                ClassStep filter => nodes.SelectMany(x => x.Type == "array" ? Index(context, x, "*") : [x])
                    .Where(x => x.Type == "struct" && x.ClassId == ClassId(context.Schema, filter.Class)).ToList(),
                PredicateStep predicate => nodes.Where(x => Matches(context, x, predicate)).ToList(),
                _ => throw new InvalidOperationException(),
            };
        return nodes;
    }

    private List<QueryNode> Roots(QueryContext context, QueryNode? start)
    {
        if (root == ".") return [start ?? throw new PathQueryException("A relative query needs a start node.")];
        if (root.StartsWith("##0x", StringComparison.Ordinal))
        {
            ulong wide = ulong.Parse(root.AsSpan(4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            return [TagRoot(context, context.ResolveWide(wide) ?? throw new PathQueryException($"No tag has wide hash {root[2..]}."))];
        }
        if (root.StartsWith("@0x", StringComparison.Ordinal))
            return [TagRoot(context, uint.Parse(root.AsSpan(3), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture))];
        if (root.StartsWith('<')) return context.TagsOfClass(ClassId(context.Schema, root[1..^1])).Select(x => TagRoot(context, x)).ToList();
        string name = root[1..^1];
        return [TagRoot(context, context.ResolveName(name) ?? throw new PathQueryException($"No project tag is named \"{name}\"."))];
    }

    private static QueryNode TagRoot(QueryContext context, uint tag) => new(tag, 0, context.ClassOf(tag), null, "struct");

    private static QueryNode Field(QueryContext context, QueryNode node, string name)
    {
        if (node.Type != "struct") throw new PathQueryException($"'.{name}' needs a struct, not a {node.Type}.");
        var structure = context.Schema.Class(node.ClassId) ?? throw new PathQueryException($"Class 0x{node.ClassId:X8} is not in the registry.");
        var field = structure.Fields.FirstOrDefault(x => x.Name == name)
            ?? structure.Fields.FirstOrDefault(x => x.NameHash is { } hash && name == $"h_{hash:x8}")
            ?? throw new PathQueryException($"Class {structure.Name} has no field \"{name}\".");
        string type = context.Schema.TypeName(field);
        return new(node.Tag, node.Offset + field.Offset, type == "struct" ? field.Struct ?? 0 : node.ClassId, field, type);
    }

    private static IEnumerable<QueryNode> Index(QueryContext context, QueryNode node, string spec)
    {
        if (node.Type == "inline_array" && node.Field is { Struct: { } inlineClass, Count: > 0 and int inlineCount, Length: > 0 and int length })
        {
            var elements = Enumerable.Range(0, inlineCount).Select(i => new QueryNode(node.Tag, node.Offset + i * (length / inlineCount), inlineClass, null, "struct")).ToList();
            return Select(context, elements, spec);
        }
        if (node.Type != "array") throw new PathQueryException($"'[{spec}]' needs an array, not a {node.Type}.");
        byte[] bytes = context.Bytes(node.Tag);
        var (count, element, data) = ArrayRows(bytes, node.Offset);
        int stride = context.Schema.Class(element)?.Size ?? throw new PathQueryException($"Element class 0x{element:X8} has no size.");
        var rows = Enumerable.Range(0, (int)count).Select(i => new QueryNode(node.Tag, checked(data + i * stride), element, null, "struct")).ToList();
        return Select(context, rows, spec);
    }

    /** `*`, a position (negative counts from the end), or `#key` matched against each row's class key field. */
    private static List<QueryNode> Select(QueryContext context, List<QueryNode> rows, string spec)
    {
        if (spec == "*") return rows;
        if (spec.StartsWith('#'))
        {
            ulong wanted = Literal(spec[1..]);
            return rows.Where(row =>
            {
                var structure = context.Schema.Class(row.ClassId)!;
                string key = structure.KeyField ?? throw new PathQueryException($"Class {structure.Name} has no key field for '[{spec}]'.");
                return Number(context, Field(context, row, key)) == wanted;
            }).ToList();
        }
        if (!long.TryParse(spec, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long index))
            throw new PathQueryException($"'[{spec}]' is not *, a position or #key.");
        if (index < 0) index += rows.Count;
        if (index < 0 || index >= rows.Count) throw new PathQueryException($"Row {spec} is outside {rows.Count} rows.");
        return [rows[(int)index]];
    }

    private static (long Count, uint Element, int Data) ArrayRows(byte[] bytes, int field)
    {
        var array = new TagArray(bytes, field);
        if (!array.IsWellFormed) throw new PathQueryException($"The array at {field} is malformed.");
        return array.HasHeader ? (array.Count, array.ElementClass, array.DataOffset) : (0, 0, 0);
    }

    private static QueryNode? Follow(QueryContext context, QueryNode node)
    {
        byte[] bytes = context.Bytes(node.Tag);
        switch (node.Type)
        {
            case "tagref" or "tagptr":
                uint tag = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(node.Offset));
                return tag is 0 or uint.MaxValue ? null : TagRoot(context, tag);
            case "wideref":
                ulong wide = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(node.Offset));
                return wide is 0 or ulong.MaxValue ? null
                    : TagRoot(context, context.ResolveWide(wide) ?? throw new PathQueryException($"No tag has wide hash 0x{wide:X16}."));
            case "relptr":
                var pointer = new TagPointer(bytes, node.Offset);
                return pointer.Target is { } target ? new(node.Tag, target, pointer.TargetClass, null, "struct") : null;
            case "string_ptr" or "inline_string":
                return new TagPointer(bytes, node.Offset).Target is { } text ? new(node.Tag, text, 0, null, "string") : null;
            default:
                throw new PathQueryException($"'->' follows a reference or pointer, not a {node.Type}; an array takes '[ ]'.");
        }
    }

    private static bool Matches(QueryContext context, QueryNode node, PredicateStep predicate)
    {
        ulong value = Number(context, Field(context, node, predicate.Field)), literal = Literal(predicate.Literal);
        return predicate.Operator switch
        {
            "==" => value == literal,
            "!=" => value != literal,
            "<" => value < literal,
            ">" => value > literal,
            _ => throw new PathQueryException($"Unknown operator {predicate.Operator}."),
        };
    }

    /** A scalar field as an unsigned number of its own width; floats compare by bits only with == and !=. */
    private static ulong Number(QueryContext context, QueryNode node)
    {
        int size = node.Field is { } field ? context.Schema.FieldSize(field) : 0;
        var bytes = context.Bytes(node.Tag).AsSpan(node.Offset);
        return size switch
        {
            1 => bytes[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            8 => BinaryPrimitives.ReadUInt64LittleEndian(bytes),
            _ => throw new PathQueryException($"Field {node.Field?.Name} is not a scalar a query can compare."),
        };
    }

    /** 0x hex, a decimal integer, or a quoted string compared as its FNV-1 hash. */
    private static ulong Literal(string text)
    {
        text = text.Trim();
        if (text.StartsWith('"') && text.EndsWith('"') && text.Length >= 2) return StringBankWriter.Hash(text[1..^1]);
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.Parse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong number) ? number
            : throw new PathQueryException($"\"{text}\" is not a literal; use 0x hex, a decimal or a quoted string.");
    }

    /** A class filter: 0x hex id, a registry class name, that name without its s_ prefix, or a .tft type. */
    private static uint ClassId(RegistryIndex schema, string name)
    {
        if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.Parse(name.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        var classes = schema.Registry.Classes;
        return (classes.FirstOrDefault(x => x.Name == name) ?? classes.FirstOrDefault(x => x.Name == "s_" + name)
            ?? classes.FirstOrDefault(x => x.TftType == name))?.Class ?? throw new PathQueryException($"No class is named \"{name}\".");
    }

    private static PredicateStep Predicate(string body)
    {
        foreach (string op in new[] { "==", "!=", "<", ">" })
        {
            int at = body.IndexOf(op, StringComparison.Ordinal);
            if (at > 0) return new(body[..at].Trim(), op, body[(at + op.Length)..].Trim());
        }
        throw new PathQueryException($"A predicate is field op literal: {{{body}}}");
    }

    private static string Take(string text, ref int at, int prefix, int digits, Func<char, bool> valid, string what)
    {
        if (text.Length < prefix + digits || !text.AsSpan(prefix, digits).ToString().All(valid))
            throw new PathQueryException($"Expected {what}: {text}");
        at = prefix + digits;
        return text[..at];
    }

    private static string Identifier(string text, ref int at)
    {
        int start = at;
        while (at < text.Length && (char.IsAsciiLetterOrDigit(text[at]) || text[at] == '_')) at++;
        return at > start ? text[start..at] : throw new PathQueryException($"Expected a field name at {start}: {text}");
    }

    /** Reads `open ... close`, allowing a quoted string inside; `at` ends after the closing bracket. */
    private static string Bracketed(string text, ref int at, char open, char close)
    {
        if (text[at] != open) throw new PathQueryException($"Expected '{open}' at {at}: {text}");
        var inner = new StringBuilder();
        for (at++; at < text.Length; at++)
        {
            if (text[at] == '"') { inner.Append('"').Append(Quoted(text, ref at)).Append('"'); at--; continue; }
            if (text[at] == close) { at++; return inner.ToString(); }
            inner.Append(text[at]);
        }
        throw new PathQueryException($"Missing '{close}': {text}");
    }

    private static string Quoted(string text, ref int at)
    {
        int end = text.IndexOf('"', at + 1);
        if (end < 0) throw new PathQueryException($"Missing closing quote: {text}");
        string value = text[(at + 1)..end];
        at = end + 1;
        return value;
    }
}
