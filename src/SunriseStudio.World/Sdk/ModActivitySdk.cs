using System.Text.Json.Nodes;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Tft;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.World.Sdk;

/**
 * The activity SDK rows the server needs for a project destination's roster: the source scenario's
 * rows, cut to the kept bubbles, in their positions, moved to the project's tags. Object keys stay.
 */
public static partial class ModActivitySdk
{
    /** The pack a mod installs beside its packages. */
    public const string FileName = "activity_sdk.pack";
    /** A bubble's slice sets start at 8 * its row in the scenario. */
    private const uint SliceSetsPerBubble = 8;

    /**
     * Builds the pack for every destination in the project.
     * @param builtTags The tag each compiled `.tft` file got, by file name.
     * @param activityTable The project's activity table; each destination's activity row gives its definition hash.
     * @return The pack image, or null when the project has no destination.
     */
    public static byte[]? Build(string contentDirectory, RegistryIndex schema, IReadOnlyDictionary<string, uint> builtTags,
        string installedPack, byte[] activityTable)
    {
        var hashes = ActivityRows.FreeRoam(activityTable).GroupBy(row => row.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Hash, StringComparer.Ordinal);
        var destinations = WorldTemplate.ProjectDestinations(contentDirectory, schema);
        if (destinations.Count == 0) return null;
        using var pack = ActivitySdkPack.Open(installedPack);
        var source = new Source(pack);
        var tables = new ActivitySdkTables();
        foreach (string destination in destinations)
        {
            var world = Destination.Read(contentDirectory, destination, builtTags, source.ActorClasses.Select(row => row.DefinitionTag).ToHashSet());
            // The server keys a mission program by this hash; a destination with no activity yet has none.
            Add(tables, source, world, hashes.GetValueOrDefault(world.Activity));
        }
        AddActorRows(tables, source);
        return ActivitySdkPackWriter.Write(pack.Strides, tables);
    }

