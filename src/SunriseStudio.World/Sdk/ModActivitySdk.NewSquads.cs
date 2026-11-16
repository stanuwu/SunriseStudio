using System.Numerics;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Tft;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.World.Sdk;

public static partial class ModActivitySdk
{
    /** Squad slot schema: component, sense and auth classes, and the exact-join slot flag. */
    private const uint SquadComponent = SSquadComponent.Class, SquadSense = 0x80807ECC, SquadAuth = 0x80807EC9, SlotJoinExact = 0x2;
    /** Squad flags: descriptor, rule edge, occurrence and points exact, member count valid, counts complete. */
    private const uint NewSquadFlags = 0x3F;
    /** A squad with no point: descriptor and occurrence exact, counts valid and complete, a spawn rule needed. */
    private const uint UnboundSquadFlags = 0x75;
    /** Member flags: actor class exact, counts complete, counts equal per lane, no null candidate, profile exact. */
    private const uint MemberActorExact = 0x1, MemberCountsReady = 0xE, MemberProfileExact = 0x10;
    /** Anchor flags: exact and inline, so the point is the config's own placement. */
    private const uint InlineAnchorFlags = 0x3;
    /** Anchor flag of a rule point in an object list the occurrence owns. */
    private const uint ListAnchorFlags = 0x1;
    /** Spawn rule slot schema: the rule component, with no sense or auth schema. */
    private const uint RuleComponent = SSquadRuleComponent.Class;
    /** A slot alias text row: the text, then a kind word the installed rows set to 3. */
    private const uint AliasKind = 3;

    /** One squad the world added, as its tags give it. */
    private sealed record NewSquadRow(string ConfigFile, uint ConfigTag, string Name, uint OwnerTag, ushort SlotIndex, uint BubbleIndex,
        uint MemberKey, int Count, ulong Identity, Vector3 Position, uint Entity)
    {
        /** A rule-form squad's rule config, rule slot and point row in an installed list's world copy. */
        public uint RuleTag { get; init; }
        public ushort RuleIndex { get; init; } = ushort.MaxValue;
        public uint ListTag { get; init; }
        public int PointRow { get; init; } = -1;
        public bool RuleForm => RuleIndex != ushort.MaxValue;
        /** Slots the squad adds to its owner: the squad, and its rule in the rule form. */
        public int SlotCount => RuleForm ? 2 : 1;
    }

    private sealed partial class Destination
    {
        /** New squads by owner's installed tag, in slot order. */
        public Dictionary<uint, List<NewSquadRow>> NewSquads { get; } = [];
        /** Per owner's installed tag: the first new slot index. Installed slots from there move up. */
        public Dictionary<uint, int> InsertedAt { get; } = [];
        /** The pack slot row each new squad's slot got, by config file. */
        public Dictionary<string, uint> NewSlotRows { get; } = [];
        public HashSet<uint> SlotsWritten { get; } = [];
        /** Squad owners the world made, by built tag: their key, installed template and place in their tag set list. */
        public Dictionary<uint, (uint Key, uint Template, int Ordinal)> MadeOwners { get; } = [];
        /** The states whose tag sets list a made door owner, with its row there, by built tag. */
        public Dictionary<uint, IReadOnlyList<(int Bubble, int State, int Ordinal)>> DoorStates { get; } = [];
        /** The combat slots of each made owner that has a combat area, by built tag. */
        public Dictionary<uint, IReadOnlyList<CombatSlot>> MadeCombat { get; } = [];
        /** Task group rows of made owners' objectives; they follow the copied rows. */
        public List<uint[]> MadeGroupRows { get; } = [];

