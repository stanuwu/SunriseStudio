using System.Text.Json;
using System.Text.RegularExpressions;

namespace SunriseStudio.World.Import;

public sealed partial class WorldBundleSchema
{
    private const string BundleFile = "bundle.json", BubbleFile = "bubble.json";

    /**
     * Checks a bundle folder: bundle.json and every listed bubble.json against the schema, then the
     * rules that span fields or files, which a JSON Schema cannot state.
     * @return Every failure; empty when the bundle is valid.
     */
    public IReadOnlyList<WorldBundleIssue> CheckBundle(string bundleFolder)
    {
        var issues = new List<WorldBundleIssue>();
        if (Read(Path.Combine(bundleFolder, BundleFile), BundleFile, issues) is not { } bundle) return issues;
        issues.AddRange(Validate(bundle, "bundle", BundleFile, bundleFolder));
        var names = Strings(bundle, "bubbles");
        var bubbles = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        for (int index = 0; index < names.Count; index++)
        {
            string name = names[index], folder = Path.Combine(bundleFolder, name);
            if (!File.Exists(Path.Combine(folder, BubbleFile)))
            {
                issues.Add(new(BundleFile, $"bubbles[{index}]", $"no {BubbleFile} in folder {name}"));
                continue;
            }
            if (Read(Path.Combine(folder, BubbleFile), $"{name}/{BubbleFile}", issues) is not { } bubble || bubbles.ContainsKey(name)) continue;
            bubbles[name] = bubble;
            issues.AddRange(CheckBubbleDocument(bubble, $"{name}/{BubbleFile}", folder, SoundNames(bundle)));
        }
        CheckBundleRules(bundle, names, bubbles, issues);
        return issues;
    }

    /**
     * Checks one bubble folder on its own, as placing a single bubble.json does.
     * @param soundNames The bundle sounds an entity event may name; none for a lone bubble.
     */
    public IReadOnlyList<WorldBundleIssue> CheckBubble(string bubbleFolder, IReadOnlyCollection<string>? soundNames = null)
    {
        var issues = new List<WorldBundleIssue>();
        if (Read(Path.Combine(bubbleFolder, BubbleFile), BubbleFile, issues) is { } bubble)
            issues.AddRange(CheckBubbleDocument(bubble, BubbleFile, bubbleFolder, soundNames ?? []));
        return issues;
    }

