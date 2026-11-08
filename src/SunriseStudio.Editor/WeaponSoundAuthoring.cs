using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Animation;
using SunriseStudio.Audio;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.Tags;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Editor;

/**
 * One sound event the weapon plays, the records from an owned one down to its holder, and whether the event is the project's.
 * Detail names the holder, offset and event in hex.
 */
public sealed record WeaponSoundSlot(uint EventTag, string Label, IReadOnlyList<uint> Path, bool Owned, string Detail = "");

/** New assets, edits to base records, and edits to this project's own earlier sound assets by asset ID. */
public sealed record PreparedWeaponSounds(IReadOnlyList<ProjectAssetAddition> Assets,
    IReadOnlyDictionary<uint, byte[]> Changes, IReadOnlyList<string> Summary, IReadOnlyDictionary<string, byte[]> AddedChanges);

/** A weapon sound is replaced by a new bank and event; the records naming the old event are copied. */
public static class WeaponSoundAuthoring
{
    private const uint EntityClass = SEntity.Class, ConfigClass = SEntityConfig.Class, WrapperClass = SArtWrapper.Class,
        TableClass = SAudioEventTable.Class;
    private static readonly HashSet<uint> Followed = [EntityClass, ConfigClass, WrapperClass, TableClass];

    /**
     * Lists the sound events reachable from the weapon's own records through the base weapon's
     * package. In a selector config only the weapon's own row is followed. Each slot keeps one path.
     */
    public static IReadOnlyList<WeaponSoundSlot> Find(StudioProject project, PackageCatalog catalog, uint weaponTag)
    {
        var owned = OwnedTags(project);
        var art = WeaponArchetypeAuthoring.Read(catalog, catalog.Read(weaponTag));
        uint gearKey = WeaponArchetypeAuthoring.GearKey(catalog, art);
        if (!catalog.TryGetEntry(WeaponPresentationAuthoring.GameplayTemplate, out var template))
            throw new InvalidDataException("The template weapon is missing.");
        var seeds = art.Variants.Select(part => part.EntityTag).Prepend(art.EntityTag).ToList();
        foreach (uint entity in seeds.ToArray())
            if (catalog.TryGetEntry(entity, out var seed) && seed.Reference == EntityClass)
                seeds.AddRange(WeaponConfigEdits.ConstructionConfigs(catalog.Read(entity)));
        var paths = new Dictionary<uint, List<uint>>();
        var queue = new Queue<uint>();
        foreach (uint tag in seeds.Where(owned.Contains).Distinct()) { paths[tag] = [tag]; queue.Enqueue(tag); }
        var slots = new List<WeaponSoundSlot>();
        while (queue.TryDequeue(out uint holder))
        {
            if (!catalog.TryGetEntry(holder, out var entry) || !Followed.Contains(entry.Reference)) continue;
            byte[] bytes = catalog.Read(holder);
            var seen = new HashSet<uint>();
            var foreign = entry.Reference == ConfigClass ? ForeignRows(bytes, gearKey) : [];
            int next = 0;
            foreach (var reference in TagReferences(bytes, entry.Reference).OrderBy(reference => reference.Offset))
            {
                int at = reference.Offset;
                while (next < foreign.Count && foreign[next].End <= at) next++;
                if (next < foreign.Count && at >= foreign[next].Start) continue;
                uint value = (uint)reference.Value;
                if (!TagHashes.IsTag(value) || !seen.Add(value) || !catalog.TryGetEntry(value, out var target)) continue;
                if (target.Reference == AudioCatalog.EventClass)
                    slots.Add(new(value, (entry.Reference == TableClass ? Label(bytes, value) : null) ?? "", paths[holder], owned.Contains(value),
                        $"{holder:X8} +0x{at:X} event {value:X8}"));
                else if (!paths.ContainsKey(value) && Followed.Contains(target.Reference)
                    && (owned.Contains(value) || target.PackageId == template.PackageId))
                {
                    // An owned record starts a new path; only base records on a path are copied.
                    paths[value] = owned.Contains(value) ? [value] : [.. paths[holder], value];
                    queue.Enqueue(value);
                }
            }
        }
        var numbers = new Dictionary<uint, int>();
        string Number(uint tag)
        {
            if (!numbers.TryGetValue(tag, out int number)) numbers[tag] = number = numbers.Count + 1;
            return $"Sound {number}";
        }
        return slots.DistinctBy(slot => (slot.EventTag, slot.Path[^1]))
            .Select(slot => slot.Label.Length != 0 ? slot : slot with { Label = Number(slot.EventTag) }).ToArray();
    }

