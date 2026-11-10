using System.Numerics;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

public sealed partial class AssetViewport
{
    private sealed class RuntimeClass(LiveScenePrototype source, object? native)
    {
        public LiveScenePrototype Source { get; } = source;
        public object? Native { get; } = native;
        public List<RuntimeSlot> Slots { get; } = [];
    }
    private sealed class RuntimeSlot(RuntimeClass prototype, MeshInstance[] meshes)
    {
        public RuntimeClass Prototype { get; } = prototype;
        public MeshInstance[] Meshes { get; } = meshes;
        public string? Key { get; set; }
    }
    /** Entity classes and drawn models share one prototype table; the geometry budget still applies. */
    public const int MaximumLivePrototypes = 256;
    private readonly Dictionary<uint, RuntimeClass> runtimeClasses = [];
    private readonly Dictionary<string, RuntimeSlot> runtimeInstances = new(StringComparer.Ordinal);
    private readonly HashSet<int> runtimeIndices = [];
    private readonly List<int> activeRuntimeIndices = [];
    private readonly HashSet<object> activeRuntimeObjects = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object> runtimeObjects = new(ReferenceEqualityComparer.Instance);
    private long runtimeDiagnosticBytes;
    public int LiveInstanceCount => runtimeInstances.Count;
    public int LivePrototypeCount => runtimeClasses.Count;
    public int LiveSlotCount => runtimeClasses.Values.Sum(value => value.Slots.Count);
    public int LivePrototypeRevision { get; private set; }
    public bool HasLivePrototype(uint classTag) => runtimeClasses.ContainsKey(classTag);

