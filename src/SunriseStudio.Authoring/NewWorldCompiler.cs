using SunriseStudio.Formats.NewWorld;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Authoring;

/**
 * A compiled new map: its additions (the recipe source first, then every entry in key order with explicit entry
 * indices), the root and activity tags, and the allocation they came from.
 */
public sealed record NewWorldBuild(IReadOnlyList<ProjectAssetAddition> Additions, uint MapTag, uint ActivityTag,
    GeneratedAllocation Allocation);

/** Compiles a new-map recipe into project additions in one new package, deterministic from the recipe. */
public static class NewWorldCompiler
{
    /** Installed classes of the tags a new map names in place. */
    private static readonly (uint Tag, uint Class)[] Named =
    [
        (NewWorldComposer.CollisionArchetype, 0x80809C0F), (NewWorldComposer.Surface, 0x80808BCB),
        (MapComponentWriter.NavigationEntity, 0x80809C0F), (MapComponentWriter.PlaceholderNavigationRoot, 0x80808CB6)
    ];
    private static readonly (uint Tag, uint Class)[] Derived =
    [
        (0x80C70CC0, 0x80809C0F), (0x80FEB3EC, 0x80809C36), (0x80C70B90, 0x80809BBB), (0x80C70CBC, 0x80809C54),
        (0x80FEB3EB, 0x80809C54), (0x80C70CBD, 0x80809C54), (0x80C70CBE, 0x80809C54),
        (0x80C01DA0, 0x80809C36), (0x80F9FE17, 0x80809C36), (0x80F9FE14, 0x80809C36)
    ];

    /** Entries one package can hold. */
    public const int PackageMaximumEntries = 8192;

    /** File name of a map's recipe asset. */
    public static string RecipeName(NewWorldRecipe recipe) => recipe.Name + ".newmap.json";

    /** The installed bodies and closures, read by tag; throws when one is missing or has another class. */
    public static NewWorldInstalled ReadInstalled(PackageCatalog catalog)
    {
        foreach (var (tag, type) in Named.Concat(Derived))
            if (!IsInstalled(catalog, tag, out var entry) || entry.Reference != type || entry.Type != 8)
                throw new InvalidDataException($"Installed {tag:X8} is missing or is not class {type:X8}.");
        var profile = StaticArchetypeWriter.Profile(StaticArchetypeWriter.Sources.Select(catalog.ReadOriginal).ToArray());
        var players = ScenarioWriter.PlayerServiceSources.Select(catalog.ReadOriginal).ToArray();
        return new(profile, new(players[0], players[1], players[2]),
            RetailClosure(catalog, NewWorldComposer.CollisionArchetype), RetailClosure(catalog, NewWorldComposer.Surface),
            RetailClosure(catalog, MapComponentWriter.NavigationEntity), RetailClosure(catalog, MapComponentWriter.PlaceholderNavigationRoot));
    }

    private static bool IsInstalled(PackageCatalog catalog, uint tag, out PackageEntry entry) =>
        catalog.TryGetEntry(tag, out entry) && catalog.ContainsInstalledPackage(entry.PackageId);

    /**
     * The package-byte load closure of an installed resource: aligned words that are installed tags, and a GPU
     * entry's reference; type-16 roots stop descent and are left out (World Studio borrowed_environment.retail_closure).
     */
    public static IReadOnlyList<uint> RetailClosure(PackageCatalog catalog, uint start)
    {
        var seen = new HashSet<uint>();
        var pending = new Stack<uint>([start]);
        while (pending.TryPop(out uint tag))
        {
            if (!seen.Add(tag) || !IsInstalled(catalog, tag, out var entry)) continue;
            if (entry.Type is not (8 or 16))
            {
                if (IsInstalled(catalog, entry.Reference, out _)) pending.Push(entry.Reference);
                continue;
            }
            byte[] raw = catalog.ReadOriginal(tag);
            for (int at = 0; at + 4 <= raw.Length; at += 4)
            {
                uint word = BitConverter.ToUInt32(raw, at);
                if (word == tag || !IsInstalled(catalog, word, out var target)) continue;
                if (target.Type == 16 && word != start) continue;
                pending.Push(word);
            }
        }
        return seen.Order().ToArray();
    }

    /** Composes and allocates the map from firstEntry; Group joins every addition to the recipe's group. */
    public static NewWorldBuild Compile(PackageCatalog catalog, NewWorldRecipe recipe, ushort package, int firstEntry)
    {
        if (!PackageWriter.IsNewPackageId(package)) throw new InvalidDataException($"New packages need an id from {PackageWriter.NewPackageIdRange}.");
        if (catalog.ContainsInstalledPackage(package)) throw new InvalidDataException($"Package {package:X4} is installed.");
        return Compile(recipe, ReadInstalled(catalog), package, firstEntry);
    }

    /** The same compile over supplied installed inputs. */
    public static NewWorldBuild Compile(NewWorldRecipe recipe, NewWorldInstalled installed, ushort package, int firstEntry)
    {
        var world = NewWorldComposer.Compose(recipe, installed, NewWorldCollision.Frame);
        var allocation = world.Graph.Allocate(package, firstEntry, world.Roots);
        if (firstEntry + allocation.Entries.Count > PackageMaximumEntries)
            throw new InvalidDataException($"The map needs {allocation.Entries.Count} entries; package {package:X4} has room for "
                + $"{Math.Max(0, PackageMaximumEntries - firstEntry)}.");
        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [NewWorldGraph.WorldKey] = world.MapName,
            [world.Roots[1]] = world.ActivityName,
            [NewWorldGraph.ScenarioKey] = world.ActivityName + ":scenario_client"
        };
        string group = recipe.Name;
        var additions = new List<ProjectAssetAddition>
        {
            new(RecipeName(recipe), recipe.ToJson()) { Group = group }
        };
        foreach (var entry in allocation.Entries)
        {
            var native = new NativeAssetMetadata(package, entry.Reference, entry.TypeInfo, names.GetValueOrDefault(entry.Key), entry.EntryIndex);
            additions.Add(new($"{recipe.Name}.{entry.Key}.bin", entry.Data, native, BaseOf(entry.Key)) { Group = group });
        }
        return new(additions, allocation.Tags[NewWorldGraph.WorldKey], allocation.Tags[world.Roots[1]], allocation);
    }

    /** Copies of installed profile bodies are stored against their original. */
    private static uint? BaseOf(string key)
    {
        int at = key.IndexOf("_st_", StringComparison.Ordinal);
        if (at > 0 && key[0] == 'b')
            return key[(at + 4)..] switch
            {
                "archetype" => 0x80C70CC0,
                "resource" => 0x80FEB3EC,
                "header" => 0x80C70B90,
                "config_0" => 0x80C70CBC,
                "config_1" => 0x80FEB3EB,
                "config_2" => 0x80C70CBD,
                "config_3" => 0x80C70CBE,
                _ => null
            };
        return key switch
        {
            "player_services_config_18" => 0x80C01DA0,
            "player_services_config_17" => 0x80F9FE17,
            "player_services_config_13" => 0x80F9FE14,
            _ => null
        };
    }
}
