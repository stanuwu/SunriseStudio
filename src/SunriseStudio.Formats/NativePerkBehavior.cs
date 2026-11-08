using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

/** Native operations stay fixed; authored graphs select their inputs and phase membership. */
public static partial class NativePerkBehavior
{
    public const uint Class = SSandboxPerkProgram.Class;
    private const int MaximumNodes = 128;
    private const uint ConditionRowClass = C808040ba.Class;
    private const uint EffectRowClass = C808040ac.Class;
    // A status row in the root feedback array puts a buff in the HUD tray.
    private const uint FeedbackRowClass = C80804083.Class, StatusClass = SPerkStatusFeedback.Class;
    // Status mask bit 0x02 shows the entry while the perk is active.
    private const byte StatusWhileActive = 0x02;

    /** Status labels already present in both the HUD icon library and the label strings. */
    public static IReadOnlyList<PerkBehaviorChoice> HudLabels { get; } =
        [new(0xF2ED5B38, "Ammo Finder")];

    public static IReadOnlyList<PerkBehaviorPrimitive> Primitives { get; } =
    [
        new("always", "When activated", PerkBehaviorNodeKind.Condition, AlwaysClass,
            "Passes when the perk enters this phase. Probability can limit activation.", true, 0),
        new("timer", "After a delay", PerkBehaviorNodeKind.Condition, TimerClass,
            "Passes after this phase's timer expires.", true, 1),
        new("kill", "On kill", PerkBehaviorNodeKind.Condition, KillClass,
            "Passes when the owner kills a target.", true, 2),
        new("damage", "On damage dealt", PerkBehaviorNodeKind.Condition, DamageClass,
            "Passes when the owner damages a target.", true, 4),
        new("unequip", "On item stowed", PerkBehaviorNodeKind.Condition, UnequipClass,
            "Passes when an item leaves its active or equipped state, for example when the weapon is stowed.", true, 17),
        new("while-state", "While a state holds", PerkBehaviorNodeKind.Condition, StateClass,
            "Tested on every update. Passes while an object property is in range.", true, 20),
        new("counter", "Counter reaches a threshold", PerkBehaviorNodeKind.Condition, CounterClass,
            "Each row changes the perk counter when its condition passes. Passes when the counter reaches the threshold.", true, 26),
        new("event", "On gameplay event", PerkBehaviorNodeKind.Condition, EventClass,
            "Passes when the perk receives the chosen event hash. Which game event each hash is, is not known.", true, 30),
        new("state-kill", "On kill while a state holds", PerkBehaviorNodeKind.Condition, GatedClass,
            "Passes on a kill while an object property is in range. Both probabilities apply.", true, 35),
        new("state-damage", "On damage while a state holds", PerkBehaviorNodeKind.Condition, GatedClass,
            "Passes on damage dealt while an object property is in range. Both probabilities apply.", true, 35),
        new("state-unequip", "On item stowed while a state holds", PerkBehaviorNodeKind.Condition, GatedClass,
            "Passes when an item is stowed while an object property is in range. Both probabilities apply.", true, 35),
        new("state-event", "On gameplay event while a state holds", PerkBehaviorNodeKind.Condition, GatedClass,
            "Passes on the chosen event hash while an object property is in range. Both probabilities apply.", true, 35),
        new("spawn", "Spawn an object", PerkBehaviorNodeKind.Effect, SpawnClass,
            "Creates an object from an existing object definition while the perk is active.", true, 1),
        new("driven-spawn", "Spawn an object driven by a value", PerkBehaviorNodeKind.Effect, DrivenSpawnClass,
            "Creates an object and pushes a value into it whenever the perk counter or a condition changes. "
            + "What the object does with the value is not known.", true, 2),
        new("ability-energy", "Grant ability energy", PerkBehaviorNodeKind.Effect, EnergyClass,
            "Adds a fraction of a full charge to one ability once, when the perk activates.", true, 8),
        new("property", "Change object property", PerkBehaviorNodeKind.Effect, PropertyClass,
            "Sets, adds, subtracts or multiplies a named object property. Native code performs the write.", true, 10),
        new("ammo-drop", "Adjust ammo drops", PerkBehaviorNodeKind.Effect, AmmoDropClass,
            "Changes native primary, special and heavy ammo drop chances while the perk is active.", true, 11),
        new("damage-multiplier", "Damage multiplier", PerkBehaviorNodeKind.Effect, DamageScaleClass,
            "Multiplies damage by one plus the bonus while the perk is active. Assumed: who the damage belongs to "
            + "and the element meanings.", true, 40),
        new("perk-counter", "Set perk counter", PerkBehaviorNodeKind.Effect, CounterEffectClass,
            "Sets or adds to the perk counter when the perk activates.", true, 42),
        new("transmat", "Transmat or Ghost effect", PerkBehaviorNodeKind.Effect, TransmatClass,
            "Selects the player's transmat or Ghost effect while this perk is present.", true, 47),
        .. SpecTable.All.Select(spec => new PerkBehaviorPrimitive(spec.Key, spec.Name, spec.Kind, spec.Class,
            spec.Description, spec.CanCreate, spec.Opcode))
    ];

