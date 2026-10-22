using SunriseStudio.Packages;

namespace SunriseStudio.Scene;

public sealed record GuardianClass(uint BodyTag, byte ClassByte, string Name)
{
    public string DisplayName => $"{Name} guardian";
}

/** The class picks the body entity; race and gender do not. */
public static class GuardianClasses
{
    public const uint Titan = 0x80BAB778, Hunter = 0x80BB1E44, Warlock = 0x80BB2C9A;
    /** Entity class of every body. */
    public const uint BodyClass = 0x80809C0F;
    public static IReadOnlyList<GuardianClass> All { get; } =
        [new(Titan, 0, "Titan"), new(Hunter, 1, "Hunter"), new(Warlock, 2, "Warlock")];
    public static GuardianClass? ForBody(uint bodyTag) => All.FirstOrDefault(value => value.BodyTag == bodyTag);
}

/** One face preset: its arrangement row and the largest art hash count of each selector. */
public sealed record GuardianPreset(int Arrangement, IReadOnlyDictionary<int, int> VariantCounts);

/** One header dye field group; Choices[i] are the dye indices of choice i (-1 dropped). Empty when the row has no list. */
public sealed record GuardianDyeList(int HeaderOffset, IReadOnlyList<IReadOnlyList<int>> Choices);

/**
 * NameHash is row +16, FNV-1 of a name such as "human_male". Race indexes GuardianAppearance.RaceNames and Gender
 * GenderNames; both are -1 when the name hash is not a known spelling.
 */
public sealed record GuardianRaceGender(int Row, uint NameHash, int Race, int Gender, IReadOnlyList<GuardianPreset> Presets,
    IReadOnlyList<GuardianDyeList> Dyes)
{
    public bool Female => Gender == 1;
}

public enum GuardianPartKind { Body, Customisation, Armor, Weapon }

/**
 * One entity to draw. ItemTag is the armor or weapon item (0 otherwise). Selector is 13/15/17/19 for customisation, else -1.
 * DyeIndices are customisation dye indices into 0x81613D24; empty means item default dyes (armor, weapon) or none (body).
 */
public sealed record GuardianPart(GuardianPartKind Kind, string Label, uint EntityTag, uint ItemTag, int Selector,
    IReadOnlyList<int> DyeIndices);

/**
 * WeaponEntity is the weapon's gameplay root (0 when no weapon or unreadable).
 * SlotNotes holds the diagnostics about one part label, keyed by that label.
 * SpawnPairs are the variant pairs the game passes to every gear slot: the gender and whether the helmet shows.
 */
public sealed record GuardianParts(uint BodyTag, IReadOnlyList<GuardianPart> Parts, uint WeaponEntity,
    IReadOnlyList<string> Diagnostics, IReadOnlySet<uint> SourceTags, IReadOnlyDictionary<string, IReadOnlyList<string>> SlotNotes,
    IReadOnlyList<(uint Key, uint Value)> SpawnPairs);

/** The race/gender customisation table and the face preset groups it names. */
public sealed class GuardianAppearance
{
    public const uint RaceGenderTable = 0x81327D65, ArrangementTable = 0x81613D23, ArtMap = 0x80EC3F61,
        RuntimeMap = 0x80EC3F60, GearTable = 0x81613D27, DyeTable = 0x81613D24;
    /** Gear-preview placeholders: one row per class byte; row +8 lists item indices. */
    public const uint ClassDefaultsTable = 0x81327CE3;
    private const uint ClassDefaultsRowClass = 0x808074FA, ClassDefaultsItemClass = 0x808087DB;
    private const int ClassDefaultsStride = 32, ClassDefaultsItems = 8;
    // Item index rows are 24 bytes with the definition tag at +16; definition +184 is the bucket.
    internal const int ItemIndexStride = 24, ItemIndexTagField = 16, BucketField = 184;
    // Buckets 3 to 7 are armor slots 0 to 4.
    private const int FirstArmorBucket = 3;
    /** Armor slots, helmet to class item, in GuardianLoadout.Armor order. */
    public const int ArmorSlots = 5;
    // The world draws the first selector of each pair. The part names are assumed.
    public const int HeadSelector = 13, HairSelector = 15, ThirdSelector = 17, MarkingSelector = 19;
    public static IReadOnlyList<string> RaceNames { get; } = ["Human", "Awoken", "Exo"];
    public static IReadOnlyList<string> GenderNames { get; } = ["Male", "Female"];
    private const uint RowClass = 0x80805858, PresetClass = 0x80805867, DyeClass = 0x80805864, TripleClass = 0x80805861;
    private const int RowStride = 432, PresetStride = 24, DyeStride = 24, TripleStride = 28;
    // Assumed: the six row dye lists feed header +6, +8, +10, +12, +18 and +24, in that order.
    // Triple lists read these i16 fields; the game never uses header +18 and +20, so +320 keeps its third.
    private static readonly (int Field, int Header, int[]? Triple)[] DyeFields =
        [(192, 6, null), (224, 8, null), (256, 10, null), (288, 12, [0, 2, 4]), (320, 18, [4]), (352, 24, null)];
    private const int NameField = 16;
    // Row names such as "human_male" hash with FNV-1.
    private static readonly Dictionary<uint, (int Race, int Gender)> RowNames =
        Enumerable.Range(0, 3).SelectMany(race => Enumerable.Range(0, 2).Select(gender => (race, gender)))
            .ToDictionary(pair => StringBankWriter.Hash($"{RaceNames[pair.race]}_{GenderNames[pair.gender]}".ToLowerInvariant()));
    private readonly Dictionary<int, IReadOnlyList<(int Selector, uint[] Hashes)>> groups;
    private readonly GuardianArtTables tables;
    private readonly IReadOnlyList<uint[]> previewDefaults;
    private readonly string? previewDefaultsError;

