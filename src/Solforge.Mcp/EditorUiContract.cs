/**
 * File: the wire types of the editor UI channel (/editor/* on the MCP loopback server), the C# twin of
 * solforge-next/ai/runtime/packages/experimental/solforge-contract/src/editor-api.ts. Both files change together; JSON is
 * camelCase (EditorUiJson.Options). The channel is separate from MCP: it needs the per-process UiToken that only a paired
 * Solforge AI main process holds (EditorUiApi.cs, SOLFORGE-AI-APP-SPEC §6.2, §6.3).
 */
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Solforge.Mcp;

/** Paths of the editor UI channel. Every path but Pair needs "Authorization: Bearer <UiToken>". */
public static class EditorUiPaths
{
    public const string Prefix = "/editor/";
    public const string Pair = "/editor/pair";
    public const string State = "/editor/state";
    public const string Events = "/editor/events";
    public const string Requests = "/editor/requests";
    public const string Log = "/editor/log";
    public const string Sessions = "/editor/sessions";
    public const string Edits = "/editor/edits";
    public const string Context = "/editor/context";
    public const string ContextPublish = "/editor/context/publish";
    public const string ContextPins = "/editor/context/pins";
    public const string Folders = "/editor/folders";
    public const string Ask = "/editor/ask";
    public const string Screenshot = "/editor/screenshot";
}

/** Event names on GET /editor/events. */
public static class EditorUiEvents
{
    public const string RequestAdded = "request.added";
    public const string RequestAnswered = "request.answered";
    public const string LogAppended = "log.appended";
    public const string SessionsChanged = "sessions.changed";
    public const string ProjectChanged = "project.changed";
    public const string ContextChanged = "context.changed";
    public const string EditsChanged = "edits.changed";
    public const string StateChanged = "state.changed";
}

/** Request kinds: an MCP action approval, a reviewed file edit, a model workflow start. */
public static class EditorRequestKinds
{
    public const string Action = "action";
    public const string Edit = "edit";
    public const string Workflow = "workflow";
}

/** How a request ended: approved or denied by the user, auto (the editor's AutoApproveRequests setting), expired, cancelled. */
public static class EditorRequestOutcomes
{
    public const string Approved = "approved";
    public const string Denied = "denied";
    public const string Auto = "auto";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";
}

/** The serializer options of the channel. */
public static class EditorUiJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };
}

/** The open project: absolute folder and its folder name. */
public sealed record EditorProjectDto(string Path, string Name);

/** MCP server facts in GET /editor/state. */
public sealed record EditorMcpDto(int Port, int Sessions, int Calls);

/** GET /editor/state. */
public sealed record EditorStateDto(int EditorPid, string Version, EditorProjectDto? Project, string? Map, bool Paused, bool AutoApprove,
    EditorMcpDto Mcp, bool CredentialFileSet, int Pending, long Seq);

/** The MCP session behind a request: its id, "client (pid N)" label, client name and shim pid; null fields when unknown. */
public sealed record EditorSessionRefDto(string? Id, string Label, string? Client, int? Pid);

/** One side-by-side diff row (null side: no line there). */
public sealed record EditorDiffRowDto(string? Left, string? Right, bool Removed, bool Added);

/** detail of an edit request. Rows are null while the editor still compares; Truncated means only the first rows are sent. */
public sealed record EditorEditDetailDto(string Path, bool NewFile, int Removed, int Added, IReadOnlyList<EditorDiffRowDto>? Rows, bool Truncated);

/** One role of a workflow start and its ladder (display names in order; "(disabled)" marks skipped models). */
public sealed record EditorRoutedRoleDto(string Role, IReadOnlyList<string> Models, bool Required);

/**
 * detail of a workflow start. TaskEditable is false for a start an MCP client asked for (the task is read-only, as in the
 * editor dialog); SmallFix is always editable. Problems are settings validation errors that block the start.
 */
public sealed record EditorWorkflowDetailDto(string Task, bool SmallFix, bool FromMcp, bool TaskEditable,
    IReadOnlyList<EditorRoutedRoleDto> RoutedModels, IReadOnlyList<string> Problems);

