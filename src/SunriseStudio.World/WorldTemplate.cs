using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Graph;
using SunriseStudio.Tags.Tft;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.World;

/** One bubble a template scenario lists; Copyable when its map root row names a bubble record. */
public sealed record WorldBubble(uint NameHash, int SliceSet, bool Copyable);

/** A stock destination's world graph copied into a project as `.tft` files, ready to be cut down and edited. */
public static class WorldTemplate
{
    /** Project folder that holds every world. */
    public const string Folder = "world";
    /** The client loads the scenario of activity NAME as NAME:scenario_client. */
    public const string ScenarioSuffix = ":scenario_client";

    /**
     * The skeleton under the roots, one file per tag, named world/DESTINATION/TYPE/OLDTAG.TYPE.
     * References between the files become project names, by tag or by wide hash; anything else stays stock.
     */
    public static IReadOnlyList<TftFile> Files(PackageCatalog catalog, RegistryIndex schema, string destination, IEnumerable<uint> roots)
    {
        TftNames.Check(destination);
        if (destination.Contains('/')) throw new ArgumentException("A destination name is one segment.", nameof(destination));
        var members = WorldSkeleton.Collect(catalog, schema, roots);
        return TftClone.Clone(members, new CatalogTagSource(catalog), schema, tag =>
        {
            string type = TypeName(schema, catalog.TryGetEntry(tag, out var entry) ? entry.Reference : 0);
            return $"{Folder}/{destination}/{type}/{tag:x8}.{type}";
        });
    }

    /** A class's tag type: its .tft type, else its registry name without the s_ prefix. */
    public static string TypeName(RegistryIndex schema, uint classId)
    {
        var known = schema.Class(classId) ?? throw new InvalidDataException($"Class 0x{classId:X8} is not in the registry.");
        string name = known.TftType ?? (known.Name.StartsWith("s_", StringComparison.Ordinal) ? known.Name[2..] : known.Name);
        return TftNames.IsTagType(name) ? name : throw new InvalidDataException($"Class {known.Name} has no usable tag type.");
    }

    /** The scenario's dev name: the client loads the map root named map:DEVNAME:root. */
    public static string DevName(PackageCatalog catalog, RegistryIndex schema, uint scenario)
    {
        var document = TagDocument.Parse(catalog.Read(scenario), SScenario.Class, schema);
        var node = document.Root.Children.FirstOrDefault(x => x.Kind == TagNodeKind.String && x.Field?.Name == "dev_name")
            ?? throw new InvalidDataException($"Scenario 0x{scenario:X8} has no dev name.");
        return System.Text.Encoding.ASCII.GetString(document.Bytes.Slice(node.Offset, node.Length - 1));
    }

    /** The map root the client loads with a scenario; two tags can share the name, and the largest holds the most rows. */
    public static uint MapRootOf(PackageCatalog catalog, RegistryIndex schema, uint scenario)
    {
        string name = $"map:{DevName(catalog, schema, scenario)}:root";
        return catalog.Names.Where(x => x.Value == name && catalog.TryGetEntry(x.Key, out var entry) && entry.Reference == SMapRoot.Class)
            .Select(x => x.Key).OrderByDescending(x => catalog.Read(x).Length).ThenBy(x => x).FirstOrDefault() is var tag and not 0
            ? tag : throw new InvalidDataException($"The installation has no map root named {name}.");
    }

