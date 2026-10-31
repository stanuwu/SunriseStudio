/**
 * File: get_perk_behavior, list_perk_node_types, edit_perk_behavior and create_perk (SolforgeTools.Perks.cs defines
 * the tools): sandbox perk programs read and written through NativePerkBehavior, as the Perk Behavior page does.
 */
using System.Globalization;
using System.Text.Json.Nodes;
using Solforge.Mcp;
using SunriseStudio.Formats;
using SunriseStudio.Packages;

namespace SunriseStudio.Editor;

/** MCP access to perk behavior programs (part of StudioForm). */
public sealed partial class StudioForm
{
    /** Dispatches get_perk_behavior and list_perk_node_types. */
    private async Task<JsonObject> AiPerkQueryAsync(string tool, JsonObject args, CancellationToken cancel)
    {
        if (tool == "list_perk_node_types") return AiPerkNodeTypes(McpJson.String(args, "query"));
        var (catalog, investment) = await OnUi(() => (workspace.Catalog, workspace.Investment)).WaitAsync(cancel).ConfigureAwait(false);
        if (catalog is null) return AiError("No installation is open.");
        return await Task.Run(() =>
        {
            var (tag, index, problem) = AiResolvePerkProgram(catalog, investment, McpJson.String(args, "perk") ?? "");
            if (problem is not null) return AiError(problem);
            var graph = NativePerkBehavior.Decompile(catalog.Read(tag));
            var result = AiPerkGraphJson(graph);
            result["programTag"] = Hex(tag);
            result["perkIndex"] = index;
            result["name"] = index is int row && investment is not null && row < investment.PerkDefinitions.Count
                ? investment.PerkDefinitions[row].Name : null;
            return result;
        }, cancel).WaitAsync(AiReadTimeout, cancel).ConfigureAwait(false);
    }

    /** edit_perk_behavior and create_perk; runs on the UI thread after the approve click. */
    private string AiPerkAction(string tool, JsonObject args)
    {
        if (!workspace.CanEdit || workspace.Project is null || workspace.Catalog is not { } catalog) return "No project is open, or Solforge is busy.";
        try
        {
            return tool == "create_perk" ? AiCreatePerk(args) : AiEditPerk(catalog, args);
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException or KeyNotFoundException
            or FormatException or OverflowException)
        {
            return error.Message;
        }
    }

