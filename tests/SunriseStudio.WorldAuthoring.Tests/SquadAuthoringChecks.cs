using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Tft;
using SunriseStudio.Tags.Views;
using SunriseStudio.World;
using SunriseStudio.World.Sdk;

/**
 * New squads on the EDZ testbed: a bulk list adds three squads to one copied object, the project
 * builds, and the built tags and the mod's SDK pack give every squad back under its name.
 */
static class SquadAuthoringChecks
{
    // Trostland, Widow's Walk and the Atrium in EDZ.
    private static readonly uint[] Kept = [0xB8459D59, 0xC0064AEF, 0xC0064AED];
    private const string Destination = "squadbed";
    private static readonly int SlotIndexField = TagView.FieldOffset<SlotComponentHeader>(nameof(SlotComponentHeader.SlotIndex));

    /** @param scratchRoot Folder for the scratch projects; null takes the temp folder. */
    public static void Run(string gameRoot, string? scratchRoot = null)
    {
        scratchRoot ??= Path.GetTempPath();
        using var catalog = PackageCatalog.Open(gameRoot);
        var schema = StudioRegistry.Index;
        uint scenario = WorldBuild.Named(catalog, "edz_freeroam:scenario_client");
        string installedPack = Path.Combine(gameRoot, "bin", "x64", "Sunrise", ModActivitySdk.FileName);
        string scratch = Path.Combine(scratchRoot, "sunrise-squads-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = SunriseStudio.Projects.StudioProject.Create(Path.Combine(scratch, "project"), "Squadbed");
            WorldTemplate.Create(project.ContentDirectory, catalog, schema, scenario, Kept.ToHashSet(), Destination);
            var world = WorldFiles.Load(project.ContentDirectory, Destination);
            var templates = WorldSquadAuthoring.InlineTemplates(installedPack, catalog, scenario);
            WorldBuild.Check(templates.Count > 0, "The EDZ scenario has no inline squad spawner to copy.");
            uint entity = WorldSquads.List(world, catalog).First(squad => squad.Entities.Count == 1).Entities[0];
            var origin = new Vector3(700, 160, 85);
            var list = WorldSquadAuthoring.ParseList($$"""
                [
                  {"name": "squadbed.wild_a", "entity": "0x{{entity:X8}}", "count": 1, "position": [{{origin.X}}, {{origin.Y}}, {{origin.Z}}]},
                  {"name": "squadbed.horde", "entity": "0x{{entity:X8}}", "count": 5, "position": [{{origin.X + 10}}, {{origin.Y}}, {{origin.Z}}]},
                  {"name": "squadbed.guard_1", "entity": "0x{{entity:X8}}", "count": 2, "position": [{{origin.X}}, {{origin.Y + 10}}, {{origin.Z}}]}
                ]
                """);
            WorldBuild.Check(list.Count == 3 && list[1].Count == 5 && list[0].Entity == TftNames.Installed(entity), "The squad list did not parse.");

            var before = Held(world);
            var added = WorldSquadAuthoring.AddMany(world, schema, catalog, Destination, templates[0], list);
            string ownerFile = added[0].OwnerFile;
            var (first0, rest0, _) = Groups(world, ownerFile, before);
            var (first1, rest1, rows) = Groups(world, ownerFile);
            WorldBuild.Check(added.All(squad => squad.OwnerFile == ownerFile) && first1 == first0 + 3 && rest1 == rest0
                && added.Select(squad => (int)squad.SlotIndex).SequenceEqual([first0, first0 + 1, first0 + 2])
                && added.All(squad => rows[squad.SlotIndex].SlotType == 1 && rows[squad.SlotIndex].NameHash == StringBankWriter.Hash(squad.Name)),
                "The new squad slots are not the end of the owner's first slot group.");
            var listed = WorldSquadAuthoring.List(world);
            WorldBuild.Check(listed.Count == 3 && listed.Zip(list).All(pair => pair.First.Name == pair.Second.Name && pair.First.Count == pair.Second.Count
                    && pair.First.Position == pair.Second.Position && pair.First.Entity == pair.Second.Entity
                    && pair.First.Identity == WorldSquadAuthoring.Identity(Destination, pair.First.Name)),
                "The new squads do not read back from their configs.");
            int moved = Renumbered(world, catalog, ownerFile, first0);
            Refusals(world, schema, catalog, templates[0], entity);
            int local = LocalOnly(world, catalog, added);
            Console.WriteLine($"world_squad_new owner={ownerFile.Split('/')[^1]} template={templates[0]:X8} templates={templates.Count} "
                + $"slots={first0}..{first0 + 2} first_group={first0}->{first1} moved_configs={moved} local_leaves={local} refusals=4");

            world.SaveChanged(project.ContentDirectory);
            var build = project.Build(catalog, tagSchema: schema);
            Built(catalog, schema, build, world, added, entity, first0);
            Pack(build, world, added, entity, first0);
            Variant(catalog, schema, scenario, scratch, templates[0], entity);
            OwnTemplate(catalog, schema, scenario, scratch, entity);
            MadeOwner(catalog, schema, scenario, scratch, templates[0], entity);
            Doors(catalog, schema, scenario, scratch);
            DoorPlacement(catalog, schema, scenario, scratch);
            RuleSquad(catalog, schema, scenario, scratch, entity);
            RuleBisect(catalog, schema, scenario, scratch, entity);
        }
        finally
        {
            catalog.SetOverlayEntries([]);
            catalog.ReadOverride = null;
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
        }
    }

    /** The new configs name no installed tag from a package that no other world file names. @return The leaf copies made. */
    private static int LocalOnly(WorldFiles world, PackageCatalog catalog, IReadOnlyList<AddedSquad> added)
    {
        var news = added.Select(squad => squad.ConfigFile).ToHashSet();
        var packages = new HashSet<ushort>();
        foreach (var file in world.Files.Where(file => !news.Contains(file.Name)))
        {
            if (file.SourceTag is uint copied && catalog.TryGetEntry(copied, out var own)) packages.Add(own.PackageId);
            foreach (var reference in file.References.Where(x => x.Target.StartsWith('#') && !x.Target.StartsWith("##")))
                if (catalog.TryGetEntry((uint)TftNames.InstalledHash(reference.Target), out var entry)) packages.Add(entry.PackageId);
        }
        // The chosen entity is the caller's; only the template's own references must stay local.
        foreach (var squad in added)
            foreach (var reference in world[squad.ConfigFile].References.Where(x => x.Target.StartsWith('#') && !x.Target.StartsWith("##") && x.Target != squad.Entity))
                WorldBuild.Check(catalog.TryGetEntry((uint)TftNames.InstalledHash(reference.Target), out var entry) && packages.Contains(entry.PackageId),
                    $"{squad.ConfigFile} names {reference.Target} from a package no other world file names.");
        return world.Files.Count(file => file.Name.EndsWith("_local." + file.TagType, StringComparison.Ordinal));
    }

