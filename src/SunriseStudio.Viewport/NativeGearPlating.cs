using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

/** One executed dye scope: its pixel constant buffer and the pixel textures it binds. */
public sealed record NativeGearScope(int ScopeIndex, uint RecordTag, uint ConstantBufferSlot, Vector4[] Constants,
    IReadOnlyDictionary<uint, uint> Textures);

/**
 * The dye scopes and plate textures one material reads on one part; Key separates materials.
 * ForcedBlend is the blend byte the decal dye forces over the material's; null forces nothing.
 */
public sealed record NativeGearMaterial(IReadOnlyList<NativeGearScope> Scopes, IReadOnlyDictionary<uint, uint> PlateTextures,
    string Key, byte? ForcedBlend = null)
{
    public IEnumerable<uint> TextureTags => Scopes.SelectMany(scope => scope.Textures.Values)
        .Concat(PlateTextures.Values).Where(tag => tag != NativeGearPlating.FrameDyeTexture);
}

/**
 * Dye records for gear_dye_0 to gear_dye_decal, and where they came from.
 * DecalMode is dye byte +0x14 of a real gear_dye_decal dye; null when slot 3 is the global default.
 * Kind is the gear kind of the item the dyes came from; null when no item gave one.
 */
public sealed record NativeGearDyes(IReadOnlyList<uint> Records, string Source, byte? DecalMode = null, int? Kind = null);

/**
 * Gear-plated model inputs: scope 25 binds three plate textures at pixel t0 to t2, and scopes 26 to 29
 * run one dye record each. 0x7FF742E76280 and 0x7FF742E71450 execute them; the cloth draw 0x7FF742E78D60 runs 25 and 27.
 */
public static class NativeGearPlating
{
    // Material scope-mask word; bit 25 reads the plate textures and bits 26 to 29 the dye scopes.
    private const int MaterialMaskOffset = 0x18;
    private const int PlateScopeBit = 25, FirstDyeScope = 26, DyeSlots = 4;
    // Global default records the draw uses for a slot with no dye; dword_7FF744B21010 holds them.
    public static readonly IReadOnlyList<uint> DefaultRecords = [0x80B35715, 0x80B35718, 0x80B3571B, 0x80B3571E];
    public static readonly NativeGearDyes Undyed = new(DefaultRecords, "global default dye records");
    // Stages whose draw runs dye scopes 26 to 28 (mask 0x104C7), 29 (0x144E) and plate scope 25 (0x114CF).
    private const uint DyeStages = 0x104C7, DecalStages = 0x144E, PlateStages = 0x114CF;
    // Model and cloth component resources name the gear-plated record at +0x248.
    private const int RecordField = 0x248;
    private const uint RecordClass = 0x808072D2;
    internal const uint DescriptorClass = 0x80809EBB, DescriptorRowClass = 0x80809EBD;
    private const uint ResourceClass = 0x808072BD, ClothResourceClass = 0x80807286, ScopeClass = 0x808071F3, DyeClass = 0x808071CD;
    // The cloth draw 0x7FF742E78D60 runs only gear_dye_1 (scope 27), with no dye pair check.
    private const int ClothDyeSlot = 1;
    // Dye +0x14 picks the forced decal blend; that the game reads it as dye object +20 is inferred.
    private const int DecalModeOffset = 0x14;
    // Item definition +184 is the bucket.
    private const int ItemBucketField = 184;
    /** Placeholder tag for extern 1 resource 20; the frame writer leaves it null, so it reads zero. */
    public const uint FrameDyeTexture = 0;
    public static readonly TfxResource FrameDyeResource = new("extern 1 resource 20",
        Vortice.Direct3D.ShaderResourceViewDimension.Texture2D);
    public static readonly TfxExternKey FrameDyeKey = new(1, 20);

    private static readonly ConditionalWeakTable<SceneDocument, NativeGearDyes> SceneDyes = new();
    private static readonly ConditionalWeakTable<MeshInstance, NativeGearDyes> InstanceDyes = new();
    private static readonly ConditionalWeakTable<MeshInstance, GearObject> GearObjects = new();

