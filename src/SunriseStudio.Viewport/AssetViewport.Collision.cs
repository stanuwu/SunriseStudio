using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Viewport;

/** Decoded collision with its uploaded geometry and culling index; an update may share the geometry. */
internal sealed class CollisionView
{
    public required CollisionScene Scene { get; init; }
    public required CollisionGeometry? Geometry { get; init; }
    /** Index range of each piece; zero for a piece without triangles. */
    public required (uint FirstIndex, uint IndexCount, int BaseVertex)[] Ranges { get; init; }
    public required int[] ByPiece { get; init; }
    public required Matrix4x4[] Boxes { get; init; }
    public required Vector4[] Tints { get; init; }
    public required SceneGrid Grid { get; init; }
    public required double LoadMilliseconds { get; init; }
    /** Set on a view made by an in-place update. */
    public CollisionRefresh? Refresh { get; init; }
}

/** How the last project change reached the collision layer: in place or by reload, why, and how long. */
public sealed record CollisionRefresh(bool InPlace, string Reason, double Milliseconds, int MovedPlacements, int ChangedShapes);

public sealed partial class AssetViewport
{
    // Placements smaller than this on screen are not drawn.
    private const float CollisionMinPixels = 1.5f;
    private CollisionPass? collisionPass;
    private CollisionView? collisionView;
    private Task<(CollisionView? View, string Reason)>? collisionLoad;
    private CancellationTokenSource? collisionCancel;
    private SceneDocument? collisionSource;
    private bool collisionStale;
    // Changes not yet applied to the current view, and the ones the running update applies.
    private readonly HashSet<uint> collisionPending = [];
    private HashSet<uint>? collisionUpdateTags;
    private string collisionReloadReason = "";
    private readonly List<int> visibleCollision = [];
    private readonly List<CollisionInstance> collisionInstances = [];
    private readonly List<CollisionRange> collisionRanges = [];
    private readonly List<CollisionInstance> collisionBoxes = [];
    private bool[] collisionEntityShown = [];
    private int[] collisionPieceCounts = [];
    private Func<int, bool>? collisionShown;

    /** The decoded collision of the current scene, once it has loaded. */
    public CollisionScene? Collision => collisionView?.Scene;
    /** Loading or failure text; empty when the layer is ready or unused. */
    public string CollisionStatus { get; private set; } = "";
    public bool CollisionEdges { get; set; }
    public bool CollisionSolid { get; set; }
    public bool ShowInactiveCollision { get; set; }
    public int VisibleCollisionCount { get; private set; }
    public long VisibleCollisionTriangles { get; private set; }
    public double CollisionLoadMilliseconds => collisionView?.LoadMilliseconds ?? 0;
    public bool CollisionLoading => collisionLoad is not null;
    /** True while a change waits to reach the drawn collision. */
    public bool CollisionUpdatePending => collisionLoad is not null || collisionPending.Count > 0 || collisionStale && collisionView is not null;
    /** The last project change the layer followed; null before the first one. */
    public CollisionRefresh? LastCollisionRefresh { get; private set; }
    /** The host sets this while it changes the project, so no load reads the catalog meanwhile. */
    public bool PauseCollisionLoads { get; set; }
    /** The collision shape under the pointer, as a tooltip text. */
    public string? HoveredCollision { get; private set; }

    /** True when the scene can hold collision: a map, bubble, static model, owner or Havok packfile. */
    public bool CollisionPossible(SceneDocument scene)
    {
        if (Catalog is not { } catalog) return false;
        if (ReferenceEquals(possibleScene, scene) && ReferenceEquals(possibleCatalog, catalog)) return possible;
        possible = catalog.TryGetEntry(scene.TagHash, out var entry)
            && (entry is { Type: 26, SubType: 7 } || entry.Reference is StaticCollisionReader.OwnerClass or 0x808071A7 or 0x80809C0F)
            || scene.ReadTags.Any(tag => catalog.TryGetEntry(tag, out var list) && list.Reference == StaticCollisionReader.ObjectListClass);
        (possibleScene, possibleCatalog) = (scene, catalog);
        return possible;
    }