    private string AiEditPerk(PackageCatalog catalog, JsonObject args)
    {
        var (tag, _, problem) = AiResolvePerkProgram(catalog, workspace.Investment, McpJson.String(args, "perk") ?? "");
        if (problem is not null) return problem;
        // ApplyAssetEdits records undo against the open document, as revert_to_base does.
        if (workspace.Document is null) { workspace.Run(() => workspace.Select(tag)); return "Opened the perk program; call edit_perk_behavior again once it shows."; }
        var graph = NativePerkBehavior.Decompile(catalog.Read(tag));
        var source = NativePerkBehavior.Decompile(catalog.Read(tag));
        var notes = new List<string>();
        foreach (var op in (JsonArray)args["ops"]!)
        {
            var row = (JsonObject)op!;
            string kind = (string?)row["op"] ?? "";
            if (kind == "hud")
            {
                uint label = (uint)AiPerkNumber(row["label"], 0);
                graph.Hud = label == 0 ? null : new PerkHudStatus { Label = label, ShowTimer = AiPerkNumber(row["show_timer"], 1) != 0 };
                notes.Add(label == 0 ? "HUD status cleared" : $"HUD status {Hex(label)}");
                continue;
            }
            var phase = AiPerkPhase(graph, (string?)row["phase"]);
            switch (kind)
            {
                case "add":
                {
                    var node = AiPerkNode(row["node"] as JsonObject ?? throw new InvalidDataException("add needs node {primitive, values}."));
                    int at = row["at"] is null ? phase.Count : (int)AiPerkNumber(row["at"], phase.Count);
                    if (at < 0 || at > phase.Count) throw new InvalidDataException($"at {at} is outside 0..{phase.Count}.");
                    phase.Insert(at, node);
                    notes.Add($"added {node.Primitive} at {(string?)row["phase"]}[{at}]");
                    break;
                }
                case "set":
                {
                    var node = AiPerkAt(phase, row);
                    if (!node.Editable) throw new InvalidDataException($"{node.Title} is kept byte for byte and cannot be edited.");
                    AiPerkSetValues(node, row["values"] as JsonObject);
                    if (row["inner"] is JsonObject inner)
                        AiPerkSetValues(node.Children.FirstOrDefault() ?? throw new InvalidDataException($"{node.Title} has no inner condition."), inner);
                    notes.Add($"set {node.Primitive} {(string?)row["phase"]}[{(int)AiPerkNumber(row["index"], -1)}]");
                    break;
                }
                case "remove":
                {
                    var node = AiPerkAt(phase, row);
                    phase.Remove(node);
                    notes.Add($"removed {node.Primitive}");
                    break;
                }
                case "labels":
                {
                    var node = AiPerkAt(phase, row);
                    string key = (string?)row["filter"] ?? "";
                    var filter = node.LabelFilters.FirstOrDefault(item => item.Key == key)
                        ?? throw new InvalidDataException($"{node.Title} has no label filter '{key}'. Filters: {string.Join(", ", node.LabelFilters.Select(item => item.Key))}.");
                    if (!filter.Editable) throw new InvalidDataException($"{node.Title} filter '{key}' is read-only.");
                    AiPerkLabels(filter.AnyOf, row["anyOf"]); AiPerkLabels(filter.AllOf, row["allOf"]);
                    AiPerkLabels(filter.NoneOf, row["noneOf"]); AiPerkLabels(filter.NotAllOf, row["notAllOf"]);
                    notes.Add($"set {key} labels of {node.Primitive}");
                    break;
                }
                default: throw new InvalidDataException($"Unknown op '{kind}'.");
            }
        }
        // Label filters validate and compile against the installed label table, as custom perks do.
        if (graph.Activation.Concat(graph.Effects).Concat(graph.Deactivation)
            .Any(node => node.LabelFilters.Any(filter => filter.Arrays().Any(array => array.Count != 0))))
            PerkLabelTable.Installed ??= PerkLabelTable.Parse(catalog.Read(PerkLabelTable.Tag))
                ?? throw new InvalidDataException("The installed perk label table cannot be read.");
        var problems = NativePerkBehavior.Validate(graph, source);
        if (problems.Count != 0) return "Not saved: " + string.Join(" ", problems);
        byte[] compiled = NativePerkBehavior.Compile(graph);
        workspace.Run(() => workspace.ApplyAssetEdits(new Dictionary<uint, byte[]> { [tag] = compiled }, "Saved perk behavior"));
        if (workspace.Error.Length > 0) return workspace.Error;
        return $"Saved perk program {Hex(tag)} ({compiled.Length} bytes): {string.Join("; ", notes)}. It is in Project changes with undo; "
            + "it changes every item with this perk. Untested node kinds may do nothing in game.";
    }

    private string AiCreatePerk(JsonObject args)
    {
        string name = (McpJson.String(args, "name") ?? "").Trim(), description = McpJson.String(args, "description") ?? "";
        if (workspace.LastStandalonePerk is { } last && last.Name == name)
            return $"Perk '{name}' is sandbox perk {last.PerkIndex}, program {Hex(last.ProgramTag)}. It is in Project changes with undo.";
        PerkBehaviorGraph graph, source = new();
        if (McpJson.String(args, "from") is { Length: > 0 } from)
        {
            // A copy keeps every installed node byte for byte; the arrays below are added after its own nodes.
            var (fromTag, _, problem) = AiResolvePerkProgram(workspace.Catalog!, workspace.Investment, from);
            if (problem is not null) return problem;
            graph = NativePerkBehavior.Decompile(workspace.Catalog!.Read(fromTag));
            source = NativePerkBehavior.Decompile(workspace.Catalog!.Read(fromTag));
        }
        else graph = NativePerkBehavior.New();
        AiPerkFill(graph.Activation, args["activation"] as JsonArray);
        AiPerkFill(graph.Effects, args["effects"] as JsonArray);
        if (args["deactivation"] is JsonArray { Count: > 0 } off) AiPerkFill(graph.Deactivation, off);
        else if (graph.Deactivation.Count == 0)
        {
            var timer = NativePerkBehavior.CreateNode("timer");
            timer.Properties.First(row => row.Key == "seconds").Value = 0.1;
            graph.Deactivation.Add(timer);
        }
        if (graph.Activation.Concat(graph.Effects).Concat(graph.Deactivation)
            .Any(node => node.LabelFilters.Any(filter => filter.Arrays().Any(array => array.Count != 0))))
            PerkLabelTable.Installed ??= PerkLabelTable.Parse(workspace.Catalog!.Read(PerkLabelTable.Tag))
                ?? throw new InvalidDataException("The installed perk label table cannot be read.");
        var problems = NativePerkBehavior.Validate(graph, graph.OriginalBytes.Length == 0 ? null : source);
        if (problems.Count != 0) return "Not created: " + string.Join(" ", problems);
        ushort package = McpJson.String(args, "package") is { Length: > 0 } text
            ? ushort.Parse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : workspace.CustomPerkPackage() ?? throw new InvalidDataException("Give an unused package ID (hex).");
        workspace.Run(() => workspace.CreateStandalonePerk(name, description, graph, package));
        return workspace.Error.Length > 0 ? workspace.Error
            : $"Registering '{name}' in package {package:X4}; call create_perk again with the same name for its sandbox perk index once get_build_status is idle.";
    }

