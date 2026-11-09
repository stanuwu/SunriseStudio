/**
 * File: Live player position handling for the editor; position sources and PlayerFix/PlayerTrail are in
 * Solforge.Mcp/PlayerTracking.cs.
 */
using System.Numerics;
using System.Text.Json.Nodes;
using Solforge.Mcp;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/**
 * Live player position on the open map. Sources, first that answers wins: the Live link (studio_attach), the newest
 * test-recorder snapshot (bin\x64\Sunrise\logs\recorder; cam when pos froze at spawn) and the local server's admin
 * /coop page (every joined player). All three report game world space, which is Solforge's map space. Runs while a map
 * is open or for two minutes after get_player_position; the viewport shows a marker, facing arrow and trail, and its
 * Follow player toggle keeps the camera on the player and opens the player's map when the recorder or server names it
 * (the Live link's own Sync > Map does that when attached).
 */
public sealed partial class StudioForm
{
    /** Breadcrumb trail of recent player positions. */
    private readonly PlayerTrail playerTrail = new();
    private PlayerFix? playerFix;
    private PlayerPlace playerPlace = PlayerPlace.Unknown;
    private uint? playerTrailMap;
    private volatile PlayerFix? playerRecorder, playerServer;
    private volatile IReadOnlyList<PlayerFix> playerServerRows = [];
    private volatile string playerRecorderNote = "not read yet", playerServerNote = "not read yet";
    private Task? playerRecorderRead, playerServerRead;
    private long playerNextRecorder, playerNextServer, playerWantedUntil;
    private bool playerFollowing;
    private string? playerSwitchedFor;
    /** HTTP client used for the local position query. */
    private static readonly HttpClient PlayerHttp = new() { Timeout = TimeSpan.FromSeconds(1.5) };
    /** How old a reading may be before it is ignored. */
    private static readonly TimeSpan PlayerStale = TimeSpan.FromSeconds(5);

    /** Matches: the fix belongs on the open map. Known: the fix names its map (else it is drawn on the open map). */
    private sealed record PlayerPlace(bool Matches, bool Known, uint? Map, WorldMissionEntry? Mission)
    {
        /** The place used when nothing is known. */
        public static readonly PlayerPlace Unknown = new(false, false, null, null);
    }

    private static string PlayerServerUrl =>
        (Environment.GetEnvironmentVariable("SOLFORGE_SERVER_ADMIN") is { Length: > 0 } url ? url : "http://127.0.0.1:8099").TrimEnd('/');

    /** Once a frame after the Live link polls. */
    private void TrackPlayer()
    {
        if (viewport is null) return;
        long now = Environment.TickCount64;
        if (workspace.World is null && now >= playerWantedUntil)
        {
            viewport.PlayerMarker = null; playerFix = null;
            StopFollowingPlayer();
            return;
        }
        var fix = LivePlayerFix();
        if (fix is null) { PollPlayerSources(now); fix = Fresh(playerRecorder) ?? Fresh(playerServer); }
        playerFix = fix;
        playerPlace = fix is null ? PlayerPlace.Unknown : PlacePlayer(fix);
        uint? openMap = workspace.World?.Map.Tag;
        if (openMap != playerTrailMap) { playerTrail.Clear(); playerTrailMap = openMap; }
        bool shown = fix is not null && openMap is not null && (playerPlace.Matches || !playerPlace.Known);
        if (shown) playerTrail.Add(fix!.Position);
        viewport.PlayerMarker = shown ? new PlayerMarker(fix!.Position, fix.Yaw, playerTrail.Points, fix.Source) : null;
        if (viewport.FollowPlayer && shown && !viewport.Camera.HasExternalPose)
        {
            viewport.Camera.MovePivot(fix!.Position + new Vector3(0, 0, 1));
            viewport.Camera.PivotFollowed = true;
            playerFollowing = true;
        }
        else StopFollowingPlayer();
        if (viewport.FollowPlayer && fix is not null && !playerPlace.Matches) SwitchToPlayerMap(fix);
    }

    /** Stops following the player in the camera. */
    private void StopFollowingPlayer()
    {
        if (!playerFollowing || viewport is null) return;
        playerFollowing = false;
        if (!livePage.IsAttached) viewport.Camera.PivotFollowed = false;
    }

    /** A fix only if it is recent enough, else null. */
    private static PlayerFix? Fresh(PlayerFix? fix) => fix is not null && DateTimeOffset.UtcNow - fix.Time < PlayerStale ? fix : null;

