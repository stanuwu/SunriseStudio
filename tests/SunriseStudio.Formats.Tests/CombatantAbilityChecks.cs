using System.Buffers.Binary;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

/** Projectile, gun, loadout and ability definition readers and writers over every installed instance. */
internal static class CombatantAbilityChecks
{
    private const uint EntityConfigClass = 0x80809C36;
    // Dreg entity 0x80C1AF8A: its loadout, the shock pistol gun, its projectile entity and projectile config.
    private const uint DregLoadout = 0x81579F45, DregGun = 0x815B3968, DregProjectileEntity = 0x80C1BD98;
    private const uint DregProjectile = 0x815B395D, DregImpactTable = 0x80C1BD96, DregActorGroup = 0x815B2E58;
    private const uint DregDefinition = 0x80FEF6B6;

    // Captain and Servitor squad entities; the Servitor's gun is its own component.
    private const uint DregEnemy = 0x80C1AF8A, Captain = 0x80C1AE68, Servitor = 0x80C1B2B2;
    private const ushort ScratchPackage = 0xFFE;

    /** The Dreg's own rows, every installed instance of each class, then the weapon copy plans. */
    public static void Installed(PackageCatalog catalog)
    {
        Dreg(catalog);
        foreach (uint enemy in new[] { DregEnemy, Captain, Servitor }) WeaponCopy(catalog, enemy);
        var counts = new SortedDictionary<string, int>();
        foreach (var entry in catalog.Entries.Where(e => e.Reference == EntityConfigClass && catalog.ContainsInstalledPackage(e.PackageId)))
        {
            byte[] bytes = catalog.ReadOriginal(entry.TagHash);
            uint cls = DefinitionClass(bytes);
            if (cls == CombatantProjectile.PayloadClass) { Projectile(entry.TagHash, bytes); Count(counts, "projectile"); }
            else if (CombatantGun.Classes.Contains(cls)) { Gun(entry.TagHash, bytes); Count(counts, "gun"); }
            else if (cls == CombatantLoadout.PayloadClass) { Loadout(entry.TagHash, bytes); Count(counts, "loadout"); }
            else if (cls == CombatantAbilityDefinition.ActorGroupClass)
            {
                Check(CombatantAbilityDefinition.Definitions(entry.TagHash, bytes) is not null, $"Actor group {entry.TagHash:X8} was not read.");
                Count(counts, "actor_group");
            }
        }
        foreach (var entry in catalog.Entries.Where(e => e.Reference == CombatantAbilityDefinition.TagClass && catalog.ContainsInstalledPackage(e.PackageId)))
        {
            Definition(entry.TagHash, catalog.ReadOriginal(entry.TagHash));
            Count(counts, "ability_definition");
        }
        Console.WriteLine("pass installed_combatant_abilities " + string.Join(" ", counts.Select(pair => $"{pair.Key}={pair.Value}")));
    }

    /** The Dreg's loadout names its pistol, the pistol names its projectile, and the projectile its impact table. */
    private static void Dreg(PackageCatalog catalog)
    {
        var loadout = CombatantLoadout.Read(DregLoadout, catalog.Read(DregLoadout)) ?? throw new InvalidOperationException("Dreg loadout was not read.");
        Check(loadout.Groups.Count == 3 && loadout.Weapons.Contains(0x80C1BDD0u), "Dreg loadout groups differ.");
        var gun = CombatantGun.Read(DregGun, catalog.Read(DregGun)) ?? throw new InvalidOperationException("Dreg gun was not read.");
        Check(gun.Projectiles[0].Entity == DregProjectileEntity && gun.Projectiles[1].Entity == DregProjectileEntity, "Dreg gun projectile differs.");
        Check(gun.Stat("damage_falloff_distance_start") == 25 && gun.Stat("damage_falloff_distance_end") == 50, "Dreg falloff differs.");
        var projectile = CombatantProjectile.Read(DregProjectile, catalog.Read(DregProjectile)) ?? throw new InvalidOperationException("Dreg projectile was not read.");
        Check(projectile.Speed == 45 && projectile.ImpactTable == DregImpactTable, "Dreg projectile differs.");
        var rows = CombatantProjectileEntity.Components(catalog.Read(DregProjectileEntity));
        Check(rows.Any(row => row.Config == DregProjectile), "Dreg projectile entity does not list its projectile config.");
        Check(CombatantAbilityDefinition.Definitions(DregActorGroup, catalog.Read(DregActorGroup))?.Local == DregDefinition, "Dreg ability definition differs.");
        Check(CombatantAbilityDefinition.Read(catalog.Read(DregDefinition)) is { Groups.Count: > 0 }, "Dreg ability groups were not read.");
        Console.WriteLine($"pass dreg_ability_chain weapons={loadout.Weapons.Count()} speed={projectile.Speed} impact={projectile.ImpactTable:X8}");
    }

