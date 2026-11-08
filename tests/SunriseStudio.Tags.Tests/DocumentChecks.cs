using System.Buffers.Binary;
using System.Text;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.Tags;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;

/** TagDocument, TagRelocator and TagAllocator on a hand-built tag with arrays, nested arrays, an object and a string. */
static class DocumentChecks
{
    private const uint Root = 0x80800100, Row = 0x80800101, Item = 0x80800102, Inner = 0x80800103;
    private const int RowsField = 8, ChildField = 24, LabelField = 32, ValueField = 40, InnerField = 8;

    public static void Run()
    {
        var schema = Schema();
        byte[] blob = Build(schema, out int[] rows);
        Parsing(schema, blob, rows);
        Growing(schema, blob, rows);
        GoldenAgainstExistingHelpers(schema, blob, rows);
        Writing(schema);
        ArrayBehindPointer();
        Allocating();
    }

    /** A class that names only the second word of an array descriptor still parses it as an array. */
    private static void ArrayBehindPointer()
    {
        var registry = new TagRegistry { BuildId = "test" };
        registry.FieldTypes.Add(new(0, "relptr", 8, 8, "ref"));
        registry.FieldTypes.Add(new(1, "u32", 4, 4, "scalar"));
        registry.Classes.Add(new() { Class = 0x80800300, Name = "holder", Kind = "tag", Size = 16, Source = "game",
            Fields = [new() { Name = "unk_8", Offset = 8, Type = 0, Confidence = "game" }] });
        registry.Classes.Add(new() { Class = 0x80800301, Name = "cell", Kind = "element", Size = 4, Source = "game",
            Fields = [new() { Name = "v", Offset = 0, Type = 1, Confidence = "game" }] });
        var schema = new RegistryIndex(registry);
        byte[] blob = TagRelocator.ReplaceArray(new byte[16], 0, 0x80800301, 4, U32s(7, 8, 9), out _);
        var document = TagDocument.Parse(blob, 0x80800300, schema);
        Check(document.Problems.Count == 0 && document.OutOfLine.Single() is { Kind: TagNodeKind.Array, Count: 3, ClassId: 0x80800301 }
            && document.Pointers.Single() == new TagPointerSite(0, TagPointerKind.Array), "An array named by its second word was not parsed as an array.");
    }

    /** A tag built from structure parses cleanly, reads back its values, and repacks to itself. */
    private static void Writing(RegistryIndex schema)
    {
        TagBlueprint Struct(uint classId, int size, params (int At, uint Value)[] words)
        {
            byte[] bytes = new byte[size];
            foreach (var (at, value) in words) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
            return new() { ClassId = classId, Bytes = bytes };
        }
        var root = Struct(Root, 48, (ValueField, 7));
        var first = Struct(Row, 24, (0, 100));
        first.Links.Add(new(InnerField, TagLinkKind.Array, Inner, [Struct(Inner, 4, (0, 11)), Struct(Inner, 4, (0, 12))]));
        var second = Struct(Row, 24, (0, 200));
        second.Links.Add(new(InnerField, TagLinkKind.Array, Inner, [Struct(Inner, 4, (0, 21))]));
        root.Links.Add(new(RowsField, TagLinkKind.Array, Row, [first, second]));
        root.Links.Add(new(ChildField, TagLinkKind.Object, Item, [Struct(Item, 8, (0, 5))]));
        root.Links.Add(new(LabelField, TagLinkKind.String, 0, [], Encoding.ASCII.GetBytes("hello\0")));

        byte[] written = CanonicalTagWriter.Write(root, schema);
        Check(written.AsSpan().SequenceEqual(CanonicalTagWriter.Write(root, schema)), "Two writes of one blueprint differ.");
        var document = TagDocument.Parse(written, Root, schema);
        Check(document.Problems.Count == 0, "Problems in a written tag: " + string.Join("; ", document.Problems));
        Check(BinaryPrimitives.ReadUInt64LittleEndian(written) == (ulong)written.Length, "The size word is not the length.");
        var array = document.Root.Children.Single(x => x.Kind == TagNodeKind.Array);
        Check(array.Count == 2 && array.Children[1].Children.Single(x => x.Kind == TagNodeKind.Array).Count == 1, "Written arrays were misread.");
        Check(document.Root.Children.Single(x => x.Kind == TagNodeKind.String).Length == 6
            && document.OutOfLine.Last(x => !x.Alias) is { Kind: TagNodeKind.Object, ClassId: SInlineString.Class, Length: 18 },
            "The string is not in one string block at the end.");
        Check(CanonicalTagWriter.Repack(document, schema, out string? reason) is { } repacked && repacked.AsSpan().SequenceEqual(written),
            "A written tag does not repack to itself: " + reason);
    }