    /**
     * The newest player position from the Live link, else the recorder or local server (see the class comment), or
     * null.
     */
    private PlayerFix? LivePlayerFix()
    {
        if (!livePage.IsAttached || livePage.Player is not { } at) return null;
        var world = livePage.LoadedWorld;
        string bubble = "";
        if (world is { Present: true, CurrentSlice: int slice } && world.Slices.FirstOrDefault(row => row.Index == slice) is { } current)
            bubble = workspace.ResolveHash(current.BubbleHash) ?? $"0x{current.BubbleHash:X8}";
        return new PlayerFix("live", at, livePage.Camera is { } camera ? PlayerSources.YawFromForward(camera.Forward) : null, DateTimeOffset.UtcNow)
        {
            MapDefinition = world is { Present: true } ? world.MapDefinitionTag : null, Activity = world?.Activity ?? "", Bubble = bubble,
            Slice = world?.CurrentSlice?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""
        };
    }

    /** Reads the recorder every 400 ms and the server every second, on workers; a read still running is not doubled. */
    private void PollPlayerSources(long now)
    {
        if (now >= playerNextRecorder && playerRecorderRead is not { IsCompleted: false })
        {
            playerNextRecorder = now + 400;
            var roots = PlayerSources.RecorderRoots(workspace.Catalog?.GameRoot).ToArray();
            playerRecorderRead = Task.Run(() =>
            {
                try
                {
                    if (PlayerSources.LatestRecorderRoot(roots) is { } root)
                    {
                        var fix = PlayerSources.LatestRecorder(root);
                        playerRecorder = fix;
                        playerRecorderNote = fix is null ? "no in-world snapshot (menu, loading or not recording) in " + root
                            : Fresh(fix) is null ? "last snapshot is old (game closed or recorder stopped)" : "ok";
                        return;
                    }
                    playerRecorder = null;
                    playerRecorderNote = "no recorder folder; launch the game with SOL_RECORDER=1 (or set SOLFORGE_RECORDER)";
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { playerRecorderNote = error.Message; }
            });
        }
        if (now >= playerNextServer && playerServerRead is not { IsCompleted: false })
        {
            playerNextServer = now + 1000;
            string url = PlayerServerUrl + "/coop";
            playerServerRead = Task.Run(async () =>
            {
                try
                {
                    var rows = PlayerSources.ParseCoop(await PlayerHttp.GetStringAsync(url).ConfigureAwait(false), DateTimeOffset.UtcNow);
                    string? wanted = Environment.GetEnvironmentVariable("SOLFORGE_PLAYER");
                    playerServerRows = rows;
                    playerServer = rows.FirstOrDefault(row => wanted is { Length: > 0 } && string.Equals(row.Player, wanted, StringComparison.OrdinalIgnoreCase))
                        ?? rows.FirstOrDefault();
                    playerServerNote = rows.Count == 0 ? "no joined players" : $"{rows.Count} player(s); following {playerServer?.Player} (SOLFORGE_PLAYER picks)";
                }
                catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException)
                {
                    playerServer = null; playerServerRows = [];
                    playerServerNote = $"not reachable at {url} ({error.Message}); set SOLFORGE_SERVER_ADMIN for another address";
                }
            });
        }
    }

    /** The Live link names the map definition; the recorder and server name the activity, resolved through the mission list. */
    private PlayerPlace PlacePlayer(PlayerFix fix)
    {
        var world = workspace.World;
        if (fix.MapDefinition is { } definition)
        {
            bool matches = livePage.WorldMatches || world is not null && world.Map.DefinitionTag == definition;
            uint? map = matches ? world!.Map.Tag : workspace.WorldCatalog?.Maps.FirstOrDefault(row => row.DefinitionTag == definition)?.Tag;
            return new(matches, true, map, null);
        }
        if (PlayerMission(fix) is not { MapTags.Count: > 0 } mission) return new(false, false, null, null);
        bool on = world is not null && mission.MapTags.Contains(world.Map.Tag);
        return new(on, true, on ? world!.Map.Tag : mission.MapTags[0], mission);
    }

    /** The mission entry the position falls in, or null. */
    private WorldMissionEntry? PlayerMission(PlayerFix fix)
    {
        if (workspace.WorldCatalog?.Missions is not { } missions) return null;
        if (fix.Activity.Length == 0) return null;
        // loc.activity is the scenario's package name, not the localized title WorldMissionEntry.Name holds.
        if (workspace.Catalog is { } catalog && missions.FirstOrDefault(row => PlayerSources.IsScenario(catalog.GetName(row.Tag), fix.Activity)) is { } byName)
            return byName;
        return missions.FirstOrDefault(row => string.Equals(row.Name, fix.Activity, StringComparison.OrdinalIgnoreCase));
    }

