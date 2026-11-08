using System.Buffers.Binary;
using SunriseStudio.Editor;
using SunriseStudio.Formats;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

/** The shipped registry tree with a class panel above it, on synthetic item, material and behavior tags. */
internal static class InspectorPanelChecks
{
    private static readonly int StackAt = TagView.FieldOffset<ItemDefinition>("InventoryItemBlock.MaximumStack");
    private static readonly int BucketAt = TagView.FieldOffset<ItemDefinition>("InventoryItemBlock.InventoryBucket");

    internal static void Run()
    {
        var registry = StudioRegistry.TryIndex(out string? error) ?? throw new InvalidOperationException(error);
        var inspector = new TagInspector(() => registry, new AssetInspectionService().Inspectors);
        Item(inspector);
        Material(inspector);
        Behavior(inspector);
        CuiProgram(inspector);
        Console.WriteLine("editor_inspector_panels item=tree material=panel,lanes,flags behavior=panel,graph cui_program=graph,root_resource");
    }

    /** An item definition has no panel; the tree shows the stack, socket, stat and perk rows the old view listed. */
    private static void Item(TagInspector inspector)
    {
        var bytes = new byte[576];
        U64(bytes, 0, bytes.Length);
        U32(bytes, StackAt, 99); bytes[BucketAt] = 2;
        Object(bytes, 104, 248, 0x808077C1);
        Object(bytes, 112, 288, 0x808077B9);
        ArrayAt(bytes, 248, 336, 1, 0x808077C4); U16(bytes, 352, 7); U16(bytes, 354, 42);
        ArrayAt(bytes, 288, 448, 1, 0x80803033); bytes[464] = 3; U32(bytes, 468, 15);
        ArrayAt(bytes, 304, 520, 1, 0x808077BC); U16(bytes, 536, 88);
        var result = inspector.Inspect(new(0x81323456, 0x80807BEA, bytes));
        Require(result.Kind == TagInspector.KindName && result.Diagnostics.Count == 0, "Item: " + string.Join("; ", result.Diagnostics));
        var fields = Fields(result);
        Require(fields.Any(x => x.Offset == StackAt && x is { Name: "maximum_stack", Value: "99", Editable: true, Encoding: ScalarEncoding.Int32 }), "Item stack is missing.");
        Require(fields.Any(x => x.Offset == BucketAt && x is { Name: "inventory_bucket", Value: "2", Editable: true }), "Item bucket is missing.");
        Require(fields.Any(x => x is { Name: "initial_plug_item", Offset: 354, Value: "42", Editable: true }), "Socket plug is missing.");
        var stat = fields.Single(x => x.Name == "value" && x.Offset == 468);
        Require(stat is { Value: "15", Editable: true }, "Stat value is missing.");
        Require(BinaryPrimitives.ReadInt32LittleEndian(AssetEdit.Apply(bytes, stat, "23").AsSpan(468)) == 23, "Stat edit failed.");
        Require(fields.Any(x => x is { Name: "sandbox_perk_index", Offset: 536, Value: "88", Editable: true }), "Perk index is missing.");
    }

    /** The panel checks the header; the tree edits flags and each constant lane as scalars. */
    private static void Material(TagInspector inspector)
    {
        const int constants = 0x2C8 + 8 + 0x18 + 0x10, header = 1040;
        var bytes = new byte[1072];
        U64(bytes, 0, bytes.Length);
        U32(bytes, 0x20, 0x12345678);
        ArrayAt(bytes, constants, header, 1, 0x80800090);
        for (int lane = 0; lane < 4; lane++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(header + 16 + lane * 4), lane + 0.5f);
        var result = inspector.Inspect(new(0x81323457, 0x808071E8, bytes));
        Require(result.Kind == "Material" && result.Diagnostics.Count == 0, "Material: " + string.Join("; ", result.Diagnostics));
        var fields = Fields(result);
        var flags = fields.Single(x => x.Name == "state_overrides");
        Require(flags is { Value: "0x12345678", Editable: true, Encoding: ScalarEncoding.UInt32 }, "Material flags are not an editable number.");
        var z = fields.Single(x => x.Name == "value.z" && x.Offset == header + 24);
        Require(z is { Value: "2.5", Editable: true, Encoding: ScalarEncoding.Float32 }, "A constant lane is not an editable float.");
        Require(MaterialInspector.Fields(bytes).Constants.Single(x => x.Lane == "Z").Offset == z.Offset, "The material export lane differs from the tree.");
        U64(bytes, 0, 8);
        var refused = inspector.Inspect(new(0x81323457, 0x808071E8, bytes));
        Require(refused.Kind == "Material" && refused.Diagnostics.Contains("Invalid material header.") && refused.Sections.Count == 1,
            "A refused material header hid the tree.");
    }

    /** The behavior panel adds its graph above the tree, which shows the node behind the root array. */
    private static void Behavior(TagInspector inspector)
    {
        var bytes = new byte[128];
        ArrayAt(bytes, 8, 32, 1, 0x80809420);
        U64(bytes, 56, 80 - 56); U32(bytes, 76, 0x80804E31);
        ArrayAt(bytes, 96, 32, 1, 0x80809420);
        var result = inspector.Inspect(new(0x81323458, 0x8080941E, bytes));
        Require(result.Kind == "Behavior" && result.Graph!.Nodes.Any(x => x.Id == "node:80"), "The behavior graph is missing.");
        Require(result.Sections[^1].Name == "object_behavior_root", "The behavior tree is missing.");
    }

    /** The CUI program panel draws the node graph; the tree names the root resource as a reference. */
    private static void CuiProgram(TagInspector inspector)
    {
        var bytes = new byte[80];
        U64(bytes, 0, bytes.Length);
        ArrayAt(bytes, 8, 48, 3, 0x80804616);
        U16(bytes, 24, 3); U16(bytes, 26, 3); U32(bytes, 28, 0x81234567);
        U16(bytes, 64, 0x7FFF); U16(bytes, 66, 0);
        U16(bytes, 68, 0); U16(bytes, 70, 1); U16(bytes, 72, 1); U16(bytes, 74, 2);
        var result = inspector.Inspect(new(0x81323459, 0x8080496A, bytes));
        Require(result.Graph is { Nodes.Count: 3, Edges.Count: 2 }, "The CUI program graph is missing.");
        Require(result.References.Any(x => x.TagHash == 0x81234567 && x.Offset == 28), "The CUI program's root resource is not a reference.");
    }

    private static List<AssetProperty> Fields(AssetInspection inspection)
    {
        static IEnumerable<AssetNode> Nodes(AssetNode node) => new[] { node }.Concat((node.Children ?? []).SelectMany(Nodes));
        return inspection.Sections.SelectMany(Nodes).SelectMany(x => x.Properties).ToList();
    }

    /** A relative pointer at field to an object whose class word sits before body. */
    private static void Object(byte[] bytes, int field, int body, uint type)
    {
        U64(bytes, field, body - field);
        U32(bytes, body - 4, type);
    }

    private static void ArrayAt(byte[] bytes, int field, int header, int count, uint type)
    {
        U64(bytes, field, count); U64(bytes, field + 8, header - field - 8);
        U32(bytes, header - 4, TagArray.Marker); U64(bytes, header, count); U32(bytes, header + 8, type);
    }

    private static void U16(byte[] bytes, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);
    private static void U32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    private static void U64(byte[] bytes, int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
