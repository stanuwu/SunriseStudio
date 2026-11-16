using System.Numerics;
using SunriseStudio.Animation;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using SunriseStudio.World;
using SunriseStudio.World.Entities;

/** Builds a synthetic creature over a Dreg copy and checks that nothing of the Dreg's body remains. */
public static class CombatantCreatureChecks
{
    private const uint Dreg = 0x80C1AF8A;
    private static readonly string[] Points = ["head", "right_hand", "left_hand", "right_foot", "left_foot"];

    /**
     * Runs the synthetic checks, then the same build on a private glb when one is given.
     * @param glb A skinned glb with named actions, one or more skinned meshes; its clip names map as `clips` says.
     * @param glbWork Takes the glb reader's single-mesh copies.
     */
    public static void Run(string gameRoot, string? glb = null, IReadOnlyDictionary<CombatantBehaviour, string>? clips = null,
        CombatantCreatureOptions? glbOptions = null, float glbScale = 1, float glbYawDegrees = 0, string? glbWork = null)
    {
        using var catalog = PackageCatalog.Open(gameRoot);
        var creature = Synthetic();
        var map = new Dictionary<CombatantBehaviour, string>
        {
            [CombatantBehaviour.Idle] = "idle", [CombatantBehaviour.Locomotion] = "idle", [CombatantBehaviour.Melee] = "bite",
            [CombatantBehaviour.Fire] = "bite", [CombatantBehaviour.Flinch] = "hurt", [CombatantBehaviour.Death] = "die",
        };
        var options = new CombatantCreatureOptions { HeadBone = "head", RightHandBone = "foot_fr", LeftHandBone = "foot_fl", RightFootBone = "leg_br", LeftFootBone = "leg_bl", Texture = Checker() };
        Check(catalog, creature, map, options, "synthetic");
        var replaced = options with { KeepDonorBones = false };
        foreach (var variant in new[] { options with { ShareDonorClips = false }, options with { StillMotion = true }, options with { StillClips = true, DonorRootMotion = false }, options with { NeutralLayers = false, DonorRootMotion = false, FollowDonorBones = false, HideWeapon = false, HideDonorCloth = false, ParentPoint = "pedestal" }, replaced, replaced with { PadToDonorBones = false },
            replaced with { KeepCorrections = true }, replaced with { StillClips = true } })
            Check(catalog, creature, map, variant, $"variant keep={variant.KeepDonorBones} pad={variant.PadToDonorBones} neutral={variant.NeutralLayers} corrections={variant.KeepCorrections} still={variant.StillClips} share={variant.ShareDonorClips} still_motion={variant.StillMotion}");
        Check(catalog, CombatantCreatureInput.WithBoxes(creature), map, replaced with { StillClips = true }, "variant boxes keep=False still=True");
        Capsule(catalog, creature, map, options);
        GunScale(catalog, creature, map, options);
        HealthScale(catalog, creature, map, options);
        DisplayName(catalog, creature, map, options);
        NoGrenades(catalog, creature, map, options);
        MeleeOnly(catalog, creature, map, options);
        Projectile(catalog, creature, map, options);
        OneClip(catalog);
        Transform(creature);
        if (glb is not null && clips is not null)
        {
            var source = CombatantCreatureGltf.Read(glb, Path.GetFileNameWithoutExtension(glb).ToLowerInvariant(), clips[CombatantBehaviour.Idle],
                glbWork ?? Path.Combine(Path.GetTempPath(), "combatant-creature-glb"));
            var read = source.Input;
            AtlasSamples(glb, source, glbWork ?? Path.Combine(Path.GetTempPath(), "combatant-creature-glb"),
                Path.GetFileNameWithoutExtension(glb).ToLowerInvariant(), clips[CombatantBehaviour.Idle]);
            var fitted = CombatantCreatureInput.Transformed(read, glbScale, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, glbYawDegrees * MathF.PI / 180));
            Console.WriteLine($"glb meshes={source.Meshes} bones={read.Bones.Count} clips={read.Clips.Count} vertices={read.Geometry.Positions.Length} texture={source.Texture?.Width}x{source.Texture?.Height} height={fitted.Geometry.Positions.Max(p => p.Z):F2}m");
            Check(catalog, fitted, clips, (glbOptions ?? new CombatantCreatureOptions()) with { Texture = source.Texture }, "glb " + Path.GetFileName(glb));
        }
    }

    /**
     * Every vertex samples the same texel from the atlas, read v from the top as the game does, as from its
     * own glb image under the glb's wrap modes. Catches a v flip and clamped mirrored or repeated uvs.
     */
    private static void AtlasSamples(string glb, CombatantCreatureSource source, string work, string name, string plays)
    {
        if (source.Texture is not { } atlas) throw new InvalidDataException("The glb gave no atlas.");
        var json = System.Text.Json.Nodes.JsonNode.Parse(GlbJson(glb))!;
        var images = GltfInterchange.ReadImages(glb).ToDictionary(image => image.Index, image => PngInterchange.Read(image.Bytes));
        var skinned = json["nodes"]!.AsArray().Where(node => node!["mesh"] is not null && node["skin"] is not null).ToArray();
        int vertex = 0, checkedCount = 0, wrong = 0;
        for (int part = 0; part < skinned.Length; part++)
        {
            var piece = RiggedEntityGltf.Read(Path.Combine(work, $"{name}_mesh{part}.glb"), name, plays).Geometry;
            int material = json["meshes"]![skinned[part]!["mesh"]!.GetValue<int>()]!["primitives"]![0]!["material"]!.GetValue<int>();
            var texture = json["textures"]![json["materials"]![material]!["pbrMetallicRoughness"]!["baseColorTexture"]!["index"]!.GetValue<int>()]!;
            var sampler = texture["sampler"] is { } index ? json["samplers"]![index.GetValue<int>()] : null;
            int wrapS = sampler?["wrapS"]?.GetValue<int>() ?? 10497, wrapT = sampler?["wrapT"]?.GetValue<int>() ?? 10497;
            var image = images[texture["source"]!.GetValue<int>()];
            for (int i = 0; i < piece.TexCoords.Length; i++, vertex++)
            {
                // The reader stores v from the bottom; glTF and the game read from the top.
                var expected = Texel(image, Wrap(piece.TexCoords[i].X, wrapS), Wrap(1 - piece.TexCoords[i].Y, wrapT));
                var stored = source.Input.Geometry.TexCoords[vertex];
                var actual = Texel(atlas, stored.X, stored.Y);
                checkedCount++;
                if (Math.Abs(expected.R - actual.R) + Math.Abs(expected.G - actual.G) + Math.Abs(expected.B - actual.B) > 24) wrong++;
            }
        }
        // A vertex on a texel edge may round to its neighbour; a flip or clamp moves far more.
        if (wrong > checkedCount / 50) throw new InvalidDataException($"Atlas samples differ from the glb at {wrong} of {checkedCount} vertices.");
        Console.WriteLine($"atlas samples: {checkedCount - wrong} of {checkedCount} vertices match the glb");
    }

    private static float Wrap(float c, int mode) => mode switch
    {
        33648 => ((int)MathF.Floor(c) & 1) != 0 ? 1 - (c - MathF.Floor(c)) : c - MathF.Floor(c),
        33071 => Math.Clamp(c, 0, 1),
        _ => c - MathF.Floor(c),
    };

    private static (int R, int G, int B) Texel(RgbaImage image, float u, float v)
    {
        int x = Math.Clamp((int)(u * image.Width), 0, image.Width - 1), y = Math.Clamp((int)(v * image.Height), 0, image.Height - 1);
        int at = (y * image.Width + x) * 4;
        return (image.Pixels[at], image.Pixels[at + 1], image.Pixels[at + 2]);
    }

    private static string GlbJson(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        int length = BitConverter.ToInt32(file, 12);
        return System.Text.Encoding.UTF8.GetString(file, 20, length);
    }

    /** A capsule option lands in a copy of the movement config; the stock config is left alone; a capsule too short is refused. */
    private static void Capsule(PackageCatalog catalog, RiggedEntityInput creature, IReadOnlyDictionary<CombatantBehaviour, string> map,
        CombatantCreatureOptions options)
    {
        const uint movement = 0x80806908;
        var built = CombatantCreatureBuilder.Build(catalog, StudioRegistry.Index, Dreg, creature, map, 0x0C01, 0,
            options with { Capsule = new(0.6f, 1.6f, Step: 0.25f) });
        int at = TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition));
        foreach (var tag in built.Tags.Where(tag => tag.Data.Length > 8 && EntityBuilder.DefinitionClass(tag.Data) == movement))
        {
            int definition = at + (int)BitConverter.ToInt64(tag.Data, at);
            Require(BitConverter.ToSingle(tag.Data, definition + 0x1BC) == 0.35f && BitConverter.ToSingle(tag.Data, definition + 0x1B0) == 2.0f,
                "capsule: the movement config changed; it must keep the donor's nav radius.");
        }
        // The standing body in the packfile copy: radius 0.6, A (0,0,0.85,0.6), B (0,0,1.0,0.6).
        var bodies = built.Assets.Where(asset => asset.BaseTagHash is not null && Floats(asset.Data, [0.6f, 0, 0, 0, 0, 0, 0.85f, 0.6f, 0, 0, 1.0f, 0.6f])).ToArray();
        Require(bodies.Length == 1, $"capsule: {bodies.Length} assets hold the new standing body, not 1.");
        uint stockPack = bodies[0].BaseTagHash!.Value, packTag = TagHashes.Make(bodies[0].Native!.PackageId, bodies[0].Native!.EntryIndex!.Value);
        Require(Floats(catalog.Read(stockPack), [0.35f, 0, 0, 0, 0, 0, 0.65f, 0.35f, 0, 0, 1.65f, 0.35f]), "capsule: the stock packfile does not hold the donor body.");
        bool Names(byte[] data, uint tag) => Enumerable.Range(0, data.Length / 4).Any(i => BitConverter.ToUInt32(data, i * 4) == tag);
        Require(built.Tags.Count(tag => Names(tag.Data, packTag)) == 1 && !built.Tags.Any(tag => Names(tag.Data, stockPack)),
            "capsule: the copied physics config does not name the new packfile alone.");
        try
        {
            CombatantCreatureBuilder.Build(catalog, StudioRegistry.Index, Dreg, creature, map, 0x0C01, 0, options with { Capsule = new(1.0f, 1.6f) });
            throw new InvalidDataException("capsule: a capsule shorter than step + 2 * radius was accepted.");
        }
        catch (ArgumentException) { }
        Console.WriteLine($"PASS combatant-creature-capsule movement=unchanged packfile={packTag:X8} from={stockPack:X8} radius=0.6 height=1.6 step=0.25 crouch=1.5");
    }

    /** True when the floats appear in a row at any 4-byte step, within 1e-4. */
    private static bool Floats(byte[] bytes, float[] run)
    {
        for (int at = 0; at + run.Length * 4 <= bytes.Length; at += 4)
            if (run.Select((value, i) => MathF.Abs(BitConverter.ToSingle(bytes, at + i * 4) - value) < 1e-4f).All(match => match)) return true;
        return false;
    }

    /** A gun scale lands on a copy of each loadout gun as stock value times the factor; the stock gun is left alone. */
    private static void GunScale(PackageCatalog catalog, RiggedEntityInput creature, IReadOnlyDictionary<CombatantBehaviour, string> map,
        CombatantCreatureOptions options)
    {
        const string stat = "damage_multiplier_base";
        var built = CombatantCreatureBuilder.Build(catalog, StudioRegistry.Index, Dreg, creature, map, 0x0C01, 0,
            options with { GunScale = new Dictionary<string, float> { [stat] = 0.25f } });
        var guns = CombatantWeaponCopy.Plan(catalog, Dreg).Chains.Select(chain => chain.Gun).Distinct().ToArray();
        Require(guns.Length > 0, "gun scale: the Dreg has no loadout gun.");
        foreach (uint gun in guns)
        {
            float stock = CombatantGun.Read(gun, catalog.Read(gun))!.Stat(stat);
            var copy = built.Tags.SingleOrDefault(tag => tag.Source == gun) ?? throw new InvalidDataException($"gun scale: no copy of gun 0x{gun:X8}.");
            float scaled = CombatantGun.Read(copy.Tag, copy.Data)!.Stat(stat);
            Require(MathF.Abs(scaled - stock * 0.25f) < 1e-4f, $"gun scale: gun 0x{gun:X8} holds {scaled}, not {stock} * 0.25.");
            Console.WriteLine($"PASS combatant-creature-gun-scale gun={gun:X8} stock={stock} scaled={scaled}");
        }
    }

    /** A health scale lands on a copy of each damage config as stock hit points times the factor; a factor of 0 is refused. */
    private static void HealthScale(PackageCatalog catalog, RiggedEntityInput creature, IReadOnlyDictionary<CombatantBehaviour, string> map,
        CombatantCreatureOptions options)
    {
        var built = CombatantCreatureBuilder.Build(catalog, StudioRegistry.Index, Dreg, creature, map, 0x0C01, 0, options with { HealthScale = 0.5f });
        int seen = 0;
        foreach (var copy in built.Tags.Where(tag => tag.Source != 0 && CombatantDamage.Read(tag.Source, catalog.Read(tag.Source)) is not null))
        {
            float stock = CombatantDamage.Read(copy.Source, catalog.Read(copy.Source))!.BaseHitPoints;
            float scaled = CombatantDamage.Read(copy.Tag, copy.Data)!.BaseHitPoints;
            Require(MathF.Abs(scaled - stock * 0.5f) < 1e-3f, $"health scale: config 0x{copy.Source:X8} holds {scaled}, not {stock} * 0.5.");
            Console.WriteLine($"PASS combatant-creature-health-scale config={copy.Source:X8} stock={stock} scaled={scaled}");
            seen++;
        }
        Require(seen > 0, "health scale: the copy has no damage config.");
        try
        {
            CombatantCreatureBuilder.Build(catalog, StudioRegistry.Index, Dreg, creature, map, 0x0C01, 0, options with { HealthScale = 0 });
            throw new InvalidDataException("health scale: a factor of 0 was accepted.");
        }
        catch (ArgumentOutOfRangeException) { }
    }

    /** LoadClosure with the built tags and assets overlaid on the catalog, as Check reads them. */
    private static void Closure(PackageCatalog catalog, CombatantCreature built, string label)
    {
        var data = built.Tags.ToDictionary(tag => tag.Tag, tag => tag.Data);
        var entries = built.Tags.Select(tag => new PackageOverlayEntry(new PackageEntry(tag.Tag, tag.Class, (int)(tag.TypeInfo >> 9 & 127),
            (int)(tag.TypeInfo >> 6 & 7), tag.Data.Length, "w64_sunrise_0ffe_0", EntityBuilder.ScratchPackage, TagHashes.Split(tag.Tag).EntryIndex)
        { TypeInfo = tag.TypeInfo }, null)).ToList();
        foreach (var asset in built.Assets)
        {
            var native = asset.Native!;
            uint tag = TagHashes.Make(native.PackageId, native.EntryIndex!.Value);
            data[tag] = asset.Data;
            entries.Add(new(new PackageEntry(tag, native.Reference, (int)(native.TypeInfo >> 9 & 127), (int)(native.TypeInfo >> 6 & 7),
                asset.Data.Length, $"w64_sunrise_{native.PackageId:x4}_0", native.PackageId, native.EntryIndex.Value) { TypeInfo = native.TypeInfo }, native.Name));
        }
        catalog.SetOverlayEntries(entries);
        catalog.ReadOverride = tag => data.GetValueOrDefault(tag);
        try { LoadClosure(catalog, built, label); }
        finally { catalog.ReadOverride = null; catalog.SetOverlayEntries([]); }
    }

    // The Dreg's actor definition, which holds its name hash and its ability groups.
    private const uint DregDefinition = 0x80FEF6B6;
    // A melee-only enemy: its loadout names no weapon.
    private const uint Thrall = 0x80BF8A1D;
    // The Servitor's shot, a slow orb unlike the Dreg's bolt.
    private const uint ServitorShot = 0x80C1C197;

    /**
     * A display name lands as its hash in a copy of the actor definition, and the name bank rewrite keeps every
     * installed row, empty ones too, adds the name, and passes the template checks again.
     */
    private static void DisplayName(PackageCatalog catalog, RiggedEntityInput creature, IReadOnlyDictionary<CombatantBehaviour, string> map,
        CombatantCreatureOptions options)
    {
        const string name = "Hound";
        var built = CombatantCreatureBuilder.Build(catalog, StudioRegistry.Index, Dreg, creature, map, 0x0C01, 0, options with { DisplayName = name });
        var copy = built.Tags.Single(tag => tag.Source == DregDefinition);
        Require(BitConverter.ToUInt32(copy.Data, 8) == CombatantNameBank.Hash(name), "display name: the definition copy does not hold the name hash.");
        Require(BitConverter.ToUInt32(catalog.Read(DregDefinition), 8) == 0x6CBB5C93, "display name: the stock definition changed.");
        Closure(catalog, built, "display-name");
        try
        {
            CombatantCreatureBuilder.Build(catalog, StudioRegistry.Index, Dreg, creature, map, 0x0C01, 0, options with { DisplayName = name, DisplayNameHash = 1 });
            throw new InvalidDataException("display name: a name and a hash together were accepted.");
        }
        catch (ArgumentException) { }

        byte[] container = catalog.Read(CombatantNameBank.Bank);
        uint dataTag = BitConverter.ToUInt32(container, TagView.FieldOffset<SLocalizedStrings>(nameof(SLocalizedStrings.LanguageData)));
        var stock = StringBankWriter.Read(container, catalog.Read(dataTag));
        var edits = CombatantNameBank.WithNames(catalog, catalog.Read, [name, "Badger"]);
        Require(edits.Count == 2 && edits.ContainsKey(CombatantNameBank.Bank) && edits.ContainsKey(dataTag), "display name: the bank edit is not its container and English data.");
        var written = StringBankWriter.Read(edits[CombatantNameBank.Bank], edits[dataTag]);
        var byHash = written.ToDictionary(row => row.Hash, row => row.Text);
        Require(written.Count == stock.Count + 2 && stock.All(row => byHash.TryGetValue(row.Hash, out string? text) && text == row.Text),
            "display name: the rewritten bank lost or changed an installed row.");
        Require(byHash[CombatantNameBank.Hash(name)] == name && byHash[0x6CBB5C93] == "Dreg", "display name: the new or the Dreg name does not read back.");
        Require(written.Zip(written.Skip(1)).All(pair => pair.First.Hash < pair.Second.Hash), "display name: the rewritten bank is not sorted by hash.");
        StringBankTemplate.Read(CombatantNameBank.Bank, edits[CombatantNameBank.Bank], dataTag, edits[dataTag], _ => true);
        // A second name joins the first; a name already present changes nothing.
        var again = CombatantNameBank.WithNames(catalog, tag => edits.GetValueOrDefault(tag) ?? catalog.Read(tag), [name, "Weasel"]);
        Require(StringBankWriter.Read(again[CombatantNameBank.Bank], again[dataTag]).Count == stock.Count + 3, "display name: a second edit dropped the first.");
        Require(CombatantNameBank.WithNames(catalog, tag => edits.GetValueOrDefault(tag) ?? catalog.Read(tag), [name]).Count == 0,
            "display name: a name already in the bank was written again.");
        int empty = stock.Count(row => row.Text.Length == 0);
        Console.WriteLine($"PASS combatant-creature-display-name hash={CombatantNameBank.Hash(name):X8} bank={CombatantNameBank.Bank:X8} rows={stock.Count}+2 empty_rows={empty} sorted=yes template=reread");
    }

    /** Grenades off drops the grenade group from the definition copy and keeps every other group and key. */
    private static void NoGrenades(PackageCatalog catalog, RiggedEntityInput creature, IReadOnlyDictionary<CombatantBehaviour, string> map,
        CombatantCreatureOptions options)
    {
        var built = CombatantCreatureBuilder.Build(catalog, StudioRegistry.Index, Dreg, creature, map, 0x0C01, 0, options with { Grenades = false });
        var stock = CombatantAbilityDefinition.Read(catalog.Read(DregDefinition))!;
        var copy = CombatantAbilityDefinition.Read(built.Tags.Single(tag => tag.Source == DregDefinition).Data)
            ?? throw new InvalidDataException("no grenades: the definition copy does not reread.");
        Require(stock.Groups.Any(group => group.GroupHash == CombatantAbilityDefinition.GrenadeGroup), "no grenades: the stock Dreg has no grenade group.");
        var kept = stock.Groups.Where(group => group.GroupHash != CombatantAbilityDefinition.GrenadeGroup).ToArray();
        Require(copy.Groups.Count == kept.Length && copy.Groups.Zip(kept).All(pair => pair.First.GroupHash == pair.Second.GroupHash
            && pair.First.Requests.Select(row => row.Key).SequenceEqual(pair.Second.Requests.Select(row => row.Key))),
            "no grenades: a kept group or its keys changed.");
        Require(copy.Sequences.Select(row => row.Resource).SequenceEqual(stock.Sequences.Select(row => row.Resource)), "no grenades: the sequence rows changed.");
        // The first group moved down: removing it checks the key array link fix.
        byte[] first = CombatantAbilityDefinition.WithoutGroup(catalog.Read(DregDefinition), stock.Groups[0].GroupHash);
        var moved = CombatantAbilityDefinition.Read(first)!;
        Require(moved.Groups.Select(group => group.GroupHash).SequenceEqual(stock.Groups.Skip(1).Select(group => group.GroupHash))
            && moved.Groups.Zip(stock.Groups.Skip(1)).All(pair => pair.First.Requests.Select(row => row.Key).SequenceEqual(pair.Second.Requests.Select(row => row.Key))),
            "no grenades: removing the first group broke a later group's keys.");
        Closure(catalog, built, "no-grenades");
        Console.WriteLine($"PASS combatant-creature-no-grenades groups={stock.Groups.Count}->{copy.Groups.Count} kept=[{string.Join(" ", copy.Groups.Select(group => $"{group.GroupHash:X8}:{group.Requests.Count}"))}]");
    }

    /** Ranged off empties a copy of the loadout and copies no gun; a gun option with it is refused; a Thrall builds as a donor. */
    private static void MeleeOnly(PackageCatalog catalog, RiggedEntityInput creature, IReadOnlyDictionary<CombatantBehaviour, string> map,
        CombatantCreatureOptions options)
    {
        var built = CombatantCreatureBuilder.Build(catalog, StudioRegistry.Index, Dreg, creature, map, 0x0C01, 0, options with { Ranged = false, Grenades = false });
        var loadouts = built.Tags.Where(tag => CombatantLoadout.Read(tag.Tag, tag.Data) is not null).ToArray();
        Require(loadouts.Length == 1 && loadouts[0].Source != 0, "melee: the copy has no copied loadout.");
        var loadout = CombatantLoadout.Read(loadouts[0].Tag, loadouts[0].Data)!;
        Require(loadout.Groups.Count == 0 && loadout.Fallback.Weapons.Count == 0, "melee: the copied loadout still names a weapon.");
        Require(CombatantLoadout.Read(loadouts[0].Source, catalog.Read(loadouts[0].Source))!.Groups.Count == 3, "melee: the stock loadout changed.");
        var guns = CombatantWeaponCopy.Plan(catalog, Dreg).Chains.Select(chain => chain.Gun).ToHashSet();
        Require(!built.Tags.Any(tag => guns.Contains(tag.Source)), "melee: a gun was copied.");
        Closure(catalog, built, "melee");
        try
        {
            CombatantCreatureBuilder.Build(catalog, StudioRegistry.Index, Dreg, creature, map, 0x0C01, 0,
                options with { Ranged = false, GunScale = new Dictionary<string, float> { ["damage_multiplier_base"] = 2 } });
            throw new InvalidDataException("melee: a gun scale without a gun was accepted.");
        }
        catch (ArgumentException) { }
        var thrall = CombatantLoadout.Read(EntityBuilder.Components(catalog.Read(Thrall)).Single(tag => CombatantLoadout.Read(tag, catalog.Read(tag)) is not null),
            catalog.Read(EntityBuilder.Components(catalog.Read(Thrall)).Single(tag => CombatantLoadout.Read(tag, catalog.Read(tag)) is not null)))!;
        Require(thrall.Groups.Count == 0 && thrall.Fallback.Weapons.Count == 0, "melee: the Thrall's loadout names a weapon.");
        var overThrall = CombatantCreatureBuilder.Build(catalog, StudioRegistry.Index, Thrall, creature, map, 0x0C01, 0, options with { Grenades = false });
        Closure(catalog, overThrall, "thrall");
        Console.WriteLine($"PASS combatant-creature-melee loadout={loadouts[0].Source:X8} groups=3->0 guns_copied=0 thrall_loadout=empty thrall_build=ok slots={overThrall.Slots.Count}");
    }

    /** A projectile swap points every copied gun slot at a copy of the new shot, sets its speed, and drops the old shot. */
    private static void Projectile(PackageCatalog catalog, RiggedEntityInput creature, IReadOnlyDictionary<CombatantBehaviour, string> map,
        CombatantCreatureOptions options)
    {
        var built = CombatantCreatureBuilder.Build(catalog, StudioRegistry.Index, Dreg, creature, map, 0x0C01, 0,
            options with { Projectile = ServitorShot, ProjectileValues = new Dictionary<string, float> { ["speed"] = 12 } });
        var stock = CombatantWeaponCopy.Plan(catalog, Dreg);
        var shot = built.Tags.SingleOrDefault(tag => tag.Source == ServitorShot) ?? throw new InvalidDataException("projectile: the new shot was not copied.");
        var oldShots = stock.Chains.Select(chain => chain.Projectile).ToHashSet();
        Require(!built.Tags.Any(tag => oldShots.Contains(tag.Source)), "projectile: the Dreg's own shot is still copied.");
        int slots = 0;
        foreach (uint gun in stock.Chains.Select(chain => chain.Gun).Distinct())
        {
            var copy = built.Tags.Single(tag => tag.Source == gun);
            var read = CombatantGun.Read(copy.Tag, copy.Data)!;
            var filled = read.Projectiles.Where(slot => slot.Entity is not (0 or uint.MaxValue)).ToArray();
            Require(filled.Length > 0 && filled.All(slot => slot.Entity == shot.Tag), $"projectile: gun 0x{gun:X8} names a shot other than the copy.");
            slots += filled.Length;
        }
        var configs = EntityBuilder.Components(catalog.Read(ServitorShot)).Where(tag => CombatantProjectile.Read(tag, catalog.Read(tag)) is not null).ToArray();
        foreach (uint config in configs)
        {
            var copy = built.Tags.SingleOrDefault(tag => tag.Source == config) ?? throw new InvalidDataException($"projectile: config 0x{config:X8} was not copied.");
            Require(CombatantProjectile.Read(copy.Tag, copy.Data)!.Speed == 12, "projectile: the copied shot's speed is not 12.");
            Require(Words(shot.Data).Contains(copy.Tag) && !Words(shot.Data).Contains(config), "projectile: the shot copy does not name its config copy.");
        }
        Closure(catalog, built, "projectile");
        Console.WriteLine($"PASS combatant-creature-projectile shot={ServitorShot:X8}->{shot.Tag:X8} slots={slots} configs={configs.Length} speed={CombatantProjectile.Read(configs[0], catalog.Read(configs[0]))!.Speed}->12");
    }

    /** Builds only the glb creature and runs its checks, the atlas samples among them. */
    public static void RunGlb(string gameRoot, string glb, IReadOnlyDictionary<CombatantBehaviour, string> clips, float scale, float yawDegrees)
    {
        using var catalog = PackageCatalog.Open(gameRoot);
        string work = Path.Combine(Path.GetTempPath(), "combatant-creature-glb");
        string name = Path.GetFileNameWithoutExtension(glb).ToLowerInvariant();
        var source = CombatantCreatureGltf.Read(glb, name, clips[CombatantBehaviour.Idle], work);
        AtlasSamples(glb, source, work, name, clips[CombatantBehaviour.Idle]);
        var fitted = CombatantCreatureInput.Transformed(source.Input, scale, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yawDegrees * MathF.PI / 180));
        Check(catalog, fitted, clips, new CombatantCreatureOptions { Texture = source.Texture }, "glb " + Path.GetFileName(glb));
    }

    /** Builds one creature and checks the copy's tags, then reads it back through an overlay. */
    private static void Check(PackageCatalog catalog, RiggedEntityInput creature, IReadOnlyDictionary<CombatantBehaviour, string> map,
        CombatantCreatureOptions options, string label)
    {
        ushort package = Enumerable.Range(0x100, 0xC00).Select(value => (ushort)value).First(id => !catalog.ContainsInstalledPackage(id));
        var built = CombatantCreatureBuilder.Build(catalog, StudioRegistry.Index, Dreg, creature, map, package, 0, options);
        // The donor copied with nothing replaced, for the superset's byte comparison; overlays hide scratch tags from Clone.
        var stockCopy = EntityBuilder.Clone(catalog, Dreg, [0x80FEF6B7u]);
        var donorSkeleton = new SkeletonReader(catalog).Read(Dreg);
        var donorSlots = CombatantAnimationSlots.Read(catalog, Dreg)!;
        var donorClips = CombatantCreatureBuilder.DefinitionSlots(catalog.Read(donorSlots.DefinitionTag)).ToHashSet();
        var creatureClips = built.ClipTags.Values.ToHashSet();
        var copies = built.Tags.Select(tag => tag.Tag).ToHashSet();
        var donorModels = donorSkeleton.ModelTags.ToHashSet();
        var banned = donorClips.Concat(donorModels).Concat(EntityBuilder.Components(catalog.Read(Dreg))).Append(Dreg).Append(donorSlots.DefinitionTag).ToHashSet();

        // No copy names a Dreg clip, the Dreg's model, or a Dreg config the copy replaced.
        var kept = new Dictionary<uint, uint>();
        foreach (var tag in built.Tags)
            foreach (uint word in Words(tag.Data))
            {
                if (copies.Contains(word) || creatureClips.Contains(word)) continue;
                Require(!banned.Contains(word), $"{label}: copy {tag.Tag:X8} still names Dreg tag {word:X8}.");
                // Installed tags that bind to the old body: the cape, physics shapes and ragdoll, the retarget table.
                if (catalog.TryGetEntry(word, out var entry) && entry.Reference is 0x8080727A or 0x808073A5 or 0x80808BCB or 0x80808BCD
                    or 0x80809071 or 0x808036D1 or 0xFFFFFFFF)
                    kept.TryAdd(word, entry.Reference);
            }

        var definition = built.Tags.Single(tag => tag.Class == SAnimationDefinition.Class);
        var slots = CombatantCreatureBuilder.DefinitionSlots(definition.Data);
        Require(slots.Length == donorSlots.ClipSlotCount && slots.All(creatureClips.Contains),
            $"{label}: a definition slot does not name a creature clip.");
        Fields(catalog, built, donorSlots, label);
        Compact(catalog, built, options, label);

        var data = built.Tags.ToDictionary(tag => tag.Tag, tag => tag.Data);
        var entries = built.Tags.Select(tag => new PackageOverlayEntry(new PackageEntry(tag.Tag, tag.Class, (int)(tag.TypeInfo >> 9 & 127),
            (int)(tag.TypeInfo >> 6 & 7), tag.Data.Length, "w64_sunrise_0ffe_0", EntityBuilder.ScratchPackage, TagHashes.Split(tag.Tag).EntryIndex)
        { TypeInfo = tag.TypeInfo }, null)).ToList();
        foreach (var asset in built.Assets)
        {
            var native = asset.Native!;
            uint tag = TagHashes.Make(native.PackageId, native.EntryIndex!.Value);
            data[tag] = asset.Data;
            entries.Add(new(new PackageEntry(tag, native.Reference, (int)(native.TypeInfo >> 9 & 127), (int)(native.TypeInfo >> 6 & 7),
                asset.Data.Length, $"w64_sunrise_{native.PackageId:x4}_0", native.PackageId, native.EntryIndex.Value) { TypeInfo = native.TypeInfo }, native.Name));
        }
        catalog.SetOverlayEntries(entries);
        catalog.ReadOverride = tag => data.GetValueOrDefault(tag);
        try
        {
            uint entity = built.Tags[0].Tag;
            var skeleton = new SkeletonReader(catalog).Read(entity);
            Require(skeleton.Bones.Count == built.BoneCount && skeleton.ModelTags.Contains(built.ModelTag), $"{label}: the skeleton or its model link does not reread.");
            var model = TagView.Checked<SEntityModel>(data[built.ModelTag], 0);
            // The creature's own model config is the one naming its model; hidden weapon model configs are copies too.
            var modelConfig = built.Tags.Single(tag => EntityBuilder.DefinitionClass(tag.Data) == SModelComponent.Class && Words(tag.Data).Contains(built.ModelTag)).Data;
            int primary = TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.DefaultInstance));
            primary += (int)BitConverter.ToInt64(modelConfig, primary);
            uint consumed = BitConverter.ToUInt32(modelConfig, primary + TagView.FieldOffset<SModelComponentPrimary>(nameof(SModelComponentPrimary.ConsumedBoneCount)));
            Require(model.AuthoredBoneCount == built.BoneCount && consumed == built.BoneCount, $"{label}: model bones {model.AuthoredBoneCount}, config {consumed}, skeleton {built.BoneCount}.");
            var binding = new AnimationRigReader(catalog).Read(skeleton);
            if (built.DonorBoneCount == 0)
                Require(binding.Controls.Count == built.BoneCount && binding.ControlGroups.Count == 1 && binding.IkControlIndices.Count == 0
                    && binding.BoneCorrectionCount == (options.KeepCorrections ? 2 : 0), $"{label}: the rig is not one control per bone.");
            else Superset(catalog, built, skeleton, binding, data, stockCopy, label);
            var links = new EntityAnimationCatalog(catalog).Read(entity);
            Require(links.Count == slots.Length && links.All(link => creatureClips.Contains(link.ClipTag)),
                $"{label}: the animation component links {links.Count} clips, {links.Count(link => !creatureClips.Contains(link.ClipTag))} not the creature's.");
            var reader = new AnimationControlReader(catalog);
            var pose = skeleton.CreateBindPose();
            foreach (uint clipTag in creatureClips)
            {
                var clip = reader.Read(clipTag);
                // A shared clip carries the donor's groups alone and leaves the creature's controls to the rig.
                int width = built.SharedClips.ContainsKey(clipTag) ? binding.Controls.Count - built.CreatureBoneCount : binding.Controls.Count;
                Require(clip.ControlCount == width && binding.CompatibleControlCount(clip) == clip.ControlCount
                    && binding.TranslationScale(clip) == 1, $"{label}: clip {clipTag:X8} does not fit the rig.");
                if (clip.PoseMode != 0) continue;
                try { binding.Apply(clip, clip.DurationSeconds / 2, pose); }
                catch (InvalidDataException error) { throw new InvalidDataException($"{label}: clip {clipTag:X8} mode {clip.PoseMode} linked={links.Any(link => link.ClipTag == clipTag)}: {error.Message}"); }
            }
            PosedBounds(catalog, entity, skeleton, binding, built.SlotsByClip, built.ClipTags, creature, label);
            Shared(catalog, built, skeleton, binding, options, label);
            LoadClosure(catalog, built, label);
            if (options.OwnTexture && options.Texture is { } image) OwnTexture(catalog, built, image, options.FlatSlots, label);
            NoForeignTags(catalog, built, label);
            var table = CombatantAnimationRig.NameTable(data[binding.ResourceTag]).ToDictionary(row => row.Name, row => row.Bone);
            foreach (string point in Points)
                Require(table.TryGetValue(AnimationDefinitionWriter.NameHash(point), out int bone) && bone < Math.Max(built.CreatureBoneCount, built.DonorBoneCount),
                    $"{label}: point {point} does not resolve.");
            var set = CombatantAnimationSlots.Read(catalog, entity)!;
            Require(set.Queries.Count == donorSlots.Queries.Count && set.NameRowCount == donorSlots.NameRowCount
                && set.Queries.SelectMany(q => q.Rows).Concat(set.OtherRows).All(row => row.ClipSlot < 0 || creatureClips.Contains(row.ClipTag)),
                $"{label}: a name row reads back to a clip that is not the creature's.");
            var flinch = set.Queries.Single(q => q.Name == "flinch").Rows.Select(row => row.ClipTag).Distinct().ToArray();
            Console.WriteLine($"PASS combatant-creature {label} bones={built.BoneCount} creature={built.CreatureBoneCount} model_bones={model.AuthoredBoneCount} "
                + $"controls={binding.Controls.Count} clips={creatureClips.Count} slots={slots.Length} keys={set.Queries.Count} "
                + $"plan=[{string.Join(" ", built.SlotsByClip.Where(pair => !pair.Key.StartsWith(CombatantCreatureBuilder.SpacePrefix, StringComparison.Ordinal)).Select(pair => $"{pair.Key}:{pair.Value}"))} pose_space:{built.SlotsByClip.Where(pair => pair.Key.StartsWith(CombatantCreatureBuilder.SpacePrefix, StringComparison.Ordinal)).Sum(pair => pair.Value)}/{built.SlotsByClip.Count(pair => pair.Key.StartsWith(CombatantCreatureBuilder.SpacePrefix, StringComparison.Ordinal))}] flinch={string.Join(",", flinch.Select(tag => tag.ToString("X8")))} "
                + $"points={string.Join(",", Points.Select(point => $"{point}->{table[AnimationDefinitionWriter.NameHash(point)]}"))}");
            Console.WriteLine("  slots " + string.Join(" ", built.Slots.GroupBy(row => (row.Behaviour, row.DonorPoseMode, row.DonorPoseSpace, row.Clip))
                .OrderBy(group => group.Key.Behaviour).Select(group => $"{group.Key.Behaviour}/m{group.Key.DonorPoseMode}{(group.Key.DonorPoseSpace ? "s" : "")}->{group.Key.Clip}:{group.Count()}")));
            Console.WriteLine($"  kept installed tags {kept.Count}: {string.Join(" ", kept.OrderBy(pair => pair.Value).Select(pair => $"{pair.Key:X8}/{pair.Value:X8}"))}");
        }
        finally { catalog.ReadOverride = null; catalog.SetOverlayEntries([]); }
    }

    /**
     * The built model, read back and skinned by the built skeleton with the idle clip, stands where the input does:
     * the bind bounds match the input geometry and every posed vertex is finite and near them.
     */
    private static void PosedBounds(PackageCatalog catalog, uint entity, SkeletonRig skeleton, AnimationRigBinding binding,
        IReadOnlyDictionary<string, int> slotsByClip, IReadOnlyDictionary<string, uint> clipTags, RiggedEntityInput creature, string label)
    {
        var scene = new SunriseStudio.Scene.AssetSceneLoader(catalog).Load(entity);
        var instance = scene.Instances.Single(value => skeleton.ModelTags.Contains(value.Mesh.TagHash) && value.Mesh.LodLevel == 0
            && value.ModelSourceResourceClass == SModelComponent.Class);
        var mesh = instance.Mesh;
        var skin = new ModelSkinReader(catalog).Read(mesh.TagHash, skeleton).Single(value => value.PartOffsets.Contains(mesh.PartIndex));
        var input = (creature.Geometry.Positions.Aggregate(Vector3.Min), creature.Geometry.Positions.Aggregate(Vector3.Max));
        (Vector3 Min, Vector3 Max) Box(IEnumerable<Vector3> points)
        {
            var list = points.ToArray();
            Require(list.All(point => float.IsFinite(point.X + point.Y + point.Z)), $"{label}: a skinned vertex is not finite.");
            return (list.Aggregate(Vector3.Min), list.Aggregate(Vector3.Max));
        }
        var bind = Box(mesh.Vertices.Select(vertex => Vector3.Transform(vertex.Position, instance.Transform)));
        Require(Vector3.Distance(bind.Min, input.Item1) < 0.01f && Vector3.Distance(bind.Max, input.Item2) < 0.01f,
            $"{label}: the model's bind box {bind} is not the input's {input}.");
        // The clip that plays most slots stands in for idle.
        string idle = slotsByClip.Where(pair => !pair.Key.StartsWith(CombatantCreatureBuilder.SpacePrefix, StringComparison.Ordinal)
            && pair.Key != CombatantCreatureBuilder.NeutralClip).MaxBy(pair => pair.Value).Key;
        var clip = new AnimationControlReader(catalog).Read(clipTags[idle]);
        var pose = skeleton.CreateBindPose();
        var size = bind.Max - bind.Min;
        foreach (float seconds in new[] { 0f, clip.DurationSeconds / 2 })
        {
            binding.Apply(clip, seconds, pose);
            var posed = Box(mesh.Vertices.Select((vertex, index) => Vector3.Transform(pose.SkinPosition(vertex.Position,
                skin.Vertices[mesh.SourceVertexIndices.Length > index ? (int)mesh.SourceVertexIndices[index] : index]), instance.Transform)));
            var posedSize = posed.Max - posed.Min;
            Require(Vector3.Distance(posed.Min, bind.Min) < size.Length() && Vector3.Distance(posed.Max, bind.Max) < size.Length()
                && posedSize.Length() > size.Length() / 4, $"{label}: posed box {posed} is far from the bind box {bind}.");
            Console.WriteLine($"PASS combatant-creature-posed-bounds {label} clip={idle} t={seconds:F2} bind=({bind.Min.X:F2},{bind.Min.Y:F2},{bind.Min.Z:F2})..({bind.Max.X:F2},{bind.Max.Y:F2},{bind.Max.Z:F2}) "
                + $"posed=({posed.Min.X:F2},{posed.Min.Y:F2},{posed.Min.Z:F2})..({posed.Max.X:F2},{posed.Max.Y:F2},{posed.Max.Z:F2})");
        }
    }

    /**
     * Every pointer or array field that is set in all Dreg clips of a slot's kind is set in the creature clip of that
     * slot; kind is the pose mode and whether a pose space is present. The same holds field by field for the
     * definition, the skeleton and the rig against the Dreg's own, except the rig's limb solve list.
     */
    private static void Fields(PackageCatalog catalog, CombatantCreature built, CombatantAnimationSet donor, string label)
    {
        var clipFields = CombatantAnimationPoseSpace.PointerFields(typeof(SAnimationClip));
        int modeField = TagView.FieldOffset<SAnimationClip>(nameof(SAnimationClip.PoseMode));
        int spaceField = TagView.FieldOffset<SAnimationClip>("H42cf5d81.PayloadSection8");
        static bool Set(byte[] tag, int at) => BitConverter.ToInt64(tag, at) != 0;
        (int, bool) Kind(byte[] clip) => (clip[modeField], Set(clip, spaceField));
        uint[] stock = CombatantCreatureBuilder.DefinitionSlots(catalog.Read(donor.DefinitionTag));
        var stockBytes = stock.Distinct().ToDictionary(tag => tag, catalog.Read);
        var always = stockBytes.Values.GroupBy(Kind).ToDictionary(group => group.Key,
            group => clipFields.Where(field => group.All(clip => Set(clip, field.Offset))).ToArray());
        var ours = built.Tags.ToDictionary(tag => tag.Tag, tag => tag.Data);
        uint[] slots = CombatantCreatureBuilder.DefinitionSlots(built.Tags.Single(tag => tag.Class == SAnimationDefinition.Class).Data);
        // A field is excused for a kind only when installed clips of that kind leave it empty too.
        census ??= Census(catalog, clipFields, Kind);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var waived = new SortedSet<string>(StringComparer.Ordinal);
        for (int slot = 0; slot < slots.Length; slot++)
        {
            var kind = Kind(stockBytes[stock[slot]]);
            foreach (var field in always[kind])
                if (!Set(ours[slots[slot]], field.Offset))
                    (census.GetValueOrDefault((kind, field.Offset)) > 0 ? waived : missing)
                        .Add($"{field.Name}@{field.Offset} kind={kind} installed_empty={census.GetValueOrDefault((kind, field.Offset))}");
        }
        Require(missing.Count == 0, $"{label}: creature clips leave unset what every Dreg clip of the kind sets: {string.Join("; ", missing)}");
        Console.WriteLine($"  excused, set in every Dreg clip of the kind but empty in installed clips of it too: {string.Join(", ", waived)}");

        void Same<T>(byte[] mine, byte[] theirs, int at, string what, params string[] allowed) where T : unmanaged
        {
            var lost = CombatantAnimationPoseSpace.PointerFields(typeof(T)).Where(field => !allowed.Contains(field.Name)
                && Set(theirs, at + field.Offset) && !Set(mine, at + field.Offset)).Select(field => field.Name).ToArray();
            Require(lost.Length == 0, $"{label}: the {what} lost {string.Join(", ", lost)}.");
        }
        Same<SAnimationDefinition>(built.Tags.Single(tag => tag.Class == SAnimationDefinition.Class).Data, catalog.Read(donor.DefinitionTag), 0, "definition");
        foreach (var (resourceClass, allowed) in new (uint, string[])[] { (SSkeletonResource.Class, []), (SAnimationRigResource.Class, ["IkControls"]) })
        {
            uint config = EntityBuilder.Components(catalog.Read(Dreg)).Single(tag => EntityBuilder.DefinitionClass(catalog.Read(tag)) == resourceClass);
            byte[] theirs = catalog.Read(config), mine = built.Tags.Single(tag => tag.Class == SEntityConfig.Class && EntityBuilder.DefinitionClass(tag.Data) == resourceClass).Data;
            int Resource(byte[] tag) => TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition)) + (int)BitConverter.ToInt64(tag, TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition)));
            Require(Resource(mine) == Resource(theirs), $"{label}: the {resourceClass:X8} resource moved.");
            if (resourceClass == SSkeletonResource.Class) Same<SSkeletonResource>(mine, theirs, Resource(mine), "skeleton", allowed);
            else Same<SAnimationRigResource>(mine, theirs, Resource(mine), "rig", allowed);
        }
        // Each pose-space slot's clip carries the Dreg clip's space, point for point, one frame per point.
        var reader = new AnimationControlReader(catalog);
        var clipEntry = catalog.TryGetEntry(stock[0], out var stockEntry) ? stockEntry : throw new InvalidDataException("A Dreg clip is missing.");
        AnimationControlClip Decode(uint tag)
        {
            catalog.SetOverlayEntries([new(new PackageEntry(tag, clipEntry.Reference, (int)(clipEntry.TypeInfo >> 9 & 127), (int)(clipEntry.TypeInfo >> 6 & 7),
                ours[tag].Length, "w64_sunrise_0ffe_0", EntityBuilder.ScratchPackage, TagHashes.Split(tag).EntryIndex) { TypeInfo = clipEntry.TypeInfo }, null)]);
            catalog.ReadOverride = read => read == tag ? ours[tag] : null;
            try { return reader.Read(tag); }
            finally { catalog.ReadOverride = null; catalog.SetOverlayEntries([]); }
        }
        foreach (int slot in Enumerable.Range(0, slots.Length).Where(slot => Set(stockBytes[stock[slot]], spaceField)))
        {
            var theirs = reader.Read(stock[slot]).PoseSpace!;
            var mineClip = Decode(slots[slot]);
            var mine = mineClip.PoseSpace;
            Require(mine is not null && mine.Kind == theirs.Kind && mine.Points.SequenceEqual(theirs.Points) && mineClip.FrameCount == theirs.Points.Count
                && mineClip.PoseMode == reader.Read(stock[slot]).PoseMode, $"{label}: slot {slot} lost its pose space.");
        }
        int spaces = slots.Count(slot => Set(ours[slot], spaceField));
        Console.WriteLine($"PASS combatant-creature-fields {label} clip_fields={clipFields.Count} kinds={always.Count} pose_space_slots={spaces}/{stock.Count(tag => Set(stockBytes[tag], spaceField))}");
    }

    /**
     * Superset clips write constant components once, in a codec-3 static section, as installed clips do. Donor
     * clips with the same root motion share one copy; with StillMotion every copy is all static.
     */
    private static void Compact(PackageCatalog catalog, CombatantCreature built, CombatantCreatureOptions options, string label)
    {
        if (built.DonorBoneCount == 0) return;
        int staticField = TagView.FieldOffset<SAnimationClip>("H42cf5d81.PayloadSection0");
        int animatedField = TagView.FieldOffset<SAnimationClip>("H42cf5d81.PayloadSection1");
        var clips = built.Tags.Where(tag => tag.Class == SAnimationClip.Class).ToArray();
        Require(clips.All(clip => BitConverter.ToInt64(clip.Data, staticField) != 0
            && BitConverter.ToUInt32(clip.Data, staticField + (int)BitConverter.ToInt64(clip.Data, staticField) - 4) == SAnimationCodec3Uniform.Class),
            $"{label}: a clip has no codec-3 static section.");
        var moving = built.Slots.Where(row => row.RootMotion != 0).ToArray();
        var copies = moving.Select(row => row.Clip).Distinct().Count();
        Require(copies == moving.Select(row => (row.Clip.Split(CombatantCreatureBuilder.MotionSeparator)[0], row.RootMotion)).Distinct().Count(),
            $"{label}: two copies carry the same root motion.");
        var motion = new AuthoredRootMotionReader(catalog);
        foreach (var row in moving.Where(row => row.RootMotion != row.DonorClip))
        {
            var (mine, theirs) = (motion.Read(row.RootMotion), motion.Read(row.DonorClip));
            Require(mine.FrameCount == theirs.FrameCount && mine.NativeExtractionMode == theirs.NativeExtractionMode
                && Enumerable.Range(0, mine.FrameCount).All(frame => mine.Sample(frame / 30f) == theirs.Sample(frame / 30f)),
                $"{label}: slot {row.Slot} shares root motion {row.RootMotion:X8} that differs from its own {row.DonorClip:X8}.");
        }
        int still = clips.Count(clip => BitConverter.ToInt64(clip.Data, animatedField) == 0);
        if (options.StillMotion)
            Require(moving.Select(row => built.ClipTags[row.Clip]).All(tag => BitConverter.ToInt64(clips.Single(clip => clip.Tag == tag).Data, animatedField) == 0),
                $"{label}: a still-motion copy has animated tracks.");
        Console.WriteLine($"PASS combatant-creature-compact {label} clips={clips.Length} bytes={clips.Sum(clip => (long)clip.Data.Length)} "
            + $"moving_slots={moving.Length} motion_copies={copies} all_static={still}");
    }

    /**
     * Shared clips carry the donor's groups alone, one shared name per content, and hold no creature data. A held
     * clip leaves the creature bones where the idle clip's first frame puts them, through the rig defaults.
     */
    private static void Shared(PackageCatalog catalog, CombatantCreature built, SkeletonRig skeleton, AnimationRigBinding binding,
        CombatantCreatureOptions options, string label)
    {
        bool expected = built.DonorBoneCount > 0 && options.ShareDonorClips;
        Require(expected == built.SharedClips.Count > 0, $"{label}: {built.SharedClips.Count} shared clips, sharing {(expected ? "on" : "off")}.");
        if (!expected) return;
        var reader = new AnimationControlReader(catalog);
        int donorGroups = binding.ControlGroups.Count - 1;
        foreach (var (tag, name) in built.SharedClips)
        {
            var clip = reader.Read(tag);
            Require(clip.ControlGroups.SequenceEqual(binding.ControlGroups.Take(donorGroups)) && clip.NodeCount == built.DonorBoneCount,
                $"{label}: shared clip {name} is not donor-only.");
        }
        Require(built.SharedClips.Values.Distinct().Count() == built.SharedClips.Count, $"{label}: two shared clips have one name.");
        var byTag = built.ClipTags.ToDictionary(pair => pair.Value, pair => pair.Key);
        var names = built.SharedClips.Keys.Select(tag => byTag[tag]).ToArray();
        Require(names.All(name => name.StartsWith(CombatantCreatureBuilder.SpacePrefix, StringComparison.Ordinal) || name == CombatantCreatureBuilder.NeutralClip
            || name.StartsWith(CombatantCreatureBuilder.HeldClip + CombatantCreatureBuilder.MotionSeparator, StringComparison.Ordinal)),
            $"{label}: a creature clip is marked shared.");
        string? heldName = names.FirstOrDefault(name => name.StartsWith(CombatantCreatureBuilder.HeldClip, StringComparison.Ordinal));
        if (heldName is null || options.StillClips) { Console.WriteLine($"PASS combatant-creature-shared {label} shared={names.Length} held=none"); return; }
        string idle = built.SlotsByClip.Where(pair => built.ClipTags.ContainsKey(pair.Key) && !built.SharedClips.ContainsKey(built.ClipTags[pair.Key])
            && !pair.Key.Contains(CombatantCreatureBuilder.MotionSeparator)).MaxBy(pair => pair.Value).Key;
        var pose = skeleton.CreateBindPose();
        binding.Apply(reader.Read(built.ClipTags[idle]), 0, pose);
        var first = pose.ObjectTransforms.ToArray();
        binding.Apply(reader.Read(built.ClipTags[heldName]), 0, pose);
        var heldPose = pose.ObjectTransforms.ToArray();
        float worst = Enumerable.Range(built.DonorBoneCount, built.CreatureBoneCount)
            .Max(bone => System.Numerics.Vector3.Distance(first[bone].Translation, heldPose[bone].Translation));
        Require(worst < 0.002f, $"{label}: under {heldName} a creature bone sits {worst * 1000:F1} mm from the idle first frame.");
        Console.WriteLine($"PASS combatant-creature-shared {label} shared={names.Length} held={heldName} idle={idle} worst_mm={worst * 1000:F2}");
    }

    private static Dictionary<((int, bool) Kind, int Offset), int>? census;

    /** Per clip kind and pointer field, how many installed clips leave the field empty. */
    private static Dictionary<((int, bool), int), int> Census(PackageCatalog catalog, IReadOnlyList<(int Offset, string Name)> fields,
        Func<byte[], (int, bool)> kind)
    {
        var result = new Dictionary<((int, bool), int), int>();
        int limit = fields.Max(field => field.Offset) + sizeof(long);
        foreach (var entry in catalog.Entries)
        {
            if (entry.Reference != SAnimationClip.Class) continue;
            byte[] clip;
            try { clip = catalog.Read(entry.TagHash); }
            catch (Exception error) when (error is InvalidDataException or IOException) { continue; }
            if (clip.Length < limit) continue;
            var key = kind(clip);
            foreach (var field in fields)
                if (BitConverter.ToInt64(clip, field.Offset) == 0) result[(key, field.Offset)] = result.GetValueOrDefault((key, field.Offset)) + 1;
        }
        return result;
    }

    /**
     * Every copied tag is reached from the copy's entity by the rules the build uses to load world files. A copy
     * no rule reaches is never resident, and the game reads a null object where a loaded tag names it.
     */
    private static void LoadClosure(PackageCatalog catalog, CombatantCreature built, string label)
    {
        var copies = built.Tags.ToDictionary(tag => tag.Tag);
        var reached = new HashSet<uint> { built.Tags[0].Tag };
        var queue = new Queue<uint>(reached);
        while (queue.Count > 0)
        {
            uint tag = queue.Dequeue();
            if (!copies.TryGetValue(tag, out var copy)) continue;
            foreach (uint next in PackageNativeDependencies.Read(catalog, tag, copy.Class, copy.TypeInfo, copy.Data))
                if (reached.Add(next)) queue.Enqueue(next);
        }
        var missing = built.Tags.Where(tag => !reached.Contains(tag.Tag)).Select(tag => $"{tag.Tag:X8}/{tag.Class:X8}").ToArray();
        Require(missing.Length == 0, $"{label}: {missing.Length} copied tags no load rule reaches: {string.Join(" ", missing.Take(12))}");
        Console.WriteLine($"PASS combatant-creature-load-closure {label} copies={built.Tags.Count} reached={built.Tags.Count(tag => reached.Contains(tag.Tag))}");
    }

    /**
     * No copied tag's words name an installed tag the copy does not mean to load. The build loads every tag an
     * animation asset or config word names, so a sample that spells a foreign tag loads it, unresolved.
     */
    private static void NoForeignTags(PackageCatalog catalog, CombatantCreature built, string label)
    {
        var copies = built.Tags.Select(tag => tag.Tag).ToHashSet();
        var foreign = new List<string>();
        foreach (var clip in built.Tags.Where(tag => tag.Class == SAnimationClip.Class))
            foreach (uint named in PackageNativeDependencies.Read(catalog, clip.Tag, clip.Class, clip.TypeInfo, clip.Data))
                if (!copies.Contains(named)) foreign.Add($"{clip.Tag:X8}->{named:X8}");
        Require(foreign.Count == 0, $"{label}: clips name {foreign.Count} installed tags by chance: {string.Join(" ", foreign.Take(10))}");
        Console.WriteLine($"PASS combatant-creature-no-foreign-tags {label} clips={built.Tags.Count(tag => tag.Class == SAnimationClip.Class)}");
    }

    /**
     * The superset keeps the donor's body exactly: bones 0..N-1, their groups, every donor control row, the name table,
     * the limb solves and the corrections, and every config other than skeleton, rig, model and definition, byte for byte.
     */
    private static void Superset(PackageCatalog catalog, CombatantCreature built, SkeletonRig skeleton, AnimationRigBinding binding,
        IReadOnlyDictionary<uint, byte[]> data, IReadOnlyList<WorldTag> stock, string label)
    {
        var donor = new SkeletonReader(catalog).Read(Dreg);
        var donorBinding = new AnimationRigReader(catalog).Read(donor);
        int n = donor.Bones.Count;
        Require(built.DonorBoneCount == n && skeleton.Bones.Count == n + built.CreatureBoneCount, $"{label}: the superset bone counts are wrong.");
        for (int bone = 0; bone < n; bone++)
        {
            var mine = skeleton.Bones[bone]; var theirs = donor.Bones[bone];
            Require(mine.NameHash == theirs.NameHash && mine.ParentIndex == theirs.ParentIndex && mine.BindObject == theirs.BindObject
                && mine.InverseBindObject == theirs.InverseBindObject, $"{label}: donor bone {bone} changed.");
        }
        Require(skeleton.Bones.Skip(n).All(bone => bone.ParentIndex >= 0), $"{label}: an appended bone is a root.");
        var configs = EntityBuilder.Components(catalog.Read(Dreg));
        uint Config(uint resourceClass) => configs.Single(tag => EntityBuilder.DefinitionClass(catalog.Read(tag)) == resourceClass);
        byte[] skeletonData = built.Tags.Single(tag => tag.Class == SEntityConfig.Class && EntityBuilder.DefinitionClass(tag.Data) == SSkeletonResource.Class).Data;
        byte[] donorSkeleton = catalog.Read(Config(SSkeletonResource.Class));
        Require(SkeletonWriter.GroupZeroCount(skeletonData) == SkeletonWriter.GroupZeroCount(donorSkeleton), $"{label}: the group 0 count changed.");
        static short[] Shorts(byte[] config, int field)
        {
            int resource = TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition));
            resource += (int)BitConverter.ToInt64(config, resource);
            var (count, at, _) = TagArray.Checked(config, resource + field);
            return Enumerable.Range(0, count).Select(i => BitConverter.ToInt16(config, at + 2 * i)).ToArray();
        }
        foreach (int field in new[] { 176, 192 })
            Require(Shorts(skeletonData, field).Take(n).SequenceEqual(Shorts(donorSkeleton, field)), $"{label}: the donor bones' group array at +{field} changed.");
        for (int control = 0; control < donorBinding.Controls.Count; control++)
            Require(binding.Controls[control] == donorBinding.Controls[control], $"{label}: donor control {control} changed.");
        Require(binding.ControlGroups.Take(donorBinding.ControlGroups.Count).SequenceEqual(donorBinding.ControlGroups)
            && binding.ControlGroups.Count == donorBinding.ControlGroups.Count + 1 && binding.IkControlIndices.SequenceEqual(donorBinding.IkControlIndices)
            && binding.BoneCorrectionCount == donorBinding.BoneCorrectionCount && binding.Controls.Count == donorBinding.Controls.Count + built.CreatureBoneCount,
            $"{label}: the donor groups, limb solves or corrections changed.");
        Require(CombatantAnimationRig.NameTable(data[binding.ResourceTag]).SequenceEqual(CombatantAnimationRig.NameTable(catalog.Read(Config(SAnimationRigResource.Class)))),
            $"{label}: the name table changed.");
        // Every other config is the donor's, with only its tag words moved to the copies.
        var rewritten = new[] { SSkeletonResource.Class, SAnimationRigResource.Class, SModelComponent.Class };
        int same = 0;
        // A word may differ only where ours names a tag the creature copied in place of the installed one.
        var copied = built.Tags.Select(tag => tag.Tag).ToHashSet();
        foreach (var copy in stock.Skip(1).Where(tag => tag.Class == SEntityConfig.Class && !rewritten.Contains(EntityBuilder.DefinitionClass(tag.Data))))
        {
            byte[] mine = built.Tags.Single(tag => tag.Tag == copy.Tag).Data;
            Require(mine.Length == copy.Data.Length, $"{label}: config copy {copy.Tag:X8} changed size.");
            for (int at = 0; at + 4 <= mine.Length; at += 4)
            {
                uint a = BitConverter.ToUInt32(mine, at), b = BitConverter.ToUInt32(copy.Data, at);
                Require(a == b || copied.Contains(a) && catalog.ContainsTag(b), $"{label}: config copy {copy.Tag:X8} differs at +{at}.");
            }
            same++;
        }
        // Every moving slot's clip carries its donor clip's root motion, sample for sample.
        var motion = new AuthoredRootMotionReader(catalog);
        int moving = 0;
        foreach (var row in built.Slots.Where(row => row.RootMotion != 0).GroupBy(row => row.Clip).Select(group => group.First()))
        {
            var theirs = motion.Read(row.RootMotion);
            var mineMotion = motion.Read(built.ClipTags[row.Clip]);
            Require(mineMotion.FrameCount == theirs.FrameCount && mineMotion.NativeExtractionMode == theirs.NativeExtractionMode
                && mineMotion.CycleTransform == theirs.CycleTransform, $"{label}: clip {row.Clip} lost its root motion header.");
            for (int frame = 0; frame < theirs.FrameCount; frame++)
            {
                // Scrubbing may move a sample by one quantum.
                var a = mineMotion.Sample(frame / 30f); var b = theirs.Sample(frame / 30f);
                Require(System.Numerics.Vector3.Distance(a.Translation, b.Translation) < 0.005f && CombatantAnimationConverter.Degrees(a.Rotation, b.Rotation) < 0.2f,
                    $"{label}: clip {row.Clip} root motion differs at frame {frame}.");
            }
            moving++;
        }
        // The hidden donor models draw nothing: every part of the copies carries the never-drawn flag.
        int hidden = 0;
        foreach (var tag in built.Tags.Where(tag => tag.Class == SEntityModel.Class))
        {
            ref readonly var model = ref TagView.Checked<SEntityModel>(tag.Data, 0);
            foreach (ref readonly var mesh in TagView.Rows(tag.Data, in model.Meshes, SEntityModelMesh.Class))
                foreach (ref readonly var part in TagView.Rows(tag.Data, in mesh.Parts, SEntityModelPart.Class))
                    Require((part.PartFlags & 0x40) != 0 && part.IndexCount == 0, $"{label}: hidden model {tag.Tag:X8} still draws a part.");
            hidden++;
        }
        Console.WriteLine($"PASS combatant-creature-superset {label} moving_clips={moving} hidden_models={hidden}");
        Console.WriteLine($"PASS combatant-creature-superset {label} donor_bones={n} appended={built.CreatureBoneCount} controls={donorBinding.Controls.Count}+{built.CreatureBoneCount} groups={binding.ControlGroups.Count} configs_unchanged={same}");
    }

    /** The donor variant keeps every pose-space clip and names its row 0 clip everywhere else. */
    private static void OneClip(PackageCatalog catalog)
    {
        var tags = CombatantCreatureBuilder.DonorWithOneClip(catalog, Dreg);
        uint[] stock = CombatantCreatureBuilder.DefinitionSlots(catalog.Read(0x80FEF6B7));
        uint[] slots = CombatantCreatureBuilder.DefinitionSlots(tags.Single(tag => tag.Class == SAnimationDefinition.Class).Data);
        var reader = new AnimationControlReader(catalog);
        int spaces = 0;
        for (int slot = 0; slot < slots.Length; slot++)
        {
            bool space = reader.Read(stock[slot]).PoseSpace is not null;
            spaces += space ? 1 : 0;
            Require(slots[slot] == (space ? stock[slot] : 0x80C1A913u), $"Donor variant slot {slot} names {slots[slot]:X8}.");
        }
        Console.WriteLine($"PASS combatant-donor-one-clip tags={tags.Count} slots={slots.Length} pose_space_kept={spaces} other=0x80C1A913");
    }

    /** An 8 by 8 checker in four colours, the synthetic creature's base colour. */
    private static SunriseStudio.Formats.RgbaImage Checker()
    {
        byte[] pixels = new byte[8 * 8 * 4];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int at = (y * 8 + x) * 4, cell = (x / 4) + 2 * (y / 4);
                pixels[at] = (byte)(cell is 0 or 3 ? 220 : 40); pixels[at + 1] = (byte)(cell is 1 or 3 ? 200 : 30);
                pixels[at + 2] = (byte)(cell == 2 ? 230 : 50); pixels[at + 3] = 255;
            }
        return new(8, 8, pixels);
    }

    /**
     * The creature's material is the stock skin material with only its colour row and its flat rows changed: same
     * shaders. The colour row decodes back to the input image within BC7 error; each flat row to its one colour.
     */
    private static void OwnTexture(PackageCatalog catalog, CombatantCreature built, SunriseStudio.Formats.RgbaImage image,
        IReadOnlyDictionary<int, uint> flatSlots, string label)
    {
        uint stock = CompatibleSkinnedMesh.NewRigMaterial;
        var material = built.Assets.Single(asset => asset.BaseTagHash == stock && asset.Native?.Reference == EntryClass(catalog, stock));
        var stockRows = EffectMaterial.Textures(catalog.Read(stock));
        var rows = EffectMaterial.Textures(material.Data);
        Require(material.Data.Length == catalog.Read(stock).Length && rows.Count == stockRows.Count, $"{label}: the material copy changed shape.");
        int changed = Enumerable.Range(0, material.Data.Length / 4).Count(word => BitConverter.ToUInt32(material.Data, word * 4) != BitConverter.ToUInt32(catalog.Read(stock), word * 4));
        Require(changed == 1 + flatSlots.Count, $"{label}: the material copy differs from the stock skin material in {changed} words, not {1 + flatSlots.Count}.");
        var colour = rows.Single(row => row.Slot == 1).Texture;
        bool Kept(int slot) => slot != 1 && !flatSlots.ContainsKey(slot);
        Require(rows.Where(row => Kept(row.Slot)).SequenceEqual(stockRows.Where(row => Kept(row.Slot))) && colour != stockRows.Single(row => row.Slot == 1).Texture,
            $"{label}: the colour row does not name the new texture, or a kept row changed.");
        foreach (var (slot, rgba) in flatSlots)
        {
            var flat = new TextureDataReader(catalog).Read(rows.Single(row => row.Slot == slot).Texture);
            var texel = new BCnEncoder.Decoder.BcDecoder().DecodeRaw(flat.Subresources[0].Data, flat.Width, flat.Height, BCnEncoder.Shared.CompressionFormat.Bc7)[0];
            byte[] want = BitConverter.GetBytes(rgba);
            Require(Math.Abs(texel.r - want[0]) <= 2 && Math.Abs(texel.g - want[1]) <= 2 && Math.Abs(texel.b - want[2]) <= 2 && Math.Abs(texel.a - want[3]) <= 2,
                $"{label}: flat slot {slot} decodes to {texel.r},{texel.g},{texel.b},{texel.a}, not {want[0]},{want[1]},{want[2]},{want[3]}.");
        }
        // Studio's pixel decoder has no BC7; the encoder's own decoder reads mip 0.
        var texture = new TextureDataReader(catalog).Read(colour);
        Require(texture.Format == 99, $"{label}: the texture is DXGI {texture.Format}, not BC7 sRGB (99) as the stock colour row is.");
        var raw = new BCnEncoder.Decoder.BcDecoder().DecodeRaw(texture.Subresources[0].Data, texture.Width, texture.Height, BCnEncoder.Shared.CompressionFormat.Bc7);
        var decoded = new SunriseStudio.Formats.RgbaImage(texture.Width, texture.Height, raw.SelectMany(pixel => new[] { pixel.r, pixel.g, pixel.b, pixel.a }).ToArray());
        Require(decoded.Width == image.Width && decoded.Height == image.Height, $"{label}: the texture is {decoded.Width}x{decoded.Height}, not {image.Width}x{image.Height}.");
        double error = Enumerable.Range(0, image.Pixels.Length).Where(index => index % 4 != 3).Average(index => Math.Abs(image.Pixels[index] - decoded.Pixels[index]));
        Require(error < 6, $"{label}: the texture decodes with mean error {error:F2}.");
        // The model's drawn part names the copy, not the stock material.
        var model = built.Assets.Single(asset => asset.Native?.EntryIndex == TagHashes.Split(built.ModelTag).EntryIndex && asset.Native?.PackageId == TagHashes.Split(built.ModelTag).PackageId);
        uint materialTag = TagHashes.Make(material.Native!.PackageId, material.Native.EntryIndex!.Value);
        Require(Words(model.Data).Contains(materialTag) && !Words(model.Data).Contains(stock), $"{label}: the model does not draw with the material copy.");
        Console.WriteLine($"PASS combatant-creature-own-texture {label} material={materialTag:X8} texture={colour:X8} {decoded.Width}x{decoded.Height} mean_error={error:F2}");
    }

    private static uint EntryClass(PackageCatalog catalog, uint tag) => catalog.TryGetEntry(tag, out var entry) ? entry.Reference : 0;

    /** A scaled and turned creature poses each bone where the same transform puts the original pose. */
    private static void Transform(RiggedEntityInput creature)
    {
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var fitted = CombatantCreatureInput.Transformed(creature, 0.5f, turn);
        var similar = Matrix4x4.CreateScale(0.5f) * Matrix4x4.CreateFromQuaternion(turn);
        float error = 0;
        foreach (int frame in new[] { 0, 7 })
        {
            Matrix4x4[] Pose(RiggedEntityInput input)
            {
                var pose = new Matrix4x4[input.Bones.Count];
                for (int bone = 0; bone < pose.Length; bone++)
                {
                    var local = input.Clips[1].Bones[bone][frame].Matrix;
                    pose[bone] = input.Bones[bone].Parent < 0 ? local : local * pose[input.Bones[bone].Parent];
                }
                return pose;
            }
            var before = Pose(creature);
            var after = Pose(fitted);
            for (int bone = 0; bone < before.Length; bone++)
                error = Math.Max(error, Vector3.Distance(Vector3.Transform(before[bone].Translation, similar), after[bone].Translation));
        }
        Require(error < 1e-5f, $"A transformed creature misses by {error}.");
        Console.WriteLine($"PASS combatant-creature-transform error={error:G3}");
    }

    /** Twelve bones, a quadruped, with idle, bite, hurt and die clips. */
    private static RiggedEntityInput Synthetic()
    {
        static BoneTransform At(float x, float y, float z) => new(Quaternion.Identity, new(x, y, z), 1);
        RiggedBone[] bones =
        [
            new("root", -1, At(0, 0, 0)), new("hips", 0, At(-0.3f, 0, 0.6f)), new("spine", 1, At(0.3f, 0, 0.65f)), new("neck", 2, At(0.5f, 0, 0.8f)),
            new("head", 3, At(0.65f, 0, 0.9f)), new("leg_fl", 2, At(0.3f, 0.15f, 0.3f)), new("foot_fl", 5, At(0.3f, 0.15f, 0)),
            new("leg_fr", 2, At(0.3f, -0.15f, 0.3f)), new("foot_fr", 7, At(0.3f, -0.15f, 0)), new("leg_bl", 1, At(-0.3f, 0.15f, 0.3f)),
            new("leg_br", 1, At(-0.3f, -0.15f, 0.3f)), new("tail", 1, At(-0.6f, 0, 0.7f)),
        ];
        var positions = bones.SelectMany(bone => new[] { bone.BindObject.Translation, bone.BindObject.Translation + Vector3.UnitX * 0.1f,
            bone.BindObject.Translation + Vector3.UnitZ * 0.1f }).ToArray();
        var geometry = new InterchangeMesh("creature", positions, Enumerable.Repeat(-Vector3.UnitY, positions.Length).ToArray(),
            positions.Select(position => new Vector2(position.X, position.Z)).ToArray(), Enumerable.Range(0, positions.Length).Select(index => (uint)index).ToArray());
        var weights = Enumerable.Range(0, positions.Length).Select(index => new VertexSkinWeights(index / 3, 0, 0, 0, new(1, 0, 0, 0))).ToArray();
        RiggedClip Clip(string name, int frames, float swing) => new(name, bones.Select((bone, index) =>
        {
            var local = bone.Parent < 0 ? bone.BindObject : BoneTransform.FromMatrix(bone.BindObject.Matrix * Inverse(bones[bone.Parent].BindObject).Matrix);
            return (IReadOnlyList<BoneTransform>)Enumerable.Range(0, frames).Select(frame => local with
            {
                Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, swing * MathF.Sin(frame * 0.3f + index))
            }).ToArray();
        }).ToArray());
        return new("creature", bones, geometry, weights, [Clip("idle", 31, 0.05f), Clip("bite", 16, 0.4f), Clip("hurt", 11, 0.3f), Clip("die", 25, 0.8f)], "idle");
    }

    private static BoneTransform Inverse(BoneTransform value) =>
        Matrix4x4.Invert(value.Matrix, out var inverse) ? BoneTransform.FromMatrix(inverse) : throw new InvalidDataException("A bind is singular.");

    private static IEnumerable<uint> Words(byte[] data)
    {
        for (int at = 0; at + 4 <= data.Length; at += 4) yield return BitConverter.ToUInt32(data, at);
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
