using System.Buffers.Binary;
using System.Text;

namespace SunriseStudio.Audio;

/** A property value; its 32 bits are a float or an integer depending on the property. */
public sealed record WwiseProp(byte Id, uint Raw)
{
    public float Value => BitConverter.UInt32BitsToSingle(Raw);
}
/** A randomized offset; Wwise picks a value between the bounds on each play. */
public sealed record WwiseRangedProp(byte Id, uint MinimumRaw, uint MaximumRaw)
{
    public float Minimum => BitConverter.UInt32BitsToSingle(MinimumRaw);
    public float Maximum => BitConverter.UInt32BitsToSingle(MaximumRaw);
    /** False when both bounds are zero, either sign. */
    public bool Varies => ((MinimumRaw | MaximumRaw) & 0x7FFFFFFF) != 0;
    /** The midpoint of a float property's range. */
    public double Midpoint => ((double)Minimum + Maximum) / 2;
}
public sealed record WwiseGraphPoint(float From, float To, uint Curve);
public sealed record WwiseRtpc(uint Id, byte Type, byte Accumulation, byte Parameter, uint Curve, byte Scaling,
    IReadOnlyList<WwiseGraphPoint> Points);
public sealed record WwiseStateGroup(uint Group, byte Sync, IReadOnlyList<(uint State, uint Instance)> States);
/**
 * Shared node parameters.
 * @param Positioning Bit 0 overrides the parent; bits 1 and 2 enable the 2D panner; bit 3 adds 3D.
 */
public sealed record WwiseNodeBase(bool OverrideParentEffects, int EffectCount, uint OutputBus, uint Parent,
    IReadOnlyList<WwiseProp> Props, IReadOnlyList<WwiseRangedProp> Ranged, IReadOnlyList<WwiseStateGroup> States,
    IReadOnlyList<WwiseRtpc> Rtpcs, IReadOnlyList<uint>? Effects = null, byte Positioning = 0)
{
    public float Prop(byte id, float fallback = 0) => Props.FirstOrDefault(prop => prop.Id == id)?.Value ?? fallback;
}
public sealed record WwiseSource(uint Plugin, byte StreamType, uint MediaId, uint MediaSize, byte Bits,
    byte[] Parameters)
{
    /** Plug-in type 1 is a codec; type 2 is a source plug-in that generates audio. */
    public bool Codec => (Plugin & 0xF) == 1;
}

public abstract record WwiseObject(byte Type, uint Id);
public sealed record WwiseState(uint Id, IReadOnlyList<WwiseProp> Props) : WwiseObject(1, Id);
public sealed record WwiseSound(uint Id, WwiseSource Source, WwiseNodeBase Node) : WwiseObject(2, Id);
public sealed record WwiseAction(uint Id, ushort ActionType, uint Target, bool TargetsBus, IReadOnlyList<WwiseProp> Props,
    IReadOnlyList<WwiseRangedProp> Ranged, byte[] Parameters) : WwiseObject(3, Id)
{
    /** The action kind sits in the high byte; 0x04 is Play. */
    public int Kind => ActionType >> 8;
    /** Milliseconds; the delay is an integer property and a range adds its midpoint. */
    public double Delay => (int)(Props.FirstOrDefault(prop => prop.Id == WwiseHierarchy.DelayProp)?.Raw ?? 0)
        + Ranged.Where(range => range.Id == WwiseHierarchy.DelayProp)
            .Sum(range => ((double)(int)range.MinimumRaw + (int)range.MaximumRaw) / 2);
}
public sealed record WwiseEvent(uint Id, IReadOnlyList<uint> Actions) : WwiseObject(4, Id);
public sealed record WwisePlaylistEntry(uint Id, int Weight);
public sealed record WwiseRandomSequence(uint Id, WwiseNodeBase Node, ushort LoopCount, ushort LoopMinimum,
    ushort LoopMaximum, float TransitionTime, float TransitionMinimum, float TransitionMaximum, ushort AvoidRepeat,
    byte TransitionMode, byte RandomMode, byte Mode, byte Flags, IReadOnlyList<uint> Children,
    IReadOnlyList<WwisePlaylistEntry> Playlist) : WwiseObject(5, Id)
{
    public bool Sequence => Mode == 1;
    public bool Continuous => (Flags & 8) != 0;
    public bool UsesWeights => (Flags & 1) != 0;
}
public sealed record WwiseSwitchParameters(uint Node, byte Flags, byte Mode, int FadeOut, int FadeIn);
public sealed record WwiseSwitch(uint Id, WwiseNodeBase Node, byte GroupType, uint Group, uint DefaultSwitch,
    bool ContinuousValidation, IReadOnlyList<uint> Children, IReadOnlyList<(uint Switch, IReadOnlyList<uint> Nodes)> Groups,
    IReadOnlyList<WwiseSwitchParameters> Parameters) : WwiseObject(6, Id);
