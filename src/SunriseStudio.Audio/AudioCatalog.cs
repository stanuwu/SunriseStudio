using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Audio;

public sealed record AudioMedia(uint Id, uint? Tag, string Name, int Size, byte[]? Embedded = null);
public sealed record AudioBankObject(byte Type, uint Id, byte[] Data);
public sealed record AudioBank(uint Tag, uint Id, uint Version, byte[] Bytes,
    IReadOnlyList<AudioBankObject> Objects, IReadOnlyList<AudioMedia> EmbeddedMedia);
public sealed record AudioDocument(uint Tag, string Name, string Kind, uint? EventId, uint? RelatedEventId,
    AudioBank? Bank, IReadOnlyList<AudioMedia> Media, IReadOnlyList<string> Diagnostics);

/** Package event links and Wwise chunk boundaries are checked before exporting any media. */
public sealed class AudioCatalog(PackageCatalog catalog, Func<uint, string?>? names = null)
{
    public const uint EventClass = SWwiseEvent.Class;
    public static bool Supports(PackageEntry entry) => entry.Reference == EventClass
        || entry.Type == 26 && entry.SubType is 4 or 5 or 6;

    /** Reads an event, bank or stream with its linked bank and media; soft problems become diagnostics. */
    public AudioDocument Read(uint tag)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || !Supports(entry))
            throw new NotSupportedException("Choose an audio event, bank or stream.");
        string name = names?.Invoke(tag) ?? catalog.GetName(tag) ?? $"Audio {tag:X8}";
        if (entry.Type == 26 && entry.SubType == 6)
            return new(tag, name, "Stream", null, null, null, [new(entry.Reference, tag, name, entry.Size)], []);
        byte[] bytes = catalog.Read(tag);
        var diagnostics = new List<string>();
        var media = new List<AudioMedia>();
        uint? eventId = null, related = null;
        uint bankTag = tag;
        if (entry.Reference == EventClass)
        {
            if (!IsEvent(bytes)) throw new InvalidDataException("Audio event header is invalid.");
            eventId = EventField<uint>(bytes, nameof(SWwiseEvent.WwiseEventId));
            related = EventField<uint>(bytes, nameof(SWwiseEvent.RelatedEventId));
            bankTag = EventField<uint>(bytes, nameof(SWwiseEvent.SoundBank));
            var handles = EventField<TagArray<TagReferenceElement>>(bytes, nameof(SWwiseEvent.StreamHandles));
            long count = handles.Count, header = MediaHeader(handles);
            if (!(count == 0 && handles.Relative == 0) && !MediaFits(bytes, count, header))
                throw new InvalidDataException("Audio event media array is invalid.");
            for (int i = 0; i < count; i++)
            {
                uint mediaTag = MediaTag(bytes, header, i);
                if (!catalog.TryGetEntry(mediaTag, out var stream) || stream.Type != 26 || stream.SubType != 6)
                    throw new InvalidDataException($"Event media {mediaTag:X8} is not an installed audio stream.");
                media.Add(new(stream.Reference, mediaTag, names?.Invoke(mediaTag) ?? $"Media {stream.Reference}", stream.Size));
            }
        }
        if (!catalog.TryGetEntry(bankTag, out var bankEntry) || bankEntry.Type != 26 || bankEntry.SubType is not (4 or 5))
            throw new InvalidDataException("The event has no supported package bank link.");
        var bank = ReadBank(bankTag, catalog.Read(bankTag));
        if (bank.Id != bankEntry.Reference) throw new InvalidDataException("Package and Wwise bank identities differ.");
        if (eventId is uint selectedId && !bank.Objects.Any(value => value.Type == 4 && value.Id == selectedId))
            diagnostics.Add($"Event {selectedId} is not present in the linked bank.");
        media.AddRange(bank.EmbeddedMedia.Where(value => !media.Any(existing => existing.Id == value.Id)));
        if (entry.Reference != EventClass)
        {
            if (bank.Version == 113)
            {
                var sources = catalog.Entries.Where(value => value.Type == 26 && value.SubType == 6)
                    .GroupBy(value => value.Reference).ToDictionary(group => group.Key, group => group.ToArray());
                var declared = DeclaredBankMedia(bankTag, bankEntry.PackageId);
                var mediaIds = AudioSources.Read(bank).Select(value => (Id: value.MediaId, value.External)).Distinct();
                foreach (var source in mediaIds)
                {
                    uint id = source.Id;
                    if (media.Any(value => value.Id == id && (value.Tag.HasValue || !source.External))) continue;
                    var candidates = sources.GetValueOrDefault(id) ?? [];
                    if (declared.TryGetValue(id, out var linked)) candidates = candidates.Where(candidate => linked.Contains(candidate.TagHash)).ToArray();
                    if (candidates.Length == 1)
                    {
                        var stream = candidates[0];
                        media.RemoveAll(value => value.Id == id);
                        media.Add(new(id, stream.TagHash, names?.Invoke(stream.TagHash) ?? $"Media {id}", stream.Size));
                    }
                    else diagnostics.Add(candidates.Length == 0 ? $"Missing media {id}." : $"Media {id} has multiple package sources; open its event.");
                }
            }
            else diagnostics.Add($"External media lookup for Wwise bank version {bank.Version} is not supported.");
        }
        return new(tag, name, entry.Reference == EventClass ? "Event" : "Bank", eventId, related, bank,
            media.DistinctBy(value => (value.Id, value.Tag)).ToArray(), diagnostics);
    }

    public byte[] ReadMedia(AudioMedia media) => media.Embedded ?? catalog.Read(media.Tag
        ?? throw new InvalidDataException("Media has no package source."));

    public IReadOnlyList<AudioBank> ReadInitBanks() => catalog.Entries.Where(entry => entry.Type == 26 && entry.SubType == 4)
        .Select(entry => ReadBank(entry.TagHash, catalog.Read(entry.TagHash))).ToArray();

    /** Only a typed event's explicit bank and media links disambiguate duplicate media IDs. */
    private Dictionary<uint, HashSet<uint>> DeclaredBankMedia(uint bankTag, ushort package)
    {
        var result = new Dictionary<uint, HashSet<uint>>();
        foreach (var entry in catalog.Entries.Where(entry => entry.PackageId == package
            && entry.Type is 8 or 16 && entry.Reference == EventClass))
        {
            byte[] bytes = catalog.Read(entry.TagHash);
            if (!IsEvent(bytes) || EventField<uint>(bytes, nameof(SWwiseEvent.SoundBank)) != bankTag) continue;
            var handles = EventField<TagArray<TagReferenceElement>>(bytes, nameof(SWwiseEvent.StreamHandles));
            // The count is unsigned here; a top bit overflows.
            long count = handles.Count >= 0 ? handles.Count : throw new OverflowException();
            long header = MediaHeader(handles);
            if (count == 0) continue;
            if (!MediaFits(bytes, count, header)) throw new InvalidDataException("Linked event media array is invalid.");
            for (int i = 0; i < count; i++)
            {
                uint tag = MediaTag(bytes, header, i);
                if (!catalog.TryGetEntry(tag, out var media) || media.Type != 26 || media.SubType != 6)
                    throw new InvalidDataException("Linked event media has the wrong package type.");
                if (!result.TryGetValue(media.Reference, out var tags)) result[media.Reference] = tags = [];
                tags.Add(tag);
            }
        }
        return result;
    }

    /** Parses a Wwise bank file; the package entry it came from is `tag`. */
    public static AudioBank ReadBank(uint tag, byte[] bytes) => WwiseSoundBank.Read(tag, bytes);

    /** An event member read in place; the event only has to reach the member's end. */
    private static T EventField<T>(byte[] bytes, string member) where T : unmanaged =>
        TagView.Member<SWwiseEvent, T>(bytes, 0, member);

    /** True when the event reaches through its media array and its length word matches the bytes. */
    private static bool IsEvent(byte[] bytes) => TagView.FitsThrough<SWwiseEvent>(bytes, 0, nameof(SWwiseEvent.StreamHandles))
        && EventField<ulong>(bytes, nameof(SWwiseEvent.TagByteLength)) == (ulong)bytes.Length;

    /** The media array header offset; a relative word that overflows throws. */
    private static long MediaHeader(TagArray<TagReferenceElement> handles) =>
        checked(TagView.FieldOffset<SWwiseEvent>("StreamHandles.Relative") + handles.Relative);

    /** True when `count` tag references, at most 100000, sit after a marked header that repeats the count. */
    private static bool MediaFits(byte[] bytes, long count, long header)
    {
        var view = new TagArrayHeader(bytes, header);
        return count is >= 0 and <= 100000 && header >= 4 && header <= bytes.Length - TagArray.HeaderSize && TagArray.MarkerAt(bytes, header)
            && view.Count == count && view.ElementClass == TagReferenceElement.Class && view.RowsFit(Unsafe.SizeOf<TagReferenceElement>());
    }

    private static uint MediaTag(byte[] bytes, long header, int index) => TagView.At<TagReferenceElement>(bytes,
        checked((int)new TagArrayHeader(bytes, header).DataOffset + index * Unsafe.SizeOf<TagReferenceElement>())).Tag;
}
