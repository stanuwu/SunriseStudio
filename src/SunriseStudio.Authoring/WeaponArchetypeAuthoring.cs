using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

public sealed record WeaponArtVariant(int Selector, uint WrapperTag, uint EntityTag,
    IReadOnlyList<uint> ArtHashes);
public sealed record WeaponArchetypeSource(ushort GearIndex, ushort ArtIndex, uint EntityTag,
    IReadOnlyList<WeaponArtVariant> Variants);
public sealed record PreparedWeaponArchetype(byte[] ItemDefinition,
    IReadOnlyList<ProjectAssetAddition> Assets, IReadOnlyDictionary<uint, byte[]> Changes,
    ushort GearIndex, ushort ArtIndex, uint EntityTag);

/** A weapon owns new gear and art rows while its native firing components remain shared. */
public static class WeaponArchetypeAuthoring
{
    public const uint GearTableTag = 0x81613D27, ArrangementTableTag = 0x81613D23;
    public const uint GearDefinitionTableTag = 0x81319326;
    public const uint ArrangementDefinitionTableTag = 0x81319329;
    public const uint ArtMapTag = 0x80EC3F61, RuntimeMapTag = 0x80EC3F60;
    private const uint GearTableClass = SGearTableTag0x81613D27.Class, GearRowClass = SGearRow.Class;
    private const uint GearDefinitionClass = SGearDefinitionTableTag0x81319326.Class, GearDefinitionRowClass = SGearDefinitionRow.Class;
    private const uint ArrangementDefinitionClass = SArrangementDefinitionTableTag0x81319329.Class,
        ArrangementDefinitionRowClass = SArrangementDefinitionRow.Class;
    private const uint ArrangementTableClass = SArrangementTableTag0x81613D23.Class, ArrangementRowClass = SArrangementRow.Class;
    private const uint ArtMapClass = SArtNameMapTag0x80EC3F61.Class, ArtMapRowClass = SArtNameRow.Class;
    private const uint RuntimeMapClass = SRuntimeNameMap.Class, RuntimeMapRowClass = SRuntimeNameRow.Class;
    private const uint HolderClass = SArrangementHolder.Class, ArtHashClass = SArtHash.Class;
    private const uint EntityClass = SEntity.Class, WrapperClass = SArtWrapper.Class;
    // Native readers use signed 16-bit row indices.
    private const int MaximumRows = short.MaxValue + 1;

    /** Art variants resolve through the installed art map, not the gameplay name map. */
    public static WeaponArchetypeSource Read(PackageCatalog catalog, byte[] itemDefinition)
    {
        var item = new Bytes(itemDefinition);
        int art = ArtBlock(item);
        if (art < 0) throw new InvalidDataException("This weapon has no art block.");
        ushort gearIndex = item.Field<ItemTranslationBlock, ushort>(art, nameof(ItemTranslationBlock.GearIndex));
        var itemRows = item.Rows<ItemTranslationBlock, ItemArtRow>(art, nameof(ItemTranslationBlock.ArtRows), ItemArtRow.Class);
        var arrangements = Enumerable.Range(0, itemRows.Count)
            .Select(index => item.At<ItemArtRow>(itemRows.Data + index * Unsafe.SizeOf<ItemArtRow>()).ArrangementRow).Distinct().ToArray();
        if (arrangements.Length != 1)
            throw new NotSupportedException("Choose a weapon with one shared art arrangement.");
        ushort arrangement = arrangements[0];
        var gear = Read(catalog, GearTableTag, GearTableClass);
        var gearRows = GearRows(gear);
        int gearRow = Row<SGearRow>(gearRows, gearIndex);
        var names = Read(catalog, RuntimeMapTag, RuntimeMapClass);
        uint entity = Resolve(names, RuntimeMapRowClass, gear.At<SGearRow>(gearRow).GameplayRuntimeName);
        Read(catalog, entity, EntityClass);
        var table = Read(catalog, ArrangementTableTag, ArrangementTableClass);
        var rows = ArrangementRows(table);
        int row = Row<SArrangementRow>(rows, arrangement);
        var holders = Holders(table, row);
        var artMap = Read(catalog, ArtMapTag, ArtMapClass);
        var variants = new List<WeaponArtVariant>();
        for (int i = 0; i < holders.Count; i++)
        {
            int group = Group(table, holders, i);
            if (group < 0) throw new InvalidDataException("An art variant has no group.");
            int selector = table.Field<ArrangementArtGroup, byte>(group, nameof(ArrangementArtGroup.Selector));
            var hashes = table.Rows<ArrangementArtGroup, SArtHash>(group, nameof(ArrangementArtGroup.H3265b065), ArtHashClass);
            if (hashes.Count == 0) throw new InvalidDataException("An art variant has no model.");
            uint[] values = Column<SArtHash>(table, hashes, value => value.ArtName);
            uint wrapper = Resolve(artMap, ArtMapRowClass, values[0]);
            var wrapperBytes = Read(catalog, wrapper, WrapperClass);
            uint modelEntity = wrapperBytes.Field<SArtWrapper, uint>(0, nameof(SArtWrapper.ArtTag));
            Read(catalog, modelEntity, EntityClass);
            variants.Add(new(selector, wrapper, modelEntity, values));
        }
        if (variants.Count == 0 || variants.Select(value => value.Selector).Distinct().Count()
            != variants.Count)
            throw new InvalidDataException("The weapon art selectors are empty or repeated.");
        return new(gearIndex, arrangement, entity, variants);
    }

