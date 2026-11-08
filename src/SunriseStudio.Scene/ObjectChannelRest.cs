using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

/**
 * A channel's value in the game at rest. Unsettled names the run-time input it waits on; Value is then 1.
 * Changeable is a component config that names a start value this one reads; it may change it at run time.
 */
public readonly record struct ObjectChannelRestValue(Vector4 Value, string? Unsettled, uint Changeable = 0)
{
    public bool Settled => Unsettled is null;
    public static ObjectChannelRestValue Open(string reason) => new(new Vector4(SceneObjectChannel.ViewerDefault), reason);
}

/**
 * At-rest values of an entity's value provider, the source of its object channels.
 * Provider rows start at their stored value; expression rows rerun when an input changes
 * (0x7FF742245F90), bound-input rows copy their source, and the rest keep their value until written.
 */
internal sealed class ObjectChannelRest
{
    private const uint ProviderPrimary = UnknownValueProviderPrimary.Class, ProviderResource = SVariableStore.Class;
    private const ushort NoSlot = 0xFFFF;
    // FNV-1 of the empty name: a source in the provider's own object.
    private const uint OwnObject = StringHash.Empty;
    // FNV-1 of "time": a parameter the game rewrites each frame.
    private const uint TimeName = 0x9E3669DA;

    // Constant publisher rows (getter 0x7FF7427F1470): a name and an authored float4.
    private const uint ConstantResource = UnknownConstantPublisherResource.Class;
    // Damage value rows (getter 0x7FF742996CE0): a name, a kind and flags.
    private const uint DamageResource = SCombatantDamagePayload.Class;
    private const byte AliveKind = 3, DividedByMaximum = 1, MaximumMinusValue = 2;
    // Model components hold the channel requests; their names are readers, not writers.
    private const uint ModelResource = SModelComponent.Class, ClothModelResource = SClothModelComponent.Class,
        VariantResource = UnknownVariantResource.Class;

    private sealed record Row(uint Name, byte[] Code, Vector4[] Constants, int[] Inputs, ushort Slot);

    private readonly Row[] rows = [];
    private readonly Vector4[] stored = [];
    private readonly Dictionary<int, (uint Scope, uint Name)> sources = [];
    private readonly Dictionary<uint, int> byName = [];
    private readonly Dictionary<uint, ObjectChannelRestValue> published = [];
    private readonly Dictionary<int, ObjectChannelRestValue> memo = [];
    private readonly HashSet<int> visiting = [];
    // Aligned words of the entity's other component configs, each with the first config holding it.
    private readonly Dictionary<uint, uint> namedBy = [];
    private readonly int timeOutput = -1;

