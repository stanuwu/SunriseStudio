/**
 * File: MCP tools retail_* over the retail (build 245048) dump: offline reads of the extracted payloads, the tag index and
 * the lookup cache (Solforge.Retail), tiger-mcp at 127.0.0.1:3033 for incoming references and localized text when it is
 * up, port plans, and the porting actions that write into the open project. The read tools are shared with the
 * standalone stdio host (Solforge.Retail.Mcp) through RetailReadToolsFor.
 */
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Solforge.Content;
using Solforge.Mcp.Data;
using Solforge.Retail;
using Solforge.Retail.Decoding;
using Solforge.Retail.Porting;
using SunriseStudio.Packages.TagSchemas;

namespace Solforge.Mcp;

/** The retail_* tools (see the file header). */
public static partial class SolforgeTools
{
    /** Read tools of this file, in listing order (also served by the standalone host). */
    public static readonly string[] RetailReadTools =
    [
        "retail_status", "retail_search", "retail_tag", "retail_read", "retail_references", "retail_strings", "retail_class_map", "retail_capabilities",
        "retail_activity", "retail_map", "retail_port_plan", "retail_preview_mission", "retail_engine"
    ];
    /** Action tools of this file (editor only: they write into the open project or open the viewport). */
    public static readonly string[] RetailActionTools = ["retail_port_mission", "retail_port_strings", "retail_convert_tag", "retail_port_audio", "retail_export_geometry", "retail_open_map"];
    /** Every tool of this file, in listing order. */
    public static readonly string[] RetailToolNames = [.. RetailReadTools, .. RetailActionTools];

    /** Server instructions of the standalone host and the hint in Instructions. */
    public const string RetailHint =
        "Retail (live build 245048) data is read with the retail_* tools: retail_status first, then retail_activity, retail_map, " +
        "retail_strings, retail_read (hex, annotated, decoded) and retail_port_plan; read the solforge-retail-port skill. " +
        "Decoded fields say whether their layout is confirmed or guessed; nothing retail is written to game packages.";

    /** Yields the retail tools for the editor. */
    private static IEnumerable<McpTool> RetailTools(ISolforgeHost host,
        Func<string, string, JsonObject, Func<JsonObject, CancellationToken, Task<McpToolResult>>, McpTool> read,
        Func<string, string, JsonObject, Func<JsonObject, string?>, Func<JsonObject, string>, McpTool> act)
    {
        foreach (var tool in RetailReadToolsFor(RetailSource.Shared, read)) yield return tool;
        var source = RetailSource.Shared;

        McpTool Write(string name, string description, JsonObject schema, Func<JsonObject, string?> validate, Func<JsonObject, string> summary,
            Func<string, JsonObject, CancellationToken, JsonNode> run) =>
            new(name, description, schema, true, async (args, cancel) =>
            {
                if (host.ProjectRoot is not { Length: > 0 } root) return McpToolResult.Error(NoProject);
                if (validate(args) is { } problem) return McpToolResult.Error(problem);
                if (!await host.ApproveAsync(name, summary(args), cancel).ConfigureAwait(false))
                    return McpToolResult.Error("The user declined this in Solforge.");
                if (await RetailIndexing(source, cancel).ConfigureAwait(false) is { } loading) return loading;
                try { return McpToolResult.Json(await Task.Run(() => run(root, args, cancel), cancel).ConfigureAwait(false)); }
                catch (Exception error) when (RetailRecoverable(error)) { return McpToolResult.Error(error.Message); }
            });

        yield return Write("retail_port_mission", "Port a retail activity into the open project: content/activity/<id>.json (an " +
            "activity document with one objective per retail phase, chained, tagged retail-port) plus retail_ports/<id>/<id>.lua " +
            "(a mission script skeleton with CHANGE_ME placeholders) and report.json (what was ported, what was not, next steps). " +
            "Use retail_preview_mission first to see the same output without writing. Encounter logic, AI, loot and dialogue are " +
            "not ported: the retail entity scripts are not decoded.",
            McpJson.Schema(("activity", "string", "Activity tag (0x...) or its named-tag name, e.g. schism.schism_freeroam", true),
                ("overwrite", "boolean", "Replace an earlier port of the same activity (default false)", false)),
            args => RetailResolve(source, McpJson.String(args, "activity"), RetailActivity.ActivityClass) is null
                ? "Give an activity tag (0x...) or a name from retail_activity." : null,
            args => $"Port retail activity {McpJson.String(args, "activity")} into the project (content/activity and retail_ports)",
            (root, args, _) =>
            {
                uint tag = RetailResolve(source, McpJson.String(args, "activity"), RetailActivity.ActivityClass)!.Value;
                var result = RetailMissionPort.Port(source, tag, out string? problem) ?? throw new InvalidDataException(problem ?? "The activity did not decode.");
                var files = RetailMissionPort.Write(root, result, McpJson.Bool(args, "overwrite", false));
                return new JsonObject
                {
                    ["activity"] = result.Activity.Id, ["files"] = new JsonArray(files.Select(file => (JsonNode)Path.GetRelativePath(root, file)).ToArray()),
                    ["report"] = result.Report.DeepClone()
                };
            });

        yield return Write("retail_port_strings", "Write a retail localized-string container's English strings into the project " +
            "as retail_ports/strings/<tag>.json (hash and text per row, hashes kept so ported data that names them still resolves). " +
            "Rows that do not fit a Sol string bank are listed in notes. Packing them into an 86657 string bank package is not " +
            "implemented yet (RetailConverters.ToBank builds the tags from an installed template bank; no tool calls it).",
            McpJson.Schema(("container", "string", "String container tag (class 0x808099EF), e.g. 0x80D26002", true)),
            args => RetailTag.Parse(McpJson.String(args, "container")) is null ? "Give a string container tag (0x...)." : null,
            args => $"Write retail strings of {McpJson.String(args, "container")} to retail_ports/strings",
            (root, args, _) =>
            {
                uint tag = RetailTag.Parse(McpJson.String(args, "container"))!.Value;
                var strings = RetailStrings.Read(source, tag, out string? problem);
                if (strings.Count == 0) throw new InvalidDataException(problem ?? "The container has no strings.");
                var rows = RetailConverters.ToBankTexts(strings, out var notes);
                string folder = Path.Combine(root, "retail_ports", "strings");
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, tag.ToString("x8", CultureInfo.InvariantCulture) + ".json");
                var document = new JsonObject
                {
                    ["container"] = RetailTag.Hex(tag), ["build"] = RetailOptions.Build, ["count"] = rows.Count,
                    ["strings"] = new JsonArray(rows.Select(row => (JsonNode)new JsonObject { ["hash"] = RetailTag.Hex(row.Hash), ["text"] = row.Text }).ToArray()),
                    ["notes"] = RetailList(notes)
                };
                File.WriteAllText(path, document.ToJsonString(McpJson.Indented), new UTF8Encoding(false));
                return new JsonObject { ["file"] = Path.GetRelativePath(root, path), ["count"] = rows.Count, ["notes"] = RetailList(notes) };
            });

