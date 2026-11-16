using System.Buffers.Binary;
using SunriseStudio.Animation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.World;
using SunriseStudio.World.Entities;

namespace SunriseStudio.Authoring;

/** One bone of a new entity, in package order: a parent comes before its children. */
public sealed record RiggedBone(string Name, int Parent, BoneTransform BindObject);

/** One clip: per bone, its parent-relative transform per frame at 30 frames a second. */
public sealed record RiggedClip(string Name, IReadOnlyList<IReadOnlyList<BoneTransform>> Bones)
{
    /** Events the clip fires as it plays; see ClipEvent.NewTagged. */
    public IReadOnlyList<ClipEvent> Events { get; init; } = [];
}

/**
 * A new animated entity. Influences index Bones; Plays names the clip the entity plays by itself.
 */
public sealed record RiggedEntityInput(string Name, IReadOnlyList<RiggedBone> Bones, InterchangeMesh Geometry,
    IReadOnlyList<VertexSkinWeights> Influences, IReadOnlyList<RiggedClip> Clips, string Plays)
{
    /** An installed skin material that reads no object channel; null gives the default. */
    public uint? Material { get; init; }
    /**
     * The installed entity to build over. RiggedEntityBuilder.DeviceTemplate gives a device: its
     * lanes play the clips named position, position_on, power_on, power_off, lock_on and lock_off,
     * and Plays is empty.
     */
    public uint Template { get; init; } = RiggedEntityBuilder.Template;
}

/** World tags of the entity, the entity first, and the project assets of its model. */
public sealed record RiggedEntity(IReadOnlyList<WorldTag> Tags, IReadOnlyList<ProjectAssetAddition> Assets, uint ModelTag);

/**
 * Builds an animated entity with its own skeleton, rig, model, clips and definition, over an
 * installed template entity. Every bone has one control and every clip writes every control.
 */
public static class RiggedEntityBuilder
{
    public const uint Template = 0x80C14247;
    /** A seven-component device whose 0x80803910 component maps the device lanes to clip names. */
    public const uint DeviceTemplate = 0x80F3C422;
    // The component that plays a named clip without a request from outside.
    private const uint PlayerComponent = 0x8080366C, QueryTable = 0x80803465;
    // The Hob's four-weight part, the mesh template of a new skinned model.
    internal const uint SkinEntity = 0x80B82FE3, SkinModel = 0x80C4424F;
    internal const int SkinPart = 0x300;

    /**
     * @param packageId The project package that takes the model and its buffers.
     * @param firstEntry The first of seven free entries in that package.
     * @param seed Picks the scratch tags; entities built with different seeds do not collide.
     */
    public static RiggedEntity Build(PackageCatalog catalog, RegistryIndex schema, RiggedEntityInput input, ushort packageId,
        int firstEntry, int seed, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Bones.Count < 2) throw new ArgumentException("An entity needs a root bone and one child.", nameof(input));
        if (input.Clips.Count is < 1 or > EntityBuilder.SeedTags / 2) throw new ArgumentException($"An entity needs 1 to {EntityBuilder.SeedTags / 2} clips.", nameof(input));
        if (input.Plays.Length != 0 && input.Clips.All(clip => clip.Name != input.Plays)) throw new ArgumentException($"No clip is named {input.Plays}.", nameof(input));
        if (input.Clips.Any(clip => clip.Bones.Count != input.Bones.Count)) throw new ArgumentException("A clip must hold every bone.", nameof(input));