    /** Root {size, rows[] of Row, child -> Item, label -> string, value}; Row {id, inner[] of Inner}. */
    private static RegistryIndex Schema()
    {
        var registry = new TagRegistry { BuildId = "test" };
        foreach (var (name, size) in new[] { ("u64", 8), ("u32", 4), ("f32", 4), ("array", 16), ("relptr", 8), ("string_ptr", 8) })
            registry.FieldTypes.Add(new(registry.FieldTypes.Count, name, size, Math.Min(size, 8), "scalar"));
        RegistryField F(string name, int offset, string type) => new() { Name = name, Offset = offset, Type = registry.TypeId(type), Confidence = "game" };
        registry.Classes.Add(new() { Class = Root, Name = "root", Kind = "tag", Size = 48, Source = "game",
            Fields = [F("size", 0, "u64"), F("rows", RowsField, "array"), F("child", ChildField, "relptr"), F("label", LabelField, "string_ptr"), F("value", ValueField, "u32")] });
        registry.Classes.Add(new() { Class = Row, Name = "row", Kind = "element", Size = 24, Source = "game", Fields = [F("id", 0, "u32"), F("inner", InnerField, "array")] });
        registry.Classes.Add(new() { Class = Item, Name = "item", Kind = "struct", Size = 8, Source = "game", Fields = [F("x", 0, "u32"), F("y", 4, "f32")] });
        registry.Classes.Add(new() { Class = Inner, Name = "inner", Kind = "element", Size = 4, Source = "game", Fields = [F("v", 0, "u32")] });
        return new RegistryIndex(registry);
    }

