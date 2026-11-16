using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Graph;
using SunriseStudio.Tags.Tft;
using SunriseStudio.Tags.Views;
using SunriseStudio.World.Sdk;

namespace SunriseStudio.World;

/**
 * A squad to add to a world.
 * @param Name The slot name a mission script finds the squad by.
 * @param Entity A file of this world, or TftNames.Installed of an installed entity.
 * @param Count Candidates per lane, which is the member count the server asks for by default.
 * @param Position The squad's own spawn point, in map space.
 */
public sealed record NewSquad(string Name, string Entity, int Count, Vector3 Position);

/**
 * Parts of a squad edit, each on by default; turning one off isolates it.
 * @param Renumber Put the slot at the end of the first slot group and move later slots up; off puts it at the table end, in the second group.
 * @param GrowCandidates Give each lane the asked count; off keeps the template's count.
 * @param LocalLeaves Copy leaf tags the template names from packages no world file names into the world.
 * @param PairIdentity Give the spawner a new own identity and its point that identity + 1; off keeps the template's own identity.
 * @param ClearGroupHash Set the descriptor's group hash at peer +0x40 to the empty hash, as dropship squads have it.
 * @param AreaHash Value for the spawner's area hash at peer +0x84, such as a patrol spawner's; null keeps the template's.
 */
public sealed record SquadAddOptions(bool Renumber = true, bool GrowCandidates = true, bool LocalLeaves = true, bool PairIdentity = true,
    bool ClearGroupHash = false, uint? AreaHash = null);

/**
 * Fields a rule-form squad does not share with the stock squad it copies; each on by default.
 * @param NewName A new name hash at peer +0x80; off keeps the stock one.
 * @param NewIdentity A new own identity at peer +0x78; off keeps the stock one.
 * @param OwnRule Its own rule slot and rule config; off names the stock rule slots, so it spawns at the stock point.
 * @param NewRow With its own rule, a new placement row at the position; off names the stock rule's row.
 * @param Retarget Point the candidates at the asked entity; off keeps the stock entity.
 */
public sealed record RuleSquadOptions(bool NewName = true, bool NewIdentity = true, bool OwnRule = true, bool NewRow = true, bool Retarget = true);

/** A squad the world added: its owner, its new slot, its config and its point. */
public sealed record AddedSquad(string Name, string OwnerFile, ushort SlotIndex, string ConfigFile, ulong Identity, Vector3 Position,
    int Count, string? Entity);

/**
 * New squads in a world copy. Each is a new type-1 slot in an authored object the world owns, a new
 * set, link and inline spawner config copied from an installed one, and its own point in the config.
 */
public static partial class WorldSquadAuthoring
{
    /** The most candidates one lane takes. */
    public const int MaximumCount = 64;
    /** The longest squad name. */
    public const int MaximumName = 128;
    private const ushort SquadSlotType = 1;
    // The slot table's first word holds two u16 counts: slots with components first, the rest after.
    private static readonly int GroupsField = TagView.FieldOffset<SAuthoredObject>(nameof(SAuthoredObject.SlotTable));
    private const string NameKey = "squad_name", TemplateKey = "squad_template";

    /** The placement identity of a squad's point: its spawner's own identity + 1; stable for a name. */
    public static ulong Identity(string destination, string name) => OwnIdentity(destination, name) + 1;

    /** The spawner's own placement identity; the low bit is clear so the point's + 1 never wraps. */
    private static ulong OwnIdentity(string destination, string name) => WorldContent.PlacementIdentity($"squad/{destination}/{name}") & ~1UL;

    /**
     * Adds one squad. The slot goes at the end of the owner's first slot group, so slots after it move
     * up one; every config the owner reaches that names one of those slots is copied and renumbered.
     * @param template An installed inline spawner config, as InlineTemplates lists them.
     * @param ownerFile The authored object file to own the slot; null takes the first with a squad in a live bubble.
     */
    public static AddedSquad Add(WorldFiles world, RegistryIndex schema, PackageCatalog catalog, string destination, uint template,
        NewSquad squad, string? ownerFile = null, CancellationToken token = default) =>
        Add(world, schema, catalog, destination, template, squad, new SquadAddOptions(), ownerFile, token);