public sealed record WwiseActorMixer(uint Id, WwiseNodeBase Node, IReadOnlyList<uint> Children) : WwiseObject(7, Id);
public sealed record WwiseDuck(uint Bus, float Volume, int FadeOut, int FadeIn, byte Curve, byte Property);
public sealed record WwiseBus(byte Kind, uint Id, uint OutputBus, IReadOnlyList<WwiseProp> Props, int EffectCount,
    IReadOnlyList<WwiseDuck> Ducks, IReadOnlyList<WwiseRtpc> Rtpcs, IReadOnlyList<WwiseStateGroup> States,
    IReadOnlyList<uint>? Effects = null)
    : WwiseObject(Kind, Id)
{
    public float Prop(byte id, float fallback = 0) => Props.FirstOrDefault(prop => prop.Id == id)?.Value ?? fallback;
}
public sealed record WwiseLayerAssociation(uint Child, IReadOnlyList<WwiseGraphPoint> Points);
public sealed record WwiseLayer(uint Id, IReadOnlyList<WwiseRtpc> Rtpcs, uint Rtpc, byte RtpcType,
    IReadOnlyList<WwiseLayerAssociation> Associations);
public sealed record WwiseLayerContainer(uint Id, WwiseNodeBase Node, IReadOnlyList<uint> Children,
    IReadOnlyList<WwiseLayer> Layers) : WwiseObject(9, Id);
public sealed record WwiseMeter(double GridPeriod, double GridOffset, float Tempo, byte BeatsPerBar, byte BeatValue);
public sealed record WwiseMusicNode(byte Flags, WwiseNodeBase Node, IReadOnlyList<uint> Children, WwiseMeter Meter,
    byte MeterFlag, int Stingers);
public sealed record WwiseMarker(uint Id, double Position, string? Name);
public sealed record WwiseMusicSegment(uint Id, WwiseMusicNode Music, double Duration, IReadOnlyList<WwiseMarker> Markers)
    : WwiseObject(10, Id);
public sealed record WwiseClip(uint Track, uint Source, double PlayAt, double BeginTrim, double EndTrim, double SourceDuration);
public sealed record WwiseClipAutomation(uint Clip, uint Type, IReadOnlyList<WwiseGraphPoint> Points);
public sealed record WwiseMusicTrack(uint Id, byte Flags, IReadOnlyList<WwiseSource> Sources, IReadOnlyList<WwiseClip> Clips,
    uint SubTracks, IReadOnlyList<WwiseClipAutomation> Automation, WwiseNodeBase Node, byte TrackType, int LookAhead)
    : WwiseObject(11, Id);
public sealed record WwiseFade(int Time, uint Curve, int Offset);
public sealed record WwiseSourceRule(WwiseFade Fade, uint Sync, uint CueFilter, bool PostExit);
public sealed record WwiseDestinationRule(WwiseFade Fade, uint CueFilter, uint JumpTo, ushort Entry, bool PreEntry,
    bool MatchSourceCue);
public sealed record WwiseBridge(uint Segment, WwiseFade FadeIn, WwiseFade FadeOut, bool PreEntry, bool PostExit);
public sealed record WwiseTransitionRule(IReadOnlyList<uint> Sources, IReadOnlyList<uint> Destinations,
    WwiseSourceRule Source, WwiseDestinationRule Destination, WwiseBridge? Bridge);