    /** The gear row's key that selects the weapon's row in each presentation and HUD config. */
    public static uint GearKey(PackageCatalog catalog, WeaponArchetypeSource art)
    {
        var gear = Read(catalog, GearTableTag, GearTableClass);
        return gear.At<SGearRow>(Row<SGearRow>(GearRows(gear), art.GearIndex)).GearKeyPresentationHudSelector;
    }

    /** The gear row's key that selects the weapon's group in its translation config. */
    public static uint TypeKey(PackageCatalog catalog, WeaponArchetypeSource art, byte[]? gearTable = null) =>
        TypeKey(gearTable ?? catalog.Read(GearTableTag), art.GearIndex);

    public static uint TypeKey(byte[] gearTable, ushort gearIndex)
    {
        var gear = new Bytes(gearTable);
        return gear.At<SGearRow>(Row<SGearRow>(GearRows(gear), gearIndex)).TypeKeyTranslationGroup;
    }

    public static int GearRowCount(byte[] gearTable) => GearRows(new Bytes(gearTable)).Count;

    /** The gear row an item definition names, or null when it has no art block. */
    public static ushort? GearIndex(byte[] itemDefinition)
    {
        var item = new Bytes(itemDefinition);
        int art = ArtBlock(item);
        return art < 0 ? null : item.Field<ItemTranslationBlock, ushort>(art, nameof(ItemTranslationBlock.GearIndex));
    }

    /** Sets the gear row's type key, which moves the weapon to another group of its translation config. */
    public static byte[] SetTypeKey(byte[] gearTable, ushort gearIndex, uint key)
    {
        var gear = new Bytes(gearTable);
        int row = Row<SGearRow>(GearRows(gear), gearIndex);
        byte[] result = gearTable.ToArray();
        TagView.Edit<SGearRow>(result, row).TypeKeyTranslationGroup = key;
        return result;
    }