        /**
         * Finds the squads the world added: new inline spawner configs that carry a squad name, and the
         * owner whose key their descriptor holds. The new slots sit just below the owner's second slot group.
         */
        public void ReadNewSquads(IReadOnlyList<TftFile> files, Func<TftFile, uint> built, IReadOnlyDictionary<string, uint> copies)
        {
            var owners = files.Where(file => file.ClassId == SAuthoredObject.Class && (file.SourceTag is not null || WorldSquadAuthoring.OwnerTemplate(file) is not null))
                .ToLookup(file => TagView.At<SAuthoredObject>(file.Data, 0).RegistryKey);
            uint OwnerTag(TftFile owner) => owner.SourceTag ?? built(owner);
            var world = new WorldFiles(files);
            foreach (var owner in owners.SelectMany(group => group).Where(owner => owner.SourceTag is null))
            {
                MadeOwners[built(owner)] = (TagView.At<SAuthoredObject>(owner.Data, 0).RegistryKey, WorldSquadAuthoring.OwnerTemplate(owner)!.Value,
                    TagSetOrdinal(files, owner.Name));
                var made = (WorldSquadAuthoring.CombatSlots(owner) ?? []).Concat(WorldSquadAuthoring.DoorSlots(owner) ?? []).OrderBy(slot => slot.Index).ToList();
                if (made.Count > 0) MadeCombat[built(owner)] = made;
                // A door or squad owner that loads with one bubble occurs only where a tag set lists it.
                var listed = WorldSquadAuthoring.ListedIn(world, owner.Name);
                if (WorldSquadAuthoring.DoorSlots(owner) is { Count: > 0 } || listed.Count < WorldSquadAuthoring.StateRegistries(world).Count)
                    DoorStates[built(owner)] = listed;
            }
            foreach (var file in files.Where(file => file.ClassId == SEntityConfig.Class && file.SourceTag is null && file.CopyOf is null))
            {
                if (WorldSquadAuthoring.NameOf(file) is not string name) continue;
                var facts = SquadConfig.Read(file) ?? throw new InvalidDataException($"{file.Name} names squad {name} but is not an inline spawner.");
                var owner = owners[facts.RegistryKey].SingleOrDefault() ?? throw new InvalidDataException($"No one copied object owns squad {name}.");
                uint entity = facts.Entity is null ? 0 : facts.Entity.StartsWith('#') ? (uint)TftNames.InstalledHash(facts.Entity) : copies.GetValueOrDefault(facts.Entity);
                if (!NewSquads.TryGetValue(OwnerTag(owner), out var list)) NewSquads[OwnerTag(owner)] = list = [];
                var row = new NewSquadRow(file.Name, built(file), name, OwnerTag(owner), facts.SlotIndex, facts.BubbleIndex, facts.MemberKey, facts.Count,
                    facts.Identity, facts.Position, entity);
                if (facts.RuleIndex != ushort.MaxValue)
                {
                    // The rule form keeps its point in the info: the list, the row, its identity, and its own or the stock rule.
                    var info = (JsonObject)JsonNode.Parse(file.Info!)!;
                    string? ruleFile = WorldFiles.Text(info[WorldSquadAuthoring.RuleKey]);
                    string? shared = WorldFiles.Text(info[WorldSquadAuthoring.SharedRuleKey]);
                    if (ruleFile is null && shared is null) throw new InvalidDataException($"{file.Name} names no rule config.");
                    row = row with { Identity = Convert.ToUInt64(WorldFiles.Text(info[WorldSquadAuthoring.PointKey])![2..], 16) };
                    row = row with
                    {
                        RuleTag = ruleFile is not null ? built(files.Single(item => item.Name == ruleFile)) : Tag(Convert.ToUInt32(shared![2..], 16)),
                        RuleIndex = ruleFile is not null ? facts.RuleIndex : ushort.MaxValue,
                        ListTag = Convert.ToUInt32(WorldFiles.Text(info[WorldSquadAuthoring.ListKey])![2..], 16), PointRow = (int)info[WorldSquadAuthoring.RowKey]!,
                        Position = PointPosition(files, Convert.ToUInt32(WorldFiles.Text(info[WorldSquadAuthoring.ListKey])![2..], 16), (int)info[WorldSquadAuthoring.RowKey]!),
                    };
                }
                list.Add(row);
            }
            foreach (var (tag, list) in NewSquads)
            {
                list.Sort((a, b) => a.SlotIndex.CompareTo(b.SlotIndex));
                // A made owner holds only new slots; its rows are written whole, not inserted.
                if (MadeOwners.ContainsKey(tag)) continue;
                var owner = files.Single(file => file.SourceTag == tag);
                int first = (int)(TagRead.U32(owner.Data, TagView.FieldOffset<SAuthoredObject>(nameof(SAuthoredObject.SlotTable))) & 0xFFFF);
                int slots = (int)TagView.At<SAuthoredObject>(owner.Data, 0).SlotTable.Slots.Count;
                int at = list[0].SlotIndex, rules = list.Count(squad => squad.RuleForm);
                // Squad slots end the first slot group, or the table when nothing was renumbered; rule slots end the table.
                var ruleSlots = list.Where(squad => squad.RuleForm).Select(squad => (int)squad.RuleIndex).Order().ToList();
                if (list.Select((squad, row) => squad.SlotIndex == at + row).Any(x => !x) || (at + list.Count != first && at + list.Count + rules != slots)
                    || !ruleSlots.SequenceEqual(Enumerable.Range(slots - rules, rules)))
                    throw new InvalidDataException($"{owner.Name} new squad slots do not end its first slot group, or its new rule slots do not end its table.");
                InsertedAt[tag] = at;
            }
        }
    }