    private static void Add(ActivitySdkTables tables, Source source, Destination world, uint definitionHash)
    {
        int sourceScenario = Array.FindIndex(source.Scenarios, row => row.Tag == world.SourceScenario);
        if (sourceScenario < 0) throw new InvalidDataException($"The installed activity SDK has no scenario 0x{world.SourceScenario:X8}.");
        var from = source.Scenarios[sourceScenario];
        uint scenarioRow = (uint)tables.Scenarios.Count;
        uint firstBubble = (uint)tables.Bubbles.Count, firstState = (uint)tables.States.Count, firstOccurrence = (uint)tables.Occurrences.Count;
        var objectRows = new Dictionary<uint, uint>();
        var slotRows = new Dictionary<uint, uint>();
        var occurrenceRows = new Dictionary<uint, uint>();

        for (int ordinal = 0; ordinal < world.Bubbles.Count; ordinal++)
        {
            // Rows are positional and a copy renames its bubbles, so row k comes from the source's row k.
            // An added bubble takes its template's row, moved to its own row and map index.
            var made = world.Added.FirstOrDefault(item => item.Row == ordinal);
            int sourceOrdinal = made?.Template ?? ordinal;
            uint sourceBubble = from.Bubbles.First + (uint)sourceOrdinal;
            if ((uint)sourceOrdinal >= from.Bubbles.Count || source.Bubbles[sourceBubble].BubbleOrdinal != (uint)sourceOrdinal)
                throw new InvalidDataException($"Row {sourceOrdinal} of {world.Activity} has no installed scenario row.");
            var bubble = source.Bubbles[sourceBubble];
            uint nameHash = world.Bubbles[ordinal].NameHash;
            uint bubbleRow = (uint)tables.Bubbles.Count, statesFirst = (uint)tables.States.Count;
            // A dropped bubble keeps its row, with no states, so later rows keep their slice sets.
            uint stateCount = world.Bubbles[ordinal].Kept ? bubble.States.Count : 0;
            for (uint stateRow = bubble.States.First; stateRow < bubble.States.First + stateCount; stateRow++)
            {
                var state = source.States[stateRow];
                if (state.SliceSetIndex != state.EntryIndex * SliceSetsPerBubble)
                    throw new InvalidDataException($"Installed state {stateRow} does not start at its bubble's slice sets.");
                uint newState = (uint)tables.States.Count;
                state.Id = tables.Text($"state/{world.Scenario:x8}/{ordinal:x4}/{state.StateOrdinal:x4}");
                state.EntryId = tables.Text(source.Pack.Text(state.EntryId));
                state.RegistryId = tables.Text(source.Pack.Text(state.RegistryId));
                state.ScenarioIndex = scenarioRow;
                state.BubbleIndex = bubbleRow;
                state.EntryIndex = (uint)ordinal;
                state.SliceSetIndex = (uint)ordinal * SliceSetsPerBubble;
                // A bubble may have its own copy of the template's tag set.
                state.RegistryTag = world.StateRegistries.TryGetValue((ordinal, (int)state.StateOrdinal), out uint registry) ? registry : world.Tag(state.RegistryTag);
                if (made is not null) state.MapBubbleIndex = (uint)made.RegionRow;
                // The flag is the low byte; the project may have made a public template row private.
                if (world.StatePublic.TryGetValue((ordinal, (int)state.StateOrdinal), out byte flag))
                    state.PublicValue = (state.PublicValue & ~0xFFu) | flag;
                tables.States.Add(state);
                foreach (uint occurrenceRow in source.OccurrencesOf(from, stateRow))
                {
                    var occurrence = source.Occurrences[occurrenceRow];
                    occurrence.Id = tables.Text(source.Pack.Text(occurrence.Id));
                    occurrence.ContextRegistryKey = tables.Text(source.Pack.Text(occurrence.ContextRegistryKey));
                    occurrence.RegistryId = tables.Text(source.Pack.Text(occurrence.RegistryId));
                    occurrence.EntryId = tables.Text(source.Pack.Text(occurrence.EntryId));
                    occurrence.ScenarioIndex = scenarioRow;
                    occurrence.BubbleIndex = bubbleRow;
                    occurrence.StateIndex = newState;
                    occurrence.ObjectIndex = ObjectRow(tables, source, world, objectRows, slotRows, occurrence.ObjectIndex);
                    // A squad stays on its template's own occurrence, not on an added bubble's copy.
                    if (made is null) occurrenceRows[occurrenceRow] = (uint)tables.Occurrences.Count;
                    tables.Occurrences.Add(occurrence);
                }
            }
            tables.Bubbles.Add(new SdkBubble
            {
                Id = tables.Text($"bubble/{world.Scenario:x8}/{ordinal:x4}/{nameHash:x8}"),
                Name = tables.Text(made?.Name ?? source.Pack.Text(bubble.Name)),
                ScenarioIndex = scenarioRow,
                BubbleOrdinal = (uint)ordinal,
                NameHash = nameHash,
                States = new() { First = statesFirst, Count = (uint)tables.States.Count - statesFirst },
            });
        }

        AddMadeOwners(tables, source, world, scenarioRow, objectRows);
        tables.Scenarios.Add(new SdkScenario
        {
            Tag = world.Scenario,
            Id = tables.Text($"scenario/{world.Scenario:x8}"),
            Name = tables.Text(world.Activity),
            Bubbles = new() { First = firstBubble, Count = (uint)tables.Bubbles.Count - firstBubble },
            States = new() { First = firstState, Count = (uint)tables.States.Count - firstState },
            Occurrences = new() { First = firstOccurrence, Count = (uint)tables.Occurrences.Count - firstOccurrence },
        });
        AddSquads(tables, source, world, (uint)sourceScenario, scenarioRow, objectRows, slotRows, occurrenceRows);
        AddNewSquads(tables, source, world, scenarioRow);
        AddSlotRows(tables, source, slotRows);
        foreach (var row in world.MadeGroupRows) tables.Words(SdkSection.CombatObjectiveGroups).AddRange(row);
        // Sunrise numbers mod activity rows at load, so the row is found by its name, not an index.
        tables.Activities.Add(new SdkActivity
        {
            ActivityIndex = ActivitySdkFormat.AbsentIndex,
            DefinitionHash = definitionHash,
            Id = tables.Text($"act/{world.Activity}"),
            InternalName = tables.Text(world.Activity),
            DisplayName = tables.Text(world.Activity),
            ScenarioIndex = scenarioRow,
            Flags = ActivitySdkFormat.ActivityExactMask,
            SelectedActivityRootTag = world.RootTag,
            SelectedScenarioTag = world.Scenario,
            MatchmakingConfigTag = ActivitySdkFormat.AbsentIndex,
        });
    }

