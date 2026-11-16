using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Graph;
using SunriseStudio.Tags.Tft;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.World;

/**
 * A slot a combat area added to a squad owner.
 * @param Index The slot's index in the owner.
 * @param TemplateIndex The index of the template object's slot it copies.
 * @param Name The slot name a mission script finds it by.
 * @param TemplateObject The installed object the slot copies; 0 for the owner's own template.
 */
public sealed record CombatSlot(ushort Index, ushort TemplateIndex, ushort SlotType, string Name, uint TemplateObject = 0);

/**
 * What AddCombatArea gave a squad owner: an objective slot, firing area set slots and firing area slots.
 * @param Groups The objective's task group count; a script assigns a squad to group 0 to Groups - 1.
 */
public sealed record AddedCombatArea(string OwnerFile, CombatSlot Objective, IReadOnlyList<CombatSlot> Slots, int Groups,
    string ObjectiveConfig, string AreaConfig);

public static partial class WorldSquadAuthoring
{
    /** Slot types of a combat area: the objective, a firing area and a firing area set. */
    public const ushort ObjectiveSlotType = 3, FiringAreaSlotType = 44, FiringAreaSetSlotType = 45;
    private const string CombatKey = "combat_area";
    // Firing area config rows: 128-byte areas with their slot at +16, 32-byte sets with their slot at +12 and members at +16.
    private const uint AreaRowClass = 0x80808354, SetRowClass = 0x80808350, GroupRowClass = 0x80807D8F;
    private const int AreaSize = 128, AreaSlotField = 16, AreaSectorField = 32, SetSize = 32, SetSlotField = 12, SetMembersField = 16, MemberSize = 16;
    // A set member: the area identity, an area row (-1 when the identity names it), then a flag word.
    private const int MemberFlagField = 12;
    private static readonly int SlotKeyField = TagView.FieldOffset<SlotComponentHeader>(nameof(SlotComponentHeader.RegistryKey)),
        SlotBubbleField = TagView.FieldOffset<SlotComponentHeader>(nameof(SlotComponentHeader.BubbleIndex));