    /** The row a tag set list gives the object file; the occurrence's object ordinal. */
    private static int TagSetOrdinal(IReadOnlyList<TftFile> files, string owner)
    {
        foreach (var set in files.Where(file => file.ClassId == SScenarioTagSet.Class && file.References.Any(x => x.Target == owner)))
            foreach (var (_, value) in (JsonObject)TftText.ToJson(set, out _)["fields"]!)
                if (value is JsonArray rows)
                    for (int row = 0; row < rows.Count; row++)
                        if (rows[row] is JsonObject item && WorldFiles.Text(item["tag"]) == owner) return row;
        throw new InvalidDataException($"No scenario tag set lists {owner}.");
    }

    /**
     * Adds an object row, its slots and its occurrences for each squad owner the world made. The
     * occurrences copy its template's, at the owner's own place in the tag set list. A door owner's
     * follow the tag sets that list it.
     */
    private static void AddMadeOwners(ActivitySdkTables tables, Source source, Destination world, uint scenarioRow, Dictionary<uint, uint> objectRows)
    {
        foreach (var (tag, made) in world.MadeOwners)
        {
            int template = Array.FindIndex(source.Objects, row => row.ObjectTag == made.Template);
            if (template < 0 || !objectRows.TryGetValue((uint)template, out uint templateRow))
                throw new InvalidDataException($"Squad owner 0x{tag:X8} copies 0x{made.Template:X8}, which no kept state names.");
            uint row = (uint)tables.Objects.Count, firstSlot = (uint)tables.Slots.Count;
            var squads = world.NewSquads.GetValueOrDefault(tag) ?? [];
            if (world.SlotsWritten.Add(tag)) WriteSlots(tables, world, tag, row, squads, rules: false);
            uint chains = (uint)squads.Count, blocks = 1;
            if (world.MadeCombat.TryGetValue(tag, out var combat))
            {
                WriteCombatSlots(tables, source, world, tag, row, template, combat);
                // A combat area is two chains, a door one per slot; each kind adds a block of its own.
                bool area = combat.Any(slot => slot.TemplateObject == 0);
                int doors = combat.Count(slot => slot.TemplateObject != 0);
                chains += (area ? 2u : 0u) + (uint)doors;
                blocks += (area ? 1u : 0u) + (doors > 0 ? 1u : 0u);
            }
            tables.Objects.Add(new SdkObject
            {
                Id = tables.Text($"object/{tag:x8}"), ObjectTag = tag, ObjectKey = made.Key,
                Slots = new() { First = firstSlot, Count = (uint)tables.Slots.Count - firstSlot },
                // Each chain is one handle, one set, one link and one config, as the copied chain is.
                ConfigCount = chains, DescriptorCount = chains, PlacedSubblockCount = blocks, PlacedLeafCount = chains, PlacedHopCount = 3 * chains,
            });
            var copied = tables.Occurrences.Where(item => item.ObjectIndex == templateRow && item.ScenarioIndex == scenarioRow).ToList();
            if (world.DoorStates.TryGetValue(tag, out var placed))
            {
                AddDoorOccurrences(tables, scenarioRow, tag, row, copied, placed);
                continue;
            }
            foreach (var occurrence in copied)
                tables.Occurrences.Add(occurrence with
                {
                    Id = tables.Text($"object-occurrence/squad-owner/{tag:x8}/{occurrence.StateIndex:x4}/{made.Ordinal:x6}"),
                    ObjectIndex = row, ObjectOrdinal = (uint)made.Ordinal,
                });
            // The server reaches a slot only through an occurrence in the held state, so the owner
            // also occurs in every state its template does not; the state's own rows give the context.
            if (copied.Count == 0) continue;
            var held = copied.Select(item => item.StateIndex).ToHashSet();
            foreach (var context in tables.Occurrences.Where(item => item.ScenarioIndex == scenarioRow && !held.Contains(item.StateIndex))
                .GroupBy(item => item.StateIndex).Select(group => group.First()).ToList())
                tables.Occurrences.Add(copied[0] with
                {
                    Id = tables.Text($"object-occurrence/squad-owner/{tag:x8}/{context.StateIndex:x4}/{made.Ordinal:x6}"),
                    ContextRegistryKey = context.ContextRegistryKey, RegistryId = context.RegistryId, EntryId = context.EntryId,
                    BubbleIndex = context.BubbleIndex, StateIndex = context.StateIndex,
                    ObjectIndex = row, ObjectOrdinal = (uint)made.Ordinal,
                });
        }
    }