    private SceneDocument? possibleScene;
    private PackageCatalog? possibleCatalog;
    private bool possible;

    /** Reloads collision the next time it is shown; the current one stays drawn until then. */
    public void InvalidateCollision(string reason = "Reload requested.")
    {
        if (collisionView is null && collisionLoad is null) return;
        CancelCollisionLoad();
        collisionStale = true;
        collisionReloadReason = reason;
    }

    /**
     * Follows a project change. Changes that touch only compound instances or per-mesh shapes
     * update the loaded collision in place; any other change reloads it.
     * @param changedTags Tags whose read bytes changed; null when the change is not known.
     */
    public void UpdateCollision(IReadOnlyCollection<uint>? changedTags)
    {
        if (changedTags is { Count: 0 } || collisionView is null && collisionLoad is null) return;
        if (changedTags is null) { InvalidateCollision("The change is not known."); return; }
        if (collisionStale) return;
        collisionPending.UnionWith(changedTags);
    }

    /** Stops a running load and waits for it, so no worker reads the catalog during a project change. */
    public void CancelCollisionLoad()
    {
        if (collisionLoad is not { } load) return;
        collisionCancel?.Cancel();
        try { load.Wait(); } catch (AggregateException) { }
        if (load.IsCompletedSuccessfully) Release(load.Result.View);
        // A stopped update runs again later; a stopped load reloads.
        if (collisionUpdateTags is { } tags) collisionPending.UnionWith(tags);
        else collisionStale = true;
        collisionLoad = null;
        collisionUpdateTags = null;
        collisionCancel?.Dispose();
        collisionCancel = null;
        CollisionStatus = "";
    }

    private void ClearCollision()
    {
        CancelCollisionLoad();
        Release(collisionView, true);
        collisionView = null;
        collisionSource = null;
        collisionStale = false;
        collisionPending.Clear();
        collisionReloadReason = "";
        HoveredCollision = null;
        CollisionStatus = "";
        VisibleCollisionCount = 0;
        VisibleCollisionTriangles = 0;
        SetCollisionSelection(-1);
        DisposeCollisionOutline();
    }

    /** Disposes a view's geometry unless the current view still draws it. */
    private void Release(CollisionView? view, bool current = false)
    {
        if (view?.Geometry is { } geometry && (current || !ReferenceEquals(geometry, collisionView?.Geometry))) geometry.Dispose();
    }

    /** Adopts a finished search, load or update, then starts a due load when the layer is shown. */
    private void PollCollision()
    {
        PollCollisionConnect();
        if (collisionLoad is { IsCompleted: true } done)
        {
            bool update = collisionUpdateTags is not null;
            collisionLoad = null;
            collisionUpdateTags = null;
            collisionCancel?.Dispose();
            collisionCancel = null;
            if (done.IsCompletedSuccessfully && ReferenceEquals(collisionSource, Scene))
            {
                var (view, reason) = done.Result;
                if (view is null) { collisionStale = true; collisionReloadReason = reason; }
                else AdoptCollision(view, update);
            }
            else if (done.IsCompletedSuccessfully) Release(done.Result.View);
            else if (done.Exception is { } failure)
            {
                if (update) { collisionStale = true; collisionReloadReason = failure.InnerExceptions[0].Message; }
                else CollisionStatus = "Collision failed: " + failure.InnerExceptions[0].Message;
            }
        }
        if (!Visibility.ShowCollisionContent && !Visibility.ShowCollisionBoxes || Scene is not { } scene || Catalog is not { } catalog) return;
        if (!ReferenceEquals(collisionSource, scene))
        {
            Release(collisionView, true);
            collisionView = null;
            collisionStale = true;
            collisionPending.Clear();
            collisionReloadReason = "";
            CollisionStatus = "";
            SetCollisionSelection(-1);
        }
        if (collisionLoad is not null || PauseCollisionLoads) return;
        collisionPass ??= new CollisionPass(renderer);
        var pass = collisionPass;
        // A failed load is not retried until the scene or the project changes.
        if (collisionStale)
        {
            collisionSource = scene;
            collisionStale = false;
            collisionPending.Clear();
            var cancel = collisionCancel = new CancellationTokenSource();
            CollisionStatus = "Loading collision...";
            collisionLoad = Task.Run(() => ((CollisionView?)BuildCollisionView(catalog, scene, pass, cancel.Token), ""), cancel.Token);
        }
        else if (collisionPending.Count > 0 && collisionView is { } current)
        {
            var tags = collisionUpdateTags = [.. collisionPending];
            collisionPending.Clear();
            var cancel = collisionCancel = new CancellationTokenSource();
            collisionLoad = Task.Run(() => UpdateCollisionView(catalog, current, tags, pass, cancel.Token), cancel.Token);
        }
    }