    /** Every art variant needs an imported entity; an imported gameplay root keeps its bindings. */
    public static PreparedWeaponArchetype Prepare(PackageCatalog catalog, byte[] itemDefinition,
        uint itemHash, string name, ushort packageId, int firstEntry,
        IReadOnlyDictionary<int, uint> variantEntities, uint? gameplayEntityTag = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(variantEntities);
        var source = Read(catalog, itemDefinition);
        if (!source.Variants.Select(value => value.Selector).Order()
            .SequenceEqual(variantEntities.Keys.Order()))
            throw new InvalidDataException("Import a model for each weapon art variant.");
        int wrapperStart = gameplayEntityTag.HasValue ? 0 : 1;
        int count = checked(wrapperStart + source.Variants.Count);
        if (!PackageWriter.IsNewPackageId(packageId) || catalog.ContainsInstalledPackage(packageId)
            || firstEntry < 0 || firstEntry > TagHashes.EntriesPerPackage - count)
            throw new InvalidDataException("The weapon needs unused entries in a new package.");
        uint Tag(int index) => TagHashes.Make(packageId, firstEntry + index);
        for (int index = 0; index < count; index++)
            if (catalog.ContainsTag(Tag(index)))
                throw new InvalidDataException("A weapon destination entry is already used.");
        foreach (uint entity in variantEntities.Values) Read(catalog, entity, EntityClass);
        if (gameplayEntityTag is { } authored)
        {
            Read(catalog, authored, EntityClass);
            catalog.TryGetEntry(authored, out var entry);
            if (catalog.ContainsInstalledPackage(entry.PackageId) || entry.PackageId != packageId)
                throw new InvalidDataException("The gameplay root must belong to the new weapon package.");
        }
        uint gameplayEntity = gameplayEntityTag ?? Tag(0);

        var gear = Read(catalog, GearTableTag, GearTableClass);
        var gearRows = GearRows(gear);
        var definitions = Read(catalog, GearDefinitionTableTag, GearDefinitionClass);
        var definitionRows = GearDefinitionRows(definitions);
        RequireIdentityPair(Column<SGearRow>(gear, gearRows, row => row.ItemHash),
            Column<SGearDefinitionRow>(definitions, definitionRows, row => row.ItemHash), complete: true);
        var arrangement = Read(catalog, ArrangementTableTag, ArrangementTableClass);
        var artRows = ArrangementRows(arrangement);
        var arrangementDefinitions = Read(catalog, ArrangementDefinitionTableTag, ArrangementDefinitionClass);
        var arrangementDefinitionRows = ArrangementDefinitionRows(arrangementDefinitions);
        RequireIdentityPair(Column<SArrangementRow>(arrangement, artRows, row => row.ItemHash),
            Column<SArrangementDefinitionRow>(arrangementDefinitions, arrangementDefinitionRows, row => row.ItemHash),
            complete: true);
        if (gearRows.Count >= MaximumRows || artRows.Count >= MaximumRows)
            throw new InvalidDataException("The weapon art tables have no reachable row left.");
        RequireUnusedHash(Column<SGearRow>(gear, gearRows, row => row.ItemHash), itemHash);
        RequireUnusedHash(Column<SArrangementRow>(arrangement, artRows, row => row.ItemHash), itemHash);
        byte[] names = Read(catalog, RuntimeMapTag, RuntimeMapClass).Data;
        byte[] artMap = Read(catalog, ArtMapTag, ArtMapClass).Data;
        uint gameplayName = NewName(names, RuntimeMapRowClass,
            $"sunrise.weapon.{packageId:X4}.{firstEntry}.{name}");
        names = AppendName(names, RuntimeMapRowClass, gameplayName, gameplayEntity);
        var additions = new List<ProjectAssetAddition>();
        if (gameplayEntityTag is null)
            Add(source.EntityTag, catalog.Read(source.EntityTag), 0, "archetype");

        int originalGear = Row<SGearRow>(gearRows, source.GearIndex);
        byte[] gearRow = gear.Slice(originalGear, Unsafe.SizeOf<SGearRow>());
        ref var newGear = ref TagView.Edit<SGearRow>(gearRow);
        newGear.ItemHash = itemHash; newGear.GameplayRuntimeName = gameplayName;
        byte[] changedGear = InvestmentItemClone.AppendRow(gear.Data, TagView.FieldOffset<SGearTableTag0x81613D27>(nameof(SGearTableTag0x81613D27.Table)),
            Unsafe.SizeOf<SGearRow>(), gearRow, GearRowArrays, originalGear);
        byte[] definitionRow = definitions.Slice(Row<SGearDefinitionRow>(definitionRows, source.GearIndex), Unsafe.SizeOf<SGearDefinitionRow>());
        TagView.Edit<SGearDefinitionRow>(definitionRow).ItemHash = itemHash;
        byte[] changedDefinitions = InvestmentItemClone.AppendRow(definitions.Data,
            TagView.FieldOffset<SGearDefinitionTableTag0x81319326>(nameof(SGearDefinitionTableTag0x81319326.Table)), Unsafe.SizeOf<SGearDefinitionRow>(), definitionRow);

        int originalArt = Row<SArrangementRow>(artRows, source.ArtIndex);
        byte[] artRow = arrangement.Slice(originalArt, Unsafe.SizeOf<SArrangementRow>());
        TagView.Edit<SArrangementRow>(artRow).ItemHash = itemHash;
        byte[] arrangementIdentity = new byte[Unsafe.SizeOf<SArrangementDefinitionRow>()];
        TagView.Edit<SArrangementDefinitionRow>(arrangementIdentity).ItemHash = itemHash;
        byte[] changedArrangementDefinitions = InvestmentItemClone.AppendRow(arrangementDefinitions.Data,
            TagView.FieldOffset<SArrangementDefinitionTableTag0x81319329>(nameof(SArrangementDefinitionTableTag0x81319329.Table)), Unsafe.SizeOf<SArrangementDefinitionRow>(),
            arrangementIdentity);
        var oldHolders = Holders(arrangement, originalArt);
        // The new row's holders, groups and hashes form one block of self-relative pointers.
        var output = new List<byte>();
        int newHolders = AppendArray(output, -1, HolderClass, oldHolders.Count, Unsafe.SizeOf<SArrangementHolder>());
        for (int i = 0; i < source.Variants.Count; i++)
        {
            var variant = source.Variants[i];
            int originalGroup = Group(arrangement, oldHolders, i);
            Align(output, ArrangementArtGroup.Align);
            int group = output.Count;
            output.AddRange(arrangement.Slice(originalGroup, Unsafe.SizeOf<ArrangementArtGroup>()));
            var block = CollectionsMarshal.AsSpan(output);
            TagView.SetTarget(block, in TagView.At<SArrangementHolder>(block, newHolders + i * Unsafe.SizeOf<SArrangementHolder>()).Group, group);
            int hashField = TagView.OffsetOf(block, in TagView.At<ArrangementArtGroup>(block, group).H3265b065);
            int hashes = AppendArray(output, hashField, ArtHashClass, variant.ArtHashes.Count, Unsafe.SizeOf<SArtHash>());
            uint hash = NewName(artMap, ArtMapRowClass,
                $"sunrise.weapon.art.{packageId:X4}.{firstEntry}.{variant.Selector}.{name}");
            artMap = AppendName(artMap, ArtMapRowClass, hash, Tag(i + wrapperStart));
            block = CollectionsMarshal.AsSpan(output);
            for (int j = 0; j < variant.ArtHashes.Count; j++) TagView.Edit<SArtHash>(block, hashes + j * Unsafe.SizeOf<SArtHash>()).ArtName = hash;
            byte[] wrapper = Read(catalog, variant.WrapperTag, WrapperClass).Data.ToArray();
            EditField<SArtWrapper, uint>(wrapper, 0, nameof(SArtWrapper.ArtTag)) = variantEntities[variant.Selector];
            Add(variant.WrapperTag, wrapper, i + wrapperStart, $"art-{variant.Selector}");
        }
        // Block before rows: the rows stay last, so the next weapon reuses their bytes and leaves no dead copy.
        var (changedArt, blocks) = InvestmentItemClone.AppendRows(arrangement.Data,
            [new(TagView.FieldOffset<SArrangementTableTag0x81613D23>(nameof(SArrangementTableTag0x81613D23.Table)), Unsafe.SizeOf<SArrangementRow>(), [artRow])
                { RowArrays = [TagView.FieldOffset<SArrangementRow>("GearSet.Regions")], RowSources = [originalArt] }],
            [output.ToArray()], catalog.ReadOriginal(ArrangementTableTag).Length);
        int newRow = Row<SArrangementRow>(ArrangementRows(new Bytes(changedArt)), artRows.Count);
        int regions = TagView.OffsetOf(changedArt, in TagView.At<SArrangementRow>(changedArt, newRow).GearSet.Regions);
        TagArray.WriteField(changedArt, regions, oldHolders.Count, blocks[0] + newHolders - TagArray.HeaderSize);
        EditField<SArrangementTableTag0x81613D23, ulong>(changedArt, 0, nameof(SArrangementTableTag0x81613D23.TagByteLength)) = (ulong)changedArt.Length;
        byte[] item = itemDefinition.ToArray();
        var itemReader = new Bytes(item);
        int artBlock = ArtBlock(itemReader);
        if (artBlock < 0) throw new InvalidDataException("This weapon has no art block.");
        EditField<ItemTranslationBlock, ushort>(item, artBlock, nameof(ItemTranslationBlock.GearIndex)) = (ushort)gearRows.Count;
        var itemArts = itemReader.Rows<ItemTranslationBlock, ItemArtRow>(artBlock, nameof(ItemTranslationBlock.ArtRows), ItemArtRow.Class);
        for (int index = 0; index < itemArts.Count; index++)
            TagView.Edit<ItemArtRow>(item, itemArts.Data + index * Unsafe.SizeOf<ItemArtRow>()).ArrangementRow = (ushort)artRows.Count;
        return new(item, additions, new Dictionary<uint, byte[]>
        {
            [GearTableTag] = changedGear, [ArrangementTableTag] = changedArt,
            [GearDefinitionTableTag] = changedDefinitions,
            [ArrangementDefinitionTableTag] = changedArrangementDefinitions,
            [ArtMapTag] = artMap, [RuntimeMapTag] = names
        }, (ushort)gearRows.Count, (ushort)artRows.Count, gameplayEntity);

        void Add(uint baseTag, byte[] bytes, int index, string suffix)
        {
            catalog.TryGetEntry(baseTag, out var entry);
            additions.Add(new($"{name}.{suffix}", bytes,
                new(packageId, entry.Reference, entry.TypeInfo, $"{name} {suffix}",
                    firstEntry + index), baseTag));
        }
    }