    /**
     * Adds one squad with chosen variants of the edit, so each part can be tried alone in game.
     * @param options Which parts of the edit to make; the default makes all of them.
     */
    public static AddedSquad Add(WorldFiles world, RegistryIndex schema, PackageCatalog catalog, string destination, uint template,
        NewSquad squad, SquadAddOptions options, string? ownerFile = null, CancellationToken token = default)
    {
        CheckSquad(world, catalog, squad);
        if (!catalog.TryGetEntry(template, out var templateEntry) || templateEntry.Reference != SEntityConfig.Class
            || SquadConfig.Find(catalog.Read(template)) is null)
            throw new InvalidDataException($"0x{template:X8} is not an installed squad spawner with empty rule refs and one member.");
        var owner = ownerFile is null ? DefaultOwner(world, catalog, token) : world[ownerFile];
        // A squad owner the world made has no installed source; its chain and block come from its template.
        uint? made = OwnerTemplate(owner);
        if (owner.ClassId != SAuthoredObject.Class || (owner.SourceTag ?? made) is not uint source)
            throw new InvalidDataException($"{owner.Name} is not a copied authored object or a squad owner.");
        var evidence = ComponentSlotCatalog.OwnerSlots(catalog, source, token);
        var chain = evidence.FirstOrDefault(row => row.SlotType == SquadSlotType && row.Path.Length == 3
                && row.Path[0].Class == SAuthoredObjectSet.Class && row.Path[1].Class == SEntityLink.Class && SingleTarget(catalog, row.Path[0].Tag))
            ?? throw new InvalidDataException($"{owner.Name} has no squad reached through one set and one link to copy.");

        byte[] held = owner.Data;
        uint key = TagView.At<SAuthoredObject>(held, 0).RegistryKey, groups = TagRead.U32(held, GroupsField);
        var types = TagView.Rows(held, in TagView.At<SAuthoredObject>(held, 0).SlotTable.Slots, ObjectSlot.Class).ToArray();
        int first = (int)(groups & 0xFFFF), rest = (int)(groups >> 16);
        uint nameHash = StringBankWriter.Hash(squad.Name);
        if (first + rest != types.Length) throw new InvalidDataException($"{owner.Name} slot groups do not add up to its {types.Length} slots.");
        if (types.Any(row => row.NameHash == nameHash)) throw new InvalidDataException($"{owner.Name} already has a slot named {squad.Name}.");
        // A made owner's combat slots end its table and are named by index, so no squad may go before them.
        if (made is not null && rest > 0) throw new InvalidDataException($"{owner.Name} has a combat area; add its squads before the area.");

        // At the table end no slot moves, but the new slot sits in the second group.
        int index = options.Renumber || made is not null ? first : types.Length;
        if (options.Renumber && made is null)
            Renumber(world, schema, catalog, destination, owner.Name, source, key, types.Select(row => (ushort)row.SlotType).ToArray(), first, token);

        string slug = Slug(squad.Name);
        string File(uint classId) => $"{WorldTemplate.Folder}/{destination}/{WorldTemplate.TypeName(schema, classId)}/squad_{slug}.{WorldTemplate.TypeName(schema, classId)}";
        string setFile = File(SAuthoredObjectSet.Class), linkFile = File(SEntityLink.Class), configFile = File(SEntityConfig.Class);
        if (world.Files.Any(file => file.Name == setFile || file.Name == linkFile || file.Name == configFile))
            throw new InvalidDataException($"The world already has files for a squad named like {squad.Name}.");

        string info = new JsonObject { [NameKey] = squad.Name, [TemplateKey] = $"0x{template:X8}" }.ToJsonString();
        uint set = chain.Path[0].Tag, link = chain.Path[1].Tag;
        string oldConfig = TftNames.Installed(chain.ConfigTag);
        foreach (var file in TftClone.Clone([set, link], new CatalogTagSource(catalog), schema, tag => tag == set ? setFile : linkFile))
            world.Add(new TftFile
            {
                Name = file.Name, TagType = file.TagType, ClassId = file.ClassId, TypeInfo = file.TypeInfo, BuildHash = file.BuildHash,
                Layout = file.Layout, Data = file.Data, Info = info,
                References = file.References.Select(reference => reference.Target == oldConfig ? reference with { Target = configFile } : reference).ToList(),
            });

        int block = made is null ? BlockIndex(catalog.Read(source), chain.SubblockOffset) : 0;
        int bubble = 0;
        world.Edit(owner.Name, (_, fields) =>
        {
            var table = (JsonObject)WorldFiles.Field(fields, "slot_table");
            ((JsonArray)WorldFiles.Field(table, "slots")).Insert(index, new JsonObject { ["name_hash"] = WorldFiles.HashText(nameHash), ["slot_type"] = (int)SquadSlotType });
            var row = (JsonObject)((JsonArray)WorldFiles.Field(fields, "entry_filters"))[block]!;
            bubble = (int)WorldFiles.Field(row, "bubble_index");
            ((JsonArray)WorldFiles.Field(row, "handles")).Add(new JsonObject { ["handle"] = setFile });
        });
        var edited = world[owner.Name];
        byte[] data = edited.Data.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(GroupsField),
            options.Renumber || made is not null ? (uint)(first + 1) | (uint)rest << 16 : (uint)first | (uint)(rest + 1) << 16);
        world.Replace(WorldDevices.With(edited, data, edited.References));

