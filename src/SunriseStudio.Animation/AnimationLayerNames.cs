using System.Text;
using SunriseStudio.Packages;

namespace SunriseStudio.Animation;

/** FNV-1 matches for layer-name hashes; except "base", each is a candidate, not a stored name. */
public static class AnimationLayerNames
{
    private static readonly IReadOnlyDictionary<uint, string> names = new[]
    {
        "base", "aim", "aim_yaw", "aim_pitch", "look", "look_yaw", "look_pitch", "grip", "move_dip", "character", "replacement", "alert", "breathe",
        "idle_breathe", "crouch", "face", "face_idle_layer", "shake", "arms", "iron_sight", "aim_down",
        "aim_velocity", "pose_look", "flinch_poses", "soft_ping_pose", "hard_ping_layer", "base_overlay",
        "charged_overlay", "ability_pose_overlay", "acceleration", "velocity", "steering", "throttle",
        "left_door", "right_door"
    }.ToDictionary(Hash);

    /** Returns the matched name, or null for an unresolved hash. */
    public static string? Resolve(uint hash) => names.GetValueOrDefault(hash);

    /** FNV-1 over ASCII bytes, exact case; each non-ASCII char hashes as '?'. */
    public static uint Hash(string name)
    {
        uint hash = StringHash.Empty;
        foreach (byte value in Encoding.ASCII.GetBytes(name)) hash = unchecked(hash * 0x01000193) ^ value;
        return hash;
    }
}