    /** Condition kinds a new counter row can hold. */
    public static IReadOnlyList<string> CounterRowPrimitives =>
        [.. StateGatedKeys, .. EventPrimitives];

    /** True for the kinds an in-game perk has used; every other kind is untested in game. */
    public static bool IsTested(string primitive) => primitive is "always" or "timer" or "ammo-drop";

    public static PerkBehaviorGraph New() => new();

    /** Defaults are authored inputs; no installed perk supplies the new node's bytes. */
    public static PerkBehaviorNode CreateNode(string primitive)
    {
        if (primitive == GroupKey) return CreateGroup();
        var spec = PrimitiveOf(primitive) ?? throw new InvalidDataException("Choose a supported native operation.");
        var node = Blank(primitive, spec);
        if (primitive.StartsWith(StatePrefix, StringComparison.Ordinal))
            node.Children.Add(CreateNode(primitive[StatePrefix.Length..]));
        return node;
    }

    /** A counter row is its condition plus the row's own inputs. */
    public static PerkBehaviorNode CreateRow(string primitive)
    {
        if (!CounterRowPrimitives.Contains(primitive))
            throw new InvalidDataException("A counter row needs a decoded condition.");
        var node = CreateNode(primitive);
        node.Properties.AddRange(RowInputs());
        return node;
    }

    /** Undecoded nodes keep their source locations so unrelated edits preserve every byte. */
    public static PerkBehaviorGraph Decompile(ReadOnlyMemory<byte> bytes)
    {
        RequireRoot(bytes);
        var graph = new PerkBehaviorGraph { OriginalBytes = bytes.ToArray(), ProgramTag = FindProgramTag(bytes) };
        ReadPhase(PayloadFields.Activation, graph.Activation, PerkBehaviorNodeKind.Condition);
        ReadPhase(PayloadFields.Effects, graph.Effects, PerkBehaviorNodeKind.Effect);
        ReadPhase(PayloadFields.Deactivation, graph.Deactivation, PerkBehaviorNodeKind.Condition);
        graph.Hud = FindHud(bytes)?.Hud;
        ref readonly var root = ref TagView.At<SSandboxPerkProgram>(bytes.Span);
        if (root.Feedback.Count != 0 && graph.Hud is null || root.HB38b5954.CooldownConditions.Count != 0
            || root.StackBranches.Count != 0)
            graph.Diagnostics.Add("HUD, cooldown and stack branches kept; not shown.");
        return graph;

        void ReadPhase(int field, List<PerkBehaviorNode> nodes, PerkBehaviorNodeKind kind)
        {
            bool condition = kind == PerkBehaviorNodeKind.Condition;
            int stride = condition ? PayloadFields.ConditionRowSize : PayloadFields.EffectRowSize;
            var array = TagArray.Checked(bytes.Span, field, condition ? ConditionRowClass : EffectRowClass, stride);
            if (array.Count > MaximumNodes) throw new InvalidDataException("The perk phase exceeds the node limit.");
            for (int i = 0; i < array.Count; i++)
            {
                int target = TagPointer.Checked(bytes.Span, array.Data + i * stride);
                nodes.Add(ReadNode(bytes, target, kind, Role.Top, $"{field}:{i}:{target}", graph.Diagnostics));
            }
        }
    }