public sealed record WwiseDecisionNode(uint Key, uint AudioNode, ushort Weight, ushort Probability,
    IReadOnlyList<WwiseDecisionNode> Children);
public sealed record WwiseMusicSwitch(uint Id, WwiseMusicNode Music, IReadOnlyList<WwiseTransitionRule> Rules,
    bool ContinuePlayback, IReadOnlyList<(uint Group, byte Type)> Arguments, byte Mode,
    IReadOnlyList<WwiseDecisionNode> Tree) : WwiseObject(12, Id);
public sealed record WwiseMusicItem(uint Segment, uint Id, int Kind, short Loop, short LoopMinimum, short LoopMaximum,
    uint Weight, ushort AvoidRepeat, bool UsesWeights, bool Shuffle, IReadOnlyList<WwiseMusicItem> Children);
public sealed record WwiseMusicPlaylist(uint Id, WwiseMusicNode Music, IReadOnlyList<WwiseTransitionRule> Rules,
    WwiseMusicItem? Root) : WwiseObject(13, Id);
/** Attenuations, effects and modulators; effects also keep their plug-in ID and parameter block. */
public sealed record WwiseOther(byte Kind, uint Id, IReadOnlyList<WwiseRtpc> Rtpcs, uint Plugin = 0, byte[]? Parameters = null)
    : WwiseObject(Kind, Id);

public sealed record WwiseBankObjects(uint Bank, uint Version, IReadOnlyList<WwiseObject> Objects);

/** Version-113 HIRC objects read field by field; an object that does not fill its record exactly is skipped. */
public sealed class WwiseHierarchy
{
    // Property IDs.
    public const byte VolumeProp = 0, BusVolumeProp = 5, DelayProp = 14, LoopProp = 58;
    // Effect types whose payload is only parameters and game-parameter curves.
    private const byte Attenuation = 14, EffectShareSet = 18, EffectCustom = 19, LfoModulator = 21, EnvelopeModulator = 22;
    private const uint Version = 113;

    public IReadOnlyList<WwiseBankObjects> Banks { get; private init; } = [];
    /** Sounds, containers, music objects and buses by ID. */
    public IReadOnlyDictionary<uint, WwiseObject> Objects { get; private init; } = new Dictionary<uint, WwiseObject>();
    public IReadOnlyList<string> Diagnostics { get; private init; } = [];
    // Events, actions, states and plug-in settings have their own ID spaces; a bus may share an event's ID.
    private IReadOnlyDictionary<(Space, uint), WwiseObject> Spaces { get; init; } = new Dictionary<(Space, uint), WwiseObject>();

    private enum Space { Node, Event, Action, State, Settings }

    public T? Get<T>(uint id) where T : WwiseObject =>
        Spaces.TryGetValue((SpaceOf(typeof(T)), id), out var item) ? item as T : null;

    private static Space SpaceOf(Type type) =>
        type == typeof(WwiseEvent) ? Space.Event : type == typeof(WwiseAction) ? Space.Action
        : type == typeof(WwiseState) ? Space.State : type == typeof(WwiseOther) ? Space.Settings : Space.Node;

    // Positioning byte bits, the left-right pan property and the RTPC parameter that drives it.
    private const byte OverridePositioning = 1, PannerEnabled = 6, Positioning3D = 8, PanLeftRightProp = 11, PanLeftRightParameter = 20;

    /** The shared parameters of a sound, container or music object; null for other objects. */
    public static WwiseNodeBase? NodeOf(WwiseObject? value) => value switch
    {
        WwiseSound item => item.Node, WwiseRandomSequence item => item.Node, WwiseSwitch item => item.Node,
        WwiseActorMixer item => item.Node, WwiseLayerContainer item => item.Node, WwiseMusicTrack item => item.Node,
        WwiseMusicSegment item => item.Music.Node, WwiseMusicSwitch item => item.Music.Node, WwiseMusicPlaylist item => item.Music.Node,
        _ => null
    };

