/**
 * File: The single `sol` stdio MCP server. It starts the sol-testkit, tiger-mcp and optional delegate upstreams written by
 * SolMcpServers.WriteUpstreams, merges their tools, resources, resource templates and prompts, and forwards every
 * call/read back to the server that owns it. The other servers' names are prefixed (testkit_, tiger_ and so on) so one
 * flattened namespace cannot collide. Resource URIs are wrapped as sol://<server>/<base64url(original)>; template
 * variables survive the wrapper.
 *
 * UI-UE5 §4.4 (standalone MCP): the `solforge` upstream is no longer a child process that is "initialized once, else
 * unavailable". SolforgeRoute serves it in-process: on every tools/list and tools/call (discovery cached for 2 s, plus a
 * background check every 5 s) it looks for a running editor through McpDiscovery and talks to it over HTTP; with no editor
 * it answers from the headless host (Solforge.Mcp.Headless). The listed Solforge tools are the union of the live editor
 * catalogue, the cached one (tools-cache.json) and the headless ones, so names stay stable either way; tools.listChanged
 * is true and notifications/tools/list_changed goes out when the route changes. Solforge's own tool names stay unchanged.
 */
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Solforge.Mcp;
using Solforge.Mcp.Headless;

namespace Solforge.Mcp.Shim;

/** The one client-facing MCP server: sol. */
public static class SolAggregator
{
    private const string ServerName = "sol";
    private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(20);
    /** A failed upstream (other than solforge) is tried once more after this long. */
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    /** Reads the aggregate's upstream list and serves one MCP session on stdin/stdout. */
    public static Task<int> Serve(string? project, string? upstreamFile) => Serve(project, upstreamFile, worker: false);

