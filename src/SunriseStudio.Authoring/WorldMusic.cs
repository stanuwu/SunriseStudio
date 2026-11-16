using System.Buffers.Binary;
using SunriseStudio.Audio;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Graph;
using SunriseStudio.Tags.Tft;
using SunriseStudio.Tags.Views;
using SunriseStudio.World;

namespace SunriseStudio.Authoring;

/**
 * One music sensor slot of a world, rewritten to play the project's music.
 * @param OwnerTag The installed authored object the slot belongs to; SDK slot ids name it.
 * @param SdkConstant The slot's constant in the source mission's SDK module, or null when its name is not m_music_sensor.
 * @param ConfigFile The world's own copy of the slot's config.
 */
public sealed record WorldMusicSensor(string OwnerFile, uint OwnerTag, ushort SlotIndex, uint BubbleIndex, uint NameHash,
    uint ConfigTag, string ConfigFile, string? SdkConstant);

/**
 * What a script needs to switch the music.
 * @param Sections Theme name to the section index set_music_section takes.
 */
public sealed record WorldMusicResult(uint BankTag, uint EventTag, AuthoredMusicSet Set, IReadOnlyList<WorldMusicSensor> Sensors,
    IReadOnlyDictionary<string, int> Sections);

/** Puts a music set in a project destination: a new bank and event, and every music sensor of the world pointed at them. */
public static partial class WorldMusic
{
    // Music sensors are slot type 11.
    private const ushort MusicSlotType = 11;
    private static readonly int NameField = TagView.FieldOffset<ObjectSlot>(nameof(ObjectSlot.NameHash));
    private static readonly uint SensorName = StringBankWriter.Hash("m_music_sensor");

    /**
     * Writes the set's bank and event as project assets, then gives the world its own copy of every
     * music sensor config it reaches, with one section row per theme.
     * @param name Unique lower-case name; the bank and event IDs derive from it.
     * @param packageId The project package that takes the bank and the event.
     */
    public static WorldMusicResult Apply(StudioProject project, PackageCatalog catalog, RegistryIndex schema, string destination,
        MusicSet set, string name, ushort packageId, CancellationToken token = default)
    {
        var world = WorldFiles.Load(project.ContentDirectory, destination);
        var found = Sensors(world, catalog, token);
        if (found.Count == 0) throw new InvalidDataException($"The world {destination} has no music sensor slot.");

        int first = project.NextEntry(packageId);
        if (first + 1 >= TagHashes.EntriesPerPackage) throw new InvalidDataException("The music set needs two free package entries.");
        uint bankTag = TagHashes.Make(packageId, first), eventTag = TagHashes.Make(packageId, first + 1);
        var authored = MusicSetAuthoring.Author(catalog, set, $"{destination}/{name}", bankTag, eventTag);
        project.AddAssets(
        [
            new($"{destination}.{name}.music_bank", authored.Bank.Bytes,
                new(packageId, authored.Bank.BankId, authored.BankEntry.TypeInfo, null, first), authored.BankSource),
            new($"{destination}.{name}.music_event", authored.Event,
                new(packageId, AudioCatalog.EventClass, authored.EventEntry.TypeInfo, null, first + 1), set.TemplateEvent),
        ], catalog);

        var sensors = new List<WorldMusicSensor>();
        foreach (var group in found.GroupBy(row => row.Evidence.ConfigTag))
        {
            foreach (var (owner, evidence, _) in group)
            {
                string from = owner.Name;
                foreach (var hop in evidence.Path)
                {
                    string next = world.Files.FirstOrDefault(file => file.SourceTag == hop.Tag)?.Name ?? Copy(world, schema, catalog, destination, hop.Tag);
                    Repoint(world, from, hop.Tag, next);
                    from = next;
                }
            }
            var config = world.Files.Single(file => file.SourceTag == group.Key);
            world.Replace(WithSections(config, schema, authored.Sections, eventTag));
            foreach (var (owner, evidence, nameHash) in group)
                sensors.Add(new(owner.Name, owner.SourceTag!.Value, evidence.SlotIndex, evidence.BubbleIndex, nameHash, group.Key, config.Name,
                    nameHash == SensorName ? $"M_MUSIC_SENSOR_{owner.SourceTag!.Value:X8}" : null));
        }
        world.SaveChanged(project.ContentDirectory);
        var sections = authored.Bank.Themes.Select((theme, index) => (theme.Name, index)).ToDictionary(x => x.Name, x => x.index, StringComparer.Ordinal);
        return new(bankTag, eventTag, authored, sensors, sections);
    }

