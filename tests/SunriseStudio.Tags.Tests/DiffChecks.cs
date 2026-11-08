using System.Buffers.Binary;
using System.Text;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;

/** String blocks and the tag diff. */
static class DiffChecks
{
    private const uint Holder = 0x80800400, Cell = 0x80800401;

    public static void Run()
    {
        var schema = Schema();
        StringBlocks(schema);
        Diffing(schema);
    }

    /** Holder {size, cells[], pointer at 32 onto an array, label string, then 16 zero bytes}. */
    private static RegistryIndex Schema()
    {
        var registry = new TagRegistry { BuildId = "test" };
        foreach (var (name, size) in new[] { ("u64", 8), ("u32", 4), ("array", 16), ("relptr", 8), ("string_ptr", 8) })
            registry.FieldTypes.Add(new(registry.FieldTypes.Count, name, size, Math.Min(size, 8), "scalar"));
        RegistryField F(string name, int offset, string type) => new() { Name = name, Offset = offset, Type = registry.TypeId(type), Confidence = "game" };
        registry.Classes.Add(new() { Class = Holder, Name = "holder", Kind = "tag", Size = 64, Align = 8, Source = "game",
            Fields = [F("size", 0, "u64"), F("cells", 8, "array"), F("unk_20", 32, "relptr"), F("label", 40, "string_ptr")] });
        registry.Classes.Add(new() { Class = Cell, Name = "cell", Kind = "element", Size = 4, Align = 4, Source = "game", Fields = [F("v", 0, "u32")] });
        return new RegistryIndex(registry);
    }

    /** One holder: cells [n], an array behind the pointer at 32 and the label in a string block. */
    private static byte[] HolderTag(int cells, string label)
    {
        byte[] root = new byte[64];
        BinaryPrimitives.WriteUInt64LittleEndian(root, 64);
        byte[] blob = TagRelocator.ReplaceArray(root, 8, Cell, 4, new byte[cells * 4], out _);
        blob = TagRelocator.ReplaceArray(blob, 24, Cell, 4, new byte[8], out _);
        // A string block: class word, u64 length, text; the pointer names the text.
        byte[] text = Encoding.ASCII.GetBytes(label + "\0");
        int body = (blob.Length + 4 + 7) & ~7;
        byte[] output = new byte[body + 8 + text.Length];
        blob.CopyTo(output, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(body - 4), SInlineString.Class);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(body), text.Length);
        text.CopyTo(output, body + 8);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(40), body + 8 - 40);
        BinaryPrimitives.WriteUInt64LittleEndian(output, (ulong)output.Length);
        return output;
    }

    private static void StringBlocks(RegistryIndex schema)
    {
        byte[] blob = HolderTag(2, "hello");
        var document = TagDocument.Parse(blob, Holder, schema);
        Check(document.Problems.Count == 0, "Problems: " + string.Join("; ", document.Problems));
        Check(document.OutOfLine.Count(x => x.Kind == TagNodeKind.Object && x.ClassId == SInlineString.Class && !x.Alias) == 1
            && document.OutOfLine.Single(x => x.Kind == TagNodeKind.String).Alias, "The string block does not own the text.");
        Check(document.Opaque.All(x => !blob.AsSpan(x.Start, x.Length).ContainsAnyExcept((byte)0)), "String block bytes stayed undescribed.");
    }

    private static void Diffing(RegistryIndex schema)
    {
        byte[] left = HolderTag(2, "a"), right = HolderTag(3, "a");
        var differences = TagDiff.Compare(left, right, Holder, schema);
        Check(differences.Any(x => x.Path == ".cells.count" && x.Left == "2" && x.Right == "3") && differences.All(x => !x.Path.StartsWith(".label")),
            "The diff missed the array count or reported the string.");
        Check(TagDiff.Compare(left, left, Holder, schema).Count == 0, "A tag differs from itself.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
