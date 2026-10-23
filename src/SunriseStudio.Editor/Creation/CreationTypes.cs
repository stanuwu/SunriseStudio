using SunriseStudio.Formats;
using SunriseStudio.Packages;

namespace SunriseStudio.Editor.Creation;

/** New starts empty; Clone copies an installed asset of the same category. */
public enum CreationMode { New, Clone }

/**
 * One New or Clone action of a category or asset key.
 * @param Blocker Why the action cannot run yet; empty when it can.
 * @param Factory Builds the workflow; null for the Duplicate route.
 */
public sealed record CreationType(string Name, string Summary, string Blocker,
    Func<StudioWorkspace, CreationWorkflow>? Factory = null)
{
    public bool Available => Blocker.Length == 0;
    /** A Clone that also takes a project asset as its base; the base is never written. */
    public bool ClonesProject { get; init; }
}

/** New and Clone per category or asset key. Each Add*Types hook is implemented once, in its own file. */
public static partial class CreationTypes
{
    public const string Duplicate = "Duplicate";
    public const string TextureKey = "Texture", MaterialKey = "Material", StaticPropKey = "Static prop", OtherKey = "Other";

    public const string CombatantBlocker = "Entity clone is not supported yet.";
    public const string AudioBlocker = "Sound banks can only replace weapon sounds.";
    public const string AnimationBlocker = "Edit clips of an existing rig with Import animation.";
    public const string NoScratchItem = "No item writer. Use Clone.";
    public const string NotBuilt = "Not built yet.";

    private static readonly Dictionary<(string Key, CreationMode Mode), CreationType> Types = [];

    /** Types with no key: nothing in Studio writes them. */
    public static IReadOnlyList<CreationType> Blocked { get; } =
    [
        new("Activity", "", "Activity creation is not supported yet."),
        new("Animation", "", AnimationBlocker)
    ];

    static CreationTypes()
    {
        AddItemTypes();
        AddExoticTypes();
        AddPerkTypes();
        AddVendorTypes();
        AddContentTypes();
        AddEntityTypes();
        AddMapTypes();
        Add(OtherKey, CreationMode.New, new("Asset", "", "Use Add asset for raw files."));
        Add(OtherKey, CreationMode.Clone, new(Duplicate, "A copy of this asset in a new package. May not work in game.", ""));
    }

    private static void Add(string key, CreationMode mode, CreationType type)
    {
        if (!Types.TryAdd((key, mode), type)) throw new InvalidOperationException($"{key} has two {mode} types.");
    }

    /** Never null; a key with no registered type gives a disabled type. */
    public static CreationType For(string key, CreationMode mode) =>
        Types.GetValueOrDefault((key, mode)) ?? new(key, "", NotBuilt);

    /** The category of the row, else its record key, else the asset key its class gives. */
    public static string KeyFor(PackageEntry entry, StudioWorkspace workspace) =>
        workspace.CategoryNameOf(entry.TagHash) ?? RecordKey(entry) ?? KeyOf(entry);

    /** Item, display and vendor records with no category row still never take OtherKey. */
    private static string? RecordKey(PackageEntry entry) => entry switch
    {
        { Reference: InvestmentCatalog.ItemClass or InvestmentCatalog.DisplayClass, Type: 8 or 16 }
            when entry.TagHash is not (InvestmentCatalog.ItemIndexTag or InvestmentCatalog.DisplayIndexTag) => InvestmentItemKinds.Other,
        { Reference: InvestmentCatalog.VendorClass or InvestmentVendorAuthoring.DisplayClass, Type: 8 or 16 } => StudioCategories.VendorsName,
        _ => null
    };

    private static string KeyOf(PackageEntry entry) => entry switch
    {
        { Type: 32, SubType: >= 1 and <= 3 } => TextureKey,
        { Reference: 0x808071E8 } => MaterialKey,
        { Reference: 0x808071A7 } => StaticPropKey,
        _ => OtherKey
    };

    /** What Edit in workflow does for a document. */
    public enum EditRoute { None, Item, Vendor, Material, Texture, CuiPage, PerkPage }

    public static EditRoute RouteFor(StudioDocument document) => document.Entry.Reference switch
    {
        InvestmentCatalog.ItemClass or InvestmentCatalog.DisplayClass when document.Entry.Type is 8 or 16
            && document.Entry.TagHash is not (InvestmentCatalog.ItemIndexTag or InvestmentCatalog.DisplayIndexTag) => EditRoute.Item,
        InvestmentCatalog.VendorClass or InvestmentVendorAuthoring.DisplayClass when document.Entry.Type is 8 or 16 => EditRoute.Vendor,
        0x808071E8 when document.Entry.Type is 8 or 16 => EditRoute.Material,
        InvestmentSandboxPerkAuthoring.GameplayClass when document.Entry.Type is 8 or 16 => EditRoute.PerkPage,
        _ when CuiDocument.Supports(document.Entry.Reference) && document.Entry.Type is 8 or 16 => EditRoute.CuiPage,
        _ when document.Texture is not null && document.Entry.Type == 32 && document.Entry.SubType is >= 1 and <= 3 => EditRoute.Texture,
        _ => EditRoute.None
    };

    static partial void AddItemTypes();
    static partial void AddExoticTypes();
    static partial void AddPerkTypes();
    static partial void AddVendorTypes();
    static partial void AddContentTypes();
    static partial void AddEntityTypes();
    static partial void AddMapTypes();
}