    /** The dyes an assembled item view uses; a scene with none uses the global default records. */
    public static void SetDyes(SceneDocument scene, NativeGearDyes dyes) => SceneDyes.AddOrUpdate(scene, dyes);

    /** Dyes for one part; they win over the scene's. */
    public static void SetDyes(MeshInstance instance, NativeGearDyes dyes) => InstanceDyes.AddOrUpdate(instance, dyes);

    public static NativeGearDyes DyesFor(SceneDocument scene) => SceneDyes.TryGetValue(scene, out var dyes) ? dyes : Undyed;

    /** The instance's dyes, else the scene's, else Undyed. */
    public static NativeGearDyes DyesFor(SceneDocument scene, MeshInstance instance) =>
        InstanceDyes.TryGetValue(instance, out var dyes) ? dyes : DyesFor(scene);

    /** Material scope-mask bits 25 to 29; zero when the material reads no gear scope. */
    public static uint GearBits(PackageCatalog catalog, uint material)
    {
        if (!catalog.TryGetEntry(material, out var entry) || entry.Reference != 0x808071E8) return 0;
        byte[] bytes = catalog.Read(material);
        return bytes.Length < MaterialMaskOffset + 4 ? 0
            : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(MaterialMaskOffset)) & (0x1Fu << PlateScopeBit);
    }

    /**
     * Item dyes: art block +0x38 rows, then +0x48 rows replace rows of the same i8 key, then the rows of the shader the
     * item's sockets start with, read the same way. A shader that does not read leaves the item's own.
     * Rows are {i8 key, i8 pad, u16 dye index}; 0x7FF742200F30 owns the order.
     * @return The records by slot, the default record where the item names none; null when unreadable.
     */
    public static NativeGearDyes? ReadItemDyes(PackageCatalog catalog, byte[] item, out string? error)
    {
        error = null;
        try
        {
            var rows = new List<(sbyte Key, int Dye)>();
            void Take(byte[] source)
            {
                int art = Relative(source, 136);
                foreach (int row in Rows(source, art + 0x38, 0x808077B3, 4).Concat(Rows(source, art + 0x48, 0x808077B3, 4)))
                {
                    (sbyte Key, int Dye) value = ((sbyte)source[row], BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(row + 2)));
                    int same = rows.FindIndex(other => other.Key == value.Key);
                    if (same < 0) rows.Add(value);
                    else rows[same] = value;
                }
            }
            Take(item);
            string described = "the item's dyes";
            var own = rows.ToList();
            try
            {
                if (GuardianArt.EquippedShader(catalog, item) is not 0 and var shader)
                {
                    Take(catalog.Read(shader));
                    return Dyes("the item's dyes with its shader");
                }
            }
            catch (Exception exception) when (Unreadable(exception))
            {
                rows = own;
                described += $" (shader unreadable: {exception.Message})";
            }
            return Dyes(described);
            NativeGearDyes Dyes(string source)
            {
                var (records, decalMode) = ScopeRecords(catalog, rows.Select(row => row.Dye));
                return new(records, source, decalMode, ItemKind(item));
            }
        }
        catch (Exception exception) when (Unreadable(exception))
        {
            error = exception.Message;
            return null;
        }
    }

    /** The gear kind of an item definition's bucket; null when the bucket has no measured kind. */
    public static int? ItemKind(byte[] item) =>
        item.Length > ItemBucketField ? NativePlateAtlases.KindForBucket(item[ItemBucketField]) : null;

    /**
     * Marks the gear object an instance belongs to; each gear object gets its own plate atlases.
     * @param kind The gear kind, which picks the atlas sizes; null when unknown.
     */
    public static void SetGearObject(MeshInstance instance, object key, int? kind) =>
        GearObjects.AddOrUpdate(instance, new GearObject(key, kind));

    /** The instance's gear object, else the scene, so an unmarked scene is one gear object. */
    public static object GearObjectFor(SceneDocument scene, MeshInstance instance) =>
        GearObjects.TryGetValue(instance, out var found) ? found.Key : scene;

    /**
     * The instance's marked gear kind, else the kind of the item its dyes came from.
     * @param known False when neither gives one; the weapon kind is returned then.
     */
    public static int KindFor(SceneDocument scene, MeshInstance instance, out bool known)
    {
        int? kind = (GearObjects.TryGetValue(instance, out var found) ? found.Kind : null) ?? DyesFor(scene, instance).Kind;
        known = kind is not null;
        return kind ?? NativePlateAtlases.WeaponKind;
    }

    private sealed record GearObject(object Key, int? Kind);

    /**
     * Records by slot for these dye indices (table 0x81613D24); default records elsewhere.
     * @return Null, with the error, when one index does not resolve.
     */
    public static NativeGearDyes? ReadDyeIndices(PackageCatalog catalog, IEnumerable<int> dyeIndices, string source, out string? error)
    {
        error = null;
        try
        {
            var (records, decalMode) = ScopeRecords(catalog, dyeIndices);
            return new(records, source, decalMode);
        }
        catch (Exception exception) when (Unreadable(exception))
        {
            error = exception.Message;
            return null;
        }
    }

    /**
     * Dye index -> table 0x81613D24 hash -> map 0x80EC3F60 wrapper -> wrapper +0x10 dye -> dye +0x0C scope record.
     * The record's scope index 26 to 29 picks its slot; a later index wins a shared slot.
     * @return The records by slot, and byte +0x14 of the dye that fills slot 3, if any.
     */
    private static (uint[] Records, byte? DecalMode) ScopeRecords(PackageCatalog catalog, IEnumerable<int> indices)
    {
        var records = DefaultRecords.ToArray();
        byte? decalMode = null;
        var lookup = DyeLookupFor(catalog);
        foreach (int index in indices)
        {
            if (index < 0 || index >= lookup.DyeHashes.Length) throw new InvalidDataException($"Dye index {index} is outside its table.");
            uint hash = lookup.DyeHashes[index];
            if (!lookup.Wrappers.TryGetValue(hash, out uint wrapper)) throw new InvalidDataException($"Dye hash 0x{hash:X8} does not resolve.");
            byte[] dye = Require(catalog, U32(Require(catalog, wrapper, 0x8080744A), 0x10), DyeClass);
            uint scope = U32(dye, 0x0C);
            int slot = checked((int)U32(Require(catalog, scope, ScopeClass), 0x10)) - FirstDyeScope;
            if (slot is < 0 or >= DyeSlots) throw new InvalidDataException($"Dye scope 0x{scope:X8} is not a gear dye scope.");
            records[slot] = scope;
            if (slot == 3) decalMode = dye[DecalModeOffset];
        }
        return (records, decalMode);
    }

    /** The blend byte 0x7FF742E71450 forces at context +0x1664 for a decal dye mode; it writes nothing past 4. */
    internal static byte? ForcedDecalBlend(byte mode) => mode switch
    {
        0 => 0x97, 1 => 0xAB, 2 => 0xA4, 3 => 0x96, 4 => 0x9B, _ => null
    };

    /** Dye table 0x81613D24 row hashes and map 0x80EC3F60, read once per catalog read generation. */
    private sealed record DyeLookup(long Generation, uint[] DyeHashes, Dictionary<uint, uint> Wrappers);
    private static readonly ConditionalWeakTable<PackageCatalog, DyeLookup> DyeLookups = new();

    private static DyeLookup DyeLookupFor(PackageCatalog catalog)
    {
        long generation = catalog.ReadGeneration;
        if (DyeLookups.TryGetValue(catalog, out var held) && held.Generation == generation) return held;
        byte[] dyeTable = catalog.Read(0x81613D24);
        uint[] hashes = [.. Rows(dyeTable, 8, 0x80805DEC, 8).Select(row => U32(dyeTable, row + 4))];
        var lookup = new DyeLookup(generation, hashes, PackageHashMap.Read(catalog, 0x80EC3F60, 0x80809252));
        DyeLookups.AddOrUpdate(catalog, lookup);
        return lookup;
    }

    private static bool Unreadable(Exception exception) => exception is InvalidDataException or KeyNotFoundException
        or ArgumentException or OverflowException or IndexOutOfRangeException;

    /**
     * Gear inputs for one part's material, or null when the material reads none.
     * @param atlasTagOffset Added to each plate atlas tag, so each gear object's atlases keep their own tags.
     * @param error Why a scope the material reads cannot be supplied; the draw would keep stale state.
     */
    internal static NativeGearMaterial? ForPart(PackageCatalog catalog, MeshInstance instance, uint gearBits,
        NativeGearDyes dyes, Func<uint, NativeGearScope> evaluate, Dictionary<uint, (byte[]? Record, string? Error)> records,
        NativePlateAtlases atlases, out string? error, uint atlasTagOffset = 0)
    {
        error = null;
        if (gearBits == 0 || instance.Mesh.Native is not { Kind: NativeMeshKind.Entity } native) return null;
        if (!records.TryGetValue(instance.ModelSourceConfigTag, out var found))
            records.Add(instance.ModelSourceConfigTag, found = ReadRecord(catalog, instance.ModelSourceConfigTag));
        if (found.Record is not { } record) { error = found.Error; return null; }
        int stage = native.RenderStage;
        bool cloth = instance.RequiresClothSimulation;
        // 0x7FF742E71450 runs the dye scopes only when the part's pair index is below the record count plus one.
        int pair = (native.SourceHeader.Length > instance.Mesh.PartIndex + 0x1A ? native.SourceHeader[instance.Mesh.PartIndex + 0x1A] : 0) >> 1;
        int count = (int)Math.Min(4, U32(record, 24));
        var scopes = new List<NativeGearScope>();
        byte? forced = null;
        for (int slot = 0; slot < DyeSlots; slot++)
        {
            if ((gearBits & (1u << (FirstDyeScope + slot))) == 0) continue;
            if (cloth && slot != ClothDyeSlot) { error = $"The cloth draw runs no gear_dye scope {slot}"; return null; }
            uint stages = slot == 3 ? DecalStages : DyeStages;
            if ((stages & (1u << stage)) == 0) { error = $"Stage {stage} runs no gear_dye scope {slot}"; return null; }
            if (!cloth && pair >= count + 1) { error = "The part's dye pair is outside the gear record's dye count"; return null; }
            scopes.Add(evaluate(dyes.Records[slot]));
            // The game keeps the forced byte for later decal draws too; Studio applies it to this part only.
            if (slot == 3 && dyes.DecalMode is { } mode) forced = ForcedDecalBlend(mode);
        }
        var plates = new Dictionary<uint, uint>();
        if ((gearBits & (1u << PlateScopeBit)) != 0)
        {
            if (record[21] == 0 || (PlateStages & (1u << stage)) == 0)
            { error = "The gear record binds no plate textures in this stage"; return null; }
            if (!atlases.TagsFor(record, plates, out error)) return null;
            foreach (uint slot in plates.Keys.ToArray()) plates[slot] += atlasTagOffset;
        }
        string key = string.Join(',', scopes.Select(scope => $"{scope.ScopeIndex}:{scope.RecordTag:X8}"))
            + "|" + string.Join(',', plates.OrderBy(pair => pair.Key).Select(pair => $"t{pair.Key}:{pair.Value:X8}"))
            + (forced is { } blend ? $"|f{blend:X2}" : "");
        return new(scopes, plates, key, forced);
    }

    /** Runs a dye record's pixel block; extern 1 resource 20 is the zero frame texture. */
    internal static NativeGearScope Evaluate(GameMaterialReader reader, uint record, TfxInputs inputs)
    {
        var scope = reader.ReadScopeExecution(record);
        if (scope.ScopeIndex is < FirstDyeScope or >= FirstDyeScope + DyeSlots || scope.Vertex.Data.TfxBytecode.Length != 0
            || scope.Vertex.Data.ConstantTemplate.Length != 0 || scope.Vertex.Textures.Count != 0)
            throw new NotSupportedException($"Dye scope 0x{record:X8} is not a pixel-only gear dye scope.");
        var evaluation = TfxEvaluator.Evaluate(scope.Pixel.Data, inputs);
        evaluation.RequireComplete();
        if (evaluation.Samplers.Count != 0) throw new NotSupportedException($"Dye scope 0x{record:X8} binds samplers.");
        var textures = scope.Pixel.Textures.ToDictionary(texture => texture.Slot, texture => texture.TextureTag);
        foreach (var (key, resource) in evaluation.ResourceBindings)
        {
            if (key.Stage != GameShaderStage.Pixel || resource != FrameDyeResource)
                throw new NotSupportedException($"Dye scope 0x{record:X8} binds an unsupported resource.");
            textures[key.Slot] = FrameDyeTexture;
        }
        return new((int)scope.ScopeIndex, record, scope.Pixel.ConstantBufferSlot, evaluation.Constants, textures);
    }

    /** The model or cloth component resource behind the part's config and its gear-plated record. */
    internal static (byte[]? Record, string? Error) ReadRecord(PackageCatalog catalog, uint configTag)
    {
        try
        {
            byte[] config = Require(catalog, configTag, 0x80809C36);
            int resource = Relative(config, 0x18);
            if (resource < 4 || U32(config, resource - 4) is not (ResourceClass or ClothResourceClass))
                return (null, "The part's model component is not a gear-plated model");
            uint recordTag = U32(config, resource + RecordField);
            if (recordTag == uint.MaxValue) return (null, "The part's model has no gear-plated record");
            return (Require(catalog, recordTag, RecordClass), null);
        }
        catch (Exception exception) when (exception is InvalidDataException or KeyNotFoundException or ArgumentException
            or OverflowException or IndexOutOfRangeException)
        {
            return (null, $"Gear record unreadable: {exception.Message}");
        }
    }

    /** The gear-plated record of every distinct model config among one gear object's instances. */
    internal static IEnumerable<byte[]> SceneRecords(PackageCatalog catalog, IEnumerable<MeshInstance> instances,
        Dictionary<uint, (byte[]? Record, string? Error)> records)
    {
        foreach (uint config in instances.Where(instance => instance.Mesh.Native?.Kind == NativeMeshKind.Entity)
            .Select(instance => instance.ModelSourceConfigTag).Distinct())
        {
            if (!records.TryGetValue(config, out var found)) records.Add(config, found = ReadRecord(catalog, config));
            if (found.Record is { } record && record[21] != 0) yield return record;
        }
    }

    internal static byte[] Require(PackageCatalog catalog, uint tag, uint type)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != type)
            throw new InvalidDataException($"Tag 0x{tag:X8} is not class 0x{type:X8}.");
        return catalog.Read(tag);
    }

    internal static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    private static int Relative(byte[] data, int offset)
    {
        long relative = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset, 8));
        if (relative == 0) throw new InvalidDataException("A required pointer is empty.");
        return checked((int)(offset + relative));
    }

    /** Row offsets of a typed array; the header sits behind the 0x80809FBD marker. */
    internal static int[] Rows(byte[] data, int field, uint type, int stride)
    {
        long count = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(field, 8));
        if (count == 0) return [];
        if (count is < 0 or > 1_000_000) throw new InvalidDataException("A gear array count is invalid.");
        int header = Relative(data, field + 8);
        if (header < 4 || U32(data, header - 4) != 0x80809FBD || U32(data, header + 8) != type)
            throw new InvalidDataException($"A gear array is not class 0x{type:X8}.");
        int start = checked(header + 16);
        _ = data.AsSpan(start, checked((int)count * stride));
        return Enumerable.Range(0, (int)count).Select(index => start + index * stride).ToArray();
    }
}
