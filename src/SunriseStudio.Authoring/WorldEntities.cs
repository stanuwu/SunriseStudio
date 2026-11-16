using System.Numerics;
using SunriseStudio.Animation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using SunriseStudio.World;
using SunriseStudio.World.Entities;

namespace SunriseStudio.Authoring;

/** Where a new entity stands: a bubble of a project destination and a map-space transform. */
public sealed record WorldEntitySpot(string Destination, uint BubbleNameHash, Vector3 Position, Quaternion Rotation, float Scale = 1);

/** Adds entities to a project destination: the entity's files, and a placement in a bubble's own container. */
public static class WorldEntities
{
    /**
     * Builds a new animated entity and places it.
     * @param packageId The project package that takes the entity's model.
     * @return The entity's file name in the world.
     */
    public static string Place(StudioProject project, PackageCatalog catalog, RegistryIndex schema, RiggedEntityInput input,
        WorldEntitySpot spot, ushort packageId, CancellationToken token = default)
    {
        var world = WorldFiles.Load(project.ContentDirectory, spot.Destination);
        string file = Place(project, catalog, schema, world, input, spot, packageId, token);
        world.SaveChanged(project.ContentDirectory);
        return file;
    }

    /** Place on a loaded world; the caller saves it. */
    internal static string Place(StudioProject project, PackageCatalog catalog, RegistryIndex schema, WorldFiles world, RiggedEntityInput input,
        WorldEntitySpot spot, ushort packageId, CancellationToken token)
    {
        Refuse(project, world, spot, input.Name);
        int first = project.NextEntry(packageId);
        // Scratch tags only live until AddTags names the files, so every entity can use seed 0.
        var built = RiggedEntityBuilder.Build(catalog, schema, input, packageId, first, seed: 0, token);
        project.AddAssets(built.Assets, catalog);
        return Add(catalog, schema, world, built.Tags, input.Name, spot);
    }

    // The clip a device's position lane scrubs: lane value 0 shows its first frame, 1 its last.
    private const string PositionClip = "position";

    /**
     * Builds a new device, places it and binds it to a device slot of the source activity.
     * @param slot The slot's name in the source activity; a script moves the device through it.
     * @param position The action the slot's position lane scrubs.
     * @return The entity's file name in the world.
     */
    public static string PlaceDevice(StudioProject project, PackageCatalog catalog, RegistryIndex schema, RiggedEntityInput input,
        WorldEntitySpot spot, ushort packageId, string slot, string position, CancellationToken token = default)
    {
        var world = WorldFiles.Load(project.ContentDirectory, spot.Destination);
        string file = PlaceDevice(project, catalog, schema, world, input, spot, packageId, slot, position, token);
        world.SaveChanged(project.ContentDirectory);
        return file;
    }

    /** PlaceDevice on a loaded world; the caller saves it. */
    internal static string PlaceDevice(StudioProject project, PackageCatalog catalog, RegistryIndex schema, WorldFiles world,
        RiggedEntityInput input, WorldEntitySpot spot, ushort packageId, string slot, string position, CancellationToken token)
    {
        string file = Place(project, catalog, schema, world, AsDevice(input, position), spot, packageId, token);
        WorldDevices.Bind(world, schema, catalog, spot.Destination, slot, WorldContent.PlacementIdentity(Identity(spot, input.Name)), token);
        return file;
    }

    /** The input as a device whose position lane scrubs the action named `position`. */
    private static RiggedEntityInput AsDevice(RiggedEntityInput input, string position)
    {
        if (input.Clips.All(clip => clip.Name != position)) throw new InvalidDataException($"Entity {input.Name} has no action named {position}.");
        if (position != PositionClip && input.Clips.Any(clip => clip.Name == PositionClip))
            throw new InvalidDataException($"Entity {input.Name} already has an action named {PositionClip}.");
        return input with
        {
            Template = RiggedEntityBuilder.DeviceTemplate,
            Clips = input.Clips.Select(clip => clip.Name == position ? clip with { Name = PositionClip } : clip).ToArray(),
        };
    }

    /**
     * Copies an installed entity with its component configs and places the copy.
     * @param name Names the copy's placement list; unique in the destination.
     * @param also Further installed tags to copy with it, such as a particle system and its emitter.
     * @param replace New bytes for a copied tag, given by its installed tag.
     * @return The copy's file name in the world.
     */
    public static string PlaceCopy(StudioProject project, PackageCatalog catalog, RegistryIndex schema, uint entity, string name, WorldEntitySpot spot,
        IReadOnlyList<uint>? also = null, IReadOnlyDictionary<uint, byte[]>? replace = null)
    {
        var world = WorldFiles.Load(project.ContentDirectory, spot.Destination);
        Refuse(project, world, spot, name);
        string file = Add(catalog, schema, world, EntityBuilder.Clone(catalog, entity, also, replace), name, spot);
        world.SaveChanged(project.ContentDirectory);
        return file;
    }

