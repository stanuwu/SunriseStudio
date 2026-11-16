using System.Text.Json.Nodes;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Tft;

namespace SunriseStudio.World;

public static partial class WorldSquadAuthoring
{
    /**
     * Lists an object in the tag sets of one bubble's states and in no other. A tag set or state entry
     * that another bubble also uses is copied for this bubble first, so no other bubble changes.
     * @param objectFile An authored object a tag set already lists; it keeps its list.
     * @param bubbleRow The scenario row whose states load the object.
     */
    public static void LoadOnlyIn(WorldFiles world, string objectFile, int bubbleRow)
    {
        var scenario = world.Single(SScenario.Class).Name;
        var rows = (JsonArray)WorldFiles.Field(world.Fields(scenario), "bubbles");
        if (bubbleRow < 0 || bubbleRow >= rows.Count || ((JsonArray)WorldFiles.Field((JsonObject)rows[bubbleRow]!, "states")).Count == 0)
            throw new InvalidDataException($"Scenario row {bubbleRow} has no states to load {objectFile}.");
        var sets = world.Files.Where(file => file.ClassId == SScenarioTagSet.Class).Select(file => file.Name).ToList();
        string list = sets.SelectMany(set => world.Fields(set).Where(pair => pair.Value is JsonArray items
                && items.OfType<JsonObject>().Any(item => WorldFiles.Text(item["tag"]) == objectFile)).Select(pair => pair.Key))
            .FirstOrDefault() ?? throw new InvalidDataException($"No scenario tag set lists {objectFile}.");
        foreach (string set in sets.Where(set => world[set].References.Any(reference => reference.Target == objectFile)))
            world.Edit(set, (_, fields) =>
            {
                foreach (var items in fields.Select(pair => pair.Value).OfType<JsonArray>())
                    foreach (var item in items.OfType<JsonObject>().Where(item => WorldFiles.Text(item["tag"]) == objectFile).ToList()) items.Remove(item);
            });

        // Which bubbles use each entry and each tag set; a copy made below serves this bubble alone.
        var entryUsers = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var entries = new List<(int Bubble, int State, string Entry)>();
        for (int bubble = 0; bubble < rows.Count; bubble++)
        {
            var states = (JsonArray)WorldFiles.Field((JsonObject)rows[bubble]!, "states");
            for (int state = 0; state < states.Count; state++)
            {
                string entry = WorldFiles.Text(WorldFiles.Field((JsonObject)states[state]!, "entry"))!;
                entries.Add((bubble, state, entry));
                if (!entryUsers.TryGetValue(entry, out var users)) entryUsers[entry] = users = [];
                users.Add(bubble);
            }
        }
        string? LinkOf(string entry) => world.Contains(entry) ? WorldFiles.Text(world.Fields(entry)["link"]) : null;
        var setUsers = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var (bubble, _, entry) in entries)
            if (LinkOf(entry) is string set)
            {
                if (!setUsers.TryGetValue(set, out var users)) setUsers[set] = users = [];
                users.Add(bubble);
            }

        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, state, entry) in entries.Where(item => item.Bubble == bubbleRow).ToList())
        {
            if (!world.Contains(entry)) throw new InvalidDataException($"State {state} of row {bubbleRow} names {entry}, which the world does not hold.");
            string own = entry;
            if (entryUsers[entry].Any(user => user != bubbleRow))
            {
                own = CopyFor(world, entry, bubbleRow);
                int at = state;
                world.Edit(scenario, (_, fields) =>
                    ((JsonObject)((JsonArray)WorldFiles.Field((JsonObject)((JsonArray)WorldFiles.Field(fields, "bubbles"))[bubbleRow]!, "states"))[at]!)["entry"] = own);
            }
            string set = LinkOf(own) ?? throw new InvalidDataException($"{own} names no tag set.");
            if (setUsers[set].Any(user => user != bubbleRow))
            {
                string copy = CopyFor(world, set, bubbleRow);
                world.Edit(own, (_, fields) => fields["link"] = copy);
                set = copy;
            }
            if (listed.Add(set)) world.Edit(set, (_, fields) => ((JsonArray)WorldFiles.Field(fields, list)).Add(new JsonObject { ["tag"] = objectFile }));
        }
    }

    /** Each scenario state's tag set file, by scenario row and state; null when the world does not hold the entry or the set. */
    public static IReadOnlyList<(int Bubble, int State, string? Registry)> StateRegistries(WorldFiles world)
    {
        var rows = (JsonArray)WorldFiles.Field(world.Fields(world.Single(SScenario.Class).Name), "bubbles");
        var output = new List<(int, int, string?)>();
        for (int bubble = 0; bubble < rows.Count; bubble++)
        {
            var states = (JsonArray)WorldFiles.Field((JsonObject)rows[bubble]!, "states");
            for (int state = 0; state < states.Count; state++)
            {
                string entry = WorldFiles.Text(WorldFiles.Field((JsonObject)states[state]!, "entry"))!;
                string? set = world.Contains(entry) ? WorldFiles.Text(world.Fields(entry)["link"]) : null;
                output.Add((bubble, state, set is not null && world.Contains(set) ? set : null));
            }
        }
        return output;
    }

    /** Every state whose tag set lists the object, with the object's row in that list. */
    public static IReadOnlyList<(int Bubble, int State, int Ordinal)> ListedIn(WorldFiles world, string objectFile)
    {
        var output = new List<(int, int, int)>();
        var rows = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var (bubble, state, set) in StateRegistries(world))
        {
            if (set is null) continue;
            if (!rows.TryGetValue(set, out var found))
            {
                rows[set] = found = [];
                if (world[set].References.Any(reference => reference.Target == objectFile))
                    foreach (var items in world.Fields(set).Select(pair => pair.Value).OfType<JsonArray>())
                        for (int row = 0; row < items.Count; row++)
                            if (items[row] is JsonObject item && WorldFiles.Text(item["tag"]) == objectFile) found.Add(row);
            }
            output.AddRange(found.Select(row => (bubble, state, row)));
        }
        return output;
    }

    /** The bubble's own copy of a file: STEM_bubbleROW, made once and named as a copy of its source tag. */
    private static string CopyFor(WorldFiles world, string file, int bubbleRow)
    {
        int dot = file.LastIndexOf('.');
        string name = $"{file[..dot]}_bubble{bubbleRow}{file[dot..]}";
        if (world.Contains(name)) return name;
        var source = world[file];
        var json = TftText.ToJson(source, out string? fallback);
        if (fallback is not null) throw new InvalidDataException($"{file} has no field view ({fallback}).");
        json["name"] = name;
        var made = TftText.FromJson(json);
        uint? tag = source.SourceTag ?? source.CopyOf;
        world.Add(new TftFile
        {
            Name = made.Name, TagType = made.TagType, ClassId = made.ClassId, TypeInfo = made.TypeInfo, BuildHash = made.BuildHash,
            Layout = made.Layout, Data = made.Data, References = made.References, Meta = source.Meta,
            Info = tag is uint copied ? TftFile.CopyInfo(copied) : source.Info,
        });
        return name;
    }
}
