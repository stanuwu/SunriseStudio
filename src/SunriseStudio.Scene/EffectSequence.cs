using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

/** Reads the class-0x808084E9 sequence resource and gives each particle node its activation. */
internal static class EffectSequence
{
    // Work node type 4 is the particle node.
    private const byte ParticleType = 4;
    // Flow types: 0 serial, 2 random by weight, 6 channel gate.
    private const byte SerialFlow = 0, RandomFlow = 2, GateFlow = 6;
    private const int FlowKind = SequenceResource.FlowKind, WorkKind = SequenceResource.WorkKind;

    /**
     * Activations of the config's particle nodes, keyed by node body offset.
     * @return Empty when the config holds no sequence resource.
     */
    public static Dictionary<int, EffectActivation> Read(uint config, byte[] data)
    {
        var result = new Dictionary<int, EffectActivation>();
        // The config must reach Definition; the members after it are not read.
        if (!TagView.FitsThrough<SEntityConfig>(data, 0, nameof(SEntityConfig.Definition))) return result;
        ref readonly var definition = ref TagView.At<RelativePointer<Unknown>>(data,
            TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition)));
        if (TagView.TargetOffset(data, in definition) is not { } resource
            || TagView.TargetClass(data, in definition) != SParticleSequenceResource.Class
            || !TagView.Fits<SParticleSequenceResource>(data, resource)) return result;
        var sequence = new SequenceResource(data, resource);
        var works = sequence.Works;
        for (int index = 0; index < works.Length; index++)
        {
            var work = works[index];
            if (work.Type != ParticleType) continue;
            result[work.Body] = Activate(config, index, work, sequence.AutoStart,
                sequence.Paths.GetValueOrDefault((WorkKind, index)), sequence.Flows, works, sequence.Inputs);
        }
        return result;
    }

    /** Folds the flows on the path into the node's gates, delays, picks and serial start. */
    private static EffectActivation Activate(uint config, int index, SequenceNode work, bool autoStart,
        (int Flow, int Position)[]? path, SequenceNode[] flows, SequenceNode[] works, uint[] channels)
    {
        var gates = new List<EffectGate>();
        var picks = new List<EffectRandomPick>();
        bool unknown = false, loop = false;
        float delay = Finite(work.DelayMin);
        float? serial = 0;
        foreach (var (flowIndex, position) in path ?? [])
        {
            var flow = flows[flowIndex];
            delay += Finite(flow.DelayMin);
            loop |= flow.Loop != 0;
            if (flow.Type == GateFlow) unknown |= !AddGate(gates, channels, flow.RangeChannel, flow.RangeMin, flow.RangeMax);
            if (flow.Start is { } start)
                unknown |= start.Kind != 0 || !AddGate(gates, channels, start.Channel, start.Min, start.Max);
            if (flow.Type == RandomFlow) picks.Add(new(flowIndex, position, flow.Children.Length));
            if (flow.Type != SerialFlow) continue;
            // A sibling takes its delay and duration; that a flow sibling ends on its own timer is assumed.
            for (int earlier = 0; earlier < position && serial is not null; earlier++)
            {
                var (kind, at) = flow.Children[earlier];
                var sibling = kind == FlowKind && at < flows.Length ? flows[at] : kind == WorkKind && at < works.Length ? works[at] : null;
                serial = sibling is { Timed: true } ? serial + Finite(sibling.DelayMin) + Finite(sibling.DurationMax) : null;
            }
        }
        return new EffectActivation
        {
            SequenceTag = config, WorkIndex = index, AutoStart = autoStart, Reachable = path is not null,
            UnknownCondition = unknown, Continuous = !work.Timed || loop, Timed = work.Timed, Delay = delay,
            Duration = work.Timed ? Finite(work.DurationMax) : 0, SerialStart = serial, Gates = gates, RandomPicks = picks
        };
    }

    /** @return False when the channel index is outside the sequence's channel list. */
    private static bool AddGate(List<EffectGate> gates, uint[] channels, int channel, float min, float max)
    {
        if (channel < 0 || channel >= channels.Length) return false;
        gates.Add(new(channels[channel], Finite(min), Finite(max)));
        return true;
    }

    /** Infinities and NaN do not survive the bundle's JSON; the range ends at the float limit. */
    private static float Finite(float value) =>
        float.IsNaN(value) ? 0 : Math.Clamp(value, float.MinValue, float.MaxValue);
}