    /**
     * The 2D placement the engine gives a sound or music track: positioning comes from the nearest node up the
     * parent chain that overrides it, with nothing summed.
     * @return Pan is the panner position 0..1 when that node enables the 2D panner, else null. Spatial is a 3D node.
     *     PanCurve is a game-parameter curve on the pan, which a preview does not apply.
     */
    public (float? Pan, bool Spatial, bool PanCurve) Positioning(uint id)
    {
        WwiseNodeBase? owner = null; var visited = new HashSet<uint>();
        for (uint at = id; at != 0 && visited.Add(at);)
        {
            var node = NodeOf(Objects.GetValueOrDefault(at));
            if (node is null) break;
            owner = node;
            if ((node.Positioning & OverridePositioning) != 0) break;
            at = node.Parent;
        }
        // A chain with no override keeps the defaults: 2D with the panner off.
        if (owner is null || (owner.Positioning & OverridePositioning) == 0) return (null, false, false);
        if ((owner.Positioning & Positioning3D) != 0) return (null, true, false);
        if ((owner.Positioning & PannerEnabled) != PannerEnabled) return (null, false, false);
        bool curve = owner.Rtpcs.Any(rtpc => rtpc.Parameter == PanLeftRightParameter && rtpc.Points.Count > 0);
        // The engine maps the -100..100 pan to 0..1 in single precision.
        return (Math.Clamp(owner.Prop(PanLeftRightProp) * 0.0049999999f + 0.5f, 0f, 1f), false, curve);
    }

    /** Reads banks in order; an ID seen twice in one ID space keeps its first object. */
    public static WwiseHierarchy Read(IReadOnlyList<AudioBank> banks)
    {
        var result = new List<WwiseBankObjects>(); var objects = new Dictionary<uint, WwiseObject>();
        var spaces = new Dictionary<(Space, uint), WwiseObject>(); var diagnostics = new List<string>();
        foreach (var bank in banks)
        {
            if (bank.Version != Version)
            {
                diagnostics.Add("Detailed music timelines need bank version 113.");
                continue;
            }
            var list = new List<WwiseObject>();
            foreach (var item in bank.Objects)
            {
                WwiseObject? parsed;
                try { parsed = Parse(item); }
                catch (InvalidDataException error)
                {
                    diagnostics.Add($"Bank {bank.Id}: object {item.Id} (type {item.Type}) skipped: {error.Message}");
                    continue;
                }
                if (parsed is null) continue;
                list.Add(parsed);
                var space = SpaceOf(parsed.GetType());
                if (spaces.TryAdd((space, parsed.Id), parsed) && space == Space.Node) objects.Add(parsed.Id, parsed);
            }
            result.Add(new(bank.Id, bank.Version, list));
        }
        return new() { Banks = result, Objects = objects, Spaces = spaces, Diagnostics = diagnostics };
    }

    /** Parses one object; an unknown type returns null and an unknown layout throws. */
    public static WwiseObject? Parse(AudioBankObject item)
    {
        var read = new WwiseReader(item.Data);
        WwiseObject? result = item.Type switch
        {
            1 => new WwiseState(item.Id, Props(read)),
            2 => new WwiseSound(item.Id, Source(read), NodeBase(read)),
            3 => ReadAction(item.Id, read),
            4 => new WwiseEvent(item.Id, Ids(read, read.U32())),
            5 => ReadRandomSequence(item.Id, read),
            6 => ReadSwitch(item.Id, read),
            7 => new WwiseActorMixer(item.Id, NodeBase(read), Ids(read, read.U32())),
            8 or 20 => ReadBus(item.Type, item.Id, read),
            9 => ReadLayer(item.Id, read),
            10 => ReadSegment(item.Id, read),
            11 => ReadTrack(item.Id, read),
            12 => ReadMusicSwitch(item.Id, read),
            13 => ReadMusicPlaylist(item.Id, read),
            Attenuation => ReadAttenuation(item.Id, read),
            EffectShareSet or EffectCustom => ReadEffect(item.Type, item.Id, read),
            LfoModulator or EnvelopeModulator => ReadModulator(item.Type, item.Id, read),
            _ => null
        };
        if (result is null) return null;
        if (read.At != item.Data.Length)
            throw new InvalidDataException($"{item.Data.Length - read.At} bytes left after its fields");
        return result;
    }

