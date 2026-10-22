using System.Numerics;
using ImGuiNET;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

/** First-person camera at player scale: W, A, S and D move, Shift sprints, Space rises, Ctrl sinks. */
public sealed partial class AssetViewport
{
    // Approximate guardian eye height and run speed in metres; sprint multiplies the run speed.
    private const float PlayerEyeHeight = 1.7f, PlayerSpeed = 7, SprintFactor = 1.6f;
    private const float MinimumSpeedScale = .125f, MaximumSpeedScale = 16;
    private float playerSpeedScale = 1;
    private SceneMarker[]? spawnList;
    private SceneDocument? spawnScene;

    /** True while the editor drives the camera as a player; a live game pose is not a player camera. */
    public bool PlayerCamera => Camera.Player && !Camera.HasExternalPose;

    /** Switches between the orbit camera and the player camera at the current eye. */
    public void SetPlayerCamera(bool player)
    {
        if (Camera.HasExternalPose || player == Camera.Player) return;
        // Leaving keeps the eye and orbits a point a few metres ahead of it.
        const float OrbitDistance = 10;
        // First person walks the world floor, so an item seen in its own frame returns to the world frame and floor.
        if (player && Camera.Basis != Quaternion.Identity)
        {
            var eye = Camera.Position;
            var forward = Camera.Forward;
            Camera.Basis = Quaternion.Identity;
            Camera.LookFrom(eye, eye + forward);
        }
        if (player) { gridHeight = gridStep = 0; Camera.EnterPlayer(); }
        else Camera.EnterEditor(Camera.Position + Camera.Forward * OrbitDistance);
    }

    /** Spawn points of the shown scene, by name. */
    public IReadOnlyList<SceneMarker> Spawns
    {
        get
        {
            if (!ReferenceEquals(spawnScene, Scene) || spawnList is null)
            {
                spawnScene = Scene;
                spawnList = Scene?.Markers.Where(marker => marker.Layer == SceneLayer.Spawns)
                    .OrderBy(marker => Name(marker) ?? "", StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
            }
            return spawnList;
        }
    }

    /** Puts the player eye above the spawn, facing its heading, and switches to the player camera. */
    public void TeleportTo(SceneMarker spawn)
    {
        if (Camera.HasExternalPose) return;
        SetPlayerCamera(true);
        var forward = Vector3.Transform(Vector3.UnitX, spawn.Rotation);
        float yaw = forward.X * forward.X + forward.Y * forward.Y > 1e-6f ? MathF.Atan2(forward.Y, forward.X) : Camera.Yaw;
        Camera.SetEye(spawn.Position + Vector3.UnitZ * PlayerEyeHeight, yaw, 0);
    }

    /** Movement reads held keys every frame while the view is hovered or the look drag holds it. */
    private void MovePlayer(bool active)
    {
        var io = ImGui.GetIO();
        if (!active || io.WantTextInput) return;
        var local = Vector3.Zero;
        if (ImGui.IsKeyDown(ImGuiKey.W)) local.Y += 1;
        if (ImGui.IsKeyDown(ImGuiKey.S)) local.Y -= 1;
        if (ImGui.IsKeyDown(ImGuiKey.D)) local.X += 1;
        if (ImGui.IsKeyDown(ImGuiKey.A)) local.X -= 1;
        if (ImGui.IsKeyDown(ImGuiKey.Space)) local.Z += 1;
        if (io.KeyCtrl) local.Z -= 1;
        if (local == Vector3.Zero) return;
        Camera.Move(local, io.DeltaTime, PlayerSpeed * playerSpeedScale * (io.KeyShift ? SprintFactor : 1));
    }

    /** The zoom wheel changes the player speed instead of moving the eye. */
    private void ScalePlayerSpeed(float steps)
    {
        if (steps != 0) playerSpeedScale = Math.Clamp(playerSpeedScale * MathF.Pow(1.25f, steps), MinimumSpeedScale, MaximumSpeedScale);
    }

    private void DrawPlayerControls(Action<float> next)
    {
        float button = ImGui.GetFrameHeight();
        next(button);
        ImGui.BeginDisabled(Camera.HasExternalPose);
        bool player = Camera.Player;
        string toggle = ShortcutInput.Text(ShortcutActions.TogglePlayer3D);
        string look = ShortcutInput.Text(ShortcutActions.Orbit3D);
        string tip = "Player camera\nW A S D, Space, Ctrl: move\nShift: sprint" + (look.Length == 0 ? "" : $"\n{look}: look")
            + "\nWheel: speed" + (toggle.Length == 0 ? "" : $"\n{toggle}: toggle");
        if (ImGuiIcons.Toggle("player-camera", StudioIcon.Player, tip, ref player)) SetPlayerCamera(player);
        var spawns = Spawns;
        if (spawns.Count > 0)
        {
            const float Width = 150;
            next(Width);
            ImGui.SetNextItemWidth(Width);
            if (ImGui.BeginCombo("##teleport-spawn", "Teleport to spawn", ImGuiComboFlags.HeightLarge))
            {
                for (int index = 0; index < spawns.Count; index++)
                    if (ImGui.Selectable($"{Name(spawns[index]) ?? "Spawn point"}##spawn{index}")) TeleportTo(spawns[index]);
                ImGui.EndCombo();
            }
        }
        ImGui.EndDisabled();
        if (PlayerCamera && Math.Abs(playerSpeedScale - 1) > 1e-3f)
        {
            string speed = $"{playerSpeedScale:0.##}x";
            next(ImGui.CalcTextSize(speed).X);
            ImGui.TextDisabled(speed);
        }
    }
}
