/**
 * File: MCP tools over the decompiled game code (decomp_status, search_functions, get_function, xrefs_*, find_string,
 * grep_decomp, get_struct, get_vtables, get_class, tag_type_handlers, rename_function) and export_data; indexes are
 * in Decomp/.
 */
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Solforge.Mcp.Data;
using Solforge.Mcp.Decomp;

namespace Solforge.Mcp;

/**
 * The deep-data tools: the decompiled destiny2.exe (functions, C source, calls and data xrefs, strings, vtables, struct
 * layouts, tag class handlers) and raw package data (tag bytes, generic tag layouts and per-class schemas, localized
 * strings), plus export_data, which pages any read tool to a JSON or CSV file. Plain MCP: JSON-schema inputs, text
 * output, cursor paging (next_cursor). None reads what the user looks at, so they work while context is paused.
 */
public static partial class SolforgeTools
{
    public static readonly string[] DecompToolNames =
    [
        "decomp_status", "search_functions", "get_function", "xrefs_to", "xrefs_from", "find_string", "grep_decomp", "get_struct",
        "get_vtables", "get_class", "tag_type_handlers", "rename_function"
    ];

    public static readonly string[] PackageDataToolNames =
    [
        "get_string", "search_strings", "read_string_bank", "list_tag_classes", "list_tags_of_class", "read_tag_bytes", "dump_tag", "infer_tag_schema", "export_data"
    ];

    /** Error shown when no decompiled-code folder can be found. */
    private const string NoDecomp = "No decompiled code found. Set SOLFORGE_DECOMP to the analysis folder (the one holding decomp\\all\\decomp_*.c), " +
        "or keep it at <sol>\\04-reverse-engineering\\analysis above the project or Solforge folder.";

    /** Where export_data writes: <project>\exports, else %LOCALAPPDATA%\Solforge\exports. */
    public static string ExportFolder(string? projectRoot) => projectRoot is { Length: > 0 } && Directory.Exists(projectRoot)
        ? Path.Combine(projectRoot, "exports")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Solforge", "exports");

    /** Locates the decomp folder (project, SDK roots, reference folder, app folder, or SOLFORGE_DECOMP), or null. */
    public static string? DecompRoot(ISolforgeHost host) =>
        DecompIndex.Locate([host.ProjectRoot, .. host.SdkRoots, host.ReferenceFolder, AppContext.BaseDirectory, Environment.CurrentDirectory]);