        yield return Write("retail_convert_tag", "Convert one retail tag's bytes for 86657: rewrites typed-array markers " +
            "(0x80809FB8 to 0x80809FBD), maps array element classes the class map names exactly once, and relinks references you " +
            "map (relink: {\"0xRETAIL\": \"0xSOL\"}). Writes retail_ports/tags/<tag>.bin and <tag>.json (what changed, unmapped " +
            "classes and references, the port plan verdict). Field layouts are not changed, so only tags retail_port_plan marks " +
            "portable are safe; nothing is installed into game packages.",
            McpJson.Schema(("tag", "string", "Retail tag (0x...)", true),
                ("relink", "object", "Optional map of retail tag to Sol tag, both 0x hex", false)),
            args => RetailTag.Parse(McpJson.String(args, "tag")) is null ? "Give a retail tag (0x...)." : RetailRelink(args, out string? why) is null ? why : null,
            args => $"Convert retail tag {McpJson.String(args, "tag")} into retail_ports/tags",
            (root, args, cancel) =>
            {
                uint tag = RetailTag.Parse(McpJson.String(args, "tag"))!.Value;
                byte[] bytes = source.Read(tag) ?? throw new InvalidDataException($"{RetailTag.Hex(tag)} has no payload in the dump.");
                var relink = RetailRelink(args, out _)!;
                var plan = RetailPortPlanner.Plan(source, tag, 1, 200, cancel);
                var references = plan.Items.Where(item => item.Depth == 1).Select(item => item.Tag).ToList();
                var conversion = RetailConverters.Convert(bytes, source.ClassMap, relink, references);
                string folder = Path.Combine(root, "retail_ports", "tags");
                Directory.CreateDirectory(folder);
                string stem = tag.ToString("x8", CultureInfo.InvariantCulture);
                File.WriteAllBytes(Path.Combine(folder, stem + ".bin"), conversion.Bytes);
                var rootItem = plan.Items.FirstOrDefault(item => item.Tag == tag);
                var report = new JsonObject
                {
                    ["tag"] = RetailTag.Hex(tag), ["class"] = rootItem?.Class is { } type ? RetailTag.Hex(type) : null,
                    ["sol_class"] = rootItem?.SolClass is { } sol ? RetailTag.Hex(sol) : null,
                    ["verdict"] = rootItem is null ? null : RetailStatusName(rootItem.Status), ["reason"] = rootItem?.Reason,
                    ["arrays_rewritten"] = conversion.ArraysRewritten, ["classes_mapped"] = conversion.ClassesMapped,
                    ["unmapped_classes"] = RetailList(conversion.UnmappedClasses), ["references_relinked"] = conversion.ReferencesRelinked,
                    ["unmapped_references"] = new JsonArray(conversion.UnmappedReferences.Select(value => (JsonNode)RetailTag.Hex(value)).ToArray()),
                    ["notes"] = RetailList(conversion.Notes),
                    ["files"] = new JsonArray((JsonNode)$"retail_ports/tags/{stem}.bin", (JsonNode)$"retail_ports/tags/{stem}.json")
                };
                if (rootItem is { Status: not RetailPortStatus.Portable })
                    report["warning"] = "retail_port_plan does not mark this tag portable; the converted bytes need a layout converter before the game can read them.";
                File.WriteAllText(Path.Combine(folder, stem + ".json"), report.ToJsonString(McpJson.Indented), new UTF8Encoding(false));
                return report;
            });

        yield return Write("retail_port_audio", "Decode a retail Wwise media tag (file type 26) to 16-bit PCM WAV under " +
            "the open project's retail_ports\\audio folder. Retail audio is packaged by family (sr_audio, sr_dialog_*, " +
            "sr_dialog_v6xx..v9xx) with no destination id, so find the tag with retail_search (type 26) or read a bank with " +
            "retail_read, then decode one media stream per call. Reports codec, sample rate, channels and duration. " +
            "Putting the result into an 86657 audio tag is not implemented: the WAV is the deliverable for now.",
            McpJson.Schema(("tag", "string", "Retail Wwise media tag (file type 26, 0x...)", true)),
            args => RetailTag.Parse(McpJson.String(args, "tag")) is null ? "Give a retail tag (0x...)." : null,
            args => $"Decode retail audio {McpJson.String(args, "tag")} to retail_ports/audio",
            (root, args, cancel) =>
            {
                if (host is not ISolforgeMediaHost media)
                    throw new InvalidDataException("This Solforge host has no audio decoder; open the project in the editor.");
                uint tag = RetailTag.Parse(McpJson.String(args, "tag"))!.Value;
                byte[] bytes = source.Read(tag) ?? throw new InvalidDataException($"{RetailTag.Hex(tag)} has no payload in the dump.");
                string folder = Path.Combine(root, "retail_ports", "audio");
                return media.ExportWwiseBytes(bytes, tag, folder, cancel)
                    ?? throw new InvalidDataException($"{RetailTag.Hex(tag)} is not a decodable Wwise stream.");
            });


        yield return Write("retail_export_geometry", "Export a retail bubble's static meshes and terrain to Wavefront OBJ " +
            "under the project's retail_ports\\geometry\\<bubble> folder, with placements.json (each mesh tag's world " +
            "transforms) and report.json. This is the usable map path: import_asset or start_creation reads .obj and writes " +
            "a real 86657 static mesh, so retail geometry becomes project content you can place. Textures and entity models " +
            "are still absent, so the result is untextured geometry.",
            McpJson.Schema(("bubble", "string", "Bubble parent (0x8080891E) or definition (0x80808701) tag, from retail_map", true),
                ("statics", "boolean", "Include static meshes (default true)", false),
                ("terrain", "boolean", "Include terrain (default true)", false),
                ("max_meshes", "integer", "Cap on meshes written (default 200, max 2000)", false)),
            args => RetailTag.Parse(McpJson.String(args, "bubble")) is null ? "Give a bubble tag (0x...) from retail_map." : null,
            args => $"Export retail bubble {McpJson.String(args, "bubble")} geometry to retail_ports/geometry",
            (root, args, cancel) =>
            {
                uint bubble = RetailTag.Parse(McpJson.String(args, "bubble"))!.Value;
                var result = RetailGeometryExport.Export(source, bubble, root,
                    McpJson.Bool(args, "statics", true), McpJson.Bool(args, "terrain", true),
                    Math.Clamp(McpJson.Int(args, "max_meshes", 200), 1, 2000), cancel);
                var report = result.ToJson();
                report["files"] = new JsonArray(result.Files.Select(file => (JsonNode)Path.GetRelativePath(root, file)).ToArray());
                return report;
            });