    /** A legacy project may append its one missing gear identity without moving existing indices. */
    // TODO: call from a Repair action in ExoticWeaponAuthoring.Repair; nothing calls this yet.
    public static byte[] PrepareGearIdentityRepair(PackageCatalog catalog, byte[] itemDefinition,
        uint expectedHash, ushort templateGearIndex)
    {
        var profile = Read(catalog, itemDefinition);
        var gear = Read(catalog, GearTableTag, GearTableClass);
        var gearRows = GearRows(gear);
        var definitions = Read(catalog, GearDefinitionTableTag, GearDefinitionClass);
        var rows = GearDefinitionRows(definitions);
        RequireIdentityPair(Column<SGearRow>(gear, gearRows, row => row.ItemHash),
            Column<SGearDefinitionRow>(definitions, rows, row => row.ItemHash), complete: false);
        if (gear.At<SGearRow>(Row<SGearRow>(gearRows, profile.GearIndex)).ItemHash != expectedHash)
            throw new InvalidDataException("The weapon gear identity changed before repair.");
        if (rows.Count == gearRows.Count) return definitions.Data.ToArray();
        if (profile.GearIndex != rows.Count || gearRows.Count != rows.Count + 1)
            throw new InvalidDataException("Repair needs exactly one missing gear identity at the end of the table.");
        byte[] row = definitions.Slice(Row<SGearDefinitionRow>(rows, templateGearIndex), Unsafe.SizeOf<SGearDefinitionRow>());
        TagView.Edit<SGearDefinitionRow>(row).ItemHash = expectedHash;
        return InvestmentItemClone.AppendRow(definitions.Data, TagView.FieldOffset<SGearDefinitionTableTag0x81319326>(nameof(SGearDefinitionTableTag0x81319326.Table)),
            Unsafe.SizeOf<SGearDefinitionRow>(), row);
    }

