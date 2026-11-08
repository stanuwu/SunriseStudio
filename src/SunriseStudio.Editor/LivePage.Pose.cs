using System.Numerics;
using ImGuiNET;
using SunriseStudio.Live;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** Captured game draws animate live objects; the selected one also gets posed pick geometry. */
public sealed partial class LivePage
{
    /** A draw missing from later captures stops animating after this long. */
    private const ulong DrawHoldMilliseconds = 250;
    private bool syncAnimation = true;
    private LiveDraws? mergedDraws;
    private readonly Dictionary<uint, LiveDraw> drawsByComponent = [];
    private Dictionary<uint, LiveDraw[]> drawsByOwner = [];
    private long drawRevision, paletteRevision;
    private (long Draws, long Instances, bool Placed, NativeSceneRenderer? Renderer, uint Player)? paletteKey;
    private readonly Dictionary<MeshInstance, (LiveDraw Draw, uint Entity)> animated = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<MeshInstance> playerMeshes = new(ReferenceEqualityComparer.Instance);
    /** Parts of a drawn model that got no palette; they would stand in their rest pose, so they are hidden. */
    private readonly HashSet<MeshInstance> unanimated = new(ReferenceEqualityComparer.Instance);
    private IReadOnlyDictionary<string, int> animationNotes = new Dictionary<string, int>();
    private uint playerOwner = uint.MaxValue;
    private readonly Dictionary<(uint Entity, uint Model), LivePoseModelBinding> poseBindings = [];
    private readonly Dictionary<(uint Entity, uint Model), string> poseErrors = [];
    private Task<(LivePoseModelBinding? Binding, string Error)>? poseWork;
    private (uint Entity, uint Model) poseWorkKey;
    private CancellationTokenSource? poseCancellation;
    private (PackageCatalog Catalog, long Reads, long Overlay)? poseSource, poseWorkSource;
    private AssetViewport? poseViewport;
    private (long Palettes, object? Selection, int Parts, int Bindings)? poseKey;
    private string poseStatus = "";
    public bool AnimationEnabled { get => syncAnimation; set => syncAnimation = value; }
    public string SelectedPoseStatus => poseStatus;

    /**
     * Folds the newest capture into the held draws and tells the spawned binding which models to show.
     * The game copies palettes only for owners Studio shows a model for, the player and the selection.
     */
    private void MergeDraws()
    {
        bool wanted = syncAnimation && MatchesWorld() && snapshot?.InWorld == true;
        connection!.RequestDraws(wanted, wanted ? PoseOwners() : [], wanted ? snapshot?.PlayerHandle : null);
        dynamic.SetPlayer(wanted ? snapshot?.PlayerHandle : null);
        if (!wanted) { if (mergedDraws is not null || drawsByComponent.Count > 0) ResetDraws(); return; }
        ulong now = (ulong)Environment.TickCount64;
        bool changed = false;
        if (connection.Draws is { } frame && !ReferenceEquals(frame, mergedDraws))
        {
            mergedDraws = frame; changed = true;
            foreach (var group in frame.Draws.GroupBy(draw => draw.Owner))
            {
                var generations = group.Select(draw => draw.OwnerGeneration).Distinct().ToArray();
                foreach (var held in drawsByComponent.Values.Where(draw => draw.Owner == group.Key
                    && (generations.Length != 1 || draw.OwnerGeneration != generations[0])).ToArray())
                    drawsByComponent.Remove(held.Component);
                if (generations.Length == 1)
                    foreach (var draw in group.Where(draw => DrawMatchesOwner(draw, now))) drawsByComponent[draw.Component] = draw;
            }
        }
        foreach (uint component in drawsByComponent.Where(pair => pair.Value.Time > now || now - pair.Value.Time > DrawHoldMilliseconds
            || !DrawMatchesOwner(pair.Value, now))
            .Select(pair => pair.Key).ToArray())
        { drawsByComponent.Remove(component); changed = true; }
        if (!changed) return;
        drawRevision++;
        drawsByOwner = drawsByComponent.Values.GroupBy(draw => draw.Owner).ToDictionary(group => group.Key, group => group.ToArray());
        // Authored owners animate their authored meshes, so only the others need drawn models.
        dynamic.SetDrawn(drawsByOwner.Where(pair => RowOf(pair.Key) is not { Dynamic: false })
            .ToDictionary(pair => pair.Key, pair => new DrawnOwner(pair.Value[0].Anchor,
                pair.Value.Select(draw => draw.ModelTag).Distinct().ToArray(), pair.Value[0].OwnerGeneration)));
    }