        yield return act("retail_open_map", "Open a retail bubble (map) read-only in Solforge's viewport, in the Retail workspace: " +
            "every map node as a marker by component kind (statics, terrain, lights, decals, respawn points...) with selection and a " +
            "retail tag inspector, plus untextured terrain and static mesh geometry. Textures, materials, entity models and " +
            "dynamic objects are not drawn. Get bubble tags from retail_map with activity. The first open of a big bubble takes a while.",
            McpJson.Schema(("bubble", "string", "Bubble parent (class 0x8080891E) or definition (0x80808701) tag", true),
                ("statics", "boolean", "Decode static mesh geometry (default true)", false),
                ("terrain", "boolean", "Decode terrain geometry (default true)", false)),
            args => RetailTag.Parse(McpJson.String(args, "bubble")) is null ? "Give a bubble tag (0x...) from retail_map." : null,
            args => $"Open retail bubble {McpJson.String(args, "bubble")} in the viewport (read-only)");
    }

    /**
     * The read-only retail tools over source; read wraps each handler (the editor adds its pause check, the standalone
     * host builds plain tools).
     */
    public static IEnumerable<McpTool> RetailReadToolsFor(RetailSource source,
        Func<string, string, JsonObject, Func<JsonObject, CancellationToken, Task<McpToolResult>>, McpTool> read)
    {
        static JsonObject Paged(params (string, string, string, bool)[] properties) => McpJson.Schema([.. properties, .. DataPages.Args]);
        /** A tool whose body needs the index and runs on a worker. */
        McpTool Indexed(string name, string description, JsonObject schema, Func<JsonObject, CancellationToken, McpToolResult> run) =>
            read(name, description, schema, async (args, cancel) =>
            {
                if (await RetailIndexing(source, cancel).ConfigureAwait(false) is { } loading) return loading;
                try { return await Task.Run(() => run(args, cancel), cancel).ConfigureAwait(false); }
                catch (Exception error) when (RetailRecoverable(error)) { return McpToolResult.Error(error.Message); }
            });
        McpToolResult? NeedTag(JsonObject args, string key, out uint tag)
        {
            tag = 0;
            if (RetailTag.Parse(McpJson.String(args, key)) is { } value) { tag = value; return null; }
            return McpToolResult.Error($"Give {key} as a retail tag (0x80D26002 or package:index).");
        }

        yield return read("retail_status", "Where the retail data stands: the dump folders, the tag index (rows, or its build " +
            "progress as rows done of the total: the first build parses the dump's TSVs once, later starts read a cache in seconds), " +
            "the lookup cache (64-bit hashes and named " +
            "tags), the retail schemas (confirmed vs guessed fields), the class map and whether tiger-mcp answers. Start here.",
            McpJson.Schema(("engine", "boolean", "Also ask tiger-mcp for its status (default true)", false)),
            async (args, cancel) =>
            {
                var options = source.Options;
                bool ready = await source.WaitForIndexAsync(TimeSpan.FromSeconds(2), cancel).ConfigureAwait(false);
                var result = new JsonObject
                {
                    ["build"] = RetailOptions.Build, ["root"] = options.Root, ["root_exists"] = source.RootExists, ["media_root"] = options.MediaRoot,
                    ["media_root_exists"] = options.MediaRoot is { Length: > 0 } media && Directory.Exists(media),
                    ["index"] = ready
                        ? new JsonObject
                        {
                            ["loaded"] = source.Index is not null, ["rows"] = source.Index?.Count, ["error"] = source.IndexError,
                            ["struct_classes"] = source.Index?.ClassCounts.Count
                        }
                        : RetailIndexState(source),
                    ["lookup_cache"] = new JsonObject
                    {
                        ["path"] = options.LookupCache, ["hash64"] = source.Lookup.Tag64Count, ["named_tags"] = source.Lookup.Named.Count,
                        ["error"] = source.LookupError
                    },
                    ["schemas"] = new JsonObject
                    {
                        ["count"] = source.Schemas.All.Count,
                        ["confirmed_fields"] = source.Schemas.All.Sum(schema => schema.Fields.Count(field => field.Confirmed)),
                        ["guessed_fields"] = source.Schemas.All.Sum(schema => schema.Guessed.Count()),
                        ["problems"] = RetailList(source.Schemas.Problems)
                    },
                    ["class_map"] = new JsonObject(source.ClassMap.Rows.GroupBy(row => row.Status).OrderBy(group => group.Key, StringComparer.Ordinal)
                        .Select(group => KeyValuePair.Create(group.Key, (JsonNode?)group.Count())))
                };
                if (McpJson.Bool(args, "engine", true))
                {
                    var reply = await source.Engine.StatusAsync(cancel).ConfigureAwait(false);
                    result["engine"] = new JsonObject { ["url"] = source.Engine.Url, ["online"] = !reply.Offline && reply.Ok, ["error"] = reply.Error, ["status"] = reply.Result };
                }
                return McpToolResult.Json(result);
            });

        yield return Indexed("retail_search", "Find retail tags: by name in the engine's named tags (activities, destinations, " +
            "bubbles; works without the index), or by struct class, file type/subtype and package in the tag index. Rows give tag, " +
            "class with its name, size and package.",
            Paged(("name", "string", "Substring of a named tag (e.g. schism, mission_alice)", false),
                ("class", "string", "Struct class hash (0x80808E8E activity, 0x8080891E bubble parent...)", false),
                ("type", "integer", "File type (8/16 structs, 32/40 buffers, 33/41 textures, 26 audio)", false),
                ("subtype", "integer", "File subtype", false),
                ("package", "string", "Package file name or part of it (e.g. w64_sr_schism)", false)),
            (args, _) =>
            {
                string? name = McpJson.String(args, "name");
                uint? type = RetailTag.Parse(McpJson.String(args, "class"));
                if (name is { Length: > 0 })
                {
                    var named = source.Lookup.Named.Where(row => row.Name.Contains(name, StringComparison.OrdinalIgnoreCase) && (type is null || row.Class == type))
                        .OrderBy(row => row.Name, StringComparer.Ordinal);
                    return McpToolResult.Json(DataPages.Page(named, args, row => new JsonObject
                    {
                        ["tag"] = RetailTag.Hex(row.Tag), ["name"] = row.Name, ["class"] = RetailTag.Hex(row.Class), ["class_name"] = source.ClassName(row.Class)
                    }));
                }
                if (source.Index is not { } index) return McpToolResult.Error(source.IndexError ?? "The retail index is not available.");
                int fileType = McpJson.Int(args, "type", -1), subtype = McpJson.Int(args, "subtype", -1);
                string? package = McpJson.String(args, "package");
                if (type is null && fileType < 0 && subtype < 0 && string.IsNullOrEmpty(package))
                    return McpToolResult.Error("Give name, class, type, subtype or package.");
                var rows = index.Search(type, fileType >= 0 ? fileType : null, subtype >= 0 ? subtype : null, string.IsNullOrEmpty(package) ? null : package);
                return McpToolResult.Json(DataPages.Page(rows, args, entry => RetailEntryJson(source, entry), countAll: false));
            });

        yield return Indexed("retail_tag", "Everything known about one retail tag without reading it all: index row (class, file " +
            "type, size, package), engine name, 64-bit hash, payload path, the retail schema for its class (confirmed and guessed " +
            "fields), class map rows (the 86657 class it corresponds to) and which converter would port it.",
            McpJson.Schema(("tag", "string", "Retail tag (0x... or package:index)", true)),
            (args, _) =>
            {
                if (NeedTag(args, "tag", out uint tag) is { } bad) return bad;
                var entry = source.Index?.Find(tag);
                uint? type = entry is { IsStruct: true } structEntry ? structEntry.Reference : null;
                string? path = source.PayloadPath(tag);
                var result = new JsonObject
                {
                    ["tag"] = RetailTag.Hex(tag), ["package_id"] = RetailTag.Package(tag).ToString("X4", CultureInfo.InvariantCulture), ["index"] = RetailTag.Index(tag),
                    ["in_index"] = entry is not null, ["entry"] = entry is { } row ? RetailEntryJson(source, row) : null,
                    ["name"] = source.NameOf(tag),
                    ["hash64"] = source.Lookup.Hash64Of(tag) is { } hash64 ? "0x" + hash64.ToString("X16", CultureInfo.InvariantCulture) : null,
                    ["payload"] = path, ["payload_size"] = path is null ? null : new FileInfo(path).Length
                };
                if (type is { } known)
                {
                    result["schema"] = RetailSchemaJson(source, known);
                    result["class_map"] = new JsonArray(source.ClassMap.ForRetail(known).Select(row => (JsonNode)RetailClassRowJson(row)).ToArray());
                    if (RetailPortPlanner.Converters.TryGetValue(known, out var converter))
                        result["converter"] = new JsonObject { ["name"] = converter.Converter, ["note"] = converter.Note };
                }
                if (path is null) result["note"] = "No payload was extracted for this tag (redacted package, empty tag, or the media drive is missing).";
                return McpToolResult.Json(result);
            });

        yield return Indexed("retail_read", "Read a retail tag's bytes. mode hex: a hex dump (offset/length). mode annotated: " +
            "every field of the retail schema for its class with its value, status (confirmed or guessed) and source, plus " +
            "validation problems. mode decoded (default): the decoder for its class (strings, activity, destination, bubble, " +
            "static instances, static mesh, terrain) with what each part is based on; other classes fall back to annotated.",
            McpJson.Schema(("tag", "string", "Retail tag (0x...)", true), ("mode", "string", "decoded (default), annotated or hex", false),
                ("offset", "integer", "hex: first byte (default 0)", false), ("length", "integer", "hex: bytes (default 512, max 8192)", false)),
            (args, _) =>
            {
                if (NeedTag(args, "tag", out uint tag) is { } bad) return bad;
                if (source.Read(tag) is not { } bytes) return McpToolResult.Error($"{RetailTag.Hex(tag)} has no payload in the dump.");
                string mode = (McpJson.String(args, "mode") ?? "decoded").ToLowerInvariant();
                uint? type = source.ClassOf(tag);
                if (mode == "hex")
                {
                    int offset = Math.Clamp(McpJson.Int(args, "offset", 0), 0, bytes.Length), length = Math.Clamp(McpJson.Int(args, "length", 512), 1, 8192);
                    return McpToolResult.Json(new JsonObject
                    {
                        ["tag"] = RetailTag.Hex(tag), ["size"] = bytes.Length, ["class"] = type is { } c ? RetailTag.Hex(c) : null,
                        ["offset"] = offset, ["hex"] = RetailBytes.HexDump(bytes, offset, Math.Min(length, bytes.Length - offset))
                    });
                }
                if (mode == "decoded" && type is { } decodedClass && RetailDecode(source, tag, decodedClass) is { } decoded)
                    return McpToolResult.Json(decoded);
                return McpToolResult.Json(RetailAnnotate(source, tag, type, bytes));
            });

        yield return read("retail_references", "References of a retail tag. outgoing (default, offline): tags its bytes name " +
            "(aligned tag words found in the index and 64-bit hashes the lookup cache resolves; heuristic, with offsets). incoming: " +
            "tags that name it (tiger-mcp get_tag_references). traverse: tiger-mcp's tree, mode children or ancestors.",
            Paged(("tag", "string", "Retail tag (0x...)", true), ("direction", "string", "outgoing (default), incoming or traverse", false),
                ("mode", "string", "traverse: children (default) or ancestors", false), ("depth", "integer", "traverse: depth (default 2, max 5)", false)),
            async (args, cancel) =>
            {
                if (NeedTag(args, "tag", out uint tag) is { } bad) return bad;
                string direction = (McpJson.String(args, "direction") ?? "outgoing").ToLowerInvariant();
                if (direction is "incoming" or "traverse")
                {
                    var reply = direction == "incoming"
                        ? await source.Engine.ReferencesAsync(tag, DataPages.Limit(args, 200), cancel).ConfigureAwait(false)
                        : await source.Engine.TraverseAsync(tag, McpJson.String(args, "mode") is "ancestors" ? "ancestors" : "children",
                            Math.Clamp(McpJson.Int(args, "depth", 2), 1, 5), cancel).ConfigureAwait(false);
                    return reply.Ok ? McpToolResult.Json(new JsonObject { ["tag"] = RetailTag.Hex(tag), ["direction"] = direction, ["engine"] = reply.Result })
                        : McpToolResult.Error(reply.Error ?? "tiger-mcp failed.");
                }
                if (await RetailIndexing(source, cancel).ConfigureAwait(false) is { } loading) return loading;
                return await Task.Run(() =>
                {
                    if (source.Read(tag) is not { } bytes) return McpToolResult.Error($"{RetailTag.Hex(tag)} has no payload in the dump.");
                    var references = RetailReferences.Scan(source, bytes, tag);
                    var page = DataPages.Page(references, args, reference => new JsonObject
                    {
                        ["offset"] = "0x" + reference.Offset.ToString("X", CultureInfo.InvariantCulture), ["tag"] = RetailTag.Hex(reference.Tag),
                        ["via"] = reference.Wide ? "hash64" : "tag32", ["class"] = source.ClassOf(reference.Tag) is { } c ? source.ClassName(c) : RetailFileType(source, reference.Tag),
                        ["name"] = source.NameOf(reference.Tag)
                    });
                    page["note"] = "Heuristic scan: a hash or number can look like a tag; the schema (retail_read annotated) says which offsets are references.";
                    return McpToolResult.Json(page);
                }, cancel).ConfigureAwait(false);
            });

        yield return read("retail_strings", "Retail localized (English) strings. container: decode a string container " +
            "(class 0x808099EF) offline, filtered by text. query or hash without a container: search every localized string " +
            "through tiger-mcp.",
            Paged(("container", "string", "String container tag (0x...)", false), ("query", "string", "Text to look for", false),
                ("hash", "string", "Exact string hash (0x...)", false)),
            async (args, cancel) =>
            {
                string? query = McpJson.String(args, "query");
                uint? hash = RetailTag.Parse(McpJson.String(args, "hash"));
                if (RetailTag.Parse(McpJson.String(args, "container")) is { } container)
                    return await Task.Run(() =>
                    {
                        var strings = RetailStrings.Read(source, container, out string? problem);
                        if (strings.Count == 0) return McpToolResult.Error(problem ?? "The container has no strings.");
                        var rows = strings.Where(row => (hash is null || row.Hash == hash) &&
                            (string.IsNullOrEmpty(query) || row.Text.Contains(query, StringComparison.OrdinalIgnoreCase)));
                        var page = DataPages.Page(rows, args, row => new JsonObject { ["hash"] = RetailTag.Hex(row.Hash), ["text"] = row.Text });
                        page["container"] = RetailTag.Hex(container);
                        page["verification"] = "Decoded with the confirmed 0x808099EF/0x808099F1 layout (UTF-8 parts with the per-part cipher shift).";
                        return McpToolResult.Json(page);
                    }, cancel).ConfigureAwait(false);
                if (string.IsNullOrEmpty(query) && hash is null) return McpToolResult.Error("Give a container, a query or a hash.");
                var reply = await source.Engine.LocalizedAsync(query, hash, DataPages.Limit(args), cancel).ConfigureAwait(false);
                return reply.Ok ? McpToolResult.Json(reply.Result) : McpToolResult.Error(reply.Error ?? "tiger-mcp failed.");
            });

        yield return read("retail_class_map", "The class map between retail (245048) and Sol (86657) struct classes: name, " +
            "retail and Sol class and size, how many retail tags use it, status (corroborated, retail_only, sol_only, charm_only) " +
            "and evidence. Filter by class hash (either side), name or status.",
            Paged(("class", "string", "Retail or Sol class hash", false), ("name", "string", "Substring of the struct name", false),
                ("status", "string", "corroborated, retail_only, sol_only or charm_only", false)),
            (args, _) =>
            {
                uint? type = RetailTag.Parse(McpJson.String(args, "class"));
                string? name = McpJson.String(args, "name"), status = McpJson.String(args, "status");
                var rows = source.ClassMap.Rows.Where(row => (type is null || row.RetailClass == type || row.SolClass == type)
                    && (string.IsNullOrEmpty(name) || row.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                    && (string.IsNullOrEmpty(status) || string.Equals(row.Status, status, StringComparison.OrdinalIgnoreCase)));
                var page = DataPages.Page(rows, args, row => RetailClassRowJson(row));
                page["source"] = source.ClassMap.Source;
                return Task.FromResult(McpToolResult.Json(page));
            });

        yield return read("retail_capabilities", "What can actually be ported from retail to Sol today, and what cannot: one row " +
            "per content kind (missions, maps, textures, materials, audio, entities, subclasses) with state (portable, " +
            "needs_authoring, needs_writer, blocked), the tool to use, the reason from the code, and the next step. Call this " +
            "BEFORE promising a DLC port: it is the source of truth for feasibility, not the presence of a retail tag. " +
            "filter narrows to one kind or to a state.",
            McpJson.Schema(("filter", "string", "Kind (missions, maps, textures, materials, audio, entities, subclasses) or state (portable, needs_authoring, needs_writer, blocked)", false)),
            (args, _) => Task.FromResult(McpToolResult.Json(RetailCapabilities(source, McpJson.String(args, "filter")))));


        yield return Indexed("retail_activity", "Retail destinations and activities. No arguments: every named destination. " +
            "query: named activities whose name contains it (e.g. schism, mission_alice). destination: that destination decoded " +
            "(name, string container, activity list). activity: that activity decoded (phases with bubble hashes, map references " +
            "and entity links).",
            Paged(("query", "string", "Substring of an activity name", false), ("destination", "string", "Destination tag or name", false),
                ("activity", "string", "Activity tag or name", false)),
            (args, cancel) =>
            {
                if (McpJson.String(args, "activity") is { Length: > 0 } activityText)
                {
                    if (RetailResolve(source, activityText, RetailActivity.ActivityClass) is not { } activityTag)
                        return McpToolResult.Error($"No activity named {Quote(activityText)}; use query to search.");
                    var activity = RetailActivity.ReadActivity(source, activityTag, out string? problem, cancel: cancel);
                    return activity is null ? McpToolResult.Error(problem ?? "The activity did not decode.") : McpToolResult.Json(RetailActivityJson(source, activity));
                }
                if (McpJson.String(args, "destination") is { Length: > 0 } destinationText)
                {
                    if (RetailResolve(source, destinationText, RetailActivity.DestinationClass) is not { } destinationTag)
                        return McpToolResult.Error($"No destination named {Quote(destinationText)}.");
                    var destination = RetailActivity.ReadDestination(source, destinationTag, out string? problem);
                    return destination is null ? McpToolResult.Error(problem ?? "The destination did not decode.") : McpToolResult.Json(RetailDestinationJson(destination));
                }
                var list = McpJson.String(args, "query") is { Length: > 0 } query ? RetailActivity.Activities(source, query) : RetailActivity.Destinations(source);
                var page = DataPages.Page(list, args, row => new JsonObject { ["tag"] = RetailTag.Hex(row.Tag), ["name"] = row.Name });
                if (source.Lookup.Named.Count == 0) page["note"] = "No named tags: " + (source.LookupError ?? "the lookup cache is empty.");
                return McpToolResult.Json(page);
            });

        yield return Indexed("retail_map", "Retail maps. activity: the bubbles (maps) each phase uses. bubble: a bubble " +
            "decoded (containers, node tables, node counts per component kind) with its nodes a page at a time (position, " +
            "rotation, kind, entity). geometry: true also decodes terrain and static meshes and reports counts (slower). " +
            "retail_open_map shows the same in the viewport.",
            Paged(("bubble", "string", "Bubble parent or definition tag", false), ("activity", "string", "Activity tag or name", false),
                ("kind", "string", "Only nodes with this component kind (statics, terrain, light, decals, respawn_point...)", false),
                ("geometry", "boolean", "Also decode terrain and statics and report counts (default false)", false)),
            (args, cancel) =>
            {
                if (McpJson.String(args, "activity") is { Length: > 0 } activityText)
                {
                    if (RetailResolve(source, activityText, RetailActivity.ActivityClass) is not { } activityTag)
                        return McpToolResult.Error($"No activity named {Quote(activityText)}.");
                    var activity = RetailActivity.ReadActivity(source, activityTag, out string? problem, entities: false, cancel: cancel);
                    if (activity is null) return McpToolResult.Error(problem ?? "The activity did not decode.");
                    return McpToolResult.Json(new JsonObject
                    {
                        ["activity"] = RetailTag.Hex(activity.Tag), ["name"] = activity.Name,
                        ["bubbles"] = new JsonArray(RetailMap.BubblesOf(activity).Select(bubble => (JsonNode)new JsonObject
                            { ["tag"] = RetailTag.Hex(bubble), ["name"] = source.NameOf(bubble) }).ToArray()),
                        ["phases"] = new JsonArray(activity.Phases.Select(phase => (JsonNode)new JsonObject
                        {
                            ["index"] = phase.Index, ["bubble_hash"] = RetailTag.Hex(phase.BubbleHash),
                            ["bubble_parents"] = new JsonArray(phase.BubbleParents.Select(value => (JsonNode)RetailTag.Hex(value)).ToArray()),
                            ["unresolved_maps"] = phase.UnresolvedMaps
                        }).ToArray())
                    });
                }
                if (NeedTag(args, "bubble", out uint tag) is not null) return McpToolResult.Error("Give bubble (a tag) or activity.");
                var map = RetailMap.ReadBubble(source, tag, out string? why);
                if (map is null) return McpToolResult.Error(why ?? "The bubble did not decode.");
                string? kind = McpJson.String(args, "kind");
                var nodes = map.Nodes.Where(node => string.IsNullOrEmpty(kind) || node.Components.Any(component => component.Kind == kind));
                var page = DataPages.Page(nodes, args, node => new JsonObject
                {
                    ["table"] = RetailTag.Hex(node.Table), ["index"] = node.Index, ["kind"] = node.Kind,
                    ["kinds"] = RetailList(node.Components.Select(component => component.Kind).Distinct().ToList()),
                    ["position"] = RetailVector(node.Translation), ["w"] = Math.Round(node.TranslationW, 4),
                    ["rotation"] = new JsonArray((JsonNode)Math.Round(node.Rotation.X, 4), (JsonNode)Math.Round(node.Rotation.Y, 4),
                        (JsonNode)Math.Round(node.Rotation.Z, 4), (JsonNode)Math.Round(node.Rotation.W, 4)),
                    ["entity"] = node.Entity is { } entity ? RetailTag.Hex(entity) : null,
                    ["data"] = new JsonArray(node.Components.Where(component => component.DataTag is not null)
                        .Select(component => (JsonNode)$"{component.Kind}:{RetailTag.Hex(component.DataTag!.Value)}").ToArray())
                });
                page["bubble"] = new JsonObject
                {
                    ["parent"] = RetailTag.Hex(map.Parent), ["name_hash"] = RetailTag.Hex(map.NameHash), ["name"] = source.NameOf(map.Parent),
                    ["definition"] = map.Definition is { } definition ? RetailTag.Hex(definition) : null,
                    ["containers"] = map.Containers.Count, ["node_tables"] = map.Tables.Count, ["nodes"] = map.Nodes.Count, ["truncated"] = map.Truncated,
                    ["kind_counts"] = new JsonObject(map.KindCounts.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value))),
                    ["notes"] = RetailList(map.Notes)
                };
                page["verification"] = "Node rotation (unit quaternions), translation and the entity WideHash are confirmed; w is a guessed " +
                    "scale/radius; component kinds come from alkahest's class names.";
                if (McpJson.Bool(args, "geometry", false))
                {
                    var scene = RetailSceneBuilder.Build(source, tag, new RetailSceneOptions(), out string? problem, null, cancel);
                    page["geometry"] = scene is null ? new JsonObject { ["error"] = problem } : new JsonObject
                    {
                        ["stats"] = new JsonObject(scene.Stats.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value))),
                        ["meshes"] = scene.Meshes.Count, ["triangles"] = scene.Meshes.Sum(mesh => (long)mesh.Mesh.Triangles),
                        ["instances"] = scene.Meshes.Sum(mesh => mesh.Transforms.Count), ["notes"] = RetailList(scene.Notes.Take(20).ToList())
                    };
                }
                return McpToolResult.Json(page);
            });

        yield return Indexed("retail_port_plan", "What porting a retail tag to 86657 involves: walks its references (depth " +
            "levels) and marks each dependency portable (the class map names a same-size 86657 class: only array markers and " +
            "references change), needs_converter (with the converter: strings, mission, map_view, geometry, layout...) or blocked " +
            "(no payload, textures, audio, no 86657 class). Portable is a layout match, not tested in game.",
            Paged(("tag", "string", "Retail tag (0x...)", true), ("depth", "integer", "Reference depth (default 2, max 4)", false),
                ("max_tags", "integer", "Most tags to visit (default 400, max 3000)", false),
                ("status", "string", "Only items with this status: portable, needs_converter or blocked", false)),
            (args, cancel) =>
            {
                if (NeedTag(args, "tag", out uint tag) is { } bad) return bad;
                var plan = RetailPortPlanner.Plan(source, tag, Math.Clamp(McpJson.Int(args, "depth", 2), 0, 4), Math.Clamp(McpJson.Int(args, "max_tags", 400), 1, 3000), cancel);
                string? status = McpJson.String(args, "status");
                var items = plan.Items.Where(item => string.IsNullOrEmpty(status) || RetailStatusName(item.Status) == status);
                var page = DataPages.Page(items, args, item => new JsonObject
                {
                    ["tag"] = RetailTag.Hex(item.Tag), ["depth"] = item.Depth, ["parent"] = item.Depth == 0 ? null : RetailTag.Hex(item.Parent),
                    ["class"] = item.Class is { } c ? RetailTag.Hex(c) : null, ["class_name"] = item.ClassName, ["size"] = item.Size,
                    ["status"] = RetailStatusName(item.Status), ["converter"] = item.Converter.Length > 0 ? item.Converter : null, ["reason"] = item.Reason,
                    ["sol_class"] = item.SolClass is { } sol ? RetailTag.Hex(sol) : null
                });
                page["root"] = RetailTag.Hex(plan.Root);
                page["totals"] = new JsonObject
                {
                    ["portable"] = plan.Count(RetailPortStatus.Portable), ["needs_converter"] = plan.Count(RetailPortStatus.NeedsConverter),
                    ["blocked"] = plan.Count(RetailPortStatus.Blocked), ["truncated"] = plan.Truncated
                };
                page["converters"] = new JsonObject(plan.Items.Where(item => item.Converter.Length > 0).GroupBy(item => item.Converter)
                    .OrderByDescending(group => group.Count()).Select(group => KeyValuePair.Create(group.Key, (JsonNode?)group.Count())));
                page["notes"] = RetailList(plan.Notes);
                return McpToolResult.Json(page);
            });

        yield return Indexed("retail_preview_mission", "Preview porting a retail activity without writing anything: the Sol " +
            "activity document (objectives per phase), the mission script skeleton (Lua, CHANGE_ME placeholders) and the report " +
            "of what was and was not ported. retail_port_mission (editor) writes the same into the project.",
            McpJson.Schema(("activity", "string", "Activity tag (0x...) or name", true), ("script", "boolean", "Include the script text (default true)", false)),
            (args, _) =>
            {
                string text = McpJson.String(args, "activity") ?? "";
                if (RetailResolve(source, text, RetailActivity.ActivityClass) is not { } tag)
                    return McpToolResult.Error($"No activity named {Quote(text)}; use retail_activity with query.");
                var result = RetailMissionPort.Port(source, tag, out string? problem);
                if (result is null) return McpToolResult.Error(problem ?? "The activity did not decode.");
                return McpToolResult.Json(new JsonObject
                {
                    ["activity"] = ContentJson.ToNode(result.Activity), ["script_stem"] = result.ScriptStem,
                    ["script"] = McpJson.Bool(args, "script", true) ? result.Script : null, ["report"] = result.Report.DeepClone()
                });
            });

        yield return read("retail_engine", "Call one tiger-mcp tool directly (read-only ones: get_tag_info, read_tag_data, " +
            "get_tag_references, get_tag_details, search_named_tags, search_tags_by_type, search_strings, search_localized_strings, " +
            "search_raw_strings, search_wordlist_hashes, find_tags_for_hash, scan_tag_for_hashes, traverse_tag, list_package_files, " +
            "get_server_status). Use it for what the retail_* tools do not cover; it fails cleanly when tiger-mcp is down.",
            McpJson.Schema(("tool", "string", "tiger-mcp tool name", true), ("arguments", "object", "Its arguments", false)),
            async (args, cancel) =>
            {
                string tool = McpJson.String(args, "tool") ?? "";
                var arguments = args["arguments"] is JsonObject given ? (JsonObject)given.DeepClone() : new JsonObject();
                var reply = await source.Engine.CallAsync(tool, arguments, cancel).ConfigureAwait(false);
                return reply.Ok ? McpToolResult.Json(reply.Result) : McpToolResult.Error(reply.Error ?? "tiger-mcp failed.");
            });
    }

    /**
     * Null when the index is ready (or failed, which the tool reports); otherwise, after up to 20 seconds, a structured
     * "still loading, call again" reply with the build's real progress (rows done of the total).
     */
    private static async Task<McpToolResult?> RetailIndexing(RetailSource source, CancellationToken cancel)
    {
        McpJobs.Report(source.IndexProgress.Length > 0 ? source.IndexProgress : "Loading the retail tag index");
        if (await source.WaitForIndexAsync(TimeSpan.FromSeconds(20), cancel).ConfigureAwait(false)) return null;
        var state = RetailIndexState(source);
        state["retry"] = true;
        state["message"] = "The retail tag index is still loading (" + (source.IndexProgress.Length > 0 ? source.IndexProgress : "starting") +
            "). The first build parses the dump's TSVs once and writes a cache; later starts load the cache in seconds. Call again in a few seconds.";
        return McpToolResult.Json(state);
    }

    /** The index build state for replies: phase, done, total and the progress line. */
    private static JsonObject RetailIndexState(RetailSource source)
    {
        var info = source.IndexProgressInfo;
        return new JsonObject
        {
            ["loaded"] = false, ["building"] = true, ["progress"] = source.IndexProgress, ["phase"] = info?.Phase,
            ["done"] = info?.Done, ["total"] = info?.Total, ["unit"] = info is null ? null : info.Value.Phase == "counting" ? "bytes" : "rows"
        };
    }

    private static bool RetailRecoverable(Exception error) =>
        error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or FormatException or KeyNotFoundException;

    /** A tag from text: 0x hex, 8 hex digits or package:index; otherwise an exact (then unique substring) named-tag name of the class. */
    private static uint? RetailResolve(RetailSource source, string? text, uint type)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || text.Contains(':') || (text.Length == 8 && text.All(Uri.IsHexDigit)))
            return RetailTag.Parse(text);
        var named = source.Lookup.NamedOfClass(type);
        if (source.Lookup.FindNamed(type, text) is { } ordinal) return ordinal;
        if (named.FirstOrDefault(row => string.Equals(row.Name, text, StringComparison.OrdinalIgnoreCase)) is { } exact) return exact.Tag;
        var partial = named.Where(row => row.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).Select(row => row.Tag).Distinct().ToList();
        return partial.Count == 1 ? partial[0] : null;
    }

    /** The relink argument as a map, or null with why. */
    private static Dictionary<uint, uint>? RetailRelink(JsonObject args, out string? why)
    {
        why = null;
        var map = new Dictionary<uint, uint>();
        if (args["relink"] is null) return map;
        if (args["relink"] is not JsonObject relink) { why = "relink must be an object of retail tag to Sol tag."; return null; }
        foreach (var (key, value) in relink)
        {
            if (RetailTag.Parse(key) is not { } from || RetailTag.Parse(value?.ToString()) is not { } to) { why = $"relink entry {Quote(key)} is not two tags."; return null; }
            map[from] = to;
        }
        return map;
    }

    private static string RetailStatusName(RetailPortStatus status) => status switch
    {
        RetailPortStatus.Portable => "portable", RetailPortStatus.NeedsConverter => "needs_converter", _ => "blocked"
    };

    private static JsonArray RetailList(IEnumerable<string> values) => new(values.Select(value => (JsonNode)value).ToArray());

    private static JsonArray RetailVector(Vector3 value) =>
        new((JsonNode)Math.Round(value.X, 3), (JsonNode)Math.Round(value.Y, 3), (JsonNode)Math.Round(value.Z, 3));

    private static string RetailFileType(RetailSource source, uint tag) => source.Index?.Find(tag) is { } entry ? $"file type {entry.Type}/{entry.Subtype}" : "not indexed";

    private static JsonObject RetailEntryJson(RetailSource source, RetailEntry entry) => new()
    {
        ["tag"] = RetailTag.Hex(entry.Tag), ["class"] = entry.IsStruct ? RetailTag.Hex(entry.Reference) : null,
        ["class_name"] = entry.IsStruct ? source.ClassName(entry.Reference) : null,
        ["reference"] = entry.IsStruct ? null : RetailTag.Hex(entry.Reference), ["type"] = entry.Type, ["subtype"] = entry.Subtype,
        ["size"] = entry.Size, ["package"] = entry.Package, ["name"] = source.NameOf(entry.Tag)
    };

    /**
     * The portability matrix behind retail_capabilities. Every row names the check that produced it, so a reader can
     * re-derive it rather than trust it: the texture and entity rows come from the dump's own entry sizes (no type 32/33
     * payload exceeds 64 bytes, type 41 is DXBC shader bytecode), and the subclass row from Solfore's content families.
     */
    private static JsonObject RetailCapabilities(RetailSource source, string? filter)
    {
        // (kind, state, tool, what, why, next)
        (string Kind, string State, string? Tool, string What, string Why, string Next)[] rows =
        [
            ("missions", "portable", "retail_port_mission",
                "The activity document (one objective per retail phase, chained) plus a Lua script skeleton and report.json.",
                "RetailActivity and RetailMissionPort decode phases and directives; the script is written with CHANGE_ME placeholders.",
                "Run retail_preview_mission, then retail_port_mission, then finish with the solforge-new-mission skill "
                + "(bind script, replace CHANGE_ME, check_mission_script, simulate_mission, build)."),
            ("missions", "blocked", null,
                "Encounter logic, AI, loot and dialogue inside a mission.",
                "The retail entity scripts are not decoded; only phases/objectives are readable.",
                "Hand-write encounters on 86657 objects. Do not promise parity: this is authored, not ported."),
            ("maps", "portable", "retail_export_geometry",
                "Bubble geometry as Wavefront OBJ per mesh plus placements.json (world transforms) and report.json.",
                "Two decoders feed it: RetailGeometry reads the triangles, and MeshAssetImporter reads .obj and writes a real "
                + "86657 static mesh, so the exported geometry becomes project content. Still untextured: retail texels are "
                + "absent, and terrain exports as geometry rather than as a v38 terrain tag.",
                "retail_export_geometry {bubble} then import the .obj files (import_asset or start_creation) and place them "
                + "with the transforms in placements.json."),
            ("textures", "blocked", null,
                "Texture pixels.",
                "No texture payload exists to convert: across 1.2M indexed entries every type-32 header is at most 64 bytes "
                + "and every type-33 stub is exactly 40 bytes; type 41 is DXBC shader bytecode, not texels. Not a decoder gap.",
                "Do not plan around retail textures. Reuse an existing Sol texture, or author new art and import it."),
            ("materials", "blocked", null,
                "Material graph look; shader code is readable (DXBC) but its texture inputs are absent.",
                "Material tags decode, so names and slot wiring are known; the texels they bind are missing (see textures).",
                "Port material structure as a starting point, then supply authored textures from the Sol install."),
            ("audio", "portable", "retail_port_audio",
                "Wwise media as 16-bit PCM WAV: 13.15 GB across 276,872 type-26 entries. Payloads carry a RIFF (Wwise) "
                + "header and decode with WemDecoder.",
                "Decoding works today. Two limits remain: audio is packaged by family (sr_audio, sr_dialog_*, "
                + "sr_dialog_v6xx..v9xx) with no destination id, so a tag must be found by search rather than by map; and "
                + "packing the WAV into an 86657 audio tag is not implemented, so the WAV is the deliverable.",
                "Find the tag with retail_search (type 26) or retail_read a bank, then retail_port_audio to write "
                + "retail_ports/audio/<tag>.wav. Placement into the game still needs an 86657 audio tag writer."),
            ("entities", "blocked", null,
                "Entity models, materials and skinning.",
                "No retail model/material/skin reader exists in Solforge.Retail (only viewport geometry), and the never-indexed "
                + "packages checked carry zero mesh bytes.",
                "Phase 4.4: mirror ModelSkinReader/ClothSkinReader/AnimationRigReader for v53, after R.10."),
            ("subclasses", "needs_authoring", null,
                "Stasis subclasses: Behemoth, Shadebinder, Revenant present supers, melees, grenades, aspects and fragments.",
                "Reverse-engineered 2026-10-19: 86657 ships NO Stasis content. A full 15,424-item export has zero Stasis "
                + "abilities (only emotes/bounties named slow/freeze) and just the nine light subclass items. Abilities come "
                + "from the client talent grid (ability_buckets.bin), which Solforge does not write.",
                "Use gameplay \"reskin\": the packs declare reskin_of (Behemoth->arc.titan.striker, Shadebinder->"
                + "arc.warlock.stormcaller, Revenant->void.hunter.nightstalker) and framework.reskin() builds that light "
                + "subclass while showing the Stasis names. Going live needs a client talent-grid writer."),
        ];
        var items = rows.Where(row => string.IsNullOrEmpty(filter)
            || string.Equals(row.Kind, filter, StringComparison.OrdinalIgnoreCase)
            || string.Equals(row.State, filter, StringComparison.OrdinalIgnoreCase));
        var result = new JsonObject
        {
            ["build_retail"] = RetailOptions.Build,
            ["build_sol"] = 86657,
            ["summary"] = "Missions port as skeletons. Map geometry exports to OBJ and imports as real 86657 static meshes. "
                + "Textures and entity models are absent from the retail install, so no converter can supply them. Audio "
                + "decodes to WAV but is not yet installable as an 86657 tag. Stasis is data-complete but needs authored plugs.",
            ["kinds"] = new JsonArray(items.Select(row => (JsonNode)new JsonObject
            {
                ["kind"] = row.Kind, ["state"] = row.State, ["tool"] = row.Tool,
                ["what"] = row.What, ["why"] = row.Why, ["next"] = row.Next
            }).ToArray()),
            ["states"] = new JsonObject
            {
                ["portable"] = "a tool writes it today", ["needs_writer"] = "the data decodes, a writer is missing",
                ["needs_authoring"] = "the data is present but the game needs hand-authored content", ["blocked"] = "the source data is not available"
            },
            ["root"] = source.Options.Root, ["root_exists"] = source.RootExists
        };
        return result;
    }

    private static JsonObject RetailClassRowJson(RetailClassRow row) => new()
    {
        ["name"] = row.Name, ["charm"] = row.Charm, ["retail_class"] = row.RetailClass is { } retail ? RetailTag.Hex(retail) : null,
        ["retail_size"] = row.RetailSize, ["sol_class"] = row.SolClass is { } sol ? RetailTag.Hex(sol) : null, ["sol_size"] = row.SolSize,
        ["retail_tags"] = row.RetailTags, ["status"] = row.Status, ["evidence"] = RetailList(row.Evidence)
    };

    private static JsonObject? RetailSchemaJson(RetailSource source, uint type) => source.Schemas.Find(type) is not { } schema ? null : new JsonObject
    {
        ["id"] = schema.Id, ["name"] = schema.Name, ["size"] = schema.Size, ["provenance"] = schema.Provenance,
        ["confirmed"] = RetailList(schema.Fields.Where(field => field.Confirmed).Select(field => field.Name).ToList()),
        ["guessed"] = RetailList(schema.Guessed.Select(field => field.Name).ToList())
    };

    /** The retail schema's fields with values, statuses and validation problems; a note when there is no schema. */
    private static JsonObject RetailAnnotate(RetailSource source, uint tag, uint? type, byte[] bytes)
    {
        var result = new JsonObject { ["tag"] = RetailTag.Hex(tag), ["size"] = bytes.Length, ["class"] = type is { } c ? RetailTag.Hex(c) : null };
        if (type is null || source.Schemas.Find(type.Value) is not { } schema)
        {
            result["schema"] = null;
            result["note"] = "No retail schema for this class: use mode hex, retail_references and retail_class_map. Fields are not guessed.";
            result["hex"] = RetailBytes.HexDump(bytes, 0, Math.Min(bytes.Length, 256));
            return result;
        }
        result["schema"] = schema.Id;
        result["provenance"] = schema.Provenance;
        result["fields"] = new JsonArray(TagSchemaReader.ReadAll(bytes, 0, schema, RetailTag.ArrayMarker).Select(value => (JsonNode)new JsonObject
        {
            ["name"] = value.Field.Name, ["offset"] = "0x" + value.Offset.ToString("X", CultureInfo.InvariantCulture), ["type"] = value.Field.Type,
            ["value"] = value.Text, ["status"] = value.Field.Status, ["source"] = value.Field.Source, ["note"] = value.Field.Note
        }).ToArray());
        result["problems"] = new JsonArray(TagSchemaReader.Validate(source.Schemas, bytes, schema).Select(issue => (JsonNode)new JsonObject
            { ["path"] = issue.Path, ["offset"] = "0x" + issue.Offset.ToString("X", CultureInfo.InvariantCulture), ["message"] = issue.Message, ["error"] = issue.Error }).ToArray());
        return result;
    }

    /** The class decoder's view of a tag, or null when no decoder handles the class. */
    private static JsonObject? RetailDecode(RetailSource source, uint tag, uint type)
    {
        string? problem;
        switch (type)
        {
            case RetailStrings.ContainerClass:
                var strings = RetailStrings.Read(source, tag, out problem);
                return new JsonObject
                {
                    ["tag"] = RetailTag.Hex(tag), ["decoder"] = "strings", ["count"] = strings.Count, ["problem"] = problem,
                    ["first"] = new JsonArray(strings.Take(50).Select(row => (JsonNode)new JsonObject { ["hash"] = RetailTag.Hex(row.Hash), ["text"] = row.Text }).ToArray()),
                    ["more"] = "retail_strings pages through every string.", ["verification"] = "confirmed layout"
                };
            case RetailActivity.ActivityClass:
                return RetailActivity.ReadActivity(source, tag, out problem) is { } activity ? RetailActivityJson(source, activity) : new JsonObject { ["error"] = problem };
            case RetailActivity.DestinationClass:
                return RetailActivity.ReadDestination(source, tag, out problem) is { } destination ? RetailDestinationJson(destination) : new JsonObject { ["error"] = problem };
            case RetailMap.BubbleParentClass or RetailMap.BubbleDefinitionClass:
                if (RetailMap.ReadBubble(source, tag, out problem, 0) is not { } bubble) return new JsonObject { ["error"] = problem };
                return new JsonObject
                {
                    ["tag"] = RetailTag.Hex(tag), ["decoder"] = "bubble", ["parent"] = RetailTag.Hex(bubble.Parent), ["name_hash"] = RetailTag.Hex(bubble.NameHash),
                    ["definition"] = bubble.Definition is { } definition ? RetailTag.Hex(definition) : null,
                    ["containers"] = new JsonArray(bubble.Containers.Select(value => (JsonNode)RetailTag.Hex(value)).ToArray()),
                    ["node_tables"] = new JsonArray(bubble.Tables.Select(value => (JsonNode)RetailTag.Hex(value)).ToArray()),
                    ["notes"] = RetailList(bubble.Notes), ["more"] = "retail_map with bubble lists the nodes."
                };
            case RetailGeometry.StaticMapDataClass or 0x80806A0D:
                if (RetailGeometry.ReadStaticInstances(source, tag, out problem) is not { } set) return new JsonObject { ["error"] = problem };
                return new JsonObject
                {
                    ["tag"] = RetailTag.Hex(set.Tag), ["decoder"] = "static_instances", ["statics"] = set.Placements.Count, ["instances"] = set.Instances,
                    ["most_used"] = new JsonArray(set.Placements.OrderByDescending(placement => placement.Transforms.Count).Take(30).Select(placement => (JsonNode)new JsonObject
                    {
                        ["static"] = RetailTag.Hex(placement.Static), ["count"] = placement.Transforms.Count,
                        ["first_position"] = placement.Transforms.Count > 0 ? RetailVector(placement.Transforms[0].Translation) : null
                    }).ToArray()),
                    ["notes"] = RetailList(set.Notes),
                    ["verification"] = "Transforms confirmed: decoded instance positions fall inside the tag's own world bounds."
                };
            case RetailGeometry.StaticMeshClass:
                return RetailMeshJson(RetailGeometry.ReadStaticMesh(source, tag, out problem), problem,
                    "Positions and the GenerateGbuffer/highest-LOD part choice follow alkahest; normals, UVs and materials are not decoded.");
            case RetailGeometry.TerrainClass:
                return RetailMeshJson(RetailGeometry.ReadTerrain(source, tag, out problem), problem,
                    "Terrain positions and strips decode with consistent upward winding; materials and textures are not decoded.");
            default:
                return null;
        }
    }

    private static JsonObject RetailMeshJson(RetailMesh? mesh, string? problem, string verification)
    {
        if (mesh is null) return new JsonObject { ["error"] = problem };
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var position in mesh.Positions) { min = Vector3.Min(min, position); max = Vector3.Max(max, position); }
        return new JsonObject
        {
            ["tag"] = RetailTag.Hex(mesh.Tag), ["decoder"] = mesh.Kind, ["vertices"] = mesh.Positions.Length, ["triangles"] = mesh.Triangles,
            ["bounds"] = mesh.Positions.Length == 0 ? null : new JsonObject { ["min"] = RetailVector(min), ["max"] = RetailVector(max) },
            ["notes"] = RetailList(mesh.Notes), ["verification"] = verification
        };
    }

    private static JsonObject RetailActivityJson(RetailSource source, RetailActivityInfo activity) => new()
    {
        ["tag"] = RetailTag.Hex(activity.Tag), ["name"] = activity.Name ?? source.NameOf(activity.Tag),
        ["location_hash"] = RetailTag.Hex(activity.LocationHash), ["activity_hash"] = RetailTag.Hex(activity.ActivityHash),
        ["destination"] = activity.Destination is { } destination ? RetailTag.Hex(destination) : null,
        ["ambient_activity"] = activity.AmbientActivity is { } ambient ? RetailTag.Hex(ambient) : null,
        ["phases"] = new JsonArray(activity.Phases.Select(phase => (JsonNode)new JsonObject
        {
            ["index"] = phase.Index, ["location_hash"] = RetailTag.Hex(phase.LocationHash), ["activity_hash"] = RetailTag.Hex(phase.ActivityHash),
            ["bubble_hash"] = RetailTag.Hex(phase.BubbleHash),
            ["bubble_parents"] = new JsonArray(phase.BubbleParents.Select(value => (JsonNode)RetailTag.Hex(value)).ToArray()),
            ["unresolved_maps"] = phase.UnresolvedMaps,
            ["entity_links"] = phase.Entities.Count, ["entity_resources"] = phase.Entities.Sum(link => link.EntityResources.Count),
            ["links"] = new JsonArray(phase.Entities.Take(20).Select(link => (JsonNode)new JsonObject
            {
                ["phase_hash"] = RetailTag.Hex(link.PhaseHash), ["link"] = RetailTag.Hex(link.LinkTag), ["resources"] = link.EntityResources.Count
            }).ToArray())
        }).ToArray()),
        ["notes"] = RetailList(activity.Notes),
        ["verification"] = "Phases, map references and the entity-link chain are confirmed against the dump; what the entity " +
            "resources do (scripts, spawns, objectives) is not decoded."
    };

    private static JsonObject RetailDestinationJson(RetailDestinationInfo destination) => new()
    {
        ["tag"] = RetailTag.Hex(destination.Tag), ["name"] = destination.Name, ["location_hash"] = RetailTag.Hex(destination.LocationHash),
        ["string_container"] = destination.StringContainer is { } strings ? RetailTag.Hex(strings) : null,
        ["events"] = destination.Events is { } events ? RetailTag.Hex(events) : null, ["patrols"] = destination.Patrols is { } patrols ? RetailTag.Hex(patrols) : null,
        ["activities"] = new JsonArray(destination.Activities.Select(row => (JsonNode)new JsonObject
            { ["hash"] = RetailTag.Hex(row.Hash), ["name"] = row.Name, ["tag"] = row.Tag is { } tag ? RetailTag.Hex(tag) : null }).ToArray()),
        ["notes"] = RetailList(destination.Notes)
    };
}
