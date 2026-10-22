using System.Collections.Concurrent;
using SunriseStudio.Animation;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

/**
 * Scene and animation of one assembled guardian, with the head's neutral face as a rest layer.
 * PoseClipTag is the weapon's hold pose, which the view opens in; 0 opens in bind pose.
 * SlotNotes holds the notes about one part label.
 */
public sealed record PreparedGuardian(SceneDocument Scene, AssetAnimationSource? Animation, uint PoseClipTag,
    GuardianParts Parts, IReadOnlyList<string> Diagnostics, IReadOnlySet<uint> SourceTags,
    IReadOnlyDictionary<string, IReadOnlyList<string>> SlotNotes);

/**
 * A guardian that wears or shows an item in its preview; EntityTag is the item's model entity for that class and gender,
 * 0 for an item with no model of its own.
 */
public sealed record ArmorWearer(GuardianClass Class, bool Female, uint EntityTag)
{
    public string Name => $"{Class.Name} {GuardianAppearance.GenderNames[Female ? 1 : 0]}";
}

/** Thread-safe; caches the appearance tables and each body's base animation source for the catalog's life. */
public sealed class GuardianAssembler(PackageCatalog catalog)
{
    /** b_r_grip in the 72-bone body skeleton. */
    public const int GripBone = 24;
    /** Slot note of a part whose cloth is skinned to the body. */
    public const string ClothNote = "no cloth simulation (WIP)";
    private const int BodyBones = 72;
    // A standing clip keeps the pelvis above this share of its bind height; the crouch clips sit near half.
    private const float StandingPelvis = 0.8f;
    private static readonly uint GripNameHash = StringBankWriter.Hash("b_r_grip"), PelvisNameHash = StringBankWriter.Hash("b_pelvis");
    private readonly object appearanceLock = new();
    private readonly ConcurrentDictionary<uint, Lazy<AssetAnimationSource>> bodies = new();
    // Hold pose by body, weapon tag, race and gender; a null choice when the body has no stand selector.
    private readonly ConcurrentDictionary<(uint Body, uint Tag, string Race, string Gender), (AnimationStandChoice? Choice, bool Crouches)> holds = new();
    // Readable clip labels by body, race and gender.
    private readonly ConcurrentDictionary<(uint Body, string Race, string Gender), IReadOnlyDictionary<uint, string>> readable = new();
    private GuardianAppearance? appearance;

    public Func<uint, string?>? BoneNames { get; init; }
    public Func<uint, string?>? ClipNames { get; init; }
    public PackageCatalog Catalog { get; } = catalog;

    /** Read on first use, under a lock. */
    public GuardianAppearance Appearance
    {
        get { lock (appearanceLock) return appearance ??= GuardianAppearance.Read(Catalog); }
    }

    /** The appearance tables if a prepare has read them; never reads. */
    public GuardianAppearance? AppearanceIfRead
    {
        get { lock (appearanceLock) return appearance; }
    }

    /** Diagnostics in order, and the ones about each part label. */
    private sealed class Notes(GuardianParts parts)
    {
        public List<string> Lines { get; } = [.. parts.Diagnostics];
        public Dictionary<string, List<string>> Slots { get; } =
            parts.SlotNotes.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.Ordinal);

