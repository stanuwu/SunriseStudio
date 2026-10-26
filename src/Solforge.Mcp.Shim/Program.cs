// solforge-mcp: a stdio MCP server for clients that only speak stdio. Each line on stdin is one JSON-RPC message.
// --project forwards it to the Solforge editor that has the project open (found through the discovery file in
// %LOCALAPPDATA%\Solforge\mcp) and, with no editor running, answers from the in-process headless host (UI-UE5 §4.4.5):
// the same tool names, with the editor-only ones saying "needs the Solforge editor". It re-checks discovery on every
// message (cached 2 s, plus a 5 s background check) and attaches to an editor that starts later.
// --sol serves the aggregate (SolAggregator): Solforge (editor or headless) plus sol-testkit, tiger-mcp and delegates.
// --from-env is --sol with the project from SOLFORGE_NEXT_PROJECT (solforge-next's overlay row); it never fails on a
// missing variable. --worker marks a model-workflow worker session (WorkerSessionPolicy). Logs go to stderr. With
// --context it prints the live context as a few plain lines and exits (the Claude Code prompt hook).
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Solforge.Mcp;
using Solforge.Mcp.Headless;
using Solforge.Mcp.Shim;

string? project = null, upstreams = null;
bool context = false, gemini = false, clinePass = false, sol = false, fromEnv = false, worker = false;
for (int i = 0; i < args.Length; i++)
    if (args[i] == "--project" && i + 1 < args.Length) project = args[++i];
    else if (args[i] == "--upstreams" && i + 1 < args.Length) upstreams = args[++i];
    else if (args[i] == "--pipe" && i + 1 < args.Length) i++; // solforge-next's worker argument; the shim finds the editor through discovery
    else if (args[i] == "--context") context = true;
    else if (args[i] == "--gemini") gemini = true;
    else if (args[i] == "--clinepass") clinePass = true;
    else if (args[i] == "--sol") sol = true;
    else if (args[i] == "--from-env") fromEnv = true;
    else if (args[i] == "--worker") worker = true;
    else if (args[i] is "--help" or "-h")
    {
        Console.Error.WriteLine("solforge-mcp [--sol [--upstreams <file>]] [--project <folder>] [--from-env] [--worker] [--context] [--gemini] [--clinepass]");
        Console.Error.WriteLine("  --sol serves the one aggregated `sol` MCP server (tools, resources, templates and prompts from Solforge, sol-testkit, tiger-mcp and delegates).");
        Console.Error.WriteLine("        Solforge's tools come from the running editor, or from the standalone headless host when no editor runs.");
        Console.Error.WriteLine("  --project serves Solforge's tools for that project (editor when running, headless otherwise).");
        Console.Error.WriteLine("  --from-env is --sol with the project from SOLFORGE_NEXT_PROJECT (unset: no project); it never fails on a missing variable.");
        Console.Error.WriteLine("  --worker marks a model-workflow worker session: reads and reviewed project edits only.");
        Console.Error.WriteLine("  --context prints its live context as plain text and exits.");
        Console.Error.WriteLine("  --gemini serves only ask_gemini (runs the Antigravity CLI, agy -p, on your own agy login).");
        Console.Error.WriteLine("  --clinepass serves only ask_clinepass (runs the Cline CLI, cline -P cline-pass, on your own Cline login).");
        return 0;
    }
if (fromEnv)
{
    // SOLFORGE_NEXT_PIPE is solforge-next's editor pipe; this editor is found through discovery instead, so it is ignored.
    string? fromVariable = Environment.GetEnvironmentVariable("SOLFORGE_NEXT_PROJECT");
    if (project is null && fromVariable is { Length: > 0 })
    {
        try { if (Directory.Exists(fromVariable)) project = Path.GetFullPath(fromVariable); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { }
        if (project is null) Console.Error.WriteLine("solforge-mcp: SOLFORGE_NEXT_PROJECT is not a folder; serving with no project.");
    }
    sol = true;
}
if (context) return await PrintContext(project);
if (sol) return await SolAggregator.Serve(project, upstreams, worker);
if (gemini || clinePass)
{
    // Works without Solforge running, so any Claude Code session can add it (claude mcp add --scope user).
    var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
    await (gemini ? AgyDelegate.Serve(reader, output) : ClineDelegate.Serve(reader, output));
    return 0;
}

string? explicitProject = string.IsNullOrWhiteSpace(project) ? null : Path.GetFullPath(project);
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
var writeGate = new SemaphoreSlim(1, 1);
var attachGate = new SemaphoreSlim(1, 1);
EditorLink? editor = null;
string? initializeLine = null, lastEditorProject = null, routeKey = null;
bool clientInitialized = false;
(string? Project, McpDispatcher Dispatcher)? headless = null;
Task initialized = Task.CompletedTask;
var pending = new List<Task>();
using var stopChecks = new CancellationTokenSource();
// UI-UE5 §4.4.3: a background check every 5 s, so a client hears list_changed when the editor starts or stops.
var checks = Task.Run(async () =>
{
    try
    {
        while (!stopChecks.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stopChecks.Token);
            await Route();
        }
    }
    catch (OperationCanceledException) { }
});
using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
while (await input.ReadLineAsync() is { } line)
{
    if (line.Length == 0) continue;
    bool initialize = line.Contains("\"initialize\"", StringComparison.Ordinal);
    var previous = initialized;
    var work = Task.Run(async () =>
    {
        if (!initialize) await previous;
        await Handle(line, initialize);
    });
    if (initialize) initialized = work;
    pending.Add(work);
    pending.RemoveAll(task => task.IsCompleted);
}
await Task.WhenAll(pending);
stopChecks.Cancel();
try { await checks; } catch (OperationCanceledException) { }
return 0;