    /**
     * Validation checks native phase types, input ranges and the provenance of preserved nodes.
     * @param source Decompile of graph.OriginalBytes, when the caller already has it; null decompiles here.
     */
    public static IReadOnlyList<string> Validate(PerkBehaviorGraph graph, PerkBehaviorGraph? source = null)
    {
        var problems = new List<string>();
        if (graph.Version != 1) problems.Add("This perk graph version is unsupported.");
        if (graph.OriginalBytes.Length == 0 && graph.Activation.Count == 0)
            problems.Add("Add a condition that activates the perk.");
        if (graph.OriginalBytes.Length == 0 && graph.Effects.Count == 0)
            problems.Add("Add an effect for the perk to perform.");
        var top = graph.Activation.Concat(graph.Effects).Concat(graph.Deactivation).ToArray();
        var nodes = top.SelectMany(Flatten).ToArray();
        if (top.Length > MaximumNodes) problems.Add($"A perk graph supports up to {MaximumNodes} nodes.");
        if (nodes.Select(node => node.Id).Distinct(StringComparer.Ordinal).Count() != nodes.Length)
            problems.Add("Each perk node needs a unique identity.");
        if (graph.OriginalBytes.Length == 0) source = null;
        else source ??= Decompile(graph.OriginalBytes);
        var originals = source?.Activation.Concat(source.Effects).Concat(source.Deactivation).SelectMany(Flatten)
            .ToDictionary(node => node.Id, StringComparer.Ordinal);
        // Extend-timers conditions under effects take no index, so only phase conditions count here.
        if (source is not null && graph.Activation.Concat(graph.Deactivation).SelectMany(Flatten)
                .Any(node => node.OriginalOffset < 0 && node.Primitive != GroupKey)
            && (TagRead.U64(graph.OriginalBytes, PayloadFields.Stack) != 0 || ConditionIndices(graph.OriginalBytes) is null))
            problems.Add("Adding conditions needs a perk without stack branches.");
        if (graph.Effects.SelectMany(Flatten).Any(node => node.NativeClass == ResistClass && node.OriginalOffset < 0)
            && graph.ProgramTag == 0)
            problems.Add("A new owner damage multiplier needs the program's tag.");
        if (graph.Effects.Skip(64).Any(node => node.NativeClass == ExtendTimersClass))
            problems.Add("Extend-timers effects must be among the first 64 effects.");
        if (source is not null && source.Activation.Count > 0 && graph.Activation.Count == 0)
            problems.Add("Keep an explicit activation condition when editing this perk.");
        if (graph.Hud is { } hud)
        {
            if (!HudLabels.Any(label => label.Value == hud.Label))
                problems.Add("Choose a HUD label from the installed status labels.");
            if (source is { Hud: null } && TagRead.U64(graph.OriginalBytes, PayloadFields.Feedback) != 0)
                problems.Add("This perk already has native HUD feedback, which is preserved.");
            if (hud.ShowTimer && !graph.Deactivation.Any(node => node.Primitive == "timer"))
                problems.Add("The HUD timer needs a deactivation timer.");
        }
        foreach (var shared in nodes.Where(node => node.OriginalOffset >= 0).GroupBy(node => node.OriginalOffset))
        {
            var first = shared.First().Properties.Select(property => (property.Key, property.Value));
            if (shared.Skip(1).Any(node => !node.Properties.Select(property => (property.Key, property.Value)).SequenceEqual(first)))
                problems.Add("Shared native nodes must keep the same input values.");
        }
        foreach (var node in top)
        {
            bool effect = graph.Effects.Contains(node);
            if (node.Kind != (effect ? PerkBehaviorNodeKind.Effect : PerkBehaviorNodeKind.Condition))
                problems.Add($"{node.Title} is in the wrong phase.");
            ValidateNode(node, Role.Top, null);
        }
        return problems;

        void ValidateNode(PerkBehaviorNode node, Role role, PerkBehaviorNode? parent)
        {
            List<PerkBehaviorProperty> expected;
            PerkBehaviorNode? original = null;
            if (node.OriginalOffset >= 0)
            {
                if (originals is null || !originals.TryGetValue(node.Id, out original)
                    || original.OriginalOffset != node.OriginalOffset
                    || original.NativeClass != node.NativeClass || original.Primitive != node.Primitive
                    || original.Kind != node.Kind || !original.NativeBytes.SequenceEqual(node.NativeBytes)
                    || !original.Children.Select(child => child.Id).SequenceEqual(node.Children.Select(child => child.Id)))
                { problems.Add($"{node.Title} no longer matches its source bytes."); return; }
                expected = original.Properties;
                if (!original.Editable)
                    foreach (var spec in expected) spec.Editable = false;
            }
            else
            {
                if (parent is { OriginalOffset: >= 0 })
                { problems.Add($"{parent.Title} keeps its installed rows; it cannot take new ones."); return; }
                bool allowed = role switch
                {
                    Role.Inner => parent is not null && parent.Primitive == StatePrefix + node.Primitive,
                    Role.Top => node.Primitive != GroupKey,
                    _ => parent is not null && ChildPrimitives(parent).Contains(node.Primitive)
                };
                bool known = node.Primitive == GroupKey ? node.NativeClass == GroupClass && role == Role.Groups
                    : PrimitiveOf(node.Primitive) is { CanCreate: true } kind && kind.NativeClass == node.NativeClass
                        && kind.Kind == node.Kind;
                if (!allowed || !known)
                { problems.Add($"{node.Title} cannot be created from scratch here."); return; }
                expected = role == Role.Row ? CreateRow(node.Primitive).Properties : CreateNode(node.Primitive).Properties;
                problems.AddRange(Semantics(node));
            }
            if (expected.Count != node.Properties.Count
                || node.Properties.Select(property => property.Key).Distinct().Count() != node.Properties.Count)
                problems.Add($"{node.Title} has missing or duplicate inputs.");
            foreach (var spec in expected)
            {
                var value = node.Properties.FirstOrDefault(property => property.Key == spec.Key);
                if (value is null) continue;
                if (!double.IsFinite(value.Value) || value.Value < spec.Minimum || value.Value > spec.Maximum
                    || spec.Kind != PerkBehaviorValueKind.Float && value.Value != Math.Truncate(value.Value)
                    || spec.Kind == PerkBehaviorValueKind.Boolean && value.Value is not (0 or 1)
                    || node.OriginalOffset < 0 && spec.Picker.Length == 0 && spec.Choices.Count != 0
                        && spec.Choices.All(choice => choice.Value != value.Value))
                    problems.Add($"{node.Title}: {spec.Label} is outside its supported range.");
                else if (!spec.Editable && BitConverter.DoubleToInt64Bits(value.Value) != BitConverter.DoubleToInt64Bits(spec.Value))
                    problems.Add($"{node.Title}: {spec.Label} is read-only.");
            }
            problems.AddRange(FilterProblems(node, original));
            foreach (var child in node.Children)
            {
                if (child.Kind != PerkBehaviorNodeKind.Condition) problems.Add($"{child.Title} is not a condition.");
                ValidateNode(child, ChildRole(node), node);
            }
        }
    }

