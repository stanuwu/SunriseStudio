/**
 * File: who is using this Solforge right now. Several AI agents can share one Solforge (each runs its own solforge-mcp
 * shim, or talks streamable HTTP directly); McpSessionRegistry remembers each MCP session (the Mcp-Session-Id the HTTP
 * server hands out at initialize, the clientInfo name and the shim's pid from X-Solforge-Client-Pid), which session opened
 * the map shown in the UI, and the builds and deploys sessions started. McpHttpServer sets McpSessionRegistry.Current for
 * the duration of each request; MultiAgentTools reads it.
 */
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/** One MCP client session. Mutable fields change under the registry lock. */
public sealed class McpSession(string id, string client, string? version, int? pid, DateTimeOffset started)
{
    public string Id { get; } = id;
    public string Client { get; internal set; } = client;
    public string? Version { get; internal set; } = version;
    /** The solforge-mcp shim's process id; null for clients that speak HTTP directly. */
    public int? Pid { get; internal set; } = pid;
    public DateTimeOffset Started { get; } = started;
    public DateTimeOffset LastSeen { get; internal set; } = started;
    public string? LastTool { get; internal set; }
    public int Calls { get; internal set; }
    /**
     * UI-UE5 §5.6: the project this session works on, bound at initialize (or at its first call when none was open). Calls
     * after the editor switched to another project are refused with "project changed" (McpHttpServer).
     */
    public string? Project { get; internal set; }
    /** A model-workflow worker (solforge-mcp --worker): WorkerSessionPolicy limits what it may call. */
    public bool IsWorker { get; internal set; }
    /** Planner/reviewer workers receive reads only, even while editor automatic approval is enabled. */
    public bool IsReadOnlyWorker { get; internal set; }
    /** "claude-code (pid 1234)": what people and other agents see. */
    public string Label => Pid is { } p ? $"{Client} (pid {p})" : Client;
    /** The id people can quote: the first 8 characters. */
    public string ShortId => Id.Length > 8 ? Id[..8] : Id;
}

/** A build, deploy, map load or server action a session (or the user) started. */
public sealed class McpActivity(long id, string tool, string summary, string? sessionId, string startedBy, DateTimeOffset started, bool background)
{
    public long Id { get; } = id;
    public string Tool { get; } = tool;
    public string Summary { get; } = summary;
    public string? SessionId { get; } = sessionId;
    public string StartedBy { get; } = startedBy;
    public DateTimeOffset Started { get; } = started;
    /** Background work (builds, map loads) keeps running after the tool replies, until Solforge is idle again. */
    public bool Background { get; } = background;
    public bool Replied { get; internal set; }
    public DateTimeOffset? Ended { get; internal set; }
    public string? Outcome { get; internal set; }
    public bool Running => Ended is null;
}

/** Which session opened the map the UI shows, and when. */
public sealed record MapClaim(string? SessionId, string Owner, uint MapTag, uint? MissionTag, DateTimeOffset Opened);

/**
 * The editor facts the multi-agent tools need beyond ISolforgeHost; the editor's host implements it next to ISolforgeHost.
 * Members are read on MCP threads.
 */
public interface ISolforgePresence
{
    /** The map open in the UI, or null. */
    uint? OpenMapTag { get; }
    string? OpenMapName { get; }
    /** What Solforge's worker is busy with (a build, a map load), or null when idle. */
    string? BusyWork { get; }
    /**
     * A read-only copy of another map for the map-scoped tools, loaded in the background without touching the UI. IsOpen
     * means the asked map is the one the UI shows (use the normal path). A load that is still running gives Pending text.
     */
    Task<MapScopeResult> PrepareMapScopeAsync(uint map, uint? mission, CancellationToken cancel);
}

/** PrepareMapScopeAsync's answer: exactly one of IsOpen, View, Pending or Error. */
public sealed record MapScopeResult(bool IsOpen, object? View, string? Name, string? Pending, string? Error)
{
    public static MapScopeResult Open() => new(true, null, null, null, null);
    public static MapScopeResult Ready(object view, string name) => new(false, view, name, null, null);
    public static MapScopeResult Loading(string text) => new(false, null, null, text, null);
    public static MapScopeResult Failed(string text) => new(false, null, null, null, text);
}

/**
 * The map a tool call should read instead of the UI's open map. MultiAgentTools sets it around a map-scoped tool call;
 * the editor's host answers find_object, get_object, the mission slots, find_world_objects and get_placement from it.
 */
public static class MapScope
{
    private static readonly AsyncLocal<object?> current = new();
    /** The background map view (editor-defined object) for this call, or null for the UI's open map. */
    public static object? Current { get => current.Value; set => current.Value = value; }
}

