using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

/** Art entities of armor and weapon items, the shader an item starts with, and armor to show a shader on. */
public static class GuardianArt
{
    private const ushort NoGearRow = 0xFFFF, NoPlug = 0xFFFF;
    // Assumed: a set's five pieces sit within four rows of each other in the item index.
    private const int SetRun = 4, ClassItemSlot = 4;
    private static readonly ConditionalWeakTable<PackageCatalog, GuardianArtTables> SharedTables = new();
    /** Shown with a shader preview, since ShowcaseArmor rests on assumptions. */
    public const string ShowcaseNote = "Showcase armor: Legendary (assumed tier 4) pieces within four item index rows"
        + " of a class item, assumed to be one set. May be inaccurate.";

    /**
     * Armor to show a shader on, helmet to class item, all Legendary with a model for this gender.
     * Assumed: class items of one class share their display word; the set is the armor near the class item.
     * @param placeholder The class's gear-preview class item.
     * @return Every slot 0 when no complete set is found.
     */
    public static uint[] ShowcaseArmor(PackageCatalog catalog, byte classByte, bool female, uint placeholder)
    {
        var tables = TablesFor(catalog);
        return tables.Showcase.GetOrAdd((classByte, female), _ =>
        {
            var empty = new uint[GuardianAppearance.ArmorSlots];
            if (tables.IndexOf(placeholder) is not { } own || tables.DisplayWord(own) is not { } type) return empty;
            bool Fits(int index, out int slot, out uint tag)
            {
                slot = -1; tag = 0;
                if (tables.ItemTag(index) is not { } found) return false;
                byte[] definition = catalog.Read(found);
                slot = GuardianAppearance.ArmorSlot(definition);
                if (slot < 0 || Tier(definition) != InvestmentItemKinds.LegendaryTier) return false;
                try { if (ItemEntities(tables, definition, classByte, female).Count == 0) return false; }
                catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or ArgumentException) { return false; }
                tag = found;
                return true;
            }
            for (int index = 0; index < tables.ItemCount; index++)
            {
                if (!Fits(index, out int slot, out uint classItem) || slot != ClassItemSlot
                    || tables.DisplayWord(index) != type) continue;
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
        if (!TagView.FitsThrough<ItemDefinition>(itemDefinition, 0, nameof(ItemDefinition.SocketsItemBlock))) return 0;
        if (TagView.TargetOffset(itemDefinition, in TagView.At<ItemDefinition>(itemDefinition).SocketsItemBlock) is not { } block)
            return 0;
        var tables = TablesFor(catalog);
        ref readonly var sockets = ref TagView.At<SItemSocketBlock>(itemDefinition, block).Sockets;
        foreach (ref readonly var socket in TagView.Rows(itemDefinition, in sockets, ItemOrdinarySocketEntry.Class))
        {
            ushort plug = socket.InitialPlugItem;
            if (plug == NoPlug || tables.ItemTag(plug) is not { } tag) continue;
            if (Bucket(catalog.Read(tag)) == InvestmentItemKinds.ShaderBucket) return tag;
        }
        return 0;
    }

    /** The definition's inventory bucket; -1 when the definition is too short for it. */
    private static int Bucket(byte[] definition) => InventoryByte(definition, "InventoryItemBlock.InventoryBucket");

    /** The definition's tier; -1 when the definition is too short for it. */
    private static int Tier(byte[] definition) => InventoryByte(definition, "InventoryItemBlock.Tier");

    private static int InventoryByte(byte[] definition, string member) => TagView.FitsThrough<ItemDefinition>(definition, 0, member)
        ? TagRead.U8(definition, TagView.FieldOffset<ItemDefinition>(member)) : -1;

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
            return ArtBlock(itemDefinition) < 0 ? [] : throw new InvalidDataException($"No art row matches class {classByte}.");
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
        int art = ArtBlock(itemDefinition);
        if (art < 0) return -1;
        ref readonly var artRows = ref TagView.At<TagArray<ItemArtRow>>(itemDefinition,
            art + TagView.FieldOffset<ItemTranslationBlock>(nameof(ItemTranslationBlock.ArtRows)));
        foreach (ref readonly var artRow in TagView.Rows(itemDefinition, in artRows, ItemArtRow.Class))
            if (artRow.ClassValue == -1 || artRow.ClassValue == classByte) return artRow.ArrangementRow;
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
    private readonly Lazy<(byte[] Data, int[] Rows)> gear;
    private readonly Lazy<uint[]> items, displays;

    public GuardianArtTables(PackageCatalog catalog)
    {
        this.catalog = catalog;
        Arrangements = PackageHashMap.ReadStructured(catalog, GuardianAppearance.ArrangementTable);
        ref readonly var arrangements = ref TagView.At<TagArray<SArrangementRow>>(Arrangements,
            TagView.FieldOffset<SArrangementTableTag0x81613D23>(nameof(SArrangementTableTag0x81613D23.Table)));
        arrangementRows = TagView.RowOffsets(Arrangements, TagView.Rows(Arrangements, in arrangements, SArrangementRow.Class));
        artMap = new(() => PackageHashMap.Read(catalog, GuardianAppearance.ArtMap, SArtNameRow.Class));
        runtimeMap = new(() => PackageHashMap.Read(catalog, GuardianAppearance.RuntimeMap, SRuntimeNameRow.Class));
        items = new(() =>
        {
            var data = PackageHashMap.ReadStructured(catalog, InvestmentTags.ItemIndex);
            return TagView.Rows(data, in TagView.At<ItemIndexTable>(data).Table).ToArray().Select(row => row.ItemDefinition).ToArray();
        });
        displays = new(() =>
        {
            var data = PackageHashMap.ReadStructured(catalog, InvestmentTags.ItemDisplayIndex);
            return TagView.Rows(data, in TagView.At<C80805cdb>(data).Table).ToArray().Select(row => row.ItemDisplay).ToArray();
        });
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

    /** Showcase armor by class byte and gender; see GuardianArt.ShowcaseArmor. */
    public ConcurrentDictionary<(byte ClassByte, bool Female), uint[]> Showcase { get; } = new();

    /** The item index of an item definition; null when the index does not name it. */
    public int? IndexOf(uint tag) => Array.IndexOf(items.Value, tag) is >= 0 and var index ? index : null;

    /** The number of rows in the item index. */
    public int ItemCount => items.Value.Length;

    /** The item definition at an item index; null outside the index or for a row naming no item definition. */
    public uint? ItemTag(int index)
    {
        var tags = items.Value;
        return (uint)index < (uint)tags.Length && IsClass(tags[index], InvestmentTags.ItemClass) ? tags[index] : null;
    }

    /** The unnamed display word at DisplayProperties +0x38 of the display at this index; null when it does not read. */
    public uint? DisplayWord(int index)
    {
        const string word = "DisplayProperties.Unk38";
        var tags = displays.Value;
        if ((uint)index >= (uint)tags.Length || !IsClass(tags[index], InvestmentTags.ItemDisplayClass)) return null;
        byte[] display = catalog.Read(tags[index]);
        return TagView.FitsThrough<ItemDisplayStrings>(display, 0, word)
            ? TagRead.U32(display, TagView.FieldOffset<ItemDisplayStrings>(word)) : null;
    }

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
