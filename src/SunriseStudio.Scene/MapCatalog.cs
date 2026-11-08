using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

public sealed record MapEntry(uint Tag, string Name, IReadOnlyList<uint> BubbleHashes)
{
    public uint DefinitionTag { get; init; }
}
public sealed record MapActivity(uint Tag, string Name, IReadOnlyList<uint> BubbleHashes, bool ExactMatch);

/** Maps and the scenarios that place content on them, matched by authored bubble name hashes. */
public sealed class MapCatalog
{
    private readonly List<(uint Tag, string Name, uint[] Bubbles)> scenarios = [];

    public IReadOnlyList<MapEntry> Maps { get; }
    public int ScenarioCount => scenarios.Count;
    public List<string> Errors { get; } = [];

    private MapCatalog(PackageCatalog catalog, Func<uint, string> name, CancellationToken token)
    {
        var maps = new List<MapEntry>();
        foreach (var entry in catalog.EntriesWithAdded())
        {
            token.ThrowIfCancellationRequested();
            if (entry.Reference == SMapRoot.Class)
            {
                try { maps.Add(ReadMap(catalog, entry.TagHash, name(entry.TagHash))); }
                catch (Exception error) when (error is InvalidDataException or ArgumentException or IOException)
                { Errors.Add($"Map 0x{entry.TagHash:X8}: {error.Message}"); maps.Add(new MapEntry(entry.TagHash, name(entry.TagHash), [])); }
            }
            else if (entry.Reference == SScenario.Class)
            {
                try { scenarios.Add((entry.TagHash, name(entry.TagHash), ScenarioBubbles(catalog.Read(entry.TagHash)))); }
                catch (Exception error) when (error is InvalidDataException or ArgumentException or IOException)
                { Errors.Add($"Scenario 0x{entry.TagHash:X8}: {error.Message}"); }
            }
        }
        Maps = maps.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static MapCatalog Build(PackageCatalog catalog, Func<uint, string> name, CancellationToken token = default) =>
        new(catalog, name, token);

    /** Bubble name hashes in scenario order. */
    private static uint[] ScenarioBubbles(byte[] tag)
    {
        ref readonly var scenario = ref TagView.Checked<SScenario>(tag, 0);
        var rows = TagView.Rows(tag, in scenario.Bubbles, ScenarioBubble.Class);
        var hashes = new uint[rows.Length];
        for (int index = 0; index < rows.Length; index++) hashes[index] = rows[index].NameHash;
        return hashes;
    }

    /** Bubble name hashes in map order, from the map definition's bubble rows. */
    private static MapEntry ReadMap(PackageCatalog catalog, uint mapTag, string name)
    {
        uint definitionTag = TagView.Checked<SMapRoot>(catalog.Read(mapTag), 0).MapDefinition;
        if (!catalog.TryGetEntry(definitionTag, out var definitionEntry) || definitionEntry.Reference != SMapLocationNames.Class)
            throw new InvalidDataException("Map definition has the wrong class.");
        byte[] definition = catalog.Read(definitionTag);
        ref readonly var map = ref TagView.Checked<SMapLocationNames>(definition, 0);
        var rows = TagView.Rows(definition, in map.SliceSets, MapBubble.Class);
        var hashes = new uint[rows.Length];
        for (int index = 0; index < rows.Length; index++) hashes[index] = rows[index].BubbleNameHash;
        return new(mapTag, name, hashes) { DefinitionTag = definitionTag };
    }

    /** Scenarios whose bubbles all belong to the map; an exact bubble list match sorts first. */
    public IReadOnlyList<MapActivity> ActivitiesFor(uint mapTag)
    {
        var map = Maps.FirstOrDefault(m => m.Tag == mapTag);
        if (map is null || map.BubbleHashes.Count == 0) return [];
        var bubbles = map.BubbleHashes.ToHashSet();
        return scenarios.Where(s => s.Bubbles.Length != 0 && s.Bubbles.All(bubbles.Contains))
            .Select(s => new MapActivity(s.Tag, s.Name, s.Bubbles, s.Bubbles.SequenceEqual(map.BubbleHashes)))
            .OrderByDescending(a => a.ExactMatch).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
