/**
 * File: MCP tools for automatic mode (AutomaticWatchdog.cs): automatic_start, automatic_status, automatic_stop for any
 * client, and goal_progress, goal_complete, goal_blocked for the engine working on the goal. None needs an approve click
 * and all work while context publishing is paused; they only steer the watchdog, never the project. No host in this
 * Solforge has a watchdog any more (the console it typed into is removed), so all six refuse with AutomaticUnavailable;
 * they stay registered so the tool list does not change.
 */
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/** Partial: the automatic-mode tools (automatic_start, goal_progress, goal_complete, goal_blocked). */
public static partial class SolforgeTools
{
    /**
     * Why the automatic_* and goal_* tools refuse: automatic nudges typed into the in-editor AI console, which is removed.
     * The editor's get_editor_state and Settings > AI use the same wording (StudioForm.Ai.Automatic.cs).
     */
    public const string AutomaticUnavailable =
        "Automatic nudges needed the removed in-editor console. Use model workflows (start_model_workflow) instead.";

    /** Shared by the server instructions and the session file: one sentence pointing long goals at model workflows. */
    public const string AutomaticHint =
        "For a long multi-step goal use start_model_workflow (the automatic_* and goal_* tools are kept for compatibility but refuse).";

    /** The automatic-mode tool names; they skip the approve click and the pause gate. */
    public static readonly string[] AutomaticTools =
        ["automatic_start", "automatic_status", "automatic_stop", "goal_progress", "goal_complete", "goal_blocked"];

    /** Yields the automatic-mode tools; each reports an error when this Solforge has no watchdog. */
    private static IEnumerable<McpTool> AutomaticModeTools(ISolforgeHost host)
    {
        McpTool Tool(string name, string description, JsonObject schema, Func<AutomaticWatchdog, JsonObject, McpToolResult> run) =>
            new(name, description, schema, false, (args, _) => Task.FromResult(host.Automatic is { } watchdog
                ? run(watchdog, args) : McpToolResult.Error(AutomaticUnavailable)));
        static JsonObject Schema(params (string, string, string, bool)[] properties) => McpJson.Schema(properties);
        static string Arg(JsonObject args, string name) => McpJson.String(args, name)?.Trim() ?? "";
        static McpToolResult Status(AutomaticWatchdog watchdog, string? note = null)
        {
            var json = watchdog.ToJson();
            if (note is not null) json["note"] = note;
            return McpToolResult.Json(json);
        }

        yield return Tool("automatic_start", "Automatic mode (idle nudges until goal_complete or goal_blocked). Not available in this " +
            "Solforge: it needed the removed in-editor console, so the call refuses. Use start_model_workflow for long multi-step goals. " +
            "Kept so the tool list stays the same.",
            Schema(("goal", "string", "What must be true when done, specific enough to verify", true),
                ("replace", "boolean", "Replace a goal that is already running (default false)", false),
                ("send_prompt", "boolean", "Also send the goal to the engine being nudged (ignored here: no engine is attached)", false)),
            (watchdog, args) =>
            {
                string goal = Arg(args, "goal");
                if (goal.Length == 0) return McpToolResult.Error("Give a goal.");
                if (watchdog.Start(goal, "mcp", McpJson.Bool(args, "send_prompt", false), McpJson.Bool(args, "replace", false)) is { } problem)
                    return McpToolResult.Error(problem);
                return Status(watchdog, AutomaticWatchdog.StartPrompt(goal));
            });
        yield return Tool("automatic_status", "Automatic mode now: state (off, running, complete, blocked, stopped, limit), goal, nudges " +
            "and the limit, elapsed seconds, the last nudge, reported progress and remaining steps, and how it ended. No approve click.",
            Schema(), (watchdog, _) => Status(watchdog));
        yield return Tool("automatic_stop", "End automatic mode now (no more nudges). No approve click.",
            Schema(("reason", "string", "Why (optional)", false)),
            (watchdog, args) => watchdog.Stop(Arg(args, "reason") is { Length: > 0 } reason ? reason : "Stopped over MCP.")
                ? Status(watchdog, "Stopped.") : Status(watchdog, "Automatic mode was not running."));
        yield return Tool("goal_progress", "Automatic mode: report a finished step and what is left; the next nudge quotes it back. " +
            "No approve click.",
            Schema(("done", "string", "What is done and verified so far", true), ("remaining", "string", "The steps still left", false)),
            (watchdog, args) => Arg(args, "done") is { Length: > 0 } done
                ? watchdog.Progress(done, Arg(args, "remaining")) ? Status(watchdog, "Noted.") : McpToolResult.Error("Automatic mode is not running.")
                : McpToolResult.Error("Say what is done."));
        yield return Tool("goal_complete", "Automatic mode: the goal is achieved AND verified. Ends the nudges. Give a summary and the " +
            "evidence (checks run, output, log lines, screenshots looked at). No approve click.",
            Schema(("summary", "string", "What was done", true), ("evidence", "string", "How you verified it", true)),
            (watchdog, args) =>
            {
                string summary = Arg(args, "summary"), evidence = Arg(args, "evidence");
                if (summary.Length == 0 || evidence.Length == 0) return McpToolResult.Error("Give both a summary and the evidence that it works.");
                return watchdog.Complete(summary, evidence) ? Status(watchdog, "Goal complete.") : McpToolResult.Error("Automatic mode is not running.");
            });
        yield return Tool("goal_blocked", "Automatic mode: you cannot go on without the user (a decision, login, hardware or access). Ends " +
            "the nudges and shows the reason in Solforge. No approve click.",
            Schema(("reason", "string", "What you need from the user", true)),
            (watchdog, args) => Arg(args, "reason") is { Length: > 0 } reason
                ? watchdog.Block(reason) ? Status(watchdog, "Blocked; the user will see the reason.") : McpToolResult.Error("Automatic mode is not running.")
                : McpToolResult.Error("Say what you need."));
    }
}