    /**
     * Gives a door owner one occurrence in each state whose tag set lists it, as the packages place it.
     * The template's occurrence gives the list field; the state's own rows give the context.
     */
    private static void AddDoorOccurrences(ActivitySdkTables tables, uint scenarioRow, uint tag, uint row, List<SdkOccurrence> copied,
        IReadOnlyList<(int Bubble, int State, int Ordinal)> placed)
    {
        if (copied.Count == 0) throw new InvalidDataException($"Door owner 0x{tag:X8}: its template occurs in no kept state.");
        var scenarioStates = Enumerable.Range(0, tables.States.Count).Where(at => tables.States[at].ScenarioIndex == scenarioRow).ToList();
        foreach (var (bubble, state, ordinal) in placed)
        {
            int stateRow = scenarioStates.FirstOrDefault(at => tables.Bubbles[(int)tables.States[at].BubbleIndex].BubbleOrdinal == (uint)bubble
                && tables.States[at].StateOrdinal == (uint)state, -1);
            if (stateRow < 0) continue;
            var context = copied.FirstOrDefault(item => item.StateIndex == (uint)stateRow) is { Id.Length: > 0 } own ? own
                : tables.Occurrences.FirstOrDefault(item => item.StateIndex == (uint)stateRow, copied[0]);
            tables.Occurrences.Add(copied[0] with
            {
                Id = tables.Text($"object-occurrence/squad-owner/{tag:x8}/{stateRow:x4}/{ordinal:x6}"),
                ContextRegistryKey = context.ContextRegistryKey, RegistryId = context.RegistryId, EntryId = context.EntryId,
                BubbleIndex = tables.States[stateRow].BubbleIndex, StateIndex = (uint)stateRow,
                ObjectIndex = row, ObjectOrdinal = (uint)ordinal,
            });
        }
    }

    /**
     * Writes a made owner's combat slots, each a copy of its template object's slot under its own
     * name, and keeps the objective's task group rows for after the copied ones.
     */
    private static void WriteCombatSlots(ActivitySdkTables tables, Source source, Destination world, uint objectTag, uint objectRow, int ownerTemplate,
        IReadOnlyList<CombatSlot> combat)
    {
        var (groups, groupWords) = source.Words(SdkSection.CombatObjectiveGroups);
        foreach (var item in combat)
        {
            int at0 = item.TemplateObject == 0 ? ownerTemplate : Array.FindIndex(source.Objects, row => row.ObjectTag == item.TemplateObject);
            if (at0 < 0) throw new InvalidDataException($"No kept installed object 0x{item.TemplateObject:X8} for {item.Name}.");
            var template = source.Objects[at0];
            uint from = Enumerable.Range((int)template.Slots.First, (int)template.Slots.Count).Select(at => (uint)at)
                .Where(at => source.Slots[at].SlotIndex == item.TemplateIndex && source.Slots[at].SlotType == item.SlotType)
                .DefaultIfEmpty(uint.MaxValue).First();
            if (from == uint.MaxValue)
                throw new InvalidDataException($"Template 0x{template.ObjectTag:X8} has no type-{item.SlotType} slot {item.TemplateIndex} for {item.Name}.");
            var slot = source.Slots[from];
            uint row = (uint)tables.Slots.Count;
            slot.Id = tables.Text($"slot/{objectTag:x8}/{item.Index:x6}/{item.Index:x4}/{item.SlotType:x4}");
            slot.Name = tables.Text(item.Name);
            slot.SenseSchemaId = tables.Text(source.Pack.Text(slot.SenseSchemaId));
            slot.AuthSchemaId = tables.Text(source.Pack.Text(slot.AuthSchemaId));
            slot.ObjectIndex = objectRow;
            slot.SlotIndex = item.Index;
            slot.Aliases = AliasRow(tables, item.Name);
            slot.Capabilities = default;
            tables.Slots.Add(slot);
            for (int at = 0; at < groups.Length; at += groupWords)
                if (groups[at] == from)
                {
                    var copy = groups.AsSpan(at, groupWords).ToArray();
                    copy[0] = row;
                    world.MadeGroupRows.Add(copy);
                }
        }
    }

