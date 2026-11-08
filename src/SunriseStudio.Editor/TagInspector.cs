using System.Buffers.Binary;
using System.Globalization;
using SunriseStudio.Formats;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Editing;

namespace SunriseStudio.Editor;

/**
 * Any tag whose class the registry describes, shown by schema in the Properties page. A class panel
 * adds what the schema cannot show above the tree; the Tag page has the full editor.
 */
public sealed class TagInspector(Func<RegistryIndex?> schema, IReadOnlyList<IAssetInspector>? panels = null) : IAssetInspector
{
    public const string KindName = "Tag";
    /** Rows listed per array until the user shows the rest. */
    public const int RowsPerArray = 256;
    private static readonly string[] Lanes = ["x", "y", "z", "w"];

    public string Id => "sunrise.tag-inspector";
    public string DisplayName => KindName;

    public bool CanInspect(AssetInspectionContext context) => schema()?.Class(context.ClassHash) is not null;

    public AssetInspection Inspect(AssetInspectionContext context)
    {
        var registry = schema() ?? throw new InvalidDataException("The tag registry is not loaded.");
        byte[] bytes = context.Data.ToArray();
        var document = TagDocument.Parse(bytes, context.ClassHash, registry);
        var tree = TagFieldTree.Build(document, registry, context.ResolveName);
        var references = tree.Walk().Where(x => x.Reference is not null)
            .Select(x => new AssetReference(x.Offset, x.Reference!.Value, context.ResolveName?.Invoke(x.Reference.Value) ?? $"{x.Reference:X8}"))
            .ToArray();
        string title = context.Name.Length == 0 ? $"{context.TagHash:X8}" : context.Name;
        var inspection = new AssetInspection(KindName, title, [Node(tree, bytes)], references, document.Problems.ToArray());
        return panels?.FirstOrDefault(x => x.CanInspect(context)) is { } panel ? WithPanel(inspection, panel, context) : inspection;
    }

    /** The panel names the asset kind and its sections come first; a panel that fails leaves its error as a note. */
    private static AssetInspection WithPanel(AssetInspection tree, IAssetInspector panel, AssetInspectionContext context)
    {
        try
        {
            var shown = panel.Inspect(context);
            return tree with
            {
                Kind = shown.Kind, Sections = [.. shown.Sections, .. tree.Sections], Diagnostics = [.. shown.Diagnostics, .. tree.Diagnostics],
                References = shown.References.Concat(tree.References).DistinctBy(x => (x.Offset, x.TagHash)).ToArray(), Graph = shown.Graph,
            };
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException
            or OverflowException or IndexOutOfRangeException)
        {
            return tree with { Kind = panel.DisplayName, Diagnostics = [error.Message, .. tree.Diagnostics] };
        }
    }

    private static AssetNode Node(TagFieldRow row, byte[] bytes)
    {
        var properties = new List<AssetProperty>();
        var children = new List<AssetNode>();
        foreach (var child in row.Children)
        {
            if (child.Edit == TagValueEdit.Vector)
            {
                // The Properties editor writes scalars, so each float lane is its own row.
                int lanes = child.Size / 4;
                for (int lane = 0; lane < lanes; lane++)
                {
                    int at = child.Offset + lane * 4;
                    string name = lanes <= Lanes.Length ? $"{child.Name}.{Lanes[lane]}" : $"{child.Name}[{lane}]";
                    properties.Add(new(name, TagFieldTree.FloatText(bytes.AsSpan(at, 4)), at, ScalarEncoding.Float32, true, child.Type));
                }
                continue;
            }
            if (child.Kind is TagFieldKind.Value or TagFieldKind.String or TagFieldKind.Unknown)
            {
                if (Bits(child, bytes) is { } bits)
                {
                    properties.Add(bits);
                    continue;
                }
                // The inspector panel edits Value as typed, so an editable value carries its note in Evidence instead.
                var encoding = Encoding(child);
                bool editable = encoding != ScalarEncoding.None;
                string value = editable || child.Note.Length == 0 ? child.Value : $"{child.Value}  ({child.Note})";
                string evidence = child.Note.Length == 0 ? child.Type : $"{child.Type}: {child.Note}";
                properties.Add(new(child.Name, value, child.Offset, encoding, editable, evidence));
                continue;
            }
            if (child.Kind == TagFieldKind.Array)
            {
                properties.Add(new(child.Name, child.Value, child.Offset, Evidence: child.Type));
                if (child.Children.Count > 0)
                    children.Add(new(child.Name, [], new LazyRows(child.Children, bytes)) { FoldedRows = RowsPerArray });
                continue;
            }
            children.Add(Node(child, bytes));
        }
        return new(row.Name.Length == 0 ? "root" : row.Name, properties, children);
    }

    /** Array rows built on first read, so a long array costs nothing until it is shown. */
    private sealed class LazyRows(IReadOnlyList<TagFieldRow> rows, byte[] bytes) : IReadOnlyList<AssetNode>
    {
        private readonly AssetNode?[] built = new AssetNode?[rows.Count];
        public int Count => rows.Count;
        public AssetNode this[int index] => built[index] ??= Node(rows[index], bytes);
        public IEnumerator<AssetNode> GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /** A flags or enum field as one unsigned number; the tree shows it as raw bytes. */
    private static AssetProperty? Bits(TagFieldRow row, byte[] bytes)
    {
        if (row.Type is not ("flags" or "enum") || row.Size is not (1 or 2 or 4 or 8)) return null;
        ulong value = row.Size switch
        {
            1 => bytes[row.Offset],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(row.Offset)),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(row.Offset)),
            _ => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(row.Offset)),
        };
        var encoding = row.Size switch
        {
            1 => ScalarEncoding.UInt8, 2 => ScalarEncoding.UInt16, 4 => ScalarEncoding.UInt32, _ => ScalarEncoding.UInt64,
        };
        string text = row.Type == "flags" ? "0x" + value.ToString("X" + row.Size * 2, CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);
        return new(row.Name, text, row.Offset, encoding, true, row.Type);
    }

    /** The Properties editor writes these widths; the Tag page edits every other value. */
    private static ScalarEncoding Encoding(TagFieldRow row) => row.Edit switch
    {
        TagValueEdit.Integer => row.Type switch
        {
            "u8" or "bool8" => ScalarEncoding.UInt8, "i16" => ScalarEncoding.Int16, "u16" => ScalarEncoding.UInt16,
            "i32" => ScalarEncoding.Int32, "u32" or "bool32" => ScalarEncoding.UInt32, "i64" => ScalarEncoding.Int64,
            "u64" => ScalarEncoding.UInt64, _ => ScalarEncoding.None,
        },
        TagValueEdit.Float when row.Size == 4 => ScalarEncoding.Float32,
        TagValueEdit.TagRef or TagValueEdit.Hash when row.Size == 4 => ScalarEncoding.UInt32,
        TagValueEdit.WideRef => ScalarEncoding.UInt64,
        _ => ScalarEncoding.None,
    };
}
