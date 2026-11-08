using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Packages;

/** Browser categories and filter tags: a tag class has a registry category, other entries a package type. */
public static partial class AssetCategories
{
    private static readonly ConcurrentDictionary<(int Type, int SubType, uint Class, ushort Package), (string, string[])> Cache = new();
    private static readonly ConcurrentDictionary<string, string[]> PackageTags = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, (string, string)> KindParts = new(StringComparer.Ordinal);
    // A kind is "Category / Type"; a kind with no separator has no type.
    private const string KindSeparator = " / ";

    /** Splits a kind such as "Entity / Prop" into its category and type; the type is empty when absent. */
    public static (string Category, string Type) SplitKind(string kind) => KindParts.GetOrAdd(kind, value =>
    {
        int separator = value.IndexOf(KindSeparator, StringComparison.Ordinal);
        return separator < 0 ? (value, "") : (value[..separator], value[(separator + KindSeparator.Length)..]);
    });

    [GeneratedRegex(@"^w64_|_[0-9a-f]{4}_\d+$|_unp\d+_\d+$")]
    private static partial Regex PackageAffixes();

    /** True for package types whose Reference field is a tag class. */
    public static bool IsStructured(PackageEntry entry) => entry.Type is 8 or 16;

    /**
     * Category and filter tags of one entry; results for the same type, class and package are shared.
     * @param entry Catalog entry.
     * @param names Optional names that add activity, destination and map tags.
     */
    public static (string Category, string[] Tags) Classify(PackageEntry entry, GameNames? names)
    {
        var classKey = IsStructured(entry) ? entry.Reference : 0;
        var (category, tags) = Cache.GetOrAdd((entry.Type, entry.SubType, classKey, entry.PackageId), _ =>
            (CategoryOf(entry), BuildTags(entry)));
        if (names?.Components is { } index && classKey is SEntity.Class or SEntityConfig.Class)
            return EntityClassify(entry, index, tags);
        if (names == null || entry.Reference is not (SScenario.Class or SMapRoot.Class) || !IsStructured(entry))
            return (category, tags);
        var extra = new List<string>(tags);
        if (names.Scenario(entry.TagHash) is { } scenario)
        {
            if (scenario.DevName != null) extra.Add($"map {scenario.DevName}");
            if (scenario.Destination != null) extra.Add($"destination {scenario.Destination}");
            if (scenario.Activity != null) extra.Add($"activity {scenario.Activity}");
        }
        if (names.MapDevName(entry.TagHash) is { } dev) extra.Add($"map {dev}");
        return (category, extra.Distinct().ToArray());
    }

    /** Browser category of a package entry; a tag class takes its registry category. */
    public static string CategoryOf(PackageEntry entry) => (entry.Type, entry.SubType) switch
    {
        (8 or 16, _) => StudioRegistry.Index.Class(entry.Reference)?.Category ?? "Other",
        (32 or 40, 1 or 2 or 3) or (48, _) => "Texture",
        (32 or 40, 4 or 6) => "Buffer",
        (32 or 40, 7) or (34 or 42, 1) => "Material",
        (33 or 41, _) => "Shader",
        (26, 4 or 5 or 6) => "Audio",
        (26, 7) => "Havok",
        (27, _) => "Video",
        (24, _) => "Font",
        _ => "Other"
    };

    /** A short kind label: the class label of a structured tag, else the package type's meaning. */
    public static string KindLabel(PackageEntry entry)
    {
        if (IsStructured(entry)) return ClassNames.Describe(entry.Reference);
        var part = entry.Type is >= 32 and <= 34 ? " header" : entry.Type is >= 40 and <= 42 ? " data" : "";
        return (entry.Type, entry.SubType) switch
        {
            (32 or 40, 1) => "texture 2D" + part,
            (32 or 40, 2) => "texture cube" + part,
            (32 or 40, 3) => "texture 3D" + part,
            (32 or 40, 4) => "vertex buffer" + part,
            (32 or 40, 6) => "index buffer" + part,
            (32 or 40, 7) => "constant buffer" + part,
            (48, _) => "texture large buffer",
            (33 or 41, 0) => "pixel shader" + part,
            (33 or 41, 1) => "vertex shader" + part,
            (33 or 41, 2) => "geometry shader" + part,
            (33 or 41, 6) => "compute shader" + part,
            (34 or 42, 1) => "sampler" + part,
            (26, 4) => "wwise init bank",
            (26, 5) => "wwise bank",
            (26, 6) => "wwise stream",
            (26, 7) => "havok",
            (27, _) => "video",
            (24, _) => "font",
            _ => $"type {entry.Type}/{entry.SubType}"
        };
    }

    /** "w64_edz_activities_0197_5" gives "edz_activities" and "edz". */
    public static IReadOnlyList<string> PackageStems(string packageName) =>
        PackageTags.GetOrAdd(packageName, name =>
        {
            var stem = PackageAffixes().Replace(Path.GetFileNameWithoutExtension(name), "");
            foreach (var suffix in new[] { "_activities", "_destination" })
                if (stem.EndsWith(suffix, StringComparison.Ordinal) && stem.Length > suffix.Length)
                    return [stem, stem[..^suffix.Length]];
            return [stem];
        });

    /** Entities split by kind, configs by their primary component class; activity slots get their own branch. */
    private static (string, string[]) EntityClassify(PackageEntry entry, ComponentIndex index, string[] tags)
    {
        var extra = new List<string>(tags);
        string category;
        if (entry.Reference == SEntity.Class)
        {
            var kind = index.EntityKind(entry.TagHash) ?? "Other";
            category = $"Entity / {kind}";
            extra.Add($"kind {kind}");
            if (index.CombatantName(entry.TagHash) is { } combatant) extra.Add($"combatant {combatant}");
            if (index.ObjectType(entry.TagHash) is { } objectType) extra.Add($"object type {objectType}");
        }
        else
        {
            var primary = index.PrimaryClass(entry.TagHash);
            var label = index.ComponentLabel(primary);
            category = index.IsSlotComponent(primary) ? $"Activity slot / {label}"
                : label != null ? $"Entity component / {label}" : "Entity component / Other";
            extra.Add($"component 0x{primary:X8}");
            if (label != null) extra.Add($"component {label}");
        }
        if (index.Prompt(entry.TagHash) is { } prompt) extra.Add($"prompt {prompt}");
        return (category, extra.ToArray());
    }

    private static string[] BuildTags(PackageEntry entry)
    {
        var tags = new List<string> { KindLabel(entry), $"type {entry.Type}/{entry.SubType}" };
        foreach (var stem in PackageStems(entry.PackageName)) tags.Add($"package {stem}");
        return tags.Distinct().ToArray();
    }
}