    private static WwiseAction ReadAction(uint id, WwiseReader read)
    {
        ushort type = read.U16(); uint target = read.U32(); bool bus = (read.U8() & 1) != 0;
        var props = Props(read); var ranged = Ranged(read);
        return new(id, type, target, bus, props, ranged, read.Bytes(read.Remaining));
    }

    private static WwiseRandomSequence ReadRandomSequence(uint id, WwiseReader read)
    {
        var node = NodeBase(read);
        ushort loop = read.U16(), loopMin = read.U16(), loopMax = read.U16();
        float time = read.F32(), timeMin = read.F32(), timeMax = read.F32();
        ushort avoid = read.U16(); byte transition = read.U8(), random = read.U8(), mode = read.U8(), flags = read.U8();
        var children = Ids(read, read.U32());
        var playlist = new List<WwisePlaylistEntry>(); int count = read.U16();
        for (int index = 0; index < count; index++) playlist.Add(new(read.U32(), read.S32()));
        return new(id, node, loop, loopMin, loopMax, time, timeMin, timeMax, avoid, transition, random, mode, flags, children, playlist);
    }

    private static WwiseSwitch ReadSwitch(uint id, WwiseReader read)
    {
        var node = NodeBase(read);
        byte groupType = read.U8(); uint group = read.U32(), fallback = read.U32(); bool validation = read.U8() != 0;
        var children = Ids(read, read.U32());
        var groups = new List<(uint, IReadOnlyList<uint>)>(); uint count = read.U32();
        for (uint index = 0; index < count; index++) { uint key = read.U32(); groups.Add((key, Ids(read, read.U32()))); }
        var parameters = new List<WwiseSwitchParameters>(); count = read.U32();
        for (uint index = 0; index < count; index++) parameters.Add(new(read.U32(), read.U8(), read.U8(), read.S32(), read.S32()));
        return new(id, node, groupType, group, fallback, validation, children, groups, parameters);
    }

    /** A bus or auxiliary bus: output, properties, ducks, effects, curves and states. */
    private static WwiseBus ReadBus(byte kind, uint id, WwiseReader read)
    {
        uint output = read.U32(); var props = Props(read);
        // Two flag bytes, the instance limit, the channel configuration and the HDR flags.
        read.Skip(1 + 1 + 2 + 4 + 1);
        read.Skip(4 + 4);
        var ducks = new List<WwiseDuck>(); uint count = read.U32();
        for (uint index = 0; index < count; index++) ducks.Add(new(read.U32(), read.F32(), read.S32(), read.S32(), read.U8(), read.U8()));
        var effects = Effects(read);
        // Mixer plug-in ID and its share-set flag, then the attachment override.
        read.Skip(4 + 1 + 1);
        var rtpcs = Rtpcs(read); var states = States(read);
        return new(kind, id, output, props, effects.Count, ducks, rtpcs, states, effects);
    }

    private static WwiseLayerContainer ReadLayer(uint id, WwiseReader read)
    {
        var node = NodeBase(read); var children = Ids(read, read.U32());
        var layers = new List<WwiseLayer>(); uint count = read.U32();
        for (uint index = 0; index < count; index++)
        {
            uint layer = read.U32(); var rtpcs = Rtpcs(read); uint rtpc = read.U32(); byte type = read.U8();
            var associations = new List<WwiseLayerAssociation>(); uint links = read.U32();
            for (uint link = 0; link < links; link++) associations.Add(new(read.U32(), Points(read, read.U32())));
            layers.Add(new(layer, rtpcs, rtpc, type, associations));
        }
        return new(id, node, children, layers);
    }

    private static WwiseMusicNode MusicNode(WwiseReader read)
    {
        byte flags = read.U8(); var node = NodeBase(read); var children = Ids(read, read.U32());
        var meter = new WwiseMeter(read.F64(), read.F64(), read.F32(), read.U8(), read.U8());
        byte meterFlag = read.U8();
        uint stingers = read.U32();
        // Trigger, segment, sync, cue filter, repeat time and look-ahead count per stinger.
        read.Skip(checked((int)stingers * 24));
        return new(flags, node, children, meter, meterFlag, (int)stingers);
    }

