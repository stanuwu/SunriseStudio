using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Editing;
using SunriseStudio.Tags.Graph;
using SunriseStudio.Tags.Query;

namespace SunriseStudio.Editor;

/**
 * Any tag the registry describes, by schema: typed value editors, array row commands, a reference
 * picker, the diff against the installed original and a hex pane on the selection. Every edit goes
 * through the workspace, so it is one undo step stored as a project delta.
 */
public sealed class TagPage : IStudioPage
{
    /** Array rows drawn per page. */
    private const int RowsPerPage = 200;
    /** Hex rows shown above and below the selection; the pane shows at most MaximumHexRows. */
    private const int HexContext = 2, MaximumHexRows = 24;
    /** Query results listed; a query can fan out over every row of every array. */
    private const int MaximumResults = 64;

    private sealed record TreeState(StudioDocument Document, TagDocument Parsed, TagFieldRow Root, Dictionary<string, TagFieldRow> ByPath,
        Dictionary<string, FieldDifference> Differences, HashSet<string> Changed);

    private StudioDocument? requested;
    private Task<TreeState>? building;
    private TreeState? state;
    private string buildError = "";
    private string search = "", matchedSearch = "";
    private TreeState? matchedState;
    private HashSet<string>? matching;
    private bool changedOnly;
    private string? selected;
    private string? reveal;
    private string editText = "", editFor = "";
    private StudioDocument? editDocument;
    private readonly Dictionary<string, int> pages = [];
    private Task<List<QueryNode>>? query;
    private List<QueryNode> queryResults = [];
    private string queryError = "";
    private bool picking;
    private uint? picked;
    private string pickedLabel = "";

    public string Id => "sunrise.tag";
    public string Title => "Tag";
    public bool CanOpen(StudioDocument document) => StudioWorkspace.TagSchema?.Class(document.Entry.Reference) is not null;

    public void Draw(StudioPageContext context)
    {
        var workspace = context.Workspace;
        if (workspace.Document is not { } document || StudioWorkspace.TagSchema is not { } schema) return;
        Refresh(workspace, document, schema);
        if (state is null)
        {
            ImGui.TextDisabled(buildError.Length != 0 ? buildError : "Reading the tag");
            return;
        }
        // While a re-read runs the old tree stays visible, but nothing may be edited against it.
        bool current = ReferenceEquals(state.Document, document);
        DrawSearch(workspace, document, schema);
        var structure = schema.Class(document.Entry.Reference);
        ImGui.TextDisabled($"{structure?.Name} 0x{document.Entry.Reference:X8}   {document.Bytes.Length:N0} bytes   "
            + (state.Differences.Count == 0 ? "matches the installed original" : $"{state.Differences.Count} fields changed"));
        if (state.Parsed.Problems.Count > 0)
        {
            ImGui.TextColored(StudioTheme.Accent, $"{state.Parsed.Problems.Count} parse problems; their bytes show raw.");
            StudioUi.Tip(string.Join("\n", state.Parsed.Problems.Take(12)));
        }
        DrawResults(workspace, document);

        var available = ImGui.GetContentRegionAvail();
        float treeWidth = MathF.Max(240, available.X * 0.58f);
        ImGui.BeginChild("tag-tree", new Vector2(treeWidth, 0), ImGuiChildFlags.ResizeX);
        DrawTree();
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("tag-detail", Vector2.Zero);
        if (selected is not null && state.ByPath.TryGetValue(selected, out var row)) DrawDetail(context, row, schema, current);
        else ImGui.TextDisabled("Select a field");
        ImGui.EndChild();
    }