/** Every MCP session this Solforge has seen, the map claim and the activity list. Thread-safe. */
public sealed class McpSessionRegistry(Func<DateTimeOffset>? clock = null, Func<int, bool>? isAlive = null)
{
    private static readonly AsyncLocal<McpSession?> current = new();
    private readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Func<int, bool> isAlive = isAlive ?? ProcessAlive;
    private readonly Dictionary<string, McpSession> sessions = new(StringComparer.Ordinal);
    private readonly List<McpActivity> activities = [];
    private long nextActivity;
    private MapClaim? claim;

    /** The header the solforge-mcp shim sends with its process id. */
    public const string PidHeader = "X-Solforge-Client-Pid";
    /** The header `solforge-mcp --worker` sends at initialize; the session then follows WorkerSessionPolicy. */
    public const string WorkerHeader = "X-Solforge-Worker";
    public const string ReadOnlyWorkerHeader = "X-Solforge-Worker-ReadOnly";
    /** JSON-RPC error code of a call refused because the editor switched projects (the shim then reconnects). */
    public const int ProjectChangedCode = -32001;

    /** Binds a session to a project and marks workers; called right after Register at initialize. */
    public void Bind(McpSession session, string? project, bool worker, bool readOnly = false)
    {
        lock (sessions) { session.Project = Normalize(project); session.IsWorker |= worker || readOnly; session.IsReadOnlyWorker |= readOnly; }
        Changed?.Invoke();
    }

    /**
     * Null when the session may act on the current project, else the "project changed" refusal. A session bound to no
     * project binds to the current one on its first call.
     */
    public string? ProjectProblem(McpSession? session, string? current)
    {
        if (session is null) return null;
        string? now = Normalize(current);
        lock (sessions)
        {
            if (session.Project is null) { session.Project = now; return null; }
            if (now is null || string.Equals(session.Project, now, StringComparison.OrdinalIgnoreCase)) return null;
            return $"project changed: this session was opened for {session.Project}, and Solforge now has {now} open. " +
                "Nothing was done; reconnect (the solforge-mcp shim does this by itself) and read again before acting.";
        }
    }

    /** After the session itself opened, created or closed a project, it follows the editor to it. */
    public void Rebind(McpSession? session, string? current)
    {
        if (session is null) return;
        lock (sessions) session.Project = Normalize(current);
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }
    /** Sessions idle this long are dropped (clients that speak HTTP directly never say goodbye). */
    public static readonly TimeSpan Forget = TimeSpan.FromHours(2);

    /** open_map and close_map refuse to replace another session's map opened less than this long ago (unless force=true). */
    public TimeSpan MapGuard { get; set; } = TimeSpan.FromMinutes(
        int.TryParse(Environment.GetEnvironmentVariable("SOLFORGE_MAP_GUARD_MINUTES"), out int minutes) && minutes >= 0 ? minutes : 10);

    /** The session of the request being served on this async flow, or null (no session header, or an unknown one). */
    public static McpSession? Current { get => current.Value; set => current.Value = value; }

    /** Raised after any change, for the editor to redraw its presence list. */
    public event Action? Changed;

    public DateTimeOffset Now => clock();

    /** Adds a session at initialize: clientInfo name and version, and the shim pid when it sent one. */
    public McpSession Register(string id, string? client, string? version, int? pid)
    {
        McpSession session;
        lock (sessions)
        {
            session = new McpSession(id, string.IsNullOrWhiteSpace(client) ? "unknown client" : client.Trim(), version, pid, clock());
            sessions[id] = session;
        }
        Changed?.Invoke();
        return session;
    }

    /** The session with this id, touched as seen now; a pid header fills a pid the session lacked. */
    public McpSession? Find(string? id, int? pid = null)
    {
        if (id is null) return null;
        lock (sessions)
        {
            if (!sessions.TryGetValue(id, out var session)) return null;
            session.LastSeen = clock();
            if (session.Pid is null && pid is not null) session.Pid = pid;
            return session;
        }
    }

    /** Ends a session (DELETE /mcp). Its map claim stays until it expires, so another agent still sees who opened the map. */
    public void Remove(string? id)
    {
        if (id is null) return;
        bool removed;
        lock (sessions) removed = sessions.Remove(id);
        if (removed) Changed?.Invoke();
    }

    /** Counts a tool call for the session (null: an anonymous call, not counted). */
    public void NoteCall(McpSession? session, string tool)
    {
        if (session is null) return;
        lock (sessions) { session.LastTool = tool; session.Calls++; session.LastSeen = clock(); }
    }