    /** Reads the provider and the value publishers among the entity's component configs; the first provider is used. */
    public ObjectChannelRest(IEnumerable<(uint Tag, byte[] Data)> configs)
    {
        foreach (var (tag, bytes) in configs)
        {
            // A null pointer has no target and no class.
            int? primaryTarget = TagView.TargetOffset(bytes, in ConfigPointer(bytes, nameof(SEntityConfig.DefaultInstance)));
            int? resourceTarget = TagView.TargetOffset(bytes, in ConfigPointer(bytes, nameof(SEntityConfig.Definition)));
            if (primaryTarget < TagView.ClassWordSize || resourceTarget < TagView.ClassWordSize
                || resourceTarget is { } end && !TagView.Fits<uint>(bytes, end)) continue;
            int primary = primaryTarget ?? 0, resource = resourceTarget ?? 0;
            uint primaryClass = primaryTarget is null ? 0 : ClassBefore(bytes, primary);
            uint resourceClass = resourceTarget is null ? 0 : ClassBefore(bytes, resource);
            if (resourceClass == ConstantResource) ReadConstants(bytes, resource);
            else if (resourceClass == DamageResource) ReadDamage(bytes, resource);
            else if (primaryClass == ProviderPrimary && resourceClass == ProviderResource && rows.Length == 0)
            {
                var providerRows = TagView.Rows(bytes, in ArrayAt<SVariableStore, UnknownValueProviderRow>(bytes, resource,
                    nameof(SVariableStore.ProviderRows)), UnknownValueProviderRow.Class);
                rows = new Row[providerRows.Length];
                for (int i = 0; i < rows.Length; i++) rows[i] = ReadRow(bytes, in providerRows[i]);
                var storedValues = TagView.Rows(bytes, in ArrayAt<UnknownValueProviderPrimary, ExpressionConstant>(bytes, primary,
                    nameof(UnknownValueProviderPrimary.StoredValues)), ExpressionConstant.Class);
                stored = new Vector4[storedValues.Length];
                for (int i = 0; i < stored.Length; i++) stored[i] = storedValues[i].Value;
                foreach (ref readonly var source in TagView.Rows(bytes, in ArrayAt<SVariableStore, UnknownValueProviderSource>(bytes,
                             resource, nameof(SVariableStore.BoundSources)), UnknownValueProviderSource.Class))
                    sources[source.RowIndex] = (source.SourceScope, source.SourceName);
                timeOutput = TagRead.I32(bytes, resource + TagView.FieldOffset<SVariableStore>(nameof(SVariableStore.TimeOutputRow)));
            }
            else if (resourceClass == SequenceResource.ResourceClass && SequenceRest.Read(tag, bytes, resource) is { } sequence)
            {
                // A sequence's input channels are readers; only its outputs and named writes can change a row.
                sequences.Add(sequence);
                foreach (uint name in sequence.Writes) namedBy.TryAdd(name, tag);
            }
            // Variant values are part filter labels; no variant method calls a provider writer.
            else if (resourceClass is not (ModelResource or ClothModelResource or VariantResource))
                for (int at = 0; at + 4 <= bytes.Length; at += 4) namedBy.TryAdd(TagRead.U32(bytes, at), tag);
        }
        for (int i = 0; i < rows.Length; i++) byName.TryAdd(rows[i].Name, i);
    }

    private readonly List<SequenceRest> sequences = [];
    private readonly HashSet<uint> sequenceVisiting = [];

    /**
     * The value sequence outputs of this name hold at rest; null when no writer of it runs at rest.
     * That an output sets the provider value of its name is assumed: the binding is not read.
     */
    private ObjectChannelRestValue? SequenceValue(uint name)
    {
        if (!sequenceVisiting.Add(name)) return ObjectChannelRestValue.Open("sequence gate reads its own output");
        try
        {
            ObjectChannelRestValue? found = null;
            foreach (var sequence in sequences)
            {
                if (sequence.Rest(name, Channel) is not { } value) continue;
                if (found is { } other && (other.Unsettled is not null || other.Value != value.Value))
                    return ObjectChannelRestValue.Open("two sequences write this value");
                found = value;
            }
            return found;
        }
        finally { sequenceVisiting.Remove(name); }
    }

    public bool HasProvider => rows.Length != 0;

    /** Channel names of a model resource in TFX channel order, one request per channel; empty when it has none. */
    public static uint[] RequestNames(byte[] bytes, int resource)
    {
        const string channels = "HCa95f655.Channels";
        if (!TagView.FitsThrough<SModelComponent>(bytes, resource, channels)) return [];
        ref readonly var requests = ref ArrayAt<SModelComponent, SModelValueBankResource>(bytes, resource, channels);
        if (requests.Count == 0) return [];
        var rows = TagView.Rows(bytes, in requests, SModelValueBankResource.Class);
        var names = new uint[rows.Length];
        for (int i = 0; i < names.Length; i++) names[i] = rows[i].ChannelHashReloading0xecde609e;
        return names;
    }

    /** Each named channel's rest value, read from the entity's component configs. */
    public static ObjectChannelRestValue[] RestValues(IEnumerable<(uint Tag, byte[] Data)> configs, uint[] names)
    {
        var provider = new ObjectChannelRest(configs);
        return provider.HasProvider ? names.Select(provider.Channel).ToArray()
            : names.Select(_ => ObjectChannelRestValue.Open("entity has no value provider")).ToArray();
    }

    /** The value a channel request of this name reads at rest. */
    public ObjectChannelRestValue Channel(uint name) =>
        byName.TryGetValue(name, out int row) ? Output(row) : ObjectChannelRestValue.Open("no provider output of this name");

