using System.Buffers.Binary;

namespace SunriseStudio.Audio;

/** Shared node parameters kept as bytes around the parent ID, the one link they hold. */
public sealed record MusicNodeBase(byte[] Before, uint Parent, byte[] After);

/** One stinger: a trigger plays a segment at a sync point. */
public sealed record MusicStinger(uint Trigger, uint Segment, uint Sync, uint CueFilter, int DontRepeat, uint LookAhead);

/** The part every music segment, switch and playlist opens with. Meter holds the grid, tempo, signature and meter flag. */
public sealed record MusicNode(byte Flags, MusicNodeBase Node, IReadOnlyList<uint> Children, byte[] Meter,
    IReadOnlyList<MusicStinger> Stingers);

public sealed record MusicFade(int Time, uint Curve, int Offset);

/** A bridge segment and its two fades; Rest holds the fades and the two flags as stored. */
public sealed record MusicBridge(uint Segment, byte[] Rest);

/** One transition rule; uint.MaxValue in a list means any object. Flags are bytes: zero is off, installed banks also store 0xFF. */
public sealed record MusicRule(IReadOnlyList<uint> Sources, IReadOnlyList<uint> Destinations,
    MusicFade SourceFade, uint Sync, uint SourceCueFilter, byte PostExit,
    MusicFade DestinationFade, uint DestinationCueFilter, uint JumpTo, ushort Entry, byte PreEntry, byte MatchSourceCue,
    MusicBridge? Bridge);

/** A codec source; Parameters is the plug-in block of a source or mixer plug-in. */
public sealed record MusicSource(uint Plugin, byte StreamType, uint MediaId, uint MediaSize, byte Bits, byte[]? Parameters);

/** Times are milliseconds. */
public sealed record MusicClip(uint Track, uint Source, double PlayAt, double BeginTrim, double EndTrim, double SourceDuration);

/** Points holds the point count and the 12-byte points as stored. */
public sealed record MusicClipAutomation(uint Clip, uint Type, byte[] Points);

public sealed record MusicMarker(uint Id, double Position, byte[] Name);

/** A decision node: Value is an audio node at the leaf level and a child range above it. */
public sealed record MusicDecision(uint Key, uint Value, ushort Weight, ushort Probability);

/** A playlist item as stored, depth first; ChildCount items follow as its children. */
public sealed record MusicItem(uint Segment, uint Id, uint ChildCount, int Kind, short Loop, short LoopMinimum,
    short LoopMaximum, uint Weight, ushort AvoidRepeat, byte UsesWeights, byte Shuffle);

public abstract record MusicObject(byte Type, uint Id);

public sealed record MusicSegmentObject(uint Id, MusicNode Music, double Duration, IReadOnlyList<MusicMarker> Markers)
    : MusicObject(MusicHirc.Segment, Id);

/** SwitchData is the switch-track block (track type 3) as stored, else empty. */
public sealed record MusicTrackObject(uint Id, byte Flags, IReadOnlyList<MusicSource> Sources, IReadOnlyList<MusicClip> Clips,
    uint SubTracks, IReadOnlyList<MusicClipAutomation> Automation, MusicNodeBase Node, byte TrackType, byte[] SwitchData,
    int LookAhead) : MusicObject(MusicHirc.Track, Id);

public sealed record MusicSwitchObject(uint Id, MusicNode Music, IReadOnlyList<MusicRule> Rules, byte ContinuePlayback,
    IReadOnlyList<uint> Groups, byte[] GroupTypes, byte Mode, IReadOnlyList<MusicDecision> Tree)
    : MusicObject(MusicHirc.Switch, Id);

public sealed record MusicPlaylistObject(uint Id, MusicNode Music, IReadOnlyList<MusicRule> Rules, IReadOnlyList<MusicItem> Items)
    : MusicObject(MusicHirc.Playlist, Id);