    /**
     * Writes the owner's new squad slots once, before the first installed slot at or past their index.
     * @return The installed slot's index in the world: moved up past the new slots when it follows them.
     */
    private static uint NewSlotsBefore(ActivitySdkTables tables, Destination world, uint objectTag, uint objectRow, uint slotIndex)
    {
        if (!world.InsertedAt.TryGetValue(objectTag, out int at)) return slotIndex;
        var squads = world.NewSquads[objectTag];
        if (slotIndex < at) return slotIndex;
        if (world.SlotsWritten.Add(objectTag)) WriteSlots(tables, world, objectTag, objectRow, squads, rules: false);
        // The loop end writes the rule slots, which follow every installed slot.
        if (slotIndex == uint.MaxValue) WriteSlots(tables, world, objectTag, objectRow, squads, rules: true);
        return slotIndex == uint.MaxValue ? slotIndex : slotIndex + (uint)squads.Count;
    }

    /** Writes the squad slot rows of an owner's new squads, or with `rules` their rule slot rows, in slot order. */
    private static void WriteSlots(ActivitySdkTables tables, Destination world, uint objectTag, uint objectRow, List<NewSquadRow> squads, bool rules)
    {
        if (rules)
        {
            foreach (var squad in squads.Where(squad => squad.RuleForm).OrderBy(squad => squad.RuleIndex))
            {
                string rule = squad.Name + ".spawn_rule";
                tables.Slots.Add(new SdkSlot
                {
                    Id = tables.Text($"slot/{objectTag:x8}/{squad.RuleIndex:x6}/{squad.RuleIndex:x4}/0042"),
                    Name = tables.Text(rule),
                    ObjectIndex = objectRow, SlotIndex = squad.RuleIndex, SlotType = SquadConfig.RuleSlotType, ComponentClass = RuleComponent,
                    SenseSchema = ActivitySdkFormat.AbsentIndex, AuthSchema = ActivitySdkFormat.AbsentIndex, Flags = SlotJoinExact,
                    Aliases = AliasRow(tables, rule),
                });
            }
            return;
        }
        foreach (var squad in squads)
        {
            world.NewSlotRows[squad.ConfigFile] = (uint)tables.Slots.Count;
            tables.Slots.Add(new SdkSlot
            {
                Id = tables.Text($"slot/{objectTag:x8}/{squad.SlotIndex:x6}/{squad.SlotIndex:x4}/0001"),
                Name = tables.Text(squad.Name),
                SenseSchemaId = tables.Text($"schema/{SquadSense:x8}"),
                AuthSchemaId = tables.Text($"schema/{SquadAuth:x8}"),
                ObjectIndex = objectRow, SlotIndex = squad.SlotIndex, SlotType = 1, ComponentClass = SquadComponent,
                SenseSchema = SquadSense, AuthSchema = SquadAuth, Flags = SlotJoinExact,
                Aliases = AliasRow(tables, squad.Name),
            });
        }
    }

    /** The position of one row of a world-owned copy of an installed placement list. */
    private static Vector3 PointPosition(IReadOnlyList<TftFile> files, uint list, int row)
    {
        var copy = files.Single(file => file.SourceTag == list && file.ClassId == SMapPlacements.Class);
        return TagView.Rows(copy.Data, in TagView.At<SMapPlacements>(copy.Data, 0).Placements, AuthoredPlacementEntry.Class)[row].Position;
    }

    /** Adds one alias text row and returns its range. */
    private static SdkRange AliasRow(ActivitySdkTables tables, string text)
    {
        var list = tables.Words(SdkSection.Texts);
        const int rowWords = 4;
        var value = tables.Text(text);
        uint first = (uint)(list.Count / rowWords);
        list.AddRange([value.Offset, value.Length, AliasKind, 0]);
        return new() { First = first, Count = 1 };
    }

