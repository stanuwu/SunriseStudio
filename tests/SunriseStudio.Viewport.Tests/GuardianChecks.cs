using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SunriseStudio.Animation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using SunriseStudio.Viewport;

/** Guardian tables, armor art and one assembled loadout against the installed packages. */
internal static class GuardianChecks
{
    private const uint HelmetItem = 0x8131934E, WeaponItem = 0x8132017D;
    // Investment item table: rows at +8, 24 bytes, definition tag at +16.
    private const uint ItemTable = 0x81327CCB;
    private static readonly int BucketByte = TagView.FieldOffset<ItemDefinition>("InventoryItemBlock.InventoryBucket");
    // Bucket 7 is the class item; the assembler labels its parts "Class item".
    private const int ClassItemBucket = 7;
    private const string ClassItemLabel = "Class item";
    // A bond wraps the wrist, so its centre sits this close to a forearm or hand bone, in metres.
    private const float WornArmReach = 0.25f;
    // A cloak or hood reaches at least this far behind the spine, in metres.
    private const float WornBackDepth = 0.05f;
    // A hood wraps the head, so the head bone sits this close to the hood's bind bounds, in metres.
    private const float HoodHeadReach = 0.15f;
    // Hair with a stage-2 hairline decal; the decal dye forces blend byte 0x97, which is blend 23.
    private const uint HairlineHair = 0x80BB1080;
    private const int DecalBlend = 23;
    // Cloth asset draw set 0 holds one part index array per stage.
    private const string ClothSetZeroLists = "HC250af49.ClothDrawSets[0].StagePartLists";
    private const int ClothStages = 23;
    private static readonly int BindsPlatesByte = TagView.FieldOffset<SPlatedTextureSet>(nameof(SPlatedTextureSet.BindsPlates));
    // A part posed by the stance moves at least this far from bind, in metres.
    private const float StanceMove = 0.001f;
    // The rifle-family hold pose. It moves every body part, so the checks that need a pose use it.
    private const uint HoldClip = 0x80BC9FDB, PistolHoldClip = 0x80B9EB0E;
    // A standing pose keeps the pelvis above this height and a foot below the other, in metres.
    private const float StandingPelvis = 0.8f, GroundedFoot = 0.25f;
    // A gripping finger turns at least this far from bind, in degrees.
    private const float GripTurn = 10;
    // A grip bone on the weapon sits this close to its surface, in metres.
    private const float OnWeapon = 0.075f;
    // A left grip this far forward of the right grip, in the right grip's frame, holds the weapon's front.
    private const float TwoHanded = 0.2f;
    // The first armor item with art of each slot, for the full-armor render.
    private static readonly uint[] FirstArmor = new uint[5];

    public static void Run(string install, string output)
    {
        Directory.CreateDirectory(output);
        using var catalog = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
        var clock = Stopwatch.StartNew();
        var appearance = GuardianAppearance.Read(catalog);
        Console.WriteLine($"guardian_tables rows={appearance.Rows.Count} read_ms={clock.ElapsedMilliseconds}");
        Check(appearance.Rows.Count == 6, "The race/gender table does not have 6 rows.");
        foreach (var row in appearance.Rows)
            Console.WriteLine($"  row {row.Row} name=0x{row.NameHash:X8} presets={string.Join(",", row.Presets.Select(p => p.Arrangement))} "
                + $"groups={string.Join(" ", row.Presets[0].VariantCounts.OrderBy(p => p.Key).Select(p => $"{p.Key}x{p.Value}"))} "
                + $"dyes={string.Join("/", row.Dyes.Select(d => d.Choices.Count))}");
        Check(appearance.Rows[0].Presets.Select(p => p.Arrangement).SequenceEqual(Enumerable.Range(35, 7)),
            "Row 0 face presets are not arrangements 35 to 41.");
        Check(appearance.Rows[0].NameHash == Fnv("human_male", false), "Row 0 NameHash is not FNV-1 of human_male.");
        Check(appearance.Rows.Select(row => row.Female).SequenceEqual([false, true, false, true, false, true]),
            "The female rows are not 1, 3 and 5.");
        Check(appearance.Rows.Select(row => row.Race).SequenceEqual([0, 0, 1, 1, 2, 2]), "The race of each row is not Human, Awoken, Exo.");
        DefaultHeader(appearance);
        ItemArt(catalog);
        FemaleArt(catalog);
        Bodies(catalog);
        ArmorScan(catalog);
        Assembly(catalog, output);
        Console.WriteLine("guardian_checks result=pass");
    }

    /** Only the class item sweep, for a quick run after a cloth or class item change. */
    public static void RunClassItems(string install, string output)
    {
        Directory.CreateDirectory(output);
        using var catalog = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
        var assembler = new GuardianAssembler(catalog);
        Hoods(assembler, output);
        CloakCopies(assembler);
        ClassItemSweep(assembler, output);
        Console.WriteLine("guardian_class_items result=pass");
    }

    /** The default loadout maps to the default 36-byte header values. */
    private static void DefaultHeader(GuardianAppearance appearance)
    {
        var loadout = appearance.Clamp(GuardianLoadout.Default);
        Check(loadout == GuardianLoadout.Default, "Clamp changed the default loadout.");
        var row = appearance.Rows[0];
        int[] positions = [loadout.Dye06, loadout.Dye08, loadout.Dye10, loadout.Dye12, loadout.Dye18, loadout.Dye24];
        var values = new List<int> { row.Presets[loadout.Preset].Arrangement, loadout.Variant2, loadout.Variant4 };
        for (int list = 0; list < 6; list++) values.AddRange(row.Dyes[list].Choices[positions[list]]);
        Console.WriteLine($"guardian_default header={string.Join("/", values)}");
        Check(values.SequenceEqual([37, 5, 1, 746, 769, 582, 840, 841, 842, 787, 711]), "The default loadout does not give the default header.");
    }

    /** Item 3 picks its male or female model through arrangement +8 or +12. */
    private static void ItemArt(PackageCatalog catalog)
    {
        byte[] helmet = catalog.Read(HelmetItem);
        var male = GuardianArt.ItemEntities(catalog, helmet, 0, false);
        var female = GuardianArt.ItemEntities(catalog, helmet, 0, true);
        Console.WriteLine($"guardian_item_art item=0x{HelmetItem:X8} male={Tags(male)} female={Tags(female)}");
        Check(male.SequenceEqual([0x80EF51BFu]) && female.SequenceEqual([0x80EF51A9u]), "Item 3 does not give its male and female entities.");
        byte[] weapon = catalog.Read(WeaponItem);
        uint gameplay = GuardianArt.GameplayEntity(catalog, weapon);
        var art = GuardianArt.ItemEntities(catalog, weapon, 0, false);
        Console.WriteLine($"guardian_weapon_art item=0x{WeaponItem:X8} gameplay=0x{gameplay:X8} art={Tags(art)}");
        Check(gameplay == 0x81A6B325 && art.Count > 0, "The weapon does not give its gameplay entity and art.");
        Check(GuardianArt.GameplayEntity(catalog, helmet) == 0, "An armor item reported a gameplay entity.");
        var dyes = NativeGearPlating.ReadItemDyes(catalog, helmet, out string? dyeError);
        Console.WriteLine($"guardian_item_dyes item=0x{HelmetItem:X8} records={(dyes is null ? dyeError : Tags(dyes.Records))}");
        Check(dyes is not null, "Item 3 dyes are unreadable.");
    }

    /** Item 3's male and female art entities: instances, part indices, initial visibility and bind width. Prints only. */
    private static void FemaleArt(PackageCatalog catalog)
    {
        foreach (var (name, entity) in new[] { ("male", 0x80EF51BFu), ("female", 0x80EF51A9u) })
        {
            var scene = new AssetSceneLoader(catalog).Load(entity);
            var lod0 = scene.Instances.Where(i => !i.Mesh.NeverDrawn && i.Mesh.LodLevel == 0).ToArray();
            var bounds = Bounds(lod0);
            Console.WriteLine($"guardian_female_art {name} entity=0x{entity:X8} instances={scene.Instances.Count} lod0={lod0.Length} "
                + $"parts={string.Join(",", lod0.Select(i => $"{i.Mesh.TagHash:X8}:{i.Mesh.PartIndex:X}/{i.Mesh.LogicalPartIndex}"))} "
                + $"visible={string.Join(",", lod0.Select(i => i.InitiallyVisible switch { true => "on", false => "off", null => "?" }))} "
                + $"reasons={string.Join("|", lod0.Select(i => i.InitialVisibilityReason ?? "-").Distinct())} "
                + $"width_x={bounds.Max.X - bounds.Min.X:F3} width_y={bounds.Max.Y - bounds.Min.Y:F3} height={bounds.Max.Z - bounds.Min.Z:F3}");
        }
    }

    /** Every class body has the same 72-bone skeleton config. */
    private static void Bodies(PackageCatalog catalog)
    {
        foreach (var body in GuardianClasses.All)
        {
            var rig = new SkeletonReader(catalog).Read(body.BodyTag);
            Console.WriteLine($"guardian_body class={body.Name} entity=0x{body.BodyTag:X8} bones={rig.Bones.Count} "
                + $"skeleton=0x{rig.ResourceTag:X8} models={Tags(rig.ModelTags)} bone24=0x{rig.Bones[24].NameHash:X8}");
            Check(rig.Bones.Count == 72 && rig.ResourceTag == 0x80C22410, $"{body.Name} does not use the 72-bone config 0x80C22410.");
        }
    }

    /** Every armor item with art resolves to one art entity for class 0, male. */
    private static void ArmorScan(PackageCatalog catalog)
    {
        var clock = Stopwatch.StartNew();
        byte[] items = catalog.Read(ItemTable);
        long count = BinaryPrimitives.ReadInt64LittleEndian(items.AsSpan(8));
        int start = checked((int)(16 + BinaryPrimitives.ReadInt64LittleEndian(items.AsSpan(16)) + 16));
        string[] slots = ["Helmet", "Gauntlets", "Chest", "Legs", "Class"];
        var resolved = new int[5]; var failed = new int[5]; var noArt = new int[5];
        var errors = new List<string>();
        for (int index = 0; index < count; index++)
        {
            uint tag = BinaryPrimitives.ReadUInt32LittleEndian(items.AsSpan(start + index * 24 + 16));
            if (!catalog.TryGetEntry(tag, out _)) continue;
            byte[] definition = catalog.Read(tag);
            if (definition.Length <= BucketByte || definition[BucketByte] is < 3 or > 7) continue;
            int slot = definition[BucketByte] - 3;
            try
            {
                int entities = GuardianArt.ItemEntities(catalog, definition, 0, false).Count;
                if (entities == 1) { if (resolved[slot]++ == 0) FirstArmor[slot] = tag; }
                else if (entities == 0) noArt[slot]++;
                else { failed[slot]++; if (errors.Count < 8) errors.Add($"0x{tag:X8}: {entities} entities"); }
            }
            catch (InvalidDataException error) { failed[slot]++; if (errors.Count < 5) errors.Add($"0x{tag:X8}: {error.Message}"); }
        }
        Console.WriteLine($"guardian_armor_scan {string.Join(" ", slots.Select((s, i) => $"{s}={resolved[i]}/{resolved[i] + failed[i]}+{noArt[i]}_no_art"))} "
            + $"ms={clock.ElapsedMilliseconds}{(errors.Count == 0 ? "" : " first_failures=" + string.Join("; ", errors))}");
        Check(failed.Sum() == 0 && resolved.Sum() > 3000, "An armor item does not resolve to one art entity.");
    }

