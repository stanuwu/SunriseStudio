using System.Numerics;
using ImGuiNET;
using SunriseStudio.Live;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** The live connection only reads diagnostic frames; updates to the game go through the command link. */
public sealed partial class LivePage : IDisposable
{
    private LiveConnection? connection;
    private IReadOnlyList<LiveProcess> processes = [];
    private LiveSnapshot? snapshot;
    private string status = "", root = "";
    private string syncStatus = "";
    private int selected, follow;
    private AssetViewport? followedViewport;
    private AssetViewport? filteredViewport;
    private WorldDocument? scene;
    private LiveSceneBinding? binding;
    private readonly LiveDynamicBinding dynamic = new();
    private LiveWorld? world;
    private (uint Root, uint Definition, uint Scenario, bool Present)? observedWorld;
    private readonly HashSet<uint> loadedBubbles = [];
    private WorldDocument? bubbleScene;
    private uint[] sceneBubbles = [];
    private HashSet<uint>? liveBubbles;
    private bool syncWorld = true, syncBubbles = true, syncPlacements = true, transformsApplied;
    private bool syncDynamic = true, dynamicApplied, showPanel = true;
    /** The world, mission and loaded bubble set of the last load request. */
    private (uint Map, uint Scenario, string Bubbles)? requestedWorld;
    /** The game's loaded bubble set and when it last changed; a load waits for it to settle. */
    private (string Bubbles, long Since)? settlingBubbles;
    // Bubbles load and unload in bursts while the player moves; one reload per settled set.
    private const long BubbleSettleMilliseconds = 2000;
    private string findObject = "";
    private string appliedObjectFilter = "";
    private LivePlacements? displayedPlacements, indexedPlacements;
    private (LivePlacement Row, string Name, float Distance)[] placementRows = [];
    private Dictionary<uint, LivePlacement> byHandle = [], byIndex = [];
    private readonly HashSet<uint> ambiguousSlots = [];
    private int markerRows;
    private (uint Handle, uint Generation)? inspected;
    private WorldCatalog? indexedWorlds;
    private Dictionary<uint, MapEntry?> mapRoots = [];
    private Dictionary<uint, MapEntry> mapsByTag = [];
    private static readonly string[] FollowModes = ["Off", "First person", "Player cursor"];
    public LivePlacements? MatchedPlacements => MatchesWorld() ? connection?.Placements : null;
    public IReadOnlyCollection<LivePlacement> RuntimePlacements => dynamic.Owners;
    public long RuntimeRevision => dynamic.Revision;
    /** True while a game is attached; Studio then shares the GPU with it. */
    public bool IsAttached => connection is not null;
    public Vector3? Player => snapshot?.Player;

    /** The live row paired with an authored mesh or marker, if the game has spawned it. */
    public LivePlacement? Observed(object source) => MatchesWorld() ? binding?.RowFor(source) : null;
    public LivePlacement? RowByHandle(uint handle) => byHandle.GetValueOrDefault(handle);
    /** The game's answer for the selected object; Pending until it arrives. */
    public LiveObjectSnapshot? InspectedObject => inspected is null ? null : connection?.ObjectSnapshot;
    public string ObjectBridgeDiagnostic => connection?.ObjectBridgeDiagnostic ?? "";

    /** Asks the game once for the selected object's full record; only a new selection asks again. */
    public void Inspect(LivePlacement? row)
    {
        var target = row is { Handle: not uint.MaxValue, Generation: not uint.MaxValue }
            ? (row.Handle, row.Generation) : ((uint, uint)?)null;
        if (connection is null || target == inspected) return;
        inspected = target;
        connection.RequestObject(target);
    }

    /** The user asks again for the selected object. */
    public void RefreshInspected() { if (inspected is { } target) connection?.RequestObject(target); }

