using System.Numerics;
using SunriseStudio.Formats;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Authoring;

/**
 * Moved occluders: every tome of every bubble that places the row opens the old place, and closes the
 * new place when it blocked the old one. Runs before InstanceCullRule, which adds generated lists.
 */
internal sealed class OcclusionTomeRule : IObjectEditRule
{
    // Blocked shares of installed meshes cluster near 0 and near 1; the middle splits them.
    private const double OccluderShare = 0.5;
    // World margin around the moved meshes; it covers two voxels of the coarsest tome.
    private const float Margin = 1;

    public ObjectEditLayout Layout => ObjectEditLayout.StaticInstance;

    public bool BuildOnly => true;

    /** A removed row only opens its old place. */
    public IReadOnlyCollection<ObjectEditKind> Kinds => [ObjectEditKind.PlacementTransform, ObjectEditKind.RemoveInstance];

    /** One edited row through one placement, at its installed and its edited pose. */
    private sealed record Moved(ObjectEdit Edit, BubbleGraph.Placement Placement, Vector3[] Removed, Vector3[] Added);

    /** What one tome gets: the moved meshes of every bubble that uses it, and their neighbours. */
    private sealed class TomeWork
    {
        private readonly HashSet<(string, BubbleGraph.Placement)> keys = [];
        private readonly HashSet<uint> bubbles = [];
        internal List<Moved> Moved { get; } = [];
        internal List<IReadOnlyList<Vector3>> Kept { get; } = [];

        /** A bubble adds its neighbours once, and a row placed through one placement moves once. */
        internal void Add(uint bubble, IEnumerable<Moved> moved, IEnumerable<IReadOnlyList<Vector3>> kept)
        {
            if (!bubbles.Add(bubble)) return;
            Moved.AddRange(moved.Where(item => keys.Add((item.Edit.Key, item.Placement))));
            Kept.AddRange(kept);
        }
    }

    public void Apply(ObjectEditContext context, IReadOnlyList<ObjectEdit> edits)
    {
        var graph = new BubbleGraph(context);
        var meshes = new Dictionary<uint, MeshData[]>();
        var models = new Dictionary<uint, Vector3[]>();
        var tomes = new SortedDictionary<uint, TomeWork>();
        var editedRows = edits.Select(edit => (edit.Target.SourceTag, edit.Target.Index)).ToHashSet();
        foreach (var (bubble, scope) in graph.Bubbles.OrderBy(pair => pair.Key))
        {
            var moved = new List<Moved>();
            foreach (var edit in edits)
                foreach (var placement in scope.Placements.Where(placement => placement.Table == edit.Target.SourceTag))
                    if (Move(context, graph.Tags, edit, placement, meshes, models) is { } item) moved.Add(item);
            if (moved.Count == 0) continue;
            var region = Bounds(moved.SelectMany(item => item.Removed.Concat(item.Added)));
            region = (region.Min - new Vector3(Margin), region.Max + new Vector3(Margin));
            var kept = Kept(context, bubble, region, editedRows, meshes);
            foreach (uint map in scope.Maps.Order())
            {
                byte[] data = graph.Tags.Read(map);
                foreach (uint tome in new[]
                         {
                             ObjectRows.Field<SVisibilityTomeMap, uint>(data, 0, nameof(SVisibilityTomeMap.FineTome)),
                             ObjectRows.Field<SVisibilityTomeMap, uint>(data, 0, nameof(SVisibilityTomeMap.CoarseTome))
                         })
                {
                    // An object edit cannot rewrite a project entry; the project's own tome is its to rebake.
                    if (graph.Tags.IsAdded(tome))
                    {
                        context.Note($"Occlusion tome {tome:X8} belongs to this project and was left unchanged.");
                        continue;
                    }
                    if (!context.Catalog.TryGetEntry(tome, out _)) continue;
                    if (!tomes.TryGetValue(tome, out var work)) tomes[tome] = work = new TomeWork();
                    work.Add(bubble, moved, kept);
                }
            }
        }
        foreach (var (tag, work) in tomes) Rewrite(context, tag, work);
    }

    /** Opens and closes one tome, or notes why it stays as it is. */
    private static void Rewrite(ObjectEditContext context, uint tag, TomeWork work)
    {
        byte[] bytes = context.Current(tag);
        if (!TomeReader.IsTome(bytes)) return;
        try
        {
            var tome = TomeReader.Read(bytes);
            var edit = new TomeOcclusionEdit();
            edit.Kept.AddRange(work.Kept);
            // Opening only adds sight lines, so every old place opens; only a proved occluder closes its new place.
            foreach (var item in work.Moved)
            {
                edit.Removed.Add(item.Removed);
                if (item.Added.Length != 0 && TomeEditor.BlockedShare(tome, item.Removed, work.Kept) >= OccluderShare) edit.Added.Add(item.Added);
            }
            TomeEditor.Apply(tome, edit);
            byte[] written = TomeWriter.Write(tome);
            if (written.AsSpan().SequenceEqual(bytes)) return;
            context.ReplaceCurrent(tag, written);
            foreach (var contributor in work.Moved.Select(item => item.Edit).Distinct()) context.Produced(contributor, tag);
        }
        catch (Exception error) when (error is NotSupportedException or InvalidDataException)
        {
            context.Note($"Occlusion tome {tag:X8} was left unchanged: {error.Message}");
        }
    }