        ulong identity = options.PairIdentity ? OwnIdentity(destination, squad.Name) : Identity(destination, squad.Name);
        var clone = TftClone.Clone([template], new CatalogTagSource(catalog), schema, _ => configFile).Single();
        var parts = SquadConfig.Find(clone.Data)!.Value;
        int count = options.GrowCandidates ? squad.Count : SquadConfig.LaneCount(clone.Data, parts);
        // A template with no point of its own gives a squad with no point; the script must name a spawn rule.
        ulong point = parts.Record < 0 ? 0 : Identity(destination, squad.Name);
        if (options.LocalLeaves) clone = LocalLeaves(world, schema, catalog, destination, clone);
        world.Add(SquadConfig.Make(clone, schema, key, (ushort)index, bubble, squad.Name, identity, squad.Position, count, squad.Entity, info,
            options.PairIdentity, options.ClearGroupHash ? SquadConfig.EmptyKey : null, areaHash: options.AreaHash));
        return new(squad.Name, owner.Name, (ushort)index, configFile, point, parts.Record < 0 ? default : squad.Position, count, squad.Entity);
    }

    /**
     * Copies each leaf tag the clone names from a package no world file names, and points the clone at
     * the copy. A leaf is a tag with no references, such as a spawn location table.
     */
    private static TftFile LocalLeaves(WorldFiles world, RegistryIndex schema, PackageCatalog catalog, string destination, TftFile clone)
    {
        var packages = new HashSet<ushort>();
        foreach (var file in world.Files)
        {
            if (file.SourceTag is uint copied && catalog.TryGetEntry(copied, out var own)) packages.Add(own.PackageId);
            foreach (var reference in file.References.Where(x => x.Target.StartsWith('#') && !x.Target.StartsWith("##", StringComparison.Ordinal)))
                if (catalog.TryGetEntry((uint)TftNames.InstalledHash(reference.Target), out var entry)) packages.Add(entry.PackageId);
        }
        var references = clone.References.ToList();
        for (int row = 0; row < references.Count; row++)
        {
            var reference = references[row];
            if (!reference.Target.StartsWith('#') || reference.Target.StartsWith("##", StringComparison.Ordinal)) continue;
            uint tag = (uint)TftNames.InstalledHash(reference.Target);
            if (!catalog.TryGetEntry(tag, out var entry) || packages.Contains(entry.PackageId)) continue;
            string type = WorldTemplate.TypeName(schema, entry.Reference);
            string name = $"{WorldTemplate.Folder}/{destination}/{type}/{tag:x8}_local.{type}";
            if (world.Files.All(file => file.Name != name))
            {
                var leaf = TftClone.Clone([tag], new CatalogTagSource(catalog), schema, _ => name).Single();
                if (leaf.References.Any(x => x.Target != name)) continue;
                // No source tag: the copy is a new tag, not a stand-in for the installed one.
                world.Add(new TftFile
                {
                    Name = leaf.Name, TagType = leaf.TagType, ClassId = leaf.ClassId, TypeInfo = leaf.TypeInfo, BuildHash = leaf.BuildHash,
                    Layout = leaf.Layout, Data = leaf.Data, References = leaf.References,
                });
            }
            references[row] = reference with { Target = name };
        }
        return WorldDevices.With(clone, clone.Data, references);
    }

    /** Adds squads in order; the bulk form of Add. */
    public static IReadOnlyList<AddedSquad> AddMany(WorldFiles world, RegistryIndex schema, PackageCatalog catalog, string destination, uint template,
        IEnumerable<NewSquad> squads, string? ownerFile = null, CancellationToken token = default) =>
        squads.Select(squad => Add(world, schema, catalog, destination, template, squad, ownerFile, token)).ToList();

    /** The squads this world added, read back from their configs and owners. */
    public static IReadOnlyList<AddedSquad> List(WorldFiles world)
    {
        var owners = world.Files.Where(file => file.ClassId == SAuthoredObject.Class && (file.SourceTag is not null || OwnerTemplate(file) is not null))
            .ToLookup(file => TagView.At<SAuthoredObject>(file.Data, 0).RegistryKey);
        var result = new List<AddedSquad>();
        foreach (var file in world.Files.Where(file => file.ClassId == SEntityConfig.Class && file.SourceTag is null && file.CopyOf is null))
        {
            if (NameOf(file) is not string name || SquadConfig.Read(file) is not { } facts) continue;
            var owner = owners[facts.RegistryKey].SingleOrDefault() ?? throw new InvalidDataException($"No one copied object owns squad {name}.");
            result.Add(new(name, owner.Name, facts.SlotIndex, file.Name, facts.Identity, facts.Position, facts.Count, facts.Entity));
        }
        return result.OrderBy(squad => squad.OwnerFile, StringComparer.Ordinal).ThenBy(squad => squad.SlotIndex).ToList();
    }

    /** The squad name a new config file carries, or null for any other file. */
    internal static string? NameOf(TftFile file)
    {
        if (file.Info is null) return null;
        try { return JsonNode.Parse(file.Info)?[NameKey] is JsonValue value && value.TryGetValue(out string? text) ? text : null; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /**
     * Installed inline spawner configs a new squad can copy: one member with an exact actor class and
     * its own point, in the given scenario, in SDK row order.
     * @param installedPack The activity SDK pack Sunrise generated for the installation.
     */
    public static IReadOnlyList<uint> InlineTemplates(string installedPack, PackageCatalog catalog, uint scenario)
    {
        using var pack = ActivitySdkPack.Open(installedPack);
        int row = Array.FindIndex(pack.Rows<SdkScenario>(SdkSection.Scenarios), item => item.Tag == scenario);
        if (row < 0) return [];
        var members = pack.Rows<SdkSquadMember>(SdkSection.SquadMembers);
        return pack.Rows<SdkSquad>(SdkSection.Squads)
            .Where(squad => squad.ScenarioIndex == row && squad.SpawnRuleConfigTag == squad.SpawnerConfigTag && squad.Members.Count == 1
                && (members[squad.Members.First].Flags & 1) != 0)
            .Select(squad => squad.SpawnerConfigTag).Distinct()
            .Where(tag => catalog.ContainsTag(tag) && SquadConfig.Find(catalog.Read(tag)) is not null).ToList();
    }

    /**
     * Reads a bulk squad list: a JSON array of {name, entity, count, position}.
     * @return The squads; entity "0x80C1B2B2" names an installed entity, any other text a world file.
     */
    public static IReadOnlyList<NewSquad> ParseList(string json)
    {
        var rows = JsonNode.Parse(json) as JsonArray ?? throw new InvalidDataException("A squad list is a JSON array.");
        var result = new List<NewSquad>();
        foreach (var node in rows)
        {
            if (node is not JsonObject row || WorldFiles.Text(row["name"]) is not string name || WorldFiles.Text(row["entity"]) is not string entity
                || row["position"] is not JsonArray { Count: 3 } position)
                throw new InvalidDataException("Each squad needs a name, an entity and a position of three numbers.");
            JsonNode? x = position[0], y = position[1], z = position[2];
            string target = entity.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? TftNames.Installed(uint.Parse(entity.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)) : entity;
            result.Add(new(name, target, row["count"] is { } count ? (int)count : 1, new((float)x!, (float)y!, (float)z!)));
        }
        return result;
    }

    private static void CheckSquad(WorldFiles world, PackageCatalog catalog, NewSquad squad)
    {
        if (string.IsNullOrEmpty(squad.Name) || squad.Name.Length > MaximumName || squad.Name.Any(c => c is < '!' or > '~'))
            throw new InvalidDataException($"\"{squad.Name}\" is not a squad name: 1 to {MaximumName} printable ASCII characters.");
        if (squad.Count is < 1 or > MaximumCount) throw new ArgumentOutOfRangeException(nameof(squad), $"A squad member takes 1 to {MaximumCount} candidates.");
        if (!float.IsFinite(squad.Position.X) || !float.IsFinite(squad.Position.Y) || !float.IsFinite(squad.Position.Z))
            throw new ArgumentOutOfRangeException(nameof(squad), "A squad position is three finite numbers.");
        bool entity = squad.Entity.StartsWith('#')
            ? catalog.TryGetEntry((uint)TftNames.InstalledHash(squad.Entity), out var entry) && entry.Reference == SEntity.Class
            : world.Files.Any(file => file.Name == squad.Entity && file.ClassId == SEntity.Class);
        if (!entity) throw new InvalidDataException($"{squad.Entity} is not an entity of this world or of the installation.");
        if (List(world).Any(added => added.Name == squad.Name)) throw new InvalidDataException($"The world already has a squad named {squad.Name}.");
    }

    /** The first copied object, by file name, with a squad reached through a set in a bubble that has states. */
    private static TftFile DefaultOwner(WorldFiles world, PackageCatalog catalog, CancellationToken token)
    {
        var rows = (JsonArray)WorldFiles.Field(world.Fields(world.Single(SScenario.Class).Name), "bubbles");
        var live = Enumerable.Range(0, rows.Count).Where(row => ((JsonArray)WorldFiles.Field((JsonObject)rows[row]!, "states")).Count > 0)
            .Select(row => (uint)row).ToHashSet();
        foreach (var owner in world.Files.Where(file => file.ClassId == SAuthoredObject.Class && file.SourceTag is not null).ToList())
            if (ComponentSlotCatalog.OwnerSlots(catalog, owner.SourceTag!.Value, token)
                .Any(row => row.SlotType == SquadSlotType && row.Path.Length == 3 && live.Contains(row.BubbleIndex)))
                return owner;
        throw new InvalidDataException("The world has no copied object with a squad in a bubble that has states.");
    }

    /**
     * Moves every slot ref at or past `first` up one in every tag the owner reaches. A ref is the owner's
     * key, then the slot's type and index as u16; descriptors and other configs' slot refs share that shape.
     * A config with no descriptor of its own also counts, so the walk is not the slot catalog's.
     */
    private static void Renumber(WorldFiles world, RegistryIndex schema, PackageCatalog catalog, string destination, string ownerFile,
        uint ownerTag, uint key, ushort[] types, int first, CancellationToken token, int by = 1)
    {
        var parents = Reach(catalog, schema, ownerTag, token);
        var files = new Dictionary<uint, string> { [ownerTag] = ownerFile };
        string Own(uint tag)
        {
            if (files.TryGetValue(tag, out string? known)) return known;
            string name = world.Files.FirstOrDefault(file => file.SourceTag == tag)?.Name ?? WorldDevices.Copy(world, schema, catalog, destination, tag);
            files[tag] = name;
            // Every parent that names the installed tag must name the copy.
            foreach (uint parent in parents[tag]) WorldDevices.Repoint(world, Own(parent), tag, name);
            return name;
        }
        foreach (uint tag in parents.Keys)
        {
            token.ThrowIfCancellationRequested();
            byte[] data = (world.Files.FirstOrDefault(file => file.SourceTag == tag)?.Data ?? catalog.Read(tag)).ToArray();
            var hits = new List<int>();
            for (int at = 0; at + 8 <= data.Length; at += sizeof(uint))
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at)) != key) continue;
                int index = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 6));
                if (index >= first && index < types.Length && BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 4)) == types[index]) hits.Add(at);
            }
            if (hits.Count == 0) continue;
            var file = world[Own(tag)];
            data = file.Data.ToArray();
            foreach (int at in hits)
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at + 6), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 6)) + by));
            world.Replace(WorldDevices.With(file, data, file.References));
        }
        // Configs earlier squads added to this owner name its slots too.
        foreach (var made in world.Files.Where(file => file.ClassId == SEntityConfig.Class && file.SourceTag is null && file.CopyOf is null
            && file.Info is not null && file.Info.Contains("\"squad_", StringComparison.Ordinal)).ToList())
        {
            byte[] data = made.Data.ToArray();
            bool moved = false;
            for (int at = 0; at + 8 <= data.Length; at += sizeof(uint))
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at)) != key) continue;
                int index = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 6));
                if (index < first || index >= types.Length || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 4)) != types[index]) continue;
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at + 6), (ushort)(index + by));
                moved = true;
            }
            if (moved) world.Replace(WorldDevices.With(made, data, made.References));
        }
    }

    /**
     * Every installed set, link and config the owner reaches through its handles and their references,
     * in the owner's package, with the tags that name each one.
     */
    private static Dictionary<uint, HashSet<uint>> Reach(PackageCatalog catalog, RegistryIndex schema, uint ownerTag, CancellationToken token)
    {
        catalog.TryGetEntry(ownerTag, out var ownerEntry);
        var parents = new Dictionary<uint, HashSet<uint>>();
        var pending = new Queue<(uint Tag, uint Parent)>();
        byte[] owner = catalog.Read(ownerTag);
        foreach (ref readonly var block in TagView.Rows(owner, in TagView.At<SAuthoredObject>(owner, 0).EntryFilters, ObjectBubbleBlock.Class))
            foreach (ref readonly var handle in TagView.Rows(owner, in block.Handles, PlacedHandle.Class)) pending.Enqueue((handle.Handle, ownerTag));
        while (pending.TryDequeue(out var next))
        {
            token.ThrowIfCancellationRequested();
            if (!catalog.TryGetEntry(next.Tag, out var entry) || entry.PackageId != ownerEntry!.PackageId
                || entry.Reference is not (SAuthoredObjectSet.Class or SEntityLink.Class or SEntityConfig.Class)) continue;
            if (parents.TryGetValue(next.Tag, out var known)) { known.Add(next.Parent); continue; }
            parents[next.Tag] = [next.Parent];
            var document = SunriseStudio.Tags.Document.TagDocument.Parse(catalog.Read(next.Tag), entry.Reference, schema);
            foreach (var reference in document.References.Where(x => !x.IsNull && x.Kind == SunriseStudio.Tags.Document.TagReferenceKind.TagRef))
                if ((uint)reference.Value != next.Tag) pending.Enqueue(((uint)reference.Value, next.Tag));
        }
        return parents;
    }

    /** True when an installed set names no list and exactly one redirect. */
    private static bool SingleTarget(PackageCatalog catalog, uint set)
    {
        byte[] bytes = catalog.Read(set);
        return TagView.Fits<SAuthoredObjectSet>(bytes, 0) && TagView.At<SAuthoredObjectSet>(bytes, 0).DirectTarget == uint.MaxValue
            && TagView.Rows(bytes, in TagView.At<SAuthoredObjectSet>(bytes, 0).Objects, SAuthoredObjectSetTarget.Class).Length == 1;
    }

    /** The row of the owner's bubble block that starts at `offset`. */
    private static int BlockIndex(byte[] owner, int offset)
    {
        var blocks = TagView.Rows(owner, in TagView.At<SAuthoredObject>(owner, 0).EntryFilters, ObjectBubbleBlock.Class);
        for (int row = 0; row < blocks.Length; row++)
            if (TagView.OffsetOf(owner, in blocks[row]) == offset) return row;
        throw new InvalidDataException($"No bubble block starts at {offset}.");
    }

    private static string Slug(string name) =>
        new(name.ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '_').ToArray());
}