    /** Titan default with a helmet and a weapon: the hold pose, helmet skin and height, weapon on bone 24. */
    private static void Assembly(PackageCatalog catalog, string output)
    {
        var assembler = new GuardianAssembler(catalog);
        var loadout = GuardianLoadout.Default with { Helmet = HelmetItem, Weapon = WeaponItem };
        var clock = Stopwatch.StartNew();
        var prepared = assembler.Prepare(GuardianClasses.Titan, loadout);
        long cold = clock.ElapsedMilliseconds;
        clock.Restart();
        var warm = assembler.Prepare(GuardianClasses.Titan, loadout);
        long warmMs = clock.ElapsedMilliseconds;
        Console.WriteLine($"guardian_prepare cold_ms={cold} warm_ms={warmMs} instances={prepared.Scene.Instances.Count} "
            + $"pose=0x{prepared.PoseClipTag:X8} sources={prepared.SourceTags.Count}");
        foreach (var group in prepared.Parts.Parts.GroupBy(part => part.Label))
            Console.WriteLine($"  part {group.Key}: entities={Tags(group.Select(part => part.EntityTag))} "
                + $"instances={prepared.Scene.Instances.Count(i => i.Name.StartsWith(group.Key + ":", StringComparison.Ordinal))}");
        foreach (string line in prepared.Diagnostics) Console.WriteLine($"  note: {line}");
        Check(warm.Scene.Instances.Count == prepared.Scene.Instances.Count, "A warm prepare differs from the cold one.");
        Check(prepared.PoseClipTag == HoldClip, $"The hold pose is 0x{prepared.PoseClipTag:X8}, not 0x{HoldClip:X8}.");
        var animation = prepared.Animation ?? throw new InvalidDataException("The guardian has no animation.");
        Check(animation.Rig.Bones.Count == 72, "The guardian rig does not have 72 bones.");
        var helmet = prepared.Scene.Instances.Where(i => i.Name.StartsWith("Helmet:", StringComparison.Ordinal)).ToArray();
        Check(helmet.Length > 0, "The helmet has no instances.");
        var helmetSkins = animation.SkinBindings.Where(s => helmet.Any(i => i.Mesh.TagHash == s.ModelTag)).ToArray();
        int maximumBone = helmetSkins.SelectMany(s => s.Vertices).SelectMany(Bones).DefaultIfEmpty(-1).Max();
        var bounds = Bounds(helmet);
        float centre = (bounds.Min.Z + bounds.Max.Z) / 2;
        Console.WriteLine($"guardian_helmet skins={helmetSkins.Length} max_bone={maximumBone} centre_z={centre:F3}");
        Check(helmetSkins.Length > 0 && maximumBone is >= 0 and < 72, "The helmet does not skin onto the 72-bone rig.");
        Check(centre is >= 1.4f and <= 2.0f, $"The bind-pose helmet centre z is {centre:F3}.");
        Check(!prepared.Parts.Parts.Any(p => p.Kind == GuardianPartKind.Customisation), "Head parts drew under a helmet.");
        var weapon = prepared.Scene.Instances.Where(i => i.Name.StartsWith("Weapon:", StringComparison.Ordinal)).ToArray();
        Check(weapon.Length > 0, "The weapon has no instances.");
        Check(weapon.All(i => animation.AttachedSkins.Any(pair => pair.Key.ModelTag == i.Mesh.TagHash
            && pair.Key.PartOffsets.Contains(i.Mesh.PartIndex) && pair.Value.Bone == GuardianAssembler.GripBone)),
            "A weapon part is not attached on bone 24.");
        Posed(catalog, prepared, animation, weapon);
        HoldPoses(assembler);
        Render(catalog, prepared, Path.Combine(output, "guardian-titan-helmet.png"));
        HeadReport(assembler);
        Faces(assembler);
        HelmetOff(assembler, output);
        ClassItems(assembler, output);
        Hoods(assembler, output);
        CloakCopies(assembler);
        ClassItemSweep(assembler, output);
        Hands(assembler, output);
        GearSweep(assembler);
        AtlasSurvey(assembler);
        Decals(assembler, output);
    }

    /** The first class item draws on each body above z 0.5, or its slot notes say it stays at the origin. */
    private static void ClassItems(GuardianAssembler assembler, string output)
    {
        var catalog = assembler.Catalog;
        uint item = FirstArmor[4];
        var rig = new SkeletonReader(catalog).Read(GuardianClasses.Titan);
        var reader = new ModelSkinReader(catalog);
        foreach (uint entity in GuardianArt.ItemEntities(catalog, catalog.Read(item), 0, false))
            foreach (var i in new AssetSceneLoader(catalog).Load(entity).Instances)
            {
                string bone;
                try { bone = reader.ReadExternal(i.Mesh.TagHash, rig).SelectMany(s => s.Vertices).SelectMany(Bones).DefaultIfEmpty(-1).Max().ToString(); }
                catch (Exception error) when (error is NotSupportedException or InvalidDataException) { bone = "error " + error.Message; }
                Console.WriteLine($"guardian_class_item_instance item=0x{item:X8} entity=0x{entity:X8} mesh=0x{i.Mesh.TagHash:X8} "
                    + $"part={i.Mesh.PartIndex:X} stage={i.Mesh.Native?.RenderStage} lod={i.Mesh.LodLevel} never={i.Mesh.NeverDrawn} "
                    + $"visible={i.InitiallyVisible?.ToString() ?? "?"} vertices={i.Mesh.Vertices.Length} max_bone={bone}");
            }
        foreach (var body in GuardianClasses.All)
        {
            var prepared = assembler.Prepare(body.BodyTag, GuardianLoadout.Default with { ClassArmor = item });
            var drawn = prepared.Scene.Instances.Where(i => i.Name.StartsWith("Class item:", StringComparison.Ordinal)).ToArray();
            float centre = drawn.Length == 0 ? float.NaN : (Bounds(drawn).Min.Z + Bounds(drawn).Max.Z) / 2;
            var notes = prepared.SlotNotes.GetValueOrDefault("Class item") ?? [];
            bool flagged = notes.Any(line => line.Contains("stays at the origin", StringComparison.Ordinal));
            Console.WriteLine($"guardian_class_item class={body.Name} item=0x{item:X8} instances={drawn.Length} centre_z={centre:F3} "
                + $"flagged={flagged} notes={string.Join(" | ", notes)}");
            Check((drawn.Length != 0 && centre > 0.5f) || flagged,
                $"The {body.Name} class item neither draws on the body nor says it stays at the origin.");
            if (body.BodyTag == GuardianClasses.Titan) Render(catalog, prepared, Path.Combine(output, "guardian-titan-class-item.png"), posed: false);
        }
    }

    /**
     * Every class item of every class and gender, each art entity set once. Each drawn part is skinned to the body,
     * sits where its class wears it and moves with the stance; plated parts bind plates; cloth binds a posed stream.
     * A part that cannot must carry a slot note naming why; a part the plan skips for a non-gear reason is listed.
     */
    private static void ClassItemSweep(GuardianAssembler assembler, string output)
    {
        var catalog = assembler.Catalog;
        var clock = Stopwatch.StartNew();
        var cache = new Dictionary<uint, TextureData>();
        var clips = new Dictionary<uint, AnimationControlClip>();
        var seen = new HashSet<string>();
        var failures = new List<string>();
        var tally = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var centres = new SortedDictionary<string, (Vector3 Min, Vector3 Max)>(StringComparer.Ordinal);
        var skips = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var rendered = new HashSet<string>();
        // The item type names the class, and so where the item is worn; art rows do not.
        var places = InvestmentCatalog.Load(catalog).Items.Where(item => item.Bucket == ClassItemBucket)
            .ToDictionary(item => item.Tag, item => item.TypeName switch
                { "Hunter Cloak" => "back", "Titan Mark" => "waist", "Warlock Bond" => "arm", _ => null });
        byte[] items = catalog.Read(ItemTable);
        long count = BinaryPrimitives.ReadInt64LittleEndian(items.AsSpan(8));
        int start = checked((int)(16 + BinaryPrimitives.ReadInt64LittleEndian(items.AsSpan(16)) + 16));
        for (int index = 0; index < count; index++)
        {
            uint tag = BinaryPrimitives.ReadUInt32LittleEndian(items.AsSpan(start + index * 24 + 16));
            if (!catalog.TryGetEntry(tag, out _)) continue;
            byte[] definition = catalog.Read(tag);
            if (definition.Length <= BucketByte || definition[BucketByte] != ClassItemBucket) continue;
            foreach (var body in GuardianClasses.All)
                foreach (bool female in new[] { false, true })
                {
                    string where = $"{body.Name} {(female ? "female" : "male")} item 0x{tag:X8}";
                    IReadOnlyList<uint> entities;
                    try { entities = GuardianArt.ItemEntities(catalog, definition, body.ClassByte, female); }
                    catch (InvalidDataException error) { failures.Add($"{where}: art unreadable: {error.Message}"); continue; }
                    if (entities.Count == 0 || !seen.Add($"{body.Name}:{string.Join(",", entities.Order())}")) continue;
                    var prepared = assembler.Prepare(body.BodyTag, GuardianLoadout.Default with { ClassArmor = tag, RaceGender = female ? 1 : 0 });
                    if (!clips.TryGetValue(body.BodyTag, out var clip))
                        clips.Add(body.BodyTag, clip = prepared.Animation!.ReadClip(HoldClip));
                    var result = ClassItem(catalog, prepared, clip, cache, places.GetValueOrDefault(tag));
                    string key = $"{body.Name} {result.Kind}";
                    tally[key] = tally.GetValueOrDefault(key) + 1;
                    if (result.Centre is { } centre)
                        centres[key] = centres.TryGetValue(key, out var range) ? (Vector3.Min(range.Min, centre), Vector3.Max(range.Max, centre)) : (centre, centre);
                    foreach (string reason in result.Skips) skips[reason] = skips.GetValueOrDefault(reason) + 1;
                    if (result.Error is not null) failures.Add($"{where}: {result.Error}");
                    // One render of the first passing cloth item per place, on the class that wears it there.
                    string? place = places.GetValueOrDefault(tag);
                    string wearer = place switch { "back" => "Hunter", "waist" => "Titan", _ => "Warlock" };
                    if (result.Error is null && result.Kind == $"cloth {place}" && body.Name == wearer && rendered.Add(place!))
                    {
                        string name = $"guardian-{body.Name.ToLowerInvariant()}-{(female ? "female" : "male")}-cloth-{tag:X8}";
                        Render(catalog, prepared, Path.Combine(output, name + ".png"));
                        Render(catalog, prepared, Path.Combine(output, name + "-back.png"), back: true);
                    }
                }
        }
        Console.WriteLine($"guardian_class_item_sweep sets={seen.Count} failures={failures.Count} ms={clock.ElapsedMilliseconds} "
            + string.Join(" ", tally.Select(pair => $"[{pair.Key}]={pair.Value}")));
        foreach (var (key, range) in centres)
            Console.WriteLine($"  class_item_centres [{key}] min={range.Min:F2} max={range.Max:F2}");
        foreach (var (reason, parts) in skips) Console.WriteLine($"  class_item_plan_skip parts={parts} {reason}");
        foreach (string failure in failures.Take(40)) Console.WriteLine($"  class_item_failure {failure}");
        Check(failures.Count == 0, "A class item part is not skinned, worn, posed or textured, and no note says why.");
        Check(GuardianClasses.All.All(body => tally.Keys.Any(key => key.StartsWith($"{body.Name} cloth", StringComparison.Ordinal))),
            "The sweep did not meet cloth on every class.");
    }