    private ObjectChannelRestValue Output(int index)
    {
        if (memo.TryGetValue(index, out var known)) return known;
        // A row that reads itself settles on a fixed point the viewer does not solve.
        if (!visiting.Add(index)) return ObjectChannelRestValue.Open("provider row reads its own value");
        var row = rows[index];
        ObjectChannelRestValue value;
        if (index == timeOutput) value = ObjectChannelRestValue.Open("simulation time");
        else if (row.Code.Length != 0)
        {
            var inputs = new Vector4[row.Inputs.Length];
            value = default;
            uint changeable = 0;
            for (int i = 0; i < inputs.Length && value.Unsettled is null; i++)
            {
                var input = row.Inputs[i] >= 0 && row.Inputs[i] < rows.Length ? Output(row.Inputs[i])
                    : ObjectChannelRestValue.Open("expression input out of range");
                if (input.Unsettled is not null) value = input;
                else if (changeable == 0) changeable = input.Changeable;
                inputs[i] = input.Value;
            }
            if (value.Unsettled is null)
                value = ProviderExpression.Run(row.Code, row.Constants, inputs) is { } result ? new(result, null, changeable)
                    : ObjectChannelRestValue.Open("expression uses an operation the viewer does not run");
        }
        else if (row.Slot != NoSlot)
        {
            // A bound input mirrors its source; only a source published in this entity is known.
            value = !sources.TryGetValue(index, out var source) ? ObjectChannelRestValue.Open("bound input without a source")
                : source.Scope != OwnObject ? ObjectChannelRestValue.Open($"input from scope 0x{source.Scope:X8}")
                : published.TryGetValue(source.Name, out var publishedValue) ? publishedValue
                : SequenceValue(source.Name) ?? ObjectChannelRestValue.Open($"run-time input 0x{source.Name:X8}");
        }
        else if (published.TryGetValue(row.Name, out var publishedValue)) value = publishedValue;
        else if (row.Name == TimeName) value = ObjectChannelRestValue.Open("simulation time");
        else if (SequenceValue(row.Name) is { } sequenceValue) value = sequenceValue;
        // A row no component of the entity can write is set from outside the entity data.
        else if (!namedBy.TryGetValue(row.Name, out uint writer)) value = ObjectChannelRestValue.Open($"run-time input 0x{row.Name:X8}");
        else value = new(index < stored.Length ? stored[index] : Vector4.Zero, null, writer);
        visiting.Remove(index);
        return memo[index] = value;
    }

    // The expression arrays are read without a class check.
    private static Row ReadRow(byte[] data, ref readonly UnknownValueProviderRow row)
    {
        var code = TagView.Rows(data, in row.Expression.Bytecode);
        var constants = TagView.Rows(data, in row.Expression.ConstantBuffer);
        var inputs = TagView.Rows(data, in row.ExpressionInputs);
        var codeBytes = new byte[code.Length];
        for (int i = 0; i < codeBytes.Length; i++) codeBytes[i] = code[i].Value;
        var constantValues = new Vector4[constants.Length];
        for (int i = 0; i < constantValues.Length; i++) constantValues[i] = constants[i].Value;
        var inputRows = new int[inputs.Length];
        for (int i = 0; i < inputRows.Length; i++) inputRows[i] = inputs[i].Value;
        return new(row.RowName, codeBytes, constantValues, inputRows, row.BoundInputSlot);
    }

    private void ReadConstants(byte[] data, int resource)
    {
        foreach (ref readonly var row in ConstantRows<UnknownConstantScalarRow>(data, resource,
                     nameof(UnknownConstantPublisherResource.ScalarConstantsA), UnknownConstantScalarRow.Class))
            published.TryAdd(row.Name, new(row.Value, null));
        foreach (ref readonly var row in ConstantRows<UnknownConstantScalarRow>(data, resource,
                     nameof(UnknownConstantPublisherResource.ScalarConstantsB), UnknownConstantScalarRow.Class))
            published.TryAdd(row.Name, new(row.Value, null));
        foreach (ref readonly var row in ConstantRows<UnknownConstantVectorRow>(data, resource,
                     nameof(UnknownConstantPublisherResource.VectorConstants), UnknownConstantVectorRow.Class))
            published.TryAdd(row.Name, new(row.Value, null));
    }

