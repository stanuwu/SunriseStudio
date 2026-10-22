using System.Numerics;
using SunriseStudio.Rendering;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Viewport;

public sealed partial class AssetViewport
{
    // Outline meshes by placement, rebuilt when the shape or the placement's stamp changes.
    private readonly Dictionary<int, (GpuMesh Mesh, CollisionShape Shape, int Stamp)> collisionOutlines = [];
    // A new stamp per placement whose chosen parts or picked part change.
    private readonly Dictionary<int, int> outlineStamps = [];
    private int outlineStamp;

    private void TouchOutline(int placement) => outlineStamps[placement] = ++outlineStamp;

    /** Index of the selected placement in Collision.Placements, or -1. It excludes an object selection. */
    public int SelectedCollision { get; private set; } = -1;

    public CollisionPlacement? SelectedCollisionPlacement =>
        SelectedCollision >= 0 && collisionView is { } view ? view.Scene.Placements[SelectedCollision] : null;

    /** Triangle of the selected placement's shape that the last click hit; -1 when unknown. */
    public int SelectedCollisionTriangle
    {
        get => selectedCollisionTriangle;
        private set { selectedCollisionTriangle = value; if (SelectedCollision >= 0) TouchOutline(SelectedCollision); }
    }
    private int selectedCollisionTriangle = -1;

    // Parts chosen for a part edit, by placement.
    private readonly Dictionary<int, HashSet<int>> collisionPartSets = [];
    private static readonly HashSet<int> NoParts = [];
    private int collisionPartsRevision;
    private (int Revision, IReadOnlyList<CollisionPartRef> Parts) collisionPartList = (-1, []);

    /** The selected placement's chosen parts. Selecting a placement without any clears them all. */
    public IReadOnlySet<int> SelectedCollisionParts => collisionPartSets.GetValueOrDefault(SelectedCollision, NoParts);

    /** Every chosen part, sorted by placement and part. */
    public IReadOnlyList<CollisionPartRef> CollisionPartSelection
    {
        get
        {
            if (collisionPartList.Revision != collisionPartsRevision)
                collisionPartList = (collisionPartsRevision, collisionPartSets.OrderBy(pair => pair.Key)
                    .SelectMany(pair => pair.Value.Order().Select(part => new CollisionPartRef(pair.Key, part))).ToArray());
            return collisionPartList.Parts;
        }
    }

    /** Adds a part of the selected placement to the chosen parts, or removes it. */
    public void ToggleCollisionPart(int part)
    {
        if (SelectedCollision < 0) return;
        if (!collisionPartSets.TryGetValue(SelectedCollision, out var set)) collisionPartSets[SelectedCollision] = set = [];
        if (!set.Remove(part)) set.Add(part);
        if (set.Count == 0) collisionPartSets.Remove(SelectedCollision);
        TouchOutline(SelectedCollision);
        collisionPartsRevision++;
    }

    public void ClearCollisionParts()
    {
        foreach (int placement in collisionPartSets.Keys) TouchOutline(placement);
        collisionPartSets.Clear();
        collisionPartsRevision++;
    }

    /** Selects the first part's placement and chooses every part; an empty list clears it. */
    public void SelectCollisionParts(IReadOnlyCollection<CollisionPartRef> parts)
    {
        if (collisionView is not { } view) throw new InvalidOperationException("No collision is loaded.");
        if (parts.Any(part => part.Placement < 0 || part.Placement >= view.Scene.Placements.Count))
            throw new ArgumentOutOfRangeException(nameof(parts), "No loaded collision placement has this index.");
        SelectCollision(parts.Count == 0 ? -1 : parts.Min(part => part.Placement));
        ClearCollisionParts();
        AddCollisionParts(parts);
    }

    private void AddCollisionParts(IEnumerable<CollisionPartRef> parts)
    {
        foreach (var part in parts)
        {
            if (!collisionPartSets.TryGetValue(part.Placement, out var set)) collisionPartSets[part.Placement] = set = [];
            if (set.Add(part.Part)) TouchOutline(part.Placement);
        }
        collisionPartsRevision++;
    }

    private Task<(CollisionConnectResult Result, double Milliseconds)>? connectWork;
    // A search result is dropped when the scene, the chosen parts or the pick changed meanwhile.
    private (CollisionScene? Scene, int Revision, int Placement, int Triangle) connectStart;