    /** Every music sensor slot the world's authored objects list, with its owner file and name hash. */
    public static IReadOnlyList<(TftFile Owner, ComponentSlotEvidence Evidence, uint NameHash)> Sensors(WorldFiles world, PackageCatalog catalog,
        CancellationToken token = default)
    {
        var found = new List<(TftFile, ComponentSlotEvidence, uint)>();
        foreach (var owner in world.Files.Where(file => file.ClassId == SAuthoredObject.Class && file.SourceTag is not null).ToList())
        {
            byte[] bytes = catalog.Read(owner.SourceTag!.Value);
            foreach (var slot in ComponentSlotCatalog.OwnerSlots(catalog, owner.SourceTag!.Value, token).Where(slot => slot.SlotType == MusicSlotType))
                found.Add((owner, slot, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(slot.SlotOffset + NameField))));
        }
        return found;
    }

    /**
     * The config with the new section rows and a layout read from them. Each row's event tag is a
     * reference to the project event, so the build writes it.
     */
    private static TftFile WithSections(TftFile config, RegistryIndex schema, IReadOnlyList<MusicSensorSection> sections, uint eventTag)
    {
        byte[] data = MusicSensor.Write(config.Data, sections);
        var rows = MusicSensor.RowOffsets(data);
        var starts = rows.ToHashSet();
        // A tag reference holds the 0xFFFFFFFF placeholder; the build writes the tag.
        foreach (int at in rows) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), uint.MaxValue);
        byte flags = config.References.FirstOrDefault(reference => reference.Kind == TftReferenceKind.TagRef).Flags;
        var references = config.References.Where(reference => !starts.Contains(reference.Offset)).ToList();
        references.AddRange(rows.Select(at => new TftReference(at, TftReferenceKind.TagRef, flags, TftNames.Installed(eventTag))));
        references.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        // The layout must match the new bytes, or the build takes the file for an old registry.
        return new()
        {
            Name = config.Name, TagType = config.TagType, GameName = config.GameName, ClassId = config.ClassId, TypeInfo = config.TypeInfo,
            BuildHash = config.BuildHash, Layout = TftLayout.Extract(schema, data, config.ClassId), Data = data, References = references,
            Info = config.Info, Meta = config.Meta,
        };
    }

    /** Adds one installed tag as a file of the world, named as the template names its copies. */
    private static string Copy(WorldFiles world, RegistryIndex schema, PackageCatalog catalog, string destination, uint tag)
    {
        string type = WorldTemplate.TypeName(schema, catalog.TryGetEntry(tag, out var entry) ? entry.Reference : 0);
        string name = $"{WorldTemplate.Folder}/{destination}/{type}/{tag:x8}.{type}";
        world.Add(TftClone.Clone([tag], new CatalogTagSource(catalog), schema, _ => name).Single());
        return name;
    }

    /** Turns a file's references to an installed tag into references to a file of the world. */
    private static void Repoint(WorldFiles world, string file, uint installed, string target)
    {
        var source = world[file];
        string raw = TftNames.Installed(installed);
        if (source.References.All(reference => reference.Target != raw)) return;
        world.Replace(With(source, source.Data, source.References.Select(reference => reference.Target == raw ? reference with { Target = target } : reference).ToList()));
    }

    private static TftFile With(TftFile file, byte[] data, IReadOnlyList<TftReference> references) => new()
    {
        Name = file.Name, TagType = file.TagType, GameName = file.GameName, ClassId = file.ClassId, TypeInfo = file.TypeInfo,
        BuildHash = file.BuildHash, Layout = file.Layout, Data = data, References = references, Info = file.Info, Meta = file.Meta,
    };
}
