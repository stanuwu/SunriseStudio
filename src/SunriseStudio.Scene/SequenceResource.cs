using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

/** A sequence start condition; kind 0 is a channel range [Min, Max). */
internal sealed record SequenceCondition(byte Kind, float Min, float Max, int Channel);

/**
 * A flow or work node as stored; offsets are from the body, 16 bytes after its wrapper.
 * Only flow nodes carry children and a start condition; only gate nodes carry a gate range.
 */
internal sealed record SequenceNode(int Body, byte Flags, float DelayMin, float DurationMax, byte Type, byte Loop,
    (int Kind, int Index)[] Children, SequenceCondition? Start, float RangeMin, float RangeMax, int RangeChannel)
{
    // Flag bits 0x08 and 0x10 mean the node has no timer.
    private const byte NoTimer = 0x18;
    public bool Timed => (Flags & NoTimer) == 0;
}

/** The class-0x808084E9 sequence resource: its nodes, input channels and the first path to each node. */
internal sealed class SequenceResource
{
    public const uint ResourceClass = SParticleSequenceResource.Class;
    /** Offset of the lifecycle byte in the resource; bit 0 starts the sequence on spawn. */
    public static readonly int Flags =
        TagView.FieldOffset<SParticleSequenceResource>(nameof(SParticleSequenceResource.LifecycleBit0StartOnSpawnBit1Retire));
    // Root and child references: kind 0 flow, 1 work.
    public const int FlowKind = 0, WorkKind = 1;
    // The body follows the node's 16-byte owner pointer.
    private static readonly int BodyOffset = Unsafe.SizeOf<TagOffsetPointer>();
    // A work node needs its body through the pad byte after Loop.
    private static readonly int WorkBodySize =
        TagView.FieldOffset<C808093db>(nameof(C808093db.Loop)) + sizeof(ushort) - BodyOffset;
    // Every flow class holds Children and StartCondition at the gate node's offsets.
    private static readonly int FlowBodySize = TagView.FieldOffset<SequenceRangeGateNode>(nameof(SequenceRangeGateNode.StartCondition))
        + Unsafe.SizeOf<RelativePointer<SequenceStartCondition>>() - BodyOffset;
    // Only a gate node holds the range; it needs its body through the gate channel.
    private static readonly int GateBodySize =
        TagView.FieldOffset<SequenceRangeGateNode>(nameof(SequenceRangeGateNode.GateRangeChannel)) + sizeof(int) - BodyOffset;

    public SequenceNode[] Flows { get; }
    public SequenceNode[] Works { get; }
    /** Input channel names, in the order gates and start conditions index them. */
    public uint[] Inputs { get; }
    public bool AutoStart { get; }
    /** The first path that reaches each node, as (flow, child position) steps from the root. */
    public Dictionary<(int Kind, int Index), (int Flow, int Position)[]> Paths { get; } = [];

    /** Throws InvalidDataException when a node or row leaves the data. Reads only the members it uses. */
    public SequenceResource(byte[] data, int resource)
    {
        Flows = ReadNodes(data, EntityFields.Rows(data, in TagView.Member<SParticleSequenceResource, TagArray<SSequenceFlowRow>>(data, resource,
            nameof(SParticleSequenceResource.FlowNodes)), SSequenceFlowRow.Class), true);
        Works = ReadNodes(data, EntityFields.Rows(data, in TagView.Member<SParticleSequenceResource, TagArray<SSequenceFlowRow>>(data, resource,
            nameof(SParticleSequenceResource.WorkNodes)), SSequenceFlowRow.Class), false);
        var inputs = EntityFields.Rows(data, in TagView.Member<SParticleSequenceResource, TagArray<SModelValueBankResource>>(data, resource,
            nameof(SParticleSequenceResource.InputChannels)), SModelValueBankResource.Class);
        Inputs = new uint[inputs.Length];
        for (int i = 0; i < inputs.Length; i++) Inputs[i] = inputs[i].ChannelHashReloading0xecde609e;
        uint root = TagView.Member<SParticleSequenceResource, uint>(data, resource, nameof(SParticleSequenceResource.RootNode));
        AutoStart = (TagView.Member<SParticleSequenceResource, byte>(data, resource,
            nameof(SParticleSequenceResource.LifecycleBit0StartOnSpawnBit1Retire)) & 1) != 0;
        Visit((int)(root & 0xFF), (int)(root >> 16), []);
    }