    /** The vendor interaction configs among an installed entity's components, with the vendor each names. */
    public static IReadOnlyList<InvestmentVendorInteraction> VendorInteractions(PackageCatalog catalog, uint entity)
    {
        if (!catalog.TryGetEntry(entity, out var root) || root.Reference != SEntity.Class)
            throw new InvalidDataException($"0x{entity:X8} is not an installed entity.");
        var result = new List<InvestmentVendorInteraction>();
        foreach (uint tag in EntityBuilder.Components(catalog.Read(entity)))
        {
            try { result.Add(InvestmentVendorAuthoring.ReadInteraction(catalog, tag, catalog.Read(tag))); }
            catch (InvalidDataException) { }
        }
        return result;
    }

    /**
     * Copies an installed vendor NPC and places the copy. The copy's interaction config names
     * `vendorHash`, so the copy opens that vendor; the installed NPC keeps its own.
     * @param entity An installed entity with one vendor interaction config.
     * @param vendorHash The vendor the copy opens.
     * @return The copy's file name in the world.
     */
    public static string PlaceVendorCopy(StudioProject project, PackageCatalog catalog, RegistryIndex schema, uint entity, string name,
        WorldEntitySpot spot, uint vendorHash)
    {
        if (VendorInteractions(catalog, entity) is not [var site])
            throw new InvalidDataException($"0x{entity:X8} does not hold one vendor interaction.");
        var replace = new Dictionary<uint, byte[]>
        {
            [site.Tag] = InvestmentVendorAuthoring.RetargetInteraction(catalog, catalog.Read(site.Tag), site, vendorHash),
        };
        var world = WorldFiles.Load(project.ContentDirectory, spot.Destination);
        Refuse(project, world, spot, name);
        string file = Add(catalog, schema, world, EntityBuilder.Clone(catalog, entity, replace: replace), name, spot);
        world.SaveChanged(project.ContentDirectory);
        return file;
    }

