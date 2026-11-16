using System.Buffers.Binary;
using System.Text;
using SunriseStudio.Packages;

namespace SunriseStudio.Audio;

public sealed record AuthoredSoundBank(byte[] Bytes, uint BankId, uint EventId, uint RelatedEventId);

/** One sound or container of a version-113 bank: its links, its source and its fire-rate transition curves. */
public sealed record WwiseNode(byte Type, uint Id, uint Parent, IReadOnlyList<uint> Children, uint SourcePlugin,
    uint SourceId, byte TransitionMode, IReadOnlyList<IReadOnlyList<(float X, float Y)>> RateOfFireTransitions)
{
    /** A switch container's group ID; zero for another node. */
    public uint SwitchGroup { get; init; }
    /** A switch container's children, each with the switch value that plays it. */
    public IReadOnlyDictionary<uint, uint> SwitchValues { get; init; } = new Dictionary<uint, uint>();
    /** The attenuation this node sets for itself and its children; zero when it takes its parent's. */
    public uint Attenuation { get; init; }
}

/** One attenuation share set: its curves as distance and value pairs, in stored order. */
public sealed record WwiseAttenuation(uint Id, IReadOnlyList<IReadOnlyList<(float Distance, float Value)>> Curves)
{
    /** The farthest distance any curve reaches. */
    public float MaxDistance => Curves.SelectMany(curve => curve).Select(point => point.Distance).DefaultIfEmpty(0).Max();
}

/**
 * Choices beyond one wave for every sound.
 * @param Variations More waves for the sounds of each random or sequence container; with the main
 * wave they go round the container's sounds in order.
 * @param Switches Switch value ID to the wave every sound of that switch branch plays.
 * @param MaxDistance The farthest distance of the sound's distance curves; each curve is stretched to it.
 */
public sealed record WwiseSoundOptions(IReadOnlyList<PcmWave>? Variations = null,
    IReadOnlyDictionary<uint, PcmWave>? Switches = null, float? MaxDistance = null);

/**
 * A version-113 bank that keeps a template event's whole hierarchy and plays in-memory PCM from every
 * codec leaf. Containers, loops, actions and volumes stay the template's.
 */
public static class WwiseSoundBank
{
    // HIRC object types.
    private const byte Sound = 2, Action = 3, Event = 4, RandomSequence = 5, Switch = 6, ActorMixer = 7, Layer = 9, Attenuation = 14;
    // The runtime decodes plug-in 0x00010001 (PCM) itself; stream type 0 keeps the media in the bank.
    private const uint PcmPlugin = 0x00010001;
    // The rate_of_fire game parameter, in rounds per second.
    private const uint RateOfFire = 0x6379397C;
    // RTPC parameter 0x07 is a container's transition time in seconds; transition mode 5 is trigger rate.
    private const byte TransitionParameter = 0x07, TriggerRate = 5;
    // Action types keep the action in the high byte; 0x04 is Play.
    private const int PlayKind = 0x04;
    // The fire-rate curve covers 1 to 31 rounds per second, the range of every installed curve.
    private const int MaxRoundsPerSecond = 31;
    // In-bank media files start on 16-byte boundaries.
    private const int MediaAlignment = 16;

    /** Wwise names objects by the FNV-1 hash of their lower-case name, as ASCII bytes. */
    public static uint Hash(string name)
    {
        uint hash = StringHash.Empty;
        foreach (byte value in Encoding.ASCII.GetBytes(name.ToLowerInvariant())) hash = unchecked(hash * 0x01000193) ^ value;
        return hash;
    }

    /** Object and media IDs stay below 2^30, like every installed one. */
    private static uint ObjectId(string name) => Hash(name) & 0x3FFFFFFF;

