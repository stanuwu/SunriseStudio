using System.Buffers.Binary;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Graph;
using SunriseStudio.Tags.Tft;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.World;

/** One device slot of a world copy: the authored object file that lists it, its name hash and its installed config. */
public sealed record WorldDeviceSlot(string OwnerFile, uint NameHash, uint ConfigTag, ulong Identity);

/**
 * Device slots of a world copy. A copy keeps the slots of its source's authored objects; the server
 * drives a slot by its source name. Binding a slot points it at one placed object.
 */
public static class WorldDevices
{
    // The server's device channels belong to this slot type.
    private const ushort DeviceSlotType = 23;
    private static readonly int IdentityField = TagView.FieldOffset<DeviceResourceType23>(nameof(DeviceResourceType23.PlacementIdentifier)),
        NameField = TagView.FieldOffset<ObjectSlot>(nameof(ObjectSlot.NameHash));

    /** Every device slot the world's authored objects list, with the identity its config holds now. */
    public static IReadOnlyList<WorldDeviceSlot> Slots(WorldFiles world, PackageCatalog catalog, CancellationToken token = default)
    {
        var slots = new List<WorldDeviceSlot>();
        foreach (var owner in world.Files.Where(file => file.ClassId == SAuthoredObject.Class && file.SourceTag is not null).ToList())
        {
            uint source = owner.SourceTag!.Value;
            byte[] bytes = catalog.Read(source);
            foreach (var slot in ComponentSlotCatalog.OwnerSlots(catalog, source, token).Where(slot => slot.SlotType == DeviceSlotType))
            {
                var copy = world.Files.FirstOrDefault(file => file.SourceTag == slot.ConfigTag);
                byte[] config = copy?.Data ?? catalog.Read(slot.ConfigTag);
                slots.Add(new(owner.Name, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(slot.SlotOffset + NameField)), slot.ConfigTag,
                    BinaryPrimitives.ReadUInt64LittleEndian(config.AsSpan(slot.DescriptorOffset + IdentityField))));
            }
        }
        return slots;
    }

    /**
     * Points the device slot named `slot` at the placement that carries `identity`. The world takes
     * its own copy of the slot's config and of the tags that lead to it, so the installed slot stays
     * as it is.
     * @param slot The slot's name in the source activity, such as d_lure.
     * @param identity WorldContent.PlacementIdentity of the placement's name.
     */
    public static void Bind(WorldFiles world, RegistryIndex schema, PackageCatalog catalog, string destination, string slot, ulong identity,
        CancellationToken token = default)
    {
        uint nameHash = StringBankWriter.Hash(slot);
        var found = new List<(TftFile Owner, ComponentSlotEvidence Slot)>();
        foreach (var owner in world.Files.Where(file => file.ClassId == SAuthoredObject.Class && file.SourceTag is not null))
        {
            uint source = owner.SourceTag!.Value;
            byte[] bytes = catalog.Read(source);
            found.AddRange(ComponentSlotCatalog.OwnerSlots(catalog, source, token)
                .Where(row => row.SlotType == DeviceSlotType
                    && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(row.SlotOffset + NameField)) == nameHash)
                .Select(row => (owner, row)));
        }
        // One name can reach its config through several bubbles of one object; the config is what is bound.
        var configs = found.Select(row => row.Slot.ConfigTag).Distinct().ToList();
        if (configs.Count != 1)
            throw new InvalidDataException(configs.Count == 0 ? $"The world has no device slot named {slot}." : $"The world has {configs.Count} device slots named {slot}.");

        foreach (var (owner, evidence) in found)
        {
            string from = owner.Name;
            foreach (var hop in evidence.Path)
            {
                string name = world.Files.FirstOrDefault(file => file.SourceTag == hop.Tag)?.Name ?? Copy(world, schema, catalog, destination, hop.Tag);
                Repoint(world, from, hop.Tag, name);
                from = name;
            }
        }
        var config = world.Files.Single(file => file.SourceTag == configs[0]);
        byte[] data = config.Data.ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(found[0].Slot.DescriptorOffset + IdentityField), identity);
        world.Replace(With(config, data, config.References));
    }

    /** Adds one installed tag as a file of the world, named as the template names its copies. */
    internal static string Copy(WorldFiles world, RegistryIndex schema, PackageCatalog catalog, string destination, uint tag)
    {
        string type = WorldTemplate.TypeName(schema, catalog.TryGetEntry(tag, out var entry) ? entry.Reference : 0);
        string name = $"{WorldTemplate.Folder}/{destination}/{type}/{tag:x8}.{type}";
        world.Add(TftClone.Clone([tag], new CatalogTagSource(catalog), schema, _ => name).Single());
        return name;
    }

    /** Turns a file's references to an installed tag into references to a file of the world. */
    internal static void Repoint(WorldFiles world, string file, uint installed, string target)
    {
        var source = world[file];
        string raw = TftNames.Installed(installed);
        if (source.References.All(reference => reference.Target != raw)) return;
        world.Replace(With(source, source.Data, source.References.Select(reference => reference.Target == raw ? reference with { Target = target } : reference).ToList()));
    }

    internal static TftFile With(TftFile file, byte[] data, IReadOnlyList<TftReference> references) => new()
    {
        Name = file.Name, TagType = file.TagType, GameName = file.GameName, ClassId = file.ClassId, TypeInfo = file.TypeInfo,
        BuildHash = file.BuildHash, Layout = file.Layout, Data = data, References = references, Info = file.Info, Meta = file.Meta,
    };
}
