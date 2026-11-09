/**
 * File: How workflow workers reach the editor (UI-UE5 §5.4). Replaces solforge-next's named-pipe EditorBridgeClient with
 * four operations over ISolforgeHost: the live context, the open documents, a whole-file read with its sha256 version,
 * and a reviewed edit request. HostEditorGateway serves both the in-editor host (the editor's AiHost, wrapped by
 * SunriseStudio.Editor/EditorGateway.cs) and the headless host. Every edit goes through apply_project_edit, so the user
 * reviews the exact before/after text and a stale version is refused.
 */
using System.Text.Json;
using System.Text.Json.Nodes;
using Solforge.Harness.Credentials;
using Solforge.Mcp;

namespace Solforge.Harness.Runtime;

/** One gateway answer: Data on success, a display-safe Error otherwise. */
public sealed record EditorGatewayReply(bool Success, JsonElement Data, string? Error = null)
{
    public static EditorGatewayReply Ok(JsonNode? data) => new(true, JsonSerializer.SerializeToElement(data ?? new JsonObject()));
    public static EditorGatewayReply Fail(string error) => new(false, JsonSerializer.SerializeToElement(new JsonObject()), error);
}

/** The editor operations a workflow worker may use. Implementations refuse other projects, paused sharing and the credential file. */
public interface IEditorGateway
{
    /** get_context: the live editor context (project, map, selection, open documents summary). */
    Task<EditorGatewayReply> GetContextAsync(CancellationToken cancellationToken);
    /** list_open_documents: what the editor has open, with dirty flags and sha256 versions of file-backed entries. */
    Task<EditorGatewayReply> ListOpenDocumentsAsync(CancellationToken cancellationToken);
    /** read_document: a whole project file (or sdk:/scripts: path) with its sha256 version. */
    Task<EditorGatewayReply> ReadDocumentAsync(string path, CancellationToken cancellationToken);
    /** request_document_edit: replace a project file after the user's before/after review; version is the sha256 last read. */
    Task<EditorGatewayReply> RequestDocumentEditAsync(string path, string content, string version, CancellationToken cancellationToken);
}

/**
 * IEditorGateway over any ISolforgeHost, bound to one project: when the host's project changes, every operation is
 * refused ("project changed"), exactly like the MCP sessions.
 */
public class HostEditorGateway : IEditorGateway
{
    private const long MaximumDocument = 2 << 20;
    private readonly ISolforgeHost host;
    private readonly string? projectRoot;
    private readonly Lazy<IReadOnlyDictionary<string, McpTool>> tools;

    public HostEditorGateway(ISolforgeHost host, string? projectRoot)
    {
        this.host = host;
        this.projectRoot = Normalize(projectRoot);
        tools = new(() => SolforgeTools.Create(host).ToDictionary(tool => tool.Name, StringComparer.Ordinal));
    }

    /** Null when the host still works on the gateway's project and is not paused, else why not. */
    protected virtual string? Problem()
    {
        if (host.Paused) return "Editor context sharing is paused by the user.";
        if (!string.Equals(Normalize(host.ProjectRoot), projectRoot, StringComparison.OrdinalIgnoreCase))
            return "project changed: this workflow belongs to " + (projectRoot ?? "no project") + ", and Solforge now has "
                + (host.ProjectRoot ?? "no project") + " open. Nothing was done.";
        return null;
    }

    public virtual async Task<EditorGatewayReply> GetContextAsync(CancellationToken cancellationToken)
    {
        if (Problem() is { } problem) return EditorGatewayReply.Fail(problem);
        await Task.CompletedTask.ConfigureAwait(false);
        try { return EditorGatewayReply.Ok(JsonNode.Parse(host.ContextJson)); }
        catch (JsonException) { return EditorGatewayReply.Fail("The editor context is not available yet."); }
    }

    public virtual async Task<EditorGatewayReply> ListOpenDocumentsAsync(CancellationToken cancellationToken)
    {
        if (Problem() is { } problem) return EditorGatewayReply.Fail(problem);
        return await CallToolAsync("get_open_documents", new JsonObject(), cancellationToken).ConfigureAwait(false);
    }

    public virtual async Task<EditorGatewayReply> ReadDocumentAsync(string path, CancellationToken cancellationToken)
    {
        if (Problem() is { } problem) return EditorGatewayReply.Fail(problem);
        if (ProjectFiles.Resolve(host.ProjectRoot, host.SdkRoots, path, host.ServerScriptsRoot, host.AllowedReadFolders) is not { } full)
            return EditorGatewayReply.Fail("That path is outside the project, SDK docs, server scripts and enabled AI folders (including their subfolders).");
        if (CredentialGuard.IsCredentialPath(full)) return EditorGatewayReply.Fail(CredentialGuard.Refusal);
        var info = new FileInfo(full);
        if (!info.Exists) return EditorGatewayReply.Fail("No such file: " + path);
        if (info.Length > MaximumDocument) return EditorGatewayReply.Fail($"{path} is {info.Length} bytes; the limit is {MaximumDocument}.");
        byte[] bytes = await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);
        string text;
        using (var reader = new StreamReader(new MemoryStream(bytes), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return EditorGatewayReply.Ok(new JsonObject
        {
            ["path"] = path, ["content"] = text, ["sha256"] = ProjectFiles.Sha256(bytes), ["characters"] = text.Length,
            ["note"] = "Disk content. A Graph panel draft that is not saved yet is not included; get_open_documents marks it dirty."
        });
    }

    public virtual async Task<EditorGatewayReply> RequestDocumentEditAsync(string path, string content, string version, CancellationToken cancellationToken)
    {
        if (Problem() is { } problem) return EditorGatewayReply.Fail(problem);
        return await CallToolAsync("apply_project_edit", new JsonObject
        {
            ["path"] = path, ["content"] = content, ["expected_sha256"] = version
        }, cancellationToken).ConfigureAwait(false);
    }

    /** Runs one Solforge tool handler and turns its result into a reply (JSON text becomes Data). */
    protected async Task<EditorGatewayReply> CallToolAsync(string name, JsonObject arguments, CancellationToken cancellationToken)
    {
        if (!tools.Value.TryGetValue(name, out var tool)) return EditorGatewayReply.Fail("This Solforge host has no " + name + ".");
        McpToolResult result;
        try { result = await tool.Handler(arguments, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException)
        { return EditorGatewayReply.Fail(name + " failed: " + exception.Message); }
        string text = string.Join("\n", result.Texts);
        if (result.IsError) return EditorGatewayReply.Fail(text);
        JsonNode? data;
        try { data = result.Texts.Count == 1 ? JsonNode.Parse(text) : new JsonObject { ["text"] = text }; }
        catch (JsonException) { data = new JsonObject { ["text"] = text }; }
        return EditorGatewayReply.Ok(data);
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }
}