    /**
     * Every armor art entity with a helmet-key part, for each gender and helmet state: a part labelled with a gender or
     * the helmet key draws exactly when its labels match. With the helmet on, each Hunter cloak hood wraps the head.
     */
    private static void Hoods(GuardianAssembler assembler, string output)
    {
        var catalog = assembler.Catalog;
        var clock = Stopwatch.StartNew();
        string[] slotLabels = ["Helmet", "Gauntlets", "Chest", "Legs", ClassItemLabel];
        var types = InvestmentCatalog.Load(catalog).Items.Where(item => item.Bucket is >= 3 and <= 7).DistinctBy(item => item.Tag)
            .ToDictionary(item => item.Tag, item => item.TypeName);
        var configLabels = new Dictionary<uint, IReadOnlyList<IReadOnlySet<(uint Key, uint Value)>>?>();
        var keyed = new Dictionary<uint, bool>();
        var raw = new Dictionary<uint, MeshInstance[]>();
        var seen = new HashSet<string>();
        var failures = new List<string>();
        var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int prepares = 0, labelled = 0, cloakHoods = 0;
        bool rendered = false;
        byte[] items = catalog.Read(ItemTable);
        long count = BinaryPrimitives.ReadInt64LittleEndian(items.AsSpan(8));
        int start = checked((int)(16 + BinaryPrimitives.ReadInt64LittleEndian(items.AsSpan(16)) + 16));
        for (int index = 0; index < count; index++)
        {
            uint tag = BinaryPrimitives.ReadUInt32LittleEndian(items.AsSpan(start + index * 24 + 16));
            if (!catalog.TryGetEntry(tag, out _)) continue;
            byte[] definition = catalog.Read(tag);
            if (definition.Length <= BucketByte || definition[BucketByte] is < 3 or > 7) continue;
            int slot = definition[BucketByte] - 3;
            string type = types.GetValueOrDefault(tag) ?? "";
            // The class the type names wears the item; the art rows do not name it.
            var bodies = GuardianClasses.All.OrderBy(body => type.StartsWith(body.Name, StringComparison.Ordinal) ? 0 : 1).ToArray();
            foreach (bool female in new[] { false, true })
            {
                var (body, entities) = bodies.Select(candidate => (candidate, Entities(definition, candidate, female)))
                    .FirstOrDefault(pair => pair.Item2.Count != 0);
                if (body is null || !seen.Add(string.Join(",", entities.Order())) || !entities.Any(HasKey)) continue;
                kinds[type] = kinds.GetValueOrDefault(type) + 1;
                var states = new List<(bool Helmet, PreparedGuardian Prepared)>();
                int hoodsBefore = cloakHoods, failuresBefore = failures.Count;
                // A helmet item draws only with the helmet on.
                foreach (bool helmet in slot == 0 ? new[] { true } : new[] { true, false })
                {
                    var loadout = GuardianLoadout.Default.WithArmor(slot, tag) with { RaceGender = female ? 1 : 0, ShowHelmet = helmet };
                    var prepared = assembler.Prepare(body.BodyTag, loadout);
                    prepares++;
                    states.Add((helmet, prepared));
                    string where = $"{body.Name} {(female ? "female" : "male")} item 0x{tag:X8} helmet {(helmet ? "on" : "off")}";
                    var hood = Compare(prepared, slotLabels[slot], entities, female, where);
                    if (type == "Hunter Cloak" && hood.Count != 0) Wraps(prepared, hood, where);
                }
                // One render of the first Hunter cloak with a hood, from behind, helmet on and off.
                if (!rendered && cloakHoods != hoodsBefore && failures.Count == failuresBefore)
                {
                    rendered = true;
                    foreach (var (helmet, prepared) in states)
                        Render(catalog, prepared, Path.Combine(output, $"guardian-hunter-hood-{tag:X8}-helmet-{(helmet ? "on" : "off")}.png"),
                            posed: false, back: true);
                }
            }
        }
        Console.WriteLine($"guardian_hoods sets={kinds.Values.Sum()} prepares={prepares} labelled_parts={labelled} cloak_hoods={cloakHoods} "
            + $"failures={failures.Count} ms={clock.ElapsedMilliseconds} " + string.Join(" ", kinds.Select(pair => $"[{pair.Key}]={pair.Value}")));
        foreach (string failure in failures.Take(40)) Console.WriteLine($"  hood_failure {failure}");
        Check(failures.Count == 0, "An armor part labelled with a gender or the helmet key draws in the wrong state, or a hood misses the head.");
        Check(cloakHoods > 0, "The check met no Hunter cloak hood.");

        IReadOnlyList<uint> Entities(byte[] item, GuardianClass body, bool female)
        {
            try { return GuardianArt.ItemEntities(catalog, item, body.ClassByte, female); }
            catch (InvalidDataException) { return []; }
        }

        IReadOnlyList<IReadOnlySet<(uint Key, uint Value)>>? Labels(uint config)
        {
            if (configLabels.TryGetValue(config, out var found)) return found;
            try { found = EntityPartLabels.Read(catalog, config); }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException
                or ArgumentException or IndexOutOfRangeException) { found = null; }
            configLabels.Add(config, found);
            return found;
        }

        bool HasKey(uint entity)
        {
            if (keyed.TryGetValue(entity, out bool found)) return found;
            byte[] definition = catalog.Read(entity);
            found = EntityConfigs(definition)
                .Any(config => Labels(config)?.Any(set => set.Any(label => label.Key == EntityPartLabels.HelmetShownKey)) == true);
            keyed.Add(entity, found);
            return found;
        }

        // Each drawable part of the entities in a spawn with no inputs, whatever its initial state.
        MeshInstance[] Raw(uint entity)
        {
            if (!raw.TryGetValue(entity, out var found))
                raw.Add(entity, found = new AssetSceneLoader(catalog).Load(entity).Instances
                    .Where(i => !i.Mesh.NeverDrawn && i.Mesh.LodLevel == 0 && i.InitialVisibilityReason != MeshData.ClothSetPartReason
                        && (i.Mesh.Native is not { } native || NativeScenePlan.DrawnStages.Contains(native.RenderStage))).ToArray());
            return found;
        }

        // Adds a failure for each labelled part drawn in the wrong state; returns the drawn hood parts.
        List<MeshInstance> Compare(PreparedGuardian prepared, string label, IReadOnlyList<uint> entities, bool female, string where)
        {
            uint state = prepared.Parts.Parts.Any(part => part.Label == "Helmet") ? EntityPartLabels.True : EntityPartLabels.False;
            uint gender = female ? EntityPartLabels.Female : EntityPartLabels.Male;
            var drawn = InstancesOf(prepared, part => part.Label == label).GroupBy(i => (i.Mesh.TagHash, i.Mesh.PartIndex))
                .ToDictionary(group => group.Key, group => group.First());
            var hood = new List<MeshInstance>();
            foreach (var instance in entities.SelectMany(Raw))
            {
                var parts = Labels(instance.ModelSourceConfigTag);
                if (parts is null || (uint)instance.Mesh.LogicalPartIndex >= parts.Count) continue;
                var set = parts[instance.Mesh.LogicalPartIndex];
                uint? partGender = set.Where(pair => pair.Key == EntityPartLabels.GenderKey).Select(pair => (uint?)pair.Value).FirstOrDefault();
                uint? partHelmet = set.Where(pair => pair.Key == EntityPartLabels.HelmetShownKey).Select(pair => (uint?)pair.Value).FirstOrDefault();
                if (partGender is null && partHelmet is null) continue;
                labelled++;
                bool want = (partGender is null || partGender == gender) && (partHelmet is null || partHelmet == state);
                bool got = drawn.TryGetValue((instance.Mesh.TagHash, instance.Mesh.PartIndex), out var shown);
                if (want != got)
                    failures.Add($"{where}: model 0x{instance.Mesh.TagHash:X8} part {instance.Mesh.PartIndex:X} "
                        + $"gender=0x{partGender ?? 0:X8} helmet=0x{partHelmet ?? 0:X8} is {(got ? "drawn" : "not drawn")}");
                if (got && partHelmet == EntityPartLabels.True) hood.Add(shown!);
            }
            return hood;
        }