    /**
     * Serves the aggregate. @param project the project the session is for (null: whichever project the running editor has
     * open); @param worker a model-workflow worker session (WorkerSessionPolicy applies to the Solforge tools).
     */
    public static async Task<int> Serve(string? project, string? upstreamFile, bool worker)
    {
        string? explicitProject = string.IsNullOrWhiteSpace(project) ? null : Path.GetFullPath(project);
        // Child upstreams start in the project folder, or where the client started us.
        string workingDirectory = explicitProject is not null && Directory.Exists(explicitProject) ? explicitProject : Environment.CurrentDirectory;
        var configs = LoadUpstreams(explicitProject, upstreamFile);
        SolforgeRoute? solforge = null;
        var upstreams = new List<Upstream>();
        foreach (var config in configs)
        {
            if (config.Name.Equals(SolMcpServers.Solforge, StringComparison.OrdinalIgnoreCase))
                solforge ??= new SolforgeRoute(explicitProject ?? ProjectArgument(config.Args), worker);
            else upstreams.Add(new Upstream(config, workingDirectory));
        }
        bool started = false, initialized = false;
        var sync = new object();
        var route = new Dictionary<string, (Upstream Upstream, string Original)>(StringComparer.Ordinal);
        var resourceRoutes = new Dictionary<string, (Upstream Upstream, string Original)>(StringComparer.Ordinal);
        var templateRoutes = new List<TemplateRoute>();
        var promptRoutes = new Dictionary<string, (Upstream Upstream, string Original)>(StringComparer.Ordinal);
        var tools = new JsonArray();
        var resources = new JsonArray();
        var resourceTemplates = new JsonArray();
        var prompts = new JsonArray();
        bool resourcesAvailable = false, promptsAvailable = false;
        string instructions = "";

        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var writeGate = new SemaphoreSlim(1, 1);
        async Task Send(JsonObject message)
        {
            await writeGate.WaitAsync().ConfigureAwait(false);
            try { await Write(output, message).ConfigureAwait(false); }
            finally { writeGate.Release(); }
        }
        // Tool names may change after initialize (an editor attached, a retried upstream came up): tell the client.
        async Task ToolsChanged()
        {
            if (!Volatile.Read(ref initialized)) return;
            try { await Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/tools/list_changed" }).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
        }
        if (solforge is not null) solforge.Changed += () => _ = ToolsChanged();

        /** Loads one ready upstream's metadata into the merged lists. */
        async Task LoadAsync(Upstream upstream)
        {
            var upstreamTools = new JsonArray();
            var upstreamResources = new JsonArray();
            var upstreamTemplates = new JsonArray();
            var upstreamPrompts = new JsonArray();
            var toolRoutes = new Dictionary<string, (Upstream Upstream, string Original)>(StringComparer.Ordinal);
            var resourceRoute = new Dictionary<string, (Upstream Upstream, string Original)>(StringComparer.Ordinal);
            var templateRoute = new List<TemplateRoute>();
            var promptRoute = new Dictionary<string, (Upstream Upstream, string Original)>(StringComparer.Ordinal);
            bool hasResources = upstream.Capabilities?["resources"] is JsonObject, hasPrompts = upstream.Capabilities?["prompts"] is JsonObject;
            try { await LoadToolsAsync(upstream, upstreamTools, toolRoutes).ConfigureAwait(false); }
            catch (Exception exception) { Console.Error.WriteLine($"sol: {upstream.Name} tools/list failed: {exception.Message}"); }
            try { hasResources |= await LoadResourcesAsync(upstream, upstreamResources, resourceRoute).ConfigureAwait(false); }
            catch (Exception exception) { Console.Error.WriteLine($"sol: {upstream.Name} resources/list failed: {exception.Message}"); }
            try { hasResources |= await LoadTemplatesAsync(upstream, upstreamTemplates, templateRoute).ConfigureAwait(false); }
            catch (Exception exception) { Console.Error.WriteLine($"sol: {upstream.Name} resources/templates/list failed: {exception.Message}"); }
            try { hasPrompts |= await LoadPromptsAsync(upstream, upstreamPrompts, promptRoute).ConfigureAwait(false); }
            catch (Exception exception) { Console.Error.WriteLine($"sol: {upstream.Name} prompts/list failed: {exception.Message}"); }
            lock (sync)
            {
                foreach (var tool in upstreamTools.OfType<JsonObject>())
                {
                    string name = tool["name"]!.ToString();
                    var original = toolRoutes[name];
                    if (route.ContainsKey(name)) { name += "_" + route.Count.ToString(System.Globalization.CultureInfo.InvariantCulture); tool["name"] = name; }
                    route[name] = original;
                    tools.Add(tool.DeepClone());
                }
                foreach (var node in upstreamResources) resources.Add(node?.DeepClone());
                foreach (var (key, value) in resourceRoute) resourceRoutes[key] = value;
                foreach (var node in upstreamTemplates) resourceTemplates.Add(node?.DeepClone());
                templateRoutes.AddRange(templateRoute);
                foreach (var node in upstreamPrompts.OfType<JsonObject>())
                {
                    string name = node["name"]!.ToString();
                    var original = promptRoute[name];
                    if (promptRoutes.ContainsKey(name)) { name += "_" + promptRoutes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture); node["name"] = name; }
                    promptRoutes[name] = original;
                    prompts.Add(node.DeepClone());
                }
                resourcesAvailable |= hasResources;
                promptsAvailable |= hasPrompts;
            }
        }

        async Task StartAsync()
        {
            if (started) return;
            started = true;
            var failed = new List<Upstream>();
            await Task.WhenAll(upstreams.Select(async upstream =>
            {
                try { await upstream.InitializeAsync().ConfigureAwait(false); }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"sol: {upstream.Name} unavailable: {exception.Message}");
                    lock (failed) failed.Add(upstream);
                }
            })).ConfigureAwait(false);
            var text = new StringBuilder();
            if (solforge is not null)
            {
                await solforge.StartAsync().ConfigureAwait(false);
                text.Append("## solforge\n").Append(solforge.Instructions.Trim()).Append("\n\n");
            }
            foreach (var upstream in upstreams.Where(upstream => upstream.Ready))
            {
                if (upstream.Instructions is { Length: > 0 } found)
                    text.Append("## ").Append(upstream.Name).Append('\n').Append(found.Trim()).Append("\n\n");
                await LoadAsync(upstream).ConfigureAwait(false);
            }
            instructions = text.ToString().TrimEnd();
            // One retry per failed upstream after 30 s, in the background; the client hears list_changed when it comes up.
            foreach (var upstream in failed)
                _ = Task.Run(async () =>
                {
                    await Task.Delay(RetryDelay).ConfigureAwait(false);
                    var retry = upstream.Fresh();
                    try { await retry.InitializeAsync().ConfigureAwait(false); }
                    catch (Exception exception)
                    {
                        Console.Error.WriteLine($"sol: {retry.Name} still unavailable: {exception.Message}");
                        await retry.DisposeAsync().ConfigureAwait(false);
                        return;
                    }
                    lock (sync) upstreams.Add(retry);
                    await LoadAsync(retry).ConfigureAwait(false);
                    await ToolsChanged().ConfigureAwait(false);
                });
        }

        JsonArray Snapshot(JsonArray list) { lock (sync) return (JsonArray)list.DeepClone(); }

        // One request's reply. Runs on its own task (see the loop below), so a slow tool call never holds up the rest.
        async Task<JsonObject> Handle(JsonObject request, JsonNode? id, string method)
        {
        JsonObject reply;
        switch (method)
        {
            case "initialize":
                solforge?.SetClient(request["params"] as JsonObject);
                await StartAsync().ConfigureAwait(false);
                string asked = request["params"]?["protocolVersion"]?.ToString() is { Length: > 0 } version
                    ? version : "2025-06-18";
                var capabilities = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = true } };
                if (resourcesAvailable)
                    capabilities["resources"] = new JsonObject { ["listChanged"] = false, ["subscribe"] = false };
                if (promptsAvailable) capabilities["prompts"] = new JsonObject { ["listChanged"] = false };
                reply = Success(id, new JsonObject
                {
                    ["protocolVersion"] = asked,
                    ["capabilities"] = capabilities,
                    ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["title"] = "Sol", ["version"] = "1.0" },
                    ["instructions"] = instructions
                });
                break;
            case "ping":
                reply = Success(id, new JsonObject());
                break;
            case "tools/list":
                await StartAsync().ConfigureAwait(false);
                var listed = new JsonArray();
                if (solforge is not null) foreach (var tool in await solforge.ToolsAsync().ConfigureAwait(false)) listed.Add(tool?.DeepClone());
                foreach (var tool in Snapshot(tools)) listed.Add(tool?.DeepClone());
                reply = Success(id, new JsonObject { ["tools"] = listed });
                break;
            case "tools/call":
                await StartAsync().ConfigureAwait(false);
                string? name = request["params"]?["name"]?.ToString();
                (Upstream Upstream, string Original) target;
                bool upstreamTool;
                lock (sync) upstreamTool = name is not null && route.TryGetValue(name, out target);
                if (!upstreamTool || name is null)
                {
                    if (name is not null && solforge is not null && await solforge.OwnsAsync(name).ConfigureAwait(false))
                    {
                        var routed = await solforge.CallAsync(name, request["params"]?["arguments"] as JsonObject ?? new JsonObject()).ConfigureAwait(false);
                        reply = routed["error"] is JsonObject failure
                            ? Error(id, failure["code"]?.GetValue<int>() ?? -32603, failure["message"]?.ToString() ?? "Solforge error")
                            : Success(id, routed["result"] is JsonObject result ? result.DeepClone().AsObject() : new JsonObject());
                    }
                    else reply = Error(id, -32602, "Unknown tool: " + (name ?? ""));
                    break;
                }
                lock (sync) target = route[name];
                try
                {
                    var arguments = request["params"]?["arguments"] as JsonObject ?? new JsonObject();
                    var forwarded = await target.Upstream.RequestAsync("tools/call", new JsonObject
                    {
                        ["name"] = target.Original,
                        ["arguments"] = arguments.DeepClone()
                    }, CallTimeout).ConfigureAwait(false);
                    reply = forwarded["error"] is JsonObject error
                        ? Error(id, error["code"]?.GetValue<int>() ?? -32603, error["message"]?.ToString() ?? "upstream error")
                        : Success(id, forwarded["result"] is JsonObject result ? result.DeepClone().AsObject() : new JsonObject());
                }
                catch (Exception exception)
                {
                    var content = new JsonArray();
                    content.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = exception.Message });
                    reply = Success(id, new JsonObject { ["content"] = content, ["isError"] = true });
                }
                break;
            case "resources/list":
                await StartAsync().ConfigureAwait(false);
                reply = Success(id, new JsonObject { ["resources"] = Snapshot(resources) });
                break;
            case "resources/templates/list":
                await StartAsync().ConfigureAwait(false);
                reply = Success(id, new JsonObject { ["resourceTemplates"] = Snapshot(resourceTemplates) });
                break;
            case "resources/read":
                await StartAsync().ConfigureAwait(false);
                reply = await ForwardResourceAsync(id, request["params"] as JsonObject, resourceRoutes,
                    templateRoutes, "resources/read", sync).ConfigureAwait(false);
                break;
            case "resources/subscribe":
            case "resources/unsubscribe":
                await StartAsync().ConfigureAwait(false);
                reply = await ForwardResourceAsync(id, request["params"] as JsonObject, resourceRoutes,
                    templateRoutes, method, sync).ConfigureAwait(false);
                break;
            case "prompts/list":
                await StartAsync().ConfigureAwait(false);
                reply = Success(id, new JsonObject { ["prompts"] = Snapshot(prompts) });
                break;
            case "prompts/get":
                await StartAsync().ConfigureAwait(false);
                string? promptName = request["params"]?["name"]?.ToString();
                (Upstream Upstream, string Original) promptTarget = default;
                bool knownPrompt;
                lock (sync) knownPrompt = promptName is not null && promptRoutes.TryGetValue(promptName, out promptTarget);
                if (!knownPrompt || promptTarget.Upstream is null)
                {
                    reply = Error(id, -32602, "Unknown prompt: " + (promptName ?? ""));
                    break;
                }
                reply = await ForwardAsync(id, promptTarget.Upstream, "prompts/get", new JsonObject
                {
                    ["name"] = promptTarget.Original,
                    ["arguments"] = request["params"]?["arguments"]?.DeepClone() ?? new JsonObject()
                }).ConfigureAwait(false);
                if (reply["result"] is JsonObject promptResult)
                    RewritePromptResources(promptResult, promptTarget.Upstream);
                break;
            default:
                reply = Error(id, -32601, "Method not found: " + method);
                break;
        }
            return reply;
        }

        var pending = new System.Collections.Concurrent.ConcurrentDictionary<Task, byte>();

        try
        {
            while (await input.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0) continue;
                JsonNode? node;
                try { node = JsonNode.Parse(line); }
                catch (JsonException) { await Send(Error(null, -32700, "Parse error")).ConfigureAwait(false); continue; }
                if (node is not JsonObject request) continue;
                JsonNode? id = request["id"]?.DeepClone();
                string method = request["method"]?.ToString() ?? "";
                if (method == "notifications/initialized") Volatile.Write(ref initialized, true);
                if (id is null) continue;
                // initialize stays in order (its reply must precede everything else); every other request is answered
                // on its own task, so one long call (a delegate model, a build) cannot time out the calls behind it.
                if (method == "initialize")
                {
                    await Send(await Handle(request, id, method).ConfigureAwait(false)).ConfigureAwait(false);
                    Volatile.Write(ref initialized, true);
                    continue;
                }
                Task job = Task.Run(async () =>
                {
                    JsonObject reply;
                    try { reply = await Handle(request, id, method).ConfigureAwait(false); }
                    catch (Exception exception) { reply = Error(id, -32603, exception.Message); }
                    try { await Send(reply).ConfigureAwait(false); }
                    catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
                });
                pending[job] = 0;
                _ = job.ContinueWith(done => pending.TryRemove(done, out _), TaskScheduler.Default);
            }
        }
        finally
        {
            try { await Task.WhenAll(pending.Keys).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception) { }
            Upstream[] owned;
            lock (sync) owned = [.. upstreams];
            foreach (var upstream in owned) await upstream.DisposeAsync().ConfigureAwait(false);
            if (solforge is not null) await solforge.DisposeAsync().ConfigureAwait(false);
        }
        return 0;
    }

    /** The --project value of an upstream's arguments, or null. */
    private static string? ProjectArgument(IReadOnlyList<string> args)
    {
        for (int i = 0; i + 1 < args.Count; i++)
            if (args[i] == "--project" && !string.IsNullOrWhiteSpace(args[i + 1])) return Path.GetFullPath(args[i + 1]);
        return null;
    }

    /** One upstream's static resources, with every URI routed through the aggregate. */
    private static async Task<bool> LoadResourcesAsync(Upstream upstream, JsonArray resources,
        Dictionary<string, (Upstream Upstream, string Original)> routes)
    {
        bool found = false;
        string? cursor = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var parameters = new JsonObject();
            if (cursor is { Length: > 0 }) parameters["cursor"] = cursor;
            var reply = await upstream.RequestAsync("resources/list", parameters, MetadataTimeout).ConfigureAwait(false);
            if (reply["error"] is not null || reply["result"] is not JsonObject result || result["resources"] is not JsonArray list) break;
            foreach (var node in list.OfType<JsonObject>())
            {
                string? original = node["uri"]?.ToString();
                if (string.IsNullOrWhiteSpace(original)) continue;
                string wrapped = WrapResource(upstream.Name, original);
                var resource = node.DeepClone().AsObject();
                resource["uri"] = wrapped;
                if (!upstream.Name.Equals("solforge", StringComparison.OrdinalIgnoreCase))
                    resource["description"] = $"[{upstream.Name}] {resource["description"]?.ToString() ?? ""}".TrimEnd();
                resources.Add((JsonNode)resource);
                routes[wrapped] = (upstream, original);
                found = true;
            }
            cursor = result["nextCursor"]?.ToString();
            if (string.IsNullOrEmpty(cursor) || !seen.Add(cursor)) break;
        }
        return found;
    }

    /** One upstream's resource templates, with wrapper templates that preserve their variables. */
    private static async Task<bool> LoadTemplatesAsync(Upstream upstream, JsonArray templates, List<TemplateRoute> routes)
    {
        bool found = false;
        string? cursor = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var parameters = new JsonObject();
            if (cursor is { Length: > 0 }) parameters["cursor"] = cursor;
            var reply = await upstream.RequestAsync("resources/templates/list", parameters, MetadataTimeout).ConfigureAwait(false);
            if (reply["error"] is not null || reply["result"] is not JsonObject result
                || result["resourceTemplates"] is not JsonArray list) break;
            foreach (var node in list.OfType<JsonObject>())
            {
                string? original = node["uriTemplate"]?.ToString();
                if (string.IsNullOrWhiteSpace(original)) continue;
                string wrapped = WrapTemplate(upstream.Name, original);
                var template = node.DeepClone().AsObject();
                template["uriTemplate"] = wrapped;
                if (!upstream.Name.Equals("solforge", StringComparison.OrdinalIgnoreCase))
                    template["description"] = $"[{upstream.Name}] {template["description"]?.ToString() ?? ""}".TrimEnd();
                templates.Add((JsonNode)template);
                routes.Add(new TemplateRoute(upstream, original, wrapped));
                found = true;
            }
            cursor = result["nextCursor"]?.ToString();
            if (string.IsNullOrEmpty(cursor) || !seen.Add(cursor)) break;
        }
        return found;
    }

    /** One upstream's prompts, namespaced so a prompt name cannot collide. */
    private static async Task<bool> LoadPromptsAsync(Upstream upstream, JsonArray prompts,
        Dictionary<string, (Upstream Upstream, string Original)> routes)
    {
        bool found = false;
        string? cursor = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var parameters = new JsonObject();
            if (cursor is { Length: > 0 }) parameters["cursor"] = cursor;
            var reply = await upstream.RequestAsync("prompts/list", parameters, MetadataTimeout).ConfigureAwait(false);
            if (reply["error"] is not null || reply["result"] is not JsonObject result || result["prompts"] is not JsonArray list) break;
            foreach (var node in list.OfType<JsonObject>())
            {
                string? original = node["name"]?.ToString();
                if (string.IsNullOrWhiteSpace(original)) continue;
                string name = PublicPromptName(upstream.Name, original);
                if (routes.ContainsKey(name)) name += "_" + routes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var prompt = node.DeepClone().AsObject();
                prompt["name"] = name;
                if (!upstream.Name.Equals("solforge", StringComparison.OrdinalIgnoreCase))
                    prompt["description"] = $"[{upstream.Name}] {prompt["description"]?.ToString() ?? ""}".TrimEnd();
                prompts.Add((JsonNode)prompt);
                routes[name] = (upstream, original);
                found = true;
            }
            cursor = result["nextCursor"]?.ToString();
            if (string.IsNullOrEmpty(cursor) || !seen.Add(cursor)) break;
        }
        return found;
    }

    /** One upstream's tools with their public names (prefixed; the delegates keep ask_*). */
    private static async Task LoadToolsAsync(Upstream upstream, JsonArray tools,
        Dictionary<string, (Upstream Upstream, string Original)> route)
    {
        var reply = await upstream.RequestAsync("tools/list", new JsonObject(), CallTimeout).ConfigureAwait(false);
        if (reply["result"]?["tools"] is not JsonArray found) return;
        foreach (var node in found.OfType<JsonObject>())
        {
            string? original = node["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(original)) continue;
            string name = PublicName(upstream.Name, original);
            if (route.ContainsKey(name)) name += "_" + route.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var tool = node.DeepClone().AsObject();
            tool["name"] = name;
            if (!upstream.Name.Equals("solforge", StringComparison.OrdinalIgnoreCase))
                tool["description"] = $"[{upstream.Name}] {tool["description"]?.ToString() ?? ""}".TrimEnd();
            tools.Add((JsonNode)tool);
            route[name] = (upstream, original);
        }
    }

    /** solforge keeps its public names; testkit/tiger and any other aggregate server get a namespace. */
    private static string PublicName(string server, string tool)
    {
        if (server.Equals("solforge", StringComparison.OrdinalIgnoreCase)) return tool;
        if (server is "gemini" or "clinepass" && tool.StartsWith("ask_", StringComparison.Ordinal)) return tool;
        string key = server switch
        {
            "sol-testkit" => "testkit",
            "tiger-mcp" => "tiger",
            _ => new string(server.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_').ToArray()).Trim('_')
        };
        if (key.Length == 0) key = "mcp";
        return key + "_" + tool;
    }

    /** Resource/prompt namespace: Solforge keeps its names, everything else gets the tool-style key. */
    private static string ResourceKey(string server) => server switch
    {
        "sol-testkit" => "testkit",
        "tiger-mcp" => "tiger",
        _ => new string(server.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_').ToArray()).Trim('_') switch
        {
            { Length: > 0 } key => key,
            _ => "mcp"
        }
    };

    /** A stable aggregate URI for one upstream resource. */
    private static string WrapResource(string server, string uri) =>
        $"sol://{ResourceKey(server)}/{Base64Url(uri)}";

    /** A wrapper URI template that keeps the upstream's {variables} usable by the client. */
    private static string WrapTemplate(string server, string template)
    {
        var text = new StringBuilder("sol://").Append(ResourceKey(server)).Append('/');
        int index = 0;
        foreach (Match match in Regex.Matches(template, @"\{[^}]+\}"))
        {
            text.Append(Uri.EscapeDataString(template[index..match.Index]));
            text.Append(match.Value);
            index = match.Index + match.Length;
        }
        return text.Append(Uri.EscapeDataString(template[index..])).ToString();
    }

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /** Reads one aggregate resource, resolving both static URIs and expanded templates. */
    private static async Task<JsonObject> ForwardResourceAsync(JsonNode? id, JsonObject? parameters,
        Dictionary<string, (Upstream Upstream, string Original)> routes, List<TemplateRoute> templates, string method, object sync)
    {
        string? uri = parameters?["uri"]?.ToString();
        if (string.IsNullOrWhiteSpace(uri)) return Error(id, -32602, "uri is required.");
        Upstream? upstream = null;
        string? original = null;
        lock (sync)
        {
            if (routes.TryGetValue(uri, out var direct))
            {
                upstream = direct.Upstream;
                original = direct.Original;
            }
            else
            {
                foreach (var template in templates)
                    if (template.TryExpand(uri, out string expanded))
                    {
                        upstream = template.Upstream;
                        original = expanded;
                        break;
                    }
            }
        }
        if (upstream is null || original is null) return Error(id, -32602, "Unknown resource URI: " + uri);
        var reply = await ForwardAsync(id, upstream, method, new JsonObject { ["uri"] = original }).ConfigureAwait(false);
        if (method == "resources/read" && reply["result"] is JsonObject result && result["contents"] is JsonArray contents)
            foreach (var item in contents.OfType<JsonObject>()) item["uri"] = uri;
        return reply;
    }

    /** Forwards one request and converts its result/error into that request's reply. */
    private static async Task<JsonObject> ForwardAsync(JsonNode? id, Upstream upstream, string method, JsonObject parameters)
    {
        try
        {
            var reply = await upstream.RequestAsync(method, parameters, CallTimeout).ConfigureAwait(false);
            if (reply["error"] is JsonObject error)
                return Error(id, error["code"]?.GetValue<int>() ?? -32603, error["message"]?.ToString() ?? "upstream error");
            return Success(id, reply["result"] is JsonObject result ? result.DeepClone().AsObject() : new JsonObject());
        }
        catch (Exception exception)
        {
            return Error(id, -32603, exception.Message);
        }
    }

    /** A namespaced prompt name; Solforge prompts keep their public names. */
    private static string PublicPromptName(string server, string name) =>
        server.Equals("solforge", StringComparison.OrdinalIgnoreCase) ? name : ResourceKey(server) + "_" + name;

    /** Prompt messages may embed resource links; keep those readable through the aggregate too. */
    private static void RewritePromptResources(JsonObject result, Upstream upstream)
    {
        if (result["messages"] is not JsonArray messages) return;
        foreach (var message in messages.OfType<JsonObject>()) RewriteContent(message["content"], upstream);
    }

    private static void RewriteContent(JsonNode? node, Upstream upstream)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array) RewriteContent(item, upstream);
            return;
        }
        if (node is not JsonObject content) return;
        if (content["resource"] is JsonObject resource
            && resource["uri"] is JsonValue value && value.TryGetValue<string>(out string? uri)
            && !string.IsNullOrWhiteSpace(uri))
            resource["uri"] = WrapResource(upstream.Name, uri);
        if (content["type"]?.ToString() == "resource_link"
            && content["uri"] is JsonValue link && link.TryGetValue<string>(out string? linkUri)
            && !string.IsNullOrWhiteSpace(linkUri))
            content["uri"] = WrapResource(upstream.Name, linkUri);
        if (content["content"] is JsonNode nested) RewriteContent(nested, upstream);
    }

    /**
     * Upstreams from the given list; else the project's own list (SolMcpServers.UpstreamFile), else the per-user default
     * (sol-upstreams-default.json, UI-UE5 §4.4.7); else solforge alone.
     */
    private static List<UpstreamConfig> LoadUpstreams(string? project, string? upstreamFile)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(upstreamFile)) candidates.Add(upstreamFile);
        if (project is not null) candidates.Add(SolMcpServers.UpstreamFile(project));
        candidates.Add(SolMcpServers.DefaultUpstreamFile());
        var configs = new List<UpstreamConfig>();
        foreach (string file in candidates)
        {
            configs = ReadUpstreams(file);
            if (configs.Count > 0) break;
        }
        if (configs.Count == 0)
            configs.Add(new(SolMcpServers.Solforge, Environment.ProcessPath ?? "solforge-mcp.exe", project is null ? [] : ["--project", project], new JsonObject()));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return configs.Where(config => seen.Add(config.Name)).ToList();
    }

    /** One upstream list file; empty when it is missing, unreadable or lists nothing. */
    private static List<UpstreamConfig> ReadUpstreams(string upstreamFile)
    {
        var configs = new List<UpstreamConfig>();
        try
        {
            if (File.Exists(upstreamFile) && JsonNode.Parse(File.ReadAllText(upstreamFile))?["servers"] is JsonArray servers)
                foreach (var node in servers.OfType<JsonObject>())
                {
                    string? name = node["name"]?.ToString();
                    string? command = node["command"]?.ToString();
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(command)) continue;
                    if (name.Equals(ServerName, StringComparison.OrdinalIgnoreCase)) continue;
                    var args = node["args"] is JsonArray array ? array.Select(arg => arg?.ToString() ?? "").ToList() : [];
                    var env = node["env"] as JsonObject ?? new JsonObject();
                    configs.Add(new(name, command, args, env.DeepClone().AsObject()));
                }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"sol: could not read upstreams {upstreamFile}: {exception.Message}");
        }
        return configs;
    }

    /** One spawned upstream stdio server with serialized requests and its own lifetime. */
    private sealed class Upstream : IAsyncDisposable
    {
        private readonly UpstreamConfig config;
        private readonly string project;
        private readonly SemaphoreSlim gate = new(1, 1);
        private Process? process;
        private StreamWriter? input;
        private StreamReader? output;
        private int nextId;

        public Upstream(UpstreamConfig config, string project)
        {
            this.config = config;
            this.project = project;
        }

        public string Name => config.Name;
        public bool Ready { get; private set; }
        public string? Instructions { get; private set; }
        public JsonObject? Capabilities { get; private set; }

        /** A new, not yet started copy of this upstream (the one retry after a failed start). */
        public Upstream Fresh() => new(config, project);

        public async Task InitializeAsync()
        {
            var info = new ProcessStartInfo(config.Command)
            {
                WorkingDirectory = project, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
            };
            foreach (string arg in config.Args) info.ArgumentList.Add(arg);
            foreach (var (key, value) in config.Env)
            {
                if (value is null) info.Environment.Remove(key);
                else info.Environment[key] = value.ToString();
            }
            info.Environment["PATH"] = EngineCommands.FreshPath();
            try { process = Process.Start(info) ?? throw new InvalidOperationException("no process"); }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            { throw new InvalidOperationException("could not start", exception); }
            input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            output = new StreamReader(process.StandardOutput.BaseStream, new UTF8Encoding(false));
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                        Console.Error.WriteLine($"sol[{config.Name}] {line}");
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
            });
            var reply = await RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "sol", ["version"] = "1.0" }
            }, InitializeTimeout).ConfigureAwait(false);
            if (reply["error"] is JsonObject error)
                throw new InvalidOperationException(error["message"]?.ToString() ?? "initialize failed");
            if (reply["result"] is not JsonObject result)
                throw new InvalidOperationException("initialize had no result");
            Instructions = result["instructions"]?.ToString();
            Capabilities = result["capabilities"] as JsonObject;
            Ready = true;
            await NotifyAsync("notifications/initialized").ConfigureAwait(false);
        }

        /** Sends one request and returns its JSON-RPC reply (result or error). */
        public async Task<JsonObject> RequestAsync(string method, JsonObject parameters, TimeSpan timeout)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (process is null || input is null || output is null) throw new InvalidOperationException("upstream is not running");
                if (process.HasExited) throw new InvalidOperationException("upstream exited");
                int id = ++nextId;
                await input.WriteLineAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters.DeepClone()
                }.ToJsonString()).ConfigureAwait(false);
                await input.FlushAsync().ConfigureAwait(false);
                using var limit = new CancellationTokenSource(timeout);
                while (true)
                {
                    string? line = await output.ReadLineAsync(limit.Token).ConfigureAwait(false);
                    if (line is null) throw new InvalidOperationException("upstream closed its output");
                    if (line.Length == 0) continue;
                    JsonNode? node;
                    try { node = JsonNode.Parse(line); }
                    catch (JsonException) { continue; }
                    if (node is not JsonObject reply) continue;
                    if (!int.TryParse(reply["id"]?.ToString(), out int replyId) || replyId != id) continue;
                    return reply;
                }
            }
            finally { gate.Release(); }
        }

        private async Task NotifyAsync(string method)
        {
            if (input is null) return;
            await input.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method }.ToJsonString()).ConfigureAwait(false);
            await input.FlushAsync().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            // Only the process this upstream started is ever killed.
            try { if (process is { HasExited: false }) process.Kill(true); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            input?.Dispose();
            output?.Dispose();
            process?.Dispose();
            gate.Dispose();
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    /** One wrapped resource template, able to expand a client URI back to the upstream template. */
    private sealed class TemplateRoute
    {
        private readonly string original;
        private readonly Regex pattern;
        private readonly IReadOnlyList<string> variables;

        public TemplateRoute(Upstream upstream, string original, string wrapped)
        {
            Upstream = upstream;
            this.original = original;
            (pattern, variables) = BuildPattern(wrapped);
        }

        public Upstream Upstream { get; }

        /** Replaces the wrapper's variable captures in the original template. */
        public bool TryExpand(string uri, out string expanded)
        {
            var match = pattern.Match(uri);
            if (!match.Success) { expanded = ""; return false; }
            expanded = original;
            for (int i = 0; i < variables.Count; i++)
            {
                string value = match.Groups["v" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)].Value;
                try { value = Uri.UnescapeDataString(value); }
                catch (Exception exception) when (exception is UriFormatException or ArgumentException) { }
                expanded = expanded.Replace("{" + variables[i] + "}", value);
            }
            return true;
        }

        private static (Regex Pattern, IReadOnlyList<string> Variables) BuildPattern(string template)
        {
            var variables = new List<string>();
            var text = new StringBuilder("^");
            int index = 0;
            foreach (Match match in Regex.Matches(template, @"\{[^}]+\}"))
            {
                text.Append(Regex.Escape(template[index..match.Index]));
                variables.Add(match.Value[1..^1]);
                text.Append("(?<v").Append((variables.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(">.*?)");
                index = match.Index + match.Length;
            }
            text.Append(Regex.Escape(template[index..])).Append('$');
            return (new Regex(text.ToString(), RegexOptions.CultureInvariant), variables);
        }
    }

    private sealed record UpstreamConfig(string Name, string Command, IReadOnlyList<string> Args, JsonObject Env);

    private static async Task Write(TextWriter output, JsonObject reply)
    {
        await output.WriteLineAsync(reply.ToJsonString()).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }

    private static JsonObject Success(JsonNode? id, JsonObject result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}

/**
 * One HTTP MCP session with a running editor (streamable HTTP, Authorization: Bearer from its discovery file). Used by
 * SolforgeRoute (the aggregate) and by `solforge-mcp --project` (Program.cs), which forwards client lines verbatim.
 */
internal sealed class EditorLink(HttpClient http, McpDiscoveryEntry entry, bool worker)
{
    private string? session;
    private int nextId;
    private JsonObject? initializeParameters;

    public McpDiscoveryEntry Entry { get; } = entry;
    /** The route key: the same pid and URL is the same editor. */
    public string Key => Entry.Pid + "|" + Entry.Url;
    /** True until initialize succeeded, and again after the editor refused a call with "project changed". */
    public bool NeedsInitialize { get; private set; } = true;
    public string? Instructions { get; private set; }
    /** The editor's own tools/list entries (loaded at attach). */
    public JsonArray Tools { get; private set; } = [];

    /** Sends initialize (with the client's own clientInfo, so get_sessions names it) and notifications/initialized. */
    public async Task InitializeAsync(JsonObject? parameters)
    {
        initializeParameters = parameters?.DeepClone().AsObject() ?? new JsonObject
        {
            ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "sol", ["version"] = "1.0" }
        };
        session = null;
        var reply = await RequestAsync("initialize", initializeParameters.DeepClone().AsObject()).ConfigureAwait(false);
        if (reply["result"] is not JsonObject result)
            throw new HttpRequestException(reply["error"]?["message"]?.ToString() ?? "the editor did not accept initialize");
        Instructions = result["instructions"]?.ToString();
        await PostAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }.ToJsonString()).ConfigureAwait(false);
        NeedsInitialize = false;
    }

    /** The client's own initialize went through ForwardAsync and succeeded: the session is open. */
    public void MarkInitialized(JsonObject? parameters)
    {
        initializeParameters = parameters?.DeepClone().AsObject() ?? initializeParameters;
        NeedsInitialize = false;
    }

    /** Re-initializes when needed (after "project changed"), with the parameters of the last initialize. */
    public Task EnsureInitializedAsync() => NeedsInitialize ? InitializeAsync(initializeParameters) : Task.CompletedTask;

    /** Loads the editor's catalogue. */
    public async Task LoadToolsAsync()
    {
        var reply = await RequestAsync("tools/list", new JsonObject()).ConfigureAwait(false);
        Tools = reply["result"]?["tools"] is JsonArray tools ? (JsonArray)tools.DeepClone() : [];
    }

    /** One request; returns the JSON-RPC reply object. HttpRequestException/TaskCanceledException mean the editor is gone. */
    public async Task<JsonObject> RequestAsync(string method, JsonObject parameters)
    {
        int id = Interlocked.Increment(ref nextId);
        string body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }.ToJsonString();
        foreach (string line in await PostAsync(body).ConfigureAwait(false))
        {
            if (JsonNode.Parse(line) is not JsonObject reply) continue;
            if (reply["id"]?.GetValueKind() == JsonValueKind.Number && reply["id"]!.GetValue<int>() == id)
            {
                Observe(reply);
                return reply;
            }
        }
        throw new HttpRequestException($"Solforge sent no reply to {method}.");
    }

    /** Forwards one client line verbatim; returns the reply lines (none for a notification). */
    public async Task<IReadOnlyList<string>> ForwardAsync(string line)
    {
        var replies = await PostAsync(line).ConfigureAwait(false);
        foreach (string reply in replies)
            try { if (JsonNode.Parse(reply) is JsonObject message) Observe(message); }
            catch (JsonException) { }
        return replies;
    }

    /** A "project changed" refusal makes the next call open a new session for the editor's current project. */
    private void Observe(JsonObject reply)
    {
        if (reply["error"]?["code"]?.GetValueKind() == JsonValueKind.Number
            && reply["error"]!["code"]!.GetValue<int>() == McpSessionRegistry.ProjectChangedCode)
            NeedsInitialize = true;
    }

    private async Task<IReadOnlyList<string>> PostAsync(string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Entry.Url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Entry.Token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (session is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
        // Several agents can share one Solforge: the pid tells their sessions apart in get_sessions and the presence list.
        request.Headers.TryAddWithoutValidation(McpSessionRegistry.PidHeader, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (worker) request.Headers.TryAddWithoutValidation(McpSessionRegistry.WorkerHeader, "1");
        using var response = await http.SendAsync(request).ConfigureAwait(false);
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var values)) session = values.FirstOrDefault() ?? session;
        if (response.StatusCode == HttpStatusCode.Accepted) return [];
        string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Solforge answered {(int)response.StatusCode}: {text}");
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            return [.. text.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal)).Select(line => line[5..].Trim())];
        return text.Length > 0 ? [text.ReplaceLineEndings(" ")] : [];
    }
}