    /** Rows of one publisher list; empty when the list leaves the config or holds another class. */
    private static ReadOnlySpan<T> ConstantRows<T>(byte[] data, int resource, string list, uint rowClass) where T : unmanaged
    {
        if (!TagView.FitsThrough<UnknownConstantPublisherResource>(data, resource, list)) return [];
        ref readonly var array = ref ArrayAt<UnknownConstantPublisherResource, T>(data, resource, list);
        return ArrayClass(data, in array) == rowClass ? TagView.Rows(data, in array, rowClass) : [];
    }

    /** A spawned actor is alive at full health: a fraction or the alive kind reads 1, and maximum minus value reads 0. */
    private void ReadDamage(byte[] data, int resource)
    {
        const string list = nameof(SCombatantDamagePayload.DamageValues);
        if (!TagView.FitsThrough<SCombatantDamagePayload>(data, resource, list)) return;
        ref readonly var values = ref ArrayAt<SCombatantDamagePayload, UnknownDamageValueRow>(data, resource, list);
        if (ArrayClass(data, in values) != UnknownDamageValueRow.Class) return;
        foreach (ref readonly var row in TagView.Rows(data, in values, UnknownDamageValueRow.Class))
        {
            byte kind = row.Kind, flags = row.Flags;
            bool inverted = (flags & MaximumMinusValue) != 0;
            var value = inverted || (flags & DividedByMaximum) != 0 || kind == AliveKind
                ? new ObjectChannelRestValue(new Vector4(inverted ? 0 : 1), null)
                : ObjectChannelRestValue.Open("damage amount without its maximum");
            published.TryAdd(row.Name, value);
        }
    }

    /** The element class word of an array; zero when it has no rows. With rows and a null header link, the word after the field. */
    private static uint ArrayClass<T>(byte[] data, ref readonly TagArray<T> array) where T : unmanaged
    {
        var view = new TagArray(data, TagView.OffsetOf(data, in array));
        return view.Count == 0 ? 0 : TagRead.U32(data, view.HeaderOffset + TagArray.ElementClassOffset);
    }

    /** The array field `member` of a TOwner at `owner`; only the field itself must fit. */
    internal static ref readonly TagArray<T> ArrayAt<TOwner, T>(byte[] data, int owner, string member)
        where TOwner : unmanaged where T : unmanaged =>
        ref TagView.Member<TOwner, TagArray<T>>(data, owner, member);

    /** A config's pointer field `member`; only the field itself must fit. */
    internal static ref readonly RelativePointer<Unknown> ConfigPointer(byte[] config, string member) =>
        ref TagView.Member<SEntityConfig, RelativePointer<Unknown>>(config, 0, member);

    /** The class word before the object at `at`; past the tag end it throws ArgumentOutOfRangeException. */
    private static uint ClassBefore(byte[] data, int at) =>
        TagView.ClassAt(data, at) ?? throw new ArgumentOutOfRangeException(nameof(at), "The class word is outside the tag.");
}

/**
 * Values a sequence player's outputs hold at rest. A type-2 work node writes its output with an expression of
 * the node's progress (0x7FF7420CDA40); progress is 0 for a node with no duration and 1 when a timed node ends.
 */
internal sealed class SequenceRest
{
    private const byte WriteOutput = 2, WriteByName = 3, SerialFlow = 0, RandomFlow = 2, GateFlow = 6, ChannelCondition = 0;
    private const int FlowKind = SequenceResource.FlowKind, WorkKind = SequenceResource.WorkKind;
    // Expression opcode that pushes an input; the node's progress is input 0.
    private const byte PushInput = 0x3C;

    // A node's body follows its 16-byte owner pointer.
    private static readonly int BodyOffset = Unsafe.SizeOf<TagOffsetPointer>();
    private const string WriterCode = "Expression.Expression.Bytecode", WriterConstants = "Expression.Expression.ConstantBuffer";