    /** Opens the player's map once per place; the Live link switches maps itself (Sync > Map). */
    private void SwitchToPlayerMap(PlayerFix fix)
    {
        if (fix.Source == "live" || !playerPlace.Known || playerPlace.Map is not { } map || workspace.IsBusy) return;
        string key = $"{map:X8}:{playerPlace.Mission?.Tag:X8}";
        if (playerSwitchedFor == key) return;
        playerSwitchedFor = key;
        if (playerPlace.Mission is { } mission) workspace.Run(() => workspace.OpenMission(map, mission.Tag));
        else workspace.Run(() => workspace.OpenMap(map));
    }

    /** get_player_position: the fix and place on the UI thread, then nearest objects, slots and the target on a worker. */
    private async Task<JsonObject> AiPlayerPositionAsync(JsonObject args, CancellationToken cancel)
    {
        int near = Math.Clamp(McpJson.Int(args, "near", 5), 0, 25);
        string? target = McpJson.String(args, "target");
        bool wasTracking = await OnUi(() =>
        {
            bool tracking = workspace.World is not null || Environment.TickCount64 < playerWantedUntil;
            playerWantedUntil = Environment.TickCount64 + 120_000;
            return tracking;
        }).WaitAsync(cancel).ConfigureAwait(false);
        // Tracking was off: give the recorder and server a moment to answer.
        for (int wait = 0; !wasTracking && wait < 10 && await OnUi(() => playerFix is null).WaitAsync(cancel).ConfigureAwait(false); wait++)
            await Task.Delay(200, cancel).ConfigureAwait(false);
        var (state, fix, place, capture, entriesTask, missionTask) = await OnUi(() =>
        {
            var capture = AiCaptureNow();
            return (AiPlayerState(), playerFix, playerPlace, capture, AiEntriesTask(capture),
                inspectionWorld is null ? Task.FromResult<MissionSnapshot?>(null) : AiMissionTask(capture));
        }).WaitAsync(cancel).ConfigureAwait(false);
        var mission = await missionTask.WaitAsync(AiReadTimeout, cancel).ConfigureAwait(false);
        bool onMap = fix is not null && capture.World is not null && (place.Matches || !place.Known);
        if (target is { Length: > 0 })
        {
            var entries = await entriesTask.WaitAsync(AiReadTimeout, cancel).ConfigureAwait(false);
            if (await Task.Run(() => AiPlayerTarget(capture, mission, entries, target, fix), cancel).ConfigureAwait(false) is not { } found)
                return AiError($"No object or mission slot '{target}' on the open map; find_object lists candidates.");
            state["target"] = found;
        }
        if (onMap && near > 0)
            await Task.Run(() => AiPlayerNearby(state, capture, mission, fix!.Position, near), cancel).ConfigureAwait(false);
        return state;
    }

    /** The UI-thread half: source, age, map, zone, activity, position, yaw and why other sources are silent. */
    private JsonObject AiPlayerState()
    {
        var fix = playerFix;
        var world = workspace.World;
        var place = playerPlace;
        string? mapName = place.Map is { } tag ? workspace.WorldCatalog?.Maps.FirstOrDefault(row => row.Tag == tag)?.Name : null;
        var others = playerServerRows.Where(row => !ReferenceEquals(row, fix)).Take(16)
            .Select(row => (JsonNode)new JsonObject { ["name"] = row.Player, ["position"] = PlayerSources.Json(row.Position),
                ["room"] = row.Room.Length > 0 ? row.Room : null }).ToArray();
        var state = new JsonObject
        {
            ["found"] = fix is not null,
            ["source"] = fix?.Source,
            ["ageMs"] = fix is null ? null : (long)(DateTimeOffset.UtcNow - fix.Time).TotalMilliseconds,
            ["position"] = fix is null ? null : PlayerSources.Json(fix.Position),
            ["yaw"] = fix?.Yaw is { } yaw ? Math.Round(yaw, 1) : null,
            ["facing"] = fix?.Yaw is { } facing ? new JsonArray(Math.Round(PlayerSources.Facing(facing).X, 3), Math.Round(PlayerSources.Facing(facing).Y, 3)) : null,
            ["map"] = place.Map is { } map ? new JsonObject { ["tag"] = Hex(map), ["name"] = mapName } : null,
            ["mapKnown"] = place.Known,
            ["onOpenMap"] = fix is not null && world is not null && (place.Matches || !place.Known),
            ["openMap"] = world is null ? null : new JsonObject { ["tag"] = Hex(world.Map.Tag), ["name"] = world.Name },
            ["zone"] = fix is { Bubble.Length: > 0 } ? fix.Bubble : null,
            ["activity"] = fix is { Activity.Length: > 0 } ? fix.Activity : place.Mission?.Name,
            ["mission"] = place.Mission is { } mission ? new JsonObject { ["tag"] = Hex(mission.Tag), ["name"] = mission.Name } : null,
            ["slice"] = fix is { Slice.Length: > 0 } ? fix.Slice : null,
            ["player"] = fix?.Player,
            ["trailPoints"] = playerTrail.Points.Count,
            ["following"] = viewport?.FollowPlayer ?? false,
            ["sources"] = new JsonObject
            {
                ["live"] = !livePage.IsAttached ? "not attached (Live page; needs core.studio_attach in the client's settings.json)"
                    : fix?.Source == "live" ? "ok" : "attached, no player (loading or in a menu)",
                ["recorder"] = playerRecorderNote,
                ["server"] = playerServerNote
            },
            ["otherPlayers"] = new JsonArray(others)
        };
        if (fix is null) state["note"] = "No source has the player now; sources says why. The Live link is the most exact.";
        else if (!place.Known) state["note"] = "The source does not name the map; the position is shown on the open map.";
        else if (!place.Matches) state["note"] = "The player is on another map; open_map with that map, or turn on Follow player in the viewport.";
        return state;
    }

