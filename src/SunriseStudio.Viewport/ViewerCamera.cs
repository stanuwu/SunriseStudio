using System.Numerics;

namespace SunriseStudio.Viewport;

/** Z-up orbit camera: the eye sits Distance behind Pivot along Forward. */
public sealed class ViewerCamera
{
    public const float FieldOfView = MathF.PI / 3;
    // Just short of vertical, so the up vector stays defined.
    private const float PitchLimit = MathF.PI / 2 - 0.0005f;

    public Vector3 Pivot { get; private set; }
    public float Distance { get; private set; } = 15;
    public float Yaw { get; private set; } = 2.16f;
    public float Pitch { get; private set; } = -0.35f;
    public bool Orthographic { get; set; }
    /** First-person mode keeps a short fixed pivot distance and a near plane at player scale. */
    public bool Player { get; private set; }
    private float sceneRadius = 10;
    private (Vector3 Eye, Vector3 Forward, Vector3 Up, float HorizontalFov)? externalPose;
    public bool HasExternalPose => externalPose is not null;
    /** Set while an outside source moves the pivot each frame; pan, framing and set-cursor would fight it. */
    public bool PivotFollowed { get; set; }
    /** True when the editor may move the pivot itself. */
    public bool PivotFree => externalPose is null && !PivotFollowed;

    /**
     * The frame Yaw and Pitch turn in: identity for maps, an asset's own axes for a posed model such as a weapon,
     * so the orbit turns about the model's up and the model stays level on screen.
     */
    public Quaternion Basis { get; set; } = Quaternion.Identity;

    public ViewerCamera() => Pivot = Vector3.Zero;

