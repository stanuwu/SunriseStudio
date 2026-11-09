/**
 * File: The standalone Solforge MCP host (UI-UE5 §4.4.2). When no editor runs, `solforge-mcp --sol` (and `--project`)
 * serve the same tool catalogue in-process from this host: tools whose handlers need no editor UI (project files, content,
 * graphs, decomp, schema registry, retail reads, local server reads, testkit, client build; SolforgeTools.HeadlessTools)
 * answer normally, every other tool answers "needs the Solforge editor: start Solforge (and open <project>)". Nothing here
 * opens game packages, shows UI or approves anything.
 */
using System.Text.Json;
using System.Text.Json.Nodes;
using Solforge.Harness.Credentials;

namespace Solforge.Mcp.Headless;

/** ISolforgeHost without an editor: file-backed reads only. Thread-safe; members read settings at call time. */
public sealed class HeadlessSolforgeHost : ISolforgeHost
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /** @param project the project folder (null: none; project tools say how to get one). */
    public HeadlessSolforgeHost(string? project, string? settingsPath = null)
    {
        this.settingsPath = settingsPath;
        ProjectRoot = Normalize(project);
        var settings = EditorSettings.Read(settingsPath);
        GameRoot = settings.GameRoot;
        ServerScriptsRoot = settings.ScriptsRoot is { } scripts && Directory.Exists(scripts) ? scripts : null;
        // The same credential guard as the editor: the file named in the editor's settings is never read or searched.
        CredentialGuard.CredentialFile ??= settings.CredentialFile;
        codeRoots = new Lazy<IReadOnlyList<CodeRoot>>(() =>
        {
            try { return CodeRefs.Locate(GameRoot, ProjectRoot, [], ServerScriptsRoot); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
        });
    }

    private readonly Lazy<IReadOnlyList<CodeRoot>> codeRoots;
    private readonly string? settingsPath;

    /** The game installation from the editor's settings (code roots, reference library), or null. */
    public string? GameRoot { get; }
    public bool Headless => true;
    public bool Paused => false;
    public string? ProjectRoot { get; }
    public IReadOnlyList<string> SdkRoots => ProjectRoot is { } root
        ? new[] { Path.Combine(root, "sdk"), Path.Combine(root, "docs") }.Where(Directory.Exists).ToArray() : [];
    public IReadOnlyList<string> AllowedReadFolders
    {
        get
        {
            // Read grants live, so hiding/removing a folder applies without restarting a standalone MCP.
            var settings = EditorSettings.Read(settingsPath);
            if (ProjectRoot is not { } project) return settings.ReadFolders.Where(Directory.Exists).ToArray();
            var found = EngineLaunch.AddDirs(project, [settings.GameRoot])
                .Where(folder => !settings.HiddenFolders.Contains(folder, StringComparer.OrdinalIgnoreCase));
            // An explicitly added folder is still a grant even if the same automatically found row is hidden.
            return EngineLaunch.AddDirs(project, found.Concat(settings.ReadFolders), ancestors: false);
        }
    }
    public string? ServerScriptsRoot { get; }
    public string? CurrentMap => null;
    public AttentionLog Attention { get; } = new();
    public string ContextJson => new JsonObject
    {
        ["headless"] = true, ["project"] = ProjectRoot,
        ["note"] = "The Solforge editor is not running: this is the standalone MCP. File, content, graph, decomp, retail and local " +
            "server reads work; editor tools answer 'needs the Solforge editor'. Start Solforge" + (ProjectRoot is null ? "" : " and open " + ProjectRoot) +
            " and the editor tools attach to this same session."
    }.ToJsonString(Options);
    public IReadOnlyList<VisibleObject> Visible => [];
    public IReadOnlyList<CodeRoot> CodeRoots => codeRoots.Value;
    public string? ReferenceFolder => ReferenceLibrary.Locate([ProjectRoot, GameRoot, AppContext.BaseDirectory]);
    public ViewportFeed Viewport { get; } = new();

    /** The error every editor-only tool and host call gives. */
    public string NeedsEditor => NeedsEditorMessage(ProjectRoot);

    /** "needs the Solforge editor: start Solforge (and open <project>)". */
    public static string NeedsEditorMessage(string? project) =>
        "needs the Solforge editor: start Solforge" + (project is null ? "" : " and open " + project) +
        ". The editor's tools attach to this session by themselves once it runs.";

    public Task<IReadOnlyList<ObjectEntry>> ObjectsAsync(string query, CancellationToken cancel) => Task.FromResult<IReadOnlyList<ObjectEntry>>([]);
    public Task<JsonObject?> GetObjectAsync(string id, CancellationToken cancel) => Task.FromResult<JsonObject?>(null);
    public Task<JsonObject> GetOpenDocumentsAsync(CancellationToken cancel) =>
        Task.FromResult(new JsonObject { ["headless"] = true, ["project"] = ProjectRoot, ["note"] = NeedsEditor });
    public Task<byte[]?> ScreenshotAsync(string? objectId, bool labels, CancellationToken cancel) => Task.FromResult<byte[]?>(null);
    public Task<JsonObject?> GetTagAsync(uint hash, CancellationToken cancel) => Task.FromResult<JsonObject?>(null);
    public Task<JsonObject?> GetPackageEntryAsync(string id, CancellationToken cancel) => Task.FromResult<JsonObject?>(null);
    public JsonObject LiveState() => new() { ["attached"] = false, ["note"] = NeedsEditor };
    public Task<MissionSnapshot?> MissionAsync(CancellationToken cancel) => Task.FromResult<MissionSnapshot?>(null);
    public Task<JsonObject> ReadPanelAsync(string panel, PanelQuery query, CancellationToken cancel) =>
        Task.FromResult(new JsonObject { ["error"] = NeedsEditor });
    /** No UI to ask: nothing is ever approved headless (the dispatcher refuses those tools before they get here). */
    public Task<bool> ApproveAsync(string tool, string summary, CancellationToken cancel) => Task.FromResult(false);
    public Task<string> RunActionAsync(string tool, JsonObject args, CancellationToken cancel) => Task.FromResult(NeedsEditor);
    public Task<IReadOnlyList<UiControl>> UiControlsAsync(CancellationToken cancel) => Task.FromResult<IReadOnlyList<UiControl>>([]);
    public Task<(bool Ok, string Message)> UiCommandAsync(UiCommand command, CancellationToken cancel) => Task.FromResult((false, NeedsEditor));

    /** list_project_files is answered from the folder; every other query needs the editor. */
    public Task<JsonObject> QueryAsync(string tool, JsonObject args, CancellationToken cancel) => Task.Run(() =>
    {
        if (tool != "list_project_files") return new JsonObject { ["error"] = NeedsEditor };
        if (ProjectRoot is not { } root || !Directory.Exists(root)) return new JsonObject { ["error"] = SolforgeTools.NoProjectOpen(this) };
        string filter = McpJson.String(args, "filter") ?? "", extension = McpJson.String(args, "extension") ?? "";
        if (extension.Length > 0 && !extension.StartsWith('.')) extension = "." + extension;
        var files = new List<(string Path, long Size)>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (string file in Directory.EnumerateFiles(root, "*", options))
        {
            cancel.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Split('/').Any(part => part is ".git" or "bin" or "obj" or "node_modules" or ".vs")) continue;
            if (filter.Length > 0 && !relative.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            if (extension.Length > 0 && !relative.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;
            if (CredentialGuard.IsCredentialPath(file)) continue;
            try { files.Add((relative, new FileInfo(file).Length)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        int offset = Math.Max(0, McpJson.Int(args, "offset", 0)), limit = Math.Clamp(McpJson.Int(args, "limit", 50), 1, 500);
        var rows = new JsonArray();
        foreach (var (path, size) in files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).Skip(offset).Take(limit))
            rows.Add(new JsonObject { ["path"] = path, ["size"] = size });
        return new JsonObject
        {
            ["total"] = files.Count, ["offset"] = offset, ["rows"] = rows,
            ["next_offset"] = offset + limit < files.Count ? offset + limit : null, ["headless"] = true
        };
    }, cancel);

    /**
     * The full Solforge tool catalogue over a headless host: the same names and schemas the editor serves, with every
     * RequiresEditor tool answering NeedsEditor and project-folder tools answering "no project" when none was given.
     * @param worker apply WorkerSessionPolicy (solforge-mcp --worker).
     */
    public static McpDispatcher CreateDispatcher(HeadlessSolforgeHost host, string version, bool worker = false, bool readOnly = false)
    {
        var sessions = new McpSessionRegistry();
        IReadOnlyList<McpTool> tools = MultiAgentTools.Wrap([.. SolforgeTools.Create(host),
            .. LocalServer.LocalServerTools.Create(LocalServer.LocalServerManager.CreateDefault(), () => host.ProjectRoot, host.ApproveAsync),
            .. ClientProvenance.ClientBuildTools.Create(),
            .. Testkit.TestkitTools.Create(Testkit.TestkitMonitor.CreateDefault(), host.ApproveAsync)], null, sessions);
        var served = new List<McpTool>(tools.Count);
        foreach (var tool in tools)
        {
            if (worker && !WorkerSessionPolicy.Allows(tool.Name, tool.RequiresApproval, readOnly)) continue;
            if (SolforgeTools.RequiresEditor(tool))
            {
                string message = host.NeedsEditor;
                served.Add(tool with { Handler = (_, _) => Task.FromResult(McpToolResult.Error(message)) });
            }
            else if (host.ProjectRoot is null && SolforgeTools.ProjectFolderTools.Contains(tool.Name))
                served.Add(tool with { Handler = (args, cancel) => tool.Name is "read_project_file" or "search_project" && host.AllowedReadFolders.Count > 0
                    ? tool.Handler(args, cancel) : Task.FromResult(McpToolResult.Error(SolforgeTools.NoProjectOpen(host))) });
            else served.Add(tool);
        }
        return new McpDispatcher(served, version, "Standalone Solforge MCP (no editor running): " + SolforgeTools.Instructions +
            LocalServer.LocalServerTools.Instructions + ClientProvenance.ClientBuildTools.Instructions + Testkit.TestkitTools.Instructions);
    }

    private static string? Normalize(string? project)
    {
        if (string.IsNullOrWhiteSpace(project)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(project)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /** The few path settings the headless host borrows from the editor's settings file (never anything else). */
    private sealed record EditorSettings(string? GameRoot, string? ScriptsRoot, string? CredentialFile,
        IReadOnlyList<string> ReadFolders, IReadOnlyList<string> HiddenFolders)
    {
        public static EditorSettings Read(string? settingsPath = null)
        {
            string path = settingsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sunrise Studio", "settings.json");
            try
            {
                if (!File.Exists(path)) return new(null, null, null, [], []);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var document = JsonDocument.Parse(stream);
                string? Text(string name) => document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: > 0 } text ? text : null;
                IReadOnlyList<string> Folders(string name) => document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
                    ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)
                        .Where(folder => !string.IsNullOrWhiteSpace(folder)).ToArray() : [];
                return new(Text("SunrisePath"), Text("ScriptsPath"), Text("CredentialFile"), Folders("AiFolders"), Folders("AiHiddenFolders"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return new(null, null, null, [], []); }
        }
    }
}