    /** Replaces the drawn view; an update keeps selection indices, a load finds the same placement again. */
    private void AdoptCollision(CollisionView view, bool update)
    {
        var old = collisionView;
        collisionView = view;
        if (old is not null && !ReferenceEquals(old, view)) Release(old);
        if (update)
        {
            LastCollisionRefresh = view.Refresh;
            if (old is not null) KeepCollisionParts(old.Scene, view.Scene);
            return;
        }
        CollisionStatus = view.Scene.IsEmpty ? view.Scene.Notes.FirstOrDefault() ?? "No collision" : "";
        if (collisionReloadReason.Length > 0)
            LastCollisionRefresh = new(false, collisionReloadReason, view.LoadMilliseconds, 0, 0);
        collisionReloadReason = "";
        RefindCollisionSelection(old?.Scene, view.Scene);
        // An asset with nothing else to show is framed on its collision.
        if (Scene is { Instances.Count: 0, Markers.Count: 0 } && !view.Scene.IsEmpty && Camera.PivotFree && old is null)
            Camera.Frame(view.Scene.Placements.Select(p => p.BoundsMin).Aggregate(Vector3.Min),
                view.Scene.Placements.Select(p => p.BoundsMax).Aggregate(Vector3.Max), lastAspect);
    }

    /** Decodes, packs and uploads on a worker; only the device is used, which is free-threaded. */
    private static CollisionView BuildCollisionView(PackageCatalog catalog, SceneDocument source, CollisionPass pass, CancellationToken token)
    {
        long start = Stopwatch.GetTimestamp();
        var scene = new CollisionSceneLoader(catalog).Load(source, token);
        var (geometry, ranges) = PackCollision(scene, pass, token);
        try { return IndexCollision(scene, geometry, ranges, Stopwatch.GetElapsedTime(start).TotalMilliseconds, null); }
        catch { geometry?.Dispose(); throw; }
    }

    /** Applies changed tags to a view on a worker. The geometry is shared unless a shape changed. */
    private static (CollisionView? View, string Reason) UpdateCollisionView(PackageCatalog catalog, CollisionView view,
        IReadOnlyCollection<uint> tags, CollisionPass pass, CancellationToken token)
    {
        long start = Stopwatch.GetTimestamp();
        var change = CollisionSceneUpdate.Apply(catalog, view.Scene, tags, out string reason);
        if (change is null) return (null, reason);
        if (ReferenceEquals(change.Scene, view.Scene))
            return (new CollisionView
            {
                Scene = view.Scene, Geometry = view.Geometry, Ranges = view.Ranges, ByPiece = view.ByPiece, Boxes = view.Boxes,
                Tints = view.Tints, Grid = view.Grid, LoadMilliseconds = view.LoadMilliseconds,
                Refresh = new(true, "", Stopwatch.GetElapsedTime(start).TotalMilliseconds, 0, 0)
            }, "");
        token.ThrowIfCancellationRequested();
        var (geometry, ranges) = change.ChangedPieces.Count > 0 ? PackCollision(change.Scene, pass, token) : (view.Geometry, view.Ranges);
        try
        {
            double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var refresh = new CollisionRefresh(true, "", milliseconds, change.MovedPlacements, change.ChangedPieces.Count);
            return (IndexCollision(change.Scene, geometry, ranges, view.LoadMilliseconds, refresh), "");
        }
        catch
        {
            if (!ReferenceEquals(geometry, view.Geometry)) geometry?.Dispose();
            throw;
        }
    }