    /** Selects the scene object shown for a live row; false when Studio shows nothing for it. */
    public bool SelectRow(LivePlacement row)
    {
        if (filteredViewport is not { } viewport) return false;
        // A placed row drawn as a marker has its authored copy hidden; the marker stands for it.
        if (row.ShowsModel && binding?.Find(row) is { } source) { viewport.SetSelection(source.Source); return true; }
        string key = LiveDynamicBinding.Key(row);
        if (viewport.LiveInstances.Where(view => view.Key == key || view.Key.StartsWith(key + "/", StringComparison.Ordinal))
            .SelectMany(view => view.Meshes).FirstOrDefault() is { } mesh)
        { viewport.SetSelection(mesh); return true; }
        return false;
    }

    public string DisplayName(StudioWorkspace workspace, LivePlacement row) => binding?.Find(row)?.Name
        ?? (row.ClassTag is not (0 or uint.MaxValue) ? workspace.DisplayName(row.ClassTag) : $"{workspace.DisplayName(row.ObjectListTag)} / {row.EntryIndex}");

    /** Once a frame: reads the game, syncs the world and screen, and applies placements, spawned rows and poses. */
    public void Poll(StudioWorkspace workspace, AssetViewport viewport)
    {
        PollUpdates(workspace);
        if (connection is null) return;
        try
        {
            if (workspace.Catalog?.GameRoot != root || connection.HasExited)
            { Disconnect(); status = "Game closed"; return; }
            snapshot = connection.Poll();
            var nextWorld = connection.World;
            var identity = nextWorld is null ? ((uint, uint, uint, bool)?)null
                : (nextWorld.MapRootTag, nextWorld.MapDefinitionTag, nextWorld.ScenarioTag, nextWorld.Present);
            bool identityChanged = observedWorld != identity;
            observedWorld = identity;
            if (!ReferenceEquals(nextWorld, world) || !ReferenceEquals(scene, workspace.World))
            {
                world = nextWorld; scene = workspace.World;
                if (identityChanged || !ReferenceEquals(binding?.World, scene))
                {
                    ResetDraws(discardCurrent: true);
                    if (transformsApplied) viewport.ClearLiveTransforms();
                    if (dynamicApplied) dynamic.Clear(viewport);
                    transformsApplied = false;
                    dynamicApplied = false;
                    binding = MatchesWorld() ? new(scene!) : null;
                    if (identityChanged) requestedWorld = null;
                    displayedPlacements = null;
                }
                loadedBubbles.Clear();
                if (world is { Present: true })
                    foreach (var slice in world.Slices) loadedBubbles.Add(scene?.CanonicalBubble(slice.BubbleHash) ?? slice.BubbleHash);
            }
            SyncWorld(workspace);
            SyncCui(workspace);
            IndexRows(connection.Placements);
            MergeDraws();
            if (syncPlacements && binding is not null && MatchesWorld() && snapshot?.InWorld == true
                && connection.Placements is { Available: true } placements)
            {
                binding.Apply(placements, viewport, (ulong)Environment.TickCount64);
                transformsApplied = true;
            }
            else if (transformsApplied)
            { viewport.ClearLiveTransforms(); binding?.InvalidatePreview(); transformsApplied = false; }
            if (syncDynamic && MatchesWorld() && snapshot?.InWorld == true
                && connection.Placements is { Available: true } spawned)
            {
                dynamic.Poll(workspace, viewport, spawned, (ulong)Environment.TickCount64);
                dynamicApplied = true;
            }
            else if (dynamicApplied) { dynamic.Clear(viewport); dynamicApplied = false; }
            filteredViewport = viewport;
            ApplyAnimation(workspace, viewport);
            bool live = MatchesWorld() && snapshot?.InWorld == true;
            viewport.Visibility.LiveBubbles = syncBubbles && live ? LiveBubbleSet() : null;
            viewport.Visibility.LiveFilter = live && (syncPlacements && transformsApplied || unanimated.Count > 0
                || follow == 1 && playerMeshes.Count > 0) ? LiveVisible : null;
            status = snapshot is null ? "Waiting for the game" : snapshot.InWorld ? "Connected" : "Loading";
            if (syncStatus.Length > 0) status += " / " + syncStatus;
            if (cuiStatus.Length > 0) status += " / " + cuiStatus;
            if (snapshot is { Camera: { } camera } && follow == 1 && MatchesWorld())
            {
                viewport.Camera.SetExternalPose(camera.Position, camera.Forward, camera.Up, camera.HorizontalFieldOfView);
                followedViewport = viewport;
            }
            else viewport.Camera.ClearExternalPose();
            bool followPlayer = follow == 2 && snapshot?.Player is not null && MatchesWorld();
            // The previous orbit distance can be one meter after first person or hundreds after framing a map.
            if (followPlayer && !viewport.Camera.PivotFollowed) SnapToPlayer(viewport);
            viewport.Camera.PivotFollowed = followPlayer;
            if (followPlayer) { viewport.Camera.MovePivot(snapshot!.Player!.Value); viewport.ShowCursor = true; followedViewport = viewport; }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or NotSupportedException
            or ArgumentException or InvalidDataException or System.ComponentModel.Win32Exception)
        { Disconnect(); status = error.Message; }
    }

