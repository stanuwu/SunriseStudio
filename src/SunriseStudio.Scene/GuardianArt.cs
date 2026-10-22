using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;

namespace SunriseStudio.Scene;

/**
 * Art entities of armor and weapon items, read from the item's art block at +136, the shader an item's sockets start
 * with, and armor to show a shader on.
 */
public static class GuardianArt
{
    // Hash of the empty string; a slot holding it names no art.
    internal const uint EmptyHash = 0x811C9DC5;
    private const uint ItemArtClass = 0x808077B5, HolderClass = 0x80805DFE, ArtHashClass = 0x80805E01;
    // Item definition +136 points to the art block; art block +88 is the u16 gear row.
    private const int ArtBlockField = 136, GearRowField = 88;
    private const ushort NoGearRow = 0xFFFF;
    // Gear row +16 is the key that picks the weapon's row in each presentation config.
    private const int GearKeyField = 16;
    private const uint ComponentRowClass = 0x80809C04, ComponentClass = 0x80809C36;
    // Weapon resource: a typed row array at +1416, 232-byte rows, a default row at +1184; row +0x6C is the tag.
    private const uint PresentationClass = 0x80804221, PresentationRowClass = 0x80803E72;
    private const int PresentationRows = 1416, PresentationDefault = 1184, PresentationRowStride = 232, ThirdPersonField = 0x6C;
    private static readonly ConditionalWeakTable<PackageCatalog, GuardianArtTables> SharedTables = new();
    // Item definition +104 points to the socket block, whose first field holds the socket rows: 80 bytes, the initial
    // plug's item index at +2. Shaders are bucket 14, as InvestmentItemKinds files them.
    private const int SocketBlockField = 104, SocketStride = 80, SocketInitialPlug = 2, ShaderBucket = 14;
    private const uint SocketRowClass = 0x808077C4;
    private const ushort NoPlug = 0xFFFF;

    // Item definition +186 is the tier. Display +0xB8 is a word every class item of one class shares, as its type line
    // is. A set's five pieces sit together in the item index.
    private const int ItemTierField = 186, ClassItemTypeField = 0xB8, SetRun = 4, ClassItemSlot = 4;