    /** Incomplete rows cannot reassign a captured lifetime to a different object in the same slot. */
    private bool DrawMatchesOwner(LiveDraw draw, ulong now)
    {
        bool player = snapshot?.PlayerHandle == draw.Owner;
        if (player && (connection?.Draws is not { Player: { } observed } frame
            || !LivePoseProtocol.Fresh(frame.Time, now) || observed.Handle != draw.Owner || observed.Generation != draw.OwnerGeneration)) return false;
        if (connection?.Placements is not { Available: true } placements || !LiveWorldProtocol.Fresh(placements.Time, now)) return player;
        if (ambiguousSlots.Contains(draw.Owner & LiveDynamicBinding.HandleIndexMask)) return false;
        if (byIndex.TryGetValue(draw.Owner & LiveDynamicBinding.HandleIndexMask, out var current))
            return current.Handle == draw.Owner && current.Generation == draw.OwnerGeneration;
        return player;
    }

    private IEnumerable<uint> PoseOwners()
    {
        var owners = dynamic.ModelOwners;
        if (transformsApplied && binding is not null) owners = owners.Concat(binding.ShownHandles);
        if (snapshot?.PlayerHandle is { } player) owners = owners.Append(player);
        if (inspected is { } selected) owners = owners.Append(selected.Handle);
        return owners;
    }

    private void ResetDraws(bool discardCurrent = false)
    {
        mergedDraws = discardCurrent ? connection?.Draws : null; drawsByComponent.Clear(); drawsByOwner = []; drawRevision++;
        dynamic.SetDrawn(new Dictionary<uint, DrawnOwner>());
    }

    /** Palettes change only with new draws, a new instance set, the placement toggle or a new renderer. */
    private void ApplyAnimation(StudioWorkspace workspace, AssetViewport viewport)
    {
        var native = viewport.NativeRenderer;
        uint player = snapshot?.PlayerHandle ?? uint.MaxValue;
        var key = (drawRevision, dynamic.InstanceRevision, transformsApplied, native, player);
        if (paletteKey != key)
        {
            paletteKey = key; paletteRevision++;
            animated.Clear(); playerMeshes.Clear(); unanimated.Clear();
            var palettes = new Dictionary<MeshInstance, NativeLivePalette>(ReferenceEqualityComparer.Instance);
            var notes = new Dictionary<string, int>();
            playerOwner = drawsByOwner.Keys.FirstOrDefault(owner => LiveDynamicBinding.SameObject(owner, player), uint.MaxValue);
            if (native is not null)
                foreach (var (owner, draws) in drawsByOwner)
                    foreach (var (mesh, entity, drawnModel) in TargetMeshes(owner, draws, viewport))
                    {
                        if (owner == playerOwner) playerMeshes.Add(mesh);
                        var draw = Array.Find(draws, value => value.ModelTag == mesh.Mesh.TagHash);
                        if (palettes.ContainsKey(mesh)) continue;
                        if (draw is null) { if (drawnModel) unanimated.Add(mesh); continue; }
                        if (Palette(draw, mesh, native, out string reason) is { } palette)
                        { palettes.Add(mesh, palette); animated.Add(mesh, (draw, entity)); continue; }
                        if (drawnModel) unanimated.Add(mesh);
                        notes[reason] = notes.GetValueOrDefault(reason) + 1;
                    }
            try { native?.SetLivePalettes(palettes); }
            catch (NotSupportedException error)
            { native!.ClearLivePalettes(); animated.Clear(); notes[error.Message] = palettes.Count; }
            animationNotes = notes;
        }
        PollSelectedPose(workspace, viewport);
    }

    /** Meshes that show one owner, and whether each belongs to a drawn-model instance. */
    private IEnumerable<(MeshInstance Mesh, uint Entity, bool Drawn)> TargetMeshes(uint owner, LiveDraw[] draws, AssetViewport viewport)
    {
        if (RowOf(owner) is { Dynamic: false } placed)
        {
            if (transformsApplied && binding is not null)
                foreach (var mesh in binding.MeshesFor(placed.Handle))
                    yield return (mesh, placed.ClassTag is > 0 and < uint.MaxValue ? placed.ClassTag : mesh.ObjectClassTag, false);
            yield break;
        }
        var actor = dynamic.Actor(owner);
        if (actor is not null && actor.Generation != draws[0].OwnerGeneration) yield break;
        uint entity = actor?.ClassTag ?? 0;
        if (actor is not null && viewport.TryGetLiveInstance(LiveDynamicBinding.Key(actor)) is { } instance)
            foreach (var mesh in instance.Meshes) yield return (mesh, entity, false);
        foreach (uint model in draws.Select(draw => draw.ModelTag).Distinct())
            if (viewport.TryGetLiveInstance(LiveDynamicBinding.ModelKey(actor, owner, model, draws[0].OwnerGeneration)) is { } drawn)
                foreach (var mesh in drawn.Meshes) yield return (mesh, entity, true);
    }