    /** An arrangement needs a parallel identity before its new index can be used at runtime. */
    // TODO: call from a Repair action in ExoticWeaponAuthoring.Repair; nothing calls this yet.
    public static byte[] PrepareArrangementIdentityRepair(PackageCatalog catalog, byte[] itemDefinition,
        uint expectedHash)
    {
        var profile = Read(catalog, itemDefinition);
        var arrangement = Read(catalog, ArrangementTableTag, ArrangementTableClass);
        var artRows = ArrangementRows(arrangement);
        var definitions = Read(catalog, ArrangementDefinitionTableTag, ArrangementDefinitionClass);
        var rows = ArrangementDefinitionRows(definitions);
        RequireIdentityPair(Column<SArrangementRow>(arrangement, artRows, row => row.ItemHash),
            Column<SArrangementDefinitionRow>(definitions, rows, row => row.ItemHash), complete: false);
        if (arrangement.At<SArrangementRow>(Row<SArrangementRow>(artRows, profile.ArtIndex)).ItemHash != expectedHash)
            throw new InvalidDataException("The weapon arrangement identity changed before repair.");
        if (rows.Count == artRows.Count) return definitions.Data.ToArray();
        if (profile.ArtIndex != rows.Count || artRows.Count != rows.Count + 1)
            throw new InvalidDataException("Repair needs one missing arrangement identity at the end of the table.");
        byte[] row = new byte[Unsafe.SizeOf<SArrangementDefinitionRow>()];
        TagView.Edit<SArrangementDefinitionRow>(row).ItemHash = expectedHash;
        return InvestmentItemClone.AppendRow(definitions.Data, TagView.FieldOffset<SArrangementDefinitionTableTag0x81319329>(nameof(SArrangementDefinitionTableTag0x81319329.Table)),
            Unsafe.SizeOf<SArrangementDefinitionRow>(), row);
    }