        /** Adds "label: text"; a detail goes on its own line, which the page shows only on hover. */
        public void Add(string label, string text, string detail = "")
        {
            string line = detail.Length == 0 ? $"{label}: {text}" : $"{label}: {text}\n{detail}";
            Lines.Add(line);
            if (!Slots.TryGetValue(label, out var lines)) Slots.Add(label, lines = []);
            lines.Add(line);
        }
    }

    /**
     * Runs on a worker. Scene.TagHash = bodyTag; instance names are prefixed with their part label.
     * A part that cannot load or skin is left out or stays at bind, with a diagnostic.
     * @param shown The parts the scene draws; null draws all. Every part still joins the rig, so shown parts pose as worn.
     * @param armorDyes Dyes every armor piece takes in place of its own, such as a shader's; null keeps each piece's.
     */
    public PreparedGuardian Prepare(uint bodyTag, GuardianLoadout loadout, CancellationToken token = default,
        Func<GuardianPart, bool>? shown = null, NativeGearDyes? armorDyes = null)
    {
        var parts = Appearance.Resolve(Catalog, bodyTag, loadout, token);
        var notes = new Notes(parts);
        var sources = new HashSet<uint>(parts.SourceTags);
        var scene = new SceneDocument { TagHash = bodyTag, Name = GuardianClasses.ForBody(bodyTag)!.DisplayName };
        var loaded = new List<(GuardianPart Part, MeshInstance[] Instances)>();
        var row = Appearance.Rows[Appearance.Clamp(loadout).RaceGender];
        bool female = row.Female;
        // The game passes the same pairs to every gear slot; the hands keep their own gender filter.
        var gear = new SceneLoadOptions { SpawnPairs = parts.SpawnPairs };
        int undrawn = 0;
        foreach (var part in parts.Parts)
        {
            token.ThrowIfCancellationRequested();
            SceneDocument partScene;
            var options = part.Kind is GuardianPartKind.Armor or GuardianPartKind.Customisation ? gear : new SceneLoadOptions();
            try { partScene = new AssetSceneLoader(Catalog).Load(part.EntityTag, options, token); }
            catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
            { notes.Add(part.Label, "does not load", $"Entity 0x{part.EntityTag:X8}: {error.Message}"); continue; }
            sources.UnionWith(partScene.ReadTags);
            scene.Diagnostics.AddRange(partScene.Diagnostics.Where(line => !scene.Diagnostics.Contains(line)));
            // The cloth draw skips the cloth parts its draw set does not list, whatever the part state.
            var lod0 = partScene.Instances.Where(instance => !instance.Mesh.NeverDrawn && instance.Mesh.LodLevel == 0
                && instance.InitialVisibilityReason != MeshData.ClothSetPartReason).ToArray();
            // Weapon parts off at spawn are perk alternates, which the weapon view also hides.
            var visible = part.Kind == GuardianPartKind.Body ? Hands(part, lod0, female, notes)
                : lod0.Where(instance => instance.InitiallyVisible != false).ToArray();
            var drawn = visible.Where(instance => instance.Mesh.Native is not { } native
                || NativeScenePlan.DrawnStages.Contains(native.RenderStage)).ToArray();
            undrawn += visible.Length - drawn.Length;
            if (drawn.Length == 0) { notes.Add(part.Label, "nothing to draw", $"Entity 0x{part.EntityTag:X8} has no drawn geometry."); continue; }
            foreach (var instance in drawn) instance.Name = $"{part.Label}: {instance.Name}";
            if (shown is null || shown(part)) scene.Instances.AddRange(drawn);
            loaded.Add((part, drawn));
        }
        if (undrawn != 0)
            notes.Lines.Add($"Viewer: {undrawn} {(undrawn == 1 ? "part" : "parts")} not drawn\nThe viewer does not draw their render stages.");
        SetDyes(loaded, notes, armorDyes);
        var (animation, pose) = Animate(bodyTag, row, parts, loaded, notes, sources, token);
        return new(scene, animation, pose, parts, notes.Lines.AsReadOnly(), sources,
            notes.Slots.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.AsReadOnly(), StringComparer.Ordinal));
    }

    /** The guardians an armor item has art for, by class and then gender; art rows name a class or any class. */
    public IReadOnlyList<ArmorWearer> Wearers(uint itemTag)
    {
        byte[] item = Catalog.Read(itemTag);
        var wearers = new List<ArmorWearer>();
        foreach (var guardianClass in GuardianClasses.All)
            foreach (bool female in (bool[])[false, true])
                try
                {
                    if (GuardianArt.ItemEntities(Catalog, item, guardianClass.ClassByte, female) is [var entity, ..])
                        wearers.Add(new(guardianClass, female, entity));
                }
                catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or ArgumentException) { }
        return wearers;
    }

    /**
     * Runs on a worker. An armor item as an item preview shows it: worn and posed by the wearer, or the first guardian
     * it has art for, with only the item's own parts drawn.
     * @param wholeGuardian Draws the wearer too, in its default armor with this item in its slot.
     */
    public PreparedGuardian PrepareItem(uint itemTag, ArmorWearer? wearer = null, CancellationToken token = default,
        bool wholeGuardian = false)
    {
        int slot = GuardianAppearance.ArmorSlot(Catalog.Read(itemTag));
        if (slot < 0) throw new InvalidDataException("The item is not in an armor slot.");
        wearer ??= Wearers(itemTag).FirstOrDefault() ?? throw new InvalidDataException("The item has no art for any class.");
        var loadout = DefaultLoadout(wearer.Female).WithArmor(slot, itemTag);
        var prepared = Prepare(wearer.Class.BodyTag, loadout, token, wholeGuardian ? null : part => part.ItemTag == itemTag);
        if (prepared.Scene.Instances.Count == 0) throw new InvalidDataException("The item's art has no drawn geometry.");
        return prepared;
    }

    /**
     * Runs on a worker. A guardian of this class and gender, for an item shown on it rather than worn: a weapon in its
     * hands, in the default loadout. A shader's dyes go on showcase armor, since the default slots hold the undyed
     * gear-preview placeholders.
     * @param weapon The weapon item it holds in its hold pose; 0 holds none.
     */
    public PreparedGuardian PrepareWearer(ArmorWearer wearer, NativeGearDyes? armorDyes = null, CancellationToken token = default,
        uint weapon = 0)
    {
        var loadout = DefaultLoadout(wearer.Female) with { Weapon = weapon };
        if (armorDyes is not null)
        {
            byte classByte = wearer.Class.ClassByte;
            uint[] armor = GuardianArt.ShowcaseArmor(Catalog, classByte, wearer.Female,
                Appearance.PreviewDefault(classByte, GuardianAppearance.ArmorSlots - 1));
            for (int slot = 0; slot < armor.Length; slot++)
                if (armor[slot] != 0) loadout = loadout.WithArmor(slot, armor[slot]);
        }
        return Prepare(wearer.Class.BodyTag, loadout, token, armorDyes: armorDyes);
    }

    /** Every class and gender, with no item model, for an item shown on a guardian. */
    public static IReadOnlyList<ArmorWearer> AllWearers { get; } =
        [.. GuardianClasses.All.SelectMany(guardianClass => new[] { false, true }.Select(female => new ArmorWearer(guardianClass, female, 0)))];

    /** The default loadout on the first race and gender row of this gender. */
    private GuardianLoadout DefaultLoadout(bool female) => GuardianLoadout.Default with
    {
        RaceGender = Enumerable.Range(0, Appearance.Rows.Count).FirstOrDefault(index => Appearance.Rows[index].Female == female)
    };

    /**
     * The body's hand parts of one gender. The gear component shows parts labelled gear-applied true
     * once real gear is on, and the variant keeps the guardian's gender; parts keyed by player state stay out.
     */
    private MeshInstance[] Hands(GuardianPart part, MeshInstance[] lod0, bool female, Notes notes)
    {
        var labels = new Dictionary<uint, IReadOnlyList<IReadOnlySet<(uint Key, uint Value)>>?>();
        var hands = new List<MeshInstance>();
        foreach (var instance in lod0)
        {
            uint config = instance.ModelSourceConfigTag;
            if (!labels.TryGetValue(config, out var parts))
            {
                try { parts = EntityPartLabels.Read(Catalog, config); }
                catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException
                    or ArgumentException or IndexOutOfRangeException)
                { notes.Add(part.Label, "part labels unreadable", $"Config 0x{config:X8}: {error.Message}"); }
                labels.Add(config, parts);
            }
            if (parts is null || (uint)instance.Mesh.LogicalPartIndex >= parts.Count) continue;
            var set = parts[instance.Mesh.LogicalPartIndex];
            if (set.Contains((EntityPartLabels.GearAppliedKey, EntityPartLabels.True))
                && set.Contains((EntityPartLabels.GenderKey, female ? EntityPartLabels.Female : EntityPartLabels.Male))
                && !set.Any(label => label.Key == EntityPartLabels.RuntimeKey))
                hands.Add(instance.WithVisibility(true, "Hands of this gender, as the game shows them with gear on"));
        }
        if (hands.Count != 0) notes.Add(part.Label, "no skin colour (WIP)", "The hand skin colour and dyes are not applied.");
        return [.. hands];
    }

    /**
     * Armor and weapon parts use the item's dyes; customisation parts their dye indices.
     * Each item, and each other entity, is its own gear object with its own plate atlases.
     * An item's bucket gives its gear kind, which sizes the atlases; a part with no item is kind 0.
     * @param armorDyes Dyes every armor piece takes in place of its own; null keeps each piece's.
     */
    private void SetDyes(List<(GuardianPart Part, MeshInstance[] Instances)> loaded, Notes notes, NativeGearDyes? armorDyes)
    {
        var items = new Dictionary<uint, NativeGearDyes?>();
        foreach (var (part, instances) in loaded)
        {
            object gearObject = part.ItemTag != 0 ? part.ItemTag : part.EntityTag;
            byte[]? item = part.ItemTag != 0 ? Catalog.Read(part.ItemTag) : null;
            // Customisation markings use plate set 3, which only kind 0 has; the body has no plates.
            int? kind = item is null ? NativePlateAtlases.CharacterKind : NativeGearPlating.ItemKind(item);
            foreach (var instance in instances) NativeGearPlating.SetGearObject(instance, gearObject, kind);
            NativeGearDyes? dyes = null;
            string? error = null;
            if (armorDyes is not null && part.Kind == GuardianPartKind.Armor) dyes = armorDyes;
            else if (item is not null && !items.TryGetValue(part.ItemTag, out dyes))
            {
                items.Add(part.ItemTag, dyes = NativeGearPlating.ReadItemDyes(Catalog, item, out error));
                if (dyes is null) notes.Add(part.Label, "dyes unreadable", $"Uses the default dyes. {error}");
            }
            else if (part.Kind == GuardianPartKind.Customisation && part.DyeIndices.Count != 0)
            {
                dyes = NativeGearPlating.ReadDyeIndices(Catalog, part.DyeIndices, $"the colours of {part.Label}", out error);
                if (dyes is null) notes.Add(part.Label, "colours unreadable", $"Uses the default colours. {error}");
            }
            if (dyes is not null) foreach (var instance in instances) NativeGearPlating.SetDyes(instance, dyes);
        }
    }

    /** The body rig with every part skinned to it, the weapon on the grip bone, and the weapon's hold pose. */
    private (AssetAnimationSource? Animation, uint Pose) Animate(uint bodyTag, GuardianRaceGender row, GuardianParts parts,
        List<(GuardianPart Part, MeshInstance[] Instances)> loaded, Notes notes, HashSet<uint> sources,
        CancellationToken token)
    {
        AssetAnimationSource body;
        try { body = BodySource(bodyTag); }
        catch (Exception error) when (error is NotSupportedException or InvalidDataException or KeyNotFoundException)
        {
            notes.Lines.Add($"Body: rig unreadable\nEvery part stays at bind. {error.Message}");
            return (null, 0);
        }
        token.ThrowIfCancellationRequested();
        sources.UnionWith(body.Clips.Select(clip => clip.DefinitionTag).Append(body.Rig.ResourceTag));
        if (body.Binding is { } binding) sources.Add(binding.ResourceTag);
        notes.Lines.AddRange(body.Diagnostics);
        var rig = body.Rig;
        bool gripFound = rig.Bones.Count == BodyBones && rig.Bones[GripBone].NameHash == GripNameHash;
        var reader = new ModelSkinReader(Catalog);
        var extra = new List<ModelSkinBinding>();
        var models = rig.ModelTags.ToHashSet();
        foreach (var (part, instances) in loaded.Where(item => item.Part.Kind != GuardianPartKind.Weapon))
        {
            Cloth(part, instances, rig, reader, models, extra, notes);
            foreach (uint model in instances.Select(instance => instance.Mesh.TagHash).Distinct())
            {
                token.ThrowIfCancellationRequested();
                if (!models.Add(model)) continue;
                IReadOnlyList<ModelSkinBinding> skins;
                try { skins = reader.ReadExternal(model, rig); }
                catch (Exception error) when (error is NotSupportedException or InvalidDataException or KeyNotFoundException)
                { notes.Add(part.Label, "stays at bind", $"Model 0x{model:X8}: {error.Message}"); continue; }
                // Bone order alone does not place a model that weights everything to bone 0.
                if (skins.Any(skin => skin.Vertices.Count != 0)
                    && skins.All(skin => skin.Vertices.All(vertex => vertex.Bone0 == 0 && vertex.Weights.X == 1)))
                {
                    notes.Add(part.Label, "stays at the origin", $"Model 0x{model:X8} weights every vertex to bone 0.");
                    continue;
                }
                extra.AddRange(skins);
            }
        }
        var attached = new Dictionary<ModelSkinBinding, BoneAttachment>(ReferenceEqualityComparer.Instance);
        var weapon = loaded.Where(item => item.Part.Kind == GuardianPartKind.Weapon).SelectMany(item => item.Instances).ToArray();
        if (weapon.Length != 0 && !gripFound)
            notes.Add("Weapon", "stays at the origin", $"Body bone {GripBone} is not b_r_grip.");
        else if (weapon.Length != 0)
            AttachWeapon(parts.WeaponEntity, weapon, attached, notes, sources);
        var names = ReadableNames(bodyTag, row, body, notes);
        var pose = weapon.Length != 0 && gripFound ? Hold(bodyTag, row, body, parts, notes, sources) : null;
        if (pose is not null)
            names = new Dictionary<uint, string>(names) { [pose.ClipTag] = "Weapon hold pose" };
        var face = parts.Parts.FirstOrDefault(part => part.Selector == GuardianAppearance.HeadSelector
            && loaded.Any(item => ReferenceEquals(item.Part, part)));
        var faced = face is null || body.Binding is null ? null : FaceRest(body.Binding, face, notes, sources);
        return (AssetAnimationSource.WithSkins(body, extra, attached, names, faced), pose?.ClipTag ?? 0);
    }

    /**
     * The stand clip the body's selector picks for the weapon's third-person tag, class, gender and race, in the
     * combat stance on the ground. Null with a note when there is none; a pick that crouches the body gets a WIP note.
     */
    private AnimationStandChoice? Hold(uint bodyTag, GuardianRaceGender row, AssetAnimationSource body, GuardianParts parts,
        Notes notes, HashSet<uint> sources)
    {
        uint item = parts.Parts.First(part => part.Kind == GuardianPartKind.Weapon).ItemTag;
        string Name(IReadOnlyList<string> names, int index) => index >= 0 ? names[index].ToLowerInvariant() : "";
        string race = Name(GuardianAppearance.RaceNames, row.Race), gender = Name(GuardianAppearance.GenderNames, row.Gender);
        (AnimationStandChoice? Choice, bool Crouches) hold;
        try
        {
            uint tag = GuardianArt.ThirdPersonTag(Catalog, Catalog.Read(item));
            hold = holds.GetOrAdd((bodyTag, tag, race, gender), key =>
            {
                var choice = AnimationStandSelector.Select(Catalog, key.Body, key.Tag,
                    GuardianClasses.ForBody(key.Body)!.Name.ToLowerInvariant(), key.Gender, key.Race);
                return (choice, choice is not null && body.Clips.Any(clip => clip.ClipTag == choice.ClipTag)
                    && Crouches(body, choice.ClipTag));
            });
        }
        catch (Exception error) when (error is NotSupportedException or InvalidDataException or KeyNotFoundException
            or OverflowException or ArgumentException)
        {
            notes.Add("Weapon", "no hold pose", $"Shows the bind pose. {error.Message}");
            return null;
        }
        if (hold.Choice is not { } chosen || !body.Clips.Any(clip => clip.ClipTag == chosen.ClipTag))
        {
            notes.Add("Weapon", "no hold pose", "The body has no stand pose for this weapon. Shows the bind pose.");
            return null;
        }
        sources.Add(chosen.ClipTag);
        if (hold.Crouches)
            notes.Add("Weapon", "crouch pose (WIP)", $"Stand set {chosen.Set} crouches the body; the game asks for standing.");
        return chosen;
    }

    /**
     * Body clip labels led by the stand weapons and action queries that play the clip, as in "ready - Clip 27".
     * Clips no table names keep their label. A table that does not read keeps every label, with a note.
     */
    private IReadOnlyDictionary<uint, string> ReadableNames(uint bodyTag, GuardianRaceGender row, AssetAnimationSource body,
        Notes notes)
    {
        string race = row.Race >= 0 ? GuardianAppearance.RaceNames[row.Race].ToLowerInvariant() : "";
        string gender = row.Gender >= 0 ? GuardianAppearance.GenderNames[row.Gender].ToLowerInvariant() : "";
        string guardianClass = GuardianClasses.ForBody(bodyTag)!.Name.ToLowerInvariant();
        try
        {
            return readable.GetOrAdd((bodyTag, race, gender), _ =>
            {
                var stands = new Dictionary<uint, List<string>>();
                foreach (string weapon in AnimationStandSelector.WeaponTags)
                    if (AnimationStandSelector.Select(Catalog, bodyTag, AnimationStandSelector.TagHash(weapon), guardianClass, gender, race) is { } stand)
                    {
                        if (!stands.TryGetValue(stand.ClipTag, out var list)) stands.Add(stand.ClipTag, list = []);
                        list.Add(weapon);
                    }
                var queries = AnimationStandSelector.QueryNames(Catalog, bodyTag, AnimationStandSelector.KnownQueries);
                var labels = new Dictionary<uint, string>(body.ClipNames);
                foreach (var (clip, label) in body.ClipNames)
                {
                    var parts = new List<string>();
                    if (stands.TryGetValue(clip, out var weapons)) parts.Add("stand " + string.Join(", ", weapons));
                    if (queries.TryGetValue(clip, out var actions)) parts.AddRange(actions);
                    if (parts.Count != 0) labels[clip] = $"{string.Join(" / ", parts)} - {label}";
                }
                return labels;
            });
        }
        catch (Exception error) when (error is NotSupportedException or InvalidDataException or KeyNotFoundException
            or OverflowException or ArgumentException)
        {
            notes.Lines.Add($"Body: clip names unreadable\nClips keep their slot numbers. {error.Message}");
            return body.ClipNames;
        }
    }

    /** True when frame 0 of the clip puts the pelvis below StandingPelvis of its bind height. */
    private bool Crouches(AssetAnimationSource body, uint clipTag)
    {
        int pelvis = body.Rig.Bones.FirstOrDefault(bone => bone.NameHash == PelvisNameHash)?.Index ?? -1;
        if (pelvis < 0 || body.Binding is null) return false;
        var pose = body.Rig.CreateBindPose();
        body.Binding.Apply(new AnimationControlReader(Catalog).Read(clipTag), 0, pose);
        return pose.ObjectTransforms[pelvis].Translation.Z < StandingPelvis * body.Rig.Bones[pelvis].BindObject.Translation.Z;
    }

    /**
     * Skins a part's cloth models to the body rig by their skeletal input, the rest shape the simulation starts from.
     * The model tags are added to models, so the rigid skin pass skips them.
     */
    private void Cloth(GuardianPart part, MeshInstance[] instances, SkeletonRig rig, ModelSkinReader reader,
        HashSet<uint> models, List<ModelSkinBinding> extra, Notes notes)
    {
        var skeletons = new SkeletonReader(Catalog);
        bool skinned = false;
        foreach (uint config in instances.Where(instance => instance.RequiresClothSimulation)
            .Select(instance => instance.ModelSourceConfigTag).Distinct())
        {
            ClothModelReference? cloth;
            try { cloth = skeletons.ClothReference(config); }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException)
            { notes.Add(part.Label, "cloth stays at bind", $"Cloth config 0x{config:X8}: {error.Message}"); continue; }
            if (cloth is null)
            { notes.Add(part.Label, "cloth stays at bind", $"Cloth config 0x{config:X8} names no cloth model."); continue; }
            if (!models.Add(cloth.ModelTag)) continue;
            try
            {
                extra.AddRange(reader.ReadExternalClothInput(cloth, rig));
                skinned = true;
            }
            catch (Exception error) when (error is NotSupportedException or InvalidDataException or KeyNotFoundException)
            { notes.Add(part.Label, "cloth stays at bind", $"Cloth model 0x{cloth.ModelTag:X8}: {error.Message}"); }
        }
        if (skinned) notes.Add(part.Label, ClothNote, "The cloth shows its rest shape on the body.");
    }

    /** The body binding with the head's neutral face as its rest layer; the head's bind mesh is not neutral. */
    private AnimationRigBinding? FaceRest(AnimationRigBinding body, GuardianPart head, Notes notes, HashSet<uint> sources)
    {
        try
        {
            uint tag = FaceRestReader.Read(Catalog, head.EntityTag);
            if (tag == 0)
            {
                notes.Add(head.Label, "face not posed", $"Entity 0x{head.EntityTag:X8} has no face component.");
                return null;
            }
            var binding = body.WithRestLayer(new AnimationControlReader(Catalog).Read(tag));
            sources.Add(tag);
            return binding;
        }
        catch (Exception error) when (error is NotSupportedException or InvalidDataException or KeyNotFoundException)
        {
            notes.Add(head.Label, "face not posed", $"Entity 0x{head.EntityTag:X8}: {error.Message}");
            return null;
        }
    }

    /** Weapon parts are bound like the weapon view, then every palette entry follows the grip bone. */
    private void AttachWeapon(uint weaponEntity, MeshInstance[] weapon, Dictionary<ModelSkinBinding, BoneAttachment> attached,
        Notes notes, HashSet<uint> sources)
    {
        int boneCount = 1;
        try
        {
            if (weaponEntity == 0) throw new InvalidDataException("the weapon has no gameplay entity");
            var skeleton = new SkeletonReader(Catalog).Read(weaponEntity);
            boneCount = skeleton.Bones.Count;
            sources.Add(skeleton.ResourceTag);
        }
        catch (Exception error) when (error is NotSupportedException or InvalidDataException or KeyNotFoundException)
        { notes.Add("Weapon", "skeleton unreadable", $"Palette checks assume one bone. {error.Message}"); }
        var weaponScene = new SceneDocument();
        weaponScene.Instances.AddRange(weapon);
        try
        {
            var (skins, rigid, rigidNames) = WeaponAnimationPreviewSource.BindSkins(Catalog, weaponScene, boneCount);
            if (rigidNames.Count != 0)
                notes.Add("Weapon", "skinning unverified", "Parts: " + string.Join(", ", rigidNames.Order()) + ".");
            foreach (var skin in skins) attached[skin] = new(GripBone, rigid.TryGetValue(skin, out int entries) ? entries : boneCount);
        }
        catch (Exception error) when (error is NotSupportedException or InvalidDataException or KeyNotFoundException)
        {
            attached.Clear();
            notes.Add("Weapon", "stays at the origin", error.Message);
        }
        notes.Lines.AddRange(weaponScene.Diagnostics);
    }

    /** One build per body; it takes no caller token, so a cancelled prepare never caches a failure. */
    private AssetAnimationSource BodySource(uint bodyTag)
    {
        var lazy = bodies.GetOrAdd(bodyTag, tag => new(() => AssetAnimationSource.Prepare(Catalog, tag, CancellationToken.None, BoneNames, ClipNames)));
        try { return lazy.Value; }
        catch
        {
            bodies.TryRemove(new(bodyTag, lazy));
            throw;
        }
    }
}