    /** A type-2 work node: reset to zero on stop, the output it writes, and its expression. */
    private sealed record WriterNode(bool Reset, int Output, byte[] Code, Vector4[] Constants);

    private readonly uint config;
    private readonly bool autoStart;
    private readonly SequenceNode[] flows, works;
    // Null for a work node that is not type 2.
    private readonly WriterNode?[] writerNodes;
    private readonly uint[] inputs, outputs;
    private readonly Dictionary<(int Kind, int Index), (int Flow, int Position)[]> paths;

    private SequenceRest(uint config, byte[] data, int resource)
    {
        this.config = config;
        var sequence = new SequenceResource(data, resource);
        (autoStart, flows, works, inputs, paths) = (sequence.AutoStart, sequence.Flows, sequence.Works, sequence.Inputs, sequence.Paths);
        writerNodes = works.Select(work => work.Type == WriteOutput ? ReadWriter(data, work.Body) : null).ToArray();
        outputs = OutputNames(data, resource);
        var written = new List<uint>(outputs);
        const string name = nameof(SequenceWriteByNameNode.WrittenName);
        foreach (var work in works)
        {
            int wrapper = work.Body - BodyOffset;
            if (work.Type != WriteByName) continue;
            CheckClass(data, wrapper, SequenceWriteByNameNode.Class);
            // A type-3 work node names the provider row it writes.
            if (TagView.FitsThrough<SequenceWriteByNameNode>(data, wrapper, name))
                written.Add(TagRead.U32(data, wrapper + TagView.FieldOffset<SequenceWriteByNameNode>(name)));
        }
        Writes = written;
    }

    // The output rows are read without a class check.
    private static uint[] OutputNames(byte[] data, int resource)
    {
        ref readonly var list = ref ObjectChannelRest.ArrayAt<SParticleSequenceResource, C808084df>(data, resource,
            nameof(SParticleSequenceResource.Outputs));
        if (list.Count == 0) return [];
        var rows = TagView.Rows(data, in list);
        var names = new uint[rows.Length];
        for (int i = 0; i < names.Length; i++) names[i] = rows[i].Unk28;
        return names;
    }

    /** Names this sequence can write: its outputs and the provider rows its type-3 nodes name. */
    public IReadOnlyList<uint> Writes { get; }

    /** @return Null when the config holds no readable sequence. */
    public static SequenceRest? Read(uint config, byte[] data, int resource)
    {
        const string flags = nameof(SParticleSequenceResource.LifecycleBit0StartOnSpawnBit1Retire);
        try
        {
            return TagView.FitsThrough<SParticleSequenceResource>(data, resource, flags) ? new SequenceRest(config, data, resource) : null;
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException
            or IndexOutOfRangeException) { return null; }
    }