    /** Every distinct shape in one vertex and one index buffer. */
    private static (CollisionGeometry? Geometry, (uint, uint, int)[] Ranges) PackCollision(CollisionScene scene, CollisionPass pass,
        CancellationToken token)
    {
        var ranges = new (uint, uint, int)[scene.Pieces.Count];
        long vertexCount = 0, indexCount = 0;
        foreach (var piece in scene.Pieces) { vertexCount += piece.Shape.Vertices.Length; indexCount += piece.Shape.Indices.Length; }
        var positions = new Vector3[vertexCount];
        var indices = new uint[indexCount];
        int vertexAt = 0, indexAt = 0;
        foreach (var piece in scene.Pieces)
        {
            token.ThrowIfCancellationRequested();
            var shape = piece.Shape;
            ranges[piece.Index] = ((uint)indexAt, (uint)shape.Indices.Length, vertexAt);
            shape.Vertices.CopyTo(positions, vertexAt);
            for (int i = 0; i < shape.Indices.Length; i++) indices[indexAt + i] = (uint)shape.Indices[i];
            vertexAt += shape.Vertices.Length;
            indexAt += shape.Indices.Length;
        }
        return (indexCount > 0 ? pass.CreateGeometry(positions, indices) : null, ranges);
    }

    /** Boxes, tints and the culling grid of every placement. */
    private static CollisionView IndexCollision(CollisionScene scene, CollisionGeometry? geometry, (uint, uint, int)[] ranges,
        double loadMilliseconds, CollisionRefresh? refresh)
    {
        var byPiece = Enumerable.Range(0, scene.Placements.Count).OrderBy(i => scene.Placements[i].Piece).ThenBy(i => i).ToArray();
        var boxes = new Matrix4x4[scene.Placements.Count];
        var tints = new Vector4[scene.Placements.Count];
        var centers = new Vector3[scene.Placements.Count];
        var radii = new float[scene.Placements.Count];
        for (int i = 0; i < scene.Placements.Count; i++)
        {
            var placement = scene.Placements[i];
            var shape = scene.Pieces[placement.Piece].Shape;
            boxes[i] = BoxMatrix(shape) * placement.World;
            tints[i] = Tint(scene.Pieces[placement.Piece].Kind, placement.Role);
            centers[i] = (placement.BoundsMin + placement.BoundsMax) / 2;
            radii[i] = Vector3.Distance(placement.BoundsMin, placement.BoundsMax) / 2;
        }
        return new CollisionView
        {
            Scene = scene, Geometry = geometry, Ranges = ranges, ByPiece = byPiece, Boxes = boxes, Tints = tints,
            Grid = new SceneGrid(centers, radii), LoadMilliseconds = loadMilliseconds, Refresh = refresh
        };
    }

    /** The unit cube mapped onto a shape's local box. */
    private static Matrix4x4 BoxMatrix(CollisionShape shape)
    {
        var min = shape.BoundsMin; var size = shape.BoundsMax - shape.BoundsMin;
        if (!(size.X >= 0)) (min, size) = (Vector3.Zero, Vector3.Zero);
        return Matrix4x4.CreateScale(Vector3.Max(size, new Vector3(1e-3f))) * Matrix4x4.CreateTranslation(min);
    }

    /** World collision by kind: mesh orange, convex cyan, box green. Triggers violet, hitboxes red, other grey. */
    private static Vector4 Tint(string kind, CollisionRole role) => role switch
    {
        CollisionRole.Trigger => new(.75f, .45f, 1, .3f),
        CollisionRole.Hitbox => new(1, .25f, .25f, .4f),
        CollisionRole.Other => new(.7f, .7f, .7f, .3f),
        _ => kind switch
        {
            "Mesh" => new(1, .55f, .15f, .35f),
            "Convex" => new(.2f, .8f, 1, .35f),
            "Box" => new(.4f, 1, .4f, .35f),
            _ => new(1, .3f, .7f, .35f)
        }
    };

    /** Roles the layer does not draw or pick. */
    public HashSet<CollisionRole> HiddenCollisionRoles { get; } = [];

    /** The drawn world matrix of a loaded placement; see CollisionWorld. */
    public Matrix4x4 DrawnCollisionWorld(int index) =>
        collisionView is { } view ? CollisionWorld(view.Scene, index) : throw new InvalidOperationException("No collision is loaded.");