        var configs = EntityBuilder.Components(catalog.Read(input.Template));
        uint Config(uint definitionClass) => configs.Single(tag => EntityBuilder.DefinitionClass(catalog.Read(tag)) == definitionClass);
        uint skeleton = Config(SSkeletonResource.Class), model = Config(SModelComponent.Class);
        uint rig = Config(SAnimationRigResource.Class);
        uint player = configs.SingleOrDefault(tag => EntityBuilder.DefinitionClass(catalog.Read(tag)) == PlayerComponent);
        if ((player != 0) != (input.Plays.Length != 0))
            throw new ArgumentException(player != 0 ? "This template needs the clip it plays." : "This template plays its clips by device lane; leave Plays empty.", nameof(input));
        uint definition = configs.SelectMany(tag => Words(catalog.Read(tag)))
            .Where(word => catalog.TryGetEntry(word, out var entry) && entry.Reference == SAnimationDefinition.Class).Distinct().Single();
        // The selector's query table names the clip to play, as the player config does.
        uint query = configs.SelectMany(tag => Words(catalog.Read(tag)))
            .Where(word => catalog.TryGetEntry(word, out var entry) && entry.Reference == QueryTable).Distinct().Single();
        byte[] stockDefinition = catalog.Read(definition);
        uint stockClip = Words(stockDefinition).First(word => catalog.TryGetEntry(word, out var entry) && entry.Reference == SAnimationClip.Class);
        var stock = new AnimationControlReader(catalog).Read(stockClip);
        uint stockName = BinaryPrimitives.ReadUInt32LittleEndian(catalog.Read(stockClip).AsSpan(RigClipCompiler.NameField));
        if (stock.ControlGroups.Count != 1 || !catalog.TryGetEntry(stockClip, out var clipEntry))
            throw new InvalidDataException("The template clip does not have one control group.");

        var bones = input.Bones.Select(bone => new SkeletonBoneInput(AnimationDefinitionWriter.NameHash(bone.Name), bone.Parent, bone.BindObject)).ToArray();
        var prepared = CompatibleSkinnedMesh.PrepareNewRig(catalog, input.Geometry, input.Influences, bones.Length,
            new(SkinEntity, SkinModel, SkinPart, input.Name, packageId, firstEntry),
            input.Material ?? CompatibleSkinnedMesh.NewRigMaterial, token: token);
        var clips = new List<WorldTag>();
        var rows = new List<AnimationDefinitionClip>();
        foreach (var clip in input.Clips)
        {
            // Clip tags take the upper half of the seed's scratch tags.
            uint tag = EntityBuilder.ScratchTag(seed, EntityBuilder.SeedTags - 1 - clips.Count);
            byte[] bytes = RigClipCompiler.Prepare(catalog, stockClip, stock.ControlGroups[0].NameHash,
                AnimationDefinitionWriter.NameHash(clip.Name), clip.Bones);
            if (clip.Events.Any(value => value.Frame < 0 || value.Frame >= clip.Bones[0].Count))
                throw new ArgumentException($"Clip {clip.Name} has an event outside its frames.", nameof(input));
            if (clip.Events.Count > 0) bytes = AnimationClipEvents.Write(bytes, schema, clip.Events, compact: true);
            clips.Add(new(tag, clipEntry.Reference, clipEntry.TypeInfo, bytes));
            rows.Add(new(clip.Name, tag, clip.Bones[0].Count));
        }
        var replace = new Dictionary<uint, byte[]>
        {
            [skeleton] = SkeletonWriter.Write(catalog.Read(skeleton), schema, bones),
            [rig] = AnimationRigWriter.Write(catalog.Read(rig), schema, bones.Select(bone => bone.Parent).ToArray(),
                bones.Select(bone => SkeletonWriter.Local(bones, bone)).ToArray()),
            [model] = EntityBuilder.WithModel(catalog.Read(model), prepared.ModelTag, bones.Length),
            [definition] = AnimationDefinitionWriter.Write(stockDefinition, schema, rows),
        };
        var also = new List<uint> { definition };
        if (player != 0)
        {
            replace[player] = Renamed(catalog.Read(player), stockName, AnimationDefinitionWriter.NameHash(input.Plays));
            replace[query] = Renamed(catalog.Read(query), stockName, AnimationDefinitionWriter.NameHash(input.Plays));
            also.Add(query);
        }
        var tags = EntityBuilder.Clone(catalog, input.Template, also, replace, seed).ToList();
        tags.AddRange(clips);
        return new(tags, prepared.Additions, prepared.ModelTag);
    }

    private static IEnumerable<uint> Words(byte[] data)
    {
        for (int at = 0; at + sizeof(uint) <= data.Length; at += sizeof(uint))
            yield return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
    }

    /** A tag that names the played clip once, with that name replaced. */
    private static byte[] Renamed(byte[] tag, uint from, uint to)
    {
        byte[] output = (byte[])tag.Clone();
        int found = 0;
        for (int at = 0; at + sizeof(uint) <= output.Length; at += sizeof(uint))
            if (BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(at)) == from)
            { BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at), to); found++; }
        return found == 1 ? output : throw new InvalidDataException($"A template tag names its clip {found} times, not once.");
    }
}
