using System.Text.Json.Nodes;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.World;

/**
 * Gives a copied world its own names. The client finds the activity root by the name ACTIVITY, the
 * scenario as ACTIVITY:scenario_client and the map root as map:DEVNAME:root.
 */
public static class WorldIdentity
{
    /** Activity and destination names are what an activity row's internal name allows. */
    public const int NameLimit = 39;

    public static void Apply(WorldFiles world, string activity, string destination)
    {
        CheckName(activity, nameof(activity));
        CheckName(destination, nameof(destination));
        var scenario = world.Single(SScenario.Class);
        string sourceActivity = (string)WorldFiles.Field(world.Fields(scenario.Name), "activity_name_hash")!;
        // The activity root repeats the activity's name hash beside its name.
        foreach (var root in world.Files.Where(x => x.ClassId == ActivityRoot.Class).ToList())
            world.Edit(root.Name, (json, fields) =>
            {
                json["game_name"] = activity;
                foreach (var (key, value) in fields.ToList())
                    if (WorldFiles.Text(value) == sourceActivity) fields[key] = WorldFiles.HashText(activity);
            });
        world.Edit(scenario.Name, (json, fields) =>
        {
            uint oldActivity = WorldFiles.Hash(WorldFiles.Field(fields, "activity_name_hash"));
            string oldDestination = (string)WorldFiles.Field(fields, "destination_name_hash")!;
            json["game_name"] = $"{activity}:scenario_client";
            fields["activity_name_hash"] = WorldFiles.HashText(activity);
            fields["dev_name"] = destination;
            // The scenario repeats the destination hash beside the named field.
            foreach (var (key, value) in fields.ToList())
                if (WorldFiles.Text(value) == oldDestination) fields[key] = WorldFiles.HashText(destination);
            // Other activity rows name the source destination's own activities, such as its patrols.
            string oldHash = WorldFiles.HashText(oldActivity);
            var rows = (JsonArray)WorldFiles.Field(fields, "activities");
            var own = rows.OfType<JsonObject>().Where(row => row.Any(x => WorldFiles.Text(x.Value) == oldHash)).ToList();
            if (own.Count != 1) throw new InvalidDataException("The scenario does not list its own activity exactly once.");
            foreach (var (key, value) in own[0].ToList())
                if (WorldFiles.Text(value) is not { } text) continue;
                else if (text == oldHash) own[0][key] = WorldFiles.HashText(activity);
                else if (!text.StartsWith("0x", StringComparison.Ordinal) && !text.StartsWith('#')) own[0][key] = activity;
            rows.Clear();
            rows.Add(own[0].DeepClone());
        });
        world.Edit(world.Single(SMapRoot.Class).Name, (json, _) => json["game_name"] = $"map:{destination}:root");
        world.Edit(world.Single(SMapLocationNames.Class).Name, (_, fields) => fields["map_dev_name"] = destination);
        _ = RenameBubbles(world, destination);
    }

    /** The name a copy's scenario row gets; its hash is the bubble's new name hash. */
    public static string BubbleName(string destination, int row) => $"{destination}_bubble_{row}";

    /**
     * Gives every scenario row the hash of its own name in the scenario row, its states, the bubble
     * parent and the map's slice set. Layer and slot names keep their hashes; a renamed world is left.
     * @return How many bubble hashes changed.
     */
    public static int RenameBubbles(WorldFiles world, string destination)
    {
        var scenario = world.Single(SScenario.Class);
        var mapNames = world.Single(SMapLocationNames.Class);
        var rows = ((JsonArray)WorldFiles.Field(world.Fields(scenario.Name), "bubbles")).OfType<JsonObject>().ToList();
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int row = 0; row < rows.Count; row++)
        {
            string from = (string)WorldFiles.Field(rows[row], "name_hash")!, to = WorldFiles.HashText(BubbleName(destination, row));
            if (from != to) renamed.TryAdd(from, to);
        }
        if (renamed.Count == 0) return 0;
        // A new hash that already names a map row would join two bubbles.
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var set in ((JsonArray)WorldFiles.Field(world.Fields(mapNames.Name), "slice_sets")).OfType<JsonObject>())
            used.Add((string)WorldFiles.Field(set, "bubble_name_hash")!);
        if (renamed.Values.Any(used.Contains) || renamed.Values.Distinct().Count() != renamed.Count)
            throw new InvalidDataException($"A bubble name of {destination} collides with a map row; choose another name.");
        string Rename(JsonNode? value) => renamed.TryGetValue((string)value!, out string? next) ? next : (string)value!;
        world.Edit(scenario.Name, (_, fields) =>
        {
            foreach (var bubble in ((JsonArray)WorldFiles.Field(fields, "bubbles")).OfType<JsonObject>())
            {
                bubble["name_hash"] = Rename(WorldFiles.Field(bubble, "name_hash"));
                foreach (var state in ((JsonArray)WorldFiles.Field(bubble, "states")).OfType<JsonObject>())
                    state["bubble_name_hash"] = Rename(WorldFiles.Field(state, "bubble_name_hash"));
            }
        });
        foreach (var parent in world.Files.Where(file => file.ClassId == SBubbleParent.Class).ToList())
            world.Edit(parent.Name, (_, fields) =>
            {
                var metadata = (JsonObject)WorldFiles.Field(fields, "metadata");
                metadata["bubble_name_hash"] = Rename(WorldFiles.Field(metadata, "bubble_name_hash"));
            });
        world.Edit(mapNames.Name, (_, fields) =>
        {
            foreach (var set in ((JsonArray)WorldFiles.Field(fields, "slice_sets")).OfType<JsonObject>())
                set["bubble_name_hash"] = Rename(WorldFiles.Field(set, "bubble_name_hash"));
        });
        return renamed.Count;
    }

    /** @return Why a name cannot name an activity or a destination, or null when it can. */
    public static string? NameProblem(string name) =>
        name.Length is 0 or > NameLimit || name.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
            ? $"\"{name}\" must be 1 to {NameLimit} lowercase letters, digits and _." : null;

    /** The launcher title a name gets when none is given: its first letter capitalized, _ as spaces. */
    public static string DefaultTitle(string name) => name.Length == 0 ? "" : char.ToUpperInvariant(name[0]) + name[1..].Replace('_', ' ');

    private static void CheckName(string name, string parameter)
    {
        if (NameProblem(name) is { } problem) throw new ArgumentException(problem, parameter);
    }
}