        // A hood rises past the head bone and passes within HoodHeadReach of it at bind.
        void Wraps(PreparedGuardian prepared, List<MeshInstance> hood, string where)
        {
            var rig = prepared.Animation?.Rig;
            var bone = rig?.Bones.FirstOrDefault(b => b.Name.StartsWith("b_head", StringComparison.Ordinal));
            if (bone is null) { failures.Add($"{where}: the rig has no b_head"); return; }
            var head = bone.BindObject.Translation;
            var (min, max) = Bounds(hood);
            float reach = Vector3.Distance(head, Vector3.Clamp(head, min, max));
            if (max.Z < head.Z || reach > HoodHeadReach)
                failures.Add($"{where}: hood bind bounds {min:F2}..{max:F2} miss the head at {head:F2}");
            else cloakHoods++;
        }
    }

    /**
     * Every Hunter cloak of each gender, helmet on and off. No class item part draws twice, a cloth model draws only the
     * parts its draw set 0 lists, and every drawn part moves under the stance unless a slot note names its model.
     */
    private static void CloakCopies(GuardianAssembler assembler)
    {
        var catalog = assembler.Catalog;
        var clock = Stopwatch.StartNew();
        var hunter = GuardianClasses.ForBody(GuardianClasses.Hunter)!;
        var failures = new List<string>();
        var seen = new HashSet<string>();
        AnimationControlClip? clip = null;
        int prepares = 0, clothParts = 0, cloaks = 0;
        foreach (var item in InvestmentCatalog.Load(catalog).Items
            .Where(item => item.Bucket == ClassItemBucket && item.TypeName == "Hunter Cloak").DistinctBy(item => item.Tag))
        {
            cloaks++;
            byte[] definition = catalog.Read(item.Tag);
            foreach (bool female in new[] { false, true })
            {
                IReadOnlyList<uint> entities;
                try { entities = GuardianArt.ItemEntities(catalog, definition, hunter.ClassByte, female); }
                catch (InvalidDataException error) { failures.Add($"item 0x{item.Tag:X8}: art unreadable: {error.Message}"); continue; }
                if (entities.Count == 0 || !seen.Add($"{female}:{string.Join(",", entities.Order())}")) continue;
                foreach (bool helmet in new[] { true, false })
                {
                    string where = $"{(female ? "female" : "male")} item 0x{item.Tag:X8} helmet {(helmet ? "on" : "off")}";
                    var prepared = assembler.Prepare(GuardianClasses.Hunter,
                        GuardianLoadout.Default with { ClassArmor = item.Tag, RaceGender = female ? 1 : 0, ShowHelmet = helmet });
                    prepares++;
                    var animation = prepared.Animation ?? throw new InvalidDataException($"{where}: the guardian has no animation.");
                    clip ??= animation.ReadClip(HoldClip);
                    var parts = InstancesOf(prepared, part => part.Label == ClassItemLabel);
                    string? error;
                    try { error = DrawnOnce(catalog, parts, ref clothParts) ?? Moves(prepared, animation, clip, parts); }
                    catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
                    { error = exception.Message; }
                    if (error is not null) failures.Add($"{where}: {error}");
                }
            }
        }
        Console.WriteLine($"guardian_cloak_copies cloaks={cloaks} prepares={prepares} cloth_parts={clothParts} failures={failures.Count} "
            + $"ms={clock.ElapsedMilliseconds}");
        foreach (string failure in failures.Take(40)) Console.WriteLine($"  cloak_copy_failure {failure}");
        Check(failures.Count == 0, "A Hunter cloak draws a part twice, draws a cloth part its draw set does not list, or leaves a part at bind.");
        Check(clothParts > 0, "The check met no drawn cloth part.");
    }

    /**
     * Each drawn part is one index range of its model, drawn once; a cloth part is in its cloth asset's draw set 0.
     * @param clothParts Counts the cloth parts checked.
     * @return The first defect; null when none.
     */
    private static string? DrawnOnce(PackageCatalog catalog, MeshInstance[] parts, ref int clothParts)
    {
        var drawn = new HashSet<(uint Model, int Stage, uint First, uint Count)>();
        var sets = new Dictionary<uint, HashSet<(int, uint, uint)>>();
        foreach (var part in parts)
        {
            var native = part.Mesh.Native!;
            if (!drawn.Add((part.Mesh.TagHash, native.RenderStage, native.FirstIndex, native.IndexCount)))
                return $"{part.Name} draws model 0x{part.Mesh.TagHash:X8} indices {native.FirstIndex}+{native.IndexCount} twice";
            if (!part.RequiresClothSimulation) continue;
            if (!sets.TryGetValue(part.ModelSourceConfigTag, out var listed))
                sets.Add(part.ModelSourceConfigTag, listed = ClothSetZero(catalog, part.ModelSourceConfigTag));
            if (!listed.Contains((native.RenderStage, native.FirstIndex, native.IndexCount)))
                return $"{part.Name} cloth part {part.Mesh.PartIndex:X} is not in its cloth draw set 0";
            clothParts++;
        }
        return null;
    }

    /** The config tag of each component row of an entity. */
    private static IEnumerable<uint> EntityConfigs(byte[] entity) =>
        NativeGearPlating.Rows(entity, TagView.FieldOffset<SEntity>(nameof(SEntity.Components)), SPatternComponent.Class,
            Unsafe.SizeOf<SPatternComponent>()).Select(row => TagView.At<SPatternComponent>(entity, row).Config);

    /** Stage and index range of each part that draw set 0 of a cloth config's asset lists, read from the packages. */
    private static HashSet<(int, uint, uint)> ClothSetZero(PackageCatalog catalog, uint configTag)
    {
        byte[] config = NativeGearPlating.Require(catalog, configTag, SEntityConfig.Class);
        int resource = TagView.TargetOrSelf(config,
            in TagView.Member<SEntityConfig, RelativePointer<Unknown>>(config, 0, nameof(SEntityConfig.Definition)));
        uint ClothMember(string member) => TagView.Member<SClothModelComponent, uint>(config, resource, member);
        byte[] model = NativeGearPlating.Require(catalog, ClothMember(nameof(SClothModelComponent.Model)), SEntityModel.Class);
        byte[] asset = NativeGearPlating.Require(catalog, ClothMember(nameof(SClothModelComponent.HavokClothAsset)), SSkeletonAsset.Class);
        int mesh = NativeGearPlating.Rows(model, TagView.FieldOffset<SEntityModel>(nameof(SEntityModel.Meshes)),
            SEntityModelMesh.Class, Unsafe.SizeOf<SEntityModelMesh>()).Single();
        int[] rows = NativeGearPlating.Rows(model, mesh + TagView.FieldOffset<SEntityModelMesh>(nameof(SEntityModelMesh.Parts)),
            SEntityModelPart.Class, Unsafe.SizeOf<SEntityModelPart>());
        var listed = new HashSet<(int, uint, uint)>();
        for (int stage = 0; stage < ClothStages; stage++)
            foreach (int at in NativeGearPlating.Rows(asset, TagView.FieldOffset<SSkeletonAsset>($"{ClothSetZeroLists}[{stage}].PartIndices"),
                SI32Element.Class, Unsafe.SizeOf<SI32Element>()))
            {
                int part = TagView.Member<SEntityModelMesh, ushort>(model, mesh, $"StageIndexBoundaries[{stage}]")
                    + TagView.Member<SI32Element, int>(asset, at, nameof(SI32Element.Value));
                listed.Add((stage, TagView.Member<SEntityModelPart, uint>(model, rows[part], nameof(SEntityModelPart.IndexStart)),
                    TagView.Member<SEntityModelPart, uint>(model, rows[part], nameof(SEntityModelPart.IndexCount))));
            }
        return listed;
    }

    /**
     * Under the stance each drawn part moves, measured on its own vertices; a cloth display part on its posed stream.
     * @return The first part that stays at bind and that no class item note names; null when every part moves.
     */
    private static string? Moves(PreparedGuardian prepared, AssetAnimationSource animation, AnimationControlClip clip, MeshInstance[] parts)
    {
        var uploaded = new Dictionary<MeshData, RenderVertex[]>();
        using var preview = new AssetAnimationPreview(animation, prepared.Scene, (mesh, vertices) => uploaded[mesh] = vertices.ToArray(), () => { });
        preview.SetClip(clip, play: false);
        if (preview.Error is not null) return $"the stance fails: {preview.Error}";
        var notes = prepared.SlotNotes.GetValueOrDefault(ClassItemLabel) ?? [];
        foreach (var part in parts)
        {
            float moved = 0;
            if (NativeClothDisplay.IsDisplayMesh(part) && preview.ClothStreamFor(part.Mesh.Native!.VertexStreams[0].HeaderTag) is { } stream)
            {
                // Lane 0 of each three-lane vertex is the position.
                var posed = MemoryMarshal.Cast<byte, Vector4>(stream.Span);
                var bind = MemoryMarshal.Cast<byte, Vector4>(NativeClothDisplay.BindStream(part.Mesh.Native!).AsSpan());
                foreach (uint index in part.Mesh.SourceVertexIndices)
                    moved = MathF.Max(moved, Vector4.Distance(posed[(int)index * 3], bind[(int)index * 3]));
            }
            else if (uploaded.TryGetValue(part.Mesh, out var vertices))
                for (int v = 0; v < vertices.Length; v++)
                    moved = MathF.Max(moved, Vector3.Distance(vertices[v].Position, part.Mesh.Vertices[v].Position));
            if (moved < StanceMove && !notes.Any(line => line.Contains($"0x{part.Mesh.TagHash:X8}", StringComparison.Ordinal)))
                return $"{part.Name} part {part.Mesh.PartIndex:X} stays at bind under the stance";
        }
        return null;
    }

    private sealed record ClassItemResult(string Kind, string? Error, Vector3? Centre, IReadOnlyList<string> Skips);

    /**
     * One prepared class item. Kind is "cloth" or "model" and its place, or "noted" when no part is skinned and a note says why.
     * @return The kind, the first defect, the bind centre of the skinned parts, and the plan's non-gear skip reasons.
     */
    private static ClassItemResult ClassItem(PackageCatalog catalog, PreparedGuardian prepared,
        AnimationControlClip clip, Dictionary<uint, TextureData> cache, string? wanted)
    {
        var parts = InstancesOf(prepared, part => part.Label == ClassItemLabel);
        var notes = prepared.SlotNotes.GetValueOrDefault(ClassItemLabel) ?? [];
        if (parts.Length == 0)
            return new("noted", notes.Count != 0 ? null : "no part drew and no note says why", null, []);
        var animation = prepared.Animation ?? throw new InvalidDataException("The guardian has no animation.");
        bool cloth = parts.Any(part => part.RequiresClothSimulation);
        string kind = cloth ? "cloth" : "model";
        if (cloth && !notes.Any(line => line.Contains(GuardianAssembler.ClothNote, StringComparison.Ordinal)))
            return new(kind, "its cloth is not skinned: " + string.Join(" | ", notes), null, []);
        var skinned = new List<MeshInstance>();
        foreach (var part in parts)
        {
            var mesh = part.Mesh;
            var skin = animation.SkinBindings.FirstOrDefault(s => s.ModelTag == mesh.TagHash && s.PartOffsets.Contains(mesh.PartIndex));
            if (skin is null)
            {
                if (notes.Any(line => line.Contains($"0x{mesh.TagHash:X8}", StringComparison.Ordinal))) continue;
                return new(kind, $"{part.Name} has no skin and no note", null, []);
            }
            if (part.RequiresClothSimulation != (skin.PaletteKind == SkinPaletteKind.ClothInput))
                return new(kind, $"{part.Name} has a {skin.PaletteKind} skin", null, []);
            skinned.Add(part);
        }
        if (skinned.Count == 0) return new("noted", null, null, []);
        if (skinned.Count != parts.Length) kind += " partly noted";
        var bounds = Bounds(skinned);
        var (error, skips) = Textured(catalog, prepared, parts, cache);
        var (place, misplaced) = Worn(wanted, bounds, animation.Rig);
        error = misplaced ?? Posed(prepared, animation, clip, skinned) ?? error;
        return new($"{kind} {place}", error, (bounds.Min + bounds.Max) / 2, skips);
    }

    /**
     * Where a class item sits at bind: "back" reaching behind the spine and up to the chest (a cloak or hood), "waist" across
     * the belt line (a mark), "arm" at an arm bone (a bond).
     * @param wanted The place the item's type needs; null accepts any of the three.
     * @return The place, and why the bounds miss the place the item needs; null when they reach it.
     */
    private static (string Place, string? Error) Worn(string? wanted, (Vector3 Min, Vector3 Max) bounds, SkeletonRig rig)
    {
        Vector3 Bone(string name) => rig.Bones.First(bone => bone.Name.StartsWith(name, StringComparison.Ordinal)).BindObject.Translation;
        var centre = (bounds.Min + bounds.Max) / 2;
        float arm = new[] { "b_l_upperarm", "b_r_upperarm", "b_l_forearm", "b_r_forearm", "b_l_hand", "b_r_hand" }
            .Min(name => Vector3.Distance(centre, Bone(name)));
        var places = new List<string>();
        if (bounds.Min.X < Bone("b_spine_3").X - WornBackDepth && bounds.Max.Z > Bone("b_spine_3").Z) places.Add("back");
        if (bounds.Min.Z < Bone("b_spine_2").Z && bounds.Max.Z > Bone("b_l_thigh").Z) places.Add("waist");
        if (arm < WornArmReach) places.Add("arm");
        if (wanted is null) return places.Count != 0 ? ($"{places[0]} (untyped)", null)
            : ("nowhere", $"bind bounds {bounds.Min:F2}..{bounds.Max:F2} are at no worn place");
        return places.Contains(wanted) ? (wanted, null)
            : ("nowhere", $"bind bounds {bounds.Min:F2}..{bounds.Max:F2} are not at the {wanted}");
    }

    /**
     * Under the stance the class item moves and stays on the posed body; a cloth display part binds a posed stream.
     * @return The first defect; null when it passes.
     */
    private static string? Posed(PreparedGuardian prepared, AssetAnimationSource animation, AnimationControlClip clip, List<MeshInstance> skinned)
    {
        var uploaded = new Dictionary<MeshData, RenderVertex[]>();
        using var preview = new AssetAnimationPreview(animation, prepared.Scene, (mesh, vertices) => uploaded[mesh] = vertices.ToArray(), () => { });
        var cloth = skinned.Where(NativeClothDisplay.IsDisplayMesh).ToArray();
        if (cloth.Any(part => preview.ClothStreamFor(part.Mesh.Native!.VertexStreams[0].HeaderTag) is null))
            return "a cloth display part has no posed stream";
        preview.SetClip(clip, play: false);
        if (preview.Error is not null) return $"the stance fails: {preview.Error}";
        foreach (var part in cloth)
            if (preview.ClothStreamFor(part.Mesh.Native!.VertexStreams[0].HeaderTag)!.Value.Span.SequenceEqual(NativeClothDisplay.BindStream(part.Mesh.Native!)))
                return $"{part.Name} cloth stream did not move under the stance";
        if (skinned.Any(part => !uploaded.ContainsKey(part.Mesh))) return "a skinned part was not posed";
        var item = skinned.SelectMany(part => uploaded[part.Mesh]).Select(v => v.Position).ToArray();
        var rest = uploaded.Where(pair => !skinned.Any(part => part.Mesh == pair.Key)).SelectMany(pair => pair.Value).Select(v => v.Position).ToArray();
        (Vector3 Min, Vector3 Max) posed = (item.Aggregate(Vector3.Min), item.Aggregate(Vector3.Max));
        (Vector3 Min, Vector3 Max) body = (rest.Aggregate(Vector3.Min) - new Vector3(.05f), rest.Aggregate(Vector3.Max) + new Vector3(.05f));
        if (!Overlaps(posed, body)) return $"posed bounds {posed.Min:F2}..{posed.Max:F2} leave the posed body";
        var bind = Bounds(skinned);
        if (Vector3.Distance((bind.Min + bind.Max) / 2, (posed.Min + posed.Max) / 2) < .01f)
            return "the class item did not move under the stance";
        return null;
    }

    private static bool Overlaps((Vector3 Min, Vector3 Max) a, (Vector3 Min, Vector3 Max) b) =>
        a.Min.X <= b.Max.X && b.Min.X <= a.Max.X && a.Min.Y <= b.Max.Y && b.Min.Y <= a.Max.Y && a.Min.Z <= b.Max.Z && b.Min.Z <= a.Max.Z;

    /**
     * Each class item part the plan draws binds its gear inputs: plates for bit 25 and a scope for each dye bit.
     * @return The first defect, and the plan's skip reasons for the parts that are not about the gear scope.
     */
    private static (string? Error, IReadOnlyList<string> Skips) Textured(PackageCatalog catalog, PreparedGuardian prepared,
        MeshInstance[] parts, Dictionary<uint, TextureData> cache)
    {
        var plan = NativeScenePlan.Prepare(catalog, prepared.Scene, assetPreview: true, textureCache: cache);
        var lines = plan.Diagnostics.Where(line => parts.Any(part => line.Contains($"0x{part.Mesh.TagHash:X8}", StringComparison.Ordinal)
            || line.Contains($"0x{part.Mesh.MaterialTag:X8}", StringComparison.Ordinal))).Distinct().ToArray();
        if (lines.FirstOrDefault(line => line.StartsWith("Gear scope", StringComparison.Ordinal)) is { } gear) return (gear, lines);
        var set = parts.ToHashSet();
        foreach (var group in plan.Groups)
            foreach (var part in group.Parts.Where(part => set.Contains(part.Instance)))
            {
                uint bits = NativeGearPlating.GearBits(catalog, group.Mesh.MaterialTag);
                if (part.Instance.RequiresClothSimulation && NativeClothDisplay.IsDisplayMesh(part.Instance) && group.ClothDisplayStream is null)
                    return ($"{part.Instance.Name} cloth draws without its display stream", lines);
                if (bits == 0) continue;
                if (group.Material.Gear is not { } inputs) return ($"{part.Instance.Name} reads gear bits 0x{bits:X8} but binds none", lines);
                if ((bits & (1u << 25)) != 0 && !new uint[] { 0, 1, 2 }.All(inputs.PlateTextures.ContainsKey))
                    return ($"{part.Instance.Name} binds no plate t0 to t2", lines);
            }
        return (null, lines);
    }

    /** Each class and row draws exactly the four hand instances of the row's gender, under the default gauntlets. */
    private static void Hands(GuardianAssembler assembler, string output)
    {
        var catalog = assembler.Catalog;
        foreach (var body in GuardianClasses.All)
            foreach (var row in assembler.Appearance.Rows)
            {
                var prepared = assembler.Prepare(body.BodyTag, GuardianLoadout.Default with { RaceGender = row.Row });
                var hands = InstancesOf(prepared, part => part.Kind == GuardianPartKind.Body);
                uint gender = row.Female ? EntityPartLabels.Female : EntityPartLabels.Male;
                int matching = hands.Count(i => EntityPartLabels.Read(catalog, i.ModelSourceConfigTag)[i.Mesh.LogicalPartIndex]
                    .Contains((EntityPartLabels.GenderKey, gender)));
                bool gauntlets = InstancesOf(prepared, part => Bucket(catalog, part) == 4).Length != 0;
                Console.WriteLine($"guardian_hands class={body.Name} row={row.Row} female={row.Female} instances={hands.Length} "
                    + $"of_gender={matching} gauntlets={gauntlets} vertices={hands.Sum(i => i.Mesh.Vertices.Length)}");
                Check(hands.Length == 4 && matching == 4, $"The {body.Name} row {row.Row} hands are not four parts of the row's gender.");
                Check(gauntlets, $"The {body.Name} row {row.Row} default loadout has no gauntlets.");
                if (row.Row is 0 or 1)
                    Render(catalog, prepared, Path.Combine(output, $"guardian-{body.Name.ToLowerInvariant()}-row{row.Row}-hands.png"), posed: false);
            }
    }

    /** Every class and row, default armor and helmet off with markings 1 to 8: no skipped part, plate atlases and decal blends. */
    private static void GearSweep(GuardianAssembler assembler)
    {
        var catalog = assembler.Catalog;
        var cache = new Dictionary<uint, TextureData>();
        var failures = new List<string>();
        int plans = 0, plated = 0, decals = 0;
        var clock = Stopwatch.StartNew();
        foreach (var body in GuardianClasses.All)
            foreach (var row in assembler.Appearance.Rows)
            {
                var loadouts = new List<(string Name, GuardianLoadout Loadout)> { ("default", GuardianLoadout.Default with { RaceGender = row.Row }) };
                for (int marking = 1; marking <= 8; marking++)
                    loadouts.Add(($"markings {marking}", GuardianLoadout.Default with { RaceGender = row.Row, ShowHelmet = false, Variant4 = marking }));
                foreach (var (name, loadout) in loadouts)
                {
                    string where = $"{body.Name} row {row.Row} {name}";
                    var prepared = assembler.Prepare(body.BodyTag, loadout);
                    var plan = NativeScenePlan.Prepare(catalog, prepared.Scene, assetPreview: true, textureCache: cache);
                    plans++;
                    if (plan.SkippedReasons.Count != 0)
                        failures.Add($"{where}: skipped {string.Join("; ", plan.SkippedReasons.Select(pair => $"{pair.Value} {pair.Key}"))}: "
                            + string.Join(" | ", plan.Diagnostics.Where(line => line.StartsWith("Gear scope", StringComparison.Ordinal)).Distinct()));
                    if (plan.Diagnostics.Any(line => line.StartsWith("Gear kind unknown", StringComparison.Ordinal)))
                        failures.Add($"{where}: a plated gear object has no gear kind");
                    var (atlases, atlasError) = PlateAtlases(catalog, prepared, plan);
                    plated += atlases;
                    if (atlasError is not null) failures.Add($"{where}: {atlasError}");
                    var (blends, blendError) = DecalBlends(catalog, plan);
                    decals += blends;
                    if (blendError is not null) failures.Add($"{where}: {blendError}");
                }
            }
        Console.WriteLine($"guardian_gear_sweep plans={plans} plated_parts={plated} decal_parts={decals} failures={failures.Count} "
            + $"ms={clock.ElapsedMilliseconds}{(failures.Count == 0 ? "" : " first=" + string.Join(" || ", failures.Take(4)))}");
        Check(failures.Count == 0, "A guardian plan skipped a part, or a part lost its plate atlas or decal blend.");
        Check(plated > 0 && decals > 0, "The sweep met no plated marking or gauntlet part, or no decal part.");
    }

    /**
     * Every item with art in buckets 0 to 10, for each class and gender, and every face part the pickers reach:
     * its plate atlases at its gear kind's sizes hold every rectangle. Prints the furthest edge by bucket and set.
     */
    private static void AtlasSurvey(GuardianAssembler assembler)
    {
        var catalog = assembler.Catalog;
        var clock = Stopwatch.StartNew();
        var records = new Dictionary<uint, (byte[]? Record, string? Error)>();
        var edges = new SortedDictionary<(int Bucket, int Set), int>();
        var objects = new HashSet<string>();
        var failures = new List<string>();
        int failed = 0, plated = 0;
        byte[] items = catalog.Read(ItemTable);
        long count = BinaryPrimitives.ReadInt64LittleEndian(items.AsSpan(8));
        int start = checked((int)(16 + BinaryPrimitives.ReadInt64LittleEndian(items.AsSpan(16)) + 16));
        for (int index = 0; index < count; index++)
        {
            uint tag = BinaryPrimitives.ReadUInt32LittleEndian(items.AsSpan(start + index * 24 + 16));
            if (!catalog.TryGetEntry(tag, out _)) continue;
            byte[] definition = catalog.Read(tag);
            if (definition.Length <= BucketByte || NativePlateAtlases.KindForBucket(definition[BucketByte]) is not { } kind) continue;
            for (byte classByte = 0; classByte < GuardianClasses.All.Count; classByte++)
                foreach (bool female in new[] { false, true })
                {
                    IReadOnlyList<uint> entities;
                    try { entities = GuardianArt.ItemEntities(catalog, definition, classByte, female); }
                    catch (InvalidDataException) { continue; }
                    Survey($"item 0x{tag:X8}", definition[BucketByte], kind, entities);
                }
        }
        // Each face of each row, with each hair and each marking, reaches every head part the pickers offer.
        foreach (var row in assembler.Appearance.Rows)
            for (int preset = 0; preset < row.Presets.Count; preset++)
            {
                var counts = row.Presets[preset].VariantCounts;
                var bare = GuardianLoadout.Default with { RaceGender = row.Row, Preset = preset, ShowHelmet = false };
                var loadouts = Enumerable.Range(0, Math.Max(1, counts.GetValueOrDefault(GuardianAppearance.HairSelector)))
                    .Select(hair => bare with { Variant2 = hair })
                    .Concat(Enumerable.Range(0, Math.Max(1, counts.GetValueOrDefault(GuardianAppearance.MarkingSelector)))
                        .Select(marking => bare with { Variant4 = marking }));
                foreach (var loadout in loadouts)
                    foreach (var part in assembler.Appearance.Resolve(catalog, GuardianClasses.Titan, loadout).Parts
                        .Where(part => part.Kind == GuardianPartKind.Customisation))
                        Survey($"face part 0x{part.EntityTag:X8}", -1, NativePlateAtlases.CharacterKind, [part.EntityTag]);
            }
        int faces = objects.Count(key => key.StartsWith("-1:", StringComparison.Ordinal));
        Console.WriteLine($"guardian_atlas_survey gear_objects={objects.Count} face_parts={faces} plated_records={plated} failures={failed} "
            + $"ms={clock.ElapsedMilliseconds} furthest={string.Join(" ", edges.Select(pair => $"{(pair.Key.Bucket < 0 ? "face" : $"b{pair.Key.Bucket}")}/s{pair.Key.Set}={pair.Value}"))}"
            + (failures.Count == 0 ? "" : " first=" + string.Join(" || ", failures)));
        Check(failed == 0, "A plate rectangle does not fit the atlas its gear kind gives.");
        Check(plated > 0 && faces > 0, "The survey met no plated record or no face part.");

        // One gear object: the entities of one item for one class and gender, or one face part.
        void Survey(string where, int bucket, int kind, IReadOnlyList<uint> entities)
        {
            if (entities.Count == 0 || !objects.Add($"{bucket}:{string.Join(",", entities.Order())}")) return;
            var found = new List<byte[]>();
            foreach (uint entity in entities)
            {
                byte[] definition = catalog.Read(entity);
                foreach (uint config in EntityConfigs(definition))
                {
                    if (!catalog.TryGetEntry(config, out var entry) || entry.Reference != SEntityConfig.Class) continue;
                    if (!records.TryGetValue(config, out var read)) records.Add(config, read = NativeGearPlating.ReadRecord(catalog, config));
                    // The plan builds atlases from the same records that bind plates.
                    if (read.Record is { } record && record[BindsPlatesByte] != 0) found.Add(record);
                }
            }
            var atlases = NativePlateAtlases.Build(catalog, found, kind);
            foreach (byte[] record in found)
            {
                plated++;
                if (!atlases.TagsFor(record, new Dictionary<uint, uint>(), out string? error))
                {
                    failed++;
                    if (failures.Count < 6) failures.Add($"{where} kind {kind}: {error}");
                    continue;
                }
                uint plates = TagView.Member<SPlatedTextureSet, uint>(record, 0, nameof(SPlatedTextureSet.PlateCount));
                for (int plate = 0; plate < Math.Min(plates, 3); plate++)
                {
                    uint descriptorTag = TagView.Member<SPlatedTextureSet, uint>(record, 0, $"PlateDescriptor0[{plate}].Unk0");
                    byte[] descriptor = NativeGearPlating.Require(catalog, descriptorTag, NativeGearPlating.DescriptorClass);
                    int set = TagView.Member<STextureDescriptor, ushort>(descriptor, 0, nameof(STextureDescriptor.PlateSet));
                    foreach (int at in NativeGearPlating.Rows(descriptor, TagView.FieldOffset<STextureDescriptor>(nameof(STextureDescriptor.Rows)),
                        NativeGearPlating.DescriptorRowClass, Unsafe.SizeOf<UnknownTextureDescriptorRow>()))
                    {
                        ref readonly var cell = ref TagView.At<UnknownTextureDescriptorRow>(descriptor, at);
                        int edge = (int)Math.Max(unchecked((uint)cell.X + (uint)cell.Width), unchecked((uint)cell.Y + (uint)cell.Height));
                        edges[(bucket, set)] = Math.Max(edges.GetValueOrDefault((bucket, set)), edge);
                    }
                }
            }
        }
    }

    /**
     * Marking (selector 19) and gauntlet (bucket 4) parts that read the plate scope bind composed atlases at t0 to t2.
     * @return The parts checked, and the first failure.
     */
    private static (int Count, string? Error) PlateAtlases(PackageCatalog catalog, PreparedGuardian prepared, NativeScenePlan plan)
    {
        var wanted = InstancesOf(prepared, part => part.Selector == GuardianAppearance.MarkingSelector || Bucket(catalog, part) == 4)
            .Where(i => (NativeGearPlating.GearBits(catalog, i.Mesh.Native!.MaterialTag) & (1u << 25)) != 0).ToHashSet();
        int count = 0;
        foreach (var group in plan.Groups)
            foreach (var part in group.Parts.Where(part => wanted.Contains(part.Instance)))
            {
                count++;
                var plates = group.Material.Gear?.PlateTextures;
                if (plates is null || !new uint[] { 0, 1, 2 }.All(plates.ContainsKey))
                    return (count, $"{part.Instance.Name} binds no plate t0 to t2");
                foreach (uint tag in plates.Values)
                    if (!plan.Textures.TryGetValue(tag, out var texture) || !texture.LayoutStatus.StartsWith("Plate atlas", StringComparison.Ordinal))
                        return (count, $"{part.Instance.Name} plate 0x{tag:X8} is not a composed atlas");
            }
        return count == wanted.Count ? (count, null) : (count, $"{wanted.Count - count} marking or gauntlet parts are not in the plan");
    }

    /**
     * Every part whose material reads gear bit 29 (the hair and selector 17 decals) draws with blend 23.
     * @return The parts checked, and the first failure.
     */
    private static (int Count, string? Error) DecalBlends(PackageCatalog catalog, NativeScenePlan plan)
    {
        int count = 0;
        foreach (var group in plan.Groups.Where(group => (NativeGearPlating.GearBits(catalog, group.Mesh.MaterialTag) & (1u << 29)) != 0))
        {
            count += group.Parts.Count;
            if (group.Material.States.Selection.Blend != DecalBlend)
                return (count, $"decal material 0x{group.Mesh.MaterialTag:X8} draws with blend {group.Material.States.Selection.Blend}");
        }
        return (count, null);
    }

    /** The hairline decal of hair 0x80BB1080 leaves no magenta, and selector 17 does not paint its card black. */
    private static void Decals(GuardianAssembler assembler, string output)
    {
        var catalog = assembler.Catalog;
        var prepared = assembler.Prepare(GuardianClasses.Titan, GuardianLoadout.Default with { ShowHelmet = false, Variant2 = 0 });
        Check(prepared.Parts.Parts.Any(part => part.Selector == GuardianAppearance.HairSelector && part.EntityTag == HairlineHair),
            $"Hair variant 0 of the default face is not 0x{HairlineHair:X8}.");
        var head = InstancesOf(prepared, part => part.Kind == GuardianPartKind.Customisation);
        var focus = Bounds(head);
        var all = Render(catalog, prepared, Path.Combine(output, "guardian-titan-hairline.png"), posed: false, focus: focus);
        int magenta = all.Count(pixel => Magenta(pixel));
        var brows = InstancesOf(prepared, part => part.Selector == GuardianAppearance.ThirdSelector).ToHashSet();
        Check(brows.Count != 0, "The default face has no selector 17 part.");
        var bare = Render(catalog, prepared, Path.Combine(output, "guardian-titan-nobrows.png"), posed: false, focus: focus,
            keep: instance => !brows.Contains(instance));
        int changed = 0, black = 0;
        for (int i = 0; i < all.Length; i++)
        {
            if (Vector4.Distance(all[i], bare[i]) < 0.02f) continue;
            changed++;
            if (Luma(all[i]) < 0.01f) black++;
        }
        float blackShare = changed == 0 ? 0 : (float)black / changed;
        Console.WriteLine($"guardian_decals magenta={magenta} brow_pixels={changed} brow_black={black} brow_black_share={blackShare:F3}");
        Check(magenta == 0, $"The hairline render has {magenta} magenta pixels.");
        // Blended brows leave almost no black pixels; an opaque brow card leaves many.
        Check(changed > 0 && blackShare < 0.1f, "Selector 17 draws as a black card.");
    }

    /** Hair (selector 15) and markings (19) of the default face, helmet off: material, gear bits, dyes and plan notes; the plan skips nothing. */
    private static void HeadReport(GuardianAssembler assembler)
    {
        var catalog = assembler.Catalog;
        var prepared = assembler.Prepare(GuardianClasses.Titan, GuardianLoadout.Default with { ShowHelmet = false });
        var plan = NativeScenePlan.Prepare(catalog, prepared.Scene, assetPreview: true);
        Check(prepared.Parts.Parts.Any(p => p.Selector == GuardianAppearance.MarkingSelector), "The default face has no markings.");
        foreach (var part in prepared.Parts.Parts.Where(p => p.Selector is GuardianAppearance.HairSelector or GuardianAppearance.MarkingSelector))
        {
            var dyes = NativeGearPlating.ReadDyeIndices(catalog, part.DyeIndices, part.Label, out string? dyeError);
            Console.WriteLine($"guardian_head_report part={part.Label} entity=0x{part.EntityTag:X8} dyes={string.Join(",", part.DyeIndices)} "
                + $"records={(dyes is null ? "error " + dyeError : Tags(dyes.Records))}");
            foreach (var instance in prepared.Scene.Instances.Where(i => i.Name.StartsWith(part.Label + ":", StringComparison.Ordinal)))
            {
                uint material = instance.Mesh.Native?.MaterialTag ?? instance.Mesh.MaterialTag;
                string mesh = $"0x{instance.Mesh.TagHash:X8}";
                Console.WriteLine($"  instance mesh={mesh} part={instance.Mesh.PartIndex:X} stage={instance.Mesh.Native?.RenderStage} "
                    + $"material=0x{material:X8} gear_bits=0x{NativeGearPlating.GearBits(catalog, material):X8} "
                    + $"plan={string.Join(" | ", plan.Diagnostics.Where(line => line.Contains(mesh, StringComparison.OrdinalIgnoreCase)
                        || line.Contains($"{material:X8}", StringComparison.OrdinalIgnoreCase)).Distinct())}");
            }
        }
        Console.WriteLine($"guardian_head_report prepared={plan.PreparedPartCount}/{prepared.Scene.Instances.Count} "
            + $"skipped={string.Join("; ", plan.SkippedReasons.Select(pair => $"{pair.Value} {pair.Key}"))}");
        Check(plan.SkippedReasons.Count == 0, "The default guardian plan skips parts.");
    }

    /**
     * Every row and face preset opens with the head's neutral face: the face bones are mirror pairs, so each
     * face vertex has a mirror vertex within 2 mm.
     */
    private static void Faces(GuardianAssembler assembler)
    {
        const float Tolerance = 0.002f;
        var clock = Stopwatch.StartNew();
        var failures = new List<string>();
        int heads = 0;
        float worst = 0, worstRaw = 0;
        foreach (var row in assembler.Appearance.Rows)
            for (int preset = 0; preset < row.Presets.Count; preset++)
            {
                var body = GuardianClasses.All[preset % GuardianClasses.All.Count];
                var prepared = assembler.Prepare(body.BodyTag,
                    GuardianLoadout.Default with { RaceGender = row.Row, Preset = preset, ShowHelmet = false });
                string where = $"{body.Name} row {row.Row} preset {preset}";
                var animation = prepared.Animation ?? throw new InvalidDataException($"{where}: the guardian has no animation.");
                var restBones = animation.Binding?.RestBones ?? [];
                if (restBones.Count == 0) { failures.Add($"{where}: no face rest layer"); continue; }
                var head = InstancesOf(prepared, part => part.Selector == GuardianAppearance.HeadSelector)
                    .Where(i => i.Mesh.Native?.RenderStage == 0).ToArray();
                var uploaded = new Dictionary<MeshData, RenderVertex[]>();
                using var preview = new AssetAnimationPreview(animation, prepared.Scene, (mesh, vertices) => uploaded[mesh] = vertices.ToArray(), () => { });
                preview.ResetBindPose();
                var posed = new List<Vector3>(); var raw = new List<Vector3>(); var face = new List<int>();
                foreach (var instance in head)
                {
                    var skin = animation.SkinBindings.FirstOrDefault(s => s.ModelTag == instance.Mesh.TagHash && s.PartOffsets.Contains(instance.Mesh.PartIndex));
                    if (skin is null || !uploaded.TryGetValue(instance.Mesh, out var vertices)) { failures.Add($"{where}: head part {instance.Mesh.PartIndex:X} is not skinned"); continue; }
                    for (int v = 0; v < vertices.Length; v++)
                    {
                        var weights = skin.Vertices[(int)instance.Mesh.SourceVertexIndices[v]];
                        if (restBones.Contains(Dominant(weights))) face.Add(posed.Count);
                        posed.Add(vertices[v].Position);
                        raw.Add(instance.Mesh.Vertices[v].Position);
                    }
                }
                float miss = MirrorMiss(posed, face), rawMiss = MirrorMiss(raw, face);
                heads++;
                worst = MathF.Max(worst, miss); worstRaw = MathF.Max(worstRaw, rawMiss);
                if (face.Count == 0 || miss > Tolerance)
                    failures.Add($"{where}: {face.Count} face vertices, mirror miss {miss * 1000:F1} mm");
            }
        StanceKeepsFace(assembler, failures);
        Console.WriteLine($"guardian_faces heads={heads} worst_mirror_miss_mm={worst * 1000:F2} raw_bind_worst_mm={worstRaw * 1000:F2} "
            + $"ms={clock.ElapsedMilliseconds}{(failures.Count == 0 ? "" : " first=" + string.Join(" || ", failures.Take(4)))}");
        Check(failures.Count == 0, "A guardian face is not its neutral, mirror-symmetric pose.");

        static int Dominant(VertexSkinWeights w)
        {
            float[] weights = [w.Weights.X, w.Weights.Y, w.Weights.Z, w.Weights.W];
            int[] bones = [w.Bone0, w.Bone1, w.Bone2, w.Bone3];
            return bones[Array.IndexOf(weights, weights.Max())];
        }

        // The largest distance from a face vertex's mirror across y = 0 to its nearest vertex.
        static float MirrorMiss(List<Vector3> points, List<int> face)
        {
            float result = 0;
            foreach (int index in face)
            {
                var mirror = points[index] with { Y = -points[index].Y };
                float best = float.MaxValue;
                foreach (var point in points) best = MathF.Min(best, Vector3.DistanceSquared(point, mirror));
                result = MathF.Max(result, MathF.Sqrt(best));
            }
            return result;
        }
    }

    /** Under the hold pose each face bone keeps its rest place relative to b_head. */
    private static void StanceKeepsFace(GuardianAssembler assembler, List<string> failures)
    {
        var prepared = assembler.Prepare(GuardianClasses.Titan, GuardianLoadout.Default with { ShowHelmet = false, RaceGender = 4 });
        var animation = prepared.Animation!;
        int head = animation.Rig.Bones.First(b => b.Name.StartsWith("b_head", StringComparison.Ordinal)).Index;
        using var preview = new AssetAnimationPreview(animation, prepared.Scene, (_, _) => { }, () => { });
        preview.ResetBindPose();
        var rest = Relative(preview.Pose);
        preview.SetClip(animation.ReadClip(HoldClip), play: false);
        var stance = Relative(preview.Pose);
        float drift = rest.Zip(stance, (a, b) => (a - b).Translation.Length()).DefaultIfEmpty(0).Max();
        Console.WriteLine($"guardian_faces_stance rest_bones={rest.Length} face_drift_mm={drift * 1000:F3}");
        if (rest.Length == 0 || drift > 0.0005f) failures.Add($"stance moves the face bones {drift * 1000:F2} mm off their rest place");

        Matrix4x4[] Relative(SkeletonPose pose)
        {
            Matrix4x4.Invert(pose.ObjectTransforms[head], out var inverse);
            return (animation.Binding?.RestBones ?? []).Select(bone => pose.ObjectTransforms[bone] * inverse).ToArray();
        }
    }

    /** The stance moves the body; weapon palettes repeat the grip bone and its vertices sit near it. */
    private static void Posed(PackageCatalog catalog, PreparedGuardian prepared, AssetAnimationSource animation, MeshInstance[] weapon)
    {
        var uploaded = new Dictionary<MeshData, RenderVertex[]>();
        using var preview = new AssetAnimationPreview(animation, prepared.Scene, (mesh, vertices) => uploaded[mesh] = vertices.ToArray(), () => { });
        preview.SetClip(animation.ReadClip(prepared.PoseClipTag), play: false);
        Check(preview.Error is null, preview.Error ?? "");
        var grip = preview.Pose.ObjectTransforms[GuardianAssembler.GripBone];
        var palette = preview.SkinFor(weapon[0])!;
        Check(palette.Count > 0 && palette.All(matrix => matrix == grip), "A weapon palette entry is not the grip transform.");
        var points = weapon.Where(i => uploaded.ContainsKey(i.Mesh)).SelectMany(i => uploaded[i.Mesh]).Select(v => v.Position).ToArray();
        float distance = points.Length == 0 ? float.NaN : Vector3.Distance(points.Aggregate(Vector3.Zero, (a, b) => a + b) / points.Length, grip.Translation);
        Console.WriteLine($"guardian_posed skinned_parts={preview.SkinnedPartCount}/{prepared.Scene.Instances.Select(i => i.Mesh).Distinct().Count()} "
            + $"grip=({grip.Translation.X:F2},{grip.Translation.Y:F2},{grip.Translation.Z:F2}) weapon_to_grip={distance:F3}");
        Check(distance < 1, "The posed weapon is not near the grip bone.");
        var clock = Stopwatch.StartNew();
        var plan = NativeScenePlan.Prepare(catalog, prepared.Scene, assetPreview: true);
        Console.WriteLine($"guardian_native prepared={plan.PreparedPartCount}/{prepared.Scene.Instances.Count} ms={clock.ElapsedMilliseconds} "
            + $"skipped={string.Join("; ", plan.SkippedReasons.Select(pair => $"{pair.Value} {pair.Key}"))}");
        foreach (string line in plan.Diagnostics.Where(line => line.Contains("Gear", StringComparison.Ordinal)))
            Console.WriteLine($"  native: {line}");
    }

    /**
     * One weapon of each type on each class, under its hold pose: the right grip is on the weapon, a forward left grip
     * is on it too, the fingers grip, and the body stands unless a WIP note says it crouches. No weapon opens at bind.
     * Merged clip groups place their translation channels on controls whose defaults they equal.
     */
    private static void HoldPoses(GuardianAssembler assembler)
    {
        var catalog = assembler.Catalog;
        var clock = Stopwatch.StartNew();
        var failures = new List<string>();
        var items = InvestmentCatalog.Load(catalog).Items.Where(item => item.Bucket <= 2 && item.Name.Length != 0).ToArray();
        // The first named item of each type, legendary or better when the type has one.
        var picks = items.GroupBy(item => item.TypeName).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Where(item => item.Tier >= 4).OrderBy(item => item.Name, StringComparer.Ordinal).FirstOrDefault()
                ?? group.OrderBy(item => item.Name, StringComparer.Ordinal).First()).ToArray();
        var expected = new Dictionary<string, uint>(StringComparer.Ordinal)
            { ["Auto Rifle"] = HoldClip, ["Pulse Rifle"] = HoldClip, ["Hand Cannon"] = PistolHoldClip, ["Sword"] = PistolHoldClip };
        int poses = 0, crouched = 0, twoHanded = 0;
        foreach (var body in GuardianClasses.All)
        {
            if (assembler.Prepare(body.BodyTag, GuardianLoadout.Default).PoseClipTag != 0)
                failures.Add($"{body.Name} with no weapon does not open at bind");
            foreach (var item in picks)
            {
                string where = $"{body.Name} {item.TypeName} 0x{item.Tag:X8}";
                var prepared = assembler.Prepare(body.BodyTag, GuardianLoadout.Default with { Weapon = item.Tag });
                var notes = prepared.SlotNotes.GetValueOrDefault("Weapon") ?? [];
                if (prepared.PoseClipTag == 0)
                {
                    if (!notes.Any(line => line.Contains("bind pose", StringComparison.Ordinal))) failures.Add($"{where}: no hold pose and no note");
                    continue;
                }
                poses++;
                if (expected.TryGetValue(item.TypeName, out uint clip) && prepared.PoseClipTag != clip)
                    failures.Add($"{where}: hold pose 0x{prepared.PoseClipTag:X8}, not 0x{clip:X8}");
                string? error = Held(prepared, notes, ref crouched, ref twoHanded);
                if (error is not null) failures.Add($"{where}: {error}");
            }
        }
        var (anchors, misses) = MergedGroups(catalog);
        if (anchors == 0 || misses != 0)
            failures.Add($"merged clip groups: {anchors} anchors on mapped controls; the index reading anchors more on {misses} channels");
        Console.WriteLine($"guardian_hold_poses classes={GuardianClasses.All.Count} types={picks.Length} posed={poses} crouched_wip={crouched} "
            + $"two_handed={twoHanded} merged_anchors={anchors} merged_misses={misses} failures={failures.Count} ms={clock.ElapsedMilliseconds}");
        foreach (string failure in failures.Take(40)) Console.WriteLine($"  hold_failure {failure}");
        Check(failures.Count == 0, "A weapon is not held, a hand does not grip, or the body does not stand under its hold pose.");
    }

    /**
     * Measures one guardian under its hold pose.
     * @return The first defect; null when it passes.
     */
    private static string? Held(PreparedGuardian prepared, IReadOnlyList<string> notes, ref int crouched, ref int twoHanded)
    {
        var animation = prepared.Animation!;
        var weapon = prepared.Scene.Instances.Where(i => i.Name.StartsWith("Weapon:", StringComparison.Ordinal)).ToArray();
        var uploaded = new Dictionary<MeshData, RenderVertex[]>();
        using var preview = new AssetAnimationPreview(animation, prepared.Scene, (mesh, vertices) => uploaded[mesh] = vertices.ToArray(), () => { });
        preview.SetClip(animation.ReadClip(prepared.PoseClipTag), play: false);
        if (preview.Error is not null) return $"the hold pose fails: {preview.Error}";
        var rig = animation.Rig;
        var pose = preview.Pose;
        int Bone(string name) => rig.Bones.First(bone => bone.NameHash == StringBankWriter.Hash(name)).Index;
        Vector3 At(int bone) => pose.ObjectTransforms[bone].Translation;
        var points = weapon.Where(i => uploaded.ContainsKey(i.Mesh)).SelectMany(i => uploaded[i.Mesh]).Select(v => v.Position).ToArray();
        if (points.Length == 0) return "no weapon vertex was posed";
        float Nearest(Vector3 point) => points.Min(p => Vector3.Distance(p, point));
        int left = Bone("b_l_grip"), right = Bone("b_r_grip");
        if (right != GuardianAssembler.GripBone) return $"b_r_grip is bone {right}";
        if (Nearest(At(right)) > OnWeapon) return $"the right grip is {Nearest(At(right)):F3} m from the weapon";
        Matrix4x4.Invert(pose.ObjectTransforms[right], out var inverse);
        if (Vector3.Transform(At(left), inverse).X > TwoHanded)
        {
            twoHanded++;
            if (Nearest(At(left)) > OnWeapon) return $"the left grip is {Nearest(At(left)):F3} m from the weapon";
        }
        foreach (string hand in new[] { "b_l_hand", "b_r_hand" })
        {
            int root = Bone(hand);
            float turn = rig.Bones.Where(bone => bone.Index != left && bone.Index != right && Below(rig, bone.Index, root))
                .Select(bone => Angle(pose.LocalTransforms[bone.Index].Rotation, bone.BindLocal.Rotation)).DefaultIfEmpty(0).Max();
            if (turn < GripTurn) return $"the {hand} fingers stay at bind ({turn:F1} degrees)";
        }
        float pelvis = At(Bone("b_pelvis")).Z, foot = MathF.Min(At(Bone("b_l_foot")).Z, At(Bone("b_r_foot")).Z);
        bool noted = notes.Any(line => line.StartsWith("Weapon: crouch pose (WIP)", StringComparison.Ordinal));
        if (noted) crouched++;
        if (noted != pelvis < StandingPelvis) return $"pelvis at {pelvis:F2} m, crouch note {noted}";
        return foot > GroundedFoot ? $"the lower foot is {foot:F2} m off the ground" : null;

        static bool Below(SkeletonRig rig, int bone, int ancestor)
        {
            for (int parent = rig.Bones[bone].ParentIndex; parent >= 0; parent = rig.Bones[parent].ParentIndex)
                if (parent == ancestor) return true;
            return false;
        }

        static float Angle(Quaternion a, Quaternion b) => 2 * MathF.Acos(Math.Clamp(
            MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0, 1)) * 180 / MathF.PI;
    }

    /**
     * Every absolute Titan clip with merged groups. A rigid bone keeps its length, so a channel whose translation over
     * 1 cm equals a control's default within 1 mm anchors that control. Each merged channel must anchor the control
     * ChannelMap gives at least as often as the control of its own index.
     * @return Anchors on the mapped controls, and channels the index reading anchors more often.
     */
    private static (int Anchors, int Misses) MergedGroups(PackageCatalog catalog)
    {
        var source = AssetAnimationSource.Prepare(catalog, GuardianClasses.Titan);
        var binding = source.Binding!;
        var reader = new AnimationControlReader(catalog);
        var counts = new Dictionary<(string Layout, int Channel), (int Mapped, int Index)>();
        foreach (uint tag in source.Clips.Select(clip => clip.ClipTag).Distinct())
        {
            var clip = reader.Read(tag);
            int lead = binding.CompatibleControlCount(clip);
            int[] map = binding.ChannelMap(clip);
            if (clip.PoseMode != 0 || map.Skip(lead).All(control => control < 0)) continue;
            string layout = string.Join(",", clip.ControlGroups.Select(group => $"{group.NameHash:X8}x{group.ControlCount}"));
            foreach (var sample in clip.Sample(0).Where(sample => sample.ControlIndex >= lead && sample.HasTranslation
                && sample.Translation.Length() > 0.01f))
            {
                bool Anchors(int control) => control < binding.Controls.Count
                    && Vector3.Distance(sample.Translation, binding.Controls[control].DefaultValue.Translation) < 0.001f;
                var key = (layout, sample.ControlIndex);
                var (mapped, index) = counts.GetValueOrDefault(key);
                counts[key] = (mapped + (Anchors(map[sample.ControlIndex]) ? 1 : 0), index + (Anchors(sample.ControlIndex) ? 1 : 0));
            }
        }
        return (counts.Values.Sum(value => value.Mapped), counts.Values.Count(value => value.Index > value.Mapped));
    }

    /** Helmet off draws the head parts with their colours; selector 13 of arrangement 37 is entity 0x80BB1136. */
    private static void HelmetOff(GuardianAssembler assembler, string output)
    {
        var parts = assembler.Appearance.Resolve(assembler.Catalog, GuardianClasses.Titan,
            GuardianLoadout.Default with { Helmet = HelmetItem, ShowHelmet = false });
        foreach (var part in parts.Parts.Where(p => p.Kind == GuardianPartKind.Customisation))
            Console.WriteLine($"guardian_head part={part.Label} selector={part.Selector} entity=0x{part.EntityTag:X8} dyes={string.Join(",", part.DyeIndices)}");
        Check(parts.Parts.Any(p => p.Selector == 13 && p.EntityTag == 0x80BB1136), "Selector 13 of the default face is not 0x80BB1136.");
        Check(!parts.Parts.Any(p => p.Label == "Helmet"), "Helmet off still drew the helmet.");
        foreach (var body in GuardianClasses.All)
        {
            var clock = Stopwatch.StartNew();
            var prepared = assembler.Prepare(body.BodyTag, GuardianLoadout.Default with { ShowHelmet = false, RaceGender = 4 });
            Console.WriteLine($"guardian_class class={body.Name} exo_male instances={prepared.Scene.Instances.Count} pose=0x{prepared.PoseClipTag:X8} "
                + $"parts={string.Join(",", prepared.Parts.Parts.Select(p => p.Label))} notes={prepared.Diagnostics.Count} ms={clock.ElapsedMilliseconds}");
            Render(assembler.Catalog, prepared, Path.Combine(output, $"guardian-{body.Name.ToLowerInvariant()}-exo.png"));
        }
        var human = assembler.Prepare(GuardianClasses.Warlock, GuardianLoadout.Default with { ShowHelmet = false, Weapon = WeaponItem });
        Render(assembler.Catalog, human, Path.Combine(output, "guardian-warlock-human.png"));
        var armored = assembler.Prepare(GuardianClasses.Titan, GuardianLoadout.Default with { Helmet = FirstArmor[0], Gauntlets = FirstArmor[1],
            Chest = FirstArmor[2], Legs = FirstArmor[3], ClassArmor = FirstArmor[4], Weapon = WeaponItem });
        Console.WriteLine($"guardian_armored items={string.Join(",", FirstArmor.Select(tag => $"0x{tag:X8}"))} instances={armored.Scene.Instances.Count} "
            + $"notes={string.Join(" | ", armored.Diagnostics)}");
        var armoredPlan = NativeScenePlan.Prepare(assembler.Catalog, armored.Scene, assetPreview: true);
        Console.WriteLine($"guardian_armored_native prepared={armoredPlan.PreparedPartCount}/{armored.Scene.Instances.Count} "
            + $"skipped={string.Join("; ", armoredPlan.SkippedReasons.Select(pair => $"{pair.Value} {pair.Key}"))} "
            + $"reasons={string.Join(" | ", armoredPlan.Diagnostics.Where(line => line.StartsWith("Gear scope", StringComparison.Ordinal)).Select(line => line[(line.LastIndexOf(':') + 2)..]).Distinct())}");
        Check(armoredPlan.SkippedReasons.Count == 0, "The armored guardian plan skips parts.");
        Render(assembler.Catalog, armored, Path.Combine(output, "guardian-titan-armored.png"));
        Render(assembler.Catalog, armored, Path.Combine(output, "guardian-titan-armored-bind.png"), posed: false);
        var clip = armored.Animation!.ReadClip(armored.PoseClipTag);
        Console.WriteLine($"guardian_stance clip=0x{clip.TagHash:X8} seconds={clip.DurationSeconds:F2} pose_mode={clip.PoseMode} pose_space={clip.PoseSpace is not null}");
        var rig = armored.Animation.Rig;
        int pelvis = rig.Bones.First(b => b.Name.StartsWith("b_pelvis", StringComparison.Ordinal)).Index;
        int head = rig.Bones.First(b => b.Name.StartsWith("b_head", StringComparison.Ordinal)).Index;
        var pose = rig.CreateBindPose();
        string Heights() => $"pelvis_z={pose.ObjectTransforms[pelvis].Translation.Z:F2} head_z={pose.ObjectTransforms[head].Translation.Z:F2}";
        Console.WriteLine($"guardian_stance_heights bind {Heights()}");
        foreach (float time in new[] { 0f, clip.DurationSeconds / 2, clip.DurationSeconds })
        {
            armored.Animation.Binding!.ApplyPreview(clip, time, pose);
            Console.WriteLine($"guardian_stance_heights t={time:F2} {Heights()}");
        }
    }

    /**
     * Renders the guardian on WARP with game shaders and saves it.
     * @param focus Bounds to frame instead of the whole scene.
     * @param keep Instances to draw; null draws all.
     * @param back Looks at the guardian's back; the body faces +x.
     * @return The pixels, row by row.
     */
    private static Vector4[] Render(PackageCatalog catalog, PreparedGuardian prepared, string path, bool posed = true,
        (Vector3 Min, Vector3 Max)? focus = null, Func<MeshInstance, bool>? keep = null, bool back = false)
    {
        const int width = 480, height = 640;
        using var renderer = new Dx11Renderer(IntPtr.Zero, width, height, true);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        var scene = prepared.Scene;
        if (keep is not null)
        {
            scene = new SceneDocument { TagHash = prepared.Scene.TagHash, Name = prepared.Scene.Name };
            scene.Instances.AddRange(prepared.Scene.Instances.Where(keep));
        }
        viewport.SetScene(scene);
        viewport.SetAnimation(prepared.Animation!);
        viewport.SetNativeScene(AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: 256L * 1024 * 1024, assetPreview: true));
        if (posed) viewport.Animation!.SetClip(prepared.Animation!.ReadClip(prepared.PoseClipTag != 0 ? prepared.PoseClipTag : HoldClip), false);
        var (min, max) = focus ?? prepared.Scene.GetBounds(includeMarkers: false);
        var centre = (min + max) / 2;
        viewport.Camera.LookFrom(centre + (back ? new Vector3(-3.2f, 0, 0.2f) : new Vector3(0, -3.2f, 0.2f)), centre);
        viewport.Camera.Frame(min, max, (float)width / height);
        for (int frame = 0; frame < 3; frame++) viewport.RenderImage(width, height);
        var points = Enumerable.Range(0, height).SelectMany(y => Enumerable.Range(0, width).Select(x => (x, y))).ToArray();
        var pixels = renderer.ReadTexturePixels(viewport.RenderImage(width, height), points);
        using var bitmap = new Bitmap(width, height);
        for (int i = 0; i < pixels.Length; i++)
        {
            var pixel = Vector4.Clamp(pixels[i], Vector4.Zero, Vector4.One) * 255;
            bitmap.SetPixel(i % width, i / width, Color.FromArgb(255, (int)pixel.X, (int)pixel.Y, (int)pixel.Z));
        }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"guardian_render file={Path.GetFileName(path)} game_shaders={viewport.UseGameShaders} parts={viewport.NativePartCount} "
            + $"error={viewport.NativeError ?? "none"}");
        return pixels;
    }

    /** Instances drawn for the parts that match, found by their label prefix. */
    private static MeshInstance[] InstancesOf(PreparedGuardian prepared, Func<GuardianPart, bool> match)
    {
        var labels = prepared.Parts.Parts.Where(match).Select(part => part.Label + ":").Distinct().ToArray();
        return prepared.Scene.Instances.Where(i => labels.Any(label => i.Name.StartsWith(label, StringComparison.Ordinal))).ToArray();
    }

    /** The item's bucket byte; -1 for a part with no item. */
    private static int Bucket(PackageCatalog catalog, GuardianPart part) =>
        part.ItemTag == 0 ? -1 : catalog.Read(part.ItemTag)[BucketByte];

    // The missing-texture colour: red and blue well above green.
    private static bool Magenta(Vector4 pixel)
    {
        var value = Vector4.Clamp(pixel, Vector4.Zero, Vector4.One) * 255;
        return value.X > 1.6f * value.Y + 20 && value.Z > 1.6f * value.Y + 20;
    }

    private static float Luma(Vector4 pixel) => 0.2126f * pixel.X + 0.7152f * pixel.Y + 0.0722f * pixel.Z;

    private static IEnumerable<int> Bones(VertexSkinWeights value)
    {
        if (value.Weights.X > 0) yield return value.Bone0;
        if (value.Weights.Y > 0) yield return value.Bone1;
        if (value.Weights.Z > 0) yield return value.Bone2;
        if (value.Weights.W > 0) yield return value.Bone3;
    }

    private static (Vector3 Min, Vector3 Max) Bounds(IEnumerable<MeshInstance> instances)
    {
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        foreach (var instance in instances)
            foreach (var vertex in instance.Mesh.Vertices)
            {
                var point = Vector3.Transform(vertex.Position, instance.Transform);
                min = Vector3.Min(min, point); max = Vector3.Max(max, point);
            }
        return (min, max);
    }

    private static uint Fnv(string text, bool alternate)
    {
        uint hash = 0x811C9DC5;
        foreach (char value in text) hash = alternate ? unchecked((hash ^ value) * 0x01000193) : unchecked(hash * 0x01000193) ^ value;
        return hash;
    }

    private static string Tags(IEnumerable<uint> tags) => string.Join(",", tags.Select(tag => $"0x{tag:X8}"));

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