    /** Rows by full handle and by slot; a new snapshot rebuilds them once. */
    private void IndexRows(LivePlacements? placements)
    {
        if (ReferenceEquals(placements, indexedPlacements)) return;
        indexedPlacements = placements;
        byHandle = new(); byIndex = new(); ambiguousSlots.Clear(); markerRows = 0;
        foreach (var group in (placements?.Rows ?? []).GroupBy(row => row.Handle & LiveDynamicBinding.HandleIndexMask))
        {
            var rows = group.ToArray();
            if (rows.Length != 1) { ambiguousSlots.Add(group.Key); continue; }
            var row = rows[0];
            byHandle[row.Handle] = row; byIndex[row.Handle & LiveDynamicBinding.HandleIndexMask] = row;
            if (!row.ShowsModel) markerRows++;
        }
    }

    /** Refresh is explicit so process enumeration never runs on each UI frame. */
    public void Draw(StudioWorkspace workspace, AssetViewport viewport, Action? drawScene = null)
    {
        if (workspace.Catalog is not { } catalog) { ImGui.TextDisabled("Open a Sunrise installation"); return; }
        if (root != catalog.GameRoot)
        { Disconnect(); root = catalog.GameRoot; Refresh(); }
        DrawToolbar(workspace, viewport);
        DrawUpdates(workspace);
        if (connection is null)
        {
            ImGui.TextDisabled("Attach needs studio_attach");
            StudioUi.Tip("Set core.studio_attach to true in bin\\x64\\Sunrise\\settings.json.");
            return;
        }
        if (connection.WorldBridgeDiagnostic is { Length: > 0 } bridgeDiagnostic)
            ImGui.TextColored(new Vector4(1, .6f, .35f, 1), bridgeDiagnostic);
        // The Screens tab sets this again while it is drawn.
        bool cuiInUse = CuiInUse;
        screensShown = false;
        if (cuiInUse && connection.CuiBridgeDiagnostic is { Length: > 0 } cuiDiagnostic)
            ImGui.TextColored(new Vector4(1, .6f, .35f, 1), cuiDiagnostic);
        DrawSummary(workspace);
        var size = ImGui.GetContentRegionAvail();
        float panel = showPanel ? Math.Clamp(size.X * .3f, 280, 460) : 0;
        ImGui.BeginChild("live-scene", new(size.X - panel - (showPanel ? ImGui.GetStyle().ItemSpacing.X : 0), size.Y));
        if (workspace.World is null) ImGui.TextDisabled("Open a map or turn on Sync > Map");
        else if (drawScene is not null) drawScene();
        else viewport.DrawImGui(ImGui.GetContentRegionAvail());
        ImGui.EndChild();
        if (!showPanel) return;
        ImGui.SameLine();
        ImGui.BeginChild("live-panel", new(0, size.Y), ImGuiChildFlags.Borders);
        if (ImGui.BeginTabBar("live-panel-tabs"))
        {
            if (ImGui.BeginTabItem("Objects")) { DrawPlacements(workspace); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("World")) { DrawWorld(workspace); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Screens")) { DrawScreens(workspace); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Notes")) { DrawNotes(workspace, viewport); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
        ImGui.EndChild();
    }

    /** Detached: the process picker and Attach. Attached: Detach, status, camera follow, Sync and report. */
    private void DrawToolbar(StudioWorkspace workspace, AssetViewport viewport)
    {
        if (connection is null)
        {
            ImGui.SetNextItemWidth(200);
            if (ImGui.BeginCombo("##liveProcess", processes.Count == 0 ? "No running game" : $"Destiny 2 ({processes[selected].Id})"))
            {
                for (int i = 0; i < processes.Count; i++)
                    if (ImGui.Selectable($"Destiny 2 ({processes[i].Id})##{processes[i].Id}", selected == i)) selected = i;
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            if (ImGui.Button("Refresh")) Refresh();
            ImGui.SameLine(); ImGui.BeginDisabled(processes.Count == 0);
            if (ImGui.Button("Attach"))
                try { connection = LiveConnection.Attach(processes[selected]); status = "Waiting for the game"; }
                catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                { status = error.Message; }
            ImGui.EndDisabled();
            if (status.Length > 0) { ImGui.SameLine(); ImGui.TextDisabled(status); }
            return;
        }
        if (ImGui.Button("Detach")) { Disconnect(); return; }
        ImGui.SameLine();
        bool connected = snapshot?.InWorld == true && syncStatus.Length == 0 && cuiStatus.Length == 0;
        ImGui.TextColored(connected ? new Vector4(.45f, .85f, .5f, 1) : new Vector4(1, .75f, .35f, 1), status);
        ImGui.SameLine(); ImGui.TextDisabled("|"); ImGui.SameLine();
        ImGui.AlignTextToFramePadding(); ImGui.TextUnformatted("Camera"); ImGui.SameLine();
        ImGui.SetNextItemWidth(140);
        if (ImGui.Combo("##follow", ref follow, FollowModes, FollowModes.Length)) ReleaseCamera();
        StudioUi.Tip("Game camera / orbit player");
        ImGui.SameLine(); ImGui.BeginDisabled(snapshot?.Player is null || follow == 1);
        if (ImGuiIcons.Button("live-snap", StudioIcon.Player, "Snap to player")) SnapToPlayer(viewport);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGuiIcons.Button("live-sync-button", StudioIcon.Loop, "Sync")) ImGui.OpenPopup("live-sync");
        if (ImGui.BeginPopup("live-sync"))
        {
            ImGui.Checkbox("Map", ref syncWorld);
            StudioUi.Tip("Open the game's map and loaded bubbles");
            ImGui.Checkbox("Loaded bubbles only", ref syncBubbles);
            ImGui.Checkbox("Placements", ref syncPlacements);
            StudioUi.Tip("Match game positions; hide unspawned");
            ImGui.Checkbox("Spawned entities", ref syncDynamic);
            if (ImGui.Checkbox("Animation", ref syncAnimation) && !syncAnimation) StopAnimation();
            StudioUi.Tip("Copy game poses");
            if (ImGui.Checkbox("CUI screen", ref syncCui)) { requestedCui = shownCui = null; cuiSettle = null; }
            StudioUi.Tip("Follow the game's top screen. " + StudioUi.MayBeInaccurate);
            ImGui.Separator();
            if (ImGui.Selectable("Sync map now")) requestedWorld = null;
            ImGui.BeginDisabled(!syncCui);
            if (ImGui.Selectable("Sync screen now")) { requestedCui = shownCui = null; cuiSettle = null; }
            ImGui.EndDisabled();
            ImGui.EndPopup();
        }
        ImGui.SameLine();
        ImGuiIcons.Toggle("live-panel", StudioIcon.Tree, "Objects, world, screens and notes panel", ref showPanel);
        ImGui.SameLine();
        if (ImGuiIcons.Button("live-report", StudioIcon.Export,
            reportPath.Length == 0 ? "Save live state to a file" : "Save live state to a file\n" + reportPath))
            try { SaveReport(workspace, viewport); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
            { reportPath = ""; status = error.Message; }
    }

    /** One line: where the game is, where the player stands, and how many objects are live. */
    private void DrawSummary(StudioWorkspace workspace)
    {
        if (world is not { Present: true } liveWorld) { ImGui.TextDisabled(world is null ? "Waiting for world data" : "No loaded activity"); return; }
        ImGui.TextUnformatted(liveWorld.Activity.Replace('_', ' '));
        ImGui.SameLine(); ImGui.TextDisabled($"| {liveWorld.Slices.Count} bubbles");
        if (ImGui.IsItemHovered())
            StudioUi.Tip(string.Join("\n", liveWorld.Slices.OrderByDescending(slice => slice.Residency)
                .Select(slice => workspace.ResolveHash(slice.BubbleHash) ?? $"{slice.BubbleHash:X8}")));
        if (snapshot?.Player is { } player) { ImGui.SameLine(); ImGui.TextDisabled($"| Player {player.X:F1}, {player.Y:F1}, {player.Z:F1}"); }
        if (connection?.Placements is { } placements)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"| {placements.Rows.Count:N0} live objects");
            StudioUi.Tip($"Markers: {markerRows:N0}" + (binding is null ? "" : $"\nHidden (not spawned): {binding.Inactive:N0}"));
        }
    }

