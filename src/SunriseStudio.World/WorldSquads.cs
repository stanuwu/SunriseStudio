using System.Buffers.Binary;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Tft;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.World;

/**
 * One squad slot of a world copy.
 * @param OwnerFile The authored object file that lists the slot.
 * @param NameHash Name hash of the slot, as the source activity names it.
 * @param ConfigTag The installed spawner config behind the slot.
 * @param Entities The installed entities the spawner config names, one per distinct member.
 */
public sealed record WorldSquad(string OwnerFile, uint NameHash, uint ConfigTag, IReadOnlyList<uint> Entities);

/**
 * Squad slots of a world copy. A squad's spawner config names the entity each member is built from.
 * Changing that entity gives the world its own copy of the config and of the tags that lead to it.
 */
public static class WorldSquads
{
    private const ushort SquadSlotType = 1;
    private static readonly int NameField = TagView.FieldOffset<ObjectSlot>(nameof(ObjectSlot.NameHash));

    /** Every squad slot the world's authored objects list, once per owner and config. */
    public static IReadOnlyList<WorldSquad> List(WorldFiles world, PackageCatalog catalog, CancellationToken token = default) =>
        Slots(world, catalog, token).Select(row => new WorldSquad(row.Owner.Name, row.NameHash, row.Slot.ConfigTag, Entities(catalog, row.Slot.ConfigTag)))
            .DistinctBy(squad => (squad.OwnerFile, squad.ConfigTag)).ToList();

    /**
     * Makes every member of the squad behind `configTag` spawn as `entity`.
     * @param configTag The squad's installed spawner config.
     * @param entity A file of this world, or TftNames.Installed of an installed entity.
     */
    public static void SetEntity(WorldFiles world, RegistryIndex schema, PackageCatalog catalog, string destination, uint configTag,
        string entity, CancellationToken token = default)
    {
        var found = Slots(world, catalog, token).Where(row => row.Slot.ConfigTag == configTag).ToList();
        if (found.Count == 0) throw new InvalidDataException($"The world has no squad with spawner config 0x{configTag:X8}.");
        var members = Entities(catalog, configTag).Select(TftNames.Installed).ToHashSet(StringComparer.Ordinal);
        if (members.Count == 0) throw new InvalidDataException($"Spawner config 0x{configTag:X8} names no entity.");
        foreach (var (owner, _, slot) in found)
        {
            string from = owner.Name;
            foreach (var hop in slot.Path)
            {
                string name = world.Files.FirstOrDefault(file => file.SourceTag == hop.Tag)?.Name ?? WorldDevices.Copy(world, schema, catalog, destination, hop.Tag);
                WorldDevices.Repoint(world, from, hop.Tag, name);
                from = name;
            }
        }
        var config = world.Files.Single(file => file.SourceTag == configTag);
        world.Replace(WorldDevices.With(config, config.Data, config.References
            .Select(reference => members.Contains(reference.Target) ? reference with { Target = entity } : reference).ToList()));
    }

    /**
     * Copies an installed entity with its component configs into the world and makes every member
     * of the squad behind `configTag` spawn as the copy.
     * @param replace New bytes for a component config of the copy, given by its installed tag.
     * @return The copy's file name in the world.
     */
    public static string SetEntityCopy(WorldFiles world, RegistryIndex schema, PackageCatalog catalog, string destination, uint configTag,
        uint entity, IReadOnlyDictionary<uint, byte[]>? replace = null, CancellationToken token = default)
    {
        if (!catalog.TryGetEntry(entity, out var entry) || entry.Reference != SEntity.Class)
            throw new InvalidDataException($"0x{entity:X8} is not an installed entity.");
        return SetEntityCopy(world, schema, catalog, destination, configTag, entity,
            SunriseStudio.World.Entities.EntityBuilder.Clone(catalog, entity, replace: replace), token);
    }

    /**
     * Adds a prepared copy of an installed entity to the world and makes every member of the squad
     * behind `configTag` spawn as it.
     * @param entity The installed entity the copy was made from.
     * @param copy The copy's tags, the entity first, as EntityBuilder.Clone returns them.
     * @return The copy's file name in the world.
     */
    public static string SetEntityCopy(WorldFiles world, RegistryIndex schema, PackageCatalog catalog, string destination, uint configTag,
        uint entity, IReadOnlyList<WorldTag> copy, CancellationToken token = default)
    {
        string file = AddEntityCopy(world, schema, catalog, destination, entity, copy);
        SetEntity(world, schema, catalog, destination, configTag, file, token);
        return file;
    }

    /**
     * Adds a prepared copy of an installed entity to the world, for a squad to name by file.
     * @param copy The copy's tags, the entity first, as EntityBuilder.Clone returns them.
     * @return The copy's file name in the world.
     */
    public static string AddEntityCopy(WorldFiles world, RegistryIndex schema, PackageCatalog catalog, string destination, uint entity,
        IReadOnlyList<WorldTag> copy)
    {
        string file = WorldContent.AddTags(world, schema, catalog, destination, copy)[0];
        // The pack builder reads this to give the squad's members the copied enemy's actor class.
        var added = world[file];
        world.Replace(new TftFile
        {
            Name = added.Name, TagType = added.TagType, GameName = added.GameName, ClassId = added.ClassId, TypeInfo = added.TypeInfo,
            BuildHash = added.BuildHash, Layout = added.Layout, Data = added.Data, References = added.References, Meta = added.Meta,
            Info = TftFile.CopyInfo(entity),
        });
        return file;
    }

    private static List<(TftFile Owner, uint NameHash, ComponentSlotEvidence Slot)> Slots(WorldFiles world, PackageCatalog catalog, CancellationToken token)
    {
        var result = new List<(TftFile, uint, ComponentSlotEvidence)>();
        foreach (var owner in world.Files.Where(file => file.ClassId == SAuthoredObject.Class && file.SourceTag is not null).ToList())
        {
            uint source = owner.SourceTag!.Value;
            byte[] bytes = catalog.Read(source);
            foreach (var slot in ComponentSlotCatalog.OwnerSlots(catalog, source, token).Where(slot => slot.SlotType == SquadSlotType))
                result.Add((owner, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(slot.SlotOffset + NameField)), slot));
        }
        return result;
    }

    /** The installed entities an installed spawner config names, in first-use order. */
    private static List<uint> Entities(PackageCatalog catalog, uint configTag)
    {
        byte[] config = catalog.Read(configTag);
        var result = new List<uint>();
        for (int at = 0; at + sizeof(uint) <= config.Length; at += sizeof(uint))
        {
            uint word = BinaryPrimitives.ReadUInt32LittleEndian(config.AsSpan(at));
            if (word >= TagHashes.Base && catalog.TryGetEntry(word, out var entry) && entry.Reference == SEntity.Class && !result.Contains(word))
                result.Add(word);
        }
        return result;
    }
}
