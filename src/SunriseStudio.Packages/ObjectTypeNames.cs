namespace SunriseStudio.Packages;

/** The game's object type names, indexed by SEntity.ObjectType. */
public static class ObjectTypeNames
{
    private static readonly string[] Names =
    [
        "inherited", "static_mesh", "prop_simple_DEPRECATED", "prop_expensive_DEPRECATED",
        "prop_cosmetic_static", "prop_cosmetic_movable", "prop_cosmetic_movable_garbage",
        "prop_networked_static", "prop_networked_movable", "prop_cinematic", "speedtree", "interactive",
        "biped", "creature", "weapon", "vehicle", "turret", "emitter", "projectile", "item", "item_ammo",
        "item_loot", "gear", "hop_on", "hop_on_gear_biped", "hop_on_gear_weapon", "hop_on_gear_ship",
        "hop_on_gear_sparrow", "system"
    ];

    public static int Count => Names.Length;
    public static string? Name(int type) => (uint)type < (uint)Names.Length ? Names[type] : null;
}