    /** Runtime identities and presentation rows share their index and definition hash. */
    private static void RequireIdentityPair(uint[] presentation, uint[] definitions, bool complete)
    {
        if (definitions.Length > presentation.Length || complete && definitions.Length != presentation.Length)
            throw new InvalidDataException("The weapon identity and presentation tables have different row counts.");
        for (int index = 0; index < definitions.Length; index++)
            if (definitions[index] != presentation[index])
                throw new InvalidDataException("The weapon identity and presentation indices disagree.");
    }

    /**
     * A copied table keeps every old nested pointer at its original target.
     * @param field Descriptor to point at the new array, or -1 when the caller writes it.
     * @return Offset of the first element; the header is 16 bytes before it.
     */
    private static int AppendArray(List<byte> output, int field, uint type, int count, int stride)
    {
        Align(output, TagArray.HeaderAlignment);
        output.AddRange(new byte[TagArray.HeaderAlignment]);
        int header = output.Count;
        output.AddRange(new byte[checked(TagArray.HeaderSize + count * stride)]);
        var span = CollectionsMarshal.AsSpan(output);
        TagArray.WriteHeader(span, header, count, type);
        if (field >= 0) TagArray.WriteField(span, field, count, header);
        return header + TagArray.HeaderSize;
    }

    private static void Align(List<byte> output, int alignment)
    { while (output.Count % alignment != 0) output.Add(0); }

    /** Name lookup uses unsigned ascending keys and admits no duplicate identity. */
    private static byte[] AppendName(byte[] data, uint type, uint hash, uint tag)
    {
        var reader = new Bytes(data);
        var rows = NameRows(reader, type);
        RequireUnusedHash(NameHashes(reader, type, rows), hash);
        byte[] row = new byte[NameStride(type)]; PutName(row, type, 0, hash, tag);
        byte[] output = InvestmentItemClone.AppendRow(data, NameField(type), NameStride(type), row);
        var destination = NameRows(new Bytes(output), type);
        var values = Enumerable.Range(0, rows.Count).Select(index => Name(reader, type, rows.Data + index * NameStride(type)))
            .Append((hash, tag)).OrderBy(value => value.Item1).ToArray();
        for (int index = 0; index < values.Length; index++)
            PutName(output, type, destination.Data + index * NameStride(type), values[index].Item1, values[index].Item2);
        return output;
    }

    private static uint NewName(byte[] data, uint type, string key)
    {
        var reader = new Bytes(data);
        return StringHash.NewDefinitionHash(key, NameHashes(reader, type, NameRows(reader, type)).ToHashSet());
    }

    private static uint Resolve(Bytes reader, uint type, uint hash)
    {
        var rows = NameRows(reader, type);
        for (int i = 0; i < rows.Count; i++)
        {
            var (name, tag) = Name(reader, type, rows.Data + i * NameStride(type));
            if (name == hash) return tag;
        }
        throw new InvalidDataException($"Weapon art name {hash:X8} does not resolve.");
    }

    private static void RequireUnusedHash(uint[] hashes, uint hash)
    {
        if (hash is 0 or uint.MaxValue or StringHash.Empty || hashes.Contains(hash))
            throw new InvalidDataException("The weapon identity is empty or already used.");
    }