    /**
     * The activity root of a scenario: the tag named ACTIVITY whose first reference is the scenario.
     * The client finds an activity's package by this name before it loads the scenario.
     */
    public static uint ActivityRootOf(PackageCatalog catalog, uint scenario)
    {
        string activity = ActivityName(catalog, scenario)
            ?? throw new InvalidDataException($"Scenario 0x{scenario:X8} is not named ACTIVITY{ScenarioSuffix}.");
        foreach (var (tag, _) in catalog.Names.Where(x => x.Value == activity).OrderBy(x => x.Key))
        {
            if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != ActivityRoot.Class) continue;
            byte[] bytes = catalog.Read(tag);
            var targets = TagView.Rows(bytes, in TagView.Checked<ActivityRoot>(bytes, 0).BoundTags, ActivityRootTagRow.Class);
            if (targets.Length != 0 && targets[0].Tag == scenario) return tag;
        }
        throw new InvalidDataException($"The installation has no activity root named {activity} for its scenario.");
    }

    /** @return The activity a scenario belongs to, from the scenario's name; null when the name does not end in ScenarioSuffix. */
    public static string? ActivityName(PackageCatalog catalog, uint scenario) =>
        catalog.Names.TryGetValue(scenario, out string? name) && name.EndsWith(ScenarioSuffix, StringComparison.Ordinal)
            ? name[..^ScenarioSuffix.Length] : null;

    /** The bubbles a scenario lists, with their slice set on its map; only copyable ones can be kept. */
    public static IReadOnlyList<WorldBubble> Bubbles(PackageCatalog catalog, uint scenario, uint mapRoot)
    {
        byte[] root = catalog.Read(mapRoot);
        ref readonly var map = ref TagView.Checked<SMapRoot>(root, 0);
        byte[] definition = catalog.Read(map.MapDefinition);
        ref readonly var sets = ref TagView.Checked<SMapLocationNames>(definition, 0);
        var slices = TagView.Rows(definition, in sets.SliceSets, MapBubble.Class);
        var rows = TagView.Rows(root, in map.MapEntries, MapBubbleRef.Class);
        var copyable = new HashSet<uint>();
        for (int index = 0; index < rows.Length && index < slices.Length; index++)
        {
            uint? parent = rows[index].NarrowTag != uint.MaxValue ? rows[index].NarrowTag : catalog.Resolve64(rows[index].WideTag);
            if (parent is { } tag && catalog.TryGetEntry(tag, out var entry) && entry.Reference == SBubbleParent.Class)
                copyable.Add(slices[index].BubbleNameHash);
        }
        var slice = new Dictionary<uint, int>();
        for (int index = 0; index < slices.Length; index++) slice.TryAdd(slices[index].BubbleNameHash, index);
        byte[] bytes = catalog.Read(scenario);
        ref readonly var scenarioRoot = ref TagView.Checked<SScenario>(bytes, 0);
        var result = new List<WorldBubble>();
        foreach (ref readonly var bubble in TagView.Rows(bytes, in scenarioRoot.Bubbles, ScenarioBubble.Class))
            result.Add(new(bubble.NameHash, slice.GetValueOrDefault(bubble.NameHash, -1), copyable.Contains(bubble.NameHash)));
        return result;
    }

    /** The destinations a project made: its world folders that hold a scenario. */
    public static IReadOnlyList<string> ProjectDestinations(string contentDirectory, RegistryIndex schema)
    {
        string root = Path.Combine(contentDirectory, Folder);
        if (!Directory.Exists(root)) return [];
        string type = TypeName(schema, SScenario.Class);
        return Directory.EnumerateDirectories(root).Where(folder => Directory.Exists(Path.Combine(folder, type)))
            .Select(folder => Path.GetFileName(folder)).Order(StringComparer.Ordinal).ToArray();
    }

    /** The installed scenario a project destination was copied from, or null when its scenario names none. */
    public static uint? SourceScenario(string contentDirectory, RegistryIndex schema, string destination)
    {
        string folder = Path.Combine(contentDirectory, Folder, destination, TypeName(schema, SScenario.Class));
        if (!Directory.Exists(folder)) return null;
        foreach (string path in Directory.EnumerateFiles(folder, "*" + TftNames.FileExtension))
            if (TftFile.Read(File.ReadAllBytes(path)).SourceTag is uint source) return source;
        return null;
    }

    /**
     * Writes a new destination into a project: the template's world cut to the kept bubbles and
     * named so the client finds it as NAME:scenario_client and map:NAME:root.
     * @return The world folder and how many files it holds.
     */
    public static (string Folder, int Files) Create(string contentDirectory, PackageCatalog catalog, RegistryIndex schema, uint scenario,
        IReadOnlySet<uint> bubbles, string name)
    {
        if (catalog.Names.Values.Any(x => x == name + ScenarioSuffix || x == $"map:{name}:root"))
            throw new InvalidOperationException($"The installation already has a destination named {name}.");
        uint mapRoot = MapRootOf(catalog, schema, scenario);
        var world = new WorldFiles(Files(catalog, schema, name, [scenario, mapRoot, ActivityRootOf(catalog, scenario)]));
        WorldCut.KeepBubbles(world, bubbles);
        WorldIdentity.Apply(world, name, name);
        return (world.Save(contentDirectory), world.Files.Count);
    }
}
