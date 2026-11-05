/**
 * File: MCP content tools (list/get/validate/create/update/delete/export content, subclass layout checks, get_help);
 * the data model is the Solforge.Content project.
 */
using System.Text.Json;
using System.Text.Json.Nodes;
using Solforge.Content;

namespace Solforge.Mcp;

/**
 * Content tools (Solforge.Content): subclasses, abilities, damage types, loot pools, enemies,
 * activities and the seasonal artifact as JSON documents under <project>\content. Reads work with
 * context paused and need no map; writes wait for the approve click and then change the project files
 * directly (Window > Content editors re-reads them). Plain JSON in and out, so any MCP client and
 * model can use them. get_help answers Help > Keybinds & Tips from the editor's live shortcut map.
 */
public static partial class SolforgeTools
{
    /** Content and help reads; like CatalogTools they keep working while context publishing is paused. */
    public static readonly string[] ContentReadTools =
        ["list_content_families", "list_content", "get_content", "validate_content", "get_subclass_layout", "check_subclass_build", "get_help"];

    private const string NoProject = "No project is open in Solforge; open or create one first.";
    private const string FamilyList = "subclass, ability, damage_type, loot_pool, enemy, activity or artifact";

    /** An object argument; clients that send objects as JSON text are accepted too. */
    private static JsonObject? ObjectArg(JsonObject args, string name)
    {
        if (args[name] is JsonObject value) return value;
        if (McpJson.String(args, name) is { Length: > 0 } text)
            try { return JsonNode.Parse(text) as JsonObject; } catch (JsonException) { return null; }
        return null;
    }

    /** JSON array of validation issues (path and message). */
    private static JsonArray IssuesJson(IEnumerable<ContentIssue> issues) =>
        new(issues.Select(issue => (JsonNode)new JsonObject { ["path"] = issue.Path, ["message"] = issue.Message, ["error"] = issue.Error }).ToArray());

