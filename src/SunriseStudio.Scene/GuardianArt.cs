using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

/**
 * Art entities of armor and weapon items, read from the item's art block at +136, the shader an item's sockets start
 * with, and armor to show a shader on.
 */
public static class GuardianArt
{
    private const ushort NoGearRow = 0xFFFF;
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
        int arrangement = ItemArrangement(itemDefinition, classByte);
        if (arrangement < 0)
            return ArtBlock(new TagData(itemDefinition)) < 0 ? []
                : throw new InvalidDataException($"No art row matches class {classByte}.");
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
            return (data, TagView.RowOffsets(data, TagView.Rows(data, in TagView.At<SGearTableTag0x81613D27>(data).Table, SGearRow.Class)));
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