    /** Program tag and perk index for a perk index, exact perk name or program tag; or why not. */
    private static (uint Tag, int? Index, string? Problem) AiResolvePerkProgram(PackageCatalog catalog, InvestmentCatalog? investment, string text)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (SolforgeTools.ParseHash(text) is not { } tag || !catalog.TryGetEntry(tag, out var entry)) return (0, null, $"No tag {text}.");
            return entry.Reference == NativePerkBehavior.Class ? (tag, null, null) : (0, null, $"{text} is not a perk program (class {Hex(NativePerkBehavior.Class)}).");
        }
        int index;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number)) index = number;
        else if (investment?.PerkDefinitions.FirstOrDefault(perk => string.Equals(perk.Name, text, StringComparison.OrdinalIgnoreCase)) is { } named)
            index = named.Index;
        else return (0, null, investment is null ? "Perk names are still loading; give the index." : $"No perk named '{text}'; use search_perks.");
        try
        {
            var source = InvestmentSandboxPerkAuthoring.Read(catalog, index);
            return (source.GameplayTag, index, null);
        }
        catch (InvalidDataException error) { return (0, index, $"Perk {index} has no behavior program: {error.Message}"); }
    }

    private static JsonObject AiPerkNodeTypes(string? query) => new()
    {
        ["types"] = new JsonArray([.. NativePerkBehavior.Primitives
            .Where(kind => kind.CanCreate && (string.IsNullOrWhiteSpace(query) || $"{kind.Key} {kind.Name} {kind.Description}".Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)))
            .Select(kind => (JsonNode)new JsonObject
            {
                ["primitive"] = kind.Key, ["name"] = kind.Name, ["kind"] = kind.Kind == PerkBehaviorNodeKind.Condition ? "condition" : "effect",
                ["tested"] = NativePerkBehavior.IsTested(kind.Key), ["description"] = kind.Description,
                ["inputs"] = AiPerkProperties(NativePerkBehavior.CreateNode(kind.Key).Properties)
            })]),
        ["phases"] = "activation holds conditions, effects holds effects, deactivation holds conditions (usually one timer)."
    };

    private static JsonObject AiPerkGraphJson(PerkBehaviorGraph graph) => new()
    {
        ["hud"] = graph.Hud is { } hud ? new JsonObject { ["label"] = Hex(hud.Label), ["showTimer"] = hud.ShowTimer } : null,
        ["activation"] = AiPerkNodes(graph.Activation), ["effects"] = AiPerkNodes(graph.Effects),
        ["deactivation"] = AiPerkNodes(graph.Deactivation),
        ["diagnostics"] = new JsonArray([.. graph.Diagnostics.Select(text => (JsonNode)text)])
    };

    private static JsonArray AiPerkNodes(IEnumerable<PerkBehaviorNode> nodes) => new([.. nodes.Select((node, index) => (JsonNode)new JsonObject
    {
        ["index"] = index, ["primitive"] = node.Primitive, ["title"] = node.Title, ["class"] = Hex(node.NativeClass),
        ["editable"] = node.Editable, ["tested"] = NativePerkBehavior.IsTested(node.Primitive),
        ["inputs"] = AiPerkProperties(node.Properties),
        ["inner"] = node.Children.Count == 0 ? null : AiPerkNodes(node.Children),
        ["labelFilters"] = node.LabelFilters.Count == 0 ? null : new JsonArray([.. node.LabelFilters.Select(filter => (JsonNode)new JsonObject
        {
            ["key"] = filter.Key, ["label"] = filter.Label,
            ["anyOf"] = Hashes(filter.AnyOf), ["allOf"] = Hashes(filter.AllOf), ["noneOf"] = Hashes(filter.NoneOf), ["notAllOf"] = Hashes(filter.NotAllOf)
        })])
    })]);

    private static JsonArray Hashes(IEnumerable<uint> values) => new([.. values.Select(value => (JsonNode)Hex(value))]);

    private static JsonArray AiPerkProperties(IEnumerable<PerkBehaviorProperty> rows) => new([.. rows.Select(row => (JsonNode)new JsonObject
    {
        ["key"] = row.Key, ["label"] = row.Label, ["kind"] = row.Kind.ToString(),
        ["value"] = row.Kind is PerkBehaviorValueKind.Hash or PerkBehaviorValueKind.Tag ? Hex((uint)row.Value) : JsonValue.Create(row.Value),
        ["min"] = row.Minimum, ["max"] = row.Maximum, ["editable"] = row.Editable,
        ["choices"] = row.Choices.Count == 0 ? null : new JsonArray([.. row.Choices.Select(choice => (JsonNode)$"{choice.Value}={choice.Label}")]),
        ["description"] = row.Description.Length == 0 ? null : row.Description
    })]);

    private static List<PerkBehaviorNode> AiPerkPhase(PerkBehaviorGraph graph, string? phase) => phase switch
    {
        "activation" => graph.Activation, "effects" => graph.Effects, "deactivation" => graph.Deactivation,
        _ => throw new InvalidDataException("phase must be activation, effects or deactivation.")
    };

    private static PerkBehaviorNode AiPerkAt(List<PerkBehaviorNode> phase, JsonObject row)
    {
        int index = (int)AiPerkNumber(row["index"], -1);
        return index >= 0 && index < phase.Count ? phase[index] : throw new InvalidDataException($"index {index} is outside 0..{phase.Count - 1}.");
    }

    private static void AiPerkFill(List<PerkBehaviorNode> phase, JsonArray? nodes)
    {
        foreach (var node in nodes ?? []) phase.Add(AiPerkNode(node as JsonObject ?? throw new InvalidDataException("Each node is an object.")));
    }

    /** A new node from {primitive, values, inner}. */
    private static PerkBehaviorNode AiPerkNode(JsonObject spec)
    {
        string primitive = (string?)spec["primitive"] ?? throw new InvalidDataException("A node needs a primitive (list_perk_node_types).");
        var node = NativePerkBehavior.CreateNode(primitive);
        AiPerkSetValues(node, spec["values"] as JsonObject);
        if (spec["inner"] is JsonObject inner)
            AiPerkSetValues(node.Children.FirstOrDefault() ?? throw new InvalidDataException($"{primitive} has no inner condition."), inner);
        return node;
    }

    private static void AiPerkSetValues(PerkBehaviorNode node, JsonObject? values)
    {
        foreach (var (key, value) in values ?? [])
        {
            var row = node.Properties.FirstOrDefault(property => property.Key == key)
                ?? throw new InvalidDataException($"{node.Title} has no input '{key}'. Inputs: {string.Join(", ", node.Properties.Select(property => property.Key))}.");
            if (!row.Editable) throw new InvalidDataException($"{node.Title} input '{key}' is read-only.");
            row.Value = AiPerkNumber(value, double.NaN);
            if (double.IsNaN(row.Value)) throw new InvalidDataException($"{node.Title} input '{key}' needs a number or 0x hex.");
        }
    }

    /** Replaces a label list with the given hashes (0x hex or numbers); absent leaves it as it is. */
    private static void AiPerkLabels(List<uint> target, JsonNode? values)
    {
        if (values is null) return;
        if (values is not JsonArray array) throw new InvalidDataException("Label lists are arrays of hashes.");
        target.Clear();
        foreach (var value in array)
        {
            double hash = AiPerkNumber(value, double.NaN);
            if (double.IsNaN(hash) || hash < 0 || hash > uint.MaxValue) throw new InvalidDataException("A label is a hash such as 0x905E8440.");
            target.Add((uint)hash);
        }
    }

    /** A JSON number, boolean, decimal string or 0x hex string; fallback when absent or unreadable. */
    private static double AiPerkNumber(JsonNode? node, double fallback)
    {
        if (node is not JsonValue value) return fallback;
        if (value.TryGetValue(out double number)) return number;
        if (value.TryGetValue(out bool flag)) return flag ? 1 : 0;
        if (value.TryGetValue(out string? text) && text is not null)
        {
            text = text.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex)) return hex;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)) return parsed;
        }
        return fallback;
    }
}
