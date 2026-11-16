using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.World.Entities;

/**
 * Builds a new entity by copying an installed one: the entity, every component config it lists, and
 * any further tags the caller names. Each copy gets a scratch tag, and every reference among the
 * copies, a config's references to itself included, moves to the copies.
 */
public static class EntityBuilder
{
    /** Scratch tags use this package id, which no installed package uses. */
    public const ushort ScratchPackage = 0xFFE;

    /**
     * @param also Further installed tags to copy, such as a model a config names.
     * @param replace New bytes for a copied tag, given by its installed tag; references in them move too.
     * @param seed Picks the scratch tags; calls with different seeds do not collide.
     * @return The copies, the entity first, then its configs in component order, then `also`.
     */
    public static IReadOnlyList<WorldTag> Clone(PackageCatalog catalog, uint entity, IReadOnlyList<uint>? also = null,
        IReadOnlyDictionary<uint, byte[]>? replace = null, int seed = 0)
    {
        if (!catalog.TryGetEntry(entity, out var root) || root.Reference != SEntity.Class)
            throw new InvalidDataException($"0x{entity:X8} is not an installed entity.");
        var sources = new List<uint> { entity };
        sources.AddRange(Components(catalog.Read(entity)).Where(tag => !sources.Contains(tag)));
        foreach (uint tag in also ?? []) if (!sources.Contains(tag)) sources.Add(tag);
        foreach (uint tag in replace?.Keys ?? []) if (!sources.Contains(tag)) throw new ArgumentException($"0x{tag:X8} is replaced but not copied.", nameof(replace));

        if (sources.Count > SeedTags / 2) throw new InvalidDataException("An entity copy holds too many tags for one seed.");
        var scratch = sources.Select((tag, index) => (tag, copy: ScratchTag(seed, index))).ToDictionary(x => x.tag, x => x.copy);
        if (scratch.Values.Any(catalog.ContainsTag)) throw new InvalidDataException("A scratch tag is installed.");
        return sources.Select(tag =>
        {
            if (!catalog.TryGetEntry(tag, out var entry)) throw new InvalidDataException($"0x{tag:X8} is not installed.");
            byte[] data = replace is not null && replace.TryGetValue(tag, out var bytes) ? (byte[])bytes.Clone() : catalog.Read(tag);
            Retarget(data, scratch);
            return new WorldTag(scratch[tag], entry.Reference, entry.TypeInfo, data, tag);
        }).ToArray();
    }

    /** Scratch tags per seed. Clone takes the lower half; a caller's own new tags take the upper half. */
    public const int SeedTags = 256;

    /** The scratch tag `index` of `seed`. */
    public static uint ScratchTag(int seed, int index)
    {
        if (seed < 0 || index is < 0 or >= SeedTags || seed >= TagHashes.EntriesPerPackage / SeedTags) throw new ArgumentOutOfRangeException(nameof(seed));
        return TagHashes.Make(ScratchPackage, seed * SeedTags + index);
    }

    /** The class of the definition object a component config holds; 0 when it holds none. */
    public static uint DefinitionClass(byte[] config)
    {
        int at = TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition));
        if (config.Length < at + sizeof(long)) return 0;
        long relative = BinaryPrimitives.ReadInt64LittleEndian(config.AsSpan(at)), target = at + relative;
        return relative == 0 || target < 4 || target > config.Length ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(config.AsSpan((int)target - 4));
    }

    // The word after a model config's model tag: 2 on configs whose models use matrix skinning, 1 on the Dreg's.
    private const uint MatrixSkinning = 2;

    /** The component config tags an entity lists, in order. */
    public static IReadOnlyList<uint> Components(byte[] entity)
    {
        ref readonly var root = ref TagView.Checked<SEntity>(entity, 0);
        var rows = TagView.Rows(entity, in root.Components, SPatternComponent.Class);
        var tags = new uint[rows.Length];
        for (int index = 0; index < rows.Length; index++) tags[index] = rows[index].Config;
        return tags;
    }

    /**
     * @param config A model component config.
     * @param boneCount Bones the model consumes from the entity's skeleton.
     * @param matrixSkinned Marks the config for a matrix-skinned model, which the model writer produces.
     * @return The config with its model replaced.
     */
    public static byte[] WithModel(byte[] config, uint model, int boneCount, bool matrixSkinned = false)
    {
        int primary = Target(config, nameof(SEntityConfig.DefaultInstance), SModelComponentPrimary.Class);
        int resource = Target(config, nameof(SEntityConfig.Definition), SModelComponent.Class);
        byte[] output = (byte[])config.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(resource + TagView.FieldOffset<SModelComponent>(nameof(SModelComponent.Model))), model);
        if (matrixSkinned)
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(resource + TagView.FieldOffset<SModelComponent>(nameof(SModelComponent.Model)) + sizeof(uint)), MatrixSkinning);
        BinaryPrimitives.WriteUInt32LittleEndian(
            output.AsSpan(primary + TagView.FieldOffset<SModelComponentPrimary>(nameof(SModelComponentPrimary.ConsumedBoneCount))), checked((uint)boneCount));
        return output;
    }

    /** The object a config pointer names, checked against its class word. */
    private static int Target(byte[] config, string member, uint objectClass)
    {
        int at = TagView.FieldOffset<SEntityConfig>(member);
        long relative = BinaryPrimitives.ReadInt64LittleEndian(config.AsSpan(at));
        long target = at + relative;
        if (relative == 0 || target < 4 || target >= config.Length
            || BinaryPrimitives.ReadUInt32LittleEndian(config.AsSpan((int)target - 4)) != objectClass)
            throw new InvalidDataException($"The config's {member} is not a 0x{objectClass:X8} object.");
        return (int)target;
    }

    // Tag references sit on 4-byte boundaries; a copied tag's value is rewritten wherever it occurs.
    private static void Retarget(byte[] data, IReadOnlyDictionary<uint, uint> scratch)
    {
        for (int at = 0; at + sizeof(uint) <= data.Length; at += sizeof(uint))
            if (scratch.TryGetValue(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at)), out uint copy))
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), copy);
    }
}