    /** Copies one installed object and its slots the first time a kept state names it. */
    private static uint ObjectRow(ActivitySdkTables tables, Source source, Destination world, Dictionary<uint, uint> rows,
        Dictionary<uint, uint> slotRows, uint sourceRow)
    {
        if (rows.TryGetValue(sourceRow, out uint known)) return known;
        uint row = (uint)tables.Objects.Count;
        var item = source.Objects[sourceRow];
        uint firstSlot = (uint)tables.Slots.Count;
        for (uint slotRow = item.Slots.First; slotRow < item.Slots.First + item.Slots.Count; slotRow++)
        {
            var slot = source.Slots[slotRow];
            slot.SlotIndex = NewSlotsBefore(tables, world, item.ObjectTag, row, slot.SlotIndex);
            uint newSlot = (uint)tables.Slots.Count;
            slotRows[slotRow] = newSlot;
            slot.Id = tables.Text(source.Pack.Text(slot.Id));
            slot.Name = tables.Text(source.Pack.Text(slot.Name));
            slot.SenseSchemaId = tables.Text(source.Pack.Text(slot.SenseSchemaId));
            slot.AuthSchemaId = tables.Text(source.Pack.Text(slot.AuthSchemaId));
            slot.ObjectIndex = row;
            slot.Aliases = AliasRows(tables, source, slot.Aliases);
            // Capabilities name gate and refusal rows the mod pack does not carry.
            slot.Capabilities = default;
            tables.Slots.Add(slot);
            foreach (var resource in source.ScenesOf(slotRow))
                tables.AuthoredSceneResources.Add(resource with
                {
                    Id = tables.Text(source.Pack.Text(resource.Id)),
                    SlotIndex = newSlot,
                    ConfigTag = world.Tag(resource.ConfigTag),
                    ResourceTag = world.Tag(resource.ResourceTag),
                });
        }
        NewSlotsBefore(tables, world, item.ObjectTag, row, uint.MaxValue);
        item.Id = tables.Text($"object/{world.Tag(item.ObjectTag):x8}");
        item.ObjectTag = world.Tag(item.ObjectTag);
        item.Slots = new() { First = firstSlot, Count = (uint)tables.Slots.Count - firstSlot };
        tables.Objects.Add(item);
        rows.Add(sourceRow, row);
        return row;
    }

    /**
     * Copies the source scenario's squads whose object and occurrence the cut kept, with their
     * members, anchors and actor classes. Squad rows stay in scenario order, which the server searches.
     * An anchor whose placement row the world moved takes the row's new position.
     */
    private static void AddSquads(ActivitySdkTables tables, Source source, Destination world, uint sourceScenario, uint scenarioRow,
        Dictionary<uint, uint> objectRows, Dictionary<uint, uint> slotRows, Dictionary<uint, uint> occurrenceRows)
    {
        // A stripped world has only the squads it added.
        if (world.HostStripped) return;
        for (uint row = 0; row < source.Squads.Length; row++)
        {
            var squad = source.Squads[row];
            if (squad.ScenarioIndex != sourceScenario || !objectRows.TryGetValue(squad.ObjectIndex, out uint objectRow)
                || !slotRows.TryGetValue(squad.SlotIndex, out uint slotRow) || !occurrenceRows.TryGetValue(squad.OccurrenceIndex, out uint occurrenceRow))
                continue;
            uint squadRow = (uint)tables.Squads.Count, firstMember = (uint)tables.SquadMembers.Count, firstAnchor = (uint)tables.SquadAnchors.Count;
            // A squad the world pointed at one other enemy takes that enemy's actor class on every member.
            int swapped = world.SquadEntities.TryGetValue(squad.SpawnerConfigTag, out uint entity)
                ? Array.FindIndex(source.ActorClasses, row => row.DefinitionTag == entity) : -1;
            for (uint member = squad.Members.First; member < squad.Members.First + squad.Members.Count; member++)
            {
                var item = source.Members[member];
                item.Id = tables.Text(source.Pack.Text(item.Id));
                item.SquadIndex = squadRow;
                if (swapped >= 0) item.ActorClassIndex = (uint)swapped;
                if (item.ActorClassIndex != ActivitySdkFormat.AbsentIndex) item.ActorClassIndex = ActorClassRow(tables, source, item.ActorClassIndex);
                tables.SquadMembers.Add(item);
            }
            for (uint anchor = squad.Anchors.First; anchor < squad.Anchors.First + squad.Anchors.Count; anchor++)
            {
                var item = source.Anchors[anchor];
                item.Id = tables.Text(source.Pack.Text(item.Id));
                item.SquadIndex = squadRow;
                item.ObjectListTag = world.Tag(item.ObjectListTag);
                if (world.Points.TryGetValue(item.PlacedEntryIdentity, out var point))
                {
                    item.PositionX = BitConverter.SingleToUInt32Bits(point.X);
                    item.PositionY = BitConverter.SingleToUInt32Bits(point.Y);
                    item.PositionZ = BitConverter.SingleToUInt32Bits(point.Z);
                }
                tables.SquadAnchors.Add(item);
            }
            squad.Id = tables.Text(source.Pack.Text(squad.Id));
            squad.ScenarioIndex = scenarioRow;
            squad.ObjectIndex = objectRow;
            squad.SlotIndex = slotRow;
            squad.OccurrenceIndex = occurrenceRow;
            squad.Members = new() { First = firstMember, Count = (uint)tables.SquadMembers.Count - firstMember };
            squad.Anchors = new() { First = firstAnchor, Count = (uint)tables.SquadAnchors.Count - firstAnchor };
            tables.Squads.Add(squad);
        }
    }