    /**
     * Adds the squad, member and anchor rows of each squad the world added, after the copied squads of
     * the same scenario. The occurrence is the owner's first in the slot's bubble.
     */
    private static void AddNewSquads(ActivitySdkTables tables, Source source, Destination world, uint scenarioRow)
    {
        foreach (var squad in world.NewSquads.Values.SelectMany(list => list))
        {
            if (!world.NewSlotRows.TryGetValue(squad.ConfigFile, out uint slotRow))
                throw new InvalidDataException($"Squad {squad.Name}: its owner has no occurrence in a kept bubble.");
            uint objectRow = tables.Slots[(int)slotRow].ObjectIndex;
            var occurrences = Enumerable.Range(0, tables.Occurrences.Count)
                .Where(row => tables.Occurrences[row].ObjectIndex == objectRow && tables.Occurrences[row].ScenarioIndex == scenarioRow).ToList();
            int occurrence = occurrences.Where(row => squad.BubbleIndex == uint.MaxValue
                    || tables.Bubbles[(int)tables.Occurrences[row].BubbleIndex].BubbleOrdinal == squad.BubbleIndex)
                .DefaultIfEmpty(occurrences.DefaultIfEmpty(-1).First()).First();
            if (occurrence < 0) throw new InvalidDataException($"Squad {squad.Name}: its owner has no occurrence.");

            string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{world.Activity}/{squad.Name}")));
            uint squadRow = (uint)tables.Squads.Count;
            int actor = Array.FindIndex(source.ActorClasses, row => row.DefinitionTag == squad.Entity);
            uint lanes = (uint)squad.Count | (uint)squad.Count << 16;
            var member = new SdkSquadMember
            {
                Id = tables.Text($"squad-member/{digest}/000000"), SquadIndex = squadRow, MemberOrdinal = 0, MemberKey = squad.MemberKey,
                ActorClassIndex = ActivitySdkFormat.AbsentIndex, Flags = MemberCountsReady,
                CandidateCounts0 = lanes, CandidateCounts1 = lanes, CandidateCounts2 = lanes, DefaultCount = squad.Count,
            };
            if (actor >= 0)
            {
                member.ActorClassIndex = ActorClassRow(tables, source, (uint)actor);
                member.Flags |= MemberActorExact;
                uint profile = source.ActorClasses[actor].AuthoredSpawnProfile;
                if (ValidProfile(profile)) { member.Flags |= MemberProfileExact; member.AuthoredSpawnProfile = profile; }
            }
            tables.SquadMembers.Add(member);
            uint firstAnchor = (uint)tables.SquadAnchors.Count;
            // A rule point sits in a list the occurrence owns; an inline point is the config's own placement.
            if (squad.Identity != 0) tables.SquadAnchors.Add(new SdkSquadAnchor
            {
                Id = tables.Text($"squad-anchor/{digest}/000000"), SquadIndex = squadRow, PointOrdinal = 0,
                ObjectListTag = squad.ListTag != 0 ? world.Tag(squad.ListTag) : 0, PlacementOrdinal = squad.ListTag != 0 ? (uint)squad.PointRow : 0,
                Flags = squad.ListTag != 0 ? ListAnchorFlags : InlineAnchorFlags, PlacedEntryIdentity = squad.Identity,
                PositionX = BitConverter.SingleToUInt32Bits(squad.Position.X), PositionY = BitConverter.SingleToUInt32Bits(squad.Position.Y),
                PositionZ = BitConverter.SingleToUInt32Bits(squad.Position.Z),
            });
            tables.Squads.Add(new SdkSquad
            {
                Id = tables.Text($"squad/{digest}"), ScenarioIndex = scenarioRow, ObjectIndex = objectRow, SlotIndex = slotRow,
                SpawnerConfigTag = squad.ConfigTag, SpawnRuleConfigTag = squad.RuleTag != 0 ? squad.RuleTag : squad.Identity != 0 ? squad.ConfigTag : ActivitySdkFormat.AbsentIndex,
                Flags = squad.Identity != 0 ? NewSquadFlags : UnboundSquadFlags, OccurrenceIndex = (uint)occurrence,
                Members = new() { First = (uint)tables.SquadMembers.Count - 1, Count = 1 },
                Anchors = new() { First = firstAnchor, Count = (uint)tables.SquadAnchors.Count - firstAnchor },
            });
        }
    }

    /** A spawn profile is four signed bytes, each from 0 to 2, 6, 2 and 6. */
    private static bool ValidProfile(uint profile)
    {
        ReadOnlySpan<int> top = [2, 6, 2, 6];
        for (int lane = 0; lane < top.Length; lane++)
            if ((sbyte)(profile >> (lane * 8)) is var value && (value < 0 || value > top[lane])) return false;
        return true;
    }
}