    /** Places the editor camera five meters behind and above the player, looking along the current view. */
    private void SnapToPlayer(AssetViewport viewport)
    {
        if (snapshot?.Player is not { } player) return;
        var forward = (snapshot.Camera?.Forward ?? viewport.Camera.Forward) with { Z = 0 };
        forward = forward.LengthSquared() > 1e-4f ? Vector3.Normalize(forward) : Vector3.UnitX;
        viewport.Camera.LookFrom(player - forward * 5 + Vector3.UnitZ * 1.5f, player);
    }

    private void Refresh() { processes = LiveConnection.FindProcesses(root); selected = 0; }

    private void ReleaseCamera()
    {
        if (followedViewport is null) return;
        followedViewport.Camera.ClearExternalPose(); followedViewport.Camera.PivotFollowed = false; followedViewport = null;
    }

    private void Disconnect()
    {
        StopAnimation();
        poseViewport = null; poseBindings.Clear(); poseErrors.Clear();
        ReleaseCamera();
        if (filteredViewport is not null)
        {
            filteredViewport.Visibility.LiveFilter = null; filteredViewport.Visibility.LiveBubbles = null;
            filteredViewport.ClearLiveTransforms(); dynamic.Clear(filteredViewport);
        }
        filteredViewport = null; requestedWorld = null; settlingBubbles = null; world = null; scene = null;
        binding = null; transformsApplied = false;
        dynamicApplied = false; inspected = null;
        observedWorld = null; byHandle = []; byIndex = []; indexedPlacements = null;
        connection?.Dispose(); connection = null; snapshot = null; status = "";
        syncStatus = "";
        ResetCui();
    }