    private static WwiseMusicSegment ReadSegment(uint id, WwiseReader read)
    {
        var music = MusicNode(read); double duration = read.F64();
        var markers = new List<WwiseMarker>(); uint count = read.U32();
        for (uint index = 0; index < count; index++)
        {
            uint marker = read.U32(); double position = read.F64(); int length = checked((int)read.U32());
            markers.Add(new(marker, position, length == 0 ? null : Encoding.UTF8.GetString(read.Bytes(length)).TrimEnd('\0')));
        }
        return new(id, music, duration, markers);
    }

    /** A music track: sources, clips, clip automation, node parameters and track type. */
    private static WwiseMusicTrack ReadTrack(uint id, WwiseReader read)
    {
        byte flags = read.U8();
        var sources = new List<WwiseSource>(); uint count = read.U32();
        for (uint index = 0; index < count; index++) sources.Add(Source(read));
        var clips = new List<WwiseClip>(); count = read.U32();
        for (uint index = 0; index < count; index++)
            clips.Add(new(read.U32(), read.U32(), read.F64(), read.F64(), read.F64(), read.F64()));
        uint subTracks = count > 0 ? read.U32() : 0;
        var automation = new List<WwiseClipAutomation>(); count = read.U32();
        for (uint index = 0; index < count; index++)
        {
            uint clip = read.U32(), type = read.U32();
            automation.Add(new(clip, type, Points(read, read.U32())));
        }
        var node = NodeBase(read); byte trackType = read.U8();
        // Switch tracks: group type and ID, default switch, switch list, then source and destination fades.
        if (trackType == 3)
        {
            read.Skip(1 + 4 + 4); read.Skip(checked((int)read.U32() * 4));
            read.Skip(12 + 4 + 4 + 12);
        }
        return new(id, flags, sources, clips, subTracks, automation, node, trackType, read.S32());
    }

    /** Transition rules: source and destination lists, their fades and an optional bridge. */
    private static IReadOnlyList<WwiseTransitionRule> Rules(WwiseReader read)
    {
        var rules = new List<WwiseTransitionRule>(); uint count = read.U32();
        for (uint index = 0; index < count; index++)
        {
            var sources = Ids(read, read.U32()); var destinations = Ids(read, read.U32());
            var source = new WwiseSourceRule(Fade(read), read.U32(), read.U32(), read.U8() != 0);
            var fade = Fade(read); uint cue = read.U32(), jump = read.U32(); ushort entry = read.U16();
            var destination = new WwiseDestinationRule(fade, cue, jump, entry, read.U8() != 0, read.U8() != 0);
            WwiseBridge? bridge = null;
            if (read.U8() != 0) bridge = new(read.U32(), Fade(read), Fade(read), read.U8() != 0, read.U8() != 0);
            rules.Add(new(sources, destinations, source, destination, bridge));
        }
        return rules;
    }

    private static WwiseFade Fade(WwiseReader read) => new(read.S32(), read.U32(), read.S32());

    /** A music switch: rules, game-sync arguments and the decision tree. */
    private static WwiseMusicSwitch ReadMusicSwitch(uint id, WwiseReader read)
    {
        var music = MusicNode(read); var rules = Rules(read);
        bool continuePlayback = read.U8() != 0; int depth = checked((int)read.U32());
        var groups = Ids(read, (uint)depth); var types = read.Bytes(depth);
        var arguments = groups.Select((group, index) => (group, types[index])).ToArray();
        int size = checked((int)read.U32()); byte mode = read.U8();
        // A decision node is 12 bytes: key, then a child range or an audio node, then weight and probability.
        if (size % 12 != 0) throw new InvalidDataException("decision tree size is not whole nodes");
        var raw = new (uint Key, uint Value, ushort Weight, ushort Probability)[size / 12];
        for (int index = 0; index < raw.Length; index++) raw[index] = (read.U32(), read.U32(), read.U16(), read.U16());
        IReadOnlyList<WwiseDecisionNode> Build(int first, int count, int level)
        {
            if (first < 0 || count < 0 || first + count > raw.Length) throw new InvalidDataException("decision tree leaves its array");
            var nodes = new List<WwiseDecisionNode>();
            for (int index = first; index < first + count; index++)
            {
                var (key, value, weight, probability) = raw[index];
                nodes.Add(level < depth
                    ? new(key, 0, weight, probability, Build((int)(value & 0xFFFF), (int)(value >> 16), level + 1))
                    : new(key, value, weight, probability, []));
            }
            return nodes;
        }
        var tree = raw.Length == 0 ? [] : Build(0, 1, 0);
        return new(id, music, rules, continuePlayback, arguments, mode, tree);
    }