    /** Existing payloads stay in place; structural edits append arrays and fresh native node records. */
    public static byte[] Compile(PerkBehaviorGraph graph)
    {
        bool fresh = graph.OriginalBytes.Length == 0;
        var source = fresh ? null : Decompile(graph.OriginalBytes);
        var problems = Validate(graph, source);
        if (problems.Count != 0) throw new InvalidDataException(string.Join("\n", problems));
        var writer = new PerkWriter(fresh ? NewRoot() : graph.OriginalBytes) { ProgramTag = graph.ProgramTag };
        List<int> indices = fresh ? [] : ConditionIndices(graph.OriginalBytes) ?? [];
        // ConditionIndex is unique within the program; new conditions continue after the highest.
        int nextIndex = indices.Count == 0 ? 0 : indices.Max() + 1;
        WritePhase(PayloadFields.Activation, graph.Activation, source?.Activation, ConditionRowClass);
        WritePhase(PayloadFields.Effects, graph.Effects, source?.Effects, EffectRowClass);
        WritePhase(PayloadFields.Deactivation, graph.Deactivation, source?.Deactivation, ConditionRowClass);
        WriteRootMasks(writer, graph, fresh);
        // A HUD row Studio wrote is rewritten in place; removing it empties the feedback array.
        var installedHud = fresh ? null : FindHud(graph.OriginalBytes);
        if (graph.Hud is { } hud)
        {
            int status = installedHud?.At ?? writer.Node(StatusClass, PayloadFields.StatusSize);
            writer.Copy(status, StatusBytes(hud));
            if (installedHud is null)
            {
                int rows = writer.Array(PayloadFields.Feedback, FeedbackRowClass, 1, PayloadFields.FeedbackRowSize);
                writer.I64(rows, status - rows);
            }
        }
        else if (installedHud is not null) writer.Array(PayloadFields.Feedback, FeedbackRowClass, 0, PayloadFields.FeedbackRowSize);
        return writer.Finish();

        void WritePhase(int field, List<PerkBehaviorNode> nodes, List<PerkBehaviorNode>? original, uint rowClass)
        {
            int stride = rowClass == ConditionRowClass ? PayloadFields.ConditionRowSize : PayloadFields.EffectRowSize;
            var offsets = new List<int>();
            foreach (var node in nodes)
            {
                int target;
                if (node.OriginalOffset >= 0)
                {
                    target = node.OriginalOffset;
                    PatchNode(writer, graph.OriginalBytes, node, target, Role.Top);
                }
                else target = WriteNode(writer, node, Role.Top, ref nextIndex);
                offsets.Add(target);
            }
            if (original is not null && offsets.SequenceEqual(original.Select(node => node.OriginalOffset))) return;
            int rows = writer.Array(field, rowClass, nodes.Count, stride);
            for (int i = 0; i < offsets.Count; i++) writer.I64(rows + i * stride, offsets[i] - rows - i * stride);
        }
    }