    /** Copies one installed actor class once per pack; the server reads its RSAT rows from the installed pack. */
    private static uint ActorClassRow(ActivitySdkTables tables, Source source, uint sourceRow)
    {
        if (source.ActorRows.TryGetValue(sourceRow, out uint known)) return known;
        uint row = (uint)tables.ActorClasses.Count;
        var item = source.ActorClasses[sourceRow];
        item.Id = tables.Text(source.Pack.Text(item.Id));
        item.Descriptors = default;
        tables.ActorClasses.Add(item);
        source.ActorRows.Add(sourceRow, row);
        return row;
    }

    /** The installed rows, read once per build. */
    private sealed class Source(ActivitySdkPack pack)
    {
        public ActivitySdkPack Pack { get; } = pack;
        public SdkScenario[] Scenarios { get; } = pack.Rows<SdkScenario>(SdkSection.Scenarios);
        public SdkBubble[] Bubbles { get; } = pack.Rows<SdkBubble>(SdkSection.Bubbles);
        public SdkState[] States { get; } = pack.Rows<SdkState>(SdkSection.States);
        public SdkOccurrence[] Occurrences { get; } = pack.Rows<SdkOccurrence>(SdkSection.Occurrences);
        public SdkObject[] Objects { get; } = pack.Rows<SdkObject>(SdkSection.Objects);
        public SdkSlot[] Slots { get; } = pack.Rows<SdkSlot>(SdkSection.Slots);
        public SdkSquad[] Squads { get; } = pack.Rows<SdkSquad>(SdkSection.Squads);
        public SdkSquadMember[] Members { get; } = pack.Rows<SdkSquadMember>(SdkSection.SquadMembers);
        public SdkSquadAnchor[] Anchors { get; } = pack.Rows<SdkSquadAnchor>(SdkSection.SquadAnchors);
        public SdkActorClass[] ActorClasses { get; } = pack.Rows<SdkActorClass>(SdkSection.ActorClasses);
        /** Installed actor class row to its row in the pack being built. */
        public Dictionary<uint, uint> ActorRows { get; } = [];
        private readonly Dictionary<SdkSection, (uint[] Words, int RowWords)> words = [];

        /** One installed section as words, read once. */
        public (uint[] Words, int RowWords) Words(SdkSection section) =>
            words.TryGetValue(section, out var known) ? known : words[section] = Pack.Words(section);
        private readonly ILookup<uint, SdkAuthoredSceneResource> scenes =
            pack.Rows<SdkAuthoredSceneResource>(SdkSection.AuthoredSceneResources).ToLookup(x => x.SlotIndex);

        public IEnumerable<uint> OccurrencesOf(SdkScenario scenario, uint state) =>
            Enumerable.Range((int)scenario.Occurrences.First, (int)scenario.Occurrences.Count).Select(x => (uint)x)
                .Where(row => Occurrences[row].StateIndex == state);

        public IEnumerable<SdkAuthoredSceneResource> ScenesOf(uint slot) => scenes[slot];
    }

    /** One project destination: its scenario, activity root and kept bubbles, and its tag moves. */
    private sealed partial class Destination
    {
        private readonly Dictionary<uint, uint> moved = [];
        public uint SourceScenario { get; private init; }
        public uint Scenario { get; private init; }
        public uint RootTag { get; private init; }
        public string Activity { get; private init; } = "";
        /** WorldStrip removed the host content; no installed squad is copied. */
        public bool HostStripped { get; private init; }
        /** Every scenario row in order, and whether the cut kept its states. */
        public IReadOnlyList<(uint NameHash, bool Kept)> Bubbles { get; private init; } = [];
        /** Bubbles WorldBubbles added, each copied from a template row. */
        public IReadOnlyList<WorldBubbles.Added> Added { get; private init; } = [];
        /** Where each placement row of a copied installed list stands now, by placement identity. */
        public Dictionary<ulong, System.Numerics.Vector3> Points { get; } = [];
        /** The one installed entity each copied spawner config names now, by the config's installed tag. */
        public Dictionary<uint, uint> SquadEntities { get; } = [];
        /** Each state's built tag set, by scenario row and state. */
        public Dictionary<(int Bubble, int State), uint> StateRegistries { get; } = [];
        /** Each state's public flag as the project's scenario holds it, by scenario row and state. */
        public Dictionary<(int Bubble, int State), byte> StatePublic { get; } = [];

