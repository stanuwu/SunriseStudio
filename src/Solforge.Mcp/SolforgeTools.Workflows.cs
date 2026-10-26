/**
 * File: MCP start_model_workflow, get_model_workflow and cancel_model_workflow (UI-UE5 §5.4), the solforge-next model team
 * tools. The editor owns the workflows (Solforge.Harness.Runtime.HarnessWorkflowService behind ISolforgeHost.Workflows);
 * start always opens the editor's native confirm dialog showing the task and the routed model per role, and is never
 * auto-approved. Nothing here contacts a provider: the tools only hand the request to the editor.
 */
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/**
 * The editor's model team, as the workflow tools see it. Implementations run the native confirm themselves; every
 * member may be called on an MCP thread.
 */
public interface IModelWorkflows
{
    /**
     * Shows the editor's confirm dialog (task, SmallFix, routed models) and starts the workflow once the user confirms.
     * Returns { run_id, status } or an object with "error" (declined, another start pending, no project, settings invalid).
     */
    Task<JsonObject> StartAsync(string task, bool smallFix, CancellationToken cancel);
    /** The durable snapshot of one run (stages, selected and attempted models, verdicts, journal), or { error }. */
    JsonObject Get(string runId);
    /** Requests cancellation; false when the run is unknown or already finished. */
    bool Cancel(string runId);
    /** Recent run ids of the open project, newest first (live and persisted). */
    IReadOnlyList<string> RecentIds();
}

public static partial class SolforgeTools
{
    /** The workflow tool names (blocked for worker sessions, WorkerSessionPolicy). */
    public static readonly string[] WorkflowToolNames = ["start_model_workflow", "get_model_workflow", "cancel_model_workflow"];

    private const string NoWorkflows = "Model workflows need the Solforge editor with a project open (Settings > Models & workflows " +
        "configures the role ladders).";

    /** Yields the three workflow tools. */
    private static IEnumerable<McpTool> WorkflowTools(ISolforgeHost host)
    {
        // RequiresApproval: listed as needing a click. The click is the editor's own confirm dialog, never ApproveAsync,
        // so the auto-approve setting cannot start a workflow.
        yield return new McpTool("start_model_workflow", "Start the user's configured model team: Planner -> Executor -> parallel " +
            "reviewers -> Reviewer (one targeted SmallFix and re-review when the review has findings), or SmallFix -> Reviewer with " +
            "small_fix. Uses ONLY the ordered models configured per role in Solforge (Settings > Models & workflows) and may consume " +
            "the user's subscription or API quota. Solforge always asks the user to confirm (task and routed models); returns a run_id, " +
            "not a result: read get_model_workflow for real progress. Edits still need the user's before/after review; nothing is saved " +
            "automatically. Read the solforge-model-workflow skill first.",
            McpJson.Schema(("task", "string", "Self-contained task with concrete acceptance criteria (1..32768 characters)", true),
                ("small_fix", "boolean", "One bounded fix: SmallFix -> Reviewer, no planner (default false)", false)),
            true, async (args, cancel) =>
            {
                if (host.Workflows is not { } workflows) return McpToolResult.Error(NoWorkflows);
                string task = McpJson.String(args, "task") ?? "";
                if (string.IsNullOrWhiteSpace(task) || task.Length > 32768) return McpToolResult.Error("task must contain 1..32768 characters.");
                var reply = await workflows.StartAsync(task, McpJson.Bool(args, "small_fix", false), cancel).ConfigureAwait(false);
                return reply["error"] is JsonValue error ? McpToolResult.Error(error.ToString()) : McpToolResult.Json(reply);
            });
        yield return new McpTool("get_model_workflow", "A model workflow's durable state: status, each stage (role, selected model, " +
            "attempted models, verdict, output), the journal and quota pauses. Paused or failed runs keep the task and completed stages. " +
            "Without run_id: the recent runs of this project.",
            McpJson.Schema(("run_id", "string", "Run id from start_model_workflow (default: list recent runs)", false)),
            false, (args, _) =>
            {
                if (host.Workflows is not { } workflows) return Task.FromResult(McpToolResult.Error(NoWorkflows));
                if (McpJson.String(args, "run_id") is not { Length: > 0 } id)
                    return Task.FromResult(McpToolResult.Json(new JsonObject
                    {
                        ["recent"] = new JsonArray([.. workflows.RecentIds().Select(run => (JsonNode)run)])
                    }));
                var state = workflows.Get(id);
                return Task.FromResult(state["error"] is JsonValue error ? McpToolResult.Error(error.ToString()) : McpToolResult.Json(state));
            });
        yield return new McpTool("cancel_model_workflow", "Cancel a model workflow of this project; its task and completed stage " +
            "outputs are kept.",
            McpJson.Schema(("run_id", "string", "Run id from start_model_workflow", true)),
            false, (args, _) =>
            {
                if (host.Workflows is not { } workflows) return Task.FromResult(McpToolResult.Error(NoWorkflows));
                string id = McpJson.String(args, "run_id") ?? "";
                return Task.FromResult(McpToolResult.Json(new JsonObject { ["run_id"] = id, ["cancelled"] = workflows.Cancel(id) }));
            });
    }
}
