using System.Buffers.Binary;
using SunriseStudio.Audio;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Tft;
using SunriseStudio.Tags.Views;
using SunriseStudio.World;

/**
 * A three-track bundle music set on the EDZ testbed: the project builds, and the built packages hold
 * the bank with its loop cues, the event that names it, and sensor configs the owners reach.
 */
static class MusicWorldChecks
{
    // Trostland, Widow's Walk and the Atrium in EDZ.
    private static readonly uint[] Kept = [0xB8459D59, 0xC0064AEF, 0xC0064AED];
    private const string Destination = "musicbed";
    private const int Rate = 48000;
    // Track name, seconds, loop start and loop end in frames.
    private static readonly (string Name, int Seconds, long Start, long End)[] Tracks =
        [("zone_a", 6, 24_000, 264_000), ("zone_b", 5, 0, 240_000), ("battle", 7, 48_123, 300_007)];

    /** @param scratchRoot Folder for the scratch project; null takes the temp folder. */
    public static void Run(string gameRoot, string? scratchRoot = null)
    {
        scratchRoot ??= Path.GetTempPath();
        using var catalog = PackageCatalog.Open(gameRoot);
        var schema = StudioRegistry.Index;
        uint scenario = WorldBuild.Named(catalog, "edz_freeroam:scenario_client");
        string scratch = Path.Combine(scratchRoot, "sunrise-music-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = SunriseStudio.Projects.StudioProject.Create(Path.Combine(scratch, "project"), "Musicbed");
            WorldTemplate.Create(project.ContentDirectory, catalog, schema, scenario, Kept.ToHashSet(), Destination);
            ushort package = Enumerable.Range(0, 0x10000).Select(x => (ushort)x)
                .First(x => PackageWriter.IsNewPackageId(x) && !catalog.ContainsInstalledPackage(x));
            string bundle = Bundle(Path.Combine(scratch, "bundle"));
            var bundleJson = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(bundle, "bundle.json")))!.AsObject();
            var placed = WorldMusic.PlaceBundleMusic(project, catalog, schema, Destination, bundle, bundleJson, package)
                ?? throw new InvalidOperationException("The bundle's music was not read.");
            var music = placed.Music;
            WorldBuild.Check(music.Sensors.Count > 0 && placed.CombatSection == 2 && placed.BubbleSections.Values.Order().SequenceEqual([0, 1]),
                "The bundle's music sections are wrong.");

            var build = project.Build(catalog, tagSchema: schema);
            string packages = Path.Combine(build.OutputDirectory, "music-check", "packages");
            Directory.CreateDirectory(packages);
            foreach (string file in build.PackageFiles) File.Copy(file, Path.Combine(packages, Path.GetFileName(file)), true);
            using var built = PackageCatalog.Open(Path.GetDirectoryName(packages)!);

            // The bank: three themes, each segment's cues on the authored frames.
            var bank = AudioCatalog.ReadBank(music.BankTag, built.Read(music.BankTag));
            var objects = bank.Objects.Where(item => MusicHirc.IsMusic(item.Type)).ToDictionary(item => item.Id, MusicHirc.Read);
            WorldBuild.Check(built.TryGetEntry(music.BankTag, out var bankEntry) && bankEntry.Type == 26 && bankEntry.SubType == 5
                && bankEntry.Reference == bank.Id && bank.Id == music.Set.Bank.BankId && music.Set.Bank.Themes.Count == 3 && bank.EmbeddedMedia.Count == 3,
                "The built bank is not the authored one.");
            foreach (var (theme, track) in music.Set.Bank.Themes.Zip(Tracks))
            {
                var segment = (MusicSegmentObject)objects[theme.Segment];
                WorldBuild.Check(MusicBankAuthoring.Frame(segment.Markers[0].Position, Rate) == track.Start
                    && MusicBankAuthoring.Frame(segment.Markers[^1].Position, Rate) == track.End
                    && MusicBankAuthoring.Frame(segment.Duration, Rate) == track.Seconds * Rate, $"Track {track.Name}: a loop cue moved.");
            }
            // The event names the bank and plays its event.
            byte[] eventBytes = built.Read(music.EventTag);
            ref readonly var musicEvent = ref TagView.At<SWwiseEvent>(eventBytes);
            WorldBuild.Check(musicEvent.SoundBank == music.BankTag && musicEvent.WwiseEventId == music.Set.Bank.PlayEventId,
                "The built music event does not name the bank.");

