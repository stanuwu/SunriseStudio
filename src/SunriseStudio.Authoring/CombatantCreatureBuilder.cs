using System.Buffers.Binary;
using SunriseStudio.Animation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.Tags;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using SunriseStudio.World;
using SunriseStudio.World.Entities;

namespace SunriseStudio.Authoring;

/**
 * Choices for a creature built over an enemy. Bone names are the creature's; null means its root.
 * The switches exist so each can be turned off in game one at a time.
 */
public sealed record CombatantCreatureOptions
{
    public string? HeadBone { get; init; }
    public string? RightHandBone { get; init; }
    public string? LeftHandBone { get; init; }
    public string? RightFootBone { get; init; }
    public string? LeftFootBone { get; init; }
    /** Appends root children up to the donor's bone count, so donor bone indices stay in range; off pads to its group 0 count only. */
    public bool PadToDonorBones { get; init; } = true;
    /** Keeps the donor's twist corrections, which name donor bones. */
    public bool KeepCorrections { get; init; }
    /**
     * Keeps the donor's bones, controls and names exactly and appends the creature's after them, so every
     * donor config that binds to a bone still finds it. The model draws only on the appended bones.
     */
    public bool KeepDonorBones { get; init; } = true;
    /**
     * Superset: donor point names (rig name table, such as head, pelvis, right_hand) moved onto creature bones in
     * every absolute clip, so hit shapes, the name plate and the gun follow the creature. Null maps by nearest bone.
     */
    public IReadOnlyDictionary<string, string>? FollowBones { get; init; }
    /** Superset: moves the donor points at all; off leaves the donor bones in its idle pose. */
    public bool FollowDonorBones { get; init; } = true;
    /**
     * Superset: before the followed points move, the whole hidden donor pose shrinks about the entity origin to the
     * creature's height, so bones no map names (neck, aim and hit shapes) sit inside the creature too.
     */
    public bool ScaleDonorPose { get; init; } = true;
    /** Draws the creature with Texture in the colour slot of a copy of its skin material. */
    public bool OwnTexture { get; init; } = true;
    /**
     * Slots of the copied skin material rebound to one flat colour each, as RGBA bytes; empty keeps the stock maps.
     * The defaults suit 0x80FC0FBB: slot 0 at its stock texture's mean, slots 3 to 5 as flat normals.
     */
    public IReadOnlyDictionary<int, uint> FlatSlots { get; init; } = new Dictionary<int, uint>
    {
        [0] = 0xFFEB6E01, [3] = 0xFFFF8080, [4] = 0xFFFF8080, [5] = 0xFFFF8080,
    };
    /** The creature's base colour, such as CombatantCreatureSource.Texture; null keeps the stock material. */
    public SunriseStudio.Formats.RgbaImage? Texture { get; init; }
    /** Superset: the donor point whose bone parents the creature root, so the creature falls with that bone. */
    public string ParentPoint { get; init; } = "pelvis";
    /** Superset: every part of the donor's cloth model draws no indices. */
    public bool HideDonorCloth { get; init; } = true;
    /** Superset: every part of the donor's loadout weapon models draws no indices; the weapon still fires. */
    public bool HideWeapon { get; init; } = true;
    /** Superset: the physical body capsule in metres, written to a copy of the physics packfile; null keeps the donor's. */
    public CombatantCapsule? Capsule { get; init; }
    /**
     * Superset: every donor hit body moves to the entity root bone as one box around the creature's idle mesh.
     * Shared clips hold the hidden donor at full size, so hit bodies on donor bones miss a smaller creature.
     */
    public bool HitVolume { get; init; } = true;
    /** Superset: gun stats multiplied on every loadout gun, by CombatantGun stat name, such as damage_multiplier_base. */
    public IReadOnlyDictionary<string, float>? GunScale { get; init; }
    /** Superset: gun stats set on every loadout gun, by CombatantGun stat name, such as damage_multiplier_base. */
    public IReadOnlyDictionary<string, float>? GunStats { get; init; }
    /** Multiplies the hit points of every damage config the donor carries; null keeps the donor's. */
    public float? HealthScale { get; init; }
    /**
     * Superset: a slot whose donor clip carries root motion plays its creature clip resampled to the donor clip's
     * frames, with the donor's root motion, so the creature moves and turns as the donor does.
     */
    public bool DonorRootMotion { get; init; } = true;
    /** Root-motion copies hold their clip's first frame, so the creature moves in a still pose; each copy is then small. */
    public bool StillMotion { get; init; }
    /**
     * A localized string hash written over the donor's display name: word +8 of the actor group's local sequence
     * definition, which holds the hash of "Dreg" on the Dreg. Null keeps the donor's name.
     */
    public uint? DisplayNameHash { get; init; }
    /**
     * The name plate text. The definition takes its hash; WorldEntities adds the text to CombatantNameBank.Bank.
     * Null keeps the donor's name; it cannot be set with DisplayNameHash.
     */
    public string? DisplayName { get; init; }
    /** Off removes the `grenade` ability group from the copied actor definition, as enemies without grenades have none. */
    public bool Grenades { get; init; } = true;
    /** Off empties the copied loadout, as the melee-only Thrall's is, so no gun is given; melee stays. */
    public bool Ranged { get; init; } = true;
    /** Superset: an installed projectile entity every copied gun fires in place of its own; null keeps the donor's. */
    public uint? Projectile { get; init; }
    /** Superset: projectile values set on every copied projectile config, by CombatantProjectile value name, such as speed. */
    public IReadOnlyDictionary<string, float>? ProjectileValues { get; init; }
    /** Plays one still bind-pose clip in every slot without a pose space, to test the body apart from its clips. */
    public bool StillClips { get; init; }
    /** Gives unmapped layer rows a still relative clip; pose-space rows always get a still clip with their space. */
    public bool NeutralLayers { get; init; } = true;
    /**
     * Superset: pose-space, neutral and idle root-motion clips carry only the donor's control groups, so every
     * creature of one donor shares them. The creature's rig defaults hold its idle first frame for those clips.
     */
    public bool ShareDonorClips { get; init; } = true;
    /** With ShareDonorClips: a moving slot whose clip is not idle gets its own root-motion copy; off plays the plain clip, unmoved. */
    public bool OwnMotionCopies { get; init; } = true;
}

/** A movement capsule: radius, standing height, and optional crouch height and step height, in metres. */
public sealed record CombatantCapsule(float Radius, float Height, float? CrouchHeight = null, float? Step = null);

/** One definition slot: the donor clip it held, the behaviour that reaches it, and the creature clip it now plays. */
public sealed record CombatantSlotPlan(int Slot, uint DonorClip, CombatantBehaviour Behaviour, int DonorPoseMode, bool DonorPoseSpace, string Clip)
{
    /** The donor clip whose root motion this slot's clip carries; 0 for none. */
    public uint RootMotion { get; init; }
}