    /**
     * Copies a template bank with new IDs; each codec leaf plays the wave, or the release wave when
     * only the paired event plays it. Trigger-rate containers follow rate_of_fire, one shot per round.
     * @param name Unique lower-case name; the bank and event IDs derive from it.
     * @param options Variations, switch branches and the farthest distance; a choice the template cannot take is refused.
     */
    public static AuthoredSoundBank Build(AudioBank template, uint templateEventId, uint templateRelatedId,
        string name, PcmWave wave, PcmWave? release = null, WwiseSoundOptions? options = null)
    {
        options ??= new();
        RequireLayout(template);
        var objects = template.Objects.ToDictionary(item => item.Id);
        if (!objects.TryGetValue(templateEventId, out var mainEvent) || mainEvent.Type != Event)
            throw new InvalidDataException("The template bank does not hold its event.");
        var layouts = template.Objects.Where(IsNode).ToDictionary(item => item.Id, Parse);
        if (!layouts.Values.Any(layout => layout.Codec))
            throw new InvalidDataException("The template bank has no sound to replace.");

        uint bankId = Hash(name), eventId = bankId;
        var ids = new Dictionary<uint, uint> { [templateEventId] = eventId };
        foreach (var item in template.Objects.Where(item => item.Id != templateEventId))
        {
            string key = item.Id.ToString("X8");
            if (item.Type == Event) ids[item.Id] = Hash(name + "/event/" + key);
            else if (item.Type == Action) ids[item.Id] = ObjectId(name + "/action/" + key);
            else if (layouts.ContainsKey(item.Id)) ids[item.Id] = ObjectId(name + "/node/" + key);
        }
        var layerIds = new Dictionary<uint, uint>();
        foreach (var (id, layout) in layouts)
            foreach (int at in layout.Layers)
            {
                uint layer = U32(objects[id].Data, at);
                layerIds.TryAdd(layer, ObjectId(name + "/layer/" + layer.ToString("X8")));
            }
        var kept = template.Objects.Where(item => !ids.ContainsKey(item.Id)).Select(item => item.Id);
        if (ids.Values.Concat(kept).Distinct().Count() != template.Objects.Count || layerIds.Values.Distinct().Count() != layerIds.Count)
            throw new InvalidDataException("A new sound object ID collides; choose another name.");

        // A leaf only the paired event plays is the release sound, such as an automatic weapon's tail.
        var mainLeaves = Reach(PlayTargets(mainEvent, objects), layouts);
        var releaseLeaves = objects.TryGetValue(templateRelatedId, out var relatedEvent) && relatedEvent.Type == Event
            ? Reach(PlayTargets(relatedEvent, objects), layouts).Except(mainLeaves).ToHashSet() : [];
        bool useRelease = release is not null && releaseLeaves.Any(id => layouts[id].Codec);
        bool Released(uint id) => useRelease && releaseLeaves.Contains(id);
        var chosen = ChooseWaves(template, layouts, wave, options, Released);
        // One media per wave; the main wave keeps the plain media name.
        var waves = new List<PcmWave> { wave };
        foreach (var picked in chosen.Values)
            if (!waves.Any(known => ReferenceEquals(known, picked))) waves.Add(picked);
        var media = waves.Select((item, index) => (Id: ObjectId(index == 0 ? name + "/media" : $"{name}/media/{index}"), Data: PcmWem(item))).ToList();
        int releaseMedia = -1;
        if (useRelease) { releaseMedia = media.Count; media.Add((ObjectId(name + "/release"), PcmWem(release!))); }
        if (media.Select(item => item.Id).Distinct().Count() != media.Count)
            throw new InvalidDataException("A new media ID collides; choose another name.");
        (uint Id, byte[] Data) MediaOf(uint leaf) => Released(leaf) ? media[releaseMedia]
            : media[waves.FindIndex(known => ReferenceEquals(known, chosen.GetValueOrDefault(leaf, wave)))];

        var (attenuationIds, inherited) = options.MaxDistance is { } farthest
            ? PlanAttenuations(template, objects, layouts, name, farthest) : ([], []);
        var hirc = new List<(byte Type, uint Id, byte[] Data)>();
        foreach (var item in template.Objects)
        {
            if (layouts.TryGetValue(item.Id, out var layout))
            {
                var source = MediaOf(item.Id);
                hirc.Add((item.Type, ids[item.Id], Rewrite(item.Data, layout, ids, layerIds, source.Id, source.Data.Length,
                    attenuationIds, inherited.GetValueOrDefault(item.Id))));
            }
            else if (item.Type == Action) hirc.Add((Action, ids[item.Id], RewriteAction(item.Data, objects, ids, template.Id, bankId)));
            else if (item.Type == Event) hirc.Add((Event, ids[item.Id], RewriteEvent(item.Data, ids)));
            else
            {
                hirc.Add((item.Type, item.Id, item.Data));
                // The stretched copy follows its share set, so it is loaded before any node names it.
                if (item.Type == Attenuation && attenuationIds.TryGetValue(item.Id, out uint stretched))
                    hirc.Add((Attenuation, stretched, StretchAttenuation(item, options.MaxDistance!.Value)));
            }
        }
        uint related = templateRelatedId != StringHash.Empty && ids.TryGetValue(templateRelatedId, out uint mappedRelated)
            ? mappedRelated : StringHash.Empty;
        return new(Write(template, bankId, media, hirc), bankId, eventId, related);
    }

