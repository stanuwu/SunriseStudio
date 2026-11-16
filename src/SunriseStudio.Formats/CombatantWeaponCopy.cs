using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/** One shot path of an enemy; Loadout is 0 and Weapon is the enemy when the enemy lists the gun itself. */
public sealed record CombatantWeaponChain(uint Loadout, uint Weapon, uint Gun, uint Projectile, uint ProjectileConfig);

/**
 * The installed tags an enemy copy must also copy so its weapons stay its own, and the new bytes for some of them.
 * Pass Also and Replace to the entity clone; it moves every aligned word that names a copied tag.
 */
public sealed record CombatantWeaponCopyPlan(uint Enemy, IReadOnlyList<uint> Also, IReadOnlyDictionary<uint, byte[]> Replace,
    IReadOnlyList<CombatantWeaponChain> Chains, IReadOnlyList<uint> Weapons);

public static class CombatantWeaponCopy
{
    /**
     * Plans the weapon copy of an enemy: every loadout weapon, the gun configs it lists, the projectile entities
     * the guns name, their projectile configs, and the class record of each copied entity.
     * @param weaponFilter Copies only the weapons it accepts; null copies every loadout weapon.
     * @return The plan; Also holds no tag the enemy lists as a component, since the clone copies those itself.
     */
    public static CombatantWeaponCopyPlan Plan(PackageCatalog catalog, uint enemy, Func<uint, bool>? weaponFilter = null)
    {
        if (ClassOf(catalog, enemy) != SEntity.Class) throw new InvalidDataException($"0x{enemy:X8} is not an installed entity.");
        var own = Components(catalog.Read(enemy)).ToHashSet();
        var also = new List<uint>();
        var chains = new List<CombatantWeaponChain>();
        var weapons = new List<uint>();
        void Add(uint tag) { if (tag != enemy && !own.Contains(tag) && !also.Contains(tag)) also.Add(tag); }
        void AddEntity(uint entity)
        {
            Add(entity);
            // The entity and its class record name each other; both move or the link is one-sided.
            if (OwnRecord(catalog, entity) is { } record) Add(record);
        }
        void AddGuns(uint loadout, uint holder)
        {
            foreach (uint gunTag in Components(catalog.Read(holder)))
            {
                if (CombatantGun.Read(gunTag, catalog.Read(gunTag)) is not { } gun) continue;
                Add(gunTag);
                foreach (uint shot in gun.Projectiles.Select(slot => slot.Entity).Distinct())
                {
                    if (ClassOf(catalog, shot) != SEntity.Class) continue;
                    AddEntity(shot);
                    foreach (uint shotConfig in Components(catalog.Read(shot)))
                        if (CombatantProjectile.Read(shotConfig, catalog.Read(shotConfig)) is not null)
                        {
                            Add(shotConfig);
                            chains.Add(new(loadout, holder, gunTag, shot, shotConfig));
                        }
                }
            }
        }
        // An enemy with a built-in gun lists it as its own component; its chain has no loadout.
        AddGuns(0, enemy);
        foreach (uint config in own)
        {
            if (CombatantLoadout.Read(config, catalog.Read(config)) is not { } loadout) continue;
            foreach (uint weapon in loadout.Weapons.Distinct())
            {
                if (ClassOf(catalog, weapon) != SEntity.Class || (weaponFilter is not null && !weaponFilter(weapon))) continue;
                if (!weapons.Contains(weapon)) weapons.Add(weapon);
                AddEntity(weapon);
                AddGuns(config, weapon);
            }
        }
        return new(enemy, also, new Dictionary<uint, byte[]>(), chains, weapons);
    }

    /** The plan with one projectile value set on every projectile config it copies. */
    public static CombatantWeaponCopyPlan WithProjectileValue(PackageCatalog catalog, CombatantWeaponCopyPlan plan, string name, float value)
    {
        var replace = new Dictionary<uint, byte[]>(plan.Replace);
        foreach (uint config in plan.Chains.Select(chain => chain.ProjectileConfig).Distinct())
            replace[config] = CombatantProjectile.SetValue(config, replace.TryGetValue(config, out var bytes) ? bytes : catalog.Read(config), name, value);
        return plan with { Replace = replace };
    }