    public IReadOnlyList<GuardianRaceGender> Rows { get; }

    private GuardianAppearance(IReadOnlyList<GuardianRaceGender> rows,
        Dictionary<int, IReadOnlyList<(int Selector, uint[] Hashes)>> groups, GuardianArtTables tables,
        (IReadOnlyList<uint[]> Defaults, string? Error) previews)
    {
        Rows = rows;
        this.groups = groups;
        this.tables = tables;
        (previewDefaults, previewDefaultsError) = previews;
    }

    /** The armor slot 0..4 of an item definition, in GuardianLoadout.Armor order; -1 for any other item. */
    public static int ArmorSlot(byte[] definition) =>
        definition.Length > BucketField && (uint)(definition[BucketField] - FirstArmorBucket) < ArmorSlots
            ? definition[BucketField] - FirstArmorBucket : -1;

    /** The class's gear-preview placeholder for armor slot 0..4, in GuardianLoadout.Armor order; 0 when there is none. */
    public uint PreviewDefault(byte classByte, int slot) =>
        classByte < previewDefaults.Count && (uint)slot < ArmorSlots ? previewDefaults[classByte][slot] : 0;

    /** Reads the race/gender table and each preset's groups once. Throws InvalidDataException on a bad table. */
    public static GuardianAppearance Read(PackageCatalog catalog, CancellationToken token = default)
    {
        var table = PackageHashMap.ReadStructured(catalog, RaceGenderTable);
        var tables = new GuardianArtTables(catalog);
        int[] rows = table.Rows(8, RowClass, RowStride);
        if (rows.Length == 0) throw new InvalidDataException("The race/gender table has no rows.");
        var groups = new Dictionary<int, IReadOnlyList<(int Selector, uint[] Hashes)>>();
        var result = new List<GuardianRaceGender>();
        for (int index = 0; index < rows.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            int row = rows[index];
            var presets = new List<GuardianPreset>();
            foreach (int preset in table.Rows(row + 64, PresetClass, PresetStride))
            {
                int arrangement = checked((int)table.U32(preset));
                if (!groups.TryGetValue(arrangement, out var found))
                    groups.Add(arrangement, found = GuardianArt.Groups(tables.Arrangements, tables.Arrangement(arrangement)));
                presets.Add(new(arrangement, found.GroupBy(group => group.Selector)
                    .ToDictionary(group => group.Key, group => group.Max(value => value.Hashes.Length))));
            }
            var dyes = DyeFields.Select(field => new GuardianDyeList(field.Header, ReadDyes(table, row + field.Field, field.Triple)))
                .ToArray();
            uint name = table.U32(row + NameField);
            var (race, gender) = RowNames.TryGetValue(name, out var known) ? known : (-1, -1);
            result.Add(new(index, name, race, gender, presets, dyes));
        }
        return new(result, groups, tables, ReadPreviewDefaults(catalog));
    }

