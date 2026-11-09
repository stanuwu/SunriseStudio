using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/**
 * MCP streamable HTTP on 127.0.0.1 only: POST /mcp carries one JSON-RPC message, GET /mcp opens an SSE
 * stream (keep-alive only; Solforge sends no server requests), DELETE /mcp ends the session. Every request
 * needs "Authorization: Bearer <token>". A plain TcpListener keeps http.sys URL ACLs and ASP.NET Core out.
 * GET /viewport, /viewport.mjpeg and /viewport.jpg show the live viewport in a local browser; they need ?key=<ViewKey>,
 * a separate random key that grants nothing else, and refuse framing. /editor/* is the Solforge AI UI channel (EditorUiApi.cs):
 * its own UiToken and peer check; /mcp refuses that token.
 */
public sealed partial class McpHttpServer : IAsyncDisposable
{
    private const int MaximumBody = 16 << 20;
    private readonly McpDispatcher dispatcher;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop = new();
    private readonly HashSet<string> sessions = [];
    private Task? acceptLoop;

    public McpHttpServer(McpDispatcher dispatcher, int port = 0)
    {
        this.dispatcher = dispatcher;
        listener = new TcpListener(IPAddress.Loopback, port);
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        ViewKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }

    /** The live viewport frames the /viewport pages show; null serves 404. */
    public ViewportFeed? Viewport { get; set; }
    public string ViewKey { get; }
    /** Who is connected (McpSessions.cs); null keeps no record. Each request runs with McpSessionRegistry.Current set. */
    public McpSessionRegistry? Sessions { get; set; }
    public string ViewportUrl => $"http://127.0.0.1:{Port}/viewport?key={ViewKey}";

    public string Token { get; }
    public int Port { get; private set; }
    public string Url => $"http://127.0.0.1:{Port}/mcp";
    public int RequestCount;
    public event Action<string>? Log;