/** A built creature: the world tags, entity first, the model's project assets, and what each definition slot plays. */
public sealed record CombatantCreature(IReadOnlyList<WorldTag> Tags, IReadOnlyList<ProjectAssetAddition> Assets, uint ModelTag,
    int BoneCount, int CreatureBoneCount, IReadOnlyList<CombatantSlotPlan> Slots, IReadOnlyDictionary<string, uint> ClipTags,
    IReadOnlyDictionary<string, int> SlotsByClip)
{
    /** Leading bones that are the donor's own; 0 when the creature replaces them. */
    public int DonorBoneCount { get; init; }
    /** Clip tags that hold no creature data, with a name that is the same for every creature whose clip is the same. */
    public IReadOnlyDictionary<uint, string> SharedClips { get; init; } = new Dictionary<uint, string>();
}

/** Builds a creature with its own skeleton, rig, model and clips over a copy of an installed enemy. */
public static class CombatantCreatureBuilder
{
    /** The clip name every layer row plays when NeutralLayers is set. */
    public const string NeutralClip = "neutral_layer";
    /** The one clip every slot without a pose space plays when StillClips is set. */
    public const string StillClip = "still_pose";
    /** Separates a clip's name from the donor clip whose root motion its copy carries. */
    public const char MotionSeparator = '@';
    /** Clips that carry a donor's pose space take this prefix; one clip serves every slot with the same space. */
    public const string SpacePrefix = "pose_space_";
    /** A shared root-motion clip: the donor's held pose and one donor clip's root motion, after the separator. */
    public const string HeldClip = "donor_held";
    // The prop whose rig gives the root and child control rows and whose clip gives the codec header.
    private const uint TemplateRig = 0x80C14242;
    // When several queries reach one slot, the earlier behaviour wins.
    private static readonly CombatantBehaviour[] Priority =
    [
        CombatantBehaviour.Death, CombatantBehaviour.Flinch, CombatantBehaviour.Melee, CombatantBehaviour.Fire, CombatantBehaviour.Throw,
        CombatantBehaviour.Evade, CombatantBehaviour.Reaction, CombatantBehaviour.Spawn, CombatantBehaviour.Ability,
        CombatantBehaviour.Locomotion, CombatantBehaviour.Idle, CombatantBehaviour.Unknown
    ];
    private const int FramesPerSecond = 30;
    // A relative clip that adds nothing needs only two frames.
    private const int NeutralFrames = 2;
    private const byte RelativeMode = 1;

