using System.Numerics;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

public enum SceneSelectionMode { Object, MeshPart }

/** The triangle faces a pick ray can hit. Front faces wind clockwise on screen, the D3D default. */
public enum PickFaces { None, Both, Front, Back }

public sealed record ScenePointProxy(string Key, string Name, Vector3 Position,
    Vector3 Extents, SceneLayer Layer = SceneLayer.Objects, uint BubbleHash = 0, object? Details = null) : ISceneSelectionProxy
{
    object ISceneSelectionProxy.Key => Details ?? Key;
}

public sealed class SceneVisibility
{
    private readonly Dictionary<uint, int[]> detailLevels = [];
    private readonly HashSet<SceneLayer> preparedLayers = [];
    private readonly HashSet<SceneLayer> preparedGeometryLayers = [];
    private readonly HashSet<SceneLayer> preparedOverlayLayers = [];
    private readonly HashSet<uint> preparedBubbles = [];
    private IReadOnlySet<uint>? preparedLiveBubbles;
    private IReadOnlyList<object>? preparedObjects;
    private bool[] preparedVisibility = [];
    private int preparedDetail;
    private bool preparedInactiveParts, preparedGameLod;
    // Prepared state and the cached host filter, per object index.
    private bool[] filtered = [];
    // The same for drawing: with GameLod every static level passes, so the distance rule can pick one.
    private bool[] preparedRender = [], renderFiltered = [];
    private bool filteredReady;
    private Predicate<object>? preparedFilter;
    private Func<long>? preparedRevisionSource;
    private long preparedFilterRevision;
    private ulong preparedBoundsRevision;
    public HashSet<SceneLayer> HiddenLayers { get; } = [];
    /** Hides what a layer draws: meshes, volume shapes and effect particles. */
    public HashSet<SceneLayer> HiddenGeometryLayers { get; } = [SceneLayer.Navigation];
    /** Hides a layer's placement boxes: markers and point proxies. */
    public HashSet<SceneLayer> HiddenOverlayLayers { get; } = [SceneLayer.Navigation];
    public HashSet<uint> HiddenBubbles { get; } = [];
    /**
     * Bubbles a live game has loaded; null shows every bubble. It is apart from the user's hidden
     * bubbles, and a change must assign a new set, since the prepared cache compares the reference.
     */
    public IReadOnlySet<uint>? LiveBubbles { get; set; }
    public Predicate<object>? Filter { get; set; }
    /** Changes whenever Filter's answer may change; Filter results are cached per value. Null calls Filter live. */
    public Func<long>? FilterRevision { get; set; }
    /** Changes whenever Prepare rebuilds the cached hide state. */
    public long Revision { get; private set; }
    /** True when PreparedVisible reads only cached state: no live host or live-link predicate runs. */
    internal bool FiltersCached => LiveFilter is null && (Filter is null || FilterRevision is not null);
    public Predicate<object>? LiveFilter { get; set; }
    internal Predicate<object>? ContextFilter { get; set; }
    public int DetailLevel { get; set; }
    /**
     * Drawing lets every level of a static part through, for the per-frame distance rule to choose.
     * Picking, framing and outlines keep DetailLevel.
     */
    public bool GameLod { get; set; }
    public bool ShowInactiveParts { get; set; }
    public IReadOnlyList<int> AvailableDetailLevels { get; private set; } = [];
    /** Collision starts hidden; these show its triangles and its shape boxes. */
    public bool ShowCollisionContent { get; set; }
    public bool ShowCollisionBoxes { get; set; }

    /** A collision entity follows its row's marker through the host filters, and its bubble. */
    internal bool CollisionVisible(SceneMarker? marker, uint bubble) => !HiddenLayers.Contains(SceneLayer.Collision)
        && !HiddenBubbles.Contains(bubble) && (LiveBubbles is null || bubble == 0 || LiveBubbles.Contains(bubble))
        && (marker is null || (Filter?.Invoke(marker) ?? true) && (LiveFilter?.Invoke(marker) ?? true)
            && (ContextFilter?.Invoke(marker) ?? true));

    /** One detail level is drawn per source asset; missing levels use the nearest available one. */
    internal void SetScene(SceneDocument scene)
    {
        detailLevels.Clear();
        preparedObjects = null;
        foreach (var group in scene.Instances.GroupBy(x => x.Mesh.TagHash))
            detailLevels[group.Key] = group.Select(x => x.Mesh.LodLevel).Distinct().Order().ToArray();
        AvailableDetailLevels = detailLevels.Values.SelectMany(levels => levels).Distinct().Order().ToArray();
    }

    internal void AddRuntimeMeshes(IEnumerable<MeshInstance> meshes)
    {
        foreach (var group in meshes.GroupBy(instance => instance.Mesh.TagHash))
            detailLevels[group.Key] = detailLevels.GetValueOrDefault(group.Key, [])
                .Concat(group.Select(instance => instance.Mesh.LodLevel)).Distinct().Order().ToArray();
        AvailableDetailLevels = detailLevels.Values.SelectMany(levels => levels).Distinct().Order().ToArray();
        preparedObjects = null;
    }