    /** Yields the decomp and data-export tools. */
    private static IEnumerable<McpTool> DataTools(ISolforgeHost host)
    {
        /** Schema with the standard paging arguments added. */
        static JsonObject Paged(params (string, string, string, bool)[] properties) => McpJson.Schema([.. properties, .. DataPages.Args]);
        /** String argument or empty text. */
        static string A(JsonObject args, string name) => McpJson.String(args, name) ?? "";

        McpTool Code(string name, string description, JsonObject schema, Func<DecompIndex, JsonObject, CancellationToken, McpToolResult> run) =>
            new(name, description, schema, false, async (args, cancel) =>
            {
                if (DecompRoot(host) is not { } root) return McpToolResult.Error(NoDecomp);
                var index = await DecompIndex.LoadAsync(root, cancel).ConfigureAwait(false);
                return await Task.Run(() =>
                {
                    _ = index.Classes; // the class layer (Class::vfN names): built once per image and cached
                    return run(index, args, cancel);
                }, cancel).ConfigureAwait(false);
            });
        McpTool Pkg(string name, string description, JsonObject schema, Func<ISolforgeDataHost, JsonObject, CancellationToken, Task<McpToolResult>> run) =>
            new(name, description, schema, false, async (args, cancel) =>
            {
                if (host is not ISolforgeDataHost data) return McpToolResult.Error("This Solforge host has no package data access.");
                if (data.DataUnavailable is { } reason) return McpToolResult.Error(reason);
                return await run(data, args, cancel).ConfigureAwait(false);
            });

        // ---- decompiled code ----

        yield return Code("decomp_status", "The decompiled-code layer: analysis folder, function count, named functions, failed " +
            "decompiles, whether the memory image (strings, vtables) and C/C++ source roots (struct definitions) are present. Call first.",
            McpJson.Schema(), (index, _, _) => McpToolResult.Json(new JsonObject
            {
                ["root"] = index.Root, ["files"] = index.Files.Count, ["functions"] = index.Functions.Count,
                ["named"] = index.Names.Count(), ["failed"] = index.Failed.Count, ["tag_classes_referenced"] = index.TagClasses.Count(),
                ["image"] = File.Exists(Path.Combine(index.Root, "image", "destiny2_image.bin")),
                ["source_roots"] = new JsonArray([.. index.SourceRoots.Select(root => (JsonNode?)root)]),
                ["names_file"] = Path.Combine(index.Root, DecompIndex.NamesFile),
                ["classes"] = index.Classes is { } classes ? new JsonObject
                {
                    ["rtti_classes"] = classes.RttiClasses, ["rtti_vtables"] = classes.Classes.Where(c => c.Rtti).Sum(c => c.VTables.Length),
                    ["vtables"] = classes.VTableCount, ["virtual_functions"] = classes.Virtuals.Count, ["named_virtuals"] = index.VirtualNameCount,
                    ["classes_with_fields"] = classes.Classes.Count(c => c.Fields.Length > 0)
                } : null,
                ["note"] = "Addresses are RVAs (offset from the image start); FUN_00E2DBE0 is the function at 0xE2DBE0. Build 86657."
            }));

        yield return Code("search_functions", "Find decompiled functions by name (from NOTES.md, listings and solforge-names.csv), " +
            "RVA ('0xE2DBE0', 'E2DB', 'FUN_00E2DBE0') or signature text ('undefined8 *param_1'). Named functions first. " +
            "For text inside function bodies use grep_decomp.",
            Paged(("query", "string", "Name, RVA or signature text", true)),
            (index, args, _) => McpToolResult.Json(DataPages.Page(index.Search(A(args, "query")), args, function => FunctionRow(index, function))));

        yield return Code("get_function", "A function's decompiled C source (Ghidra, build 86657) with its RVA, size, name, signature, " +
            "caller count and callees. Long functions page by line: pass next_cursor back as cursor. An address inside a function " +
            "resolves to the containing function.",
            McpJson.Schema(("function", "string", "RVA ('0xE2DBE0'), FUN_ name or known name", true),
                ("cursor", "string", "First source line to return (from next_cursor; default 0)", false),
                ("max_lines", "integer", "Source lines per page (default 400, max 2000)", false)),
            (index, args, _) =>
            {
                if (index.Resolve(A(args, "function")) is not { } rva) return McpToolResult.Error("Give a function RVA (0xE2DBE0), FUN_ name or known name.");
                if (index.Containing(rva) is not { } function)
                    return McpToolResult.Error(index.Failed.Contains(rva)
                        ? $"0x{rva:X8} did not decompile (FAILED.txt); read its assembly in full\\text\\*.asm."
                        : $"No decompiled function at or around 0x{rva:X8}.");
                string[] lines = index.Source(function).Split('\n');
                int start = Math.Clamp(DataPages.Start(args), 0, Math.Max(0, lines.Length - 1));
                int count = Math.Clamp(McpJson.Int(args, "max_lines", 400), 1, 2000);
                int end = Math.Min(lines.Length, start + count);
                var text = new StringBuilder();
                text.AppendLine($"function: {index.Label(function.Rva)}");
                text.AppendLine($"rva: 0x{function.Rva:X8}  size: {function.Size}  callers: {index.CallerCount(function.Rva)}  name source: {index.NameSource(function.Rva) ?? "none"}");
                text.AppendLine($"signature: {function.Signature}");
                var callees = index.CallsFrom(function);
                text.AppendLine($"calls ({callees.Count}): " + string.Join(", ", callees.Take(40).Select(index.Label)) + (callees.Count > 40 ? ", ..." : ""));
                var classes = index.TagClassesOf(function);
                if (classes.Count > 0) text.AppendLine("tag classes: " + string.Join(", ", classes.Select(type => $"0x{type:X8}")));
                text.AppendLine($"lines {start}-{end - 1} of {lines.Length}" + (end < lines.Length ? $"  next_cursor: {end}" : "  (end)"));
                text.AppendLine("----");
                for (int i = start; i < end; i++) text.AppendLine(lines[i]);
                return McpToolResult.Text(text.ToString());
            });

        yield return Code("xrefs_to", "Everything that references an address: functions that call it, functions whose C names it as " +
            "data (DAT_/UNK_/PTR_/LAB_), and vtables that contain it. Use on a function, a global or a string RVA from find_string.",
            Paged(("address", "string", "RVA, FUN_/DAT_ name or known function name", true),
                ("kind", "string", "calls, data, vtables or all (default all)", false)),
            (index, args, _) =>
            {
                if (index.Resolve(A(args, "address")) is not { } rva) return McpToolResult.Error("Give an RVA such as 0x01C277D0.");
                string kind = (McpJson.String(args, "kind") ?? "all").ToLowerInvariant();
                var rows = new List<JsonObject>();
                if (kind is "all" or "calls") rows.AddRange(index.CallersOf(rva).Select(function => Ref("call", function)));
                if (kind is "all" or "data") rows.AddRange(index.UsersOf(rva).Select(function => Ref("data", function)));
                if (kind is "all" or "vtables" && index.Image is { } image)
                    rows.AddRange(image.VTablesWith(rva).Select(table => new JsonObject
                        { ["kind"] = "vtable", ["rva"] = Hex(table.Rva), ["slot"] = Array.IndexOf(table.Entries, rva), ["entries"] = table.Entries.Length }));
                var page = DataPages.Page(rows, args, row => row);
                page["address"] = index.Label(rva);
                if (index.Image?.StringAt(rva) is { } text) page["string"] = text.Text;
                return McpToolResult.Json(page);
                JsonObject Ref(string type, DecompFunction function) => new()
                    { ["kind"] = type, ["rva"] = Hex(function.Rva), ["name"] = index.NameOf(function.Rva), ["signature"] = function.Signature };
            });

        yield return Code("xrefs_from", "What a function references: callees (with names and signatures), data addresses (with the " +
            "string at each when there is one, and vtables) and Tiger tag class constants it compares against.",
            Paged(("function", "string", "RVA, FUN_ name or known name", true), ("kind", "string", "calls, data, tag_classes or all (default all)", false)),
            (index, args, _) =>
            {
                if (index.Resolve(A(args, "function")) is not { } rva || index.Containing(rva) is not { } function)
                    return McpToolResult.Error("No decompiled function there.");
                string kind = (McpJson.String(args, "kind") ?? "all").ToLowerInvariant();
                var image = index.Image;
                var rows = new List<JsonObject>();
                if (kind is "all" or "calls")
                    rows.AddRange(index.CallsFrom(function).Select(target => new JsonObject
                        { ["kind"] = "call", ["rva"] = Hex(target), ["name"] = index.NameOf(target), ["signature"] = index.Get(target)?.Signature }));
                if (kind is "all" or "data")
                    rows.AddRange(index.RefsFrom(function).Select(target => new JsonObject
                    {
                        ["kind"] = "data", ["rva"] = Hex(target), ["string"] = image?.StringAt(target)?.Text,
                        ["vtable"] = image?.VTableAt(target) is { } table ? table.Entries.Length : null,
                        ["function"] = index.Get(target) is not null ? index.Label(target) : null
                    }));
                if (kind is "all" or "tag_classes")
                    rows.AddRange(index.TagClassesOf(function).Select(type => new JsonObject { ["kind"] = "tag_class", ["class"] = Hex(type) }));
                var page = DataPages.Page(rows, args, row => row);
                page["function"] = index.Label(function.Rva);
                return McpToolResult.Json(page);
            });

        yield return Code("find_string", "Search strings in the dumped destiny2.exe image (ASCII and UTF-16, 5+ characters): log " +
            "formats, state names, asset paths, error text. Each match gives its RVA and the functions that reference it, so you can " +
            "go from a log line to the code that prints it.",
            Paged(("query", "string", "Text to find (case-insensitive substring)", true), ("exact", "boolean", "Whole string must equal the query", false),
                ("xrefs", "boolean", "Include referencing functions (default true)", false)),
            (index, args, cancel) =>
            {
                if (index.Image is not { } image) return McpToolResult.Error("No image\\destiny2_image.bin in the analysis folder.");
                string query = A(args, "query");
                if (query.Length == 0) return McpToolResult.Error("Give text to find.");
                bool exact = McpJson.Bool(args, "exact", false), xrefs = McpJson.Bool(args, "xrefs", true);
                var matches = image.Strings.Where(item => exact ? string.Equals(item.Text, query, StringComparison.OrdinalIgnoreCase)
                    : item.Text.Contains(query, StringComparison.OrdinalIgnoreCase));
                return McpToolResult.Json(DataPages.Page(matches, args, item =>
                {
                    cancel.ThrowIfCancellationRequested();
                    var row = new JsonObject { ["rva"] = Hex(item.Rva), ["text"] = item.Text.Length > 300 ? item.Text[..300] + "..." : item.Text, ["wide"] = item.Wide };
                    if (xrefs) row["referenced_by"] = new JsonArray([.. index.UsersOf(item.Rva).Take(20).Select(function => (JsonNode?)index.Label(function.Rva))]);
                    return row;
                }));
            });

        yield return Code("grep_decomp", "Full-text search inside every decompiled function body (case-insensitive), e.g. a constant " +
            "'0x390', a callee 'FUN_00C26430' or a pattern '+ 0x1848'. Slow (reads all decomp files, a few seconds); results page by cursor.",
            Paged(("text", "string", "Text to find", true)),
            (index, args, cancel) =>
            {
                string text = A(args, "text");
                if (text.Length < 3) return McpToolResult.Error("Give at least 3 characters.");
                return McpToolResult.Json(DataPages.Page(index.Grep(text, cancel), args, hit => new JsonObject
                    { ["rva"] = Hex(hit.Function.Rva), ["name"] = index.NameOf(hit.Function.Rva), ["line"] = hit.Line }, countAll: false));
            });

        yield return Code("get_struct", "A struct layout. With 'name': image classes (RTTI or vtable_RVA, see get_class) with fields inferred from their methods' " +
            "this-pointer accesses, then struct/class definitions in the Sunrise client and server C/C++ " +
            "source (SOLFORGE_SOURCE_ROOTS adds folders). With 'function' (and optional 'variable', e.g. param_1): the fields that function's " +
            "decompiled C reads or writes through that pointer, by offset and type; without 'variable' it lists the candidate variables.",
            Paged(("name", "string", "Struct or class name (substring)", false), ("function", "string", "Function RVA or name to infer from", false),
                ("variable", "string", "Pointer variable in that function, e.g. param_1 or lVar3", false)),
            (index, args, cancel) =>
            {
                if (McpJson.String(args, "function") is { Length: > 0 } functionText)
                {
                    if (index.Resolve(functionText) is not { } rva || index.Containing(rva) is not { } function) return McpToolResult.Error("No decompiled function there.");
                    string source = index.Source(function);
                    if (McpJson.String(args, "variable") is not { Length: > 0 } variable)
                        return McpToolResult.Json(new JsonObject
                        {
                            ["function"] = index.Label(function.Rva), ["signature"] = function.Signature,
                            ["variables"] = new JsonArray([.. StructLayouts.Variables(source).Select(v => (JsonNode?)v)]),
                            ["next"] = "call again with variable"
                        });
                    var fields = StructLayouts.Infer(source, variable);
                    var page = DataPages.Page(fields, args, field => new JsonObject { ["offset"] = Hex(field.Offset), ["type"] = field.Type, ["uses"] = field.Uses }, fallback: 200);
                    page["function"] = index.Label(function.Rva); page["variable"] = variable;
                    return McpToolResult.Json(page);
                }
                if (McpJson.String(args, "name") is not { Length: > 0 } name) return McpToolResult.Error("Give name, or function (and variable).");
                var roots = index.SourceRoots;
                var classLayer = index.Classes;
                if (roots.Count == 0 && classLayer is null) return McpToolResult.Error("No C/C++ source roots found; set SOLFORGE_SOURCE_ROOTS.");
                // Image classes (RTTI names or vtable_RVA) with fields inferred from their methods' this-pointer accesses, then source definitions.
                var inferredClasses = (classLayer?.Search(name) ?? Enumerable.Empty<ClassInfo>()).Where(c => c.Fields.Length > 0).Take(20)
                    .Select(c => new JsonObject
                    {
                        ["kind"] = "inferred", ["name"] = c.Name, ["rtti"] = c.Rtti, ["vtable"] = c.VTables.Length > 0 ? Hex(c.VTables[0].Rva) : null,
                        ["fields"] = new JsonArray([.. c.Fields.Select(f => (JsonNode?)new JsonObject { ["offset"] = Hex(f.Offset), ["type"] = f.Type, ["uses"] = f.Uses })])
                    });
                var definitions = StructLayouts.FindDefinitions(roots, name, cancel).Select(found => new JsonObject
                    { ["kind"] = "source", ["name"] = found.Name, ["file"] = found.File, ["line"] = found.Line, ["text"] = found.Text.Length > 6000 ? found.Text[..6000] + "\n..." : found.Text });
                return McpToolResult.Json(DataPages.Page(inferredClasses.Concat(definitions), args, row => row, fallback: 5));
            });

        yield return Code("get_vtables", "Vtables: MSVC RTTI vtables (class name, subobject offset) plus runs of code pointers in the image's data " +
            "sections, split where the decompiled code references a table start. With 'address' of a vtable: its entries with names and " +
            "owning class. With 'function': the vtables that contain it and its slot. With neither: every vtable with its class, paged.",
            Paged(("address", "string", "Vtable RVA", false), ("function", "string", "Function RVA or name", false)),
            (index, args, _) =>
            {
                if (index.Image is not { } image) return McpToolResult.Error("No image\\destiny2_image.bin in the analysis folder.");
                var classes = index.Classes;
                if (index.Resolve(McpJson.String(args, "address")) is { } address)
                {
                    var owner = classes?.VTableAt(address);
                    uint[]? entries = owner?.Table.Entries ?? image.VTableAt(address)?.Entries;
                    if (entries is null) return McpToolResult.Error($"No vtable starts at 0x{address:X8}.");
                    var page = DataPages.Page(entries.Select((entry, slot) => (entry, slot)), args, item => new JsonObject
                    {
                        ["slot"] = item.slot, ["offset"] = Hex(item.slot * 8), ["rva"] = Hex(item.entry), ["name"] = index.NameOf(item.entry),
                        ["owner"] = classes?.Virtuals.GetValueOrDefault(item.entry) is { } virtualOwner ? (virtualOwner.Shared ? "shared" : virtualOwner.Class) : null,
                        ["signature"] = index.Get(item.entry)?.Signature
                    }, fallback: 200);
                    page["vtable"] = Hex(address);
                    if (owner is { } found)
                    {
                        page["class"] = found.Class.Name; page["rtti"] = found.Class.Rtti; page["subobject_offset"] = Hex(found.Table.Offset);
                        page["bases"] = new JsonArray([.. found.Class.Bases.Select(b => (JsonNode?)b.Name)]);
                    }
                    page["referenced_by"] = new JsonArray([.. index.UsersOf(address).Take(20).Select(f => (JsonNode?)index.Label(f.Rva))]);
                    return McpToolResult.Json(page);
                }
                if (index.Resolve(McpJson.String(args, "function")) is { } function)
                {
                    if (classes is null)
                        return McpToolResult.Json(DataPages.Page(image.VTablesWith(function), args, table => new JsonObject
                            { ["rva"] = Hex(table.Rva), ["entries"] = table.Entries.Length, ["slot"] = Array.IndexOf(table.Entries, function) }));
                    return McpToolResult.Json(DataPages.Page(classes.Holding(function), args, hit => new JsonObject
                    {
                        ["rva"] = Hex(hit.Table.Rva), ["class"] = hit.Class.Name, ["rtti"] = hit.Class.Rtti, ["subobject_offset"] = Hex(hit.Table.Offset),
                        ["slot"] = hit.Slot, ["entries"] = hit.Table.Entries.Length
                    }));
                }
                if (classes is null)
                    return McpToolResult.Json(DataPages.Page(image.VTables, args, table => new JsonObject
                        { ["rva"] = Hex(table.Rva), ["entries"] = table.Entries.Length, ["users"] = index.UsersOf(table.Rva).Count() }));
                return McpToolResult.Json(DataPages.Page(classes.VTables, args, table => new JsonObject
                {
                    ["rva"] = Hex(table.Rva), ["class"] = classes.VTableAt(table.Rva)?.Class.Name, ["subobject_offset"] = Hex(table.Offset),
                    ["entries"] = table.Entries.Length, ["users"] = index.UsersOf(table.Rva).Count()
                }));
            });

        yield return Code("get_class", "A C++ class from the image: MSVC RTTI name, base classes (depth 1 = direct) and derived classes, " +
            "its vtables, virtual methods by slot (Class::vfN unless named; 'inherited' when a base owns the slot), constructors (functions " +
            "that store the vtable), fields inferred from this-pointer (param_1) accesses in its methods and constructors, the Tiger tag " +
            "classes those functions check, and matching source definitions. 'class' takes a name (substring), a vtable RVA or a method; " +
            "without it, every class (query filters, rtti=true for RTTI classes only), paged.",
            Paged(("class", "string", "Class name (e.g. hkpEntity), vtable RVA, or a virtual method's RVA or name", false),
                ("query", "string", "Name filter when listing", false), ("rtti", "boolean", "List only classes with RTTI names", false),
                ("source", "boolean", "Also look up struct/class definitions in the C/C++ source roots (default true)", false)),
            (index, args, cancel) =>
            {
                if (index.Classes is not { } classes) return McpToolResult.Error("No image\\destiny2_image.bin in the analysis folder, so no class layer.");
                var labels = (host as ISolforgeDataHost)?.ClassLabels;
                string text = A(args, "class").Trim();
                if (text.Length == 0)
                {
                    string query = A(args, "query");
                    bool rttiOnly = McpJson.Bool(args, "rtti", false);
                    var list = (query.Length > 0 ? classes.Search(query) : classes.Classes.OrderBy(c => !c.Rtti).ThenBy(c => c.Name, StringComparer.Ordinal))
                        .Where(c => !rttiOnly || c.Rtti);
                    var listing = DataPages.Page(list, args, c => new JsonObject
                    {
                        ["name"] = c.Name, ["rtti"] = c.Rtti, ["vtable"] = c.VTables.Length > 0 ? Hex(c.VTables[0].Rva) : null,
                        ["bases"] = c.Bases.Length, ["methods"] = c.VTables.Length > 0 ? c.VTables[0].Entries.Length : 0, ["fields"] = c.Fields.Length
                    });
                    listing["rtti_classes"] = classes.RttiClasses; listing["vtables"] = classes.VTableCount;
                    return McpToolResult.Json(listing);
                }
                ClassInfo? info = classes.Search(text).FirstOrDefault();
                if (info is null && index.Resolve(text) is { } rva)
                    info = classes.VTableAt(rva)?.Class ?? classes.Holding(rva).OrderBy(hit => hit.Class.Bases.Length).Select(hit => hit.Class).FirstOrDefault();
                if (info is null) return McpToolResult.Error($"No class named or holding '{text}'. List with get_class (query=...).");
                var primary = info.VTables.FirstOrDefault(table => table.Offset == 0) ?? info.VTables.FirstOrDefault();
                var methods = new JsonArray();
                if (primary is not null)
                    for (int slot = 0; slot < primary.Entries.Length; slot++)
                    {
                        uint entry = primary.Entries[slot];
                        var owner = classes.Virtuals.GetValueOrDefault(entry);
                        methods.Add(new JsonObject
                        {
                            ["slot"] = slot, ["rva"] = Hex(entry), ["name"] = index.NameOf(entry), ["signature"] = index.Get(entry)?.Signature,
                            ["owner"] = owner is null ? null : owner.Shared ? "shared" : owner.Class,
                            ["inherited"] = owner is { Shared: false } && owner.Class != info.Name
                        });
                    }
                var result = new JsonObject
                {
                    ["name"] = info.Name, ["mangled"] = info.Mangled, ["rtti"] = info.Rtti,
                    ["type_descriptor"] = info.Rtti ? Hex(info.TypeDescriptor) : null, ["attributes"] = info.Rtti ? Hex(info.Attributes) : null,
                    ["bases"] = new JsonArray([.. info.Bases.Select(b => (JsonNode?)new JsonObject
                        { ["name"] = b.Name, ["depth"] = b.Depth, ["mdisp"] = Hex(b.Mdisp), ["pdisp"] = b.Pdisp, ["vdisp"] = b.Vdisp, ["virtual"] = b.Pdisp >= 0 })]),
                    ["derived"] = new JsonArray([.. classes.DerivedFrom(info).Take(100).Select(d => (JsonNode?)d.Name)]),
                    ["vtables"] = new JsonArray([.. info.VTables.Select(t => (JsonNode?)new JsonObject
                        { ["rva"] = Hex(t.Rva), ["locator"] = t.Locator != 0 ? Hex(t.Locator) : null, ["subobject_offset"] = Hex(t.Offset), ["entries"] = t.Entries.Length })]),
                    ["methods"] = methods,
                    ["constructors"] = new JsonArray([.. info.Constructors.Select(c => (JsonNode?)index.Label(c))]),
                    ["fields"] = new JsonArray([.. info.Fields.Select(f => (JsonNode?)new JsonObject
                        { ["offset"] = Hex(f.Offset), ["type"] = f.Offset == 0 && info.VTables.Length > 0 ? "vftable* (" + f.Type + ")" : f.Type, ["uses"] = f.Uses })]),
                    ["tag_classes"] = new JsonArray([.. info.TagClasses.Select(t => (JsonNode?)new JsonObject { ["class"] = Hex(t), ["label"] = labels?.GetValueOrDefault(t) })]),
                    ["note"] = "Fields are merged from param_1 (this) accesses in the class's own virtual methods and constructors; offsets are from the object start."
                };
                if (McpJson.Bool(args, "source", true) && info.Rtti && index.SourceRoots is { Count: > 0 } roots)
                {
                    string shortName = info.Name.Split("::")[^1];
                    int open = shortName.IndexOf('<');
                    if (open > 0) shortName = shortName[..open];
                    result["source_definitions"] = new JsonArray([.. StructLayouts.FindDefinitions(roots, shortName, cancel)
                        .Where(found => string.Equals(found.Name, shortName, StringComparison.Ordinal)).Take(5)
                        .Select(found => (JsonNode?)new JsonObject { ["file"] = found.File, ["line"] = found.Line })]);
                }
                return McpToolResult.Json(result);
            });

        yield return Code("tag_type_handlers", "Links a Tiger tag class (e.g. 0x80809FBD, the 'class_...' or label from list_tag_classes) " +
            "to the exe functions that compare against that class hash, and to Sunrise client/server source lines that name it. Without " +
            "'class': every class hash the decompiled code uses, with function counts.",
            Paged(("class", "string", "Tag class hash, e.g. 0x8080891E", false), ("source", "boolean", "Also grep the C/C++ source roots (default true)", false)),
            (index, args, cancel) =>
            {
                string text = A(args, "class");
                if (text.Length == 0)
                    return McpToolResult.Json(DataPages.Page(index.TagClasses, args, t => new JsonObject
                    {
                        ["class"] = Hex(t), ["label"] = (host as ISolforgeDataHost)?.ClassLabels.GetValueOrDefault(t), ["functions"] = index.UsersOfTagClass(t).Count()
                    }));
                if (!DecompIndex.TryHex(text.Replace("class_", "", StringComparison.OrdinalIgnoreCase), out uint type)) return McpToolResult.Error("Give a class hash like 0x80809FBD.");
                var rows = index.UsersOfTagClass(type).Select(function => new JsonObject
                    { ["kind"] = "function", ["rva"] = Hex(function.Rva), ["name"] = index.NameOf(function.Rva), ["signature"] = function.Signature }).ToList();
                if (McpJson.Bool(args, "source", true))
                {
                    string[] needles = [$"0x{type:X8}", $"0x{type:x8}", $"{type:X8}", $"{type:x8}"];
                    rows.AddRange(StructLayouts.Grep(index.SourceRoots, [.. needles.Distinct()], cancel).Take(200).Select(hit => new JsonObject
                        { ["kind"] = "source", ["file"] = hit.File, ["line"] = hit.Line, ["text"] = hit.Text }));
                }
                var page = DataPages.Page(rows, args, row => row);
                page["class"] = Hex(type);
                page["label"] = (host as ISolforgeDataHost)?.ClassLabels.GetValueOrDefault(type);
                return McpToolResult.Json(page);
            });

        yield return new McpTool("rename_function", "Name a decompiled function (saved to solforge-names.csv in the analysis folder, " +
            "shared by every agent and the Decompiled code window).",
            McpJson.Schema(("function", "string", "RVA or current name", true), ("name", "string", "New name, e.g. world_controller_goal_allowed", true)), true,
            async (args, cancel) =>
            {
                if (DecompRoot(host) is not { } root) return McpToolResult.Error(NoDecomp);
                var index = await DecompIndex.LoadAsync(root, cancel).ConfigureAwait(false);
                if (index.Resolve(A(args, "function")) is not { } rva) return McpToolResult.Error("Give a function RVA.");
                string name = A(args, "name").Trim();
                if (name.Length == 0 || name.Any(char.IsWhiteSpace)) return McpToolResult.Error("Give a name without spaces.");
                if (!await host.ApproveAsync("rename_function", $"Name 0x{rva:X8} '{name}'", cancel).ConfigureAwait(false))
                    return McpToolResult.Error("The user declined this in Solforge.");
                index.Rename(rva, name);
                return McpToolResult.Text($"0x{rva:X8} is now {name}.");
            });

        // ---- package data ----

        yield return Pkg("get_string", "Localized text for a string hash (u32, as found in tag bytes or item definitions). Ambiguous hashes " +
            "list every candidate. The first call loads all string banks (can take a while).",
            McpJson.Schema(("hash", "string", "String hash, hex (0x...) or decimal", true)),
            async (data, args, cancel) =>
            {
                if (ParseHash(McpJson.String(args, "hash")) is not { } hash) return McpToolResult.Error("Give a string hash.");
                var texts = await data.StringsAsync(hash, cancel).ConfigureAwait(false);
                return McpToolResult.Json(new JsonObject { ["hash"] = Hex(hash), ["texts"] = new JsonArray([.. texts.Select(text => (JsonNode?)text)]) });
            });

        yield return Pkg("search_strings", "Search all localized strings (English) by text; returns hash, text and the string bank tag.",
            Paged(("query", "string", "Text to find (case-insensitive)", true)),
            async (data, args, cancel) =>
            {
                string query = A(args, "query");
                if (query.Length < 2) return McpToolResult.Error("Give at least 2 characters.");
                var hits = await data.SearchStringsAsync(query, DataPages.Start(args) + DataPages.Limit(args) + 1, cancel).ConfigureAwait(false);
                return McpToolResult.Json(DataPages.Page(hits, args, hit => new JsonObject
                    { ["hash"] = Hex(hit.Hash), ["text"] = hit.Text, ["bank"] = hit.Container is { } bank ? Hex(bank) : null }, countAll: false));
            });

        yield return Pkg("read_string_bank", "Every string of one string bank (class 0x80809A88 container, e.g. a socket-entry " +
            "list's text bank): role hash and English text, in bank order. Reads the project's bytes unless original is true.",
            McpJson.Schema(("bank", "string", "Bank container tag (0x...)", true), ("original", "boolean", "Shipped bytes", false)),
            (data, args, cancel) =>
            {
                if (ParseHash(McpJson.String(args, "bank")) is not { } bank) return Task.FromResult(McpToolResult.Error("Give the bank tag."));
                bool original = args["original"] is JsonValue flag && flag.TryGetValue(out bool value) && value;
                if (data.ReadTag(bank, original) is not { Length: >= 0x1C } container)
                    return Task.FromResult(McpToolResult.Error($"0x{bank:X8} is not a readable string bank."));
                uint dataTag = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(0x18));
                if (data.ReadTag(dataTag, original) is not { } bytes)
                    return Task.FromResult(McpToolResult.Error($"The bank's English data 0x{dataTag:X8} is missing."));
                try
                {
                    var rows = SunriseStudio.Packages.StringBankWriter.Read(container, bytes);
                    return Task.FromResult(McpToolResult.Json(new JsonObject
                    {
                        ["bank"] = Hex(bank), ["data"] = Hex(dataTag), ["count"] = rows.Count,
                        ["strings"] = new JsonArray([.. rows.Select(row => (JsonNode?)new JsonObject { ["hash"] = Hex(row.Hash), ["text"] = row.Text })])
                    }));
                }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException or IndexOutOfRangeException)
                {
                    return Task.FromResult(McpToolResult.Error($"0x{bank:X8} could not be read as a string bank: {error.Message}"));
                }
            });

        yield return Pkg("list_tag_classes", "Every tag class (reference hash) in the installation with its tag count and Solforge's label " +
            "when it has one. Unlabelled classes are what Solforge has no decoder for: use infer_tag_schema on them.",
            Paged(("filter", "string", "Optional text the hash or label must contain", false), ("unlabelled", "boolean", "Only classes without a label", false)),
            (data, args, _) =>
            {
                string filter = A(args, "filter");
                bool unlabelled = McpJson.Bool(args, "unlabelled", false);
                var rows = data.ClassCounts().OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key)
                    .Select(pair => (Class: pair.Key, Count: pair.Value, Label: data.ClassLabels.GetValueOrDefault(pair.Key)))
                    .Where(row => (!unlabelled || row.Label is null) && (filter.Length == 0 || Hex(row.Class).Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || row.Label?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true));
                return Task.FromResult(McpToolResult.Json(DataPages.Page(rows, args, row => new JsonObject
                    { ["class"] = Hex(row.Class), ["label"] = row.Label, ["tags"] = row.Count })));
            });

        yield return Pkg("list_tags_of_class", "Tags of one class: hash, name, package and size, paged.",
            Paged(("class", "string", "Class reference hash", true)),
            (data, args, _) => ParseHash(McpJson.String(args, "class")) is not { } type
                ? Task.FromResult(McpToolResult.Error("Give a class hash."))
                : Task.FromResult(McpToolResult.Json(DataPages.Page(data.TagsOfClass(type), args, TagRow))));

        yield return Pkg("read_tag_bytes", "Raw bytes of a tag as a hex dump (16 bytes per line with u32 and ASCII columns), paged by " +
            "offset. 'original' reads the shipped bytes instead of the project's edited ones.",
            McpJson.Schema(("hash", "string", "Tag hash", true), ("offset", "integer", "First byte (default 0)", false),
                ("length", "integer", "Bytes (default 512, max 16384)", false), ("original", "boolean", "Shipped bytes, ignoring project edits", false)),
            (data, args, _) =>
            {
                if (ParseHash(McpJson.String(args, "hash")) is not { } hash || data.ReadTag(hash, McpJson.Bool(args, "original", false)) is not { } bytes)
                    return Task.FromResult(McpToolResult.Error("Unknown tag."));
                int offset = Math.Clamp(McpJson.Int(args, "offset", 0), 0, bytes.Length), length = Math.Clamp(McpJson.Int(args, "length", 512), 1, 16384);
                int end = Math.Min(bytes.Length, offset + length);
                var text = new StringBuilder($"tag 0x{hash:X8}  size {bytes.Length}  bytes {offset}-{end - 1}" + (end < bytes.Length ? $"  next offset: {end}" : "  (end)") + "\n");
                for (int line = offset; line < end; line += 16)
                {
                    int count = Math.Min(16, end - line);
                    text.Append(line.ToString("X6", CultureInfo.InvariantCulture)).Append("  ");
                    for (int i = 0; i < 16; i++) text.Append(i < count ? bytes[line + i].ToString("X2", CultureInfo.InvariantCulture) + " " : "   ");
                    text.Append(' ');
                    for (int i = 0; i < count; i++) text.Append(bytes[line + i] is >= 0x20 and < 0x7F ? (char)bytes[line + i] : '.');
                    text.Append('\n');
                }
                return Task.FromResult(McpToolResult.Text(text.ToString()));
            });

        yield return Pkg("dump_tag", "Decode any tag without a dedicated decoder: its root struct field by field (tag references with " +
            "class and name, localized strings, class hashes, floats, ints) and its typed arrays (count, element class, stride) with the " +
            "first elements decoded the same way. Works for every class, including ones Solforge has no editor for. Classes in the tag " +
            "schema registry also get 'named': their fields by name with confirmed/guessed status and a validation summary.",
            McpJson.Schema(("hash", "string", "Tag hash", true), ("depth", "integer", "Array nesting to expand (default 2, max 4)", false),
                ("elements", "integer", "Elements decoded per array (default 3, max 20)", false), ("original", "boolean", "Shipped bytes", false)),
            (data, args, _) =>
            {
                if (ParseHash(McpJson.String(args, "hash")) is not { } hash || data.ReadTag(hash, McpJson.Bool(args, "original", false)) is not { } bytes)
                    return Task.FromResult(McpToolResult.Error("Unknown tag."));
                var result = TagLayout.Describe(bytes, data, Math.Clamp(McpJson.Int(args, "depth", 2), 0, 4), Math.Clamp(McpJson.Int(args, "elements", 3), 1, 20));
                result["tag"] = data.Tag(hash) is { } tag ? TagRow(tag) : Hex(hash);
                if (data.Tag(hash) is { } known && SchemaView(bytes, known.Class, data.StringIfLoaded) is { } named) result["named"] = named;
                return Task.FromResult(McpToolResult.Json(result));
            });

        yield return Pkg("infer_tag_schema", "Infer a struct layout for a whole tag class from many of its tags: per root offset, the " +
            "dominant kind (tag, string, class, f32, u32, hash, array), whether every sample agrees, examples, and array element classes. " +
            "Use for classes with no Solforge decoder (activities, director, dialog, loot...).",
            McpJson.Schema(("class", "string", "Class reference hash", true), ("samples", "integer", "Tags to sample (default 40, max 400)", false)),
            (data, args, cancel) =>
            {
                if (ParseHash(McpJson.String(args, "class")) is not { } type) return Task.FromResult(McpToolResult.Error("Give a class hash."));
                int limit = Math.Clamp(McpJson.Int(args, "samples", 40), 1, 400);
                var samples = data.TagsOfClass(type).Take(limit).Select(tag => { cancel.ThrowIfCancellationRequested(); return (tag, data.ReadTag(tag.Hash, true)); })
                    .Where(pair => pair.Item2 is not null).Select(pair => (pair.tag, pair.Item2!)).ToList();
                if (samples.Count == 0) return Task.FromResult(McpToolResult.Error("No readable tags of that class."));
                var schema = TagLayout.InferSchema(samples, data);
                schema["class"] = Hex(type);
                schema["label"] = data.ClassLabels.GetValueOrDefault(type);
                return Task.FromResult(McpToolResult.Json(schema));
            });

        yield return new McpTool("export_data", "Run any read-only Solforge tool across all its pages and write every row to a JSON or CSV " +
            "file under the project's exports folder (or %LOCALAPPDATA%\\Solforge\\exports). Example: tool=search_functions args={\"query\":\"world\"} " +
            "format=csv. Returns the file path and row count.",
            new JsonObject
            {
                ["type"] = "object", ["required"] = new JsonArray("tool"),
                ["properties"] = new JsonObject
                {
                    ["tool"] = new JsonObject { ["type"] = "string", ["description"] = "Name of a read tool that returns rows, e.g. search_items, list_tags_of_class, xrefs_to" },
                    ["args"] = new JsonObject { ["type"] = "object", ["description"] = "That tool's arguments (without cursor/offset)" },
                    ["format"] = new JsonObject { ["type"] = "string", ["description"] = "json (default) or csv" },
                    ["file"] = new JsonObject { ["type"] = "string", ["description"] = "Optional file name (no folders); default <tool>-<time>.<format>" },
                    ["max_rows"] = new JsonObject { ["type"] = "integer", ["description"] = "Stop after this many rows (default 100000)" }
                }
            }, false,
            async (args, cancel) =>
            {
                string name = A(args, "tool");
                var tool = Create(host).FirstOrDefault(candidate => candidate.Name == name);
                if (tool is null || tool.RequiresApproval || name == "export_data") return McpToolResult.Error("Give the name of a read tool.");
                var baseArgs = args["args"] as JsonObject ?? new JsonObject();
                string format = (McpJson.String(args, "format") ?? "json").ToLowerInvariant() == "csv" ? "csv" : "json";
                int maxRows = Math.Clamp(McpJson.Int(args, "max_rows", 100_000), 1, 1_000_000);
                var rows = new List<JsonNode?>();
                string? cursor = null; int? offset = null;
                for (int pages = 0; pages < 10_000 && rows.Count < maxRows; pages++)
                {
                    var call = (JsonObject)baseArgs.DeepClone();
                    call["limit"] = 500;
                    if (cursor is not null) call["cursor"] = cursor;
                    if (offset is not null) call["offset"] = offset;
                    var result = await tool.Handler(call, cancel).ConfigureAwait(false);
                    if (result.IsError) return rows.Count == 0 ? result : McpToolResult.Error($"Stopped after {rows.Count} rows: {string.Join(" ", result.Texts)}");
                    JsonObject? page;
                    try { page = JsonNode.Parse(string.Join("", result.Texts)) as JsonObject; }
                    catch (System.Text.Json.JsonException) { return McpToolResult.Error("That tool does not return JSON rows."); }
                    if (page is null) return McpToolResult.Error("That tool does not return JSON rows.");
                    var list = page["rows"] as JsonArray ?? page.Select(pair => pair.Value).OfType<JsonArray>().FirstOrDefault();
                    if (list is null) { rows.Add(page.DeepClone()); break; }
                    foreach (var row in list) { if (rows.Count >= maxRows) break; rows.Add(row?.DeepClone()); }
                    if (page["next_cursor"] is JsonValue next && next.ToString() is { Length: > 0 } nextCursor) { cursor = nextCursor; offset = null; }
                    else if (page["next"] is JsonValue more && more.TryGetValue(out int nextOffset)) { offset = nextOffset; cursor = null; }
                    else break;
                }
                string folder = ExportFolder(host.ProjectRoot);
                Directory.CreateDirectory(folder);
                string file = Path.GetFileName(McpJson.String(args, "file") is { Length: > 0 } given ? given
                    : $"{name}-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.{format}");
                if (!file.EndsWith("." + format, StringComparison.OrdinalIgnoreCase)) file += "." + format;
                string path = Path.Combine(folder, file);
                await File.WriteAllTextAsync(path, format == "csv" ? DataPages.Csv(rows) : new JsonArray([.. rows]).ToJsonString(McpJson.Indented), cancel).ConfigureAwait(false);
                return McpToolResult.Json(new JsonObject { ["path"] = path, ["rows"] = rows.Count, ["format"] = format });
            });
    }

    /** JSON row for a decompiled function (rva, name, size, signature, caller counts). */
    private static JsonObject FunctionRow(DecompIndex index, DecompFunction function) => new()
    {
        ["rva"] = Hex(function.Rva), ["name"] = index.NameOf(function.Rva), ["size"] = function.Size,
        ["signature"] = function.Signature, ["callers"] = index.CallerCount(function.Rva)
    };

    /** JSON row for a tag summary (hash, name, class, package, size). */
    private static JsonObject TagRow(TagSummary tag) => new()
    {
        ["hash"] = Hex(tag.Hash), ["name"] = tag.Name, ["class"] = Hex(tag.Class), ["class_name"] = tag.ClassName,
        ["package"] = tag.Package, ["size"] = tag.Size
    };

    /** Formats a uint as 0x%08X. */
    private static string Hex(uint value) => "0x" + value.ToString("X8", CultureInfo.InvariantCulture);
    /** Formats an int as 0x hex. */
    private static string Hex(int value) => "0x" + value.ToString("X", CultureInfo.InvariantCulture);
}
