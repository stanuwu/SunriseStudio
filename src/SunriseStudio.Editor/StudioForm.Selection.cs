using System.Numerics;
using ImGuiNET;
using SunriseStudio.Authoring;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    private WorldObjectInspection? objectInspection;
    private SceneEditInfo? sceneEditInfo;
    private object? editTarget;
    private Vector3 editPosition, editRotation, editRotationBase, editMinimum, editMaximum;
    private float editScale = 1;
    private bool expandObjectDetails;

    /** Names the current Frame selection key, which the user can remap. */
    private static string FrameTooltip => ShortcutInput.Text(ShortcutActions.FrameSelection3D) is { Length: > 0 } key
        ? $"Frame selection ({key})" : "Frame selection";

    private bool DrawSelectedObjectInspector()
    {
        if (selectedObject is null) return false;
        ImGui.PushStyleColor(ImGuiCol.Button, !inspectAsset ? ImGui.GetStyle().Colors[(int)ImGuiCol.Header]
            : ImGui.GetStyle().Colors[(int)ImGuiCol.Button]);
        // The mode buttons share labels with property groups below, so they carry their own IDs.
        if (ImGui.SmallButton("Object##inspector-mode")) inspectAsset = false;
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, inspectAsset ? ImGui.GetStyle().Colors[(int)ImGuiCol.Header]
            : ImGui.GetStyle().Colors[(int)ImGuiCol.Button]);
        if (ImGui.SmallButton("Asset##inspector-mode")) inspectAsset = true;
        ImGui.PopStyleColor();
        if (inspectAsset) return false;
        if (DrawRuntimeObjectInspector()) return true;
        if (inspectionWorld is null) return false;
        objectInspection ??= inspectionWorld.Inspect(selectedObject);
        if (objectInspection is not { } inspection) return false;
        var row = inspection.Object;
        if (ProjectName(row) is { } named) { ImGui.TextWrapped(named); ImGui.TextDisabled(row.Name); }
        else ImGui.TextWrapped(row.Name);
        DrawObjectNameField(row);
        DrawGroupMembership();
        ImGui.TextDisabled(selectedObject is MeshInstance && workspace.World is not null ? "Placement" : row.Kind.ToString());
        if (selectedObject is MeshInstance && workspace.World is not null)
        {
            ImGui.TextUnformatted($"{viewport!.SelectedMeshParts.Count:N0} mesh parts");
            var bounds = viewport.SelectedBounds;
            if (bounds.IsFinite)
            {
                var size = bounds.Maximum - bounds.Minimum;
                ImGui.TextDisabled($"{size.X:0.##} x {size.Y:0.##} x {size.Z:0.##} m");
            }
        }
        ImGui.TextWrapped(row.BubbleName);
        if (row.StateName.Length != 0) ImGui.TextWrapped(row.StateName);
        bool hasPoint = viewport!.SelectedObject is { } selectedPoint && viewport.Selection.GetBounds(selectedPoint).IsFinite;
        ImGui.BeginDisabled(!hasPoint || !viewport.Camera.PivotFree);
        if (ImGuiIcons.Button("frame-selection", StudioIcon.Focus, FrameTooltip))
        { viewport.FocusSelection(); focusWorldScene = true; }
        ImGui.SameLine();
        if (ImGuiIcons.Button("selection-cursor", StudioIcon.Cursor, "Set cursor to selection")) { viewport.SetCursorToSelection(); focusWorldScene = true; }
        ImGui.EndDisabled(); ImGui.BeginDisabled(!hasPoint);
        ImGui.SameLine();
        if (ImGuiIcons.Button("isolate-selection", StudioIcon.Filter, "Isolate selection", isolatedObject is not null))
        {
            isolatedObject = selectedObject;
            isolatedMembers.Clear();
            foreach (var member in viewport.SelectedMembers) isolatedMembers.Add(member);
            isolatedMembers.Add(selectedObject);
            focusWorldScene = true;
        }
        ImGui.SameLine();
        if (ImGuiIcons.Button("clear-isolation", StudioIcon.Eye, "Show all objects")) isolatedObject = null;
        ImGui.EndDisabled();
        if (row.AssetTag != 0 && ImGui.Button("Open asset", new(-1, 0))) OpenObjectAsset(row.AssetTag);
        if (livePage.Observed(selectedObject) is { } observed)
        {
            bool holding = !observed.TransformAvailable && (selectedObject is MeshInstance { PreviewTransform: not null }
                or SceneMarker { PreviewPose: not null });
            DrawObservedPlacement(observed, holding); ImGui.SeparatorText("Authored placement");
        }
        DrawSpawnPointActions(selectedObject as SceneMarker);
        DrawPlacementEditor();
        if (selectedObject is SceneMarker { Effect: not null } effectMarker && workspace.World?.Scene is { } effectScene)
            DrawEffectControls(effectScene, effectMarker);
        ImGui.Spacing();
        ImGui.Checkbox("Expand details", ref expandObjectDetails);
        foreach (var group in inspection.Properties.GroupBy(property => property.Group))
        {
            if (expandObjectDetails) ImGui.SetNextItemOpen(true);
            if (!ImGui.CollapsingHeader($"{group.Key}##property-group")) continue;
            var facts = new List<(string, string, string)>();
            foreach (var property in group)
            {
                facts.Add((property.Name, property.Value, ""));
                if (property is { Group: "Renderer", Name: "Stage" } && row.Source is MeshInstance { Mesh.Native: { } native })
                    facts.Add(("Blend and depth", NativePartStates.Label(native), ""));
            }
            StudioUi.Facts($"property-facts-{group.Key}", facts);
        }
        if (inspection.References.Count != 0 && ImGui.CollapsingHeader("References", ImGuiTreeNodeFlags.DefaultOpen))
            foreach (var reference in inspection.References)
            {
                ImGui.PushID($"{reference.Name}:{reference.TagHash}:{reference.Offset}");
                ImGui.TextDisabled(reference.Name);
                if (ImGui.Selectable(workspace.DisplayName(reference.TagHash))) OpenObjectAsset(reference.TagHash);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(reference.Offset < 0 ? $"{reference.TagHash:X8}"
                    : $"{reference.TagHash:X8} + {reference.Offset:X}");
                ImGui.PopID();
            }
        if (inspection.Slots.Count != 0 && ImGui.CollapsingHeader("Mission slots", ImGuiTreeNodeFlags.DefaultOpen))
            StudioUi.Facts("mission-slots", inspection.Slots.Select(slot => (
                workspace.ResolveHash(slot.NameHash) ?? slot.Name, $"type {slot.Identity.Type}, slot {slot.Identity.Index}", "")));
        if (inspection.RelatedObjects.Count != 0 && ImGui.CollapsingHeader("Related objects"))
            foreach (var related in inspection.RelatedObjects)
                if (inspectionWorld.Find(related) is { } relatedRow)
                {
                    ImGui.PushID(relatedRow.Id);
                    if (ImGui.Selectable(relatedRow.Name)) SelectWorldObject(relatedRow);
                    ImGui.PopID();
                }
        if (row.Owners.Count != 0 && ImGui.CollapsingHeader("State ownership"))
            foreach (var owner in row.Owners)
            {
                var state = inspectionWorld.AllStates.FirstOrDefault(state => state.BubbleNameHash == owner.BubbleHash
                    && state.Ordinal == owner.Ordinal);
                ImGui.TextWrapped($"{BubbleTitle(owner.BubbleHash, row.BubbleName)} / {state?.Name ?? $"State {owner.Ordinal}"}");
            }
        DrawNameEvidence(row.AssetTag);
        return true;
    }

    /** Only proved placement fields are editable. Apply records one object edit; the build derives the rest. */
    private void DrawPlacementEditor()
    {
        if (workspace.Catalog is not { } catalog || selectedObject is null) return;
        if (!ReferenceEquals(editTarget, selectedObject))
        {
            // The catalog shows compiled project bytes, so these start from the current edited state.
            editTarget = selectedObject;
            sceneEditInfo = SceneEdits.Describe(catalog, selectedObject, inspectionWorld, workspace.Project?.ObjectEdits);
            editPosition = sceneEditInfo.Position;
            editRotation = editRotationBase = QuaternionToDegrees(sceneEditInfo.Rotation);
            editScale = sceneEditInfo.Scale;
            editMinimum = sceneEditInfo.Minimum; editMaximum = sceneEditInfo.Maximum;
        }
        if (sceneEditInfo is not { } info) return;
        if (!info.CanTransform && !info.CanBounds)
        {
            ImGui.TextDisabled("Placement read only");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(info.Reason);
            return;
        }
        if (!ImGui.CollapsingHeader($"{info.CoordinateSpace} placement", ImGuiTreeNodeFlags.DefaultOpen)) return;
        ImGui.BeginDisabled(!workspace.CanEdit);
        if (info.CanTransform)
        {
            ImGui.TextDisabled("Position"); ImGui.SetNextItemWidth(-1); ImGui.InputFloat3("##position", ref editPosition, "%.3f");
            ImGui.TextDisabled("Rotation"); ImGui.SetNextItemWidth(-1); ImGui.InputFloat3("##rotation", ref editRotation, "%.2f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("XYZ degrees");
            if (info.CanScale)
            { ImGui.TextDisabled("Scale"); ImGui.SetNextItemWidth(-1); ImGui.InputFloat("##scale", ref editScale, 0, 0, "%.4f"); }
            if (ImGui.Button("Apply transform", new(-1, 0))) workspace.Run(() =>
                workspace.ApplyObjectEdit(SceneEdits.CreateTransformEdit(catalog, selectedObject, editPosition,
                    editRotation == editRotationBase ? null : DegreesToQuaternion(editRotation), editScale, inspectionWorld,
                    objectInspection?.Object.Name, workspace.Project?.ObjectEdits)));
        }
        if (info.CanBounds)
        {
            ImGui.TextDisabled("Minimum"); ImGui.SetNextItemWidth(-1); ImGui.InputFloat3("##minimum", ref editMinimum, "%.3f");
            ImGui.TextDisabled("Maximum"); ImGui.SetNextItemWidth(-1); ImGui.InputFloat3("##maximum", ref editMaximum, "%.3f");
            if (ImGui.Button("Apply bounds", new(-1, 0))) workspace.Run(() =>
                workspace.ApplyObjectEdit(SceneEdits.CreateBoundsEdit(catalog, selectedObject, editMinimum, editMaximum,
                    inspectionWorld, objectInspection?.Object.Name)));
        }
        if (info.Target is { } target && workspace.Project?.FindObjectEdit(target.Key) is { } saved)
        {
            ImGui.TextColored(StudioTheme.Accent, $"Edited: {saved.Summary()}");
            if (ImGui.Button(saved.Target.Layout == ObjectEditLayout.AddedSpawnPoint ? "Remove added spawn point" : "Revert object", new(-1, 0))) workspace.Run(() => workspace.RevertObjectEdit(saved.Key));
        }
        ImGui.EndDisabled();
        if (workspace.Project is null) ImGui.TextDisabled("Create a project to edit");
    }

    /** A Changes row selects its object in the open world; with no world, or no match, it opens the source asset. */
    private void SelectEditedObject(ObjectEdit edit)
    {
        if (FindEditedSource(edit.Target) is { } source && inspectionWorld?.Find(source) is { } row)
        {
            SelectWorldObject(row);
            focusWorldScene = true;
            return;
        }
        workspace.Open(edit.Target.SourceTag);
    }

    /** The open world's scene object for an edit target; null without a world or a match. */
    private object? FindEditedSource(ObjectEditTarget target)
    {
        if (workspace.World is not { } world) return null;
        var scene = world.Scene;
        return target.Layout switch
        {
            ObjectEditLayout.StaticInstance => scene.Instances.FirstOrDefault(item =>
                item.InstanceSourceTag == target.SourceTag && item.InstanceSourceIndex == target.Index),
            ObjectEditLayout.Light => world.Objects.FirstOrDefault(row => row.Source is WorldLight light
                && light.Owner.Table.TagHash == target.SourceTag && light.Row.TransformOffset == target.Offset)?.Source,
            ObjectEditLayout.AddedSpawnPoint => scene.Markers.FirstOrDefault(marker => marker.SourceTag == target.SourceTag
                && marker.SourceOffset == SpawnPointAuthoring.Offset(workspace.Catalog!, target, workspace.Project!.ObjectEdits)),
            ObjectEditLayout.CollisionInstance => null,
            ObjectEditLayout.DecoratorInstance => scene.Instances.FirstOrDefault(item =>
                item.ObjectPlacement?.SourceTag == target.Identity && item.ObjectPlacement.SourceIndex == target.Index),
            _ => (object?)scene.Markers.FirstOrDefault(marker => marker.SourceTag == target.SourceTag
                    && marker.SourceOffset == target.Offset)
                ?? scene.Instances.FirstOrDefault(item => item.PlacementSourceTag == target.SourceTag
                    && item.PlacementSourceOffset == target.Offset)
        };
    }

    private void OpenPartMaterial(MeshInstance part) =>
        OpenContextAsset(part.Mesh.MaterialTag, AssetOpenContext.ForObject(part));

    private void OpenObjectAsset(uint tag) => OpenContextAsset(tag, AssetOpenContext.ForObject(selectedObject));

    /**
     * Related assets retain the owner of the view that opened them.
     * @param tag Referenced asset tag.
     * @param context Owner evidence from the opening view.
     */
    private void OpenContextAsset(uint tag, AssetOpenContext? context)
    {
        if (workspace.IsBusy) return;
        workspace.Select(tag, context: context);
        inspectAsset = true;
        focusAssets = true;
        focusAssetPage = workspace.World is not null;
    }

    private void DrawNameEvidence(uint tag)
    {
        if (workspace.Names is not { } names || !ImGui.CollapsingHeader("Name source")) return;
        var info = names.Lookup(tag);
        var facts = new List<(string, string, string)>
        {
            ("Source", $"{info.Source}", ""),
            ("Confidence", info.IsAmbiguous ? $"{info.Confidence}, several names" : $"{info.Confidence}",
                info.IsAmbiguous ? "Multiple authored names" : ""),
            ("Detail", info.Detail, "")
        };
        if (info.Candidates.Count > 0)
            facts.Add(("Other names", $"{info.Candidates.Count}",
                string.Join("\n", info.Candidates.Take(12).Select(candidate => candidate.Text))));
        facts.Add(("Tag", $"{tag:X8}", ""));
        StudioUi.Facts("name-source", facts);
    }

    private static Vector3 QuaternionToDegrees(Quaternion value)
    {
        var q = Quaternion.Normalize(value);
        return new Vector3(
            MathF.Atan2(2 * (q.W * q.X + q.Y * q.Z), 1 - 2 * (q.X * q.X + q.Y * q.Y)),
            MathF.Asin(Math.Clamp(2 * (q.W * q.Y - q.Z * q.X), -1, 1)),
            MathF.Atan2(2 * (q.W * q.Z + q.X * q.Y), 1 - 2 * (q.Y * q.Y + q.Z * q.Z))) * (180 / MathF.PI);
    }

    private static Quaternion DegreesToQuaternion(Vector3 value)
    {
        var half = value * (MathF.PI / 360);
        float sx = MathF.Sin(half.X), cx = MathF.Cos(half.X), sy = MathF.Sin(half.Y), cy = MathF.Cos(half.Y);
        float sz = MathF.Sin(half.Z), cz = MathF.Cos(half.Z);
        return Quaternion.Normalize(new(sx * cy * cz - cx * sy * sz, cx * sy * cz + sx * cy * sz,
            cx * cy * sz - sx * sy * cz, cx * cy * cz + sx * sy * sz));
    }
}