    /** Drawing and picking use the same layer, bubble, and detail filters. */
    public bool IsVisible(object item) => IsVisible(item, IsBox(item));

    /** A marker's drawn content, such as an effect's particles, follows its layer's content toggle. */
    public bool IsContentVisible(object item) => IsVisible(item, false);

    private bool IsVisible(object item, bool box) => BuiltinVisible(item, box) && (Filter?.Invoke(item) ?? true)
        && (LiveFilter?.Invoke(item) ?? true) && (ContextFilter?.Invoke(item) ?? true);

    /** A shadow caster part follows the same filters as its colour parts, though its stage never draws in colour. */
    public bool IsCasterVisible(object item) => BuiltinVisible(item, false, caster: true, anyLevel: GameLod)
        && (Filter?.Invoke(item) ?? true) && (LiveFilter?.Invoke(item) ?? true) && (ContextFilter?.Invoke(item) ?? true);

    /**
     * Immutable object fields are checked again only when their layer, bubble, or detail filters change.
     * @param boundsRevision The selection's AuthoredBoundsRevision; a replaced proxy keeps its index.
     */
    internal void Prepare(IReadOnlyList<object> objects, ulong boundsRevision)
    {
        bool builtin = !(ReferenceEquals(preparedObjects, objects) && preparedVisibility.Length == objects.Count &&
            preparedDetail == DetailLevel && preparedInactiveParts == ShowInactiveParts && preparedGameLod == GameLod
            && preparedLayers.SetEquals(HiddenLayers) &&
            preparedGeometryLayers.SetEquals(HiddenGeometryLayers) &&
            preparedOverlayLayers.SetEquals(HiddenOverlayLayers) && preparedBubbles.SetEquals(HiddenBubbles)
            && ReferenceEquals(preparedLiveBubbles, LiveBubbles));
        if (builtin)
        {
            if (preparedVisibility.Length != objects.Count) preparedVisibility = new bool[objects.Count];
            for (int index = 0; index < objects.Count; index++)
                preparedVisibility[index] = BuiltinVisible(objects[index], IsBox(objects[index]));
            if (GameLod)
            {
                if (preparedRender.Length != objects.Count || ReferenceEquals(preparedRender, preparedVisibility))
                    preparedRender = new bool[objects.Count];
                for (int index = 0; index < objects.Count; index++)
                    preparedRender[index] = objects[index] is MeshInstance { Mesh.StaticLod: not null } part
                        ? BuiltinVisible(part, false, anyLevel: true) : preparedVisibility[index];
            }
            else preparedRender = preparedVisibility;
            preparedObjects = objects;
            preparedDetail = DetailLevel;
            preparedInactiveParts = ShowInactiveParts;
            preparedGameLod = GameLod;
            preparedLayers.Clear(); preparedLayers.UnionWith(HiddenLayers);
            preparedGeometryLayers.Clear(); preparedGeometryLayers.UnionWith(HiddenGeometryLayers);
            preparedOverlayLayers.Clear(); preparedOverlayLayers.UnionWith(HiddenOverlayLayers);
            preparedBubbles.Clear(); preparedBubbles.UnionWith(HiddenBubbles);
            preparedLiveBubbles = LiveBubbles;
        }
        var source = FilterRevision;
        long host = source?.Invoke() ?? 0;
        if (!builtin && filteredReady && ReferenceEquals(preparedFilter, Filter) && ReferenceEquals(preparedRevisionSource, source)
            && preparedFilterRevision == host && preparedBoundsRevision == boundsRevision) return;
        if (filtered.Length != objects.Count) filtered = new bool[objects.Count];
        // Without a revision the host filter cannot be cached, so PreparedVisible calls it live.
        var filter = source is null ? null : Filter;
        if (ReferenceEquals(preparedRender, preparedVisibility))
        {
            for (int index = 0; index < objects.Count; index++)
                filtered[index] = preparedVisibility[index] && (filter?.Invoke(objects[index]) ?? true);
            renderFiltered = filtered;
        }
        else
        {
            if (renderFiltered.Length != objects.Count || ReferenceEquals(renderFiltered, filtered)) renderFiltered = new bool[objects.Count];
            for (int index = 0; index < objects.Count; index++)
            {
                bool passes = (preparedVisibility[index] || preparedRender[index]) && (filter?.Invoke(objects[index]) ?? true);
                filtered[index] = preparedVisibility[index] && passes;
                renderFiltered[index] = preparedRender[index] && passes;
            }
        }
        preparedFilter = Filter; preparedRevisionSource = source;
        preparedFilterRevision = host; preparedBoundsRevision = boundsRevision;
        filteredReady = true;
        Revision++;
    }

    /** Matches IsVisible for a prepared item, less ContextFilter, which the caller applies. */
    internal bool PreparedVisible(int index, object item) => filtered[index]
        && (preparedRevisionSource is not null || (Filter?.Invoke(item) ?? true)) && (LiveFilter?.Invoke(item) ?? true);