    /** Live sessions, newest activity first; dead shims and long-idle sessions are dropped. */
    public IReadOnlyList<McpSession> Sessions()
    {
        McpSession[] list;
        lock (sessions)
        {
            var now = clock();
            foreach (var stale in sessions.Values.Where(s => now - s.LastSeen > Forget || (s.Pid is { } pid && !isAlive(pid))).ToArray())
                sessions.Remove(stale.Id);
            list = [.. sessions.Values.OrderByDescending(s => s.LastSeen)];
        }
        return list;
    }

    /** Who opened the UI's map; cleared once the UI shows another map (the user opened one) and nothing is loading. */
    public MapClaim? Claim(ISolforgePresence? presence)
    {
        lock (sessions)
        {
            // MapTag 0: open_map named only a mission; the map it loaded on is known once the load finishes.
            if (claim is { MapTag: 0 } pending && presence is not null && presence.BusyWork is null)
                claim = presence.OpenMapTag is { } loaded ? pending with { MapTag = loaded } : null;
            if (claim is { } held && presence is not null && presence.BusyWork is null && presence.OpenMapTag != held.MapTag) claim = null;
            return claim;
        }
    }

    /** Records that the caller opened this map (open_map succeeded). */
    public void ClaimMap(McpSession? session, uint map, uint? mission)
    {
        lock (sessions) claim = new MapClaim(session?.Id, session?.Label ?? "an MCP client without a session", map, mission, clock());
        Changed?.Invoke();
    }

    /** close_map succeeded: nobody holds the map. */
    public void ReleaseMap()
    {
        lock (sessions) claim = null;
        Changed?.Invoke();
    }

    /**
     * Why the caller may not replace the UI's map now, or null when it may: another session opened it less than MapGuard
     * ago. The same session, an expired claim, force, or asking for the map already open (no mission) always pass.
     */
    public string? MapGuardProblem(McpSession? caller, ISolforgePresence? presence, uint? wantedMap, uint? wantedMission, bool force, string verb)
    {
        if (force || Claim(presence) is not { } held) return null;
        if (caller is not null && held.SessionId == caller.Id) return null;
        var age = clock() - held.Opened;
        if (age >= MapGuard) return null;
        if (wantedMap is { } map && map == held.MapTag && wantedMission is null && verb == "open") return null;
        string name = presence?.OpenMapName is { Length: > 0 } shown && presence.OpenMapTag == held.MapTag ? $"{shown} (0x{held.MapTag:X8})" : $"0x{held.MapTag:X8}";
        return $"Not done: {held.Owner} opened map {name} {Minutes(age)} ago, and {(verb == "open" ? "opening another map" : "closing it")} " +
            "would take it away from that agent. To read another map without changing the UI, pass map=<tag> to find_object, get_object, " +
            "list_mission_objects, get_mission_object, find_world_objects or get_placement (get_sessions shows who is here). " +
            $"If you really must, call {verb}_map again with force=true (the guard lasts {MapGuard.TotalMinutes:0} minutes).";
    }

    /** Starts an activity for the caller (or "the user" when there is no session). */
    public McpActivity Begin(McpSession? session, string tool, string summary, bool background)
    {
        McpActivity activity;
        lock (sessions)
        {
            activity = new McpActivity(++nextActivity, tool, summary, session?.Id, session?.Label ?? "an MCP client without a session", clock(), background);
            activities.Add(activity);
            // Keep the newest 50: the oldest finished rows go first.
            while (activities.Count > 50 && activities.FindIndex(a => !a.Running) is var oldest and >= 0) activities.RemoveAt(oldest);
        }
        Changed?.Invoke();
        return activity;
    }

    /** The tool replied. Failed or foreground work ends now; background work runs on until Solforge is idle. */
    public void Replied(McpActivity activity, bool ok, string? outcome)
    {
        lock (sessions)
        {
            activity.Replied = true;
            activity.Outcome = outcome;
            if (!ok || !activity.Background) activity.Ended = clock();
        }
        Changed?.Invoke();
    }

    /** Running activities, oldest first; background ones whose tool replied end once Solforge is idle. */
    public IReadOnlyList<McpActivity> Running(ISolforgePresence? presence)
    {
        lock (sessions)
        {
            var now = clock();
            foreach (var activity in activities)
                if (activity is { Running: true, Background: true, Replied: true } && presence?.BusyWork is null
                    && now - activity.Started > TimeSpan.FromSeconds(1))
                    activity.Ended = now;
            return [.. activities.Where(a => a.Running)];
        }
    }

    /** Recent finished activities, newest first. */
    public IReadOnlyList<McpActivity> Recent(int count)
    {
        lock (sessions) return [.. activities.Where(a => !a.Running).OrderByDescending(a => a.Ended).Take(count)];
    }