    /** Selection uses the drawn pose rather than the bounds stored with the bind pose. */
    public (Vector3 Min, Vector3 Max) DrawnCollisionBounds(int index)
    {
        if (collisionView is not { } view) throw new InvalidOperationException("No collision is loaded.");
        var placement = view.Scene.Placements[index];
        return CollisionScene.WorldBounds(view.Scene.Pieces[placement.Piece].Shape, CollisionWorld(view.Scene, index));
    }

    /** The drawn world matrix: a posed bone for an opened entity, else the loaded placement through the item's pose. */
    private Matrix4x4 CollisionWorld(CollisionScene scene, int index)
    {
        var placement = scene.Placements[index];
        var entity = scene.Entities[placement.Entity];
        if (placement.Bone < 0 || entity.Source != CollisionSource.EntityPhysics || entity.Marker is not null
            || Animation?.Pose.ObjectTransforms is not { } pose || placement.Bone >= pose.Count) return placement.World * itemPose;
        // The bone already places this shape; applying the item's pose again would move it twice.
        return pose[placement.Bone] * entity.Frame;
    }

    /** True while the item or its bones move collision off its loaded bounds, which then skips the culling grid. */
    private bool CollisionPosed(CollisionScene scene) => !itemPose.IsIdentity || Animation is not null
        && scene.Entities.Any(entity => entity.Source == CollisionSource.EntityPhysics && entity.Marker is null);

    /** Drawn when its entity shows, its role is not hidden, and it is enabled or selected. */
    private bool CollisionShown(CollisionScene scene, int index, bool entityShown) => entityShown
        && (HiddenCollisionRoles.Count == 0 || !HiddenCollisionRoles.Contains(scene.Placements[index].Role))
        && (ShowInactiveCollision || index == SelectedCollision || scene.Placements[index].InitiallyEnabled != false);

    /** Culls placements per frame and draws the shown ones, grouped by shape, into the bound target. */
    private void DrawCollision(Matrix4x4 view, int width, int height)
    {
        VisibleCollisionCount = 0;
        VisibleCollisionTriangles = 0;
        bool content = Visibility.ShowCollisionContent, boxes = Visibility.ShowCollisionBoxes;
        if (!content && !boxes || collisionView is not { } data || collisionPass is null) return;
        var scene = data.Scene;
        if (collisionEntityShown.Length != scene.Entities.Count) collisionEntityShown = new bool[scene.Entities.Count];
        foreach (var entity in scene.Entities) collisionEntityShown[entity.Index] = Visibility.CollisionVisible(entity.Marker, entity.BubbleHash);
        visibleCollision.Clear();
        float pixelsPerUnit = Camera.Orthographic ? height / Camera.ViewHeight
            : height / (2 * MathF.Tan(Camera.VerticalFieldOfView((float)width / height) / 2));
        collisionShown = index => CollisionShown(collisionView!.Scene, index, collisionEntityShown[collisionView.Scene.Placements[index].Entity]);
        // A posed entity has moved off its loaded bounds, so all of it is drawn.
        if (CollisionPosed(scene))
        {
            for (int index = 0; index < scene.Placements.Count; index++) if (collisionShown(index)) visibleCollision.Add(index);
        }
        else data.Grid.Query(SceneGrid.FrustumPlanes(view), Camera.Position, pixelsPerUnit, Camera.Orthographic, CollisionMinPixels,
            collisionShown, visibleCollision);
        if (visibleCollision.Count == 0) return;
        VisibleCollisionCount = visibleCollision.Count;
        if (boxes)
        {
            collisionBoxes.Clear();
            foreach (int index in visibleCollision)
            {
                var box = scene.Placements[index].Bone >= 0 || !itemPose.IsIdentity
                    ? BoxMatrix(scene.Pieces[scene.Placements[index].Piece].Shape) * CollisionWorld(scene, index) : data.Boxes[index];
                collisionBoxes.Add(new(box, data.Tints[index] with { W = 1 }));
            }
            collisionPass.DrawBoxes(CollectionsMarshal.AsSpan(collisionBoxes), view);
        }
        if (!content || data.Geometry is null) return;
        // Triggers stay translucent when solid collision is on.
        DrawCollisionGroup(data, view, trigger: false);
        DrawCollisionGroup(data, view, trigger: true);
    }