    /** A music playlist: rules and the item tree, stored depth first. */
    private static WwiseMusicPlaylist ReadMusicPlaylist(uint id, WwiseReader read)
    {
        var music = MusicNode(read); var rules = Rules(read);
        uint count = read.U32(); uint used = 0;
        WwiseMusicItem Item(int level)
        {
            if (level > 64 || ++used > count) throw new InvalidDataException("music playlist tree is invalid");
            uint segment = read.U32(), item = read.U32(), children = read.U32(); int kind = read.S32();
            short loop = read.S16(), loopMin = read.S16(), loopMax = read.S16(); uint weight = read.U32();
            ushort avoid = read.U16(); bool weights = read.U8() != 0, shuffle = read.U8() != 0;
            var nodes = new List<WwiseMusicItem>();
            for (uint index = 0; index < children; index++) nodes.Add(Item(level + 1));
            return new(segment, item, kind, loop, loopMin, loopMax, weight, avoid, weights, shuffle, nodes);
        }
        var root = count == 0 ? null : Item(0);
        if (used != count) throw new InvalidDataException("music playlist count differs from its tree");
        return new(id, music, rules, root);
    }

    private static WwiseOther ReadAttenuation(uint id, WwiseReader read)
    {
        // A cone adds inside and outside angles, outside volume and two filter values.
        if ((read.U8() & 1) != 0) read.Skip(5 * 4);
        read.Skip(7);
        int curves = read.U8();
        for (int index = 0; index < curves; index++) { read.Skip(1); Points(read, read.U16()); }
        return new(Attenuation, id, Rtpcs(read));
    }

    private static WwiseOther ReadEffect(byte kind, uint id, WwiseReader read)
    {
        uint plugin = read.U32(); byte[] parameters = read.Bytes(checked((int)read.U32()));
        // Bank media: an index and a source ID each.
        read.Skip(read.U8() * 5);
        var rtpcs = Rtpcs(read);
        // Initial values: a parameter ID and a value each.
        read.Skip(read.U16() * 5);
        return new(kind, id, rtpcs, plugin, parameters);
    }

    private static WwiseOther ReadModulator(byte kind, uint id, WwiseReader read)
    {
        Props(read); Ranged(read);
        return new(kind, id, Rtpcs(read));
    }

    private static WwiseSource Source(WwiseReader read)
    {
        uint plugin = read.U32(); byte stream = read.U8(); uint media = read.U32(), size = read.U32(); byte bits = read.U8();
        // Source plug-ins (type 2) and mixer plug-ins (type 5) carry a parameter block.
        byte[] parameters = (plugin & 0xF) is 2 or 5 ? read.Bytes(checked((int)read.U32())) : [];
        return new(plugin, stream, media, size, bits, parameters);
    }