    /** get_sessions: you, every live session, the map claim, running and recent work. */
    public JsonObject ToJson(McpSession? you, ISolforgePresence? presence)
    {
        var now = clock();
        var list = new JsonArray();
        var held = Claim(presence);
        foreach (var session in Sessions())
            list.Add(new JsonObject
            {
                ["id"] = session.Id, ["client"] = session.Client, ["version"] = session.Version, ["pid"] = session.Pid,
                ["label"] = session.Label, ["you"] = you is not null && you.Id == session.Id,
                ["connectedMinutes"] = Math.Round((now - session.Started).TotalMinutes, 1),
                ["idleSeconds"] = Math.Round((now - session.LastSeen).TotalSeconds), ["calls"] = session.Calls, ["lastTool"] = session.LastTool,
                ["hasMap"] = held is not null && held.SessionId == session.Id,
                ["project"] = session.Project, ["worker"] = session.IsWorker, ["readOnlyWorker"] = session.IsReadOnlyWorker
            });
        var running = new JsonArray();
        var live = Running(presence);
        foreach (var activity in live) running.Add(ActivityJson(activity, now));
        string? busy = presence?.BusyWork;
        if (busy is not null && !live.Any(a => a.Background))
            running.Add(new JsonObject { ["tool"] = null, ["summary"] = busy, ["startedBy"] = "the user (Solforge UI)" });
        var recent = new JsonArray();
        foreach (var activity in Recent(5)) recent.Add(ActivityJson(activity, now));
        return new JsonObject
        {
            ["you"] = you is null ? null : new JsonObject { ["id"] = you.Id, ["label"] = you.Label },
            ["sessions"] = list,
            ["map"] = presence?.OpenMapTag is { } tag ? new JsonObject
            {
                ["tag"] = $"0x{tag:X8}", ["name"] = presence.OpenMapName,
                ["openedBy"] = held?.Owner ?? "the user (Solforge UI)", ["openedBySession"] = held?.SessionId,
                ["minutesAgo"] = held is null ? null : Math.Round((now - held.Opened).TotalMinutes, 1),
                ["guardedMinutesLeft"] = held is null ? null : Math.Max(0, Math.Round((MapGuard - (now - held.Opened)).TotalMinutes, 1))
            } : null,
            ["running"] = running,
            ["recent"] = recent,
            ["note"] = "open_map/close_map refuse to replace another session's map for " + MapGuard.TotalMinutes.ToString("0") +
                " minutes unless force=true; map-scoped reads (map=<tag>) never change the UI."
        };
    }

    /** One line per row for the editor's presence list: sessions (with the map holder marked) then running work. */
    public IReadOnlyList<(string Text, bool Busy)> PresenceLines(ISolforgePresence? presence)
    {
        var now = clock();
        var held = Claim(presence);
        var lines = new List<(string, bool)>();
        foreach (var session in Sessions())
        {
            string map = held is not null && held.SessionId == session.Id
                ? $" - has the map open ({presence?.OpenMapName ?? $"0x{held.MapTag:X8}"}, {Minutes(now - held.Opened)} ago)" : "";
            string idle = now - session.LastSeen > TimeSpan.FromMinutes(2) ? $", idle {Minutes(now - session.LastSeen)}" : "";
            lines.Add(($"{session.Label} [{session.ShortId}]{idle}{map}", false));
        }
        if (held is not null && held.SessionId is { } owner && !lines.Any(line => line.Item1.Contains(owner[..Math.Min(8, owner.Length)], StringComparison.Ordinal)))
            lines.Add(($"{held.Owner} (gone) opened the map {Minutes(now - held.Opened)} ago", false));
        var running = Running(presence);
        foreach (var activity in running)
            lines.Add(($"{activity.Summary} - started by {activity.StartedBy} {Minutes(now - activity.Started)} ago", true));
        if (presence?.BusyWork is { } busy && !running.Any(a => a.Background)) lines.Add(($"{busy} - started in the Solforge UI", true));
        return lines;
    }

    private static JsonObject ActivityJson(McpActivity activity, DateTimeOffset now) => new()
    {
        ["tool"] = activity.Tool, ["summary"] = activity.Summary, ["startedBy"] = activity.StartedBy, ["session"] = activity.SessionId,
        ["minutesAgo"] = Math.Round((now - activity.Started).TotalMinutes, 1), ["running"] = activity.Running,
        ["outcome"] = activity.Outcome
    };

    private static string Minutes(TimeSpan span) => span.TotalMinutes < 1 ? $"{Math.Max(0, (int)span.TotalSeconds)} s" : $"{(int)span.TotalMinutes} min";

    private static bool ProcessAlive(int pid)
    {
        // GetProcessById throws for a pid that is not running; HasExited would need rights we may not have.
        try { using var process = Process.GetProcessById(pid); return true; }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { return false; }
    }
}