    /**
     * Exact package identities select the map and scenario; labels never choose a world. Only the
     * bubbles the game has loaded are opened, once that set has held for a moment.
     */
    private void SyncWorld(StudioWorkspace workspace)
    {
        syncStatus = "";
        if (world is not { Present: true } loaded) return;
        if (!syncWorld) { if (!MatchesWorld()) syncStatus = "Different map or scenario"; return; }
        if (workspace.IsBusy) { if (!MatchesWorld()) syncStatus = "Loading current map"; return; }
        if (workspace.WorldCatalog is null) { syncStatus = "Map catalog unavailable"; return; }
        if (!ReferenceEquals(indexedWorlds, workspace.WorldCatalog))
        {
            indexedWorlds = workspace.WorldCatalog;
            mapRoots = indexedWorlds.Maps.Where(map => map.DefinitionTag != 0).GroupBy(map => map.DefinitionTag)
                .ToDictionary(group => group.Key, group => group.Count() == 1 ? group.First() : null);
            mapsByTag = indexedWorlds.Maps.ToDictionary(map => map.Tag);
        }
        var map = mapsByTag.GetValueOrDefault(loaded.MapRootTag);
        if (map?.DefinitionTag != loaded.MapDefinitionTag) map = mapRoots.GetValueOrDefault(loaded.MapDefinitionTag);
        if (map is null) { syncStatus = "Map unavailable"; return; }
        uint scenario = workspace.Catalog?.TryGetEntry(loaded.ScenarioTag, out var entry) == true
            && entry.Reference == SScenario.Class ? loaded.ScenarioTag : 0;
        if (scenario == 0 && loaded.ScenarioTag is not (0 or uint.MaxValue)) syncStatus = "Scenario unavailable";
        // A map whose bubble list could not be read opens whole.
        HashSet<uint>? bubbles = map.BubbleHashes.Count == 0 ? null
            : loaded.Slices.Select(slice => slice.BubbleHash).Where(map.BubbleHashes.Contains).ToHashSet();
        if (bubbles is { Count: 0 }) { syncStatus = "No loaded bubble belongs to the map"; return; }
        string bubbleKey = bubbles is null ? "" : string.Join(",", bubbles.Order());
        long now = Environment.TickCount64;
        if (settlingBubbles?.Bubbles != bubbleKey) settlingBubbles = (bubbleKey, now);
        var current = workspace.World;
        bool sameWorld = current is not null && current.Map.Tag == map.Tag && (current.MissionTag ?? 0) == scenario;
        if (sameWorld && (bubbles is null ? current!.Options.BubbleNameHashes is null
            : current!.Options.BubbleNameHashes?.SetEquals(bubbles) == true))
        { requestedWorld = null; return; }
        if (now - settlingBubbles!.Value.Since < BubbleSettleMilliseconds) { if (!sameWorld) syncStatus = "Waiting for bubbles to load"; return; }
        uint mapTag = map.Tag;
        var target = (mapTag, scenario, bubbleKey);
        if (requestedWorld == target) { syncStatus = "Map sync failed; retry Sync"; return; }
        requestedWorld = target;
        var options = (sameWorld ? current!.Options : new WorldLoadOptions()) with { BubbleNameHashes = bubbles };
        // A new world replaces the open document, so CUI Sync opens its screen again afterwards.
        if (!sameWorld) { requestedCui = shownCui = null; cuiSettle = null; }
        workspace.Run(() =>
        {
            if (sameWorld) workspace.ReloadWorld(options);
            else if (scenario == 0) workspace.OpenMap(mapTag, options);
            else workspace.OpenMission(mapTag, scenario, options);
        });
    }

