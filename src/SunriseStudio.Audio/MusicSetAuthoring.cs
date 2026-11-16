using System.Globalization;
using System.Text.Json;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Audio;

/**
 * A music set as `music.json` describes it, waves loaded.
 * @param TemplateEvent The installed music event tag whose bank is copied.
 */
public sealed record MusicSet(uint TemplateEvent, IReadOnlyList<MusicTheme> Themes);

/**
 * A new music event, its bank and the sensor rows that select each theme.
 * @param Event The event tag bytes; they name the new bank.
 * @param Sections One row per theme, in theme order; section i selects theme i.
 * @param BankEntry The installed bank's package entry, for its type word.
 * @param EventEntry The installed event's package entry, for its type word.
 */
public sealed record AuthoredMusicSet(byte[] Event, AuthoredMusicBank Bank, IReadOnlyList<MusicSensorSection> Sections,
    uint BankSource, PackageEntry BankEntry, PackageEntry EventEntry);

/** Reads `music.json` and turns a music set into an event, a bank and sensor rows. */
public static class MusicSetAuthoring
{
    public const string FileName = "music.json";
    /** The draft schema; A13 freezes it. */
    public const int SchemaVersion = 0;

    /**
     * Reads `music.json` and the waves it names from one folder.
     * @param stateChoices The template's state values; a theme without a `state` takes the next unused one.
     */
    public static MusicSet Read(string directory, Func<uint, IReadOnlyList<uint>>? stateChoices = null)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, FileName)));
        var root = document.RootElement;
        if (root.TryGetProperty("version", out var version) && version.GetInt32() != SchemaVersion)
            throw new InvalidDataException($"{FileName} has version {version.GetInt32()}; this build reads {SchemaVersion}.");
        uint template = Number(root.GetProperty("template_event"));
        var free = new Queue<uint>(stateChoices?.Invoke(template) ?? []);
        var themes = new List<MusicTheme>();
        foreach (var item in root.GetProperty("themes").EnumerateArray())
        {
            string name = item.GetProperty("name").GetString() ?? throw new InvalidDataException("A theme has no name.");
            string file = item.GetProperty("file").GetString() ?? throw new InvalidDataException($"Theme {name} has no file.");
            string full = Path.GetFullPath(Path.Combine(directory, file));
            if (!full.StartsWith(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Theme {name} names a file outside its folder.");
            var wave = PcmWave.Read(full);
            long frames = MusicBankAuthoring.Frames(wave);
            long start = item.TryGetProperty("loop_start", out var a) ? a.GetInt64() : 0;
            long end = item.TryGetProperty("loop_end", out var b) ? b.GetInt64() : frames;
            uint state = item.TryGetProperty("state", out var s) ? Number(s)
                : free.Count > 0 ? free.Dequeue() : throw new InvalidDataException($"Theme {name} has no state and the template has none left.");
            themes.Add(new(name, wave, start, end, state));
        }
        return new(template, themes);
    }

    /**
     * Copies the set's template event and bank around its themes.
     * @param name Unique lower-case name; the new bank and event IDs derive from it.
     * @param bankTag The tag the new bank will take; the new event names it.
     * @param eventTag The tag the new event will take; the sensor rows name it.
     */
    public static AuthoredMusicSet Author(PackageCatalog catalog, MusicSet set, string name, uint bankTag, uint eventTag)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        byte[] template = catalog.Read(set.TemplateEvent);
        if (!catalog.TryGetEntry(set.TemplateEvent, out var eventEntry) || eventEntry.Reference != AudioCatalog.EventClass
            || !TagView.FitsThrough<SWwiseEvent>(template, 0, nameof(SWwiseEvent.StreamHandles))
            || Field<ulong>(template, nameof(SWwiseEvent.TagByteLength)) != (ulong)template.Length)
            throw new InvalidDataException($"{set.TemplateEvent:X8} is not an audio event.");
        uint bankSource = Field<uint>(template, nameof(SWwiseEvent.SoundBank));
        if (!catalog.TryGetEntry(bankSource, out var bankEntry)) throw new InvalidDataException("The event's bank is missing.");
        var bank = AudioCatalog.ReadBank(bankSource, catalog.Read(bankSource));
        var built = MusicBankAuthoring.Build(bank, Field<uint>(template, nameof(SWwiseEvent.WwiseEventId)), name, set.Themes);
        byte[] authored = template.ToArray();
        Edit<uint>(authored, nameof(SWwiseEvent.WwiseEventId)) = built.PlayEventId;
        // A related event names an event of the template bank; the new bank has its own IDs.
        Edit<uint>(authored, nameof(SWwiseEvent.RelatedEventId)) = StringHash.Empty;
        Edit<uint>(authored, nameof(SWwiseEvent.SoundBank)) = bankTag;
        // The new media sits inside the bank, so the event lists no streamed media.
        Edit<TagArray<TagReferenceElement>>(authored, nameof(SWwiseEvent.StreamHandles)) = default;
        var sections = built.Themes.Select(theme => new MusicSensorSection(eventTag, built.StateGroup, theme.State)).ToArray();
        return new(authored, built, sections, bankSource, bankEntry, eventEntry);
    }

    /** A JSON number, or a string in decimal or 0x hex. */
    private static uint Number(JsonElement value) => value.ValueKind == JsonValueKind.Number ? value.GetUInt32()
        : value.GetString() is { } text && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : uint.Parse(value.GetString() ?? "", CultureInfo.InvariantCulture);

    private static T Field<T>(byte[] bytes, string member) where T : unmanaged =>
        TagView.At<T>(bytes, TagView.FieldOffset<SWwiseEvent>(member));

    private static ref T Edit<T>(byte[] bytes, string member) where T : unmanaged =>
        ref TagView.Edit<T>(bytes, TagView.FieldOffset<SWwiseEvent>(member));
}
