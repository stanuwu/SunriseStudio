using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Graph;
using SunriseStudio.Tags.Tft;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.World;

public static partial class WorldSquadAuthoring
{
    internal const string RuleKey = "squad_rule", ListKey = "squad_point_list", RowKey = "squad_point_row", PointKey = "squad_point_identity",
        SharedRuleKey = "squad_shared_rule";

    /** The index a stock slot of the owner holds now: its copied descriptor's, else its installed one. */
    private static ushort CurrentIndex(WorldFiles world, PackageCatalog catalog, System.Collections.Immutable.ImmutableArray<ComponentSlotEvidence> evidence, ushort installed)
    {
        var row = evidence.FirstOrDefault(slot => slot.SlotIndex == installed);
        var copy = row is null ? null : world.Files.FirstOrDefault(file => file.SourceTag == row.ConfigTag);
        return copy is null ? installed : TagRead.U16(copy.Data, row!.DescriptorOffset + TagView.FieldOffset<SlotComponentHeader>(nameof(SlotComponentHeader.SlotIndex)));
    }

    /**
     * Adds a squad in the rule form of one of the owner's stock squads: the spawner and its one-point
     * rule on new slots, with a new placement row. The squad slot ends the first slot group, where
     * networked slots sit; a new rule slot has no schema, so it ends the table.
     * @param spawner An installed spawner config of the owner whose rule refs name a one-point rule.
     * @param growCandidates False keeps the template's candidate count.
     * @param existingRow A row of the rule's list to use as the point as it stands, instead of a new row at the position.
     * @param options Which fields differ from the stock squad; null makes all of them new.
     */
    public static AddedSquad AddRuleSquad(WorldFiles world, RegistryIndex schema, PackageCatalog catalog, string destination, uint spawner,
        NewSquad squad, string ownerFile, bool growCandidates = true, int? existingRow = null, CancellationToken token = default,
        RuleSquadOptions? options = null)
    {
        options ??= new RuleSquadOptions();
        CheckSquad(world, catalog, squad);
        var owner = world[ownerFile];
        if (owner.ClassId != SAuthoredObject.Class || owner.SourceTag is not uint source)
            throw new InvalidDataException($"{owner.Name} is not a copied authored object.");
        var evidence = ComponentSlotCatalog.OwnerSlots(catalog, source, token);
        var spawnRow = evidence.FirstOrDefault(row => row.ConfigTag == spawner && row.SlotType == SquadSlotType && row.Path.Length == 3
                && SingleTarget(catalog, row.Path[0].Tag))
            ?? throw new InvalidDataException($"0x{spawner:X8} is not a squad of {owner.Name} reached through one set and one link.");
        byte[] spawnBytes = catalog.Read(spawner);
        var spawnParts = SquadConfig.Find(spawnBytes, rules: true) ?? throw new InvalidDataException($"0x{spawner:X8} is not a one-member spawner.");
        ushort ruleIndex = SquadConfig.RuleIndex(spawnBytes, spawnParts);
        var (installedA, installedB) = SquadConfig.RuleRefs(spawnBytes, spawnParts);
        var ruleRow = evidence.FirstOrDefault(row => row.SlotType == SquadConfig.RuleSlotType && row.SlotIndex == ruleIndex && row.Path.Length == 3
                && SingleTarget(catalog, row.Path[0].Tag))
            ?? throw new InvalidDataException($"0x{spawner:X8} names no spawn rule of {owner.Name} reached through one set and one link.");
        ulong oldPoint = SquadConfig.RulePoint(catalog.Read(ruleRow.ConfigTag))
            ?? throw new InvalidDataException($"Rule 0x{ruleRow.ConfigTag:X8} does not have exactly one point.");
        var point = WorldObjectPoints.List(world, catalog, token).FirstOrDefault(row => row.Identity == oldPoint && row.OwnerFile == owner.Name)
            ?? throw new InvalidDataException($"{owner.Name} reaches no placement row 0x{oldPoint:X16} for rule 0x{ruleRow.ConfigTag:X8}.");

        byte[] held = owner.Data;
        uint key = TagView.At<SAuthoredObject>(held, 0).RegistryKey, groups = TagRead.U32(held, GroupsField);
        var types = TagView.Rows(held, in TagView.At<SAuthoredObject>(held, 0).SlotTable.Slots, ObjectSlot.Class).ToArray();
        int first = (int)(groups & 0xFFFF), rest = (int)(groups >> 16);
        string ruleName = squad.Name + ".spawn_rule";
        uint nameHash = StringBankWriter.Hash(squad.Name), ruleHash = StringBankWriter.Hash(ruleName);
        if (first + rest != types.Length) throw new InvalidDataException($"{owner.Name} slot groups do not add up to its {types.Length} slots.");
        if (types.Any(row => row.NameHash == nameHash || row.NameHash == ruleHash)) throw new InvalidDataException($"{owner.Name} already has a slot named {squad.Name}.");
        Renumber(world, schema, catalog, destination, owner.Name, source, key, types.Select(row => (ushort)row.SlotType).ToArray(), first, token);
        // The squad slot goes in at the first group's end, so the table end is one further.
        int ruleSlot = types.Length + 1;

        string slug = Slug(squad.Name);
        string File(uint classId, string suffix) =>
            $"{WorldTemplate.Folder}/{destination}/{WorldTemplate.TypeName(schema, classId)}/squad_{slug}{suffix}.{WorldTemplate.TypeName(schema, classId)}";
        string configFile = File(SEntityConfig.Class, ""), ruleFile = File(SEntityConfig.Class, "_rule");
        string setFile = File(SAuthoredObjectSet.Class, ""), ruleSetFile = File(SAuthoredObjectSet.Class, "_rule");
        if (world.Files.Any(file => file.Name == configFile || file.Name == ruleFile || file.Name == setFile || file.Name == ruleSetFile))
            throw new InvalidDataException($"The world already has files for a squad named like {squad.Name}.");

        // The point: a new row at the asked position, a given row, or with the stock rule its own row.
        ulong pointIdentity = WorldContent.PlacementIdentity($"squad-point/{destination}/{squad.Name}");
        string from = owner.Name;
        foreach (uint hop in (ReadOnlySpan<uint>)[point.SetTag, point.ListTag])
        {
            string name = world.Files.FirstOrDefault(file => file.SourceTag == hop)?.Name ?? WorldDevices.Copy(world, schema, catalog, destination, hop);
            WorldDevices.Repoint(world, from, hop, name);
            from = name;
        }
        string listFile = world.Files.Single(file => file.SourceTag == point.ListTag).Name;
        int pointRow;
        int? reuseRow = options.OwnRule ? existingRow ?? (options.NewRow ? null : point.Row) : point.Row;
        if (reuseRow is int reuse)
        {
            var rows = WorldObjectPoints.List(world, catalog, token).Where(row => row.ListTag == point.ListTag && row.Row == reuse).ToList();
            if (rows.Count == 0) throw new InvalidDataException($"List 0x{point.ListTag:X8} has no row {reuse}.");
            pointRow = reuse;
            pointIdentity = rows[0].Identity;
        }
        else pointRow = AppendPlacement(world, schema, listFile, point.Row, squad.Position, pointIdentity);

        var infoNode = new JsonObject
        {
            [NameKey] = squad.Name, [TemplateKey] = $"0x{spawner:X8}", [ListKey] = $"0x{point.ListTag:X8}",
            [RowKey] = pointRow, [PointKey] = $"0x{pointIdentity:X16}",
        };
        if (options.OwnRule) infoNode[RuleKey] = ruleFile;
        else infoNode[SharedRuleKey] = $"0x{ruleRow.ConfigTag:X8}";
        string info = infoNode.ToJsonString();
        string ruleInfo = new JsonObject { [RuleKey + "_of"] = squad.Name }.ToJsonString();
        AddChain(world, schema, catalog, spawnRow, setFile, File(SEntityLink.Class, ""), configFile, info);
        if (options.OwnRule) AddChain(world, schema, catalog, ruleRow, ruleSetFile, File(SEntityLink.Class, "_rule"), ruleFile, ruleInfo);

        int spawnBlock = BlockIndex(catalog.Read(source), spawnRow.SubblockOffset), ruleBlock = BlockIndex(catalog.Read(source), ruleRow.SubblockOffset);
        int bubble = 0;
        world.Edit(owner.Name, (_, fields) =>
        {
            var slots = (JsonArray)WorldFiles.Field((JsonObject)WorldFiles.Field(fields, "slot_table"), "slots");
            slots.Insert(first, new JsonObject { ["name_hash"] = WorldFiles.HashText(nameHash), ["slot_type"] = (int)SquadSlotType });
            if (options.OwnRule) slots.Add(new JsonObject { ["name_hash"] = WorldFiles.HashText(ruleHash), ["slot_type"] = (int)SquadConfig.RuleSlotType });
            var blocks = (JsonArray)WorldFiles.Field(fields, "entry_filters");
            bubble = (int)WorldFiles.Field((JsonObject)blocks[spawnBlock]!, "bubble_index");
            ((JsonArray)WorldFiles.Field((JsonObject)blocks[spawnBlock]!, "handles")).Add(new JsonObject { ["handle"] = setFile });
            if (options.OwnRule) ((JsonArray)WorldFiles.Field((JsonObject)blocks[ruleBlock]!, "handles")).Add(new JsonObject { ["handle"] = ruleSetFile });
        });
        var edited = world[owner.Name];
        byte[] data = edited.Data.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(GroupsField), (uint)(first + 1) | (uint)(rest + (options.OwnRule ? 1 : 0)) << 16);
        world.Replace(WorldDevices.With(edited, data, edited.References));

