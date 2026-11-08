using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

/** Art entities of armor and weapon items, read from the item's translation block. */
public static class GuardianArt
{
    private const ushort NoGearRow = 0xFFFF;
    private static readonly ConditionalWeakTable<PackageCatalog, GuardianArtTables> SharedTables = new();

    /** Art entities of an armor or weapon item for one class byte and gender. Empty when the item has no art block. */
    public static IReadOnlyList<uint> ItemEntities(PackageCatalog catalog, byte[] itemDefinition, byte classByte, bool female) =>
        ItemEntities(TablesFor(catalog), itemDefinition, classByte, female);

    /** The weapon's gameplay root from the art block's gear row; 0 when the item has none (gear row 0xFFFF). */
    public static uint GameplayEntity(PackageCatalog catalog, byte[] itemDefinition) =>
        GameplayEntity(TablesFor(catalog), itemDefinition);

    /**
     * The weapon's third-person animation tag: that of its presentation row keyed by the gear row's key, else of
     * the default row. The empty hash when the item has no gear row or no presentation component.
     */
    public static uint ThirdPersonTag(PackageCatalog catalog, byte[] itemDefinition)
    {
        var tables = TablesFor(catalog);
        if (GearRow(tables, itemDefinition) is not { } row) return StringHash.Empty;
        uint key = TagView.At<SGearRow>(tables.Gear.Data, row).GearKeyPresentationHudSelector;
        byte[] entity = catalog.Read(GameplayEntity(tables, itemDefinition));
        ref readonly var components = ref TagView.At<TagArray<SPatternComponent>>(entity, TagView.FieldOffset<SEntity>(nameof(SEntity.Components)));
        foreach (ref readonly var component in TagView.Rows(entity, in components, SPatternComponent.Class))
        {
            uint tag = component.Config;
            if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != SEntityConfig.Class) continue;
            byte[] config = catalog.Read(tag);
            // A null resource pointer resolves to the field itself, whose class word is not a weapon resource.
            int resource = TagView.TargetOrSelf(config,
                in TagView.At<RelativePointer<Unknown>>(config, TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition))));
            if (TagView.ClassAt(config, resource) != SWeaponResource.Class) continue;
            ref readonly var presentations = ref TagView.At<TagArray<SWeaponVariantRow>>(config,
                resource + TagView.FieldOffset<SWeaponResource>(nameof(SWeaponResource.PresentationRows)));
            foreach (ref readonly var presentation in TagView.Rows(config, in presentations, SWeaponVariantRow.Class))
                if (presentation.GearSelectorKey0x44a0b334Base == key) return presentation.ThirdPersonAnimation;
            return TagRead.U32(config, resource + TagView.FieldOffset<SWeaponResource>("HDe30eaea.ThirdPersonAnimation"));
        }
        return StringHash.Empty;
    }

    // The public wrappers share one table set per catalog.
    private static GuardianArtTables TablesFor(PackageCatalog catalog) => SharedTables.GetValue(catalog, value => new(value));

    internal static uint GameplayEntity(GuardianArtTables tables, byte[] itemDefinition)
    {
        if (GearRow(tables, itemDefinition) is not { } row) return 0;
        uint name = TagView.At<SGearRow>(tables.Gear.Data, row).GameplayRuntimeName;
        if (!tables.RuntimeNames.TryGetValue(name, out uint entity))
            throw new InvalidDataException($"Gear name 0x{name:X8} does not resolve.");
        tables.RequireClass(entity, GuardianArtTables.EntityClass);
        return entity;
    }

    // Offset of the item's gear row in the gear table; null when the item has none.
    private static int? GearRow(GuardianArtTables tables, byte[] itemDefinition)
    {
        int art = ArtBlock(itemDefinition);
        if (art < 0) return null;
        ushort gearRow = TagRead.U16(itemDefinition, art + TagView.FieldOffset<ItemTranslationBlock>(nameof(ItemTranslationBlock.GearIndex)));
        if (gearRow == NoGearRow) return null;
        var rows = tables.Gear.Rows;
        if (gearRow >= rows.Length) throw new InvalidDataException($"Gear row {gearRow} is outside its table.");
        return rows[gearRow];
    }

    /**
     * The first art row whose class byte is -1 or the class byte. A female takes the female art hash when it
     * resolves to a model, else the male one. Every item also takes hash 0 of each arrangement group; only weapons
     * have groups today.
     */
    internal static IReadOnlyList<uint> ItemEntities(GuardianArtTables tables, byte[] itemDefinition, byte classByte, bool female)
    {
        int art = ArtBlock(itemDefinition);
        if (art < 0) return [];
        int arrangement = -1;
        ref readonly var artRows = ref TagView.At<TagArray<ItemArtRow>>(itemDefinition,
            art + TagView.FieldOffset<ItemTranslationBlock>(nameof(ItemTranslationBlock.ArtRows)));
        foreach (ref readonly var artRow in TagView.Rows(itemDefinition, in artRows, ItemArtRow.Class))
        {
            sbyte rowClass = artRow.ClassValue;
            if (rowClass != -1 && rowClass != classByte) continue;
            arrangement = artRow.ArrangementRow;
            break;
        }
        if (arrangement < 0) throw new InvalidDataException($"No art row matches class {classByte}.");
        int row = tables.Arrangement(arrangement);
        var table = tables.Arrangements;
        var result = new List<uint>();
        ref readonly var gearSet = ref TagView.At<SArrangementRow>(table, row).GearSet;
        uint male = gearSet.MaleArtHash, femaleHash = gearSet.FemaleArtHash;
        if (female && !IsEmpty(femaleHash) && tables.TryEntity(femaleHash, out uint femaleEntity)) result.Add(femaleEntity);
        else if (!IsEmpty(male)) result.Add(tables.Entity(male));
        foreach (var (_, hashes) in Groups(table, row))
            if (hashes.Length != 0 && !IsEmpty(hashes[0])) result.Add(tables.Entity(hashes[0]));
        return result.Distinct().ToArray();
    }

    /** Selector and art hashes of each group of one arrangement row, in row order; a selector may repeat. */
    internal static IReadOnlyList<(int Selector, uint[] Hashes)> Groups(byte[] table, int row)
    {
        var result = new List<(int, uint[])>();
        foreach (ref readonly var holder in TagView.Rows(table, in TagView.At<SArrangementRow>(table, row).GearSet.Regions,
                     SArrangementHolder.Class))
        {
            if (holder.Group.IsNull) throw new InvalidDataException("An art group pointer is empty.");
            int group = TagView.TargetOffset(table, in holder.Group)!.Value;
            byte selector = TagRead.U8(table, group + TagView.FieldOffset<ArrangementArtGroup>(nameof(ArrangementArtGroup.Selector)));
            ref readonly var hashArray = ref TagView.At<TagArray<SArtHash>>(table,
                group + TagView.FieldOffset<ArrangementArtGroup>(nameof(ArrangementArtGroup.H3265b065)));
            var hashes = TagView.Rows(table, in hashArray, SArtHash.Class);
            var names = new uint[hashes.Length];
            for (int i = 0; i < names.Length; i++) names[i] = hashes[i].ArtName;
            result.Add((selector, names));
        }
        return result;
    }

    internal static bool IsEmpty(uint hash) => hash is 0 or StringHash.Empty;

    // -1 when the item has no art block.
    private static int ArtBlock(byte[] item) => TagView.TargetOffset(item, in TagView.At<RelativePointer<ItemTranslationBlock>>(item,
        TagView.FieldOffset<ItemDefinition>(nameof(ItemDefinition.TranslationItemBlock)))) ?? -1;
}

