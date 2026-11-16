using System.Buffers.Binary;
using SunriseStudio.Audio;
using SunriseStudio.Packages;

/** Music bank codec, music bank authoring and music sensor table checks. */
internal static class MusicAuthoringChecks
{
    // An installed music sensor config with ten full section rows, and the music event its rows name.
    private const uint SensorConfig = 0x80B2ED2A, SensorEvent = 0x80F1F059;
    private const int Rate = 48000;

    /** Loop points survive the millisecond cue at every installed rate; music.json reads its waves and states. */
    public static void Synthetic(string directory)
    {
        foreach (int rate in new[] { 22050, 24000, 32000, 44100, 48000, 96000 })
        {
            var wave = new PcmWave(rate, 2, 16, new byte[4]);
            for (long frame = 0; frame < 20_000_000; frame += 9973)
                Check(MusicBankAuthoring.Frame(MusicBankAuthoring.Ms(frame, wave), rate) == frame, $"Frame {frame} at {rate} Hz moved.");
        }
        string folder = Path.Combine(directory, "music-set"); Directory.CreateDirectory(folder);
        PcmWave.Write(Path.Combine(folder, "a.wav"), Rate, 2, Tone(Rate * 2, 1000).Samples);
        PcmWave.Write(Path.Combine(folder, "b.wav"), Rate, 2, Tone(Rate * 3, 2000).Samples);
        File.WriteAllText(Path.Combine(folder, MusicSetAuthoring.FileName),
            """{ "version": 0, "template_event": "0x80F1F059", "themes": [ { "name": "a", "file": "a.wav", "loop_start": 4800, "loop_end": 90000 }, { "name": "b", "file": "b.wav", "state": "0x44ED3F6F" } ] }""");
        var set = MusicSetAuthoring.Read(folder, _ => [0x44ED3F6Cu]);
        Check(set.TemplateEvent == SensorEvent && set.Themes.Count == 2 && set.Themes[0] is { LoopStart: 4800, LoopEnd: 90000, State: 0x44ED3F6C }
            && set.Themes[1] is { LoopStart: 0, LoopEnd: 144000, State: 0x44ED3F6F }, "music.json fields were misread.");
        File.WriteAllText(Path.Combine(folder, MusicSetAuthoring.FileName),
            """{ "template_event": 1, "themes": [ { "name": "x", "file": "../outside.wav" } ] }""");
        Reject(() => MusicSetAuthoring.Read(folder));
        Console.WriteLine("music_authoring synthetic=pass cue_frames=exact music_json=read");
    }

    /** Every installed music bank and music sensor round trips; an authored set rereads with exact loop cues. */
    public static void Owned(PackageCatalog catalog, string directory)
    {
        RoundTrips(catalog);
        Sensors(catalog);
        Authoring(catalog, directory);
    }

    /** Every installed music object writes back and renames both ways; every music bank reassembles byte for byte. */
    private static void RoundTrips(PackageCatalog catalog)
    {
        int banks = 0, objects = 0, remapped = 0;
        foreach (var entry in catalog.Entries.Where(entry => entry.Type == 26 && entry.SubType is 4 or 5))
        {
            byte[] bytes = catalog.Read(entry.TagHash);
            var bank = AudioCatalog.ReadBank(entry.TagHash, bytes);
            if (bank.Version != 113 || !bank.Objects.Any(item => MusicHirc.IsMusic(item.Type))) continue;
            banks++;
            foreach (var item in bank.Objects.Where(item => MusicHirc.IsMusic(item.Type)))
                Check(MusicHirc.Write(MusicHirc.Read(item)).AsSpan().SequenceEqual(item.Data),
                    $"Object {item.Id} (type {item.Type}) of {entry.TagHash:X8} did not round trip.");
            Check(MusicBankFile.RoundTrip(bytes).AsSpan().SequenceEqual(bytes), $"Music bank {entry.TagHash:X8} did not round trip.");
            // Every in-bank link follows a rename; nothing else moves.
            var inBank = bank.Objects.Where(item => MusicHirc.IsMusic(item.Type)).Select(item => item.Id).ToHashSet();
            uint Map(uint id) => inBank.Contains(id) ? id ^ 0x40000000 : id;
            foreach (var item in bank.Objects.Where(item => MusicHirc.IsMusic(item.Type)))
            {
                objects++;
                var read = MusicHirc.Read(item);
                var renamed = MusicHirc.Remap(read, Map);
                Check(renamed.Id == Map(item.Id) && MusicHirc.Links(renamed).SequenceEqual(MusicHirc.Links(read).Select(Map)),
                    $"Object {item.Id} of {entry.TagHash:X8} lost a link on rename.");
                byte[] back = MusicHirc.Write(MusicHirc.Remap(renamed, id => inBank.Contains(id ^ 0x40000000) ? id ^ 0x40000000 : id));
                Check(back.AsSpan().SequenceEqual(item.Data), $"Object {item.Id} of {entry.TagHash:X8} did not rename back.");
                if (MusicHirc.Links(read).Any(inBank.Contains)) remapped++;
            }
        }
        Check(banks > 300, "Installed music banks were not found.");
        Console.WriteLine($"music_hirc round_trip banks={banks} objects={objects} renamed_with_links={remapped} bytes=identical");
    }