    /** True while Select connected runs on a worker. */
    public bool CollisionConnecting => connectWork is not null;
    /** The last finished Select connected and its time; null before the first one. */
    public (CollisionConnectResult Result, double Milliseconds)? LastCollisionConnect { get; private set; }

    /**
     * Starts adding the parts connected to the picked part and to the chosen parts. With nothing
     * picked, all parts of the selected placement are the seeds. Only shown placements join.
     * @return False when nothing is selected or a search already runs.
     */
    public bool SelectConnectedCollision(CollisionConnectOptions options)
    {
        if (connectWork is not null || collisionView is not { } view || SelectedCollision < 0) return false;
        var scene = view.Scene;
        var seeds = CollisionPartSelection.ToList();
        var shape = scene.Pieces[scene.Placements[SelectedCollision].Piece].Shape;
        var parts = CollisionParts.Of(shape);
        if (SelectedCollisionTriangle >= 0 && SelectedCollisionTriangle < parts.PartOfTriangle.Length)
            seeds.Add(new(SelectedCollision, parts.PartOfTriangle[SelectedCollisionTriangle]));
        else seeds.AddRange(Enumerable.Range(0, parts.Triangles.Count).Select(part => new CollisionPartRef(SelectedCollision, part)));
        // The pose and the filters are read here, on the thread that changes them.
        var worlds = Enumerable.Range(0, scene.Placements.Count).Select(index => CollisionWorld(scene, index)).ToArray();
        var shown = Enumerable.Range(0, scene.Placements.Count).Select(index => CollisionShown(scene, index,
            Visibility.CollisionVisible(scene.Entities[scene.Placements[index].Entity].Marker,
                scene.Entities[scene.Placements[index].Entity].BubbleHash))).ToArray();
        connectStart = (scene, collisionPartsRevision, SelectedCollision, SelectedCollisionTriangle);
        connectWork = Task.Run(() =>
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            var result = CollisionConnectivity.Connected(scene, seeds, options, index => worlds[index], index => shown[index]);
            return (result, System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        });
        return true;
    }

    /** Adds a finished search's parts unless the collision or the chosen parts changed. */
    private void PollCollisionConnect()
    {
        if (connectWork is not { IsCompleted: true } done) return;
        connectWork = null;
        if (!done.IsCompletedSuccessfully) { CollisionStatus = "Select connected failed: " + done.Exception?.InnerExceptions[0].Message; return; }
        LastCollisionConnect = done.Result;
        if (connectStart == (collisionView?.Scene, collisionPartsRevision, SelectedCollision, SelectedCollisionTriangle))
            AddCollisionParts(done.Result.Result.Parts);
    }

    /** The world box of every chosen part, in the drawn pose. */
    public (Vector3 Min, Vector3 Max) CollisionPartBounds()
    {
        if (collisionView is not { } view) throw new InvalidOperationException("No collision is loaded.");
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        foreach (var (placement, set) in collisionPartSets)
        {
            var shape = view.Scene.Pieces[view.Scene.Placements[placement].Piece].Shape;
            var (low, high) = CollisionParts.Of(shape).Bounds(shape, set, CollisionWorld(view.Scene, placement));
            min = Vector3.Min(min, low); max = Vector3.Max(max, high);
        }
        return (min, max);
    }

    /** The chosen parts' box when parts are chosen, else the selected placement's drawn box. */
    private (Vector3 Min, Vector3 Max) CollisionSelectionBounds() =>
        collisionPartSets.Count > 0 ? CollisionPartBounds() : DrawnCollisionBounds(SelectedCollision);

    private bool SamePart(int placement, int first, int second)
    {
        if (first < 0 || second < 0 || collisionView is not { } view) return first == second;
        var parts = CollisionParts.Of(view.Scene.Pieces[view.Scene.Placements[placement].Piece].Shape);
        return first < parts.PartOfTriangle.Length && second < parts.PartOfTriangle.Length
            && parts.PartOfTriangle[first] == parts.PartOfTriangle[second];
    }

    /** Raised with the new placement index, or -1 when the collision selection clears. */
    public event Action<int>? CollisionSelectionChanged;

