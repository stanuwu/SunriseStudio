/**
 * File: What a model-workflow worker session may call (UI-UE5 §4.4.8, §5.4). A subscription worker (Codex or Claude Code run
 * by a workflow stage) connects through `solforge-mcp --worker --project <p>`, which marks its session as a worker
 * (McpSessionRegistry.WorkerHeader). Workers read freely, may propose project file edits (apply_project_edit, which always
 * goes through the reviewed before/after diff), and nothing else: no workflow tools (a worker must never start another
 * root workflow), no other write or UI tools.
 */
namespace Solforge.Mcp;

/** The per-session allowlist McpHttpServer (and the headless shim) applies to worker sessions. */
public static class WorkerSessionPolicy
{
    /** The only approval tools a worker may call: each one is a reviewed edit the user approves in Solforge. */
    public static readonly IReadOnlySet<string> AllowedWriteTools = new HashSet<string>(StringComparer.Ordinal) { "apply_project_edit" };

    /** Reads that still change state or reach outside the editor; refused for workers. */
    public static readonly IReadOnlySet<string> BlockedReadTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "start_model_workflow", "get_model_workflow", "cancel_model_workflow",
        "automatic_start", "automatic_status", "automatic_stop", "goal_progress", "goal_complete", "goal_blocked",
        "export_data", "retail_engine", "read_screen_text", "get_viewport_stream"
    };

    /** Null when a worker may call the tool, else why not. */
    public static string? Refusal(string tool, bool requiresApproval)
    {
        if (SolforgeTools.WorkflowToolNames.Contains(tool))
            return $"{tool} is not available to workflow workers: a worker cannot start, read or cancel workflows.";
        if (BlockedReadTools.Contains(tool)) return $"{tool} is not available to workflow workers.";
        if (requiresApproval && !AllowedWriteTools.Contains(tool))
            return $"{tool} is not available to workflow workers: propose file changes with apply_project_edit (the user reviews them).";
        return null;
    }

    /** True when a worker may see and call the tool. */
    public static bool Allows(string tool, bool requiresApproval) => Refusal(tool, requiresApproval) is null;
}