    /** Yields the content-family tools (see the file header). */
    private static IEnumerable<McpTool> ContentTools(ISolforgeHost host,
        Func<string, string, JsonObject, Func<JsonObject, CancellationToken, Task<McpToolResult>>, McpTool> read)
    {
        ContentStore? Store() => host.ProjectRoot is { Length: > 0 } root ? new ContentStore(root) : null;
        McpTool Local(string name, string description, JsonObject schema, Func<ContentStore, JsonObject, JsonNode> run) =>
            read(name, description, schema, (args, _) =>
            {
                if (Store() is not { } store) return Task.FromResult(McpToolResult.Error(NoProject));
                try { return Task.FromResult(McpToolResult.Json(run(store, args))); }
                catch (Exception error) when (Recoverable(error)) { return Task.FromResult(McpToolResult.Error(error.Message)); }
            });
        McpTool Act(string name, string description, JsonObject schema, Func<JsonObject, string?> validate, Func<JsonObject, string> summary,
            Func<ContentStore, JsonObject, string> run) =>
            new(name, description, schema, true, async (args, cancel) =>
            {
                if (Store() is not { } store) return McpToolResult.Error(NoProject);
                if (validate(args) is { } problem) return McpToolResult.Error(problem);
                if (!await host.ApproveAsync(name, summary(args), cancel).ConfigureAwait(false))
                    return McpToolResult.Error("The user declined this in Solforge.");
                try { return McpToolResult.Text(run(store, args)); }
                catch (Exception error) when (Recoverable(error)) { return McpToolResult.Error(error.Message); }
            });
        /** String argument or empty text. */
        static string Arg(JsonObject args, string name) => McpJson.String(args, name) ?? "";
        /** Error text when the family argument is not a known content family; null when valid. */
        static string? NeedFamily(JsonObject args) => ContentFamilies.Find(McpJson.String(args, "family")) is null ? $"family must be {FamilyList}." : null;

        yield return Local("list_content_families", "Every content family Solforge edits (subclass, ability, damage_type, loot_pool, enemy, " +
            "activity, artifact) with whether the game reads it (live, partial, authoring_only), the server file it exports to, and notes; " +
            "plus content that should be moddable but is not yet, and why. Start here.", McpJson.Schema(), (_, _) => new JsonObject
            {
                ["families"] = new JsonArray(ContentFamilies.All.Select(family => (JsonNode)new JsonObject
                {
                    ["id"] = family.Id, ["title"] = family.Title, ["support"] = JsonNamingPolicy.SnakeCaseLower.ConvertName(family.Support.ToString()),
                    ["output"] = family.Output, ["notes"] = family.Notes
                }).ToArray()),
                ["not_yet_moddable"] = new JsonArray(ContentFamilies.NotYetModdable.Select(row => (JsonNode)new JsonObject
                    { ["content"] = row.Content, ["blocker"] = row.Blocker }).ToArray()),
                ["folder"] = "content/<family>/<id>.json"
            });
        yield return Local("list_content", "Content documents in the project, paged: family, id, name and issue counts.",
            Paged(("family", "string", "Optional family: " + FamilyList, false), ("filter", "string", "Optional words the id or name must contain", false)),
            (store, args) =>
            {
                string? family = McpJson.String(args, "family");
                if (family is not null && ContentFamilies.Find(family) is null) throw new ArgumentException($"family must be {FamilyList}.");
                var rows = store.LoadAll(family).Where(document => Matches(McpJson.String(args, "filter"), document.Id + " " + document.Name)).ToList();
                int offset = Math.Max(0, McpJson.Int(args, "offset", 0)), limit = Math.Clamp(McpJson.Int(args, "limit", 50), 1, Pages.MaximumLimit);
                return new JsonObject
                {
                    ["total"] = rows.Count, ["offset"] = offset,
                    ["rows"] = new JsonArray(rows.Skip(offset).Take(limit).Select(document =>
                    {
                        var issues = document.Validate();
                        return (JsonNode)new JsonObject
                        {
                            ["family"] = document.Family, ["id"] = document.Id, ["name"] = document.Name,
                            ["errors"] = issues.Count(issue => issue.Error), ["warnings"] = issues.Count(issue => !issue.Error)
                        };
                    }).ToArray())
                };
            });
        yield return Local("get_content", "One content document as JSON (the exact file shape update_content patches) with its issues, " +
            "including references to documents the project lacks.",
            McpJson.Schema(("family", "string", "Family: " + FamilyList, true), ("id", "string", "Document id", true)),
            (store, args) =>
            {
                var document = store.Load(Arg(args, "family"), Arg(args, "id"));
                return new JsonObject
                {
                    ["document"] = ContentJson.ToNode(document), ["file"] = store.PathOf(document.Family, document.Id),
                    ["issues"] = IssuesJson(store.ValidateAll(document.Family, document.Id).Select(row => row.Issue))
                };
            });
        yield return Local("validate_content", "Validate every content document (or one family, or one document): schema, limits, the server's " +
            "subclass rules, unreachable activity objectives, loot weights, and missing cross-references. Export refuses while errors remain.",
            McpJson.Schema(("family", "string", "Optional family", false), ("id", "string", "Optional document id (with family)", false)),
            (store, args) =>
            {
                var results = store.ValidateAll(McpJson.String(args, "family"), McpJson.String(args, "id"));
                return new JsonObject
                {
                    ["errors"] = results.Count(row => row.Issue.Error), ["warnings"] = results.Count(row => !row.Issue.Error),
                    ["issues"] = new JsonArray(results.Take(500).Select(row => (JsonNode)new JsonObject
                    {
                        ["family"] = row.Family, ["id"] = row.Id, ["path"] = row.Issue.Path, ["message"] = row.Issue.Message, ["error"] = row.Issue.Error
                    }).ToArray())
                };
            });
        yield return Local("get_subclass_layout", "The shipped subclass layout and rules Solforge mirrors from the Sol server: socket-entry " +
            "slots (class 2-3, jump 4-6, grenade 7-9, super 10, paths 11-14 / 15-18 / 20-23), node roles, today's server limits and the " +
            "planned six-node limits (plan.md D6).", McpJson.Schema(), (_, _) => new JsonObject
            {
                ["entry_count"] = SubclassLayout.EntryCount, ["sprint"] = SubclassLayout.SprintEntry, ["super"] = SubclassLayout.SuperEntry,
                ["class"] = Ints(SubclassLayout.ClassEntries), ["jump"] = Ints(SubclassLayout.JumpEntries), ["grenade"] = Ints(SubclassLayout.GrenadeEntries),
                ["paths"] = new JsonArray(SubclassLayout.TreeEntries.Select(entries => (JsonNode)Ints(entries)).ToArray()),
                ["roles"] = new JsonArray(Enum.GetNames<SubclassRole>().Select(role => (JsonNode)JsonNamingPolicy.SnakeCaseLower.ConvertName(role)).ToArray()),
                ["server_limits"] = new JsonObject { ["nodes"] = SubclassLimits.ServerNodes, ["perks"] = SubclassLimits.ServerPerks, ["per_path"] = SubclassLimits.ServerPerTree },
                ["planned_limits"] = new JsonObject { ["nodes"] = 6, ["perks"] = 5, ["per_path"] = 2, ["aspects"] = 2 },
                ["rules"] = new JsonArray(
                    "Exactly one super (the lane super or a path super), grenade, melee (a path node), class ability and jump.",
                    "Perks: at most per_path from one path and perks in all; super plus perks within nodes.",
                    "A perk that modifies the lane super is refused under a path super; modifiers from different paths exclude each other.",
                    "Aspects: at most limits.aspects; fragments: at most the picked aspects' fragment_slots.",
                    "Names and descriptions go live through Sunrise\\data\\subclass_nodes.tsv (export_content).")
            });
        yield return Local("check_subclass_build", "Check a subclass build against the subclass's rules, as the server would: give the " +
            "subclass id and a build {super, grenade, melee, class_ability, movement, perks[], aspects[], fragments[]} of node ids.",
            McpJson.Schema(("id", "string", "Subclass document id", true), ("build", "object", "The build (node ids)", true)),
            (store, args) =>
            {
                var subclass = store.Load(ContentFamilies.Subclass, Arg(args, "id")) as SubclassDefinition
                    ?? throw new InvalidDataException("Not a subclass.");
                var build = ObjectArg(args, "build")?.Deserialize<SubclassBuild>(ContentJson.Options) ?? throw new ArgumentException("Give build as an object.");
                var issues = SubclassRules.CheckBuild(subclass, build);
                return new JsonObject { ["valid"] = !issues.HasErrors, ["issues"] = IssuesJson(issues) };
            });
        yield return read("get_help", "Solforge's keybinds (with the user's current keys, grouped by area: General, Viewport, Hierarchy, " +
            "Inspector, Mission & node graphs, AI / MCP, Live) and tips (workflow, performance, MCP setup for other AI clients), as in " +
            "Help > Keybinds & Tips (F1); section tools maps every menu, panel and dialog to the MCP tools that do it (and the " +
            "run_command ids).",
            McpJson.Schema(("query", "string", "Optional words to search for", false), ("area", "string", "Optional area name", false),
                ("section", "string", "keybinds, tips, tools or all (default)", false)),
            async (args, cancel) =>
            {
                var result = await host.QueryAsync("get_help", args, cancel).ConfigureAwait(false);
                return result["error"] is JsonValue error ? McpToolResult.Error(error.ToString()) : McpToolResult.Json(result);
            });

        // Writes: each waits for the approve click.
        yield return Act("save_subclass_definition", "Create or replace a Solforge server subclass definition with explicit MCP controls. " +
            "Start from template='shipped' or 'abilities3', then optionally supply item_hash, base_item_hash, socket_entry_list, limits, " +
            "columns, trees, aspects, fragments, stats and default_build. Node controls include id, name, description, role, entry, ability, " +
            "sandbox_perks, stats, excludes, requires, modifies_super and fragment_slots. This writes Solforge server content, not a Parhelion package recipe.",
            McpJson.Schema(("id", "string", "Document id (a-z, 0-9, '_', '-', '.')", true),
                ("name", "string", "Subclass display name", true), ("class", "string", "titan, hunter, warlock or any", true),
                ("element", "string", "arc, solar, void, kinetic, stasis, strand or a project damage_type id", false),
                ("template", "string", "shipped or abilities3; defaults to shipped", false),
                ("item_hash", "string", "Existing or cloned subclass item hash", false),
                ("base_item_hash", "string", "Stock subclass item hash this definition clones", false),
                ("socket_entry_list", "integer", "Known native socket-entry-list index", false),
                ("limits", "object", "Selection limits: selectable_nodes, perks, per_tree and aspects", false),
                ("columns", "array", "D1 grid columns with required and nodes", false),
                ("trees", "array", "D2 paths with index, name, description and nodes", false),
                ("aspects", "array", "D2.0 aspect nodes including fragment_slots", false),
                ("fragments", "array", "D2.0 fragment nodes", false),
                ("stats", "object", "Subclass base stat effects keyed by stat name", false),
                ("default_build", "object", "Default node ids: super, grenade, melee, class_ability, movement, perks, aspects, fragments", false)),
            args => string.IsNullOrWhiteSpace(Arg(args, "id")) ? "Give an id." :
                string.IsNullOrWhiteSpace(Arg(args, "name")) ? "Give a name." :
                !Enum.TryParse<GuardianClass>(Arg(args, "class"), true, out _) ? "class must be titan, hunter, warlock or any." :
                McpJson.String(args, "template") is { Length: > 0 } template && template is not ("shipped" or "abilities3") ? "template must be shipped or abilities3." : null,
            args => $"Save subclass definition '{Arg(args, "id")}' ({Arg(args, "name")})",
            (store, args) =>
            {
                string id = Arg(args, "id"), name = Arg(args, "name");
                string element = (McpJson.String(args, "element") ?? "arc").ToLowerInvariant();
                var subclass = SubclassTemplates.New(id, name, SubclassInterop.ParseClass(Arg(args, "class")), element);
                if (McpJson.String(args, "template") == "abilities3") SubclassTemplates.AddAbilities3(subclass);
                var node = ContentJson.ToNode(subclass).AsObject();
                foreach (string field in new[] { "item_hash", "base_item_hash", "socket_entry_list", "limits", "columns", "trees", "aspects", "fragments", "stats", "default_build" })
                    if (args[field] is { } value) node[field] = value.DeepClone();
                var parsed = (SubclassDefinition)ContentJson.Parse(ContentFamilies.Subclass, node.ToJsonString());
                string path = store.Save(parsed);
                var issues = store.ValidateAll(ContentFamilies.Subclass, id);
                return $"Saved {path}. {issues.Count(row => row.Issue.Error)} error(s), {issues.Count(row => !row.Issue.Error)} warning(s)" +
                    string.Concat(issues.Take(20).Select(row => "\n- " + row.Issue));
            });
        yield return Act("create_content", "Create a content document. Give family, name and optionally id. Subclasses: class (titan, hunter, " +
            "warlock) and element start from the shipped layout (3 paths of 4 nodes, grenade/jump/class columns, a lane super); " +
            "from_server_view imports the Sol server's GET /api/subclass answer; from_item imports get_item's answer for a subclass item; " +
            "abilities3=true adds starter aspects and fragments. Or give document for the whole JSON. A new subclass item itself comes from " +
            "start_creation (key subclass); set its hash as item_hash.",
            McpJson.Schema(("family", "string", "Family: " + FamilyList, true), ("name", "string", "Display name", false),
                ("id", "string", "Optional id (a-z, 0-9, '_', '-', '.'); default from the name", false),
                ("class", "string", "Subclass or ability class: titan, hunter or warlock", false), ("element", "string", "arc, solar, void, kinetic or a damage_type id", false),
                ("document", "object", "Optional full document JSON (as get_content returns)", false),
                ("from_server_view", "object", "Optional: the server's /api/subclass answer (subclass family)", false),
                ("from_item", "object", "Optional: get_item's answer for a subclass item (subclass family)", false),
                ("abilities3", "boolean", "Subclass: add starter aspects and fragments", false)),
            args => NeedFamily(args) ?? (McpJson.String(args, "name") is { Length: > 0 } || args["document"] is not null || args["from_server_view"] is not null
                || args["from_item"] is not null ? null : "Give name (or document, from_server_view or from_item)."),
            args => $"Create {Arg(args, "family")} '{(McpJson.String(args, "id") is { Length: > 0 } id ? id : Arg(args, "name"))}'"
                + (args["from_server_view"] is not null ? " from the server's subclass view" : args["from_item"] is not null ? " from an item" : ""),
            (store, args) =>
            {
                string family = ContentFamilies.Find(Arg(args, "family"))!.Id;
                string? id = McpJson.String(args, "id") is { Length: > 0 } given ? given : null;
                var guardianClass = SubclassInterop.ParseClass(McpJson.String(args, "class"));
                ContentDocument document;
                if (ObjectArg(args, "document") is { } whole)
                {
                    if (id is not null) whole["id"] = id;
                    document = ContentJson.Parse(family, whole.ToJsonString());
                }
                else if (family == ContentFamilies.Subclass && ObjectArg(args, "from_server_view") is { } view)
                    document = SubclassInterop.FromServerView(view, id);
                else if (family == ContentFamilies.Subclass && ObjectArg(args, "from_item") is { } item)
                {
                    document = SubclassInterop.FromItem(item, guardianClass, id);
                    if (McpJson.String(args, "element") is { Length: > 0 } element) ((SubclassDefinition)document).Element = element.ToLowerInvariant();
                }
                else
                {
                    string name = Arg(args, "name");
                    document = ContentStore.NewDocument(family, id ?? SubclassInterop.Slug(name), name, guardianClass,
                        (McpJson.String(args, "element") ?? "arc").ToLowerInvariant());
                }
                if (McpJson.String(args, "name") is { Length: > 0 } rename) document.Name = rename;
                if (document is SubclassDefinition subclass && McpJson.Bool(args, "abilities3", false)) SubclassTemplates.AddAbilities3(subclass);
                if (store.Exists(document.Family, document.Id)) throw new InvalidOperationException($"{document.Family} '{document.Id}' already exists; use update_content.");
                string path = store.Save(document);
                var issues = store.ValidateAll(document.Family, document.Id);
                return $"Created {path}. {issues.Count(row => row.Issue.Error)} error(s), {issues.Count(row => !row.Issue.Error)} warning(s)"
                    + string.Concat(issues.Take(10).Select(row => "\n- " + row.Issue));
            });
        yield return Act("update_content", "Change a content document: patch is a JSON merge patch (RFC 7396) on the document as get_content " +
            "returns it (null removes a member; arrays such as trees or nodes are replaced whole), or document replaces it. The id cannot change.",
            McpJson.Schema(("family", "string", "Family", true), ("id", "string", "Document id", true),
                ("patch", "object", "Merge patch", false), ("document", "object", "Whole replacement", false)),
            args => NeedFamily(args) ?? (args["patch"] is null == args["document"] is null ? "Give either patch or document." : null),
            args => $"Update {Arg(args, "family")} '{Arg(args, "id")}': " + Quote((args["patch"] ?? args["document"])?.ToJsonString()),
            (store, args) =>
            {
                string family = ContentFamilies.Find(Arg(args, "family"))!.Id, id = Arg(args, "id");
                var current = ContentJson.ToNode(store.Load(family, id));
                var next = ObjectArg(args, "document") ?? ContentJson.MergePatch(current, ObjectArg(args, "patch") ?? throw new ArgumentException("patch must be an object."));
                next["id"] = id;
                var document = ContentJson.Parse(family, next.ToJsonString());
                string path = store.Save(document);
                var issues = store.ValidateAll(family, id);
                return $"Saved {path}. {issues.Count(row => row.Issue.Error)} error(s), {issues.Count(row => !row.Issue.Error)} warning(s)"
                    + string.Concat(issues.Take(10).Select(row => "\n- " + row.Issue));
            });
        yield return Act("delete_content", "Delete a content document's file from the project.",
            McpJson.Schema(("family", "string", "Family", true), ("id", "string", "Document id", true)),
            NeedFamily, args => $"Delete {Arg(args, "family")} '{Arg(args, "id")}'",
            (store, args) => store.Delete(ContentFamilies.Find(Arg(args, "family"))!.Id, Arg(args, "id")) ? "Deleted." : "No such document.");
        yield return Act("export_content", "Export the project's content under <project>/build (or output, a folder inside the project): " +
            "server/scripts/loot_pools.lua (live: copy to Sunrise\\scripts beside the server), server/data/subclass_nodes.tsv (live node names: copy to " +
            "Sunrise\\data) and server/scripts/sol_custom_data.lua (every document as plain data; the server applies each family's live parts: copy to Sunrise\\scripts). Refuses while validate_content reports errors.",
            McpJson.Schema(("output", "string", "Optional project-relative folder (default build)", false)),
            args => McpJson.String(args, "output") is { Length: > 0 } output && (Path.IsPathRooted(output) || output.Contains("..")) ? "output must be a folder inside the project." : null,
            args => "Export content to " + (McpJson.String(args, "output") is { Length: > 0 } output ? output : "build"),
            (store, args) =>
            {
                var errors = store.ValidateAll().Where(row => row.Issue.Error).ToList();
                if (errors.Count > 0)
                    return $"Not exported: {errors.Count} error(s)." + string.Concat(errors.Take(10).Select(row => $"\n- {row.Family} '{row.Id}': {row.Issue}"));
                string output = Path.Combine(host.ProjectRoot!, McpJson.String(args, "output") is { Length: > 0 } relative ? relative : "build");
                var written = ContentExport.WriteTo(output, ContentExport.Build(store.LoadAll()));
                return written.Count == 0 ? "No content to export." : "Wrote:\n" + string.Join("\n", written);
            });
    }

    /** JSON array of integers. */
    private static JsonArray Ints(IEnumerable<int> values) => new(values.Select(value => (JsonNode)value).ToArray());

    /** Whether text contains every space-separated word of the query (an empty query matches). */
    private static bool Matches(string? query, string text) => query is not { Length: > 0 }
        || query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));

    /** Whether an exception is an expected file or argument problem to report as a tool error instead of crashing. */
    private static bool Recoverable(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
        or ArgumentException or InvalidOperationException or NotSupportedException or KeyNotFoundException;
}