    /** Runs per drawn object each frame, so it holds only set lookups; bubbles go through the prepared filter. */
    private bool LiveVisible(object item) =>
        (!syncPlacements || !transformsApplied || binding?.IsVisible(item) != false)
        && !(item is MeshInstance part && (unanimated.Contains(part)
            // In first person the camera stands inside the player, whose model would fill the view.
            || follow == 1 && playerMeshes.Contains(part)));

    /** Scene bubble hashes whose canonical bubble the game has loaded; a new set only when it changes. */
    private IReadOnlySet<uint> LiveBubbleSet()
    {
        if (!ReferenceEquals(bubbleScene, scene))
        {
            bubbleScene = scene; liveBubbles = null;
            sceneBubbles = scene!.Scene.Instances.Select(mesh => mesh.BubbleHash)
                .Concat(scene.Scene.Markers.Select(marker => marker.BubbleHash)).Where(hash => hash != 0).Distinct().ToArray();
        }
        var next = sceneBubbles.Where(hash => loadedBubbles.Contains(scene!.CanonicalBubble(hash))).ToHashSet();
        if (liveBubbles is null || !next.SetEquals(liveBubbles)) liveBubbles = next;
        return liveBubbles;
    }

    private bool MatchesWorld() => scene is not null && world is { Available: true, Present: true }
        && scene.Map.DefinitionTag == world.MapDefinitionTag
        && (world.MapRootTag is 0 or uint.MaxValue || scene.Map.Tag == world.MapRootTag)
        && (scene.MissionTag ?? 0) == (world.ScenarioTag == uint.MaxValue ? 0 : world.ScenarioTag);