    /**
     * The plan with every barrel slot of every copied gun pointing at another installed projectile entity.
     * The new entity, its class record and its projectile configs are copied in place of the old ones.
     */
    public static CombatantWeaponCopyPlan WithProjectile(PackageCatalog catalog, CombatantWeaponCopyPlan plan, uint projectile)
    {
        if (ClassOf(catalog, projectile) != SEntity.Class) throw new InvalidDataException($"0x{projectile:X8} is not an installed entity.");
        var configs = Components(catalog.Read(projectile)).Where(tag => CombatantProjectile.Read(tag, catalog.Read(tag)) is not null).Distinct().ToArray();
        if (configs.Length == 0) throw new InvalidDataException($"0x{projectile:X8} has no projectile config.");
        if (plan.Chains.Count == 0) throw new InvalidDataException("The enemy has no gun to give the projectile.");
        var replace = new Dictionary<uint, byte[]>(plan.Replace);
        var old = new HashSet<uint>();
        foreach (var chain in plan.Chains)
        {
            old.Add(chain.Projectile);
            old.Add(chain.ProjectileConfig);
            if (OwnRecord(catalog, chain.Projectile) is { } record) old.Add(record);
        }
        foreach (uint gun in plan.Chains.Select(chain => chain.Gun).Distinct())
        {
            byte[] bytes = replace.TryGetValue(gun, out var edited) ? edited : catalog.Read(gun);
            foreach (var slot in CombatantGun.Read(gun, bytes)!.Projectiles.Where(slot => slot.Entity is not (0 or uint.MaxValue)))
                bytes = CombatantGun.SetProjectile(gun, bytes, slot.Slot, projectile);
            replace[gun] = bytes;
        }
        var also = plan.Also.Where(tag => !old.Contains(tag)).ToList();
        foreach (uint tag in new[] { projectile }.Concat(OwnRecord(catalog, projectile) is { } own ? [own] : []).Concat(configs))
            if (!also.Contains(tag)) also.Add(tag);
        foreach (uint tag in old) replace.Remove(tag);
        var chains = plan.Chains.Select(chain => (chain.Loadout, chain.Weapon, chain.Gun)).Distinct()
            .SelectMany(gun => configs.Select(config => new CombatantWeaponChain(gun.Loadout, gun.Weapon, gun.Gun, projectile, config))).ToArray();
        return plan with { Also = also, Replace = replace, Chains = chains };
    }

    /** The plan with one gun stat set on every gun config it copies. */
    public static CombatantWeaponCopyPlan WithGunStat(PackageCatalog catalog, CombatantWeaponCopyPlan plan, string name, float value)
    {
        var replace = new Dictionary<uint, byte[]>(plan.Replace);
        foreach (uint config in plan.Chains.Select(chain => chain.Gun).Distinct())
            replace[config] = CombatantGun.SetStat(config, replace.TryGetValue(config, out var bytes) ? bytes : catalog.Read(config), name, value);
        return plan with { Replace = replace };
    }

    /** The class record of an entity when the record names the entity back; null otherwise. */
    public static uint? OwnRecord(PackageCatalog catalog, uint entity)
    {
        byte[] bytes = catalog.Read(entity);
        if (!TagView.Fits<SEntity>(bytes, 0)) return null;
        uint record = TagView.At<SEntity>(bytes, 0).ClassRecordGameplayRoot;
        if (record is 0 or uint.MaxValue || ClassOf(catalog, record) != SNetworkClassRecord.Class) return null;
        byte[] recordBytes = catalog.Read(record);
        return TagView.Fits<SNetworkClassRecord>(recordBytes, 0) && TagView.At<SNetworkClassRecord>(recordBytes, 0).OwnerDefinition == entity
            ? record : null;
    }

    /** The component config tags an entity lists, in row order. */
    public static IReadOnlyList<uint> Components(byte[] entity) =>
        CombatantProjectileEntity.Components(entity).Select(row => row.Config).ToArray();

    private static uint ClassOf(PackageCatalog catalog, uint tag) => catalog.TryGetEntry(tag, out var entry) ? entry.Reference : 0;
}