    /** Nearest named objects and placed mission slots, and the zone of the nearest object when the source gave none. */
    private static void AiPlayerNearby(JsonObject state, AiCapture capture, MissionSnapshot? mission, Vector3 at, int near)
    {
        var objects = capture.World!.Objects.Where(row => row.Position is not null)
            .Select(row => (Row: row, Distance: Vector3.Distance(row.Position!.Value, at)))
            .OrderBy(pair => pair.Distance).Take(near * 4)
            .Select(pair => (pair.Row, pair.Distance, Label: AiSafeLabel(capture, pair.Row)))
            .Where(pair => pair.Label.Length > 0).Take(near).ToArray();
        state["nearest"] = new JsonArray(objects.Select(pair => (JsonNode)new JsonObject
        {
            ["id"] = pair.Row.Id, ["label"] = pair.Label, ["kind"] = pair.Row.Kind.ToString(), ["zone"] = pair.Row.BubbleName,
            ["distance"] = Math.Round(pair.Distance, 1)
        }).ToArray());
        if (state["zone"] is null && objects.Length > 0 && objects[0].Row.BubbleName.Length > 0) state["zone"] = objects[0].Row.BubbleName;
        if (mission is null) return;
        state["slots"] = new JsonArray(mission.Slots.Where(slot => slot.Position is { Length: >= 3 })
            .Select(slot => (Slot: slot, Distance: Vector3.Distance(new Vector3(slot.Position![0], slot.Position[1], slot.Position[2]), at)))
            .OrderBy(pair => pair.Distance).Take(near)
            .Select(pair => (JsonNode)new JsonObject
            {
                ["slot"] = pair.Slot.SlotId, ["name"] = pair.Slot.Name, ["objectId"] = pair.Slot.ObjectId, ["distance"] = Math.Round(pair.Distance, 1)
            }).ToArray());
    }

    /** A slot id, an object id, or the best name match among the open map's objects and slots. */
    private static JsonObject? AiPlayerTarget(AiCapture capture, MissionSnapshot? mission, ObjectEntry[] entries, string target, PlayerFix? fix)
    {
        string id = target.Trim();
        if (mission?.Find(id) is null && capture.World?.Find(id) is null)
        {
            var all = entries.Concat(mission?.Entries() ?? Enumerable.Empty<ObjectEntry>());
            if (ObjectIndex.Find(all, target, capture.Map, null, 1).FirstOrDefault() is not { } match) return null;
            id = match.Entry.Id;
        }
        Vector3? position = null;
        string label = id;
        if (mission?.Find(id) is { } slot)
        {
            label = slot.Name;
            if (slot.Position is { Length: >= 3 } p) position = new Vector3(p[0], p[1], p[2]);
        }
        else if (capture.World?.Find(id) is { } row) { label = AiSafeLabel(capture, row); position = row.Position; }
        else return null;
        return new JsonObject
        {
            ["id"] = id, ["label"] = label, ["position"] = position is { } at ? PlayerSources.Json(at) : null,
            ["distance"] = position is { } goal && fix is not null ? Math.Round(Vector3.Distance(goal, fix.Position), 2) : null
        };
    }
}