    /**
     * Picks the wave of each sound that does not play the main wave.
     * @param released True for a sound that plays the release wave; it takes no other.
     * @return Sound ID to its wave.
     */
    private static Dictionary<uint, PcmWave> ChooseWaves(AudioBank template, IReadOnlyDictionary<uint, Layout> layouts, PcmWave wave,
        WwiseSoundOptions options, Func<uint, bool> released)
    {
        var chosen = new Dictionary<uint, PcmWave>();
        if (options.Switches is { Count: > 0 } switches)
        {
            int matched = 0;
            foreach (var layout in layouts.Values)
                foreach (var (child, value) in layout.SwitchOf)
                {
                    if (!switches.TryGetValue(value, out var branch)) continue;
                    matched++;
                    foreach (uint leaf in Reach([child], layouts).Where(leaf => layouts[leaf].Codec && !released(leaf))) chosen[leaf] = branch;
                }
            if (matched == 0) throw new InvalidDataException("The template bank has no switch branch for the given switch values.");
        }
        if (options.Variations is { Count: > 0 } variations)
        {
            PcmWave[] round = [wave, .. variations];
            bool varied = false;
            foreach (var container in template.Objects.Where(item => item.Type == RandomSequence))
            {
                var leaves = layouts[container.Id].ChildIds
                    .Where(child => layouts.TryGetValue(child, out var layout) && layout.Codec && !chosen.ContainsKey(child) && !released(child))
                    .ToList();
                if (leaves.Count < 2) continue;
                varied = true;
                for (int index = 0; index < leaves.Count; index++) chosen[leaves[index]] = round[index % round.Length];
            }
            if (!varied) throw new InvalidDataException("The template bank has no random or sequence container with two sounds to vary.");
        }
        return chosen;
    }

    /**
     * Finds the attenuations the rewritten nodes use. A node that sets its own keeps its block. A
     * top node that takes a kept actor-mixer's positioning gets a copy of that block, since the
     * mixer is shared with the installed bank and cannot change.
     * @return Old attenuation ID to its stretched copy's ID, and node ID to the block it gains with
     * the attenuation ID's offset in it.
     */
    private static (Dictionary<uint, uint> Ids, Dictionary<uint, (byte[] Block, int Attenuation)> Inherited) PlanAttenuations(
        AudioBank template, IReadOnlyDictionary<uint, AudioBankObject> objects, IReadOnlyDictionary<uint, Layout> layouts,
        string name, float maxDistance)
    {
        if (!(maxDistance > 0) || !float.IsFinite(maxDistance)) throw new InvalidDataException("The farthest distance must be above zero.");
        var shareSets = template.Objects.Where(item => item.Type == Attenuation).Select(item => item.Id).ToHashSet();
        var ids = new Dictionary<uint, uint>();
        var inherited = new Dictionary<uint, (byte[], int)>();
        void Stretch(uint attenuation) => ids.TryAdd(attenuation, ObjectId($"{name}/attenuation/{attenuation:X8}"));
        foreach (var (id, layout) in layouts)
        {
            byte[] data = objects[id].Data;
            if (layout.Attenuation >= 0)
            {
                if (shareSets.Contains(U32(data, layout.Attenuation))) Stretch(U32(data, layout.Attenuation));
                continue;
            }
            // A node under another rewritten node takes that node's positioning.
            uint parent = U32(data, layout.Parent);
            if (layouts.ContainsKey(parent)) continue;
            for (int depth = 0; depth < 64 && objects.TryGetValue(parent, out var mixer) && mixer.Type == ActorMixer; depth++)
            {
                var above = Parse(mixer);
                if (above.Attenuation >= 0)
                {
                    uint attenuation = U32(mixer.Data, above.Attenuation);
                    if (!shareSets.Contains(attenuation)) break;
                    Stretch(attenuation);
                    inherited[id] = (mixer.Data.AsSpan(above.Positioning.Start, above.Positioning.Length).ToArray(),
                        above.Attenuation - above.Positioning.Start);
                    break;
                }
                parent = U32(mixer.Data, above.Parent);
            }
        }
        if (ids.Count == 0) throw new InvalidDataException("The template bank has no distance curve of its own to change.");
        if (ids.Values.Distinct().Count() != ids.Count || ids.Values.Any(objects.ContainsKey))
            throw new InvalidDataException("A new attenuation ID collides; choose another name.");
        return (ids, inherited);
    }

