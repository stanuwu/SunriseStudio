using System.Buffers.Binary;

namespace SunriseStudio.Audio;

/**
 * One looping theme.
 * @param Name Unique within the set; the theme's object IDs derive from it.
 * @param LoopStart First sample frame of the loop; frames before it play once as the intro.
 * @param LoopEnd Sample frame where playback jumps back to LoopStart; at most the wave's frame count.
 * @param State The state value of the bank's music state group that selects this theme.
 */
public sealed record MusicTheme(string Name, PcmWave Wave, long LoopStart, long LoopEnd, uint State);

/** Where a theme's objects and media went in the authored bank. */
public sealed record AuthoredMusicTheme(string Name, uint State, uint Playlist, uint Segment, uint Track, uint Media,
    double EntryMs, double ExitMs, double DurationMs);

/**
 * A music bank built over an installed one.
 * @param PlayEventId The Wwise event that starts the music; the template event's copy.
 * @param StateGroup The state group the theme switch follows; sensor sections set it.
 * @param Media The new media, in bank order.
 */
public sealed record AuthoredMusicBank(byte[] Bytes, uint BankId, uint PlayEventId, uint StateGroup,
    IReadOnlyList<AuthoredMusicTheme> Themes, IReadOnlyList<(uint Id, byte[] Data)> Media);

/**
 * Builds a music bank from an installed destination bank: its theme switch, the switches above it,
 * their rules and its events stay; every theme becomes a playlist that loops one segment of one track.
 */
public static class MusicBankAuthoring
{
    // HIRC object types.
    private const byte Action = 3, Event = 4;
    // Action types keep the action in the high byte; 0x04 is Play.
    private const int PlayKind = 0x04;
    // The runtime decodes plug-in 0x00010001 (PCM) itself; stream type 0 keeps the media in the bank.
    private const uint PcmPlugin = 0x00010001;
    // A playlist leaf with loop count 0 repeats forever, as installed endless items do.
    private const short Forever = 0;
    // Item kind -1 is a segment leaf.
    private const int SegmentLeaf = -1;
    // A rule list entry of uint.MaxValue means any object.
    private const uint Any = uint.MaxValue;
    // Node types the authored bank drops: sound, random or sequence, switch, actor-mixer, layer.
    private static readonly HashSet<byte> SoundNodes = [2, 5, 6, 7, 9];