    /**
     * @param donor An installed enemy with a skeleton, a rig, a model, a selector and an animation component.
     * @param clipByBehaviour The creature clip each behaviour plays; Idle is required and fills every other row.
     * @param packageId The project package that takes the model and its buffers.
     * @param firstEntry The first of seven free entries in that package.
     * @param clipSeed Picks the clips' scratch tags; it must differ from 0, which the copy uses.
     */
    public static CombatantCreature Build(PackageCatalog catalog, RegistryIndex schema, uint donor, RiggedEntityInput creature,
        IReadOnlyDictionary<CombatantBehaviour, string> clipByBehaviour, ushort packageId, int firstEntry,
        CombatantCreatureOptions? options = null, int clipSeed = 1, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(clipByBehaviour);
        options ??= new();
        if (clipSeed == 0) throw new ArgumentOutOfRangeException(nameof(clipSeed), "Seed 0 holds the copy itself.");
        if (!clipByBehaviour.TryGetValue(CombatantBehaviour.Idle, out string? idle)) throw new ArgumentException("Idle needs a clip.", nameof(clipByBehaviour));
        var named = creature.Clips.ToDictionary(clip => clip.Name, StringComparer.Ordinal);
        foreach (string name in clipByBehaviour.Values)
            if (!named.ContainsKey(name)) throw new ArgumentException($"The creature has no clip named {name}.", nameof(clipByBehaviour));
        if (creature.Clips.Any(clip => clip.Bones.Count != creature.Bones.Count)) throw new ArgumentException("A clip must hold every bone.", nameof(creature));

        var configs = EntityBuilder.Components(catalog.Read(donor));
        uint Config(uint definitionClass) => configs.Single(tag => EntityBuilder.DefinitionClass(catalog.Read(tag)) == definitionClass);
        uint skeletonConfig = Config(SSkeletonResource.Class), modelConfig = Config(SModelComponent.Class);
        uint rigConfig = Config(SAnimationRigResource.Class), animationConfig = Config(SAnimationCorrectionOwnerResource.Class);
        var slots = CombatantAnimationSlots.Read(catalog, donor) ?? throw new InvalidDataException("The donor has no sequence selector.");
        byte[] donorSkeleton = catalog.Read(skeletonConfig);

        // The donor's entity state holds a pose for its first group 0 bones, so the skeleton never has fewer.
        int zero = SkeletonWriter.GroupZeroCount(donorSkeleton);
        var donorRig = new SkeletonReader(catalog).Read(donor);
        bool superset = options.KeepDonorBones;
        int offset = superset ? donorRig.Bones.Count : 0;
        int parentBone = superset ? PointBone(catalog.Read(rigConfig), options.ParentPoint) : -1;
        var bones = superset ? Superset(donorRig, creature, parentBone)
            : Bones(creature, Math.Max(zero, options.PadToDonorBones ? donorRig.Bones.Count : 0));
        if (bones.Count > MaximumBones) throw new NotSupportedException($"The skeleton would hold {bones.Count} bones; a model addresses at most {MaximumBones}.");
        var locals = bones.Select(bone => SkeletonWriter.Local(bones, bone)).ToArray();

        var influences = creature.Influences.Select(value => value with { Bone0 = value.Bone0 + offset, Bone1 = value.Bone1 + offset,
            Bone2 = value.Bone2 + offset, Bone3 = value.Bone3 + offset }).ToArray();

        var prepared = CompatibleSkinnedMesh.PrepareNewRig(catalog, creature.Geometry, influences, bones.Count,
            new(RiggedEntityBuilder.SkinEntity, RiggedEntityBuilder.SkinModel, RiggedEntityBuilder.SkinPart, creature.Name, packageId, firstEntry),
            creature.Material ?? CompatibleSkinnedMesh.NewRigMaterial, token: token, part: CompatibleSkinnedMesh.InstalledPart(catalog, donor, token));

        byte[] templateRig = catalog.Read(TemplateRig);
        var (stockClip, groupName) = TemplateClip(catalog);
        var clipEntry = catalog.TryGetEntry(stockClip, out var found) ? found : throw new InvalidDataException("The template clip is missing.");
        var plan = SlotPlan(catalog, slots, clipByBehaviour, idle, options.NeutralLayers, options.StillClips, superset && options.DonorRootMotion);
        bool share = superset && options.ShareDonorClips;
        // An idle copy only moves the body, so a donor-only clip with the same root motion stands in for it.
        if (share)
            plan = plan.Select(row => row.RootMotion == 0 ? row
                : row.Clip.Split(MotionSeparator)[0] == idle ? row with { Clip = $"{HeldClip}{MotionSeparator}{row.RootMotion:x8}" }
                : options.OwnMotionCopies ? row : row with { Clip = row.Clip.Split(MotionSeparator)[0], RootMotion = 0 }).ToArray();
        var motions = plan.Where(row => row.RootMotion != 0).GroupBy(row => row.Clip, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().RootMotion, StringComparer.Ordinal);
        var used = plan.Select(row => row.Clip).Distinct(StringComparer.Ordinal).ToList();
        var spaceDonors = plan.Where(row => row.Clip.StartsWith(SpacePrefix, StringComparison.Ordinal))
            .GroupBy(row => row.Clip, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().DonorClip, StringComparer.Ordinal);
        var reader = new AnimationControlReader(catalog);
        var donorBinding = superset ? new AnimationRigReader(catalog).Read(donorRig) : null;
        BoneTransform[] held = donorBinding is null ? [] : CombatantAnimationConverter.SampleControls(donorBinding,
            reader.Read(slots.OtherRows.Concat(slots.Queries.SelectMany(query => query.Rows)).First(row => row.NameRow == 0).ClipTag), 0);
        int creatureCount = bones.Count - offset;
        var follow = donorBinding is null || !options.FollowDonorBones ? []
            : FollowMap(donorRig, catalog.Read(rigConfig), creature, options.FollowBones);
        var heldPose = donorBinding?.Evaluate(held).ToArray() ?? [];
        float scale = donorBinding is null || !options.FollowDonorBones || !options.ScaleDonorPose ? 1
            : creature.Bones.Max(bone => bone.BindObject.Translation.Z) / Math.Max(0.01f, donorRig.Bones.Max(bone => bone.BindObject.Translation.Z));
        using var staging = donorBinding is null ? null : WeaponAssetAnimationCompiler.OpenStaging(catalog);

        // Samples per control. A relative clip moves nothing. An absolute clip moves the followed donor points onto
        // their creature bones and solves the donor controls for them; the creature root is then made local to its
        // parent's solved transform, so the creature stands where its own clip puts it.
        byte[] Compile(string name, IReadOnlyList<IReadOnlyList<BoneTransform>> creatureTracks, int count, bool relative)
        {
            uint hash = AnimationDefinitionWriter.NameHash(name);
            if (donorBinding is null) return RigClipCompiler.Prepare(catalog, stockClip, groupName, hash, creatureTracks);
            var donorTracks = Enumerable.Range(0, held.Length).Select(_ => new BoneTransform[count]).ToArray();
            var tracks = creatureTracks.Select(track => track.ToArray()).ToArray();
            for (int frame = 0; frame < count; frame++)
            {
                if (relative)
                {
                    foreach (var track in donorTracks) track[frame] = BoneTransform.Identity;
                    continue;
                }
                var objects = new BoneTransform[creatureCount];
                for (int bone = 0; bone < creatureCount; bone++)
                {
                    int parent = bones[offset + bone].Parent - offset;
                    objects[bone] = parent < 0 ? tracks[bone][frame] : BoneTransform.FromMatrix(tracks[bone][frame].Matrix * objects[parent].Matrix);
                }
                var targets = heldPose.ToArray();
                if (scale != 1)
                    for (int bone = 0; bone < targets.Length; bone++) targets[bone] = targets[bone] with { Translation = targets[bone].Translation * scale };
                foreach (var (donorBone, creatureBone) in follow)
                    targets[donorBone] = targets[donorBone] with { Translation = objects[creatureBone].Translation };
                var values = follow.Count == 0 && scale == 1 ? held : donorBinding.Solve(targets);
                for (int control = 0; control < values.Length; control++) donorTracks[control][frame] = values[control];
                var solved = donorBinding.Evaluate(values);
                int root = creature.Bones.ToList().FindIndex(bone => bone.Parent < 0);
                tracks[root][frame] = Compose(Inverse(solved[bones[offset + root].Parent]), objects[root]);
            }
            var controls = donorTracks.Select(track => (IReadOnlyList<BoneTransform>)track).Concat(tracks.Select(track => (IReadOnlyList<BoneTransform>)track)).ToArray();
            var groups = donorBinding.ControlGroups.Append(new AnimationControlGroup(groupName, creatureCount)).ToArray();
            return CombatantCreatureClips.Prepare(catalog, stockClip, bones.Count, groups, hash, controls, staging);
        }
        // The donor's groups alone: a clip writes only its leading groups, so the creature's controls keep their rig defaults.
        byte[] CompileDonor(string name, int count, bool relative)
        {
            var controls = Enumerable.Range(0, held.Length).Select(control =>
                (IReadOnlyList<BoneTransform>)Enumerable.Repeat(relative ? BoneTransform.Identity : held[control], count).ToArray()).ToArray();
            return CombatantCreatureClips.Prepare(catalog, stockClip, donorRig.Bones.Count, donorBinding!.ControlGroups,
                AnimationDefinitionWriter.NameHash(name), controls, staging);
        }
        IReadOnlyList<IReadOnlyList<BoneTransform>> Repeat(Func<int, BoneTransform> value, int count) =>
            Enumerable.Range(0, creatureCount).Select(bone => (IReadOnlyList<BoneTransform>)Enumerable.Repeat(value(bone), count).ToArray()).ToArray();
        // Creature-space bind tracks: the root as its object bind, every other bone as its local bind.
        BoneTransform BindTrack(int bone) => superset && creature.Bones[bone].Parent < 0 ? creature.Bones[bone].BindObject : locals[offset + bone];

        var clipTags = new Dictionary<string, uint>(StringComparer.Ordinal);
        var frames = new Dictionary<string, int>(StringComparer.Ordinal);
        var clips = new List<WorldTag>();
        var shared = new Dictionary<uint, string>();
        foreach (string name in used)
        {
            // Clip tags fill one seed's scratch tags, then the next seed's.
            uint tag = EntityBuilder.ScratchTag(clipSeed + clips.Count / EntityBuilder.SeedTags, clips.Count % EntityBuilder.SeedTags);
            string baseName = name.Split(MotionSeparator)[0];
            int? motionFrames = motions.TryGetValue(name, out uint motion) ? reader.Read(motion).FrameCount : null;
            byte[] bytes;
            bool donorOnly = false;
            if (spaceDonors.TryGetValue(name, out uint spaceDonor))
            {
                int count = reader.Read(spaceDonor).FrameCount;
                donorOnly = share;
                bytes = share ? CompileDonor(name, count, relative: true) : Compile(name, Repeat(_ => BoneTransform.Identity, count), count, relative: true);
                bytes = CombatantAnimationPoseSpace.Write(bytes, catalog.Read(spaceDonor), schema);
                frames[name] = count;
            }
            else if (share && baseName == HeldClip && motionFrames is int heldFrames)
            {
                donorOnly = true;
                bytes = CompileDonor(name, heldFrames, relative: false);
                frames[name] = heldFrames;
            }
            else if (baseName == StillClip && !named.ContainsKey(StillClip))
            {
                int count = motionFrames ?? NeutralFrames;
                bytes = Compile(name, Repeat(BindTrack, count), count, relative: false);
                frames[name] = count;
            }
            else if (name == NeutralClip && !named.ContainsKey(NeutralClip))
            {
                donorOnly = share;
                bytes = share ? CompileDonor(name, NeutralFrames, relative: true)
                    : Compile(name, Repeat(_ => BoneTransform.Identity, NeutralFrames), NeutralFrames, relative: true);
                bytes[TagView.FieldOffset<SAnimationClip>(nameof(SAnimationClip.PoseMode))] = RelativeMode;
                frames[name] = NeutralFrames;
            }
            else
            {
                var clip = named[baseName];
                int count = motionFrames ?? clip.Bones[0].Count;
                var tracks = clip.Bones.Select(track => motionFrames is null ? track
                        : options.StillMotion ? Enumerable.Repeat(track[0], count).ToArray() : Resample(track, count))
                    .Concat(Enumerable.Range(0, creatureCount - creature.Bones.Count).Select(_ =>
                        (IReadOnlyList<BoneTransform>)Enumerable.Repeat(BoneTransform.Identity, count).ToArray())).ToArray();
                bytes = Compile(name, tracks, count, relative: false);
                // Events keep their frame on the clip's own timeline; a resampled copy drops them.
                if (clip.Events.Any(value => value.Frame < 0 || value.Frame >= clip.Bones[0].Count))
                    throw new ArgumentException($"Clip {baseName} has an event outside its frames.", nameof(creature));
                if (clip.Events.Count > 0 && motionFrames is null) bytes = AnimationClipEvents.Write(bytes, schema, clip.Events, compact: true);
                frames[name] = count;
            }
            if (motionFrames is not null) bytes = CombatantAnimationPoseSpace.WithRootMotion(bytes, catalog.Read(motion), schema);
            // The world build loads every tag a clip word names; a sample that spells one would drag a foreign tag in.
            CombatantAnimationPoseSpace.ScrubTagWords(bytes, catalog.ContainsTag);
            if (donorOnly) shared[tag] = $"creature_{donor:x8}_{ContentHash(bytes):x16}";
            clipTags[name] = tag;
            clips.Add(new(tag, clipEntry.Reference, clipEntry.TypeInfo, bytes));
        }
        var parents = bones.Select(bone => bone.Parent).ToArray();
        // A donor-only clip leaves the creature at its rig defaults: the idle first frame, the root placed on the held pose.
        var defaults = !share ? locals[offset..] : Enumerable.Range(0, creatureCount).Select(bone => creature.Bones[bone].Parent < 0
            ? Compose(Inverse(heldPose[bones[offset + bone].Parent]), named[idle].Bones[bone][0]) : named[idle].Bones[bone][0]).ToArray();
        var replace = new Dictionary<uint, byte[]>
        {
            [skeletonConfig] = SkeletonWriter.Write(donorSkeleton, schema, bones),
            [rigConfig] = superset
                ? CombatantAnimationRig.Append(catalog.Read(rigConfig), templateRig, schema, parents[offset..], defaults, offset)
                : CombatantAnimationRig.Write(catalog.Read(rigConfig), templateRig, schema, parents, locals, PointNames(catalog.Read(rigConfig), creature, options)),
            [modelConfig] = EntityBuilder.WithModel(catalog.Read(modelConfig), prepared.ModelTag, bones.Count, matrixSkinned: true),
            [slots.DefinitionTag] = Definition(catalog.Read(slots.DefinitionTag), plan.Select(row => clipTags[row.Clip]).ToArray(),
                plan.Select(row => (frames[row.Clip] - 1) / (float)FramesPerSecond).ToArray()),
        };
        // The superset keeps the donor's bones, so its corrections still name the right ones.
        if (!superset && !options.KeepCorrections) replace[animationConfig] = CombatantAnimationRig.WithoutCorrections(catalog.Read(animationConfig), schema);
        var also = new List<uint> { slots.DefinitionTag };
        if (superset && options.HideDonorCloth)
            foreach (uint cloth in configs.Where(tag => EntityBuilder.DefinitionClass(catalog.Read(tag)) == SClothModelComponent.Class))
                Hide(ModelOf(catalog.Read(cloth), SClothModelComponent.Class, TagView.FieldOffset<SClothModelComponent>(nameof(SClothModelComponent.Model))));
        bool shotEdits = options.Projectile is not null || options.ProjectileValues is { Count: > 0 };
        bool gunEdits = shotEdits || options.GunStats is { Count: > 0 } || options.GunScale is { Count: > 0 };
        if (!options.Ranged)
        {
            if (gunEdits) throw new ArgumentException("Gun and projectile options need Ranged on.", nameof(options));
            var loadouts = configs.Where(tag => CombatantLoadout.Read(tag, catalog.Read(tag)) is not null).ToArray();
            if (loadouts.Length == 0) throw new InvalidDataException($"0x{donor:X8} has no loadout to empty.");
            foreach (uint loadout in loadouts)
            {
                if (!also.Contains(loadout)) also.Add(loadout);
                replace[loadout] = CombatantLoadout.WithoutWeapons(loadout, catalog.Read(loadout));
            }
        }
        if (shotEdits && !superset) throw new ArgumentException("Projectile options need KeepDonorBones.", nameof(options));
        if (superset && options.Ranged && (options.HideWeapon || gunEdits))
        {
            var weapons = CombatantWeaponCopy.Plan(catalog, donor);
            if (options.Projectile is uint shot) weapons = CombatantWeaponCopy.WithProjectile(catalog, weapons, shot);
            foreach (var (name, value) in options.GunStats ?? new Dictionary<string, float>())
                weapons = CombatantWeaponCopy.WithGunStat(catalog, weapons, name, value);
            weapons = ScaleGuns(catalog, weapons, options.GunScale);
            foreach (var (name, value) in options.ProjectileValues ?? new Dictionary<string, float>())
                weapons = CombatantWeaponCopy.WithProjectileValue(catalog, weapons, name, value);
            foreach (uint tag in weapons.Also) if (!also.Contains(tag)) also.Add(tag);
            foreach (var (tag, bytes) in weapons.Replace) replace[tag] = bytes;
            foreach (uint weapon in weapons.Weapons.Where(_ => options.HideWeapon))
                foreach (uint config in EntityBuilder.Components(catalog.Read(weapon)).Where(tag => EntityBuilder.DefinitionClass(catalog.Read(tag)) == SModelComponent.Class))
                {
                    if (!also.Contains(config)) also.Add(config);
                    Hide(ModelOf(catalog.Read(config), SModelComponent.Class, TagView.FieldOffset<SModelComponent>(nameof(SModelComponent.Model))));
                }
        }
        if (options.HealthScale is { } healthScale)
        {
            if (!(healthScale > 0 && float.IsFinite(healthScale))) throw new ArgumentOutOfRangeException(nameof(options), "HealthScale must be above 0.");
            int scaled = 0;
            foreach (uint tag in configs)
            {
                byte[] config = catalog.Read(tag);
                if (CombatantDamage.Read(tag, config) is not { } damage) continue;
                if (!also.Contains(tag)) also.Add(tag);
                replace[tag] = CombatantDamage.SetBaseHitPoints(tag, config, damage.BaseHitPoints * healthScale);
                scaled++;
            }
            if (scaled == 0) throw new InvalidDataException($"0x{donor:X8} has no damage config to scale health in.");
        }
        if (options.DisplayNameHash is not null && options.DisplayName is not null)
            throw new ArgumentException("Set DisplayName or DisplayNameHash, not both.", nameof(options));
        if (options.DisplayName is { } text && string.IsNullOrWhiteSpace(text)) throw new ArgumentException("DisplayName cannot be empty.", nameof(options));
        uint? display = options.DisplayNameHash ?? (options.DisplayName is { } plate ? CombatantNameBank.Hash(plate) : null);
        if (display is not null || !options.Grenades)
        {
            uint source = configs.Single(tag => EntityBuilder.DefinitionClass(catalog.Read(tag)) == ActorGroupSource);
            byte[] sourceBytes = catalog.Read(source);
            int at = TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition));
            uint local = BitConverter.ToUInt32(sourceBytes, at + (int)BitConverter.ToInt64(sourceBytes, at) + LocalDefinitionField);
            byte[] edited = (byte[])catalog.Read(local).Clone();
            if (display is uint hash) BitConverter.TryWriteBytes(edited.AsSpan(DisplayNameField), hash);
            // A donor without the group throws no grenade already.
            if (!options.Grenades && CombatantAbilityDefinition.Read(edited)!.Groups.Any(group => group.GroupHash == CombatantAbilityDefinition.GrenadeGroup))
                edited = CombatantAbilityDefinition.WithoutGroup(edited, CombatantAbilityDefinition.GrenadeGroup);
            if (!also.Contains(local)) also.Add(local);
            replace[local] = edited;
        }
        uint physicsPack = 0, physicsHolder = 0;
        byte[]? patchedPack = null;
        bool hitVolume = superset && options.HitVolume;
        if (options.Capsule is not null || hitVolume)
        {
            uint move = configs.Single(tag => EntityBuilder.DefinitionClass(catalog.Read(tag)) == MovementConfig);
            var donorShape = CapsuleShape.Read(catalog.Read(move));
            // The physics packfile holds bodies baked from the movement config; the player collides with those.
            (physicsPack, physicsHolder) = CapsuleShape.FindPackfile(catalog, configs, donorShape);
            patchedPack = catalog.Read(physicsPack);
            if (options.Capsule is { } capsule)
            {
                // Only the physics bodies change. The movement config keeps the donor's radius: a radius of 1 there crashed
                // the pathfinder, which likely picks its navmesh agent class from it.
                var written = CapsuleShape.Read(WithCapsule(catalog.Read(move), capsule));
                patchedPack = CapsuleShape.Patch(patchedPack, donorShape, written);
            }
            if (hitVolume)
            {
                var (low, high) = CombatantHitVolume.IdleBox(creature, idle);
                (replace[physicsHolder], patchedPack) = CombatantHitVolume.Write(catalog.Read(physicsHolder), patchedPack, physicsPack, low, high);
            }
            // The packfile is a raw file, not a tag: it ships as an asset, and the copied holder names it.
            if (!also.Contains(physicsHolder)) also.Add(physicsHolder);
        }
        void Hide(uint model)
        {
            if (model is 0 or uint.MaxValue || replace.ContainsKey(model)) return;
            if (!also.Contains(model)) also.Add(model);
            replace[model] = HiddenModel(catalog.Read(model));
        }
        var tags = EntityBuilder.Clone(catalog, donor, also, replace).ToList();
        tags.AddRange(clips);
        var counts = plan.GroupBy(row => row.Clip, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var assets = options.OwnTexture && options.Texture is { } image
            ? Textured(catalog, prepared, image, creature, packageId, options.FlatSlots, token) : prepared.Additions;
        if (patchedPack is not null)
        {
            int entry = assets.Max(asset => asset.Native?.EntryIndex ?? -1) + 1;
            uint packTag = TagHashes.Make(packageId, entry);
            if (!catalog.TryGetEntry(physicsPack, out var packEntry)) throw new InvalidDataException("The physics packfile is not installed.");
            assets = [.. assets, new(creature.Name + ".physics", patchedPack,
                new NativeAssetMetadata(packageId, packEntry.Reference, packEntry.TypeInfo, creature.Name + ".physics", entry), physicsPack)];
            var holder = tags.Single(tag => tag.Source == physicsHolder);
            int renamed = 0;
            for (int at = 0; at + 4 <= holder.Data.Length; at += 4)
                if (BitConverter.ToUInt32(holder.Data, at) == physicsPack) { BitConverter.TryWriteBytes(holder.Data.AsSpan(at), packTag); renamed++; }
            if (renamed == 0) throw new InvalidDataException("The copied physics config does not name the packfile.");
        }
        return new(tags, assets, prepared.ModelTag, bones.Count, creature.Bones.Count, plan, clipTags, counts)
        {
            DonorBoneCount = offset, SharedClips = shared,
        };
    }

    /** FNV-1a over the bytes; names a shared clip by its content. */
    private static ulong ContentHash(byte[] bytes)
    {
        ulong hash = 0xCBF29CE484222325;
        foreach (byte value in bytes) hash = unchecked((hash ^ value) * 0x100000001B3);
        return hash;
    }

    /** A 16 by 16 image of one colour; the low byte of `rgba` is red. */
    private static RgbaImage Flat(uint rgba) =>
        new(16, 16, Enumerable.Range(0, 16 * 16).SelectMany(_ => BitConverter.GetBytes(rgba)).ToArray());

    // The skin material's colour texture: its one sRGB slot (BC7 sRGB in the installed material).
    private const int ColourSlot = 1;

    /**
     * The model's assets with a texture and a copy of its skin material that binds it in the colour slot, and the
     * model's parts moved onto that copy. Only the texture row differs from the installed material, so its
     * shaders stay the checked skinning shaders that PrepareNewRig validated.
     */
    private static IReadOnlyList<ProjectAssetAddition> Textured(PackageCatalog catalog, PreparedCompatibleSkin prepared, RgbaImage image,
        RiggedEntityInput creature, ushort packageId, IReadOnlyDictionary<int, uint> flatSlots, CancellationToken token)
    {
        var assets = prepared.Additions.ToList();
        int next = assets.Max(asset => asset.Native?.EntryIndex ?? -1) + 1;
        var texture = TextureAssetImporter.PrepareEncoded(catalog, image, creature.Name + ".color", packageId, next, TextureEncoding.Bc7, srgb: true, token: token);
        assets.AddRange(texture.Assets);
        int entry = texture.Assets.Max(asset => asset.Native!.EntryIndex!.Value) + 1;
        uint stock = creature.Material ?? CompatibleSkinnedMesh.NewRigMaterial;
        if (!catalog.TryGetEntry(stock, out var materialEntry)) throw new InvalidDataException("The skin material is not installed.");
        byte[] material = EffectMaterial.WithTexture(catalog.Read(stock), ColourSlot, texture.HeaderTag);
        // The stock maps are painted for the donor's cloth; on the creature's uvs they draw as scratches and patches.
        foreach (var (colour, slots) in flatSlots.GroupBy(pair => pair.Value, pair => pair.Key).Select(group => (group.Key, group.ToArray())))
        {
            var flat = TextureAssetImporter.PrepareEncoded(catalog, Flat(colour), $"{creature.Name}.flat{colour:x8}", packageId, entry,
                TextureEncoding.Bc7, srgb: false, token: token);
            assets.AddRange(flat.Assets);
            entry = flat.Assets.Max(asset => asset.Native!.EntryIndex!.Value) + 1;
            foreach (int slot in slots) material = EffectMaterial.WithTexture(material, slot, flat.HeaderTag);
        }        uint materialTag = TagHashes.Make(packageId, entry);
        assets.Add(new(creature.Name + ".material", material,
            new NativeAssetMetadata(packageId, materialEntry.Reference, materialEntry.TypeInfo, creature.Name + ".material", entry), stock));
        int model = assets.FindIndex(asset => asset.Native is { } native && native.PackageId == packageId
            && native.EntryIndex == TagHashes.Split(prepared.ModelTag).EntryIndex);
        byte[] bytes = (byte[])assets[model].Data.Clone();
        ref readonly var root = ref TagView.Checked<SEntityModel>(bytes, 0);
        int moved = 0;
        foreach (ref readonly var mesh in TagView.Rows(bytes, in root.Meshes, SEntityModelMesh.Class))
            foreach (ref var part in TagView.EditRows(bytes, in mesh.Parts, SEntityModelPart.Class))
                if (part.Material == stock) { part.Material = materialTag; moved++; }
        if (moved == 0) throw new InvalidDataException("The new model draws no part with its skin material.");
        assets[model] = assets[model] with { Data = bytes };
        return assets;
    }

    // The skin profile addresses at most this many bones.
    private const int MaximumBones = 256;

    /** The donor's bones exactly as installed, then the creature's, its root under the donor bone parent. */
    private static IReadOnlyList<SkeletonBoneInput> Superset(SkeletonRig donor, RiggedEntityInput creature, int parent)
    {
        var bones = donor.Bones.Select(bone => new SkeletonBoneInput(bone.NameHash, bone.ParentIndex, bone.BindObject, bone.InverseBindObject, bone.BindLocal)).ToList();
        int offset = bones.Count;
        bones.AddRange(creature.Bones.Select(bone => new SkeletonBoneInput(AnimationDefinitionWriter.NameHash(bone.Name),
            bone.Parent < 0 ? parent : bone.Parent + offset, bone.BindObject)));
        return bones;
    }

    /** The model tag a model or cloth component config names. */
    private static uint ModelOf(byte[] config, uint resourceClass, int modelField)
    {
        int at = TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition));
        int resource = at + (int)BitConverter.ToInt64(config, at);
        if (resource < 4 || resource + modelField + 4 > config.Length || BitConverter.ToUInt32(config, resource - 4) != resourceClass)
            throw new InvalidDataException($"The config is not a 0x{resourceClass:X8} component.");
        return BitConverter.ToUInt32(config, resource + modelField);
    }

    // The actor group source names its local sequence definition at +760; that definition holds the display name at +8.
    private const uint ActorGroupSource = 0x808082EC;
    /** The plan with each gun's stat multiplied from its own current value. */
    private static CombatantWeaponCopyPlan ScaleGuns(PackageCatalog catalog, CombatantWeaponCopyPlan plan, IReadOnlyDictionary<string, float>? scale)
    {
        if (scale is not { Count: > 0 }) return plan;
        var replace = new Dictionary<uint, byte[]>(plan.Replace);
        foreach (uint gun in plan.Chains.Select(chain => chain.Gun).Distinct())
        {
            byte[] bytes = replace.TryGetValue(gun, out var edited) ? edited : catalog.Read(gun);
            foreach (var (name, factor) in scale)
            {
                var read = CombatantGun.Read(gun, bytes) ?? throw new InvalidDataException($"Gun 0x{gun:X8} has no gun payload.");
                bytes = CombatantGun.SetStat(gun, bytes, name, read.Stat(name) * factor);
            }
            replace[gun] = bytes;
        }
        return plan with { Replace = replace };
    }

    // Movement config: capsule floats at these offsets from its definition.
    private const uint MovementConfig = 0x80806908;
    private const int CapsuleHeight = 0x1B0, CapsuleCrouch = 0x1B4, CapsuleRadius = 0x1BC, CapsuleStep = 0x1C0;

    /** The movement config with the capsule written; the stock rule height >= step + 2 * radius must hold. */
    private static byte[] WithCapsule(byte[] config, CombatantCapsule capsule)
    {
        byte[] output = (byte[])config.Clone();
        int at = TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition));
        int definition = at + (int)BitConverter.ToInt64(output, at);
        if (definition + CapsuleStep + 4 > output.Length) throw new InvalidDataException("The movement config ends before its capsule.");
        float step = capsule.Step ?? BitConverter.ToSingle(output, definition + CapsuleStep);
        float least = step + 2 * capsule.Radius;
        if (capsule.Radius <= 0 || capsule.Height < least)
            throw new ArgumentException($"A capsule of radius {capsule.Radius} on step {step} needs a height of at least {least}.");
        // Without a crouch height, the donor's is kept but raised to the least the radius allows.
        float crouch = capsule.CrouchHeight ?? Math.Clamp(BitConverter.ToSingle(output, definition + CapsuleCrouch), least, capsule.Height);
        if (crouch < least || crouch > capsule.Height)
            throw new ArgumentException($"A crouch height of {crouch} must lie between {least} and the height {capsule.Height}.");
        BitConverter.TryWriteBytes(output.AsSpan(definition + CapsuleHeight), capsule.Height);
        BitConverter.TryWriteBytes(output.AsSpan(definition + CapsuleCrouch), crouch);
        BitConverter.TryWriteBytes(output.AsSpan(definition + CapsuleRadius), capsule.Radius);
        BitConverter.TryWriteBytes(output.AsSpan(definition + CapsuleStep), step);
        return output;
    }
    private const int LocalDefinitionField = 760, DisplayNameField = 8;
    // A part with this flag is never drawn.
    private const ushort NeverDrawn = 0x40;

    /** The model with every part of every mesh given no indices and flagged never drawn; vertices, bones and materials stay. */
    public static byte[] HiddenModel(byte[] model)
    {
        byte[] output = (byte[])model.Clone();
        ref readonly var root = ref TagView.Checked<SEntityModel>(output, 0);
        var meshes = TagView.Rows(output, in root.Meshes, SEntityModelMesh.Class);
        for (int mesh = 0; mesh < meshes.Length; mesh++)
        {
            var parts = TagView.EditRows(output, in meshes[mesh].Parts, SEntityModelPart.Class);
            // The draw reads each part's index range, so an empty range draws nothing; the flag is what Studio skips.
            for (int part = 0; part < parts.Length; part++) { parts[part].PartFlags |= NeverDrawn; parts[part].IndexCount = 0; }
        }
        return output;
    }

    /** The bone a donor point name resolves to through the rig's name table. */
    private static int PointBone(byte[] rig, string point)
    {
        uint hash = AnimationDefinitionWriter.NameHash(point);
        var rows = CombatantAnimationRig.NameTable(rig).Where(row => row.Name == hash).ToArray();
        return rows.Length > 0 ? rows[0].Bone : throw new ArgumentException($"The donor rig names no point {point}.");
    }

    // The donor points moved onto the creature when no map is given; they carry the hit shapes, the plate and the gun.
    private static readonly string[] DefaultFollow = ["pelvis", "sternum", "neck_base", "head", "right_hand", "left_hand", "right_grip", "left_grip"];

    /**
     * Donor bone to creature bone (creature index, from 0). Without a map each default point takes the creature bone
     * nearest to it once both bodies are scaled to the same height.
     */
    private static List<(int Donor, int Creature)> FollowMap(SkeletonRig donor, byte[] rig, RiggedEntityInput creature,
        IReadOnlyDictionary<string, string>? map)
    {
        var result = new List<(int, int)>();
        if (map is not null)
        {
            foreach (var (point, bone) in map)
            {
                int index = creature.Bones.ToList().FindIndex(value => value.Name == bone);
                if (index < 0) throw new ArgumentException($"The creature has no bone named {bone}.", nameof(map));
                result.Add((PointBone(rig, point), index));
            }
            return result;
        }
        float donorHeight = Math.Max(0.01f, donor.Bones.Max(bone => bone.BindObject.Translation.Z));
        float creatureHeight = Math.Max(0.01f, creature.Bones.Max(bone => bone.BindObject.Translation.Z));
        var names = CombatantAnimationRig.NameTable(rig).Select(row => row.Name).ToHashSet();
        foreach (string point in DefaultFollow.Where(point => names.Contains(AnimationDefinitionWriter.NameHash(point))))
        {
            int target = PointBone(rig, point);
            var at = donor.Bones[target].BindObject.Translation * (creatureHeight / donorHeight);
            int nearest = Enumerable.Range(0, creature.Bones.Count).MinBy(index => System.Numerics.Vector3.DistanceSquared(creature.Bones[index].BindObject.Translation, at));
            result.Add((target, nearest));
        }
        return result;
    }

    /** A track resampled to count frames over the same span; rotations by normalized linear blend. */
    private static IReadOnlyList<BoneTransform> Resample(IReadOnlyList<BoneTransform> track, int count)
    {
        var result = new BoneTransform[count];
        for (int frame = 0; frame < count; frame++)
        {
            float at = count == 1 ? 0 : frame * (track.Count - 1) / (float)(count - 1);
            int first = Math.Min((int)at, track.Count - 1), second = Math.Min(first + 1, track.Count - 1);
            float amount = at - first;
            var a = track[first]; var b = track[second];
            if (System.Numerics.Quaternion.Dot(a.Rotation, b.Rotation) < 0) b = b with { Rotation = -b.Rotation };
            result[frame] = new(System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.Lerp(a.Rotation, b.Rotation, amount)),
                System.Numerics.Vector3.Lerp(a.Translation, b.Translation, amount), a.Scale + (b.Scale - a.Scale) * amount);
        }
        return result;
    }

    private static BoneTransform Inverse(BoneTransform value) =>
        System.Numerics.Matrix4x4.Invert(value.Matrix, out var inverse) ? BoneTransform.FromMatrix(inverse)
            : throw new InvalidDataException("A bind transform is singular.");

    /** The child's transform relative to a parent, given the parent's inverse. */
    private static BoneTransform Compose(BoneTransform inverseParent, BoneTransform child) =>
        BoneTransform.FromMatrix(child.Matrix * inverseParent.Matrix);
    /** The creature's bones, then root children at the root's bind until `count` is reached. */
    private static IReadOnlyList<SkeletonBoneInput> Bones(RiggedEntityInput creature, int count)
    {
        var bones = creature.Bones.Select(bone => new SkeletonBoneInput(AnimationDefinitionWriter.NameHash(bone.Name), bone.Parent, bone.BindObject)).ToList();
        int root = creature.Bones.ToList().FindIndex(bone => bone.Parent < 0);
        for (int index = bones.Count; index < count; index++)
            bones.Add(new(AnimationDefinitionWriter.NameHash($"pad_{index}"), root, creature.Bones[root].BindObject));
        return bones;
    }

    /**
     * What each definition slot plays. A slot takes the behaviour of the highest-priority query that
     * reaches it. A pose-space slot plays a still clip with the donor's space; a layer slot with no
     * mapped behaviour plays the neutral clip; every other slot plays its behaviour's clip, or idle.
     */
    public static CombatantSlotPlan[] SlotPlan(PackageCatalog catalog, CombatantAnimationSet slots,
        IReadOnlyDictionary<CombatantBehaviour, string> clipByBehaviour, string idle, bool neutralLayers, bool stillClips = false, bool rootMotion = false)
    {
        var bySlot = new Dictionary<int, CombatantBehaviour>();
        foreach (var query in slots.Queries)
            foreach (var row in query.Rows)
                if (row.ClipSlot >= 0 && (!bySlot.TryGetValue(row.ClipSlot, out var held) || Array.IndexOf(Priority, query.Behaviour) < Array.IndexOf(Priority, held)))
                    bySlot[row.ClipSlot] = query.Behaviour;
        uint[] stock = DefinitionSlots(catalog.Read(slots.DefinitionTag));
        var reader = new AnimationControlReader(catalog);
        var motionReader = new AuthoredRootMotionReader(catalog);
        // Donor clips with the same root motion share one creature copy.
        var motionByKey = new Dictionary<string, uint>(StringComparer.Ordinal);
        uint Motion(uint donorClip)
        {
            var motion = motionReader.Read(donorClip);
            var key = new System.Text.StringBuilder($"{motion.FrameCount}/{motion.NativeExtractionMode}/{motion.CycleTransform}");
            for (int frame = 0; frame < motion.FrameCount; frame++) key.Append('|').Append(motion.Sample(frame / (float)FramesPerSecond));
            return motionByKey.TryAdd(key.ToString(), donorClip) ? donorClip : motionByKey[key.ToString()];
        }
        var plan = new CombatantSlotPlan[stock.Length];
        for (int slot = 0; slot < stock.Length; slot++)
        {
            var behaviour = bySlot.GetValueOrDefault(slot, CombatantBehaviour.Unknown);
            int mode = 0;
            bool space = false, moves = false;
            if (stock[slot] is not (0 or uint.MaxValue))
            {
                var clip = reader.Read(stock[slot]);
                (mode, space) = (clip.PoseMode, clip.PoseSpace is not null);
                moves = rootMotion && mode == 0 && CombatantAnimationPoseSpace.HasRootMotion(catalog.Read(stock[slot]));
            }
            bool neutral = neutralLayers && mode != 0 && !clipByBehaviour.ContainsKey(behaviour);
            string name = space ? SpaceName(reader.Read(stock[slot])) : stillClips ? StillClip
                : neutral ? NeutralClip : clipByBehaviour.TryGetValue(behaviour, out string? mapped) ? mapped : idle;
            // A moving slot gets a copy of the clip carrying that donor clip's root motion.
            uint motion = moves && !space && name != NeutralClip ? Motion(stock[slot]) : 0;
            plan[slot] = motion != 0
                ? new(slot, stock[slot], behaviour, mode, space, $"{name}{MotionSeparator}{motion:x8}") { RootMotion = motion }
                : new(slot, stock[slot], behaviour, mode, space, name);
        }
        return plan;
    }

    /**
     * A copy of the donor with its own body and clips, whose definition names the donor's row 0 clip in every
     * slot without a pose space. It tests the definition writer and the action system with one clip, apart
     * from any new body.
     * @return The copy's tags, entity first, for WorldSquads.SetEntityCopy.
     */
    public static IReadOnlyList<WorldTag> DonorWithOneClip(PackageCatalog catalog, uint donor)
    {
        var slots = CombatantAnimationSlots.Read(catalog, donor) ?? throw new InvalidDataException("The donor has no sequence selector.");
        var row = slots.OtherRows.Concat(slots.Queries.SelectMany(query => query.Rows)).First(value => value.NameRow == 0);
        byte[] definition = catalog.Read(slots.DefinitionTag);
        uint[] stock = DefinitionSlots(definition);
        var reader = new AnimationControlReader(catalog);
        var clips = stock.Select(tag => tag is 0 or uint.MaxValue || reader.Read(tag).PoseSpace is null ? row.ClipTag : tag).ToArray();
        float[] seconds = clips.Select(tag => reader.Read(tag).DurationSeconds).ToArray();
        return EntityBuilder.Clone(catalog, donor, [slots.DefinitionTag],
            new Dictionary<uint, byte[]> { [slots.DefinitionTag] = Definition(definition, clips, seconds) });
    }

    /** One name per distinct pose space: its pose mode, kind, points and simplices. */
    private static string SpaceName(AnimationControlClip clip)
    {
        var space = clip.PoseSpace!;
        uint hash = AnimationDefinitionWriter.NameHash($"{clip.PoseMode}/{(int)space.Kind}/{clip.FrameCount}");
        foreach (var point in space.Points) hash = unchecked((hash ^ BitConverter.SingleToUInt32Bits(point.X)) * 0x01000193 ^ BitConverter.SingleToUInt32Bits(point.Y));
        foreach (var cell in space.Simplices) foreach (sbyte value in cell) hash = unchecked((hash ^ (byte)value) * 0x01000193);
        return $"{SpacePrefix}{hash:x8}";
    }

    /** The definition with every slot naming a creature clip and every clip row's duration set to its clip's. */
    private static byte[] Definition(byte[] donor, uint[] clips, float[] seconds)
    {
        byte[] output = (byte[])donor.Clone();
        ref readonly var root = ref TagView.Checked<SAnimationDefinition>(output, 0);
        var slotRows = TagView.EditRows(output, in root.Animations, SAnimationClipSlot.Class);
        if (slotRows.Length != clips.Length) throw new InvalidDataException("The slot plan does not cover the definition.");
        for (int slot = 0; slot < clips.Length; slot++) slotRows[slot].Clip = clips[slot];
        var nameRows = TagView.EditRows(output, in root.MotionDefinitions, SAnimationNameRow.Class);
        for (int row = 0; row < nameRows.Length; row++)
            if ((uint)nameRows[row].ClipSlot < (uint)clips.Length) nameRows[row].DurationSeconds = seconds[nameRows[row].ClipSlot];
        return output;
    }

    /** The clip tag of every slot of a definition. */
    public static uint[] DefinitionSlots(byte[] definition)
    {
        ref readonly var root = ref TagView.Checked<SAnimationDefinition>(definition, 0);
        var rows = TagView.Rows(definition, in root.Animations, SAnimationClipSlot.Class);
        var result = new uint[rows.Length];
        for (int i = 0; i < result.Length; i++) result[i] = rows[i].Clip;
        return result;
    }

    /** Every name of the donor's name table on a creature bone: the named points on the bones the options give, the rest on the root. */
    private static Dictionary<uint, int> PointNames(byte[] donorRig, RiggedEntityInput creature, CombatantCreatureOptions options)
    {
        int Bone(string? name)
        {
            if (name is null) return creature.Bones.ToList().FindIndex(bone => bone.Parent < 0);
            int index = creature.Bones.ToList().FindIndex(bone => bone.Name == name);
            return index >= 0 ? index : throw new ArgumentException($"The creature has no bone named {name}.", nameof(options));
        }
        var points = new Dictionary<string, string?>
        {
            ["head"] = options.HeadBone, ["right_hand"] = options.RightHandBone, ["right_grip"] = options.RightHandBone,
            ["left_hand"] = options.LeftHandBone, ["left_grip"] = options.LeftHandBone,
            ["right_foot"] = options.RightFootBone, ["left_foot"] = options.LeftFootBone,
        };
        var byHash = points.ToDictionary(pair => AnimationDefinitionWriter.NameHash(pair.Key), pair => Bone(pair.Value));
        int root = Bone(null);
        var names = CombatantAnimationRig.NameTable(donorRig).Select(row => row.Name).Distinct().ToDictionary(hash => hash, hash => byHash.GetValueOrDefault(hash, root));
        foreach (var (hash, bone) in byHash) names[hash] = bone;
        return names;
    }

    /** The first clip of the prop template's definition and its one control group. */
    private static (uint Clip, uint Group) TemplateClip(PackageCatalog catalog)
    {
        uint definition = EntityBuilder.Components(catalog.Read(RiggedEntityBuilder.Template)).SelectMany(tag => Words(catalog.Read(tag)))
            .Where(word => catalog.TryGetEntry(word, out var entry) && entry.Reference == SAnimationDefinition.Class).Distinct().Single();
        uint clip = DefinitionSlots(catalog.Read(definition)).First(tag => catalog.TryGetEntry(tag, out var entry) && entry.Reference == SAnimationClip.Class);
        var read = new AnimationControlReader(catalog).Read(clip);
        if (read.ControlGroups.Count != 1) throw new InvalidDataException("The template clip does not have one control group.");
        return (clip, read.ControlGroups[0].NameHash);
    }

    private static IEnumerable<uint> Words(byte[] data)
    {
        for (int at = 0; at + sizeof(uint) <= data.Length; at += sizeof(uint))
            yield return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
    }
}