    /**
     * Rows of an entity config's weapon or HUD resource that another gear key selects, sorted by start.
     * A config with no row for the key falls back to its default row, so nothing is excluded then.
     */
    private static List<(int Start, int End)> ForeignRows(byte[] bytes, uint key)
    {
        var result = new List<(int, int)>();
        void Rows<T>(int field, uint rowClass, Func<int, uint> rowKey) where T : unmanaged
        {
            if (!TagView.Fits<TagArray<T>>(bytes, field)) return;
            var view = new TagArray(bytes, field);
            // The header is read even when the relative word is zero.
            var header = view.Header;
            long count = view.Count;
            if (count is <= 0 or > 1024 || header.Offset < 4 || header.Offset > bytes.Length - TagArray.HeaderSize
                || header.ElementClass != rowClass) return;
            int data = (int)header.DataOffset, stride = Unsafe.SizeOf<T>();
            if (!view.RowsFit(stride)) return;
            var rows = Enumerable.Range(0, (int)count).Select(index => data + index * stride).ToArray();
            if (!rows.Any(row => rowKey(row) == key)) return;
            result.AddRange(rows.Where(row => rowKey(row) != key).Select(row => (row, row + stride)));
        }
        // The rows hang off the resource the config's definition pointer names.
        if (!TagView.FitsThrough<SEntityConfig>(bytes, 0, nameof(SEntityConfig.Definition))) return result;
        ref readonly var pointer = ref TagView.At<RelativePointer<Unknown>>(bytes,
            TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition)));
        long resource = TagView.OffsetOf(bytes, in pointer) + pointer.Relative;
        if (pointer.IsNull || resource is < 4 or >= int.MaxValue || resource > bytes.Length - 4) return result;
        uint? resourceClass = TagView.ClassAt(bytes, (int)resource);
        if (resourceClass == SWeaponResource.Class)
            Rows<SWeaponVariantRow>((int)resource + TagView.FieldOffset<SWeaponResource>(nameof(SWeaponResource.PresentationRows)),
                SWeaponVariantRow.Class, row => TagView.At<SWeaponVariantRow>(bytes, row).GearSelectorKey0x44a0b334Base);
        else if (resourceClass == SWeaponHudConfigResource.Class)
            Rows<SWeaponFiringRow>((int)resource + TagView.FieldOffset<SWeaponHudConfigResource>(nameof(SWeaponHudConfigResource.FiringRows448Byte)),
                SWeaponFiringRow.Class, row => TagView.At<SWeaponFiringRow>(bytes, row).GearKey);
        result.Sort();
        return result;
    }

    /** The sound's name: the audio path of the table's first frame event that targets `eventTag`; null when none has one. */
    private static string? Label(byte[] table, uint eventTag)
    {
        int stride = Unsafe.SizeOf<AnimationTagEvent>();
        if (!TagView.FitsThrough<SAudioEventTable>(table, 0, nameof(SAudioEventTable.SharedEvents))
            || !TagArray.TryChecked(table, TagView.FieldOffset<SAudioEventTable>(nameof(SAudioEventTable.SharedEvents)),
                C8080902f.Class, Unsafe.SizeOf<C8080902f>(), out var shared)) return null;
        for (int index = 0; index < shared.Count; index++)
        {
            int frames = shared.Data + index * Unsafe.SizeOf<C8080902f>() + TagView.FieldOffset<C8080902f>(nameof(C8080902f.FrameEvents));
            if (!TagArray.TryChecked(table, frames, AnimationTagEvent.Class, stride, out var events)) continue;
            for (int row = 0; row < events.Count; row++)
            {
                ref readonly var frameEvent = ref TagView.At<AnimationTagEvent>(table, events.Data + row * stride);
                if (frameEvent.EventTarget != eventTag || frameEvent.AudioPath.IsNull) continue;
                long start = TagView.OffsetOf(table, in frameEvent.AudioPath) + frameEvent.AudioPath.Relative;
                if (start < 0 || start >= table.Length) continue;
                int end = Array.IndexOf(table, (byte)0, (int)start);
                if (end > start && end - start < 400)
                {
                    string path = System.Text.Encoding.ASCII.GetString(table, (int)start, end - (int)start);
                    return Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
                }
            }
        }
        return null;
    }

    /**
     * Replaces each chosen event with a new bank and event of the weapon's own.
     * @param slots The weapon's slots from Find; only records on their paths are retargeted.
     * @param waves Template event tag to the wave that replaces it.
     * @param name Prefix for the new event names; lower case, unique to the weapon.
     * @param releases Template event tag to the wave its paired event plays, such as an automatic tail.
     */
    public static PreparedWeaponSounds Prepare(StudioProject project, PackageCatalog catalog, uint weaponTag,
        IReadOnlyList<WeaponSoundSlot> slots, IReadOnlyDictionary<uint, PcmWave> waves, string name,
        ushort packageId, int firstEntry, IReadOnlyDictionary<uint, PcmWave>? releases = null)
    {
        var owned = OwnedTags(project);
        var assets = new List<ProjectAssetAddition>();
        var changes = new Dictionary<uint, byte[]>();
        var addedChanges = new Dictionary<string, byte[]>();
        var summary = new List<string>();
        var clones = new Dictionary<uint, uint>();
        int cursor = firstEntry;
        uint Next()
        {
            if (cursor >= TagHashes.EntriesPerPackage || catalog.ContainsTag(TagHashes.Make(packageId, cursor)))
                throw new InvalidDataException("The weapon sounds need free package entries.");
            return TagHashes.Make(packageId, cursor++);
        }
        int Entry(uint tag) => TagHashes.Split(tag).EntryIndex;
        byte[] Read(uint tag) => changes.TryGetValue(tag, out var bytes) ? bytes
            : assets.FirstOrDefault(asset => asset.Native is { } native && native.Tag() == tag)?.Data ?? catalog.Read(tag);
        void Write(uint tag, byte[] bytes)
        {
            int index = assets.FindIndex(asset => asset.Native is { } native && native.Tag() == tag);
            if (index >= 0) assets[index] = assets[index] with { Data = bytes };
            else changes[tag] = bytes;
        }

        // Only this weapon's own records and clips switch to a copy; other weapons keep theirs.
        var candidates = slots.SelectMany(slot => slot.Path).Where(owned.Contains)
            .Concat(WeaponClips(catalog, weaponTag).Where(owned.Contains)).Distinct().ToList();
        uint ClassOf(uint tag) => catalog.TryGetEntry(tag, out var entry) ? entry.Reference
            : assets.FirstOrDefault(asset => asset.Native is { } native && native.Tag() == tag)?.Native!.Reference
                ?? throw new InvalidDataException($"{tag:X8} has no class.");
        void Retarget(uint from, uint to)
        {
            bool constructed = catalog.TryGetEntry(from, out var fromEntry) && fromEntry.Reference == ConfigClass;
            foreach (uint tag in candidates.Concat(clones.Values).Distinct())
            {
                byte[] bytes = Read(tag);
                uint classId = ClassOf(tag);
                if (Count(bytes, classId, from) == 0) continue;
                Write(tag, constructed && classId == EntityClass && WeaponConfigEdits.ConstructionConfigs(bytes).Contains(from)
                    ? WeaponConfigEdits.RetagRoot(bytes, from, to) : Replace(bytes, classId, from, to));
                summary.Add($"retargeted {tag:X8}: {from:X8} -> {to:X8}");
            }
        }

        // Builds a template event's new bank, and the event bytes that name it at bankTag.
        (byte[] Event, AuthoredSoundBank Bank, uint BankSource, PackageEntry BankEntry, PackageEntry EventEntry) Author(
            uint source, PcmWave wave, PcmWave? release, uint bankTag)
        {
            byte[] template = catalog.Read(source);
            if (!catalog.TryGetEntry(source, out var eventEntry) || eventEntry.Reference != AudioCatalog.EventClass
                || !TagView.FitsThrough<SWwiseEvent>(template, 0, nameof(SWwiseEvent.StreamHandles))
                || EventField<ulong>(template, nameof(SWwiseEvent.TagByteLength)) != (ulong)template.Length)
                throw new InvalidDataException($"{source:X8} is not an audio event.");
            uint bankSource = EventField<uint>(template, nameof(SWwiseEvent.SoundBank));
            if (!catalog.TryGetEntry(bankSource, out var bankEntry)) throw new InvalidDataException("The event's bank is missing.");
            var bank = AudioCatalog.ReadBank(bankSource, catalog.Read(bankSource));
            var built = WwiseSoundBank.Build(bank, EventField<uint>(template, nameof(SWwiseEvent.WwiseEventId)),
                EventField<uint>(template, nameof(SWwiseEvent.RelatedEventId)), $"{name}/{source:x8}", wave, release);
            byte[] authored = template.ToArray();
            EditEventField<uint>(authored, nameof(SWwiseEvent.WwiseEventId)) = built.EventId;
            EditEventField<uint>(authored, nameof(SWwiseEvent.RelatedEventId)) = built.RelatedEventId;
            EditEventField<uint>(authored, nameof(SWwiseEvent.SoundBank)) = bankTag;
            // The new media sits inside the bank, so the event lists no streamed media.
            EditEventField<TagArray<TagReferenceElement>>(authored, nameof(SWwiseEvent.StreamHandles)) = default;
            return (authored, built, bankSource, bankEntry, eventEntry);
        }

        // An event this weapon already replaced is rebuilt in place from its template; records keep naming it.
        var replaced = project.AddedAssets.Where(asset => asset.Native?.Reference == AudioCatalog.EventClass && asset.BaseTagHash is not null).ToList();
        foreach (var (source, wave) in waves)
        {
            var release = releases?.GetValueOrDefault(source);
            string seconds = $"({wave.DurationSeconds:F2} s" + (release is null ? ")" : $", release {release.DurationSeconds:F2} s)");
            var previous = replaced.FirstOrDefault(asset => asset.Native!.Tag() == source);
            if (previous is not null)
            {
                uint ownedEvent = previous.Native!.Tag(), ownedBank = EventField<uint>(catalog.Read(ownedEvent), nameof(SWwiseEvent.SoundBank));
                var bankAsset = project.AddedAssets.FirstOrDefault(asset => asset.Native is { } native && native.Tag() == ownedBank)
                    ?? throw new InvalidDataException($"{ownedEvent:X8} does not name a bank of this project.");
                var rebuilt = Author(previous.BaseTagHash!.Value, wave, release, ownedBank);
                if (rebuilt.Bank.BankId != bankAsset.Native!.Reference || bankAsset.BaseTagHash != rebuilt.BankSource)
                    throw new InvalidDataException($"{ownedBank:X8} is not the bank built from {previous.BaseTagHash:X8}.");
                addedChanges[previous.Id] = rebuilt.Event;
                addedChanges[bankAsset.Id] = rebuilt.Bank.Bytes;
                summary.Add($"rebuilt event {ownedEvent:X8} and bank {ownedBank:X8} from {previous.BaseTagHash:X8} {seconds}");
                continue;
            }
            uint bankTag = Next(), eventTag = Next();
            var (authored, built, bankSource, bankEntry, eventEntry) = Author(source, wave, release, bankTag);
            assets.Add(new($"{name}.{source:X8}.bank", built.Bytes,
                new(packageId, built.BankId, bankEntry.TypeInfo, null, Entry(bankTag)), bankSource));
            assets.Add(new($"{name}.{source:X8}.event", authored,
                new(packageId, AudioCatalog.EventClass, eventEntry.TypeInfo, null, Entry(eventTag)), source));
            summary.Add($"event {source:X8} -> {eventTag:X8}, bank {bankSource:X8} -> {bankTag:X8} {seconds}");

            // Copy the base-weapon records on each path to this event.
            foreach (var slot in slots.Where(slot => slot.EventTag == source))
                for (int index = 1; index < slot.Path.Count; index++)
                {
                    uint record = slot.Path[index];
                    if (owned.Contains(record) || clones.ContainsKey(record)) continue;
                    uint copy = Next();
                    catalog.TryGetEntry(record, out var recordEntry);
                    byte[] bytes = catalog.Read(record);
                    bytes = recordEntry.Reference == ConfigClass ? WeaponConfigEdits.RetagConfig(bytes, record, copy).Bytes : bytes.ToArray();
                    if (recordEntry.Reference != ConfigClass && Count(bytes, recordEntry.Reference, record) != 0)
                        throw new InvalidDataException($"{record:X8} names itself and has no checked copy rule.");
                    assets.Add(new($"{name}.{record:X8}", bytes, new(packageId, recordEntry.Reference, recordEntry.TypeInfo, null, Entry(copy)), record));
                    clones[record] = copy;
                    summary.Add($"copied {record:X8} -> {copy:X8}");
                    Retarget(record, copy);
                }
            Retarget(source, eventTag);
        }
        return new(assets, changes, summary, addedChanges);
    }

    /** The clips the weapon's presentation entity plays. */
    private static IEnumerable<uint> WeaponClips(PackageCatalog catalog, uint weaponTag)
    {
        var art = WeaponArchetypeAuthoring.Read(catalog, catalog.Read(weaponTag));
        uint owner = WeaponPresentationReader.Read(catalog, art.EntityTag, WeaponArchetypeAuthoring.GearKey(catalog, art));
        return new EntityAnimationCatalog(catalog).Read(owner).Select(clip => clip.ClipTag);
    }

    private static HashSet<uint> OwnedTags(StudioProject project) =>
        project.AddedAssets.Where(asset => asset.Native is not null).Select(asset => asset.Native!.Tag()).ToHashSet();

    /** The tagref fields and tagptr tags of a record; a record the registry cannot parse is refused. */
    private static IEnumerable<TagReference> TagReferences(byte[] bytes, uint classId)
    {
        var document = TagDocument.Parse(bytes, classId, StudioRegistry.Index);
        if (document.Problems.Count > 0) throw new InvalidDataException($"A 0x{classId:X8} record does not parse: {document.Problems[0]}");
        return document.References.Where(reference => reference.Kind != TagReferenceKind.WideRef);
    }

    /** Offsets of the tagref fields and tagptr tags that name `value`. */
    private static List<int> References(byte[] bytes, uint classId, uint value) =>
        TagReferences(bytes, classId).Where(reference => reference.Value == value).Select(reference => reference.Offset).ToList();

    private static int Count(byte[] bytes, uint classId, uint value) => References(bytes, classId, value).Count;

    private static byte[] Replace(byte[] bytes, uint classId, uint from, uint to)
    {
        var sites = References(bytes, classId, from);
        if (sites.Count == 0) throw new InvalidDataException($"No reference to {from:X8} to replace.");
        byte[] output = bytes.ToArray();
        foreach (int at in sites) Put(output, at, to);
        return output;
    }

    /** An event member read in place; the event only has to reach the member's end. */
    private static T EventField<T>(byte[] bytes, string member) where T : unmanaged =>
        TagView.At<T>(bytes, TagView.FieldOffset<SWwiseEvent>(member));

    private static ref T EditEventField<T>(byte[] bytes, string member) where T : unmanaged =>
        ref TagView.Edit<T>(bytes, TagView.FieldOffset<SWwiseEvent>(member));

    private static void Put(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
}