    /** Draws the visible placements of one group with one instanced draw per shape. */
    private void DrawCollisionGroup(CollisionView data, Matrix4x4 view, bool trigger)
    {
        var scene = data.Scene;
        // A counting sort by piece keeps one draw per shape.
        if (collisionPieceCounts.Length != scene.Pieces.Count + 1) collisionPieceCounts = new int[scene.Pieces.Count + 1];
        Array.Clear(collisionPieceCounts);
        int total = 0;
        foreach (int index in visibleCollision)
            if (scene.Placements[index].Role == CollisionRole.Trigger == trigger) { collisionPieceCounts[scene.Placements[index].Piece + 1]++; total++; }
        if (total == 0) return;
        for (int piece = 0; piece < scene.Pieces.Count; piece++) collisionPieceCounts[piece + 1] += collisionPieceCounts[piece];
        collisionInstances.Clear();
        CollectionsMarshal.SetCount(collisionInstances, total);
        var instances = CollectionsMarshal.AsSpan(collisionInstances);
        collisionRanges.Clear();
        for (int piece = 0; piece < scene.Pieces.Count; piece++)
        {
            int count = collisionPieceCounts[piece + 1] - collisionPieceCounts[piece];
            var (first, indexCount, baseVertex) = data.Ranges[piece];
            if (count > 0 && indexCount > 0)
                collisionRanges.Add(new(first, indexCount, baseVertex, (uint)collisionPieceCounts[piece], (uint)count));
            VisibleCollisionTriangles += count * indexCount / 3;
        }
        foreach (int index in visibleCollision)
        {
            var placement = scene.Placements[index];
            if (placement.Role == CollisionRole.Trigger != trigger) continue;
            instances[collisionPieceCounts[placement.Piece]++] = new(placement.Bone >= 0 || !itemPose.IsIdentity
                ? CollisionWorld(scene, index) : placement.World,
                data.Tints[index]);
        }
        collisionPass!.Draw(data.Geometry!, instances, CollectionsMarshal.AsSpan(collisionRanges), view, true, CollisionEdges,
            CollisionSolid && !trigger);
    }

    /** Names the collision triangle under the pointer. */
    private void HoverCollision(Vector2 viewportPixel, Vector2 size)
    {
        HoveredCollision = null;
        int hit = PickCollision(viewportPixel, size, out _);
        if (hit < 0) return;
        var scene = collisionView!.Scene;
        var chosen = scene.Placements[hit];
        var piece = scene.Pieces[chosen.Piece];
        string instance = chosen.Instance >= 0 ? $"\nCompound {chosen.CompoundTag:X8}, instance {chosen.Instance}" : "";
        HoveredCollision = $"Collision: {RoleText(chosen)}, {piece.Kind} {piece.Tag:X8}\n{piece.Shape.TriangleCount:N0} triangles{instance}\n"
            + CollisionSourceText(scene.Entities[chosen.Entity]) + (chosen.InitiallyEnabled == false ? "\nInitially disabled" : "")
            + (piece.Shape.Undecoded is { } missing ? $"\nNot decoded: {missing}" : "");
    }

    /** The role, with a hitbox's region name, such as "Hitbox head". */
    public static string RoleText(CollisionPlacement placement) =>
        placement.Role == CollisionRole.Hitbox && CollisionRegions.Name(placement.Region) is { Length: > 0 } region
            ? $"Hitbox {region}" : placement.Role.ToString();

    /** A short source line for an entity, such as "Static collision, owner 80EDC2D1". */
    public static string CollisionSourceText(CollisionEntity owner) => owner.Source switch
    {
        CollisionSource.StaticEntity => $"Static collision, owner {owner.Tag:X8}",
        CollisionSource.ShapeArray => $"Trigger volume {owner.Tag:X8}",
        CollisionSource.EntityPhysics => $"Entity physics {owner.Tag:X8}",
        _ => $"Asset {owner.Tag:X8}"
    };
}