    private static int Row<T>((int Count, int Data) rows, int index) where T : unmanaged =>
        index >= 0 && index < rows.Count ? checked(rows.Data + index * Unsafe.SizeOf<T>())
            : throw new InvalidDataException("The weapon art index is outside its table.");

    /** The item's art block, or -1 when it has none. */
    private static int ArtBlock(Bytes item) => item.Target<ItemDefinition>(0, nameof(ItemDefinition.TranslationItemBlock));

    private static (int Count, int Data) GearRows(Bytes gear) =>
        gear.Rows<SGearTableTag0x81613D27, SGearRow>(0, nameof(SGearTableTag0x81613D27.Table), GearRowClass);

    private static (int Count, int Data) GearDefinitionRows(Bytes definitions) =>
        definitions.Rows<SGearDefinitionTableTag0x81319326, SGearDefinitionRow>(0, nameof(SGearDefinitionTableTag0x81319326.Table), GearDefinitionRowClass);

    private static (int Count, int Data) ArrangementRows(Bytes table) =>
        table.Rows<SArrangementTableTag0x81613D23, SArrangementRow>(0, nameof(SArrangementTableTag0x81613D23.Table), ArrangementRowClass);

    private static (int Count, int Data) ArrangementDefinitionRows(Bytes definitions) =>
        definitions.Rows<SArrangementDefinitionTableTag0x81319329, SArrangementDefinitionRow>(0, nameof(SArrangementDefinitionTableTag0x81319329.Table),
            ArrangementDefinitionRowClass);

    /** The art variant holders of the arrangement row at `row`. */
    private static (int Count, int Data) Holders(Bytes table, int row) =>
        table.Rows<SArrangementRow, SArrangementHolder>(row, "GearSet.Regions", HolderClass);

    /** The group a holder points to, or -1 when it is null. */
    private static int Group(Bytes table, (int Count, int Data) holders, int index) =>
        table.Target<SArrangementHolder>(holders.Data + index * Unsafe.SizeOf<SArrangementHolder>(), "Group");

    /**
     * RowArrays rebases the word 8 bytes past each entry, so entry 0 rebases ObjectPointer.
     * TODO: pass ObjectPointer as a row pointer once the relocator takes one.
     */
    private static readonly int[] GearRowArrays =
    [
        TagView.FieldOffset<SGearRow>(nameof(SGearRow.ObjectPointer)) - TagView.FieldOffset<TagArray<Unknown>>("Relative"),
        TagView.FieldOffset<SGearRow>(nameof(SGearRow.Owned0x20))
    ];

    /** The name rows field of the runtime map or the art map; the row class picks the map. */
    private static int NameField(uint type) => type == RuntimeMapRowClass
        ? TagView.FieldOffset<SRuntimeNameMap>(nameof(SRuntimeNameMap.TagEntries))
        : TagView.FieldOffset<SArtNameMapTag0x80EC3F61>(nameof(SArtNameMapTag0x80EC3F61.TagEntries));

    private static int NameStride(uint type) =>
        type == RuntimeMapRowClass ? Unsafe.SizeOf<SRuntimeNameRow>() : Unsafe.SizeOf<SArtNameRow>();

    /** @return The writable `member` of a T at `at`; a member past the end throws ArgumentOutOfRangeException. */
    private static ref TField EditField<T, TField>(byte[] data, int at, string member)
        where T : unmanaged where TField : unmanaged =>
        ref MemoryMarshal.AsRef<TField>(data.AsSpan(at + TagView.FieldOffset<T>(member), Unsafe.SizeOf<TField>()));

    /** One u32 per row, read through the row's view. */
    private static uint[] Column<T>(Bytes reader, (int Count, int Data) rows, Func<T, uint> select) where T : unmanaged =>
        Enumerable.Range(0, rows.Count).Select(index => select(reader.At<T>(rows.Data + index * Unsafe.SizeOf<T>()))).ToArray();

    /** The name rows of the runtime map or the art map; the row class picks the map. */
    private static (int Count, int Data) NameRows(Bytes reader, uint type) => type == RuntimeMapRowClass
        ? reader.Rows<SRuntimeNameMap, SRuntimeNameRow>(0, nameof(SRuntimeNameMap.TagEntries), type)
        : reader.Rows<SArtNameMapTag0x80EC3F61, SArtNameRow>(0, nameof(SArtNameMapTag0x80EC3F61.TagEntries), type);