    /** Lists a version-113 bank's sounds and containers in bank order. */
    public static IReadOnlyList<WwiseNode> Nodes(AudioBank bank)
    {
        RequireLayout(bank);
        return bank.Objects.Where(IsNode).Select(item =>
        {
            var layout = Parse(item);
            var curves = layout.Curves.Where(curve => curve.Id == RateOfFire && curve.Parameter == TransitionParameter)
                .Select(curve => (IReadOnlyList<(float, float)>)Enumerable.Range(0, curve.Count)
                    .Select(index => (F32(item.Data, curve.Points + index * 12), F32(item.Data, curve.Points + index * 12 + 4))).ToArray())
                .ToArray();
            return new WwiseNode(item.Type, item.Id, U32(item.Data, layout.Parent), layout.Children.Select(at => U32(item.Data, at)).ToArray(),
                item.Type == Sound ? U32(item.Data, 0) : 0, item.Type == Sound ? U32(item.Data, 5) : 0, layout.TransitionMode, curves)
            {
                SwitchGroup = layout.SwitchGroup, SwitchValues = layout.SwitchOf,
                Attenuation = layout.Attenuation >= 0 ? U32(item.Data, layout.Attenuation) : 0,
            };
        }).ToArray();
    }

    /** Lists a version-113 bank's attenuation share sets. */
    public static IReadOnlyList<WwiseAttenuation> Attenuations(AudioBank bank)
    {
        RequireLayout(bank);
        return bank.Objects.Where(item => item.Type == Attenuation).Select(item => new WwiseAttenuation(item.Id,
            AttenuationCurves(item).Select(curve => (IReadOnlyList<(float, float)>)Enumerable.Range(0, curve.Count)
                .Select(index => (F32(item.Data, curve.Points + index * 12), F32(item.Data, curve.Points + index * 12 + 4))).ToArray())
            .ToArray())).ToArray();
    }

    /**
     * Walks a version-113 attenuation: cone, the curve each use takes, the curves, then RTPC curves.
     * @return Each curve's first point and point count; each point is distance, value and interpolation.
     */
    private static List<(int Points, int Count)> AttenuationCurves(AudioBankObject item)
    {
        var read = new Reader(item.Data);
        // A cone adds inside and outside angles, outside volume, and low-pass and high-pass values.
        if (read.U8() != 0) read.Skip(5 * 4);
        read.Skip(7);
        int count = read.U8();
        var curves = new List<(int, int)>(count);
        for (int index = 0; index < count; index++)
        {
            read.Skip(1);
            int points = read.U16();
            curves.Add((read.At, points));
            read.Skip(points * 12);
        }
        Rtpc(read, []);
        if (read.At != item.Data.Length) throw new InvalidDataException($"Attenuation {item.Id} has an unknown layout.");
        return curves;
    }