/** Reads and writes version-113 HIRC music objects (types 10 to 13) field by field; a write gives the read bytes back. */
public static class MusicHirc
{
    /** HIRC object types. */
    public const byte Segment = 10, Track = 11, Switch = 12, Playlist = 13;
    // A music node's meter: grid period and offset, tempo, beats per bar, beat value, then the meter flag.
    private const int MeterSize = 8 + 8 + 4 + 1 + 1 + 1;
    // A decision node: key, value, weight, probability.
    private const int DecisionSize = 12;
    // Track type 3 is a switch track with its own block.
    private const byte SwitchTrack = 3;

    public static bool IsMusic(byte type) => type is Segment or Track or Switch or Playlist;

    /** Parses one music object; a layout that does not fill the record exactly throws. */
    public static MusicObject Read(AudioBankObject item)
    {
        var read = new WwiseReader(item.Data);
        MusicObject result = item.Type switch
        {
            Segment => ReadSegment(item.Id, read),
            Track => ReadTrack(item.Id, read),
            Switch => ReadSwitch(item.Id, read),
            Playlist => ReadPlaylist(item.Id, read),
            _ => throw new NotSupportedException($"HIRC type {item.Type} is not a music object."),
        };
        if (read.Remaining != 0) throw new InvalidDataException($"Music object {item.Id} has {read.Remaining} bytes after its fields.");
        return result;
    }

    /** The object's record data, without the type, length and ID header. */
    public static byte[] Write(MusicObject value)
    {
        var write = new Writer();
        switch (value)
        {
            case MusicSegmentObject segment:
                MusicNodeOut(write, segment.Music);
                write.F64(segment.Duration);
                write.U32((uint)segment.Markers.Count);
                foreach (var marker in segment.Markers) { write.U32(marker.Id); write.F64(marker.Position); write.U32((uint)marker.Name.Length); write.Bytes(marker.Name); }
                break;
            case MusicTrackObject track: TrackOut(write, track); break;
            case MusicSwitchObject item:
                MusicNodeOut(write, item.Music);
                RulesOut(write, item.Rules);
                write.U8(item.ContinuePlayback);
                write.U32((uint)item.Groups.Count);
                foreach (uint group in item.Groups) write.U32(group);
                write.Bytes(item.GroupTypes);
                write.U32((uint)(item.Tree.Count * DecisionSize));
                write.U8(item.Mode);
                foreach (var node in item.Tree) { write.U32(node.Key); write.U32(node.Value); write.U16(node.Weight); write.U16(node.Probability); }
                break;
            case MusicPlaylistObject playlist:
                MusicNodeOut(write, playlist.Music);
                RulesOut(write, playlist.Rules);
                write.U32((uint)playlist.Items.Count);
                foreach (var entry in playlist.Items)
                {
                    write.U32(entry.Segment); write.U32(entry.Id); write.U32(entry.ChildCount); write.S32(entry.Kind);
                    write.S16(entry.Loop); write.S16(entry.LoopMinimum); write.S16(entry.LoopMaximum); write.U32(entry.Weight);
                    write.U16(entry.AvoidRepeat); write.U8(entry.UsesWeights); write.U8(entry.Shuffle);
                }
                break;
            default: throw new NotSupportedException("Unknown music object.");
        }
        return write.ToArray();
    }