    /** Every installed sensor table writes back in place; a shrunk and a grown table reread. */
    private static void Sensors(PackageCatalog catalog)
    {
        int sensors = 0, rows = 0, full = 0;
        foreach (var entry in catalog.Entries.Where(entry => entry.Reference == SunriseStudio.Tags.Generated.SEntityConfig.Class))
        {
            byte[] bytes = catalog.Read(entry.TagHash);
            if (!MusicSensor.IsSensor(bytes)) continue;
            sensors++;
            var table = MusicSensor.Read(entry.TagHash, bytes);
            rows += table.Sections.Count; full += table.Sections.Count(section => section.StateGroup is not null);
            Check(MusicSensor.Write(bytes, table.Sections).AsSpan().SequenceEqual(bytes), $"Sensor {entry.TagHash:X8} did not round trip.");
        }
        byte[] sample = catalog.Read(SensorConfig);
        var original = MusicSensor.Read(SensorConfig, sample);
        Check(original.Sections.Count == 10 && original.Sections.All(section => section is { EventTag: SensorEvent, StateGroup: 0x4A467C68 }),
            "The sample sensor's rows changed.");
        MusicSensorSection[] three = [new(0x81000001, 0x4A467C68, 1), new(0x81000001, 0x4A467C68, 2), new(0x81000001, null, 3)];
        var shrunk = MusicSensor.Read(SensorConfig, MusicSensor.Write(sample, three));
        var many = Enumerable.Range(0, 40).Select(index => new MusicSensorSection(0x81000002, 0x4A467C68, (uint)index + 7)).ToArray();
        byte[] grownBytes = MusicSensor.Write(sample, many);
        var grown = MusicSensor.Read(SensorConfig, grownBytes);
        Check(shrunk.Sections.SequenceEqual(three) && grown.Sections.SequenceEqual(many) && shrunk.Names.SequenceEqual(original.Names)
            && BinaryPrimitives.ReadUInt64LittleEndian(grownBytes) == (ulong)grownBytes.Length, "A rewritten sensor table did not reread.");
        Reject(() => MusicSensor.Write(sample, Enumerable.Repeat(many[0], MusicSensor.MaximumSections + 1).ToArray()));
        Check(sensors == 997, $"Installed music sensor count changed: {sensors}.");
        Console.WriteLine($"music_sensor round_trip sensors={sensors} rows={rows} full_rows={full} state_rows={rows - full} rewrite=3,40 bytes=identical");
    }