    /** Begins accepting connections. */
    public void Start()
    {
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptLoop = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false); }
            catch (Exception) when (stop.IsCancellationRequested) { return; }
            catch (SocketException) { continue; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var request = await HttpRequest.Read(stream, MaximumBody, stop.Token).ConfigureAwait(false);
            if (request is null) return;
            Interlocked.Increment(ref RequestCount);
            await Respond(stream, request, client.Client.RemoteEndPoint as IPEndPoint, client.Client.LocalEndPoint as IPEndPoint).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        catch (Exception exception) { Log?.Invoke("MCP request failed: " + exception.Message); }
    }

    /** The editor UI channel (EditorUiApi.cs, /editor/*); null answers 404 there. */
    public EditorUiApi? EditorUi { get; set; }

    private async Task Respond(NetworkStream stream, HttpRequest request, IPEndPoint? remote, IPEndPoint? local)
    {
        // DNS rebinding: a browser page may only reach this server from a loopback origin.
        if (request.Header("origin") is { } origin && !IsLoopbackOrigin(origin))
        { await Write(stream, 403, "text/plain", "Origin not allowed").ConfigureAwait(false); return; }
        if (request.Method == "GET" && request.Path.StartsWith("/viewport", StringComparison.Ordinal))
        { await ServeViewport(stream, request).ConfigureAwait(false); return; }
        // SOLFORGE-AI-APP-SPEC §6.2: /editor/* has its own token and peer check; /mcp never accepts that token.
        if (request.Path.StartsWith(EditorUiPaths.Prefix, StringComparison.Ordinal))
        {
            if (EditorUi is { } ui) await ui.ServeAsync(stream, request, remote, local, stop.Token).ConfigureAwait(false);
            else await Write(stream, 404, "text/plain", "Not found").ConfigureAwait(false);
            return;
        }
        if (EditorUi is { } editorUi && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.Header("authorization") ?? ""),
                Encoding.UTF8.GetBytes("Bearer " + editorUi.UiToken)))
        { await Write(stream, 401, "text/plain", "The editor UI token is not an MCP token").ConfigureAwait(false); return; }
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.Header("authorization") ?? ""),
                Encoding.UTF8.GetBytes("Bearer " + Token)))
        { await Write(stream, 401, "text/plain", "Missing or wrong Solforge MCP token").ConfigureAwait(false); return; }
        if (request.Path != "/mcp") { await Write(stream, 404, "text/plain", "Not found").ConfigureAwait(false); return; }
        string? session = request.Header("mcp-session-id");
        switch (request.Method)
        {
            case "DELETE":
                if (session is not null) lock (sessions) sessions.Remove(session);
                Sessions?.Remove(session);
                await Write(stream, 200, "text/plain", "").ConfigureAwait(false);
                return;
            case "GET":
                await Stream(stream).ConfigureAwait(false);
                return;
            case "POST":
                break;
            default:
                await Write(stream, 405, "text/plain", "Method not allowed").ConfigureAwait(false);
                return;
        }
        string body = Encoding.UTF8.GetString(request.Body);
        bool initialize = body.Contains("\"initialize\"", StringComparison.Ordinal);
        int? pid = int.TryParse(request.Header(McpSessionRegistry.PidHeader), out int shimPid) && shimPid > 0 ? shimPid : null;
        // Tools see who called them (get_sessions, the open_map guard, who started a build).
        var caller = McpSessionRegistry.Current = Sessions?.Find(session, pid);
        var (method, callId, toolName) = Peek(body);
        string? reply = Refuse(caller, method, callId, toolName) ?? await dispatcher.HandleAsync(body, stop.Token).ConfigureAwait(false);
        if (method == "tools/call" && toolName is "open_project" or "create_project" or "close_project")
            Sessions?.Rebind(caller, CurrentProject?.Invoke());
        if (method == "tools/list" && caller is { IsWorker: true } && reply is not null) reply = WorkerToolList(reply, caller.IsReadOnlyWorker);
        var headers = new List<(string, string)>();
        if (initialize && reply is not null && reply.Contains("\"result\"", StringComparison.Ordinal))
        {
            session = Guid.NewGuid().ToString("N");
            lock (sessions) sessions.Add(session);
            headers.Add(("Mcp-Session-Id", session));
            var (client, version) = ClientInfo(body);
            if (Sessions?.Register(session, client, version, pid) is { } registered)
                Sessions.Bind(registered, CurrentProject?.Invoke(), request.Header(McpSessionRegistry.WorkerHeader) is "1" or "true",
                    request.Header(McpSessionRegistry.ReadOnlyWorkerHeader) is "1" or "true");
        }
        if (reply is null) { await Write(stream, 202, "text/plain", "", headers).ConfigureAwait(false); return; }
        // A client that accepts only SSE gets the reply as one event; everyone else gets plain JSON.
        string accept = request.Header("accept") ?? "application/json";
        if (accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)
            && !accept.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            await Write(stream, 200, "text/event-stream", $"event: message\ndata: {reply}\n\n", headers).ConfigureAwait(false);
        else
            await Write(stream, 200, "application/json", reply, headers).ConfigureAwait(false);
    }

    private async Task Stream(NetworkStream stream)
    {
        var head = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), stop.Token).ConfigureAwait(false);
        while (!stop.IsCancellationRequested)
        {
            await stream.WriteAsync(": keep-alive\n\n"u8.ToArray(), stop.Token).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(15), stop.Token).ConfigureAwait(false);
        }
    }

    private static readonly (string, string)[] ViewHeaders =
        [("Cache-Control", "no-store"), ("X-Frame-Options", "DENY"), ("Content-Security-Policy", "frame-ancestors 'none'"),
            ("Referrer-Policy", "no-referrer"), ("Cross-Origin-Resource-Policy", "same-origin"), ("X-Content-Type-Options", "nosniff")];

    private async Task ServeViewport(NetworkStream stream, HttpRequest request)
    {
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.QueryValue("key") ?? ""), Encoding.UTF8.GetBytes(ViewKey)))
        { await Write(stream, 401, "text/plain", "Missing or wrong view key (get_viewport_stream gives the URL)", ViewHeaders).ConfigureAwait(false); return; }
        if (Viewport is not { } feed) { await Write(stream, 404, "text/plain", "No viewport", ViewHeaders).ConfigureAwait(false); return; }
        if (feed.Paused) { await Write(stream, 503, "text/plain", "Context publishing is paused in Solforge", ViewHeaders).ConfigureAwait(false); return; }
        feed.Want();
        switch (request.Path)
        {
            case "/viewport":
                string key = Uri.EscapeDataString(ViewKey);
                await Write(stream, 200, "text/html", "<!doctype html><meta charset=utf-8><meta name=viewport content='width=device-width'>" +
                    "<title>Solforge viewport</title><body style='margin:0;background:#0b0c10;display:grid;place-items:center;min-height:100vh'>" +
                    $"<img src='/viewport.mjpeg?key={key}' alt='Solforge viewport' style='max-width:100%;max-height:100vh'></body>",
                    [.. ViewHeaders.Where(header => header.Item1 != "Content-Security-Policy"),
                        ("Content-Security-Policy", "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; frame-ancestors 'none'")])
                    .ConfigureAwait(false);
                return;
            case "/viewport.jpg":
                var frame = feed.Latest ?? await feed.WaitForChangeAsync(0, TimeSpan.FromSeconds(3), stop.Token).ConfigureAwait(false);
                if (frame is null) { await Write(stream, 503, "text/plain", "No frame yet: " + feed.Status, ViewHeaders).ConfigureAwait(false); return; }
                var head = new StringBuilder("HTTP/1.1 200 OK\r\nContent-Type: image/jpeg\r\n");
                head.Append($"Content-Length: {frame.Jpeg.Length}\r\nX-Solforge-Frame: {frame.Id}\r\nConnection: close\r\n");
                foreach (var (name, value) in ViewHeaders) head.Append($"{name}: {value}\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head.Append("\r\n").ToString()), stop.Token).ConfigureAwait(false);
                await stream.WriteAsync(frame.Jpeg, stop.Token).ConfigureAwait(false);
                return;
            case "/viewport.mjpeg":
                await StreamFrames(stream, feed).ConfigureAwait(false);
                return;
            default:
                await Write(stream, 404, "text/plain", "Not found", ViewHeaders).ConfigureAwait(false);
                return;
        }
    }

    /** multipart/x-mixed-replace: every browser shows it in an <img>; a still view resends its frame every 10 s. */
    private async Task StreamFrames(NetworkStream stream, ViewportFeed feed)
    {
        var head = new StringBuilder("HTTP/1.1 200 OK\r\nContent-Type: multipart/x-mixed-replace; boundary=solforgeframe\r\nConnection: close\r\n");
        foreach (var (name, value) in ViewHeaders) head.Append($"{name}: {value}\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.Append("\r\n").ToString()), stop.Token).ConfigureAwait(false);
        feed.BeginStream();
        try
        {
            long last = 0;
            while (!stop.IsCancellationRequested && !feed.Paused)
            {
                var frame = await feed.WaitForChangeAsync(last, TimeSpan.FromSeconds(10), stop.Token).ConfigureAwait(false) ?? feed.Latest;
                if (frame is null) continue;
                last = frame.Id;
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    $"--solforgeframe\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Jpeg.Length}\r\n\r\n"), stop.Token).ConfigureAwait(false);
                await stream.WriteAsync(frame.Jpeg, stop.Token).ConfigureAwait(false);
                await stream.WriteAsync("\r\n"u8.ToArray(), stop.Token).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(1.0 / feed.Fps), stop.Token).ConfigureAwait(false);
            }
        }
        finally { feed.EndStream(); }
    }

    /** UI-UE5 §5.6: the editor's open project, read per request so a project switch is noticed at once. */
    public Func<string?>? CurrentProject { get; set; }

    /** Tools that always answer, whichever project the session was opened for. */
    private static readonly HashSet<string> ProjectFreeTools = new(StringComparer.Ordinal)
        { "get_sessions", "get_editor_state", "get_context", McpJobs.ToolName, "open_project", "create_project", "close_project", "list_ui_controls" };

    /** method, id (raw JSON) and tools/call name of one JSON-RPC message; nulls when it is not one. */
    private static (string? Method, string? Id, string? Tool) Peek(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject message) return (null, null, null);
            return (message["method"]?.ToString(), message["id"]?.ToJsonString(), message["params"]?["name"]?.ToString());
        }
        catch (JsonException) { return (null, null, null); }
    }

    /**
     * A refusal reply instead of dispatching: a worker session calling a tool WorkerSessionPolicy does not allow (a tool
     * error), or any session calling after the editor switched projects (JSON-RPC error ProjectChangedCode). Null to dispatch.
     */
    private string? Refuse(McpSession? caller, string? method, string? id, string? tool)
    {
        if (method != "tools/call" || tool is null || id is null || caller is null) return null;
        if (caller.IsWorker)
        {
            var target = dispatcher.Tools.FirstOrDefault(item => item.Name == tool);
            if (WorkerSessionPolicy.Refusal(tool, target?.RequiresApproval ?? true, caller.IsReadOnlyWorker) is { } refusal)
                return new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id), ["result"] = McpToolResult.Error(refusal).ToJson()
                }.ToJsonString(McpJson.Compact);
        }
        if (ProjectFreeTools.Contains(tool) || Sessions?.ProjectProblem(caller, CurrentProject?.Invoke()) is not { } problem) return null;
        return new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id),
            ["error"] = new JsonObject { ["code"] = McpSessionRegistry.ProjectChangedCode, ["message"] = problem }
        }.ToJsonString(McpJson.Compact);
    }

    /** A worker's tools/list without the tools it may not call. */
    private string WorkerToolList(string reply, bool readOnly)
    {
        try
        {
            if (JsonNode.Parse(reply) is not JsonObject message || message["result"]?["tools"] is not JsonArray tools) return reply;
            foreach (var node in tools.OfType<JsonObject>().ToArray())
            {
                string name = node["name"]?.ToString() ?? "";
                bool approval = dispatcher.Tools.FirstOrDefault(tool => tool.Name == name)?.RequiresApproval ?? true;
                if (!WorkerSessionPolicy.Allows(name, approval, readOnly)) tools.Remove(node);
            }
            return message.ToJsonString(McpJson.Compact);
        }
        catch (JsonException) { return reply; }
    }

    /** initialize params.clientInfo name and version; nulls when absent or not JSON. */
    private static (string? Name, string? Version) ClientInfo(string body)
    {
        try
        {
            var info = JsonNode.Parse(body)?["params"]?["clientInfo"];
            return (info?["name"]?.ToString(), info?["version"]?.ToString());
        }
        catch (JsonException) { return (null, null); }
    }

    private static bool IsLoopbackOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host == "localhost");

    internal static async Task Write(NetworkStream stream, int status, string type, string body, IReadOnlyList<(string, string)>? headers = null)
    {
        byte[] content = Encoding.UTF8.GetBytes(body);
        var head = new StringBuilder($"HTTP/1.1 {status} {Reason(status)}\r\n");
        head.Append($"Content-Type: {type}; charset=utf-8\r\nContent-Length: {content.Length}\r\nConnection: close\r\n");
        if (headers is not null) foreach (var (name, value) in headers) head.Append($"{name}: {value}\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString())).ConfigureAwait(false);
        await stream.WriteAsync(content).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK", 202 => "Accepted", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found",
        405 => "Method Not Allowed", 409 => "Conflict", 413 => "Payload Too Large", 422 => "Unprocessable Entity",
        429 => "Too Many Requests", 503 => "Service Unavailable", _ => "Error"
    };

    /** Cancels the accept loop and closes the listener. */
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        if (acceptLoop is not null) try { await acceptLoop.ConfigureAwait(false); } catch (Exception) { }
        stop.Dispose();
    }
}

