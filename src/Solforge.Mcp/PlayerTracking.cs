/**
 * File: Player position model and sources: PlayerFix readings, yaw/facing helpers, recorder-line parsing and the
 * breadcrumb PlayerTrail. Used by SolforgeTools.Player.cs and the editor player overlay.
 */
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/**
 * One reading of where the player is, from whichever source answered. Positions are game world space (Z up), the
 * same space Solforge draws maps in. Yaw is degrees, atan2(dy, dx), as the recorder measures it.
 */
public sealed record PlayerFix(string Source, Vector3 Position, float? Yaw, DateTimeOffset Time)
{
    /** The Live link's map definition tag. */
    public uint? MapDefinition { get; init; }
    /** The server /coop room: FNV-1a of a custom room text or of the slice-set index; it names no activity or map. */
    public string Room { get; init; } = "";
    public string Activity { get; init; } = "";
    public string Bubble { get; init; } = "";
    public string Slice { get; init; } = "";
    public string? Player { get; init; }
}

/** Parsers for the player sources Solforge falls back to when the Live link is not attached. */
public static class PlayerSources
{
    /** Fallback only: the first-person camera is taken to sit this far above the feet (not measured). */
    public const float CameraHeight = 1.7f;
    /** A third-person camera stays within a few metres of the body; farther means pos is frozen. */
    public const float ThirdPersonReach = 8f;

    /** Yaw in degrees from a forward vector (atan2 of y over x). */
    public static float YawFromForward(Vector3 forward) => MathF.Atan2(forward.Y, forward.X) * 180 / MathF.PI;

    /** Unit facing vector (Z = 0) for a yaw in degrees. */
    public static Vector3 Facing(float yaw) => new(MathF.Cos(yaw * MathF.PI / 180), MathF.Sin(yaw * MathF.PI / 180), 0);

    /** Recorder overrides shared with testkit, followed by the client install and the open game's recorder folders. */
    public static IEnumerable<string> RecorderRoots(string? gameRoot)
    {
        if (Environment.GetEnvironmentVariable("SOLFORGE_RECORDER") is { Length: > 0 } custom) yield return custom;
        if (Environment.GetEnvironmentVariable("SOL_RECORDER_ROOT") is { Length: > 0 } recorder) yield return recorder;
        if (Environment.GetEnvironmentVariable("SOL_CLIENT_DIR") is { Length: > 0 } client)
            yield return Path.Combine(client, "Sunrise", "logs", "recorder");
        if (string.IsNullOrWhiteSpace(gameRoot)) yield break;
        yield return Path.Combine(gameRoot, "bin", "x64", "Sunrise", "logs", "recorder");
        yield return Path.Combine(gameRoot, "Sunrise", "logs", "recorder");
    }

    /** Selects the most recently written recorder file across roots, including a menu snapshot that clears an old position. */
    public static string? LatestRecorderRoot(IEnumerable<string> roots) => roots.Distinct(StringComparer.OrdinalIgnoreCase)
        .Where(Directory.Exists)
        .SelectMany(root => new DirectoryInfo(root).EnumerateDirectories()
            .Select(session => new FileInfo(Path.Combine(session.FullName, "snapshots.jsonl")))
            .Where(file => file.Exists).Select(file => (Root: root, File: file)))
        .OrderByDescending(row => row.File.LastWriteTimeUtc).Select(row => row.Root).FirstOrDefault();

    /**
     * One recorder snapshot line. pos is player::position::snapshot(), the physics body position, the same map
     * space as /coop x,y,z, package placements (combatant spawn= vs plr_dist= in the client log trilaterate to one
     * point within 0.01 m) and Solforge's Z-up world. pos wins; cam (lowered to the feet) is used only when pos is
     * missing, stale, flagged invalid, or farther than a third-person camera can be (an old client froze pos at
     * spawn). Menus, loads and marks give null.
     */
    /**
     * The recorder's loc.activity is the scenario's named-tag name (DestinationSelection.packageName); the client
     * drops a ":scenario_client" suffix. True when a package named tag is that scenario.
     */
    public static bool IsScenario(string? namedTag, string activity)
    {
        if (string.IsNullOrEmpty(namedTag) || activity.Length == 0 || activity == "unknown") return false;
        const string suffix = ":scenario_client";
        string name = namedTag.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? namedTag[..^suffix.Length] : namedTag;
        return string.Equals(name, activity, StringComparison.OrdinalIgnoreCase);
    }