    /** The row's mesh at its installed pose and at its edited pose, through one placement of its table. */
    private static Moved? Move(ObjectEditContext context, IsolationTags tags, ObjectEdit edit, BubbleGraph.Placement placement,
        Dictionary<uint, MeshData[]> meshes, Dictionary<uint, Vector3[]> models)
    {
        uint table = edit.Target.SourceTag;
        int index = edit.Target.Index;
        byte[] original = context.Original(table), current = context.Current(table);
        var originalRows = ObjectRows.Rows<SStaticMeshInstances, SStaticInstanceTransform>(original, 0, "Instances.Transforms",
            SStaticInstanceTransform.Class);
        var rows = ObjectRows.Rows<SStaticMeshInstances, SStaticInstanceTransform>(current, 0, "Instances.Transforms",
            SStaticInstanceTransform.Class);
        if ((uint)index >= originalRows.Length || rows.Length != originalRows.Length)
            throw new InvalidDataException("The edited instance row is missing.");
        if (Resource(current, index) is not { } resource || context.ClassOf(resource) != SStaticMesh.Class) return null;
        // A project list has no installed original; object edits never change it.
        byte[] originalList = tags.IsAdded(placement.List) ? tags.Read(placement.List) : context.Original(placement.List),
            currentList = tags.Read(placement.List);
        var listRows = ObjectRows.Rows<SMapPlacements, AuthoredPlacementEntry>(originalList, 0, nameof(SMapPlacements.Placements),
            AuthoredPlacementEntry.Class);
        if ((uint)placement.Index >= listRows.Length) throw new InvalidDataException("The static placement row is missing.");
        // The placement row's transform applies after the instance row's.
        var before = Matrix(originalRows[index].Scale, originalRows[index].Rotation, originalRows[index].Position)
            * Matrix(listRows[placement.Index].UniformScale, listRows[placement.Index].Rotation, listRows[placement.Index].Position);
        // The current list row is not checked against its array, so each field is checked alone.
        int row = placement.Row;
        var after = Matrix(rows[index].Scale, rows[index].Rotation, rows[index].Position)
            * Matrix(ObjectRows.Field<AuthoredPlacementEntry, float>(currentList, row, nameof(AuthoredPlacementEntry.UniformScale)),
                ObjectRows.Field<AuthoredPlacementEntry, Quaternion>(currentList, row, nameof(AuthoredPlacementEntry.Rotation)),
                ObjectRows.Field<AuthoredPlacementEntry, Vector3>(currentList, row, nameof(AuthoredPlacementEntry.Position)));
        bool removed = edit.Kind == ObjectEditKind.RemoveInstance;
        if (before == after && !removed) return null;
        var model = Model(context, resource, meshes, models);
        return model.Length == 0 ? null : new(edit, placement, Transform(model, before), removed ? [] : Transform(model, after));
    }

    /** The static resource that draws one row, from the table's ranges. */
    private static uint? Resource(byte[] table, int index)
    {
        var resources = ObjectRows.Rows<SStaticMeshInstances, SStaticInstanceResource>(table, 0, "Instances.Resources",
            SStaticInstanceResource.Class);
        foreach (ref readonly var range in ObjectRows.Rows<SStaticMeshInstances, SStaticMeshInstanceGroup>(table, 0, "Instances.Ranges",
                     SStaticMeshInstanceGroup.Class))
        {
            int count = range.Count, first = range.FirstInstance, resource = range.ResourceIndex;
            if (index >= first && index < first + count && resource < resources.Length) return resources[resource].StaticMesh;
        }
        return null;
    }

    /** Every part of one static resource as one triangle list, in model space. */
    private static Vector3[] Model(ObjectEditContext context, uint resource, Dictionary<uint, MeshData[]> meshes,
        Dictionary<uint, Vector3[]> models)
    {
        if (models.TryGetValue(resource, out var model)) return model;
        var scene = Loader(context, meshes, null).LoadMesh(resource);
        return models[resource] = scene.Instances.Where(instance => instance.Layer == SceneLayer.Geometry)
            .SelectMany(instance => Triangles(instance.Mesh, Matrix4x4.Identity)).ToArray();
    }