    private static SequenceNode[] ReadNodes(byte[] data, ReadOnlySpan<SSequenceFlowRow> rows, bool flow)
    {
        var nodes = new SequenceNode[rows.Length];
        for (int i = 0; i < rows.Length; i++) nodes[i] = ReadNode(data, in rows[i], flow);
        return nodes;
    }

    /** The first path that reaches each node wins; a revisit ends the walk there. */
    private void Visit(int kind, int index, (int Flow, int Position)[] path)
    {
        if (kind is not (FlowKind or WorkKind) || !Paths.TryAdd((kind, index), path)) return;
        if (kind != FlowKind || index >= Flows.Length) return;
        var children = Flows[index].Children;
        for (int position = 0; position < children.Length; position++)
            Visit(children[position].Kind, children[position].Index, [.. path, (index, position)]);
    }

    /**
     * A row's pointer lands on the wrapper; a null pointer is refused.
     * Only a node of the gate class has a gate range; other nodes read it as none.
     */
    private static SequenceNode ReadNode(byte[] data, ref readonly SSequenceFlowRow node, bool flow)
    {
        int row = TagView.OffsetOf(data, in node);
        if (TagView.TargetOffset(data, in node.OwnerPointer) is not { } wrapper)
            throw new InvalidDataException($"Sequence node at 0x{row:X} has no node.");
        int body = wrapper + BodyOffset;
        bool gate = flow && TagView.ClassAt(data, wrapper) == SequenceRangeGateNode.Class;
        if (!Inside(data, body, !flow ? WorkBodySize : gate ? GateBodySize : FlowBodySize))
            throw new InvalidDataException($"Sequence node at 0x{row:X} leaves its config.");
        byte flags = TagView.Member<C808093db, byte>(data, wrapper, nameof(C808093db.NodeFlags));
        float delayMin = TagView.Member<C808093db, float>(data, wrapper, nameof(C808093db.StartMin));
        float durationMax = TagView.Member<C808093db, float>(data, wrapper, nameof(C808093db.DurationMax));
        byte type = TagView.Member<C808093db, byte>(data, wrapper, nameof(C808093db.NodeKind));
        byte loop = TagView.Member<C808093db, byte>(data, wrapper, nameof(C808093db.Loop));
        if (!flow) return new(body, flags, delayMin, durationMax, type, loop, [], null, 0, 0, -1);
        ref readonly var childList = ref TagView.Member<SequenceRangeGateNode, TagArray<SequenceChildRef>>(data, wrapper,
            nameof(SequenceRangeGateNode.Children));
        var childRows = childList.Count == 0 ? [] : EntityFields.Rows(data, in childList);
        var children = new (int Kind, int Index)[childRows.Length];
        for (int i = 0; i < children.Length; i++) children[i] = (childRows[i].Kind, childRows[i].Index);
        SequenceCondition? start = null;
        ref readonly var condition = ref TagView.Member<SequenceRangeGateNode, RelativePointer<SequenceStartCondition>>(data, wrapper,
            nameof(SequenceRangeGateNode.StartCondition));
        if (TagView.TargetOffset(data, in condition) is { } at)
        {
            if (!TagView.Fits<SequenceStartCondition>(data, at))
                throw new InvalidDataException($"Start condition of node 0x{body:X} leaves its config.");
            ref readonly var value = ref TagView.At<SequenceStartCondition>(data, at);
            start = new(value.Kind, value.Minimum, value.Maximum, value.Channel);
        }
        if (!gate) return new(body, flags, delayMin, durationMax, type, loop, children, start, 0, 0, -1);
        return new(body, flags, delayMin, durationMax, type, loop, children, start,
            TagView.Member<SequenceRangeGateNode, float>(data, wrapper, nameof(SequenceRangeGateNode.GateRangeMin)),
            TagView.Member<SequenceRangeGateNode, float>(data, wrapper, nameof(SequenceRangeGateNode.GateRangeMax)),
            TagView.Member<SequenceRangeGateNode, int>(data, wrapper, nameof(SequenceRangeGateNode.GateRangeChannel)));
    }

    private static bool Inside(ReadOnlySpan<byte> tag, int offset, int count) =>
        offset >= 0 && count >= 0 && offset <= tag.Length - count;
}
