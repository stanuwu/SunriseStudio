using System.Text.Json.Nodes;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Tft;

namespace SunriseStudio.World.Sdk;

/**
 * The activity SDK rows the server needs for a project destination's roster: the source scenario's
 * rows, cut to the kept bubbles, in their positions, moved to the project's tags. Object keys stay.
 */
public static class ModActivitySdk
{
    /** The pack a mod installs beside its packages. */
    public const string FileName = "activity_sdk.pack";
    /** A bubble's slice sets start at 8 * its row in the scenario. */
    private const uint SliceSetsPerBubble = 8;

    /**
     * Builds the pack for every destination in the project.
     * @param builtTags The tag each compiled `.tft` file got, by file name.
     * @return The pack image, or null when the project has no destination.
     */
    public static byte[]? Build(string contentDirectory, RegistryIndex schema, IReadOnlyDictionary<string, uint> builtTags,
        string installedPack)
    {
        var destinations = WorldTemplate.ProjectDestinations(contentDirectory, schema);
        if (destinations.Count == 0) return null;
        using var pack = ActivitySdkPack.Open(installedPack);
        var source = new Source(pack);
        var tables = new ActivitySdkTables();
        foreach (string destination in destinations)
            Add(tables, source, Destination.Read(contentDirectory, destination, builtTags));
        return ActivitySdkPackWriter.Write(pack.Strides, tables);
    }

    private static void Add(ActivitySdkTables tables, Source source, Destination world)
    {
        int sourceScenario = Array.FindIndex(source.Scenarios, row => row.Tag == world.SourceScenario);
        if (sourceScenario < 0) throw new InvalidDataException($"The installed activity SDK has no scenario 0x{world.SourceScenario:X8}.");
        var from = source.Scenarios[sourceScenario];
        uint scenarioRow = (uint)tables.Scenarios.Count;
        uint firstBubble = (uint)tables.Bubbles.Count, firstState = (uint)tables.States.Count, firstOccurrence = (uint)tables.Occurrences.Count;
        var objectRows = new Dictionary<uint, uint>();

        for (int ordinal = 0; ordinal < world.Bubbles.Count; ordinal++)
        {
            // Rows are positional and a copy renames its bubbles, so row k comes from the source's row k.
            uint sourceBubble = from.Bubbles.First + (uint)ordinal;
            if ((uint)ordinal >= from.Bubbles.Count || source.Bubbles[sourceBubble].BubbleOrdinal != (uint)ordinal)
                throw new InvalidDataException($"Row {ordinal} of {world.Activity} has no installed scenario row.");
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
                state.RegistryTag = world.Tag(state.RegistryTag);
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
                    occurrence.ObjectIndex = ObjectRow(tables, source, world, objectRows, occurrence.ObjectIndex);
                    tables.Occurrences.Add(occurrence);
                }
            }
            tables.Bubbles.Add(new SdkBubble
            {
                Id = tables.Text($"bubble/{world.Scenario:x8}/{ordinal:x4}/{nameHash:x8}"),
                Name = tables.Text(source.Pack.Text(bubble.Name)),
                ScenarioIndex = scenarioRow,
                BubbleOrdinal = (uint)ordinal,
                NameHash = nameHash,
                States = new() { First = statesFirst, Count = (uint)tables.States.Count - statesFirst },
            });
        }

        tables.Scenarios.Add(new SdkScenario
        {
            Tag = world.Scenario,
            Id = tables.Text($"scenario/{world.Scenario:x8}"),
            Name = tables.Text(world.Activity),
            Bubbles = new() { First = firstBubble, Count = (uint)tables.Bubbles.Count - firstBubble },
            States = new() { First = firstState, Count = (uint)tables.States.Count - firstState },
            Occurrences = new() { First = firstOccurrence, Count = (uint)tables.Occurrences.Count - firstOccurrence },
        });
        // Sunrise numbers mod activity rows at load, so the row is found by its name, not an index.
        tables.Activities.Add(new SdkActivity
        {
            ActivityIndex = ActivitySdkFormat.AbsentIndex,
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
    private static uint ObjectRow(ActivitySdkTables tables, Source source, Destination world, Dictionary<uint, uint> rows, uint sourceRow)
    {
        if (rows.TryGetValue(sourceRow, out uint known)) return known;
        uint row = (uint)tables.Objects.Count;
        var item = source.Objects[sourceRow];
        uint firstSlot = (uint)tables.Slots.Count;
        for (uint slotRow = item.Slots.First; slotRow < item.Slots.First + item.Slots.Count; slotRow++)
        {
            var slot = source.Slots[slotRow];
            uint newSlot = (uint)tables.Slots.Count;
            slot.Id = tables.Text(source.Pack.Text(slot.Id));
            slot.Name = tables.Text(source.Pack.Text(slot.Name));
            slot.SenseSchemaId = tables.Text(source.Pack.Text(slot.SenseSchemaId));
            slot.AuthSchemaId = tables.Text(source.Pack.Text(slot.AuthSchemaId));
            slot.ObjectIndex = row;
            // Aliases and capabilities name rows the mod pack does not carry.
            slot.Aliases = default;
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
        item.Id = tables.Text($"object/{world.Tag(item.ObjectTag):x8}");
        item.ObjectTag = world.Tag(item.ObjectTag);
        item.Slots = new() { First = firstSlot, Count = (uint)tables.Slots.Count - firstSlot };
        tables.Objects.Add(item);
        rows.Add(sourceRow, row);
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
        private readonly ILookup<uint, SdkAuthoredSceneResource> scenes =
            pack.Rows<SdkAuthoredSceneResource>(SdkSection.AuthoredSceneResources).ToLookup(x => x.SlotIndex);

        public IEnumerable<uint> OccurrencesOf(SdkScenario scenario, uint state) =>
            Enumerable.Range((int)scenario.Occurrences.First, (int)scenario.Occurrences.Count).Select(x => (uint)x)
                .Where(row => Occurrences[row].StateIndex == state);

        public IEnumerable<SdkAuthoredSceneResource> ScenesOf(uint slot) => scenes[slot];
    }

    /** One project destination: its scenario, activity root and kept bubbles, and its tag moves. */
    private sealed class Destination
    {
        private readonly Dictionary<uint, uint> moved = [];
        public uint SourceScenario { get; private init; }
        public uint Scenario { get; private init; }
        public uint RootTag { get; private init; }
        public string Activity { get; private init; } = "";
        /** Every scenario row in order, and whether the cut kept its states. */
        public IReadOnlyList<(uint NameHash, bool Kept)> Bubbles { get; private init; } = [];

        /** A copied tag's project tag; a tag the copy did not take stays installed. */
        public uint Tag(uint installed) => moved.TryGetValue(installed, out uint tag) ? tag : installed;

        public static Destination Read(string contentDirectory, string name, IReadOnlyDictionary<string, uint> builtTags)
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
                Bubbles = rows.OfType<JsonObject>().Select(row => (WorldFiles.Hash(WorldFiles.Field(row, "name_hash")),
                    ((JsonArray)WorldFiles.Field(row, "states")).Count > 0)).ToArray(),
            };
            foreach (var (from, to) in moves) destination.moved[from] = to;
            return destination;
        }
    }
}