    /** Live objects, nearest to the player first. */
    private unsafe void DrawPlacements(StudioWorkspace workspace)
    {
        if (connection?.Placements is not { } placements) { ImGui.TextDisabled("No placement data yet"); return; }
        if (!placements.Available) { ImGui.TextDisabled("Placement registry unavailable"); return; }
        if (placements.Truncated) ImGui.TextDisabled($"Partial snapshot: {placements.Total:N0} objects, {placements.Overflow:N0} missed");
        ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##liveFind", "Find object", ref findObject, 128);
        if (!ReferenceEquals(displayedPlacements, placements) || appliedObjectFilter != findObject)
        {
            displayedPlacements = placements; appliedObjectFilter = findObject;
            var player = snapshot?.Player;
            placementRows = placements.Rows.Select(row => (Row: row, Name: DisplayName(workspace, row),
                    Distance: player is { } at && row.TransformAvailable ? Vector3.Distance(at, row.Position) : float.PositiveInfinity))
                .Where(row => findObject.Length == 0 || row.Name.Contains(findObject, StringComparison.OrdinalIgnoreCase))
                .OrderBy(row => row.Distance).ToArray();
        }
        if (!ImGui.BeginTable("live-placements", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY
            | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp, ImGui.GetContentRegionAvail())) return;
        ImGui.TableSetupColumn("Object", ImGuiTableColumnFlags.WidthStretch, 3);
        ImGui.TableSetupColumn("Distance", ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(placementRows.Length);
        while (clipper.Step())
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var (row, name, distance) = placementRows[i];
                ImGui.PushID($"{row.Handle}:{row.Generation}"); ImGui.TableNextRow(); ImGui.TableNextColumn();
                if (ImGui.Selectable(name, false, ImGuiSelectableFlags.AllowDoubleClick | ImGuiSelectableFlags.SpanAllColumns))
                {
                    var source = binding?.Find(row);
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                        workspace.Select(row.ClassTag != 0 ? row.ClassTag : source?.AssetTag is > 0 ? source.AssetTag : row.ObjectListTag);
                    else SelectRow(row);
                }
                if (ImGui.IsItemHovered())
                {
                    bool fresh = row.TransformAvailable && LiveWorldProtocol.Fresh(row.Time, (ulong)Environment.TickCount64);
                    ImGui.SetTooltip((fresh ? $"{row.Position.X:F2}, {row.Position.Y:F2}, {row.Position.Z:F2}\n" : "Position unavailable\n")
                        + $"Handle {row.Handle:X8} / Generation {row.Generation}\nFlags 0x{row.DatumFlags:X8}\n"
                        + (row.PlacedNotHidden == false ? "Not placed or hidden: drawn as a marker\n" : "")
                        + (row.AtOrigin ? "Exactly at the world origin\n" : "")
                        + "Double-click to open its asset");
                }
                ImGui.TableNextColumn(); ImGui.TextDisabled(float.IsFinite(distance) ? $"{distance:F0} m" : "-");
                ImGui.TableNextColumn();
                ImGui.TextDisabled((row.Dynamic ? "spawned" : "placed") + (row.PlacedNotHidden == false ? ", marker" : "")
                    + (row.AtOrigin ? ", at origin" : ""));
                ImGui.PopID();
            }
        clipper.End(); clipper.Destroy();
        ImGui.EndTable();
    }