    /**
     * Armor to show a shader on, helmet to class item: one class's set, found without naming an item. The first
     * Legendary class item whose display shares the class's gear-preview class item's type word fixes the class, and
     * its set's other pieces are the Legendary armor within the same run of the item index. Each piece needs a model
     * for this gender; if no complete set is found, every slot is 0. Read once per catalog, class and gender.
     * @param placeholder The class's gear-preview class item.
     */
    public static uint[] ShowcaseArmor(PackageCatalog catalog, byte classByte, bool female, uint placeholder)
    {
        // The item definition's Legendary tier.
        const byte legendaryTier = 4;
        var tables = TablesFor(catalog);
        return tables.Showcase.GetOrAdd((classByte, female), _ =>
        {
            var empty = new uint[GuardianAppearance.ArmorSlots];
            if (tables.IndexOf(placeholder) is not { } own || tables.DisplayWord(own, ClassItemTypeField) is not { } type) return empty;
            bool Fits(int index, out int slot, out uint tag)
            {
                slot = -1; tag = 0;
                if (tables.ItemTag(index) is not { } found) return false;
                byte[] definition = catalog.Read(found);
                slot = GuardianAppearance.ArmorSlot(definition);
                if (slot < 0 || definition.Length <= ItemTierField
                    || definition[ItemTierField] != legendaryTier) return false;
                try { if (ItemEntities(tables, definition, classByte, female).Count == 0) return false; }
                catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or ArgumentException) { return false; }
                tag = found;
                return true;
            }
            for (int index = 0; index < tables.ItemCount; index++)
            {
                if (!Fits(index, out int slot, out uint classItem) || slot != ClassItemSlot
                    || tables.DisplayWord(index, ClassItemTypeField) != type) continue;
                var armor = new uint[GuardianAppearance.ArmorSlots];
                armor[ClassItemSlot] = classItem;
                for (int near = Math.Max(0, index - SetRun); near <= index + SetRun && near < tables.ItemCount; near++)
                    if (near != index && Fits(near, out int piece, out uint tag) && piece != ClassItemSlot && armor[piece] == 0)
                        armor[piece] = tag;
                if (!armor.Contains(0u)) return armor;
            }
            return empty;
        });
    }

    /** The first shader plug the item's sockets start with, as its item definition tag; 0 when none does. */
    public static uint EquippedShader(PackageCatalog catalog, byte[] itemDefinition)
    {
        var item = new TagData(itemDefinition);
        if (!item.Has(SocketBlockField, 8) || item.I64(SocketBlockField) == 0) return 0;
        var tables = TablesFor(catalog);
        foreach (int socket in item.Rows(item.Relative(SocketBlockField), SocketRowClass, SocketStride))
        {
            ushort plug = item.U16(socket + SocketInitialPlug);
            if (plug == NoPlug || tables.ItemTag(plug) is not { } tag) continue;
            byte[] definition = catalog.Read(tag);
            if (definition.Length > GuardianAppearance.BucketField
                && definition[GuardianAppearance.BucketField] == ShaderBucket) return tag;
        }
        return 0;
    }

    /** Art entities of an armor or weapon item for one class byte and gender. Empty when the item has no art block. */
    public static IReadOnlyList<uint> ItemEntities(PackageCatalog catalog, byte[] itemDefinition, byte classByte, bool female) =>
        ItemEntities(TablesFor(catalog), itemDefinition, classByte, female);

    /** The weapon's gameplay root from gear row +88; 0 when the item has none (+88 == 0xFFFF). */
    public static uint GameplayEntity(PackageCatalog catalog, byte[] itemDefinition) =>
        GameplayEntity(TablesFor(catalog), itemDefinition);

    /**
     * The weapon's third-person animation tag: +0x6C of its presentation row keyed by gear row +16, else of the
     * default row. The empty hash when the item has no gear row or no presentation component.
     */
    public static uint ThirdPersonTag(PackageCatalog catalog, byte[] itemDefinition)
    {
        var tables = TablesFor(catalog);
        if (GearRow(tables, itemDefinition) is not { } row) return EmptyHash;
        uint key = tables.Gear.Data.U32(row + GearKeyField);
        var entity = new TagData(catalog.Read(GameplayEntity(tables, itemDefinition)));
        foreach (int component in entity.Rows(0x10, ComponentRowClass, 12))
        {
            uint tag = entity.U32(component);
            if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != ComponentClass) continue;
            var config = new TagData(catalog.Read(tag));
            int resource = config.Relative(0x18);
            if (!config.Has(resource - 4, 4) || config.U32(resource - 4) != PresentationClass) continue;
            int selected = config.Rows(resource + PresentationRows, PresentationRowClass, PresentationRowStride)
                .FirstOrDefault(at => config.U32(at) == key, resource + PresentationDefault);
            return config.U32(selected + ThirdPersonField);
        }
        return EmptyHash;
    }

    // The public wrappers share one table set per catalog.
    private static GuardianArtTables TablesFor(PackageCatalog catalog) => SharedTables.GetValue(catalog, value => new(value));

    internal static uint GameplayEntity(GuardianArtTables tables, byte[] itemDefinition)
    {
        if (GearRow(tables, itemDefinition) is not { } row) return 0;
        uint name = tables.Gear.Data.U32(row + 4);
        if (!tables.RuntimeNames.TryGetValue(name, out uint entity))
            throw new InvalidDataException($"Gear name 0x{name:X8} does not resolve.");
        tables.RequireClass(entity, GuardianArtTables.EntityClass);
        return entity;
    }

    // Offset of the item's gear row in the gear table; null when the item has none.
    private static int? GearRow(GuardianArtTables tables, byte[] itemDefinition)
    {
        var item = new TagData(itemDefinition);
        int art = ArtBlock(item);
        if (art < 0) return null;
        ushort gearRow = item.U16(art + GearRowField);
        if (gearRow == NoGearRow) return null;
        var rows = tables.Gear.Rows;
        if (gearRow >= rows.Length) throw new InvalidDataException($"Gear row {gearRow} is outside its table.");
        return rows[gearRow];
    }

    /**
     * The first art row whose class byte is -1 or the class byte. A female takes arrangement +12 when it resolves
     * to a model, else +8. Every item also takes hash 0 of each arrangement group; only weapons have groups today.
     */
    internal static IReadOnlyList<uint> ItemEntities(GuardianArtTables tables, byte[] itemDefinition, byte classByte, bool female)
    {
        int arrangement = ItemArrangement(itemDefinition, classByte);
        if (arrangement < 0)
            return ArtBlock(new TagData(itemDefinition)) < 0 ? []
                : throw new InvalidDataException($"No art row matches class {classByte}.");
        int row = tables.Arrangement(arrangement);
        var table = tables.Arrangements;
        var result = new List<uint>();
        uint male = table.U32(row + 8), femaleHash = table.U32(row + 12);
        if (female && !IsEmpty(femaleHash) && tables.TryEntity(femaleHash, out uint femaleEntity)) result.Add(femaleEntity);
        else if (!IsEmpty(male)) result.Add(tables.Entity(male));
        foreach (var (_, hashes) in Groups(table, row))
            if (hashes.Length != 0 && !IsEmpty(hashes[0])) result.Add(tables.Entity(hashes[0]));
        return result.Distinct().ToArray();
    }

    /** The art arrangement of the item's first art row whose class byte is -1 or the class byte; -1 when none is. */
    public static int ItemArrangement(byte[] itemDefinition, byte classByte)
    {
        var item = new TagData(itemDefinition);
        int art = ArtBlock(item);
        if (art >= 0)
            foreach (int artRow in item.Rows(art, ItemArtClass, 4))
            {
                sbyte rowClass = (sbyte)item.U8(artRow);
                if (rowClass == -1 || rowClass == classByte) return item.U16(artRow + 2);
            }
        return -1;
    }

    /** Selector and art hashes of each group of one arrangement row, in row order; a selector may repeat. */
    internal static IReadOnlyList<(int Selector, uint[] Hashes)> Groups(TagData table, int row)
    {
        var result = new List<(int, uint[])>();
        foreach (int holder in table.Rows(row + 16, HolderClass, 8))
        {
            if (table.I64(holder) == 0) throw new InvalidDataException("An art group pointer is empty.");
            int group = table.Relative(holder);
            result.Add((table.U8(group), table.Rows(group + 8, ArtHashClass, 4).Select(table.U32).ToArray()));
        }
        return result;
    }

    internal static bool IsEmpty(uint hash) => hash is 0 or EmptyHash;

    // -1 when the item has no art block.
    private static int ArtBlock(TagData item) => item.I64(ArtBlockField) == 0 ? -1 : item.Relative(ArtBlockField);
}