    /**
     * Gives a squad owner the world made its own combat objective and firing areas, copied from the
     * patrol object it was made from. Every firing area becomes one box over `centre`, so any task of
     * the objective sends its squads there. Add the owner's squads first: the new slots end its table.
     * @param centre The middle of the box's floor, in map space.
     * @param halfSize Half the box's width on x and y, in metres.
     * @param height How far the box reaches up from its floor.
     */
    public static AddedCombatArea AddCombatArea(WorldFiles world, RegistryIndex schema, PackageCatalog catalog, string destination, string ownerFile,
        Vector3 centre, float halfSize = 40, float height = 30, CancellationToken token = default)
    {
        var owner = world[ownerFile];
        if (OwnerTemplate(owner) is not uint template) throw new InvalidDataException($"{ownerFile} is not a squad owner the world made.");
        if (CombatSlots(owner) is not null) throw new InvalidDataException($"{ownerFile} already has a combat area.");
        if (!(halfSize > 0) || !(height > 0) || !float.IsFinite(centre.X + centre.Y + centre.Z))
            throw new ArgumentOutOfRangeException(nameof(centre), "A combat area needs a finite centre and a positive size.");
        byte[] installed = catalog.Read(template);
        uint oldKey = TagView.At<SAuthoredObject>(installed, 0).RegistryKey;
        uint key = TagView.At<SAuthoredObject>(owner.Data, 0).RegistryKey;
        var oldRows = TagView.Rows(installed, in TagView.At<SAuthoredObject>(installed, 0).SlotTable.Slots, ObjectSlot.Class).ToArray();

        var objective = ComponentSlotCatalog.OwnerSlots(catalog, template, token)
            .FirstOrDefault(row => row.SlotType == ObjectiveSlotType && row.Path.Length == 3)
            ?? throw new InvalidDataException($"0x{template:X8} has no objective reached through one set and one link.");
        var parents = Reach(catalog, schema, template, token);
        uint areaConfig = parents.Keys.FirstOrDefault(tag => catalog.TryGetEntry(tag, out var entry) && entry.Reference == SEntityConfig.Class
            && ArrayOf(catalog.Read(tag), AreaRowClass) >= 0 && SlotRefs(catalog.Read(tag), oldKey).Any(at => TagRead.U16(catalog.Read(tag), at + 4) == FiringAreaSlotType));
        if (areaConfig == 0) throw new InvalidDataException($"0x{template:X8} has no firing area config.");
        uint areaLink = parents[areaConfig].Single(), areaSet = parents[areaLink].Single();

        // New slots: the objective ends the first group; the sets, then the areas, end the table.
        var head = TagView.At<SAuthoredObject>(owner.Data, 0);
        uint groups = TagRead.U32(owner.Data, GroupsField);
        int first = (int)(groups & 0xFFFF), rest = (int)(groups >> 16);
        if (rest != 0) throw new InvalidDataException($"{ownerFile} has slots past its first group.");
        string ownerName = OwnerName(owner);
        var map = new Dictionary<(ushort Type, ushort Index), CombatSlot>
        {
            [(ObjectiveSlotType, objective.SlotIndex)] = new((ushort)first, objective.SlotIndex, ObjectiveSlotType, $"{ownerName}.objective"),
        };
        byte[] areaBytes = catalog.Read(areaConfig);
        int next = first + 1;
        foreach (ushort type in (ReadOnlySpan<ushort>)[FiringAreaSetSlotType, FiringAreaSlotType])
        {
            var indices = SlotRefs(areaBytes, oldKey).Where(at => TagRead.U16(areaBytes, at + 4) == type).Select(at => TagRead.U16(areaBytes, at + 6))
                .Distinct().Order().ToList();
            string kind = type == FiringAreaSetSlotType ? "firing_area_set" : "firing_area";
            for (int k = 0; k < indices.Count; k++)
            {
                if (indices[k] >= oldRows.Length || oldRows[indices[k]].SlotType != type)
                    throw new InvalidDataException($"0x{areaConfig:X8} names slot {indices[k]} of 0x{template:X8} as type {type}, which it is not.");
                map[(type, indices[k])] = new((ushort)next++, indices[k], type, $"{ownerName}.{kind}_{k}");
            }
        }
        // Every provider the objective names must be one of the copied sets.
        byte[] objectiveBytes = catalog.Read(objective.ConfigTag);
        foreach (int at in SlotRefs(objectiveBytes, oldKey))
            if (!map.ContainsKey((TagRead.U16(objectiveBytes, at + 4), TagRead.U16(objectiveBytes, at + 6))))
                throw new InvalidDataException($"Objective 0x{objective.ConfigTag:X8} names slot {TagRead.U16(objectiveBytes, at + 6)}, which the firing area config does not.");

        string File(string slug, uint classId) =>
            $"{WorldTemplate.Folder}/{destination}/{WorldTemplate.TypeName(schema, classId)}/{slug}.{WorldTemplate.TypeName(schema, classId)}";
        string slugBase = "combat_" + Slug(ownerName);
        var (objectiveSet, objectiveConfig) = CloneChain(world, schema, catalog, objective.Path[0].Tag, objective.Path[1].Tag, objective.ConfigTag, slugBase + "_objective", File);
        var (firingSet, firingConfig) = CloneChain(world, schema, catalog, areaSet, areaLink, areaConfig, slugBase + "_areas", File);

        // The objective's handle joins the squads' block; the areas keep a block like their template's.
        int areaBlock = BlockHolding(installed, areaSet);
        int bubble = 0;
        world.Edit(ownerFile, (_, fields) =>
        {
            var slots = (JsonArray)WorldFiles.Field((JsonObject)WorldFiles.Field(fields, "slot_table"), "slots");
            foreach (var slot in map.Values.OrderBy(slot => slot.Index))
                slots.Add(new JsonObject { ["name_hash"] = WorldFiles.HashText(StringBankWriter.Hash(slot.Name)), ["slot_type"] = (int)slot.SlotType });
            var blocks = (JsonArray)WorldFiles.Field(fields, "entry_filters");
            var squads = (JsonObject)blocks[0]!;
            bubble = (int)WorldFiles.Field(squads, "bubble_index");
            ((JsonArray)WorldFiles.Field(squads, "handles")).Add(new JsonObject { ["handle"] = objectiveSet });
            var areas = (JsonObject)squads.DeepClone();
            areas["bubble_index"] = TagView.Rows(installed, in TagView.At<SAuthoredObject>(installed, 0).EntryFilters, ObjectBubbleBlock.Class)[areaBlock].BubbleIndex;
            var handles = (JsonArray)WorldFiles.Field(areas, "handles");
            handles.Clear();
            handles.Add(new JsonObject { ["handle"] = firingSet });
            blocks.Add(areas);
        });
        var edited = world[ownerFile];
        byte[] ownerData = edited.Data.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(ownerData.AsSpan(GroupsField), (uint)(first + 1) | (uint)(map.Count - 1) << 16);
        var info = JsonNode.Parse(edited.Info!)!.AsObject();
        info[CombatKey] = new JsonArray(map.Values.OrderBy(slot => slot.Index).Select(slot => (JsonNode)new JsonObject
        {
            ["index"] = slot.Index, ["template_index"] = slot.TemplateIndex, ["slot_type"] = slot.SlotType, ["name"] = slot.Name,
        }).ToArray());
        world.Replace(new TftFile
        {
            Name = edited.Name, TagType = edited.TagType, GameName = edited.GameName, ClassId = edited.ClassId, TypeInfo = edited.TypeInfo,
            BuildHash = edited.BuildHash, Layout = edited.Layout, Data = ownerData, References = edited.References, Meta = edited.Meta, Info = info.ToJsonString(),
        });

        // Identities of areas and sets are global, so the copies take new ones derived from the new key.
        ulong Identity(ulong old) => old == ulong.MaxValue ? old
            : BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes($"{key:X8}/{old:X16}")));
        Retarget(world, objectiveConfig, oldKey, key, map, bubble, objective.DescriptorOffset);
        Retarget(world, firingConfig, oldKey, key, map, null, -1);
        var fire = world[firingConfig];
        byte[] data = fire.Data.ToArray();
        var corners = new[] { new Vector3(-halfSize, -halfSize, 0), new Vector3(halfSize, -halfSize, 0), new Vector3(halfSize, halfSize, 0), new Vector3(-halfSize, halfSize, 0) }
            .Select(corner => corner + centre).ToArray();
        var (areaCount, areaRows, _) = TagArray.Checked(data, ArrayOf(data, AreaRowClass));
        for (int row = 0; row < areaCount; row++)
        {
            int at = areaRows + row * AreaSize;
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(at), Identity(TagRead.U64(data, at)));
            ushort oldIndex = (ushort)TagRead.U32(data, at + AreaSlotField);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at + AreaSlotField), map[(FiringAreaSlotType, oldIndex)].Index);
            Box(data, at + AreaSectorField, corners, height);
        }
        var (setCount, setRows, _) = TagArray.Checked(data, ArrayOf(data, SetRowClass));
        for (int row = 0; row < setCount; row++)
        {
            int at = setRows + row * SetSize;
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(at), Identity(TagRead.U64(data, at)));
            ushort oldIndex = (ushort)TagRead.U32(data, at + SetSlotField);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at + SetSlotField), map[(FiringAreaSetSlotType, oldIndex)].Index);
            var (members, memberRows, _) = TagArray.Checked(data, at + SetMembersField);
            for (int member = 0; member < members; member++)
            {
                int entry = memberRows + member * MemberSize;
                BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(entry), Identity(TagRead.U64(data, entry)));
                // All areas are one box now; a group whose set flags a member 1 held still in game.
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(entry + MemberFlagField), 0);
            }
        }
        world.Replace(WorldDevices.With(fire, data, fire.References));
        int groupCount = TagArray.Checked(world[objectiveConfig].Data, ArrayOf(world[objectiveConfig].Data, GroupRowClass)).Count;
        var all = map.Values.OrderBy(slot => slot.Index).ToList();
        return new(ownerFile, all[0], all.Skip(1).ToList(), groupCount, objectiveConfig, firingConfig);
    }

    /** The combat slots of a squad owner the world made, in slot order; null when it has none. */
    public static IReadOnlyList<CombatSlot>? CombatSlots(TftFile owner)
    {
        if (OwnerTemplate(owner) is null) return null;
        try
        {
            return JsonNode.Parse(owner.Info!)?[CombatKey] is JsonArray rows
                ? rows.OfType<JsonObject>().Select(row => new CombatSlot((ushort)row["index"]!, (ushort)row["template_index"]!, (ushort)row["slot_type"]!,
                    (string)row["name"]!)).ToList()
                : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static string OwnerName(TftFile owner) => (string)JsonNode.Parse(owner.Info!)![OwnerKey]!;

    /** Copies one set, link and config chain into the world. @return The new set's and config's file names. */
    private static (string Set, string Config) CloneChain(WorldFiles world, RegistryIndex schema, PackageCatalog catalog, uint set, uint link, uint config,
        string slug, Func<string, uint, string> file)
    {
        string setFile = file(slug, SAuthoredObjectSet.Class), linkFile = file(slug, SEntityLink.Class), configFile = file(slug, SEntityConfig.Class);
        if (world.Contains(setFile) || world.Contains(linkFile) || world.Contains(configFile))
            throw new InvalidDataException($"The world already has files named like {slug}.");
        string oldConfig = TftNames.Installed(config), info = new JsonObject { [CombatKey] = slug }.ToJsonString();
        // A chain is copied many times (once per door), so its clone is made once per catalog and renamed.
        var chains = ChainClones.GetValue(catalog, _ => []);
        // The stand-in names keep each file's type suffix, which the clone takes its tag type from.
        string setName = file(ChainStandIn, SAuthoredObjectSet.Class), linkName = file(ChainStandIn, SEntityLink.Class),
            configName = file(ChainStandIn, SEntityConfig.Class);
        if (!chains.TryGetValue((set, link, config, setName), out var cached))
        {
            var source = new CatalogTagSource(catalog);
            cached = [.. TftClone.Clone([set, link], source, schema, tag => tag == set ? setName : linkName),
                TftClone.Clone([config], source, schema, _ => configName).Single()];
            chains[(set, link, config, setName)] = cached;
        }
        string Renamed(string name) => name == setName ? setFile : name == linkName ? linkFile : name == configName || name == oldConfig ? configFile : name;
        foreach (var made in cached)
            world.Add(new TftFile
            {
                Name = Renamed(made.Name), TagType = made.TagType, ClassId = made.ClassId, TypeInfo = made.TypeInfo, BuildHash = made.BuildHash,
                Layout = made.Layout, Data = made.Data.ToArray(), Info = info,
                // The config keeps its own references to the installed config's targets; only the chain's names move.
                References = made.References.Select(reference => made.Name == configName && reference.Target == oldConfig ? reference
                    : reference with { Target = Renamed(reference.Target) }).ToList(),
            });
        return (setFile, configFile);
    }

    // The slug of a cached chain clone's stand-in file names; no real slug holds a colon.
    private const string ChainStandIn = ":chain";
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PackageCatalog, Dictionary<(uint, uint, uint, string), TftFile[]>> ChainClones = new();

    /**
     * Points every slot ref of the template's key in a world config at the owner's key and new index.
     * @param bubble The bubble index for the descriptor at `descriptor`; null keeps every bubble index.
     */
    private static void Retarget(WorldFiles world, string configFile, uint oldKey, uint key,
        IReadOnlyDictionary<(ushort Type, ushort Index), CombatSlot> map, int? bubble, int descriptor, ushort[]? types = null)
    {
        var file = world[configFile];
        byte[] data = file.Data.ToArray();
        foreach (int at in types is null ? SlotRefs(data, oldKey) : SlotRefs(data, oldKey, types))
        {
            var slot = map[(TagRead.U16(data, at + 4), TagRead.U16(data, at + 6))];
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), key);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at + 6), slot.Index);
        }
        if (bubble is int index)
        {
            if (TagRead.U32(data, descriptor + SlotKeyField) != key) throw new InvalidDataException($"{configFile} has no descriptor at {descriptor}.");
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(descriptor + SlotBubbleField), index);
        }
        world.Replace(WorldDevices.With(file, data, file.References));
    }

    /** Offsets of every 8-byte slot ref of `key`: the key, then a combat slot type and an index as u16. */
    private static List<int> SlotRefs(ReadOnlySpan<byte> data, uint key) => SlotRefs(data, key, [ObjectiveSlotType, FiringAreaSlotType, FiringAreaSetSlotType]);

    /** Offsets of every 8-byte slot ref of `key` whose slot type is one of `types`. */
    private static List<int> SlotRefs(ReadOnlySpan<byte> data, uint key, ReadOnlySpan<ushort> types)
    {
        var found = new List<int>();
        for (int at = 0; at + 8 <= data.Length; at += sizeof(uint))
            if (BinaryPrimitives.ReadUInt32LittleEndian(data[at..]) == key
                && types.Contains(BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 4)..])))
                found.Add(at);
        return found;
    }

    /** The field of the first array of `elementClass` in the bytes; -1 when there is none. */
    private static int ArrayOf(ReadOnlySpan<byte> data, uint elementClass)
    {
        for (int field = 0; field + 16 <= data.Length; field += 8)
            if (TagArray.TryChecked(data, field, 0, 0, out var array) && array.Count > 0 && array.Class == elementClass) return field;
        return -1;
    }

    /** The row of the object's bubble blocks whose handles name `set`. */
    private static int BlockHolding(byte[] owner, uint set)
    {
        var blocks = TagView.Rows(owner, in TagView.At<SAuthoredObject>(owner, 0).EntryFilters, ObjectBubbleBlock.Class);
        for (int row = 0; row < blocks.Length; row++)
            foreach (ref readonly var handle in TagView.Rows(owner, in blocks[row].Handles, PlacedHandle.Class))
                if (handle.Handle == set) return row;
        throw new InvalidDataException($"No bubble block names 0x{set:X8}.");
    }

    /**
     * Makes one firing area sector the box over `corners`. The sector keeps its vertex and triangle
     * counts: the first four vertices are the corners, the rest repeat the last corner, and the
     * triangles past the first two are empty.
     */
    private static void Box(byte[] data, int sector, IReadOnlyList<Vector3> corners, float height)
    {
        ref var view = ref TagView.Edit<C80809b8f>(data, sector);
        var points = TagView.Rows(data, in view.Vertices, SVec4Point.Class);
        var triangles = TagView.Rows(data, in view.Triangles, TriggerVolumeTriangle.Class);
        if (points.Length < 4 || triangles.Length < 2) throw new InvalidDataException("A firing area has fewer than four vertices or two triangles.");
        int vertices = TagView.OffsetOf(data, in points[0]), faces = TagView.OffsetOf(data, in triangles[0]);
        int stride = System.Runtime.CompilerServices.Unsafe.SizeOf<SVec4Point>();
        for (int index = 0; index < points.Length; index++)
            TagView.Edit<SVec4Point>(data, vertices + index * stride).Position = corners[Math.Min(index, 3)];
        data.AsSpan(faces, triangles.Length * 3).Clear();
        ((ReadOnlySpan<byte>)[0, 1, 2, 0, 2, 3]).CopyTo(data.AsSpan(faces));
        ref var box = ref TagView.Edit<C80809b8f>(data, sector);
        box.BoundsMin = new(corners.Min(corner => corner.X), corners.Min(corner => corner.Y), corners[0].Z);
        box.BoundsMax = new(corners.Max(corner => corner.X), corners.Max(corner => corner.Y), corners[0].Z + height);
        box.ExtrusionHeight = height;
    }
}
