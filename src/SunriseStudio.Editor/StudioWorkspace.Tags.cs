using SunriseStudio.Formats;
using SunriseStudio.Packages.Registry;

namespace SunriseStudio.Editor;

public sealed partial class StudioWorkspace
{
    // Read once; a failed read is kept too, so pages that ask every frame do not retry it.
    private static readonly Lazy<(RegistryIndex? Index, string? Error)> tagSchema = new(() => (StudioRegistry.TryIndex(out string? error), error));
    private static RegistryIndex? tagSchemaOverride;

    /** The shipped tag registry; null when it cannot be read, with TagSchemaError saying why. */
    public static RegistryIndex? TagSchema => tagSchemaOverride ?? tagSchema.Value.Index;
    public static string? TagSchemaError => tagSchemaOverride is null ? tagSchema.Value.Error : null;

    /** Tests run without the shipped registry; this sets the one the inspector and the Tag page use. */
    internal static void UseTagSchema(RegistryIndex? schema) => tagSchemaOverride = schema;

    /** Tests build installs with no key export; this names one for an install root, or returns null. */
    internal static Func<string, string?>? TestKeySource { get; set; }

    /** The schema tree carries each class panel; a panel stands alone only when the registry has no tree for the tag. */
    private static AssetInspectionService CreateInspectors()
    {
        var panels = new AssetInspectionService().Inspectors;
        var service = new AssetInspectionService(includeDefaults: false);
        service.Register(new TagInspector(() => TagSchema, panels));
        foreach (var panel in panels) service.Register(panel);
        return service;
    }
}
