using System.Numerics;

namespace SunriseStudio.Formats;

/** A widget type key with its parent type and whether it uses the standard constructor defaults. */
public sealed record CuiTypeDescriptor(ulong Key, ulong ParentKey, bool StandardDefaults);

/** Widget type inheritance and constructor defaults of build 86657. */
public sealed partial class CuiTypeRegistry
{
    public const ulong LayoutBase = 0xFF700E02;
    public const ulong FrameType = 0xAE7E6D99;
    public const ulong ContainerType = 0x725AD1D887A7707B;
    public const ulong TextType = 0x0CE30D0B;
    public const ulong BitmapType = 0x7D1BD221;
    private readonly Dictionary<ulong, ulong> parents;
    private readonly HashSet<ulong> defaults;
    public Vector2? DefaultSize { get; }
    public int Count => parents.Count;
    /** Knows only the core widget types; used where no registry is supplied. */
    public static CuiTypeRegistry Core { get; } = new([], [], null);
    public static CuiTypeRegistry BuiltIn { get; } = Create(BuiltInRows, new(BuiltInWidth, BuiltInHeight));
    public static IReadOnlyList<CuiTypeDescriptor> BuiltInTypes => BuiltInRows;

    private CuiTypeRegistry(Dictionary<ulong, ulong> parents, HashSet<ulong> defaults, Vector2? defaultSize) =>
        (this.parents, this.defaults, DefaultSize) = (parents, defaults, defaultSize);

    /** @param defaultSize Size of a widget with the standard constructor defaults. */
    public static CuiTypeRegistry Create(IEnumerable<CuiTypeDescriptor> rows, Vector2 defaultSize) =>
        new(rows.ToDictionary(row => row.Key, row => row.ParentKey),
            rows.Where(row => row.StandardDefaults).Select(row => row.Key).ToHashSet(), defaultSize);

    /** True when the widget type derives from the layout base type. */
    public bool HasLayout(uint type, uint auxiliary) => Derives(Key(type, auxiliary), LayoutBase);
    /** True when the widget type derives from the container type. */
    public bool IsContainer(uint type, uint auxiliary) => Derives(Key(type, auxiliary), ContainerType);
    /** True when the default size is known and the type has verified constructor defaults (or is text or bitmap). */
    public bool HasVerifiedDefaults(uint type, uint auxiliary) => DefaultSize is not null
        && (defaults.Contains(Key(type, auxiliary)) || Key(type, auxiliary) is TextType or BitmapType);
    /** The type itself followed by its known ancestors, nearest first; just the type for an unregistered one. */
    public IReadOnlyList<ulong> Lineage(uint type, uint auxiliary)
    {
        List<ulong> chain = [];
        ulong current = Key(type, auxiliary);
        while (current != 0 && chain.Count < 32 && !chain.Contains(current))
        {
            chain.Add(current);
            if (!parents.TryGetValue(current, out current)) break;
        }
        return chain;
    }
    private static ulong Key(uint type, uint auxiliary) => ((ulong)auxiliary << 32) | type;

    private bool Derives(ulong type, ulong target)
    {
        if (parents.Count == 0)
            return type == target || target == LayoutBase && type is FrameType or ContainerType or TextType or BitmapType;
        while (type != 0)
        {
            if (type == target) return true;
            if (!parents.TryGetValue(type, out type)) return false;
        }
        return false;
    }
}