    /** PreparedVisible for drawing: with GameLod every level of a static part passes. */
    internal bool PreparedRenderVisible(int index, object item) => renderFiltered[index]
        && (preparedRevisionSource is not null || (Filter?.Invoke(item) ?? true)) && (LiveFilter?.Invoke(item) ?? true);

    /**
     * IsContentVisible for a scene mesh, read from the prepared state when the index still holds it.
     * The caller applies ContextFilter.
     */
    internal bool PreparedContentVisible(int index, MeshInstance item) =>
        (uint)index < (uint)filtered.Length && preparedObjects is { } objects && ReferenceEquals(objects[index], item)
            ? PreparedVisible(index, item)
            : BuiltinVisible(item, false) && (Filter?.Invoke(item) ?? true) && (LiveFilter?.Invoke(item) ?? true);

    /** @param anyLevel Skips the detail level test for a static part; the distance rule then picks its level. */
    private bool BuiltinVisible(object item, bool box, bool caster = false, bool anyLevel = false)
    {
        if (!ShowInactiveParts && item is MeshInstance { InitiallyVisible: false }) return false;
        // Shadow and prepass parts repeat the colour geometry; the game never draws them in colour.
        if (!caster && item is MeshInstance { Mesh.Native: { RenderStage: >= 0 } native }
            && !NativeScenePlan.DrawnStages.Contains(native.RenderStage)) return false;
        if (item is MeshInstance instance && !(anyLevel && instance.Mesh.StaticLod is not null)
            && detailLevels.TryGetValue(instance.Mesh.TagHash, out var levels))
        {
            int best = levels[0];
            foreach (int level in levels)
                if (Math.Abs(level - DetailLevel) < Math.Abs(best - DetailLevel)) best = level;
            if (instance.Mesh.LodLevel != best) return false;
        }
        var (layer, bubble) = item switch
        {
            MeshInstance mesh => (mesh.Layer, mesh.BubbleHash),
            SceneMarker marker => (marker.Layer, marker.BubbleHash),
            ScenePointProxy point => (point.Layer, point.BubbleHash),
            _ => (SceneLayer.Objects, 0u)
        };
        return !HiddenLayers.Contains(layer) && !HiddenBubbles.Contains(bubble) &&
            (LiveBubbles is null || bubble == 0 || LiveBubbles.Contains(bubble)) &&
            !(box ? HiddenOverlayLayers : HiddenGeometryLayers).Contains(layer);
    }

    /** Overlays draw as wire over the scene and pick by their edges; a volume shape is one. */
    public static bool IsOverlay(object item) => item is not MeshInstance { Layer: not SceneLayer.Volumes };

    /** Every mesh is content, a volume shape included; markers and point proxies are boxes. */
    public static bool IsBox(object item) => item is not MeshInstance;
}

public readonly record struct SelectionBounds(Vector3 Minimum, Vector3 Maximum)
{
    public Vector3 Center => (Minimum + Maximum) * .5f;
    public float Radius => Vector3.Distance(Minimum, Maximum) * .5f;
    public static SelectionBounds Empty => new(new(float.PositiveInfinity), new(float.NegativeInfinity));
    public bool IsFinite => float.IsFinite(Minimum.X + Minimum.Y + Minimum.Z +
        Maximum.X + Maximum.Y + Maximum.Z);
    public SelectionBounds Include(Vector3 point) => new(Vector3.Min(Minimum, point), Vector3.Max(Maximum, point));

    /** Rotated boxes must include all eight corners. */
    public SelectionBounds Transform(Matrix4x4 transform)
    {
        var result = Empty;
        for (int corner = 0; corner < 8; corner++)
            result = result.Include(Vector3.Transform(new Vector3(
                (corner & 1) == 0 ? Minimum.X : Maximum.X,
                (corner & 2) == 0 ? Minimum.Y : Maximum.Y,
                (corner & 4) == 0 ? Minimum.Z : Maximum.Z), transform));
        return result;
    }
}

/** Source meshes stay in bind pose; posed positions are private copies for picking. */
public sealed class SceneSelection
{
    private sealed class ObjectGroup
    {
        public List<object> Members { get; } = [];
        public List<MeshInstance> Meshes { get; } = [];
    }

    private enum GroupKind { Identity, PlacementRow, InstanceRow, Class, Mesh }