    /** The loaded activity and bubbles, then the last frame's slice, player and camera. */
    private void DrawWorld(StudioWorkspace workspace)
    {
        if (world is { Available: true } liveWorld)
        {
            ImGui.TextUnformatted(liveWorld.Present ? liveWorld.Activity.Replace('_', ' ') : "No loaded activity");
            if (liveWorld.ScenarioTag is not (0 or uint.MaxValue)) ImGui.TextDisabled(workspace.DisplayName(liveWorld.ScenarioTag));
            ImGui.SeparatorText("Loaded bubbles");
            foreach (var slice in liveWorld.Slices)
                ImGui.TextUnformatted($"{workspace.ResolveHash(slice.BubbleHash) ?? $"Bubble {slice.BubbleHash:X8}"}  -  slice {slice.Index}{(slice.Residency == 4 ? ", current" : "")}");
        }
        if (snapshot is not { } frame) return;
        ImGui.SeparatorText("Frame");
        ImGui.TextUnformatted(frame.SliceAvailable ? $"Current slice: {frame.CurrentSlice?.ToString() ?? "none"}" : "Current slice: unavailable");
        if (frame.Player is { } player) ImGui.TextUnformatted($"Player {player.X:F2}, {player.Y:F2}, {player.Z:F2}");
        if (frame.Camera is { } camera)
        {
            ImGui.TextUnformatted($"Camera {camera.Position.X:F2}, {camera.Position.Y:F2}, {camera.Position.Z:F2}");
            ImGui.TextDisabled($"Field of view {camera.HorizontalFieldOfView * 180 / MathF.PI:F1} deg, aspect {camera.Aspect:F3}");
        }
        ImGui.TextDisabled($"Process {frame.ProcessId}, frame time {frame.FrameTime}");
    }

    /** Join counts for placed and spawned objects, classes with no preview, and the animation notes. */
    private void DrawNotes(StudioWorkspace workspace, AssetViewport viewport)
    {
        ImGui.SeparatorText("Objects");
        if (binding is null) ImGui.TextDisabled("Open map does not match the game");
        var rows = new List<(string Name, string Value, string Tip)>();
        if (binding is not null)
        {
            rows.Add(("Following", $"{binding.Matched:N0}", "Placed objects that follow the game"));
            rows.Add(("Hidden (not spawned)", $"{binding.Inactive:N0}", "Placed objects the game has not spawned"));
            rows.Add(("Markers", $"{binding.Dormant:N0}", "Placed objects shown as markers"));
            rows.Add(("Ambiguous", $"{binding.Ambiguous:N0}", "More than one authored match"));
            rows.Add(("Unmatched", $"{binding.Unmatched:N0}", "No authored match"));
        }
        if (connection?.Placements is { Excluded: > 0 } placements)
            rows.Add(("Left out by game", $"{placements.Excluded:N0}", "Destroyed or reused objects"));
        rows.Add(("Spawned shown", $"{viewport.LiveInstanceCount:N0} of {dynamic.ObservedCount:N0}", "Spawned entities shown of those observed"));
        StudioUi.Facts("live-object-notes", rows);
        if (dynamic.Status.Length > 0) ImGui.TextDisabled(dynamic.Status);
        if (dynamic.Errors.Count > 0 && ImGui.TreeNode($"No preview ({dynamic.Errors.Count})###entity-notes"))
        {
            foreach (var (tag, reason) in dynamic.Errors)
            {
                ImGui.TextUnformatted(workspace.DisplayName(tag));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(reason);
            }
            ImGui.TreePop();
        }
        DrawAnimation();
    }

    public void CancelPreparation() { dynamic.CancelPreparation(); poseCancellation?.Cancel(); }
    public void Dispose()
    {
        Disconnect(); dynamic.Dispose();
        if (poseWork is { } pending && poseCancellation is { } cancellation)
            _ = pending.ContinueWith(_ => cancellation.Dispose(), TaskScheduler.Default);
        else poseCancellation?.Dispose();
    }
}
