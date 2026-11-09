using System.Diagnostics;

namespace Solforge.Mcp;

/** The running server for one Solforge process: dispatcher, HTTP listener, discovery file and client configs. */
public sealed partial class SolforgeMcpService : IAsyncDisposable
{
    private readonly McpHttpServer server;

    private SolforgeMcpService(McpHttpServer server, McpDispatcher dispatcher)
    {
        this.server = server;
        Dispatcher = dispatcher;
    }

    public McpDispatcher Dispatcher { get; }
    /** The /editor/* channel (null when the host does not implement IEditorUiHost). */
    public EditorUiApi? EditorUi => server.EditorUi;
    /** Connected MCP sessions, the map claim and running work (the editor's presence list reads it). */
    public McpSessionRegistry? Sessions => server.Sessions;
    public string Url => server.Url;
    public int Port => server.Port;
    public int Requests => server.RequestCount;
    public string? DiscoveryPath { get; private set; }
    public IReadOnlyList<McpClientConfigs.Written> Configs { get; private set; } = [];

    /** Starts the HTTP server, registers the tools and writes the discovery file. */
    public static SolforgeMcpService Start(ISolforgeHost host, string version)
    {
        // Local server tools (LocalServer/LocalServerTools.cs) sit beside the editor tools; they never reach the VPS.
        var localServer = LocalServer.LocalServerTools.Create(LocalServer.LocalServerManager.CreateDefault(), () => host.ProjectRoot, host.ApproveAsync);
        // get_client_build, testkit_status and testkit_restart live in ClientProvenance/ and Testkit/.
        // Several agents can share this Solforge (MultiAgentTools.cs): sessions, the open_map guard and map-scoped reads.
        var sessions = new McpSessionRegistry();
        var dispatcher = new McpDispatcher(MultiAgentTools.Wrap([.. SolforgeTools.Create(host), .. localServer, .. ClientProvenance.ClientBuildTools.Create(),
            .. Testkit.TestkitTools.Create(Testkit.TestkitMonitor.CreateDefault(), host.ApproveAsync)], host as ISolforgePresence, sessions), version,
            SolforgeTools.Instructions + LocalServer.LocalServerTools.Instructions + ClientProvenance.ClientBuildTools.Instructions + Testkit.TestkitTools.Instructions + MultiAgentTools.Instructions);
        dispatcher.OnToolCall = tool => host.Automatic?.NoteToolCall(tool);
        // UI-UE5 §5.6: sessions are bound to the project they were opened for; a switch refuses their calls ("project changed").
        var server = new McpHttpServer(dispatcher) { Viewport = host.Viewport, Sessions = sessions, CurrentProject = () => host.ProjectRoot };
        // SOLFORGE-AI-APP-SPEC §6.3: the editor UI channel for the Solforge AI app; its token never leaves this process
        // except to a paired app, and is not part of the discovery entry.
        if (host is IEditorUiHost uiHost)
        {
            var ui = new EditorUiApi(uiHost);
            server.EditorUi = ui;
            sessions.Changed += () => ui.EmitSoon(EditorUiEvents.SessionsChanged);
        }
        server.Start();
        host.Viewport.LocalUrl = server.ViewportUrl;
        McpDiscovery.CleanStale();
        var service = new SolforgeMcpService(server, dispatcher);
        service.Publish(host.ProjectRoot);
        // UI-UE5 §4.4: the standalone shim's tool cache, per-user upstreams and descriptor; off the UI thread, best effort.
        var tools = dispatcher.Tools;
        _ = Task.Run(() => PublishStandaloneFiles(tools, version, null));
        return service;
    }

    /** Where the standalone files went (or why not), for the AI panel. */
    public static string StandaloneStatus { get; private set; } = "Standalone MCP files not written yet.";

