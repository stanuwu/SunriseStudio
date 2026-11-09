/**
 * File: What a model-workflow worker session may call (UI-UE5 §4.4.8, §5.4). A subscription worker (Codex or Claude Code run
 * by a workflow stage) connects through `solforge-mcp --worker --project <p>`, which marks its session as a worker
 * (McpSessionRegistry.WorkerHeader). Workers can inspect the project and use the explicit project-authoring actions below.
 * Actions follow the user's editor approval preference and remain audited. Workers cannot start recursive workflows,
 * drive arbitrary UI/commands, deploy builds or manage shared processes.
 */
namespace Solforge.Mcp;

/** The per-session allowlist McpHttpServer (and the headless shim) applies to worker sessions. */
public static class WorkerSessionPolicy
{
    /** Explicit project-authoring actions. Every action keeps its existing validation and approval gate. */
    public static readonly IReadOnlySet<string> AllowedWriteTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "apply_project_edit", "select_object", "focus_camera", "highlight", "open_file",
        "open_map", "close_map", "open_asset", "close_asset", "set_camera", "set_viewport_options", "set_viewport_rendering",
        "edit_placement", "edit_bounds", "revert_object_edit", "apply_item_edit", "apply_vendor_edit", "edit_perk_behavior", "create_perk",
        "create_content", "update_content", "delete_content", "create_graph", "edit_graph", "apply_graph",
        "create_mission_script", "insert_mission_lib_call", "reload_mission_scripts", "start_parhelion_weapon_recipe"
    };

    /** Reads that still change state or reach outside the editor; refused for workers. */
    public static readonly IReadOnlySet<string> BlockedReadTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "start_model_workflow", "get_model_workflow", "cancel_model_workflow",
        "automatic_start", "automatic_status", "automatic_stop", "goal_progress", "goal_complete", "goal_blocked",
        "retail_engine"
    };

    /** Null when a worker may call the tool, else why not. */
    public static string? Refusal(string tool, bool requiresApproval, bool readOnly = false)
    {
        if (SolforgeTools.WorkflowToolNames.Contains(tool))
            return $"{tool} is not available to workflow workers: a worker cannot start, read or cancel workflows.";
        if (BlockedReadTools.Contains(tool)) return $"{tool} is not available to workflow workers.";
        if (readOnly && (requiresApproval || tool == "export_data"))
            return $"{tool} is not available to planning/review workers: this stage has read-only access.";
        if (requiresApproval && !AllowedWriteTools.Contains(tool))
            return $"{tool} is not available to workflow workers. This worker has project-authoring tools only; shared-process, deployment and arbitrary UI/command actions stay with the parent conversation.";
        return null;
    }

    /** True when a worker may see and call the tool. */
    public static bool Allows(string tool, bool requiresApproval, bool readOnly = false) => Refusal(tool, requiresApproval, readOnly) is null;
}