/** The arrangement, gear and map tables of one catalog; thread-safe. */
internal sealed class GuardianArtTables
{
    internal const uint WrapperClass = 0x8080744A, EntityClass = 0x80809C0F;
    private const uint ArtMapRowClass = 0x808056EC, RuntimeMapRowClass = 0x80809252;
    private const uint ArrangementRowClass = 0x80805DFB, GearRowClass = 0x80805B7C;
    private readonly PackageCatalog catalog;
    private readonly int[] arrangementRows;
    private readonly Lazy<Dictionary<uint, uint>> artMap, runtimeMap;
    private readonly Lazy<(TagData Data, int[] Rows)> gear;
    private readonly Lazy<uint[]> items, displays;
    private const int ItemIndexStride = GuardianAppearance.ItemIndexStride, ItemIndexTag = GuardianAppearance.ItemIndexTagField;

    public GuardianArtTables(PackageCatalog catalog)
    {
        this.catalog = catalog;
        Arrangements = PackageHashMap.ReadStructured(catalog, GuardianAppearance.ArrangementTable);
        arrangementRows = Arrangements.Rows(8, ArrangementRowClass, 32);
        artMap = new(() => PackageHashMap.Read(catalog, GuardianAppearance.ArtMap, ArtMapRowClass));
        runtimeMap = new(() => PackageHashMap.Read(catalog, GuardianAppearance.RuntimeMap, RuntimeMapRowClass));
        items = new(() => ReadItemIndex(InvestmentTags.ItemIndex));
        displays = new(() => ReadItemIndex(InvestmentTags.ItemDisplayIndex));
        gear = new(() =>
        {
            var data = PackageHashMap.ReadStructured(catalog, GuardianAppearance.GearTable);
            return (data, data.Rows(8, GearRowClass, 48));
        });
    }