/** The arrangement, gear and map tables of one catalog; thread-safe. */
internal sealed class GuardianArtTables
{
    internal const uint WrapperClass = SArtWrapper.Class, EntityClass = SEntity.Class;
    private readonly PackageCatalog catalog;
    private readonly int[] arrangementRows;
    private readonly Lazy<Dictionary<uint, uint>> artMap, runtimeMap;
    private readonly Lazy<(byte[] Data, int[] Rows)> gear;

    public GuardianArtTables(PackageCatalog catalog)
    {
        this.catalog = catalog;
        Arrangements = PackageHashMap.ReadStructured(catalog, GuardianAppearance.ArrangementTable);
        ref readonly var arrangements = ref TagView.At<TagArray<SArrangementRow>>(Arrangements,
            TagView.FieldOffset<SArrangementTableTag0x81613D23>(nameof(SArrangementTableTag0x81613D23.Table)));
        arrangementRows = TagView.RowOffsets(Arrangements, TagView.Rows(Arrangements, in arrangements, SArrangementRow.Class));
        artMap = new(() => PackageHashMap.Read(catalog, GuardianAppearance.ArtMap, SArtNameRow.Class));
        runtimeMap = new(() => PackageHashMap.Read(catalog, GuardianAppearance.RuntimeMap, SRuntimeNameRow.Class));
        gear = new(() =>
        {
            var data = PackageHashMap.ReadStructured(catalog, GuardianAppearance.GearTable);
            return (data, TagView.RowOffsets(data, TagView.Rows(data, in TagView.At<SGearTableTag0x81613D27>(data).Table, SGearRow.Class)));
        });
    }

    public byte[] Arrangements { get; }
    public (byte[] Data, int[] Rows) Gear => gear.Value;
    /** Runtime map 0x80EC3F60: gear name hash -> entity. */
    public IReadOnlyDictionary<uint, uint> RuntimeNames => runtimeMap.Value;

    /** Offset of arrangement row index in Arrangements. */
    public int Arrangement(int index) => (uint)index < (uint)arrangementRows.Length ? arrangementRows[index]
        : throw new InvalidDataException($"Arrangement {index} is outside its table.");

    /** Art hash -> map 0x80EC3F61 -> art wrapper -> the wrapper's entity. */
    public uint Entity(uint hash) => TryEntity(hash, out uint entity) ? entity
        : throw new InvalidDataException($"Art hash 0x{hash:X8} does not resolve to an entity.");

    /** False when the map misses the hash, the wrapper is not an art wrapper, or its art tag is not an entity. */
    public bool TryEntity(uint hash, out uint entity)
    {
        entity = 0;
        if (!artMap.Value.TryGetValue(hash, out uint wrapper) || !IsClass(wrapper, WrapperClass)) return false;
        uint candidate = TagRead.U32(catalog.Read(wrapper), TagView.FieldOffset<SArtWrapper>(nameof(SArtWrapper.ArtTag)));
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