    /** Three themes over the sample sensor's event: the bank, its cues, media, links, event and sensor rows. */
    private static void Authoring(PackageCatalog catalog, string directory)
    {
        byte[] eventBytes = catalog.Read(SensorEvent);
        uint bankTag = SunriseStudio.Tags.Views.TagView.Member<SunriseStudio.Tags.Generated.SWwiseEvent, uint>(eventBytes, 0, "SoundBank");
        uint templateEvent = SunriseStudio.Tags.Views.TagView.Member<SunriseStudio.Tags.Generated.SWwiseEvent, uint>(eventBytes, 0, "WwiseEventId");
        var template = AudioCatalog.ReadBank(bankTag, catalog.Read(bankTag));
        var (group, states) = MusicBankAuthoring.StateChoices(template, templateEvent);
        Check(group == 0x4A467C68 && states.Count >= 3, "The template theme switch changed.");
        // Three loops with known points; each frame holds its own index so a misplaced sample shows.
        MusicTheme[] themes =
        [
            new("zone_a", Ramp(Rate * 4, 1), 12_345, Rate * 4 - 1_000, states[0]),
            new("zone_b", Ramp(Rate * 3, 2), 0, Rate * 3, states[1]),
            new("battle", Ramp(Rate * 5, 3), 48_000, 200_000, states[2]),
        ];
        var set = new MusicSet(SensorEvent, themes);
        var authored = MusicSetAuthoring.Author(catalog, set, "a12_test_music", 0x81000010, 0x81000011);
        var bank = AudioCatalog.ReadBank(0x81000010, authored.Bank.Bytes);
        var hierarchy = WwiseHierarchy.Read([bank]);
        Check(hierarchy.Diagnostics.Count == 0 && bank.Id == authored.Bank.BankId && bank.EmbeddedMedia.Count == 3, "The authored bank did not reread.");
        var templateIds = template.Objects.Select(item => item.Id).ToHashSet();
        var music = bank.Objects.Where(item => MusicHirc.IsMusic(item.Type)).ToDictionary(item => item.Id, MusicHirc.Read);
        Check(!bank.Objects.Where(item => item.Type is 3 or 4 || MusicHirc.IsMusic(item.Type)).Any(item => templateIds.Contains(item.Id)),
            "An authored object took a template ID.");
        // Every music link names an object of the new bank, or none.
        var dangling = music.Values.SelectMany(MusicHirc.Links).Where(id => id is not (0 or uint.MaxValue) && !music.ContainsKey(id)).ToArray();
        Check(dangling.Length == 0, $"Authored music links dangle: {string.Join(",", dangling.Distinct())}.");
        var themeSwitch = (MusicSwitchObject)music.Values.Single(item => item is MusicSwitchObject s && s.Groups.Count == 1);
        Check(themeSwitch.Groups[0] == group && MusicHirc.LeafIndices(themeSwitch).Select(index => themeSwitch.Tree[index].Key)
            .SequenceEqual(themes.Select(theme => theme.State).Append(0u).OrderBy(key => key)), "The theme switch keys are not the theme states.");
        foreach (var (theme, made) in themes.Zip(authored.Bank.Themes))
        {
            var segment = (MusicSegmentObject)music[made.Segment];
            var track = (MusicTrackObject)music[made.Track];
            var playlist = (MusicPlaylistObject)music[made.Playlist];
            Check(MusicBankAuthoring.Frame(segment.Markers[0].Position, Rate) == theme.LoopStart
                && MusicBankAuthoring.Frame(segment.Markers[^1].Position, Rate) == theme.LoopEnd
                && MusicBankAuthoring.Frame(segment.Duration, Rate) == MusicBankAuthoring.Frames(theme.Wave), $"Theme {theme.Name}: a cue moved.");
            Check(playlist.Items[^1] is { Loop: 0, Kind: -1 } && playlist.Items[^1].Segment == made.Segment && playlist.Rules.Single() is { Sync: 7, PostExit: 0, PreEntry: 0 },
                $"Theme {theme.Name}: the playlist does not loop its segment on the exit cue.");
            var source = track.Sources.Single();
            var media = bank.EmbeddedMedia.Single(item => item.Id == made.Media);
            var decoded = WemDecoder.Decode(media.Embedded, 2, CancellationToken.None);
            Check(source is { Plugin: 0x00010001, StreamType: 0 } && source.MediaId == made.Media && source.MediaSize == media.Size
                && decoded.Samples.AsSpan().SequenceEqual(theme.Wave.Samples), $"Theme {theme.Name}: the media did not reread.");
        }
        // The new event plays the switch chain of the new bank.
        var play = hierarchy.Get<WwiseEvent>(authored.Bank.PlayEventId)!;
        var targets = play.Actions.Select(id => hierarchy.Get<WwiseAction>(id)!).Where(action => action.Kind == 4).Select(action => action.Target).ToArray();
        Check(targets.Length > 0 && targets.All(music.ContainsKey), "The authored event does not play the authored music.");
        Check(SunriseStudio.Tags.Views.TagView.Member<SunriseStudio.Tags.Generated.SWwiseEvent, uint>(authored.Event, 0, "SoundBank") == 0x81000010
            && authored.Sections.Select(section => section.State).SequenceEqual(themes.Select(theme => theme.State))
            && authored.Sections.All(section => section is { EventTag: 0x81000011, StateGroup: 0x4A467C68 }), "The event or the sensor rows are wrong.");
        byte[] sensor = MusicSensor.Write(catalog.Read(SensorConfig), authored.Sections);
        Check(MusicSensor.Read(SensorConfig, sensor).Sections.SequenceEqual(authored.Sections), "The authored sensor rows did not reread.");
        string timing = Audition(bank, authored.Bank.Themes[2], themes[2], directory);
        Console.WriteLine($"music_authoring owned template={SensorEvent:X8} bank_objects={bank.Objects.Count} themes=3 cues=exact media=exact links=closed sensor_rows=3 {timing}");
    }