    /** Own-value writes keep every byte; a speed and an impact table edit read back. */
    private static void Projectile(uint tag, byte[] bytes)
    {
        var projectile = CombatantProjectile.Read(tag, bytes) ?? throw new InvalidOperationException($"Projectile {tag:X8} was not read.");
        foreach (var value in projectile.Values) Same(bytes, CombatantProjectile.SetValue(tag, bytes, value.Name, value.Value), tag);
        foreach (var link in projectile.Links.Where(link => link.Tag != 0))
            Same(bytes, CombatantProjectile.SetLink(tag, bytes, link.Name, link.Tag), tag);
        byte[] edited = CombatantProjectile.SetValue(tag, bytes, "speed", 123.5f);
        OnlyChanged(bytes, edited, projectile.Values.Single(value => value.Name == "speed").Offset);
        Check(CombatantProjectile.Read(tag, edited)?.Speed == 123.5f, $"Projectile {tag:X8} speed did not read back.");
        byte[] swapped = CombatantProjectile.SetImpactTable(tag, bytes, 0x81234567);
        Check(CombatantProjectile.Read(tag, swapped)?.ImpactTable == 0x81234567, $"Projectile {tag:X8} impact table did not read back.");
    }

    /** Own-value writes keep every byte; a stat and a projectile edit read back. */
    private static void Gun(uint tag, byte[] bytes)
    {
        var gun = CombatantGun.Read(tag, bytes) ?? throw new InvalidOperationException($"Gun {tag:X8} was not read.");
        foreach (var stat in gun.Stats) Same(bytes, CombatantGun.SetStat(tag, bytes, stat.Name, stat.Value), tag);
        foreach (var slot in gun.Projectiles.Where(slot => slot.Entity is not (0 or uint.MaxValue)))
            Same(bytes, CombatantGun.SetProjectile(tag, bytes, slot.Slot, slot.Entity), tag);
        byte[] edited = CombatantGun.SetStat(tag, bytes, "damage_multiplier_base", 2.5f);
        OnlyChanged(bytes, edited, gun.Stats.Single(stat => stat.Name == "damage_multiplier_base").Offset);
        Check(CombatantGun.Read(tag, edited)?.Stat("damage_multiplier_base") == 2.5f, $"Gun {tag:X8} stat did not read back.");
        byte[] moved = CombatantGun.SetProjectile(tag, bytes, 0, 0x81234567);
        Check(CombatantGun.Read(tag, moved)?.Projectiles[0].Entity == 0x81234567, $"Gun {tag:X8} projectile did not read back.");
    }