    private LivePlacement? RowOf(uint handle) => byHandle.GetValueOrDefault(handle);

    /** Captured world rows replace the placement; a mesh must match the drawn model's constants. */
    private static NativeLivePalette? Palette(LiveDraw draw, MeshInstance mesh, NativeSceneRenderer native, out string reason)
    {
        reason = "";
        var kind = draw.Kind switch
        {
            LivePaletteKind.DualQuaternion => NativeEntityPalette.DualQuaternion,
            LivePaletteKind.Matrix3x4 => NativeEntityPalette.Matrix,
            _ => NativeEntityPalette.None
        };
        if (kind == NativeEntityPalette.None) { reason = "No shader for 4-row palettes"; return null; }
        if (!native.CanApplyLivePalette(mesh, kind)) { reason = "No matching skinning shader"; return null; }
        if (mesh.Mesh.Native is not { Kind: NativeMeshKind.Entity } source
            || LivePoseModelBinding.ModelRows(source.SourceHeader) is not { } model
            || model.Position != draw.FixedRows[0] || model.Uv != draw.FixedRows[1])
        { reason = "Model constants differ from loaded geometry"; return null; }
        var rows = new Vector4[8 + draw.Palette.Count];
        for (int i = 0; i < 3; i++) rows[5 + i] = draw.FixedRows[i];
        for (int i = 0; i < draw.Palette.Count; i++) rows[8 + i] = draw.Palette[i];
        return new(kind, Array.AsReadOnly(rows), draw.Anchor);
    }

    /** The selected parts get posed pick geometry, so the outline and picking follow the pose. */
    private void PollSelectedPose(StudioWorkspace workspace, AssetViewport viewport)
    {
        SyncPoseSource(workspace);
        FinishPosePreparation();
        if (!ReferenceEquals(poseViewport, viewport)) { ClearPoseGeometry(); poseViewport = viewport; }
        IReadOnlyList<MeshInstance> parts = viewport.SelectedObject is MeshInstance ? viewport.SelectedMeshParts : [];
        var key = (paletteRevision, viewport.SelectedObject, Parts(parts), poseBindings.Count + poseErrors.Count);
        if (poseKey == key) return;
        poseKey = key;
        var targets = parts.Where(animated.ContainsKey).ToArray();
        if (targets.Length == 0 || viewport.NativeRenderer is not { } native)
        { ClearPoseGeometry(); poseStatus = parts.Count == 0 ? "" : "Not animated"; return; }
        var geometry = new Dictionary<MeshInstance, RenderVertex[]>(ReferenceEqualityComparer.Instance);
        var waiting = false; string failure = "";
        foreach (var group in targets.GroupBy(part => (animated[part].Entity, part.Mesh.TagHash)))
        {
            if (group.Key.Entity is 0 or uint.MaxValue) { failure = "No rig; picking uses the rest pose"; continue; }
            if (poseErrors.TryGetValue(group.Key, out var error)) { failure = error; continue; }
            if (!poseBindings.TryGetValue(group.Key, out var prepared)) { StartPosePreparation(workspace, group.Key); waiting = true; continue; }
            try { foreach (var (part, vertices) in prepared.Build(animated[group.First()].Draw, group.ToArray(), native).Geometry) geometry[part] = vertices; }
            catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or ArgumentException)
            { poseErrors[group.Key] = exception.Message; failure = exception.Message; }
        }
        try { viewport.SetLivePoseGeometry(geometry); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { ClearPoseGeometry(); failure = exception.Message; }
        poseStatus = waiting ? "Preparing posed picking" : failure.Length > 0 ? failure
            : $"Posed picking on {geometry.Count} of {parts.Count} parts";
    }