    /**
     * UI-UE5 §4.4: writes tools-cache.json (this editor's catalogue), sol-upstreams-default.json (solforge, the delegates and the
     * carried workspace MCPs, no project entries) and solforge-ai-mcp.json (the standalone descriptor). With shared null the
     * descriptor is written when none exists and rewritten when stale (StandaloneDescriptor.RefreshIfStale), keeping the
     * destinyModder and skill folders a richer one from the Solforge AI settings carried.
     */
    public static void PublishStandaloneFiles(IReadOnlyList<McpTool>? tools, string version, Solforge.Harness.Shared.SharedHarnessResourceSnapshot? shared)
    {
        // The editor start (Start) and SolforgeAiStartup both publish at once; one at a time, so neither loses its write.
        lock (publishGate)
        {
            try
            {
                if (tools is not null) ToolsCache.Write(tools, version);
                if (FindShim() is not { } published) { StandaloneStatus = "solforge-mcp.exe not found beside Solforge: standalone files not written."; return; }
                string shim = SolMcpServers.RunCopy(published, Path.Combine(McpDiscovery.Folder, "bin"));
                string path = EngineCommands.FreshPath();
                string upstreams = SolMcpServers.WriteDefaultUpstreams(SolMcpServers.CollectDefault(shim,
                    gemini: EngineCommands.Resolve("agy", path) is not null, clinePass: EngineCommands.Resolve("cline", path) is not null));
                // With the shared snapshot the descriptor is written in full. On a plain editor start a stale descriptor
                // (older version, pruned run copy, other upstreams file) is rewritten, keeping its destinyModder and skill folders.
                string? descriptor = shared is not null
                    ? StandaloneDescriptor.Write(shim, upstreams, shared.ServerDefinition, shared.SkillDirectories ?? [])
                    : StandaloneDescriptor.RefreshIfStale(shim, upstreams);
                StandaloneShim = shim;
                // A registered ~/.claude.json entry follows the run copy (older copies are pruned); never created here.
                StandaloneRegistration.Refresh(shim, upstreams);
                // Every other ~/.claude.json entry on an older run copy (the user-level `sol` among them) follows too, so a
                // client never points at a pruned copy, even when no project is opened in this editor session.
                StandaloneRegistration.RefreshRunCopies(shim);
                // Write and RefreshIfStale return null when the folder is not writable: never report that as success.
                StandaloneStatus = descriptor is null
                    ? "solforge-ai-mcp.json could not be written/refreshed in " + McpDiscovery.Folder + " (folder not writable)."
                    : descriptor == "current"
                        ? "Standalone MCP files written to " + McpDiscovery.Folder + " (solforge-ai-mcp.json already current)."
                        : descriptor == "rewritten"
                            ? "Standalone MCP files written to " + McpDiscovery.Folder + " (stale solforge-ai-mcp.json rewritten)."
                            : "Standalone MCP files written to " + McpDiscovery.Folder + ".";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                or InvalidOperationException or ArgumentException)
            {
                StandaloneStatus = "Standalone MCP files not written: " + exception.Message;
            }
        }
    }

    private static readonly object publishGate = new();

    /** The run copy of solforge-mcp.exe the last publish used (the standalone registration's command), or null. */
    public static string? StandaloneShim { get; private set; }

    /**
     * The user's Register action (Settings > Solforge AI > STANDALONE MCP): publishes the files, then registers the shim with a
     * Solforge AI started without the editor (StandaloneRegistration). Throws with a curated message when it cannot.
     */
    public static StandaloneRegistration.State RegisterStandalone(IReadOnlyList<McpTool>? tools, string version,
        Solforge.Harness.Shared.SharedHarnessResourceSnapshot? shared)
    {
        PublishStandaloneFiles(tools, version, shared);
        lock (publishGate)
        {
            if (StandaloneShim is not { } shim || !File.Exists(McpDiscovery.DefaultUpstreamsFile))
                throw new InvalidOperationException(StandaloneStatus);
            return StandaloneRegistration.Register(shim, McpDiscovery.DefaultUpstreamsFile);
        }
    }

    /** Points the discovery file at the open project (null when none is open); called on every project change. */
    public void Publish(string? project) =>
        DiscoveryPath = McpDiscovery.Write(new McpDiscoveryEntry(Environment.ProcessId, server.Url, server.Token, project, DateTimeOffset.UtcNow));

    /** Writes .mcp.json and the Cline, Antigravity desktop and Antigravity CLI settings for the project; null shim means it was not found. */
    public IReadOnlyList<McpClientConfigs.Written> WriteClientConfigs(string projectRoot, string shimPath,
        IReadOnlyList<SolMcpServers.Server>? servers = null) =>
        Configs = McpClientConfigs.WriteAll(projectRoot, shimPath, servers: servers);

    /** solforge-mcp.exe beside the running exe, else the published single-file exe, else the developer build. */
    public static string? FindShim()
    {
        string here = AppContext.BaseDirectory;
        string beside = Path.Combine(here, "solforge-mcp.exe");
        if (File.Exists(beside)) return beside;
        for (var folder = new DirectoryInfo(here); folder is not null; folder = folder.Parent)
        {
            string published = Path.Combine(folder.FullName, "publish", "solforge-mcp.exe");
            if (File.Exists(published)) return published;
            string project = Path.Combine(folder.FullName, "src", "Solforge.Mcp.Shim", "bin");
            if (!Directory.Exists(project)) continue;
            return Directory.EnumerateFiles(project, "solforge-mcp.exe", SearchOption.AllDirectories)
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path)).FirstOrDefault();
        }
        return null;
    }

    /** Removes the discovery file and stops the server. */
    public async ValueTask DisposeAsync()
    {
        McpDiscovery.Remove(Environment.ProcessId);
        if (server.Viewport is { } feed) feed.LocalUrl = null;
        await server.DisposeAsync().ConfigureAwait(false);
        server.EditorUi?.Dispose();
    }

    /** Debug-output trace line. */
    internal static void Trace(string text) => Debug.WriteLine("[solforge-mcp] " + text);
}