    /** Static and terrain meshes of a bubble near the region, except the edited rows, clipped to it. */
    private static List<IReadOnlyList<Vector3>> Kept(ObjectEditContext context, uint bubble, (Vector3 Min, Vector3 Max) region,
        HashSet<(uint, int)> editedRows, Dictionary<uint, MeshData[]> meshes)
    {
        var scene = Loader(context, meshes, region).LoadBubble(bubble);
        var kept = new List<IReadOnlyList<Vector3>>();
        foreach (var instance in scene.Instances)
        {
            if (instance.Layer is not (SceneLayer.Geometry or SceneLayer.Terrain)) continue;
            if (instance.InstanceSourceTag != 0 && editedRows.Contains((instance.InstanceSourceTag, instance.InstanceSourceIndex))) continue;
            var triangles = new List<Vector3>();
            var all = Triangles(instance.Mesh, instance.Transform);
            for (int i = 0; i + 2 < all.Length; i += 3)
            {
                var box = Bounds(all.AsSpan(i, 3).ToArray());
                if (box.Min.X > region.Max.X || box.Min.Y > region.Max.Y || box.Min.Z > region.Max.Z
                    || box.Max.X < region.Min.X || box.Max.Y < region.Min.Y || box.Max.Z < region.Min.Z) continue;
                triangles.AddRange(all.AsSpan(i, 3));
            }
            if (triangles.Count > 0) kept.Add(triangles);
        }
        return kept;
    }

    /** Reads the compile's own bytes, so raw deltas apply and edited rows hold their new values. */
    private static AssetSceneLoader Loader(ObjectEditContext context, Dictionary<uint, MeshData[]> meshes,
        (Vector3 Min, Vector3 Max)? region) =>
        new(context.Catalog) { Source = context.ReadLayer, Region = region, MeshCache = meshes };

    private static Vector3[] Triangles(MeshData mesh, Matrix4x4 transform) =>
        mesh.Indices.Select(index => Vector3.Transform(mesh.Vertices[index].Position, transform)).ToArray();

    private static Vector3[] Transform(Vector3[] model, Matrix4x4 transform) =>
        model.Select(point => Vector3.Transform(point, transform)).ToArray();

    /** Scale, then the normalized rotation, then the translation, as the scene decoder builds a placement. */
    private static Matrix4x4 Matrix(float scale, Quaternion rotation, Vector3 translation) =>
        Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(ObjectRows.Rotation(rotation))
            * Matrix4x4.CreateTranslation(translation);

    private static (Vector3 Min, Vector3 Max) Bounds(IEnumerable<Vector3> points)
    {
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        foreach (var point in points) { min = Vector3.Min(min, point); max = Vector3.Max(max, point); }
        return (min, max);
    }

    /** Each bubble's static placements and visibility maps, by the typed map edges the scene decoder follows. */
    private sealed class BubbleGraph
    {
        internal sealed record Placement(uint List, int Row, int Index, uint Table);
        internal sealed class Scope
        {
            internal HashSet<uint> Maps { get; } = [];
            internal List<Placement> Placements { get; } = [];
        }

        private readonly ObjectEditContext context;
        private readonly InstanceIsolationSources sources;
        private readonly HashSet<(uint, uint)> visited = [];
        private readonly HashSet<(uint, uint)> lists = [];
        internal Dictionary<uint, Scope> Bubbles { get; } = [];

        internal BubbleGraph(ObjectEditContext context)
        {
            this.context = context;
            sources = new InstanceIsolationSources(context);
            foreach (var root in sources.OfClass(SMapRoot.Class)) Visit(root.Tag, 0, 0);
        }

        internal IsolationTags Tags => sources.Tags;

        private void Visit(uint tag, uint bubble, int depth)
        {
            if (depth > 24) throw new InvalidDataException("The static map graph exceeds its limit.");
            if (!visited.Add((tag, bubble))) return;
            var node = sources.Read(tag);
            if (!node.Valid) return;
            switch (node.Class)
            {
                case SMapRoot.Class:
                    foreach (var child in node.Children)
                        if (child.Resolve(sources.Tags) is { } resolved) Visit(resolved, 0, depth + 1);
                    break;
                case SBubbleParent.Class:
                    Visit(node.Link, tag, depth + 1);
                    break;
                case SBubbleDefinition.Class or SScenarioTagSet.Class:
                    foreach (var child in node.Children)
                        if (child.Resolve(sources.Tags) is { } resolved) Visit(resolved, bubble, depth + 1);
                    break;
                case SScenarioStateEntry.Class:
                    Visit(node.Link, bubble, depth + 1);
                    break;
                case SMapContainer.Class when bubble != 0:
                    foreach (var child in node.Children) AddList(child.Tag, bubble);
                    break;
            }
        }

        private void AddList(uint tag, uint bubble)
        {
            if (sources.Tags.ClassOf(tag) != SMapPlacements.Class || !lists.Add((tag, bubble))) return;
            var list = sources.Read(tag);
            if (!list.Valid) return;
            if (!Bubbles.TryGetValue(bubble, out var scope)) Bubbles[bubble] = scope = new Scope();
            foreach (uint map in list.Maps)
                if (sources.Tags.ClassOf(map) == SVisibilityTomeMap.Class) scope.Maps.Add(map);
            foreach (var placement in list.Placements)
                if (sources.Read(placement.Owner) is { Valid: true, Class: SMapResourceLink.Class } owner)
                    scope.Placements.Add(new(placement.List, placement.Row, placement.Index, owner.Link));
        }
    }
}