/** Handles one stdin line: forwarded to the editor when one runs, else answered by the headless host. */
async Task Handle(string message, bool initialize)
{
    string? id = null, method = null;
    try
    {
        using var document = JsonDocument.Parse(message);
        if (document.RootElement.TryGetProperty("id", out var idElement)) id = idElement.GetRawText();
        if (document.RootElement.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String) method = methodElement.GetString();
    }
    catch (JsonException) { }
    if (initialize && method == "initialize") initializeLine = message;
    if (method == "notifications/initialized") clientInitialized = true;
    var link = await Route();
    if (link is not null)
    {
        try
        {
            IReadOnlyList<string> replies;
            if (method == "initialize")
            {
                replies = await link.ForwardAsync(message);
                if (replies.Any(reply => reply.Contains("\"result\"", StringComparison.Ordinal)))
                {
                    JsonObject? parameters = null;
                    try { parameters = JsonNode.Parse(message)?["params"] as JsonObject; }
                    catch (JsonException) { }
                    link.MarkInitialized(parameters);
                }
                // Live attach and detach: the tool list may change during the session.
                replies = [.. replies.Select(ListChanged)];
            }
            else
            {
                await EnsureEditorSession(link);
                replies = await link.ForwardAsync(message);
            }
            foreach (string reply in replies) await Write(reply);
            return;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            Console.Error.WriteLine("solforge-mcp: the Solforge editor stopped answering, serving headless: " + exception.Message);
            McpDiscovery.Invalidate();
            await attachGate.WaitAsync();
            try { if (ReferenceEquals(editor, link)) editor = null; }
            finally { attachGate.Release(); }
        }
    }
    string? answer = await Headless().HandleAsync(message, CancellationToken.None);
    if (answer is null) return;
    await Write(method == "initialize" ? ListChanged(answer) : answer);
}

/** Re-runs discovery (cached 2 s) and attaches to or drops the editor; a route change notifies the client. */
async Task<EditorLink?> Route()
{
    var entry = McpDiscovery.FindCached(explicitProject, TimeSpan.FromSeconds(2));
    string key = entry is null ? "headless" : entry.Pid + "|" + entry.Url;
    bool changed;
    EditorLink? current;
    await attachGate.WaitAsync();
    try
    {
        if (entry is null) editor = null;
        else if (editor?.Key != key) { editor = new EditorLink(http, entry, worker); lastEditorProject = entry.Project ?? lastEditorProject; }
        changed = routeKey is not null && routeKey != key;
        routeKey = key;
        current = editor;
    }
    finally { attachGate.Release(); }
    if (changed && clientInitialized)
    {
        Console.Error.WriteLine(key == "headless" ? "solforge-mcp: Solforge editor gone, serving headless." : "solforge-mcp: attached to the Solforge editor.");
        await Write("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/tools/list_changed\"}");
    }
    return current;
}

/** An editor found after the client's initialize (or after "project changed") gets that initialize replayed first. */
async Task EnsureEditorSession(EditorLink link)
{
    if (!link.NeedsInitialize) return;
    JsonObject? parameters = null;
    if (initializeLine is not null)
        try { parameters = JsonNode.Parse(initializeLine)?["params"] as JsonObject; }
        catch (JsonException) { }
    await link.InitializeAsync(parameters);
}

/** The headless dispatcher for the project (or the last attached editor's project). */
McpDispatcher Headless()
{
    string? wanted = explicitProject ?? lastEditorProject;
    if (headless is { } current && string.Equals(current.Project, wanted, StringComparison.OrdinalIgnoreCase)) return current.Dispatcher;
    var created = HeadlessSolforgeHost.CreateDispatcher(new HeadlessSolforgeHost(wanted),
        typeof(SolforgeRoute).Assembly.GetName().Version?.ToString(3) ?? "0", worker);
    headless = (wanted, created);
    return created;
}

/** An initialize reply advertising tools.listChanged (the route can change after it). */
static string ListChanged(string reply)
{
    try
    {
        if (JsonNode.Parse(reply) is JsonObject message && message["result"]?["capabilities"] is JsonObject capabilities)
        {
            capabilities["tools"] = new JsonObject { ["listChanged"] = true };
            return message.ToJsonString();
        }
    }
    catch (JsonException) { }
    return reply;
}

/** Writes one reply line to stdout. */
async Task Write(string raw)
{
    await writeGate.WaitAsync();
    try { await stdout.WriteLineAsync(raw); }
    finally { writeGate.Release(); }
}

/** No Solforge, paused context or any failure prints nothing: the hook must never block a prompt. */
static async Task<int> PrintContext(string? project)
{
    try
    {
        if (McpDiscovery.Find(project) is not { } server) return 0;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var request = new HttpRequestMessage(HttpMethod.Post, server.Url)
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"get_context\",\"arguments\":{}}}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return 0;
        var result = JsonNode.Parse(await response.Content.ReadAsStringAsync())?["result"];
        if (result?["isError"]?.GetValueKind() == JsonValueKind.True || result?["content"]?[0]?["text"]?.GetValue<string>() is not { } text) return 0;
        string summary = ContextSummary.Format(JsonNode.Parse(text));
        if (summary.Length == 0) return 0;
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
        await stdout.WriteAsync(summary);
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException
        or InvalidOperationException or IOException) { }
    return 0;
}
