using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Audio;

/**
 * A new sound event and its bank, built over an installed event.
 * @param Event The event tag bytes; they name the new bank.
 * @param BankSource The installed bank the new one copies.
 * @param BankEntry The installed bank's package entry, for its type word.
 * @param EventEntry The installed event's package entry, for its type word.
 */
public sealed record AuthoredSoundEvent(byte[] Event, AuthoredSoundBank Bank, uint BankSource, PackageEntry BankEntry,
    PackageEntry EventEntry);

/** Makes a sound event that plays a new wave, for a weapon, a prop or any other entity. */
public static class EntitySoundAuthoring
{
    /**
     * Copies an installed event and its bank so that every sound of the hierarchy plays `wave`.
     * Volumes, positioning, containers and loops stay the template's.
     * @param templateEvent An installed sound event tag.
     * @param name Unique lower-case name; the new bank and event IDs derive from it.
     * @param release The wave only the template's paired event plays, such as a tail; null plays `wave` there too.
     * @param bankTag The tag the new bank will take; the new event names it.
     * @param options Variations for random containers, a wave per switch branch, and the farthest distance.
     */
    public static AuthoredSoundEvent Author(PackageCatalog catalog, uint templateEvent, string name, PcmWave wave, PcmWave? release,
        uint bankTag, WwiseSoundOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        byte[] template = catalog.Read(templateEvent);
        if (!catalog.TryGetEntry(templateEvent, out var eventEntry) || eventEntry.Reference != AudioCatalog.EventClass
            || !TagView.FitsThrough<SWwiseEvent>(template, 0, nameof(SWwiseEvent.StreamHandles))
            || Field<ulong>(template, nameof(SWwiseEvent.TagByteLength)) != (ulong)template.Length)
            throw new InvalidDataException($"{templateEvent:X8} is not an audio event.");
        uint bankSource = Field<uint>(template, nameof(SWwiseEvent.SoundBank));
        if (!catalog.TryGetEntry(bankSource, out var bankEntry)) throw new InvalidDataException("The event's bank is missing.");
        var bank = AudioCatalog.ReadBank(bankSource, catalog.Read(bankSource));
        var built = WwiseSoundBank.Build(bank, Field<uint>(template, nameof(SWwiseEvent.WwiseEventId)),
            Field<uint>(template, nameof(SWwiseEvent.RelatedEventId)), name, wave, release, options);
        byte[] authored = template.ToArray();
        Edit<uint>(authored, nameof(SWwiseEvent.WwiseEventId)) = built.EventId;
        Edit<uint>(authored, nameof(SWwiseEvent.RelatedEventId)) = built.RelatedEventId;
        Edit<uint>(authored, nameof(SWwiseEvent.SoundBank)) = bankTag;
        // The new media sits inside the bank, so the event lists no streamed media.
        Edit<TagArray<TagReferenceElement>>(authored, nameof(SWwiseEvent.StreamHandles)) = default;
        return new(authored, built, bankSource, bankEntry, eventEntry);
    }

    /** An event member read in place; the event only has to reach the member's end. */
    private static T Field<T>(byte[] bytes, string member) where T : unmanaged =>
        TagView.At<T>(bytes, TagView.FieldOffset<SWwiseEvent>(member));

    private static ref T Edit<T>(byte[] bytes, string member) where T : unmanaged =>
        ref TagView.Edit<T>(bytes, TagView.FieldOffset<SWwiseEvent>(member));
}