    /** The shared node parameters of every sound, container and music object. */
    private static WwiseNodeBase NodeBase(WwiseReader read)
    {
        bool overrideEffects = read.U8() != 0; var effects = Effects(read);
        read.Skip(1);
        uint bus = read.U32(), parent = read.U32();
        read.Skip(1);
        var props = Props(read); var ranged = Ranged(read);
        byte positioning = read.U8();
        // Bit 0 overrides the parent's positioning and bit 3 adds 3D parameters.
        if ((positioning & 1) != 0 && (positioning & 8) != 0)
        {
            byte bits = read.U8(); read.Skip(4);
            if ((bits & 3) != 1)
            {
                read.Skip(1 + 4);
                read.Skip(checked((int)read.U32() * 16));
                read.Skip(checked((int)read.U32() * (8 + 12)));
            }
        }
        // Bit 3 adds four auxiliary bus IDs.
        if ((read.U8() & 8) != 0) read.Skip(16);
        read.Skip(6);
        var states = States(read);
        return new(overrideEffects, effects.Count, bus, parent, props, ranged, states, Rtpcs(read), effects, positioning);
    }

    /** An effect count, then bypass bits, then index, effect ID and two flags per effect. */
    private static IReadOnlyList<uint> Effects(WwiseReader read)
    {
        int count = read.U8();
        if (count == 0) return [];
        read.Skip(1);
        var ids = new uint[count];
        for (int index = 0; index < count; index++) { read.Skip(1); ids[index] = read.U32(); read.Skip(2); }
        return ids;
    }

    private static IReadOnlyList<WwiseProp> Props(WwiseReader read)
    {
        int count = read.U8(); byte[] ids = read.Bytes(count);
        return ids.Select(id => new WwiseProp(id, read.U32())).ToArray();
    }

    private static IReadOnlyList<WwiseRangedProp> Ranged(WwiseReader read)
    {
        int count = read.U8(); byte[] ids = read.Bytes(count);
        return ids.Select(id => new WwiseRangedProp(id, read.U32(), read.U32())).ToArray();
    }

    private static IReadOnlyList<WwiseStateGroup> States(WwiseReader read)
    {
        var groups = new List<WwiseStateGroup>(); uint count = read.U32();
        for (uint index = 0; index < count; index++)
        {
            uint group = read.U32(); byte sync = read.U8(); int states = read.U16();
            var list = new List<(uint, uint)>();
            for (int state = 0; state < states; state++) list.Add((read.U32(), read.U32()));
            groups.Add(new(group, sync, list));
        }
        return groups;
    }

    private static IReadOnlyList<WwiseRtpc> Rtpcs(WwiseReader read)
    {
        var result = new List<WwiseRtpc>(); int count = read.U16();
        for (int index = 0; index < count; index++)
            result.Add(new(read.U32(), read.U8(), read.U8(), read.U8(), read.U32(), read.U8(), Points(read, read.U16())));
        return result;
    }

    private static IReadOnlyList<WwiseGraphPoint> Points(WwiseReader read, uint count)
    {
        if (count > read.Remaining / 12) throw new InvalidDataException("graph points leave the object");
        var points = new WwiseGraphPoint[count];
        for (int index = 0; index < count; index++) points[index] = new(read.F32(), read.F32(), read.U32());
        return points;
    }

    private static IReadOnlyList<uint> Ids(WwiseReader read, uint count)
    {
        if (count > read.Remaining / 4) throw new InvalidDataException("ID list leaves the object");
        var ids = new uint[count];
        for (int index = 0; index < count; index++) ids[index] = read.U32();
        return ids;
    }
}

/** A bounds-checked little-endian cursor over one object's data. */
internal sealed class WwiseReader(byte[] data)
{
    public int At { get; private set; }
    public int Remaining => data.Length - At;
    public byte[] Data => data;
    public byte U8() { Need(1); return data[At++]; }
    public ushort U16() { Need(2); var value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(At)); At += 2; return value; }
    public short S16() => unchecked((short)U16());
    public uint U32() { Need(4); var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(At)); At += 4; return value; }
    public int S32() => unchecked((int)U32());
    public float F32() => BitConverter.UInt32BitsToSingle(U32());
    public double F64() { Need(8); var value = BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(At)); At += 8; return value; }
    public byte[] Bytes(int count) { Need(count); var value = data.AsSpan(At, count).ToArray(); At += count; return value; }
    public void Skip(int count) { Need(count); At += count; }
    private void Need(int count)
    {
        if (count < 0 || count > data.Length - At) throw new InvalidDataException("truncated");
    }
}
