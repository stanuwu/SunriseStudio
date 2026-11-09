/**
 * File: the editor UI channel (SOLFORGE-AI-APP-SPEC §6.2, §6.3): /editor/* on the MCP loopback server, used by the Solforge
 * AI app's Electron main process to show and answer the editor's AI requests, read the AI log, sessions and AI edits, and
 * drive the context features. It is not MCP and agents cannot use it:
 *  - every path but /editor/pair needs "Authorization: Bearer <UiToken>", a per-process random token that is never written
 *    to disk, discovery or any environment; the TCP peer must also be a paired Solforge AI process (GetExtendedTcpTable);
 *  - requests carrying Mcp-Session-Id, X-Solforge-Client-Pid or X-Solforge-Worker are refused, and /mcp refuses the UiToken;
 *  - POST /editor/pair checks that the TCP peer pid is the stated app pid and its image is named Solforge AI.exe, accepts
 *    silently only the known build this editor launched, and otherwise asks the user in a modal MCP UI automation cannot
 *    click (an unrecognized build is flagged there). One pairing may wait at a time; a deny starts a 30 s cooldown. The
 *    packaged Solforge AI.exe has the Electron RunAsNode fuse off (ai/package-desktop.ps1 step 1c), so a process with that
 *    image is the Electron browser process, not a Node-mode script.
 * The editor (IEditorUiHost) runs every state change on its UI thread. Approvals stay a user decision: this channel never
 * auto-approves; the editor's user-controlled AutoApproveRequests preference covers permitted actions and project edits,
 * including worker sessions. The channel cannot turn that preference on.
 */
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Solforge.Mcp;

/** A reply of the editor: HTTP status and a body serialized with EditorUiJson.Options. */
public sealed record EditorUiResult(int Status, object? Body)
{
    public static EditorUiResult Ok(object? body) => new(200, body);
    public static EditorUiResult Fail(int status, string code, string message) => new(status, new EditorErrorDto(message, code));
}

/** What the editor provides to the UI channel. Implementations marshal onto the UI thread. */
public interface IEditorUiHost
{
    /** The open project, read from any thread. */
    EditorProjectDto? Project { get; }
    /** True when this editor started that process (the Solforge AI launcher knows its pid). */
    bool LaunchedApp(int pid);
    /** True when the image is the configured Solforge AI exe or the one AppDiscovery finds. */
    bool IsSolforgeAiImage(string imagePath);
    /**
     * Asks the user (blocked for MCP UI automation) whether this app may pair; false on Deny or close. recognized is false
     * for a "Solforge AI.exe" that is not the configured or discovered build, which the prompt calls out.
     */
    Task<bool> ConfirmPairAsync(int pid, string imagePath, bool recognized, CancellationToken cancel);

    Task<EditorStateDto> GetStateAsync(CancellationToken cancel);
    Task<IReadOnlyList<EditorRequestDto>> GetRequestsAsync(CancellationToken cancel);
    /** Approve or deny a pending request: 404 when it is gone, 409 when answered already, 422 for an invalid workflow start. */
    Task<EditorUiResult> AnswerAsync(string id, bool approve, EditorApproveBody? body, CancellationToken cancel);
    Task<EditorLogDto> GetLogAsync(long after, CancellationToken cancel);
    Task<object> GetSessionsAsync(CancellationToken cancel);
    Task<IReadOnlyList<EditorEditDto>> GetEditsAsync(CancellationToken cancel);
    Task<EditorUiResult> UndoEditAsync(string id, CancellationToken cancel);
    Task<EditorContextDto> GetContextAsync(CancellationToken cancel);
    Task<EditorUiResult> SetPublishAsync(bool on, CancellationToken cancel);
    Task<EditorUiResult> PinAsync(string target, CancellationToken cancel);
    Task<EditorUiResult> UnpinAsync(int index, CancellationToken cancel);
    Task<EditorFoldersDto> GetFoldersAsync(CancellationToken cancel);
    Task<EditorUiResult> FolderOpAsync(string op, string path, CancellationToken cancel);
    Task<EditorUiResult> AskAsync(CancellationToken cancel);
    Task<EditorUiResult> ScreenshotAsync(CancellationToken cancel);
}