    /** With every option off, the slot ends the table, nothing moves, the count is the template's, and the pack still joins. */
    private static void Variant(PackageCatalog catalog, RegistryIndex schema, uint scenario, string scratch, uint template, uint entity)
    {
        var project = SunriseStudio.Projects.StudioProject.Create(Path.Combine(scratch, "variant"), "Squadbed");
        WorldTemplate.Create(project.ContentDirectory, catalog, schema, scenario, Kept.ToHashSet(), Destination);
        var world = WorldFiles.Load(project.ContentDirectory, Destination);
        var before = Held(world);
        var options = new SquadAddOptions(Renumber: false, GrowCandidates: false, LocalLeaves: false, ClearGroupHash: true, AreaHash: 0xB8459D59);
        var squad = WorldSquadAuthoring.Add(world, schema, catalog, Destination, template,
            new("squadbed.end_slot", TftNames.Installed(entity), 4, new(700, 160, 85)), options);
        var (first0, rest0, rows0) = Groups(world, squad.OwnerFile, before);
        var (first1, rest1, rows1) = Groups(world, squad.OwnerFile);
        WorldBuild.Check(squad.SlotIndex == rows0.Length && rows1.Length == rows0.Length + 1 && first1 == first0 && rest1 == rest0 + 1 && squad.Count == 1
            && !world.Files.Any(file => file.ClassId == SEntityConfig.Class && file.SourceTag is not null),
            "The end-of-table variant moved a slot or changed the count.");
        byte[] made = world[squad.ConfigFile].Data;
        int peerAt = new TagPointer(made, TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition))).Target!.Value;
        WorldBuild.Check(TagRead.U32(made, peerAt + TagView.FieldOffset<SSquadComponentPeer>(nameof(SSquadComponentPeer.Unk40))) == 0x811C9DC5,
            "ClearGroupHash left the template's group hash.");
        int area = peerAt + TagView.FieldOffset<SSquadComponentPeer>(nameof(SSquadComponentPeer.H81eeb835)) + TagView.FieldOffset<C80807eba>(nameof(C80807eba.Unk1c));
        WorldBuild.Check(TagRead.U32(made, area) == 0xB8459D59, "AreaHash left the template's area hash.");
        world.SaveChanged(project.ContentDirectory);
        var build = project.Build(catalog, tagSchema: schema);
        Pack(build, world, [squad], entity, first0);
        Console.WriteLine($"world_squad_new_variant group_hash=811C9DC5 slot={squad.SlotIndex} groups={first0},{rest0}->{first1},{rest1} count={squad.Count}");
    }

    /**
     * The owner's own unbound boss squad config (0x80BE8CAA) as the template: the new squad has no point,
     * names no tag outside the owner's package set, and the pack marks it as needing a spawn rule.
     */
    private static void OwnTemplate(PackageCatalog catalog, RegistryIndex schema, uint scenario, string scratch, uint entity)
    {
        const uint boss = 0x80BE8CAA;
        var project = SunriseStudio.Projects.StudioProject.Create(Path.Combine(scratch, "own"), "Squadbed");
        WorldTemplate.Create(project.ContentDirectory, catalog, schema, scenario, Kept.ToHashSet(), Destination);
        var world = WorldFiles.Load(project.ContentDirectory, Destination);
        var squad = WorldSquadAuthoring.Add(world, schema, catalog, Destination, boss,
            new("squadbed.own_template", TftNames.Installed(entity), 1, new(700, 160, 85)), new SquadAddOptions(Renumber: false, GrowCandidates: false));
        int local = LocalOnly(world, catalog, [squad]);
        WorldBuild.Check(squad.Identity == 0 && squad.Count == 1 && local == 0, "The boss template gave a point, a count or a leaf copy.");
        world.SaveChanged(project.ContentDirectory);
        var build = project.Build(catalog, tagSchema: schema);
        Pack(build, world, [squad], entity, 0);
        Console.WriteLine($"world_squad_new_own_template template={boss:X8} slot={squad.SlotIndex} owner={squad.OwnerFile.Split('/')[^1]} leaves={local}");
    }

    /**
     * A squad owner the world makes: an empty copy of a patrol-kind object, listed by the same tag set,
     * holding only the new squads; no stock slot table changes, and the built owner reaches each squad.
     */
    private static void MadeOwner(PackageCatalog catalog, RegistryIndex schema, uint scenario, string scratch, uint template, uint entity)
    {
        var project = SunriseStudio.Projects.StudioProject.Create(Path.Combine(scratch, "made"), "Squadbed");
        WorldTemplate.Create(project.ContentDirectory, catalog, schema, scenario, Kept.ToHashSet(), Destination);
        var world = WorldFiles.Load(project.ContentDirectory, Destination);
        var before = Held(world);
        string owner = WorldSquadAuthoring.AddOwner(world, schema, catalog, Destination, "squadbed.squads");
        var added = WorldSquadAuthoring.AddMany(world, schema, catalog, Destination, template,
            [new("squadbed.made_a", TftNames.Installed(entity), 1, new(604, 146, 89.9f)), new("squadbed.made_b", TftNames.Installed(entity), 3, new(610, 148, 89.9f))],
            owner);
        var (first, second, rows) = Groups(world, owner);
        var tagSet = world.Files.Single(file => file.ClassId == SScenarioTagSet.Class && file.References.Any(x => x.Target == owner));
        WorldBuild.Check(first == 2 && second == 0 && rows.Length == 2 && rows.All(row => row.SlotType == 1)
            && added.Select(squad => (int)squad.SlotIndex).SequenceEqual([0, 1]) && world[owner].SourceTag is null
            && before.All(pair => world[pair.Key].Data.AsSpan().SequenceEqual(pair.Value)),
            "The made owner does not hold exactly the new squads, or a stock object changed.");
        var area = CombatArea(world, schema, catalog, owner, template, entity);
        (first, second, rows) = Groups(world, owner);
        world.SaveChanged(project.ContentDirectory);
        var build = project.Build(catalog, tagSchema: schema);
        var tags = Compiled(build).ToDictionary(tag => tag.Name, StringComparer.Ordinal);
        catalog.SetOverlayEntries(tags.Values.Select(tag =>
        {
            var (package, index) = TagHashes.Split(tag.TagHash);
            return new PackageOverlayEntry(new PackageEntry(tag.TagHash, tag.ClassId, (int)((tag.TypeInfo >> 9) & 127), (int)((tag.TypeInfo >> 6) & 7),
                tag.Data.Length, $"w64_sunrise_{package:x4}_0", package, index) { TypeInfo = tag.TypeInfo }, tag.Name);
        }));
        var bytes = tags.Values.ToDictionary(tag => tag.TagHash, tag => tag.Data);
        catalog.ReadOverride = tag => bytes.GetValueOrDefault(tag);
        var slots = ComponentSlotCatalog.OwnerSlots(catalog, tags[owner].TagHash);
        catalog.SetOverlayEntries([]);
        catalog.ReadOverride = null;
        WorldBuild.Check(added.All(squad => slots.Count(row => row.SlotIndex == squad.SlotIndex && row.SlotType == 1
                && row.ConfigTag == tags[squad.ConfigFile].TagHash) == 1)
            && slots.Count(row => row.SlotIndex == area.Objective.Index && row.SlotType == 3 && row.ConfigTag == tags[area.ObjectiveConfig].TagHash
                && row.SenseSchema == ObjectiveSense && row.AuthSchema == ObjectiveAuth) == 1
            && slots.All(row => row.SlotType != 1 || added.Any(squad => squad.SlotIndex == row.SlotIndex)),
            "The built made owner does not reach its two squads and its objective.");
        Pack(build, world, added, entity, 0);
        using var pack = ActivitySdkPack.Open(build.ActivitySdkPack!);
        var objects = pack.Rows<SdkObject>(SdkSection.Objects);
        var occurrences = pack.Rows<SdkOccurrence>(SdkSection.Occurrences);
        int row = Array.FindIndex(objects, item => item.ObjectTag == tags[owner].TagHash);
        int occurs = occurrences.Count(item => item.ObjectIndex == row);
        WorldBuild.Check(row >= 0 && objects[row].ObjectKey == TagView.At<SAuthoredObject>(world[owner].Data, 0).RegistryKey && objects[row].ReplicatedPlacementCount == 0
            && objects[row].PlacedLeafCount == 4 && objects[row].PlacedSubblockCount == 2 && occurs > 0, "The pack has no object row or occurrence for the made owner.");
        // The objective's slot row and its task groups, as assign_combat_objective resolves them.
        var packSlots = pack.Rows<SdkSlot>(SdkSection.Slots);
        int objectiveRow = Array.FindIndex(packSlots, slot => slot.ObjectIndex == row && pack.Text(slot.Name) == area.Objective.Name);
        var (groups, groupWords) = pack.Words(SdkSection.CombatObjectiveGroups);
        var mine = Enumerable.Range(0, groups.Length / groupWords).Where(at => groups[at * groupWords] == objectiveRow).Select(at => groups[at * groupWords + 1]).ToList();
        WorldBuild.Check(objectiveRow >= 0 && packSlots[objectiveRow].SlotType == 3 && packSlots[objectiveRow].ComponentClass == 0x80808348
            && packSlots[objectiveRow].AuthSchema == ObjectiveAuth && (packSlots[objectiveRow].Flags & 2) != 0 && packSlots[objectiveRow].SlotIndex == area.Objective.Index
            && mine.Count == area.Groups && mine.Order().SequenceEqual(Enumerable.Range(0, area.Groups).Select(at => (uint)at))
            && area.Slots.All(slot => packSlots.Count(item => item.ObjectIndex == row && item.SlotIndex == slot.Index && item.SlotType == slot.SlotType
                && pack.Text(item.Name) == slot.Name) == 1),
            "The pack's objective slot, its task groups or its firing area slots are wrong.");
        Console.WriteLine($"world_squad_new_made_owner owner={owner.Split('/')[^1]} key={objects[row].ObjectKey:X8} tag_set={tagSet.Name.Split('/')[^1]} slots={rows.Length} occurrences={occurs} evidence={slots.Length} objective_row={objectiveRow} groups={mine.Count}");
    }

    /**
     * Three doors on an empty made owner: triggers fill its first slot group and volumes its second,
     * every copied slot ref names the owner, each volume is its box, the built owner reaches each
     * trigger, and the pack names every slot door.<name>. A taken name and a full owner are refused.
     */
    private static void Doors(PackageCatalog catalog, RegistryIndex schema, uint scenario, string scratch)
    {
        var project = SunriseStudio.Projects.StudioProject.Create(Path.Combine(scratch, "doors"), "Squadbed");
        WorldTemplate.Create(project.ContentDirectory, catalog, schema, scenario, Kept.ToHashSet(), Destination);
        var world = WorldFiles.Load(project.ContentDirectory, Destination);
        string owner = WorldSquadAuthoring.AddOwner(world, schema, catalog, Destination, "squadbed.doors");
        NewDoor[] wanted = [new("lane:0", new(600, 140, 80), new(2, 1, 3)), new("lane:1", new(610, 140, 80), new(4, 2, 3), MathF.PI / 2),
            new("house_1", new(620, 150, 81), new(1.5f, 1.5f, 2.5f), 0.3f)];
        var doors = WorldSquadAuthoring.AddDoors(world, schema, catalog, Destination, owner, wanted);
        var (first, second, rows) = Groups(world, owner);
        uint key = TagView.At<SAuthoredObject>(world[owner].Data, 0).RegistryKey;
        uint oldKey = TagView.At<SAuthoredObject>(catalog.Read(0x80B9A4B9), 0).RegistryKey;
        WorldBuild.Check(first == 3 && second == 3 && rows.Select(row => row.SlotType).SequenceEqual([31u, 31u, 31u, 60u, 60u, 60u])
            && doors.Select((door, at) => door.Trigger.Index == at && door.Volume.Index == 3 + at && door.Trigger.Name == $"door.{wanted[at].Name}"
                && rows[door.Trigger.Index].NameHash == StringBankWriter.Hash(door.Trigger.Name)
                && rows[door.Volume.Index].NameHash == StringBankWriter.Hash(door.Volume.Name)).All(x => x)
            && WorldSquadAuthoring.DoorSlots(world[owner])!.SequenceEqual(doors.Select(door => door.Trigger).Concat(doors.Select(door => door.Volume))),
            "The door slots do not fill the owner's two groups in order.");
        var identities = new HashSet<ulong>();
        foreach (var (door, at) in doors.Select((door, at) => (door, at)))
        {
            foreach (string file in new[] { door.TriggerConfig, door.VolumeConfig })
            {
                byte[] data = world[file].Data;
                for (int word = 0; word + 8 <= data.Length; word += 4)
                {
                    uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(word));
                    ushort type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(word + 4)), index = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(word + 6));
                    WorldBuild.Check(value != oldKey || type is not (31 or 60), $"{file} still names the template's slot {index}.");
                    if (value == key) WorldBuild.Check(type == 31 && index == door.Trigger.Index || type == 60 && index == door.Volume.Index,
                        $"{file} names slot {index} of type {type}.");
                }
            }
            byte[] volume = world[door.VolumeConfig].Data;
            int field = -1;
            for (int at2 = 0; at2 + 16 <= volume.Length && field < 0; at2 += 8)
                if (TagArray.TryChecked(volume, at2, 0, 0, out var array) && array.Count > 0 && array.Class == STriggerVolumeRow.Class) field = at2;
            var (count, rowAt, _) = TagArray.Checked(volume, field);
            var row = TagView.At<STriggerVolumeRow>(volume, rowAt);
            var corners = WorldSquadAuthoring.DoorCorners(wanted[at]);
            WorldBuild.Check(count == 1 && row.SlotType == 60 && row.SlotOrdinal == door.Volume.Index && row.Key == key
                && Vector3.Distance(row.Sector.BoundsMin, new(corners.Min(c => c.X), corners.Min(c => c.Y), wanted[at].Floor.Z)) < 1e-3f
                && MathF.Abs(row.Sector.ExtrusionHeight - wanted[at].Size.Z) < 1e-6f && row.SpawnEntry.Position == Vector3.Zero
                && identities.Add(row.SpawnEntry.PlacementIdentity), $"Door {door.Name}'s volume is not its box, or its identity repeats.");
        }
        bool taken = false, full = false;
        string second2 = WorldSquadAuthoring.AddOwner(world, schema, catalog, Destination, "squadbed.doors_2");
        try { WorldSquadAuthoring.AddDoors(world, schema, catalog, Destination, second2, [wanted[0]]); }
        catch (InvalidDataException) { taken = true; }
        try { WorldSquadAuthoring.AddDoors(world, schema, catalog, Destination, owner, [new("late", new(0, 0, 0), new(1, 1, 1))]); }
        catch (InvalidDataException) { full = true; }
        WorldBuild.Check(taken && full, "A taken door name or a second door call on one owner was accepted.");
        world.Remove(second2);
        world.Edit(world.Files.Single(file => file.ClassId == SScenarioTagSet.Class && file.References.Any(x => x.Target == second2)).Name, (_, fields) =>
        {
            foreach (var (_, value) in fields.ToList())
                if (value is System.Text.Json.Nodes.JsonArray list)
                    foreach (var item in list.OfType<System.Text.Json.Nodes.JsonObject>().Where(item => WorldFiles.Text(item["tag"]) == second2).ToList()) list.Remove(item);
        });
        world.SaveChanged(project.ContentDirectory);
        var build = project.Build(catalog, tagSchema: schema);
        var tags = Compiled(build).ToDictionary(tag => tag.Name, StringComparer.Ordinal);
        catalog.SetOverlayEntries(tags.Values.Select(tag =>
        {
            var (package, index) = TagHashes.Split(tag.TagHash);
            return new PackageOverlayEntry(new PackageEntry(tag.TagHash, tag.ClassId, (int)((tag.TypeInfo >> 9) & 127), (int)((tag.TypeInfo >> 6) & 7),
                tag.Data.Length, $"w64_sunrise_{package:x4}_0", package, index) { TypeInfo = tag.TypeInfo }, tag.Name);
        }));
        var bytes = tags.Values.ToDictionary(tag => tag.TagHash, tag => tag.Data);
        catalog.ReadOverride = tag => bytes.GetValueOrDefault(tag);
        var slots = ComponentSlotCatalog.OwnerSlots(catalog, tags[owner].TagHash);
        catalog.SetOverlayEntries([]);
        catalog.ReadOverride = null;
        WorldBuild.Check(doors.All(door => slots.Count(slot => slot.SlotType == 31 && slot.SlotIndex == door.Trigger.Index
            && slot.ConfigTag == tags[door.TriggerConfig].TagHash) == 1), "The built owner does not reach every door trigger.");
        using var pack = ActivitySdkPack.Open(build.ActivitySdkPack!);
        var objects = pack.Rows<SdkObject>(SdkSection.Objects);
        var packSlots = pack.Rows<SdkSlot>(SdkSection.Slots);
        int objectRow = Array.FindIndex(objects, item => item.ObjectTag == tags[owner].TagHash);
        var ownerSlots = Enumerable.Range((int)objects[objectRow].Slots.First, (int)objects[objectRow].Slots.Count).Select(at => packSlots[at]).ToArray();
        WorldBuild.Check(objectRow >= 0 && ownerSlots.Length == 6 && ownerSlots.Select((slot, at) => slot.SlotIndex == at && slot.SlotType == rows[at].SlotType).All(x => x)
            && doors.All(door => packSlots.Count(slot => pack.Text(slot.Name) == door.Trigger.Name) == 1 && packSlots.Count(slot => pack.Text(slot.Name) == door.Volume.Name) == 1)
            && ownerSlots.Take(3).All(slot => slot.SlotType == 31 && (slot.Flags & 2) != 0),
            "The pack's door slot rows are wrong.");
        Console.WriteLine($"world_doors owner={owner.Split('/')[^1]} doors={doors.Count} groups={first},{second} evidence={slots.Length} "
            + $"trigger_class={ownerSlots[0].ComponentClass:X8} trigger_auth={ownerSlots[0].AuthSchema:X8} volume_class={ownerSlots[3].ComponentClass:X8} taken=refused second_call=refused");
    }
    /**
     * Doors in a stock bubble and in a copy of it: the built world gives the server exactly one source
     * for each door's trigger, and the pack one occurrence in each of the bubble's states. An owner no
     * bubble claims stays listed by both bubbles and resolves ambiguous.
     */
    private static void DoorPlacement(PackageCatalog catalog, RegistryIndex schema, uint scenario, string scratch)
    {
        var project = SunriseStudio.Projects.StudioProject.Create(Path.Combine(scratch, "door-placement"), "Squadbed");
        WorldTemplate.Create(project.ContentDirectory, catalog, schema, scenario, Kept.ToHashSet(), Destination);
        var world = WorldFiles.Load(project.ContentDirectory, Destination);
        uint stock = WorldContent.FirstLiveBubble(world);
        var clone = WorldBubbles.AddBubble(world, stock, "squadbed_door_copy");
        int stockRow = WorldBubbles.ScenarioRow(world, stock);
        var cases = new (string Owner, int? Row)[] { ("squadbed.doors_shared", null), ("squadbed.doors_stock", stockRow), ("squadbed.doors_copy", clone.Row) };
        var owners = new List<(string File, int? Row, AddedDoor Door)>();
        foreach (var (name, row) in cases)
        {
            string owner = WorldSquadAuthoring.AddOwner(world, schema, catalog, Destination, name);
            var door = WorldSquadAuthoring.AddDoors(world, schema, catalog, Destination, owner, [new(name.Split('.')[1], new(600, 140, 80), new(2, 1, 3))], bubbleRow: row)[0];
            owners.Add((owner, row, door));
        }
        var listed = owners.ToDictionary(owner => owner.File, owner => WorldSquadAuthoring.ListedIn(world, owner.File));
        world.SaveChanged(project.ContentDirectory);
        var build = project.Build(catalog, tagSchema: schema);
        var tags = Compiled(build).ToDictionary(tag => tag.Name, StringComparer.Ordinal);
        catalog.SetOverlayEntries(tags.Values.Select(tag =>
        {
            var (package, index) = TagHashes.Split(tag.TagHash);
            return new PackageOverlayEntry(new PackageEntry(tag.TagHash, tag.ClassId, (int)((tag.TypeInfo >> 9) & 127), (int)((tag.TypeInfo >> 6) & 7),
                tag.Data.Length, $"w64_sunrise_{package:x4}_0", package, index) { TypeInfo = tag.TypeInfo }, tag.Name);
        }));
        var bytes = tags.Values.ToDictionary(tag => tag.TagHash, tag => tag.Data);
        catalog.ReadOverride = tag => bytes.GetValueOrDefault(tag);
        var found = new Dictionary<string, List<TriggerResolverMirror.Candidate>>(StringComparer.Ordinal);
        try
        {
            foreach (var (file, _, door) in owners)
                found[file] = TriggerResolverMirror.Candidates(catalog, tags[world.Single(SScenario.Class).Name].TagHash,
                    TagView.At<SAuthoredObject>(world[file].Data, 0).RegistryKey, door.Trigger.Index);
        }
        finally
        {
            catalog.SetOverlayEntries([]);
            catalog.ReadOverride = null;
        }
        using var pack = ActivitySdkPack.Open(build.ActivitySdkPack!);
        var objects = pack.Rows<SdkObject>(SdkSection.Objects);
        var states = pack.Rows<SdkState>(SdkSection.States);
        var bubbles = pack.Rows<SdkBubble>(SdkSection.Bubbles);
        var occurrences = pack.Rows<SdkOccurrence>(SdkSection.Occurrences);
        var lines = new List<string>();
        foreach (var (file, row, door) in owners)
        {
            var candidates = found[file];
            int objectRow = Array.FindIndex(objects, item => item.ObjectTag == tags[file].TagHash);
            var occurs = occurrences.Where(item => item.ObjectIndex == objectRow).ToList();
            if (row is int only)
            {
                var own = Enumerable.Range(0, states.Length).Where(at => bubbles[states[at].BubbleIndex].BubbleOrdinal == only).ToList();
                // Arming in a state finds the owner through the one occurrence whose region is the state's.
                bool arms = own.All(at => occurs.Count(item => states[item.StateIndex].SliceSetIndex + states[item.StateIndex].StateOrdinal
                    == states[at].SliceSetIndex + states[at].StateOrdinal) == 1);
                WorldBuild.Check(candidates.Count == 1 && TriggerResolverMirror.Distinct(candidates) == 1 && candidates[0].Bubble == only
                    && listed[file].Count == 1 && listed[file][0].Bubble == only && own.Count == 1 && occurs.Count == 1 && arms
                    && states[own[0]].RegistryTag == candidates[0].Registry,
                    $"Door {door.Name} on row {only}: {candidates.Count} sources in rows {string.Join(",", candidates.Select(item => item.Bubble))}, "
                    + $"{occurs.Count} occurrences, arms={arms}.");
            }
            else WorldBuild.Check(candidates.Count == 2 && TriggerResolverMirror.Distinct(candidates) == 2,
                $"The unclaimed door {door.Name} has {candidates.Count} sources, not the two shared bubbles.");
            lines.Add($"{door.Name}:row={row?.ToString() ?? "none"},sources={candidates.Count},distinct={TriggerResolverMirror.Distinct(candidates)},"
                + $"rows={string.Join("+", candidates.Select(item => item.Bubble))},occurrences={occurs.Count}");
        }
        Console.WriteLine($"world_door_placement stock_row={stockRow} copy_row={clone.Row} {string.Join(" ", lines)}");
    }

    // The objective slot's report and request schemas.
    private const uint ObjectiveSense = 0x80807F04, ObjectiveAuth = 0x80807F0C;

    /**
     * Gives the made owner a combat area: the objective ends its first slot group, the firing area sets
     * and areas end its table, every copied slot ref names the owner, and every area is the asked box.
     * A squad added after the area is refused.
     */
    private static AddedCombatArea CombatArea(WorldFiles world, RegistryIndex schema, PackageCatalog catalog, string owner, uint template, uint entity)
    {
        var centre = new Vector3(607, 147, 79.9f);
        var area = WorldSquadAuthoring.AddCombatArea(world, schema, catalog, Destination, owner, centre, 25, 20);
        var (first, second, rows) = Groups(world, owner);
        uint key = TagView.At<SAuthoredObject>(world[owner].Data, 0).RegistryKey;
        uint oldKey = TagView.At<SAuthoredObject>(catalog.Read(WorldSquadAuthoring.OwnerTemplate(world[owner])!.Value), 0).RegistryKey;
        var all = new[] { area.Objective }.Concat(area.Slots).ToList();
        WorldBuild.Check(first == 3 && second == area.Slots.Count && rows.Length == 3 + area.Slots.Count && area.Objective.Index == 2 && rows[2].SlotType == 3
            && all.All(slot => rows[slot.Index].SlotType == slot.SlotType && rows[slot.Index].NameHash == StringBankWriter.Hash(slot.Name))
            && area.Slots.Any(slot => slot.SlotType == 44) && area.Slots.Any(slot => slot.SlotType == 45) && area.Groups > 0
            && WorldSquadAuthoring.CombatSlots(world[owner])!.SequenceEqual(all),
            "The combat slots do not end the made owner's groups.");
        int refs = 0;
        foreach (string file in new[] { area.ObjectiveConfig, area.AreaConfig })
        {
            byte[] data = world[file].Data;
            for (int at = 0; at + 8 <= data.Length; at += 4)
            {
                uint word = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
                ushort type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 4)), index = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 6));
                WorldBuild.Check(word != oldKey || type is not (3 or 44 or 45), $"{file} still names the template's slot {index}.");
                if (word != key) continue;
                WorldBuild.Check(index < rows.Length && rows[index].SlotType == type, $"{file} names slot {index} as type {type}.");
                refs++;
            }
        }
        // Every area of the copied config is the asked box, and every set member names one of them.
        byte[] areas = world[area.AreaConfig].Data;
        var boxes = new List<(Vector3 Min, Vector3 Max, ulong Identity, uint Slot)>();
        var members = new List<ulong>();
        for (int field = 0; field + 16 <= areas.Length; field += 8)
        {
            if (!TagArray.TryChecked(areas, field, 0, 0, out var array) || array.Count == 0) continue;
            for (int row = 0; row < array.Count; row++)
                if (array.Class == 0x80808354)
                {
                    var sector = TagView.At<C80809b8f>(areas, array.Data + row * 128 + 32);
                    boxes.Add((sector.BoundsMin, sector.BoundsMax, TagRead.U64(areas, array.Data + row * 128), TagRead.U32(areas, array.Data + row * 128 + 16)));
                }
                else if (array.Class == 0x80808352)
                {
                    members.Add(TagRead.U64(areas, array.Data + row * 16));
                    WorldBuild.Check(TagRead.U32(areas, array.Data + row * 16 + 12) == 0, "A firing area set member keeps a flag.");
                }
        }
        WorldBuild.Check(boxes.Count > 0 && boxes.All(box => box.Min == centre - new Vector3(25, 25, 0) && box.Max == centre + new Vector3(25, 25, 20)
                && rows[box.Slot].SlotType == 44)
            && members.Count > 0 && members.All(member => member == ulong.MaxValue || boxes.Any(box => box.Identity == member)),
            "A firing area is not the asked box, or a set member names no area.");
        bool refused = false;
        try { WorldSquadAuthoring.Add(world, schema, catalog, Destination, template, new("squadbed.made_late", TftNames.Installed(entity), 1, centre), owner); }
        catch (InvalidDataException) { refused = true; }
        WorldBuild.Check(refused, "A squad was added after the combat area.");
        Console.WriteLine($"world_squad_combat_area objective={area.Objective.Index} slots={area.Slots.Count} groups={area.Groups} keyed_refs={refs} areas={boxes.Count} late_squad=refused");
        return area;
    }

    /**
     * A rule-form squad copied from the patrol object's own Dreg squad (0x80BE9374): a squad slot and a
     * rule slot end its first group, the new rule names a new row in the world's copy of its list, and
     * the pack anchors the squad on that row.
     */
    private static void RuleSquad(PackageCatalog catalog, RegistryIndex schema, uint scenario, string scratch, uint entity)
    {
        const uint patrol = 0x80BE9573, spawner = 0x80BE9374;
        var project = SunriseStudio.Projects.StudioProject.Create(Path.Combine(scratch, "rule"), "Squadbed");
        WorldTemplate.Create(project.ContentDirectory, catalog, schema, scenario, Kept.ToHashSet(), Destination);
        var world = WorldFiles.Load(project.ContentDirectory, Destination);
        string owner = world.Files.Single(file => file.SourceTag == patrol).Name;
        var before = Groups(world, owner);
        var position = new System.Numerics.Vector3(604, 146, 89.9f);
        var squad = WorldSquadAuthoring.AddRuleSquad(world, schema, catalog, Destination, spawner,
            new("squadbed.rule_a", TftNames.Installed(entity), 2, position), owner);
        var after = Groups(world, owner);
        var list = WorldObjectPoints.List(world, catalog).Where(point => point.Identity == squad.Identity).ToList();
        // The squad slot ends the first group; the rule slot, which has no schema, ends the table.
        int ruleIndex = after.Rows.Length - 1;
        var firstTypes = before.Rows.Take(before.First).Select(row => row.SlotType).Append(1u).ToHashSet();
        WorldBuild.Check(after.First == before.First + 1 && after.Second == before.Second + 1 && squad.SlotIndex == before.First
            && after.Rows[squad.SlotIndex].SlotType == 1 && after.Rows[ruleIndex].SlotType == 66
            && after.Rows.Take(after.First).All(row => firstTypes.Contains(row.SlotType))
            && after.Rows[squad.SlotIndex].NameHash == StringBankWriter.Hash(squad.Name)
            && list.Count == 1 && list[0].Position == position, "The rule squad's slots or its new placement row are wrong.");
        // A second squad on the same owner that uses an existing row of the list as its point.
        var reused = WorldSquadAuthoring.AddRuleSquad(world, schema, catalog, Destination, spawner,
            new("squadbed.rule_b", TftNames.Installed(entity), 1, default), owner, existingRow: 1);
        var row1 = WorldObjectPoints.List(world, catalog).First(point => point.ListTag == list[0].ListTag && point.Row == 1);
        WorldBuild.Check(reused.Identity == row1.Identity && reused.Position == row1.Position && reused.SlotIndex == squad.SlotIndex + 1,
            "The reused-row squad does not name the list's row 1.");
        world.SaveChanged(project.ContentDirectory);
        var build = project.Build(catalog, tagSchema: schema);
        var tags = Compiled(build).ToDictionary(tag => tag.Name, StringComparer.Ordinal);
        catalog.SetOverlayEntries(tags.Values.Select(tag =>
        {
            var (package, index) = TagHashes.Split(tag.TagHash);
            return new PackageOverlayEntry(new PackageEntry(tag.TagHash, tag.ClassId, (int)((tag.TypeInfo >> 9) & 127), (int)((tag.TypeInfo >> 6) & 7),
                tag.Data.Length, $"w64_sunrise_{package:x4}_0", package, index) { TypeInfo = tag.TypeInfo }, tag.Name);
        }));
        var bytes = tags.Values.ToDictionary(tag => tag.TagHash, tag => tag.Data);
        catalog.ReadOverride = tag => bytes.GetValueOrDefault(tag);
        var slots = ComponentSlotCatalog.OwnerSlots(catalog, tags[owner].TagHash);
        // The pack keeps installed spawner and rule tags; the world's copies stand in for them in the build.
        var copies = world.Files.Where(file => file.SourceTag is not null).ToDictionary(file => file.SourceTag!.Value, file => tags[file.Name].TagHash);
        int stock = StockRules(catalog, build, tags[owner].TagHash, slots, tags[squad.ConfigFile].TagHash, copies);
        catalog.SetOverlayEntries([]);
        catalog.ReadOverride = null;
        // The second squad went in at the first group's end, so the first squad's rule moved up one.
        ruleIndex = Groups(world, owner).Rows.Length - 2;
        string ruleFile = squad.ConfigFile.Replace(".entity_config", "_rule.entity_config");
        uint config = tags[squad.ConfigFile].TagHash, rule = tags[ruleFile].TagHash;
        byte[] made = tags[squad.ConfigFile].Data;
        int peer = new TagPointer(made, TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition))).Target!.Value;
        int refs = peer + TagView.FieldOffset<SSquadComponentPeer>(nameof(SSquadComponentPeer.H81eeb835)) + TagView.FieldOffset<C80807eba>(nameof(C80807eba.RuleSlotAObjectKey));
        uint key = TagView.At<SAuthoredObject>(world[owner].Data, 0).RegistryKey;
        WorldBuild.Check(slots.Count(row => row.SlotIndex == squad.SlotIndex && row.SlotType == 1 && row.ConfigTag == config) == 1
            && slots.Count(row => row.SlotIndex == ruleIndex && row.SlotType == 66 && row.ConfigTag == rule) == 1
            && TagRead.U32(made, refs) == key && TagRead.U16(made, refs + 4) == 66 && TagRead.U16(made, refs + 6) == ruleIndex
            && TagRead.U16(made, refs + 14) == ruleIndex, "The built owner does not reach the rule squad and its rule, or the rule refs are wrong.");
        using var pack = ActivitySdkPack.Open(build.ActivitySdkPack!);
        var squads = pack.Rows<SdkSquad>(SdkSection.Squads);
        var packSlots = pack.Rows<SdkSlot>(SdkSection.Slots);
        var anchors = pack.Rows<SdkSquadAnchor>(SdkSection.SquadAnchors);
        var members = pack.Rows<SdkSquadMember>(SdkSection.SquadMembers);
        var second = squads.Single(row => pack.Text(packSlots[row.SlotIndex].Name) == reused.Name);
        WorldBuild.Check(anchors[second.Anchors.First].PlacementOrdinal == 1 && anchors[second.Anchors.First].PlacedEntryIdentity == row1.Identity,
            "The reused-row squad's anchor is wrong.");
        var item = squads.Single(row => pack.Text(packSlots[row.SlotIndex].Name) == squad.Name);
        var anchor = anchors[item.Anchors.First];
        uint listTag = tags[world.Files.Single(file => file.SourceTag == list[0].ListTag).Name].TagHash;
        WorldBuild.Check(item.SpawnerConfigTag == config && item.SpawnRuleConfigTag == rule && item.Flags == 0x3F && item.Anchors.Count == 1
            && anchor.Flags == 1 && anchor.ObjectListTag == listTag && anchor.PlacementOrdinal == list[0].Row && anchor.PlacedEntryIdentity == squad.Identity
            && BitConverter.UInt32BitsToSingle(anchor.PositionX) == position.X && members[item.Members.First].DefaultCount == 2
            && packSlots.Count(slot => slot.ObjectIndex == item.ObjectIndex && slot.SlotIndex == ruleIndex && slot.SlotType == 66) == 1,
            "The pack rows of the rule squad are wrong.");
        Console.WriteLine($"world_squad_new_rule owner={owner.Split('/')[^1]} spawner={spawner:X8} slots={squad.SlotIndex},{ruleIndex} stock_rules_ok={stock} "
            + $"list={list[0].ListTag:X8} row={list[0].Row} identity={squad.Identity:X16} rule={rule:X8}");
    }

    /**
     * Every other squad of the built owner: both rule refs of its built spawner name type-66 slots of the
     * owner, and its pack row's rule config is the config behind one of them. Run with the build overlaid.
     * @return How many stock squads were checked.
     */
    private static int StockRules(PackageCatalog catalog, SunriseStudio.Projects.ProjectBuildResult build, uint owner,
        System.Collections.Immutable.ImmutableArray<ComponentSlotEvidence> slots, uint skip, IReadOnlyDictionary<uint, uint> copies)
    {
        using var pack = ActivitySdkPack.Open(build.ActivitySdkPack!);
        var objects = pack.Rows<SdkObject>(SdkSection.Objects);
        int row = Array.FindIndex(objects, item => item.ObjectTag == owner);
        int checkedSquads = 0;
        int refsField = TagView.FieldOffset<SSquadComponentPeer>(nameof(SSquadComponentPeer.H81eeb835)) + TagView.FieldOffset<C80807eba>(nameof(C80807eba.RuleSlotAObjectKey));
        foreach (var squad in pack.Rows<SdkSquad>(SdkSection.Squads).Where(item => item.ObjectIndex == row && item.SpawnerConfigTag != skip))
        {
            byte[] data = catalog.Read(copies.GetValueOrDefault(squad.SpawnerConfigTag, squad.SpawnerConfigTag));
            int peer = new TagPointer(data, TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition))).Target!.Value;
            var rules = new List<uint>();
            foreach (int at in (ReadOnlySpan<int>)[peer + refsField, peer + refsField + 8])
            {
                if (TagRead.U16(data, at + 4) != 66) continue;
                ushort index = TagRead.U16(data, at + 6);
                var target = slots.Where(slot => slot.SlotIndex == index).ToList();
                WorldBuild.Check(target.Count > 0 && target.All(slot => slot.SlotType == 66),
                    $"Stock spawner 0x{squad.SpawnerConfigTag:X8} names slot {index}, which is not a spawn rule of the built owner.");
                rules.AddRange(target.Select(slot => slot.ConfigTag));
            }
            WorldBuild.Check(squad.SpawnRuleConfigTag == uint.MaxValue || rules.Contains(copies.GetValueOrDefault(squad.SpawnRuleConfigTag, squad.SpawnRuleConfigTag)),
                $"Stock squad 0x{squad.SpawnerConfigTag:X8} has pack rule 0x{squad.SpawnRuleConfigTag:X8}, which its spawner does not name.");
            checkedSquads++;
        }
        return checkedSquads;
    }

    /**
     * Five rule-form squads on the patrol object, each differing from stock squad 0x80BE9374 in a known
     * set of fields. The twin differs only in its slot index and the current indices of its rule refs.
     */
    private static void RuleBisect(PackageCatalog catalog, RegistryIndex schema, uint scenario, string scratch, uint entity)
    {
        const uint patrol = 0x80BE9573, spawner = 0x80BE9374;
        var project = SunriseStudio.Projects.StudioProject.Create(Path.Combine(scratch, "bisect"), "Squadbed");
        WorldTemplate.Create(project.ContentDirectory, catalog, schema, scenario, Kept.ToHashSet(), Destination);
        var world = WorldFiles.Load(project.ContentDirectory, Destination);
        string owner = world.Files.Single(file => file.SourceTag == patrol).Name;
        var target = new System.Numerics.Vector3(604, 146, 89.9f);
        var variants = new (string Name, RuleSquadOptions Options)[]
        {
            ("squadbed.v0", new(NewName: false, NewIdentity: false, OwnRule: false, NewRow: false, Retarget: false)),
            ("squadbed.v1", new(NewName: true, NewIdentity: false, OwnRule: false, NewRow: false, Retarget: false)),
            ("squadbed.v2", new(NewName: false, NewIdentity: true, OwnRule: false, NewRow: false, Retarget: false)),
            ("squadbed.v3", new(NewName: false, NewIdentity: false, OwnRule: true, NewRow: false, Retarget: false)),
            ("squadbed.v4", new()),
        };
        var added = variants.Select(variant => WorldSquadAuthoring.AddRuleSquad(world, schema, catalog, Destination, spawner,
            new(variant.Name, TftNames.Installed(entity), 1, target), owner, growCandidates: false, options: variant.Options)).ToList();
        // Which bytes of each config differ from the installed spawner, as peer offsets.
        var installed = TftClone.Clone([spawner], new SunriseStudio.Tags.Graph.CatalogTagSource(catalog), schema, _ => "x.entity_config").Single().Data;
        int peer = new TagPointer(installed, TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition))).Target!.Value;
        string Diff(AddedSquad squad)
        {
            byte[] data = world[squad.ConfigFile].Data;
            if (data.Length != installed.Length) return "length";
            var words = Enumerable.Range(0, data.Length / 2).Select(at => at * 2).Where(at => data[at] != installed[at] || data[at + 1] != installed[at + 1])
                .Select(at => at - peer).ToList();
            return string.Join(",", words.Select(at => $"0x{at:X}"));
        }
        var diffs = added.Select(Diff).ToList();
        // Peer +0x36 slot index; +0x9E and +0xA6 rule ref indices; +0x78..0x7F own identity; +0x80..0x83 name hash.
        WorldBuild.Check(diffs[0] == "0x36,0x9E,0xA6" && diffs[1] == "0x36,0x80,0x82,0x9E,0xA6" && diffs[2] == "0x36,0x78,0x7A,0x7C,0x7E,0x9E,0xA6"
            && added.Select(squad => (int)squad.SlotIndex).SequenceEqual([6, 7, 8, 9, 10]), $"The bisect squads differ from stock in other fields: {string.Join(" | ", diffs)}.");
        var stockRow = WorldObjectPoints.List(world, catalog).First(point => point.Identity == 0x91AA769751A9E9B0);
        WorldBuild.Check(added.Take(4).All(squad => squad.Identity == stockRow.Identity && squad.Position == stockRow.Position)
            && added[4].Identity != stockRow.Identity && added[4].Position == target, "The bisect squads' points are wrong.");
        world.SaveChanged(project.ContentDirectory);
        var build = project.Build(catalog, tagSchema: schema);
        var tags = Compiled(build).ToDictionary(tag => tag.Name, StringComparer.Ordinal);
        catalog.SetOverlayEntries(tags.Values.Select(tag =>
        {
            var (package, index) = TagHashes.Split(tag.TagHash);
            return new PackageOverlayEntry(new PackageEntry(tag.TagHash, tag.ClassId, (int)((tag.TypeInfo >> 9) & 127), (int)((tag.TypeInfo >> 6) & 7),
                tag.Data.Length, $"w64_sunrise_{package:x4}_0", package, index) { TypeInfo = tag.TypeInfo }, tag.Name);
        }));
        var bytes = tags.Values.ToDictionary(tag => tag.TagHash, tag => tag.Data);
        catalog.ReadOverride = tag => bytes.GetValueOrDefault(tag);
        var slots = ComponentSlotCatalog.OwnerSlots(catalog, tags[owner].TagHash);
        var copies = world.Files.Where(file => file.SourceTag is not null).ToDictionary(file => file.SourceTag!.Value, file => tags[file.Name].TagHash);
        int checkedRules = StockRules(catalog, build, tags[owner].TagHash, slots, 0, copies);
        catalog.SetOverlayEntries([]);
        catalog.ReadOverride = null;
        WorldBuild.Check(added.All(squad => slots.Count(row => row.SlotIndex == squad.SlotIndex && row.SlotType == 1 && row.ConfigTag == tags[squad.ConfigFile].TagHash) == 1),
            "The built owner does not reach every bisect squad.");
        using var pack = ActivitySdkPack.Open(build.ActivitySdkPack!);
        var squads = pack.Rows<SdkSquad>(SdkSection.Squads);
        var packSlots = pack.Rows<SdkSlot>(SdkSection.Slots);
        var anchors = pack.Rows<SdkSquadAnchor>(SdkSection.SquadAnchors);
        var lines = new List<string>();
        foreach (var squad in added)
        {
            var item = squads.Single(row => pack.Text(packSlots[row.SlotIndex].Name) == squad.Name);
            var anchor = anchors[item.Anchors.First];
            WorldBuild.Check(item.Anchors.Count == 1 && anchor.Flags == 1 && anchor.PlacedEntryIdentity == squad.Identity && item.Flags == 0x3F,
                $"The pack rows of {squad.Name} are wrong.");
            lines.Add($"{squad.Name.Split('.')[^1]}:slot={squad.SlotIndex},row={anchor.PlacementOrdinal},rule={item.SpawnRuleConfigTag:X8}");
        }
        Console.WriteLine($"world_squad_new_bisect {string.Join(" ", lines)} stock_point=({stockRow.Position.X:F1},{stockRow.Position.Y:F1},{stockRow.Position.Z:F1}) rule_checks={checkedRules}");
    }

    /** Every authored object's bytes as the world holds them now. */
    private static Dictionary<string, byte[]> Held(WorldFiles world) =>
        world.Files.Where(file => file.ClassId == SAuthoredObject.Class).ToDictionary(file => file.Name, file => file.Data);

    private static (int First, int Second, ObjectSlot[] Rows) Groups(WorldFiles world, string owner, Dictionary<string, byte[]>? held = null)
    {
        byte[] data = held?[owner] ?? world[owner].Data;
        uint groups = TagRead.U32(data, TagView.FieldOffset<SAuthoredObject>(nameof(SAuthoredObject.SlotTable)));
        return ((int)(groups & 0xFFFF), (int)(groups >> 16), TagView.Rows(data, in TagView.At<SAuthoredObject>(data, 0).SlotTable.Slots, ObjectSlot.Class).ToArray());
    }

    /** Every installed descriptor of the owner past the old first group now sits three slots up, in a copy the world owns. */
    private static int Renumbered(WorldFiles world, PackageCatalog catalog, string ownerFile, int first)
    {
        var evidence = ComponentSlotCatalog.OwnerSlots(catalog, world[ownerFile].SourceTag!.Value);
        var late = evidence.Where(row => row.SlotIndex >= first).ToList();
        foreach (var row in late)
        {
            var copy = world.Files.SingleOrDefault(file => file.SourceTag == row.ConfigTag);
            WorldBuild.Check(copy is not null && TagRead.U16(copy.Data, row.DescriptorOffset + SlotIndexField) == row.SlotIndex + 3
                && TagRead.U16(catalog.Read(row.ConfigTag), row.DescriptorOffset + SlotIndexField) == row.SlotIndex,
                $"Slot {row.SlotIndex} of the owner was not moved up in a config the world owns.");
        }
        WorldBuild.Check(evidence.Where(row => row.SlotIndex < first && row.SlotType != 1).All(row =>
                world.Files.SingleOrDefault(file => file.SourceTag == row.ConfigTag) is not { } copy
                || TagRead.U16(copy.Data, row.DescriptorOffset + SlotIndexField) == row.SlotIndex),
            "A slot before the new ones changed its index.");
        // Every keyed slot ref past the old group end in the owner's package, descriptor or not, moved up in a world copy.
        var owner = world[ownerFile].SourceTag!.Value;
        catalog.TryGetEntry(owner, out var ownerEntry);
        byte[] installedOwner = catalog.Read(owner);
        uint key = TagView.At<SAuthoredObject>(installedOwner, 0).RegistryKey;
        var types = TagView.Rows(installedOwner, in TagView.At<SAuthoredObject>(installedOwner, 0).SlotTable.Slots, ObjectSlot.Class).ToArray();
        int refs = 0, tags = 0;
        foreach (var entry in catalog.Entries.Where(entry => entry.PackageId == ownerEntry!.PackageId && entry.Reference == SEntityConfig.Class))
        {
            byte[] bytes = catalog.Read(entry.TagHash);
            var hits = Enumerable.Range(0, bytes.Length / 4 - 1).Select(word => word * 4).Where(at => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)) == key
                && BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 6)) is var index && index >= first && index < types.Length
                && BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 4)) == types[index].SlotType).ToList();
            if (hits.Count == 0) continue;
            var copy = world.Files.SingleOrDefault(file => file.SourceTag == entry.TagHash);
            WorldBuild.Check(copy is not null && hits.All(at => TagRead.U16(copy.Data, at + 6) == TagRead.U16(bytes, at + 6) + 3),
                $"Config 0x{entry.TagHash:X8} still names a moved slot of the owner by its old index.");
            refs += hits.Count;
            tags++;
        }
        Console.WriteLine($"world_squad_new_renumber keyed_refs={refs} configs={tags} descriptor_configs={late.Select(row => row.ConfigTag).Distinct().Count()}");
        return tags;
    }

    private static void Refusals(WorldFiles world, RegistryIndex schema, PackageCatalog catalog, uint template, uint entity)
    {
        var cases = new (NewSquad Squad, uint Template)[]
        {
            (new("squadbed.wild_a", TftNames.Installed(entity), 1, default), template),
            (new("squadbed.none", TftNames.Installed(entity), 0, default), template),
            (new("squadbed.not_entity", TftNames.Installed(template), 1, default), template),
            (new("squadbed.bad_template", TftNames.Installed(entity), 1, default), entity),
        };
        foreach (var (squad, from) in cases)
        {
            bool refused = false;
            try { WorldSquadAuthoring.Add(world, schema, catalog, Destination, from, squad); }
            catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException) { refused = true; }
            WorldBuild.Check(refused, $"Squad {squad.Name} was added.");
        }
    }

    /** The project's tags as the built packages hold them. */
    private static List<TftCompiledTag> Compiled(SunriseStudio.Projects.ProjectBuildResult build)
    {
        string packages = Path.Combine(build.OutputDirectory, "squad-check", "packages");
        Directory.CreateDirectory(packages);
        foreach (string file in build.PackageFiles) File.Copy(file, Path.Combine(packages, Path.GetFileName(file)), true);
        using var built = PackageCatalog.Open(Path.GetDirectoryName(packages)!);
        return build.TagFiles.Select(tag => built.TryGetEntry(tag.TagHash, out var entry)
            ? new TftCompiledTag(tag.Id, tag.TagHash, entry.Reference, entry.TypeInfo, built.Read(tag.TagHash))
            : throw new InvalidDataException($"{tag.Id} is not in the built packages.")).ToList();
    }

    /** The built owner reaches each new config through its new set and link, as the slot walk reads a placed object. */
    private static void Built(PackageCatalog catalog, RegistryIndex schema, SunriseStudio.Projects.ProjectBuildResult build, WorldFiles world,
        IReadOnlyList<AddedSquad> added, uint entity, int first)
    {
        var tags = Compiled(build).ToDictionary(tag => tag.Name, StringComparer.Ordinal);
        foreach (var name in added.SelectMany(squad => new[] { squad.ConfigFile, squad.OwnerFile }).Distinct())
        {
            var tag = tags[name];
            WorldBuild.Check(TagDocument.Parse(tag.Data, tag.ClassId, schema).Problems.Count == 0, $"{name} does not parse after the build.");
        }
        // The built tags stand over the installation, by tag only; the walk reads no wide references.
        catalog.SetOverlayEntries(tags.Values.Select(tag =>
        {
            var (package, index) = TagHashes.Split(tag.TagHash);
            return new PackageOverlayEntry(new PackageEntry(tag.TagHash, tag.ClassId, (int)((tag.TypeInfo >> 9) & 127), (int)((tag.TypeInfo >> 6) & 7),
                tag.Data.Length, $"w64_sunrise_{package:x4}_0", package, index) { TypeInfo = tag.TypeInfo }, tag.Name);
        }));
        var bytes = tags.Values.ToDictionary(tag => tag.TagHash, tag => tag.Data);
        catalog.ReadOverride = tag => bytes.GetValueOrDefault(tag);
        uint owner = tags[added[0].OwnerFile].TagHash;
        var slots = ComponentSlotCatalog.OwnerSlots(catalog, owner);
        int candidates = 0;
        var owns = new HashSet<ulong>();
        foreach (var squad in added)
        {
            uint config = tags[squad.ConfigFile].TagHash;
            WorldBuild.Check(slots.Count(row => row.SlotIndex == squad.SlotIndex && row.SlotType == 1 && row.ConfigTag == config
                    && row.SenseSchema == 0x80807ECC && row.AuthSchema == 0x80807EC9) == 1,
                $"The built owner does not reach squad {squad.Name} at slot {squad.SlotIndex}.");
            // Every candidate of every lane is a placed entry of the chosen entity.
            byte[] data = tags[squad.ConfigFile].Data;
            int peer = new TagPointer(data, TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition))).Target!.Value;
            int cells = peer + TagView.FieldOffset<SSquadComponentPeer>(nameof(SSquadComponentPeer.H81eeb835)) + TagView.FieldOffset<C80807eba>(nameof(C80807eba.Cells));
            var (_, member, _) = TagArray.Checked(data, cells);
            // Every installed inline spawner's point is its own identity + 1.
            ulong own = TagRead.U64(data, peer + TagView.FieldOffset<SSquadComponentPeer>(nameof(SSquadComponentPeer.H81eeb835)) + 16);
            WorldBuild.Check(own + 1 == squad.Identity && owns.Add(own), $"Squad {squad.Name} point is not its own identity + 1, or two squads share one.");
            for (int lane = 0; lane < 6; lane++)
            {
                var (count, rows, _) = TagArray.Checked(data, member + TagView.FieldOffset<SquadMember>(nameof(SquadMember.CharacterChoices)) + lane * 16);
                WorldBuild.Check(count == squad.Count, $"Squad {squad.Name} lane {lane} has {count} candidates.");
                for (int row = 0; row < count; row++)
                {
                    int blueprint = new TagPointer(data, rows + row * 24).Target!.Value;
                    WorldBuild.Check(TagView.ClassAt(data, blueprint) == AuthoredPlacementEntry.Class && TagRead.U32(data, blueprint) == entity,
                        $"Squad {squad.Name} lane {lane} candidate {row} does not place 0x{entity:X8}.");
                    candidates++;
                }
            }
        }
        var moved = slots.Where(row => row.SlotIndex >= first + added.Count).ToList();
        WorldBuild.Check(moved.Count > 0 && slots.All(row => row.SlotIndex < first || row.SlotIndex >= first + added.Count || added.Any(squad => squad.SlotIndex == row.SlotIndex)),
            "The built owner's later slots did not move past the new ones.");
        catalog.SetOverlayEntries([]);
        catalog.ReadOverride = null;
        Console.WriteLine($"world_squad_new_built owner={owner:X8} evidence={slots.Length} new_slots={added.Count} moved_slots={moved.Count} candidates={candidates}");
    }

    /** The mod pack carries one squad per name, with its slot, member, actor class and inline anchor, as the server reads them. */
    private static void Pack(SunriseStudio.Projects.ProjectBuildResult build, WorldFiles world, IReadOnlyList<AddedSquad> added, uint entity, int first)
    {
        using var pack = ActivitySdkPack.Open(build.ActivitySdkPack!);
        var squads = pack.Rows<SdkSquad>(SdkSection.Squads);
        var slots = pack.Rows<SdkSlot>(SdkSection.Slots);
        var objects = pack.Rows<SdkObject>(SdkSection.Objects);
        var members = pack.Rows<SdkSquadMember>(SdkSection.SquadMembers);
        var anchors = pack.Rows<SdkSquadAnchor>(SdkSection.SquadAnchors);
        var classes = pack.Rows<SdkActorClass>(SdkSection.ActorClasses);
        var occurrences = pack.Rows<SdkOccurrence>(SdkSection.Occurrences);
        var (texts, textWords) = pack.Words(SdkSection.Texts);
        var tags = build.TagFiles.ToDictionary(tag => tag.Id, StringComparer.Ordinal);
        // The server resolves a squad by squad id, slot id, slot name or alias, and needs exactly one match.
        int Matches(string name) => squads.Count(squad => pack.Text(squad.Id) == name || pack.Text(slots[squad.SlotIndex].Id) == name
            || pack.Text(slots[squad.SlotIndex].Name) == name || Enumerable.Range((int)slots[squad.SlotIndex].Aliases.First, (int)slots[squad.SlotIndex].Aliases.Count)
                .Any(row => pack.Text(new SdkStringRef { Offset = texts[row * textWords], Length = texts[row * textWords + 1] }) == name));
        foreach (var squad in added)
        {
            WorldBuild.Check(Matches(squad.Name) == 1, $"The pack does not resolve {squad.Name} to one squad.");
            int row = Array.FindIndex(squads, item => pack.Text(slots[item.SlotIndex].Name) == squad.Name);
            var item = squads[row];
            var slot = slots[item.SlotIndex];
            var member = members[item.Members.First];
            bool bound = squad.Identity != 0;
            var anchor = bound ? anchors[item.Anchors.First] : default;
            uint config = tags[squad.ConfigFile].TagHash;
            WorldBuild.Check(item.ScenarioIndex == 0 && item.SpawnerConfigTag == config && item.SpawnRuleConfigTag == (bound ? config : uint.MaxValue) && item.Flags == (bound ? 0x3Fu : 0x75u)
                && slot.ObjectIndex == item.ObjectIndex && slot.SlotIndex == squad.SlotIndex && slot.SlotType == 1 && slot.ComponentClass == 0x80809A3B
                && slot.Flags == 2 && objects[item.ObjectIndex].ObjectTag == tags[squad.OwnerFile].TagHash
                && occurrences[item.OccurrenceIndex].ObjectIndex == item.ObjectIndex
                && item.Members.Count == 1 && member.SquadIndex == row && member.DefaultCount == squad.Count
                && member.CandidateCounts0 == ((uint)squad.Count | (uint)squad.Count << 16) && member.ActorClassIndex < classes.Length
                && classes[member.ActorClassIndex].DefinitionTag == entity && (member.Flags & 0xF) == 0xF
                && (!bound ? item.Anchors.Count == 0 : item.Anchors.Count == 1 && anchor.SquadIndex == row && anchor.Flags == 3 && anchor.PlacedEntryIdentity == squad.Identity
                    && BitConverter.UInt32BitsToSingle(anchor.PositionX) == squad.Position.X && BitConverter.UInt32BitsToSingle(anchor.PositionY) == squad.Position.Y
                    && BitConverter.UInt32BitsToSingle(anchor.PositionZ) == squad.Position.Z),
                $"The pack rows of squad {squad.Name} are wrong.");
        }
        // Each object's slots rise in index, as the roster group the server builds needs; the owner holds three more than before.
        bool rising = objects.All(item => Enumerable.Range((int)item.Slots.First, (int)item.Slots.Count).Select(at => slots[at])
            .Where(slot => slot.ComponentClass != uint.MaxValue).Select(slot => slot.SlotIndex).Zip(
                Enumerable.Range((int)item.Slots.First, (int)item.Slots.Count).Select(at => slots[at]).Where(slot => slot.ComponentClass != uint.MaxValue)
                    .Select(slot => slot.SlotIndex).Skip(1), (a, b) => a < b).All(x => x));
        var owner = objects.Single(item => item.ObjectTag == tags[added[0].OwnerFile].TagHash);
        var ownerSlots = Enumerable.Range((int)owner.Slots.First, (int)owner.Slots.Count).Select(at => slots[at]).ToArray();
        var rows = TagView.Rows(world[added[0].OwnerFile].Data, in TagView.At<SAuthoredObject>(world[added[0].OwnerFile].Data, 0).SlotTable.Slots, ObjectSlot.Class).ToArray();
        WorldBuild.Check(rising && ownerSlots.Length == rows.Length
            && ownerSlots.Select((slot, at) => slot.SlotIndex == at && slot.SlotType == rows[at].SlotType).All(x => x)
            && squads.Select((item, at) => item.ScenarioIndex).SequenceEqual(squads.Select(item => item.ScenarioIndex).Order()),
            $"The pack's slot rows do not follow the owner's slot table, or squads left their scenario's run: rising={rising} pack={string.Join(",", ownerSlots.Select(slot => $"{slot.SlotIndex}:{slot.SlotType}"))} table={string.Join(",", rows.Select(row => row.SlotType))}.");
        Console.WriteLine($"world_squad_new_sdk squads={squads.Length} owner_slots={ownerSlots.Length} new={added.Count} "
            + $"names={string.Join(',', added.Select(squad => squad.Name))} resolved=1 each");
    }
}
