using SunriseStudio.Packages;

namespace SunriseStudio.Scene;

public sealed record MapEntry(uint Tag, string Name, IReadOnlyList<uint> BubbleHashes)
{
    public uint DefinitionTag { get; init; }
}
public sealed record MapActivity(uint Tag, string Name, IReadOnlyList<uint> BubbleHashes, bool ExactMatch);

/** Maps and the scenarios that place content on them, matched by authored bubble name hashes. */
public sealed class MapCatalog
{
    private const uint MapRootClass = 0x808091DE, MapDefinitionClass = 0x80809962, ScenarioClass = 0x80809994;
    private readonly List<(uint Tag, string Name, uint[] Bubbles)> scenarios = [];

    public IReadOnlyList<MapEntry> Maps { get; }
    public int ScenarioCount => scenarios.Count;
    public List<string> Errors { get; } = [];

    private MapCatalog(PackageCatalog catalog, Func<uint, string> name, CancellationToken token)
    {
        var maps = new List<MapEntry>();
        foreach (var entry in AllEntries(catalog))
        {
            token.ThrowIfCancellationRequested();
            if (entry.Reference == MapRootClass)
            {
                try { maps.Add(ReadMap(catalog, entry.TagHash, name(entry.TagHash))); }
                catch (Exception error) when (error is InvalidDataException or ArgumentException or IOException)
                { Errors.Add($"Map 0x{entry.TagHash:X8}: {error.Message}"); maps.Add(new MapEntry(entry.TagHash, name(entry.TagHash), [])); }
            }
            else if (entry.Reference == ScenarioClass)
            {
                try
                {
                    var data = new TagData(catalog.Read(entry.TagHash));
                    scenarios.Add((entry.TagHash, name(entry.TagHash),
                        data.Rows(0x50, 0x8080924D, 24).Select(row => data.U32(row)).ToArray()));
                }
                catch (Exception error) when (error is InvalidDataException or ArgumentException or IOException)
                { Errors.Add($"Scenario 0x{entry.TagHash:X8}: {error.Message}"); }
            }
        }
        Maps = maps.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static MapCatalog Build(PackageCatalog catalog, Func<uint, string> name, CancellationToken token = default) =>
        new(catalog, name, token);

    /** Installed entries, then the project's added entries, so a new map lists beside the installed ones. */
    public static IEnumerable<PackageEntry> AllEntries(PackageCatalog catalog) => catalog.Entries.Concat(catalog.GetOverlayEntries()
        .Select(overlay => overlay.Entry).Where(entry => !catalog.ContainsInstalledPackage(entry.PackageId)).OrderBy(entry => entry.TagHash));

    /** Bubble name hashes in map order, from the map definition's bubble rows. */
    private static MapEntry ReadMap(PackageCatalog catalog, uint mapTag, string name)
    {
        var root = new TagData(catalog.Read(mapTag));
        uint definitionTag = root.U32(8);
        if (!catalog.TryGetEntry(definitionTag, out var definitionEntry) || definitionEntry.Reference != MapDefinitionClass)
            throw new InvalidDataException("Map definition has the wrong class.");
        var definition = new TagData(catalog.Read(definitionTag));
        return new(mapTag, name, definition.Rows(32, 0x808098C2, 80).Select(row => definition.U32(row)).ToArray())
        { DefinitionTag = definitionTag };
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
