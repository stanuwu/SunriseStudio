using System.Buffers.Binary;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Editing;

/** The field tree and its edits on the `.tft` sample tag: values, and inserting, duplicating, deleting and moving rows. */
static class EditingChecks
{
    private const uint Root = 0x80800500;

    public static void Run()
    {
        var schema = TftChecks.Schema(upgraded: false);
        byte[] tag = TftChecks.Sample(schema, "#0x80000001").Data;
        var tree = Tree(tag, schema);
        var rows = tree.Walk().Single(x => x.Path == "rows");
        Check(rows is { Kind: TagFieldKind.Array, Count: 2, CanResize: true } && Row(tree, "rows[0].inner").Count == 2
            && Row(tree, "label").Value == "hello" && Row(tree, "+60") is { Kind: TagFieldKind.Unknown, Value: "EFBEADDE" }
            && Row(tree, "child").Children.Single().Children.Count == 2, "The field tree is wrong.");

        byte[] edited = TagFieldEdits.SetValue(tag, Row(tree, "value"), "2.5");
        edited = TagFieldEdits.SetValue(edited, Row(tree, "rows[1].id"), "0x10");
        edited = TagFieldEdits.SetValue(edited, Row(tree, "link"), "0x80001111");
        Check(BinaryPrimitives.ReadSingleLittleEndian(edited.AsSpan(56)) == 2.5f && Value(edited, schema, "rows[1].id") == "16"
            && Value(edited, schema, "link") == "0x80001111" && edited.Length == tag.Length, "A value edit was not written.");
        Throws<FormatException>(() => TagFieldEdits.SetValue(tag, Row(tree, "rows[0].id"), "-1"), "A negative u32 was accepted.");
        Throws<FormatException>(() => TagFieldEdits.SetValue(tag, Row(tree, "+60"), "00"), "Raw bytes of the wrong length were accepted.");

        var document = TagDocument.Parse(tag, Root, schema);
        // Insert: the old rows keep their inner arrays through the move.
        var inserted = Tree(TagFieldEdits.InsertRow(document, schema, rows, 1), schema);
        Check(Row(inserted, "rows").Count == 3 && Row(inserted, "rows[1].id").Value == "0" && Row(inserted, "rows[0].inner").Count == 2
            && Row(inserted, "rows[0].inner[1].v").Value == "12" && Row(inserted, "rows[2].id").Value == "2", "Insert lost a row or a pointer.");
        var duplicated = Tree(TagFieldEdits.DuplicateRow(document, schema, rows, 0), schema);
        Check(Row(duplicated, "rows").Count == 3 && Row(duplicated, "rows[1].id").Value == "1"
            && Row(duplicated, "rows[1].inner").Count == 2, "Duplicate did not copy the row and what it points at.");
        var deleted = Tree(TagFieldEdits.DeleteRow(document, schema, rows, 0), schema);
        Check(Row(deleted, "rows").Count == 1 && Row(deleted, "rows[0].id").Value == "2" && Row(deleted, "label").Value == "hello",
            "Delete removed the wrong row.");
        var moved = Tree(TagFieldEdits.MoveRow(document, schema, rows, 0, 1), schema);
        Check(Row(moved, "rows[0].id").Value == "2" && Row(moved, "rows[1].inner[0].v").Value == "11", "Move did not swap the rows.");
        foreach (var bytes in new[] { TagFieldEdits.InsertRow(document, schema, rows, 2), TagFieldEdits.DeleteRow(document, schema, rows, 1) })
            Check(TagDocument.Parse(bytes, Root, schema).Problems.Count == 0 && BinaryPrimitives.ReadInt64LittleEndian(bytes) == bytes.Length,
                "A resized tag has parse problems or a stale size word.");
        Console.WriteLine("editing values=written rows=insert,duplicate,delete,move pointers=rebased");
    }

    private static TagFieldRow Tree(byte[] tag, RegistryIndex schema) => TagFieldTree.Build(TagDocument.Parse(tag, Root, schema), schema);

    private static TagFieldRow Row(TagFieldRow tree, string path) =>
        tree.Walk().FirstOrDefault(x => x.Path == path) ?? throw new InvalidOperationException($"No row {path}.");

    private static string Value(byte[] tag, RegistryIndex schema, string path) => Row(Tree(tag, schema), path).Value;

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
