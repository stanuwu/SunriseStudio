using System.Buffers.Binary;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Authoring;

public sealed record WeaponArtVariant(int Selector, uint WrapperTag, uint EntityTag,
    IReadOnlyList<uint> ArtHashes)
{
    /** The variant's index among the arrangement row's art regions. */
    public int HolderIndex { get; init; }
}
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
    private const uint GearTableClass = 0x80805B78, GearRowClass = 0x80805B7C;
    private const uint GearDefinitionClass = 0x80807A5F, GearDefinitionRowClass = 0x80807A65;
    private const uint ArrangementDefinitionClass = 0x80807546, ArrangementDefinitionRowClass = 0x80807484;
    private const uint ArrangementTableClass = 0x80805DF5, ArrangementRowClass = 0x80805DFB;
    private const uint ArtMapClass = 0x808056EA, ArtMapRowClass = 0x808056EC;
    private const uint RuntimeMapClass = 0x80809780, RuntimeMapRowClass = 0x80809252;
    private const uint HolderClass = 0x80805DFE, ArtHashClass = 0x80805E01;
    private const uint EntityClass = 0x80809C0F, WrapperClass = 0x8080744A;
    private const uint ArrayMarker = 0x80809FBD;
    // Native readers use signed 16-bit row indices.
    private const int MaximumRows = short.MaxValue + 1;

    /** Art variants resolve through the installed art map, not the gameplay name map. */
    public static WeaponArchetypeSource Read(PackageCatalog catalog, byte[] itemDefinition)
    {
        var item = new Bytes(itemDefinition);
        int art = item.Relative(136);
        if (art < 0) throw new InvalidDataException("This weapon has no art block.");
        ushort gearIndex = item.U16(art + 88);
        var itemRows = item.Array(art, 0x808077B5, 4);
        var arrangements = Enumerable.Range(0, itemRows.Count)
            .Select(index => item.U16(itemRows.Data + index * 4 + 2)).Distinct().ToArray();
        if (arrangements.Length != 1)
            throw new NotSupportedException("Choose a weapon with one shared art arrangement.");
        ushort arrangement = arrangements[0];
        var gear = Read(catalog, GearTableTag, GearTableClass);
        var gearRows = gear.Array(8, GearRowClass, 48);
        int gearRow = Row(gearRows, gearIndex, 48);
        var names = Read(catalog, RuntimeMapTag, RuntimeMapClass);
        uint entity = Resolve(names, RuntimeMapRowClass, gear.U32(gearRow + 4));
        Read(catalog, entity, EntityClass);
        var table = Read(catalog, ArrangementTableTag, ArrangementTableClass);
        var rows = table.Array(8, ArrangementRowClass, 32);
        int row = Row(rows, arrangement, 32);
        var holders = table.Array(row + 16, HolderClass, 8);
        var artMap = Read(catalog, ArtMapTag, ArtMapClass);
        var variants = new List<WeaponArtVariant>();
        for (int i = 0; i < holders.Count; i++)
        {
            int group = table.Relative(holders.Data + i * 8);
            if (group < 0) throw new InvalidDataException("An art variant has no group.");
            int selector = table.U8(group);
            var hashes = table.Array(group + 8, ArtHashClass, 4);
            // A region with no art is optional and adds nothing to the weapon: no hashes, an empty first name the art map
            // does not name, or a wrapper that names no model.
            if (hashes.Count == 0) continue;
            uint[] values = Enumerable.Range(0, hashes.Count)
                .Select(index => table.U32(hashes.Data + index * 4)).ToArray();
            if (!TryResolve(artMap, ArtMapRowClass, values[0], out uint wrapper))
            {
                if (values[0] is 0 or uint.MaxValue or InvestmentItemClone.EmptyStringHash) continue;
                throw new InvalidDataException($"Weapon art name {values[0]:X8} does not resolve.");
            }
            var wrapperBytes = Read(catalog, wrapper, WrapperClass);
            uint modelEntity = wrapperBytes.U32(16);
            if (modelEntity is 0 or uint.MaxValue) continue;
            Read(catalog, modelEntity, EntityClass);
            variants.Add(new(selector, wrapper, modelEntity, values) { HolderIndex = i });
        }
        if (variants.Count == 0 || variants.Select(value => value.Selector).Distinct().Count()
            != variants.Count)
            throw new InvalidDataException("The weapon art selectors are empty or repeated.");
        return new(gearIndex, arrangement, entity, variants);
    }

    /** The key at gear row +16 that selects the weapon's row in each presentation and HUD config. */
    public static uint GearKey(PackageCatalog catalog, WeaponArchetypeSource art)
    {
        var gear = Read(catalog, GearTableTag, GearTableClass);
        return gear.U32(Row(gear.Array(8, GearRowClass, 48), art.GearIndex, 48) + 16);
    }

    /** The key at gear row +20 that selects the weapon's group in its translation config. */
    public static uint TypeKey(PackageCatalog catalog, WeaponArchetypeSource art, byte[]? gearTable = null) =>
        TypeKey(gearTable ?? catalog.Read(GearTableTag), art.GearIndex);

    public static uint TypeKey(byte[] gearTable, ushort gearIndex)
    {
        var gear = new Bytes(gearTable);
        return gear.U32(Row(gear.Array(8, GearRowClass, 48), gearIndex, 48) + 20);
    }

    public static int GearRowCount(byte[] gearTable) => new Bytes(gearTable).Array(8, GearRowClass, 48).Count;

    /** The gear row an item definition names, or null when it has no art block. */
    public static ushort? GearIndex(byte[] itemDefinition)
    {
        var item = new Bytes(itemDefinition);
        int art = item.Relative(136);
        return art < 0 ? null : item.U16(art + 88);
    }

    /** Sets gear row +20, which moves the weapon to another group of its translation config. */
    public static byte[] SetTypeKey(byte[] gearTable, ushort gearIndex, uint key)
    {
        var gear = new Bytes(gearTable);
        int row = Row(gear.Array(8, GearRowClass, 48), gearIndex, 48);
        byte[] result = gearTable.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(row + 20), key);
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
            || firstEntry < 0 || firstEntry > 8192 - count)
            throw new InvalidDataException("The weapon needs unused entries in a new package.");
        uint Tag(int index) => checked(0x80800000u + ((uint)packageId << 13)
            + (uint)(firstEntry + index));
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
        var gearRows = gear.Array(8, GearRowClass, 48);
        var definitions = Read(catalog, GearDefinitionTableTag, GearDefinitionClass);
        var definitionRows = definitions.Array(8, GearDefinitionRowClass, 8);
        RequireIdentityPair(gear, gearRows, 48, definitions, definitionRows, 8, complete: true);
        var arrangement = Read(catalog, ArrangementTableTag, ArrangementTableClass);
        var artRows = arrangement.Array(8, ArrangementRowClass, 32);
        var arrangementDefinitions = Read(catalog, ArrangementDefinitionTableTag, ArrangementDefinitionClass);
        var arrangementDefinitionRows = arrangementDefinitions.Array(8, ArrangementDefinitionRowClass, 4);
        RequireIdentityPair(arrangement, artRows, 32, arrangementDefinitions,
            arrangementDefinitionRows, 4, complete: true);
        if (gearRows.Count >= MaximumRows || artRows.Count >= MaximumRows)
            throw new InvalidDataException("The weapon art tables have no reachable row left.");
        RequireUnusedHash(gear, gearRows, 48, itemHash);
        RequireUnusedHash(arrangement, artRows, 32, itemHash);
        byte[] names = Read(catalog, RuntimeMapTag, RuntimeMapClass).Data;
        byte[] artMap = Read(catalog, ArtMapTag, ArtMapClass).Data;
        uint gameplayName = NewName(names, RuntimeMapRowClass,
            $"sunrise.weapon.{packageId:X4}.{firstEntry}.{name}");
        names = AppendName(names, RuntimeMapRowClass, gameplayName, gameplayEntity);
        var additions = new List<ProjectAssetAddition>();
        if (gameplayEntityTag is null)
            Add(source.EntityTag, catalog.Read(source.EntityTag), 0, "archetype");

        int originalGear = Row(gearRows, source.GearIndex, 48);
        byte[] gearRow = gear.Slice(originalGear, 48);
        Put32(gearRow, 0, itemHash); Put32(gearRow, 4, gameplayName);
        byte[] changedGear = InvestmentItemClone.AppendRow(gear.Data, 8, 48, gearRow,
            [0, 32], originalGear);
        byte[] definitionRow = definitions.Slice(Row(definitionRows, source.GearIndex, 8), 8);
        Put32(definitionRow, 0, itemHash);
        byte[] changedDefinitions = InvestmentItemClone.AppendRow(definitions.Data, 8, 8, definitionRow);

        int originalArt = Row(artRows, source.ArtIndex, 32);
        byte[] artRow = arrangement.Slice(originalArt, 32);
        Put32(artRow, 0, itemHash);
        byte[] arrangementIdentity = new byte[4]; Put32(arrangementIdentity, 0, itemHash);
        byte[] changedArrangementDefinitions = InvestmentItemClone.AppendRow(arrangementDefinitions.Data,
            8, 4, arrangementIdentity);
        var oldHolders = arrangement.Array(originalArt + 16, HolderClass, 8);
        // The new row's holders, groups and hashes form one block of self-relative pointers.
        var output = new List<byte>();
        int newHolders = AppendArray(output, -1, HolderClass, oldHolders.Count, 8);
        for (int i = 0, v = 0; i < oldHolders.Count; i++)
        {
            int originalGroup = arrangement.Relative(oldHolders.Data + i * 8);
            Align(output, 8);
            int group = output.Count;
            output.AddRange(arrangement.Slice(originalGroup, 24));
            Put64(output, newHolders + i * 8, group - newHolders - i * 8);
            // An empty region stays empty in the copy.
            if (v == source.Variants.Count || source.Variants[v].HolderIndex != i)
            { AppendArray(output, group + 8, ArtHashClass, 0, 4); continue; }
            var variant = source.Variants[v];
            int hashes = AppendArray(output, group + 8, ArtHashClass, variant.ArtHashes.Count, 4);
            uint hash = NewName(artMap, ArtMapRowClass,
                $"sunrise.weapon.art.{packageId:X4}.{firstEntry}.{variant.Selector}.{name}");
            artMap = AppendName(artMap, ArtMapRowClass, hash, Tag(v + wrapperStart));
            for (int j = 0; j < variant.ArtHashes.Count; j++) Put32(output, hashes + j * 4, hash);
            byte[] wrapper = Read(catalog, variant.WrapperTag, WrapperClass).Data.ToArray();
            Put32(wrapper, 16, variantEntities[variant.Selector]);
            Add(variant.WrapperTag, wrapper, v + wrapperStart, $"art-{variant.Selector}");
            v++;
        }
        // Block before rows: the rows stay last, so the next weapon reuses their bytes and leaves no dead copy.
        var (changedArt, blocks) = InvestmentItemClone.AppendRows(arrangement.Data,
            [new(8, 32, [artRow]) { RowArrays = [16], RowSources = [originalArt] }], [output.ToArray()],
            catalog.ReadOriginal(ArrangementTableTag).Length);
        int newRow = Row(new Bytes(changedArt).Array(8, ArrangementRowClass, 32), artRows.Count, 32);
        BinaryPrimitives.WriteInt64LittleEndian(changedArt.AsSpan(newRow + 16), oldHolders.Count);
        BinaryPrimitives.WriteInt64LittleEndian(changedArt.AsSpan(newRow + 24), blocks[0] + newHolders - 16 - newRow - 24);
        BinaryPrimitives.WriteInt64LittleEndian(changedArt, changedArt.Length);
        byte[] item = itemDefinition.ToArray();
        var itemReader = new Bytes(item);
        int artBlock = itemReader.Relative(136);
        BinaryPrimitives.WriteUInt16LittleEndian(item.AsSpan(artBlock + 88), (ushort)gearRows.Count);
        var itemArts = itemReader.Array(artBlock, 0x808077B5, 4);
        for (int index = 0; index < itemArts.Count; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(item.AsSpan(itemArts.Data + index * 4 + 2),
                (ushort)artRows.Count);
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
        var gearRows = gear.Array(8, GearRowClass, 48);
        var definitions = Read(catalog, GearDefinitionTableTag, GearDefinitionClass);
        var rows = definitions.Array(8, GearDefinitionRowClass, 8);
        RequireIdentityPair(gear, gearRows, 48, definitions, rows, 8, complete: false);
        if (gear.U32(Row(gearRows, profile.GearIndex, 48)) != expectedHash)
            throw new InvalidDataException("The weapon gear identity changed before repair.");
        if (rows.Count == gearRows.Count) return definitions.Data.ToArray();
        if (profile.GearIndex != rows.Count || gearRows.Count != rows.Count + 1)
            throw new InvalidDataException("Repair needs exactly one missing gear identity at the end of the table.");
        byte[] row = definitions.Slice(Row(rows, templateGearIndex, 8), 8);
        Put32(row, 0, expectedHash);
        return InvestmentItemClone.AppendRow(definitions.Data, 8, 8, row);
    }

    /** An arrangement needs a parallel identity before its new index can be used at runtime. */
    // TODO: call from a Repair action in ExoticWeaponAuthoring.Repair; nothing calls this yet.
    public static byte[] PrepareArrangementIdentityRepair(PackageCatalog catalog, byte[] itemDefinition,
        uint expectedHash)
    {
        var profile = Read(catalog, itemDefinition);
        var arrangement = Read(catalog, ArrangementTableTag, ArrangementTableClass);
        var artRows = arrangement.Array(8, ArrangementRowClass, 32);
        var definitions = Read(catalog, ArrangementDefinitionTableTag, ArrangementDefinitionClass);
        var rows = definitions.Array(8, ArrangementDefinitionRowClass, 4);
        RequireIdentityPair(arrangement, artRows, 32, definitions, rows, 4, complete: false);
        if (arrangement.U32(Row(artRows, profile.ArtIndex, 32)) != expectedHash)
            throw new InvalidDataException("The weapon arrangement identity changed before repair.");
        if (rows.Count == artRows.Count) return definitions.Data.ToArray();
        if (profile.ArtIndex != rows.Count || artRows.Count != rows.Count + 1)
            throw new InvalidDataException("Repair needs one missing arrangement identity at the end of the table.");
        byte[] row = new byte[4]; Put32(row, 0, expectedHash);
        return InvestmentItemClone.AppendRow(definitions.Data, 8, 4, row);
    }

    /** Runtime identities and presentation rows share their index and definition hash. */
    private static void RequireIdentityPair(Bytes presentation, (int Count, int Data) rows, int stride,
        Bytes definitions, (int Count, int Data) definitionRows, int definitionStride, bool complete)
    {
        if (definitionRows.Count > rows.Count || complete && definitionRows.Count != rows.Count)
            throw new InvalidDataException("The weapon identity and presentation tables have different row counts.");
        for (int index = 0; index < definitionRows.Count; index++)
            if (definitions.U32(definitionRows.Data + index * definitionStride)
                != presentation.U32(rows.Data + index * stride))
                throw new InvalidDataException("The weapon identity and presentation indices disagree.");
    }

    /**
     * A copied table keeps every old nested pointer at its original target.
     * @param field Descriptor to point at the new array, or -1 when the caller writes it.
     * @return Offset of the first element; the header is 16 bytes before it.
     */
    private static int AppendArray(List<byte> output, int field, uint type, int count, int stride)
    {
        Align(output, 16);
        output.AddRange(new byte[16]);
        int header = output.Count;
        output.AddRange(new byte[checked(16 + count * stride)]);
        Put32(output, header - 4, ArrayMarker);
        Put64(output, header, count); Put32(output, header + 8, type);
        if (field >= 0) { Put64(output, field, count); Put64(output, field + 8, header - field - 8); }
        return header + 16;
    }

    private static void Align(List<byte> output, int alignment)
    { while (output.Count % alignment != 0) output.Add(0); }

    /** Name lookup uses unsigned ascending keys and admits no duplicate identity. */
    private static byte[] AppendName(byte[] data, uint type, uint hash, uint tag)
    {
        var reader = new Bytes(data);
        var rows = reader.Array(8, type, 8);
        RequireUnusedHash(reader, rows, 8, hash);
        byte[] row = new byte[8]; Put32(row, 0, hash); Put32(row, 4, tag);
        byte[] output = InvestmentItemClone.AppendRow(data, 8, 8, row);
        var destination = new Bytes(output).Array(8, type, 8);
        var values = Enumerable.Range(0, rows.Count).Select(index =>
            (Hash: reader.U32(rows.Data + index * 8), Tag: reader.U32(rows.Data + index * 8 + 4)))
            .Append((hash, tag)).OrderBy(value => value.Item1).ToArray();
        for (int index = 0; index < values.Length; index++)
        {
            Put32(output, destination.Data + index * 8, values[index].Item1);
            Put32(output, destination.Data + index * 8 + 4, values[index].Item2);
        }
        return output;
    }

    private static uint NewName(byte[] data, uint type, string key)
    {
        var reader = new Bytes(data);
        var rows = reader.Array(8, type, 8);
        return InvestmentItemClone.NewDefinitionHash(key, Enumerable.Range(0, rows.Count)
            .Select(index => reader.U32(rows.Data + index * 8)).ToHashSet());
    }

    private static uint Resolve(Bytes reader, uint type, uint hash) => TryResolve(reader, type, hash, out uint value) ? value
        : throw new InvalidDataException($"Weapon art name {hash:X8} does not resolve.");

    private static bool TryResolve(Bytes reader, uint type, uint hash, out uint value)
    {
        var rows = reader.Array(8, type, 8);
        for (int i = 0; i < rows.Count; i++)
            if (reader.U32(rows.Data + i * 8) == hash) { value = reader.U32(rows.Data + i * 8 + 4); return true; }
        value = 0;
        return false;
    }

    private static void RequireUnusedHash(Bytes reader, (int Count, int Data) rows,
        int stride, uint hash)
    {
        if (hash is 0 or uint.MaxValue or 0x811C9DC5 || Enumerable.Range(0, rows.Count)
            .Any(index => reader.U32(rows.Data + index * stride) == hash))
            throw new InvalidDataException("The weapon identity is empty or already used.");
    }

    private static int Row((int Count, int Data) rows, int index, int stride) =>
        index >= 0 && index < rows.Count ? checked(rows.Data + index * stride)
            : throw new InvalidDataException("The weapon art index is outside its table.");

    private static Bytes Read(PackageCatalog catalog, uint tag, uint type)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != type
            || entry.Type is not (8 or 16))
            throw new InvalidDataException($"Weapon asset {tag:X8} has an unexpected type.");
        return new(catalog.Read(tag));
    }

    private static void Put32(byte[] output, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset), value);
    private static void Put32(List<byte> output, int offset, uint value)
    { var bytes = BitConverter.GetBytes(value); for (int i = 0; i < 4; i++) output[offset + i] = bytes[i]; }
    private static void Put64(List<byte> output, int offset, long value)
    { var bytes = BitConverter.GetBytes(value); for (int i = 0; i < 8; i++) output[offset + i] = bytes[i]; }

    /** Every pointer and typed array is bounded before it supplies a row. */
    private sealed class Bytes
    {
        public byte[] Data { get; }
        public Bytes(byte[] data)
        {
            if (data.Length < 8 || BinaryPrimitives.ReadUInt64LittleEndian(data) != (ulong)data.Length)
                throw new InvalidDataException("Weapon asset size header is invalid.");
            Data = data;
        }
        public byte[] Slice(int at, int count) { Require(at, count); return Data.AsSpan(at, count).ToArray(); }
        public byte U8(int at) { Require(at, 1); return Data[at]; }
        public ushort U16(int at) { Require(at, 2); return BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(at)); }
        public uint U32(int at) { Require(at, 4); return BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(at)); }
        public long I64(int at) { Require(at, 8); return BinaryPrimitives.ReadInt64LittleEndian(Data.AsSpan(at)); }
        public int Relative(int at)
        {
            long offset = I64(at);
            if (offset == 0) return -1;
            int target = checked((int)(at + offset)); Require(target, 1); return target;
        }
        public (int Count, int Data) Array(int at, uint type, int stride)
        {
            long count = I64(at);
            if (count == 0) return (0, 0);
            if (count < 0 || count > 1_048_576)
                throw new InvalidDataException("Weapon array count is invalid.");
            int header = Relative(at + 8);
            if (header < 4 || U32(header - 4) != ArrayMarker || I64(header) != count
                || U32(header + 8) != type)
                throw new InvalidDataException("Weapon array type or count changed.");
            int start = checked(header + 16); Require(start, checked((int)count * stride));
            return ((int)count, start);
        }
        private void Require(int at, int count)
        {
            if (at < 0 || count < 0 || at > Data.Length - count)
                throw new InvalidDataException("Weapon asset field leaves its record.");
        }
    }
}