    /** Phase event masks, the extend-timers mask and, on a new root, the stock trailer bytes. */
    private static void WriteRootMasks(PerkWriter writer, PerkBehaviorGraph graph, bool fresh)
    {
        ref var root = ref writer.Edit<SSandboxPerkProgram>(0);
        root.ActivationEventMask |= Mask(graph.Activation);
        root.DeactivationEventMask |= Mask(graph.Deactivation);
        // Bit i is set when effect i extends timers; the event walker visits only those.
        root.ExtendTimerEffectMask = Enumerable.Range(0, Math.Min(64, graph.Effects.Count))
            .Where(i => graph.Effects[i].NativeClass == ExtendTimersClass).Aggregate(0UL, (mask, i) => mask | 1UL << i);
        if (!fresh) return;
        // Every stock root sets these; their reader is unknown. Values follow the stock pattern.
        root.Unknown204 = 1;
        root.TimerCount = checked((byte)graph.Activation.Concat(graph.Deactivation).Count(node => node.Primitive == "timer"));
    }

    /** The status row Compile writes for a HUD entry. */
    private static byte[] StatusBytes(PerkHudStatus hud)
    {
        byte[] status = new byte[PayloadFields.StatusSize];
        ref var row = ref TagView.Edit<SPerkStatusFeedback>(status);
        row.StatusMask = StatusWhileActive;
        row.Unknown1 = byte.MaxValue;
        row.HudLabel = hud.Label;
        row.Unknown20 = byte.MaxValue;
        row.Unknown21 = 1;
        row.ShowTimer = hud.ShowTimer ? (byte)1 : (byte)0;
        return status;
    }