    /**
     * Copies a template music bank around new themes.
     * @param templateEventId The Wwise event the template's event tag plays.
     * @param name Unique lower-case name; the bank, event and object IDs derive from it.
     * @param themes At least one; the first also plays when the state group holds no listed value.
     */
    public static AuthoredMusicBank Build(AudioBank template, uint templateEventId, string name, IReadOnlyList<MusicTheme> themes)
    {
        if (template.Version != 113) throw new NotSupportedException("Music authoring needs a version-113 template bank.");
        if (themes.Count == 0) throw new ArgumentException("A music set needs a theme.");
        if (themes.Select(theme => theme.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != themes.Count
            || themes.Select(theme => theme.State).Distinct().Count() != themes.Count || themes.Any(theme => theme.State == 0))
            throw new InvalidDataException("Each theme needs its own name and its own non-zero state value.");
        foreach (var theme in themes) CheckLoop(theme);

        var objects = template.Objects.ToDictionary(item => item.Id);
        var music = template.Objects.Where(item => MusicHirc.IsMusic(item.Type)).ToDictionary(item => item.Id, MusicHirc.Read);
        if (!objects.TryGetValue(templateEventId, out var mainEvent) || mainEvent.Type != Event)
            throw new InvalidDataException("The template bank does not hold its event.");
        var themeSwitch = FindThemeSwitch(PlayTargets(mainEvent, objects), music)
            ?? throw new InvalidDataException("The template event plays no music switch with one state group.");
        var (playlist, segment, track) = FindTemplateLeaf(themeSwitch, music);

        // The switches above the theme switch stay, so the template's bus routing stays.
        var chain = new List<MusicSwitchObject>();
        for (uint parent = themeSwitch.Music.Node.Parent; parent != 0;)
        {
            if (!objects.TryGetValue(parent, out var above)) break;
            if (!music.TryGetValue(parent, out var aboveMusic) || aboveMusic is not MusicSwitchObject aboveSwitch)
                throw new InvalidDataException($"The theme switch sits under object {parent}, which is not a music switch.");
            if (chain.Count > 64 || chain.Any(item => item.Id == parent)) throw new InvalidDataException("The music switch chain loops.");
            chain.Add(aboveSwitch);
            parent = aboveSwitch.Music.Node.Parent;
        }

        uint bankId = WwiseSoundBank.Hash(name);
        uint Id(string key) => WwiseSoundBank.Hash(name + "/" + key) & 0x3FFFFFFF;
        var ids = new Dictionary<uint, uint> { [themeSwitch.Id] = Id("switch") };
        foreach (var item in chain) ids[item.Id] = Id("chain/" + item.Id.ToString("X8"));
        var built = themes.Select(theme => new AuthoredMusicTheme(theme.Name, theme.State, Id("playlist/" + theme.Name),
            Id("segment/" + theme.Name), Id("track/" + theme.Name), Id("media/" + theme.Name),
            Ms(theme.LoopStart, theme.Wave), Ms(theme.LoopEnd, theme.Wave), Ms(Frames(theme.Wave), theme.Wave))).ToArray();

        // Events and actions keep their template order; an action on a dropped object is dropped.
        var kept = new HashSet<uint>(ids.Keys);
        bool Dropped(uint id) => objects.TryGetValue(id, out var item) && (MusicHirc.IsMusic(item.Type) || SoundNodes.Contains(item.Type)) && !kept.Contains(id);
        var actions = new Dictionary<uint, byte[]>();
        foreach (var item in template.Objects.Where(item => item.Type == Action))
        {
            if (item.Data.Length < 6) throw new InvalidDataException("A template action is truncated.");
            uint target = U32(item.Data, 2);
            if (Dropped(target)) continue;
            byte[] data = item.Data.ToArray();
            Put(data, 2, ids.GetValueOrDefault(target, target));
            // A play action ends with the bank that holds its target.
            if (data[1] == PlayKind && U32(data, data.Length - 4) == template.Id) Put(data, data.Length - 4, bankId);
            actions[item.Id] = data;
            ids[item.Id] = Id("action/" + item.Id.ToString("X8"));
        }
        var events = new List<(uint Id, byte[] Data)>();
        foreach (var item in template.Objects.Where(item => item.Type == Event))
        {
            uint count = item.Data.Length >= 4 ? U32(item.Data, 0) : uint.MaxValue;
            if (count > 256 || item.Data.Length != 4 + count * 4) throw new InvalidDataException("A template event has an unexpected action list.");
            var list = Enumerable.Range(0, (int)count).Select(index => U32(item.Data, 4 + index * 4)).Where(actions.ContainsKey).ToArray();
            if (list.Length == 0) continue;
            byte[] data = new byte[4 + list.Length * 4];
            Put(data, 0, (uint)list.Length);
            for (int index = 0; index < list.Length; index++) Put(data, 4 + index * 4, ids[list[index]]);
            uint eventId = item.Id == templateEventId ? bankId : WwiseSoundBank.Hash(name + "/event/" + item.Id.ToString("X8"));
            events.Add((eventId, data));
        }
        if (!events.Any(item => item.Id == bankId)) throw new InvalidDataException("The template event lost every action.");

        var all = ids.Values.Concat(built.SelectMany(theme => new[] { theme.Playlist, theme.Segment, theme.Track }))
            .Concat(events.Select(item => item.Id)).ToArray();
        var media = built.Select(theme => theme.Media).ToArray();
        if (all.Distinct().Count() != all.Length || media.Distinct().Count() != media.Length
            || all.Any(id => objects.ContainsKey(id)) || objects.ContainsKey(bankId))
            throw new InvalidDataException("A new music object ID collides; choose another name.");

        uint Map(uint id) => ids.GetValueOrDefault(id, id);
        var hirc = new List<(byte Type, uint Id, byte[] Data)>();
        // Share sets, states and plug-in settings stay as the template holds them.
        foreach (var item in template.Objects.Where(item => !MusicHirc.IsMusic(item.Type) && !SoundNodes.Contains(item.Type)
            && item.Type is not (Action or Event)))
            hirc.Add((item.Type, item.Id, item.Data));
        var mediaData = new List<(uint, byte[])>();
        for (int index = 0; index < themes.Count; index++)
        {
            var theme = themes[index]; var made = built[index];
            byte[] wem = WwiseSoundBank.PcmWem(theme.Wave);
            mediaData.Add((made.Media, wem));
            hirc.Add((MusicHirc.Track, made.Track, MusicHirc.Write(ThemeTrack(track, made, wem.Length))));
            hirc.Add((MusicHirc.Segment, made.Segment, MusicHirc.Write(ThemeSegment(segment, made))));
            hirc.Add((MusicHirc.Playlist, made.Playlist, MusicHirc.Write(ThemePlaylist(playlist, made, ids[themeSwitch.Id], Id("item/" + theme.Name)))));
        }
        var newSwitch = ThemeSwitchOf(themeSwitch, built, ids, kept);
        hirc.Add((MusicHirc.Switch, newSwitch.Id, MusicHirc.Write(newSwitch)));
        foreach (var item in chain)
        {
            var above = (MusicSwitchObject)MusicHirc.Remap(item, Map);
            // A switch above keeps only the kept child; any other leaf plays nothing.
            var leaves = MusicHirc.LeafIndices(item).ToHashSet();
            var tree = above.Tree.Select((node, index) => leaves.Contains(index) && Dropped(item.Tree[index].Value) ? node with { Value = 0 } : node).ToArray();
            above = above with
            {
                Music = above.Music with
                {
                    Children = item.Music.Children.Where(child => !Dropped(child)).Select(Map).ToArray(), Stingers = [],
                },
                Rules = KeptRules(item.Rules, kept, Map), Tree = tree,
            };
            hirc.Add((MusicHirc.Switch, above.Id, MusicHirc.Write(above)));
        }
        foreach (var item in template.Objects.Where(item => item.Type == Action && actions.ContainsKey(item.Id)))
            hirc.Add((Action, ids[item.Id], actions[item.Id]));
        foreach (var item in events) hirc.Add((Event, item.Id, item.Data));

        byte[] bytes = Write(template, bankId, mediaData, hirc);
        return new(bytes, bankId, bankId, themeSwitch.Groups[0], built, mediaData);
    }

    /** The state group the template event's theme switch follows and the non-zero state values its tree lists, in tree order. */
    public static (uint Group, IReadOnlyList<uint> States) StateChoices(AudioBank template, uint templateEventId)
    {
        var objects = template.Objects.ToDictionary(item => item.Id);
        var music = template.Objects.Where(item => MusicHirc.IsMusic(item.Type)).ToDictionary(item => item.Id, MusicHirc.Read);
        if (!objects.TryGetValue(templateEventId, out var mainEvent) || mainEvent.Type != Event)
            throw new InvalidDataException("The template bank does not hold its event.");
        var found = FindThemeSwitch(PlayTargets(mainEvent, objects), music)
            ?? throw new InvalidDataException("The template event plays no music switch with one state group.");
        var states = MusicHirc.LeafIndices(found).Select(index => found.Tree[index].Key).Where(key => key != 0).Distinct().ToArray();
        return (found.Groups[0], states);
    }

    /** Sample frame to milliseconds; converting back with Frame gives the same frame exactly. */
    public static double Ms(long frame, PcmWave wave) => frame * 1000.0 / wave.SampleRate;

    /** Milliseconds to the nearest sample frame. */
    public static long Frame(double ms, int sampleRate) => (long)Math.Round(ms * sampleRate / 1000.0, MidpointRounding.AwayFromZero);

    public static long Frames(PcmWave wave) => wave.Samples.Length / wave.BlockAlign;

    private static void CheckLoop(MusicTheme theme)
    {
        long frames = Frames(theme.Wave);
        if (theme.LoopStart < 0 || theme.LoopEnd <= theme.LoopStart || theme.LoopEnd > frames)
            throw new InvalidDataException($"Theme {theme.Name}: the loop must lie inside the wave and end after it starts.");
        if (Frame(Ms(theme.LoopStart, theme.Wave), theme.Wave.SampleRate) != theme.LoopStart
            || Frame(Ms(theme.LoopEnd, theme.Wave), theme.Wave.SampleRate) != theme.LoopEnd)
            throw new InvalidDataException($"Theme {theme.Name}: a loop point does not survive the millisecond cue.");
    }

    /** The first music switch with one game-sync argument, searched breadth first from the event's play targets. */
    private static MusicSwitchObject? FindThemeSwitch(IEnumerable<uint> roots, IReadOnlyDictionary<uint, MusicObject> music)
    {
        var seen = new HashSet<uint>();
        var queue = new Queue<uint>(roots);
        while (queue.TryDequeue(out uint id))
        {
            if (!seen.Add(id) || !music.TryGetValue(id, out var item) || item is not MusicSwitchObject found) continue;
            if (found.Groups.Count == 1) return found;
            foreach (uint child in found.Music.Children) queue.Enqueue(child);
        }
        return null;
    }

    /** The first playlist under the theme switch, its first segment and that segment's first track; their settings are copied. */
    private static (MusicPlaylistObject, MusicSegmentObject, MusicTrackObject) FindTemplateLeaf(MusicSwitchObject themeSwitch,
        IReadOnlyDictionary<uint, MusicObject> music)
    {
        foreach (uint child in themeSwitch.Music.Children)
        {
            if (!music.TryGetValue(child, out var item) || item is not MusicPlaylistObject playlist) continue;
            foreach (var entry in playlist.Items.Where(entry => entry.Kind == SegmentLeaf))
                if (music.TryGetValue(entry.Segment, out var found) && found is MusicSegmentObject segment && segment.Markers.Count >= 2)
                    foreach (uint trackId in segment.Music.Children)
                        if (music.TryGetValue(trackId, out var trackItem) && trackItem is MusicTrackObject track && track.TrackType == 0)
                            return (playlist, segment, track);
        }
        throw new InvalidDataException("The theme switch has no playlist with a cued segment and a plain track to copy.");
    }

    private static MusicTrackObject ThemeTrack(MusicTrackObject template, AuthoredMusicTheme theme, int mediaSize) => template with
    {
        Id = theme.Track, Node = template.Node with { Parent = theme.Segment },
        Sources = [new(PcmPlugin, 0, theme.Media, (uint)mediaSize, 0, null)],
        Clips = [new(0, theme.Media, 0, 0, 0, theme.DurationMs)], SubTracks = 1, Automation = [],
    };

    /** The segment's first marker is its entry cue and its last its exit cue; both move to the loop points. */
    private static MusicSegmentObject ThemeSegment(MusicSegmentObject template, AuthoredMusicTheme theme) => template with
    {
        Id = theme.Segment, Duration = theme.DurationMs,
        Music = template.Music with { Node = template.Music.Node with { Parent = theme.Playlist }, Children = [theme.Track], Stingers = [] },
        Markers = [new(template.Markers[0].Id, theme.EntryMs, []), new(template.Markers[^1].Id, theme.ExitMs, [])],
    };

    /**
     * One endless leaf under a sequence root. The loop rule jumps at the exit cue to the entry cue with
     * no pre-entry and no post-exit, so frames LoopStart to LoopEnd repeat exactly.
     */
    private static MusicPlaylistObject ThemePlaylist(MusicPlaylistObject template, AuthoredMusicTheme theme, uint parent, uint itemId)
    {
        var root = template.Items[0];
        var leaf = template.Items.FirstOrDefault(entry => entry.Kind == SegmentLeaf) ?? root;
        var rule = template.Rules.FirstOrDefault() ?? throw new InvalidDataException("The template playlist has no transition rule.");
        return template with
        {
            Id = theme.Playlist,
            Music = template.Music with { Node = template.Music.Node with { Parent = parent }, Children = [theme.Segment], Stingers = [] },
            Rules = [rule with { Sources = [Any], Destinations = [Any], PostExit = 0, PreEntry = 0, JumpTo = 0, Bridge = null }],
            Items =
            [
                root with { Segment = 0, Id = itemId, ChildCount = 1, Loop = 1 },
                leaf with { Segment = theme.Segment, Id = itemId ^ 1, ChildCount = 0, Kind = SegmentLeaf, Loop = Forever },
            ],
        };
    }

    /** The theme switch with one leaf per theme, keys sorted; key 0 plays the first theme. */
    private static MusicSwitchObject ThemeSwitchOf(MusicSwitchObject template, IReadOnlyList<AuthoredMusicTheme> themes,
        IReadOnlyDictionary<uint, uint> ids, IReadOnlySet<uint> kept)
    {
        var leafIndices = MusicHirc.LeafIndices(template);
        var leafTemplate = leafIndices.Count > 0 ? template.Tree[leafIndices[0]] : new MusicDecision(0, 0, 50, 100);
        var rootTemplate = template.Tree.Count > 0 ? template.Tree[0] : new MusicDecision(0, 0, 50, 100);
        var leaves = themes.Select(theme => (theme.State, theme.Playlist)).Prepend((0u, themes[0].Playlist)).OrderBy(leaf => leaf.Item1).ToArray();
        var tree = new List<MusicDecision> { rootTemplate with { Key = 0, Value = 1u | ((uint)leaves.Length << 16) } };
        tree.AddRange(leaves.Select(leaf => leafTemplate with { Key = leaf.Item1, Value = leaf.Item2 }));
        uint Map(uint id) => ids.GetValueOrDefault(id, id);
        return template with
        {
            Id = ids[template.Id],
            Music = template.Music with
            {
                Node = template.Music.Node with { Parent = Map(template.Music.Node.Parent) },
                Children = themes.Select(theme => theme.Playlist).ToArray(), Stingers = [],
            },
            Rules = KeptRules(template.Rules, kept, Map), Tree = tree,
        };
    }

    /** Rules whose lists name only any-object or kept objects, renamed; a jump target is cleared. */
    private static IReadOnlyList<MusicRule> KeptRules(IReadOnlyList<MusicRule> rules, IReadOnlySet<uint> kept, Func<uint, uint> map) =>
        rules.Where(rule => rule.Sources.Concat(rule.Destinations).All(id => id == Any || kept.Contains(id)) && rule.Bridge is null)
            .Select(rule => rule with { Sources = rule.Sources.Select(map).ToArray(), Destinations = rule.Destinations.Select(map).ToArray(), JumpTo = 0 })
            .ToArray();

    private static IEnumerable<uint> PlayTargets(AudioBankObject item, IReadOnlyDictionary<uint, AudioBankObject> objects)
    {
        uint count = item.Data.Length >= 4 ? U32(item.Data, 0) : 0;
        for (int index = 0; index < count && 8 + index * 4 <= item.Data.Length; index++)
            if (objects.TryGetValue(U32(item.Data, 4 + index * 4), out var action) && action.Type == Action && action.Data.Length >= 6
                && action.Data[1] == PlayKind)
                yield return U32(action.Data, 2);
    }

    /** Writes BKHD with the new ID, DIDX, DATA and HIRC, then reads the bank back. */
    private static byte[] Write(AudioBank template, uint bankId, IReadOnlyList<(uint Id, byte[] Data)> media,
        IReadOnlyList<(byte Type, uint Id, byte[] Data)> objects)
    {
        byte[] header = MusicBankFile.Chunks(template.Bytes).FirstOrDefault(chunk => chunk.Kind == "BKHD").Data?.ToArray()
            ?? throw new InvalidDataException("The template bank has no header.");
        Put(header, 4, bankId);
        var chunks = new List<(string, byte[])> { ("BKHD", header) };
        if (media.Count > 0)
        {
            byte[] index = new byte[media.Count * 12];
            using var payload = new MemoryStream();
            for (int item = 0; item < media.Count; item++)
            {
                // In-bank media files start on 16-byte boundaries.
                while (payload.Length % 16 != 0) payload.WriteByte(0);
                Put(index, item * 12, media[item].Id);
                Put(index, item * 12 + 4, (uint)payload.Length);
                Put(index, item * 12 + 8, (uint)media[item].Data.Length);
                payload.Write(media[item].Data);
            }
            chunks.Add(("DIDX", index));
            chunks.Add(("DATA", payload.ToArray()));
        }
        chunks.Add(("HIRC", MusicBankFile.Hirc(objects)));
        byte[] bytes = MusicBankFile.Assemble(chunks);
        var check = WwiseSoundBank.Read(0, bytes);
        if (check.Id != bankId || check.EmbeddedMedia.Count != media.Count || check.Objects.Count != objects.Count)
            throw new InvalidDataException("The authored music bank did not read back.");
        return bytes;
    }

    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static void Put(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
}