    private static uint[] NameHashes(Bytes reader, uint type, (int Count, int Data) rows) =>
        Enumerable.Range(0, rows.Count).Select(index => Name(reader, type, rows.Data + index * NameStride(type)).Hash).ToArray();

    /** One name row as its name hash and the tag it names. */
    private static (uint Hash, uint Tag) Name(Bytes reader, uint type, int row)
    {
        if (type == RuntimeMapRowClass)
        {
            ref readonly var runtime = ref reader.At<SRuntimeNameRow>(row);
            return (runtime.NameHash, runtime.ProgramTag);
        }
        ref readonly var art = ref reader.At<SArtNameRow>(row);
        return (art.ArtName, art.ArtWrapper);
    }

    private static void PutName(byte[] output, uint type, int row, uint hash, uint tag)
    {
        if (type == RuntimeMapRowClass)
        {
            ref var runtime = ref TagView.Edit<SRuntimeNameRow>(output, row);
            runtime.NameHash = hash; runtime.ProgramTag = tag;
            return;
        }
        ref var art = ref TagView.Edit<SArtNameRow>(output, row);
        art.ArtName = hash; art.ArtWrapper = tag;
    }

    private static Bytes Read(PackageCatalog catalog, uint tag, uint type)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != type
            || entry.Type is not (8 or 16))
            throw new InvalidDataException($"Weapon asset {tag:X8} has an unexpected type.");
        return new(catalog.Read(tag));
    }

    /** Every pointer and typed array is bounded before it supplies a row; only the field read must fit. */
    private sealed class Bytes
    {
        public byte[] Data { get; }
        public Bytes(byte[] data)
        {
            if (!TagView.HasSizeWord(data))
                throw new InvalidDataException("Weapon asset size header is invalid.");
            Data = data;
        }
        public byte[] Slice(int at, int count) { Require(at, count); return Data.AsSpan(at, count).ToArray(); }

        /** @return The whole struct at `at`; for rows that a Rows call already bounded. */
        public ref readonly T At<T>(int at) where T : unmanaged => ref TagView.Checked<T>(Data, at);

        /** @return The `member` of a T at `at`; only that member must lie inside the record. */
        public ref readonly TField Field<T, TField>(int at, string member) where T : unmanaged where TField : unmanaged
        {
            if (!TagView.FitsThrough<T>(Data, at, member)) throw new InvalidDataException("Weapon asset field leaves its record.");
            return ref TagView.Member<T, TField>(Data, at, member);
        }

        /** @return The target of the pointer `member` of a T at `at`, or -1 for null; one outside the record throws. */
        public int Target<T>(int at, string member) where T : unmanaged
        {
            long relative = Field<T, long>(at, member);
            if (relative == 0) return -1;
            int target = checked((int)(at + TagView.FieldOffset<T>(member) + relative));
            Require(target, 1);
            return target;
        }

        /**
         * An empty array needs only its count word inside the record.
         * @return The row count and first row of the array `member` of a T at `at`; a bad header, class or bound throws.
         */
        public (int Count, int Data) Rows<T, TRow>(int at, string member, uint type) where T : unmanaged where TRow : unmanaged
        {
            long count = Field<T, long>(at, member + ".Count");
            if (count == 0) return (0, 0);
            if (count < 0 || count > TagArray.MaximumCheckedCount) throw new InvalidDataException("Weapon array count is invalid.");
            int header = Target<T>(at, member + ".Relative");
            var view = new TagArrayHeader(Data, header);
            if (!view.IsWellFormed || view.Count != count || view.ElementClass != type)
                throw new InvalidDataException("Weapon array type or count changed.");
            int start = checked(header + TagArray.HeaderSize);
            Require(start, checked((int)count * Unsafe.SizeOf<TRow>()));
            return ((int)count, start);
        }

        private void Require(int at, int count)
        {
            if (at < 0 || count < 0 || at > Data.Length - count)
                throw new InvalidDataException("Weapon asset field leaves its record.");
        }
    }
}