    /** Starts a parse when the document changes and adopts it when it finishes. */
    private void Refresh(StudioWorkspace workspace, StudioDocument document, RegistryIndex schema)
    {
        if (!ReferenceEquals(requested, document))
        {
            if (requested?.Entry.TagHash != document.Entry.TagHash) { selected = null; pages.Clear(); queryResults = []; queryError = ""; picking = false; }
            requested = document;
            buildError = "";
            building = Task.Run(() => Build(workspace, document, schema));
        }
        if (building is not { IsCompleted: true } done) return;
        building = null;
        try { state = done.GetAwaiter().GetResult(); }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or IOException)
        {
            state = null;
            buildError = "The tag could not be read: " + error.Message;
        }
    }

    private static TreeState Build(StudioWorkspace workspace, StudioDocument document, RegistryIndex schema)
    {
        var parsed = TagDocument.Parse(document.Bytes, document.Entry.Reference, schema);
        var root = TagFieldTree.Build(parsed, schema, workspace.ResolveName, hash => workspace.ResolveHash(hash) is { } text ? [text] : []);
        var byPath = new Dictionary<string, TagFieldRow>(StringComparer.Ordinal);
        foreach (var row in root.Walk()) byPath.TryAdd(row.Path, row);
        var differences = new Dictionary<string, FieldDifference>(StringComparer.Ordinal);
        var changed = new HashSet<string>(StringComparer.Ordinal);
        if (document.AddedAssetId is null && workspace.Project?.ContainsEdit(document.Entry.TagHash) == true && workspace.Catalog is { } catalog)
            foreach (var difference in TagDiff.Compare(catalog.ReadOriginal(document.Entry.TagHash), document.Bytes, document.Entry.Reference, schema))
            {
                // TagDiff paths start with a dot and name an array's count as `.count`.
                string path = difference.Path.TrimStart('.');
                if (path.EndsWith(".count", StringComparison.Ordinal) && !byPath.ContainsKey(path)) path = path[..^".count".Length];
                differences.TryAdd(path, difference);
                foreach (string ancestor in Ancestors(path)) changed.Add(ancestor);
            }
        return new(document, parsed, root, byPath, differences, changed);
    }

    /** The path itself, then each enclosing field, row and array, then the root. */
    private static IEnumerable<string> Ancestors(string path)
    {
        yield return path;
        for (int index = path.Length - 1; index > 0; index--)
            if (path[index] is '.' or '[') yield return path[..index];
        yield return "";
    }

    private void DrawSearch(StudioWorkspace workspace, StudioDocument document, RegistryIndex schema)
    {
        ImGui.SetNextItemWidth(-ImGui.CalcTextSize("Changed").X - 48);
        bool enter = ImGui.InputTextWithHint("##tagSearch", "Find a field, value or string; or a query such as .bubbles[0].name", ref search, 512,
            ImGuiInputTextFlags.EnterReturnsTrue);
        StudioUi.Tip("Text filters the fields by name, value, note or string hash.\nA query starting with ., @ or ##0x runs on Enter.");
        ImGui.SameLine();
        ImGui.Checkbox("Changed", ref changedOnly);
        StudioUi.Tip("Show only fields that differ from the installed original.");
        string text = search.Trim();
        bool isQuery = text.StartsWith('.') || BrowserRowFilter.IsQuery(text);
        if (enter && isQuery) StartQuery(workspace, document, schema, text);
        if (text != matchedSearch || !ReferenceEquals(state, matchedState))
        {
            matchedSearch = text; matchedState = state;
            matching = text.Length == 0 || isQuery ? null : Match(text);
        }
        if (query is { IsCompleted: true } done)
        {
            query = null;
            try { queryResults = done.GetAwaiter().GetResult(); }
            catch (Exception error) when (error is PathQueryException or InvalidDataException or KeyNotFoundException)
            {
                queryResults = []; queryError = error.Message;
            }
        }
    }

    /** Matching rows and their ancestors. Text also matches a hash field holding the text's FNV-1 hash. */
    private HashSet<string> Match(string text)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        string hash = $"0x{StringBankWriter.Hash(text):X8}";
        foreach (var row in state!.Root.Walk())
            if (row.Path.Length > 0 && (row.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || row.Value.Contains(text, StringComparison.OrdinalIgnoreCase)
                || row.Note.Contains(text, StringComparison.OrdinalIgnoreCase) || (row.Edit == TagValueEdit.Hash && row.Value == hash)))
                foreach (string ancestor in Ancestors(row.Path)) result.Add(ancestor);
        return result;
    }

    private void StartQuery(StudioWorkspace workspace, StudioDocument document, RegistryIndex schema, string text)
    {
        queryResults = []; queryError = "";
        if (workspace.Catalog is not { } catalog || document.AddedAssetId is not null)
        {
            queryError = "Queries read installed tags; this asset is not one.";
            return;
        }
        var start = new QueryNode(document.Entry.TagHash, 0, document.Entry.Reference, null, "struct");
        query = Task.Run(() => PathQuery.Parse(text).Evaluate(new QueryContext(new CatalogTagSource(catalog), schema), start));
    }

    private void DrawResults(StudioWorkspace workspace, StudioDocument document)
    {
        if (query is not null) { ImGui.TextDisabled("Running the query"); return; }
        if (queryError.Length != 0) { ImGui.TextColored(StudioTheme.Accent, queryError); return; }
        if (queryResults.Count == 0) return;
        ImGui.TextDisabled($"{queryResults.Count:N0} results");
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear")) { queryResults = []; return; }
        ImGui.BeginChild("tag-results", new Vector2(0, MathF.Min(queryResults.Count, 6) * ImGui.GetTextLineHeightWithSpacing() + 4), ImGuiChildFlags.Borders);
        for (int index = 0; index < Math.Min(queryResults.Count, MaximumResults); index++)
        {
            var node = queryResults[index];
            var row = node.Tag == document.Entry.TagHash ? RowAt(node) : null;
            string label = row is not null ? row.Path : $"0x{node.Tag:X8} +0x{node.Offset:X} {node.Field?.Name ?? node.Type}";
            ImGui.PushID(index);
            if (ImGui.Selectable(label))
            {
                if (row is not null) Reveal(row.Path);
                else workspace.Select(node.Tag);
            }
            ImGui.PopID();
        }
        ImGui.EndChild();
    }

    /** The row a query node names: the struct at its offset, or the field it resolved. */
    private TagFieldRow? RowAt(QueryNode node) => state!.Root.Walk().FirstOrDefault(row => row.Offset == node.Offset
        && (node.Field is null ? row.Kind == TagFieldKind.Struct : row.Name == node.Field.Name));

    /** Selects a row, turns each enclosing array to the page that shows it, and opens the tree down to it. */
    private void Reveal(string path)
    {
        selected = path;
        reveal = path;
        foreach (string ancestor in Ancestors(path))
            if (ancestor.EndsWith(']') && int.TryParse(ancestor[(ancestor.LastIndexOf('[') + 1)..^1], NumberStyles.None, CultureInfo.InvariantCulture, out int row))
                pages[ancestor[..ancestor.LastIndexOf('[')]] = row / RowsPerPage;
    }

    private bool Visible(TagFieldRow row) => (matching?.Contains(row.Path) ?? true) && (!changedOnly || state!.Changed.Contains(row.Path));

    private void DrawTree()
    {
        if (!ImGui.BeginTable("tag-fields", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY
            | ImGuiTableFlags.BordersInnerV, ImGui.GetContentRegionAvail())) return;
        ImGui.TableSetupColumn("Field", ImGuiTableColumnFlags.WidthStretch, 0.45f);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 0.4f);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthStretch, 0.15f);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();
        foreach (var row in state!.Root.Children) DrawRow(row);
        ImGui.EndTable();
        reveal = null;
    }

    private void DrawRow(TagFieldRow row)
    {
        if (!Visible(row)) return;
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        bool leaf = row.Children.Count == 0;
        var flags = ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick;
        if (leaf) flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
        if (selected == row.Path) flags |= ImGuiTreeNodeFlags.Selected;
        // A filter or a reveal opens the rows on the way to what it found.
        if (!leaf && (matching is not null || changedOnly || (reveal is not null && reveal.StartsWith(row.Path, StringComparison.Ordinal) && reveal != row.Path)))
            ImGui.SetNextItemOpen(true);
        bool open = ImGui.TreeNodeEx($"{row.Name}##{row.Path}", flags);
        if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen()) selected = row.Path;
        if (reveal == row.Path) ImGui.SetScrollHereY();
        ImGui.TableNextColumn();
        if (state!.Changed.Contains(row.Path)) ImGui.TextColored(StudioTheme.Accent, row.Value);
        else ImGui.TextUnformatted(row.Value);
        if (row.Note.Length != 0) { ImGui.SameLine(); ImGui.TextDisabled(row.Note); }
        ImGui.TableNextColumn();
        ImGui.TextDisabled(row.Type.Length != 0 ? row.Type : StudioWorkspace.TagSchema?.Class(row.ClassId)?.Name ?? "");
        if (!open || leaf) return;
        if (row.Kind == TagFieldKind.Array && row.Children.Count > RowsPerPage) DrawPagedRows(row);
        else foreach (var child in row.Children) DrawRow(child);
        ImGui.TreePop();
    }

    private void DrawPagedRows(TagFieldRow array)
    {
        int pageCount = (array.Children.Count + RowsPerPage - 1) / RowsPerPage;
        int page = Math.Clamp(pages.GetValueOrDefault(array.Path), 0, pageCount - 1);
        int first = page * RowsPerPage, last = Math.Min(array.Children.Count, first + RowsPerPage);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.PushID(array.Path);
        ImGui.BeginDisabled(page == 0);
        if (ImGui.SmallButton("<")) pages[array.Path] = page - 1;
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled($"rows {first:N0} to {last - 1:N0} of {array.Children.Count:N0}");
        ImGui.SameLine();
        ImGui.BeginDisabled(page == pageCount - 1);
        if (ImGui.SmallButton(">")) pages[array.Path] = page + 1;
        ImGui.EndDisabled();
        ImGui.PopID();
        for (int index = first; index < last; index++) DrawRow(array.Children[index]);
    }

    private void DrawDetail(StudioPageContext context, TagFieldRow row, RegistryIndex schema, bool current)
    {
        var workspace = context.Workspace;
        var document = workspace.Document!;
        ImGui.TextWrapped(row.Path.Length == 0 ? "root" : row.Path);
        ImGui.TextDisabled($"{(row.Type.Length != 0 ? row.Type : "struct")}   offset 0x{row.Offset:X}   {row.Size} bytes");
        if (state!.Differences.TryGetValue(row.Path, out var difference))
            ImGui.TextColored(StudioTheme.Accent, $"Installed: {difference.Left}   now: {difference.Right}");
        ImGui.Separator();
        ImGui.BeginDisabled(!workspace.CanEdit || !current);
        switch (row.Kind)
        {
            case TagFieldKind.Value or TagFieldKind.Unknown when row.Edit != TagValueEdit.None:
                DrawValueEditor(workspace, document, row);
                break;
            case TagFieldKind.Array:
                ImGui.TextDisabled(row.CanResize ? $"{Math.Max(row.Count, 0)} rows" : "This array cannot be resized: it is shared or its rows have no class.");
                if (row.CanResize && ImGui.Button("Add row"))
                    Apply(workspace, () => TagFieldEdits.InsertRow(state.Parsed, schema, row, Math.Max(row.Count, 0)), $"Added a row to {row.Path}");
                break;
            case TagFieldKind.String:
                ImGui.TextDisabled("A string changes the tag length. Edit it in a .tft file.");
                break;
        }
        if (ArrayRow(row) is var (array, index) && array.CanResize)
        {
            ImGui.TextDisabled($"Row {index} of {array.Path}");
            if (ImGui.Button("Insert before")) Apply(workspace, () => TagFieldEdits.InsertRow(state.Parsed, schema, array, index), $"Inserted a row in {array.Path}");
            ImGui.SameLine();
            if (ImGui.Button("Duplicate")) Apply(workspace, () => TagFieldEdits.DuplicateRow(state.Parsed, schema, array, index), $"Duplicated {row.Path}");
            StudioUi.Tip("The copy shares the arrays, objects and strings the row points at.");
            ImGui.SameLine();
            if (ImGui.Button("Delete")) Apply(workspace, () => TagFieldEdits.DeleteRow(state.Parsed, schema, array, index), $"Deleted {row.Path}");
            ImGui.SameLine();
            ImGui.BeginDisabled(index == 0);
            if (ImGui.Button("Up")) Apply(workspace, () => TagFieldEdits.MoveRow(state.Parsed, schema, array, index, -1), $"Moved {row.Path}");
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(index == array.Count - 1);
            if (ImGui.Button("Down")) Apply(workspace, () => TagFieldEdits.MoveRow(state.Parsed, schema, array, index, 1), $"Moved {row.Path}");
            ImGui.EndDisabled();
        }
        ImGui.EndDisabled();
        ImGui.Separator();
        DrawHex(context, document.Bytes, row.Offset, row.Size);
    }

    /** The array a row belongs to and its index, when the row is an array row. */
    private (TagFieldRow Array, int Index)? ArrayRow(TagFieldRow row)
    {
        if (!row.Path.EndsWith(']')) return null;
        int open = row.Path.LastIndexOf('[');
        return state!.ByPath.TryGetValue(row.Path[..open], out var parent) && parent.Kind == TagFieldKind.Array
            && int.TryParse(row.Path[(open + 1)..^1], NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            ? (parent, index) : null;
    }

    private void DrawValueEditor(StudioWorkspace workspace, StudioDocument document, TagFieldRow row)
    {
        // The text reloads when the selection changes or an edit brings a new document.
        if (editFor != row.Path || !ReferenceEquals(editDocument, state!.Document))
        {
            if (editFor != row.Path) picking = false;
            editFor = row.Path;
            editDocument = state!.Document;
            editText = row.Value;
        }
        if (row.Type == "flags" && row.Size is > 0 and <= 8) DrawFlags(workspace, document, row);
        ImGui.SetNextItemWidth(-ImGui.CalcTextSize("Apply").X - 24);
        bool enter = ImGui.InputText("##tagValue", ref editText, 1024, ImGuiInputTextFlags.EnterReturnsTrue);
        StudioUi.Tip(row.Edit switch
        {
            TagValueEdit.Vector => "Numbers separated by commas.",
            TagValueEdit.Float => "A number, or its bits as 0x hex.",
            TagValueEdit.Raw => "Hex bytes; the length stays the same.",
            TagValueEdit.TagRef or TagValueEdit.WideRef or TagValueEdit.Hash => "A 0x hex value.",
            _ => "A whole number, or 0x hex.",
        });
        ImGui.SameLine();
        if (ImGui.Button("Apply") || enter)
        {
            string text = editText;
            Apply(workspace, () => TagFieldEdits.SetValue(document.Bytes, row, text), $"Edited {row.Path}");
        }
        if (row.Edit != TagValueEdit.TagRef) return;
        if (row.Reference is { } target)
        {
            if (ImGui.Button("Open")) workspace.Select(target);
            ImGui.SameLine();
        }
        if (ImGui.Button(picking ? "Close picker" : "Pick")) { picking = !picking; picked = null; pickedLabel = ""; }
        ImGui.SameLine();
        if (ImGui.Button("None")) Apply(workspace, () => TagFieldEdits.SetValue(document.Bytes, row, "0xFFFFFFFF"), $"Cleared {row.Path}");
        if (picking) DrawPicker(workspace, document, row);
    }

    /** One checkbox per bit; a flags field is a little-endian bit set. */
    private static void DrawFlags(StudioWorkspace workspace, StudioDocument document, TagFieldRow row)
    {
        ulong bits = 0;
        for (int index = 0; index < row.Size; index++) bits |= (ulong)document.Bytes[row.Offset + index] << (index * 8);
        for (int bit = 0; bit < row.Size * 8; bit++)
        {
            bool set = (bits >> bit & 1) != 0;
            if (bit % 8 != 0) ImGui.SameLine();
            if (!ImGui.Checkbox($"##bit{bit}", ref set)) continue;
            ulong value = set ? bits | 1UL << bit : bits & ~(1UL << bit);
            string hex = Convert.ToHexString(BitConverter.GetBytes(value), 0, row.Size);
            Apply(workspace, () => TagFieldEdits.SetValue(document.Bytes, row, hex), $"Edited {row.Path}");
        }
    }

    /** Assets of the classes the field names, or of the class it holds now; the choice shows before it is used. */
    private void DrawPicker(StudioWorkspace workspace, StudioDocument document, TagFieldRow row)
    {
        var classes = row.Targets.Count > 0 ? row.Targets.ToHashSet()
            : row.Reference is { } now && workspace.Catalog?.TryGetEntry(now, out var entry) == true ? [entry!.Reference] : null;
        ImGui.TextDisabled(classes is null ? "Any class" : string.Join(", ", classes.Select(x => StudioWorkspace.TagSchema?.Class(x)?.Name ?? $"0x{x:X8}")));
        CreationWidgets.RowPicker("tag-reference", ref picked, ref pickedLabel, workspace.Assets,
            classes is null ? null : asset => classes.Contains(asset.Entry.Reference),
            HashCode.Combine(workspace.AssetRevision, workspace.ProjectRevision, workspace.NameRevision), workspace.IsProjectTag, 180, clearable: false);
        if (picked is not { } choice) return;
        if (workspace.Catalog?.TryGetEntry(choice, out var chosen) == true)
            ImGui.TextDisabled($"{pickedLabel}\n{StudioWorkspace.TagSchema?.Class(chosen!.Reference)?.Name ?? "class"} 0x{chosen!.Reference:X8}   {chosen.PackageName}");
        if (ImGui.Button($"Use 0x{choice:X8}"))
        {
            picking = false;
            Apply(workspace, () => TagFieldEdits.SetValue(document.Bytes, row, $"0x{choice:X8}"), $"Set {row.Path}");
        }
    }

    private static void Apply(StudioWorkspace workspace, Func<byte[]> edit, string action) =>
        workspace.Run(() => workspace.ApplyBytes(edit(), action));

    /** The rows around a byte range, with the range drawn in the accent colour. */
    private static void DrawHex(StudioPageContext context, byte[] bytes, int start, int size)
    {
        if (bytes.Length == 0) return;
        int first = Math.Max(0, start / 16 - HexContext);
        int last = Math.Min((bytes.Length - 1) / 16, (start + Math.Max(size, 1) - 1) / 16 + HexContext);
        last = Math.Min(last, first + MaximumHexRows - 1);
        unsafe { if (context.MonospaceFont.NativePtr != null) ImGui.PushFont(context.MonospaceFont); }
        for (int line = first; line <= last; line++)
        {
            ImGui.TextDisabled($"{line * 16:X8} ");
            for (int column = 0; column < 16 && line * 16 + column < bytes.Length; column++)
            {
                int at = line * 16 + column;
                ImGui.SameLine(0, 0);
                string text = $"{bytes[at]:X2} ";
                if (at >= start && at < start + size) ImGui.TextColored(StudioTheme.Accent, text);
                else ImGui.TextUnformatted(text);
            }
        }
        unsafe { if (context.MonospaceFont.NativePtr != null) ImGui.PopFont(); }
    }
}