    /** The HUD entry and its row offset when the feedback array holds one row in Compile's form. */
    private static (PerkHudStatus Hud, int At)? FindHud(ReadOnlyMemory<byte> bytes)
    {
        var span = bytes.Span;
        if (!TagArray.TryChecked(span, PayloadFields.Feedback, FeedbackRowClass, PayloadFields.FeedbackRowSize, out var rows)
            || rows.Count != 1) return null;
        int at = TagPointer.TryChecked(span, rows.Data);
        if (at < TagView.ClassWordSize || !TagView.Fits<SPerkStatusFeedback>(span, at) || TagView.ClassAt(span, at) != StatusClass)
            return null;
        ref readonly var status = ref TagView.At<SPerkStatusFeedback>(span, at);
        var hud = new PerkHudStatus { Label = status.HudLabel, ShowTimer = status.ShowTimer == 1 };
        if (!HudLabels.Any(label => label.Value == hud.Label)
            || !span.Slice(at, PayloadFields.StatusSize).SequenceEqual(StatusBytes(hud))) return null;
        return (hud, at);
    }

    /** A phase mask needs every event its conditions match, nested rows and inner conditions included. */
    private static ulong Mask(IEnumerable<PerkBehaviorNode> nodes) =>
        nodes.Aggregate(0UL, (mask, node) => mask | Bits(node));

    private static ulong Bits(PerkBehaviorNode node)
    {
        // Only conditions reach here, so installed bytes start with the condition header.
        int opcode = node.Primitive == GroupKey ? -1 : node.NativeBytes.Length > ConditionOpcode ? node.NativeBytes[ConditionOpcode]
            : PrimitiveOf(node.Primitive)?.Opcode ?? -1;
        ulong bits = opcode is >= 0 and < 64 ? 1UL << opcode : 0;
        return node.Children.Aggregate(bits, (mask, child) => mask | Bits(child));
    }

    /** Every condition's ConditionIndex, nested ones included; null when a payload cannot be walked. */
    private static List<int>? ConditionIndices(ReadOnlyMemory<byte> bytes)
    {
        var seen = new HashSet<int>();
        var result = new List<int>();
        try
        {
            foreach (int field in new[] { PayloadFields.Activation, PayloadFields.Deactivation, PayloadFields.Cooldown })
            {
                var array = TagArray.Checked(bytes.Span, field, ConditionRowClass, PayloadFields.ConditionRowSize);
                for (int i = 0; i < array.Count; i++) Visit(array.Data + i * PayloadFields.ConditionRowSize, 0);
            }
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException) { return null; }
        return result;

        // `pointer` is the field that names the condition.
        void Visit(int pointer, int depth)
        {
            var span = bytes.Span;
            int at = TagPointer.Checked(span, pointer);
            if (at < 4 || at > span.Length - ConditionHeaderSize || depth > 8)
                throw new InvalidDataException("The perk condition is truncated.");
            if (!seen.Add(at)) return;
            result.Add(ConditionHeader(span, at).ConditionIndex);
            uint? type = TagView.ClassAt(span, at);
            if (type == CounterClass && TagView.Fits<PerkCounter>(span, at))
            {
                var rows = TagArray.Checked(span, at + PayloadFields.CounterRows, CounterRowClass, PayloadFields.CounterRowSize);
                for (int i = 0; i < rows.Count; i++) Visit(rows.Data + i * PayloadFields.CounterRowSize, depth + 1);
            }
            if (type == GatedClass && TagView.Fits<PerkStateGated>(span, at)) Visit(at + PayloadFields.GatedInner, depth + 1);
            if (type == AllGroupsClass && TagView.Fits<PerkAllGroups>(span, at))
            {
                var groups = TagArray.Checked(span, at + PayloadFields.AllGroupsGroups, GroupClass, PayloadFields.GroupSize);
                for (int i = 0; i < groups.Count; i++)
                {
                    var members = TagArray.Checked(span, groups.Data + i * PayloadFields.GroupSize + PayloadFields.GroupMembers,
                        ConditionRowClass, PayloadFields.ConditionRowSize);
                    for (int j = 0; j < members.Count; j++) Visit(members.Data + j * PayloadFields.ConditionRowSize, depth + 1);
                }
            }
        }
    }