    /** The attenuation with every curve's distances stretched so the farthest point sits at `maxDistance`. */
    private static byte[] StretchAttenuation(AudioBankObject item, float maxDistance)
    {
        byte[] data = item.Data.ToArray();
        var curves = AttenuationCurves(item);
        float farthest = curves.SelectMany(curve => Enumerable.Range(0, curve.Count).Select(index => F32(data, curve.Points + index * 12)))
            .DefaultIfEmpty(0).Max();
        if (!(farthest > 0)) throw new InvalidDataException($"Attenuation {item.Id} has no distance to stretch.");
        foreach (var (points, count) in curves)
            for (int index = 0; index < count; index++)
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(points + index * 12), F32(data, points + index * 12) * maxDistance / farthest);
        return data;
    }

    private static bool IsNode(AudioBankObject item) => item.Type is Sound or RandomSequence or Switch or Layer;

    private static void RequireLayout(AudioBank bank)
    {
        if (bank.Version != 113) throw new NotSupportedException("Sound authoring needs a version-113 template bank.");
        byte[] header = Chunk(bank.Bytes, "BKHD") ?? throw new InvalidDataException("The template bank has no header.");
        // A bank with feedback data adds a field to every node.
        if (header.Length >= 16 && U32(header, 12) != 0) throw new NotSupportedException("Sound authoring does not read feedback banks.");
    }

    /** Where a node's IDs, source and curves sit; offsets are into the node's data. */
    private sealed record Layout(bool Codec, int SourceLength, int Parent, List<int> Children, List<uint> ChildIds,
        List<int> Links, List<int> Layers, int Transition, byte TransitionMode, List<Curve> Curves)
    {
        /** The positioning byte and the block behind it. */
        public (int Start, int Length) Positioning { get; init; }
        /** Where the node's own attenuation ID sits, or -1 when it takes its parent's positioning. */
        public int Attenuation { get; init; } = -1;
        public uint SwitchGroup { get; init; }
        /** A switch container's child IDs, each with its switch value. */
        public Dictionary<uint, uint> SwitchOf { get; init; } = [];
    }

    /** One RTPC curve: its point count sits at Points - 2. */
    private sealed record Curve(uint Id, byte Parameter, int Points, int Count);

    /** Walks a version-113 sound, random or sequence, switch or layer container field by field. */
    private static Layout Parse(AudioBankObject node)
    {
        var read = new Reader(node.Data);
        var children = new List<int>(); var links = new List<int>(); var layers = new List<int>(); var curves = new List<Curve>();
        bool codec = false; int sourceLength = 0, transition = -1; byte transitionMode = 0;
        if (node.Type == Sound)
        {
            uint plugin = read.U32(); read.Skip(1 + 4 + 4 + 1);
            // Source plug-ins (type 2) and mixer plug-ins (type 5) carry a parameter block.
            if ((plugin & 0xF) is 2 or 5) read.Skip(checked((int)read.U32()));
            codec = (plugin & 0xF) == 1;
            sourceLength = read.At;
        }
        int parent = NodeBase(read, curves, out var positioning, out int attenuation);
        uint switchGroup = 0; var switchOf = new Dictionary<uint, uint>();
        if (node.Type == ActorMixer) ChildList(read, children);
        else if (node.Type == RandomSequence)
        {
            read.Skip(6);
            transition = read.At;
            read.Skip(12 + 2);
            transitionMode = read.U8();
            read.Skip(3);
            ChildList(read, children);
            int items = read.U16();
            for (int index = 0; index < items; index++) { links.Add(read.At); read.Skip(8); }
        }
        else if (node.Type == Switch)
        {
            // Group type, group ID, default switch value, continuous validation.
            read.Skip(1);
            switchGroup = read.U32();
            read.Skip(4 + 1);
            ChildList(read, children);
            uint groups = read.U32();
            for (int group = 0; group < groups; group++)
            {
                uint value = read.U32();
                uint count = read.U32();
                for (int index = 0; index < count; index++)
                {
                    links.Add(read.At);
                    switchOf.TryAdd(read.U32(), value);
                }
            }
            uint parameters = read.U32();
            for (int index = 0; index < parameters; index++) { links.Add(read.At); read.Skip(4 + 1 + 1 + 4 + 4); }
        }
        else if (node.Type == Layer)
        {
            ChildList(read, children);
            uint count = read.U32();
            for (int index = 0; index < count; index++)
            {
                layers.Add(read.At); read.Skip(4);
                Rtpc(read, []);
                read.Skip(4 + 1);
                uint associations = read.U32();
                for (int item = 0; item < associations; item++)
                {
                    links.Add(read.At); read.Skip(4);
                    read.Skip(checked((int)read.U32() * 12));
                }
            }
        }
        if (read.At != node.Data.Length) throw new InvalidDataException($"Template node {node.Id} has an unknown layout.");
        return new(codec, sourceLength, parent, children, children.Select(at => U32(node.Data, at)).ToList(), links, layers,
            transition, transitionMode, curves)
        {
            Positioning = positioning, Attenuation = attenuation, SwitchGroup = switchGroup, SwitchOf = switchOf,
        };
    }

    /**
     * Reads the shared node parameters.
     * @param positioning Receives where the positioning byte and its block sit.
     * @param attenuation Receives where the node's own attenuation ID sits, or -1.
     * @return The parent ID's offset.
     */
    private static int NodeBase(Reader read, List<Curve> curves, out (int Start, int Length) positioning, out int attenuation)
    {
        attenuation = -1;
        read.Skip(1);
        int effects = read.U8();
        // Bypass bits, then index, share-set ID and two flags per effect.
        if (effects > 0) read.Skip(1 + effects * 7);
        read.Skip(1 + 4);
        int parent = read.At;
        read.Skip(4 + 1);
        int props = read.U8(); read.Skip(props * 5);
        int ranged = read.U8(); read.Skip(ranged * 9);
        int positioningStart = read.At;
        byte flags = read.U8();
        // Bit 0 overrides the parent's positioning and bit 3 adds 3D parameters.
        if ((flags & 1) != 0 && (flags & 8) != 0)
        {
            byte bits = read.U8();
            attenuation = read.At;
            read.Skip(4);
            if ((bits & 3) != 1)
            {
                read.Skip(1 + 4);
                read.Skip(checked((int)read.U32() * 16));
                int items = checked((int)read.U32());
                read.Skip(checked(items * (8 + 12)));
            }
        }
        positioning = (positioningStart, read.At - positioningStart);
        // Bit 3 adds four auxiliary bus IDs.
        if ((read.U8() & 8) != 0) read.Skip(16);
        read.Skip(6);
        uint groups = read.U32();
        for (int group = 0; group < groups; group++) { read.Skip(4 + 1); read.Skip(read.U16() * 8); }
        Rtpc(read, curves);
        return parent;
    }

    private static void Rtpc(Reader read, List<Curve> curves)
    {
        int count = read.U16();
        for (int index = 0; index < count; index++)
        {
            uint id = read.U32(); read.Skip(2);
            byte parameter = read.U8(); read.Skip(4 + 1);
            int points = read.U16();
            curves.Add(new(id, parameter, read.At, points));
            read.Skip(points * 12);
        }
    }

    private static void ChildList(Reader read, List<int> children)
    {
        uint count = read.U32();
        for (int index = 0; index < count; index++) { children.Add(read.At); read.Skip(4); }
    }

    /** Renames a node's links, points a codec leaf at the authored media and sets fire-rate curves. */
    private static byte[] Rewrite(byte[] original, Layout layout, IReadOnlyDictionary<uint, uint> ids,
        IReadOnlyDictionary<uint, uint> layerIds, uint mediaId, int mediaLength,
        IReadOnlyDictionary<uint, uint> attenuationIds, (byte[] Block, int Attenuation) inherited)
    {
        byte[] data = original.ToArray();
        foreach (int at in layout.Children.Concat(layout.Links).Append(layout.Parent))
            Put(data, at, ids.GetValueOrDefault(U32(data, at), U32(data, at)));
        foreach (int at in layout.Layers) Put(data, at, layerIds[U32(data, at)]);
        if (layout.Attenuation >= 0 && attenuationIds.TryGetValue(U32(data, layout.Attenuation), out uint own))
            Put(data, layout.Attenuation, own);
        var splices = new List<(int At, int Length, byte[] Bytes)>();
        if (inherited.Block is { } block)
        {
            // The node's one positioning byte becomes the mixer's whole block, with the stretched attenuation.
            byte[] positioning = block.ToArray();
            Put(positioning, inherited.Attenuation, attenuationIds[U32(positioning, inherited.Attenuation)]);
            splices.Add((layout.Positioning.Start, layout.Positioning.Length, positioning));
        }
        if (layout.TransitionMode == TriggerRate)
        {
            var rates = layout.Curves.Where(curve => curve.Id == RateOfFire && curve.Parameter == TransitionParameter).ToList();
            // The curve then carries the whole period, so the fixed transition time is cleared.
            if (rates.Count > 0) BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(layout.Transition), 0);
            foreach (var curve in rates) splices.Add((curve.Points - 2, 2 + curve.Count * 12, ShotPeriodCurve()));
        }
        if (layout.Codec)
        {
            byte[] source = new byte[14];
            Put(source, 0, PcmPlugin);
            Put(source, 5, mediaId);
            Put(source, 9, (uint)mediaLength);
            splices.Add((0, layout.SourceLength, source));
        }
        foreach (var (at, length, bytes) in splices.OrderByDescending(splice => splice.At))
            data = [.. data.AsSpan(0, at), .. bytes, .. data.AsSpan(at + length)];
        return data;
    }

    /** Transition time 1/x seconds at x rounds per second, so a trigger-rate container plays once per round. */
    private static byte[] ShotPeriodCurve()
    {
        byte[] curve = new byte[2 + MaxRoundsPerSecond * 12];
        BinaryPrimitives.WriteUInt16LittleEndian(curve, MaxRoundsPerSecond);
        for (int rate = 1; rate <= MaxRoundsPerSecond; rate++)
        {
            int at = 2 + (rate - 1) * 12;
            BinaryPrimitives.WriteSingleLittleEndian(curve.AsSpan(at), rate);
            BinaryPrimitives.WriteSingleLittleEndian(curve.AsSpan(at + 4), 1f / rate);
            // Interpolation 4 is linear.
            Put(curve, at + 8, 4);
        }
        return curve;
    }

    private static byte[] RewriteAction(byte[] original, IReadOnlyDictionary<uint, AudioBankObject> objects,
        IReadOnlyDictionary<uint, uint> ids, uint templateBank, uint bankId)
    {
        byte[] data = original.ToArray();
        if (data.Length < 6) throw new InvalidDataException("A template action is truncated.");
        uint target = U32(data, 2);
        if (!objects.ContainsKey(target)) throw new InvalidDataException("A template action targets an object outside its bank.");
        Put(data, 2, ids.GetValueOrDefault(target, target));
        // A play action ends with the bank that holds its target.
        if (data[1] == PlayKind && U32(data, data.Length - 4) == templateBank) Put(data, data.Length - 4, bankId);
        return data;
    }

    private static byte[] RewriteEvent(byte[] original, IReadOnlyDictionary<uint, uint> ids)
    {
        byte[] data = original.ToArray();
        foreach (int at in ActionOffsets(data))
        {
            if (!ids.TryGetValue(U32(data, at), out uint mapped)) throw new InvalidDataException("A template event names an action outside its bank.");
            Put(data, at, mapped);
        }
        return data;
    }

    private static IEnumerable<int> ActionOffsets(byte[] data)
    {
        uint count = data.Length >= 4 ? U32(data, 0) : uint.MaxValue;
        if (count > 256 || data.Length != 4 + count * 4) throw new InvalidDataException("A template event has an unexpected action list.");
        return Enumerable.Range(0, (int)count).Select(index => 4 + index * 4);
    }

    private static IEnumerable<uint> PlayTargets(AudioBankObject item, IReadOnlyDictionary<uint, AudioBankObject> objects) =>
        ActionOffsets(item.Data).Select(at => objects.GetValueOrDefault(U32(item.Data, at)))
            .Where(action => action is { Type: Action } && action.Data.Length >= 6 && action.Data[1] == PlayKind)
            .Select(action => U32(action!.Data, 2));

    private static HashSet<uint> Reach(IEnumerable<uint> roots, IReadOnlyDictionary<uint, Layout> layouts)
    {
        var seen = new HashSet<uint>();
        var queue = new Queue<uint>(roots);
        while (queue.TryDequeue(out uint id))
            if (layouts.TryGetValue(id, out var layout) && seen.Add(id))
                foreach (uint child in layout.ChildIds) queue.Enqueue(child);
        return seen;
    }

    /** Wwise PCM media: an extensible RIFF header the runtime's parser accepts, then 16-bit samples. */
    public static byte[] PcmWem(PcmWave wave)
    {
        if (wave.BitsPerSample != 16 || wave.Channels is < 1 or > 2 || wave.Samples.Length % wave.BlockAlign != 0)
            throw new InvalidDataException("Weapon sounds need mono or stereo 16-bit PCM.");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(4 + 8 + 24 + 8 + wave.Samples.Length);
        writer.Write("WAVE"u8); writer.Write("fmt "u8); writer.Write(24);
        writer.Write((ushort)0xFFFE); writer.Write(wave.Channels); writer.Write(wave.SampleRate);
        writer.Write(wave.SampleRate * wave.BlockAlign); writer.Write((ushort)wave.BlockAlign); writer.Write((ushort)16);
        writer.Write((ushort)6); writer.Write((ushort)16);
        // Speaker mask: front centre for mono, front left and right for stereo.
        writer.Write(wave.Channels == 1 ? 4u : 3u);
        writer.Write("data"u8); writer.Write(wave.Samples.Length); writer.Write(wave.Samples);
        return stream.ToArray();
    }

    /** Writes BKHD, DIDX, DATA and HIRC, then reads the bank back. */
    private static byte[] Write(AudioBank template, uint bankId, IReadOnlyList<(uint Id, byte[] Data)> media,
        IReadOnlyList<(byte Type, uint Id, byte[] Data)> objects)
    {
        byte[] header = (Chunk(template.Bytes, "BKHD") ?? throw new InvalidDataException("The template bank has no header.")).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), bankId);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        void Put(string kind, byte[] data) { writer.Write(Encoding.ASCII.GetBytes(kind)); writer.Write(data.Length); writer.Write(data); }
        Put("BKHD", header);
        byte[] index = new byte[media.Count * 12];
        using var payload = new MemoryStream();
        for (int item = 0; item < media.Count; item++)
        {
            while (payload.Length % MediaAlignment != 0) payload.WriteByte(0);
            BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(item * 12), media[item].Id);
            BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(item * 12 + 4), (uint)payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(item * 12 + 8), (uint)media[item].Data.Length);
            payload.Write(media[item].Data);
        }
        Put("DIDX", index);
        Put("DATA", payload.ToArray());
        using var table = new MemoryStream();
        using (var rows = new BinaryWriter(table, Encoding.ASCII, true))
        {
            rows.Write(objects.Count);
            foreach (var (type, id, data) in objects) { rows.Write(type); rows.Write(data.Length + 4); rows.Write(id); rows.Write(data); }
        }
        Put("HIRC", table.ToArray());
        byte[] bytes = stream.ToArray();
        var check = Read(0, bytes);
        if (check.Id != bankId || check.EmbeddedMedia.Count != media.Count || check.Objects.Count != objects.Count)
            throw new InvalidDataException("The authored bank did not read back.");
        return bytes;
    }

    /** HIRC record lengths and DIDX slices must fit their own chunks, not just the bank file. */
    public static AudioBank Read(uint tag, byte[] bytes)
    {
        var objects = new List<AudioBankObject>(); var media = new List<AudioMedia>();
        uint version = 0, id = 0; byte[]? index = null, data = null; bool headerSeen = false;
        var chunks = new HashSet<string>(StringComparer.Ordinal);
        for (int p = 0; p < bytes.Length;)
        {
            if (p > bytes.Length - 8) throw new InvalidDataException("Truncated Wwise chunk header.");
            uint length = U32(bytes, p + 4);
            if (length > bytes.Length - p - 8) throw new InvalidDataException("Wwise chunk leaves its bank.");
            int start = p + 8, end = checked(start + (int)length);
            string kind = Encoding.ASCII.GetString(bytes, p, 4);
            if (kind is "BKHD" or "DIDX" or "DATA" or "HIRC" && !chunks.Add(kind))
                throw new InvalidDataException("Duplicate Wwise data chunk.");
            if (kind == "BKHD")
            {
                if (headerSeen || length < 8) throw new InvalidDataException("Wwise bank header is invalid.");
                version = U32(bytes, start); id = U32(bytes, start + 4); headerSeen = true;
            }
            else if (kind == "DIDX") index = bytes.AsSpan(start, (int)length).ToArray();
            else if (kind == "DATA") data = bytes.AsSpan(start, (int)length).ToArray();
            else if (kind == "HIRC")
            {
                if (length < 4) throw new InvalidDataException("Truncated Wwise object table.");
                uint count = U32(bytes, start); int cursor = start + 4;
                if (count > 1000000) throw new InvalidDataException("Wwise object table is too large.");
                for (int i = 0; i < count; i++)
                {
                    if (cursor > end - 9) throw new InvalidDataException("Truncated Wwise object.");
                    uint size = U32(bytes, cursor + 1);
                    if (size < 4 || size > end - cursor - 5) throw new InvalidDataException("Wwise object leaves its table.");
                    objects.Add(new(bytes[cursor], U32(bytes, cursor + 5), bytes.AsSpan(cursor + 9, checked((int)size - 4)).ToArray()));
                    cursor += checked((int)size + 5);
                }
                if (cursor != end) throw new InvalidDataException("Wwise object count differs from its table.");
            }
            p = end;
        }
        if (!headerSeen) throw new InvalidDataException("Audio entry is not a Wwise bank.");
        if (index is not null)
        {
            if (data is null || index.Length % 12 != 0) throw new InvalidDataException("Wwise embedded media index is invalid.");
            for (int p = 0; p < index.Length; p += 12)
            {
                uint mediaId = U32(index, p), offset = U32(index, p + 4), size = U32(index, p + 8);
                if (offset > data.Length || size > data.Length - offset) throw new InvalidDataException("Embedded media leaves DATA.");
                media.Add(new(mediaId, null, $"Media {mediaId}", (int)size, data.AsSpan((int)offset, (int)size).ToArray()));
            }
        }
        return new(tag, id, version, bytes, objects, media);
    }

    private static byte[]? Chunk(byte[] bank, string kind)
    {
        for (int at = 0; at + 8 <= bank.Length;)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bank.AsSpan(at + 4)));
            if (Encoding.ASCII.GetString(bank, at, 4) == kind) return bank.AsSpan(at + 8, length).ToArray();
            at = checked(at + 8 + length);
        }
        return null;
    }

    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static float F32(byte[] bytes, int at) => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at));
    private static void Put(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);

    /** A bounds-checked cursor over one node's data. */
    private sealed class Reader(byte[] data)
    {
        public int At { get; private set; }
        public byte U8() { Need(1); return data[At++]; }
        public ushort U16() { Need(2); ushort value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(At)); At += 2; return value; }
        public uint U32() { Need(4); uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(At)); At += 4; return value; }
        public void Skip(int count) { Need(count); At += count; }
        private void Need(int count)
        {
            if (count < 0 || count > data.Length - At) throw new InvalidDataException("A template node is truncated.");
        }
    }
}