    private static int Parts(IReadOnlyList<MeshInstance> parts)
    {
        var hash = new HashCode();
        foreach (var part in parts) hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(part));
        return hash.ToHashCode();
    }

    /** One preparation runs at a time; a result is kept for the installation it was read from. */
    private void StartPosePreparation(StudioWorkspace workspace, (uint Entity, uint Model) key)
    {
        if (poseWork is not null || workspace.IsBusy || workspace.Catalog is not { } catalog) return;
        SyncPoseSource(workspace);
        poseWorkSource = poseSource;
        poseWorkKey = key; poseCancellation = new(); var token = poseCancellation.Token;
        poseWork = Task.Run(() =>
        {
            try { return (LivePoseModelBinding.Prepare(catalog, key.Entity, key.Model, token), ""); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { return ((LivePoseModelBinding?)null, error is OperationCanceledException ? "" : error.Message); }
        });
    }

    private void FinishPosePreparation()
    {
        if (poseWork is not { IsCompleted: true }) return;
        var result = poseWork.GetAwaiter().GetResult(); poseWork = null;
        bool current = poseCancellation?.IsCancellationRequested == false && poseWorkSource == poseSource
            && poseSource is { } source && source.Catalog.ReadGeneration == source.Reads
            && source.Catalog.OverlayRevision == source.Overlay;
        poseWorkSource = null;
        poseCancellation?.Dispose(); poseCancellation = null;
        if (!current) return;
        if (result.Binding is { } prepared) poseBindings[poseWorkKey] = prepared;
        else if (result.Error.Length > 0) poseErrors[poseWorkKey] = result.Error;
        poseKey = null;
    }

    /** Cached weights and errors belong to one catalog and both its original and overlay bytes. */
    private void SyncPoseSource(StudioWorkspace workspace)
    {
        var next = workspace.Catalog is { } catalog
            ? (catalog, catalog.ReadGeneration, catalog.OverlayRevision) : ((PackageCatalog, long, long)?)null;
        if (poseSource == next) return;
        poseCancellation?.Cancel();
        poseSource = next;
        poseBindings.Clear(); poseErrors.Clear(); poseKey = null; poseStatus = "";
        ClearPoseGeometry();
    }

    private void ClearPoseGeometry()
    {
        if (poseViewport?.LivePoseGeometryCount > 0) poseViewport.ClearLivePoseGeometry();
    }

    /** Drops every palette and posed mesh this page set; the next poll rebuilds them. */
    private void StopAnimation()
    {
        poseCancellation?.Cancel();
        ClearPoseGeometry();
        filteredViewport?.NativeRenderer?.ClearLivePalettes();
        animated.Clear(); playerMeshes.Clear(); unanimated.Clear(); paletteKey = null; poseKey = null; poseStatus = "";
        animationNotes = new Dictionary<string, int>();
        mergedDraws = null; drawsByComponent.Clear(); drawsByOwner = []; drawRevision++;
    }

    /** The Animation notes: parts following, capture losses, the player model and the selection's posed picking. */
    private void DrawAnimation()
    {
        ImGui.SeparatorText("Animation");
        if (!syncAnimation) { ImGui.TextDisabled("Off"); StudioUi.Tip("Turn on Sync > Animation."); return; }
        if (connection?.PoseDiagnostic is { Length: > 0 } diagnostic) { ImGui.TextColored(new Vector4(1, .6f, .35f, 1), diagnostic); return; }
        var rows = new List<(string Name, string Value, string Tip)>
        {
            ("Parts following", $"{animated.Count:N0}", "Parts that follow the game's pose"),
            ("Part visibility", "initial state", "Game part masks are not captured."),
            ("Draws", $"{drawsByComponent.Count:N0} from {drawsByOwner.Count:N0} objects", "")
        };
        if (mergedDraws is { } frame && frame.Dropped + frame.Rejected > 0)
            rows.Add(("Capture losses", $"{frame.Dropped} over capacity, {frame.Rejected} malformed", "Draws lost in the last capture"));
        rows.Add(snapshot?.PlayerHandle is not { } player ? ("Player model", "no player handle", "")
            : connection?.Draws?.Player is not { } proof || proof.Handle != player ? ("Player model", "lifetime unavailable", "")
            : playerOwner == uint.MaxValue ? ("Player model", "nothing drawn", $"The game draws nothing for {player:X8}")
            : ("Player model", $"{playerMeshes.Count - playerMeshes.Count(unanimated.Contains)} of {playerMeshes.Count} parts", "Player parts animated"));
        if (poseStatus.Length > 0) rows.Add(("Selection", poseStatus, ""));
        StudioUi.Facts("live-animation", rows);
        if (animationNotes.Count > 0 && ImGui.TreeNode($"Parts not animated ({animationNotes.Values.Sum()})###animation-notes"))
        {
            StudioUi.Facts("live-animation-notes", animationNotes.Select(note => (note.Key, $"{note.Value:N0}", note.Key)));
            ImGui.TreePop();
        }
    }
}