/** One pending request. Detail is null for action, EditorEditDetailDto for edit, EditorWorkflowDetailDto for workflow. */
public sealed record EditorRequestDto(string Id, string Kind, string Tool, string Summary, EditorSessionRefDto? Session, bool Worker,
    DateTimeOffset Created, DateTimeOffset Expires, object? Detail);

/** POST /editor/requests/{id}/approve body: a workflow start may carry an edited task (when editable) and SmallFix. */
public sealed record EditorApproveBody(string? Task, bool? SmallFix);

/** Reply of approve and deny. */
public sealed record EditorAnswerResultDto(string Id, string Outcome);

/** data of request.answered. Reason: "user", "app", "timeout", "stopped", "paused", "stale", "cancelled". */
public sealed record EditorRequestAnsweredDto(string Id, string Kind, string Outcome, string Reason);

/** One answered tool request of the AI log. Outcome: auto, approved or denied. */
public sealed record EditorLogEntryDto(long Seq, DateTimeOffset Time, string Tool, string Summary, string Outcome, string? Session);

/** One Solforge AI or workflow line of the AI log (AppendAiLog). */
public sealed record EditorLogLineDto(long Seq, DateTimeOffset Time, string Text);

/** GET /editor/log?after=seq: entries and lines with Seq > after; Seq is the newest seq the editor has. */
public sealed record EditorLogDto(IReadOnlyList<EditorLogEntryDto> Entries, IReadOnlyList<EditorLogLineDto> Lines, long Seq);

/** One AI edit that Undo can restore; Created means the edit created the file (undo deletes it). */
public sealed record EditorEditDto(string Id, string Path, DateTimeOffset Time, bool Created);

/** One pinned object. Index is the position DELETE /editor/context/pins/{index} removes. */
public sealed record EditorPinDto(int Index, string Id, string Label);

/** One attention log row. */
public sealed record EditorAttentionDto(DateTimeOffset Time, string Kind, string Label);

/** GET /editor/context. ContextJson is the get_context snapshot text ("{\"paused\":true}" while paused). */
public sealed record EditorContextDto(string Chip, bool CanAsk, bool CanScreenshot, string ContextJson, IReadOnlyList<EditorPinDto> Pinned,
    IReadOnlyList<EditorAttentionDto> Recent, bool Published);

/** POST /editor/context/publish. */
public sealed record EditorPublishBody(bool On);

/** POST /editor/context/pins: target "hovered" (hovered, else selected) or "selected". */
public sealed record EditorPinBody(string Target);

/** A found folder (Shown false when the user hid it) or an added one (Exists false when it is missing). */
public sealed record EditorFoundFolderDto(string Path, bool Shown);
public sealed record EditorAddedFolderDto(string Path, bool Exists);

/** GET /editor/folders. */
public sealed record EditorFoldersDto(string? Project, IReadOnlyList<EditorFoundFolderDto> Found, IReadOnlyList<EditorAddedFolderDto> Added,
    bool IncludesSubfolders = true);

/** POST /editor/folders: op add or remove (added folders), hide or show (found folders). */
public sealed record EditorFolderOpBody(string Op, string Path);

/** POST /editor/ask: the question text (nothing goes to the clipboard). */
public sealed record EditorAskDto(string Text);

/** POST /editor/screenshot: the PNG's project-relative path under .solforge/screenshots. */
public sealed record EditorScreenshotDto(string RelativePath);

/** POST /editor/pair body. */
public sealed record EditorPairBody(int AppPid, string? AppVersion, string? Nonce);

/** POST /editor/pair reply on accept; the token is held only in the app's main process memory. */
public sealed record EditorPairDto(string UiToken, int EditorPid, EditorProjectDto? Project, string? Nonce);

/** Error replies: a curated message and a stable code. */
public sealed record EditorErrorDto(string Error, string Code);

/** One event on the stream: Type is an EditorUiEvents name, Seq the SSE id, Data the event body. */
public sealed record EditorEventDto(string Type, long Seq, object? Data);