    /**
     * The value this sequence's output of that name holds at rest; null when it has none or no writer runs.
     * @param channel Rest value of an input channel, for gates and start conditions.
     */
    public ObjectChannelRestValue? Rest(uint name, Func<uint, ObjectChannelRestValue> channel)
    {
        int output = Array.IndexOf(outputs, name);
        if (output < 0 || !autoStart) return null;
        var writers = new List<(float End, Vector4 Value)>();
        for (int index = 0; index < works.Length; index++)
        {
            var work = works[index];
            if (work.Type != WriteOutput || writerNodes[index]!.Output != output || !paths.TryGetValue((WorkKind, index), out var path)) continue;
            var writer = writerNodes[index]!;
            float start = Finite(work.DelayMin);
            bool runs = true, loops = false;
            foreach (var (flowIndex, position) in path)
            {
                var flow = flows[flowIndex];
                start += Finite(flow.DelayMin);
                if (flow.Type == RandomFlow) return Open("a random sequence flow picks the writer");
                loops |= flow.Loop != 0 && flow.Type != GateFlow;
                var gate = flow.Type == GateFlow ? new SequenceCondition(ChannelCondition, flow.RangeMin, flow.RangeMax, flow.RangeChannel) : null;
                foreach (var condition in new[] { gate, flow.Start })
                {
                    if (condition is null) continue;
                    if (condition.Kind != ChannelCondition) return Open("a sequence start condition is not a channel range");
                    if (condition.Channel < 0 || condition.Channel >= inputs.Length) return Open("a sequence gate names no channel");
                    var value = channel(inputs[condition.Channel]);
                    if (value.Unsettled is not null) return Open($"a sequence gate reads 0x{inputs[condition.Channel]:X8}, which is not settled");
                    runs &= value.Value.X >= condition.Min && condition.Max > value.Value.X;
                }
                if (flow.Type != SerialFlow) continue;
                // A serial child starts when the one before it ends; one without a timer never ends.
                for (int earlier = 0; earlier < position && runs; earlier++)
                {
                    var (kind, at) = flow.Children[earlier];
                    var sibling = kind == FlowKind && at < flows.Length ? flows[at] : kind == WorkKind && at < works.Length ? works[at] : null;
                    if (sibling is not { Timed: true }) runs = false;
                    else start += Finite(sibling.DelayMin) + Finite(sibling.DurationMax);
                }
            }
            if (!runs) continue;
            bool progress = ReadsInput(writer.Code);
            if (loops && progress) return Open("a looping sequence flow repeats the writer");
            if (!work.Timed && progress) return Open("an untimed writer reads its progress");
            float duration = Finite(work.DurationMax);
            float ended = work.Timed && duration > 0 ? 1 : 0;
            var result = work.Timed && writer.Reset ? Vector4.Zero
                : ProviderExpression.Run(writer.Code, writer.Constants, [new Vector4(ended)]);
            if (result is not { } written) return Open("a sequence writer uses an operation the viewer does not run");
            writers.Add((work.Timed ? start + duration : float.PositiveInfinity, written));
        }
        if (writers.Count == 0) return null;
        float last = writers.Max(writer => writer.End);
        var values = writers.Where(writer => writer.End == last).Select(writer => writer.Value).Distinct().ToArray();
        return values.Length == 1 ? new ObjectChannelRestValue(values[0], null, config)
            : Open("sequence writers end together with different values");
    }

    private ObjectChannelRestValue Open(string reason) => ObjectChannelRestValue.Open(reason) with { Changeable = config };

    /** Opcodes 0x22 and 0x34 to 0x3E take one operand byte, as ProviderExpression reads them. */
    private static bool ReadsInput(byte[] code)
    {
        for (int at = 0; at < code.Length; at += code[at] is 0x22 or >= 0x34 and <= 0x3E ? 2 : 1)
            if (code[at] == PushInput) return true;
        return false;
    }

    /** The expression arrays are read without a class check. */
    private static WriterNode ReadWriter(byte[] data, int body)
    {
        int wrapper = body - BodyOffset;
        CheckClass(data, wrapper, SequenceWriteOutputNode.Class);
        if (!TagView.FitsThrough<SequenceWriteOutputNode>(data, wrapper, WriterConstants))
            throw new InvalidDataException($"Sequence writer at 0x{body:X} leaves its config.");
        bool reset = TagRead.U8(data, wrapper + TagView.FieldOffset<SequenceWriteOutputNode>(
            nameof(SequenceWriteOutputNode.ResetOnStop))) != 0;
        int output = TagRead.I32(data, wrapper + TagView.FieldOffset<SequenceWriteOutputNode>(
            nameof(SequenceWriteOutputNode.OutputIndex)));
        ref readonly var codeArray = ref ObjectChannelRest.ArrayAt<SequenceWriteOutputNode, ExpressionCode>(data, wrapper, WriterCode);
        var code = codeArray.Count != 0 ? TagView.Rows(data, in codeArray) : [];
        var codeBytes = new byte[code.Length];
        for (int i = 0; i < codeBytes.Length; i++) codeBytes[i] = code[i].Value;
        ref readonly var constantArray = ref ObjectChannelRest.ArrayAt<SequenceWriteOutputNode, ExpressionConstant>(data, wrapper,
            WriterConstants);
        var constants = constantArray.Count != 0 ? TagView.Rows(data, in constantArray) : [];
        var constantValues = new Vector4[constants.Length];
        for (int i = 0; i < constantValues.Length; i++) constantValues[i] = constants[i].Value;
        return new(reset, output, codeBytes, constantValues);
    }