    /** Own-value writes keep every byte; a weapon edit reads back. */
    private static void Loadout(uint tag, byte[] bytes)
    {
        var loadout = CombatantLoadout.Read(tag, bytes) ?? throw new InvalidOperationException($"Loadout {tag:X8} was not read.");
        foreach (var group in loadout.Groups.Append(loadout.Fallback))
            foreach (var weapon in group.Weapons.Where(weapon => weapon.Entity is not (0 or uint.MaxValue)))
                Same(bytes, CombatantLoadout.SetWeapon(tag, bytes, group.Group, weapon.Index, weapon.Entity), tag);
        foreach (var group in loadout.Groups.Where(group => group.Weight >= 0 && float.IsFinite(group.Weight)))
            Same(bytes, CombatantLoadout.SetWeight(tag, bytes, group.Group, group.Weight), tag);
        if (loadout.Groups.FirstOrDefault(group => group.Weapons.Count > 0) is { } first)
        {
            byte[] edited = CombatantLoadout.SetWeapon(tag, bytes, first.Group, 0, 0x81234567);
            OnlyChanged(bytes, edited, first.Weapons[0].Offset);
            Check(CombatantLoadout.Read(tag, edited)?.Groups[first.Group].Weapons[0].Entity == 0x81234567, $"Loadout {tag:X8} weapon did not read back.");
        }
    }

    /** Own-value writes keep every byte; the canonical writer gives the tag back. */
    private static void Definition(uint tag, byte[] bytes)
    {
        var definition = CombatantAbilityDefinition.Read(bytes) ?? throw new InvalidOperationException($"Ability definition {tag:X8} was not read.");
        foreach (var group in definition.Groups)
            for (int request = 0; request < group.Requests.Count; request++)
                Same(bytes, CombatantAbilityDefinition.SetRequest(bytes, group.Index, request, group.Requests[request].Key), tag);
        foreach (var sequence in definition.Sequences)
            Same(bytes, CombatantAbilityDefinition.SetSequenceResource(bytes, sequence.Index, sequence.Resource), tag);
        if (definition.Groups.FirstOrDefault(group => group.Requests.Count > 0) is { } first)
        {
            byte[] edited = CombatantAbilityDefinition.SetRequest(bytes, first.Index, 0, 0x12345678);
            OnlyChanged(bytes, edited, first.Requests[0].Offset);
            Check(CombatantAbilityDefinition.Read(edited)?.Groups[first.Index].Requests[0].Key == 0x12345678, $"Ability definition {tag:X8} request did not read back.");
        }
        var schema = StudioRegistry.Index;
        byte[] written = CanonicalTagWriter.Write(TagBlueprintReader.Read(TagDocument.Parse(bytes, CombatantAbilityDefinition.TagClass, schema), schema, false), schema);
        Check(written.AsSpan().SequenceEqual(bytes), $"Ability definition {tag:X8} did not round trip through the canonical writer.");
    }