    public Vector3 Forward => externalPose?.Forward ?? Vector3.Transform(new(MathF.Cos(Pitch) * MathF.Cos(Yaw),
        MathF.Cos(Pitch) * MathF.Sin(Yaw), MathF.Sin(Pitch)), Basis);
    public Vector3 Right => externalPose is { } pose ? Vector3.Normalize(Vector3.Cross(pose.Forward, pose.Up))
        : Vector3.Transform(new(MathF.Sin(Yaw), -MathF.Cos(Yaw), 0), Basis);
    public Vector3 Up => externalPose?.Up ?? Vector3.Cross(Right, Forward);
    public Vector3 Position => externalPose?.Eye ?? Pivot - Forward * Distance;
    public float NearPlane => Orthographic ? -FarPlane : Player ? 0.05f : Math.Clamp(Distance * 0.002f, 0.01f, 1f);
    public float FarPlane => MathF.Max(MathF.Max(10000, sceneRadius * 8), Distance * 4);

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Position + Forward, Up);
    public Matrix4x4 Projection(float aspect)
    {
        aspect = MathF.Max(0.01f, aspect);
        if (!Orthographic)
            return Matrix4x4.CreatePerspectiveFieldOfView(VerticalFieldOfView(aspect), aspect, NearPlane, FarPlane);
        float height = ViewHeight;
        return Matrix4x4.CreateOrthographic(height * aspect, height, NearPlane, FarPlane);
    }
    public Matrix4x4 ViewProjection(float aspect) => View * Projection(aspect);

    public float VerticalFieldOfView(float aspect) => externalPose is { } pose
        ? 2 * MathF.Atan(MathF.Tan(pose.HorizontalFov / 2) / MathF.Max(.01f, aspect)) : FieldOfView;

    /** Live poses preserve camera roll and the native horizontal lens angle. */
    public void SetExternalPose(Vector3 eye, Vector3 forward, Vector3 up, float horizontalFov)
    {
        if (!IsFinite(eye) || !IsFinite(forward) || !IsFinite(up) || !float.IsFinite(horizontalFov)
            || forward.LengthSquared() < 1e-6f || Vector3.Cross(forward, up).LengthSquared() < 1e-6f
            || horizontalFov <= 0 || horizontalFov >= MathF.PI)
            throw new ArgumentException("External camera pose is invalid.");
        forward = Vector3.Normalize(forward);
        up = Vector3.Normalize(up - forward * Vector3.Dot(up, forward));
        externalPose = (eye, forward, up, horizontalFov);
        Player = true; Orthographic = false; Distance = 1; Pivot = eye + forward;
    }

    /** Disconnect keeps the last eye position and returns control to the editor. */
    public void ClearExternalPose()
    {
        if (externalPose is not { } pose) return;
        externalPose = null;
        Player = false;
        LookFrom(pose.Eye, pose.Eye + pose.Forward);
    }

    /** World height covered by the view at the pivot. */
    public float ViewHeight => 2 * Distance * MathF.Tan(FieldOfView / 2);

    /** Turntable orbit around the pivot, as in Blender. */
    public void Orbit(float x, float y)
    {
        Yaw -= x * 0.006f;
        Pitch = Math.Clamp(Pitch - y * 0.006f, -PitchLimit, PitchLimit);
    }

    /** Fly look turns around the eye and keeps it in place. */
    public void Look(float x, float y)
    {
        var eye = Position;
        Yaw -= x * 0.004f;
        Pitch = Math.Clamp(Pitch - y * 0.004f, -PitchLimit, PitchLimit);
        Pivot = eye + Forward * Distance;
    }

    /** Moves the pivot so the point under the cursor follows it. */
    public void Pan(float x, float y, int viewportHeight)
    {
        float scale = ViewHeight / Math.Max(1, viewportHeight);
        Pivot += (-Right * x + Up * y) * scale;
    }

    /** Positive steps move toward the pivot. */
    public void Zoom(float steps) => Distance = Math.Clamp(Distance * MathF.Pow(0.85f, steps), 0.01f, 1e6f);

    /**
     * Player movement carries the pivot with the eye.
     * @param local X right, Y along the flat heading, Z straight up; pitch never lifts the eye.
     * @param speed Units per second.
     */
    public void Move(Vector3 local, float seconds, float speed)
    {
        if (local.LengthSquared() > 1) local = Vector3.Normalize(local);
        var heading = new Vector3(MathF.Cos(Yaw), MathF.Sin(Yaw), 0);
        Pivot += (Right * local.X + heading * local.Y + Vector3.UnitZ * local.Z) * speed * seconds;
    }

    public void SetView(float yaw, float pitch)
    {
        Yaw = yaw;
        Pitch = Math.Clamp(pitch, -PitchLimit, PitchLimit);
    }

    /** Frame finite bounds and keep the current view direction. */
    public void Frame(Vector3 min, Vector3 max, float aspect = 1)
    {
        if (!IsFinite(min) || !IsFinite(max)) return;
        float radius = MathF.Max(0.05f, Vector3.Distance(min, max) * 0.5f);
        sceneRadius = MathF.Max(sceneRadius, radius);
        float halfAngle = MathF.Atan(MathF.Tan(FieldOfView / 2) * MathF.Min(1, MathF.Max(.01f, aspect)));
        float fit = radius / MathF.Sin(halfAngle) * 1.1f;
        if (Player)
        {
            // The player eye moves back to the framing point; the pivot stays one unit ahead.
            Pivot = (min + max) * 0.5f - Forward * (fit - Distance);
            return;
        }
        Pivot = (min + max) * 0.5f;
        Distance = fit;
    }

    /** Enters first-person mode at the current eye. */
    public void EnterPlayer()
    {
        var eye = Position;
        Player = true; Orthographic = false;
        Distance = 1;
        Pivot = eye + Forward * Distance;
    }

    /** Leaves first-person mode, orbiting pivot from the current eye. */
    public void EnterEditor(Vector3 pivot)
    {
        var eye = Position;
        Player = false;
        LookFrom(eye, pivot);
    }

    /** Places the eye and faces it toward pivot, which becomes the orbit centre. */
    public void LookFrom(Vector3 eye, Vector3 pivot)
    {
        var offset = Vector3.Transform(pivot - eye, Quaternion.Inverse(Basis));
        float length = offset.Length();
        if (!float.IsFinite(length) || length < 1e-3f) { Pivot = pivot; return; }
        Yaw = MathF.Atan2(offset.Y, offset.X);
        Pitch = Math.Clamp(MathF.Asin(Math.Clamp(offset.Z / length, -1, 1)), -PitchLimit, PitchLimit);
        Pivot = pivot; Distance = length;
    }

    /** Puts the eye at a point with a heading; used for spawn teleports. */
    public void SetEye(Vector3 eye, float yaw, float pitch)
    {
        SetView(yaw, pitch);
        Pivot = eye + Forward * Distance;
    }

    /** Moves the orbit centre and keeps the view direction and distance. */
    public void MovePivot(Vector3 pivot)
    {
        if (!IsFinite(pivot)) throw new ArgumentOutOfRangeException(nameof(pivot));
        Pivot = pivot;
    }

    /** A new scene resets the far-plane reach. */
    public void ResetReach() => sceneRadius = 10;

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
