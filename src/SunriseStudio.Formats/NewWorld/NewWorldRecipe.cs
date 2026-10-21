using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SunriseStudio.Formats.NewWorld;

/**
 * One blank bubble: a flat square ground of Width by Depth units centred at Position, its flat material and one
 * arrival point. Positions are native units, Z up.
 */
public sealed record NewWorldBubble(string Id, string Name, double[] Position, double Width, double Depth, string Material,
    double[] Arrival);

/** A walkable link between two bubbles; kept as data, compiled as no crossing. */
public sealed record NewWorldConnection(string From, string To);

/** The authored data a new map compiles from; the same recipe always gives the same bytes. */
public sealed record NewWorldRecipe(string Name, string Title, IReadOnlyList<NewWorldBubble> Bubbles)
{
    public const int MaximumBubbles = 64, MaximumNameLength = 39;
    /** The flat ground colours, as World Studio names them. */
    public static readonly IReadOnlyDictionary<string, double[]> Materials = new Dictionary<string, double[]>(StringComparer.Ordinal)
    {
        ["flat-clay"] = [0.36, 0.42, 0.46],
        ["flat-sand"] = [0.55, 0.43, 0.27],
        ["flat-grass"] = [0.23, 0.34, 0.19],
        ["flat-stone"] = [0.34, 0.32, 0.30]
    };
    private static readonly Regex Identity = new("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<NewWorldConnection>? Connections { get; init; }

    /** The package root name of the map, map:NAME:root. */
    [JsonIgnore] public string MapName => "map:" + Name + ":root";

    /** Bubbles in root-slot order: sorted by id. */
    [JsonIgnore] public IReadOnlyList<NewWorldBubble> Slots => Bubbles.OrderBy(b => b.Id, StringComparer.Ordinal).ToArray();

    /** Throws with a sentence naming the first invalid field. */
    public void Validate()
    {
        if (Name is null || Name.Length is 0 or > MaximumNameLength || !Identity.IsMatch(Name))
            throw new InvalidDataException("The map name is 1 to 39 lowercase letters, digits or underscores, starting with a letter.");
        if (string.IsNullOrWhiteSpace(Title) || Title.Length > 200 || Title.Any(char.IsControl) || Title != Title.Trim())
            throw new InvalidDataException("The world title is one trimmed line of up to 200 characters.");
        if (Bubbles is null || Bubbles.Count is < 1 or > MaximumBubbles) throw new InvalidDataException("A map holds 1 to 64 bubbles.");
        foreach (var bubble in Bubbles)
        {
            if (bubble is null || bubble.Id is null || bubble.Id.Length is 0 or > 32 || !Identity.IsMatch(bubble.Id))
                throw new InvalidDataException("A bubble id is 1 to 32 lowercase letters, digits or underscores.");
            if (string.IsNullOrWhiteSpace(bubble.Name) || bubble.Name.Length > 200 || bubble.Name.Any(char.IsControl) || bubble.Name != bubble.Name.Trim())
                throw new InvalidDataException($"Bubble {bubble.Id} needs a one-line name.");
            if (!Point(bubble.Position, 1e5) || !Point(bubble.Arrival, 1e5))
                throw new InvalidDataException($"Bubble {bubble.Id} needs finite positions.");
            foreach (double size in new[] { bubble.Width, bubble.Depth })
                if (!double.IsFinite(size) || size is <= 0 or > 256 || size * 64 < 1 || Math.Abs(size * 64 - Math.Round(size * 64)) >= 1e-7)
                    throw new InvalidDataException($"Bubble {bubble.Id} ground sides are 1/64 to 256 units in 1/64 steps.");
            if (bubble.Material is null || !Materials.ContainsKey(bubble.Material))
                throw new InvalidDataException($"Bubble {bubble.Id} needs a flat ground colour.");
        }
        if (Bubbles.Select(b => b.Id).Distinct(StringComparer.Ordinal).Count() != Bubbles.Count)
            throw new InvalidDataException("Two bubbles share one id.");
        var hashes = Bubbles.Select(b => MapIdentityWriter.NameHash(b.Id)).Append(MapIdentityWriter.NameHash(Name)).ToArray();
        if (hashes.Distinct().Count() != hashes.Length) throw new InvalidDataException("Bubble name hashes collide.");
        foreach (var link in Connections ?? [])
            if (link is null || !Bubbles.Any(b => b.Id == link.From) || !Bubbles.Any(b => b.Id == link.To) || link.From == link.To)
                throw new InvalidDataException("A connection names two different bubbles of this map.");
    }

    private static bool Point(double[]? value, double limit) =>
        value is { Length: 3 } && value.All(v => double.IsFinite(v) && Math.Abs(v) <= limit);

    public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    public static NewWorldRecipe FromJson(ReadOnlySpan<byte> json)
    {
        NewWorldRecipe? recipe;
        try { recipe = JsonSerializer.Deserialize<NewWorldRecipe>(json, Json); }
        catch (JsonException error) { throw new InvalidDataException("The map recipe is not valid JSON: " + error.Message, error); }
        if (recipe is null) throw new InvalidDataException("The map recipe is empty.");
        recipe.Validate();
        return recipe;
    }

    /**
     * Bubbles in a row along X, each a square ground of the given side with its arrival 0.15 above the centre.
     * @param count 1 to 64 bubbles named Bubble 1, Bubble 2 and so on.
     */
    public static NewWorldRecipe Create(string name, string title, int count, double side, string material)
    {
        var bubbles = Enumerable.Range(0, count).Select(i => new NewWorldBubble($"bubble_{i:D2}", $"Bubble {i + 1}",
            [i * side * 2, 0, 0], side, side, material, [i * side * 2, 0, 0.15])).ToArray();
        var recipe = new NewWorldRecipe(name, title, bubbles);
        recipe.Validate();
        return recipe;
    }
}