    /**
     * Renames every object link in a music object: its ID, parent, children, stinger segments, rule
     * lists, jump targets, bridges, decision leaves and playlist items. An ID the map does not hold stays.
     * @param map Old ID to new ID.
     */
    public static MusicObject Remap(MusicObject value, Func<uint, uint> map)
    {
        IReadOnlyList<uint> List(IReadOnlyList<uint> ids) => ids.Select(map).ToArray();
        MusicNodeBase NodeOf(MusicNodeBase node) => node with { Parent = map(node.Parent) };
        MusicNode MusicOf(MusicNode music) => music with
        {
            Node = NodeOf(music.Node), Children = List(music.Children),
            Stingers = music.Stingers.Select(stinger => stinger with { Segment = map(stinger.Segment) }).ToArray(),
        };
        IReadOnlyList<MusicRule> RulesOf(IReadOnlyList<MusicRule> rules) => rules.Select(rule => rule with
        {
            Sources = List(rule.Sources), Destinations = List(rule.Destinations), JumpTo = map(rule.JumpTo),
            Bridge = rule.Bridge is { } bridge ? bridge with { Segment = map(bridge.Segment) } : null,
        }).ToArray();
        return value switch
        {
            MusicSegmentObject segment => segment with { Id = map(segment.Id), Music = MusicOf(segment.Music) },
            MusicTrackObject track => track with { Id = map(track.Id), Node = NodeOf(track.Node) },
            MusicSwitchObject item => item with
            {
                Id = map(item.Id), Music = MusicOf(item.Music), Rules = RulesOf(item.Rules),
                Tree = LeafIndices(item).Aggregate(item.Tree.ToArray(), (tree, leaf) =>
                {
                    tree[leaf] = tree[leaf] with { Value = map(tree[leaf].Value) };
                    return tree;
                }),
            },
            MusicPlaylistObject playlist => playlist with
            {
                Id = map(playlist.Id), Music = MusicOf(playlist.Music), Rules = RulesOf(playlist.Rules),
                Items = playlist.Items.Select(entry => entry with { Segment = map(entry.Segment), Id = map(entry.Id) }).ToArray(),
            },
            _ => throw new NotSupportedException("Unknown music object."),
        };
    }

    /**
     * The decision nodes whose value is an audio node: the nodes one level below the last game-sync argument.
     * @return Indices into the switch's tree; empty for an empty tree.
     */
    public static IReadOnlyList<int> LeafIndices(MusicSwitchObject item)
    {
        var leaves = new List<int>();
        if (item.Tree.Count == 0) return leaves;
        void Walk(int first, int count, int level)
        {
            if (first < 0 || count < 0 || first + count > item.Tree.Count || level > item.Groups.Count)
                throw new InvalidDataException("A decision tree leaves its array.");
            for (int index = first; index < first + count; index++)
            {
                if (level == item.Groups.Count) leaves.Add(index);
                else Walk((int)(item.Tree[index].Value & 0xFFFF), (int)(item.Tree[index].Value >> 16), level + 1);
            }
        }
        Walk(0, 1, 0);
        return leaves;
    }

    /**
     * The IDs a music object names as other objects: parent, children, stinger segments, rule
     * targets, bridges, decision leaves and playlist segments. Playlist item IDs and the tree of a
     * switch with no game-sync argument are not object links.
     */
    public static IEnumerable<uint> Links(MusicObject value)
    {
        var links = new List<uint>();
        void Music(MusicNode music)
        {
            links.Add(music.Node.Parent); links.AddRange(music.Children);
            links.AddRange(music.Stingers.Select(stinger => stinger.Segment));
        }
        void Rules(IReadOnlyList<MusicRule> rules)
        {
            foreach (var rule in rules)
            {
                links.AddRange(rule.Sources); links.AddRange(rule.Destinations); links.Add(rule.JumpTo);
                if (rule.Bridge is { } bridge) links.Add(bridge.Segment);
            }
        }
        switch (value)
        {
            case MusicSegmentObject segment: Music(segment.Music); break;
            case MusicTrackObject track: links.Add(track.Node.Parent); break;
            case MusicSwitchObject item:
                Music(item.Music); Rules(item.Rules);
                if (item.Groups.Count > 0) links.AddRange(LeafIndices(item).Select(index => item.Tree[index].Value));
                break;
            case MusicPlaylistObject playlist:
                Music(playlist.Music); Rules(playlist.Rules);
                links.AddRange(playlist.Items.Select(entry => entry.Segment));
                break;
        }
        return links;
    }
    /** A segment: music node, duration, then markers with their position and name bytes. */
    private static MusicSegmentObject ReadSegment(uint id, WwiseReader read)
    {
        var music = MusicNodeIn(read);
        double duration = read.F64();
        uint count = read.U32();
        var markers = new List<MusicMarker>();
        for (uint index = 0; index < count; index++)
        {
            uint marker = read.U32(); double position = read.F64();
            markers.Add(new(marker, position, read.Bytes(checked((int)read.U32()))));
        }
        return new(id, music, duration, markers);
    }

