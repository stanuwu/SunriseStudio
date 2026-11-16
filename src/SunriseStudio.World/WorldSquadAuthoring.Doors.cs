using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Tft;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.World;

/**
 * A door: a box the player walks into, reported to the mission script as a player trigger.
 * @param Name Unique among the destination's doors; the trigger slot is named `door.<Name>`.
 * @param Floor The middle of the box's floor, in map space.
 * @param Size Width along the turned x, depth along the turned y, and height, in metres.
 * @param Yaw Turn about +Z in radians.
 */
public sealed record NewDoor(string Name, Vector3 Floor, Vector3 Size, float Yaw = 0);

/** One door a made owner holds: its type-31 trigger slot and its type-60 volume slot, with their configs. */
public sealed record AddedDoor(string Name, CombatSlot Trigger, CombatSlot Volume, string TriggerConfig, string VolumeConfig, IReadOnlyList<Vector3> Corners);

public static partial class WorldSquadAuthoring
{
    /** Slot types of a door: the player trigger and the trigger volume it watches. */
    public const ushort PlayerTriggerSlotType = 31, TriggerVolumeSlotType = 60;
    /**
     * Doors one owner takes. Each door is two slots, and the widest installed object holds 1,218;
     * Sunrise's roster group holds 1,280.
     */
    public const int MaximumDoorsPerOwner = 600;
    // An installed object whose type-31 trigger (slot 19) watches one type-60 volume (slot 37) that a
    // config of its own holds alone; every keyed ref of either config is one of those two slots.
    private const uint DoorTemplate = 0x80B9A4B9;
    private const ushort DoorTriggerIndex = 19, DoorVolumeIndex = 37;
    private const string DoorKey = "doors";
    private static readonly ushort[] DoorTypes = [PlayerTriggerSlotType, TriggerVolumeSlotType];