    /** Item and display indices share their row layout; keep only the tags. */
    private uint[] ReadItemIndex(uint tag)
    {
        var data = PackageHashMap.ReadStructured(catalog, tag);
        return data.RowsOfAnyClass(8, ItemIndexStride).Select(row => data.U32(row + ItemIndexTag)).ToArray();
    }

    public TagData Arrangements { get; }
    public (TagData Data, int[] Rows) Gear => gear.Value;
    /** Runtime map 0x80EC3F60: gear name hash -> entity. */
    public IReadOnlyDictionary<uint, uint> RuntimeNames => runtimeMap.Value;

    /** Offset of arrangement row index in Arrangements. */
    public int Arrangement(int index) => (uint)index < (uint)arrangementRows.Length ? arrangementRows[index]
        : throw new InvalidDataException($"Arrangement {index} is outside its table.");

    /** Showcase armor by class byte and gender; see GuardianArt.ShowcaseArmor. */
    public ConcurrentDictionary<(byte ClassByte, bool Female), uint[]> Showcase { get; } = new();

    /** The item index of an item definition; null when the index does not name it. */
    public int? IndexOf(uint tag) => Array.IndexOf(items.Value, tag) is >= 0 and var index ? index : null;

    /** The word at an offset of the display the display index names at this index; null when it does not read. */
    public uint? DisplayWord(int index, int offset)
    {
        var tags = displays.Value;
        if ((uint)index >= (uint)tags.Length || !IsClass(tags[index], InvestmentTags.ItemDisplayClass)) return null;
        var display = new TagData(catalog.Read(tags[index]));
        return display.Has(offset, 4) ? display.U32(offset) : null;
    }

    /** The number of rows in the item index. */
    public int ItemCount => items.Value.Length;

    /** The item definition at an item index; null outside the index or for a row naming no item definition. */
    public uint? ItemTag(int index)
    {
        var tags = items.Value;
        return (uint)index < (uint)tags.Length && IsClass(tags[index], InvestmentTags.ItemClass) ? tags[index] : null;
    }

    /** Art hash -> map 0x80EC3F61 -> 0x8080744A wrapper; wrapper +16 is the entity. */
    public uint Entity(uint hash) => TryEntity(hash, out uint entity) ? entity
        : throw new InvalidDataException($"Art hash 0x{hash:X8} does not resolve to an entity.");

    /** False when the map misses the hash, the wrapper is not 0x8080744A, or wrapper +16 is not an entity. */
    public bool TryEntity(uint hash, out uint entity)
    {
        entity = 0;
        if (!artMap.Value.TryGetValue(hash, out uint wrapper) || !IsClass(wrapper, WrapperClass)) return false;
        uint candidate = new TagData(catalog.Read(wrapper)).U32(16);
        if (!IsClass(candidate, EntityClass)) return false;
        entity = candidate;
        return true;
    }

    public void RequireClass(uint tag, uint type)
    {
        if (!IsClass(tag, type)) throw new InvalidDataException($"Tag 0x{tag:X8} is not class 0x{type:X8}.");
    }

    private bool IsClass(uint tag, uint type) => catalog.TryGetEntry(tag, out var entry) && entry.Reference == type;
}