    /**
     * Plans the battle playlist in Studio's music preview and checks the joins: after the first pass,
     * each pass plays frames LoopStart to LoopEnd and starts where the last one reached LoopEnd.
     */
    private static string Audition(AudioBank bank, AuthoredMusicTheme made, MusicTheme theme, string directory)
    {
        var analysis = AudioAnalysis.Read([bank]);
        var plan = AudioTimeline.Build(analysis, made.Playlist, 3);
        var render = AudioTimeline.Render(plan, new Dictionary<uint, PcmWave> { [made.Media] = theme.Wave }, Path.Combine(directory, "battle.wav"));
        var wave = PcmWave.Read(render.Path);
        int Value(long frame) => BinaryPrimitives.ReadInt16LittleEndian(wave.Samples.AsSpan((int)frame * 4));
        long body = theme.LoopEnd - theme.LoopStart;
        string segments = string.Join(";", plan.Segments.Select(segment => $"{segment.StartSeconds:0.######}/{segment.EntrySeconds:0.######}/{segment.ExitSeconds:0.######}"));
        // The template's volumes scale every sample by one gain; a sum of two passes would show as a step.
        double gain = Value(29_999) / (double)Expected(29_999);
        bool Near(long output, long source) => Math.Abs(Value(output) - gain * Expected(source)) <= 2;
        // Pass 2 starts at output frame LoopEnd and plays source frame LoopStart; pass 3 follows one body later.
        bool joined = plan.Segments.Count == 3 && gain > 0.1 && Near(theme.LoopEnd - 1, theme.LoopEnd - 1) && Near(theme.LoopEnd, theme.LoopStart)
            && Near(theme.LoopEnd + body - 1, theme.LoopEnd - 1) && Near(theme.LoopEnd + body, theme.LoopStart)
            && Near(theme.LoopEnd + 2 * body - 1, theme.LoopEnd - 1);
        Console.WriteLine($"music_authoring audition segments={segments} frames={MusicBankAuthoring.Frames(wave)} gain={gain:0.0000} joined={joined}");
        Check(joined, "Studio's preview does not join the loop at the authored frames.");
        return $"preview_loop={(joined ? "exact" : "differs")}";
    }

    /** A ramp's left sample at a source frame; see Ramp. */
    private static short Expected(long frame) => (short)(frame % 30000);

    /** A stereo ramp whose left sample is its frame index modulo 30000; the right one names the theme. */
    private static PcmWave Ramp(int frames, short theme)
    {
        byte[] samples = new byte[frames * 4];
        for (int frame = 0; frame < frames; frame++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(frame * 4), (short)(frame % 30000));
            BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(frame * 4 + 2), theme);
        }
        return new(Rate, 2, 16, samples);
    }

    private static PcmWave Tone(int frames, short value)
    {
        byte[] samples = new byte[frames * 4];
        for (int at = 0; at < samples.Length; at += 2) BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(at), value);
        return new(Rate, 2, 16, samples);
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception error) when (error is InvalidDataException or FormatException or IOException) { return; }
        throw new InvalidDataException("Invalid music input was accepted.");
    }
}