    /** Parses one recorder snapshot line into a PlayerFix; null when it does not hold a position. */
    public static PlayerFix? ParseRecorder(string line, DateTimeOffset fallbackTime)
    {
        JsonObject? snap;
        try { snap = JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return null; }
        if (snap is null || snap["mark"] is not null) return null;
        if (snap["ui"]?["in_world"] is JsonValue world && world.TryGetValue(out bool inWorld) && !inWorld) return null;
        Vector3? pos = Vector(snap["pos"]), cam = Vector(snap["cam"]);
        Vector3 at;
        bool invalid = snap["player"]?["pos_valid"] is JsonValue valid && valid.TryGetValue(out bool ok) && !ok;
        if (cam is { } c && (pos is not { } p0 || invalid || Number(snap["pos_stale_ms"]) is > 0 || Vector3.Distance(p0, c) > ThirdPersonReach))
            at = c - new Vector3(0, 0, CameraHeight);
        else if (pos is { } p) at = p;
        else return null;
        var time = snap["utc"] is JsonValue utc && utc.TryGetValue(out string? stamp)
            && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : fallbackTime;
        var loc = snap["loc"] as JsonObject;
        return new PlayerFix("recorder", at, Number(snap["yaw"]), time)
        {
            Activity = Text(loc?["activity"]), Bubble = Text(loc?["bubble"]), Slice = Text(loc?["slice"])
        };
    }

    /** The newest session's last snapshot; reads only the file's tail. Null when there is none or it is not in a world. */
    public static PlayerFix? LatestRecorder(string root)
    {
        if (!Directory.Exists(root)) return null;
        string? file = new DirectoryInfo(root).EnumerateDirectories()
            .Select(folder => new FileInfo(Path.Combine(folder.FullName, "snapshots.jsonl")))
            .Where(snapshot => snapshot.Exists).OrderByDescending(snapshot => snapshot.LastWriteTimeUtc)
            .Select(snapshot => snapshot.FullName).FirstOrDefault();
        if (file is null || !File.Exists(file)) return null;
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, stream.Length - 16384);
        stream.Seek(start, SeekOrigin.Begin);
        var bytes = new byte[stream.Length - start];
        stream.ReadExactly(bytes);
        var lines = Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // The newest snapshot decides: an older position while the player sits in a menu would be stale.
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (!lines[i].StartsWith('{') || lines[i].Contains("\"mark\"", StringComparison.Ordinal)) continue;
            return ParseRecorder(lines[i], File.GetLastWriteTimeUtc(file));
        }
        return null;
    }

    /** The server admin's /coop page: every joined player with name, room (not a map identity) and position. */
    public static IReadOnlyList<PlayerFix> ParseCoop(string json, DateTimeOffset time)
    {
        var rows = new List<PlayerFix>();
        JsonObject? page;
        try { page = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return rows; }
        if (page?["players"] is not JsonArray players) return rows;
        foreach (var node in players)
        {
            if (node is not JsonObject row || Number(row["x"]) is not { } x || Number(row["y"]) is not { } y || Number(row["z"]) is not { } z) continue;
            if (x == 0 && y == 0 && z == 0) continue;
            rows.Add(new PlayerFix("server", new Vector3(x, y, z), null, time)
            {
                Player = Text(row["name"]), Room = Text(row["room"])
            });
        }
        return rows;
    }

    /** A JSON array of three numbers, or null. */
    public static Vector3? Vector(JsonNode? node) =>
        node is JsonArray { Count: >= 3 } array && Number(array[0]) is { } x && Number(array[1]) is { } y && Number(array[2]) is { } z
            ? new Vector3(x, y, z) : null;

    /** A vector as a JSON array of rounded numbers. */
    public static JsonArray Json(Vector3 value) => new(Round(value.X), Round(value.Y), Round(value.Z));

    /** Rounds to two places. */
    public static double Round(float value) => Math.Round(value, 2);

    /** Any JSON number, including whole numbers built in-process (a JsonValue<int> refuses TryGetValue<double>). */
    private static float? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        double number = value.TryGetValue(out double d) ? d : value.TryGetValue(out float f) ? f : value.TryGetValue(out long l) ? l
            : value.TryGetValue(out int i) ? i : double.NaN;
        return double.IsFinite(number) ? (float)number : null;
    }

    /** A JSON node as text, or empty. */
    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : "";
}

/** The breadcrumb trail: points at least `spacing` apart, the oldest dropped past `capacity`, cleared by a teleport. */
public sealed class PlayerTrail(int capacity = 240, float spacing = .75f, float teleport = 150)
{
    private readonly List<Vector3> points = [];
    public IReadOnlyList<Vector3> Points => points;
    public int Revision { get; private set; }

    /** Adds a point if it is far enough from the last one, dropping the oldest past capacity. */
    public void Add(Vector3 point)
    {
        if (points.Count > 0)
        {
            float distance = Vector3.Distance(points[^1], point);
            if (distance < spacing) return;
            if (distance > teleport) points.Clear();
        }
        points.Add(point);
        if (points.Count > capacity) points.RemoveAt(0);
        Revision++;
    }

    /** Empties the trail. */
    public void Clear() { if (points.Count == 0) return; points.Clear(); Revision++; }
}