/** The /editor/* handler, its event stream and the pairing state. Thread-safe. */
public sealed partial class EditorUiApi : IDisposable
{
    private const int EventRing = 500;
    private static readonly TimeSpan PairCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PairWait = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);
    private static readonly string[] RefusedHeaders = ["mcp-session-id", McpSessionRegistry.PidHeader, McpSessionRegistry.WorkerHeader, McpSessionRegistry.ReadOnlyWorkerHeader];

    private readonly IEditorUiHost host;
    private readonly object sync = new();
    private readonly LinkedList<(long Seq, string Frame)> ring = new();
    private readonly List<Channel<string>> subscribers = [];
    private readonly HashSet<int> paired = [];
    private readonly HashSet<string> coalesced = new(StringComparer.Ordinal);
    private readonly Timer coalesceTimer;
    private long seq;
    private bool pairPending;
    private DateTimeOffset pairCooldownUntil = DateTimeOffset.MinValue;
    private DateTimeOffset lastContact = DateTimeOffset.MinValue;
    private int activeStreams;

    public EditorUiApi(IEditorUiHost host)
    {
        this.host = host;
        UiToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        coalesceTimer = new Timer(_ => FlushCoalesced(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /** The per-process UI token (64 hex). Never written anywhere; handed out only by an accepted pairing. */
    public string UiToken { get; }

    /** Newest event sequence number. */
    public long Seq => Interlocked.Read(ref seq);

    /** True while a paired app holds the event stream or made an authorized request in the last 5 s (the editor's fallback check). */
    public bool AppAttached
    {
        get { lock (sync) return activeStreams > 0 || DateTimeOffset.UtcNow - lastContact < TimeSpan.FromSeconds(5); }
    }

    /** Paired app pids still running. */
    public IReadOnlyList<int> PairedApps
    {
        get { lock (sync) { paired.RemoveWhere(pid => !Alive(pid)); return [.. paired]; } }
    }

    /** True while a pairing waits for the user. */
    public bool PairPending { get { lock (sync) return pairPending; } }

    /** Raised (any thread) when pairing or attachment changes, for the editor to redraw. */
    public event Action? Changed;

    /** Publishes one event to every stream and the resume ring. */
    public void Emit(string type, object? data)
    {
        string frame;
        long id;
        lock (sync)
        {
            id = ++seq;
            string json = JsonSerializer.Serialize(new EditorEventDto(type, id, data ?? new Dictionary<string, object>()), EditorUiJson.Options);
            frame = $"id: {id}\nevent: {type}\ndata: {json}\n\n";
            ring.AddLast((id, frame));
            while (ring.Count > EventRing) ring.RemoveFirst();
            foreach (var subscriber in subscribers) subscriber.Writer.TryWrite(frame);
        }
    }

    /** Emits a data-less event at most once per 250 ms (sessions, context and state changes come in bursts). */
    public void EmitSoon(string type)
    {
        lock (sync)
        {
            if (!coalesced.Add(type)) return;
            if (coalesced.Count == 1) coalesceTimer.Change(250, Timeout.Infinite);
        }
    }

    private void FlushCoalesced()
    {
        string[] types;
        lock (sync) { types = [.. coalesced]; coalesced.Clear(); }
        foreach (string type in types) Emit(type, null);
    }

    /** Serves one /editor/* request (McpHttpServer routes them here after its Origin check). */
    internal async Task ServeAsync(NetworkStream stream, HttpRequest request, IPEndPoint? remote, IPEndPoint? local, CancellationToken stop)
    {
        foreach (string header in RefusedHeaders)
            if (request.Header(header) is not null)
            { await Reply(stream, EditorUiResult.Fail(403, "mcp_header", "MCP session headers are not accepted on the editor UI channel.")).ConfigureAwait(false); return; }
        int? peer = remote is not null && local is not null ? TcpOwner.PidOf(remote, local) : null;
        if (request.Path == EditorUiPaths.Pair)
        {
            if (request.Method != "POST") { await Reply(stream, EditorUiResult.Fail(405, "method", "Use POST.")).ConfigureAwait(false); return; }
            await Reply(stream, await PairAsync(request, peer, stop).ConfigureAwait(false)).ConfigureAwait(false);
            return;
        }
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.Header("authorization") ?? ""), Encoding.UTF8.GetBytes("Bearer " + UiToken)))
        { await Reply(stream, EditorUiResult.Fail(401, "token", "Missing or wrong editor UI token; pair first.")).ConfigureAwait(false); return; }
        if (peer is not { } pid || !IsPaired(pid))
        { await Reply(stream, EditorUiResult.Fail(403, "peer", "The caller is not a paired Solforge AI process.")).ConfigureAwait(false); return; }
        Touch();
        if (request.Method == "GET" && request.Path == EditorUiPaths.Events)
        {
            await StreamEvents(stream, request, stop).ConfigureAwait(false);
            return;
        }
        EditorUiResult result;
        try { result = await Route(request, stop).ConfigureAwait(false); }
        catch (JsonException) { result = EditorUiResult.Fail(400, "json", "The body is not valid JSON for this path."); }
        catch (TimeoutException) { result = EditorUiResult.Fail(503, "busy", "The editor did not answer in time (busy or minimised)."); }
        catch (InvalidOperationException error) { result = EditorUiResult.Fail(503, "unavailable", error.Message); }
        await Reply(stream, result).ConfigureAwait(false);
    }

    [GeneratedRegex(@"\A/editor/requests/([0-9a-f]{32})/(approve|deny)\z")]
    private static partial Regex AnswerPath();
    [GeneratedRegex(@"\A/editor/edits/([0-9a-f]{32})/undo\z")]
    private static partial Regex UndoPath();
    [GeneratedRegex(@"\A/editor/context/pins/(\d{1,4})(/remove)?\z")]
    private static partial Regex PinPath();

    private async Task<EditorUiResult> Route(HttpRequest request, CancellationToken stop)
    {
        var timeout = TimeSpan.FromSeconds(15);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop);
        cancel.CancelAfter(timeout);
        var token = cancel.Token;
        string path = request.Path;
        if (request.Method == "GET")
            return path switch
            {
                EditorUiPaths.State => EditorUiResult.Ok(await host.GetStateAsync(token).WaitAsync(timeout).ConfigureAwait(false) with { Seq = Seq }),
                EditorUiPaths.Requests => EditorUiResult.Ok(await host.GetRequestsAsync(token).WaitAsync(timeout).ConfigureAwait(false)),
                EditorUiPaths.Log => EditorUiResult.Ok(await host.GetLogAsync(long.TryParse(request.QueryValue("after"), out long after) ? after : 0, token)
                    .WaitAsync(timeout).ConfigureAwait(false)),
                EditorUiPaths.Sessions => EditorUiResult.Ok(await host.GetSessionsAsync(token).WaitAsync(timeout).ConfigureAwait(false)),
                EditorUiPaths.Edits => EditorUiResult.Ok(await host.GetEditsAsync(token).WaitAsync(timeout).ConfigureAwait(false)),
                EditorUiPaths.Context => EditorUiResult.Ok(await host.GetContextAsync(token).WaitAsync(timeout).ConfigureAwait(false)),
                EditorUiPaths.Folders => EditorUiResult.Ok(await host.GetFoldersAsync(token).WaitAsync(timeout).ConfigureAwait(false)),
                _ => EditorUiResult.Fail(404, "path", "Not found")
            };
        if (request.Method == "DELETE" && PinPath().Match(path) is { Success: true, Groups: var deleteGroups } && deleteGroups[2].Length == 0)
            return await host.UnpinAsync(int.Parse(deleteGroups[1].Value, System.Globalization.CultureInfo.InvariantCulture), token).WaitAsync(timeout).ConfigureAwait(false);
        if (request.Method != "POST") return EditorUiResult.Fail(405, "method", "Method not allowed");
        if (AnswerPath().Match(path) is { Success: true } answer)
        {
            bool approve = answer.Groups[2].Value == "approve";
            var body = approve && request.Body.Length > 0 ? Parse<EditorApproveBody>(request) : null;
            return await host.AnswerAsync(answer.Groups[1].Value, approve, body, token).WaitAsync(timeout).ConfigureAwait(false);
        }
        if (UndoPath().Match(path) is { Success: true } undo)
            return await host.UndoEditAsync(undo.Groups[1].Value, token).WaitAsync(timeout).ConfigureAwait(false);
        if (PinPath().Match(path) is { Success: true } pin && pin.Groups[2].Length > 0)
            return await host.UnpinAsync(int.Parse(pin.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), token).WaitAsync(timeout).ConfigureAwait(false);
        switch (path)
        {
            case EditorUiPaths.ContextPublish:
                return await host.SetPublishAsync(Parse<EditorPublishBody>(request)?.On ?? throw new JsonException(), token).WaitAsync(timeout).ConfigureAwait(false);
            case EditorUiPaths.ContextPins:
                string target = Parse<EditorPinBody>(request)?.Target ?? "hovered";
                if (target is not ("hovered" or "selected")) return EditorUiResult.Fail(400, "target", "target must be hovered or selected.");
                return await host.PinAsync(target, token).WaitAsync(timeout).ConfigureAwait(false);
            case EditorUiPaths.Folders:
                var op = Parse<EditorFolderOpBody>(request);
                if (op is null || op.Op is not ("add" or "remove" or "hide" or "show") || string.IsNullOrWhiteSpace(op.Path) || op.Path.Length > 1024)
                    return EditorUiResult.Fail(400, "folder", "op must be add, remove, hide or show, with a path.");
                return await host.FolderOpAsync(op.Op, op.Path, token).WaitAsync(timeout).ConfigureAwait(false);
            case EditorUiPaths.Ask:
                return await host.AskAsync(token).WaitAsync(timeout).ConfigureAwait(false);
            case EditorUiPaths.Screenshot:
                return await host.ScreenshotAsync(token).WaitAsync(timeout).ConfigureAwait(false);
            default:
                return EditorUiResult.Fail(404, "path", "Not found");
        }
    }

    private static readonly JsonSerializerOptions ReadOptions = new(EditorUiJson.Options) { PropertyNameCaseInsensitive = true };

    private static T? Parse<T>(HttpRequest request) where T : class =>
        request.Body.Length == 0 ? null : JsonSerializer.Deserialize<T>(request.Body, ReadOptions);

    private async Task<EditorUiResult> PairAsync(HttpRequest request, int? peer, CancellationToken stop)
    {
        EditorPairBody? body;
        try { body = Parse<EditorPairBody>(request); }
        catch (JsonException) { body = null; }
        if (body is null || body.AppPid <= 0) return EditorUiResult.Fail(400, "body", "Send {appPid, appVersion, nonce}.");
        if (peer != body.AppPid) return EditorUiResult.Fail(403, "peer_mismatch", "The connection does not come from the stated process.");
        if (ProcessImage(body.AppPid) is not { } image
            || !string.Equals(Path.GetFileName(image), "Solforge AI.exe", StringComparison.OrdinalIgnoreCase))
            return EditorUiResult.Fail(403, "not_solforge_ai", "Only the Solforge AI app may pair with this editor.");
        // A "Solforge AI.exe" that is not the configured or discovered build (a standalone copy elsewhere) is never accepted
        // silently: the user decides, and the prompt says the build is not the one this editor knows.
        bool recognized = host.IsSolforgeAiImage(image);
        bool silent;
        lock (sync)
        {
            bool known = paired.Contains(body.AppPid) && Alive(body.AppPid);
            silent = known || (recognized && host.LaunchedApp(body.AppPid));
            if (!silent)
            {
                if (pairPending) return EditorUiResult.Fail(409, "pending", "Another pairing is waiting for the user.");
                if (DateTimeOffset.UtcNow < pairCooldownUntil) return EditorUiResult.Fail(429, "cooldown", "Pairing was denied; wait 30 s before asking again.");
                pairPending = true;
            }
        }
        if (!silent)
        {
            Raise();
            bool allowed = false;
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop);
                wait.CancelAfter(PairWait);
                allowed = await host.ConfirmPairAsync(body.AppPid, image, recognized, wait.Token).WaitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { allowed = false; }
            finally
            {
                lock (sync)
                {
                    pairPending = false;
                    if (!allowed) pairCooldownUntil = DateTimeOffset.UtcNow + PairCooldown;
                }
            }
            if (!allowed) { Raise(); return EditorUiResult.Fail(403, "denied", "The editor's user did not allow this connection."); }
        }
        lock (sync) { paired.Add(body.AppPid); lastContact = DateTimeOffset.UtcNow; }
        Raise();
        return EditorUiResult.Ok(new EditorPairDto(UiToken, Environment.ProcessId, host.Project, body.Nonce));
    }

    private bool IsPaired(int pid)
    {
        lock (sync)
        {
            if (!paired.Contains(pid)) return false;
            if (Alive(pid)) return true;
            paired.Remove(pid);
        }
        Raise();
        return false;
    }

    private void Touch()
    {
        bool was;
        lock (sync) { was = activeStreams > 0 || DateTimeOffset.UtcNow - lastContact < TimeSpan.FromSeconds(5); lastContact = DateTimeOffset.UtcNow; }
        if (!was) Raise();
    }

    private async Task StreamEvents(NetworkStream stream, HttpRequest request, CancellationToken stop)
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });
        var replay = new List<string>();
        long.TryParse(request.Header("last-event-id"), out long resume);
        lock (sync)
        {
            if (resume > 0)
            {
                long oldest = ring.First?.Value.Seq ?? seq + 1;
                if (resume + 1 < oldest)
                    replay.Add($"id: {seq}\nevent: {EditorUiEvents.StateChanged}\ndata: " +
                        JsonSerializer.Serialize(new EditorEventDto(EditorUiEvents.StateChanged, seq, new { resync = true }), EditorUiJson.Options) + "\n\n");
                else
                    foreach (var (id, frame) in ring) if (id > resume) replay.Add(frame);
            }
            subscribers.Add(channel);
            activeStreams++;
        }
        Raise();
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream; charset=utf-8\r\nCache-Control: no-store\r\n" +
                "Connection: close\r\nX-Content-Type-Options: nosniff\r\n\r\n"), stop).ConfigureAwait(false);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(": connected\n\n" + string.Concat(replay)), stop).ConfigureAwait(false);
            while (!stop.IsCancellationRequested)
            {
                using var beat = CancellationTokenSource.CreateLinkedTokenSource(stop);
                beat.CancelAfter(Heartbeat);
                string text;
                try { text = await channel.Reader.ReadAsync(beat.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested) { text = ": heartbeat\n\n"; }
                await stream.WriteAsync(Encoding.UTF8.GetBytes(text), stop).ConfigureAwait(false);
                lock (sync) lastContact = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            lock (sync) { subscribers.Remove(channel); activeStreams--; lastContact = DateTimeOffset.UtcNow; }
            Raise();
        }
    }

    private static async Task Reply(NetworkStream stream, EditorUiResult result) =>
        await McpHttpServer.Write(stream, result.Status, "application/json", JsonSerializer.Serialize(result.Body, EditorUiJson.Options),
            [("Cache-Control", "no-store"), ("X-Content-Type-Options", "nosniff")]).ConfigureAwait(false);

    private void Raise()
    {
        try { Changed?.Invoke(); } catch (Exception error) { Debug.WriteLine("[editor-ui] Changed handler failed: " + error.Message); }
    }

    private static bool Alive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    /** Full image path of a process (PROCESS_QUERY_LIMITED_INFORMATION), or null. */
    private static string? ProcessImage(int pid)
    {
        if (!OperatingSystem.IsWindows()) return null;
        IntPtr handle = OpenProcess(0x1000, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    public void Dispose()
    {
        coalesceTimer.Dispose();
        lock (sync)
        {
            foreach (var subscriber in subscribers) subscriber.Writer.TryComplete();
            subscribers.Clear();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    /** Owning process of a loopback TCP connection, from the IPv4 owner-pid TCP table. */
    private static class TcpOwner
    {
        private const int AfInet = 2, TcpTableOwnerPidAll = 5;

        /** The pid owning the client end (remote, as seen by the server) of a connection to local; null when unknown. */
        public static int? PidOf(IPEndPoint remote, IPEndPoint local)
        {
            if (!OperatingSystem.IsWindows() || remote.AddressFamily != AddressFamily.InterNetwork) return null;
            int size = 0;
            _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                IntPtr table = Marshal.AllocHGlobal(size);
                try
                {
                    uint result = GetExtendedTcpTable(table, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
                    if (result == 122) continue; // ERROR_INSUFFICIENT_BUFFER: the table grew; size holds the new need.
                    if (result != 0) return null;
                    int rows = Marshal.ReadInt32(table);
                    uint remoteAddress = AddressValue(remote.Address), localAddress = AddressValue(local.Address);
                    for (int i = 0; i < rows; i++)
                    {
                        IntPtr row = table + 4 + i * 24;
                        uint rowLocalAddress = (uint)Marshal.ReadInt32(row, 4), rowRemoteAddress = (uint)Marshal.ReadInt32(row, 12);
                        int rowLocalPort = Port(Marshal.ReadInt32(row, 8)), rowRemotePort = Port(Marshal.ReadInt32(row, 16));
                        if (rowLocalPort == remote.Port && rowRemotePort == local.Port && rowLocalAddress == remoteAddress && rowRemoteAddress == localAddress)
                            return Marshal.ReadInt32(row, 20);
                    }
                    return null;
                }
                finally { Marshal.FreeHGlobal(table); }
            }
            return null;
        }

        private static uint AddressValue(IPAddress address) => BitConverter.ToUInt32(address.MapToIPv4().GetAddressBytes(), 0);
        private static int Port(int value) => ((value & 0xFF) << 8) | ((value >> 8) & 0xFF);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, int reserved);
    }
}