    /**
     * Gives an empty made owner one player trigger and one trigger volume per door, copied from an
     * installed one-volume trigger. Triggers fill the first slot group and volumes the second.
     * @param shapeFor Gives the Havok shape tag of a box floor in map space and its height; null keeps the template's shape.
     * @param bubbleRow The scenario row whose bubble loads the doors and alone lists the owner; null keeps the owner's and the template's.
     */
    public static IReadOnlyList<AddedDoor> AddDoors(WorldFiles world, RegistryIndex schema, PackageCatalog catalog, string destination, string ownerFile,
        IReadOnlyList<NewDoor> doors, Func<IReadOnlyList<Vector3>, float, uint>? shapeFor = null, CancellationToken token = default, int? bubbleRow = null)
    {
        var owner = world[ownerFile];
        if (OwnerTemplate(owner) is null) throw new InvalidDataException($"{ownerFile} is not an owner the world made.");
        if (TagRead.U32(owner.Data, GroupsField) != 0) throw new InvalidDataException($"{ownerFile} already has slots; doors need an empty owner.");
        if (doors.Count is 0 or > MaximumDoorsPerOwner)
            throw new ArgumentOutOfRangeException(nameof(doors), $"One owner takes 1 to {MaximumDoorsPerOwner} doors.");
        var taken = DoorNames(world);
        foreach (var door in doors)
        {
            if (string.IsNullOrEmpty(door.Name) || door.Name.Length > MaximumName || door.Name.Any(c => c is < '!' or > '~'))
                throw new InvalidDataException($"\"{door.Name}\" is not a door name: 1 to {MaximumName} printable ASCII characters.");
            if (!taken.Add(door.Name)) throw new InvalidDataException($"The destination already has a door named {door.Name}.");
            if (!(door.Size.X > 0 && door.Size.Y > 0 && door.Size.Z > 0) || !float.IsFinite(door.Floor.X + door.Floor.Y + door.Floor.Z + door.Yaw))
                throw new ArgumentOutOfRangeException(nameof(doors), $"Door {door.Name} needs a finite floor and a positive size.");
        }

        byte[] installed = catalog.Read(DoorTemplate);
        uint oldKey = TagView.At<SAuthoredObject>(installed, 0).RegistryKey;
        uint key = TagView.At<SAuthoredObject>(owner.Data, 0).RegistryKey;
        var trigger = ComponentSlotCatalog.OwnerSlots(catalog, DoorTemplate, token)
            .SingleOrDefault(row => row.SlotType == PlayerTriggerSlotType && row.SlotIndex == DoorTriggerIndex && row.Path.Length == 3)
            ?? throw new InvalidDataException($"0x{DoorTemplate:X8} has no player trigger at slot {DoorTriggerIndex}.");
        var (volumeSet, volumeLink, volumeConfig) = Chains(catalog, installed).FirstOrDefault(chain =>
            ArrayOf(catalog.Read(chain.Config), STriggerVolumeRow.Class) >= 0 && SlotRefs(catalog.Read(chain.Config), oldKey, DoorTypes) is { Count: > 0 } refs
            && refs.All(at => TagRead.U16(catalog.Read(chain.Config), at + 4) == TriggerVolumeSlotType && TagRead.U16(catalog.Read(chain.Config), at + 6) == DoorVolumeIndex));
        if (volumeConfig == 0) throw new InvalidDataException($"0x{DoorTemplate:X8} has no config that holds volume {DoorVolumeIndex} alone.");
        int volumeBlock = BlockHolding(installed, volumeSet);
        int volumeBubble = TagView.Rows(installed, in TagView.At<SAuthoredObject>(installed, 0).EntryFilters, ObjectBubbleBlock.Class)[volumeBlock].BubbleIndex;

        string File(string slug, uint classId) =>
            $"{WorldTemplate.Folder}/{destination}/{WorldTemplate.TypeName(schema, classId)}/{slug}.{WorldTemplate.TypeName(schema, classId)}";
        int count = doors.Count;
        var added = new List<AddedDoor>();
        var triggerSets = new List<string>();
        var volumeSets = new List<string>();
        int bubble = bubbleRow ?? (int)WorldFiles.Field((JsonObject)((JsonArray)WorldFiles.Field(world.Fields(ownerFile), "entry_filters"))[0]!, "bubble_index");
        for (int index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            var door = doors[index];
            var map = new Dictionary<(ushort Type, ushort Index), CombatSlot>
            {
                [(PlayerTriggerSlotType, DoorTriggerIndex)] = new((ushort)index, DoorTriggerIndex, PlayerTriggerSlotType, $"door.{door.Name}", DoorTemplate),
                [(TriggerVolumeSlotType, DoorVolumeIndex)] = new((ushort)(count + index), DoorVolumeIndex, TriggerVolumeSlotType, $"door.{door.Name}.volume", DoorTemplate),
            };
            string slug = "door_" + Slug(door.Name);
            var (triggerSet, triggerConfig) = CloneChain(world, schema, catalog, trigger.Path[0].Tag, trigger.Path[1].Tag, trigger.ConfigTag, slug, File);
            var (volumeSetFile, volumeConfigFile) = CloneChain(world, schema, catalog, volumeSet, volumeLink, volumeConfig, slug + "_volume", File);
            Retarget(world, triggerConfig, oldKey, key, map, bubble, trigger.DescriptorOffset, DoorTypes);
            Retarget(world, volumeConfigFile, oldKey, key, map, null, -1, DoorTypes);
            var corners = DoorCorners(door);
            Volume(world, volumeConfigFile, corners, door.Size.Z, shapeFor?.Invoke(corners, door.Size.Z),
                WorldContent.PlacementIdentity($"door/{destination}/{door.Name}"));
            triggerSets.Add(triggerSet);
            volumeSets.Add(volumeSetFile);
            added.Add(new(door.Name, map[(PlayerTriggerSlotType, DoorTriggerIndex)], map[(TriggerVolumeSlotType, DoorVolumeIndex)], triggerConfig, volumeConfigFile, corners));
        }

        // Triggers join the owner's block; volumes take a block like their template's.
        world.Edit(ownerFile, (_, fields) =>
        {
            var slots = (JsonArray)WorldFiles.Field((JsonObject)WorldFiles.Field(fields, "slot_table"), "slots");
            foreach (var slot in added.Select(door => door.Trigger).Concat(added.Select(door => door.Volume)))
                slots.Add(new JsonObject { ["name_hash"] = WorldFiles.HashText(StringBankWriter.Hash(slot.Name)), ["slot_type"] = (int)slot.SlotType });
            var blocks = (JsonArray)WorldFiles.Field(fields, "entry_filters");
            var first = (JsonObject)blocks[0]!;
            first["bubble_index"] = bubble;
            foreach (string set in triggerSets) ((JsonArray)WorldFiles.Field(first, "handles")).Add(new JsonObject { ["handle"] = set });
            var volumes = (JsonObject)first.DeepClone();
            volumes["bubble_index"] = bubbleRow ?? volumeBubble;
            var handles = (JsonArray)WorldFiles.Field(volumes, "handles");
            handles.Clear();
            foreach (string set in volumeSets) handles.Add(new JsonObject { ["handle"] = set });
            blocks.Add(volumes);
        });
        var edited = world[ownerFile];
        byte[] ownerData = edited.Data.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(ownerData.AsSpan(GroupsField), (uint)count | (uint)count << 16);
        var info = JsonNode.Parse(edited.Info!)!.AsObject();
        info[DoorKey] = new JsonArray(added.Select(door => door.Trigger).Concat(added.Select(door => door.Volume)).Select(slot => (JsonNode)new JsonObject
        {
            ["index"] = slot.Index, ["template_index"] = slot.TemplateIndex, ["slot_type"] = slot.SlotType, ["name"] = slot.Name,
            ["template_object"] = $"0x{slot.TemplateObject:X8}",
        }).ToArray());
        world.Replace(new TftFile
        {
            Name = edited.Name, TagType = edited.TagType, GameName = edited.GameName, ClassId = edited.ClassId, TypeInfo = edited.TypeInfo,
            BuildHash = edited.BuildHash, Layout = edited.Layout, Data = ownerData, References = edited.References, Meta = edited.Meta, Info = info.ToJsonString(),
        });
        // The server finds a reported trigger through its one placement, so one bubble lists the owner.
        if (bubbleRow is int only) LoadOnlyIn(world, ownerFile, only);
        return added;
    }