    private static JsonElement? Read(string path, string label, List<WorldBundleIssue> issues)
    {
        if (!File.Exists(path)) { issues.Add(new(label, "", "missing")); return null; }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.Clone();
        }
        catch (JsonException error) { issues.Add(new(label, "", $"not valid JSON: {error.Message}")); return null; }
    }

    private IReadOnlyList<WorldBundleIssue> CheckBubbleDocument(JsonElement bubble, string label, string folder, IReadOnlyCollection<string> soundNames)
    {
        var issues = Validate(bubble, "bubble", label, folder).ToList();
        CheckBubbleRules(bubble, label, soundNames, issues);
        return issues;
    }

    /**
     * Far tiles cover listed bubbles with bounds and the first bubble anchors them; far, sound and creature names are
     * unique; a squad names only a bundle creature; a map has a shared space and a landing; door names are unique.
     */
    private static void CheckBundleRules(JsonElement bundle, IReadOnlyList<string> names, IReadOnlyDictionary<string, JsonElement> bubbles,
        List<WorldBundleIssue> issues)
    {
        var far = Objects(bundle, "far");
        for (int index = 0; index < far.Count; index++)
        {
            var covers = Strings(far[index], "covers");
            for (int cover = 0; cover < covers.Count; cover++)
            {
                string path = $"far[{index}].covers[{cover}]";
                if (!names.Contains(covers[cover])) issues.Add(new(BundleFile, path, $"{covers[cover]} is not a listed bubble"));
                else if (bubbles.TryGetValue(covers[cover], out var covered) && !covered.TryGetProperty("bounds", out _))
                    issues.Add(new(BundleFile, path, $"bubble {covers[cover]} has no bounds"));
            }
        }
        if (far.Count > 0 && names.Count > 0 && bubbles.TryGetValue(names[0], out var first) && Objects(first, "spawn_points").Count == 0)
            issues.Add(new($"{names[0]}/{BubbleFile}", "spawn_points", "needs at least 1 item (the first bubble anchors the far tiles)"));
        Unique(far, "name", "far", BundleFile, issues);
        Unique(Objects(bundle, "sounds"), "name", "sounds", BundleFile, issues);
        var creatures = Objects(bundle, "creatures");
        Unique(creatures, "name", "creatures", BundleFile, issues);
        var creatureNames = creatures.Select(item => Text(item, "name")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var (name, bubble) in bubbles)
        {
            var squads = Objects(bubble, "squads");
            for (int index = 0; index < squads.Count; index++)
                if (Text(squads[index], "entity") is { } entity && entity.StartsWith(CreaturePrefix, StringComparison.Ordinal)
                    && !creatureNames.Contains(entity[CreaturePrefix.Length..]))
                    issues.Add(new($"{name}/{BubbleFile}", $"squads[{index}].entity", $"{entity[CreaturePrefix.Length..]} is not a bundle creature"));
        }
        // The map rectangle moves with the shared space, and a landing tells Studio how far it moved.
        if (bundle.TryGetProperty("map", out _))
        {
            if (Text(bundle, "space") != "shared") issues.Add(new(BundleFile, "map", "needs space shared"));
            else if (!bubbles.Values.Any(bubble => Objects(bubble, "landings").Count > 0)) issues.Add(new(BundleFile, "map", "needs a bubble with landings"));
        }
        // A door's slot name is door.<name> in one destination, so names are unique across bubbles.
        var doorNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, bubble) in bubbles)
        {
            var doors = Objects(bubble, "doors");
            for (int index = 0; index < doors.Count; index++)
                if (Text(doors[index], "name") is { } door && !doorNames.Add(door))
                    issues.Add(new($"{name}/{BubbleFile}", $"doors[{index}].name", $"{door} is already a door of this bundle"));
        }
    }

    // A squad names a bundle creature with this prefix.
    private const string CreaturePrefix = "creature:";

    /**
     * Names unique per list, no more LOD distances than LODs, convex volume floors, and event sounds
     * that name a bundle sound or a hex tag.
     */
    private static void CheckBubbleRules(JsonElement bubble, string label, IReadOnlyCollection<string> soundNames, List<WorldBundleIssue> issues)
    {
        var statics = Objects(bubble, "statics");
        // A static without a name takes its glb's file name.
        var staticNames = statics.Select(item => Text(item, "name") ?? (Text(item, "glb") is { } glb ? Path.GetFileNameWithoutExtension(glb) : null)).ToArray();
        UniqueValues(staticNames, "statics", "name", label, issues);
        Unique(Objects(bubble, "lights"), "name", "lights", label, issues);
        var entities = Objects(bubble, "entities");
        Unique(entities, "name", "entities", label, issues);
        // A named spawn point is its own spawn set, so its name is unique and a landing names one.
        var spawns = Objects(bubble, "spawn_points");
        Unique(spawns, "name", "spawn_points", label, issues);
        var landings = Objects(bubble, "landings");
        for (int index = 0; index < landings.Count; index++)
            if (Text(landings[index], "spawn") is { } spawn && !spawns.Any(point => Text(point, "name") == spawn))
                issues.Add(new(label, $"landings[{index}].spawn", $"{spawn} is not a spawn point of this bubble"));
        for (int index = 0; index < statics.Count; index++)
            if (Count(statics[index], "lod_distances") is int distances && distances > (Count(statics[index], "lods") ?? 0))
                issues.Add(new(label, $"statics[{index}].lod_distances", $"has {distances} entries for {Count(statics[index], "lods") ?? 0} lods"));
        var volumes = Objects(bubble, "volumes");
        for (int index = 0; index < volumes.Count; index++)
            if (Corners(volumes[index]) is { } corners && corners.Count >= 3 && !Convex(corners))
                issues.Add(new(label, $"volumes[{index}].corners", "do not go around a convex floor"));
        for (int index = 0; index < entities.Count; index++)
        {
            var events = Objects(entities[index], "events");
            for (int item = 0; item < events.Count; item++)
                if (Text(events[item], "sound") is { } sound && !soundNames.Contains(sound) && !HexTag().IsMatch(sound))
                    issues.Add(new(label, $"entities[{index}].events[{item}].sound", $"{sound} is not a bundle sound or a hex tag"));
        }
    }

    // The reader strips 0x, then parses up to eight hex digits.
    [GeneratedRegex("^(0[xX])?[0-9A-Fa-f]{1,8}$")]
    private static partial Regex HexTag();

    private static IReadOnlyList<JsonElement> Objects(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).ToArray() : [];

    private static IReadOnlyList<string> Strings(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray() : [];

    private static string? Text(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Count(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : null;

    private static IReadOnlyCollection<string> SoundNames(JsonElement bundle) =>
        Objects(bundle, "sounds").Select(item => Text(item, "name")).OfType<string>().ToHashSet(StringComparer.Ordinal);

    private static void Unique(IReadOnlyList<JsonElement> items, string field, string list, string label, List<WorldBundleIssue> issues) =>
        UniqueValues(items.Select(item => Text(item, field)).ToArray(), list, field, label, issues);

    private static void UniqueValues(IReadOnlyList<string?> values, string list, string field, string label, List<WorldBundleIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < values.Count; index++)
            if (values[index] is { } value && !seen.Add(value))
                issues.Add(new(label, $"{list}[{index}].{field}", $"{value} is used twice"));
    }

    private static List<(double X, double Y)>? Corners(JsonElement volume)
    {
        if (!volume.TryGetProperty("corners", out var corners) || corners.ValueKind != JsonValueKind.Array) return null;
        var points = new List<(double X, double Y)>();
        foreach (var point in corners.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() < 2 || point[0].ValueKind != JsonValueKind.Number
                || point[1].ValueKind != JsonValueKind.Number) return null;
            points.Add((point[0].GetDouble(), point[1].GetDouble()));
        }
        return points;
    }

    // Same test as the trigger shape writer: every turn the same way, none straight.
    private static bool Convex(IReadOnlyList<(double X, double Y)> corners)
    {
        int sign = 0;
        for (int index = 0; index < corners.Count; index++)
        {
            var (a, b, c) = (corners[index], corners[(index + 1) % corners.Count], corners[(index + 2) % corners.Count]);
            double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (Math.Abs(cross) < 1e-6) return false;
            int turn = Math.Sign(cross);
            if (sign != 0 && turn != sign) return false;
            sign = turn;
        }
        return true;
    }
}