    /**
     * Placeholder tags per class byte, by armor slot. Each item lands in the slot of its bucket.
     * @return No rows and the reason when the table or the item index is unreadable; an empty row and
     * the reason for a row that does not name its class.
     */
    private static (IReadOnlyList<uint[]> Defaults, string? Error) ReadPreviewDefaults(PackageCatalog catalog)
    {
        try
        {
            var table = PackageHashMap.ReadStructured(catalog, ClassDefaultsTable);
            var index = PackageHashMap.ReadStructured(catalog, InvestmentTags.ItemIndex);
            int[] items = index.RowsOfAnyClass(8, ItemIndexStride);
            var result = new List<uint[]>();
            string? error = null;
            foreach (int row in table.Rows(8, ClassDefaultsRowClass, ClassDefaultsStride))
            {
                var slots = new uint[ArmorSlots];
                result.Add(slots);
                // Row +0 is FNV-1 of the class name; the row index is assumed to be the class byte.
                var guardianClass = GuardianClasses.All.FirstOrDefault(value => value.ClassByte == result.Count - 1);
                if (guardianClass is null || table.U32(row) != StringBankWriter.Hash(guardianClass.Name.ToLowerInvariant()))
                {
                    error ??= $"placeholder row {result.Count - 1} does not name the {guardianClass?.Name ?? "class"}";
                    continue;
                }
                foreach (int at in table.Rows(row + ClassDefaultsItems, ClassDefaultsItemClass, 2))
                {
                    int item = table.U16(at);
                    if (item >= items.Length) throw new InvalidDataException($"Item index {item} is outside the item index.");
                    uint tag = index.U32(items[item] + ItemIndexTagField);
                    if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != InvestmentTags.ItemClass)
                        throw new InvalidDataException($"Item index {item} does not name an item definition.");
                    byte[] definition = catalog.Read(tag);
                    int slot = ArmorSlot(definition);
                    if (slot >= 0 && slots[slot] == 0) slots[slot] = tag;
                }
            }
            return (result, error);
        }
        catch (Exception error) when (Recoverable(error)) { return ([], error.Message); }
    }

    /** Dye choices of one list; a triple keeps the i16 fields at these offsets, and -1 entries are dropped. */
    private static IReadOnlyList<IReadOnlyList<int>> ReadDyes(TagData table, int field, int[]? triple)
    {
        var result = new List<IReadOnlyList<int>>();
        foreach (int at in table.Rows(field, triple is null ? DyeClass : TripleClass, triple is null ? DyeStride : TripleStride))
        {
            int[] values = triple is null ? [(int)table.U32(at)] : [.. triple.Select(offset => (int)table.I16(at + offset))];
            result.Add(values.Where(value => value >= 0).ToArray());
        }
        return result;
    }

    /** Clamps every position to the row's lists, so the loadout is always drawable. */
    public GuardianLoadout Clamp(GuardianLoadout loadout)
    {
        int rowIndex = Math.Clamp(loadout.RaceGender, 0, Rows.Count - 1);
        var row = Rows[rowIndex];
        int preset = Fit(loadout.Preset, row.Presets.Count);
        var counts = row.Presets.Count == 0 ? null : row.Presets[preset].VariantCounts;
        return loadout with
        {
            RaceGender = rowIndex,
            Preset = preset,
            Variant2 = Fit(loadout.Variant2, counts?.GetValueOrDefault(HairSelector) ?? 0),
            Variant4 = Fit(loadout.Variant4, counts?.GetValueOrDefault(MarkingSelector) ?? 0),
            Dye06 = Fit(loadout.Dye06, row.Dyes[0].Choices.Count),
            Dye08 = Fit(loadout.Dye08, row.Dyes[1].Choices.Count),
            Dye10 = Fit(loadout.Dye10, row.Dyes[2].Choices.Count),
            Dye12 = Fit(loadout.Dye12, row.Dyes[3].Choices.Count),
            Dye18 = Fit(loadout.Dye18, row.Dyes[4].Choices.Count),
            Dye24 = Fit(loadout.Dye24, row.Dyes[5].Choices.Count)
        };
    }

    private static int Fit(int value, int count) => Math.Clamp(value, 0, Math.Max(0, count - 1));

    /** The entities and dyes of one loadout. One failing part adds a diagnostic and is left out. Never throws for one part. */
    public GuardianParts Resolve(PackageCatalog catalog, uint bodyTag, GuardianLoadout loadout, CancellationToken token = default)
    {
        var guardianClass = GuardianClasses.ForBody(bodyTag)
            ?? throw new ArgumentException($"0x{bodyTag:X8} is not a guardian body.", nameof(bodyTag));
        loadout = Clamp(loadout);
        var row = Rows[loadout.RaceGender];
        var parts = new List<GuardianPart>();
        var diagnostics = new List<string>();
        var slotNotes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        // "label: text"; a detail goes on its own line, which the page shows only on hover.
        void Note(string label, string text, string detail = "")
        {
            string line = detail.Length == 0 ? $"{label}: {text}" : $"{label}: {text}\n{detail}";
            diagnostics.Add(line);
            if (!slotNotes.TryGetValue(label, out var lines)) slotNotes.Add(label, lines = []);
            lines.Add(line);
        }
        var sources = new HashSet<uint> { bodyTag, RaceGenderTable, ArrangementTable, ArtMap, RuntimeMap, GearTable, DyeTable,
            ClassDefaultsTable, InvestmentTags.ItemIndex };
        // The body model holds both genders' hands; the assembler keeps the row's pair.
        parts.Add(new(GuardianPartKind.Body, "Hands", bodyTag, 0, -1, []));
        // Helmet on hides the head parts; which mode the game uses where is assumed.
        bool helmetOn = loadout.ShowHelmet && AddItem(GuardianPartKind.Armor, "Helmet", Pick(0, "Helmet", loadout.Helmet)) != 0;
        if (!helmetOn && row.Presets.Count != 0)
        {
            var preset = groups[row.Presets[loadout.Preset].Arrangement];
            int[] positions = loadout.DyePositions;
            IReadOnlyList<int> Dyes(params int[] lists) => lists.Where(list => row.Dyes[list].Choices.Count != 0)
                .SelectMany(list => row.Dyes[list].Choices[positions[list]]).ToArray();
            AddCustomisation(HeadSelector, "Head", 0, Dyes(0, 1, 2));
            AddCustomisation(HairSelector, "Hair", loadout.Variant2, Dyes(3));
            AddCustomisation(ThirdSelector, "Eyebrows", 0, Dyes(4));
            AddCustomisation(MarkingSelector, "Markings", loadout.Variant4, Dyes(5));

            // The first group of the selector that is longer than the variant, as the game looks it up.
            void AddCustomisation(int selector, string label, int variant, IReadOnlyList<int> dyes)
            {
                token.ThrowIfCancellationRequested();
                var hashes = preset.FirstOrDefault(group => group.Selector == selector && group.Hashes.Length > variant).Hashes;
                // No group (selector 17 on Exo) or an empty hash (markings entry 0) draws nothing.
                if (hashes is null || GuardianArt.IsEmpty(hashes[variant])) return;
                try { parts.Add(new(GuardianPartKind.Customisation, label, tables.Entity(hashes[variant]), 0, selector, dyes)); }
                catch (Exception error) when (Recoverable(error)) { Note(label, "does not load", error.Message); }
            }
        }
        (string Label, uint Item)[] armor = [("Gauntlets", loadout.Gauntlets), ("Chest", loadout.Chest), ("Legs", loadout.Legs),
            ("Class item", loadout.ClassArmor)];
        for (int slot = 1; slot <= armor.Length; slot++)
            AddItem(GuardianPartKind.Armor, armor[slot - 1].Label, Pick(slot, armor[slot - 1].Label, armor[slot - 1].Item));
        uint weaponEntity = 0;
        if (loadout.Weapon != 0)
        {
            AddItem(GuardianPartKind.Weapon, "Weapon", loadout.Weapon);
            try { weaponEntity = GuardianArt.GameplayEntity(tables, catalog.Read(loadout.Weapon)); }
            catch (Exception error) when (Recoverable(error)) { Note("Weapon", "no gameplay entity", error.Message); }
            if (weaponEntity != 0) sources.Add(weaponEntity);
        }
        sources.UnionWith(parts.Select(part => part.EntityTag));
        // 0x7FF742BFC6C0 also passes key 0x6FCBCE96; the armor variant has no such parameter, so it is left out.
        (uint, uint)[] pairs =
        [
            (EntityPartLabels.GenderKey, row.Female ? EntityPartLabels.Female : EntityPartLabels.Male),
            (EntityPartLabels.HelmetShownKey, helmetOn ? EntityPartLabels.True : EntityPartLabels.False)
        ];
        return new(bodyTag, parts, weaponEntity, diagnostics, sources,
            slotNotes.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value, StringComparer.Ordinal), pairs);

        // 0 picks the class's placeholder; a missing one leaves the slot empty with a note.
        uint Pick(int slot, string label, uint item)
        {
            if (item != 0) return item;
            uint placeholder = PreviewDefault(guardianClass.ClassByte, slot);
            if (placeholder == 0)
                Note(label, "no default item", $"The {guardianClass.Name} has no gear-preview placeholder for this slot."
                    + (previewDefaultsError is null ? "" : $" {previewDefaultsError}"));
            return placeholder;
        }

        // Returns the number of entities added; 0 when the item is empty, has no art or fails.
        int AddItem(GuardianPartKind kind, string label, uint item)
        {
            if (item == 0) return 0;
            token.ThrowIfCancellationRequested();
            sources.Add(item);
            try
            {
                var entities = GuardianArt.ItemEntities(tables, catalog.Read(item), guardianClass.ClassByte, row.Female);
                if (entities.Count == 0) Note(label, "no art for this class", $"Item 0x{item:X8} has no art for this class and gender.");
                parts.AddRange(entities.Select(entity => new GuardianPart(kind, label, entity, item, -1, [])));
                return entities.Count;
            }
            catch (Exception error) when (Recoverable(error))
            {
                Note(label, "item unreadable", $"Item 0x{item:X8}: {error.Message}");
                return 0;
            }
        }
    }

    internal static bool Recoverable(Exception error) => error is InvalidDataException or KeyNotFoundException
        or OverflowException or ArgumentException or IndexOutOfRangeException;
}
