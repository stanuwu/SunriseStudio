using System.Text.Json;
using SunriseStudio.Missions;

/** Typed mission API nodes: the schema reader, receiver typing, span-exact edits and the runtime's checks. */
internal static class ApiNodeChecks
{
    internal static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "mission_api.json");

    private const string Script = "local missions = require(\"missions\")\n"
        + "local mission = require(missions.MISSION_X)\n"
        + "local Slot = mission.Slot\n"
        + "local function helper(context)\n    context:set_variable(\"a\", 1)\nend\n"
        + "return {\n"
        + "    on_start = function(context, state)\n"
        + "        local door = context:slot(Slot.DOOR)\n"
        + "        door:transition{transition = context.sdk.device_transitions.open, snap = false}\n"
        + "        context:slot(Slot.SENSOR):set_directive{directive = mission.Directive.GOAL, state = 3, bogus = 1}\n"
        + "        context:slot(Slot.DOOR):set_directive{directive = mission.Directive.GOAL}\n"
        + "        graph:advance(context, state)\n"
        + "        handlers[1](context)\n"
        + "        state:variable(\"x\")\n"
        + "        unknown:set_directive{}\n"
        + "        context.sdk.unit(0.5)\n"
        + "        context:scene(mission.Scene.SC):activate()\n"
        + "        context:scene(mission.Scene.SC):send_event{\n"
        + "            key = mission.SceneInput.OTHER.GO, -- wrong scene\n"
        + "        }\n"
        + "        context:activate_objects{slots = {Slot.DOOR, Slot.CRATE}}\n"
        + "        context:slot(Slot.ACTOR):run_atoms{atoms = {{kind = \"sleep\", seconds = 1}}}\n"
        + "    end,\n"
        + "    on_event_scene_output = function(context, state, event)\n"
        + "        if event.key == 1 then event.slot:disarm_trigger() end\n"
        + "    end,\n"
        + "}\n";

    private static readonly MissionSdkReference[] References =
    [
        new("mission.Slot.DOOR", "slots", "door", "slot/1") { SlotType = 23 },
        new("mission.Slot.SENSOR", "slots", "sensor", "slot/2") { SlotType = 68 },
        new("mission.Slot.NAV", "slots", "nav", "slot/3") { SlotType = 47 },
        new("mission.Slot.CRATE", "slots", "crate", "slot/4") { SlotType = 4 },
        new("mission.Slot.ACTOR", "slots", "actor", "slot/5") { SlotType = 2 },
        new("mission.Directive.GOAL", "directives", "goal", "directive/1") { Owner = "SENSOR" },
        new("mission.Directive.ELSEWHERE", "directives", "elsewhere", "directive/2") { Owner = "OTHER_SENSOR" },
        new("mission.Scene.SC", "scenes", "scene", "scene/1"),
        new("mission.SceneInput.SC.START", "scene_inputs", "start", "0x00000001") { Scene = "SC" },
        new("mission.SceneInput.OTHER.GO", "scene_inputs", "go", "0x00000002") { Scene = "OTHER" },
    ];

    public static void Synthetic()
    {
        var schema = MissionApiSchema.Load(Fixture);
        string json = File.ReadAllText(Fixture);
        int receivers = FixtureCount(json, "receivers"), members = FixtureCount(json, "members"), events = FixtureCount(json, "events");
        Check(schema.Receivers.Count == receivers && schema.Members.Count == members && schema.Events.Count == events
            && schema.Program.EventParameters.SequenceEqual(["context", "state", "event"]),
            $"The schema fixture did not read whole: receivers {schema.Receivers.Count}/{receivers}, members {schema.Members.Count}/{members}, "
            + $"events {schema.Events.Count}/{events}, event_parameters={string.Join(",", schema.Program.EventParameters)}.");
        // The teleport request reads as a callable with its two required arguments.
        Check(schema.Members.Single(member => member.Name == "teleport") is { Callable: true } teleport
            && teleport.Arguments.Select(argument => argument.Name).SequenceEqual(["region", "spawn_set_hash"]),
            "The schema fixture does not hold context:teleport{region, spawn_set_hash}.");
        Reject(() => MissionApiSchema.Parse(json.Replace(MissionApiSchema.SchemaName, "sunrise-mission-lua-api-v2", StringComparison.Ordinal)), "Studio reads");
        Reject(() => MissionApiSchema.Parse(json.Replace("\"kind\": \"boolean\"", "\"kind\": \"bool\"", StringComparison.Ordinal)), "value_kinds");
        Reject(() => MissionApiSchema.Parse("{"), "not valid JSON");

        var analysis = Analyze(Script);
        string Outcome(string callee) => string.Join(",", analysis.Calls.Where(call => call.Call.Callee.Read(Script).EndsWith(callee, StringComparison.Ordinal))
            .Select(call => call.Outcome));
        Check(Outcome(":transition") == "Typed" && Outcome(":set_directive") == "Typed,Typed,UnknownReceiver"
            && Outcome(":advance") == "NotApi" && Outcome("handlers[1]") == "Dynamic" && Outcome("state:variable") == "Typed"
            && Outcome("context:set_variable") == "Typed" && Outcome("sdk.unit") == "Typed" && Outcome(":disarm_trigger") == "Typed"
            && Outcome("require") == "NotApi,NotApi", "A call was typed wrongly: " + string.Join("; ",
                analysis.Calls.Select(call => call.Call.Callee.Read(Script) + "=" + call.Outcome)));
        var transition = Node(analysis, ":transition");
        Check(transition.Receiver is { Receiver: "slot", SlotType: 23 } && transition.Findings.Count == 0
            && transition.Find("transition") is { Widget: MissionApiWidget.Choice, Literal: true, Choice: "open" }
            && transition.Find("snap") is { Widget: MissionApiWidget.Boolean, Literal: true }, "A local slot handle did not type its call.");
        var directive = Node(analysis, "SENSOR):set_directive");
        Check(directive.Findings.Any(value => value.Path == "state" && value.Message.Contains("range is 0..2", StringComparison.Ordinal))
            && directive.Findings.Any(value => value.Path == "bogus")
            && directive.Find("directive") is { Literal: true, Reference.Key: "GOAL", Choices: [{ Key: "GOAL" }] }
            && directive.Find("navpoint") is { Present: false, Widget: MissionApiWidget.SlotHandle, Choices: [{ Key: "NAV" }] },
            "The set_directive checks are wrong: " + string.Join("; ", directive.Findings.Select(value => value.Message)));
        Check(Node(analysis, "DOOR):set_directive").Findings.Any(value => value.Message.Contains("type-68", StringComparison.Ordinal)),
            "A wrong receiver slot type was not reported.");
        Check(Node(analysis, "send_event").Findings.Any(value => value.Message.Contains("belongs to OTHER", StringComparison.Ordinal)),
            "A scene input of another scene was not reported.");
        Check(Node(analysis, "activate_objects").Find("slots[2]") is { Reference.Key: "CRATE" } items
            && Node(analysis, "activate_objects").Findings.Any(value => value.Path == "slots[1]"), "A list item was not typed and gated.");

        var atoms = Node(analysis, "run_atoms");
        Check(atoms.Findings.Count == 0 && atoms.Find("atoms[1]") is { Widget: MissionApiWidget.Atom, Literal: true, Choice: "sleep" }
            && atoms.Find("atoms[1].seconds") is { Widget: MissionApiWidget.Number, Literal: true }, "An atom did not type by its kind.");
        Check(MissionApiWriter.SetAtomKind(atoms, "atoms[1]", "trivial") == Script.Replace("\"sleep\"", "\"trivial\"", StringComparison.Ordinal),
            "An atom kind change is not exact.");
        Check(MissionApiWriter.AddItem(atoms, "atoms") == Script.Replace("seconds = 1}}", "seconds = 1}, {kind = \"face\", target = context:slot(Slot.DOOR)}}",
            StringComparison.Ordinal), "A new atom is not a valid placeholder.");

        // Value edits replace one span.
        string edited = MissionApiWriter.SetValue(directive, "state", "1");
        Check(edited == Script.Replace("state = 3", "state = 1", StringComparison.Ordinal), "A value edit touched other source.");
        Check(MissionApiWriter.SetValue(directive, "state", "3") == Script, "A no-op edit changed the source.");
        string choice = MissionApiWriter.SetValue(transition, "transition", MissionApiWriter.ChoiceText(transition, transition.Find("transition")!, "close"));
        Check(choice == Script.Replace("device_transitions.open", "device_transitions.close", StringComparison.Ordinal), "An enum edit is not exact.");
        // Optional arguments follow the table's style.
        string added = MissionApiWriter.Add(directive, "navpoint");
        Check(added == Script.Replace("bogus = 1}", "bogus = 1, navpoint = context:slot(Slot.NAV)}", StringComparison.Ordinal), "Add did not match a one-line table: " + added);
        string removed = MissionApiWriter.Remove(directive, "state");
        Check(removed == Script.Replace("state = 3, ", "", StringComparison.Ordinal), "Remove left a separator.");
        string snapRemoved = MissionApiWriter.Remove(transition, "snap");
        Check(snapRemoved == Script.Replace(", snap = false}", "}", StringComparison.Ordinal), "Removing the last field left its separator.");
        string activate = MissionApiWriter.Add(Node(analysis, ":activate"), "spawn");
        Check(activate == Script.Replace(":activate()", ":activate{spawn = false}", StringComparison.Ordinal), "An argument was not added to an empty call.");
        var send = Node(analysis, "send_event");
        Check(MissionApiWriter.SetValue(send, "key", "mission.SceneInput.SC.START")
            == Script.Replace("mission.SceneInput.OTHER.GO", "mission.SceneInput.SC.START", StringComparison.Ordinal), "A multi-line value edit is not exact.");
        Reject(() => MissionApiWriter.Remove(send, "key"), "required");
        Reject(() => MissionApiWriter.SetValue(send, "key", "1 -- comment"), "one Lua expression");

        // A multi-line table keeps its trailing separator and its comments.
        const string lines = "return {on_start = function(context, state)\r\n    context:slot(mission.Slot.SENSOR):set_directive{\r\n"
            + "        directive = mission.Directive.GOAL, -- goal\r\n    }\r\nend}\r\n";
        var multi = Node(Analyze(lines), "set_directive");
        string multiAdded = MissionApiWriter.Add(multi, "state");
        Check(multiAdded == lines.Replace("-- goal\r\n", "-- goal\r\n        state = 0,\r\n", StringComparison.Ordinal), "Add broke a multi-line table: " + multiAdded);
        var reread = Node(Analyze(multiAdded), "set_directive");
        Check(MissionApiWriter.Remove(reread, "state") == lines, "Remove did not restore the multi-line table.");
        const string bare = "return {on_start = function(context, state)\n    context:slot(mission.Slot.SENSOR):set_directive{\n"
            + "        directive = mission.Directive.GOAL\n    }\nend}\n";
        string bareAdded = MissionApiWriter.Add(Node(Analyze(bare), "set_directive"), "state");
        Check(bareAdded == bare.Replace("GOAL\n", "GOAL,\n        state = 0\n", StringComparison.Ordinal), "Add did not keep a table without a trailing separator: " + bareAdded);
        Check(MissionApiWriter.Remove(Node(Analyze(bareAdded), "set_directive"), "state") == bare, "Remove left the new last field's separator.");

        // Creation writes valid calls and callbacks.
        var names = analysis.Calls.First(call => call.Outcome == MissionApiOutcome.Typed && call.Names.ContainsKey("door")).Names;
        var place = schema.Members.Single(member => member.Name == "set_directive");
        var receiver = MissionApiWriter.ReceiverText(analysis, place, names) ?? throw new InvalidDataException("No receiver reaches set_directive.");
        string call = MissionApiWriter.CallText(analysis, place, receiver.Text, names, receiver.Type);
        Check(call == "context:slot(Slot.SENSOR):set_directive{directive = mission.Directive.GOAL}", "A new call is not valid: " + call);
        Check(MissionApiWriter.CallbackText(schema, "on_event_squad_state") == "on_event_squad_state = function(context, state, event)\nend",
            "A new callback does not take the schema's parameters.");
        Console.WriteLine("mission_api schema=strict typing=pass edits=exact separators=styled checks=pass create=valid");
    }

    /** An installation reads its own schema; without one, or with another schema, typed nodes are off and say why. */
    public static void InstalledSchema()
    {
        string json = File.ReadAllText(Fixture);
        string game = Path.Combine(Path.GetTempPath(), "sunrise-api-schema-" + Guid.NewGuid().ToString("N"));
        string installed = MissionApiSchema.InstalledPath(Path.Combine(game, "bin", "x64", "Sunrise", "sdk", "lua"));
        try
        {
            Check(new MissionScriptCatalog(game) is { Api: null, ApiStatus: var missing } && missing.Contains("mission_api.json", StringComparison.Ordinal),
                "A missing schema did not say what is missing.");
            Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
            File.WriteAllText(installed, json);
            Check(new MissionScriptCatalog(game).Api?.Members.Count == FixtureCount(json, "members"), "The installed schema did not load.");
            File.WriteAllText(installed, json.Replace(MissionApiSchema.SchemaName, "sunrise-mission-lua-api-v2", StringComparison.Ordinal));
            Check(new MissionScriptCatalog(game) is { Api: null, ApiStatus: var other } && other.Contains("Studio reads", StringComparison.Ordinal),
                "Another schema version did not refuse with a clear message.");
        }
        finally { if (Directory.Exists(game)) Directory.Delete(game, true); }
    }

    /**
     * Every installed script, lib included, with the fixture schema: sources round-trip, every typed parameter takes a
     * no-op edit, and every literal parameter takes a real edit of exactly its span.
     * @param references SDK references by script name; other folders get none.
     */
    public static void Installed(string gameRoot, IReadOnlyDictionary<string, IReadOnlyList<MissionSdkReference>> references)
    {
        var schema = MissionApiSchema.Load(Fixture);
        var catalog = new MissionScriptCatalog(gameRoot);
        var outcomes = new Dictionary<string, int>(StringComparer.Ordinal);
        var findings = new List<string>();
        var gaps = new List<string>();
        var unknown = new List<string>();
        var widgets = new Dictionary<string, int>(StringComparer.Ordinal);
        var receivers = new Dictionary<string, int>(StringComparer.Ordinal);
        int files = 0, typed = 0, noOps = 0, edits = 0, parameters = 0, roundTrips = 0;
        foreach (var (name, source) in LuaSyntaxChecks.InstalledScripts(catalog.ScriptsRoot))
        {
            string folder = name.Split('/')[0];
            var script = LuaScript.Parse(source);
            Check(script.Reconstruct() == source, name + ": the source does not round-trip.");
            var context = new MissionApiContext(schema, references.GetValueOrDefault(folder) ?? []);
            var analysis = MissionApiAnalysis.Analyze(script, context);
            files++;
            for (int index = 0; index < analysis.Calls.Count; index++)
            {
                var call = analysis.Calls[index];
                string key = call.Outcome + (call.Outcome == MissionApiOutcome.Typed ? "" : ": " + Generalize(call.Reason));
                outcomes[key] = outcomes.GetValueOrDefault(key) + 1;
                string where = $"{name}:{Line(source, call.Call.Span.Start)}";
                if (call.Outcome == MissionApiOutcome.UnknownMember) gaps.Add($"{where} {call.Call.Callee.Read(source)}: {call.Reason}");
                if (call.Outcome is MissionApiOutcome.UnknownReceiver or MissionApiOutcome.Dynamic)
                    unknown.Add($"{call.Outcome} {where} {Compact(call.Call.Callee.Read(source))}: {call.Reason}");
                if (call.Outcome != MissionApiOutcome.Typed) continue;
                typed++;
                var node = MissionApiNode.Build(analysis, call);
                foreach (var finding in node.Findings)
                    findings.Add($"{where} {node.Member.Name} {finding.Path}: {finding.Message}");
                string gate = node.Member.Gate is null ? "none" : node.Receiver.SlotType is null ? "unchecked" : "checked";
                receivers[gate] = receivers.GetValueOrDefault(gate) + 1;
                foreach (var parameter in Flatten(node.Parameters).Where(value => value.Present))
                {
                    parameters++;
                    string widget = parameter.Widget + (parameter.Literal ? "" : "/lua");
                    widgets[widget] = widgets.GetValueOrDefault(widget) + 1;
                    Check(MissionApiWriter.SetValue(node, parameter.Path, parameter.Text) == source, $"{where}: a no-op edit of {parameter.Path} changed the source.");
                    noOps++;
                    if (!parameter.Literal || Mutate(node, parameter) is not { } text || text == parameter.Text) continue;
                    string edited = MissionApiWriter.SetValue(node, parameter.Path, text);
                    var span = parameter.Value!.Span;
                    Check(edited == source[..span.Start] + text + source[span.End..], $"{where}: the edit of {parameter.Path} touched other source.");
                    var reread = Same(MissionApiAnalysis.Analyze(LuaScript.Parse(edited), context), call)?.Find(parameter.Path);
                    Check(reread is { Literal: true } && reread.Text == text && reread.Widget == parameter.Widget,
                        $"{where}: {parameter.Path} did not reread as {text}.");
                    edits++;
                }
                // Adding an absent optional argument and removing it again restores the source exactly.
                foreach (var parameter in Flatten(node.Parameters).Where(value => !value.Present && value.Spec.Optional && !node.Computed))
                {
                    string added;
                    try { added = MissionApiWriter.Add(node, parameter.Path); }
                    catch (NotSupportedException) { continue; }
                    var grown = Same(MissionApiAnalysis.Analyze(LuaScript.Parse(added), context), call);
                    Check(grown?.Find(parameter.Path) is { Present: true }, $"{where}: adding {parameter.Path} did not read back.");
                    Check(MissionApiWriter.Remove(grown!, parameter.Path) == source, $"{where}: adding and removing {parameter.Path} did not restore the source.");
                    roundTrips++;
                }
            }
        }
        Check(files > 0 && typed > 0, "No installed call was typed.");
        foreach (var (outcome, count) in outcomes.OrderBy(pair => pair.Key, StringComparer.Ordinal)) Console.WriteLine($"mission_api_outcome {count} {outcome}");
        Console.WriteLine("mission_api_parameters " + string.Join(" ", widgets.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value)));
        Console.WriteLine("mission_api_slot_gates " + string.Join(" ", receivers.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value)));
        foreach (string gap in gaps) Console.WriteLine("mission_api_gap " + gap);
        foreach (string call in unknown) Console.WriteLine("mission_api_untyped " + call);
        foreach (string finding in findings) Console.WriteLine("mission_api_finding " + finding);
        Console.WriteLine($"mission_api_installed files={files} calls={outcomes.Values.Sum()} typed={typed} parameters={parameters} "
            + $"noop_edits={noOps} literal_edits={edits} add_remove={roundTrips} findings={findings.Count} gaps={gaps.Count} round_trip=exact");
    }

    /**
     * The lib.flow and lib.campaign views still write API calls from templates. Every call they write must type
     * against the schema with no finding, and their slot pickers must use the schema's slot types.
     */
    public static void Templates(string game)
    {
        var schema = MissionApiSchema.Load(Fixture);
        var catalog = new MissionScriptCatalog(game);
        var activity = catalog.Activities.Single(value => value.Key == "MISSION_LIGHTS");
        var refs = catalog.ReadReferences(catalog.EntryForActivity(activity), schema.MissionTables);
        MissionSdkReference First(MissionReferenceKind kind, Func<MissionSdkReference, bool>? filter = null) =>
            refs.First(value => MissionVisualAuthoring.Accepts(value, kind) && (filter?.Invoke(value) ?? true));
        var gates = new Dictionary<MissionReferenceKind, int>
        {
            [MissionReferenceKind.DirectiveSensor] = Gate("set_directive"), [MissionReferenceKind.Navpoint] = Slot("set_directive", "navpoint"),
            [MissionReferenceKind.Volume] = Slot("set_directive", "waypoint"), [MissionReferenceKind.Trigger] = Gate("fire_trigger"),
            [MissionReferenceKind.Monitor] = Gate("set_occupancy_condition"), [MissionReferenceKind.GhostLink] = Gate("set_ghost_link"),
            [MissionReferenceKind.Object] = Gate("set_interactable_object"), [MissionReferenceKind.DialogueSensor] = Gate("play_dialogue_cue")
        };
        foreach (var (kind, slot) in gates)
            Check(refs.Where(value => MissionVisualAuthoring.Accepts(value, kind)).All(value => value.SlotType == slot)
                && refs.Any(value => MissionVisualAuthoring.Accepts(value, kind)), $"The {kind} picker does not use the schema's slot type {slot}.");

        var initial = First(MissionReferenceKind.State);
        var graph = MissionScriptFactory.CreateFlow(catalog, activity, initial, "agreement").Graph;
        var program = LuaScript.Parse(graph.Source).Chunk.Statements.OfType<LuaReturnStatement>().Single().Values.OfType<LuaTable>().Single();
        Check(program.Fields.All(field => schema.CallbackNames().Contains(field.Name) || schema.Program.Entries.Any(entry => entry.Name == field.Name)),
            "The starter script returns a key the schema does not list.");
        string steps = graph.Groups.Single(value => value.Collection == "steps").Key, facts = graph.Groups.Single(value => value.Collection == "facts").Key;
        var trigger = First(MissionReferenceKind.Trigger);
        var dialogue = First(MissionReferenceKind.DialogueSensor, sensor => refs.Any(cue => cue.Category == "dialogue" && cue.Sensor == sensor.Expression));
        var cue = refs.First(value => value.Category == "dialogue" && value.Sensor == dialogue.Expression);
        var visual = new MissionVisualAuthoring(graph, refs);
        var input = refs.FirstOrDefault(value => MissionVisualAuthoring.Accepts(value, MissionReferenceKind.SceneInput)
            && visual.AuthoredScene(value.Scene) is not null);
        var output = refs.FirstOrDefault(value => MissionVisualAuthoring.Accepts(value, MissionReferenceKind.SceneOutput) && visual.SceneSlot(value.Scene) is not null);
        MissionGraphDocument Create(string group, string id, MissionNodeTemplate template, MissionSdkReference? target = null,
            MissionSdkReference? sensor = null, MissionSdkReference? navpoint = null, MissionSdkReference? scene = null,
            IReadOnlyList<MissionSdkReference>? inputs = null) =>
            graph = new MissionVisualAuthoring(graph, refs).CreateNode(group, id, template, target, null, sensor, navpoint, scene, inputs);
        Create(facts, "trigger", MissionNodeTemplate.TriggerFact, trigger);
        if (output is not null) Create(facts, "output", MissionNodeTemplate.SceneOutputFact, output);
        Create(steps, "objective", MissionNodeTemplate.FlowObjective, First(MissionReferenceKind.Directive),
            First(MissionReferenceKind.DirectiveSensor), First(MissionReferenceKind.Navpoint));
        Create(steps, "dialogue", MissionNodeTemplate.FlowDialogue, cue, dialogue);
        Create(steps, "state", MissionNodeTemplate.FlowState, initial);
        Create(steps, "arm", MissionNodeTemplate.FlowArmTrigger, trigger);
        Create(steps, "complete", MissionNodeTemplate.FlowCompleteMission);
        if (input is not null)
        {
            var scene = visual.AuthoredScene(input.Scene)!;
            Create(steps, "scene", MissionNodeTemplate.FlowStartScene, scene: scene, inputs: [input]);
            Create(steps, "input", MissionNodeTemplate.FlowSceneInput, input, scene: scene);
        }
        var analysis = MissionApiAnalysis.Analyze(LuaScript.Parse(graph.Source), new(schema, refs));
        int typed = 0;
        foreach (var call in analysis.Calls.Where(value => value.Outcome != MissionApiOutcome.NotApi))
        {
            string text = call.Call.Span.Read(graph.Source);
            Check(call.Outcome == MissionApiOutcome.Typed, $"A template writes {text}, which the schema does not type: {call.Reason}");
            var node = MissionApiNode.Build(analysis, call);
            Check(node.Findings.Count == 0, $"A template writes {text}: {string.Join("; ", node.Findings.Select(value => value.Message))}");
            typed++;
        }
        Check(typed >= 10, "The templates wrote too few API calls to check.");
        Console.WriteLine($"mission_api_templates slot_pickers={gates.Count} calls={typed} scene_templates={(input is null ? "skipped" : "checked")} findings=0");

        int Gate(string member) => schema.Members.Single(value => value.Name == member).Gate!.SlotType;
        int Slot(string member, string argument) =>
            schema.Members.Single(value => value.Name == member).Arguments.Single(value => value.Name == argument).SlotGate!.SlotType;
    }

    /** Every call the member palette offers in an event callback is valid as written. */
    public static void Palette(string game)
    {
        var schema = MissionApiSchema.Load(Fixture);
        var catalog = new MissionScriptCatalog(game);
        var activity = catalog.Activities.Single(value => value.Key == "MISSION_LIGHTS");
        var refs = catalog.ReadReferences(catalog.EntryForActivity(activity), schema.MissionTables);
        const string shell = "local missions = require(\"missions\")\nlocal mission = require(missions.MISSION_LIGHTS)\nreturn {\n"
            + "    on_event_squad_state = function(context, state, event)\n    end,\n}\n";
        var shellScript = LuaScript.Parse(shell);
        var empty = MissionApiAnalysis.Analyze(shellScript, new(schema, refs));
        var names = empty.ScopeAt(shellScript.Descendants().OfType<LuaFunction>().Single().Body);
        var offered = new List<(MissionApiMember Member, string Code)>();
        foreach (var member in schema.Members.Where(value => value.Callable))
            if (MissionApiWriter.ReceiverText(empty, member, names) is { } receiver)
                offered.Add((member, MissionApiWriter.CallText(empty, member, receiver.Text, names, receiver.Type)));
        foreach (var member in schema.EventForCallback("on_event_squad_state")!.Fields.Where(value => value.Callable))
            offered.Add((member, MissionApiWriter.CallText(empty, member, "event", names)));
        string palette = shell.Replace("event)\n", "event)\n" + string.Concat(offered.Select(item => "        local _ = " + item.Code + "\n")), StringComparison.Ordinal);
        var written = MissionApiAnalysis.Analyze(LuaScript.Parse(palette), new(schema, refs));
        var outer = written.Calls.Where(call => written.ConsumerOf(call.Call) is null && call.Outcome != MissionApiOutcome.NotApi).ToArray();
        var problems = outer.Where(call => call.Outcome != MissionApiOutcome.Typed || MissionApiNode.Build(written, call).Findings.Count > 0)
            .Select(call => call.Call.Span.Read(palette) + ": " + (call.Outcome == MissionApiOutcome.Typed
                ? string.Join("; ", MissionApiNode.Build(written, call).Findings.Select(value => value.Message)) : call.Reason)).ToArray();
        Check(problems.Length == 0 && offered.Count > 40, "The palette offers calls that are not valid as written:\n" + string.Join("\n", problems));
        Console.WriteLine("mission_api_palette_not_offered " + string.Join(" ", schema.Members.Where(value => value.Callable && offered.All(item => item.Member != value))
            .Select(value => value.Receiver + ":" + value.Name + (value.Gate is { } gate ? "/" + gate.SlotType : ""))));
        Check(schema.Events.Any(value => value.Name == "PLAYER_TRIGGER") && schema.Events.Any(value => value.Name == "SCENE_OUTPUT")
            && schema.EventForCallback("on_event_scene_output") is not null, "The fact templates name an event the schema does not list.");
        Console.WriteLine($"mission_api_palette members={schema.Members.Count(value => value.Callable)} offered={offered.Count} valid={offered.Count - problems.Length}");
    }

    /** The same call after an edit after it: it starts where it started and calls the same member. */
    private static MissionApiNode? Same(MissionApiAnalysis analysis, MissionApiCall call) =>
        analysis.Calls.FirstOrDefault(value => value.Outcome == MissionApiOutcome.Typed && value.Call.Span.Start == call.Call.Span.Start
            && value.Member == call.Member) is { } found ? MissionApiNode.Build(analysis, found) : null;

    /** Every parameter, and the children of those present; the fields of an absent table cannot be added before it. */
    private static IEnumerable<MissionApiParameter> Flatten(IEnumerable<MissionApiParameter> parameters) =>
        parameters.SelectMany(parameter => (parameter.Present ? Flatten(parameter.Children) : []).Prepend(parameter));

    /** Another literal of the same kind, or null when the parameter has no other value to try. */
    private static string? Mutate(MissionApiNode node, MissionApiParameter parameter)
    {
        switch (parameter.Widget)
        {
            case MissionApiWidget.Boolean: return parameter.Text == "true" ? "false" : "true";
            case MissionApiWidget.Integer or MissionApiWidget.Number when MissionApiNode.Number(parameter.Value) is double number:
                double next = number + 1 > parameter.Spec.Maximum ? number - 1 : number + 1;
                return MissionApiSchema.NumberText(next);
            case MissionApiWidget.Text: return LuaScript.Quote(((LuaLiteral)parameter.Value!).StringValue + "x");
            case MissionApiWidget.Reference when parameter.Reference is { } current:
                return parameter.Choices.FirstOrDefault(value => value != current) is { } other ? MissionApiWriter.ReferenceText(node, other) : null;
            case MissionApiWidget.SlotHandle when parameter.Reference is { } current:
                return parameter.Choices.FirstOrDefault(value => value != current) is { } slot ? MissionApiWriter.HandleText(node, parameter, slot) : null;
            case MissionApiWidget.Choice:
                return parameter.Entries.FirstOrDefault(value => value != parameter.Choice) is { } entry ? MissionApiWriter.ChoiceText(node, parameter, entry) : null;
            default: return null;
        }
    }

    /** A reason with its names removed, so like reasons count together. */
    private static string Generalize(string reason) => System.Text.RegularExpressions.Regex.Replace(reason, "'[^']*'", "'_'");

    /** The length of one top-level array in the fixture JSON. */
    private static int FixtureCount(string json, string name)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(name).GetArrayLength();
    }

    private static int Line(string source, int offset) => source.AsSpan(0, offset).Count('\n') + 1;

    private static string Compact(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

    private static MissionApiSchema? schema;

    /** The fixture schema with the synthetic SDK entries. */
    public static MissionApiContext Context() => new(schema ??= MissionApiSchema.Load(Fixture), References);

    private static MissionApiAnalysis Analyze(string source) => MissionApiAnalysis.Analyze(LuaScript.Parse(source), Context());

    private static MissionApiNode Node(MissionApiAnalysis analysis, string callee) => MissionApiNode.Build(analysis,
        analysis.Calls.First(call => call.Outcome == MissionApiOutcome.Typed && call.Call.Callee.Read(analysis.Source).EndsWith(callee, StringComparison.Ordinal)));

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or NotSupportedException)
        {
            if (!error.Message.Contains(message, StringComparison.Ordinal)) throw new InvalidDataException($"Refused with \"{error.Message}\", not \"{message}\".");
            return;
        }
        throw new InvalidDataException("Accepted what should be refused: " + message);
    }
}