        /** A copied tag's project tag; a tag the copy did not take stays installed. */
        public uint Tag(uint installed) => moved.TryGetValue(installed, out uint tag) ? tag : installed;

        public static Destination Read(string contentDirectory, string name, IReadOnlyDictionary<string, uint> builtTags, HashSet<uint> actorDefinitions)
        {
            string folder = Path.Combine(contentDirectory, WorldTemplate.Folder, name);
            var files = Directory.EnumerateFiles(folder, "*" + TftNames.FileExtension, SearchOption.AllDirectories)
                .Select(path => TftFile.Read(File.ReadAllBytes(path))).ToList();
            var world = new WorldFiles(files);
            uint Built(TftFile file) => builtTags.TryGetValue(file.Name, out uint tag) ? tag
                : throw new InvalidDataException($"{file.Name} was not built.");
            var moves = new Dictionary<uint, uint>();
            foreach (var file in files)
                if (file.SourceTag is uint from) moves[from] = Built(file);
            var scenario = world.Single(SScenario.Class);
            var root = world.Single(ActivityRoot.Class);
            var rows = (JsonArray)WorldFiles.Field(world.Fields(scenario.Name), "bubbles");
            var destination = new Destination
            {
                SourceScenario = scenario.SourceTag ?? throw new InvalidDataException($"{scenario.Name} names no installed scenario."),
                Scenario = Built(scenario),
                RootTag = Built(root),
                Activity = root.GameName ?? throw new InvalidDataException($"{root.Name} has no game name."),
                HostStripped = WorldStrip.IsStripped(scenario),
                Added = WorldBubbles.List(scenario),
                Bubbles = rows.OfType<JsonObject>().Select(row => (WorldFiles.Hash(WorldFiles.Field(row, "name_hash")),
                    ((JsonArray)WorldFiles.Field(row, "states")).Count > 0)).ToArray(),
            };
            foreach (var (from, to) in moves) destination.moved[from] = to;
            var bubbleRows = rows.OfType<JsonObject>().ToList();
            for (int bubble = 0; bubble < bubbleRows.Count; bubble++)
            {
                var states = ((JsonArray)WorldFiles.Field(bubbleRows[bubble], "states")).OfType<JsonObject>().ToList();
                for (int state = 0; state < states.Count; state++)
                    if (WorldBubbles.Number(states[state]["public_flag"]) is long flag) destination.StatePublic[(bubble, state)] = (byte)flag;
            }
            foreach (var (bubble, state, set) in WorldSquadAuthoring.StateRegistries(world))
                if (set is not null) destination.StateRegistries[(bubble, state)] = Built(world[set]);
            foreach (var list in files.Where(file => file.ClassId == SMapPlacements.Class && file.SourceTag is not null))
                foreach (ref readonly var row in TagView.Rows(list.Data, in TagView.At<SMapPlacements>(list.Data, 0).Placements, AuthoredPlacementEntry.Class))
                    destination.Points[row.PlacementIdentity] = row.Position;
            var copies = files.Where(file => file.CopyOf is not null).ToDictionary(file => file.Name, file => file.CopyOf!.Value, StringComparer.Ordinal);
            foreach (var config in files.Where(file => file.ClassId == SEntityConfig.Class && file.SourceTag is not null))
            {
                // A reference to an entity copy counts as its installed entity; other project files are not enemies.
                var named = config.References.Where(reference => reference.Kind == TftReferenceKind.TagRef)
                    .Select(reference => reference.IsProjectTag ? copies.GetValueOrDefault(reference.Target) : (uint)TftNames.InstalledHash(reference.Target))
                    .Where(tag => tag != 0 && actorDefinitions.Contains(tag)).Distinct().ToList();
                if (named.Count == 1) destination.SquadEntities[config.SourceTag!.Value] = named[0];
            }
            destination.ReadNewSquads(files, Built, copies);
            return destination;
        }
    }
}
