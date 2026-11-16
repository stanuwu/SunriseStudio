using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Tft;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.World;

/**
 * One trigger volume of a world copy and the player trigger that reports it.
 * @param OwnerFile The authored object file that lists the trigger slot.
 * @param TriggerNameHash Name hash of the player trigger slot, as the source activity names it.
 * @param Path The installed tags from the owner to the volume's config, in order.
 * @param RowOffset Where the volume's row starts in its config.
 * @param Min The volume's box in map space, before Offset.
 * @param Offset The placement offset the config holds now.
 */
public sealed record WorldVolume(string OwnerFile, uint TriggerNameHash, uint Key, ushort Ordinal, uint ConfigTag,
    IReadOnlyList<uint> Path, int RowOffset, Vector3 Min, Vector3 Max, Vector3 Offset);

/**
 * Trigger volumes of a world copy. A copy keeps its source's player triggers, and the server hears
 * one by its source name. Moving a volume gives the world its own copy of the volume's config.
 */
public static class WorldVolumes
{
    private const ushort PlayerTriggerType = 31, VolumeType = 60;
    // More tags than any installed owner reaches; a walk past it is a cycle.
    private const int MaximumWalk = 4096;
    private static readonly int NameField = TagView.FieldOffset<ObjectSlot>(nameof(ObjectSlot.NameHash)),
        TargetsField = TagView.FieldOffset<Type31TriggerComponent>(nameof(Type31TriggerComponent.TargetReferences)),
        ShapesField = TagView.FieldOffset<STriggerComponentPeer>(nameof(STriggerComponentPeer.TriggerShapes)),
        LinksField = TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.ComponentLinks));

    /** Every player trigger of the world that names a volume, with the volume's box. */
    public static IReadOnlyList<WorldVolume> List(WorldFiles world, PackageCatalog catalog, CancellationToken token = default)
    {
        var result = new List<WorldVolume>();
        foreach (var owner in world.Files.Where(file => file.ClassId == SAuthoredObject.Class && file.SourceTag is not null).ToList())
        {
            uint source = owner.SourceTag!.Value;
            byte[] bytes = catalog.Read(source);
            var triggers = ComponentSlotCatalog.OwnerSlots(catalog, source, token).Where(slot => slot.SlotType == PlayerTriggerType).ToList();
            if (triggers.Count == 0) continue;
            var rows = Rows(world, catalog, source, token);
            foreach (var trigger in triggers)
            {
                byte[] config = catalog.Read(trigger.ConfigTag);
                ref readonly var targets = ref TagView.At<TagArray<ClientRef>>(config, trigger.DescriptorOffset + TargetsField);
                foreach (ref readonly var target in TagView.Rows(config, in targets, ClientRef.Class))
                {
                    if (target.TargetSlotType != VolumeType) continue;
                    var key = (target.TargetKey, target.TargetSlotIndex);
                    if (!rows.TryGetValue(key, out var row)) continue;
                    result.Add(new(owner.Name, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(trigger.SlotOffset + NameField)),
                        key.TargetKey, key.TargetSlotIndex, row.Config, row.Path, row.Offset, row.Min, row.Max, row.Placed));
                }
            }
        }
        return result;
    }

    /**
     * Moves the volume of the player trigger named `trigger` so the middle of its floor sits at
     * `floorCentre`. The volume keeps its shape and size. The world takes its own copy of the
     * volume's config and of the tags that lead to it.
     * @param trigger The trigger's name in the source activity.
     */
    public static void Move(WorldFiles world, RegistryIndex schema, PackageCatalog catalog, string destination, string trigger,
        Vector3 floorCentre, CancellationToken token = default)
    {
        var (config, first) = Own(world, schema, catalog, destination, trigger, token);
        byte[] data = config.Data.ToArray();
        var floor = new Vector3((first.Min.X + first.Max.X) / 2, (first.Min.Y + first.Max.Y) / 2, first.Min.Z);
        TagView.Edit<STriggerVolumeRow>(data, first.RowOffset).SpawnEntry.Position = floorCentre - floor;
        world.Replace(WorldDevices.With(config, data, config.References));
    }

    /**
     * Gives the volume of the player trigger named `trigger` a new floor and a new shape.
     * The volume row must already hold four vertices and two triangles. The world takes its own
     * copy of the volume's config and of the tags that lead to it.
     * @param shapeTag A project shape array whose first shape is the prism over `corners`.
     * @param corners The floor's corners in map space, in order around it; their Z is the floor.
     * @param height How far the volume reaches up from the floor.
     */
    public static void Reshape(WorldFiles world, RegistryIndex schema, PackageCatalog catalog, string destination, string trigger,
        uint shapeTag, IReadOnlyList<Vector3> corners, float height, CancellationToken token = default)
    {
        var (config, first) = Own(world, schema, catalog, destination, trigger, token);
        byte[] data = config.Data.ToArray();
        ref var row = ref TagView.Edit<STriggerVolumeRow>(data, first.RowOffset);
        var points = TagView.Rows(data, in row.Sector.Vertices, SVec4Point.Class);
        var triangles = TagView.Rows(data, in row.Sector.Triangles, TriggerVolumeTriangle.Class);
        if (corners.Count < 3 || points.Length != 4 || triangles.Length != 2 || row.SpawnEntry.Payload.IsNull)
            throw new InvalidDataException($"The volume of {trigger} does not have four vertices, two triangles and a shape.");
        // The row keeps four vertices. A floor of another corner count gets the rectangle around it.
        if (corners.Count != 4)
        {
            float lowX = corners.Min(corner => corner.X), highX = corners.Max(corner => corner.X);
            float lowY = corners.Min(corner => corner.Y), highY = corners.Max(corner => corner.Y), z = corners[0].Z;
            corners = [new(lowX, lowY, z), new(highX, lowY, z), new(highX, highY, z), new(lowX, highY, z)];
        }
        int vertices = TagView.OffsetOf(data, in points[0]), faces = TagView.OffsetOf(data, in triangles[0]);
        int payload = TagView.TargetOffset(data, in row.SpawnEntry.Payload)!.Value;
        row.SpawnEntry.Position = Vector3.Zero;
        row.Sector.BoundsMin = new(corners.Min(corner => corner.X), corners.Min(corner => corner.Y), corners[0].Z);
        row.Sector.BoundsMax = new(corners.Max(corner => corner.X), corners.Max(corner => corner.Y), corners[0].Z + height);
        row.Sector.ExtrusionHeight = height;
        int stride = System.Runtime.CompilerServices.Unsafe.SizeOf<SVec4Point>();
        for (int index = 0; index < 4; index++) TagView.Edit<SVec4Point>(data, vertices + index * stride).Position = corners[index];
        // Two triangles that share the first corner, as the shape is cut.
        ((ReadOnlySpan<byte>)[0, 1, 2, 0, 2, 3]).CopyTo(data.AsSpan(faces));
        TagView.Edit<TriggerShapeReference>(data, payload).ShapeIndex = 0;
        int resource = payload + TagView.FieldOffset<TriggerShapeReference>(nameof(TriggerShapeReference.Resource));
        if (config.References.All(reference => reference.Offset != resource))
            throw new InvalidDataException($"The volume of {trigger} names no shape resource.");
        world.Replace(WorldDevices.With(config, data, config.References
            .Select(reference => reference.Offset == resource ? reference with { Target = TftNames.Installed(shapeTag) } : reference).ToList()));
    }

    /** Copies the config chain of one trigger's volume into the world. @return The world's config file and the volume. */
    private static (TftFile Config, WorldVolume Volume) Own(WorldFiles world, RegistryIndex schema, PackageCatalog catalog,
        string destination, string trigger, CancellationToken token)
    {
        uint nameHash = StringBankWriter.Hash(trigger);
        var found = List(world, catalog, token).Where(volume => volume.TriggerNameHash == nameHash).ToList();
        var rows = found.Select(volume => (volume.ConfigTag, volume.RowOffset)).Distinct().ToList();
        if (rows.Count != 1)
            throw new InvalidDataException(rows.Count == 0 ? $"The world has no player trigger named {trigger} with a volume."
                : $"The world has {rows.Count} volumes for player triggers named {trigger}.");
        foreach (var volume in found)
        {
            string from = volume.OwnerFile;
            foreach (uint hop in volume.Path)
            {
                string name = world.Files.FirstOrDefault(file => file.SourceTag == hop)?.Name ?? WorldDevices.Copy(world, schema, catalog, destination, hop);
                WorldDevices.Repoint(world, from, hop, name);
                from = name;
            }
        }
        return (world.Files.Single(file => file.SourceTag == found[0].ConfigTag), found[0]);
    }

    /**
     * Walks from an authored object through its set and link tags to every config with a trigger
     * component. The world's own copy of a config is read in place of the installed one.
     * @return Each volume row by its key and ordinal.
     */
    private static Dictionary<(uint Key, ushort Ordinal), (uint Config, IReadOnlyList<uint> Path, int Offset, Vector3 Min, Vector3 Max, Vector3 Placed)>
        Rows(WorldFiles world, PackageCatalog catalog, uint owner, CancellationToken token)
    {
        var result = new Dictionary<(uint, ushort), (uint, IReadOnlyList<uint>, int, Vector3, Vector3, Vector3)>();
        var parent = new Dictionary<uint, uint>();
        var pending = new Queue<uint>();
        pending.Enqueue(owner);
        var seen = new HashSet<uint> { owner };
        while (pending.TryDequeue(out uint tag))
        {
            token.ThrowIfCancellationRequested();
            if (seen.Count > MaximumWalk) throw new InvalidDataException($"Owner 0x{owner:X8} reaches too many tags.");
            if (!catalog.TryGetEntry(tag, out var entry)) continue;
            if (entry.Reference == SEntityConfig.Class)
            {
                var path = new List<uint>();
                for (uint at = tag; at != owner; at = parent[at]) path.Add(at);
                path.Reverse();
                byte[] config = world.Files.FirstOrDefault(file => file.SourceTag == tag)?.Data ?? catalog.Read(tag);
                foreach (var (offset, row) in VolumeRows(config))
                    result.TryAdd((row.Key, row.SlotOrdinal), (tag, path, offset, row.Sector.BoundsMin, row.Sector.BoundsMax, row.SpawnEntry.Position));
                continue;
            }
            if (tag != owner && entry.Reference != SAuthoredObjectSet.Class && entry.Reference != SEntityLink.Class) continue;
            byte[] bytes = catalog.Read(tag);
            for (int at = 0; at + sizeof(uint) <= bytes.Length; at += sizeof(uint))
            {
                uint word = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
                if (word >= TagHashes.Base && catalog.ContainsTag(word) && seen.Add(word))
                {
                    parent[word] = tag;
                    pending.Enqueue(word);
                }
            }
        }
        return result;
    }

    /** The type-60 rows of a config's trigger component, each with where it starts. */
    private static List<(int Offset, STriggerVolumeRow Row)> VolumeRows(byte[] config)
    {
        var result = new List<(int, STriggerVolumeRow)>();
        if (!TagView.Fits<SEntityConfig>(config, 0)) return result;
        ref readonly var links = ref TagView.At<TagArray<PlacedConfigComponentRow>>(config, LinksField);
        if (!TagArray.TryChecked(config, LinksField, PlacedConfigComponentRow.Class, System.Runtime.CompilerServices.Unsafe.SizeOf<PlacedConfigComponentRow>(), out _))
            return result;
        foreach (ref readonly var component in TagView.Rows(config, in links, PlacedConfigComponentRow.Class))
        {
            if (component.ComponentClass != STriggerComponent.Class || component.ComponentHeader.IsNull) continue;
            int header = TagView.TargetOffset(config, in component.ComponentHeader)!.Value;
            int root = checked((int)(ulong)TagView.At<TagOffsetPointer>(config, header).Offset);
            int stride = System.Runtime.CompilerServices.Unsafe.SizeOf<STriggerVolumeRow>();
            if (!TagArray.TryChecked(config, root + ShapesField, STriggerVolumeRow.Class, stride, out var rows)) continue;
            for (int index = 0; index < rows.Count; index++)
            {
                var row = TagView.At<STriggerVolumeRow>(config, rows.Data + index * stride);
                if (row.SlotType == VolumeType) result.Add((rows.Data + index * stride, row));
            }
        }
        return result;
    }
}