            // Every sensor config the world owns holds one row per theme naming the event, and its owner reaches it.
            var tags = build.TagFiles.ToDictionary(tag => tag.Id, tag => tag.TagHash, StringComparer.Ordinal);
            var overlay = build.TagFiles.Select(tag => (Tag: tag, Entry: built.TryGetEntry(tag.TagHash, out var entry) ? entry
                : throw new InvalidDataException($"{tag.Id} is not in the built packages."))).ToArray();
            var bytes = overlay.ToDictionary(item => item.Tag.TagHash, item => built.Read(item.Tag.TagHash));
            catalog.SetOverlayEntries(overlay.Select(item => new PackageOverlayEntry(item.Entry, item.Tag.Id)));
            catalog.ReadOverride = tag => bytes.GetValueOrDefault(tag);
            int reached = 0;
            foreach (var sensor in music.Sensors)
            {
                uint config = tags[sensor.ConfigFile], owner = tags[sensor.OwnerFile];
                var table = MusicSensor.Read(config, bytes[config]);
                WorldBuild.Check(table.Sections.SequenceEqual(music.Set.Sections) && table.Sections.All(row => row.EventTag == music.EventTag),
                    $"Sensor {sensor.ConfigFile} does not hold the theme rows.");
                WorldBuild.Check(ComponentSlotCatalog.OwnerSlots(catalog, owner).Any(slot => slot.SlotType == 11 && slot.SlotIndex == sensor.SlotIndex
                    && slot.ConfigTag == config), $"Owner {sensor.OwnerFile} does not reach its new sensor config.");
                reached++;
            }
            // The stock SDK module names each slot by its owner.
            string module = File.ReadAllText(Directory.GetFiles(Path.Combine(gameRoot, "bin", "x64", "Sunrise", "sdk", "lua", "missions"), "edz_freeroam_*.lua").Single());
            var named = music.Sensors.Where(sensor => sensor.SdkConstant is { } constant
                && module.Contains($"{constant} = {{ id = \"slot/{sensor.OwnerTag:x8}/", StringComparison.Ordinal)).ToArray();
            WorldBuild.Check(named.Length == music.Sensors.Count, "A sensor's SDK constant is not in the stock module.");
            string bubbles = string.Join(",", placed.BubbleSections.Select(pair => $"{pair.Key:X8}:{pair.Value}"));
            int configs = music.Sensors.Select(sensor => sensor.ConfigFile).Distinct().Count();
            int global = music.Sensors.Count(sensor => sensor.BubbleIndex == uint.MaxValue);
            Console.WriteLine($"world_music template_event={music.Set.EventEntry.TagHash:X8} template_bank={music.Set.BankSource:X8} bank={music.BankTag:X8} event={music.EventTag:X8} themes=3 cues=exact "
                + $"sensors={reached} rows=3 owners=reach configs={configs} activity_wide={global} first_slot={music.Sensors[0].SdkConstant} bubbles={bubbles} combat={placed.CombatSection} bank_bytes={built.Read(music.BankTag).Length}");
        }
        finally
        {
            catalog.SetOverlayEntries([]);
            catalog.ReadOverride = null;
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
        }
    }

    /** A bundle folder with three tone wavs and a bundle.json whose music maps two bubbles and a combat track. */
    private static string Bundle(string folder)
    {
        Directory.CreateDirectory(Path.Combine(folder, "music"));
        foreach (var (name, seconds, _, _) in Tracks)
        {
            byte[] samples = new byte[seconds * Rate * 4];
            double pitch = name switch { "zone_a" => 220, "zone_b" => 330, _ => 440 };
            for (int frame = 0; frame < seconds * Rate; frame++)
            {
                short value = (short)(8000 * Math.Sin(2 * Math.PI * pitch * frame / Rate));
                BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(frame * 4), value);
                BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(frame * 4 + 2), value);
            }
            PcmWave.Write(Path.Combine(folder, "music", name + ".wav"), Rate, 2, samples);
        }
        string tracks = string.Join(",", Tracks.Select(track =>
            $$"""{ "name": "{{track.Name}}", "wav": "music/{{track.Name}}.wav", "loop_start": {{track.Start}}, "loop_end": {{track.End}} }"""));
        File.WriteAllText(Path.Combine(folder, "bundle.json"), $$"""
            { "schema_version": 0, "name": "musicbed", "bubbles": ["bubble_a", "bubble_b", "bubble_c"],
              "music": { "tracks": [{{tracks}}], "bubbles": { "bubble_a": "zone_a", "bubble_b": "zone_b" }, "combat": "battle" } }
            """);
        return folder;
    }
}