    private readonly record struct GroupKey(object? Identity, uint Tag, int Row, GroupKind Kind,
        uint Bubble, uint Activity, int State, Matrix4x4 Transform)
    {
        // Identity compares by reference; a proxy record's value changes when its source moves.
        public bool Equals(GroupKey other) => ReferenceEquals(Identity, other.Identity) && Tag == other.Tag
            && Row == other.Row && Kind == other.Kind && Bubble == other.Bubble && Activity == other.Activity
            && State == other.State && Transform.Equals(other.Transform);

        public override int GetHashCode() => HashCode.Combine(
            Identity is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Identity),
            Tag, Row, Kind, Bubble, Activity, State, Transform);
    }

    private readonly Dictionary<MeshData, SelectionBounds> meshBounds = [];
    private readonly Dictionary<MeshData, Vector3[]> posed = [];
    private readonly Dictionary<MeshInstance, Vector3[]> instancePoses = [];
    private readonly Dictionary<MeshInstance, SelectionBounds> instancePoseBounds = [];
    private readonly Dictionary<object, SelectionBounds> bounds = new(ReferenceEqualityComparer.Instance);
    private readonly List<object> objects = [];
    private readonly Dictionary<object, int> objectIndices = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<GroupKey, ObjectGroup> groups = [];
    private readonly Dictionary<object, ObjectGroup> objectGroups = new(ReferenceEqualityComparer.Instance);

    public IReadOnlyList<object> Objects => objects;
    /** Transient placements and world-space poses do not alter the authored grid snapshot. */
    public ulong AuthoredBoundsRevision { get; private set; }

    internal void AddRuntimeMesh(MeshInstance instance)
    {
        if (!meshBounds.ContainsKey(instance.Mesh))
        {
            var local = SelectionBounds.Empty;
            foreach (var vertex in instance.Mesh.Vertices) local = local.Include(vertex.Position);
            meshBounds.Add(instance.Mesh, local);
        }
        objectIndices[instance] = objects.Count; objects.Add(instance); AddToGroup(instance);
        bounds[instance] = MeshBounds(instance);
    }

    /** Renderer replacement removes runtime slots while preserving authored pose and proxy bounds. */
    internal void RemoveRuntimeMeshes(IReadOnlySet<object> removed)
    {
        if (removed.Count > 0) AuthoredBoundsRevision++;
        var discardedMeshes = new HashSet<MeshData>();
        foreach (object item in removed)
        {
            bounds.Remove(item); objectIndices.Remove(item);
            if (item is MeshInstance removedPose) { instancePoses.Remove(removedPose); instancePoseBounds.Remove(removedPose); }
            if (item is MeshInstance mesh) discardedMeshes.Add(mesh.Mesh);
            if (!objectGroups.Remove(item, out var group)) continue;
            group.Members.Remove(item);
            if (item is MeshInstance part) group.Meshes.Remove(part);
            if (group.Members.Count == 0) groups.Remove(ObjectKey(item));
        }
        objects.RemoveAll(item => removed.Contains(item));
        for (int index = 0; index < objects.Count; index++) objectIndices[objects[index]] = index;
        foreach (var kept in objects.OfType<MeshInstance>()) discardedMeshes.Remove(kept.Mesh);
        foreach (var mesh in discardedMeshes) { meshBounds.Remove(mesh); posed.Remove(mesh); }
    }

    /** Call when the host replaces a scene, before accepting selections from its outliner. */
    public void SetScene(SceneDocument scene, Action<int, int>? progress = null)
    {
        AuthoredBoundsRevision++;
        meshBounds.Clear(); posed.Clear(); bounds.Clear(); objects.Clear(); objectIndices.Clear();
        instancePoses.Clear(); instancePoseBounds.Clear();
        groups.Clear(); objectGroups.Clear();
        int total = scene.Instances.Count + scene.Markers.Count;
        progress?.Invoke(0, total);
        foreach (var instance in scene.Instances)
        {
            if (!meshBounds.ContainsKey(instance.Mesh))
            {
                var local = SelectionBounds.Empty;
                foreach (var vertex in instance.Mesh.Vertices) local = local.Include(vertex.Position);
                meshBounds.Add(instance.Mesh, local);
            }
            objectIndices[instance] = objects.Count; objects.Add(instance);
            AddToGroup(instance);
            bounds[instance] = MeshBounds(instance);
            if (objects.Count % 4096 == 0) progress?.Invoke(objects.Count, total);
        }
        foreach (var marker in scene.Markers)
        {
            objectIndices[marker] = objects.Count; objects.Add(marker); AddToGroup(marker); bounds[marker] = GetBounds(marker);
            if (objects.Count % 4096 == 0) progress?.Invoke(objects.Count, total);
        }
        progress?.Invoke(total, total);
    }

    /** Proxies join the same bounds index as package-backed scene objects. */
    public void AddProxy(ScenePointProxy point)
    {
        AuthoredBoundsRevision++;
        objectIndices[point] = objects.Count; objects.Add(point);
        AddToGroup(point);
        bounds[point] = GetBounds(point);
    }

    /** The replacement takes the old proxy's index and group. */
    internal bool ReplaceProxy(ScenePointProxy old, ScenePointProxy replacement)
    {
        if (!objectIndices.Remove(old, out int index)) return false;
        AuthoredBoundsRevision++;
        objects[index] = replacement;
        objectIndices[replacement] = index;
        bounds.Remove(old);
        if (objectGroups.Remove(old, out var group))
        {
            int member = group.Members.FindIndex(item => ReferenceEquals(item, old));
            if (member >= 0) group.Members[member] = replacement;
            groups.Remove(ObjectKey(old));
            groups[ObjectKey(replacement)] = group;
            objectGroups[replacement] = group;
        }
        bounds[replacement] = GetBounds(replacement);
        return true;
    }

    /** Picking tracks the same deformed vertices uploaded for the selection mask. */
    public void SetPose(MeshData mesh, IReadOnlyList<Vector3> positions)
    {
        AuthoredBoundsRevision++;
        posed[mesh] = positions.ToArray();
        var local = SelectionBounds.Empty;
        foreach (var position in positions) local = local.Include(position);
        meshBounds[mesh] = local;
        foreach (var instance in objects.OfType<MeshInstance>().Where(x => ReferenceEquals(x.Mesh, mesh)))
            bounds[instance] = MeshBounds(instance);
    }

    /** World-space snapshots override one placement and leave shared asset animation unchanged. */
    public void SetInstancePose(MeshInstance instance, IReadOnlyList<Vector3>? worldPositions)
    {
        if (IndexOf(instance) < 0) throw new ArgumentException("Pose instance is outside this scene.", nameof(instance));
        if (worldPositions is null)
        {
            instancePoses.Remove(instance); instancePoseBounds.Remove(instance); RefreshBounds(instance, authored: false); return;
        }
        Vector3[] positions = worldPositions.ToArray();
        if (positions.Length != instance.Mesh.Vertices.Length || positions.Length == 0
            || positions.Any(position => !float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)))
            throw new ArgumentException("Pose vertices must be finite and retain the source count.", nameof(worldPositions));
        var box = SelectionBounds.Empty;
        foreach (var position in positions) box = box.Include(position);
        instancePoses[instance] = positions; instancePoseBounds[instance] = box; bounds[instance] = box;
    }

    public SelectionBounds GetBounds(object item) => bounds.TryGetValue(item, out var stored) ? stored : item switch
    {
        MeshInstance instance => MeshBounds(instance),
        SceneMarker marker => new SelectionBounds(-Vector3.Abs(marker.RenderExtents), Vector3.Abs(marker.RenderExtents))
            .Transform(Matrix4x4.CreateFromQuaternion(marker.RenderRotation) * Matrix4x4.CreateTranslation(marker.RenderPosition)),
        ScenePointProxy point => new(point.Position - Vector3.Abs(point.Extents), point.Position + Vector3.Abs(point.Extents)),
        _ => SelectionBounds.Empty
    };

    private SelectionBounds MeshBounds(MeshInstance instance) =>
        instancePoseBounds.TryGetValue(instance, out var world) ? world :
        instance.CellBounds is { } cell ? new(cell.Min, cell.Max) :
        meshBounds.TryGetValue(instance.Mesh, out var local) && local.IsFinite
            ? local.Transform(instance.RenderTransform) : new(instance.RenderTransform.Translation, instance.RenderTransform.Translation);

    public int IndexOf(object item) => objectIndices.GetValueOrDefault(item, -1);

    /** The static grid retains authored placements while live objects use a separate query. */
    public SelectionBounds GetAuthoredBounds(object item) => item switch
    {
        MeshInstance { CellBounds: { } cell } => new SelectionBounds(cell.Min, cell.Max),
        MeshInstance instance => meshBounds.TryGetValue(instance.Mesh, out var local) && local.IsFinite
            ? local.Transform(instance.Transform) : new(instance.Transform.Translation, instance.Transform.Translation),
        SceneMarker marker => new SelectionBounds(-Vector3.Abs(marker.Extents), Vector3.Abs(marker.Extents))
            .Transform(Matrix4x4.CreateFromQuaternion(marker.Rotation) * Matrix4x4.CreateTranslation(marker.Position)),
        _ => GetBounds(item)
    };

    /** Authored changes rebuild the spatial snapshot; transient overrides use the separate live query. */
    public void RefreshBounds(object item, bool authored = true)
    {
        if (authored && objectIndices.ContainsKey(item)) AuthoredBoundsRevision++;
        bounds.Remove(item);
        bounds[item] = GetBounds(item);
    }

    public IReadOnlyList<MeshInstance> MeshParts(object selected) => objectGroups.TryGetValue(selected, out var group)
        ? group.Meshes : selected is MeshInstance mesh ? [mesh] : [];

    public IReadOnlyList<object> ObjectMembers(object selected) => objectGroups.TryGetValue(selected, out var group)
        ? group.Members : [selected];

    public MeshInstance ObjectRepresentative(MeshInstance selected) =>
        objectGroups.TryGetValue(selected, out var group) && group.Meshes.Count > 0 ? group.Meshes[0] : selected;

    public object ResolveObject(object selected) => selected is MeshInstance mesh ? ObjectRepresentative(mesh) : selected;

    /** Decoder identities separate repeated placements without comparing mesh-local transforms. */
    private void AddToGroup(object item)
    {
        var key = ObjectKey(item);
        if (!groups.TryGetValue(key, out var group)) groups.Add(key, group = new());
        group.Members.Add(item);
        if (item is MeshInstance mesh) group.Meshes.Add(mesh);
        objectGroups[item] = group;
    }

    /** Hand-built scenes without placement identities keep conservative source-and-transform grouping. */
    private static GroupKey ObjectKey(object item)
    {
        if (item is SceneMarker marker)
            return new(marker.ObjectPlacement ?? (object)marker, 0, 0, GroupKind.Identity,
                marker.BubbleHash, marker.ActivityTag, marker.StateOrdinal, default);
        if (item is not MeshInstance mesh) return new(item, 0, 0, GroupKind.Identity, 0, 0, 0, default);
        if (mesh.ObjectPlacement is { } placement)
            return new(placement, 0, 0, GroupKind.Identity, mesh.BubbleHash, mesh.ActivityTag, mesh.StateOrdinal, default);
        var key = new GroupKey(null, 0, 0, GroupKind.Identity, mesh.BubbleHash, mesh.ActivityTag, mesh.StateOrdinal, mesh.Transform);
        if (mesh.PlacementSourceTag != 0 && mesh.PlacementSourceOffset >= 0)
            return key with { Kind = GroupKind.PlacementRow, Tag = mesh.PlacementSourceTag, Row = mesh.PlacementSourceOffset };
        if (mesh.InstanceSourceTag != 0 && mesh.InstanceSourceIndex >= 0)
            return key with { Kind = GroupKind.InstanceRow, Tag = mesh.InstanceSourceTag, Row = mesh.InstanceSourceIndex };
        if (mesh.ObjectClassTag != 0) return key with { Kind = GroupKind.Class, Tag = mesh.ObjectClassTag };
        if (mesh.Mesh.TagHash != 0) return key with { Kind = GroupKind.Mesh, Tag = mesh.Mesh.TagHash,
            Row = mesh.Layer == SceneLayer.Volumes ? mesh.Mesh.PartIndex : 0 };
        return key with { Identity = mesh };
    }

    /** Framing covers every mesh part belonging to the selected placement. */
    public SelectionBounds GroupBounds(object item)
    {
        var parts = MeshParts(item);
        if (parts.Count == 0) return GetBounds(item);
        var result = SelectionBounds.Empty;
        foreach (var part in parts)
        {
            var partBounds = GetBounds(part);
            result = result.Include(partBounds.Minimum).Include(partBounds.Maximum);
        }
        return result;
    }

    /** Distances stay in world units even under nonuniform instance scale. */
    public object? Pick(ViewerCamera camera, Vector2 pixel, Vector2 size, SceneVisibility visibility,
        IReadOnlyList<int>? candidates = null, IReadOnlySet<object>? selectedOverlays = null,
        bool occludeOverlays = true) =>
        Pick(camera, pixel, size, visibility, out _, candidates, selectedOverlays, occludeOverlays);

    /** @param hitDistance World distance of the hit along the pick ray; infinity when nothing is hit. */
    public object? Pick(ViewerCamera camera, Vector2 pixel, Vector2 size, SceneVisibility visibility, out float hitDistance,
        IReadOnlyList<int>? candidates = null, IReadOnlySet<object>? selectedOverlays = null,
        bool occludeOverlays = true) =>
        Pick(camera, pixel, size, item => visibility.IsVisible(item) ? PickFaces.Both : PickFaces.None, out hitDistance,
            candidates, selectedOverlays, occludeOverlays);

    /**
     * @param faces The faces a ray can hit on each candidate; None skips it.
     * @param hitDistance World distance of the hit along the pick ray; infinity when nothing is hit.
     */
    public object? Pick(ViewerCamera camera, Vector2 pixel, Vector2 size, Func<object, PickFaces> faces, out float hitDistance,
        IReadOnlyList<int>? candidates = null, IReadOnlySet<object>? selectedOverlays = null,
        bool occludeOverlays = true)
    {
        hitDistance = float.PositiveInfinity;
        if (size.X <= 0 || size.Y <= 0 || pixel.X < 0 || pixel.Y < 0 || pixel.X >= size.X || pixel.Y >= size.Y ||
            !Matrix4x4.Invert(camera.ViewProjection(size.X / size.Y), out var inverse))
            return null;
        var ndc = new Vector2(pixel.X / size.X * 2 - 1, 1 - pixel.Y / size.Y * 2);
        var near = Vector4.Transform(new Vector4(ndc, 0, 1), inverse);
        var far = Vector4.Transform(new Vector4(ndc, .5f, 1), inverse);
        var origin = new Vector3(near.X, near.Y, near.Z) / near.W;
        var destination = new Vector3(far.X, far.Y, far.Z) / far.W;
        var direction = Vector3.Normalize(destination - origin);
        float best = (camera.FarPlane - camera.NearPlane) / MathF.Max(.001f, Vector3.Dot(direction, camera.Forward));
        object? selection = null;
        for (int index = 0; index < (candidates?.Count ?? objects.Count); index++)
        {
            var item = objects[candidates is null ? index : candidates[index]];
            if (SceneVisibility.IsOverlay(item)) continue;
            var sides = faces(item);
            if (sides == PickFaces.None) continue;
            if (item is MeshInstance { Layer: SceneLayer.Skybox or SceneLayer.Distant }) continue;
            var box = GetBounds(item);
            if (!RayBox(origin, direction, box, best, out float distance)) continue;
            if (item is MeshInstance instance)
            {
                bool worldPose = instancePoses.TryGetValue(instance, out var worldPositions);
                Matrix4x4 worldToLocal = Matrix4x4.Identity;
                if (!worldPose && !Matrix4x4.Invert(instance.RenderTransform, out worldToLocal)) continue;
                var localOrigin = Vector3.Transform(origin, worldToLocal);
                var localDirection = Vector3.TransformNormal(direction, worldToLocal);
                float nearest = best;
                var mesh = instance.Mesh;
                var positions = worldPose ? worldPositions : posed.GetValueOrDefault(mesh);
                // A mirrored placement flips which side of a triangle faces the camera.
                bool mirrored = !worldPose && instance.RenderTransform.GetDeterminant() < 0;
                for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
                {
                    Vector3 Position(uint index) => positions is null ? mesh.Vertices[index].Position : positions[index];
                    Vector3 a = Position(mesh.Indices[i]), b = Position(mesh.Indices[i + 1]), c = Position(mesh.Indices[i + 2]);
                    if (!RayTriangle(localOrigin, localDirection, a, b, c, out float along) || along >= nearest) continue;
                    if (sides != PickFaces.Both && sides != (Vector3.Dot(Vector3.Cross(b - a, c - a), localDirection) > 0 != mirrored
                        ? PickFaces.Front : PickFaces.Back)) continue;
                    nearest = along;
                }
                if (nearest >= best) continue;
                distance = nearest;
            }
            if (distance >= best) continue;
            best = distance;
            selection = item;
        }
        float geometryDistance = best;
        bool selectedOverlayHit = false;
        for (int index = 0; index < (candidates?.Count ?? objects.Count); index++)
        {
            var item = objects[candidates is null ? index : candidates[index]];
            if (!SceneVisibility.IsOverlay(item) || faces(item) == PickFaces.None) continue;
            bool xray = !occludeOverlays || (selectedOverlays?.Contains(item) ?? false);
            float limit = xray ? float.PositiveInfinity : geometryDistance;
            var box = GetBounds(item);
            float padding = (camera.Orthographic ? camera.ViewHeight :
                2 * MathF.Max(.01f, Vector3.Dot(box.Center - camera.Position, camera.Forward) + box.Radius) *
                    MathF.Tan(camera.VerticalFieldOfView(size.X / size.Y) / 2)) / size.Y * 8;
            var broad = new SelectionBounds(box.Minimum - new Vector3(padding), box.Maximum + new Vector3(padding));
            if (!RayBox(origin, direction, broad, limit, out _) ||
                !OverlayHit(item, camera, pixel, size, origin, direction, out float distance) || distance > limit) continue;
            if (selectedOverlayHit && !xray || xray == selectedOverlayHit && distance >= best) continue;
            selectedOverlayHit = xray;
            best = distance;
            selection = item;
        }
        if (selection is not null) hitDistance = best;
        return selection;
    }

    /** Wire overlays hit projected edges; small markers retain a usable center target. */
    private bool OverlayHit(object item, ViewerCamera camera, Vector2 pixel, Vector2 size,
        Vector3 origin, Vector3 direction, out float distance)
    {
        var view = camera.ViewProjection(size.X / size.Y);
        float nearest = float.PositiveInfinity;
        if (item is SceneMarker marker)
        {
            var extent = Vector3.Max(Vector3.Abs(marker.RenderExtents), new Vector3(.05f));
            var bounds = GetBounds(marker);
            float depth = MathF.Max(.01f, Vector3.Dot(marker.RenderPosition - camera.Position, camera.Forward));
            float worldPerPixel = (camera.Orthographic ? camera.ViewHeight :
                2 * depth * MathF.Tan(camera.VerticalFieldOfView(size.X / size.Y) / 2)) / size.Y;
            // Small markers need a center target even when all box edges fall between pixels.
            const float smallMarkerPixels = 14;
            var sizeInWorld = (bounds.Maximum - bounds.Minimum) * .5f;
            if (MathF.Max(sizeInWorld.X, MathF.Max(sizeInWorld.Y, sizeInWorld.Z)) / worldPerPixel <= smallMarkerPixels)
                Edge(marker.RenderPosition, marker.RenderPosition);
            for (int corner = 0; corner < 8; corner++)
                for (int bit = 1; bit <= 4; bit <<= 1)
                    if ((corner & bit) == 0) Edge(Corner(corner), Corner(corner | bit));
            Vector3 Corner(int index) => marker.RenderPosition + Vector3.Transform(new Vector3(
                (index & 1) == 0 ? -extent.X : extent.X,
                (index & 2) == 0 ? -extent.Y : extent.Y,
                (index & 4) == 0 ? -extent.Z : extent.Z), marker.RenderRotation);
        }
        else if (item is ScenePointProxy point)
        {
            var extent = Vector3.Max(Vector3.Abs(point.Extents), new Vector3(.15f));
            for (int axis = 0; axis < 3; axis++)
            {
                var offset = Vector3.Zero; offset[axis] = extent[axis];
                Edge(point.Position - offset, point.Position + offset);
            }
        }
        else if (item is MeshInstance volume)
        {
            var mesh = volume.Mesh;
            var positions = posed.GetValueOrDefault(mesh);
            var worldPositions = instancePoses.GetValueOrDefault(volume);
            for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
            {
                var a = Position(mesh.Indices[i]); var b = Position(mesh.Indices[i + 1]); var c = Position(mesh.Indices[i + 2]);
                Edge(a, b); Edge(b, c); Edge(c, a);
            }
            Vector3 Position(uint index) => worldPositions is not null ? worldPositions[index] : Vector3.Transform(positions is null
                ? mesh.Vertices[index].Position : positions[index], volume.RenderTransform);
        }
        distance = nearest;
        return float.IsFinite(nearest);

        void Edge(Vector3 a, Vector3 b)
        {
            if (ProjectedEdgeDistance(a, b, view, pixel, size, origin, direction, out float hit))
                nearest = MathF.Min(nearest, hit);
        }
    }

    /** Clip before dividing by W so edges crossing the eye cannot fill the screen. */
    private static bool ProjectedEdgeDistance(Vector3 a, Vector3 b, Matrix4x4 view,
        Vector2 pixel, Vector2 size, Vector3 origin, Vector3 direction, out float distance)
    {
        distance = 0;
        var ca = Vector4.Transform(new Vector4(a, 1), view);
        var cb = Vector4.Transform(new Vector4(b, 1), view);
        float start = 0, end = 1;
        if (!Clip(ca.Z, cb.Z) || !Clip(ca.W - ca.Z, cb.W - cb.Z)) return false;
        var originalA = a; var originalCa = ca;
        a = Vector3.Lerp(originalA, b, start); b = Vector3.Lerp(originalA, b, end);
        ca = Vector4.Lerp(originalCa, cb, start); cb = Vector4.Lerp(originalCa, cb, end);
        if (ca.W <= 0 || cb.W <= 0) return false;
        var sa = new Vector2(ca.X / ca.W * .5f + .5f, .5f - ca.Y / ca.W * .5f) * size;
        var sb = new Vector2(cb.X / cb.W * .5f + .5f, .5f - cb.Y / cb.W * .5f) * size;
        var delta = sb - sa;
        float t = delta.LengthSquared() < .0001f ? 0 : Math.Clamp(Vector2.Dot(pixel - sa, delta) / delta.LengthSquared(), 0, 1);
        // Six pixels match the size of a small point target.
        const float tolerancePixels = 6;
        if (Vector2.DistanceSquared(pixel, sa + delta * t) > tolerancePixels * tolerancePixels) return false;
        float u = (t / cb.W) / ((1 - t) / ca.W + t / cb.W);
        distance = Vector3.Dot(Vector3.Lerp(a, b, u) - origin, direction);
        return distance >= 0 && float.IsFinite(distance);

        bool Clip(float first, float last)
        {
            if (first < 0 && last < 0) return false;
            if (first < 0) start = MathF.Max(start, first / (first - last));
            if (last < 0) end = MathF.Min(end, first / (first - last));
            return start <= end;
        }
    }

    /** Parallel rays may hit a slab only when their origin lies inside it. */
    public static bool RayBox(Vector3 origin, Vector3 direction, SelectionBounds box, float limit, out float distance)
    {
        distance = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            float component = direction[axis];
            if (MathF.Abs(component) < 1e-8f)
            {
                if (origin[axis] < box.Minimum[axis] || origin[axis] > box.Maximum[axis]) return false;
                continue;
            }
            float a = (box.Minimum[axis] - origin[axis]) / component;
            float b = (box.Maximum[axis] - origin[axis]) / component;
            distance = MathF.Max(distance, MathF.Min(a, b));
            limit = MathF.Min(limit, MathF.Max(a, b));
            if (distance > limit) return false;
        }
        return box.IsFinite && limit >= 0;
    }

    /** Both triangle faces are pickable, matching the editor's two-sided geometry view. */
    public static bool RayTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b,
        Vector3 c, out float distance)
    {
        distance = 0;
        var edge1 = b - a;
        var edge2 = c - a;
        var p = Vector3.Cross(direction, edge2);
        float determinant = Vector3.Dot(edge1, p);
        if (MathF.Abs(determinant) < 1e-8f) return false;
        float inverse = 1 / determinant;
        var offset = origin - a;
        float u = Vector3.Dot(offset, p) * inverse;
        if (u < 0 || u > 1) return false;
        var q = Vector3.Cross(offset, edge1);
        float v = Vector3.Dot(direction, q) * inverse;
        if (v < 0 || u + v > 1) return false;
        distance = Vector3.Dot(edge2, q) * inverse;
        return distance >= 0;
    }
}