/**
 * The `solforge` upstream of the aggregate (UI-UE5 §4.4.3): the live editor when McpDiscovery finds one, the in-process
 * headless host otherwise. Discovery is cached for 2 s and re-checked every 5 s in the background; Changed fires when the
 * route switches between an editor (or another editor) and headless.
 */
internal sealed class SolforgeRoute(string? project, bool worker) : IAsyncDisposable
{
    private static readonly TimeSpan CacheAge = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(5);
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(20) };
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private EditorLink? editor;
    private string routeKey = "";
    private string? lastEditorProject;
    private (string? Project, McpDispatcher Dispatcher, HeadlessSolforgeHost Host)? headless;
    private JsonObject? clientParameters;
    private Task? loop;

    /** Raised (off the request path) when the route changes, so the aggregate sends notifications/tools/list_changed. */
    public event Action? Changed;

    /** The server instructions for the aggregate's initialize: the editor's when attached, else the headless ones. */
    public string Instructions => editor?.Instructions is { Length: > 0 } live ? live : SolforgeTools.Instructions +
        " When the Solforge editor is not running, the tools that need it answer 'needs the Solforge editor'; they attach by themselves once it starts.";

    /** The client's initialize parameters (clientInfo), passed on to the editor's session. */
    public void SetClient(JsonObject? parameters) => clientParameters = parameters?.DeepClone().AsObject();

    /** First route check and the 5 s background re-check. */
    public async Task StartAsync()
    {
        await CheckAsync().ConfigureAwait(false);
        loop ??= Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(CheckEvery, stop.Token).ConfigureAwait(false);
                    await CheckAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    /** Re-runs discovery (cached 2 s) and attaches to, switches or drops the editor. */
    public async Task CheckAsync()
    {
        var entry = McpDiscovery.FindCached(project, CacheAge);
        string key = entry is null ? "headless" : entry.Pid + "|" + entry.Url;
        bool changed;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (entry is not null && editor?.Key != key)
            {
                var link = new EditorLink(http, entry, worker);
                try
                {
                    await link.InitializeAsync(clientParameters).ConfigureAwait(false);
                    await link.LoadToolsAsync().ConfigureAwait(false);
                    editor = link;
                    lastEditorProject = entry.Project ?? lastEditorProject;
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
                {
                    Console.Error.WriteLine("sol: Solforge editor found but not reachable, serving headless: " + exception.Message);
                    McpDiscovery.Invalidate();
                    editor = null;
                    key = "headless";
                }
            }
            else if (entry is null) editor = null;
            changed = routeKey.Length > 0 && routeKey != key;
            routeKey = key;
        }
        finally { gate.Release(); }
        if (changed)
        {
            Console.Error.WriteLine(key == "headless" ? "sol: Solforge editor gone, serving headless." : "sol: attached to the Solforge editor (pid " + entry?.Pid + ").");
            try { Changed?.Invoke(); } catch (Exception) { }
        }
    }

    /** The Solforge tools: the live editor catalogue, then cached editor tools, then the headless ones; one entry per name. */
    public async Task<JsonArray> ToolsAsync()
    {
        await CheckAsync().ConfigureAwait(false);
        var cached = ToolsCache.Read();
        var editorOnly = cached.Where(tool => tool.RequiresEditor).Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        var list = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (editor is { } live)
            foreach (var node in live.Tools.OfType<JsonObject>())
                if (node["name"]?.ToString() is { } name && seen.Add(name))
                {
                    var entry = node.DeepClone().AsObject();
                    entry["_meta"] ??= new JsonObject { [ToolsCache.MetaKey] = editorOnly.Contains(name) };
                    list.Add(entry);
                }
        foreach (var tool in cached)
            if (seen.Add(tool.Name) && (!worker || WorkerSessionPolicy.Allows(tool.Name, tool.Entry["annotations"]?["readOnlyHint"]?.GetValueKind() == JsonValueKind.False)))
                list.Add(tool.Entry.DeepClone());
        foreach (var tool in Headless().Dispatcher.Tools)
            if (seen.Add(tool.Name)) list.Add(ToolsCache.Entry(tool));
        return list;
    }

    /** True when the name is a Solforge tool (live, cached or headless). */
    public async Task<bool> OwnsAsync(string name)
    {
        if (editor?.Tools.OfType<JsonObject>().Any(tool => tool["name"]?.ToString() == name) == true) return true;
        if (Headless().Dispatcher.Tools.Any(tool => tool.Name == name)) return true;
        await Task.CompletedTask.ConfigureAwait(false);
        return ToolsCache.Read().Any(tool => tool.Name == name);
    }

    /** One tools/call: the editor when attached (a failed request falls back to headless), else headless. Returns the JSON-RPC reply. */
    public async Task<JsonObject> CallAsync(string name, JsonObject arguments)
    {
        await CheckAsync().ConfigureAwait(false);
        if (editor is { } live)
        {
            try
            {
                await live.EnsureInitializedAsync().ConfigureAwait(false);
                return await live.RequestAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments.DeepClone() }).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
            {
                Console.Error.WriteLine("sol: the Solforge editor stopped answering, falling back to headless: " + exception.Message);
                McpDiscovery.Invalidate();
                await CheckAsync().ConfigureAwait(false);
            }
        }
        var (_, dispatcher, host) = Headless();
        if (dispatcher.Tools.All(tool => tool.Name != name))
            return ToolsCache.Read().Any(tool => tool.Name == name)
                ? new JsonObject { ["result"] = McpToolResult.Error(host.NeedsEditor).ToJson() }
                : new JsonObject { ["error"] = new JsonObject { ["code"] = -32602, ["message"] = "Unknown tool: " + name } };
        string message = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = name, ["arguments"] = arguments.DeepClone() }
        }.ToJsonString();
        string? reply = await dispatcher.HandleAsync(message, stop.Token).ConfigureAwait(false);
        return reply is not null && JsonNode.Parse(reply) is JsonObject parsed ? parsed
            : new JsonObject { ["error"] = new JsonObject { ["code"] = -32603, ["message"] = "The headless Solforge host gave no reply." } };
    }

    /** The headless dispatcher for the session's project (or the last attached editor's), rebuilt when that changes. */
    private (string? Project, McpDispatcher Dispatcher, HeadlessSolforgeHost Host) Headless()
    {
        string? wanted = project ?? lastEditorProject;
        if (headless is { } current && string.Equals(current.Project, wanted, StringComparison.OrdinalIgnoreCase)) return current;
        var host = new HeadlessSolforgeHost(wanted);
        var created = (wanted, HeadlessSolforgeHost.CreateDispatcher(host, ShimVersion, worker), host);
        headless = created;
        return created;
    }

    /** The shim's version for serverInfo. */
    public static string ShimVersion => typeof(SolforgeRoute).Assembly.GetName().Version?.ToString(3) ?? "0";

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (loop is not null) try { await loop.ConfigureAwait(false); } catch (Exception) { }
        http.Dispose();
        gate.Dispose();
        stop.Dispose();
    }
}