    /** The door slots of a made owner, triggers then volumes; null when it has none. */
    public static IReadOnlyList<CombatSlot>? DoorSlots(TftFile owner)
    {
        if (OwnerTemplate(owner) is null) return null;
        try
        {
            return JsonNode.Parse(owner.Info!)?[DoorKey] is JsonArray rows
                ? rows.OfType<JsonObject>().Select(row => new CombatSlot((ushort)row["index"]!, (ushort)row["template_index"]!, (ushort)row["slot_type"]!,
                    (string)row["name"]!, Convert.ToUInt32(((string)row["template_object"]!)[2..], 16))).ToList()
                : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /** Every set, link and config chain an installed object's handles reach, in any package. */
    private static IEnumerable<(uint Set, uint Link, uint Config)> Chains(PackageCatalog catalog, byte[] owner)
    {
        IEnumerable<uint> Named(uint tag, uint classId)
        {
            byte[] bytes = catalog.Read(tag);
            for (int at = 0; at + sizeof(uint) <= bytes.Length; at += sizeof(uint))
            {
                uint word = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
                if (word != tag && word >= TagHashes.Base && catalog.TryGetEntry(word, out var entry) && entry.Reference == classId) yield return word;
            }
        }
        var sets = new List<uint>();
        foreach (ref readonly var block in TagView.Rows(owner, in TagView.At<SAuthoredObject>(owner, 0).EntryFilters, ObjectBubbleBlock.Class))
            foreach (ref readonly var handle in TagView.Rows(owner, in block.Handles, PlacedHandle.Class)) sets.Add(handle.Handle);
        foreach (uint set in sets.Where(set => catalog.TryGetEntry(set, out var entry) && entry.Reference == SAuthoredObjectSet.Class))
            foreach (uint link in Named(set, SEntityLink.Class).Distinct())
                foreach (uint config in Named(link, SEntityConfig.Class).Distinct())
                    yield return (set, link, config);
    }

    /** Every door name the world's made owners hold. */
    private static HashSet<string> DoorNames(WorldFiles world) => world.Files.Where(file => file.ClassId == SAuthoredObject.Class)
        .SelectMany(file => DoorSlots(file) ?? []).Where(slot => slot.SlotType == PlayerTriggerSlotType).Select(slot => slot.Name["door.".Length..])
        .ToHashSet(StringComparer.Ordinal);

    /** The four floor corners of a door's box in map space, in order around it. */
    public static IReadOnlyList<Vector3> DoorCorners(NewDoor door)
    {
        var spin = Matrix4x4.CreateRotationZ(door.Yaw);
        float x = door.Size.X / 2, y = door.Size.Y / 2;
        return new[] { new Vector3(-x, -y, 0), new Vector3(x, -y, 0), new Vector3(x, y, 0), new Vector3(-x, y, 0) }
            .Select(corner => door.Floor + Vector3.Transform(corner, spin)).ToArray();
    }

    /**
     * Makes the one volume row of a cloned volume config the box over `corners`: its sector, its
     * placement at no offset with a new identity, and its shape when one is given.
     */
    private static void Volume(WorldFiles world, string configFile, IReadOnlyList<Vector3> corners, float height, uint? shape, ulong identity)
    {
        var file = world[configFile];
        byte[] data = file.Data.ToArray();
        var (rows, first, _) = TagArray.Checked(data, ArrayOf(data, STriggerVolumeRow.Class));
        if (rows != 1) throw new InvalidDataException($"{configFile} holds {rows} volume rows, not one.");
        ref var row = ref TagView.Edit<STriggerVolumeRow>(data, first);
        var points = TagView.Rows(data, in row.Sector.Vertices, SVec4Point.Class);
        var triangles = TagView.Rows(data, in row.Sector.Triangles, TriggerVolumeTriangle.Class);
        if (points.Length != 4 || triangles.Length != 2 || row.SpawnEntry.Payload.IsNull)
            throw new InvalidDataException($"{configFile} does not hold four vertices, two triangles and a shape.");
        int vertices = TagView.OffsetOf(data, in points[0]), faces = TagView.OffsetOf(data, in triangles[0]);
        int payload = TagView.TargetOffset(data, in row.SpawnEntry.Payload)!.Value;
        row.SpawnEntry.Position = Vector3.Zero;
        row.SpawnEntry.PlacementIdentity = identity;
        row.Sector.BoundsMin = new(corners.Min(corner => corner.X), corners.Min(corner => corner.Y), corners[0].Z);
        row.Sector.BoundsMax = new(corners.Max(corner => corner.X), corners.Max(corner => corner.Y), corners[0].Z + height);
        row.Sector.ExtrusionHeight = height;
        int stride = System.Runtime.CompilerServices.Unsafe.SizeOf<SVec4Point>();
        for (int index = 0; index < 4; index++) TagView.Edit<SVec4Point>(data, vertices + index * stride).Position = corners[index];
        ((ReadOnlySpan<byte>)[0, 1, 2, 0, 2, 3]).CopyTo(data.AsSpan(faces));
        TagView.Edit<TriggerShapeReference>(data, payload).ShapeIndex = 0;
        int resource = payload + TagView.FieldOffset<TriggerShapeReference>(nameof(TriggerShapeReference.Resource));
        if (file.References.All(reference => reference.Offset != resource)) throw new InvalidDataException($"{configFile} names no shape resource.");
        var references = shape is uint tag
            ? file.References.Select(reference => reference.Offset == resource ? reference with { Target = TftNames.Installed(tag) } : reference).ToList()
            : file.References;
        world.Replace(WorldDevices.With(file, data, references));
    }
}