/** The request line, headers and Content-Length body of one HTTP/1.1 request. */
internal sealed class HttpRequest
{
    public string Method { get; private init; } = "";
    public string Path { get; private init; } = "";
    public string Query { get; private init; } = "";

    /** One query string value, percent-decoded; null when absent. */
    public string? QueryValue(string name)
    {
        foreach (string pair in Query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            string key = Uri.UnescapeDataString((equals < 0 ? pair : pair[..equals]).Replace('+', ' '));
            if (key == name) return equals < 0 ? "" : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
        }
        return null;
    }
    public byte[] Body { get; private set; } = [];
    private readonly Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);

    /** Request header by name, or null. */
    public string? Header(string name) => headers.GetValueOrDefault(name);

    /** Parses one HTTP request from the stream, refusing bodies above maximumBody. */
    public static async Task<HttpRequest?> Read(Stream stream, int maximumBody, CancellationToken cancel)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int read = await stream.ReadAsync(chunk, cancel).ConfigureAwait(false);
            if (read == 0) return null;
            buffer.Write(chunk, 0, read);
            headerEnd = IndexOf(buffer.GetBuffer(), (int)buffer.Length, "\r\n\r\n"u8);
            if (headerEnd < 0 && buffer.Length > 64 * 1024) return null;
        }
        string head = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, headerEnd);
        string[] lines = head.Split("\r\n");
        string[] start = lines[0].Split(' ');
        if (start.Length < 2) return null;
        string target = start[1];
        int query = target.IndexOf('?');
        var request = new HttpRequest { Method = start[0].ToUpperInvariant(), Path = query < 0 ? target : target[..query],
            Query = query < 0 ? "" : target[(query + 1)..] };
        foreach (string line in lines.Skip(1))
            if (line.IndexOf(':') is var colon and > 0) request.headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        int length = int.TryParse(request.Header("content-length"), out int parsed) ? parsed : 0;
        if (length < 0 || length > maximumBody) return null;
        var body = new byte[length];
        int have = (int)buffer.Length - (headerEnd + 4);
        Array.Copy(buffer.GetBuffer(), headerEnd + 4, body, 0, Math.Min(have, length));
        while (have < length)
        {
            int read = await stream.ReadAsync(body.AsMemory(have, length - have), cancel).ConfigureAwait(false);
            if (read == 0) return null;
            have += read;
        }
        request.Body = body;
        return request;
    }

    private static int IndexOf(byte[] data, int length, ReadOnlySpan<byte> pattern) =>
        data.AsSpan(0, length).IndexOf(pattern);
}
