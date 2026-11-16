using System.Globalization;
using System.Text.Json.Nodes;
using SunriseStudio.Audio;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using SunriseStudio.World;

namespace SunriseStudio.Authoring;

/**
 * The bundle's music in place.
 * @param BubbleSections Bubble name hash to the section of its zone track.
 * @param CombatSection The section of the combat track, or null when the bundle names none.
 */
public sealed record PlacedBundleMusic(WorldMusicResult Music, IReadOnlyDictionary<uint, int> BubbleSections, int? CombatSection);

public static partial class WorldMusic
{
    /**
     * Reads the bundle's `music` and applies it. `tracks` lists name, wav, loop_start and loop_end;
     * `bubbles` maps a bundle bubble to a track; `combat` names a track; `like` is the installed music
     * event to copy, else the first one the world's sensors name that has enough states.
     * @param bundle The parsed bundle.json.
     * @return Null when the bundle has no music.
     */
    public static PlacedBundleMusic? PlaceBundleMusic(StudioProject project, PackageCatalog catalog, RegistryIndex schema, string destination,
        string bundleFolder, JsonObject bundle, ushort packageId, CancellationToken token = default)
    {
        if (bundle["music"] is not JsonObject music) return null;
        var tracks = (music["tracks"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        if (tracks.Length == 0) throw new InvalidDataException("The bundle's music has no tracks.");
        var world = WorldFiles.Load(project.ContentDirectory, destination);
        uint template = music["like"] is { } like ? Hex((string)like!) : DefaultTemplate(world, catalog, tracks.Length, token);
        var (_, states) = TemplateStates(catalog, template);
        var free = new Queue<uint>(states);
        var themes = new List<MusicTheme>();
        foreach (var track in tracks)
        {
            string name = (string)track["name"]!;
            var wave = PcmWave.Read(Path.Combine(bundleFolder, (string)track["wav"]!));
            long frames = MusicBankAuthoring.Frames(wave);
            uint state = track["state"] is { } given ? Hex((string)given!)
                : free.Count > 0 ? free.Dequeue() : throw new InvalidDataException($"Music track {name}: the template has no state left.");
            themes.Add(new(name, wave, (long?)track["loop_start"] ?? 0, (long?)track["loop_end"] ?? frames, state));
        }
        var result = Apply(project, catalog, schema, destination, new(template, themes), "music", packageId, token);

        var names = ((JsonArray)bundle["bubbles"]!).Select(name => (string)name!).ToArray();
        var live = WorldContent.LiveBubbles(WorldFiles.Load(project.ContentDirectory, destination));
        int Section(string track) => result.Sections.TryGetValue(track, out int index) ? index
            : throw new InvalidDataException($"The bundle's music names track {track}, which it does not list.");
        var bubbles = new Dictionary<uint, int>();
        foreach (var (bubble, track) in music["bubbles"] as JsonObject ?? new JsonObject())
        {
            int at = Array.IndexOf(names, bubble);
            if (at < 0 || at >= live.Count) throw new InvalidDataException($"The bundle's music names bubble {bubble}, which it does not place.");
            bubbles[live[at]] = Section((string)track!);
        }
        int? combat = music["combat"] is { } fight ? Section((string)fight!) : null;
        return new(result, bubbles, combat);
    }

    /** The template event's state group and states; an event without a theme switch throws. */
    private static (uint Group, IReadOnlyList<uint> States) TemplateStates(PackageCatalog catalog, uint template)
    {
        if (!catalog.TryGetEntry(template, out var entry) || entry.Reference != AudioCatalog.EventClass)
            throw new InvalidDataException($"{template:X8} is not an installed audio event.");
        byte[] bytes = catalog.Read(template);
        uint bankTag = TagView.Member<SWwiseEvent, uint>(bytes, 0, nameof(SWwiseEvent.SoundBank));
        var bank = AudioCatalog.ReadBank(bankTag, catalog.Read(bankTag));
        return MusicBankAuthoring.StateChoices(bank, TagView.Member<SWwiseEvent, uint>(bytes, 0, nameof(SWwiseEvent.WwiseEventId)));
    }

    /** The first music event the world's sensor rows name whose theme switch lists at least `count` states. */
    private static uint DefaultTemplate(WorldFiles world, PackageCatalog catalog, int count, CancellationToken token)
    {
        foreach (uint config in Sensors(world, catalog, token).Select(row => row.Evidence.ConfigTag).Distinct())
            foreach (var row in MusicSensor.Read(config, catalog.Read(config)).Sections.Where(row => row.EventTag != uint.MaxValue))
            {
                try
                {
                    if (TemplateStates(catalog, row.EventTag).States.Count >= count) return row.EventTag;
                }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException) { }
            }
        throw new InvalidDataException($"No music event the world's sensors name has {count} states; give the bundle's music a `like` event.");
    }

    private static uint Hex(string text) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? uint.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : uint.Parse(text, CultureInfo.InvariantCulture);
}