    /** Selects one loaded collision placement and clears the object selection; -1 clears it. */
    public void SelectCollision(int placement)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (placement >= 0 && (collisionView is not { } view || placement >= view.Scene.Placements.Count))
            throw new ArgumentOutOfRangeException(nameof(placement), "No loaded collision placement has this index.");
        if (placement >= 0) ApplySelection([], false);
        SetCollisionSelection(placement);
    }

    private void SetCollisionSelection(int placement)
    {
        if (SelectedCollision == placement) return;
        SelectedCollision = placement;
        SelectedCollisionTriangle = -1;
        // A pick inside the chosen parts keeps them, so their parts can be added or removed.
        if (!collisionPartSets.ContainsKey(placement)) { ClearCollisionParts(); LastCollisionConnect = null; }
        CollisionSelectionChanged?.Invoke(placement);
    }

    /** A full reload finds the selected placement and the chosen parts again by their keys. */
    private void RefindCollisionSelection(CollisionScene? before, CollisionScene after)
    {
        if (SelectedCollision < 0 || before is null || SelectedCollision >= before.Placements.Count) { SetCollisionSelection(-1); return; }
        var old = before.Placements[SelectedCollision];
        int found = CollisionPartKey.OfPlacement(before, SelectedCollision).FindPlacement(after);
        var keys = CollisionPartKey.Of(before, CollisionPartSelection);
        // Outlines are cached by index, and a reload renumbers placements.
        DisposeCollisionOutline();
        // The index can change while the placement stays, so the host is told either way.
        SelectedCollision = found;
        if (found < 0 || after.Pieces[after.Placements[found].Piece].Shape.TriangleCount
            != before.Pieces[old.Piece].Shape.TriangleCount) SelectedCollisionTriangle = -1;
        collisionPartSets.Clear();
        if (found >= 0) AddCollisionParts(CollisionPartKey.Resolve(after, keys).Found);
        else collisionPartsRevision++;
        CollisionSelectionChanged?.Invoke(found);
    }

    /** An in-place update keeps indices; a changed shape renumbers its parts, so its set drops. */
    private void KeepCollisionParts(CollisionScene before, CollisionScene after)
    {
        foreach (int placement in collisionPartSets.Keys.ToArray())
            if (placement >= after.Placements.Count || !ReferenceEquals(before.Pieces[before.Placements[placement].Piece].Shape,
                after.Pieces[after.Placements[placement].Piece].Shape))
            {
                collisionPartSets.Remove(placement);
                TouchOutline(placement);
            }
        collisionPartsRevision++;
    }

    /**
     * The nearest collision triangle under the pointer, among the placements drawn last frame.
     * Only drawn content is picked, so boxes alone pick nothing.
     * @param distance World distance along the pick ray; infinity when nothing is hit.
     * @return The placement index, or -1.
     */
    public int PickCollision(Vector2 viewportPixel, Vector2 size, out float distance) =>
        PickCollision(viewportPixel, size, out distance, out _);

    /** @param triangle The hit triangle in the placement's shape, or -1. */
    public int PickCollision(Vector2 viewportPixel, Vector2 size, out float distance, out int triangle)
    {
        distance = float.PositiveInfinity;
        triangle = -1;
        if (!Visibility.ShowCollisionContent || collisionView is not { } data || visibleCollision.Count == 0) return -1;
        // Through the item's pose, as the collision is drawn.
        if (size.X <= 0 || size.Y <= 0 || !Matrix4x4.Invert(itemPose * Camera.ViewProjection(size.X / size.Y), out var inverse))
            return -1;
        var ndc = new Vector2(viewportPixel.X / size.X * 2 - 1, 1 - viewportPixel.Y / size.Y * 2);
        var near = Vector4.Transform(new Vector4(ndc, 0, 1), inverse);
        var far = Vector4.Transform(new Vector4(ndc, .5f, 1), inverse);
        var origin = new Vector3(near.X, near.Y, near.Z) / near.W;
        var direction = Vector3.Normalize(new Vector3(far.X, far.Y, far.Z) / far.W - origin);
        var scene = data.Scene;
        var candidates = new List<(float Distance, int Index)>();
        foreach (int index in visibleCollision)
        {
            var placement = scene.Placements[index];
            var (min, max) = placement.Bone < 0 ? (placement.BoundsMin, placement.BoundsMax)
                : CollisionScene.WorldBounds(scene.Pieces[placement.Piece].Shape, CollisionWorld(scene, index));
            if (SceneSelection.RayBox(origin, direction, new(min, max), float.PositiveInfinity, out float entry))
                candidates.Add((entry, index));
        }
        candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        int hit = -1;
        foreach (var (entry, index) in candidates)
        {
            if (entry > distance) break;
            var placement = scene.Placements[index];
            if (!Matrix4x4.Invert(CollisionWorld(scene, index), out var toLocal)) continue;
            var localOrigin = Vector3.Transform(origin, toLocal);
            // Unnormalized, so the hit parameter stays a world distance.
            var localDirection = Vector3.TransformNormal(direction, toLocal);
            if (localDirection.LengthSquared() < 1e-24f) continue;
            var shape = scene.Pieces[placement.Piece].Shape;
            for (int i = 0; i + 2 < shape.Indices.Length; i += 3)
                if (SceneSelection.RayTriangle(localOrigin, localDirection, shape.Vertices[shape.Indices[i]],
                    shape.Vertices[shape.Indices[i + 1]], shape.Vertices[shape.Indices[i + 2]], out float along) && along < distance)
                { distance = along; hit = index; triangle = i / 3; }
        }
        return hit;
    }

    /** Outlines the selected placement and every placement with chosen parts, as objects are. */
    private void DrawCollisionOutline(Matrix4x4 view)
    {
        if (SelectedCollisionPlacement is null) { DisposeCollisionOutline(); return; }
        if (outline is null || !Visibility.ShowCollisionContent && !Visibility.ShowCollisionBoxes) return;
        var scene = collisionView!.Scene;
        var drawn = collisionPartSets.Keys.Append(SelectedCollision).ToHashSet();
        foreach (int index in drawn)
        {
            var placement = scene.Placements[index];
            var entity = scene.Entities[placement.Entity];
            if (!Visibility.CollisionVisible(entity.Marker, entity.BubbleHash)) continue;
            var shape = scene.Pieces[placement.Piece].Shape;
            var world = CollisionWorld(scene, index);
            if (shape.TriangleCount == 0)
            {
                // A shape without triangles outlines its box.
                if (!(shape.BoundsMin.X <= shape.BoundsMax.X)) continue;
                markerBox ??= CreateMarkerBox();
                var box = Matrix4x4.CreateScale(Vector3.Max((shape.BoundsMax - shape.BoundsMin) * .5f, new Vector3(.01f)))
                    * Matrix4x4.CreateTranslation((shape.BoundsMin + shape.BoundsMax) * .5f);
                outline.Draw(markerBox, box * world * view, true);
                continue;
            }
            int stamp = outlineStamps.GetValueOrDefault(index);
            if (!collisionOutlines.TryGetValue(index, out var cached) || !ReferenceEquals(cached.Shape, shape) || cached.Stamp != stamp)
            {
                cached.Mesh?.Dispose();
                cached = (CollisionOutlineMesh(shape, index), shape, stamp);
                collisionOutlines[index] = cached;
            }
            outline.Draw(cached.Mesh, world * view, true);
        }
        foreach (int index in collisionOutlines.Keys.Where(index => !drawn.Contains(index)).ToArray())
        {
            collisionOutlines[index].Mesh.Dispose();
            collisionOutlines.Remove(index);
        }
    }

    /** The chosen parts, else the picked part of the selected placement, else the whole shape. */
    private GpuMesh CollisionOutlineMesh(CollisionShape shape, int index)
    {
        var vertices = shape.Vertices.Select(position => new RenderVertex(position, Vector3.UnitZ, Vector2.Zero)).ToArray();
        var parts = CollisionParts.Of(shape);
        IEnumerable<int> chosen = collisionPartSets.TryGetValue(index, out var set) ? set
            : index == SelectedCollision && SelectedCollisionTriangle >= 0 && SelectedCollisionTriangle < parts.PartOfTriangle.Length
                ? [parts.PartOfTriangle[SelectedCollisionTriangle]] : [];
        var triangles = chosen.SelectMany(part => parts.Triangles[part]).ToArray();
        var indices = triangles.Length == 0 ? shape.Indices.Select(vertex => (uint)vertex).ToArray()
            : triangles.SelectMany(triangle => new[] { (uint)shape.Indices[triangle * 3], (uint)shape.Indices[triangle * 3 + 1],
                (uint)shape.Indices[triangle * 3 + 2] }).ToArray();
        return renderer.CreateMesh(vertices, indices);
    }

    private void DisposeCollisionOutline()
    {
        foreach (var (mesh, _, _) in collisionOutlines.Values) mesh.Dispose();
        collisionOutlines.Clear();
        outlineStamps.Clear();
    }
}