    /**
     * Copies an installed enemy and makes one squad of a project destination spawn the copy.
     * @param squadConfig The squad's installed spawner config, as WorldSquads lists it.
     * @param health Share of health the copy starts with, above 0 up to 1; null keeps the installed share.
     * @param hitPoints Hit points of the copy's full pool at rank minor; null keeps the installed value.
     * @param modelPackage A project package that takes a new model for the copy; null keeps the installed model.
     * @param modelGlb A glb of one mesh skinned to the enemy's exported skeleton; null gives one box per bone.
     * @param clips Edited clips of the enemy: each installed clip tag with the controls glTF that replaces it for the copy.
     * @param clipEvents Events added to clips of the copy, by installed clip tag; see ClipEvent.NewTagged.
     * @return The copy's file name in the world.
     */
    public static string PlaceEnemyCopy(StudioProject project, PackageCatalog catalog, RegistryIndex schema, string destination,
        uint squadConfig, uint entity, float? health = null, float? hitPoints = null, ushort? modelPackage = null,
        string? modelGlb = null, IReadOnlyDictionary<uint, string>? clips = null, CancellationToken token = default,
        IReadOnlyDictionary<uint, IReadOnlyList<ClipEvent>>? clipEvents = null)
    {
        if (health is { } share && !(share > 0 && share <= 1)) throw new ArgumentOutOfRangeException(nameof(health));
        var replace = new Dictionary<uint, byte[]>();
        if (hitPoints is { } points && !(points > 0 && float.IsFinite(points))) throw new ArgumentOutOfRangeException(nameof(hitPoints));
        if (health is not null || hitPoints is not null)
        {
            if (!catalog.TryGetEntry(entity, out var root) || root.Reference != SEntity.Class)
                throw new InvalidDataException($"0x{entity:X8} is not an installed entity.");
            foreach (uint tag in EntityBuilder.Components(catalog.Read(entity)).Distinct())
            {
                byte[] config = catalog.Read(tag);
                if (CombatantDamage.Read(tag, config) is not { } damage) continue;
                if (health is { } fraction)
                    foreach (var pool in damage.Pools) config = CombatantDamage.SetPoolFraction(tag, config, pool.Index, fraction);
                if (hitPoints is { } value) config = CombatantDamage.SetBaseHitPoints(tag, config, value);
                replace[tag] = config;
            }
            if (replace.Count == 0) throw new InvalidDataException($"0x{entity:X8} has no damage config to set health in.");
        }
        if (modelGlb is not null && modelPackage is null) throw new ArgumentException("A model file needs a package for the new model.", nameof(modelPackage));
        if (modelPackage is { } packageId)
        {
            // The new model is skinned to the enemy's own skeleton, so its clips and behaviour still drive it.
            var rig = new SkeletonReader(catalog).Read(entity);
            string name = $"enemy_{entity:x8}_{squadConfig:x8}";
            int first = project.NextEntry(packageId);
            var options = new CompatibleSkinReplacementOptions(RiggedEntityBuilder.SkinEntity, RiggedEntityBuilder.SkinModel,
                RiggedEntityBuilder.SkinPart, name, packageId, first);
            var part = CompatibleSkinnedMesh.InstalledPart(catalog, entity, token);
            PreparedCompatibleSkin prepared;
            if (modelGlb is not null) prepared = CompatibleSkinnedMesh.PrepareNewRigGltf(catalog, modelGlb, entity, options, part, token);
            else
            {
                var (geometry, influences) = EnemyPlaceholderMesh.Boxes(rig, name);
                prepared = CompatibleSkinnedMesh.PrepareNewRig(catalog, geometry, influences, rig.Bones.Count, options, token: token, part: part);
            }
            uint model = EntityBuilder.Components(catalog.Read(entity))
                .Single(tag => EntityBuilder.DefinitionClass(catalog.Read(tag)) == SModelComponent.Class);
            replace[model] = EntityBuilder.WithModel(replace.GetValueOrDefault(model) ?? catalog.Read(model), prepared.ModelTag, rig.Bones.Count, matrixSkinned: true);
            project.AddAssets(prepared.Additions, catalog);
        }
        var also = new List<uint>();
        var newClips = new List<WorldTag>();
        if (clips is { Count: > 0 } || clipEvents is { Count: > 0 })
        {
            // The copy gets its own animation definition, which names a new clip in place of each edited one.
            uint definition = EntityBuilder.Components(catalog.Read(entity)).SelectMany(tag => Words(catalog.Read(tag)))
                .Where(word => catalog.TryGetEntry(word, out var found) && found.Reference == SAnimationDefinition.Class).Distinct().Single();
            byte[] names = catalog.Read(definition);
            foreach (uint clip in (clips?.Keys ?? []).Union(clipEvents?.Keys ?? []).ToList())
            {
                if (!catalog.TryGetEntry(clip, out var clipEntry) || clipEntry.Reference != SAnimationClip.Class)
                    throw new InvalidDataException($"0x{clip:X8} is not an installed clip.");
                // Clip tags take scratch seeds above the copy's own.
                uint tag = EntityBuilder.ScratchTag(1 + newClips.Count / EntityBuilder.SeedTags, newClips.Count % EntityBuilder.SeedTags);
                int named = 0;
                for (int at = 0; at + sizeof(uint) <= names.Length; at += sizeof(uint))
                    if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(names.AsSpan(at)) == clip)
                    { System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(names.AsSpan(at), tag); named++; }
                if (named == 0) throw new InvalidDataException($"The enemy's animation definition does not name clip 0x{clip:X8}.");
                byte[] data = clips is not null && clips.TryGetValue(clip, out string? path)
                    ? AnimationAssetImporter.PrepareGltf(catalog, clip, path).Data : catalog.Read(clip);
                if (clipEvents is not null && clipEvents.TryGetValue(clip, out var added))
                    data = AnimationClipEvents.Write(data, schema, AnimationClipEvents.Read(data, schema).Concat(added));
                newClips.Add(new(tag, clipEntry.Reference, clipEntry.TypeInfo, data));
            }
            replace[definition] = names;
            also.Add(definition);
        }
        var copy = EntityBuilder.Clone(catalog, entity, also, replace).ToList();
        copy.AddRange(newClips);
        var world = WorldFiles.Load(project.ContentDirectory, destination);
        string file = WorldSquads.SetEntityCopy(world, schema, catalog, destination, squadConfig, entity, copy, token);
        world.SaveChanged(project.ContentDirectory);
        return file;
    }

    /**
     * Builds a creature on its own skeleton over a copy of an installed enemy and makes one squad spawn it.
     * @param clipByBehaviour The creature clip each behaviour plays; Idle fills every other row.
     * @param packageId A project package that takes the creature's model.
     * @return The copy's file name in the world.
     */
    public static string PlaceCreatureEnemy(StudioProject project, PackageCatalog catalog, RegistryIndex schema, string destination,
        uint squadConfig, uint donorEnemy, RiggedEntityInput creature, IReadOnlyDictionary<CombatantBehaviour, string> clipByBehaviour,
        ushort packageId, CombatantCreatureOptions? options = null, CancellationToken token = default)
    {
        int first = project.NextEntry(packageId);
        var built = CombatantCreatureBuilder.Build(catalog, schema, donorEnemy, creature, clipByBehaviour, packageId, first, options, token: token);
        project.AddAssets(built.Assets, catalog);
        AddDisplayName(project, catalog, options);
        var world = WorldFiles.Load(project.ContentDirectory, destination);
        string file = AddCreatureCopy(world, schema, catalog, destination, donorEnemy, built);
        WorldSquads.SetEntity(world, schema, catalog, destination, squadConfig, file, token);
        world.SaveChanged(project.ContentDirectory);
        return file;
    }

    /** Adds a built creature as an entity copy; its shared clips join the world once, under their shared names. */
    private static string AddCreatureCopy(WorldFiles world, RegistryIndex schema, PackageCatalog catalog, string destination, uint donorEnemy,
        CombatantCreature built)
    {
        string file = WorldContent.AddTags(world, schema, catalog, destination, built.Tags, built.SharedClips)[0];
        // The pack builder reads this to give the squad's members the copied enemy's actor class.
        var added = world[file];
        world.Replace(new SunriseStudio.Tags.Tft.TftFile
        {
            Name = added.Name, TagType = added.TagType, GameName = added.GameName, ClassId = added.ClassId, TypeInfo = added.TypeInfo,
            BuildHash = added.BuildHash, Layout = added.Layout, Data = added.Data, References = added.References, Meta = added.Meta,
            Info = SunriseStudio.Tags.Tft.TftFile.CopyInfo(donorEnemy),
        });
        return file;
    }

    /**
     * Builds a creature over an installed enemy and adds it to the world as an entity copy, unbound to any squad.
     * @return The copy's world file name, which a new squad can name as its entity.
     */
    public static string AddCreatureEnemy(StudioProject project, PackageCatalog catalog, RegistryIndex schema, string destination,
        uint donorEnemy, RiggedEntityInput creature, IReadOnlyDictionary<CombatantBehaviour, string> clipByBehaviour, ushort packageId,
        CombatantCreatureOptions? options = null, CancellationToken token = default)
    {
        int first = project.NextEntry(packageId);
        var built = CombatantCreatureBuilder.Build(catalog, schema, donorEnemy, creature, clipByBehaviour, packageId, first, options, token: token);
        project.AddAssets(built.Assets, catalog);
        AddDisplayName(project, catalog, options);
        var world = WorldFiles.Load(project.ContentDirectory, destination);
        string file = AddCreatureCopy(world, schema, catalog, destination, donorEnemy, built);
        world.SaveChanged(project.ContentDirectory);
        return file;
    }

    /** Adds the creature's display name to the name bank as a project edit; the build only wrote its hash. */
    private static void AddDisplayName(StudioProject project, PackageCatalog catalog, CombatantCreatureOptions? options)
    {
        if (options?.DisplayName is not { } name) return;
        var edits = CombatantNameBank.WithNames(catalog, tag => project.ReadAsset(tag, catalog), [name]);
        if (edits.Count != 0) project.ApplyEdits(edits, catalog);
    }

    private static IEnumerable<uint> Words(byte[] data)
    {
        for (int at = 0; at + sizeof(uint) <= data.Length; at += sizeof(uint))
            yield return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
    }

    private static void Refuse(StudioProject project, WorldFiles world, WorldEntitySpot spot, string name)
    {
        if (name.Length == 0 || name.Any(value => value is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
            throw new InvalidDataException("An entity name holds lowercase letters, digits and underscores.");
        if (!float.IsFinite(spot.Scale) || spot.Scale <= 0) throw new InvalidDataException("An entity's scale must be above 0.");
        if (world.Files.Any(file => file.ClassId == SMapPlacements.Class && Path.GetFileNameWithoutExtension(file.Name) == name)
            || project.AddedAssets.Any(asset => asset.Name.StartsWith(name + ".", StringComparison.Ordinal)))
            throw new InvalidOperationException($"The project already holds {name}.");
    }

    private static string Add(PackageCatalog catalog, RegistryIndex schema, WorldFiles world, IReadOnlyList<WorldTag> tags, string name,
        WorldEntitySpot spot)
    {
        string container = WorldBubbles.OwnFirst(world, spot.BubbleNameHash)[0];
        var files = WorldContent.AddTags(world, schema, catalog, spot.Destination, tags);
        WorldContent.AddEntityList(world, spot.Destination, name, files[0], spot.Position, spot.Rotation, spot.Scale, Identity(spot, name), container);
        return files[0];
    }

    /** The text a placed entity's placement identity is made from. */
    public static string Identity(WorldEntitySpot spot, string name) => $"{spot.Destination}/{spot.BubbleNameHash:X8}/{name}";
}