        var clone = TftClone.Clone([spawner], new CatalogTagSource(catalog), schema, _ => configFile).Single();
        int count = growCandidates ? squad.Count : SquadConfig.LaneCount(clone.Data, SquadConfig.Find(clone.Data, rules: true)!.Value);
        // A shared rule keeps the stock refs, at the indices the stock rule slots hold now.
        (ushort, ushort) refs = options.OwnRule ? ((ushort)ruleSlot, (ushort)ruleSlot)
            : (CurrentIndex(world, catalog, evidence, installedA), CurrentIndex(world, catalog, evidence, installedB));
        world.Replace(SquadConfig.Make(clone, schema, key, (ushort)first, bubble, squad.Name, OwnIdentity(destination, squad.Name), squad.Position,
            count, options.Retarget ? squad.Entity : SquadConfig.Read(clone)?.Entity ?? squad.Entity, info, pairIdentity: options.NewIdentity,
            ruleRefs: refs, writeName: options.NewName));
        if (options.OwnRule)
        {
            var rule = TftClone.Clone([ruleRow.ConfigTag], new CatalogTagSource(catalog), schema, _ => ruleFile).Single();
            world.Replace(SquadConfig.MakeRule(rule, schema, key, (ushort)ruleSlot, pointIdentity, ruleInfo));
        }
        var placed = WorldObjectPoints.List(world, catalog, token).First(row => row.ListTag == point.ListTag && row.Row == pointRow).Position;
        return new(squad.Name, owner.Name, (ushort)first, configFile, pointIdentity, placed, count, squad.Entity);
    }

    /** Adds a new set, link and config file copied from a slot's chain; the config is the bare clone, patched by the caller. */
    private static void AddChain(WorldFiles world, RegistryIndex schema, PackageCatalog catalog, ComponentSlotEvidence row, string setFile,
        string linkFile, string configFile, string info)
    {
        uint set = row.Path[0].Tag, link = row.Path[1].Tag;
        string oldConfig = TftNames.Installed(row.ConfigTag);
        foreach (var file in TftClone.Clone([set, link], new CatalogTagSource(catalog), schema, tag => tag == set ? setFile : linkFile))
            world.Add(new TftFile
            {
                Name = file.Name, TagType = file.TagType, ClassId = file.ClassId, TypeInfo = file.TypeInfo, BuildHash = file.BuildHash,
                Layout = file.Layout, Data = file.Data, Info = info,
                References = file.References.Select(reference => reference.Target == oldConfig ? reference with { Target = configFile } : reference).ToList(),
            });
        var config = TftClone.Clone([row.ConfigTag], new CatalogTagSource(catalog), schema, _ => configFile).Single();
        world.Add(WorldDevices.With(config, config.Data, config.References));
    }

    /**
     * Appends a copy of one placement row to a list the world owns, at a position with a new identity.
     * A list that does not end its tag moves to the end, so every reference inside it moves too.
     * @return The new row's index.
     */
    private static int AppendPlacement(WorldFiles world, RegistryIndex schema, string listFile, int sourceRow, Vector3 position, ulong identity)
    {
        var list = world[listFile];
        byte[] data = list.Data;
        int field = TagView.FieldOffset<SMapPlacements>(nameof(SMapPlacements.Placements));
        var (count, rows, _) = TagArray.Checked(data, field);
        int stride = System.Runtime.CompilerServices.Unsafe.SizeOf<AuthoredPlacementEntry>(), source = rows + sourceRow * stride;
        var document = TagDocument.Parse(data, SMapPlacements.Class, schema);
        byte[] output = TagRelocator.AppendRows(document, schema, field, [data.AsSpan(source, stride).ToArray()], [source]);
        var (_, moved, _) = TagArray.Checked(output, field);
        int at = moved + count * stride, delta = moved - rows;
        TagView.Edit<AuthoredPlacementEntry>(output, at).Position = position;
        TagView.Edit<AuthoredPlacementEntry>(output, at).PlacementIdentity = identity;
        var references = list.References.Select(reference => reference.Offset >= rows && reference.Offset < rows + count * stride
            ? reference with { Offset = reference.Offset + delta } : reference).ToList();
        references.AddRange(list.References.Where(reference => reference.Offset >= source && reference.Offset < source + stride)
            .Select(reference => reference with { Offset = at + reference.Offset - source }));
        world.Replace(new TftFile
        {
            Name = list.Name, TagType = list.TagType, GameName = list.GameName, ClassId = list.ClassId, TypeInfo = list.TypeInfo, BuildHash = list.BuildHash,
            Layout = TftLayout.Extract(schema, output, list.ClassId), Data = output, References = references.OrderBy(x => x.Offset).ToList(),
            Info = list.Info, Meta = list.Meta,
        });
        return (int)count;
    }
}
