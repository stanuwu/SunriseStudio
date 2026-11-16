using System.Numerics;
using System.Text.Json.Nodes;
using SunriseStudio.Animation;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;

namespace SunriseStudio.Authoring;

public static partial class WorldBundleImport
{
    // A squad names a bundle creature as this prefix plus the creature's name.
    private const string CreaturePrefix = "creature:";
    // The gun stat a creature's `damage` multiplies.
    private const string GunDamageStat = "damage_multiplier_base";
    // The values of a creature's `attack`.
    private const string GunAttack = "gun", MeleeAttack = "melee";
    // The projectile value a creature's `projectile_speed` sets.
    private const string ProjectileSpeed = "speed";

    /**
     * Builds every bundle `creatures` entry over its donor enemy and adds it to the world, unbound to a squad.
     * @return Each creature's name and its entity file in the world.
     */
    private static Dictionary<string, string> PlaceCreatures(StudioProject project, PackageCatalog catalog, RegistryIndex schema,
        string destination, string bundleFolder, JsonObject bundle, ushort packageId, CancellationToken token)
    {
        var placed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in (bundle["creatures"] as JsonArray ?? []).OfType<JsonObject>())
        {
            token.ThrowIfCancellationRequested();
            string name = (string)item["name"]!;
            if (placed.ContainsKey(name)) throw new InvalidDataException($"The bundle has two creatures named {name}.");
            var clips = ((JsonObject)item["clips"]!).ToDictionary(
                pair => Enum.Parse<CombatantBehaviour>(pair.Key, ignoreCase: true), pair => (string)pair.Value!);
            if (!clips.TryGetValue(CombatantBehaviour.Idle, out string? idle)) throw new InvalidDataException($"Creature {name} maps no idle clip.");
            string work = Path.Combine(Path.GetTempPath(), "sunrise-bundle-creatures", name);
            var source = CombatantCreatureGltf.Read(Path.Combine(bundleFolder, (string)item["glb"]!), name, idle, work);
            float yaw = ((float?)item["yaw"] ?? 0) * MathF.PI / 180;
            var input = CombatantCreatureInput.Transformed(source.Input, (float?)item["scale"] ?? 1, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yaw));
            var bones = item["bones"] as JsonObject;
            var options = new CombatantCreatureOptions
            {
                HeadBone = (string?)bones?["head"], RightHandBone = (string?)bones?["right_hand"], LeftHandBone = (string?)bones?["left_hand"],
                RightFootBone = (string?)bones?["right_foot"], LeftFootBone = (string?)bones?["left_foot"],
                FollowBones = (item["follow"] as JsonObject)?.ToDictionary(pair => pair.Key, pair => (string)pair.Value!),
                Texture = source.Texture,
                Capsule = item["capsule"] is JsonObject capsule ? new((float)capsule["radius"]!, (float)capsule["height"]!) : null,
                GunScale = (float?)item["damage"] is float damage ? new Dictionary<string, float> { [GunDamageStat] = damage } : null,
                HealthScale = (float?)item["health"],
                DisplayName = (string?)item["display_name"],
                Grenades = (bool?)item["grenades"] ?? true,
                Ranged = ((string?)item["attack"] ?? GunAttack) switch
                {
                    GunAttack => true, MeleeAttack => false,
                    var other => throw new InvalidDataException($"Creature {name} has attack {other}; use {GunAttack} or {MeleeAttack}."),
                },
                Projectile = item["projectile"] is { } shot ? (uint?)Hex((string)shot!) : null,
                ProjectileValues = (float?)item["projectile_speed"] is float speed ? new Dictionary<string, float> { [ProjectileSpeed] = speed } : null,
            };
            placed[name] = WorldEntities.AddCreatureEnemy(project, catalog, schema, destination, Hex((string)item["donor"]!), input, clips,
                packageId, options, token);
        }
        return placed;
    }

    /** A squad's entity: an installed hex tag, or a bundle creature by `creature:name`. */
    private static string SquadEntity(string text, IReadOnlyDictionary<string, string> creatures) =>
        !text.StartsWith(CreaturePrefix, StringComparison.Ordinal) ? SunriseStudio.Tags.Tft.TftNames.Installed(Hex(text))
        : creatures.TryGetValue(text[CreaturePrefix.Length..], out string? file) ? file
        : throw new InvalidDataException($"A squad names {text}, which the bundle does not define.");
}