    /** Lays the tag out the way the packer does: root, then each block 16-byte aligned behind a 4-byte class word. */
    private static byte[] Build(RegistryIndex schema, out int[] rows)
    {
        byte[] blob = new byte[48];
        BinaryPrimitives.WriteUInt64LittleEndian(blob, 48);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(ValueField), 7);
        byte[] rowBytes = new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(rowBytes, 100);
        BinaryPrimitives.WriteUInt32LittleEndian(rowBytes.AsSpan(24), 200);
        blob = TagRelocator.ReplaceArray(blob, RowsField, Row, 24, rowBytes, out int first);
        rows = [first, first + 24];
        blob = TagRelocator.ReplaceArray(blob, rows[0] + InnerField, Inner, 4, U32s(11, 12), out _);
        blob = TagRelocator.ReplaceArray(blob, rows[1] + InnerField, Inner, 4, U32s(21), out _);
        blob = Block(blob, Item, U32s(5, BitConverter.SingleToUInt32Bits(2.5f)), ChildField);
        blob = Block(blob, 0, Encoding.ASCII.GetBytes("hello\0"), LabelField, classWord: false);
        return blob;
    }

    private static byte[] Block(byte[] blob, uint classId, byte[] body, int pointer, bool classWord = true)
    {
        int start = (blob.Length + 19) & ~15;
        byte[] output = new byte[start + body.Length];
        blob.CopyTo(output, 0);
        if (classWord) BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(start - 4), classId);
        body.CopyTo(output, start);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(pointer), start - pointer);
        BinaryPrimitives.WriteUInt64LittleEndian(output, (ulong)output.Length);
        return output;
    }

    private static byte[] U32s(params uint[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int index = 0; index < values.Length; index++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4), values[index]);
        return bytes;
    }

    private static void Parsing(RegistryIndex schema, byte[] blob, int[] rows)
    {
        var document = TagDocument.Parse(blob, Root, schema);
        Check(document.Problems.Count == 0, "Problems: " + string.Join("; ", document.Problems));
        Check(document.Encode().AsSpan().SequenceEqual(blob), "An unedited document does not encode to its input.");
        var array = document.Root.Children.Single(x => x.Kind == TagNodeKind.Array);
        Check(array is { Count: 2, Stride: 24, ClassId: Row } && array.Children[0].Offset == rows[0], "The row array was misread.");
        Check(array.Children[1].Children.Single(x => x.Kind == TagNodeKind.Array).Count == 1, "A nested array was misread.");
        Check(document.Root.Children.Single(x => x.Kind == TagNodeKind.Object) is { ClassId: Item, Length: 12 }, "The object was misread.");
        Check(document.Root.Children.Single(x => x.Kind == TagNodeKind.String).Length == 6, "The string was misread.");
        Check(document.Pointers.Count == 5, $"Expected 5 pointer sites, got {document.Pointers.Count}.");
        Check(document.Opaque.All(x => !blob.AsSpan(x.Start, x.Length).ContainsAnyExcept((byte)0)), "Only zero padding may be opaque here.");

        document.WriteU32(document.FieldOffset(document.Root, "value", schema), 9);
        Check(document.Changes() is [(ValueField, 1)], "A scalar edit touched more than its byte.");
        var delta = document.Delta();
        Check(delta.Apply(blob).AsSpan().SequenceEqual(document.Encode()), "The edit did not survive the project delta.");
    }

    private static void Growing(RegistryIndex schema, byte[] blob, int[] rows)
    {
        var document = TagDocument.Parse(blob, Root, schema);
        // The row array is not at the end, so it moves; the new row is a copy of row 0.
        byte[] copy = blob.AsSpan(rows[0], 24).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(copy, 300);
        byte[] grown = TagRelocator.AppendRows(document, schema, RowsField, [copy], [rows[0]]);
        var after = TagDocument.Parse(grown, Root, schema);
        Check(after.Problems.Count == 0, "Problems after growth: " + string.Join("; ", after.Problems));
        Check(grown.AsSpan(0, blob.Length).SequenceEqual(Patched(blob, grown, RowsField, 16, 0, 8)), "Growth changed installed bytes other than the descriptor and size word.");
        var array = after.Root.Children.Single(x => x.Kind == TagNodeKind.Array);
        Check(array.Count == 3 && array.Offset >= blob.Length, "The grown array did not move to the end.");
        // A copied row shares its source's inner array, so read through the pointer, not the parse tree.
        uint[] Inners(TagNode row)
        {
            var inner = new SunriseStudio.Tags.Views.TagArray(grown, row.Offset + InnerField);
            var values = new uint[inner.Count];
            for (int i = 0; i < values.Length; i++) values[i] = BinaryPrimitives.ReadUInt32LittleEndian(grown.AsSpan(inner.RowOffset(i, 4)));
            return values;
        }
        Check(Inners(array.Children[0]).SequenceEqual([11u, 12u]) && Inners(array.Children[1]).SequenceEqual([21u])
            && Inners(array.Children[2]).SequenceEqual([11u, 12u]), "Moved or copied rows lost their nested arrays.");
        Check(BinaryPrimitives.ReadUInt64LittleEndian(grown) == (ulong)grown.Length, "The size word did not follow the length.");

        // An array that ends the tag grows in place.
        byte[] tail = TagRelocator.ReplaceArray(blob, rows[1] + InnerField, Inner, 4, U32s(21, 22), out _);
        var tailDocument = TagDocument.Parse(tail, Root, schema);
        var tailArray = tailDocument.OutOfLine.Last(x => x.Kind == TagNodeKind.Array);
        byte[] inPlace = TagRelocator.AppendRows(tailDocument, schema, rows[1] + InnerField, [U32s(23)]);
        Check(inPlace.Length == tail.Length + 4 && TagDocument.Parse(inPlace, Root, schema).OutOfLine.Last(x => x.Kind == TagNodeKind.Array)
            is { Count: 3 } grownTail && grownTail.Offset == tailArray.Offset, "The last array did not grow in place.");

        bool refused = false;
        byte[] pointerRow = blob.AsSpan(rows[0], 24).ToArray();
        try { TagRelocator.AppendRows(document, schema, RowsField, [pointerRow]); }
        catch (ArgumentException) { refused = true; }
        Check(refused, "A row with pointers and no source was accepted.");
    }

    /** The original bytes with the listed ranges taken from `grown`, so the comparison ignores only them. */
    private static byte[] Patched(byte[] original, byte[] grown, params int[] ranges)
    {
        byte[] result = original.ToArray();
        for (int index = 0; index < ranges.Length; index += 2)
            grown.AsSpan(ranges[index], ranges[index + 1]).CopyTo(result.AsSpan(ranges[index]));
        return result;
    }

    /**
     * ReplaceArray must equal AppendArray plus the row copy and the size word. AppendRows must
     * equal InvestmentItemClone.AppendRow on their shared cases.
     */
    private static void GoldenAgainstExistingHelpers(RegistryIndex schema, byte[] blob, int[] rows)
    {
        byte[] newRows = U32s(1, 2, 3);
        byte[] expected = blob;
        int expectedFirst = TagRelocator.AppendArray(ref expected, rows[0] + InnerField, Inner, newRows.Length / 4, newRows.Length);
        newRows.CopyTo(expected, expectedFirst); BinaryPrimitives.WriteInt64LittleEndian(expected, expected.Length);
        byte[] actual = TagRelocator.ReplaceArray(blob, rows[0] + InnerField, Inner, 4, newRows, out int first);
        Check(actual.AsSpan().SequenceEqual(expected) && first == expectedFirst, "ReplaceArray differs from AppendArray.");

        var document = TagDocument.Parse(blob, Root, schema);
        byte[] copy = blob.AsSpan(rows[0], 24).ToArray();
        byte[] moved = InvestmentItemClone.AppendRow(blob, RowsField, 24, copy, [InnerField], rows[0]);
        Check(TagRelocator.AppendRows(document, schema, RowsField, [copy], [rows[0]]).AsSpan().SequenceEqual(moved),
            "Moving growth differs from InvestmentItemClone.AppendRow.");
        byte[] tail = TagRelocator.ReplaceArray(blob, RowsField, Row, 24, blob.AsSpan(rows[0], 48), out _);
        byte[] inPlace = InvestmentItemClone.AppendRow(tail, RowsField, 24, copy, [InnerField], rows[0]);
        Check(TagRelocator.AppendRows(TagDocument.Parse(tail, Root, schema), schema, RowsField, [copy], [rows[0]]).AsSpan().SequenceEqual(inPlace),
            "In-place growth differs from InvestmentItemClone.AppendRow.");
    }

    private static void Allocating()
    {
        Check(TagHashes.Make(0x0412, 4) == 0x80800000u + (0x0412u << 13) + 4 && TagHashes.Split(TagHashes.Make(0x0412, 4)) == (0x0412, 4) && TagHashes.Split(0x80882004) == (0x0041, 4),
            "The tag formula is wrong.");
        Throws<ArgumentOutOfRangeException>(() => new TagAllocator([0x00FF]), "A package outside the new range was accepted.");
        Throws<ArgumentOutOfRangeException>(() => TagHashes.Make(0x0412, TagHashes.EntriesPerPackage), "Entry 8192 was accepted.");
        var allocator = new TagAllocator([0x0412, 0x0413]);
        uint a = allocator.Allocate("a"), b = allocator.Allocate("b");
        Check(a == TagHashes.Make(0x0412, 0) && b == TagHashes.Make(0x0412, 1) && allocator.Allocate("a") == a, "Allocation order is wrong.");
        allocator.Release("b");
        Check(allocator.Allocate("c") == TagHashes.Make(0x0412, 2), "A released entry was handed out again.");
        var loaded = TagAllocator.Parse(allocator.ToJson());
        Check(loaded.ToJson().AsSpan().SequenceEqual(allocator.ToJson()) && loaded.Allocate("d") == TagHashes.Make(0x0412, 3),
            "allocation.json does not round trip.");
        for (int index = 4; index < TagHashes.EntriesPerPackage; index++) loaded.Allocate($"n{index}");
        Check(loaded.Allocate("next") == TagHashes.Make(0x0413, 0), "A full package did not move on to the next.");
        var single = new TagAllocator([0x0500]);
        for (int index = 0; index < TagHashes.EntriesPerPackage; index++) single.Allocate($"n{index}");
        Throws<InvalidOperationException>(() => single.Allocate("over"), "Entry 8193 was allocated in a one-package project.");
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
