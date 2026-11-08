using System.Numerics;
using ImGuiNET;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    // The view whose collision selection the inspector shows, and the rows joined to it.
    private AssetViewport? collisionInspected;
    private (CollisionScene Scene, int Placement, IReadOnlyList<MeshInstance> Rows)? collisionRows;

    /** The view whose collision the Assets menu exports: the world scene, or the open asset. */
    private AssetViewport? CollisionViewport => workspace.World is not null && !assetPageVisible ? viewport : AssetPreviewViewport;

    private bool CanExportCollision => !workspace.IsBusy && CollisionViewport is { Scene: { } scene } view && view.CollisionPossible(scene);

    /** The selected collision placement and its view; null when no collision is selected. */
    public (AssetViewport View, CollisionPlacement Placement)? SelectedCollisionDetails =>
        collisionInspected?.SelectedCollisionPlacement is { } placement ? (collisionInspected, placement) : null;

    /** Loads wait while the workspace works; a project change updates or reloads the collision that is shown. */
    private void PollCollisionLayer()
    {
        bool busy = workspace.IsBusy;
        viewport!.PauseCollisionLoads = busy;
        assetViewport!.PauseCollisionLoads = busy;
        var changes = workspace.TakeCollisionChanges();
        if (changes is { Count: 0 }) return;
        viewport.UpdateCollision(changes);
        assetViewport.UpdateCollision(changes);
    }

    private void OnCollisionSelection(AssetViewport view, int placement)
    {
        if (placement >= 0) { collisionInspected = view; inspectAsset = false; }
        else if (ReferenceEquals(collisionInspected, view)) collisionInspected = null;
        collisionRows = null;
    }

    private void ExportCollision()
    {
        if (!CanExportCollision || CollisionViewport?.Scene is not { } scene) return;
        SaveFile("glTF binary|*.glb|glTF scene|*.gltf|Wavefront mesh|*.obj", $"collision-{scene.TagHash:X8}.glb",
            path => workspace.ExportCollision(path, scene));
    }

    private void ImportCollision()
    {
        if (!workspace.CanEdit || CollisionViewport?.Scene is not { } scene) return;
        PickFile("Collision glTF|*.glb;*.gltf", path => workspace.ImportCollision(path, scene));
    }

    /** Kind, triangles, compound, instance, source and the static instance rows the placement copies. */
    private bool DrawCollisionInspector()
    {
        if (collisionInspected is not { } view || view.SelectedCollisionPlacement is not { } placement
            || view.Collision is not { } scene) return false;
        ImGui.PushStyleColor(ImGuiCol.Button, !inspectAsset ? ImGui.GetStyle().Colors[(int)ImGuiCol.Header]
            : ImGui.GetStyle().Colors[(int)ImGuiCol.Button]);
        // The mode buttons share labels with asset sections, so they carry their own IDs.
        if (ImGui.SmallButton("Collision##inspector-mode")) inspectAsset = false;
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, inspectAsset ? ImGui.GetStyle().Colors[(int)ImGuiCol.Header]
            : ImGui.GetStyle().Colors[(int)ImGuiCol.Button]);
        if (ImGui.SmallButton("Asset##inspector-mode")) inspectAsset = true;
        ImGui.PopStyleColor();
        if (inspectAsset) return false;
        var piece = scene.Pieces[placement.Piece];
        var entity = scene.Entities[placement.Entity];
        ImGui.TextWrapped(scene.Describe(placement));
        ImGui.TextDisabled("Collision");
        var (drawnMin, drawnMax) = view.DrawnCollisionBounds(view.SelectedCollision);
        var size = drawnMax - drawnMin;
        ImGui.TextDisabled($"{size.X:0.##} x {size.Y:0.##} x {size.Z:0.##} m");
        ImGui.BeginDisabled(!view.Camera.PivotFree);
        if (ImGuiIcons.Button("collision-frame", StudioIcon.Focus, FrameTooltip)) { view.FocusSelection(); focusWorldScene = true; }
        ImGui.SameLine();
        if (ImGuiIcons.Button("collision-cursor", StudioIcon.Cursor, "Set cursor to selection")) { view.SetCursorToSelection(); focusWorldScene = true; }
        ImGui.EndDisabled();
        var facts = new List<(string, string, string)>
        {
            ("Kind", piece.Kind, ""),
            ("Role", AssetViewport.RoleText(placement), "")
        };
        if (entity.Source == CollisionSource.EntityPhysics)
            facts.Add(("Initial state", placement.InitiallyEnabled is { } enabled ? enabled ? "Enabled" : "Disabled (authored)"
                : entity.InitialStateError ?? "Unresolved", ""));
        if (placement.Region is not (0 or StringHash.Empty)) facts.Add(("Region", $"0x{placement.Region:X8}", "Region hash"));
        if (placement.Bone >= 0) facts.Add(("Bone", placement.Bone.ToString(), ""));
        facts.Add(("Triangles", piece.Shape.TriangleCount.ToString("N0"), ""));
        if (piece.Shape.Undecoded is { } missing) facts.Add(("Not decoded", missing, ""));
        if (placement.Instance >= 0) facts.Add(("Instance", placement.Instance.ToString(), ""));
        facts.Add(("Source", AssetViewport.CollisionSourceText(entity), ""));
        if (entity.ListRow >= 0) facts.Add(("List row", $"{entity.ListTag:X8} + {entity.ListRow:X}", "Object list row"));
        var t = placement.Local;
        facts.Add(("Local", $"{t.Translation.X:0.###}, {t.Translation.Y:0.###}, {t.Translation.Z:0.###}",
            $"t ({t.Translation.X:0.###}, {t.Translation.Y:0.###}, {t.Translation.Z:0.###})\n"
            + $"q ({t.Rotation.X:0.###}, {t.Rotation.Y:0.###}, {t.Rotation.Z:0.###}, {t.Rotation.W:0.###})\ns {t.Scale.X:0.###}"));
        var at = view.DrawnCollisionWorld(view.SelectedCollision).Translation;
        facts.Add(("World position", $"{at.X:0.###}, {at.Y:0.###}, {at.Z:0.###}", ""));
        StudioUi.Facts("collision-facts", facts);
        ImGui.TextDisabled("Shape");
        if (ImGui.Selectable($"{workspace.DisplayName(piece.Tag)}##collision-shape")) OpenObjectAsset(piece.Tag);
        if (placement.Instance >= 0)
        {
            ImGui.TextDisabled("Compound");
            if (ImGui.Selectable($"{workspace.DisplayName(placement.CompoundTag)}##collision-compound")) OpenObjectAsset(placement.CompoundTag);
        }
        DrawCollisionParts(view, scene, placement);
        if (view.Scene is { } source && entity.Source == CollisionSource.StaticEntity && placement.Instance >= 0)
        {
            int index = view.SelectedCollision;
            if (collisionRows is not { } cached || !ReferenceEquals(cached.Scene, scene) || cached.Placement != index)
                collisionRows = cached = (scene, index, CollisionJoin.StaticRows(source, scene, placement));
            if (ImGui.CollapsingHeader("Static instance", ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (cached.Rows.Count == 0) ImGui.TextDisabled("No placed row at this transform");
                foreach (var row in cached.Rows)
                {
                    ImGui.PushID($"{row.InstanceSourceTag:X8}:{row.InstanceSourceIndex}:{row.PlacementSourceTag:X8}:{row.PlacementSourceOffset}");
                    string where = $"Table {row.InstanceSourceTag:X8} row {row.InstanceSourceIndex}";
                    if (ImGui.SmallButton("Select")) SelectCollisionRow(view, row);
                    StudioUi.Tip(where);
                    ImGui.SameLine();
                    ImGui.TextUnformatted(row.Name.Length > 0 ? row.Name : $"Row {row.InstanceSourceIndex}");
                    StudioUi.Tip(where);
                    ImGui.PopID();
                }
            }
        }
        return true;
    }

    /**
     * Parts are the separate pieces of one mesh, such as the leaf hulls of a tree in a map mesh.
     * Chosen parts can span shapes; the edit moves, turns and scales them about a world pivot.
     */
    private void DrawCollisionParts(AssetViewport view, CollisionScene scene, CollisionPlacement placement)
    {
        var shape = scene.Pieces[placement.Piece].Shape;
        if (shape.TriangleCount == 0) return;
        if (!ImGui.CollapsingHeader("Parts", ImGuiTreeNodeFlags.DefaultOpen)) return;
        var parts = CollisionParts.Of(shape);
        int picked = view.SelectedCollisionTriangle is >= 0 and var triangle && triangle < parts.PartOfTriangle.Length
            ? parts.PartOfTriangle[triangle] : -1;
        ImGui.TextDisabled($"{parts.Triangles.Count:N0} parts in this shape");
        if (picked >= 0)
        {
            ImGui.TextWrapped($"Picked part: {parts.Triangles[picked].Length} triangles"
                + (parts.IsPrimitive(shape, picked) ? ", a convex piece" : ""));
            bool chosen = view.SelectedCollisionParts.Contains(picked);
            if (ImGui.Button(chosen ? "Remove picked part" : "Add picked part", new(-1, 0))) view.ToggleCollisionPart(picked);
        }
        else ImGui.TextDisabled("Click a triangle to pick a part");
        DrawCollisionConnected(view, scene);
        var selected = view.CollisionPartSelection;
        if (selected.Count == 0) return;
        // The viewport keeps one list per selection; the summary is rebuilt when it changes.
        if (partSummary is not { } summary || !ReferenceEquals(summary.Parts, selected) || !ReferenceEquals(summary.Scene, scene))
        {
            partSummary = summary = SummarizeParts(view, scene, selected);
            // New parts turn and scale about their own centre, not the world origin.
            partPivot = (summary.Min + summary.Max) * .5f;
        }
        var (min, max) = (summary.Min, summary.Max);
        ImGui.TextUnformatted($"{selected.Count:N0} parts, {summary.Shapes:N0} shapes, {summary.Triangles:N0} tris");
        StudioUi.Tip($"Box {max.X - min.X:0.##} x {max.Y - min.Y:0.##} x {max.Z - min.Z:0.##} m");
        if (ImGui.SmallButton("Clear parts")) { view.ClearCollisionParts(); return; }
        DrawCollisionGroupPicker(view);
        if (summary.Others > 0)
        {
            ImGui.TextColored(StudioTheme.Accent, $"Also changes {summary.Others} other placements");
            StudioUi.Tip("They use the same shapes.");
        }
        string? refusal = summary.Refusal;
        DrawAccentNote(refusal ?? "");
        ImGui.InputFloat3("Move", ref partMove);
        ImGui.InputFloat3("Rotate (deg)", ref partRotate);
        ImGui.InputFloat("Scale", ref partScale);
        ImGui.InputFloat3("Pivot", ref partPivot);
        if (ImGui.SmallButton("Pivot at parts center")) partPivot = (min + max) * .5f;
        ImGui.SameLine();
        if (ImGui.SmallButton("Pivot at cursor")) partPivot = view.Camera.Pivot;
        bool valid = float.IsFinite(partScale) && partScale > 0;
        ImGui.BeginDisabled(!valid || !workspace.CanEdit || refusal is not null);
        if (ImGui.Button("Apply to parts", new(-1, 0)))
        {
            const float degrees = MathF.PI / 180;
            var turn = Matrix4x4.CreateFromYawPitchRoll(partRotate.Y * degrees, partRotate.X * degrees, partRotate.Z * degrees);
            var transform = Matrix4x4.CreateTranslation(-partPivot) * Matrix4x4.CreateScale(partScale) * turn
                * Matrix4x4.CreateTranslation(partPivot + partMove);
            var chosen = selected.ToArray();
            workspace.Run(() => workspace.EditCollisionParts(scene, chosen, transform));
            // A refused edit keeps the parts; a started one clears them, as hulls renumber parts.
            if (workspace.Error.Length == 0)
            {
                partMove = Vector3.Zero; partRotate = Vector3.Zero; partScale = 1;
                view.ClearCollisionParts();
            }
        }
        ImGui.EndDisabled();
    }

    // Part edit inputs; they reset after each apply.
    private Vector3 partMove, partRotate, partPivot;
    private float partScale = 1;
    private PartSummary? partSummary;

    /** The chosen parts' shapes, triangles, other placements, box and edit refusal. */
    private sealed record PartSummary(IReadOnlyList<CollisionPartRef> Parts, CollisionScene Scene, int Shapes, int Triangles, int Others,
        Vector3 Min, Vector3 Max, string? Refusal);

    private static PartSummary SummarizeParts(AssetViewport view, CollisionScene scene, IReadOnlyList<CollisionPartRef> selected)
    {
        var placements = selected.Select(part => part.Placement).ToHashSet();
        int triangles = selected.Sum(part =>
        {
            var shape = scene.Pieces[scene.Placements[part.Placement].Piece].Shape;
            return CollisionParts.Of(shape).Triangles[part.Part].Length;
        });
        var tags = placements.Select(index => scene.Pieces[scene.Placements[index].Piece].Tag).ToHashSet();
        int others = scene.Placements.Where((other, index) => tags.Contains(scene.Pieces[other.Piece].Tag) && !placements.Contains(index)).Count();
        var (min, max) = view.CollisionPartBounds();
        return new(selected, scene, placements.Count, triangles, others, min, max, CollisionPartEdit.Refusal(scene, selected));
    }
    // Select connected inputs; the user's scope is kept separately for physics bodies and for static meshes.
    private readonly Dictionary<CollisionConnectScope, CollisionConnectScope> connectScopes = [];
    private float connectGap = new CollisionConnectOptions().Gap;

    /** Select connected grows the chosen parts over touching and nearby parts. */
    private void DrawCollisionConnected(AssetViewport view, CollisionScene collision)
    {
        string[] scopes = ["This shape", "This entity", "Touching shapes"];
        var kind = CollisionConnectivity.DefaultScope(collision, view.SelectedCollision);
        var connectScope = connectScopes.GetValueOrDefault(kind, kind);
        int scope = (int)connectScope;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##connect-scope", ref scope, scopes, scopes.Length)) connectScopes[kind] = connectScope = (CollisionConnectScope)scope;
        StudioUi.Tip("This shape: the picked placement\nThis entity: its shown shapes\nTouching shapes: any shown shape that touches");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputFloat("##connect-gap", ref connectGap, .01f, .1f, "Gap %.3f m");
        connectGap = float.IsFinite(connectGap) ? MathF.Max(0, connectGap) : 0;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Parts nearer than this count as connected");
        ImGui.BeginDisabled(view.CollisionConnecting);
        if (ImGui.Button(view.CollisionConnecting ? "Searching..." : "Select connected", new(-1, 0)))
            view.SelectConnectedCollision(new(connectGap, connectScope));
        ImGui.EndDisabled();
        if (view.LastCollisionConnect is ({ } result, double milliseconds))
        {
            ImGui.TextDisabled($"{result.Parts.Count:N0} connected parts");
            StudioUi.Tip($"{milliseconds:N0} ms"
                + (result.SkippedPlacements > 0 ? $"\n{result.SkippedPlacements:N0} shapes without triangles skipped" : "")
                + (result.Truncated ? "\nStopped at the limit" : ""));
        }
    }

    /** Add to group saves the chosen parts in a project group of the open map. */
    private void DrawCollisionGroupPicker(AssetViewport view)
    {
        // Groups resolve part keys in the world view, so only parts chosen there can be kept.
        string? reason = !ReferenceEquals(view, viewport) || workspace.World is null ? "Open a map to use groups"
            : workspace.Project is null ? "Open a project to use groups" : null;
        var project = workspace.Project;
        ImGui.BeginDisabled(reason is not null || !workspace.CanEdit);
        ImGui.SetNextItemWidth(-1);
        bool open = ImGui.BeginCombo("##collision-group", "Add to group");
        if (reason is not null) StudioUi.Tip(reason);
        if (open)
        {
            foreach (var group in project?.ObjectGroups ?? [])
            {
                ImGui.PushID(group.Id);
                if (ImGui.Selectable(group.Name)) AddCollisionPartsToGroup(view, group);
                ImGui.PopID();
            }
            if (ImGui.Selectable("New group")) AddCollisionPartsToGroup(view, null);
            ImGui.EndCombo();
        }
        ImGui.EndDisabled();
        if (reason is null) DrawAccentNote(groupPartsNote);
    }

    /** One accent line with the first sentence; the full text is the tooltip. */
    private static void DrawAccentNote(string note)
    {
        if (note.Length == 0) return;
        ImGui.TextColored(StudioTheme.Accent, FirstSentence(note));
        StudioUi.Tip(note);
    }

    /** The joined render row becomes the selection, as a tree or viewport pick would make it. */
    private void SelectCollisionRow(AssetViewport view, MeshInstance row)
    {
        if (ReferenceEquals(view, viewport) && inspectionWorld?.Find(row) is { } worldRow) SelectWorldObject(worldRow);
        else view.SetSelection(row);
    }

    /** Counts, load time, content without triangles, and every load note. */
    private static void DrawCollisionReport(AssetViewport view)
    {
        if (!ImGui.CollapsingHeader("Collision")) return;
        if (view.CollisionStatus.Length > 0)
        {
            ImGui.TextDisabled(FirstSentence(view.CollisionStatus));
            StudioUi.Tip(view.CollisionStatus);
        }
        if (view.Collision is not { } scene)
        {
            if (view.CollisionStatus.Length == 0)
            {
                ImGui.TextDisabled("Not loaded");
                StudioUi.Tip("Show the Collision layer.");
            }
            return;
        }
        var facts = new List<(string, string, string)>
        {
            ("Entities", $"{scene.Entities.Count:N0}", ""),
            ("Placed shapes", $"{scene.Placements.Count:N0}", ""),
            ("Distinct shapes", $"{scene.Pieces.Count:N0}", ""),
            ("Placed triangles", $"{scene.PlacedTriangles:N0}", ""),
            ("Distinct triangles", $"{scene.DistinctTriangles:N0}", ""),
            ("Load time", $"{view.CollisionLoadMilliseconds:N0} ms", "")
        };
        int inactive = scene.Placements.Count(placement => placement.InitiallyEnabled == false);
        if (inactive > 0) facts.Add(("Disabled bodies", $"{inactive:N0}", "Initially disabled; kept in the scene"));
        if (view.LastCollisionRefresh is { } refresh)
            facts.Add(refresh.InPlace
                ? ("Last change", $"In place, {refresh.Milliseconds:N0} ms",
                    $"{refresh.MovedPlacements:N0} moved, {refresh.ChangedShapes:N0} shapes")
                : ("Last change", $"Reloaded, {refresh.Milliseconds:N0} ms", refresh.Reason));
        StudioUi.Facts("collision-report", facts);
        var notes = scene.Entities.Select(entity => entity.InitialStateError).OfType<string>().Distinct().Take(8)
            .Select(reason => $"Initial state: {reason}")
            .Concat(scene.Undecoded.Select(pair => $"No triangles: {pair.Key} ({pair.Value:N0})"))
            .Concat(scene.Notes.Take(100)).ToList();
        if (notes.Count > 0 && ImGui.TreeNode($"Notes ({notes.Count})###collision-notes"))
        {
            StudioUi.Facts("collision-notes", notes);
            ImGui.TreePop();
        }
    }
}
