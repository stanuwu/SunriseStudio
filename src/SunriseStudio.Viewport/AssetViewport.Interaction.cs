using System.Numerics;
using ImGuiNET;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

public sealed partial class AssetViewport
{
    private readonly List<int> pickCandidates = [];
    private Func<int, bool>? pickStatic, pickTransient;
    private Func<object, PickFaces>? pickFaces;
    // Per scene item: true when the frame for the pick's size draws it.
    private bool[] pickDrawn = [];
    private bool pickNative;
    // The faces each native part's rasterizer keeps, read from nativeFacesPlan.
    private readonly Dictionary<MeshInstance, PickFaces> nativeFaces = new(ReferenceEqualityComparer.Instance);
    private NativeScenePlan? nativeFacesPlan;
    private double nextHoverTime;
    private ulong gridBoundsRevision;

    public object? HoveredObject { get; private set; }
    public Func<object, string?>? ObjectName { get; set; }

    /** The pick cone includes the screen-space tolerance used by marker hit tests. */
    public object? Pick(Vector2 viewportPixel, Vector2 size) => Pick(viewportPixel, size, out _);

    /**
     * Hits only what a frame of this size draws, as it draws it: culled faces and see-through stages
     * are not hit. Collision and the outliner select by their own rules.
     * @param distance World distance of the hit along the pick ray; infinity when nothing is hit.
     */
    public object? Pick(Vector2 viewportPixel, Vector2 size, out float distance)
    {
        distance = float.PositiveInfinity;
        if (size.X <= 0 || size.Y <= 0 || viewportPixel.X < 0 || viewportPixel.Y < 0 ||
            viewportPixel.X >= size.X || viewportPixel.Y >= size.Y) return null;
        // The frame's own cull, level and foliage tests; an unchanged frame reuses its lists.
        // A pick is not a frame, so the frame's timings stay.
        var timings = LastFrameTimings;
        UpdateVisibility(Math.Max(1, (int)size.X), Math.Max(1, (int)size.Y));
        LastFrameTimings = timings;
        if (pickDrawn.Length < selection.Objects.Count) pickDrawn = new bool[selection.Objects.Count];
        else Array.Clear(pickDrawn);
        foreach (int index in visibleItems) pickDrawn[index] = true;
        pickNative = DecoratorLodInputs().Native;
        if (pickNative && !ReferenceEquals(nativeFacesPlan, nativeScene!.Plan)) ReadNativeFaces(nativeScene.Plan);
        const float tolerancePixels = 12;
        var ndc = new Vector2(viewportPixel.X / size.X * 2 - 1, 1 - viewportPixel.Y / size.Y * 2);
        var crop = Matrix4x4.CreateTranslation(-ndc.X, -ndc.Y, 0) *
            Matrix4x4.CreateScale(size.X / (2 * tolerancePixels), size.Y / (2 * tolerancePixels), 1);
        var planes = SceneGrid.FrustumPlanes(Camera.ViewProjection(size.X / size.Y) * crop);
        pickCandidates.Clear();
        sceneGrid!.Query(planes, Camera.Position, 1, Camera.Orthographic, 0,
            pickStatic ??= index => pickDrawn[index] && !liveIndexSet.Contains(index) && !runtimeIndices.Contains(index)
                && !livePoseIndices.Contains(index), pickCandidates);
        pickTransient ??= index => pickDrawn[index];
        QueryLive(planes, pickTransient, pickCandidates);
        QueryRuntime(planes, pickTransient, pickCandidates);
        QueryLivePose(planes, pickTransient, pickCandidates);
        pickCandidates.Sort();
        return selection.Pick(Camera, viewportPixel, size, pickFaces ??= DrawnFaces, out distance, pickCandidates,
            selectedMemberSet, OccludeOverlays, itemPose);
    }

    /** Native decal and transparent parts write no depth, so the surface behind them is what shows. */
    private PickFaces DrawnFaces(object item)
    {
        if (!pickNative || item is not MeshInstance { Mesh.Native: { } native } mesh) return PickFaces.Both;
        int index = selection.IndexOf(mesh);
        // Fallback parts draw both faces, opaque.
        if (index < 0 || itemKinds[index] != RenderItemKind.NativeMesh
            || mesh.PreviewTransform is not null && !nativeScene!.SupportsLiveTransform(mesh)) return PickFaces.Both;
        return native.RenderStage > 0 ? PickFaces.None : nativeFaces.GetValueOrDefault(mesh, PickFaces.Both);
    }

    /** Runtime parts are not in the plan; they keep both faces. */
    private void ReadNativeFaces(NativeScenePlan plan)
    {
        nativeFaces.Clear();
        foreach (var group in plan.Groups)
        {
            var state = group.Material.States.Rasterizer;
            var faces = state.CullMode switch
            {
                Vortice.Direct3D11.CullMode.Back => state.FrontCounterClockwise ? PickFaces.Back : PickFaces.Front,
                Vortice.Direct3D11.CullMode.Front => state.FrontCounterClockwise ? PickFaces.Front : PickFaces.Back,
                _ => PickFaces.Both
            };
            foreach (var part in group.Parts) nativeFaces[part.Instance] = faces;
        }
        nativeFacesPlan = plan;
    }