    /** Prototype upload runs on the graphics thread and leaves the authored scene collection untouched. */
    public void RegisterLivePrototype(LiveScenePrototype prototype)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Scene is null) throw new InvalidOperationException("Load a scene before registering live classes.");
        if (runtimeClasses.ContainsKey(prototype.ClassTag)) return;
        if (prototype.Scene.Instances.Count is 0 or > 4096)
            throw new NotSupportedException("Live classes need between 1 and 4096 decoded mesh parts.");
        var members = prototype.Scene.Instances.ToHashSet();
        if (members.Count != prototype.Scene.Instances.Count || prototype.NativePlan?.Groups
            .SelectMany(group => group.Parts).Any(part => !members.Contains(part.Instance)) == true)
            throw new InvalidDataException("Live prototype geometry and native plan have different source instances.");
        if (runtimeClasses.Count >= MaximumLivePrototypes)
            throw new NotSupportedException($"Live preview supports {MaximumLivePrototypes} classes and models per loaded scene.");
        long bytes = prototype.Scene.Instances.Select(part => part.Mesh).Distinct().Where(mesh => !meshes.ContainsKey(mesh))
            .Sum(mesh => mesh.Vertices.LongLength * 32 + mesh.Indices.LongLength * 4);
        if (bytes > 256L * 1024 * 1024 - runtimeDiagnosticBytes)
            throw new NotSupportedException("Live diagnostic geometry budget is full.");
        object? native = prototype.NativePlan is { } plan && nativeScene is not null
            ? nativeScene.UploadRuntimePrototype(plan) : null;
        foreach (var mesh in prototype.Scene.Instances.Select(part => part.Mesh).Distinct())
        {
            if (meshes.ContainsKey(mesh)) continue;
            var vertices = mesh.Vertices.Select(vertex => new RenderVertex(vertex.Position, vertex.Normal, vertex.TexCoord)).ToArray();
            meshes.Add(mesh, renderer.CreateMesh(vertices, mesh.Indices));
        }
        runtimeDiagnosticBytes += bytes;
        runtimeClasses.Add(prototype.ClassTag, new(prototype, native));
        Visibility.AddRuntimeMeshes(prototype.Scene.Instances);
    }

    /** A snapshot contains stable handle-and-generation keys; absent keys despawn and release their slots. */
    public void SetLiveInstances(IReadOnlyList<LiveSceneInstance> instances)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        const int maximumInstances = 2048;
        if (instances.Count > maximumInstances) throw new NotSupportedException("Live instance limit is 2048.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in instances)
        {
            if (string.IsNullOrWhiteSpace(item.Key) || !keys.Add(item.Key)) throw new ArgumentException("Live keys must be unique and nonempty.");
            if (!runtimeClasses.TryGetValue(item.ClassTag, out var prototype)) throw new ArgumentException($"Live class {item.ClassTag:X8} is not prepared.");
            if (!Finite(item.Transform) || MathF.Abs(item.Transform.M14) + MathF.Abs(item.Transform.M24) + MathF.Abs(item.Transform.M34) > 1e-5f
                || MathF.Abs(item.Transform.M44 - 1) > 1e-5f || !Matrix4x4.Invert(item.Transform, out _) && !Collapsed(item.Transform))
                throw new ArgumentException("Live instance transform must be finite, invertible and affine.");
            if (runtimeInstances.TryGetValue(item.Key, out var old) && !ReferenceEquals(old.Prototype, prototype))
                throw new ArgumentException("A live generation cannot change its class.");
            foreach (var group in prototype.Source.NativePlan?.Groups ?? [])
                foreach (var part in group.Parts)
                    NativeEntityPalettes.ValidatePlacement(part.Instance.Transform * item.Transform, group.Material.Palette);
        }
        int extraSlots = 0, extraParts = 0;
        foreach (var group in instances.GroupBy(item => item.ClassTag))
        {
            var prototype = runtimeClasses[group.Key];
            int extra = Math.Max(0, group.Count() - prototype.Slots.Count);
            extraSlots += extra; extraParts = checked(extraParts + extra * prototype.Source.Scene.Instances.Count);
        }
        int existingParts = runtimeClasses.Values.Sum(value => value.Slots.Sum(slot => slot.Meshes.Length));
        if (extraSlots > maximumInstances - LiveSlotCount || extraParts > 131072 - existingParts)
            throw new NotSupportedException("Live slot budget is full; reload the scene to release unused classes.");
        foreach (var key in runtimeInstances.Keys.Where(key => !keys.Contains(key)).ToArray())
        {
            var slot = runtimeInstances[key];
            nativeScene?.RemoveLivePalettes(slot.Meshes);
            RemoveLivePoseGeometry(slot.Meshes);
            if (SelectedObject is { } selected && slot.Meshes.Contains(selected)) SetSelection(null);
            slot.Key = null; runtimeInstances.Remove(key);
        }
        var changed = new List<MeshInstance>();
        foreach (var item in instances)
        {
            if (!runtimeInstances.TryGetValue(item.Key, out var slot))
            {
                var prototype = runtimeClasses[item.ClassTag];
                slot = prototype.Slots.FirstOrDefault(value => value.Key is null) ?? CreateRuntimeSlot(prototype);
                slot.Key = item.Key; runtimeInstances.Add(item.Key, slot);
            }
            for (int index = 0; index < slot.Meshes.Length; index++)
            {
                var mesh = slot.Meshes[index];
                mesh.PreviewTransform = mesh.Transform * item.Transform;
                mesh.Name = item.Name.Length > 0 ? item.Name : slot.Prototype.Source.Scene.Name;
                selection.RefreshBounds(mesh, authored: false); changed.Add(mesh);
            }
        }
        activeRuntimeIndices.Clear(); activeRuntimeObjects.Clear();
        foreach (var slot in runtimeInstances.Values)
            foreach (var mesh in slot.Meshes)
            { activeRuntimeIndices.Add(selection.IndexOf(mesh)); activeRuntimeObjects.Add(mesh); }
        nativeScene?.RefreshLiveTransforms(changed);
        HoveredObject = null;
    }

    public void ClearLiveInstances() => SetLiveInstances([]);
    public IEnumerable<LiveSceneInstanceView> LiveInstances =>
        runtimeInstances.Select(pair => new LiveSceneInstanceView(pair.Key, pair.Value.Prototype.Source.ClassTag, pair.Value.Meshes));
    public LiveSceneInstanceView? TryGetLiveInstance(string key) => runtimeInstances.TryGetValue(key, out var slot)
        ? new(key, slot.Prototype.Source.ClassTag, slot.Meshes) : null;

    /** Pool growth uploads no new mesh data; each slot owns only placement rows and stable part handles. */
    private RuntimeSlot CreateRuntimeSlot(RuntimeClass prototype)
    {
        if (LiveSlotCount >= 2048) throw new NotSupportedException("Live slot pool is full; reload the scene to release unused classes.");
        if (renderIndexDirty || nativeHandleOffsets.Length != selection.Objects.Count + 1) BuildRenderIndex();
        int firstIndex = selection.Objects.Count;
        var placement = new SceneObjectPlacement(0, -1, Matrix4x4.Identity);
        var replacements = prototype.Source.Scene.Instances.ToDictionary(source => source, source => new MeshInstance
        {
            Mesh = source.Mesh, Transform = source.Transform, Name = source.Name, ObjectPlacement = placement,
            Layer = SceneLayer.Objects, ObjectClassTag = prototype.Source.ClassTag,
            ModelSourceConfigTag = source.ModelSourceConfigTag, ModelSourceResourceClass = source.ModelSourceResourceClass,
            InitiallyVisible = source.InitiallyVisible, InitialVisibilityReason = source.InitialVisibilityReason,
            InstanceSourceTag = source.InstanceSourceTag, InstanceSourceIndex = source.InstanceSourceIndex,
            NativeInstanceData = source.NativeInstanceData, Category = source.Category, Tags = source.Tags
        });
        var slot = new RuntimeSlot(prototype, replacements.Values.ToArray());
        foreach (var mesh in slot.Meshes)
        {
            selection.AddRuntimeMesh(mesh); runtimeIndices.Add(selection.IndexOf(mesh));
            runtimeObjects.Add(mesh);
        }
        if (prototype.Native is { } native)
            foreach (var (instance, handle) in nativeScene!.AddRuntimeSlot(native, replacements))
            {
                if (!nativeObjectHandles.TryGetValue(instance, out var handles)) nativeObjectHandles.Add(instance, handles = []);
                handles.Add(handle);
            }
        prototype.Slots.Add(slot); AppendRuntimeRenderIndex(firstIndex);
        return slot;
    }

    /** Runtime slots extend dense indices without revisiting authored placements or marker lines. */
    private void AppendRuntimeRenderIndex(int firstIndex)
    {
        int count = selection.Objects.Count;
        Array.Resize(ref nativeHandleOffsets, count + 1); Array.Resize(ref itemKinds, count);
        Array.Resize(ref markerLineStarts, count); Array.Resize(ref backdropItems, count); Array.Resize(ref itemBubbles, count);
        Array.Resize(ref itemLod, count);
        // Runtime slots get no decorator LOD test (-1), so CollectVisible never drops them for decorator LOD.
        int indexed = itemDecorators.Length;
        Array.Resize(ref itemDecorators, count); Array.Fill(itemDecorators, -1, indexed, count - indexed);
        var extra = new List<int>();
        for (int index = firstIndex; index < count; index++)
        {
            var instance = (MeshInstance)selection.Objects[index];
            itemLod[index] = StaticLod.Place(instance);
            nativeHandleOffsets[index] = nativeHandleMap.Length + extra.Count;
            if (nativeObjectHandles.TryGetValue(instance, out var handles)) extra.AddRange(handles);
            itemKinds[index] = nativeHandleOffsets[index] != nativeHandleMap.Length + extra.Count
                ? RenderItemKind.NativeMesh : RenderItemKind.FallbackMesh;
        }
        nativeHandleOffsets[count] = nativeHandleMap.Length + extra.Count;
        int previous = nativeHandleMap.Length; Array.Resize(ref nativeHandleMap, previous + extra.Count);
        extra.CopyTo(nativeHandleMap, previous);
    }

    private void QueryRuntime(Vector4[] planes, Func<int, bool> shown, List<int> result)
    {
        foreach (int index in activeRuntimeIndices)
        {
            if (livePoseIndices.Contains(index)) continue;
            var bounds = selection.GetBounds(selection.Objects[index]);
            if (shown(index) && SceneGrid.SphereInFrustum(bounds.Center, bounds.Radius, planes)) result.Add(index);
        }
    }

    private void ResetRuntimeClasses()
    {
        runtimeClasses.Clear(); runtimeInstances.Clear(); runtimeIndices.Clear(); activeRuntimeIndices.Clear();
        activeRuntimeObjects.Clear(); runtimeDiagnosticBytes = 0;
        runtimeObjects.Clear();
        LivePrototypeRevision++;
    }

    /** A replaced native renderer cannot retain prototype tokens owned by the previous renderer. */
    private void ReleaseRuntimeClasses()
    {
        if (runtimeClasses.Count == 0) return;
        if (SelectedObject is { } selected && runtimeObjects.Contains(selected)) SetSelection(null);
        var discarded = runtimeClasses.Values.SelectMany(value => value.Source.Scene.Instances)
            .Select(value => value.Mesh).ToHashSet();
        foreach (var retained in Scene!.Instances) discarded.Remove(retained.Mesh);
        nativeScene?.RemoveLivePalettes(runtimeObjects.OfType<MeshInstance>());
        RemoveLivePoseGeometry(runtimeObjects.OfType<MeshInstance>().ToArray());
        selection.RemoveRuntimeMeshes(runtimeObjects);
        ReindexLivePoseGeometry();
        foreach (var mesh in discarded) if (meshes.Remove(mesh, out var gpu)) gpu.Dispose();
        ResetRuntimeClasses();
        gridDirty = true; renderIndexDirty = true;
    }

    private bool PreviewContextVisible(object item) => Backdrop.IsVisible(item)
        && (!runtimeObjects.Contains(item) || activeRuntimeObjects.Contains(item));
}