    /**
     * Applies the plan the way the entity clone does, by aligned word retarget over the copy set, then checks
     * that no copy names a copied stock tag, every chain resolves inside the copy, and class records stay paired.
     */
    private static void WeaponCopy(PackageCatalog catalog, uint enemy)
    {
        var plan = CombatantWeaponCopy.WithProjectileValue(catalog, CombatantWeaponCopy.Plan(catalog, enemy), "speed", 20);
        Check(plan.Chains.Count > 0, $"Enemy {enemy:X8} has no weapon chain.");
        var copies = Retarget(catalog, enemy, plan.Also, plan.Replace, out var map);
        foreach (var (copy, bytes) in copies)
            for (int at = 0; at + 4 <= bytes.Length; at++)
                Check(!map.ContainsKey(TagRead.U32(bytes, at)), $"Copy {copy:X8} still names copied stock tag {TagRead.U32(bytes, at):X8} at +{at:X}.");
        foreach (var chain in plan.Chains)
        {
            uint weapon = map[chain.Weapon], gun = map[chain.Gun], shot = map[chain.Projectile], config = map[chain.ProjectileConfig];
            if (chain.Loadout != 0)
                Check(CombatantLoadout.Read(map[chain.Loadout], copies[map[chain.Loadout]])?.Weapons.Contains(weapon) == true,
                    $"Copied loadout of {enemy:X8} does not name the copied weapon.");
            Check(CombatantWeaponCopy.Components(copies[weapon]).Contains(gun), $"Copied weapon {weapon:X8} does not list the copied gun.");
            Check(CombatantGun.Read(gun, copies[gun])?.Projectiles.Any(slot => slot.Entity == shot) == true, $"Copied gun {gun:X8} does not name the copied projectile.");
            Check(CombatantWeaponCopy.Components(copies[shot]).Contains(config), $"Copied projectile entity {shot:X8} does not list the copied config.");
            Check(CombatantProjectile.Read(config, copies[config])?.Speed == 20, $"Copied projectile config {config:X8} lost its edit.");
        }
        int records = 0;
        foreach (var (copy, bytes) in copies.Where(pair => catalog.TryGetEntry(map.First(m => m.Value == pair.Key).Key, out var e) && e.Reference == SEntity.Class))
        {
            uint record = TagView.At<SEntity>(bytes, 0).ClassRecordGameplayRoot;
            if (!copies.TryGetValue(record, out var recordBytes))
            {
                // A stock record must not name a stock entity this plan copies, except the enemy's own.
                uint owner = record is 0 or uint.MaxValue ? 0 : TagView.At<SNetworkClassRecord>(catalog.Read(record), 0).OwnerDefinition;
                Check(!map.ContainsKey(owner) || owner == enemy, $"Copy {copy:X8} keeps a one-sided class record {record:X8}.");
                continue;
            }
            Check(TagView.At<SNetworkClassRecord>(recordBytes, 0).OwnerDefinition == copy, $"Copied record {record:X8} does not name copy {copy:X8}.");
            records++;
        }
        Console.WriteLine($"pass weapon_copy {enemy:X8} copied={copies.Count} chains={plan.Chains.Count} weapons={plan.Weapons.Count} paired_records={records}");
    }

    /** The copy set with scratch tags and every aligned word that names a copied tag moved; map is stock to copy. */
    private static Dictionary<uint, byte[]> Retarget(PackageCatalog catalog, uint enemy, IReadOnlyList<uint> also,
        IReadOnlyDictionary<uint, byte[]> replace, out Dictionary<uint, uint> map)
    {
        var sources = new List<uint> { enemy };
        foreach (uint tag in CombatantWeaponCopy.Components(catalog.Read(enemy)).Concat(also)) if (!sources.Contains(tag)) sources.Add(tag);
        map = sources.Select((tag, index) => (tag, copy: TagHashes.Make(ScratchPackage, index))).ToDictionary(x => x.tag, x => x.copy);
        var copies = new Dictionary<uint, byte[]>();
        foreach (uint tag in sources)
        {
            byte[] bytes = replace.TryGetValue(tag, out var edited) ? edited.ToArray() : catalog.Read(tag).ToArray();
            for (int at = 0; at + 4 <= bytes.Length; at += 4)
                if (map.TryGetValue(TagRead.U32(bytes, at), out uint copy)) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), copy);
            copies[map[tag]] = bytes;
        }
        return copies;
    }

    private static uint DefinitionClass(byte[] config)
    {
        int at = TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition));
        if (config.Length < at + sizeof(long)) return 0;
        long relative = BinaryPrimitives.ReadInt64LittleEndian(config.AsSpan(at)), target = at + relative;
        return relative == 0 || target < 4 || target > config.Length ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(config.AsSpan((int)target - 4));
    }

    private static void Same(byte[] before, byte[] after, uint tag) =>
        Check(before.AsSpan().SequenceEqual(after), $"Writing {tag:X8}'s own value changed its bytes.");

    private static void OnlyChanged(byte[] before, byte[] after, int offset)
    {
        Check(before.Length == after.Length && before.AsSpan(0, offset).SequenceEqual(after.AsSpan(0, offset))
            && before.AsSpan(offset + 4).SequenceEqual(after.AsSpan(offset + 4)), "An edit changed other bytes.");
    }

    private static void Count(SortedDictionary<string, int> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
