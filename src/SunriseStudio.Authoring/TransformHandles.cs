using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

namespace SunriseStudio.Authoring;

/** The world pivot and edit space behind the viewport's move, rotate and scale handles. */
public sealed record TransformHandleFrame(Vector3 Pivot, Matrix4x4 Parent, SceneEditInfo Info);

/** Maps a world-axis handle drag into the selection's own edit space. */
public static class TransformHandles
{
    /**
     * The pivot is the edit position mapped through its parent; it must land on the drawn position.
     * @param reason Why the selection gets no handles; empty when it gets them.
     */
    public static TransformHandleFrame? Read(PackageCatalog catalog, object target, WorldDocument? world, out string reason, IReadOnlyList<ObjectEdit>? edits = null)
    {
        var info = SceneEdits.Describe(catalog, target, world, edits);
        if (!info.CanTransform)
        {
            reason = info.Reason.Length > 0 ? info.Reason : "This selection has no editable transform.";
            return null;
        }
        (Matrix4x4? parent, Vector3? drawn) = target switch
        {
            MeshInstance mesh => (info.CoordinateSpace == "World" ? Matrix4x4.Identity : MeshParent(mesh, world), mesh.Transform.Translation),
            SceneMarker marker => (info.CoordinateSpace == "World" ? Matrix4x4.Identity : marker.PlacementParent, marker.Position),
            _ => ((Matrix4x4?)null, (Vector3?)null)
        };
        if (parent is not { } space || drawn is not { } actual)
        {
            reason = "Use the inspector to move this selection.";
            return null;
        }
        var pivot = Vector3.Transform(info.Position, space);
        // A parent this code cannot rebuild would move the object somewhere other than the drag shows.
        if (Vector3.Distance(pivot, actual) > 1e-3f * MathF.Max(1, actual.Length()))
        {
            reason = "No move handle for this selection; use the inspector.";
            return null;
        }
        reason = "";
        return new(pivot, space, info);
    }

    /**
     * Maps a world change about a pivot into the row's own parent space.
     * Size follows only a uniform scale on a row that stores one; else only the position moves.
     * @param pivot World point to turn and scale about; the frame's own pivot for one object.
     * @return Null when nothing changed.
     */
    public static ObjectEdit? CreateEdit(PackageCatalog catalog, object target, TransformHandleFrame frame, Vector3 pivot,
        TransformChange change, WorldDocument? world, string? name, IReadOnlyList<ObjectEdit>? edits = null)
    {
        if (!Matrix4x4.Invert(frame.Parent, out var inverse)) throw new InvalidDataException("The edit space has no inverse.");
        var moved = change.Apply(frame.Pivot, pivot);
        // An unmoved pivot keeps the stored position exactly instead of a round trip through the parent.
        var position = moved == frame.Pivot ? frame.Info.Position : Vector3.Transform(moved, inverse);
        Quaternion? rotation = change.Turn == Quaternion.Identity ? null : Turned(frame, change.Turn, inverse);
        float scale = frame.Info.CanScale && change.UniformScale ? frame.Info.Scale * change.Scale.X : frame.Info.Scale;
        return SceneEdits.CreateTransformEdit(catalog, target, position, rotation, scale, world, name, edits);
    }

    /** The row's local rotation after a world turn; the parent must not shear it. */
    private static Quaternion Turned(TransformHandleFrame frame, Quaternion turn, Matrix4x4 inverse)
    {
        var stored = frame.Info.Rotation.LengthSquared() > 1e-6f ? Quaternion.Normalize(frame.Info.Rotation) : Quaternion.Identity;
        var local = Matrix4x4.CreateFromQuaternion(stored) * frame.Parent * Matrix4x4.CreateFromQuaternion(turn) * inverse;
        if (!Matrix4x4.Decompose(local, out var size, out var rotation, out _) || Vector3.Distance(size, Vector3.One) > 1e-3f)
            throw new InvalidDataException("This object's parent space cannot turn it without changing its shape.");
        rotation = Quaternion.Normalize(rotation);
        // Keep the stored sign so the row does not flip between q and -q.
        return Quaternion.Dot(rotation, stored) < 0 ? Quaternion.Negate(rotation) : rotation;
    }

    /** Static rows and canopy records keep their visit's parent; a placed model takes its owner's. */
    private static Matrix4x4? MeshParent(MeshInstance mesh, WorldDocument? world)
    {
        if (mesh.InstanceSourceTag != 0 || mesh.ObjectPlacement is not null && mesh.PlacementSourceTag == 0) return mesh.InstanceParent;
        if (mesh.ObjectPlacement is not { } placement || world is null) return null;
        return world.Scene.Markers.FirstOrDefault(marker => ReferenceEquals(marker.ObjectPlacement, placement))?.PlacementParent;
    }
}