    /** A track: flags, sources, clips, clip automation, node parameters, track type, switch block and look-ahead. */
    private static MusicTrackObject ReadTrack(uint id, WwiseReader read)
    {
        byte flags = read.U8();
        var sources = new List<MusicSource>(); uint count = read.U32();
        for (uint index = 0; index < count; index++)
        {
            uint plugin = read.U32(); byte stream = read.U8(); uint media = read.U32(), size = read.U32(); byte bits = read.U8();
            // Source plug-ins (type 2) and mixer plug-ins (type 5) carry a parameter block.
            byte[]? parameters = (plugin & 0xF) is 2 or 5 ? read.Bytes(checked((int)read.U32())) : null;
            sources.Add(new(plugin, stream, media, size, bits, parameters));
        }
        var clips = new List<MusicClip>(); count = read.U32();
        for (uint index = 0; index < count; index++) clips.Add(new(read.U32(), read.U32(), read.F64(), read.F64(), read.F64(), read.F64()));
        // The sub-track count is stored only when there are clips.
        uint subTracks = count > 0 ? read.U32() : 0;
        var automation = new List<MusicClipAutomation>(); count = read.U32();
        for (uint index = 0; index < count; index++)
        {
            uint clip = read.U32(), type = read.U32(); int points = checked((int)read.U32());
            if (points > read.Remaining / 12) throw new InvalidDataException("Clip automation leaves the object.");
            byte[] bytes = new byte[4 + points * 12];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)points);
            read.Bytes(points * 12).CopyTo(bytes, 4);
            automation.Add(new(clip, type, bytes));
        }
        var node = NodeBaseIn(read);
        byte trackType = read.U8();
        byte[] switchData = [];
        if (trackType == SwitchTrack)
        {
            int start = read.At;
            // Group type and ID, default switch, switch list, then the source and destination fades.
            read.Skip(1 + 4 + 4); read.Skip(checked((int)read.U32() * 4)); read.Skip(12 + 4 + 4 + 12);
            switchData = read.Data[start..read.At];
        }
        return new(id, flags, sources, clips, subTracks, automation, node, trackType, switchData, read.S32());
    }

    /** Writes a track in ReadTrack's order; the sub-track count only when there are clips. */
    private static void TrackOut(Writer write, MusicTrackObject track)
    {
        write.U8(track.Flags);
        write.U32((uint)track.Sources.Count);
        foreach (var source in track.Sources)
        {
            write.U32(source.Plugin); write.U8(source.StreamType); write.U32(source.MediaId); write.U32(source.MediaSize); write.U8(source.Bits);
            if ((source.Plugin & 0xF) is 2 or 5)
            {
                byte[] parameters = source.Parameters ?? [];
                write.U32((uint)parameters.Length); write.Bytes(parameters);
            }
        }
        write.U32((uint)track.Clips.Count);
        foreach (var clip in track.Clips)
        {
            write.U32(clip.Track); write.U32(clip.Source); write.F64(clip.PlayAt); write.F64(clip.BeginTrim);
            write.F64(clip.EndTrim); write.F64(clip.SourceDuration);
        }
        if (track.Clips.Count > 0) write.U32(track.SubTracks);
        write.U32((uint)track.Automation.Count);
        foreach (var item in track.Automation) { write.U32(item.Clip); write.U32(item.Type); write.Bytes(item.Points); }
        NodeBaseOut(write, track.Node);
        write.U8(track.TrackType);
        write.Bytes(track.SwitchData);
        write.S32(track.LookAhead);
    }

    /** A music switch: rules, continue flag, game-sync arguments and their types, then the decision tree. */
    private static MusicSwitchObject ReadSwitch(uint id, WwiseReader read)
    {
        var music = MusicNodeIn(read); var rules = RulesIn(read);
        byte continuePlayback = read.U8();
        int depth = checked((int)read.U32());
        if (depth > read.Remaining / 5) throw new InvalidDataException("Game-sync arguments leave the object.");
        var groups = new uint[depth];
        for (int index = 0; index < depth; index++) groups[index] = read.U32();
        byte[] types = read.Bytes(depth);
        int size = checked((int)read.U32()); byte mode = read.U8();
        if (size % DecisionSize != 0 || size > read.Remaining) throw new InvalidDataException("A decision tree is not whole nodes.");
        var tree = new MusicDecision[size / DecisionSize];
        for (int index = 0; index < tree.Length; index++) tree[index] = new(read.U32(), read.U32(), read.U16(), read.U16());
        return new(id, music, rules, continuePlayback, groups, types, mode, tree);
    }

    /** A music playlist: rules, then its 30-byte items depth first. */
    private static MusicPlaylistObject ReadPlaylist(uint id, WwiseReader read)
    {
        var music = MusicNodeIn(read); var rules = RulesIn(read);
        uint count = read.U32();
        if (count > read.Remaining / 30) throw new InvalidDataException("Playlist items leave the object.");
        var items = new MusicItem[count];
        for (int index = 0; index < count; index++)
            items[index] = new(read.U32(), read.U32(), read.U32(), read.S32(), read.S16(), read.S16(), read.S16(), read.U32(),
                read.U16(), read.U8(), read.U8());
        return new(id, music, rules, items);
    }

    /** The shared music node: flags, node parameters, children, meter and stingers. */
    private static MusicNode MusicNodeIn(WwiseReader read)
    {
        byte flags = read.U8(); var node = NodeBaseIn(read);
        uint count = read.U32();
        if (count > read.Remaining / 4) throw new InvalidDataException("Music children leave the object.");
        var children = new uint[count];
        for (int index = 0; index < count; index++) children[index] = read.U32();
        byte[] meter = read.Bytes(MeterSize);
        uint stingers = read.U32();
        if (stingers > read.Remaining / 24) throw new InvalidDataException("Stingers leave the object.");
        var list = new MusicStinger[stingers];
        for (int index = 0; index < stingers; index++) list[index] = new(read.U32(), read.U32(), read.U32(), read.U32(), read.S32(), read.U32());
        return new(flags, node, children, meter, list);
    }

    /** Writes a music node in MusicNodeIn's order. */
    private static void MusicNodeOut(Writer write, MusicNode music)
    {
        write.U8(music.Flags); NodeBaseOut(write, music.Node);
        write.U32((uint)music.Children.Count);
        foreach (uint child in music.Children) write.U32(child);
        if (music.Meter.Length != MeterSize) throw new InvalidDataException("A music meter is not 23 bytes.");
        write.Bytes(music.Meter);
        write.U32((uint)music.Stingers.Count);
        foreach (var stinger in music.Stingers)
        {
            write.U32(stinger.Trigger); write.U32(stinger.Segment); write.U32(stinger.Sync); write.U32(stinger.CueFilter);
            write.S32(stinger.DontRepeat); write.U32(stinger.LookAhead);
        }
    }

    /** Transition rules: source and destination lists, their fades and cues, then an optional bridge. */
    private static IReadOnlyList<MusicRule> RulesIn(WwiseReader read)
    {
        uint count = read.U32();
        if (count > read.Remaining / 54) throw new InvalidDataException("Transition rules leave the object.");
        var rules = new List<MusicRule>();
        for (uint index = 0; index < count; index++)
        {
            var sources = IdsIn(read); var destinations = IdsIn(read);
            var sourceFade = FadeIn(read); uint sync = read.U32(), sourceCue = read.U32(); byte postExit = read.U8();
            var destinationFade = FadeIn(read); uint destinationCue = read.U32(), jump = read.U32(); ushort entry = read.U16();
            byte preEntry = read.U8(), match = read.U8();
            // A bridge flag; a set flag adds the bridge segment, its two fades and two flags.
            MusicBridge? bridge = read.U8() != 0 ? new(read.U32(), read.Bytes(12 + 12 + 1 + 1)) : null;
            rules.Add(new(sources, destinations, sourceFade, sync, sourceCue, postExit, destinationFade, destinationCue, jump, entry,
                preEntry, match, bridge));
        }
        return rules;
    }

    /** Writes rules in RulesIn's order. */
    private static void RulesOut(Writer write, IReadOnlyList<MusicRule> rules)
    {
        write.U32((uint)rules.Count);
        foreach (var rule in rules)
        {
            IdsOut(write, rule.Sources); IdsOut(write, rule.Destinations);
            FadeOut(write, rule.SourceFade); write.U32(rule.Sync); write.U32(rule.SourceCueFilter); write.U8(rule.PostExit);
            FadeOut(write, rule.DestinationFade); write.U32(rule.DestinationCueFilter); write.U32(rule.JumpTo); write.U16(rule.Entry);
            write.U8(rule.PreEntry); write.U8(rule.MatchSourceCue);
            write.U8(rule.Bridge is null ? (byte)0 : (byte)1);
            if (rule.Bridge is { } bridge) { write.U32(bridge.Segment); write.Bytes(bridge.Rest); }
        }
    }

    private static uint[] IdsIn(WwiseReader read)
    {
        uint count = read.U32();
        if (count > read.Remaining / 4) throw new InvalidDataException("An ID list leaves the object.");
        var ids = new uint[count];
        for (int index = 0; index < count; index++) ids[index] = read.U32();
        return ids;
    }

    private static void IdsOut(Writer write, IReadOnlyList<uint> ids)
    {
        write.U32((uint)ids.Count);
        foreach (uint id in ids) write.U32(id);
    }

    private static MusicFade FadeIn(WwiseReader read) => new(read.S32(), read.U32(), read.S32());
    private static void FadeOut(Writer write, MusicFade fade) { write.S32(fade.Time); write.U32(fade.Curve); write.S32(fade.Offset); }

    /** Walks the shared node parameters with the same layout WwiseHierarchy reads, keeping them as bytes. */
    private static MusicNodeBase NodeBaseIn(WwiseReader read)
    {
        int start = read.At;
        read.Skip(1);
        int effects = read.U8();
        // Bypass bits, then index, effect ID and two flags per effect.
        if (effects > 0) read.Skip(1 + effects * 7);
        read.Skip(1 + 4);
        int parentAt = read.At;
        uint parent = read.U32();
        read.Skip(1);
        read.Skip(read.U8() * 5);
        read.Skip(read.U8() * 9);
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
        uint groups = read.U32();
        for (uint group = 0; group < groups; group++) { read.Skip(4 + 1); read.Skip(read.U16() * 8); }
        int curves = read.U16();
        for (int curve = 0; curve < curves; curve++) { read.Skip(4 + 1 + 1 + 1 + 4 + 1); read.Skip(read.U16() * 12); }
        int end = read.At;
        return new(read.Data[start..parentAt], parent, read.Data[(parentAt + 4)..end]);
    }

    private static void NodeBaseOut(Writer write, MusicNodeBase node) { write.Bytes(node.Before); write.U32(node.Parent); write.Bytes(node.After); }

    /** A growing little-endian buffer. */
    private sealed class Writer
    {
        private readonly MemoryStream stream = new();
        public void U8(byte value) => stream.WriteByte(value);
        public void U16(ushort value) { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, value); stream.Write(bytes); }
        public void S16(short value) => U16(unchecked((ushort)value));
        public void U32(uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); stream.Write(bytes); }
        public void S32(int value) => U32(unchecked((uint)value));
        public void F64(double value) { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteDoubleLittleEndian(bytes, value); stream.Write(bytes); }
        public void Bytes(byte[] value) => stream.Write(value);
        public byte[] ToArray() => stream.ToArray();
    }
}