    private static IEnumerable<PerkBehaviorNode> Flatten(PerkBehaviorNode node) =>
        node.Children.SelectMany(Flatten).Prepend(node);

    /** The native no-stack sentinel prevents reads from absent stack branches. */
    private static byte[] NewRoot()
    {
        byte[] bytes = new byte[PayloadFields.RootSize];
        ref var root = ref TagView.Edit<SSandboxPerkProgram>(bytes);
        root.Unknown8 = StringHash.Empty;
        root.Unknown128 = StringHash.Empty;
        root.NoStackSentinel = 1;
        ((Span<byte>)root.Unknown188).Fill(byte.MaxValue);
        return bytes;
    }

    private static void RequireRoot(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length < PayloadFields.RootSize || TagView.At<SSandboxPerkProgram>(bytes.Span).TagSize != (ulong)bytes.Length)
            throw new InvalidDataException("The native perk root is truncated or has an invalid size.");
    }

    private static double Value(PerkBehaviorNode node, string key) =>
        node.Properties.Single(property => property.Key == key).Value;

    private static PerkBehaviorProperty Property(string key, string label, double value, double minimum,
        double maximum, PerkBehaviorValueKind kind = PerkBehaviorValueKind.Float, string description = "") =>
        new() { Key = key, Label = label, Value = value, Minimum = minimum, Maximum = maximum,
            Kind = kind, Description = description };

    /** Payload field offsets from the generated views; a nested class has its own static order. */
    private static class PayloadFields
    {
        public static int RootSize => Unsafe.SizeOf<SSandboxPerkProgram>();
        public static int ConditionRowSize => Unsafe.SizeOf<C808040ba>();
        public static int EffectRowSize => Unsafe.SizeOf<C808040ac>();
        public static int FeedbackRowSize => Unsafe.SizeOf<C80804083>();
        public static readonly int Feedback = Root(nameof(SSandboxPerkProgram.Feedback));
        public static readonly int Activation = Root("HB38b5954.ActivationConditions");
        public static readonly int Effects = Root("HB38b5954.Effects");
        public static readonly int Deactivation = Root("HB38b5954.DeactivationConditions");
        // Cooldown conditions and stack branches are kept but never shown as a phase.
        public static readonly int Cooldown = Root("HB38b5954.CooldownConditions");
        public static readonly int Stack = Root(nameof(SSandboxPerkProgram.StackBranches));

        public static int BranchSize => Unsafe.SizeOf<C8080407d>();
        public static readonly int BranchEffects = TagView.FieldOffset<C8080407d>(nameof(C8080407d.Effects));

        public static int StatusSize => Unsafe.SizeOf<SPerkStatusFeedback>();

        public static int GroupSize => Unsafe.SizeOf<PerkConditionGroup>();
        public static readonly int GroupMembers = TagView.FieldOffset<PerkConditionGroup>(nameof(PerkConditionGroup.Members));
        public static readonly int GroupMask = TagView.FieldOffset<PerkConditionGroup>(nameof(PerkConditionGroup.MemberEventMask));
        public static readonly int AllGroupsGroups = TagView.FieldOffset<PerkAllGroups>(nameof(PerkAllGroups.Groups));

        public static readonly int CounterRows = TagView.FieldOffset<PerkCounter>(nameof(PerkCounter.Rows));
        public static int CounterRowSize => Unsafe.SizeOf<PerkCounterRow>();
        public static readonly int GatedInner = TagView.FieldOffset<PerkStateGated>(nameof(PerkStateGated.InnerCondition));
        public static readonly int TimerConditions = TagView.FieldOffset<PerkExtendTimers>(nameof(PerkExtendTimers.Conditions));
        public static readonly int TimerMask = TagView.FieldOffset<PerkExtendTimers>(nameof(PerkExtendTimers.ConditionEventMask));

        public static readonly int MovementBlock = TagView.FieldOffset<PerkMovement>(nameof(PerkMovement.MovementBlock));
        public static int MovementBlockSize => Unsafe.SizeOf<PerkMovementBlock>();
        public static readonly int MovementName = Block(nameof(PerkMovementBlock.BlockName));
        public static readonly int MovementAllowFirst = Block(nameof(PerkMovementBlock.AllowFirst));
        public static readonly int MovementAllowSecond = Block(nameof(PerkMovementBlock.AllowSecond));
        // Per set: value 0, entry, value 8, horizontal min, max, value 20, vertical min, max.
        public static readonly int[] MovementFirst =
        [
            Block(nameof(PerkMovementBlock.FirstValue0)), Block(nameof(PerkMovementBlock.FirstEntry)),
            Block(nameof(PerkMovementBlock.FirstValue8)), Block(nameof(PerkMovementBlock.FirstHmin)),
            Block(nameof(PerkMovementBlock.FirstHmax)), Block(nameof(PerkMovementBlock.FirstValue20)),
            Block(nameof(PerkMovementBlock.FirstVmin)), Block(nameof(PerkMovementBlock.FirstVmax))
        ];
        public static readonly int[] MovementSecond =
        [
            Block(nameof(PerkMovementBlock.SecondValue0)), Block(nameof(PerkMovementBlock.SecondEntry)),
            Block(nameof(PerkMovementBlock.SecondValue8)), Block(nameof(PerkMovementBlock.SecondHmin)),
            Block(nameof(PerkMovementBlock.SecondHmax)), Block(nameof(PerkMovementBlock.SecondValue20)),
            Block(nameof(PerkMovementBlock.SecondVmin)), Block(nameof(PerkMovementBlock.SecondVmax))
        ];

        public static int LabelRowSize => Unsafe.SizeOf<PerkLabelRow>();
        public static readonly int LabelRowPath = TagView.FieldOffset<PerkLabelRow>(nameof(PerkLabelRow.LabelTablePath));
        public static readonly int LabelRowTable = TagView.FieldOffset<PerkLabelRow>(nameof(PerkLabelRow.LabelTable));
        public static readonly int LabelSetPath = TagView.FieldOffset<C808093f3>(nameof(C808093f3.ItemLabelTablePath));
        public static readonly int LabelSetTable = TagView.FieldOffset<C808093f3>(nameof(C808093f3.ItemLabelTable));

        // Refill fractions: active weapon, this weapon, slots 0 to 2.
        public static readonly int[] RefillFractions =
        [
            TagView.FieldOffset<PerkRefill>(nameof(PerkRefill.H0268fe0f)),
            TagView.FieldOffset<PerkRefill>(nameof(PerkRefill.H4ae7200a)),
            TagView.FieldOffset<PerkRefill>(nameof(PerkRefill.HFd12630f)),
            TagView.FieldOffset<PerkRefill>(nameof(PerkRefill.HD9077078)),
            TagView.FieldOffset<PerkRefill>(nameof(PerkRefill.HFb53277c))
        ];
        // A constant or pass-through program sets both of these words to 1.
        public static readonly int ExpressionFirstOne =
            TagView.FieldOffset<SCompiledExpression>(nameof(SCompiledExpression.Unknown56One));
        public static readonly int ExpressionSecondOne =
            TagView.FieldOffset<SCompiledExpression>(nameof(SCompiledExpression.Unknown64One));

        private static int Root(string member) => TagView.FieldOffset<SSandboxPerkProgram>(member);
        private static int Block(string member) => TagView.FieldOffset<PerkMovementBlock>(member);
    }
}