    /**
     * Selects the nearer of the object and the shown collision under the pointer. Repeated clicks
     * clear selection; a double-click always selects before framing.
     */
    public void SelectAt(Vector2 viewportPixel, Vector2 size, bool focus = false)
    {
        var item = Pick(viewportPixel, size, out float itemDistance);
        int collision = PickCollision(viewportPixel, size, out float collisionDistance, out int triangle);
        if (collision >= 0 && collisionDistance < itemDistance)
        {
            // Another part of the selected shape moves the pick; the picked part again clears the selection.
            if (!focus && collision == SelectedCollision && SamePart(collision, SelectedCollisionTriangle, triangle)) SelectCollision(-1);
            else { SelectCollision(collision); SelectedCollisionTriangle = triangle; }
            if (focus) FocusSelection();
            return;
        }
        SetSelection(!focus && item is not null && selectedMemberSet.Contains(item) ? null : item);
        if (focus && SelectedObject is not null) FocusSelection();
    }

    public object? HoverAt(Vector2 viewportPixel, Vector2 size)
    {
        var item = Pick(viewportPixel, size);
        return HoveredObject = item is not null && selectionMode == SceneSelectionMode.Object
            ? selection.ResolveObject(item) : item;
    }

    private void EnsureSceneGrid()
    {
        if (!gridDirty && sceneGrid is not null && gridBoundsRevision == selection.AuthoredBoundsRevision) return;
        var bounds = selection.Objects.Select(item =>
        {
            var box = selection.GetAuthoredBounds(item);
            return !itemPose.IsIdentity && SceneVisibility.IsOverlay(item) ? box.Transform(itemPose) : box;
        }).ToArray();
        sceneGrid = new SceneGrid(bounds.Select(x => x.Center).ToArray(), bounds.Select(x => x.Radius).ToArray());
        SceneGridBuildCount++;
        gridBoundsRevision = selection.AuthoredBoundsRevision;
        gridDirty = false;
    }

    /** Hover names use the nearest visible hit; selected names remain visible through geometry. */
    private void DrawObjectNames(ImDrawListPtr draw, Vector2 origin, Vector2 size, bool hovered)
    {
        if (!hovered || dragButton >= 0 || selecting) { HoveredObject = null; HoveredCollision = null; }
        else if (ImGui.GetTime() >= nextHoverTime)
        {
            // Limit exact triangle queries while the mouse crosses dense scenes.
            const double hoverIntervalSeconds = .1;
            nextHoverTime = ImGui.GetTime() + hoverIntervalSeconds;
            HoverAt(ImGui.GetIO().MousePos - origin, size);
            HoverCollision(ImGui.GetIO().MousePos - origin, size);
        }
        if (HoveredCollision is { } collisionText)
            ImGui.SetTooltip(HoveredObject is { } under && Name(under) is { Length: > 0 } underName ? $"{underName}\n{collisionText}" : collisionText);
        else if (HoveredObject is { } hit && Name(hit) is { Length: > 0 } hoveredName)
            ImGui.SetTooltip(hoveredName);
        if (SelectedObject is not { } selected || Name(selected) is not { Length: > 0 } selectedName) return;
        var p = Vector4.Transform(new Vector4(SelectedBounds.Center, 1), Camera.ViewProjection(size.X / size.Y));
        if (p.W <= 0 || p.Z < 0 || p.Z > p.W) return;
        var at = origin + new Vector2(p.X / p.W * .5f + .5f, .5f - p.Y / p.W * .5f) * size + new Vector2(16, 16);
        var padding = new Vector2(4, 2);
        var margin = new Vector2(4);
        // Cap labels to one short line without letting their background cross the image edge.
        float maxWidth = MathF.Min(ImGui.GetFontSize() * 24, size.X - 2 * (padding.X + margin.X));
        if (maxWidth < ImGui.CalcTextSize("...").X || size.Y < ImGui.GetFontSize() + 2 * (padding.Y + margin.Y)) return;
        var textSize = ImGui.CalcTextSize(selectedName);
        if (textSize.X > maxWidth)
        {
            float suffixWidth = ImGui.CalcTextSize("...").X;
            int low = 0, high = selectedName.Length;
            while (low < high)
            {
                int count = (low + high + 1) / 2;
                if (ImGui.CalcTextSize(selectedName[..count]).X + suffixWidth <= maxWidth) low = count;
                else high = count - 1;
            }
            if (low > 0 && char.IsHighSurrogate(selectedName[low - 1])) low--;
            selectedName = selectedName[..low] + "...";
            textSize = ImGui.CalcTextSize(selectedName);
        }
        at = Vector2.Clamp(at, origin + margin + padding, origin + size - margin - padding - textSize);
        draw.AddRectFilled(at - padding, at + textSize + padding,
            ImGui.GetColorU32(new Vector4(.04f, .05f, .06f, .9f)), 3);
        draw.AddText(at, ImGui.GetColorU32(new Vector4(1, .8f, .45f, 1)), selectedName);
    }

    private string? Name(object item) => ObjectName?.Invoke(item) ?? item switch
    {
        MeshInstance mesh => mesh.Name,
        SceneMarker marker => marker.Name,
        ScenePointProxy point => point.Name,
        _ => null
    };
}
