using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** Interface and Audio categories; rows split by the type in their registry category or package type. */
public static partial class StudioCategories
{
    /** Category keys; creation types and shortcuts look them up. */
    internal const string CuiName = "CUI", AudioName = "Audio";

    static partial void AddContentCategories(List<StudioCategory> categories)
    {
        categories.Add(new(CuiName, "Interface", ContentGroup, StudioIcon.Frame,
            "Game screens and their widget resources", ["sunrise.cui", "sunrise.related"]));
        categories.Add(new(AudioName, AudioName, ContentGroup, StudioIcon.Play,
            "Sound events, banks and streams", ["sunrise.audio", "sunrise.related"]));
    }

    // Records the Audio view cannot open; their rows sort after the ones it can.
    private static readonly HashSet<string> ListOnlyKinds = new(StringComparer.Ordinal)
        { "Audio / Event timing", "Audio / Event shared" };

    // The registry category of a tag class names its type; runs on every row, so cached lookups only.
    static partial void ClassifyContent(PackageEntry entry, StudioNameIndex names, ref StudioCategoryMatch? match)
    {
        var (category, type) = AssetCategories.SplitKind(StudioWorkspace.AssetKind(entry));
        if (category is not (CuiName or AudioName)) return;
        match = new(ItemKinds.GetOrAdd((category, type.Length == 0 ? BankType(entry) : type), key =>
            key.Type.Length == 0 ? key.Category : $"{key.Category} / {key.Type}"));
    }

    // Wwise banks are package types, not tag classes.
    private static string BankType(PackageEntry entry) => (entry.Type, entry.SubType) switch
    {
        (26, 4) => "Init bank",
        (26, 5) => "Bank",
        (26, 6) => "Stream",
        _ => ""
    };
}