    /** A work node's kind byte picks its layout; its class word must agree. */
    private static void CheckClass(byte[] data, int wrapper, uint expected)
    {
        if (TagView.ClassAt(data, wrapper) != expected)
            throw new InvalidDataException($"Sequence node at 0x{wrapper:X} is not class 0x{expected:X8}.");
    }

    /** Delays and durations that are not finite read as zero. */
    private static float Finite(float value) => float.IsFinite(value) ? value : 0;
}

/**
 * Rest channel tables of entity models. No authored spawn input reaches the value provider, so a table
 * depends on the entity and its model config only. Equal tables are one shared array.
 */
public sealed class ObjectChannelTables(PackageCatalog catalog)
{
    private const uint ConfigClass = SEntityConfig.Class, ModelResource = SModelComponent.Class;
    private readonly Dictionary<(uint Entity, uint Config), (uint[] Names, Vector4[]? Table)> models = [];
    private readonly Dictionary<Vector4[], Vector4[]> shared = new(TableComparer.Instance);
    private readonly Dictionary<uint, byte[]> tags = [];

    /** Distinct tables handed out; a model whose channels all rest at one is not counted. */
    public int DistinctTables => shared.Count;
    /** Models whose channels did not read; they take the viewer default. */
    public int Unread { get; private set; }

    /** Channel names of the model config in TFX order; empty when it has none or does not read. */
    public IReadOnlyList<uint> Names(uint entity, uint modelConfig) => Model(entity, modelConfig).Names;

    /** The model's channel values at rest; null when every one rests at one, the viewer default. */
    public IReadOnlyList<Vector4>? Table(uint entity, uint modelConfig) => Model(entity, modelConfig).Table;

    private (uint[] Names, Vector4[]? Table) Model(uint entity, uint config)
    {
        if (models.TryGetValue((entity, config), out var known)) return known;
        (uint[] Names, Vector4[]? Table) result = ([], null);
        try
        {
            var data = Get(config);
            // A config with a null Definition has no model resource.
            if (TagView.TargetOffset(data, in ObjectChannelRest.ConfigPointer(data,
                    nameof(SEntityConfig.Definition))) is { } resource
                && resource >= TagView.ClassWordSize && TagView.Fits<uint>(data, resource)
                && TagView.ClassAt(data, resource) == ModelResource
                && ObjectChannelRest.RequestNames(data, resource) is { Length: > 0 } names)
            {
                var definition = Get(entity);
                var componentRows = TagView.Rows(definition, in ObjectChannelRest.ArrayAt<SEntity, SPatternComponent>(definition, 0,
                    nameof(SEntity.Components)), SPatternComponent.Class);
                var componentTags = new uint[componentRows.Length];
                for (int i = 0; i < componentTags.Length; i++) componentTags[i] = componentRows[i].Config;
                var configs = componentTags
                    .Where(tag => catalog.TryGetEntry(tag, out var entry) && entry.Reference == ConfigClass)
                    .Select(tag => (tag, Get(tag)));
                var values = ObjectChannelRest.RestValues(configs, names).Select(rest => rest.Value).ToArray();
                Vector4[]? table = null;
                if (values.Any(value => value != Vector4.One) && !shared.TryGetValue(values, out table))
                    shared.Add(values, table = values);
                result = (names, table);
            }
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException
            or IOException or KeyNotFoundException or IndexOutOfRangeException)
        { Unread++; }
        return models[(entity, config)] = result;
    }

    private byte[] Get(uint tag)
    {
        if (!tags.TryGetValue(tag, out var data)) tags.Add(tag, data = catalog.Read(tag));
        return data;
    }

    /** Tables compare by their float bits, so equal values share one array. */
    private sealed class TableComparer : IEqualityComparer<Vector4[]>
    {
        public static readonly TableComparer Instance = new();
        public bool Equals(Vector4[]? a, Vector4[]? b) => ReferenceEquals(a, b) || a is not null && b is not null
            && System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan())
                .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(b.AsSpan()));
        public int GetHashCode(Vector4[] table)
        {
            var hash = new HashCode();
            hash.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(table.AsSpan()));
            return hash.ToHashCode();
        }
    }
}
