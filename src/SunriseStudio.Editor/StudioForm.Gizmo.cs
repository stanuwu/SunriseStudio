using SunriseStudio.Authoring;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** Feeds the viewport's handles and records a drag as one undo step, for one object or a group. */
public sealed partial class StudioForm
{
    private object? gizmoTarget;
    private int gizmoRevision = -1;
    private WorldDocument? gizmoWorld;
    private object? gizmoCollision;
    private TransformHandleFrame? gizmoFrame;
    private GroupTransformFrame? groupFrame;
    private string gizmoReason = "";

    /** Call once a frame before the world viewport draws. */
    private void UpdateGizmo()
    {
        if (viewport is not { } view) return;
        view.ShowTransformTools = workspace.World is not null;
        view.GizmoCommitted ??= CommitGizmo;
        string? group = selectedGroupId;
        view.AxisScale = group is not null;
        if (!view.ShowTransformTools || view.Tool == TransformTool.Select || selectedObject is null
            || workspace.Catalog is not { } catalog)
        {
            view.GizmoPivot = null; view.GizmoUnavailableReason = "";
            return;
        }
        bool same = group is not null ? gizmoTarget as string == group : ReferenceEquals(gizmoTarget, selectedObject);
        if (!view.GizmoDragging && (!same || gizmoRevision != workspace.ProjectRevision
            || !ReferenceEquals(gizmoWorld, inspectionWorld) || !ReferenceEquals(gizmoCollision, view.Collision)))
        {
            gizmoTarget = group ?? selectedObject;
            gizmoRevision = workspace.ProjectRevision;
            gizmoWorld = inspectionWorld;
            // A group's collision parts resolve in the loaded collision, which can arrive later.
            gizmoCollision = view.Collision;
            gizmoFrame = null; groupFrame = null;
            if (group is not null) groupFrame = ReadGroupFrame(catalog, group, out gizmoReason);
            else gizmoFrame = TransformHandles.Read(catalog, selectedObject, inspectionWorld, out gizmoReason, workspace.Project?.ObjectEdits);
        }
        // A group member without a scale field still moves, so only one object is refused here.
        string reason = !workspace.CanEdit ? "Create a project to edit"
            : view.Tool == TransformTool.Scale && gizmoFrame is { Info.CanScale: false } ? "This placement has no scale field."
            : gizmoReason;
        view.GizmoUnavailableReason = reason;
        view.GizmoPivot = reason.Length == 0 ? groupFrame?.Pivot ?? gizmoFrame?.Pivot : null;
    }

    private void CommitGizmo(TransformChange change)
    {
        if (selectedGroupId is { } group)
        {
            if (groupFrame is { } members && gizmoTarget as string == group) ApplyGroupChange(group, change, members);
            return;
        }
        if (workspace.Catalog is not { } catalog || gizmoFrame is not { } frame || selectedObject is not { } target
            || !ReferenceEquals(gizmoTarget, target)) return;
        var world = inspectionWorld;
        string? name = objectInspection?.Object.Name;
        workspace.Run(() => workspace.ApplyObjectEdit(
            TransformHandles.CreateEdit(catalog, target, frame, frame.Pivot, change, world, name, workspace.Project?.ObjectEdits)));
    }
}
